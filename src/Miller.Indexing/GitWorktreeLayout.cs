namespace Miller.Indexing;

/// <summary>
/// The resolved git administrative layout for a workspace root: where this checkout's own git directory
/// lives, where the repository's shared git directory lives, and — for a linked worktree — where the main
/// checkout is.
///
/// <para>A linked worktree (<c>git worktree add</c>) has a <c>.git</c> FILE holding
/// <c>gitdir: &lt;path&gt;</c> rather than a <c>.git</c> directory, and that directory holds a
/// <c>commondir</c> file pointing back at the repository's shared git dir. Code that tests
/// <c>Directory.Exists(root + "/.git")</c> therefore silently treats every linked worktree as
/// "not a git repository" — which is how the HEAD watch went missing for exactly the fleet this
/// workstream protects, and why a worktree's scan never saw the main checkout's <c>.julieignore</c>.</para>
///
/// <para>Resolution is filesystem-only: no <c>git</c> subprocess, no repository parsing beyond the two
/// pointer files git documents. The parsing seams are pure and fast-suite-testable; only
/// <see cref="Resolve"/> touches disk, and it never throws — an unreadable or malformed layout resolves to
/// null so index hygiene can degrade rather than break the scan that asked.</para>
/// </summary>
/// <param name="GitDir">
/// This checkout's own git directory: <c>&lt;root&gt;/.git</c> for a normal checkout, or the
/// <c>.git/worktrees/&lt;name&gt;</c> directory for a linked worktree. This is where the per-worktree
/// <c>HEAD</c> lives, so it is the path to watch for branch switches.
/// </param>
/// <param name="CommonDir">
/// The repository's shared git directory — equal to <paramref name="GitDir"/> for a normal checkout.
/// </param>
/// <param name="MainCheckoutRoot">
/// The working tree that owns <paramref name="CommonDir"/>, or null when the repository is bare (the
/// common dir is not named <c>.git</c>) so there is no main checkout to inherit policy from.
/// </param>
public sealed record GitWorktreeLayout(
    string GitDir,
    string CommonDir,
    string? MainCheckoutRoot)
{
    /// <summary>True when this root is a linked worktree rather than the checkout owning the repository.</summary>
    public bool IsLinkedWorktree =>
        !PathComparer.Equals(Path.TrimEndingDirectorySeparator(GitDir), Path.TrimEndingDirectorySeparator(CommonDir));

    private const string GitDirPrefix = "gitdir:";

    /// <summary>
    /// Resolve the layout for <paramref name="workspaceRoot"/>, or null when the root is not a git checkout
    /// (no <c>.git</c> entry) or its pointer files are unreadable or malformed. Never throws.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="workspaceRoot"/> is null or blank.</exception>
    public static GitWorktreeLayout? Resolve(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        try
        {
            string root = Path.GetFullPath(workspaceRoot);
            string dotGit = Path.Combine(root, ".git");

            if (Directory.Exists(dotGit))
                return new GitWorktreeLayout(dotGit, dotGit, root);

            if (!File.Exists(dotGit))
                return null;

            string? gitDir = ParseGitFile(File.ReadAllText(dotGit), root);
            if (gitDir is null || !Directory.Exists(gitDir))
                return null;

            string commonDir = gitDir;
            string commonDirFile = Path.Combine(gitDir, "commondir");
            if (File.Exists(commonDirFile)
                && ParseCommonDirFile(File.ReadAllText(commonDirFile), gitDir) is { } resolved
                && Directory.Exists(resolved))
            {
                commonDir = resolved;
            }

            return new GitWorktreeLayout(gitDir, commonDir, MainCheckoutRootFor(commonDir));
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
               or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Pure parse of a <c>.git</c> FILE's <c>gitdir: &lt;path&gt;</c> line into an absolute path. A relative
    /// path is resolved against <paramref name="rootForRelativePaths"/>, exactly as git resolves it against
    /// the working tree. Returns null when no <c>gitdir:</c> line carries a non-empty path.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="contents"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="rootForRelativePaths"/> is null or blank.</exception>
    public static string? ParseGitFile(string contents, string rootForRelativePaths)
    {
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootForRelativePaths);

        foreach (string line in contents.Split('\n'))
        {
            string trimmed = line.Trim();
            if (!trimmed.StartsWith(GitDirPrefix, StringComparison.Ordinal))
                continue;
            string value = trimmed[GitDirPrefix.Length..].Trim();
            if (value.Length == 0)
                continue;
            return Absolutize(value, rootForRelativePaths);
        }
        return null;
    }

    /// <summary>
    /// Pure parse of a linked worktree's <c>commondir</c> file into an absolute path. Git writes the path
    /// relative to the worktree's own git directory (typically <c>../..</c>), so
    /// <paramref name="gitDir"/> is the base for a relative value. Returns null when the file holds no
    /// non-blank line.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="contents"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="gitDir"/> is null or blank.</exception>
    public static string? ParseCommonDirFile(string contents, string gitDir)
    {
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentException.ThrowIfNullOrWhiteSpace(gitDir);

        foreach (string line in contents.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;
            return Absolutize(trimmed, gitDir);
        }
        return null;
    }

    /// <summary>
    /// The working tree owning a common git dir: its parent when the dir is named <c>.git</c>, else null —
    /// a bare repository has no checkout whose in-tree policy files could be inherited.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="commonDir"/> is null or blank.</exception>
    public static string? MainCheckoutRootFor(string commonDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commonDir);
        string trimmed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(commonDir));
        return PathComparer.Equals(Path.GetFileName(trimmed), ".git")
            ? Path.GetDirectoryName(trimmed)
            : null;
    }

    private static string Absolutize(string value, string baseDirectory) =>
        Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(baseDirectory, value));

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
