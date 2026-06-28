using Microsoft.Data.Sqlite;

namespace Miller.Indexing;

/// <summary>
/// Read-only edit candidate discovery over Miller's content corpus. Candidates are advisory chunks; callers must
/// verify against current disk text before planning or applying an edit.
/// </summary>
public sealed class IndexedEditCandidateReader
{
    public const int MaxCandidates = 8;

    public IndexedEditCandidateResult FindCandidates(
        string symbolsDbPath,
        string relativePath,
        long expectedRevision,
        string? oldText,
        string? query,
        string? anchor,
        int? line,
        int limit = MaxCandidates)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolsDbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        string contentDbPath = ContentCorpusSidecar.ContentDbPathFor(symbolsDbPath);
        if (!File.Exists(contentDbPath))
            return IndexedEditCandidateResult.Unavailable("missing content.db at " + contentDbPath);

        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(contentDbPath),
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            connection.Open();

            if (!TryReadCurrentMeta(connection, expectedRevision, out long workspaceRevision, out string? unavailableReason))
                return IndexedEditCandidateResult.Unavailable(unavailableReason);

            if (!HasActiveWorkspaceContentSource(connection, relativePath))
            {
                return IndexedEditCandidateResult.Unavailable(
                    "no active content source exists for " + relativePath + "; the file may have been skipped by the content corpus");
            }

            IReadOnlyList<string> selectors = Selectors(oldText, query, anchor);
            if (selectors.Count == 0 && line is null)
                return IndexedEditCandidateResult.NoMatch("no selector or line hint was provided for indexed edit candidate discovery");

            var candidates = QueryCandidates(connection, relativePath, selectors, line, Math.Clamp(limit, 1, MaxCandidates));
            return candidates.Count == 0
                ? IndexedEditCandidateResult.NoMatch("no indexed content candidates matched the selector")
                : IndexedEditCandidateResult.Current(candidates, workspaceRevision);
        }
        catch (SqliteException ex)
        {
            return IndexedEditCandidateResult.Unavailable("corrupt content.db: " + ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return IndexedEditCandidateResult.Unavailable("corrupt content.db: " + ex.Message);
        }
        catch (IOException ex)
        {
            return IndexedEditCandidateResult.Unavailable("unavailable content.db: " + ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return IndexedEditCandidateResult.Unavailable("unavailable content.db: " + ex.Message);
        }
    }

    private static bool TryReadCurrentMeta(
        SqliteConnection connection,
        long expectedRevision,
        out long workspaceRevision,
        out string unavailableReason)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT schema_version, workspace_revision FROM content_meta LIMIT 2;";
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            workspaceRevision = 0;
            unavailableReason = "corrupt content.db: content_meta has no row";
            return false;
        }

        int schemaVersion = checked((int)reader.GetInt64(0));
        workspaceRevision = reader.GetInt64(1);
        if (reader.Read())
        {
            unavailableReason = "corrupt content.db: content_meta has multiple rows";
            return false;
        }

        if (schemaVersion != ContentCorpusSchema.SchemaVersion)
        {
            unavailableReason = $"stale content.db schema {schemaVersion}; expected {ContentCorpusSchema.SchemaVersion}";
            return false;
        }

        if (workspaceRevision != expectedRevision)
        {
            unavailableReason = $"stale content.db revision {workspaceRevision}; expected {expectedRevision}";
            return false;
        }

        unavailableReason = string.Empty;
        return true;
    }

    private static bool HasActiveWorkspaceContentSource(SqliteConnection connection, string relativePath)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT 1
            FROM content_sources
            WHERE path = $path
              AND status = 'active'
              AND content_kind IN ('{TextContentKind.WorkspaceSource}', '{TextContentKind.WorkspaceDocs}', '{TextContentKind.WorkspaceConfig}')
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$path", relativePath);
        return command.ExecuteScalar() is not null;
    }

    private static IReadOnlyList<IndexedEditCandidate> QueryCandidates(
        SqliteConnection connection,
        string relativePath,
        IReadOnlyList<string> selectors,
        int? line,
        int limit)
    {
        using var command = connection.CreateCommand();
        var selectorSql = new List<string>(selectors.Count);
        for (int i = 0; i < selectors.Count; i++)
        {
            string name = "$selector" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            selectorSql.Add($"instr(c.raw_text, {name}) > 0");
            command.Parameters.AddWithValue(name, selectors[i]);
        }

        string selectorWhere = selectorSql.Count == 0
            ? string.Empty
            : " AND " + string.Join(" AND ", selectorSql);
        string lineWhere = line is null
            ? string.Empty
            : " AND c.line_start <= $line AND c.line_end >= $line";

        command.CommandText = $"""
            SELECT s.source_id, s.content_hash, s.workspace_revision,
                   c.path, c.line_start, c.line_end, c.byte_start, c.byte_end, c.raw_text
            FROM content_sources s
            JOIN content_chunks c ON c.source_id = s.source_id
            WHERE s.path = $path
              AND s.status = 'active'
              AND s.content_kind IN ('{TextContentKind.WorkspaceSource}', '{TextContentKind.WorkspaceDocs}', '{TextContentKind.WorkspaceConfig}')
              AND c.path = s.path
              {lineWhere}
              {selectorWhere}
            ORDER BY c.line_start, c.byte_start
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$path", relativePath);
        command.Parameters.AddWithValue("$limit", limit);
        if (line is not null)
            command.Parameters.AddWithValue("$line", line.Value);

        using var reader = command.ExecuteReader();
        var candidates = new List<IndexedEditCandidate>();
        while (reader.Read())
        {
            candidates.Add(new IndexedEditCandidate(
                reader.GetString(3),
                checked((int)reader.GetInt64(4)),
                checked((int)reader.GetInt64(5)),
                reader.GetInt64(6),
                reader.GetInt64(7),
                reader.GetString(8),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetString(0)));
        }

        return candidates;
    }

    private static IReadOnlyList<string> Selectors(string? oldText, string? query, string? anchor)
    {
        var selectors = new List<string>(3);
        AddSelector(selectors, oldText);
        AddSelector(selectors, query);
        AddSelector(selectors, anchor);
        return selectors;
    }

    private static void AddSelector(List<string> selectors, string? value)
    {
        if (!string.IsNullOrEmpty(value))
            selectors.Add(value);
    }
}

public enum IndexedEditCandidateState
{
    Current,
    NoMatch,
    Unavailable,
}

public sealed record IndexedEditCandidateResult(
    IndexedEditCandidateState State,
    string Reason,
    IReadOnlyList<IndexedEditCandidate> Candidates,
    long? WorkspaceRevision)
{
    public static IndexedEditCandidateResult Current(IReadOnlyList<IndexedEditCandidate> candidates, long workspaceRevision) =>
        new(IndexedEditCandidateState.Current, "current", candidates, workspaceRevision);

    public static IndexedEditCandidateResult NoMatch(string reason) =>
        new(IndexedEditCandidateState.NoMatch, reason, [], WorkspaceRevision: null);

    public static IndexedEditCandidateResult Unavailable(string reason) =>
        new(IndexedEditCandidateState.Unavailable, reason, [], WorkspaceRevision: null);
}

public sealed record IndexedEditCandidate(
    string Path,
    int LineStart,
    int LineEnd,
    long ByteStart,
    long ByteEnd,
    string RawText,
    string SourceHash,
    long WorkspaceRevision,
    string SourceId);
