using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Server;
using Miller.Server.Telemetry;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the telemetry ledger (M2 §6): the STRICT append-only <c>tool_telemetry</c> table is created on open;
/// a <see cref="TelemetryLedger.Measure"/> scope writes exactly one row with the right outcome
/// (ok/empty/error), a non-negative duration, est_tokens, and a <c>target_hash</c> (the SHA256, never the raw
/// query); <see cref="TelemetryLedger.Record"/> never throws on a bad row; and <c>Prune</c> deletes old rows.
/// The ledger writes to its OWN <c>telemetry.db</c> (never the Mode=ReadOnly extract DB).
/// </summary>
public sealed class TelemetryLedgerTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public TelemetryLedgerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-telemetry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "telemetry.db");
    }

    public void Dispose()
    {
        // No SqliteConnection.ClearAllPools() here: it is process-global and would race a concurrently
        // running test's live connection (xUnit parallelizes collections). All connections below are
        // Pooling=false, so they leave no pool entry and the temp file is released on Dispose anyway.
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static string ReadOnlyUnpooled(string dbPath) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
        }.ToString();

    private static string Sha256Hex(string raw) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

    private static string ExplainPlan(SqliteConnection connection, string sql, params (string, object?)[] parameters)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        using var reader = cmd.ExecuteReader();
        var details = new List<string>();
        while (reader.Read())
            details.Add(reader.GetString(3));
        return string.Join('\n', details);
    }

    private static void AssertPlanUsesIndex(
        SqliteConnection connection,
        string sql,
        string indexName,
        params (string, object?)[] parameters)
    {
        string plan = ExplainPlan(connection, sql, parameters);
        Assert.Contains(indexName, plan, StringComparison.Ordinal);
    }

    private static bool IndexExists(SqliteConnection connection, string indexName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT 1
            FROM sqlite_master
            WHERE type = 'index' AND name = $name
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$name", indexName);
        return cmd.ExecuteScalar() is not null;
    }

    private int RowCount()
    {
        using var c = new SqliteConnection(ReadOnlyUnpooled(_dbPath));
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM tool_telemetry;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private (
        string tool,
        string outcome,
        long duration,
        long? estTokens,
        string? hash,
        int? resultCount,
        string? workspaceId,
        string? workspaceRoot) ReadOnlyRow()
    {
        using var c = new SqliteConnection(ReadOnlyUnpooled(_dbPath));
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText =
            """
            SELECT tool, outcome, duration_ms, est_tokens, target_hash, result_count, workspace_id, workspace_root
            FROM tool_telemetry LIMIT 1;
            """;
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        return (
            r.GetString(0),
            r.GetString(1),
            r.GetInt64(2),
            r.IsDBNull(3) ? null : r.GetInt64(3),
            r.IsDBNull(4) ? null : r.GetString(4),
            r.IsDBNull(5) ? null : r.GetInt32(5),
            r.IsDBNull(6) ? null : r.GetString(6),
            r.IsDBNull(7) ? null : r.GetString(7));
    }

    private static int ColumnCount(SqliteConnection connection, string columnName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('tool_telemetry') WHERE name = $name;";
        cmd.Parameters.AddWithValue("$name", columnName);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private List<string?> ReadMillerVersions()
    {
        using var c = new SqliteConnection(ReadOnlyUnpooled(_dbPath));
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT miller_version FROM tool_telemetry ORDER BY id;";
        var versions = new List<string?>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            versions.Add(r.IsDBNull(0) ? null : r.GetString(0));
        return versions;
    }

    [Fact]
    public void Open_AddsMillerVersionColumn_ExactlyOnce_AcrossRepeatedOpens()
    {
        using (TelemetryLedger.Open(_dbPath, workspaceId: "ws1"))
        {
        }
        using (TelemetryLedger.Open(_dbPath, workspaceId: "ws1"))
        {
        }

        using var c = new SqliteConnection(ReadOnlyUnpooled(_dbPath));
        c.Open();
        Assert.Equal(1, ColumnCount(c, "miller_version"));
    }

    private void SeedTableWithoutMillerVersion()
    {
        using var seed = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false,
            }.ToString());
        seed.Open();
        using var ddl = seed.CreateCommand();
        ddl.CommandText = """
            CREATE TABLE tool_telemetry (
                id TEXT PRIMARY KEY, ts TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                tool TEXT NOT NULL, op TEXT, workspace_id TEXT, workspace_root TEXT,
                duration_ms INTEGER NOT NULL CHECK (duration_ms >= 0),
                outcome TEXT NOT NULL CHECK (outcome IN ('ok','empty','error')), error_kind TEXT,
                result_count INTEGER,
                bytes_examined INTEGER NOT NULL DEFAULT 0 CHECK (bytes_examined >= 0),
                bytes_returned INTEGER NOT NULL DEFAULT 0 CHECK (bytes_returned >= 0),
                source_bytes  INTEGER NOT NULL DEFAULT 0 CHECK (source_bytes >= 0),
                est_tokens INTEGER, index_fresh INTEGER CHECK (index_fresh IS NULL OR index_fresh IN (0,1)),
                target_hash TEXT, metadata_json TEXT NOT NULL DEFAULT '{}'
            ) STRICT;
            """;
        ddl.ExecuteNonQuery();
    }

    [Fact]
    public void Open_MigratesAPreExistingTableThatLacksMillerVersion()
    {
        SeedTableWithoutMillerVersion();

        using (var ledger = TelemetryLedger.Open(_dbPath, workspaceId: "ws1"))
        {
            using var scope = ledger.Measure("search", op: "auto");
            scope.Outcome = TelemetryOutcome.Ok;
        }

        Assert.Equal(MillerVersion.Current, Assert.Single(ReadMillerVersions()));
    }

    [Fact]
    public void AddTextColumn_ToleratesAColumnAnotherProcessAlreadyAdded()
    {
        // Stands in for the TOCTOU window: the pragma guard saw no column, another Miller process added it,
        // and this ALTER arrives second. The second call must be a no-op, not a throw.
        SeedTableWithoutMillerVersion();

        using var c = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
            }.ToString());
        c.Open();

        TelemetryLedger.AddTextColumnToleratingConcurrentAdder(c, "miller_version");
        TelemetryLedger.AddTextColumnToleratingConcurrentAdder(c, "miller_version");

        Assert.Equal(1, ColumnCount(c, "miller_version"));
    }

    [Fact]
    public void Open_FromManyProcessesConcurrently_LeavesExactlyOneMillerVersionColumn()
    {
        SeedTableWithoutMillerVersion();

        using var barrier = new Barrier(8);
        var failures = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        Parallel.For(0, 8, _ =>
        {
            try
            {
                barrier.SignalAndWait();
                using var ledger = TelemetryLedger.Open(_dbPath, workspaceId: "ws1");
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        });

        Assert.Empty(failures);
        using var c = new SqliteConnection(ReadOnlyUnpooled(_dbPath));
        c.Open();
        Assert.Equal(1, ColumnCount(c, "miller_version"));
    }

    [Fact]
    public void Record_StampsTheRunningMillerVersion()
    {
        using (var ledger = TelemetryLedger.Open(_dbPath, workspaceId: "ws1"))
        {
            var record = new TelemetryRecord(
                Tool: "search", Op: "auto", WorkspaceId: "ws1", WorkspaceRoot: null,
                DurationMs: 1, Outcome: "ok", ErrorKind: null,
                ResultCount: null, BytesExamined: 0, BytesReturned: 0, SourceBytes: 0,
                EstTokens: null, IndexFresh: null, TargetHash: null, MetadataJson: "{}");
            ledger.Record(in record, id: "row-1");

            using var scope = ledger.Measure("inspect", op: null);
            scope.Outcome = TelemetryOutcome.Ok;
        }

        var versions = ReadMillerVersions();
        Assert.Equal(2, versions.Count);
        Assert.All(versions, v => Assert.Equal(MillerVersion.Current, v));
    }

    [Fact]
    public void Record_StampsAVersionStringOnly_NotQueryTextOrPaths()
    {
        string secretRoot = Path.Combine(_dir, "secret-workspace");
        using (var ledger = TelemetryLedger.Open(_dbPath, workspaceId: "ws1", secretRoot))
        {
            using var scope = ledger.Measure("search", op: "auto");
            scope.SetTarget("SELECT * FROM secrets");
            scope.Outcome = TelemetryOutcome.Ok;
        }

        string version = Assert.Single(ReadMillerVersions())!;
        Assert.Equal(MillerVersion.Current, version);
        Assert.DoesNotContain("secret", version, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, version);
    }

    [Fact]
    public void OlderWriterInsert_NamingTheLegacyColumnList_StillSucceeds_AfterMigration()
    {
        using var ledger = TelemetryLedger.Open(_dbPath, workspaceId: "ws1");

        using var write = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
            }.ToString());
        write.Open();
        using var cmd = write.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tool_telemetry
                (id, tool, op, workspace_id, workspace_root, duration_ms, outcome, error_kind, result_count,
                 error_message, error_detail, bytes_examined, bytes_returned, source_bytes, est_tokens, index_fresh,
                 target_hash, metadata_json)
            VALUES
                ('legacy-row', 'search', 'auto', 'ws1', NULL, 3, 'ok', NULL, NULL,
                 NULL, NULL, 0, 0, 0, NULL, NULL, NULL, '{}');
            """;
        Assert.Equal(1, cmd.ExecuteNonQuery());

        Assert.Null(Assert.Single(ReadMillerVersions()));
    }

    [Fact]
    public void Open_CreatesTheTable()
    {
        using var ledger = TelemetryLedger.Open(_dbPath, workspaceId: "ws1");
        Assert.Equal(0, RowCount());
    }

    [Fact]
    public void Open_CreatesDashboardQueryIndexes()
    {
        using (TelemetryLedger.Open(_dbPath, workspaceId: "ws1"))
        {
        }

        using var c = new SqliteConnection(ReadOnlyUnpooled(_dbPath));
        c.Open();
        AssertPlanUsesIndex(
            c,
            "SELECT id FROM tool_telemetry ORDER BY ts DESC, id DESC LIMIT 20;",
            "idx_tool_telemetry_ts_id");
        AssertPlanUsesIndex(
            c,
            "SELECT id FROM tool_telemetry WHERE workspace_id IS $ws ORDER BY ts DESC, id DESC LIMIT 20;",
            "idx_tool_telemetry_ws_ts_id",
            ("$ws", "ws1"));
        AssertPlanUsesIndex(
            c,
            "SELECT id FROM tool_telemetry WHERE outcome = 'error' ORDER BY ts DESC, id DESC LIMIT 8;",
            "idx_tool_telemetry_outcome_ts_id");
        AssertPlanUsesIndex(
            c,
            "SELECT id FROM tool_telemetry WHERE workspace_id IS $ws AND outcome = 'error' ORDER BY ts DESC, id DESC LIMIT 8;",
            "idx_tool_telemetry_ws_outcome_ts_id",
            ("$ws", "ws1"));
        AssertPlanUsesIndex(
            c,
            "SELECT outcome FROM tool_telemetry WHERE tool = $tool ORDER BY ts DESC, id DESC LIMIT 1;",
            "idx_tool_telemetry_tool_ts_id",
            ("$tool", "search"));
        AssertPlanUsesIndex(
            c,
            "SELECT outcome FROM tool_telemetry WHERE workspace_id IS $ws AND tool = $tool ORDER BY ts DESC, id DESC LIMIT 1;",
            "idx_tool_telemetry_ws_tool_ts_id",
            ("$ws", "ws1"),
            ("$tool", "search"));
        AssertPlanUsesIndex(
            c,
            "SELECT duration_ms FROM tool_telemetry WHERE tool = $tool ORDER BY duration_ms ASC LIMIT 1;",
            "idx_tool_telemetry_tool_duration",
            ("$tool", "search"));
        AssertPlanUsesIndex(
            c,
            "SELECT duration_ms FROM tool_telemetry WHERE workspace_id IS $ws AND tool = $tool ORDER BY duration_ms ASC LIMIT 1;",
            "idx_tool_telemetry_ws_tool_duration",
            ("$ws", "ws1"),
            ("$tool", "search"));
    }

    [Fact]
    public void Open_DropsSupersededSingleColumnIndexes()
    {
        using (TelemetryLedger.Open(_dbPath, workspaceId: "ws1"))
        {
        }

        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString()))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                CREATE INDEX idx_tool_telemetry_ts ON tool_telemetry(ts);
                CREATE INDEX idx_tool_telemetry_tool ON tool_telemetry(tool);
                CREATE INDEX idx_tool_telemetry_ws ON tool_telemetry(workspace_id);
                """;
            cmd.ExecuteNonQuery();
        }

        using (TelemetryLedger.Open(_dbPath, workspaceId: "ws1"))
        {
        }

        using var c = new SqliteConnection(ReadOnlyUnpooled(_dbPath));
        c.Open();
        Assert.False(IndexExists(c, "idx_tool_telemetry_ts"));
        Assert.False(IndexExists(c, "idx_tool_telemetry_tool"));
        Assert.False(IndexExists(c, "idx_tool_telemetry_ws"));
    }

    [Fact]
    public void Measure_WritesOneRow_WithOkOutcome_AndHashedTarget()
    {
        const string target = "retry handler";
        using (var ledger = TelemetryLedger.Open(_dbPath, workspaceId: "ws1"))
        {
            using var scope = ledger.Measure("search", op: "auto");
            scope.ResultCount = 3;
            scope.BytesReturned = 120;
            scope.EstTokens = 30;
            scope.IndexFresh = true;
            scope.SetTarget(target); // hashed, not stored raw
            scope.Outcome = TelemetryOutcome.Ok;
        }

        Assert.Equal(1, RowCount());
        var row = ReadOnlyRow();
        Assert.Equal("search", row.tool);
        Assert.Equal("ok", row.outcome);
        Assert.True(row.duration >= 0);
        Assert.Equal(30, row.estTokens);
        Assert.Equal(3, row.resultCount);
        Assert.NotNull(row.hash);
        Assert.Equal(Sha256Hex(target), row.hash);
        Assert.DoesNotContain("retry", row.hash, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Measure_WithoutWorkspaceOverride_UsesLedgerWorkspaceAndRoot()
    {
        string workspaceRoot = Path.Combine(_dir, "current-workspace");
        using (var ledger = TelemetryLedger.Open(_dbPath, workspaceId: "current-ws", workspaceRoot))
        {
            using var scope = ledger.Measure("search", op: "auto");
            scope.Outcome = TelemetryOutcome.Ok;
        }

        var row = ReadOnlyRow();
        Assert.Equal("current-ws", row.workspaceId);
        Assert.Equal(workspaceRoot, row.workspaceRoot);
    }

    [Fact]
    public void Measure_WithWorkspaceOverride_UsesTargetWorkspaceAndRoot_AndStillHashesTarget()
    {
        const string target = "workspace override query";
        string currentRoot = Path.Combine(_dir, "current-workspace");
        string targetRoot = Path.Combine(_dir, "target-workspace");
        using (var ledger = TelemetryLedger.Open(_dbPath, workspaceId: "current-ws", currentRoot))
        {
            using var scope = ledger.Measure("search", op: "auto");
            scope.SetWorkspace("target-ws", targetRoot);
            scope.SetTarget(target);
            scope.Outcome = TelemetryOutcome.Ok;
        }

        var row = ReadOnlyRow();
        Assert.Equal("target-ws", row.workspaceId);
        Assert.Equal(targetRoot, row.workspaceRoot);
        Assert.NotNull(row.hash);
        Assert.Equal(Sha256Hex(target), row.hash);
        Assert.DoesNotContain(target, row.hash, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Record_UsesRecordWorkspaceRoot_WhenProvided_AndFallsBackToLedgerRoot_WhenMissing()
    {
        string ledgerRoot = Path.Combine(_dir, "ledger-workspace");
        string targetRoot = Path.Combine(_dir, "target-workspace");
        using (var ledger = TelemetryLedger.Open(_dbPath, workspaceId: "ledger-ws", ledgerRoot))
        {
            var target = new TelemetryRecord(
                Tool: "search", Op: "auto", WorkspaceId: "target-ws", WorkspaceRoot: targetRoot,
                DurationMs: 1, Outcome: "ok", ErrorKind: null,
                ResultCount: null, BytesExamined: 0, BytesReturned: 0, SourceBytes: 0,
                EstTokens: null, IndexFresh: null, TargetHash: null, MetadataJson: "{}");
            ledger.Record(in target, id: "target-row");

            var fallback = new TelemetryRecord(
                Tool: "inspect", Op: "summary", WorkspaceId: "legacy-ws", WorkspaceRoot: null,
                DurationMs: 1, Outcome: "ok", ErrorKind: null,
                ResultCount: null, BytesExamined: 0, BytesReturned: 0, SourceBytes: 0,
                EstTokens: null, IndexFresh: null, TargetHash: null, MetadataJson: "{}");
            ledger.Record(in fallback, id: "fallback-row");
        }

        using var c = new SqliteConnection(ReadOnlyUnpooled(_dbPath));
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, workspace_id, workspace_root
            FROM tool_telemetry
            ORDER BY id;
            """;
        var rows = new List<(string id, string? workspaceId, string? workspaceRoot)>();
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                rows.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)));

        Assert.Equal(("fallback-row", "legacy-ws", ledgerRoot), Assert.Single(rows, row => row.id == "fallback-row"));
        Assert.Equal(("target-row", "target-ws", targetRoot), Assert.Single(rows, row => row.id == "target-row"));
    }

    [Theory]
    [InlineData(TelemetryOutcome.Ok, "ok")]
    [InlineData(TelemetryOutcome.Empty, "empty")]
    [InlineData(TelemetryOutcome.Error, "error")]
    public void Measure_PersistsEachOutcome(TelemetryOutcome outcome, string expected)
    {
        using (var ledger = TelemetryLedger.Open(_dbPath, workspaceId: "ws1"))
        {
            using var scope = ledger.Measure("inspect", op: null);
            scope.Outcome = outcome;
        }
        Assert.Equal(expected, ReadOnlyRow().outcome);
    }

    [Fact]
    public void Measure_EmptyIsDistinctFromOk()
    {
        // The empty outcome (zero results) must be recorded as 'empty', not collapsed into 'ok'.
        using (var ledger = TelemetryLedger.Open(_dbPath, workspaceId: "ws1"))
        {
            using var scope = ledger.Measure("search", op: "auto");
            scope.ResultCount = 0;
            scope.Outcome = TelemetryOutcome.Empty;
        }
        Assert.Equal("empty", ReadOnlyRow().outcome);
    }

    [Fact]
    public void Measure_SetError_PersistsMessageAndCopyableDetail()
    {
        using (var ledger = TelemetryLedger.Open(_dbPath, workspaceId: "ws1"))
        {
            using var scope = ledger.Measure("inspect", op: null);
            try
            {
                ThrowKnownFailure();
            }
            catch (Exception ex)
            {
                scope.Outcome = TelemetryOutcome.Error;
                scope.SetError(ex);
            }
        }

        using var c = new SqliteConnection(ReadOnlyUnpooled(_dbPath));
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT error_kind, error_message, error_detail FROM tool_telemetry LIMIT 1;";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal("InvalidOperationException", r.GetString(0));
        Assert.Equal("known ledger failure", r.GetString(1));
        string detail = r.GetString(2);
        Assert.Contains("System.InvalidOperationException: known ledger failure", detail);
        Assert.Contains(nameof(ThrowKnownFailure), detail);

        static void ThrowKnownFailure() => throw new InvalidOperationException("known ledger failure");
    }

    [Fact]
    public void Measure_MergesStructuredMetadata_WithoutRawTarget()
    {
        using (var ledger = TelemetryLedger.Open(_dbPath, workspaceId: "ws1"))
        {
            using var scope = ledger.Measure("search", op: null);
            scope.SetTarget("raw secret query");
            scope.SetMetadata("route", "symbols");
            scope.SetMetadata("has_regions", true);
            scope.SetMetadata("limit_bucket", "11-25");
            scope.SetEmptyReason("no_symbol_hits");
            scope.Outcome = TelemetryOutcome.Empty;
        }

        using var c = new SqliteConnection(ReadOnlyUnpooled(_dbPath));
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT metadata_json FROM tool_telemetry LIMIT 1;";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        string metadata = r.GetString(0);
        using JsonDocument doc = JsonDocument.Parse(metadata);
        Assert.Equal("symbols", doc.RootElement.GetProperty("route").GetString());
        Assert.True(doc.RootElement.GetProperty("has_regions").GetBoolean());
        Assert.Equal("11-25", doc.RootElement.GetProperty("limit_bucket").GetString());
        Assert.Equal("no_symbol_hits", doc.RootElement.GetProperty("empty_reason").GetString());
        Assert.DoesNotContain("raw secret query", metadata, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Record_NeverThrows_OnABadRow_AndCountsTheDrop()
    {
        using var ledger = TelemetryLedger.Open(_dbPath, workspaceId: "ws1");

        // A negative duration violates the CHECK (duration_ms >= 0). Record must swallow + count, not throw.
        var bad = new TelemetryRecord(
            Tool: "search", Op: null, WorkspaceId: "ws1", WorkspaceRoot: null,
            DurationMs: -5, Outcome: "ok", ErrorKind: null,
            ResultCount: null, BytesExamined: 0, BytesReturned: 0, SourceBytes: 0,
            EstTokens: null, IndexFresh: null, TargetHash: null, MetadataJson: "{}");

        var ex = Record.Exception(() => ledger.Record(in bad));
        Assert.Null(ex);
        Assert.True(ledger.DroppedWrites >= 1);
        Assert.Equal(0, RowCount());
    }

    [Fact]
    public void Measure_IsAppendOnly_AccumulatingRows()
    {
        using (var ledger = TelemetryLedger.Open(_dbPath, workspaceId: "ws1"))
        {
            for (int i = 0; i < 5; i++)
            {
                using var scope = ledger.Measure("search", op: "auto");
                scope.Outcome = TelemetryOutcome.Ok;
            }
        }
        Assert.Equal(5, RowCount());
    }

    [Fact]
    public void Prune_DeletesRowsOlderThanRetention()
    {
        using var ledger = TelemetryLedger.Open(_dbPath, workspaceId: "ws1");

        // Insert one old row (40 days ago) and one fresh row directly via the ledger's connection helper.
        ledger.InsertRawForTest(
            id: Guid.CreateVersion7().ToString(), tsUtc: DateTime.UtcNow.AddDays(-40), tool: "search");
        ledger.InsertRawForTest(
            id: Guid.CreateVersion7().ToString(), tsUtc: DateTime.UtcNow, tool: "search");
        Assert.Equal(2, RowCount());

        int deleted = ledger.Prune(retentionDays: 30);

        Assert.Equal(1, deleted);
        Assert.Equal(1, RowCount());
    }
}
