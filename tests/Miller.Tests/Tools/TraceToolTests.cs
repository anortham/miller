using Miller.Core.Contracts;
using Miller.Core.Graph;
using Miller.Core.Resolver;
using Miller.Indexing;
using Miller.Server.Resolution;
using Miller.Server.Tools;
using Xunit;

namespace Miller.Tests.Tools;

/// <summary>
/// Fast/pure unit tests for the <c>trace</c> tool's <see cref="TraceTool.Run"/> core (M4 Task 10). All three modes
/// (auto / path / bridge) plus the no-path case, the load-bearing honesty flags ([verb-unknown] / [ambiguous]),
/// depth/limit bounds, and compact-vs-full rendering are exercised directly against in-memory fixtures — no MCP, no DI,
/// no DB, no julie. Category!=Scale (the default fast suite).
/// </summary>
public sealed class TraceToolTests
{
    // ---------- fixture builders ----------

    // A symbol-graph index over the given symbols + dependency edges (no bridge). DocIds are the 0-based ordinals
    // MillerRepositoryIndex.Build requires.
    private static MillerRepositoryIndex BuildSymbolIndex(
        IReadOnlyList<(string id, string name, string kind, string file, int line)> symbols,
        IReadOnlyList<(string from, string to)> edges)
    {
        var indexed = new List<IndexedSymbol>(symbols.Count);
        for (int i = 0; i < symbols.Count; i++)
        {
            var (id, name, kind, file, line) = symbols[i];
            indexed.Add(new IndexedSymbol(
                DocId: i, SymbolId: id, Name: name, Signature: $"{kind} {name}()",
                Kind: kind, Language: "csharp", FilePath: file, StartLine: line, EndLine: line, ParentId: null));
        }
        var graphEdges = edges.Select(e => new GraphEdge(e.from, e.to, "calls")).ToList();
        return MillerRepositoryIndex.Build(indexed, graphEdges);
    }

    // A trivially-resolved NameResolution for a symbol-backed endpoint.
    private static NameResolution Resolved(string symbolId) =>
        new(ResolutionStatus.Resolved, symbolId, MatchCount: 1);

    // An EdgeRef for a symbol-backed endpoint (id == node id).
    private static EdgeRef SymbolRef(string symbolId, string display, string file) =>
        new(display, symbolId, file, Resolved(symbolId));

    // An EdgeRef for a non-symbol endpoint (DB table / route): no symbol id, trivially resolved.
    private static EdgeRef NonSymbolRef(string display) =>
        new(display, SymbolId: null, FilePath: null, new NameResolution(ResolutionStatus.Resolved, null, 1));

    private static ScoredEdge MakeScored(
        BridgeKind kind, EdgeRef source, EdgeRef target, ConfidenceBand band, double score,
        bool ambiguous = false, bool verbUnknown = false, IReadOnlyList<Signal>? signals = null)
    {
        var candidate = new CandidateEdge(
            kind, source, target,
            Evidence: Array.Empty<Evidence>(),
            Signals: signals ?? Array.Empty<Signal>());
        return new ScoredEdge(candidate, score, band,
            IsMultiSignal: (signals?.Count ?? 0) > 1, HasAmbiguousName: ambiguous, IsVerbUnknown: verbUnknown);
    }

    // Build a node lookup covering every endpoint of the given scored edges, then the bridge graph + an index that
    // carries it. Symbol-backed endpoints get a real IndexedSymbol so SmartTargetResolver can resolve them by name.
    private static MillerRepositoryIndex BuildBridgeIndex(
        IReadOnlyList<(string symbolId, string name, string file, int line)> symbols,
        IReadOnlyList<ScoredEdge> edges,
        IReadOnlyDictionary<string, BridgeNode> extraNodes)
    {
        var indexed = new List<IndexedSymbol>(symbols.Count);
        var nodes = new Dictionary<string, BridgeNode>(StringComparer.Ordinal);
        for (int i = 0; i < symbols.Count; i++)
        {
            var (symbolId, name, file, line) = symbols[i];
            indexed.Add(new IndexedSymbol(
                DocId: i, SymbolId: symbolId, Name: name, Signature: $"class {name}", Kind: "class",
                Language: "csharp", FilePath: file, StartLine: line, EndLine: line, ParentId: null));
            nodes[symbolId] = new BridgeNode(symbolId, BridgeNodeKind.CsDto, name, file, line);
        }
        foreach (var (id, node) in extraNodes)
            nodes[id] = node;

        var bridge = BridgeGraph.Build(edges, nodes);
        return MillerRepositoryIndex.Build(indexed, Array.Empty<GraphEdge>(), bridge);
    }

