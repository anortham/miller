using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Miller.Indexing;
using Miller.Server.Telemetry;

namespace Miller.Server.Tools;

/// <summary>
/// The assembled facts for a <c>workspace status</c> / <c>list</c> view (M7 decision-2): the workspace identity,
/// the in-memory index facts, and the freshness signals. The tool gathers these from its live singletons
/// (<c>IndexHolder</c>, <c>WorkspaceContext</c>, <c>IndexerService</c>, <c>FreshnessService</c>,
/// <c>IndexFreshProbe</c>) and hands them to the PURE <see cref="WorkspaceRender"/>; the telemetry breakdown
/// rides alongside as a <see cref="TelemetrySummary"/>.
/// </summary>
/// <param name="Root">The served workspace root (the CWD Miller was launched in).</param>
/// <param name="WorkspaceId">Miller's stable workspace id (SHA-256 of the canonical root), or null if not yet known.</param>
/// <param name="DbPath">The julie extract DB path Miller reads.</param>
/// <param name="IsLeader">Whether THIS instance holds the writer lock (runs the watcher/extract writes).</param>
/// <param name="DocumentCount">Indexed symbol count of the live index.</param>
/// <param name="KnownExtensionsCount">Distinct file-extension count (the cross-language "languages indexed" proxy).</param>
/// <param name="BuiltRevision">The <c>extraction_revisions</c> revision the held index was built from.</param>
/// <param name="LatestObservedRevision">The latest revision the freshness poll has observed.</param>
/// <param name="IndexFresh">The coarse <c>index_fresh</c> probe (built==latest AND queue empty); null = unknown.</param>
/// <param name="QueueEmpty">Whether the leader's watcher queue holds no pending events (vacuously true on a reader).</param>
/// <param name="DisplayId">Human-sized selector shown in compact output, when known from the registry.</param>
/// <param name="ServerVersion">The version of the Miller binary that produced this status (build-identity
/// signal), or null when not surfaced. Always THIS process's version — the responder — regardless of which
/// workspace's facts are shown.</param>
/// <param name="ServerProcessId">The OS process id of the Miller process that produced this status, or null when
/// not surfaced. Useful when verifying that a restarted stdio MCP server is actually a new process.</param>
/// <param name="SearchSidecar">Status of the Miller-owned <c>search.db</c> sidecar, when known.</param>
/// <param name="ContentCorpus">Status of the Miller-owned <c>content.db</c> sidecar, when known.</param>
public readonly record struct WorkspaceFacts(
    string Root,
    string? WorkspaceId,
    string DbPath,
    bool IsLeader,
    long DocumentCount,
    int KnownExtensionsCount,
    long BuiltRevision,
    long LatestObservedRevision,
    bool? IndexFresh,
    bool QueueEmpty,
    string? FreshnessStatus = null,
    string? WarningText = null,
    string? DisplayId = null,
    string? ServerVersion = null,
    int? ServerProcessId = null,
    SearchSidecarFacts? SearchSidecar = null,
    ContentCorpusFacts? ContentCorpus = null);

/// <summary>A registry-backed row rendered by <c>workspace list</c>.</summary>
public readonly record struct WorkspaceListEntry(
    string WorkspaceId,
    string DisplayId,
    string Root,
    string DbPath,
    string State,
    long? LastRevision,
    bool Current,
    string? LastError);

/// <summary>
/// The result of a <c>refresh</c>/<c>full</c> action (M7 decision-3): whether the leader ran a scan, whether the
/// freshness poll swapped a newer index in, the revision the index now reflects, and an optional HONEST note (set
/// when a non-leader could not force a scan, or the leader's scan failed) — never a faked success.
/// </summary>
/// <param name="Operation">The operation name (<c>refresh</c> or <c>full</c>).</param>
/// <param name="Scanned">True iff THIS instance (the leader) ran an <c>extract scan</c>.</param>
/// <param name="Swapped">True iff the on-demand freshness poll rebuilt + swapped a newer index.</param>
/// <param name="Revision">The revision the held index reflects after the action.</param>
/// <param name="Note">An honesty note (non-leader cannot force a rescan / a scan failure), or null.</param>
public readonly record struct WorkspaceActionResult(
    string Operation,
    bool Scanned,
    bool Swapped,
    long Revision,
    string? Note,
    string? WorkspaceId = null,
    string? Root = null,
    string? Status = null,
    bool? IndexFresh = null,
    SearchSidecarFacts? SearchSidecar = null,
    ContentCorpusFacts? ContentCorpus = null);

