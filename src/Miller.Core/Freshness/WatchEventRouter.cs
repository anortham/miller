namespace Miller.Core.Freshness;

/// <summary>
/// Translates a drained, coalesced batch of <see cref="WatchEvent"/>s into an ordered list of
/// <see cref="ExtractOp"/>s. Pure: file existence is supplied as an injected stat predicate, so the router
/// is unit-tested with no real file system (spec §Components/1, §Test strategy).
///
/// <para>Routing table:</para>
/// <list type="bullet">
/// <item>If a whole-repo scan is due (queue overflowed, <c>.git/HEAD</c> moved, or a latched request) → that
///   single <see cref="ScanOp"/>; every per-file event is dropped, because julie's whole-repo
///   <c>extract scan</c> reconcile supersedes a lossy event stream.</item>
/// <item><see cref="WatchEventKind.Created"/>/<see cref="WatchEventKind.Modified"/> whose path exists →
///   <see cref="UpdateOp"/>; if the path has since vanished → <see cref="DeleteOp"/> (avoids handing julie
///   an update for a missing file).</item>
/// <item><see cref="WatchEventKind.Deleted"/> → <see cref="DeleteOp"/> unconditionally (the deletion was
///   observed; remove the index entry even if a later create raced the stat).</item>
/// <item><see cref="WatchEventKind.Renamed"/> → <see cref="DeleteOp"/> for the old path, then the new path
///   routed through the same exists check (Update if present, Delete if gone).</item>
/// </list>
/// </summary>
public static class WatchEventRouter
{
    /// <summary>
    /// Route <paramref name="events"/> to ordered <see cref="ExtractOp"/>s. Per-event order is preserved;
    /// a rename expands to its Delete(old) immediately followed by the op for its new path.
    /// </summary>
    /// <param name="events">The drained, coalesced batch (FIFO order).</param>
    /// <param name="exists">Stat predicate: does this absolute path currently exist? Injected for purity.</param>
    /// <param name="wholeRepoScan">
    /// The whole-repo scan this tick owes, already carrying its <see cref="ScanIntent"/> and any explicit jobs
    /// cap, or null when none is due. Non-null makes the result that single op and <paramref name="events"/> is
    /// ignored. Passed IN so this router stays pure — it reads nothing of its own.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="events"/> or <paramref name="exists"/> is null.</exception>
    public static IReadOnlyList<ExtractOp> Route(
        IReadOnlyList<WatchEvent> events,
        Func<string, bool> exists,
        ScanOp? wholeRepoScan)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(exists);

        if (wholeRepoScan is not null)
            return new ExtractOp[] { wholeRepoScan };

        if (events.Count == 0)
            return Array.Empty<ExtractOp>();

        var ops = new List<ExtractOp>(events.Count + 1);
        foreach (var ev in events)
        {
            switch (ev.Kind)
            {
                case WatchEventKind.Deleted:
                    ops.Add(new DeleteOp(ev.Path));
                    break;

                case WatchEventKind.Renamed:
                    // Old path is gone for certain; the destination is routed by its current existence.
                    ops.Add(new DeleteOp(ev.OldPath!));
                    ops.Add(RouteExisting(ev.Path, exists));
                    break;

                case WatchEventKind.Created:
                case WatchEventKind.Modified:
                    ops.Add(RouteExisting(ev.Path, exists));
                    break;

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(events), ev.Kind, "Unhandled WatchEventKind in routing.");
            }
        }

        return ops;
    }

    /// <summary>Update if the path exists, else Delete (the create/modify/rename-target raced a removal).</summary>
    private static ExtractOp RouteExisting(string path, Func<string, bool> exists) =>
        exists(path) ? new UpdateOp(path) : new DeleteOp(path);
}
