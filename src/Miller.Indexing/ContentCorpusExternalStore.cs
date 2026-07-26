using System.Security.Cryptography;
using System.Text;
using Blake3;
using Microsoft.Data.Sqlite;
using Miller.Core.Search;
using Miller.Core.Tokenization;

namespace Miller.Indexing;

public sealed class ContentCorpusExternalStore
{
    public const long DefaultMaxImportBytes = 25 * 1024 * 1024;
    public const int DefaultContextLines = 10;
    public const int MaxReadWindowLines = 200;
    public const int MaxContextLines = 1_000_000;
    public const int MaxStreamingLineChars = 64 * 1024;
    public const int MaxStreamingChunkChars = 1024 * 1024;

    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly long _defaultMaxImportBytes;
    private readonly TimeSpan? _writeLockTimeout;
    private readonly Func<string, Stream> _openRead;

    public ContentCorpusExternalStore(
        long defaultMaxImportBytes = DefaultMaxImportBytes,
        TimeSpan? writeLockTimeout = null)
        : this(defaultMaxImportBytes, writeLockTimeout, OpenRead)
    {
    }

    internal ContentCorpusExternalStore(
        long defaultMaxImportBytes,
        TimeSpan? writeLockTimeout,
        Func<string, Stream> openRead)
    {
        if (defaultMaxImportBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(defaultMaxImportBytes), "Default max import bytes must be > 0.");
        if (writeLockTimeout is { } timeout && timeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(writeLockTimeout), "Write lock timeout must be >= 0.");
        ArgumentNullException.ThrowIfNull(openRead);
        _defaultMaxImportBytes = defaultMaxImportBytes;
        _writeLockTimeout = writeLockTimeout;
        _openRead = openRead;
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

        if (maxBytes > _defaultMaxImportBytes)
        {
            return ImportFileStreaming(
                contentDbPath,
                absFile,
                contentKind,
                sourceId,
                path,
                url,
                displayPath,
                effectiveMaxBytes,
                info.Length);
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

    private ExternalContentImportResult ImportFileStreaming(
        string contentDbPath,
        string absFile,
        string contentKind,
        string sourceId,
        string? path,
        string? url,
        string? displayPath,
        long effectiveMaxBytes,
        long sourceBytes)
    {
        string renderedPath = string.IsNullOrWhiteSpace(displayPath) ? absFile : displayPath!;
        string language = LanguageFor(absFile);

        using (ContentCorpusWriteLock.AcquireFor(contentDbPath, _writeLockTimeout))
        {
            using var connection = OpenWritable(contentDbPath);
            using var tx = connection.BeginTransaction();
            bool replaced = DeleteSource(connection, sourceId).SourceCount > 0;
            using var chunks = new StreamingChunkWriter(
                connection,
                sourceId,
                contentKind,
                path,
                url,
                renderedPath,
                language,
                sourceBytes);
            using Stream file = _openRead(absFile);
            using var hashing = new Blake3Stream(file, dispose: false);
            using var reader = new StreamReader(
                hashing,
                StrictUtf8,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 16 * 1024,
                leaveOpen: true);

            int lineCount;
            try
            {
                lineCount = StreamNormalizedLines(
                    reader,
                    chunks.AddLine,
                    () =>
                    {
                        long currentBytes = file.Position;
                        if (currentBytes > effectiveMaxBytes)
                        {
                            throw new InvalidOperationException(
                                $"External file is {currentBytes} bytes, which exceeds max_bytes {effectiveMaxBytes}. " +
                                "Pass a larger max_bytes value when this import is intentional.");
                        }
                        if (currentBytes > sourceBytes)
                            throw new IOException($"External file '{absFile}' changed while it was being imported.");
                    });
            }
            catch (DecoderFallbackException ex)
            {
                throw new InvalidOperationException("External content import supports UTF-8 text files only.", ex);
            }

            long bytesRead = file.Position;
            if (bytesRead > effectiveMaxBytes)
            {
                throw new InvalidOperationException(
                    $"External file is {bytesRead} bytes, which exceeds max_bytes {effectiveMaxBytes}. " +
                    "Pass a larger max_bytes value when this import is intentional.");
            }
            if (bytesRead != sourceBytes)
                throw new IOException($"External file '{absFile}' changed while it was being imported.");

            chunks.Complete();
            string contentHash = "blake3:" + Convert.ToHexStringLower(hashing.ComputeHash().AsSpan());
            InsertSourceMetadata(
                connection,
                sourceId,
                contentKind,
                path,
                url,
                renderedPath,
                language,
                contentHash,
                bytesRead,
                lineCount);
            UpdateMeta(connection);
            tx.Commit();

            return new ExternalContentImportResult(
                sourceId,
                contentKind,
                renderedPath,
                contentHash,
                bytesRead,
                chunks.ChunkCount,
                replaced,
                url);
        }
    }

    private static Stream OpenRead(string path) =>
        new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            FileOptions.SequentialScan);

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

    public ExternalContentInventory Inventory(
        string contentDbPath,
        string? contentKind,
        int perKindLimit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentDbPath);
        if (perKindLimit <= 0)
            throw new ArgumentOutOfRangeException(nameof(perKindLimit), "limit must be > 0.");

        string[] kinds = contentKind is null
            ? [TextContentKind.ExternalFile, TextContentKind.Web]
            : [contentKind];
        if (!File.Exists(Path.GetFullPath(contentDbPath)))
        {
            return new ExternalContentInventory(
                perKindLimit,
                [.. kinds.Select(static kind => new ExternalContentKindInventory(kind, 0, []))]);
        }

        using var connection = OpenReadOnly(contentDbPath);
        using var transaction = connection.BeginTransaction();
        var inventories = new List<ExternalContentKindInventory>(kinds.Length);
        foreach (string kind in kinds)
        {
            using var count = connection.CreateCommand();
            count.Transaction = transaction;
            count.CommandText = "SELECT COUNT(*) FROM content_sources WHERE content_kind = $kind;";
            count.Parameters.AddWithValue("$kind", kind);
            int total = Convert.ToInt32(count.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT s.source_id, s.content_kind, s.display_path, s.content_hash, s.source_bytes, s.url,
                       s.line_count, s.indexed_at_utc, COUNT(c.chunk_id) AS chunk_count
                FROM content_sources s
                LEFT JOIN content_chunks c ON c.source_id = s.source_id
                WHERE s.content_kind = $kind
                GROUP BY s.source_id, s.content_kind, s.display_path, s.content_hash, s.source_bytes,
                         s.url, s.line_count, s.indexed_at_utc
                ORDER BY s.display_path, s.source_id
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$limit", perKindLimit);

            var sources = new List<ExternalContentSource>(Math.Min(total, perKindLimit));
            using var reader = command.ExecuteReader();
            while (reader.Read())
                sources.Add(ReadSourceRow(reader));
            inventories.Add(new ExternalContentKindInventory(kind, total, sources));
        }

        transaction.Commit();
        return new ExternalContentInventory(perKindLimit, inventories);
    }

    public ExternalContentShape Shape(string contentDbPath, string sourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentDbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        if (!File.Exists(Path.GetFullPath(contentDbPath)))
            throw new InvalidOperationException("No content corpus exists. Import a file first.");

        using var connection = OpenReadOnly(contentDbPath);
        using var transaction = connection.BeginTransaction();
        ExternalContentSource source = ReadSource(connection, transaction, sourceId);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT line_start, raw_text
            FROM content_chunks
            WHERE source_id = $source
            ORDER BY line_start, chunk_id;
            """;
        command.Parameters.AddWithValue("$source", sourceId);

        var head = new List<ExternalContentLine>(ExternalContentShape.EdgeLineLimit);
        var tail = new Queue<ExternalContentLine>(ExternalContentShape.EdgeLineLimit);
        var severity = new int[6];
        int lastLine = 0;
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                int lineStart = reader.GetInt32(0);
                string[] lines = Normalize(reader.GetString(1)).Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    int lineNumber = lineStart + i;
                    if (lineNumber <= lastLine || lineNumber > source.LineCount)
                        continue;
                    lastLine = lineNumber;
                    var line = new ExternalContentLine(lineNumber, lines[i]);
                    if (head.Count < ExternalContentShape.EdgeLineLimit)
                        head.Add(line);
                    if (tail.Count == ExternalContentShape.EdgeLineLimit)
                        tail.Dequeue();
                    tail.Enqueue(line);
                    severity[(int)TextSeverity(lines[i])]++;
                }
            }
        }
        transaction.Commit();

        return new ExternalContentShape(
            source.SourceId,
            source.ContentKind,
            source.DisplayPath,
            source.ContentHash,
            source.SourceBytes,
            source.LineCount,
            head,
            [.. tail],
            new ExternalContentSeveritySummary(
                severity[(int)ExternalContentSeverity.Fatal],
                severity[(int)ExternalContentSeverity.Error],
                severity[(int)ExternalContentSeverity.Warning],
                severity[(int)ExternalContentSeverity.Info],
                severity[(int)ExternalContentSeverity.Debug],
                severity[(int)ExternalContentSeverity.Other]));
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
        if (contextLines > MaxContextLines)
        {
            throw new ArgumentOutOfRangeException(
                nameof(contextLines),
                $"context_lines must be <= {MaxContextLines}.");
        }
        if (!File.Exists(Path.GetFullPath(contentDbPath)))
            throw new InvalidOperationException("No content corpus exists. Import a file first.");

        using var connection = OpenReadOnly(contentDbPath);
        using var transaction = connection.BeginTransaction();
        ExternalContentSource source = ReadSource(connection, transaction, sourceId);
        if (line > source.LineCount)
            throw new InvalidOperationException($"Source '{sourceId}' has {source.LineCount} lines; requested line {line}.");

        int start = checked((int)Math.Max(1L, (long)line - contextLines));
        int end = checked((int)Math.Min(source.LineCount, (long)line + contextLines));
        bool clamped = end - start + 1 > MaxReadWindowLines;
        if (clamped)
        {
            int maxStart = Math.Max(1, source.LineCount - MaxReadWindowLines + 1);
            int preferredStart = line - ((MaxReadWindowLines - 1) / 2);
            start = Math.Clamp(preferredStart, 1, maxStart);
            end = start + MaxReadWindowLines - 1;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
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
        using (var reader = command.ExecuteReader())
        {
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
        }
        transaction.Commit();

        return new ExternalContentReadResult(
            source.SourceId,
            source.ContentKind,
            source.DisplayPath,
            source.ContentHash,
            start,
            end,
            linesByNumber.Select(static kv => new ExternalContentLine(kv.Key, kv.Value)).ToArray(),
            clamped,
            source.LineCount);
    }

    /// <summary>Largest number of ambiguous alias candidates reported back to the caller.</summary>
    public const int MaxAliasCandidates = 5;

    /// <summary>
    /// Resolves a read/remove target that may be a real <c>source_id</c> (<c>external_file:&lt;hash&gt;</c> /
    /// <c>web:&lt;hash&gt;</c>) OR a unique <c>display_path</c> alias. Direct source_id match wins; then a
    /// case-insensitive whole <c>display_path</c> match; then a case-insensitive path-SUFFIX match on a segment
    /// boundary (<c>plans/x.md</c> resolves <c>docs/plans/x.md</c>, while <c>ans/x.md</c> does not). Each alias
    /// tier is accepted only when exactly one external/web source matches, so an ambiguous alias never silently
    /// picks the wrong source; ambiguous suffix candidates are reported up to <see cref="MaxAliasCandidates"/>.
    /// Workspace-source ids are not aliased here (they are routed by <c>ResolveReadContentDbPath</c> in the
    /// caller).
    /// </summary>
    public SourceIdResolution ResolveSourceId(string contentDbPath, string sourceIdOrDisplayPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentDbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceIdOrDisplayPath);
        if (!File.Exists(Path.GetFullPath(contentDbPath)))
            return new SourceIdResolution(sourceIdOrDisplayPath, SourceId: null, Array.Empty<string>());

        using var connection = OpenReadOnly(contentDbPath);

        using (var direct = connection.CreateCommand())
        {
            direct.CommandText = "SELECT 1 FROM content_sources WHERE source_id = $id LIMIT 1;";
            direct.Parameters.AddWithValue("$id", sourceIdOrDisplayPath);
            if (direct.ExecuteScalar() is not null)
                return new SourceIdResolution(sourceIdOrDisplayPath, sourceIdOrDisplayPath, Array.Empty<string>());
        }

        using var alias = connection.CreateCommand();
        alias.CommandText = """
            SELECT source_id FROM content_sources
            WHERE display_path = $display COLLATE NOCASE
              AND content_kind IN ($external, $web)
            ORDER BY source_id
            LIMIT $limit;
            """;
        alias.Parameters.AddWithValue("$display", sourceIdOrDisplayPath);
        alias.Parameters.AddWithValue("$external", TextContentKind.ExternalFile);
        alias.Parameters.AddWithValue("$web", TextContentKind.Web);
        alias.Parameters.AddWithValue("$limit", MaxAliasCandidates);

        var candidates = new List<string>();
        using var reader = alias.ExecuteReader();
        while (reader.Read())
            candidates.Add(reader.GetString(0));

        if (candidates.Count == 1)
            return new SourceIdResolution(sourceIdOrDisplayPath, candidates[0], candidates);
        if (candidates.Count > 1)
            return new SourceIdResolution(sourceIdOrDisplayPath, SourceId: null, candidates);

        IReadOnlyList<string> suffixMatches = SuffixCandidates(connection, sourceIdOrDisplayPath);
        if (suffixMatches.Count == 1)
            return new SourceIdResolution(sourceIdOrDisplayPath, suffixMatches[0], suffixMatches);
        return new SourceIdResolution(sourceIdOrDisplayPath, SourceId: null, suffixMatches);
    }

    private static IReadOnlyList<string> SuffixCandidates(SqliteConnection connection, string suffix)
    {
        string needle = NormalizePathSeparators(suffix);
        if (needle.Length == 0)
            return Array.Empty<string>();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT source_id, display_path FROM content_sources
            WHERE content_kind IN ($external, $web)
            ORDER BY display_path, source_id;
            """;
        command.Parameters.AddWithValue("$external", TextContentKind.ExternalFile);
        command.Parameters.AddWithValue("$web", TextContentKind.Web);

        var matches = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read() && matches.Count < MaxAliasCandidates)
        {
            if (IsPathSuffixMatch(reader.GetString(1), needle))
                matches.Add(reader.GetString(0));
        }

        return matches;
    }

