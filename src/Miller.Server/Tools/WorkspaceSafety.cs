using Miller.Indexing;

namespace Miller.Server.Tools;

/// <summary>
/// The PURE safety predicate behind <c>workspace remove</c> (M7 decision-1/8). <see cref="IsLiveWorkspace"/>
/// answers "does this candidate path resolve to the workspace this process is currently serving?" so the tool can
/// REFUSE to delete the in-use <c>.miller</c> dir while still allowing the cleanup of any OTHER workspace's index.
/// The comparison is CANONICAL — both paths are symlink-resolved via <see cref="PathCanonicalizer"/> (so a
/// trailing slash, a <c>./</c> segment, or a symlink alias to the live root is still recognised) — degrading to a
/// lexical full-path compare when the candidate does not exist on disk (a never-served path can never be the live
/// one, and the canonicalizer's filesystem walk must not throw into the predicate). No I/O beyond the read-only
/// path walk; deterministic; unit-tested.
/// </summary>
public static class WorkspaceSafety
{
    /// <summary>
    /// True iff <paramref name="candidateRoot"/> resolves to the same workspace root as
    /// <paramref name="liveRoot"/> (the workspace this process serves). Both are canonicalized (symlink-resolved)
    /// before comparison so cosmetic or symlink differences cannot let a half-delete of the in-use index slip
    /// through; if canonicalization of the candidate fails (e.g. it does not exist), it falls back to a lexical
    /// <see cref="Path.GetFullPath(string)"/> compare — a candidate that does not exist is, by definition, not the
    /// live workspace, so the predicate stays honest rather than throwing.
    /// </summary>
    /// <exception cref="ArgumentException">Either argument is null/blank.</exception>
    public static bool IsLiveWorkspace(string candidateRoot, string liveRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(liveRoot);

        string canonicalLive = Canonicalize(liveRoot);
        string canonicalCandidate = Canonicalize(candidateRoot);

        // Path comparison is OS-sensitive: ordinal on the case-sensitive POSIX filesystems, case-insensitive on
        // Windows/macOS-default. Use the platform's own rule so an alias differing only in case is still caught.
        return string.Equals(canonicalCandidate, canonicalLive, PathComparison);
    }

    // Canonicalize when the path exists (symlink-resolved, the strongest comparison); otherwise fall back to a
    // lexical absolute path. CanonicalizeRoot throws on a non-existent dir, which for the candidate simply means
    // "not the live workspace" — never a fault in the predicate.
    private static string Canonicalize(string path)
    {
        try
        {
            return TrimTrailingSeparators(PathCanonicalizer.CanonicalizeRoot(path));
        }
        catch (DirectoryNotFoundException)
        {
            return TrimTrailingSeparators(Path.GetFullPath(path));
        }
    }

    private static string TrimTrailingSeparators(string path)
    {
        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // Keep a bare root ("/" on POSIX, "C:\" on Windows) rather than collapsing it to empty.
        return trimmed.Length == 0 ? path : trimmed;
    }

    // POSIX filesystems are case-sensitive; Windows and the default macOS volume are not. Match the host so a
    // case-only alias to the live root is recognised on the platforms where it would collide on disk.
    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
