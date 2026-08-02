using Miller.Core.Freshness;

namespace Miller.Indexing;

/// <summary>
/// The scan-failure policy as the orchestration layer consumes it: ask what one attempt may do, then report what
/// it did. Injected into <c>IndexerCore</c> exactly like its extract ops and stat predicate, so that class keeps
/// owning no I/O of its own.
///
/// <para>This is the SOLE retry timer for whole-repo scans. <c>IndexerCore</c>'s former in-memory doubling
/// backoff was REPLACED by it rather than layered under it: two independent backoffs that each believe they own
/// the retry timer disagree the moment one is reset, and the in-memory one could not survive the process restart
/// that the whole feature exists to survive.</para>
/// </summary>
public interface IScanFailurePolicy
{
    /// <summary>
    /// What an attempt at <paramref name="intent"/> may do right now. <paramref name="bypassBackoff"/> skips the
    /// retry timer for a user asking directly; it never skips recording or the post-SIGKILL jobs clamp.
    /// </summary>
    ScanAttemptDecision Evaluate(ScanIntent intent, bool bypassBackoff = false);

    /// <summary>
    /// Report that a scan at <paramref name="completed"/> succeeded. The history is cleared only when that
    /// completion <see cref="ScanIntentPolicy.ClearsFailureRecord"/> the intent the RECORDED failure ran at: a
    /// delta clears a delta-intent record, and any completed force clears a force-intent record.
    ///
    /// <para>INTENT-AWARE because a routine <c>workspace refresh</c> otherwise erases a cross-process throttle it
    /// proves nothing about. The exact workspace this feature targets — one whose <c>scan --force</c> is
    /// OOM-killed while a delta fits in memory — would build a 30-minute backoff and then have any agent's
    /// stale-looking-results refresh delete the record.</para>
    ///
    /// <para>A DOWNGRADED attempt must not call this at all: succeeding at a delta is no evidence the
    /// from-scratch rebuild would have survived. It calls <see cref="RecordDowngradedServe"/> instead.</para>
    /// </summary>
    void RecordSuccess(ScanIntent completed);

    /// <summary>
    /// Report that an attempt was DOWNGRADED and served the prior artifact: the streak is left exactly as it is
    /// (nothing new failed) but the next automatic attempt is pushed out by the current streak's backoff, so the
    /// downgrade consumes the attempt slot instead of leaving a due retry that repeats on every tick. A no-op
    /// when no failure is recorded, which is also the only state in which no downgrade can be decided.
    /// </summary>
    void RecordDowngradedServe();

    /// <summary>Extend the failure streak and push out the next automatic attempt.</summary>
    void RecordFailure(ScanIntent intent, int? exitCode, int jobs);

    /// <summary>The current record, or null when no failure is recorded.</summary>
    ScanFailureRecord? Read();
}

/// <summary>
/// The production <see cref="IScanFailurePolicy"/>: state lives in the workspace's
/// <see cref="ScanFailureJournal"/>, so the backoff is shared by every Miller process on that workspace and
/// survives restarts.
/// </summary>
public sealed class PersistedScanFailurePolicy : IScanFailurePolicy
{
    private readonly string _millerDir;
    private readonly Func<bool> _priorArtifactUsable;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<double> _jitter;

    private PersistedScanFailurePolicy(
        string millerDir, Func<bool> priorArtifactUsable, Func<DateTimeOffset> utcNow, Func<double> jitter)
    {
        _millerDir = millerDir;
        _priorArtifactUsable = priorArtifactUsable;
        _utcNow = utcNow;
        _jitter = jitter;
    }

