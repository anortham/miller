using System.Diagnostics;
using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Daemon.ControlPlane;

public sealed class CtDaemonLeaseTests : IDisposable
{
    private readonly string _root;

    public CtDaemonLeaseTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "miller-ct-lease-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void TryRead_OnEmptyWorkspace_DoesNotCreateControlPlaneDirectory()
    {
        Assert.Null(CtDaemonLease.TryRead(_root));
        Assert.Null(CtDaemonLease.TryReadLive(_root));
        Assert.False(Directory.Exists(CtDaemonProtocol.RootDirectory(_root)));
        Assert.False(Directory.Exists(Path.Combine(_root, ".miller")));
    }

    [Fact]
    public void TryAcquire_WritesPidPlusStartTimeIdentityAndStatus()
    {
        using CtDaemonLease? lease = CtDaemonLease.TryAcquire(_root, "1.20.0-test");

        Assert.NotNull(lease);
        CtDaemonLeaseIdentity current = CtDaemonLease.CurrentIdentity();
        Assert.Equal(current.Pid, lease.Record.Identity.Pid);
        Assert.Equal(current.ProcessStartTimeUtc, lease.Record.Identity.ProcessStartTimeUtc);
        Assert.Equal("1.20.0-test", lease.Record.MillerVersion);
        Assert.Equal(Path.GetFullPath(_root), lease.Record.WorkspaceRoot);
        Assert.True(File.Exists(CtDaemonProtocol.LockPath(_root)));
        Assert.True(File.Exists(CtDaemonProtocol.LeasePath(_root)));
        Assert.True(File.Exists(CtDaemonProtocol.StatusPath(_root)));
        // The lease is written once and never renewed, so it carries no timestamp that pretends to be a
        // liveness signal. How recently the daemon moved lives in the status record.
        Assert.False(File.Exists(Path.Combine(CtDaemonProtocol.RootDirectory(_root), "daemon.heartbeat.json")));

        CtDaemonLeaseRecord? read = CtDaemonLease.TryRead(_root);
        Assert.NotNull(read);
        Assert.Equal(lease.Record.Identity, read.Identity);
        Assert.Equal(lease.Record, CtDaemonLease.TryReadLive(_root));
    }

    [Fact]
    public void TryAcquire_SecondStartWhileHeld_IsRefused()
    {
        using CtDaemonLease? first = CtDaemonLease.TryAcquire(_root, "1.20.0-test");
        Assert.NotNull(first);

        using CtDaemonLease? second = CtDaemonLease.TryAcquire(_root, "1.20.0-other");
        Assert.Null(second);
        Assert.Equal(first.Record.Identity, CtDaemonLease.TryRead(_root)!.Identity);
    }

    [Fact]
    public void TryAcquire_DeadPidStaleLease_IsReclaimed()
    {
        WriteStaleLease(new CtDaemonLeaseIdentity(Pid: 999_999_991, ProcessStartTimeUtc: DateTimeOffset.UnixEpoch));

        using CtDaemonLease? lease = CtDaemonLease.TryAcquire(_root, "1.20.0-reclaim");

        Assert.NotNull(lease);
        Assert.Equal(Environment.ProcessId, lease.Record.Identity.Pid);
        Assert.NotEqual(DateTimeOffset.UnixEpoch, lease.Record.Identity.ProcessStartTimeUtc);
        Assert.Equal("1.20.0-reclaim", lease.Record.MillerVersion);
        Assert.True(CtDaemonLease.IsIdentityLive(lease.Record.Identity));
    }

    [Fact]
    public void TryAcquire_ReusedPidDifferentStartTime_IsReclaimed()
    {
        var reused = new CtDaemonLeaseIdentity(
            Environment.ProcessId,
            DateTimeOffset.UtcNow.AddHours(-3));
        WriteStaleLease(reused);
        Assert.False(CtDaemonLease.IsIdentityLive(reused));

        using CtDaemonLease? lease = CtDaemonLease.TryAcquire(_root, "1.20.0-reuse");

        Assert.NotNull(lease);
        Assert.Equal(CtDaemonLease.CurrentIdentity(), lease.Record.Identity);
        Assert.NotEqual(reused.ProcessStartTimeUtc, lease.Record.Identity.ProcessStartTimeUtc);
    }

    /// <summary>
    /// A status write keeps the lease file alone: the lease names the holder, and rewriting it every pulse
    /// would put a periodic writer on the file that routing and the version check read on every status call.
    /// </summary>
    [Fact]
    public void WriteStatus_RepublishesTheStatusRecord_WithoutRewritingTheLease()
    {
        using CtDaemonLease? lease = CtDaemonLease.TryAcquire(_root, "1.20.0-test");
        Assert.NotNull(lease);
        CtDaemonLeaseRecord? before = CtDaemonLease.TryRead(_root);
        Assert.NotNull(before);

        var later = new FrozenTime(before.AcquiredAtUtc.AddSeconds(12));
        lease.WriteStatus(CtDaemonLifecycleState.Running, "idle", later);

        Assert.Equal(before, CtDaemonLease.TryRead(_root));
        CtDaemonStatusRecord? status = CtDaemonLease.TryReadStatus(_root);
        Assert.NotNull(status);
        Assert.Equal(later.GetUtcNow(), status.UpdatedAtUtc);
        Assert.Equal(before.Identity, status.Identity);
    }

    [Fact]
    public void Dispose_ReleasesLock_AndMarksStopped()
    {
        CtDaemonLease lease = CtDaemonLease.TryAcquire(_root, "1.20.0-test")!;
        lease.Dispose();
        lease.Dispose();

        Assert.Equal(CtDaemonLifecycleState.Stopped, CtDaemonLease.TryReadStatus(_root)?.State);
        using CtDaemonLease? again = CtDaemonLease.TryAcquire(_root, "1.20.0-next");
        Assert.NotNull(again);
    }

    [Fact]
    public void TryReadLive_WhenRecordedPidIsDead_ReturnsNull()
    {
        WriteStaleLease(new CtDaemonLeaseIdentity(999_999_992, DateTimeOffset.UnixEpoch));
        Assert.Null(CtDaemonLease.TryReadLive(_root));
        Assert.NotNull(CtDaemonLease.TryRead(_root));
        Assert.False(Directory.Exists(Path.Combine(_root, ".miller", "logs")));
    }

    private void WriteStaleLease(CtDaemonLeaseIdentity identity)
    {
        Directory.CreateDirectory(CtDaemonProtocol.RootDirectory(_root));
        var record = new CtDaemonLeaseRecord(
            identity,
            DateTimeOffset.UnixEpoch,
            Path.GetFullPath(_root),
            "stale");
        File.WriteAllText(CtDaemonProtocol.LeasePath(_root), CtDaemonJson.Serialize(record));
    }

    private sealed class FrozenTime(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
