using Miller.Indexing;

namespace Miller.Testing;

/// <summary>
/// Global kill switch plus per-workspace opt-in. CT is off until a workspace is enabled.
/// <c>MILLER_CT=off</c> constructs nothing.
///
/// <para>A linked git worktree inherits the MAIN checkout's opt-in, because <c>.miller/</c> is not
/// in git and a fresh worktree of an enabled repo would otherwise start with CT off. Precedence,
/// strictest first: kill switch → local <c>ct.disabled</c> tombstone → local <c>ct.enabled</c> →
/// inherited main-checkout <c>ct.enabled</c> (linked worktrees only) → off. A non-git root, a
/// normal <c>.git</c>-directory checkout, or an unreadable/malformed worktree link inherits
/// nothing and fails closed to off.</para>
/// </summary>
public static class ContinuousTestPolicy
{
    public const string EnabledFileName = "ct.enabled";

    /// <summary>
    /// The local opt-out tombstone. It beats a local <see cref="EnabledFileName"/> marker and the
    /// enablement a linked worktree would inherit from its main checkout, so a worktree's
    /// <c>tests disable</c> sticks without touching the main checkout's marker.
    /// </summary>
    public const string DisabledFileName = "ct.disabled";

    public static string EnabledMarkerPath(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        return Path.Combine(workspaceRoot, CtDaemonProtocol.MillerDirectoryName, EnabledFileName);
    }

    public static string DisabledMarkerPath(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        return Path.Combine(workspaceRoot, CtDaemonProtocol.MillerDirectoryName, DisabledFileName);
    }

    public static bool IsKillSwitchOff() => IsKillSwitchOff(Environment.GetEnvironmentVariable(CtEnvironment.KillSwitch));

    public static bool IsKillSwitchOff(string? raw) => CtEnvironment.IsOff(raw);

    /// <summary>
    /// A pure filesystem probe: it reads marker files (and, for a linked worktree, the two git
    /// pointer files), and never creates a file, a directory, or <c>ct.db</c>.
    /// </summary>
    public static bool IsWorkspaceOptedIn(string workspaceRoot, bool? enabled = null)
    {
        if (enabled is { } flag)
            return flag;
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        if (File.Exists(DisabledMarkerPath(workspaceRoot)))
            return false;
        if (File.Exists(EnabledMarkerPath(workspaceRoot)))
            return true;
        return InheritsMainCheckoutOptIn(workspaceRoot);
    }

    /// <summary>
    /// True only for a linked worktree whose resolved main checkout carries the enabled marker.
    /// <see cref="GitWorktreeLayout.Resolve"/> is filesystem-only and returns null for a non-git
    /// root or a broken layout, so every uncertain case reads as "inherits nothing".
    /// </summary>
    private static bool InheritsMainCheckoutOptIn(string workspaceRoot)
    {
        GitWorktreeLayout? layout = GitWorktreeLayout.Resolve(workspaceRoot);
        return layout is { IsLinkedWorktree: true, MainCheckoutRoot: { } mainRoot }
            && File.Exists(EnabledMarkerPath(mainRoot));
    }

    public static bool ShouldConstructEngine(string workspaceRoot, string? killSwitch = null, bool? enabled = null) =>
        !IsKillSwitchOff(killSwitch) && IsWorkspaceOptedIn(workspaceRoot, enabled);

    /// <summary>
    /// Start is status-only. Enqueue only after the first observed key, on a later different key,
    /// or on an explicit run.
    /// </summary>
    public static bool ShouldEnqueueAfterStart(
        CtFreshnessKey? startedAt,
        CtFreshnessKey? observed,
        bool explicitRun)
    {
        if (explicitRun)
            return true;
        if (startedAt is null || observed is null)
            return false;
        return startedAt != observed;
    }
}
