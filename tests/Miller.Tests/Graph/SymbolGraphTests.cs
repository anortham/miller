using Miller.Core.Graph;
using Xunit;

namespace Miller.Tests.Graph;

/// <summary>
/// The pure dependency-graph engine (M5 decision D3/D4). Pins forward/reverse adjacency construction (edge
/// hygiene: unknown endpoints dropped, self-loops dropped, duplicates collapsed, neighbour lists sorted) and
/// bounded BFS reachability (depth caps, diamonds taking the minimum hop, cycle termination, unknown starts
/// skipped, the limit cap truncating deterministically, and all three directions). Every test asserts on the
/// concrete reached set / hop distances, never just "non-empty". All in-memory, instant — no I/O.
/// </summary>
public sealed class SymbolGraphTests
{
    private static GraphNode N(string id, bool isTest = false) => new(id, isTest);

    private static GraphEdge E(string from, string to, string kind = "calls") => new(from, to, kind);

    [Fact]
    public void GraphNode_CarriesVisibilityNeededForImpactPeerRanking()
    {
        Assert.Equal(
            new[] { "Id", "IsTest", "Visibility" },
            typeof(GraphNode).GetProperties().Select(static property => property.Name));
    }

    [Fact]
    public void ReachWithEvidence_PreservesStrongestShortestEdgeAndPredecessor()
    {
        var graph = SymbolGraph.Build(
            [N("seed"), N("via-a"), N("via-b"), N("target")],
            [
                new GraphEdge("via-a", "seed", "uses", 0.6, "identifier_name"),
                new GraphEdge("via-b", "seed", "calls", 1.0, "relationship"),
                new GraphEdge("target", "via-a", "uses", 0.7, "pending_resolution"),
                new GraphEdge("target", "via-b", "calls", 0.9, "relationship"),
            ]);

        ReachedNode target = Assert.Single(
            graph.ReachWithEvidence(["seed"], 2, 10, Direction.Reverse).Nodes,
            node => node.Id == "target");

        Assert.Equal("via-b", target.ReachedVia);
        Assert.Equal("calls", target.EdgeKind);
        Assert.Equal(0.9, target.EdgeConfidence);
        Assert.Equal("relationship", target.EdgeSource);
    }

    /// <summary>Project a reach result down to the (id, hop) pairs in their returned order.</summary>
    private static (string Id, int Hop)[] Pairs(IEnumerable<ReachedNode> reached)
        => reached.Select(r => (r.Id, r.Hop)).ToArray();

    private static GraphReachResult ReachWithEvidence(
        ISymbolGraphReachability graph,
        IEnumerable<string> starts,
        int maxDepth,
        int limit,
        Direction direction = Direction.Forward)
        => graph.ReachWithEvidence(starts, maxDepth, limit, direction);

    [Fact]
    public void Build_EmptyGraph_HasNoNeighboursAndEmptyReach()
    {
        var graph = SymbolGraph.Build([], []);

        Assert.Empty(graph.Dependencies("anything"));
        Assert.Empty(graph.Dependents("anything"));
        Assert.Empty(graph.Reach(["anything"], maxDepth: 2, limit: 100, Direction.Both));
    }

    [Fact]
    public void Build_SingleEdge_PopulatesForwardAndReverseAdjacency()
    {
        var graph = SymbolGraph.Build([N("A"), N("B")], [E("A", "B")]);

        Assert.Equal(["B"], graph.Dependencies("A"));
        Assert.Empty(graph.Dependencies("B"));
        Assert.Equal(["A"], graph.Dependents("B"));
        Assert.Empty(graph.Dependents("A"));
    }

    [Fact]
    public void Build_EdgeWithUnknownEndpoint_IsIgnored()
    {
        // B is not in the node set → the A->B edge must be dropped entirely.
        var graph = SymbolGraph.Build([N("A")], [E("A", "B"), E("C", "A")]);

        Assert.Empty(graph.Dependencies("A"));
        Assert.Empty(graph.Dependents("A"));
    }

