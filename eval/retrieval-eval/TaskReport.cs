using System.Text.Json.Serialization;

namespace RetrievalEval;

public static class TaskVerdicts
{
    public const string Pass = "pass";
    public const string Fail = "fail";
    public const string Underpowered = "underpowered";
}

public sealed record TaskCompletionCells
{
    [JsonPropertyName("both_completed")] public int BothCompleted { get; init; }
    [JsonPropertyName("candidate_only")] public int CandidateOnly { get; init; }
    [JsonPropertyName("baseline_only")] public int BaselineOnly { get; init; }
    [JsonPropertyName("neither_completed")] public int NeitherCompleted { get; init; }
}

public sealed record TaskCompletionGate
{
    [JsonPropertyName("verdict")] public string Verdict { get; init; } = "";
    [JsonPropertyName("pair_count")] public int PairCount { get; init; }
    [JsonPropertyName("discordant_pair_count")] public int DiscordantPairCount { get; init; }
    [JsonPropertyName("candidate_win_share")] public double? CandidateWinShare { get; init; }
    [JsonPropertyName("wilson_lower_bound")] public double? WilsonLowerBound { get; init; }
    [JsonPropertyName("wilson_upper_bound")] public double? WilsonUpperBound { get; init; }
}

public sealed record TaskSafetyGate
{
    [JsonPropertyName("verdict")] public string Verdict { get; init; } = "";
    [JsonPropertyName("pair_count")] public int PairCount { get; init; }
    [JsonPropertyName("completion")] public TaskCompletionCells Completion { get; init; } = new();
}

public sealed record TaskArmAggregate
{
    [JsonPropertyName("pair_count")] public int PairCount { get; init; }
    [JsonPropertyName("completed_count")] public int CompletedCount { get; init; }
    [JsonPropertyName("completion_rate")] public double CompletionRate { get; init; }
    [JsonPropertyName("total_duration_ms")] public long TotalDurationMs { get; init; }
    [JsonPropertyName("mean_duration_ms")] public double MeanDurationMs { get; init; }
    [JsonPropertyName("total_tool_calls")] public long TotalToolCalls { get; init; }
    [JsonPropertyName("mean_tool_calls")] public double MeanToolCalls { get; init; }
    [JsonPropertyName("total_search_calls")] public long TotalSearchCalls { get; init; }
    [JsonPropertyName("mean_search_calls")] public double MeanSearchCalls { get; init; }
    [JsonPropertyName("total_zero_result_search_calls")] public long TotalZeroResultSearchCalls { get; init; }
    [JsonPropertyName("mean_zero_result_search_calls")] public double MeanZeroResultSearchCalls { get; init; }
    [JsonPropertyName("zero_result_search_rate")] public double ZeroResultSearchRate { get; init; }
}

public sealed record TaskArmDiagnostics
{
    [JsonPropertyName("baseline")] public TaskArmAggregate Baseline { get; init; } = new();
    [JsonPropertyName("candidate")] public TaskArmAggregate Candidate { get; init; } = new();
}

public sealed record TaskSubgroupReport
{
    [JsonPropertyName("pair_count")] public int PairCount { get; init; }
    [JsonPropertyName("completion")] public TaskCompletionCells Completion { get; init; } = new();
    [JsonPropertyName("diagnostics")] public TaskArmDiagnostics Diagnostics { get; init; } = new();
}

/// <summary>Aggregate-only paired task-completion report. It intentionally has no task-level rows.</summary>
public sealed record TaskCompletionReport
{
    [JsonPropertyName("schema")] public int Schema { get; init; } = 1;
    [JsonPropertyName("pair_count")] public int PairCount { get; init; }
    [JsonPropertyName("completion")] public TaskCompletionCells Completion { get; init; } = new();
    [JsonPropertyName("primary_gate")] public TaskCompletionGate PrimaryGate { get; init; } = new();
    [JsonPropertyName("identifier_path_safety")] public TaskSafetyGate IdentifierPathSafety { get; init; } = new();
    [JsonPropertyName("diagnostics")] public TaskArmDiagnostics Diagnostics { get; init; } = new();
    [JsonPropertyName("by_repo")] public IReadOnlyDictionary<string, TaskSubgroupReport> ByRepo { get; init; } = new SortedDictionary<string, TaskSubgroupReport>(StringComparer.Ordinal);
    [JsonPropertyName("by_language")] public IReadOnlyDictionary<string, TaskSubgroupReport> ByLanguage { get; init; } = new SortedDictionary<string, TaskSubgroupReport>(StringComparer.Ordinal);
    [JsonPropertyName("by_query_profile")] public IReadOnlyDictionary<string, TaskSubgroupReport> ByQueryProfile { get; init; } = new SortedDictionary<string, TaskSubgroupReport>(StringComparer.Ordinal);
}
