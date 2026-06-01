namespace Miller.Server;

/// <summary>
/// The resolved workspace paths/ids shared by tools, readers, and startup (M2 §1) — one source of truth so
/// nothing re-derives a path inconsistently. <see cref="ExtractDbPath"/> is the julie extract Miller reads
/// <c>Mode=ReadOnly</c> and is genuinely per-repo (under <c>&lt;root&gt;/.miller</c>); <see cref="TelemetryDbPath"/>
/// is the SEPARATE Miller-owned writable ledger and is MACHINE-GLOBAL — one shared DB under <c>&lt;home&gt;/.miller</c>
/// that collects tool-usage rows from every workspace (each row carries its <c>workspace_id</c>/<c>workspace_root</c>),
/// so cross-repo usage aggregates in one place and a per-repo index rebuild (<c>rm -rf .miller</c>) never wipes it.
/// <see cref="RegistryDbPath"/> is the shared workspace metadata registry under the same machine-global Miller dir.
/// <see cref="ToolsRoot"/> is where the pinned julie-extract ships (under the app base dir, NOT the repo cwd).
/// <see cref="WorkspaceId"/> is read from the extract metadata after the scan (null until known).
/// </summary>
public sealed record WorkspaceContext(
    string WorkspaceRoot,    // Environment.CurrentDirectory (the repo Claude Code launched us in)
    string ExtractDbPath,    // <root>/.miller/symbols.db   (julie extract; Miller reads Mode=ReadOnly; per-repo)
    string TelemetryDbPath,  // <home>/.miller/telemetry.db (Miller-owned, writable; machine-global, shared across workspaces)
    string RegistryDbPath,   // <home>/.miller/workspaces.db (Miller-owned metadata registry; machine-global)
    string ToolsRoot,        // AppContext.BaseDirectory/.tools (where pinned julie-extract ships — NOT the repo)
    string? WorkspaceId,     // from external_extract_metadata after scan (nullable until known)
    string? CanonicalRoot = null,          // symlink-resolved WorkspaceRoot (verified-fact 4); set by bootstrap (M3)
    string? CanonicalExtractDbPath = null) // ExtractDbPath composed under CanonicalRoot (verified-fact 4); set by bootstrap (M3)
{
    /// <summary>
    /// Build the context from the current working directory + the app base directory, using the M2 path
    /// conventions. The per-repo extract lives under <paramref name="workspaceRoot"/>; the shared telemetry
    /// ledger lives under <paramref name="homeDirectory"/> (the user profile — pass null in production to resolve
    /// it from <see cref="Environment.SpecialFolder.UserProfile"/>; tests inject a temp dir).
    /// <see cref="WorkspaceId"/>, <see cref="CanonicalRoot"/>, and <see cref="CanonicalExtractDbPath"/> start
    /// null; the bootstrap sets the workspace id after reading the extract, the canonical root via
    /// <c>PathCanonicalizer.CanonicalizeRoot</c>, and the canonical DB path composed under that root (M3) —
    /// symlink resolution needs a real filesystem walk.
    /// </summary>
    public static WorkspaceContext Create(string workspaceRoot, string appBaseDirectory, string? homeDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(appBaseDirectory);
        string root = Path.GetFullPath(workspaceRoot);
        string home = Path.GetFullPath(
            string.IsNullOrWhiteSpace(homeDirectory)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : homeDirectory);
        return new WorkspaceContext(
            WorkspaceRoot: root,
            ExtractDbPath: Path.Combine(root, ".miller", "symbols.db"),
            TelemetryDbPath: Path.Combine(home, ".miller", "telemetry.db"),
            RegistryDbPath: Path.Combine(home, ".miller", "workspaces.db"),
            ToolsRoot: Path.Combine(Path.GetFullPath(appBaseDirectory), ".tools"),
            WorkspaceId: null,
            CanonicalRoot: null,
            CanonicalExtractDbPath: null);
    }
}
