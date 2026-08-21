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
/// <para>The loop now stamps <c>loop_tick_at_utc</c> every time it moves and the pulse copies that verbatim,
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
    /// bound and the kill is DUE. When the child has then stayed silent well past that bound and the run is
    /// still in flight, the kill did not happen — the supervision is hung, not the loop.
    /// </summary>
    [Fact]
    public void A_kill_that_was_owed_and_did_not_happen_is_hung_supervision()
    {
        CtLoopHealthVerdict verdict = Evaluate(
            Record(
                lag: TimeSpan.FromMinutes(22),
                activity: CtDaemonActivity.Executing,
                run: Run(CtRunActivity.Stalled, silence: ChildBound + TimeSpan.FromMinutes(12))));

        Assert.Equal(CtLoopHealth.HungSupervision, verdict.Health);
        Assert.True(verdict.Stalled);
        Assert.Contains("Sample.Tests.csproj", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A child reaches "stalled" the INSTANT its silence passes the bound, which is the same instant the
    /// runner's own kill fires. The drain elapsed cannot separate the two cases: one drain runs every ready
    /// project, so a chatty forty-minute suite that has only just gone quiet has a long drain and a kill that
    /// is not late at all. Judged by the drain, that healthy daemon was reported as hung supervision, and the
    /// nudge told the operator to stop the daemon that was correctly killing its child.
    /// </summary>
    [Fact]
    public void A_long_drain_whose_child_has_only_just_crossed_the_bound_is_not_hung_supervision()
    {
        CtLoopHealthVerdict verdict = Evaluate(
            Record(
                lag: TimeSpan.FromMinutes(40),
                activity: CtDaemonActivity.Executing,
                run: Run(CtRunActivity.Stalled, silence: ChildBound + TimeSpan.FromSeconds(10))));

        Assert.Equal(CtLoopHealth.Healthy, verdict.Health);
        Assert.False(verdict.Stalled);
    }

    /// <summary>The grace is named here so a change to it is a deliberate edit to a test.</summary>
    [Fact]
    public void The_hung_supervision_grace_is_sixty_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), CtDaemonLoopHealth.HungSupervisionGrace);
    }

    /// <summary>
    /// The daemon resolved its own child bound from the environment IT was started in. A reader in another
    /// shell that re-resolved the bound judged the daemon against a number the daemon never used, in either
    /// direction. The record carries the daemon's own bound, and that is the one the rule uses.
    /// </summary>
    [Fact]
    public void The_daemons_own_child_bound_beats_the_readers()
    {
        CtLoopHealthVerdict verdict = Evaluate(
            Record(
                lag: TimeSpan.FromMinutes(40),
                activity: CtDaemonActivity.Executing,
                run: Run(
                    CtRunActivity.Stalled,
                    silence: TimeSpan.FromMinutes(25),
                    childBound: TimeSpan.FromMinutes(30))));

        // The reader's own bound is ten minutes, so a reader that used it would call this hung.
        Assert.Equal(CtLoopHealth.Healthy, verdict.Health);
    }

    /// <summary>A daemon whose own guard is off owes no kill, whatever the reading process has set.</summary>
    [Fact]
    public void A_daemon_that_records_its_guard_as_off_never_owes_a_kill()
    {
        CtLoopHealthVerdict verdict = Evaluate(
            Record(
                lag: TimeSpan.FromHours(3),
                activity: CtDaemonActivity.Executing,
                run: Run(CtRunActivity.Stalled, silence: TimeSpan.FromHours(3), childBound: TimeSpan.Zero)));

        Assert.Equal(CtLoopHealth.Healthy, verdict.Health);
    }

    /// <summary>
    /// Without the daemon's own silence measurement the claim cannot be made at all: "stalled" says the
    /// silence passed the bound, never by how much. Absence proves nothing, exactly as a missing tick does.
    /// </summary>
    [Fact]
    public void A_stalled_child_with_no_silence_measurement_proves_nothing()
    {
        CtLoopHealthVerdict verdict = Evaluate(
            Record(
                lag: TimeSpan.FromMinutes(40),
                activity: CtDaemonActivity.Executing,
                run: Run(CtRunActivity.Stalled)));

        Assert.Equal(CtLoopHealth.Healthy, verdict.Health);
        Assert.False(verdict.Stalled);
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
                run: Run(CtRunActivity.Stalled, silence: TimeSpan.FromHours(3))),
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
    /// The documented switch turns the WHOLE detection off, hung supervision included. It used to sit below
    /// the executing branch, so an operator who switched it off still got <c>loop_stalled: true</c> from a
    /// daemon running a silent child — a kill switch that did not switch everything off.
    /// </summary>
    [Fact]
    public void Detection_switched_off_silences_hung_supervision_too()
    {
        CtLoopHealthVerdict verdict = CtDaemonLoopHealth.Evaluate(
            Record(
                lag: TimeSpan.FromHours(3),
                activity: CtDaemonActivity.Executing,
                run: Run(CtRunActivity.Stalled, silence: TimeSpan.FromHours(3))),
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

    /// <summary>
    /// Both wall-clock stamps come from the daemon, but a forward correction landing BETWEEN them — an NTP
    /// step, a laptop waking — moves only the later one. The pair then shows a lag the loop never had, and
    /// the reader would nudge the operator to stop a working daemon. The daemon's own monotonic age cannot be
    /// corrected, so it decides.
    /// </summary>
    [Fact]
    public void A_forward_clock_jump_between_the_two_stamps_does_not_fabricate_a_stall()
    {
        CtLoopHealthVerdict verdict = Evaluate(
            Record(lag: TimeSpan.FromMinutes(5), loopAge: TimeSpan.FromSeconds(2)));

        Assert.Equal(CtLoopHealth.Healthy, verdict.Health);
        Assert.False(verdict.Stalled);
        Assert.Equal(2, verdict.LagSeconds);
    }

    /// <summary>The same correction the other way round: a backward step must not hide a wedged loop.</summary>
    [Fact]
    public void A_backward_clock_jump_cannot_conceal_a_wedged_loop()
    {
        CtLoopHealthVerdict verdict = Evaluate(
            Record(lag: TimeSpan.FromSeconds(-30), loopAge: TimeSpan.FromMinutes(5)));

        Assert.Equal(CtLoopHealth.LoopStalled, verdict.Health);
        Assert.True(verdict.Stalled);
        Assert.Equal(300, verdict.LagSeconds);
    }

    /// <summary>
    /// A record from a build that published no age still has its two stamps, and they are all it has. The
    /// fallback keeps that build readable rather than reporting every one of its records as unproven.
    /// </summary>
    [Fact]
    public void A_record_with_no_published_age_falls_back_to_its_two_stamps()
    {
        CtDaemonStatusRecord old = Record(lag: TimeSpan.FromMinutes(5));

        Assert.Null(old.LoopAgeSeconds);
        Assert.Equal(CtLoopHealth.LoopStalled, Evaluate(old).Health);
        Assert.Equal(300, Evaluate(old).LagSeconds);
    }

    /// <summary>A monotonic clock cannot run backwards, so a negative age is a corrupt file, not evidence.</summary>
    [Fact]
    public void A_negative_published_age_is_ignored_like_a_backwards_stamp_pair()
    {
        CtLoopHealthVerdict verdict = Evaluate(
            Record(lag: TimeSpan.FromMinutes(5), loopAge: TimeSpan.FromSeconds(-10)));

        Assert.Equal(CtLoopHealth.LoopStalled, verdict.Health);
        Assert.Equal(300, verdict.LagSeconds);
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
    /// The name the reader actually looks up. The theory above passes literal strings to the parser, so a
    /// typo in the constant would ship green and the documented switch would do nothing.
    /// </summary>
    [Fact]
    public void The_loop_stall_bound_is_read_from_the_documented_variable_name()
    {
        var read = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["MILLER_CT_LOOP_STALL_TIMEOUT"] = "off",
        };

        TimeSpan resolved = CtDaemonLoopHealth.ResolveLoopStallTimeout(
            name => read.TryGetValue(name, out string? value) ? value : null);

        Assert.False(resolved > TimeSpan.Zero, "the off token left the detection bounded");
    }

    /// <summary>
    /// End to end through the production seam: the parameterless resolve and the single-argument
    /// <see cref="CtDaemonLoopHealth.Evaluate(CtDaemonStatusRecord?)"/> that <c>TestsCore</c> calls both read
    /// the real process environment. Nothing else in the suite reads this variable, and it is restored before
    /// the test returns.
    /// </summary>
    [Fact]
    public void The_environment_switch_reaches_the_readers_default_path()
    {
        string? original = Environment.GetEnvironmentVariable(CtEnvironment.LoopStallTimeout);
        try
        {
            Environment.SetEnvironmentVariable(CtEnvironment.LoopStallTimeout, "00:02:00");
            Assert.Equal(TimeSpan.FromMinutes(2), CtDaemonLoopHealth.ResolveLoopStallTimeout());
            Assert.Equal(
                CtLoopHealth.Healthy,
                CtDaemonLoopHealth.Evaluate(Record(lag: TimeSpan.FromSeconds(100))).Health);

            Environment.SetEnvironmentVariable(CtEnvironment.LoopStallTimeout, "off");
            Assert.Equal(
                CtLoopHealth.Unknown,
                CtDaemonLoopHealth.Evaluate(Record(lag: TimeSpan.FromHours(1))).Health);
        }
        finally
        {
            Environment.SetEnvironmentVariable(CtEnvironment.LoopStallTimeout, original);
        }
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
    /// The hung-supervision rule reads two numbers off the FILE, so both must survive the round trip. A
    /// field the serializer dropped would silence the rule everywhere and look like a healthy daemon.
    /// </summary>
    [Fact]
    public void The_childs_silence_and_the_daemons_bound_survive_the_status_file()
    {
        WriteRecord(
            _root,
            Record(
                lag: TimeSpan.FromMinutes(40),
                activity: CtDaemonActivity.Executing,
                run: Run(
                    CtRunActivity.Stalled,
                    silence: TimeSpan.FromMinutes(30),
                    childBound: TimeSpan.FromMinutes(10))));

        CtDaemonStatusRecord? read = CtDaemonLease.TryReadStatus(_root);

        Assert.NotNull(read?.Run);
        Assert.Equal(1800, read.Run.SilenceSeconds);
        Assert.Equal(600, read.Run.ChildStallSeconds);
        Assert.Equal(CtLoopHealth.HungSupervision, Evaluate(read).Health);

        string json = File.ReadAllText(CtDaemonProtocol.StatusPath(_root));
        Assert.Contains("\"silence_seconds\"", json, StringComparison.Ordinal);
        Assert.Contains("\"child_stall_seconds\"", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// The age the rule now prefers rides the same file, so it must survive the same round trip under the
    /// documented key. A field the serializer dropped would send every reader back to the wall-clock pair
    /// this change exists to stop trusting.
    /// </summary>
    [Fact]
    public void The_published_loop_age_survives_the_status_file()
    {
        WriteRecord(_root, Record(lag: TimeSpan.FromMinutes(5), loopAge: TimeSpan.FromSeconds(2.5)));

        CtDaemonStatusRecord? read = CtDaemonLease.TryReadStatus(_root);

        Assert.Equal(2.5, read?.LoopAgeSeconds);
        Assert.Equal(CtLoopHealth.Healthy, Evaluate(read).Health);
        Assert.Contains(
            "\"loop_age_seconds\"",
            File.ReadAllText(CtDaemonProtocol.StatusPath(_root)),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The whole feature is one subtraction, so both stamps must come from ONE clock. The tick used the
    /// host's clock while the record's timestamp came from <see cref="TimeProvider.System"/>, and they
    /// agreed only because both default to UtcNow — a host given a clock of its own wrote a tick from one
    /// clock and a timestamp from another, and the lag between them meant nothing.
    /// </summary>
    [Fact]
    public async Task Both_stamps_come_from_the_hosts_own_clock()
    {
        using var cts = new CancellationTokenSource();
        Task<ContinuousTestDaemonSnapshot> run = ContinuousTestDaemonHost.RunAsync(
            _root,
            new ContinuousTestDaemonHostOptions
            {
                Enabled = true,
                AcquireLease = true,
                Enqueuer = new RecordingEnqueuer(),
                Clock = () => Now,
                PollInterval = TimeSpan.FromMilliseconds(5),
                HeartbeatInterval = TimeSpan.FromSeconds(30),
            },
            cts.Token);

        try
        {
            CtDaemonStatusRecord record = await WaitForStatusAsync(after: null);

            Assert.Equal(Now, record.UpdatedAtUtc);
            Assert.Equal(Now, record.LoopTickAtUtc);
        }
        finally
        {
            await cts.CancelAsync();
            await run;
        }
    }

    /// <summary>
    /// The window right after a run ends, which every healthy daemon passes through. The drain returns, the
    /// activity cell goes back to idle, and the loop does not publish again until it has completed another
    /// whole pass — poll delay, commands, worktree scan, one index poll per context. A pulse firing in that
    /// window republished <c>idle</c> with the tick the loop stamped BEFORE the drain, so a reader saw the
    /// run's whole duration as loop lag and the tool nudged the operator to stop a working daemon.
    ///
    /// <para>The clock advances five minutes inside the provider run, and the loop is parked in its poll
    /// delay afterwards, so the window stands still and the record can be read without racing it.</para>
    /// </summary>
    [Fact]
    public async Task A_daemon_that_has_just_finished_a_run_is_not_reported_as_wedged()
    {
        DateTimeOffset start = DateTimeOffset.UtcNow;
        var clock = new MovableClock(start);
        ContinuousTestWorkspace workspace = EngineTestSupport.Workspace(_root);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        store.PutTestCase(EngineTestSupport.Case("test:app", workspace.ProjectPath));
        var queue = new ContinuousTestDaemonQueue(
            store,
            EngineTestSupport.Selector(store),
            new ContinuousTestCoordinator(
                new ClockAdvancingProvider(() => clock.Advance(TimeSpan.FromMinutes(5))),
                store));
        queue.Enqueue(EngineTestSupport.Change(workspace, observedAt: start));

        var pollInterval = TimeSpan.FromSeconds(30);
        var delay = new WedgeTheMainLoop(pollInterval);
        using var cts = new CancellationTokenSource();
        Task<ContinuousTestDaemonSnapshot> run = ContinuousTestDaemonHost.RunAsync(
            _root,
            new ContinuousTestDaemonHostOptions
            {
                Enabled = true,
                AcquireLease = true,
                WorkspaceId = EngineTestSupport.WorkspaceId,
                Store = store,
                Queue = queue,
                Budget = CtExecutionBudget.Disabled(),
                RunActivity = new CtRunActivityCell(ChildBound, clock.Read),
                Clock = clock.Read,
                PollInterval = pollInterval,
                HeartbeatInterval = TimeSpan.FromMilliseconds(5),
                Delay = delay.DelayAsync,
            },
            cts.Token);

        try
        {
            CtDaemonStatusRecord afterTheRun = await WaitForStatusAsync(record =>
                record.Activity == CtDaemonActivity.Idle && record.UpdatedAtUtc > start);

            Assert.Equal(
                CtLoopHealth.Healthy,
                CtDaemonLoopHealth.Evaluate(
                    afterTheRun,
                    CtDaemonLoopHealth.DefaultLoopStallTimeout,
                    ChildBound).Health);
        }
        finally
        {
            await cts.CancelAsync();
            await run;
        }
    }

    /// <summary>
    /// The live half of the clock-jump proof. The daemon's WALL clock is frozen here, so its two stamps are
    /// identical and their difference proves nothing — the shape a backward correction leaves behind. The
    /// published age comes from a monotonic clock instead, so it keeps growing and the rule still reads the
    /// wedge. The reader cannot hold a monotonic stamp of the daemon's, so the daemon subtracts and publishes
    /// the age; this proves the number in the file is that measurement and not the frozen pair.
    /// </summary>
    [Fact]
    public async Task The_published_loop_age_comes_from_a_clock_the_wall_clock_cannot_move()
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
                Clock = () => Now,
                PollInterval = pollInterval,
                HeartbeatInterval = TimeSpan.FromMilliseconds(5),
                Delay = delay.DelayAsync,
            },
            cts.Token);

        try
        {
            CtDaemonStatusRecord record = await WaitForStatusAsync(
                status => status.LoopAgeSeconds >= 0.1);

            // The frozen pair says the loop ticked at the same instant the record was written.
            Assert.Equal(Now, record.UpdatedAtUtc);
            Assert.Equal(Now, record.LoopTickAtUtc);
            Assert.Equal(
                CtLoopHealth.LoopStalled,
                CtDaemonLoopHealth.Evaluate(record, TimeSpan.FromTicks(1), ChildBound).Health);
        }
        finally
        {
            await cts.CancelAsync();
            await run;
        }
    }

    /// <summary>
    /// A host without a <see cref="ContinuousTestDaemonHostOptions.RunActivity"/> cell is a documented
    /// configuration — the option's own summary says the status file then carries the lifecycle state alone.
    /// It published <c>idle</c> for the whole drain, and idle IS judged by loop lag, so a healthy drain
    /// longer than the bound read as a wedged loop. The loop knows it is draining whether or not a cell was
    /// supplied, so it says so.
    /// </summary>
    [Fact]
    public async Task A_daemon_with_no_activity_cell_publishes_executing_while_it_drains()
    {
        DateTimeOffset start = DateTimeOffset.UtcNow;
        ContinuousTestWorkspace workspace = EngineTestSupport.Workspace(_root);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        store.PutTestCase(EngineTestSupport.Case("test:app", workspace.ProjectPath));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new ContinuousTestDaemonQueue(
            store,
            EngineTestSupport.Selector(store),
            new ContinuousTestCoordinator(new GatedProvider(gate.Task), store));
        queue.Enqueue(EngineTestSupport.Change(workspace, observedAt: start));

        using var cts = new CancellationTokenSource();
        Task<ContinuousTestDaemonSnapshot> run = ContinuousTestDaemonHost.RunAsync(
            _root,
            new ContinuousTestDaemonHostOptions
            {
                Enabled = true,
                AcquireLease = true,
                WorkspaceId = EngineTestSupport.WorkspaceId,
                Store = store,
                Queue = queue,
                Budget = CtExecutionBudget.Disabled(),

                // No RunActivity: the whole point of this test.
                PollInterval = TimeSpan.FromMilliseconds(5),
                HeartbeatInterval = TimeSpan.FromMilliseconds(5),
            },
            cts.Token);

        try
        {
            CtDaemonStatusRecord record = await WaitForStatusAsync(
                status => status.Activity == CtDaemonActivity.Executing);

            // No cell means no run details to publish; the ACTIVITY is still honest, and that is what
            // decides whether the record is judged by loop lag at all.
            Assert.Null(record.Run);
            Assert.Equal(
                CtLoopHealth.Healthy,
                CtDaemonLoopHealth.Evaluate(record, TimeSpan.FromTicks(1), ChildBound).Health);
        }
        finally
        {
            gate.TrySetResult();
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
                    run: Run(CtRunActivity.Stalled, silence: ChildBound + TimeSpan.FromMinutes(12)))));

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

    /// <param name="silence">
    /// How long the daemon says its child has been quiet. Null stands for a record from a build that
    /// predates the measurement.
    /// </param>
    /// <param name="childBound">
    /// The bound the DAEMON resolved. Null stands for a record that predates the field; zero is a daemon
    /// whose guard is off.
    /// </param>
    private static CtDaemonRunProgress Run(
        CtRunActivity activity,
        TimeSpan? silence = null,
        TimeSpan? childBound = null) =>
        new(
            Path.Combine("tests", "Sample.Tests.csproj"),
            "ct-run:abc",
            SelectedCaseCount: 7,
            RunStartedAtUtc: Now,
            Activity: activity,
            SilenceSeconds: silence is { } quiet ? (int)quiet.TotalSeconds : null,
            ChildStallSeconds: childBound is { } bound ? (int)bound.TotalSeconds : null);

    /// <summary>
    /// One record whose two stamps are <paramref name="lag"/> apart — the pulse republished at
    /// <c>Now</c>, the loop last ticked <paramref name="lag"/> earlier.
    /// </summary>
    /// <param name="loopAge">
    /// The age the DAEMON measured on its own monotonic clock. Null stands for a record from a build that
    /// published none, which is the only case where the two stamps above are used.
    /// </param>
    private static CtDaemonStatusRecord Record(
        TimeSpan lag,
        CtDaemonActivity activity = CtDaemonActivity.Idle,
        CtDaemonRunProgress? run = null,
        CtDaemonLifecycleState state = CtDaemonLifecycleState.Running,
        CtDaemonLeaseIdentity? identity = null,
        TimeSpan? loopAge = null) =>
        new(
            state,
            "idle",
            identity ?? Identity(),
            Now,
            activity,
            run,
            Now - lag,
            loopAge?.TotalSeconds);

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

    /// <summary>
    /// Waits for a record the LOOP has stamped. <see cref="CtDaemonLease.TryAcquire"/> publishes a record
    /// before the loop's first write, and that one carries no tick — a poll that won the race against the
    /// first loop write used to fail the caller's non-null assertion for the wrong reason.
    /// </summary>
    private Task<CtDaemonStatusRecord> WaitForStatusAsync(DateTimeOffset? after) =>
        WaitForStatusAsync(record =>
            record.LoopTickAtUtc is not null && (after is null || record.UpdatedAtUtc > after));

    private async Task<CtDaemonStatusRecord> WaitForStatusAsync(Func<CtDaemonStatusRecord, bool> matches)
    {
        for (int attempt = 0; attempt < 400; attempt++)
        {
            CtDaemonStatusRecord? record = CtDaemonLease.TryReadStatus(_root);
            if (record is not null && matches(record))
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

    /// <summary>
    /// One wall clock the whole daemon reads: the loop's tick, the record's own timestamp, and the run
    /// start. A test moves it by hand, so a five-minute run costs no wall time and no sleep.
    /// </summary>
    private sealed class MovableClock(DateTimeOffset start)
    {
        private long _ticks = start.UtcTicks;

        public DateTimeOffset Read() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public void Advance(TimeSpan amount) => Interlocked.Add(ref _ticks, amount.Ticks);
    }

    /// <summary>
    /// A provider whose run holds the drain until the test lets go, which is how a long suite looks to the
    /// daemon loop: one blocked call, one pulse still publishing.
    /// </summary>
    private sealed class GatedProvider(Task gate) : IContinuousTestProvider
    {
        public Task<IReadOnlyList<ProviderTestCase>> DiscoverAsync(
            ContinuousTestWorkspace workspace,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProviderTestCase>>([]);

        public async Task<ProviderRunResult> RunAsync(
            ContinuousTestProviderRunRequest request,
            CancellationToken cancellationToken = default)
        {
            await gate.ConfigureAwait(false);
            return new ProviderRunResult(request.RunId ?? "run:1", "passed");
        }
    }

    /// <summary>A provider whose run takes time on the daemon's clock and then succeeds.</summary>
    private sealed class ClockAdvancingProvider(Action onRun) : IContinuousTestProvider
    {
        public Task<IReadOnlyList<ProviderTestCase>> DiscoverAsync(
            ContinuousTestWorkspace workspace,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProviderTestCase>>([]);

        public Task<ProviderRunResult> RunAsync(
            ContinuousTestProviderRunRequest request,
            CancellationToken cancellationToken = default)
        {
            onRun();
            return Task.FromResult(new ProviderRunResult(request.RunId ?? "run:1", "passed"));
        }
    }
}
