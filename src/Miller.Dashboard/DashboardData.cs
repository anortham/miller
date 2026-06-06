using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Server.Workspaces;

namespace Miller.Dashboard;

public sealed record DashboardWorkspaceRow(
    [property: JsonPropertyName("workspace_id")] string WorkspaceId,
    [property: JsonPropertyName("display_id")] string DisplayId,
    [property: JsonPropertyName("canonical_root")] string CanonicalRoot,
    [property: JsonPropertyName("index_db_path")] string IndexDbPath,
    [property: JsonPropertyName("last_seen_at")] string LastSeenAt,
    [property: JsonPropertyName("last_scan_at")] string? LastScanAt,
    [property: JsonPropertyName("last_revision")] long? LastRevision,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("last_error")] string? LastError);

public sealed record DashboardTelemetrySummary(
    [property: JsonPropertyName("workspace_id")] string? WorkspaceId,
    [property: JsonPropertyName("tools")] IReadOnlyList<DashboardToolStat> Tools,
    [property: JsonPropertyName("total_calls")] long TotalCalls,
    [property: JsonPropertyName("window_start_ts")] string? WindowStartTs,
    [property: JsonPropertyName("window_end_ts")] string? WindowEndTs,
    [property: JsonPropertyName("recent_errors")] IReadOnlyList<DashboardRecentError> RecentErrors);

public sealed record DashboardToolStat(
    [property: JsonPropertyName("tool")] string Tool,
    [property: JsonPropertyName("calls")] long Calls,
    [property: JsonPropertyName("avg_ms")] double AvgMs,
    [property: JsonPropertyName("p95_ms")] long P95Ms,
    [property: JsonPropertyName("max_ms")] long MaxMs,
    [property: JsonPropertyName("error_count")] long ErrorCount,
    [property: JsonPropertyName("sum_est_tokens")] long SumEstTokens,
    [property: JsonPropertyName("last_call_ts")] string? LastCallTs,
    [property: JsonPropertyName("last_outcome")] string? LastOutcome,
    [property: JsonPropertyName("last_error_ts")] string? LastErrorTs,
    [property: JsonPropertyName("last_error_kind")] string? LastErrorKind);

public sealed record DashboardRecentError(
    [property: JsonPropertyName("ts")] string Ts,
    [property: JsonPropertyName("tool")] string Tool,
    [property: JsonPropertyName("op")] string? Op,
    [property: JsonPropertyName("error_kind")] string? ErrorKind,
    [property: JsonPropertyName("duration_ms")] long DurationMs);

public sealed record DashboardLanguageStat(
    [property: JsonPropertyName("language")] string Language,
    [property: JsonPropertyName("file_count")] long FileCount,
    [property: JsonPropertyName("symbol_count")] long SymbolCount,
    [property: JsonPropertyName("content_bytes")] long ContentBytes);

public sealed record DashboardSymbolKindStat(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("count")] long Count);

public sealed record DashboardWorkspaceFacts(
    [property: JsonPropertyName("workspace_id")] string WorkspaceId,
    [property: JsonPropertyName("display_id")] string DisplayId,
    [property: JsonPropertyName("canonical_root")] string CanonicalRoot,
    [property: JsonPropertyName("index_db_path")] string IndexDbPath,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("file_count")] long FileCount,
    [property: JsonPropertyName("symbol_count")] long SymbolCount,
    [property: JsonPropertyName("language_count")] long LanguageCount,
    [property: JsonPropertyName("content_bytes")] long ContentBytes,
    [property: JsonPropertyName("last_revision")] long? LastRevision,
    [property: JsonPropertyName("last_scan_at")] string? LastScanAt,
    [property: JsonPropertyName("search_sidecar_status")] string SearchSidecarStatus,
    [property: JsonPropertyName("languages")] IReadOnlyList<DashboardLanguageStat> Languages,
    [property: JsonPropertyName("symbol_kinds")] IReadOnlyList<DashboardSymbolKindStat> SymbolKinds);

public sealed record DashboardContextSavingsTool(
    [property: JsonPropertyName("tool")] string Tool,
    [property: JsonPropertyName("tracked_calls")] long TrackedCalls,
    [property: JsonPropertyName("source_bytes")] long SourceBytes,
    [property: JsonPropertyName("bytes_returned")] long BytesReturned,
    [property: JsonPropertyName("saved_bytes")] long SavedBytes,
    [property: JsonPropertyName("estimated_returned_tokens")] long EstimatedReturnedTokens);

