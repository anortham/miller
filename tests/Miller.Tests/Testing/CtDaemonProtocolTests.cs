using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing;

public sealed class CtDaemonProtocolTests
{
    [Fact]
    public void Lease_identity_is_pid_plus_process_start_time()
    {
        var start = new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);
        var first = new CtDaemonLeaseIdentity(Pid: 4242, ProcessStartTimeUtc: start);
        var reusedPid = new CtDaemonLeaseIdentity(Pid: 4242, ProcessStartTimeUtc: start.AddSeconds(30));
        var same = new CtDaemonLeaseIdentity(Pid: 4242, ProcessStartTimeUtc: start);

        Assert.Equal(4242, first.Pid);
        Assert.Equal(start, first.ProcessStartTimeUtc);
        Assert.Equal(first, same);
        Assert.NotEqual(first, reusedPid);

        var lease = new CtDaemonLeaseRecord(
            Identity: first,
            AcquiredAtUtc: start,
            WorkspaceRoot: "/tmp/ws",
            MillerVersion: "1.20.0");
        Assert.Equal(first, lease.Identity);
        Assert.Equal(start, lease.AcquiredAtUtc);
    }

    [Fact]
    public void Command_channel_is_file_based_run_and_stop_with_request_ack()
    {
        Assert.Equal(new[] { CtDaemonCommandKind.Run, CtDaemonCommandKind.Stop }, Enum.GetValues<CtDaemonCommandKind>());
        Assert.Contains(CtDaemonCommandState.Requested, Enum.GetValues<CtDaemonCommandState>());
        Assert.Contains(CtDaemonCommandState.Acknowledged, Enum.GetValues<CtDaemonCommandState>());
        Assert.Contains(CtDaemonCommandState.Rejected, Enum.GetValues<CtDaemonCommandState>());

        var freshness = new CtFreshnessKey("store:abc", 12);
        var request = new CtDaemonCommandRequest(
            CommandId: "cmd-1",
            Kind: CtDaemonCommandKind.Run,
            RequestedAtUtc: DateTimeOffset.UnixEpoch,
            Reason: "explicit",
            Freshness: freshness);
        var ack = new CtDaemonCommandAck(
            CommandId: request.CommandId,
            State: CtDaemonCommandState.Acknowledged,
            AcknowledgedAtUtc: DateTimeOffset.UnixEpoch.AddSeconds(1),
            Reason: null);

        Assert.Equal("cmd-1", request.CommandId);
        Assert.Equal(CtDaemonCommandKind.Run, request.Kind);
        Assert.Equal(freshness, request.Freshness);
        Assert.Equal(CtDaemonCommandState.Acknowledged, ack.State);
        Assert.Equal(request.CommandId, ack.CommandId);
    }

    [Fact]
    public void Status_record_carries_running_paused_stopped_and_reason()
    {
        Assert.Equal(
            new[] { CtDaemonLifecycleState.Running, CtDaemonLifecycleState.Paused, CtDaemonLifecycleState.Stopped },
            Enum.GetValues<CtDaemonLifecycleState>());

        var identity = new CtDaemonLeaseIdentity(7, DateTimeOffset.UnixEpoch);
        var status = new CtDaemonStatusRecord(
            State: CtDaemonLifecycleState.Paused,
            Reason: "user stop pending",
            Identity: identity,
            UpdatedAtUtc: DateTimeOffset.UnixEpoch);

        Assert.Equal(CtDaemonLifecycleState.Paused, status.State);
        Assert.Equal("user stop pending", status.Reason);
        Assert.Equal(identity, status.Identity);
    }

    [Fact]
    public void Control_plane_paths_live_under_workspace_miller_ct()
    {
        string root = Path.Combine(Path.GetTempPath(), "ws");
        string ctDir = Path.Combine(root, ".miller", "ct");

        Assert.Equal("ct", CtDaemonProtocol.DirectoryName);
        Assert.Equal(ctDir, CtDaemonProtocol.RootDirectory(root));
        Assert.Equal(Path.Combine(ctDir, "daemon-v1.lock"), CtDaemonProtocol.LockPath(root));
        Assert.Equal(Path.Combine(ctDir, "daemon.lease.json"), CtDaemonProtocol.LeasePath(root));
        Assert.Equal(Path.Combine(ctDir, "daemon.status.json"), CtDaemonProtocol.StatusPath(root));
        Assert.Equal(
            Path.Combine(ctDir, "commands", "abc.request.json"),
            CtDaemonProtocol.CommandRequestPath(root, "abc"));
        Assert.Equal(
            Path.Combine(ctDir, "commands", "abc.ack.json"),
            CtDaemonProtocol.CommandAckPath(root, "abc"));
        Assert.False(Directory.Exists(ctDir));
    }
}
