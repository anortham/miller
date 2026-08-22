using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Indexing.Semantic;

namespace Miller.Server.Hosting;

/// <summary>
/// The bounded, coalescing wake signal of vectors-v1 §Cursors that connects the indexer-side converger — which
/// only stamps a target revision and wakes — to the vector drain loop. Capacity is 1 by construction: many
/// converges between two drains collapse into one wake carrying the latest target, because the cursors plus
/// <c>revision_file_changes</c> fully determine the outstanding work. The signal is deliberately not durable.
/// </summary>
/// <remarks>
/// A process runs at most one indexer leader and at most one drain loop, so the pair shares one
/// <see cref="Shared"/> instance. <see cref="IndexerSidecarConverger"/> reaches it that way rather than through
/// DI because its only constructor call site lives in <c>IndexerService</c>. Under
/// <see cref="SemanticMode.Off"/> the instance is inert: <see cref="StampTarget"/> returns on a bool check
/// without touching the filesystem, a process, or the semaphore.
/// </remarks>
public sealed class VectorConvergeSignal
{
    private readonly SemaphoreSlim _wake = new(0, 1);
    private long _targetRevision;
    private long _fullRebuildPending;

    public VectorConvergeSignal(bool enabled) => Enabled = enabled;

    /// <summary>The process-wide instance the converger stamps and the drain loop waits on.</summary>
    public static VectorConvergeSignal Shared { get; } =
        new(SemanticActivation.FromEnvironment() is not SemanticMode.Off);

    public bool Enabled { get; }

    /// <summary>The highest target revision stamped since the last drain.</summary>
    public long TargetRevision => Interlocked.Read(ref _targetRevision);

    /// <summary>Records the desired target revision and wakes the drain loop. Cheap enough to run under the
    /// indexer ops gate: no I/O, and a second stamp before the drain runs simply coalesces.</summary>
    public void StampTarget(long revision, bool fullRebuild)
    {
        if (!Enabled || revision <= 0)
            return;

        long observed = Interlocked.Read(ref _targetRevision);
        while (revision > observed)
        {
            long previous = Interlocked.CompareExchange(ref _targetRevision, revision, observed);
            if (previous == observed)
                break;
            observed = previous;
        }

        if (fullRebuild)
            Interlocked.Exchange(ref _fullRebuildPending, 1);

        // A full semaphore IS the coalesced state; dropping the extra release is the capacity-1 contract.
        if (_wake.CurrentCount == 0)
        {
            try
            {
                _wake.Release();
            }
            catch (SemaphoreFullException)
            {
                // Raced with another stamp that released first — the drain is already pending.
            }
        }
    }

    /// <summary>Consumes the full-rebuild flag, so a rebuild is acted on exactly once.</summary>
    public bool TakeFullRebuild() => Interlocked.Exchange(ref _fullRebuildPending, 0) == 1;

    public Task WaitAsync(CancellationToken cancellationToken) => _wake.WaitAsync(cancellationToken);
}

/// <summary>What one drain did to one cursor, so status, telemetry, and tests read the same facts.</summary>
public sealed record VectorCursorOutcome(
    VectorUnitKind Kind,
    VectorConvergeDecision Decision,
    VectorEscalationTrigger Trigger,
    int Embedded,
    int Deleted,
    long CompletedRevision,
    string? LastError);

/// <summary>
/// Everything one drain reads and writes, behind one seam. Splitting it out is what lets the drain loop's
/// snapshot / embed / re-validate / commit ordering be tested without sqlite-vec, a real artifact, or a real
/// sidecar process.
/// </summary>
internal interface IVectorConvergePort : IDisposable
{
    SemanticGenerationIdentity StoredIdentity { get; }

    string? Meta(string key);

    string? StoreSidecarScope { get; }

    void SetMeta(string key, string value);

    /// <summary>The changed-path snapshot taken under the writer gate, before any inference.</summary>
    VectorConvergeSnapshot Snapshot(long completedRevision);

    /// <summary>The live corpus units on <paramref name="paths"/>, or the whole corpus when it is null.</summary>
    IReadOnlyList<VectorCorpusUnit> Units(VectorUnitKind kind, IReadOnlyCollection<string>? paths);

    IReadOnlyList<VectorUnitState> Stored(VectorUnitKind kind, IReadOnlyCollection<string>? paths);

    int TotalStored(VectorUnitKind kind);

    ChunkCursorFacts ChunkFacts(long targetRevision);

    /// <summary>Re-validates generation identity and artifact binding after inference and before the commit,
    /// so a response produced for a superseded generation is never committed.</summary>
    bool StillValid(SemanticGenerationIdentity identity, string artifactId);

    /// <summary>
    /// The one short transaction of vectors-v1 §Cursors: vec0 deletes, vec0 inserts, mapping-table updates and
    /// the cursor advance commit together. <paramref name="advanceTo"/> of zero commits the vectors and leaves
    /// the cursor where it was.
    /// </summary>
    void Commit(
        VectorUnitKind kind,
        IReadOnlyList<VectorCommit> vectors,
        IReadOnlyList<string> delete,
        string completedRevisionKey,
        long advanceTo,
        long revision);

    void PublishCompleteness()
    {
    }
}

/// <summary>
/// The shadow-generation arm of a drain, behind one seam: open a fresh shadow artifact, then promote it over the
/// live one. Splitting it out is what lets escalation execution be tested without sqlite-vec or real files, and
/// keeps the drain loop unaware of file mechanics — those live in <see cref="VectorGenerationManager"/>.
/// </summary>
internal interface IVectorShadowRebuilder
{
    /// <summary>Reclaims any stale shadow trio and opens a shadow generation, seeded from the live generation
    /// when its semantic identity is reusable. Null ⟹ one cannot be built now and the cursor holds.</summary>
    IVectorConvergePort? OpenShadow(IVectorConvergePort live);

    /// <summary>Promotes the built shadow over the live artifact, retaining the superseded generation when the
    /// generation tag changed. The caller has already disposed both ports.</summary>
    void Promote(SemanticGenerationIdentity live, SemanticGenerationIdentity built);
}

/// <summary>
/// The garbage-collection arm of a drain, behind one seam: enumerate the retained generations, plan the GC pass
/// from the pure rules in <see cref="VectorGenerationManager"/>, and delete the eligible ones. Splitting it out
/// is what lets the drain schedule GC without touching a real disk, and keeps the drain unaware of file
/// mechanics — those live in <see cref="VectorGenerationManager"/>.
/// </summary>
internal interface IVectorGenerationGc
{
    /// <summary>Plans and executes one GC pass over the workspace's retained generations. Never throws: a
    /// held-handle deletion failure is logged and left for the next wake.</summary>
    void Collect(bool activeIsReady, DateTimeOffset now, IReadOnlySet<string> tagsWithLiveReaders);
}

/// <summary>
/// The changed-path inputs of one span, read under the gate. <see cref="FullPass"/> is the initial build: the
/// cursor is at zero, so the span is the whole corpus rather than a delta.
/// </summary>
internal sealed record VectorConvergeSnapshot(
    string ArtifactId,
    long TargetRevision,
    bool DeltaHistoryComplete,
    IReadOnlyList<string> ChangedPaths,
    bool FullPass = false);

/// <summary>The durable store target and whether the vector sidecar has published its exact stamp for it.</summary>
internal sealed record VectorDesiredState(long TargetRevision, bool IsExact);

/// <summary>One embedded unit ready to commit.</summary>
internal sealed record VectorCommit(VectorWorkUnit Unit, sbyte[] Embedding);

/// <summary>
/// The per-wake disk gate a shadow rebuild consults with the number of units it is about to embed, so a build
/// that could not fit is refused before it writes a corrupt half-artifact. Injected so the fast suite decides
/// space without touching a real disk; the production gate closes over the workspace's <c>.miller</c> free
/// space and the live artifact's observed bytes-per-unit.
/// </summary>
internal delegate DiskPreflightVerdict DiskGate(int workUnits);

/// <summary>
/// The leader-side drain loop for <c>vectors.db</c>: on wake it recomputes each cursor's changed paths from its
/// own <c>completed_revision</c>, rebuilds card and chunk texts, hash-gates them, embeds outside any gate, then
/// re-validates and commits the batch atomically with the cursor advance.
/// </summary>
/// <remarks>
/// <para><b>Host lifecycle (load-bearing).</b> The .NET Generic Host constructs every hosted service before any
/// <c>StartAsync</c> runs, so this constructor reads NO <see cref="IndexBootstrapService"/> getter. The
/// workspace is read lazily inside <see cref="ExecuteAsync"/>, exactly like the M3 services.</para>
/// <para><b>Off-guarantee.</b> Under <c>MILLER_SEMANTIC=off</c> <see cref="ExecuteAsync"/> returns before
/// waiting, opening, stating, or launching anything at all.</para>
/// <para>The two cursors are drained independently and each keeps its own last-error, so a failing chunk corpus
/// never stalls symbol-card convergence and vice versa.</para>
/// </remarks>
public sealed class VectorConvergeService : BackgroundService
{
    internal const string SymbolCompletedKey = "symbol_completed_revision";
    internal const string SymbolTargetKey = "symbol_target_revision";
    internal const string SymbolErrorKey = "symbol_last_error";
    internal const string SymbolErrorAtKey = "symbol_last_error_at";
    internal const string ChunkCompletedKey = "chunk_completed_revision";
    internal const string ChunkTargetKey = "chunk_target_revision";
    internal const string ChunkErrorKey = "chunk_last_error";
    internal const string ChunkErrorAtKey = "chunk_last_error_at";
    internal const string ChunkSchemaVersionKey = "chunk_content_schema_version";
    internal const string ChunkSourceArtifactKey = "chunk_source_artifact_id";
    internal const string ConvergePauseStateKey = "converge_pause_state";
    internal const string ConvergePauseReasonKey = "converge_pause_reason";
    internal const string ConvergePauseScopeKey = "converge_pause_scope";
    internal const string CircuitOpenPauseValue = "circuit-open";
    internal const string ModelNotPreparedPauseValue = "model-not-prepared";
    internal const string DiskBlockedPauseValue = "disk-blocked";

    /// <summary>A disk gate that always reports space: the default for drains that do not project onto a real
    /// artifact (the fast-suite entry points), and the neutral element of pause resolution.</summary>
    internal static readonly DiskGate AlwaysAvailable = static _ => new DiskPreflightVerdict(true, -1, 0);

    /// <summary>Bounded so one embed call can never outgrow the sidecar's per-request budget.</summary>
    internal const int EmbedBatchSize = 64;

    private const int MaxLastErrorLength = 300;

