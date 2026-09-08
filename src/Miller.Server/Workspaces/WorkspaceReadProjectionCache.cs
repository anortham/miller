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
            snapshot.GenerationName ?? snapshot.ArtifactOrStoreId ?? string.Empty,
            snapshot.ViewId ?? string.Empty,
            snapshot.Freshness.Revision,
            "symbol");

    public static WorkspaceReadProjectionKey ForBridge(
        string? workspaceId,
        WorkspaceReadSnapshot snapshot) =>
        new(
            workspaceId ?? string.Empty,
            snapshot.GenerationName ?? snapshot.ArtifactOrStoreId ?? string.Empty,
            snapshot.ViewId ?? string.Empty,
            snapshot.Freshness.Revision,
            "bridge");
}

/// <summary>
/// Thread-safe, bounded cache for immutable symbol and bridge graph projections.
/// Coalesces concurrent readers across requests for the same workspace revision,
/// immediately evicts failed loads, purges superseded revisions, and evicts removed workspaces.
/// </summary>
public sealed class WorkspaceReadProjectionCache
{
    private const int MaxEntries = 64;
    private readonly object _gate = new();
    private readonly Dictionary<WorkspaceReadProjectionKey, Lazy<object>> _cache = new();
    private readonly LinkedList<WorkspaceReadProjectionKey> _lru = new();
    private readonly Dictionary<WorkspaceReadProjectionKey, LinkedListNode<WorkspaceReadProjectionKey>> _lruNodes = new();

    public int Count
    {
        get
        {
            lock (_gate)
                return _cache.Count;
        }
    }

    public ISymbolLookupIndex GetOrAddSymbolIndex(
        WorkspaceReadProjectionKey key,
        Func<ISymbolLookupIndex> load) =>
        GetOrAdd(key, load);

    public BridgeGraph GetOrAddBridgeGraph(
        WorkspaceReadProjectionKey key,
        Func<BridgeGraph> load) =>
        GetOrAdd(key, load);

    public T GetOrAdd<T>(
        WorkspaceReadProjectionKey key,
        Func<T> load) where T : class
    {
        ArgumentNullException.ThrowIfNull(load);
        Lazy<object> lazy;
        lock (_gate)
        {
            if (!_cache.TryGetValue(key, out lazy!))
            {
                // Release superseded entries for this workspace and projection type
                var superseded = _cache.Keys
                    .Where(existing =>
                        string.Equals(existing.WorkspaceId, key.WorkspaceId, StringComparison.Ordinal) &&
                        string.Equals(existing.ProjectionType, key.ProjectionType, StringComparison.Ordinal) &&
                        (!string.Equals(existing.StoreGeneration, key.StoreGeneration, StringComparison.Ordinal) ||
                         !string.Equals(existing.ViewId, key.ViewId, StringComparison.Ordinal) ||
                         existing.Revision < key.Revision))
                    .ToArray();

                foreach (var oldKey in superseded)
                {
                    RemoveLocked(oldKey);
                }

                // Bound entry count
                while (_cache.Count >= MaxEntries && _lru.First is not null)
                {
                    RemoveLocked(_lru.First.Value);
                }

                lazy = new Lazy<object>(() => load(), LazyThreadSafetyMode.ExecutionAndPublication);
                _cache[key] = lazy;
                var node = _lru.AddLast(key);
                _lruNodes[key] = node;
            }
            else
            {
                // Refresh LRU position
                if (_lruNodes.TryGetValue(key, out var existingNode))
                {
                    _lru.Remove(existingNode);
                    _lru.AddLast(existingNode);
                }
            }
        }

        try
        {
            if (!lazy.IsValueCreated)
                TelemetryContext.Current?.SetWaitReason("projection_load");
            return (T)lazy.Value;
        }
        catch
        {
            // A pending failed load is not a successful cached value: purge on failure so future callers retry
            lock (_gate)
            {
                if (_cache.TryGetValue(key, out var cachedLazy) && ReferenceEquals(cachedLazy, lazy))
                {
                    RemoveLocked(key);
                }
            }
            throw;
        }
    }

    public void EvictWorkspace(string workspaceId)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
            return;

        lock (_gate)
        {
            var matching = _cache.Keys
                .Where(k => string.Equals(k.WorkspaceId, workspaceId, StringComparison.Ordinal))
                .ToArray();
            foreach (var k in matching)
            {
                RemoveLocked(k);
            }
        }
    }

    public void Evict(WorkspaceReadProjectionKey key)
    {
        lock (_gate)
        {
            RemoveLocked(key);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _cache.Clear();
            _lru.Clear();
            _lruNodes.Clear();
        }
    }

    private void RemoveLocked(WorkspaceReadProjectionKey key)
    {
        _cache.Remove(key);
        if (_lruNodes.Remove(key, out var node))
        {
            _lru.Remove(node);
        }
    }
}