    [Fact]
    public void Build_SelfLoop_IsDropped()
    {
        var graph = SymbolGraph.Build([N("A")], [E("A", "A")]);

        Assert.Empty(graph.Dependencies("A"));
        Assert.Empty(graph.Dependents("A"));
    }

    [Fact]
    public void Build_DuplicateEdges_AreDedupedPerDirection()
    {
        // Two A->B edges (different kinds) collapse to a single neighbour entry.
        var graph = SymbolGraph.Build([N("A"), N("B")], [E("A", "B", "calls"), E("A", "B", "uses")]);

        Assert.Equal(["B"], graph.Dependencies("A"));
        Assert.Equal(["A"], graph.Dependents("B"));
    }

    [Fact]
    public void Build_NeighbourLists_AreSortedById()
    {
        var graph = SymbolGraph.Build(
            [N("A"), N("B"), N("C"), N("D")],
            [E("A", "D"), E("A", "B"), E("A", "C")]);

        Assert.Equal(["B", "C", "D"], graph.Dependencies("A"));
    }

    [Fact]
    public void Reach_MaxDepthZero_ReturnsEmpty()
    {
        var graph = SymbolGraph.Build([N("A"), N("B")], [E("A", "B")]);

        Assert.Empty(graph.Reach(["A"], maxDepth: 0, limit: 100, Direction.Forward));
    }

    [Theory]
    [InlineData(0, 10, 0, true, false)]
    [InlineData(2, 0, 4, false, true)]
    [InlineData(-1, -1, 0, true, false)]
    public void ReachWithEvidence_NonPositiveBounds_ReportHonestTruncation(
        int maxDepth,
        int limit,
        int expectedReachedCount,
        bool expectedDepthTruncation,
        bool expectedLimitTruncation)
    {
        var graph = SymbolGraph.Build(
            [N("A"), N("B"), N("C"), N("D"), N("E")],
            [E("A", "B"), E("A", "C"), E("B", "D"), E("C", "E")]);

        GraphReachResult result = ReachWithEvidence(graph, ["A"], maxDepth, limit);

        Assert.Empty(result.Nodes);
        Assert.Equal(expectedReachedCount, result.ReachedCount);
        Assert.Equal(expectedDepthTruncation, result.TruncatedByDepth);
        Assert.Equal(expectedLimitTruncation, result.TruncatedByLimit);
        Assert.False(result.Exhausted);
    }

    [Fact]
    public void Reach_ExcludesTheStartsThemselves()
    {
        var graph = SymbolGraph.Build([N("A"), N("B")], [E("A", "B")]);

        var reached = graph.Reach(["A"], maxDepth: 2, limit: 100, Direction.Forward);

        Assert.DoesNotContain("A", reached.Select(r => r.Id));
        Assert.Equal([("B", 1)], Pairs(reached));
    }

    [Fact]
    public void Reach_Chain_HonoursDepthCap()
    {
        // A -> B -> C -> D. Forward from A at depth 2 reaches B(1), C(2) but not D.
        var graph = SymbolGraph.Build(
            [N("A"), N("B"), N("C"), N("D")],
            [E("A", "B"), E("B", "C"), E("C", "D")]);

        var reached = graph.Reach(["A"], maxDepth: 2, limit: 100, Direction.Forward);

        Assert.Equal([("B", 1), ("C", 2)], Pairs(reached));
    }

    [Fact]
    public void Reach_Diamond_TakesMinimumHopDistance()
    {
        // A -> B -> D and A -> C -> D, plus a direct A -> D. D's minimum hop is 1, reported once.
        var graph = SymbolGraph.Build(
            [N("A"), N("B"), N("C"), N("D")],
            [E("A", "B"), E("A", "C"), E("B", "D"), E("C", "D"), E("A", "D")]);

        var reached = graph.Reach(["A"], maxDepth: 5, limit: 100, Direction.Forward);

        // D appears exactly once, at hop 1 (the shortest path), not at hop 2.
        Assert.Equal([("B", 1), ("C", 1), ("D", 1)], Pairs(reached));
    }

