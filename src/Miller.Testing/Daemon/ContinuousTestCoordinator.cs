using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Miller.Testing.Parsing;

namespace Miller.Testing;

public sealed class ContinuousTestCoordinatorOptions
{
    public static readonly TimeSpan DefaultProviderOperationTimeout = TimeSpan.FromMinutes(30);

    public const long DefaultGenerationDiskBudgetBytes = 20L * 1024 * 1024 * 1024;

    public TimeSpan ProviderOperationTimeout { get; init; } = DefaultProviderOperationTimeout;

    public long GenerationDiskBudgetBytes { get; init; } = DefaultGenerationDiskBudgetBytes;

    public string OwnerToken { get; init; } = Guid.NewGuid().ToString("N");

    public Action<string>? LifecycleLog { get; init; }

    internal Func<string, bool> ReapGenerationDirectory { get; init; } = CtGenerationPaths.TryReap;

    internal Action<string> DeleteReapRemnant { get; init; } =
        static directory => Directory.Delete(directory, recursive: true);

    internal Func<string, long?> MeasureDirectoryBytes { get; init; } =
        ContinuousTestCoordinator.MeasureTreeBytes;

    internal Func<ContinuousTestCoordinatorRunRequest, CtRevisionObservation> RevisionObserver { get; init; } =
        ContinuousTestCoordinator.ObserveRevision;

    internal void Validate()
    {
        if (ProviderOperationTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ProviderOperationTimeout));
        if (GenerationDiskBudgetBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(GenerationDiskBudgetBytes));
        if (string.IsNullOrWhiteSpace(OwnerToken))
            throw new ArgumentException("must not be blank", nameof(OwnerToken));
    }
}

internal readonly record struct CtRevisionObservation(string? Revision, bool Converged)
{
    public static CtRevisionObservation Unread => new(null, false);
}

public sealed class ContinuousTestCoordinator
{
    private static readonly StringComparer BuildOutputRootComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProjectGates =
        new(BuildOutputRootComparer);

    /// <summary>
    /// Consecutive provider failures per build output root, the trigger for wiping the project-stable build
    /// cache. In-memory on purpose: the daemon holds ONE coordinator for its whole life, which is where a
    /// build that keeps failing actually accumulates. A one-shot CLI process never reaches two in a row and
    /// therefore never wipes — the right answer for a caller who cannot know whether the last failure came
    /// from the cache or from the tree.
    /// </summary>
    private readonly ConcurrentDictionary<string, int> _buildCacheFailures = new(BuildOutputRootComparer);

    private readonly IContinuousTestProviderResolver _providerResolver;
    private readonly ContinuousTestStore _store;
    private readonly ContinuousTestStoreApplier _applier;
    private readonly Func<string> _runIdFactory;
    private readonly ContinuousTestCoordinatorOptions _options;
    private readonly Action<string>? _lifecycleLog;

    public ContinuousTestCoordinator(
        IContinuousTestProvider provider,
        ContinuousTestStore store,
        Func<string>? runIdFactory = null,
        ContinuousTestCoordinatorOptions? options = null,
        Action<string>? onDiagnostic = null)
        : this(new FixedContinuousTestProviderResolver(provider), store, runIdFactory, options, onDiagnostic)
    {
    }

    /// <summary>
    /// <paramref name="onDiagnostic"/> receives the maintenance degradations a run survives rather than fails
    /// on: a build generation directory the reap could not remove, and generation disk over its budget. Left
    /// unwired they are silent, and a surviving test host that still holds a generation directory reads exactly
    /// like a clean workspace - the reap fails on every cycle and the operator sees no cause anywhere. Callers
    /// that know the workspace root pass <see cref="CtDaemonLog.Write"/>, the same sink
    /// <see cref="ContinuousTestProviderFactory.CreateDefault"/> takes, so CT reports through one channel; a
    /// caller that does not (a unit test, a preview) passes nothing and keeps today's silence. An explicit
    /// argument BEATS <see cref="ContinuousTestCoordinatorOptions.LifecycleLog"/>, which stays the seam for a
    /// caller that already builds an options object.
    /// </summary>
    public ContinuousTestCoordinator(
        IContinuousTestProviderResolver providerResolver,
        ContinuousTestStore store,
        Func<string>? runIdFactory = null,
        ContinuousTestCoordinatorOptions? options = null,
        Action<string>? onDiagnostic = null)
    {
        _providerResolver = providerResolver ?? throw new ArgumentNullException(nameof(providerResolver));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _applier = new ContinuousTestStoreApplier(store);
        _runIdFactory = runIdFactory ?? NewRunId;
        _options = options ?? new ContinuousTestCoordinatorOptions();
        _options.Validate();
        _lifecycleLog = onDiagnostic ?? _options.LifecycleLog;
    }

