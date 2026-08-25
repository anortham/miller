namespace Miller.Indexing;

/// <summary>
/// The single resolver for the user-global home directory that Miller's machine-wide state hangs off —
/// <c>&lt;home&gt;/.miller/</c> holds the workspace registry, the telemetry DB, the scan governor lock, the
/// semantic broker's lock/socket, and the family stores.
///
/// <para><b>Why this exists (load-bearing).</b> <see cref="Environment.SpecialFolder.UserProfile"/> resolves
/// through the Windows known-folder API and <b>ignores <c>USERPROFILE</c> and <c>HOME</c></b>. Every test
/// fixture that "isolated" a child Miller process by setting those variables was therefore a no-op on
/// Windows: the parent governed a lock under its temp home while every spawned child governed the real one,
/// two disjoint files, so a held lease could never refuse a child. That silently broke five scan-governor
/// tests plus a CLI subprocess test, and — worse than the red tests — let the suite write into the developer's
/// real registry and steal the machine-wide scan lease from a live Miller server (2026-08-12 triage).</para>
///
/// <para><b>Convert every home-derived path together or not at all.</b> A child that reads this for its
/// registry but the known folder for its governor splits its own state across two homes, which is harder to
/// debug than no isolation at all.</para>
///
/// <para><b>Deliberately NOT routed through here:</b>
/// <list type="bullet">
/// <item><description><c>WorkspaceRootSafety</c> — the sensitive-root guard must keep resolving the REAL
/// profile. Making the forbidden set steerable by an environment variable is a security regression, and
/// CLAUDE.md marks that set load-bearing.</description></item>
/// <item><description><c>WorkspaceBindingResolver</c>'s plugin-install-root probing — it looks for a real
/// installation, not for Miller state.</description></item>
/// <item><description>The <c>~/.cache</c> embedding-model directory — shared by design under ADR-0003, and
/// re-downloading a 133 MB model per test run is not isolation worth having.</description></item>
/// </list></para>
/// </summary>
public static class MillerHome
{
    /// <summary>Overrides the resolved home. Intended for tests and sandboxed runs.</summary>
    public const string EnvironmentVariable = "MILLER_HOME";

    /// <summary>The home directory: <see cref="EnvironmentVariable"/> when set, else the user profile.</summary>
    public static string Resolve() => Resolve(Environment.GetEnvironmentVariable);

    /// <summary>Testable overload. A blank or whitespace override is ignored.</summary>
    public static string Resolve(
        Func<string, string?> readEnvironmentVariable, Func<string>? readUserProfile = null)
    {
        ArgumentNullException.ThrowIfNull(readEnvironmentVariable);
        string? configured = readEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured);

        string profile = readUserProfile is null
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : readUserProfile();

        // A broken or roaming Windows profile makes the known-folder call return an empty string, and
        // Path.Combine("", ".miller") yields the RELATIVE path ".miller". Miller would then put the registry,
        // the governor lock, and its logs inside whatever directory it was launched from — a silent third
        // location nobody looks in. Failing by name is the honest outcome.
        if (!Path.IsPathRooted(profile))
        {
            throw new InvalidOperationException(
                "Miller cannot resolve the user profile directory, so it has nowhere to keep its machine-wide "
                + $"state. Set {EnvironmentVariable} to an absolute path.");
        }

        return profile;
    }

    /// <summary>The <c>.miller</c> directory under <see cref="Resolve()"/>.</summary>
    public static string ResolveMillerDirectory() => Path.Combine(Resolve(), ".miller");
}
