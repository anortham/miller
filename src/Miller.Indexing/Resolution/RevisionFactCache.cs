using Microsoft.Data.Sqlite;
using Miller.Core.Resolution;
using Miller.Indexing.Reads;

namespace Miller.Indexing.Resolution;

internal readonly record struct PackedTypeFact(string SymbolId, string ResolvedType, bool IsInferred);

internal readonly struct PackedSymbol
{
    internal PackedSymbol(
        string symbolId,
        string name,
        FactSymbolKind kind,
        string language,
        string? parentId,
        string? signature,
        string? visibility,
        byte staticCode,
        string? metadataJson)
    {
        SymbolId = symbolId;
        Name = name;
        Kind = kind;
        Language = language;
        ParentId = parentId;
        Signature = signature;
        Visibility = visibility;
        StaticCode = staticCode;
        MetadataJson = metadataJson;
    }

    internal string SymbolId { get; }

    internal string Name { get; }

    internal FactSymbolKind Kind { get; }

    internal string Language { get; }

    internal string? ParentId { get; }

    internal string? Signature { get; }

    internal string? Visibility { get; }

    internal byte StaticCode { get; }

    internal string? MetadataJson { get; }

    internal bool? IsStatic => StaticCode switch
    {
        1 => true,
        2 => false,
        _ => null,
    };

    internal FactSymbol ToFact(long versionId)
    {
        FactSymbolKey key = new(versionId, SymbolId);
        FactSymbolKey? parent = ParentId is null ? null : new FactSymbolKey(versionId, ParentId);
        return new FactSymbol(key, Name, Kind, Language, parent, Signature, Visibility, IsStatic);
    }
}

internal sealed class VersionSlice
{
    internal VersionSlice(
        long versionId,
        string path,
        string language,
        PackedSymbol[] symbols,
        PackedTypeFact[] typeFacts,
        long[] locatedRowIds,
        PropagationSource[] locatedSources,
        RevisionFactCacheLoader.ImportSeed[] importSeeds)
    {
        VersionId = versionId;
        Path = path;
        Language = language;
        Packed = symbols;
        TypeFactRows = typeFacts;
        LocatedRowIds = locatedRowIds;
        LocatedSources = locatedSources;
        ImportSeeds = importSeeds;
        Imports = [];
    }

    internal long VersionId { get; }

    internal string Path { get; }

    internal string Language { get; }

    internal PackedSymbol[] Packed { get; set; }

    internal PackedTypeFact[] TypeFactRows { get; set; }

    internal long[] LocatedRowIds { get; set; }

    internal PropagationSource[] LocatedSources { get; set; }

    internal RevisionFactCacheLoader.ImportSeed[] ImportSeeds { get; set; }

    internal ImportBinding[] Imports { get; set; }

    internal int LocatedCount => LocatedRowIds.Length;

    internal FactSymbol[] Symbols
    {
        get
        {
            if (_materialized is not null)
                return _materialized;
            var facts = new FactSymbol[Packed.Length];
            for (int i = 0; i < Packed.Length; i++)
                facts[i] = Packed[i].ToFact(VersionId);
            _materialized = facts;
            return facts;
        }
    }

    private FactSymbol[]? _materialized;

    internal bool TryGetLocated(long rowId, out PropagationSource source)
    {
        int index = Array.BinarySearch(LocatedRowIds, rowId);
        if (index < 0)
        {
            source = default;
            return false;
        }

        source = LocatedSources[index];
        return true;
    }
}

internal sealed class RevisionFactCache : IResolutionFacts
{
    private readonly record struct PackedRef(long VersionId, int Index);

    private readonly StringInternPool _intern;
    private readonly Dictionary<long, VersionSlice> _slices;
    private readonly Dictionary<string, RevisionFactCacheLoader.VisibleFile> _pathIndex;
    private readonly StoreVisibility? _visibility;
    private readonly BoundedStoreSource? _bounded;
    private readonly QmlVisibilityCatalog _qml;
    private readonly object _boundedGate = new();
    private int _boundedSliceMisses;
    private int _boundedPointSliceLoads;
    private Dictionary<string, List<PackedRef>> _byName = new(StringComparer.Ordinal);
    private Dictionary<long, ImportBinding[]> _imports = [];

    private RevisionFactCache(
        StringInternPool intern,
        Dictionary<long, VersionSlice> slices,
        Dictionary<string, RevisionFactCacheLoader.VisibleFile> pathIndex,
        StoreVisibility? visibility,
        QmlVisibilityCatalog qml)
    {
        _intern = intern;
        _slices = slices;
        _pathIndex = pathIndex;
        _visibility = visibility;
        _bounded = null;
        _qml = qml;
        RebuildIndexes();
        Propagation = new PropagationIndex(_slices);
        ResidentBytes = EstimateBytes();
    }

