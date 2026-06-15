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
    [property: JsonPropertyName("duration_ms")] long DurationMs,
    [property: JsonPropertyName("id")] string? Id = null,
    [property: JsonPropertyName("workspace_id")] string? WorkspaceId = null,
    [property: JsonPropertyName("workspace_display_id")] string? WorkspaceDisplayId = null,
    [property: JsonPropertyName("error_message")] string? ErrorMessage = null,
    [property: JsonPropertyName("error_detail")] string? ErrorDetail = null);

public sealed record DashboardActivityEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("ts")] string Ts,
    [property: JsonPropertyName("tool")] string Tool,
    [property: JsonPropertyName("op")] string? Op,
    [property: JsonPropertyName("workspace_id")] string? WorkspaceId,
    [property: JsonPropertyName("workspace_display_id")] string? WorkspaceDisplayId,
    [property: JsonPropertyName("duration_ms")] long DurationMs,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("error_kind")] string? ErrorKind,
    [property: JsonPropertyName("result_count")] long? ResultCount,
    [property: JsonPropertyName("est_tokens")] long? EstTokens,
    [property: JsonPropertyName("error_message")] string? ErrorMessage = null,
    [property: JsonPropertyName("error_detail")] string? ErrorDetail = null);

public sealed record DashboardActivityFeed(
    [property: JsonPropertyName("workspace_id")] string? WorkspaceId,
    [property: JsonPropertyName("entries")] IReadOnlyList<DashboardActivityEntry> Entries);

public sealed record DashboardRuntimeInfo(
    [property: JsonPropertyName("registry_db_path")] string RegistryDbPath,
    [property: JsonPropertyName("telemetry_db_path")] string TelemetryDbPath,
    [property: JsonPropertyName("tools_root")] string ToolsRoot,
    [property: JsonPropertyName("web_root")] string WebRoot,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("preferred_workspace_root")] string PreferredWorkspaceRoot,
    [property: JsonPropertyName("process_id")] int ProcessId,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("executable_path")] string? ExecutablePath,
    [property: JsonPropertyName("stdout_log_path")] string StdoutLogPath,
    [property: JsonPropertyName("stderr_log_path")] string StderrLogPath);

public sealed record DashboardDiagnostics(
    [property: JsonPropertyName("registry_db_path")] string RegistryDbPath,
    [property: JsonPropertyName("telemetry_db_path")] string TelemetryDbPath,
    [property: JsonPropertyName("tools_root")] string ToolsRoot,
    [property: JsonPropertyName("web_root")] string WebRoot,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("preferred_workspace_root")] string PreferredWorkspaceRoot,
    [property: JsonPropertyName("process_id")] int ProcessId,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("executable_path")] string? ExecutablePath,
    [property: JsonPropertyName("stdout_log_path")] string StdoutLogPath,
    [property: JsonPropertyName("stderr_log_path")] string StderrLogPath,
    [property: JsonPropertyName("registry_db_exists")] bool RegistryDbExists,
    [property: JsonPropertyName("telemetry_db_exists")] bool TelemetryDbExists,
    [property: JsonPropertyName("telemetry_table_exists")] bool TelemetryTableExists,
    [property: JsonPropertyName("telemetry_error_details_available")] bool TelemetryErrorDetailsAvailable,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings);

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
    [property: JsonPropertyName("symbol_kinds")] IReadOnlyList<DashboardSymbolKindStat> SymbolKinds,
    [property: JsonPropertyName("content_sidecar_status")] string ContentSidecarStatus = "unknown",
    [property: JsonPropertyName("symbol_kind_count")] int SymbolKindCount = 0,
    [property: JsonPropertyName("registry_last_error")] string? RegistryLastError = null,
    [property: JsonPropertyName("extractor_version")] string? ExtractorVersion = null,
    [property: JsonPropertyName("artifact_id")] string? ArtifactId = null,
    [property: JsonPropertyName("index_revision")] long? IndexRevision = null,
    [property: JsonPropertyName("freshness_status")] string FreshnessStatus = "unknown");

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
    [property: JsonPropertyName("tools")] IReadOnlyList<DashboardContextSavingsTool> Tools,
    [property: JsonPropertyName("savings_ratio")] double? SavingsRatio = null)
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

