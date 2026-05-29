using Miller.Core.Freshness;

namespace Miller.Server.Hosting;

/// <summary>
/// Pure translation from a .NET <see cref="WatcherChangeTypes"/> notification to a Core <see cref="WatchEvent"/>
/// — the seam between the infra <see cref="FileSystemWatcher"/> and the pure coalescing queue. Keeping it
/// pure/static means the FSW handler is a one-liner and the mapping is unit-tested without a real watcher.
/// </summary>
public static class WatcherEventMapper
{
    /// <summary>
    /// Map a non-rename change to the affected <paramref name="path"/>. Created → Created; Deleted → Deleted;
    /// anything else (Changed, or a folded attribute/size/lastwrite change) → Modified, since julie blake3-checks
    /// the content and no-ops if the bytes are unchanged (verified-fact 2). A folded value carrying the Created
    /// bit is treated as a create (a new file).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    public static WatchEvent Map(WatcherChangeTypes changeType, string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (changeType.HasFlag(WatcherChangeTypes.Created))
            return new WatchEvent(path, WatchEventKind.Created);
        if (changeType.HasFlag(WatcherChangeTypes.Deleted))
            return new WatchEvent(path, WatchEventKind.Deleted);
        return new WatchEvent(path, WatchEventKind.Modified);
    }

    /// <summary>
    /// Map a rename to a <see cref="WatchEvent.Renamed"/> carrying both paths, so the router emits
    /// Delete(old) + Update(new).
    /// </summary>
    /// <exception cref="ArgumentNullException">Either path is null.</exception>
    public static WatchEvent MapRenamed(string oldPath, string newPath) =>
        WatchEvent.Renamed(oldPath, newPath);
}
