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

    public bool ShouldDrain(CtIdleDrainObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        return observation.StaleCount > 0
            && !observation.QueueHasPendingWork
            && !observation.RunExecuting
            && observation.PollSettled
            && !observation.AutoRunsPaused
            && (observation.LastActivityAt is not { } activity
                || observation.Now - activity >= _quietPeriod)
            && (observation.LastDrainAt is not { } drained
                || observation.Now - drained >= Cooldown);
    }
}
