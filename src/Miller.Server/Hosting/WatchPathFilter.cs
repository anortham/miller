namespace Miller.Server.Hosting;

/// <summary>
/// The language-agnostic path filter for the file watcher (m3-design §Components/3). It decides whether a
/// FileSystemWatcher event for a given absolute path should be enqueued for an <c>extract</c> op.
///
/// <para><b>It never hand-picks source extensions.</b> The multi-language rule (CLAUDE.md): a cross-language
/// feature scopes to every capable language, and julie — not a hand-picked extension list — owns what is
/// indexable. An <c>update</c> on a file julie does not index simply no-ops (verified-fact 2), so over-feeding
/// is harmless to the INDEX — but each over-fed event still spawns a julie-extract subprocess. The optional
/// <paramref name="supportedExtensions"/> set therefore lets the caller gate events on julie's OWN claimed
/// extension list (<c>julie-extract languages --json</c>, fetched once per process). That keeps julie the
/// authority: a <c>.zig</c> or <c>.vue</c> passes because julie claims it, and when the set is unavailable
/// (fetch failed, binary missing) the filter gates NOTHING — the historical accept-by-default behavior.</para>
///
/// <para>It also SKIPS noise directories that would either churn pointlessly or feed back on themselves:
/// version-control internals (<c>.git</c> — the dedicated <c>.git/HEAD</c> watch handles branch switches —
/// plus <c>.hg</c>/<c>.svn</c>), Miller's own <c>.miller</c> sidecar (its extract/telemetry/WAL writes must
/// not re-enter as events), julie's <c>.julie</c> home, IDE/tool caches (<c>.vs</c>, <c>.cache</c>), agent
/// memory checkpoints (<c>.memories</c>), nested worktrees (<c>.worktrees</c> and <c>.claude/worktrees</c> —
/// the same two locations Miller's invariant ignore file excludes from extraction, so a repo-root worktree
/// pool is neither indexed by the parent workspace nor watched), and the usual
/// build-output trees (<c>node_modules</c>,
/// <c>target</c>, <c>bin</c>, <c>obj</c>) — parity with julie-extract's own hard-excluded directories, so
/// the watcher never spawns a subprocess for a file julie would refuse anyway. Matching is on whole path
/// SEGMENTS, so a <c>.github</c> dir or an <c>object.cs</c> file is not caught by a substring. It also
/// applies workspace ignore files (<c>.gitignore</c> plus <c>.julieignore</c>) so live per-file updates do
/// not churn on files a full scan would skip.</para>
///
/// <para><b>The skip set is matched ROOT-RELATIVE.</b> These are directory names INSIDE a workspace, so a
/// workspace whose own root sits under one of them — <c>&lt;repo&gt;/.worktrees/&lt;branch&gt;</c>, the agent
/// worktree convention this filter exists to serve — must not have every file it owns rejected for a segment
/// that belongs to its own root. Only the remainder below the root is matched; a path OUTSIDE the root falls
/// back to whole-path matching, where <see cref="WorkspaceIgnorePolicy.IsIgnored"/> rejects it anyway.</para>
/// </summary>
public static class WatchPathFilter
{
    // Whole-segment skip set. NOT an extension list — these are directory names anywhere in the path.
    // Keep in step with julie-extract's hard-excluded directories (the extractor refuses these regardless
    // of ignore files; Miller's watcher should not spawn subprocesses for them either).
    private static readonly HashSet<string> SkipSegments = new(SegmentComparer)
    {
        ".git",
        ".hg",
        ".svn",
        ".miller",
        ".julie",
        ".vs",
        ".cache",
        ".memories",
        ".worktrees",
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
    /// processed; false to drop it. The skip-segment decision is made on the path's segments BELOW
    /// <paramref name="root"/>, so a root that itself contains a skip segment still watches its own files.
    /// </summary>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public static bool ShouldProcess(string root, string absolutePath) =>
        ShouldProcess(root, absolutePath, supportedExtensions: null);

    /// <summary>
    /// As <see cref="ShouldProcess(string,string)"/>, additionally gating on julie's claimed extension set
    /// (<c>julie-extract languages --json</c>). A null or EMPTY set gates nothing — fail soft to the
    /// historical accept-by-default behavior when the languages probe failed or returned nothing usable.
    /// The set is membership-only here (pure, fast-suite-testable); fetching it is the caller's edge.
    /// Paths with no explicit extension are kept fail-soft because the extension-only catalog cannot prove they
    /// are unsupported. Ignore-policy files (<c>.gitignore</c>/<c>.julieignore</c>) are still dropped from
    /// per-file dispatch — their special handling runs through <see cref="ShouldForceRescan"/>, which the watcher
    /// consults FIRST, so policy-change rescans still fire.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="root"/> or <paramref name="absolutePath"/> is null.</exception>
    public static bool ShouldProcess(string root, string absolutePath, IReadOnlySet<string>? supportedExtensions)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(absolutePath);

        if (IsWorkspaceRootItself(root, absolutePath))
            return false;
        if (HasSkippedSegment(root, absolutePath))
            return false;
        if (HasUnsupportedExtension(absolutePath, supportedExtensions))
            return false;
        return !WorkspaceIgnorePolicy.IsIgnored(root, absolutePath);
    }