    private static SmartTargetResolver ResolverFor(MillerRepositoryIndex index) => new(index);

    // ---------- mode: auto ----------

    [Fact]
    public void Auto_ReturnsCallersAndCallees_WithHopAndProvenance()
    {
        // A depends on B; C depends on A. Direction.Both from A reaches both B (callee) and C (caller).
        var index = BuildSymbolIndex(
            new[]
            {
                ("a", "Alpha", "method", "src/A.cs", 10),
                ("b", "Beta", "method", "src/B.cs", 20),
                ("c", "Gamma", "method", "src/C.cs", 30),
            },
            new[] { ("a", "b"), ("c", "a") });

        string outp = TraceTool.Run(index, ResolverFor(index),
            target: "Alpha", mode: "auto", to: null, depth: 3, limit: 20, fullFormat: false,
            out int emitted, out int visited);

        Assert.Equal(2, emitted);
        Assert.Equal(2, visited);
        Assert.Contains("# trace Alpha (auto, 2 neighbour(s))", outp);
        Assert.Contains("Beta  method  src/B.cs:20  (hop 1)", outp);
        Assert.Contains("Gamma  method  src/C.cs:30  (hop 1)", outp);
    }

    [Fact]
    public void Auto_RespectsLimit()
    {
        var index = BuildSymbolIndex(
            new[]
            {
                ("a", "Alpha", "method", "src/A.cs", 1),
                ("b", "Beta", "method", "src/B.cs", 2),
                ("c", "Gamma", "method", "src/C.cs", 3),
                ("d", "Delta", "method", "src/D.cs", 4),
            },
            new[] { ("a", "b"), ("a", "c"), ("a", "d") });

        string outp = TraceTool.Run(index, ResolverFor(index),
            target: "Alpha", mode: "auto", to: null, depth: 1, limit: 2, fullFormat: false,
            out int emitted, out _);

        Assert.Equal(2, emitted);
    }

    [Fact]
    public void Auto_NoNeighbours_CleanMessage()
    {
        var index = BuildSymbolIndex(
            new[] { ("a", "Alpha", "method", "src/A.cs", 1) },
            Array.Empty<(string, string)>());

        string outp = TraceTool.Run(index, ResolverFor(index),
            target: "Alpha", mode: "auto", to: null, depth: 3, limit: 20, fullFormat: false,
            out int emitted, out _);

        Assert.Equal(0, emitted);
        Assert.Contains("No neighbours", outp);
    }

    [Fact]
    public void Auto_TargetNotFound_CleanMessage()
    {
        var index = BuildSymbolIndex(
            new[] { ("a", "Alpha", "method", "src/A.cs", 1) },
            Array.Empty<(string, string)>());

        string outp = TraceTool.Run(index, ResolverFor(index),
            target: "DoesNotExist", mode: "auto", to: null, depth: 3, limit: 20, fullFormat: false,
            out int emitted, out _);

        Assert.Equal(0, emitted);
        Assert.Contains("not found", outp);
    }

    // ---------- mode: path ----------

