using Miller.Testing;

namespace Miller.Server.Cli;

/// <summary>
/// Whether the build asking for the dashboard may stop the dashboard already running, and what to
/// say about it either way.
///
/// <para>The ordering rules are NOT re-implemented here. <see cref="CtDaemonVersion.Evaluate"/> is the
/// one place that decides sameness (the whole build string, ordinal) and direction (numeric
/// <c>major.minor.patch</c>, never text order). This wrapper only re-words the verdict for a dashboard
/// and applies the one rule the dashboard does not share with the CT daemon.</para>
///
/// <para>That rule is the UNRECORDED build. <c>dashboard.json</c> gained its version field with this
/// feature, so a record without one was written by a build that predates the check — which is exactly
/// the stale dashboard an upgrade leaves behind, still serving old pages and, on Windows, still locking
/// its plugin-cache directory. The CT daemon refuses to act on an unrecorded build because its lease has
/// always carried the field, so a missing one there means unreadable rather than old. Here it means old,
/// so it is replaced.</para>
/// </summary>
internal sealed record DashboardVersionDecision(
    bool MayReplace,
    bool Mismatch,
    string? RunningVersion,
    string Reason)
{
    public string RunningVersionLabel =>
        string.IsNullOrWhiteSpace(RunningVersion) ? "unknown" : RunningVersion;

    public static DashboardVersionDecision For(string ownVersion, string? runningVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownVersion);

        CtDaemonVersionVerdict verdict = CtDaemonVersion.Evaluate(ownVersion, runningVersion);
        return verdict.Match switch
        {
            CtDaemonVersionMatch.Same => new DashboardVersionDecision(
                MayReplace: false,
                Mismatch: false,
                runningVersion,
                $"the dashboard runs this build ({ownVersion})"),
            CtDaemonVersionMatch.DaemonOlder => new DashboardVersionDecision(
                MayReplace: true,
                Mismatch: true,
                runningVersion,
                $"the dashboard runs an older build ({runningVersion}); this is {ownVersion}"),
            CtDaemonVersionMatch.DaemonNewer => new DashboardVersionDecision(
                MayReplace: false,
                Mismatch: true,
                runningVersion,
                $"the dashboard runs a newer build ({runningVersion}); this is {ownVersion}"),
            CtDaemonVersionMatch.BuildDiffers => new DashboardVersionDecision(
                MayReplace: true,
                Mismatch: true,
                runningVersion,
                $"the dashboard runs the same release from a different build ({runningVersion}); "
                + $"this is {ownVersion}"),
            _ => Unordered(ownVersion, runningVersion),
        };
    }

    private static DashboardVersionDecision Unordered(string ownVersion, string? runningVersion) =>
        string.IsNullOrWhiteSpace(runningVersion)
            ? new DashboardVersionDecision(
                MayReplace: true,
                Mismatch: true,
                RunningVersion: null,
                $"the dashboard records no build, so it predates this check; this is {ownVersion}")
            : new DashboardVersionDecision(
                MayReplace: false,
                Mismatch: true,
                runningVersion,
                $"the dashboard runs build '{runningVersion}' and this is '{ownVersion}'; "
                + "neither can be ordered, so the dashboard is left alone");
}
