using System.Text;
using System.Text.RegularExpressions;

namespace Miller.Server.Tools;

internal sealed class ToolSearchFilters
{
    private readonly GlobMatcher[] _filePatterns;
    private readonly HashSet<string>? _languages;

    private ToolSearchFilters(GlobMatcher[] filePatterns, HashSet<string>? languages, string? scopeDescription)
    {
        _filePatterns = filePatterns;
        _languages = languages;
        ScopeDescription = scopeDescription ?? "the requested scope";
    }

    public bool HasAny => _filePatterns.Length > 0 || _languages is not null;

    public string ScopeDescription { get; }

    public static ToolSearchFilters Parse(string? filePattern, string? language)
    {
        string[] filePatternParts = string.IsNullOrWhiteSpace(filePattern)
            ? Array.Empty<string>()
            : filePattern
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static pattern => pattern.Length > 0)
                .ToArray();
        GlobMatcher[] filePatterns = filePatternParts
            .Select(static pattern => new GlobMatcher(pattern))
            .ToArray();

        HashSet<string>? languages = null;
        var languageParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(language))
        {
            languages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string part in language.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (part.Length > 0)
                {
                    languages.Add(part);
                    languageParts.Add(part);
                }
            }
            if (languages.Count == 0)
                languages = null;
        }

        var scopeParts = new List<string>(capacity: 2);
        if (filePatternParts.Length > 0)
            scopeParts.Add("file_pattern=" + string.Join(",", filePatternParts));
        if (languageParts.Count > 0)
            scopeParts.Add("language=" + string.Join(",", languageParts));

        return new ToolSearchFilters(
            filePatterns,
            languages,
            scopeParts.Count == 0 ? null : string.Join(", ", scopeParts));
    }

    public bool Allows(string path, string language)
    {
        if (_filePatterns.Length > 0 && !_filePatterns.Any(pattern => pattern.IsMatch(path)))
            return false;
        if (_languages is not null && !_languages.Contains(language))
            return false;
        return true;
    }

    private sealed class GlobMatcher
    {
        private readonly Regex _regex;
        private readonly bool _containsSlash;

        public GlobMatcher(string pattern)
        {
            string normalized = NormalizePath(pattern);
            _containsSlash = normalized.Contains('/', StringComparison.Ordinal);
            _regex = new Regex("^" + GlobToRegex(normalized) + "$", RegexOptions.CultureInvariant);
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
