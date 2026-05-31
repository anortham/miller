using System.Globalization;

namespace Miller.Server.Tools;

/// <summary>
/// Refuses to use an "obviously sensitive" directory as a Miller workspace root — the home directory, a
/// filesystem/drive root, or a platform system directory (Windows\System32, Program Files, /var/root, …). This
/// is the guard against a launcher (an IDE, a GUI agent, a misconfigured shell) starting the MCP server with its
/// cwd set to <c>/</c>, <c>~</c>, or <c>C:\Windows\System32</c> — which would otherwise kick off a full
/// julie-server scan of the entire home/system tree. Ported from julie's <c>workspace/root_safety.rs</c> (the
/// product family's authoritative behavior; see CLAUDE.md "consume julie's signals") so Miller, julie, and eros
/// reject the same set.
///
/// <para>The decision splits into a PURE predicate (<see cref="IsSensitiveRoot"/>, given a candidate + an
/// explicit forbidden list — fully unit-testable with synthetic inputs) and the environment-derived forbidden
/// list (<see cref="SensitiveRootCandidates"/>, which reads the home dir + system env). Only EXACT roots are
/// rejected, never their children: a project under the home dir (e.g. <c>~/src/app</c>) is fine; <c>~</c> itself
/// is not. Comparison is OS-sensitive (case-insensitive on Windows / the default macOS volume, ordinal on the
/// case-sensitive POSIX filesystems), matching <see cref="WorkspaceSafety"/>.</para>
/// </summary>
public static class WorkspaceRootSafety
{
    /// <summary>
    /// Throw an <see cref="InvalidOperationException"/> with actionable guidance if <paramref name="root"/> is a
    /// sensitive system path; otherwise return. <paramref name="fromCwd"/> tailors the message: the cwd path
    /// (the MCP server was launched in a sensitive dir) points the operator at launching from a project dir,
    /// while an explicit-path call (a <c>workspace open</c> argument) points at choosing a narrower path.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="root"/> is null/blank.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="root"/> is a sensitive root.</exception>
    public static void RejectSensitiveRoot(string root, bool fromCwd)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!IsSensitiveRoot(root, SensitiveRootCandidates()))
            return;

        string full = Path.GetFullPath(root);
        string remedy = fromCwd
            ? "Launch the Miller MCP server from a project directory (its cwd becomes the workspace root)."
            : "Choose a project directory or pass a narrower path.";
        throw new InvalidOperationException(
            $"Refusing to use sensitive system path '{full}' as a Miller workspace root. {remedy}");
    }

    /// <summary>
    /// True iff <paramref name="candidate"/> is a filesystem/drive root (no parent) OR normalizes to EXACTLY one
    /// of <paramref name="forbidden"/>. Pure: no environment reads, no filesystem I/O beyond
    /// <see cref="Path.GetFullPath(string)"/> normalization. Children of a forbidden root are NOT sensitive.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="candidate"/> is null/blank.</exception>
    public static bool IsSensitiveRoot(string candidate, IReadOnlyCollection<string> forbidden)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate);
        ArgumentNullException.ThrowIfNull(forbidden);

        string normCandidate = Normalize(candidate);

        // A filesystem/drive root has no parent directory — "/" on POSIX, "C:\" on Windows. Always sensitive.
        if (Path.GetDirectoryName(normCandidate) is null)
            return true;

        foreach (string entry in forbidden)
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;
            if (string.Equals(normCandidate, Normalize(entry), PathComparison))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The environment-derived list of sensitive roots: the user's home directory plus the platform's system
    /// directories. Resolved at runtime via <see cref="OperatingSystem"/> checks (not compile-time) so the single
    /// cross-platform binary rejects the right set on whatever host it runs. On Windows the system drive + the
    /// known-folder env vars (SystemRoot/ProgramFiles/…) are honoured so installs off <c>C:</c> are still caught.
    /// </summary>
    public static IReadOnlyList<string> SensitiveRootCandidates()
    {
        var forbidden = new List<string>();

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
            forbidden.Add(home);

        if (OperatingSystem.IsMacOS())
        {
            forbidden.Add("/Users");
            forbidden.Add("/var/root");
            forbidden.Add("/private/var/root");
        }
        else if (OperatingSystem.IsLinux())
        {
            forbidden.Add("/home");
            forbidden.Add("/root");
        }
        else if (OperatingSystem.IsWindows())
        {
            AddWindowsSensitiveCandidates(forbidden);
        }

        return forbidden;
    }

    private static void AddWindowsSensitiveCandidates(List<string> forbidden)
    {
        string systemDrive = Environment.GetEnvironmentVariable("SystemDrive") is { Length: > 0 } sd
            ? sd
            : "C:";
        string driveRoot = systemDrive.TrimEnd('\\') + "\\";

        forbidden.Add(driveRoot + "Users");
        forbidden.Add(driveRoot + "Windows");
        forbidden.Add(driveRoot + "Windows\\System32");
        forbidden.Add(driveRoot + "Program Files");
        forbidden.Add(driveRoot + "Program Files (x86)");
        forbidden.Add(driveRoot + "ProgramData");

        foreach (string key in new[]
                 {
                     "SystemRoot", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "ProgramData", "PUBLIC",
                 })
        {
            if (Environment.GetEnvironmentVariable(key) is { Length: > 0 } value)
                forbidden.Add(value);
        }
    }

    // Lexical normalization: absolute, no trailing separator (but a bare root stays a root so GetDirectoryName
    // still reports null for it). Symlink resolution is deliberately NOT done here — the call sites pass an
    // already-canonicalized root (bootstrap canonicalizes cwd; workspace-open canonicalizes its argument), and
    // keeping this pure lets the predicate be unit-tested without touching the filesystem.
    private static string Normalize(string path)
    {
        string full = Path.GetFullPath(path);
        string trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // A bare root trims to empty (POSIX "/") or a drive letter ("C:") — keep the original so it still reads
        // as a root (GetDirectoryName(null-parent)) rather than collapsing to something with a parent.
        return trimmed.Length == 0 || trimmed.EndsWith(':') ? full : trimmed;
    }

    // POSIX filesystems are case-sensitive; Windows and the default macOS volume are not. Match the host so a
    // case-only alias to a sensitive root is still caught where it would collide on disk (mirrors WorkspaceSafety).
    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
