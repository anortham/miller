using System.Globalization;
using Miller.Indexing;
using Miller.Indexing.Store;
using Miller.Server.Hosting;
using Miller.Server.Telemetry;

namespace Miller.Server.Tools;

public enum HealthState
{
    Ready,
    UsableWithWarnings,
    Degraded,
    Unavailable,
}

public sealed record HealthWarning(string Code, string Severity, string Message);

/// <summary>
/// The indexer-leader view for health: who recorded itself as leader (<see cref="Identity"/>, null when no
/// identity file exists — e.g. an older build leads, or no leader runs) and whether that pid is alive right now
/// (<see cref="Alive"/>, null when there is no identity to probe). Whether THIS process leads, and its version,
/// already travel on <see cref="WorkspaceFacts"/>. The additive version-aware-leadership fields (D6) are null
/// when the caller could not gather them (older paths, one-shot CLI without a probe):
/// <see cref="OwnExtractorVersion"/> is THIS process's bundled <c>julie-extract</c> version,
/// <see cref="ArtifactExtractorVersion"/> is the artifact's recorded <c>binary_version</c>, and
/// <see cref="OwnVerdict"/> is this process's <see cref="LeadershipVerdict"/> (the leader's extractor version
/// travels on <see cref="Identity"/>).
/// </summary>
public sealed record LeaderHealthFacts(
    LeaderIdentity? Identity,
    bool? Alive,
    string? OwnExtractorVersion = null,
    string? ArtifactExtractorVersion = null,
    LeadershipVerdict? OwnVerdict = null)
{
    /// <summary>
    /// True iff both versions carry a parseable <c>X.Y.Z</c> token and <paramref name="version"/> is strictly
    /// older than <paramref name="other"/> (shared comparison: <see cref="LeadershipEligibility.CompareVersions"/>).
    /// An unparseable version can never prove a downgrade, so it reads as "not older".
    /// </summary>
    internal static bool IsExtractorOlder(string version, string other)
    {
        try
        {
            return LeadershipEligibility.CompareVersions(version, other) < 0;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Read the recorded identity under <paramref name="millerDir"/> and probe its pid's liveness.</summary>
    public static LeaderHealthFacts Read(string millerDir) => Read(millerDir, probe: null);

    /// <summary>Test seam: <paramref name="probe"/> replaces the real process probe (null = real).</summary>
    internal static LeaderHealthFacts Read(string millerDir, Func<int, LeaderProcessProbe>? probe)
    {
        LeaderIdentity? identity = LeaderIdentityFile.TryRead(millerDir);
        return new LeaderHealthFacts(identity, identity is null ? null : LeaderIdentityFile.IsProcessAlive(identity, probe));
    }
}

public sealed record WorkspaceHealthFacts(
    WorkspaceFacts StatusFacts,
    TelemetrySummary Telemetry,
    TelemetryHealthFacts TelemetryHealth,
    WorkspaceExtractionHealthFacts Extraction,
    IReadOnlyList<HealthWarning> Warnings,
    IReadOnlyList<string> RecommendedActions,
    HealthState State,
    string Summary,
    LeaderHealthFacts? Leader = null,
    MetricHistoryStatus? History = null)
{
    public static WorkspaceHealthFacts Create(
        WorkspaceFacts statusFacts,
        TelemetrySummary telemetry,
        TelemetryHealthFacts telemetryHealth,
        WorkspaceExtractionHealthFacts extraction,
        LeaderHealthFacts? leader = null,
        MetricHistoryStatus? history = null)
    {
        var warnings = new List<HealthWarning>();
        var recommended = new List<string>();

        if (statusFacts.Store?.Wal is { NeedsWarning: true } wal)
        {
            string storeBytes = wal.StoreBytes?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
            string coordBytes = wal.CoordinatorBytes?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
            string age = wal.DebtAgeSeconds?.ToString("F0", CultureInfo.InvariantCulture) ?? "unknown";
            warnings.Add(new HealthWarning("store_wal_checkpoint_owed", "usable_with_warnings",
                $"family WAL cleanup needs attention: store_bytes={storeBytes} coordinator_bytes={coordBytes} debt_age_seconds={age}"));
            recommended.Add("run workspace refresh to retry WAL cleanup; if debt persists, inspect checkpoint logs and long-lived readers; never delete a live WAL");
        }

        long openCapabilityGaps = extraction.CapabilityGaps.Rows
                .Where(static row => string.Equals(row.Status, "open", StringComparison.Ordinal))
            .Sum(static row => row.Count);
        if (openCapabilityGaps > 0)
        {
            warnings.Add(new HealthWarning(
                "capability_gaps",
                "usable_with_warnings",
                $"{openCapabilityGaps} open capability gaps reported"));
            recommended.Add("inspect language capability gaps before relying on unsupported language facts");
        }

        long parseDiagnostics = extraction.ParseDiagnostics.Rows.Sum(static row => row.Count);
        if (parseDiagnostics > 0)
        {
            warnings.Add(new HealthWarning(
                "parse_diagnostics",
                "usable_with_warnings",
                $"{parseDiagnostics} parse diagnostics reported"));
            recommended.Add("inspect parse diagnostics before relying on unsupported language facts");
        }

        AddUnavailableSectionWarnings(warnings, extraction.ParseDiagnostics.Available, extraction.ParseDiagnostics.Error,
            "parse_diagnostics_unavailable");
        AddUnavailableSectionWarnings(warnings, extraction.CapabilityGaps.Available, extraction.CapabilityGaps.Error,
            "capability_gaps_unavailable");
        AddUnavailableSectionWarnings(warnings, extraction.LanguageCapabilities.Available, extraction.LanguageCapabilities.Error,
            "language_capabilities_unavailable");
        AddUnavailableSectionWarnings(warnings, extraction.StructuralFacts.Available, extraction.StructuralFacts.Error,
            "structural_facts_unavailable");
        AddUnavailableSectionWarnings(warnings, extraction.ComplexityMetrics.Available, extraction.ComplexityMetrics.Error,
            "complexity_metrics_unavailable");
        AddUnavailableSectionWarnings(warnings, extraction.Files.Available, extraction.Files.Error, "files_unavailable");

        if (statusFacts.IndexFresh == false)
            warnings.Add(new HealthWarning("index_stale", "degraded", "workspace index is stale"));
        if (!string.IsNullOrWhiteSpace(statusFacts.WarningText))
            warnings.Add(new HealthWarning("index_warning", "degraded", statusFacts.WarningText));
        AddVectorWarnings(warnings, recommended, statusFacts.Vectors, statusFacts.IsLeader);
        AddSidecarWarning(
            warnings,
            recommended,
            "search_sidecar",
            statusFacts.SearchSidecar?.State,
            statusFacts.SearchSidecar?.Error);
        AddSidecarWarning(
            warnings,
            recommended,
            "content_corpus",
            statusFacts.ContentCorpus?.State,
            statusFacts.ContentCorpus?.Error);
        AddScanGovernorWarning(warnings, recommended, statusFacts.ScanGovernor);

        AddLeaderWarnings(warnings, recommended, statusFacts, leader);

        if (telemetryHealth.ErrorCount > 0)
        {
            warnings.Add(new HealthWarning(
                "telemetry_errors",
                "usable_with_warnings",
                $"{telemetryHealth.ErrorCount} recent tool errors reported"));
            recommended.Add("review recent telemetry errors if the current workflow depends on failing tools");
        }

        HealthState state = StateFrom(statusFacts, warnings);
        string summary = state switch
        {
            HealthState.Ready => "index and sidecars are ready",
            HealthState.UsableWithWarnings => "index readable with warnings",
            HealthState.Degraded => "workspace readable but degraded",
            HealthState.Unavailable => "workspace index is unavailable",
            _ => "workspace health unknown",
        };

        return new WorkspaceHealthFacts(
            statusFacts,
            telemetry,
            telemetryHealth,
            extraction,
            warnings,
            recommended.Distinct(StringComparer.Ordinal).ToArray(),
            state,
            summary,
            leader,
            history);
    }

    public static string StateName(HealthState state) => state switch
    {
        HealthState.Ready => "ready",
        HealthState.UsableWithWarnings => "usable_with_warnings",
        HealthState.Degraded => "degraded",
        HealthState.Unavailable => "unavailable",
        _ => state.ToString().ToLowerInvariant(),
    };

    // The indexer-leader diagnosis (the multi-process pile-up surface): this process's freshness depends on
    // whichever process leads. When WE lead there is nothing to diagnose. Otherwise: no identity recorded ⇒ an
    // older build may lead (it predates leader.json) or nothing leads; a dead recorded pid ⇒ convergence is
    // stalled until a live leader takes over (degraded); a live leader on a different version ⇒ convergence is
    // owned by another build (the stale-binary trap) — surface it.
    private static void AddLeaderWarnings(
        List<HealthWarning> warnings,
        List<string> recommended,
        WorkspaceFacts statusFacts,
        LeaderHealthFacts? leader)
    {
        if (leader is null || statusFacts.IsLeader)
            return;

        // D6 frozen-index diagnosis: THIS process may never index (ineligible verdict) AND nobody else does
        // either (no recorded leader, or a dead one) — the index is stale-but-correct until someone eligible
        // leads. Surfaced FIRST because it carries the remedy.
        if (leader.OwnVerdict is { Eligible: false } ownVerdict &&
            (leader.Identity is null || leader.Alive == false))
        {
            warnings.Add(new HealthWarning(
                "index_frozen_extractor_outdated",
                "degraded",
                $"no eligible indexer can lead this workspace ({ownVerdict.Reason}) — the index is frozen until " +
                "a current extractor leads"));
            recommended.Add(
                "upgrade miller or restore the pinned extractor (scripts/restore-julie-extract); " +
                "MILLER_ALLOW_EXTRACTOR_DOWNGRADE=1 only for intentional downgrades");
        }

        if (leader.Identity is null)
        {
            warnings.Add(new HealthWarning(
                "indexer_leader_unknown",
                "usable_with_warnings",
                "no indexer leader identity recorded — an older Miller build may be leading, or no leader is running"));
            recommended.Add("restart Miller servers for this workspace so a current build records leadership");
            return;
        }

        if (leader.Alive == false)
        {
            warnings.Add(new HealthWarning(
                "indexer_leader_dead",
                "degraded",
                $"indexer leader pid {leader.Identity.Pid} is not running — the index will not converge until a live leader takes over"));
            recommended.Add("restart a Miller server (or run workspace refresh) so a live leader resumes indexing");
            return;
        }

        // D6 outdated-LIVE-leader diagnosis: the leading process bundles an extractor strictly older than the
        // one that built the artifact — it holds the lock but can never rebuild without regressing the data.
        if (leader.Identity.ExtractorVersion is { } leaderExtractor &&
            leader.ArtifactExtractorVersion is { } artifactExtractor &&
            LeaderHealthFacts.IsExtractorOlder(leaderExtractor, artifactExtractor))
        {
            warnings.Add(new HealthWarning(
                "leader_extractor_older_than_artifact",
                "usable_with_warnings",
                $"indexer leader pid {leader.Identity.Pid} bundles extractor {leaderExtractor}, older than the " +
                $"index artifact's {artifactExtractor} — it cannot rebuild this index without regressing it"));
            recommended.Add("upgrade or restart the leading Miller so a current extractor owns indexing");
        }

        if (!string.IsNullOrWhiteSpace(statusFacts.ServerVersion) &&
            !string.Equals(leader.Identity.Version, statusFacts.ServerVersion, StringComparison.Ordinal))
        {
            string path = leader.Identity.ProcessPath is { } processPath ? $" ({processPath})" : string.Empty;
            warnings.Add(new HealthWarning(
                "indexer_leader_version_mismatch",
                "usable_with_warnings",
                $"indexer leader pid {leader.Identity.Pid} runs {leader.Identity.Version} but this server is " +
                $"{statusFacts.ServerVersion}{path} — index convergence is owned by the other build"));
            recommended.Add("stop stale Miller processes so the current build leads indexing");
        }
    }

    private static void AddUnavailableSectionWarnings(
        List<HealthWarning> warnings,
        bool available,
        string? error,
        string code)
    {
        if (available)
            return;

        warnings.Add(new HealthWarning(
            code,
            "usable_with_warnings",
            string.IsNullOrWhiteSpace(error) ? "health section is unavailable" : error));
    }

    private static void AddSidecarWarning(
        List<HealthWarning> warnings,
        List<string> recommended,
        string code,
        string? state,
        string? error)
    {
        if (state is null || string.Equals(state, "current", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(state, "disabled", StringComparison.OrdinalIgnoreCase))
            return;

        string message = string.IsNullOrWhiteSpace(error)
            ? $"{code} is {state}"
            : $"{code} is {state}: {error}";
        bool refreshable = string.Equals(state, "missing", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(state, "stale", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(state, "imports_only", StringComparison.OrdinalIgnoreCase);
        string severity = string.Equals(state, "missing", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(state, "imports_only", StringComparison.OrdinalIgnoreCase)
            ? "usable_with_warnings"
            : "degraded";
        warnings.Add(new HealthWarning(code, severity, message));
        if (string.Equals(state, "preservation_blocked", StringComparison.OrdinalIgnoreCase))
        {
            recommended.Add(
                "run miller content export to preserve imported chunks, keep content.db as the recovery source, " +
                "then recover or re-import sources before replacing the content_corpus");
        }
        else if (refreshable)
        {
            recommended.Add($"run workspace refresh to rebuild the {code}");
        }
        else
        {
            recommended.Add(
                $"inspect {code} diagnostics, then retry its convergence with bounded backoff");
        }
    }

    // Queueing behind another workspace's scan is the governor working as DESIGNED, so it is
    // usable_with_warnings, never degraded — a degraded severity would flip every queued fleet workspace's
    // HealthState through StateFrom.
    private static void AddScanGovernorWarning(
        List<HealthWarning> warnings,
        List<string> recommended,
        ScanGovernorSnapshot? governor)
    {
        if (governor is null || !string.Equals(governor.State, ScanGovernorStates.Waiting, StringComparison.Ordinal))
            return;

        string holder = governor.HolderPid is { } pid
            ? "recorded holder pid " + pid.ToString(CultureInfo.InvariantCulture) +
              (string.IsNullOrWhiteSpace(governor.HolderWorkspaceRoot)
                  ? string.Empty
                  : " scanning " + governor.HolderWorkspaceRoot)
            : "no live holder is recorded";
        warnings.Add(new HealthWarning(
            "scan_waiting_on_machine_governor",
            "usable_with_warnings",
            $"this workspace's scan is queued behind another scan on this machine ({holder})"));
        recommended.Add(
            "wait for the in-flight scan to finish, or set MILLER_SCAN_GOVERNOR=0 to disable machine-wide " +
            "scan admission");
    }

    private static void AddVectorWarnings(
        List<HealthWarning> warnings,
        List<string> recommended,
        VectorSidecarFacts? vectors,
        bool isLeader)
    {
        if (vectors is null || string.Equals(vectors.State, "disabled", StringComparison.OrdinalIgnoreCase))
            return;

        var vectorActions = new List<string>();

        if (string.Equals(vectors.State, "ready", StringComparison.OrdinalIgnoreCase))
        {
            string? failure = new[] { vectors.SymbolCursor?.LastError, vectors.ChunkCursor?.LastError }
                .FirstOrDefault(static error => !string.IsNullOrWhiteSpace(error));
            if (failure is not null)
            {
                warnings.Add(new HealthWarning(
                    "vectors_failed",
                    "usable_with_warnings",
                    $"vector convergence reported a failure: {failure}"));
                vectorActions.Add(isLeader
                    ? "retry vector convergence with bounded backoff and inspect vector convergence diagnostics"
                    : "open or keep a resident Miller leader running, then inspect vector convergence diagnostics");
            }

            VectorCursorFacts? lagging = new[] { vectors.SymbolCursor, vectors.ChunkCursor }
                .Where(static cursor => cursor is not null &&
                    (cursor.CompletedRevision < cursor.TargetRevision || cursor.PendingFiles > 0))
                .OrderByDescending(static cursor => cursor!.PendingFiles ?? 0)
                .FirstOrDefault();
            if (lagging is not null)
            {
                string pending = lagging.PendingFiles is { } count
                    ? $"; {count} files pending"
                    : string.Empty;
                warnings.Add(new HealthWarning(
                    "vectors_stale",
                    "usable_with_warnings",
                    $"vector convergence is behind revision {lagging.TargetRevision}{pending}"));
                vectorActions.Add(isLeader
                    ? "wait for this resident Miller leader to finish vector convergence"
                    : "open or keep a resident Miller leader running so vector convergence can complete");
            }

            recommended.InsertRange(0, vectorActions);
            return;
        }

        if (string.Equals(vectors.State, "model-not-prepared", StringComparison.OrdinalIgnoreCase))
        {
            string modelMessage = string.IsNullOrWhiteSpace(vectors.Reason)
                ? "vector retrieval is model-not-prepared"
                : $"vector retrieval is model-not-prepared: {vectors.Reason}";
            warnings.Add(new HealthWarning("vectors_model_not_prepared", "degraded", modelMessage));
            vectorActions.Add("run `miller semantic prepare` to install the selected embedding model");
            recommended.InsertRange(0, vectorActions);
            return;
        }

        string normalizedState = vectors.State.Replace('-', '_');
        string message = string.IsNullOrWhiteSpace(vectors.Reason)
            ? $"vector retrieval is {vectors.State}"
            : $"vector retrieval is {vectors.State}: {vectors.Reason}";
        warnings.Add(new HealthWarning($"vectors_{normalizedState}", "degraded", message));

        // A blocked coordinator queue is the one unavailable reason whose remedy is NOT a Miller leader doing
        // more convergence work: every convergence path submits into that same queue. Naming a wait or a
        // refresh here would send the reader in a circle, so the queue is what the action names.
        bool blockedQueue = vectors.Reason?.Contains(
            StoreCoordinatorQueueReader.BlockedQueueMarker, StringComparison.OrdinalIgnoreCase) == true;
        if (blockedQueue)
        {
            vectorActions.Add(
                "clear the family-store coordinator queue (`coord.db`) — vector convergence and " +
                "`miller workspace refresh` both submit into the blocked queue");
            recommended.InsertRange(0, vectorActions);
            return;
        }

        bool noArtifact = string.Equals(vectors.State, "unavailable", StringComparison.OrdinalIgnoreCase) &&
            vectors.Reason?.Contains("no vector artifact exists", StringComparison.OrdinalIgnoreCase) == true;
        if (noArtifact)
        {
            vectorActions.Add(isLeader
                ? "wait for this resident Miller leader to finish vector convergence after preparing the model"
                : "open or keep a resident Miller leader running after preparing the model");
            vectorActions.Add(
                "if vectors have never converged here, install the embedding model with `miller semantic prepare` " +
                "— a refresh alone cannot download it");
        }
        else
            vectorActions.Add(isLeader
                ? "retry vector convergence with bounded backoff and inspect vector convergence diagnostics"
                : "open or keep a resident Miller leader running so vector convergence can complete");

        recommended.InsertRange(0, vectorActions);
    }

    private static HealthState StateFrom(WorkspaceFacts statusFacts, IReadOnlyList<HealthWarning> warnings)
    {
        if (string.Equals(statusFacts.FreshnessStatus, "missing_index", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(statusFacts.FreshnessStatus, "unreadable_index", StringComparison.OrdinalIgnoreCase))
            return HealthState.Unavailable;

        if (warnings.Any(static warning => string.Equals(warning.Severity, "degraded", StringComparison.Ordinal)))
            return HealthState.Degraded;

        return warnings.Count > 0 ? HealthState.UsableWithWarnings : HealthState.Ready;
    }
}
