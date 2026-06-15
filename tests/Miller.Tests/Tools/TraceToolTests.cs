using Miller.Core.Contracts;
using Miller.Core.Graph;
using Miller.Core.Resolver;
using Miller.Indexing;
using Miller.Server.Resolution;
using Miller.Server.Tools;
using Miller.Tests;
using System.Text.Json;
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
                Kind: kind, Language: "csharp", FilePath: file, StartLine: line, EndLine: line, ParentId: null, IsTest: false));
        }
        var graphEdges = edges.Select(e => new GraphEdge(e.from, e.to, "calls")).ToList();
        return MillerRepositoryIndex.Build(indexed, graphEdges);
    }

    private static MillerRepositoryIndex EmptyIndex() =>
        MillerRepositoryIndex.Build(Array.Empty<IndexedSymbol>(), Array.Empty<GraphEdge>());

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
        IReadOnlyDictionary<string, BridgeNode> extraNodes,
        BridgeCapabilityReport? capabilityReport = null)
    {
        var indexed = new List<IndexedSymbol>(symbols.Count);
        var nodes = new Dictionary<string, BridgeNode>(StringComparer.Ordinal);
        for (int i = 0; i < symbols.Count; i++)
        {
            var (symbolId, name, file, line) = symbols[i];
            indexed.Add(new IndexedSymbol(
                DocId: i, SymbolId: symbolId, Name: name, Signature: $"class {name}", Kind: "class",
                Language: "csharp", FilePath: file, StartLine: line, EndLine: line, ParentId: null, IsTest: false));
            nodes[symbolId] = new BridgeNode(symbolId, BridgeNodeKind.CsDto, name, file, line);
        }
        foreach (var (id, node) in extraNodes)
            nodes[id] = node;

        var bridge = BridgeGraph.Build(edges, nodes, capabilityReport);
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
    public void Auto_Json_RendersStructuredNeighbours()
    {
        var index = BuildSymbolIndex(
            new[]
            {
                ("a", "Alpha", "method", "src/A.cs", 10),
                ("b", "Beta", "method", "src/B.cs", 20),
                ("c", "Gamma", "method", "src/C.cs", 30),
            },
            new[] { ("a", "b"), ("c", "a") });

        string json = TraceTool.Run(index, ResolverFor(index),
            target: "Alpha", mode: "auto", to: null, depth: 3, limit: 20, fullFormat: false, json: true,
            out int emitted, out int visited);

        Assert.Equal(2, emitted);
        Assert.Equal(2, visited);
        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        Assert.Equal("auto", root.GetProperty("mode").GetString());
        Assert.Equal("Alpha", root.GetProperty("target").GetString());
        Assert.Equal(2, root.GetProperty("emitted").GetInt32());
        Assert.Equal(2, root.GetProperty("nodes_visited").GetInt32());
        Assert.Equal("Alpha", root.GetProperty("resolved_target").GetProperty("name").GetString());

        JsonElement[] nodes = root.GetProperty("nodes").EnumerateArray().ToArray();
        Assert.Equal(3, nodes.Length);
        Assert.Contains(nodes, node => node.GetProperty("id").GetString() == "a" &&
                                       node.GetProperty("role").GetString() == "target");
        Assert.Contains(nodes, node => node.GetProperty("name").GetString() == "Beta" &&
                                       node.GetProperty("hop").GetInt32() == 1);

        JsonElement[] links = root.GetProperty("links").EnumerateArray().ToArray();
        Assert.Equal(2, links.Length);
        Assert.Contains(links, link => link.GetProperty("source").GetString() == "a" &&
                                       link.GetProperty("target").GetString() == "b" &&
                                       link.GetProperty("kind").GetString() == "neighbour");
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

    [Fact]
    public void Auto_MisspelledTarget_SuggestsNearMissesInNote()
    {
        var index = BuildSymbolIndex(
            new[] { ("a", "Alpha", "method", "src/A.cs", 1) },
            Array.Empty<(string, string)>());

        // Wrong-case miss of "Alpha" — the note must offer the close name for a one-turn correction.
        string outp = TraceTool.Run(index, ResolverFor(index),
            target: "alpha", mode: "auto", to: null, depth: 3, limit: 20, fullFormat: false,
            out int emitted, out _);

        Assert.Equal(0, emitted);
        Assert.Contains("not found", outp);
        Assert.Contains("Closest:", outp);
        Assert.Contains("Alpha", outp);
    }

    [Fact]
    public void Auto_ScopeDisambiguatesAmbiguousTarget()
    {
        var index = BuildSymbolIndex(
            new[]
            {
                ("a1", "Handle", "method", "src/First.cs", 1),
                ("a2", "Handle", "method", "src/Second.cs", 1),
                ("b", "Next", "method", "src/Next.cs", 10),
            },
            new[] { ("a2", "b") });

        string outp = TraceTool.Run(index, ResolverFor(index),
            target: "Handle", scope: "src/Second.cs", mode: "auto", to: null, depth: 3, limit: 20,
            fullFormat: false, emitted: out int emitted, nodesVisited: out int visited);

        Assert.Equal(1, emitted);
        Assert.Equal(1, visited);
        Assert.Contains("# trace Handle (auto, 1 neighbour(s))", outp);
        Assert.Contains("Next  method  src/Next.cs:10  (hop 1)", outp);
        Assert.DoesNotContain("Multiple candidates", outp);
    }

    [Fact]
    public void Auto_AmbiguousTarget_PointsToScope()
    {
        var index = BuildSymbolIndex(
            new[]
            {
                ("a1", "Handle", "method", "src/First.cs", 1),
                ("a2", "Handle", "method", "src/Second.cs", 1),
            },
            Array.Empty<(string, string)>());

        string outp = TraceTool.Run(index, ResolverFor(index),
            target: "Handle", scope: null, mode: "auto", to: null, depth: 3, limit: 20,
            fullFormat: false, emitted: out int emitted, nodesVisited: out _);

        Assert.Equal(0, emitted);
        Assert.Contains("Multiple candidates", outp);
        Assert.Contains("scope=<file>", outp);
    }

    [Fact]
    public void Auto_AmbiguousTarget_CompactCapsCandidatesWithRemainderNote()
    {
        var symbols = Enumerable.Range(1, 25)
            .Select(i => ($"a{i}", "Search", "method", $"src/File{i:00}.cs", i))
            .ToArray();
        var index = BuildSymbolIndex(symbols, Array.Empty<(string, string)>());

        string outp = TraceTool.Run(index, ResolverFor(index),
            target: "Search", scope: null, mode: "auto", to: null, depth: 3, limit: 20,
            fullFormat: false, emitted: out int emitted, nodesVisited: out _);

        Assert.Equal(0, emitted);
        Assert.Contains("src/File20.cs", outp);
        Assert.DoesNotContain("src/File21.cs", outp);
        Assert.Contains("5 more candidates", outp);
    }

    [Fact]
    public void Auto_ScopedAmbiguousTarget_AsksForMoreSpecificTarget()
    {
        var index = BuildSymbolIndex(
            new[]
            {
                ("a1", "SearchTool", "class", "src/SearchTool.cs", 10),
                ("a2", "SearchTool", "constructor", "src/SearchTool.cs", 12),
            },
            Array.Empty<(string, string)>());

        string outp = TraceTool.Run(index, ResolverFor(index),
            target: "SearchTool", scope: "src/SearchTool.cs", mode: "auto", to: null, depth: 3, limit: 20,
            fullFormat: false, emitted: out int emitted, nodesVisited: out _);

        Assert.Equal(0, emitted);
        Assert.Contains("more specific target", outp);
        Assert.DoesNotContain("pass scope=<file>", outp);
    }

    [Fact]
    public void Auto_AmbiguousTarget_JsonCarriesDiagnostic()
    {
        var index = BuildSymbolIndex(
            new[]
            {
                ("a1", "Handle", "method", "src/First.cs", 1),
                ("a2", "Handle", "method", "src/Second.cs", 1),
            },
            Array.Empty<(string, string)>());

        string json = TraceTool.Run(index, ResolverFor(index),
            target: "Handle", scope: null, mode: "auto", to: null, depth: 3, limit: 20,
            fullFormat: false, json: true, emitted: out int emitted, nodesVisited: out _);

        Assert.Equal(0, emitted);
        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        Assert.Contains("Multiple candidates", root.GetProperty("note").GetString());
        JsonElement diagnostic = Assert.Single(root.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("ambiguous_target", diagnostic.GetProperty("code").GetString());
        Assert.Empty(root.GetProperty("nodes").EnumerateArray());
        Assert.Empty(root.GetProperty("links").EnumerateArray());
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
    public void Path_Json_RendersStructuredPathLinks()
    {
        var index = BuildSymbolIndex(
            new[]
            {
                ("a", "Alpha", "method", "src/A.cs", 1),
                ("b", "Beta", "method", "src/B.cs", 2),
                ("c", "Gamma", "method", "src/C.cs", 3),
            },
            new[] { ("a", "b"), ("b", "c") });

        string json = TraceTool.Run(index, ResolverFor(index),
            target: "Alpha", mode: "path", to: "Gamma", depth: 5, limit: 20, fullFormat: false, json: true,
            out int emitted, out int visited);

        Assert.Equal(3, emitted);
        Assert.Equal(3, visited);
        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        Assert.Equal("path", root.GetProperty("mode").GetString());
        Assert.Equal("Gamma", root.GetProperty("to").GetString());
        Assert.Equal("Gamma", root.GetProperty("resolved_to").GetProperty("name").GetString());
        Assert.Equal(2, root.GetProperty("hops").GetInt32());

        JsonElement[] nodes = root.GetProperty("nodes").EnumerateArray().ToArray();
        Assert.Equal(new[] { "Alpha", "Beta", "Gamma" }, nodes.Select(node => node.GetProperty("name").GetString()).ToArray());

        JsonElement[] links = root.GetProperty("links").EnumerateArray().ToArray();
        Assert.Equal(2, links.Length);
        Assert.Equal("a", links[0].GetProperty("source").GetString());
        Assert.Equal("b", links[0].GetProperty("target").GetString());
        Assert.Equal("dependency_path", links[0].GetProperty("kind").GetString());
        Assert.Equal(1, links[0].GetProperty("hop").GetInt32());
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
    public void Path_NoConnection_JsonCarriesDiagnostic()
    {
        var index = BuildSymbolIndex(
            new[]
            {
                ("a", "Alpha", "method", "src/A.cs", 1),
                ("c", "Gamma", "method", "src/C.cs", 3),
            },
            Array.Empty<(string, string)>());

        string json = TraceTool.Run(index, ResolverFor(index),
            target: "Alpha", mode: "path", to: "Gamma", depth: 5, limit: 20, fullFormat: false, json: true,
            out int emitted, out int visited);

        Assert.Equal(0, emitted);
        Assert.Equal(0, visited);
        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        Assert.Contains("No path from 'Alpha' to 'Gamma'", root.GetProperty("note").GetString());
        Assert.Equal("Alpha", root.GetProperty("resolved_target").GetProperty("name").GetString());
        Assert.Equal("Gamma", root.GetProperty("resolved_to").GetProperty("name").GetString());
        JsonElement diagnostic = Assert.Single(root.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("no_path", diagnostic.GetProperty("code").GetString());
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
    public void Bridge_Json_RendersProviderNodesLinksConfidenceAndFlags()
    {
        string tableNodeId = BridgeGraph.SynthesizeId(BridgeNodeKind.DbTable, "ApplicationUsers");
        var dtoEntity = MakeScored(
            BridgeKind.MapsTo,
            SymbolRef("dto", "UserDto", "src/UserDto.cs"),
            SymbolRef("ent", "ApplicationUser", "src/ApplicationUser.cs"),
            ConfidenceBand.High, 0.95,
            signals: new Signal[] { new StructuralSignal(SignalRule.CreateMap, Present: true, new Evidence("src/Profile.cs", 7)) });
        var entityTable = MakeScored(
            BridgeKind.StoredIn,
            SymbolRef("ent", "ApplicationUser", "src/ApplicationUser.cs"),
            NonSymbolRef("ApplicationUsers"),
            ConfidenceBand.Medium, 0.75, ambiguous: true, verbUnknown: true,
            signals: new Signal[] { new NameResolutionSignal(EndpointSide.Target, ResolutionStatus.Ambiguous, MatchCount: 2) });

        var extra = new Dictionary<string, BridgeNode>(StringComparer.Ordinal)
        {
            [tableNodeId] = new BridgeNode(tableNodeId, BridgeNodeKind.DbTable, "ApplicationUsers", null, 0),
        };
        var capability = new BridgeCapabilityReport(
            ActiveProviders: ["dotnet-web"],
            SkippedProviders: [new BridgeProviderSkip("other-provider", "disabled")],
            Notes: ["dotnet-web bridge evidence loaded"],
            EvidenceCounts: new Dictionary<string, int>(StringComparer.Ordinal) { ["routes"] = 1 });
        var index = BuildBridgeIndex(
            new[] { ("dto", "UserDto", "src/UserDto.cs", 1), ("ent", "ApplicationUser", "src/ApplicationUser.cs", 2) },
            new[] { dtoEntity, entityTable },
            extra,
            capability);

        string json = TraceTool.Run(index, ResolverFor(index),
            target: "UserDto", mode: "bridge", to: null, depth: 3, limit: 20, fullFormat: false, json: true,
            out int emitted, out int visited);

        Assert.Equal(2, emitted);
        Assert.Equal(2, visited);
        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        Assert.Equal("bridge", root.GetProperty("mode").GetString());
        Assert.Equal("UserDto", root.GetProperty("resolved_target").GetProperty("display").GetString());

        JsonElement provider = root.GetProperty("provider");
        Assert.Equal("dotnet-web", provider.GetProperty("active_providers")[0].GetString());
        Assert.Equal("other-provider", provider.GetProperty("skipped_providers")[0].GetProperty("provider_id").GetString());
        Assert.Equal("routes", provider.GetProperty("evidence_counts")[0].GetProperty("name").GetString());

        JsonElement[] nodes = root.GetProperty("nodes").EnumerateArray().ToArray();
        Assert.Contains(nodes, node => node.GetProperty("id").GetString() == tableNodeId &&
                                       node.GetProperty("kind").GetString() == "db_table" &&
                                       node.GetProperty("line").GetInt32() == 0);

        JsonElement[] links = root.GetProperty("links").EnumerateArray().ToArray();
        Assert.Equal(2, links.Length);
        Assert.Equal("maps_to", links[0].GetProperty("kind").GetString());
        Assert.Equal("CreateMap", links[0].GetProperty("label").GetString());
        Assert.Equal("high", links[0].GetProperty("confidence").GetString());
        Assert.Equal(0.95, links[0].GetProperty("score").GetDouble(), precision: 5);
        Assert.Equal("CreateMap", links[0].GetProperty("signals")[0].GetProperty("rule").GetString());
        Assert.Equal("src/Profile.cs", links[0].GetProperty("signals")[0].GetProperty("evidence").GetProperty("file").GetString());

        Assert.Equal("stored_in", links[1].GetProperty("kind").GetString());
        Assert.Equal("medium", links[1].GetProperty("confidence").GetString());
        Assert.Equal(new[] { "ambiguous", "verb_unknown" },
            links[1].GetProperty("flags").EnumerateArray().Select(flag => flag.GetString()).ToArray());
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
    public void Bridge_RouteStringTarget_StartsFromClientSymbolForThatRoute()
    {
        var hits = MakeScored(
            BridgeKind.Hits,
            new EdgeRef("api/users/{}", "client", "web/api.ts", Resolved("client")),
            SymbolRef("ep", "DismissUser", "src/UsersController.cs"),
            ConfidenceBand.High, 0.9);

        var index = BuildBridgeIndex(
            new[]
            {
                ("client", "dismissUser", "web/api.ts", 5),
                ("ep", "DismissUser", "src/UsersController.cs", 12),
            },
            new[] { hits },
            new Dictionary<string, BridgeNode>(StringComparer.Ordinal));

        string outp = TraceTool.Run(index, ResolverFor(index),
            target: "/api/users/{userId}", mode: "bridge", to: null, depth: 2, limit: 20, fullFormat: false,
            out int emitted, out _);

        Assert.Equal(1, emitted);
        Assert.Contains("# trace bridge dismissUser", outp);
        Assert.Contains("dismissUser  --route-->  DismissUser", outp);
        Assert.DoesNotContain("is a file", outp);
    }

    [Fact]
    public void Bridge_FilePathWithOneBridgeSymbol_StartsFromThatSymbol()
    {
        var mapsTo = MakeScored(
            BridgeKind.MapsTo,
            SymbolRef("dto", "UserDto", "src/UserDto.cs"),
            SymbolRef("entity", "User", "src/User.cs"),
            ConfidenceBand.High, 0.95);
        var index = BuildBridgeIndex(
            new[]
            {
                ("dto", "UserDto", "src/UserDto.cs", 1),
                ("helper", "UserDtoHelper", "src/UserDto.cs", 20),
                ("entity", "User", "src/User.cs", 1),
            },
            new[] { mapsTo },
            new Dictionary<string, BridgeNode>(StringComparer.Ordinal));

        string outp = TraceTool.Run(index, ResolverFor(index),
            target: "src/UserDto.cs", mode: "bridge", to: null, depth: 2, limit: 20, fullFormat: false,
            out int emitted, out _);

        Assert.Equal(1, emitted);
        Assert.Contains("# trace bridge UserDto", outp);
        Assert.Contains("UserDto  --CreateMap-->  User", outp);
        Assert.DoesNotContain("is a file", outp);
    }

    [Fact]
    public void Bridge_ScopeDisambiguatesFunctionExportDuplicate()
    {
        var hits = MakeScored(
            BridgeKind.Hits,
            SymbolRef("fn", "getAllSecurityUsers", "web/userManagementservice.ts"),
            SymbolRef("ep", "AllUsers", "Controllers/UserManagementController.cs"),
            ConfidenceBand.High, 0.9);
        var index = BuildBridgeIndex(
            new[]
            {
                ("fn", "getAllSecurityUsers", "web/userManagementservice.ts", 16),
                ("export", "getAllSecurityUsers", "web/userManagementservice.ts", 16),
                ("import", "getAllSecurityUsers", "store/UserManagementModule.ts", 4),
                ("ep", "AllUsers", "Controllers/UserManagementController.cs", 65),
            },
            new[] { hits },
            new Dictionary<string, BridgeNode>(StringComparer.Ordinal));

        string outp = TraceTool.Run(index, ResolverFor(index),
            target: "getAllSecurityUsers", scope: "web/userManagementservice.ts", mode: "bridge", to: null,
            depth: 2, limit: 20, fullFormat: false, emitted: out int emitted, nodesVisited: out _);

        Assert.Equal(1, emitted);
        Assert.Contains("getAllSecurityUsers  --route-->  AllUsers  0.90 (High)", outp);
        Assert.DoesNotContain("Multiple candidates", outp);
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
    public void Bridge_NotOnBridge_IncludesCapabilityStatus_WhenProvidersSkipped()
    {
        var capability = new BridgeCapabilityReport(
            ActiveProviders: [],
            SkippedProviders: [new BridgeProviderSkip("dotnet-web", "no dotnet-web bridge evidence")],
            Notes: [],
            EvidenceCounts: new Dictionary<string, int>(StringComparer.Ordinal));
        var index = BuildBridgeIndex(
            new[] { ("x", "Loner", "src/Loner.cs", 1) },
            Array.Empty<ScoredEdge>(),
            new Dictionary<string, BridgeNode>(StringComparer.Ordinal),
            capability);

        string outp = TraceTool.Run(index, ResolverFor(index),
            target: "Loner", mode: "bridge", to: null, depth: 3, limit: 20, fullFormat: false,
            out int emitted, out _);

        Assert.Equal(0, emitted);
        Assert.Contains("bridge providers active: none", outp);
        Assert.Contains("dotnet-web skipped: no dotnet-web bridge evidence", outp);
    }

    [Fact]
    public void Bridge_NotOnBridge_JsonIncludesCapabilityDiagnostics()
    {
        var capability = new BridgeCapabilityReport(
            ActiveProviders: [],
            SkippedProviders: [new BridgeProviderSkip("dotnet-web", "no dotnet-web bridge evidence")],
            Notes: [],
            EvidenceCounts: new Dictionary<string, int>(StringComparer.Ordinal));
        var index = BuildBridgeIndex(
            new[] { ("x", "Loner", "src/Loner.cs", 1) },
            Array.Empty<ScoredEdge>(),
            new Dictionary<string, BridgeNode>(StringComparer.Ordinal),
            capability);

        string json = TraceTool.Run(index, ResolverFor(index),
            target: "Loner", mode: "bridge", to: null, depth: 3, limit: 20, fullFormat: false, json: true,
            out int emitted, out _);

        Assert.Equal(0, emitted);
        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        Assert.Contains("not on a cross-language bridge", root.GetProperty("note").GetString());
        Assert.Equal("dotnet-web", root.GetProperty("provider").GetProperty("skipped_providers")[0].GetProperty("provider_id").GetString());
        JsonElement diagnostic = Assert.Single(root.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("not_on_bridge", diagnostic.GetProperty("code").GetString());
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
    public void Trace_ExplicitWorkspaceId_DefaultsEnsureFreshTrue_AndRoutesToTargetIndex()
    {
        var currentIndex = EmptyIndex();
        var targetIndex = BuildSymbolIndex(
            new[]
            {
                ("a", "Alpha", "method", "src/A.cs", 10),
                ("b", "Beta", "method", "src/B.cs", 20),
            },
            new[] { ("a", "b") });
        string currentRoot = Path.Combine(Path.GetTempPath(), "miller-current-" + Guid.NewGuid().ToString("N"));
        string targetRoot = Path.Combine(Path.GetTempPath(), "miller-target-" + Guid.NewGuid().ToString("N"));
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(currentIndex, "current.db", "current-ws", currentRoot),
            ("target-ws", ReadToolRoutingTestSupport.ContextFor(targetIndex, "target.db", "target-ws", targetRoot)));
        var tool = new TraceTool(provider);

        string output = tool.Trace("Alpha", workspace_id: "target-ws");

        Assert.Equal("target-ws", provider.LastWorkspaceId);
        Assert.True(provider.LastEnsureFresh);
        Assert.StartsWith("workspace: target-ws\n", output);
        Assert.DoesNotContain(targetRoot, output);
        Assert.Contains("Beta", output);
    }

    [Fact]
    public void Ctor_RequiresWorkspaceIndexProvider()
    {
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(EmptyIndex(), "current.db", "current-ws", "/current"));

        var tool = new TraceTool(provider);
        Assert.NotNull(tool);

        Assert.Throws<ArgumentNullException>(() => new TraceTool(null!));
    }

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
