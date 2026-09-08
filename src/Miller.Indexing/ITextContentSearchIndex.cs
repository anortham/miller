using Miller.Core.Search;

namespace Miller.Indexing;

public interface ITextContentSearchIndex
{
    int DocumentCount { get; }

    IReadOnlyList<TextContentSearchHit> Search(
        string query,
        string contentKind,
        int limit = 10,
        bool excludeTests = false) =>
        Search(query, contentKind, limit, excludeTests, sourceId: null);

    IReadOnlyList<TextContentSearchHit> Search(
        string query,
        IReadOnlyCollection<string> contentKinds,
        int limit = 10,
        bool excludeTests = false) =>
        Search(query, contentKinds, limit, excludeTests, sourceId: null);

    IReadOnlyList<TextContentSearchHit> Search(
        string query,
        string contentKind,
        int limit,
        bool excludeTests,
        string? sourceId) =>
        Search(query, contentKind, limit, excludeTests);

    IReadOnlyList<TextContentSearchHit> Search(
        string query,
        IReadOnlyCollection<string> contentKinds,
        int limit,
        bool excludeTests,
        string? sourceId) =>
        Search(query, contentKinds, limit, excludeTests);

    TextContentSearchResult SearchExtended(
        string query,
        IReadOnlyCollection<string> contentKinds,
        int limit = 10,
        bool excludeTests = false,
        string? sourceId = null) =>
        new(Search(query, contentKinds, limit, excludeTests, sourceId), 0, false);

    TextContentSearchResult SearchExtended(
        string query,
        string contentKind,
        int limit = 10,
        bool excludeTests = false,
        string? sourceId = null) =>
        SearchExtended(query, [contentKind], limit, excludeTests, sourceId);
}
