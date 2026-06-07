using Microsoft.Data.Sqlite;

namespace Miller.Indexing;

/// <summary>
/// Lifecycle gate for the on-disk <c>content.db</c> sidecar. Writers converge it from <c>symbols.db</c>; readers
/// open only revision-fresh artifacts so source-body search cannot silently answer from stale content.
/// </summary>
public sealed class ContentCorpusSidecar
{
    /// <summary>The on-disk <c>content.db</c> path for a Miller <c>symbols.db</c> sibling.</summary>
    public static string ContentDbPathFor(string symbolsDbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolsDbPath);
        string dir = Path.GetDirectoryName(Path.GetFullPath(symbolsDbPath))
            ?? throw new ArgumentException($"Path has no directory: {symbolsDbPath}", nameof(symbolsDbPath));
        return Path.Combine(dir, "content.db");
    }

    /// <summary>
    /// Ensure a revision-fresh content corpus exists. Returns <c>true</c> when the sidecar was rebuilt and
    /// <c>false</c> when the existing artifact was already current.
    /// </summary>
    public bool EnsureBuilt(string symbolsDbPath, string workspaceRoot, string? workspaceId, long revision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolsDbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        string contentDbPath = ContentDbPathFor(symbolsDbPath);
        if (IsFresh(contentDbPath, revision))
            return false;

        ContentCorpusWriter.Write(contentDbPath, symbolsDbPath, workspaceRoot, workspaceId, revision);
        return true;
    }

    /// <summary>Cheap status facts for human/JSON workspace status surfaces.</summary>
    public ContentCorpusFacts Inspect(string symbolsDbPath, long expectedRevision)
    {
        string contentDbPath;
        try
        {
            contentDbPath = ContentDbPathFor(symbolsDbPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return new ContentCorpusFacts(
                "unreadable",
                Path: null,
                SchemaVersion: null,
                WorkspaceRevision: null,
                SourceCount: 0,
                ChunkCount: 0,
                IndexedSourceBytes: 0,
                StoredRawBytes: 0,
                Error: "content.db path could not be derived: " + ex.Message);
        }

        if (!File.Exists(contentDbPath))
        {
            return new ContentCorpusFacts(
                "missing",
                contentDbPath,
                SchemaVersion: null,
                WorkspaceRevision: null,
                SourceCount: 0,
                ChunkCount: 0,
                IndexedSourceBytes: 0,
                StoredRawBytes: 0);
        }

        try
        {
            return ReadFacts(contentDbPath, expectedRevision);
        }
        catch (Exception ex) when (
            ex is SqliteException or InvalidOperationException or IOException
                or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new ContentCorpusFacts(
                "unreadable",
                contentDbPath,
                SchemaVersion: null,
                WorkspaceRevision: null,
                SourceCount: 0,
                ChunkCount: 0,
                IndexedSourceBytes: 0,
                StoredRawBytes: 0,
                Error: ex.Message);
        }
    }

    /// <summary>Open a revision-fresh content corpus or throw a user-actionable error.</summary>
    public FtsTextContentSearchIndex OpenRequired(string symbolsDbPath, long expectedRevision)
    {
        string contentDbPath = ContentDbPathFor(symbolsDbPath);
        if (!File.Exists(contentDbPath))
        {
            throw new InvalidOperationException(
                $"Content corpus sidecar is missing at '{contentDbPath}'. Run `miller workspace refresh` to rebuild it.");
        }

        try
        {
            return FtsTextContentSearchIndex.Open(contentDbPath, expectedRevision);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is FileNotFoundException or SqliteException or IOException
                or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new InvalidOperationException(
                $"Content corpus sidecar at '{contentDbPath}' could not be opened. " +
                "Run `miller workspace refresh` to rebuild it.",
                ex);
        }
    }

    private static bool IsFresh(string contentDbPath, long revision)
    {
        if (!File.Exists(contentDbPath))
            return false;

        try
        {
            FtsTextContentSearchIndex.Open(contentDbPath, revision);
            return true;
        }
        catch (Exception ex) when (
            ex is FileNotFoundException or SqliteException or InvalidOperationException or IOException
                or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static ContentCorpusFacts ReadFacts(string contentDbPath, long expectedRevision)
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
            LIMIT 2;
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw new InvalidOperationException("content_meta has no row");

        int schemaVersion = checked((int)reader.GetInt64(0));
        long workspaceRevision = reader.GetInt64(1);
        int sourceCount = checked((int)reader.GetInt64(2));
        int chunkCount = checked((int)reader.GetInt64(3));
        long indexedSourceBytes = reader.GetInt64(4);
        long storedRawBytes = reader.GetInt64(5);
        int statusSkipped = checked((int)reader.GetInt64(6));
        int scopeSkipped = checked((int)reader.GetInt64(7));
        int tooLargeSkipped = checked((int)reader.GetInt64(8));
        int missingSkipped = checked((int)reader.GetInt64(9));
        int hashMismatchSkipped = checked((int)reader.GetInt64(10));
        int nonUtf8Skipped = checked((int)reader.GetInt64(11));
        int ioSkipped = checked((int)reader.GetInt64(12));
        if (reader.Read())
            throw new InvalidOperationException("content_meta has multiple rows");

        string state = schemaVersion == ContentCorpusSchema.SchemaVersion && workspaceRevision == expectedRevision
            ? "current"
            : "stale";
        return new ContentCorpusFacts(
            state,
            Path.GetFullPath(contentDbPath),
            schemaVersion,
            workspaceRevision,
            sourceCount,
            chunkCount,
            indexedSourceBytes,
            storedRawBytes,
            statusSkipped,
            scopeSkipped,
            tooLargeSkipped,
            missingSkipped,
            hashMismatchSkipped,
            nonUtf8Skipped,
            ioSkipped);
    }
}
