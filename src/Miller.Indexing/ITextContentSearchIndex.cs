using Miller.Core.Search;

namespace Miller.Indexing;

public interface ITextContentSearchIndex
{
    int DocumentCount { get; }

    IReadOnlyList<TextContentSearchHit> Search(
        string query,
        string contentKind,
        int limit = 10,
        bool excludeTests = false);
}
