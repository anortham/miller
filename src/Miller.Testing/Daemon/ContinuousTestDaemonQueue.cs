using System.Globalization;

namespace Miller.Testing;

public sealed class ContinuousTestDaemonQueue : IContinuousTestDaemonEnqueuer
{
    private const int MaxFlakyRetryAttempts = 1;
    private const int BackfillBatchSize = 32;
    private const string DiscoveryFailureKind = "ct-project-discovery-failure";

    private readonly ContinuousTestStore _store;
    private readonly ContinuousTestImpactSelector _selector;
    private readonly ContinuousTestCoordinator _coordinator;
    private readonly ContinuousTestStoreApplier _storeApplier;
    private readonly Action<string>? _ctStateChanged;
    private readonly Action<string>? _lifecycleLog;

    /// <summary>
    /// Where this queue reports that a provider run started and ended, so the daemon can publish it. Null in
    /// a unit test that does not care; the drain path works the same either way.
    /// </summary>
    private readonly CtRunActivityCell? _runActivity;
    private readonly ContinuousTestCoverageNarrowingMode _coverageNarrowingMode;
    private readonly Dictionary<PendingKey, ContinuousTestDaemonPendingRun> _pending = [];

    /// <summary>
    /// Foreground pendings whose selection is PURELY workspace-scope-derived. Only these may
    /// collapse to the whole-suite provider form; an impact-derived selection that happens to
    /// cover every known case still travels as its explicit id list (contract clause e). A merge
    /// with any impact-derived enqueue clears the mark.
    /// </summary>
    private readonly HashSet<PendingKey> _wholeSuiteEligible = [];
    private readonly Dictionary<RetryKey, int> _retryAttempts = [];
    private readonly Dictionary<string, CtFreshnessKey> _latestByWorkspace = new(StringComparer.Ordinal);
    private readonly Dictionary<PendingKey, CancellationTokenSource> _backfillCancellationByProject = [];
    private readonly Dictionary<PendingKey, string> _runFailureRetrySpentAtRevision = [];
    private readonly object _lock = new();

    public ContinuousTestDaemonQueue(
        ContinuousTestStore store,
        ContinuousTestImpactSelector selector,
        ContinuousTestCoordinator coordinator,
        Action<string>? ctStateChanged = null,
        Action<string>? lifecycleLog = null,
        ContinuousTestCoverageNarrowingMode coverageNarrowingMode = ContinuousTestCoverageNarrowingMode.Off,
        CtRunActivityCell? runActivity = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _selector = selector ?? throw new ArgumentNullException(nameof(selector));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        if (!Enum.IsDefined(coverageNarrowingMode))
            throw new ArgumentOutOfRangeException(nameof(coverageNarrowingMode));
        _storeApplier = new ContinuousTestStoreApplier(_store);
        _ctStateChanged = ctStateChanged;
        _lifecycleLog = lifecycleLog;
        _coverageNarrowingMode = coverageNarrowingMode;
        _runActivity = runActivity;
    }

    public bool HasReadyWork(DateTimeOffset now)
    {
        lock (_lock)
            return _pending.Values.Any(pending => pending.ReadyAt <= now);
    }

