namespace Miller.Core.Search;

public sealed record TextContentDocument(
    string SourceId,
    string ChunkId,
    string ContentKind,
    string? Path,
    string? Url,
    string DisplayPath,
    string Language,
    int LineStart,
    int LineEnd,
    long ByteStart,
    long ByteEnd,
    string Text,
    int DocLen,
    bool IsTest,
    long SourceBytes,
    string? ContainingSymbolId,
    string? ContainingSymbolName);
