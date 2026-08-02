using Microsoft.Extensions.Logging;
using Miller.Core.Freshness;
using Miller.Indexing;
using Miller.Server.Logging;

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
/// </summary>
public sealed class IndexerCore
{
    /// <summary>The first cooldown after a failed whole-repo scan; each further failure doubles it.</summary>
    internal static readonly TimeSpan InitialScanFailureBackoff = TimeSpan.FromSeconds(1);

    /// <summary>The ceiling the doubling backoff saturates at.</summary>
    internal static readonly TimeSpan MaxScanFailureBackoff = TimeSpan.FromMinutes(5);

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
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly object _gate = new();

    // The FileSystemWatcher Error (InternalBuffer overflow) signal. Kept in this layer rather than mutating the
    // Core queue's NeedsRescan, so the queue's pure surface (handed off by the Core layer) stays untouched: a
    // dropped-events overflow there sets its own NeedsRescan, an FSW buffer overflow sets this one, and a drain
    // ORs both (with .git/HEAD) into the router's single-scan decision. Guarded by _gate.
    private bool _overflowSignaled;

    // The persistent whole-repo-rescan latch. Every transient signal (the queue's own NeedsRescan, the FSW
    // overflow flag, a per-tick .git/HEAD move) folds into it, and ONLY a scan that actually ran and succeeded
    // clears it — either here or, for the out-of-band scans IndexerService runs itself, through
    // NoteWholeRepoScanCompleted. _pendingWholeRepoScanForce carries the strongest intent any folded request
    // asked for, so a re-armed force:true request is never retried as a delta scan, and a delta scan that
    // succeeds elsewhere never clears it. Guarded by _gate.
    private bool _pendingWholeRepoScan;
    private bool _pendingWholeRepoScanForce;

    // Bumped by every RequestWholeRepoScan. An out-of-band scan captures it BEFORE it starts and hands it back on
    // completion, so a request armed while that scan was already in flight no longer matches and cannot be
    // cleared by it: the finished scan never consumed the later request, and matching intent alone cannot tell
    // the two apart. The drain's own scan needs no such guard — it runs under _gate, which every arming path
    // takes, so nothing can arm mid-drain. Guarded by _gate.
    private long _wholeRepoScanArmingGeneration;

