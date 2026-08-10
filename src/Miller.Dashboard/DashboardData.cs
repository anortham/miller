using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Server.Telemetry;
using Miller.Server.Tools;
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
    [property: JsonPropertyName("recent_errors")] IReadOnlyList<DashboardRecentError> RecentErrors,
    [property: JsonPropertyName("error")] string? Error = null);

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
    [property: JsonPropertyName("entries")] IReadOnlyList<DashboardActivityEntry> Entries,
    [property: JsonPropertyName("error")] string? Error = null);

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
    [property: JsonPropertyName("freshness_status")] string FreshnessStatus = "unknown",
    [property: JsonPropertyName("rebound_from_root")] string? ReboundFromRoot = null,
    [property: JsonPropertyName("rebound_from_workspace")] string? ReboundFromWorkspace = null,
    [property: JsonPropertyName("rebound_from_artifact_id")] string? ReboundFromArtifactId = null,
    [property: JsonPropertyName("rebound_at")] string? ReboundAt = null,
    [property: JsonPropertyName("store")] StoreWorkspaceFacts? Store = null,
    [property: JsonIgnore] SearchSidecarFacts? SearchFacts = null,
    [property: JsonIgnore] ContentCorpusFacts? ContentFacts = null);

public sealed record DashboardHealthWarning(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("message")] string Message);

public sealed record DashboardPatternFamily(
    [property: JsonPropertyName("family")] string Family,
    [property: JsonPropertyName("pattern_count")] int PatternCount,
    [property: JsonPropertyName("fact_count")] long FactCount,
    [property: JsonPropertyName("languages")] IReadOnlyList<string> Languages,
    [property: JsonPropertyName("captures")] IReadOnlyList<string> Captures);

public sealed record DashboardPatternInventoryPanel(
    [property: JsonPropertyName("workspace_id")] string? WorkspaceId,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("families")] IReadOnlyList<DashboardPatternFamily> Families,
    [property: JsonPropertyName("error")] string? Error = null);

public sealed record DashboardWorkspaceHealthPanel(
    [property: JsonPropertyName("workspace_id")] string? WorkspaceId,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("warnings")] IReadOnlyList<DashboardHealthWarning> Warnings,
    [property: JsonPropertyName("recommended_actions")] IReadOnlyList<string> RecommendedActions,
    [property: JsonPropertyName("leader")] string Leader,
    [property: JsonPropertyName("search_sidecar_status")] string SearchSidecarStatus,
    [property: JsonPropertyName("content_sidecar_status")] string ContentSidecarStatus,
    [property: JsonPropertyName("parse_diagnostic_count")] long ParseDiagnosticCount,
    [property: JsonPropertyName("capability_gap_count")] long CapabilityGapCount,
    [property: JsonPropertyName("error")] string? Error = null);

public sealed record DashboardOnboardingTarget(
    [property: JsonPropertyName("confidence")] string Confidence,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("kind")] string? Kind,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("line")] int? Line,
    [property: JsonPropertyName("calls")] long Calls);

public sealed record DashboardOnboardingMiss(
    [property: JsonPropertyName("tool")] string Tool,
    [property: JsonPropertyName("op")] string? Op,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("calls")] long Calls);

public sealed record DashboardWorkspaceOnboardingPanel(
    [property: JsonPropertyName("workspace_id")] string? WorkspaceId,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("total_calls")] long TotalCalls,
    [property: JsonPropertyName("start_here")] IReadOnlyList<string> StartHere,
    [property: JsonPropertyName("hot_targets")] IReadOnlyList<DashboardOnboardingTarget> HotTargets,
    [property: JsonPropertyName("common_misses")] IReadOnlyList<DashboardOnboardingMiss> CommonMisses,
    [property: JsonPropertyName("notes")] IReadOnlyList<string> Notes,
    [property: JsonPropertyName("error")] string? Error = null);

public sealed record DashboardMetricComplexityHotspot(
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("symbol_name")] string? SymbolName,
    [property: JsonPropertyName("symbol_kind")] string? SymbolKind,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("decision_count")] int DecisionCount,
    [property: JsonPropertyName("max_nesting_depth")] int MaxNestingDepth);

public sealed record DashboardMetricCloneSymbol(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("line")] int Line);

public sealed record DashboardMetricCloneGroup(
    [property: JsonPropertyName("body_hash")] string BodyHash,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("symbols")] IReadOnlyList<DashboardMetricCloneSymbol> Symbols);

public sealed record DashboardLocalMetricsPanel(
    [property: JsonPropertyName("workspace_id")] string? WorkspaceId,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("complexity_hotspots")] IReadOnlyList<DashboardMetricComplexityHotspot> ComplexityHotspots,
    [property: JsonPropertyName("clone_groups")] IReadOnlyList<DashboardMetricCloneGroup> CloneGroups,
    [property: JsonPropertyName("error")] string? Error = null);

/// <summary>
/// One metric's trend line for the workspace-detail Trends panel: the ordered per-snapshot values (already
/// downsampled to at most <c>maxPoints</c> by the store) plus its first/latest for a compact delta label. A series
/// is only present when its metric has at least one recorded point — an ABSENT metric never becomes a zero row.
/// </summary>
/// <param name="FirstRecordedAtUtc">
/// Display-only <c>recorded_at_utc</c> of the FIRST plotted point, anchoring the "since first" delta to a date. Null
/// when unknown; the panel then renders without a window line. Never used to order the series — point order is
/// <c>snapshot_id</c> per the metrics-history contract.
/// </param>
/// <param name="LatestRecordedAtUtc">Display-only <c>recorded_at_utc</c> of the LAST plotted point. Null when unknown.</param>
public sealed record DashboardTrendSeries(
    [property: JsonPropertyName("metric")] string Metric,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("points")] IReadOnlyList<double> Points,
    [property: JsonPropertyName("first")] double First,
    [property: JsonPropertyName("latest")] double Latest,
    [property: JsonPropertyName("first_recorded_at_utc")] string? FirstRecordedAtUtc = null,
    [property: JsonPropertyName("latest_recorded_at_utc")] string? LatestRecordedAtUtc = null)
{
    /// <summary>A single point cannot draw a line; the panel shows the "run miller report" hint for these.</summary>
    [JsonIgnore]
    public bool HasTrend => Points.Count >= 2;

    /// <summary>Both bounds known ⟹ the panel can render the recorded window; either missing ⟹ it renders as before.</summary>
    [JsonIgnore]
    public bool HasRecordedWindow =>
        !string.IsNullOrWhiteSpace(FirstRecordedAtUtc) && !string.IsNullOrWhiteSpace(LatestRecordedAtUtc);
}