    [Fact]
    public void Reach_Cycle_Terminates()
    {
        // A -> B -> A. From A, B is reached at hop 1; A (a start) is never re-emitted; no infinite loop.
        var graph = SymbolGraph.Build([N("A"), N("B")], [E("A", "B"), E("B", "A")]);

        var reached = graph.Reach(["A"], maxDepth: 100, limit: 100, Direction.Forward);

        Assert.Equal([("B", 1)], Pairs(reached));
    }

    [Fact]
    public void Reach_UnknownStart_IsSkipped()
    {
        var graph = SymbolGraph.Build([N("A"), N("B")], [E("A", "B")]);

        // "Z" is not in the graph; "A" is. Only A's reachable set comes back.
        var reached = graph.Reach(["Z", "A"], maxDepth: 2, limit: 100, Direction.Forward);

        Assert.Equal([("B", 1)], Pairs(reached));
    }

    [Fact]
    public void Reach_LimitCap_TruncatesDeterministically()
    {
        // A -> B, C, D, E (all hop 1). With limit 2, the first two by (hop, id) order survive: B, C.
        var graph = SymbolGraph.Build(
            [N("A"), N("B"), N("C"), N("D"), N("E")],
            [E("A", "B"), E("A", "C"), E("A", "D"), E("A", "E")]);

        var reached = graph.Reach(["A"], maxDepth: 1, limit: 2, Direction.Forward);

        Assert.Equal([("B", 1), ("C", 1)], Pairs(reached));
    }

    [Fact]
    public void Reach_OrdersByHopThenId()
    {
        // A -> C (hop 1), A -> B (hop 1), B -> Z (hop 2). Order: B(1), C(1), Z(2).
        var graph = SymbolGraph.Build(
            [N("A"), N("B"), N("C"), N("Z")],
            [E("A", "C"), E("A", "B"), E("B", "Z")]);

        var reached = graph.Reach(["A"], maxDepth: 2, limit: 100, Direction.Forward);

        Assert.Equal([("B", 1), ("C", 1), ("Z", 2)], Pairs(reached));
    }

    [Fact]
    public void Reach_Reverse_WalksDependents()
    {
        // A -> B -> C. Reverse from C reaches B(1), A(2) — the callers.
        var graph = SymbolGraph.Build(
            [N("A"), N("B"), N("C")],
            [E("A", "B"), E("B", "C")]);

        var reached = graph.Reach(["C"], maxDepth: 2, limit: 100, Direction.Reverse);

        Assert.Equal([("B", 1), ("A", 2)], Pairs(reached));
    }

    [Fact]
    public void Reach_Forward_DiffersFromReverse()
    {
        // A -> B -> C. Forward from B reaches only C; reverse from B reaches only A.
        var graph = SymbolGraph.Build(
            [N("A"), N("B"), N("C")],
            [E("A", "B"), E("B", "C")]);

        Assert.Equal([("C", 1)], Pairs(graph.Reach(["B"], 2, 100, Direction.Forward)));
        Assert.Equal([("A", 1)], Pairs(graph.Reach(["B"], 2, 100, Direction.Reverse)));
    }

    [Fact]
    public void Reach_Both_UnionsForwardAndReverse()
    {
        // A -> B -> C. From B, Both reaches A (reverse, hop 1) and C (forward, hop 1).
        var graph = SymbolGraph.Build(
            [N("A"), N("B"), N("C")],
            [E("A", "B"), E("B", "C")]);

        var reached = graph.Reach(["B"], maxDepth: 1, limit: 100, Direction.Both);

        Assert.Equal([("A", 1), ("C", 1)], Pairs(reached));
    }