    private readonly IndexBootstrapService _bootstrap;
    private readonly VectorSidecar _sidecar;
    private readonly VectorConvergeSignal _signal;
    private readonly ILogger _logger;
    private readonly Func<WorkspaceContext, IVectorConvergePort?> _openPort;
    private readonly Func<WorkspaceContext, SemanticEmbeddingSession?> _openSession;
    private readonly SemanticEmbeddingSessionBroker? _broker;
    private readonly Func<WorkspaceContext, IVectorShadowRebuilder?> _openShadow;
    private readonly Func<Exception, string, Action, bool> _recoverCorrupt;
    private readonly Func<WorkspaceContext, IVectorConvergePort, DiskGate> _diskGateFactory;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<WorkspaceContext, IVectorGenerationGc?> _openGc;
    private readonly VectorLiveReaderRegistry _readerRegistry;
    private readonly TimeSpan _heldRetryDelay;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<WorkspaceContext, VectorDesiredState?> _readDesiredState;

    private SemanticEmbeddingSession? _session;
    private IVectorGenerationGc? _gc;
    private string? _gcWorkspaceIdentity;
    private CancellationTokenSource? _pendingRetry;

    public VectorConvergeService(
        IndexBootstrapService bootstrap,
        VectorSidecar sidecar,
        VectorConvergeSignal signal,
        SemanticEmbeddingSessionBroker broker,
        ILogger<VectorConvergeService> logger)
        : this(
            bootstrap,
            sidecar,
            signal,
            logger,
            workspace => SqliteVectorConvergePort.TryOpen(workspace, sidecar.Encoder),
            static _ => null,
            null,
            workspace => SqliteVectorShadowRebuilder.TryOpen(workspace, sidecar.Encoder))
    {
        ArgumentNullException.ThrowIfNull(broker);
        _broker = broker;
    }

    internal VectorConvergeService(
        IndexBootstrapService bootstrap,
        VectorSidecar sidecar,
        VectorConvergeSignal signal,
        ILogger logger,
        Func<WorkspaceContext, IVectorConvergePort?> openPort,
        Func<WorkspaceContext, SemanticEmbeddingSession?> openSession,
        Func<DateTimeOffset>? clock,
        Func<WorkspaceContext, IVectorShadowRebuilder?>? openShadow = null,
        Func<Exception, string, Action, bool>? recoverCorrupt = null,
        Func<WorkspaceContext, IVectorConvergePort, DiskGate>? diskGateFactory = null,
        Func<WorkspaceContext, IVectorGenerationGc?>? openGc = null,
        VectorLiveReaderRegistry? readerRegistry = null,
        TimeSpan? heldRetryDelay = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<WorkspaceContext, VectorDesiredState?>? readDesiredState = null)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        ArgumentNullException.ThrowIfNull(sidecar);
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(openPort);
        ArgumentNullException.ThrowIfNull(openSession);

        _bootstrap = bootstrap;
        _sidecar = sidecar;
        _signal = signal;
        _logger = logger;
        _openPort = openPort;
        _openSession = openSession;
        _broker = null;
        _openShadow = openShadow
            ?? (workspace => SqliteVectorShadowRebuilder.TryOpen(workspace, sidecar.Encoder));
        _recoverCorrupt = recoverCorrupt ?? ((failure, path, rebuild) =>
            SidecarCorruptionRecovery.TryRecoverCorruptVectorGeneration(failure, path, rebuild, logger));
        _diskGateFactory = diskGateFactory ?? ProductionDiskGate;
        _clock = clock ?? (static () => DateTimeOffset.UtcNow);
        _openGc = openGc ?? (workspace => VectorGenerationGc.Create(workspace, _logger));
        _readerRegistry = readerRegistry ?? VectorLiveReaderRegistry.Shared;
        _heldRetryDelay = heldRetryDelay ?? TimeSpan.FromMinutes(5);
        _delay = delay ?? (static (span, token) => Task.Delay(span, token));
        _readDesiredState = readDesiredState ?? ReadDesiredState;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The zero-work guarantee: no wait, no open, no stat, no child process, nothing.
        if (!_sidecar.Enabled)
            return;

        // Start the broker as soon as the host is up. Do not wait for the first index stamp: that stamp
        // arrives after the startup scan, and a long resolve left workspace status at not_started.
        _ = WarmBrokerAsync(stoppingToken);

        try
        {
            await _bootstrap.WaitUntilBoundAsync(stoppingToken).ConfigureAwait(false);
            WorkspaceContext workspace = _bootstrap.Workspace;

            StampMissingDesiredState(workspace);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await _signal.WaitAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                // A real wake supersedes any scheduled held-cursor retry: it either produced this wake or renders
                // the pending one redundant. Cancelling first keeps at most one retry alive and never double-drains.
                CancelPendingRetry();

                if (TryGetWorkspace() is { } currentWorkspace &&
                    TryReadDesiredState(currentWorkspace) is { IsExact: true })
                    continue;

                try
                {
                    if (await DrainOnceAsync(stoppingToken).ConfigureAwait(false))
                        ScheduleHeldRetry(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (IsConvergeException(ex))
                {
                    _logger.LogWarning(ex,
                        "Vector convergence drain failed; the cursors are unchanged and the next wake retries.");
                }
            }
        }
        finally
        {
            CancelPendingRetry();
            await DisposeSessionAsync().ConfigureAwait(false);
        }
    }

    private async Task WarmBrokerAsync(CancellationToken cancellationToken)
    {
        if (_broker is null)
            return;

        try
        {
            // Bootstrap StartAsync returns before it binds. Wait for the workspace; do not treat
            // that gap as a permanent warmup failure.
            while (TryGetWorkspace() is null)
            {
                await _delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
            }

            await _broker.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (IsConvergeException(ex))
        {
            _logger.LogWarning(ex,
                "Semantic broker warmup failed; the first embed retries.");
        }
    }

    /// <summary>Drains once and reports whether any cursor ended held: a hold on a quiet workspace never gets
    /// another index-convergence wake, so the loop schedules a delayed retry when this returns true.</summary>
    internal async Task<bool> DrainOnceAsync(CancellationToken cancellationToken)
    {
        WorkspaceContext? workspace = TryGetWorkspace();
        if (workspace is null)
            return false;

        using IVectorConvergePort? port = OpenPortWithRecovery(workspace);
        if (port is null)
            return UnfinishedPromoteAwaitsRecovery(workspace);

        EmbeddingClient embedding;
        if (_broker is not null)
        {
            if (!_broker.Available)
                return false;
            embedding = EmbeddingClient.For(_broker);
        }
        else
        {
            _session ??= _openSession(workspace);
            if (_session is null)
                return false;
            embedding = EmbeddingClient.For(_session);
        }

        string workspaceIdentity = workspace.WorkspaceId ?? workspace.CanonicalRoot ?? workspace.WorkspaceRoot;
        if (!string.Equals(_gcWorkspaceIdentity, workspaceIdentity, StringComparison.Ordinal))
        {
            _gc = _openGc(workspace);
            _gcWorkspaceIdentity = workspaceIdentity;
        }

        IReadOnlyList<VectorCursorOutcome> outcomes = await DrainAsync(
                port,
                embedding,
                _openShadow(workspace),
                () => OpenPortWithRecovery(workspace),
                _diskGateFactory(workspace, port),
                cancellationToken)
            .ConfigureAwait(false);

        return outcomes.Any(static outcome => outcome.LastError is not null);
    }

    /// <summary>
    /// The production disk gate: free space under the workspace's <c>.miller</c> directory versus the projected
    /// shadow footprint, which is the work-list size times the bytes-per-unit observed on the live artifact. The
    /// probe reads real free space; the verdict is pure. A workspace with no live artifact yet falls back to a
    /// conservative per-unit estimate inside <see cref="DiskPreflight.EstimateRequiredBytes"/>.
    /// </summary>
    private static DiskGate ProductionDiskGate(WorkspaceContext workspace, IVectorConvergePort port)
    {
        string vectorsPath = VectorArtifactPathFor(workspace);
        string millerDir = Path.GetDirectoryName(vectorsPath) ?? vectorsPath;
        long artifactBytes = FileSizeOrZero(vectorsPath);
        int storedUnits = port.TotalStored(VectorUnitKind.Symbol);
        var preflight = new DiskPreflight();

        return workUnits => preflight.Check(
            millerDir, DiskPreflight.EstimateRequiredBytes(workUnits, artifactBytes, storedUnits));
    }

    private static long FileSizeOrZero(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return 0;
        }
    }

