using System.Text.Json.Serialization;

namespace RetrievalEval;

public static class AgentEfficiencyVerdicts
{
    public const string Pass = "pass";
    public const string Fail = "fail";
}

public sealed record AgentCompletionCells
{
    [JsonPropertyName("both_completed")] public int BothCompleted { get; init; }
    [JsonPropertyName("miller_only")] public int MillerOnly { get; init; }
    [JsonPropertyName("julie_only")] public int JulieOnly { get; init; }
    [JsonPropertyName("neither_completed")] public int NeitherCompleted { get; init; }
}

public sealed record AgentCorrectnessGate
{
    [JsonPropertyName("verdict")] public string Verdict { get; init; } = "";
    [JsonPropertyName("miller_completed_count")] public int MillerCompletedCount { get; init; }
    [JsonPropertyName("julie_completed_count")] public int JulieCompletedCount { get; init; }
    [JsonPropertyName("critical_loss_count")] public int CriticalLossCount { get; init; }
}

public sealed record AgentEfficiencyGate
{
    [JsonPropertyName("verdict")] public string Verdict { get; init; } = "";
    [JsonPropertyName("measurable")] public bool Measurable { get; init; }
    [JsonPropertyName("both_pass_task_count")] public int BothPassTaskCount { get; init; }
    [JsonPropertyName("token_route_passed")] public bool TokenRoutePassed { get; init; }
    [JsonPropertyName("call_route_passed")] public bool CallRoutePassed { get; init; }
    [JsonPropertyName("wall_guard_passed")] public bool WallGuardPassed { get; init; }
}

public sealed record AgentArmMetrics
{
    [JsonPropertyName("median_tool_output_tokens")] public double? MedianToolOutputTokens { get; init; }
    [JsonPropertyName("median_tool_calls")] public double? MedianToolCalls { get; init; }
    [JsonPropertyName("p75_duration_ms")] public double? P75DurationMs { get; init; }
}

public sealed record AgentFailureCounts
{
    [JsonPropertyName("miller")] public IReadOnlyDictionary<string, int> Miller { get; init; } = new SortedDictionary<string, int>(StringComparer.Ordinal);
    [JsonPropertyName("julie")] public IReadOnlyDictionary<string, int> Julie { get; init; } = new SortedDictionary<string, int>(StringComparer.Ordinal);
}

public sealed record AgentSubgroupReport
{
    [JsonPropertyName("task_count")] public int TaskCount { get; init; }
    [JsonPropertyName("completion")] public AgentCompletionCells Completion { get; init; } = new();
}

public sealed record AgentEfficiencyReport
{
    [JsonPropertyName("schema")] public int Schema { get; init; } = 1;
    [JsonPropertyName("verdict")] public string Verdict { get; init; } = "";
    [JsonPropertyName("task_count")] public int TaskCount { get; init; }
    [JsonPropertyName("completion")] public AgentCompletionCells Completion { get; init; } = new();
    [JsonPropertyName("correctness")] public AgentCorrectnessGate Correctness { get; init; } = new();
    [JsonPropertyName("efficiency")] public AgentEfficiencyGate Efficiency { get; init; } = new();
    [JsonPropertyName("miller")] public AgentArmMetrics Miller { get; init; } = new();
    [JsonPropertyName("julie")] public AgentArmMetrics Julie { get; init; } = new();
    [JsonPropertyName("failure_counts")] public AgentFailureCounts FailureCounts { get; init; } = new();
    [JsonPropertyName("by_workflow")] public IReadOnlyDictionary<string, AgentSubgroupReport> ByWorkflow { get; init; } = new SortedDictionary<string, AgentSubgroupReport>(StringComparer.Ordinal);
    [JsonPropertyName("by_repo")] public IReadOnlyDictionary<string, AgentSubgroupReport> ByRepo { get; init; } = new SortedDictionary<string, AgentSubgroupReport>(StringComparer.Ordinal);
    [JsonPropertyName("by_language")] public IReadOnlyDictionary<string, AgentSubgroupReport> ByLanguage { get; init; } = new SortedDictionary<string, AgentSubgroupReport>(StringComparer.Ordinal);
}
