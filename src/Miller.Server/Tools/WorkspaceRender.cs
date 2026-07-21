using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Miller.Indexing;
using Miller.Indexing.Semantic;
using Miller.Server;
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
/// <param name="ArtifactId">The current extract artifact generation id, or null when the artifact is missing or unreadable.</param>
/// <param name="Vectors">Status of the Miller-owned <c>vectors.db</c> sidecar, when known. A <c>disabled</c>
/// state renders nowhere: with <c>MILLER_SEMANTIC</c> off, existing status output stays byte-identical.</param>
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
    ContentCorpusFacts? ContentCorpus = null,
    string? ArtifactId = null,
    VectorSidecarFacts? Vectors = null);

/// <summary>A registry-backed row rendered by <c>workspace list</c>.</summary>
/// <remarks><see cref="LastSeenAt"/> drives recency ordering (current first, then most-recently-seen); it is
/// optional so pre-existing construction sites compile, and defaults to <see cref="DateTimeOffset.MinValue"/>.</remarks>
public readonly record struct WorkspaceListEntry(
    string WorkspaceId,
    string DisplayId,
    string Root,
    string DbPath,
    string State,
    long? LastRevision,
    bool Current,
    string? LastError,
    DateTimeOffset LastSeenAt = default);

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
/// <param name="ScanDurationMs">Wall ms of the julie-extract scan attempt (set even for a failed/killed scan);
/// null when no scan ran or the path does not measure it.</param>
/// <param name="DurationMs">Wall ms of the whole refresh attempt, when measured.</param>
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
    ContentCorpusFacts? ContentCorpus = null,
    long? ScanDurationMs = null,
    long? DurationMs = null,
    string? ArtifactId = null);

/// <summary>The result of starting or reusing the local loopback dashboard from the <c>workspace</c> tool.</summary>
internal readonly record struct WorkspaceDashboardResult(
    string Status,
    bool Success,
    string Url,
    int? ProcessId,
    string? Message);

internal readonly record struct WorkspaceLeaderResult(
    WorkspaceFacts Status,
    LeaderHealthFacts Leader,
    string Recommendation,
    bool HandoffRequested,
    bool HandoffWaited,
    bool HandoffObserved,
    string? HandoffRequestId,
    string? HandoffNote);

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
    string? Root = null,
    bool IndexDirDeleted = false)
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

    /// <summary>The workspace was removed from Miller; <paramref name="indexDirDeleted"/> records whether the index dir existed and was deleted.</summary>
    public static WorkspaceRemoveResult Removed(
        string millerDir,
        string? workspaceId = null,
        string? root = null,
        bool indexDirDeleted = true) =>
        new(Outcome.Removed, millerDir, workspaceId, root, indexDirDeleted);

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
/// The result of a <c>prune</c> pass: registry rows whose <c>canonical_root</c> no longer exists, plus the
/// count of rows kept (existing roots and any protected current workspace).
/// </summary>
public readonly record struct WorkspacePruneResult(
    bool DryRun,
    IReadOnlyList<WorkspacePruneEntry> Pruned,
    int Kept);