    private RevisionFactCache(
        StringInternPool intern,
        Dictionary<string, RevisionFactCacheLoader.VisibleFile> pathIndex,
        StoreVisibility visibility,
        BoundedStoreSource bounded,
        QmlVisibilityCatalog qml)
    {
        _intern = intern;
        _slices = [];
        _pathIndex = pathIndex;
        _visibility = visibility;
        _bounded = bounded;
        _qml = qml;
        Propagation = new PropagationIndex(_slices, SliceFor);
        ResidentBytes = 0;
    }

    internal PropagationIndex Propagation { get; private set; }

    internal long ResidentBytes { get; private set; }

    internal int BoundedSliceMisses
    {
        get
        {
            lock (_boundedGate)
                return _boundedSliceMisses;
        }
    }

    internal int BoundedPointSliceLoads
    {
        get
        {
            lock (_boundedGate)
                return _boundedPointSliceLoads;
        }
    }

    /// <summary>Files this cache has materialized. A full load has one per visible file from the start.</summary>
    internal int LoadedSliceCount
    {
        get
        {
            if (_bounded is null)
                return _slices.Count;

            lock (_boundedGate)
                return _slices.Count;
        }
    }

    // A bounded cache reads through a session-owned connection and holds only what one query asked for, so it
    // can neither be shared by a later revision nor advanced onto one.
    internal bool CanAdvance => _visibility is not null && _bounded is null;

    internal static RevisionFactCache Load(SqliteConnection storeRead, StoreVisibility visibility)
    {
        ArgumentNullException.ThrowIfNull(storeRead);
        ArgumentNullException.ThrowIfNull(visibility);
        var intern = new StringInternPool();
        List<RevisionFactCacheLoader.VisibleFile> files = RevisionFactCacheLoader.ReadVisibleStore(storeRead, visibility);
        var pathIndex = new Dictionary<string, RevisionFactCacheLoader.VisibleFile>(files.Count, StringComparer.Ordinal);
        var internedFiles = new List<RevisionFactCacheLoader.VisibleFile>(files.Count);
        foreach (RevisionFactCacheLoader.VisibleFile file in files)
        {
            RevisionFactCacheLoader.VisibleFile interned = file with
            {
                Path = intern.Intern(file.Path),
                Language = intern.Intern(file.Language),
            };
            pathIndex[interned.Path] = interned;
            internedFiles.Add(interned);
        }

        Dictionary<long, VersionSlice> slices = RevisionFactCacheLoader.LoadAllStoreSlices(
            storeRead,
            visibility,
            internedFiles,
            intern);

        BindAllImports(slices, pathIndex);
        DropImportSeeds(slices);
        QmlVisibilityCatalog qml = QmlVisibilityCatalog.LoadStore(storeRead, visibility, internedFiles, slices, intern);
        return new RevisionFactCache(intern, slices, pathIndex, visibility, qml);
    }

    internal static RevisionFactCache LoadFromArtifact(SqliteConnection artifactRead)
    {
        ArgumentNullException.ThrowIfNull(artifactRead);
        var intern = new StringInternPool();
        List<RevisionFactCacheLoader.VisibleFile> files = RevisionFactCacheLoader.ReadVisibleArtifact(artifactRead);
        var pathIndex = new Dictionary<string, RevisionFactCacheLoader.VisibleFile>(files.Count, StringComparer.Ordinal);
        var internedFiles = new List<RevisionFactCacheLoader.VisibleFile>(files.Count);
        foreach (RevisionFactCacheLoader.VisibleFile file in files)
        {
            RevisionFactCacheLoader.VisibleFile interned = file with
            {
                Path = intern.Intern(file.Path),
                Language = intern.Intern(file.Language),
            };
            pathIndex[interned.Path] = interned;
            internedFiles.Add(interned);
        }

        Dictionary<long, VersionSlice> slices = RevisionFactCacheLoader.LoadAllArtifactSlices(
            artifactRead,
            internedFiles,
            intern);

        BindAllImports(slices, pathIndex);
        DropImportSeeds(slices);
        QmlVisibilityCatalog qml = QmlVisibilityCatalog.LoadArtifact(artifactRead, internedFiles, slices, intern);
        return new RevisionFactCache(intern, slices, pathIndex, visibility: null, qml);
    }