public sealed record DashboardWorkspaceIndexEntry(
    [property: JsonPropertyName("workspace")] DashboardWorkspaceRow Workspace,
    [property: JsonPropertyName("facts")] DashboardWorkspaceFacts Facts)
{
    /// <summary>True when the index DB was opened and its facts are real (not missing/unreadable).</summary>
    [JsonIgnore]
    public bool HasFacts => Facts.Status is not ("missing" or "unreadable");
}

public sealed record DashboardWorkspaceIndex(
    [property: JsonPropertyName("entries")] IReadOnlyList<DashboardWorkspaceIndexEntry> Entries,
    [property: JsonPropertyName("workspace_count")] int WorkspaceCount,
    [property: JsonPropertyName("total_files")] long TotalFiles,
    [property: JsonPropertyName("total_symbols")] long TotalSymbols,
    [property: JsonPropertyName("language_count")] int LanguageCount);

public static class DashboardData
{
    private static readonly DashboardJsonContext JsonContext = new(new JsonSerializerOptions
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    });

    /// <summary>
    /// Landing-page view: every registered workspace paired with its index facts, plus machine-wide totals.
    /// Opens each workspace's symbols.db once (best effort — missing/unreadable ones still appear with their state).
    /// </summary>
    public static DashboardWorkspaceIndex ReadIndex(string registryDbPath)
    {
        IReadOnlyList<DashboardWorkspaceRow> workspaces = ReadWorkspaces(registryDbPath);
        var entries = new List<DashboardWorkspaceIndexEntry>(workspaces.Count);
        long totalFiles = 0;
        long totalSymbols = 0;
        var languages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (DashboardWorkspaceRow workspace in workspaces)
        {
            DashboardWorkspaceFacts facts = DashboardIndexFactsCache.Read(workspace);
            var entry = new DashboardWorkspaceIndexEntry(workspace, facts);
            entries.Add(entry);
            if (entry.HasFacts)
            {
                totalFiles += facts.FileCount;
                totalSymbols += facts.SymbolCount;
                foreach (DashboardLanguageStat language in facts.Languages)
                    languages.Add(language.Language);
            }
        }

        return new DashboardWorkspaceIndex(entries, workspaces.Count, totalFiles, totalSymbols, languages.Count);
    }

    public static string RenderIndexJson(string registryDbPath) =>
        JsonSerializer.Serialize(ReadIndex(registryDbPath), JsonContext.DashboardWorkspaceIndex);

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

    /// <summary>
    /// Per-tool telemetry rollup. <paramref name="workspaceId"/> follows the content-search convention:
    /// the sentinel <c>all</c> aggregates across every workspace (workspace ids are SHA-256 hex, so the
    /// sentinel cannot collide); any other value scopes with <c>workspace_id IS $ws</c>.
    /// </summary>
    public static DashboardTelemetrySummary ReadTelemetrySummary(string telemetryDbPath, string? workspaceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(telemetryDbPath);
        bool allWorkspaces = string.Equals(workspaceId, "all", StringComparison.Ordinal);
        string? scope = allWorkspaces ? null : workspaceId;
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

        var tools = ReadToolStats(connection, scope, allWorkspaces);
        (long totalCalls, string? windowStart, string? windowEnd) = ReadTotals(connection, scope, allWorkspaces);
        IReadOnlyList<DashboardRecentError> recentErrors = ReadRecentErrors(connection, scope, allWorkspaces);
        return new DashboardTelemetrySummary(workspaceId, tools, totalCalls, windowStart, windowEnd, recentErrors);
    }

    /// <summary>
    /// Newest-first per-call rows for the live activity feed. A null/blank <paramref name="workspaceId"/>
    /// returns the machine-wide feed (every workspace, including rows with no workspace), each entry annotated
    /// with the registered display id when the registry knows the workspace (raw id otherwise).
    /// </summary>
    public static DashboardActivityFeed ReadRecentActivity(
        string telemetryDbPath,
        string registryDbPath,
        string? workspaceId,
        int limit = 20)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(telemetryDbPath);
        string? scope = string.IsNullOrWhiteSpace(workspaceId) ? null : workspaceId;
        limit = Math.Clamp(limit, 1, 100);
        if (!File.Exists(telemetryDbPath))
            return new DashboardActivityFeed(scope, Array.Empty<DashboardActivityEntry>());

        using var connection = OpenReadOnly(telemetryDbPath);
        if (!TableExists(connection, "tool_telemetry"))
            return new DashboardActivityFeed(scope, Array.Empty<DashboardActivityEntry>());

        string errorMessageSelect = ColumnExists(connection, "tool_telemetry", "error_message")
            ? "error_message"
            : "NULL AS error_message";
        string errorDetailSelect = ColumnExists(connection, "tool_telemetry", "error_detail")
            ? "error_detail"
            : "NULL AS error_detail";

        var displayIds = scope is null
            ? ReadWorkspaces(registryDbPath).ToDictionary(
                row => row.WorkspaceId,
                row => row.DisplayId,
                StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = scope is null
            ? $"""
              SELECT id, ts, tool, op, workspace_id, duration_ms, outcome, error_kind,
                     {errorMessageSelect}, {errorDetailSelect}, result_count, est_tokens
              FROM tool_telemetry
              ORDER BY ts DESC, id DESC
              LIMIT $limit;
              """
            : $"""
              SELECT id, ts, tool, op, workspace_id, duration_ms, outcome, error_kind,
                     {errorMessageSelect}, {errorDetailSelect}, result_count, est_tokens
              FROM tool_telemetry
              WHERE workspace_id IS $ws
              ORDER BY ts DESC, id DESC
              LIMIT $limit;
              """;
        if (scope is not null)
            cmd.Parameters.AddWithValue("$ws", scope);
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader();
        var entries = new List<DashboardActivityEntry>();
        while (reader.Read())
        {
            string? rowWorkspaceId = reader.IsDBNull(4) ? null : reader.GetString(4);
            string? displayId = rowWorkspaceId is null
                ? null
                : displayIds.TryGetValue(rowWorkspaceId, out string? registered) ? registered : rowWorkspaceId;
            entries.Add(new DashboardActivityEntry(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                rowWorkspaceId,
                displayId,
                reader.GetInt64(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(10) ? null : reader.GetInt64(10),
                reader.IsDBNull(11) ? null : reader.GetInt64(11),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9)));
        }

        return new DashboardActivityFeed(scope, entries);
    }

    public static string RenderActivityJson(
        string telemetryDbPath,
        string registryDbPath,
        string? workspaceId,
        int limit = 20) =>
        JsonSerializer.Serialize(
            ReadRecentActivity(telemetryDbPath, registryDbPath, workspaceId, limit),
            JsonContext.DashboardActivityFeed);

    public static DashboardDiagnostics ReadDiagnostics(DashboardRuntimeInfo runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        bool registryExists = File.Exists(runtime.RegistryDbPath);
        bool telemetryExists = File.Exists(runtime.TelemetryDbPath);
        bool telemetryTableExists = false;
        bool errorDetailsAvailable = false;
        var warnings = new List<string>();

        if (telemetryExists)
        {
            try
            {
                using var connection = OpenReadOnly(runtime.TelemetryDbPath);
                telemetryTableExists = TableExists(connection, "tool_telemetry");
                errorDetailsAvailable = telemetryTableExists &&
                    ColumnExists(connection, "tool_telemetry", "error_message") &&
                    ColumnExists(connection, "tool_telemetry", "error_detail");
            }
            catch (SqliteException ex)
            {
                warnings.Add($"Telemetry DB is unreadable: {ex.Message}");
            }
            catch (IOException ex)
            {
                warnings.Add($"Telemetry DB is unreadable: {ex.Message}");
            }
        }

        if (telemetryTableExists && !errorDetailsAvailable)
        {
            warnings.Add(
                "Telemetry DB uses an older telemetry schema; restart a Miller server to migrate error_message/error_detail columns before issue details can be captured.");
        }

        return new DashboardDiagnostics(
            runtime.RegistryDbPath,
            runtime.TelemetryDbPath,
            runtime.ToolsRoot,
            runtime.WebRoot,
            runtime.Url,
            runtime.PreferredWorkspaceRoot,
            runtime.ProcessId,
            runtime.Version,
            runtime.ExecutablePath,
            runtime.StdoutLogPath,
            runtime.StderrLogPath,
            registryExists,
            telemetryExists,
            telemetryTableExists,
            errorDetailsAvailable,
            warnings);
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

            long sourceBytes = reader.GetInt64(1);
            long savedBytes = reader.GetInt64(3);
            return new DashboardContextSavingsSummary(
                workspaceId,
                "tracked",
                trackedCalls,
                sourceBytes,
                reader.GetInt64(2),
                savedBytes,
                reader.GetInt64(4),
                ReadContextSavingsTools(connection, workspaceId),
                ComputeSavingsRatio(sourceBytes, savedBytes));
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
            : DashboardIndexFactsCache.Read(selectedWorkspace);
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

    public static DashboardWorkspaceFacts? ReadWorkspaceFacts(string registryDbPath, string workspaceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        DashboardWorkspaceRow? workspace = ReadWorkspaces(registryDbPath)
            .FirstOrDefault(row => string.Equals(row.WorkspaceId, workspaceId, StringComparison.Ordinal));
        return workspace is null ? null : DashboardIndexFactsCache.Read(workspace);
    }

    private static double? ComputeSavingsRatio(long sourceBytes, long savedBytes) =>
        sourceBytes > 0 ? (double)savedBytes / sourceBytes : null;

    public static string RenderWorkspacesJson(string registryDbPath) =>
        JsonSerializer.Serialize(ReadWorkspaces(registryDbPath), JsonContext.IReadOnlyListDashboardWorkspaceRow);

    public static string RenderTelemetryJson(string telemetryDbPath, string? workspaceId) =>
        JsonSerializer.Serialize(ReadTelemetrySummary(telemetryDbPath, workspaceId), JsonContext.DashboardTelemetrySummary);

    public static string RenderSnapshotJson(string registryDbPath, string telemetryDbPath, string? workspaceId) =>
        JsonSerializer.Serialize(ReadSnapshot(registryDbPath, telemetryDbPath, workspaceId), JsonContext.DashboardSnapshot);

    public static string RenderRefreshJson(WorkspaceRefreshResult result) =>
        JsonSerializer.Serialize(result, JsonContext.WorkspaceRefreshResult);

    public static string RenderDiagnosticsJson(DashboardRuntimeInfo runtime) =>
        JsonSerializer.Serialize(ReadDiagnostics(runtime), JsonContext.DashboardDiagnostics);

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
            // Case-insensitive on BOTH case-insensitive release targets (Windows and default macOS), matching
            // WorkspaceSafety/WorkspaceId — otherwise the dashboard fails to recognize a registered workspace
            // whose stored root differs only in case from the preferred root.
            return string.Equals(
                NormalizeRoot(left),
                NormalizeRoot(right),
                OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
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

    /// <summary>
    /// <see cref="RefreshWorkspace"/> for UI swap targets: any failure (unregistered workspace, missing
    /// extractor, scan fault) renders as a <see cref="WorkspaceRefreshStatus.Failed"/> result instead of
    /// throwing — a 500 with an empty body would leave the htmx refresh button looking dead.
    /// </summary>
    public static WorkspaceRefreshResult TryRefreshWorkspace(string registryDbPath, string toolsRoot, string workspaceId)
    {
        try
        {
            return RefreshWorkspace(registryDbPath, toolsRoot, workspaceId);
        }
        catch (Exception ex)
        {
            return new WorkspaceRefreshResult(
                WorkspaceRefreshStatus.Failed,
                workspaceId,
                WorkspaceRoot: string.Empty,
                IndexDbPath: string.Empty,
                Error: ex.Message);
        }
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

    private static bool ColumnExists(SqliteConnection connection, string tableName, string columnName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName});";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static IReadOnlyList<DashboardToolStat> ReadToolStats(
        SqliteConnection connection,
        string? workspaceId,
        bool allWorkspaces)
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
                    WHERE ($all = 1 OR latest.workspace_id IS $ws) AND latest.tool = tool_telemetry.tool
                    ORDER BY latest.ts DESC, latest.id DESC
                    LIMIT 1) AS last_outcome,
                   MAX(CASE WHEN outcome = 'error' THEN ts END) AS last_error_ts,
                   (SELECT latest_error.error_kind
                    FROM tool_telemetry latest_error
                    WHERE ($all = 1 OR latest_error.workspace_id IS $ws)
                      AND latest_error.tool = tool_telemetry.tool
                      AND latest_error.outcome = 'error'
                    ORDER BY latest_error.ts DESC, latest_error.id DESC
                    LIMIT 1) AS last_error_kind
            FROM tool_telemetry
            WHERE ($all = 1 OR workspace_id IS $ws)
            GROUP BY tool
            ORDER BY tool;
            """;
        AddScopeParameters(cmd, workspaceId, allWorkspaces);
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
                ComputeP95(connection, workspaceId, allWorkspaces, tool, calls),
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
        string? workspaceId,
        bool allWorkspaces)
    {
        string idSelect = ColumnExists(connection, "tool_telemetry", "id")
            ? "id"
            : "NULL AS id";
        string workspaceIdSelect = ColumnExists(connection, "tool_telemetry", "workspace_id")
            ? "workspace_id"
            : "NULL AS workspace_id";
        string errorMessageSelect = ColumnExists(connection, "tool_telemetry", "error_message")
            ? "error_message"
            : "NULL AS error_message";
        string errorDetailSelect = ColumnExists(connection, "tool_telemetry", "error_detail")
            ? "error_detail"
            : "NULL AS error_detail";

        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT ts, tool, op, error_kind, duration_ms,
                   {idSelect}, {workspaceIdSelect}, {errorMessageSelect}, {errorDetailSelect}
            FROM tool_telemetry
            WHERE ($all = 1 OR workspace_id IS $ws) AND outcome = 'error'
            ORDER BY ts DESC, id DESC
            LIMIT 8;
            """;
        AddScopeParameters(cmd, workspaceId, allWorkspaces);
        using var reader = cmd.ExecuteReader();
        var errors = new List<DashboardRecentError>();
        while (reader.Read())
        {
            errors.Add(new DashboardRecentError(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                WorkspaceDisplayId: null,
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }

        return errors;
    }

    private static (long TotalCalls, string? WindowStart, string? WindowEnd) ReadTotals(
        SqliteConnection connection,
        string? workspaceId,
        bool allWorkspaces)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*), MIN(ts), MAX(ts) FROM tool_telemetry WHERE ($all = 1 OR workspace_id IS $ws);";
        AddScopeParameters(cmd, workspaceId, allWorkspaces);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return (0, null, null);

        return (
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    private static long ComputeP95(
        SqliteConnection connection,
        string? workspaceId,
        bool allWorkspaces,
        string tool,
        long count)
    {
        long offset = (long)Math.Floor((count - 1) * 0.95);
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT duration_ms FROM tool_telemetry WHERE tool = $tool AND ($all = 1 OR workspace_id IS $ws) " +
            "ORDER BY duration_ms ASC LIMIT 1 OFFSET $offset;";
        cmd.Parameters.AddWithValue("$tool", tool);
        AddScopeParameters(cmd, workspaceId, allWorkspaces);
        cmd.Parameters.AddWithValue("$offset", offset);
        object? value = cmd.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static void AddScopeParameters(SqliteCommand cmd, string? workspaceId, bool allWorkspaces)
    {
        cmd.Parameters.AddWithValue("$all", allWorkspaces ? 1 : 0);
        cmd.Parameters.AddWithValue("$ws", (object?)workspaceId ?? DBNull.Value);
    }

}