/// <summary>One registry row removed (or that would be removed in dry-run) by <c>prune</c>.</summary>
public readonly record struct WorkspacePruneEntry(string WorkspaceId, string DisplayId, string Root);

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
        Status(facts, telemetry, json, leader: null);

    /// <summary>
    /// Role-aware status overload (version-aware leadership D6): when <paramref name="leader"/> carries this
    /// process's eligibility verdict, the compact role label can explain a permanently-outdated reader
    /// (<c>reader (extractor outdated: own X &lt; index Y)</c>) instead of looking mysteriously idle.
    /// </summary>
    public static string Status(
        WorkspaceFacts facts,
        TelemetrySummary telemetry,
        bool json,
        LeaderHealthFacts? leader,
        BootstrapSnapshot? bootstrap = null) =>
        json ? StatusJson(facts, telemetry, leader) : StatusCompact(facts, telemetry, leader, bootstrap);

    // "leader" / "reader" / "reader (extractor outdated: own X < index Y)" — the D6 role string. The outdated
    // form fires only when the verdict is ineligible AND both versions prove the downgrade direction.
    private static string RoleLabel(WorkspaceFacts facts, LeaderHealthFacts? leader)
    {
        if (facts.IsLeader)
            return "leader";
        if (leader is { OwnVerdict.Eligible: false, OwnExtractorVersion: { } own, ArtifactExtractorVersion: { } artifact } &&
            LeaderHealthFacts.IsExtractorOlder(own, artifact))
            return $"reader (extractor outdated: own {own} < index {artifact})";
        return "reader";
    }

    private static string StatusCompact(
        WorkspaceFacts facts,
        TelemetrySummary telemetry,
        LeaderHealthFacts? leader,
        BootstrapSnapshot? bootstrap)
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
          .Append("  [").Append(RoleLabel(facts, leader)).Append("]\n");

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
        if (VectorsLabel(facts.Vectors) is { } vectorsLabel)
            sb.Append("vectors: ").Append(vectorsLabel).Append('\n');
        if (!string.IsNullOrEmpty(facts.WarningText))
            sb.Append("warning: ").Append(facts.WarningText).Append('\n');
        if (bootstrap is { Phase: BootstrapPhase.Running, CanonicalRoot.Length: > 0 })
        {
            sb.Append("rebinding: ")
              .Append(bootstrap.CanonicalRoot)
              .Append(" (started ")
              .Append(ElapsedSeconds(bootstrap.StartedAtUtc))
              .Append("s ago)\n");
        }

        string telemetryLine = TelemetryLine(telemetry);
        if (!string.IsNullOrEmpty(telemetryLine))
            sb.Append(telemetryLine).Append('\n');
        return sb.ToString().TrimEnd('\n');
    }

    private static long ElapsedSeconds(DateTimeOffset? startedAtUtc)
    {
        if (startedAtUtc is null)
            return 0;

        var elapsed = DateTimeOffset.UtcNow - startedAtUtc.Value;
        return Math.Max(0, (long)elapsed.TotalSeconds);
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

    // Null ⇒ render no `vectors:` line at all. Both an absent fact and the `disabled` state map to null, so a
    // workspace with MILLER_SEMANTIC off produces byte-identical status output to a build without vectors.
    private static string? VectorsLabel(VectorSidecarFacts? facts) => facts?.State switch
    {
        null or "disabled" => null,
        "ready" => VectorsReadyLabel(facts),
        "building" => $"building {facts.BuildProgressPercent ?? 0}% (not queryable)",
        "unavailable" => string.IsNullOrWhiteSpace(facts.Reason)
            ? "unavailable"
            : $"unavailable ({facts.Reason})",
        _ => facts.State,
    };

    // Serving from a retained generation is still `ready`: which generation answers queries is a JSON fact. A
    // pending shadow rebuild — surfaced through the chunk cursor's hold reason, already in JSON — outranks the
    // pending-files hint so a long rebuild does not read as idle.
    private static string VectorsReadyLabel(VectorSidecarFacts facts)
    {
        if (ShadowRebuildPending(facts))
            return "ready (rebuilding)";

        return facts.LaggierCursor?.PendingFiles is > 0 and { } pending
            ? $"ready (updating; {pending.ToString(CultureInfo.InvariantCulture)} files pending)"
            : "ready";
    }

    private static bool ShadowRebuildPending(VectorSidecarFacts facts) =>
        HoldsForShadowRebuild(facts.SymbolCursor) || HoldsForShadowRebuild(facts.ChunkCursor);

    private static bool HoldsForShadowRebuild(VectorCursorFacts? cursor) =>
        cursor?.LastError is { } error
        && error.Contains(VectorConvergePlanner.ShadowRebuildPendingMarker, StringComparison.Ordinal);

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

    // "present  N snapshots  <size>  schema v1" / "absent" / "unreadable" — the append-only metric-history sidecar. A
    // recovered corruption (a history.db.corrupt-* renamed aside) is flagged so an operator can see history was reset;
    // a PRESENT-but-unreadable file reads "unreadable" (never a healthy-looking "present  0 snapshots").
    private static string HistorySidecarLabel(MetricHistoryStatus history)
    {
        string recovered = history.CorruptRecovered ? "  corrupt-recovered" : string.Empty;
        if (!history.Present)
            return "absent" + recovered;
        if (history.Unreadable)
            return "unreadable" + recovered;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"present  {history.SnapshotCount} snapshots  {FormatBytes(history.SizeBytes)}  schema v{history.SchemaVersion}{recovered}");
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        double kb = bytes / 1024d;
        if (kb < 1024)
            return kb.ToString("0.#", CultureInfo.InvariantCulture) + " KB";
        return (kb / 1024d).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
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

    private static string StatusJson(WorkspaceFacts facts, TelemetrySummary telemetry, LeaderHealthFacts? leader)
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
            w.WriteString("role", RoleLabel(facts, leader));
            if (facts.ServerVersion is null) w.WriteNull("server_version");
            else w.WriteString("server_version", facts.ServerVersion);
            if (facts.ServerProcessId is { } pid) w.WriteNumber("server_pid", pid);
            else w.WriteNull("server_pid");
            w.WriteEndObject();

            w.WritePropertyName("indexer_leader");
            WriteLeaderJson(w, facts, leader);

            w.WritePropertyName("index");
            w.WriteStartObject();
            w.WriteNumber("document_count", facts.DocumentCount);
            w.WriteNumber("known_extensions", facts.KnownExtensionsCount);
            w.WriteNumber("built_revision", facts.BuiltRevision);
            w.WriteNumber("latest_revision", facts.LatestObservedRevision);
            if (facts.ArtifactId is null) w.WriteNull("artifact_id");
            else w.WriteString("artifact_id", facts.ArtifactId);
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
            WriteVectorsJson(w, facts.Vectors);
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

    // Additive per workspace-status-v1/workspace-health-v1, and OMITTED entirely when semantic is off or absent
    // so existing consumers see an unchanged document until the operator opts in.
    private static void WriteVectorsJson(Utf8JsonWriter w, VectorSidecarFacts? facts)
    {
        if (facts is null || facts.State == "disabled")
            return;

        w.WritePropertyName("vectors");
        w.WriteStartObject();
        w.WriteString("state", facts.State);
        if (facts.Path is null) w.WriteNull("path");
        else w.WriteString("path", facts.Path);
        if (facts.Reason is null) w.WriteNull("reason");
        else w.WriteString("reason", facts.Reason);
        if (facts.BuildProgressPercent is { } percent) w.WriteNumber("build_progress_percent", percent);
        else w.WriteNull("build_progress_percent");
        if (facts.DownloadingModel is null) w.WriteNull("downloading_model");
        else w.WriteString("downloading_model", facts.DownloadingModel);
        if (facts.ServingTag is null) w.WriteNull("serving_tag");
        else w.WriteString("serving_tag", facts.ServingTag);
        if (facts.ServingRole is null) w.WriteNull("serving_role");
        else w.WriteString("serving_role", facts.ServingRole);
        if (facts.ArtifactId is null) w.WriteNull("artifact_id");
        else w.WriteString("artifact_id", facts.ArtifactId);
        w.WritePropertyName("symbol_cursor");
        WriteVectorCursorJson(w, facts.SymbolCursor);
        w.WritePropertyName("chunk_cursor");
        WriteVectorCursorJson(w, facts.ChunkCursor);
        w.WritePropertyName("identity");
        WriteVectorIdentityJson(w, facts.Identity);
        w.WritePropertyName("retained_generations");
        w.WriteStartArray();
        foreach (VectorGenerationFacts generation in facts.Retained)
        {
            w.WriteStartObject();
            w.WriteString("tag", generation.Tag);
            w.WriteString("path", generation.Path);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteVectorCursorJson(Utf8JsonWriter w, VectorCursorFacts? cursor)
    {
        if (cursor is null)
        {
            w.WriteNullValue();
            return;
        }

        w.WriteStartObject();
        w.WriteNumber("completed_revision", cursor.CompletedRevision);
        w.WriteNumber("target_revision", cursor.TargetRevision);
        if (cursor.PendingFiles is { } pending) w.WriteNumber("pending_files", pending);
        else w.WriteNull("pending_files");
        if (cursor.LastError is null) w.WriteNull("last_error");
        else w.WriteString("last_error", cursor.LastError);
        if (cursor.LastErrorAt is null) w.WriteNull("last_error_at");
        else w.WriteString("last_error_at", cursor.LastErrorAt);
        w.WriteEndObject();
    }

    private static void WriteVectorIdentityJson(Utf8JsonWriter w, SemanticGenerationIdentity? identity)
    {
        if (identity is null)
        {
            w.WriteNullValue();
            return;
        }

        w.WriteStartObject();
        w.WriteString("encoder_fingerprint", identity.EncoderFingerprint);
        w.WriteString("storage_schema", identity.StorageSchema);
        w.WriteString("corpus_generation", identity.CorpusGeneration);
        w.WriteString("writer_version", identity.WriterVersion);
        w.WriteString("min_reader_version", identity.MinReaderVersion);
        w.WriteString("fusion_profile", identity.FusionProfile);
        w.WriteEndObject();
    }

    private static void WriteHistorySidecarJson(Utf8JsonWriter w, MetricHistoryStatus? history)
    {
        if (history is null)
        {
            w.WriteNullValue();
            return;
        }

        w.WriteStartObject();
        w.WriteBoolean("present", history.Present);
        w.WriteBoolean("unreadable", history.Unreadable);
        w.WriteNumber("schema_version", history.SchemaVersion);
        w.WriteNumber("snapshot_count", history.SnapshotCount);
        w.WriteNumber("size_bytes", history.SizeBytes);
        w.WriteBoolean("corrupt_recovered", history.CorruptRecovered);
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

    // ---------- leader ----------

    internal static string Leader(WorkspaceLeaderResult result, bool json) =>
        json ? LeaderJson(result) : LeaderCompact(result);

    private static string LeaderCompact(WorkspaceLeaderResult result)
    {
        WorkspaceFacts status = result.Status;
        var sb = new StringBuilder();
        sb.Append("# workspace leader\n");
        sb.Append("workspace: ").Append(DisplayId(status.Root, status.WorkspaceId, status.DisplayId))
          .Append("  ").Append(status.Root).Append('\n');
        sb.Append("leader: ").Append(LeaderLabel(status, result.Leader)).Append('\n');
        sb.Append("recommendation: ").Append(result.Recommendation).Append('\n');
        if (result.HandoffRequested)
        {
            sb.Append("handoff: queued");
            if (!string.IsNullOrWhiteSpace(result.HandoffRequestId))
                sb.Append(" ").Append(result.HandoffRequestId);
            sb.Append(result.HandoffObserved ? "  observed" : "  not observed");
            if (!string.IsNullOrWhiteSpace(result.HandoffNote))
                sb.Append("  ").Append(result.HandoffNote);
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    private static string LeaderJson(WorkspaceLeaderResult result)
    {
        WorkspaceFacts status = result.Status;
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = NewWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("schema_version", 1);

            w.WritePropertyName("workspace");
            w.WriteStartObject();
            w.WriteString("root", status.Root);
            if (status.WorkspaceId is null) w.WriteNull("workspace_id");
            else w.WriteString("workspace_id", status.WorkspaceId);
            if (status.DisplayId is null) w.WriteNull("display_id");
            else w.WriteString("display_id", status.DisplayId);
            w.WriteString("db", status.DbPath);
            w.WriteBoolean("leader", status.IsLeader);
            if (status.ServerVersion is null) w.WriteNull("server_version");
            else w.WriteString("server_version", status.ServerVersion);
            if (status.ServerProcessId is { } pid) w.WriteNumber("server_pid", pid);
            else w.WriteNull("server_pid");
            w.WriteEndObject();

            w.WritePropertyName("indexer_leader");
            WriteLeaderJson(w, status, result.Leader);

            w.WriteString("recommendation", result.Recommendation);

            w.WritePropertyName("handoff");
            w.WriteStartObject();
            w.WriteBoolean("requested", result.HandoffRequested);
            w.WriteBoolean("waited", result.HandoffWaited);
            w.WriteBoolean("observed", result.HandoffObserved);
            if (result.HandoffRequestId is null) w.WriteNull("request_id");
            else w.WriteString("request_id", result.HandoffRequestId);
            if (result.HandoffNote is null) w.WriteNull("note");
            else w.WriteString("note", result.HandoffNote);
            w.WriteEndObject();

            w.WriteEndObject();
        }
        return Utf8(buffer);
    }

    // ---------- health ----------

    public static string Health(WorkspaceHealthFacts facts, bool json) =>
        json ? HealthJson(facts) : HealthCompact(facts);

    private static string HealthCompact(WorkspaceHealthFacts facts)
    {
        WorkspaceFacts status = facts.StatusFacts;
        var sb = new StringBuilder();
        sb.Append("# workspace health  ").Append(WorkspaceHealthFacts.StateName(facts.State)).Append('\n');
        sb.Append("workspace: ").Append(DisplayId(status.Root, status.WorkspaceId, status.DisplayId))
          .Append("  ").Append(status.Root).Append('\n');
        sb.Append("index: ").Append(FreshLabel(status))
          .Append(" rev ").Append(status.BuiltRevision.ToString(CultureInfo.InvariantCulture))
          .Append("  symbols ").Append(status.DocumentCount.ToString(CultureInfo.InvariantCulture))
          .Append("  ext ").Append(status.KnownExtensionsCount.ToString(CultureInfo.InvariantCulture))
          .Append("  queue ").Append(status.QueueEmpty ? "empty" : "pending")
          .Append('\n');
        if (facts.Leader is { } leader)
            sb.Append("leader: ").Append(LeaderLabel(status, leader)).Append('\n');
        if (status.SearchSidecar is { } sidecar)
            sb.Append("search_db: ").Append(SearchSidecarLabel(sidecar)).Append('\n');
        if (status.ContentCorpus is { } corpus)
            sb.Append("content_db: ").Append(ContentCorpusLabel(corpus, status.BuiltRevision)).Append('\n');
        if (VectorsLabel(status.Vectors) is { } vectorsLabel)
            sb.Append("vectors: ").Append(vectorsLabel).Append('\n');
        if (facts.History is { } history)
            sb.Append("history_db: ").Append(HistorySidecarLabel(history)).Append('\n');
        sb.Append("quality: ")
          .Append(ParseDiagnosticCount(facts.Extraction).ToString(CultureInfo.InvariantCulture))
          .Append(" parse diagnostics  ")
          .Append(OpenCapabilityGapCount(facts.Extraction).ToString(CultureInfo.InvariantCulture))
          .Append(" open capability gaps  ")
          .Append(StructuralFactCount(facts.Extraction).ToString(CultureInfo.InvariantCulture))
          .Append(" structural facts  ")
          .Append(ComplexityMetricCount(facts.Extraction).ToString(CultureInfo.InvariantCulture))
          .Append(" complexity metrics")
          .Append('\n');
        sb.Append("telemetry: ")
          .Append(facts.TelemetryHealth.TotalCalls.ToString(CultureInfo.InvariantCulture))
          .Append(" calls  errors=")
          .Append(facts.TelemetryHealth.ErrorCount.ToString(CultureInfo.InvariantCulture))
          .Append("  empty=")
          .Append(facts.TelemetryHealth.EmptyCount.ToString(CultureInfo.InvariantCulture))
          .Append('\n');
        if (facts.Warnings.Count > 0)
            sb.Append("warning: ").Append(facts.Warnings[0].Message).Append('\n');
        if (facts.RecommendedActions.Count > 0)
            sb.Append("recommended: ").Append(facts.RecommendedActions[0]).Append('\n');
        return sb.ToString().TrimEnd('\n');
    }

    // "leader: this process ..." / "pid N vX alive" / "pid N vX NOT RUNNING" / "unknown ..." — who owns index
    // convergence for this workspace, honestly, because it is often NOT the serving process.
    private static string LeaderLabel(WorkspaceFacts status, LeaderHealthFacts leader)
    {
        if (status.IsLeader)
            return $"this process (pid {status.ServerProcessId?.ToString(CultureInfo.InvariantCulture) ?? "?"})";
        if (leader.Identity is not { } identity)
            return "unknown (no leader identity recorded — older build leading, or no leader running)";
        string liveness = leader.Alive == false ? "NOT RUNNING (stale identity)" : "alive";
        string extract = identity.ExtractorVersion is { } extractorVersion ? $" extract {extractorVersion}" : string.Empty;
        return $"pid {identity.Pid.ToString(CultureInfo.InvariantCulture)} v{identity.Version}{extract} {liveness}";
    }

    private static void WriteLeaderJson(Utf8JsonWriter w, WorkspaceFacts status, LeaderHealthFacts? leader)
    {
        if (leader is null)
        {
            w.WriteNullValue();
            return;
        }

        w.WriteStartObject();
        w.WriteBoolean("this_process", status.IsLeader);
        if (leader.Identity is { } identity)
        {
            w.WriteNumber("pid", identity.Pid);
            w.WriteString("version", identity.Version);
            if (identity.ProcessPath is null) w.WriteNull("process_path");
            else w.WriteString("process_path", identity.ProcessPath);
            w.WriteString("started_at", identity.StartedAtUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            if (identity.ExtractorVersion is null) w.WriteNull("extractor_version");
            else w.WriteString("extractor_version", identity.ExtractorVersion);
        }
        else
        {
            w.WriteNull("pid");
            w.WriteNull("version");
            w.WriteNull("process_path");
            w.WriteNull("started_at");
            w.WriteNull("extractor_version");
        }
        if (leader.Alive is { } alive) w.WriteBoolean("alive", alive);
        else w.WriteNull("alive");
        // Additive version-aware-leadership fields (D6): this PROCESS's extractor, the artifact's recorded
        // binary_version, and this process's eligibility verdict. All null when the caller did not gather them.
        if (leader.OwnExtractorVersion is null) w.WriteNull("own_extractor_version");
        else w.WriteString("own_extractor_version", leader.OwnExtractorVersion);
        if (leader.ArtifactExtractorVersion is null) w.WriteNull("artifact_extractor_version");
        else w.WriteString("artifact_extractor_version", leader.ArtifactExtractorVersion);
        if (leader.OwnVerdict is { } verdict)
        {
            w.WritePropertyName("own_eligibility");
            w.WriteStartObject();
            w.WriteBoolean("eligible", verdict.Eligible);
            w.WriteString("reason", verdict.Reason);
            w.WriteEndObject();
        }
        else
        {
            w.WriteNull("own_eligibility");
        }
        w.WriteEndObject();
    }

    private static string HealthJson(WorkspaceHealthFacts facts)
    {
        WorkspaceFacts status = facts.StatusFacts;
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = NewWriter(buffer))
        {
            w.WriteStartObject();

            w.WritePropertyName("verdict");
            w.WriteStartObject();
            w.WriteString("state", WorkspaceHealthFacts.StateName(facts.State));
            w.WriteString("summary", facts.Summary);
            w.WriteEndObject();

            w.WritePropertyName("workspace");
            w.WriteStartObject();
            w.WriteString("root", status.Root);
            if (status.WorkspaceId is null) w.WriteNull("workspace_id");
            else w.WriteString("workspace_id", status.WorkspaceId);
            if (status.DisplayId is null) w.WriteNull("display_id");
            else w.WriteString("display_id", status.DisplayId);
            w.WriteString("db", status.DbPath);
            w.WriteBoolean("leader", status.IsLeader);
            if (status.ServerVersion is null) w.WriteNull("server_version");
            else w.WriteString("server_version", status.ServerVersion);
            if (status.ServerProcessId is { } pid) w.WriteNumber("server_pid", pid);
            else w.WriteNull("server_pid");
            w.WriteEndObject();

            w.WritePropertyName("indexer_leader");
            WriteLeaderJson(w, status, facts.Leader);

            w.WritePropertyName("index");
            w.WriteStartObject();
            w.WriteNumber("document_count", status.DocumentCount);
            w.WriteNumber("known_extensions", status.KnownExtensionsCount);
            w.WriteNumber("built_revision", status.BuiltRevision);
            w.WriteNumber("latest_revision", status.LatestObservedRevision);
            if (status.IndexFresh is { } fresh) w.WriteBoolean("index_fresh", fresh);
            else w.WriteNull("index_fresh");
            if (status.FreshnessStatus is null) w.WriteNull("freshness_status");
            else w.WriteString("freshness_status", status.FreshnessStatus);
            if (status.WarningText is null) w.WriteNull("warning");
            else w.WriteString("warning", status.WarningText);
            w.WriteBoolean("queue_empty", status.QueueEmpty);
            w.WritePropertyName("search_sidecar");
            WriteSearchSidecarJson(w, status.SearchSidecar);
            w.WritePropertyName("content_corpus");
            WriteContentCorpusJson(w, status.ContentCorpus);
            WriteVectorsJson(w, status.Vectors);
            w.WritePropertyName("history_db");
            WriteHistorySidecarJson(w, facts.History);
            w.WriteEndObject();

            w.WritePropertyName("extraction_quality");
            WriteExtractionHealthJson(w, facts.Extraction);

            w.WritePropertyName("telemetry");
            w.WriteStartObject();
            w.WritePropertyName("outcomes");
            w.WriteStartObject();
            w.WriteNumber("ok_count", facts.TelemetryHealth.OkCount);
            w.WriteNumber("empty_count", facts.TelemetryHealth.EmptyCount);
            w.WriteNumber("error_count", facts.TelemetryHealth.ErrorCount);
            w.WriteNumber("total_calls", facts.TelemetryHealth.TotalCalls);
            w.WriteEndObject();
            w.WritePropertyName("summary");
            using (var telemetryDoc = JsonDocument.Parse(TelemetryRender.Json(facts.Telemetry)))
                telemetryDoc.RootElement.WriteTo(w);
            w.WriteEndObject();

            w.WritePropertyName("warnings");
            w.WriteStartArray();
            foreach (HealthWarning warning in facts.Warnings)
            {
                w.WriteStartObject();
                w.WriteString("code", warning.Code);
                w.WriteString("severity", warning.Severity);
                w.WriteString("message", warning.Message);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WritePropertyName("recommended_actions");
            w.WriteStartArray();
            foreach (string action in facts.RecommendedActions)
                w.WriteStringValue(action);
            w.WriteEndArray();

            w.WriteEndObject();
        }
        return Utf8(buffer);
    }

    private static void WriteExtractionHealthJson(Utf8JsonWriter w, WorkspaceExtractionHealthFacts facts)
    {
        w.WriteStartObject();
        WriteParseDiagnosticsJson(w, facts.ParseDiagnostics);
        WriteCapabilityGapsJson(w, facts.CapabilityGaps);
        WriteLanguageCapabilitiesJson(w, facts.LanguageCapabilities);
        WriteStructuralFactsJson(w, facts.StructuralFacts);
        WriteComplexityMetricsJson(w, facts.ComplexityMetrics);
        WriteFileStatusesJson(w, facts.Files);
        w.WriteEndObject();
    }

    private static void WriteParseDiagnosticsJson(
        Utf8JsonWriter w,
        HealthFactSection<ParseDiagnosticGroup> section)
    {
        w.WritePropertyName("parse_diagnostics");
        w.WriteStartObject();
        WriteSectionHeaderJson(w, section.Available, section.Error);
        w.WritePropertyName("rows");
        w.WriteStartArray();
        foreach (ParseDiagnosticGroup row in section.Rows)
        {
            w.WriteStartObject();
            w.WriteString("language", row.Language);
            w.WriteString("kind", row.Kind);
            w.WriteNumber("count", row.Count);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteCapabilityGapsJson(
        Utf8JsonWriter w,
        HealthFactSection<CapabilityGapGroup> section)
    {
        w.WritePropertyName("capability_gaps");
        w.WriteStartObject();
        WriteSectionHeaderJson(w, section.Available, section.Error);
        w.WritePropertyName("rows");
        w.WriteStartArray();
        foreach (CapabilityGapGroup row in section.Rows)
        {
            w.WriteStartObject();
            w.WriteString("language", row.Language);
            w.WriteString("capability", row.Capability);
            w.WriteString("status", row.Status);
            w.WriteNumber("count", row.Count);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteLanguageCapabilitiesJson(
        Utf8JsonWriter w,
        HealthFactSection<LanguageCapabilitySummary> section)
    {
        w.WritePropertyName("language_capabilities");
        w.WriteStartObject();
        WriteSectionHeaderJson(w, section.Available, section.Error);
        w.WritePropertyName("rows");
        w.WriteStartArray();
        foreach (LanguageCapabilitySummary row in section.Rows)
        {
            w.WriteStartObject();
            w.WriteString("language", row.Language);
            w.WriteNumber("target_symbols", row.TargetSymbols);
            w.WriteNumber("actual_symbols", row.ActualSymbols);
            w.WriteNumber("target_relationships", row.TargetRelationships);
            w.WriteNumber("actual_relationships", row.ActualRelationships);
            w.WriteNumber("target_pending_relationships", row.TargetPendingRelationships);
            w.WriteNumber("actual_pending_relationships", row.ActualPendingRelationships);
            w.WriteNumber("target_identifiers", row.TargetIdentifiers);
            w.WriteNumber("actual_identifiers", row.ActualIdentifiers);
            w.WriteNumber("target_types", row.TargetTypes);
            w.WriteNumber("actual_types", row.ActualTypes);
            WriteKindCoverageJson(w, row.KindCoverage);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteKindCoverageJson(Utf8JsonWriter w, IReadOnlyList<KindCoverageDomain> domains)
    {
        w.WritePropertyName("kind_coverage");
        w.WriteStartObject();
        foreach (KindCoverageDomain domain in domains)
        {
            w.WritePropertyName(domain.Domain);
            w.WriteStartObject();
            WriteKindArray(w, "supported", domain.Supported);
            WriteOpenGapArray(w, domain.OpenGaps);
            WriteKindArray(w, "not_applicable", domain.NotApplicable);
            w.WriteEndObject();
        }
        w.WriteEndObject();
    }

    // Artifact open_gaps entries are written verbatim (strings or structured gap objects) so declared
    // uncertainty always reaches consumers; the shape contract lives in docs/contracts/workspace-health-v1.md.
    private static void WriteOpenGapArray(Utf8JsonWriter w, IReadOnlyList<JsonElement> entries)
    {
        w.WritePropertyName("open_gaps");
        w.WriteStartArray();
        foreach (JsonElement entry in entries)
            entry.WriteTo(w);
        w.WriteEndArray();
    }

    private static void WriteKindArray(Utf8JsonWriter w, string propertyName, IReadOnlyList<string> values)
    {
        w.WritePropertyName(propertyName);
        w.WriteStartArray();
        foreach (string value in values)
            w.WriteStringValue(value);
        w.WriteEndArray();
    }

    private static void WriteFileStatusesJson(
        Utf8JsonWriter w,
        HealthFactSection<FileStatusGroup> section)
    {
        w.WritePropertyName("files");
        w.WriteStartObject();
        WriteSectionHeaderJson(w, section.Available, section.Error);
        w.WritePropertyName("rows");
        w.WriteStartArray();
        foreach (FileStatusGroup row in section.Rows)
        {
            w.WriteStartObject();
            w.WriteString("language", row.Language);
            w.WriteString("status", row.Status);
            w.WriteNumber("count", row.Count);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteStructuralFactsJson(
        Utf8JsonWriter w,
        HealthFactSection<StructuralFactGroup> section)
    {
        w.WritePropertyName("structural_facts");
        w.WriteStartObject();
        WriteSectionHeaderJson(w, section.Available, section.Error);
        w.WritePropertyName("rows");
        w.WriteStartArray();
        foreach (StructuralFactGroup row in section.Rows)
        {
            w.WriteStartObject();
            w.WriteString("language", row.Language);
            w.WriteString("pattern_id", row.PatternId);
            w.WriteString("capture_name", row.CaptureName);
            w.WriteNumber("count", row.Count);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteComplexityMetricsJson(
        Utf8JsonWriter w,
        HealthFactSection<ComplexityMetricGroup> section)
    {
        w.WritePropertyName("complexity_metrics");
        w.WriteStartObject();
        WriteSectionHeaderJson(w, section.Available, section.Error);
        w.WritePropertyName("rows");
        w.WriteStartArray();
        foreach (ComplexityMetricGroup row in section.Rows)
        {
            w.WriteStartObject();
            w.WriteString("language", row.Language);
            w.WriteString("scope", row.Scope);
            w.WriteString("algorithm_id", row.AlgorithmId);
            w.WriteNumber("count", row.Count);
            w.WriteNumber("max_decision_count", row.MaxDecisionCount);
            w.WriteNumber("max_loop_count", row.MaxLoopCount);
            w.WriteNumber("max_nesting_depth", row.MaxNestingDepth);
            if (row.MaxParameterCount is { } parameterCount)
                w.WriteNumber("max_parameter_count", parameterCount);
            else
                w.WriteNull("max_parameter_count");
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteSectionHeaderJson(Utf8JsonWriter w, bool available, string? error)
    {
        w.WriteBoolean("available", available);
        if (error is null) w.WriteNull("error");
        else w.WriteString("error", error);
    }

    private static long ParseDiagnosticCount(WorkspaceExtractionHealthFacts facts) =>
        facts.ParseDiagnostics.Rows.Sum(static row => row.Count);

    private static long OpenCapabilityGapCount(WorkspaceExtractionHealthFacts facts) =>
        facts.CapabilityGaps.Rows
            .Where(static row => string.Equals(row.Status, "open", StringComparison.OrdinalIgnoreCase))
            .Sum(static row => row.Count);

    private static long StructuralFactCount(WorkspaceExtractionHealthFacts facts) =>
        facts.StructuralFacts.Rows.Sum(static row => row.Count);

    private static long ComplexityMetricCount(WorkspaceExtractionHealthFacts facts) =>
        facts.ComplexityMetrics.Rows.Sum(static row => row.Count);

    // ---------- list ----------

    /// <summary>
    /// Render the list view. Miller serves ONE workspace per process (a multi-workspace registry is
    /// eros/commercial-tier — decision-1), so the list is the CURRENT workspace, honestly labelled so it is not
    /// mistaken for a multi-entry registry.
    /// </summary>
    public static string List(WorkspaceFacts facts, bool json) =>
        json ? ListJson(facts) : ListCompact(facts);

    /// <summary>
    /// Render the registry-backed workspace list. Entries are ordered current-first then most-recently-seen.
    /// <paramref name="filter"/> is a case-insensitive substring matched against display id or root, applied
    /// before the cap. <paramref name="limit"/> caps the compact view (default 20; <c>&lt;= 0</c> unlimited);
    /// JSON is unlimited unless <paramref name="limit"/> is set to a positive value (additive <c>last_seen_at</c>).
    /// </summary>
    public static string List(
        IReadOnlyList<WorkspaceListEntry> entries, bool json, string? filter = null, int? limit = null) =>
        json ? ListJson(entries, filter, limit ?? 0) : ListCompact(entries, filter, limit ?? DefaultListLimit);

    /// <summary>The default number of compact <c>workspace list</c> entries before the omitted-count tail.</summary>
    public const int DefaultListLimit = 20;

    private static bool MatchesFilter(in WorkspaceListEntry entry, string filter) =>
        entry.DisplayId.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || entry.Root.Contains(filter, StringComparison.OrdinalIgnoreCase);

    // Current workspace first, then most-recently-seen. LINQ OrderBy is stable, so equal keys keep registry order.
    private static List<WorkspaceListEntry> OrderAndFilter(
        IReadOnlyList<WorkspaceListEntry> entries, string? filter)
    {
        IEnumerable<WorkspaceListEntry> ordered = entries
            .OrderByDescending(static e => e.Current)
            .ThenByDescending(static e => e.LastSeenAt);
        if (!string.IsNullOrWhiteSpace(filter))
            ordered = ordered.Where(e => MatchesFilter(e, filter));
        return ordered.ToList();
    }

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

    private static string ListCompact(IReadOnlyList<WorkspaceListEntry> entries, string? filter, int limit)
    {
        int total = entries.Count;
        bool hasFilter = !string.IsNullOrWhiteSpace(filter);
        List<WorkspaceListEntry> matched = OrderAndFilter(entries, filter);

        var sb = new StringBuilder();

        // A filter that matches nothing is a helpful line, never an empty string — say so with the total count.
        if (hasFilter && matched.Count == 0)
        {
            sb.Append("# workspaces (0 shown)\n");
            sb.Append("no workspace matches filter \"").Append(filter).Append("\" — ")
              .Append(total).Append(" registered; adjust the substring or omit filter");
            return sb.ToString();
        }

        int cap = limit <= 0 ? matched.Count : limit;
        int shown = Math.Min(cap, matched.Count);
        List<WorkspaceListEntry> omitted = matched.Skip(shown).ToList();

        if (hasFilter)
            sb.Append("# workspaces (").Append(shown).Append(" of ").Append(matched.Count)
              .Append(" matched, ").Append(total).Append(" registered; filter=\"").Append(filter).Append("\")\n");
        else if (shown < total)
            sb.Append("# workspaces (").Append(shown).Append(" of ").Append(total).Append(")\n");
        else
            sb.Append("# workspaces (").Append(total).Append(")\n");

        foreach (WorkspaceListEntry entry in matched.Take(shown))
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

        if (omitted.Count > 0)
        {
            sb.Append("… ").Append(omitted.Count).Append(" more — raise limit or pass filter=<substring>\n");
            // Omitted error-state rows would otherwise be invisible past the cap — surface a discoverable summary.
            int omittedErrors = omitted.Count(static e => string.Equals(e.State, "error", StringComparison.Ordinal));
            if (omittedErrors > 0)
                sb.Append("errors: ").Append(omittedErrors)
                  .Append(" workspace(s) in error state — filter or raise limit to see them\n");
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

    private static string ListJson(IReadOnlyList<WorkspaceListEntry> entries, string? filter, int limit)
    {
        List<WorkspaceListEntry> matched = OrderAndFilter(entries, filter);
        // JSON is unlimited by default (existing consumers); a positive limit narrows it.
        IEnumerable<WorkspaceListEntry> visible = limit > 0 ? matched.Take(limit) : matched;

        var buffer = new ArrayBufferWriter<byte>();
        using (var w = NewWriter(buffer))
        {
            w.WriteStartObject();
            w.WritePropertyName("workspaces");
            w.WriteStartArray();
            foreach (WorkspaceListEntry entry in visible)
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
                // Additive: ISO-8601 recency stamp (round-trip "o" format) used for ordering.
                w.WriteString("last_seen_at", entry.LastSeenAt);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Utf8(buffer);
    }

    // ---------- onboarding ----------

    public static string Onboarding(WorkspaceOnboardingFacts facts, bool json) =>
        json ? OnboardingJson(facts) : OnboardingCompact(facts);

    private static string OnboardingCompact(WorkspaceOnboardingFacts facts)
    {
        var sb = new StringBuilder();
        sb.Append("# workspace onboarding\n");
        sb.Append("workspace: ").Append(DisplayId(
                facts.StatusFacts.Root,
                facts.StatusFacts.WorkspaceId,
                facts.StatusFacts.DisplayId))
            .Append("  ").Append(facts.StatusFacts.Root).Append('\n');
        sb.Append("telemetry: ").Append(facts.Telemetry.State)
            .Append("  calls ").Append(facts.Telemetry.TotalCalls.ToString(CultureInfo.InvariantCulture));
        if (facts.Telemetry.WindowStartTs is not null && facts.Telemetry.WindowEndTs is not null)
            sb.Append("  window ").Append(facts.Telemetry.WindowStartTs).Append("..").Append(facts.Telemetry.WindowEndTs);
        if (!facts.Telemetry.Available && !string.IsNullOrWhiteSpace(facts.Telemetry.Error))
            sb.Append("  error ").Append(facts.Telemetry.Error);
        sb.Append('\n');

        AppendLines(sb, "start here", facts.StartHere);

        if (facts.Telemetry.ToolMix.Count > 0)
        {
            sb.Append("tool mix:\n");
            foreach (TelemetryToolMix row in facts.Telemetry.ToolMix.Take(5))
                sb.Append("- ").Append(OnboardingLabel(row.Tool, row.Op)).Append("  calls ")
                    .Append(row.Calls.ToString(CultureInfo.InvariantCulture))
                    .Append("  empty ").Append(row.EmptyCount.ToString(CultureInfo.InvariantCulture))
                    .Append("  errors ").Append(row.ErrorCount.ToString(CultureInfo.InvariantCulture))
                    .Append("  p95 ").Append(row.P95Ms.ToString(CultureInfo.InvariantCulture)).Append("ms\n");
        }

        if (facts.Telemetry.SuccessfulFlows.Count > 0)
        {
            sb.Append("successful flows:\n");
            foreach (TelemetryFlow flow in facts.Telemetry.SuccessfulFlows.Take(5))
                sb.Append("- ").Append(flow.From).Append(" -> ").Append(flow.To)
                    .Append(" (").Append(flow.Calls.ToString(CultureInfo.InvariantCulture)).Append(")\n");
        }

        if (facts.HotTargets.Count > 0)
        {
            sb.Append("hot targets:\n");
            // Resolved targets carry a per-row label worth a line; unresolved hashes convey nothing
            // individually, so collapse however many there are into one aggregate line.
            List<RecoveredTargetHash> resolved = facts.HotTargets.Where(static t => !IsUnresolvedTarget(t)).ToList();
            List<RecoveredTargetHash> unresolved = facts.HotTargets.Where(static t => IsUnresolvedTarget(t)).ToList();

            foreach (RecoveredTargetHash target in resolved.Take(5))
                sb.Append("- ").Append(TargetLabel(target)).Append("  ")
                    .Append(target.Confidence).Append("  calls ")
                    .Append(target.Calls.ToString(CultureInfo.InvariantCulture))
                    .Append(target.CandidateCount > 1
                        ? $"  candidates {target.CandidateCount.ToString(CultureInfo.InvariantCulture)}"
                        : string.Empty)
                    .Append('\n');

            if (unresolved.Count > 0)
            {
                long unresolvedCalls = unresolved.Sum(static t => t.Calls);
                sb.Append("- unresolved repeated targets: ")
                    .Append(unresolved.Count.ToString(CultureInfo.InvariantCulture))
                    .Append(" (")
                    .Append(unresolvedCalls.ToString(CultureInfo.InvariantCulture))
                    .Append(" calls total)\n");
            }
        }

        if (facts.Telemetry.CommonMisses.Count > 0)
        {
            sb.Append("common misses:\n");
            foreach (TelemetryMiss miss in facts.Telemetry.CommonMisses.Take(5))
                sb.Append("- ").Append(OnboardingLabel(miss.Tool, miss.Op)).Append("  ")
                    .Append(miss.Reason).Append(" (")
                    .Append(miss.Calls.ToString(CultureInfo.InvariantCulture)).Append(")\n");
        }

        AppendLines(sb, "instruction notes", facts.InstructionNotes);
        AppendLines(sb, "privacy", facts.PrivacyNotes);
        return sb.ToString().TrimEnd('\n');
    }

    private static string OnboardingJson(WorkspaceOnboardingFacts facts)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = NewWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("operation", "onboarding");
            w.WritePropertyName("workspace");
            w.WriteStartObject();
            w.WriteString("root", facts.StatusFacts.Root);
            if (facts.StatusFacts.WorkspaceId is null) w.WriteNull("workspace_id");
            else w.WriteString("workspace_id", facts.StatusFacts.WorkspaceId);
            if (facts.StatusFacts.DisplayId is null) w.WriteNull("display_id");
            else w.WriteString("display_id", facts.StatusFacts.DisplayId);
            w.WriteString("db", facts.StatusFacts.DbPath);
            w.WriteEndObject();

            w.WritePropertyName("telemetry");
            w.WriteStartObject();
            w.WriteBoolean("available", facts.Telemetry.Available);
            w.WriteString("state", facts.Telemetry.State);
            w.WriteNumber("total_calls", facts.Telemetry.TotalCalls);
            if (facts.Telemetry.WindowStartTs is null) w.WriteNull("window_start_ts");
            else w.WriteString("window_start_ts", facts.Telemetry.WindowStartTs);
            if (facts.Telemetry.WindowEndTs is null) w.WriteNull("window_end_ts");
            else w.WriteString("window_end_ts", facts.Telemetry.WindowEndTs);
            if (facts.Telemetry.Error is null) w.WriteNull("error");
            else w.WriteString("error", facts.Telemetry.Error);
            w.WriteEndObject();

            WriteStringArray(w, "start_here", facts.StartHere);
            WriteToolMixJson(w, facts.Telemetry.ToolMix);
            WriteFlowsJson(w, facts.Telemetry.SuccessfulFlows);
            WriteHotTargetsJson(w, facts.HotTargets);
            WriteMissesJson(w, facts.Telemetry.CommonMisses);
            WriteFrictionJson(w, facts.Telemetry.Friction);
            WriteStringArray(w, "instruction_notes", facts.InstructionNotes);

            w.WritePropertyName("privacy");
            w.WriteStartObject();
            w.WriteBoolean("raw_queries_stored", false);
            w.WriteBoolean("raw_targets_stored", false);
            WriteStringArray(w, "notes", facts.PrivacyNotes);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Utf8(buffer);
    }

    private static void WriteToolMixJson(Utf8JsonWriter w, IReadOnlyList<TelemetryToolMix> rows)
    {
        w.WritePropertyName("tool_mix");
        w.WriteStartArray();
        foreach (TelemetryToolMix row in rows)
        {
            w.WriteStartObject();
            w.WriteString("tool", row.Tool);
            if (row.Op is null) w.WriteNull("op");
            else w.WriteString("op", row.Op);
            w.WriteNumber("calls", row.Calls);
            w.WriteNumber("ok_count", row.OkCount);
            w.WriteNumber("empty_count", row.EmptyCount);
            w.WriteNumber("error_count", row.ErrorCount);
            w.WriteNumber("avg_ms", row.AvgMs);
            w.WriteNumber("p95_ms", row.P95Ms);
            w.WriteNumber("max_ms", row.MaxMs);
            w.WriteNumber("result_count", row.ResultCount);
            w.WriteNumber("bytes_returned", row.BytesReturned);
            w.WriteNumber("est_tokens", row.EstTokens);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void WriteFlowsJson(Utf8JsonWriter w, IReadOnlyList<TelemetryFlow> flows)
    {
        w.WritePropertyName("successful_flows");
        w.WriteStartArray();
        foreach (TelemetryFlow flow in flows)
        {
            w.WriteStartObject();
            w.WriteString("from", flow.From);
            w.WriteString("to", flow.To);
            w.WriteNumber("calls", flow.Calls);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void WriteHotTargetsJson(Utf8JsonWriter w, IReadOnlyList<RecoveredTargetHash> targets)
    {
        w.WritePropertyName("hot_targets");
        w.WriteStartArray();
        foreach (RecoveredTargetHash target in targets)
        {
            w.WriteStartObject();
            w.WriteString("label", TargetLabel(target));
            w.WriteString("confidence", target.Confidence);
            if (target.SymbolId is null) w.WriteNull("symbol_id");
            else w.WriteString("symbol_id", target.SymbolId);
            if (target.Name is null) w.WriteNull("name");
            else w.WriteString("name", target.Name);
            if (target.Kind is null) w.WriteNull("kind");
            else w.WriteString("kind", target.Kind);
            if (target.Path is null) w.WriteNull("path");
            else w.WriteString("path", target.Path);
            if (target.StartLine is { } startLine) w.WriteNumber("start_line", startLine);
            else w.WriteNull("start_line");
            w.WriteNumber("calls", target.Calls);
            w.WriteNumber("candidate_count", target.CandidateCount);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void WriteMissesJson(Utf8JsonWriter w, IReadOnlyList<TelemetryMiss> misses)
    {
        w.WritePropertyName("common_misses");
        w.WriteStartArray();
        foreach (TelemetryMiss miss in misses)
        {
            w.WriteStartObject();
            w.WriteString("tool", miss.Tool);
            if (miss.Op is null) w.WriteNull("op");
            else w.WriteString("op", miss.Op);
            w.WriteString("reason", miss.Reason);
            w.WriteNumber("calls", miss.Calls);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void WriteFrictionJson(Utf8JsonWriter w, IReadOnlyList<TelemetryFriction> friction)
    {
        w.WritePropertyName("friction");
        w.WriteStartArray();
        foreach (TelemetryFriction row in friction)
        {
            w.WriteStartObject();
            w.WriteString("tool", row.Tool);
            if (row.Op is null) w.WriteNull("op");
            else w.WriteString("op", row.Op);
            w.WriteNumber("calls", row.Calls);
            w.WriteNumber("avg_ms", row.AvgMs);
            w.WriteNumber("p95_ms", row.P95Ms);
            w.WriteNumber("max_ms", row.MaxMs);
            w.WriteNumber("bytes_returned", row.BytesReturned);
            w.WriteNumber("est_tokens", row.EstTokens);
            w.WriteNumber("empty_count", row.EmptyCount);
            w.WriteNumber("error_count", row.ErrorCount);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void WriteStringArray(Utf8JsonWriter w, string propertyName, IReadOnlyList<string> values)
    {
        w.WritePropertyName(propertyName);
        w.WriteStartArray();
        foreach (string value in values)
            w.WriteStringValue(value);
        w.WriteEndArray();
    }

    private static void AppendLines(StringBuilder sb, string heading, IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
            return;
        sb.Append(heading).Append(":\n");
        foreach (string line in lines)
            sb.Append("- ").Append(line).Append('\n');
    }

    private static string TargetLabel(RecoveredTargetHash target)
    {
        if (target.Path is not null && target.Name is not null)
            return $"{target.Name}  {target.Path}";
        if (target.Path is not null)
            return target.Path;
        if (target.Name is not null)
            return target.Name;
        return "unresolved repeated target";
    }

    // True exactly when TargetLabel would fall through to the placeholder "unresolved repeated target"
    // (both name and path unknown) — the rows worth collapsing into a single aggregate onboarding line.
    private static bool IsUnresolvedTarget(RecoveredTargetHash target) =>
        target.Path is null && target.Name is null;

    private static string OnboardingLabel(string tool, string? op) =>
        string.IsNullOrWhiteSpace(op) ? tool : tool + ":" + op;

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
        if (result.ScanDurationMs is { } scanMs)
            sb.Append('\n').Append("scan_duration_ms: ").Append(scanMs);
        if (result.DurationMs is { } totalMs)
            sb.Append('\n').Append("duration_ms: ").Append(totalMs);
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
            if (result.ScanDurationMs is { } scanMs) w.WriteNumber("scan_duration_ms", scanMs);
            else w.WriteNull("scan_duration_ms");
            if (result.DurationMs is { } totalMs) w.WriteNumber("duration_ms", totalMs);
            else w.WriteNull("duration_ms");
            if (result.IndexFresh is { } fresh) w.WriteBoolean("index_fresh", fresh);
            else w.WriteNull("index_fresh");
            if (string.IsNullOrEmpty(result.Note)) w.WriteNull("note");
            else w.WriteString("note", result.Note);
            w.WritePropertyName("search_sidecar");
            WriteSearchSidecarJson(w, result.SearchSidecar);
            w.WritePropertyName("content_corpus");
            WriteContentCorpusJson(w, result.ContentCorpus);
            if (string.IsNullOrEmpty(result.ArtifactId)) w.WriteNull("artifact_id");
            else w.WriteString("artifact_id", result.ArtifactId);
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
        WorkspaceRemoveResult.Outcome.Removed when result.IndexDirDeleted =>
            $"removed workspace index: {result.MillerDir}",
        WorkspaceRemoveResult.Outcome.Removed =>
            $"removed workspace registry entry: {result.Root ?? result.WorkspaceId ?? result.MillerDir} " +
            $"(no index dir at {result.MillerDir})",
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
            w.WriteBoolean("index_dir_deleted", result.IndexDirDeleted);
            w.WriteString("message", RemoveCompact(result));
            w.WriteEndObject();
        }
        return Utf8(buffer);
    }

    // ---------- prune ----------

    /// <summary>Render a <c>prune</c> result (removed / would-remove + kept count).</summary>
    public static string Prune(WorkspacePruneResult result, bool json) =>
        json ? PruneJson(result) : PruneCompact(result);

    private const int PruneCompactExampleCap = 10;

    private static string PruneCompact(WorkspacePruneResult result)
    {
        var lines = new List<string>
        {
            result.DryRun ? $"would prune: {result.Pruned.Count}" : $"pruned: {result.Pruned.Count}",
        };
        foreach (WorkspacePruneEntry entry in result.Pruned.Take(PruneCompactExampleCap))
            lines.Add($"  {entry.DisplayId} {entry.Root}");
        lines.Add($"kept: {result.Kept}");
        return string.Join('\n', lines);
    }

    private static string PruneJson(WorkspacePruneResult result)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = NewWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteBoolean("dry_run", result.DryRun);
            w.WriteStartArray("pruned");
            foreach (WorkspacePruneEntry entry in result.Pruned)
            {
                w.WriteStartObject();
                w.WriteString("workspace_id", entry.WorkspaceId);
                w.WriteString("display_id", entry.DisplayId);
                w.WriteString("root", entry.Root);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteNumber("kept", result.Kept);
            w.WriteEndObject();
        }
        return Utf8(buffer);
    }

    // ---------- shared helpers ----------

    private static Utf8JsonWriter NewWriter(ArrayBufferWriter<byte> buffer) =>
        new(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private static string Utf8(ArrayBufferWriter<byte> buffer) => Encoding.UTF8.GetString(buffer.WrittenSpan);
}
