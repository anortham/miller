using System.Text.Json.Serialization;

namespace RetrievalEval;

public sealed record AgentRelevanceMetrics
{
    [JsonPropertyName("recall_at_6")] public double RecallAt6 { get; init; }
    [JsonPropertyName("ndcg_at_6")] public double NdcgAt6 { get; init; }
    [JsonPropertyName("mrr")] public double Mrr { get; init; }
    [JsonPropertyName("top_1")] public double Top1 { get; init; }
}

public sealed record AgentRelevanceGate
{
    [JsonPropertyName("verdict")] public string Verdict { get; init; } = "";
    [JsonPropertyName("task_count")] public int TaskCount { get; init; }
    [JsonPropertyName("baseline")] public AgentRelevanceMetrics Baseline { get; init; } = new();
    [JsonPropertyName("candidate")] public AgentRelevanceMetrics Candidate { get; init; } = new();
}

public sealed record AgentDecisionReport
{
    [JsonPropertyName("contract_id")] public string ContractId { get; init; } = AgentEvaluationContract.Id;
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; } = AgentEvaluationContract.Version;
    [JsonPropertyName("decision_scope")] public string DecisionScope { get; init; } = "";
    [JsonPropertyName("decision_verdict")] public string DecisionVerdict { get; init; } = AgentEfficiencyVerdicts.NotDecisional;
    [JsonPropertyName("action_verdict")] public string ActionVerdict { get; init; } = "";
    [JsonPropertyName("task_count")] public int TaskCount { get; init; }
    [JsonPropertyName("completion")] public AgentCompletionCells Completion { get; init; } = new();
    [JsonPropertyName("outcome_counts")] public AgentOutcomeCounts OutcomeCounts { get; init; } = new();
    [JsonPropertyName("relevance")] public AgentRelevanceGate Relevance { get; init; } = new();
    [JsonPropertyName("correctness")] public AgentCorrectnessGate Correctness { get; init; } = new();
    [JsonPropertyName("efficiency")] public AgentEfficiencyGate Efficiency { get; init; } = new();
    [JsonPropertyName("baseline")] public AgentArmMetrics Baseline { get; init; } = new();
    [JsonPropertyName("candidate")] public AgentArmMetrics Candidate { get; init; } = new();
    [JsonPropertyName("failure_counts")] public AgentFailureCounts FailureCounts { get; init; } = new();
    [JsonPropertyName("by_workflow")] public IReadOnlyDictionary<string, AgentSubgroupReport> ByWorkflow { get; init; } = new SortedDictionary<string, AgentSubgroupReport>(StringComparer.Ordinal);
    [JsonPropertyName("by_capability")] public IReadOnlyDictionary<string, AgentSubgroupReport> ByCapability { get; init; } = new SortedDictionary<string, AgentSubgroupReport>(StringComparer.Ordinal);
    [JsonPropertyName("by_repo")] public IReadOnlyDictionary<string, AgentSubgroupReport> ByRepo { get; init; } = new SortedDictionary<string, AgentSubgroupReport>(StringComparer.Ordinal);
    [JsonPropertyName("by_language")] public IReadOnlyDictionary<string, AgentSubgroupReport> ByLanguage { get; init; } = new SortedDictionary<string, AgentSubgroupReport>(StringComparer.Ordinal);
}
