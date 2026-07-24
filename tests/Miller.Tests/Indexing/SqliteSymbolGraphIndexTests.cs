using Miller.Core.Graph;
using Miller.Indexing;
using System.Globalization;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class SqliteSymbolGraphIndexTests
{
    private const string FirstTargetId = "10000000000000000000000000000001";
    private const string SecondTargetId = "10000000000000000000000000000002";
    private const string CallerId = "20000000000000000000000000000001";
    private const string MissingTargetId = "30000000000000000000000000000001";

    [Fact]
    public void Reach_MatchesRepositoryGraphForInspectFixture()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        using var sqliteGraph = new SqliteSymbolGraphIndex(fx.DbPath);
        var full = RepositoryIndexLoader.Load(fx.DbPath);

        foreach (Direction direction in new[] { Direction.Forward, Direction.Reverse, Direction.Both })
        {
            var expected = full.Graph.Reach([JulieDbFixture.GetUserId], maxDepth: 2, limit: 50, direction);
            var actual = sqliteGraph.Reach([JulieDbFixture.GetUserId], maxDepth: 2, limit: 50, direction);

            Assert.Equal(
                expected.Select(static n => (n.Id, n.Hop)).ToArray(),
                actual.Select(static n => (n.Id, n.Hop)).ToArray());
        }
    }

    [Theory]
    [InlineData(Direction.Forward, 1, 1)]
    [InlineData(Direction.Reverse, 1, 1)]
    [InlineData(Direction.Both, 2, 50)]
    public void ReachWithEvidence_MatchesRepositoryGraphForInspectFixture(
        Direction direction,
        int maxDepth,
        int limit)
    {
        using var fx = JulieDbFixture.CreateForInspect();
        using var sqliteGraph = new SqliteSymbolGraphIndex(fx.DbPath);
        ISymbolGraphReachability graphInterface = sqliteGraph;
        var full = RepositoryIndexLoader.Load(fx.DbPath);

        GraphReachResult expected = full.Graph.ReachWithEvidence(
            [JulieDbFixture.GetUserId],
            maxDepth,
            limit,
            direction);
        GraphReachResult actual = graphInterface.ReachWithEvidence(
            [JulieDbFixture.GetUserId],
            maxDepth,
            limit,
            direction);

        Assert.Equal(
            expected.Nodes,
            actual.Nodes);
        Assert.Equal(expected.ReachedCount, actual.ReachedCount);
        Assert.Equal(expected.TruncatedByDepth, actual.TruncatedByDepth);
        Assert.Equal(expected.TruncatedByLimit, actual.TruncatedByLimit);
        Assert.Equal(expected.Exhausted, actual.Exhausted);
    }

    [Fact]
    public void ShortestPath_MatchesRepositoryGraphForInspectFixture()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        using var sqliteGraph = new SqliteSymbolGraphIndex(fx.DbPath);
        var full = RepositoryIndexLoader.Load(fx.DbPath);
        string findId = SqliteSymbolReader.Read(fx.DbPath).Single(static s => s.Name == "Find").SymbolId;

        Assert.Equal(
            full.Graph.ShortestPath(JulieDbFixture.GetUserId, findId, maxDepth: 2),
            sqliteGraph.ShortestPath(JulieDbFixture.GetUserId, findId, maxDepth: 2));
    }

    [Fact]
    public void Reach_ResolvedHomonym_MatchesExactFirstRepositoryGraphInBothDirections()
    {
        using var fixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new(FirstTargetId, "Run", "method", "csharp", "src/First.cs", "void Run()", 1, null),
                new(SecondTargetId, "Run", "method", "csharp", "src/Second.cs", "void Run()", 1, null),
                new(CallerId, "Caller", "method", "csharp", "src/Caller.cs", "void Caller()", 1, null),
            ],
            identifiers:
            [
                new("identifier-run", "Run", "call", "csharp", "src/Caller.cs", 10, CallerId),
            ]);
        fixture.AddIdentifierResolution("identifier-run", SecondTargetId);

        var repository = RepositoryIndexLoader.Load(fixture.DbPath);
        using var sqlite = new SqliteSymbolGraphIndex(fixture.DbPath);

        Assert.Equal(
            repository.Graph.Reach([CallerId], 1, 10, Direction.Forward).Select(node => node.Id),
            sqlite.Reach([CallerId], 1, 10, Direction.Forward).Select(node => node.Id));
        Assert.Equal(
            repository.Graph.Reach([FirstTargetId], 1, 10, Direction.Reverse).Select(node => node.Id),
            sqlite.Reach([FirstTargetId], 1, 10, Direction.Reverse).Select(node => node.Id));
        Assert.Equal(
            repository.Graph.Reach([SecondTargetId], 1, 10, Direction.Reverse).Select(node => node.Id),
            sqlite.Reach([SecondTargetId], 1, 10, Direction.Reverse).Select(node => node.Id));
    }

    [Fact]
    public void Reach_ResolvedIdentifierOutsideArtifact_MatchesRepositoryGraph()
    {
        using var fixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new(CallerId, "Caller", "method", "csharp", "src/Caller.cs", "void Caller()", 1, null),
            ],
            identifiers:
            [
                new JulieDbFixture.IdentifierRow(
                    "identifier-missing-target",
                    "Missing",
                    "call",
                    "csharp",
                    "src/Caller.cs",
                    10,
                    CallerId)
                {
                    TargetSymbolId = MissingTargetId,
                },
            ]);

        var repository = RepositoryIndexLoader.Load(fixture.DbPath);
        using var sqlite = new SqliteSymbolGraphIndex(fixture.DbPath);

        Assert.Equal(
            repository.Graph.Reach([CallerId], 1, 10, Direction.Forward).Select(node => node.Id),
            sqlite.Reach([CallerId], 1, 10, Direction.Forward).Select(node => node.Id));
    }

    [Fact]
    public void ReachWithEvidence_TestLinkageMetadata_MatchesRepositoryGraph()
    {
        const string sourceId = "52000000000000000000000000000001";
        const string testId = "52000000000000000000000000000002";
        using var fixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new(sourceId, "Execute", "method", "csharp", "src/Service.cs", "void Execute()", 1, null),
                new(testId, "ExecuteWorks", "method", "csharp", "tests/ServiceTests.cs", "void ExecuteWorks()", 1, null)
                {
                    IsTest = true,
                    Metadata = "{\"test_coverage\":{\"symbol_id\":\"" + sourceId +
                        "\",\"confidence\":0.97}}",
                },
            ]);
        MillerRepositoryIndex repository = RepositoryIndexLoader.Load(fixture.DbPath);
        using var sqlite = new SqliteSymbolGraphIndex(fixture.DbPath);

        GraphReachResult expected = repository.Graph.ReachWithEvidence(
            [sourceId], 1, 10, Direction.Reverse);
        GraphReachResult actual = sqlite.ReachWithEvidence(
            [sourceId], 1, 10, Direction.Reverse);

        Assert.Equal(
            repository.Graph.Reach([sourceId], 1, 10, Direction.Reverse),
            sqlite.Reach([sourceId], 1, 10, Direction.Reverse));
        Assert.Equal(expected.Nodes, actual.Nodes);
    }

    [Fact]
    public void ReachWithEvidence_DanglingTestLinkageTarget_MatchesRepositoryGraph()
    {
        const string testId = "52100000000000000000000000000001";
        const string missingId = "52100000000000000000000000000002";
        using var fixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new(testId, "ExecuteWorks", "method", "csharp", "tests/ServiceTests.cs", "void ExecuteWorks()", 1, null)
                {
                    IsTest = true,
                    Metadata = "{\"test_coverage\":{\"symbol_id\":\"" + missingId +
                        "\",\"confidence\":0.97}}",
                },
            ]);
        MillerRepositoryIndex repository = RepositoryIndexLoader.Load(fixture.DbPath);
        using var sqlite = new SqliteSymbolGraphIndex(fixture.DbPath);

        GraphReachResult expected = repository.Graph.ReachWithEvidence(
            [testId], 1, 10, Direction.Forward);
        GraphReachResult actual = sqlite.ReachWithEvidence(
            [testId], 1, 10, Direction.Forward);

        Assert.Equal(expected, actual);
        Assert.Empty(actual.Nodes);
    }

    [Fact]
    public void ReachWithEvidence_EqualPriorityEdgesUseDeterministicKindTieBreak()
    {
        const string fromId = "52200000000000000000000000000001";
        const string toId = "52200000000000000000000000000002";
        using var fixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new(fromId, "Caller", "method", "csharp", "src/Caller.cs", "void Caller()", 1, null),
                new(toId, "Target", "method", "csharp", "src/Target.cs", "void Target()", 1, null),
            ],
            relationships:
            [
                new("rel-uses", fromId, toId, "uses"),
                new("rel-references", fromId, toId, "references"),
            ]);
        MillerRepositoryIndex repository = RepositoryIndexLoader.Load(fixture.DbPath);
        using var sqlite = new SqliteSymbolGraphIndex(fixture.DbPath);

        ReachedNode memoryNode = Assert.Single(repository.Graph.ReachWithEvidence(
            [fromId], 1, 10, Direction.Forward).Nodes);
        ReachedNode sqliteNode = Assert.Single(sqlite.ReachWithEvidence(
            [fromId], 1, 10, Direction.Forward).Nodes);

        Assert.Equal("references", memoryNode.EdgeKind);
        Assert.Equal(memoryNode, sqliteNode);
    }

    [Fact]
    public void ReachWithEvidence_EqualPriorityEdgeKindTieBreakIsOrdinalAcrossCultures()
    {
        const string fromId = "52300000000000000000000000000001";
        const string toId = "52300000000000000000000000000002";
        using var fixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new(fromId, "Caller", "method", "csharp", "src/Caller.cs", "void Caller()", 1, null),
                new(toId, "Target", "method", "csharp", "src/Target.cs", "void Target()", 1, null),
            ],
            relationships:
            [
                new("rel-z", fromId, toId, "z_kind"),
                new("rel-a-umlaut", fromId, toId, "ä_kind"),
            ]);
        MillerRepositoryIndex repository = RepositoryIndexLoader.Load(fixture.DbPath);
        using var sqlite = new SqliteSymbolGraphIndex(fixture.DbPath);
        CultureInfo originalCulture = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            ReachedNode memoryNode = Assert.Single(repository.Graph.ReachWithEvidence(
                [fromId], 1, 10, Direction.Forward).Nodes);
            ReachedNode sqliteNode = Assert.Single(sqlite.ReachWithEvidence(
                [fromId], 1, 10, Direction.Forward).Nodes);

            Assert.Equal("z_kind", memoryNode.EdgeKind);
            Assert.Equal(memoryNode, sqliteNode);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }
}
