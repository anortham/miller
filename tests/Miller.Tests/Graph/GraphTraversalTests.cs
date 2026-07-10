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
