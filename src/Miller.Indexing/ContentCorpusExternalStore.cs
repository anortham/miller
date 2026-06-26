using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Miller.Core.Search;
using Miller.Core.Tokenization;

namespace Miller.Indexing;

public sealed class ContentCorpusExternalStore
{
    public const long DefaultMaxImportBytes = 25 * 1024 * 1024;
    public const int DefaultContextLines = 10;
    public const int MaxReadWindowLines = 200;

    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly long _defaultMaxImportBytes;
    private readonly TimeSpan? _writeLockTimeout;

    public ContentCorpusExternalStore(
        long defaultMaxImportBytes = DefaultMaxImportBytes,
        TimeSpan? writeLockTimeout = null)
    {
        if (defaultMaxImportBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(defaultMaxImportBytes), "Default max import bytes must be > 0.");
        if (writeLockTimeout is { } timeout && timeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(writeLockTimeout), "Write lock timeout must be >= 0.");
        _defaultMaxImportBytes = defaultMaxImportBytes;
        _writeLockTimeout = writeLockTimeout;
    }

    public ExternalContentImportResult Import(
        string contentDbPath,
        string filePath,
        long? maxBytes = null,
        string? displayPath = null)
    {
        string absFile = Path.GetFullPath(filePath);
        return ImportFile(
            contentDbPath,
            absFile,
            TextContentKind.ExternalFile,
            SourceIdFor(absFile),
            path: absFile,
            url: null,
            displayPath,
            maxBytes);
    }

    public ExternalContentImportResult ImportMarkdown(
        string contentDbPath,
        string filePath,
        string url,
        long? maxBytes = null,
        string? displayPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        string normalizedUrl = url.Trim();
        return ImportFile(
            contentDbPath,
            Path.GetFullPath(filePath),
            TextContentKind.Web,
            SourceIdForWeb(normalizedUrl),
            path: null,
            url: normalizedUrl,
            displayPath ?? normalizedUrl,
            maxBytes);
    }

    private ExternalContentImportResult ImportFile(
        string contentDbPath,
        string absFile,
        string contentKind,
        string sourceId,
        string? path,
        string? url,
        string? displayPath,
        long? maxBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentDbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(absFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);

        if (!File.Exists(absFile))
            throw new FileNotFoundException($"External content file not found at '{absFile}'.", absFile);

        long effectiveMaxBytes = maxBytes ?? _defaultMaxImportBytes;
        if (effectiveMaxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes), "max_bytes must be > 0.");

        var info = new FileInfo(absFile);
        if (info.Length > effectiveMaxBytes)
        {
            throw new InvalidOperationException(
                $"External file is {info.Length} bytes, which exceeds max_bytes {effectiveMaxBytes}. " +
                "Pass a larger max_bytes value when this import is intentional.");
        }

        byte[] bytes = File.ReadAllBytes(absFile);
        if (bytes.LongLength > effectiveMaxBytes)
        {
            throw new InvalidOperationException(
                $"External file is {bytes.LongLength} bytes, which exceeds max_bytes {effectiveMaxBytes}. " +
                "Pass a larger max_bytes value when this import is intentional.");
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidOperationException("External content import supports UTF-8 text files only.", ex);
        }

        string renderedPath = string.IsNullOrWhiteSpace(displayPath) ? absFile : displayPath!;
        string contentHash = "blake3:" + ContentHasher.Blake3Hex(bytes);
        IReadOnlyList<TextContentDocument> chunks = ContentCorpusChunker.Chunk(
            sourceId,
            contentKind,
            path,
            url,
            renderedPath,
            LanguageFor(absFile),
            text,
            bytes.LongLength,
            isTest: false,
            containingSymbols: Array.Empty<ContentCorpusSymbolSpan>());

