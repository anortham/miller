using Microsoft.Data.Sqlite;
using Miller.Core.Search;

namespace Miller.Indexing;

public static class MarkerFactReader
{
    public const string PatternId = "code.marker.v1";

    public static IReadOnlyList<MarkerFactRow> Read(
        string dbPath,
        bool excludeTests,
        int limit,
        Func<MarkerFactRow, bool>? predicate = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        int boundedLimit = Math.Clamp(limit, 1, 500);

        using SqliteConnection connection = SqliteReadOnlyAccess.Open(dbPath);
        JulieSchemaGate.Verify(connection);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT sf.structural_fact_id,
                   json_extract(sf.metadata_json, '$.marker'),
                   json_extract(sf.metadata_json, '$.owner'),
                   json_extract(sf.metadata_json, '$.description'),
                   sf.language, sf.path, sf.node_kind, sf.containing_symbol_id,
                   s.name, COALESCE(s.is_test, 0),
                   sf.start_line, sf.start_column, sf.end_line, sf.end_column,
                   sf.start_byte, sf.end_byte
            FROM structural_facts sf
            LEFT JOIN symbols s ON s.symbol_id = sf.containing_symbol_id
            WHERE sf.pattern_id = $pattern
              AND json_valid(sf.metadata_json)
              AND json_type(sf.metadata_json, '$.marker') = 'text'
            ORDER BY sf.path, sf.start_byte, sf.structural_fact_id;
            """;
        command.Parameters.AddWithValue("$pattern", PatternId);

        var rows = new List<MarkerFactRow>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read() && rows.Count < boundedLimit)
        {
            string path = reader.GetString(5);
            bool containingSymbolIsTest = reader.GetInt32(9) != 0;
            if (excludeTests && (containingSymbolIsTest || TestPathClassifier.Check(path)))
                continue;

            var row = new MarkerFactRow(
                FactId: reader.GetString(0),
                Marker: reader.GetString(1),
                Owner: reader.IsDBNull(2) ? null : reader.GetString(2),
                Description: reader.IsDBNull(3) ? null : reader.GetString(3),
                Language: reader.GetString(4),
                Path: path,
                NodeKind: reader.GetString(6),
                ContainingSymbolId: reader.IsDBNull(7) ? null : reader.GetString(7),
                ContainingSymbolName: reader.IsDBNull(8) ? null : reader.GetString(8),
                StartLine: reader.GetInt32(10),
                StartColumn: reader.GetInt32(11),
                EndLine: reader.GetInt32(12),
                EndColumn: reader.GetInt32(13),
                StartByte: reader.GetInt32(14),
                EndByte: reader.GetInt32(15));
            if (predicate is null || predicate(row))
                rows.Add(row);
        }

        return rows;
    }
}

public sealed record MarkerFactRow(
    string FactId,
    string Marker,
    string? Owner,
    string? Description,
    string Language,
    string Path,
    string NodeKind,
    string? ContainingSymbolId,
    string? ContainingSymbolName,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn,
    int StartByte,
    int EndByte);