/// <summary>The result of starting or reusing the local loopback dashboard from the <c>workspace</c> tool.</summary>
internal readonly record struct WorkspaceDashboardResult(
    string Status,
    bool Success,
    string Url,
    int? ProcessId,
    string? Message);

/// <summary>
/// The result of an <c>open(path)</c> prime (M7 decision-1): an <c>extract scan</c> ran AT <paramref name="Path"/>
/// so a future Miller launched there has a warm index. NOT a live switch — the served index/watcher/telemetry
/// stay bound to the CWD they bootstrapped against; the renderer says so explicitly.
/// </summary>
/// <param name="Path">The primed workspace root.</param>
/// <param name="DbPath">The extract DB written under <paramref name="Path"/>'s <c>.miller</c>.</param>
/// <param name="SymbolsExtracted">Symbols julie extracted during the prime scan.</param>
/// <param name="Revision">The revision the prime scan produced.</param>
public readonly record struct WorkspaceOpenResult(
    string Path,
    string DbPath,
    long SymbolsExtracted,
    long Revision,
    string? WorkspaceId = null,
    string? DisplayId = null,
    string? WarningText = null);

/// <summary>
/// The result of a <c>remove(path)</c> (M7 decision-1/8): the <c>.miller</c> index dir was deleted, the deletion
/// was REFUSED because the path is the live workspace (in use), or there was no <c>.miller</c> dir to remove
/// (not an error — a clean no-op). <see cref="MillerDir"/> is the resolved <c>.miller</c> path the result concerns.
/// </summary>
public readonly record struct WorkspaceRemoveResult(
    WorkspaceRemoveResult.Outcome Result,
    string MillerDir,
    string? WorkspaceId = null,
    string? Root = null)
{
    /// <summary>The three honest outcomes of a remove.</summary>
    public enum Outcome
    {
        /// <summary>The <c>.miller</c> index dir was deleted.</summary>
        Removed,

        /// <summary>Refused: the path is the workspace this process is serving (the index is in use).</summary>
        RefusedLive,

        /// <summary>Refused: another process holds the target workspace writer lock.</summary>
        RefusedInUse,

        /// <summary>No <c>.miller</c> dir existed at the path — nothing to remove (a clean no-op, not an error).</summary>
        NotFound,
    }

    /// <summary>The <c>.miller</c> dir was deleted.</summary>
    public static WorkspaceRemoveResult Removed(string millerDir, string? workspaceId = null, string? root = null) =>
        new(Outcome.Removed, millerDir, workspaceId, root);

    /// <summary>Refused because the path is the live (in-use) workspace.</summary>
    public static WorkspaceRemoveResult RefusedLive(string millerDir, string? workspaceId = null, string? root = null) =>
        new(Outcome.RefusedLive, millerDir, workspaceId, root);

    /// <summary>Refused because another Miller is using the target workspace.</summary>
    public static WorkspaceRemoveResult RefusedInUse(string millerDir, string? workspaceId = null, string? root = null) =>
        new(Outcome.RefusedInUse, millerDir, workspaceId, root);

    /// <summary>No <c>.miller</c> dir to remove (clean no-op).</summary>
    public static WorkspaceRemoveResult NotFound(string millerDir, string? workspaceId = null, string? root = null) =>
        new(Outcome.NotFound, millerDir, workspaceId, root);
}

