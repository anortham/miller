using System.Text;
using System.Text.RegularExpressions;
using Miller.Indexing;

namespace Miller.Server.Hosting;

/// <summary>
/// Watcher-side ignore-file matcher for the same policy surface delegated full scans already get from
/// <c>julie-extract</c>. This is deliberately scoped to deciding whether a live file-system event should be
/// dispatched; julie remains the source of truth for final indexability.
/// </summary>
internal static class WorkspaceIgnorePolicy
{
    private static readonly char[] SeparatorChars = ['/', '\\'];

    public static bool IsIgnored(string root, string absolutePath)
        => IsIgnored(root, absolutePath, MillerHome.ResolveMillerDirectory());

    internal static bool IsIgnored(string root, string absolutePath, string millerDirectory)
    {
        string fullRoot;
        string fullPath;
        try
        {
            fullRoot = Path.GetFullPath(root);
            fullPath = Path.GetFullPath(absolutePath);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }

        if (!IsUnderOrEqual(fullRoot, fullPath))
            return true;

        string targetDirectory = Directory.Exists(fullPath)
            ? fullPath
            : Path.GetDirectoryName(fullPath) ?? fullRoot;

        var ignored = false;
        foreach (var rule in LoadRules(fullRoot, targetDirectory, millerDirectory))
        {
            if (rule.IsMatch(fullPath))
                ignored = !rule.Negated;
        }
        return ignored;
    }

