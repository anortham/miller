using System.Diagnostics;
using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Daemon.ControlPlane;

/// <summary>
/// The cell answers two questions the CT daemon could not answer before: is the daemon busy, and is the child
/// it started still talking. <c>tests run --wait</c> reads the first; an operator watching
/// <c>daemon.status.json</c> reads the second.
///
/// <para>The monotonic clock is driven by hand. The production silence bound is ten minutes, so a test that
/// slept for it would not run, and one that shortened it would prove only that a short sleep is short.</para>
/// </summary>
public sealed class CtRunActivityCellTests
{
    private static readonly TimeSpan StallBound = TimeSpan.FromMinutes(10);

    private long _ticks;

    [Fact]
    public void A_fresh_cell_is_idle_with_no_run()
    {
        (CtDaemonActivity activity, CtDaemonRunProgress? run) = NewCell().Read();

        Assert.Equal(CtDaemonActivity.Idle, activity);
        Assert.Null(run);
    }

    [Fact]
    public void Ready_work_that_cannot_execute_reads_as_queued()
    {
        CtRunActivityCell cell = NewCell();

        cell.EnterQueued();

        Assert.Equal(CtDaemonActivity.Queued, cell.Read().Activity);
    }

    [Fact]
    public void Entering_idle_clears_queued()
    {
        CtRunActivityCell cell = NewCell();
        cell.EnterQueued();

        cell.EnterIdle();

        Assert.Equal(CtDaemonActivity.Idle, cell.Read().Activity);
    }

    [Fact]
    public void A_drain_reads_as_executing_before_any_child_starts()
    {
        CtRunActivityCell cell = NewCell();
        cell.EnterQueued();

        cell.BeginDrain();

        // The gap between accepting work and starting the first child is exactly where a wait used to slip
        // through and report a mid-run verdict as the result.
        (CtDaemonActivity activity, CtDaemonRunProgress? run) = cell.Read();
        Assert.Equal(CtDaemonActivity.Executing, activity);
        Assert.Null(run);
    }

    /// <summary>
    /// The defect this split exists for. One drain runs every ready project in turn, so a child ending is not
    /// the daemon going idle. If <c>EndRun</c> reported idle, a waiting caller would return in the gap between
    /// two projects of the same drain with only the first project's results.
    /// </summary>
    [Fact]
    public void A_finished_child_does_not_end_the_drain()
    {
        CtRunActivityCell cell = NewCell();
        cell.BeginDrain();
        cell.BeginRun("a.csproj", "run:1", 3);

        cell.EndRun();

        (CtDaemonActivity activity, CtDaemonRunProgress? run) = cell.Read();
        Assert.Equal(CtDaemonActivity.Executing, activity);
        Assert.Null(run);
    }

    [Fact]
    public void Ending_the_drain_returns_to_idle_and_drops_the_run()
    {
        CtRunActivityCell cell = NewCell();
        cell.BeginDrain();
        cell.BeginRun("a.csproj", "run:1", 3);

        cell.EndDrain();

        (CtDaemonActivity activity, CtDaemonRunProgress? run) = cell.Read();
        Assert.Equal(CtDaemonActivity.Idle, activity);
        Assert.Null(run);
    }

    [Fact]
    public void Queued_cannot_demote_a_drain_in_flight()
    {
        CtRunActivityCell cell = NewCell();
        cell.BeginDrain();

        cell.EnterQueued();
        cell.EnterIdle();

        Assert.Equal(CtDaemonActivity.Executing, cell.Read().Activity);
    }

    [Fact]
    public void A_run_carries_the_project_the_run_id_the_case_count_and_the_start_time()
    {
        var startedAt = new DateTimeOffset(2026, 8, 20, 9, 30, 0, TimeSpan.Zero);
        CtRunActivityCell cell = NewCell(clock: () => startedAt);
        cell.BeginDrain();

        cell.BeginRun("C:/repo/App.Tests.csproj", "run:42", 117);

        CtDaemonRunProgress run = Assert.IsType<CtDaemonRunProgress>(cell.Read().Run);
        Assert.Equal("C:/repo/App.Tests.csproj", run.ProjectPath);
        Assert.Equal("run:42", run.RunId);
        Assert.Equal(117, run.SelectedCaseCount);
        Assert.Equal(startedAt, run.RunStartedAtUtc);
    }

    [Fact]
    public void A_child_that_has_said_nothing_reads_as_starting()
    {
        CtRunActivityCell cell = NewCell();
        cell.BeginDrain();
        cell.BeginRun("a.csproj", "run:1", 1);

        // Even after a long wait: no output at all is a different state from a child that fell silent.
        Advance(TimeSpan.FromMinutes(4));

        Assert.Equal(CtRunActivity.Starting, RunOf(cell).Activity);
    }