    /// <summary>
    /// Bind the policy to a workspace's artifact. <paramref name="canonicalDbPath"/> locates both the record
    /// (its <c>.miller</c> directory) and the artifact the downgrade rule probes;
    /// <paramref name="canonicalRoot"/> is the root that artifact must record to be servable.
    /// </summary>
    /// <exception cref="ArgumentException">Either path is null, blank, or has no parent directory.</exception>
    public static PersistedScanFailurePolicy For(
        string canonicalDbPath,
        string canonicalRoot,
        Func<DateTimeOffset>? utcNow = null,
        Func<double>? jitter = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalDbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalRoot);
        string millerDir = Path.GetDirectoryName(Path.GetFullPath(canonicalDbPath))
            ?? throw new ArgumentException(
                $"Cannot determine the .miller directory for '{canonicalDbPath}'.", nameof(canonicalDbPath));
        return new PersistedScanFailurePolicy(
            millerDir,
            () => ArtifactRootIdentity.ServableFor(canonicalDbPath, canonicalRoot),
            utcNow ?? (static () => DateTimeOffset.UtcNow),
            jitter ?? (static () => Random.Shared.NextDouble()));
    }

    /// <summary>Test seam: bind to a directory with an injected artifact probe, clock, and jitter draw.</summary>
    internal static PersistedScanFailurePolicy ForTest(
        string millerDir, Func<bool> priorArtifactUsable, Func<DateTimeOffset> utcNow, Func<double> jitter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(millerDir);
        ArgumentNullException.ThrowIfNull(priorArtifactUsable);
        ArgumentNullException.ThrowIfNull(utcNow);
        ArgumentNullException.ThrowIfNull(jitter);
        return new PersistedScanFailurePolicy(Path.GetFullPath(millerDir), priorArtifactUsable, utcNow, jitter);
    }

    /// <summary>The record file this policy reads and writes.</summary>
    public string RecordPath => ScanFailureJournal.PathFor(_millerDir);

    /// <inheritdoc/>
    public ScanAttemptDecision Evaluate(ScanIntent intent, bool bypassBackoff = false) =>
        ScanFailurePolicy.Decide(Read(), _utcNow(), intent, bypassBackoff, _priorArtifactUsable);

    /// <inheritdoc/>
    public void RecordSuccess(ScanIntent completed)
    {
        if (Read() is { } record && !ScanIntentPolicy.ClearsFailureRecord(completed, record.Intent))
            return;
        ScanFailureJournal.TryClear(_millerDir);
    }

    /// <inheritdoc/>
    public void RecordDowngradedServe()
    {
        if (Read() is not { } record)
            return;
        ScanFailureJournal.TryWrite(
            _millerDir, ScanFailurePolicy.RecordDowngrade(record, _utcNow(), _jitter()));
    }

    /// <inheritdoc/>
    public void RecordFailure(ScanIntent intent, int? exitCode, int jobs) =>
        ScanFailureJournal.TryWrite(
            _millerDir,
            ScanFailurePolicy.RecordFailure(Read(), intent, exitCode, jobs, _utcNow(), _jitter()));

    /// <inheritdoc/>
    public ScanFailureRecord? Read() => ScanFailureJournal.TryRead(_millerDir);
}

/// <summary>
/// A process-local <see cref="IScanFailurePolicy"/> over the SAME pure decision core, for seams that have no
/// workspace directory to persist into (unit tests, and a core built before a workspace is bound). It throttles
/// this process only — it cannot hold a backoff across restarts or siblings.
/// </summary>
public sealed class InMemoryScanFailurePolicy : IScanFailurePolicy
{
    private readonly Func<bool> _priorArtifactUsable;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<double> _jitter;
    private readonly object _gate = new();
    private ScanFailureRecord? _record;

    /// <summary>Build over an injected artifact probe, clock, and jitter draw (each has a production default).</summary>
    public InMemoryScanFailurePolicy(
        Func<bool>? priorArtifactUsable = null,
        Func<DateTimeOffset>? utcNow = null,
        Func<double>? jitter = null)
    {
        _priorArtifactUsable = priorArtifactUsable ?? (static () => false);
        _utcNow = utcNow ?? (static () => DateTimeOffset.UtcNow);
        _jitter = jitter ?? (static () => Random.Shared.NextDouble());
    }

    /// <inheritdoc/>
    public ScanAttemptDecision Evaluate(ScanIntent intent, bool bypassBackoff = false)
    {
        lock (_gate)
            return ScanFailurePolicy.Decide(_record, _utcNow(), intent, bypassBackoff, _priorArtifactUsable);
    }

    /// <inheritdoc/>
    public void RecordSuccess(ScanIntent completed)
    {
        lock (_gate)
        {
            if (_record is { } record && !ScanIntentPolicy.ClearsFailureRecord(completed, record.Intent))
                return;
            _record = null;
        }
    }

    /// <inheritdoc/>
    public void RecordDowngradedServe()
    {
        lock (_gate)
        {
            if (_record is not { } record)
                return;
            _record = ScanFailurePolicy.RecordDowngrade(record, _utcNow(), _jitter());
        }
    }

    /// <inheritdoc/>
    public void RecordFailure(ScanIntent intent, int? exitCode, int jobs)
    {
        lock (_gate)
            _record = ScanFailurePolicy.RecordFailure(_record, intent, exitCode, jobs, _utcNow(), _jitter());
    }

    /// <inheritdoc/>
    public ScanFailureRecord? Read()
    {
        lock (_gate)
            return _record;
    }
}