    public static bool IsOutsideRoot(string root, string absolutePath)
    {
        try
        {
            return !IsUnderOrEqual(Path.GetFullPath(root), Path.GetFullPath(absolutePath));
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    public static IReadOnlyList<string> AncestorGitignoreFilesOutsideRoot(string root)
    {
        string fullRoot;
        try
        {
            fullRoot = Path.GetFullPath(root);
        }
        catch (ArgumentException)
        {
            return Array.Empty<string>();
        }
        catch (NotSupportedException)
        {
            return Array.Empty<string>();
        }

        string? gitRoot = FindGitRoot(fullRoot);
        string? parent = Path.GetDirectoryName(fullRoot);
        if (gitRoot is null || parent is null || string.Equals(gitRoot, fullRoot, PathComparison))
            return Array.Empty<string>();
        if (!IsUnderOrEqual(gitRoot, fullRoot))
            return Array.Empty<string>();

        return DirectoriesBetween(gitRoot, parent)
            .Where(directory => !IsUnderOrEqual(fullRoot, directory))
            .Select(directory => Path.Combine(directory, ".gitignore"))
            .ToArray();
    }

    private static IEnumerable<IgnoreRule> LoadRules(
        string fullRoot, string targetDirectory, string millerDirectory)
    {
        string gitRoot = FindGitRoot(fullRoot) ?? fullRoot;
        if (!IsUnderOrEqual(gitRoot, targetDirectory))
            gitRoot = fullRoot;

        foreach (string directory in DirectoriesBetween(gitRoot, targetDirectory))
        {
            foreach (var rule in LoadFile(Path.Combine(directory, ".gitignore"), directory))
                yield return rule;

            if (IsUnderOrEqual(fullRoot, directory))
            {
                foreach (var rule in LoadFile(Path.Combine(directory, ".julieignore"), directory))
                    yield return rule;
            }
        }

        if (!File.Exists(Path.Combine(fullRoot, JulieIgnoreSeeder.WorkspaceIgnoreFileName))
            && JulieIgnoreSeeder.ResolveInheritedIgnoreFile(fullRoot) is null)
        {
            string generated = JulieIgnoreSeeder.GeneratedGlobalIgnorePathForWorkspaceId(
                WorkspaceId.FromCanonicalRoot(fullRoot), millerDirectory);
            foreach (var rule in LoadFile(generated, fullRoot))
                yield return rule;
        }
    }

    private static string? FindGitRoot(string fullRoot)
    {
        for (string? directory = fullRoot; directory is not null; directory = Path.GetDirectoryName(directory))
        {
            string marker = Path.Combine(directory, ".git");
            if (Directory.Exists(marker) || File.Exists(marker))
                return directory;
        }
        return null;
    }

    private static IEnumerable<string> DirectoriesBetween(string baseDirectory, string targetDirectory)
    {
        yield return baseDirectory;

        string relative = Path.GetRelativePath(baseDirectory, targetDirectory);
        if (relative.Length == 0 || relative == ".")
            yield break;
        if (IsParentRelative(relative))
            yield break;

        string current = baseDirectory;
        foreach (string segment in relative.Split(SeparatorChars, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            yield return current;
        }
    }

    private static IEnumerable<IgnoreRule> LoadFile(string path, string baseDirectory)
    {
        if (!File.Exists(path))
            yield break;

        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (IOException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (string rawLine in lines)
        {
            if (TryParseRule(baseDirectory, rawLine, out var rule))
                yield return rule;
        }
    }

    private static bool TryParseRule(string baseDirectory, string rawLine, out IgnoreRule rule)
    {
        rule = default;

        string line = rawLine.Trim();
        if (line.Length == 0)
            return false;

        if (line[0] == '#')
            return false;
        if (line.StartsWith("\\#", StringComparison.Ordinal) || line.StartsWith("\\!", StringComparison.Ordinal))
            line = line[1..];

        bool negated = false;
        if (line.StartsWith('!'))
        {
            negated = true;
            line = line[1..].Trim();
        }

        bool directoryOnly = line.EndsWith("/", StringComparison.Ordinal);
        line = line.TrimEnd('/');
        if (line.Length == 0)
            return false;

        rule = new IgnoreRule(baseDirectory, line, negated, directoryOnly);
        return true;
    }

    private static bool IsUnderOrEqual(string root, string path)
    {
        string normalizedRoot = NormalizeAbsolute(root);
        string normalizedPath = NormalizeAbsolute(path);

        if (string.Equals(normalizedRoot, normalizedPath, PathComparison))
            return true;
        if (!normalizedRoot.EndsWith("/", StringComparison.Ordinal))
            normalizedRoot += "/";

        return normalizedPath.StartsWith(normalizedRoot, PathComparison);
    }

    private static string NormalizeAbsolute(string path) =>
        Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/');

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly struct IgnoreRule
    {
        private readonly string _baseDirectory;
        private readonly Regex _regex;
        private readonly bool _containsSlash;

        public IgnoreRule(string baseDirectory, string pattern, bool negated, bool directoryOnly)
        {
            _baseDirectory = NormalizeAbsolute(baseDirectory);
            Negated = negated;
            DirectoryOnly = directoryOnly;

            string normalized = pattern.Replace('\\', '/');
            Anchored = normalized.StartsWith("/", StringComparison.Ordinal);
            if (Anchored)
                normalized = normalized[1..];

            _containsSlash = normalized.Contains('/', StringComparison.Ordinal);
            _regex = new Regex("^" + GlobToRegex(normalized) + "$", RegexOptions.CultureInvariant);
        }

        public bool Negated { get; }

        private bool Anchored { get; }

        private bool DirectoryOnly { get; }

        public bool IsMatch(string fullPath)
        {
            string relative = NormalizeRelative(_baseDirectory, fullPath);
            if (relative.Length == 0 || IsParentRelative(relative))
                return false;

            string[] segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (!_containsSlash && !Anchored)
            {
                foreach (string segment in segments)
                {
                    if (_regex.IsMatch(segment))
                        return true;
                }
                return false;
            }

            foreach (string prefix in PathPrefixes(relative))
            {
                if (_regex.IsMatch(prefix))
                    return true;
            }

            return DirectoryOnly && _regex.IsMatch(relative.TrimEnd('/'));
        }

        private static IEnumerable<string> PathPrefixes(string relative)
        {
            yield return relative;

            int slash = relative.LastIndexOf('/');
            while (slash > 0)
            {
                relative = relative[..slash];
                yield return relative;
                slash = relative.LastIndexOf('/');
            }
        }

        private static string NormalizeRelative(string baseDirectory, string fullPath)
        {
            string relative = Path.GetRelativePath(baseDirectory, Path.GetFullPath(fullPath));
            return relative.Replace('\\', '/').Trim('/');
        }

        private static string GlobToRegex(string pattern)
        {
            var sb = new StringBuilder(pattern.Length * 2);
            for (int i = 0; i < pattern.Length; i++)
            {
                char c = pattern[i];
                if (c == '*')
                {
                    bool globstar = i + 1 < pattern.Length && pattern[i + 1] == '*';
                    if (globstar)
                    {
                        sb.Append(".*");
                        i++;
                    }
                    else
                    {
                        sb.Append("[^/]*");
                    }
                }
                else if (c == '?')
                {
                    sb.Append("[^/]");
                }
                else
                {
                    sb.Append(Regex.Escape(c.ToString()));
                }
            }
            return sb.ToString();
        }
    }

    private static bool IsParentRelative(string relative) =>
        relative == ".."
        || relative.StartsWith("../", PathComparison)
        || relative.StartsWith(@"..\", PathComparison);
}
