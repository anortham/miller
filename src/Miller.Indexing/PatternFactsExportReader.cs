using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Miller.Indexing;

/// <summary>
/// The <c>patterns export --jsonl</c> feed: one deterministic JSON line per <c>structural_facts</c> row.
/// </summary>
public static class PatternFactsExportReader
{
    public const int SchemaVersion = 2;

    public static string ExportJsonLines(string symbolsDbPath)
    {
        using var writer = new StringWriter();
        WriteJsonLines(symbolsDbPath, writer);
        return writer.ToString();
    }

    public static void WriteJsonLines(string symbolsDbPath, TextWriter writer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolsDbPath);
        ArgumentNullException.ThrowIfNull(writer);

        using SqliteConnection connection = SqliteReadOnlyAccess.Open(symbolsDbPath);
        JulieSchemaGate.Verify(connection);
        if (!TableExists(connection, "structural_facts"))
            throw new InvalidOperationException("table 'structural_facts' is missing");

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT structural_fact_id, pattern_id, language, path, capture_name, node_kind,
                   containing_symbol_id, start_line, start_column, end_line, end_column,
                   start_byte, end_byte, confidence, metadata_json
            FROM structural_facts
            ORDER BY path, start_byte, structural_fact_id;
            """;

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            writer.Write(RenderRow(reader));
            writer.Write('\n');
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

    private static string RenderRow(SqliteDataReader reader)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer,
            new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", SchemaVersion);
            writer.WriteString("structural_fact_id", reader.GetString(0));
            writer.WriteString("pattern_id", reader.GetString(1));
            writer.WriteString("language", reader.GetString(2));
            writer.WriteString("path", reader.GetString(3));
            writer.WriteString("capture_name", reader.GetString(4));
            writer.WriteString("node_kind", reader.GetString(5));
            ExportJson.WriteNullableString(writer, "containing_symbol_id", reader, 6);
            writer.WriteNumber("start_line", reader.GetInt32(7));
            writer.WriteNumber("start_column", reader.GetInt32(8));
            writer.WriteNumber("end_line", reader.GetInt32(9));
            writer.WriteNumber("end_column", reader.GetInt32(10));
            writer.WriteNumber("start_byte", reader.GetInt32(11));
            writer.WriteNumber("end_byte", reader.GetInt32(12));
            writer.WriteNumber("confidence", reader.GetDouble(13));
            ExportJson.WriteNullableString(writer, "metadata_json", reader, 14);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
