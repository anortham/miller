using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class JulieExtractVersionProbeTests
{
    [Theory]
    [InlineData("julie-extract 2.1.3", "2.1.3")]
    [InlineData("julie-extract 2.2.1\n", "2.2.1")]
    [InlineData("2.2.1", "2.2.1")]
    [InlineData("julie-extract v2.2.1 (build abc)", "2.2.1")]
    public void ParseVersion_ExtractsSemverToken(string output, string expected) =>
        Assert.Equal(expected, JulieExtractVersionProbe.ParseVersion(output));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no version here")]
    public void ParseVersion_NoSemver_ReturnsNull(string? output) =>
        Assert.Null(JulieExtractVersionProbe.ParseVersion(output));

    [Fact]
    public void StaleBinaryWarning_OlderBundled_NamesBothVersionsAndPointsAtRestore()
    {
        string? warning = JulieExtractVersionProbe.StaleBinaryWarning(bundledVersion: "2.1.3", pinnedVersion: "2.2.1");

        Assert.NotNull(warning);
        Assert.Contains("2.1.3", warning);
        Assert.Contains("2.2.1", warning);
        Assert.Contains("older", warning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("restore", warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StaleBinaryWarning_MatchingVersion_ReturnsNull() =>
        Assert.Null(JulieExtractVersionProbe.StaleBinaryWarning("2.2.1", "2.2.1"));

    [Fact]
    public void StaleBinaryWarning_NewerBundled_ReturnsNull_ForwardCompatIsTheGatesJob() =>
        // A newer binary keeping the same contract must still work (product version != schema/contract). A version
        // probe must never warn (or fail) on newer — the schema/contract gate is the compatibility authority.
        Assert.Null(JulieExtractVersionProbe.StaleBinaryWarning("2.3.0", "2.2.1"));

    [Theory]
    [InlineData(null)]
    [InlineData("garbage")]
    public void StaleBinaryWarning_UnparseableBundled_ReturnsNull(string? bundled) =>
        Assert.Null(JulieExtractVersionProbe.StaleBinaryWarning(bundled, "2.2.1"));

    [Fact]
    public void StaleBinaryWarning_DefaultPin_ComparesAgainstPinnedContractVersion()
    {
        // The single-arg overload (the startup form) must compare against the build's pinned version.
        Assert.Null(JulieExtractVersionProbe.StaleBinaryWarning(MillerExtractContract.PinnedJulieExtractVersion));
        Assert.NotNull(JulieExtractVersionProbe.StaleBinaryWarning("0.0.1"));
    }
}
