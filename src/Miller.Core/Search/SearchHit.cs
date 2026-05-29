namespace Miller.Core.Search;

/// <summary>A scored search result: the matched document and its accumulated BM25 score.</summary>
public sealed record SearchHit(SearchableDocument Document, double Score);
