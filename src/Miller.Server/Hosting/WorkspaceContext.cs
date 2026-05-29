namespace Miller.Server;

/// <summary>
/// The resolved workspace paths/ids shared by tools, readers, and startup (M2 §1) — one source of truth so
/// nothing re-derives a path inconsistently. <see cref="ExtractDbPath"/> is the julie extract Miller reads
/// <c>Mode=ReadOnly</c>; <see cref="TelemetryDbPath"/> is the SEPARATE Miller-owned writable ledger;
/// <see cref="ToolsRoot"/> is where the pinned julie-server ships (under the app base dir, NOT the repo cwd).
/// <see cref="WorkspaceId"/> is read from the extract metadata after the scan (null until known).
/// </summary>
public sealed record WorkspaceContext(
    string WorkspaceRoot,   // Environment.CurrentDirectory (the repo Claude Code launched us in)
    string ExtractDbPath,   // <root>/.miller/symbols.db   (julie extract; Miller reads Mode=ReadOnly)
    string TelemetryDbPath, // <root>/.miller/telemetry.db (Miller-owned, writable)
    string ToolsRoot,       // AppContext.BaseDirectory/.tools (where pinned julie-server ships — NOT the repo)
    string? WorkspaceId)    // from external_extract_metadata after scan (nullable until known)
{
    /// <summary>
    /// Build the context from the current working directory + the app base directory, using the M2 path
    /// conventions. <see cref="WorkspaceId"/> starts null; the bootstrap sets it after reading the extract.
    /// </summary>
    public static WorkspaceContext Create(string workspaceRoot, string appBaseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(appBaseDirectory);
        string root = Path.GetFullPath(workspaceRoot);
        string millerDir = Path.Combine(root, ".miller");
        return new WorkspaceContext(
            WorkspaceRoot: root,
            ExtractDbPath: Path.Combine(millerDir, "symbols.db"),
            TelemetryDbPath: Path.Combine(millerDir, "telemetry.db"),
            ToolsRoot: Path.Combine(Path.GetFullPath(appBaseDirectory), ".tools"),
            WorkspaceId: null);
    }
}