    /// <summary>
    /// True when <paramref name="displayPath"/> ends with <paramref name="normalizedSuffix"/> on a path-segment
    /// boundary, so <c>plans/x.md</c> matches <c>docs/plans/x.md</c> but <c>ans/x.md</c> does not.
    /// </summary>
    internal static bool IsPathSuffixMatch(string displayPath, string normalizedSuffix)
    {
        string path = NormalizePathSeparators(displayPath);
        if (path.Length < normalizedSuffix.Length)
            return false;
        if (!path.EndsWith(normalizedSuffix, StringComparison.OrdinalIgnoreCase))
            return false;
        return path.Length == normalizedSuffix.Length
            || path[path.Length - normalizedSuffix.Length - 1] == '/';
    }

    private static string NormalizePathSeparators(string value) => value.Replace('\\', '/').Trim();

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

    private static ExternalContentSource ReadSource(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
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

        return ReadSourceRow(reader);
    }

    private static ExternalContentSource ReadSourceRow(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt64(4),
            reader.GetInt32(6),
            reader.GetString(7),
            reader.GetInt32(8),
            reader.IsDBNull(5) ? null : reader.GetString(5));

    private static void InsertSourceMetadata(
        SqliteConnection connection,
        string sourceId,
        string contentKind,
        string? path,
        string? url,
        string displayPath,
        string language,
        string contentHash,
        long sourceBytes,
        int lineCount)
    {
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
        source.Parameters.AddWithValue("$lines", lineCount);
        source.Parameters.AddWithValue("$indexed", DateTimeOffset.UtcNow.ToString("O"));
        source.ExecuteNonQuery();
    }

