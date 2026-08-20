namespace Miller.Testing;

/// <summary>
/// Process-environment names for continuous testing. <see cref="KillSwitch"/> is the permanent
/// zero-work opt-out; every other CT variable uses the <c>MILLER_CT_</c> prefix.
/// </summary>
public static class CtEnvironment
{
    public const string KillSwitch = "MILLER_CT";

    public const string WorkspaceRoot = "MILLER_CT_WORKSPACE_ROOT";

    public const string DaemonWorkspaceRoot = "MILLER_CT_DAEMON_WORKSPACE_ROOT";

    /// <summary>
    /// Resolves the workspace root for the <c>ct-daemon</c> verb: the dedicated spawn variable
    /// wins, otherwise the explicit CLI context. The provider-facing <see cref="WorkspaceRoot"/>
    /// variable is never consulted — test processes under CT inherit it, and a CLI verb run
    /// inside such a test must bind its own root, not the workspace under test.
    /// </summary>
    public static string? ResolveDaemonWorkspaceRoot(string? contextRoot, Func<string, string?> readVariable)
    {
        ArgumentNullException.ThrowIfNull(readVariable);
        return readVariable(DaemonWorkspaceRoot) ?? contextRoot;
    }

    /// <summary>
    /// Overrides how long a test process may stay SILENT before the run is treated as wedged and killed.
    /// Accepts whole seconds (<c>900</c>) or a TimeSpan (<c>00:15:00</c>). <c>off</c>/<c>0</c>/<c>false</c>/
    /// <c>no</c> disables the guard and restores the unbounded wait.
    /// </summary>
    public const string StallTimeout = "MILLER_CT_STALL_TIMEOUT";

    public static bool IsOff() => IsOff(Environment.GetEnvironmentVariable(KillSwitch));

    /// <summary>
    /// The stall bound to use, or <paramref name="fallback"/> when the variable is unset or unreadable.
    ///
    /// <para>An unparseable value falls back rather than throwing. This variable decides only whether a
    /// SILENT run is killed; a typo in it must not stop CT from running at all, and the default it falls back
    /// to is the safe one.</para>
    /// </summary>
    public static TimeSpan ResolveStallTimeout(string? raw, TimeSpan fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        if (IsOff(raw))
            return Timeout.InfiniteTimeSpan;

        string trimmed = raw.Trim();
        if (int.TryParse(trimmed, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int seconds))
        {
            return seconds <= 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(seconds);
        }

        if (TimeSpan.TryParse(trimmed, System.Globalization.CultureInfo.InvariantCulture, out TimeSpan parsed))
            return parsed <= TimeSpan.Zero ? Timeout.InfiniteTimeSpan : parsed;

        return fallback;
    }

    /// <summary>
    /// True only for an explicit falsy token (<c>off/0/false/no</c>, any case). Unset or blank stays on.
    /// </summary>
    public static bool IsOff(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        return raw.Trim().ToLowerInvariant() switch
        {
            "off" or "0" or "false" or "no" => true,
            _ => false,
        };
    }
}
