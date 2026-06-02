using Microsoft.Data.Sqlite;

namespace Miller.Indexing;

/// <summary>The kind of change a <see cref="RevisionFileChange"/> records (julie v1 <c>change_kind</c>).</summary>
public enum RevisionChangeKind
{
    /// <summary>A new file appeared in this revision (v1 <c>inserted</c>).</summary>
    Added,

    /// <summary>An existing file's content changed in this revision (v1 <c>updated</c>).</summary>
    Modified,

    /// <summary>A file was removed in this revision (v1 <c>deleted</c>).</summary>
    Deleted,

    /// <summary>A file became unsupported and is no longer represented (v1 <c>unsupported</c>); for
    /// freshness/incremental purposes treat it as a removal from the index.</summary>
    Unsupported,
}

/// <summary>
/// One row of julie's v1 <c>revision_file_changes</c> delta: which file changed, how, and in which revision.
/// The exact changed-file delta — gold for the incremental rebuild the freshness path would consume and for M4.
/// <see cref="Path"/> is workspace-relative (the v1 <c>path</c> column).
/// </summary>
public sealed record RevisionFileChange(long RevisionId, string Path, RevisionChangeKind ChangeKind);

/// <summary>
/// Owns the long-lived <c>Mode=ReadOnly</c> connection that every Miller instance polls for freshness
/// (decision-2). On an interval (and right after the leader's own <c>extract</c>) the freshness service calls
/// <see cref="LatestRevision"/>; when it exceeds the held index's built revision, the instance rebuilds + swaps.
///
/// <para><b>The no-lingering-transaction contract (verified-fact 8).</b> The connection holds NO open explicit
/// transaction between polls: each query auto-commits, so the next <see cref="LatestRevision"/> command sees a
/// fresh snapshot of the WAL at its latest committed state — picking up a separate writer's (julie-extract's)
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
    /// The freshness cursor: <c>SELECT MAX(revision_id) FROM extraction_revisions</c>. v1 has no
    /// <c>workspace_id</c> (one DB = one root), so there is no per-workspace filter. Returns 0 when the DB has no
    /// revision yet (the "no revision" sentinel — MAX over no rows is SQL NULL). Re-runnable on the same
    /// connection for every poll.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The reader has been disposed.</exception>
    public long LatestRevision()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT MAX(revision_id) FROM extraction_revisions;";

        object? result = cmd.ExecuteScalar();
        // MAX over zero matching rows is SQL NULL → DBNull here. Map to 0 (no revision yet).
        return result is null or DBNull ? 0L : Convert.ToInt64(result);
    }

    /// <summary>
    /// The changed-file delta strictly AFTER <paramref name="sinceRevision"/>, from v1's
    /// <c>revision_file_changes</c>, ordered by revision_id then path. v1 has no <c>workspace_id</c> (one DB =
    /// one root), so there is no per-workspace filter. Used by the future incremental rebuild / M4. Returns an
    /// empty list when nothing changed since.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The reader has been disposed.</exception>
    public IReadOnlyList<RevisionFileChange> ChangedSince(long sinceRevision)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "SELECT revision_id, path, change_kind FROM revision_file_changes " +
            "WHERE revision_id > $since ORDER BY revision_id, path;";
        cmd.Parameters.AddWithValue("$since", sinceRevision);

        var changes = new List<RevisionFileChange>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            long revisionId = reader.GetInt64(0);
            string path = reader.GetString(1);
            string changeKind = reader.GetString(2);
            changes.Add(new RevisionFileChange(revisionId, path, ParseChangeKind(changeKind)));
        }

        return changes;
    }

    // v1's revision_file_changes.change_kind has NO CHECK constraint (julie-extractors schema.rs:47) — Miller is
    // the only guard. The writer emits exactly inserted|updated|deleted|unsupported (model.rs:60-66); anything
    // else means the v1 contract drifted (a future julie-extract), so fail loud rather than misclassify.
    private static RevisionChangeKind ParseChangeKind(string changeKind) => changeKind switch
    {
        "inserted" => RevisionChangeKind.Added,
        "updated" => RevisionChangeKind.Modified,
        "deleted" => RevisionChangeKind.Deleted,
        "unsupported" => RevisionChangeKind.Unsupported,
        _ => throw new InvalidOperationException(
            $"Unknown revision_file_changes.change_kind '{changeKind}'; expected " +
            "inserted|updated|deleted|unsupported (the julie-extract v1 schema may have drifted)."),
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
