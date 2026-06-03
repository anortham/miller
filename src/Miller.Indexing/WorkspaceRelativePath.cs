namespace Miller.Indexing;

/// <summary>
/// The workspace trust boundary for re-sourcing on-disk file content (§7). julie-extract records
/// ROOT-RELATIVE paths; a rooted path, or one that escapes the workspace root via <c>..</c>, must never
/// reach <see cref="File.ReadAllBytes"/> — a corrupt or tampered artifact could otherwise disclose a file
/// OUTSIDE the workspace (and a content_hash gate would not stop it if the artifact recorded the external
/// file's real hash). One shared check so the body-slice reader and the content-search loader can never
/// drift apart.
/// </summary>
internal static class WorkspaceRelativePath
{
    /// <summary>
    /// Resolve <paramref name="relPath"/> against <paramref name="workspaceRoot"/>, returning the absolute
    /// path when it stays under the (canonicalized) root, or <c>null</c> when the path is rooted or escapes
    /// the root. Existence is NOT checked here; the caller probes <see cref="File.Exists"/>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="workspaceRoot"/> is null/empty/whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="relPath"/> is null.</exception>
    public static string? ResolveUnderRoot(string workspaceRoot, string relPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentNullException.ThrowIfNull(relPath);

        if (Path.IsPathRooted(relPath))
            return null;

        string root = Path.GetFullPath(workspaceRoot);
        string abs = Path.GetFullPath(Path.Combine(root, relPath));
        return IsUnderRoot(root, abs) ? abs : null;
    }

    // Whether <paramref name="candidate"/> (an absolute, normalized path) is the root itself or lies beneath
    // it. Ordinal comparison matches the rest of the path discipline; the trailing separator stops a sibling
    // like "/ws-evil" from matching root "/ws".
    private static bool IsUnderRoot(string root, string candidate)
    {
        if (string.Equals(candidate, root, StringComparison.Ordinal))
            return true;
        string rootWithSep = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(rootWithSep, StringComparison.Ordinal);
    }
}
