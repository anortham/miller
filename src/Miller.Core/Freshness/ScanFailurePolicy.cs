namespace Miller.Core.Freshness;

/// <summary>
/// What the last whole-repo scan failure for one workspace observed. Persisted per workspace (see
/// <c>Miller.Indexing.ScanFailureJournal</c>) so the retry spacing survives process restarts: without it every
/// fresh Miller process starts at zero consecutive failures, and N worktree agents each re-attempt an
/// OOM-killed extractor from scratch, each taking the machine-wide scan lease and each leaking a temp spool.
/// </summary>
/// <param name="Intent">The intent the failed scan ran at (already downgraded, if it was).</param>
/// <param name="ExitCode">
/// julie-extract's process exit code when one was observed; null when the failure carried none (a Miller-side
/// timeout kill, an exec failure, a report parse error). <see cref="ScanFailurePolicy.SigkillExitCode"/> is the
/// OOM-killer signature.
/// </param>
/// <param name="ConsecutiveFailures">Failures in a row, counting this one. Reset to zero by a success.</param>
/// <param name="Jobs">The <c>--jobs</c> cap the failed attempt ran with.</param>
/// <param name="LastFailureAtUtc">When the failure was recorded.</param>
/// <param name="NextAttemptAtUtc">The instant before which no AUTOMATIC attempt may run.</param>
public sealed record ScanFailureRecord(
    ScanIntent Intent,
    int? ExitCode,
    int ConsecutiveFailures,
    int Jobs,
    DateTimeOffset LastFailureAtUtc,
    DateTimeOffset NextAttemptAtUtc);

/// <summary>
/// What one scan attempt is allowed to do, given the recorded failure history.
/// </summary>
/// <param name="Attempt">False when the caller must defer and leave its rescan latch armed.</param>
/// <param name="EffectiveIntent">
/// The intent to actually run — equal to the requested one except on a permitted downgrade.
/// </param>
/// <param name="Jobs">
/// An explicit <c>--jobs</c> cap the attempt must carry, or null to use the ambient policy. Non-null only as a
/// safety response to a recorded SIGKILL.
/// </param>
/// <param name="Downgraded">
/// True when <see cref="EffectiveIntent"/> is weaker than the requested intent. A downgrade is a THIRD outcome,
/// neither success nor failure: the attempt serves the prior artifact, so it must NOT clear the failure history,
/// must NOT discharge the pending rebuild, and must NOT be reported to the caller as the scan they asked for.
/// It must still consume the attempt slot through <see cref="ScanFailurePolicy.RecordDowngrade"/>, or the next
/// evaluation sees identical state and downgrades again on every tick.
/// </param>
/// <param name="RetryAtUtc">When a deferred attempt becomes eligible; null when nothing is deferred.</param>
/// <param name="ConsecutiveFailures">The failure streak this decision was made against.</param>
public sealed record ScanAttemptDecision(
    bool Attempt,
    ScanIntent EffectiveIntent,
    int? Jobs,
    bool Downgraded,
    DateTimeOffset? RetryAtUtc,
    int ConsecutiveFailures);

/// <summary>
/// The pure decision core behind the persisted scan-failure policy: record + clock + intent ⇒ attempt or defer,
/// the next backoff, the post-SIGKILL jobs clamp, and the downgrade rule. No file I/O, no ambient clock, no
/// randomness of its own — the caller injects <c>now</c>, the jitter draw, and the artifact probe, so every branch
/// is fast-suite testable.
/// </summary>
public static class ScanFailurePolicy
{
    /// <summary>Backoff after the first failure.</summary>
    public static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(30);

    /// <summary>Backoff after the second consecutive failure.</summary>
    public static readonly TimeSpan SecondBackoff = TimeSpan.FromMinutes(2);

    /// <summary>Backoff after the third consecutive failure.</summary>
    public static readonly TimeSpan ThirdBackoff = TimeSpan.FromMinutes(10);

    /// <summary>The ceiling every further consecutive failure saturates at.</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How much the jitter draw may EXTEND a backoff, as a fraction of its base. Jitter only ever adds, so the
    /// scheduled value is a floor and a test can pin the schedule exactly by drawing zero. It exists because N
    /// sibling worktree agents that all failed on the same OOM would otherwise retry in lockstep and reproduce
    /// the memory spike that killed them.
    /// </summary>
    public const double JitterFraction = 0.25;

    /// <summary>The exit code a SIGKILLed child reports (128 + 9) — the OOM killer's signature.</summary>
    public const int SigkillExitCode = 137;

    /// <summary>The <c>--jobs</c> cap the next attempt after a SIGKILL runs with.</summary>
    public const int PostSigkillJobs = 1;

    /// <summary>The unjittered backoff for a streak of <paramref name="consecutiveFailures"/> (1-based).</summary>
    public static TimeSpan BaseBackoffFor(int consecutiveFailures) => consecutiveFailures switch
    {
        <= 1 => FirstBackoff,
        2 => SecondBackoff,
        3 => ThirdBackoff,
        _ => MaxBackoff,
    };

    /// <summary>
    /// The jittered backoff. <paramref name="jitter01"/> is a draw in <c>[0, 1)</c>; it is clamped, so a
    /// misbehaving generator can only cost spacing accuracy, never produce a negative delay.
    /// </summary>
    public static TimeSpan BackoffFor(int consecutiveFailures, double jitter01)
    {
        double draw = double.IsFinite(jitter01) ? Math.Clamp(jitter01, 0, 1) : 0;
        return BaseBackoffFor(consecutiveFailures) * (1 + (JitterFraction * draw));
    }

