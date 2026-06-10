using System.Text.RegularExpressions;

namespace Miller.Indexing;

/// <summary>
/// Pure, diagnostic-only comparison of the bundled <c>julie-extract</c>'s reported version against the pinned
/// version. The schema/contract gates (<see cref="ExtractVersionMismatch"/>, <see cref="JulieSchemaGate"/>) remain
/// the SOLE compatibility authority — this never decides pass/fail. Its only job is to turn the most common
/// operator mistake — a <c>julie-extract</c> left in <c>.tools/</c> that predates a pin bump (so it is PRESENT and
/// the "missing binary" hint misleads) — into an explicit "older than the pin, re-run restore" warning at startup.
/// It is deliberately silent for a matching, NEWER (product version and schema/contract are orthogonal — a newer
/// binary keeping the contract must still work), or unparseable version: warning there would be noise or a false
/// alarm. Parsing is lenient — <c>julie-extract</c> prints <c>julie-extract X.Y.Z</c>.
/// </summary>
public static class JulieExtractVersionProbe
{
    private static readonly Regex Semver = new(@"\d+\.\d+\.\d+", RegexOptions.Compiled);

    /// <summary>Extract the first <c>X.Y.Z</c> token from a <c>julie-extract --version</c> line, or null if none.</summary>
    public static string? ParseVersion(string? versionOutput)
    {
        if (string.IsNullOrWhiteSpace(versionOutput))
            return null;
        Match match = Semver.Match(versionOutput);
        return match.Success ? match.Value : null;
    }

    /// <summary>
    /// <see cref="StaleBinaryWarning(string?, string)"/> against this build's pinned julie-extract version
    /// (<see cref="MillerExtractContract.PinnedJulieExtractVersion"/>) — the form startup callers use, so they need
    /// no reference to the internal contract.
    /// </summary>
    public static string? StaleBinaryWarning(string? bundledVersion) =>
        StaleBinaryWarning(bundledVersion, MillerExtractContract.PinnedJulieExtractVersion);

    /// <summary>
    /// A startup warning when the bundled binary is strictly OLDER than the pin (the stale-but-present case), else
    /// null. Null covers a matching version (healthy), a newer version (forward-compat is the gates' call), and an
    /// unparseable/absent version (a probe quirk must not raise a false alarm).
    /// </summary>
    public static string? StaleBinaryWarning(string? bundledVersion, string pinnedVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pinnedVersion);

        if (!Version.TryParse(bundledVersion, out Version? bundled) || bundled is null)
            return null;
        if (!Version.TryParse(pinnedVersion, out Version? pinned) || pinned is null)
            return null;
        if (bundled.CompareTo(pinned) >= 0)
            return null;

        return $"Bundled julie-extract is v{bundledVersion}, older than the pinned v{pinnedVersion}. A pre-v{pinnedVersion} " +
               "binary emits an older schema/contract this Miller build rejects, so indexing will keep failing with a " +
               "schema mismatch (the binary is present, so the \"missing binary\" hint does not apply). Re-run " +
               "scripts/restore-julie-extract.sh (or restore-julie-extract.ps1 on Windows) to update .tools/julie-extract, " +
               "then rebuild with `workspace full`.";
    }
}
