using System.Text.Json.Serialization;

namespace RetrievalEval;

/// <summary>A graded relevant document for one query.</summary>
public sealed record RelevantDoc
{
    [JsonPropertyName("doc_id")] public string DocId { get; init; } = "";
    [JsonPropertyName("grade")] public int Grade { get; init; }
}

/// <summary>One row of the query-set JSONL.</summary>
public sealed record EvalQuery
{
    [JsonPropertyName("query_id")] public string QueryId { get; init; } = "";
    [JsonPropertyName("query")] public string Query { get; init; } = "";
    [JsonPropertyName("intent_cluster")] public string? IntentCluster { get; init; }
    [JsonPropertyName("query_class")] public string QueryClass { get; init; } = "";
    [JsonPropertyName("search_mode")] public string SearchMode { get; init; } = SearchModes.Auto;
    [JsonPropertyName("repo")] public string Repo { get; init; } = "";
    [JsonPropertyName("language")] public string Language { get; init; } = "";
    [JsonPropertyName("relevant")] public IReadOnlyList<RelevantDoc> Relevant { get; init; } = [];
    [JsonPropertyName("negative")] public bool Negative { get; init; }
    [JsonPropertyName("tags")] public IReadOnlyList<string> Tags { get; init; } = [];
    [JsonPropertyName("note")] public string? Note { get; init; }
}

/// <summary>One row of an arm's results JSONL.</summary>
public sealed record EvalResult
{
    [JsonPropertyName("query_id")] public string QueryId { get; init; } = "";
    [JsonPropertyName("ranked")] public IReadOnlyList<string> Ranked { get; init; } = [];
}

public static class QueryClasses
{
    public static readonly IReadOnlyList<string> All =
        ["identifier", "path", "short_token", "prose", "docs_like", "mixed"];
}

public static class SearchModes
{
    public const string Auto = "auto";
    public const string Symbol = "symbol";
    public const string File = "file";
    public const string Content = "content";
    public const string Source = "source";

    public static readonly IReadOnlyList<string> All = [Auto, Symbol, File, Content, Source];
}
