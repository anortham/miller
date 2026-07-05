using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Miller.Indexing;

/// <summary>
/// The <c>complexity export --jsonl</c> feed (cli-eros-v1): one deterministic JSON line per
/// <c>complexity_metrics</c> row (emitted broadly since julie-extract 2.3.0; previously consumed only at the
/// <c>workspace health</c> aggregate level), so a fleet orchestrator can rank hotspots fleet-wide. Rows are
/// ordered <c>(path, start_byte, complexity_metric_id)</c> so re-exporting an unchanged artifact is
/// byte-identical. The D5 schema gate runs before the read (incompatible artifact ⇒ CLI exit 3).
/// </summary>
public static class ComplexityExportReader
{
    public const int SchemaVersion = 1;

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

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT complexity_metric_id, path, language, scope, symbol_id, algorithm_id,
                   covered_lines, covered_bytes, decision_count, loop_count, max_nesting_depth,
                   parameter_count, start_line, end_line, start_byte, end_byte
            FROM complexity_metrics
            ORDER BY path, start_byte, complexity_metric_id;
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            writer.Write(RenderRow(reader));
            writer.Write('\n');
        }
    }

    private static string RenderRow(SqliteDataReader reader)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer,
            new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteNumber("schema_version", SchemaVersion);
            w.WriteString("complexity_metric_id", reader.GetString(0));
            w.WriteString("path", reader.GetString(1));
            w.WriteString("language", reader.GetString(2));
            w.WriteString("scope", reader.GetString(3));
            ExportJson.WriteNullableString(w, "symbol_id", reader, 4);
            w.WriteString("algorithm_id", reader.GetString(5));
            w.WriteNumber("covered_lines", reader.GetInt64(6));
            w.WriteNumber("covered_bytes", reader.GetInt64(7));
            w.WriteNumber("decision_count", reader.GetInt64(8));
            w.WriteNumber("loop_count", reader.GetInt64(9));
            w.WriteNumber("max_nesting_depth", reader.GetInt64(10));
            ExportJson.WriteNullableLong(w, "parameter_count", reader, 11);
            w.WriteNumber("start_line", reader.GetInt64(12));
            w.WriteNumber("end_line", reader.GetInt64(13));
            w.WriteNumber("start_byte", reader.GetInt64(14));
            w.WriteNumber("end_byte", reader.GetInt64(15));
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
