using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Miller.Core.Search;
using Miller.Core.Tokenization;
using Miller.Indexing.Reads;

namespace Miller.Indexing;

public static partial class ContentCorpusWriter
{
    private const long MaxWorkspaceFileBytes = 1_048_576;

    public static ContentCorpusFacts Write(
        string contentDbPath,
        string symbolsDbPath,
        string workspaceRoot,
        string? workspaceId,
        long revision,
        TimeSpan? writeLockTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentDbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolsDbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        string fullPath = Path.GetFullPath(contentDbPath);
        string dir = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException($"Path has no directory: {contentDbPath}", nameof(contentDbPath));
        Directory.CreateDirectory(dir);
        string tempPath = Path.Combine(dir, ".content-build-" + Guid.NewGuid().ToString("N") + ".db");
        SidecarStagingReaper.ReapStale(dir, ".content-build-", SidecarStagingReaper.DefaultStaleAge, tempPath);

        ContentCorpusFacts facts;
        try
        {
            facts = BuildInto(
                tempPath,
                symbolsDbPath,
                workspaceRoot,
                workspaceId,
                revision,
                sourceRows: null,
                symbolsByPath: null,
                artifactId: TryReadArtifactId(symbolsDbPath),
                storeStamp: null);
            using (ContentCorpusWriteLock.AcquireFor(fullPath, writeLockTimeout))
            {
                int preserved = PreserveExternalSources(tempPath, fullPath);
                SqliteConnection.ClearAllPools();
                for (int attempt = 1; ; attempt++)
                {
                    try { File.Move(tempPath, fullPath, overwrite: true); break; }
                    catch (IOException) when (attempt < 5) { Thread.Sleep(20 * attempt); }
                }
                ClearPreservationFailure(fullPath);
                if (preserved > 0)
                    facts = ReadFacts(fullPath, revision);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                SqliteConnection.ClearAllPools();
                try { File.Delete(tempPath); } catch (IOException) { }
            }
        }

        return facts with { Path = fullPath };
    }

    public static ContentCorpusFacts WriteStoreView(
        string contentDbPath,
        IWorkspaceReadSession session,
        TimeSpan? writeLockTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentDbPath);
        ArgumentNullException.ThrowIfNull(session);
        StoreSidecarStamp stamp = StoreSidecarStamp.FromSnapshot(StoreSidecarKind.Content, session.Snapshot);
        long storeCursor = stamp.StoreLogSequence;
        IReadOnlyList<SourceRow> sourceRows = session.Read(ReadSourceRows);
        IReadOnlyDictionary<string, IReadOnlyList<ContentCorpusSymbolSpan>> symbolsByPath =
            session.Read(ReadSymbolSpans);

        string fullPath = Path.GetFullPath(contentDbPath);
        string dir = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException($"Path has no directory: {contentDbPath}", nameof(contentDbPath));
        Directory.CreateDirectory(dir);
        string tempPath = Path.Combine(dir, ".content-build-" + Guid.NewGuid().ToString("N") + ".db");
        SidecarStagingReaper.ReapStale(dir, ".content-build-", SidecarStagingReaper.DefaultStaleAge, tempPath);

        ContentCorpusFacts facts;
        try
        {
            facts = BuildInto(
                tempPath,
                symbolsDbPath: null,
                session.Snapshot.WorkspaceRoot,
                session.Snapshot.WorkspaceId,
                storeCursor,
                sourceRows,
                symbolsByPath,
                artifactId: null,
                stamp);
            using (ContentCorpusWriteLock.AcquireFor(fullPath, writeLockTimeout))
            {
                int preserved = PreserveExternalSources(tempPath, fullPath);
                SqliteConnection.ClearAllPools();
                for (int attempt = 1; ; attempt++)
                {
                    try { File.Move(tempPath, fullPath, overwrite: true); break; }
                    catch (IOException) when (attempt < 5) { Thread.Sleep(20 * attempt); }
                }
                ClearPreservationFailure(fullPath);
                if (preserved > 0)
                    facts = ReadFacts(fullPath, storeCursor);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                SqliteConnection.ClearAllPools();
                try { File.Delete(tempPath); } catch (IOException) { }
            }
        }

