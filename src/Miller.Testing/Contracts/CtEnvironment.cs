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

    public static bool IsOff() => IsOff(Environment.GetEnvironmentVariable(KillSwitch));

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
