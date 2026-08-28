using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Providers.Dotnet;

public sealed class MtpTestToolingTests
{
    [Theory]
    [InlineData("Microsoft.Testing.Platform 1.7.0", true)]
    [InlineData("Microsoft.Testing.Platform: 2.3.0", true)]
    [InlineData("Microsoft.Testing.Platform Version: 1.6.9", false)]
    [InlineData("Microsoft.Testing.Platform Version: 2.3.0-preview.1", false)]
    [InlineData("Microsoft.Testing.Platform Version: unknown", false)]
    public void ParseInfo_requires_a_supported_complete_platform_version(string output, bool expected)
    {
        bool parsed = MtpTestTooling.TryParseInfo(output, false, out var info, out _);

        Assert.Equal(expected, parsed);
        if (expected)
            Assert.NotNull(info);
    }

    [Fact]
    public void ParseInfo_rejects_truncated_output_and_missing_platform_evidence()
    {
        Assert.False(MtpTestTooling.TryParseInfo(
            "Microsoft.Testing.Platform 2.3.0\n--filter",
            true,
            out _,
            out string? truncated));
        Assert.Contains("truncated", truncated, StringComparison.OrdinalIgnoreCase);

        Assert.False(MtpTestTooling.TryParseInfo(
            ".NET test application\nVersion: 2.3.0",
            false,
            out _,
            out string? missing));
        Assert.Contains("version", missing, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildListArguments_selects_text_before_json_support()
    {
        Assert.Equal(
            ["--no-banner", "--list-tests"],
            MtpTestTooling.BuildListArguments(new MtpVersion(1, 7, 0)));
        Assert.Equal(
            ["--no-banner", "--list-tests", "json"],
            MtpTestTooling.BuildListArguments(new MtpVersion(2, 3, 0)));
    }

    [Fact]
    public void BuildRunArguments_places_framework_filter_in_app_arguments_and_keeps_results_inside_generation()
    {
        const string resultArtifactPath = "/tmp/generation/results/run.trx";
        var arguments = MtpTestTooling.BuildRunArguments(
            new MtpVersion(2, 3, 0),
            resultArtifactPath,
            "FullyQualifiedName=Sample.Tests.UnitTests.Adds",
            wholeSuite: false);

        Assert.Equal(
            [
                "--no-banner",
                "--results-directory",
                Path.GetDirectoryName(resultArtifactPath)!,
                "--report-trx",
                "--report-trx-filename",
                Path.GetFileName(resultArtifactPath),
                "--filter",
                "FullyQualifiedName=Sample.Tests.UnitTests.Adds",
            ],
            arguments);
    }

    [Fact]
    public void BuildRunArguments_refuses_report_without_extension_evidence()
    {
        Assert.Throws<ArgumentException>(() => MtpTestTooling.BuildRunArguments(
            new MtpVersion(2, 3, 0),
            "/tmp/generation/results/run.trx",
            null,
            wholeSuite: true,
            hasTrxReportExtension: false));
    }

    [Theory]
    [InlineData("MSTest\n--filter\n--list-tests", "mstest", true)]
    [InlineData("NUnit\n--filter\n--list-tests", "nunit", true)]
    [InlineData("MSTest\n--list-tests", "mstest", false)]
    [InlineData("NUnit\n--list-tests", "nunit", false)]
    public void HasFrameworkFilter_requires_proven_filter_capability(
        string info,
        string framework,
        bool expected)
    {
        Assert.Equal(expected, MtpTestTooling.HasFrameworkFilter(info, framework));
    }

    [Fact]
    public void Capability_detection_requires_option_boundaries()
    {
        Assert.False(MtpTestTooling.HasFrameworkFilter("--filter-uid", "mstest"));
        Assert.False(MtpTestTooling.HasTrxReportExtension("--report-trx-filename run.trx"));
        Assert.True(MtpTestTooling.HasTrxReportExtension("--report-trx\n"));
    }
}
