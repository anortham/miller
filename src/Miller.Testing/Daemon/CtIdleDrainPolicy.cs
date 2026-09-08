namespace Miller.Testing;

/// <summary>
/// One tick's view of a workspace context, gathered by the daemon loop for
/// <see cref="CtIdleDrainPolicy.ShouldDrain"/>. <paramref name="LastActivityAt"/> is the last poll
/// that observed anything other than a settled no-op (null means no activity was ever observed,
/// which reads as quiet). <paramref name="LastDrainAt"/> is the cooldown anchor: the last idle
/// drain, or the moment the loop first evaluated this context, so a restarted daemon stays
/// status-only for one full cooldown before it may drain a backlog it did not watch grow.
/// </summary>
public sealed record CtIdleDrainObservation(
    DateTimeOffset Now,
    int StaleCount,
    bool QueueHasPendingWork,
    bool RunExecuting,
    bool PollSettled,
    bool AutoRunsPaused,
    DateTimeOffset? LastActivityAt,
    DateTimeOffset? LastDrainAt);

/// <summary>
/// Structured decision from <see cref="CtIdleDrainPolicy.Evaluate"/> explaining why a context may or may not drain.
/// </summary>
public sealed record CtIdleDrainDecision(
    bool ShouldDrain,
    string Reason,
    DateTimeOffset? NextEligibleAtUtc = null,
    int? RemainingCooldownSeconds = null);

/// <summary>
/// Decides when an idle daemon may convert store staleness back into ONE scheduled run — the
/// convergence half of the Unknown fail-safe. An Unknown selection still executes nothing at the
/// moment it lands; this policy fires LATER, under healthy settled conditions, and the drain it
/// permits is a new workspace-scope selection that travels as an explicit test-ID list (the same
/// stale-set selection an explicit run uses), never a whole-suite run. Without it, a churn window
/// that resolved to Unknown left the whole case set stale with an empty queue forever
/// (2026-08-26 field report: 1,504 stale cases, five idle minutes, no convergence without a human
/// typing <c>tests run</c>).
///
/// <para>Every guard must hold: staleness exists, the queue holds no pending work and no run
/// executes, the last poll was healthy with the saved cursor at the live revision, automatic runs
/// are not paused, the workspace has been quiet for at least the debounce window, and the
/// per-context cooldown has elapsed. The cooldown plus the settled guard is the loop bound: a
/// drain whose own build re-stales cases fires again at most once per <see cref="Cooldown"/>, and
/// a byte-identical rebuild re-stales nothing.</para>
/// </summary>
public sealed class CtIdleDrainPolicy
{
    /// <summary>
    /// Minimum spacing between idle drains for one context. A constant on purpose: the drain is a
    /// background convergence mechanism, not a tunable run trigger, and five minutes bounds a
    /// worst-case self-re-staling drain to a slow, visible cycle.
    /// </summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(5);

    private readonly TimeSpan _quietPeriod;

    public CtIdleDrainPolicy(TimeSpan quietPeriod)
    {
        if (quietPeriod < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(quietPeriod), "must not be negative");
        _quietPeriod = quietPeriod;
    }

    public CtIdleDrainDecision Evaluate(CtIdleDrainObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        if (observation.AutoRunsPaused)
            return new CtIdleDrainDecision(false, "auto_runs_paused");

        if (observation.RunExecuting)
            return new CtIdleDrainDecision(false, "run_executing");

        if (observation.QueueHasPendingWork)
            return new CtIdleDrainDecision(false, "pending_work");

        if (!observation.PollSettled)
            return new CtIdleDrainDecision(false, "poll_unsettled");

        if (observation.StaleCount <= 0)
            return new CtIdleDrainDecision(false, "no_stale_cases");

        if (observation.LastActivityAt is { } activity && observation.Now - activity < _quietPeriod)
        {
            DateTimeOffset nextEligible = activity + _quietPeriod;
            int? remainingCooldown = null;
            if (observation.LastDrainAt is { } lastDrain && lastDrain + Cooldown > observation.Now)
            {
                DateTimeOffset cooldownDeadline = lastDrain + Cooldown;
                if (cooldownDeadline > nextEligible)
                    nextEligible = cooldownDeadline;
                remainingCooldown = (int)Math.Ceiling((cooldownDeadline - observation.Now).TotalSeconds);
            }
            return new CtIdleDrainDecision(false, "waiting_quiet", nextEligible, remainingCooldown);
        }

        if (observation.LastDrainAt is { } drained && observation.Now - drained < Cooldown)
        {
            DateTimeOffset nextEligible = drained + Cooldown;
            TimeSpan remaining = nextEligible - observation.Now;
            int remainingSec = (int)Math.Ceiling(Math.Max(0, remaining.TotalSeconds));
            return new CtIdleDrainDecision(false, "cooldown", nextEligible, remainingSec);
        }

        return new CtIdleDrainDecision(true, "eligible");
    }

    public bool ShouldDrain(CtIdleDrainObservation observation) =>
        Evaluate(observation).ShouldDrain;
}
