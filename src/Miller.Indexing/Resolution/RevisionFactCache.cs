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
        byte staticCode)
    {
        SymbolId = symbolId;
        Name = name;
        Kind = kind;
        Language = language;
        ParentId = parentId;
        Signature = signature;
        Visibility = visibility;
        StaticCode = staticCode;
    }

    internal string SymbolId { get; }

    internal string Name { get; }

    internal FactSymbolKind Kind { get; }

    internal string Language { get; }

    internal string? ParentId { get; }

    internal string? Signature { get; }

    internal string? Visibility { get; }

    internal byte StaticCode { get; }

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
    private Dictionary<string, List<PackedRef>> _byName = new(StringComparer.Ordinal);
    private Dictionary<long, ImportBinding[]> _imports = [];

    private RevisionFactCache(
        StringInternPool intern,
        Dictionary<long, VersionSlice> slices,
        Dictionary<string, RevisionFactCacheLoader.VisibleFile> pathIndex,
        StoreVisibility? visibility)
    {
        _intern = intern;
        _slices = slices;
        _pathIndex = pathIndex;
        _visibility = visibility;
        RebuildIndexes();
        Propagation = new PropagationIndex(_slices);
        ResidentBytes = EstimateBytes();
    }

    internal PropagationIndex Propagation { get; private set; }

    internal long ResidentBytes { get; private set; }

    internal bool CanAdvance => _visibility is not null;

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
        return new RevisionFactCache(intern, slices, pathIndex, visibility);
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
        return new RevisionFactCache(intern, slices, pathIndex, visibility: null);
    }

    internal RevisionFactCache Advance(SqliteConnection storeRead, StoreVisibility newVisibility)
    {
        ArgumentNullException.ThrowIfNull(storeRead);
        ArgumentNullException.ThrowIfNull(newVisibility);
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

        return new RevisionFactCache(_intern, nextSlices, pathIndex, newVisibility);
    }

    public IEnumerable<FactSymbol> SymbolsNamed(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!_byName.TryGetValue(name, out List<PackedRef>? refs))
            return [];
        var matches = new FactSymbol[refs.Count];
        for (int i = 0; i < refs.Count; i++)
            matches[i] = Materialize(refs[i]);
        return matches;
    }

    public FactSymbol? Symbol(FactSymbolKey key)
    {
        if (!_slices.TryGetValue(key.VersionId, out VersionSlice? slice))
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
        if (!_slices.TryGetValue(parent.VersionId, out VersionSlice? slice))
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
        if (!_slices.TryGetValue(versionId, out VersionSlice? slice))
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
        if (!_slices.TryGetValue(symbol.VersionId, out VersionSlice? slice))
            return [];
        var matches = new List<FactTypeFact>();
        foreach (PackedTypeFact fact in slice.TypeFactRows)
        {
            if (string.Equals(fact.SymbolId, symbol.SymbolId, StringComparison.Ordinal))
                matches.Add(new FactTypeFact(fact.ResolvedType, fact.IsInferred));
        }

        return matches.Count == 0 ? [] : matches.ToArray();
    }

    public IReadOnlyList<ImportBinding> ImportsOf(long versionId) =>
        _imports.TryGetValue(versionId, out ImportBinding[]? imports) ? imports : [];

    internal FactSymbol[] SymbolsOfVersion(long versionId) =>
        _slices.TryGetValue(versionId, out VersionSlice? slice) ? slice.Symbols : [];

    internal ImportBinding[] ImportArrayOf(long versionId) =>
        _imports.TryGetValue(versionId, out ImportBinding[]? imports) ? imports : [];

    internal VersionSlice? Slice(long versionId) =>
        _slices.TryGetValue(versionId, out VersionSlice? slice) ? slice : null;

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
