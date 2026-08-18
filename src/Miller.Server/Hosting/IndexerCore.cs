using Microsoft.Extensions.Logging;
using Miller.Core.Freshness;
using Miller.Indexing;
using Miller.Server.Logging;
using Miller.Server.Workspaces;

namespace Miller.Server.Hosting;

/// <summary>
/// The pure dispatch core of <see cref="IndexerService"/> — the testable seam that owns the coalescing
/// <see cref="WatchEventQueue"/> and turns a drained batch into <c>extract</c> calls, with NO FileSystemWatcher,
/// NO subprocess, and NO SQLite of its own (the runner is an injected <see cref="IExtractOps"/>, file existence
/// is an injected stat predicate). The hosted service feeds it watcher events on a debounce tick.
///
/// <para><b>Threading.</b> <see cref="WatchEventQueue"/> is not thread-safe, so every access here is guarded by
/// a single <see cref="_gate"/> lock (the FileSystemWatcher delivers events on thread-pool threads while the
/// debounce timer drains on another). This mirrors julie serializing its queue under a Tokio mutex.</para>
///
/// <para><b>One in-flight subprocess.</b> A drain holds the lock for the whole batch, so a second tick cannot
/// interleave a concurrent <c>extract</c> — the operations run strictly one at a time, in routed order.</para>
///
/// <para><b>Failure isolation (decision-10).</b> A single op throwing (a lock timeout, a data-loss guard, a
/// recoverable parser failure) is logged and the batch continues; the prior index is kept and the failed file
/// is reconciled on the next scan. One bad file never wipes the queue or aborts sibling updates.</para>
///
/// <para><b>Scan-failure backoff.</b> The whole-repo retry timer is the injected
/// <see cref="IScanFailurePolicy"/> — the persisted, per-workspace, cross-process record — and this class keeps
/// none of its own. It REPLACED the former in-memory doubling backoff rather than layering over it: two
/// independent timers disagree the moment one is reset, and an in-memory one cannot survive the process restart
/// the policy exists to survive. Every whole-repo scan that runs here reports back exactly one of three outcomes —
/// failed, downgraded, or completed at the requested strength — because only the third may clear the record.</para>
/// </summary>
public sealed class IndexerCore
{
    /// <summary>
    /// The largest queued per-file batch still drained on a tick that owes a whole-repo scan it cannot run —
    /// refused machine-wide admission, or the post-failure backoff. Draining then is a RESPONSIVENESS
    /// optimization for the small-batch case (an agent editing a handful of files), never a correctness
    /// requirement — the armed latch is what guarantees the reconcile — so a batch above this bound is left
    /// queued instead of spawning one sequential <c>extract update</c> per path that the latched whole-repo scan
    /// would redo anyway. Order tens because that is the boundary between the two shapes: an agent's edit burst
    /// is a handful of files, a branch switch or checkout is hundreds up to
    /// <see cref="WatchEventQueue.MaxQueue"/>.
    /// </summary>
    internal const int MaxDeferredScanDrain = 32;

    private readonly IExtractOps _ops;
    private readonly Func<string, bool> _exists;
    private readonly ILogger? _logger;
    private readonly IScanFailurePolicy _failurePolicy;
    private readonly object _gate = new();

    // The FileSystemWatcher Error (InternalBuffer overflow) signal. Kept in this layer rather than mutating the
    // Core queue's NeedsRescan, so the queue's pure surface (handed off by the Core layer) stays untouched: a
    // dropped-events overflow there sets its own NeedsRescan, an FSW buffer overflow sets this one, and a drain
    // ORs both (with .git/HEAD) into the router's single-scan decision. Guarded by _gate.
    private bool _overflowSignaled;

