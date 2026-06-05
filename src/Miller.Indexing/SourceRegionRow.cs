namespace Miller.Indexing;

/// <summary>
/// A source span from julie's <c>source_regions</c> table, joined with the file freshness facts Miller needs
/// before slicing region text from disk.
/// </summary>
public sealed record SourceRegionRow(
    string SourceRegionId,
    string FileId,
    string Path,
    string Language,
    string Kind,
    string? ContainingSymbolId,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn,
    int StartByte,
    int EndByte,
    string? MetadataJson,
    string ContentHash,
    long ContentBytes,
    string Status);
