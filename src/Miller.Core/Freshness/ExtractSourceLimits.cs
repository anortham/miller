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
/// </summary>
public static class ExtractSourceLimits
{
    /// <summary>Operator override for <see cref="DefaultMaxSourceFileBytes"/>.</summary>
    public const string MaxSourceFileBytesEnvVar = "MILLER_MAX_SOURCE_FILE_BYTES";

    /// <summary>julie-extract's <c>MAX_SOURCE_FILE_BYTES</c>: 1 MiB.</summary>
    public const long DefaultMaxSourceFileBytes = 1024 * 1024;

    // julie-extract's HARD_EXCLUDE_SUFFIXES, verbatim and in the same order.
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
