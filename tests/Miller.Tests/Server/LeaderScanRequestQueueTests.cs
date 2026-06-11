using Miller.Server.Workspaces;
using Xunit;

namespace Miller.Tests.Server;

public sealed class LeaderScanRequestQueueTests : IDisposable
{
    private readonly string _dir;

    public LeaderScanRequestQueueTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-leader-scan-requests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>Re-stamp the single pending request of <paramref name="suffix"/> kind to look older than the TTL
    /// (the drain reads age from the leading file-name stamp, which the writer formats as yyyyMMddHHmmssfffffff).</summary>
    private static string AgeSingleRequestBeyondTtl(string requestDir, string suffix)
    {
        string path = Directory.EnumerateFiles(requestDir, "*" + suffix).Single();
        string name = Path.GetFileName(path);
        string oldStamp = DateTimeOffset.UtcNow
            .Subtract(LeaderScanRequestQueue.RequestTtl + TimeSpan.FromMinutes(1))
            .ToString("yyyyMMddHHmmssfffffff", System.Globalization.CultureInfo.InvariantCulture);
        string agedPath = Path.Combine(requestDir, oldStamp + name[name.IndexOf('-')..]);
        File.Move(path, agedPath);
        return agedPath;
    }

    [Fact]
    public void RequestFullScan_WritesUniqueRequestThatLeaderDrainConsumesOnce()
    {
        string millerDir = Path.Combine(_dir, ".miller");

        LeaderScanRequestQueue.RequestFullScan(millerDir, "workspace-1", baselineRevision: 7);

        string requestDir = Path.Combine(millerDir, "requests");
        Assert.Single(Directory.EnumerateFiles(requestDir, "*.full-scan.json"));
        Assert.True(LeaderScanRequestQueue.DrainFullScanRequests(millerDir).Requested);
        Assert.Empty(Directory.EnumerateFiles(requestDir, "*.full-scan.json"));
        Assert.False(LeaderScanRequestQueue.DrainFullScanRequests(millerDir).Requested);
    }

    [Fact]
    public void RequestFileConverge_RoundTripsPaths_AndDrainConsumesOnce()
    {
        string millerDir = Path.Combine(_dir, ".miller");

        LeaderScanRequestQueue.RequestFileConverge(
            millerDir, "workspace-1", new[] { "/abs/repo/a.cs", "/abs/repo/b.cs" });

        string requestDir = Path.Combine(millerDir, "requests");
        Assert.Single(Directory.EnumerateFiles(requestDir, "*.file-converge.json"));
        Assert.Equal(
            new[] { "/abs/repo/a.cs", "/abs/repo/b.cs" },
            LeaderScanRequestQueue.DrainFileConvergeRequests(millerDir).Paths);
        Assert.Empty(Directory.EnumerateFiles(requestDir, "*.file-converge.json"));
        // The claim rename must not strand a serviced request as a *.claimed leftover either.
        Assert.Empty(Directory.EnumerateFiles(requestDir));
        Assert.Empty(LeaderScanRequestQueue.DrainFileConvergeRequests(millerDir).Paths);
    }

    [Fact]
    public void DrainFileConvergeRequests_DedupesAcrossRequests_AndDeletesMalformed()
    {
        string millerDir = Path.Combine(_dir, ".miller");
        LeaderScanRequestQueue.RequestFileConverge(millerDir, "workspace-1", new[] { "/abs/repo/a.cs" });
        LeaderScanRequestQueue.RequestFileConverge(
            millerDir, "workspace-1", new[] { "/abs/repo/a.cs", "/abs/repo/b.cs" });
        string requestDir = Path.Combine(millerDir, "requests");
        string malformed = Path.Combine(requestDir, "bad.file-converge.json");
        File.WriteAllText(malformed, "{ not-json");

        Assert.Equal(
            new[] { "/abs/repo/a.cs", "/abs/repo/b.cs" },
            LeaderScanRequestQueue.DrainFileConvergeRequests(millerDir).Paths);
        Assert.False(File.Exists(malformed));
    }

    [Fact]
    public void DrainFileConvergeRequests_NoRequestDirectory_ReturnsEmpty()
    {
        Assert.Empty(LeaderScanRequestQueue.DrainFileConvergeRequests(Path.Combine(_dir, ".miller")).Paths);
    }

