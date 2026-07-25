namespace Miller.Core.Search;

public sealed record TextContentSearchHit(
    string SourceId,
    string ChunkId,
    string ContentKind,
    string? Path,
    string? Url,
    string DisplayPath,
    string Language,
    double Score,
    int Line,
    int LineStart,
    int LineEnd,
    long ByteStart,
    long ByteEnd,
    string Snippet,
    long SourceBytes,
    string? ContainingSymbolId,
    string? ContainingSymbolName,
    string? ContentHash = null);
