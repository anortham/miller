using Microsoft.Data.Sqlite;
using Miller.Indexing.Reads;
using Miller.Indexing.Store;

namespace Miller.Indexing;

/// <summary>
/// Lifecycle gate for the on-disk <c>content.db</c> sidecar. Writers converge it from <c>symbols.db</c>; readers
/// open only revision-fresh artifacts so source-body search cannot silently answer from stale content.
/// </summary>
public sealed class ContentCorpusSidecar
{
    // Microsoft.Data.Sqlite reapplies sqlite3_busy_timeout(DefaultTimeout) on every command.
    // DefaultTimeout is whole seconds and 0 means infinite, so 1 s is the short-retry floor.
    private const int InspectBusyTimeoutSeconds = 1;
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;
    private const string DatabaseLockedError = "database_locked";

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
        if (IsFresh(contentDbPath, revision) &&
            BuiltFromCurrentArtifact(contentDbPath, symbolsDbPath) &&
            WorkspaceSourcesAgree(contentDbPath, symbolsDbPath))
        {
            return false;
        }

        ContentCorpusWriter.Write(contentDbPath, symbolsDbPath, workspaceRoot, workspaceId, revision);
        return true;
    }

    public bool EnsureStoreCurrent(string storeRoot, IWorkspaceReadSession session) =>
        EnsureStoreCurrentDetailed(storeRoot, session).DidWork;

    internal SidecarConvergenceDetail EnsureStoreCurrentDetailed(
        string storeRoot,
        IWorkspaceReadSession session) =>
        EnsureStoreCurrentCore(storeRoot, session, cursor: null);

    internal SidecarConvergenceDetail EnsureStoreCurrentWithCursor(
        string storeRoot,
        IWorkspaceReadSession session,
        IStoreSidecarCursorSession cursor) =>
        EnsureStoreCurrentCore(storeRoot, session, cursor);

    private static SidecarConvergenceDetail EnsureStoreCurrentCore(
        string storeRoot,
        IWorkspaceReadSession session,
        IStoreSidecarCursorSession? cursor)
    {
        ArgumentNullException.ThrowIfNull(session);
        StoreSidecarStamp expected = StoreSidecarStamp.FromSnapshot(StoreSidecarKind.Content, session.Snapshot);
        string contentDbPath = StoreSidecarCatalog.PathFor(storeRoot, StoreSidecarKind.Content, session.Snapshot.ViewId);
        if (StoreSidecarCatalog.IsCurrent(contentDbPath, expected))
            return new(SidecarConvergencePath.Current, SidecarConvergenceReason.None, false);

        if (cursor is not null)
        {
            StoreSidecarStamp? previous = StoreSidecarCatalog.TryRead(contentDbPath);
            if (!CanReadDelta(previous, expected) || !cursor.TryProtectBaseline(previous!))
            {
                cursor.PrepareTarget(expected.StoreLogSequence);
                SidecarConvergenceReason fallbackReason = ClassifyBaseline(previous, expected);
                ContentCorpusWriter.WriteStoreView(contentDbPath, session);
                return new(SidecarConvergencePath.Full, fallbackReason, true);
            }
            cursor.PrepareTarget(expected.StoreLogSequence);
        }

        if (StoreSidecarCatalog.TryFastForwardEmptyDelta(
                contentDbPath,
                expected,
                session,
                ContentCorpusWriter.TryFastForwardStoreMetadata))
        {
            return new(SidecarConvergencePath.EmptyDelta, SidecarConvergenceReason.None, true);
        }

        SidecarConvergenceReason reason = TryApplyStoreDelta(contentDbPath, expected, session, out bool applied);
        if (applied)
            return new(SidecarConvergencePath.Incremental, SidecarConvergenceReason.None, true);
        ContentCorpusWriter.WriteStoreView(contentDbPath, session);
        return new(SidecarConvergencePath.Full, reason, true);
    }

    private static SidecarConvergenceReason ClassifyBaseline(StoreSidecarStamp? previous, StoreSidecarStamp expected) =>
        previous is null
            ? SidecarConvergenceReason.DeltaMissing
            : !SameDeltaIdentity(previous, expected)
                ? SidecarConvergenceReason.IdentityChanged
                : previous.StoreLogSequence >= expected.StoreLogSequence
                    ? SidecarConvergenceReason.StampMismatch
                    : SidecarConvergenceReason.DeltaIncomplete;

    private static bool CanReadDelta(StoreSidecarStamp? previous, StoreSidecarStamp expected) =>
        previous is not null && SameDeltaIdentity(previous, expected) &&
        previous.StoreLogSequence < expected.StoreLogSequence;

    private static bool SameDeltaIdentity(StoreSidecarStamp previous, StoreSidecarStamp expected) =>
        previous.Kind == expected.Kind &&
        string.Equals(previous.FamilyId, expected.FamilyId, StringComparison.Ordinal) &&
        string.Equals(previous.ViewId, expected.ViewId, StringComparison.Ordinal) &&
        string.Equals(previous.StoreInstanceId, expected.StoreInstanceId, StringComparison.Ordinal) &&
        string.Equals(previous.GenerationName, expected.GenerationName, StringComparison.Ordinal) &&
        string.Equals(previous.IndexLevel, expected.IndexLevel, StringComparison.Ordinal);

    private static SidecarConvergenceReason TryApplyStoreDelta(
        string contentDbPath,
        StoreSidecarStamp expected,
        IWorkspaceReadSession session,
        out bool applied)
    {
        applied = false;
        StoreSidecarStamp? previous = StoreSidecarCatalog.TryRead(contentDbPath);
        if (previous is null)
            return SidecarConvergenceReason.DeltaMissing;
        if (previous.Kind != expected.Kind ||
            !string.Equals(previous.FamilyId, expected.FamilyId, StringComparison.Ordinal) ||
            !string.Equals(previous.ViewId, expected.ViewId, StringComparison.Ordinal) ||
            !string.Equals(previous.StoreInstanceId, expected.StoreInstanceId, StringComparison.Ordinal) ||
            !string.Equals(previous.GenerationName, expected.GenerationName, StringComparison.Ordinal) ||
            !string.Equals(previous.IndexLevel, expected.IndexLevel, StringComparison.Ordinal))
        {
            return SidecarConvergenceReason.IdentityChanged;
        }
        if (previous.StoreLogSequence >= expected.StoreLogSequence)
            return SidecarConvergenceReason.StampMismatch;

        RevisionDeltaResult delta = RevisionDeltaReader.Read(
            session,
            previous.StoreLogSequence,
            previous.FamilyId);
        if (delta.Status != RevisionDeltaStatus.Complete ||
            delta.ToRevision != expected.StoreLogSequence)
        {
            return SidecarConvergenceReason.DeltaIncomplete;
        }

        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in delta.ChangedPaths)
            paths.Add(path);
        if (delta.DeletedPaths is not null)
        {
            foreach (string path in delta.DeletedPaths)
                paths.Add(path);
        }

        try
        {
            ContentCorpusWriter.ApplyStoreFileChanges(contentDbPath, session, paths, expected);
            if (!StoreSidecarCatalog.IsCurrent(contentDbPath, expected))
                return SidecarConvergenceReason.StampMismatch;
            applied = true;
            return SidecarConvergenceReason.None;
        }
        catch (Exception ex) when (
            ex is SqliteException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return SidecarConvergenceReason.ApplyFailed;
        }
    }

    /// <summary>
    /// Revision equality alone cannot prove freshness: the extractor updates <c>files.content_hash</c> (and
    /// drops rows) for symbol-free files WITHOUT advancing the revision, so a corpus that matches the revision
    /// can still disagree with <c>symbols.db</c> forever — permanently wedging every consumer that gates on
    /// per-source hash agreement (the vectors chunk cursor). Active workspace sources must exist in
    /// <c>symbols.db</c> with an agreeing hash; external/web imports have no <c>symbols.db</c> counterpart and
    /// are exempt. Any read failure counts as disagreement so the rebuild path surfaces the real error.
    /// </summary>
    private static bool WorkspaceSourcesAgree(string contentDbPath, string symbolsDbPath)
    {
        try
        {
            var symbolsHashes = new Dictionary<string, string>(StringComparer.Ordinal);
            using (var symbols = SqliteReadOnlyAccess.Open(symbolsDbPath))
            using (var files = symbols.CreateCommand())
            {
                files.CommandText = "SELECT path, content_hash FROM files;";
                using var reader = files.ExecuteReader();
                while (reader.Read())
                    symbolsHashes[reader.GetString(0)] = reader.IsDBNull(1) ? "" : reader.GetString(1);
            }

            using var content = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(contentDbPath),
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            content.Open();
            using var sources = content.CreateCommand();
            sources.CommandText = $"""
                SELECT path, content_hash FROM content_sources
                WHERE status = 'active'
                  AND content_kind IN ('{TextContentKind.WorkspaceSource}', '{TextContentKind.WorkspaceDocs}', '{TextContentKind.WorkspaceConfig}')
                  AND path IS NOT NULL AND path != '';
                """;
            using var sourceReader = sources.ExecuteReader();
            while (sourceReader.Read())
            {
                if (!symbolsHashes.TryGetValue(sourceReader.GetString(0), out string? symbolsHash)
                    || !HashesAgree(sourceReader.GetString(1), symbolsHash))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex) when (
            ex is FileNotFoundException or SqliteException or InvalidOperationException or IOException
                or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool HashesAgree(string contentHash, string symbolsHash) =>
        string.Equals(
            contentHash.Trim().ToLowerInvariant(),
            symbolsHash.Trim().ToLowerInvariant(),
            StringComparison.Ordinal);

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

        if (ContentCorpusWriter.TryReadPreservationFailure(contentDbPath) is { } preservationFailure)
        {
            try
            {
                return ReadFactsWithBusyRetry(
                    contentDbPath,
                    expectedRevision,
                    () => ReadGateArtifactAgrees(contentDbPath, symbolsDbPath));
            }
            catch (Exception ex) when (
                ex is SqliteException or InvalidOperationException or IOException
                    or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
            }

            return new ContentCorpusFacts(
                "preservation_blocked",
                contentDbPath,
                SchemaVersion: null,
                WorkspaceRevision: null,
                SourceCount: 0,
                ChunkCount: 0,
                IndexedSourceBytes: 0,
                StoredRawBytes: 0,
                Error: preservationFailure);
        }

        try
        {
            return ReadFactsWithBusyRetry(
                contentDbPath,
                expectedRevision,
                () => ReadGateArtifactAgrees(contentDbPath, symbolsDbPath));
        }
        catch (Exception ex) when (
            ex is SqliteException or InvalidOperationException or IOException
                or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return InspectFailureFacts(contentDbPath, expectedRevision: null, ex);
        }
    }

    public ContentCorpusFacts InspectStore(string storeRoot, WorkspaceReadSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        StoreSidecarStamp expected = StoreSidecarStamp.FromSnapshot(StoreSidecarKind.Content, snapshot);
        string path = StoreSidecarCatalog.PathFor(storeRoot, StoreSidecarKind.Content, snapshot.ViewId);
        if (!File.Exists(path))
        {
            return new ContentCorpusFacts(
                "missing", path, null, expected.StoreLogSequence, 0, 0, 0, 0);
        }

        try
        {
            return ReadFactsWithBusyRetry(
                path,
                expected.StoreLogSequence,
                () => StoreSidecarCatalog.IsCurrent(path, expected));
        }
        catch (Exception ex) when (
            ex is SqliteException or InvalidOperationException or IOException
                or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return InspectFailureFacts(path, expected.StoreLogSequence, ex);
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
            return OpenGenerationChecked(contentDbPath, symbolsDbPath, expectedRevision);
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

    /// <summary>
    /// Open a content corpus that is both revision-fresh and built from the extract generation currently on
    /// disk. Every reader that serves workspace-derived text as authoritative must come through here.
    /// </summary>
    /// <remarks>
    /// Revision alone cannot prove the generation: a full-rebuild promote restarts julie's counter, so a corpus
    /// built at revision N from the superseded artifact matches a post-promote revision N exactly. The revision
    /// check runs first so an ordinary staleness gets its own more specific message.
    /// </remarks>
    public static FtsTextContentSearchIndex OpenGenerationChecked(
        string contentDbPath, string symbolsDbPath, long expectedRevision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentDbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolsDbPath);

        FtsTextContentSearchIndex index = FtsTextContentSearchIndex.Open(contentDbPath, expectedRevision);
        if (!ReadGateArtifactAgrees(contentDbPath, symbolsDbPath))
        {
            throw new InvalidOperationException(
                $"Content corpus sidecar at '{contentDbPath}' is stale: it was built from a different index " +
                "generation (the workspace was fully rebuilt). Run `miller workspace refresh` to converge it.");
        }

        return index;
    }

    public static FtsTextContentSearchIndex OpenStoreGenerationChecked(
        string storeRoot,
        WorkspaceReadSnapshot snapshot)
    {
        StoreSidecarStamp expected = StoreSidecarStamp.FromSnapshot(StoreSidecarKind.Content, snapshot);
        string contentDbPath = StoreSidecarCatalog.PathFor(storeRoot, StoreSidecarKind.Content, snapshot.ViewId);
        StoreSidecarStamp serve = StoreSidecarCatalog.TryResolveReadable(contentDbPath, expected, snapshot)
            ?? throw new InvalidOperationException(
                $"Content sidecar for view '{snapshot.ViewId}' is missing or stale. " +
                "Run `miller workspace refresh` to converge it.");
        return FtsTextContentSearchIndex.Open(contentDbPath, serve.StoreLogSequence);
    }

    /// <summary>
    /// Whether <paramref name="contentDbPath"/> is safe to read alongside <paramref name="symbolsDbPath"/>:
    /// revision-fresh AND built from the extract generation currently on disk. Readers that answer from the
    /// corpus without this check can serve pre-promote text as if it were current.
    /// </summary>
    public static bool IsCurrentFor(string contentDbPath, string symbolsDbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentDbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolsDbPath);
        if (!File.Exists(contentDbPath) || !File.Exists(symbolsDbPath))
            return false;

        long revision;
        try
        {
            revision = SymbolsArtifactIdentity.Read(symbolsDbPath).Revision;
        }
        catch (Exception ex) when (
            ex is FileNotFoundException or SqliteException or InvalidOperationException or IOException
                or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }

        return IsFresh(contentDbPath, revision) && BuiltFromCurrentArtifact(contentDbPath, symbolsDbPath);
    }

    /// <summary>
    /// Whether the corpus is PROVABLY from a different generation than <paramref name="symbolsDbPath"/>. Use
    /// from a reader that has its own error path for a missing or damaged corpus.
    /// </summary>
    /// <remarks>
    /// A corpus that cannot be read at all reports agreement here, deliberately. This gate exists to catch a
    /// SILENT wrong-generation answer; an unreadable corpus is not silent, and pre-empting it would replace an
    /// accurate "corrupt content.db" diagnostic with a misleading "run refresh, the workspace was rebuilt".
    /// </remarks>
    public static bool GenerationAgrees(string contentDbPath, string symbolsDbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentDbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolsDbPath);
        if (!File.Exists(contentDbPath))
            return true;

        return !TryReadCorpusArtifactId(contentDbPath, out string? stamped)
               || SymbolsArtifactIdentity.TryRead(symbolsDbPath).MatchesArtifact(stamped);
    }

    /// <summary>
    /// The read-gate half of <see cref="IsCurrentFor"/>, applied AFTER a successful open — so unlike
    /// <see cref="GenerationAgrees"/>, a failed read here is genuinely anomalous and counts as disagreement.
    /// </summary>
    private static bool ReadGateArtifactAgrees(string contentDbPath, string symbolsDbPath) =>
        TryReadCorpusArtifactId(contentDbPath, out string? stamped)
        && SymbolsArtifactIdentity.TryRead(symbolsDbPath).MatchesArtifact(stamped);

    private static bool BuiltFromCurrentArtifact(string contentDbPath, string symbolsDbPath)
    {
        try
        {
            return TryReadCorpusArtifactId(contentDbPath, out string? stamped)
                   && SymbolsArtifactIdentity.Read(symbolsDbPath).MatchesArtifact(stamped);
        }
        catch (Exception ex) when (
            ex is FileNotFoundException or SqliteException or InvalidOperationException or IOException
                or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryReadCorpusArtifactId(string contentDbPath, out string? artifactId)
    {
        artifactId = null;
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(contentDbPath),
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT artifact_id FROM content_meta LIMIT 1;";
            artifactId = cmd.ExecuteScalar() as string;
            return true;
        }
        catch (Exception ex) when (
            ex is SqliteException or InvalidOperationException or IOException
                or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
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

    private static ContentCorpusFacts ReadFactsWithBusyRetry(
        string contentDbPath,
        long expectedRevision,
        Func<bool> currentGate)
    {
        try
        {
            return ReadFacts(contentDbPath, expectedRevision, currentGate);
        }
        catch (Exception ex) when (IsBusy(ex))
        {
            return ReadFacts(contentDbPath, expectedRevision, currentGate);
        }
    }

    // Locked inspect is converging, not a dead corpus. Do not leak the raw SQLite Error 5 sentence.
    private static ContentCorpusFacts InspectFailureFacts(
        string contentDbPath,
        long? expectedRevision,
        Exception ex) =>
        IsBusy(ex)
            ? new ContentCorpusFacts(
                "converging",
                contentDbPath,
                SchemaVersion: null,
                WorkspaceRevision: expectedRevision,
                SourceCount: 0,
                ChunkCount: 0,
                IndexedSourceBytes: 0,
                StoredRawBytes: 0,
                Error: DatabaseLockedError)
            : new ContentCorpusFacts(
                "unreadable",
                contentDbPath,
                SchemaVersion: null,
                WorkspaceRevision: expectedRevision,
                SourceCount: 0,
                ChunkCount: 0,
                IndexedSourceBytes: 0,
                StoredRawBytes: 0,
                Error: ex.Message);

    private static bool IsBusy(Exception ex) =>
        ex is SqliteException sqlite && sqlite.SqliteErrorCode is SqliteBusy or SqliteLocked;

    private static ContentCorpusFacts ReadFacts(
        string contentDbPath,
        long expectedRevision,
        Func<bool> currentGate)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(contentDbPath),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.DefaultTimeout = InspectBusyTimeoutSeconds;
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
        long? workspaceRevision = reader.IsDBNull(1) ? null : reader.GetInt64(1);
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

        string? preservationFailure = ContentCorpusWriter.TryReadPreservationFailure(contentDbPath);
        string state = preservationFailure is not null
            ? "preservation_blocked"
            : workspaceRevision is null
            ? "imports_only"
            : schemaVersion == ContentCorpusSchema.SchemaVersion
              && workspaceRevision == expectedRevision
              && currentGate()
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
            ioSkipped,
            Error: preservationFailure);
    }
}
