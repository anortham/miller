namespace Miller.Core.Freshness;

/// <summary>
/// The kind of file-system change a <see cref="WatchEvent"/> represents. Mirrors julie's
/// <c>FileChangeType</c> (<c>src/watcher/types.rs</c>), the authoritative cross-language source.
/// </summary>
public enum WatchEventKind
{
    /// <summary>A new file appeared.</summary>
    Created,

    /// <summary>An existing file's contents changed.</summary>
    Modified,

    /// <summary>A file was removed.</summary>
    Deleted,

    /// <summary>A file moved from <see cref="WatchEvent.OldPath"/> to <see cref="WatchEvent.Path"/>.</summary>
    Renamed,
}

/// <summary>
/// A single coalesced file-system change. Pure data, no I/O. Ported from julie's <c>FileChangeEvent</c>:
/// <see cref="Path"/> is the <em>affected</em> path — for a rename that is the destination (the <c>to</c>),
/// matching julie's <c>affected_path</c> so the coalescing queue keys renames on where the file now lives.
/// <see cref="OldPath"/> is the rename source and is non-null <em>only</em> for <see cref="WatchEventKind.Renamed"/>.
/// </summary>
public sealed record WatchEvent
{
    /// <summary>
    /// The affected path. For <see cref="WatchEventKind.Renamed"/> this is the destination path; for every
    /// other kind it is the path of the file that changed.
    /// </summary>
    public string Path { get; }

    /// <summary>The change kind.</summary>
    public WatchEventKind Kind { get; }

    /// <summary>
    /// The rename source path. Non-null only when <see cref="Kind"/> is <see cref="WatchEventKind.Renamed"/>;
    /// null otherwise.
    /// </summary>
    public string? OldPath { get; }

    /// <summary>
    /// Construct a non-rename event. Use <see cref="Renamed"/> to construct a rename (which requires both paths).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="kind"/> is <see cref="WatchEventKind.Renamed"/>
    /// (use <see cref="Renamed"/>, which carries the old path).</exception>
    public WatchEvent(string path, WatchEventKind kind)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (kind == WatchEventKind.Renamed)
            throw new ArgumentException(
                "Use WatchEvent.Renamed(oldPath, newPath) to construct a rename.", nameof(kind));

        Path = path;
        Kind = kind;
        OldPath = null;
    }

    private WatchEvent(string newPath, string oldPath, WatchEventKind kind)
    {
        Path = newPath;
        OldPath = oldPath;
        Kind = kind;
    }

    /// <summary>
    /// Construct a <see cref="WatchEventKind.Renamed"/> event. <paramref name="newPath"/> becomes the
    /// affected <see cref="Path"/> (the destination); <paramref name="oldPath"/> becomes <see cref="OldPath"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException">Either path is null.</exception>
    public static WatchEvent Renamed(string oldPath, string newPath)
    {
        ArgumentNullException.ThrowIfNull(oldPath);
        ArgumentNullException.ThrowIfNull(newPath);
        return new WatchEvent(newPath, oldPath, WatchEventKind.Renamed);
    }
}
