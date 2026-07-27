using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Miller.Server.Telemetry;

/// <summary>
/// The append-only metrics ledger (M2 §6 / miller-toolbox.md L193-229). Owns ONE writable
/// <c>Mode=ReadWriteCreate</c> connection to <c>&lt;root&gt;/.miller/telemetry.db</c> — a SEPARATE,
/// Miller-owned DB, NEVER the Mode=ReadOnly julie extract (which <c>scan --force</c> recreates). Sets WAL +
/// NORMAL sync + a busy timeout on connect (these pragmas are per-connection), creates the STRICT
/// <c>tool_telemetry</c> table, and reuses a prepared INSERT. <see cref="Record"/> is best-effort and NEVER
/// throws — a telemetry write failure must never break a tool call; it is swallowed and counted in
/// <see cref="DroppedWrites"/>. Registered as a DI singleton; the single connection is guarded by a lock
/// because MCP tool calls can run concurrently. Every row is stamped with <see cref="MillerVersion.Current"/>
/// in the nullable <c>miller_version</c> column so cohorts can be attributed to a binary version; the column is
/// added additively, and rows from older binaries (which name their columns explicitly) stay NULL.
/// </summary>
public sealed class TelemetryLedger : IDisposable
{
    private const string CreateTableDdl = """
        CREATE TABLE IF NOT EXISTS tool_telemetry (
            id TEXT PRIMARY KEY, ts TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
            tool TEXT NOT NULL, op TEXT, workspace_id TEXT, workspace_root TEXT,
            duration_ms INTEGER NOT NULL CHECK (duration_ms >= 0),
            outcome TEXT NOT NULL CHECK (outcome IN ('ok','empty','error')), error_kind TEXT,
            error_message TEXT, error_detail TEXT,
            result_count INTEGER,
            bytes_examined INTEGER NOT NULL DEFAULT 0 CHECK (bytes_examined >= 0),
            bytes_returned INTEGER NOT NULL DEFAULT 0 CHECK (bytes_returned >= 0),
            source_bytes  INTEGER NOT NULL DEFAULT 0 CHECK (source_bytes >= 0),
            est_tokens INTEGER, index_fresh INTEGER CHECK (index_fresh IS NULL OR index_fresh IN (0,1)),
            target_hash TEXT, metadata_json TEXT NOT NULL DEFAULT '{}',
            miller_version TEXT
        ) STRICT;
        DROP INDEX IF EXISTS idx_tool_telemetry_ts;
        DROP INDEX IF EXISTS idx_tool_telemetry_tool;
        DROP INDEX IF EXISTS idx_tool_telemetry_ws;
        CREATE INDEX IF NOT EXISTS idx_tool_telemetry_ts_id ON tool_telemetry(ts DESC, id DESC);
        CREATE INDEX IF NOT EXISTS idx_tool_telemetry_ws_ts_id ON tool_telemetry(workspace_id, ts DESC, id DESC);
        CREATE INDEX IF NOT EXISTS idx_tool_telemetry_outcome_ts_id ON tool_telemetry(outcome, ts DESC, id DESC);
        CREATE INDEX IF NOT EXISTS idx_tool_telemetry_ws_outcome_ts_id ON tool_telemetry(workspace_id, outcome, ts DESC, id DESC);
        CREATE INDEX IF NOT EXISTS idx_tool_telemetry_tool_ts_id ON tool_telemetry(tool, ts DESC, id DESC);
        CREATE INDEX IF NOT EXISTS idx_tool_telemetry_ws_tool_ts_id ON tool_telemetry(workspace_id, tool, ts DESC, id DESC);
        CREATE INDEX IF NOT EXISTS idx_tool_telemetry_tool_duration ON tool_telemetry(tool, duration_ms);
        CREATE INDEX IF NOT EXISTS idx_tool_telemetry_ws_tool_duration ON tool_telemetry(workspace_id, tool, duration_ms);
        CREATE TABLE IF NOT EXISTS telemetry_drops (
            process_id TEXT PRIMARY KEY,
            dropped_writes INTEGER NOT NULL CHECK (dropped_writes >= 0),
            recorded_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
        ) STRICT;
        """;

