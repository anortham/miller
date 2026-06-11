using System.Globalization;
using System.Text.RegularExpressions;

namespace Miller.Indexing;

/// <summary>
/// The eligibility verdict for indexer leadership (version-aware leadership D2). <see cref="Eligible"/>
/// gates the claim attempt; <see cref="ArtifactOlderThanOwn"/> is the auto-upgrade-rescan signal — true
/// exactly when BOTH versions parse and the artifact's <c>binary_version</c> is older than this instance's
/// extractor. <see cref="Reason"/> is user-facing (rendered in <c>workspace status</c>/<c>health</c>).
/// </summary>
public sealed record LeadershipVerdict(bool Eligible, bool ArtifactOlderThanOwn, string Reason);

/// <summary>
/// Pure decision core for version-aware indexer leadership: may THIS instance (with its bundled
/// <c>julie-extract</c> version) claim the index-writer lease over an artifact stamped with
/// <c>artifact_metadata.binary_version</c>? Invariant protected: the artifact's <c>binary_version</c> never
/// goes backwards — an older extractor never claims leadership (it becomes a permanent reader), while
/// missing/unparseable ARTIFACT versions stay eligible (a downgrade cannot be proven) and a
/// missing/unparseable OWN version is ineligible (it cannot index anyway). The explicit
/// <paramref name="allowDowngrade"/> escape hatch (<c>MILLER_ALLOW_EXTRACTOR_DOWNGRADE=1</c>) makes the
/// instance eligible regardless. Comparison is numeric over <c>major.minor.patch</c>; prerelease/build
/// suffixes are ignored (same lenient first-token normalization as
/// <see cref="JulieExtractVersionProbe.ParseVersion"/>). No I/O — consumed by the IndexerService claim
/// loop, the CLI gate, and health rendering.
/// </summary>
public static class LeadershipEligibility
{
    // Same shape as JulieExtractVersionProbe.Semver, with capture groups for numeric comparison. First match
    // wins, so a full "julie-extract 2.3.0" --version line normalizes the same way the probe does.
    private static readonly Regex Semver = new(@"(\d+)\.(\d+)\.(\d+)", RegexOptions.Compiled);

    /// <summary>
    /// Evaluate the D2 verdict matrix for an instance whose extractor reports
    /// <paramref name="ownExtractorVersion"/> against an artifact stamped
    /// <paramref name="artifactBinaryVersion"/> (null/empty when no artifact exists yet or it predates the
    /// <c>binary_version</c> contract).
    /// </summary>
    public static LeadershipVerdict Evaluate(
        string? ownExtractorVersion,
        string? artifactBinaryVersion,
        bool allowDowngrade)
    {
        (long, long, long)? own = TryParseTriple(ownExtractorVersion);
        (long, long, long)? artifact = TryParseTriple(artifactBinaryVersion);
        bool artifactOlder = own is { } o && artifact is { } a && Compare(a, o) < 0;

        if (allowDowngrade)
        {
            return new LeadershipVerdict(
                Eligible: true,
                ArtifactOlderThanOwn: artifactOlder,
                Reason: "extractor downgrade override is active; this instance may index regardless of version");
        }

        if (own is not { } ownVersion)
        {
            string detail = string.IsNullOrWhiteSpace(ownExtractorVersion)
                ? "extractor version is unknown"
                : $"extractor version '{ownExtractorVersion}' is unparseable";
            return new LeadershipVerdict(
                Eligible: false,
                ArtifactOlderThanOwn: false,
                Reason: detail + "; this instance cannot index and serves reads only");
        }

        string ownText = Render(ownVersion);

        if (string.IsNullOrWhiteSpace(artifactBinaryVersion))
        {
            return new LeadershipVerdict(
                Eligible: true,
                ArtifactOlderThanOwn: false,
                Reason: $"no index artifact version recorded; extractor {ownText} may index");
        }

        if (artifact is not { } artifactVersion)
        {
            return new LeadershipVerdict(
                Eligible: true,
                ArtifactOlderThanOwn: false,
                Reason: $"index artifact version '{artifactBinaryVersion}' is unparseable; " +
                        $"extractor {ownText} may index");
        }

        string artifactText = Render(artifactVersion);
        int comparison = Compare(ownVersion, artifactVersion);
        if (comparison < 0)
        {
            return new LeadershipVerdict(
                Eligible: false,
                ArtifactOlderThanOwn: false,
                Reason: $"extractor {ownText} is older than the index artifact {artifactText}; " +
                        "this instance serves reads only");
        }

        return new LeadershipVerdict(
            Eligible: true,
            ArtifactOlderThanOwn: artifactOlder,
            Reason: comparison == 0
                ? $"extractor {ownText} matches the index artifact {artifactText}"
                : $"extractor {ownText} is newer than the index artifact {artifactText}");
    }

    /// <summary>
    /// Numeric <c>major.minor.patch</c> comparison ("2.10.0" &gt; "2.9.9"); prerelease/build suffixes are
    /// ignored. Throws <see cref="ArgumentException"/> when either input carries no <c>X.Y.Z</c> token —
    /// callers needing tolerance go through <see cref="Evaluate"/>, which never throws.
    /// </summary>
    public static int CompareVersions(string a, string b)
    {
        (long, long, long) pa = TryParseTriple(a)
            ?? throw new ArgumentException($"'{a}' contains no major.minor.patch version token.", nameof(a));
        (long, long, long) pb = TryParseTriple(b)
            ?? throw new ArgumentException($"'{b}' contains no major.minor.patch version token.", nameof(b));
        return Compare(pa, pb);
    }

    private static (long Major, long Minor, long Patch)? TryParseTriple(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return null;
        Match match = Semver.Match(version);
        if (!match.Success
            || !long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long major)
            || !long.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long minor)
            || !long.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long patch))
        {
            return null;
        }
        return (major, minor, patch);
    }

    private static int Compare((long Major, long Minor, long Patch) a, (long Major, long Minor, long Patch) b)
    {
        int major = a.Major.CompareTo(b.Major);
        if (major != 0)
            return major;
        int minor = a.Minor.CompareTo(b.Minor);
        return minor != 0 ? minor : a.Patch.CompareTo(b.Patch);
    }

    private static string Render((long Major, long Minor, long Patch) v) =>
        string.Create(CultureInfo.InvariantCulture, $"{v.Major}.{v.Minor}.{v.Patch}");
}
