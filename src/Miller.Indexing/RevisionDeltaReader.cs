using Microsoft.Data.Sqlite;
using Miller.Indexing.Reads;

namespace Miller.Indexing;

/// <summary>Whether an index-revision delta could be reconstructed for the requested span (R0/R3).</summary>
public enum RevisionDeltaStatus
{
    /// <summary>The span <c>(from, to]</c> is fully journaled; <see cref="RevisionDeltaResult.ChangedPaths"/>
    /// is a truthful file delta for it.</summary>
    Complete,

    /// <summary>The mechanism cannot vouch for the span (no journal, pruned/rebuilt history, a base ahead of
    /// current, or a read failure). Callers must fall back conservatively — never treat the empty path list as a
    /// truthful "nothing changed".</summary>
    Unavailable,
}

/// <summary>
/// The result of an index-revision delta query. <see cref="ToRevision"/> is the revision the delta was actually
/// computed to (the current index revision at read time), reported even when <see cref="Status"/> is
/// <see cref="RevisionDeltaStatus.Unavailable"/> so callers can compare it against the revision they observed.
/// <see cref="ChangedPaths"/> is workspace-relative and empty whenever the status is not
/// <see cref="RevisionDeltaStatus.Complete"/>. <see cref="Reason"/> is a stable machine token for logging/tests.
/// </summary>
public sealed record RevisionDeltaResult(
    RevisionDeltaStatus Status,
    long FromRevision,
    long ToRevision,
    string? ArtifactId,
    IReadOnlyList<string> ChangedPaths,
    string Reason,
    IReadOnlyList<string>? DeletedPaths = null);

/// <summary>
/// Computes the file delta between a base index revision and the current index revision from julie-extract's own
/// per-file change journal — the <c>revision_file_changes</c> table (each row stamps a <c>path</c> with the
/// <c>revision_id</c> it changed at and a <c>change_kind</c> of inserted/updated/deleted). This is the
/// per-file-revision-stamp mechanism the CT revision-delta contract calls for (design
/// 2026-07-03-ct-revision-delta-design.md §1): Miller keeps no separate journal because the extract already is one.
///
/// <para><b>Truthful inclusion (R1).</b> The journal records every file julie-extract processed for a revision,
/// including files that produce no code symbols (config, docs, data-shaped json/yaml) — so a change to a file
/// Miller does not parse into symbols still appears. It answers "what that Miller watches changed on disk", not
/// "what got indexed into the symbol graph". Renames land as a delete of the old path plus an insert of the new;
/// both appear.</para>
///
/// <para><b>Honest span failure (R3).</b> The reader never returns a guessed-empty delta it cannot vouch for. A
/// base ahead of the current revision (a full rebuild restarted the counter, or a bogus base), a base below the
/// retained history floor (pruned/rebuilt history), a missing journal, or a read failure each yield
/// <see cref="RevisionDeltaStatus.Unavailable"/>. Exclusion of ignored/tooling paths (R2) is the caller's edge —
/// this reader reports the raw journal, which already omits ignored paths because Miller never feeds them to the
/// extractor; the delta tool re-applies Miller's watch/ignore policy as defense-in-depth.</para>
/// </summary>
public static class RevisionDeltaReader
{
    public static RevisionDeltaResult Read(
        IWorkspaceReadSession session,
        long fromRevision,
        string? fromArtifactId = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        try
        {
            return session.Read(connection => Read(connection, fromRevision, fromArtifactId));
        }
        catch (Exception ex) when (ex is InvalidOperationException or SqliteException or IOException)
        {
            return Unavailable(fromRevision, toRevision: 0, artifactId: null, "read_error");
        }
    }