/// <summary>
/// The workspace-detail "Trends" panel model: one <see cref="DashboardTrendSeries"/> per deterministic metric that
/// has any recorded history. Empty <see cref="Series"/> ⟹ the panel renders its empty-state line (no history.db, or
/// a history.db with none of the tracked metrics yet). <see cref="Unreadable"/> ⟹ the history.db is PRESENT but could
/// not be read; the panel renders a distinct "history unreadable" state instead of "no trend data yet" so a broken
/// sidecar never looks like a fresh one. Read-only aggregate facts — no index hydration.
/// </summary>
public sealed record DashboardWorkspaceTrendsPanel(
    [property: JsonPropertyName("workspace_id")] string? WorkspaceId,
    [property: JsonPropertyName("series")] IReadOnlyList<DashboardTrendSeries> Series,
    [property: JsonPropertyName("unreadable")] bool Unreadable = false)
{
    [JsonIgnore]
    public bool HasData => Series.Count > 0;

    public static DashboardWorkspaceTrendsPanel Empty(string? workspaceId) =>
        new(workspaceId, Array.Empty<DashboardTrendSeries>());

    /// <summary>A PRESENT-but-unreadable history.db: no series, but flagged so the panel renders an error state.</summary>
    public static DashboardWorkspaceTrendsPanel UnreadablePanel(string? workspaceId) =>
        new(workspaceId, Array.Empty<DashboardTrendSeries>(), Unreadable: true);
}

/// <summary>
/// Pure, local-first inline-SVG sparkline geometry — no external assets, no rendering framework. Given an ordered
/// value series it produces the <c>points</c> attribute for an SVG <c>&lt;polyline&gt;</c> inside a fixed viewBox,
/// normalising the values across the height (min at the bottom, max at the top; a flat series draws a mid line).
/// Kept out of the .razor so it is unit-testable in isolation.
/// </summary>
public static class DashboardSparkline
{
    public const double ViewWidth = 100d;
    public const double ViewHeight = 24d;
    private const double PadY = 2d; // keep the stroke off the top/bottom edges of the viewBox.

    /// <summary>The <c>viewBox</c> string for the sparkline SVG, e.g. <c>"0 0 100 24"</c>.</summary>
    public static string ViewBox { get; } = string.Create(
        CultureInfo.InvariantCulture, $"0 0 {ViewWidth} {ViewHeight}");

    /// <summary>
    /// Build the SVG <c>polyline</c> <c>points</c> string (space-separated <c>x,y</c> pairs) for
    /// <paramref name="values"/>. Returns an empty string for fewer than two points (no line to draw).
    /// </summary>
    public static string Points(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        int n = values.Count;
        if (n < 2)
            return string.Empty;

        double min = values[0];
        double max = values[0];
        for (int i = 1; i < n; i++)
        {
            if (values[i] < min) min = values[i];
            if (values[i] > max) max = values[i];
        }

        double range = max - min;
        double usableHeight = ViewHeight - (2 * PadY);
        var sb = new StringBuilder(n * 12);
        for (int i = 0; i < n; i++)
        {
            double x = n == 1 ? 0d : (double)i * ViewWidth / (n - 1);
            // Flat series ⟹ a centred horizontal line; otherwise invert so the max value sits at the top.
            double normalized = range <= 0d ? 0.5d : (values[i] - min) / range;
            double y = ViewHeight - PadY - (normalized * usableHeight);
            if (i > 0)
                sb.Append(' ');
            sb.Append(Coord(x)).Append(',').Append(Coord(y));
        }

        return sb.ToString();
    }

    private static string Coord(double value) =>
        Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);
}

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
        DashboardContextSavingsSummary? ContextSavings = null,
        DashboardWorkspaceHealthPanel? Health = null,
        DashboardPatternInventoryPanel? PatternInventory = null,
        DashboardWorkspaceOnboardingPanel? Onboarding = null,
        DashboardLocalMetricsPanel? LocalMetrics = null,
        DashboardWorkspaceTrendsPanel? Trends = null)
    {
        this.Workspaces = Workspaces;
        this.Telemetry = Telemetry;
        this.SelectedWorkspaceId = SelectedWorkspaceId;
        this.WorkspaceFacts = WorkspaceFacts ?? Array.Empty<DashboardWorkspaceFacts>();
        this.SelectedWorkspaceFacts = SelectedWorkspaceFacts;
        this.ContextSavings = ContextSavings ?? DashboardContextSavingsSummary.NotTracked(SelectedWorkspaceId);
        this.Health = Health;
        this.PatternInventory = PatternInventory;
        this.Onboarding = Onboarding;
        this.LocalMetrics = LocalMetrics;
        this.Trends = Trends;
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

    [JsonPropertyName("health")]
    public DashboardWorkspaceHealthPanel? Health { get; init; }

    [JsonPropertyName("pattern_inventory")]
    public DashboardPatternInventoryPanel? PatternInventory { get; init; }

    [JsonPropertyName("onboarding")]
    public DashboardWorkspaceOnboardingPanel? Onboarding { get; init; }

    [JsonPropertyName("local_metrics")]
    public DashboardLocalMetricsPanel? LocalMetrics { get; init; }

    [JsonPropertyName("trends")]
    public DashboardWorkspaceTrendsPanel? Trends { get; init; }
}

