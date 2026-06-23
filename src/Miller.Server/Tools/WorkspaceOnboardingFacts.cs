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

        if (telemetry.ToolMix.Any(static row => row.Tool == "context"))
            startHere.Add("use context for broad orientation before reading whole files");
        else
            startHere.Add("use context when you need a bounded map of the code around a task");

        if (telemetry.TotalCalls == 0 || telemetry.ToolMix.Any(static row => row.Tool == "impact"))
            startHere.Add("use impact before refactors or risky edits");

        var notes = new List<string>();
        if (telemetry.CommonMisses.Any(static miss => miss.Tool == "search"))
            notes.Add("search has recent empty results; try mode=source, mode=content, or a narrower symbol name when symbol search misses");
        if (hotTargets.Any(static target => target.Confidence == "unresolved_hash"))
            notes.Add("some repeated targets no longer match the current index; refresh before relying on older telemetry patterns");
        if (telemetry.Friction.Any(static row => row.ErrorCount > 0))
            notes.Add("recent telemetry includes tool errors; check workspace health before trusting follow-on results");
        if (telemetry.State == "sparse")
            notes.Add("telemetry is sparse; treat this onboarding as a starting point, not a stable workflow profile");
        if (!telemetry.Available)
            notes.Add("telemetry is unavailable; this onboarding falls back to generic Miller startup guidance");

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
                .Take(10)
                .ToArray(),
            startHere.Distinct(StringComparer.Ordinal).Take(5).ToArray(),
            notes.Distinct(StringComparer.Ordinal).Take(5).ToArray(),
            privacy);
    }
}
