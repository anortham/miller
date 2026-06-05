using Miller.Indexing;
using Miller.Server.Resolution;
using Miller.Server.Tools;

namespace Miller.Server.Workspaces;

public sealed class WorkspaceIndexProvider
    : IWorkspaceIndexProvider, IWorkspaceSearchProvider, IWorkspaceContentSearchProvider, IWorkspaceRegionSearchProvider
{
    private readonly IndexHolder _holder;
    private readonly WorkspaceContext _currentWorkspace;
    private readonly WorkspaceRegistry _registry;
    private readonly Func<string, WorkspaceRefreshResult> _refresh;
    private readonly Func<string, MillerRepositoryIndex> _loadIndex;
    private readonly Func<string, SymbolSearchProjection> _loadSymbolSearch;
    private readonly Func<string, string, ContentSearchProjection> _loadContentSearch;
    private readonly Func<string, long, IRegionSearchIndex> _loadRegionSearch;
    private readonly Func<long, bool?> _currentIndexFresh;
    private readonly SymbolSearchSidecar _sidecar;
    private readonly object _cacheGate = new();
    private readonly Dictionary<CacheKey, Lazy<CachedIndex>> _cache = new();
    private readonly Dictionary<CacheKey, Lazy<CachedSymbolSearch>> _symbolSearchCache = new();
    private readonly Dictionary<CacheKey, Lazy<CachedContentSearch>> _contentSearchCache = new();
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
            workspaceId => refreshService.Refresh(workspaceId),
            dbPath => RepositoryIndexLoader.Load(dbPath),
            dbPath => SymbolSearchProjectionLoader.Load(dbPath),
            (dbPath, root) => ContentSearchProjectionLoader.Load(dbPath, root),
            (dbPath, revision) => FtsRegionSearchIndex.Open(SymbolSearchSidecar.SearchDbPathFor(dbPath), revision),
            currentIndexFresh: _ => null,
            sidecar)
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
        Func<string, long, IRegionSearchIndex> loadRegionSearch,
        Func<long, bool?> currentIndexFresh,
        SymbolSearchSidecar sidecar)
    {
        ArgumentNullException.ThrowIfNull(holder);
        ArgumentNullException.ThrowIfNull(currentWorkspace);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(refresh);
        ArgumentNullException.ThrowIfNull(loadIndex);
        ArgumentNullException.ThrowIfNull(loadSymbolSearch);
        ArgumentNullException.ThrowIfNull(loadContentSearch);
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
        _loadRegionSearch = loadRegionSearch;
        _currentIndexFresh = currentIndexFresh;
        _sidecar = sidecar;
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

    public WorkspaceSymbolSearchContext ResolveSymbolSearch(string? workspaceId, bool ensureFresh)
    {
        if (workspaceId is null)
            return ResolveCurrentSymbolSearch();

        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        if (SelectorTargetsCurrent(workspaceId))
            return ResolveCurrentSymbolSearch();

        return ResolveRegisteredSymbolSearch(workspaceId, ensureFresh);
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

    private WorkspaceReadContext ResolveCurrent()
    {
        (MillerRepositoryIndex index, long revision) = _holder.Snapshot();
        var resolver = new SmartTargetResolver(index);
        return new WorkspaceReadContext(
            index,
            resolver,
            _currentWorkspace.CanonicalExtractDbPath ?? _currentWorkspace.ExtractDbPath,
            _currentWorkspace.WorkspaceId,
            _currentWorkspace.CanonicalRoot ?? _currentWorkspace.WorkspaceRoot,
            revision,
            _currentIndexFresh(revision),
            "current",
            WarningText: null,
            DisplayId: CurrentDisplayId());
    }

    private WorkspaceSymbolSearchContext ResolveCurrentSymbolSearch()
    {
        (MillerRepositoryIndex index, long revision) = _holder.Snapshot();
        ISymbolLookupIndex searchIndex = ResolveCurrentSymbolSearchIndex(index, revision);
        return new WorkspaceSymbolSearchContext(
            searchIndex,
            _currentWorkspace.CanonicalExtractDbPath ?? _currentWorkspace.ExtractDbPath,
            _currentWorkspace.WorkspaceId,
            _currentWorkspace.CanonicalRoot ?? _currentWorkspace.WorkspaceRoot,
            revision,
            _currentIndexFresh(revision),
            "current",
            WarningText: null,
            DisplayId: CurrentDisplayId());
    }

    // Phase 3 routing for the current workspace. Default (flag off): the holder's already-built full index
    // serves search, byte-identical to pre-Phase-3. Flag on: route to the revision-fresh on-disk sidecar when
    // present, else self-heal to the holder index. The chosen backend is cached keyed on (workspace, dbPath,
    // revision) so a freshness Swap (revision bump) rebuilds it and the sidecar is not re-opened per query.
    private ISymbolLookupIndex ResolveCurrentSymbolSearchIndex(MillerRepositoryIndex holderIndex, long revision)
    {
        if (!_sidecar.Enabled)
            return holderIndex;

        string dbPath = _currentWorkspace.CanonicalExtractDbPath ?? _currentWorkspace.ExtractDbPath;
        string workspaceKey = string.IsNullOrEmpty(_currentWorkspace.WorkspaceId) ? dbPath : _currentWorkspace.WorkspaceId;
        var key = new CacheKey(workspaceKey, dbPath, revision);
        return GetOrLoadSymbolSearch(key, dbPath, () => holderIndex).Index;
    }

    private WorkspaceReadContext ResolveRegistered(string workspaceId, bool ensureFresh)
    {
        RegisteredWorkspaceState state = ResolveRegisteredState(workspaceId, ensureFresh);
        WorkspaceRegistryRow row = state.Row;
        WorkspaceRefreshResult? refreshResult = state.RefreshResult;

        long revision = row.LastRevision ?? 0;
        CachedIndex cached = GetOrLoad(row, revision);
        return new WorkspaceReadContext(
            cached.Index,
            cached.Resolver,
            row.IndexDbPath,
            row.WorkspaceId,
            row.CanonicalRoot,
            revision,
            WorkspaceFreshnessView.IndexFreshFor(refreshResult, row),
            WorkspaceFreshnessView.FreshnessStatusFor(refreshResult, row),
            WorkspaceFreshnessView.WarningTextFor(refreshResult),
            row.DisplayId);
    }

    private WorkspaceSymbolSearchContext ResolveRegisteredSymbolSearch(string workspaceId, bool ensureFresh)
    {
        RegisteredWorkspaceState state = ResolveRegisteredState(workspaceId, ensureFresh);
        WorkspaceRegistryRow row = state.Row;
        WorkspaceRefreshResult? refreshResult = state.RefreshResult;

        long revision = row.LastRevision ?? 0;
        var key = new CacheKey(row.WorkspaceId, row.IndexDbPath, revision);
        CachedSymbolSearch cached = GetOrLoadSymbolSearch(key, row.IndexDbPath, () => _loadSymbolSearch(row.IndexDbPath));
        return new WorkspaceSymbolSearchContext(
            cached.Index,
            row.IndexDbPath,
            row.WorkspaceId,
            row.CanonicalRoot,
            revision,
            WorkspaceFreshnessView.IndexFreshFor(refreshResult, row),
            WorkspaceFreshnessView.FreshnessStatusFor(refreshResult, row),
            WorkspaceFreshnessView.WarningTextFor(refreshResult),
            row.DisplayId);
    }

    private WorkspaceContentSearchContext ResolveCurrentContentSearch()
    {
        // No content index lives in the holder (the bootstrap seeds only the full repository index), so the
        // current workspace builds its content projection lazily on first content query and caches it keyed on
        // the holder's built revision — a freshness Swap (reindex) bumps the revision and rebuilds.
        (_, long revision) = _holder.Snapshot();
        string dbPath = _currentWorkspace.CanonicalExtractDbPath ?? _currentWorkspace.ExtractDbPath;
        string root = _currentWorkspace.CanonicalRoot ?? _currentWorkspace.WorkspaceRoot;
        string workspaceKey = string.IsNullOrEmpty(_currentWorkspace.WorkspaceId) ? dbPath : _currentWorkspace.WorkspaceId;
        CachedContentSearch cached = GetOrLoadContentSearch(workspaceKey, dbPath, root, revision);
        return new WorkspaceContentSearchContext(
            cached.Index,
            dbPath,
            _currentWorkspace.WorkspaceId,
            root,
            revision,
            _currentIndexFresh(revision),
            "current",
            WarningText: null,
            DisplayId: CurrentDisplayId());
    }

    private WorkspaceContentSearchContext ResolveRegisteredContentSearch(string workspaceId, bool ensureFresh)
    {
        RegisteredWorkspaceState state = ResolveRegisteredState(workspaceId, ensureFresh);
        WorkspaceRegistryRow row = state.Row;
        WorkspaceRefreshResult? refreshResult = state.RefreshResult;

        long revision = row.LastRevision ?? 0;
        CachedContentSearch cached = GetOrLoadContentSearch(row.WorkspaceId, row.IndexDbPath, row.CanonicalRoot, revision);
        return new WorkspaceContentSearchContext(
            cached.Index,
            row.IndexDbPath,
            row.WorkspaceId,
            row.CanonicalRoot,
            revision,
            WorkspaceFreshnessView.IndexFreshFor(refreshResult, row),
            WorkspaceFreshnessView.FreshnessStatusFor(refreshResult, row),
            WorkspaceFreshnessView.WarningTextFor(refreshResult),
            row.DisplayId);
    }

    private WorkspaceRegionSearchContext ResolveCurrentRegionSearch()
    {
        (_, long revision) = _holder.Snapshot();
        string dbPath = _currentWorkspace.CanonicalExtractDbPath ?? _currentWorkspace.ExtractDbPath;
        string root = _currentWorkspace.CanonicalRoot ?? _currentWorkspace.WorkspaceRoot;
        string workspaceKey = string.IsNullOrEmpty(_currentWorkspace.WorkspaceId) ? dbPath : _currentWorkspace.WorkspaceId;
        CachedRegionSearch cached = GetOrLoadRegionSearch(new CacheKey(workspaceKey, dbPath, revision), dbPath);
        return new WorkspaceRegionSearchContext(
            cached.Index,
            dbPath,
            _currentWorkspace.WorkspaceId,
            root,
            revision,
            _currentIndexFresh(revision),
            "current",
            WarningText: null,
            DisplayId: CurrentDisplayId());
    }

    private WorkspaceRegionSearchContext ResolveRegisteredRegionSearch(string workspaceId, bool ensureFresh)
    {
        RegisteredWorkspaceState state = ResolveRegisteredState(workspaceId, ensureFresh);
        WorkspaceRegistryRow row = state.Row;
        WorkspaceRefreshResult? refreshResult = state.RefreshResult;

        long revision = row.LastRevision ?? 0;
        CachedRegionSearch cached = GetOrLoadRegionSearch(new CacheKey(row.WorkspaceId, row.IndexDbPath, revision), row.IndexDbPath);
        return new WorkspaceRegionSearchContext(
            cached.Index,
            row.IndexDbPath,
            row.WorkspaceId,
            row.CanonicalRoot,
            revision,
            WorkspaceFreshnessView.IndexFreshFor(refreshResult, row),
            WorkspaceFreshnessView.FreshnessStatusFor(refreshResult, row),
            WorkspaceFreshnessView.WarningTextFor(refreshResult),
            row.DisplayId);
    }

    private RegisteredWorkspaceState ResolveRegisteredState(string workspaceId, bool ensureFresh)
    {
        WorkspaceRegistryRow row = WorkspaceRegistrySelector.Resolve(_registry, workspaceId);
        VerifyRegisteredRoot(row);

        WorkspaceRefreshResult? refreshResult = null;
        if (ensureFresh)
        {
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
        }

        return new RegisteredWorkspaceState(row, refreshResult);
    }

    private CachedIndex GetOrLoad(WorkspaceRegistryRow row, long revision)
    {
        var key = new CacheKey(row.WorkspaceId, row.IndexDbPath, revision);
        Lazy<CachedIndex> lazy;
        lock (_cacheGate)
        {
            if (!_cache.TryGetValue(key, out lazy!))
            {
                lazy = new Lazy<CachedIndex>(
                    () =>
                    {
                        MillerRepositoryIndex index = _loadIndex(row.IndexDbPath);
                        return new CachedIndex(index, new SmartTargetResolver(index));
                    },
                    LazyThreadSafetyMode.ExecutionAndPublication);
                _cache[key] = lazy;
                EvictOtherEntriesForWorkspaceUnderLock(_cache, key);
            }
        }

        try
        {
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

    // Resolve the search index for a cache key: the on-disk sidecar when enabled + present + revision-fresh
    // (Phase 3), else the in-memory backend from <paramref name="loadInMemory"/> (the lean projection for a
    // registered workspace; the holder's full index for the current one). Single-flight + revision-keyed so a
    // miss loads once and a revision bump evicts the prior entry.
    private CachedSymbolSearch GetOrLoadSymbolSearch(
        CacheKey key, string symbolsDbPath, Func<ISymbolLookupIndex> loadInMemory)
    {
        Lazy<CachedSymbolSearch> lazy;
        lock (_cacheGate)
        {
            if (!_symbolSearchCache.TryGetValue(key, out lazy!))
            {
                lazy = new Lazy<CachedSymbolSearch>(
                    () => new CachedSymbolSearch(
                        _sidecar.TryOpen(symbolsDbPath, key.Revision) ?? loadInMemory()),
                    LazyThreadSafetyMode.ExecutionAndPublication);
                _symbolSearchCache[key] = lazy;
                EvictOtherEntriesForWorkspaceUnderLock(_symbolSearchCache, key);
            }
        }

        try
        {
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

    private CachedContentSearch GetOrLoadContentSearch(string workspaceId, string dbPath, string root, long revision)
    {
        var key = new CacheKey(workspaceId, dbPath, revision);
        Lazy<CachedContentSearch> lazy;
        lock (_cacheGate)
        {
            if (!_contentSearchCache.TryGetValue(key, out lazy!))
            {
                lazy = new Lazy<CachedContentSearch>(
                    () => new CachedContentSearch(_loadContentSearch(dbPath, root)),
                    LazyThreadSafetyMode.ExecutionAndPublication);
                _contentSearchCache[key] = lazy;
                EvictOtherEntriesForWorkspaceUnderLock(_contentSearchCache, key);
            }
        }

        try
        {
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
        EnsureRegionSearchEnabled();

        Lazy<CachedRegionSearch> lazy;
        lock (_cacheGate)
        {
            if (!_regionSearchCache.TryGetValue(key, out lazy!))
            {
                lazy = new Lazy<CachedRegionSearch>(
                    () => new CachedRegionSearch(OpenRegionSearch(dbPath, key.Revision)),
                    LazyThreadSafetyMode.ExecutionAndPublication);
                _regionSearchCache[key] = lazy;
                EvictOtherEntriesForWorkspaceUnderLock(_regionSearchCache, key);
            }
        }

        try
        {
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

    private void EnsureRegionSearchEnabled()
    {
        if (!_sidecar.Enabled)
        {
            throw new InvalidOperationException(
                "region search requires the search sidecar. Set MILLER_SEARCH_SIDECAR=1 and " +
                "MILLER_REGION_INDEX=1, then refresh the workspace.");
        }
        if (!_sidecar.RegionOptions.Enabled)
        {
            throw new InvalidOperationException(
                "region search requires MILLER_REGION_INDEX=1 and a refreshed search sidecar.");
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
                "region search requires MILLER_REGION_INDEX=1 and a refreshed search sidecar: " + ex.Message,
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

    private readonly record struct CacheKey(string WorkspaceId, string IndexDbPath, long Revision);

    private sealed record RegisteredWorkspaceState(WorkspaceRegistryRow Row, WorkspaceRefreshResult? RefreshResult);

    private sealed record CachedIndex(MillerRepositoryIndex Index, SmartTargetResolver Resolver);

    private sealed record CachedSymbolSearch(ISymbolLookupIndex Index);

    private sealed record CachedContentSearch(ContentSearchProjection Index);

    private sealed record CachedRegionSearch(IRegionSearchIndex Index);
}