    [Fact]
    public void Path_RendersOrderedShortestPath()
    {
        // a -> b -> c (forward dependency adjacency).
        var index = BuildSymbolIndex(
            new[]
            {
                ("a", "Alpha", "method", "src/A.cs", 1),
                ("b", "Beta", "method", "src/B.cs", 2),
                ("c", "Gamma", "method", "src/C.cs", 3),
            },
            new[] { ("a", "b"), ("b", "c") });

        string outp = TraceTool.Run(index, ResolverFor(index),
            target: "Alpha", mode: "path", to: "Gamma", depth: 5, limit: 20, fullFormat: false,
            out int emitted, out int visited);

        Assert.Equal(3, emitted);   // Alpha, Beta, Gamma
        Assert.Equal(3, visited);
        Assert.Contains("# trace path Alpha -> Gamma (2 hop(s))", outp);
        // Ordered: Alpha first (no arrow), then -> Beta, then -> Gamma. Search the BODY only — the header line also
        // contains "Alpha" and "Gamma", which would otherwise confuse the ordering check.
        int headerEnd = outp.IndexOf('\n');
        Assert.True(headerEnd > 0, "compact path output must have a header line");
        string body = outp[(headerEnd + 1)..];
        int alpha = body.IndexOf("Alpha", StringComparison.Ordinal);
        int beta = body.IndexOf("Beta", StringComparison.Ordinal);
        int gamma = body.IndexOf("Gamma", StringComparison.Ordinal);
        Assert.True(alpha >= 0 && alpha < beta && beta < gamma,
            "path body must be rendered Alpha -> Beta -> Gamma in order");
        Assert.Contains("-> Beta  method  src/B.cs:2", outp);
    }

    [Fact]
    public void Path_NoConnection_CleanMessage()
    {
        // a and c are not connected.
        var index = BuildSymbolIndex(
            new[]
            {
                ("a", "Alpha", "method", "src/A.cs", 1),
                ("c", "Gamma", "method", "src/C.cs", 3),
            },
            Array.Empty<(string, string)>());

        string outp = TraceTool.Run(index, ResolverFor(index),
            target: "Alpha", mode: "path", to: "Gamma", depth: 5, limit: 20, fullFormat: false,
            out int emitted, out _);

        Assert.Equal(0, emitted);
        Assert.Contains("No path from 'Alpha' to 'Gamma'", outp);
    }

    [Fact]
    public void Path_BeyondDepth_NoPath()
    {
        var index = BuildSymbolIndex(
            new[]
            {
                ("a", "Alpha", "method", "src/A.cs", 1),
                ("b", "Beta", "method", "src/B.cs", 2),
                ("c", "Gamma", "method", "src/C.cs", 3),
            },
            new[] { ("a", "b"), ("b", "c") });

        // depth=1 cannot reach Gamma (2 hops away).
        string outp = TraceTool.Run(index, ResolverFor(index),
            target: "Alpha", mode: "path", to: "Gamma", depth: 1, limit: 20, fullFormat: false,
            out int emitted, out _);

        Assert.Equal(0, emitted);
        Assert.Contains("No path", outp);
    }

    [Fact]
    public void Path_MissingTo_UsageMessage()
    {
        var index = BuildSymbolIndex(
            new[] { ("a", "Alpha", "method", "src/A.cs", 1) },
            Array.Empty<(string, string)>());

        string outp = TraceTool.Run(index, ResolverFor(index),
            target: "Alpha", mode: "path", to: null, depth: 5, limit: 20, fullFormat: false,
            out int emitted, out _);

        Assert.Equal(0, emitted);
        Assert.Contains("mode=path requires 'to'", outp);
    }

    // ---------- mode: bridge ----------

    [Fact]
    public void Bridge_RendersChainWithScoreAndBand()
    {
        // UserDto --CreateMap--> ApplicationUser --DbSet--> ApplicationUsers (a table, non-symbol node).
        var dtoEntity = MakeScored(
            BridgeKind.MapsTo,
            SymbolRef("dto", "UserDto", "src/UserDto.cs"),
            SymbolRef("ent", "ApplicationUser", "src/ApplicationUser.cs"),
            ConfidenceBand.High, 0.95);

        string tableNodeId = BridgeGraph.SynthesizeId(BridgeNodeKind.DbTable, "ApplicationUsers");
        var entityTable = MakeScored(
            BridgeKind.StoredIn,
            SymbolRef("ent", "ApplicationUser", "src/ApplicationUser.cs"),
            NonSymbolRef("ApplicationUsers"),
            ConfidenceBand.High, 0.9);

        var extra = new Dictionary<string, BridgeNode>(StringComparer.Ordinal)
        {
            [tableNodeId] = new BridgeNode(tableNodeId, BridgeNodeKind.DbTable, "ApplicationUsers", null, 0),
        };
        var index = BuildBridgeIndex(
            new[] { ("dto", "UserDto", "src/UserDto.cs", 1), ("ent", "ApplicationUser", "src/ApplicationUser.cs", 1) },
            new[] { dtoEntity, entityTable },
            extra);

        string outp = TraceTool.Run(index, ResolverFor(index),
            target: "UserDto", mode: "bridge", to: null, depth: 3, limit: 20, fullFormat: false,
            out int emitted, out int visited);

        Assert.Equal(2, emitted);
        Assert.Equal(2, visited);
        Assert.Contains("UserDto  --CreateMap-->  ApplicationUser", outp);
        Assert.Contains("0.95 (High)", outp);
        Assert.Contains("ApplicationUser  --DbSet-->  ApplicationUsers", outp);
        Assert.Contains("0.90 (High)", outp);
    }

