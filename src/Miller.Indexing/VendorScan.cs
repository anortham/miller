namespace Miller.Indexing;

/// <summary>
/// Pure vendor-directory detection over a workspace file listing — the consumer-side port of julie's
/// <c>analyze_vendor_patterns()</c> (<c>src/tools/workspace/discovery.rs</c>). Given root-relative file paths
/// it returns the directories that look like vendored/third-party/build-output code, as root-relative
/// <c>.julieignore</c> patterns. ZERO I/O: the caller (the <see cref="JulieIgnoreSeeder"/> edge) enumerates
/// the real tree; unit tests feed fake trees.
///
/// <para>Heuristics (ported, not transliterated): (1) a directory — or any ancestor — whose NAME is a known
/// vendor/build-output name (<c>vendor</c>, <c>third-party</c>, <c>node_modules</c>, <c>target</c>,
/// <c>build</c>, <c>dist</c>, <c>out</c>, <c>obj</c>, <c>Debug</c>, <c>Release</c>, <c>bower_components</c>)
/// qualifies when it holds more than <see cref="VendorDirectoryFileThreshold"/> files recursively; (2) a
/// directory with a jquery*/bootstrap* file cluster or a high concentration of <c>.min.</c> files qualifies
/// regardless of its name (the "unusual vendor dir" case). Source-layout names like <c>src</c>,
/// <c>packages</c>, <c>libs</c>, <c>lib</c>, <c>bin</c>, <c>plugins</c> are deliberately NOT vendor names —
/// julie's own notes: they commonly hold user code.</para>
/// </summary>
public static class VendorScan
{
    /// <summary>A name-matched vendor candidate must hold more than this many files recursively.</summary>
    public const int VendorDirectoryFileThreshold = 5;

    // Directory names that signal vendored/build-output code (julie's matches! list, Ordinal — "Debug" and
    // "Release" are the cased MSBuild conventions).
    private static readonly HashSet<string> VendorDirectoryNames = new(StringComparer.Ordinal)
    {
        "vendor",
        "third-party",
        "target",
        "node_modules",
        "build",
        "dist",
        "out",
        "obj",
        "Debug",
        "Release",
        "bower_components",
    };

    /// <summary>
    /// True when <paramref name="name"/> is one of the known vendor/build-output directory names. Lets the
    /// seeding walk decide a directory by NAME and prune it instead of enumerating a vendored tree.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    public static bool IsVendorDirectoryName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return VendorDirectoryNames.Contains(name);
    }

    /// <summary>
    /// The always-recommended baseline ignore patterns, independent of detection. <c>.miller/</c> is Miller's
    /// generated sidecar and must never feed back into extraction. <c>*.log</c> is index noise: julie parses no
    /// log files (zero artifact rows) and Miller has a separate ad-hoc log-scan tool, so log churn should not
    /// even reach the watcher/scan paths.
    /// </summary>
    public static IReadOnlyList<string> BaselinePatterns { get; } = new[] { ".miller/", "*.log" };

    /// <summary>
    /// Detect vendor-ish directories in <paramref name="relativeFilePaths"/> (root-relative, either
    /// separator). Returns sorted, deduplicated root-relative directory patterns with forward slashes and no
    /// trailing slash (the renderer adds the gitignore-style <c>/</c> suffix). Never returns the root itself.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="relativeFilePaths"/> is null.</exception>
    public static IReadOnlyList<string> DetectVendorDirectories(IEnumerable<string> relativeFilePaths)
    {
        ArgumentNullException.ThrowIfNull(relativeFilePaths);

        // Per-directory stats keyed by the file's IMMEDIATE parent ("" = the root itself).
        var stats = new Dictionary<string, DirectoryStats>(StringComparer.Ordinal);
        foreach (string path in relativeFilePaths)
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;
            string normalized = path.Replace('\\', '/').Trim('/');
            int slash = normalized.LastIndexOf('/');
            string directory = slash < 0 ? string.Empty : normalized[..slash];
            string fileName = slash < 0 ? normalized : normalized[(slash + 1)..];
            if (fileName.Length == 0)
                continue;

            if (!stats.TryGetValue(directory, out var entry))
            {
                entry = new DirectoryStats();
                stats[directory] = entry;
            }
            entry.FileCount++;
            if (fileName.Contains(".min.", StringComparison.Ordinal))
                entry.MinifiedCount++;
            if (fileName.StartsWith("jquery", StringComparison.OrdinalIgnoreCase))
                entry.JqueryCount++;
            if (fileName.StartsWith("bootstrap", StringComparison.OrdinalIgnoreCase))
                entry.BootstrapCount++;
        }

        // High confidence: a vendor-NAMED directory (the dir itself or any ancestor) with enough files under it.
        var patterns = new List<string>();
        var candidates = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string directory in stats.Keys)
        {
            foreach (string prefix in SelfAndAncestors(directory))
            {
                if (VendorDirectoryNames.Contains(LastSegment(prefix)))
                    candidates.Add(prefix);
            }
        }
        foreach (string candidate in candidates)
        {
            int recursiveCount = 0;
            foreach (var (directory, entry) in stats)
            {
                if (IsSelfOrUnder(candidate, directory))
                    recursiveCount += entry.FileCount;
            }
            if (recursiveCount > VendorDirectoryFileThreshold)
                patterns.Add(candidate);
        }

        // Medium confidence: vendor-shaped CONTENT in an arbitrarily named directory (sorted for determinism).
        foreach (string directory in stats.Keys.Where(d => d.Length > 0).OrderBy(d => d, StringComparer.Ordinal))
        {
            var entry = stats[directory];
            bool libraryCluster = entry.JqueryCount > 3 || entry.BootstrapCount > 2;
            bool minifiedCluster = entry.MinifiedCount > 10 && entry.MinifiedCount > entry.FileCount / 2;
            if ((libraryCluster || minifiedCluster) && !patterns.Any(p => IsSelfOrUnder(p, directory)))
                patterns.Add(directory);
        }

        return patterns.Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToArray();
    }

    // "a/b/c" -> ["a/b/c", "a/b", "a"]; "" yields nothing (the root is never a pattern).
    private static IEnumerable<string> SelfAndAncestors(string directory)
    {
        for (string current = directory; current.Length > 0;)
        {
            yield return current;
            int slash = current.LastIndexOf('/');
            current = slash < 0 ? string.Empty : current[..slash];
        }
    }

    private static string LastSegment(string directory)
    {
        int slash = directory.LastIndexOf('/');
        return slash < 0 ? directory : directory[(slash + 1)..];
    }

    private static bool IsSelfOrUnder(string ancestor, string directory) =>
        directory.Equals(ancestor, StringComparison.Ordinal)
        || directory.StartsWith(ancestor + "/", StringComparison.Ordinal);

    private sealed class DirectoryStats
    {
        public int FileCount;
        public int MinifiedCount;
        public int JqueryCount;
        public int BootstrapCount;
    }
}
