namespace Miller.Core.Search;

/// <summary>
/// A scored source-region search result from comment, doc-comment, or string-literal text.
/// </summary>
public sealed record RegionSearchHit(
    string Path,
    double Score,
    int Line,
    string Kind,
    string Snippet,
    string RawText,
    string RegionId,
    string? ContainingSymbolId,
    string? ContainingSymbolName);
