using Miller.Core.Graph;
using Xunit;

namespace Miller.Tests.Graph;

public sealed class GraphTraversalTests
{
    [Fact]
    public void Reach_UsesSuppliedExistenceAndNeighbourLookup()
    {
        var known = new HashSet<string>(["a", "b", "c", "d"], StringComparer.Ordinal);
        var forward = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["a"] = ["b", "c"],
            ["b"] = ["d"],
            ["c"] = ["d"],
        };

        GraphReachResult result = GraphTraversal.ReachWithEvidence(
            ["missing", "a"],
            maxDepth: 2,
            limit: 10,
            Direction.Forward,
            known.Contains,
            (id, direction) => direction == Direction.Forward && forward.TryGetValue(id, out string[]? neighbours)
                ? neighbours
                : []);

        Assert.Equal(
            [new ReachedNode("b", 1), new ReachedNode("c", 1), new ReachedNode("d", 2)],
            result.Nodes);
        Assert.Equal(3, result.ReachedCount);
        Assert.False(result.TruncatedByDepth);
        Assert.False(result.TruncatedByLimit);
        Assert.True(result.Exhausted);
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
        var known = new HashSet<string>(["a", "b", "c", "d", "e"], StringComparer.Ordinal);

        GraphReachResult result = GraphTraversal.ReachWithEvidence(
            ["a"], maxDepth, limit, Direction.Forward, known.Contains, RecordingNeighbours([]));

