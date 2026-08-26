using System.Globalization;

namespace Miller.Core.Freshness;

/// <summary>
/// julie-extract's own discovery limits, mirrored here so Miller never SUBMITS a file julie's discovery
/// refuses. An extension catalog alone is not enough: julie also hard-excludes oversized files and generated
/// artifacts, and a file it refuses never lands in the store — so an incremental discovery loop that keeps
/// finding it "missing from the manifest" re-submits it on every pass, forever. A 32 MB generated
/// <c>src/parser.c</c> did exactly that and wedged the family-store coordinator queue (2026-08-25 field
/// report).
///
/// <para>Mirrors <c>julie-extract-cli/src/limits.rs</c> (<c>MAX_SOURCE_FILE_BYTES</c>) and
/// <c>julie-extract-cli/src/discovery.rs</c> (<c>HARD_EXCLUDE_SUFFIXES</c>). Both sides use a STRICT
/// greater-than comparison, so a file of exactly the limit is still extractable.</para>
///
/// <para>julie-extract 2.37.0 PUBLISHES both values as <c>languages.discovery_limits</c> in
/// <c>languages --json</c>. Miller keeps mirroring them as constants — the decision runs on the watcher's hot
/// path, where a subprocess is not affordable — but the mirror is no longer taken on trust:
/// <c>JulieExtractLanguagesScaleTests</c> runs the pinned binary and fails when a published value and this
/// mirror disagree, so a pin bump that moves a limit fails the branch gate instead of drifting silently.</para>
/// </summary>
public static class ExtractSourceLimits
{
    /// <summary>Operator override for <see cref="DefaultMaxSourceFileBytes"/>.</summary>
    public const string MaxSourceFileBytesEnvVar = "MILLER_MAX_SOURCE_FILE_BYTES";

    /// <summary>julie-extract's <c>MAX_SOURCE_FILE_BYTES</c>: 1 MiB.</summary>
    public const long DefaultMaxSourceFileBytes = 1024 * 1024;

    private static readonly string[] GeneratedSuffixes =
    [
        ".min.js",
        ".bundle.js",
        ".generated.js",
        ".generated.jsx",
        ".generated.ts",
        ".generated.tsx",
        ".generated.d.ts",
    ];

    /// <summary>
    /// julie-extract's <c>HARD_EXCLUDE_SUFFIXES</c>, verbatim and in the same order. Exposed so the pinned
    /// binary's published <c>languages --json</c> <c>discovery_limits</c> can be compared against this mirror
    /// at the build gate, which is what turns a pin bump that moves the limits into a loud failure.
    /// </summary>
    public static IReadOnlyList<string> HardExcludeSuffixes => GeneratedSuffixes;

    /// <summary>Resolve the byte ceiling from the process environment.</summary>
    public static long MaxSourceFileBytesFromEnvironment() =>
        ParseMaxSourceFileBytes(Environment.GetEnvironmentVariable(MaxSourceFileBytesEnvVar));

    /// <summary>
    /// The pure env-value ⇒ ceiling mapping behind <see cref="MaxSourceFileBytesFromEnvironment"/>, testable
    /// without mutating the process environment. Blank, unparseable, and non-positive values fall back to
    /// <see cref="DefaultMaxSourceFileBytes"/> — a zero or negative ceiling would refuse every file, which is
    /// never what an operator meant. Any positive value is honored verbatim, including one above the default,
    /// because the variable exists to track a julie-extract limit Miller's build may not yet mirror.
    /// </summary>
    public static long ParseMaxSourceFileBytes(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return DefaultMaxSourceFileBytes;

        return long.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
               && parsed > 0
            ? parsed
            : DefaultMaxSourceFileBytes;
    }

    /// <summary>True when a file of <paramref name="lengthBytes"/> exceeds <paramref name="maxBytes"/>.</summary>
    public static bool IsOversized(long lengthBytes, long maxBytes) => lengthBytes > maxBytes;

    /// <summary>
    /// True when <paramref name="path"/> ends with one of julie-extract's hard-excluded generated suffixes.
    /// Matched on the whole path the way julie matches its root-relative path, so a directory component can
    /// never fake a suffix match.
    /// </summary>
    public static bool HasGeneratedSuffix(string path, StringComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(path);
        foreach (string suffix in GeneratedSuffixes)
        {
            if (path.EndsWith(suffix, comparison))
                return true;
        }

        return false;
    }
}
