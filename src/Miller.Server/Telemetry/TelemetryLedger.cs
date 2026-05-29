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
            tool TEXT NOT NULL, op TEXT, workspace_id TEXT,
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
        """;

    private readonly object _gate = new();
    private readonly SqliteConnection _connection;
    private readonly SqliteCommand _insert;
    private bool _disposed;

    /// <summary>The workspace id stamped onto every row, or null if unknown at open time.</summary>
    public string? WorkspaceId { get; }

    /// <summary>Count of telemetry rows that failed to persist and were swallowed (never throws).</summary>
    public long DroppedWrites { get; private set; }

    private TelemetryLedger(SqliteConnection connection, string? workspaceId)
    {
        _connection = connection;
        WorkspaceId = workspaceId;

        _insert = _connection.CreateCommand();
        _insert.CommandText = """
            INSERT INTO tool_telemetry
                (id, tool, op, workspace_id, duration_ms, outcome, error_kind, result_count,
                 bytes_examined, bytes_returned, source_bytes, est_tokens, index_fresh, target_hash, metadata_json)
            VALUES
                ($id, $tool, $op, $ws, $dur, $outcome, $errkind, $rc,
                 $bex, $bret, $src, $est, $fresh, $hash, $meta);
            """;
        // Declare parameters once; values are set per Record() call. Prepared and reused on the hot path.
        _insert.Parameters.Add("$id", SqliteType.Text);
        _insert.Parameters.Add("$tool", SqliteType.Text);
        _insert.Parameters.Add("$op", SqliteType.Text);
        _insert.Parameters.Add("$ws", SqliteType.Text);
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
    /// The parent directory must already exist (startup creates <c>&lt;root&gt;/.miller</c>).
    /// </summary>
    public static TelemetryLedger Open(string dbPath, string? workspaceId)
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

        return new TelemetryLedger(connection, workspaceId);
    }

    /// <summary>
    /// Begin measuring a tool call. The returned scope is enriched by the caller/filter and persists one row
    /// on dispose. <paramref name="op"/> is the operation/mode sub-axis (null when the tool has none).
    /// </summary>
    public TelemetryScope Measure(string tool, string? op)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);
        return new TelemetryScope(this, tool, op);
    }

    /// <summary>
    /// Persist one row. Best-effort: ANY failure (a CHECK violation, a locked DB, a disposed ledger) is
    /// swallowed and counted in <see cref="DroppedWrites"/>. Telemetry must NEVER break a tool call.
    /// </summary>
    public void Record(in TelemetryRecord record)
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

                _insert.Parameters["$id"].Value = Guid.CreateVersion7().ToString();
                _insert.Parameters["$tool"].Value = record.Tool;
                _insert.Parameters["$op"].Value = (object?)record.Op ?? DBNull.Value;
                _insert.Parameters["$ws"].Value = (object?)record.WorkspaceId ?? DBNull.Value;
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
    /// Test-only: insert a minimal valid row with an explicit timestamp so <see cref="Prune"/> can be
    /// exercised against rows of a known age. Not part of the production write path.
    /// </summary>
    internal void InsertRawForTest(string id, DateTime tsUtc, string tool)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "INSERT INTO tool_telemetry (id, ts, tool, duration_ms, outcome) " +
                "VALUES ($id, $ts, $tool, 0, 'ok');";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$ts",
                tsUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$tool", tool);
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