    public void ObserveFreshRevision(string workspaceId, CtFreshnessKey freshness)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        lock (_lock)
            _latestByWorkspace[workspaceId] = freshness;
    }

    /// <summary>
    /// An explicit run request (<c>tests run</c>, the daemon run command). A workspace-scope
    /// explicit run executes the current stale set PLUS every RED case at the request's key: a
    /// user-requested run means "prove it again". GREEN cases fresh at that key — committed there
    /// or under a covering watermark — are neither re-marked stale nor re-run, so a green result
    /// survives an explicit run that has nothing to prove about it.
    ///
    /// <para>A red is committed-fresh by the same rule a green is, so the trim used to drop it and
    /// the run selected nothing: the verdict stayed red, the stale count stayed 0, and
    /// <c>last_run</c> never moved however many times the user asked (observed 2026-08-21). Only the
    /// EXPLICIT path includes reds — an automatic run that re-ran every failing test on every
    /// debounce would be a red loop on every save.</para>
    /// </summary>
    public ContinuousTestDaemonEnqueueResult EnqueueExplicit(ContinuousTestDaemonChange change) =>
        EnqueueCore(change, requireCompleteDelta: false, explicitRun: true);

    public ContinuousTestDaemonEnqueueResult Enqueue(ContinuousTestDaemonChange change) =>
        EnqueueCore(change, requireCompleteDelta: true, explicitRun: false);

    /// <summary>
    /// The debounce lives HERE, between observation and execution, as pending-run state: staleness
    /// and the watermark advance are applied immediately (status stays honest while the timer
    /// counts down), but the pending's <c>ReadyAt</c> is <c>ObservedAt + DebounceDelay</c> and the
    /// drain executes nothing before it. A newer change for the same project MERGES into the
    /// pending and rewrites <c>ReadyAt</c> from its own observation time — trailing-edge
    /// coalescing, so a save burst yields exactly one run after the last save's quiet period. A
    /// change enqueued while a run executes simply becomes the next pending: it never cancels the
    /// running suite (the stall detector owns killing).
    /// </summary>
    private ContinuousTestDaemonEnqueueResult EnqueueCore(
        ContinuousTestDaemonChange change,
        bool requireCompleteDelta,
        bool explicitRun)
    {
        ArgumentNullException.ThrowIfNull(change);
        ValidateBuildOutputRoot(change.Workspace);
        var empty = new ContinuousTestSelectionResult([], [], []);
        var rejected = new ContinuousTestDaemonPendingRun(
            change.Workspace,
            change.CurrentRevision,
            change.CurrentRevision,
            change.IndexIdentity,
            [],
            change.FilterArguments,
            change.Command,
            change.Framework,
            false,
            change.ObservedAt,
            change.ObservedAt);
        if (requireCompleteDelta && change.DeltaCompleteness != ContinuousTestDeltaCompleteness.Complete)
            return new ContinuousTestDaemonEnqueueResult(empty, rejected);

        ContinuousTestSelectionResult selection = _selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: change.Workspace.WorkspaceId,
            ChangedPaths: change.ChangedPaths,
            ImpactedSymbols: change.ImpactedSymbols,
            ImpactedTests: change.ImpactedTests,
            WorkspaceScope: change.WorkspaceScope,
            ProjectPath: change.Workspace.ProjectPath));
        IReadOnlyList<string> foregroundTestCaseIds = SelectForegroundTestCaseIds(change, selection);
        if (explicitRun && change.WorkspaceScope)
        {
            // The explicit run executes the current stale set plus every red, so trim fresh cases
            // (committed or watermark-fresh) BEFORE the stale marking below would overwrite their
            // rows. The two trims differ on purpose: what EXECUTES keeps the reds, what is marked
            // STALE does not. Marking a red stale would erase the standing verdict for the whole
            // length of the run it is about to be proved by.
            selection = selection with
            {
                SelectedTestCaseIds = DropFreshAt(
                    change.Workspace.WorkspaceId,
                    change.Workspace.ProjectPath,
                    selection.SelectedTestCaseIds,
                    change.Freshness,
                    keepRed: true),
                StaleTestCaseIds = DropFreshAt(
                    change.Workspace.WorkspaceId,
                    change.Workspace.ProjectPath,
                    selection.StaleTestCaseIds,
                    change.Freshness),
            };
            foregroundTestCaseIds = selection.SelectedTestCaseIds;
        }

        // ONE crash-atomic store write per observed revision: the impacted set goes stale AND the
        // keep-set's fresh watermarks advance to the new key. A complete delta names the interval;
        // without one the advance is anchored at the new key itself, which can only confirm cases
        // already fresh there (the outcome then advances nothing anyway).
        CtFreshnessKey advanceFrom = ContinuousTestDurableFreshness.TryGetCompleteDelta(
            change, out long deltaFromRevision, out _, out _)
            ? new CtFreshnessKey(change.IndexIdentity, deltaFromRevision)
            : change.Freshness;
        _store.ApplyRevisionAdvance(
            change.Workspace.WorkspaceId,
            change.Workspace.ProjectPath,
            advanceFrom,
            change.Freshness,
            selection.StaleTestCaseIds,
            selection.Outcome);
        NotifyCtStateChanged(change.Workspace.WorkspaceId);

        if (!selection.MayExecute)
        {
            // Fail closed: the staleness above is recorded, but an unknown or known-empty
            // selection never enqueues provider execution — no foreground run, no backfill,
            // and NEVER a full-suite fallback. It still SUPERSEDES older pendings for the
            // project, exactly like an executable enqueue would.
            ReconcilePendingOnNoRun(change, selection.Outcome);
            Log($"ct enqueue no-run workspace={change.Workspace.WorkspaceId} revision={change.CurrentRevision} "
                + $"outcome={selection.Outcome} stale={selection.StaleTestCaseIds.Count}");
            return new ContinuousTestDaemonEnqueueResult(selection, rejected);
        }

        PendingKey key = PendingKey.FromWorkspace(change.Workspace, ContinuousTestRunLane.Foreground);
        ContinuousTestDaemonPendingRun pending;
        lock (_lock)
        {
            TrackLatest(key, change.Freshness);
            PendingKey backfillKey = PendingKey.FromWorkspace(change.Workspace, ContinuousTestRunLane.Backfill);
            _pending.Remove(backfillKey);
            if (_backfillCancellationByProject.Remove(key, out CancellationTokenSource? inFlightBackfill))
                inFlightBackfill.Cancel();

            IReadOnlyList<string> selectedIds = foregroundTestCaseIds;
            bool merged = _pending.TryGetValue(key, out ContinuousTestDaemonPendingRun? existing);
            if (merged)
            {
                selectedIds = existing!.TestCaseIds
                    .Concat(foregroundTestCaseIds)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
            }

            if (change.WorkspaceScope && (!merged || _wholeSuiteEligible.Contains(key)))
                _wholeSuiteEligible.Add(key);
            else
                _wholeSuiteEligible.Remove(key);

            pending = new ContinuousTestDaemonPendingRun(
                Workspace: change.Workspace,
                SelectedRevision: change.CurrentRevision,
                CurrentRevision: change.CurrentRevision,
                IndexIdentity: change.IndexIdentity,
                TestCaseIds: selectedIds,
                FilterArguments: change.FilterArguments,
                Command: change.Command,
                Framework: change.Framework,
                RefreshInventory: change.WorkspaceScope,
                ObservedAt: change.ObservedAt,
                ReadyAt: change.ObservedAt + change.DebounceDelay)
            {
                Lane = ContinuousTestRunLane.Foreground,
                ImpactPriority = PriorityFor(change.WorkspaceScope, selection),
                // Carried to the drain, which trims fresh cases again: without it the reds this
                // enqueue selected are dropped there and the explicit run executes nothing. A merge
                // KEEPS the mark - a user asked for this run, and a later automatic change arriving
                // during the debounce does not withdraw the request.
                ExplicitRun = explicitRun || (merged && existing!.ExplicitRun),
            };
            _pending[key] = pending;

            var selectedSet = selectedIds.ToHashSet(StringComparer.Ordinal);
            string[] residualIds = selection.StaleTestCaseIds
                .Where(id => !selectedSet.Contains(id))
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (residualIds.Length > 0)
            {
                _pending[backfillKey] = pending with
                {
                    Lane = ContinuousTestRunLane.Backfill,
                    TestCaseIds = residualIds,
                    RefreshInventory = false,
                    ImpactPriority = ContinuousTestImpactPriority.WorkspaceScope,
                };
            }
        }

        Log($"ct enqueue workspace={change.Workspace.WorkspaceId} revision={change.CurrentRevision} "
            + $"identity={change.IndexIdentity} selected={selection.SelectedTestCaseIds.Count}");
        return new ContinuousTestDaemonEnqueueResult(selection, pending);
    }

    /// <summary>
    /// A no-run observation still supersedes older pendings for its project. Returning without
    /// touching <see cref="_pending"/> left a run selected at an older key executable behind a
    /// newer observation that invalidated it: it drained later, at the stale key, with the stale
    /// id list. KnownEmpty (complete knowledge, empty delta) RE-KEYS the pendings to the new
    /// observation — the owed run survives, commits at the new key, and the debounce resets
    /// trailing-edge exactly as an executable merge would, so a doc-only save never cancels the
    /// run a code save is owed. Unknown (no knowledge) DROPS them: everything is stale at the new
    /// key, the old id list has no standing there, and the owed work stays visible as store
    /// staleness until the next executable selection (impacted ∪ backlog) or an explicit run
    /// rebuilds it. Either way the latest key moves, so an in-flight run's revision resolver and
    /// the backfill begin/requeue gates see the supersession; only queued work is touched — a
    /// healthy foreground run is never killed, and cancelling an in-flight backfill batch is the
    /// same supersession the executable path already performs.
    /// </summary>
    private void ReconcilePendingOnNoRun(ContinuousTestDaemonChange change, ContinuousTestSelectionOutcome outcome)
    {
        PendingKey key = PendingKey.FromWorkspace(change.Workspace, ContinuousTestRunLane.Foreground);
        PendingKey backfillKey = PendingKey.FromWorkspace(change.Workspace, ContinuousTestRunLane.Backfill);
        lock (_lock)
        {
            TrackLatest(key, change.Freshness);
            if (outcome == ContinuousTestSelectionOutcome.KnownEmpty)
            {
                RekeyPendingTo(key, change);
                RekeyPendingTo(backfillKey, change);
                return;
            }

            _pending.Remove(key);
            _pending.Remove(backfillKey);
            _wholeSuiteEligible.Remove(key);
            if (_backfillCancellationByProject.Remove(key, out CancellationTokenSource? inFlightBackfill))
                inFlightBackfill.Cancel();
        }
    }

    /// <summary>
    /// Re-keys one queued pending to a newer observation: same id list and lane, new revision,
    /// identity, and trailing-edge debounce deadline. Built through the constructor, not
    /// <c>with</c>, because the record derives its parsed revision there and a <c>with</c> on
    /// <see cref="ContinuousTestDaemonPendingRun.CurrentRevision"/> would leave the parsed value —
    /// and therefore <see cref="ContinuousTestDaemonPendingRun.Freshness"/> — at the old key.
    /// Callers hold <see cref="_lock"/>.
    /// </summary>
    private void RekeyPendingTo(PendingKey key, ContinuousTestDaemonChange change)
    {
        if (!_pending.TryGetValue(key, out ContinuousTestDaemonPendingRun? existing))
            return;
        _pending[key] = new ContinuousTestDaemonPendingRun(
            Workspace: existing.Workspace,
            SelectedRevision: change.CurrentRevision,
            CurrentRevision: change.CurrentRevision,
            IndexIdentity: change.IndexIdentity,
            TestCaseIds: existing.TestCaseIds,
            FilterArguments: existing.FilterArguments,
            Command: existing.Command,
            Framework: existing.Framework,
            RefreshInventory: existing.RefreshInventory,
            ObservedAt: change.ObservedAt,
            ReadyAt: change.ObservedAt + change.DebounceDelay)
        {
            Lane = existing.Lane,
            ExcludeTraits = existing.ExcludeTraits,
            ImpactPriority = existing.ImpactPriority,
            CoverageMode = existing.CoverageMode,
            ExplicitRun = existing.ExplicitRun,
        };
    }

    public async Task<IReadOnlyList<ContinuousTestDaemonDrainResult>> DrainReadyAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        (PendingKey Key, ContinuousTestDaemonPendingRun Pending, bool WholeSuiteEligible)[] ready = SnapshotReady(now);
        var results = new List<ContinuousTestDaemonDrainResult>(ready.Length);
        foreach ((PendingKey key, ContinuousTestDaemonPendingRun pending, bool wholeSuiteEligible) in ready)
        {
            CancellationTokenSource? backfillCancellation = null;
            CancellationToken drainToken = cancellationToken;
            if (pending.Lane == ContinuousTestRunLane.Backfill)
            {
                backfillCancellation = TryBeginBackfill(key, pending, cancellationToken);
                if (backfillCancellation is null)
                    continue;
                drainToken = backfillCancellation.Token;
            }

            try
            {
                ContinuousTestDaemonPendingRun readyPending = await RefreshInventoryIfNeededAsync(pending, drainToken)
                    .ConfigureAwait(false);
                if (readyPending.TestCaseIds.Count == 0)
                    continue;

                IReadOnlyList<string> survivors = DropFreshAt(
                    readyPending.Workspace.WorkspaceId,
                    readyPending.Workspace.ProjectPath,
                    readyPending.TestCaseIds,
                    readyPending.Freshness,
                    keepRed: readyPending.ExplicitRun);
                if (survivors.Count == 0)
                {
                    Log($"ct drain skip workspace={readyPending.Workspace.WorkspaceId} reason=all_fresh_at_revision");
                    continue;
                }

                IReadOnlyList<string> remainder = [];
                if (readyPending.Lane == ContinuousTestRunLane.Backfill)
                {
                    IReadOnlyList<string> ordered = OrderBackfillCases(readyPending.Workspace.WorkspaceId, survivors);
                    readyPending = readyPending with { TestCaseIds = ordered.Take(BackfillBatchSize).ToArray() };
                    remainder = ordered.Skip(BackfillBatchSize).ToArray();
                }
                else
                {
                    readyPending = readyPending with { TestCaseIds = survivors };
                }

                string runId = NewRunId();

                // The daemon blocks here for the whole run. Without this the published status froze at the
                // reason "executing" until the run ended, so nothing could name the project it was on.
                _runActivity?.BeginRun(
                    readyPending.Workspace.ProjectPath,
                    runId,
                    readyPending.TestCaseIds.Count);
                try
                {
                    ContinuousTestCoordinatorRunResult coordinatorResult = await _coordinator.RunSelectedAsync(
                        new ContinuousTestCoordinatorRunRequest(
                            Workspace: readyPending.Workspace,
                            SelectedRevision: readyPending.SelectedRevision,
                            CurrentRevision: readyPending.CurrentRevision,
                            IndexIdentity: readyPending.IndexIdentity,
                            TestCaseIds: readyPending.TestCaseIds,
                            FilterArguments: readyPending.FilterArguments,
                            Command: readyPending.Command,
                            Framework: readyPending.Framework,
                            StartedAt: now,
                            CurrentRevisionResolver: () => LatestRevisionStringFor(key, readyPending.CurrentRevision),
                            RunId: runId,
                            CoverageMode: readyPending.CoverageMode,
                            WholeSuite: wholeSuiteEligible && CoversEveryKnownCase(readyPending)),
                        drainToken).ConfigureAwait(false);

                    ClearRunFailureRetry(key);
                    coordinatorResult = ScheduleFlakyRetries(key, readyPending, coordinatorResult, now);
                    RequeueBackfillRemainder(key, pending, remainder, now);
                    NotifyCtStateChanged(readyPending.Workspace.WorkspaceId);
                    LogRunCompletion(readyPending, coordinatorResult);
                    results.Add(new ContinuousTestDaemonDrainResult(readyPending, coordinatorResult));
                }
                catch (OperationCanceledException) when (
                    pending.Lane == ContinuousTestRunLane.Backfill
                    && backfillCancellation!.IsCancellationRequested
                    && !cancellationToken.IsCancellationRequested)
                {
                    FailCancelledRunBestEffort(key, readyPending, runId);
                    RequeueBackfillRemainder(
                        key, pending, readyPending.TestCaseIds.Concat(remainder).ToArray(), now);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    FailCancelledRunBestEffort(key, readyPending, runId);
                    throw;
                }
                catch (Exception)
                {
                    if (TrySpendRunFailureRetry(key, readyPending.SelectedRevision))
                    {
                        if (readyPending.Lane == ContinuousTestRunLane.Backfill)
                            RequeueBackfillRemainder(key, pending, readyPending.TestCaseIds.Concat(remainder).ToArray(), now);
                        else
                            RequeueForegroundRetry(key, pending, readyPending.TestCaseIds, now);
                    }

                    throw;
                }
                finally
                {
                    // Every exit from the run clears it, including the two cancellation paths and the retry
                    // path that rethrows. A missed clear would leave the status claiming a run forever.
                    _runActivity?.EndRun();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exc)
            {
                NotifyCtStateChanged(pending.Workspace.WorkspaceId);
                Log($"ct drain error workspace={pending.Workspace.WorkspaceId} error={FailureSummary(exc)}");
            }
            finally
            {
                EndBackfill(key, backfillCancellation);
            }
        }

        return results;
    }

    private (PendingKey Key, ContinuousTestDaemonPendingRun Pending, bool WholeSuiteEligible)[] SnapshotReady(
        DateTimeOffset now)
    {
        lock (_lock)
        {
            return _pending
                .Where(row => row.Value.ReadyAt <= now)
                .OrderBy(row => row.Key.Lane)
                .ThenBy(row => row.Value.ImpactPriority)
                .ThenBy(row => row.Key.WorkspaceId, StringComparer.Ordinal)
                .ThenBy(row => row.Key.ProjectPath, StringComparer.Ordinal)
                .ToArray()
                .Select(row =>
                {
                    _pending.Remove(row.Key);

                    // Eligibility leaves the queue with its pending. A retry or remainder
                    // requeued later is a bounded id-list run, never a whole suite.
                    return (row.Key, row.Value, _wholeSuiteEligible.Remove(row.Key));
                })
                .ToArray();
        }
    }

    private static int PriorityFor(bool workspaceScope, ContinuousTestSelectionResult selection)
    {
        if (workspaceScope)
            return ContinuousTestImpactPriority.WorkspaceScope;
        double maxConfidence = selection.Evidence.Count == 0 ? 0.0 : selection.Evidence.Max(row => row.Confidence);
        return ContinuousTestImpactPriority.ForConfidence(maxConfidence);
    }

    /// <summary>
    /// True when this run's selection covers EVERY test case the store knows for the project, which is what
    /// lets the coordinator hand the provider an empty selection and run the whole assembly once instead of
    /// chunking ~6,000 <c>-method</c> pairs across ~50 processes.
    ///
    /// <para>Coverage alone is NOT permission. The drain also requires the pending to be
    /// workspace-scope-derived (<see cref="_wholeSuiteEligible"/>): an impact-derived selection
    /// that happens to equal the inventory still travels as its explicit id list — contract
    /// clause (e) of the impacted/stale contract.</para>
    ///
    /// <para>The comparison is on the SET, not on counts. Equal counts can mean two different sets when the
    /// inventory has drifted mid-run, and "run everything" is the wrong instruction for a selection that is
    /// merely the same SIZE as everything.</para>
    ///
    /// <para>The backfill lane is excluded by construction: it deliberately takes a bounded batch, so its
    /// selection is never the whole inventory, and treating it as one would defeat the batching.</para>
    ///
    /// <para>An empty inventory is NOT a whole-suite run. Nothing known means nothing to cover, and telling a
    /// provider to run the whole assembly on that basis would execute an entire suite that no selection
    /// asked for.</para>
    /// </summary>
    private bool CoversEveryKnownCase(ContinuousTestDaemonPendingRun pending)
    {
        if (pending.Lane == ContinuousTestRunLane.Backfill)
            return false;

        HashSet<string> known;
        try
        {
            known = _store.ListTestCases(pending.Workspace.WorkspaceId)
                .Where(row => ContinuousTestImpactSelector.IsProviderManagedTestCaseForProject(
                    row, pending.Workspace.ProjectPath))
                .Select(row => row.Id)
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception)
        {
            // Unreadable inventory is not proof of coverage. Fall back to the per-case selection, which is
            // slower and always correct.
            return false;
        }

        if (known.Count == 0)
            return false;

        var selected = pending.TestCaseIds.ToHashSet(StringComparer.Ordinal);
        return selected.IsSupersetOf(known);
    }

    private IReadOnlyList<string> SelectForegroundTestCaseIds(
        ContinuousTestDaemonChange change,
        ContinuousTestSelectionResult staticSelection)
    {
        IReadOnlyList<string> staticIds = staticSelection.SelectedTestCaseIds;
        if (_coverageNarrowingMode == ContinuousTestCoverageNarrowingMode.Off)
            return staticIds;
        if (staticIds.Count == 0 || change.WorkspaceScope)
            return staticIds;
        if (change.DeltaCompleteness != ContinuousTestDeltaCompleteness.Complete
            || change.DeltaFromRevision is not { } fromRevision
            || change.DeltaToRevision is not { } toRevision)
        {
            return staticIds;
        }

        try
        {
            var from = new CtFreshnessKey(change.IndexIdentity, fromRevision);
            var to = new CtFreshnessKey(change.IndexIdentity, toRevision);
            CtCoverageDeltaApplyResult deltaResult = _store.ApplyCtCoverageDelta(
                change.Workspace.WorkspaceId, from, to, change.ChangedPaths);
            if (deltaResult.Status == CtCoverageDeltaApplyStatus.Rejected)
                return staticIds;
            IReadOnlyList<CtCoverageNarrowingEvidence> evidence = _store.ListCtCoverageNarrowingEvidence(
                change.Workspace.WorkspaceId,
                change.Workspace.ProjectPath,
                staticIds,
                change.Freshness);
            ContinuousTestCoverageNarrowingResult result = ContinuousTestCoverageNarrower.Narrow(
                staticSelection,
                change.Workspace.WorkspaceId,
                change.Workspace.ProjectPath,
                change.Freshness,
                evidence);
            return _coverageNarrowingMode == ContinuousTestCoverageNarrowingMode.Active
                ? result.FinalSelectedTestCaseIds
                : staticIds;
        }
        catch (Exception)
        {
            return staticIds;
        }
    }

    private void TrackLatest(PendingKey key, CtFreshnessKey freshness) =>
        _latestByWorkspace[key.WorkspaceId] = freshness;

    private string LatestRevisionStringFor(PendingKey key, string fallback)
    {
        lock (_lock)
        {
            return _latestByWorkspace.TryGetValue(key.WorkspaceId, out CtFreshnessKey latest)
                ? latest.Revision.ToString(CultureInfo.InvariantCulture)
                : fallback;
        }
    }

    private CancellationTokenSource? TryBeginBackfill(
        PendingKey key,
        ContinuousTestDaemonPendingRun pending,
        CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_latestByWorkspace.TryGetValue(key.WorkspaceId, out CtFreshnessKey latest)
                && latest != pending.Freshness)
            {
                return null;
            }

            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _backfillCancellationByProject[key.AsForeground()] = cancellation;
            return cancellation;
        }
    }

    private void EndBackfill(PendingKey key, CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
            return;
        lock (_lock)
        {
            PendingKey projectKey = key.AsForeground();
            if (_backfillCancellationByProject.TryGetValue(projectKey, out CancellationTokenSource? current)
                && ReferenceEquals(current, cancellation))
            {
                _backfillCancellationByProject.Remove(projectKey);
            }
        }

        cancellation.Dispose();
    }

    private IReadOnlyList<string> OrderBackfillCases(string workspaceId, IReadOnlyList<string> testCaseIds)
    {
        var statuses = _store.ListContinuousTestStatuses(workspaceId)
            .ToDictionary(status => status.TestCaseId, StringComparer.Ordinal);
        return testCaseIds
            .OrderBy(id => statuses.TryGetValue(id, out ContinuousTestStatus? status)
                ? status.Revision
                : long.MinValue)
            .ThenBy(id => id, StringComparer.Ordinal)
            .ToArray();
    }

    private void RequeueBackfillRemainder(
        PendingKey key,
        ContinuousTestDaemonPendingRun pending,
        IReadOnlyList<string> remainder,
        DateTimeOffset now)
    {
        if (pending.Lane != ContinuousTestRunLane.Backfill || remainder.Count == 0)
            return;
        lock (_lock)
        {
            if (_latestByWorkspace.TryGetValue(key.WorkspaceId, out CtFreshnessKey latest)
                && latest != pending.Freshness)
            {
                return;
            }

            IReadOnlyList<string> ids = _pending.TryGetValue(key, out ContinuousTestDaemonPendingRun? existing)
                ? existing.TestCaseIds.Concat(remainder).Distinct(StringComparer.Ordinal).ToArray()
                : remainder;
            _pending[key] = pending with
            {
                TestCaseIds = ids,
                RefreshInventory = false,
                ObservedAt = now,
                ReadyAt = now,
            };
        }
    }

    private void RequeueForegroundRetry(
        PendingKey key,
        ContinuousTestDaemonPendingRun pending,
        IReadOnlyList<string> cases,
        DateTimeOffset now)
    {
        if (cases.Count == 0)
            return;
        lock (_lock)
        {
            if (_latestByWorkspace.TryGetValue(key.WorkspaceId, out CtFreshnessKey latest)
                && latest != pending.Freshness)
            {
                return;
            }

            IReadOnlyList<string> ids = _pending.TryGetValue(key, out ContinuousTestDaemonPendingRun? existing)
                ? existing.TestCaseIds.Concat(cases).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
                : cases;
            _pending[key] = pending with
            {
                TestCaseIds = ids,
                RefreshInventory = false,
                ObservedAt = now,
                ReadyAt = now,
            };
        }
    }

    private bool TrySpendRunFailureRetry(PendingKey key, string selectedRevision)
    {
        lock (_lock)
        {
            if (_runFailureRetrySpentAtRevision.TryGetValue(key, out string? spentAt)
                && string.Equals(spentAt, selectedRevision, StringComparison.Ordinal))
            {
                return false;
            }

            _runFailureRetrySpentAtRevision[key] = selectedRevision;
            return true;
        }
    }

    private void ClearRunFailureRetry(PendingKey key)
    {
        lock (_lock)
            _runFailureRetrySpentAtRevision.Remove(key);
    }

    private async Task<ContinuousTestDaemonPendingRun> RefreshInventoryIfNeededAsync(
        ContinuousTestDaemonPendingRun pending,
        CancellationToken cancellationToken)
    {
        if (!pending.RefreshInventory
            && (pending.TestCaseIds.Count > 0
                || HasProviderInventory(pending.Workspace.WorkspaceId, pending.Workspace.ProjectPath)))
        {
            return pending;
        }

        try
        {
            await _coordinator
                .DiscoverAsync(new ContinuousTestDiscoveryRequest(pending.Workspace), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RecordDiscoveryFailure(pending, ex);
            return pending;
        }

        ClearDiscoveryFailure(pending.Workspace);
        ContinuousTestSelectionResult selection = _selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: pending.Workspace.WorkspaceId,
            WorkspaceScope: true,
            ProjectPath: pending.Workspace.ProjectPath));
        // The explicit run's selection is rebuilt here after discovery, so the red-keeping rule has to
        // be applied again: this is the list the drain executes.
        IReadOnlyList<string> selectedTestCaseIds = DropFreshAt(
            pending.Workspace.WorkspaceId,
            pending.Workspace.ProjectPath,
            selection.SelectedTestCaseIds,
            pending.Freshness,
            keepRed: pending.ExplicitRun);
        _store.MarkContinuousTestsStale(
            pending.Workspace.WorkspaceId,
            DropFreshAt(
                pending.Workspace.WorkspaceId,
                pending.Workspace.ProjectPath,
                selection.StaleTestCaseIds,
                pending.Freshness),
            pending.Freshness);
        NotifyCtStateChanged(pending.Workspace.WorkspaceId);
        return pending with
        {
            TestCaseIds = selectedTestCaseIds,
            RefreshInventory = false,
        };
    }

    /// <summary>
    /// Drops the cases that are fresh at <paramref name="selected"/> and have nothing to prove.
    ///
    /// <para><paramref name="keepRed"/> is the EXPLICIT run's exception. A red is committed-fresh by
    /// the same rule a green is, so without it a user-requested run over a red tree selected nothing
    /// at all. It is off everywhere else: an automatic run that re-ran every failing test on every
    /// debounce would be a red loop on every save.</para>
    /// </summary>
    private IReadOnlyList<string> DropFreshAt(
        string workspaceId,
        string projectPath,
        IReadOnlyList<string> testCaseIds,
        CtFreshnessKey selected,
        bool keepRed = false)
    {
        if (testCaseIds.Count == 0)
            return testCaseIds;
        if (ContinuousTestDurableFreshness.HasActiveDiscoveryFailure(_store.ListTestCases(workspaceId), projectPath))
            return testCaseIds;
        var statusesById = _store.ListContinuousTestStatuses(workspaceId)
            .ToDictionary(status => status.TestCaseId, StringComparer.Ordinal);
        // Fresh-by-watermark counts as fresh: a green the last change could not reach has nothing
        // to prove, so it is neither re-marked stale nor re-run. Same rule as the status projection.
        IReadOnlyDictionary<string, CtFreshnessKey> watermarks =
            _store.ListContinuousTestFreshWatermarks(workspaceId, selected.IndexIdentity);
        string[] survivors = testCaseIds
            .Where(id => !(statusesById.TryGetValue(id, out ContinuousTestStatus? status)
                && !(keepRed && status.State == ContinuousTestState.Red)
                && ContinuousTestDurableFreshness.IsFreshAt(status, selected, watermarks)))
            .ToArray();
        return survivors.Length == testCaseIds.Count ? testCaseIds : survivors;
    }

    private bool HasProviderInventory(string workspaceId, string projectPath) =>
        _store.ListTestCases(workspaceId).Any(row =>
            ContinuousTestImpactSelector.IsProviderManagedTestCaseForProject(row, projectPath));

    private void RecordDiscoveryFailure(ContinuousTestDaemonPendingRun pending, Exception exception)
    {
        ContinuousTestWorkspace workspace = pending.Workspace;

        // Logged BEFORE the store write, and with the FULL detail. The `ct.db` row below keeps only
        // FailureSummary's first line, which is right for a status column and useless for a diagnosis:
        // finding the last discovery failure of a dogfood run meant querying the database. This line
        // carries the type, the whole message, and the stack, so the shared daily log answers it.
        Log($"ct discovery failed workspace={workspace.WorkspaceId} project={workspace.ProjectPath} "
            + CtDaemonLog.FailureDetail(exception));

        string testCaseId = DiscoveryFailureTestCaseId(workspace);
        string runId = CtStableIds.StableId(
            "ct_discovery_failure_run",
            workspace.WorkspaceId,
            workspace.ProjectPath,
            pending.CurrentRevision);
        _store.PutTestCase(new ContinuousTestCase(
            Id: testCaseId,
            WorkspaceId: workspace.WorkspaceId,
            Name: "Project discovery failed",
            QualifiedName: $"Project discovery failed: {Path.GetFileName(workspace.ProjectPath)}",
            Selector: $"project-discovery::{workspace.ProjectPath}",
            Framework: pending.Framework ?? workspace.Framework,
            Source: "ct-project-status",
            Metadata: new Dictionary<string, object?>
            {
                ["kind"] = DiscoveryFailureKind,
                ["ct_project_path"] = workspace.ProjectPath,
            }));
        _storeApplier.StartRun(new ContinuousTestProviderRunStart(
            workspace.WorkspaceId,
            runId,
            pending.SelectedRevision,
            pending.IndexIdentity,
            pending.Freshness.Revision,
            [testCaseId],
            pending.Command,
            pending.Framework,
            pending.ObservedAt));
        _store.CompleteContinuousTestRun(new ContinuousTestRunCompletion(
            workspace.WorkspaceId,
            runId,
            pending.SelectedRevision,
            pending.CurrentRevision,
            pending.IndexIdentity,
            pending.Freshness.Revision,
            "failed",
            DateTimeOffset.UtcNow,
            [
                new ContinuousTestResult(
                    CtStableIds.StableId("test_result", workspace.WorkspaceId, testCaseId, runId),
                    workspace.WorkspaceId,
                    testCaseId,
                    runId,
                    "failed",
                    pending.SelectedRevision,
                    pending.IndexIdentity,
                    pending.Freshness.Revision,
                    FailureSummary: FailureSummary(exception)),
            ]));
        NotifyCtStateChanged(workspace.WorkspaceId);
    }

    private void ClearDiscoveryFailure(ContinuousTestWorkspace workspace) =>
        _store.DeleteTestCase(workspace.WorkspaceId, DiscoveryFailureTestCaseId(workspace));

    private static string DiscoveryFailureTestCaseId(ContinuousTestWorkspace workspace) =>
        CtStableIds.StableId("ct-discovery-failure", workspace.WorkspaceId, workspace.ProjectPath);

    /// <summary>
    /// The one-line summary the <c>ct.db</c> status column keeps. Deliberately the FIRST line only: a
    /// status column has to stay short. Use <see cref="CtDaemonLog.FailureDetail"/> for a log line.
    /// </summary>
    private static string FailureSummary(Exception exception)
    {
        string text = exception.Message.Trim();
        return string.IsNullOrWhiteSpace(text)
            ? exception.GetType().Name
            : text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
    }

    private ContinuousTestCoordinatorRunResult ScheduleFlakyRetries(
        PendingKey pendingKey,
        ContinuousTestDaemonPendingRun pending,
        ContinuousTestCoordinatorRunResult coordinatorResult,
        DateTimeOffset now)
    {
        var selectedTestCaseIds = pending.TestCaseIds.ToHashSet(StringComparer.Ordinal);
        string[] retryTestCaseIds = coordinatorResult.Statuses
            .Where(status => selectedTestCaseIds.Contains(status.TestCaseId))
            .Where(status => status.State == ContinuousTestState.Red)
            .Where(status => string.Equals(status.LastRunRevision, pending.CurrentRevision, StringComparison.Ordinal))
            .Where(status => TryRecordRetryAttempt(pending, status.TestCaseId))
            .Select(status => status.TestCaseId)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (retryTestCaseIds.Length == 0)
            return coordinatorResult;
        _store.MarkContinuousTestsStale(pending.Workspace.WorkspaceId, retryTestCaseIds, pending.Freshness);
        NotifyCtStateChanged(pending.Workspace.WorkspaceId);
        lock (_lock)
        {
            if (_pending.TryGetValue(pendingKey, out ContinuousTestDaemonPendingRun? existing))
            {
                _pending[pendingKey] = existing with
                {
                    TestCaseIds = existing.TestCaseIds
                        .Concat(retryTestCaseIds)
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal)
                        .ToArray(),
                };
            }
            else
            {
                _pending[pendingKey] = pending with
                {
                    TestCaseIds = retryTestCaseIds,
                    RefreshInventory = false,
                    ObservedAt = now,
                    ReadyAt = now,
                };
            }
        }

        return coordinatorResult with
        {
            Statuses = _store.ListContinuousTestStatuses(pending.Workspace.WorkspaceId),
        };
    }

    private bool TryRecordRetryAttempt(ContinuousTestDaemonPendingRun pending, string testCaseId)
    {
        ContinuousTestFlakinessScore score = _store.ScoreContinuousTestFlakiness(
            pending.Workspace.WorkspaceId, testCaseId);
        if (score.State != ContinuousTestFlakinessState.Flaky)
            return false;
        RetryKey retryKey = RetryKey.From(pending, testCaseId);
        lock (_lock)
        {
            _retryAttempts.TryGetValue(retryKey, out int attempts);
            if (attempts >= MaxFlakyRetryAttempts)
                return false;
            _retryAttempts[retryKey] = attempts + 1;
            return true;
        }
    }

    private static string NewRunId() => $"ct-run:{Guid.NewGuid():N}";

    private void FailCancelledRunBestEffort(PendingKey key, ContinuousTestDaemonPendingRun pending, string runId)
    {
        try
        {
            _storeApplier.FailRunAndMarkStale(
                pending.Workspace.WorkspaceId,
                runId,
                pending.SelectedRevision,
                LatestRevisionStringFor(key, pending.CurrentRevision),
                pending.IndexIdentity,
                pending.Freshness.Revision,
                pending.TestCaseIds,
                DateTimeOffset.UtcNow);
            NotifyCtStateChanged(pending.Workspace.WorkspaceId);
        }
        catch (Exception)
        {
        }
    }

    private void NotifyCtStateChanged(string workspaceId) => _ctStateChanged?.Invoke(workspaceId);

    private void Log(string message) => _lifecycleLog?.Invoke(message);

    private void LogRunCompletion(
        ContinuousTestDaemonPendingRun pending,
        ContinuousTestCoordinatorRunResult coordinatorResult)
    {
        if (_lifecycleLog is null)
            return;
        ProviderRunResult provider = coordinatorResult.ProviderResult;
        Log($"ct run complete workspace={pending.Workspace.WorkspaceId} run={provider.RunId} status={provider.Status}");
    }

    private static void ValidateBuildOutputRoot(ContinuousTestWorkspace workspace)
    {
        string workspaceRoot = Path.GetFullPath(workspace.WorkspaceRoot);
        string buildOutputRoot = Path.GetFullPath(workspace.BuildOutputRoot);
        string relative = Path.GetRelativePath(workspaceRoot, buildOutputRoot);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (relative == "." || (!relative.StartsWith("..", comparison) && !Path.IsPathRooted(relative)))
        {
            throw new ArgumentException(
                "continuous test build output root must live outside the workspace root",
                nameof(ContinuousTestWorkspace.BuildOutputRoot));
        }
    }

    private readonly record struct PendingKey(
        string WorkspaceId,
        string ProjectPath,
        ContinuousTestRunLane Lane)
    {
        public static PendingKey FromWorkspace(
            ContinuousTestWorkspace workspace,
            ContinuousTestRunLane lane = ContinuousTestRunLane.Foreground) =>
            new(workspace.WorkspaceId, workspace.ProjectPath, lane);

        public PendingKey AsForeground() => this with { Lane = ContinuousTestRunLane.Foreground };
    }

    private readonly record struct RetryKey(
        string WorkspaceId,
        string ProjectPath,
        string Revision,
        string TestCaseId)
    {
        public static RetryKey From(ContinuousTestDaemonPendingRun pending, string testCaseId) =>
            new(pending.Workspace.WorkspaceId, pending.Workspace.ProjectPath, pending.CurrentRevision, testCaseId);
    }
}
