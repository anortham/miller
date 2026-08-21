namespace Miller.Server.Workspaces;

/// <summary>
/// How a read tool routes the refresh of a REGISTERED (cross-workspace) target. The three values are the whole
/// contract behind <c>ensure_fresh</c>, and they exist as an enum rather than a <c>bool</c> because
/// "do not refresh in the foreground" and "do not refresh at all" are different promises that a single flag
/// cannot tell apart.
///
/// <para><see cref="None"/> is the explicit <c>ensure_fresh=false</c> zero-work read.
/// <see cref="Background"/> is the DEFAULT for an explicit <c>workspace_id</c>: serve the pinned view now and
/// run the refresh off the read path, so the caller pays no scan latency and the NEXT call sees fresher data.
/// <see cref="Blocking"/> is the explicit <c>ensure_fresh=true</c> read: a caller who asks to wait still waits.</para>
///
/// <para>A <see cref="Background"/> request against a workspace with NO readable index cannot serve stale — there
/// is nothing to serve — so <c>WorkspaceIndexProvider</c> upgrades that one case to <see cref="Blocking"/>.</para>
/// </summary>
public enum WorkspaceRefreshMode
{
    /// <summary>Read the pinned view and start no refresh at all.</summary>
    None = 0,

    /// <summary>Serve the pinned view immediately; run the refresh off the read path, coalesced per workspace.</summary>
    Background = 1,

    /// <summary>Refresh first and wait for it, then read.</summary>
    Blocking = 2,
}
