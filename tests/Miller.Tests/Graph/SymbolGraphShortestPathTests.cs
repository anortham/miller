using Miller.Core.Graph;
using Xunit;

namespace Miller.Tests.Graph;

/// <summary>
/// Tests for <see cref="SymbolGraph.ShortestPath"/> (plan Task 8): BFS + parent reconstruction over the forward
/// (dependency) adjacency, deterministic id-sorted tie-break consistent with <see cref="SymbolGraph.Reach"/>, null on
/// an unreachable goal / missing endpoint / depth cut-off.
/// </summary>
public sealed class SymbolGraphShortestPathTests
{
    private static GraphNode N(string id) => new(id, IsTest: false);
    private static GraphEdge E(string from, string to) => new(from, to, "calls");

    [Fact]
    public void ShortestPath_returns_ordered_ids_along_the_chain()
    {
        // a -> b -> c -> d (a linear dependency chain)
        var graph = SymbolGraph.Build(
            [N("a"), N("b"), N("c"), N("d")],
            [E("a", "b"), E("b", "c"), E("c", "d")]);

        var path = graph.ShortestPath("a", "d", maxDepth: 10);

        Assert.NotNull(path);
        Assert.Equal(["a", "b", "c", "d"], path);
    }

    [Fact]
    public void ShortestPath_picks_the_fewest_hops_when_a_shortcut_exists()
    {
        // a -> b -> c -> d AND a -> d (direct). Shortest is the 1-hop direct edge.
        var graph = SymbolGraph.Build(
            [N("a"), N("b"), N("c"), N("d")],
            [E("a", "b"), E("b", "c"), E("c", "d"), E("a", "d")]);

        var path = graph.ShortestPath("a", "d", maxDepth: 10);

        Assert.Equal(["a", "d"], path);
    }

    [Fact]
    public void ShortestPath_same_node_yields_single_node_path_even_at_zero_depth()
    {
        var graph = SymbolGraph.Build([N("a")], []);

        Assert.Equal(["a"], graph.ShortestPath("a", "a", maxDepth: 0));
        Assert.Equal(["a"], graph.ShortestPath("a", "a", maxDepth: 5));
    }

    [Fact]
    public void ShortestPath_returns_null_when_goal_is_unreachable()
    {
        // a -> b ; c isolated. No path a -> c.
        var graph = SymbolGraph.Build(
            [N("a"), N("b"), N("c")],
            [E("a", "b")]);

        Assert.Null(graph.ShortestPath("a", "c", maxDepth: 10));
    }

    [Fact]
    public void ShortestPath_respects_the_reverse_direction_of_an_edge()
    {
        // Forward adjacency is a -> b only; b does NOT reach a.
        var graph = SymbolGraph.Build([N("a"), N("b")], [E("a", "b")]);

        Assert.Equal(["a", "b"], graph.ShortestPath("a", "b", maxDepth: 5));
        Assert.Null(graph.ShortestPath("b", "a", maxDepth: 5));
    }

    [Fact]
    public void ShortestPath_returns_null_when_the_goal_is_beyond_maxDepth()
    {
        // a -> b -> c -> d needs 3 hops; a depth cap of 2 cannot reach d.
        var graph = SymbolGraph.Build(
            [N("a"), N("b"), N("c"), N("d")],
            [E("a", "b"), E("b", "c"), E("c", "d")]);

        Assert.Null(graph.ShortestPath("a", "d", maxDepth: 2));
        Assert.Equal(["a", "b", "c", "d"], graph.ShortestPath("a", "d", maxDepth: 3));
    }

    [Fact]
    public void ShortestPath_returns_null_when_an_endpoint_is_not_in_the_graph()
    {
        var graph = SymbolGraph.Build([N("a"), N("b")], [E("a", "b")]);

        Assert.Null(graph.ShortestPath("a", "zzz", maxDepth: 5)); // unknown goal
        Assert.Null(graph.ShortestPath("zzz", "b", maxDepth: 5)); // unknown start
    }

    [Fact]
    public void ShortestPath_tie_break_is_deterministic_by_neighbour_id()
    {
        // Two equal-length 2-hop paths to "z": via "m" and via "p". BFS visits neighbours of "a" in id order
        // (m before p), so the parent of "z" is set from "m" first — the path goes a -> m -> z.
        var graph = SymbolGraph.Build(
            [N("a"), N("m"), N("p"), N("z")],
            [E("a", "p"), E("a", "m"), E("m", "z"), E("p", "z")]);

        var path = graph.ShortestPath("a", "z", maxDepth: 5);

        Assert.Equal(["a", "m", "z"], path);
    }

    [Fact]
    public void ShortestPath_terminates_and_is_correct_on_a_cycle()
    {
        // a -> b -> c -> a (cycle) plus c -> d. Visited tracking must terminate; a -> d is 3 hops.
        var graph = SymbolGraph.Build(
            [N("a"), N("b"), N("c"), N("d")],
            [E("a", "b"), E("b", "c"), E("c", "a"), E("c", "d")]);

        var path = graph.ShortestPath("a", "d", maxDepth: 10);

        Assert.Equal(["a", "b", "c", "d"], path);
    }
}