/// <summary>
/// The PURE renderers for the <c>workspace</c> tool (M7 decision-2/6). Deterministic, no I/O: each takes an
/// already-assembled fact/result record (the tool does the SQLite/subprocess work) and produces compact
/// token-thrifty markdown or a structured JSON document. The status view embeds the
/// <see cref="TelemetryRender"/> tool-breakdown (julie's tool-breakdown screen) so the SQL/aggregation stays
/// cleanly separate from formatting. Mirrors the JSON-writer style of the other tools (<c>ArrayBufferWriter</c> +
/// <c>Utf8JsonWriter</c> with relaxed escaping).
/// </summary>
public static class WorkspaceRender
{
    // ---------- status ----------

    /// <summary>
    /// Render the status view: workspace identity + index facts + freshness, then the embedded telemetry
    /// tool-breakdown. Compact is a labelled key/value block followed by the telemetry table; JSON nests a
    /// <c>workspace</c>, an <c>index</c>, and a <c>telemetry</c> section.
    /// </summary>
    public static string Status(WorkspaceFacts facts, TelemetrySummary telemetry, bool json) =>
        json ? StatusJson(facts, telemetry) : StatusCompact(facts, telemetry);

    private static string StatusCompact(WorkspaceFacts facts, TelemetrySummary telemetry)
    {
        var sb = new StringBuilder();
        sb.Append("# workspace");
        if (!string.IsNullOrEmpty(facts.ServerVersion))
            sb.Append("  miller ").Append(facts.ServerVersion);
        if (facts.ServerProcessId is { } pid)
            sb.Append("  pid ").Append(pid);
        sb.Append('\n');
        sb.Append(DisplayId(facts.Root, facts.WorkspaceId, facts.DisplayId))
          .Append("  ").Append(facts.Root)
          .Append("  [").Append(facts.IsLeader ? "leader" : "reader").Append("]\n");

        sb.Append("symbols: ").Append(facts.DocumentCount)
          .Append("  ext: ").Append(facts.KnownExtensionsCount)
          .Append("  rev: ").Append(facts.BuiltRevision);
        if (facts.LatestObservedRevision != facts.BuiltRevision)
            sb.Append('/').Append(facts.LatestObservedRevision);
        sb.Append("  ").Append(FreshLabel(facts))
          .Append("  queue: ").Append(facts.QueueEmpty ? "empty" : "pending")
          .Append('\n');
        if (!string.IsNullOrEmpty(facts.FreshnessStatus) &&
            !string.Equals(facts.FreshnessStatus, "current", StringComparison.OrdinalIgnoreCase))
            sb.Append("freshness: ").Append(facts.FreshnessStatus).Append('\n');
        if (facts.SearchSidecar is { } sidecar)
            sb.Append("search_db: ").Append(SearchSidecarLabel(sidecar)).Append('\n');
        if (facts.ContentCorpus is { } corpus)
            sb.Append("content_db: ").Append(ContentCorpusLabel(corpus, facts.BuiltRevision)).Append('\n');
        if (!string.IsNullOrEmpty(facts.WarningText))
            sb.Append("warning: ").Append(facts.WarningText).Append('\n');

        string telemetryLine = TelemetryLine(telemetry);
        if (!string.IsNullOrEmpty(telemetryLine))
            sb.Append(telemetryLine).Append('\n');
        return sb.ToString().TrimEnd('\n');
    }

    private static string SearchSidecarLabel(SearchSidecarFacts facts) => facts.State switch
    {
        "disabled" => "disabled",
        "current" => $"current rev {facts.Revision}",
        "missing" => $"MISSING expected rev {facts.ExpectedRevision}",
        "stale" => $"STALE ({RevisionComparison(facts.Revision, facts.ExpectedRevision, "expected")})",
        "unreadable" => string.IsNullOrWhiteSpace(facts.Error)
            ? "UNREADABLE"
            : "UNREADABLE: " + facts.Error,
        _ => facts.State,
    };

