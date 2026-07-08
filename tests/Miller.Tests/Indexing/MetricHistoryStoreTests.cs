using System.Globalization;
using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins the append-only <c>history.db</c> contract <see cref="MetricHistoryStore"/> owns: schema shape, the three
/// write paths (converge INSERT-OR-IGNORE dedup, per-source heavy upsert, identity re-check), trend read ordering
/// by <c>snapshot_id</c> with uniform-stride downsampling, corruption rename-aside recovery, skip-on-busy, and
/// newer-schema skip-not-destroy. Pure temp-dir SQLite — fast suite, no julie-extract spawn.
/// </summary>
public sealed class MetricHistoryStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public MetricHistoryStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-historydb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, MetricHistoryStore.HistoryDbFileName);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static MetricHistorySnapshot Snapshot(
        string source,
        long revision,
        string artifactId = "art-1",
        string workspaceId = "ws-1",
        params (string Metric, double Value, string? Detail)[] metrics)
        => new(
            WorkspaceId: workspaceId,
            ArtifactId: artifactId,
            Revision: revision,
            ExtractorVersion: "2.11.0",
            MillerVersion: "0.9.9",
            Source: source,
            Metrics: metrics.Select(m => new MetricHistoryPoint(m.Metric, m.Value, m.Detail)).ToList());

    private SqliteConnection OpenRead()
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        c.Open();
        return c;
    }

    private static object? Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    // ---- schema ----------------------------------------------------------------------------------------------

    [Fact]
    public void RecordConverge_creates_schema_matching_the_ddl()
    {
        var result = MetricHistoryStore.RecordConverge(
            _dbPath, Snapshot("converge", revision: 1, metrics: ("symbol_count", 100, null)));

        Assert.Equal(MetricHistoryWriteResult.Recorded, result);
        using var c = OpenRead();

        Assert.Equal("1", Convert.ToString(
            Scalar(c, "SELECT value FROM meta WHERE key='schema_version';"), CultureInfo.InvariantCulture));

        var snapshotColumns = TableColumns(c, "snapshots");
        Assert.Equal(
            new[] { "snapshot_id", "recorded_at_utc", "workspace_id", "artifact_id", "revision", "extractor_version", "miller_version", "source" },
            snapshotColumns);

        var metricColumns = TableColumns(c, "snapshot_metrics");
        Assert.Equal(new[] { "snapshot_id", "metric", "value", "detail_json" }, metricColumns);

        // UNIQUE(artifact_id, revision, source) and the metric index exist.
        Assert.True(Convert.ToInt64(Scalar(c,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='idx_snapshot_metrics_metric';"),
            CultureInfo.InvariantCulture) == 1);
    }

    private static string[] TableColumns(SqliteConnection c, string table)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info('{table}');";
        var cols = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            cols.Add(reader.GetString(1));
        return cols.ToArray();
    }

    // ---- converge dedup --------------------------------------------------------------------------------------

    [Fact]
    public void RecordConverge_second_identical_revision_is_skipped_duplicate_and_changes_nothing()
    {
        Assert.Equal(MetricHistoryWriteResult.Recorded, MetricHistoryStore.RecordConverge(
            _dbPath, Snapshot("converge", revision: 3, metrics: ("symbol_count", 100, null))));

        // Second converge at the same (artifact_id, revision) with different values must be ignored.
        var second = MetricHistoryStore.RecordConverge(
            _dbPath, Snapshot("converge", revision: 3, metrics: ("symbol_count", 999, null)));

        Assert.Equal(MetricHistoryWriteResult.SkippedDuplicate, second);
        using var c = OpenRead();
        Assert.Equal(1L, Convert.ToInt64(Scalar(c, "SELECT COUNT(*) FROM snapshots;"), CultureInfo.InvariantCulture));
        Assert.Equal(100.0, Convert.ToDouble(
            Scalar(c, "SELECT value FROM snapshot_metrics WHERE metric='symbol_count';"), CultureInfo.InvariantCulture));
    }

    // ---- per-source upsert -----------------------------------------------------------------------------------

    [Fact]
    public void RecordRun_per_source_upsert_keeps_sources_independent_and_replaces_only_its_own_snapshot()
    {
        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var t3 = new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(MetricHistoryWriteResult.Recorded, MetricHistoryStore.RecordRun(
            _dbPath, Snapshot("churn", revision: 5, metrics: ("churn_files_changed", 10, null)),
            () => ("art-1", 5), t1));
        Assert.Equal(MetricHistoryWriteResult.Recorded, MetricHistoryStore.RecordRun(
            _dbPath, Snapshot("risk", revision: 5, metrics: ("risk_top_score", 42, null)),
            () => ("art-1", 5), t2));

        using (var c = OpenRead())
        {
            Assert.Equal(2L, Convert.ToInt64(Scalar(c, "SELECT COUNT(*) FROM snapshots;"), CultureInfo.InvariantCulture));
            // Independent timestamps.
            Assert.NotEqual(
                Convert.ToString(Scalar(c, "SELECT recorded_at_utc FROM snapshots WHERE source='churn';"), CultureInfo.InvariantCulture),
                Convert.ToString(Scalar(c, "SELECT recorded_at_utc FROM snapshots WHERE source='risk';"), CultureInfo.InvariantCulture));
        }

        long riskSnapshotId;
        string riskTs;
        using (var c = OpenRead())
        {
            riskSnapshotId = Convert.ToInt64(Scalar(c, "SELECT snapshot_id FROM snapshots WHERE source='risk';"), CultureInfo.InvariantCulture);
            riskTs = Convert.ToString(Scalar(c, "SELECT recorded_at_utc FROM snapshots WHERE source='risk';"), CultureInfo.InvariantCulture)!;
        }

        // Re-run churn at the same revision: replaces the churn snapshot only.
        Assert.Equal(MetricHistoryWriteResult.Recorded, MetricHistoryStore.RecordRun(
            _dbPath, Snapshot("churn", revision: 5, metrics: ("churn_files_changed", 77, null)),
            () => ("art-1", 5), t3));

        using (var c = OpenRead())
        {
            Assert.Equal(2L, Convert.ToInt64(Scalar(c, "SELECT COUNT(*) FROM snapshots;"), CultureInfo.InvariantCulture));
            // FK ON DELETE CASCADE left no orphaned metric rows: exactly one metric per remaining snapshot.
            Assert.Equal(2L, Convert.ToInt64(Scalar(c, "SELECT COUNT(*) FROM snapshot_metrics;"), CultureInfo.InvariantCulture));
            Assert.Equal(77.0, Convert.ToDouble(
                Scalar(c, "SELECT value FROM snapshot_metrics sm JOIN snapshots s ON s.snapshot_id=sm.snapshot_id WHERE s.source='churn';"),
                CultureInfo.InvariantCulture));
            // Risk snapshot untouched: same id, same timestamp.
            Assert.Equal(riskSnapshotId, Convert.ToInt64(Scalar(c, "SELECT snapshot_id FROM snapshots WHERE source='risk';"), CultureInfo.InvariantCulture));
            Assert.Equal(riskTs, Convert.ToString(Scalar(c, "SELECT recorded_at_utc FROM snapshots WHERE source='risk';"), CultureInfo.InvariantCulture));
        }
    }

    // ---- identity re-check -----------------------------------------------------------------------------------

    [Fact]
    public void RecordRun_identity_mismatch_writes_nothing()
    {
        var result = MetricHistoryStore.RecordRun(
            _dbPath, Snapshot("report", revision: 1, artifactId: "art-1", metrics: ("symbol_count", 5, null)),
            () => ("art-DIFFERENT", 1));

        Assert.Equal(MetricHistoryWriteResult.SkippedIdentityChanged, result);
        using var c = OpenRead();
        Assert.Equal(0L, Convert.ToInt64(Scalar(c, "SELECT COUNT(*) FROM snapshots;"), CultureInfo.InvariantCulture));
        Assert.Equal(0L, Convert.ToInt64(Scalar(c, "SELECT COUNT(*) FROM snapshot_metrics;"), CultureInfo.InvariantCulture));
    }

    [Fact]
    public void RecordRun_revision_mismatch_writes_nothing()
    {
        var result = MetricHistoryStore.RecordRun(
            _dbPath, Snapshot("report", revision: 7, metrics: ("symbol_count", 5, null)),
            () => ("art-1", 8));

        Assert.Equal(MetricHistoryWriteResult.SkippedIdentityChanged, result);
        using var c = OpenRead();
        Assert.Equal(0L, Convert.ToInt64(Scalar(c, "SELECT COUNT(*) FROM snapshots;"), CultureInfo.InvariantCulture));
    }

    // ---- skip on busy ----------------------------------------------------------------------------------------

    [Fact]
    public void RecordConverge_returns_skipped_busy_when_db_write_lock_is_held()
    {
        // Seed so the schema/db already exist.
        Assert.Equal(MetricHistoryWriteResult.Recorded, MetricHistoryStore.RecordConverge(
            _dbPath, Snapshot("converge", revision: 1, metrics: ("symbol_count", 100, null))));

        using var blocker = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        blocker.Open();
        using (var busy = blocker.CreateCommand())
        {
            busy.CommandText = "PRAGMA busy_timeout=0;";
            busy.ExecuteNonQuery();
        }
        using (var begin = blocker.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE;"; // hold the WAL write lock
            begin.ExecuteNonQuery();
        }

        try
        {
            var result = MetricHistoryStore.RecordConverge(
                _dbPath, Snapshot("converge", revision: 2, metrics: ("symbol_count", 200, null)));
            Assert.Equal(MetricHistoryWriteResult.SkippedBusy, result);
        }
        finally
        {
            using var rollback = blocker.CreateCommand();
            rollback.CommandText = "ROLLBACK;";
            rollback.ExecuteNonQuery();
        }

        // The busy revision never landed.
        using var c = OpenRead();
        Assert.Equal(1L, Convert.ToInt64(Scalar(c, "SELECT COUNT(*) FROM snapshots;"), CultureInfo.InvariantCulture));
    }

    // ---- newer schema ----------------------------------------------------------------------------------------

    [Fact]
    public void Newer_schema_version_makes_both_writers_skip_and_leaves_the_file_untouched()
    {
        SeedNewerSchemaDb(schemaVersion: MetricHistoryStore.SchemaVersion + 1);
        long sizeBefore = new FileInfo(_dbPath).Length;

        Assert.Equal(MetricHistoryWriteResult.SkippedNewerSchema, MetricHistoryStore.RecordConverge(
            _dbPath, Snapshot("converge", revision: 1, metrics: ("symbol_count", 1, null))));
        Assert.Equal(MetricHistoryWriteResult.SkippedNewerSchema, MetricHistoryStore.RecordRun(
            _dbPath, Snapshot("report", revision: 1, metrics: ("symbol_count", 1, null)), () => ("art-1", 1)));

        using var c = OpenRead();
        Assert.Equal(0L, Convert.ToInt64(Scalar(c, "SELECT COUNT(*) FROM snapshots;"), CultureInfo.InvariantCulture));
        Assert.Equal((MetricHistoryStore.SchemaVersion + 1).ToString(CultureInfo.InvariantCulture),
            Convert.ToString(Scalar(c, "SELECT value FROM meta WHERE key='schema_version';"), CultureInfo.InvariantCulture));
        Assert.Equal(sizeBefore, new FileInfo(_dbPath).Length);
    }

    private void SeedNewerSchemaDb(int schemaVersion)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE snapshots(snapshot_id INTEGER PRIMARY KEY AUTOINCREMENT, recorded_at_utc TEXT NOT NULL,
                workspace_id TEXT NOT NULL, artifact_id TEXT NOT NULL, revision INTEGER NOT NULL,
                extractor_version TEXT NOT NULL, miller_version TEXT NOT NULL, source TEXT NOT NULL,
                UNIQUE(artifact_id, revision, source));
            INSERT INTO meta(key, value) VALUES ('schema_version', $v);
            """;
        cmd.Parameters.AddWithValue("$v", schemaVersion.ToString(CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
    }

    // ---- corruption ------------------------------------------------------------------------------------------

    [Fact]
    public void RecordConverge_corrupt_file_is_reactively_renamed_aside_and_the_snapshot_still_lands()
    {
        // No proactive probe: the corruption is only discovered when the write itself opens the DB, which must
        // rename the garbage aside and retry the write once so the current snapshot still lands.
        File.WriteAllText(_dbPath, "this is not a sqlite database at all, just garbage bytes");

        var result = MetricHistoryStore.RecordConverge(
            _dbPath, Snapshot("converge", revision: 1, metrics: ("symbol_count", 100, null)));

        Assert.Equal(MetricHistoryWriteResult.Recorded, result);

        var corruptSiblings = Directory.EnumerateFiles(_dir, MetricHistoryStore.HistoryDbFileName + ".corrupt-*").ToArray();
        Assert.Single(corruptSiblings);

        using (var c = OpenRead())
            Assert.Equal(1L, Convert.ToInt64(Scalar(c, "SELECT COUNT(*) FROM snapshots;"), CultureInfo.InvariantCulture));

        var status = MetricHistoryStore.ReadStatus(_dbPath);
        Assert.True(status.Present);
        Assert.True(status.CorruptRecovered);
        Assert.Equal(1L, status.SnapshotCount);
        Assert.Equal(MetricHistoryStore.SchemaVersion, status.SchemaVersion);
    }

    [Fact]
    public void RecordRun_corrupt_file_is_reactively_renamed_aside_and_the_snapshot_still_lands()
    {
        File.WriteAllText(_dbPath, "this is not a sqlite database at all, just garbage bytes");

        var result = MetricHistoryStore.RecordRun(
            _dbPath, Snapshot("report", revision: 1, metrics: ("symbol_count", 42, null)), () => ("art-1", 1));

        Assert.Equal(MetricHistoryWriteResult.Recorded, result);

        var corruptSiblings = Directory.EnumerateFiles(_dir, MetricHistoryStore.HistoryDbFileName + ".corrupt-*").ToArray();
        Assert.Single(corruptSiblings);

        using (var c = OpenRead())
        {
            Assert.Equal(1L, Convert.ToInt64(Scalar(c, "SELECT COUNT(*) FROM snapshots;"), CultureInfo.InvariantCulture));
            Assert.Equal(42.0, Convert.ToDouble(
                Scalar(c, "SELECT value FROM snapshot_metrics WHERE metric='symbol_count';"), CultureInfo.InvariantCulture));
        }

        var status = MetricHistoryStore.ReadStatus(_dbPath);
        Assert.True(status.Present);
        Assert.True(status.CorruptRecovered);
        Assert.Equal(1L, status.SnapshotCount);
    }

    [Fact]
    public void RenameAside_preserves_wal_resident_committed_data_under_one_stamp_and_deletes_nothing()
    {
        // The load-bearing recovery case: a valid-header WAL-mode db whose body is corrupted, with committed frames
        // still RESIDENT in the -wal (uncheckpointed). SQLite keeps such a -wal on the failing connection's close, so
        // RenameAside must MOVE the whole bundle aside — never delete — or committed, non-derivable history is lost.
        // (A header-less garbage file is a different case: SQLite deletes its orphan -wal on close before recovery can
        // run, and a wal with no anchoring db is unrecoverable anyway — see RenameAside_no_siblings below.)
        MetricHistoryStore.RecordConverge(_dbPath, Snapshot("converge", revision: 1, metrics: ("symbol_count", 100, null)));

        // Hold a second connection open with autocheckpoint disabled so extra committed frames stay in the -wal (a
        // lone connection would checkpoint-and-truncate the -wal on close). SQLite opens with FILE_SHARE_DELETE, so
        // holding this handle still permits RenameAside's rename cross-platform.
        using (var pin = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString()))
        {
            pin.Open();
            ExecOn(pin, "PRAGMA wal_autocheckpoint=0;");
            ExecOn(pin, "INSERT INTO snapshots(recorded_at_utc,workspace_id,artifact_id,revision,extractor_version," +
                        "miller_version,source) VALUES('t','ws-1','art-2',2,'x','y','converge');");
            Assert.True(new FileInfo(_dbPath + "-wal").Length > 0, "precondition: committed frames resident in -wal");

            // Corrupt the MAIN db body while keeping the 'SQLite format 3\0' header magic intact so SQLite still
            // recognizes it as a WAL-mode database (and thus does NOT treat the -wal as a deletable orphan).
            CorruptSqliteBodyPreservingHeader(_dbPath);

            // Trigger the reactive corruption recovery.
            var result = MetricHistoryStore.RecordConverge(
                _dbPath, Snapshot("converge", revision: 3, artifactId: "art-3", metrics: ("symbol_count", 200, null)));
            Assert.Equal(MetricHistoryWriteResult.Recorded, result);
        }

        string[] corruptMain = Directory.EnumerateFiles(_dir, MetricHistoryStore.HistoryDbFileName + ".corrupt-*")
            .Where(p => !p.EndsWith("-wal", StringComparison.Ordinal) && !p.EndsWith("-shm", StringComparison.Ordinal))
            .ToArray();
        Assert.Single(corruptMain);
        string stamped = corruptMain[0];

        // WAL-resident committed data preserved under the SAME stamp (SQLite-replayable naming), never deleted.
        Assert.True(File.Exists(stamped + "-wal"), "corrupt -wal (committed frames) must be preserved, never deleted");
        Assert.True(new FileInfo(stamped + "-wal").Length > 0);
        Assert.True(File.Exists(stamped + "-shm"), "corrupt -shm must be preserved, never deleted");

        // And the current snapshot still landed in a fresh DB.
        using var c = OpenRead();
        Assert.Equal(1L, Convert.ToInt64(Scalar(c, "SELECT COUNT(*) FROM snapshots;"), CultureInfo.InvariantCulture));
    }

    [Fact]
    public void RenameAside_no_siblings_is_fine_main_still_moves()
    {
        // No -wal/-shm present. The main file must still move aside and the snapshot land.
        File.WriteAllText(_dbPath, "garbage, not a database");

        var result = MetricHistoryStore.RecordRun(
            _dbPath, Snapshot("report", revision: 1, metrics: ("symbol_count", 5, null)), () => ("art-1", 1));
        Assert.Equal(MetricHistoryWriteResult.Recorded, result);

        string[] corruptMain = Directory.EnumerateFiles(_dir, MetricHistoryStore.HistoryDbFileName + ".corrupt-*")
            .Where(p => !p.EndsWith("-wal", StringComparison.Ordinal) && !p.EndsWith("-shm", StringComparison.Ordinal))
            .ToArray();
        Assert.Single(corruptMain);
    }

    private static void ExecOn(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void CorruptSqliteBodyPreservingHeader(string path)
    {
        // Match SQLite's sharing expectations on Windows while the pin connection keeps WAL frames resident.
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        byte[] bytes = new byte[checked((int)fs.Length)];
        fs.ReadExactly(bytes);

        for (int i = 200; i < Math.Min(bytes.Length, 8000); i++)
            bytes[i] = 0xEE;

        fs.Position = 0;
        fs.Write(bytes);
        fs.SetLength(bytes.Length);
        fs.Flush(flushToDisk: true);
    }

    // ---- present-but-unreadable (fails visibly, never a silent empty) ----------------------------------------

    [Fact]
    public void ReadTrend_present_but_unreadable_throws_typed_exception()
    {
        // A PRESENT-but-corrupt file must NOT degrade to an empty trend — it throws so the CLI/dashboard fail visibly.
        File.WriteAllText(_dbPath, "this is not a sqlite database, just garbage");

        Assert.Throws<MetricHistoryUnreadableException>(() =>
            MetricHistoryStore.ReadTrend(_dbPath, new[] { "symbol_count" }, limit: 20, maxPoints: 0));
    }

    [Fact]
    public void ReadTrend_absent_file_is_empty_success()
    {
        // No file at all is a normal fresh workspace — empty-success, never the unreadable exception.
        var trend = MetricHistoryStore.ReadTrend(_dbPath, new[] { "symbol_count" }, limit: 20, maxPoints: 0);
        Assert.Empty(trend);
    }

    [Fact]
    public void ReadStatus_present_but_unreadable_sets_the_Unreadable_flag()
    {
        File.WriteAllText(_dbPath, "this is not a sqlite database, just garbage");

        var status = MetricHistoryStore.ReadStatus(_dbPath);
        Assert.True(status.Present);
        Assert.True(status.Unreadable);
        // Not a silent healthy-looking schema-0/count-0 default: the flag is what health reads.
        Assert.Equal(0L, status.SnapshotCount);
    }

    // ---- trend ordering + downsampling ----------------------------------------------------------------------

    [Fact]
    public void ReadTrend_orders_by_snapshot_id_even_when_recorded_at_utc_is_out_of_order()
    {
        // Insert in revision order (snapshot_id ascending) but with DESCENDING wall-clock timestamps.
        for (int rev = 1; rev <= 5; rev++)
        {
            var ts = new DateTime(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc).AddDays(-rev);
            Assert.Equal(MetricHistoryWriteResult.Recorded, MetricHistoryStore.RecordConverge(
                _dbPath, Snapshot("converge", revision: rev, metrics: ("symbol_count", rev * 10, null)), ts));
        }

        var trend = MetricHistoryStore.ReadTrend(_dbPath, new[] { "symbol_count" }, limit: 100, maxPoints: 100);

        Assert.Equal(5, trend.Count);
        // Ordered by snapshot_id (insertion order), NOT by recorded_at_utc.
        Assert.Equal(new[] { 10.0, 20.0, 30.0, 40.0, 50.0 }, trend.Select(p => p.Value).ToArray());
        Assert.True(trend.Select(p => p.SnapshotId).SequenceEqual(trend.Select(p => p.SnapshotId).OrderBy(x => x)));
    }

    [Fact]
    public void ReadTrend_downsamples_to_maxPoints_by_uniform_stride()
    {
        for (int rev = 1; rev <= 9; rev++)
            Assert.Equal(MetricHistoryWriteResult.Recorded, MetricHistoryStore.RecordConverge(
                _dbPath, Snapshot("converge", revision: rev, metrics: ("symbol_count", rev, null))));

        var trend = MetricHistoryStore.ReadTrend(_dbPath, new[] { "symbol_count" }, limit: 100, maxPoints: 3);

        Assert.Equal(3, trend.Count);
        // Uniform stride over 9 points (indices 0,4,8) -> values 1,5,9. First and last always included.
        Assert.Equal(new[] { 1.0, 5.0, 9.0 }, trend.Select(p => p.Value).ToArray());
    }

    [Fact]
    public void ReadTrend_respects_limit_to_the_most_recent_snapshots()
    {
        for (int rev = 1; rev <= 5; rev++)
            MetricHistoryStore.RecordConverge(
                _dbPath, Snapshot("converge", revision: rev, metrics: ("symbol_count", rev, null)));

        var trend = MetricHistoryStore.ReadTrend(_dbPath, new[] { "symbol_count" }, limit: 2, maxPoints: 100);

        Assert.Equal(new[] { 4.0, 5.0 }, trend.Select(p => p.Value).ToArray());
    }

    // ---- absent metric ---------------------------------------------------------------------------------------

    [Fact]
    public void Absent_metric_is_an_absent_row_never_zero()
    {
        // symbol_count present; marker_total omitted (region index unavailable).
        MetricHistoryStore.RecordConverge(
            _dbPath, Snapshot("converge", revision: 1, metrics: ("symbol_count", 100, null)));

        var trend = MetricHistoryStore.ReadTrend(
            _dbPath, new[] { "symbol_count", "marker_total" }, limit: 100, maxPoints: 100);

        Assert.Single(trend);
        Assert.Equal("symbol_count", trend[0].Metric);
        Assert.DoesNotContain(trend, p => p.Metric == "marker_total");
    }

    // ---- status ----------------------------------------------------------------------------------------------

    [Fact]
    public void ReadStatus_reports_absent_when_no_file_exists()
    {
        var status = MetricHistoryStore.ReadStatus(_dbPath);
        Assert.False(status.Present);
        Assert.False(status.CorruptRecovered);
        Assert.Equal(0, status.SchemaVersion);
        Assert.Equal(0L, status.SnapshotCount);
    }

    [Fact]
    public void ReadStatus_reports_present_with_schema_and_count()
    {
        MetricHistoryStore.RecordConverge(_dbPath, Snapshot("converge", 1, metrics: ("symbol_count", 100, null)));
        MetricHistoryStore.RecordRun(_dbPath, Snapshot("report", 1, metrics: ("symbol_count", 100, null)), () => ("art-1", 1));

        var status = MetricHistoryStore.ReadStatus(_dbPath);
        Assert.True(status.Present);
        Assert.Equal(MetricHistoryStore.SchemaVersion, status.SchemaVersion);
        Assert.Equal(2L, status.SnapshotCount);
        Assert.True(status.SizeBytes > 0);
        Assert.False(status.CorruptRecovered);
        Assert.False(status.Unreadable);
    }

    [Fact]
    public void DetailJson_round_trips_and_metrics_read_back()
    {
        MetricHistoryStore.RecordRun(
            _dbPath,
            Snapshot("candidates", 1, metrics: ("dead_code_candidate_count", 5, "{\"range\":\"default\"}")),
            () => ("art-1", 1));

        using var c = OpenRead();
        Assert.Equal("{\"range\":\"default\"}", Convert.ToString(
            Scalar(c, "SELECT detail_json FROM snapshot_metrics WHERE metric='dead_code_candidate_count';"),
            CultureInfo.InvariantCulture));
    }
}
