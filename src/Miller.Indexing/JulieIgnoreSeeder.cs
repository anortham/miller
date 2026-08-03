using System.Text;

namespace Miller.Indexing;

/// <summary>
/// The filesystem edge for <see cref="VendorScan"/> — the consumer-side port of julie's
/// <c>generate_julieignore_file()</c>. When a workspace root has NO <c>.julieignore</c>, it writes one, and the
/// seeded file then flows everywhere ignore policy already flows: julie-extract reads it from the root on full
/// scans AND on single-file <c>update</c>s, and the watcher's <c>WorkspaceIgnorePolicy</c> reads the same file
/// for live events. In-tree is the ONLY placement with that property.
///
/// <para><b>Two shapes.</b> A LINKED WORKTREE with no <c>.julieignore</c> of its own whose main checkout has
/// one is seeded with a COPY of that file behind a generated header (<see cref="RenderInheritedContent"/>) —
/// <c>git worktree add</c> hands over the committed tree, but the interesting ignore file is usually
/// uncommitted (Miller seeds one; users write local ones) and exists only in the main checkout. Every other
/// root is seeded with the baseline noise patterns plus auto-detected vendor directories
/// (<see cref="RenderContent"/>). No root comes out of a scan without an in-tree policy.</para>
///
/// <para><b>Why a copy rather than <c>--ignore-file</c>.</b> Pointing julie-extract at the main checkout's file
/// leaves the worktree with no in-tree policy at all, and the watcher only loads <c>.julieignore</c> at or under
/// the workspace root — so the scan would exclude <c>generated/foo.cs</c> while a later touch of that same file
/// let the watcher through and <c>julie-extract update</c> re-inserted it (verified: an <c>update</c> on a file
/// excluded IN-TREE reports <c>unsupported</c> and writes nothing). The copy also sidesteps the hard-error
/// hazard: a caller-supplied <c>--ignore-file</c> that cannot be read or parsed FAILS the whole scan, while the
/// same content in-tree only warns.</para>
///
/// <para>The copy is a snapshot — it does not track later edits to the main checkout's file. That is the same
/// staleness the seeded file already has, and the seeder's existing contract covers it: delete the file and
/// rescan to re-copy.</para>
///
/// <para>Vendor-NAMED directories are detected by name and PRUNED rather than enumerated: the walk counts
/// their files only until the <see cref="VendorScan.VendorDirectoryFileThreshold"/> decision is settled, then
/// stops descending. A <c>node_modules</c> holding 60k files costs one directory listing instead of 60k
/// yielded paths, which is what previously drove the walk into its cap.</para>
///
/// <para>The remaining enumeration — the evidence for content-shaped detection (jquery/bootstrap clusters,
/// minified concentration) — stays bounded at <see cref="MaxEnumeratedFiles"/>, because an unbounded walk on a
/// 74k-file root is the failure this whole workstream exists to prevent. Hitting that bound is no longer
/// SILENT: it is carried out of the walk and rendered as a warning block in the generated file, so a root whose
/// detection was truncated says so where the user reads the result.</para>
///
/// <para>Contract: NEVER overwrites or appends to an existing <c>.julieignore</c> (a user-authored file is
/// authoritative; deleting the generated one and rescanning regenerates from scratch). Best-effort: any I/O
/// failure returns false rather than throwing — seeding hygiene must never break the scan that triggered it.
/// The pure pieces (<see cref="RenderContent"/>, <see cref="RenderInheritedContent"/>,
/// <see cref="VendorScan"/>) are fast-suite-testable; only the walk/write here touches the real filesystem.</para>
/// </summary>
public static class JulieIgnoreSeeder
{
    /// <summary>The workspace-root ignore file julie-extract reads in-tree and this seeder writes.</summary>
    public const string WorkspaceIgnoreFileName = ".julieignore";

    /// <summary>
    /// Bound on the files the detection walk collects as content evidence. Vendor-named trees are pruned
    /// before they contribute, so reaching this means the root really does hold this many non-vendor files;
    /// the walk then stops and <see cref="RenderContent"/> says so instead of silently under-detecting.
    /// </summary>
    internal const int MaxEnumeratedFiles = 200_000;

