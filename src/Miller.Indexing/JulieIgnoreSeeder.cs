using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Miller.Indexing;

/// <summary>The owner of an effective ignore policy and the consumer path used for it.</summary>
public enum IgnorePolicySource
{
    UserRoot,
    InheritedRootCopy,
    GeneratedGlobal,
}

/// <summary>Immutable policy identity shared by scan, update, and watcher preparation.</summary>
public sealed record EffectiveIgnorePolicy(
    IgnorePolicySource Source,
    string Path,
    string ContentHash,
    bool WroteNewBytes);

/// <summary>
/// The filesystem edge for <see cref="VendorScan"/> — the consumer-side port of julie's
/// <c>generate_julieignore_file()</c>. It resolves one immutable effective-policy descriptor for full scans,
/// single-file updates, and watcher matching. User policy remains in-tree; only Miller's generated baseline/vendor
/// policy is materialized globally under Miller home and passed as an external ignore file.
///
/// <para><b>Two shapes.</b> A LINKED WORKTREE with no <c>.julieignore</c> of its own whose main checkout has
/// one is seeded with a COPY of that file behind a generated header (<see cref="RenderInheritedContent"/>) —
/// <c>git worktree add</c> hands over the committed tree, but the interesting ignore file is usually
/// uncommitted (Miller seeds one; users write local ones) and exists only in the main checkout. Every other
/// root is represented by a deterministic global policy containing baseline noise patterns plus auto-detected
/// vendor directories (<see cref="RenderContent"/>). No generated root file is created.</para>
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
/// <para>Contract: NEVER overwrites or appends to an existing root <c>.julieignore</c> (a user-authored file is
/// authoritative). Generated policy bytes are deterministic and atomically materialized under Miller home;
/// a root-policy race is rechecked before that write. The pure pieces (<see cref="RenderContent"/>,
/// <see cref="RenderInheritedContent"/>, <see cref="VendorScan"/>) are fast-suite-testable; only the walk/write
/// here touches the real filesystem.</para>
/// </summary>
public static class JulieIgnoreSeeder
{
    /// <summary>The workspace-root ignore file julie-extract reads in-tree.</summary>
    public const string WorkspaceIgnoreFileName = ".julieignore";

    private const string GeneratedPolicyDirectoryName = "ignore-policies";
    private static readonly ConcurrentDictionary<string, object> MaterializationGates = new(StringComparer.Ordinal);

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
    /// Prepare effective ignore policy for <paramref name="workspaceRoot"/>. A linked worktree that inherits a
    /// user file receives the existing exclusive in-tree copy; otherwise generated baseline/vendor bytes are
    /// materialized under Miller home. Returns true only when preparation wrote new bytes; it never creates a
    /// generated root policy.
    /// </summary>
    /// <remarks>
    /// The linked-worktree copy is created exclusively. Generated policy uses a same-directory temporary file and
    /// atomic replace/move after comparing bytes; user root-policy existence is checked again after detection/render.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="workspaceRoot"/> is null or blank.</exception>
    public static bool EnsureSeeded(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        string root = Path.GetFullPath(workspaceRoot);
        return PreparePolicy(root, WorkspaceId.FromCanonicalRoot(root))?.WroteNewBytes == true;
    }

    /// <summary>
    /// <see cref="EnsureSeeded(string)"/> with a hook fired before policy materialization and an injectable reader
    /// for the inherited source. Not used in production.
    /// </summary>
    internal static bool EnsureSeeded(
        string workspaceRoot, Action? betweenProbeAndCreate, Func<string, string>? readAllText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        string root = Path.GetFullPath(workspaceRoot);
        return PreparePolicy(
            root,
            WorkspaceId.FromCanonicalRoot(root),
            MillerHome.ResolveMillerDirectory(),
            betweenProbeAndCreate,
            readAllText)?.WroteNewBytes == true;
    }

    public static EffectiveIgnorePolicy? PreparePolicy(string workspaceRoot, string workspaceId) =>
        PreparePolicy(workspaceRoot, workspaceId, MillerHome.ResolveMillerDirectory(), null, null);

