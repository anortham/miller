using Microsoft.Data.Sqlite;

namespace Miller.Indexing;

/// <summary>
/// Translates workspace-relative path globs into SQL predicates that match
/// <c>Miller.Server.Tools.ToolSearchFilters</c> glob semantics for common patterns.
/// Returns false when the glob cannot be represented exactly in SQL.
/// </summary>
internal static class PatternPathGlobSql
{
    public static bool TryAddPathPredicate(
        SqliteCommand command,
        List<string> where,
        string pathGlob,
        ref int paramIndex)
    {
        if (string.IsNullOrWhiteSpace(pathGlob))
            return true;

        string normalized = pathGlob.Replace('\\', '/').Trim();
        if (normalized.Length == 0)
            return true;

        if (!normalized.Contains('*', StringComparison.Ordinal)
            && !normalized.Contains('?', StringComparison.Ordinal))
        {
            string exactParam = NextParam(ref paramIndex);
            where.Add($"path = {exactParam}");
            command.Parameters.AddWithValue(exactParam, normalized);
            return true;
        }

        if (normalized.EndsWith("/**", StringComparison.Ordinal)
            && normalized.Length > 3
            && !normalized[..^3].Contains('*', StringComparison.Ordinal)
            && !normalized[..^3].Contains('?', StringComparison.Ordinal))
        {
            string prefix = normalized[..^3];
            string likeParam = NextParam(ref paramIndex);
            where.Add($"path LIKE {likeParam} ESCAPE '\\'");
            command.Parameters.AddWithValue(likeParam, EscapeLike(prefix) + "/%");
            return true;
        }

        if (normalized.StartsWith("**/", StringComparison.Ordinal)
            && normalized.Length > 3
            && !normalized[3..].Contains('*', StringComparison.Ordinal)
            && !normalized[3..].Contains('?', StringComparison.Ordinal))
        {
            string suffix = normalized[3..];
            string likeParam = NextParam(ref paramIndex);
            string exactParam = NextParam(ref paramIndex);
            where.Add($"(path LIKE {likeParam} ESCAPE '\\' OR path = {exactParam})");
            command.Parameters.AddWithValue(likeParam, "%/" + EscapeLike(suffix));
            command.Parameters.AddWithValue(exactParam, suffix);
            return true;
        }

        return false;
    }

    private static string EscapeLike(string pattern) =>
        pattern.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static string NextParam(ref int paramIndex) => "$path_" + paramIndex++;
}
