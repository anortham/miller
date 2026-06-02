using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
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
/// <param name="WorkspaceId">julie's workspace id (SHA256 of the canonical root), or null if not yet known.</param>
/// <param name="DbPath">The julie extract DB path Miller reads.</param>
/// <param name="IsLeader">Whether THIS instance holds the writer lock (runs the watcher/extract writes).</param>
/// <param name="DocumentCount">Indexed symbol count of the live index.</param>
/// <param name="KnownExtensionsCount">Distinct file-extension count (the cross-language "languages indexed" proxy).</param>
/// <param name="BuiltRevision">The <c>extraction_revisions</c> revision the held index was built from.</param>
/// <param name="LatestObservedRevision">The latest revision the freshness poll has observed.</param>
/// <param name="IndexFresh">The coarse <c>index_fresh</c> probe (built==latest AND queue empty); null = unknown.</param>
/// <param name="QueueEmpty">Whether the leader's watcher queue holds no pending events (vacuously true on a reader).</param>
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
    string? WarningText = null);

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
    string? Status = null);

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
    string? DisplayId = null);

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
        sb.Append("# workspace\n");
        sb.Append("root: ").Append(facts.Root).Append('\n');
        sb.Append("workspace_id: ").Append(facts.WorkspaceId ?? "(unknown)").Append('\n');
        sb.Append("db: ").Append(facts.DbPath).Append('\n');
        sb.Append("role: ").Append(facts.IsLeader ? "leader (writer)" : "reader").Append('\n');

        sb.Append("\n# index\n");
        sb.Append("symbols: ").Append(facts.DocumentCount).Append('\n');
        sb.Append("extensions: ").Append(facts.KnownExtensionsCount).Append('\n');
        sb.Append("built_revision: ").Append(facts.BuiltRevision).Append('\n');
        sb.Append("latest_revision: ").Append(facts.LatestObservedRevision).Append('\n');
        sb.Append("index_fresh: ").Append(FreshLabel(facts)).Append('\n');
        if (!string.IsNullOrEmpty(facts.FreshnessStatus))
            sb.Append("freshness_status: ").Append(facts.FreshnessStatus).Append('\n');
        if (!string.IsNullOrEmpty(facts.WarningText))
            sb.Append("warning: ").Append(facts.WarningText).Append('\n');
        sb.Append("queue_empty: ").Append(facts.QueueEmpty ? "yes" : "no").Append('\n');

        sb.Append('\n').Append(TelemetryRender.Compact(telemetry));
        return sb.ToString().TrimEnd('\n');
    }

    // "fresh" / "STALE (built N < latest M)" / "unknown" — a stale index is called out, never silently glossed.
    private static string FreshLabel(WorkspaceFacts facts) => facts.IndexFresh switch
    {
        true => "fresh",
        false => $"STALE (built {facts.BuiltRevision} < latest {facts.LatestObservedRevision})",
        null => "unknown",
    };

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
            w.WriteString("db", facts.DbPath);
            w.WriteBoolean("leader", facts.IsLeader);
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
        sb.Append("# workspaces (1 — Miller serves one workspace per process)\n");
        sb.Append("* ").Append(facts.Root).Append("  [current]\n");
        sb.Append("  workspace_id: ").Append(facts.WorkspaceId ?? "(unknown)").Append('\n');
        sb.Append("  symbols: ").Append(facts.DocumentCount)
          .Append("  built_revision: ").Append(facts.BuiltRevision)
          .Append("  index_fresh: ").Append(FreshLabel(facts))
          .Append("  role: ").Append(facts.IsLeader ? "leader" : "reader");
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
            sb.Append('\n');
            sb.Append("  workspace_id: ").Append(entry.WorkspaceId)
              .Append("  state: ").Append(entry.State)
              .Append("  revision: ").Append(entry.LastRevision?.ToString() ?? "(unknown)")
              .Append('\n');
            sb.Append("  db: ").Append(entry.DbPath);
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
            if (string.IsNullOrEmpty(result.Note)) w.WriteNull("note");
            else w.WriteString("note", result.Note);
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
