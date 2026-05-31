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
/// because MCP tool calls can run concurrently.
/// </summary>
public sealed class TelemetryLedger : IDisposable
{
    private const string CreateTableDdl = """
        CREATE TABLE IF NOT EXISTS tool_telemetry (
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
        CREATE INDEX IF NOT EXISTS idx_tool_telemetry_ts ON tool_telemetry(ts);
        CREATE INDEX IF NOT EXISTS idx_tool_telemetry_tool ON tool_telemetry(tool);
        CREATE INDEX IF NOT EXISTS idx_tool_telemetry_ws ON tool_telemetry(workspace_id);
        """;

    private readonly object _gate = new();
    private readonly SqliteConnection _connection;
    private readonly SqliteCommand _insert;
    private readonly string? _workspaceRoot;
    private bool _disposed;

    /// <summary>The workspace id stamped onto every row, or null if unknown at open time.</summary>
    public string? WorkspaceId { get; }

    /// <summary>Count of telemetry rows that failed to persist and were swallowed (never throws).</summary>
    public long DroppedWrites { get; private set; }

    private TelemetryLedger(SqliteConnection connection, string? workspaceId, string? workspaceRoot)
    {
        _connection = connection;
        WorkspaceId = workspaceId;
        _workspaceRoot = workspaceRoot;

        _insert = _connection.CreateCommand();
        _insert.CommandText = """
            INSERT INTO tool_telemetry
                (id, tool, op, workspace_id, workspace_root, duration_ms, outcome, error_kind, result_count,
                 bytes_examined, bytes_returned, source_bytes, est_tokens, index_fresh, target_hash, metadata_json)
            VALUES
                ($id, $tool, $op, $ws, $wsroot, $dur, $outcome, $errkind, $rc,
                 $bex, $bret, $src, $est, $fresh, $hash, $meta);
            """;
        // Declare parameters once; values are set per Record() call. Prepared and reused on the hot path.
        _insert.Parameters.Add("$id", SqliteType.Text);
        _insert.Parameters.Add("$tool", SqliteType.Text);
        _insert.Parameters.Add("$op", SqliteType.Text);
        _insert.Parameters.Add("$ws", SqliteType.Text);
        _insert.Parameters.Add("$wsroot", SqliteType.Text);
        _insert.Parameters.Add("$dur", SqliteType.Integer);
        _insert.Parameters.Add("$outcome", SqliteType.Text);
        _insert.Parameters.Add("$errkind", SqliteType.Text);
        _insert.Parameters.Add("$rc", SqliteType.Integer);
        _insert.Parameters.Add("$bex", SqliteType.Integer);
        _insert.Parameters.Add("$bret", SqliteType.Integer);
        _insert.Parameters.Add("$src", SqliteType.Integer);
        _insert.Parameters.Add("$est", SqliteType.Integer);
        _insert.Parameters.Add("$fresh", SqliteType.Integer);
        _insert.Parameters.Add("$hash", SqliteType.Text);
        _insert.Parameters.Add("$meta", SqliteType.Text);
        _insert.Prepare();
    }

    /// <summary>
    /// Open (creating if needed) the writable telemetry DB at <paramref name="dbPath"/> and ensure the table.
    /// The parent directory must already exist (startup creates <c>&lt;home&gt;/.miller</c>). The DB is
    /// machine-global — every workspace's miller process opens this same file — so each row is stamped with
    /// <paramref name="workspaceId"/> + <paramref name="workspaceRoot"/> and <see cref="Summarize"/> scopes back
    /// to <paramref name="workspaceId"/> so a per-workspace view never reports another workspace's rows.
    /// </summary>
    public static TelemetryLedger Open(string dbPath, string? workspaceId, string? workspaceRoot = null)
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

        return new TelemetryLedger(connection, workspaceId, workspaceRoot);
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
        return new TelemetryScope(this, tool, op, correlationId);
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
                _insert.Parameters["$tool"].Value = record.Tool;
                _insert.Parameters["$op"].Value = (object?)record.Op ?? DBNull.Value;
                _insert.Parameters["$ws"].Value = (object?)record.WorkspaceId ?? DBNull.Value;
                // workspace_root normally falls back to the process workspace root. Cross-workspace calls may
                // override it per-record so shared ledger rows attribute reads to the target workspace.
                string? workspaceRoot = string.IsNullOrWhiteSpace(record.WorkspaceRoot)
                    ? _workspaceRoot
                    : record.WorkspaceRoot;
                _insert.Parameters["$wsroot"].Value = (object?)workspaceRoot ?? DBNull.Value;
                _insert.Parameters["$dur"].Value = record.DurationMs;
                _insert.Parameters["$outcome"].Value = record.Outcome;
                _insert.Parameters["$errkind"].Value = (object?)record.ErrorKind ?? DBNull.Value;
                _insert.Parameters["$rc"].Value = (object?)record.ResultCount ?? DBNull.Value;
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
    public TelemetrySummary Summarize()
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
                group.Parameters.AddWithValue("$ws", (object?)WorkspaceId ?? DBNull.Value);
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
                    long p95Ms = ComputeP95(tool, calls);
                    stats.Add(new ToolStat(tool, calls, avgMs, p95Ms, maxMs, errors, sumTokens));
                }
            }

            long totalCalls = 0;
            string? windowStart = null;
            string? windowEnd = null;
            using (var totals = _connection.CreateCommand())
            {
                totals.CommandText = "SELECT COUNT(*), MIN(ts), MAX(ts) FROM tool_telemetry WHERE workspace_id IS $ws;";
                totals.Parameters.AddWithValue("$ws", (object?)WorkspaceId ?? DBNull.Value);
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

    /// <summary>
    /// Nearest-rank p95 latency for one tool. The row at 0-based offset <c>floor((count-1)*0.95)</c> of the
    /// ascending-duration ordering is the 95th-percentile value (so a single row yields its own duration, and
    /// the max row is never skipped). Caller holds <see cref="_gate"/>.
    /// </summary>
    private long ComputeP95(string tool, long count)
    {
        // floor((count-1)*0.95): integer math on count>=1. count is the GROUP BY count, always >= 1 here.
        long offset = (long)Math.Floor((count - 1) * 0.95);
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "SELECT duration_ms FROM tool_telemetry WHERE tool = $tool AND workspace_id IS $ws " +
            "ORDER BY duration_ms ASC LIMIT 1 OFFSET $offset;";
        cmd.Parameters.AddWithValue("$tool", tool);
        cmd.Parameters.AddWithValue("$ws", (object?)WorkspaceId ?? DBNull.Value);
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

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _insert.Dispose();
            _connection.Dispose();
        }
    }
}
