using System.ComponentModel;
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

    private static LeaderIdentity IdentityRecordedAt(DateTimeOffset recordedAtUtc, int pid = 4242) =>
        new(pid, "0.9.9+abc1234", null, recordedAtUtc);

    // Documents the bool collapse: a probe that THROWS (e.g. Win32Exception access-denied probing an elevated
    // process on Windows after pid reuse) means a process with this pid exists but cannot be interrogated. That
    // is "not provably dead" — collapsing it to false would make `workspace health` spuriously report
    // indexer_leader_dead (degraded) on a mere probe failure, so it collapses to TRUE (alive).
    [Fact]
    public void IsProcessAlive_ProbeThrows_CollapsesToAliveNotDead()
    {
        bool alive = LeaderIdentityFile.IsProcessAlive(
            IdentityRecordedAt(DateTimeOffset.UtcNow),
            static _ => throw new Win32Exception(5 /* ERROR_ACCESS_DENIED */));

        Assert.True(alive);
    }

    [Fact]
    public void IsProcessAlive_PidReuse_ProcessStartedWellAfterRecordedIdentity_NotAlive()
    {
        // The identity was recorded an hour ago, but the process now wearing that pid started just now: the
        // original leader is gone and the pid was recycled — the leader must not be reported alive.
        DateTimeOffset recorded = DateTimeOffset.UtcNow.AddHours(-1);
        bool alive = LeaderIdentityFile.IsProcessAlive(
            IdentityRecordedAt(recorded),
            _ => new LeaderProcessProbe(Running: true, StartedAtUtc: DateTimeOffset.UtcNow));

        Assert.False(alive);
    }

    [Fact]
    public void IsProcessAlive_StartWithinToleranceOrBeforeRecorded_Alive()
    {
        DateTimeOffset recorded = DateTimeOffset.UtcNow;

        // Clock skew / write latency inside the tolerance window is NOT pid reuse.
        Assert.True(LeaderIdentityFile.IsProcessAlive(
            IdentityRecordedAt(recorded),
            _ => new LeaderProcessProbe(Running: true, StartedAtUtc: recorded.AddSeconds(5))));
        // The normal case: the process started BEFORE it recorded its identity.
        Assert.True(LeaderIdentityFile.IsProcessAlive(
            IdentityRecordedAt(recorded),
            _ => new LeaderProcessProbe(Running: true, StartedAtUtc: recorded.AddHours(-2))));
    }

    [Fact]
    public void IsProcessAlive_ProbeLacksStartTime_SkipsReuseCheck()
    {
        // Start time can be denied (elevated process) even when liveness is readable — the cross-check is
        // best-effort and must not turn an unreadable start time into a dead report.
        bool alive = LeaderIdentityFile.IsProcessAlive(
            IdentityRecordedAt(DateTimeOffset.UtcNow.AddDays(-365)),
            _ => new LeaderProcessProbe(Running: true, StartedAtUtc: null));

        Assert.True(alive);
    }

    [Fact]
    public void IsProcessAlive_RealProcess_RecordedFarOlderThanProcessStart_NotAlive()
    {
        // Acceptance pin through the REAL probe: this test process is alive, but an identity recorded a year
        // before it started can only belong to a recycled pid.
        var identity = IdentityRecordedAt(DateTimeOffset.UtcNow.AddDays(-365), pid: Environment.ProcessId);

        Assert.False(LeaderIdentityFile.IsProcessAlive(identity));
        // And with an honest recorded time, the same real process reads alive.
        Assert.True(LeaderIdentityFile.IsProcessAlive(IdentityRecordedAt(DateTimeOffset.UtcNow, pid: Environment.ProcessId)));
    }

    [Fact]
    public void TryRead_LegacyFileWithoutStartedAtUtc_ParsesAndSkipsReuseCheck()
    {
        // Files written before StartedAtUtc existed must still parse (default timestamp) and must NOT trip the
        // pid-reuse cross-check: default means "unknown", not "recorded at year one".
        Directory.CreateDirectory(MillerDir);
        File.WriteAllText(
            LeaderIdentityFile.PathFor(MillerDir),
            """{"pid":4242,"version":"0.3.6+old1234","processPath":null}""");

        LeaderIdentity? identity = LeaderIdentityFile.TryRead(MillerDir);

        Assert.NotNull(identity);
        Assert.Equal(4242, identity!.Pid);
        Assert.Equal(default, identity.StartedAtUtc);
        Assert.True(LeaderIdentityFile.IsProcessAlive(
            identity,
            _ => new LeaderProcessProbe(Running: true, StartedAtUtc: DateTimeOffset.UtcNow)));
    }
}
