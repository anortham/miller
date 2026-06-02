using Miller.Indexing;
using Miller.Server.Resolution;
using Miller.Server.Tools;

namespace Miller.Server.Workspaces;

public sealed class WorkspaceIndexProvider : IWorkspaceIndexProvider, IWorkspaceSearchProvider
{
    private readonly IndexHolder _holder;
    private readonly WorkspaceContext _currentWorkspace;
    private readonly WorkspaceRegistry _registry;
    private readonly Func<string, WorkspaceRefreshResult> _refresh;
    private readonly Func<string, MillerRepositoryIndex> _loadIndex;
    private readonly Func<string, SymbolSearchProjection> _loadSymbolSearch;
    private readonly Func<long, bool?> _currentIndexFresh;
    private readonly object _cacheGate = new();
    private readonly Dictionary<CacheKey, Lazy<CachedIndex>> _cache = new();
    private readonly Dictionary<CacheKey, Lazy<CachedSymbolSearch>> _symbolSearchCache = new();

    public WorkspaceIndexProvider(
        IndexHolder holder,
        WorkspaceContext currentWorkspace,
        WorkspaceRegistry registry,
        CrossWorkspaceRefreshService refreshService)
        : this(
            holder,
            currentWorkspace,
            registry,
            workspaceId => refreshService.Refresh(workspaceId),
            dbPath => RepositoryIndexLoader.Load(dbPath),
            dbPath => SymbolSearchProjectionLoader.Load(dbPath),
            currentIndexFresh: _ => null)
    {
    }

    internal WorkspaceIndexProvider(
        IndexHolder holder,
        WorkspaceContext currentWorkspace,
        WorkspaceRegistry registry,
        Func<string, WorkspaceRefreshResult> refresh,
        Func<string, MillerRepositoryIndex> loadIndex,
        Func<string, SymbolSearchProjection> loadSymbolSearch,
        Func<long, bool?> currentIndexFresh)
    {
        ArgumentNullException.ThrowIfNull(holder);
        ArgumentNullException.ThrowIfNull(currentWorkspace);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(refresh);
        ArgumentNullException.ThrowIfNull(loadIndex);
        ArgumentNullException.ThrowIfNull(loadSymbolSearch);
        ArgumentNullException.ThrowIfNull(currentIndexFresh);
        _holder = holder;
        _currentWorkspace = currentWorkspace;
        _registry = registry;
        _refresh = refresh;
        _loadIndex = loadIndex;
        _loadSymbolSearch = loadSymbolSearch;
        _currentIndexFresh = currentIndexFresh;
    }

    public WorkspaceReadContext Resolve(string? workspaceId, bool ensureFresh)
    {
        if (workspaceId is null)
            return ResolveCurrent();

        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        if (string.Equals(workspaceId, _currentWorkspace.WorkspaceId, StringComparison.Ordinal))
            return ResolveCurrent();

        return ResolveRegistered(workspaceId, ensureFresh);
    }

    public WorkspaceSymbolSearchContext ResolveSymbolSearch(string? workspaceId, bool ensureFresh)
    {
        if (workspaceId is null)
            return ResolveCurrentSymbolSearch();

        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        if (string.Equals(workspaceId, _currentWorkspace.WorkspaceId, StringComparison.Ordinal))
            return ResolveCurrentSymbolSearch();

        return ResolveRegisteredSymbolSearch(workspaceId, ensureFresh);
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
        return new WorkspaceSymbolSearchContext(
            index,
            _currentWorkspace.CanonicalExtractDbPath ?? _currentWorkspace.ExtractDbPath,
            _currentWorkspace.WorkspaceId,
            _currentWorkspace.CanonicalRoot ?? _currentWorkspace.WorkspaceRoot,
            revision,
            _currentIndexFresh(revision),
            "current",
            WarningText: null,
            DisplayId: CurrentDisplayId());
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
        CachedSymbolSearch cached = GetOrLoadSymbolSearch(row, revision);
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

    private RegisteredWorkspaceState ResolveRegisteredState(string workspaceId, bool ensureFresh)
    {
        WorkspaceRegistryRow row = GetRequiredRow(workspaceId);
        VerifyRegisteredRoot(row);

        WorkspaceRefreshResult? refreshResult = null;
        if (ensureFresh)
        {
            refreshResult = _refresh(workspaceId);
            if (refreshResult.Status == WorkspaceRefreshStatus.MissingRoot)
                throw new DirectoryNotFoundException(refreshResult.Error ?? $"Workspace root not found: {row.CanonicalRoot}");
            if (refreshResult.Status == WorkspaceRefreshStatus.MissingIndex)
                throw new FileNotFoundException(
                    refreshResult.Error ?? $"Workspace index DB not found: {row.IndexDbPath}",
                    refreshResult.IndexDbPath);
            if (refreshResult.Status == WorkspaceRefreshStatus.Failed)
                throw new InvalidOperationException(
                    refreshResult.Error ?? $"Workspace '{workspaceId}' refresh failed.");

            row = GetRequiredRow(workspaceId);
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

    private CachedSymbolSearch GetOrLoadSymbolSearch(WorkspaceRegistryRow row, long revision)
    {
        var key = new CacheKey(row.WorkspaceId, row.IndexDbPath, revision);
        Lazy<CachedSymbolSearch> lazy;
        lock (_cacheGate)
        {
            if (!_symbolSearchCache.TryGetValue(key, out lazy!))
            {
                lazy = new Lazy<CachedSymbolSearch>(
                    () => new CachedSymbolSearch(_loadSymbolSearch(row.IndexDbPath)),
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

    private WorkspaceRegistryRow GetRequiredRow(string workspaceId) =>
        _registry.Get(workspaceId) ?? throw new KeyNotFoundException(
            $"Workspace registry row '{workspaceId}' was not found.");

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

    private readonly record struct CacheKey(string WorkspaceId, string IndexDbPath, long Revision);

    private sealed record RegisteredWorkspaceState(WorkspaceRegistryRow Row, WorkspaceRefreshResult? RefreshResult);

    private sealed record CachedIndex(MillerRepositoryIndex Index, SmartTargetResolver Resolver);

    private sealed record CachedSymbolSearch(SymbolSearchProjection Index);
}
