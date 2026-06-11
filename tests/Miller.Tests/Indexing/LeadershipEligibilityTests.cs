using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins the pure leadership-eligibility verdict matrix (version-aware leadership D2): an instance whose
/// bundled extractor is older than the artifact's <c>binary_version</c> never claims leadership, while
/// missing/unparseable artifact versions stay eligible (cannot prove a downgrade) and a missing/unparseable
/// own version is ineligible (cannot index anyway). <c>ArtifactOlderThanOwn</c> is the auto-upgrade-rescan
/// signal: true exactly when both versions parse and artifact &lt; own. Fast suite (pure logic).
/// </summary>
public sealed class LeadershipEligibilityTests
{
    // ---- verdict matrix ----

    [Fact]
    public void Evaluate_OwnNewerThanArtifact_EligibleAndArtifactOlder()
    {
        var verdict = LeadershipEligibility.Evaluate("2.3.0", "2.1.0", allowDowngrade: false);

        Assert.True(verdict.Eligible);
        Assert.True(verdict.ArtifactOlderThanOwn);
        Assert.Equal("extractor 2.3.0 is newer than the index artifact 2.1.0", verdict.Reason);
    }

    [Fact]
    public void Evaluate_EqualVersions_EligibleAndNotOlder()
    {
        var verdict = LeadershipEligibility.Evaluate("2.3.0", "2.3.0", allowDowngrade: false);

        Assert.True(verdict.Eligible);
        Assert.False(verdict.ArtifactOlderThanOwn);
        Assert.Equal("extractor 2.3.0 matches the index artifact 2.3.0", verdict.Reason);
    }

    [Fact]
    public void Evaluate_OwnOlderThanArtifact_Ineligible()
    {
        var verdict = LeadershipEligibility.Evaluate("2.1.3", "2.3.0", allowDowngrade: false);

        Assert.False(verdict.Eligible);
        Assert.False(verdict.ArtifactOlderThanOwn);
        Assert.Equal(
            "extractor 2.1.3 is older than the index artifact 2.3.0; this instance serves reads only",
            verdict.Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Evaluate_NoArtifactVersion_Eligible(string? artifactVersion)
    {
        var verdict = LeadershipEligibility.Evaluate("2.3.0", artifactVersion, allowDowngrade: false);

        Assert.True(verdict.Eligible);
        Assert.False(verdict.ArtifactOlderThanOwn);
        Assert.Equal("no index artifact version recorded; extractor 2.3.0 may index", verdict.Reason);
    }

    [Fact]
    public void Evaluate_UnparseableArtifactVersion_Eligible()
    {
        var verdict = LeadershipEligibility.Evaluate("2.3.0", "not-a-version", allowDowngrade: false);

        Assert.True(verdict.Eligible);
        Assert.False(verdict.ArtifactOlderThanOwn);
        Assert.Equal(
            "index artifact version 'not-a-version' is unparseable; extractor 2.3.0 may index",
            verdict.Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Evaluate_MissingOwnVersion_Ineligible(string? ownVersion)
    {
        var verdict = LeadershipEligibility.Evaluate(ownVersion, "2.3.0", allowDowngrade: false);

        Assert.False(verdict.Eligible);
        Assert.False(verdict.ArtifactOlderThanOwn);
        Assert.Equal(
            "extractor version is unknown; this instance cannot index and serves reads only",
            verdict.Reason);
    }

    [Fact]
    public void Evaluate_UnparseableOwnVersion_Ineligible()
    {
        var verdict = LeadershipEligibility.Evaluate("garbage", "2.3.0", allowDowngrade: false);

        Assert.False(verdict.Eligible);
        Assert.False(verdict.ArtifactOlderThanOwn);
        Assert.Equal(
            "extractor version 'garbage' is unparseable; this instance cannot index and serves reads only",
            verdict.Reason);
    }

    // ---- allowDowngrade escape hatch ----

    [Fact]
    public void Evaluate_AllowDowngrade_OverridesOlderOwnVersion()
    {
        var verdict = LeadershipEligibility.Evaluate("2.1.3", "2.3.0", allowDowngrade: true);

        Assert.True(verdict.Eligible);
        Assert.False(verdict.ArtifactOlderThanOwn);
        Assert.Contains("downgrade override", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_AllowDowngrade_OverridesMissingOwnVersion()
    {
        var verdict = LeadershipEligibility.Evaluate(null, "2.3.0", allowDowngrade: true);

        Assert.True(verdict.Eligible);
        Assert.False(verdict.ArtifactOlderThanOwn);
    }

    [Fact]
    public void Evaluate_AllowDowngrade_StillReportsArtifactOlderWhenItIs()
    {
        // The override changes eligibility, never the auto-upgrade-rescan signal.
        var verdict = LeadershipEligibility.Evaluate("2.3.0", "2.1.0", allowDowngrade: true);

        Assert.True(verdict.Eligible);
        Assert.True(verdict.ArtifactOlderThanOwn);
    }

    // ---- version-string tolerance (probe-style normalization) ----

    [Fact]
    public void Evaluate_PrereleaseSuffixesIgnored_EqualCoreVersionsAreEligible()
    {
        var verdict = LeadershipEligibility.Evaluate("2.3.0-beta.1", "2.3.0", allowDowngrade: false);

        Assert.True(verdict.Eligible);
        Assert.False(verdict.ArtifactOlderThanOwn);
    }

    [Fact]
    public void Evaluate_ToolPrefixedVersionLine_ParsesLikeTheProbe()
    {
        var verdict = LeadershipEligibility.Evaluate("julie-extract 2.3.0", "2.1.0", allowDowngrade: false);

        Assert.True(verdict.Eligible);
        Assert.True(verdict.ArtifactOlderThanOwn);
        Assert.Equal("extractor 2.3.0 is newer than the index artifact 2.1.0", verdict.Reason);
    }

    // ---- CompareVersions ----

    [Fact]
    public void CompareVersions_IsNumericNotLexicographic()
    {
        Assert.True(LeadershipEligibility.CompareVersions("2.10.0", "2.9.9") > 0);
    }

    [Theory]
    [InlineData("2.3.0", "2.3.0", 0)]
    [InlineData("2.3.0", "2.3.1", -1)]
    [InlineData("3.0.0", "2.9.9", 1)]
    [InlineData("2.3.0-beta.1", "2.3.0+build7", 0)] // prerelease/build suffixes ignored
    public void CompareVersions_ComparesMajorMinorPatch(string a, string b, int expectedSign)
    {
        Assert.Equal(expectedSign, Math.Sign(LeadershipEligibility.CompareVersions(a, b)));
    }

    [Fact]
    public void CompareVersions_UnparseableInput_Throws()
    {
        Assert.Throws<ArgumentException>(() => LeadershipEligibility.CompareVersions("nope", "2.3.0"));
        Assert.Throws<ArgumentException>(() => LeadershipEligibility.CompareVersions("2.3.0", "nope"));
    }
}
