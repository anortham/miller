using System.Text.Json;
using Xunit;

namespace FusionArm.Tests;

public sealed class FusionArmTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "fusion-arm-tests", Guid.NewGuid().ToString("N"));
    readonly string _lexicalDir;
    readonly string _semanticDir;

    public FusionArmTests()
    {
        _lexicalDir = Path.Combine(_root, "lexical");
        _semanticDir = Path.Combine(_root, "semantic");
        Directory.CreateDirectory(_lexicalDir);
        Directory.CreateDirectory(_semanticDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void IdentifierQuery_RoutesLexicalPassthrough_IgnoringSemantic()
    {
        var config = new FusionConfig(ConceptualRatio: 1.0, RankConstant: 60, ForcedHybrid: false);
        var lexical = new[] { Row("A", "docA", 5.0), Row("B", "docB", 4.0) };
        var semantic = new[] { Semantic("C", "docC", 1.0, rank: 1) };

        FusionPlan plan = Fuser.Plan("getUserName", lexical, config);
        IReadOnlyList<string> ranked = Fuser.Apply(plan, lexical, semantic, config);

        Assert.Equal(FusionMode.LexicalPassthrough, plan.Mode);
        Assert.Equal(new[] { "docA", "docB" }, ranked);
    }

    [Theory]
    [InlineData(1.0, new[] { "docB", "docA", "docC" })]
    [InlineData(3.0, new[] { "docB", "docC", "docA" })]
    public void ProseQuery_Fuses_AndConceptualRatioReordersPredictably(double ratio, string[] expected)
    {
        var config = new FusionConfig(ConceptualRatio: ratio, RankConstant: 60, ForcedHybrid: false);
        var lexical = new[] { Row("A", "docA", 5.0), Row("B", "docB", 4.1) };
        var semantic = new[] { Semantic("B", "docB", 1.0, rank: 1), Semantic("C", "docC", 1.0, rank: 2) };

        FusionPlan plan = Fuser.Plan("how to parse", lexical, config);
        IReadOnlyList<string> ranked = Fuser.Apply(plan, lexical, semantic, config);

        Assert.Equal(FusionMode.Fuse, plan.Mode);
        Assert.Equal(expected, ranked);
    }

    [Fact]
    public void DocCollapse_HappensAfterFusion_DedupingByBestFusedRank()
    {
        var config = new FusionConfig(ConceptualRatio: 5.0, RankConstant: 60, ForcedHybrid: true);
        var lexical = new[] { Row("A", "docX", 5.0), Row("B", "docX", 4.0), Row("C", "docY", 3.0) };
        var semantic = new[] { Semantic("C", "docY", 1.0, rank: 1) };

        FusionPlan plan = Fuser.Plan("anything", lexical, config);
        IReadOnlyList<string> ranked = Fuser.Apply(plan, lexical, semantic, config);

        Assert.Equal(new[] { "docY", "docX" }, ranked);
    }

    [Fact]
    public void ForcedHybrid_BypassesRoute_ForIdentifierQuery()
    {
        var lexical = new[] { Row("A", "docA", 5.0) };
        var semantic = new[] { Semantic("B", "docB", 1.0, rank: 2) };

        var honored = new FusionConfig(ConceptualRatio: 1.0, RankConstant: 60, ForcedHybrid: false);
        FusionPlan honoredPlan = Fuser.Plan("getUserName", lexical, honored);
        IReadOnlyList<string> honoredRanked = Fuser.Apply(honoredPlan, lexical, semantic, honored);

        var forced = new FusionConfig(ConceptualRatio: 1.0, RankConstant: 60, ForcedHybrid: true);
        FusionPlan forcedPlan = Fuser.Plan("getUserName", lexical, forced);
        IReadOnlyList<string> forcedRanked = Fuser.Apply(forcedPlan, lexical, semantic, forced);

        Assert.Equal(FusionMode.LexicalPassthrough, honoredPlan.Mode);
        Assert.DoesNotContain("docB", honoredRanked);

        Assert.Equal(FusionMode.Fuse, forcedPlan.Mode);
        Assert.Equal(new[] { "docA", "docB" }, forcedRanked);
    }

    [Fact]
    public void OneLexicalHit_RemainsFirstWhileForcedHybridExpands()
    {
        var lexical = new[] { Row("A", "docA", 5.0) };
        var semantic = new[] { Semantic("B", "docB", 1.0, rank: 1) };
        var config = new FusionConfig(ConceptualRatio: 5.0, RankConstant: 60, ForcedHybrid: true);

        FusionPlan plan = Fuser.Plan("getUserName", lexical, config);
        IReadOnlyList<string> ranked = Fuser.Apply(plan, lexical, semantic, config);

        Assert.Equal(new[] { "docA", "docB" }, ranked);
    }

    [Theory]
    [InlineData("VectorSidecar TryOpen")]
    [InlineData("release process")]
    [InlineData("how does the workspace refresh converge")]
    public void DecisiveMultiHit_ExcludesSemanticOnlyRowsForEveryHybridClass(string query)
    {
        var lexical = new[] { Row("A", "docA", 10.0), Row("B", "docB", 2.0) };
        var semantic = new[] { Semantic("C", "docC", 1.0, rank: 1) };
        var config = new FusionConfig(ConceptualRatio: 5.0, RankConstant: 60, ForcedHybrid: false);

        FusionPlan plan = Fuser.Plan(query, lexical, config);
        IReadOnlyList<string> ranked = Fuser.Apply(plan, lexical, semantic, config);

        Assert.Equal(new[] { "docA", "docB" }, ranked);
    }

    [Fact]
    public void MissingInputFile_EmitsNoRow_AndIsCountedInSummary()
    {
        WriteArm(_lexicalDir, "q1", Row("A", "docA", 5.0));
        WriteArm(_semanticDir, "q1", Semantic("B", "docB", 1.0, rank: 1));
        string queriesPath = WriteQuerySet(
            ("q1", "how to parse the tree"),
            ("q2", "how to embed a document chunk"));
        string outPath = Path.Combine(_root, "results.jsonl");

        FusionRunSummary summary = FusionRunner.Run(new FusionRunOptions(
            queriesPath, _lexicalDir, _semanticDir, outPath,
            new FusionConfig(ConceptualRatio: 1.0, RankConstant: 60, ForcedHybrid: false)));

        Assert.Equal(2, summary.QueryCount);
        Assert.Equal(1, summary.EmittedCount);
        Assert.Equal(1, summary.MissingCount);
        Assert.Equal(new[] { "q2" }, summary.MissingQueryIds);

        List<FusedResultRow> emitted = ReadResults(outPath);
        Assert.Single(emitted);
        Assert.Equal("q1", emitted[0].QueryId);
        Assert.Equal(2, emitted[0].PolicyVersion);
        Assert.Equal(2, JsonDocument.Parse(File.ReadAllLines(outPath)[0]).RootElement
            .GetProperty("policy_version").GetInt32());
    }

    [Fact]
    public void Run_IsDeterministic_ByteIdenticalAcrossRuns()
    {
        WriteArm(_lexicalDir, "q1", Row("A", "docA", 5.0), Row("B", "docB", 4.0));
        WriteArm(_semanticDir, "q1", Semantic("B", "docB", 1.0, rank: 1), Semantic("C", "docC", 1.0, rank: 2));
        WriteArm(_lexicalDir, "q2", Row("D", "docD", 9.0));
        WriteArm(_semanticDir, "q2", Semantic("E", "docE", 1.0, rank: 1));
        string queriesPath = WriteQuerySet(
            ("q1", "how to parse the tree"),
            ("q2", "getUserName"));
        var options = new FusionRunOptions(
            queriesPath, _lexicalDir, _semanticDir, Path.Combine(_root, "run.jsonl"),
            new FusionConfig(ConceptualRatio: 2.0, RankConstant: 60, ForcedHybrid: false));

        string firstPath = Path.Combine(_root, "first.jsonl");
        string secondPath = Path.Combine(_root, "second.jsonl");
        FusionRunner.Run(options with { OutPath = firstPath });
        FusionRunner.Run(options with { OutPath = secondPath });

        Assert.Equal(File.ReadAllBytes(firstPath), File.ReadAllBytes(secondPath));
    }

    static ArmInputRow Row(string symbolId, string docId, double score) =>
        new() { SymbolId = symbolId, DocId = docId, Score = score };

    static ArmInputRow Semantic(string symbolId, string docId, double score, int rank) =>
        new() { SymbolId = symbolId, DocId = docId, Score = score, Rank = rank };

    void WriteArm(string dir, string queryId, params ArmInputRow[] rows) =>
        File.WriteAllText(Path.Combine(dir, queryId + ".json"), JsonSerializer.Serialize(rows));

    string WriteQuerySet(params (string Id, string Query)[] queries)
    {
        string path = Path.Combine(_root, "queries.jsonl");
        IEnumerable<string> lines = queries.Select(q =>
            JsonSerializer.Serialize(new QueryRow { QueryId = q.Id, Query = q.Query }));
        File.WriteAllText(path, string.Join('\n', lines) + '\n');
        return path;
    }

    static List<FusedResultRow> ReadResults(string path) =>
        File.ReadAllLines(path)
            .Where(line => line.Trim().Length > 0)
            .Select(line => JsonSerializer.Deserialize<FusedResultRow>(line)!)
            .ToList();
}
