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
/// <para><b>Failure isolation (decision-10).</b> A single op throwing (a flock timeout, a data-loss guard, a
/// transient parser failure) is logged and the batch continues; the prior index is kept and the failed file
/// is reconciled on the next scan. One bad file never wipes the queue or aborts sibling updates.</para>
/// </summary>
public sealed class IndexerCore
{
    private readonly IExtractOps _ops;
    private readonly Func<string, bool> _exists;
    private readonly ILogger? _logger;
    private readonly object _gate = new();

    // The FileSystemWatcher Error (InternalBuffer overflow) signal. Kept in this layer rather than mutating the
    // Core queue's NeedsRescan, so the queue's pure surface (handed off by the Core layer) stays untouched: a
    // dropped-events overflow there sets its own NeedsRescan, an FSW buffer overflow sets this one, and a drain
    // ORs both (with .git/HEAD) into the router's single-scan decision. Guarded by _gate.
    private bool _overflowSignaled;

    /// <summary>The coalescing event queue. Enqueue under the watcher; drained on the debounce tick.</summary>
    public WatchEventQueue Queue { get; }

    /// <summary>
    /// Construct the core over a fresh (or supplied) queue, the extract-op runner, and a file-existence stat.
    /// </summary>
    /// <exception cref="ArgumentNullException">Any required argument is null.</exception>
    public IndexerCore(WatchEventQueue queue, IExtractOps ops, Func<string, bool> exists, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(ops);
        ArgumentNullException.ThrowIfNull(exists);
        Queue = queue;
        _ops = ops;
        _exists = exists;
        _logger = logger;
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
    /// trust the lossy event stream. Cleared after the forced scan is scheduled.
    /// </summary>
    public void SignalRescan()
    {
        lock (_gate)
            _overflowSignaled = true;
    }

    /// <summary>
    /// Drain the queue and execute the routed <see cref="ExtractOp"/>s. <paramref name="headChanged"/> is true
    /// when <c>.git/HEAD</c> moved since the last drain (a branch switch / checkout): together with an overflow
    /// <see cref="WatchEventQueue.NeedsRescan"/> it forces a single <c>scan</c> and drops the per-file events.
    /// Returns true if any op ran (work was scheduled), false on an empty no-op drain.
    /// </summary>
    public bool DrainAndProcess(bool headChanged)
    {
        // The whole drain+execute runs under one lock so a second debounce tick cannot interleave a concurrent
        // subprocess: the operations run strictly one-in-flight, in routed order.
        lock (_gate)
        {
            // NeedsRescan from the Core queue (its own overflow), the FSW buffer-overflow signal, or a
            // .git/HEAD move each force a single whole-repo scan that supersedes the lossy per-file stream.
            bool needsRescanOrHead = Queue.NeedsRescan || _overflowSignaled || headChanged;
            IReadOnlyList<WatchEvent> drained = Queue.Drain();

            if (!needsRescanOrHead && drained.Count == 0)
                return false; // nothing observed and no forced reconcile — a clean no-op tick.

            // Stat is injected; watcher paths may not yet be canonical here — they are canonicalized later in
            // JulieExtractOps (per-file via PathCanonicalizer) just before the extract call, NOT in this layer.
            IReadOnlyList<ExtractOp> ops = WatchEventRouter.Route(drained, _exists, needsRescanOrHead);

            // Both rescan signals are consumed by the routed Scan; clear them so the next drain does not re-scan.
            if (Queue.NeedsRescan)
                Queue.ClearNeedsRescan();
            _overflowSignaled = false;

            foreach (var op in ops)
                ExecuteIsolated(op);

            return ops.Count > 0;
        }
    }

    // julie's exit-1 error codes that are EXPECTED/transient (decision-10): the empty-re-parse data-loss guard
    // (a previously-populated file that re-parsed empty — julie refuses to wipe it) and the cross-process flock
    // timeout (another writer held the lock). Both leave the prior index intact and self-heal on the next scan,
    // so they are an INFO-level "retry later", never an Error/Warning that implies something is broken.
    private static readonly HashSet<string> TransientErrorCodes =
        new(StringComparer.Ordinal) { "data_loss_guard", "flock_timeout" };

    // Run one op, isolating any failure so a single bad file never aborts the rest of the batch (decision-10).
    private void ExecuteIsolated(ExtractOp op)
    {
        try
        {
            ExtractReport report = op switch
            {
                UpdateOp u => _ops.Update(u.Path),
                DeleteOp d => _ops.Delete(d.Path),
                ScanOp => _ops.Scan(),
                _ => throw new InvalidOperationException($"Unhandled ExtractOp '{op.GetType().Name}'."),
            };

            // M8 §D4: a per-file extract outcome at Debug — silent at the default Information level, but the line
            // an operator running MILLER_LOG_LEVEL=Debug needs to follow which file moved the index to which
            // revision. Cheap: the template is not rendered unless Debug is enabled.
            _logger?.LogDebug(
                "extract op {Op} succeeded (status {Status}, revision {Revision}).",
                Describe(op), report.Status, report.Revision);
        }
        catch (JulieExtractFailedException ex)
        {
            // Outcome-aware handling (decision-10): inspect the structured error codes the failed report
            // carried. A transient/expected code (data-loss guard, flock timeout) is a recoverable "keep the
            // prior index, retry later" at INFO; everything else (usage, outside-root, operator errors, or a
            // failure with NO structured code) is abnormal and must surface LOUDLY at Error. In all cases the
            // prior index is kept and the batch continues — the next scan reconciles the failed file.
            // M8 §D3: source codes + julie's raw stderr tail from the pure helper (the codes wording is
            // behavior-preserving; the stderr tail is the missing piece Exception.ToString() drops).
            var described = ExtractErrorLog.Describe(ex);
            bool isTransient = ex.Errors.Count > 0 && ex.Errors.Any(e => TransientErrorCodes.Contains(e.Code));

            if (isTransient)
            {
                _logger?.LogInformation(ex,
                    "extract op {Op} hit a transient/expected failure ({Codes}); keeping the prior index and " +
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
        }
    }

    private static string Describe(ExtractOp op) => op switch
    {
        UpdateOp u => $"update({u.Path})",
        DeleteOp d => $"delete({d.Path})",
        ScanOp => "scan",
        _ => op.GetType().Name,
    };
}