    private static int StreamNormalizedLines(
        TextReader reader,
        Action<string> accept,
        Action afterRead)
    {
        var line = new StringBuilder();
        char[] buffer = new char[16 * 1024];
        bool previousWasCarriageReturn = false;
        int lineCount = 0;
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                char ch = buffer[i];
                if (previousWasCarriageReturn)
                {
                    previousWasCarriageReturn = false;
                    if (ch == '\n')
                        continue;
                }

                if (ch is '\r' or '\n')
                {
                    accept(line.ToString());
                    line.Clear();
                    lineCount++;
                    previousWasCarriageReturn = ch == '\r';
                }
                else
                {
                    if (line.Length >= MaxStreamingLineChars)
                    {
                        throw new InvalidOperationException(
                            $"External content logical line exceeds the {MaxStreamingLineChars}-character " +
                            "streaming import limit. Split the line before importing the file.");
                    }
                    line.Append(ch);
                }
            }
            afterRead();
        }

        accept(line.ToString());
        return lineCount + 1;
    }

    private static ExternalContentSeverity TextSeverity(string line)
    {
        if (ContainsSeverityWord(line, "fatal") || ContainsSeverityWord(line, "panic"))
            return ExternalContentSeverity.Fatal;
        if (ContainsSeverityWord(line, "error") || ContainsSeverityWord(line, "exception")
            || ContainsSeverityWord(line, "failed") || ContainsSeverityWord(line, "failure"))
            return ExternalContentSeverity.Error;
        if (ContainsSeverityWord(line, "warn") || ContainsSeverityWord(line, "warning"))
            return ExternalContentSeverity.Warning;
        if (ContainsSeverityWord(line, "info") || ContainsSeverityWord(line, "notice"))
            return ExternalContentSeverity.Info;
        if (ContainsSeverityWord(line, "debug") || ContainsSeverityWord(line, "trace"))
            return ExternalContentSeverity.Debug;
        return ExternalContentSeverity.Other;
    }

    private static bool ContainsSeverityWord(string line, string word)
    {
        int start = 0;
        while ((start = line.IndexOf(word, start, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int end = start + word.Length;
            bool leftBoundary = start == 0 || !char.IsLetterOrDigit(line[start - 1]);
            bool rightBoundary = end == line.Length || !char.IsLetterOrDigit(line[end]);
            if (leftBoundary && rightBoundary)
                return true;
            start = end;
        }
        return false;
    }

    private sealed class StreamingChunkWriter : IDisposable
    {
        private readonly string _sourceId;
        private readonly SqliteCommand _chunk;
        private readonly SqliteParameter _pcChunk;
        private readonly SqliteParameter _pcLineStart;
        private readonly SqliteParameter _pcLineEnd;
        private readonly SqliteParameter _pcByteStart;
        private readonly SqliteParameter _pcByteEnd;
        private readonly SqliteParameter _pcRaw;
        private readonly SqliteParameter _pcDocLen;
        private readonly SqliteCommand _fts;
        private readonly SqliteParameter _pfChunk;
        private readonly SqliteParameter _pfBody;
        private readonly List<StreamingLine> _lines = new(ContentCorpusChunker.DefaultChunkLines);
        private readonly List<string> _tokens = new(128);
        private int _lineCount;
        private int _lastEmittedLine;
        private int _bufferedChars;
        private long _nextByteStart;

        public StreamingChunkWriter(
            SqliteConnection connection,
            string sourceId,
            string contentKind,
            string? path,
            string? url,
            string displayPath,
            string language,
            long sourceBytes)
        {
            _sourceId = sourceId;
            _chunk = connection.CreateCommand();
            _chunk.CommandText = """
                INSERT INTO content_chunks
                    (chunk_id, source_id, content_kind, path, url, display_path, language, line_start,
                     line_end, byte_start, byte_end, raw_text, doc_len, is_test, source_bytes,
                     containing_symbol_id, containing_symbol_name)
                VALUES ($chunk, $source, $kind, $path, $url, $display, $language, $line_start, $line_end,
                        $byte_start, $byte_end, $raw, $doc_len, 0, $source_bytes, NULL, NULL);
                """;
            _pcChunk = _chunk.Parameters.Add("$chunk", SqliteType.Text);
            _chunk.Parameters.AddWithValue("$source", sourceId);
            _chunk.Parameters.AddWithValue("$kind", contentKind);
            _chunk.Parameters.AddWithValue("$path", (object?)path ?? DBNull.Value);
            _chunk.Parameters.AddWithValue("$url", (object?)url ?? DBNull.Value);
            _chunk.Parameters.AddWithValue("$display", displayPath);
            _chunk.Parameters.AddWithValue("$language", language);
            _pcLineStart = _chunk.Parameters.Add("$line_start", SqliteType.Integer);
            _pcLineEnd = _chunk.Parameters.Add("$line_end", SqliteType.Integer);
            _pcByteStart = _chunk.Parameters.Add("$byte_start", SqliteType.Integer);
            _pcByteEnd = _chunk.Parameters.Add("$byte_end", SqliteType.Integer);
            _pcRaw = _chunk.Parameters.Add("$raw", SqliteType.Text);
            _pcDocLen = _chunk.Parameters.Add("$doc_len", SqliteType.Integer);
            _chunk.Parameters.AddWithValue("$source_bytes", sourceBytes);

            _fts = connection.CreateCommand();
            _fts.CommandText = "INSERT INTO content_fts(chunk_id, body) VALUES ($chunk, $body);";
            _pfChunk = _fts.Parameters.Add("$chunk", SqliteType.Text);
            _pfBody = _fts.Parameters.Add("$body", SqliteType.Text);
        }

        public int ChunkCount { get; private set; }

        public void AddLine(string text)
        {
            if (text.Length > MaxStreamingLineChars)
            {
                throw new InvalidOperationException(
                    $"External content logical line exceeds the {MaxStreamingLineChars}-character " +
                    "streaming import limit. Split the line before importing the file.");
            }
            if (_lines.Count > 0 && BufferedCharsWith(text.Length) > MaxStreamingChunkChars)
            {
                Emit();
                RetainOverlapFor(text.Length);
            }

            _lineCount++;
            _lines.Add(new StreamingLine(_lineCount, _nextByteStart, text));
            _bufferedChars += text.Length + (_lines.Count > 1 ? 1 : 0);
            _nextByteStart += Encoding.UTF8.GetByteCount(text) + 1L;
            if (_lines.Count < ContentCorpusChunker.DefaultChunkLines)
                return;

            Emit();
            RetainOverlapFor(nextLineChars: 0);
        }

        public void Complete()
        {
            if (_lastEmittedLine < _lineCount)
                Emit();
        }

        public void Dispose()
        {
            _chunk.Dispose();
            _fts.Dispose();
        }

        private void Emit()
        {
            if (_bufferedChars > MaxStreamingChunkChars)
            {
                throw new InvalidOperationException(
                    $"External content chunk exceeded the {MaxStreamingChunkChars}-character streaming limit.");
            }
            StreamingLine first = _lines[0];
            StreamingLine last = _lines[^1];
            string text = string.Join('\n', _lines.Select(static line => line.Text));
            string chunkId = _sourceId + "#" +
                first.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" +
                first.ByteStart.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _tokens.Clear();
            CodeTokenizer.Tokenize(text, _tokens);

            _pcChunk.Value = chunkId;
            _pcLineStart.Value = first.Number;
            _pcLineEnd.Value = last.Number;
            _pcByteStart.Value = first.ByteStart;
            _pcByteEnd.Value = last.ByteStart + Encoding.UTF8.GetByteCount(last.Text);
            _pcRaw.Value = text;
            _pcDocLen.Value = _tokens.Count;
            _chunk.ExecuteNonQuery();

            _pfChunk.Value = chunkId;
            _pfBody.Value = string.Join(' ', _tokens);
            _fts.ExecuteNonQuery();
            _lastEmittedLine = last.Number;
            ChunkCount++;
        }

        private int BufferedCharsWith(int textChars) =>
            _bufferedChars + (_lines.Count > 0 ? 1 : 0) + textChars;

        private void RetainOverlapFor(int nextLineChars)
        {
            int removeCount = Math.Max(0, _lines.Count - ContentCorpusChunker.DefaultOverlapLines);
            if (removeCount > 0)
                _lines.RemoveRange(0, removeCount);
            RecalculateBufferedChars();

            while (_lines.Count > 0 && BufferedCharsWith(nextLineChars) > MaxStreamingChunkChars)
            {
                _lines.RemoveAt(0);
                RecalculateBufferedChars();
            }
        }

        private void RecalculateBufferedChars()
        {
            _bufferedChars = _lines.Sum(static line => line.Text.Length);
            if (_lines.Count > 1)
                _bufferedChars += _lines.Count - 1;
        }
    }

    private sealed record StreamingLine(int Number, long ByteStart, string Text);

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

public sealed record ExternalContentKindInventory(
    string ContentKind,
    int TotalCount,
    IReadOnlyList<ExternalContentSource> Sources)
{
    public int ReturnedCount => Sources.Count;
    public int OmittedCount => TotalCount - ReturnedCount;
}

public sealed record ExternalContentInventory(
    int PerKindLimit,
    IReadOnlyList<ExternalContentKindInventory> Kinds)
{
    public int TotalCount => Kinds.Sum(static kind => kind.TotalCount);
    public int ReturnedCount => Kinds.Sum(static kind => kind.ReturnedCount);
    public int OmittedCount => TotalCount - ReturnedCount;
}

public enum ExternalContentSeverity
{
    Fatal,
    Error,
    Warning,
    Info,
    Debug,
    Other,
}

public sealed record ExternalContentSeveritySummary(
    int Fatal,
    int Error,
    int Warning,
    int Info,
    int Debug,
    int Other);

public sealed record ExternalContentShape(
    string SourceId,
    string ContentKind,
    string DisplayPath,
    string ContentHash,
    long SourceBytes,
    int LineCount,
    IReadOnlyList<ExternalContentLine> Head,
    IReadOnlyList<ExternalContentLine> Tail,
    ExternalContentSeveritySummary Severity)
{
    public const int EdgeLineLimit = 5;
}

/// <summary>
/// A rendered read window. <paramref name="Clamped"/> is true when the requested window exceeded
/// <see cref="ContentCorpusExternalStore.MaxReadWindowLines"/> and was trimmed to fit, so callers can offer a
/// continuation; a window merely clipped by the start/end of the source is not clamped.
/// <paramref name="SourceLineCount"/> is the whole source's line count, which callers need to keep a
/// continuation hint inside the source.
/// </summary>
public sealed record ExternalContentReadResult(
    string SourceId,
    string ContentKind,
    string DisplayPath,
    string ContentHash,
    int LineStart,
    int LineEnd,
    IReadOnlyList<ExternalContentLine> Lines,
    bool Clamped = false,
    int SourceLineCount = 0);

public sealed record ExternalContentLine(int LineNumber, string Text);

public sealed record ExternalContentRemoveResult(
    string SourceId,
    bool Removed,
    int SourceCount,
    int ChunkCount);

public sealed record SourceIdResolution(
    string Requested,
    string? SourceId,
    IReadOnlyList<string> Candidates)
{
    public bool Found => SourceId is not null;
    public bool Ambiguous => SourceId is null && Candidates.Count > 1;
}