    [Fact]
    public void Reach_MultipleStarts_ReportsMinimumHopAcrossSeeds()
    {
        // X -> Y -> T and S -> T. Starts {X, S}: T is hop 1 from S and hop 2 from X → reported at hop 1.
        var graph = SymbolGraph.Build(
            [N("X"), N("Y"), N("T"), N("S")],
            [E("X", "Y"), E("Y", "T"), E("S", "T")]);

        var reached = graph.Reach(["X", "S"], maxDepth: 5, limit: 100, Direction.Forward);

        // Starts X and S are excluded; Y(1 from X), T(1 from S).
        Assert.Equal([("T", 1), ("Y", 1)], Pairs(reached));
    }

    [Fact]
    public void Reach_StartThatIsAlsoReachable_StaysExcluded()
    {
        // A -> B, B -> A (cycle), starts {A}. B is reached; A is a start so never emitted even though B->A.
        var graph = SymbolGraph.Build([N("A"), N("B")], [E("A", "B"), E("B", "A")]);

        var reached = graph.Reach(["A"], maxDepth: 5, limit: 100, Direction.Both);

        Assert.Equal([("B", 1)], Pairs(reached));
    }

    [Fact]
    public void ReachWithEvidence_EmptyGraph_IsExhausted()
    {
        ISymbolGraphReachability graph = SymbolGraph.Build([], []);

        GraphReachResult result = ReachWithEvidence(graph, ["missing"], maxDepth: 2, limit: 100);

        Assert.Empty(result.Nodes);
        Assert.Equal(0, result.ReachedCount);
        Assert.False(result.TruncatedByDepth);
        Assert.False(result.TruncatedByLimit);
        Assert.True(result.Exhausted);
    }

    [Fact]
    public void ReachWithEvidence_ExhaustedChain_ReportsEveryReachedNode()
    {
        ISymbolGraphReachability graph = SymbolGraph.Build(
            [N("A"), N("B"), N("C")],
            [E("A", "B"), E("B", "C")]);

        GraphReachResult result = ReachWithEvidence(graph, ["A"], maxDepth: 2, limit: 100);

        Assert.Equal([("B", 1), ("C", 2)], Pairs(result.Nodes));
        Assert.Equal(2, result.ReachedCount);
        Assert.False(result.TruncatedByDepth);
        Assert.False(result.TruncatedByLimit);
        Assert.True(result.Exhausted);
    }

    [Fact]
    public void ReachWithEvidence_DepthBoundaryWithUnseenNeighbour_ReportsDepthTruncation()
    {
        ISymbolGraphReachability graph = SymbolGraph.Build(
            [N("A"), N("B"), N("C"), N("D")],
            [E("A", "B"), E("B", "C"), E("C", "D")]);

        GraphReachResult result = ReachWithEvidence(graph, ["A"], maxDepth: 2, limit: 100);

        Assert.Equal([("B", 1), ("C", 2)], Pairs(result.Nodes));
        Assert.Equal(2, result.ReachedCount);
        Assert.True(result.TruncatedByDepth);
        Assert.False(result.TruncatedByLimit);
        Assert.False(result.Exhausted);
    }

    [Fact]
    public void ReachWithEvidence_LimitBoundary_ReportsPreLimitCountAndDeterministicPrefix()
    {
        ISymbolGraphReachability graph = SymbolGraph.Build(
            [N("A"), N("B"), N("C"), N("D")],
            [E("A", "D"), E("A", "B"), E("A", "C")]);

        GraphReachResult result = ReachWithEvidence(graph, ["A"], maxDepth: 2, limit: 2);

        Assert.Equal([("B", 1), ("C", 1)], Pairs(result.Nodes));
        Assert.Equal(3, result.ReachedCount);
        Assert.False(result.TruncatedByDepth);
        Assert.True(result.TruncatedByLimit);
        Assert.False(result.Exhausted);
    }