    private static string ContentCorpusLabel(ContentCorpusFacts facts, long expectedRevision)
    {
        string suffix = $"sources {facts.SourceCount}  chunks {facts.ChunkCount}";
        return facts.State switch
        {
            "current" => $"current rev {facts.WorkspaceRevision}  {suffix}",
            "missing" => $"MISSING expected rev {expectedRevision}",
            "stale" => $"STALE rev {facts.WorkspaceRevision} ({RevisionComparison(facts.WorkspaceRevision, expectedRevision, "expected")})  {suffix}",
            "unreadable" => string.IsNullOrWhiteSpace(facts.Error)
                ? "UNREADABLE"
                : "UNREADABLE: " + facts.Error,
            _ => string.IsNullOrWhiteSpace(facts.State) ? suffix : facts.State + "  " + suffix,
        };
    }

    // "fresh" / "STALE (built N < latest M)" / "unknown" — a stale index is called out, never silently glossed.
    private static string FreshLabel(WorkspaceFacts facts) => facts.IndexFresh switch
    {
        true => "fresh",
        false => $"STALE ({RevisionComparison(facts.BuiltRevision, facts.LatestObservedRevision, "latest")})",
        null => "unknown",
    };

    private static string RevisionComparison(long? builtRevision, long expectedRevision, string expectedLabel)
    {
        string expected = expectedRevision.ToString(CultureInfo.InvariantCulture);
        if (builtRevision is not { } built)
            return $"built unknown, {expectedLabel} {expected}";

        string op = built < expectedRevision ? "<" : built > expectedRevision ? ">" : "=";
        return $"built {built.ToString(CultureInfo.InvariantCulture)} {op} {expectedLabel} {expected}";
    }

    private static string DisplayId(string root, string? workspaceId, string? displayId)
    {
        if (!string.IsNullOrWhiteSpace(displayId))
            return displayId;

        if (string.IsNullOrWhiteSpace(workspaceId))
            return "(unknown)";

        try
        {
            return WorkspaceId.Display(root, workspaceId);
        }
        catch (ArgumentException)
        {
            return workspaceId;
        }
    }

