using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Miller.Indexing;

/// <summary>A single metric value inside a snapshot. <paramref name="DetailJson"/> is an optional bounded breakdown.</summary>
public sealed record MetricHistoryPoint(string Metric, double Value, string? DetailJson);

/// <summary>
/// One coherent metric-history snapshot: the identity of the artifact it was computed against plus the metrics
/// produced by one computing operation (<see cref="MetricHistorySnapshot.Source"/>).
/// </summary>
public sealed record MetricHistorySnapshot(
    string WorkspaceId,
    string ArtifactId,
    long Revision,
    string ExtractorVersion,
    string MillerVersion,
    string Source,
    IReadOnlyList<MetricHistoryPoint> Metrics);

/// <summary>Outcome of a history write. All values are non-throwing return states, not error conditions.</summary>
public enum MetricHistoryWriteResult
{
    /// <summary>The snapshot (and its metrics) were written.</summary>
    Recorded,

    /// <summary>Another writer held the DB write lock; skipped without blocking (leader converge path).</summary>
    SkippedBusy,

    /// <summary><c>meta.schema_version</c> is newer than this binary knows; skipped, file untouched.</summary>
    SkippedNewerSchema,

    /// <summary>A converge snapshot already exists for this <c>(artifact_id, revision)</c>; first writer wins.</summary>
    SkippedDuplicate,

    /// <summary>The artifact identity changed between capture and the append transaction; skipped.</summary>
    SkippedIdentityChanged,
}

/// <summary>One flattened trend point returned by <see cref="MetricHistoryStore.ReadTrend"/>.</summary>
public sealed record MetricHistoryTrendPoint(
    long SnapshotId,
    string RecordedAtUtc,
    string ArtifactId,
    long Revision,
    string Source,
    string Metric,
    double Value);

/// <summary>Best-effort status of a workspace's <c>history.db</c> sidecar for <c>workspace health</c>.</summary>
public sealed record MetricHistoryStatus(
    bool Present,
    int SchemaVersion,
    long SnapshotCount,
    long SizeBytes,
    bool CorruptRecovered);

/// <summary>
/// The single owner of <c>&lt;workspace&gt;/.miller/history.db</c> — a workspace-local, append-only SQLite sidecar
/// of deterministic-metric snapshots. Unlike <c>search.db</c>/<c>content.db</c> it is NEVER atomically replaced:
/// it is not derivable from the current artifact (it IS the history), so the rebuild-by-replace pattern would erase
/// it. All writes take <see cref="MetricHistoryWriteLock"/> for the append transaction only.
///
/// <para>Multi-process append is handled by construction: WAL mode; the leader's converge write is non-blocking
/// (<c>busy_timeout=0</c>, skip-on-busy) so it never stalls indexing; CLI heavy arms use a short busy timeout.
/// Corruption is unrecoverable data — on an open/integrity failure the file is renamed aside to
/// <c>history.db.corrupt-&lt;utc-stamp&gt;</c> and a fresh DB started. A <c>schema_version</c> newer than this
/// binary knows is skip-never-destroy (append-only history has no rebuild escape hatch).</para>
///
/// <para>Design: docs/plans/2026-07-07-metric-history-design.md.</para>
/// </summary>
public static class MetricHistoryStore
{
    /// <summary>The on-disk schema version stamped into <c>meta.schema_version</c>.</summary>
    public const int SchemaVersion = 1;

    public const string HistoryDbFileName = "history.db";

    // The leader's converge non-blocking guarantee is delivered primarily by the FILE lock: RecordConverge takes
    // MetricHistoryWriteLock with TimeSpan.Zero, so whenever another *Miller* writer is active it returns
    // SkippedBusy immediately without ever touching the DB. All legitimate Miller writers take that lock first, so
    // two of them never hold the SQLite write lock at once inside the ops gate.
    //
    // The DB-level busy budget below is only a backstop for a NON-Miller writer holding history.db (which does not
    // happen in Miller). It is driven by the connection's DefaultTimeout, NOT a `PRAGMA busy_timeout`:
    // Microsoft.Data.Sqlite re-applies sqlite3_busy_timeout(DefaultTimeout) on every command, clobbering any PRAGMA
    // on the next statement. DefaultTimeout is whole seconds and 0 means INFINITE, so 1 is the fail-fast floor.
    private static readonly TimeSpan RunLockTimeout = TimeSpan.FromSeconds(5);
    private const int ConvergeDbTimeoutSeconds = 1;
    private const int RunDbTimeoutSeconds = 2;
    private const int CorruptProbeTimeoutSeconds = 1;