    [Fact]
    public void Bridge_RendersVerbUnknownFlag()
    {
        // A route-only (verb-unknown) Hits edge: TS call -> endpoint.
        string callNodeId = BridgeGraph.SynthesizeId(BridgeNodeKind.TsType, "api/users");
        var hits = MakeScored(
            BridgeKind.Hits,
            NonSymbolRef("api/users"),
            SymbolRef("ep", "GetUsers", "src/UsersController.cs"),
            ConfidenceBand.Medium, 0.75, verbUnknown: true);

        var extra = new Dictionary<string, BridgeNode>(StringComparer.Ordinal)
        {
            [callNodeId] = new BridgeNode(callNodeId, BridgeNodeKind.TsType, "api/users", "web/api.ts", 5),
        };
        var index = BuildBridgeIndex(
            new[] { ("ep", "GetUsers", "src/UsersController.cs", 12) },
            new[] { hits },
            extra);

        // Start from the endpoint symbol (a symbol-backed node) so it resolves by name.
        string outp = TraceTool.Run(index, ResolverFor(index),
            target: "GetUsers", mode: "bridge", to: null, depth: 2, limit: 20, fullFormat: false,
            out int emitted, out _);

        Assert.Equal(1, emitted);
        Assert.Contains("[verb-unknown]", outp);
        Assert.Contains("0.75 (Medium)", outp);
    }

    [Fact]
    public void Bridge_RendersAmbiguousFlag()
    {
        var mapsTo = MakeScored(
            BridgeKind.MapsTo,
            SymbolRef("dto", "OrderDto", "src/OrderDto.cs"),
            SymbolRef("ent", "Order", "src/Order.cs"),
            ConfidenceBand.Medium, 0.7, ambiguous: true);

        var index = BuildBridgeIndex(
            new[] { ("dto", "OrderDto", "src/OrderDto.cs", 1), ("ent", "Order", "src/Order.cs", 1) },
            new[] { mapsTo },
            new Dictionary<string, BridgeNode>(StringComparer.Ordinal));

        string outp = TraceTool.Run(index, ResolverFor(index),
            target: "OrderDto", mode: "bridge", to: null, depth: 2, limit: 20, fullFormat: false,
            out int emitted, out _);

        Assert.Equal(1, emitted);
        Assert.Contains("[ambiguous]", outp);
        // An ambiguous edge is never High — assert the band is rendered Medium, not silently certain.
        Assert.Contains("(Medium)", outp);
        Assert.DoesNotContain("(High)", outp);
    }

    [Fact]
    public void Bridge_NotOnBridge_CleanMessage()
    {
        // A symbol that exists but has no bridge edges.
        var index = BuildBridgeIndex(
            new[] { ("x", "Loner", "src/Loner.cs", 1) },
            Array.Empty<ScoredEdge>(),
            new Dictionary<string, BridgeNode>(StringComparer.Ordinal));

        string outp = TraceTool.Run(index, ResolverFor(index),
            target: "Loner", mode: "bridge", to: null, depth: 3, limit: 20, fullFormat: false,
            out int emitted, out _);

        Assert.Equal(0, emitted);
        Assert.Contains("not on a cross-language bridge", outp);
    }