    private static string TelemetryLine(TelemetrySummary telemetry)
    {
        if (telemetry.Tools.Count == 0)
            return string.Empty;

        ToolStat top = telemetry.Tools
            .OrderByDescending(static tool => tool.Calls)
            .ThenByDescending(static tool => tool.P95Ms)
            .ThenBy(static tool => tool.Tool, StringComparer.Ordinal)
            .First();
        long errors = telemetry.Tools.Sum(static tool => tool.ErrorCount);
        var sb = new StringBuilder();
        sb.Append("telemetry: ").Append(telemetry.TotalCalls.ToString(CultureInfo.InvariantCulture))
          .Append(" calls");
        if (errors > 0)
            sb.Append("  errors=").Append(errors.ToString(CultureInfo.InvariantCulture));
        sb.Append("  top=").Append(top.Tool)
          .Append(" p95=").Append(top.P95Ms.ToString(CultureInfo.InvariantCulture)).Append("ms");
        if (telemetry.DroppedWrites > 0)
            sb.Append("  dropped=").Append(telemetry.DroppedWrites.ToString(CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    private static string StatusJson(WorkspaceFacts facts, TelemetrySummary telemetry)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = NewWriter(buffer))
        {
            w.WriteStartObject();

            w.WritePropertyName("workspace");
            w.WriteStartObject();
            w.WriteString("root", facts.Root);
            if (facts.WorkspaceId is null) w.WriteNull("workspace_id");
            else w.WriteString("workspace_id", facts.WorkspaceId);
            if (facts.DisplayId is null) w.WriteNull("display_id");
            else w.WriteString("display_id", facts.DisplayId);
            w.WriteString("db", facts.DbPath);
            w.WriteBoolean("leader", facts.IsLeader);
            if (facts.ServerVersion is null) w.WriteNull("server_version");
            else w.WriteString("server_version", facts.ServerVersion);
            if (facts.ServerProcessId is { } pid) w.WriteNumber("server_pid", pid);
            else w.WriteNull("server_pid");
            w.WriteEndObject();

            w.WritePropertyName("index");
            w.WriteStartObject();
            w.WriteNumber("document_count", facts.DocumentCount);
            w.WriteNumber("known_extensions", facts.KnownExtensionsCount);
            w.WriteNumber("built_revision", facts.BuiltRevision);
            w.WriteNumber("latest_revision", facts.LatestObservedRevision);
            if (facts.IndexFresh is { } fresh) w.WriteBoolean("index_fresh", fresh);
            else w.WriteNull("index_fresh");
            if (facts.FreshnessStatus is null) w.WriteNull("freshness_status");
            else w.WriteString("freshness_status", facts.FreshnessStatus);
            if (facts.WarningText is null) w.WriteNull("warning");
            else w.WriteString("warning", facts.WarningText);
            w.WriteBoolean("queue_empty", facts.QueueEmpty);
            w.WritePropertyName("search_sidecar");
            WriteSearchSidecarJson(w, facts.SearchSidecar);
            w.WritePropertyName("content_corpus");
            WriteContentCorpusJson(w, facts.ContentCorpus);
            w.WriteEndObject();

            // Embed the telemetry breakdown as a nested object: re-parse TelemetryRender.Json so the two stay in
            // lockstep (one source of truth for the tool-breakdown shape) rather than duplicating its fields here.
            w.WritePropertyName("telemetry");
            using (var telemetryDoc = JsonDocument.Parse(TelemetryRender.Json(telemetry)))
                telemetryDoc.RootElement.WriteTo(w);

            w.WriteEndObject();
        }
        return Utf8(buffer);
    }

    private static void WriteSearchSidecarJson(Utf8JsonWriter w, SearchSidecarFacts? facts)
    {
        if (facts is null)
        {
            w.WriteNullValue();
            return;
        }

        w.WriteStartObject();
        w.WriteString("state", facts.State);
        if (facts.Path is null) w.WriteNull("path");
        else w.WriteString("path", facts.Path);
        if (facts.Revision is { } revision) w.WriteNumber("revision", revision);
        else w.WriteNull("revision");
        w.WriteNumber("expected_revision", facts.ExpectedRevision);
        if (facts.DocumentCount is { } documentCount) w.WriteNumber("document_count", documentCount);
        else w.WriteNull("document_count");
        if (facts.Error is null) w.WriteNull("error");
        else w.WriteString("error", facts.Error);
        w.WriteEndObject();
    }

    private static void WriteContentCorpusJson(Utf8JsonWriter w, ContentCorpusFacts? facts)
    {
        if (facts is null)
        {
            w.WriteNullValue();
            return;
        }

        w.WriteStartObject();
        w.WriteString("state", facts.State);
        if (facts.Path is null) w.WriteNull("path");
        else w.WriteString("path", facts.Path);
        if (facts.SchemaVersion is { } schemaVersion) w.WriteNumber("schema_version", schemaVersion);
        else w.WriteNull("schema_version");
        if (facts.WorkspaceRevision is { } revision) w.WriteNumber("workspace_revision", revision);
        else w.WriteNull("workspace_revision");
        w.WriteNumber("source_count", facts.SourceCount);
        w.WriteNumber("chunk_count", facts.ChunkCount);
        w.WriteNumber("indexed_source_bytes", facts.IndexedSourceBytes);
        w.WriteNumber("stored_raw_bytes", facts.StoredRawBytes);
        w.WriteNumber("status_skipped", facts.StatusSkipped);
        w.WriteNumber("scope_skipped", facts.ScopeSkipped);
        w.WriteNumber("too_large_skipped", facts.TooLargeSkipped);
        w.WriteNumber("missing_skipped", facts.MissingSkipped);
        w.WriteNumber("hash_mismatch_skipped", facts.HashMismatchSkipped);
        w.WriteNumber("non_utf8_skipped", facts.NonUtf8Skipped);
        w.WriteNumber("io_skipped", facts.IoSkipped);
        if (facts.Error is null) w.WriteNull("error");
        else w.WriteString("error", facts.Error);
        w.WriteEndObject();
    }

    // ---------- list ----------

    /// <summary>
    /// Render the list view. Miller serves ONE workspace per process (a multi-workspace registry is
    /// eros/commercial-tier — decision-1), so the list is the CURRENT workspace, honestly labelled so it is not
    /// mistaken for a multi-entry registry.
    /// </summary>
    public static string List(WorkspaceFacts facts, bool json) =>
        json ? ListJson(facts) : ListCompact(facts);

    /// <summary>Render the registry-backed workspace list.</summary>
    public static string List(IReadOnlyList<WorkspaceListEntry> entries, bool json) =>
        json ? ListJson(entries) : ListCompact(entries);

    private static string ListCompact(WorkspaceFacts facts)
    {
        var sb = new StringBuilder();
        sb.Append("# workspaces (1)\n");
        sb.Append("* ").Append(DisplayId(facts.Root, facts.WorkspaceId, facts.DisplayId))
          .Append("  ").Append(facts.Root)
          .Append("  [current]")
          .Append("  symbols=").Append(facts.DocumentCount)
          .Append("  rev=").Append(facts.BuiltRevision)
          .Append("  ").Append(FreshLabel(facts))
          .Append("  role=").Append(facts.IsLeader ? "leader" : "reader");
        return sb.ToString();
    }

    private static string ListCompact(IReadOnlyList<WorkspaceListEntry> entries)
    {
        var sb = new StringBuilder();
        sb.Append("# workspaces (").Append(entries.Count).Append(")\n");
        foreach (WorkspaceListEntry entry in entries)
        {
            sb.Append("* ").Append(entry.DisplayId).Append("  ").Append(entry.Root);
            if (entry.Current)
                sb.Append("  [current]");
            sb.Append("  state: ").Append(entry.State)
              .Append("  rev: ").Append(entry.LastRevision?.ToString() ?? "(unknown)");
            if (!string.IsNullOrEmpty(entry.LastError))
                sb.Append('\n').Append("  error: ").Append(entry.LastError);
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    private static string ListJson(WorkspaceFacts facts)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = NewWriter(buffer))
        {
            w.WriteStartObject();
            w.WritePropertyName("workspaces");
            w.WriteStartArray();
            w.WriteStartObject();
            w.WriteString("root", facts.Root);
            if (facts.WorkspaceId is null) w.WriteNull("workspace_id");
            else w.WriteString("workspace_id", facts.WorkspaceId);
            w.WriteNumber("document_count", facts.DocumentCount);
            w.WriteNumber("built_revision", facts.BuiltRevision);
            w.WriteBoolean("leader", facts.IsLeader);
            w.WriteBoolean("current", true); // the only entry is always the served workspace
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Utf8(buffer);
    }

    private static string ListJson(IReadOnlyList<WorkspaceListEntry> entries)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = NewWriter(buffer))
        {
            w.WriteStartObject();
            w.WritePropertyName("workspaces");
            w.WriteStartArray();
            foreach (WorkspaceListEntry entry in entries)
            {
                w.WriteStartObject();
                w.WriteString("workspace_id", entry.WorkspaceId);
                w.WriteString("display_id", entry.DisplayId);
                w.WriteString("root", entry.Root);
                w.WriteString("index_db_path", entry.DbPath);
                w.WriteString("state", entry.State);
                if (entry.LastRevision is { } revision) w.WriteNumber("last_revision", revision);
                else w.WriteNull("last_revision");
                w.WriteBoolean("current", entry.Current);
                if (entry.LastError is null) w.WriteNull("last_error");
                else w.WriteString("last_error", entry.LastError);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Utf8(buffer);
    }

    // ---------- refresh / full ----------

    /// <summary>Render a <c>refresh</c>/<c>full</c> action result (scanned? swapped? new revision? honesty note).</summary>
    public static string Action(WorkspaceActionResult result, bool json) =>
        json ? ActionJson(result) : ActionCompact(result);

    private static string ActionCompact(WorkspaceActionResult result)
    {
        var sb = new StringBuilder();
        sb.Append("# workspace ").Append(result.Operation).Append('\n');
        if (!string.IsNullOrEmpty(result.WorkspaceId))
            sb.Append("workspace_id: ").Append(result.WorkspaceId).Append('\n');
        if (!string.IsNullOrEmpty(result.Root))
            sb.Append("root: ").Append(result.Root).Append('\n');
        if (!string.IsNullOrEmpty(result.Status))
            sb.Append("status: ").Append(result.Status).Append('\n');
        sb.Append("scanned: ").Append(result.Scanned ? "yes" : "no").Append('\n');
        sb.Append("swapped: ").Append(result.Swapped ? "yes" : "no").Append('\n');
        sb.Append("revision: ").Append(result.Revision);
        if (!string.IsNullOrEmpty(result.Note))
            sb.Append('\n').Append("note: ").Append(result.Note);
        return sb.ToString();
    }

    private static string ActionJson(WorkspaceActionResult result)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = NewWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("operation", result.Operation);
            if (result.WorkspaceId is null) w.WriteNull("workspace_id");
            else w.WriteString("workspace_id", result.WorkspaceId);
            if (result.Root is null) w.WriteNull("root");
            else w.WriteString("root", result.Root);
            if (result.Status is null) w.WriteNull("status");
            else w.WriteString("status", result.Status);
            w.WriteBoolean("scanned", result.Scanned);
            w.WriteBoolean("swapped", result.Swapped);
            w.WriteNumber("revision", result.Revision);
            if (result.IndexFresh is { } fresh) w.WriteBoolean("index_fresh", fresh);
            else w.WriteNull("index_fresh");
            if (string.IsNullOrEmpty(result.Note)) w.WriteNull("note");
            else w.WriteString("note", result.Note);
            w.WritePropertyName("search_sidecar");
            WriteSearchSidecarJson(w, result.SearchSidecar);
            w.WritePropertyName("content_corpus");
            WriteContentCorpusJson(w, result.ContentCorpus);
            w.WriteEndObject();
        }
        return Utf8(buffer);
    }

