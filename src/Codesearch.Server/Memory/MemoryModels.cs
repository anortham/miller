namespace Codesearch.Server.Memory;

/// <summary>
/// Types of memories that can be stored.
/// </summary>
internal enum MemoryType
{
    Checkpoint,
    Plan,
    Decision,
    Learning
}

/// <summary>
/// Git context captured when creating a memory.
/// </summary>
internal record GitContext
{
    public string? Branch { get; init; }
    public string? Commit { get; init; }
    public bool Dirty { get; init; }
    public List<string> FilesChanged { get; init; } = new();
}

/// <summary>
/// Memory metadata stored in frontmatter.
/// </summary>
internal record MemoryMetadata
{
    public required string Id { get; init; }
    public required MemoryType Type { get; init; }
    public required long Timestamp { get; init; }
    public List<string> Tags { get; init; } = new();
    public GitContext? Git { get; init; }

    // Plan-specific
    public string? Title { get; init; }
    public string? Status { get; init; }  // pending, in_progress, completed

    // Decision-specific
    public List<string>? Options { get; init; }
    public string? Chosen { get; init; }
}

/// <summary>
/// Complete memory entry with metadata and content.
/// </summary>
internal record MemoryEntry
{
    public required MemoryMetadata Metadata { get; init; }
    public required string Content { get; init; }
    public required string FilePath { get; init; }
}

/// <summary>
/// Result of a recall operation.
/// </summary>
internal record RecallResult
{
    public required List<MemoryEntry> Entries { get; init; }
    public required int TotalCount { get; init; }
}
