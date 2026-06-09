using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Miller.Indexing;

/// <summary>
/// Read-only projection over julie-extractors' structural_facts table.
/// </summary>
public sealed class PatternFactsReader
{
    public IReadOnlyList<PatternListRow> List(string dbPath, string? language = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);

        using SqliteConnection connection = OpenStructuralFacts(dbPath);
        using SqliteCommand command = connection.CreateCommand();
        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(language))
        {
            where.Add("language = $language");
            command.Parameters.AddWithValue("$language", language.Trim());
        }

        command.CommandText = $"""
            SELECT pattern_id, language, capture_name, COUNT(*) AS count
            FROM structural_facts
            {WhereClause(where)}
            GROUP BY pattern_id, language, capture_name
            ORDER BY pattern_id, language, capture_name;
            """;

        var grouped = new Dictionary<string, PatternListAccumulator>(StringComparer.Ordinal);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string patternId = reader.GetString(0);
            if (!grouped.TryGetValue(patternId, out PatternListAccumulator? acc))
            {
                acc = new PatternListAccumulator(patternId);
                grouped.Add(patternId, acc);
            }

            acc.Languages.Add(reader.GetString(1));
            acc.Captures.Add(reader.GetString(2));
            acc.Count += reader.GetInt64(3);
        }

        return grouped.Values
            .OrderBy(static row => row.PatternId, StringComparer.Ordinal)
            .Select(static row => new PatternListRow(
                row.PatternId,
                Label: row.PatternId,
                Languages: row.Languages.OrderBy(static value => value, StringComparer.Ordinal).ToArray(),
                Captures: row.Captures.OrderBy(static value => value, StringComparer.Ordinal).ToArray(),
                Count: row.Count,
                Catalog: "observed"))
            .ToArray();
    }

    public IReadOnlyList<PatternSummaryRow> Summary(string dbPath, string? patternId, string? language)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);

        using SqliteConnection connection = OpenStructuralFacts(dbPath);
        using SqliteCommand command = connection.CreateCommand();
        List<string> where = AddFactFilters(command, patternId, language);

        command.CommandText = $"""
            SELECT language, pattern_id, capture_name, COUNT(*) AS count
            FROM structural_facts
            {WhereClause(where)}
            GROUP BY language, pattern_id, capture_name
            ORDER BY language, pattern_id, capture_name;
            """;

        var rows = new List<PatternSummaryRow>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new PatternSummaryRow(
                Language: reader.GetString(0),
                PatternId: reader.GetString(1),
                CaptureName: reader.GetString(2),
                Count: reader.GetInt64(3)));
        }

        return rows;
    }

    public IReadOnlyList<PatternMatchRow> Search(
        string dbPath,
        string patternId,
        string? language,
        PatternMetadataFilter? metadataFilter,
        int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(patternId);

        return Matches(dbPath, patternId, language, metadataFilter, limit);
    }

    public IReadOnlyList<PatternMatchRow> Matches(
        string dbPath,
        string? patternId,
        string? language,
        PatternMetadataFilter? metadataFilter,
        int? limit = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        if (metadataFilter is not null)
            metadataFilter.Validate();

        int? boundedLimit = limit is null ? null : Math.Clamp(limit.Value, 1, 500);
        using SqliteConnection connection = OpenStructuralFacts(dbPath);
        using SqliteCommand command = connection.CreateCommand();
        List<string> where = AddFactFilters(command, patternId, language);

        command.CommandText = $"""
            SELECT structural_fact_id, pattern_id, language, path, capture_name, node_kind,
                   containing_symbol_id, start_line, start_column, end_line, end_column,
                   start_byte, end_byte, confidence, metadata_json
            FROM structural_facts
            {WhereClause(where)}
            ORDER BY path, start_byte, structural_fact_id;
            """;

        var rows = new List<PatternMatchRow>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            PatternMatchRow row = ReadMatch(reader);
            if (metadataFilter is not null && !MetadataMatches(row, metadataFilter))
                continue;

            rows.Add(row);
            if (boundedLimit is not null && rows.Count >= boundedLimit.Value)
                break;
        }

        return rows;
    }

    private static SqliteConnection OpenStructuralFacts(string dbPath)
    {
        SqliteConnection connection = SqliteReadOnlyAccess.Open(dbPath);
        try
        {
            JulieSchemaGate.Verify(connection);
            if (!TableExists(connection, "structural_facts"))
                throw new InvalidOperationException("table 'structural_facts' is missing");
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", tableName);
        object? result = command.ExecuteScalar();
        return result is not null and not DBNull;
    }

    private static List<string> AddFactFilters(SqliteCommand command, string? patternId, string? language)
    {
        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(patternId))
        {
            where.Add("pattern_id = $pattern_id");
            command.Parameters.AddWithValue("$pattern_id", patternId.Trim());
        }

        if (!string.IsNullOrWhiteSpace(language))
        {
            where.Add("language = $language");
            command.Parameters.AddWithValue("$language", language.Trim());
        }

        return where;
    }

    private static string WhereClause(IReadOnlyCollection<string> where) =>
        where.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", where);

    private static PatternMatchRow ReadMatch(SqliteDataReader reader)
    {
        string? metadataJson = reader.IsDBNull(14) ? null : reader.GetString(14);
        JsonElement metadata = default;
        string? metadataError = null;
        if (!string.IsNullOrWhiteSpace(metadataJson))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(metadataJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    metadata = doc.RootElement.Clone();
                else
                    metadataError = "metadata_json root is not an object";
            }
            catch (JsonException ex)
            {
                metadataError = ex.Message;
            }
        }

        return new PatternMatchRow(
            FactId: reader.GetString(0),
            PatternId: reader.GetString(1),
            Language: reader.GetString(2),
            Path: reader.GetString(3),
            CaptureName: reader.GetString(4),
            NodeKind: reader.GetString(5),
            ContainingSymbolId: reader.IsDBNull(6) ? null : reader.GetString(6),
            Span: new PatternSpan(
                StartLine: reader.GetInt32(7),
                StartColumn: reader.GetInt32(8),
                EndLine: reader.GetInt32(9),
                EndColumn: reader.GetInt32(10),
                StartByte: reader.GetInt32(11),
                EndByte: reader.GetInt32(12)),
            Confidence: reader.GetDouble(13),
            MetadataJson: metadataJson,
            Metadata: metadata,
            MetadataError: metadataError);
    }

    private static bool MetadataMatches(PatternMatchRow row, PatternMetadataFilter filter)
    {
        if (row.MetadataError is not null || row.Metadata.ValueKind != JsonValueKind.Object)
            return false;
        if (!row.Metadata.TryGetProperty(filter.Key, out JsonElement value))
            return false;

        string actual = value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : value.GetRawText();
        return string.Equals(actual, filter.Value, StringComparison.Ordinal);
    }

    private sealed class PatternListAccumulator(string patternId)
    {
        public string PatternId { get; } = patternId;
        public HashSet<string> Languages { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Captures { get; } = new(StringComparer.Ordinal);
        public long Count { get; set; }
    }
}

public sealed record PatternListRow(
    string PatternId,
    string Label,
    IReadOnlyList<string> Languages,
    IReadOnlyList<string> Captures,
    long Count,
    string Catalog);

public sealed record PatternSummaryRow(string Language, string PatternId, string CaptureName, long Count);

public sealed record PatternMatchRow(
    string FactId,
    string PatternId,
    string Language,
    string Path,
    string CaptureName,
    string NodeKind,
    string? ContainingSymbolId,
    PatternSpan Span,
    double Confidence,
    string? MetadataJson,
    JsonElement Metadata,
    string? MetadataError);

public sealed record PatternSpan(
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn,
    int StartByte,
    int EndByte);

public sealed record PatternMetadataFilter(string Key, string Value)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Key);
    }
}
