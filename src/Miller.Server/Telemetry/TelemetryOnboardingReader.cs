using System.Globalization;
using System.Text.Json;
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
    string? Error)
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

            string? windowEnd = ReadMaxTimestamp(connection, workspaceId);
            if (string.IsNullOrWhiteSpace(windowEnd))
                return EmptyAvailable("sparse");

            string cutoff = ComputeCutoff(windowEnd, windowDays);
            List<TelemetryEvent> events = ReadEvents(connection, workspaceId, cutoff);
            if (events.Count == 0)
                return EmptyAvailable("sparse");

            string state = events.Count >= 3 ? "ready" : "sparse";
            return new TelemetryOnboardingFacts(
                Available: true,
                State: state,
                TotalCalls: events.Count,
                WindowStartTs: events[0].Ts,
                WindowEndTs: events[^1].Ts,
                ToolMix: ToolMix(events, limit),
                SuccessfulFlows: Flows(events, limit),
                TargetHashes: TargetHashes(events, limit),
                CommonMisses: Misses(events, limit),
                Friction: Friction(events, limit),
                Error: null);
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

    private static string? ReadMaxTimestamp(SqliteConnection connection, string? workspaceId)
    {
        using var command = connection.CreateCommand();
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

    private static List<TelemetryEvent> ReadEvents(SqliteConnection connection, string? workspaceId, string cutoff)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ts, tool, op, outcome, error_kind, result_count, duration_ms,
                   bytes_returned, est_tokens, target_hash, metadata_json
            FROM tool_telemetry
            WHERE workspace_id IS $ws
              AND ($cutoff = '' OR ts >= $cutoff)
            ORDER BY ts, id;
            """;
        command.Parameters.AddWithValue("$ws", (object?)workspaceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$cutoff", cutoff);

        var events = new List<TelemetryEvent>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            events.Add(new TelemetryEvent(
                Ts: reader.GetString(0),
                Tool: reader.GetString(1),
                Op: reader.IsDBNull(2) ? null : reader.GetString(2),
                Outcome: reader.GetString(3),
                ErrorKind: reader.IsDBNull(4) ? null : reader.GetString(4),
                ResultCount: reader.IsDBNull(5) ? null : reader.GetInt64(5),
                DurationMs: reader.GetInt64(6),
                BytesReturned: reader.GetInt64(7),
                EstTokens: reader.IsDBNull(8) ? 0 : reader.GetInt64(8),
                TargetHash: reader.IsDBNull(9) ? null : reader.GetString(9),
                MetadataJson: reader.IsDBNull(10) ? "{}" : reader.GetString(10)));
        }
        return events;
    }

    private static IReadOnlyList<TelemetryToolMix> ToolMix(List<TelemetryEvent> events, int limit) =>
        events.GroupBy(static row => (row.Tool, row.Op))
            .Select(group =>
            {
                long calls = group.LongCount();
                return new TelemetryToolMix(
                    group.Key.Tool,
                    group.Key.Op,
                    calls,
                    OkCount: group.LongCount(static row => row.Outcome == "ok"),
                    EmptyCount: group.LongCount(static row => row.Outcome == "empty"),
                    ErrorCount: group.LongCount(static row => row.Outcome == "error"),
                    AvgMs: group.Average(static row => row.DurationMs),
                    P95Ms: P95(group.Select(static row => row.DurationMs)),
                    MaxMs: group.Max(static row => row.DurationMs),
                    ResultCount: group.Sum(static row => row.ResultCount ?? 0),
                    BytesReturned: group.Sum(static row => row.BytesReturned),
                    EstTokens: group.Sum(static row => row.EstTokens));
            })
            .OrderByDescending(static row => row.Calls)
            .ThenByDescending(static row => row.P95Ms)
            .ThenBy(static row => row.Tool, StringComparer.Ordinal)
            .ThenBy(static row => row.Op, StringComparer.Ordinal)
            .Take(Math.Max(1, limit))
            .ToArray();

    private static IReadOnlyList<TelemetryFlow> Flows(List<TelemetryEvent> events, int limit)
    {
        var counts = new Dictionary<(string From, string To), long>();
        for (int i = 0; i + 1 < events.Count; i++)
        {
            TelemetryEvent current = events[i];
            TelemetryEvent next = events[i + 1];
            if (current.Outcome != "ok" || next.Outcome != "ok")
                continue;
            if (!WithinFlowWindow(current.Ts, next.Ts))
                continue;

            var key = (Label(current.Tool, current.Op), Label(next.Tool, next.Op));
            counts[key] = counts.TryGetValue(key, out long existing) ? existing + 1 : 1;
        }

        return counts.Select(row => new TelemetryFlow(row.Key.From, row.Key.To, row.Value))
            .OrderByDescending(static row => row.Calls)
            .ThenBy(static row => row.From, StringComparer.Ordinal)
            .ThenBy(static row => row.To, StringComparer.Ordinal)
            .Take(Math.Max(1, limit))
            .ToArray();
    }

    private static IReadOnlyList<TargetHashFrequency> TargetHashes(List<TelemetryEvent> events, int limit) =>
        events.Where(static row => !string.IsNullOrWhiteSpace(row.TargetHash))
            .GroupBy(static row => row.TargetHash!, StringComparer.Ordinal)
            .Select(static group => new TargetHashFrequency(group.Key, group.LongCount()))
            .OrderByDescending(static row => row.Calls)
            .ThenBy(static row => row.TargetHash, StringComparer.Ordinal)
            .Take(Math.Max(1, limit))
            .ToArray();

    private static IReadOnlyList<TelemetryMiss> Misses(List<TelemetryEvent> events, int limit) =>
        events.Where(static row => row.Outcome is "empty" or "error")
            .Select(static row => (row.Tool, row.Op, Reason: MissReason(row)))
            .GroupBy(static row => (row.Tool, row.Op, row.Reason))
            .Select(static group => new TelemetryMiss(group.Key.Tool, group.Key.Op, group.Key.Reason, group.LongCount()))
            .OrderByDescending(static row => row.Calls)
            .ThenBy(static row => row.Tool, StringComparer.Ordinal)
            .ThenBy(static row => row.Reason, StringComparer.Ordinal)
            .Take(Math.Max(1, limit))
            .ToArray();

    private static IReadOnlyList<TelemetryFriction> Friction(List<TelemetryEvent> events, int limit) =>
        events.GroupBy(static row => (row.Tool, row.Op))
            .Select(group =>
            {
                long calls = group.LongCount();
                return new TelemetryFriction(
                    group.Key.Tool,
                    group.Key.Op,
                    calls,
                    AvgMs: group.Average(static row => row.DurationMs),
                    P95Ms: P95(group.Select(static row => row.DurationMs)),
                    MaxMs: group.Max(static row => row.DurationMs),
                    BytesReturned: group.Sum(static row => row.BytesReturned),
                    EstTokens: group.Sum(static row => row.EstTokens),
                    EmptyCount: group.LongCount(static row => row.Outcome == "empty"),
                    ErrorCount: group.LongCount(static row => row.Outcome == "error"));
            })
            .OrderByDescending(static row => row.ErrorCount)
            .ThenByDescending(static row => row.EmptyCount)
            .ThenByDescending(static row => row.P95Ms)
            .ThenByDescending(static row => row.BytesReturned)
            .ThenBy(static row => row.Tool, StringComparer.Ordinal)
            .Take(Math.Max(1, limit))
            .ToArray();

    private static bool WithinFlowWindow(string fromTs, string toTs)
    {
        if (!DateTimeOffset.TryParse(
                fromTs,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset from))
            return false;
        if (!DateTimeOffset.TryParse(
                toTs,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset to))
            return false;

        double seconds = (to - from).TotalSeconds;
        return seconds is >= 0 and <= 300;
    }

    private static long P95(IEnumerable<long> values)
    {
        long[] sorted = values.Order().ToArray();
        if (sorted.Length == 0)
            return 0;
        int index = (int)Math.Ceiling(sorted.Length * 0.95) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    private static string Label(string tool, string? op) =>
        string.IsNullOrWhiteSpace(op) ? tool : tool + ":" + op;

    private static string MissReason(TelemetryEvent row)
    {
        string? fromJson = MetadataString(row.MetadataJson, "empty_reason")
            ?? MetadataString(row.MetadataJson, "error_reason")
            ?? MetadataString(row.MetadataJson, "reason");
        if (!string.IsNullOrWhiteSpace(fromJson))
            return fromJson;
        if (!string.IsNullOrWhiteSpace(row.ErrorKind))
            return row.ErrorKind;
        return row.Outcome;
    }

    private static string? MetadataString(string metadataJson, string propertyName)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(metadataJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            return document.RootElement.TryGetProperty(propertyName, out JsonElement value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record TelemetryEvent(
        string Ts,
        string Tool,
        string? Op,
        string Outcome,
        string? ErrorKind,
        long? ResultCount,
        long DurationMs,
        long BytesReturned,
        long EstTokens,
        string? TargetHash,
        string MetadataJson);
}
