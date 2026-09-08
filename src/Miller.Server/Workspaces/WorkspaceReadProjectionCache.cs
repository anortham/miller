using Miller.Core.Graph;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Server.Telemetry;

namespace Miller.Server.Workspaces;

public readonly record struct WorkspaceReadProjectionKey(
    string WorkspaceId,
    string StoreGeneration,
    string ViewId,
    long Revision,
    string ProjectionType)
{
    public static WorkspaceReadProjectionKey ForSymbol(
        string? workspaceId,
        WorkspaceReadSnapshot snapshot) =>
        new(
            workspaceId ?? string.Empty,
            snapshot.IndexIdentity + ":" + snapshot.IndexLevel,
            snapshot.ViewId ?? string.Empty,
            snapshot.Freshness.Revision,
            "symbol");

    public static WorkspaceReadProjectionKey ForBridge(
        string? workspaceId,
        WorkspaceReadSnapshot snapshot) =>
        new(
            workspaceId ?? string.Empty,
            snapshot.IndexIdentity + ":" + snapshot.IndexLevel,
            snapshot.ViewId ?? string.Empty,
            snapshot.Freshness.Revision,
            "bridge");
}

/// <summary>
/// Thread-safe, bounded cache for immutable symbol and bridge graph projections.
/// Coalesces concurrent readers across requests for the same workspace revision,
/// immediately evicts failed loads, purges superseded revisions, and evicts removed workspaces.
/// </summary>
public sealed class WorkspaceReadProjectionCache : IDisposable
{
    private const int MaxEntries = 64;
    private readonly object _gate = new();
    private readonly Dictionary<WorkspaceReadProjectionKey, Lazy<Projection>> _cache = new();
    private readonly LinkedList<WorkspaceReadProjectionKey> _lru = new();
    private readonly Dictionary<WorkspaceReadProjectionKey, DateTimeOffset> _accessed = new();
    private readonly Dictionary<WorkspaceReadProjectionKey, long> _sizes = new();
    private readonly TimeProvider _clock;
    private readonly long _maxEstimatedBytes;
    private readonly TimeSpan _idleLifetime;
    private readonly ITimer _expiryTimer;
    private long _estimatedRetainedBytes;
    private bool _disposed;

    private sealed record Projection(object Value, long EstimatedBytes);

    public WorkspaceReadProjectionCache(TimeProvider? clock = null,
        long maxEstimatedBytes = 512L * 1024 * 1024, TimeSpan? idleLifetime = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEstimatedBytes);
        _clock = clock ?? TimeProvider.System;
        _maxEstimatedBytes = maxEstimatedBytes;
        _idleLifetime = idleLifetime ?? TimeSpan.FromMinutes(5);
        if (_idleLifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(idleLifetime));
        _expiryTimer = _clock.CreateTimer(_ => Expire(), null, _idleLifetime, _idleLifetime);
    }

    public int Count { get { lock (_gate) return _cache.Count; } }

    /// <summary>Current estimate from retained projection structures, not an exact CLR heap measurement.</summary>
    public long EstimatedRetainedBytes { get { lock (_gate) return _estimatedRetainedBytes; } }

    public ISymbolLookupIndex GetOrAddSymbolIndex(WorkspaceReadProjectionKey key, Func<ISymbolLookupIndex> load) =>
        GetOrAdd(key, load);

    public BridgeGraph GetOrAddBridgeGraph(WorkspaceReadProjectionKey key, Func<BridgeGraph> load) =>
        GetOrAdd(key, load);

    public T GetOrAdd<T>(WorkspaceReadProjectionKey key, Func<T> load) where T : class
    {
        ArgumentNullException.ThrowIfNull(load);
        Lazy<Projection> lazy;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ExpireLocked();
            if (!_cache.TryGetValue(key, out lazy!))
            {
                foreach (var existing in _cache.Keys.Where(existing =>
                    existing.WorkspaceId == key.WorkspaceId && existing.ProjectionType == key.ProjectionType &&
                    (existing.StoreGeneration != key.StoreGeneration || existing.ViewId != key.ViewId ||
                        existing.Revision < key.Revision)).ToArray())
                    RemoveLocked(existing);
                while (_cache.Count >= MaxEntries && _lru.First is { } oldest)
                    RemoveLocked(oldest.Value);
                lazy = new Lazy<Projection>(() =>
                {
                    T value = load();
                    long size = value switch
                    {
                        SymbolSearchProjection symbols => symbols.EstimatedRetainedBytes,
                        BridgeGraph bridge => bridge.EstimatedRetainedBytes,
                        ISymbolLookupIndex symbols => 1024L + 2048L * symbols.DocumentCount,
                        _ => 4096L
                    };
                    return new Projection(value, size);
                }, LazyThreadSafetyMode.ExecutionAndPublication);
                _cache.Add(key, lazy);
            }
            TouchLocked(key);
        }
        try
        {
            if (!lazy.IsValueCreated)
                TelemetryContext.Current?.SetWaitReason("projection_load");
            Projection projection = lazy.Value;
            lock (_gate)
            {
                if (_cache.TryGetValue(key, out var current) && ReferenceEquals(current, lazy) && !_sizes.ContainsKey(key))
                {
                    _sizes.Add(key, projection.EstimatedBytes);
                    _estimatedRetainedBytes += projection.EstimatedBytes;
                    while (_estimatedRetainedBytes > _maxEstimatedBytes && _lru.First is { } oldest)
                        RemoveLocked(oldest.Value);
                }
            }
            return (T)projection.Value;
        }
        catch
        {
            lock (_gate)
                if (_cache.TryGetValue(key, out var current) && ReferenceEquals(current, lazy))
                    RemoveLocked(key);
            throw;
        }
    }

    public void EvictWorkspace(string workspaceId)
    {
        lock (_gate)
            foreach (var key in _cache.Keys.Where(key => key.WorkspaceId == workspaceId).ToArray())
                RemoveLocked(key);
    }

    public void Evict(WorkspaceReadProjectionKey key) { lock (_gate) RemoveLocked(key); }

    public void Clear()
    {
        lock (_gate)
        {
            _cache.Clear();
            _lru.Clear();
            _accessed.Clear();
            _sizes.Clear();
            _estimatedRetainedBytes = 0;
        }
    }

    public void Dispose()
    {
        _expiryTimer.Dispose();
        lock (_gate)
        {
            _disposed = true;
            Clear();
        }
    }

    private void Expire() { lock (_gate) ExpireLocked(); }

    private void ExpireLocked()
    {
        DateTimeOffset cutoff = _clock.GetUtcNow() - _idleLifetime;
        foreach (var key in _accessed.Where(entry => entry.Value <= cutoff).Select(entry => entry.Key).ToArray())
            RemoveLocked(key);
    }

    private void TouchLocked(WorkspaceReadProjectionKey key)
    {
        _accessed[key] = _clock.GetUtcNow();
        _lru.Remove(key);
        _lru.AddLast(key);
    }

    private void RemoveLocked(WorkspaceReadProjectionKey key)
    {
        _cache.Remove(key);
        _lru.Remove(key);
        _accessed.Remove(key);
        if (_sizes.Remove(key, out long size))
            _estimatedRetainedBytes -= size;
    }
}