    /// <summary>
    /// Bound on the directories one vendor-name probe visits while deciding whether a candidate clears
    /// <see cref="VendorScan.VendorDirectoryFileThreshold"/>. The probe stops as soon as the threshold is
    /// cleared, so this only bounds the pathological shape: a deep tree of near-empty directories.
    /// </summary>
    internal const int MaxVendorProbeDirectories = 4_096;

    // Internal dirs the detection walk skips outright: their contents are never indexable and never vendor
    // evidence. Vendor-named dirs (node_modules, target, ...) are NOT here — they are handled by the name
    // probe, which must see them to report them.
    private static readonly HashSet<string> WalkSkipDirectories = new(StringComparer.Ordinal)
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
    };

    /// <summary>
    /// Seed <c>&lt;workspaceRoot&gt;/.julieignore</c> when none exists — a copy of the main checkout's file for
    /// a linked worktree that inherits one, else the baseline + detected-vendor generation. Returns true only
    /// when a new file was written; false when one already exists (never overwritten), the root is missing, or
    /// any I/O step failed (best-effort — never throws).
    /// </summary>
    /// <remarks>
    /// The file is created EXCLUSIVELY (<see cref="FileMode.CreateNew"/>), not written over the earlier
    /// <see cref="File.Exists(string)"/> answer. Detection walks a whole repository between the two, and
    /// <see cref="File.WriteAllText(string, string?)"/> TRUNCATES — so two Miller processes bootstrapping the same
    /// fresh worktree (the ordinary fleet case), or a user authoring their own <c>.julieignore</c> inside that
    /// window, had authoritative scan input silently replaced by generated content. With an exclusive create the
    /// racing creator wins and this call becomes a no-op returning false: an already-existing file is an EXPECTED
    /// outcome of the race, not an error, and it reaches the same never-throw exit as every other I/O failure.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="workspaceRoot"/> is null or blank.</exception>
    public static bool EnsureSeeded(string workspaceRoot) =>
        EnsureSeeded(workspaceRoot, betweenProbeAndCreate: null, readAllText: null);

    /// <summary>
    /// <see cref="EnsureSeeded(string)"/> with a hook fired after the existence probe and the content render, and
    /// before the exclusive create — the seam that lets the race window be occupied deterministically instead of
    /// hoped for — plus an injectable reader for the inherited source, so an unreadable main-checkout file can be
    /// exercised without depending on platform locking semantics. Not used in production.
    /// </summary>
    internal static bool EnsureSeeded(
        string workspaceRoot, Action? betweenProbeAndCreate, Func<string, string>? readAllText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        try
        {
            string ignorePath = Path.Combine(workspaceRoot, WorkspaceIgnoreFileName);
            if (File.Exists(ignorePath) || !Directory.Exists(workspaceRoot))
                return false;

            if (!TryRenderSeedContent(workspaceRoot, readAllText ?? File.ReadAllText, out string content))
                return false;

            betweenProbeAndCreate?.Invoke();

            using var stream = new FileStream(ignorePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream);
            writer.Write(content);
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
               or NotSupportedException)
        {
            return false; // hygiene must never break the scan that triggered it
        }
    }

    /// <summary>
    /// The main checkout's <c>.julieignore</c> that <paramref name="workspaceRoot"/> inherits, or null when
    /// nothing should be inherited: the root is not a linked worktree, it already has its own
    /// <c>.julieignore</c> (a local file is authoritative), the repository is bare, or the main checkout has no
    /// <c>.julieignore</c>. Never throws.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="workspaceRoot"/> is null or blank.</exception>
    public static string? ResolveInheritedIgnoreFile(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        try
        {
            if (File.Exists(Path.Combine(workspaceRoot, WorkspaceIgnoreFileName)))
                return null;

            if (GitWorktreeLayout.Resolve(workspaceRoot) is not { IsLinkedWorktree: true } layout
                || layout.MainCheckoutRoot is not { } mainCheckout)
                return null;

            string candidate = Path.Combine(mainCheckout, WorkspaceIgnoreFileName);
            return File.Exists(candidate) ? candidate : null;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
               or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// What to seed: the main checkout's file copied verbatim when this root inherits one, else the baseline +
    /// detected-vendor generation. False means SEED NOTHING.
    ///
    /// <para>False is returned only for a source that exists and could not be read. Falling back to the
    /// generated baseline there looks harmless and is not: the create is exclusive, so the file it writes is
    /// never revisited, and one transient read error would permanently replace the main checkout's ignore rules
    /// with a generic baseline — the worktree then indexes everything the repository deliberately excludes, for
    /// as long as that worktree exists, silently. Writing nothing keeps the failure retryable on the next
    /// scan.</para>
    ///
    /// <para>An absent source is a different answer: there is genuinely nothing to inherit (not a linked
    /// worktree, or the main checkout has no file), and the generated seed is the correct content.</para>
    /// </summary>
    private static bool TryRenderSeedContent(
        string workspaceRoot, Func<string, string> readAllText, out string content)
    {
        if (ResolveInheritedIgnoreFile(workspaceRoot) is not { } source)
        {
            content = GeneratedContent(workspaceRoot);
            return true;
        }

        try
        {
            content = RenderInheritedContent(source, readAllText(source));
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
               or NotSupportedException)
        {
            content = string.Empty;
            return false;
        }
    }

    private static string GeneratedContent(string workspaceRoot)
    {
        DetectionResult detection = Detect(workspaceRoot);
        return RenderContent(detection.VendorDirectories, DateTime.UtcNow, detection.Truncated);
    }

    /// <summary>
    /// Pure renderer for the inherited copy: a header naming <paramref name="sourcePath"/> and stating the
    /// snapshot limitation, then <paramref name="sourceContent"/> verbatim. Copied unchanged so the worktree's
    /// policy is exactly the main checkout's; a malformed pattern in it degrades to julie-extract's in-tree
    /// WARNING rather than the hard scan failure a <c>--ignore-file</c> would raise.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="sourcePath"/> is null or blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="sourceContent"/> is null.</exception>
    public static string RenderInheritedContent(string sourcePath, string sourceContent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(sourceContent);

        var sb = new StringBuilder();
        sb.Append("# .julieignore — copied by Miller from this repository's main checkout:\n");
        sb.Append("#   ").Append(sourcePath.ReplaceLineEndings(" ")).Append('\n');
        sb.Append("# This linked worktree had none of its own. The copy is a SNAPSHOT: later edits to that\n");
        sb.Append("# file do not reach this one. Edit freely — Miller never overwrites or appends. Delete\n");
        sb.Append("# this file and rescan to copy the main checkout's current version again.\n");
        sb.Append('\n');
        sb.Append(sourceContent);
        if (!sourceContent.EndsWith('\n'))
            sb.Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// Pure renderer for the generated file: a short generated-by/edit-freely header, an explicit warning when
    /// <paramref name="detectionTruncated"/>, the baseline noise patterns
    /// (<see cref="VendorScan.BaselinePatterns"/>), and the detected vendor directories as gitignore-style
    /// <c>dir/</c> patterns. <paramref name="generatedAtUtc"/> is injected for determinism.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="vendorDirectories"/> is null.</exception>
    public static string RenderContent(
        IReadOnlyList<string> vendorDirectories, DateTime generatedAtUtc, bool detectionTruncated = false)
    {
        ArgumentNullException.ThrowIfNull(vendorDirectories);

        var sb = new StringBuilder();
        string date = generatedAtUtc.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        sb.Append("# .julieignore — code-intelligence exclusion patterns (gitignore syntax)\n");
        sb.Append("# Generated by Miller on ").Append(date).Append(": no .julieignore existed, so Miller\n");
        sb.Append("# seeded baseline noise patterns plus auto-detected vendor/build directories.\n");
        sb.Append("# Edit freely — Miller never overwrites or appends to this file. Excluded files\n");
        sb.Append("# stay out of symbol extraction and search; delete a line to re-include its files\n");
        sb.Append("# on the next scan. To opt out entirely, keep an empty .julieignore.\n");

        if (detectionTruncated)
        {
            sb.Append('\n');
            sb.Append("# TRUNCATED: detection stopped after ")
              .Append(MaxEnumeratedFiles.ToString(System.Globalization.CultureInfo.InvariantCulture))
              .Append(" files, so this root may hold\n");
            sb.Append("# vendor/build directories that are NOT listed below. Add them by hand, then delete\n");
            sb.Append("# this note.\n");
        }

        sb.Append('\n');
        sb.Append("# Baseline noise (logs are index noise; use Miller's log tooling to read them)\n");
        foreach (string pattern in VendorScan.BaselinePatterns)
            sb.Append(pattern).Append('\n');

        if (vendorDirectories.Count > 0)
        {
            sb.Append('\n');
            sb.Append("# Auto-detected vendor/build directories\n");
            foreach (string directory in vendorDirectories)
                sb.Append(directory).Append("/\n");
        }
        return sb.ToString();
    }

    /// <summary>The vendor directories detected under a root, and whether the bounded walk was cut short.</summary>
    internal sealed record DetectionResult(IReadOnlyList<string> VendorDirectories, bool Truncated);

    /// <summary>
    /// Walk <paramref name="workspaceRoot"/> and combine the two detection routes: vendor directories matched
    /// by NAME (pruned in the walk, so their contents cost nothing) and vendor directories matched by content
    /// shape over the bounded file listing.
    /// </summary>
    internal static DetectionResult Detect(string workspaceRoot) =>
        Detect(workspaceRoot, MaxEnumeratedFiles);

    /// <summary>
    /// <see cref="Detect(string)"/> with an injected file bound — the seam that lets the bound-reached path be
    /// proven on a tiny tree instead of a 200k-file one.
    /// </summary>
    internal static DetectionResult Detect(string workspaceRoot, int maxEnumeratedFiles)
    {
        var named = new List<string>();
        var files = new List<string>();
        bool truncated = Walk(workspaceRoot, maxEnumeratedFiles, named, files);

        var detected = new SortedSet<string>(named, StringComparer.Ordinal);
        foreach (string directory in VendorScan.DetectVendorDirectories(files))
            detected.Add(directory);
        return new DetectionResult(detected.ToArray(), truncated);
    }

    // Bounded, non-throwing walk. Fills `namedVendorDirectories` with pruned vendor-named dirs and `files` with
    // root-relative paths (forward slashes) for content detection. Returns true when the file bound was hit.
    // Unreadable subdirectories are skipped rather than failing the whole detection.
    private static bool Walk(
        string workspaceRoot, int maxEnumeratedFiles, List<string> namedVendorDirectories, List<string> files)
    {
        var pending = new Stack<string>();
        pending.Push(workspaceRoot);

        while (pending.Count > 0)
        {
            if (!TryList(pending.Pop(), out string[] directoryFiles, out string[] subdirectories))
                continue;

            foreach (string file in directoryFiles)
            {
                if (files.Count >= maxEnumeratedFiles)
                    return true;
                files.Add(Relative(workspaceRoot, file));
            }

            foreach (string subdirectory in subdirectories)
            {
                if (IsSkippedDirectory(subdirectory))
                    continue;
                if (VendorScan.IsVendorDirectoryName(Path.GetFileName(subdirectory))
                    && HoldsMoreFilesThanVendorThreshold(subdirectory))
                {
                    namedVendorDirectories.Add(Relative(workspaceRoot, subdirectory));
                    continue;
                }
                pending.Push(subdirectory);
            }
        }
        return false;
    }

    // Whether a vendor-NAMED directory holds enough files to qualify, counting only until the answer is known.
    private static bool HoldsMoreFilesThanVendorThreshold(string directory)
    {
        var pending = new Stack<string>();
        pending.Push(directory);
        int files = 0;
        int visited = 0;

        while (pending.Count > 0 && visited < MaxVendorProbeDirectories)
        {
            visited++;
            if (!TryList(pending.Pop(), out string[] directoryFiles, out string[] subdirectories))
                continue;
            files += directoryFiles.Length;
            if (files > VendorScan.VendorDirectoryFileThreshold)
                return true;
            foreach (string subdirectory in subdirectories)
                pending.Push(subdirectory);
        }
        return false;
    }

    private static bool IsSkippedDirectory(string directory)
    {
        string name = Path.GetFileName(directory);
        if (WalkSkipDirectories.Contains(name))
            return true;
        return string.Equals(name, "worktrees", StringComparison.Ordinal)
            && string.Equals(
                Path.GetFileName(Path.GetDirectoryName(directory) ?? string.Empty), ".claude",
                StringComparison.Ordinal);
    }

    private static bool TryList(string directory, out string[] files, out string[] subdirectories)
    {
        try
        {
            files = Directory.GetFiles(directory);
            subdirectories = Directory.GetDirectories(directory);
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            files = Array.Empty<string>();
            subdirectories = Array.Empty<string>();
            return false;
        }
    }

    private static string Relative(string workspaceRoot, string path) =>
        Path.GetRelativePath(workspaceRoot, path).Replace('\\', '/');
}
