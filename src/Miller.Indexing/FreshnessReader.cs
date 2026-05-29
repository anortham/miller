using Microsoft.Data.Sqlite;

namespace Miller.Indexing;

/// <summary>The kind of change a <see cref="RevisionFileChange"/> records (julie's <c>change_kind</c>).</summary>
public enum RevisionChangeKind
{
    /// <summary>A new file appeared in this revision.</summary>
    Added,

    /// <summary>An existing file's content changed in this revision.</summary>
    Modified,

    /// <summary>A file was removed in this revision.</summary>
    Deleted,
}

/// <summary>
/// One row of julie's <c>revision_file_changes</c> delta: which file changed, how, and in which revision. The
/// exact changed-file delta (verified-fact 5) — gold for the incremental rebuild decision-5 would consume and
/// for M4. <see cref="FilePath"/> is workspace-relative (as julie stores it).
/// </summary>
public sealed record RevisionFileChange(long Revision, string FilePath, RevisionChangeKind ChangeKind);

/// <summary>
/// Owns the long-lived <c>Mode=ReadOnly</c> connection that every Miller instance polls for freshness
/// (decision-2). On an interval (and right after the leader's own <c>extract</c>) the freshness service calls
/// <see cref="LatestRevision"/>; when it exceeds the held index's built revision, the instance rebuilds + swaps.
///
/// <para><b>The no-lingering-transaction contract (verified-fact 8).</b> The connection holds NO open explicit
/// transaction between polls: each query auto-commits, so the next <see cref="LatestRevision"/> command sees a
/// fresh snapshot of the WAL at its latest committed state — picking up a separate writer's (julie-server's)
/// commits WITHOUT reopening the connection. An open transaction (or an undisposed reader) would pin a stale
/// snapshot and silently freeze freshness; this type therefore never opens one and disposes every command/reader
/// promptly.</para>
///
/// <para>Not safe for concurrent use from multiple threads on the same instance (one connection). The hosted
/// freshness service polls from a single loop.</para>
/// </summary>
public sealed class FreshnessReader : IDisposable
{
    private readonly SqliteConnection _connection;
    private bool _disposed;

    /// <summary>The resolved absolute path of the DB this reader polls.</summary>
    public string DbPath { get; }

    /// <summary>
    /// Open the long-lived read connection against <paramref name="dbPath"/> using the shared D4 read discipline
    /// (file-exists + writable-dir probe + <c>Mode=ReadOnly</c> + SQLITE_READONLY mapping).
    /// </summary>
    /// <exception cref="FileNotFoundException">The DB file does not exist.</exception>
    /// <exception cref="InvalidOperationException">The DB's directory is not writable (WAL sidecar trap).</exception>
    public FreshnessReader(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        DbPath = Path.GetFullPath(dbPath);
        _connection = SqliteReadOnlyAccess.Open(dbPath);
    }

    /// <summary>
    /// The freshness cursor: <c>SELECT MAX(revision) FROM canonical_revisions WHERE workspace_id=@id</c>.
    /// Returns 0 when the workspace has no revision yet (the "no revision" sentinel — MAX over no rows is SQL
    /// NULL). Re-runnable on the same connection for every poll.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="workspaceId"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">The reader has been disposed.</exception>
    public long LatestRevision(string workspaceId)
    {
        ArgumentNullException.ThrowIfNull(workspaceId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "SELECT MAX(revision) FROM canonical_revisions WHERE workspace_id = $ws;";
        cmd.Parameters.AddWithValue("$ws", workspaceId);

        object? result = cmd.ExecuteScalar();
        // MAX over zero matching rows is SQL NULL → DBNull here. Map to 0 (no revision yet).
        return result is null or DBNull ? 0L : Convert.ToInt64(result);
    }

    /// <summary>
    /// The changed-file delta strictly AFTER <paramref name="sinceRevision"/> for <paramref name="workspaceId"/>,
    /// from <c>revision_file_changes</c> (verified-fact 5), ordered by revision then file_path. Used by the
    /// future incremental rebuild (decision-5) / M4. Returns an empty list when nothing changed since.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="workspaceId"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">The reader has been disposed.</exception>
    public IReadOnlyList<RevisionFileChange> ChangedSince(long sinceRevision, string workspaceId)
    {
        ArgumentNullException.ThrowIfNull(workspaceId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "SELECT revision, file_path, change_kind FROM revision_file_changes " +
            "WHERE workspace_id = $ws AND revision > $since " +
            "ORDER BY revision, file_path;";
        cmd.Parameters.AddWithValue("$ws", workspaceId);
        cmd.Parameters.AddWithValue("$since", sinceRevision);

        var changes = new List<RevisionFileChange>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            long revision = reader.GetInt64(0);
            string filePath = reader.GetString(1);
            string changeKind = reader.GetString(2);
            changes.Add(new RevisionFileChange(revision, filePath, ParseChangeKind(changeKind)));
        }

        return changes;
    }

    // julie CHECK-constrains change_kind to exactly {added, modified, deleted}, so an unknown value means the
    // schema drifted (a future julie) — fail loudly rather than silently misclassify a change as Modified.
    private static RevisionChangeKind ParseChangeKind(string changeKind) => changeKind switch
    {
        "added" => RevisionChangeKind.Added,
        "modified" => RevisionChangeKind.Modified,
        "deleted" => RevisionChangeKind.Deleted,
        _ => throw new InvalidOperationException(
            $"Unknown revision_file_changes.change_kind '{changeKind}'; expected added|modified|deleted " +
            "(the julie extract schema may have drifted)."),
    };

    /// <summary>Close the long-lived read connection.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _connection.Dispose();
    }
}
