using System.Text;
using Microsoft.Data.Sqlite;

namespace Miller.Indexing;

/// <summary>
/// Builds guarded SQLite JSON predicates for top-level <c>metadata_json</c> equality filters.
/// Preserves <see cref="PatternFactsReader"/> C# semantics: strings compare as strings; other JSON
/// values compare as raw JSON text.
/// </summary>
internal static class PatternMetadataSql
{
    public static bool TryAddMetadataFilters(
        SqliteCommand command,
        List<string> where,
        IReadOnlyList<PatternMetadataFilter> filters,
        ref int paramIndex)
    {
        foreach (PatternMetadataFilter filter in filters)
        {
            filter.Validate();
            if (!TryAddMetadataFilter(command, where, filter, ref paramIndex))
                return false;
        }

        return true;
    }

    public static bool TryAddMetadataFilter(
        SqliteCommand command,
        List<string> where,
        PatternMetadataFilter filter,
        ref int paramIndex)
    {
        if (!TryBuildJsonPath(filter.Key, out string jsonPath))
            return false;

        string pathParam = NextParam(ref paramIndex);
        string valueParam = NextParam(ref paramIndex);
        command.Parameters.AddWithValue(pathParam, jsonPath);
        command.Parameters.AddWithValue(valueParam, filter.Value);

        where.Add($"""
            metadata_json IS NOT NULL
            AND json_valid(metadata_json)
            AND CASE json_type(metadata_json, {pathParam})
                WHEN 'true' THEN 'true'
                WHEN 'false' THEN 'false'
                WHEN 'null' THEN 'null'
                ELSE CAST(json_extract(metadata_json, {pathParam}) AS TEXT)
            END = {valueParam}
            """);

        return true;
    }

    internal static bool TryBuildJsonPath(string key, out string jsonPath)
    {
        jsonPath = string.Empty;
        if (string.IsNullOrWhiteSpace(key))
            return false;

        string trimmed = key.Trim();
        if (!IsSafeMetadataKey(trimmed))
            return false;

        jsonPath = "$." + trimmed;
        return true;
    }

    private static bool IsSafeMetadataKey(string key)
    {
        foreach (char c in key)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
                continue;
            return false;
        }

        return key.Length > 0;
    }

    private static string NextParam(ref int paramIndex) => "$meta_" + paramIndex++;
}
