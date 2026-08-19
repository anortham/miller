namespace Miller.Testing;

/// <summary>
/// Global kill switch plus per-workspace opt-in. CT is off until a workspace is enabled.
/// <c>MILLER_CT=off</c> constructs nothing.
/// </summary>
public static class ContinuousTestPolicy
{
    public const string EnabledFileName = "ct.enabled";

    public static string EnabledMarkerPath(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        return Path.Combine(workspaceRoot, CtDaemonProtocol.MillerDirectoryName, EnabledFileName);
    }

    public static bool IsKillSwitchOff() => IsKillSwitchOff(Environment.GetEnvironmentVariable(CtEnvironment.KillSwitch));

    public static bool IsKillSwitchOff(string? raw) => CtEnvironment.IsOff(raw);

    public static bool IsWorkspaceOptedIn(string workspaceRoot, bool? enabled = null)
    {
        if (enabled is { } flag)
            return flag;
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        return File.Exists(EnabledMarkerPath(workspaceRoot));
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
