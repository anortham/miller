using System.Text.Json.Serialization;

namespace RetrievalEval;

public static class AgentEfficiencyVerdicts
{
    public const string Pass = "pass";
    public const string Fail = "fail";
    public const string NotDecisional = "not_decisional";
}

public sealed record AgentCompletionCells
{
    [JsonPropertyName("both_correct")] public int BothCorrect { get; init; }
    [JsonPropertyName("baseline_only")] public int BaselineOnly { get; init; }
    [JsonPropertyName("candidate_only")] public int CandidateOnly { get; init; }
    [JsonPropertyName("neither_correct")] public int NeitherCorrect { get; init; }
}

public sealed record AgentOutcomeCount
{
    [JsonPropertyName("success")] public int Success { get; init; }
    [JsonPropertyName("empty")] public int Empty { get; init; }
    [JsonPropertyName("refusal")] public int Refusal { get; init; }
    [JsonPropertyName("hard_error")] public int HardError { get; init; }
    [JsonPropertyName("wrong_answer")] public int WrongAnswer { get; init; }
}

public sealed record AgentOutcomeCounts
{
    [JsonPropertyName("baseline")] public AgentOutcomeCount Baseline { get; init; } = new();
    [JsonPropertyName("candidate")] public AgentOutcomeCount Candidate { get; init; } = new();
}

public sealed record AgentCorrectnessGate
{
    [JsonPropertyName("verdict")] public string Verdict { get; init; } = "";
    [JsonPropertyName("baseline_correct_count")] public int BaselineCorrectCount { get; init; }
    [JsonPropertyName("candidate_correct_count")] public int CandidateCorrectCount { get; init; }
    [JsonPropertyName("critical_loss_count")] public int CriticalLossCount { get; init; }
    [JsonPropertyName("baseline_wrong_action_task_count")] public int BaselineWrongActionTaskCount { get; init; }
    [JsonPropertyName("candidate_wrong_action_task_count")] public int CandidateWrongActionTaskCount { get; init; }
    [JsonPropertyName("baseline_wrong_action_rate")] public double BaselineWrongActionRate { get; init; }
    [JsonPropertyName("candidate_wrong_action_rate")] public double CandidateWrongActionRate { get; init; }
}

public sealed record AgentEfficiencyGate
{
    [JsonPropertyName("verdict")] public string Verdict { get; init; } = "";
    [JsonPropertyName("measurable")] public bool Measurable { get; init; }
    [JsonPropertyName("both_correct_task_count")] public int BothCorrectTaskCount { get; init; }
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
    [JsonPropertyName("baseline")] public IReadOnlyDictionary<string, int> Baseline { get; init; } = new SortedDictionary<string, int>(StringComparer.Ordinal);
    [JsonPropertyName("candidate")] public IReadOnlyDictionary<string, int> Candidate { get; init; } = new SortedDictionary<string, int>(StringComparer.Ordinal);
}

public sealed record AgentSubgroupReport
{
    [JsonPropertyName("task_count")] public int TaskCount { get; init; }
    [JsonPropertyName("completion")] public AgentCompletionCells Completion { get; init; } = new();
    [JsonPropertyName("outcome_counts")] public AgentOutcomeCounts OutcomeCounts { get; init; } = new();
    [JsonPropertyName("baseline_wrong_action_task_count")] public int BaselineWrongActionTaskCount { get; init; }
    [JsonPropertyName("candidate_wrong_action_task_count")] public int CandidateWrongActionTaskCount { get; init; }
}

public sealed record AgentEfficiencyReport
{
    [JsonPropertyName("contract_id")] public string ContractId { get; init; } = AgentEvaluationContract.Id;
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; } = AgentEvaluationContract.Version;
    [JsonPropertyName("decision_scope")] public string DecisionScope { get; init; } = "";
    [JsonPropertyName("decision_verdict")] public string DecisionVerdict { get; init; } = "";
    [JsonPropertyName("action_verdict")] public string ActionVerdict { get; init; } = "";
    [JsonPropertyName("task_count")] public int TaskCount { get; init; }
    [JsonPropertyName("completion")] public AgentCompletionCells Completion { get; init; } = new();
    [JsonPropertyName("outcome_counts")] public AgentOutcomeCounts OutcomeCounts { get; init; } = new();
    [JsonPropertyName("correctness")] public AgentCorrectnessGate Correctness { get; init; } = new();
    [JsonPropertyName("efficiency")] public AgentEfficiencyGate Efficiency { get; init; } = new();
    [JsonPropertyName("baseline")] public AgentArmMetrics Baseline { get; init; } = new();
    [JsonPropertyName("candidate")] public AgentArmMetrics Candidate { get; init; } = new();
    [JsonPropertyName("failure_counts")] public AgentFailureCounts FailureCounts { get; init; } = new();
    [JsonPropertyName("by_workflow")] public IReadOnlyDictionary<string, AgentSubgroupReport> ByWorkflow { get; init; } = new SortedDictionary<string, AgentSubgroupReport>(StringComparer.Ordinal);
    [JsonPropertyName("by_capability")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, AgentSubgroupReport>? ByCapability { get; init; }
    [JsonPropertyName("by_repo")] public IReadOnlyDictionary<string, AgentSubgroupReport> ByRepo { get; init; } = new SortedDictionary<string, AgentSubgroupReport>(StringComparer.Ordinal);
    [JsonPropertyName("by_language")] public IReadOnlyDictionary<string, AgentSubgroupReport> ByLanguage { get; init; } = new SortedDictionary<string, AgentSubgroupReport>(StringComparer.Ordinal);
}