    private const int SqliteBusy = 5;   // SQLITE_BUSY
    private const int SqliteLocked = 6; // SQLITE_LOCKED
    private const int SqliteCorrupt = 11; // SQLITE_CORRUPT
    private const int SqliteNotADb = 26;  // SQLITE_NOTADB

    private const string SchemaDdl = """
        CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS snapshots(
            snapshot_id       INTEGER PRIMARY KEY AUTOINCREMENT,
            recorded_at_utc   TEXT NOT NULL,
            workspace_id      TEXT NOT NULL,
            artifact_id       TEXT NOT NULL,
            revision          INTEGER NOT NULL,
            extractor_version TEXT NOT NULL,
            miller_version    TEXT NOT NULL,
            source            TEXT NOT NULL,
            UNIQUE(artifact_id, revision, source)
        );
        CREATE TABLE IF NOT EXISTS snapshot_metrics(
            snapshot_id INTEGER NOT NULL REFERENCES snapshots(snapshot_id) ON DELETE CASCADE,
            metric      TEXT NOT NULL,
            value       REAL NOT NULL,
            detail_json TEXT NULL,
            PRIMARY KEY(snapshot_id, metric)
        );
        CREATE INDEX IF NOT EXISTS idx_snapshot_metrics_metric ON snapshot_metrics(metric, snapshot_id);
        """;

    /// <summary>
    /// Record a leader <c>source='converge'</c> snapshot. Exactly one per <c>(artifact_id, revision)</c> — first
    /// writer wins via <c>INSERT OR IGNORE</c>. Non-blocking: if another writer holds the DB write lock the call
    /// returns <see cref="MetricHistoryWriteResult.SkippedBusy"/> immediately (no busy wait inside the leader's
    /// ops gate). The optional <paramref name="recordedAtUtc"/> overrides the writer-clock timestamp (test seam).
    /// </summary>
    public static MetricHistoryWriteResult RecordConverge(
        string historyDbPath, MetricHistorySnapshot snapshot, DateTime? recordedAtUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(historyDbPath);
        ArgumentNullException.ThrowIfNull(snapshot);

        EnsureDirectory(historyDbPath);
        RecoverIfCorrupt(historyDbPath);

        MetricHistoryWriteLock lease;
        try
        {
            lease = MetricHistoryWriteLock.AcquireFor(historyDbPath, TimeSpan.Zero);
        }
        catch (TimeoutException)
        {
            return MetricHistoryWriteResult.SkippedBusy;
        }

        using (lease)
        {
            try
            {
                using var connection = OpenForWrite(historyDbPath, ConvergeDbTimeoutSeconds);
                if (IsNewerSchema(connection))
                    return MetricHistoryWriteResult.SkippedNewerSchema;
                EnsureSchema(connection);

                using var tx = connection.BeginTransaction();
                using (var insert = connection.CreateCommand())
                {
                    insert.CommandText = InsertSnapshotSql(orIgnore: true);
                    BindSnapshot(insert, snapshot, FormatTimestamp(recordedAtUtc));
                    if (insert.ExecuteNonQuery() == 0)
                    {
                        tx.Rollback();
                        return MetricHistoryWriteResult.SkippedDuplicate;
                    }
                }

                InsertMetrics(connection, LastInsertRowId(connection), snapshot.Metrics);
                tx.Commit();
                return MetricHistoryWriteResult.Recorded;
            }
            catch (SqliteException ex) when (IsBusy(ex))
            {
                return MetricHistoryWriteResult.SkippedBusy;
            }
        }
    }

