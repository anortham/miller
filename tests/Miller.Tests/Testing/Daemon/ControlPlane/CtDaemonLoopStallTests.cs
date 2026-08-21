using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Server.Tools;
using Miller.Testing;
using Miller.Tests.Testing.Daemon.Engine;
using Xunit;

namespace Miller.Tests.Testing.Daemon.ControlPlane;

/// <summary>
/// Nothing on disk proved the daemon's MAIN LOOP was alive. <c>daemon.status.json</c> and the lease pulse
/// both run on the pulse task, which survives a wedged loop BY DESIGN — that is why it exists, so a long
/// drain keeps the file moving — and the pid probe proves only that the process is there. A daemon whose
/// loop had stopped scanning therefore read as <c>running</c> for as long as the process lived.
///
/// <para>The loop now stamps <c>loop_tick_at</c> on its own writes and the pulse copies that value verbatim,
/// so one record carries two stamps from the same clock and their difference is the loop's lag. The reader's
/// own clock never enters it, and a loaded machine slows both writers together.</para>
/// </summary>
public sealed class CtDaemonLoopStallTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan LoopBound = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ChildBound = TimeSpan.FromMinutes(10);

    private readonly string _root = Directory.CreateTempSubdirectory("miller-ct-loop-").FullName;
    private readonly string _millerHome = Directory.CreateTempSubdirectory("miller-ct-loop-home-").FullName;

    public CtDaemonLoopStallTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, CtDaemonProtocol.MillerDirectoryName));
        File.WriteAllText(ContinuousTestPolicy.EnabledMarkerPath(_root), string.Empty);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        try { Directory.Delete(_millerHome, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void A_pulse_that_advances_while_the_tick_stands_still_is_a_wedged_loop()
    {
        CtLoopHealthVerdict verdict = Evaluate(Record(lag: TimeSpan.FromMinutes(5)));

        Assert.Equal(CtLoopHealth.LoopStalled, verdict.Health);
        Assert.True(verdict.Stalled);
        Assert.Equal(300, verdict.LagSeconds);
    }

    /// <summary>Queued is still a loop that must come round: work is accepted and waiting on the budget.</summary>
    [Fact]
    public void A_queued_daemon_is_judged_by_loop_lag_too()
    {
        CtLoopHealthVerdict verdict = Evaluate(
            Record(lag: TimeSpan.FromMinutes(5), activity: CtDaemonActivity.Queued));

        Assert.Equal(CtLoopHealth.LoopStalled, verdict.Health);
    }

    [Fact]
    public void A_loop_that_ticked_within_the_bound_reads_healthy()
    {
        CtLoopHealthVerdict verdict = Evaluate(Record(lag: TimeSpan.FromSeconds(1)));

        Assert.Equal(CtLoopHealth.Healthy, verdict.Health);
        Assert.False(verdict.Stalled);
        Assert.Equal(1, verdict.LagSeconds);
    }

    /// <summary>
    /// The loop BLOCKS for the whole drain, so while a run is in flight the lag is the run's own elapsed
    /// time. Judging it by the loop bound would report every suite longer than ninety seconds as wedged.
    /// </summary>
    [Fact]
    public void An_executing_daemon_is_never_judged_by_loop_lag()
    {
        CtLoopHealthVerdict verdict = Evaluate(
            Record(
                lag: TimeSpan.FromMinutes(40),
                activity: CtDaemonActivity.Executing,
                run: Run(CtRunActivity.Active)));

        Assert.Equal(CtLoopHealth.Healthy, verdict.Health);
        Assert.False(verdict.Stalled);
    }

    /// <summary>
    /// <see cref="CtRunActivity.Stalled"/> is the daemon's own reading that its child passed the silence
    /// bound and the kill is DUE. When the drain has then held the loop for longer than that bound and the
    /// run is still in flight, the kill did not happen — the supervision is hung, not the loop.
    /// </summary>
    [Fact]
    public void A_kill_that_was_owed_and_did_not_happen_is_hung_supervision()
    {
        CtLoopHealthVerdict verdict = Evaluate(
            Record(
                lag: TimeSpan.FromMinutes(22),
                activity: CtDaemonActivity.Executing,
                run: Run(CtRunActivity.Stalled)));

        Assert.Equal(CtLoopHealth.HungSupervision, verdict.Health);
        Assert.True(verdict.Stalled);
        Assert.Contains("Sample.Tests.csproj", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A child reaches "stalled" the instant its silence passes the bound, and the kill fires there. A run
    /// whose drain has not yet outlasted that bound is a run whose kill is not yet late.
    /// </summary>
    [Fact]
    public void A_stalled_child_inside_the_kill_bound_is_not_yet_hung_supervision()
    {
        CtLoopHealthVerdict verdict = Evaluate(
            Record(
                lag: TimeSpan.FromMinutes(4),
                activity: CtDaemonActivity.Executing,
                run: Run(CtRunActivity.Stalled)));

        Assert.Equal(CtLoopHealth.Healthy, verdict.Health);
    }

    /// <summary>An executing record with no run cannot support the claim, so it renders as executing.</summary>
    [Fact]
    public void Executing_with_no_run_is_reported_as_executing_not_hung()
    {
        CtLoopHealthVerdict verdict = Evaluate(
            Record(lag: TimeSpan.FromMinutes(40), activity: CtDaemonActivity.Executing));

        Assert.Equal(CtLoopHealth.Healthy, verdict.Health);
        Assert.False(verdict.Stalled);
    }

    /// <summary>With the child bound off no kill is ever owed, so none can be late.</summary>
    [Fact]
    public void A_child_bound_that_is_off_never_owes_a_kill()
    {
        CtLoopHealthVerdict verdict = CtDaemonLoopHealth.Evaluate(
            Record(
                lag: TimeSpan.FromHours(3),
                activity: CtDaemonActivity.Executing,
                run: Run(CtRunActivity.Stalled)),
            LoopBound,
            Timeout.InfiniteTimeSpan);

        Assert.Equal(CtLoopHealth.Healthy, verdict.Health);
    }

    /// <summary>
    /// A build that predates the field writes no tick, and so does the transition record a family daemon
    /// writes for an adopted worktree. Absence is unknown: a stall that cannot be proven is never reported.
    /// </summary>
    [Fact]
    public void A_record_with_no_loop_tick_proves_nothing()
    {
        var old = new CtDaemonStatusRecord(
            CtDaemonLifecycleState.Running,
            "idle",
            Identity(),
            Now,
            CtDaemonActivity.Idle);

        CtLoopHealthVerdict verdict = Evaluate(old);

        Assert.Equal(CtLoopHealth.Unknown, verdict.Health);
        Assert.False(verdict.Stalled);
        Assert.Null(verdict.LagSeconds);
    }

    [Fact]
    public void A_missing_record_and_a_stopped_daemon_are_both_unknown()
    {
        Assert.Equal(CtLoopHealth.Unknown, Evaluate(null).Health);
        Assert.Equal(
            CtLoopHealth.Unknown,
            Evaluate(Record(lag: TimeSpan.FromHours(1), state: CtDaemonLifecycleState.Stopped)).Health);
    }

    /// <summary>A check that did not run has proven nothing. Off must not read as healthy.</summary>
    [Fact]
    public void Detection_switched_off_reports_unknown_never_healthy()
    {
        CtLoopHealthVerdict verdict = CtDaemonLoopHealth.Evaluate(
            Record(lag: TimeSpan.FromHours(1)),
            Timeout.InfiniteTimeSpan,
            ChildBound);

        Assert.Equal(CtLoopHealth.Unknown, verdict.Health);
        Assert.False(verdict.Stalled);
    }

    /// <summary>
    /// Both stamps come from one process, so the only way the tick can land after the write is a wall-clock
    /// correction between them. That is not evidence of anything, and it must not print a negative lag.
    /// </summary>
    [Fact]
    public void A_clock_correction_between_the_two_stamps_reads_as_no_lag()
    {
        CtLoopHealthVerdict verdict = Evaluate(Record(lag: TimeSpan.FromSeconds(-30)));

        Assert.Equal(CtLoopHealth.Healthy, verdict.Health);
        Assert.Equal(0, verdict.LagSeconds);
    }

    [Fact]
    public void The_default_loop_stall_bound_is_ninety_seconds()
    {
        // Named here so a change to the default is a deliberate edit to a test, not a silent policy shift.
        Assert.Equal(TimeSpan.FromSeconds(90), CtDaemonLoopHealth.DefaultLoopStallTimeout);
    }

    [Theory]
    // Unset or blank keeps the default.
    [InlineData(null, "00:01:30")]
    [InlineData("", "00:01:30")]
    [InlineData("   ", "00:01:30")]
    // Whole seconds, and a TimeSpan, both mean what they say.
    [InlineData("120", "00:02:00")]
    [InlineData("00:02:00", "00:02:00")]
    // Every off token disables the detection, as does a non-positive number.
    [InlineData("off", "-00:00:00.0010000")]
    [InlineData("0", "-00:00:00.0010000")]
    [InlineData("false", "-00:00:00.0010000")]
    [InlineData("no", "-00:00:00.0010000")]
    [InlineData("-5", "-00:00:00.0010000")]
    // A typo must not turn the detection into something else: it falls back to the default.
    [InlineData("ninety", "00:01:30")]
    [InlineData("90s", "00:01:30")]
    public void The_loop_stall_bound_reads_its_environment_override(string? raw, string expected)
    {
        TimeSpan resolved = CtEnvironment.ResolveLoopStallTimeout(
            raw,
            CtDaemonLoopHealth.DefaultLoopStallTimeout);

        Assert.Equal(TimeSpan.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), resolved);
    }

    /// <summary>
    /// The live proof, with a real lease and real files: the main loop is parked inside its poll delay
    /// while the pulse keeps republishing. The pulse must copy the loop's last tick VERBATIM — if it
    /// stamped one of its own, a wedged loop would report a fresh tick forever and this feature would be
    /// worthless.
    /// </summary>
    [Fact]
    public async Task A_wedged_loop_keeps_publishing_while_its_tick_stands_still()
    {
        var pollInterval = TimeSpan.FromSeconds(30);
        var delay = new WedgeTheMainLoop(pollInterval);
        using var cts = new CancellationTokenSource();
        Task<ContinuousTestDaemonSnapshot> run = ContinuousTestDaemonHost.RunAsync(
            _root,
            new ContinuousTestDaemonHostOptions
            {
                Enabled = true,
                AcquireLease = true,
                Enqueuer = new RecordingEnqueuer(),
                PollInterval = pollInterval,
                HeartbeatInterval = TimeSpan.FromMilliseconds(5),
                Delay = delay.DelayAsync,
            },
            cts.Token);

        try
        {
            CtDaemonStatusRecord first = await WaitForStatusAsync(after: null);
            Assert.NotNull(first.LoopTickAtUtc);
            CtDaemonStatusRecord later = await WaitForStatusAsync(after: first.UpdatedAtUtc);
            CtDaemonStatusRecord latest = await WaitForStatusAsync(after: later.UpdatedAtUtc);

            Assert.Equal(first.LoopTickAtUtc, later.LoopTickAtUtc);
            Assert.Equal(first.LoopTickAtUtc, latest.LoopTickAtUtc);
            Assert.True(latest.UpdatedAtUtc > first.UpdatedAtUtc, "the pulse stopped republishing");

            // The wedge is what a reader must see, whatever the wall clock did while the test ran.
            Assert.Equal(
                CtLoopHealth.LoopStalled,
                CtDaemonLoopHealth.Evaluate(latest, TimeSpan.FromTicks(1), ChildBound).Health);
        }
        finally
        {
            await cts.CancelAsync();
            await run;
        }
    }

    /// <summary>
    /// The lease's own first record is written before the loop has ticked once, so it carries no tick. A
    /// writer that invented one would certify a loop that has not run yet.
    /// </summary>
    [Fact]
    public void The_record_written_at_lease_acquire_carries_no_loop_tick()
    {
        using CtDaemonLease? lease = CtDaemonLease.TryAcquire(_root, "1.20.0-test");
        Assert.NotNull(lease);

        CtDaemonStatusRecord? record = CtDaemonLease.TryReadStatus(_root);

        Assert.NotNull(record);
        Assert.Null(record.LoopTickAtUtc);
        Assert.Equal(CtLoopHealth.Unknown, CtDaemonLoopHealth.Evaluate(record, LoopBound, ChildBound).Health);
    }

    [Fact]
    public void Both_renderers_report_a_wedged_loop()
    {
        TestsStatusResult result = StatusWith(Evaluate(Record(lag: TimeSpan.FromMinutes(5))));

        using JsonDocument doc = JsonDocument.Parse(TestsCore.RenderStatusJson(result));
        JsonElement daemon = doc.RootElement.GetProperty("daemon");
        Assert.True(daemon.GetProperty("loop_stalled").GetBoolean());
        Assert.Equal(300, daemon.GetProperty("loop_stall_seconds").GetInt32());

        string compact = TestsCore.RenderStatusCompact(result);
        string line = compact.Split('\n').Single(row => row.StartsWith("daemon_loop:", StringComparison.Ordinal));
        Assert.Contains("loop_stalled", line, StringComparison.Ordinal);
        Assert.Contains("300s", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Hung_supervision_reaches_both_renderers_as_its_own_wording()
    {
        TestsStatusResult result = StatusWith(
            Evaluate(
                Record(
                    lag: TimeSpan.FromMinutes(22),
                    activity: CtDaemonActivity.Executing,
                    run: Run(CtRunActivity.Stalled))));

        using JsonDocument doc = JsonDocument.Parse(TestsCore.RenderStatusJson(result));
        Assert.True(doc.RootElement.GetProperty("daemon").GetProperty("loop_stalled").GetBoolean());

        string line = TestsCore.RenderStatusCompact(result)
            .Split('\n')
            .Single(row => row.StartsWith("daemon_loop:", StringComparison.Ordinal));
        Assert.Contains("hung_supervision", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// A healthy loop and an unproven one both stay quiet in compact output: this line names a fault, and a
    /// line on every read would train the reader to ignore the one that matters. The unproven case reports a
    /// NULL lag rather than zero, because a false zero reads as proof of health.
    /// </summary>
    [Fact]
    public void A_healthy_or_unproven_loop_adds_no_compact_line()
    {
        TestsStatusResult healthy = StatusWith(Evaluate(Record(lag: TimeSpan.FromSeconds(1))));
        Assert.DoesNotContain("daemon_loop:", TestsCore.RenderStatusCompact(healthy), StringComparison.Ordinal);

        TestsStatusResult unproven = StatusWith(CtDaemonLoopHealth.Unknown("the record carries no loop tick"));
        Assert.DoesNotContain("daemon_loop:", TestsCore.RenderStatusCompact(unproven), StringComparison.Ordinal);

        using JsonDocument doc = JsonDocument.Parse(TestsCore.RenderStatusJson(unproven));
        JsonElement daemon = doc.RootElement.GetProperty("daemon");
        Assert.False(daemon.GetProperty("loop_stalled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, daemon.GetProperty("loop_stall_seconds").ValueKind);
    }

    /// <summary>
    /// Miller reports and never kills by itself. The nudge names the recovery an operator has: stop
    /// escalates to a process-tree kill after a short unacked wait, and start puts a live loop back.
    /// </summary>
    [Fact]
    public void A_wedged_loop_nudges_stop_then_start()
    {
        string? hint = TestsTool.StatusHint(StatusWith(Evaluate(Record(lag: TimeSpan.FromMinutes(5)))));

        Assert.Contains("tests operation=stop", hint ?? "", StringComparison.Ordinal);
        Assert.Contains("wedged", hint ?? "", StringComparison.Ordinal);

        string? healthy = TestsTool.StatusHint(StatusWith(Evaluate(Record(lag: TimeSpan.FromSeconds(1)))));
        Assert.DoesNotContain("wedged", healthy ?? "", StringComparison.Ordinal);
    }

    /// <summary>The reader the agent and the dashboard actually go through.</summary>
    [Fact]
    public void Tests_status_reports_a_wedged_loop_from_the_records_own_stamps()
    {
        WriteLiveLease(_root);
        WriteRecord(_root, Record(lag: TimeSpan.FromMinutes(30), identity: CtDaemonLease.CurrentIdentity()));

        TestsStatusResult status = TestsCore.Status(new TestsCoreRequest(_root, MillerHome: _millerHome));

        Assert.NotNull(status.DaemonLoop);
        Assert.Equal(CtLoopHealth.LoopStalled, status.DaemonLoop.Health);
        Assert.Equal(1800, status.DaemonLoop.LagSeconds);
    }

    /// <summary>
    /// An adopted worktree has NO periodic record of its own: the family daemon writes that worktree's
    /// <c>daemon.status.json</c> on transitions only, so its stamps stand still while a healthy daemon
    /// serves it. The reader must resolve the family endpoint first and judge the daemon that runs the loop.
    /// </summary>
    [Fact]
    public void A_worktree_judges_the_family_daemons_loop_not_its_own_transition_record()
    {
        (string main, string worktree) = FakeWorktree();
        WriteLiveLease(main);
        WriteRecord(main, Record(lag: TimeSpan.FromMinutes(30), identity: CtDaemonLease.CurrentIdentity()));
        // The transition record the family daemon wrote when it adopted this worktree: no tick, and a
        // timestamp that has not moved since.
        WriteRecord(
            worktree,
            new CtDaemonStatusRecord(
                CtDaemonLifecycleState.Running,
                $"adopted by {main}",
                CtDaemonLease.CurrentIdentity(),
                Now));

        TestsStatusResult status = TestsCore.Status(new TestsCoreRequest(worktree, MillerHome: _millerHome));

        Assert.NotNull(status.DaemonLoop);
        Assert.Equal(CtLoopHealth.LoopStalled, status.DaemonLoop.Health);
        Assert.Equal(1800, status.DaemonLoop.LagSeconds);
    }

    /// <summary>No live daemon means no loop to judge, and never a reported stall.</summary>
    [Fact]
    public void Tests_status_on_a_workspace_with_no_daemon_reports_no_stall()
    {
        TestsStatusResult status = TestsCore.Status(new TestsCoreRequest(_root, MillerHome: _millerHome));

        Assert.NotNull(status.DaemonLoop);
        Assert.Equal(CtLoopHealth.Unknown, status.DaemonLoop.Health);
        Assert.False(status.DaemonLoop.Stalled);
    }

    private static CtLoopHealthVerdict Evaluate(CtDaemonStatusRecord? record) =>
        CtDaemonLoopHealth.Evaluate(record, LoopBound, ChildBound);

    private static CtDaemonLeaseIdentity Identity() => new(4242, DateTimeOffset.UnixEpoch);

    private static CtDaemonRunProgress Run(CtRunActivity activity) =>
        new(
            Path.Combine("tests", "Sample.Tests.csproj"),
            "ct-run:abc",
            SelectedCaseCount: 7,
            RunStartedAtUtc: Now,
            Activity: activity);

    /// <summary>
    /// One record whose two stamps are <paramref name="lag"/> apart — the pulse republished at
    /// <c>Now</c>, the loop last ticked <paramref name="lag"/> earlier.
    /// </summary>
    private static CtDaemonStatusRecord Record(
        TimeSpan lag,
        CtDaemonActivity activity = CtDaemonActivity.Idle,
        CtDaemonRunProgress? run = null,
        CtDaemonLifecycleState state = CtDaemonLifecycleState.Running,
        CtDaemonLeaseIdentity? identity = null) =>
        new(
            state,
            "idle",
            identity ?? Identity(),
            Now,
            activity,
            run,
            Now - lag);

    private static TestsStatusResult StatusWith(CtLoopHealthVerdict loop) =>
        new(
            Enabled: true,
            KillSwitchOff: false,
            Projects: [],
            DaemonState: CtDaemonLifecycleState.Running,
            DaemonReason: "idle",
            Verdict: ContinuousTestVerdict.Green,
            Selected: null,
            StaleCount: 0,
            SelectedCount: 0,
            LastRun: null,
            BudgetHolder: null,
            DaemonVersion: CtDaemonVersion.Evaluate("1.13.0+bbb", "1.13.0+bbb"),
            DaemonLoop: loop);

    private static void WriteLiveLease(string root)
    {
        Directory.CreateDirectory(CtDaemonProtocol.RootDirectory(root));
        var lease = new CtDaemonLeaseRecord(
            CtDaemonLease.CurrentIdentity(),
            Now,
            Path.GetFullPath(root),
            "1.20.0-test");
        File.WriteAllText(CtDaemonProtocol.LeasePath(root), CtDaemonJson.Serialize(lease));
    }

    private static void WriteRecord(string root, CtDaemonStatusRecord record)
    {
        Directory.CreateDirectory(CtDaemonProtocol.RootDirectory(root));
        File.WriteAllText(CtDaemonProtocol.StatusPath(root), CtDaemonJson.Serialize(record));
    }

    /// <summary>
    /// A linked-worktree layout built from files alone: <c>git worktree add</c> writes a <c>.git</c> FILE
    /// holding <c>gitdir:</c>, and the admin directory's <c>commondir</c> points back at the main checkout.
    /// No <c>git</c> subprocess, which is also how Miller resolves it.
    /// </summary>
    private (string Main, string Worktree) FakeWorktree()
    {
        string main = Path.Combine(_root, "repo");
        string adminDir = Path.Combine(main, ".git", "worktrees", "feature");
        string worktree = Path.Combine(_root, "wt-feature");
        Directory.CreateDirectory(adminDir);
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {adminDir}\n");
        File.WriteAllText(Path.Combine(adminDir, "commondir"), "../..\n");
        Directory.CreateDirectory(Path.Combine(main, CtDaemonProtocol.MillerDirectoryName));
        File.WriteAllText(ContinuousTestPolicy.EnabledMarkerPath(main), string.Empty);
        return (main, worktree);
    }

    private async Task<CtDaemonStatusRecord> WaitForStatusAsync(DateTimeOffset? after)
    {
        for (int attempt = 0; attempt < 400; attempt++)
        {
            CtDaemonStatusRecord? record = CtDaemonLease.TryReadStatus(_root);
            if (record is not null && (after is null || record.UpdatedAtUtc > after))
                return record;
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("the daemon status record did not refresh");
    }

    /// <summary>
    /// Parks the MAIN loop inside its poll delay while letting the pulse's shorter delay through, which is
    /// exactly the shape of a wedged daemon: one live process, one live pulse, one loop that never comes
    /// round again.
    /// </summary>
    private sealed class WedgeTheMainLoop(TimeSpan pollInterval)
    {
        public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken) =>
            duration == pollInterval
                ? Task.Delay(Timeout.Infinite, cancellationToken)
                : Task.Delay(duration, cancellationToken);
    }
}
