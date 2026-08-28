using Miller.Core.Search;
using Miller.Indexing;

namespace Miller.Server.Workspaces;

internal sealed class ContextSearchCacheLookupIndex(ISymbolLookupIndex inner) : ISymbolLookupIndex
{
    private const int Capacity = 64;
    private const int RowCapacity = 10_000;
    private readonly Dictionary<SearchCacheKey, IReadOnlyList<SearchHit>> _searches = new(Capacity);
    private int _retainedRows;

    public int DocumentCount => inner.DocumentCount;

    public IReadOnlySet<string> KnownExtensions => inner.KnownExtensions;

    public IReadOnlyList<SearchHit> Search(string query, int limit = 10, SearchMode mode = SearchMode.Or)
    {
        var key = new SearchCacheKey(query, limit, mode);
        if (_searches.TryGetValue(key, out IReadOnlyList<SearchHit>? cached))
        {
            SearchRequestTelemetryCollector.Current?.RecordCacheHit(cached.Count);
            return cached;
        }

        IReadOnlyList<SearchHit> result = inner.Search(query, limit, mode);
        if (_searches.Count < Capacity && result.Count <= RowCapacity - _retainedRows)
        {
            _searches.Add(key, result);
            _retainedRows += result.Count;
        }
        return result;
    }

    public IndexedSymbol Resolve(int docId) => inner.Resolve(docId);

    public IReadOnlyDictionary<int, IndexedSymbol> ResolveMany(IReadOnlyCollection<int> docIds) =>
        inner.ResolveMany(docIds);

    public IReadOnlyList<IndexedSymbol> FindByName(string name) => inner.FindByName(name);

    public IndexedSymbol? FindBySymbolId(string symbolId) => inner.FindBySymbolId(symbolId);

    public IReadOnlyList<IndexedSymbol> FindChildren(string parentId) => inner.FindChildren(parentId);

    public IReadOnlyList<IndexedSymbol> FindByFilePath(string filePath) => inner.FindByFilePath(filePath);

    public IReadOnlyList<IndexedSymbol> FindByFilePathFragment(string query, int limit) =>
        inner.FindByFilePathFragment(query, limit);

    public IReadOnlyList<string> FindFilePathsByFragment(string query, int limit) =>
        inner.FindFilePathsByFragment(query, limit);

    public bool IsIndexedFilePath(string path) => inner.IsIndexedFilePath(path);

    public string? ResolveIndexedFilePath(string target) => inner.ResolveIndexedFilePath(target);

    private readonly record struct SearchCacheKey(string? Query, int Limit, SearchMode Mode);
}
