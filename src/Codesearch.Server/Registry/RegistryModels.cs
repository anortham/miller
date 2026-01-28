namespace Codesearch.Server.Registry;

/// <summary>
/// Entry for a registered project in the central registry.
/// </summary>
internal record ProjectEntry
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required DateTimeOffset LastActive { get; init; }
    public DateTimeOffset? IndexedAt { get; init; }
}

/// <summary>
/// Central registry of known projects.
/// </summary>
internal record ProjectRegistry
{
    public string Version { get; init; } = "1.0";
    public DateTimeOffset LastUpdated { get; init; } = DateTimeOffset.UtcNow;
    public Dictionary<string, ProjectEntry> Projects { get; init; } = new();
}

/// <summary>
/// Summary of a workspace for cross-project results.
/// </summary>
internal record WorkspaceSummary
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required int CheckpointCount { get; init; }
    public DateTimeOffset? LastActivity { get; init; }
}

/// <summary>
/// Result of a cross-project recall operation.
/// </summary>
internal record CrossProjectRecallResult
{
    public required List<Memory.MemoryEntry> Entries { get; init; }
    public required List<WorkspaceSummary> Workspaces { get; init; }
    public required int TotalCount { get; init; }
}
