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
