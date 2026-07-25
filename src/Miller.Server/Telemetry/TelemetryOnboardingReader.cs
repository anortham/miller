using System.Globalization;
using Microsoft.Data.Sqlite;
using Miller.Indexing;

namespace Miller.Server.Telemetry;

public sealed record TelemetryOnboardingFacts(
    bool Available,
    string State,
    long TotalCalls,
    string? WindowStartTs,
    string? WindowEndTs,
    IReadOnlyList<TelemetryToolMix> ToolMix,
    IReadOnlyList<TelemetryFlow> SuccessfulFlows,
    IReadOnlyList<TargetHashFrequency> TargetHashes,
    IReadOnlyList<TelemetryMiss> CommonMisses,
    IReadOnlyList<TelemetryFriction> Friction,
    string? Error,
    int ToolMixTotal = 0,
    int SuccessfulFlowsTotal = 0,
    int TargetHashesTotal = 0,
    int CommonMissesTotal = 0,
    int FrictionTotal = 0)
{
    public static TelemetryOnboardingFacts Unavailable(string state, string? error = null) => new(
        Available: false,
        State: state,
        TotalCalls: 0,
        WindowStartTs: null,
        WindowEndTs: null,
        ToolMix: [],
        SuccessfulFlows: [],
        TargetHashes: [],
        CommonMisses: [],
        Friction: [],
        Error: error);
}

public sealed record TelemetryToolMix(
    string Tool,
    string? Op,
    long Calls,
    long OkCount,
    long EmptyCount,
    long ErrorCount,
    double AvgMs,
    long P95Ms,
    long MaxMs,
    long ResultCount,
    long BytesReturned,
    long EstTokens);

public sealed record TelemetryFlow(string From, string To, long Calls);

public sealed record TelemetryMiss(string Tool, string? Op, string Reason, long Calls);

public sealed record TelemetryFriction(
    string Tool,
    string? Op,
    long Calls,
    double AvgMs,
    long P95Ms,
    long MaxMs,
    long BytesReturned,
    long EstTokens,
    long EmptyCount,
    long ErrorCount);

public static class TelemetryOnboardingReader
{
    private const int DefaultLimit = 10;

