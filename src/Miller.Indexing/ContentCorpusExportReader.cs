using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Miller.Indexing;

public sealed class ContentCorpusExportReader
{
    public IReadOnlyList<ContentCorpusExportRow> Read(
        string contentDbPath,
        string? contentKind = null,
        string? workspaceId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentDbPath);
        if (!File.Exists(Path.GetFullPath(contentDbPath)))
            return Array.Empty<ContentCorpusExportRow>();

        string? kind = NormalizeKind(contentKind);
        string? workspace = string.IsNullOrWhiteSpace(workspaceId) ? null : workspaceId.Trim();

        using var connection = SqliteReadOnlyAccess.Open(contentDbPath);
        using var command = connection.CreateCommand();
        command.CommandText = BuildExportQuery(connection);
        command.Parameters.AddWithValue("$kind", (object?)kind ?? DBNull.Value);
        command.Parameters.AddWithValue("$workspace", (object?)workspace ?? DBNull.Value);

        var rows = new List<ContentCorpusExportRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(ReadRow(reader));
        }

        return rows;
    }

    public string ExportJsonLines(
        string contentDbPath,
        string? contentKind = null,
        string? workspaceId = null) =>
        ToJsonLines(Read(contentDbPath, contentKind, workspaceId));

    public long WriteJsonLines(
        string contentDbPath,
        TextWriter output,
        string? contentKind = null,
        string? workspaceId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentDbPath);
        ArgumentNullException.ThrowIfNull(output);
        if (!File.Exists(Path.GetFullPath(contentDbPath)))
            return 0;

        string? kind = NormalizeKind(contentKind);
        string? workspace = string.IsNullOrWhiteSpace(workspaceId) ? null : workspaceId.Trim();
        using var connection = SqliteReadOnlyAccess.Open(contentDbPath);
        using var command = connection.CreateCommand();
        command.CommandText = BuildExportQuery(connection);
        command.Parameters.AddWithValue("$kind", (object?)kind ?? DBNull.Value);
        command.Parameters.AddWithValue("$workspace", (object?)workspace ?? DBNull.Value);

        long count = 0;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            output.Write(ToJson(ReadRow(reader)));
            output.Write('\n');
            count++;
        }
        return count;
    }

    public static string ToJsonLines(IReadOnlyList<ContentCorpusExportRow> rows)
    {
        if (rows.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        foreach (ContentCorpusExportRow row in rows)
            sb.Append(ToJson(row)).Append('\n');
        return sb.ToString();
    }

    private static string ToJson(ContentCorpusExportRow row)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(
            buffer,
            new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", row.SchemaVersion);
            WriteNullableString(writer, "workspace_id", row.WorkspaceId);
            WriteNullableNumber(writer, "workspace_revision", row.WorkspaceRevision);
            writer.WriteString("source_id", row.SourceId);
            writer.WriteString("chunk_id", row.ChunkId);
            writer.WriteString("content_kind", row.ContentKind);
            WriteNullableString(writer, "path", row.Path);
            WriteNullableString(writer, "url", row.Url);
            writer.WriteString("display_path", row.DisplayPath);
            writer.WriteString("language", row.Language);
            writer.WriteNumber("line_start", row.LineStart);
            writer.WriteNumber("line_end", row.LineEnd);
            writer.WriteNumber("byte_start", row.ByteStart);
            writer.WriteNumber("byte_end", row.ByteEnd);
            writer.WriteNumber("source_bytes", row.SourceBytes);
            writer.WriteString("content_hash", row.ContentHash);
            writer.WriteString("chunk_text", row.ChunkText);
            writer.WriteNumber("doc_len", row.DocLen);
            writer.WriteBoolean("is_test", row.IsTest);
            WriteNullableString(writer, "containing_symbol_id", row.ContainingSymbolId);
            WriteNullableString(writer, "containing_symbol_name", row.ContainingSymbolName);
            writer.WriteString("source_status", row.SourceStatus);
            writer.WriteString("indexed_at_utc", row.IndexedAtUtc);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static ContentCorpusExportRow ReadRow(SqliteDataReader reader)
    {
        int schemaVersion = reader.GetInt32(0);
        return new ContentCorpusExportRow(
            schemaVersion,
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetInt32(10),
            reader.GetInt32(11),
            reader.GetInt64(12),
            reader.GetInt64(13),
            reader.GetInt64(14),
            reader.GetString(15),
            reader.GetString(16),
            reader.GetInt32(17),
            reader.GetInt32(18) != 0,
            reader.IsDBNull(19) ? null : reader.GetString(19),
            reader.IsDBNull(20) ? null : reader.GetString(20),
            reader.GetString(21),
            reader.GetString(22));
    }

    private static string BuildExportQuery(SqliteConnection connection)
    {
        IReadOnlySet<string> sourceColumns = ReadColumns(connection, "content_sources");
        IReadOnlySet<string> chunkColumns = ReadColumns(connection, "content_chunks");
        string Source(string name, string fallback) =>
            sourceColumns.Contains(name) ? $"s.\"{name}\"" : fallback;
        string Chunk(string name, string fallback) =>
            chunkColumns.Contains(name) ? $"c.\"{name}\"" : fallback;

        string contentKind = Chunk("content_kind", Source("content_kind", "''"));
        string displayPath = Chunk("display_path", Source("display_path", Chunk("source_id", "''")));
        string workspaceFilter = sourceColumns.Contains("workspace_id")
            ? "($workspace IS NULL OR s.workspace_id = $workspace)"
            : "$workspace IS NULL";
        return $"""
            SELECT {ReadSchemaVersion(connection)},
                   {Source("workspace_id", "NULL")},
                   {Source("workspace_revision", "NULL")},
                   {Chunk("source_id", "''")},
                   {Chunk("chunk_id", "''")},
                   {contentKind},
                   {Chunk("path", Source("path", "NULL"))},
                   {Chunk("url", Source("url", "NULL"))},
                   {displayPath},
                   {Chunk("language", Source("language", "''"))},
                   {Chunk("line_start", "1")},
                   {Chunk("line_end", Chunk("line_start", "1"))},
                   {Chunk("byte_start", "0")},
                   {Chunk("byte_end", "0")},
                   {Chunk("source_bytes", Source("source_bytes", "0"))},
                   {Source("content_hash", "''")},
                   {Chunk("raw_text", "''")},
                   {Chunk("doc_len", "0")},
                   {Chunk("is_test", Source("is_test", "0"))},
                   {Chunk("containing_symbol_id", "NULL")},
                   {Chunk("containing_symbol_name", "NULL")},
                   {Source("status", "'active'")},
                   {Source("indexed_at_utc", "''")}
            FROM content_chunks c
            JOIN content_sources s ON s.source_id = c.source_id
            WHERE {Source("status", "'active'")} = 'active'
              AND ($kind IS NULL OR {contentKind} = $kind)
              AND {workspaceFilter}
            ORDER BY {contentKind}, {displayPath}, {Chunk("line_start", "1")}, {Chunk("chunk_id", "''")};
            """;
    }

    private static IReadOnlySet<string> ReadColumns(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\");";
        using var reader = command.ExecuteReader();
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
            columns.Add(reader.GetString(1));
        if (columns.Count == 0)
            throw new InvalidOperationException($"content.db is missing required table {table}.");
        return columns;
    }

    private static int ReadSchemaVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT schema_version FROM content_meta LIMIT 1;";
        object? value = command.ExecuteScalar();
        if (value is null or DBNull)
            throw new InvalidOperationException("content.db content_meta has no schema_version.");
        return checked(Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteString(name, value);
    }

    private static void WriteNullableNumber(Utf8JsonWriter writer, string name, long? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteNumber(name, value.Value);
    }

    private static string? NormalizeKind(string? contentKind)
    {
        if (string.IsNullOrWhiteSpace(contentKind) ||
            string.Equals(contentKind.Trim(), "all", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return contentKind.Trim().ToLowerInvariant() switch
        {
            "source" or "workspace_source" => TextContentKind.WorkspaceSource,
            "docs" or "doc" or "workspace_docs" => TextContentKind.WorkspaceDocs,
            "config" or "workspace_config" => TextContentKind.WorkspaceConfig,
            "external" or "external_file" or "file" => TextContentKind.ExternalFile,
            "web" => TextContentKind.Web,
            _ => throw new InvalidOperationException("content_kind must be all, workspace_source, workspace_docs, workspace_config, external_file, or web."),
        };
    }
}

public sealed record ContentCorpusExportRow(
    int SchemaVersion,
    string? WorkspaceId,
    long? WorkspaceRevision,
    string SourceId,
    string ChunkId,
    string ContentKind,
    string? Path,
    string? Url,
    string DisplayPath,
    string Language,
    int LineStart,
    int LineEnd,
    long ByteStart,
    long ByteEnd,
    long SourceBytes,
    string ContentHash,
    string ChunkText,
    int DocLen,
    bool IsTest,
    string? ContainingSymbolId,
    string? ContainingSymbolName,
    string SourceStatus,
    string IndexedAtUtc);