        return facts with { Path = fullPath };
    }

    public static void ApplyStoreFileChanges(
        string contentDbPath,
        IWorkspaceReadSession session,
        IReadOnlyCollection<string> paths,
        StoreSidecarStamp storeStamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentDbPath);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(storeStamp);

        var distinctPaths = paths
            .Where(static p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var pathSet = distinctPaths.ToHashSet(StringComparer.Ordinal);

        IReadOnlyList<SourceRow> sourceRows = session.Read(connection =>
            ReadSourceRows(connection).Where(row => pathSet.Contains(row.Path)).ToArray());
        IReadOnlyDictionary<string, IReadOnlyList<ContentCorpusSymbolSpan>> symbolsByPath =
            session.Read(connection => FilterSymbolSpans(ReadSymbolSpans(connection), pathSet));

        List<SourceBuildInput> accepted = CollectAccepted(
            sourceRows,
            symbolsByPath,
            session.Snapshot.WorkspaceRoot,
            session.Snapshot.WorkspaceId,
            out _,
            out _,
            out _,
            out _,
            out _,
            out _,
            out _);

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(contentDbPath),
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var tx = connection.BeginTransaction();
        if (distinctPaths.Length > 0)
            DeleteWorkspaceSourcesForPaths(connection, distinctPaths);
        InsertSourcesAndChunks(connection, accepted, session.Snapshot.WorkspaceId, storeStamp.StoreLogSequence);
        UpdateMetaCounts(connection);
        using (var meta = connection.CreateCommand())
        {
            meta.Transaction = tx;
            meta.CommandText = """
                UPDATE content_meta
                SET workspace_revision=$revision,
                    updated_at_utc=$updated
                WHERE schema_version=$schema_version
                  AND chunker_version=$chunker_version;
                """;
            meta.Parameters.AddWithValue("$revision", storeStamp.StoreLogSequence);
            meta.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            meta.Parameters.AddWithValue("$schema_version", ContentCorpusSchema.SchemaVersion);
            meta.Parameters.AddWithValue("$chunker_version", ContentCorpusSchema.ChunkerVersion);
            if (meta.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("content.db content_meta could not be stamped after a file delta.");
        }

        StoreSidecarCatalog.Stamp(connection, tx, storeStamp);
        tx.Commit();
    }

    internal static bool TryFastForwardStoreMetadata(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long revision)
    {
        using (var sources = connection.CreateCommand())
        {
            sources.Transaction = transaction;
            sources.CommandText = "UPDATE content_sources SET workspace_revision=$revision WHERE workspace_revision IS NOT NULL;";
            sources.Parameters.AddWithValue("$revision", revision);
            sources.ExecuteNonQuery();
        }

        using var metadata = connection.CreateCommand();
        metadata.Transaction = transaction;
        metadata.CommandText = """
            UPDATE content_meta
            SET workspace_revision=$revision,
                updated_at_utc=$updated
            WHERE schema_version=$schema_version
              AND chunker_version=$chunker_version;
            """;
        metadata.Parameters.AddWithValue("$revision", revision);
        metadata.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        metadata.Parameters.AddWithValue("$schema_version", ContentCorpusSchema.SchemaVersion);
        metadata.Parameters.AddWithValue("$chunker_version", ContentCorpusSchema.ChunkerVersion);
        return metadata.ExecuteNonQuery() == 1;
    }

    private static int PreserveExternalSources(string tempPath, string existingPath)
    {
        if (!File.Exists(existingPath))
            return 0;
        if (!HasSqliteHeader(existingPath))
            return 0;

        bool importsProven = false;
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = tempPath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());
            connection.Open();

            using (var attach = connection.CreateCommand())
            {
                attach.CommandText = "ATTACH DATABASE $existing AS old;";
                attach.Parameters.AddWithValue("$existing", Path.GetFullPath(existingPath));
                attach.ExecuteNonQuery();
            }

            try
            {
                int expectedSourceCount = CountExternalSources(connection);
                if (expectedSourceCount == 0)
                    return 0;
                importsProven = true;
                if (!HasCompatibleExternalSourceShape(connection))
                {
                    throw new InvalidOperationException(
                        "Existing content.db imported-content tables are incompatible with the current corpus.");
                }

                using var tx = connection.BeginTransaction();
                int sourceCount = ExecuteNonQuery(connection, """
                    INSERT INTO content_sources
                        (source_id, content_kind, workspace_id, workspace_revision, path, url, display_path,
                         language, content_hash, source_bytes, line_count, is_test, status, indexed_at_utc)
                    SELECT source_id, content_kind, workspace_id, workspace_revision, path, url, display_path,
                           language, content_hash, source_bytes, line_count, is_test, status, indexed_at_utc
                    FROM old.content_sources
                    WHERE content_kind IN ($external, $web)
                      AND status = 'active';
                    """);
                if (sourceCount != expectedSourceCount)
                {
                    throw new InvalidOperationException(
                        $"Expected to preserve {expectedSourceCount} imported sources but copied {sourceCount}.");
                }

                ExecuteNonQuery(connection, """
                    INSERT INTO content_chunks
                        (chunk_id, source_id, content_kind, path, url, display_path, language, line_start,
                         line_end, byte_start, byte_end, raw_text, doc_len, is_test, source_bytes,
                         containing_symbol_id, containing_symbol_name)
                    SELECT c.chunk_id, c.source_id, c.content_kind, c.path, c.url, c.display_path, c.language,
                           c.line_start, c.line_end, c.byte_start, c.byte_end, c.raw_text, c.doc_len,
                           c.is_test, c.source_bytes, c.containing_symbol_id, c.containing_symbol_name
                    FROM old.content_chunks c
                    JOIN old.content_sources s ON s.source_id = c.source_id
                    WHERE s.content_kind IN ($external, $web)
                      AND s.status = 'active';
                    """);
                ExecuteNonQuery(connection, """
                    INSERT INTO content_symbol_spans
                        (source_id, symbol_id, symbol_name, path, start_line, end_line)
                    SELECT sp.source_id, sp.symbol_id, sp.symbol_name, sp.path, sp.start_line, sp.end_line
                    FROM old.content_symbol_spans sp
                    JOIN old.content_sources s ON s.source_id = sp.source_id
                    WHERE s.content_kind IN ($external, $web)
                      AND s.status = 'active';
                    """);
                ExecuteNonQuery(connection, """
                    INSERT INTO content_fts(chunk_id, body)
                    SELECT f.chunk_id, f.body
                    FROM old.content_fts f
                    JOIN old.content_chunks c ON c.chunk_id = f.chunk_id
                    JOIN old.content_sources s ON s.source_id = c.source_id
                    WHERE s.content_kind IN ($external, $web)
                      AND s.status = 'active';
                    """);
                UpdateMetaCounts(connection);
                tx.Commit();
                return sourceCount;
            }
            finally
            {
                using var detach = connection.CreateCommand();
                detach.CommandText = "DETACH DATABASE old;";
                detach.ExecuteNonQuery();
            }
        }
        catch (Exception ex) when (
            ex is SqliteException or InvalidOperationException or IOException
                or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            if (importsProven)
            {
                RecordPreservationFailure(existingPath, ex.Message);
                throw new ContentImportPreservationException(
                    $"Content rebuild refused because Miller could not preserve imported content from '{existingPath}'. " +
                    "The existing content.db was left unchanged.",
                    ex);
            }
            throw new InvalidOperationException(
                $"Content rebuild could not read the existing derived corpus at '{existingPath}'.",
                ex);
        }
    }

    internal static string? TryReadPreservationFailure(string contentDbPath)
    {
        try
        {
            string markerPath = PreservationFailurePathFor(contentDbPath);
            if (!File.Exists(markerPath) || !File.Exists(contentDbPath))
                return null;
            PreservationFailureStamp? stamp =
                JsonSerializer.Deserialize(
                    File.ReadAllText(markerPath),
                    ContentCorpusWriterJsonContext.Default.PreservationFailureStamp);
            var info = new FileInfo(contentDbPath);
            return stamp is not null &&
                stamp.Length == info.Length &&
                stamp.LastWriteUtcTicks == info.LastWriteTimeUtc.Ticks
                    ? stamp.Error
                    : null;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private static void RecordPreservationFailure(string contentDbPath, string error)
    {
        try
        {
            var info = new FileInfo(contentDbPath);
            var stamp = new PreservationFailureStamp(info.Length, info.LastWriteTimeUtc.Ticks, error);
            File.WriteAllText(
                PreservationFailurePathFor(contentDbPath),
                JsonSerializer.Serialize(
                    stamp,
                    ContentCorpusWriterJsonContext.Default.PreservationFailureStamp));
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
    }

    private static void ClearPreservationFailure(string contentDbPath)
    {
        try
        {
            string markerPath = PreservationFailurePathFor(contentDbPath);
            if (File.Exists(markerPath))
                File.Delete(markerPath);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
    }

    private static string PreservationFailurePathFor(string contentDbPath) =>
        Path.GetFullPath(contentDbPath) + ".preservation-error";

    private sealed record PreservationFailureStamp(long Length, long LastWriteUtcTicks, string Error);

    [JsonSerializable(typeof(PreservationFailureStamp))]
    private sealed partial class ContentCorpusWriterJsonContext : JsonSerializerContext;

    private static bool HasSqliteHeader(string path)
    {
        Span<byte> header = stackalloc byte[16];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return stream.Read(header) == header.Length
            && header.SequenceEqual("SQLite format 3\0"u8);
    }

    private static int CountExternalSources(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM old.content_sources
            WHERE content_kind IN ($external, $web)
              AND status = 'active';
            """;
        command.Parameters.AddWithValue("$external", TextContentKind.ExternalFile);
        command.Parameters.AddWithValue("$web", TextContentKind.Web);
        return checked(Convert.ToInt32(command.ExecuteScalar()));
    }

    private static bool HasCompatibleExternalSourceShape(SqliteConnection connection)
    {
        return HasColumns(
                connection,
                "content_sources",
                "source_id", "content_kind", "workspace_id", "workspace_revision", "path", "url",
                "display_path", "language", "content_hash", "source_bytes", "line_count", "is_test",
                "status", "indexed_at_utc")
            && HasColumns(
                connection,
                "content_chunks",
                "chunk_id", "source_id", "content_kind", "path", "url", "display_path", "language",
                "line_start", "line_end", "byte_start", "byte_end", "raw_text", "doc_len", "is_test",
                "source_bytes", "containing_symbol_id", "containing_symbol_name")
            && HasColumns(
                connection,
                "content_symbol_spans",
                "source_id", "symbol_id", "symbol_name", "path", "start_line", "end_line")
            && HasColumns(connection, "content_fts", "chunk_id", "body");
    }

    private static bool HasColumns(
        SqliteConnection connection,
        string table,
        params string[] requiredColumns)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA old.table_info(\"{table}\");";
        using var reader = command.ExecuteReader();
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
            columns.Add(reader.GetString(1));
        return requiredColumns.All(columns.Contains);
    }

    private static int ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$external", TextContentKind.ExternalFile);
        command.Parameters.AddWithValue("$web", TextContentKind.Web);
        return command.ExecuteNonQuery();
    }

    private static void UpdateMetaCounts(SqliteConnection connection)
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

    private static ContentCorpusFacts ReadFacts(string contentDbPath, long revision)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(contentDbPath),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT schema_version, workspace_revision, source_count, chunk_count,
                   indexed_source_bytes, stored_raw_bytes, skipped_status, skipped_scope,
                   skipped_large, skipped_missing, skipped_hash, skipped_utf8, skipped_io
            FROM content_meta
            LIMIT 1;
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw new InvalidOperationException("content_meta has no row");

        return new ContentCorpusFacts(
            "current",
            Path.GetFullPath(contentDbPath),
            reader.GetInt32(0),
            revision,
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.GetInt32(8),
            reader.GetInt32(9),
            reader.GetInt32(10),
            reader.GetInt32(11),
            reader.GetInt32(12));
    }

    private static ContentCorpusFacts BuildInto(
        string tempPath,
        string? symbolsDbPath,
        string workspaceRoot,
        string? workspaceId,
        long revision,
        IReadOnlyList<SourceRow>? sourceRows,
        IReadOnlyDictionary<string, IReadOnlyList<ContentCorpusSymbolSpan>>? symbolsByPath,
        string? artifactId,
        StoreSidecarStamp? storeStamp)
    {
        sourceRows ??= ReadSourceRows(symbolsDbPath
            ?? throw new ArgumentException("A legacy content build requires the source artifact path."));
        symbolsByPath ??= ReadSymbolSpans(symbolsDbPath!);
        List<SourceBuildInput> accepted = CollectAccepted(
            sourceRows,
            symbolsByPath,
            workspaceRoot,
            workspaceId,
            out int skippedStatus,
            out int skippedScope,
            out int skippedLarge,
            out int skippedMissing,
            out int skippedHash,
            out int skippedUtf8,
            out int skippedIo);
        long indexedSourceBytes = accepted.Sum(static source => source.SourceBytes);
        long storedRawBytes = accepted.Sum(static source =>
            source.Chunks.Sum(static chunk => Encoding.UTF8.GetByteCount(chunk.Text)));

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = tempPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=MEMORY; PRAGMA synchronous=OFF;";
            pragma.ExecuteNonQuery();
        }
        using (var ddl = connection.CreateCommand())
        {
            ddl.CommandText = ContentCorpusSchema.SchemaDdl;
            ddl.ExecuteNonQuery();
        }

        using var tx = connection.BeginTransaction();
        InsertSourcesAndChunks(connection, accepted, workspaceId, revision);
        using (var meta = connection.CreateCommand())
        {
            meta.CommandText = """
                INSERT INTO content_meta
                    (schema_version, workspace_revision, chunker_version, source_count, chunk_count,
                     indexed_source_bytes, stored_raw_bytes, updated_at_utc, skipped_status, skipped_scope,
                     skipped_large, skipped_missing, skipped_hash, skipped_utf8, skipped_io, artifact_id)
                VALUES ($schema, $revision, $chunker, $sources, $chunks, $source_bytes, $stored_bytes, $updated,
                        $status, $scope, $large, $missing, $hash, $utf8, $io, $artifact);
                """;
            meta.Parameters.AddWithValue("$schema", ContentCorpusSchema.SchemaVersion);
            meta.Parameters.AddWithValue("$revision", revision);
            meta.Parameters.AddWithValue("$chunker", ContentCorpusSchema.ChunkerVersion);
            meta.Parameters.AddWithValue("$sources", accepted.Count);
            meta.Parameters.AddWithValue("$chunks", accepted.Sum(static s => s.Chunks.Count));
            meta.Parameters.AddWithValue("$source_bytes", indexedSourceBytes);
            meta.Parameters.AddWithValue("$stored_bytes", storedRawBytes);
            meta.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            meta.Parameters.AddWithValue("$status", skippedStatus);
            meta.Parameters.AddWithValue("$scope", skippedScope);
            meta.Parameters.AddWithValue("$large", skippedLarge);
            meta.Parameters.AddWithValue("$missing", skippedMissing);
            meta.Parameters.AddWithValue("$hash", skippedHash);
            meta.Parameters.AddWithValue("$utf8", skippedUtf8);
            meta.Parameters.AddWithValue("$io", skippedIo);
            meta.Parameters.AddWithValue("$artifact", (object?)artifactId ?? DBNull.Value);
            meta.ExecuteNonQuery();
        }
        if (storeStamp is not null)
            StoreSidecarCatalog.Stamp(connection, tx, storeStamp);
        tx.Commit();

        return new ContentCorpusFacts(
            "current",
            tempPath,
            ContentCorpusSchema.SchemaVersion,
            revision,
            accepted.Count,
            accepted.Sum(static s => s.Chunks.Count),
            indexedSourceBytes,
            storedRawBytes,
            skippedStatus,
            skippedScope,
            skippedLarge,
            skippedMissing,
            skippedHash,
            skippedUtf8,
            skippedIo);
    }

    private static void InsertSourcesAndChunks(
        SqliteConnection connection,
        IReadOnlyList<SourceBuildInput> sources,
        string? workspaceId,
        long revision)
    {
        using var sourceCmd = connection.CreateCommand();
        sourceCmd.CommandText = """
            INSERT INTO content_sources
                (source_id, content_kind, workspace_id, workspace_revision, path, url, display_path,
                 language, content_hash, source_bytes, line_count, is_test, status, indexed_at_utc)
            VALUES ($id, $kind, $workspace, $revision, $path, NULL, $display, $language, $hash,
                    $bytes, $lines, $test, 'active', $indexed);
            """;
        var psId = sourceCmd.Parameters.Add("$id", SqliteType.Text);
        var psKind = sourceCmd.Parameters.Add("$kind", SqliteType.Text);
        var psWorkspace = sourceCmd.Parameters.Add("$workspace", SqliteType.Text);
        var psRevision = sourceCmd.Parameters.Add("$revision", SqliteType.Integer);
        var psPath = sourceCmd.Parameters.Add("$path", SqliteType.Text);
        var psDisplay = sourceCmd.Parameters.Add("$display", SqliteType.Text);
        var psLanguage = sourceCmd.Parameters.Add("$language", SqliteType.Text);
        var psHash = sourceCmd.Parameters.Add("$hash", SqliteType.Text);
        var psBytes = sourceCmd.Parameters.Add("$bytes", SqliteType.Integer);
        var psLines = sourceCmd.Parameters.Add("$lines", SqliteType.Integer);
        var psTest = sourceCmd.Parameters.Add("$test", SqliteType.Integer);
        var psIndexed = sourceCmd.Parameters.Add("$indexed", SqliteType.Text);

        using var chunkCmd = connection.CreateCommand();
        chunkCmd.CommandText = """
            INSERT INTO content_chunks
                (chunk_id, source_id, content_kind, path, url, display_path, language, line_start,
                 line_end, byte_start, byte_end, raw_text, doc_len, is_test, source_bytes,
                 containing_symbol_id, containing_symbol_name)
            VALUES ($chunk, $source, $kind, $path, NULL, $display, $language, $line_start, $line_end,
                    $byte_start, $byte_end, $raw, $doc_len, $test, $source_bytes, $symbol_id, $symbol_name);
            """;
        var pcChunk = chunkCmd.Parameters.Add("$chunk", SqliteType.Text);
        var pcSource = chunkCmd.Parameters.Add("$source", SqliteType.Text);
        var pcKind = chunkCmd.Parameters.Add("$kind", SqliteType.Text);
        var pcPath = chunkCmd.Parameters.Add("$path", SqliteType.Text);
        var pcDisplay = chunkCmd.Parameters.Add("$display", SqliteType.Text);
        var pcLanguage = chunkCmd.Parameters.Add("$language", SqliteType.Text);
        var pcLineStart = chunkCmd.Parameters.Add("$line_start", SqliteType.Integer);
        var pcLineEnd = chunkCmd.Parameters.Add("$line_end", SqliteType.Integer);
        var pcByteStart = chunkCmd.Parameters.Add("$byte_start", SqliteType.Integer);
        var pcByteEnd = chunkCmd.Parameters.Add("$byte_end", SqliteType.Integer);
        var pcRaw = chunkCmd.Parameters.Add("$raw", SqliteType.Text);
        var pcDocLen = chunkCmd.Parameters.Add("$doc_len", SqliteType.Integer);
        var pcTest = chunkCmd.Parameters.Add("$test", SqliteType.Integer);
        var pcSourceBytes = chunkCmd.Parameters.Add("$source_bytes", SqliteType.Integer);
        var pcSymbolId = chunkCmd.Parameters.Add("$symbol_id", SqliteType.Text);
        var pcSymbolName = chunkCmd.Parameters.Add("$symbol_name", SqliteType.Text);

        using var ftsCmd = connection.CreateCommand();
        ftsCmd.CommandText = "INSERT INTO content_fts(chunk_id, body) VALUES ($chunk, $body);";
        var pfChunk = ftsCmd.Parameters.Add("$chunk", SqliteType.Text);
        var pfBody = ftsCmd.Parameters.Add("$body", SqliteType.Text);
        var tokens = new List<string>(128);

        using var spanCmd = connection.CreateCommand();
        spanCmd.CommandText = """
            INSERT INTO content_symbol_spans
                (source_id, symbol_id, symbol_name, path, start_line, end_line)
            VALUES ($source, $symbol_id, $symbol_name, $path, $start_line, $end_line);
            """;
        var psSpanSource = spanCmd.Parameters.Add("$source", SqliteType.Text);
        var psSpanSymbolId = spanCmd.Parameters.Add("$symbol_id", SqliteType.Text);
        var psSpanSymbolName = spanCmd.Parameters.Add("$symbol_name", SqliteType.Text);
        var psSpanPath = spanCmd.Parameters.Add("$path", SqliteType.Text);
        var psSpanStart = spanCmd.Parameters.Add("$start_line", SqliteType.Integer);
        var psSpanEnd = spanCmd.Parameters.Add("$end_line", SqliteType.Integer);

        foreach (SourceBuildInput source in sources)
        {
            psId.Value = source.SourceId;
            psKind.Value = source.ContentKind;
            psWorkspace.Value = (object?)workspaceId ?? DBNull.Value;
            psRevision.Value = revision;
            psPath.Value = source.Row.Path;
            psDisplay.Value = source.Row.Path;
            psLanguage.Value = source.Row.Language;
            psHash.Value = source.Row.ContentHash;
            psBytes.Value = source.SourceBytes;
            psLines.Value = ContentCorpusChunker.CountLines(source.Text);
            psTest.Value = source.Chunks.Any(static c => c.IsTest) ? 1 : 0;
            psIndexed.Value = DateTimeOffset.UtcNow.ToString("O");
            sourceCmd.ExecuteNonQuery();

            foreach (TextContentDocument chunk in source.Chunks)
            {
                pcChunk.Value = chunk.ChunkId;
                pcSource.Value = chunk.SourceId;
                pcKind.Value = chunk.ContentKind;
                pcPath.Value = (object?)chunk.Path ?? DBNull.Value;
                pcDisplay.Value = chunk.DisplayPath;
                pcLanguage.Value = chunk.Language;
                pcLineStart.Value = chunk.LineStart;
                pcLineEnd.Value = chunk.LineEnd;
                pcByteStart.Value = chunk.ByteStart;
                pcByteEnd.Value = chunk.ByteEnd;
                pcRaw.Value = chunk.Text;
                pcDocLen.Value = chunk.DocLen;
                pcTest.Value = chunk.IsTest ? 1 : 0;
                pcSourceBytes.Value = chunk.SourceBytes;
                pcSymbolId.Value = (object?)chunk.ContainingSymbolId ?? DBNull.Value;
                pcSymbolName.Value = (object?)chunk.ContainingSymbolName ?? DBNull.Value;
                chunkCmd.ExecuteNonQuery();

                tokens.Clear();
                CodeTokenizer.Tokenize(chunk.Text, tokens);
                pfChunk.Value = chunk.ChunkId;
                pfBody.Value = string.Join(' ', tokens);
                ftsCmd.ExecuteNonQuery();
            }

            foreach (ContentCorpusSymbolSpan span in source.Symbols)
            {
                psSpanSource.Value = source.SourceId;
                psSpanSymbolId.Value = span.SymbolId;
                psSpanSymbolName.Value = span.Name;
                psSpanPath.Value = span.Path;
                psSpanStart.Value = span.StartLine;
                psSpanEnd.Value = span.EndLine;
                spanCmd.ExecuteNonQuery();
            }
        }
    }

    private static List<SourceBuildInput> CollectAccepted(
        IReadOnlyList<SourceRow> sourceRows,
        IReadOnlyDictionary<string, IReadOnlyList<ContentCorpusSymbolSpan>> symbolsByPath,
        string workspaceRoot,
        string? workspaceId,
        out int skippedStatus,
        out int skippedScope,
        out int skippedLarge,
        out int skippedMissing,
        out int skippedHash,
        out int skippedUtf8,
        out int skippedIo)
    {
        var accepted = new List<SourceBuildInput>();
        skippedStatus = 0;
        skippedScope = 0;
        skippedLarge = 0;
        skippedMissing = 0;
        skippedHash = 0;
        skippedUtf8 = 0;
        skippedIo = 0;

        foreach (SourceRow row in sourceRows)
        {
            if (!string.Equals(row.Status, "indexed", StringComparison.Ordinal))
            {
                skippedStatus++;
                continue;
            }

            string contentKind = ContentFileClassifier.WorkspaceContentKind(row.Path, row.Language);

            if (row.ContentBytes > MaxWorkspaceFileBytes)
            {
                skippedLarge++;
                continue;
            }

            string? abs = WorkspaceRelativePath.ResolveUnderRoot(workspaceRoot, row.Path);
            if (abs is null || !File.Exists(abs))
            {
                skippedMissing++;
                continue;
            }

            byte[] bytes;
            try
            {
                if (new FileInfo(abs).Length > MaxWorkspaceFileBytes)
                {
                    skippedLarge++;
                    continue;
                }

                bytes = File.ReadAllBytes(abs);
            }
            catch (IOException)
            {
                skippedIo++;
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                skippedIo++;
                continue;
            }

            if (bytes.LongLength > MaxWorkspaceFileBytes)
            {
                skippedLarge++;
                continue;
            }

            if (!StringComparer.OrdinalIgnoreCase.Equals(
                    ContentHasher.Blake3Hex(bytes),
                    ContentHasher.NormalizeHash(row.ContentHash)))
            {
                skippedHash++;
                continue;
            }

            if (!SourceTextDecoder.TryDecode(bytes, out string text))
            {
                skippedUtf8++;
                continue;
            }

            string sourceId = SourceId(workspaceId, row.Path, contentKind);
            IReadOnlyList<ContentCorpusSymbolSpan> spans = string.Equals(contentKind, TextContentKind.WorkspaceSource, StringComparison.Ordinal)
                && symbolsByPath.TryGetValue(row.Path, out var pathSpans)
                ? pathSpans
                : Array.Empty<ContentCorpusSymbolSpan>();
            IReadOnlyList<TextContentDocument> chunks = ContentCorpusChunker.Chunk(
                sourceId,
                contentKind,
                row.Path,
                url: null,
                row.Path,
                row.Language,
                text,
                bytes.LongLength,
                IsTestPath(row.Path, spans),
                containingSymbols: spans);

            accepted.Add(new SourceBuildInput(row, sourceId, contentKind, text, bytes.LongLength, spans, chunks));
        }

        return accepted;
    }

    private static void DeleteWorkspaceSourcesForPaths(SqliteConnection connection, IReadOnlyList<string> paths)
    {
        const int chunkSize = 500;
        for (int offset = 0; offset < paths.Count; offset += chunkSize)
        {
            int count = Math.Min(chunkSize, paths.Count - offset);
            using var fts = connection.CreateCommand();
            string placeholders = AddPathParameters(fts, paths, offset, count);
            fts.CommandText = $"""
                DELETE FROM content_fts
                WHERE chunk_id IN (
                    SELECT chunk_id
                    FROM content_chunks
                    WHERE path IN ({placeholders})
                      AND content_kind IN ($source, $docs, $config)
                );
                """;
            AddWorkspaceKindParameters(fts);
            fts.ExecuteNonQuery();

            using var spans = connection.CreateCommand();
            placeholders = AddPathParameters(spans, paths, offset, count);
            spans.CommandText = $"""
                DELETE FROM content_symbol_spans
                WHERE path IN ({placeholders});
                """;
            spans.ExecuteNonQuery();

            using var chunks = connection.CreateCommand();
            placeholders = AddPathParameters(chunks, paths, offset, count);
            chunks.CommandText = $"""
                DELETE FROM content_chunks
                WHERE path IN ({placeholders})
                  AND content_kind IN ($source, $docs, $config);
                """;
            AddWorkspaceKindParameters(chunks);
            chunks.ExecuteNonQuery();

            using var sources = connection.CreateCommand();
            placeholders = AddPathParameters(sources, paths, offset, count);
            sources.CommandText = $"""
                DELETE FROM content_sources
                WHERE path IN ({placeholders})
                  AND content_kind IN ($source, $docs, $config);
                """;
            AddWorkspaceKindParameters(sources);
            sources.ExecuteNonQuery();
        }
    }

    private static string AddPathParameters(
        SqliteCommand command,
        IReadOnlyList<string> paths,
        int offset,
        int count)
    {
        var names = new string[count];
        for (int i = 0; i < count; i++)
        {
            string name = "$p" + i;
            names[i] = name;
            command.Parameters.AddWithValue(name, paths[offset + i]);
        }

        return string.Join(",", names);
    }

    private static void AddWorkspaceKindParameters(SqliteCommand command)
    {
        command.Parameters.AddWithValue("$source", TextContentKind.WorkspaceSource);
        command.Parameters.AddWithValue("$docs", TextContentKind.WorkspaceDocs);
        command.Parameters.AddWithValue("$config", TextContentKind.WorkspaceConfig);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<ContentCorpusSymbolSpan>> FilterSymbolSpans(
        IReadOnlyDictionary<string, IReadOnlyList<ContentCorpusSymbolSpan>> symbolsByPath,
        HashSet<string> paths)
    {
        var filtered = new Dictionary<string, IReadOnlyList<ContentCorpusSymbolSpan>>(StringComparer.Ordinal);
        foreach (string path in paths)
        {
            if (symbolsByPath.TryGetValue(path, out IReadOnlyList<ContentCorpusSymbolSpan>? spans))
                filtered[path] = spans;
        }

        return filtered;
    }

    private static IReadOnlyList<SourceRow> ReadSourceRows(string symbolsDbPath)
    {
        using var connection = SqliteReadOnlyAccess.Open(symbolsDbPath);
        JulieSchemaGate.Verify(connection);
        return ReadSourceRows(connection);
    }

    private static IReadOnlyList<SourceRow> ReadSourceRows(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path, language, content_hash, content_bytes, status FROM files ORDER BY path;";
        using var reader = command.ExecuteReader();
        var rows = new List<SourceRow>();
        while (reader.Read())
        {
            rows.Add(new SourceRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? "" : reader.GetString(2),
                reader.GetInt64(3),
                reader.GetString(4)));
        }

        return rows;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<ContentCorpusSymbolSpan>> ReadSymbolSpans(string symbolsDbPath)
    {
        using var connection = SqliteReadOnlyAccess.Open(symbolsDbPath);
        return ReadSymbolSpans(connection);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<ContentCorpusSymbolSpan>> ReadSymbolSpans(
        SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT symbol_id, name, path, start_line, end_line
            FROM symbols
            WHERE start_line IS NOT NULL
            ORDER BY path, start_line, end_line, symbol_id;
            """;
        using var reader = command.ExecuteReader();
        var byPath = new Dictionary<string, List<ContentCorpusSymbolSpan>>(StringComparer.Ordinal);
        while (reader.Read())
        {
            string path = reader.GetString(2);
            int startLine = reader.GetInt32(3);
            int endLine = reader.IsDBNull(4) ? startLine : reader.GetInt32(4);
            if (!byPath.TryGetValue(path, out var list))
                byPath[path] = list = new List<ContentCorpusSymbolSpan>();
            list.Add(new ContentCorpusSymbolSpan(
                reader.GetString(0),
                reader.GetString(1),
                path,
                startLine,
                endLine));
        }

        return byPath.ToDictionary(
            static kv => kv.Key,
            static kv => (IReadOnlyList<ContentCorpusSymbolSpan>)kv.Value,
            StringComparer.Ordinal);
    }

    private static bool IsTestPath(string path, IReadOnlyList<ContentCorpusSymbolSpan> symbols)
    {
        if (path.Contains("/test", StringComparison.OrdinalIgnoreCase)
            || path.Contains("\\test", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(path).Contains("test", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(path).Contains("spec", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static string SourceId(string? workspaceId, string path, string kind) =>
        (workspaceId ?? "workspace") + ":" + kind + ":" + path;

    /// <summary>
    /// The <c>artifact_metadata.artifact_id</c> of the extract this corpus is built from, or <c>null</c> when
    /// unreadable. Stamped into <c>content_meta</c> so a full-rebuild promote — which restarts the revision
    /// counter — cannot be mistaken for the generation this corpus was built from.
    /// </summary>
    private static string? TryReadArtifactId(string symbolsDbPath)
    {
        try
        {
            return SymbolsArtifactIdentity.Read(symbolsDbPath).ArtifactId;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    private sealed record SourceRow(
        string Path,
        string Language,
        string ContentHash,
        long ContentBytes,
        string Status);

    private sealed record SourceBuildInput(
        SourceRow Row,
        string SourceId,
        string ContentKind,
        string Text,
        long SourceBytes,
        IReadOnlyList<ContentCorpusSymbolSpan> Symbols,
        IReadOnlyList<TextContentDocument> Chunks);
}