    public async Task<ContinuousTestDiscoveryResult> DiscoverAsync(
        ContinuousTestDiscoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        SemaphoreSlim gate = ProjectGate(request.Workspace);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await DiscoverInsideProjectGateAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ContinuousTestCoordinatorRunResult> RunSelectedAsync(
        ContinuousTestCoordinatorRunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        SemaphoreSlim gate = ProjectGate(request.Workspace);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunSelectedInsideProjectGateAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<ContinuousTestDiscoveryResult> DiscoverInsideProjectGateAsync(
        ContinuousTestDiscoveryRequest request,
        CancellationToken cancellationToken)
    {
        ContinuousTestProviderResolution resolution = _providerResolver.Resolve(request.Workspace);
        string? generationId = null;
        IReadOnlyList<ProviderTestCase> testCases;
        try
        {
            testCases = await ExecuteWithBuildCacheRecoveryAsync(
                request.Workspace,
                () => ExecuteProviderOperationAsync(
                    providerToken => resolution.Provider.DiscoverAsync(request.Workspace, providerToken),
                    "discovery",
                    request.Workspace,
                    request.Workspace.Framework,
                    cancellationToken)).ConfigureAwait(false);
        }
        catch (ContinuousTestProviderException providerException)
        {
            generationId = providerException.GenerationId;
            RecordGenerationAllocated(request.Workspace, generationId);
            ReleaseFailedOperationGeneration(request.Workspace, generationId);
            throw;
        }
        finally
        {
            CleanupOperationTemp(request.Workspace, generationId);
        }

        _applier.ApplyDiscovery(
            request.Workspace.WorkspaceId,
            testCases,
            request.Workspace.ProjectPath,
            request.ProviderSource ?? resolution.ProviderSource);
        return new ContinuousTestDiscoveryResult(
            testCases,
            _store.ListContinuousTestStatuses(request.Workspace.WorkspaceId));
    }

    private async Task<ContinuousTestCoordinatorRunResult> RunSelectedInsideProjectGateAsync(
        ContinuousTestCoordinatorRunRequest request,
        CancellationToken cancellationToken)
    {
        string runId = !string.IsNullOrWhiteSpace(request.RunId) ? request.RunId! : _runIdFactory();
        if (string.IsNullOrWhiteSpace(runId))
            throw new InvalidOperationException("continuous test run id factory returned an empty id");

        long revision = ParseRevision(request.CurrentRevision);
        _applier.StartRun(new ContinuousTestProviderRunStart(
            WorkspaceId: request.Workspace.WorkspaceId,
            RunId: runId,
            SelectedRevision: request.SelectedRevision,
            IndexIdentity: request.IndexIdentity,
            Revision: revision,
            SelectedTestCaseIds: request.TestCaseIds,
            Command: request.Command,
            Framework: request.Framework,
            StartedAt: request.StartedAt));

        ProviderRunResult? providerResult = null;
        string? generationId = null;
        bool maintenanceAttempted = false;
        ContinuousTestProviderResolution resolution = _providerResolver.Resolve(request.Workspace);
        request.ProviderResolved?.Invoke(resolution);
        bool instrumented = request.CoverageMode == ContinuousTestCoverageMode.PerTest;
        CtRevisionObservation revisionAtStart = instrumented ? _options.RevisionObserver(request) : CtRevisionObservation.Unread;
        CtRevisionObservation revisionAtEnd = CtRevisionObservation.Unread;
        try
        {
            providerResult = await ExecuteWithBuildCacheRecoveryAsync(
                request.Workspace,
                () => ExecuteProviderOperationAsync(
                    providerToken => resolution.Provider.RunAsync(
                        new ContinuousTestProviderRunRequest(
                            Workspace: request.Workspace,
                            SelectedRevision: request.SelectedRevision,
                            IndexIdentity: request.IndexIdentity,
                            RunId: runId,
                            // The whole-suite form travels as a FLAG beside the full list, never as a blanked
                            // list. Blanking it read as "run the whole assembly" only to providers that
                            // attribute from a result artifact; the cargo provider's run loop is driven by
                            // the list itself, so an empty list started no process, returned "passed" over
                            // zero results, and left every case stale forever (finding F6, 2026-08-21).
                            TestCaseIds: request.TestCaseIds,
                            WholeSuite: request.WholeSuite,
                            FilterArguments: request.FilterArguments,
                            Command: request.Command,
                            ExcludeTraits: request.ExcludeTraits,
                            Framework: request.Framework,
                            CoverageMode: request.CoverageMode,
                            Progress: request.Progress),
                        providerToken),
                    "run",
                    request.Workspace,
                    request.Framework ?? request.Workspace.Framework,
                    cancellationToken)).ConfigureAwait(false);
            generationId = providerResult.GenerationId;
            if (instrumented)
                revisionAtEnd = _options.RevisionObserver(request);
            RecordGenerationAllocated(request.Workspace, generationId);

            if (!string.Equals(providerResult.RunId, runId, StringComparison.Ordinal))
                providerResult = providerResult with { RunId = runId };

            if (IsExecutionBlocked(providerResult))
            {
                FailRun(request, runId, ResolveCurrentRevision(request));
                maintenanceAttempted = true;
                RunMaintenanceTail(request.Workspace, generationId);
                return new ContinuousTestCoordinatorRunResult(
                    providerResult,
                    _store.ListContinuousTestStatuses(request.Workspace.WorkspaceId),
                    resolution.ProviderSource);
            }

            string currentRevision = ResolveCurrentRevision(request);
            long currentNumeric = ParseRevision(currentRevision);
            string? artifactId = RecordRunArtifact(request, providerResult, currentRevision);

            if (!TryImportProviderResultArtifact(request, providerResult, currentRevision))
            {
                // FAIL-SAFE. A run that selected cases and produced a verdict for NONE of them, with no
                // result artifact to import either, knows nothing about those tests. Recording it as a
                // completed run is how a provider that executed no process at all reported "passed" over
                // 4,173 cargo cases (dogfood finding F6). An honest failure records the run failed and
                // leaves every selected case stale, which is what actually happened.
                if (providerResult.CaseResults.Count == 0 && request.TestCaseIds.Count > 0)
                    throw new ContinuousTestProviderException(
                        $"The {request.Framework ?? request.Workspace.Framework ?? "test"} run selected " +
                        $"{request.TestCaseIds.Count} test case(s) and reported a result for none of them.");

                _applier.CompleteRun(
                    WorkspaceId: request.Workspace.WorkspaceId,
                    SelectedRevision: request.SelectedRevision,
                    CurrentRevision: currentRevision,
                    IndexIdentity: request.IndexIdentity,
                    Revision: currentNumeric,
                    Result: providerResult);
                if (artifactId is not null)
                    _store.LinkContinuousTestRunArtifact(request.Workspace.WorkspaceId, providerResult.RunId, artifactId);
            }

            ImportProviderCoverageArtifacts(request, providerResult, revisionAtStart, revisionAtEnd);
            if (!string.IsNullOrWhiteSpace(generationId))
            {
                _store.MarkCtGenerationComplete(
                    request.Workspace.BuildOutputRoot, generationId, DateTimeOffset.UtcNow);
            }

            maintenanceAttempted = true;
            RunMaintenanceTail(request.Workspace, generationId);
            return new ContinuousTestCoordinatorRunResult(
                providerResult,
                _store.ListContinuousTestStatuses(request.Workspace.WorkspaceId),
                resolution.ProviderSource);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            string failedCurrentRevision = ResolveCurrentRevision(request);
            string? failedArtifactId = null;
            if (providerResult is null && exception is ContinuousTestProviderException failedProvider)
            {
                generationId = failedProvider.GenerationId;
                RecordGenerationAllocated(request.Workspace, generationId);
                if (!string.IsNullOrWhiteSpace(failedProvider.ResultArtifactPath))
                {
                    try
                    {
                        failedArtifactId = RecordRunArtifactForPath(
                            request, failedProvider.ResultArtifactPath, runId, failedCurrentRevision);
                    }
                    catch (Exception)
                    {
                    }
                }
            }

            TerminalizeFailedRun(request, runId, failedCurrentRevision, generationId, failedArtifactId, ref maintenanceAttempted);
            throw;
        }
        finally
        {
            CleanupOperationTemp(request.Workspace, generationId);
        }
    }

    internal static bool IsExecutionBlocked(ProviderRunResult result) =>
        string.Equals(result.Status, "blocked", StringComparison.OrdinalIgnoreCase)
        || ContainsAppControlCode(result.Status);

    internal static bool IsExecutionBlocked(Exception exception) =>
        ContainsAppControlCode(exception.Message);

    private static bool ContainsAppControlCode(string? text) =>
        text is not null && text.Contains("0x800711C7", StringComparison.OrdinalIgnoreCase);

    private void FailRun(ContinuousTestCoordinatorRunRequest request, string runId, string currentRevision) =>
        _applier.FailRunAndMarkStale(
            WorkspaceId: request.Workspace.WorkspaceId,
            RunId: runId,
            SelectedRevision: request.SelectedRevision,
            CurrentRevision: currentRevision,
            IndexIdentity: request.IndexIdentity,
            Revision: ParseRevision(currentRevision),
            SelectedTestCaseIds: request.TestCaseIds,
            EndedAt: DateTimeOffset.UtcNow);

    private void TerminalizeFailedRun(
        ContinuousTestCoordinatorRunRequest request,
        string runId,
        string currentRevision,
        string? generationId,
        string? artifactId,
        ref bool maintenanceAttempted)
    {
        FailRun(request, runId, currentRevision);
        if (artifactId is not null)
            _store.LinkContinuousTestRunArtifact(request.Workspace.WorkspaceId, runId, artifactId);
        ReleaseFailedOperationGeneration(request.Workspace, generationId);
        if (maintenanceAttempted)
            return;
        maintenanceAttempted = true;
        RunMaintenanceTail(request.Workspace, activeGenerationId: null);
    }

    /// <summary>
    /// Runs one provider operation, and gives a build that has now failed TWICE IN A ROW a clean cache to try
    /// once more against.
    ///
    /// <para><b>Why two, not one.</b> A single failure is ordinary — a compile error in the tree fails exactly
    /// this way — and wiping on it would turn every red build into a cold rebuild. Two in a row is the point
    /// where a poisoned cache becomes the more likely explanation.</para>
    ///
    /// <para><b>Why this is safe.</b> The cache holds no verdict. A wrong cache can only produce a build
    /// failure, which is already visible and already self-recovers; the wipe just shortens that recovery. The
    /// streak resets on the wipe, so a project that always fails wipes at most every second attempt rather
    /// than on every one.</para>
    ///
    /// <para>This replaces the guarantee the per-generation target directory used to give: a fresh directory
    /// per operation could sidestep a file a dying test process still held.</para>
    /// </summary>
    private async Task<T> ExecuteWithBuildCacheRecoveryAsync<T>(
        ContinuousTestWorkspace workspace,
        Func<Task<T>> operation)
    {
        try
        {
            T value = await operation().ConfigureAwait(false);
            _buildCacheFailures.TryRemove(workspace.BuildOutputRoot, out _);
            return value;
        }
        catch (ContinuousTestProviderException) when (RecordBuildFailureAndAskForWipe(workspace))
        {
            // Handled below: the filter has already reset the streak, so the retry cannot loop.
        }

        WipeBuildCache(workspace);
        T retried = await operation().ConfigureAwait(false);
        _buildCacheFailures.TryRemove(workspace.BuildOutputRoot, out _);
        return retried;
    }

    private bool RecordBuildFailureAndAskForWipe(ContinuousTestWorkspace workspace)
    {
        int streak = _buildCacheFailures.AddOrUpdate(workspace.BuildOutputRoot, 1, static (_, count) => count + 1);
        if (streak < 2)
            return false;

        _buildCacheFailures.TryRemove(workspace.BuildOutputRoot, out _);
        return true;
    }

    private void WipeBuildCache(ContinuousTestWorkspace workspace)
    {
        string cacheRoot = CtGenerationPaths.CacheRoot(workspace);
        if (!Directory.Exists(cacheRoot))
            return;

        // The same rename-then-delete the generation reap uses: the rename is the atomic part, and a delete
        // that cannot finish leaves a ".reap-" remnant the maintenance tail sweeps later.
        bool removed = _options.ReapGenerationDirectory(cacheRoot);
        _lifecycleLog?.Invoke(removed
            ? $"build_cache_wiped root={workspace.BuildOutputRoot}"
            : $"build_cache_wipe_failed root={workspace.BuildOutputRoot}");
    }

    private static SemaphoreSlim ProjectGate(ContinuousTestWorkspace workspace) =>
        ProjectGates.GetOrAdd(workspace.BuildOutputRoot, static _ => new SemaphoreSlim(1, 1));

    private void RecordGenerationAllocated(ContinuousTestWorkspace workspace, string? generationId)
    {
        if (string.IsNullOrWhiteSpace(generationId))
            return;
        _store.PutCtGenerationAllocated(new CtGenerationRecord(
            GenerationId: generationId,
            BuildOutputRoot: workspace.BuildOutputRoot,
            State: CtGenerationStates.Allocated,
            OwnerToken: _options.OwnerToken,
            AllocatedAt: DateTimeOffset.UtcNow,
            CompletedAt: null));
    }

    private static void CleanupOperationTemp(ContinuousTestWorkspace workspace, string? generationId)
    {
        try
        {
            string temp = string.IsNullOrWhiteSpace(generationId)
                ? CtTempPaths.ForWorkspace(workspace)
                : CtTempPaths.ForGeneration(workspace, generationId);
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void ReleaseFailedOperationGeneration(ContinuousTestWorkspace workspace, string? generationId)
    {
        if (string.IsNullOrWhiteSpace(generationId))
            return;
        _store.MarkCtGenerationReapEligible(workspace.BuildOutputRoot, generationId, _options.OwnerToken);
    }

    private void RunMaintenanceTail(ContinuousTestWorkspace workspace, string? activeGenerationId)
    {
        var ledger = new ReapLedger();
        ReapSupersededGenerations(workspace, activeGenerationId, ledger);
        DiskAccounting accounting = MeasureGenerationDisk(workspace);
        CommitMaintenance(workspace, ledger, accounting);
    }

    private void ReapSupersededGenerations(
        ContinuousTestWorkspace workspace,
        string? activeGenerationId,
        ReapLedger ledger)
    {
        string buildOutputRoot = workspace.BuildOutputRoot;
        IReadOnlyList<CtGenerationRecord> generations = _store.ListCtGenerations(buildOutputRoot);
        string? newestComplete = generations
            .LastOrDefault(row => string.Equals(row.State, CtGenerationStates.Complete, StringComparison.Ordinal))
            ?.GenerationId;
        string? newestDirectory = NewestGenerationDirectory(buildOutputRoot);

        // Only generation-shaped directories are candidates, so the project-stable cache root is never a
        // reap target: a future run depends on it, and CtGenerationPaths.IsGenerationId cannot match "cache".
        foreach (CtGenerationRecord generation in generations)
        {
            if (generation.State is not (CtGenerationStates.Complete or CtGenerationStates.ReapEligible))
                continue;
            if (IsRetained(generation.GenerationId))
                continue;
            if (TryReapGenerationDirectory(workspace, generation.GenerationId, ledger))
                _store.MarkCtGenerationReaped(buildOutputRoot, generation.GenerationId);
        }

        var recorded = generations.Select(row => row.GenerationId).ToHashSet(StringComparer.Ordinal);
        foreach (string directoryName in GenerationDirectories(buildOutputRoot))
        {
            if (recorded.Contains(directoryName) || IsRetained(directoryName)
                || string.Equals(directoryName, newestDirectory, StringComparison.Ordinal))
            {
                continue;
            }

            TryReapGenerationDirectory(workspace, directoryName, ledger);
        }

        SweepReapRemnants(buildOutputRoot, ledger);

        bool IsRetained(string generationId) =>
            string.Equals(generationId, activeGenerationId, StringComparison.Ordinal)
            || string.Equals(generationId, newestComplete, StringComparison.Ordinal);
    }

    private bool TryReapGenerationDirectory(ContinuousTestWorkspace workspace, string generationId, ReapLedger ledger)
    {
        string generationRoot = Path.Combine(workspace.BuildOutputRoot, generationId);
        if (_options.ReapGenerationDirectory(generationRoot))
        {
            ledger.Removed.Add(generationId);
            return true;
        }

        _lifecycleLog?.Invoke($"generation_reap_failed root={workspace.BuildOutputRoot} gen={generationId}");
        ledger.Debts.Add(new ReapDebt(generationId, BestEffortTreeBytes(generationRoot)));
        return false;
    }

    private static string? NewestGenerationDirectory(string buildOutputRoot)
    {
        string? newest = null;
        foreach (string directoryName in GenerationDirectories(buildOutputRoot))
            newest = directoryName;
        return newest;
    }

    private static IReadOnlyList<string> GenerationDirectories(string buildOutputRoot) =>
        DirectoryNames(buildOutputRoot).Where(CtGenerationPaths.IsGenerationId).ToArray();

    private void SweepReapRemnants(string buildOutputRoot, ReapLedger ledger)
    {
        foreach (string name in DirectoryNames(buildOutputRoot))
        {
            if (!name.Contains(CtGenerationPaths.ReapSuffixPrefix, StringComparison.Ordinal))
                continue;
            string remnant = Path.Combine(buildOutputRoot, name);
            try
            {
                _options.DeleteReapRemnant(remnant);
                ledger.Removed.Add(name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ledger.Debts.Add(new ReapDebt(name, BestEffortTreeBytes(remnant)));
            }
        }
    }

    private DiskAccounting MeasureGenerationDisk(ContinuousTestWorkspace workspace)
    {
        var stored = _store.ListCtGenerationDisk()
            .ToDictionary(row => row.BuildOutputRoot, row => row, BuildOutputRootComparer);
        IReadOnlyList<string> contentRoots = GenerationContentRoots(workspace.BuildOutputRoot);
        var writes = new List<RootMeasurement>();
        var carried = new List<CtGenerationDiskRecord>();
        foreach (string root in contentRoots)
        {
            stored.TryGetValue(root, out CtGenerationDiskRecord? previous);
            bool isOwn = BuildOutputRootComparer.Equals(root, workspace.BuildOutputRoot);
            if (!isOwn && previous is { Stale: false })
            {
                carried.Add(previous);
                continue;
            }

            long? measured = MeasureRootBytes(root);
            writes.Add(measured is null
                ? new RootMeasurement(root, previous?.Bytes ?? 0, Stale: true)
                : new RootMeasurement(root, measured.Value, Stale: false));
        }

        string[] orphans = stored.Keys
            .Where(root => !contentRoots.Contains(root, BuildOutputRootComparer))
            .ToArray();
        return new DiskAccounting(
            writes,
            orphans,
            carried.Sum(row => row.Bytes) + writes.Sum(row => row.Bytes),
            contentRoots.Count,
            carried.Count + writes.Count(row => !row.Stale));
    }

    private void CommitMaintenance(ContinuousTestWorkspace workspace, ReapLedger ledger, DiskAccounting accounting)
    {
        string buildOutputRoot = workspace.BuildOutputRoot;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        _store.Transaction(() =>
        {
            foreach (string directoryName in ledger.Removed)
                _store.ClearCtGenerationReapDebt(buildOutputRoot, directoryName);
            foreach (ReapDebt debt in ledger.Debts)
                _store.UpsertCtGenerationReapDebt(buildOutputRoot, debt.DirectoryName, debt.Bytes, now);
            foreach (string root in accounting.OrphanRoots)
                _store.DeleteCtGenerationDisk(root);
            foreach (RootMeasurement measurement in accounting.Writes)
                _store.UpsertCtGenerationDisk(measurement.Root, measurement.Bytes, measurement.Stale, now);
            _store.UpsertCtGenerationPressure(
                _options.GenerationDiskBudgetBytes,
                accounting.RootsTotal,
                accounting.RootsMeasured,
                now);
        });

        if (accounting.RootsMeasured != accounting.RootsTotal
            || accounting.TotalBytes <= _options.GenerationDiskBudgetBytes)
        {
            return;
        }

        _lifecycleLog?.Invoke(
            $"generation_disk_over_budget bytes={accounting.TotalBytes} budget={_options.GenerationDiskBudgetBytes}");
    }

    private static IReadOnlyList<string> GenerationContentRoots(string ownBuildOutputRoot)
    {
        string? parent = Path.GetDirectoryName(ownBuildOutputRoot);
        if (string.IsNullOrEmpty(parent))
            return [];
        IReadOnlyList<string>? names = TryDirectoryNames(parent);
        if (names is null)
            return [];
        var roots = new List<string>();
        foreach (string name in names)
        {
            string candidate = Path.Combine(parent, name);
            string root = BuildOutputRootComparer.Equals(candidate, ownBuildOutputRoot)
                ? ownBuildOutputRoot
                : candidate;
            if (HoldsGenerationContent(root))
                roots.Add(root);
        }

        return roots;
    }

    private static bool HoldsGenerationContent(string buildOutputRoot)
    {
        IReadOnlyList<string>? names = TryDirectoryNames(buildOutputRoot);
        return names is null || names.Any(IsGenerationContent);
    }

    private long? MeasureRootBytes(string buildOutputRoot)
    {
        IReadOnlyList<string>? names = TryDirectoryNames(buildOutputRoot);
        if (names is null)
            return null;
        long total = 0;
        foreach (string name in names.Where(IsGenerationContent))
        {
            long? bytes = _options.MeasureDirectoryBytes(Path.Combine(buildOutputRoot, name));
            if (bytes is null)
                return null;
            total += bytes.Value;
        }

        string projectTemp = CtTempPaths.ForWorkspace(new ContinuousTestWorkspace(
            "ws:measure",
            Path.GetTempPath(),
            Path.Combine(Path.GetTempPath(), "measure.csproj"),
            buildOutputRoot));
        if (!Directory.Exists(projectTemp))
            return total;
        long? tempBytes = _options.MeasureDirectoryBytes(projectTemp);
        return tempBytes is null ? null : total + tempBytes.Value;
    }

    /// <summary>
    /// What the generation disk budget counts under one build output root: the per-operation generations,
    /// the remnants of a reap that could not finish, AND the project-stable cache root. The cache is the
    /// LARGEST directory a cargo project holds (about 8 GB on julie-extractors), so leaving it out would
    /// make the 20 GB budget blind to the thing most likely to blow it.
    /// </summary>
    private static bool IsGenerationContent(string directoryName) =>
        CtGenerationPaths.IsGenerationId(directoryName)
        || CtGenerationPaths.IsCacheRootDirectoryName(directoryName)
        || directoryName.Contains(CtGenerationPaths.ReapSuffixPrefix, StringComparison.Ordinal);

    private static IReadOnlyList<string>? TryDirectoryNames(string directory)
    {
        try
        {
            return Directory.EnumerateDirectories(directory).Select(Path.GetFileName).OfType<string>().ToArray();
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed class ReapLedger
    {
        public List<string> Removed { get; } = [];

        public List<ReapDebt> Debts { get; } = [];
    }

    private readonly record struct ReapDebt(string DirectoryName, long Bytes);

    private sealed record RootMeasurement(string Root, long Bytes, bool Stale);

    private sealed record DiskAccounting(
        IReadOnlyList<RootMeasurement> Writes,
        IReadOnlyList<string> OrphanRoots,
        long TotalBytes,
        int RootsTotal,
        int RootsMeasured);

    internal static long? MeasureTreeBytes(string directory)
    {
        try
        {
            long total = 0;
            foreach (string file in Directory.EnumerateFiles(directory, "*", StrictWalk))
                total += new FileInfo(file).Length;
            return total;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static readonly EnumerationOptions StrictWalk = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = false,
    };

    private static readonly EnumerationOptions BestEffortWalk = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
    };

    private static long BestEffortTreeBytes(string directory)
    {
        long total = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(directory, "*", BestEffortWalk))
            {
                try { total += new FileInfo(file).Length; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return total;
    }

    private static IReadOnlyList<string> DirectoryNames(string buildOutputRoot)
    {
        try
        {
            return Directory.EnumerateDirectories(buildOutputRoot).Select(Path.GetFileName).OfType<string>().ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private async Task<T> ExecuteProviderOperationAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        string operationName,
        ContinuousTestWorkspace workspace,
        string? framework,
        CancellationToken cancellationToken)
    {
        using var providerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        providerCancellation.CancelAfter(_options.ProviderOperationTimeout);
        try
        {
            return await operation(providerCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception) when (providerCancellation.IsCancellationRequested)
        {
            throw new ContinuousTestProviderException(
                $"continuous test provider {operationName} timed out after {_options.ProviderOperationTimeout} "
                + $"for framework '{framework ?? "unknown"}' project '{workspace.ProjectPath}'",
                exception);
        }
    }

    private static string NewRunId() => $"ct-run:{Guid.NewGuid():N}";

    internal static CtRevisionObservation ObserveRevision(ContinuousTestCoordinatorRunRequest request)
    {
        if (request.CurrentRevisionResolver is not { } resolver)
            return CtRevisionObservation.Unread;
        string resolved = resolver();
        return string.IsNullOrWhiteSpace(resolved)
            ? CtRevisionObservation.Unread
            : new CtRevisionObservation(resolved, Converged: true);
    }

    private static string ResolveCurrentRevision(ContinuousTestCoordinatorRunRequest request)
    {
        if (request.CurrentRevisionResolver is { } resolver)
        {
            string resolved = resolver();
            if (!string.IsNullOrWhiteSpace(resolved))
                return resolved;
        }

        return request.CurrentRevision;
    }

    private static long ParseRevision(string value)
    {
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) || parsed < 0)
            throw new ArgumentException("must be a non-negative integer", nameof(value));
        return parsed;
    }

    private bool TryImportProviderResultArtifact(
        ContinuousTestCoordinatorRunRequest request,
        ProviderRunResult providerResult,
        string currentRevision)
    {
        if (providerResult.CaseResults.Count > 0)
            return false;
        if (string.IsNullOrWhiteSpace(providerResult.ResultArtifactPath) || !File.Exists(providerResult.ResultArtifactPath))
            return false;
        try
        {
            JunitTestArtifactImporter.Import(
                _store,
                new JunitTestArtifactImportRequest(
                    WorkspaceId: request.Workspace.WorkspaceId,
                    WorkspaceRoot: request.Workspace.WorkspaceRoot,
                    ArtifactPath: providerResult.ResultArtifactPath,
                    SelectedRevision: request.SelectedRevision,
                    IndexIdentity: request.IndexIdentity,
                    Revision: ParseRevision(currentRevision),
                    RunId: providerResult.RunId,
                    TestCaseIdsBySelector: TestCaseIdsByArtifactSelector(request.Workspace.WorkspaceId),
                    ArtifactRoot: request.Workspace.BuildOutputRoot,
                    CurrentRevision: currentRevision));
            return true;
        }
        catch (TestArtifactParseException)
        {
            return false;
        }
    }

    private string? RecordRunArtifact(
        ContinuousTestCoordinatorRunRequest request,
        ProviderRunResult providerResult,
        string currentRevision) =>
        RecordRunArtifactForPath(request, providerResult.ResultArtifactPath, providerResult.RunId, currentRevision);

    private string? RecordRunArtifactForPath(
        ContinuousTestCoordinatorRunRequest request,
        string? resultArtifactPath,
        string runId,
        string currentRevision)
    {
        if (string.IsNullOrWhiteSpace(resultArtifactPath) || !File.Exists(resultArtifactPath))
            return null;
        string root = Path.GetFullPath(request.Workspace.BuildOutputRoot);
        string artifactPath;
        string relativePath;
        try
        {
            artifactPath = JunitTestArtifactImporter.ResolveInsideRoot(root, resultArtifactPath);
            relativePath = JunitTestArtifactImporter.StoredRelativePath(root, artifactPath);
        }
        catch (ArgumentException)
        {
            return null;
        }

        string format = InferArtifactFormat(artifactPath);
        string artifactHash = JunitTestArtifactImporter.Sha256(artifactPath);
        string artifactId = JunitTestArtifactImporter.ComputeArtifactId(
            request.Workspace.WorkspaceId, format, artifactHash);
        _store.PutRunArtifact(new ContinuousTestRunArtifact(
            Id: artifactId,
            WorkspaceId: request.Workspace.WorkspaceId,
            Kind: JunitTestArtifactImporter.Kind,
            Path: relativePath,
            Payload: new Dictionary<string, object?>
            {
                ["format"] = format,
                ["sha256"] = artifactHash,
                ["project_path"] = request.Workspace.ProjectPath,
                ["selected_revision"] = request.SelectedRevision,
                ["current_revision"] = currentRevision,
                ["run_id"] = runId,
            }));
        return artifactId;
    }

    private static string InferArtifactFormat(string artifactPath) =>
        artifactPath.EndsWith(".cargo.log", StringComparison.OrdinalIgnoreCase) ? "cargo-log"
        : artifactPath.EndsWith(".trx", StringComparison.OrdinalIgnoreCase) ? "trx"
        : artifactPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "json"
        : "junit";

    private void ImportProviderCoverageArtifacts(
        ContinuousTestCoordinatorRunRequest request,
        ProviderRunResult providerResult,
        CtRevisionObservation revisionAtStart,
        CtRevisionObservation revisionAtEnd)
    {
        var maps = new List<(CtCoverageMapRecord Record, IReadOnlyList<CtCoverageMapFile> Files)>();
        foreach (ProviderCoverageArtifact artifact in providerResult.CoverageArtifacts)
        {
            if (artifact.TestCaseId is { } testCaseId && !string.IsNullOrWhiteSpace(testCaseId))
            {
                maps.Add(BuildCoverageMapWrite(request, providerResult, artifact, testCaseId, revisionAtStart, revisionAtEnd));
                continue;
            }

            if (string.IsNullOrWhiteSpace(artifact.ArtifactPath) || !File.Exists(artifact.ArtifactPath))
                continue;
            try
            {
                CoverageArtifactImporter.Import(
                    _store,
                    new CoverageArtifactImportRequest(
                        WorkspaceId: request.Workspace.WorkspaceId,
                        WorkspaceRoot: request.Workspace.WorkspaceRoot,
                        ArtifactPath: artifact.ArtifactPath,
                        IndexIdentity: request.IndexIdentity,
                        Revision: ParseRevision(request.CurrentRevision),
                        Parser: artifact.Parser,
                        ArtifactRoot: artifact.ArtifactRoot ?? request.Workspace.BuildOutputRoot));
            }
            catch (Exception ex) when (
                ex is ArgumentException or IOException or UnauthorizedAccessException or TestArtifactParseException)
            {
            }
        }

        if (maps.Count == 0)
            return;
        _store.Transaction(() =>
        {
            foreach ((CtCoverageMapRecord record, IReadOnlyList<CtCoverageMapFile> files) in maps)
                _store.UpsertCtCoverageMap(record, files);
        });
    }

    private (CtCoverageMapRecord Record, IReadOnlyList<CtCoverageMapFile> Files) BuildCoverageMapWrite(
        ContinuousTestCoordinatorRunRequest request,
        ProviderRunResult providerResult,
        ProviderCoverageArtifact artifact,
        string testCaseId,
        CtRevisionObservation revisionAtStart,
        CtRevisionObservation revisionAtEnd)
    {
        ContinuousTestWorkspace workspace = request.Workspace;
        if (string.IsNullOrWhiteSpace(artifact.GenerationId)
            || !string.Equals(artifact.GenerationId, providerResult.GenerationId, StringComparison.Ordinal))
        {
            throw new ContinuousTestProviderException(
                $"coverage artifact '{artifact.ArtifactPath}' for test '{testCaseId}' declares generation "
                + $"'{artifact.GenerationId ?? "none"}', but the run built generation "
                + $"'{providerResult.GenerationId ?? "none"}'");
        }

        string resultsRoot = Path.GetFullPath(CtGenerationPaths.For(workspace, artifact.GenerationId).ResultsDirectory);
        if (!ResolvesInside(resultsRoot, artifact.ArtifactPath))
        {
            throw new ContinuousTestProviderException(
                $"coverage artifact '{artifact.ArtifactPath}' for test '{testCaseId}' lives outside the results root");
        }

        if (!File.Exists(artifact.ArtifactPath))
        {
            throw new ContinuousTestProviderException(
                $"coverage artifact '{artifact.ArtifactPath}' for test '{testCaseId}' was declared but not written");
        }

        string projectPath = Path.GetFullPath(workspace.ProjectPath);
        if (!ProjectOwnsTestCase(workspace.WorkspaceId, testCaseId, projectPath))
        {
            throw new ContinuousTestProviderException(
                $"coverage artifact maps test '{testCaseId}', which project '{projectPath}' does not own");
        }

        bool complete = artifact.Complete == true;
        string? failureReason = artifact.Complete switch
        {
            true => null,
            false => "collector reported an incomplete hit-set",
            null => "collector reported nothing",
        };
        bool trusted = complete
            && revisionAtStart.Converged
            && revisionAtEnd.Converged
            && revisionAtStart.Revision is not null
            && string.Equals(revisionAtStart.Revision, revisionAtEnd.Revision, StringComparison.Ordinal);
        var record = new CtCoverageMapRecord(
            MapId: ContinuousTestStore.CtCoverageMapId(workspace.WorkspaceId, testCaseId),
            WorkspaceId: workspace.WorkspaceId,
            TestCaseId: testCaseId,
            ProjectPath: projectPath,
            RunId: providerResult.RunId,
            GenerationId: artifact.GenerationId,
            IndexIdentity: request.IndexIdentity,
            Revision: ParseRevision(request.CurrentRevision),
            RevisionAtStart: revisionAtStart.Revision,
            StartConverged: revisionAtStart.Converged,
            RevisionAtEnd: revisionAtEnd.Revision,
            EndConverged: revisionAtEnd.Converged,
            Complete: complete,
            FailureReason: failureReason,
            Granularity: "test",
            ValidThroughRevision: trusted ? revisionAtEnd.Revision : null,
            InvalidatedAtRevision: null,
            RecordedAt: DateTimeOffset.UtcNow,
            Source: "maintenance");
        IReadOnlyList<CtCoverageMapFile> files = File.ReadAllLines(artifact.ArtifactPath)
            .Select(static line => line.Trim())
            .Where(static line => line.Length > 0)
            .Select(static line => new CtCoverageMapFile(line, null))
            .ToArray();
        return (record, files);
    }

    private bool ProjectOwnsTestCase(string workspaceId, string testCaseId, string projectPath) =>
        _store.ListTestCases(workspaceId)
            .Where(row => string.Equals(row.Id, testCaseId, StringComparison.Ordinal))
            .Any(row => BuildOutputRootComparer.Equals(
                MetadataString(row.Metadata, "ct_project_path"),
                projectPath));

    private static bool ResolvesInside(string root, string candidatePath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath))
            return false;
        string candidate = Path.GetFullPath(
            Path.IsPathRooted(candidatePath) ? candidatePath : Path.Combine(root, candidatePath));
        string relative = Path.GetRelativePath(root, candidate);
        return relative != "."
            && relative != ".."
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }

    private IReadOnlyDictionary<string, string> TestCaseIdsByArtifactSelector(string workspaceId) =>
        TestCaseIdsByArtifactSelector(_store.ListTestCases(workspaceId));

    /// <summary>
    /// Maps every selector a result artifact can carry to the test case that owns it.
    ///
    /// <para>Keys come in two shapes. A PER-ROW key carries the case's full display name, which holds a
    /// theory data row's arguments, so each row of a pre-enumerated theory resolves to its own case. A
    /// COLLAPSED key - the provider selector, and <c>class::method</c> - drops the arguments, so every
    /// row of one theory claims the same one.</para>
    ///
    /// <para><b>A key that more than one case claims resolves to NOTHING.</b> Both collapsed keys used to
    /// be plain assignments, so the last case written won the key and every result in the run was
    /// attributed to one arbitrary sibling. Results upsert on (workspace, case, run), so N rows then left
    /// ONE row and a red data row could be published as its green sibling's verdict - which breaks the
    /// rule that green needs COMPLETE results.</para>
    ///
    /// <para>A key exactly one case claims is kept, because that is the legitimate fallback: a provider
    /// that does not pre-enumerate, and a theory whose data cannot be enumerated up front, produce one
    /// case for one selector in both discovery and the run.</para>
    /// </summary>
    internal static IReadOnlyDictionary<string, string> TestCaseIdsByArtifactSelector(
        IReadOnlyList<ContinuousTestCase> cases)
    {
        // A null value marks a key more than one case claims.
        var claims = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (ContinuousTestCase row in cases)
        {
            Claim(claims, row.Selector, row.Id);
            string? className = MetadataString(row.Metadata, "class");
            if (string.IsNullOrWhiteSpace(className))
                continue;

            string classPath = className.Replace('.', '/');

            // The per-row keys. A JUnit artifact names a row either by the full display name
            // ("Ns.Class.Method(x: 1)", which is what xUnit v3's -jUnit reporter writes) or by the
            // method part alone ("Method(x: 1)"), so the display name is registered in both shapes.
            // QualifiedName is the provider-neutral field that holds it; the case id is not, because
            // its prefix belongs to whichever provider minted it.
            Claim(claims, $"{classPath}::{row.QualifiedName}", row.Id);
            string classPrefix = className + ".";
            if (row.QualifiedName.StartsWith(classPrefix, StringComparison.Ordinal))
                Claim(claims, $"{classPath}::{row.QualifiedName[classPrefix.Length..]}", row.Id);

            string? methodName = MetadataString(row.Metadata, "method");
            if (!string.IsNullOrWhiteSpace(methodName))
                Claim(claims, $"{classPath}::{methodName}", row.Id);
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string key, string? testCaseId) in claims)
        {
            if (testCaseId is not null)
                result[key] = testCaseId;
        }

        return result;
    }

    /// <summary>
    /// Records one case's claim on a key. A second, different claimant makes the key ambiguous, and an
    /// ambiguous key resolves to nothing rather than to an arbitrary one of its claimants.
    /// </summary>
    private static void Claim(Dictionary<string, string?> claims, string key, string testCaseId)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;
        if (!claims.TryGetValue(key, out string? owner))
        {
            claims[key] = testCaseId;
            return;
        }

        if (!string.Equals(owner, testCaseId, StringComparison.Ordinal))
            claims[key] = null;
    }

    private static string? MetadataString(IReadOnlyDictionary<string, object?> metadata, string name)
    {
        if (!metadata.TryGetValue(name, out object? value) || value is null)
            return null;
        return value switch
        {
            string text => text,
            JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString(),
            JsonElement element when element.ValueKind == JsonValueKind.Null => null,
            JsonElement element => element.ToString(),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture),
        };
    }
}
