using Miller.Core.Contracts;
using Miller.Core.Graph;
using Miller.Core.Resolver;
using Xunit;

namespace Miller.Tests.Graph;

/// <summary>
/// Tests for <see cref="BridgeGraph"/> (plan Task 8): immutable undirected adjacency over <see cref="ScoredEdge"/>s, a
/// deterministic BFS <see cref="BridgeGraph.Walk"/> with reach-order, depth-bounded, de-duplicated edges, and node
/// connectivity across a multi-leg chain. Built from synthetic scored edges so the graph is tested in isolation from
/// the legs/scorer.
/// </summary>
public sealed class BridgeGraphTests
{
    // A symbol-backed edge endpoint (its node id IS the symbol id).
    private static EdgeRef Sym(string symbolId, string display) =>
        new(display, symbolId, $"{display}.cs", new NameResolution(ResolutionStatus.Resolved, symbolId, 1));

    // A non-symbol edge endpoint (its node id is synthesized from kind + display).
    private static EdgeRef NonSym(string display) =>
        new(display, SymbolId: null, FilePath: null, new NameResolution(ResolutionStatus.Resolved, null, 1));

    private static ScoredEdge Edge(BridgeKind kind, EdgeRef source, EdgeRef target, double score = 0.95)
    {
        var candidate = new CandidateEdge(
            kind, source, target,
            Evidence: [],
            Signals: [new StructuralSignal(StructuralRuleFor(kind), Present: true)]);
        return new ScoredEdge(candidate, score, ConfidenceBand.High, IsMultiSignal: false, HasAmbiguousName: false, IsVerbUnknown: false);
    }

    private static SignalRule StructuralRuleFor(BridgeKind kind) => kind switch
    {
        BridgeKind.MapsTo => SignalRule.CreateMap,
        BridgeKind.StoredIn => SignalRule.DbSetProperty,
        BridgeKind.Hits => SignalRule.RouteVerbMatch,
        BridgeKind.NavigatesTo => SignalRule.RouteReferenceMatch,
        BridgeKind.Responds => SignalRule.ReturnTypeDto,
        BridgeKind.Consumes => SignalRule.FromBodyDto,
        _ => SignalRule.NameMatch,
    };

    private static IReadOnlyDictionary<string, BridgeNode> NodesFor(params ScoredEdge[] edges)
    {
        var nodes = new Dictionary<string, BridgeNode>(StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            AddNode(nodes, edge.Edge.SourceRef, edge.Edge.Kind, EndpointSide.Source);
            AddNode(nodes, edge.Edge.TargetRef, edge.Edge.Kind, EndpointSide.Target);
        }
        return nodes;
    }

    private static void AddNode(Dictionary<string, BridgeNode> nodes, EdgeRef edgeRef, BridgeKind kind, EndpointSide side)
    {
        var id = BridgeGraph.NodeIdOf(edgeRef, kind, side);
        if (id is null || nodes.ContainsKey(id))
            return;
        nodes[id] = new BridgeNode(id, BridgeGraph.NodeKindFor(kind, side), edgeRef.Display, edgeRef.FilePath, 0);
    }

    // The data-model chain: UserDto --MapsTo--> ApplicationUser --StoredIn--> ApplicationUsers (table).
    private static (BridgeGraph Graph, string DtoId, string EntityId, string TableId) BuildChain()
    {
        var dto = Sym("sym-userdto", "UserDto");
        var entity = Sym("sym-appuser", "ApplicationUser");
        var entityAsSource = Sym("sym-appuser", "ApplicationUser");
        var table = NonSym("ApplicationUsers");

        var mapEdge = Edge(BridgeKind.MapsTo, dto, entity, 0.95);
        var tableEdge = Edge(BridgeKind.StoredIn, entityAsSource, table, 0.97);

        var nodes = NodesFor(mapEdge, tableEdge);
        var graph = BridgeGraph.Build([mapEdge, tableEdge], nodes);

        var tableId = BridgeGraph.NodeIdOf(table, BridgeKind.StoredIn, EndpointSide.Target)!;
        return (graph, "sym-userdto", "sym-appuser", tableId);
    }

    [Fact]
    public void Build_registers_every_endpoint_node_and_drops_edges_with_unknown_endpoints()
    {
        var (graph, dtoId, entityId, tableId) = BuildChain();

        Assert.True(graph.Contains(dtoId));
        Assert.True(graph.Contains(entityId));
        Assert.True(graph.Contains(tableId));
        Assert.False(graph.Contains("nope"));
    }