    // ---------- dashboard ----------

    /// <summary>Render a <c>dashboard</c> launch/reuse result for MCP callers.</summary>
    internal static string Dashboard(WorkspaceDashboardResult result, bool json) =>
        json ? DashboardJson(result) : DashboardCompact(result);

    private static string DashboardCompact(WorkspaceDashboardResult result)
    {
        var sb = new StringBuilder();
        sb.Append("# workspace dashboard\n");
        sb.Append("status: ").Append(result.Status).Append('\n');
        sb.Append("success: ").Append(result.Success ? "yes" : "no").Append('\n');
        sb.Append("url: ").Append(result.Url);
        if (result.ProcessId is { } pid)
            sb.Append('\n').Append("pid: ").Append(pid);
        if (!string.IsNullOrEmpty(result.Message))
            sb.Append('\n').Append("message: ").Append(result.Message);
        return sb.ToString();
    }

    private static string DashboardJson(WorkspaceDashboardResult result)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = NewWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("operation", "dashboard");
            w.WriteString("status", result.Status);
            w.WriteBoolean("success", result.Success);
            w.WriteString("url", result.Url);
            if (result.ProcessId is { } pid) w.WriteNumber("pid", pid);
            else w.WriteNull("pid");
            if (result.Message is null) w.WriteNull("message");
            else w.WriteString("message", result.Message);
            w.WriteEndObject();
        }
        return Utf8(buffer);
    }

    // ---------- open (prime) ----------

    /// <summary>Render an <c>open(path)</c> prime result, stating plainly it is NOT a live workspace switch.</summary>
    public static string Open(WorkspaceOpenResult result, bool json) =>
        json ? OpenJson(result) : OpenCompact(result);

    private static string OpenCompact(WorkspaceOpenResult result)
    {
        var sb = new StringBuilder();
        sb.Append("# workspace open (primed)\n");
        sb.Append("path: ").Append(result.Path).Append('\n');
        if (!string.IsNullOrEmpty(result.WorkspaceId))
            sb.Append("workspace_id: ").Append(result.WorkspaceId).Append('\n');
        if (!string.IsNullOrEmpty(result.DisplayId))
            sb.Append("display_id: ").Append(result.DisplayId).Append('\n');
        sb.Append("db: ").Append(result.DbPath).Append('\n');
        sb.Append("symbols_extracted: ").Append(result.SymbolsExtracted).Append('\n');
        sb.Append("revision: ").Append(result.Revision).Append('\n');
        sb.Append("note: primed this path's index (extract scan) — NOT a live switch. ");
        sb.Append("This Miller keeps serving its launch directory; start a new Miller in that path to use it.");
        if (!string.IsNullOrEmpty(result.WarningText))
            sb.Append('\n').Append("warning: ").Append(result.WarningText);
        return sb.ToString();
    }

    private static string OpenJson(WorkspaceOpenResult result)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = NewWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("path", result.Path);
            if (result.WorkspaceId is null) w.WriteNull("workspace_id");
            else w.WriteString("workspace_id", result.WorkspaceId);
            if (result.DisplayId is null) w.WriteNull("display_id");
            else w.WriteString("display_id", result.DisplayId);
            w.WriteString("db", result.DbPath);
            w.WriteNumber("symbols_extracted", result.SymbolsExtracted);
            w.WriteNumber("revision", result.Revision);
            w.WriteBoolean("live_switch", false);
            w.WriteString("note",
                "primed this path's index (extract scan) — not a live switch; start a new Miller in that path to use it.");
            if (result.WarningText is null) w.WriteNull("warning");
            else w.WriteString("warning", result.WarningText);
            w.WriteEndObject();
        }
        return Utf8(buffer);
    }

    // ---------- remove ----------

    /// <summary>Render a <c>remove(path)</c> result (removed / refused-live / not-found — each honest).</summary>
    public static string Remove(WorkspaceRemoveResult result, bool json) =>
        json ? RemoveJson(result) : RemoveCompact(result);

    private static string RemoveCompact(WorkspaceRemoveResult result) => result.Result switch
    {
        WorkspaceRemoveResult.Outcome.Removed =>
            $"removed workspace index: {result.MillerDir}",
        WorkspaceRemoveResult.Outcome.RefusedLive =>
            $"refused: {result.MillerDir} is the workspace this Miller is serving (in use). " +
            "Stop that Miller first, or remove a different workspace.",
        WorkspaceRemoveResult.Outcome.RefusedInUse =>
            $"refused: {result.MillerDir} is in use by another Miller writer. Stop that Miller first.",
        WorkspaceRemoveResult.Outcome.NotFound =>
            $"not found: no index dir at {result.MillerDir} — nothing to remove (not an error).",
        _ => $"remove: unrecognised outcome for {result.MillerDir}.",
    };

    private static string RemoveJson(WorkspaceRemoveResult result)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = NewWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("result", result.Result switch
            {
                WorkspaceRemoveResult.Outcome.Removed => "removed",
                WorkspaceRemoveResult.Outcome.RefusedLive => "refused_live",
                WorkspaceRemoveResult.Outcome.RefusedInUse => "refused_in_use",
                WorkspaceRemoveResult.Outcome.NotFound => "not_found",
                _ => "unknown",
            });
            w.WriteString("miller_dir", result.MillerDir);
            if (result.WorkspaceId is null) w.WriteNull("workspace_id");
            else w.WriteString("workspace_id", result.WorkspaceId);
            if (result.Root is null) w.WriteNull("root");
            else w.WriteString("root", result.Root);
            w.WriteString("message", RemoveCompact(result));
            w.WriteEndObject();
        }
        return Utf8(buffer);
    }

    // ---------- shared helpers ----------

    private static Utf8JsonWriter NewWriter(ArrayBufferWriter<byte> buffer) =>
        new(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private static string Utf8(ArrayBufferWriter<byte> buffer) => Encoding.UTF8.GetString(buffer.WrittenSpan);
}
