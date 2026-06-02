using Miller.Core.Search;

namespace Miller.Indexing;

public interface ISymbolSearchIndex
{
    int DocumentCount { get; }

    IReadOnlyList<SearchHit> Search(string query, int limit = 10, SearchMode mode = SearchMode.Or);

    IndexedSymbol Resolve(int docId);
}
