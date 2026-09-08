namespace Miller.Core.Search;

/// <summary>
/// Scored text search hits paired with candidate-window test exclusion facts.
/// </summary>
public sealed record TextContentSearchResult(
    IReadOnlyList<TextContentSearchHit> Hits,
    int ExcludedTestCount = 0,
    bool WindowSaturated = false);
