using Miller.Core.Graph;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Indexing.Resolution;
using Miller.Indexing.Store;
using Miller.Server.Resolution;
using Miller.Server.Telemetry;
using Miller.Server.Tools;

namespace Miller.Server.Workspaces;

public sealed class WorkspaceIndexProvider
    : IWorkspaceIndexProvider, IWorkspaceSearchProvider, IWorkspaceSymbolReadProvider, IWorkspaceContentSearchProvider,
      IWorkspaceRegionSearchProvider, IWorkspaceTextContentSearchProvider, IWorkspaceArtifactProvider
{
    private readonly IndexHolder _holder;
    private readonly WorkspaceContext _currentWorkspace;
    private readonly WorkspaceRegistry _registry;
    private readonly Func<string, WorkspaceRefreshResult> _refresh;
    private readonly Func<string, MillerRepositoryIndex> _loadIndex;
    private readonly Func<string, SymbolSearchProjection> _loadSymbolSearch;
    private readonly Func<string, string, ContentSearchProjection> _loadContentSearch;
    private readonly Func<string, long, ITextContentSearchIndex> _loadTextContentSearch;
    private readonly Func<string, long, IRegionSearchIndex> _loadRegionSearch;
    private readonly Func<long, bool?> _currentIndexFresh;
    private readonly Func<string, string, string?, WorkspaceReadHandle> _openReadSession;
    private readonly Func<IWorkspaceReadSession, SymbolSearchProjection> _loadSessionSymbolSearch;
    private readonly Func<WorkspaceReadHandle, ISymbolLookupIndex> _openStoreSymbolSearch;
    private readonly Func<WorkspaceReadHandle, ITextContentSearchIndex> _openStoreTextContentSearch;
    private readonly Func<IWorkspaceReadSession, BridgeGraph> _loadSessionBridgeGraph;
    private readonly Func<WorkspaceReadHandle, IReadOnlyList<GraphEdge>>? _loadSupplementalEdges;
    private readonly Action<GraphStatementObservation> _graphStatementObserver;
    private readonly SymbolSearchSidecar _sidecar;
    private readonly object _cacheGate = new();
    private readonly Dictionary<CacheKey, Lazy<CachedIndex>> _cache = new();
    private readonly Dictionary<CacheKey, Lazy<CachedSymbolSearch>> _symbolSearchCache = new();
    private readonly Dictionary<CacheKey, Lazy<CachedSymbolRead>> _symbolReadCache = new();
    private readonly Dictionary<CacheKey, Lazy<CachedContentSearch>> _contentSearchCache = new();
    private readonly Dictionary<CacheKey, Lazy<CachedTextContentSearch>> _textContentSearchCache = new();
    private readonly Dictionary<CacheKey, Lazy<CachedRegionSearch>> _regionSearchCache = new();
    private readonly SupplementalEdgeCache _supplementalEdgesCache;
    private readonly RevisionFactCacheStore _factCacheStore;
    private readonly Action<Action> _scheduleBackgroundRefresh;
    private readonly Func<WorkspaceRegistryRow, bool> _hasReadableIndex;

    // One background refresh per workspace at a time. See StartBackgroundRefresh.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _backgroundRefreshes =
        new(StringComparer.Ordinal);

    public WorkspaceIndexProvider(
        IndexHolder holder,
        WorkspaceContext currentWorkspace,
        WorkspaceRegistry registry,
        CrossWorkspaceRefreshService refreshService,
        SymbolSearchSidecar sidecar,
        SupplementalEdgeCache supplementalEdgesCache,
        RevisionFactCacheStore factCacheStore)
        : this(
            holder,
            currentWorkspace,
            registry,
            AutomaticRefresh(refreshService),
            dbPath => RepositoryIndexLoader.Load(dbPath),
            dbPath => SymbolSearchProjectionLoader.Load(dbPath),
            (dbPath, root) => ContentSearchProjectionLoader.Load(dbPath, root),
            (dbPath, revision) => ContentCorpusSidecar.OpenGenerationChecked(
                ContentCorpusSidecar.ContentDbPathFor(dbPath), dbPath, revision),
            (dbPath, revision) => FtsRegionSearchIndex.Open(
                SymbolSearchSidecar.SearchDbPathFor(dbPath),
                revision,
                SymbolsArtifactIdentity.TryRead(dbPath)),
            currentIndexFresh: _ => null,
            sidecar,
            openReadSession: (databasePath, root, workspaceId) =>
                WorkspaceReadSessionFactory.Open(databasePath, root, workspaceId, storeEnabled: null, factCacheStore),
            supplementalEdgesCache: supplementalEdgesCache,
            factCacheStore: factCacheStore)
    {
    }

    internal WorkspaceIndexProvider(
        IndexHolder holder,
        WorkspaceContext currentWorkspace,
        WorkspaceRegistry registry,
        Func<string, WorkspaceRefreshResult> refresh,
        Func<string, MillerRepositoryIndex> loadIndex,
        Func<string, SymbolSearchProjection> loadSymbolSearch,
        Func<string, string, ContentSearchProjection> loadContentSearch,
        Func<string, long, ITextContentSearchIndex> loadTextContentSearch,
        Func<string, long, IRegionSearchIndex> loadRegionSearch,
        Func<long, bool?> currentIndexFresh,
        SymbolSearchSidecar sidecar,
        Func<string, string, string?, WorkspaceReadHandle>? openReadSession = null,
        Func<IWorkspaceReadSession, MillerRepositoryIndex>? loadSessionIndex = null,
        Func<IWorkspaceReadSession, SymbolSearchProjection>? loadSessionSymbolSearch = null,
        Func<WorkspaceReadHandle, ISymbolLookupIndex>? openStoreSymbolSearch = null,
        Func<IWorkspaceReadSession, BridgeGraph>? loadSessionBridgeGraph = null,
        Action<GraphStatementObservation>? graphStatementObserver = null,
        Func<WorkspaceReadHandle, ITextContentSearchIndex>? openStoreTextContentSearch = null,
        Func<WorkspaceReadHandle, IReadOnlyList<GraphEdge>>? loadSupplementalEdges = null,
        SupplementalEdgeCache? supplementalEdgesCache = null,
        RevisionFactCacheStore? factCacheStore = null,
        Action<Action>? scheduleBackgroundRefresh = null,
        Func<WorkspaceRegistryRow, bool>? hasReadableIndex = null)
    {
        ArgumentNullException.ThrowIfNull(holder);
        ArgumentNullException.ThrowIfNull(currentWorkspace);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(refresh);
        ArgumentNullException.ThrowIfNull(loadIndex);
        ArgumentNullException.ThrowIfNull(loadSymbolSearch);
        ArgumentNullException.ThrowIfNull(loadContentSearch);
        ArgumentNullException.ThrowIfNull(loadTextContentSearch);
        ArgumentNullException.ThrowIfNull(loadRegionSearch);
        ArgumentNullException.ThrowIfNull(currentIndexFresh);
        ArgumentNullException.ThrowIfNull(sidecar);
        _holder = holder;
        _currentWorkspace = currentWorkspace;
        _registry = registry;
        _refresh = refresh;
        _loadIndex = loadIndex;
        _loadSymbolSearch = loadSymbolSearch;
        _loadContentSearch = loadContentSearch;
        _loadTextContentSearch = loadTextContentSearch;
        _loadRegionSearch = loadRegionSearch;
        _currentIndexFresh = currentIndexFresh;
        _sidecar = sidecar;
        _openReadSession = openReadSession ?? ((databasePath, root, workspaceId) =>
            new WorkspaceReadHandle(LegacyArtifactReadSession.Open(databasePath, root, workspaceId)));
        _ = loadSessionIndex;
        _loadSessionSymbolSearch = loadSessionSymbolSearch ?? SymbolSearchProjectionLoader.LoadSession;
        _openStoreSymbolSearch = openStoreSymbolSearch ?? (readSession =>
        {
            string storeRoot = StoreSearchRoot(readSession)
                ?? throw new InvalidOperationException("The family-store read session has no store root.");
            return _sidecar.OpenStoreRequired(storeRoot, readSession.Snapshot);
        });
        _openStoreTextContentSearch = openStoreTextContentSearch ?? (readSession =>
            ContentCorpusSidecar.OpenStoreGenerationChecked(
                readSession.FamilyStoreRoot
                    ?? throw new InvalidOperationException("The family-store read session has no store root."),
                readSession.Snapshot));
        _loadSessionBridgeGraph = loadSessionBridgeGraph ?? (session => SessionBridgeGraphLoader.Load(session));
        _loadSupplementalEdges = loadSupplementalEdges;
        _supplementalEdgesCache = supplementalEdgesCache ?? new SupplementalEdgeCache();
        _factCacheStore = factCacheStore ?? new RevisionFactCacheStore();
        _graphStatementObserver = graphStatementObserver ?? ObserveGraphStatement;
        _scheduleBackgroundRefresh = scheduleBackgroundRefresh
            ?? (work => ThreadPool.QueueUserWorkItem(static state => ((Action)state!)(), work));
        _hasReadableIndex = hasReadableIndex ?? HasReadableIndex;
    }

    public WorkspaceReadContext Resolve(string? workspaceId, WorkspaceRefreshMode refresh)
    {
        if (workspaceId is null)
            return ResolveCurrent();

        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        if (SelectorTargetsCurrent(workspaceId))
            return ResolveCurrent();

        return ResolveRegistered(workspaceId, refresh);
    }

    public WorkspaceArtifactContext ResolveArtifact(string? workspaceId, WorkspaceRefreshMode refresh)
    {
        if (workspaceId is null)
            return ResolveCurrentArtifact();

        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        if (SelectorTargetsCurrent(workspaceId))
            return ResolveCurrentArtifact();

        return ResolveRegisteredArtifact(workspaceId, refresh);
    }

    public WorkspaceSymbolSearchContext ResolveSymbolSearch(string? workspaceId, WorkspaceRefreshMode refresh)
    {
        if (workspaceId is null)
            return ResolveCurrentSymbolSearch();

        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        if (SelectorTargetsCurrent(workspaceId))
            return ResolveCurrentSymbolSearch();

        return ResolveRegisteredSymbolSearch(workspaceId, refresh);
    }

    public WorkspaceSymbolReadContext ResolveSymbolRead(string? workspaceId, WorkspaceRefreshMode refresh)
    {
        if (workspaceId is null)
            return ResolveCurrentSymbolRead();

        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        if (SelectorTargetsCurrent(workspaceId))
            return ResolveCurrentSymbolRead();

        return ResolveRegisteredSymbolRead(workspaceId, refresh);
    }

    public WorkspaceContentSearchContext ResolveContentSearch(string? workspaceId, WorkspaceRefreshMode refresh)
    {
        if (workspaceId is null)
            return ResolveCurrentContentSearch();

        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        if (SelectorTargetsCurrent(workspaceId))
            return ResolveCurrentContentSearch();

        return ResolveRegisteredContentSearch(workspaceId, refresh);
    }

    public WorkspaceRegionSearchContext ResolveRegionSearch(string? workspaceId, WorkspaceRefreshMode refresh)
    {
        if (workspaceId is null)
            return ResolveCurrentRegionSearch();

        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        if (SelectorTargetsCurrent(workspaceId))
            return ResolveCurrentRegionSearch();

        return ResolveRegisteredRegionSearch(workspaceId, refresh);
    }

    public WorkspaceTextContentSearchContext ResolveTextContentSearch(string? workspaceId, WorkspaceRefreshMode refresh)
    {
        long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        WorkspaceTextContentSearchContext context;
        if (workspaceId is null)
            context = ResolveCurrentTextContentSearch();
        else
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
            context = SelectorTargetsCurrent(workspaceId)
                ? ResolveCurrentTextContentSearch()
                : ResolveRegisteredTextContentSearch(workspaceId, refresh);
        }

        ObserveTextContentIndexResolve(TextContentIndexResolveFamily.Resolve, startedAt);
        return context;
    }

    /// <summary>
    /// The level comes from <c>dbPath</c> rather than from the snapshot index because every guarded consumer of a
    /// carried level reads its reference EVIDENCE from that same path — the level and the evidence it describes
    /// must come from one artifact. A repair scan (<c>RootRebind</c>, <c>SchemaHeal</c>, <c>CorruptionHeal</c>)
    /// promotes a symbols-level artifact over a workspace whose holder still serves the pre-repair full index, so
    /// a level taken from that snapshot would claim a complete reference layer over the empty one the consumer
    /// actually reads.
    /// </summary>
    private WorkspaceReadContext ResolveCurrent()
    {
        long resolveStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        WorkspaceReadHandle readSession = OpenCurrentReadSession();
        try
        {
            bool familyStore = readSession.Snapshot.Mode == WorkspaceReadMode.FamilyStore;
            IndexHolderMetadata? holderMetadata = familyStore ? _holder.MetadataSnapshot() : null;
            (MillerRepositoryIndex Index, long Revision)? legacySnapshot = familyStore ? null : _holder.Snapshot();
            long holderRevision = holderMetadata?.Revision ?? legacySnapshot!.Value.Revision;
            long revision = ContextRevision(readSession.Snapshot, holderRevision);
            MillerRepositoryIndex? holderIndex = legacySnapshot?.Index;
            ISymbolLookupIndex index = familyStore
                ? ResolveFamilyStoreLookup(_currentWorkspace.WorkspaceId, readSession)
                : holderIndex!;
            ISymbolGraphReachability innerGraph = familyStore
                ? ResolveFamilyStoreGraph(_currentWorkspace.WorkspaceId, readSession)
                : holderIndex!.Graph;
            var measuredGraph = familyStore
                ? new MeasuredSymbolGraphReachability(innerGraph, _graphStatementObserver)
                : null;
            ISymbolGraphReachability graph = measuredGraph ?? innerGraph;
            var bridgeGraph = new Lazy<BridgeGraph>(
                familyStore
                    ? () => _loadSessionBridgeGraph(readSession)
                    : () => holderIndex!.BridgeGraph,
                LazyThreadSafetyMode.ExecutionAndPublication);
            var resolver = new SmartTargetResolver(index);
            ReadPhaseTelemetry? readTelemetry = familyStore
                ? ReadTelemetry((MeasuredSymbolLookupIndex)index, measuredGraph)
                : null;
            var context = new WorkspaceReadContext(
                index,
                graph,
                bridgeGraph,
                resolver,
                readSession,
                _currentWorkspace.WorkspaceId,
                _currentWorkspace.CanonicalRoot ?? _currentWorkspace.WorkspaceRoot,
                revision,
                familyStore ? null : _currentIndexFresh(holderRevision),
                "current",
                WarningText: null,
                DisplayId: CurrentDisplayId(),
                IndexLevel: readSession.Snapshot.IndexLevel)
            {
                ReadTelemetry = readTelemetry,
            };
            readTelemetry?.CompleteResolve(resolveStartedAt);
            return context;
        }
        catch
        {
            readSession.Dispose();
            throw;
        }
    }

    private WorkspaceArtifactContext ResolveCurrentArtifact()
    {
        WorkspaceReadHandle readSession = OpenCurrentReadSession();
        try
        {
            long holderRevision = readSession.Snapshot.Mode == WorkspaceReadMode.FamilyStore
                ? _holder.MetadataSnapshot().Revision
                : _holder.Snapshot().Revision;
            long revision = ContextRevision(readSession.Snapshot, holderRevision);
            return new WorkspaceArtifactContext(
                readSession,
                _currentWorkspace.WorkspaceId,
                _currentWorkspace.CanonicalRoot ?? _currentWorkspace.WorkspaceRoot,
                revision,
                readSession.Snapshot.Mode == WorkspaceReadMode.FamilyStore ? null : _currentIndexFresh(holderRevision),
                "current",
                WarningText: null,
                DisplayId: CurrentDisplayId(),
                IndexLevel: readSession.Snapshot.IndexLevel);
        }
        catch
        {
            readSession.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Path-derived level, for the reason on <see cref="ResolveCurrent"/> — this context's consumer also reads
    /// from <c>IndexDbPath</c>, and holding every resolver to one rule is what keeps a level and the evidence it
    /// describes on the same artifact.
    /// </summary>
    private WorkspaceSymbolSearchContext ResolveCurrentSymbolSearch()
    {
        WorkspaceReadHandle readSession = OpenCurrentReadSession();
        try
        {
            bool familyStore = readSession.Snapshot.Mode == WorkspaceReadMode.FamilyStore;
            IndexHolderMetadata? holderMetadata = familyStore ? _holder.MetadataSnapshot() : null;
            (MillerRepositoryIndex Index, long Revision)? legacySnapshot = familyStore ? null : _holder.Snapshot();
            long holderRevision = holderMetadata?.Revision ?? legacySnapshot!.Value.Revision;
            long revision = ContextRevision(readSession.Snapshot, holderRevision);
            ISymbolLookupIndex searchIndex = ResolveCurrentSymbolSearchIndex(
                legacySnapshot?.Index,
                holderRevision,
                readSession);
            return new WorkspaceSymbolSearchContext(
                searchIndex,
                readSession,
                _currentWorkspace.WorkspaceId,
                _currentWorkspace.CanonicalRoot ?? _currentWorkspace.WorkspaceRoot,
                revision,
                familyStore ? null : _currentIndexFresh(holderRevision),
                "current",
                WarningText: null,
                DisplayId: CurrentDisplayId(),
                IndexLevel: readSession.Snapshot.IndexLevel);
        }
        catch
        {
            readSession.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Path-derived level, for the reason on <see cref="ResolveCurrent"/> — the inspect guard this level arms
    /// reads its refs/callers evidence from <c>IndexDbPath</c>, never from the index served here.
    /// </summary>
    private WorkspaceSymbolReadContext ResolveCurrentSymbolRead()
    {
        long resolveStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        WorkspaceReadHandle readSession = OpenCurrentReadSession();
        try
        {
            bool familyStore = readSession.Snapshot.Mode == WorkspaceReadMode.FamilyStore;
            IndexHolderMetadata? holderMetadata = familyStore ? _holder.MetadataSnapshot() : null;
            (MillerRepositoryIndex Index, long Revision)? legacySnapshot = familyStore ? null : _holder.Snapshot();
            long holderRevision = holderMetadata?.Revision ?? legacySnapshot!.Value.Revision;
            long revision = ContextRevision(readSession.Snapshot, holderRevision);
            ISymbolLookupIndex index = familyStore
                ? ResolveFamilyStoreLookup(_currentWorkspace.WorkspaceId, readSession)
                : legacySnapshot!.Value.Index;
            ReadPhaseTelemetry? readTelemetry = familyStore
                ? ReadTelemetry((MeasuredSymbolLookupIndex)index, graph: null)
                : null;
            var context = new WorkspaceSymbolReadContext(
                index,
                readSession,
                _currentWorkspace.WorkspaceId,
                _currentWorkspace.CanonicalRoot ?? _currentWorkspace.WorkspaceRoot,
                revision,
                familyStore ? null : _currentIndexFresh(holderRevision),
                "current",
                WarningText: null,
                DisplayId: CurrentDisplayId(),
                IndexLevel: readSession.Snapshot.IndexLevel)
            {
                ReadTelemetry = readTelemetry,
            };
            readTelemetry?.CompleteResolve(resolveStartedAt);
            return context;
        }
        catch
        {
            readSession.Dispose();
            throw;
        }
    }

    // Current workspace symbol routing. Explicit sidecar opt-out: the holder's already-built full index serves
    // search. Sidecar enabled on the legacy route: require a revision-fresh on-disk sidecar so stale/missing
    // artifacts are visible instead of hidden behind a memory fallback. On the family-store route search does NOT
    // gate the open on the served stamp: with the sidecar enabled it opens the store sidecar unconditionally and
    // a missing or corrupt one fails visibly, while the stamp only enters the cache key. ResolveFamilyStoreLookup
    // gates its open on a readable stamp instead, and verifies a lagging one against the live artifact, because
    // its consumers join on the ids — search renders self-contained rows and needs neither. The chosen backend is
    // cached keyed on (workspace, dbPath/served stamp, revision), so the sidecar is not opened per query, and a
    // sidecar that converges changes the key and is opened again on the next read.
    private ISymbolLookupIndex ResolveCurrentSymbolSearchIndex(
        MillerRepositoryIndex? holderIndex,
        long revision,
        WorkspaceReadHandle readSession)
    {
        if (readSession.Snapshot.Mode == WorkspaceReadMode.FamilyStore)
        {
            ServedSearchSidecar? served = TryServedSearchStamp(readSession);
            CacheKey storeKey = KeyFor(_currentWorkspace.WorkspaceId, readSession.Snapshot, served?.Stamp);
            if (_sidecar.Enabled)
            {
                return GetOrAddSymbolSearchCache(
                    storeKey,
                    () => new CachedSymbolSearch(
                        MeasureFamilyLookup(_openStoreSymbolSearch(readSession)),
                        IsSidecar: true)).Index;
            }
            return GetOrAddSymbolSearchCache(
                storeKey,
                () => new CachedSymbolSearch(
                    MeasureFamilyLookup(_loadSessionSymbolSearch(readSession)),
                    IsSidecar: false)).Index;
        }

        MillerRepositoryIndex legacyIndex = holderIndex
            ?? throw new InvalidOperationException("The legacy read route has no repository index.");

        if (!_sidecar.Enabled)
            return legacyIndex;

        string dbPath = _currentWorkspace.CanonicalExtractDbPath ?? _currentWorkspace.ExtractDbPath;
        string workspaceKey = string.IsNullOrEmpty(_currentWorkspace.WorkspaceId) ? dbPath : _currentWorkspace.WorkspaceId;
        var key = KeyFor(workspaceKey, dbPath, revision);
        return GetOrLoadSymbolSearch(key, dbPath, () => legacyIndex).Index;
    }

    private WorkspaceReadContext ResolveRegistered(string workspaceId, WorkspaceRefreshMode refresh)
    {
        long resolveStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        RegisteredWorkspaceState state = ResolveRegisteredState(workspaceId, refresh);
        WorkspaceRegistryRow row = state.Row;
        WorkspaceRefreshResult? refreshResult = state.RefreshResult;

        WorkspaceReadHandle readSession = OpenReadSession(row.IndexDbPath, row.CanonicalRoot, row.WorkspaceId);
        try
        {
            bool familyStore = readSession.Snapshot.Mode == WorkspaceReadMode.FamilyStore;
            long revision = ContextRevision(readSession.Snapshot, row.LastRevision ?? 0);
            CachedIndex? cached = familyStore
                ? null
                : GetOrLoad(KeyFor(row.WorkspaceId, row.IndexDbPath, revision), () => _loadIndex(row.IndexDbPath));
            ISymbolLookupIndex index = familyStore
                ? ResolveFamilyStoreLookup(row.WorkspaceId, readSession)
                : cached!.Index;
            ISymbolGraphReachability innerGraph = familyStore
                ? ResolveFamilyStoreGraph(row.WorkspaceId, readSession)
                : cached!.Index.Graph;
            var measuredGraph = familyStore
                ? new MeasuredSymbolGraphReachability(innerGraph, _graphStatementObserver)
                : null;
            ISymbolGraphReachability graph = measuredGraph ?? innerGraph;
            var bridgeGraph = new Lazy<BridgeGraph>(
                familyStore
                    ? () => _loadSessionBridgeGraph(readSession)
                    : () => cached!.Index.BridgeGraph,
                LazyThreadSafetyMode.ExecutionAndPublication);
            SmartTargetResolver resolver = familyStore
                ? new SmartTargetResolver(index)
                : cached!.Resolver;
            ReadPhaseTelemetry? readTelemetry = familyStore
                ? ReadTelemetry((MeasuredSymbolLookupIndex)index, measuredGraph)
                : null;
            var context = new WorkspaceReadContext(
                index,
                graph,
                bridgeGraph,
                resolver,
                readSession,
                row.WorkspaceId,
                row.CanonicalRoot,
                revision,
                familyStore
                    ? null
                    : WorkspaceFreshnessView.IndexFreshFor(refreshResult, row, state.RefreshPending),
                WorkspaceFreshnessView.FreshnessStatusFor(refreshResult, row, state.RefreshPending),
                WorkspaceFreshnessView.WarningTextFor(refreshResult),
                row.DisplayId,
                IndexLevel: readSession.Snapshot.IndexLevel)
            {
                ReadTelemetry = readTelemetry,
            };
            readTelemetry?.CompleteResolve(resolveStartedAt);
            return context;
        }
        catch
        {
            readSession.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The named-read route (inspect, and the context/impact/trace lookup behind <see cref="ResolveCurrent"/>)
    /// serves whatever sidecar <see cref="TryServedSearchStamp"/> reports readable — current, or the same
    /// generation's readable last-good. It used to demand a byte-equal stamp, and <c>StoreSidecarStamp</c>
    /// equality folds the store log sequence and the manifest hash, so ONE converged file change failed it and
    /// sent the read through a whole-generation <see cref="SymbolSearchProjection"/> rebuild over every visible
    /// symbol. That rebuild, not the lookup, was the measured multi-second inspect peak.
    ///
    /// <para>This is NOT the rule <see cref="ResolveCurrentSymbolSearchIndex"/> applies to search, and the
    /// difference runs both ways. Search does not gate the OPEN on the served stamp at all: with the sidecar
    /// enabled it opens the store sidecar unconditionally and lets a missing or corrupt one fail visibly, and the
    /// stamp only enters the cache key. This route gates the open on a readable stamp and falls back to the
    /// in-memory projection instead, so a missing or corrupt sidecar stays silent here. And search's ACCEPTANCE
    /// is not transferable to this consumer: <c>WorkspaceSymbolSearchContext</c> carries an index alone and
    /// renders self-contained rows, while this route's contexts also carry a live graph, a live read session, and
    /// a live bridge graph, and every consumer JOINS on the id the lookup returned.</para>
    ///
    /// <para>So a lagging sidecar is wrapped in <see cref="LaggingSidecarSymbolLookup"/>, which answers from the
    /// live artifact's row of the same id and drops a row the live generation no longer holds. That keeps the id
    /// producer and the id consumers on one generation without the projection rebuild — a stale id can no longer
    /// read as <c>no_dependents</c> or as zero references. The index level travels on the live snapshot too, so
    /// the reference-layer guard is unaffected by which sidecar answered.</para>
    ///
    /// <para>The served stamp is folded into the cache key, so a sidecar that catches up produces a different
    /// key and the next read reopens it instead of serving the lagging generation forever.</para>
    /// </summary>
    private ISymbolLookupIndex ResolveFamilyStoreLookup(
        string? workspaceId,
        WorkspaceReadHandle readSession)
    {
        // The opt-out must do ZERO sidecar I/O, so the enabled test comes before the stamp read.
        if (_sidecar.Enabled && TryServedSearchStamp(readSession) is { } served)
        {
            CacheKey servedKey = KeyFor(workspaceId, readSession.Snapshot, served.Stamp);
            ISymbolLookupIndex sidecarIndex = GetOrAddSymbolSearchCache(
                servedKey,
                () => new CachedSymbolSearch(
                    MeasureFamilyLookup(_openStoreSymbolSearch(readSession)),
                    IsSidecar: true)).Index;
            return MeasureFamilyLookup(
                LaggingSidecarSymbolLookup.Wrap(sidecarIndex, served.Lagging, readSession));
        }

        CacheKey key = KeyFor(workspaceId, readSession.Snapshot);
        return GetOrAddSymbolReadCache(
            key,
            () => new CachedSymbolRead(MeasureFamilyLookup(_loadSessionSymbolSearch(readSession)))).Index;
    }

    /// <summary>
    /// The readable search sidecar for this read, and whether it LAGS the live snapshot. The pair is read once:
    /// the lag test is the same comparison <see cref="StoreSidecarCatalog.TryResolveReadable"/> already made.
    /// </summary>
    private static ServedSearchSidecar? TryServedSearchStamp(WorkspaceReadHandle readSession)
    {
        string? storeRoot = StoreSearchRoot(readSession);
        if (storeRoot is null)
            return null;

        try
        {
            StoreSidecarStamp expected = StoreSidecarStamp.FromSnapshot(
                StoreSidecarKind.Search,
                readSession.Snapshot);
            string searchDbPath = StoreSidecarCatalog.PathFor(
                storeRoot,
                StoreSidecarKind.Search,
                readSession.Snapshot.ViewId);
            StoreSidecarStamp? served = StoreSidecarCatalog.TryResolveReadable(
                searchDbPath,
                expected,
                readSession.Snapshot);
            return served is null ? null : new ServedSearchSidecar(served, served != expected);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string? StoreSearchRoot(WorkspaceReadHandle readSession)
    {
        if (readSession.Snapshot.Mode != WorkspaceReadMode.FamilyStore)
            return null;
        if (!string.IsNullOrWhiteSpace(readSession.FamilyStoreRoot))
            return readSession.FamilyStoreRoot;
        return string.IsNullOrWhiteSpace(readSession.Snapshot.WorkspaceRoot)
            ? null
            : readSession.Snapshot.WorkspaceRoot;
    }

    private ISymbolGraphReachability ResolveFamilyStoreGraph(
        string? workspaceId,
        WorkspaceReadHandle readSession)
    {
        CacheKey key = KeyFor(workspaceId, readSession.Snapshot);
        SqliteSymbolGraphIndex? graph = null;
        graph = new SqliteSymbolGraphIndex(
            readSession,
            () => GetOrAddSupplementalEdges(
                key,
                () => _loadSupplementalEdges is not null
                    ? _loadSupplementalEdges(readSession)
                    : graph!.ReadCurrentSupplementalEdges()));
        return graph;
    }

    private WorkspaceSymbolSearchContext ResolveRegisteredSymbolSearch(string workspaceId, WorkspaceRefreshMode refresh)
    {
        RegisteredWorkspaceState state = ResolveRegisteredState(workspaceId, refresh);
        WorkspaceRegistryRow row = state.Row;
        WorkspaceRefreshResult? refreshResult = state.RefreshResult;

        WorkspaceReadHandle readSession = OpenReadSession(row.IndexDbPath, row.CanonicalRoot, row.WorkspaceId);
        try
        {
            bool familyStore = readSession.Snapshot.Mode == WorkspaceReadMode.FamilyStore;
            long revision = ContextRevision(readSession.Snapshot, row.LastRevision ?? 0);
            ServedSearchSidecar? served = familyStore ? TryServedSearchStamp(readSession) : null;
            CacheKey key = familyStore
                ? KeyFor(row.WorkspaceId, readSession.Snapshot, served?.Stamp)
                : KeyFor(row.WorkspaceId, row.IndexDbPath, revision);
            CachedSymbolSearch cached;
            if (familyStore && _sidecar.Enabled)
            {
                cached = GetOrAddSymbolSearchCache(
                    key,
                    () => new CachedSymbolSearch(
                        MeasureFamilyLookup(_openStoreSymbolSearch(readSession)),
                        IsSidecar: true));
            }
            else if (familyStore)
            {
                cached = GetOrAddSymbolSearchCache(
                    key,
                    () => new CachedSymbolSearch(
                        MeasureFamilyLookup(_loadSessionSymbolSearch(readSession)),
                        IsSidecar: false));
            }
            else
            {
                cached = GetOrLoadSymbolSearch(key, row.IndexDbPath, () => _loadSymbolSearch(row.IndexDbPath));
            }
            return new WorkspaceSymbolSearchContext(
                cached.Index,
                readSession,
                row.WorkspaceId,
                row.CanonicalRoot,
                revision,
                familyStore
                    ? null
                    : WorkspaceFreshnessView.IndexFreshFor(refreshResult, row, state.RefreshPending),
                WorkspaceFreshnessView.FreshnessStatusFor(refreshResult, row, state.RefreshPending),
                WorkspaceFreshnessView.WarningTextFor(refreshResult),
                row.DisplayId,
                IsCurrent: false,
                IndexLevel: readSession.Snapshot.IndexLevel);
        }
        catch
        {
            readSession.Dispose();
            throw;
        }
    }

    private WorkspaceSymbolReadContext ResolveRegisteredSymbolRead(string workspaceId, WorkspaceRefreshMode refresh)
    {
        long resolveStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        RegisteredWorkspaceState state = ResolveRegisteredState(workspaceId, refresh);
        WorkspaceRegistryRow row = state.Row;
        WorkspaceRefreshResult? refreshResult = state.RefreshResult;

        WorkspaceReadHandle readSession = OpenReadSession(row.IndexDbPath, row.CanonicalRoot, row.WorkspaceId);
        try
        {
            bool familyStore = readSession.Snapshot.Mode == WorkspaceReadMode.FamilyStore;
            long revision = ContextRevision(readSession.Snapshot, row.LastRevision ?? 0);
            ISymbolLookupIndex index = familyStore
                ? ResolveFamilyStoreLookup(row.WorkspaceId, readSession)
                : GetOrAddSymbolReadCache(
                    KeyFor(row.WorkspaceId, row.IndexDbPath, revision),
                    () => new CachedSymbolRead(_loadSymbolSearch(row.IndexDbPath))).Index;
            ReadPhaseTelemetry? readTelemetry = familyStore
                ? ReadTelemetry((MeasuredSymbolLookupIndex)index, graph: null)
                : null;
            var context = new WorkspaceSymbolReadContext(
                index,
                readSession,
                row.WorkspaceId,
                row.CanonicalRoot,
                revision,
                familyStore
                    ? null
                    : WorkspaceFreshnessView.IndexFreshFor(refreshResult, row, state.RefreshPending),
                WorkspaceFreshnessView.FreshnessStatusFor(refreshResult, row, state.RefreshPending),
                WorkspaceFreshnessView.WarningTextFor(refreshResult),
                row.DisplayId,
                IsCurrent: false,
                IndexLevel: readSession.Snapshot.IndexLevel)
            {
                ReadTelemetry = readTelemetry,
            };
            readTelemetry?.CompleteResolve(resolveStartedAt);
            return context;
        }
        catch
        {
            readSession.Dispose();
            throw;
        }
    }

    private WorkspaceArtifactContext ResolveRegisteredArtifact(string workspaceId, WorkspaceRefreshMode refresh)
    {
        RegisteredWorkspaceState state = ResolveRegisteredState(workspaceId, refresh);
        WorkspaceRegistryRow row = state.Row;
        WorkspaceRefreshResult? refreshResult = state.RefreshResult;

        WorkspaceReadHandle readSession = OpenReadSession(row.IndexDbPath, row.CanonicalRoot, row.WorkspaceId);
        try
        {
            bool familyStore = readSession.Snapshot.Mode == WorkspaceReadMode.FamilyStore;
            return new WorkspaceArtifactContext(
                readSession,
                row.WorkspaceId,
                row.CanonicalRoot,
                ContextRevision(readSession.Snapshot, row.LastRevision ?? 0),
                familyStore
                    ? null
                    : WorkspaceFreshnessView.IndexFreshFor(refreshResult, row, state.RefreshPending),
                WorkspaceFreshnessView.FreshnessStatusFor(refreshResult, row, state.RefreshPending),
                WorkspaceFreshnessView.WarningTextFor(refreshResult),
                row.DisplayId,
                IndexLevel: readSession.Snapshot.IndexLevel);
        }
        catch
        {
            readSession.Dispose();
            throw;
        }
    }

    // WorkspaceContentSearchContext and WorkspaceTextContentSearchContext deliberately carry NO IndexLevel,
    // unlike every other context this provider returns: they serve content.db, which the levels split does not
    // touch — a symbols-level scan builds the same content corpus a full-level one does, so no content read can
    // report an unextracted layer as an empty repository. Recorded so the asymmetry reads as a boundary rather
    // than an omission.
    private WorkspaceContentSearchContext ResolveCurrentContentSearch()
    {
        // No content index lives in the holder (the bootstrap seeds only the full repository index), so the
        // current workspace builds its content projection lazily on first content query and caches it keyed on
        // the holder's built revision — a freshness Swap (reindex) bumps the revision and rebuilds.
        long holderRevision = _holder.MetadataSnapshot().Revision;
        string dbPath = _currentWorkspace.CanonicalExtractDbPath ?? _currentWorkspace.ExtractDbPath;
        string root = _currentWorkspace.CanonicalRoot ?? _currentWorkspace.WorkspaceRoot;
        string workspaceKey = string.IsNullOrEmpty(_currentWorkspace.WorkspaceId) ? dbPath : _currentWorkspace.WorkspaceId;
        using WorkspaceReadHandle readSession = OpenCurrentReadSession();
        bool familyStore = readSession.Snapshot.Mode == WorkspaceReadMode.FamilyStore;
        long revision = ContextRevision(readSession.Snapshot, holderRevision);
        CachedContentSearch cached = familyStore
            ? GetOrLoadContentSearch(
                KeyFor(workspaceKey, readSession.Snapshot),
                () => ContentSearchProjectionLoader.Load(readSession))
            : GetOrLoadContentSearch(workspaceKey, dbPath, root, revision);
        return new WorkspaceContentSearchContext(
            cached.Index,
            familyStore ? readSession.FamilyStoreRoot! : dbPath,
            _currentWorkspace.WorkspaceId,
            root,
            revision,
            familyStore ? null : _currentIndexFresh(revision),
            "current",
            WarningText: null,
            DisplayId: CurrentDisplayId());
    }

    private WorkspaceContentSearchContext ResolveRegisteredContentSearch(string workspaceId, WorkspaceRefreshMode refresh)
    {
        RegisteredWorkspaceState state = ResolveRegisteredState(workspaceId, refresh);
        WorkspaceRegistryRow row = state.Row;
        WorkspaceRefreshResult? refreshResult = state.RefreshResult;

        using WorkspaceReadHandle readSession = OpenReadSession(row.IndexDbPath, row.CanonicalRoot, row.WorkspaceId);
        bool familyStore = readSession.Snapshot.Mode == WorkspaceReadMode.FamilyStore;
        long revision = ContextRevision(readSession.Snapshot, row.LastRevision ?? 0);
        CachedContentSearch cached = familyStore
            ? GetOrLoadContentSearch(
                KeyFor(row.WorkspaceId, readSession.Snapshot),
                () => ContentSearchProjectionLoader.Load(readSession))
            : GetOrLoadContentSearch(row.WorkspaceId, row.IndexDbPath, row.CanonicalRoot, revision);
        return new WorkspaceContentSearchContext(
            cached.Index,
            familyStore ? readSession.FamilyStoreRoot! : row.IndexDbPath,
            row.WorkspaceId,
            row.CanonicalRoot,
            revision,
            familyStore ? null : WorkspaceFreshnessView.IndexFreshFor(refreshResult, row, state.RefreshPending),
            WorkspaceFreshnessView.FreshnessStatusFor(refreshResult, row, state.RefreshPending),
            WorkspaceFreshnessView.WarningTextFor(refreshResult),
            row.DisplayId);
    }

    private WorkspaceTextContentSearchContext ResolveCurrentTextContentSearch()
    {
        long holderRevision = _holder.MetadataSnapshot().Revision;
        string dbPath = _currentWorkspace.CanonicalExtractDbPath ?? _currentWorkspace.ExtractDbPath;
        string root = _currentWorkspace.CanonicalRoot ?? _currentWorkspace.WorkspaceRoot;
        string workspaceKey = string.IsNullOrEmpty(_currentWorkspace.WorkspaceId) ? dbPath : _currentWorkspace.WorkspaceId;
        long sessionStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        using WorkspaceReadHandle readSession = OpenCurrentReadSession();
        ObserveTextContentIndexResolve(TextContentIndexResolveFamily.ReadSessionOpen, sessionStartedAt);
        bool familyStore = readSession.Snapshot.Mode == WorkspaceReadMode.FamilyStore;
        long revision = ContextRevision(readSession.Snapshot, holderRevision);
        string sourcePath = familyStore
            ? StoreSidecarCatalog.PathFor(
                readSession.FamilyStoreRoot ?? readSession.Snapshot.WorkspaceRoot,
                StoreSidecarKind.Content,
                readSession.Snapshot.ViewId)
            : dbPath;
        CachedTextContentSearch cached = familyStore
            ? GetOrLoadTextContentSearch(
                KeyFor(workspaceKey, readSession.Snapshot),
                () => _openStoreTextContentSearch(readSession))
            : GetOrLoadTextContentSearch(KeyFor(workspaceKey, dbPath, revision), dbPath);
        return new WorkspaceTextContentSearchContext(
            cached.Index,
            sourcePath,
            _currentWorkspace.WorkspaceId,
            root,
            revision,
            familyStore ? null : _currentIndexFresh(revision),
            "current",
            WarningText: null,
            DisplayId: CurrentDisplayId());
    }

    private WorkspaceTextContentSearchContext ResolveRegisteredTextContentSearch(string workspaceId, WorkspaceRefreshMode refresh)
    {
        RegisteredWorkspaceState state = ResolveRegisteredState(workspaceId, refresh);
        WorkspaceRegistryRow row = state.Row;
        WorkspaceRefreshResult? refreshResult = state.RefreshResult;

        long sessionStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        using WorkspaceReadHandle readSession = OpenReadSession(row.IndexDbPath, row.CanonicalRoot, row.WorkspaceId);
        ObserveTextContentIndexResolve(TextContentIndexResolveFamily.ReadSessionOpen, sessionStartedAt);
        bool familyStore = readSession.Snapshot.Mode == WorkspaceReadMode.FamilyStore;
        long revision = ContextRevision(readSession.Snapshot, row.LastRevision ?? 0);
        string sourcePath = familyStore
            ? StoreSidecarCatalog.PathFor(
                readSession.FamilyStoreRoot ?? readSession.Snapshot.WorkspaceRoot,
                StoreSidecarKind.Content,
                readSession.Snapshot.ViewId)
            : row.IndexDbPath;
        CachedTextContentSearch cached = familyStore
            ? GetOrLoadTextContentSearch(
                KeyFor(row.WorkspaceId, readSession.Snapshot),
                () => _openStoreTextContentSearch(readSession))
            : GetOrLoadTextContentSearch(KeyFor(row.WorkspaceId, row.IndexDbPath, revision), row.IndexDbPath);
        return new WorkspaceTextContentSearchContext(
            cached.Index,
            sourcePath,
            row.WorkspaceId,
            row.CanonicalRoot,
            revision,
            familyStore ? null : WorkspaceFreshnessView.IndexFreshFor(refreshResult, row, state.RefreshPending),
            WorkspaceFreshnessView.FreshnessStatusFor(refreshResult, row, state.RefreshPending),
            WorkspaceFreshnessView.WarningTextFor(refreshResult),
            row.DisplayId,
            IsCurrent: false);
    }

    private WorkspaceRegionSearchContext ResolveCurrentRegionSearch()
    {
        long holderRevision = _holder.MetadataSnapshot().Revision;
        string dbPath = _currentWorkspace.CanonicalExtractDbPath ?? _currentWorkspace.ExtractDbPath;
        string root = _currentWorkspace.CanonicalRoot ?? _currentWorkspace.WorkspaceRoot;
        string workspaceKey = string.IsNullOrEmpty(_currentWorkspace.WorkspaceId) ? dbPath : _currentWorkspace.WorkspaceId;
        WorkspaceReadHandle readSession = OpenCurrentReadSession();
        try
        {
            bool familyStore = readSession.Snapshot.Mode == WorkspaceReadMode.FamilyStore;
            long revision = ContextRevision(readSession.Snapshot, holderRevision);
            ServedSearchSidecar? served = familyStore ? TryServedSearchStamp(readSession) : null;
            CacheKey key = familyStore
                ? KeyFor(workspaceKey, readSession.Snapshot, served?.Stamp)
                : KeyFor(workspaceKey, dbPath, revision);
            CachedRegionSearch cached = familyStore
                ? GetOrLoadRegionSearch(
                    key,
                    () => FtsRegionSearchIndex.OpenStore(readSession.FamilyStoreRoot!, readSession.Snapshot))
                : GetOrLoadRegionSearch(key, dbPath);
            return new WorkspaceRegionSearchContext(
                cached.Index,
                readSession,
                _currentWorkspace.WorkspaceId,
                root,
                revision,
                familyStore ? null : _currentIndexFresh(revision),
                "current",
                WarningText: null,
                DisplayId: CurrentDisplayId(),
                IndexLevel: readSession.Snapshot.IndexLevel);
        }
        catch
        {
            readSession.Dispose();
            throw;
        }
    }

    private WorkspaceRegionSearchContext ResolveRegisteredRegionSearch(string workspaceId, WorkspaceRefreshMode refresh)
    {
        RegisteredWorkspaceState state = ResolveRegisteredState(workspaceId, refresh);
        WorkspaceRegistryRow row = state.Row;
        WorkspaceRefreshResult? refreshResult = state.RefreshResult;

        WorkspaceReadHandle readSession = OpenReadSession(row.IndexDbPath, row.CanonicalRoot, row.WorkspaceId);
        try
        {
            bool familyStore = readSession.Snapshot.Mode == WorkspaceReadMode.FamilyStore;
            long revision = ContextRevision(readSession.Snapshot, row.LastRevision ?? 0);
            ServedSearchSidecar? served = familyStore ? TryServedSearchStamp(readSession) : null;
            CacheKey key = familyStore
                ? KeyFor(row.WorkspaceId, readSession.Snapshot, served?.Stamp)
                : KeyFor(row.WorkspaceId, row.IndexDbPath, revision);
            CachedRegionSearch cached = familyStore
                ? GetOrLoadRegionSearch(
                    key,
                    () => FtsRegionSearchIndex.OpenStore(readSession.FamilyStoreRoot!, readSession.Snapshot))
                : GetOrLoadRegionSearch(key, row.IndexDbPath);
            return new WorkspaceRegionSearchContext(
                cached.Index,
                readSession,
                row.WorkspaceId,
                row.CanonicalRoot,
                revision,
                familyStore ? null : WorkspaceFreshnessView.IndexFreshFor(refreshResult, row, state.RefreshPending),
                WorkspaceFreshnessView.FreshnessStatusFor(refreshResult, row, state.RefreshPending),
                WorkspaceFreshnessView.WarningTextFor(refreshResult),
                row.DisplayId,
                IndexLevel: readSession.Snapshot.IndexLevel);
        }
        catch
        {
            readSession.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The refresh path behind every cross-workspace read, as a named seam so its backoff posture is
    /// directly testable rather than living in a constructor lambda a mutation could flip unobserved.
    ///
    /// <para><c>bypassBackoff</c> stays FALSE: this is the AUTOMATIC path, not a person asking. It is now reached
    /// mostly OFF the read path (<see cref="WorkspaceRefreshMode.Background"/>), which makes the posture MORE
    /// load-bearing, not less — a read no longer waits for the answer, so nothing throttles the caller. Bypassing
    /// here would let ten cross-workspace searches against a workspace whose extractor is being OOM-killed spawn
    /// ten more extractor processes.</para>
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="refreshService"/> is null.</exception>
    internal static Func<string, WorkspaceRefreshResult> AutomaticRefresh(
        CrossWorkspaceRefreshService refreshService)
    {
        ArgumentNullException.ThrowIfNull(refreshService);
        return workspaceId => refreshService.Refresh(workspaceId);
    }

    private RegisteredWorkspaceState ResolveRegisteredState(string workspaceId, WorkspaceRefreshMode refresh)
    {
        WorkspaceRegistryRow row = WorkspaceRegistrySelector.Resolve(_registry, workspaceId);
        VerifyRegisteredRoot(row);

        // A serve-then-refresh read needs something to serve. With NO readable index there is no pinned view and
        // no honest stale answer, so that ONE case does the foreground work — which then produces either the built
        // index or the same not-ready error the blocking arm has always raised.
        if (refresh == WorkspaceRefreshMode.Background && !_hasReadableIndex(row))
            refresh = WorkspaceRefreshMode.Blocking;

        if (refresh == WorkspaceRefreshMode.Background)
        {
            StartBackgroundRefresh(row);
            return new RegisteredWorkspaceState(row, RefreshResult: null, RefreshPending: true);
        }

        WorkspaceRefreshResult? refreshResult = null;
        if (refresh == WorkspaceRefreshMode.Blocking)
        {
            long revisionBeforeRefresh = row.LastRevision ?? 0;
            TelemetryContext.Current?.SetWaitReason("workspace_refresh");
            refreshResult = _refresh(row.WorkspaceId);
            if (refreshResult.Status == WorkspaceRefreshStatus.MissingRoot)
                throw new DirectoryNotFoundException(refreshResult.Error ?? $"Workspace root not found: {row.CanonicalRoot}");
            if (refreshResult.Status == WorkspaceRefreshStatus.MissingIndex)
                throw new FileNotFoundException(
                    refreshResult.Error ?? $"Workspace index DB not found: {row.IndexDbPath}",
                    refreshResult.IndexDbPath);
            if (refreshResult.Status == WorkspaceRefreshStatus.Failed)
                throw new InvalidOperationException(
                    refreshResult.Error ?? $"Workspace '{row.WorkspaceId}' refresh failed.");

            row = WorkspaceRegistrySelector.Resolve(_registry, row.WorkspaceId);
            VerifyRegisteredRoot(row);

            // A Refreshed scan whose revision did NOT advance is a from-scratch rebuild: julie deleted and
            // recreated the DB and the fresh artifact's revision counter restarted on (or before) the number
            // the registry already had. The (workspace, db, revision) cache keys collide with the pre-rebuild
            // entries, so evict them explicitly — they describe a file that no longer exists (2026-06-11
            // Eros fleet finding). A normally advancing revision changes the key and needs no eviction.
            if (refreshResult.Status == WorkspaceRefreshStatus.Refreshed &&
                (row.LastRevision ?? 0) <= revisionBeforeRefresh)
            {
                EvictWorkspaceEntries(row.WorkspaceId);
            }
        }

        return new RegisteredWorkspaceState(row, refreshResult, RefreshPending: false);
    }

    /// <summary>
    /// Start ONE background refresh for this workspace, or join the one already running.
    ///
    /// <para>The in-flight set is the coalescing guard the serve-then-refresh default needs: the blocking arm used
    /// to throttle itself because every caller queued behind the same scan, and a fire-and-forget arm has no such
    /// queue. Ten cross-workspace reads in a row therefore start ONE refresh, not ten. The persisted scan-failure
    /// backoff (<c>bypassBackoff: false</c>, see <see cref="AutomaticRefresh"/>) still governs whether that one
    /// refresh is allowed to scan at all.</para>
    ///
    /// <para>The work item never throws into the thread pool: a background refresh that fails must degrade the NEXT
    /// read's freshness, never take down the process or the read that started it.</para>
    /// </summary>
    private void StartBackgroundRefresh(WorkspaceRegistryRow row)
    {
        string workspaceId = row.WorkspaceId;
        long revisionBeforeRefresh = row.LastRevision ?? 0;
        if (!_backgroundRefreshes.TryAdd(workspaceId, 0))
            return;

        bool scheduled = false;
        try
        {
            _scheduleBackgroundRefresh(() =>
            {
                try
                {
                    WorkspaceRefreshResult result = _refresh(workspaceId);

                    // Same from-scratch-rebuild eviction the blocking arm does: a Refreshed scan whose revision did
                    // not advance replaced the file under cache keys that still name it, so the entries describe a
                    // file that no longer exists.
                    if (result.Status == WorkspaceRefreshStatus.Refreshed &&
                        (result.Revision ?? 0) <= revisionBeforeRefresh)
                    {
                        EvictWorkspaceEntries(workspaceId);
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                {
                    // Reported by the NEXT read's freshness status, which is exactly what this arm promises.
                }
                finally
                {
                    _backgroundRefreshes.TryRemove(workspaceId, out _);
                }
            });
            scheduled = true;
        }
        finally
        {
            if (!scheduled)
                _backgroundRefreshes.TryRemove(workspaceId, out _);
        }
    }

    /// <summary>
    /// Cheap "is there anything to serve?" probe for the serve-then-refresh arm. Unknown counts as NOT readable, so
    /// an unreadable workspace takes the foreground path and gets an honest answer instead of a promised stale one.
    /// </summary>
    private static bool HasReadableIndex(WorkspaceRegistryRow row)
    {
        try
        {
            return WorkspaceReadSessionFactory.StoreEnabledFromEnvironment()
                ? Directory.Exists(row.CanonicalRoot) && StoreWorkspacePointer.Read(row.CanonicalRoot) is not null
                : File.Exists(row.IndexDbPath);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    private void EvictWorkspaceEntries(string workspaceId)
    {
        lock (_cacheGate)
        {
            RemoveWorkspaceKeysUnderLock(_cache, workspaceId);
            RemoveWorkspaceKeysUnderLock(_symbolSearchCache, workspaceId);
            RemoveWorkspaceKeysUnderLock(_symbolReadCache, workspaceId);
            RemoveWorkspaceKeysUnderLock(_contentSearchCache, workspaceId);
            RemoveWorkspaceKeysUnderLock(_textContentSearchCache, workspaceId);
            RemoveWorkspaceKeysUnderLock(_regionSearchCache, workspaceId);
        }
    }

    private static void RemoveWorkspaceKeysUnderLock<T>(Dictionary<CacheKey, Lazy<T>> cache, string workspaceId)
    {
        foreach (CacheKey key in cache.Keys
                     .Where(key => string.Equals(key.WorkspaceId, workspaceId, StringComparison.Ordinal))
                     .ToArray())
        {
            cache.Remove(key);
        }
    }

    private CachedIndex GetOrLoad(WorkspaceRegistryRow row, long revision)
    {
        CacheKey key = KeyFor(row.WorkspaceId, row.IndexDbPath, revision);
        return GetOrLoad(key, () => _loadIndex(row.IndexDbPath));
    }

    private CachedIndex GetOrLoad(CacheKey key, Func<MillerRepositoryIndex> load)
    {
        Lazy<CachedIndex> lazy;
        lock (_cacheGate)
        {
            if (!_cache.TryGetValue(key, out lazy!))
            {
                lazy = new Lazy<CachedIndex>(
                    () =>
                    {
                        MillerRepositoryIndex index = load();
                        return new CachedIndex(index, new SmartTargetResolver(index));
                    },
                    LazyThreadSafetyMode.ExecutionAndPublication);
                _cache[key] = lazy;
                EvictOtherEntriesForWorkspaceUnderLock(_cache, key);
            }
        }

        try
        {
            if (!lazy.IsValueCreated)
                TelemetryContext.Current?.SetWaitReason("index_load");
            return lazy.Value;
        }
        catch
        {
            lock (_cacheGate)
            {
                if (_cache.TryGetValue(key, out Lazy<CachedIndex>? cachedLazy) && ReferenceEquals(cachedLazy, lazy))
                    _cache.Remove(key);
            }
            throw;
        }
    }

    // Resolve the search index for a cache key. Sidecar disabled: load the in-memory backend supplied by the
    // caller (the lean projection for a registered workspace; the holder's full index for the current one).
    // Sidecar enabled: require a fresh disk artifact and fail visibly if it is missing/stale/corrupt.
    private CachedSymbolSearch GetOrLoadSymbolSearch(
        CacheKey key, string symbolsDbPath, Func<ISymbolLookupIndex> loadInMemory)
    {
        if (_sidecar.Enabled)
        {
            CachedSymbolSearch? cachedSidecar = TryGetCachedSidecarSymbolSearch(key);
            if (cachedSidecar is not null)
                return cachedSidecar;

            TelemetryContext.Current?.SetWaitReason("index_load");
            FtsSymbolSearchIndex sidecarIndex = _sidecar.OpenRequired(symbolsDbPath, key.Revision);
            return ReplaceSymbolSearchCache(key, new CachedSymbolSearch(sidecarIndex, IsSidecar: true));
        }

        return GetOrAddSymbolSearchCache(key, () => new CachedSymbolSearch(loadInMemory(), IsSidecar: false));
    }

    private CachedSymbolSearch? TryGetCachedSidecarSymbolSearch(CacheKey key)
    {
        Lazy<CachedSymbolSearch>? lazy;
        lock (_cacheGate)
            _symbolSearchCache.TryGetValue(key, out lazy);

        if (lazy is null || !lazy.IsValueCreated)
            return null;

        CachedSymbolSearch cached = lazy.Value;
        return cached.IsSidecar ? cached : null;
    }

    private CachedSymbolSearch ReplaceSymbolSearchCache(CacheKey key, CachedSymbolSearch value)
    {
        var lazy = new Lazy<CachedSymbolSearch>(() => value, LazyThreadSafetyMode.ExecutionAndPublication);
        CachedSymbolSearch cached = lazy.Value;
        lock (_cacheGate)
        {
            _symbolSearchCache[key] = lazy;
            EvictOtherEntriesForWorkspaceUnderLock(_symbolSearchCache, key);
        }

        return cached;
    }

    private CachedSymbolSearch GetOrAddSymbolSearchCache(CacheKey key, Func<CachedSymbolSearch> load)
    {
        Lazy<CachedSymbolSearch> lazy;
        lock (_cacheGate)
        {
            if (!_symbolSearchCache.TryGetValue(key, out lazy!))
            {
                lazy = new Lazy<CachedSymbolSearch>(
                    load,
                    LazyThreadSafetyMode.ExecutionAndPublication);
                _symbolSearchCache[key] = lazy;
                EvictOtherEntriesForWorkspaceUnderLock(_symbolSearchCache, key);
            }
        }

        try
        {
            if (!lazy.IsValueCreated)
                TelemetryContext.Current?.SetWaitReason("index_load");
            return lazy.Value;
        }
        catch
        {
            lock (_cacheGate)
            {
                if (_symbolSearchCache.TryGetValue(key, out Lazy<CachedSymbolSearch>? cachedLazy) &&
                    ReferenceEquals(cachedLazy, lazy))
                    _symbolSearchCache.Remove(key);
            }
            throw;
        }
    }

    private CachedSymbolRead GetOrAddSymbolReadCache(CacheKey key, Func<CachedSymbolRead> load)
    {
        Lazy<CachedSymbolRead> lazy;
        lock (_cacheGate)
        {
            if (!_symbolReadCache.TryGetValue(key, out lazy!))
            {
                lazy = new Lazy<CachedSymbolRead>(load, LazyThreadSafetyMode.ExecutionAndPublication);
                _symbolReadCache[key] = lazy;
                EvictOtherEntriesForWorkspaceUnderLock(_symbolReadCache, key);
            }
        }

        try
        {
            if (!lazy.IsValueCreated)
                TelemetryContext.Current?.SetWaitReason("index_load");
            return lazy.Value;
        }
        catch
        {
            lock (_cacheGate)
            {
                if (_symbolReadCache.TryGetValue(key, out Lazy<CachedSymbolRead>? cachedLazy) &&
                    ReferenceEquals(cachedLazy, lazy))
                    _symbolReadCache.Remove(key);
            }
            throw;
        }
    }

    private CachedContentSearch GetOrLoadContentSearch(string workspaceId, string dbPath, string root, long revision)
    {
        CacheKey key = KeyFor(workspaceId, dbPath, revision);
        return GetOrLoadContentSearch(key, () => _loadContentSearch(dbPath, root));
    }

    private CachedContentSearch GetOrLoadContentSearch(CacheKey key, Func<ContentSearchProjection> load)
    {
        Lazy<CachedContentSearch> lazy;
        lock (_cacheGate)
        {
            if (!_contentSearchCache.TryGetValue(key, out lazy!))
            {
                lazy = new Lazy<CachedContentSearch>(
                    () => new CachedContentSearch(load()),
                    LazyThreadSafetyMode.ExecutionAndPublication);
                _contentSearchCache[key] = lazy;
                EvictOtherEntriesForWorkspaceUnderLock(_contentSearchCache, key);
            }
        }

        try
        {
            if (!lazy.IsValueCreated)
                TelemetryContext.Current?.SetWaitReason("index_load");
            return lazy.Value;
        }
        catch
        {
            lock (_cacheGate)
            {
                if (_contentSearchCache.TryGetValue(key, out Lazy<CachedContentSearch>? cachedLazy) &&
                    ReferenceEquals(cachedLazy, lazy))
                    _contentSearchCache.Remove(key);
            }
            throw;
        }
    }

    private CachedRegionSearch GetOrLoadRegionSearch(CacheKey key, string dbPath)
    {
        return GetOrLoadRegionSearch(key, () => OpenRegionSearch(dbPath, key.Revision));
    }

    private CachedRegionSearch GetOrLoadRegionSearch(CacheKey key, Func<IRegionSearchIndex> load)
    {
        EnsureRegionSearchEnabled();

        Lazy<CachedRegionSearch> lazy;
        lock (_cacheGate)
        {
            if (!_regionSearchCache.TryGetValue(key, out lazy!))
            {
                lazy = new Lazy<CachedRegionSearch>(
                    () => new CachedRegionSearch(load()),
                    LazyThreadSafetyMode.ExecutionAndPublication);
                _regionSearchCache[key] = lazy;
                EvictOtherEntriesForWorkspaceUnderLock(_regionSearchCache, key);
            }
        }

        try
        {
            if (!lazy.IsValueCreated)
                TelemetryContext.Current?.SetWaitReason("index_load");
            return lazy.Value;
        }
        catch
        {
            lock (_cacheGate)
            {
                if (_regionSearchCache.TryGetValue(key, out Lazy<CachedRegionSearch>? cachedLazy) &&
                    ReferenceEquals(cachedLazy, lazy))
                    _regionSearchCache.Remove(key);
            }
            throw;
        }
    }

    private CachedTextContentSearch GetOrLoadTextContentSearch(CacheKey key, string dbPath)
    {
        return GetOrLoadTextContentSearch(key, () => _loadTextContentSearch(dbPath, key.Revision));
    }

    private CachedTextContentSearch GetOrLoadTextContentSearch(
        CacheKey key,
        Func<ITextContentSearchIndex> load)
    {
        Lazy<CachedTextContentSearch> lazy;
        bool cacheHit;
        long cacheStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (_cacheGate)
        {
            cacheHit = _textContentSearchCache.TryGetValue(key, out lazy!);
            if (!cacheHit)
            {
                lazy = new Lazy<CachedTextContentSearch>(
                    () =>
                    {
                        long loadStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                        ITextContentSearchIndex index = load();
                        ObserveTextContentIndexResolve(TextContentIndexResolveFamily.IndexLoad, loadStartedAt);
                        return new CachedTextContentSearch(index);
                    },
                    LazyThreadSafetyMode.ExecutionAndPublication);
                _textContentSearchCache[key] = lazy;
                EvictOtherEntriesForWorkspaceUnderLock(_textContentSearchCache, key);
            }
        }
        ObserveTextContentIndexResolve(
            cacheHit ? TextContentIndexResolveFamily.CacheHit : TextContentIndexResolveFamily.CacheMiss,
            cacheStartedAt);

        try
        {
            if (!lazy.IsValueCreated)
                TelemetryContext.Current?.SetWaitReason("index_load");
            return lazy.Value;
        }
        catch
        {
            lock (_cacheGate)
            {
                if (_textContentSearchCache.TryGetValue(key, out Lazy<CachedTextContentSearch>? cachedLazy) &&
                    ReferenceEquals(cachedLazy, lazy))
                    _textContentSearchCache.Remove(key);
            }
            throw;
        }
    }

    private static void ObserveTextContentIndexResolve(TextContentIndexResolveFamily family, long startedAt)
    {
        TimeSpan elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(startedAt);
        TextContentIndexResolveTelemetryCollector.Current?.Record(
            new TextContentIndexResolveObservation(family, elapsed));
    }

    private void EnsureRegionSearchEnabled()
    {
        if (!_sidecar.Enabled)
        {
            throw new InvalidOperationException(
                "region search requires the search sidecar. Set MILLER_SEARCH_SIDECAR=1, then refresh the workspace.");
        }
        if (!_sidecar.RegionOptions.Enabled)
        {
            throw new InvalidOperationException(
                "region search is disabled by MILLER_REGION_INDEX=0. Unset it or use a truthy value, then refresh the workspace.");
        }
    }

    private IRegionSearchIndex OpenRegionSearch(string dbPath, long revision)
    {
        try
        {
            return _loadRegionSearch(dbPath, revision);
        }
        catch (Exception ex) when (
            ex is FileNotFoundException or InvalidOperationException or IOException
                or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new InvalidOperationException(
                "region search requires a refreshed source-region search sidecar: " + ex.Message,
                ex);
        }
    }

    private IReadOnlyList<GraphEdge> GetOrAddSupplementalEdges(
        CacheKey key,
        Func<IReadOnlyList<GraphEdge>> load) =>
        _supplementalEdgesCache.GetOrAdd(key.WorkspaceId, key.ToString(), load);

    private static void EvictOtherEntriesForWorkspaceUnderLock<T>(
        Dictionary<CacheKey, Lazy<T>> cache, CacheKey keep)
    {
        foreach (CacheKey key in cache.Keys
                     .Where(key => string.Equals(key.WorkspaceId, keep.WorkspaceId, StringComparison.Ordinal)
                                   && !key.Equals(keep))
                     .ToArray())
        {
            cache.Remove(key);
        }
    }

    private void VerifyRegisteredRoot(WorkspaceRegistryRow row)
    {
        if (!Directory.Exists(row.CanonicalRoot))
        {
            string error = $"Workspace root not found: {row.CanonicalRoot}";
            _registry.MarkMissing(row.WorkspaceId, error);
            throw new DirectoryNotFoundException(error);
        }

        try
        {
            WorkspaceRootSafety.RejectSensitiveRoot(row.CanonicalRoot, fromCwd: false);
        }
        catch (InvalidOperationException ex)
        {
            _registry.MarkError(row.WorkspaceId, ex.Message);
            throw;
        }
    }

    private string? CurrentDisplayId()
    {
        if (string.IsNullOrWhiteSpace(_currentWorkspace.WorkspaceId))
            return null;

        string root = _currentWorkspace.CanonicalRoot ?? _currentWorkspace.WorkspaceRoot;
        try
        {
            return WorkspaceId.Display(root, _currentWorkspace.WorkspaceId);
        }
        catch (ArgumentException)
        {
            return _currentWorkspace.WorkspaceId;
        }
    }

    private bool IsCurrentSelector(string selector)
    {
        string trimmed = selector.Trim();
        if (string.Equals(trimmed, "current", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "primary", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrWhiteSpace(_currentWorkspace.WorkspaceId) &&
            string.Equals(trimmed, _currentWorkspace.WorkspaceId, StringComparison.Ordinal))
            return true;

        string? displayId = CurrentDisplayId();
        return !string.IsNullOrWhiteSpace(displayId) &&
               string.Equals(trimmed, displayId, StringComparison.OrdinalIgnoreCase);
    }

    private bool SelectorTargetsCurrent(string selector)
    {
        if (IsCurrentSelector(selector))
            return true;

        WorkspaceRegistryRow row = WorkspaceRegistrySelector.Resolve(_registry, selector);
        return IsCurrentWorkspace(row);
    }

    private bool IsCurrentWorkspace(WorkspaceRegistryRow row) =>
        string.Equals(row.WorkspaceId, _currentWorkspace.WorkspaceId, StringComparison.Ordinal) ||
        WorkspaceSafety.IsLiveWorkspace(
            row.CanonicalRoot,
            _currentWorkspace.CanonicalRoot ?? _currentWorkspace.WorkspaceRoot);

    private WorkspaceReadHandle OpenCurrentReadSession()
    {
        string databasePath = _currentWorkspace.CanonicalExtractDbPath ?? _currentWorkspace.ExtractDbPath;
        string root = _currentWorkspace.CanonicalRoot ?? _currentWorkspace.WorkspaceRoot;
        return OpenReadSession(databasePath, root, _currentWorkspace.WorkspaceId);
    }

    private WorkspaceReadHandle OpenReadSession(string databasePath, string root, string? workspaceId)
    {
        try
        {
            return _openReadSession(databasePath, root, workspaceId);
        }
        catch (StorePointerFormatException exception)
        {
            if (string.IsNullOrWhiteSpace(workspaceId))
            {
                throw MalformedStorePointer(
                    "The workspace store pointer is malformed and cannot be reconciled because the workspace is not registered.",
                    exception);
            }

            WorkspaceRefreshResult refresh = _refresh(workspaceId);
            if (refresh.Status is not (WorkspaceRefreshStatus.Refreshed or WorkspaceRefreshStatus.Unchanged))
            {
                string detail = refresh.Error ?? refresh.WarningText ??
                    $"reconciliation returned {refresh.StatusText}";
                throw MalformedStorePointer(
                    $"The workspace store pointer is malformed and reconciliation did not complete: {detail}",
                    exception);
            }

            try
            {
                return _openReadSession(databasePath, root, workspaceId);
            }
            catch (StorePointerFormatException retryException)
            {
                throw MalformedStorePointer(
                    "The workspace store pointer is still malformed after reconciliation.",
                    retryException);
            }
        }
    }

    private static FamilyStoreReadException MalformedStorePointer(
        string message,
        StorePointerFormatException exception) =>
        new(FamilyStoreReadFailure.CurrentMalformed, message, exception);

    private readonly record struct CacheKey(
        string WorkspaceId,
        string IndexDbPath,
        long Revision,
        long FileWriteStampTicks,
        long FileLength,
        string? ArtifactId,
        ArtifactStampState StampState);

    // Revision alone cannot be trusted across processes: a force rebuild in ANOTHER process replaces the file,
    // and the fresh artifact's restarted revision counter can land on the number already cached here while this
    // process's own scan legitimately reports no_change (sources unchanged after the rebuild). The in-process
    // eviction in ResolveRegisteredState never sees that rewrite (2026-06-11 Eros fleet finding, cross-process
    // case).
    //
    // The key therefore carries the artifact id, which names the generation outright, PLUS the file identity
    // (last-write ticks + length). The file stamp alone was only a probabilistic guard — it argued that a
    // delete+recreate could not reproduce an identical length and tick — and a probabilistic guard is the wrong
    // shape for a correctness invariant. The stamp stays because it still catches a same-id in-place rewrite,
    // and it costs one stat.
    private static CacheKey KeyFor(string workspaceId, string dbPath, long revision)
    {
        // A null artifact id has three distinct causes, and folding them together would let a generation whose
        // identity could not be read share a key with a genuine pre-stamping one.
        SymbolsArtifactIdentity identity = SymbolsArtifactIdentity.TryRead(dbPath);
        var info = new FileInfo(dbPath);
        return info.Exists
            ? new CacheKey(
                workspaceId, dbPath, revision, info.LastWriteTimeUtc.Ticks, info.Length,
                identity.ArtifactId, identity.StampState)
            : new CacheKey(workspaceId, dbPath, revision, 0, 0, identity.ArtifactId, identity.StampState);
    }

    private static CacheKey KeyFor(
        string? workspaceId,
        WorkspaceReadSnapshot snapshot,
        StoreSidecarStamp? servedSidecar = null)
    {
        WorkspaceFreshnessToken freshness = snapshot.Freshness;
        string resolvedWorkspaceId = string.IsNullOrWhiteSpace(workspaceId)
            ? snapshot.WorkspaceId ?? snapshot.WorkspaceRoot
            : workspaceId;
        string sourceIdentity = snapshot.Mode == WorkspaceReadMode.FamilyStore
            ? $"store:{freshness.StoreInstanceId ?? snapshot.ArtifactOrStoreId}:{freshness.ViewId ?? snapshot.ViewId}:{freshness.GenerationName ?? snapshot.GenerationName}:{freshness.ManifestGeneration ?? snapshot.ManifestGeneration}:{freshness.ManifestHash}:{freshness.StoreLogSequence}:{freshness.IndexLevel ?? snapshot.IndexLevel}:{freshness.LevelStampL1}:{freshness.LevelStampL2}:{freshness.LevelStampL3}:{freshness.ResolutionStamp}:{freshness.SearchStamp}:{freshness.ContentStamp}:{freshness.VectorStamp}"
            : snapshot.ArtifactOrStoreId;
        if (servedSidecar is not null)
        {
            sourceIdentity +=
                $":serve:{servedSidecar.StoreLogSequence}:{servedSidecar.ScopeToken}";
        }

        return new CacheKey(
            resolvedWorkspaceId,
            sourceIdentity,
            freshness.Revision,
            servedSidecar?.StoreLogSequence ?? freshness.StoreLogSequence ?? 0,
            0,
            freshness.StoreInstanceId ?? freshness.ArtifactOrStoreId,
            ArtifactStampState.Present);
    }

    private static long ContextRevision(WorkspaceReadSnapshot snapshot, long legacyRevision) =>
        snapshot.Mode == WorkspaceReadMode.FamilyStore
            ? snapshot.Freshness.StoreLogSequence
                ?? throw new InvalidOperationException("The family-store snapshot has no store_log sequence.")
            : legacyRevision;

    internal WorkspaceIndexProviderCacheSnapshot CacheSnapshot()
    {
        lock (_cacheGate)
        {
            return new WorkspaceIndexProviderCacheSnapshot(
                _cache.Count,
                _symbolSearchCache.Count,
                _symbolReadCache.Count,
                _contentSearchCache.Count,
                _textContentSearchCache.Count,
                _regionSearchCache.Count,
                _supplementalEdgesCache.Count);
        }
    }

    private ReadPhaseTelemetry ReadTelemetry(
        MeasuredSymbolLookupIndex lookup,
        MeasuredSymbolGraphReachability? graph)
    {
        WorkspaceIndexProviderCacheSnapshot cache = CacheSnapshot();
        return new ReadPhaseTelemetry(lookup, graph, cache.TotalEntries);
    }

    private static MeasuredSymbolLookupIndex MeasureFamilyLookup(ISymbolLookupIndex index) =>
        index as MeasuredSymbolLookupIndex ?? new MeasuredSymbolLookupIndex(index);

    private static void ObserveGraphStatement(GraphStatementObservation observation)
    {
        string phase = observation.Phase switch
        {
            GraphStatementPhase.RelationshipForward => "relationship_forward",
            GraphStatementPhase.RelationshipReverse => "relationship_reverse",
            GraphStatementPhase.UnresolvedNameForward => "unresolved_name_forward",
            GraphStatementPhase.UnresolvedNameReverse => "unresolved_name_reverse",
            GraphStatementPhase.IdentifierBaseForward => "identifier_base_forward",
            GraphStatementPhase.IdentifierDeltaForward => "identifier_delta_forward",
            GraphStatementPhase.PendingBaseForward => "pending_base_forward",
            GraphStatementPhase.PendingDeltaForward => "pending_delta_forward",
            GraphStatementPhase.IdentifierBaseReverse => "identifier_base_reverse",
            GraphStatementPhase.IdentifierDeltaReverse => "identifier_delta_reverse",
            GraphStatementPhase.PendingBaseReverse => "pending_base_reverse",
            GraphStatementPhase.PendingDeltaReverse => "pending_delta_reverse",
            GraphStatementPhase.FamilyResolution => "family_resolution",
            GraphStatementPhase.Supplemental => "supplemental",
            GraphStatementPhase.Completion => "completion",
            _ => throw new InvalidOperationException("Unknown graph statement phase."),
        };
        Serilog.Log.Information(
            "Graph statement phase {GraphStatementPhase} completed in {GraphStatementElapsedMs} ms with " +
            "{GraphStatementRows} rows for {GraphStatementCandidateCount} candidates " +
            "{GraphStatementCandidateSample} for cid {CorrelationId}",
            phase,
            Math.Max(0, (long)observation.Elapsed.TotalMilliseconds),
            observation.Rows,
            observation.CandidateCount,
            ServerJson.Strings(observation.CandidateSample),
            TelemetryContext.Current?.CorrelationId ?? "unmeasured");
    }

    /// <param name="RefreshPending">
    /// True when this read served the PINNED view and left a refresh running behind it. It is not an error and not
    /// a confirmed-fresh read, so it gets its own freshness word rather than borrowing either.
    /// </param>
    private sealed record RegisteredWorkspaceState(
        WorkspaceRegistryRow Row, WorkspaceRefreshResult? RefreshResult, bool RefreshPending);

    private sealed record CachedIndex(MillerRepositoryIndex Index, SmartTargetResolver Resolver);

    /// <summary>The readable search sidecar for one read, and whether it lags the live snapshot.</summary>
    private readonly record struct ServedSearchSidecar(StoreSidecarStamp Stamp, bool Lagging);

    private sealed record CachedSymbolSearch(ISymbolLookupIndex Index, bool IsSidecar);

    private sealed record CachedSymbolRead(ISymbolLookupIndex Index);

    private sealed record CachedContentSearch(ContentSearchProjection Index);

    private sealed record CachedTextContentSearch(ITextContentSearchIndex Index);

    private sealed record CachedRegionSearch(IRegionSearchIndex Index);

}

public sealed class SupplementalEdgeCache
{
    private readonly object _gate = new();
    private readonly Dictionary<ShareKey, Lazy<IReadOnlyList<GraphEdge>>> _cache = new();

    internal int Count
    {
        get
        {
            lock (_gate)
                return _cache.Count;
        }
    }

    internal IReadOnlyList<GraphEdge> GetOrAdd(
        string workspaceId,
        string identity,
        Func<IReadOnlyList<GraphEdge>> load)
    {
        var key = new ShareKey(workspaceId, identity);
        Lazy<IReadOnlyList<GraphEdge>> lazy;
        lock (_gate)
        {
            if (!_cache.TryGetValue(key, out lazy!))
            {
                lazy = new Lazy<IReadOnlyList<GraphEdge>>(load, LazyThreadSafetyMode.ExecutionAndPublication);
                _cache[key] = lazy;
                foreach (ShareKey existing in _cache.Keys
                             .Where(candidate =>
                                 string.Equals(candidate.WorkspaceId, key.WorkspaceId, StringComparison.Ordinal) &&
                                 !candidate.Equals(key))
                             .ToArray())
                    _cache.Remove(existing);
            }
        }

        try
        {
            if (!lazy.IsValueCreated)
                TelemetryContext.Current?.SetWaitReason("index_load");
            return lazy.Value;
        }
        catch
        {
            lock (_gate)
            {
                if (_cache.TryGetValue(key, out Lazy<IReadOnlyList<GraphEdge>>? cachedLazy) &&
                    ReferenceEquals(cachedLazy, lazy))
                    _cache.Remove(key);
            }
            throw;
        }
    }

    private readonly record struct ShareKey(string WorkspaceId, string Identity);
}

internal readonly record struct WorkspaceIndexProviderCacheSnapshot(
    int RepositoryEntries,
    int SymbolSearchEntries,
    int SymbolReadEntries,
    int ContentSearchEntries,
    int TextContentSearchEntries,
    int RegionSearchEntries,
    int SupplementalEdgeEntries)
{
    public int TotalEntries =>
        RepositoryEntries +
        SymbolSearchEntries +
        SymbolReadEntries +
        ContentSearchEntries +
        TextContentSearchEntries +
        RegionSearchEntries +
        SupplementalEdgeEntries;
}
