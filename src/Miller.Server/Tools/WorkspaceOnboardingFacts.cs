using Miller.Indexing;
using Miller.Server.Telemetry;

namespace Miller.Server.Tools;

public sealed record WorkspaceOnboardingFacts(
    WorkspaceFacts StatusFacts,
    TelemetryOnboardingFacts Telemetry,
    IReadOnlyList<RecoveredTargetHash> HotTargets,
    IReadOnlyList<string> StartHere,
    IReadOnlyList<string> InstructionNotes,
    IReadOnlyList<string> PrivacyNotes)
{
    public static WorkspaceOnboardingFacts Create(
        WorkspaceFacts statusFacts,
        TelemetryOnboardingFacts telemetry,
        IReadOnlyList<RecoveredTargetHash> hotTargets)
    {
        ArgumentNullException.ThrowIfNull(telemetry);
        ArgumentNullException.ThrowIfNull(hotTargets);

        var startHere = new List<string>
        {
            "run workspace health first when taking over this repo",
        };

        if (telemetry.TotalCalls == 0 ||
            telemetry.SuccessfulFlows.Any(static flow => flow.From.StartsWith("search", StringComparison.Ordinal) &&
                                                        flow.To.StartsWith("inspect", StringComparison.Ordinal)) ||
            (telemetry.ToolMix.Any(static row => row.Tool == "search") &&
             telemetry.ToolMix.Any(static row => row.Tool == "inspect")))
        {
            startHere.Add("use search to find candidate symbols, then inspect the selected result before editing");
        }

        startHere.Add("use inspect depth=overview for first symbol reads; use depth=full only when you need complete bodies");

        if (telemetry.ToolMix.Any(static row => row.Tool == "context"))
            startHere.Add("use context for broad orientation before reading whole files");
        else
            startHere.Add("use context when you need a bounded map of the code around a task");

        startHere.Add("use impact before refactors or risky edits");

        var notes = new List<string>();
        if (telemetry.CommonMisses.Any(static miss => miss.Tool == "search"))
            notes.Add("search has recent empty results; try mode=source, mode=content, or a narrower symbol name when symbol search misses");
        long inspectFullCalls = telemetry.ToolMix
            .Where(static row => row.Tool == "inspect" && string.Equals(row.Op, "full", StringComparison.OrdinalIgnoreCase))
            .Sum(static row => row.Calls);
        long inspectOverviewCalls = telemetry.ToolMix
            .Where(static row => row.Tool == "inspect" && string.Equals(row.Op, "overview", StringComparison.OrdinalIgnoreCase))
            .Sum(static row => row.Calls);
        if (inspectFullCalls > inspectOverviewCalls)
            notes.Add("inspect full is common in recent telemetry; start with inspect depth=overview before full-body reads");
        long traceCalls = telemetry.ToolMix
            .Where(static row => row.Tool == "trace")
            .Sum(static row => row.Calls);
        long traceEmpty = telemetry.ToolMix
            .Where(static row => row.Tool == "trace")
            .Sum(static row => row.EmptyCount);
        if (traceCalls > 0 && traceEmpty * 2 >= traceCalls)
            notes.Add("trace has recent empty results; try trace mode=refs and search mode=source when path/graph traces do not connect");
        long contentReadCalls = telemetry.ToolMix
            .Where(static row => row.Tool == "content" && string.Equals(row.Op, "read", StringComparison.OrdinalIgnoreCase))
            .Sum(static row => row.Calls);
        long contentReadErrors = telemetry.ToolMix
            .Where(static row => row.Tool == "content" && string.Equals(row.Op, "read", StringComparison.OrdinalIgnoreCase))
            .Sum(static row => row.ErrorCount);
        if (contentReadCalls > 0 && contentReadErrors > 0)
            notes.Add("content read has recent errors; use the source_id from content search/list and pass workspace_id for cross-workspace hits");
        if (telemetry.TotalCalls > 0 && telemetry.ToolMix.All(static row => row.Tool != "patterns"))
            notes.Add("patterns operation=list is available before raw route, HTML, JSON, YAML, or Markdown greps");
        if (telemetry.TotalCalls > 0 && telemetry.ToolMix.All(static row => row.Tool != "trace"))
            notes.Add("trace is available for refs/path questions when you need usages or dependency paths");
        if (hotTargets.Any(static target => target.Confidence == "unresolved_hash"))
            notes.Add("some repeated targets no longer match the current index; refresh before relying on older telemetry patterns");
        if (telemetry.Friction.Any(static row => row.ErrorCount > 0))
            notes.Add("recent telemetry includes tool errors; check workspace health before trusting follow-on results");
        if (telemetry.State == "sparse")
            notes.Add("telemetry is sparse; treat this onboarding as a starting point, not a stable workflow profile");
        if (!telemetry.Available)
            notes.Add("telemetry is unavailable; this onboarding falls back to generic Miller startup guidance");
        if (SemanticNeedsPreparing(statusFacts.Vectors))
            notes.Add("semantic retrieval is enabled but not serving; run `miller semantic prepare` once to install " +
                      "the embedding model. If prepare reports `activated`, no restart is needed; restart the " +
                      "server only when it reports `no_live_broker` or `still_not_ready`");

        static bool SemanticNeedsPreparing(VectorSidecarFacts? vectors) =>
            vectors is not null &&
            (string.Equals(vectors.State, "model-not-prepared", StringComparison.OrdinalIgnoreCase) ||
             (string.Equals(vectors.State, "unavailable", StringComparison.OrdinalIgnoreCase) &&
              vectors.Reason?.Contains("no vector artifact exists", StringComparison.OrdinalIgnoreCase) == true));

        string[] privacy =
        [
            "raw queries and targets are not stored in telemetry",
            "target hashes are only matched against the current local index",
            "unresolved hashes are reported without exposing the hash value",
        ];

        return new WorkspaceOnboardingFacts(
            statusFacts,
            telemetry,
            hotTargets.OrderByDescending(static row => row.Calls)
                .ThenBy(static row => row.Confidence, StringComparer.Ordinal)
                .ToArray(),
            startHere.Distinct(StringComparer.Ordinal).ToArray(),
            notes.Distinct(StringComparer.Ordinal).ToArray(),
            privacy);
    }
}
