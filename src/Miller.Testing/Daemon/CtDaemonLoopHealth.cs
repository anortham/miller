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

    /// <summary>
    /// How far past the child's silence bound the run must go before a reader calls the supervision hung.
    ///
    /// <para>A child reads <see cref="CtRunActivity.Stalled"/> the INSTANT its silence passes the bound, which
    /// is the same instant the runner's own kill fires. Without a margin the verdict names a fault at the
    /// moment the daemon is correctly handling it. The kill then waits for the child's exit grace and the
    /// stream drain, about ten seconds together, so a minute of headroom reports only a kill that is genuinely
    /// late.</para>
    /// </summary>
    public static readonly TimeSpan HungSupervisionGrace = TimeSpan.FromSeconds(60);

    public static CtLoopHealthVerdict Unknown(string reason) => new(CtLoopHealth.Unknown, null, reason);

    /// <summary>The bound this process would use, resolved from the environment.</summary>
    public static TimeSpan ResolveLoopStallTimeout() =>
        ResolveLoopStallTimeout(Environment.GetEnvironmentVariable);

    /// <summary>
    /// Seam for the lookup itself, so a test can prove the documented VARIABLE NAME is the one read. A test
    /// that only calls <see cref="CtEnvironment.ResolveLoopStallTimeout(string?, TimeSpan)"/> with literal
    /// strings would stay green through a typo in the constant.
    /// </summary>
    internal static TimeSpan ResolveLoopStallTimeout(Func<string, string?> readVariable)
    {
        ArgumentNullException.ThrowIfNull(readVariable);
        return CtEnvironment.ResolveLoopStallTimeout(
            readVariable(CtEnvironment.LoopStallTimeout),
            DefaultLoopStallTimeout);
    }

    /// <summary>
    /// The CHILD silence bound this process would use. A FALLBACK only: the daemon publishes the bound it
    /// actually resolved, and the rule prefers that. This value stands in for a record from a build that
    /// predates the published bound, and it can differ from the daemon's in either direction — the two
    /// processes may have been started in different shells.
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

        // The documented kill switch turns the WHOLE detection off, hung supervision included, so it sits
        // above both rules. Below the executing branch it silenced only half of what it promises.
        if (!IsBounded(loopStallTimeout))
            return Unknown("loop-stall detection is off");

        int lag = WholeSeconds(record.UpdatedAtUtc - tick);

        if (record.Activity == CtDaemonActivity.Executing)
            return Executing(record, lag, childStallTimeout);

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
    /// <para>The separate rule is about the kill the daemon owes itself, and it is measured from the CHILD'S
    /// SILENCE, never from the drain. One drain runs every ready project, so a chatty forty-minute suite that
    /// has only just gone quiet has a long drain and a kill that is not late at all — judged by the drain,
    /// that daemon was named as hung at the exact moment its runner was correctly killing the child. The
    /// claim needs the silence to have outlasted the bound by <see cref="HungSupervisionGrace"/>.</para>
    ///
    /// <para>Both numbers come from the RECORD: the daemon measured the silence on its own monotonic clock
    /// and resolved the bound in the environment it was started in. A reader that re-resolved the bound from
    /// its own environment judged the daemon against a number the daemon never used.</para>
    /// </summary>
    private static CtLoopHealthVerdict Executing(
        CtDaemonStatusRecord record,
        int lag,
        TimeSpan childStallTimeout)
    {
        var executing = new CtLoopHealthVerdict(CtLoopHealth.Healthy, lag, "the daemon is executing a run");
        if (record.Run is not { Activity: CtRunActivity.Stalled } run)
            return executing;

        // The daemon's own bound; only a record that predates the field falls back to this reader's.
        TimeSpan bound = run.ChildStallSeconds is { } recorded
            ? TimeSpan.FromSeconds(recorded)
            : childStallTimeout;

        // With the child bound off nothing will ever kill the run, so no kill is owed and none is late.
        if (!IsBounded(bound))
            return executing;

        // "Stalled" says the silence passed the bound, never by how much. Without the measurement the claim
        // cannot be made at all, exactly as a missing loop tick proves nothing.
        if (run.SilenceSeconds is not { } silenceSeconds)
            return executing;

        var silence = TimeSpan.FromSeconds(silenceSeconds);
        return silence > bound + HungSupervisionGrace
            ? new CtLoopHealthVerdict(
                CtLoopHealth.HungSupervision,
                lag,
                $"the test process for {run.ProjectPath} has been silent for {Seconds(silenceSeconds)}, past "
                    + $"the {Seconds(WholeSeconds(bound))} bound its kill was owed at, and the run still "
                    + $"holds the loop after {Seconds(lag)}")
            : executing;
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
