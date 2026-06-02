using Miller.Core.Search;

namespace Miller.Indexing;

public sealed class SymbolSearchProjection : ISymbolLookupIndex
{
    private readonly MillerSearchIndex _index;
    private readonly IndexedSymbol[] _byDocId;
    private readonly Dictionary<string, IndexedSymbol> _bySymbolId;
    private readonly Dictionary<string, List<IndexedSymbol>> _byName;
    private readonly Dictionary<string, List<IndexedSymbol>> _byFilePath;
    private readonly Dictionary<string, List<string>> _byFileName;

    private SymbolSearchProjection(
        MillerSearchIndex index,
        IndexedSymbol[] byDocId,
        Dictionary<string, IndexedSymbol> bySymbolId,
        Dictionary<string, List<IndexedSymbol>> byName,
        Dictionary<string, List<IndexedSymbol>> byFilePath,
        Dictionary<string, List<string>> byFileName,
        IReadOnlySet<string> knownExtensions)
    {
        _index = index;
        _byDocId = byDocId;
        _bySymbolId = bySymbolId;
        _byName = byName;
        _byFilePath = byFilePath;
        _byFileName = byFileName;
        KnownExtensions = knownExtensions;
    }

    public int DocumentCount => _byDocId.Length;

    public IReadOnlySet<string> KnownExtensions { get; }

    public static SymbolSearchProjection Build(IReadOnlyList<IndexedSymbol> symbols)
    {
        ArgumentNullException.ThrowIfNull(symbols);

        var byDocId = new IndexedSymbol[symbols.Count];
        var documents = new SearchableDocument[symbols.Count];
        var bySymbolId = new Dictionary<string, IndexedSymbol>(symbols.Count, StringComparer.Ordinal);
        var byName = new Dictionary<string, List<IndexedSymbol>>(StringComparer.Ordinal);
        var byFilePath = new Dictionary<string, List<IndexedSymbol>>(StringComparer.Ordinal);

        for (int i = 0; i < symbols.Count; i++)
        {
            IndexedSymbol symbol = symbols[i];
            if (symbol.DocId != i)
                throw new ArgumentException(
                    $"Symbol at position {i} has DocId {symbol.DocId}; symbol search requires contiguous " +
                    "0-based ordinals from SqliteSymbolReader.",
                    nameof(symbols));

            byDocId[i] = symbol;
            documents[i] = symbol.ToSearchableDocument();
            bySymbolId[symbol.SymbolId] = symbol;
            Add(byName, symbol.Name, symbol);
            Add(byFilePath, symbol.FilePath, symbol);
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

        return new SymbolSearchProjection(
            MillerSearchIndex.Build(documents),
            byDocId,
            bySymbolId,
            byName,
            byFilePath,
            byFileName,
            knownExtensions);

        static void Add(Dictionary<string, List<IndexedSymbol>> map, string key, IndexedSymbol value)
        {
            if (!map.TryGetValue(key, out var list))
                map[key] = list = new List<IndexedSymbol>(1);
            list.Add(value);
        }
    }

    private static string LastPathSegment(string path)
    {
        int slash = path.LastIndexOfAny(SeparatorChars);
        return slash >= 0 ? path[(slash + 1)..] : path;
    }

    private static readonly char[] SeparatorChars = { '/', '\\' };

    private static readonly IReadOnlyList<IndexedSymbol> Empty = Array.Empty<IndexedSymbol>();

    public IReadOnlyList<SearchHit> Search(string query, int limit = 10, SearchMode mode = SearchMode.Or) =>
        _index.Search(query, limit, mode);

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

    public IReadOnlyList<IndexedSymbol> FindByFilePath(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        return _byFilePath.TryGetValue(filePath, out var list) ? list : Empty;
    }

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

    public IndexedSymbol Resolve(int docId)
    {
        if ((uint)docId >= (uint)_byDocId.Length)
            throw new ArgumentOutOfRangeException(nameof(docId), docId,
                $"DocId must be in [0, {_byDocId.Length}).");
        return _byDocId[docId];
    }
}
