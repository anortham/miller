using System.Globalization;

namespace Miller.Indexing;

/// <summary>
/// Pure decision core for julie-extract's <c>--jobs</c> cap. julie-extract's own default is rayon
/// auto-detect (every core), so N concurrent worktree agents ran N all-core extraction pools on one
/// machine and the OOM killer took them out with exit 137 (2026-08-01 multi-worktree field report).
/// Miller therefore always passes an explicit cap. Pure — the environment read is a separate, thin
/// entry point — so the fast suite covers every value.
///
/// <para>This bounds only julie-extract's extraction/spool phase, not its artifact write and not the
/// per-process SQLite cache. It reduces peak RSS per scan; bounding how many scans run at once is a
/// separate concern.</para>
/// </summary>
public static class ExtractJobsPolicy
{
    /// <summary>Operator override for the computed default. <c>0</c> opts back in to rayon auto-detect.</summary>
    public const string EnvVar = "MILLER_EXTRACT_JOBS";

    /// <summary>Ceiling on the computed default, regardless of how many cores the machine has.</summary>
    public const int MaxDefaultJobs = 4;

    /// <summary>The value julie-extract reads as "auto-detect from available cores".</summary>
    public const int RayonAuto = 0;

    /// <summary>Resolve the cap from the process environment and this machine's core count.</summary>
    public static int FromEnvironment() =>
        FromEnvValue(Environment.GetEnvironmentVariable(EnvVar), Environment.ProcessorCount);

    /// <summary>
    /// The pure env-value ⇒ cap mapping behind <see cref="FromEnvironment"/> — testable without mutating
    /// the process environment (which would leak across xUnit's parallel collections). Blank, negative, and
    /// unparseable values fall back to <see cref="DefaultFor"/>; any non-negative value is honored verbatim,
    /// including one above <see cref="MaxDefaultJobs"/>, because the variable is a deliberate operator
    /// override and clamping it could not express a dedicated build box.
    /// </summary>
    internal static int FromEnvValue(string? raw, int processorCount)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return DefaultFor(processorCount);

        return int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
               && parsed >= 0
            ? parsed
            : DefaultFor(processorCount);
    }

    /// <summary>Half the available cores, at least one and at most <see cref="MaxDefaultJobs"/>.</summary>
    internal static int DefaultFor(int processorCount) =>
        Math.Min(MaxDefaultJobs, Math.Max(1, processorCount / 2));
}
