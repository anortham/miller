using RetrievalEval;
using Xunit;

namespace RetrievalEval.Tests;

public class ScorerTests
{
    static EvalQuery Positive(
        string id,
        string language,
        string queryClass,
        string[] relevant,
        string? cluster = null,
        string repo = "miller") =>
        new()
        {
            QueryId = id,
            Query = id,
            Repo = repo,
            Language = language,
            QueryClass = queryClass,
            IntentCluster = cluster,
            Relevant = [.. relevant.Select(d => new RelevantDoc { DocId = d, Grade = 3 })],
        };

    static EvalQuery Negative(string id, string language = "csharp", string repo = "miller") =>
        new()
        {
            QueryId = id,
            Query = id,
            Repo = repo,
            Language = language,
            QueryClass = "prose",
            Relevant = [],
            Negative = true,
        };

    static EvalResult Ranked(string id, params string[] docs) => new() { QueryId = id, Ranked = docs };

    [Fact]
    public void Overall_averages_per_query_metrics_over_positive_queries_only()
    {
        var queries = new[]
        {
            Positive("q1", "csharp", "identifier", ["a"]),
            Positive("q2", "csharp", "identifier", ["b"]),
            Negative("n1"),
        };
        var results = new[] { Ranked("q1", "a"), Ranked("q2", "zz"), Ranked("n1") };

        var report = Scorer.Score(queries, results, k: 10);

        Assert.Equal(2, report.Overall.QueryCount);
        Assert.Equal(0.5, report.Overall.RecallAtK, 1e-9);
        Assert.Equal(0.5, report.Overall.NdcgAtK, 1e-9);
        Assert.Equal(3, report.QueryCount);
        Assert.Equal(1, report.NegativeQueryCount);
    }