    /// <summary>
    /// A fact view over one pinned generation that reads a file's facts, and a name's symbols, only when a
    /// query asks for them. Every answer comes from the same loader queries the whole-generation
    /// <see cref="Load"/> uses, so a bounded cache and a full one answer every accessor identically; the
    /// bounded one just never reads the files no answer depends on.
    /// <para>The caller owns <paramref name="storeRead"/> and must keep it open for the cache's life, and must
    /// give the cache that connection to ITSELF: the cache issues SQL on it from any accessor, under its own
    /// gate, so a connection shared with the caller's other reads would be used off that gate. Every bounded
    /// accessor takes the gate, so the returned cache is safe to share between threads. Use it for a one-shot
    /// process (the CLI), never for the shared server cache — that one is reused across queries, so paying the
    /// whole generation once is the cheaper trade.</para>
    /// </summary>
    internal static RevisionFactCache LoadBounded(SqliteConnection storeRead, StoreVisibility visibility)
    {
        ArgumentNullException.ThrowIfNull(storeRead);
        ArgumentNullException.ThrowIfNull(visibility);
        var intern = new StringInternPool();
        List<RevisionFactCacheLoader.VisibleFile> files = RevisionFactCacheLoader.ReadVisibleStore(storeRead, visibility);
        var pathIndex = new Dictionary<string, RevisionFactCacheLoader.VisibleFile>(files.Count, StringComparer.Ordinal);
        var byVersion = new Dictionary<long, RevisionFactCacheLoader.VisibleFile>(files.Count);
        foreach (RevisionFactCacheLoader.VisibleFile file in files)
        {
            RevisionFactCacheLoader.VisibleFile interned = file with
            {
                Path = intern.Intern(file.Path),
                Language = intern.Intern(file.Language),
            };
            pathIndex[interned.Path] = interned;

            // Path order with last-write-wins, exactly as LoadAllStoreSlices keys its slices: two manifest
            // paths can share one version_id, and the full load keeps the last path for that version.
            byVersion[interned.VersionId] = interned;
        }

        QmlVisibilityCatalog qml = QmlVisibilityCatalog.LoadBoundedStore(storeRead, visibility, files, intern);
        return new RevisionFactCache(
            intern,
            pathIndex,
            visibility,
            new BoundedStoreSource(storeRead, visibility, byVersion),
            qml);
    }

    internal RevisionFactCache Advance(SqliteConnection storeRead, StoreVisibility newVisibility)
    {
        ArgumentNullException.ThrowIfNull(storeRead);
        ArgumentNullException.ThrowIfNull(newVisibility);
        if (_bounded is not null)
            throw new InvalidOperationException("Bounded fact caches cannot Advance.");
        if (_visibility is null)
            throw new InvalidOperationException("Artifact fact caches cannot Advance.");

        List<RevisionFactCacheLoader.VisibleFile> files = RevisionFactCacheLoader.ReadVisibleStore(storeRead, newVisibility);
        var pathIndex = new Dictionary<string, RevisionFactCacheLoader.VisibleFile>(files.Count, StringComparer.Ordinal);
        var nextSlices = new Dictionary<long, VersionSlice>(files.Count);
        foreach (RevisionFactCacheLoader.VisibleFile file in files)
        {
            RevisionFactCacheLoader.VisibleFile interned = file with
            {
                Path = _intern.Intern(file.Path),
                Language = _intern.Intern(file.Language),
            };
            pathIndex[interned.Path] = interned;
            if (_slices.TryGetValue(interned.VersionId, out VersionSlice? existing)
                && existing.VersionId == interned.VersionId
                && string.Equals(existing.Path, interned.Path, StringComparison.Ordinal))
            {
                nextSlices[interned.VersionId] = existing;
            }
            else
            {
                nextSlices[interned.VersionId] = RevisionFactCacheLoader.LoadStoreSlice(
                    storeRead,
                    newVisibility,
                    interned,
                    _intern);
            }
        }

        if (MembershipChanged(_pathIndex, pathIndex))
        {
            BindAllImports(nextSlices, pathIndex);
            DropImportSeeds(nextSlices);
        }
        else
        {
            foreach (VersionSlice slice in nextSlices.Values)
            {
                if (slice.Imports.Length == 0 && slice.ImportSeeds.Length > 0)
                {
                    slice.Imports = RevisionFactCacheLoader.BindImports(slice, pathIndex);
                    slice.ImportSeeds = [];
                }
            }
        }

        QmlVisibilityCatalog qml = QmlVisibilityCatalog.LoadStore(
            storeRead,
            newVisibility,
            files,
            nextSlices,
            _intern);
        return new RevisionFactCache(_intern, nextSlices, pathIndex, newVisibility, qml);
    }