    internal static EffectiveIgnorePolicy? PreparePolicy(
        string workspaceRoot,
        string workspaceId,
        string millerDirectory,
        Action? betweenProbeAndCreate = null,
        Func<string, string>? readAllText = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(millerDirectory);

        string root = Path.GetFullPath(workspaceRoot);
        ValidateWorkspaceId(root, workspaceId);
        if (!Directory.Exists(root))
            return null;

        string rootPolicy = Path.Combine(root, WorkspaceIgnoreFileName);
        if (File.Exists(rootPolicy))
            return DescribeExisting(IgnorePolicySource.UserRoot, rootPolicy);

        string? inherited = ResolveInheritedIgnoreFile(root);
        if (inherited is not null)
            return TryPrepareInheritedCopy(rootPolicy, inherited, readAllText, betweenProbeAndCreate);

        DetectionResult detection = Detect(root);
        string generated = RenderContent(detection.VendorDirectories, DateTime.UnixEpoch, detection.Truncated);
        string generatedPath = GeneratedGlobalIgnorePathForWorkspaceId(workspaceId, millerDirectory);

        betweenProbeAndCreate?.Invoke();
        if (File.Exists(rootPolicy))
            return DescribeExisting(IgnorePolicySource.UserRoot, rootPolicy);

        if (ResolveInheritedIgnoreFile(root) is { } inheritedAfterRender)
            return TryPrepareInheritedCopy(rootPolicy, inheritedAfterRender, readAllText, null);

        bool materialized = TryMaterializeGenerated(generatedPath, generated);
        return DescribeExisting(IgnorePolicySource.GeneratedGlobal, generatedPath, materialized);
    }

    /// <summary>
    /// Resolve policy for a direct update without walking or materializing generated policy. A resident workspace
    /// has already prepared policy during its scan lifecycle; an absent generated file therefore returns null
    /// and leaves only the invariant update controls in place. A linked worktree may establish its required
    /// inherited in-tree snapshot, including malformed bytes, because external user policy is not safe.
    /// </summary>
    public static EffectiveIgnorePolicy? ResolvePolicyForUpdate(string workspaceRoot, string workspaceId) =>
        ResolvePolicyForUpdate(workspaceRoot, workspaceId, MillerHome.ResolveMillerDirectory());

    internal static EffectiveIgnorePolicy? ResolvePolicyForUpdate(
        string workspaceRoot, string workspaceId, string millerDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(millerDirectory);

        string root = Path.GetFullPath(workspaceRoot);
        ValidateWorkspaceId(root, workspaceId);
        if (!Directory.Exists(root))
            return null;

        string rootPolicy = Path.Combine(root, WorkspaceIgnoreFileName);
        if (File.Exists(rootPolicy))
            return DescribeExisting(IgnorePolicySource.UserRoot, rootPolicy);

        if (ResolveInheritedIgnoreFile(root) is { } inherited)
            return TryPrepareInheritedCopy(rootPolicy, inherited, readAllText: null, betweenProbeAndCreate: null);

        string generatedPath = GeneratedGlobalIgnorePathForWorkspaceId(workspaceId, millerDirectory);
        return File.Exists(generatedPath)
            ? DescribeExisting(IgnorePolicySource.GeneratedGlobal, generatedPath)
            : null;
    }

    public static string GeneratedGlobalIgnorePathFor(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        string root = Path.GetFullPath(workspaceRoot);
        return GeneratedGlobalIgnorePathForWorkspaceId(WorkspaceId.FromCanonicalRoot(root));
    }

    public static string GeneratedGlobalIgnorePathForWorkspaceId(string workspaceId) =>
        GeneratedGlobalIgnorePathForWorkspaceId(workspaceId, MillerHome.ResolveMillerDirectory());

    internal static string GeneratedGlobalIgnorePathForWorkspaceId(string workspaceId, string millerDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(millerDirectory);
        if (workspaceId.Any(char.IsWhiteSpace)
            || workspaceId.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || workspaceId.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
            || workspaceId.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException("Workspace id must be a single path-safe identifier.", nameof(workspaceId));
        }

        return Path.Combine(
            Path.GetFullPath(millerDirectory),
            GeneratedPolicyDirectoryName,
            workspaceId + WorkspaceIgnoreFileName);
    }

    internal static string ContentHash(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Convert.ToHexStringLower(SHA256.HashData(content));
    }

