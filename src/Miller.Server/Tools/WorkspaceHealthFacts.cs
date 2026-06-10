using Miller.Indexing;
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
/// already travel on <see cref="WorkspaceFacts"/>.
/// </summary>
public sealed record LeaderHealthFacts(LeaderIdentity? Identity, bool? Alive)
{
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
    LeaderHealthFacts? Leader = null)
{
    public static WorkspaceHealthFacts Create(
        WorkspaceFacts statusFacts,
        TelemetrySummary telemetry,
        TelemetryHealthFacts telemetryHealth,
        WorkspaceExtractionHealthFacts extraction,
        LeaderHealthFacts? leader = null)
    {
        var warnings = new List<HealthWarning>();
        var recommended = new List<string>();

        long openCapabilityGaps = extraction.CapabilityGaps.Rows
            .Where(static row => string.Equals(row.Status, "open", StringComparison.OrdinalIgnoreCase))
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
        AddSidecarWarning(warnings, "search_sidecar", statusFacts.SearchSidecar?.State, statusFacts.SearchSidecar?.Error);
        AddSidecarWarning(warnings, "content_corpus", statusFacts.ContentCorpus?.State, statusFacts.ContentCorpus?.Error);

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
            leader);
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
        string severity = string.Equals(state, "missing", StringComparison.OrdinalIgnoreCase)
            ? "usable_with_warnings"
            : "degraded";
        warnings.Add(new HealthWarning(code, severity, message));
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
