using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Miller.Core.Freshness;
using Miller.Indexing;
using Miller.Indexing.Reads;
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
/// <param name="SemanticBroker">Status of the shared semantic broker, when known.</param>
/// <param name="ScanGovernor">This workspace's position in the user-global scan-admission queue, when there is
/// one. Null — idle, disabled, or no corroborated holder — renders nowhere, so default status output stays
/// byte-identical.</param>
/// <param name="ScanFailure">The persisted whole-repo scan-failure record, when one exists. Null — the normal
/// state — renders nowhere, so default status output stays byte-identical.</param>
/// <param name="RebindProvenance">Where this artifact was rebound from, when it was rebound at all. Null — the
/// normal state — renders nowhere, so default status output stays byte-identical.</param>
public sealed record StoreWorkspaceFacts(
    string FamilyId,
    string ViewId,
    string GenerationName,
    long ManifestGeneration,
    string ManifestHash,
    long StoreLogSequence,
    string IndexLevel,
    bool LegacyArtifactPresent,
    string MigrationState,
    string RollbackState,
    string? StoreRoot = null,
    IReadOnlyList<string>? MemberDisplayLabels = null,
    int MemberCount = 0,
    string State = "ready",
    string? Failure = null,
    string? Error = null)
{
    public static StoreWorkspaceFacts Unavailable(string state, string failure, string error) =>
        new(
            string.Empty,
            string.Empty,
            string.Empty,
            0,
            string.Empty,
            0,
            string.Empty,
            false,
            "unknown",
            "unavailable",
            State: state,
            Failure: failure,
            Error: error);

    public static StoreWorkspaceFacts Unavailable(FamilyStoreReadException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        string failure = exception.Failure switch
        {
            FamilyStoreReadFailure.BindingNotReady => "binding_not_ready",
            FamilyStoreReadFailure.CurrentMissing => "current_missing",
            FamilyStoreReadFailure.CurrentMalformed => "current_malformed",
            FamilyStoreReadFailure.GenerationMissing => "generation_missing",
            FamilyStoreReadFailure.StoreMissing => "store_missing",
            FamilyStoreReadFailure.CoordinatorMissing => "coordinator_missing",
            FamilyStoreReadFailure.SchemaIncompatible => "schema_incompatible",
            FamilyStoreReadFailure.ReaderFloorIncompatible => "reader_floor_incompatible",
            FamilyStoreReadFailure.FamilyMismatch => "family_mismatch",
            FamilyStoreReadFailure.ViewNotFound => "view_not_found",
            FamilyStoreReadFailure.ViewRootMismatch => "view_root_mismatch",
            FamilyStoreReadFailure.ManifestMissing => "manifest_missing",
            FamilyStoreReadFailure.Corrupt => "corrupt",
            _ => "unknown",
        };
        string state = exception.Failure is
            FamilyStoreReadFailure.SchemaIncompatible or FamilyStoreReadFailure.ReaderFloorIncompatible
                ? "incompatible"
                : "failed";
        return Unavailable(state, failure, exception.Message);
    }
}

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
    VectorSidecarFacts? Vectors = null,
    SemanticBrokerFacts? SemanticBroker = null,
    ScanGovernorSnapshot? ScanGovernor = null,
    ScanFailureRecord? ScanFailure = null,
    IndexLevelFacts? IndexLevel = null,
    RebindProvenanceFacts? RebindProvenance = null,
    StoreWorkspaceFacts? Store = null);

/// <summary>
/// Where a rebound artifact came from (P3 provenance surfacing): the source root recorded in the artifact's
/// additive <c>rebound_from_root</c> key, that root's registered display id when it is registered on this
/// machine, the source artifact's generation id, and the retarget instant AS THE EXTRACTOR STORED IT. An
/// artifact that was never rebound produces a NULL fact, which renders nowhere — default status/health output
/// stays byte-identical, the same emit-nothing rule scan_failure follows.
/// </summary>
/// <param name="SourceRoot">The previous recorded root, always present when the fact exists.</param>
/// <param name="SourceWorkspace">The source root's registered display id, or null when that root is not
/// registered on this machine. The raw root still renders either way.</param>
/// <param name="SourceArtifactId">The source artifact's generation id, null when the artifact omits it.</param>
/// <param name="ReboundAt">The retarget instant, rendered verbatim as stored (never reparsed or
/// reformatted).</param>
public sealed record RebindProvenanceFacts(
    string SourceRoot,
    string? SourceWorkspace,
    string? SourceArtifactId,
    string? ReboundAt);

/// <summary>
/// The progressive-indexing facts for a workspace serving a SYMBOLS-level artifact. A full-level artifact (and
/// every pre-levels artifact) produces a NULL fact, which renders nowhere — default status/health output stays
/// byte-identical, the same emit-nothing rule scan_failure follows.
/// </summary>
public sealed record IndexLevelFacts(string Level, bool UpgradeOwed, string Policy);

public sealed record SemanticBrokerFacts(
    string State,
    string? EndpointIdentity,
    string? Role,
    string ServerVersion,
    string? ModelId,
    string? ModelSha256,
    string? Backend,
    bool AcceleratorLeaseHeld,
    int ReconnectCount,
    int SpawnAttempts,
    int RetiredOwnerCount,
    bool OwnershipDegraded,
    string? OwnershipDegradedReason,
    string? BackendDegradedReason,
    int? OwnerProcessId)
{
    public static SemanticBrokerFacts From(SemanticMode mode, SemanticBrokerSnapshot? snapshot)
    {
        if (mode == SemanticMode.Off)
        {
            return new SemanticBrokerFacts(
                "off", null, null, SemanticEmbeddingSession.ProtocolVersion.ToString(CultureInfo.InvariantCulture),
                null, null, null, false, 0, 0, 0, false, null, null, null);
        }

        if (snapshot is null)
        {
            return new SemanticBrokerFacts(
                "not_started", null, null,
                SemanticEmbeddingSession.ProtocolVersion.ToString(CultureInfo.InvariantCulture),
                null, null, null, false, 0, 0, 0, false, null, null, null);
        }

        return new SemanticBrokerFacts(
            snapshot.State,
            snapshot.EndpointIdentity,
            snapshot.IsOwner ? "owner" : "non_owner",
            snapshot.ServerVersion,
            snapshot.ModelId,
            snapshot.ModelSha256,
            snapshot.Backend,
            snapshot.AcceleratorLeaseHeld,
            snapshot.ReconnectCount,
            snapshot.SpawnAttempts,
            snapshot.RetiredOwnerCount,
            snapshot.OwnershipDegraded,
            snapshot.OwnershipDegradedReason,
            snapshot.BackendDegradedReason,
            snapshot.OwnerProcessId);
    }
}

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
    DateTimeOffset LastSeenAt = default,
    bool RootMissing = false);

public sealed record WorkspaceListFacts(
    IReadOnlyList<WorkspaceListEntry> Entries,
    int Registered,
    int Matched,
    int Returned,
    int Omitted,
    int OmittedErrors,
    string? Filter,
    int? Limit,
    int RegisteredMissing = 0,
    int MatchedMissing = 0,
    int ReturnedMissing = 0);

public enum WorkspaceHealthFormat
{
    Compact,
    JsonSummary,
    Json,
    Markdown,
}