    // The persistent whole-repo-rescan latch. Every transient signal (the queue's own NeedsRescan, the FSW
    // overflow flag, a per-tick .git/HEAD move) folds into it, and ONLY a scan that actually ran and succeeded
    // clears it — either here or, for the out-of-band scans IndexerService runs itself, through
    // NoteWholeRepoScanCompleted. The SET (rather than one folded value) is what keeps two differently-motivated
    // requests from erasing each other: the scan runs at ScanIntentPolicy.Strongest, so a re-armed request is
    // never retried more weakly than anything folded in, and each pending intent is discharged only by a
    // completion that ScanIntentPolicy.Satisfies. Guarded by _gate.
    private readonly HashSet<ScanIntent> _pendingWholeRepoScanIntents = new();

    // Bumped by every RequestWholeRepoScan. An out-of-band scan captures it BEFORE it starts and hands it back on
    // completion, so a request armed while that scan was already in flight no longer matches and cannot be
    // cleared by it: the finished scan never consumed the later request, and matching intent alone cannot tell
    // the two apart. The drain's own scan needs no such guard — it runs under _gate, which every arming path
    // takes, so nothing can arm mid-drain. Guarded by _gate.
    private long _wholeRepoScanArmingGeneration;

    // Log throttle for the suppressed-scan warning: one line per backoff arming, not one per 250ms tick.
    // Guarded by _gate.
    private bool _backoffWarned;

    /// <summary>The coalescing event queue. Enqueue under the watcher; drained on the debounce tick.</summary>
    public WatchEventQueue Queue { get; }

    /// <summary>
    /// True when either per-file events or a forced-rescan signal are waiting for the next drain tick.
    /// </summary>
    public bool HasPendingWork
    {
        get
        {
            if (!Monitor.TryEnter(_gate))
                return true;

            try
            {
                return Queue.Count > 0 || Queue.NeedsRescan || _overflowSignaled ||
                    _pendingWholeRepoScanIntents.Count > 0;
            }
            finally
            {
                Monitor.Exit(_gate);
            }
        }
    }