public sealed record DashboardContextSavingsSummary(
    [property: JsonPropertyName("workspace_id")] string? WorkspaceId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("tracked_calls")] long TrackedCalls,
    [property: JsonPropertyName("source_bytes")] long SourceBytes,
    [property: JsonPropertyName("bytes_returned")] long BytesReturned,
    [property: JsonPropertyName("saved_bytes")] long SavedBytes,
    [property: JsonPropertyName("estimated_returned_tokens")] long EstimatedReturnedTokens,
    [property: JsonPropertyName("tools")] IReadOnlyList<DashboardContextSavingsTool> Tools)
{
    public static DashboardContextSavingsSummary NotTracked(string? workspaceId) =>
        new(
            workspaceId,
            "not_tracked",
            TrackedCalls: 0,
            SourceBytes: 0,
            BytesReturned: 0,
            SavedBytes: 0,
            EstimatedReturnedTokens: 0,
            Array.Empty<DashboardContextSavingsTool>());
}

public sealed record DashboardSnapshot
{
    public DashboardSnapshot(
        IReadOnlyList<DashboardWorkspaceRow> Workspaces,
        DashboardTelemetrySummary Telemetry,
        string? SelectedWorkspaceId,
        IReadOnlyList<DashboardWorkspaceFacts>? WorkspaceFacts = null,
        DashboardWorkspaceFacts? SelectedWorkspaceFacts = null,
        DashboardContextSavingsSummary? ContextSavings = null)
    {
        this.Workspaces = Workspaces;
        this.Telemetry = Telemetry;
        this.SelectedWorkspaceId = SelectedWorkspaceId;
        this.WorkspaceFacts = WorkspaceFacts ?? Array.Empty<DashboardWorkspaceFacts>();
        this.SelectedWorkspaceFacts = SelectedWorkspaceFacts;
        this.ContextSavings = ContextSavings ?? DashboardContextSavingsSummary.NotTracked(SelectedWorkspaceId);
    }

    [JsonPropertyName("workspaces")]
    public IReadOnlyList<DashboardWorkspaceRow> Workspaces { get; init; }

    [JsonPropertyName("telemetry")]
    public DashboardTelemetrySummary Telemetry { get; init; }

    [JsonPropertyName("selected_workspace_id")]
    public string? SelectedWorkspaceId { get; init; }

    [JsonPropertyName("workspace_facts")]
    public IReadOnlyList<DashboardWorkspaceFacts> WorkspaceFacts { get; init; }

    [JsonPropertyName("selected_workspace_facts")]
    public DashboardWorkspaceFacts? SelectedWorkspaceFacts { get; init; }

    [JsonPropertyName("context_savings")]
    public DashboardContextSavingsSummary ContextSavings { get; init; }
}

