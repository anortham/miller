using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Miller.Indexing;

/// <summary>
/// The <c>references export --jsonl</c> feed (cli-eros-v1): one deterministic JSON line per
/// <c>identifiers</c> row. This is a fact feed, not a dead-code analysis surface; consumers do any ranking,
/// suppression, or cross-workspace workflow state outside Miller. Rows are ordered <c>(path, start_byte,
/// identifier_id)</c> so re-exporting an unchanged artifact is byte-identical. The D5 schema gate runs before
/// the read (incompatible artifact ⇒ CLI exit 3).
/// </summary>
public static class ReferenceExportReader
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

        string? artifactId = ReadArtifactId(connection);
        long? workspaceRevision = ReadWorkspaceRevision(connection);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT i.identifier_id, i.name, i.kind, i.language, i.path,
                   i.start_line, i.end_line, i.start_column, i.end_column, i.start_byte, i.end_byte,
                   i.containing_symbol_id,
                   source.name AS source_name, source.kind AS source_kind, source.is_test AS source_is_test,
                   i.target_symbol_id,
                   target.name AS target_name, target.kind AS target_kind, target.is_test AS target_is_test,
                   i.confidence, i.metadata_json
            FROM identifiers i
            LEFT JOIN symbols source ON source.symbol_id = i.containing_symbol_id
            LEFT JOIN symbols target ON target.symbol_id = i.target_symbol_id
            ORDER BY i.path, i.start_byte, i.identifier_id;
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            writer.Write(RenderRow(reader, artifactId, workspaceRevision));
            writer.Write('\n');
        }
    }

    private static string? ReadArtifactId(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM artifact_metadata WHERE key = 'artifact_id' LIMIT 1;";
        object? value = cmd.ExecuteScalar();
        return value is string text ? text : null;
    }

    private static long? ReadWorkspaceRevision(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT MAX(revision_id) FROM extraction_revisions;";
        object? value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string RenderRow(SqliteDataReader reader, string? artifactId, long? workspaceRevision)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer,
            new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteNumber("schema_version", SchemaVersion);
            w.WriteString("identifier_id", reader.GetString(0));
            w.WriteString("name", reader.GetString(1));
            w.WriteString("reference_kind", reader.GetString(2));
            w.WriteString("language", reader.GetString(3));
            w.WriteString("path", reader.GetString(4));
            ExportJson.WriteNullableLong(w, "start_line", reader, 5);
            ExportJson.WriteNullableLong(w, "end_line", reader, 6);
            ExportJson.WriteNullableLong(w, "start_column", reader, 7);
            ExportJson.WriteNullableLong(w, "end_column", reader, 8);
            ExportJson.WriteNullableLong(w, "start_byte", reader, 9);
            ExportJson.WriteNullableLong(w, "end_byte", reader, 10);
            ExportJson.WriteNullableString(w, "source_symbol_id", reader, 11);
            ExportJson.WriteNullableString(w, "source_symbol_name", reader, 12);
            ExportJson.WriteNullableString(w, "source_symbol_kind", reader, 13);
            WriteNullableBoolFromInteger(w, "source_symbol_is_test", reader, 14);
            ExportJson.WriteNullableString(w, "target_symbol_id", reader, 15);
            ExportJson.WriteNullableString(w, "target_symbol_name", reader, 16);
            ExportJson.WriteNullableString(w, "target_symbol_kind", reader, 17);
            WriteNullableBoolFromInteger(w, "target_symbol_is_test", reader, 18);
            w.WriteString("resolution_status", ResolutionStatus(reader));
            WriteNullableDouble(w, "confidence", reader, 19);
            ExportJson.WriteNullableString(w, "metadata_json", reader, 20);
            WriteNullableString(w, "artifact_id", artifactId);
            WriteNullableLong(w, "workspace_revision", workspaceRevision);
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string ResolutionStatus(SqliteDataReader reader)
    {
        if (reader.IsDBNull(15))
            return "unresolved";
        return reader.IsDBNull(16) ? "dangling_target" : "resolved";
    }

    private static void WriteNullableDouble(Utf8JsonWriter writer, string name, SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) writer.WriteNull(name);
        else writer.WriteNumber(name, reader.GetDouble(ordinal));
    }

    private static void WriteNullableBoolFromInteger(Utf8JsonWriter writer, string name, SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) writer.WriteNull(name);
        else writer.WriteBoolean(name, reader.GetInt64(ordinal) != 0);
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteString(name, value);
    }

    private static void WriteNullableLong(Utf8JsonWriter writer, string name, long? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteNumber(name, value.Value);
    }
}