    public IEnumerable<FactSymbol> SymbolsNamed(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (_bounded is { } bounded)
        {
            lock (_boundedGate)
                return bounded.SymbolsNamed(name, _intern);
        }

        if (!_byName.TryGetValue(name, out List<PackedRef>? refs))
            return [];
        var matches = new FactSymbol[refs.Count];
        for (int i = 0; i < refs.Count; i++)
            matches[i] = Materialize(refs[i]);
        return matches;
    }

    public FactSymbol? Symbol(FactSymbolKey key)
    {
        if (SliceFor(key.VersionId) is not { } slice)
            return null;
        foreach (PackedSymbol packed in slice.Packed)
        {
            if (string.Equals(packed.SymbolId, key.SymbolId, StringComparison.Ordinal))
                return packed.ToFact(key.VersionId);
        }

        return null;
    }

    public IReadOnlyList<FactSymbol> ChildrenOf(FactSymbolKey parent)
    {
        if (SliceFor(parent.VersionId) is not { } slice)
            return [];
        var matches = new List<FactSymbol>();
        for (int i = 0; i < slice.Packed.Length; i++)
        {
            PackedSymbol packed = slice.Packed[i];
            if (string.Equals(packed.ParentId, parent.SymbolId, StringComparison.Ordinal))
                matches.Add(packed.ToFact(parent.VersionId));
        }

        return matches.Count == 0 ? [] : matches.ToArray();
    }

    public IReadOnlyList<FactSymbol> TopLevelOf(long versionId)
    {
        if (SliceFor(versionId) is not { } slice)
            return [];
        var top = new List<FactSymbol>();
        foreach (PackedSymbol packed in slice.Packed)
        {
            if (packed.ParentId is null)
                top.Add(packed.ToFact(versionId));
        }

        return top.Count == 0 ? [] : top.ToArray();
    }

    public IReadOnlyList<FactTypeFact> TypeFactsOf(FactSymbolKey symbol)
    {
        if (SliceFor(symbol.VersionId) is not { } slice)
            return [];
        var matches = new List<FactTypeFact>();
        foreach (PackedTypeFact fact in slice.TypeFactRows)
        {
            if (string.Equals(fact.SymbolId, symbol.SymbolId, StringComparison.Ordinal))
                matches.Add(new FactTypeFact(fact.ResolvedType, fact.IsInferred));
        }

        return matches.Count == 0 ? [] : matches.ToArray();
    }

    public IReadOnlyList<ImportBinding> ImportsOf(long versionId) => ImportArrayOf(versionId);

    public IReadOnlyList<QmlVisibleType> QmlTypesVisibleTo(long versionId)
    {
        if (_bounded is null)
            return _qml.For(versionId);

        lock (_boundedGate)
        {
            _ = EnsureSlice(versionId);
            return _qml.For(versionId);
        }
    }

    internal FactSymbol[] SymbolsOfVersion(long versionId) =>
        SliceFor(versionId) is { } slice ? slice.Symbols : [];

    internal ImportBinding[] ImportArrayOf(long versionId)
    {
        if (_bounded is null)
            return _imports.TryGetValue(versionId, out ImportBinding[]? eager) ? eager : [];

        lock (_boundedGate)
        {
            _ = EnsureSlice(versionId);
            return _imports.TryGetValue(versionId, out ImportBinding[]? imports) ? imports : [];
        }
    }

    internal VersionSlice? Slice(long versionId) => SliceFor(versionId);

    // A full load never mutates after its constructor, so its readers need no lock. A bounded one fills as it
    // is queried — _slices, _imports and the name cache all grow on an ACCESSOR — and the reader it lives in is
    // handed out (IQueryTimeResolutionHost.Resolution, WorkspaceReadHandle.ResolutionReader) with no promise
    // that one thread holds it. Every bounded read therefore takes this gate, which also serializes the
    // cache-owned connection the read-through uses.
    private VersionSlice? SliceFor(long versionId)
    {
        if (_bounded is null)
            return _slices.TryGetValue(versionId, out VersionSlice? slice) ? slice : null;

        lock (_boundedGate)
            return EnsureSlice(versionId);
    }

    private VersionSlice? EnsureSlice(long versionId)
    {
        if (_slices.TryGetValue(versionId, out VersionSlice? existing))
            return existing;
        if (_bounded is not { } bounded
            || !bounded.TryGetVisibleFile(versionId, out RevisionFactCacheLoader.VisibleFile file))
        {
            return null;
        }

        VersionSlice slice = RevisionFactCacheLoader.LoadStoreSlice(
            bounded.Connection,
            bounded.Visibility,
            file,
            _intern,
            indexedLocate: true);
        _boundedSliceMisses++;
        _boundedPointSliceLoads++;
        slice.Imports = RevisionFactCacheLoader.BindImports(slice, _pathIndex);
        slice.ImportSeeds = [];
        _slices[versionId] = slice;
        _imports[versionId] = slice.Imports;
        return slice;
    }

