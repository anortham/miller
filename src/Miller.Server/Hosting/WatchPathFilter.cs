namespace Miller.Server.Hosting;

/// <summary>
/// The language-agnostic path filter for the file watcher (m3-design §Components/3). It decides whether a
/// FileSystemWatcher event for a given absolute path should be enqueued for an <c>extract</c> op.
///
/// <para><b>It never whitelists source extensions.</b> The multi-language rule (CLAUDE.md): a cross-language
/// feature scopes to every capable language, and julie — not a hand-picked extension list — owns what is
/// indexable. An <c>update</c> on a file julie does not index simply no-ops (verified-fact 2), so over-feeding
/// is harmless, while an extension whitelist would silently drop a supported language (a <c>.zig</c>, a
/// <c>.vue</c>, a Dockerfile with no extension). The filter therefore ACCEPTS by default.</para>
///
/// <para>It only SKIPS noise directories that would either churn pointlessly or feed back on themselves:
/// version-control internals (<c>.git</c> — the dedicated <c>.git/HEAD</c> watch handles branch switches),
/// Miller's own <c>.miller</c> sidecar (its extract/telemetry/WAL writes must not re-enter as events), IDE
/// caches (<c>.vs</c>), and the usual build-output trees (<c>node_modules</c>, <c>target</c>, <c>bin</c>,
/// <c>obj</c>). Matching is on whole path SEGMENTS, so a <c>.github</c> dir or an <c>object.cs</c> file is not
/// caught by a substring. It also applies workspace ignore files (<c>.gitignore</c> plus
/// <c>.julieignore</c>) so live per-file updates do not churn on files a full scan would skip.</para>
/// </summary>
public static class WatchPathFilter
{
    // Whole-segment skip set. NOT an extension list — these are directory names anywhere in the path.
    private static readonly HashSet<string> SkipSegments = new(SegmentComparer)
    {
        ".git",
        ".miller",
        ".vs",
        "node_modules",
        "target",
        "bin",
        "obj",
    };

    private static readonly HashSet<string> IgnorePolicyFiles = new(SegmentComparer)
    {
        ".gitignore",
        ".julieignore",
    };

    /// <summary>
    /// True if a watcher event for <paramref name="absolutePath"/> (under <paramref name="root"/>) should be
    /// processed; false to drop it. <paramref name="root"/> is accepted for symmetry / future root-relative
    /// rules; the decision is made on the path's segments.
    /// </summary>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public static bool ShouldProcess(string root, string absolutePath)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(absolutePath);

        foreach (var segment in absolutePath.Split(
                     new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (SkipSegments.Contains(segment))
                return false;
        }
        return !WorkspaceIgnorePolicy.IsIgnored(root, absolutePath);
    }

    /// <summary>
    /// True when this event changes ignore policy rather than indexable source. The watcher should force one scan
    /// so previously-indexed files that just became ignored are pruned, and newly-unignored files are discovered.
    /// </summary>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public static bool ShouldForceRescan(string root, string absolutePath)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(absolutePath);
        return IgnorePolicyFiles.Contains(LastPathSegment(absolutePath))
            && !WorkspaceIgnorePolicy.IsOutsideRoot(root, absolutePath);
    }

    private static string LastPathSegment(string path)
    {
        string[] segments = path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0 ? string.Empty : segments[^1];
    }

    private static StringComparer SegmentComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