    [Fact]
    public void FileConvergeAndFullScanRequests_DoNotConsumeEachOther()
    {
        string millerDir = Path.Combine(_dir, ".miller");
        LeaderScanRequestQueue.RequestFullScan(millerDir, "workspace-1", baselineRevision: 1);
        LeaderScanRequestQueue.RequestFileConverge(millerDir, "workspace-1", new[] { "/abs/repo/a.cs" });

        // Draining one kind leaves the other kind in place for its own consumer.
        Assert.Equal(new[] { "/abs/repo/a.cs" }, LeaderScanRequestQueue.DrainFileConvergeRequests(millerDir).Paths);
        Assert.True(LeaderScanRequestQueue.DrainFullScanRequests(millerDir).Requested);
    }

    [Fact]
    public void DrainFullScanRequests_DeletesMalformedRequestWithoutScanning()
    {
        string millerDir = Path.Combine(_dir, ".miller");
        string requestDir = Path.Combine(millerDir, "requests");
        Directory.CreateDirectory(requestDir);
        string malformed = Path.Combine(requestDir, "bad.full-scan.json");
        File.WriteAllText(malformed, "{ not-json");

        Assert.False(LeaderScanRequestQueue.DrainFullScanRequests(millerDir).Requested);
        Assert.False(File.Exists(malformed));
    }

    // ---- M2: TTL — an expired request is discarded WITHOUT being serviced -----------------------------------

    [Fact]
    public void DrainFileConvergeRequests_ExpiredRequest_IsDeletedWithoutServicing()
    {
        string millerDir = Path.Combine(_dir, ".miller");
        string requestDir = Path.Combine(millerDir, "requests");
        LeaderScanRequestQueue.RequestFileConverge(millerDir, "workspace-1", new[] { "/abs/repo/old.cs" });
        string agedPath = AgeSingleRequestBeyondTtl(requestDir, ".file-converge.json");

        FileConvergeDrainResult result = LeaderScanRequestQueue.DrainFileConvergeRequests(millerDir);

        Assert.Empty(result.Paths); // never serviced
        Assert.Equal(1, result.ExpiredDiscarded);
        Assert.False(File.Exists(agedPath)); // and deleted so it cannot accumulate forever
        Assert.Empty(Directory.EnumerateFiles(requestDir));
    }

    [Fact]
    public void DrainFullScanRequests_ExpiredRequest_IsDeletedWithoutScanning()
    {
        string millerDir = Path.Combine(_dir, ".miller");
        string requestDir = Path.Combine(millerDir, "requests");
        LeaderScanRequestQueue.RequestFullScan(millerDir, "workspace-1", baselineRevision: 3);
        string agedPath = AgeSingleRequestBeyondTtl(requestDir, ".full-scan.json");

        FullScanDrainResult result = LeaderScanRequestQueue.DrainFullScanRequests(millerDir);

        Assert.False(result.Requested);
        Assert.Equal(1, result.ExpiredDiscarded);
        Assert.False(File.Exists(agedPath));
    }

    [Fact]
    public void DrainFileConvergeRequests_FreshRequest_IsNotExpired()
    {
        string millerDir = Path.Combine(_dir, ".miller");
        LeaderScanRequestQueue.RequestFileConverge(millerDir, "workspace-1", new[] { "/abs/repo/a.cs" });

        FileConvergeDrainResult result = LeaderScanRequestQueue.DrainFileConvergeRequests(millerDir);

        Assert.Equal(new[] { "/abs/repo/a.cs" }, result.Paths);
        Assert.Equal(0, result.ExpiredDiscarded);
    }

    // ---- M4: claim-by-rename — an unclaimable request is skipped, not serviced in a tight loop --------------

    [Fact]
    public void DrainFileConvergeRequests_UnclaimableRequest_IsSkippedNotServiced()
    {
        string millerDir = Path.Combine(_dir, ".miller");
        string requestDir = Path.Combine(millerDir, "requests");
        LeaderScanRequestQueue.RequestFileConverge(millerDir, "workspace-1", new[] { "/abs/repo/a.cs" });
        string request = Directory.EnumerateFiles(requestDir, "*.file-converge.json").Single();
        // Occupy the claim slot so File.Move(request, request + ".claimed") fails with IOException.
        File.WriteAllText(request + ".claimed", "occupied");

        FileConvergeDrainResult first = LeaderScanRequestQueue.DrainFileConvergeRequests(millerDir);
        FileConvergeDrainResult second = LeaderScanRequestQueue.DrainFileConvergeRequests(millerDir);

        // Never serviced unclaimed (servicing would re-run the extract every 250ms tick forever) — skipped.
        Assert.Empty(first.Paths);
        Assert.Equal(1, first.ClaimSkipped);
        Assert.Empty(second.Paths);
        Assert.Equal(1, second.ClaimSkipped);
        Assert.True(File.Exists(request)); // left in place for a later tick / the TTL sweep
    }