    /// <summary>The longest a backoff for this streak can be drawn as — the deterministic bound tests wait out.</summary>
    public static TimeSpan MaxJitteredBackoffFor(int consecutiveFailures) =>
        BackoffFor(consecutiveFailures, jitter01: 1);

    /// <summary>Whether <paramref name="exitCode"/> is the SIGKILL/OOM signature.</summary>
    public static bool WasSignalKilled(int? exitCode) => exitCode == SigkillExitCode;

    /// <summary>
    /// Decide what one scan attempt may do.
    ///
    /// <para><paramref name="bypassBackoff"/> is the direct-user carve-out, and it governs BOTH the retry timer
    /// and the downgrade: a person who typed <c>workspace full</c> asked for a from-scratch rebuild, so silently
    /// running a hash-delta and reporting it as that rebuild is the lie the carve-out exists to prevent. It never
    /// skips recording, and it never skips the post-SIGKILL jobs clamp: that clamp is a response to the machine's
    /// memory pressure, and the memory pressure does not care who asked.</para>
    ///
    /// <para><paramref name="priorArtifactUsable"/> is consulted ONLY when a downgrade is otherwise permitted, so
    /// the happy path pays no artifact probe.</para>
    /// </summary>
    public static ScanAttemptDecision Decide(
        ScanFailureRecord? record,
        DateTimeOffset nowUtc,
        ScanIntent intent,
        bool bypassBackoff,
        Func<bool>? priorArtifactUsable)
    {
        if (record is null || record.ConsecutiveFailures <= 0)
            return new ScanAttemptDecision(true, intent, Jobs: null, Downgraded: false, RetryAtUtc: null, 0);

        if (!bypassBackoff && nowUtc < record.NextAttemptAtUtc)
        {
            return new ScanAttemptDecision(
                false, intent, Jobs: null, Downgraded: false, record.NextAttemptAtUtc, record.ConsecutiveFailures);
        }

        int? jobs = WasSignalKilled(record.ExitCode) ? PostSigkillJobs : null;
        bool downgrade = !bypassBackoff
            && ScanIntentPolicy.MayDowngradeToIncremental(intent)
            && priorArtifactUsable is not null
            && priorArtifactUsable();

        return new ScanAttemptDecision(
            true,
            downgrade ? ScanIntent.IncrementalReconcile : intent,
            jobs,
            downgrade,
            RetryAtUtc: null,
            record.ConsecutiveFailures);
    }

    /// <summary>
    /// The record a failed attempt leaves behind: the streak extended by one and the next automatic attempt
    /// pushed out by the jittered backoff for that streak.
    ///
    /// <para>The recorded intent is the STRONGEST of the previous record's and this attempt's, never simply the
    /// latest one. A downgraded retry runs at <see cref="ScanIntent.IncrementalReconcile"/>, so recording the
    /// attempt verbatim would WEAKEN a force-intent record the moment that delta also failed — and a weakened
    /// record is cleared by the next routine <c>workspace refresh</c>, erasing in two steps the cross-process
    /// throttle a repeatedly-killed rebuild built up. The record names the strongest scan still owed.</para>
    /// </summary>
    public static ScanFailureRecord RecordFailure(
        ScanFailureRecord? previous,
        ScanIntent intent,
        int? exitCode,
        int jobs,
        DateTimeOffset nowUtc,
        double jitter01)
    {
        int streak = (previous?.ConsecutiveFailures ?? 0) + 1;
        ScanIntent recorded = previous is null
            ? intent
            : ScanIntentPolicy.Strongest(new[] { previous.Intent, intent });
        return new ScanFailureRecord(
            recorded, exitCode, streak, jobs, nowUtc, nowUtc + BackoffFor(streak, jitter01));
    }

    /// <summary>
    /// The honesty note a downgraded serve owes its caller: what was asked for, why it did not run, and that the
    /// rebuild is still owed. Shared by every path that can downgrade, so no surface can quietly report a delta as
    /// the rebuild it replaced.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="attempt"/> is null.</exception>
    public static string DescribeDowngrade(ScanIntent requested, ScanAttemptDecision attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        return $"the {DescribeIntent(requested)} scan was downgraded to a delta reconcile after " +
            $"{attempt.ConsecutiveFailures} consecutive whole-repo scan failure(s); the prior index is served " +
            "with degraded freshness and the rebuild is still owed (it retries automatically).";
    }

    /// <summary>How an intent reads in agent-facing text: the workspace operation that asks for it.</summary>
    public static string DescribeIntent(ScanIntent intent) =>
        ScanIntentPolicy.RequiresForce(intent) ? "full (force)" : "refresh (delta)";

    /// <summary>
    /// The record a DOWNGRADED serve leaves behind: the streak and the observed failure are unchanged (nothing
    /// new failed, and succeeding at a delta proves nothing about the rebuild that was skipped) but the next
    /// automatic attempt is pushed out by the CURRENT streak's backoff.
    ///
    /// <para>Consuming the attempt slot is what makes the downgrade terminate. Without it the record still says
    /// "retry was due", the rescan latch still holds the undischarged rebuild, and every subsequent debounce tick
    /// evaluates identical state and runs another whole-repo delta — each one taking the machine-wide scan lease,
    /// forever.</para>
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="previous"/> is null.</exception>
    public static ScanFailureRecord RecordDowngrade(
        ScanFailureRecord previous,
        DateTimeOffset nowUtc,
        double jitter01)
    {
        ArgumentNullException.ThrowIfNull(previous);
        return previous with
        {
            NextAttemptAtUtc = nowUtc + BackoffFor(previous.ConsecutiveFailures, jitter01),
        };
    }
}