/// <summary>
/// The result of an <c>open</c>/<c>refresh</c>/<c>full</c> action (M7 decision-3): whether a scan ran, whether the
/// freshness poll swapped a newer index in, the revision the index now reflects, and an optional HONEST note.
/// </summary>
/// <param name="Operation">The operation name (<c>open</c>, <c>refresh</c>, or <c>full</c>).</param>
/// <param name="Scanned">Whether this action ran an <c>extract scan</c>.</param>
/// <param name="Swapped">True iff the on-demand freshness poll rebuilt + swapped a newer index.</param>
/// <param name="Revision">The revision the held index reflects after the action.</param>
/// <param name="Note">An honesty note, or null.</param>
/// <param name="ScanDurationMs">Wall ms of the julie-extract scan attempt (set even for a failed/killed scan);
/// null when no scan ran or the path does not measure it.</param>
/// <param name="DurationMs">Wall ms of the whole refresh attempt, when measured.</param>
/// <param name="Downgraded">
/// True when a requested from-scratch rebuild ran as a delta reconcile against the still-servable prior artifact
/// after repeated scan failures. <c>Scanned</c> is then true for the delta that DID run, so this flag is the only
/// thing distinguishing "your rebuild happened" from "your rebuild is still owed" — never drop it from a render.
/// </param>
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
    string? ArtifactId = null,
    bool Downgraded = false);

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
/// Result of a registered workspace removal attempt.
/// </summary>
public readonly record struct WorkspaceRemoveResult(
    WorkspaceRemoveResult.Outcome Result,
    string MillerDir,
    string? WorkspaceId = null,
    string? Root = null,
    bool IndexDirDeleted = false,
    StoreSidecarReclaimResult SidecarReclaim = default)
{
    /// <summary>Removal outcome.</summary>
    public enum Outcome
    {
        /// <summary>The <c>.miller</c> index dir was deleted.</summary>
        Removed,

        /// <summary>Refused: the path is the workspace this process is serving (the index is in use).</summary>
        RefusedLive,

        /// <summary>Refused: another process holds the target workspace writer lock.</summary>
        RefusedInUse,

        /// <summary>Refused: the target is a sensitive or machine-global Miller directory.</summary>
        RefusedSensitive,

        /// <summary>Refused: the registry row does not map to its canonical workspace index path.</summary>
        RefusedInvalidRegistration,

        /// <summary>No registered workspace matched the requested target.</summary>
        NotFound,
    }

    /// <summary>The workspace was removed from Miller; <paramref name="indexDirDeleted"/> records whether the index dir existed and was deleted.</summary>
    public static WorkspaceRemoveResult Removed(
        string millerDir,
        string? workspaceId = null,
        string? root = null,
        bool indexDirDeleted = true,
        StoreSidecarReclaimResult sidecarReclaim = default) =>
        new(Outcome.Removed, millerDir, workspaceId, root, indexDirDeleted, sidecarReclaim);

    /// <summary>Refused because the path is the live (in-use) workspace.</summary>
    public static WorkspaceRemoveResult RefusedLive(string millerDir, string? workspaceId = null, string? root = null) =>
        new(Outcome.RefusedLive, millerDir, workspaceId, root);

    /// <summary>Refused because another Miller is using the target workspace.</summary>
    public static WorkspaceRemoveResult RefusedInUse(string millerDir, string? workspaceId = null, string? root = null) =>
        new(Outcome.RefusedInUse, millerDir, workspaceId, root);

    public static WorkspaceRemoveResult RefusedSensitive(
        string millerDir,
        string? workspaceId = null,
        string? root = null) =>
        new(Outcome.RefusedSensitive, millerDir, workspaceId, root);

    public static WorkspaceRemoveResult RefusedInvalidRegistration(
        string millerDir,
        string? workspaceId = null,
        string? root = null) =>
        new(Outcome.RefusedInvalidRegistration, millerDir, workspaceId, root);

    /// <summary>No registered workspace matched the target.</summary>
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
    int Kept,
    StoreSidecarReclaimResult SidecarReclaim = default);

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
        if (facts.SemanticBroker is { } broker)
            sb.Append("semantic_broker: ").Append(SemanticBrokerLabel(broker)).Append('\n');
        if (ScanGovernorLabel(facts.ScanGovernor) is { } governorLabel)
            sb.Append("scan_governor: ").Append(governorLabel).Append('\n');
        if (ScanFailureLabel(facts.ScanFailure) is { } scanFailureLabel)
            sb.Append("scan_failure: ").Append(scanFailureLabel).Append('\n');
        if (IndexLevelLabel(facts.IndexLevel) is { } indexLevelLabel)
            sb.Append("index_level: ").Append(indexLevelLabel).Append('\n');
        if (RebindProvenanceLabel(facts.RebindProvenance) is { } rebindLabel)
            sb.Append("rebound_from: ").Append(rebindLabel).Append('\n');
        if (facts.Store is { } store)
            sb.Append("store: ").Append(StoreProvenanceLabel(store)).Append('\n');
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
        "model-not-prepared" => string.IsNullOrWhiteSpace(facts.Reason)
            ? "model-not-prepared (run `miller semantic prepare`)"
            : $"model-not-prepared ({facts.Reason}; run `miller semantic prepare`)",
        "unavailable" => string.IsNullOrWhiteSpace(facts.Reason)
            ? "unavailable"
            : $"unavailable ({facts.Reason})",
        _ => facts.State,
    };

    // Null ⇒ render no `scan_governor:` line at all. An idle workspace, a disabled governor, and an unreadable
    // owner record all produce a NULL fact, so anything but a live waiting/holding position produces
    // byte-identical output to a build without the governor — the same rule VectorsLabel follows.
    private static string? ScanGovernorLabel(ScanGovernorSnapshot? facts)
    {
        if (facts is null)
            return null;

        var label = new StringBuilder(facts.State);
        label.Append(' ').Append(ElapsedSeconds(facts.SinceUtc).ToString(CultureInfo.InvariantCulture)).Append('s');
        if (facts.HolderPid is { } holderPid)
        {
            label.Append(" (holder pid ").Append(holderPid.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(facts.HolderWorkspaceRoot))
                label.Append(' ').Append(facts.HolderWorkspaceRoot);
            label.Append(')');
        }
        else if (!string.IsNullOrWhiteSpace(facts.Reason))
        {
            label.Append(" (").Append(facts.Reason).Append(')');
        }

        return label.ToString();
    }

    private static void WriteScanGovernorJson(Utf8JsonWriter w, ScanGovernorSnapshot facts)
    {
        w.WriteStartObject();
        w.WriteString("state", facts.State);
        if (facts.Reason is null) w.WriteNull("reason");
        else w.WriteString("reason", facts.Reason);
        if (facts.SinceUtc is { } since)
        {
            w.WriteString("since_utc", since.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            w.WriteNumber("waiting_seconds", ElapsedSeconds(since));
        }
        else
        {
            w.WriteNull("since_utc");
            w.WriteNull("waiting_seconds");
        }
        if (facts.HolderPid is { } holderPid) w.WriteNumber("holder_pid", holderPid);
        else w.WriteNull("holder_pid");
        if (facts.HolderWorkspaceRoot is null) w.WriteNull("holder_workspace_root");
        else w.WriteString("holder_workspace_root", facts.HolderWorkspaceRoot);
        w.WriteEndObject();
    }

    // Null ⇒ render no `scan_failure:` line at all — the same emit-nothing rule ScanGovernorLabel follows, so a
    // workspace whose scans have never failed produces byte-identical output to a build without the record.
    private static string? ScanFailureLabel(ScanFailureRecord? facts)
    {
        if (facts is null)
            return null;

        var label = new StringBuilder(facts.Intent.ToString());
        label.Append(" x").Append(facts.ConsecutiveFailures.ToString(CultureInfo.InvariantCulture));
        if (facts.ExitCode is { } exitCode)
            label.Append(" exit ").Append(exitCode.ToString(CultureInfo.InvariantCulture));
        label.Append(" jobs ").Append(facts.Jobs.ToString(CultureInfo.InvariantCulture));
        label.Append(" retry_at ")
             .Append(facts.NextAttemptAtUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        return label.ToString();
    }

    private static void WriteScanFailureJson(Utf8JsonWriter w, ScanFailureRecord facts)
    {
        w.WriteStartObject();
        w.WriteString("intent", facts.Intent.ToString());
        if (facts.ExitCode is { } exitCode) w.WriteNumber("exit_code", exitCode);
        else w.WriteNull("exit_code");
        w.WriteNumber("consecutive_failures", facts.ConsecutiveFailures);
        w.WriteNumber("jobs", facts.Jobs);
        w.WriteString(
            "last_failure_utc", facts.LastFailureAtUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        w.WriteString(
            "next_attempt_utc", facts.NextAttemptAtUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        w.WriteNumber("retry_in_seconds", RemainingSeconds(facts.NextAttemptAtUtc));
        w.WriteEndObject();
    }

    // Null ⇒ render no `rebound_from:` line at all — the same emit-nothing rule ScanFailureLabel follows, so an
    // artifact that was never rebound produces byte-identical output to a build without provenance.
    private static string? RebindProvenanceLabel(RebindProvenanceFacts? facts)
    {
        if (facts is null)
            return null;

        var label = new StringBuilder();
        if (string.IsNullOrWhiteSpace(facts.SourceWorkspace))
            label.Append(facts.SourceRoot);
        else
            label.Append(facts.SourceWorkspace).Append(" (").Append(facts.SourceRoot).Append(')');
        if (!string.IsNullOrWhiteSpace(facts.ReboundAt))
            label.Append(" at ").Append(facts.ReboundAt);
        return label.ToString();
    }

    private static void WriteRebindProvenanceJson(Utf8JsonWriter w, RebindProvenanceFacts facts)
    {
        w.WriteStartObject();
        w.WriteString("source_root", facts.SourceRoot);
        if (facts.SourceWorkspace is null) w.WriteNull("source_workspace");
        else w.WriteString("source_workspace", facts.SourceWorkspace);
        if (facts.SourceArtifactId is null) w.WriteNull("source_artifact_id");
        else w.WriteString("source_artifact_id", facts.SourceArtifactId);
        if (facts.ReboundAt is null) w.WriteNull("rebound_at");
        else w.WriteString("rebound_at", facts.ReboundAt);
        w.WriteEndObject();
    }

    private static string StoreProvenanceLabel(StoreWorkspaceFacts facts)
    {
        if (!string.Equals(facts.State, "ready", StringComparison.Ordinal))
            return $"state={facts.State}  failure={facts.Failure}";

        var label = new StringBuilder()
            .Append("family=").Append(facts.FamilyId)
            .Append("  view=").Append(facts.ViewId)
            .Append("  generation=").Append(facts.ManifestGeneration)
            .Append("  manifest=").Append(facts.ManifestHash)
            .Append("  sequence=").Append(facts.StoreLogSequence)
            .Append("  level=").Append(facts.IndexLevel)
            .Append("  migration=").Append(facts.MigrationState)
            .Append("  rollback=").Append(facts.RollbackState);
        if (!string.IsNullOrWhiteSpace(facts.StoreRoot))
            label.Append("  root=").Append(facts.StoreRoot);
        if (facts.MemberDisplayLabels is { Count: > 0 } members)
        {
            label.Append("  members=").AppendJoin(',', members);
            int omitted = Math.Max(0, facts.MemberCount - members.Count);
            if (omitted > 0)
                label.Append(" (+").Append(omitted).Append(" more)");
        }
        return label.ToString();
    }

    private static void WriteStoreProvenanceJson(Utf8JsonWriter w, StoreWorkspaceFacts facts)
    {
        w.WriteStartObject();
        w.WriteString("state", facts.State);
        if (!string.Equals(facts.State, "ready", StringComparison.Ordinal))
        {
            if (facts.Failure is null) w.WriteNull("failure");
            else w.WriteString("failure", facts.Failure);
            if (facts.Error is null) w.WriteNull("error");
            else w.WriteString("error", facts.Error);
            w.WriteEndObject();
            return;
        }
        w.WriteString("family_id", facts.FamilyId);
        w.WriteString("view_id", facts.ViewId);
        w.WriteString("generation_name", facts.GenerationName);
        w.WriteNumber("manifest_generation", facts.ManifestGeneration);
        w.WriteString("manifest_hash", facts.ManifestHash);
        w.WriteNumber("store_log_sequence", facts.StoreLogSequence);
        w.WriteString("index_level", facts.IndexLevel);
        w.WriteBoolean("legacy_artifact_present", facts.LegacyArtifactPresent);
        w.WriteString("migration_state", facts.MigrationState);
        w.WriteString("rollback_state", facts.RollbackState);
        if (facts.StoreRoot is null) w.WriteNull("store_root");
        else w.WriteString("store_root", facts.StoreRoot);
        w.WriteNumber("member_count", facts.MemberCount);
        w.WriteNumber(
            "members_omitted",
            Math.Max(0, facts.MemberCount - (facts.MemberDisplayLabels?.Count ?? 0)));
        w.WriteStartArray("member_display_labels");
        foreach (string label in facts.MemberDisplayLabels ?? [])
            w.WriteStringValue(label);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static long RemainingSeconds(DateTimeOffset untilUtc) =>
        Math.Max(0, (long)(untilUtc - DateTimeOffset.UtcNow).TotalSeconds);

    // Null ⇒ render no `index_level:` line at all — full-level and pre-levels artifacts stay byte-identical.
    private static string? IndexLevelLabel(IndexLevelFacts? facts)
    {
        if (facts is null)
            return null;
        return facts.UpgradeOwed
            ? $"{facts.Level} (full-level upgrade owed — a leading session converges it; 'workspace full' forces it)"
            : $"{facts.Level} (policy {facts.Policy})";
    }

    private static void WriteIndexLevelJson(Utf8JsonWriter w, IndexLevelFacts facts)
    {
        w.WriteStartObject();
        w.WriteString("level", facts.Level);
        w.WriteBoolean("upgrade_owed", facts.UpgradeOwed);
        w.WriteString("policy", facts.Policy);
        w.WriteEndObject();
    }

    private static string SemanticBrokerLabel(SemanticBrokerFacts facts)
    {
        var label = new StringBuilder(facts.State);
        if (!string.IsNullOrWhiteSpace(facts.EndpointIdentity))
            label.Append("  endpoint: ").Append(facts.EndpointIdentity);
        if (!string.IsNullOrWhiteSpace(facts.Role))
            label.Append("  role: ").Append(facts.Role);
        label.Append("  server: ").Append(facts.ServerVersion);
        if (!string.IsNullOrWhiteSpace(facts.ModelId))
            label.Append("  model: ").Append(facts.ModelId);
        if (!string.IsNullOrWhiteSpace(facts.Backend))
            label.Append("  backend: ").Append(facts.Backend);
        label.Append("  accelerator_lease: ").Append(facts.AcceleratorLeaseHeld ? "held" : "not_held")
             .Append("  reconnects: ").Append(facts.ReconnectCount)
             .Append("  spawns: ").Append(facts.SpawnAttempts)
             .Append("  retired_owners: ").Append(facts.RetiredOwnerCount);
        if (!string.IsNullOrWhiteSpace(facts.OwnershipDegradedReason))
            label.Append("  ownership_degraded: ").Append(facts.OwnershipDegradedReason);
        if (!string.IsNullOrWhiteSpace(facts.BackendDegradedReason))
            label.Append("  backend_degraded: ").Append(facts.BackendDegradedReason);
        return label.ToString();
    }

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

        ToolStat busiest = TelemetryHighlights.Busiest(telemetry.Tools)!.Value;
        ToolStat? slowest = TelemetryHighlights.Slowest(telemetry.Tools);
        long errors = telemetry.Tools.Sum(static tool => tool.ErrorCount);
        var sb = new StringBuilder();
        sb.Append("telemetry: ");
        if (telemetry.WindowDays is { } windowDays)
            sb.Append(windowDays.ToString(CultureInfo.InvariantCulture)).Append("d  ");
        sb.Append(telemetry.TotalCalls.ToString(CultureInfo.InvariantCulture)).Append(" calls");
        if (errors > 0)
            sb.Append("  errors=").Append(errors.ToString(CultureInfo.InvariantCulture));
        sb.Append("  busiest=").Append(busiest.Tool)
          .Append(" p95=").Append(busiest.P95Ms.ToString(CultureInfo.InvariantCulture)).Append("ms");
        // The busiest tool is often the fastest one, so its p95 answers a different question than "what is
        // slow here". Naming the slowest tool too costs one clause and stops the label being misread. An
        // absent clause means the busiest tool IS the slowest; `n/a` means no tool has enough calls to say.
        if (slowest is { } slow)
        {
            if (!string.Equals(slow.Tool, busiest.Tool, StringComparison.Ordinal))
            {
                sb.Append("  slowest=").Append(slow.Tool)
                  .Append(" p95=").Append(slow.P95Ms.ToString(CultureInfo.InvariantCulture)).Append("ms");
            }
        }
        else
        {
            sb.Append("  slowest=n/a");
        }

        // DroppedWrites is this process's in-memory counter, so it spans the process, not the window this line
        // names. Say so, or a reader takes it for a windowed figure.
        if (telemetry.DroppedWrites > 0)
        {
            sb.Append("  dropped=").Append(telemetry.DroppedWrites.ToString(CultureInfo.InvariantCulture))
              .Append(" since start");
        }
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

            if (facts.SemanticBroker is { } broker)
            {
                w.WritePropertyName("semantic_broker");
                WriteSemanticBrokerJson(w, broker, includeOwnerPid: true);
            }

            if (facts.ScanGovernor is { } scanGovernor)
            {
                w.WritePropertyName("scan_governor");
                WriteScanGovernorJson(w, scanGovernor);
            }

            if (facts.ScanFailure is { } scanFailure)
            {
                w.WritePropertyName("scan_failure");
                WriteScanFailureJson(w, scanFailure);
            }

            if (facts.IndexLevel is { } indexLevel)
            {
                w.WritePropertyName("index_level");
                WriteIndexLevelJson(w, indexLevel);
            }

            if (facts.RebindProvenance is { } rebindProvenance)
            {
                w.WritePropertyName("rebound_from");
                WriteRebindProvenanceJson(w, rebindProvenance);
            }

            if (facts.Store is { } store)
            {
                w.WritePropertyName("store");
                WriteStoreProvenanceJson(w, store);
            }

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

    private static void WriteSemanticBrokerJson(
        Utf8JsonWriter writer,
        SemanticBrokerFacts facts,
        bool includeOwnerPid)
    {
        writer.WriteStartObject();
        writer.WriteString("state", facts.State);
        if (facts.EndpointIdentity is null) writer.WriteNull("endpoint_identity");
        else writer.WriteString("endpoint_identity", facts.EndpointIdentity);
        if (facts.Role is null) writer.WriteNull("role");
        else writer.WriteString("role", facts.Role);
        writer.WriteString("server_version", facts.ServerVersion);
        if (facts.ModelId is null) writer.WriteNull("model_id");
        else writer.WriteString("model_id", facts.ModelId);
        if (facts.ModelSha256 is null) writer.WriteNull("model_sha256");
        else writer.WriteString("model_sha256", facts.ModelSha256);
        if (facts.Backend is null) writer.WriteNull("backend");
        else writer.WriteString("backend", facts.Backend);
        writer.WriteBoolean("accelerator_lease_held", facts.AcceleratorLeaseHeld);
        writer.WriteNumber("reconnect_count", facts.ReconnectCount);
        writer.WriteNumber("spawn_attempts", facts.SpawnAttempts);
        writer.WriteNumber("retired_owner_count", facts.RetiredOwnerCount);
        writer.WriteBoolean("ownership_degraded", facts.OwnershipDegraded);
        if (facts.OwnershipDegradedReason is null) writer.WriteNull("ownership_degraded_reason");
        else writer.WriteString("ownership_degraded_reason", facts.OwnershipDegradedReason);
        if (facts.BackendDegradedReason is null) writer.WriteNull("backend_degraded_reason");
        else writer.WriteString("backend_degraded_reason", facts.BackendDegradedReason);
        if (includeOwnerPid)
        {
            if (facts.OwnerProcessId is { } ownerPid) writer.WriteNumber("owner_pid", ownerPid);
            else writer.WriteNull("owner_pid");
        }
        writer.WriteEndObject();
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
        Health(facts, json ? WorkspaceHealthFormat.Json : WorkspaceHealthFormat.Compact);

    public static string Health(WorkspaceHealthFacts facts, WorkspaceHealthFormat format) =>
        format switch
        {
            WorkspaceHealthFormat.Compact => HealthCompact(facts),
            WorkspaceHealthFormat.JsonSummary => HealthSummaryJson(facts),
            WorkspaceHealthFormat.Json => HealthJson(facts),
            WorkspaceHealthFormat.Markdown => HealthMarkdown(facts),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
        };

    private static string HealthCompact(WorkspaceHealthFacts facts)
    {
        WorkspaceFacts status = facts.StatusFacts;
        var sb = new StringBuilder();
        sb.Append("# workspace health  ").Append(WorkspaceHealthFacts.StateName(facts.State)).Append('\n');
        sb.Append("workspace: ").Append(HealthCompactValue(DisplayId(status.Root, status.WorkspaceId, status.DisplayId)))
          .Append("  ").Append(HealthCompactValue(status.Root)).Append('\n');
        sb.Append("index: ").Append(FreshLabel(status))
          .Append(" rev ").Append(status.BuiltRevision.ToString(CultureInfo.InvariantCulture))
          .Append("  symbols ").Append(status.DocumentCount.ToString(CultureInfo.InvariantCulture))
          .Append("  ext ").Append(status.KnownExtensionsCount.ToString(CultureInfo.InvariantCulture))
          .Append("  queue ").Append(status.QueueEmpty ? "empty" : "pending")
          .Append('\n');
        if (facts.Leader is { } leader)
            sb.Append("leader: ").Append(HealthCompactValue(LeaderLabel(status, leader))).Append('\n');
        if (status.SearchSidecar is { } sidecar)
            sb.Append("search_db: ").Append(HealthCompactValue(SearchSidecarLabel(sidecar))).Append('\n');
        if (status.ContentCorpus is { } corpus)
            sb.Append("content_db: ")
              .Append(HealthCompactValue(ContentCorpusLabel(corpus, status.BuiltRevision))).Append('\n');
        if (VectorsLabel(status.Vectors) is { } vectorsLabel)
            sb.Append("vectors: ").Append(HealthCompactValue(vectorsLabel)).Append('\n');
        if (status.SemanticBroker is { } broker)
            sb.Append("semantic_broker: ").Append(HealthCompactValue(SemanticBrokerLabel(broker))).Append('\n');
        if (ScanGovernorLabel(status.ScanGovernor) is { } governorLabel)
            sb.Append("scan_governor: ").Append(HealthCompactValue(governorLabel)).Append('\n');
        if (ScanFailureLabel(status.ScanFailure) is { } scanFailureLabel)
            sb.Append("scan_failure: ").Append(HealthCompactValue(scanFailureLabel)).Append('\n');
        if (RebindProvenanceLabel(status.RebindProvenance) is { } rebindLabel)
            sb.Append("rebound_from: ").Append(HealthCompactValue(rebindLabel)).Append('\n');
        if (status.Store is { } store)
            sb.Append("store: ").Append(HealthCompactValue(StoreProvenanceLabel(store))).Append('\n');
        if (facts.History is { } history)
            sb.Append("history_db: ").Append(HealthCompactValue(HistorySidecarLabel(history))).Append('\n');
        sb.Append("quality: ")
          .Append(ParseDiagnosticCount(facts.Extraction).ToString(CultureInfo.InvariantCulture))
          .Append(" parse diagnostics").Append(AvailabilitySuffix(facts.Extraction.ParseDiagnostics))
          .Append("  ")
          .Append(OpenCapabilityGapCount(facts.Extraction).ToString(CultureInfo.InvariantCulture))
          .Append(" open capability gaps").Append(AvailabilitySuffix(facts.Extraction.CapabilityGaps))
          .Append("  ")
          .Append(StructuralFactCount(facts.Extraction).ToString(CultureInfo.InvariantCulture))
          .Append(" structural facts").Append(AvailabilitySuffix(facts.Extraction.StructuralFacts))
          .Append("  ")
          .Append(ComplexityMetricCount(facts.Extraction).ToString(CultureInfo.InvariantCulture))
          .Append(" complexity metrics").Append(AvailabilitySuffix(facts.Extraction.ComplexityMetrics))
          .Append('\n');
        sb.Append("telemetry: ")
          .Append(facts.TelemetryHealth.TotalCalls.ToString(CultureInfo.InvariantCulture))
          .Append(" calls  errors=")
          .Append(facts.TelemetryHealth.ErrorCount.ToString(CultureInfo.InvariantCulture))
          .Append("  empty=")
          .Append(facts.TelemetryHealth.EmptyCount.ToString(CultureInfo.InvariantCulture))
          .Append('\n');
        if (facts.Warnings.Count > 0)
            sb.Append("warning: ").Append(HealthCompactValue(facts.Warnings[0].Message)).Append('\n');
        if (facts.RecommendedActions.Count > 0)
            sb.Append("recommended: ").Append(HealthCompactValue(facts.RecommendedActions[0])).Append('\n');
        int omittedRows =
            facts.Extraction.ParseDiagnostics.Rows.Count +
            facts.Extraction.CapabilityGaps.Rows.Count +
            facts.Extraction.LanguageCapabilities.Rows.Count +
            facts.Extraction.StructuralFacts.Rows.Count +
            facts.Extraction.ComplexityMetrics.Rows.Count +
            facts.Extraction.Files.Rows.Count;
        int unavailableGroups = HealthSections(facts.Extraction).Count(static available => !available);
        sb.Append("omitted: groups=").Append(6 - unavailableGroups)
          .Append(" unavailable=").Append(unavailableGroups)
          .Append(" rows=").Append(omittedRows.ToString(CultureInfo.InvariantCulture))
          .Append(" warnings=").Append(Math.Max(0, facts.Warnings.Count - 1).ToString(CultureInfo.InvariantCulture))
          .Append(" actions=").Append(Math.Max(0, facts.RecommendedActions.Count - 1).ToString(CultureInfo.InvariantCulture))
          .Append('\n');
        return sb.ToString().TrimEnd('\n');
    }

    private static string AvailabilitySuffix<TRow>(HealthFactSection<TRow> section) =>
        section.Available ? string.Empty : " (unavailable)";

    private static IReadOnlyList<bool> HealthSections(WorkspaceExtractionHealthFacts facts) =>
    [
        facts.ParseDiagnostics.Available,
        facts.CapabilityGaps.Available,
        facts.LanguageCapabilities.Available,
        facts.StructuralFacts.Available,
        facts.ComplexityMetrics.Available,
        facts.Files.Available,
    ];

    private const int HealthCompactValueMaxChars = 240;

    private static string HealthCompactValue(string value)
    {
        string normalized = value.Replace('\r', ' ').Replace('\n', ' ');
        return normalized.Length <= HealthCompactValueMaxChars
            ? normalized
            : normalized[..(HealthCompactValueMaxChars - 3)] + "...";
    }

    private static string HealthMarkdown(WorkspaceHealthFacts facts) =>
        $"{HealthCompact(facts)}\n\n```json\n{HealthJson(facts)}\n```";

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

            if (status.SemanticBroker is { } broker)
            {
                w.WritePropertyName("semantic_broker");
                WriteSemanticBrokerJson(w, broker, includeOwnerPid: true);
            }

            if (status.ScanGovernor is { } scanGovernor)
            {
                w.WritePropertyName("scan_governor");
                WriteScanGovernorJson(w, scanGovernor);
            }

            if (status.ScanFailure is { } scanFailure)
            {
                w.WritePropertyName("scan_failure");
                WriteScanFailureJson(w, scanFailure);
            }

            if (status.IndexLevel is { } healthIndexLevel)
            {
                w.WritePropertyName("index_level");
                WriteIndexLevelJson(w, healthIndexLevel);
            }

            if (status.RebindProvenance is { } healthRebindProvenance)
            {
                w.WritePropertyName("rebound_from");
                WriteRebindProvenanceJson(w, healthRebindProvenance);
            }

            if (status.Store is { } store)
            {
                w.WritePropertyName("store");
                WriteStoreProvenanceJson(w, store);
            }

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

    private static string HealthSummaryJson(WorkspaceHealthFacts facts)
    {
        WorkspaceFacts status = facts.StatusFacts;
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = NewWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", 1);
            writer.WriteString("detail", "summary");

            writer.WriteStartObject("verdict");
            writer.WriteString("state", WorkspaceHealthFacts.StateName(facts.State));
            writer.WriteString("summary", HealthCompactValue(facts.Summary));
            writer.WriteEndObject();

            writer.WriteStartObject("workspace");
            writer.WriteString("root", status.Root);
            if (status.WorkspaceId is null) writer.WriteNull("workspace_id");
            else writer.WriteString("workspace_id", status.WorkspaceId);
            if (status.DisplayId is null) writer.WriteNull("display_id");
            else writer.WriteString("display_id", status.DisplayId);
            writer.WriteBoolean("leader", status.IsLeader);
            if (status.ServerVersion is null) writer.WriteNull("server_version");
            else writer.WriteString("server_version", status.ServerVersion);
            writer.WriteEndObject();

            if (status.SemanticBroker is { } broker)
            {
                writer.WritePropertyName("semantic_broker");
                WriteSemanticBrokerJson(writer, broker, includeOwnerPid: false);
            }

            writer.WriteStartObject("index");
            writer.WriteNumber("document_count", status.DocumentCount);
            writer.WriteNumber("known_extensions", status.KnownExtensionsCount);
            writer.WriteNumber("built_revision", status.BuiltRevision);
            writer.WriteNumber("latest_revision", status.LatestObservedRevision);
            if (status.IndexFresh is { } fresh) writer.WriteBoolean("index_fresh", fresh);
            else writer.WriteNull("index_fresh");
            if (status.FreshnessStatus is null) writer.WriteNull("freshness_status");
            else writer.WriteString("freshness_status", status.FreshnessStatus);
            writer.WriteBoolean("queue_empty", status.QueueEmpty);
            writer.WritePropertyName("search_sidecar");
            WriteSearchSidecarJson(writer, status.SearchSidecar);
            writer.WritePropertyName("content_corpus");
            WriteContentCorpusJson(writer, status.ContentCorpus);
            WriteVectorsJson(writer, status.Vectors);
            writer.WritePropertyName("history_db");
            WriteHistorySidecarJson(writer, facts.History);
            writer.WriteEndObject();

            WorkspaceExtractionHealthFacts extraction = facts.Extraction;
            writer.WriteStartObject("extraction_quality");
            writer.WriteNumber("parse_diagnostic_count", ParseDiagnosticCount(extraction));
            writer.WriteNumber("open_capability_gap_count", OpenCapabilityGapCount(extraction));
            writer.WriteNumber("structural_fact_count", StructuralFactCount(extraction));
            writer.WriteNumber("complexity_metric_count", ComplexityMetricCount(extraction));
            writer.WriteNumber(
                "detailed_row_count",
                extraction.ParseDiagnostics.Rows.Count +
                extraction.CapabilityGaps.Rows.Count +
                extraction.LanguageCapabilities.Rows.Count +
                extraction.StructuralFacts.Rows.Count +
                extraction.ComplexityMetrics.Rows.Count +
                extraction.Files.Rows.Count);
            writer.WriteStartObject("availability");
            WriteHealthSectionAvailability(writer, "parse_diagnostics", extraction.ParseDiagnostics);
            WriteHealthSectionAvailability(writer, "capability_gaps", extraction.CapabilityGaps);
            WriteHealthSectionAvailability(writer, "language_capabilities", extraction.LanguageCapabilities);
            WriteHealthSectionAvailability(writer, "structural_facts", extraction.StructuralFacts);
            WriteHealthSectionAvailability(writer, "complexity_metrics", extraction.ComplexityMetrics);
            WriteHealthSectionAvailability(writer, "files", extraction.Files);
            writer.WriteEndObject();
            writer.WriteEndObject();

            writer.WriteStartObject("telemetry");
            writer.WriteNumber("ok_count", facts.TelemetryHealth.OkCount);
            writer.WriteNumber("empty_count", facts.TelemetryHealth.EmptyCount);
            writer.WriteNumber("error_count", facts.TelemetryHealth.ErrorCount);
            writer.WriteNumber("total_calls", facts.TelemetryHealth.TotalCalls);
            writer.WriteEndObject();

            writer.WriteNumber("warnings_total_count", facts.Warnings.Count);
            writer.WriteNumber(
                "warnings_omitted_count",
                Math.Max(0, facts.Warnings.Count - 3));
            writer.WriteStartArray("warnings");
            foreach (HealthWarning warning in facts.Warnings.Take(3))
            {
                writer.WriteStartObject();
                writer.WriteString("code", warning.Code);
                writer.WriteString("severity", warning.Severity);
                writer.WriteString("message", HealthCompactValue(warning.Message));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteNumber("recommended_actions_total_count", facts.RecommendedActions.Count);
            writer.WriteNumber(
                "recommended_actions_omitted_count",
                Math.Max(0, facts.RecommendedActions.Count - 3));
            writer.WriteStartArray("recommended_actions");
            foreach (string action in facts.RecommendedActions.Take(3))
                writer.WriteStringValue(HealthCompactValue(action));
            writer.WriteEndArray();
            writer.WriteString("next_action", "Run `miller workspace health --json` for exhaustive detail.");
            writer.WriteEndObject();
        }

        string output = Utf8(buffer);
        if (Encoding.UTF8.GetByteCount(output) > ToolOutputBudget.WorkspaceHealthMcpMaxBytes)
            throw new InvalidOperationException("Workspace health summary exceeded its MCP output budget.");
        return output;
    }

    private static void WriteHealthSectionAvailability<TRow>(
        Utf8JsonWriter writer,
        string name,
        HealthFactSection<TRow> section)
    {
        writer.WriteStartObject(name);
        writer.WriteBoolean("available", section.Available);
        if (section.Error is null) writer.WriteNull("error");
        else writer.WriteString("error", HealthCompactValue(section.Error));
        writer.WriteNumber("row_count", section.Rows.Count);
        writer.WriteEndObject();
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
    /// Render the legacy single-workspace list view.
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
        List(
            WorkspaceFactsAssembler.ToListFacts(entries, filter, limit ?? (json ? null : DefaultListLimit)),
            json);

    public static string List(WorkspaceListFacts facts, bool json) =>
        json ? ListJson(facts) : ListCompact(facts);

    public static BoundedPrefixRender ListWithinBudget(WorkspaceListFacts facts, bool json, int maxBytes) =>
        ToolOutputBudget.RenderPrefixWithinByteBudgetWithCount(
            facts.Entries,
            maxBytes,
            (retained, omittedByBudget) =>
                List(
                    facts with
                    {
                        Entries = retained,
                        Returned = retained.Count,
                        Omitted = facts.Omitted + omittedByBudget,
                        OmittedErrors = facts.OmittedErrors +
                            facts.Entries
                                .Skip(retained.Count)
                                .Count(static entry =>
                                    string.Equals(entry.State, "error", StringComparison.Ordinal)),
                        ReturnedMissing = retained.Count(static entry => entry.RootMissing),
                    },
                    json));

    /// <summary>The default number of compact <c>workspace list</c> entries before the omitted-count tail.</summary>
    public const int DefaultListLimit = 20;

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

    private static string ListCompact(WorkspaceListFacts facts)
    {
        var sb = new StringBuilder();
        if (facts.Filter is not null && facts.Matched == 0)
            sb.Append("# workspaces (0 shown)\n");
        else if (facts.Filter is not null)
            sb.Append("# workspaces (").Append(facts.Returned).Append(" of ").Append(facts.Matched)
              .Append(" matched, ").Append(facts.Registered).Append(" registered; filter=\"")
              .Append(facts.Filter).Append("\")\n");
        else if (facts.Returned < facts.Registered)
            sb.Append("# workspaces (").Append(facts.Returned).Append(" of ").Append(facts.Registered).Append(")\n");
        else
            sb.Append("# workspaces (").Append(facts.Registered).Append(")\n");
        sb.Append("totals: registered=").Append(facts.Registered)
          .Append(" matched=").Append(facts.Matched)
          .Append(" returned=").Append(facts.Returned)
          .Append(" omitted=").Append(facts.Omitted)
          .Append(" registered_missing=").Append(facts.RegisteredMissing)
          .Append(" matched_missing=").Append(facts.MatchedMissing)
          .Append('\n');
        sb.Append("selection: filter=");
        if (facts.Filter is null)
            sb.Append("(none)");
        else
            sb.Append('"').Append(facts.Filter).Append('"');
        sb.Append(" limit=").Append(facts.Limit?.ToString(CultureInfo.InvariantCulture) ?? "(none)")
          .Append('\n');

        if (facts.Filter is not null && facts.Matched == 0)
        {
            sb.Append("no workspace matches filter \"").Append(facts.Filter)
              .Append("\" — ").Append(facts.Registered)
              .Append(" registered; adjust the substring or omit filter");
            return sb.ToString();
        }

        foreach (WorkspaceListEntry entry in facts.Entries)
        {
            sb.Append("* ").Append(entry.DisplayId).Append("  ").Append(entry.Root);
            if (entry.Current)
                sb.Append("  [current]");
            sb.Append("  state: ").Append(entry.State)
              .Append(entry.RootMissing ? " (root missing)" : string.Empty)
              .Append("  rev: ").Append(entry.LastRevision?.ToString() ?? "(unknown)");
            if (!string.IsNullOrEmpty(entry.LastError))
                sb.Append('\n').Append("  error: ").Append(entry.LastError);
            sb.Append('\n');
        }

        if (facts.Omitted > 0)
        {
            sb.Append("… ").Append(facts.Omitted).Append(" more — raise limit or pass filter=<substring>\n");
            if (facts.OmittedErrors > 0)
                sb.Append("errors: ").Append(facts.OmittedErrors)
                  .Append(" workspace(s) in error state — filter or raise limit to see them\n");
        }
        if (facts.MatchedMissing > 0)
        {
            sb.Append("missing roots: ").Append(facts.MatchedMissing)
              .Append(" — preview registry cleanup with a prune dry run\n");
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

    private static string ListJson(WorkspaceListFacts facts)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = NewWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("registered", facts.Registered);
            w.WriteNumber("matched", facts.Matched);
            w.WriteNumber("returned", facts.Returned);
            w.WriteNumber("omitted", facts.Omitted);
            w.WriteNumber("omitted_errors", facts.OmittedErrors);
            w.WriteNumber("registered_missing", facts.RegisteredMissing);
            w.WriteNumber("matched_missing", facts.MatchedMissing);
            w.WriteNumber("returned_missing", facts.ReturnedMissing);
            if (facts.Filter is null) w.WriteNull("filter");
            else w.WriteString("filter", facts.Filter);
            if (facts.Limit is { } limit) w.WriteNumber("limit", limit);
            else w.WriteNull("limit");
            w.WritePropertyName("workspaces");
            w.WriteStartArray();
            foreach (WorkspaceListEntry entry in facts.Entries)
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
                w.WriteBoolean("root_missing", entry.RootMissing);
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

    public static string Onboarding(
        WorkspaceOnboardingFacts facts,
        bool json,
        int? rowLimit = null)
    {
        int effectiveRowLimit = rowLimit is > 0 ? rowLimit.Value : int.MaxValue;
        return json
            ? OnboardingJson(facts, effectiveRowLimit)
            : OnboardingCompact(facts, effectiveRowLimit);
    }

    private static string OnboardingCompact(WorkspaceOnboardingFacts facts, int rowLimit)
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

        IReadOnlyList<string> startHere = facts.StartHere.Take(rowLimit).ToArray();
        AppendLines(sb, "start here", startHere);

        if (facts.Telemetry.ToolMix.Count > 0)
        {
            sb.Append("tool mix:\n");
            foreach (TelemetryToolMix row in facts.Telemetry.ToolMix.Take(rowLimit))
                sb.Append("- ").Append(OnboardingLabel(row.Tool, row.Op)).Append("  calls ")
                    .Append(row.Calls.ToString(CultureInfo.InvariantCulture))
                    .Append("  empty ").Append(row.EmptyCount.ToString(CultureInfo.InvariantCulture))
                    .Append("  errors ").Append(row.ErrorCount.ToString(CultureInfo.InvariantCulture))
                    .Append("  p95 ").Append(row.P95Ms.ToString(CultureInfo.InvariantCulture)).Append("ms\n");
        }

        if (facts.Telemetry.SuccessfulFlows.Count > 0)
        {
            sb.Append("successful flows:\n");
            foreach (TelemetryFlow flow in facts.Telemetry.SuccessfulFlows.Take(rowLimit))
                sb.Append("- ").Append(flow.From).Append(" -> ").Append(flow.To)
                    .Append(" (").Append(flow.Calls.ToString(CultureInfo.InvariantCulture)).Append(")\n");
        }

        if (facts.HotTargets.Count > 0)
        {
            sb.Append("hot targets:\n");
            RecoveredTargetHash[] returnedTargets = facts.HotTargets.Take(rowLimit).ToArray();
            List<RecoveredTargetHash> resolved =
                returnedTargets.Where(static target => !IsUnresolvedTarget(target)).ToList();
            List<RecoveredTargetHash> unresolved =
                returnedTargets.Where(static target => IsUnresolvedTarget(target)).ToList();

            foreach (RecoveredTargetHash target in resolved)
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
            foreach (TelemetryMiss miss in facts.Telemetry.CommonMisses.Take(rowLimit))
                sb.Append("- ").Append(OnboardingLabel(miss.Tool, miss.Op)).Append("  ")
                    .Append(miss.Reason).Append(" (")
                    .Append(miss.Calls.ToString(CultureInfo.InvariantCulture)).Append(")\n");
        }

        if (facts.Telemetry.Friction.Count > 0)
        {
            sb.Append("friction:\n");
            foreach (TelemetryFriction row in facts.Telemetry.Friction.Take(rowLimit))
                sb.Append("- ").Append(OnboardingLabel(row.Tool, row.Op))
                    .Append("  errors ").Append(row.ErrorCount.ToString(CultureInfo.InvariantCulture))
                    .Append("  empty ").Append(row.EmptyCount.ToString(CultureInfo.InvariantCulture))
                    .Append("  p95 ").Append(row.P95Ms.ToString(CultureInfo.InvariantCulture)).Append("ms")
                    .Append("  bytes ").Append(row.BytesReturned.ToString(CultureInfo.InvariantCulture))
                    .Append('\n');
        }

        IReadOnlyList<string> instructionNotes = facts.InstructionNotes.Take(rowLimit).ToArray();
        AppendLines(sb, "instruction notes", instructionNotes);
        AppendLines(sb, "privacy", facts.PrivacyNotes);

        var omissions = new List<string>();
        AddOmission(omissions, "start here", facts.StartHere.Count, startHere.Count);
        AddOmission(
            omissions,
            "tool mix",
            ExactTotal(facts.Telemetry.ToolMixTotal, facts.Telemetry.ToolMix.Count),
            Math.Min(facts.Telemetry.ToolMix.Count, rowLimit));
        AddOmission(
            omissions,
            "successful flows",
            ExactTotal(facts.Telemetry.SuccessfulFlowsTotal, facts.Telemetry.SuccessfulFlows.Count),
            Math.Min(facts.Telemetry.SuccessfulFlows.Count, rowLimit));
        AddOmission(
            omissions,
            "hot targets",
            ExactTotal(facts.Telemetry.TargetHashesTotal, facts.HotTargets.Count),
            Math.Min(facts.HotTargets.Count, rowLimit));
        AddOmission(
            omissions,
            "common misses",
            ExactTotal(facts.Telemetry.CommonMissesTotal, facts.Telemetry.CommonMisses.Count),
            Math.Min(facts.Telemetry.CommonMisses.Count, rowLimit));
        AddOmission(
            omissions,
            "friction",
            ExactTotal(facts.Telemetry.FrictionTotal, facts.Telemetry.Friction.Count),
            Math.Min(facts.Telemetry.Friction.Count, rowLimit));
        AddOmission(omissions, "instruction notes", facts.InstructionNotes.Count, instructionNotes.Count);
        if (omissions.Count > 0)
            sb.Append("omitted: ").AppendJoin("; ", omissions).Append('\n');

        return sb.ToString().TrimEnd('\n');
    }

    private static string OnboardingJson(WorkspaceOnboardingFacts facts, int rowLimit)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = NewWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("operation", "onboarding");
            if (rowLimit != int.MaxValue)
                w.WriteNumber("agent_row_limit", rowLimit);
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

            string[] startHere = facts.StartHere.Take(rowLimit).ToArray();
            TelemetryToolMix[] toolMix = facts.Telemetry.ToolMix.Take(rowLimit).ToArray();
            TelemetryFlow[] successfulFlows = facts.Telemetry.SuccessfulFlows.Take(rowLimit).ToArray();
            RecoveredTargetHash[] hotTargets = facts.HotTargets.Take(rowLimit).ToArray();
            TelemetryMiss[] commonMisses = facts.Telemetry.CommonMisses.Take(rowLimit).ToArray();
            TelemetryFriction[] friction = facts.Telemetry.Friction.Take(rowLimit).ToArray();
            string[] instructionNotes = facts.InstructionNotes.Take(rowLimit).ToArray();

            WriteStringArray(w, "start_here", startHere);
            WriteCountMetadata(w, "start_here", facts.StartHere.Count, startHere.Length);
            WriteToolMixJson(w, toolMix);
            WriteCountMetadata(
                w,
                "tool_mix",
                ExactTotal(facts.Telemetry.ToolMixTotal, facts.Telemetry.ToolMix.Count),
                toolMix.Length);
            WriteFlowsJson(w, successfulFlows);
            WriteCountMetadata(
                w,
                "successful_flows",
                ExactTotal(facts.Telemetry.SuccessfulFlowsTotal, facts.Telemetry.SuccessfulFlows.Count),
                successfulFlows.Length);
            WriteHotTargetsJson(w, hotTargets);
            WriteCountMetadata(
                w,
                "hot_targets",
                ExactTotal(facts.Telemetry.TargetHashesTotal, facts.HotTargets.Count),
                hotTargets.Length);
            WriteMissesJson(w, commonMisses);
            WriteCountMetadata(
                w,
                "common_misses",
                ExactTotal(facts.Telemetry.CommonMissesTotal, facts.Telemetry.CommonMisses.Count),
                commonMisses.Length);
            WriteFrictionJson(w, friction);
            WriteCountMetadata(
                w,
                "friction",
                ExactTotal(facts.Telemetry.FrictionTotal, facts.Telemetry.Friction.Count),
                friction.Length);
            WriteStringArray(
                w,
                "instruction_notes",
                instructionNotes);
            WriteCountMetadata(
                w,
                "instruction_notes",
                facts.InstructionNotes.Count,
                instructionNotes.Length);

            w.WritePropertyName("privacy");
            w.WriteStartObject();
            w.WriteBoolean("raw_queries_stored", false);
            w.WriteBoolean("raw_targets_stored", false);
            WriteStringArray(w, "notes", facts.PrivacyNotes);
            w.WriteNumber("notes_total_count", facts.PrivacyNotes.Count);
            w.WriteNumber("notes_omitted_count", 0);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Utf8(buffer);
    }

    private static int ExactTotal(int reportedTotal, int returnedCount) =>
        Math.Max(reportedTotal, returnedCount);

    private static void AddOmission(List<string> omissions, string label, int total, int returned)
    {
        int omitted = Math.Max(0, total - returned);
        if (omitted > 0)
            omissions.Add($"{label} {omitted.ToString(CultureInfo.InvariantCulture)}");
    }

    private static void WriteCountMetadata(Utf8JsonWriter w, string name, int total, int returned)
    {
        w.WriteNumber($"{name}_total_count", total);
        w.WriteNumber($"{name}_omitted_count", Math.Max(0, total - returned));
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

    /// <summary>Render an <c>open</c>/<c>refresh</c>/<c>full</c> action result.</summary>
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
        if (result.Downgraded)
            sb.Append("downgraded: yes\n");
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
            if (result.Downgraded)
                w.WriteBoolean("downgraded", true);
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
            $"removed workspace index: {result.MillerDir}" + SidecarReclaimSuffix(result.SidecarReclaim),
        WorkspaceRemoveResult.Outcome.Removed =>
            $"removed workspace registry entry: {result.Root ?? result.WorkspaceId ?? result.MillerDir} " +
            $"(no index dir at {result.MillerDir})" + SidecarReclaimSuffix(result.SidecarReclaim),
        WorkspaceRemoveResult.Outcome.RefusedLive =>
            $"refused: {result.MillerDir} is the workspace this Miller is serving (in use). " +
            "Stop that Miller first, or remove a different workspace.",
        WorkspaceRemoveResult.Outcome.RefusedInUse =>
            $"refused: {result.MillerDir} is in use by another Miller writer. Stop that Miller first.",
        WorkspaceRemoveResult.Outcome.RefusedSensitive =>
            "refused: the requested removal target is a sensitive or machine-global Miller directory.",
        WorkspaceRemoveResult.Outcome.RefusedInvalidRegistration =>
            "refused: the registry entry does not map to its canonical workspace index directory.",
        WorkspaceRemoveResult.Outcome.NotFound =>
            $"not found: no registered workspace matches {result.Root ?? result.MillerDir} — nothing removed.",
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
                WorkspaceRemoveResult.Outcome.RefusedSensitive => "refused_sensitive",
                WorkspaceRemoveResult.Outcome.RefusedInvalidRegistration => "refused_invalid_registration",
                WorkspaceRemoveResult.Outcome.NotFound => "not_found",
                _ => "unknown",
            });
            w.WriteString("miller_dir", result.MillerDir);
            if (result.WorkspaceId is null) w.WriteNull("workspace_id");
            else w.WriteString("workspace_id", result.WorkspaceId);
            if (result.Root is null) w.WriteNull("root");
            else w.WriteString("root", result.Root);
            w.WriteBoolean("index_dir_deleted", result.IndexDirDeleted);
            WriteSidecarReclaim(w, result.SidecarReclaim);
            w.WriteString("message", RemoveCompact(result));
            w.WriteEndObject();
        }
        return Utf8(buffer);
    }

    /// <summary>
    /// What the removal reclaimed from the family store, appended to the success line. Silent when the workspace
    /// owned no store view, so a standalone-artifact removal reads exactly as it did before.
    /// </summary>
    private static string SidecarReclaimSuffix(StoreSidecarReclaimResult reclaim) =>
        reclaim.HasReport ? $" ({SidecarReclaimText(reclaim)})" : string.Empty;

    internal static string SidecarReclaimText(StoreSidecarReclaimResult reclaim)
    {
        string reclaimed = string.Create(
            CultureInfo.InvariantCulture,
            $"reclaimed {reclaim.FilesDeleted} store sidecar files, {reclaim.BytesReclaimed} bytes");
        return reclaim.SkipReason is { } reason ? $"{reclaimed}; kept: {reason}" : reclaimed;
    }

    private static void WriteSidecarReclaim(Utf8JsonWriter w, StoreSidecarReclaimResult reclaim)
    {
        w.WriteStartObject("store_sidecar_reclaim");
        w.WriteNumber("files_deleted", reclaim.FilesDeleted);
        w.WriteNumber("bytes_reclaimed", reclaim.BytesReclaimed);
        w.WriteNumber("files_retained", reclaim.FilesRetained);
        if (reclaim.SkipReason is null) w.WriteNull("skip_reason");
        else w.WriteString("skip_reason", reclaim.SkipReason);
        w.WriteEndObject();
    }

    // ---------- levels ----------

    /// <summary>Render a <c>workspace levels</c> result: the effective index-level policy (with its source),
    /// the served artifact's recorded level, and whether a full-level upgrade is owed. CLI-only surface.</summary>
    public static string Levels(WorkspaceLevelsResult result, bool json) =>
        json ? LevelsJson(result) : LevelsCompact(result);

    private static string LevelsCompact(WorkspaceLevelsResult result)
    {
        var lines = new List<string>();
        string target = result.DisplayId ?? result.Root ?? "(unregistered workspace)";
        lines.Add(result.Root is { } root && result.DisplayId is not null
            ? $"index levels for {result.DisplayId} ({root})"
            : $"index levels for {target}");
        lines.Add($"  policy: {result.EffectivePolicy} ({result.PolicySource})");
        if (result.IndexLevel is { } level)
        {
            lines.Add(result.UpgradeOwed
                ? $"  artifact: {level} level (full-level upgrade owed; the indexer leader runs it in the background)"
                : $"  artifact: {level} level");
        }
        else
        {
            lines.Add("  artifact: none yet (the level applies on the first build)");
        }
        if (result.Changed is "set")
            lines.Add($"  registry policy set to '{result.RegistryPolicy}' for this workspace");
        else if (result.Changed is "cleared")
            lines.Add("  registry policy cleared for this workspace");
        return string.Join('\n', lines);
    }

    private static string LevelsJson(WorkspaceLevelsResult result)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = NewWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("operation", "levels");
            if (result.WorkspaceId is null) w.WriteNull("workspace_id");
            else w.WriteString("workspace_id", result.WorkspaceId);
            if (result.DisplayId is null) w.WriteNull("display_id");
            else w.WriteString("display_id", result.DisplayId);
            if (result.Root is null) w.WriteNull("root");
            else w.WriteString("root", result.Root);
            w.WriteStartObject("level_policy");
            w.WriteString("effective", result.EffectivePolicy);
            w.WriteString("source", result.PolicySource);
            if (result.RegistryPolicy is null) w.WriteNull("registry");
            else w.WriteString("registry", result.RegistryPolicy);
            w.WriteEndObject();
            if (result.IndexLevel is null) w.WriteNull("index_level");
            else w.WriteString("index_level", result.IndexLevel);
            w.WriteBoolean("level_upgrade_owed", result.UpgradeOwed);
            if (result.Changed is null) w.WriteNull("changed");
            else w.WriteString("changed", result.Changed);
            w.WriteEndObject();
        }
        return Utf8(buffer);
    }

    // ---------- prune ----------

    /// <summary>Render a <c>prune</c> result (removed / would-remove + kept count).</summary>
    public static string Prune(WorkspacePruneResult result, bool json) =>
        json ? PruneJson(result) : PruneCompact(result);

    public static string PruneWithinBudget(WorkspacePruneResult result, bool json, int maxBytes) =>
        json
            ? ToolOutputBudget.RenderPrefixWithinByteBudget(
                result.Pruned,
                maxBytes,
                (retained, omitted) => PruneJson(result, retained, omitted))
            : PruneCompact(result);

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
        if (result.SidecarReclaim.HasReport)
            lines.Add(SidecarReclaimText(result.SidecarReclaim));
        return string.Join('\n', lines);
    }

    private static string PruneJson(WorkspacePruneResult result) =>
        PruneJson(result, result.Pruned, omitted: 0);

    private static string PruneJson(
        WorkspacePruneResult result,
        IReadOnlyList<WorkspacePruneEntry> retained,
        int omitted)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = NewWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteBoolean("dry_run", result.DryRun);
            w.WriteNumber("pruned_total", result.Pruned.Count);
            w.WriteNumber("returned", retained.Count);
            w.WriteNumber("omitted", omitted);
            w.WriteStartArray("pruned");
            foreach (WorkspacePruneEntry entry in retained)
            {
                w.WriteStartObject();
                w.WriteString("workspace_id", entry.WorkspaceId);
                w.WriteString("display_id", entry.DisplayId);
                w.WriteString("root", entry.Root);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteNumber("kept", result.Kept);
            WriteSidecarReclaim(w, result.SidecarReclaim);
            w.WriteEndObject();
        }
        return Utf8(buffer);
    }

    // ---------- shared helpers ----------

    private static Utf8JsonWriter NewWriter(ArrayBufferWriter<byte> buffer) =>
        new(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private static string Utf8(ArrayBufferWriter<byte> buffer) => Encoding.UTF8.GetString(buffer.WrittenSpan);
}

/// <summary>The facts a <c>workspace levels</c> invocation reports (CLI-only; MCP surface unchanged).
/// <paramref name="Changed"/> is null for a read, <c>"set"</c>/<c>"cleared"</c> after a mutation.</summary>
public sealed record WorkspaceLevelsResult(
    string? WorkspaceId,
    string? DisplayId,
    string? Root,
    string EffectivePolicy,
    string PolicySource,
    string? RegistryPolicy,
    string? IndexLevel,
    bool UpgradeOwed,
    string? Changed);