    [Fact]
    public void DrainFullScanRequests_UnclaimableRequest_IsSkippedNotServiced()
    {
        string millerDir = Path.Combine(_dir, ".miller");
        string requestDir = Path.Combine(millerDir, "requests");
        LeaderScanRequestQueue.RequestFullScan(millerDir, "workspace-1", baselineRevision: 1);
        string request = Directory.EnumerateFiles(requestDir, "*.full-scan.json").Single();
        File.WriteAllText(request + ".claimed", "occupied");

        FullScanDrainResult result = LeaderScanRequestQueue.DrainFullScanRequests(millerDir);

        Assert.False(result.Requested);
        Assert.Equal(1, result.ClaimSkipped);
        Assert.True(File.Exists(request));
    }

    // ---- Yield (version-aware leadership D4): a newer-extractor reader asks the live leader to abdicate ------

    [Fact]
    public void RequestYield_RoundTripsVersionAndPid_AndDrainConsumesOnce()
    {
        string millerDir = Path.Combine(_dir, ".miller");

        LeaderScanRequestQueue.RequestYield(millerDir, "workspace-1", requesterPid: 4242, requesterExtractorVersion: "2.3.0");

        string requestDir = Path.Combine(millerDir, "requests");
        Assert.Single(Directory.EnumerateFiles(requestDir, "*.yield.json"));

        YieldDrainResult result = LeaderScanRequestQueue.DrainYieldRequests(millerDir);

        Assert.True(result.Requested);
        Assert.Equal("2.3.0", result.MaxRequesterVersion);
        Assert.Equal(4242, result.RequesterPid);
        Assert.Empty(Directory.EnumerateFiles(requestDir)); // serviced request removed, no .claimed leftover
        Assert.False(LeaderScanRequestQueue.DrainYieldRequests(millerDir).Requested);
    }

    [Fact]
    public void DrainYieldRequests_TwoVersions_ReportsNumericMaxNotLexicalMax()
    {
        string millerDir = Path.Combine(_dir, ".miller");
        // "2.10.1" is numerically newer than "2.3.0" but lexically SMALLER — pins the major.minor.patch compare.
        LeaderScanRequestQueue.RequestYield(millerDir, "workspace-1", requesterPid: 111, requesterExtractorVersion: "2.3.0");
        LeaderScanRequestQueue.RequestYield(millerDir, "workspace-1", requesterPid: 222, requesterExtractorVersion: "2.10.1");

        YieldDrainResult result = LeaderScanRequestQueue.DrainYieldRequests(millerDir);

        Assert.True(result.Requested);
        Assert.Equal("2.10.1", result.MaxRequesterVersion);
        Assert.Equal(222, result.RequesterPid);
    }

