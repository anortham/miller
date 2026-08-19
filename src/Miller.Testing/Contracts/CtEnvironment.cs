namespace Miller.Testing;

/// <summary>
/// Process-environment names for continuous testing. <see cref="KillSwitch"/> is the permanent
/// zero-work opt-out; every other CT variable uses the <c>MILLER_CT_</c> prefix.
/// </summary>
public static class CtEnvironment
{
    public const string KillSwitch = "MILLER_CT";

    public const string WorkspaceRoot = "MILLER_CT_WORKSPACE_ROOT";

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