        Assert.Empty(result.Nodes);
        Assert.Equal(expectedReachedCount, result.ReachedCount);
        Assert.Equal(expectedDepthTruncation, result.TruncatedByDepth);
        Assert.Equal(expectedLimitTruncation, result.TruncatedByLimit);
        Assert.False(result.Exhausted);
    }

    // a -> b -> d
    //   \-> c -> e     : with maxDepth 1, both b and c sit on the frontier and both hide an unseen neighbour.
    private static Func<string, Direction, IEnumerable<string>> RecordingNeighbours(List<string> calls)
    {
        var forward = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["a"] = ["b", "c"],
            ["b"] = ["d"],
            ["c"] = ["e"],
        };
        return (id, direction) =>
        {
            calls.Add(id);
            return direction == Direction.Forward && forward.TryGetValue(id, out string[]? neighbours)
                ? neighbours
                : [];
        };
    }

    private static Func<string, Direction, IEnumerable<GraphNeighbour>> RecordingEvidenceNeighbours(
        List<string> calls)
    {
        Func<string, Direction, IEnumerable<string>> neighbours = RecordingNeighbours(calls);
        return (id, direction) => neighbours(id, direction)
            .Select(static neighbour => new GraphNeighbour(
                neighbour, "calls", 1.0, "test", 0, null));
    }

    [Fact]
    public void Reach_AtMaxDepth_DoesNotProbeFrontierNeighbours()
    {
        var known = new HashSet<string>(["a", "b", "c", "d", "e"], StringComparer.Ordinal);
        var calls = new List<string>();

        IReadOnlyList<ReachedNode> nodes = GraphTraversal.Reach(
            ["a"], maxDepth: 1, limit: 10, Direction.Forward, known.Contains, RecordingNeighbours(calls));

        Assert.Equal([new ReachedNode("b", 1), new ReachedNode("c", 1)], nodes);

        // Only the expansion of "a". The frontier nodes b/c are never asked for their neighbours, because
        // Reach discards TruncatedByDepth — probing them would be pure cost.
        Assert.Equal(["a"], calls);
    }

    [Fact]
    public void ReachWithEvidence_StopsProbingOnceDepthTruncationIsKnown()
    {
        var known = new HashSet<string>(["a", "b", "c", "d", "e"], StringComparer.Ordinal);
        var calls = new List<string>();

        GraphReachResult result = GraphTraversal.ReachWithEvidence(
            ["a"], maxDepth: 1, limit: 10, Direction.Forward, known.Contains, RecordingNeighbours(calls));

        Assert.Equal([new ReachedNode("b", 1), new ReachedNode("c", 1)], result.Nodes);
        Assert.True(result.TruncatedByDepth);

        // "a" expands; "b" is probed and proves truncation. "c" is on the same frontier and also hides an
        // unseen neighbour, but the flag is monotonic, so it is never probed.
        Assert.Equal(["a", "b"], calls);
    }

    [Fact]
    public void ReachWithEvidence_EvidenceStopsProbingOnceDepthTruncationIsKnown()
    {
        var known = new HashSet<string>(["a", "b", "c", "d", "e"], StringComparer.Ordinal);
        var calls = new List<string>();

        GraphReachResult result = GraphTraversal.ReachWithEvidence(
            ["a"], maxDepth: 1, limit: 10, Direction.Forward, known.Contains,
            RecordingEvidenceNeighbours(calls));

        Assert.Equal(["b", "c"], result.Nodes.Select(static node => node.Id));
        Assert.True(result.TruncatedByDepth);
        Assert.Equal(["a", "b"], calls);
    }

    [Fact]
    public void ReachWithEvidence_UsesOneBatchNeighbourLookupPerDepth()
    {
        var known = new HashSet<string>(["a", "b", "c", "d", "e"], StringComparer.Ordinal);
        var batches = new List<string[]>();
        var forward = new Dictionary<string, GraphNeighbour[]>(StringComparer.Ordinal)
        {
            ["a"] = [new("b", "calls", 1.0, "test", 0, null), new("c", "calls", 1.0, "test", 0, null)],
            ["b"] = [new("d", "calls", 1.0, "test", 0, null)],
            ["c"] = [new("e", "calls", 1.0, "test", 0, null)],
        };

        GraphReachResult result = GraphTraversal.ReachWithEvidence(
            ["a"],
            maxDepth: 2,
            limit: 10,
            Direction.Forward,
            known.Contains,
            static (_, _) => throw new InvalidOperationException("scalar lookup should not run"),
            batchNeighbours: (ids, _) =>
            {
                batches.Add(ids.ToArray());
                return ids.ToDictionary(
                    static id => id,
                    id => (IReadOnlyList<GraphNeighbour>)forward.GetValueOrDefault(id, []),
                    StringComparer.Ordinal);
            },
            hasUnseenNeighbours: static (_, _, _) => false);

        Assert.Equal(["b", "c", "d", "e"], result.Nodes.Select(static node => node.Id));
        Assert.Equal([["a"], ["b", "c"]], batches);
    }

    [Fact]
    public void ShortestPathWithEvidenceBatched_UsesOneBatchNeighbourLookupPerDepth()
    {
        var known = new HashSet<string>(["a", "b", "c", "d", "e"], StringComparer.Ordinal);
        var batches = new List<string[]>();
        var forward = new Dictionary<string, GraphNeighbour[]>(StringComparer.Ordinal)
        {
            ["a"] = [new("b", "calls", 1.0, "test", 0, null), new("c", "calls", 1.0, "test", 0, null)],
            ["b"] =
            [
                new("e", "type_usage", 1.0, "test", 0, null),
                new("d", "calls", 1.0, "test", 0, null),
            ],
            ["c"] = [new("e", "calls", 1.0, "test", 0, null)],
        };

        GraphPath path = Assert.IsType<GraphPath>(GraphTraversal.ShortestPathWithEvidenceBatched(
            from: "a",
            to: "e",
            maxDepth: 2,
            contains: known.Contains,
            batchDependencies: ids =>
            {
                batches.Add(ids.ToArray());
                return ids.ToDictionary(
                    static id => id,
                    id => (IReadOnlyList<GraphNeighbour>)forward.GetValueOrDefault(id, []),
                    StringComparer.Ordinal);
            },
            edgeFilter: static edge => edge.EdgeKind == "calls"));

        Assert.Equal(["a", "c", "e"], path.Nodes);
        Assert.Equal(
            [
                new GraphPathEdge("a", "c", "calls", 1.0, "test"),
                new GraphPathEdge("c", "e", "calls", 1.0, "test"),
            ],
            path.Edges);
        Assert.Equal([["a"], ["b", "c"]], batches);
    }

    [Fact]
    public void ReachWithEvidence_PreliminaryWindowDoesNotUseLateHydratedRankSignals()
    {
        var known = new HashSet<string>(StringComparer.Ordinal) { "seed" };
        var neighbours = Enumerable.Range(0, 501)
            .Select(index => new GraphNeighbour(
                $"node-{index:000}",
                "calls",
                1.0,
                "relationship",
                index == 500 ? 100 : 0,
                index == 500 ? "public" : "private"))
            .ToArray();
        known.UnionWith(neighbours.Select(static neighbour => neighbour.Id));

        GraphReachResult result = GraphTraversal.ReachWithEvidence(
            ["seed"],
            maxDepth: 1,
            limit: 500,
            Direction.Forward,
            known.Contains,
            (id, _) => id == "seed" ? neighbours : []);

        Assert.DoesNotContain(result.Nodes, node => node.Id == "node-500");
    }

    [Fact]
    public void ReachAndReachWithEvidence_AgreeOnTheReachedNodes()
    {
        var known = new HashSet<string>(["a", "b", "c", "d", "e"], StringComparer.Ordinal);

        IReadOnlyList<ReachedNode> nodes = GraphTraversal.Reach(
            ["a"], maxDepth: 2, limit: 10, Direction.Forward, known.Contains, RecordingNeighbours([]));
        GraphReachResult evidence = GraphTraversal.ReachWithEvidence(
            ["a"], maxDepth: 2, limit: 10, Direction.Forward, known.Contains, RecordingNeighbours([]));

        Assert.Equal(evidence.Nodes, nodes);
        Assert.Equal(4, evidence.ReachedCount);
        Assert.True(evidence.Exhausted);
    }

    [Fact]
    public void ShortestPath_UsesSuppliedForwardNeighbours()
    {
        var known = new HashSet<string>(["a", "b", "c", "d"], StringComparer.Ordinal);
        var forward = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["a"] = ["b", "c"],
            ["b"] = ["d"],
            ["c"] = ["d"],
        };

        IReadOnlyList<string>? path = GraphTraversal.ShortestPath(
            "a",
            "d",
            maxDepth: 2,
            known.Contains,
            id => forward.TryGetValue(id, out string[]? neighbours) ? neighbours : []);

        Assert.Equal(["a", "b", "d"], path);
    }
}