    private void RebuildIndexes()
    {
        _byName = new Dictionary<string, List<PackedRef>>(StringComparer.Ordinal);
        _imports = new Dictionary<long, ImportBinding[]>(_slices.Count);

        foreach (VersionSlice slice in _slices.Values.OrderBy(static s => s.VersionId))
        {
            for (int i = 0; i < slice.Packed.Length; i++)
            {
                PackedSymbol packed = slice.Packed[i];
                var pref = new PackedRef(slice.VersionId, i);
                if (!_byName.TryGetValue(packed.Name, out List<PackedRef>? named))
                {
                    named = [];
                    _byName[packed.Name] = named;
                }

                named.Add(pref);
            }

            _imports[slice.VersionId] = slice.Imports;
        }
    }

    private FactSymbol Materialize(PackedRef pref) =>
        _slices[pref.VersionId].Packed[pref.Index].ToFact(pref.VersionId);

    private long EstimateBytes()
    {
        long bytes = _intern.CharBytes;
        int symbolCount = 0;
        foreach (VersionSlice slice in _slices.Values)
        {
            symbolCount += slice.Packed.Length;
            bytes += slice.LocatedCount * 24L;
        }

        bytes += symbolCount * 96L;
        foreach (ImportBinding[] imports in _imports.Values)
            bytes += imports.Length * 96L;
        bytes += _pathIndex.Count * 48L;
        bytes += _byName.Count * 32L;
        return bytes;
    }

    private static void BindAllImports(
        Dictionary<long, VersionSlice> slices,
        Dictionary<string, RevisionFactCacheLoader.VisibleFile> pathIndex)
    {
        foreach (VersionSlice slice in slices.Values)
            slice.Imports = RevisionFactCacheLoader.BindImports(slice, pathIndex);
    }

    private static void DropImportSeeds(Dictionary<long, VersionSlice> slices)
    {
        foreach (VersionSlice slice in slices.Values)
            slice.ImportSeeds = [];
    }

    /// <summary>The read-through state a bounded cache adds: the visible files, the connection, and one
    /// materialized symbol list per name already asked for.</summary>
    private sealed class BoundedStoreSource
    {
        private readonly Dictionary<long, RevisionFactCacheLoader.VisibleFile> _byVersion;
        private readonly Dictionary<string, FactSymbol[]> _named = new(StringComparer.Ordinal);

        internal BoundedStoreSource(
            SqliteConnection connection,
            StoreVisibility visibility,
            Dictionary<long, RevisionFactCacheLoader.VisibleFile> byVersion)
        {
            Connection = connection;
            Visibility = visibility;
            _byVersion = byVersion;
        }

        internal SqliteConnection Connection { get; }

        internal StoreVisibility Visibility { get; }

        internal bool TryGetVisibleFile(long versionId, out RevisionFactCacheLoader.VisibleFile file) =>
            _byVersion.TryGetValue(versionId, out file);

        // The full load builds a fresh array per call, so a caller that sorts or writes into the result in place
        // cannot disturb a later call. Handing out the cached instance would give the two modes different
        // aliasing, so the cached list is copied on the way out.
        internal FactSymbol[] SymbolsNamed(string name, StringInternPool intern)
        {
            if (_named.TryGetValue(name, out FactSymbol[]? cached))
                return [.. cached];

            List<(long VersionId, PackedSymbol Symbol)> rows =
                RevisionFactCacheLoader.ReadStoreSymbolsNamed(Connection, Visibility, name, intern);
            var facts = new FactSymbol[rows.Count];
            for (int i = 0; i < rows.Count; i++)
                facts[i] = rows[i].Symbol.ToFact(rows[i].VersionId);
            _named[name] = facts;
            return [.. facts];
        }
    }

    private static bool MembershipChanged(
        Dictionary<string, RevisionFactCacheLoader.VisibleFile> previous,
        Dictionary<string, RevisionFactCacheLoader.VisibleFile> next)
    {
        if (previous.Count != next.Count)
            return true;
        foreach ((string path, RevisionFactCacheLoader.VisibleFile file) in next)
        {
            if (!previous.TryGetValue(path, out RevisionFactCacheLoader.VisibleFile prior)
                || prior.VersionId != file.VersionId)
            {
                return true;
            }
        }

        return false;
    }
}
