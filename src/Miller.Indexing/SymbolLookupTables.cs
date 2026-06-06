namespace Miller.Indexing;

/// <summary>
/// The resident symbol-lookup maps every <see cref="ISymbolLookupIndex"/> backend needs: resolve a
/// <c>DocId</c> ordinal, a julie <c>symbol_id</c>, a name, or a file path. This is everything in
/// <see cref="SymbolSearchProjection"/> EXCEPT the BM25 postings, factored out so the in-memory index
/// and the on-disk FTS5 reader share one implementation (and one set of guarantees) for lookups.
///
/// <para>Requires contiguous 0-based <c>DocId</c>s (the ordinal of the deterministic
/// <c>ORDER BY path, start_line, symbol_id</c> read), so <see cref="Resolve"/> is an O(1) array index.</para>
/// </summary>
public sealed class SymbolLookupTables
{
    private static readonly IReadOnlyList<IndexedSymbol> Empty = Array.Empty<IndexedSymbol>();
    private static readonly char[] SeparatorChars = { '/', '\\' };

    private readonly IndexedSymbol[] _byDocId;
    private readonly Dictionary<string, IndexedSymbol> _bySymbolId;
    private readonly Dictionary<string, List<IndexedSymbol>> _byName;
    private readonly Dictionary<string, List<IndexedSymbol>> _byFilePath;
    private readonly Dictionary<string, List<IndexedSymbol>> _byParentId;
    private readonly Dictionary<string, List<string>> _byFileName;

    private SymbolLookupTables(
        IndexedSymbol[] byDocId,
        Dictionary<string, IndexedSymbol> bySymbolId,
        Dictionary<string, List<IndexedSymbol>> byName,
        Dictionary<string, List<IndexedSymbol>> byFilePath,
        Dictionary<string, List<IndexedSymbol>> byParentId,
        Dictionary<string, List<string>> byFileName,
        IReadOnlySet<string> knownExtensions)
    {
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

    /// <summary>Distinct file extensions present in the corpus (e.g. <c>.cs</c>), OrdinalIgnoreCase.</summary>
    public IReadOnlySet<string> KnownExtensions { get; }

    /// <summary>
    /// Build the lookup maps from <paramref name="symbols"/>, which MUST carry contiguous 0-based
    /// <c>DocId</c>s (position == DocId).
    /// </summary>
    /// <exception cref="ArgumentException">A symbol's <c>DocId</c> does not equal its position.</exception>
    public static SymbolLookupTables Build(IReadOnlyList<IndexedSymbol> symbols)
    {
        ArgumentNullException.ThrowIfNull(symbols);

        var byDocId = new IndexedSymbol[symbols.Count];
        var bySymbolId = new Dictionary<string, IndexedSymbol>(symbols.Count, StringComparer.Ordinal);
        var byName = new Dictionary<string, List<IndexedSymbol>>(StringComparer.Ordinal);
        var byFilePath = new Dictionary<string, List<IndexedSymbol>>(StringComparer.Ordinal);
        var byParentId = new Dictionary<string, List<IndexedSymbol>>(StringComparer.Ordinal);

        for (int i = 0; i < symbols.Count; i++)
        {
            IndexedSymbol symbol = symbols[i];
            if (symbol.DocId != i)
                throw new ArgumentException(
                    $"Symbol at position {i} has DocId {symbol.DocId}; symbol search requires contiguous " +
                    "0-based ordinals from the deterministic symbol read.",
                    nameof(symbols));

            byDocId[i] = symbol;
            bySymbolId[symbol.SymbolId] = symbol;
            Add(byName, symbol.Name, symbol);
            Add(byFilePath, symbol.FilePath, symbol);
            if (symbol.ParentId is { } parentId)
                Add(byParentId, parentId, symbol);
        }

        var byFileName = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var knownExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in byFilePath.Keys)
        {
            string fileName = LastPathSegment(path);
            if (!byFileName.TryGetValue(fileName, out var paths))
                byFileName[fileName] = paths = new List<string>(1);
            paths.Add(path);

            string ext = Path.GetExtension(fileName);
            if (ext.Length > 1)
                knownExtensions.Add(ext);
        }

        return new SymbolLookupTables(byDocId, bySymbolId, byName, byFilePath, byParentId, byFileName, knownExtensions);

        static void Add(Dictionary<string, List<IndexedSymbol>> map, string key, IndexedSymbol value)
        {
            if (!map.TryGetValue(key, out var list))
                map[key] = list = new List<IndexedSymbol>(1);
            list.Add(value);
        }
    }

    /// <summary>Resolve a 0-based <c>DocId</c> ordinal to its symbol.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="docId"/> is outside <c>[0, DocumentCount)</c>.</exception>
    public IndexedSymbol Resolve(int docId)
    {
        if ((uint)docId >= (uint)_byDocId.Length)
            throw new ArgumentOutOfRangeException(nameof(docId), docId,
                $"DocId must be in [0, {_byDocId.Length}).");
        return _byDocId[docId];
    }

    public IReadOnlyList<IndexedSymbol> FindByName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _byName.TryGetValue(name, out var list) ? list : Empty;
    }

    public IndexedSymbol? FindBySymbolId(string symbolId)
    {
        ArgumentNullException.ThrowIfNull(symbolId);
        return _bySymbolId.TryGetValue(symbolId, out var symbol) ? symbol : null;
    }

    public IReadOnlyList<IndexedSymbol> FindChildren(string parentId)
    {
        ArgumentNullException.ThrowIfNull(parentId);
        return _byParentId.TryGetValue(parentId, out var list) ? list : Empty;
    }

    public IReadOnlyList<IndexedSymbol> FindByFilePath(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        return _byFilePath.TryGetValue(filePath, out var list) ? list : Empty;
    }

    public IReadOnlyList<IndexedSymbol> FindByFilePathFragment(string query, int limit) =>
        FilePathSymbolLookup.FindByFilePathFragment(_byFilePath, query, limit);

    public bool IsIndexedFilePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return _byFilePath.ContainsKey(path);
    }

    public string? ResolveIndexedFilePath(string target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (_byFilePath.ContainsKey(target))
            return target;
        if (_byFileName.TryGetValue(target, out var paths) && paths.Count == 1)
            return paths[0];
        return null;
    }

    private static string LastPathSegment(string path)
    {
        int slash = path.LastIndexOfAny(SeparatorChars);
        return slash >= 0 ? path[(slash + 1)..] : path;
    }
}
