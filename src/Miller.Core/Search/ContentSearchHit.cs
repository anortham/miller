namespace Miller.Core.Search;

/// <summary>
/// A scored content search result: the matched file <see cref="Path"/>, its accumulated BM25
/// <see cref="Score"/>, the 1-based <see cref="Line"/> that best matches the query, and a
/// <see cref="Snippet"/> window of context lines around it (newline-joined, raw file text).
/// </summary>
public sealed record ContentSearchHit(
    string Path,
    double Score,
    int Line,
    string Snippet,
    string Language = "",
    long SourceBytes = 0);
