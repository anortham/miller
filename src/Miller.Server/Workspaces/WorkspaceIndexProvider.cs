using Miller.Core.Graph;
using Miller.Indexing;
using Miller.Indexing.Reads;
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
    private readonly Func<IWorkspaceReadSession, BridgeGraph> _loadSessionBridgeGraph;
    private readonly SymbolSearchSidecar _sidecar;
    private readonly object _cacheGate = new();
    private readonly Dictionary<CacheKey, Lazy<CachedIndex>> _cache = new();
    private readonly Dictionary<CacheKey, Lazy<CachedSymbolSearch>> _symbolSearchCache = new();
    private readonly Dictionary<CacheKey, Lazy<CachedSymbolRead>> _symbolReadCache = new();
    private readonly Dictionary<CacheKey, Lazy<CachedContentSearch>> _contentSearchCache = new();
    private readonly Dictionary<CacheKey, Lazy<CachedTextContentSearch>> _textContentSearchCache = new();
    private readonly Dictionary<CacheKey, Lazy<CachedRegionSearch>> _regionSearchCache = new();

    public WorkspaceIndexProvider(
        IndexHolder holder,
        WorkspaceContext currentWorkspace,
        WorkspaceRegistry registry,
        CrossWorkspaceRefreshService refreshService,
        SymbolSearchSidecar sidecar)
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
                WorkspaceReadSessionFactory.Open(databasePath, root, workspaceId))
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
        Func<IWorkspaceReadSession, BridgeGraph>? loadSessionBridgeGraph = null)
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
            string storeRoot = readSession.FamilyStoreRoot
                ?? throw new InvalidOperationException("The family-store read session has no store root.");
            return _sidecar.OpenStoreRequired(storeRoot, readSession.Snapshot);
        });
        _loadSessionBridgeGraph = loadSessionBridgeGraph ?? (session => SessionBridgeGraphLoader.Load(session));
    }

    public WorkspaceReadContext Resolve(string? workspaceId, bool ensureFresh)
    {
        if (workspaceId is null)
            return ResolveCurrent();

        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        if (SelectorTargetsCurrent(workspaceId))
            return ResolveCurrent();

        return ResolveRegistered(workspaceId, ensureFresh);
    }

    public WorkspaceArtifactContext ResolveArtifact(string? workspaceId, bool ensureFresh)
    {
        if (workspaceId is null)
            return ResolveCurrentArtifact();

        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        if (SelectorTargetsCurrent(workspaceId))
            return ResolveCurrentArtifact();

        return ResolveRegisteredArtifact(workspaceId, ensureFresh);
    }

    public WorkspaceSymbolSearchContext ResolveSymbolSearch(string? workspaceId, bool ensureFresh)
    {
        if (workspaceId is null)
            return ResolveCurrentSymbolSearch();

        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        if (SelectorTargetsCurrent(workspaceId))
            return ResolveCurrentSymbolSearch();

        return ResolveRegisteredSymbolSearch(workspaceId, ensureFresh);
    }

    public WorkspaceSymbolReadContext ResolveSymbolRead(string? workspaceId, bool ensureFresh)
    {
        if (workspaceId is null)
            return ResolveCurrentSymbolRead();

        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        if (SelectorTargetsCurrent(workspaceId))
            return ResolveCurrentSymbolRead();

        return ResolveRegisteredSymbolRead(workspaceId, ensureFresh);
    }

    public WorkspaceContentSearchContext ResolveContentSearch(string? workspaceId, bool ensureFresh)
    {
        if (workspaceId is null)
            return ResolveCurrentContentSearch();

        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        if (SelectorTargetsCurrent(workspaceId))
            return ResolveCurrentContentSearch();

        return ResolveRegisteredContentSearch(workspaceId, ensureFresh);
    }

    public WorkspaceRegionSearchContext ResolveRegionSearch(string? workspaceId, bool ensureFresh)
    {
        if (workspaceId is null)
            return ResolveCurrentRegionSearch();

        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        if (SelectorTargetsCurrent(workspaceId))
            return ResolveCurrentRegionSearch();

        return ResolveRegisteredRegionSearch(workspaceId, ensureFresh);
    }

    public WorkspaceTextContentSearchContext ResolveTextContentSearch(string? workspaceId, bool ensureFresh)
    {
        if (workspaceId is null)
            return ResolveCurrentTextContentSearch();

        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        if (SelectorTargetsCurrent(workspaceId))
            return ResolveCurrentTextContentSearch();

        return ResolveRegisteredTextContentSearch(workspaceId, ensureFresh);
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
            ISymbolGraphReachability graph = familyStore
                ? new SqliteSymbolGraphIndex(readSession)
                : holderIndex!.Graph;
            var bridgeGraph = new Lazy<BridgeGraph>(
                familyStore
                    ? () => _loadSessionBridgeGraph(readSession)
                    : () => holderIndex!.BridgeGraph,
                LazyThreadSafetyMode.ExecutionAndPublication);
            var resolver = new SmartTargetResolver(index);
            return new WorkspaceReadContext(
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
                IndexLevel: readSession.Snapshot.IndexLevel);
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
            return new WorkspaceSymbolReadContext(
                index,
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

    // Current workspace symbol routing. Explicit sidecar opt-out: the holder's already-built full index serves
    // search. Sidecar enabled: require a revision-fresh on-disk sidecar so stale/missing artifacts are visible
    // instead of hidden behind a memory fallback. The chosen backend is cached keyed on (workspace, dbPath,
    // revision) so a freshness Swap (revision bump) rebuilds it and the sidecar is not re-opened per query.
    private ISymbolLookupIndex ResolveCurrentSymbolSearchIndex(
        MillerRepositoryIndex? holderIndex,
        long revision,
        WorkspaceReadHandle readSession)
    {
        if (readSession.Snapshot.Mode == WorkspaceReadMode.FamilyStore)
        {
            CacheKey storeKey = KeyFor(_currentWorkspace.WorkspaceId, readSession.Snapshot);
            if (_sidecar.Enabled)
            {
                string storeRoot = readSession.FamilyStoreRoot
                    ?? throw new InvalidOperationException("The family-store read session has no store root.");
                return GetOrAddSymbolSearchCache(
                    storeKey,
                    () => new CachedSymbolSearch(
                        _sidecar.OpenStoreRequired(storeRoot, readSession.Snapshot),
                        IsSidecar: true)).Index;
            }
            return GetOrAddSymbolSearchCache(
                storeKey,
                () => new CachedSymbolSearch(_loadSessionSymbolSearch(readSession), IsSidecar: false)).Index;
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

    private WorkspaceReadContext ResolveRegistered(string workspaceId, bool ensureFresh)
    {
        RegisteredWorkspaceState state = ResolveRegisteredState(workspaceId, ensureFresh);
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
            ISymbolGraphReachability graph = familyStore
                ? new SqliteSymbolGraphIndex(readSession)
                : cached!.Index.Graph;
            var bridgeGraph = new Lazy<BridgeGraph>(
                familyStore
                    ? () => _loadSessionBridgeGraph(readSession)
                    : () => cached!.Index.BridgeGraph,
                LazyThreadSafetyMode.ExecutionAndPublication);
            SmartTargetResolver resolver = familyStore
                ? new SmartTargetResolver(index)
                : cached!.Resolver;
            return new WorkspaceReadContext(
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
                    : WorkspaceFreshnessView.IndexFreshFor(refreshResult, row),
                WorkspaceFreshnessView.FreshnessStatusFor(refreshResult, row),
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

    private ISymbolLookupIndex ResolveFamilyStoreLookup(
        string? workspaceId,
        WorkspaceReadHandle readSession)
    {
        CacheKey key = KeyFor(workspaceId, readSession.Snapshot);
        if (_sidecar.Enabled)
        {
            return GetOrAddSymbolSearchCache(
                key,
                () => new CachedSymbolSearch(
                    _openStoreSymbolSearch(readSession),
                    IsSidecar: true)).Index;
        }

        return GetOrAddSymbolReadCache(
            key,
            () => new CachedSymbolRead(_loadSessionSymbolSearch(readSession))).Index;
    }

    private WorkspaceSymbolSearchContext ResolveRegisteredSymbolSearch(string workspaceId, bool ensureFresh)
    {
        RegisteredWorkspaceState state = ResolveRegisteredState(workspaceId, ensureFresh);
        WorkspaceRegistryRow row = state.Row;
        WorkspaceRefreshResult? refreshResult = state.RefreshResult;

        WorkspaceReadHandle readSession = OpenReadSession(row.IndexDbPath, row.CanonicalRoot, row.WorkspaceId);
        try
        {
            bool familyStore = readSession.Snapshot.Mode == WorkspaceReadMode.FamilyStore;
            long revision = ContextRevision(readSession.Snapshot, row.LastRevision ?? 0);
            CacheKey key = familyStore
                ? KeyFor(row.WorkspaceId, readSession.Snapshot)
                : KeyFor(row.WorkspaceId, row.IndexDbPath, revision);
            CachedSymbolSearch cached;
            if (familyStore && _sidecar.Enabled)
            {
                string storeRoot = readSession.FamilyStoreRoot
                    ?? throw new InvalidOperationException("The family-store read session has no store root.");
                cached = GetOrAddSymbolSearchCache(
                    key,
                    () => new CachedSymbolSearch(
                        _sidecar.OpenStoreRequired(storeRoot, readSession.Snapshot),
                        IsSidecar: true));
            }
            else if (familyStore)
            {
                cached = GetOrAddSymbolSearchCache(
                    key,
                    () => new CachedSymbolSearch(_loadSessionSymbolSearch(readSession), IsSidecar: false));
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
                    : WorkspaceFreshnessView.IndexFreshFor(refreshResult, row),
                WorkspaceFreshnessView.FreshnessStatusFor(refreshResult, row),
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

    private WorkspaceSymbolReadContext ResolveRegisteredSymbolRead(string workspaceId, bool ensureFresh)
    {
        RegisteredWorkspaceState state = ResolveRegisteredState(workspaceId, ensureFresh);
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
            return new WorkspaceSymbolReadContext(
                index,
                readSession,
                row.WorkspaceId,
                row.CanonicalRoot,
                revision,
                familyStore
                    ? null
                    : WorkspaceFreshnessView.IndexFreshFor(refreshResult, row),
                WorkspaceFreshnessView.FreshnessStatusFor(refreshResult, row),
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

    private WorkspaceArtifactContext ResolveRegisteredArtifact(string workspaceId, bool ensureFresh)
    {
        RegisteredWorkspaceState state = ResolveRegisteredState(workspaceId, ensureFresh);
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
                    : WorkspaceFreshnessView.IndexFreshFor(refreshResult, row),
                WorkspaceFreshnessView.FreshnessStatusFor(refreshResult, row),
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

    private WorkspaceContentSearchContext ResolveRegisteredContentSearch(string workspaceId, bool ensureFresh)
    {
        RegisteredWorkspaceState state = ResolveRegisteredState(workspaceId, ensureFresh);
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
            familyStore ? null : WorkspaceFreshnessView.IndexFreshFor(refreshResult, row),
            WorkspaceFreshnessView.FreshnessStatusFor(refreshResult, row),
            WorkspaceFreshnessView.WarningTextFor(refreshResult),
            row.DisplayId);
    }

    private WorkspaceTextContentSearchContext ResolveCurrentTextContentSearch()
    {
        long holderRevision = _holder.MetadataSnapshot().Revision;
        string dbPath = _currentWorkspace.CanonicalExtractDbPath ?? _currentWorkspace.ExtractDbPath;
        string root = _currentWorkspace.CanonicalRoot ?? _currentWorkspace.WorkspaceRoot;
        string workspaceKey = string.IsNullOrEmpty(_currentWorkspace.WorkspaceId) ? dbPath : _currentWorkspace.WorkspaceId;
        using WorkspaceReadHandle readSession = OpenCurrentReadSession();
        bool familyStore = readSession.Snapshot.Mode == WorkspaceReadMode.FamilyStore;
        long revision = ContextRevision(readSession.Snapshot, holderRevision);
        string sourcePath = familyStore
            ? StoreSidecarCatalog.PathFor(readSession.FamilyStoreRoot!, StoreSidecarKind.Content, readSession.Snapshot.ViewId)
            : dbPath;
        CachedTextContentSearch cached = familyStore
            ? GetOrLoadTextContentSearch(
                KeyFor(workspaceKey, readSession.Snapshot),
                () => ContentCorpusSidecar.OpenStoreGenerationChecked(readSession.FamilyStoreRoot!, readSession.Snapshot))
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

    private WorkspaceTextContentSearchContext ResolveRegisteredTextContentSearch(string workspaceId, bool ensureFresh)
    {
        RegisteredWorkspaceState state = ResolveRegisteredState(workspaceId, ensureFresh);
        WorkspaceRegistryRow row = state.Row;
        WorkspaceRefreshResult? refreshResult = state.RefreshResult;

        using WorkspaceReadHandle readSession = OpenReadSession(row.IndexDbPath, row.CanonicalRoot, row.WorkspaceId);
        bool familyStore = readSession.Snapshot.Mode == WorkspaceReadMode.FamilyStore;
        long revision = ContextRevision(readSession.Snapshot, row.LastRevision ?? 0);
        string sourcePath = familyStore
            ? StoreSidecarCatalog.PathFor(readSession.FamilyStoreRoot!, StoreSidecarKind.Content, readSession.Snapshot.ViewId)
            : row.IndexDbPath;
        CachedTextContentSearch cached = familyStore
            ? GetOrLoadTextContentSearch(
                KeyFor(row.WorkspaceId, readSession.Snapshot),
                () => ContentCorpusSidecar.OpenStoreGenerationChecked(readSession.FamilyStoreRoot!, readSession.Snapshot))
            : GetOrLoadTextContentSearch(KeyFor(row.WorkspaceId, row.IndexDbPath, revision), row.IndexDbPath);
        return new WorkspaceTextContentSearchContext(
            cached.Index,
            sourcePath,
            row.WorkspaceId,
            row.CanonicalRoot,
            revision,
            familyStore ? null : WorkspaceFreshnessView.IndexFreshFor(refreshResult, row),
            WorkspaceFreshnessView.FreshnessStatusFor(refreshResult, row),
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
            CacheKey key = familyStore
                ? KeyFor(workspaceKey, readSession.Snapshot)
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

    private WorkspaceRegionSearchContext ResolveRegisteredRegionSearch(string workspaceId, bool ensureFresh)
    {
        RegisteredWorkspaceState state = ResolveRegisteredState(workspaceId, ensureFresh);
        WorkspaceRegistryRow row = state.Row;
        WorkspaceRefreshResult? refreshResult = state.RefreshResult;

        WorkspaceReadHandle readSession = OpenReadSession(row.IndexDbPath, row.CanonicalRoot, row.WorkspaceId);
        try
        {
            bool familyStore = readSession.Snapshot.Mode == WorkspaceReadMode.FamilyStore;
            long revision = ContextRevision(readSession.Snapshot, row.LastRevision ?? 0);
            CacheKey key = familyStore
                ? KeyFor(row.WorkspaceId, readSession.Snapshot)
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
                familyStore ? null : WorkspaceFreshnessView.IndexFreshFor(refreshResult, row),
                WorkspaceFreshnessView.FreshnessStatusFor(refreshResult, row),
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
    /// The refresh-first path behind every cross-workspace read, as a named seam so its backoff posture is
    /// directly testable rather than living in a constructor lambda a mutation could flip unobserved.
    ///
    /// <para><c>bypassBackoff</c> stays FALSE: this is the AUTOMATIC path, not a person asking.
    /// <c>ReadToolWorkspaceRouting.ResolveEnsureFresh</c> turns freshness on for any explicit
    /// <c>workspace_id</c>, so bypassing here would let ten cross-workspace searches against a workspace whose
    /// extractor is being OOM-killed spawn ten more extractor processes.</para>
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="refreshService"/> is null.</exception>
    internal static Func<string, WorkspaceRefreshResult> AutomaticRefresh(
        CrossWorkspaceRefreshService refreshService)
    {
        ArgumentNullException.ThrowIfNull(refreshService);
        return workspaceId => refreshService.Refresh(workspaceId);
    }

    private RegisteredWorkspaceState ResolveRegisteredState(string workspaceId, bool ensureFresh)
    {
        WorkspaceRegistryRow row = WorkspaceRegistrySelector.Resolve(_registry, workspaceId);
        VerifyRegisteredRoot(row);

        WorkspaceRefreshResult? refreshResult = null;
        if (ensureFresh)
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

        return new RegisteredWorkspaceState(row, refreshResult);
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
        lock (_cacheGate)
        {
            if (!_textContentSearchCache.TryGetValue(key, out lazy!))
            {
                lazy = new Lazy<CachedTextContentSearch>(
                    () => new CachedTextContentSearch(load()),
                    LazyThreadSafetyMode.ExecutionAndPublication);
                _textContentSearchCache[key] = lazy;
                EvictOtherEntriesForWorkspaceUnderLock(_textContentSearchCache, key);
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
                if (_textContentSearchCache.TryGetValue(key, out Lazy<CachedTextContentSearch>? cachedLazy) &&
                    ReferenceEquals(cachedLazy, lazy))
                    _textContentSearchCache.Remove(key);
            }
            throw;
        }
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

    private static CacheKey KeyFor(string? workspaceId, WorkspaceReadSnapshot snapshot)
    {
        WorkspaceFreshnessToken freshness = snapshot.Freshness;
        string resolvedWorkspaceId = string.IsNullOrWhiteSpace(workspaceId)
            ? snapshot.WorkspaceId ?? snapshot.WorkspaceRoot
            : workspaceId;
        string sourceIdentity = snapshot.Mode == WorkspaceReadMode.FamilyStore
            ? $"store:{freshness.StoreInstanceId ?? snapshot.ArtifactOrStoreId}:{freshness.ViewId ?? snapshot.ViewId}:{freshness.GenerationName ?? snapshot.GenerationName}:{freshness.ManifestGeneration ?? snapshot.ManifestGeneration}:{freshness.ManifestHash}:{freshness.StoreLogSequence}:{freshness.IndexLevel ?? snapshot.IndexLevel}:{freshness.LevelStampL1}:{freshness.LevelStampL2}:{freshness.LevelStampL3}:{freshness.ResolutionStamp}:{freshness.SearchStamp}:{freshness.ContentStamp}:{freshness.VectorStamp}"
            : snapshot.ArtifactOrStoreId;
        return new CacheKey(
            resolvedWorkspaceId,
            sourceIdentity,
            freshness.Revision,
            freshness.StoreLogSequence ?? 0,
            0,
            freshness.StoreInstanceId ?? freshness.ArtifactOrStoreId,
            ArtifactStampState.Present);
    }

    private static long ContextRevision(WorkspaceReadSnapshot snapshot, long legacyRevision) =>
        snapshot.Mode == WorkspaceReadMode.FamilyStore
            ? snapshot.Freshness.StoreLogSequence
                ?? throw new InvalidOperationException("The family-store snapshot has no store_log sequence.")
            : legacyRevision;

    private sealed record RegisteredWorkspaceState(WorkspaceRegistryRow Row, WorkspaceRefreshResult? RefreshResult);

    private sealed record CachedIndex(MillerRepositoryIndex Index, SmartTargetResolver Resolver);

    private sealed record CachedSymbolSearch(ISymbolLookupIndex Index, bool IsSidecar);

    private sealed record CachedSymbolRead(ISymbolLookupIndex Index);

    private sealed record CachedContentSearch(ContentSearchProjection Index);

    private sealed record CachedTextContentSearch(ITextContentSearchIndex Index);

    private sealed record CachedRegionSearch(IRegionSearchIndex Index);
}
