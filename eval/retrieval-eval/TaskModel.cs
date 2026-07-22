using System.Text.Json.Serialization;

namespace RetrievalEval;

/// <summary>One privacy-safe row in the sealed task manifest.</summary>
public sealed record TaskManifestRow
{
    [JsonPropertyName("task_id")] public string TaskId { get; init; } = "";
    [JsonPropertyName("repo")] public string Repo { get; init; } = "";
    [JsonPropertyName("language")] public string Language { get; init; } = "";
    [JsonPropertyName("query_profile")] public string QueryProfile { get; init; } = "";
}

/// <summary>One arm's aggregate-safe measurements for a sealed task.</summary>
public sealed record TaskArmResult
{
    [JsonPropertyName("task_id")] public string TaskId { get; init; } = "";
    [JsonPropertyName("completed")] public bool Completed { get; init; }
    [JsonPropertyName("duration_ms")] public long DurationMs { get; init; }
    [JsonPropertyName("tool_calls")] public int ToolCalls { get; init; }
    [JsonPropertyName("search_calls")] public int SearchCalls { get; init; }
    [JsonPropertyName("zero_result_search_calls")] public int ZeroResultSearchCalls { get; init; }
}

public static class TaskQueryProfiles
{
    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        ["identifier", "path", "short_token", "prose", "docs_like", "mixed"],
        StringComparer.Ordinal);
}