    /// <summary>
    /// Record a heavy-arm snapshot (report/churn/risk/candidates) as a per-source upsert: a re-run of the same
    /// operation at the same revision replaces only its own snapshot (row + metrics) in one transaction and touches
    /// nothing else. <paramref name="identityRecheck"/> is invoked INSIDE the append transaction; on a mismatch with
    /// the snapshot's <c>(ArtifactId, Revision)</c> the write is abandoned with
    /// <see cref="MetricHistoryWriteResult.SkippedIdentityChanged"/> (the artifact was replaced mid-command).
    /// </summary>
    public static MetricHistoryWriteResult RecordRun(
        string historyDbPath,
        MetricHistorySnapshot snapshot,
        Func<(string ArtifactId, long Revision)> identityRecheck,
        DateTime? recordedAtUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(historyDbPath);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(identityRecheck);

        EnsureDirectory(historyDbPath);
        RecoverIfCorrupt(historyDbPath);

        MetricHistoryWriteLock lease;
        try
        {
            lease = MetricHistoryWriteLock.AcquireFor(historyDbPath, RunLockTimeout);
        }
        catch (TimeoutException)
        {
            return MetricHistoryWriteResult.SkippedBusy;
        }

        using (lease)
        {
            try
            {
                using var connection = OpenForWrite(historyDbPath, RunDbTimeoutSeconds);
                if (IsNewerSchema(connection))
                    return MetricHistoryWriteResult.SkippedNewerSchema;
                EnsureSchema(connection);

                using var tx = connection.BeginTransaction();

                (string artifactId, long revision) = identityRecheck();
                if (!string.Equals(artifactId, snapshot.ArtifactId, StringComparison.Ordinal)
                    || revision != snapshot.Revision)
                {
                    tx.Rollback();
                    return MetricHistoryWriteResult.SkippedIdentityChanged;
                }

                using (var delete = connection.CreateCommand())
                {
                    delete.CommandText =
                        "DELETE FROM snapshots WHERE artifact_id = $art AND revision = $rev AND source = $src;";
                    delete.Parameters.AddWithValue("$art", snapshot.ArtifactId);
                    delete.Parameters.AddWithValue("$rev", snapshot.Revision);
                    delete.Parameters.AddWithValue("$src", snapshot.Source);
                    delete.ExecuteNonQuery();
                }

                using (var insert = connection.CreateCommand())
                {
                    insert.CommandText = InsertSnapshotSql(orIgnore: false);
                    BindSnapshot(insert, snapshot, FormatTimestamp(recordedAtUtc));
                    insert.ExecuteNonQuery();
                }

                InsertMetrics(connection, LastInsertRowId(connection), snapshot.Metrics);
                tx.Commit();
                return MetricHistoryWriteResult.Recorded;
            }
            catch (SqliteException ex) when (IsBusy(ex))
            {
                return MetricHistoryWriteResult.SkippedBusy;
            }
        }
    }

    /// <summary>
    /// Read a trend for the named <paramref name="metrics"/>: the metric rows of the most-recent
    /// <paramref name="limit"/> snapshots, ordered by <c>snapshot_id</c> (immune to writer clock skew;
    /// <c>recorded_at_utc</c> is display metadata only), each metric series uniform-stride downsampled to at most
    /// <paramref name="maxPoints"/> points. A <paramref name="limit"/>/<paramref name="maxPoints"/> ≤ 0 means "no
    /// limit"/"no downsampling". A missing metric is an absent row, never 0.
    /// </summary>
    public static IReadOnlyList<MetricHistoryTrendPoint> ReadTrend(
        string historyDbPath, IReadOnlyList<string> metrics, int limit, int maxPoints)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(historyDbPath);
        ArgumentNullException.ThrowIfNull(metrics);

        string full = Path.GetFullPath(historyDbPath);
        var wanted = metrics.Where(static m => !string.IsNullOrWhiteSpace(m)).Distinct(StringComparer.Ordinal).ToArray();
        if (wanted.Length == 0 || !File.Exists(full))
            return Array.Empty<MetricHistoryTrendPoint>();

