using System.Text.Json;
using RetrievalEval;
using Xunit;

namespace RetrievalEval.Tests;

public class EndToEndTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("retrieval-eval-e2e").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Score_writes_a_report_whose_every_rollup_matches_the_hand_computed_values()
    {
        var queries = Write("queries.jsonl", string.Join('\n',
            """{"query_id":"c1","query":"a","intent_cluster":"cluster-a","query_class":"prose","repo":"r","language":"csharp","relevant":[{"doc_id":"A.cs","grade":3}],"negative":false}""",
            """{"query_id":"c2","query":"b","intent_cluster":"cluster-a","query_class":"prose","repo":"r","language":"csharp","relevant":[{"doc_id":"A.cs","grade":3}],"negative":false}""",
            """{"query_id":"i1","query":"Sym","query_class":"identifier","repo":"r","language":"rust","relevant":[{"doc_id":"b.rs","grade":3},{"doc_id":"c.rs","grade":1}],"negative":false}""",
            """{"query_id":"n1","query":"unrelated","query_class":"prose","repo":"r","language":"rust","relevant":[],"negative":true}"""));

        var results = Write("results.jsonl", string.Join('\n',
            """{"query_id":"c1","ranked":["Z.cs","Y.cs"]}""",
            """{"query_id":"c2","ranked":["A.cs"]}""",
            """{"query_id":"i1","ranked":["x.rs","b.rs"]}""",
            """{"query_id":"n1","ranked":[]}"""));

        var outPath = Path.Combine(_dir, "report.json");
        Assert.Equal(0, Program.Main(["score", "--queries", queries, "--results", results, "--out", outPath, "--k", "10"]));

        var report = JsonSerializer.Deserialize<EvalReport>(File.ReadAllText(outPath), Jsonl.Options)!;

        Assert.Equal(10, report.K);
        Assert.Equal(4, report.QueryCount);
        Assert.Equal(3, report.PositiveQueryCount);
        Assert.Equal(1, report.NegativeQueryCount);

        Assert.Equal(0.5, report.PerLanguage["csharp"].NdcgAtK, 1e-12);
        Assert.Equal(0.5, report.PerLanguage["csharp"].RecallAtK, 1e-12);

        var rustNdcg = (7.0 / Math.Log2(3.0)) / (7.0 / 1.0 + 1.0 / Math.Log2(3.0));
        Assert.Equal(0.5, report.PerLanguage["rust"].RecallAtK, 1e-12);
        Assert.Equal(rustNdcg, report.PerLanguage["rust"].NdcgAtK, 1e-12);

        Assert.Equal(0.5, report.LanguageMacroAverage.RecallAtK, 1e-12);
        Assert.Equal((0.5 + rustNdcg) / 2.0, report.LanguageMacroAverage.NdcgAtK, 1e-12);
        Assert.Equal("csharp", report.WorstLanguage!.Language);

        Assert.Equal(UnitPolicies.Cluster, report.UnitPolicy);
        Assert.Equal(2, report.EvaluationUnitCount);
        Assert.Equal(0.5, report.Overall.RecallAtK, 1e-12);
        Assert.Equal((0.5 + rustNdcg) / 2.0, report.Overall.NdcgAtK, 1e-12);

        Assert.Equal((rustNdcg + 0.0 + 1.0) / 3.0, report.OverallPerQuery.NdcgAtK, 1e-12);
        Assert.Equal(3, report.OverallPerQuery.UnitCount);
        Assert.Equal((1.0 + rustNdcg) / 2.0, report.OverallClusterMax.NdcgAtK, 1e-12);

        var cluster = Assert.Single(report.PerIntentCluster);
        Assert.True(cluster.ClusterHit);
        Assert.Equal(0.5, cluster.MemberHitRate, 1e-12);

        Assert.Equal(rustNdcg, report.PerQueryClass["identifier"].NdcgAtK, 1e-12);
        Assert.Equal(1, report.PerQueryClass["identifier"].QueryCount);
        Assert.Equal(0, report.Negatives.FalsePositiveCount);
        Assert.Equal(1.0, report.Negatives.PassRate, 1e-12);
        Assert.Empty(report.MissingResults);
        Assert.Empty(report.UnknownResults);
    }

    [Fact]
    public void Score_reports_a_negative_false_positive_and_flags_a_missing_results_row()
    {
        var queries = Write("q.jsonl", string.Join('\n',
            """{"query_id":"p1","query":"a","query_class":"prose","repo":"r","language":"csharp","relevant":[{"doc_id":"A.cs","grade":2}],"negative":false}""",
            """{"query_id":"n1","query":"unrelated","query_class":"prose","repo":"r","language":"csharp","relevant":[],"negative":true}"""));
        var results = Write("r.jsonl", """{"query_id":"n1","ranked":["A.cs"]}""");
        var outPath = Path.Combine(_dir, "report.json");

        Assert.Equal(0, Program.Main(["score", "--queries", queries, "--results", results, "--out", outPath]));
        var report = JsonSerializer.Deserialize<EvalReport>(File.ReadAllText(outPath), Jsonl.Options)!;

        Assert.Equal(1, report.Negatives.FalsePositiveCount);
        Assert.Equal(0.0, report.Negatives.PassRate, 1e-12);
        Assert.Equal(new[] { "p1" }, report.MissingResults);
        Assert.Equal(0.0, report.Overall.RecallAtK, 1e-12);
    }

    [Fact]
    public void Score_requires_the_queries_results_and_out_arguments()
    {
        Assert.Equal(1, Program.Main(["score", "--queries", "nope.jsonl"]));
        Assert.Equal(1, Program.Main(["bogus-verb"]));
    }
}
