using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Miller.Indexing;

public sealed class ContentCorpusExportReader
{
    private const string ExportQuery = """
        SELECT m.schema_version,
               s.workspace_id,
               s.workspace_revision,
               c.source_id,
               c.chunk_id,
               c.content_kind,
               c.path,
               c.url,
               c.display_path,
               c.language,
               c.line_start,
               c.line_end,
               c.byte_start,
               c.byte_end,
               c.source_bytes,
               s.content_hash,
               c.raw_text,
               c.doc_len,
               c.is_test,
               c.containing_symbol_id,
               c.containing_symbol_name,
               s.status,
               s.indexed_at_utc
        FROM content_chunks c
        JOIN content_sources s ON s.source_id = c.source_id
        CROSS JOIN content_meta m
        WHERE s.status = 'active'
          AND ($kind IS NULL OR c.content_kind = $kind)
          AND ($workspace IS NULL OR s.workspace_id = $workspace)
        ORDER BY c.content_kind, c.display_path, c.line_start, c.chunk_id;
        """;

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
        command.CommandText = ExportQuery;
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
        command.CommandText = ExportQuery;
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
        if (schemaVersion != ContentCorpusSchema.SchemaVersion)
            throw new InvalidOperationException($"content.db schema_version {schemaVersion} is not supported.");

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
