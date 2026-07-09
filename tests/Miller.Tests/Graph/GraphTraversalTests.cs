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
