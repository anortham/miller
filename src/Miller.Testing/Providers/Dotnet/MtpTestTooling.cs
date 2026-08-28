using System.Text.RegularExpressions;

namespace Miller.Testing;

internal readonly record struct MtpVersion(int Major, int Minor, int Patch) : IComparable<MtpVersion>
{
    internal static MtpVersion MinimumSupported => new(1, 7, 0);

    internal static MtpVersion JsonListAndReportMinimum => new(2, 3, 0);

    internal bool SupportsJsonListAndReports => CompareTo(JsonListAndReportMinimum) >= 0;

    public int CompareTo(MtpVersion other)
    {
        int major = Major.CompareTo(other.Major);
        if (major != 0)
            return major;

        int minor = Minor.CompareTo(other.Minor);
        return minor != 0 ? minor : Patch.CompareTo(other.Patch);
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}

internal sealed record MtpTestToolingInfo(
    MtpVersion Version,
    bool HasTrxReportExtension,
    string RawInfo);

internal static partial class MtpTestTooling
{
    private const int MaxInfoCharacters = 64 * 1024;

    internal static bool TryParseInfo(
        string output,
        bool truncated,
        out MtpTestToolingInfo? info,
        out string? diagnostic)
    {
        info = null;
        diagnostic = null;
        if (truncated || output.Length > MaxInfoCharacters)
        {
            diagnostic = "MTP --info output was truncated or exceeded the capture bound.";
            return false;
        }

        Match match = VersionPattern().Match(output);
        if (!match.Success
            || !int.TryParse(match.Groups["major"].Value, out int major)
            || !int.TryParse(match.Groups["minor"].Value, out int minor)
            || !int.TryParse(match.Groups["patch"].Value, out int patch))
        {
            diagnostic = "MTP --info output did not contain a complete Microsoft.Testing.Platform version.";
            return false;
        }

        int versionEnd = match.Index + match.Length;
        if (versionEnd < output.Length && output[versionEnd] == '-')
        {
            diagnostic = "MTP --info reported an unproven prerelease Microsoft.Testing.Platform version.";
            return false;
        }

        var version = new MtpVersion(major, minor, patch);
        if (version.CompareTo(MtpVersion.MinimumSupported) < 0)
        {
            diagnostic = $"Microsoft.Testing.Platform {version} is unsupported; MTP 1.7.0 or newer is required.";
            return false;
        }

        info = new MtpTestToolingInfo(
            version,
            HasTrxReportExtension(output),
            output);
        return true;
    }

    internal static bool TryParseVersion(
        string output,
        bool truncated,
        out MtpVersion version,
        out string? diagnostic)
    {
        if (TryParseInfo(output, truncated, out MtpTestToolingInfo? info, out diagnostic))
        {
            version = info!.Version;
            return true;
        }

        version = default;
        return false;
    }

    internal static IReadOnlyList<string> BuildListArguments(MtpVersion version)
    {
        EnsureSupported(version);
        return version.SupportsJsonListAndReports
            ? ["--no-banner", "--list-tests", "json"]
            : ["--no-banner", "--list-tests"];
    }

    internal static IReadOnlyList<string> BuildRunArguments(
        MtpVersion version,
        string resultArtifactPath,
        string? filter,
        bool wholeSuite,
        bool hasTrxReportExtension = true)
    {
        EnsureSupported(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(resultArtifactPath);
        if (Path.GetExtension(resultArtifactPath) != ".trx")
            throw new ArgumentException("MTP result artifacts must use the .trx extension.", nameof(resultArtifactPath));
        if (!hasTrxReportExtension)
            throw new ArgumentException(
                "The MTP TRX report extension was not proven by --info output.",
                nameof(hasTrxReportExtension));
        if (!wholeSuite && string.IsNullOrWhiteSpace(filter))
            throw new ArgumentException("A selected MTP run requires a framework filter.", nameof(filter));

        string? resultsDirectory = Path.GetDirectoryName(resultArtifactPath);
        if (string.IsNullOrWhiteSpace(resultsDirectory))
            throw new ArgumentException("MTP result artifact must have a results directory.", nameof(resultArtifactPath));

        var arguments = new List<string>
        {
            "--no-banner",
            "--results-directory",
            resultsDirectory,
            "--report-trx",
            "--report-trx-filename",
            Path.GetFileName(resultArtifactPath),
        };
        if (!wholeSuite)
        {
            arguments.Add("--filter");
            arguments.Add(filter!);
        }

        return arguments;
    }

    internal static bool HasFrameworkFilter(string info, string framework)
    {
        if (string.IsNullOrWhiteSpace(info))
            return false;
        if (!framework.Equals("mstest", StringComparison.OrdinalIgnoreCase)
            && !framework.Equals("nunit", StringComparison.OrdinalIgnoreCase)
            && !framework.Equals("xunit", StringComparison.OrdinalIgnoreCase))
            return false;

        return FilterOptionPattern().IsMatch(info);
    }

    internal static bool HasTrxReportExtension(string info) =>
        ReportTrxOptionPattern().IsMatch(info)
        || info.Contains("Microsoft.Testing.Extensions.TrxReport", StringComparison.OrdinalIgnoreCase);

    private static void EnsureSupported(MtpVersion version)
    {
        if (version.CompareTo(MtpVersion.MinimumSupported) < 0)
            throw new ArgumentException(
                $"Microsoft.Testing.Platform {version} is unsupported; MTP 1.7.0 or newer is required.",
                nameof(version));
    }

    [GeneratedRegex(
        @"(?im)(?:Microsoft[. ]Testing[. ]Platform|MTP)(?:[^0-9]{0,128})(?<major>[0-9]+)\.(?<minor>[0-9]+)\.(?<patch>[0-9]+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"(?im)^\s*--filter(?:\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex FilterOptionPattern();

    [GeneratedRegex(@"(?im)^\s*--report-trx(?:\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex ReportTrxOptionPattern();
}