    private readonly object _gate = new();
    private readonly SqliteConnection _connection;
    private readonly SqliteCommand _insert;
    private readonly TimeProvider _clock;

    // Identifies this ledger instance's drop tally, so concurrent hosts sharing the file accumulate rather than
    // overwrite each other. Not the OS pid: a reused pid would silently reset another host's count.
    private readonly string _processId = Guid.CreateVersion7().ToString();
    private string? _workspaceId;
    private string? _workspaceRoot;
    private bool _disposed;

    /// <summary>The clock every scope this ledger opens reads its call-start instant from.</summary>
    public TimeProvider Clock => _clock;

    /// <summary>The workspace id stamped onto every row, or null if unknown at open time.</summary>
    public string? WorkspaceId
    {
        get
        {
            lock (_gate)
                return _workspaceId;
        }
    }

    /// <summary>The shared telemetry database path opened by this ledger.</summary>
    public string DbPath { get; }

    /// <summary>Count of telemetry rows that failed to persist and were swallowed (never throws).</summary>
    public long DroppedWrites { get; private set; }

    private TelemetryLedger(
        SqliteConnection connection, string dbPath, string? workspaceId, string? workspaceRoot, TimeProvider clock)
    {
        _connection = connection;
        DbPath = dbPath;
        _workspaceId = workspaceId;
        _workspaceRoot = workspaceRoot;
        _clock = clock;

        _insert = _connection.CreateCommand();
        // ts falls back to the column DEFAULT (strftime now) when $ts is null, so a direct caller that carries no
        // scope instant keeps the historic behavior while a scope writes its captured call-start instant verbatim.
        _insert.CommandText = """
            INSERT INTO tool_telemetry
                (id, ts, tool, op, workspace_id, workspace_root, duration_ms, outcome, error_kind, result_count,
                 error_message, error_detail, bytes_examined, bytes_returned, source_bytes, est_tokens, index_fresh,
                 target_hash, metadata_json, miller_version)
            VALUES
                ($id, COALESCE($ts, strftime('%Y-%m-%dT%H:%M:%fZ','now')), $tool, $op, $ws, $wsroot, $dur, $outcome,
                 $errkind, $rc, $errmsg, $errdetail, $bex, $bret, $src, $est, $fresh, $hash, $meta, $version);
            """;
        // Declare parameters once; values are set per Record() call. Prepared and reused on the hot path.
        _insert.Parameters.Add("$id", SqliteType.Text);
        _insert.Parameters.Add("$ts", SqliteType.Text);
        _insert.Parameters.Add("$tool", SqliteType.Text);
        _insert.Parameters.Add("$op", SqliteType.Text);
        _insert.Parameters.Add("$ws", SqliteType.Text);
        _insert.Parameters.Add("$wsroot", SqliteType.Text);
        _insert.Parameters.Add("$dur", SqliteType.Integer);
        _insert.Parameters.Add("$outcome", SqliteType.Text);
        _insert.Parameters.Add("$errkind", SqliteType.Text);
        _insert.Parameters.Add("$rc", SqliteType.Integer);
        _insert.Parameters.Add("$errmsg", SqliteType.Text);
        _insert.Parameters.Add("$errdetail", SqliteType.Text);
        _insert.Parameters.Add("$bex", SqliteType.Integer);
        _insert.Parameters.Add("$bret", SqliteType.Integer);
        _insert.Parameters.Add("$src", SqliteType.Integer);
        _insert.Parameters.Add("$est", SqliteType.Integer);
        _insert.Parameters.Add("$fresh", SqliteType.Integer);
        _insert.Parameters.Add("$hash", SqliteType.Text);
        _insert.Parameters.Add("$meta", SqliteType.Text);
        // The running build's version is a process constant, so it is bound once here rather than per write.
        // Every row from this binary is stamped; rows written by older binaries stay NULL.
        _insert.Parameters.Add("$version", SqliteType.Text).Value = MillerVersion.Current;
        _insert.Prepare();
    }

