using Microsoft.Data.Sqlite;
using Miller.Indexing.Reads;

namespace Miller.Indexing.Resolution;

internal sealed class RevisionFactCacheStore
{
    internal const long DefaultByteBudget = 256L * 1024 * 1024;

    private readonly object _gate = new();
    private readonly Dictionary<string, ScopeEntry> _scopes = new(StringComparer.Ordinal);
    private readonly long _byteBudget;
    private long _clock;

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

    internal RevisionFactCache GetOrAdvance(
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
        lock (_gate)
        {
            if (_scopes.TryGetValue(workspaceScope, out ScopeEntry? existing)
                && string.Equals(existing.Identity, revisionIdentity, StringComparison.Ordinal))
            {
                existing.LastUsed = ++_clock;
                lazy = existing.Lazy;
            }
            else
            {
                RevisionFactCache? previous = existing is { Lazy.IsValueCreated: true } ? existing.Lazy.Value : null;
                lazy = new Lazy<RevisionFactCache>(
                    () =>
                    {
                        using SqliteConnection connection = openRead();
                        if (previous is { CanAdvance: true })
                            return previous.Advance(connection, visibility);
                        return RevisionFactCache.Load(connection, visibility);
                    },
                    LazyThreadSafetyMode.ExecutionAndPublication);
                _scopes[workspaceScope] = new ScopeEntry(revisionIdentity, lazy, ++_clock);
            }
        }

        try
        {
            RevisionFactCache cache = lazy.Value;
            lock (_gate)
            {
                if (_scopes.TryGetValue(workspaceScope, out ScopeEntry? current)
                    && ReferenceEquals(current.Lazy, lazy))
                {
                    current.LastUsed = ++_clock;
                    EvictToBudget(workspaceScope);
                }
            }

            return cache;
        }
        catch
        {
            lock (_gate)
            {
                if (_scopes.TryGetValue(workspaceScope, out ScopeEntry? current)
                    && ReferenceEquals(current.Lazy, lazy))
                {
                    _scopes.Remove(workspaceScope);
                }
            }

            throw;
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
        internal ScopeEntry(string identity, Lazy<RevisionFactCache> lazy, long lastUsed)
        {
            Identity = identity;
            Lazy = lazy;
            LastUsed = lastUsed;
        }

        internal string Identity { get; }

        internal Lazy<RevisionFactCache> Lazy { get; }

        internal long LastUsed { get; set; }
    }
}