public sealed record DashboardWorkspaceIndexEntry(
    [property: JsonPropertyName("workspace")] DashboardWorkspaceRow Workspace,
    [property: JsonPropertyName("facts")] DashboardWorkspaceFacts Facts,
    [property: JsonPropertyName("root_exists")] bool RootExists,
    /// <summary>ISO timestamp of this workspace's newest recorded tool call, or null when telemetry has none.</summary>
    [property: JsonPropertyName("last_activity_ts")] string? LastActivityTs = null)
{
    /// <summary>True when the index DB was opened and its facts are real (not missing/unreadable).</summary>
    [JsonIgnore]
    public bool HasFacts => Facts.Status is not ("missing" or "unreadable");

    /// <summary>True when the root is gone or the registry row is in error — shown in the stale section.</summary>
    [JsonIgnore]
    public bool IsStale =>
        !RootExists ||
        string.Equals(Workspace.State, "error", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The counts form a partition: <c>live_count + missing_root_count + error_count == workspace_count</c>.
/// A row that is both missing-root and errored counts as missing-root only (prune is its remedy);
/// <c>error_count</c> covers errored rows whose root still exists.
/// </summary>
public sealed record DashboardWorkspaceIndex(
    [property: JsonPropertyName("entries")] IReadOnlyList<DashboardWorkspaceIndexEntry> Entries,
    [property: JsonPropertyName("workspace_count")] int WorkspaceCount,
    [property: JsonPropertyName("total_files")] long TotalFiles,
    [property: JsonPropertyName("total_symbols")] long TotalSymbols,
    [property: JsonPropertyName("language_count")] int LanguageCount,
    [property: JsonPropertyName("live_count")] int LiveCount = 0,
    [property: JsonPropertyName("missing_root_count")] int MissingRootCount = 0,
    [property: JsonPropertyName("error_count")] int ErrorCount = 0,
    [property: JsonPropertyName("error")] string? Error = null);

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
    /// When <paramref name="telemetryDbPath"/> is supplied, each entry also carries the newest recorded tool-call
    /// timestamp; a missing/unreadable telemetry DB degrades those to null rather than failing the page.
    /// </summary>
    public static DashboardWorkspaceIndex ReadIndex(string registryDbPath, string? telemetryDbPath = null)
    {
        (IReadOnlyList<DashboardWorkspaceRow> workspaces, string? registryError) = TryReadWorkspaces(registryDbPath);
        IReadOnlyDictionary<string, string> lastActivity = ReadLastActivityByWorkspace(telemetryDbPath);
        var entries = new List<DashboardWorkspaceIndexEntry>(workspaces.Count);
        long totalFiles = 0;
        long totalSymbols = 0;
        var languages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int liveCount = 0;
        int missingRootCount = 0;
        int errorCount = 0;

        foreach (DashboardWorkspaceRow workspace in workspaces)
        {
            bool rootExists = Directory.Exists(workspace.CanonicalRoot);
            DashboardWorkspaceFacts facts = DashboardIndexFactsCache.Read(workspace);
            lastActivity.TryGetValue(workspace.WorkspaceId, out string? lastActivityTs);
            var entry = new DashboardWorkspaceIndexEntry(workspace, facts, rootExists, lastActivityTs);
            entries.Add(entry);
            // Partition: live + missing_root + error == workspace_count. A row that is both
            // missing-root and errored counts as missing-root (prune is its remedy).
            if (!rootExists)
                missingRootCount++;
            else if (string.Equals(workspace.State, "error", StringComparison.OrdinalIgnoreCase))
                errorCount++;
            else
                liveCount++;
            if (entry.HasFacts)
            {
                totalFiles += facts.FileCount;
                totalSymbols += facts.SymbolCount;
                foreach (DashboardLanguageStat language in facts.Languages)
                    languages.Add(language.Language);
            }
        }

        return new DashboardWorkspaceIndex(
            entries,
            workspaces.Count,
            totalFiles,
            totalSymbols,
            languages.Count,
            liveCount,
            missingRootCount,
            errorCount,
            registryError);
    }

    /// <summary>
    /// Newest tool-call timestamp per workspace, in one grouped pass over the shared telemetry ledger.
    /// <c>tool_telemetry.ts</c> is fixed-width ISO-8601 UTC, so lexicographic MAX is chronological.
    /// Every failure mode (no path, absent file, foreign schema, corruption) degrades to an empty map:
    /// last-activity is decoration on the workspace list and must never fail the page.
    /// </summary>
    private static IReadOnlyDictionary<string, string> ReadLastActivityByWorkspace(string? telemetryDbPath)
    {
        var byWorkspace = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(telemetryDbPath) || !File.Exists(telemetryDbPath))
            return byWorkspace;

        try
        {
            using var connection = OpenReadOnly(telemetryDbPath);
            if (!TableExists(connection, "tool_telemetry"))
                return byWorkspace;

            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT workspace_id, MAX(ts) FROM tool_telemetry
                WHERE workspace_id IS NOT NULL
                GROUP BY workspace_id;
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1))
                    continue;

                byWorkspace[reader.GetString(0)] = reader.GetString(1);
            }

            return byWorkspace;
        }
        catch (Exception ex) when (
            ex is SqliteException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    public static string RenderIndexJson(string registryDbPath, string? telemetryDbPath = null) =>
        JsonSerializer.Serialize(ReadIndex(registryDbPath, telemetryDbPath), JsonContext.DashboardWorkspaceIndex);

    /// <summary>
    /// Registry rows for the page spine. A corrupt/truncated <c>workspaces.db</c> degrades to an EMPTY list
    /// instead of throwing so no dashboard page 500s; callers that surface the failure to the user
    /// (<see cref="ReadIndex"/>) use <see cref="TryReadWorkspaces"/> to also carry the error message.
    /// </summary>
    public static IReadOnlyList<DashboardWorkspaceRow> ReadWorkspaces(string registryDbPath) =>
        TryReadWorkspaces(registryDbPath).Rows;

    private static (IReadOnlyList<DashboardWorkspaceRow> Rows, string? Error) TryReadWorkspaces(string registryDbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registryDbPath);
        if (!File.Exists(registryDbPath))
            return (Array.Empty<DashboardWorkspaceRow>(), null);

        try
        {
            using var connection = OpenReadOnly(registryDbPath);
            if (!TableExists(connection, "workspaces"))
                return (Array.Empty<DashboardWorkspaceRow>(), null);

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

            return (rows, null);
        }
        catch (Exception ex) when (
            ex is SqliteException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return (Array.Empty<DashboardWorkspaceRow>(), $"Workspace registry is unreadable: {ex.Message}");
        }
    }

    /// <summary>
    /// Per-tool telemetry rollup. <paramref name="workspaceId"/> follows the content-search convention:
    /// the sentinel <c>all</c> aggregates across every workspace (workspace ids are SHA-256 hex, so the
    /// sentinel cannot collide); any other value scopes with <c>workspace_id IS $ws</c>.
    /// </summary>
    public static DashboardTelemetrySummary ReadTelemetrySummary(
        string telemetryDbPath,
        string? workspaceId,
        string? registryDbPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(telemetryDbPath);
        bool allWorkspaces = string.Equals(workspaceId, "all", StringComparison.Ordinal);
        string? scope = allWorkspaces ? null : workspaceId;
        if (!File.Exists(telemetryDbPath))
            return EmptyTelemetrySummary(workspaceId);

        try
        {
            using var connection = OpenReadOnly(telemetryDbPath);
            if (!TableExists(connection, "tool_telemetry"))
                return EmptyTelemetrySummary(workspaceId);

            var tools = ReadToolStats(connection, scope, allWorkspaces);
            (long totalCalls, string? windowStart, string? windowEnd) = ReadTotals(connection, scope, allWorkspaces);
            IReadOnlyList<DashboardRecentError> recentErrors =
                ReadRecentErrors(connection, scope, allWorkspaces, registryDbPath);
            return new DashboardTelemetrySummary(workspaceId, tools, totalCalls, windowStart, windowEnd, recentErrors);
        }
        catch (Exception ex) when (
            ex is SqliteException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            // A corrupt/truncated shared telemetry.db must not 500 the page spine — degrade to the empty shape,
            // but carry the underlying message so the panel can distinguish corruption from a healthy no-data DB.
            return EmptyTelemetrySummary(workspaceId) with { Error = $"telemetry read degraded: {ex.Message}" };
        }
    }

    private static DashboardTelemetrySummary EmptyTelemetrySummary(string? workspaceId) =>
        new(
            workspaceId,
            Array.Empty<DashboardToolStat>(),
            0,
            null,
            null,
            Array.Empty<DashboardRecentError>());

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

        try
        {
            return ReadRecentActivityCore(telemetryDbPath, registryDbPath, scope, limit);
        }
        catch (Exception ex) when (
            ex is SqliteException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            // A corrupt/truncated shared telemetry.db must not 500 the page spine — degrade to the empty feed,
            // but carry the underlying message so the panel can distinguish corruption from a healthy no-data DB.
            return new DashboardActivityFeed(
                scope,
                Array.Empty<DashboardActivityEntry>(),
                $"telemetry read degraded: {ex.Message}");
        }
    }

    private static DashboardActivityFeed ReadRecentActivityCore(
        string telemetryDbPath,
        string registryDbPath,
        string? scope,
        int limit)
    {
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

        try
        {
            using var connection = OpenReadOnly(telemetryDbPath);
            if (!TableExists(connection, "tool_telemetry"))
                return DashboardContextSavingsSummary.NotTracked(workspaceId);

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
        catch (Exception ex) when (
            ex is SqliteException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            // A corrupt/truncated shared telemetry.db (or a failed open) must not 500 the page spine.
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
        DashboardTelemetrySummary telemetry = ReadTelemetrySummary(telemetryDbPath, selectedWorkspaceId, registryDbPath);
        DashboardWorkspaceRow? selectedWorkspace = workspaces.FirstOrDefault(
            row => string.Equals(row.WorkspaceId, selectedWorkspaceId, StringComparison.Ordinal));
        bool storeEnabled = WorkspaceReadSessionFactory.StoreEnabledFromEnvironment();
        WorkspaceReadHandle? storeSession = null;
        string? storeSessionError = null;
        Exception? storeSessionFailure = null;
        if (selectedWorkspace is not null && storeEnabled)
            storeSession = TryOpenStoreReadSession(selectedWorkspace, out storeSessionError, out storeSessionFailure);

        try
        {
            DashboardWorkspaceFacts? selectedFacts = selectedWorkspace is null
                ? null
                : WithRebindProvenance(
                    storeEnabled
                        ? storeSession is null
                            ? DashboardIndexFactsReader.ReadStoreUnavailable(
                                selectedWorkspace,
                                storeSessionError,
                                storeSessionFailure)
                            : DashboardIndexFactsReader.Read(selectedWorkspace, storeSession)
                        : DashboardIndexFactsCache.Read(selectedWorkspace),
                    workspaces,
                    storeEnabled ? storeSession : null);
            IReadOnlyList<DashboardWorkspaceFacts> workspaceFacts = selectedFacts is null
                ? Array.Empty<DashboardWorkspaceFacts>()
                : new[] { selectedFacts };
            DashboardContextSavingsSummary contextSavings = ReadContextSavings(telemetryDbPath, selectedWorkspaceId);
            DashboardWorkspaceHealthPanel? health = selectedWorkspace is null || selectedFacts is null
                ? null
                : ReadWorkspaceHealthPanel(selectedWorkspace, selectedFacts, storeEnabled, storeSession);
            DashboardPatternInventoryPanel? patternInventory = selectedWorkspace is null || selectedFacts is null
                ? null
                : ReadPatternInventoryPanel(selectedWorkspace, selectedFacts, storeEnabled, storeSession);
            DashboardWorkspaceOnboardingPanel? onboarding = selectedWorkspace is null || selectedFacts is null
                ? null
                : ReadWorkspaceOnboardingPanel(selectedWorkspace, selectedFacts, telemetryDbPath, storeSession);
            DashboardLocalMetricsPanel? localMetrics = selectedWorkspace is null || selectedFacts is null
                ? null
                : ReadLocalMetricsPanel(selectedWorkspace, selectedFacts, storeEnabled, storeSession);
            DashboardWorkspaceTrendsPanel? trends = selectedWorkspace is null || selectedFacts is null
                ? null
                : DashboardIndexFactsReader.ReadTrends(selectedWorkspace);
            return new DashboardSnapshot(
                workspaces,
                telemetry,
                selectedWorkspaceId,
                workspaceFacts,
                selectedFacts,
                contextSavings,
                health,
                patternInventory,
                onboarding,
                localMetrics,
                trends);
        }
        finally
        {
            storeSession?.Dispose();
        }
    }

    private static DashboardLocalMetricsPanel? ReadLocalMetricsPanel(
        DashboardWorkspaceRow workspace,
        DashboardWorkspaceFacts dashboardFacts,
        bool storeEnabled,
        IWorkspaceReadSession? storeSession)
    {
        try
        {
            if (storeEnabled && storeSession is null)
                return UnavailableLocalMetrics(workspace, dashboardFacts.Message);
            if (storeEnabled && IndexLevels.IsSymbolsLevel(storeSession!.Snapshot.IndexLevel))
                return UnavailableLocalMetrics(workspace, "local metrics require a full-level family-store view");

            IReadOnlyList<ComplexityHotspot> hotspots = storeEnabled
                ? ComplexityRankingReader.Read(
                    storeSession!,
                    limit: 5,
                    minSeverity: ComplexitySeverity.Moderate,
                    includeTests: false)
                : ComplexityRankingReader.Read(
                    workspace.IndexDbPath,
                    limit: 5,
                    minSeverity: ComplexitySeverity.Moderate,
                    includeTests: false);
            IReadOnlyList<CloneGroup> cloneGroups = storeEnabled
                ? CloneGroupReader.Read(
                    storeSession!,
                    limit: 5,
                    minCount: 2,
                    symbolsPerGroup: 3)
                : CloneGroupReader.Read(
                    workspace.IndexDbPath,
                    limit: 5,
                    minCount: 2,
                    symbolsPerGroup: 3);

            return new DashboardLocalMetricsPanel(
                workspace.WorkspaceId,
                "ready",
                hotspots.Select(static hotspot => new DashboardMetricComplexityHotspot(
                    ComplexityRankingReader.SeverityName(hotspot.Severity),
                    hotspot.SymbolName,
                    hotspot.SymbolKind,
                    hotspot.Path,
                    hotspot.StartLine,
                    hotspot.DecisionCount,
                    hotspot.MaxNestingDepth)).ToArray(),
                cloneGroups.Select(static group => new DashboardMetricCloneGroup(
                    group.BodyHash,
                    group.Count,
                    group.Symbols.Select(static symbol => new DashboardMetricCloneSymbol(
                        symbol.Name,
                        symbol.Kind,
                        symbol.Path,
                        symbol.Line)).ToArray())).ToArray());
        }
        catch (Exception ex) when (
            ex is KeyNotFoundException or SqliteException or IOException or InvalidOperationException
                or UnauthorizedAccessException or IncompatibleExtractException)
        {
            return new DashboardLocalMetricsPanel(
                workspace.WorkspaceId,
                "unavailable",
                Array.Empty<DashboardMetricComplexityHotspot>(),
                Array.Empty<DashboardMetricCloneGroup>(),
                Error: ex.Message);
        }
    }

    private static DashboardPatternInventoryPanel? ReadPatternInventoryPanel(
        DashboardWorkspaceRow workspace,
        DashboardWorkspaceFacts dashboardFacts,
        bool storeEnabled,
        IWorkspaceReadSession? storeSession)
    {
        try
        {
            WorkspaceExtractionHealthFacts extraction = storeEnabled
                ? storeSession is null
                    ? UnavailableExtraction(dashboardFacts.Message ?? dashboardFacts.FreshnessStatus)
                    : WorkspaceHealthReader.Read(storeSession)
                : ReadExtractionHealthOrUnavailable(
                    workspace.IndexDbPath,
                    dashboardFacts.Message ?? dashboardFacts.FreshnessStatus);
            if (!extraction.StructuralFacts.Available)
            {
                return new DashboardPatternInventoryPanel(
                    workspace.WorkspaceId,
                    "unavailable",
                    Array.Empty<DashboardPatternFamily>(),
                    Error: extraction.StructuralFacts.Error);
            }

            IReadOnlyList<DashboardPatternFamily> families = extraction.StructuralFacts.Rows
                .GroupBy(row => PatternFamilyName(row.PatternId), StringComparer.Ordinal)
                .OrderByDescending(static group => group.Sum(static row => row.Count))
                .ThenBy(static group => group.Key, StringComparer.Ordinal)
                .Select(static group => new DashboardPatternFamily(
                    group.Key,
                    PatternCount: group.Select(static row => row.PatternId).Distinct(StringComparer.Ordinal).Count(),
                    FactCount: group.Sum(static row => row.Count),
                    Languages: group.Select(static row => row.Language).Distinct(StringComparer.Ordinal).OrderBy(static value => value, StringComparer.Ordinal).ToArray(),
                    Captures: group.Select(static row => row.CaptureName).Distinct(StringComparer.Ordinal).OrderBy(static value => value, StringComparer.Ordinal).ToArray()))
                .ToArray();

            return new DashboardPatternInventoryPanel(
                workspace.WorkspaceId,
                families.Count == 0 ? "empty" : "ready",
                families);
        }
        catch (Exception ex) when (
            ex is KeyNotFoundException or SqliteException or IOException or InvalidOperationException
                or UnauthorizedAccessException or IncompatibleExtractException)
        {
            return new DashboardPatternInventoryPanel(
                workspace.WorkspaceId,
                "unavailable",
                Array.Empty<DashboardPatternFamily>(),
                Error: ex.Message);
        }
    }

    private static string PatternFamilyName(string patternId)
    {
        int lastDot = patternId.LastIndexOf('.');
        if (lastDot <= 0)
            return patternId;

        ReadOnlySpan<char> suffix = patternId.AsSpan(lastDot + 1);
        if (suffix.Length > 1 && suffix[0] == 'v' && IsVersionDigitSuffix(suffix[1..]))
            return patternId[..lastDot];

        return patternId;
    }

    private static bool IsVersionDigitSuffix(ReadOnlySpan<char> suffix)
    {
        foreach (char c in suffix)
        {
            if (!char.IsAsciiDigit(c))
                return false;
        }

        return suffix.Length > 0;
    }

    private static DashboardWorkspaceHealthPanel? ReadWorkspaceHealthPanel(
        DashboardWorkspaceRow workspace,
        DashboardWorkspaceFacts dashboardFacts,
        bool storeEnabled,
        IWorkspaceReadSession? storeSession)
    {
        try
        {
            WorkspaceFacts facts = BuildWorkspaceFacts(workspace, dashboardFacts);
            WorkspaceExtractionHealthFacts extraction = storeEnabled
                ? storeSession is null
                    ? UnavailableExtraction(facts.WarningText ?? facts.FreshnessStatus)
                    : WorkspaceHealthReader.Read(storeSession)
                : ReadExtractionHealthOrUnavailable(
                    workspace.IndexDbPath,
                    facts.WarningText ?? facts.FreshnessStatus);
            LeaderHealthFacts leader = LeaderHealthFacts.Read(Path.GetDirectoryName(workspace.IndexDbPath) ?? workspace.CanonicalRoot) with
            {
                ArtifactExtractorVersion = dashboardFacts.ExtractorVersion,
            };
            WorkspaceHealthFacts health = WorkspaceHealthFacts.Create(
                facts,
                TelemetrySummary.Empty,
                new TelemetryHealthFacts(0, 0, 0),
                extraction,
                leader);
            return new DashboardWorkspaceHealthPanel(
                facts.WorkspaceId,
                WorkspaceHealthFacts.StateName(health.State),
                health.Summary,
                health.Warnings.Select(static warning =>
                    new DashboardHealthWarning(warning.Code, warning.Severity, warning.Message)).ToArray(),
                health.RecommendedActions,
                DashboardLeaderLabel(facts, leader),
                facts.SearchSidecar?.State ?? "unknown",
                facts.ContentCorpus?.State ?? "unknown",
                extraction.ParseDiagnostics.Rows.Sum(static row => row.Count),
                extraction.CapabilityGaps.Rows
                    .Where(static row => string.Equals(row.Status, "open", StringComparison.OrdinalIgnoreCase))
                    .Sum(static row => row.Count),
                Error: null);
        }
        catch (Exception ex) when (
            ex is KeyNotFoundException or SqliteException or IOException or InvalidOperationException
                or UnauthorizedAccessException or IncompatibleExtractException)
        {
            return new DashboardWorkspaceHealthPanel(
                workspace.WorkspaceId,
                "unavailable",
                "workspace health is unavailable",
                Array.Empty<DashboardHealthWarning>(),
                Array.Empty<string>(),
                "unknown",
                "unknown",
                "unknown",
                ParseDiagnosticCount: 0,
                CapabilityGapCount: 0,
                Error: ex.Message);
        }
    }

    private static DashboardWorkspaceOnboardingPanel? ReadWorkspaceOnboardingPanel(
        DashboardWorkspaceRow workspace,
        DashboardWorkspaceFacts dashboardFacts,
        string telemetryDbPath,
        IWorkspaceReadSession? storeSession)
    {
        try
        {
            WorkspaceFacts facts = BuildWorkspaceFacts(workspace, dashboardFacts);
            TelemetryOnboardingFacts telemetry = TelemetryOnboardingReader.Read(telemetryDbPath, workspace.WorkspaceId);
            IReadOnlyList<RecoveredTargetHash> targets = storeSession is not null
                ? ResolveDashboardTargets(storeSession, telemetry.TargetHashes)
                : dashboardFacts.Store is not null
                    ? UnresolvedDashboardTargets(telemetry.TargetHashes)
                    : ResolveDashboardTargets(workspace.IndexDbPath, telemetry.TargetHashes);
            WorkspaceOnboardingFacts onboarding = WorkspaceOnboardingFacts.Create(
                facts,
                telemetry,
                targets);
            return new DashboardWorkspaceOnboardingPanel(
                facts.WorkspaceId,
                onboarding.Telemetry.State,
                onboarding.Telemetry.TotalCalls,
                onboarding.StartHere,
                onboarding.HotTargets.Select(static target => new DashboardOnboardingTarget(
                    target.Confidence,
                    target.Name,
                    target.Kind,
                    target.Path,
                    target.StartLine,
                    target.Calls)).ToArray(),
                onboarding.Telemetry.CommonMisses.Select(static miss => new DashboardOnboardingMiss(
                    miss.Tool,
                    miss.Op,
                    miss.Reason,
                    miss.Calls)).ToArray(),
                onboarding.InstructionNotes,
                onboarding.Telemetry.Error);
        }
        catch (Exception ex) when (
            ex is KeyNotFoundException or SqliteException or IOException or InvalidOperationException
                or UnauthorizedAccessException or IncompatibleExtractException)
        {
            return new DashboardWorkspaceOnboardingPanel(
                workspace.WorkspaceId,
                "unavailable",
                TotalCalls: 0,
                Array.Empty<string>(),
                Array.Empty<DashboardOnboardingTarget>(),
                Array.Empty<DashboardOnboardingMiss>(),
                Array.Empty<string>(),
                Error: ex.Message);
        }
    }

    /// <summary>
    /// Enriches the DETAIL view's facts with the artifact's rebind provenance. An artifact that was never
    /// rebound returns the facts unchanged, so the detail panel renders exactly as before. The source root is
    /// resolved to a display id against the registry rows this snapshot already read — no extra registry open,
    /// and no index hydration.
    /// </summary>
    private static DashboardWorkspaceFacts WithRebindProvenance(
        DashboardWorkspaceFacts facts,
        IReadOnlyList<DashboardWorkspaceRow> workspaces,
        IWorkspaceReadSession? storeSession = null)
    {
        RebindProvenanceMetadata? provenance = facts.Store is not null
            ? storeSession is null ? null : RebindProvenanceReader.ReadSession(storeSession)
            : RebindProvenanceReader.Read(facts.IndexDbPath);
        if (provenance is null)
            return facts;

        return facts with
        {
            ReboundFromRoot = provenance.SourceRoot,
            ReboundFromWorkspace = workspaces
                .FirstOrDefault(row => ArtifactRootIdentity.Matches(row.CanonicalRoot, provenance.SourceRoot))
                ?.DisplayId,
            ReboundFromArtifactId = provenance.SourceArtifactId,
            ReboundAt = provenance.ReboundAt,
        };
    }

    internal static WorkspaceFacts BuildWorkspaceFacts(
        DashboardWorkspaceRow workspace,
        DashboardWorkspaceFacts dashboardFacts)
    {
        long expectedRevision = dashboardFacts.IndexRevision ?? dashboardFacts.LastRevision ?? 0L;
        SearchSidecarFacts searchSidecar = dashboardFacts.SearchFacts
            ?? (dashboardFacts.Store is null
                ? SymbolSearchSidecar.FromEnvironment().Inspect(workspace.IndexDbPath, expectedRevision)
                : UnavailableSearchSidecar(dashboardFacts.Store.Error ?? dashboardFacts.Message, expectedRevision));
        ContentCorpusFacts contentCorpus = dashboardFacts.ContentFacts
            ?? (dashboardFacts.Store is null
                ? new ContentCorpusSidecar().Inspect(workspace.IndexDbPath, expectedRevision)
                : UnavailableContentSidecar(dashboardFacts.Store.Error ?? dashboardFacts.Message));
        string freshnessStatus = dashboardFacts.Status switch
        {
            "missing" => "missing_index",
            "unreadable" => "unreadable_index",
            _ => dashboardFacts.FreshnessStatus,
        };

        return new WorkspaceFacts(
            Root: workspace.CanonicalRoot,
            WorkspaceId: workspace.WorkspaceId,
            DbPath: workspace.IndexDbPath,
            IsLeader: false,
            DocumentCount: dashboardFacts.SymbolCount,
            KnownExtensionsCount: SafeInt(dashboardFacts.LanguageCount),
            BuiltRevision: expectedRevision,
            LatestObservedRevision: dashboardFacts.LastRevision ?? expectedRevision,
            IndexFresh: DashboardIndexFresh(workspace, dashboardFacts),
            QueueEmpty: true,
            ArtifactId: dashboardFacts.ArtifactId,
            FreshnessStatus: freshnessStatus,
            WarningText: dashboardFacts.Message ?? dashboardFacts.RegistryLastError,
            DisplayId: workspace.DisplayId,
            SearchSidecar: searchSidecar,
            ContentCorpus: contentCorpus,
            RebindProvenance: dashboardFacts.ReboundFromRoot is { Length: > 0 } reboundFromRoot
                ? new RebindProvenanceFacts(
                    reboundFromRoot,
                    dashboardFacts.ReboundFromWorkspace,
                    dashboardFacts.ReboundFromArtifactId,
                    dashboardFacts.ReboundAt)
                : null,
            Store: dashboardFacts.Store);
    }

    private static IReadOnlyList<RecoveredTargetHash> ResolveDashboardTargets(
        string indexDbPath,
        IReadOnlyList<TargetHashFrequency> targetHashes)
        => ResolveDashboardTargets(
            targetHashes,
            () => WorkspaceTargetHashResolver.Resolve(indexDbPath, targetHashes));

    private static IReadOnlyList<RecoveredTargetHash> ResolveDashboardTargets(
        IWorkspaceReadSession session,
        IReadOnlyList<TargetHashFrequency> targetHashes)
        => ResolveDashboardTargets(
            targetHashes,
            () => WorkspaceTargetHashResolver.Resolve(session, targetHashes));

    private static IReadOnlyList<RecoveredTargetHash> ResolveDashboardTargets(
        IReadOnlyList<TargetHashFrequency> targetHashes,
        Func<IReadOnlyList<RecoveredTargetHash>> resolver)
    {
        if (targetHashes.Count == 0)
            return [];

        try
        {
            return resolver();
        }
        catch (Exception ex) when (
            ex is FileNotFoundException or SqliteException or IOException or InvalidOperationException
                or UnauthorizedAccessException)
        {
            return UnresolvedDashboardTargets(targetHashes);
        }
    }

    private static IReadOnlyList<RecoveredTargetHash> UnresolvedDashboardTargets(
        IReadOnlyList<TargetHashFrequency> targetHashes) =>
        targetHashes
            .Select(static hash => new RecoveredTargetHash(
                Confidence: "unresolved_hash",
                SymbolId: null,
                Name: null,
                Kind: null,
                Path: null,
                StartLine: null,
                Calls: hash.Calls,
                CandidateCount: 0))
            .ToArray();

    private static bool? DashboardIndexFresh(DashboardWorkspaceRow workspace, DashboardWorkspaceFacts facts)
    {
        if (!string.IsNullOrWhiteSpace(workspace.LastError) ||
            facts.Status is "missing" or "unreadable" or "error")
            return false;

        if (facts.IndexRevision is { } indexRevision &&
            facts.LastRevision is { } registryRevision &&
            indexRevision > 0 &&
            registryRevision != indexRevision)
            return false;

        if (facts.IndexRevision is null && facts.LastRevision is null)
            return null;

        return true;
    }

    private static int SafeInt(long value) =>
        value >= int.MaxValue ? int.MaxValue : value <= int.MinValue ? int.MinValue : (int)value;

    private static WorkspaceExtractionHealthFacts ReadExtractionHealthOrUnavailable(string indexDbPath, string? error)
    {
        try
        {
            return WorkspaceHealthReader.Read(indexDbPath);
        }
        // IncompatibleExtractException deliberately NOT caught: the schema-gate message must reach the
        // panel-level catch (state "unavailable" + rebuild guidance in Error), not sink into section warnings.
        catch (Exception ex) when (
            ex is FileNotFoundException or SqliteException or InvalidOperationException)
        {
            string message = string.IsNullOrWhiteSpace(error) ? ex.Message : error;
            return UnavailableExtraction(message);
        }
    }

    private static WorkspaceExtractionHealthFacts UnavailableExtraction(string? message)
    {
        string error = message ?? "workspace extraction health is unavailable";
        return new(
            HealthFactSection<ParseDiagnosticGroup>.Unavailable(error),
            HealthFactSection<CapabilityGapGroup>.Unavailable(error),
            HealthFactSection<LanguageCapabilitySummary>.Unavailable(error),
            HealthFactSection<StructuralFactGroup>.Unavailable(error),
            HealthFactSection<ComplexityMetricGroup>.Unavailable(error),
            HealthFactSection<FileStatusGroup>.Unavailable(error));
    }

    private static DashboardLocalMetricsPanel UnavailableLocalMetrics(
        DashboardWorkspaceRow workspace,
        string? error) =>
        new(
            workspace.WorkspaceId,
            "unavailable",
            Array.Empty<DashboardMetricComplexityHotspot>(),
            Array.Empty<DashboardMetricCloneGroup>(),
            Error: error ?? "the family-store read session is unavailable");

    private static SearchSidecarFacts UnavailableSearchSidecar(string? error, long expectedRevision) =>
        new(
            "unavailable",
            null,
            null,
            expectedRevision,
            null,
            error ?? "the family-store search sidecar facts are unavailable");

    private static ContentCorpusFacts UnavailableContentSidecar(string? error) =>
        new(
            "unavailable",
            null,
            null,
            null,
            0,
            0,
            0,
            0,
            Error: error ?? "the family-store content sidecar facts are unavailable");

    private static WorkspaceReadHandle? TryOpenStoreReadSession(
        DashboardWorkspaceRow workspace,
        out string? error,
        out Exception? failure)
    {
        error = null;
        failure = null;
        try
        {
            return WorkspaceReadSessionFactory.Open(
                workspace.IndexDbPath,
                workspace.CanonicalRoot,
                workspace.WorkspaceId,
                storeEnabled: true);
        }
        catch (Exception ex) when (
            ex is IOException or SqliteException or InvalidOperationException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException)
        {
            error = ex.Message;
            failure = ex;
            return null;
        }
    }

    private static string DashboardLeaderLabel(WorkspaceFacts facts, LeaderHealthFacts leader)
    {
        if (facts.IsLeader)
            return "this dashboard process";
        if (leader.Identity is null)
            return "unknown";
        string liveness = leader.Alive == false ? "not running" : "alive";
        string extractor = leader.Identity.ExtractorVersion is { } version ? $" extractor {version}" : string.Empty;
        return $"pid {leader.Identity.Pid.ToString(CultureInfo.InvariantCulture)} {liveness}{extractor}";
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

    public static string RenderTelemetryJson(string telemetryDbPath, string? workspaceId, string? registryDbPath = null) =>
        JsonSerializer.Serialize(
            ReadTelemetrySummary(telemetryDbPath, workspaceId, registryDbPath),
            JsonContext.DashboardTelemetrySummary);

    public static string RenderSnapshotJson(
        string registryDbPath,
        string telemetryDbPath,
        string? workspaceId,
        string? preferredWorkspaceRoot = null) =>
        JsonSerializer.Serialize(
            ReadSnapshot(registryDbPath, telemetryDbPath, workspaceId, preferredWorkspaceRoot),
            JsonContext.DashboardSnapshot);

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

        // Never echo an unresolved request back as "selected": the /workspace endpoint detects a bad
        // id by comparing it against the selection, and an echo would mask the miss on an empty registry.
        return workspaces.Count == 0 ? null : workspaces[0].WorkspaceId;
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

        try
        {
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
        catch (Exception ex) when (
            ex is SqliteException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            // A corrupt/truncated shared telemetry.db must not 500 the /workspace page: fall back to the
            // registry-order default selection (ReadSnapshot picks workspaces[0]).
            return null;
        }
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
        var refresh = new CrossWorkspaceRefreshService(
            registry, runner, sidecar, DashboardScanGovernor(registryDbPath));
        return refresh.Refresh(workspaceId, bypassBackoff: true);
    }

    // The dashboard is one more scan source on this machine, so it queues behind the same user-global admission
    // every other Miller process does; the miller home is the registry's own directory, which honors
    // MILLER_REGISTRY_DB exactly as DashboardPaths resolves it. Cached per home because ScanGovernor holds a
    // ThreadLocal that reserves a slot for the instance's lifetime, and this runs on every dashboard refresh
    // inside a long-lived ASP.NET process.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ScanGovernor>
        DashboardScanGovernors = new(StringComparer.Ordinal);

    private static ScanGovernor DashboardScanGovernor(string registryDbPath)
    {
        string home = Path.GetDirectoryName(registryDbPath)
            ?? throw new InvalidOperationException($"Registry path '{registryDbPath}' has no parent directory.");
        return DashboardScanGovernors.GetOrAdd(home, static key => ScanGovernor.FromEnvironment(key));
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
        cmd.CommandText = allWorkspaces
            ? """
              SELECT tool,
                     COUNT(*) AS calls,
                     AVG(duration_ms) AS avg_ms,
                     MAX(duration_ms) AS max_ms,
                     SUM(CASE WHEN outcome = 'error' THEN 1 ELSE 0 END) AS errors,
                     COALESCE(SUM(est_tokens), 0) AS sum_tokens,
                     MAX(ts) AS last_call_ts,
                     (SELECT latest.outcome
                      FROM tool_telemetry latest
                      WHERE latest.tool = tool_telemetry.tool
                      ORDER BY latest.ts DESC, latest.id DESC
                      LIMIT 1) AS last_outcome,
                     MAX(CASE WHEN outcome = 'error' THEN ts END) AS last_error_ts,
                     (SELECT latest_error.error_kind
                      FROM tool_telemetry latest_error
                      WHERE latest_error.tool = tool_telemetry.tool
                        AND latest_error.outcome = 'error'
                      ORDER BY latest_error.ts DESC, latest_error.id DESC
                      LIMIT 1) AS last_error_kind
              FROM tool_telemetry
              GROUP BY tool
              ORDER BY tool;
              """
            : """
              SELECT tool,
                     COUNT(*) AS calls,
                     AVG(duration_ms) AS avg_ms,
                     MAX(duration_ms) AS max_ms,
                     SUM(CASE WHEN outcome = 'error' THEN 1 ELSE 0 END) AS errors,
                     COALESCE(SUM(est_tokens), 0) AS sum_tokens,
                     MAX(ts) AS last_call_ts,
                     (SELECT latest.outcome
                      FROM tool_telemetry latest
                      WHERE latest.workspace_id IS $ws
                        AND latest.tool = tool_telemetry.tool
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
        if (!allWorkspaces)
            cmd.Parameters.AddWithValue("$ws", (object?)workspaceId ?? DBNull.Value);

        // One extra grouped pass over the window computes EVERY tool's p95 (was an N+1: one ordered
        // ORDER BY duration_ms LIMIT 1 OFFSET n query per tool row). Total telemetry queries for the summary
        // are now bounded (this grouped stats query + one duration scan), independent of tool count.
        IReadOnlyDictionary<string, long> p95ByTool = ComputeP95ByTool(connection, workspaceId, allWorkspaces);

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
                p95ByTool.TryGetValue(tool, out long p95) ? p95 : 0,
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

    /// <summary>
    /// Per-tool p95 latency in ONE pass over the window's rows, replacing the former per-tool
    /// <c>ComputeP95</c> query loop. Preserves the exact semantics of the old
    /// <c>ORDER BY duration_ms ASC LIMIT 1 OFFSET floor((count-1)*0.95)</c>: rows are read ordered by
    /// <c>(tool, duration_ms ASC)</c> — so within each tool NULL durations sort first and values ascend,
    /// identical to the old per-tool query — and the p95 is the value at 0-based index
    /// <c>floor((count-1)*0.95)</c> (a NULL there degrades to 0, matching the old scalar read). Ordering is
    /// value-based, so ties need no secondary key: the duration at a given offset is deterministic regardless
    /// of row order within equal-duration runs.
    /// </summary>
    private static IReadOnlyDictionary<string, long> ComputeP95ByTool(
        SqliteConnection connection,
        string? workspaceId,
        bool allWorkspaces)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = allWorkspaces
            ? "SELECT tool, duration_ms FROM tool_telemetry ORDER BY tool, duration_ms ASC;"
            : "SELECT tool, duration_ms FROM tool_telemetry WHERE workspace_id IS $ws ORDER BY tool, duration_ms ASC;";
        if (!allWorkspaces)
            cmd.Parameters.AddWithValue("$ws", (object?)workspaceId ?? DBNull.Value);

        var durations = new Dictionary<string, List<long?>>(StringComparer.Ordinal);
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                string tool = reader.GetString(0);
                long? duration = reader.IsDBNull(1) ? null : reader.GetInt64(1);
                if (!durations.TryGetValue(tool, out List<long?>? ordered))
                {
                    ordered = new List<long?>();
                    durations[tool] = ordered;
                }

                ordered.Add(duration);
            }
        }

        var p95 = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach ((string tool, List<long?> ordered) in durations)
        {
            long offset = (long)Math.Floor((ordered.Count - 1) * 0.95);
            long? value = ordered[(int)offset];
            p95[tool] = value ?? 0;
        }

        return p95;
    }

    private static IReadOnlyList<DashboardRecentError> ReadRecentErrors(
        SqliteConnection connection,
        string? workspaceId,
        bool allWorkspaces,
        string? registryDbPath)
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
        cmd.CommandText = allWorkspaces
            ? $"""
              SELECT ts, tool, op, error_kind, duration_ms,
                     {idSelect}, {workspaceIdSelect}, {errorMessageSelect}, {errorDetailSelect}
              FROM tool_telemetry
              WHERE outcome = 'error'
              ORDER BY ts DESC, id DESC
              LIMIT 8;
              """
            : $"""
              SELECT ts, tool, op, error_kind, duration_ms,
                     {idSelect}, {workspaceIdSelect}, {errorMessageSelect}, {errorDetailSelect}
              FROM tool_telemetry
              WHERE workspace_id IS $ws AND outcome = 'error'
              ORDER BY ts DESC, id DESC
              LIMIT 8;
              """;
        if (!allWorkspaces)
            cmd.Parameters.AddWithValue("$ws", (object?)workspaceId ?? DBNull.Value);
        using var reader = cmd.ExecuteReader();
        var rows = new List<(string? WorkspaceId, DashboardRecentError Error)>();
        while (reader.Read())
        {
            string? rowWorkspaceId = reader.IsDBNull(6) ? null : reader.GetString(6);
            rows.Add((rowWorkspaceId, new DashboardRecentError(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                rowWorkspaceId,
                WorkspaceDisplayId: null,
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8))));
        }

        // Resolve the registered display id for each errored workspace so machine-wide errors can name which
        // workspace faulted. The registry path is threaded explicitly from the caller (ReadSnapshot / the
        // endpoints already carry both paths); a null path — or a missing/corrupt registry, which
        // ReadWorkspaces degrades safely to an empty list — leaves display ids null. Per the contract, an
        // UNREGISTERED id also stays null (not the raw id).
        IReadOnlyDictionary<string, string> displayIds =
            rows.Count == 0 || string.IsNullOrWhiteSpace(registryDbPath)
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : ReadWorkspaces(registryDbPath).ToDictionary(
                    row => row.WorkspaceId,
                    row => row.DisplayId,
                    StringComparer.Ordinal);

        var errors = new List<DashboardRecentError>(rows.Count);
        foreach ((string? rowWorkspaceId, DashboardRecentError error) in rows)
        {
            string? displayId = rowWorkspaceId is not null
                && displayIds.TryGetValue(rowWorkspaceId, out string? resolved)
                ? resolved
                : null;
            errors.Add(error with { WorkspaceDisplayId = displayId });
        }

        return errors;
    }

    private static (long TotalCalls, string? WindowStart, string? WindowEnd) ReadTotals(
        SqliteConnection connection,
        string? workspaceId,
        bool allWorkspaces)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = allWorkspaces
            ? "SELECT COUNT(*), MIN(ts), MAX(ts) FROM tool_telemetry;"
            : "SELECT COUNT(*), MIN(ts), MAX(ts) FROM tool_telemetry WHERE workspace_id IS $ws;";
        if (!allWorkspaces)
            cmd.Parameters.AddWithValue("$ws", (object?)workspaceId ?? DBNull.Value);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return (0, null, null);

        return (
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

}