    [Fact]
    public void DrainYieldRequests_UncomparableVersion_IsDroppedLikeMalformedJson()
    {
        string millerDir = Path.Combine(_dir, ".miller");
        // A version with no major.minor.patch token cannot be meaningfully compared against the leader's own
        // version — surfacing it would hand the (Task 3) leader garbage to compare. Dropped, file consumed.
        LeaderScanRequestQueue.RequestYield(millerDir, "workspace-1", requesterPid: 4242, requesterExtractorVersion: "not-a-version");

        YieldDrainResult result = LeaderScanRequestQueue.DrainYieldRequests(millerDir);

        Assert.False(result.Requested);
        Assert.Null(result.MaxRequesterVersion);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(millerDir, "requests")));
    }

    [Fact]
    public void DrainYieldRequests_ExpiredRequest_IsDeletedWithoutServicing()
    {
        string millerDir = Path.Combine(_dir, ".miller");
        string requestDir = Path.Combine(millerDir, "requests");
        LeaderScanRequestQueue.RequestYield(millerDir, "workspace-1", requesterPid: 4242, requesterExtractorVersion: "2.3.0");
        string agedPath = AgeSingleRequestBeyondTtl(requestDir, ".yield.json");

        YieldDrainResult result = LeaderScanRequestQueue.DrainYieldRequests(millerDir);

        Assert.False(result.Requested); // never serviced
        Assert.Null(result.MaxRequesterVersion);
        Assert.Equal(1, result.ExpiredDiscarded);
        Assert.False(File.Exists(agedPath));
    }

    [Fact]
    public void DrainYieldRequests_UnclaimableRequest_IsSkippedNotServiced()
    {
        string millerDir = Path.Combine(_dir, ".miller");
        string requestDir = Path.Combine(millerDir, "requests");
        LeaderScanRequestQueue.RequestYield(millerDir, "workspace-1", requesterPid: 4242, requesterExtractorVersion: "2.3.0");
        string request = Directory.EnumerateFiles(requestDir, "*.yield.json").Single();
        // Occupy the claim slot so File.Move(request, request + ".claimed") fails with IOException.
        File.WriteAllText(request + ".claimed", "occupied");

        YieldDrainResult result = LeaderScanRequestQueue.DrainYieldRequests(millerDir);

        Assert.False(result.Requested);
        Assert.Equal(1, result.ClaimSkipped);
        Assert.True(File.Exists(request)); // left in place for a later tick / the TTL sweep
    }

    [Fact]
    public void DrainYieldRequests_NoRequestDirectory_ReturnsEmpty()
    {
        Assert.False(LeaderScanRequestQueue.DrainYieldRequests(Path.Combine(_dir, ".miller")).Requested);
    }

    [Fact]
    public void YieldRequests_DoNotConsumeOtherRequestKinds()
    {
        string millerDir = Path.Combine(_dir, ".miller");
        LeaderScanRequestQueue.RequestFullScan(millerDir, "workspace-1", baselineRevision: 1);
        LeaderScanRequestQueue.RequestYield(millerDir, "workspace-1", requesterPid: 4242, requesterExtractorVersion: "2.3.0");

        Assert.True(LeaderScanRequestQueue.DrainYieldRequests(millerDir).Requested);
        Assert.True(LeaderScanRequestQueue.DrainFullScanRequests(millerDir).Requested);
    }

    [Fact]
    public void DrainYieldRequests_SweepsExpiredYieldClaims()
    {
        string millerDir = Path.Combine(_dir, ".miller");
        string requestDir = Path.Combine(millerDir, "requests");
        Directory.CreateDirectory(requestDir);
        string oldStamp = DateTimeOffset.UtcNow
            .Subtract(LeaderScanRequestQueue.RequestTtl + TimeSpan.FromMinutes(1))
            .ToString("yyyyMMddHHmmssfffffff", System.Globalization.CultureInfo.InvariantCulture);
        string staleClaim = Path.Combine(requestDir, oldStamp + "-1-dead.yield.json.claimed");
        File.WriteAllText(staleClaim, "{}");

        YieldDrainResult result = LeaderScanRequestQueue.DrainYieldRequests(millerDir);

        Assert.False(File.Exists(staleClaim)); // swept
        Assert.Equal(1, result.ExpiredDiscarded);
    }

    [Fact]
    public void Drain_SweepsClaimedLeftoversOlderThanTtl()
    {
        string millerDir = Path.Combine(_dir, ".miller");
        string requestDir = Path.Combine(millerDir, "requests");
        Directory.CreateDirectory(requestDir);
        // A leader claimed this request and crashed before deleting it; claimed names never match the drain
        // pattern, so only the TTL sweep can remove them.
        string oldStamp = DateTimeOffset.UtcNow
            .Subtract(LeaderScanRequestQueue.RequestTtl + TimeSpan.FromMinutes(1))
            .ToString("yyyyMMddHHmmssfffffff", System.Globalization.CultureInfo.InvariantCulture);
        string staleClaim = Path.Combine(requestDir, oldStamp + "-1-dead.file-converge.json.claimed");
        File.WriteAllText(staleClaim, "{}");
        string freshClaim = Path.Combine(
            requestDir,
            DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfffffff", System.Globalization.CultureInfo.InvariantCulture)
                + "-1-live.file-converge.json.claimed");
        File.WriteAllText(freshClaim, "{}");

        FileConvergeDrainResult result = LeaderScanRequestQueue.DrainFileConvergeRequests(millerDir);

        Assert.False(File.Exists(staleClaim)); // swept
        Assert.True(File.Exists(freshClaim)); // possibly mid-service by another process — left alone
        Assert.Equal(1, result.ExpiredDiscarded);
    }
}