public static class DashboardData
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    public static IReadOnlyList<DashboardWorkspaceRow> ReadWorkspaces(string registryDbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registryDbPath);
        if (!File.Exists(registryDbPath))
            return Array.Empty<DashboardWorkspaceRow>();

        using var connection = OpenReadOnly(registryDbPath);
        if (!TableExists(connection, "workspaces"))
            return Array.Empty<DashboardWorkspaceRow>();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT workspace_id, display_id, canonical_root, index_db_path, last_seen_at, last_scan_at,
                   last_revision, state, last_error
            FROM workspaces
            ORDER BY CASE WHEN state IN ('current','ready','loaded_existing') THEN 0 ELSE 1 END,
                     display_id COLLATE NOCASE,
                     display_id,
                     workspace_id;
            """;
        using var reader = cmd.ExecuteReader();
        var rows = new List<DashboardWorkspaceRow>();
        while (reader.Read())
        {
            rows.Add(new DashboardWorkspaceRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetInt64(6),
                reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }

        return rows;
    }

    public static DashboardTelemetrySummary ReadTelemetrySummary(string telemetryDbPath, string? workspaceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(telemetryDbPath);
        if (!File.Exists(telemetryDbPath))
        {
            return new DashboardTelemetrySummary(
                workspaceId,
                Array.Empty<DashboardToolStat>(),
                0,
                null,
                null,
                Array.Empty<DashboardRecentError>());
        }

        using var connection = OpenReadOnly(telemetryDbPath);
        if (!TableExists(connection, "tool_telemetry"))
        {
            return new DashboardTelemetrySummary(
                workspaceId,
                Array.Empty<DashboardToolStat>(),
                0,
                null,
                null,
                Array.Empty<DashboardRecentError>());
        }

        var tools = ReadToolStats(connection, workspaceId);
        (long totalCalls, string? windowStart, string? windowEnd) = ReadTotals(connection, workspaceId);
        IReadOnlyList<DashboardRecentError> recentErrors = ReadRecentErrors(connection, workspaceId);
        return new DashboardTelemetrySummary(workspaceId, tools, totalCalls, windowStart, windowEnd, recentErrors);
    }

    private static DashboardContextSavingsSummary ReadContextSavings(string telemetryDbPath, string? workspaceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(telemetryDbPath);
        if (!File.Exists(telemetryDbPath))
            return DashboardContextSavingsSummary.NotTracked(workspaceId);

        using var connection = OpenReadOnly(telemetryDbPath);
        if (!TableExists(connection, "tool_telemetry"))
            return DashboardContextSavingsSummary.NotTracked(workspaceId);

        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT COUNT(*) AS tracked_calls,
                       COALESCE(SUM(source_bytes), 0) AS source_bytes,
                       COALESCE(SUM(bytes_returned), 0) AS bytes_returned,
                       COALESCE(SUM(
                           CASE
                               WHEN source_bytes > COALESCE(bytes_returned, 0)
                                   THEN source_bytes - COALESCE(bytes_returned, 0)
                               ELSE 0
                           END), 0) AS saved_bytes,
                       COALESCE(SUM(est_tokens), 0) AS estimated_returned_tokens
                FROM tool_telemetry
                WHERE workspace_id IS $ws AND source_bytes > 0;
                """;
            cmd.Parameters.AddWithValue("$ws", (object?)workspaceId ?? DBNull.Value);
            using SqliteDataReader reader = cmd.ExecuteReader();
            if (!reader.Read())
                return DashboardContextSavingsSummary.NotTracked(workspaceId);

            long trackedCalls = reader.GetInt64(0);
            if (trackedCalls == 0)
                return DashboardContextSavingsSummary.NotTracked(workspaceId);

            return new DashboardContextSavingsSummary(
                workspaceId,
                "tracked",
                trackedCalls,
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                ReadContextSavingsTools(connection, workspaceId));
        }
        catch (SqliteException)
        {
            return DashboardContextSavingsSummary.NotTracked(workspaceId);
        }
    }

    private static IReadOnlyList<DashboardContextSavingsTool> ReadContextSavingsTools(
        SqliteConnection connection,
        string? workspaceId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT tool,
                   COUNT(*) AS tracked_calls,
                   COALESCE(SUM(source_bytes), 0) AS source_bytes,
                   COALESCE(SUM(bytes_returned), 0) AS bytes_returned,
                   COALESCE(SUM(
                       CASE
                           WHEN source_bytes > COALESCE(bytes_returned, 0)
                               THEN source_bytes - COALESCE(bytes_returned, 0)
                           ELSE 0
                       END), 0) AS saved_bytes,
                   COALESCE(SUM(est_tokens), 0) AS estimated_returned_tokens
            FROM tool_telemetry
            WHERE workspace_id IS $ws AND source_bytes > 0
            GROUP BY tool
            ORDER BY saved_bytes DESC, source_bytes DESC, tool
            LIMIT 8;
            """;
        cmd.Parameters.AddWithValue("$ws", (object?)workspaceId ?? DBNull.Value);
        using SqliteDataReader reader = cmd.ExecuteReader();
        var tools = new List<DashboardContextSavingsTool>();
        while (reader.Read())
        {
            tools.Add(new DashboardContextSavingsTool(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5)));
        }

        return tools;
    }

    public static DashboardSnapshot ReadSnapshot(
        string registryDbPath,
        string telemetryDbPath,
        string? workspaceId,
        string? preferredWorkspaceRoot = null)
    {
        IReadOnlyList<DashboardWorkspaceRow> workspaces = ReadWorkspaces(registryDbPath);
        string? selectedWorkspaceId = SelectWorkspace(workspaces, telemetryDbPath, workspaceId, preferredWorkspaceRoot);
        DashboardTelemetrySummary telemetry = ReadTelemetrySummary(telemetryDbPath, selectedWorkspaceId);
        DashboardWorkspaceRow? selectedWorkspace = workspaces.FirstOrDefault(
            row => string.Equals(row.WorkspaceId, selectedWorkspaceId, StringComparison.Ordinal));
        DashboardWorkspaceFacts? selectedFacts = selectedWorkspace is null
            ? null
            : DashboardIndexFactsReader.Read(selectedWorkspace);
        IReadOnlyList<DashboardWorkspaceFacts> workspaceFacts = selectedFacts is null
            ? Array.Empty<DashboardWorkspaceFacts>()
            : new[] { selectedFacts };
        DashboardContextSavingsSummary contextSavings = ReadContextSavings(telemetryDbPath, selectedWorkspaceId);
        return new DashboardSnapshot(
            workspaces,
            telemetry,
            selectedWorkspaceId,
            workspaceFacts,
            selectedFacts,
            contextSavings);
    }

    public static string RenderWorkspacesJson(string registryDbPath) =>
        JsonSerializer.Serialize(ReadWorkspaces(registryDbPath), JsonOptions);

    public static string RenderTelemetryJson(string telemetryDbPath, string? workspaceId) =>
        JsonSerializer.Serialize(ReadTelemetrySummary(telemetryDbPath, workspaceId), JsonOptions);

    public static string RenderSnapshotJson(string registryDbPath, string telemetryDbPath, string? workspaceId) =>
        JsonSerializer.Serialize(ReadSnapshot(registryDbPath, telemetryDbPath, workspaceId), JsonOptions);

    private static string? SelectWorkspace(
        IReadOnlyList<DashboardWorkspaceRow> workspaces,
        string telemetryDbPath,
        string? requestedWorkspaceId,
        string? preferredWorkspaceRoot)
    {
        if (!string.IsNullOrWhiteSpace(requestedWorkspaceId) &&
            workspaces.Any(row => string.Equals(row.WorkspaceId, requestedWorkspaceId, StringComparison.Ordinal)))
            return requestedWorkspaceId;

        if (!string.IsNullOrWhiteSpace(preferredWorkspaceRoot))
        {
            foreach (DashboardWorkspaceRow row in workspaces)
            {
                if (SameRoot(row.CanonicalRoot, preferredWorkspaceRoot))
                    return row.WorkspaceId;
            }
        }

        if (SelectTelemetryWorkspace(workspaces, telemetryDbPath) is { } telemetryWorkspaceId)
            return telemetryWorkspaceId;

        return workspaces.Count == 0 ? requestedWorkspaceId : workspaces[0].WorkspaceId;
    }

    private static string? SelectTelemetryWorkspace(
        IReadOnlyList<DashboardWorkspaceRow> workspaces,
        string telemetryDbPath)
    {
        if (workspaces.Count == 0 || !File.Exists(telemetryDbPath))
            return null;

        var registeredIds = workspaces
            .Select(row => row.WorkspaceId)
            .ToHashSet(StringComparer.Ordinal);

        using var connection = OpenReadOnly(telemetryDbPath);
        if (!TableExists(connection, "tool_telemetry"))
            return null;

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT workspace_id
            FROM tool_telemetry
            WHERE workspace_id IS NOT NULL
            GROUP BY workspace_id
            ORDER BY COUNT(*) DESC, workspace_id;
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string workspaceId = reader.GetString(0);
            if (registeredIds.Contains(workspaceId))
                return workspaceId;
        }

        return null;
    }

    private static bool SameRoot(string left, string right)
    {
        try
        {
            return string.Equals(
                NormalizeRoot(left),
                NormalizeRoot(right),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string NormalizeRoot(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string trimmed = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length == 0 ? fullPath : trimmed;
    }

    public static WorkspaceRefreshResult RefreshWorkspace(string registryDbPath, string toolsRoot, string workspaceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registryDbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolsRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        using WorkspaceRegistry registry = WorkspaceRegistry.Open(registryDbPath);
        var runner = JulieExtractRunner.Locate(toolsRoot);
        // A dashboard-triggered refresh holds the workspace lock around the scan, so it is also a safe sidecar
        // writer; honor the same default-on MILLER_SEARCH_SIDECAR flag as the server so the artifact stays consistent.
        var sidecar = SymbolSearchSidecar.FromEnvironment();
        var refresh = new CrossWorkspaceRefreshService(registry, runner, sidecar);
        return refresh.Refresh(workspaceId);
    }

    private static SqliteConnection OpenReadOnly(string dbPath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(dbPath),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA busy_timeout=3000;";
            pragma.ExecuteNonQuery();
        }
        return connection;
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        cmd.Parameters.AddWithValue("$name", tableName);
        return cmd.ExecuteScalar() is not null;
    }

    private static IReadOnlyList<DashboardToolStat> ReadToolStats(SqliteConnection connection, string? workspaceId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT tool,
                   COUNT(*) AS calls,
                   AVG(duration_ms) AS avg_ms,
                   MAX(duration_ms) AS max_ms,
                   SUM(CASE WHEN outcome = 'error' THEN 1 ELSE 0 END) AS errors,
                   COALESCE(SUM(est_tokens), 0) AS sum_tokens,
                   MAX(ts) AS last_call_ts,
                   (SELECT latest.outcome
                    FROM tool_telemetry latest
                    WHERE latest.workspace_id IS $ws AND latest.tool = tool_telemetry.tool
                    ORDER BY latest.ts DESC, latest.id DESC
                    LIMIT 1) AS last_outcome,
                   MAX(CASE WHEN outcome = 'error' THEN ts END) AS last_error_ts,
                   (SELECT latest_error.error_kind
                    FROM tool_telemetry latest_error
                    WHERE latest_error.workspace_id IS $ws
                      AND latest_error.tool = tool_telemetry.tool
                      AND latest_error.outcome = 'error'
                    ORDER BY latest_error.ts DESC, latest_error.id DESC
                    LIMIT 1) AS last_error_kind
            FROM tool_telemetry
            WHERE workspace_id IS $ws
            GROUP BY tool
            ORDER BY tool;
            """;
        cmd.Parameters.AddWithValue("$ws", (object?)workspaceId ?? DBNull.Value);
        using var reader = cmd.ExecuteReader();
        var stats = new List<DashboardToolStat>();
        while (reader.Read())
        {
            string tool = reader.GetString(0);
            long calls = reader.GetInt64(1);
            stats.Add(new DashboardToolStat(
                tool,
                calls,
                reader.GetDouble(2),
                ComputeP95(connection, workspaceId, tool, calls),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9)));
        }

        return stats;
    }

    private static IReadOnlyList<DashboardRecentError> ReadRecentErrors(
        SqliteConnection connection,
        string? workspaceId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT ts, tool, op, error_kind, duration_ms
            FROM tool_telemetry
            WHERE workspace_id IS $ws AND outcome = 'error'
            ORDER BY ts DESC, id DESC
            LIMIT 8;
            """;
        cmd.Parameters.AddWithValue("$ws", (object?)workspaceId ?? DBNull.Value);
        using var reader = cmd.ExecuteReader();
        var errors = new List<DashboardRecentError>();
        while (reader.Read())
        {
            errors.Add(new DashboardRecentError(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt64(4)));
        }

        return errors;
    }

    private static (long TotalCalls, string? WindowStart, string? WindowEnd) ReadTotals(
        SqliteConnection connection,
        string? workspaceId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*), MIN(ts), MAX(ts) FROM tool_telemetry WHERE workspace_id IS $ws;";
        cmd.Parameters.AddWithValue("$ws", (object?)workspaceId ?? DBNull.Value);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return (0, null, null);

        return (
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    private static long ComputeP95(SqliteConnection connection, string? workspaceId, string tool, long count)
    {
        long offset = (long)Math.Floor((count - 1) * 0.95);
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT duration_ms FROM tool_telemetry WHERE tool = $tool AND workspace_id IS $ws " +
            "ORDER BY duration_ms ASC LIMIT 1 OFFSET $offset;";
        cmd.Parameters.AddWithValue("$tool", tool);
        cmd.Parameters.AddWithValue("$ws", (object?)workspaceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$offset", offset);
        object? value = cmd.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

}
