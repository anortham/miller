namespace Miller.Server;

/// <summary>
/// The resolved workspace paths/ids shared by tools, readers, and startup (M2 §1) — one source of truth so
/// nothing re-derives a path inconsistently. <see cref="ExtractDbPath"/> is the julie extract Miller reads
/// <c>Mode=ReadOnly</c>; <see cref="TelemetryDbPath"/> is the SEPARATE Miller-owned writable ledger;
/// <see cref="ToolsRoot"/> is where the pinned julie-server ships (under the app base dir, NOT the repo cwd).
/// <see cref="WorkspaceId"/> is read from the extract metadata after the scan (null until known).
/// </summary>
public sealed record WorkspaceContext(
    string WorkspaceRoot,    // Environment.CurrentDirectory (the repo Claude Code launched us in)
    string ExtractDbPath,    // <root>/.miller/symbols.db   (julie extract; Miller reads Mode=ReadOnly)
    string TelemetryDbPath,  // <root>/.miller/telemetry.db (Miller-owned, writable)
    string ToolsRoot,        // AppContext.BaseDirectory/.tools (where pinned julie-server ships — NOT the repo)
    string? WorkspaceId,     // from external_extract_metadata after scan (nullable until known)
    string? CanonicalRoot = null,          // symlink-resolved WorkspaceRoot (verified-fact 4); set by bootstrap (M3)
    string? CanonicalExtractDbPath = null) // ExtractDbPath composed under CanonicalRoot (verified-fact 4); set by bootstrap (M3)
{
    /// <summary>
    /// Build the context from the current working directory + the app base directory, using the M2 path
    /// conventions. <see cref="WorkspaceId"/>, <see cref="CanonicalRoot"/>, and <see cref="CanonicalExtractDbPath"/>
    /// start null; the bootstrap sets the workspace id after reading the extract, the canonical root via
    /// <c>PathCanonicalizer.CanonicalizeRoot</c>, and the canonical DB path composed under that root (M3) —
    /// symlink resolution needs a real filesystem walk.
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
            WorkspaceId: null,
            CanonicalRoot: null,
            CanonicalExtractDbPath: null);
    }
}
