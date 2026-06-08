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

    [Fact]
    public void RequestFullScan_WritesUniqueRequestThatLeaderDrainConsumesOnce()
    {
        string millerDir = Path.Combine(_dir, ".miller");

        LeaderScanRequestQueue.RequestFullScan(millerDir, "workspace-1", baselineRevision: 7);

        string requestDir = Path.Combine(millerDir, "requests");
        Assert.Single(Directory.EnumerateFiles(requestDir, "*.full-scan.json"));
        Assert.True(LeaderScanRequestQueue.DrainFullScanRequests(millerDir));
        Assert.Empty(Directory.EnumerateFiles(requestDir, "*.full-scan.json"));
        Assert.False(LeaderScanRequestQueue.DrainFullScanRequests(millerDir));
    }

    [Fact]
    public void DrainFullScanRequests_DeletesMalformedRequestWithoutScanning()
    {
        string millerDir = Path.Combine(_dir, ".miller");
        string requestDir = Path.Combine(millerDir, "requests");
        Directory.CreateDirectory(requestDir);
        string malformed = Path.Combine(requestDir, "bad.full-scan.json");
        File.WriteAllText(malformed, "{ not-json");

        Assert.False(LeaderScanRequestQueue.DrainFullScanRequests(millerDir));
        Assert.False(File.Exists(malformed));
    }
}
