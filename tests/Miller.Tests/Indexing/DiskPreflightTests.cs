using Miller.Indexing.Semantic;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class DiskPreflightTests
{
    private const string Path = "/ws/.miller";

    [Fact]
    public void Check_FreeAboveRequired_IsOk()
    {
        DiskPreflightVerdict verdict = Preflight(free: 1000).Check(Path, requiredBytes: 400);

        Assert.True(verdict.Ok);
        Assert.Equal(1000, verdict.FreeBytes);
        Assert.Equal(400, verdict.RequiredBytes);
    }

    [Fact]
    public void Check_FreeExactlyRequired_IsOkAtTheBoundary()
    {
        Assert.True(Preflight(free: 400).Check(Path, requiredBytes: 400).Ok);
    }

    [Fact]
    public void Check_FreeOneByteBelowRequired_IsBlocked()
    {
        DiskPreflightVerdict verdict = Preflight(free: 399).Check(Path, requiredBytes: 400);

        Assert.False(verdict.Ok);
        Assert.Equal(399, verdict.FreeBytes);
        Assert.Equal(400, verdict.RequiredBytes);
    }

    [Fact]
    public void Check_UnknownFreeSpace_NeverBlocksAConsentedBuild()
    {
        Assert.True(Preflight(free: -1).Check(Path, requiredBytes: long.MaxValue).Ok);
    }

    [Fact]
    public void Check_PassesTheProbedPathThrough()
    {
        string? probed = null;
        var preflight = new DiskPreflight(path =>
        {
            probed = path;
            return 10;
        });

        preflight.Check(Path, requiredBytes: 5);

        Assert.Equal(Path, probed);
    }

    [Fact]
    public void EstimateRequiredBytes_BelowTheFloor_IsClampedToTheFloor()
    {
        long required = DiskPreflight.EstimateRequiredBytes(workUnits: 1, currentArtifactBytes: 4096, currentStoredUnits: 4096);

        Assert.Equal(DiskPreflight.MinimumRequiredBytes, required);
    }

    [Fact]
    public void EstimateRequiredBytes_ScalesByObservedBytesPerUnitAboveTheFloor()
    {
        const int stored = 1000;
        const long artifact = 2_000_000_000L;
        const int work = 900;

        long required = DiskPreflight.EstimateRequiredBytes(work, artifact, stored);

        Assert.Equal(work * (artifact / stored), required);
        Assert.True(required > DiskPreflight.MinimumRequiredBytes);
    }

    [Fact]
    public void EstimateRequiredBytes_NoStoredCorpusToObserve_FallsBackToAConservativePerUnitAndFloor()
    {
        long required = DiskPreflight.EstimateRequiredBytes(workUnits: 5, currentArtifactBytes: 0, currentStoredUnits: 0);

        Assert.Equal(DiskPreflight.MinimumRequiredBytes, required);
    }

    [Fact]
    public void EstimateRequiredBytes_LargeInitialBuildWithoutAnArtifact_UsesFallbackPerUnit()
    {
        long required = DiskPreflight.EstimateRequiredBytes(
            workUnits: 10_000_000, currentArtifactBytes: 0, currentStoredUnits: 0);

        Assert.True(required > DiskPreflight.MinimumRequiredBytes);
    }

    [Fact]
    public void Verdict_Reason_NamesBothFreeAndRequiredBytes()
    {
        string reason = new DiskPreflightVerdict(false, FreeBytes: 399, RequiredBytes: 400).Reason;

        Assert.Contains("399", reason, StringComparison.Ordinal);
        Assert.Contains("400", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultProbe_OnADeepExistingDirectory_ReturnsNonNegativeFreeSpace()
    {
        string deep = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "miller-preflight-probe", "a", "b");
        Directory.CreateDirectory(deep);

        DiskPreflightVerdict verdict = new DiskPreflight().Check(deep, requiredBytes: 1);

        Assert.True(verdict.FreeBytes >= 0);
    }

    [Fact]
    public void DefaultProbe_OnAMissingDeepPath_WalksUpToAnExistingAncestor()
    {
        string missing = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "miller-preflight-probe", "does-not-exist", "x", "y");

        DiskPreflightVerdict verdict = new DiskPreflight().Check(missing, requiredBytes: 1);

        Assert.True(verdict.FreeBytes >= 0);
    }

    private static DiskPreflight Preflight(long free) => new(_ => free);
}
