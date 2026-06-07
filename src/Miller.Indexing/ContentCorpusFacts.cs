namespace Miller.Indexing;

public sealed record ContentCorpusFacts(
    string State,
    string? Path,
    int? SchemaVersion,
    long? WorkspaceRevision,
    int SourceCount,
    int ChunkCount,
    long IndexedSourceBytes,
    long StoredRawBytes,
    int StatusSkipped = 0,
    int ScopeSkipped = 0,
    int TooLargeSkipped = 0,
    int MissingSkipped = 0,
    int HashMismatchSkipped = 0,
    int NonUtf8Skipped = 0,
    int IoSkipped = 0,
    string? Error = null);
