using System.Collections.Concurrent;

namespace Miller.Indexing.Semantic;

/// <summary>
/// The process-wide set of retained generation tags an in-process reader currently holds open, so GC never
/// deletes a file a live query is reading from. A refcount, not a flag: several concurrent queries may open the
/// same generation, and the tag stays live until the last one disposes its registration. Cross-process readers
/// are deliberately NOT tracked here — they are protected by the soak window alone (vectors-v1 §Shadow
/// generations and rollback, P2 B6 posture).
/// </summary>
/// <remarks>
/// One <see cref="Shared"/> instance pairs the reader arm's open sites with the leader's GC scheduler, the same
/// way <see cref="Miller.Server.Hosting.VectorConvergeSignal"/> pairs the converger with the drain loop. The
/// registry never reaches the filesystem, so holding it under <c>MILLER_SEMANTIC=off</c> is inert — no reader
/// ever registers because the off arm returns before opening a port.
/// </remarks>
public sealed class VectorLiveReaderRegistry
{
    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);

    /// <summary>The instance the reader arm registers on and the GC scheduler reads.</summary>
    public static VectorLiveReaderRegistry Shared { get; } = new();

    /// <summary>Registers a live reader against <paramref name="tag"/>. Disposing the returned handle releases
    /// exactly one registration; a second dispose is a no-op, so a caller's <c>using</c> is always safe.</summary>
    public IDisposable Register(string tag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);

        _counts.AddOrUpdate(tag, 1, static (_, count) => count + 1);
        return new Registration(this, tag);
    }

    /// <summary>A point-in-time snapshot of the tags with at least one live reader.</summary>
    public IReadOnlySet<string> LiveTags =>
        _counts
            .Where(static entry => entry.Value > 0)
            .Select(static entry => entry.Key)
            .ToHashSet(StringComparer.Ordinal);

    private void Release(string tag)
    {
        while (_counts.TryGetValue(tag, out int current))
        {
            if (current <= 1)
            {
                if (((ICollection<KeyValuePair<string, int>>)_counts).Remove(
                        new KeyValuePair<string, int>(tag, current)))
                {
                    return;
                }
            }
            else if (_counts.TryUpdate(tag, current - 1, current))
            {
                return;
            }
        }
    }

    private sealed class Registration(VectorLiveReaderRegistry registry, string tag) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                registry.Release(tag);
        }
    }
}
