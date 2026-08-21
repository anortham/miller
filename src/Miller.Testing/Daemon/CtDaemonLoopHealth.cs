namespace Miller.Testing;

/// <summary>
/// What the published record proves about the daemon's MAIN LOOP.
/// </summary>
public enum CtLoopHealth
{
    /// <summary>Nothing can be proven — no record, no loop tick, or no live daemon to judge.</summary>
    Unknown,

    /// <summary>The loop tick is recent enough for what the daemon says it is doing.</summary>
    Healthy,

    /// <summary>The pulse keeps publishing and the loop tick stands still. The loop is wedged.</summary>
    LoopStalled,

    /// <summary>
    /// The child passed its silence bound, so the kill was owed, and the drain still holds the loop.
    /// The daemon is not wedged in the same way — its supervision is.
    /// </summary>
    HungSupervision,
}

/// <param name="LagSeconds">
/// Whole seconds between the record's write and the loop's last tick, or null when the record does not
/// carry a tick to subtract.
/// </param>
public sealed record CtLoopHealthVerdict(CtLoopHealth Health, int? LagSeconds, string Reason)
{
    /// <summary>What a reader acts on. Both wedged shapes are reported, never acted on by Miller.</summary>
    public bool Stalled => Health is CtLoopHealth.LoopStalled or CtLoopHealth.HungSupervision;
}

/// <summary>
/// Reads a published <see cref="CtDaemonStatusRecord"/> and says whether the daemon's main loop is still
/// turning.
///
/// <para><b>Why this exists.</b> Nothing on disk proved the loop was alive. The pulse task republishes the
/// status and it survives a wedged loop BY DESIGN — that is the whole reason it exists, so that a long drain
/// keeps the file moving. A pid probe proves only that the process is there. So a daemon whose loop had
/// stopped scanning read as <c>running</c> for as long as the process lived.</para>
///
/// <para><b>The measurement uses two stamps from the same clock in the same file</b> —
/// <see cref="CtDaemonStatusRecord.UpdatedAtUtc"/> minus <see cref="CtDaemonStatusRecord.LoopTickAtUtc"/> —
/// and never the reader's own clock. Only the pulse can advance <c>updated_at</c> while the tick stands
/// still, so a large gap is proof the loop stalled. A loaded machine slows both writers together, which
/// moves both stamps and cannot fake a stall.</para>
///
/// <para><b>Report only.</b> Nothing here kills a daemon or starts a watchdog. A wedged loop is reported and
/// the operator decides: <c>tests stop</c> then <c>tests start</c>.</para>
/// </summary>
public static class CtDaemonLoopHealth
{
    /// <summary>
    /// How long an idle or queued loop may go without a tick before a reader calls it wedged. The loop's own
    /// poll interval is 250 ms, so this is three hundred passes of headroom — long enough that a paused
    /// machine or a slow disk never trips it, short enough that a wedge is reported within two minutes.
    /// </summary>
    public static readonly TimeSpan DefaultLoopStallTimeout = TimeSpan.FromSeconds(90);

    public static CtLoopHealthVerdict Unknown(string reason) => new(CtLoopHealth.Unknown, null, reason);

    /// <summary>The bound this process would use, resolved from the environment.</summary>
    public static TimeSpan ResolveLoopStallTimeout() =>
        CtEnvironment.ResolveLoopStallTimeout(
            Environment.GetEnvironmentVariable(CtEnvironment.LoopStallTimeout),
            DefaultLoopStallTimeout);

    /// <summary>
    /// The CHILD silence bound this process would use. The reader resolves it the same way the daemon does,
    /// because the hung-supervision rule asks whether a kill the daemon owed has happened.
    /// </summary>
    public static TimeSpan ResolveChildStallTimeout() =>
        CtEnvironment.ResolveStallTimeout(
            Environment.GetEnvironmentVariable(CtEnvironment.StallTimeout),
            new TestProcessRunnerOptions().OutputStallTimeout);

