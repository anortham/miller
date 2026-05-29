using Miller.Core.Search;

namespace Miller.Indexing;

/// <summary>
/// The M1 in-process deliverable: a thin facade tying the SQLite read layer to Miller.Core's ranked index.
/// Builds <see cref="MillerSearchIndex"/> from the read symbols' scoring projections and retains the
/// <see cref="IndexedSymbol"/>s for hydration. This is where the opaque-string-id ⇄ int-DocId bridge lives:
/// a <see cref="SearchHit"/>'s <see cref="SearchableDocument.DocId"/> resolves back through
/// <see cref="Resolve"/> to the julie symbol id (the M4 join key) and its containment parent.
/// </summary>
public sealed class MillerRepositoryIndex
{
    private readonly MillerSearchIndex _index;
    private readonly IndexedSymbol[] _byDocId; // DocId == array index by construction (see Build)

    // M2 lookup maps for the read tools (search/inspect) + smart-string resolution. Built once at Build()
    // time so name/id/path/parent lookups are O(1)/O(k) rather than a linear scan of ~565k rows per call.
    private readonly Dictionary<string, IndexedSymbol> _bySymbolId;
    private readonly Dictionary<string, List<IndexedSymbol>> _byName;
    private readonly Dictionary<string, List<IndexedSymbol>> _byFilePath;
    private readonly Dictionary<string, List<IndexedSymbol>> _byParentId;

    // Cross-language file-detection support for SmartTargetResolver (decision-4): the "is this a file?"
    // decision is DERIVED from the indexed data, never a hardcoded extension whitelist. _byFileName maps a
    // bare basename to the distinct full indexed paths carrying it (so `UserService.cs` can resolve to
    // `auth/UserService.cs` when unique). _knownExtensions is the distinct set of extensions julie actually
    // emitted for THIS repo — precisely the supported languages, self-updating as julie adds more.
    private readonly Dictionary<string, List<string>> _byFileName;

    private MillerRepositoryIndex(
        MillerSearchIndex index,
        IndexedSymbol[] byDocId,
        Dictionary<string, IndexedSymbol> bySymbolId,
        Dictionary<string, List<IndexedSymbol>> byName,
        Dictionary<string, List<IndexedSymbol>> byFilePath,
        Dictionary<string, List<IndexedSymbol>> byParentId,
        Dictionary<string, List<string>> byFileName,
        IReadOnlySet<string> knownExtensions)
    {
        _index = index;
        _byDocId = byDocId;
        _bySymbolId = bySymbolId;
        _byName = byName;
        _byFilePath = byFilePath;
        _byParentId = byParentId;
        _byFileName = byFileName;
        KnownExtensions = knownExtensions;
    }

    /// <summary>Total number of indexed symbols.</summary>
    public int DocumentCount => _byDocId.Length;

    /// <summary>
    /// The distinct file extensions (lowercased, leading dot, e.g. <c>.cs</c>/<c>.rs</c>/<c>.vue</c>) present
    /// among the indexed file paths. DERIVED from julie's output — the cross-language replacement for a
    /// hardcoded code-extension whitelist (M2 §3 decision-4). Used by <see cref="SmartTargetResolver"/> to
    /// classify a non-indexed but plausibly-file target.
    /// </summary>
    public IReadOnlySet<string> KnownExtensions { get; }

    /// <summary>
    /// Build the repository index from symbols produced by <see cref="SqliteSymbolReader.Read"/>. The reader
    /// assigns contiguous 0-based DocIds in deterministic order, so the hydration array is indexed directly by
    /// DocId for O(1) <see cref="Resolve"/>. This contract is validated, not assumed.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// DocIds are not the contiguous 0..n-1 ordinals this facade requires (a reader regression).
    /// </exception>
    public static MillerRepositoryIndex Build(IReadOnlyList<IndexedSymbol> symbols)
    {
        ArgumentNullException.ThrowIfNull(symbols);

        var byDocId = new IndexedSymbol[symbols.Count];
        var documents = new SearchableDocument[symbols.Count];
        // Name/file-path/parent lookups are intrinsically multi-valued; symbol-id is unique (PK).
        var bySymbolId = new Dictionary<string, IndexedSymbol>(symbols.Count, StringComparer.Ordinal);
        var byName = new Dictionary<string, List<IndexedSymbol>>(StringComparer.Ordinal);
        var byFilePath = new Dictionary<string, List<IndexedSymbol>>(StringComparer.Ordinal);
        var byParentId = new Dictionary<string, List<IndexedSymbol>>(StringComparer.Ordinal);

        for (int i = 0; i < symbols.Count; i++)
        {
            var symbol = symbols[i];
            // Pin the read-layer contract: DocId is the 0-based row ordinal. A drift here would silently
            // misroute Resolve(), so reject it loudly rather than corrupt the id bridge.
            if (symbol.DocId != i)
                throw new ArgumentException(
                    $"Symbol at position {i} has DocId {symbol.DocId}; MillerRepositoryIndex requires the " +
                    "contiguous 0-based ordinals SqliteSymbolReader assigns.", nameof(symbols));

            byDocId[i] = symbol;
            documents[i] = symbol.ToSearchableDocument();

            // symbol id is julie's PK — unique. A duplicate would mean a corrupt extract; last-wins is
            // harmless here (the reader already enforces uniqueness via the PK), so just index it.
            bySymbolId[symbol.SymbolId] = symbol;
            Add(byName, symbol.Name, symbol);
            Add(byFilePath, symbol.FilePath, symbol);
            if (symbol.ParentId is { } pid)
                Add(byParentId, pid, symbol);
        }

        // Derive the cross-language file-detection structures from the indexed paths (decision-4): basename
        // → distinct full paths, and the distinct extension set. Built from the path keys, not per-symbol, so
        // each path contributes once regardless of how many symbols it holds.
        var byFileName = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var knownExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in byFilePath.Keys)
        {
            string fileName = LastPathSegment(path);
            if (!byFileName.TryGetValue(fileName, out var paths))
                byFileName[fileName] = paths = new List<string>(1);
            paths.Add(path);

            string ext = Path.GetExtension(fileName);
            if (ext.Length > 1) // non-empty beyond the dot
                knownExtensions.Add(ext);
        }

