using Miller.Core.Graph;
using Miller.Core.References;
using Xunit;

namespace Miller.Tests.Graph;

public sealed class GraphPathCertaintyTests
{
    [Fact]
    public void PathCertainty_PropagatesMinCertaintyAcrossHops()
    {
        // Graph structure:
        // A -> B (relationship = Exact)
        // B -> C (identifier_name = Heuristic)
        // C -> D (relationship = Exact)
        // Expected:
        // Reached B: Hop 1, Certainty = Exact
        // Reached C: Hop 2, Certainty = Heuristic (min(Exact, Heuristic))
        // Reached D: Hop 3, Certainty = Heuristic (min(Exact, Heuristic, Exact) = Heuristic)

        var neighbours = new Dictionary<string, IReadOnlyList<GraphNeighbour>>(StringComparer.Ordinal)
        {
            ["A"] = [new GraphNeighbour("B", "calls", 1.0, "relationship", 0, null)],
            ["B"] = [new GraphNeighbour("C", "calls", 0.8, "identifier_name", 0, null)],
            ["C"] = [new GraphNeighbour("D", "calls", 1.0, "relationship", 0, null)],
            ["D"] = [],
        };

        GraphReachResult result = GraphTraversal.ReachWithEvidence(
            starts: ["A"],
            maxDepth: 3,
            limit: 10,
            direction: Direction.Forward,
            contains: id => neighbours.ContainsKey(id),
            neighbours: (id, _) => neighbours.GetValueOrDefault(id, []));

        Assert.Equal(3, result.Nodes.Count);

        ReachedNode b = Assert.Single(result.Nodes, n => n.Id == "B");
        Assert.Equal(1, b.Hop);
        Assert.Equal(ReferenceResolutionStatus.Exact, b.PathCertainty);

        ReachedNode c = Assert.Single(result.Nodes, n => n.Id == "C");
        Assert.Equal(2, c.Hop);
        Assert.Equal(ReferenceResolutionStatus.Heuristic, c.PathCertainty);

        ReachedNode d = Assert.Single(result.Nodes, n => n.Id == "D");
        Assert.Equal(3, d.Hop);
        Assert.Equal(ReferenceResolutionStatus.Heuristic, d.PathCertainty);
    }

    [Fact]
    public void BetterEvidence_PrefersExactPathOverHeuristicPathAtSameHop()
    {
        // Start: S
        // Path 1: S -> M1 (relationship, Exact) -> Target (relationship, Exact) => Hop 2, Certainty = Exact
        // Path 2: S -> M2 (identifier_name, Heuristic) -> Target (relationship, Exact) => Hop 2, Certainty = Heuristic
        // BetterEvidence should choose the all-exact path for Target!

        var neighbours = new Dictionary<string, IReadOnlyList<GraphNeighbour>>(StringComparer.Ordinal)
        {
            // M2 appears first to test that M1 replaces it via BetterEvidence
            ["S"] =
            [
                new GraphNeighbour("M2", "calls", 1.0, "identifier_name", 0, null),
                new GraphNeighbour("M1", "calls", 1.0, "relationship", 0, null)
            ],
            ["M2"] = [new GraphNeighbour("Target", "calls", 1.0, "relationship", 0, null)],
            ["M1"] = [new GraphNeighbour("Target", "calls", 1.0, "relationship", 0, null)],
            ["Target"] = [],
        };

        GraphReachResult result = GraphTraversal.ReachWithEvidence(
            starts: ["S"],
            maxDepth: 2,
            limit: 10,
            direction: Direction.Forward,
            contains: id => neighbours.ContainsKey(id),
            neighbours: (id, _) => neighbours.GetValueOrDefault(id, []));

        ReachedNode target = Assert.Single(result.Nodes, n => n.Id == "Target");
        Assert.Equal(2, target.Hop);
        Assert.Equal(ReferenceResolutionStatus.Exact, target.PathCertainty);
        Assert.Equal("M1", target.ReachedVia);
    }

    [Fact]
    public void AmbiguousEdge_YieldsAmbiguousPathCertainty()
    {
        var neighbours = new Dictionary<string, IReadOnlyList<GraphNeighbour>>(StringComparer.Ordinal)
        {
            ["A"] = [new GraphNeighbour("B", "calls", 0.5, "ambiguous_fallback", 0, null)],
            ["B"] = [],
        };

        GraphReachResult result = GraphTraversal.ReachWithEvidence(
            starts: ["A"],
            maxDepth: 1,
            limit: 10,
            direction: Direction.Forward,
            contains: id => neighbours.ContainsKey(id),
            neighbours: (id, _) => neighbours.GetValueOrDefault(id, []));

        ReachedNode b = Assert.Single(result.Nodes, n => n.Id == "B");
        Assert.Equal(ReferenceResolutionStatus.Ambiguous, b.PathCertainty);
    }
}