    /// <summary>
    /// Read the delta for <paramref name="fromRevision"/> (exclusive) to the current index revision (inclusive)
    /// from the extract DB at <paramref name="extractDbPath"/>. Never throws for an expected condition (missing
    /// DB, missing journal, unreconstructable span, WAL/read failure) — those map to
    /// <see cref="RevisionDeltaStatus.Unavailable"/>.
    /// </summary>
    public static RevisionDeltaResult Read(string extractDbPath, long fromRevision, string? fromArtifactId = null)
    {
        string abs;
        try
        {
            abs = Path.GetFullPath(extractDbPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Unavailable(fromRevision, toRevision: 0, artifactId: null, "invalid_db_path");
        }

        // A missing extract DB is not a crash: the mechanism simply cannot vouch for any span.
        if (!File.Exists(abs))
            return Unavailable(fromRevision, toRevision: 0, artifactId: null, "no_index");

        try
        {
            using SqliteConnection conn = SqliteReadOnlyAccess.Open(abs);
            return Read(conn, fromRevision, fromArtifactId);
        }
        catch (Exception ex) when (ex is InvalidOperationException or SqliteException or IOException)
        {
            // WAL-sidecar / read-only / corrupt-DB failure mid-read: cannot vouch for the span → unavailable,
            // not a thrown exception the CLI would surface as an error.
            return Unavailable(fromRevision, toRevision: 0, artifactId: null, "read_error");
        }
    }

    private static RevisionDeltaResult Read(
        SqliteConnection conn,
        long fromRevision,
        string? fromArtifactId)
    {
        if (!TableExists(conn, "extraction_revisions") || !TableExists(conn, "revision_file_changes"))
            return Unavailable(fromRevision, toRevision: 0, artifactId: null, "no_journal");
        long current = ScalarLong(conn, "SELECT MAX(revision_id) FROM extraction_revisions;");
        long? floor = ScalarNullableLong(conn, "SELECT MIN(revision_id) FROM extraction_revisions;");
        string? artifactId = ReadArtifactId(conn);
        if (fromRevision < 0)
            return Unavailable(fromRevision, current, artifactId, "invalid_from_revision");
        if (string.IsNullOrWhiteSpace(artifactId))
            return Unavailable(fromRevision, current, artifactId, "no_artifact_id");
        if (string.IsNullOrWhiteSpace(fromArtifactId))
            return Unavailable(fromRevision, current, artifactId, "missing_from_artifact_id");
        if (!string.Equals(fromArtifactId, artifactId, StringComparison.Ordinal))
            return Unavailable(fromRevision, current, artifactId, "artifact_changed");
        if (fromRevision > current)
            return Unavailable(fromRevision, current, artifactId, "from_after_current");
        if (floor is long retainedFloor && fromRevision < retainedFloor - 1)
            return Unavailable(fromRevision, current, artifactId, "pruned_history");
        (IReadOnlyList<string> paths, IReadOnlyList<string> deletedPaths) =
            ReadChangedPaths(conn, fromRevision, current);
        return new RevisionDeltaResult(
            RevisionDeltaStatus.Complete,
            fromRevision,
            current,
            artifactId,
            paths,
            "complete",
            deletedPaths);
    }

    private static RevisionDeltaResult Unavailable(long fromRevision, long toRevision, string? artifactId, string reason) =>
        new(
            RevisionDeltaStatus.Unavailable,
            fromRevision,
            toRevision,
            artifactId,
            Array.Empty<string>(),
            reason,
            Array.Empty<string>());

    private static (IReadOnlyList<string> Paths, IReadOnlyList<string> DeletedPaths) ReadChangedPaths(
        SqliteConnection conn,
        long fromRevision,
        long toRevision)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            WITH ranked AS (
                SELECT path, change_kind,
                       ROW_NUMBER() OVER (
                           PARTITION BY path
                           ORDER BY revision_id DESC
                       ) AS position
                FROM revision_file_changes
                WHERE revision_id > $from AND revision_id <= $to
            )
            SELECT path, change_kind
            FROM ranked
            WHERE position = 1
            ORDER BY path;
            """;
        cmd.Parameters.AddWithValue("$from", fromRevision);
        cmd.Parameters.AddWithValue("$to", toRevision);

        var paths = new List<string>();
        var deletedPaths = new List<string>();
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0))
                continue;
            string path = reader.GetString(0);
            if (!string.IsNullOrWhiteSpace(path))
            {
                paths.Add(path);
                if (!reader.IsDBNull(1) &&
                    string.Equals(reader.GetString(1), "deleted", StringComparison.OrdinalIgnoreCase))
                    deletedPaths.Add(path);
            }
        }

        return (paths, deletedPaths);
    }

    private static bool TableExists(SqliteConnection conn, string tableName)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        cmd.Parameters.AddWithValue("$name", tableName);
        return cmd.ExecuteScalar() is not null;
    }

    private static long ScalarLong(SqliteConnection conn, string sql)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        object? value = cmd.ExecuteScalar();
        return value is null or DBNull ? 0L : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static long? ScalarNullableLong(SqliteConnection conn, string sql)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        object? value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string? ReadArtifactId(SqliteConnection conn)
    {
        if (!TableExists(conn, "artifact_metadata"))
            return null;

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM artifact_metadata WHERE key = 'artifact_id' LIMIT 1;";
        object? value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