    [Fact]
    public void A_child_that_just_spoke_reads_as_active()
    {
        CtRunActivityCell cell = NewCell();
        cell.BeginDrain();
        cell.BeginRun("a.csproj", "run:1", 1);
        Advance(TimeSpan.FromMinutes(4));
        cell.StampOutput();

        Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(CtRunActivity.Active, RunOf(cell).Activity);
    }

    [Fact]
    public void A_child_silent_for_a_noticeable_share_of_the_bound_reads_as_quiet()
    {
        CtRunActivityCell cell = NewCell();
        cell.BeginDrain();
        cell.BeginRun("a.csproj", "run:1", 1);
        cell.StampOutput();

        // Past a quarter of ten minutes, well short of the kill.
        Advance(TimeSpan.FromMinutes(6));

        Assert.Equal(CtRunActivity.Quiet, RunOf(cell).Activity);
    }

    [Fact]
    public void A_child_silent_for_the_whole_bound_reads_as_stalled()
    {
        CtRunActivityCell cell = NewCell();
        cell.BeginDrain();
        cell.BeginRun("a.csproj", "run:1", 1);
        cell.StampOutput();

        Advance(StallBound);

        Assert.Equal(CtRunActivity.Stalled, RunOf(cell).Activity);
    }

    /// <summary>
    /// With <c>MILLER_CT_STALL_TIMEOUT=off</c> nothing will kill the run, so naming it "stalled" would promise
    /// an action that is not coming. The words still track the child; only the last one is withheld.
    /// </summary>
    [Fact]
    public void With_the_stall_guard_off_a_silent_child_never_reads_as_stalled()
    {
        CtRunActivityCell cell = NewCell(stallTimeout: Timeout.InfiniteTimeSpan);
        cell.BeginDrain();
        cell.BeginRun("a.csproj", "run:1", 1);
        cell.StampOutput();

        Advance(TimeSpan.FromHours(3));

        Assert.Equal(CtRunActivity.Quiet, RunOf(cell).Activity);
    }

    /// <summary>
    /// "Stalled" says the silence passed the bound, never by how much, so a reader could not separate a kill
    /// that is due this instant from one that is an hour late. The cell publishes the measurement it already
    /// makes, on the monotonic clock the kill itself is armed from.
    /// </summary>
    [Fact]
    public void A_run_carries_the_childs_silence_and_the_bound_the_daemon_will_kill_at()
    {
        CtRunActivityCell cell = NewCell();
        cell.BeginDrain();
        cell.BeginRun("a.csproj", "run:1", 1);
        cell.StampOutput();

        Advance(StallBound + TimeSpan.FromMinutes(3));

        CtDaemonRunProgress run = RunOf(cell);
        Assert.Equal((int)(StallBound + TimeSpan.FromMinutes(3)).TotalSeconds, run.SilenceSeconds);
        Assert.Equal((int)StallBound.TotalSeconds, run.ChildStallSeconds);
    }

    /// <summary>
    /// A reader must judge the daemon against the bound the DAEMON resolved, and zero says plainly that no
    /// kill is coming — not that the bound is unknown.
    /// </summary>
    [Fact]
    public void A_guard_that_is_off_publishes_a_zero_bound()
    {
        CtRunActivityCell cell = NewCell(stallTimeout: Timeout.InfiniteTimeSpan);
        cell.BeginDrain();
        cell.BeginRun("a.csproj", "run:1", 1);
        cell.StampOutput();

        Advance(TimeSpan.FromHours(3));

        CtDaemonRunProgress run = RunOf(cell);
        Assert.Equal(0, run.ChildStallSeconds);
        Assert.Equal((int)TimeSpan.FromHours(3).TotalSeconds, run.SilenceSeconds);
    }

    [Fact]
    public void A_second_run_in_the_same_drain_replaces_the_first_ones_details()
    {
        CtRunActivityCell cell = NewCell();
        cell.BeginDrain();
        cell.BeginRun("first.csproj", "run:1", 3);
        cell.StampOutput();
        cell.EndRun();

        cell.BeginRun("second.csproj", "run:2", 9);

        CtDaemonRunProgress run = RunOf(cell);
        Assert.Equal("second.csproj", run.ProjectPath);
        Assert.Equal("run:2", run.RunId);
        Assert.Equal(9, run.SelectedCaseCount);

        // The first run's output must not make the second one look like it has already spoken.
        Assert.Equal(CtRunActivity.Starting, run.Activity);
    }

    private CtRunActivityCell NewCell(TimeSpan? stallTimeout = null, Func<DateTimeOffset>? clock = null) =>
        new(stallTimeout ?? StallBound, clock, () => Interlocked.Read(ref _ticks));

    private void Advance(TimeSpan amount) =>
        Interlocked.Add(ref _ticks, (long)(amount.TotalSeconds * Stopwatch.Frequency));

    private static CtDaemonRunProgress RunOf(CtRunActivityCell cell) =>
        Assert.IsType<CtDaemonRunProgress>(cell.Read().Run);
}
