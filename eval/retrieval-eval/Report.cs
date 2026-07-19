using System.Text.Json.Serialization;

namespace RetrievalEval;

public sealed record MetricBlock
{
    [JsonPropertyName("recall_at_k")] public double RecallAtK { get; init; }
    [JsonPropertyName("ndcg_at_k")] public double NdcgAtK { get; init; }
    [JsonPropertyName("query_count")] public int QueryCount { get; init; }
}

public sealed record MacroAverage
{
    [JsonPropertyName("recall_at_k")] public double RecallAtK { get; init; }
    [JsonPropertyName("ndcg_at_k")] public double NdcgAtK { get; init; }
    [JsonPropertyName("language_count")] public int LanguageCount { get; init; }
}

public sealed record WorstLanguage
{
    [JsonPropertyName("language")] public string Language { get; init; } = "";
    [JsonPropertyName("recall_at_k")] public double RecallAtK { get; init; }
    [JsonPropertyName("ndcg_at_k")] public double NdcgAtK { get; init; }
    [JsonPropertyName("query_count")] public int QueryCount { get; init; }
}

/// <summary>
/// Per-intent-cluster rollup. A paraphrase cluster is a single sample: <see cref="ClusterHit"/> is true when
/// ANY member paraphrase retrieved at least one relevant doc inside the cutoff.
/// </summary>
public sealed record ClusterRollup
{
    [JsonPropertyName("intent_cluster")] public string IntentCluster { get; init; } = "";
    [JsonPropertyName("repo")] public string Repo { get; init; } = "";
    [JsonPropertyName("language")] public string Language { get; init; } = "";
    [JsonPropertyName("member_count")] public int MemberCount { get; init; }
    [JsonPropertyName("cluster_hit")] public bool ClusterHit { get; init; }
    [JsonPropertyName("member_hit_rate")] public double MemberHitRate { get; init; }
    [JsonPropertyName("recall_at_k")] public double RecallAtK { get; init; }
    [JsonPropertyName("ndcg_at_k")] public double NdcgAtK { get; init; }
}

public sealed record ClusterSummary
{
    [JsonPropertyName("cluster_count")] public int ClusterCount { get; init; }
    [JsonPropertyName("cluster_hit_count")] public int ClusterHitCount { get; init; }
    [JsonPropertyName("cluster_hit_rate")] public double ClusterHitRate { get; init; }
}

/// <summary>
/// Negative-query outcome. Results files are post-threshold — an arm emits a doc only when it is confident
/// enough to show it — so "returned any doc inside the cutoff" IS the false-positive signal.
/// </summary>
public sealed record NegativeBlock
{
    [JsonPropertyName("count")] public int Count { get; init; }
    [JsonPropertyName("false_positive_count")] public int FalsePositiveCount { get; init; }
    [JsonPropertyName("false_positive_rate")] public double FalsePositiveRate { get; init; }
    [JsonPropertyName("pass_rate")] public double PassRate { get; init; }
}

public sealed record EvalReport
{
    [JsonPropertyName("k")] public int K { get; init; }
    [JsonPropertyName("query_count")] public int QueryCount { get; init; }
    [JsonPropertyName("positive_query_count")] public int PositiveQueryCount { get; init; }
    [JsonPropertyName("negative_query_count")] public int NegativeQueryCount { get; init; }
    [JsonPropertyName("overall")] public MetricBlock Overall { get; init; } = new();
    [JsonPropertyName("per_language")] public IReadOnlyDictionary<string, MetricBlock> PerLanguage { get; init; } = new Dictionary<string, MetricBlock>();
    [JsonPropertyName("language_macro_average")] public MacroAverage LanguageMacroAverage { get; init; } = new();
    [JsonPropertyName("worst_language")] public WorstLanguage? WorstLanguage { get; init; }
    [JsonPropertyName("per_query_class")] public IReadOnlyDictionary<string, MetricBlock> PerQueryClass { get; init; } = new Dictionary<string, MetricBlock>();
    [JsonPropertyName("per_intent_cluster")] public IReadOnlyList<ClusterRollup> PerIntentCluster { get; init; } = [];
    [JsonPropertyName("intent_cluster_summary")] public ClusterSummary IntentClusterSummary { get; init; } = new();
    [JsonPropertyName("negatives")] public NegativeBlock Negatives { get; init; } = new();
    [JsonPropertyName("missing_results")] public IReadOnlyList<string> MissingResults { get; init; } = [];
    [JsonPropertyName("unknown_results")] public IReadOnlyList<string> UnknownResults { get; init; } = [];
    [JsonPropertyName("inputs")] public ReportInputs? Inputs { get; init; }
    [JsonPropertyName("corpus_validation")] public CorpusValidation? CorpusValidation { get; init; }
}

public sealed record ReportInputs
{
    [JsonPropertyName("queries")] public string Queries { get; init; } = "";
    [JsonPropertyName("results")] public string Results { get; init; } = "";
    [JsonPropertyName("corpus")] public IReadOnlyDictionary<string, string> Corpus { get; init; } = new Dictionary<string, string>();
}

public sealed record CorpusValidation
{
    [JsonPropertyName("checked_doc_count")] public int CheckedDocCount { get; init; }
    [JsonPropertyName("missing_doc_count")] public int MissingDocCount { get; init; }
    [JsonPropertyName("missing_docs")] public IReadOnlyList<string> MissingDocs { get; init; } = [];
}
