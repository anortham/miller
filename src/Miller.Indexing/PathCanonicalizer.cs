namespace Miller.Indexing;

/// <summary>
/// Resolves absolute, SYMLINK-RESOLVED canonical paths for the workspace root and per-file <c>extract</c>
/// arguments. This pins the verified-fact-4 fix (m3-design.md §Verified facts 4): julie's <c>extract delete</c>
/// lexically normalizes <c>--file</c> (no symlink resolve) but canonicalizes the root, so on macOS
/// (<c>/var</c> → <c>/private/var</c>, <c>/tmp</c> → <c>/private/tmp</c>) a non-canonical <c>--file</c> under a
/// symlinked root is rejected as "outside external extract root". Miller passes symlink-resolved absolute paths
/// for BOTH root and file, always — and watches the canonical root so FileSystemWatcher events are already
/// canonical.
///
/// <para>The standard .NET surface is insufficient on its own: <see cref="Path.GetFullPath(string)"/> does NOT
/// resolve symlinks, and <see cref="File.ResolveLinkTarget(string, bool)"/> resolves a path only when the path
/// <em>itself</em> is a link — it returns null for a real leaf whose <em>ancestor</em> is a symlink. So this
/// canonicalizer walks the path component by component (like <c>realpath(3)</c>), resolving each symlink segment
/// as it descends, following link chains, and tolerating a non-existent tail (the just-deleted file a
/// <c>delete</c> must still target).</para>
/// </summary>
public static class PathCanonicalizer
{
    // Guard against a symlink cycle (a → b → a). realpath(3) caps at MAXSYMLINKS (typically 40); match the spirit.
    private const int MaxLinkHops = 64;

    /// <summary>
    /// Canonicalize the workspace root: make it absolute and fully resolve every symlink component. The root
    /// MUST exist (it is resolved once at startup against a real tree).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="root"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="root"/> is empty/whitespace.</exception>
    /// <exception cref="DirectoryNotFoundException">The directory does not exist after resolution.</exception>
    public static string CanonicalizeRoot(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        string full = Path.GetFullPath(root);
        string resolved = ResolveExistingPrefix(full, out string remainder);

        // The root must fully exist — no remainder is allowed (a missing root is an operator/config error).
        if (remainder.Length != 0 || !Directory.Exists(resolved))
            throw new DirectoryNotFoundException(
                $"Workspace root '{root}' (resolved '{full}') is not an existing directory. Miller " +
                "canonicalizes the root against a real tree at startup.");

        return resolved;
    }

    /// <summary>
    /// Canonicalize a per-file <c>--file</c> argument under an already-canonical <paramref name="canonicalRoot"/>.
    /// A relative <paramref name="path"/> composes under the canonical root (NOT the process CWD); an absolute
    /// path is taken as given. Every existing component's symlinks are resolved; a non-existent tail (a deleted
    /// file, or a path whose intermediate dirs are gone) is appended lexically so a <c>delete</c> still targets a
    /// path inside the resolved root.
    /// </summary>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public static string CanonicalizeFile(string canonicalRoot, string path)
    {
        ArgumentNullException.ThrowIfNull(canonicalRoot);
        ArgumentNullException.ThrowIfNull(path);

        // Relative file paths compose under the canonical root (the affected file lives in the workspace),
        // never the ambient CWD — Path.GetFullPath(path, basePath) does exactly that for an absolute base.
        string full = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(path, canonicalRoot);

        string resolved = ResolveExistingPrefix(full, out string remainder);
        return remainder.Length == 0 ? resolved : Path.Combine(resolved, remainder);
    }

    /// <summary>
    /// Walk <paramref name="absolutePath"/> from its root, resolving each symlink component, until a component
    /// does not exist. Returns the resolved existing prefix and, via <paramref name="remainder"/>, the
    /// not-yet-existing tail (empty if the whole path exists). <paramref name="absolutePath"/> must be absolute
    /// and already lexically normalized (the caller passes <see cref="Path.GetFullPath(string)"/> output).
    /// </summary>
    private static string ResolveExistingPrefix(string absolutePath, out string remainder)
    {
        string root = Path.GetPathRoot(absolutePath) ?? string.Empty;
        string rest = absolutePath.Substring(root.Length);
        string[] parts = rest.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        // Seed the walk at the filesystem root. Trim a trailing separator so Path.Combine composes cleanly,
        // but keep a bare "/" on POSIX.
        string current = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (current.Length == 0)
            current = root.Length > 0 ? root : Path.DirectorySeparatorChar.ToString();

        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            if (part == ".")
                continue;
            if (part == "..")
            {
                current = Path.GetDirectoryName(current) ?? current;
                continue;
            }

            string next = Path.Combine(current, part);

            // A component that exists neither as a dir nor a file ends the existing prefix: the rest is the
            // not-yet-existing tail (a deleted file / missing intermediate dir), appended lexically.
            bool isDir = Directory.Exists(next);
            bool isFile = !isDir && File.Exists(next);
            if (!isDir && !isFile)
            {
                remainder = string.Join(Path.DirectorySeparatorChar, parts[i..]);
                return current;
            }

            current = ResolveIfLink(next, current);
        }

        remainder = string.Empty;
        return current;
    }

    /// <summary>
    /// If <paramref name="path"/> is a symlink, return its fully resolved real target (following chains);
    /// otherwise return <paramref name="path"/> unchanged. <paramref name="linkDir"/> is the directory the link
    /// lives in, used to resolve a relative link target.
    /// </summary>
    private static string ResolveIfLink(string path, string linkDir)
    {
        string current = path;
        string dir = linkDir;
        for (int hop = 0; hop < MaxLinkHops; hop++)
        {
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            string? target = info.LinkTarget;
            if (target is null)
                return current; // not a link (or end of chain): the real path

            // A link target may be relative to the directory the link sits in.
            string resolvedTarget = Path.IsPathRooted(target)
                ? Path.GetFullPath(target)
                : Path.GetFullPath(Path.Combine(dir, target));

            // The target itself may have symlinked ancestors — resolve the whole thing, then continue the chain
            // in case the final component is ANOTHER link.
            string resolvedPrefix = ResolveExistingPrefix(resolvedTarget, out string rem);
            current = rem.Length == 0 ? resolvedPrefix : Path.Combine(resolvedPrefix, rem);
            dir = Path.GetDirectoryName(current) ?? current;
        }

        throw new IOException(
            $"Too many symbolic-link hops resolving '{path}' (possible cycle; capped at {MaxLinkHops}).");
    }
}