    [Fact]
    public void Search_mode_counts_record_all_frozen_query_routes_without_changing_metrics()
    {
        var auto = Positive("q1", "csharp", "identifier", ["a"]);
        var content = Positive("q2", "markdown", "docs_like", ["b"]) with
        {
            SearchMode = SearchModes.Content,
        };
        var negative = Negative("n1") with { SearchMode = SearchModes.File };
        var results = new[] { Ranked("q1", "a"), Ranked("q2", "b"), Ranked("n1") };

        var report = Scorer.Score([auto, content, negative], results, k: 10);

        Assert.Equal(1.0, report.Overall.RecallAtK, 1e-9);
        Assert.Equal(1.0, report.Overall.NdcgAtK, 1e-9);
        Assert.Equal(
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                [SearchModes.Auto] = 1,
                [SearchModes.Content] = 1,
                [SearchModes.File] = 1,
            },
            report.SearchModeCounts);
    }

    [Fact]
    public void Per_language_macro_average_weights_languages_equally_not_queries()
    {
        var queries = new[]
        {
            Positive("q1", "csharp", "identifier", ["a"]),
            Positive("q2", "csharp", "identifier", ["b"]),
            Positive("q3", "rust", "identifier", ["c", "d", "e", "f"]),
        };
        var results = new[] { Ranked("q1", "a"), Ranked("q2", "zz"), Ranked("q3", "c") };

        var report = Scorer.Score(queries, results, k: 10);

        Assert.Equal(0.5, report.PerLanguage["csharp"].RecallAtK, 1e-9);
        Assert.Equal(0.25, report.PerLanguage["rust"].RecallAtK, 1e-9);
        Assert.Equal(0.375, report.LanguageMacroAverage.RecallAtK, 1e-9);
        Assert.Equal(2, report.LanguageMacroAverage.LanguageCount);
    }

    [Fact]
    public void Worst_language_reports_the_lowest_scoring_language()
    {
        var queries = new[]
        {
            Positive("q1", "csharp", "identifier", ["a"]),
            Positive("q2", "rust", "identifier", ["c", "d", "e", "f"]),
        };
        var results = new[] { Ranked("q1", "a"), Ranked("q2", "c") };

        var report = Scorer.Score(queries, results, k: 10);

        Assert.Equal("rust", report.WorstLanguage!.Language);
        Assert.Equal(0.25, report.WorstLanguage.RecallAtK, 1e-9);
    }

    [Fact]
    public void Cluster_counts_as_hit_when_any_single_paraphrase_retrieves_a_relevant_doc()
    {
        var queries = new[]
        {
            Positive("p1", "csharp", "prose", ["a"], cluster: "promote"),
            Positive("p2", "csharp", "prose", ["a"], cluster: "promote"),
            Positive("p3", "csharp", "prose", ["a"], cluster: "promote"),
            Positive("z1", "csharp", "prose", ["b"], cluster: "leadership"),
        };
        var results = new[] { Ranked("p1", "nope"), Ranked("p2", "a"), Ranked("p3"), Ranked("z1", "nope") };

        var report = Scorer.Score(queries, results, k: 10);

        var promote = report.PerIntentCluster.Single(c => c.IntentCluster == "promote");
        Assert.True(promote.ClusterHit);
        Assert.Equal(3, promote.MemberCount);
        Assert.Equal(1.0 / 3.0, promote.MemberHitRate, 1e-9);
        Assert.Equal(1.0 / 3.0, promote.RecallAtK, 1e-9);

        Assert.False(report.PerIntentCluster.Single(c => c.IntentCluster == "leadership").ClusterHit);
        Assert.Equal(2, report.IntentClusterSummary.ClusterCount);
        Assert.Equal(0.5, report.IntentClusterSummary.ClusterHitRate, 1e-9);
    }

    [Fact]
    public void An_extra_paraphrase_scoring_like_its_cluster_does_not_shift_the_primary_metric()
    {
        var baseQueries = new[]
        {
            Positive("p1", "csharp", "prose", ["a"], cluster: "promote"),
            Positive("p2", "csharp", "prose", ["a"], cluster: "promote"),
            Positive("s1", "csharp", "identifier", ["b"]),
        };
        var baseResults = new[] { Ranked("p1", "a"), Ranked("p2", "a"), Ranked("s1", "nope") };

        var widened = baseQueries.Append(
            Positive("p3", "csharp", "prose", ["a"], cluster: "promote")).ToArray();
        var widenedResults = baseResults.Append(Ranked("p3", "a")).ToArray();

        var before = Scorer.Score(baseQueries, baseResults, k: 10);
        var after = Scorer.Score(widened, widenedResults, k: 10);

        Assert.Equal(2, before.EvaluationUnitCount);
        Assert.Equal(2, after.EvaluationUnitCount);
        Assert.Equal(0.5, before.Overall.RecallAtK, 1e-9);
        Assert.Equal(before.Overall.RecallAtK, after.Overall.RecallAtK, 1e-9);
        Assert.Equal(before.Overall.NdcgAtK, after.Overall.NdcgAtK, 1e-9);
        Assert.Equal(before.PerLanguage["csharp"].RecallAtK, after.PerLanguage["csharp"].RecallAtK, 1e-9);

        Assert.Equal(2.0 / 3.0, before.OverallPerQuery.RecallAtK, 1e-9);
        Assert.Equal(0.75, after.OverallPerQuery.RecallAtK, 1e-9);
    }

    [Fact]
    public void Duplicating_a_cluster_member_verbatim_leaves_the_primary_metric_unchanged()
    {
        var queries = new[]
        {
            Positive("p1", "csharp", "prose", ["a"], cluster: "promote"),
            Positive("p2", "csharp", "prose", ["a"], cluster: "promote"),
            Positive("z1", "rust", "prose", ["b"], cluster: "safety", repo: "julie"),
            Positive("z2", "rust", "prose", ["b"], cluster: "safety", repo: "julie"),
        };
        var results = new[] { Ranked("p1", "a"), Ranked("p2", "a"), Ranked("z1"), Ranked("z2") };

        var duplicated = queries.Append(
            Positive("p1-copy", "csharp", "prose", ["a"], cluster: "promote")).ToArray();
        var duplicatedResults = results.Append(Ranked("p1-copy", "a")).ToArray();

        var before = Scorer.Score(queries, results, k: 10);
        var after = Scorer.Score(duplicated, duplicatedResults, k: 10);

        Assert.Equal(0.5, before.Overall.RecallAtK, 1e-9);
        Assert.Equal(before.Overall.RecallAtK, after.Overall.RecallAtK, 1e-9);
        Assert.Equal(before.Overall.NdcgAtK, after.Overall.NdcgAtK, 1e-9);
        Assert.Equal("rust", after.WorstLanguage!.Language);
        Assert.Equal(before.WorstLanguage!.RecallAtK, after.WorstLanguage.RecallAtK, 1e-9);

        Assert.Equal(0.5, before.OverallPerQuery.RecallAtK, 1e-9);
        Assert.Equal(0.6, after.OverallPerQuery.RecallAtK, 1e-9);
    }

    [Fact]
    public void A_cluster_weighs_the_same_as_one_standalone_query()
    {
        var queries = new[]
        {
            Positive("p1", "csharp", "prose", ["a"], cluster: "promote"),
            Positive("p2", "csharp", "prose", ["a"], cluster: "promote"),
            Positive("p3", "csharp", "prose", ["a"], cluster: "promote"),
            Positive("s1", "csharp", "identifier", ["b"]),
        };
        var results = new[] { Ranked("p1"), Ranked("p2"), Ranked("p3"), Ranked("s1", "b") };

        var report = Scorer.Score(queries, results, k: 10);

        Assert.Equal(2, report.EvaluationUnitCount);
        Assert.Equal(0.5, report.Overall.RecallAtK, 1e-9);
        Assert.Equal(4, report.Overall.QueryCount);
        Assert.Equal(0.25, report.OverallPerQuery.RecallAtK, 1e-9);
    }

    [Fact]
    public void Cluster_max_overall_credits_a_cluster_for_its_best_phrasing()
    {
        var queries = new[]
        {
            Positive("p1", "csharp", "prose", ["a"], cluster: "promote"),
            Positive("p2", "csharp", "prose", ["a"], cluster: "promote"),
        };
        var results = new[] { Ranked("p1", "a"), Ranked("p2", "nope") };

        var report = Scorer.Score(queries, results, k: 10);

        Assert.Equal(0.5, report.Overall.RecallAtK, 1e-9);
        Assert.Equal(1.0, report.OverallClusterMax.RecallAtK, 1e-9);
        Assert.Equal(1, report.OverallClusterMax.UnitCount);
    }

    [Fact]
    public void Per_language_and_worst_language_use_cluster_units()
    {
        var queries = new[]
        {
            Positive("p1", "csharp", "prose", ["a"], cluster: "promote"),
            Positive("p2", "csharp", "prose", ["a"], cluster: "promote"),
            Positive("p3", "csharp", "prose", ["a"], cluster: "promote"),
            Positive("r1", "rust", "prose", ["b"], repo: "julie"),
        };
        var results = new[] { Ranked("p1", "a"), Ranked("p2"), Ranked("p3"), Ranked("r1") };

        var report = Scorer.Score(queries, results, k: 10);

        Assert.Equal(1, report.PerLanguage["csharp"].UnitCount);
        Assert.Equal(3, report.PerLanguage["csharp"].QueryCount);
        Assert.Equal(1.0 / 3.0, report.PerLanguage["csharp"].RecallAtK, 1e-9);
        Assert.Equal(1.0 / 3.0, report.PerLanguagePerQuery["csharp"].RecallAtK, 1e-9);
        Assert.Equal("rust", report.WorstLanguage!.Language);
        Assert.Equal(0.0, report.WorstLanguage.RecallAtK, 1e-9);
        Assert.Equal(UnitPolicies.Cluster, report.UnitPolicy);
    }

    [Fact]
    public void Negative_query_fails_when_the_arm_returns_any_confident_hit()
    {
        var queries = new[] { Negative("n1"), Negative("n2"), Negative("n3") };
        var results = new[] { Ranked("n1"), Ranked("n2", "some/file.cs"), Ranked("n3") };

        var report = Scorer.Score(queries, results, k: 10);

        Assert.Equal(3, report.Negatives.Count);
        Assert.Equal(1, report.Negatives.FalsePositiveCount);
        Assert.Equal(1.0 / 3.0, report.Negatives.FalsePositiveRate, 1e-9);
        Assert.Equal(2.0 / 3.0, report.Negatives.PassRate, 1e-9);
    }

    [Fact]
    public void Negative_hits_below_the_cutoff_are_not_false_positives()
    {
        var queries = new[] { Negative("n1") };
        var results = new[] { Ranked("n1", "a", "b", "c") };

        Assert.Equal(1, Scorer.Score(queries, results, k: 2).Negatives.FalsePositiveCount);
        Assert.Equal(0, Scorer.Score(queries, results, k: 0).Negatives.FalsePositiveCount);
    }

    [Fact]
    public void Per_query_class_breakdown_isolates_the_identifier_non_inferiority_set()
    {
        var queries = new[]
        {
            Positive("q1", "csharp", "identifier", ["a"]),
            Positive("q2", "csharp", "identifier", ["b"]),
            Positive("q3", "csharp", "prose", ["c"]),
        };
        var results = new[] { Ranked("q1", "a"), Ranked("q2", "b"), Ranked("q3", "zz") };

        var report = Scorer.Score(queries, results, k: 10);

        Assert.Equal(1.0, report.PerQueryClass["identifier"].RecallAtK, 1e-9);
        Assert.Equal(2, report.PerQueryClass["identifier"].QueryCount);
        Assert.Equal(0.0, report.PerQueryClass["prose"].RecallAtK, 1e-9);
    }

    [Fact]
    public void A_query_with_no_results_row_scores_zero_and_is_reported_as_missing()
    {
        var queries = new[] { Positive("q1", "csharp", "identifier", ["a"]) };

        var report = Scorer.Score(queries, [], k: 10);

        Assert.Equal(0.0, report.Overall.RecallAtK, 1e-9);
        Assert.Equal(new[] { "q1" }, report.MissingResults);
    }

    [Fact]
    public void Results_for_unknown_query_ids_are_reported_rather_than_scored()
    {
        var queries = new[] { Positive("q1", "csharp", "identifier", ["a"]) };
        var results = new[] { Ranked("q1", "a"), Ranked("ghost", "a") };

        var report = Scorer.Score(queries, results, k: 10);

        Assert.Equal(new[] { "ghost" }, report.UnknownResults);
        Assert.Equal(1.0, report.Overall.RecallAtK, 1e-9);
    }

    [Fact]
    public void Duplicate_query_ids_are_rejected()
    {
        var queries = new[]
        {
            Positive("q1", "csharp", "identifier", ["a"]),
            Positive("q1", "csharp", "identifier", ["b"]),
        };

        Assert.Throws<InvalidOperationException>(() => Scorer.Score(queries, [], k: 10));
    }

    [Fact]
    public void Units_block_lists_every_evaluation_unit_and_reconciles_with_overall()
    {
        var queries = new[]
        {
            Positive("solo1", "csharp", "prose", ["a"]),
            Positive("c1-a", "rust", "prose", ["b"], cluster: "shared-intent"),
            Positive("c1-b", "rust", "prose", ["b"], cluster: "shared-intent"),
            Negative("n1"),
        };
        var results = new[] { Ranked("solo1", "a"), Ranked("c1-a", "b"), Ranked("c1-b", "zz"), Ranked("n1") };

        var report = Scorer.Score(queries, results, k: 10);

        Assert.Equal(report.EvaluationUnitCount, report.Units.Count);
        Assert.Contains(report.Units, u => u.UnitId == "query:solo1" && u.QueryCount == 1);
        var cluster = Assert.Single(report.Units, u => u.UnitId == "cluster:shared-intent");
        Assert.Equal(2, cluster.QueryCount);
        Assert.Equal(0.5, cluster.NdcgAtK, 1e-9);
        Assert.Equal(report.Overall.NdcgAtK, report.Units.Average(u => u.NdcgAtK), 1e-9);
        Assert.Equal(report.Overall.RecallAtK, report.Units.Average(u => u.RecallAtK), 1e-9);
    }
}
