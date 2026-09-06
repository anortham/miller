using Microsoft.Data.Sqlite;
using Miller.Indexing.Reads;

namespace Miller.Indexing.Resolution;

public sealed class RevisionFactCacheStore
{
    internal const long DefaultByteBudget = 256L * 1024 * 1024;

    private readonly object _gate = new();
    private readonly Dictionary<string, ScopeEntry> _scopes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _warms = new(StringComparer.Ordinal);
    private readonly Dictionary<RevisionFactCache, int> _activeCacheRefCounts = new(ReferenceEqualityComparer.Instance);
    private readonly long _byteBudget;
    private long _clock;
    private long _entryTokenCounter;
    private int _loadCount;
    private int _coalescedLoadCount;
    private int _activeLeaseCount;

    public RevisionFactCacheStore()
        : this(DefaultByteBudget)
    {
    }

    internal RevisionFactCacheStore(long byteBudget)
    {
        _byteBudget = byteBudget;
    }

    internal int ScopeCount
    {
        get
        {
            lock (_gate)
                return _scopes.Count;
        }
    }

    internal long ResidentBytes
    {
        get
        {
            lock (_gate)
            {
                long total = 0;
                foreach (ScopeEntry entry in _scopes.Values)
                {
                    if (entry.Lazy.IsValueCreated)
                        total += entry.Lazy.Value.ResidentBytes;
                }

                return total;
            }
        }
    }

    internal CacheResourceSnapshot GetResourceSnapshot()
    {
        lock (_gate)
        {
            var retained = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var active = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var objectBytes = new Dictionary<object, long>(ReferenceEqualityComparer.Instance);

            foreach (ScopeEntry entry in _scopes.Values)
            {
                if (entry.Lazy.IsValueCreated)
                {
                    RevisionFactCache cache = entry.Lazy.Value;
                    retained.Add(cache);
                    objectBytes[cache] = cache.ResidentBytes;
                }
            }

            foreach (RevisionFactCache activeCache in _activeCacheRefCounts.Keys)
            {
                active.Add(activeCache);
                objectBytes[activeCache] = activeCache.ResidentBytes;
            }

            var state = new CacheResourceState(retained, active, objectBytes);
            return state.ToSnapshot(
                activeLeaseCount: _activeLeaseCount,
                loadCount: _loadCount,
                coalescedLoadCount: _coalescedLoadCount,
                byteBudget: _byteBudget);
        }
    }

