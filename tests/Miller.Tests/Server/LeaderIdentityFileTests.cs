using Miller.Server.Hosting;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the leader identity sidecar (<c>.miller/leader.json</c>): atomic write/read round-trip, null for
/// missing/malformed files, best-effort delete, and the pid liveness probe health pairs it with. The file is
/// diagnostic-only — a crash leaves it stale, so liveness is the consumer's job, never the file's claim.
/// </summary>
public sealed class LeaderIdentityFileTests : IDisposable
{
    private readonly string _dir;

    public LeaderIdentityFileTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-leader-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string MillerDir => Path.Combine(_dir, ".miller");

    [Fact]
    public void WriteThenRead_RoundTripsIdentity()
    {
        var identity = new LeaderIdentity(
            Pid: 4242,
            Version: "0.9.9+abc1234",
            ProcessPath: "/opt/miller/miller",
            StartedAtUtc: new DateTimeOffset(2026, 6, 10, 12, 0, 0, TimeSpan.Zero));

        LeaderIdentityFile.Write(MillerDir, identity);

        Assert.Equal(identity, LeaderIdentityFile.TryRead(MillerDir));
    }

    [Fact]
    public void TryRead_MissingFile_ReturnsNull()
    {
        Assert.Null(LeaderIdentityFile.TryRead(MillerDir));
    }

    [Fact]
    public void TryRead_MalformedFile_ReturnsNull()
    {
        Directory.CreateDirectory(MillerDir);
        File.WriteAllText(LeaderIdentityFile.PathFor(MillerDir), "{ not-json");

        Assert.Null(LeaderIdentityFile.TryRead(MillerDir));
    }

    [Fact]
    public void TryDelete_RemovesIdentity_AndIsIdempotent()
    {
        LeaderIdentityFile.Write(MillerDir, new LeaderIdentity(1, "v", null, DateTimeOffset.UnixEpoch));

        LeaderIdentityFile.TryDelete(MillerDir);
        Assert.Null(LeaderIdentityFile.TryRead(MillerDir));
        LeaderIdentityFile.TryDelete(MillerDir); // no throw on already-gone
    }

    [Fact]
    public void IsProcessAlive_TrueForThisProcess_FalseForRecycledBogusPid()
    {
        Assert.True(LeaderIdentityFile.IsProcessAlive(Environment.ProcessId));
        // Pid 0 is never a user process on the supported platforms (kernel/idle), so the probe must say no
        // without throwing; a random huge pid is overwhelmingly unallocated too.
        Assert.False(LeaderIdentityFile.IsProcessAlive(int.MaxValue - 7));
    }
}
