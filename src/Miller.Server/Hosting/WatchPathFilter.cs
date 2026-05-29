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
/// Miller's own <c>.miller</c> sidecar (its extract/telemetry/WAL writes must not re-enter as events), and the
/// usual build-output trees (<c>node_modules</c>, <c>target</c>, <c>bin</c>, <c>obj</c>). Matching is on whole
/// path SEGMENTS, so a <c>.github</c> dir or an <c>object.cs</c> file is not caught by a substring.</para>
/// </summary>
public static class WatchPathFilter
{
    // Whole-segment skip set. NOT an extension list — these are directory names anywhere in the path. Ordinal
    // on the segment; case-sensitive matches the POSIX filesystems julie/Miller target (a Windows port would
    // pass an OrdinalIgnoreCase comparer here — out of M3 scope, the segments are lowercase by convention).
    private static readonly HashSet<string> SkipSegments = new(StringComparer.Ordinal)
    {
        ".git",
        ".miller",
        "node_modules",
        "target",
        "bin",
        "obj",
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
        return true;
    }
}