    [Fact]
    public void Bridge_FullFormat_ListsFiringSignals()
    {
        var signals = new Signal[]
        {
            new StructuralSignal(SignalRule.CreateMap, Present: true),
            new FieldSetSignal(FieldCount: 8, Jaccard: 0.6),
            new NameSignal(NameTier.Exact),
            new NameResolutionSignal(EndpointSide.Target, ResolutionStatus.Resolved, MatchCount: 1),
        };
        var mapsTo = MakeScored(
            BridgeKind.MapsTo,
            SymbolRef("dto", "UserDto", "src/UserDto.cs"),
            SymbolRef("ent", "ApplicationUser", "src/ApplicationUser.cs"),
            ConfidenceBand.High, 0.95, signals: signals);

        var index = BuildBridgeIndex(
            new[] { ("dto", "UserDto", "src/UserDto.cs", 1), ("ent", "ApplicationUser", "src/ApplicationUser.cs", 1) },
            new[] { mapsTo },
            new Dictionary<string, BridgeNode>(StringComparer.Ordinal));

        string compact = TraceTool.Run(index, ResolverFor(index),
            target: "UserDto", mode: "bridge", to: null, depth: 2, limit: 20, fullFormat: false,
            out _, out _);
        string full = TraceTool.Run(index, ResolverFor(index),
            target: "UserDto", mode: "bridge", to: null, depth: 2, limit: 20, fullFormat: true,
            out _, out _);

        // Compact must NOT carry the signal listing; full must.
        Assert.DoesNotContain("signals:", compact);
        Assert.Contains("signals:", full);
        Assert.Contains("CreateMap=present", full);
        Assert.Contains("FieldSetJaccard(count=8, jaccard=0.60)", full);
        Assert.Contains("NameMatch=Exact", full);
        Assert.Contains("NameResolution(Target=Resolved, matches=1)", full);
    }

    [Fact]
    public void Bridge_RespectsLimit()
    {
        // One DTO node bridging to three distinct entities -> three incident edges; limit caps the rendered count.
        var edges = new[]
        {
            MakeScored(BridgeKind.MapsTo, SymbolRef("dto", "Hub", "src/Hub.cs"), SymbolRef("e1", "EntityA", "src/EntityA.cs"), ConfidenceBand.High, 0.9),
            MakeScored(BridgeKind.MapsTo, SymbolRef("dto", "Hub", "src/Hub.cs"), SymbolRef("e2", "EntityB", "src/EntityB.cs"), ConfidenceBand.High, 0.9),
            MakeScored(BridgeKind.MapsTo, SymbolRef("dto", "Hub", "src/Hub.cs"), SymbolRef("e3", "EntityC", "src/EntityC.cs"), ConfidenceBand.High, 0.9),
        };
        var index = BuildBridgeIndex(
            new[]
            {
                ("dto", "Hub", "src/Hub.cs", 1),
                ("e1", "EntityA", "src/EntityA.cs", 1),
                ("e2", "EntityB", "src/EntityB.cs", 1),
                ("e3", "EntityC", "src/EntityC.cs", 1),
            },
            edges,
            new Dictionary<string, BridgeNode>(StringComparer.Ordinal));

        string outp = TraceTool.Run(index, ResolverFor(index),
            target: "Hub", mode: "bridge", to: null, depth: 1, limit: 2, fullFormat: false,
            out int emitted, out _);

        Assert.Equal(2, emitted);
    }

    // ---------- dispatch / guards ----------

    [Fact]
    public void UnknownMode_CleanMessage()
    {
        var index = BuildSymbolIndex(
            new[] { ("a", "Alpha", "method", "src/A.cs", 1) },
            Array.Empty<(string, string)>());

        string outp = TraceTool.Run(index, ResolverFor(index),
            target: "Alpha", mode: "sideways", to: null, depth: 3, limit: 20, fullFormat: false,
            out int emitted, out _);

        Assert.Equal(0, emitted);
        Assert.Contains("Unknown mode 'sideways'", outp);
    }

    [Fact]
    public void NullIndex_Throws()
    {
        var index = BuildSymbolIndex(
            new[] { ("a", "Alpha", "method", "src/A.cs", 1) },
            Array.Empty<(string, string)>());
        var resolver = ResolverFor(index);

        Assert.Throws<ArgumentNullException>(() =>
            TraceTool.Run(null!, resolver, "Alpha", "auto", null, 3, 20, false, out _, out _));
        Assert.Throws<ArgumentNullException>(() =>
            TraceTool.Run(index, null!, "Alpha", "auto", null, 3, 20, false, out _, out _));
    }
}
