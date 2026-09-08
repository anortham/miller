using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Miller.Core.Contracts;
using Miller.Core.Graph;
using Miller.Core.References;
using Miller.Core.Resolver;
using Miller.Indexing;
using Miller.Server.Resolution;
using Miller.Server.Tools;
using Xunit;

namespace Miller.Tests.Tools;

public sealed class TraceAdversarialChallengeTests
{
    private static MillerRepositoryIndex BuildIndex(
        IReadOnlyList<IndexedSymbol> symbols,
        IReadOnlyList<GraphEdge> edges,
        BridgeGraph? bridge = null)
    {
        return MillerRepositoryIndex.Build(
            symbols,
            edges,
            bridge ?? BridgeGraph.Build([], new Dictionary<string, BridgeNode>(StringComparer.Ordinal)));
    }

    private static SmartTargetResolver ResolverFor(MillerRepositoryIndex index) => new(index);

    private static GraphNode N(string id) => new(id, IsTest: false);
    private static GraphEdge E(string from, string to, string kind = "calls") =>
        new(from, to, kind);

    // =========================================================================
    // 1. N3 Class Target Start Set Probes
    // =========================================================================

    [Fact]
    public void ClassTarget_DirectClassLevelEdge_FindsDirectPath_WithoutViaAnnotation()
    {
        // Class symbol has direct instantiation edge to child component.
        // It also contains callable members that do NOT lead to target.
        IndexedSymbol[] symbols =
        [
            new(0, "class-parent", "ParentComponent", "class ParentComponent", "class", "qml", "Parent.qml", 1, 50, null, false),
            new(1, "method-helper1", "DoWork", "void DoWork()", "method", "qml", "Parent.qml", 5, 10, "class-parent", false),
            new(2, "method-helper2", "Calculate", "int Calculate()", "method", "qml", "Parent.qml", 15, 20, "class-parent", false),
            new(3, "class-child", "ChildComponent", "class ChildComponent", "class", "qml", "Child.qml", 1, 30, null, false),
        ];
        var index = BuildIndex(symbols, [new GraphEdge("class-parent", "class-child", "instantiates")]);

        // Compact verification
        string compact = TraceTool.Run(
            index,
            ResolverFor(index),
            target: "ParentComponent",
            scope: null,
            mode: "path",
            to: "ChildComponent",
            depth: 3,
            limit: 10,
            fullFormat: false,
            json: false,
            pathKind: "dependency",
            out int emitted,
            out _);

        Assert.Equal(2, emitted);
        Assert.Contains("# trace path ParentComponent -> ChildComponent (1 hop(s))", compact, StringComparison.Ordinal);
        Assert.DoesNotContain("(via ", compact, StringComparison.Ordinal);

        // JSON verification
        string json = TraceTool.Run(
            index,
            ResolverFor(index),
            target: "ParentComponent",
            scope: null,
            mode: "path",
            to: "ChildComponent",
            depth: 3,
            limit: 10,
            fullFormat: false,
            json: true,
            pathKind: "dependency",
            out emitted,
            out _);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("hops").GetInt32());
        Assert.False(root.TryGetProperty("from_member", out _));
        Assert.False(root.TryGetProperty("from_member_id", out _));
        Assert.Equal("ParentComponent", root.GetProperty("resolved_target").GetProperty("name").GetString());
    }

    [Fact]
    public void ClassTarget_MemberLevelEdge_FindsWinningMember_WithViaAnnotation()
    {
        // Class symbol has NO direct edge to sink.
        // Member Calculate calls ResultSink.
        IndexedSymbol[] symbols =
        [
            new(0, "class-service", "CalculatorService", "class CalculatorService", "class", "csharp", "calc.cs", 1, 50, null, false),
            new(1, "method-init", "Initialize", "void Initialize()", "method", "csharp", "calc.cs", 5, 10, "class-service", false),
            new(2, "method-calc", "Calculate", "void Calculate()", "method", "csharp", "calc.cs", 15, 25, "class-service", false),
            new(3, "method-sink", "ResultSink", "void ResultSink()", "method", "csharp", "sink.cs", 1, 10, null, false),
        ];
        var index = BuildIndex(symbols, [new GraphEdge("method-calc", "method-sink", "calls")]);

        // Compact verification
        string compact = TraceTool.Run(
            index,
            ResolverFor(index),
            target: "CalculatorService",
            scope: null,
            mode: "path",
            to: "ResultSink",
            depth: 3,
            limit: 10,
            fullFormat: false,
            json: false,
            pathKind: "call",
            out int emitted,
            out _);

        Assert.Equal(2, emitted);
        Assert.Contains("# trace path CalculatorService (via Calculate) -> ResultSink (1 hop(s))", compact, StringComparison.Ordinal);

        // JSON verification
        string json = TraceTool.Run(
            index,
            ResolverFor(index),
            target: "CalculatorService",
            scope: null,
            mode: "path",
            to: "ResultSink",
            depth: 3,
            limit: 10,
            fullFormat: false,
            json: true,
            pathKind: "call",
            out emitted,
            out _);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("hops").GetInt32());
        Assert.Equal("Calculate", root.GetProperty("from_member").GetString());
        Assert.Equal("method-calc", root.GetProperty("from_member_id").GetString());
    }

    [Fact]
    public void ClassTarget_MemberShorterThanClass_PicksMemberPath()
    {
        // Class has a 2-hop path: class-root -> intermediate -> target
        // Member has a 1-hop path: method-fast -> target
        IndexedSymbol[] symbols =
        [
            new(0, "class-root", "RootComponent", "class RootComponent", "class", "csharp", "root.cs", 1, 50, null, false),
            new(1, "method-fast", "FastAction", "void FastAction()", "method", "csharp", "root.cs", 5, 10, "class-root", false),
            new(2, "sym-mid", "MiddleNode", "class MiddleNode", "class", "csharp", "mid.cs", 1, 10, null, false),
            new(3, "sym-target", "TargetNode", "void TargetNode()", "method", "csharp", "target.cs", 1, 10, null, false),
        ];
        var edges = new List<GraphEdge>
        {
            new("class-root", "sym-mid", "instantiates"),
            new("sym-mid", "sym-target", "calls"),
            new("method-fast", "sym-target", "calls"),
        };
        var index = BuildIndex(symbols, edges);

        string compact = TraceTool.Run(
            index,
            ResolverFor(index),
            target: "RootComponent",
            scope: null,
            mode: "path",
            to: "TargetNode",
            depth: 3,
            limit: 10,
            fullFormat: false,
            json: false,
            pathKind: "dependency",
            out int emitted,
            out _);

        Assert.Equal(2, emitted);
        Assert.Contains("# trace path RootComponent (via FastAction) -> TargetNode (1 hop(s))", compact, StringComparison.Ordinal);
    }

    [Fact]
    public void ClassTarget_CallableMembers_TruncationNotice_WhenOver100()
    {
        // Class with 120 callable members. Member 35 connects to target.
        var symbols = new List<IndexedSymbol>
        {
            new(0, "class-giant", "GiantService", "class GiantService", "class", "csharp", "giant.cs", 1, 1000, null, false),
            new(1, "sym-dest", "Destination", "void Destination()", "method", "csharp", "dest.cs", 1, 5, null, false),
        };
        for (int i = 1; i <= 120; i++)
        {
            string id = $"fn-{i:D3}";
            string name = $"Func{i:D3}";
            symbols.Add(new(i + 1, id, name, $"void {name}()", "method", "csharp", "giant.cs", i * 5, i * 5 + 4, "class-giant", false));
        }

        var edges = new List<GraphEdge>
        {
            new("fn-035", "sym-dest", "calls")
        };
        var index = BuildIndex(symbols, edges);

        // Compact verification: must show truncation notice [callable members truncated: 100 of 120 examined]
        string compact = TraceTool.Run(
            index,
            ResolverFor(index),
            target: "GiantService",
            scope: null,
            mode: "path",
            to: "Destination",
            depth: 3,
            limit: 10,
            fullFormat: false,
            json: false,
            pathKind: "call",
            out int emitted,
            out _);

        Assert.Equal(2, emitted);
        Assert.Contains("# trace path GiantService (via Func035) -> Destination (1 hop(s))", compact, StringComparison.Ordinal);
        Assert.Contains("[callable members truncated: 100 of 120 examined]", compact, StringComparison.Ordinal);

        // JSON verification
        string json = TraceTool.Run(
            index,
            ResolverFor(index),
            target: "GiantService",
            scope: null,
            mode: "path",
            to: "Destination",
            depth: 3,
            limit: 10,
            fullFormat: false,
            json: true,
            pathKind: "call",
            out emitted,
            out _);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("start_members_truncated").GetBoolean());
        Assert.Equal(120, root.GetProperty("start_members_total").GetInt32());
        Assert.Equal(100, root.GetProperty("start_members_examined").GetInt32());
        Assert.Equal("Func035", root.GetProperty("from_member").GetString());
    }

    [Fact]
    public void ClassTarget_CallableMembers_Exactly100_NoTruncationNotice()
    {
        // Class with exactly 100 callable members.
        var symbols = new List<IndexedSymbol>
        {
            new(0, "class-exact", "ExactService", "class ExactService", "class", "csharp", "exact.cs", 1, 1000, null, false),
            new(1, "sym-dest", "Destination", "void Destination()", "method", "csharp", "dest.cs", 1, 5, null, false),
        };
        for (int i = 1; i <= 100; i++)
        {
            string id = $"fn-{i:D3}";
            string name = $"Func{i:D3}";
            symbols.Add(new(i + 1, id, name, $"void {name}()", "method", "csharp", "exact.cs", i * 5, i * 5 + 4, "class-exact", false));
        }

        var edges = new List<GraphEdge>
        {
            new("fn-100", "sym-dest", "calls")
        };
        var index = BuildIndex(symbols, edges);

        string compact = TraceTool.Run(
            index,
            ResolverFor(index),
            target: "ExactService",
            scope: null,
            mode: "path",
            to: "Destination",
            depth: 3,
            limit: 10,
            fullFormat: false,
            json: false,
            pathKind: "call",
            out int emitted,
            out _);

        Assert.Equal(2, emitted);
        Assert.Contains("# trace path ExactService (via Func100) -> Destination (1 hop(s))", compact, StringComparison.Ordinal);
        Assert.DoesNotContain("[callable members truncated", compact, StringComparison.Ordinal);

        string json = TraceTool.Run(
            index,
            ResolverFor(index),
            target: "ExactService",
            scope: null,
            mode: "path",
            to: "Destination",
            depth: 3,
            limit: 10,
            fullFormat: false,
            json: true,
            pathKind: "call",
            out emitted,
            out _);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.False(root.TryGetProperty("start_members_truncated", out _));
    }

    // =========================================================================
    // 2. N3 Route Diagnostics & File Slash Routing
    // =========================================================================

    [Fact]
    public void Bridge_UnobservedRoute_ReturnsRouteNotObserved()
    {
        string endpointId = BridgeGraph.SynthesizeId(BridgeNodeKind.Endpoint, "/api/orders");
        var extra = new Dictionary<string, BridgeNode>(StringComparer.Ordinal)
        {
            [endpointId] = new BridgeNode(endpointId, BridgeNodeKind.Endpoint, "/api/orders", "server.ts", 1),
        };
        var bridge = BridgeGraph.Build([], extra);
        var index = BuildIndex([], [], bridge);

        // 1. Completely unobserved route
        string json = TraceTool.Run(
            index,
            ResolverFor(index),
            target: "/api/nonexistent",
            mode: "bridge",
            to: null,
            depth: 2,
            limit: 10,
            fullFormat: false,
            json: true,
            out int emitted,
            out _);

        Assert.Equal(0, emitted);
        using (var doc = JsonDocument.Parse(json))
        {
            var root = doc.RootElement;
            Assert.True(root.TryGetProperty("diagnostics", out var diagElem));
            var diag = diagElem.EnumerateArray().First();
            Assert.Equal("route_not_observed", diag.GetProperty("code").GetString());
            Assert.Contains("no frontend or backend route facts observed for /api/nonexistent", diag.GetProperty("message").GetString(), StringComparison.Ordinal);
        }

        // 2. Root route "/"
        string jsonRoot = TraceTool.Run(
            index,
            ResolverFor(index),
            target: "/",
            mode: "bridge",
            to: null,
            depth: 2,
            limit: 10,
            fullFormat: false,
            json: true,
            out emitted,
            out _);

        Assert.Equal(0, emitted);
        using (var docRoot = JsonDocument.Parse(jsonRoot))
        {
            var root = docRoot.RootElement;
            Assert.True(root.TryGetProperty("diagnostics", out var diagElem));
            var diag = diagElem.EnumerateArray().First();
            Assert.Equal("route_not_observed", diag.GetProperty("code").GetString());
        }
    }

    [Fact]
    public void Bridge_FileTargetWithSlashes_NotHijackedIntoRouteDiagnostics()
    {
        IndexedSymbol[] symbols =
        [
            new(0, "file-orders", "src/Orders/OrderService.cs", "file", "file", "csharp", "src/Orders/OrderService.cs", 1, 100, null, false),
            new(1, "sym-order-svc", "OrderService", "class OrderService", "class", "csharp", "src/Orders/OrderService.cs", 5, 50, "file-orders", false),
        ];
        var bridge = BridgeGraph.Build([], new Dictionary<string, BridgeNode>(StringComparer.Ordinal));
        var index = BuildIndex(symbols, [], bridge);

        string[] fileTargets =
        [
            "src/Orders/OrderService.cs",     // indexed file with slashes
            "src/Services/PaymentService.cs",  // non-indexed file with known .cs extension
            "./src/local/component.ts",        // starts with ./
            "../external/module.js",           // starts with ../
        ];

        foreach (string fileTarget in fileTargets)
        {
            string json = TraceTool.Run(
                index,
                ResolverFor(index),
                target: fileTarget,
                mode: "bridge",
                to: null,
                depth: 2,
                limit: 10,
                fullFormat: false,
                json: true,
                out int emitted,
                out _);

            Assert.Equal(0, emitted);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("diagnostics", out var diagElem) && diagElem.GetArrayLength() > 0)
            {
                string? code = diagElem.EnumerateArray().First().GetProperty("code").GetString();
                Assert.NotEqual("route_not_observed", code);
                Assert.NotEqual("route_no_backend_match", code);
                Assert.NotEqual("route_no_frontend_match", code);
                Assert.NotEqual("route_no_bridge_link", code);
            }
        }
    }

    // =========================================================================
    // 3. Multi-Source BFS Probes (SymbolGraph & GraphTraversal)
    // =========================================================================

    [Fact]
    public void ShortestPathWithEvidence_MultiSource_DiverseTopologies()
    {
        // Topology:
        //   Start_Disconnected (no outgoing edges)
        //   Start_Cycle -> C1 -> C2 -> Start_Cycle (cycle)
        //   Start_Long -> L1 -> L2 -> Target (3 hops)
        //   Start_Short -> S1 -> Target      (2 hops, winning)
        var graph = SymbolGraph.Build(
            [
                N("Start_Disconnected"),
                N("Start_Cycle"), N("C1"), N("C2"),
                N("Start_Long"), N("L1"), N("L2"),
                N("Start_Short"), N("S1"),
                N("Target")
            ],
            [
                E("Start_Cycle", "C1"), E("C1", "C2"), E("C2", "Start_Cycle"),
                E("Start_Long", "L1"), E("L1", "L2"), E("L2", "Target"),
                E("Start_Short", "S1"), E("S1", "Target"),
            ]);

        var path = graph.ShortestPathWithEvidence(
            fromNodes: ["Start_Disconnected", "Start_Cycle", "Start_Long", "Start_Short"],
            to: "Target",
            maxDepth: 5,
            edge => true);

        Assert.NotNull(path);
        Assert.Equal(["Start_Short", "S1", "Target"], path.Nodes);
        Assert.Equal(2, path.Edges.Count);
        Assert.Equal("calls", path.Edges[0].Kind);
    }

    [Fact]
    public void ShortestPathWithEvidence_MultiSource_TargetInFromNodes_ZeroHops()
    {
        var graph = SymbolGraph.Build(
            [N("A"), N("B"), N("Target"), N("C")],
            [E("A", "Target"), E("B", "Target")]);

        // Target in the middle
        var path1 = graph.ShortestPathWithEvidence(
            fromNodes: ["A", "B", "Target", "C"],
            to: "Target",
            maxDepth: 5,
            edge => true);

        Assert.NotNull(path1);
        Assert.Equal(["Target"], path1.Nodes);
        Assert.Empty(path1.Edges);

        // Target at the start
        var path2 = graph.ShortestPathWithEvidence(
            fromNodes: ["Target", "A"],
            to: "Target",
            maxDepth: 5,
            edge => true);

        Assert.NotNull(path2);
        Assert.Equal(["Target"], path2.Nodes);
        Assert.Empty(path2.Edges);

        // Target at the end
        var path3 = graph.ShortestPathWithEvidence(
            fromNodes: ["A", "Target"],
            to: "Target",
            maxDepth: 5,
            edge => true);

        Assert.NotNull(path3);
        Assert.Equal(["Target"], path3.Nodes);
        Assert.Empty(path3.Edges);
    }

    [Fact]
    public void ShortestPathWithEvidence_MultiSource_ShallowDepthAndBounds()
    {
        var graph = SymbolGraph.Build(
            [N("Start"), N("Mid"), N("Target")],
            [E("Start", "Mid"), E("Mid", "Target")]);

        // Distance is 2 hops.
        // maxDepth: 1 -> unreachable
        Assert.Null(graph.ShortestPathWithEvidence(["Start"], "Target", maxDepth: 1, _ => true));

        // maxDepth: 0 -> unreachable
        Assert.Null(graph.ShortestPathWithEvidence(["Start"], "Target", maxDepth: 0, _ => true));

        // maxDepth: -1 -> unreachable
        Assert.Null(graph.ShortestPathWithEvidence(["Start"], "Target", maxDepth: -1, _ => true));

        // maxDepth: 2 -> reachable
        var path = graph.ShortestPathWithEvidence(["Start"], "Target", maxDepth: 2, _ => true);
        Assert.NotNull(path);
        Assert.Equal(2, path.Edges.Count);
    }

    [Fact]
    public void ShortestPathWithEvidence_MultiSource_EdgeFilter()
    {
        // StartA -type_usage-> Target (1 hop)
        // StartB -calls-> B1 -calls-> Target (2 hops)
        var graph = SymbolGraph.Build(
            [N("StartA"), N("StartB"), N("B1"), N("Target")],
            [
                E("StartA", "Target", "type_usage"),
                E("StartB", "B1", "calls"),
                E("B1", "Target", "calls"),
            ]);

        // When filtering to calls only, StartA cannot reach Target, StartB wins
        var pathCalls = graph.ShortestPathWithEvidence(
            fromNodes: ["StartA", "StartB"],
            to: "Target",
            maxDepth: 5,
            edge => edge.EdgeKind == "calls");

        Assert.NotNull(pathCalls);
        Assert.Equal(["StartB", "B1", "Target"], pathCalls.Nodes);

        // When allowing all edges, StartA wins (1 hop)
        var pathAll = graph.ShortestPathWithEvidence(
            fromNodes: ["StartA", "StartB"],
            to: "Target",
            maxDepth: 5,
            edge => true);

        Assert.NotNull(pathAll);
        Assert.Equal(["StartA", "Target"], pathAll.Nodes);
    }

    [Fact]
    public void ShortestPathWithEvidence_MultiSource_MissingNodesAndDuplicates()
    {
        var graph = SymbolGraph.Build(
            [N("A"), N("B"), N("Target")],
            [E("A", "Target"), E("B", "Target")]);

        // Duplicates and missing nodes in fromNodes
        var path = graph.ShortestPathWithEvidence(
            fromNodes: ["missing1", "B", "missing2", "B", "A"],
            to: "Target",
            maxDepth: 5,
            edge => true);

        Assert.NotNull(path);
        // B was first valid start node, so it reaches Target in 1 hop
        Assert.Equal(["B", "Target"], path.Nodes);

        // Empty fromNodes
        Assert.Null(graph.ShortestPathWithEvidence([], "Target", maxDepth: 5, _ => true));

        // All missing fromNodes
        Assert.Null(graph.ShortestPathWithEvidence(["m1", "m2"], "Target", maxDepth: 5, _ => true));

        // Missing target
        Assert.Null(graph.ShortestPathWithEvidence(["A"], "nonexistent_target", maxDepth: 5, _ => true));
    }

    [Fact]
    public void ShortestPathWithEvidence_MultiSource_MatchesInterfaceDefault()
    {
        // Compare SymbolGraph multi-source BFS implementation with ISymbolGraphReachability default implementation
        var graph = SymbolGraph.Build(
            [
                N("S1"), N("S2"), N("S3"),
                N("H1"), N("H2"), N("H3"),
                N("Target")
            ],
            [
                E("S1", "H1"), E("H1", "H2"), E("H2", "Target"), // 3 hops
                E("S2", "H3"), E("H3", "Target"),                 // 2 hops
                // S3 disconnected
            ]);

        string[] starts = ["S1", "S2", "S3"];
        ISymbolGraphReachability iface = graph;

        GraphPath? bfsResult = graph.ShortestPathWithEvidence(starts, "Target", 5, _ => true);
        GraphPath? ifaceResult = iface.ShortestPathWithEvidence(starts, "Target", 5, _ => true);

        Assert.NotNull(bfsResult);
        Assert.NotNull(ifaceResult);
        Assert.Equal(bfsResult.Nodes, ifaceResult.Nodes);
        Assert.Equal(bfsResult.Edges.Count, ifaceResult.Edges.Count);
    }

    [Fact]
    public void ClassTarget_CallMode_SuggestsDependencyFallback_WhenDependencyPathExists()
    {
        // Class has member that uses IInterface, which calls Target.
        // Class -> Member -> IInterface --type_usage--> Target.
        IndexedSymbol[] symbols =
        [
            new(0, "class-root", "RootComponent", "class RootComponent", "class", "csharp", "root.cs", 1, 50, null, false),
            new(1, "method-worker", "DoWork", "void DoWork()", "method", "csharp", "root.cs", 5, 10, "class-root", false),
            new(2, "interface-contract", "IContract", "interface IContract", "interface", "csharp", "contract.cs", 1, 10, null, false),
            new(3, "sym-target", "TargetNode", "void TargetNode()", "method", "csharp", "target.cs", 1, 10, null, false),
        ];
        var edges = new List<GraphEdge>
        {
            new("method-worker", "interface-contract", "type_usage"),
            new("interface-contract", "sym-target", "calls"),
        };
        var index = BuildIndex(symbols, edges);

        string json = TraceTool.Run(
            index,
            ResolverFor(index),
            target: "RootComponent",
            scope: null,
            mode: "path",
            to: "TargetNode",
            depth: 3,
            limit: 10,
            fullFormat: false,
            json: true,
            pathKind: "call",
            out int emitted,
            out _);

        Assert.Equal(0, emitted);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("diagnostics", out var diagElem));
        var diag = diagElem.EnumerateArray().First();
        Assert.Equal("no_call_path", diag.GetProperty("code").GetString());
        Assert.Equal(2, root.GetProperty("dependency_path_hops").GetInt32());
    }

    [Fact]
    public void FileTarget_ExpandsMembersUpTo100_WithTruncationNotice()
    {
        var symbols = new List<IndexedSymbol>
        {
            new(0, "file-huge", "src/HugeFile.cs", "file", "file", "csharp", "src/HugeFile.cs", 1, 2000, null, false),
            new(1, "sym-target", "TargetNode", "void TargetNode()", "method", "csharp", "target.cs", 1, 5, null, false),
        };
        for (int i = 1; i <= 110; i++)
        {
            string id = $"fn-file-{i:D3}";
            string name = $"FileFunc{i:D3}";
            symbols.Add(new(i + 1, id, name, $"void {name}()", "method", "csharp", "src/HugeFile.cs", i * 10, i * 10 + 5, "file-huge", false));
        }

        var edges = new List<GraphEdge>
        {
            new("fn-file-020", "sym-target", "calls")
        };
        var index = BuildIndex(symbols, edges);

        string compact = TraceTool.Run(
            index,
            ResolverFor(index),
            target: "src/HugeFile.cs",
            scope: null,
            mode: "path",
            to: "TargetNode",
            depth: 3,
            limit: 10,
            fullFormat: false,
            json: false,
            pathKind: "call",
            out int emitted,
            out _);

        Assert.Equal(2, emitted);
        Assert.Contains("# trace path src/HugeFile.cs (via FileFunc020) -> TargetNode (1 hop(s))", compact, StringComparison.Ordinal);
        Assert.Contains("[callable members truncated: 100 of 110 examined]", compact, StringComparison.Ordinal);
    }

    [Fact]
    public void ShortestPathWithEvidence_MultiSource_DeterministicTieBreakByOrderOfStarts()
    {
        // Start1 -> Target (1 hop)
        // Start2 -> Target (1 hop)
        var graph = SymbolGraph.Build(
            [N("Start1"), N("Start2"), N("Target")],
            [E("Start1", "Target"), E("Start2", "Target")]);

        // Start1 first in fromNodes -> Start1 wins
        var path1 = graph.ShortestPathWithEvidence(["Start1", "Start2"], "Target", 5, _ => true);
        Assert.NotNull(path1);
        Assert.Equal(["Start1", "Target"], path1.Nodes);

        // Start2 first in fromNodes -> Start2 wins
        var path2 = graph.ShortestPathWithEvidence(["Start2", "Start1"], "Target", 5, _ => true);
        Assert.NotNull(path2);
        Assert.Equal(["Start2", "Target"], path2.Nodes);
    }
}