    public static TelemetryOnboardingFacts Read(string dbPath, string? workspaceId, int windowDays = 30, int limit = DefaultLimit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        if (!File.Exists(dbPath))
            return TelemetryOnboardingFacts.Unavailable("missing_telemetry_db");

        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(dbPath),
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            connection.Open();

            if (!HasTelemetryTable(connection))
                return TelemetryOnboardingFacts.Unavailable("missing_telemetry_table");

            using SqliteTransaction transaction = connection.BeginTransaction();
            string? windowEnd = ReadMaxTimestamp(connection, transaction, workspaceId);
            if (string.IsNullOrWhiteSpace(windowEnd))
                return EmptyAvailable("sparse");

            string cutoff = ComputeCutoff(windowEnd, windowDays);
            WindowSummary window = ReadWindowSummary(connection, transaction, workspaceId, cutoff);
            if (window.TotalCalls == 0)
                return EmptyAvailable("sparse");

            int boundedLimit = Math.Clamp(limit, 1, 100);
            BoundedRows<TelemetryToolMix> toolMix =
                ReadToolMix(connection, transaction, workspaceId, cutoff, boundedLimit);
            BoundedRows<TelemetryFlow> flows =
                ReadFlows(connection, transaction, workspaceId, cutoff, boundedLimit);
            BoundedRows<TargetHashFrequency> targets =
                ReadTargetHashes(connection, transaction, workspaceId, cutoff, boundedLimit);
            BoundedRows<TelemetryMiss> misses =
                ReadMisses(connection, transaction, workspaceId, cutoff, boundedLimit);
            BoundedRows<TelemetryFriction> friction =
                ReadFriction(connection, transaction, workspaceId, cutoff, boundedLimit);
            string state = window.TotalCalls >= 3 ? "ready" : "sparse";
            return new TelemetryOnboardingFacts(
                Available: true,
                State: state,
                TotalCalls: window.TotalCalls,
                WindowStartTs: window.StartTs,
                WindowEndTs: window.EndTs,
                ToolMix: toolMix.Rows,
                SuccessfulFlows: flows.Rows,
                TargetHashes: targets.Rows,
                CommonMisses: misses.Rows,
                Friction: friction.Rows,
                Error: null,
                ToolMixTotal: toolMix.Total,
                SuccessfulFlowsTotal: flows.Total,
                TargetHashesTotal: targets.Total,
                CommonMissesTotal: misses.Total,
                FrictionTotal: friction.Total);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return TelemetryOnboardingFacts.Unavailable("unreadable_telemetry_db", ex.Message);
        }
    }

    private static TelemetryOnboardingFacts EmptyAvailable(string state) => new(
        Available: true,
        State: state,
        TotalCalls: 0,
        WindowStartTs: null,
        WindowEndTs: null,
        ToolMix: [],
        SuccessfulFlows: [],
        TargetHashes: [],
        CommonMisses: [],
        Friction: [],
        Error: null);

    private static bool HasTelemetryTable(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'tool_telemetry' LIMIT 1;";
        return command.ExecuteScalar() is not null;
    }

    private static string? ReadMaxTimestamp(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? workspaceId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT MAX(ts) FROM tool_telemetry WHERE workspace_id IS $ws;";
        command.Parameters.AddWithValue("$ws", (object?)workspaceId ?? DBNull.Value);
        object? value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static string ComputeCutoff(string windowEnd, int windowDays)
    {
        int boundedDays = Math.Max(1, windowDays);
        if (!DateTimeOffset.TryParse(
                windowEnd,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset parsed))
            return string.Empty;

        return parsed.AddDays(-boundedDays).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    }

    private static WindowSummary ReadWindowSummary(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? workspaceId,
        string cutoff)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*), MIN(ts), MAX(ts)
            FROM tool_telemetry
            WHERE workspace_id IS $ws
              AND ($cutoff = '' OR ts >= $cutoff);
            """;
        command.Parameters.AddWithValue("$ws", (object?)workspaceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$cutoff", cutoff);

        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
            return new WindowSummary(0, null, null);
        return new WindowSummary(
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    private static BoundedRows<TelemetryToolMix> ReadToolMix(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? workspaceId,
        string cutoff,
        int limit)
    {
        using SqliteCommand command = ScopedCommand(
            connection,
            transaction,
            workspaceId,
            cutoff,
            limit,
            """
            WITH scoped AS (
                SELECT id, tool, op, outcome, duration_ms,
                       COALESCE(result_count, 0) AS result_count,
                       bytes_returned, COALESCE(est_tokens, 0) AS est_tokens
                FROM tool_telemetry
                WHERE workspace_id IS $ws
                  AND ($cutoff = '' OR ts >= $cutoff)
            ),
            ranked AS (
                SELECT *,
                       ROW_NUMBER() OVER (
                           PARTITION BY tool, op
                           ORDER BY duration_ms, id) AS duration_rank,
                       COUNT(*) OVER (PARTITION BY tool, op) AS duration_count
                FROM scoped
            ),
            grouped AS (
                SELECT tool, op,
                       COUNT(*) AS calls,
                       SUM(outcome = 'ok') AS ok_count,
                       SUM(outcome = 'empty') AS empty_count,
                       SUM(outcome = 'error') AS error_count,
                       AVG(duration_ms) AS avg_ms,
                       MAX(CASE
                           WHEN duration_rank = ((duration_count * 95 + 99) / 100)
                           THEN duration_ms
                       END) AS p95_ms,
                       MAX(duration_ms) AS max_ms,
                       SUM(result_count) AS result_count,
                       SUM(bytes_returned) AS bytes_returned,
                       SUM(est_tokens) AS est_tokens
                FROM ranked
                GROUP BY tool, op
            )
            SELECT *, COUNT(*) OVER () AS total_groups
            FROM grouped
            ORDER BY calls DESC, p95_ms DESC, tool, op
            LIMIT $limit;
            """);

        var rows = new List<TelemetryToolMix>();
        int total = 0;
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            total = checked((int)reader.GetInt64(12));
            rows.Add(new TelemetryToolMix(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetDouble(6),
                reader.GetInt64(7),
                reader.GetInt64(8),
                reader.GetInt64(9),
                reader.GetInt64(10),
                reader.GetInt64(11)));
        }
        return new BoundedRows<TelemetryToolMix>(rows, total);
    }

    private static BoundedRows<TelemetryFlow> ReadFlows(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? workspaceId,
        string cutoff,
        int limit)
    {
        using SqliteCommand command = ScopedCommand(
            connection,
            transaction,
            workspaceId,
            cutoff,
            limit,
            """
            WITH scoped AS (
                SELECT id, ts, tool, op, outcome
                FROM tool_telemetry
                WHERE workspace_id IS $ws
                  AND ($cutoff = '' OR ts >= $cutoff)
            ),
            paired AS (
                SELECT ts, tool, op, outcome,
                       LAG(ts) OVER (ORDER BY ts, id) AS previous_ts,
                       LAG(tool) OVER (ORDER BY ts, id) AS previous_tool,
                       LAG(op) OVER (ORDER BY ts, id) AS previous_op,
                       LAG(outcome) OVER (ORDER BY ts, id) AS previous_outcome
                FROM scoped
            ),
            grouped AS (
                SELECT CASE
                           WHEN previous_op IS NULL OR TRIM(previous_op) = '' THEN previous_tool
                           ELSE previous_tool || ':' || previous_op
                       END AS from_label,
                       CASE
                           WHEN op IS NULL OR TRIM(op) = '' THEN tool
                           ELSE tool || ':' || op
                       END AS to_label,
                       COUNT(*) AS calls
                FROM paired
                WHERE previous_outcome = 'ok'
                  AND outcome = 'ok'
                  AND (julianday(ts) - julianday(previous_ts)) * 86400.0 BETWEEN 0 AND 300
                GROUP BY from_label, to_label
            )
            SELECT *, COUNT(*) OVER () AS total_groups
            FROM grouped
            ORDER BY calls DESC, from_label, to_label
            LIMIT $limit;
            """);

        var rows = new List<TelemetryFlow>();
        int total = 0;
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            total = checked((int)reader.GetInt64(3));
            rows.Add(new TelemetryFlow(reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
        }
        return new BoundedRows<TelemetryFlow>(rows, total);
    }

    private static BoundedRows<TargetHashFrequency> ReadTargetHashes(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? workspaceId,
        string cutoff,
        int limit)
    {
        using SqliteCommand command = ScopedCommand(
            connection,
            transaction,
            workspaceId,
            cutoff,
            limit,
            """
            WITH grouped AS (
                SELECT target_hash, COUNT(*) AS calls
                FROM tool_telemetry
                WHERE workspace_id IS $ws
                  AND ($cutoff = '' OR ts >= $cutoff)
                  AND target_hash IS NOT NULL
                  AND TRIM(target_hash) <> ''
                GROUP BY target_hash
            )
            SELECT *, COUNT(*) OVER () AS total_groups
            FROM grouped
            ORDER BY calls DESC, target_hash
            LIMIT $limit;
            """);

        var rows = new List<TargetHashFrequency>();
        int total = 0;
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            total = checked((int)reader.GetInt64(2));
            rows.Add(new TargetHashFrequency(reader.GetString(0), reader.GetInt64(1)));
        }
        return new BoundedRows<TargetHashFrequency>(rows, total);
    }

    private static BoundedRows<TelemetryMiss> ReadMisses(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? workspaceId,
        string cutoff,
        int limit)
    {
        using SqliteCommand command = ScopedCommand(
            connection,
            transaction,
            workspaceId,
            cutoff,
            limit,
            """
            WITH scoped AS (
                SELECT tool, op,
                       CAST(COALESCE(
                           CASE WHEN json_valid(metadata_json)
                                     AND json_type(metadata_json, '$.empty_reason') = 'text'
                                THEN json_extract(metadata_json, '$.empty_reason') END,
                           CASE WHEN json_valid(metadata_json)
                                     AND json_type(metadata_json, '$.error_reason') = 'text'
                                THEN json_extract(metadata_json, '$.error_reason') END,
                           CASE WHEN json_valid(metadata_json)
                                     AND json_type(metadata_json, '$.reason') = 'text'
                                THEN json_extract(metadata_json, '$.reason') END,
                           NULLIF(error_kind, ''),
                           outcome) AS TEXT) AS reason
                FROM tool_telemetry
                WHERE workspace_id IS $ws
                  AND ($cutoff = '' OR ts >= $cutoff)
                  AND outcome IN ('empty', 'error')
            ),
            grouped AS (
                SELECT tool, op, reason, COUNT(*) AS calls
                FROM scoped
                GROUP BY tool, op, reason
            )
            SELECT *, COUNT(*) OVER () AS total_groups
            FROM grouped
            ORDER BY calls DESC, tool, reason, op
            LIMIT $limit;
            """);

        var rows = new List<TelemetryMiss>();
        int total = 0;
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            total = checked((int)reader.GetInt64(4));
            rows.Add(new TelemetryMiss(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3)));
        }
        return new BoundedRows<TelemetryMiss>(rows, total);
    }

    private static BoundedRows<TelemetryFriction> ReadFriction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? workspaceId,
        string cutoff,
        int limit)
    {
        using SqliteCommand command = ScopedCommand(
            connection,
            transaction,
            workspaceId,
            cutoff,
            limit,
            """
            WITH scoped AS (
                SELECT id, tool, op, outcome, duration_ms,
                       bytes_returned, COALESCE(est_tokens, 0) AS est_tokens
                FROM tool_telemetry
                WHERE workspace_id IS $ws
                  AND ($cutoff = '' OR ts >= $cutoff)
            ),
            ranked AS (
                SELECT *,
                       ROW_NUMBER() OVER (
                           PARTITION BY tool, op
                           ORDER BY duration_ms, id) AS duration_rank,
                       COUNT(*) OVER (PARTITION BY tool, op) AS duration_count
                FROM scoped
            ),
            grouped AS (
                SELECT tool, op,
                       COUNT(*) AS calls,
                       AVG(duration_ms) AS avg_ms,
                       MAX(CASE
                           WHEN duration_rank = ((duration_count * 95 + 99) / 100)
                           THEN duration_ms
                       END) AS p95_ms,
                       MAX(duration_ms) AS max_ms,
                       SUM(bytes_returned) AS bytes_returned,
                       SUM(est_tokens) AS est_tokens,
                       SUM(outcome = 'empty') AS empty_count,
                       SUM(outcome = 'error') AS error_count
                FROM ranked
                GROUP BY tool, op
            )
            SELECT *, COUNT(*) OVER () AS total_groups
            FROM grouped
            ORDER BY error_count DESC, empty_count DESC, p95_ms DESC,
                     bytes_returned DESC, tool, op
            LIMIT $limit;
            """);

        var rows = new List<TelemetryFriction>();
        int total = 0;
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            total = checked((int)reader.GetInt64(10));
            rows.Add(new TelemetryFriction(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetInt64(2),
                reader.GetDouble(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt64(7),
                reader.GetInt64(8),
                reader.GetInt64(9)));
        }
        return new BoundedRows<TelemetryFriction>(rows, total);
    }

    private static SqliteCommand ScopedCommand(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? workspaceId,
        string cutoff,
        int limit,
        string sql)
    {
        SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$ws", (object?)workspaceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$cutoff", cutoff);
        command.Parameters.AddWithValue("$limit", limit);
        return command;
    }

    private sealed record WindowSummary(long TotalCalls, string? StartTs, string? EndTs);

    private sealed record BoundedRows<T>(IReadOnlyList<T> Rows, int Total);
}
