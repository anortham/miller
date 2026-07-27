using System.Globalization;
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
/// Owns the ONE <c>Mode=ReadOnly</c> connection a freshness poll reads through (decision-2): the freshness
/// service calls <see cref="LatestRevision"/> (and <see cref="ArtifactId"/>); when the writer moved ahead — or
/// the artifact file was replaced by a full rebuild — the instance rebuilds + swaps. The hosted poll constructs
/// a TRANSIENT reader per tick: a connection that outlives a <see cref="FullRebuildPromotion"/> promote keeps
/// an fd to the unlinked OLD inode and would silently freeze freshness forever (the same trap behind the
/// <see cref="SqliteReadOnlyAccess"/> <c>Pooling=false</c> rule). Holding one instance across polls remains
/// valid ONLY while the underlying file cannot be replaced.
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

    /// <summary>
    /// The artifact's identity: <c>artifact_metadata.artifact_id</c>, stamped by julie-extract when the DB file
    /// is CREATED and stable across in-place delta updates — so a changed id means the file was replaced by a
    /// full rebuild (<see cref="FullRebuildPromotion"/>), whose restarted revision counter makes
    /// <see cref="LatestRevision"/> alone unable to signal the change. Null when the key or table is absent
    /// (a synthetic/legacy DB) — callers must treat null as "unknown", never as "unchanged".
    /// </summary>
    /// <exception cref="ObjectDisposedException">The reader has been disposed.</exception>
    /// <summary>
    /// Whether this DB carries an <c>artifact_metadata</c> table with rows. Distinguishes a genuine pre-stamping
    /// extract, where a null <see cref="ArtifactId"/> is expected and harmless, from a metadata table that exists
    /// but has no id — a shape the pinned extractor never emits, so a null there is not to be trusted.
    /// </summary>
    public bool HasArtifactMetadata()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM artifact_metadata);";
        try
        {
            return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 /* SQLITE_ERROR: no such table */)
        {
            return false;
        }
    }

    public string? ArtifactId()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM artifact_metadata WHERE key = 'artifact_id';";
        try
        {
            return cmd.ExecuteScalar() as string;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 /* SQLITE_ERROR: no such table */)
        {
            return null;
        }
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
