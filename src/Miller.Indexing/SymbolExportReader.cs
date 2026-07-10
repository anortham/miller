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
            WITH diagnostic_paths AS (
                SELECT path, 1 AS has_parse_diagnostics
                FROM parse_diagnostics
                GROUP BY path
            )
            SELECT s.symbol_id, s.name, s.kind, s.language, s.path,
                   s.start_line, s.end_line, s.start_byte, s.end_byte,
                   s.visibility, s.parent_symbol_id, s.signature,
                   (s.doc_comment IS NOT NULL AND s.doc_comment != '') AS has_doc,
                   s.body_hash, s.is_test, s.test_container, s.test_lifecycle,
                   f.status AS file_status,
                   CASE WHEN f.path IS NULL THEN 0 ELSE 1 END AS has_file_evidence,
                   COALESCE(d.has_parse_diagnostics, 0) AS has_parse_diagnostics
            FROM symbols AS s
            LEFT JOIN files AS f ON f.path = s.path
            LEFT JOIN diagnostic_paths AS d ON d.path = s.path
            ORDER BY s.path, s.start_line, s.symbol_id;
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
            TestRoleEvidence testEvidence = TestRoleEvidence.FromArtifactFacts(
                isTest: reader.GetInt64(14) != 0,
                isContainer: reader.GetInt64(15) != 0,
                isLifecycle: reader.GetInt64(16) != 0,
                fileStatus: reader.IsDBNull(17) ? null : reader.GetString(17),
                hasFileEvidence: reader.GetInt64(18) != 0,
                hasParseDiagnostics: reader.GetInt64(19) != 0);
            w.WriteBoolean("test_case", testEvidence.IsCase);
            w.WriteBoolean("test_container", testEvidence.IsContainer);
            w.WriteBoolean("test_lifecycle", testEvidence.IsLifecycle);
            w.WriteString("test_evidence_status", testEvidence.Status);
            if (testEvidence.Reason is null) w.WriteNull("test_evidence_reason");
            else w.WriteString("test_evidence_reason", testEvidence.Reason);
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