    [Fact]
    public void Build_drops_an_edge_whose_endpoint_is_absent_from_the_node_lookup()
    {
        var dto = Sym("sym-userdto", "UserDto");
        var entity = Sym("sym-appuser", "ApplicationUser");
        var edge = Edge(BridgeKind.MapsTo, dto, entity);

        // Node lookup deliberately omits the entity node -> the edge cannot connect and is dropped.
        var partialNodes = new Dictionary<string, BridgeNode>(StringComparer.Ordinal)
        {
            ["sym-userdto"] = new("sym-userdto", BridgeNodeKind.CsDto, "UserDto", "UserDto.cs", 0),
        };

        var graph = BridgeGraph.Build([edge], partialNodes);

        Assert.Empty(graph.Incident("sym-userdto"));
        Assert.Empty(graph.Walk("sym-userdto", maxDepth: 5));
    }

    [Fact]
    public void NodeKindFor_NavigatesTo_UsesNextRouteTarget()
    {
        Assert.Equal(BridgeNodeKind.TsType, BridgeGraph.NodeKindFor(BridgeKind.NavigatesTo, EndpointSide.Source));
        Assert.Equal(BridgeNodeKind.FileRoute, BridgeGraph.NodeKindFor(BridgeKind.NavigatesTo, EndpointSide.Target));
    }

    [Fact]
    public void Walk_reaches_the_whole_chain_in_reach_order()
    {
        var (graph, dtoId, _, _) = BuildChain();

        var walked = graph.Walk(dtoId, maxDepth: 5);

        // Two edges traversed: MapsTo (hop 1 from the DTO) then StoredIn (hop 2 via the entity).
        Assert.Equal(2, walked.Count);
        Assert.Equal(BridgeKind.MapsTo, walked[0].Edge.Kind);
        Assert.Equal(BridgeKind.StoredIn, walked[1].Edge.Kind);
    }

    [Fact]
    public void Walk_respects_maxDepth()
    {
        var (graph, dtoId, _, _) = BuildChain();

        var oneHop = graph.Walk(dtoId, maxDepth: 1);

        // Only the first edge (DTO -> entity) is within one hop; the entity -> table edge is at depth 2.
        Assert.Single(oneHop);
        Assert.Equal(BridgeKind.MapsTo, oneHop[0].Edge.Kind);
    }

    [Fact]
    public void Walk_from_the_table_end_reaches_back_to_the_dto_undirected()
    {
        var (graph, _, _, tableId) = BuildChain();

        var walked = graph.Walk(tableId, maxDepth: 5);

        Assert.Equal(2, walked.Count);
        Assert.Equal(BridgeKind.StoredIn, walked[0].Edge.Kind); // nearest to the table
        Assert.Equal(BridgeKind.MapsTo, walked[1].Edge.Kind);
    }

    [Fact]
    public void Walk_deduplicates_an_edge_even_when_both_endpoints_are_reached()
    {
        // A triangle: a -> b, b -> c, a -> c. Walking from a must emit each of the 3 edges exactly once.
        var a = Sym("a", "A");
        var b = Sym("b", "B");
        var c = Sym("c", "C");
        var ab = Edge(BridgeKind.MapsTo, a, b);
        var bc = Edge(BridgeKind.MapsTo, b, c);
        var ac = Edge(BridgeKind.MapsTo, a, c);

        var graph = BridgeGraph.Build([ab, bc, ac], NodesFor(ab, bc, ac));

        var walked = graph.Walk("a", maxDepth: 5);

        Assert.Equal(3, walked.Count);
        Assert.Equal(3, walked.Select(e => $"{e.Edge.SourceRef.SymbolId}->{e.Edge.TargetRef.SymbolId}").Distinct().Count());
    }

    [Fact]
    public void Walk_returns_empty_for_an_unknown_start_or_nonpositive_depth()
    {
        var (graph, dtoId, _, _) = BuildChain();

        Assert.Empty(graph.Walk("unknown", maxDepth: 5));
        Assert.Empty(graph.Walk(dtoId, maxDepth: 0));
    }

    [Fact]
    public void Build_preserves_the_scored_edge_score_and_band_verbatim()
    {
        var (graph, dtoId, _, _) = BuildChain();

        var mapEdge = graph.Incident(dtoId).Single();

        Assert.Equal(0.95, mapEdge.Score, precision: 5);
        Assert.Equal(ConfidenceBand.High, mapEdge.Band);
    }
}
