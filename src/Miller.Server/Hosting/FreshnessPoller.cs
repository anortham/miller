using Miller.Indexing;

namespace Miller.Server.Hosting;

/// <summary>
/// The pure poll-then-swap decision behind <see cref="FreshnessService"/> (m3-design decision-2/-5): the
/// testable seam with no SQLite, no timer, and no subprocess. Given the index holder, the latest persisted
/// revision (read by the service from <c>canonical_revisions</c>), and a rebuild factory, it rebuilds and
/// atomically swaps the index ONLY when the writer has moved ahead of the held index — so a reader instance
/// converges on the leader's writes without churning while the writer is idle.
/// </summary>
public static class FreshnessPoller
{
    /// <summary>
    /// If <paramref name="latestRevision"/> is strictly greater than the holder's current built revision,
    /// invoke <paramref name="rebuild"/> and <see cref="IndexHolder.Swap"/> the result in at the new revision;
    /// return true. Otherwise (equal or — defensively — older) do nothing and return false.
    ///
    /// <para>Strictly-greater (not just unequal) is deliberate: a no-op <c>extract update</c> does not bump the
    /// revision (verified-fact 2), so an unchanged writer leaves this a true no-op — no rebuild, no swap, no
    /// allocation. The rebuild factory is only called on a real advance.</para>
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="holder"/> or <paramref name="rebuild"/> is null.</exception>
    public static bool PollOnce(IndexHolder holder, long latestRevision, Func<MillerRepositoryIndex> rebuild)
    {
        ArgumentNullException.ThrowIfNull(holder);
        ArgumentNullException.ThrowIfNull(rebuild);

        if (latestRevision <= holder.BuiltRevision)
            return false;

        MillerRepositoryIndex next = rebuild();
        holder.Swap(next, latestRevision);
        return true;
    }
}