    public static CtLoopHealthVerdict Evaluate(CtDaemonStatusRecord? record) =>
        Evaluate(record, ResolveLoopStallTimeout(), ResolveChildStallTimeout());

    /// <summary>
    /// The pure rule. A non-positive or infinite bound is OFF, and an off bound reports
    /// <see cref="CtLoopHealth.Unknown"/> rather than green: a check that did not run has proven nothing.
    /// </summary>
    public static CtLoopHealthVerdict Evaluate(
        CtDaemonStatusRecord? record,
        TimeSpan loopStallTimeout,
        TimeSpan childStallTimeout)
    {
        if (record is null)
            return Unknown("no status record");

        // A stopped daemon has no loop to judge, and its last record is already honest about that.
        if (record.State == CtDaemonLifecycleState.Stopped)
            return Unknown("the daemon is stopped");

        // A record written before this field existed, or a transition record a family daemon wrote for an
        // adopted worktree. Absence means unknown; a stall that cannot be proven is never reported.
        if (record.LoopTickAtUtc is not { } tick)
            return Unknown("the record carries no loop tick");

        int lag = WholeSeconds(record.UpdatedAtUtc - tick);

        if (record.Activity == CtDaemonActivity.Executing)
            return Executing(record, record.UpdatedAtUtc - tick, lag, childStallTimeout);

        if (!IsBounded(loopStallTimeout))
            return Unknown("loop-stall detection is off");

        return record.UpdatedAtUtc - tick > loopStallTimeout
            ? new CtLoopHealthVerdict(
                CtLoopHealth.LoopStalled,
                lag,
                $"the daemon kept publishing but its loop has not ticked for {Seconds(lag)}")
            : new CtLoopHealthVerdict(CtLoopHealth.Healthy, lag, "the loop is ticking");
    }

    /// <summary>
    /// An executing daemon is NEVER judged by loop lag: the loop legitimately blocks for the whole drain, so
    /// the lag IS the drain's elapsed time and a long suite would read as a wedge.
    ///
    /// <para>The separate rule is about the kill the daemon owes itself. <see cref="CtRunActivity.Stalled"/>
    /// is the daemon's own reading that its child passed the silence bound and the kill is due; when the
    /// drain has then held the loop for longer than that bound and the run is still in flight, the kill did
    /// not happen. A record with no run cannot support that claim, so it renders as executing.</para>
    /// </summary>
    private static CtLoopHealthVerdict Executing(
        CtDaemonStatusRecord record,
        TimeSpan drainElapsed,
        int lag,
        TimeSpan childStallTimeout)
    {
        if (record.Run is not { Activity: CtRunActivity.Stalled } run)
            return new CtLoopHealthVerdict(CtLoopHealth.Healthy, lag, "the daemon is executing a run");

        // With the child bound off nothing will ever kill the run, so no kill is owed and none is late.
        if (!IsBounded(childStallTimeout))
            return new CtLoopHealthVerdict(CtLoopHealth.Healthy, lag, "the daemon is executing a run");

        return drainElapsed > childStallTimeout
            ? new CtLoopHealthVerdict(
                CtLoopHealth.HungSupervision,
                lag,
                $"the test process for {run.ProjectPath} passed its silence bound, the kill has not happened, "
                    + $"and the run has held the loop for {Seconds(lag)}")
            : new CtLoopHealthVerdict(CtLoopHealth.Healthy, lag, "the daemon is executing a run");
    }

    private static bool IsBounded(TimeSpan bound) =>
        bound > TimeSpan.Zero && bound != Timeout.InfiniteTimeSpan;

    /// <summary>
    /// A clock that stepped backwards between the two stamps reads as zero, not as a negative lag: the pair
    /// is written by one process, so the only way to see one is a wall-clock correction landing between
    /// them, and that is not evidence of anything.
    /// </summary>
    private static int WholeSeconds(TimeSpan span) =>
        span <= TimeSpan.Zero ? 0 : (int)Math.Min(span.TotalSeconds, int.MaxValue);

    private static string Seconds(int seconds) =>
        seconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + "s";
}