        return new MillerRepositoryIndex(
            MillerSearchIndex.Build(documents), byDocId, bySymbolId, byName, byFilePath, byParentId,
            byFileName, knownExtensions);

        static void Add(Dictionary<string, List<IndexedSymbol>> map, string key, IndexedSymbol value)
        {
            if (!map.TryGetValue(key, out var list))
                map[key] = list = new List<IndexedSymbol>(1);
            list.Add(value);
        }
    }

    // julie stores relative-unix paths; split on '/' (and tolerate '\') for the basename.
    private static string LastPathSegment(string path)
    {
        int slash = path.LastIndexOfAny(SeparatorChars);
        return slash >= 0 ? path[(slash + 1)..] : path;
    }

    private static readonly char[] SeparatorChars = { '/', '\\' };

    private static readonly IReadOnlyList<IndexedSymbol> Empty = Array.Empty<IndexedSymbol>();

    /// <summary>
    /// All symbols sharing the exact <paramref name="name"/> (ordinal), in DocId order. Empty if none.
    /// This is the name-lookup the smart-string resolver and inspect use; it is exact, not tokenized
    /// (the tokenized/scored path is <see cref="Search"/>).
    /// </summary>
    public IReadOnlyList<IndexedSymbol> FindByName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _byName.TryGetValue(name, out var list) ? list : Empty;
    }

    /// <summary>The symbol with the given opaque julie id, or null if not indexed.</summary>
    public IndexedSymbol? FindBySymbolId(string symbolId)
    {
        ArgumentNullException.ThrowIfNull(symbolId);
        return _bySymbolId.TryGetValue(symbolId, out var symbol) ? symbol : null;
    }

    /// <summary>
    /// All symbols whose <see cref="IndexedSymbol.FilePath"/> equals <paramref name="filePath"/> exactly
    /// (ordinal), in DocId order (i.e. by start_line then id). Empty if the path is not indexed.
    /// </summary>
    public IReadOnlyList<IndexedSymbol> FindByFilePath(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        return _byFilePath.TryGetValue(filePath, out var list) ? list : Empty;
    }

    /// <summary>True if <paramref name="path"/> is exactly an indexed file path (ordinal). O(1).</summary>
    public bool IsIndexedFilePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return _byFilePath.ContainsKey(path);
    }

    /// <summary>
    /// Resolve a file target to its canonical indexed path (decision-4 file detection). Returns the path
    /// unchanged if it is already an exact indexed path; otherwise, if the target is a bare basename that
    /// uniquely identifies one indexed file (e.g. <c>UserService.cs</c> → <c>auth/UserService.cs</c>), returns
    /// that full path; returns null if the target matches no indexed file or matches an ambiguous basename
    /// (the caller decides how to render "not found / ambiguous").
    /// </summary>
    public string? ResolveIndexedFilePath(string target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (_byFilePath.ContainsKey(target))
            return target;
        if (_byFileName.TryGetValue(target, out var paths) && paths.Count == 1)
            return paths[0];
        return null;
    }

    /// <summary>
    /// The direct children of the symbol with id <paramref name="parentId"/> (those whose
    /// <see cref="IndexedSymbol.ParentId"/> equals it), in DocId order. Empty if none.
    /// </summary>
    public IReadOnlyList<IndexedSymbol> FindChildren(string parentId)
    {
        ArgumentNullException.ThrowIfNull(parentId);
        return _byParentId.TryGetValue(parentId, out var list) ? list : Empty;
    }

    /// <summary>Search the index (delegates to <see cref="MillerSearchIndex.Search"/>; see its semantics).</summary>
    public IReadOnlyList<SearchHit> Search(string query, int limit = 10, SearchMode mode = SearchMode.Or) =>
        _index.Search(query, limit, mode);

    /// <summary>
    /// Hydrate a search hit's <see cref="SearchableDocument.DocId"/> back to its full <see cref="IndexedSymbol"/>
    /// (carrying the opaque julie symbol id + parent id). O(1).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="docId"/> is not a valid DocId.</exception>
    public IndexedSymbol Resolve(int docId)
    {
        if ((uint)docId >= (uint)_byDocId.Length)
            throw new ArgumentOutOfRangeException(nameof(docId), docId,
                $"DocId must be in [0, {_byDocId.Length}).");
        return _byDocId[docId];
    }
}