    /// <summary>
    /// True when the event names the workspace ROOT rather than a file inside it. Such an event is never
    /// actionable as a per-file op — <c>delete(&lt;root&gt;\)</c> reached the extractor and came back
    /// <c>invalid_file_path</c> on the Miller workspace itself (2026-08-12 triage).
    /// </summary>
    /// <remarks>
    /// The source is an EMPTY-NAME <see cref="FileSystemWatcher"/> notification: a rename whose old-name
    /// record landed in the previous buffer read surfaces as a <c>Renamed</c> event with a null old name, and
    /// the handler stats only the new path — which resolves back to the root. Common under the rename traffic
    /// of a Release build. Nothing below the root can produce <c>"."</c>, so this rejects only the root.
    /// </remarks>
    private static bool IsWorkspaceRootItself(string root, string absolutePath)
    {
        string? relative = TryRootRelative(root, absolutePath);
        return relative is not null && (relative.Length == 0 || relative == ".");
    }

    private static bool HasSkippedSegment(string root, string absolutePath)
    {
        string[] segments = SkipCandidateSegments(root, absolutePath);
        for (int i = 0; i < segments.Length; i++)
        {
            if (SkipSegments.Contains(segments[i]))
                return true;
            if (i > 0
                && SegmentComparer.Equals(segments[i - 1], ".claude")
                && SegmentComparer.Equals(segments[i], "worktrees"))
                return true;
        }
        return false;
    }

    // The segments the skip set is matched against: the remainder BELOW the root when the path is inside it,
    // else the whole path. A root of <repo>/.worktrees/<branch> owns files whose ABSOLUTE path carries a skip
    // segment that is the root's own, not the file's.
    private static string[] SkipCandidateSegments(string root, string absolutePath) =>
        SplitSegments(TryRootRelative(root, absolutePath) ?? absolutePath);

    private static string? TryRootRelative(string root, string absolutePath)
    {
        try
        {
            string relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(absolutePath));
            return Path.IsPathRooted(relative) || IsParentRelative(relative) ? null : relative;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool IsParentRelative(string relative) =>
        relative == ".."
        || relative.StartsWith("../", StringComparison.Ordinal)
        || relative.StartsWith(@"..\", StringComparison.Ordinal);

    private static string[] SplitSegments(string path) =>
        path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);

    // Pure extension gate over julie's claimed set. The set holds lowercase, dot-less extensions exactly as
    // `languages --json` reports them — nothing is hardcoded here. A file with no extension (Dockerfile, a
    // dotfile, a trailing dot) remains fail-soft because the extension-only catalog cannot prove it is
    // unsupported; julie can still no-op cheaply if it does not recognize the path.
    private static bool HasUnsupportedExtension(string absolutePath, IReadOnlySet<string>? supportedExtensions)
    {
        if (supportedExtensions is null || supportedExtensions.Count == 0)
            return false; // no usable set — gate nothing (fail soft)

        string name = LastPathSegment(absolutePath);
        int dot = name.LastIndexOf('.');
        if (dot <= 0 || dot == name.Length - 1)
            return IgnorePolicyFiles.Contains(name);
        return !supportedExtensions.Contains(name[(dot + 1)..].ToLowerInvariant());
    }

    /// <summary>
    /// True when this event changes ignore policy rather than indexable source. The watcher should force one scan
    /// so previously-indexed files that just became ignored are pruned, and newly-unignored files are discovered.
    ///
    /// <para>Gated on the same root-relative skip decision as <see cref="ShouldProcess(string,string)"/>: a
    /// policy file inside a subtree this workspace never extracts cannot change a single row, so it must not arm
    /// a whole-tree scan. <c>git worktree add .worktrees/&lt;branch&gt;</c> writes a <c>.gitignore</c> under the
    /// parent repo's own root, once per agent worktree, on the largest roots.</para>
    /// </summary>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public static bool ShouldForceRescan(string root, string absolutePath)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(absolutePath);
        return IgnorePolicyFiles.Contains(LastPathSegment(absolutePath))
            && !WorkspaceIgnorePolicy.IsOutsideRoot(root, absolutePath)
            && !HasSkippedSegment(root, absolutePath);
    }

    private static string LastPathSegment(string path)
    {
        string[] segments = SplitSegments(path);
        return segments.Length == 0 ? string.Empty : segments[^1];
    }

    private static StringComparer SegmentComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