        var rows = new List<MetricHistoryTrendPoint>();
        try
        {
            using var connection = SqliteReadOnlyAccess.Open(full);
            using var cmd = connection.CreateCommand();

            var metricPlaceholders = new string[wanted.Length];
            for (int i = 0; i < wanted.Length; i++)
            {
                metricPlaceholders[i] = "$m" + i.ToString(CultureInfo.InvariantCulture);
                cmd.Parameters.AddWithValue(metricPlaceholders[i], wanted[i]);
            }

            string recentFilter = limit > 0
                ? "AND sm.snapshot_id IN (SELECT snapshot_id FROM snapshots ORDER BY snapshot_id DESC LIMIT $limit)"
                : string.Empty;
            if (limit > 0)
                cmd.Parameters.AddWithValue("$limit", limit);

            cmd.CommandText = $"""
                SELECT s.snapshot_id, s.recorded_at_utc, s.artifact_id, s.revision, s.source, sm.metric, sm.value
                FROM snapshot_metrics sm
                JOIN snapshots s ON s.snapshot_id = sm.snapshot_id
                WHERE sm.metric IN ({string.Join(", ", metricPlaceholders)})
                {recentFilter}
                ORDER BY sm.metric, s.snapshot_id;
                """;

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new MetricHistoryTrendPoint(
                    SnapshotId: reader.GetInt64(0),
                    RecordedAtUtc: reader.GetString(1),
                    ArtifactId: reader.GetString(2),
                    Revision: reader.GetInt64(3),
                    Source: reader.GetString(4),
                    Metric: reader.GetString(5),
                    Value: reader.GetDouble(6)));
            }
        }
        catch (SqliteException)
        {
            return Array.Empty<MetricHistoryTrendPoint>();
        }
        catch (InvalidOperationException)
        {
            return Array.Empty<MetricHistoryTrendPoint>();
        }

        // Downsample per-metric series (rows are already grouped by metric, ordered by snapshot_id), then flatten
        // back to a snapshot_id-ordered list.
        var result = new List<MetricHistoryTrendPoint>();
        foreach (var group in rows.GroupBy(static r => r.Metric, StringComparer.Ordinal))
            result.AddRange(UniformStride(group.ToList(), maxPoints));
        result.Sort(static (a, b) =>
        {
            int bySnapshot = a.SnapshotId.CompareTo(b.SnapshotId);
            return bySnapshot != 0 ? bySnapshot : string.CompareOrdinal(a.Metric, b.Metric);
        });
        return result;
    }

    /// <summary>Best-effort sidecar status for <c>workspace health</c>. Never throws; unreadable ⟹ defaults.</summary>
    public static MetricHistoryStatus ReadStatus(string historyDbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(historyDbPath);
        string full = Path.GetFullPath(historyDbPath);
        string? dir = Path.GetDirectoryName(full);
        bool corruptRecovered = dir is not null && Directory.Exists(dir)
            && Directory.EnumerateFiles(dir, HistoryDbFileName + ".corrupt-*").Any();

        if (!File.Exists(full))
            return new MetricHistoryStatus(Present: false, SchemaVersion: 0, SnapshotCount: 0, SizeBytes: 0, corruptRecovered);

        long sizeBytes = new FileInfo(full).Length;
        int schemaVersion = 0;
        long snapshotCount = 0;
        try
        {
            using var connection = SqliteReadOnlyAccess.Open(full);
            schemaVersion = ReadSchemaVersion(connection) ?? 0;
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM snapshots;";
            snapshotCount = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
        }
        catch (SqliteException) { /* unreadable; best-effort defaults */ }
        catch (InvalidOperationException) { /* WAL sidecar dir not writable; best-effort defaults */ }

        return new MetricHistoryStatus(Present: true, schemaVersion, snapshotCount, sizeBytes, corruptRecovered);
    }

    // ---- internals -------------------------------------------------------------------------------------------

    private static string EnsureDirectory(string historyDbPath)
    {
        string full = Path.GetFullPath(historyDbPath);
        string dir = Path.GetDirectoryName(full)
            ?? throw new ArgumentException($"Path has no directory: {historyDbPath}", nameof(historyDbPath));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static SqliteConnection OpenForWrite(string historyDbPath, int dbTimeoutSeconds)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = historyDbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false, // append-only file is renamed aside on corruption; never let the pool retain a stale fd
            DefaultTimeout = dbTimeoutSeconds, // drives sqlite3_busy_timeout; 0 ⟹ fail-fast on a held write lock
        }.ToString());
        connection.Open();
        // foreign_keys is per-connection and needed for the RecordRun cascade delete; it does not modify the file,
        // so it is safe to run before the newer-schema check that must leave a newer DB untouched. WAL/DDL live in
        // EnsureSchema, which only runs after that check passes.
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private static void EnsureSchema(SqliteConnection connection)
    {
        using (var walPragma = connection.CreateCommand())
        {
            walPragma.CommandText = "PRAGMA journal_mode=WAL;";
            walPragma.ExecuteNonQuery();
        }
        using (var ddl = connection.CreateCommand())
        {
            ddl.CommandText = SchemaDdl;
            ddl.ExecuteNonQuery();
        }
        using var meta = connection.CreateCommand();
        meta.CommandText = "INSERT OR IGNORE INTO meta(key, value) VALUES ('schema_version', $v);";
        meta.Parameters.AddWithValue("$v", SchemaVersion.ToString(CultureInfo.InvariantCulture));
        meta.ExecuteNonQuery();
    }

    private static bool IsNewerSchema(SqliteConnection connection)
        => ReadSchemaVersion(connection) is int v && v > SchemaVersion;

    private static int? ReadSchemaVersion(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = 'schema_version';";
        try
        {
            object? raw = cmd.ExecuteScalar();
            if (raw is null or DBNull)
                return null;
            return int.TryParse(
                Convert.ToString(raw, CultureInfo.InvariantCulture),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
                ? v
                : null;
        }
        catch (SqliteException)
        {
            // No `meta` table yet (fresh/absent DB): not a newer schema.
            return null;
        }
    }

    private static string InsertSnapshotSql(bool orIgnore) => $"""
        INSERT {(orIgnore ? "OR IGNORE " : string.Empty)}INTO snapshots
            (recorded_at_utc, workspace_id, artifact_id, revision, extractor_version, miller_version, source)
        VALUES ($ts, $ws, $art, $rev, $ext, $mil, $src);
        """;

    private static void BindSnapshot(SqliteCommand cmd, MetricHistorySnapshot snapshot, string recordedAtUtc)
    {
        cmd.Parameters.AddWithValue("$ts", recordedAtUtc);
        cmd.Parameters.AddWithValue("$ws", snapshot.WorkspaceId);
        cmd.Parameters.AddWithValue("$art", snapshot.ArtifactId);
        cmd.Parameters.AddWithValue("$rev", snapshot.Revision);
        cmd.Parameters.AddWithValue("$ext", snapshot.ExtractorVersion);
        cmd.Parameters.AddWithValue("$mil", snapshot.MillerVersion);
        cmd.Parameters.AddWithValue("$src", snapshot.Source);
    }

    private static void InsertMetrics(SqliteConnection connection, long snapshotId, IReadOnlyList<MetricHistoryPoint> metrics)
    {
        if (metrics.Count == 0)
            return;

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO snapshot_metrics(snapshot_id, metric, value, detail_json)
            VALUES ($sid, $metric, $value, $detail);
            """;
        var pSid = cmd.Parameters.Add("$sid", SqliteType.Integer);
        var pMetric = cmd.Parameters.Add("$metric", SqliteType.Text);
        var pValue = cmd.Parameters.Add("$value", SqliteType.Real);
        var pDetail = cmd.Parameters.Add("$detail", SqliteType.Text);

        foreach (MetricHistoryPoint metric in metrics)
        {
            pSid.Value = snapshotId;
            pMetric.Value = metric.Metric;
            pValue.Value = metric.Value;
            pDetail.Value = (object?)metric.DetailJson ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }
    }

    private static long LastInsertRowId(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT last_insert_rowid();";
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
    }

    private static void RecoverIfCorrupt(string historyDbPath)
    {
        string full = Path.GetFullPath(historyDbPath);
        if (!File.Exists(full))
            return;

        bool corrupt = false;
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = full,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
                DefaultTimeout = CorruptProbeTimeoutSeconds, // never block behind a live writer just to probe
            }.ToString());
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA quick_check;";
            corrupt = cmd.ExecuteScalar() is not string ok
                || !string.Equals(ok, "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch (SqliteException ex) when (IsBusy(ex))
        {
            // Another writer holds the DB — it is clearly a live, valid database, not corrupt. Skip the probe.
            return;
        }
        catch (SqliteException ex) when (IsCorruption(ex))
        {
            corrupt = true;
        }

        if (!corrupt)
            return;

        RenameAside(full);
    }

    private static void RenameAside(string historyDbPath)
    {
        SqliteConnection.ClearAllPools();
        string stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture);
        string target = historyDbPath + ".corrupt-" + stamp;
        if (File.Exists(target))
            target = historyDbPath + ".corrupt-" + stamp + "-" + Guid.NewGuid().ToString("N");

        File.Move(historyDbPath, target);
        TryDelete(historyDbPath + "-wal");
        TryDelete(historyDbPath + "-shm");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    private static string FormatTimestamp(DateTime? recordedAtUtc)
    {
        DateTime value = recordedAtUtc ?? DateTime.UtcNow;
        if (value.Kind == DateTimeKind.Unspecified)
            value = DateTime.SpecifyKind(value, DateTimeKind.Utc);
        return value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
    }

    private static IReadOnlyList<MetricHistoryTrendPoint> UniformStride(
        IReadOnlyList<MetricHistoryTrendPoint> items, int maxPoints)
    {
        int n = items.Count;
        if (maxPoints <= 0 || n <= maxPoints)
            return items;
        if (maxPoints == 1)
            return new[] { items[n - 1] };

        var picked = new List<MetricHistoryTrendPoint>(maxPoints);
        int lastIndex = -1;
        for (int i = 0; i < maxPoints; i++)
        {
            int idx = (int)Math.Round((double)i * (n - 1) / (maxPoints - 1), MidpointRounding.AwayFromZero);
            if (idx != lastIndex)
            {
                picked.Add(items[idx]);
                lastIndex = idx;
            }
        }
        return picked;
    }

    private static bool IsBusy(SqliteException ex) => ex.SqliteErrorCode is SqliteBusy or SqliteLocked;

    private static bool IsCorruption(SqliteException ex) => ex.SqliteErrorCode is SqliteCorrupt or SqliteNotADb;
}
