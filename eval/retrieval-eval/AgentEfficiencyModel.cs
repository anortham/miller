using System.Text.Json.Serialization;

namespace RetrievalEval;

public sealed record AgentTaskManifestRow
{
    [JsonPropertyName("task_id")] public string TaskId { get; init; } = "";
    [JsonPropertyName("repo")] public string Repo { get; init; } = "";
    [JsonPropertyName("language")] public string Language { get; init; } = "";
    [JsonPropertyName("workflow_class")] public string WorkflowClass { get; init; } = "";
    [JsonPropertyName("evidence_critical")] public bool EvidenceCritical { get; init; }
}

public sealed record AgentRunResult
{
    [JsonPropertyName("task_id")] public string TaskId { get; init; } = "";
    [JsonPropertyName("repetition")] public int Repetition { get; init; }
    [JsonPropertyName("completed")] public bool Completed { get; init; }
    [JsonPropertyName("failure_reason")] public string? FailureReason { get; init; }
    [JsonPropertyName("duration_ms")] public long DurationMs { get; init; }
    [JsonPropertyName("tool_calls")] public long ToolCalls { get; init; }
    [JsonPropertyName("tool_output_bytes")] public long ToolOutputBytes { get; init; }
    [JsonPropertyName("tool_output_tokens")] public long ToolOutputTokens { get; init; }
    [JsonPropertyName("model_input_tokens")] public long ModelInputTokens { get; init; }
    [JsonPropertyName("model_output_tokens")] public long ModelOutputTokens { get; init; }
    [JsonPropertyName("product_errors")] public long ProductErrors { get; init; }
    [JsonPropertyName("duplicate_calls")] public long DuplicateCalls { get; init; }
    [JsonPropertyName("uncited_tool_output_tokens")] public long UncitedToolOutputTokens { get; init; }
}

public static class AgentWorkflowClasses
{
    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        ["exact_lookup", "concept_search", "docs_config", "context_assembly", "references_trace", "impact_tests"],
        StringComparer.Ordinal);

    public static readonly IReadOnlySet<string> EvidenceCritical = new HashSet<string>(
        ["exact_lookup", "references_trace", "impact_tests"],
        StringComparer.Ordinal);
}

public static class AgentFailureReasons
{
    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        ["incorrect", "insufficient_evidence", "budget_exceeded", "disallowed_tool", "product_error", "invalid_answer"],
        StringComparer.Ordinal);
}