    [Fact]
    public void ReachWithEvidence_ReachedCountExactlyLimit_IsNotTruncated()
    {
        ISymbolGraphReachability graph = SymbolGraph.Build(
            [N("A"), N("B"), N("C"), N("D")],
            [E("A", "B"), E("A", "C"), E("A", "D")]);

        GraphReachResult result = ReachWithEvidence(graph, ["A"], maxDepth: 2, limit: 3);

        // Every reached node is returned, so `reached.Length > limit` must stay false at the boundary.
        Assert.Equal([("B", 1), ("C", 1), ("D", 1)], Pairs(result.Nodes));
        Assert.Equal(3, result.ReachedCount);
        Assert.False(result.TruncatedByDepth);
        Assert.False(result.TruncatedByLimit);
        Assert.True(result.Exhausted);
    }

    [Fact]
    public void ReachWithEvidence_DepthAndLimitBoundaries_ReportIndependentTruncation()
    {
        ISymbolGraphReachability graph = SymbolGraph.Build(
            [N("A"), N("B"), N("C"), N("D"), N("E")],
            [E("A", "B"), E("A", "E"), E("B", "C"), E("C", "D")]);

        GraphReachResult result = ReachWithEvidence(graph, ["A"], maxDepth: 2, limit: 2);

        Assert.Equal([("B", 1), ("E", 1)], Pairs(result.Nodes));
        Assert.Equal(3, result.ReachedCount);
        Assert.True(result.TruncatedByDepth);
        Assert.True(result.TruncatedByLimit);
        Assert.False(result.Exhausted);
    }

    [Fact]
    public void ReachWithEvidence_CycleAtDepthBoundary_HasNoUnseenNeighbour()
    {
        ISymbolGraphReachability graph = SymbolGraph.Build(
            [N("A"), N("B")],
            [E("A", "B"), E("B", "A")]);

        GraphReachResult result = ReachWithEvidence(graph, ["A"], maxDepth: 1, limit: 100);

        Assert.Equal([("B", 1)], Pairs(result.Nodes));
        Assert.Equal(1, result.ReachedCount);
        Assert.False(result.TruncatedByDepth);
        Assert.True(result.Exhausted);
    }

    [Fact]
    public void ReachWithEvidence_Diamond_ReportsMinimumHopAndNoFalseDepthTruncation()
    {
        ISymbolGraphReachability graph = SymbolGraph.Build(
            [N("A"), N("B"), N("C"), N("D")],
            [E("A", "B"), E("A", "C"), E("B", "D"), E("C", "D")]);

        GraphReachResult result = ReachWithEvidence(graph, ["A"], maxDepth: 2, limit: 100);

        Assert.Equal([("B", 1), ("C", 1), ("D", 2)], Pairs(result.Nodes));
        Assert.Equal(3, result.ReachedCount);
        Assert.False(result.TruncatedByDepth);
        Assert.True(result.Exhausted);
    }

    [Fact]
    public void ReachWithEvidence_UnknownStartIsSkipped_AndReachRemainsCompatible()
    {
        ISymbolGraphReachability graph = SymbolGraph.Build(
            [N("A"), N("B")],
            [E("A", "B")]);

        GraphReachResult result = ReachWithEvidence(graph, ["missing", "A"], maxDepth: 2, limit: 100);

        Assert.Equal([("B", 1)], Pairs(result.Nodes));
        Assert.Equal(Pairs(result.Nodes), Pairs(graph.Reach(["missing", "A"], 2, 100, Direction.Forward)));
        Assert.True(result.Exhausted);
    }

    [Fact]
    public void IsTest_ReflectsTheNodeFlag()
    {
        var graph = SymbolGraph.Build([N("Prod", isTest: false), N("Spec", isTest: true)], []);

        Assert.False(graph.IsTest("Prod"));
        Assert.True(graph.IsTest("Spec"));
        // Unknown ids are not tests (default false), never throw.
        Assert.False(graph.IsTest("Ghost"));
    }
}
