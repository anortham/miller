using Miller.Indexing;
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

public sealed record WorkspaceHealthFacts(
    WorkspaceFacts StatusFacts,
    TelemetrySummary Telemetry,
    TelemetryHealthFacts TelemetryHealth,
    WorkspaceExtractionHealthFacts Extraction,
    IReadOnlyList<HealthWarning> Warnings,
    IReadOnlyList<string> RecommendedActions,
    HealthState State,
    string Summary)
{
    public static WorkspaceHealthFacts Create(
        WorkspaceFacts statusFacts,
        TelemetrySummary telemetry,
        TelemetryHealthFacts telemetryHealth,
        WorkspaceExtractionHealthFacts extraction)
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
        AddUnavailableSectionWarnings(warnings, extraction.Files.Available, extraction.Files.Error, "files_unavailable");

        if (statusFacts.IndexFresh == false)
            warnings.Add(new HealthWarning("index_stale", "degraded", "workspace index is stale"));
        if (!string.IsNullOrWhiteSpace(statusFacts.WarningText))
            warnings.Add(new HealthWarning("index_warning", "degraded", statusFacts.WarningText));
        AddSidecarWarning(warnings, "search_sidecar", statusFacts.SearchSidecar?.State, statusFacts.SearchSidecar?.Error);
        AddSidecarWarning(warnings, "content_corpus", statusFacts.ContentCorpus?.State, statusFacts.ContentCorpus?.Error);

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
            summary);
    }

    public static string StateName(HealthState state) => state switch
    {
        HealthState.Ready => "ready",
        HealthState.UsableWithWarnings => "usable_with_warnings",
        HealthState.Degraded => "degraded",
        HealthState.Unavailable => "unavailable",
        _ => state.ToString().ToLowerInvariant(),
    };

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