    /// <summary>
    /// Open (creating if needed) the writable telemetry DB at <paramref name="dbPath"/> and ensure the table.
    /// The parent directory must already exist (startup creates <c>&lt;home&gt;/.miller</c>). The DB is
    /// machine-global — every workspace's miller process opens this same file — so each row is stamped with
    /// <paramref name="workspaceId"/> + <paramref name="workspaceRoot"/> and <see cref="Summarize"/> scopes back
    /// to <paramref name="workspaceId"/> so a per-workspace view never reports another workspace's rows.
    /// </summary>
    public static TelemetryLedger Open(
        string dbPath, string? workspaceId, string? workspaceRoot = null, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);

        // Pooling=false: this is ONE long-lived singleton connection held for the process lifetime, so the
        // shared pool buys it nothing — and opting out keeps it isolated from a process-global
        // SqliteConnection.ClearAllPools() (which the test fixtures call to release file handles), which
        // would otherwise dispose this live connection's handle out from under an in-flight write.
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(dbPath),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        connection.Open();

        // WAL pragmas are per-connection; set them before any write. busy_timeout guards the brief windows
        // where a concurrent reader (the future dashboard) holds the DB.
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText =
                "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=3000;";
            pragma.ExecuteNonQuery();
        }
        using (var ddl = connection.CreateCommand())
        {
            ddl.CommandText = CreateTableDdl;
            ddl.ExecuteNonQuery();
        }
        EnsureTextColumn(connection, "error_message");
        EnsureTextColumn(connection, "error_detail");
        EnsureTextColumn(connection, "miller_version");

        return new TelemetryLedger(
            connection, Path.GetFullPath(dbPath), workspaceId, workspaceRoot, clock ?? TimeProvider.System);
    }

    private static void EnsureTextColumn(SqliteConnection connection, string columnName)
    {
        using (var exists = connection.CreateCommand())
        {
            exists.CommandText = "SELECT 1 FROM pragma_table_info('tool_telemetry') WHERE name = $name LIMIT 1;";
            exists.Parameters.AddWithValue("$name", columnName);
            if (exists.ExecuteScalar() is not null)
                return;
        }

        AddTextColumnToleratingConcurrentAdder(connection, columnName);
    }

    /// <summary>
    /// The ALTER half of <see cref="EnsureTextColumn"/>. The pragma guard above it is only a fast path: the
    /// telemetry DB is machine-global, so another Miller process can add the same column between that check and
    /// this statement. A duplicate-column failure means the intended end state already holds, so it is tolerated.
    /// </summary>
    internal static void AddTextColumnToleratingConcurrentAdder(SqliteConnection connection, string columnName)
    {
        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE tool_telemetry ADD COLUMN {columnName} TEXT;";
        try
        {
            alter.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (
            ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
        {
        }
    }

    /// <summary>
    /// Begin measuring a tool call. The returned scope is enriched by the caller/filter and persists one row
    /// on dispose. <paramref name="op"/> is the operation/mode sub-axis (null when the tool has none).
    /// <paramref name="correlationId"/> (M8 decision-2) is reused as the persisted row's <c>id</c> so the ledger
    /// row and the call's log lines share an id; when null the scope self-generates a UUIDv7.
    /// </summary>
    public TelemetryScope Measure(string tool, string? op, string? correlationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);
        return new TelemetryScope(this, tool, op, correlationId, _clock);
    }

    /// <summary>
    /// Update the default workspace stamped on rows after the process rebinds its primary workspace.
    /// Cross-workspace calls may still override these values per record.
    /// </summary>
    public void RebindWorkspace(string? workspaceId, string? workspaceRoot)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _workspaceId = workspaceId;
            _workspaceRoot = workspaceRoot;
        }
    }

    /// <summary>
    /// Persist one row. Best-effort: ANY failure (a CHECK violation, a locked DB, a disposed ledger) is
    /// swallowed and counted in <see cref="DroppedWrites"/>. Telemetry must NEVER break a tool call.
    /// <paramref name="id"/> (M8 decision-2) is the row's primary key; the central filter supplies the call's
    /// correlation id so the row and its log lines share an id. When null/blank — direct callers and tests that
    /// do not correlate — a UUIDv7 is self-generated, preserving the original behavior.
    /// </summary>
    public void Record(in TelemetryRecord record, string? id = null)
    {
        try
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    DroppedWrites++;
                    return;
                }

                _insert.Parameters["$id"].Value =
                    string.IsNullOrWhiteSpace(id) ? Guid.CreateVersion7().ToString() : id;
                _insert.Parameters["$ts"].Value = record.StartedAtUtc is { } startedAt
                    ? startedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)
                    : DBNull.Value;
                _insert.Parameters["$tool"].Value = record.Tool;
                _insert.Parameters["$op"].Value = (object?)record.Op ?? DBNull.Value;
                string? defaultWorkspaceId = _workspaceId;
                string? defaultWorkspaceRoot = _workspaceRoot;

                _insert.Parameters["$ws"].Value = (object?)(record.WorkspaceId ?? defaultWorkspaceId) ?? DBNull.Value;
                // workspace_root normally falls back to the process workspace root. Cross-workspace calls may
                // override it per-record so shared ledger rows attribute reads to the target workspace.
                string? workspaceRoot = string.IsNullOrWhiteSpace(record.WorkspaceRoot)
                    ? defaultWorkspaceRoot
                    : record.WorkspaceRoot;
                _insert.Parameters["$wsroot"].Value = (object?)workspaceRoot ?? DBNull.Value;
                _insert.Parameters["$dur"].Value = record.DurationMs;
                _insert.Parameters["$outcome"].Value = record.Outcome;
                _insert.Parameters["$errkind"].Value = (object?)record.ErrorKind ?? DBNull.Value;
                _insert.Parameters["$rc"].Value = (object?)record.ResultCount ?? DBNull.Value;
                _insert.Parameters["$errmsg"].Value = (object?)record.ErrorMessage ?? DBNull.Value;
                _insert.Parameters["$errdetail"].Value = (object?)record.ErrorDetail ?? DBNull.Value;
                _insert.Parameters["$bex"].Value = record.BytesExamined;
                _insert.Parameters["$bret"].Value = record.BytesReturned;
                _insert.Parameters["$src"].Value = record.SourceBytes;
                _insert.Parameters["$est"].Value = (object?)record.EstTokens ?? DBNull.Value;
                _insert.Parameters["$fresh"].Value =
                    record.IndexFresh is { } f ? (f ? 1 : 0) : DBNull.Value;
                _insert.Parameters["$hash"].Value = (object?)record.TargetHash ?? DBNull.Value;
                _insert.Parameters["$meta"].Value = record.MetadataJson ?? "{}";

                _insert.ExecuteNonQuery();
            }
        }
        catch (Exception)
        {
            // Swallow EVERYTHING — a telemetry failure must not surface to the agent. Count it for the
            // dashboard's drop-rate KPI. (We intentionally do not narrow the catch: the ledger is on the
            // best-effort side of the seam, so any failure mode is treated identically.)
            unchecked { DroppedWrites++; }
        }
    }

    /// <summary>
    /// Delete rows older than <paramref name="retentionDays"/> days. Returns the number of rows deleted.
    /// Run on startup / periodically (M2 calls it once at bootstrap).
    /// </summary>
    public int Prune(int retentionDays = 30)
    {
        if (retentionDays < 0)
            throw new ArgumentOutOfRangeException(nameof(retentionDays));
        lock (_gate)
        {
            if (_disposed)
                return 0;
            using var cmd = _connection.CreateCommand();
            // ts is ISO-8601 UTC text; compare lexically against the cutoff in the same format.
            cmd.CommandText = "DELETE FROM tool_telemetry WHERE ts < $cutoff;";
            string cutoff = DateTime.UtcNow.AddDays(-retentionDays)
                .ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            cmd.Parameters.AddWithValue("$cutoff", cutoff);
            return cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Roll the append-only ledger into a per-tool breakdown (M7 decision-5) — the read path the
    /// <c>workspace</c> status surfaces. Queries this ledger's OWN connection under <see cref="_gate"/> (the
    /// same lock as <see cref="Record"/>), so it sees every committed row with no second connection and no
    /// WAL-visibility question; admin calls are rare, so the brief lock-out of the write path is negligible.
    /// <para>
    /// Per-tool count/avg/max/error_count/sum(est_tokens) come from ONE <c>GROUP BY tool</c>. p95 is computed
    /// per tool by a separate ordered query (<c>ORDER BY duration_ms LIMIT 1 OFFSET floor((count-1)*0.95)</c>) —
    /// the nearest-rank method, since SQLite has no PERCENTILE/window-percentile builtin. <c>NULL</c> est_tokens
    /// rows sum to 0 (<c>COALESCE</c>). The window is min/max(ts); <see cref="DroppedWrites"/> is carried
    /// straight through. Best-effort: a disposed ledger returns <see cref="TelemetrySummary.Empty"/> rather than
    /// throwing (an admin read must never fault).
    /// </para>
    /// </summary>
    public TelemetrySummary Summarize() => Summarize(WorkspaceId);

    public TelemetrySummary SummarizeForWorkspace(string? workspaceId) => Summarize(workspaceId);

    public TelemetryHealthFacts SummarizeOutcomes() => SummarizeOutcomes(WorkspaceId);

    public TelemetryHealthFacts SummarizeOutcomesForWorkspace(string? workspaceId) => SummarizeOutcomes(workspaceId);

    private TelemetrySummary Summarize(string? workspaceId)
    {
        lock (_gate)
        {
            if (_disposed)
                return TelemetrySummary.Empty;

            // One GROUP BY for the cheap aggregates; p95 needs a per-tool ordered offset query (below).
            var stats = new List<ToolStat>();
            using (var group = _connection.CreateCommand())
            {
                // Scoped to THIS workspace: the DB is shared across workspaces, so an unscoped GROUP BY would
                // report machine-wide totals in a per-workspace status view. `IS` matches a null id to null rows.
                group.CommandText = """
                    SELECT tool,
                           COUNT(*)                              AS calls,
                           AVG(duration_ms)                      AS avg_ms,
                           MAX(duration_ms)                      AS max_ms,
                           SUM(CASE WHEN outcome = 'error' THEN 1 ELSE 0 END) AS errors,
                           COALESCE(SUM(est_tokens), 0)          AS sum_tokens
                    FROM tool_telemetry
                    WHERE workspace_id IS $ws
                    GROUP BY tool
                    ORDER BY tool;
                    """;
                group.Parameters.AddWithValue("$ws", (object?)workspaceId ?? DBNull.Value);
                using var reader = group.ExecuteReader();
                while (reader.Read())
                {
                    string tool = reader.GetString(0);
                    long calls = reader.GetInt64(1);
                    // AVG returns REAL; it is non-null because the group has at least one row.
                    double avgMs = reader.GetDouble(2);
                    long maxMs = reader.GetInt64(3);
                    long errors = reader.GetInt64(4);
                    long sumTokens = reader.GetInt64(5);
                    long p95Ms = ComputeP95(tool, calls, workspaceId);
                    stats.Add(new ToolStat(tool, calls, avgMs, p95Ms, maxMs, errors, sumTokens));
                }
            }

            long totalCalls = 0;
            string? windowStart = null;
            string? windowEnd = null;
            using (var totals = _connection.CreateCommand())
            {
                totals.CommandText = "SELECT COUNT(*), MIN(ts), MAX(ts) FROM tool_telemetry WHERE workspace_id IS $ws;";
                totals.Parameters.AddWithValue("$ws", (object?)workspaceId ?? DBNull.Value);
                using var reader = totals.ExecuteReader();
                if (reader.Read())
                {
                    totalCalls = reader.GetInt64(0);
                    windowStart = reader.IsDBNull(1) ? null : reader.GetString(1);
                    windowEnd = reader.IsDBNull(2) ? null : reader.GetString(2);
                }
            }

            return new TelemetrySummary(stats, totalCalls, windowStart, windowEnd, DroppedWrites);
        }
    }

    private TelemetryHealthFacts SummarizeOutcomes(string? workspaceId)
    {
        lock (_gate)
        {
            if (_disposed)
                return new TelemetryHealthFacts(0, 0, 0);

            long ok = 0;
            long empty = 0;
            long error = 0;
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT outcome, COUNT(*) AS calls
                FROM tool_telemetry
                WHERE workspace_id IS $ws
                GROUP BY outcome;
                """;
            command.Parameters.AddWithValue("$ws", (object?)workspaceId ?? DBNull.Value);
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                string outcome = reader.GetString(0);
                long calls = reader.GetInt64(1);
                switch (outcome)
                {
                    case "ok":
                        ok = calls;
                        break;
                    case "empty":
                        empty = calls;
                        break;
                    case "error":
                        error = calls;
                        break;
                }
            }

            return new TelemetryHealthFacts(ok, empty, error);
        }
    }

    /// <summary>
    /// Nearest-rank p95 latency for one tool. The row at 0-based offset <c>floor((count-1)*0.95)</c> of the
    /// ascending-duration ordering is the 95th-percentile value (so a single row yields its own duration, and
    /// the max row is never skipped). Caller holds <see cref="_gate"/>.
    /// </summary>
    private long ComputeP95(string tool, long count, string? workspaceId)
    {
        // floor((count-1)*0.95): integer math on count>=1. count is the GROUP BY count, always >= 1 here.
        long offset = (long)Math.Floor((count - 1) * 0.95);
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "SELECT duration_ms FROM tool_telemetry WHERE tool = $tool AND workspace_id IS $ws " +
            "ORDER BY duration_ms ASC LIMIT 1 OFFSET $offset;";
        cmd.Parameters.AddWithValue("$tool", tool);
        cmd.Parameters.AddWithValue("$ws", (object?)workspaceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$offset", offset);
        object? value = cmd.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Test-only: insert a minimal valid row with an explicit timestamp so <see cref="Prune"/> can be
    /// exercised against rows of a known age. Stamps this ledger's <see cref="WorkspaceId"/> (as the production
    /// <see cref="TelemetryScope"/> write path does), so the row is visible to the workspace-scoped
    /// <see cref="Summarize"/>. Not part of the production write path.
    /// </summary>
    internal void InsertRawForTest(string id, DateTime tsUtc, string tool)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "INSERT INTO tool_telemetry (id, ts, tool, workspace_id, duration_ms, outcome) " +
                "VALUES ($id, $ts, $tool, $ws, 0, 'ok');";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$ts",
                tsUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$tool", tool);
            cmd.Parameters.AddWithValue("$ws", (object?)WorkspaceId ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Persists this process's swallowed-write count so a later reader can see it. <see cref="DroppedWrites"/> is
    /// an in-process counter that dies with the host, but every analysis that reads the ledger from another
    /// invocation — the canary gate above all — needs to know the population it is reading may be incomplete.
    /// Best-effort by the same rule as <see cref="Record"/>: a failure here is itself a drop, never an error.
    /// </summary>
    public void FlushDropCount()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            FlushDropCountLocked();
        }
    }

    private void FlushDropCountLocked()
    {
        if (DroppedWrites == 0)
            return;

        try
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText =
                "INSERT INTO telemetry_drops (process_id, dropped_writes) VALUES ($pid, $dropped) " +
                "ON CONFLICT(process_id) DO UPDATE SET dropped_writes = excluded.dropped_writes, " +
                "recorded_at = strftime('%Y-%m-%dT%H:%M:%fZ','now');";
            command.Parameters.AddWithValue("$pid", _processId);
            command.Parameters.AddWithValue("$dropped", DroppedWrites);
            command.ExecuteNonQuery();
        }
        catch (Exception)
        {
            // The ledger is already failing to write; a failed drop-count write changes nothing but the count.
            unchecked { DroppedWrites++; }
        }
    }

    /// <summary>The swallowed-write counts every process has flushed to this ledger, summed.</summary>
    public static long ReadPersistedDropCount(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        if (!File.Exists(dbPath))
            return 0;

        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT COALESCE(SUM(dropped_writes), 0) FROM telemetry_drops;";
            object? total = command.ExecuteScalar();
            return total is null or DBNull ? 0 : Convert.ToInt64(total, CultureInfo.InvariantCulture);
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            FlushDropCountLocked();
            _disposed = true;
            _insert.Dispose();
            _connection.Dispose();
        }
    }
}
