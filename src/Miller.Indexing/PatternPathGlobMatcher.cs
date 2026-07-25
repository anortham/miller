using System.Text;
using System.Text.RegularExpressions;

namespace Miller.Indexing;

/// <summary>
/// Workspace-relative path glob matching aligned with <c>ToolSearchFilters</c> semantics.
/// </summary>
internal static class PatternPathGlobMatcher
{
    public static Func<string, bool> Compile(string? pathGlob)
    {
        if (string.IsNullOrWhiteSpace(pathGlob))
            return static _ => true;

        string normalized = pathGlob.Replace('\\', '/').Trim();
        var matcher = new GlobMatcher(normalized);
        return matcher.IsMatch;
    }

    private sealed class GlobMatcher
    {
        private readonly Regex _regex;
        private readonly bool _containsSlash;

        public GlobMatcher(string pattern)
        {
            string normalized = NormalizePath(pattern);
            _containsSlash = normalized.Contains('/', StringComparison.Ordinal);
            _regex = new Regex("^" + GlobToRegex(normalized) + "$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
        }

        public bool IsMatch(string path)
        {
            string normalized = NormalizePath(path);
            if (_regex.IsMatch(normalized))
                return true;
            if (_containsSlash)
                return false;

            int lastSlash = normalized.LastIndexOf('/');
            string basename = lastSlash >= 0 ? normalized[(lastSlash + 1)..] : normalized;
            return _regex.IsMatch(basename);
        }

        private static string NormalizePath(string path) => path.Replace('\\', '/').Trim();

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
}