    private static void ValidateWorkspaceId(string root, string workspaceId)
    {
        string expected = WorkspaceId.FromCanonicalRoot(root);
        if (!string.Equals(expected, workspaceId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Workspace id '{workspaceId}' does not match the canonical root '{root}'.", nameof(workspaceId));
        }
    }

    private static EffectiveIgnorePolicy? TryPrepareInheritedCopy(
        string rootPolicy,
        string inherited,
        Func<string, string>? readAllText,
        Action? betweenProbeAndCreate)
    {
        string content;
        try
        {
            content = RenderInheritedContent(inherited, (readAllText ?? File.ReadAllText)(inherited));
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
               or NotSupportedException)
        {
            return null;
        }

        betweenProbeAndCreate?.Invoke();
        if (File.Exists(rootPolicy))
            return DescribeExisting(IgnorePolicySource.UserRoot, rootPolicy);

        if (!TryCreateRootPolicy(rootPolicy, content, out bool wroteNewBytes))
            return File.Exists(rootPolicy)
                ? DescribeExisting(IgnorePolicySource.UserRoot, rootPolicy)
                : null;
        return DescribeExisting(IgnorePolicySource.InheritedRootCopy, rootPolicy, wroteNewBytes);
    }


    private static EffectiveIgnorePolicy? DescribeExisting(
        IgnorePolicySource source, string path, bool wroteNewBytes = false)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            return new EffectiveIgnorePolicy(source, Path.GetFullPath(path), ContentHash(bytes), wroteNewBytes);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
               or NotSupportedException)
        {
            return null;
        }
    }

    private static bool TryCreateRootPolicy(string path, string content, out bool wroteNewBytes)
    {
        wroteNewBytes = false;
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(content);
            wroteNewBytes = true;
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
               or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryMaterializeGenerated(string path, string content)
    {
        byte[] desired = Encoding.UTF8.GetBytes(content);
        string fullPath = Path.GetFullPath(path);
        object gate = MaterializationGates.GetOrAdd(fullPath, static _ => new object());
        lock (gate)
        {
            try
            {
                string directory = Path.GetDirectoryName(fullPath)!;
                Directory.CreateDirectory(directory);
                if (File.Exists(fullPath) && File.ReadAllBytes(fullPath).AsSpan().SequenceEqual(desired))
                    return false;

                string temporary = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
                File.WriteAllBytes(temporary, desired);
                try
                {
                    if (File.Exists(fullPath))
                        File.Replace(temporary, fullPath, destinationBackupFileName: null);
                    else
                        File.Move(temporary, fullPath);
                    return true;
                }
                finally
                {
                    try
                    {
                        if (File.Exists(temporary))
                            File.Delete(temporary);
                    }
                    catch (IOException)
                    {
                    }
                }
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
                   or NotSupportedException)
            {
                return false;
            }
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
    /// Pure renderer for the generated file: a short generated-by/ownership header, an explicit warning when
    /// <paramref name="detectionTruncated"/>, the baseline noise patterns
    /// (<see cref="VendorScan.BaselinePatterns"/>), and the detected vendor directories as gitignore-style
    /// <c>dir/</c> patterns. The retained <paramref name="generatedAtUtc"/> parameter does not enter the bytes,
    /// keeping generated policy hashes deterministic.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="vendorDirectories"/> is null.</exception>
    public static string RenderContent(
        IReadOnlyList<string> vendorDirectories, DateTime generatedAtUtc, bool detectionTruncated = false)
    {
        ArgumentNullException.ThrowIfNull(vendorDirectories);

        var sb = new StringBuilder();
        _ = generatedAtUtc;
        sb.Append("# .julieignore — code-intelligence exclusion patterns (gitignore syntax)\n");
        sb.Append("# Generated by Miller: no .julieignore existed, so Miller\n");
        sb.Append("# seeded baseline noise patterns plus auto-detected vendor/build directories.\n");
        sb.Append("# This global policy is generated and owned by Miller; Miller may rewrite it as the workspace changes.\n");
        sb.Append("# Create .julieignore at the workspace root for custom rules. Excluded files stay out\n");
        sb.Append("# of symbol extraction and search. To opt out of generated rules, create an empty root file.\n");

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