    private async Task DisposeSessionAsync()
    {
        if (_session is not { } session)
            return;

        _session = null;
        await session.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Schedules exactly one delayed re-stamp of the converge signal after a held drain, so a quiet
    /// workspace re-drains instead of starving. A retry already pending is left alone (no stacking), and the
    /// re-stamp carries the current target so the re-drain recomputes the same span idempotently.</summary>
    private void ScheduleHeldRetry(CancellationToken stoppingToken)
    {
        long target = _signal.TargetRevision;
        if (target <= 0)
            return;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        if (Interlocked.CompareExchange(ref _pendingRetry, cts, null) is not null)
        {
            cts.Dispose();
            return;
        }

        _ = RunHeldRetryAsync(target, cts);
    }

    private async Task RunHeldRetryAsync(long target, CancellationTokenSource cts)
    {
        try
        {
            await _delay(_heldRetryDelay, cts.Token).ConfigureAwait(false);

            long retryTarget = target;
            if (TryGetWorkspace() is { } workspace && TryReadDesiredState(workspace) is { } desired)
            {
                if (desired.IsExact)
                    return;
                if (desired.TargetRevision > 0)
                    retryTarget = desired.TargetRevision;
            }

            _signal.StampTarget(retryTarget, fullRebuild: false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (Interlocked.CompareExchange(ref _pendingRetry, null, cts) == cts)
                cts.Dispose();
        }
    }

    private void CancelPendingRetry()
    {
        if (Interlocked.Exchange(ref _pendingRetry, null) is { } cts)
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    private void StampMissingDesiredState(WorkspaceContext workspace)
    {
        if (TryReadDesiredState(workspace) is { IsExact: false, TargetRevision: > 0 } desired)
            _signal.StampTarget(desired.TargetRevision, fullRebuild: false);
    }

    private VectorDesiredState? TryReadDesiredState(WorkspaceContext workspace)
    {
        try
        {
            return _readDesiredState(workspace);
        }
        catch (Exception ex) when (IsConvergeException(ex))
        {
            _logger.LogDebug(ex, "Could not probe the vector completeness stamp; the next index wake retries.");
            return null;
        }
    }

    private static VectorDesiredState? ReadDesiredState(WorkspaceContext workspace)
    {
        if (!WorkspaceReadSessionFactory.StoreEnabledFromEnvironment())
            return null;

        string workspaceRoot = workspace.CanonicalRoot ?? workspace.WorkspaceRoot;
        using WorkspaceReadHandle session = WorkspaceReadSessionFactory.Open(
            workspace.CanonicalExtractDbPath ?? Path.Combine(workspaceRoot, ".miller", "symbols.db"),
            workspaceRoot,
            workspace.WorkspaceId,
            storeEnabled: true);
        WorkspaceReadSnapshot snapshot = session.Snapshot;
        if (session.FamilyStoreRoot is not { } storeRoot || snapshot.Freshness.StoreLogSequence is not { } target)
            return null;

        StoreSidecarStamp expected = StoreSidecarStamp.FromSnapshot(StoreSidecarKind.Vector, snapshot);
        string path = StoreSidecarCatalog.PathFor(storeRoot, StoreSidecarKind.Vector, snapshot.ViewId);
        return new VectorDesiredState(target, StoreSidecarCatalog.IsCurrent(path, expected));
    }

    /// <summary>
    /// Whether a null port means "an interrupted promote still needs recovering" rather than "there is no work".
    /// A leftover shadow with no active artifact is exactly that state, and <see cref="SqliteVectorConvergePort.TryOpen"/>
    /// declines to open it rather than creating an empty active artifact that would strand the shadow.
    /// </summary>
    /// <remarks>
    /// The distinction is load-bearing: <see cref="DrainOnceAsync"/> returning false schedules no retry, and the
    /// only other wake comes from index convergence — so on a quiet workspace a recoverable promote would sit
    /// unrecovered until an unrelated source edit.
    /// </remarks>
    private static bool UnfinishedPromoteAwaitsRecovery(WorkspaceContext workspace)
    {
        try
        {
            VectorGenerationManager generations = VectorGenerationManagerFor(workspace);
            return File.Exists(generations.ShadowPath) && !File.Exists(generations.ActivePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Opens the active generation, recovering it in place when the failure is corruption-shaped: the artifact is
    /// deleted and rebuilt from the corpus, siblings and <c>symbols.db</c> untouched (vectors-v1 §Corruption
    /// recovery). A non-corruption open failure propagates to the drain's own retry.
    /// </summary>
    internal IVectorConvergePort? OpenPortWithRecovery(WorkspaceContext workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        try
        {
            return _openPort(workspace);
        }
        catch (Exception ex) when (IsConvergeException(ex))
        {
            string artifact = VectorArtifactPathFor(workspace);
            if (!_recoverCorrupt(ex, artifact, () => _openPort(workspace)?.Dispose()))
                throw;
        }

        // The only production stamp comes from index convergence, so on a quiet workspace there is no next wake:
        // this drain must continue into the rebuilt artifact or it sits empty until an unrelated source change.
        // A second corruption-shaped failure propagates rather than recovering in a loop.
        return _openPort(workspace);
    }

    internal static VectorGenerationManager VectorGenerationManagerFor(WorkspaceContext workspace) =>
        VectorGenerationManager.ForActivePath(VectorArtifactPathFor(workspace));

    internal static string? FamilyStoreRootFor(WorkspaceContext workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (!WorkspaceReadSessionFactory.StoreEnabledFromEnvironment())
            return null;

        string workspaceRoot = workspace.CanonicalRoot ?? workspace.WorkspaceRoot;
        WorkspaceFreshnessProbe probe = WorkspaceReadSessionFactory.Probe(
            workspace.CanonicalExtractDbPath ?? Path.Combine(workspaceRoot, ".miller", "symbols.db"),
            workspaceRoot,
            workspace.WorkspaceId,
            storeEnabled: true);
        return probe.StoreRoot
            ?? throw new InvalidOperationException("The family-store read session has no store root.");
    }

    private static string VectorArtifactPathFor(WorkspaceContext workspace)
    {
        string workspaceRoot = workspace.CanonicalRoot ?? workspace.WorkspaceRoot;
        if (!WorkspaceReadSessionFactory.StoreEnabledFromEnvironment())
            return VectorSidecar.PathFor(workspaceRoot);

        WorkspaceFreshnessProbe probe = WorkspaceReadSessionFactory.Probe(
            workspace.CanonicalExtractDbPath ?? Path.Combine(workspaceRoot, ".miller", "symbols.db"),
            workspaceRoot,
            workspace.WorkspaceId,
            storeEnabled: true);
        return VectorSidecar.PathForStore(
            probe.StoreRoot
                ?? throw new InvalidOperationException("The family-store read session has no store root."),
            probe.ViewId ?? throw new InvalidOperationException("The family-store freshness probe has no view ID."));
    }

    /// <summary>Drains both cursors once. Each cursor is independent: one failing never blocks the other.</summary>
    internal async Task<IReadOnlyList<VectorCursorOutcome>> DrainAsync(
        IVectorConvergePort port,
        SemanticEmbeddingSession session,
        CancellationToken cancellationToken) =>
        await DrainAsync(
            port, EmbeddingClient.For(session), null, null, AlwaysAvailable, cancellationToken).ConfigureAwait(false);

    internal async Task<IReadOnlyList<VectorCursorOutcome>> DrainAsync(
        IVectorConvergePort port,
        SemanticEmbeddingSessionBroker broker,
        CancellationToken cancellationToken) =>
        await DrainAsync(
            port, EmbeddingClient.For(broker), null, null, AlwaysAvailable, cancellationToken).ConfigureAwait(false);

    internal async Task<IReadOnlyList<VectorCursorOutcome>> DrainAsync(
        IVectorConvergePort port,
        SemanticEmbeddingSession session,
        IVectorShadowRebuilder? rebuilder,
        CancellationToken cancellationToken) =>
        await DrainAsync(
            port, EmbeddingClient.For(session), rebuilder, null, AlwaysAvailable, cancellationToken).ConfigureAwait(false);

    internal async Task<IReadOnlyList<VectorCursorOutcome>> DrainAsync(
        IVectorConvergePort port,
        SemanticEmbeddingSession session,
        IVectorShadowRebuilder? rebuilder,
        Func<IVectorConvergePort?>? reopenAfterPromote,
        CancellationToken cancellationToken) =>
        await DrainAsync(
                port, EmbeddingClient.For(session), rebuilder, reopenAfterPromote, AlwaysAvailable, cancellationToken)
            .ConfigureAwait(false);

    internal async Task<IReadOnlyList<VectorCursorOutcome>> DrainAsync(
        IVectorConvergePort port,
        SemanticEmbeddingSession session,
        IVectorShadowRebuilder? rebuilder,
        Func<IVectorConvergePort?>? reopenAfterPromote,
        DiskGate diskGate,
        CancellationToken cancellationToken)
        => await DrainAsync(
            port,
            EmbeddingClient.For(session),
            rebuilder,
            reopenAfterPromote,
            diskGate,
            cancellationToken).ConfigureAwait(false);

    private async Task<IReadOnlyList<VectorCursorOutcome>> DrainAsync(
        IVectorConvergePort port,
        EmbeddingClient embedding,
        IVectorShadowRebuilder? rebuilder,
        Func<IVectorConvergePort?>? reopenAfterPromote,
        DiskGate diskGate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(port);
        ArgumentNullException.ThrowIfNull(diskGate);

        await embedding.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        var state = new DrainState(rebuilder, diskGate, _signal.TakeFullRebuild());
        VectorCursorOutcome symbols = await DrainCursorAsync(
            port, embedding, VectorUnitKind.Symbol, state, cancellationToken).ConfigureAwait(false);

        // A promote disposed the live port and replaced the file underneath it. The only production stamp comes
        // from index convergence, so on a quiet workspace there is no next wake: the chunk cursor must converge
        // into the reopened artifact NOW or docs sit unembedded until an unrelated source change. The promoted
        // artifact is a fresh generation with no pause stamp, so resolving the pause on it is what clears a stale
        // disk-blocked or circuit-open pause the superseded artifact carried.
        if (state.Promoted)
        {
            _signal.StampTarget(_signal.TargetRevision, fullRebuild: false);
            if (reopenAfterPromote?.Invoke() is not { } reopened)
                return [symbols];

            using (reopened)
            {
                VectorCursorOutcome promotedChunks = await DrainChunkToCompletionAsync(
                    reopened, embedding, state, cancellationToken).ConfigureAwait(false);
                ResolvePause(reopened, embedding, state);
                CollectGarbage(reopened);
                reopened.PublishCompleteness();
                return [symbols, promotedChunks];
            }
        }

        VectorCursorOutcome chunks = await DrainChunkToCompletionAsync(
            port, embedding, state, cancellationToken).ConfigureAwait(false);

        ResolvePause(port, embedding, state);
        CollectGarbage(port);
        port.PublishCompleteness();
        return [symbols, chunks];
    }

    /// <summary>
    /// One GC pass at the tail of a drain, on the leader that just converged: a fresh promote leaves a superseded
    /// generation beside the active artifact, and every leader wake sweeps whatever aged past its soak window with
    /// no live in-process reader. GC is wake-gated by construction — a reader instance's converge signal is never
    /// stamped, so it never drains and never collects. Failures are swallowed so a GC fault can never crash the
    /// drain; the retained files are simply revisited next wake.
    /// </summary>
    private void CollectGarbage(IVectorConvergePort port)
    {
        if (_gc is null)
            return;

        bool activeIsReady = string.Equals(port.Meta("build_state"), "ready", StringComparison.Ordinal);
        try
        {
            _gc.Collect(activeIsReady, _clock(), _readerRegistry.LiveTags);
        }
        catch (Exception ex) when (IsConvergeException(ex))
        {
            _logger.LogWarning(ex,
                "Vector generation GC failed; the retained generations will be revisited on the next wake.");
        }
    }

    /// <summary>
    /// Drains the chunk cursor, replanning after each bounded over-cap batch so an arbitrarily large span
    /// converges within one wake. The iteration guard only backstops a port whose stored view fails to reflect
    /// its own commits; each pass otherwise strictly shrinks the remaining span.
    /// </summary>
    private async Task<VectorCursorOutcome> DrainChunkToCompletionAsync(
        IVectorConvergePort port,
        EmbeddingClient embedding,
        DrainState state,
        CancellationToken cancellationToken)
    {
        const int maxBoundedBatches = 10_000;

        VectorCursorOutcome chunks = await DrainCursorAsync(
            port, embedding, VectorUnitKind.Chunk, state, cancellationToken).ConfigureAwait(false);

        for (int batch = 0;
            batch < maxBoundedBatches
                && chunks.Embedded > 0
                && string.Equals(chunks.LastError, VectorConvergePlanner.BoundedBatchHoldReason, StringComparison.Ordinal);
            batch++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            chunks = await DrainCursorAsync(
                port, embedding, VectorUnitKind.Chunk, state, cancellationToken).ConfigureAwait(false);
        }

        return chunks;
    }

    private async Task<VectorCursorOutcome> DrainCursorAsync(
        IVectorConvergePort port,
        EmbeddingClient embedding,
        VectorUnitKind kind,
        DrainState state,
        CancellationToken cancellationToken)
    {
        (string completedKey, string targetKey, string errorKey, string errorAtKey) = Keys(kind);
        long completed = ReadRevision(port, completedKey);

        try
        {
            VectorConvergeSnapshot snapshot = port.Snapshot(completed);
            port.SetMeta(targetKey, Number(snapshot.TargetRevision));

            IReadOnlyList<string> deferredPaths = [];
            if (kind is VectorUnitKind.Chunk)
            {
                ChunkCursorDecision gate = VectorConvergePlanner.EvaluateChunkCursor(port.ChunkFacts(snapshot.TargetRevision));
                if (gate.ResetCursor)
                {
                    port.SetMeta(ChunkCompletedKey, "0");
                    port.SetMeta(ChunkTargetKey, "0");
                    port.SetMeta(ChunkSourceArtifactKey, snapshot.ArtifactId);
                    return Failed(kind, 0, RecordError(port, errorKey, errorAtKey, gate.Reason));
                }

                if (!gate.CanAdvance && gate.DeferredPaths.Count == 0)
                    return Failed(kind, completed, RecordError(port, errorKey, errorAtKey, gate.Reason));

                deferredPaths = gate.DeferredPaths;
                if (deferredPaths.Count > 0)
                {
                    _logger.LogInformation(
                        "Vector chunk convergence deferred {Count} source(s) for symbols.db hash disagreement: {Paths}.",
                        deferredPaths.Count,
                        string.Join(", ", deferredPaths));
                }
            }

            IReadOnlyList<string>? paths = snapshot.FullPass
                ? null
                : [.. snapshot.ChangedPaths.Except(deferredPaths, StringComparer.Ordinal)];

            var request = new VectorConvergeRequest
            {
                Kind = kind,
                CompletedRevision = completed,
                TargetRevision = snapshot.TargetRevision,
                Candidates = port.Units(kind, paths),
                Stored = port.Stored(kind, paths),
                TotalStoredUnits = port.TotalStored(kind),
                DeltaHistoryComplete = snapshot.DeltaHistoryComplete,
                ArtifactIdChanged = !string.Equals(
                    snapshot.ArtifactId, port.Meta("artifact_id"), StringComparison.Ordinal),
                FullRebuildSignalled = state.FullRebuildSignalled && !state.Promoted,
                IdentityAction = MillerSemanticContract.ClassifyChange(
                    port.StoredIdentity, MillerSemanticContract.PinnedIdentity(_sidecar.Encoder)),
                DeferredPaths = deferredPaths,
            };

            VectorConvergePlan plan = VectorConvergePlanner.Plan(request);
            if (plan.Decision is VectorConvergeDecision.ShadowRebuild)
            {
                return await RunShadowRebuildAsync(
                        port, embedding, kind, plan, completed, state, errorKey, errorAtKey, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (plan.ReEmbed.Count == 0 && plan.Delete.Count == 0)
            {
                if (plan.AdvanceTo > completed)
                    port.Commit(kind, [], [], completedKey, plan.AdvanceTo, plan.AdvanceTo);
                if (plan.HoldReason is null)
                    ClearError(port, errorKey, errorAtKey);
                else
                    RecordError(port, errorKey, errorAtKey, plan.HoldReason);
                return new VectorCursorOutcome(
                    kind, plan.Decision, plan.Trigger, 0, 0, Math.Max(plan.AdvanceTo, completed), plan.HoldReason);
            }

            // Mirror the shadow path: an incremental re-embed that could not fit holds the cursor with a
            // disk-blocked pause instead of writing a partial vectors.db or hard-failing. Deletes shrink the
            // artifact, so only a growing re-embed is gated.
            if (plan.ReEmbed.Count > 0
                && RefuseIncrementalForDisk(state, port, plan.ReEmbed.Count, errorKey, errorAtKey) is { } diskBlocked)
            {
                return Failed(kind, completed, diskBlocked);
            }

            var embedClock = Stopwatch.StartNew();
            (List<VectorCommit> embedded, _, string? embedFailure) =
                await EmbedAsync(embedding, plan.ReEmbed, cancellationToken).ConfigureAwait(false);
            embedClock.Stop();
            if (embedFailure is not null)
                return Failed(kind, completed, RecordError(port, errorKey, errorAtKey, embedFailure));

            // Model swap mid-flight: a response produced for a superseded generation is never committed.
            if (!port.StillValid(port.StoredIdentity, snapshot.ArtifactId))
            {
                return Failed(kind, completed, RecordError(
                    port, errorKey, errorAtKey,
                    "the generation changed while embedding was in flight; the batch was discarded"));
            }

            bool complete = embedded.Count == plan.ReEmbed.Count;
            long advanceTo = complete ? plan.AdvanceTo : 0;
            port.Commit(kind, embedded, plan.Delete, completedKey, advanceTo, snapshot.TargetRevision);

            if (complete && plan.HoldReason is null)
                ClearError(port, errorKey, errorAtKey);
            else
                RecordError(port, errorKey, errorAtKey, plan.HoldReason ?? "some units could not be embedded");

            if (embedded.Count > 0)
            {
                _logger.LogInformation(
                    "Vector {Cursor} convergence embedded {Count} unit(s) in {ElapsedMs} ms.",
                    kind, embedded.Count, embedClock.ElapsedMilliseconds);
            }

            return new VectorCursorOutcome(
                kind,
                plan.Decision,
                plan.Trigger,
                embedded.Count,
                plan.Delete.Count,
                advanceTo > 0 ? advanceTo : completed,
                complete && plan.HoldReason is null ? null : plan.HoldReason);
        }
        catch (Exception ex) when (IsConvergeException(ex))
        {
            // The cursor is untouched, so the next drain recomputes the same span from completed_revision and
            // the hash gate makes the replay idempotent.
            _logger.LogWarning(ex, "Vector {Cursor} convergence failed; the cursor stays at {Revision}.", kind, completed);
            return Failed(kind, completed, RecordError(port, errorKey, errorAtKey, ex.Message));
        }
    }

    /// <summary>
    /// Executes the escalation the planner surfaced: build a whole new generation beside the live one, then
    /// promote it. Bounded to one attempt per wake — a failed shadow build records the cursor's last error and
    /// holds, so escalation can never spin a hot loop. The cursor keeps holding until a promote succeeds.
    /// </summary>
    private async Task<VectorCursorOutcome> RunShadowRebuildAsync(
        IVectorConvergePort live,
        EmbeddingClient embedding,
        VectorUnitKind kind,
        VectorConvergePlan plan,
        long completed,
        DrainState state,
        string errorKey,
        string errorAtKey,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Vector {Cursor} convergence escalates to a shadow rebuild ({Trigger}).", kind, plan.Trigger);

        if (state.Rebuilder is null)
            return Escalated(kind, plan, completed, null);

        if (state.ShadowAttempted)
        {
            return Escalated(kind, plan, completed, RecordError(live, errorKey, errorAtKey,
                "a shadow rebuild was already attempted on this wake; the cursor holds until the next wake"));
        }

        state.ShadowAttempted = true;

        // Preflight BEFORE opening the shadow so a disk-blocked refusal never even creates a .rebuild file: the
        // cursor holds with a disk-blocked pause instead of failing mid-build with a corrupt half-artifact.
        if (RefuseForDisk(state, live, LiveRebuildUnitCount(live), errorKey, errorAtKey) is { } blocked)
            return Escalated(kind, plan, completed, blocked);

        IVectorConvergePort? shadow = null;

        try
        {
            shadow = state.Rebuilder.OpenShadow(live);
            if (shadow is null)
            {
                return Escalated(kind, plan, completed, RecordError(live, errorKey, errorAtKey,
                    "a shadow generation could not be created; the cursor holds"));
            }

            int seeded = shadow.TotalStored(VectorUnitKind.Symbol);
            if (seeded > 0)
            {
                _logger.LogInformation(
                    "Shadow vector rebuild seeded with {Count} reusable symbol cards from the live generation.",
                    seeded);
            }
            else if (live.TotalStored(VectorUnitKind.Symbol) > 0)
            {
                _logger.LogInformation(
                    "Shadow vector rebuild starts unseeded: the live generation's identity is not reuse-compatible.");
            }

            var buildClock = Stopwatch.StartNew();
            (int embedded, int deleted, int flagged, string? failure) =
                await BuildShadowAsync(shadow, embedding, state, cancellationToken).ConfigureAwait(false);
            if (failure is not null)
                return Escalated(kind, plan, completed, RecordError(live, errorKey, errorAtKey, failure));

            SemanticGenerationIdentity superseded = live.StoredIdentity;
            SemanticGenerationIdentity built = shadow.StoredIdentity;

            // Both files must be closed before the rename: a promote over an open handle fails on Windows.
            shadow.Dispose();
            shadow = null;
            live.Dispose();
            state.Promoted = true;

            state.Rebuilder.Promote(superseded, built);
            buildClock.Stop();
            _logger.LogInformation(
                "Promoted a shadow vector generation with {Embedded} embedded symbol cards, {Deleted} deleted " +
                "({Flagged} flagged) " +
                "in {ElapsedMs} ms ({CardsPerSecond:F0} cards/s).",
                embedded,
                deleted,
                flagged,
                buildClock.ElapsedMilliseconds,
                embedded / Math.Max(buildClock.Elapsed.TotalSeconds, 0.001));

            return new VectorCursorOutcome(kind, plan.Decision, plan.Trigger, embedded, deleted, completed, null);
        }
        catch (Exception ex) when (IsConvergeException(ex))
        {
            _logger.LogWarning(ex, "Shadow vector rebuild failed; the cursor stays at {Revision}.", completed);

            // After the live port is disposed there is nothing left to record onto; the retained generation makes
            // that state recoverable and the next wake rebuilds.
            return Escalated(kind, plan, completed,
                state.Promoted ? null : RecordError(live, errorKey, errorAtKey, ex.Message));
        }
        finally
        {
            shadow?.Dispose();
        }
    }

    /// <summary>
    /// Fills a shadow generation with the whole symbol corpus, reusing hash-identical vectors from a compatible
    /// seed and committing changed cards in bounded slices. Flagged cards are removed from the seed so stale
    /// embeddings cannot survive the promote; a build with no usable vectors refuses to promote. The chunk cursor
    /// is left at zero so it converges on the promoted artifact through its own gate.
    /// </summary>
    private async Task<(int Embedded, int Deleted, int Flagged, string? Failure)> BuildShadowAsync(
        IVectorConvergePort shadow,
        EmbeddingClient embedding,
        DrainState state,
        CancellationToken cancellationToken)
    {
        (string completedKey, string targetKey, _, _) = Keys(VectorUnitKind.Symbol);
        VectorConvergeSnapshot snapshot = shadow.Snapshot(0);
        long target = snapshot.TargetRevision;
        shadow.SetMeta("artifact_id", snapshot.ArtifactId);
        shadow.SetMeta(targetKey, Number(target));
        shadow.SetMeta(completedKey, "0");
        shadow.SetMeta(ChunkCompletedKey, "0");
        shadow.SetMeta(ChunkTargetKey, "0");
        shadow.SetMeta(ChunkSourceArtifactKey, snapshot.ArtifactId);
        shadow.SetMeta(ChunkErrorKey, string.Empty);
        shadow.SetMeta(ChunkErrorAtKey, string.Empty);
        shadow.SetMeta(SymbolErrorKey, string.Empty);
        shadow.SetMeta(SymbolErrorAtKey, string.Empty);
        shadow.SetMeta(ConvergePauseStateKey, string.Empty);
        shadow.SetMeta(ConvergePauseReasonKey, string.Empty);
        if (shadow.StoreSidecarScope is not null)
            shadow.SetMeta(ConvergePauseScopeKey, string.Empty);

        IReadOnlyList<VectorCorpusUnit> candidates = shadow.Units(VectorUnitKind.Symbol, null);
        IReadOnlyList<VectorUnitState> stored = shadow.Stored(VectorUnitKind.Symbol, null);
        IReadOnlyList<VectorWorkUnit> work = VectorConvergePlanner.RebuildWorkList(candidates, stored);
        IReadOnlyList<string> vanished = VectorConvergePlanner.RebuildDeleteList(candidates, stored);

        if (work.Count == 0)
        {
            shadow.Commit(VectorUnitKind.Symbol, [], vanished, completedKey, target, target);
            return (0, vanished.Count, 0, null);
        }

        int embeddedTotal = 0;
        int flaggedTotal = 0;
        for (int offset = 0; offset < work.Count; offset += VectorConvergePlanner.MaxUnitsPerTransaction)
        {
            // Re-check at each slice boundary as the shadow grows: space that held at entry can be exhausted by
            // the slices already written, and a mid-build stop must refuse rather than write on into a corruption.
            if (BlockedForDisk(state, work.Count - offset) is { } failed)
                return (0, 0, 0, failed);

            IReadOnlyList<VectorWorkUnit> slice =
                [.. work.Skip(offset).Take(VectorConvergePlanner.MaxUnitsPerTransaction)];
            (List<VectorCommit> embedded, int flagged, string? failure) =
                await EmbedAsync(embedding, slice, cancellationToken).ConfigureAwait(false);
            if (failure is not null)
                return (0, 0, 0, failure);

            if (embedded.Count + flagged != slice.Count)
                return (0, 0, 0, "some units could not be embedded into the shadow generation");

            bool final = offset + slice.Count >= work.Count;
            var embeddedIds = embedded.Select(static commit => commit.Unit.UnitId).ToHashSet(StringComparer.Ordinal);
            List<string> delete =
            [
                .. slice.Where(unit => !embeddedIds.Contains(unit.UnitId)).Select(static unit => unit.UnitId),
            ];
            if (final)
                delete.AddRange(vanished);

            shadow.Commit(VectorUnitKind.Symbol, embedded, delete, completedKey, final ? target : 0, target);
            embeddedTotal += embedded.Count;
            flaggedTotal += flagged;
        }

        if (embeddedTotal == 0 && shadow.TotalStored(VectorUnitKind.Symbol) == 0)
            return (0, 0, flaggedTotal, "every unit was flagged by the sidecar; refusing to promote an empty shadow generation");

        return (embeddedTotal, vanished.Count, flaggedTotal, null);
    }

    /// <summary>Inference, OUTSIDE any gate, in bounded batches.</summary>
    private static async Task<(List<VectorCommit> Embedded, int Flagged, string? Failure)> EmbedAsync(
        EmbeddingClient embedding,
        IReadOnlyList<VectorWorkUnit> units,
        CancellationToken cancellationToken)
    {
        List<VectorCommit> embedded = [];
        int flaggedCount = 0;
        for (int offset = 0; offset < units.Count; offset += EmbedBatchSize)
        {
            IReadOnlyList<VectorWorkUnit> batch = [.. units.Skip(offset).Take(EmbedBatchSize)];
            SemanticEmbedOutcome outcome = await embedding
                .EmbedBatchAsync([.. batch.Select(static u => u.Text)], cancellationToken)
                .ConfigureAwait(false);

            if (!outcome.Succeeded)
                return ([], 0, outcome.FailureReason);

            var flagged = outcome.FlaggedIndices.ToHashSet();
            for (int i = 0; i < batch.Count && i < outcome.Vectors.Count; i++)
            {
                // A poison unit is isolated rather than committed as a zero vector: leaving it unwritten keeps
                // its embed_text_hash absent, so the next drain retries exactly that unit.
                if (flagged.Contains(i))
                {
                    flaggedCount++;
                    continue;
                }

                embedded.Add(new VectorCommit(batch[i], QuantizeToInt8(outcome.Vectors[i])));
            }
        }

        return (embedded, flaggedCount, null);
    }

    private static VectorCursorOutcome Escalated(
        VectorUnitKind kind, VectorConvergePlan plan, long completed, string? error) =>
        new(kind, plan.Decision, plan.Trigger, 0, 0, completed, error);

    /// <summary>The whole-corpus symbol count a shadow rebuild will embed, used to project its disk footprint
    /// before the shadow is opened. Every eligible unit is stored fresh, so the live unit count is the estimate;
    /// the hash gate only ever shrinks it.</summary>
    private static int LiveRebuildUnitCount(IVectorConvergePort live) =>
        live.Units(VectorUnitKind.Symbol, null).Count;

    /// <summary>Records a disk-blocked hold on the live artifact and marks the drain's pause state, returning the
    /// stored reason. Used at shadow-rebuild entry, before any shadow file exists.</summary>
    private string? RefuseForDisk(
        DrainState state, IVectorConvergePort live, int workUnits, string errorKey, string errorAtKey)
    {
        DiskPreflightVerdict verdict = state.DiskGate(workUnits);
        if (verdict.Ok)
            return null;

        state.MarkDiskBlocked(verdict);
        return RecordError(live, errorKey, errorAtKey, DiskBlockedReason(verdict, ShadowBuildAction));
    }

    /// <summary>The incremental variant: an in-place re-embed that cannot fit marks the pause and records a
    /// disk-blocked hold on the same cursor, so status renders it exactly as the shadow path's block does.</summary>
    private string? RefuseIncrementalForDisk(
        DrainState state, IVectorConvergePort port, int workUnits, string errorKey, string errorAtKey)
    {
        DiskPreflightVerdict verdict = state.DiskGate(workUnits);
        if (verdict.Ok)
            return null;

        state.MarkDiskBlocked(verdict);
        return RecordError(port, errorKey, errorAtKey, DiskBlockedReason(verdict, "converge the vector cursor"));
    }

    /// <summary>The mid-build variant: a slice boundary refusal marks the pause and returns the build-failure
    /// reason, so the shadow arm records it on the live artifact and holds.</summary>
    private static string? BlockedForDisk(DrainState state, int remainingUnits)
    {
        DiskPreflightVerdict verdict = state.DiskGate(remainingUnits);
        if (verdict.Ok)
            return null;

        state.MarkDiskBlocked(verdict);
        return DiskBlockedReason(verdict, ShadowBuildAction);
    }

    private const string ShadowBuildAction = "build a shadow vector generation";

    private static string DiskBlockedReason(DiskPreflightVerdict verdict, string action) =>
        $"not enough free disk under .miller to {action} ({verdict.Reason}); the cursor holds";

    /// <summary>Per-wake state shared by the two cursors: the shadow arm is attempted at most once, a promote
    /// ends the drain, and a disk refusal is remembered so the pause is resolved once at the end.</summary>
    private sealed class DrainState(
        IVectorShadowRebuilder? rebuilder, DiskGate diskGate, bool fullRebuildSignalled = false)
    {
        public IVectorShadowRebuilder? Rebuilder { get; } = rebuilder;

        /// <summary>
        /// The indexer's own report that this wake follows a full rebuild, consumed once per drain so every
        /// cursor sees it. Independent of the artifact-id comparison on purpose: that comparison is one reading
        /// of the artifact, while this is the writer stating what it just did.
        /// </summary>
        public bool FullRebuildSignalled { get; } = fullRebuildSignalled;

        public DiskGate DiskGate { get; } = diskGate;

        public bool ShadowAttempted { get; set; }

        /// <summary>
        /// Whether the live port has been disposed for a promote. Set BEFORE the rename returns, on purpose:
        /// its two readers both need the pre-rename fact. The error path must not record onto a disposed port,
        /// and the post-promote chunk drain reopens the artifact rather than trusting the old handle. A promote
        /// that throws after this point leaves the artifact recoverable through the retained generation.
        /// </summary>
        public bool Promoted { get; set; }

        public bool DiskBlocked { get; private set; }

        public string? DiskBlockedReason { get; private set; }

        public void MarkDiskBlocked(DiskPreflightVerdict verdict)
        {
            DiskBlocked = true;
            DiskBlockedReason = verdict.Reason;
        }
    }

    /// <summary>The writer's half of the shared lane quantization; the reader's query path quantizes with the
    /// same <see cref="SemanticVectorQuantizer"/> so both sides land in one space.</summary>
    internal static sbyte[] QuantizeToInt8(IReadOnlyList<float> vector) => SemanticVectorQuantizer.ToInt8(vector);

    internal static (string Completed, string Target, string Error, string ErrorAt) Keys(VectorUnitKind kind) =>
        kind is VectorUnitKind.Symbol
            ? (SymbolCompletedKey, SymbolTargetKey, SymbolErrorKey, SymbolErrorAtKey)
            : (ChunkCompletedKey, ChunkTargetKey, ChunkErrorKey, ChunkErrorAtKey);

    private WorkspaceContext? TryGetWorkspace()
    {
        try
        {
            return _bootstrap.Workspace;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static VectorCursorOutcome Failed(VectorUnitKind kind, long completed, string? error) =>
        new(kind, VectorConvergeDecision.Incremental, VectorEscalationTrigger.None, 0, 0, completed, error);

    private string? RecordError(IVectorConvergePort port, string errorKey, string errorAtKey, string? reason)
    {
        string scrubbed = Scrub(reason);
        port.SetMeta(errorKey, scrubbed);
        port.SetMeta(errorAtKey, _clock().ToString("O", CultureInfo.InvariantCulture));
        return scrubbed;
    }

    private static void ClearError(IVectorConvergePort port, string errorKey, string errorAtKey)
    {
        port.SetMeta(errorKey, string.Empty);
        port.SetMeta(errorAtKey, string.Empty);
    }

    /// <summary>
    /// The single pause-resolution point for the two producers that stamp <c>converge_pause_state</c>. It records
    /// or clears the convergence pause on the artifact so a reader instance — not just the leader that tripped it
    /// — reports the pause instead of a stale <c>ready</c>. Precedence is explicit: an open circuit outranks a
    /// disk block (it is the more fundamental stop), so a wake that is both stamps <c>circuit-open</c>. The
    /// transition is detected against the artifact's own meta rather than an in-process flag, and only the
/// stamp⟶, scope-change, and ⟶clear edges write, keeping the hot drain loop off <c>vectors_meta</c>. A cleared
/// pause empties state, reason, and family-store scope, which the consumer's <c>VectorSidecar.PauseState</c>
/// treats as absent.
    /// </summary>
    private static void ResolvePause(IVectorConvergePort port, EmbeddingClient embedding, DrainState state)
    {
        (string desiredState, string desiredReason) = embedding.State switch
        {
            SemanticSessionState.CircuitOpen =>
                (CircuitOpenPauseValue, Scrub(embedding.UnavailableReason)),
            SemanticSessionState.ModelNotPrepared =>
                (ModelNotPreparedPauseValue, Scrub(embedding.UnavailableReason)),
            _ when state.DiskBlocked =>
                (DiskBlockedPauseValue, Scrub(state.DiskBlockedReason)),
            _ => (string.Empty, string.Empty),
        };

        string current = port.Meta(ConvergePauseStateKey) ?? string.Empty;
        string currentReason = port.Meta(ConvergePauseReasonKey) ?? string.Empty;
        string currentScope = port.Meta(ConvergePauseScopeKey) ?? string.Empty;
        string? desiredScope = port.StoreSidecarScope;
        if (string.IsNullOrEmpty(desiredState))
        {
            if (string.IsNullOrEmpty(current) &&
                string.IsNullOrEmpty(currentReason) &&
                string.IsNullOrEmpty(currentScope))
            {
                return;
            }

            port.SetMeta(ConvergePauseStateKey, string.Empty);
            port.SetMeta(ConvergePauseReasonKey, string.Empty);
            if (desiredScope is not null || !string.IsNullOrEmpty(currentScope))
                port.SetMeta(ConvergePauseScopeKey, string.Empty);
            return;
        }

        if (string.Equals(current, desiredState, StringComparison.Ordinal) &&
            (desiredScope is null || string.Equals(currentScope, desiredScope, StringComparison.Ordinal)))
        {
            return;
        }

        port.SetMeta(ConvergePauseStateKey, desiredState);
        port.SetMeta(ConvergePauseReasonKey, desiredReason);
        if (desiredScope is not null)
            port.SetMeta(ConvergePauseScopeKey, desiredScope);
    }

    /// <summary>Persisted last-errors are bounded and carry no path: anything absolute is replaced by its file
    /// name, per the RPC-boundary scrubbing rule vectors-v1 §Cursors applies to stored reasons.</summary>
    internal static string Scrub(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return "unknown";

        string[] words = reason.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
        {
            if (words[i].Contains('/', StringComparison.Ordinal) || words[i].Contains('\\', StringComparison.Ordinal))
                words[i] = "<path>";
        }

        string joined = string.Join(' ', words);
        return joined.Length <= MaxLastErrorLength ? joined : joined[..MaxLastErrorLength];
    }

    private static long ReadRevision(IVectorConvergePort port, string key) =>
        long.TryParse(port.Meta(key), NumberStyles.None, CultureInfo.InvariantCulture, out long value) ? value : 0;

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static bool IsConvergeException(Exception ex) =>
        ex is SqliteException or IOException or InvalidOperationException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or FormatException or TimeoutException or VectorStoreException;

    private sealed class EmbeddingClient(
        Func<IReadOnlyList<string>, CancellationToken, Task<SemanticEmbedOutcome>> embedBatch,
        Func<CancellationToken, Task<SemanticEncoderHandshake?>> ensureReady,
        Func<SemanticSessionState> state,
        Func<string?> unavailableReason)
    {
        public SemanticSessionState State => state();

        public string? UnavailableReason => unavailableReason();

        public Task<SemanticEmbedOutcome> EmbedBatchAsync(
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken) => embedBatch(texts, cancellationToken);

        public Task<SemanticEncoderHandshake?> EnsureReadyAsync(CancellationToken cancellationToken) =>
            ensureReady(cancellationToken);

        public static EmbeddingClient For(SemanticEmbeddingSession session)
        {
            ArgumentNullException.ThrowIfNull(session);
            return new EmbeddingClient(
                session.EmbedBatchAsync,
                session.EnsureStartedAsync,
                () => session.State,
                () => session.UnavailableReason);
        }

        public static EmbeddingClient For(SemanticEmbeddingSessionBroker broker)
        {
            ArgumentNullException.ThrowIfNull(broker);
            return new EmbeddingClient(
                broker.EmbedBatchAsync,
                broker.EnsureReadyAsync,
                () => broker.State,
                () => broker.UnavailableReason);
        }
    }

}

/// <summary>
/// The production shadow arm: a fresh generation at <c>vectors.db.rebuild</c>, promoted over the live artifact
/// by <see cref="VectorGenerationManager"/> — which retains the superseded generation under its own tag when the
/// promote is incompatible, so a matching reader keeps serving through the soak window.
/// </summary>
internal sealed class SqliteVectorShadowRebuilder(
    WorkspaceContext workspace,
    VectorGenerationManager manager,
    SemanticEncoderPin encoder,
    string? storeRoot)
    : IVectorShadowRebuilder
{
    public static IVectorShadowRebuilder? TryOpen(
        WorkspaceContext workspace,
        SemanticEncoderPin? encoder = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        return VectorStore.ResolveExtensionPath() is null
            ? null
            : new SqliteVectorShadowRebuilder(
                workspace,
                VectorConvergeService.VectorGenerationManagerFor(workspace),
                encoder ?? SemanticEncoderSelection.Active,
                VectorConvergeService.FamilyStoreRootFor(workspace));
    }

    public IVectorConvergePort? OpenShadow(IVectorConvergePort live)
    {
        ArgumentNullException.ThrowIfNull(live);

        using FamilyStoreSidecarWriteLease? lease = storeRoot is null
            ? null
            : FamilyStoreSidecarWriteLease.AcquireFor(storeRoot);
        manager.PrepareShadow();
        SemanticGenerationIdentity target =
            MillerSemanticContract.PinnedIdentity(encoder, MillerVersion.Current);

        // Reusability is identity fields 1–3 only. writer_version differs on every Miller build, so full
        // record equality would skip the seed on exactly the upgrade rescans this reuse exists for; the
        // vectors-v1 matrix says reader_compatibility and fusion_profile never invalidate stored vectors.
        bool seed = live is SqliteVectorConvergePort
            && MillerSemanticContract.ClassifyChange(live.StoredIdentity, target)
                is InvalidationAction.None
                or InvalidationAction.QueryTimeOnly
                or InvalidationAction.ReaderGate;
        if (seed)
            ((SqliteVectorConvergePort)live).BackupTo(manager.ShadowPath);

        IVectorConvergePort? shadow = SqliteVectorConvergePort.TryOpenAt(
            workspace,
            manager.ShadowPath,
            encoder,
            sidecarLeaseHeld: storeRoot is not null);
        if (seed && shadow is not null)
        {
            shadow.SetMeta("writer_version", target.WriterVersion);
            shadow.SetMeta("min_reader_version", target.MinReaderVersion);
            shadow.SetMeta("fusion_profile", target.FusionProfile);

            // The copy inherits the live generation's sticky "ready" label; a shadow must read as building
            // until its own cursor completes, or RecoverInterruptedPromote could adopt a half-built seed.
            shadow.SetMeta("build_state", "building");
        }

        return shadow;
    }

    public void Promote(SemanticGenerationIdentity live, SemanticGenerationIdentity built)
    {
        using FamilyStoreSidecarWriteLease? lease = storeRoot is null
            ? null
            : FamilyStoreSidecarWriteLease.AcquireFor(storeRoot);
        manager.Promote(
            MillerSemanticContract.GenerationTag(built),
            MillerSemanticContract.GenerationTag(live));
    }
}

/// <summary>
/// The production GC arm: enumerate the retained generations beside the active artifact, plan the pass with the
/// pure never-delete rules of <see cref="VectorGenerationManager"/>, and delete each eligible generation on its
/// own — one log line per deletion, and a held-handle failure logged and retried on the next wake rather than
/// aborting the pass or crashing the drain.
/// </summary>
internal sealed class VectorGenerationGc(
    VectorGenerationManager manager,
    ILogger logger,
    string? storeRoot = null) : IVectorGenerationGc
{
    public static IVectorGenerationGc Create(WorkspaceContext workspace, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return new VectorGenerationGc(
            VectorConvergeService.VectorGenerationManagerFor(workspace),
            logger,
            VectorConvergeService.FamilyStoreRootFor(workspace));
    }

    public void Collect(bool activeIsReady, DateTimeOffset now, IReadOnlySet<string> tagsWithLiveReaders)
    {
        ArgumentNullException.ThrowIfNull(tagsWithLiveReaders);

        IReadOnlyList<RetainedGeneration> retained = manager.Retained();
        if (retained.Count == 0)
            return;

        using FamilyStoreSidecarWriteLease? lease = storeRoot is null
            ? null
            : FamilyStoreSidecarWriteLease.AcquireFor(storeRoot);

        VectorGcPlan plan = VectorGenerationManager.PlanGarbageCollection(new VectorGcInputs
        {
            Retained = retained,
            ActiveIsReady = activeIsReady,
            Now = now,
            TagsWithLiveReaders = tagsWithLiveReaders,
        });

        foreach (RetainedGeneration generation in plan.Deletions)
        {
            try
            {
                manager.DeleteRetained(generation);
                logger.LogInformation(
                    "Garbage-collected retained vector generation {Tag}: past the {SoakHours}h soak window with no " +
                    "live reader (retained {RetainedAt:O}).",
                    generation.Tag,
                    VectorGenerationManager.DefaultSoakWindow.TotalHours,
                    generation.RetainedAt);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex,
                    "Could not delete retained vector generation {Tag}; it will be retried on the next wake.",
                    generation.Tag);
            }
        }
    }
}

/// <summary>
/// The production port: one writer connection to <c>vectors.db</c> with the pinned sqlite-vec extension loaded,
/// plus read connections to <c>symbols.db</c> and <c>content.db</c>. It owns the SQL because the commit must be
/// ONE short transaction spanning the vec0 deletes, the vec0 inserts, the mapping updates and the cursor
/// advance — an invariant no composition of per-unit writes can provide.
/// </summary>
internal sealed class SqliteVectorConvergePort : IVectorConvergePort
{
    private static readonly string[] DocsLikeContentKinds =
        [TextContentKind.WorkspaceDocs, TextContentKind.WorkspaceConfig];

    private readonly VectorStore _vectors;
    private readonly string? _symbolsDbPath;
    private readonly string _contentDbPath;
    private readonly WorkspaceReadHandle? _storeSession;
    private readonly StoreSidecarStamp? _storeStamp;
    private readonly string? _storeVectorPath;
    private readonly string? _storeRoot;

    public string? StoreSidecarScope => _storeStamp?.ScopeToken;

    private SqliteVectorConvergePort(VectorStore vectors, string symbolsDbPath, string contentDbPath)
    {
        _vectors = vectors;
        _symbolsDbPath = symbolsDbPath;
        _contentDbPath = contentDbPath;
    }

    private SqliteVectorConvergePort(
        VectorStore vectors,
        WorkspaceReadHandle storeSession,
        string contentDbPath,
        string vectorsPath,
        StoreSidecarStamp storeStamp,
        string storeRoot)
    {
        _vectors = vectors;
        _storeSession = storeSession;
        _contentDbPath = contentDbPath;
        _storeVectorPath = vectorsPath;
        _storeStamp = storeStamp;
        _storeRoot = storeRoot;
    }

    public SemanticGenerationIdentity StoredIdentity => _vectors.Identity;

    internal void BackupTo(string path) => _vectors.BackupTo(path);

    /// <summary>
    /// Opens (creating on first run) the active generation. Returns null — never throws — when the pinned
    /// extension is unavailable or the extract artifact has no revision yet: both are ordinary states in which
    /// the drain simply does nothing.
    /// </summary>
    public static IVectorConvergePort? TryOpen(
        WorkspaceContext workspace,
        SemanticEncoderPin? encoder = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        if (WorkspaceReadSessionFactory.StoreEnabledFromEnvironment())
            return TryOpenStore(workspace, encoder);

        // Recover BEFORE the open. TryOpenAt CREATES an empty artifact when the active path is missing, which is
        // exactly the state an interrupted promote leaves behind — and once an active file exists,
        // PrepareShadow stops recognising the leftover shadow as a promote to finish and deletes it. Creating
        // the empty file first would therefore turn a one-rename recovery into a full re-embed.
        string root = workspace.CanonicalRoot ?? workspace.WorkspaceRoot;
        string activePath = VectorSidecar.PathFor(root);
        if (!RecoverInterruptedPromoteBeforeCreate(new VectorGenerationManager(root)))
            return null;

        return TryOpenAt(workspace, activePath, encoder);
    }

    private static bool RecoverInterruptedPromoteBeforeCreate(VectorGenerationManager generations)
    {
        try
        {
            generations.RecoverInterruptedPromote();
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or SqliteException or InvalidOperationException)
        {
            return File.Exists(generations.ActivePath);
        }
    }

    internal static IVectorConvergePort? TryOpenStore(
        WorkspaceContext workspace,
        SemanticEncoderPin? encoder = null)
    {
        return TryOpenStoreAt(
            workspace,
            vectorsPath: null,
            encoder,
            sidecarLeaseHeld: false,
            recoverInterruptedPromote: true);
    }

    private static IVectorConvergePort? TryOpenStoreAt(
        WorkspaceContext workspace,
        string? vectorsPath,
        SemanticEncoderPin? encoder,
        bool sidecarLeaseHeld,
        bool recoverInterruptedPromote)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (VectorStore.ResolveExtensionPath() is not { } extension)
            return null;

        string workspaceRoot = workspace.CanonicalRoot ?? workspace.WorkspaceRoot;
        var session = WorkspaceReadSessionFactory.Open(
            workspace.CanonicalExtractDbPath ?? Path.Combine(workspaceRoot, ".miller", "symbols.db"),
            workspaceRoot,
            workspace.WorkspaceId,
            storeEnabled: true);
        try
        {
            string storeRoot = session.FamilyStoreRoot
                ?? throw new InvalidOperationException("The family-store read session has no store root.");
            StoreSidecarStamp contentStamp = StoreSidecarStamp.FromSnapshot(
                StoreSidecarKind.Content,
                session.Snapshot);
            string contentPath = StoreSidecarCatalog.PathFor(
                storeRoot,
                StoreSidecarKind.Content,
                session.Snapshot.ViewId);
            if (!StoreSidecarCatalog.IsCurrent(contentPath, contentStamp))
            {
                session.Dispose();
                return null;
            }

            vectorsPath ??= VectorSidecar.PathForStore(storeRoot, session.Snapshot.ViewId);
            using FamilyStoreSidecarWriteLease? lease = sidecarLeaseHeld
                ? null
                : FamilyStoreSidecarWriteLease.AcquireFor(storeRoot);
            if (recoverInterruptedPromote &&
                !RecoverInterruptedPromoteBeforeCreate(VectorGenerationManager.ForActivePath(vectorsPath)))
            {
                session.Dispose();
                return null;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(vectorsPath)!);
            if (!File.Exists(vectorsPath))
            {
                using VectorStore created = VectorStore.Create(
                    vectorsPath,
                    MillerSemanticContract.PinnedIdentity(
                        encoder ?? SemanticEncoderSelection.Active,
                        MillerVersion.Current),
                    StoreArtifactId(session.Snapshot),
                    extension);
            }

            VectorStore store = VectorStore.Open(vectorsPath, extension);
            try
            {
                return new SqliteVectorConvergePort(
                    store,
                    session,
                    contentPath,
                    vectorsPath,
                    StoreSidecarStamp.FromSnapshot(StoreSidecarKind.Vector, session.Snapshot),
                    storeRoot);
            }
            catch
            {
                store.Dispose();
                throw;
            }
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>Opens (creating on first run) a generation at an explicit path — the active artifact, or the
    /// shadow a rebuild is being built into.</summary>
    public static IVectorConvergePort? TryOpenAt(
        WorkspaceContext workspace,
        string vectorsPath,
        SemanticEncoderPin? encoder = null,
        bool sidecarLeaseHeld = false)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(vectorsPath);

        if (WorkspaceReadSessionFactory.StoreEnabledFromEnvironment())
        {
            return TryOpenStoreAt(
                workspace,
                vectorsPath,
                encoder,
                sidecarLeaseHeld,
                recoverInterruptedPromote: false);
        }

        if (VectorStore.ResolveExtensionPath() is not { } extension)
            return null;
        if (workspace.CanonicalExtractDbPath is not { } symbolsDbPath || !File.Exists(symbolsDbPath))
            return null;

        if (!File.Exists(vectorsPath))
        {
            string? artifactId = TryReadArtifactId(symbolsDbPath);
            if (artifactId is null)
                return null;

            using VectorStore created = VectorStore.Create(
                vectorsPath,
                MillerSemanticContract.PinnedIdentity(
                    encoder ?? SemanticEncoderSelection.Active,
                    MillerVersion.Current),
                artifactId,
                extension);
        }

        VectorStore store = VectorStore.Open(vectorsPath, extension);
        try
        {
            return new SqliteVectorConvergePort(
                store, symbolsDbPath, ContentCorpusSidecar.ContentDbPathFor(symbolsDbPath));
        }
        catch
        {
            store.Dispose();
            throw;
        }
    }

    public string? Meta(string key) => _vectors.Meta(key);

    public void SetMeta(string key, string value) => _vectors.SetMeta(key, value);

    public VectorConvergeSnapshot Snapshot(long completedRevision)
    {
        if (_storeSession is not null)
        {
            WorkspaceReadSnapshot snapshot = _storeSession.Snapshot;
            long storeLatest = snapshot.Freshness.StoreLogSequence
                ?? throw new InvalidOperationException("The family-store snapshot has no store_log sequence.");
            if (completedRevision <= 0)
            {
                return new VectorConvergeSnapshot(
                    StoreArtifactId(snapshot),
                    storeLatest,
                    DeltaHistoryComplete: true,
                    ChangedPaths: [],
                    FullPass: true);
            }

            RevisionDeltaResult delta = RevisionDeltaReader.Read(
                _storeSession,
                completedRevision,
                snapshot.ArtifactOrStoreId);
            if (delta.Status == RevisionDeltaStatus.Complete)
            {
                string[] changedPaths = delta.ChangedPaths
                    .Concat(delta.DeletedPaths ?? [])
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                return new VectorConvergeSnapshot(
                    StoreArtifactId(snapshot),
                    storeLatest,
                    DeltaHistoryComplete: true,
                    changedPaths,
                    FullPass: false);
            }

            return new VectorConvergeSnapshot(
                StoreArtifactId(snapshot),
                storeLatest,
                DeltaHistoryComplete: false,
                ChangedPaths: [],
                FullPass: completedRevision != storeLatest);
        }

        using var freshness = new FreshnessReader(_symbolsDbPath!);
        long latest = freshness.LatestRevision();
        string artifactId = freshness.ArtifactId() ?? string.Empty;

        if (completedRevision <= 0)
            return new VectorConvergeSnapshot(artifactId, latest, DeltaHistoryComplete: true, [], FullPass: true);

        IReadOnlyList<string> changed = [.. freshness
            .ChangedSince(completedRevision)
            .Where(change => change.RevisionId <= latest)
            .Select(static change => change.Path)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)];

        return new VectorConvergeSnapshot(
            artifactId, latest, DeltaHistoryExplains(completedRevision, latest), changed);
    }

    public IReadOnlyList<VectorCorpusUnit> Units(VectorUnitKind kind, IReadOnlyCollection<string>? paths) =>
        kind is VectorUnitKind.Symbol ? SymbolUnits(paths) : ChunkUnits(paths);

    public IReadOnlyList<VectorUnitState> Stored(VectorUnitKind kind, IReadOnlyCollection<string>? paths) =>
    [
        .. _vectors.MappedUnits(kind, paths)
            .Select(static entry => new VectorUnitState(entry.UnitId, entry.Path, entry.EmbedTextHash)),
    ];

    public int TotalStored(VectorUnitKind kind) => _vectors.MappedCount(kind);

    public ChunkCursorFacts ChunkFacts(long targetRevision)
    {
        string symbolsArtifactId = _storeSession is null
            ? TryReadArtifactId(_symbolsDbPath!) ?? string.Empty
            : StoreArtifactId(_storeSession.Snapshot);
        int schemaVersion = 0;
        long contentRevision = 0;
        string chunker = string.Empty;
        var sources = new List<ChunkSourceHash>();

        if (File.Exists(_contentDbPath))
        {
            using SqliteConnection content = OpenReadOnly(_contentDbPath);
            using (SqliteCommand meta = content.CreateCommand())
            {
                meta.CommandText =
                    "SELECT schema_version, COALESCE(workspace_revision, 0), chunker_version FROM content_meta LIMIT 1";
                using SqliteDataReader reader = meta.ExecuteReader();
                if (reader.Read())
                {
                    schemaVersion = reader.GetInt32(0);
                    contentRevision = reader.GetInt64(1);
                    chunker = reader.GetString(2);
                }
            }

            using SqliteCommand rows = content.CreateCommand();
            rows.CommandText =
                "SELECT path, content_hash FROM content_sources " +
                "WHERE status = 'active' AND path IS NOT NULL AND path != '' " +
                $"AND content_kind IN ({DocsKindPlaceholders(rows)})";
            using SqliteDataReader sourceReader = rows.ExecuteReader();
            while (sourceReader.Read())
                sources.Add(new ChunkSourceHash(sourceReader.GetString(0), sourceReader.GetString(1), null));
        }

        IReadOnlyDictionary<string, string> fileHashes = ReadFileHashes([.. sources.Select(static s => s.Path)]);

        return new ChunkCursorFacts
        {
            SymbolsArtifactId = symbolsArtifactId,
            VectorsArtifactId = Meta("artifact_id") ?? string.Empty,
            ChunkSourceArtifactId = Meta(VectorConvergeService.ChunkSourceArtifactKey) ?? string.Empty,
            ContentSchemaVersion = schemaVersion,
            RecordedChunkSchemaVersion = int.TryParse(
                Meta(VectorConvergeService.ChunkSchemaVersionKey),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int recorded)
                ? recorded
                : 0,
            ContentChunkerVersion = chunker,
            CorpusGeneration = StoredIdentity.CorpusGeneration,
            ContentWorkspaceRevision = contentRevision,
            TargetRevision = targetRevision,
            Sources = [.. sources.Select(source => source with
            {
                SymbolsContentHash = fileHashes.GetValueOrDefault(source.Path),
            })],
        };
    }

    public bool StillValid(SemanticGenerationIdentity identity, string artifactId)
    {
        IReadOnlyDictionary<string, string> meta = _vectors.AllMeta();
        return _vectors.ReadIdentity() == identity
            && string.Equals(meta.GetValueOrDefault("artifact_id"), artifactId, StringComparison.Ordinal);
    }

    public void Commit(
        VectorUnitKind kind,
        IReadOnlyList<VectorCommit> vectors,
        IReadOnlyList<string> delete,
        string completedRevisionKey,
        long advanceTo,
        long revision)
    {
        var metaUpdates = new Dictionary<string, string>(StringComparer.Ordinal);
        if (advanceTo > 0)
            metaUpdates[completedRevisionKey] = advanceTo.ToString(CultureInfo.InvariantCulture);

        foreach ((string key, string value) in BuildStateUpdates(kind, advanceTo))
            metaUpdates[key] = value;

        using (AcquireStoreSidecarLease())
        {
            _vectors.CommitBatch(
                kind,
                [
                    .. vectors.Select(static commit => new VectorBatchEntry(
                        commit.Unit.UnitId,
                        commit.Unit.Path,
                        commit.Unit.SymbolKind,
                        commit.Unit.IsTest,
                        commit.Embedding,
                        commit.Unit.EmbedTextHash)),
                ],
                delete,
                metaUpdates,
                revision);
        }
    }

    public void PublishCompleteness()
    {
        if (_storeStamp is null || _storeVectorPath is null)
            return;

        long expected = _storeStamp.StoreLogSequence;
        if (MetaRevision(VectorConvergeService.SymbolCompletedKey) != expected ||
            MetaRevision(VectorConvergeService.ChunkCompletedKey) != expected)
        {
            return;
        }

        using (AcquireStoreSidecarLease())
            StoreSidecarCatalog.Stamp(_storeVectorPath, _storeStamp);
    }

    private FamilyStoreSidecarWriteLease? AcquireStoreSidecarLease() =>
        _storeRoot is null ? null : FamilyStoreSidecarWriteLease.AcquireFor(_storeRoot);

    private long MetaRevision(string key) =>
        long.TryParse(Meta(key), NumberStyles.None, CultureInfo.InvariantCulture, out long revision)
            ? revision
            : 0;

    // A converged generation becomes queryable here: build_state is the reader's gate, and the commit that
    // catches the symbol cursor up with its target is the moment the artifact starts serving.
    private IReadOnlyDictionary<string, string> BuildStateUpdates(VectorUnitKind kind, long advanceTo)
    {
        long completed = kind is VectorUnitKind.Symbol && advanceTo > 0
            ? advanceTo
            : Number(VectorConvergeService.SymbolCompletedKey);

        VectorBuildStateUpdate update = VectorGenerationManager.EvaluateBuildState(new VectorBuildProgress(
            completed,
            Number(VectorConvergeService.SymbolTargetKey),
            Meta("build_state")));

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["build_state"] = update.BuildState,
            ["build_progress_percent"] = update.ProgressPercent.ToString(CultureInfo.InvariantCulture),
        };
    }

    private long Number(string key) =>
        long.TryParse(Meta(key), NumberStyles.None, CultureInfo.InvariantCulture, out long value) ? value : 0;

    public void Dispose()
    {
        try
        {
            _vectors.Dispose();
        }
        finally
        {
            _storeSession?.Dispose();
        }
    }

    private static string StoreArtifactId(WorkspaceReadSnapshot snapshot) =>
        snapshot.VectorArtifactId;

    private IReadOnlyList<VectorCorpusUnit> SymbolUnits(IReadOnlyCollection<string>? paths)
    {
        if (_storeSession is not null)
            return _storeSession.Read(connection => ReadSymbolUnits(connection, paths));

        using SqliteConnection symbols = OpenReadOnly(_symbolsDbPath!);
        return ReadSymbolUnits(symbols, paths);
    }

    private static IReadOnlyList<VectorCorpusUnit> ReadSymbolUnits(
        SqliteConnection symbols,
        IReadOnlyCollection<string>? paths)
    {
        var units = new List<VectorCorpusUnit>();

        foreach (IReadOnlyList<string>? batch in Batched(paths))
        {
            using SqliteCommand command = symbols.CreateCommand();
            string select =
                "SELECT s.symbol_id, s.name, s.kind, s.path, s.is_test, s.signature, s.doc_comment, p.name AS container " +
                "FROM symbols s LEFT JOIN symbols p ON p.symbol_id = s.parent_symbol_id";
            command.CommandText = batch is null
                ? select
                : $"{select} WHERE s.path IN ({Placeholders(batch, command)})";

            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                string kind = reader.GetString(2);
                if (!SymbolCardBuilder.IsEligible(kind))
                    continue;

                var input = new SymbolCardInput(
                    SymbolId: reader.GetString(0),
                    Name: reader.GetString(1),
                    Kind: kind,
                    Path: reader.GetString(3),
                    IsTest: reader.GetInt64(4) != 0,
                    Signature: reader.IsDBNull(5) ? null : reader.GetString(5),
                    DocComment: reader.IsDBNull(6) ? null : reader.GetString(6),
                    Container: reader.IsDBNull(7) ? null : reader.GetString(7));

                units.Add(new VectorCorpusUnit(
                    input.SymbolId, input.Path, SymbolCardBuilder.Build(input), input.Kind, input.IsTest));
            }
        }

        return units;
    }

    private IReadOnlyList<VectorCorpusUnit> ChunkUnits(IReadOnlyCollection<string>? paths)
    {
        if (!File.Exists(_contentDbPath))
            return [];

        var units = new List<VectorCorpusUnit>();
        using SqliteConnection content = OpenReadOnly(_contentDbPath);

        foreach (IReadOnlyList<string>? batch in Batched(paths))
        {
            using SqliteCommand command = content.CreateCommand();
            string select =
                "SELECT c.chunk_id, c.path, c.raw_text, c.content_kind, c.is_test " +
                "FROM content_chunks c JOIN content_sources s ON s.source_id = c.source_id " +
                $"WHERE s.status = 'active' AND c.path IS NOT NULL AND c.content_kind IN ({DocsKindPlaceholders(command)})";
            command.CommandText = batch is null
                ? select
                : $"{select} AND c.path IN ({Placeholders(batch, command)})";

            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                string text = SymbolCardBuilder.ChunkText(reader.GetString(2));
                if (text.Length == 0)
                    continue;

                units.Add(new VectorCorpusUnit(
                    reader.GetString(0), reader.GetString(1), text, reader.GetString(3), reader.GetInt64(4) != 0));
            }
        }

        return units;
    }

    private IReadOnlyDictionary<string, string> ReadFileHashes(IReadOnlyList<string> paths)
    {
        if (_storeSession is not null)
            return _storeSession.Read(connection => ReadFileHashes(connection, paths));

        using SqliteConnection symbols = OpenReadOnly(_symbolsDbPath!);
        return ReadFileHashes(symbols, paths);
    }

    private static IReadOnlyDictionary<string, string> ReadFileHashes(
        SqliteConnection symbols,
        IReadOnlyList<string> paths)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (paths.Count == 0)
            return hashes;

        foreach (IReadOnlyList<string>? batch in Batched(paths))
        {
            using SqliteCommand command = symbols.CreateCommand();
            command.CommandText =
                $"SELECT path, content_hash FROM files WHERE path IN ({Placeholders(batch!, command)})";

            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!reader.IsDBNull(1))
                    hashes[reader.GetString(0)] = reader.GetString(1);
            }
        }

        return hashes;
    }

    // The span is explainable when the delta log reaches back to (or before) the revision after the cursor.
    private bool DeltaHistoryExplains(long completedRevision, long latestRevision)
    {
        if (latestRevision <= completedRevision)
            return true;

        using SqliteConnection symbols = OpenReadOnly(_symbolsDbPath!);
        using SqliteCommand command = symbols.CreateCommand();
        command.CommandText = "SELECT MIN(revision_id) FROM revision_file_changes";
        object? minimum = command.ExecuteScalar();
        return minimum is null or DBNull
            || Convert.ToInt64(minimum, CultureInfo.InvariantCulture) <= completedRevision + 1;
    }

    // A null batch means "no path filter"; otherwise SQLite's parameter ceiling is respected by chunking.
    private static IEnumerable<IReadOnlyList<string>?> Batched(IReadOnlyCollection<string>? paths)
    {
        if (paths is null)
        {
            yield return null;
            yield break;
        }

        const int batchSize = 400;
        string[] ordered = [.. paths];
        for (int offset = 0; offset < ordered.Length; offset += batchSize)
            yield return ordered[offset..Math.Min(offset + batchSize, ordered.Length)];
    }

    private static string Placeholders(IReadOnlyList<string> values, SqliteCommand command)
    {
        var names = new List<string>(values.Count);
        for (int i = 0; i < values.Count; i++)
        {
            string name = $"$p{i.ToString(CultureInfo.InvariantCulture)}";
            command.Parameters.AddWithValue(name, values[i]);
            names.Add(name);
        }

        return names.Count == 0 ? "NULL" : string.Join(", ", names);
    }

    private static string DocsKindPlaceholders(SqliteCommand command)
    {
        var names = new List<string>(DocsLikeContentKinds.Length);
        for (int i = 0; i < DocsLikeContentKinds.Length; i++)
        {
            string name = $"$k{i.ToString(CultureInfo.InvariantCulture)}";
            command.Parameters.AddWithValue(name, DocsLikeContentKinds[i]);
            names.Add(name);
        }

        return string.Join(", ", names);
    }

    private static string? TryReadArtifactId(string symbolsDbPath)
    {
        try
        {
            using var freshness = new FreshnessReader(symbolsDbPath);
            return freshness.ArtifactId();
        }
        catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    private static SqliteConnection OpenReadOnly(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

}
