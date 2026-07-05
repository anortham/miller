using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Miller.Server.Telemetry;

public static class TelemetryExportReader
{
    public const int SchemaVersion = 1;

    public static string ExportJsonLines(string telemetryDbPath, string? workspaceId = null)
    {
        using var writer = new StringWriter();
        WriteJsonLines(telemetryDbPath, writer, workspaceId);
        return writer.ToString();
    }

    public static void WriteJsonLines(string telemetryDbPath, TextWriter writer, string? workspaceId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(telemetryDbPath);
        ArgumentNullException.ThrowIfNull(writer);
        if (!File.Exists(telemetryDbPath))
            return;

        using var connection = OpenReadOnly(telemetryDbPath);
        if (!TableExists(connection, "tool_telemetry"))
            return;

        using var cmd = connection.CreateCommand();
        bool allWorkspaces = string.IsNullOrWhiteSpace(workspaceId)
            || string.Equals(workspaceId, "all", StringComparison.OrdinalIgnoreCase);
        cmd.CommandText = allWorkspaces
            ? SelectSql + " ORDER BY ts ASC, id ASC;"
            : SelectSql + " WHERE workspace_id = $workspace_id ORDER BY ts ASC, id ASC;";
        if (!allWorkspaces)
            cmd.Parameters.AddWithValue("$workspace_id", workspaceId);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            writer.Write(RenderRow(reader));
            writer.Write('\n');
        }
    }

    private const string SelectSql = """
        SELECT id, ts, tool, op, workspace_id, workspace_root, duration_ms, outcome, error_kind, result_count,
               bytes_examined, bytes_returned, source_bytes, est_tokens, index_fresh, target_hash, metadata_json
        FROM tool_telemetry
        """;

    private static SqliteConnection OpenReadOnly(string dbPath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(dbPath),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static bool TableExists(SqliteConnection connection, string name)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name LIMIT 1;";
        cmd.Parameters.AddWithValue("$name", name);
        return cmd.ExecuteScalar() is not null;
    }

    private static string RenderRow(SqliteDataReader reader)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer,
            new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteNumber("schema_version", SchemaVersion);
            w.WriteString("id", reader.GetString(0));
            w.WriteString("ts", reader.GetString(1));
            w.WriteString("tool", reader.GetString(2));
            WriteNullableString(w, "op", reader, 3);
            WriteNullableString(w, "workspace_id", reader, 4);
            WriteNullableString(w, "workspace_root", reader, 5);
            w.WriteNumber("duration_ms", reader.GetInt64(6));
            w.WriteString("outcome", reader.GetString(7));
            WriteNullableString(w, "error_kind", reader, 8);
            WriteNullableInt(w, "result_count", reader, 9);
            w.WriteNumber("bytes_examined", reader.GetInt64(10));
            w.WriteNumber("bytes_returned", reader.GetInt64(11));
            w.WriteNumber("source_bytes", reader.GetInt64(12));
            WriteNullableLong(w, "est_tokens", reader, 13);
            WriteNullableBool(w, "index_fresh", reader, 14);
            WriteNullableString(w, "target_hash", reader, 15);
            w.WriteString("metadata_json", reader.GetString(16));
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteNullableString(Utf8JsonWriter w, string name, SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) w.WriteNull(name);
        else w.WriteString(name, reader.GetString(ordinal));
    }

    private static void WriteNullableInt(Utf8JsonWriter w, string name, SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) w.WriteNull(name);
        else w.WriteNumber(name, reader.GetInt32(ordinal));
    }

    private static void WriteNullableLong(Utf8JsonWriter w, string name, SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) w.WriteNull(name);
        else w.WriteNumber(name, reader.GetInt64(ordinal));
    }

    private static void WriteNullableBool(Utf8JsonWriter w, string name, SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            w.WriteNull(name);
            return;
        }

        w.WriteBoolean(name, reader.GetInt64(ordinal) != 0);
    }
}