        using (ContentCorpusWriteLock.AcquireFor(contentDbPath, _writeLockTimeout))
        {
            using var connection = OpenWritable(contentDbPath);
            using var tx = connection.BeginTransaction();
            bool replaced = DeleteSource(connection, sourceId).SourceCount > 0;
            InsertSource(connection, sourceId, contentKind, path, url, renderedPath, LanguageFor(absFile), contentHash, bytes.LongLength, text, chunks);
            UpdateMeta(connection);
            tx.Commit();

            return new ExternalContentImportResult(
                sourceId,
                contentKind,
                renderedPath,
                contentHash,
                bytes.LongLength,
                chunks.Count,
                replaced,
                url);
        }
    }

    public IReadOnlyList<TextContentSearchHit> Search(
        string contentDbPath,
        string query,
        string contentKind = TextContentKind.ExternalFile,
        int limit = 10)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentDbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentKind);
        if (limit <= 0 || !File.Exists(Path.GetFullPath(contentDbPath)))
            return Array.Empty<TextContentSearchHit>();

        return FtsTextContentSearchIndex
            .OpenUnversioned(contentDbPath)
            .Search(query, contentKind, limit, excludeTests: false);
    }

    public IReadOnlyList<ExternalContentSource> List(
        string contentDbPath,
        string contentKind = TextContentKind.ExternalFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentDbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentKind);
        if (!File.Exists(Path.GetFullPath(contentDbPath)))
            return Array.Empty<ExternalContentSource>();

        using var connection = OpenReadOnly(contentDbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.source_id, s.content_kind, s.display_path, s.content_hash, s.source_bytes, s.url,
                   s.line_count, s.indexed_at_utc, COUNT(c.chunk_id) AS chunk_count
            FROM content_sources s
            LEFT JOIN content_chunks c ON c.source_id = s.source_id
            WHERE s.content_kind = $kind
            GROUP BY s.source_id, s.content_kind, s.display_path, s.content_hash, s.source_bytes,
                     s.url, s.line_count, s.indexed_at_utc
            ORDER BY s.display_path, s.source_id;
            """;
        command.Parameters.AddWithValue("$kind", contentKind);

        var sources = new List<ExternalContentSource>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            sources.Add(new ExternalContentSource(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetInt32(6),
                reader.GetString(7),
                reader.GetInt32(8),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return sources;
    }

    public ExternalContentReadResult ReadWindow(
        string contentDbPath,
        string sourceId,
        int line,
        int contextLines = DefaultContextLines)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentDbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        if (line <= 0)
            throw new ArgumentOutOfRangeException(nameof(line), "line must be > 0.");
        if (contextLines < 0)
            throw new ArgumentOutOfRangeException(nameof(contextLines), "context_lines must be >= 0.");
        if (!File.Exists(Path.GetFullPath(contentDbPath)))
            throw new InvalidOperationException("No content corpus exists. Import a file first.");

        using var connection = OpenReadOnly(contentDbPath);
        ExternalContentSource source = ReadSource(connection, sourceId);
        if (line > source.LineCount)
            throw new InvalidOperationException($"Source '{sourceId}' has {source.LineCount} lines; requested line {line}.");

        int start = Math.Max(1, line - contextLines);
        int end = Math.Min(source.LineCount, line + contextLines);
        if (end - start + 1 > MaxReadWindowLines)
        {
            throw new InvalidOperationException(
                $"Requested read window has {end - start + 1} lines; maximum is {MaxReadWindowLines}. " +
                "Use a smaller context_lines value.");
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT line_start, raw_text
            FROM content_chunks
            WHERE source_id = $source
              AND line_end >= $start
              AND line_start <= $end
            ORDER BY line_start, chunk_id;
            """;
        command.Parameters.AddWithValue("$source", sourceId);
        command.Parameters.AddWithValue("$start", start);
        command.Parameters.AddWithValue("$end", end);

        var linesByNumber = new SortedDictionary<int, string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            int chunkLineStart = reader.GetInt32(0);
            string[] chunkLines = Normalize(reader.GetString(1)).Split('\n');
            for (int i = 0; i < chunkLines.Length; i++)
            {
                int number = chunkLineStart + i;
                if (number >= start && number <= end)
                    linesByNumber.TryAdd(number, chunkLines[i]);
            }
        }

        return new ExternalContentReadResult(
            source.SourceId,
            source.DisplayPath,
            start,
            end,
            linesByNumber.Select(static kv => new ExternalContentLine(kv.Key, kv.Value)).ToArray());
    }

    public ExternalContentRemoveResult Remove(string contentDbPath, string sourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentDbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        if (!File.Exists(Path.GetFullPath(contentDbPath)))
            return new ExternalContentRemoveResult(sourceId, Removed: false, SourceCount: 0, ChunkCount: 0);

        using (ContentCorpusWriteLock.AcquireFor(contentDbPath, _writeLockTimeout))
        {
            using var connection = OpenWritable(contentDbPath);
            using var tx = connection.BeginTransaction();
            ExternalContentRemoveResult result = DeleteSource(connection, sourceId);
            UpdateMeta(connection);
            tx.Commit();
            return result;
        }
    }

    public static string SourceIdFor(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        byte[] input = Encoding.UTF8.GetBytes(Path.GetFullPath(filePath));
        return TextContentKind.ExternalFile + ":" + Convert.ToHexStringLower(SHA256.HashData(input));
    }

    public static string SourceIdForWeb(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        byte[] input = Encoding.UTF8.GetBytes(url.Trim());
        return TextContentKind.Web + ":" + Convert.ToHexStringLower(SHA256.HashData(input));
    }

    private static SqliteConnection OpenWritable(string contentDbPath)
    {
        string absPath = Path.GetFullPath(contentDbPath);
        string? dir = Path.GetDirectoryName(absPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        bool existed = File.Exists(absPath);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = absPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
        if (!existed || !TableExists(connection, "content_sources"))
        {
            using var ddl = connection.CreateCommand();
            ddl.CommandText = ContentCorpusSchema.SchemaDdl;
            ddl.ExecuteNonQuery();
        }

        EnsureMetaRow(connection);
        return connection;
    }

    private static SqliteConnection OpenReadOnly(string contentDbPath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(contentDbPath),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type IN ('table', 'virtual table') AND name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", tableName);
        return command.ExecuteScalar() is not null;
    }

    private static void EnsureMetaRow(SqliteConnection connection)
    {
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT schema_version FROM content_meta LIMIT 2;";
            using var reader = read.ExecuteReader();
            if (reader.Read())
            {
                int schemaVersion = reader.GetInt32(0);
                if (schemaVersion != ContentCorpusSchema.SchemaVersion)
                    throw new InvalidOperationException($"content.db schema_version {schemaVersion} is not supported.");
                if (reader.Read())
                    throw new InvalidOperationException("content_meta has multiple rows.");
                return;
            }
        }

        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO content_meta
                (schema_version, workspace_revision, chunker_version, source_count, chunk_count,
                 indexed_source_bytes, stored_raw_bytes, updated_at_utc, skipped_status, skipped_scope,
                 skipped_large, skipped_missing, skipped_hash, skipped_utf8, skipped_io)
            VALUES ($schema, NULL, $chunker, 0, 0, 0, 0, $updated, 0, 0, 0, 0, 0, 0, 0);
            """;
        insert.Parameters.AddWithValue("$schema", ContentCorpusSchema.SchemaVersion);
        insert.Parameters.AddWithValue("$chunker", ContentCorpusSchema.ChunkerVersion);
        insert.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        insert.ExecuteNonQuery();
    }

    private static ExternalContentSource ReadSource(SqliteConnection connection, string sourceId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.source_id, s.content_kind, s.display_path, s.content_hash, s.source_bytes, s.url,
                   s.line_count, s.indexed_at_utc, COUNT(c.chunk_id) AS chunk_count
            FROM content_sources s
            LEFT JOIN content_chunks c ON c.source_id = s.source_id
            WHERE s.source_id = $source
            GROUP BY s.source_id, s.content_kind, s.display_path, s.content_hash, s.source_bytes,
                     s.url, s.line_count, s.indexed_at_utc
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$source", sourceId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw new KeyNotFoundException($"Content source '{sourceId}' was not found.");

        return new ExternalContentSource(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt64(4),
            reader.GetInt32(6),
            reader.GetString(7),
            reader.GetInt32(8),
            reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    private static void InsertSource(
        SqliteConnection connection,
        string sourceId,
        string contentKind,
        string? path,
        string? url,
        string displayPath,
        string language,
        string contentHash,
        long sourceBytes,
        string text,
        IReadOnlyList<TextContentDocument> chunks)
    {
        string indexedAt = DateTimeOffset.UtcNow.ToString("O");
        using var source = connection.CreateCommand();
        source.CommandText = """
            INSERT INTO content_sources
                (source_id, content_kind, workspace_id, workspace_revision, path, url, display_path,
                 language, content_hash, source_bytes, line_count, is_test, status, indexed_at_utc)
            VALUES ($id, $kind, NULL, NULL, $path, $url, $display, $language, $hash,
                    $bytes, $lines, 0, 'active', $indexed);
            """;
        source.Parameters.AddWithValue("$id", sourceId);
        source.Parameters.AddWithValue("$kind", contentKind);
        source.Parameters.AddWithValue("$path", (object?)path ?? DBNull.Value);
        source.Parameters.AddWithValue("$url", (object?)url ?? DBNull.Value);
        source.Parameters.AddWithValue("$display", displayPath);
        source.Parameters.AddWithValue("$language", language);
        source.Parameters.AddWithValue("$hash", contentHash);
        source.Parameters.AddWithValue("$bytes", sourceBytes);
        source.Parameters.AddWithValue("$lines", ContentCorpusChunker.CountLines(Normalize(text)));
        source.Parameters.AddWithValue("$indexed", indexedAt);
        source.ExecuteNonQuery();

        using var chunk = connection.CreateCommand();
        chunk.CommandText = """
            INSERT INTO content_chunks
                (chunk_id, source_id, content_kind, path, url, display_path, language, line_start,
                 line_end, byte_start, byte_end, raw_text, doc_len, is_test, source_bytes,
                 containing_symbol_id, containing_symbol_name)
            VALUES ($chunk, $source, $kind, $path, $url, $display, $language, $line_start, $line_end,
                    $byte_start, $byte_end, $raw, $doc_len, 0, $source_bytes, NULL, NULL);
            """;
        var pcChunk = chunk.Parameters.Add("$chunk", SqliteType.Text);
        var pcSource = chunk.Parameters.Add("$source", SqliteType.Text);
        var pcKind = chunk.Parameters.Add("$kind", SqliteType.Text);
        var pcPath = chunk.Parameters.Add("$path", SqliteType.Text);
        var pcUrl = chunk.Parameters.Add("$url", SqliteType.Text);
        var pcDisplay = chunk.Parameters.Add("$display", SqliteType.Text);
        var pcLanguage = chunk.Parameters.Add("$language", SqliteType.Text);
        var pcLineStart = chunk.Parameters.Add("$line_start", SqliteType.Integer);
        var pcLineEnd = chunk.Parameters.Add("$line_end", SqliteType.Integer);
        var pcByteStart = chunk.Parameters.Add("$byte_start", SqliteType.Integer);
        var pcByteEnd = chunk.Parameters.Add("$byte_end", SqliteType.Integer);
        var pcRaw = chunk.Parameters.Add("$raw", SqliteType.Text);
        var pcDocLen = chunk.Parameters.Add("$doc_len", SqliteType.Integer);
        var pcSourceBytes = chunk.Parameters.Add("$source_bytes", SqliteType.Integer);

        using var fts = connection.CreateCommand();
        fts.CommandText = "INSERT INTO content_fts(chunk_id, body) VALUES ($chunk, $body);";
        var pfChunk = fts.Parameters.Add("$chunk", SqliteType.Text);
        var pfBody = fts.Parameters.Add("$body", SqliteType.Text);
        var tokens = new List<string>(128);

        foreach (TextContentDocument doc in chunks)
        {
            pcChunk.Value = doc.ChunkId;
            pcSource.Value = doc.SourceId;
            pcKind.Value = doc.ContentKind;
            pcPath.Value = (object?)path ?? DBNull.Value;
            pcUrl.Value = (object?)url ?? DBNull.Value;
            pcDisplay.Value = doc.DisplayPath;
            pcLanguage.Value = doc.Language;
            pcLineStart.Value = doc.LineStart;
            pcLineEnd.Value = doc.LineEnd;
            pcByteStart.Value = doc.ByteStart;
            pcByteEnd.Value = doc.ByteEnd;
            pcRaw.Value = doc.Text;
            pcDocLen.Value = doc.DocLen;
            pcSourceBytes.Value = doc.SourceBytes;
            chunk.ExecuteNonQuery();

            tokens.Clear();
            CodeTokenizer.Tokenize(doc.Text, tokens);
            pfChunk.Value = doc.ChunkId;
            pfBody.Value = string.Join(' ', tokens);
            fts.ExecuteNonQuery();
        }
    }

    private static ExternalContentRemoveResult DeleteSource(SqliteConnection connection, string sourceId)
    {
        using var count = connection.CreateCommand();
        count.CommandText = """
            SELECT COUNT(*), COALESCE((SELECT COUNT(*) FROM content_chunks WHERE source_id = $source), 0)
            FROM content_sources
            WHERE source_id = $source AND content_kind IN ($external, $web);
            """;
        count.Parameters.AddWithValue("$source", sourceId);
        count.Parameters.AddWithValue("$external", TextContentKind.ExternalFile);
        count.Parameters.AddWithValue("$web", TextContentKind.Web);
        using var reader = count.ExecuteReader();
        reader.Read();
        int sourceCount = reader.GetInt32(0);
        int chunkCount = reader.GetInt32(1);
        reader.Close();

        if (sourceCount == 0)
            return new ExternalContentRemoveResult(sourceId, Removed: false, SourceCount: 0, ChunkCount: 0);

        using (var fts = connection.CreateCommand())
        {
            fts.CommandText = """
                DELETE FROM content_fts
                WHERE chunk_id IN (SELECT chunk_id FROM content_chunks WHERE source_id = $source);
                """;
            fts.Parameters.AddWithValue("$source", sourceId);
            fts.ExecuteNonQuery();
        }

        using (var chunks = connection.CreateCommand())
        {
            chunks.CommandText = "DELETE FROM content_chunks WHERE source_id = $source;";
            chunks.Parameters.AddWithValue("$source", sourceId);
            chunks.ExecuteNonQuery();
        }

        using (var spans = connection.CreateCommand())
        {
            spans.CommandText = "DELETE FROM content_symbol_spans WHERE source_id = $source;";
            spans.Parameters.AddWithValue("$source", sourceId);
            spans.ExecuteNonQuery();
        }

        using (var sources = connection.CreateCommand())
        {
            sources.CommandText = "DELETE FROM content_sources WHERE source_id = $source AND content_kind IN ($external, $web);";
            sources.Parameters.AddWithValue("$source", sourceId);
            sources.Parameters.AddWithValue("$external", TextContentKind.ExternalFile);
            sources.Parameters.AddWithValue("$web", TextContentKind.Web);
            sources.ExecuteNonQuery();
        }

        return new ExternalContentRemoveResult(sourceId, Removed: true, sourceCount, chunkCount);
    }

    private static void UpdateMeta(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE content_meta
            SET source_count = (SELECT COUNT(*) FROM content_sources),
                chunk_count = (SELECT COUNT(*) FROM content_chunks),
                indexed_source_bytes = COALESCE((SELECT SUM(source_bytes) FROM content_sources), 0),
                stored_raw_bytes = COALESCE((SELECT SUM(length(CAST(raw_text AS BLOB))) FROM content_chunks), 0),
                updated_at_utc = $updated;
            """;
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static string LanguageFor(string path)
    {
        string extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return string.IsNullOrWhiteSpace(extension) ? "text" : extension;
    }

    private static string Normalize(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}

public sealed record ExternalContentImportResult(
    string SourceId,
    string ContentKind,
    string DisplayPath,
    string ContentHash,
    long SourceBytes,
    int ChunkCount,
    bool Replaced,
    string? Url = null);

public sealed record ExternalContentSource(
    string SourceId,
    string ContentKind,
    string DisplayPath,
    string ContentHash,
    long SourceBytes,
    int LineCount,
    string IndexedAtUtc,
    int ChunkCount,
    string? Url = null);

public sealed record ExternalContentReadResult(
    string SourceId,
    string DisplayPath,
    int LineStart,
    int LineEnd,
    IReadOnlyList<ExternalContentLine> Lines);

public sealed record ExternalContentLine(int LineNumber, string Text);

public sealed record ExternalContentRemoveResult(
    string SourceId,
    bool Removed,
    int SourceCount,
    int ChunkCount);