    /// <summary>
    /// Whether a read for this scope would be answered without a whole-generation load: a cache is already
    /// loaded for the same identity, or a loaded cache can advance to the new identity as a bounded delta.
    /// A load that is merely in flight reports cold — the caller must not block behind it. The probe is
    /// advisory: the entry can be replaced between this answer and the caller's read, in which case that one
    /// read blocks on the load exactly as it did before the probe existed — a latency fallback, never a
    /// correctness hazard.
    /// </summary>
    internal bool IsWarm(string workspaceScope, string revisionIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceScope);
        ArgumentException.ThrowIfNullOrWhiteSpace(revisionIdentity);
        lock (_gate)
        {
            if (!_scopes.TryGetValue(workspaceScope, out ScopeEntry? entry) || !entry.Lazy.IsValueCreated)
                return false;
            return string.Equals(entry.Identity, revisionIdentity, StringComparison.Ordinal)
                || entry.Lazy.Value.CanAdvance;
        }
    }

    /// <summary>
    /// Load (or advance) this scope's cache off the calling thread, single-flight per scope: concurrent cold
    /// callers share ONE task instead of each parking a thread-pool worker on the lazy, and a warm scope
    /// spawns nothing. A faulted warm clears itself, so the next probe retries. Best-effort by design — a
    /// newer identity arriving mid-load is picked up by the next probe, never by a second concurrent load.
    /// </summary>
    internal Task WarmInBackground(
        string workspaceScope,
        string revisionIdentity,
        Func<SqliteConnection> openRead,
        StoreVisibility visibility)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceScope);
        ArgumentException.ThrowIfNullOrWhiteSpace(revisionIdentity);
        ArgumentNullException.ThrowIfNull(openRead);
        ArgumentNullException.ThrowIfNull(visibility);

        lock (_gate)
        {
            if (IsWarm(workspaceScope, revisionIdentity))
                return Task.CompletedTask;
            if (_warms.TryGetValue(workspaceScope, out Task? inflight))
            {
                _coalescedLoadCount++;
                return inflight;
            }

            // Removal in the finally needs no identity check: entries are inserted only here, and the
            // single-flight guard above blocks a second insert for the scope until this one removes itself.
            Task warm = Task.Run(() =>
            {
                try
                {
                    using RevisionFactCacheLease lease = Acquire(workspaceScope, revisionIdentity, openRead, visibility);
                }
                finally
                {
                    lock (_gate)
                        _warms.Remove(workspaceScope);
                }
            });
            _warms[workspaceScope] = warm;
            return warm;
        }
    }

    internal RevisionFactCacheLease Acquire(
        string workspaceScope,
        string revisionIdentity,
        Func<SqliteConnection> openRead,
        StoreVisibility visibility)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceScope);
        ArgumentException.ThrowIfNullOrWhiteSpace(revisionIdentity);
        ArgumentNullException.ThrowIfNull(openRead);
        ArgumentNullException.ThrowIfNull(visibility);

        Lazy<RevisionFactCache> lazy;
        long entryToken;
        lock (_gate)
        {
            if (_scopes.TryGetValue(workspaceScope, out ScopeEntry? existing)
                && string.Equals(existing.Identity, revisionIdentity, StringComparison.Ordinal))
            {
                existing.LastUsed = ++_clock;
                lazy = existing.Lazy;
                entryToken = existing.Token;
                if (!lazy.IsValueCreated)
                    _coalescedLoadCount++;
            }
            else
            {
                RevisionFactCache? previous = existing is { Lazy.IsValueCreated: true } ? existing.Lazy.Value : null;
                entryToken = ++_entryTokenCounter;
                lazy = new Lazy<RevisionFactCache>(
                    () =>
                    {
                        Interlocked.Increment(ref _loadCount);
                        SqliteConnection connection = openRead();
                        RevisionFactCache loaded;
                        try
                        {
                            loaded = previous is { CanAdvance: true }
                                ? previous.Advance(connection, visibility)
                                : RevisionFactCache.Load(connection, visibility);
                        }
                        catch
                        {
                            try { connection.Dispose(); } catch { /* Preserve the primary query failure. */ }
                            throw;
                        }
                        connection.Dispose();
                        return loaded;
                    },
                    LazyThreadSafetyMode.ExecutionAndPublication);
                _scopes[workspaceScope] = new ScopeEntry(revisionIdentity, lazy, ++_clock, entryToken);
            }
        }

        RevisionFactCache cache;
        try
        {
            cache = lazy.Value;
        }
        catch
        {
            lock (_gate)
            {
                if (_scopes.TryGetValue(workspaceScope, out ScopeEntry? current)
                    && current.Token == entryToken)
                {
                    _scopes.Remove(workspaceScope);
                }
            }

            throw;
        }

        lock (_gate)
        {
            if (_scopes.TryGetValue(workspaceScope, out ScopeEntry? current)
                && current.Token == entryToken)
            {
                current.LastUsed = ++_clock;
                EvictToBudget(workspaceScope);
            }

            _activeLeaseCount++;
            if (_activeCacheRefCounts.TryGetValue(cache, out int count))
                _activeCacheRefCounts[cache] = count + 1;
            else
                _activeCacheRefCounts[cache] = 1;
        }

        return new RevisionFactCacheLease(cache, workspaceScope, revisionIdentity, ReleaseLease);
    }

    private void ReleaseLease(RevisionFactCacheLease lease)
    {
        lock (_gate)
        {
            _activeLeaseCount--;
            if (_activeCacheRefCounts.TryGetValue(lease.Cache, out int count))
            {
                if (count <= 1)
                    _activeCacheRefCounts.Remove(lease.Cache);
                else
                    _activeCacheRefCounts[lease.Cache] = count - 1;
            }
        }
    }

    private void EvictToBudget(string keepScope)
    {
        long total = 0;
        foreach (ScopeEntry entry in _scopes.Values)
        {
            if (entry.Lazy.IsValueCreated)
                total += entry.Lazy.Value.ResidentBytes;
        }

        if (total <= _byteBudget)
            return;

        foreach (string scope in _scopes
                     .Where(pair => !string.Equals(pair.Key, keepScope, StringComparison.Ordinal))
                     .OrderBy(pair => pair.Value.LastUsed)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            if (total <= _byteBudget)
                break;
            if (_scopes.TryGetValue(scope, out ScopeEntry? entry) && entry.Lazy.IsValueCreated)
                total -= entry.Lazy.Value.ResidentBytes;
            _scopes.Remove(scope);
        }
    }

    private sealed class ScopeEntry
    {
        internal ScopeEntry(string identity, Lazy<RevisionFactCache> lazy, long lastUsed, long token)
        {
            Identity = identity;
            Lazy = lazy;
            LastUsed = lastUsed;
            Token = token;
        }

        internal string Identity { get; }

        internal Lazy<RevisionFactCache> Lazy { get; }

        internal long LastUsed { get; set; }

        internal long Token { get; }
    }
}
