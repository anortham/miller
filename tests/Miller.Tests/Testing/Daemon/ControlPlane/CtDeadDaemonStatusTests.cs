using Microsoft.Data.Sqlite;
using Miller.Server.Tools;
using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Daemon.ControlPlane;

/// <summary>
/// A daemon that dies without a clean shutdown — killed to free the locked binary, crashed, or taken
/// down with the process that spawned it — leaves its last <c>Running</c> record behind in
/// <c>.miller/ct/daemon.status.json</c>. Nothing rewrites that file once the writer is gone.
///
/// <para>Observed live on 2026-08-21: the daemon process was gone, <c>tests stop</c> answered
/// "no daemon", and <c>tests status</c> still reported <c>daemon: running, idle</c>. A reader that
/// trusts the published state believes CT watches the tree while nothing watches it — the dishonest
/// status the CT contract forbids.</para>
///
/// <para>Liveness rides the OS lock and the identity the record names, never the published state, so
/// an out-of-process reader must probe that identity. <see cref="ContinuousTestDaemonHost.ReadStatus"/>
/// stays the cheap unprobed read the wait loop polls every 50ms — it runs its own probe on a slower
/// clock — and <c>ReadLiveStatus</c> is the probing read every one-shot reader takes.</para>
/// </summary>
public sealed class CtDeadDaemonStatusTests : IDisposable
{
    /// <summary>A pid nothing here owns, paired with a start time no live process can carry.</summary>
    private static readonly CtDaemonLeaseIdentity Dead = new(999_999_993, DateTimeOffset.UnixEpoch);

    private readonly string _root = Directory.CreateTempSubdirectory("miller-ct-dead-status-").FullName;
    private readonly string _millerHome = Directory.CreateTempSubdirectory("miller-ct-dead-home-").FullName;

    public CtDeadDaemonStatusTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, CtDaemonProtocol.MillerDirectoryName));
        File.WriteAllText(ContinuousTestPolicy.EnabledMarkerPath(_root), string.Empty);
        // The fixture is only worth reading if the identity it calls dead really is dead.
        Assert.False(CtDaemonLease.IsIdentityLive(Dead));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        try { Directory.Delete(_millerHome, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void A_record_naming_a_dead_process_reads_as_stopped()
    {
        PublishStatus(CtDaemonLifecycleState.Running, "idle", Dead);

        ContinuousTestDaemonSnapshot snapshot = ContinuousTestDaemonHost.ReadLiveStatus(_root);

        Assert.Equal(CtDaemonLifecycleState.Stopped, snapshot.State);
        Assert.False(snapshot.Executing);
        Assert.True(snapshot.Enabled);
    }

    /// <summary>
    /// The run details lie the same way the state does. A daemon killed mid-run leaves a record that
    /// names the project it was executing, and a reader that keeps those fields reports a run in
    /// flight that no process is running.
    /// </summary>
    [Fact]
    public void A_dead_daemon_reports_no_run_in_flight()
    {
        var run = new CtDaemonRunProgress(
            Path.Combine(_root, "tests", "Sample.Tests.csproj"),
            "ct-run:abc",
            SelectedCaseCount: 12,
            RunStartedAtUtc: DateTimeOffset.UnixEpoch,
            Activity: CtRunActivity.Active);
        PublishStatus(CtDaemonLifecycleState.Running, "executing", Dead, CtDaemonActivity.Executing, run);

        ContinuousTestDaemonSnapshot snapshot = ContinuousTestDaemonHost.ReadLiveStatus(_root);

        Assert.Equal(CtDaemonLifecycleState.Stopped, snapshot.State);
        Assert.Equal(CtDaemonActivity.Idle, snapshot.Activity);
        Assert.Null(snapshot.Run);
        Assert.False(snapshot.Executing);
    }

    /// <summary>
    /// A clean shutdown publishes <c>Stopped</c> and THEN the process exits, so the probe fails against a
    /// record that is already honest. Synthesizing "daemon gone" there would report every orderly stop as
    /// a crash — the exact distinction this probe exists to draw. Only an ACTIVE published state can be
    /// contradicted by a dead process.
    /// </summary>
    [Fact]
    public void A_cleanly_stopped_record_keeps_its_own_reason()
    {
        PublishStatus(CtDaemonLifecycleState.Stopped, "stopped", Dead);

        ContinuousTestDaemonSnapshot snapshot = ContinuousTestDaemonHost.ReadLiveStatus(_root);

        Assert.Equal(CtDaemonLifecycleState.Stopped, snapshot.State);
        Assert.Equal("stopped", snapshot.Reason);
    }

    /// <summary>A paused daemon that died is stopped too — the probe judges the process, not the state.</summary>
    [Fact]
    public void A_dead_paused_daemon_reads_as_stopped()
    {
        PublishStatus(CtDaemonLifecycleState.Paused, "budget held elsewhere", Dead);

        Assert.Equal(
            CtDaemonLifecycleState.Stopped,
            ContinuousTestDaemonHost.ReadLiveStatus(_root).State);
    }

    [Fact]
    public void A_record_naming_a_live_process_keeps_its_published_state()
    {
        CtDaemonLeaseIdentity live = CtDaemonLease.CurrentIdentity();
        PublishStatus(CtDaemonLifecycleState.Running, "idle", live, CtDaemonActivity.Queued);

        ContinuousTestDaemonSnapshot snapshot = ContinuousTestDaemonHost.ReadLiveStatus(_root);

        Assert.Equal(CtDaemonLifecycleState.Running, snapshot.State);
        Assert.Equal("idle", snapshot.Reason);
        Assert.Equal(CtDaemonActivity.Queued, snapshot.Activity);
    }

    /// <summary>
    /// The probe costs a lease read plus an OS process lookup, so the wait loop keeps polling the
    /// unprobed read and probes on its own slower clock. This pins that split: making every read
    /// probe would add twelve thousand process lookups to a single full wait.
    /// </summary>
    [Fact]
    public void The_unprobed_read_still_reports_what_the_file_says()
    {
        PublishStatus(CtDaemonLifecycleState.Running, "idle", Dead);

        Assert.Equal(
            CtDaemonLifecycleState.Running,
            ContinuousTestDaemonHost.ReadStatus(_root).State);
    }

    /// <summary>The reader that the agent and the dashboard actually go through.</summary>
    [Fact]
    public void Tests_status_reports_a_dead_daemon_as_stopped()
    {
        PublishStatus(CtDaemonLifecycleState.Running, "idle", Dead);

        TestsStatusResult status = TestsCore.Status(
            new TestsCoreRequest(_root, MillerHome: _millerHome));

        Assert.Equal(CtDaemonLifecycleState.Stopped, status.DaemonState);
        Assert.Equal(CtDaemonActivity.Idle, status.DaemonActivity);
        Assert.Null(status.DaemonRun);
    }

    /// <summary>
    /// A status file with no identity cannot be confirmed, and no lease means no daemon holds the
    /// lock. Reporting stopped is the honest answer; reporting the file's word is the bug.
    /// </summary>
    [Fact]
    public void A_record_with_no_identity_and_no_lease_reads_as_stopped()
    {
        PublishStatus(CtDaemonLifecycleState.Running, "idle", identity: null);

        Assert.Equal(
            CtDaemonLifecycleState.Stopped,
            ContinuousTestDaemonHost.ReadLiveStatus(_root).State);
    }

    /// <summary>A read must never create the control plane it is only looking at.</summary>
    [Fact]
    public void Reading_an_empty_workspace_creates_nothing()
    {
        Assert.Equal(
            CtDaemonLifecycleState.Stopped,
            ContinuousTestDaemonHost.ReadLiveStatus(_root).State);
        Assert.False(Directory.Exists(CtDaemonProtocol.RootDirectory(_root)));
    }

    private void PublishStatus(
        CtDaemonLifecycleState state,
        string reason,
        CtDaemonLeaseIdentity? identity,
        CtDaemonActivity activity = CtDaemonActivity.Idle,
        CtDaemonRunProgress? run = null)
    {
        Directory.CreateDirectory(CtDaemonProtocol.RootDirectory(_root));
        var record = new CtDaemonStatusRecord(
            state,
            reason,
            identity,
            DateTimeOffset.UnixEpoch,
            activity,
            run);
        File.WriteAllText(CtDaemonProtocol.StatusPath(_root), CtDaemonJson.Serialize(record));
    }
}
