using Microsoft.Data.Sqlite;
using Miller.Core.Graph;
using Miller.Indexing;
using Miller.Indexing.Reads;
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

    [Fact]
    public void Reach_QueryTelemetryReportsFixedSqlFamilies()
    {
        const string sourceId = "40100000000000000000000000000001";
        const string targetId = "40100000000000000000000000000002";
        using var fixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new(sourceId, "Source", "method", "csharp", "src/Source.cs", "void Source()", 1, null),
                new(targetId, "Target", "method", "csharp", "src/Target.cs", "void Target()", 1, null),
            ],
            relationships: [new("source-target", sourceId, targetId, "calls")]);
        using var sqlite = new SqliteSymbolGraphIndex(fixture.DbPath);
        ISymbolGraphReachability graph = sqlite;

        IReadOnlyList<ReachedNode> reached = graph.Reach([sourceId], 1, 10, Direction.Forward);
        GraphQueryTelemetrySnapshot telemetry = sqlite.QueryTelemetry;

        Assert.Equal(targetId, Assert.Single(reached).Id);
        Assert.Equal(new GraphQueryFamilyTelemetry(1, 1, telemetry.SymbolExists.Elapsed), telemetry.SymbolExists);
        Assert.Equal(new GraphQueryFamilyTelemetry(0, 0, telemetry.RelationshipsForward.Elapsed), telemetry.RelationshipsForward);
        Assert.Equal(new GraphQueryFamilyTelemetry(0, 0, telemetry.PendingForward.Elapsed), telemetry.PendingForward);
        Assert.Equal(new GraphQueryFamilyTelemetry(0, 0, telemetry.IdentifiersForward.Elapsed), telemetry.IdentifiersForward);
        Assert.Equal(new GraphQueryFamilyTelemetry(1, 1, telemetry.FrontierBatch.Elapsed), telemetry.FrontierBatch);
        Assert.Equal(1, telemetry.SupplementalEdges.Executions);
        Assert.Equal(0, telemetry.SupplementalEdges.Rows);
        Assert.Equal(0, telemetry.RelationshipsReverse.Executions);
        Assert.Equal(0, telemetry.PendingReverse.Executions);
        Assert.Equal(0, telemetry.IdentifiersReverse.Executions);
        Assert.Equal(0, telemetry.UnresolvedIdentifiersReverse.Executions);
        Assert.Equal(0, telemetry.ResolveName.Executions);
        Assert.True(telemetry.TotalElapsed > TimeSpan.Zero);
    }

    [Fact]
    public void Reach_StatementObserverReportsOnlyCompletedFixedPhasesBeforeCancellation()
    {
        const string sourceId = "40400000000000000000000000000001";
        const string targetId = "40400000000000000000000000000002";
        using var fixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new(sourceId, "Source", "method", "csharp", "src/Source.cs", "void Source()", 1, null),
                new(targetId, "Target", "method", "csharp", "src/Target.cs", "void Target()", 1, null),
            ],
            relationships: [new("source-target", sourceId, targetId, "calls")]);
        using var sqlite = new SqliteSymbolGraphIndex(fixture.DbPath);
        var observations = new List<GraphStatementObservation>();
        sqlite.StatementObserver = observation =>
        {
            observations.Add(observation);
            if (observation.Phase == GraphStatementPhase.UnresolvedNameForward)
                throw new OperationCanceledException("stop after the completed forward name statement");
        };

        Assert.Throws<OperationCanceledException>(() =>
            sqlite.Reach([sourceId], 1, 10, Direction.Both));

        Assert.Equal(
            [
                GraphStatementPhase.RelationshipForward,
                GraphStatementPhase.RelationshipReverse,
                GraphStatementPhase.FamilyResolution,
                GraphStatementPhase.UnresolvedNameForward,
            ],
            observations.Select(static observation => observation.Phase));
        Assert.All(observations, static observation => Assert.True(observation.Elapsed >= TimeSpan.Zero));
        Assert.DoesNotContain(
            observations,
            static observation => observation.Phase is GraphStatementPhase.Supplemental
                or GraphStatementPhase.Completion);
    }

    [Fact]
    public void Reach_StatementObserverCapsImmutableCandidateSampleInOrdinalInputOrder()
    {
        string[] ids = Enumerable.Range(0, 12)
            .Select(index => $"405{index:00000000000000000000000000000}")
            .ToArray();
        using var fixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            ids.Select((id, index) => new JulieDbFixture.SymbolRow(
                id,
                $"Candidate{index}",
                "method",
                "csharp",
                $"src/Candidate{index}.cs",
                $"void Candidate{index}()",
                1,
                null)).ToArray());
        using var sqlite = new SqliteSymbolGraphIndex(fixture.DbPath);
        var observations = new List<GraphStatementObservation>();
        sqlite.StatementObserver = observation =>
        {
            observations.Add(observation);
            if (observation.Phase == GraphStatementPhase.UnresolvedNameForward)
                throw new OperationCanceledException("stop after the completed forward name statement");
        };

        Assert.Throws<OperationCanceledException>(() =>
            sqlite.Reach(ids, 1, 20, Direction.Forward));

        Assert.Equal(
            [
                GraphStatementPhase.RelationshipForward,
                GraphStatementPhase.FamilyResolution,
                GraphStatementPhase.UnresolvedNameForward,
            ],
            observations.Select(static observation => observation.Phase));
        Assert.All(observations, observation =>
        {
            Assert.Equal(12, observation.CandidateCount);
            Assert.Equal(ids.Take(8), observation.CandidateSample);
            Assert.Equal(8, observation.CandidateSample.Length);
            Assert.True(observation.CandidateSample.IsDefaultOrEmpty is false);
        });
    }

    [Fact]
    public void Reach_HighFrontierUsesBoundedSqlBatches()
    {
        const string rootId = "40200000000000000000000000000001";
        const string leafId = "40200000000000000000000000000002";
        JulieDbFixture.SymbolRow[] frontier = Enumerable.Range(0, 200)
            .Select(index => new JulieDbFixture.SymbolRow(
                $"403{index:00000000000000000000000000000}",
                $"Frontier{index}",
                "method",
                "csharp",
                $"src/Frontier{index}.cs",
                $"void Frontier{index}()",
                1,
                null))
            .ToArray();
        JulieDbFixture.RelationshipRow[] relationships =
        [
            .. frontier.Select((symbol, index) =>
                new JulieDbFixture.RelationshipRow($"root-frontier-{index}", rootId, symbol.Id, "calls")),
            .. frontier.Select((symbol, index) =>
                new JulieDbFixture.RelationshipRow($"frontier-leaf-{index}", symbol.Id, leafId, "calls")),
        ];
        using var fixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new(rootId, "Root", "method", "csharp", "src/Root.cs", "void Root()", 1, null),
                new(leafId, "Leaf", "method", "csharp", "src/Leaf.cs", "void Leaf()", 1, null),
                .. frontier,
            ],
            relationships: relationships);
        using var sqlite = new SqliteSymbolGraphIndex(fixture.DbPath);
        ISymbolGraphReachability graph = sqlite;

        IReadOnlyList<ReachedNode> result = graph.Reach([rootId], 2, 500, Direction.Forward);
        GraphQueryTelemetrySnapshot telemetry = sqlite.QueryTelemetry;

        Assert.Equal(201, result.Count);
        Assert.Equal(200, result.Count(node => node.Hop == 1));
        Assert.Equal(new ReachedNode(leafId, 2), result.Single(node => node.Hop == 2));
        Assert.True(telemetry.TotalExecutions <= 10, $"SQL executions: {telemetry.TotalExecutions}");
        Assert.Equal(2, telemetry.FrontierBatch.Executions);
        Assert.Equal(400, telemetry.FrontierBatch.Rows);
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
    public void ShortestPathWithEvidence_MatchesRepositoryGraphForInspectFixture()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        using var sqliteGraph = new SqliteSymbolGraphIndex(fx.DbPath);
        var full = RepositoryIndexLoader.Load(fx.DbPath);
        string findId = SqliteSymbolReader.Read(fx.DbPath).Single(static s => s.Name == "Find").SymbolId;

        GraphPath expected = Assert.IsType<GraphPath>(full.Graph.ShortestPathWithEvidence(
            JulieDbFixture.GetUserId,
            findId,
            maxDepth: 2,
            static _ => true));
        GraphPath actual = Assert.IsType<GraphPath>(sqliteGraph.ShortestPathWithEvidence(
            JulieDbFixture.GetUserId,
            findId,
            maxDepth: 2,
            static _ => true));

        Assert.Equal(expected.Nodes, actual.Nodes);
        Assert.Equal(expected.Edges, actual.Edges);
    }

    [Fact]
    public void ReachWithEvidence_MatchesRepositoryGraphForBlazorComponentEdge()
    {
        const string pageId = "41000000000000000000000000000001";
        const string widgetId = "41000000000000000000000000000002";
        using var fixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new JulieDbFixture.SymbolRow(
                    pageId, "Page", "class", "razor", "Pages/Page.razor",
                    "public partial class Page", 1, null)
                {
                    Metadata = """{"type":"razor-component","qualifiedName":"Pages.Page"}""",
                },
                new JulieDbFixture.SymbolRow(
                    widgetId, "Widget", "class", "razor", "Shared/Widget.razor",
                    "public partial class Widget", 1, null)
                {
                    Metadata = """{"type":"razor-component","qualifiedName":"Shared.Widget"}""",
                },
            ]);
        fixture.AddStructuralFact(
            "blazor-reference",
            null,
            "Pages/Page.razor",
            language: "razor",
            patternId: BridgeStructuralPatterns.BlazorComponentReference,
            captureName: "component_reference",
            nodeKind: "markup_element");
        fixture.ExecuteWrite(
            """
            UPDATE structural_facts
            SET metadata_json =
                '{"tag":"Widget","containing_component":"Page","namespace_context":["Shared"],"generic_arguments":[]}'
            WHERE structural_fact_id = 'blazor-reference';
            """);

        MillerRepositoryIndex repository = RepositoryIndexLoader.Load(fixture.DbPath);
        using var sqlite = new SqliteSymbolGraphIndex(fixture.DbPath);
        GraphReachResult expected =
            repository.Graph.ReachWithEvidence([widgetId], 1, 10, Direction.Reverse);
        GraphReachResult actual =
            sqlite.ReachWithEvidence([widgetId], 1, 10, Direction.Reverse);

        Assert.Equal(expected.Nodes, actual.Nodes);
        Assert.Equal(expected.ReachedCount, actual.ReachedCount);
        Assert.Equal(expected.TruncatedByDepth, actual.TruncatedByDepth);
        Assert.Equal(expected.TruncatedByLimit, actual.TruncatedByLimit);
    }

    [Fact]
    public void SessionBackedReachUsesOnePinnedReadAndDoesNotOwnTheSession()
    {
        const string pageId = "41100000000000000000000000000001";
        const string widgetId = "41100000000000000000000000000002";
        using var fixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new JulieDbFixture.SymbolRow(
                    pageId, "Page", "class", "razor", "Pages/Page.razor",
                    "public partial class Page", 1, null)
                {
                    Metadata = """{"type":"razor-component","qualifiedName":"Pages.Page"}""",
                },
                new JulieDbFixture.SymbolRow(
                    widgetId, "Widget", "class", "razor", "Shared/Widget.razor",
                    "public partial class Widget", 1, null)
                {
                    Metadata = """{"type":"razor-component","qualifiedName":"Shared.Widget"}""",
                },
            ]);
        fixture.AddStructuralFact(
            "blazor-session-reference",
            null,
            "Pages/Page.razor",
            language: "razor",
            patternId: BridgeStructuralPatterns.BlazorComponentReference,
            captureName: "component_reference",
            nodeKind: "markup_element");
        fixture.ExecuteWrite(
            """
            UPDATE structural_facts
            SET metadata_json =
                '{"tag":"Widget","containing_component":"Page","namespace_context":["Shared"],"generic_arguments":[]}'
            WHERE structural_fact_id = 'blazor-session-reference';
            """);
        using var session = new NonReentrantReadSession(fixture.DbPath);
        var sqlite = new SqliteSymbolGraphIndex(session);

        GraphReachResult actual = sqlite.ReachWithEvidence([widgetId], 1, 10, Direction.Reverse);
        sqlite.Dispose();

        Assert.Equal(pageId, Assert.Single(actual.Nodes).Id);
        Assert.Equal(1, session.ReadCount);
        Assert.False(session.IsDisposed);
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
    public void ReachWithEvidence_NullOverlayConfidenceFallsBackToIdentifierConfidence()
    {
        using var fixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new(FirstTargetId, "Run", "method", "csharp", "src/Target.cs", "void Run()", 1, null),
                new(CallerId, "Caller", "method", "csharp", "src/Caller.cs", "void Caller()", 1, null),
            ],
            identifiers:
            [
                new("identifier-run-null-confidence", "Run", "call", "csharp", "src/Caller.cs", 10, CallerId),
            ]);
        fixture.AddIdentifierResolution("identifier-run-null-confidence", FirstTargetId);
        fixture.ExecuteWrite(
            """
            UPDATE identifier_resolutions
            SET confidence = NULL
            WHERE identifier_id = 'identifier-run-null-confidence';
            """);

        MillerRepositoryIndex repository = RepositoryIndexLoader.Load(fixture.DbPath);
        using var sqlite = new SqliteSymbolGraphIndex(fixture.DbPath);

        Assert.Equal(
            repository.Graph.ReachWithEvidence([FirstTargetId], 1, 10, Direction.Reverse).Nodes,
            sqlite.ReachWithEvidence([FirstTargetId], 1, 10, Direction.Reverse).Nodes);
    }

    [Fact]
    public void ReachWithEvidence_DirectTargetWithOverlayUsesOverlayConfidenceInBothDirections()
    {
        using var fixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new(FirstTargetId, "Run", "method", "csharp", "src/Target.cs", "void Run()", 1, null),
                new(CallerId, "Caller", "method", "csharp", "src/Caller.cs", "void Caller()", 1, null),
            ],
            identifiers:
            [
                new("identifier-run-dual", "Run", "call", "csharp", "src/Caller.cs", 10, CallerId)
                {
                    TargetSymbolId = FirstTargetId,
                },
            ]);
        fixture.AddIdentifierResolution("identifier-run-dual", FirstTargetId, confidence: 0.25);

        MillerRepositoryIndex repository = RepositoryIndexLoader.Load(fixture.DbPath);
        using var sqlite = new SqliteSymbolGraphIndex(fixture.DbPath);

        foreach (Direction direction in new[] { Direction.Forward, Direction.Reverse })
        {
            string seed = direction == Direction.Forward ? CallerId : FirstTargetId;
            Assert.Equal(
                repository.Graph.ReachWithEvidence([seed], 1, 10, direction).Nodes,
                sqlite.ReachWithEvidence([seed], 1, 10, direction).Nodes);
        }
    }

    [Fact]
    public void ReachWithEvidence_WideFrontierCompletesAcrossSqlBatches()
    {
        const string seedId = "46000000000000000000000000000001";
        JulieDbFixture.SymbolRow[] callers = Enumerable.Range(0, 1201)
            .Select(index => new JulieDbFixture.SymbolRow(
                $"46{index + 2:000000000000000000000000000000}",
                $"Caller{index}",
                "method",
                "csharp",
                $"src/Caller{index}.cs",
                $"void Caller{index}()",
                1,
                null))
            .ToArray();
        JulieDbFixture.RelationshipRow[] relationships = callers
            .Select((caller, index) => new JulieDbFixture.RelationshipRow(
                $"relationship-{index}",
                caller.Id,
                seedId,
                "calls"))
            .ToArray();
        using var fixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new(seedId, "Seed", "method", "csharp", "src/Seed.cs", "void Seed()", 1, null),
                .. callers,
            ],
            relationships: relationships);
        using var sqlite = new SqliteSymbolGraphIndex(fixture.DbPath);

        GraphReachResult result =
            sqlite.ReachWithEvidence([seedId], 1, 2000, Direction.Reverse);

        Assert.Equal(1201, result.ReachedCount);
        Assert.Equal(1201, result.Nodes.Count);
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

    // FIX 1 (2026-08-21 context-latency diagnosis): no store julie-extract has ever written carries a
    // test_linkage/test_coverage metadata key, so the whole-index metadata scan must not run at all.
    [Fact]
    public void TestLinkage_TestMetadataWithoutLinkageKeys_SkipsTheMetadataScan()
    {
        const string testId = "52400000000000000000000000000001";
        using var fixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new(testId, "ExecuteWorks", "method", "csharp", "tests/ServiceTests.cs", "void ExecuteWorks()", 1, null)
                {
                    IsTest = true,
                    Metadata = "{\"is_test\":true,\"test_lifecycle\":\"fact\"}",
                },
            ]);
        using SqliteConnection connection = OpenReadOnly(fixture.DbPath);

        TestLinkageReadResult result = TestLinkageReader.ReadWithProbe(connection);

        Assert.False(result.Scanned);
        Assert.Empty(result.Edges);
    }

    [Fact]
    public void TestLinkage_TestMetadataWithLinkageKey_ScansAndProducesTheEdge()
    {
        const string sourceId = "52500000000000000000000000000001";
        const string testId = "52500000000000000000000000000002";
        using var fixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new(sourceId, "Execute", "method", "csharp", "src/Service.cs", "void Execute()", 1, null),
                new(testId, "ExecuteWorks", "method", "csharp", "tests/ServiceTests.cs", "void ExecuteWorks()", 1, null)
                {
                    IsTest = true,
                    Metadata = "{\"test_coverage\":{\"symbol_id\":\"" + sourceId + "\",\"confidence\":0.97}}",
                },
            ]);
        using SqliteConnection connection = OpenReadOnly(fixture.DbPath);

        TestLinkageReadResult result = TestLinkageReader.ReadWithProbe(connection);

        Assert.True(result.Scanned);
        GraphEdge edge = Assert.Single(result.Edges);
        Assert.Equal(testId, edge.From);
        Assert.Equal(sourceId, edge.To);
    }

    // FIX 7: the supplemental edge endpoints were probed one `SELECT 1 FROM symbols` at a time against a
    // per-instance cache that always started empty. Three linkage edges cost three probes plus the start probe;
    // one batched prime makes it two statements regardless of the edge count.
    [Fact]
    public void Reach_SupplementalEdgeEndpointsResolveInOneBatchedProbe()
    {
        const string sourceId = "52600000000000000000000000000001";
        string[] testIds =
        [
            "52600000000000000000000000000002",
            "52600000000000000000000000000003",
            "52600000000000000000000000000004",
        ];
        var symbols = new List<JulieDbFixture.SymbolRow>
        {
            new(sourceId, "Execute", "method", "csharp", "src/Service.cs", "void Execute()", 1, null),
        };
        foreach (string testId in testIds)
        {
            symbols.Add(new(
                testId,
                "ExecuteWorks" + testId[^1],
                "method",
                "csharp",
                "tests/ServiceTests.cs",
                "void ExecuteWorks()",
                1,
                null)
            {
                IsTest = true,
                Metadata = "{\"test_coverage\":{\"symbol_id\":\"" + sourceId + "\",\"confidence\":0.97}}",
            });
        }

        using var fixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            symbols);
        using var sqlite = new SqliteSymbolGraphIndex(fixture.DbPath);

        IReadOnlyList<ReachedNode> reached = sqlite.Reach([sourceId], 1, 10, Direction.Reverse);
        GraphQueryTelemetrySnapshot telemetry = sqlite.QueryTelemetry;

        Assert.Equal(testIds, reached.Select(static node => node.Id).OrderBy(static id => id, StringComparer.Ordinal));
        Assert.Equal(3, telemetry.SupplementalEdges.Rows);
        Assert.Equal(2, telemetry.SymbolExists.Executions);
    }

    // REVIEW 2026-08-21 (finding 1): the probe matches RAW metadata text, `AppendEdges` matches the UNESCAPED
    // property name, so a JSON-escaped key spelling is the one way the two can disagree — and it would make the
    // gate fail CLOSED, silently dropping every edge. An escaped spelling must carry a backslash, so the probe
    // checks the parsed document for exactly those rows.
    [Fact]
    public void TestLinkage_EscapedLinkageKeyName_StillScansAndProducesTheEdge()
    {
        const string sourceId = "52700000000000000000000000000001";
        const string testId = "52700000000000000000000000000002";
        // "test_\u0063overage": the same property name JsonElement.TryGetProperty reports, spelled so that no
        // raw-text match for "test_coverage" can find it.
        const string escapedKeyMetadata =
            "{\"test_\\u0063overage\":{\"symbol_id\":\"" + sourceId + "\",\"confidence\":0.97}}";
        using var fixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new(sourceId, "Execute", "method", "csharp", "src/Service.cs", "void Execute()", 1, null),
                new(testId, "ExecuteWorks", "method", "csharp", "tests/ServiceTests.cs", "void ExecuteWorks()", 1, null)
                {
                    IsTest = true,
                    Metadata = escapedKeyMetadata,
                },
            ]);
        using SqliteConnection connection = OpenReadOnly(fixture.DbPath);

        Assert.True(TestLinkageReader.HasLinkageMetadata(connection));
        TestLinkageReadResult result = TestLinkageReader.ReadWithProbe(connection);

        Assert.True(result.Scanned);
        GraphEdge edge = Assert.Single(result.Edges);
        Assert.Equal(testId, edge.From);
        Assert.Equal(sourceId, edge.To);
    }

    // REVIEW 2026-08-21 (finding 1): a backslash that is NOT part of a linkage key must still not open the gate.
    // The parsed check runs only for rows the cheap text prefilter cannot rule out; it must not report a key the
    // parser would not find.
    [Fact]
    public void TestLinkage_EscapedMetadataWithoutLinkageKeys_SkipsTheMetadataScan()
    {
        const string testId = "52800000000000000000000000000001";
        using var fixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new(testId, "ExecuteWorks", "method", "csharp", "tests/ServiceTests.cs", "void ExecuteWorks()", 1, null)
                {
                    IsTest = true,
                    Metadata = "{\"is_test\":true,\"display\":\"C:\\\\src\\\\test_coverage\"}",
                },
            ]);
        using SqliteConnection connection = OpenReadOnly(fixture.DbPath);

        TestLinkageReadResult result = TestLinkageReader.ReadWithProbe(connection);

        Assert.False(result.Scanned);
        Assert.Empty(result.Edges);
    }

    // REVIEW 2026-08-21 (finding 3): the batched endpoint prime must not run for a query that touches no
    // supplemental edge. It used to run on every graph load, so `trace path` and dependents queries paid for
    // endpoints they never asked about — work proportional to the index, not the question.
    [Fact]
    public void Reach_FrontierTouchesNoSupplementalEdge_RunsNoEndpointPrime()
    {
        const string sourceId = "52900000000000000000000000000001";
        const string testId = "52900000000000000000000000000002";
        const string unrelatedId = "52900000000000000000000000000003";
        using var fixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new(sourceId, "Execute", "method", "csharp", "src/Service.cs", "void Execute()", 1, null),
                new(testId, "ExecuteWorks", "method", "csharp", "tests/ServiceTests.cs", "void ExecuteWorks()", 1, null)
                {
                    IsTest = true,
                    Metadata = "{\"test_coverage\":{\"symbol_id\":\"" + sourceId + "\",\"confidence\":0.97}}",
                },
                new(unrelatedId, "Unrelated", "method", "csharp", "src/Other.cs", "void Unrelated()", 1, null),
            ]);
        using var sqlite = new SqliteSymbolGraphIndex(fixture.DbPath);

        IReadOnlyList<ReachedNode> reached = sqlite.Reach([unrelatedId], 1, 10, Direction.Reverse);
        GraphQueryTelemetrySnapshot telemetry = sqlite.QueryTelemetry;

        Assert.Empty(reached);
        Assert.Equal(1, telemetry.SupplementalEdges.Rows);
        // One point lookup for the start id, and nothing else: the prime never ran.
        Assert.Equal(1, telemetry.SymbolExists.Executions);
    }

    // REVIEW 2026-08-21 (finding 4): fix 2 dropped the linkage ORDER BY outright instead of re-sorting in
    // memory, on the argument that SQLite's row order cannot reach the output. Pin that argument: the same
    // linkage rows written in the opposite order produce identical evidence.
    [Fact]
    public void ReachWithEvidence_LinkageRowOrderDoesNotChangeTheOutput()
    {
        const string sourceId = "53000000000000000000000000000001";
        const string firstTestId = "53000000000000000000000000000002";
        const string secondTestId = "53000000000000000000000000000003";

        static JulieDbFixture.SymbolRow TestRow(string id, string name, string coveredId) =>
            new(id, name, "method", "csharp", "tests/ServiceTests.cs", "void " + name + "()", 1, null)
            {
                IsTest = true,
                Metadata = "{\"test_coverage\":{\"symbol_id\":\"" + coveredId + "\",\"confidence\":0.97}}",
            };

        JulieDbFixture.SymbolRow source =
            new(sourceId, "Execute", "method", "csharp", "src/Service.cs", "void Execute()", 1, null);
        JulieDbFixture.SymbolRow first = TestRow(firstTestId, "ExecuteWorksFirst", sourceId);
        JulieDbFixture.SymbolRow second = TestRow(secondTestId, "ExecuteWorksSecond", sourceId);

        using var forward = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [source, first, second]);
        using var reversed = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [source, second, first]);
        using var forwardGraph = new SqliteSymbolGraphIndex(forward.DbPath);
        using var reversedGraph = new SqliteSymbolGraphIndex(reversed.DbPath);

        GraphReachResult forwardResult = forwardGraph.ReachWithEvidence([sourceId], 1, 10, Direction.Reverse);
        GraphReachResult reversedResult = reversedGraph.ReachWithEvidence([sourceId], 1, 10, Direction.Reverse);

        Assert.Equal(2, forwardResult.Nodes.Count);
        Assert.Equal(forwardResult.Nodes, reversedResult.Nodes);
        Assert.Equal(forwardResult.ReachedCount, reversedResult.ReachedCount);
        Assert.Equal(forwardResult.TruncatedByDepth, reversedResult.TruncatedByDepth);
        Assert.Equal(forwardResult.TruncatedByLimit, reversedResult.TruncatedByLimit);
    }

    private static SqliteConnection OpenReadOnly(string dbPath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
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

    private sealed class NonReentrantReadSession : IWorkspaceReadSession
    {
        private readonly SqliteConnection _connection;
        private bool _active;

        public NonReentrantReadSession(string dbPath)
        {
            using LegacyArtifactReadSession snapshotSource = LegacyArtifactReadSession.Open(dbPath);
            Snapshot = snapshotSource.Snapshot;
            _connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            _connection.Open();
        }

        public WorkspaceReadSnapshot Snapshot { get; }

        public int ReadCount { get; private set; }

        public bool IsDisposed { get; private set; }

        public TResult Read<TResult>(Func<SqliteConnection, TResult> query)
        {
            if (_active)
                throw new InvalidOperationException("nested read");
            _active = true;
            ReadCount++;
            try
            {
                return query(_connection);
            }
            finally
            {
                _active = false;
            }
        }

        public void Dispose()
        {
            IsDisposed = true;
            _connection.Dispose();
        }
    }
}