    // Failure backoff for the latch. Without it a whole-repo scan that keeps failing (an OOM-killed extract,
    // exit 137) is respawned on every 250ms debounce tick, and each attempt first takes the user-global scan
    // lease — starving every sibling worktree and leaking a temp spool per kill. Guarded by _gate.
    private int _consecutiveScanFailures;
    private DateTimeOffset? _scanRetryNotBeforeUtc;
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
            lock (_gate)
                return Queue.Count > 0 || Queue.NeedsRescan || _overflowSignaled || _pendingWholeRepoScan;
        }
    }

    /// <summary>
    /// Construct the core over a fresh (or supplied) queue, the extract-op runner, and a file-existence stat.
    /// </summary>
    /// <exception cref="ArgumentNullException">Any required argument is null.</exception>
    public IndexerCore(
        WatchEventQueue queue,
        IExtractOps ops,
        Func<string, bool> exists,
        ILogger? logger = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(ops);
        ArgumentNullException.ThrowIfNull(exists);
        Queue = queue;
        _ops = ops;
        _exists = exists;
        _logger = logger;
        _utcNow = utcNow ?? (static () => DateTimeOffset.UtcNow);
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
    /// serviced (refused scan admission, or a scan that failed) — so the reconcile is retried instead of lost.
    /// <paramref name="force"/> carries the request's intent: any forced request makes the pending scan forced,
    /// and the bit survives until a scan that satisfies that intent succeeds.
    /// </summary>
    public void RequestWholeRepoScan(bool force)
    {
        lock (_gate)
        {
            _pendingWholeRepoScan = true;
            _pendingWholeRepoScanForce |= force;
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
    /// Report that a whole-repo scan run OUTSIDE this core succeeded, so the latch it would otherwise satisfy is
    /// cleared instead of driving a duplicate rebuild on the next tick. INTENT-AWARE: a successful
    /// <paramref name="force"/> <c>false</c> delta scan does NOT satisfy a pending forced request, because the
    /// delta would leave the from-scratch rebuild the requester asked for unrun. GENERATION-AWARE:
    /// <paramref name="armingGeneration"/> is the <see cref="WholeRepoScanArmingGeneration"/> read before the scan
    /// started, and a latch re-armed since then is left alone — the scan that just finished began before that
    /// request existed, so clearing it would silently drop a rebuild Miller already promised the caller.
    /// </summary>
    public void NoteWholeRepoScanCompleted(bool force, long armingGeneration)
    {
        lock (_gate)
        {
            // A scan that completed is direct evidence the extractor is healthy, so the failure backoff resets
            // even when the latch must stay armed — otherwise a stale backoff would suppress the very request
            // that was armed while this scan ran.
            ResetScanFailureBackoff();

            if (_wholeRepoScanArmingGeneration != armingGeneration)
                return;
            if (_pendingWholeRepoScanForce && !force)
                return;
            ClearWholeRepoScanLatch();
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
            bool pending = _pendingWholeRepoScan || Queue.NeedsRescan || _overflowSignaled || headChanged;
            return pending && !InScanFailureBackoff();
        }
    }

    /// <summary>
    /// The consecutive whole-repo scan failures currently driving the backoff. Zero once one succeeds.
    /// </summary>
    internal int ConsecutiveScanFailures
    {
        get
        {
            lock (_gate)
                return _consecutiveScanFailures;
        }
    }

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
                _pendingWholeRepoScan = true;
            }
            if (_overflowSignaled)
            {
                _overflowSignaled = false;
                _pendingWholeRepoScan = true;
            }
            if (headChanged)
                _pendingWholeRepoScan = true;

            bool scanDue = _pendingWholeRepoScan && !InScanFailureBackoff();
            if (_pendingWholeRepoScan && !scanDue)
                WarnScanSuppressedOnce();

            if (scanDue && !wholeRepoScanAdmitted)
                scanDue = false;

            // A whole-repo scan is owed but will not run this tick — refused admission, or the post-failure
            // backoff. Above the bound the queued events stay where they are: the still-armed latch reconciles
            // every one of them, so draining here would run up to WatchEventQueue.MaxQueue sequential extracts,
            // holding this gate and the caller's for the whole storm, to reach an index the latched scan produces
            // anyway. An overflow while they wait is not a loss either — it folds straight back into the latch.
            bool scanOwedButNotRunning = _pendingWholeRepoScan && !scanDue;
            if (scanOwedButNotRunning && Queue.Count > MaxDeferredScanDrain)
            {
                _logger?.LogDebug(
                    "A whole-repo scan is owed but deferred with {QueuedPaths} paths queued (over the {Bound}-path " +
                    "drain bound); leaving them for the latched reconcile instead of extracting each one.",
                    Queue.Count, MaxDeferredScanDrain);
                return false;
            }

            bool force = _pendingWholeRepoScanForce;
            IReadOnlyList<WatchEvent> drained = Queue.Drain();

            if (!scanDue && drained.Count == 0)
                return false; // no per-file events and no runnable reconcile — a no-op tick.

            // Stat is injected; watcher paths may not yet be canonical here — they are canonicalized later in
            // JulieExtractOps (per-file via PathCanonicalizer) just before the extract call, NOT in this layer.
            IReadOnlyList<ExtractOp> ops = WatchEventRouter.Route(drained, _exists, scanDue, force);

            foreach (var op in ops)
            {
                bool succeeded = ExecuteIsolated(op);
                if (op is not ScanOp)
                    continue;
                if (succeeded)
                    usedWholeRepoScan = true;
                else
                    ArmScanFailureBackoff();
            }

            // Success, not admission, is the commit point: a swallowed extractor failure must leave the latch
            // armed so the reconcile is retried rather than silently dropped.
            if (usedWholeRepoScan)
            {
                ResetScanFailureBackoff();
                ClearWholeRepoScanLatch();
            }

            return ops.Count > 0;
        }
    }

    // All five helpers below run under _gate.
    private bool InScanFailureBackoff() =>
        _scanRetryNotBeforeUtc is { } notBefore && _utcNow() < notBefore;

    private void ArmScanFailureBackoff()
    {
        _consecutiveScanFailures++;
        TimeSpan backoff = BackoffFor(_consecutiveScanFailures);
        _scanRetryNotBeforeUtc = _utcNow() + backoff;
        _backoffWarned = false;
        _logger?.LogWarning(
            "Whole-repo scan failed {Failures} time(s) in a row; deferring the next attempt by {BackoffSeconds}s " +
            "so a failing extractor cannot respawn on every debounce tick.",
            _consecutiveScanFailures, backoff.TotalSeconds);
    }

    private void ClearWholeRepoScanLatch()
    {
        _pendingWholeRepoScan = false;
        _pendingWholeRepoScanForce = false;
    }

    private void ResetScanFailureBackoff()
    {
        _consecutiveScanFailures = 0;
        _scanRetryNotBeforeUtc = null;
        _backoffWarned = false;
    }

    private void WarnScanSuppressedOnce()
    {
        if (_backoffWarned)
            return;
        _backoffWarned = true;
        _logger?.LogWarning(
            "A whole-repo scan is pending but suppressed until {RetryAtUtc:O} after {Failures} consecutive " +
            "failures; per-file updates continue meanwhile.",
            _scanRetryNotBeforeUtc, _consecutiveScanFailures);
    }

    // Doubling from InitialScanFailureBackoff, saturating at MaxScanFailureBackoff. The shift is bounded so a
    // long-running failure streak cannot overflow the multiplier.
    private static TimeSpan BackoffFor(int consecutiveFailures)
    {
        const int MaxDoublings = 30;
        int doublings = Math.Min(Math.Max(consecutiveFailures - 1, 0), MaxDoublings);
        double ms = InitialScanFailureBackoff.TotalMilliseconds * Math.Pow(2, doublings);
        return ms >= MaxScanFailureBackoff.TotalMilliseconds
            ? MaxScanFailureBackoff
            : TimeSpan.FromMilliseconds(ms);
    }

    // v1 emits a per-diagnostic `recoverable` flag; that is the primary keep-prior signal. The data-loss guard
    // is emitted recoverable:false by julie (commands.rs), yet its semantics ARE keep-prior (an empty re-parse
    // self-heals on the next scan), so it is carved in explicitly until julie marks it recoverable. lock_timeout
    // (v1 ReportCode::LockTimeout, formerly flock_timeout) rides julie's recoverable flag, so it needs no carve-in.
    private static readonly HashSet<string> KeepPriorCodes =
        new(StringComparer.Ordinal) { "data_loss_guard" };

    // Run one op, isolating any failure so a single bad file never aborts the rest of the batch (decision-10).
    // Returns whether the op completed; the caller uses that to decide whether the rescan latch may be cleared.
    private bool ExecuteIsolated(ExtractOp op)
    {
        try
        {
            ExtractReport report = op switch
            {
                UpdateOp u => _ops.Update(u.Path),
                DeleteOp d => _ops.Delete(d.Path),
                ScanOp s => _ops.Scan(s.Force),
                _ => throw new InvalidOperationException($"Unhandled ExtractOp '{op.GetType().Name}'."),
            };

            if (ExtractReportLog.DescribeWarning(report) is { } warning)
                _logger?.LogWarning("extract op {Op}: {Warning}", Describe(op), warning);
            else
                _logger?.LogDebug(
                    "extract op {Op} succeeded (status {Status}, revision {Revision}).",
                    Describe(op), report.Status, report.Revision);
            return true;
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

            return false;
        }
        catch (Exception ex)
        {
            // Truly unexpected (an unexpected exit code, an exec failure, a JSON parse error): keep the prior
            // index, flag a repair, move on. The next scan reconciles the failed file. M8 §D3: route through the
            // helper too so a base JulieExtractException's stderr (a Rust panic) is surfaced. finding-7: this
            // branch ALSO fires for non-julie exceptions (a JSON parse error), whose stderr tail is empty — only
            // attach the "julie stderr:" label when there is actually a tail, so a non-julie failure does not log
            // a dangling label asserting a julie context that is false.
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

            return false;
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
