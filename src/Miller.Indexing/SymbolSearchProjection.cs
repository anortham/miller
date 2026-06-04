using Miller.Core.Search;

namespace Miller.Indexing;

/// <summary>
/// The lean, search-only <see cref="ISymbolLookupIndex"/>: the in-memory BM25 <see cref="MillerSearchIndex"/>
/// for ranking plus the shared <see cref="SymbolLookupTables"/> for id/name/path resolution. No graph or
/// bridge data — this is what cross-workspace reads load instead of the full repository index.
/// </summary>
public sealed class SymbolSearchProjection : ISymbolLookupIndex
{
    private readonly MillerSearchIndex _index;
    private readonly SymbolLookupTables _tables;

    private SymbolSearchProjection(MillerSearchIndex index, SymbolLookupTables tables)
    {
        _index = index;
        _tables = tables;
    }

    public int DocumentCount => _tables.DocumentCount;

    public IReadOnlySet<string> KnownExtensions => _tables.KnownExtensions;

    public static SymbolSearchProjection Build(IReadOnlyList<IndexedSymbol> symbols)
    {
        ArgumentNullException.ThrowIfNull(symbols);

        SymbolLookupTables tables = SymbolLookupTables.Build(symbols);

        var documents = new SearchableDocument[symbols.Count];
        for (int i = 0; i < symbols.Count; i++)
            documents[i] = symbols[i].ToSearchableDocument();

        return new SymbolSearchProjection(MillerSearchIndex.Build(documents), tables);
    }

    public IReadOnlyList<SearchHit> Search(string query, int limit = 10, SearchMode mode = SearchMode.Or) =>
        _index.Search(query, limit, mode);

    public IReadOnlyList<IndexedSymbol> FindByName(string name) => _tables.FindByName(name);

    public IndexedSymbol? FindBySymbolId(string symbolId) => _tables.FindBySymbolId(symbolId);

    public IReadOnlyList<IndexedSymbol> FindByFilePath(string filePath) => _tables.FindByFilePath(filePath);

    public IReadOnlyList<IndexedSymbol> FindByFilePathFragment(string query, int limit) =>
        _tables.FindByFilePathFragment(query, limit);

    public bool IsIndexedFilePath(string path) => _tables.IsIndexedFilePath(path);

    public string? ResolveIndexedFilePath(string target) => _tables.ResolveIndexedFilePath(target);

    public IndexedSymbol Resolve(int docId) => _tables.Resolve(docId);
}
