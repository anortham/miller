using System.Text.Json.Serialization;

namespace RetrievalEval;

/// <summary>
/// One aggregated metric block. <see cref="UnitCount"/> is the number of evaluation units averaged;
/// <see cref="QueryCount"/> is the number of underlying queries those units cover. Under the per-query unit
/// policy the two are equal; under the cluster unit policy a paraphrase cluster contributes one unit and N queries.
/// </summary>
public sealed record MetricBlock
{
    [JsonPropertyName("recall_at_k")] public double RecallAtK { get; init; }
    [JsonPropertyName("ndcg_at_k")] public double NdcgAtK { get; init; }
    [JsonPropertyName("unit_count")] public int UnitCount { get; init; }
    [JsonPropertyName("query_count")] public int QueryCount { get; init; }
}

/// <summary>Evaluation-unit policies a report's primary metrics can average over.</summary>
public static class UnitPolicies
{
    /// <summary>Design §8: an intent cluster is one unit, scored as the mean over its member paraphrases.</summary>
    public const string Cluster = "cluster";

    /// <summary>Every positive query is its own unit.</summary>
    public const string PerQuery = "per_query";
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
    [JsonPropertyName("unit_count")] public int UnitCount { get; init; }
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
    /// <summary>Names the evaluation unit the primary metrics average over. See design §8.</summary>
    [JsonPropertyName("unit_policy")] public string UnitPolicy { get; init; } = UnitPolicies.Cluster;
    [JsonPropertyName("query_count")] public int QueryCount { get; init; }
    [JsonPropertyName("positive_query_count")] public int PositiveQueryCount { get; init; }
    [JsonPropertyName("negative_query_count")] public int NegativeQueryCount { get; init; }
    [JsonPropertyName("evaluation_unit_count")] public int EvaluationUnitCount { get; init; }
    /// <summary>PRIMARY. Cluster-unit mean: each intent cluster is one unit scored as the mean over its members.</summary>
    [JsonPropertyName("overall")] public MetricBlock Overall { get; init; } = new();
    /// <summary>Secondary. Every positive query weighted equally, ignoring cluster membership.</summary>
    [JsonPropertyName("overall_per_query")] public MetricBlock OverallPerQuery { get; init; } = new();
    /// <summary>Secondary. Cluster-unit mean where a cluster takes its best member's score (best-phrasing view).</summary>
    [JsonPropertyName("overall_cluster_max")] public MetricBlock OverallClusterMax { get; init; } = new();
    /// <summary>PRIMARY. Cluster-unit.</summary>
    [JsonPropertyName("per_language")] public IReadOnlyDictionary<string, MetricBlock> PerLanguage { get; init; } = new Dictionary<string, MetricBlock>();
    /// <summary>Secondary. Per-query reference view of the same languages.</summary>
    [JsonPropertyName("per_language_per_query")] public IReadOnlyDictionary<string, MetricBlock> PerLanguagePerQuery { get; init; } = new Dictionary<string, MetricBlock>();
    [JsonPropertyName("language_macro_average")] public MacroAverage LanguageMacroAverage { get; init; } = new();
    /// <summary>PRIMARY. Cluster-unit.</summary>
    [JsonPropertyName("worst_language")] public WorstLanguage? WorstLanguage { get; init; }
    /// <summary>
    /// Per-query by design: query classes cut across intent clusters (a cluster's paraphrases can carry different
    /// classes), so there is no cluster unit to average over. Read these as per-query metrics.
    /// </summary>
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