    /// <summary>
    /// Construct the core over a fresh (or supplied) queue, the extract-op runner, a file-existence stat, and the
    /// scan-failure policy that owns the whole-repo retry timer. <paramref name="failurePolicy"/> defaults to a
    /// process-local one; production passes the workspace's persisted policy so the backoff is shared with every
    /// other Miller process on that workspace.
    /// </summary>
    /// <exception cref="ArgumentNullException">Any required argument is null.</exception>
    public IndexerCore(
        WatchEventQueue queue,
        IExtractOps ops,
        Func<string, bool> exists,
        ILogger? logger = null,
        IScanFailurePolicy? failurePolicy = null)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(ops);
        ArgumentNullException.ThrowIfNull(exists);
        Queue = queue;
        _ops = ops;
        _exists = exists;
        _logger = logger;
        _failurePolicy = failurePolicy ?? new InMemoryScanFailurePolicy();
    }

    /// <summary>
    /// Enqueue a watcher event under the queue lock (the watcher callback's only entry point). Coalescing and
    /// overflow are handled by <see cref="WatchEventQueue"/>; this only serializes access.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="ev"/> is null.</exception>
    public void Enqueue(WatchEvent ev)
    {
        ArgumentNullException.ThrowIfNull(ev);
        lock (_gate)
            Queue.Enqueue(ev);
    }

    /// <summary>
    /// Signal that the FileSystemWatcher's internal buffer overflowed (its <c>Error</c> event) — events were
    /// dropped at the OS level, so the next drain must force a whole-repo <c>scan</c> reconcile rather than
    /// trust the lossy event stream. The next drain folds this into the persistent rescan latch, which survives
    /// until a scan actually reconciles the repo.
    /// </summary>
    public void SignalRescan()
    {
        lock (_gate)
            _overflowSignaled = true;
    }

    /// <summary>
    /// Arm the whole-repo rescan latch directly, for a request that was claimed elsewhere and then could not be
    /// serviced (refused scan admission, a deferred retry, or a scan that failed) — so the reconcile is retried
    /// instead of lost. <paramref name="intent"/> survives until a scan that
    /// <see cref="ScanIntentPolicy.Satisfies"/> it succeeds; the retry itself runs at the strongest intent armed.
    /// </summary>
    public void RequestWholeRepoScan(ScanIntent intent)
    {
        lock (_gate)
        {
            _pendingWholeRepoScanIntents.Add(intent);
            _wholeRepoScanArmingGeneration++;
        }
    }

    /// <summary>
    /// The latch's current arming generation. A whole-repo scan run OUTSIDE this core MUST read it BEFORE the
    /// scan starts and pass that value to <see cref="NoteWholeRepoScanCompleted"/>, so a request armed while the
    /// scan was in flight survives the completion of a scan that could not have serviced it.
    /// </summary>
    public long WholeRepoScanArmingGeneration
    {
        get
        {
            lock (_gate)
                return _wholeRepoScanArmingGeneration;
        }
    }

    /// <summary>
    /// Report that a whole-repo scan run OUTSIDE this core succeeded, so the latch entries it discharges are
    /// cleared instead of driving a duplicate rebuild on the next tick. INTENT-AWARE: only pending intents
    /// <see cref="ScanIntentPolicy.Satisfies"/> considers discharged by <paramref name="completed"/> are dropped,
    /// so a delta (including a DOWNGRADED rebuild that ran as one) never clears a pending force.
    /// GENERATION-AWARE: <paramref name="armingGeneration"/> is the <see cref="WholeRepoScanArmingGeneration"/>
    /// read before the scan started, and a latch re-armed since then is left alone — the scan that just finished
    /// began before that request existed, so clearing it would silently drop a rebuild Miller already promised
    /// the caller. The failure history is NOT touched here: the caller that ran the scan owns
    /// <see cref="IScanFailurePolicy.RecordSuccess"/>, because only it knows whether the run was a downgrade.
    /// </summary>
    public void NoteWholeRepoScanCompleted(ScanIntent completed, long armingGeneration)
    {
        lock (_gate)
        {
            if (_wholeRepoScanArmingGeneration != armingGeneration)
                return;
            DischargePendingIntents(completed);
        }
    }

    /// <summary>
    /// Whether the next drain would route a whole-repo <c>scan</c> — the latch, either transient rescan signal,
    /// or a <c>.git/HEAD</c> move on this tick — and is not inside the post-failure backoff. Read under the queue
    /// lock so the caller can acquire scan admission BEFORE draining without racing the watcher threads. False
    /// during backoff so a failing scan does not take the machine-wide lease on every tick.
    /// </summary>
    public bool WouldRunWholeRepoScan(bool headChanged)
    {
        lock (_gate)
        {
            if (PendingScanIntent(headChanged) is not { } intent)
                return false;
            return _failurePolicy.Evaluate(intent).Attempt;
        }
    }

    /// <summary>
    /// The consecutive whole-repo scan failures currently driving the backoff. Zero once one succeeds.
    /// </summary>
    internal int ConsecutiveScanFailures => _failurePolicy.Read()?.ConsecutiveFailures ?? 0;

    /// <summary>
    /// Drain and process the queue. <paramref name="headChanged"/> is true when <c>.git/HEAD</c> moved since the
    /// last drain (a branch switch / checkout): together with an overflow
    /// <see cref="WatchEventQueue.NeedsRescan"/> it forces a single <c>scan</c> and drops the per-file events.
    /// <paramref name="wholeRepoScanAdmitted"/> is the caller's machine-wide scan admission: when a whole-repo
    /// scan is DUE and admission was refused, the scan is skipped and the latch stays armed for a later tick,
    /// while up to <see cref="MaxDeferredScanDrain"/> observed per-file events are still applied — dropping a
    /// handful of interactive edits for the whole contention window is strictly worse than applying a
    /// possibly-lossy stream the latched reconcile will fix. A LARGER batch is left queued whenever the owed scan
    /// cannot run, refusal and post-failure backoff alike: the latched whole-repo scan reconciles every one of
    /// those paths anyway, so draining would only spend hundreds of sequential extracts — holding this gate and
    /// the caller's, and blocking the exempt per-file write-through — to reach the same index.
    /// <paramref name="usedWholeRepoScan"/> reports a whole-repo scan that actually ran and SUCCEEDED — it drives
    /// the caller's full-rebuild sidecar convergence, so a failed scan must not claim one.
    /// Returns true if any op ran (work was scheduled), false on a tick with nothing left to do.
    /// </summary>
    public bool DrainAndProcess(bool headChanged, bool wholeRepoScanAdmitted, out bool usedWholeRepoScan)
    {
        // The whole drain+execute runs under one lock so a second debounce tick cannot interleave a concurrent
        // subprocess: the operations run strictly one-in-flight, in routed order.
        lock (_gate)
        {
            usedWholeRepoScan = false;

            // Fold every transient signal into the persistent latch FIRST. headChanged is computed per-tick by
            // the caller and is not persistent, so a HEAD-move rescan that never runs would otherwise be lost.
            if (Queue.NeedsRescan)
            {
                Queue.ClearNeedsRescan();
                _pendingWholeRepoScanIntents.Add(ScanIntent.IncrementalReconcile);
            }
            if (_overflowSignaled)
            {
                _overflowSignaled = false;
                _pendingWholeRepoScanIntents.Add(ScanIntent.IncrementalReconcile);
            }
            if (headChanged)
                _pendingWholeRepoScanIntents.Add(ScanIntent.IncrementalReconcile);

            bool scanPending = _pendingWholeRepoScanIntents.Count > 0;
            ScanAttemptDecision? decision = scanPending
                ? _failurePolicy.Evaluate(ScanIntentPolicy.Strongest(_pendingWholeRepoScanIntents))
                : null;

            bool scanDue = decision is { Attempt: true };
            if (scanPending && !scanDue)
                WarnScanSuppressedOnce(decision!);

            if (scanDue && !wholeRepoScanAdmitted)
                scanDue = false;

            // A whole-repo scan is owed but will not run this tick — refused admission, or the post-failure
            // backoff. Above the bound the queued events stay where they are: the still-armed latch reconciles
            // every one of them, so draining here would run up to WatchEventQueue.MaxQueue sequential extracts,
            // holding this gate and the caller's for the whole storm, to reach an index the latched scan produces
            // anyway. An overflow while they wait is not a loss either — it folds straight back into the latch.
            bool scanOwedButNotRunning = scanPending && !scanDue;
            if (scanOwedButNotRunning && Queue.Count > MaxDeferredScanDrain)
            {
                _logger?.LogDebug(
                    "A whole-repo scan is owed but deferred with {QueuedPaths} paths queued (over the {Bound}-path " +
                    "drain bound); leaving them for the latched reconcile instead of extracting each one.",
                    Queue.Count, MaxDeferredScanDrain);
                return false;
            }

            IReadOnlyList<WatchEvent> drained = Queue.Drain();

            if (!scanDue && drained.Count == 0)
                return false; // no per-file events and no runnable reconcile — a no-op tick.

            ScanOp? wholeRepoScan = scanDue
                ? ScanOp.For(decision!.EffectiveIntent, decision.Jobs)
                : null;

            // Stat is injected; watcher paths may not yet be canonical here — they are canonicalized later in
            // JulieExtractOps (per-file via PathCanonicalizer) just before the extract call, NOT in this layer.
            IReadOnlyList<ExtractOp> ops = WatchEventRouter.Route(drained, _exists, wholeRepoScan);

            foreach (var op in ops)
            {
                (bool succeeded, Exception? failure) = ExecuteIsolated(op);
                if (op is not ScanOp scan)
                    continue;
                if (succeeded)
                    usedWholeRepoScan = true;
                else
                    RecordScanFailure(scan, failure);
            }

            // Success, not admission, is the commit point: a swallowed extractor failure must leave the latch
            // armed so the reconcile is retried rather than silently dropped.
            if (usedWholeRepoScan)
            {
                // A DOWNGRADED rebuild that succeeded as a delta is a degraded serve, not a recovery: it proves
                // nothing about the from-scratch rebuild that was skipped, so it neither clears the failure
                // history nor discharges the pending rebuild. It must still CONSUME the attempt slot — the
                // undischarged rebuild plus an already-elapsed next_attempt_at is a state that repeats itself,
                // and this drain runs every 250ms.
                if (decision!.Downgraded)
                    _failurePolicy.RecordDowngradedServe();
                else
                    _failurePolicy.RecordSuccess(decision.EffectiveIntent);
                _backoffWarned = false;
                DischargePendingIntents(decision.EffectiveIntent);
            }

            return ops.Count > 0;
        }
    }

    // All helpers below run under _gate, except where they only read the injected policy.

    // The strongest intent the next drain owes, or null when nothing is pending. headChanged is a per-tick fact
    // the caller supplies rather than latched state, so it is OR-ed in without being consumed.
    private ScanIntent? PendingScanIntent(bool headChanged)
    {
        // A transient signal only ever contributes the weakest intent, so it cannot change the strongest when the
        // latch already holds one.
        if (_pendingWholeRepoScanIntents.Count > 0)
            return ScanIntentPolicy.Strongest(_pendingWholeRepoScanIntents);

        return headChanged || Queue.NeedsRescan || _overflowSignaled
            ? ScanIntent.IncrementalReconcile
            : null;
    }

    private void RecordScanFailure(ScanOp scan, Exception? failure)
    {
        int jobs = scan.Jobs ?? ExtractJobsPolicy.FromEnvironment();
        _failurePolicy.RecordFailure(scan.Intent, JulieExtractException.ExitCodeOf(failure), jobs);
        _backoffWarned = false;
        ScanFailureRecord? record = _failurePolicy.Read();
        _logger?.LogWarning(
            "Whole-repo scan failed {Failures} time(s) in a row; the next automatic attempt is deferred until " +
            "{RetryAtUtc:O} so a failing extractor cannot respawn on every debounce tick.",
            record?.ConsecutiveFailures ?? 1, record?.NextAttemptAtUtc);
    }

    private void DischargePendingIntents(ScanIntent completed) =>
        _pendingWholeRepoScanIntents.RemoveWhere(pending => ScanIntentPolicy.Satisfies(completed, pending));

    private void WarnScanSuppressedOnce(ScanAttemptDecision decision)
    {
        if (_backoffWarned)
            return;
        _backoffWarned = true;
        _logger?.LogWarning(
            "A whole-repo scan is pending but suppressed until {RetryAtUtc:O} after {Failures} consecutive " +
            "failures; per-file updates continue meanwhile.",
            decision.RetryAtUtc, decision.ConsecutiveFailures);
    }

    // v1 emits a per-diagnostic `recoverable` flag; that is the primary keep-prior signal. The data-loss guard
    // is emitted recoverable:false by julie (commands.rs), yet its semantics ARE keep-prior (an empty re-parse
    // self-heals on the next scan), so it is carved in explicitly until julie marks it recoverable. lock_timeout
    // (v1 ReportCode::LockTimeout, formerly flock_timeout) rides julie's recoverable flag, so it needs no carve-in.
    private static readonly HashSet<string> KeepPriorCodes =
        new(StringComparer.Ordinal) { "data_loss_guard" };

    // Run one op, isolating any failure so a single bad file never aborts the rest of the batch (decision-10).
    // Returns whether the op completed and, when it did not, the exception — the caller uses the first to decide
    // whether the rescan latch may be cleared and the second to record julie's exit code in the failure history.
    private (bool Succeeded, Exception? Failure) ExecuteIsolated(ExtractOp op)
    {
        try
        {
            ExtractReport report = op switch
            {
                UpdateOp u => _ops.Update(u.Path),
                DeleteOp d => _ops.Delete(d.Path),
                ScanOp s => _ops.Scan(s.Intent, s.Jobs),
                _ => throw new InvalidOperationException($"Unhandled ExtractOp '{op.GetType().Name}'."),
            };

            if (ExtractReportLog.DescribeWarning(report) is { } warning)
                _logger?.LogWarning("extract op {Op}: {Warning}", Describe(op), warning);
            else
                _logger?.LogDebug(
                    "extract op {Op} succeeded (status {Status}, revision {Revision}).",
                    Describe(op), report.Status, report.Revision);
            return (true, null);
        }
        catch (JulieExtractFailedException ex)
        {
            // Outcome-aware handling (decision-10): v1 stamps each diagnostic with a `recoverable` flag — the
            // primary keep-prior signal. A failure is recoverable if ANY diagnostic is recoverable OR carries a
            // keep-prior code (data_loss_guard, which v1 emits recoverable:false yet self-heals). A recoverable
            // failure is logged at INFO ("keep the prior index, retry later"); everything else (usage, outside-
            // root, operator errors, or a failure with NO structured code) is abnormal and surfaces LOUDLY at
            // Error. In all cases the prior index is kept and the batch continues — the next scan reconciles.
            // M8 §D3: source codes + julie's raw stderr tail from the pure helper (the stderr tail is the missing
            // piece Exception.ToString() drops).
            var described = ExtractErrorLog.Describe(ex);
            bool isRecoverable = ex.Errors.Count > 0
                && ex.Errors.Any(e => e.Recoverable || KeepPriorCodes.Contains(e.Code));

            if (isRecoverable)
            {
                _logger?.LogInformation(ex,
                    "extract op {Op} hit a recoverable/expected failure ({Codes}); keeping the prior index and " +
                    "retrying on the next scan. julie stderr: {ExtractStderrTail}",
                    Describe(op), described.Codes, described.StderrTail);
            }
            else
            {
                _logger?.LogError(ex,
                    "extract op {Op} failed with an abnormal error ({Codes}); keeping the prior index and " +
                    "continuing the batch. julie stderr: {ExtractStderrTail}",
                    Describe(op), described.Codes, described.StderrTail);
            }

            return (false, ex);
        }
        catch (Exception ex)
        {
            // Truly unexpected (an unexpected exit code, an exec failure, a JSON parse error): keep the prior
            // index, flag a repair, move on. The next scan reconciles the failed file. M8 §D3: route through the
            // helper too so a base JulieExtractException's stderr (a Rust panic) is surfaced. finding-7: this
            // branch ALSO fires for non-julie exceptions (a JSON parse error), whose stderr tail is empty — only
            // attach the "julie stderr:" label when there is actually a tail, so a non-julie failure does not log
            // a dangling label asserting a julie context that is false.
            if (StoreWorkspaceOperationException.IsRetryableProducerFailure(ex))
            {
                _logger?.LogInformation(
                    ex,
                    "extract op {Op} hit a retryable producer miss; keeping the prior index and " +
                    "retrying on the next scan.",
                    Describe(op));
                return (false, ex);
            }

            var described = ExtractErrorLog.Describe(ex);
            if (described.StderrTail.Length == 0)
            {
                _logger?.LogWarning(ex,
                    "extract op {Op} failed with an unexpected exception; keeping the prior index and " +
                    "continuing the batch.",
                    Describe(op));
            }
            else
            {
                _logger?.LogWarning(ex,
                    "extract op {Op} failed with an unexpected exception; keeping the prior index and " +
                    "continuing the batch. julie stderr: {ExtractStderrTail}",
                    Describe(op), described.StderrTail);
            }

            return (false, ex);
        }
    }

    private static string Describe(ExtractOp op) => op switch
    {
        UpdateOp u => $"update({u.Path})",
        DeleteOp d => $"delete({d.Path})",
        ScanOp { Force: true } => "scan(force)",
        ScanOp => "scan",
        _ => op.GetType().Name,
    };
}
