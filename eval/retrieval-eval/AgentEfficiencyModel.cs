using System.Text.Json.Serialization;

namespace RetrievalEval;

public static class AgentEvaluationContract
{
    public const string Id = "takeover-evaluation-v1";
    public const int Version = 1;
    public const string LegacyAdapterId = "legacy-agent-score-adapter-v0";
    public const int LegacyAdapterVersion = 0;
}

public static class AgentDecisionScopes
{
    public const string Subset = "subset";
    public const string Full = "full";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [Subset, Full],
        StringComparer.Ordinal);
}

public static class AgentExpectedOutcomes
{
    public const string Success = "success";
    public const string Empty = "empty";
    public const string Refusal = "refusal";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [Success, Empty, Refusal],
        StringComparer.Ordinal);
}

public static class AgentObservedOutcomes
{
    public const string Success = AgentExpectedOutcomes.Success;
    public const string Empty = AgentExpectedOutcomes.Empty;
    public const string Refusal = AgentExpectedOutcomes.Refusal;
    public const string HardError = "hard_error";
    public const string WrongAnswer = "wrong_answer";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [Success, Empty, Refusal, HardError, WrongAnswer],
        StringComparer.Ordinal);

    public static bool IsFailure(string outcome) =>
        outcome is HardError or WrongAnswer;
}

public static class AgentCapabilities
{
    public const string Discovery = "discovery";
    public const string ExactSymbolLookup = "exact_symbol_lookup";
    public const string HomonymDisambiguation = "homonym_disambiguation";
    public const string ContextOrientation = "context_orientation";
    public const string Callers = "callers";
    public const string Callees = "callees";
    public const string CallPath = "call_path";
    public const string ImpactTests = "impact_tests";
    public const string Edit = "edit";
    public const string Rename = "rename";
    public const string Logs = "logs";
    public const string Patterns = "patterns";
    public const string WorkspaceRecovery = "workspace_recovery";
    internal const string LegacyCompatibility = "legacy_compatibility";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [
            Discovery,
            ExactSymbolLookup,
            HomonymDisambiguation,
            ContextOrientation,
            Callers,
            Callees,
            CallPath,
            ImpactTests,
            Edit,
            Rename,
            Logs,
            Patterns,
            WorkspaceRecovery,
        ],
        StringComparer.Ordinal);
}

public sealed record AgentTaskManifestRow
{
    [JsonPropertyName("contract_id")] public string ContractId { get; init; } = "";
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; }
    [JsonPropertyName("task_id")] public string TaskId { get; init; } = "";
    [JsonPropertyName("repo")] public string Repo { get; init; } = "";
    [JsonPropertyName("language")] public string Language { get; init; } = "";
    [JsonPropertyName("workflow_class")] public string WorkflowClass { get; init; } = "";
    [JsonPropertyName("evidence_critical")] public bool EvidenceCritical { get; init; }
    [JsonPropertyName("expected_outcome")] public string ExpectedOutcome { get; init; } = "";
    [JsonPropertyName("capabilities")] public IReadOnlyList<string> Capabilities { get; init; } = [];
}

public sealed record AgentRunResult
{
    [JsonPropertyName("contract_id")] public string ContractId { get; init; } = "";
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; }
    [JsonPropertyName("task_id")] public string TaskId { get; init; } = "";
    [JsonPropertyName("repetition")] public int Repetition { get; init; }
    [JsonPropertyName("observed_outcome")] public string ObservedOutcome { get; init; } = "";
    [JsonPropertyName("wrong_action_count")] public int WrongActionCount { get; init; }
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
