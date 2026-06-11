using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Miller.Indexing;

/// <summary>
/// The <c>symbols export --jsonl</c> feed (cli-eros-v1): one deterministic JSON line per <c>symbols</c> row, so
/// a fleet orchestrator can build per-repo symbol rollups (counts, kinds, doc coverage, clone candidates via
/// <c>body_hash</c>) without per-file <c>inspect</c> fan-out or reading Miller-private SQLite directly. Rows are
/// ordered <c>(path, start_line, symbol_id)</c> so re-exporting an unchanged artifact is byte-identical. The D5
/// schema gate runs before the read, so an incompatible artifact fails with the standard actionable message
/// (CLI exit 3) instead of silently emitting rows from a mismatched schema.
/// </summary>
public static class SymbolExportReader
{
    public const int SchemaVersion = 1;

    public static string ExportJsonLines(string symbolsDbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolsDbPath);

        using SqliteConnection connection = SqliteReadOnlyAccess.Open(symbolsDbPath);
        JulieSchemaGate.Verify(connection);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT symbol_id, name, kind, language, path,
                   start_line, end_line, start_byte, end_byte,
                   visibility, parent_symbol_id, signature,
                   (doc_comment IS NOT NULL AND doc_comment != '') AS has_doc,
                   body_hash, is_test
            FROM symbols
            ORDER BY path, start_line, symbol_id;
            """;

        var sb = new StringBuilder();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            sb.Append(RenderRow(reader)).Append('\n');
        return sb.ToString();
    }

    private static string RenderRow(SqliteDataReader reader)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer,
            new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteNumber("schema_version", SchemaVersion);
            w.WriteString("symbol_id", reader.GetString(0));
            w.WriteString("name", reader.GetString(1));
            w.WriteString("kind", reader.GetString(2));
            w.WriteString("language", reader.GetString(3));
            w.WriteString("path", reader.GetString(4));
            // The julie schema declares span columns NOT NULL, but stay null-tolerant: an export must report
            // what the artifact holds, not throw mid-feed on a row a permissive writer let through.
            ExportJson.WriteNullableLong(w, "start_line", reader, 5);
            ExportJson.WriteNullableLong(w, "end_line", reader, 6);
            ExportJson.WriteNullableLong(w, "start_byte", reader, 7);
            ExportJson.WriteNullableLong(w, "end_byte", reader, 8);
            ExportJson.WriteNullableString(w, "visibility", reader, 9);
            ExportJson.WriteNullableString(w, "parent_symbol_id", reader, 10);
            ExportJson.WriteNullableString(w, "signature", reader, 11);
            w.WriteBoolean("has_doc", reader.GetInt64(12) != 0);
            ExportJson.WriteNullableString(w, "body_hash", reader, 13);
            w.WriteBoolean("is_test", reader.GetInt64(14) != 0);
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}

/// <summary>Shared nullable-column JSON helpers for the artifact JSONL export readers.</summary>
internal static class ExportJson
{
    public static void WriteNullableString(Utf8JsonWriter w, string name, SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) w.WriteNull(name);
        else w.WriteString(name, reader.GetString(ordinal));
    }

    public static void WriteNullableLong(Utf8JsonWriter w, string name, SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) w.WriteNull(name);
        else w.WriteNumber(name, reader.GetInt64(ordinal));
    }
}
