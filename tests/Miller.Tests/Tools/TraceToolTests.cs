using Miller.Core.Contracts;
using Miller.Core.Graph;
using Miller.Core.References;
using Miller.Core.Resolver;
using Miller.Indexing;
using Miller.Server.Resolution;
using Miller.Server.Telemetry;
using Miller.Server.Tools;
using Miller.Tests;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using Xunit;

namespace Miller.Tests.Tools;

/// <summary>
/// Fast/pure unit tests for the <c>trace</c> tool's <see cref="TraceTool.Run"/> core (M4 Task 10). The trace modes
/// (path / refs / bridge) plus the no-path case, the load-bearing honesty flags ([verb-unknown] / [ambiguous]),
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

    // Build an index whose bridge graph comes from the REAL BridgeGraphBuilder run over structural facts, so
    // observation nodes carry per-provider provenance exactly as production does. The provenance-scoped route
    // diagnostics under test need it; a hand-built BridgeGraph.Build node map carries none and pools instead.
    private static MillerRepositoryIndex BuildBridgeIndexFromStructuralFacts(
        IReadOnlyList<Miller.Core.Contracts.SymbolDetail> symbols,
        IReadOnlyList<StructuralFactRecord> facts)
    {
        var bridge = BridgeGraphBuilder.Build(
            symbols, typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);
        return MillerRepositoryIndex.Build(Array.Empty<IndexedSymbol>(), Array.Empty<GraphEdge>(), bridge);
    }

    private static Miller.Core.Contracts.SymbolDetail DetailMethod(string id, string name, string parentClassName, string file) =>
        new(id, name, "method", file, Signature: name, Namespace: "Api.Controllers", IsTest: false, ParentClassName: parentClassName);

    private static Miller.Core.Contracts.SymbolDetail DetailFunction(string id, string name, string file) =>
        new(id, name, "function", file, Signature: name, Namespace: null, IsTest: false, ParentClassName: null);

    private static StructuralFactRecord StructuralFact(
        string factId, string patternId, string language, string path, string? containingSymbolId,
        int startLine, string metadataJson)
    {
        using var document = JsonDocument.Parse(metadataJson);
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            metadata[property.Name] = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : property.Value.GetRawText();
        }
        return new StructuralFactRecord(
            FactId: factId, PatternId: patternId, Language: language, Path: path,
            CaptureName: "framework.route", NodeKind: "node", ContainingSymbolId: containingSymbolId,
            Span: new StructuralFactSpan(startLine, 1, startLine, 1, startLine * 10, startLine * 10 + 1),
            Confidence: 1.0, Metadata: metadata);
    }

    private static string ReadTelemetryMetadata(string telemetryDb)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = telemetryDb,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT metadata_json FROM tool_telemetry WHERE tool = 'trace';";
        return (string)cmd.ExecuteScalar()!;
    }

    [Fact]
    public void Trace_TargetNotFoundDiagnosticAction_BoundsLongTarget()
    {
        var index = BuildSymbolIndex(
            new[] { ("a", "Alpha", "method", "src/A.cs", 1) },
            Array.Empty<(string, string)>());
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(index, "current.db", "current-ws", "/repo"));
        var tool = new TraceTool(provider);
        string target = new('x', 500);

        using var document = JsonDocument.Parse(tool.Trace(target, mode: "path", to: "Alpha", format: "json"));
        JsonElement diagnostic = document.RootElement.GetProperty("diagnostic");
        string call = diagnostic
            .GetProperty("next_actions")[0]
            .GetProperty("call")
            .GetString()!;

        Assert.Equal("unresolved_target", diagnostic.GetProperty("code").GetString());
        Assert.DoesNotContain(new string('x', 161), call, StringComparison.Ordinal);
        Assert.Contains(new string('x', 160), call, StringComparison.Ordinal);
    }

    [Fact]
    public void Trace_ResolvedEmptyPathDiagnostic_DoesNotSuggestResolvingTarget()
    {
        var index = BuildSymbolIndex(
            new[]
            {
                ("a", "Alpha", "method", "src/A.cs", 1),
                ("b", "Beta", "method", "src/B.cs", 1),
            },
            Array.Empty<(string, string)>());
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(index, "current.db", "current-ws", "/repo"));
        var tool = new TraceTool(provider);

        using var document = JsonDocument.Parse(tool.Trace("Alpha", mode: "path", to: "Beta", format: "json"));
        JsonElement diagnostic = document.RootElement.GetProperty("diagnostic");
        string[] calls = diagnostic.GetProperty("next_actions")
            .EnumerateArray()
            .Select(action => action.GetProperty("call").GetString()!)
            .ToArray();

        Assert.Equal("no_path", diagnostic.GetProperty("code").GetString());
        Assert.DoesNotContain(calls, call => call.Contains("search(query=\"Alpha\")", StringComparison.Ordinal));
        Assert.Contains(calls, call => call.Contains("trace(target=\"Alpha\", mode=\"refs\")", StringComparison.Ordinal));
    }

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
        Assert.Contains("Next:", outp);
        Assert.Contains("trace target=\"Alpha\" mode=\"refs\"", outp);
        Assert.Contains("trace target=\"Gamma\" mode=\"refs\"", outp);
        Assert.Contains("search query=\"Alpha Gamma\" mode=\"source\"", outp);
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
        JsonElement[] actions = root.GetProperty("next_actions").EnumerateArray().ToArray();
        Assert.Equal(3, actions.Length);
        Assert.Equal("trace", actions[0].GetProperty("tool").GetString());
        Assert.Equal("Alpha", actions[0].GetProperty("args").GetProperty("target").GetString());
        Assert.Equal("refs", actions[0].GetProperty("args").GetProperty("mode").GetString());
        Assert.Contains("source endpoint", actions[0].GetProperty("reason").GetString());
        Assert.Equal("search", actions[2].GetProperty("tool").GetString());
        Assert.Equal("Alpha Gamma", actions[2].GetProperty("args").GetProperty("query").GetString());
        Assert.Equal("source", actions[2].GetProperty("args").GetProperty("mode").GetString());
    }

    [Fact]
    public void Path_NoConnection_WithDepthBump_StillIncludesSourceFallback()
    {
        var index = BuildSymbolIndex(
            new[]
            {
                ("a", "Alpha", "method", "src/A.cs", 1),
                ("c", "Gamma", "method", "src/C.cs", 3),
            },
            Array.Empty<(string, string)>());

        string outp = TraceTool.Run(index, ResolverFor(index),
            target: "Alpha", mode: "path", to: "Gamma", depth: 1, limit: 20, fullFormat: false,
            out int emitted, out _);

        Assert.Equal(0, emitted);
        Assert.Contains("trace target=\"Alpha\" mode=\"refs\"", outp);
        Assert.Contains("trace target=\"Gamma\" mode=\"refs\"", outp);
        Assert.Contains("trace target=\"Alpha\" mode=\"path\" to=\"Gamma\" depth=\"2\"", outp);
        Assert.Contains("search query=\"Alpha Gamma\" mode=\"source\"", outp);
    }

    [Fact]
    public void Path_NoConnection_WithDepthBump_JsonStillIncludesSourceFallback()
    {
        var index = BuildSymbolIndex(
            new[]
            {
                ("a", "Alpha", "method", "src/A.cs", 1),
                ("c", "Gamma", "method", "src/C.cs", 3),
            },
            Array.Empty<(string, string)>());

        string json = TraceTool.Run(index, ResolverFor(index),
            target: "Alpha", mode: "path", to: "Gamma", depth: 1, limit: 20, fullFormat: false, json: true,
            out int emitted, out _);

        Assert.Equal(0, emitted);
        using var doc = JsonDocument.Parse(json);
        JsonElement[] actions = doc.RootElement.GetProperty("next_actions").EnumerateArray().ToArray();
        Assert.Equal(4, actions.Length);
        Assert.Equal("trace", actions[2].GetProperty("tool").GetString());
        Assert.Equal("path", actions[2].GetProperty("args").GetProperty("mode").GetString());
        Assert.Equal("2", actions[2].GetProperty("args").GetProperty("depth").GetString());
        Assert.Equal("search", actions[3].GetProperty("tool").GetString());
        Assert.Equal("Alpha Gamma", actions[3].GetProperty("args").GetProperty("query").GetString());
        Assert.Equal("source", actions[3].GetProperty("args").GetProperty("mode").GetString());
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

    // ---------- mode: refs ----------

    [Fact]
    public void Refs_RendersDefinitionAndFilteredFallbackReferences()
    {
        var index = BuildSymbolIndex(
            new[]
            {
                ("a", "Alpha", "method", "src/A.cs", 1),
                ("caller", "CallerMethod", "method", "src/Caller.cs", 5),
            },
            Array.Empty<(string, string)>());
        var references = new[]
        {
            new SymbolRef("Alpha", "call", "src/Caller.cs", 10, "caller"),
            new SymbolRef("Alpha", "type_usage", "src/Types.cs", 20, "types"),
        };

        string outp = TraceTool.Run(index, ResolverFor(index),
            target: "Alpha", scope: null, mode: "refs", to: null, depth: 3, limit: 20,
            fullFormat: false, json: false, referenceKind: "call", includeDefinition: true,
            readReferences: _ => references,
            out int emitted, out int visited);

        Assert.Equal(1, emitted);
        Assert.Equal(1, visited);
        Assert.Contains("# trace refs Alpha (1 reference(s), exact=0, fallback=1, kind=call)", outp);
        Assert.Contains("definition:", outp);
        Assert.Contains("Alpha  method  src/A.cs:1", outp);
        Assert.Contains("src/Caller.cs:10  call  in=CallerMethod  [fallback source=name_fallback confidence=0.50]", outp);
        Assert.DoesNotContain("containing=", outp);
        Assert.DoesNotContain("src/Types.cs", outp);
    }

    [Fact]
    public void Refs_Json_SeparatesExactEvidenceFromFallback()
    {
        var index = BuildSymbolIndex(
            new[]
            {
                ("a", "Alpha", "method", "src/A.cs", 1),
                ("caller", "CallerMethod", "method", "src/Caller.cs", 5),
            },
            Array.Empty<(string, string)>());
        var exact = new ReferenceEvidence(
            "a",
            "caller",
            "src/Caller.cs",
            10,
            4,
            10,
            9,
            100,
            105,
            ReferenceKind.Call,
            "call",
            ReferenceEvidenceSource.IdentifierResolution,
            2,
            0.9,
            ReferenceResolutionStatus.Exact);
        var evidence = new ReferenceEvidenceSet(
            [exact],
            [],
            new ReferenceEvidenceCoverage(1, 1, 1, 0, 0, 1, false, false, ReferenceFallbackStatus.NoCandidates));

        string json = TraceTool.Run(
            index,
            ResolverFor(index),
            target: "Alpha",
            scope: null,
            mode: "refs",
            to: null,
            depth: 3,
            limit: 20,
            fullFormat: false,
            json: true,
            referenceKind: null,
            includeDefinition: true,
            readReferenceEvidence: (_, _) => evidence,
            out int emitted,
            out int visited);

        Assert.Equal(1, emitted);
        Assert.Equal(1, visited);
        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        JsonElement reference = Assert.Single(root.GetProperty("exact_references").EnumerateArray());
        Assert.Equal("a", reference.GetProperty("target_symbol_id").GetString());
        Assert.Equal("exact", reference.GetProperty("resolution_status").GetString());
        Assert.Equal("identifier_resolution", reference.GetProperty("source").GetString());
        Assert.Equal(2, reference.GetProperty("resolution_tier").GetInt32());
        Assert.Equal(0.9, reference.GetProperty("confidence").GetDouble());
        Assert.Empty(root.GetProperty("fallback_references").EnumerateArray());
    }

    [Fact]
    public void Refs_Json_ExplainsAmbiguousFallbackSuppressionWithoutCallingItLimitTruncation()
    {
        var index = BuildSymbolIndex(
            new[] { ("a", "Alpha", "method", "src/A.cs", 1) },
            Array.Empty<(string, string)>());
        var exact = new ReferenceEvidence(
            "a",
            null,
            "src/Caller.cs",
            10,
            4,
            10,
            9,
            100,
            105,
            ReferenceKind.Call,
            "call",
            ReferenceEvidenceSource.IdentifierDirect,
            null,
            1,
            ReferenceResolutionStatus.Exact);
        var evidence = new ReferenceEvidenceSet(
            [exact],
            [],
            new ReferenceEvidenceCoverage(
                1,
                1,
                1,
                1,
                0,
                2,
                false,
                false,
                ReferenceFallbackStatus.SuppressedAmbiguousName));

        string json = TraceTool.Run(
            index,
            ResolverFor(index),
            target: "Alpha",
            scope: null,
            mode: "refs",
            to: null,
            depth: 3,
            limit: 20,
            fullFormat: false,
            json: true,
            referenceKind: null,
            includeDefinition: true,
            readReferenceEvidence: (_, _) => evidence,
            out _,
            out _);

        using var document = JsonDocument.Parse(json);
        JsonElement diagnostic = Assert.Single(
            document.RootElement.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal(
            "fallback_suppressed_ambiguous_name",
            diagnostic.GetProperty("code").GetString());
        Assert.Contains(
            "suppressed because the target name is ambiguous",
            diagnostic.GetProperty("message").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Refs_Compact_DisclosesHomonymFallbackSafetyWithoutCandidateRows()
    {
        var index = BuildSymbolIndex(
            new[] { ("a", "Alpha", "method", "src/A.cs", 1) },
            Array.Empty<(string, string)>());
        var evidence = new ReferenceEvidenceSet(
            [
                new ReferenceEvidence(
                    "a",
                    null,
                    "src/Caller.cs",
                    10,
                    1,
                    10,
                    6,
                    100,
                    105,
                    ReferenceKind.Call,
                    "call",
                    ReferenceEvidenceSource.IdentifierDirect,
                    null,
                    1,
                    ReferenceResolutionStatus.Exact),
            ],
            [],
            new ReferenceEvidenceCoverage(
                1,
                1,
                1,
                0,
                0,
                2,
                false,
                false,
                ReferenceFallbackStatus.SuppressedAmbiguousName));

        string output = TraceTool.Run(
            index,
            ResolverFor(index),
            target: "Alpha",
            scope: null,
            mode: "refs",
            to: null,
            depth: 3,
            limit: 20,
            fullFormat: false,
            json: false,
            referenceKind: null,
            includeDefinition: false,
            readReferenceEvidence: (_, _) => evidence,
            out _,
            out _);

        Assert.Contains(
            "same-name fallback is disabled because the target name is ambiguous",
            output,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Refs_Json_UsesArtifactBoundStatelessContinuationAboveOutputBudget()
    {
        var index = BuildSymbolIndex(
            new[] { ("a", "Alpha", "method", "src/A.cs", 1) },
            Array.Empty<(string, string)>());
        ReferenceEvidence[] references = Enumerable.Range(1, 30)
            .Select(line => new ReferenceEvidence(
                "a",
                null,
                $"src/Caller{line}.cs",
                line,
                1,
                line,
                6,
                line * 10,
                line * 10 + 5,
                ReferenceKind.Call,
                "call",
                ReferenceEvidenceSource.IdentifierDirect,
                null,
                1,
                ReferenceResolutionStatus.Exact,
                "csharp"))
            .ToArray();
        var snapshot = new ReferenceEvidenceSnapshot("artifact", 42);
        ReferenceEvidenceSet ReadPage(IndexedSymbol _, ReferenceEvidenceQuery query)
        {
            ReferenceEvidence[] page = references
                .Skip(query.ExactOffset)
                .Take(query.Bounds.ExactLimit)
                .ToArray();
            return new ReferenceEvidenceSet(
                page,
                [],
                new ReferenceEvidenceCoverage(
                    references.Length,
                    references.Length,
                    page.Length,
                    0,
                    0,
                    1,
                    references.Length > query.ExactOffset + page.Length,
                    false,
                    ReferenceFallbackStatus.NoCandidates),
                snapshot);
        }

        string first = TraceTool.Run(
            index,
            ResolverFor(index),
            target: "Alpha",
            scope: null,
            mode: "refs",
            to: null,
            depth: 3,
            limit: 30,
            fullFormat: false,
            json: true,
            referenceKind: null,
            includeDefinition: true,
            readReferenceEvidence: ReadPage,
            workspaceId: "workspace",
            snapshot,
            continuation: null,
            out int firstCount,
            out _);
        using var firstDocument = JsonDocument.Parse(first);
        string token = firstDocument.RootElement.GetProperty("continuation").GetString()!;

        string compact = TraceTool.Run(
            index,
            ResolverFor(index),
            target: "Alpha",
            scope: null,
            mode: "refs",
            to: null,
            depth: 3,
            limit: 30,
            fullFormat: false,
            json: false,
            referenceKind: null,
            includeDefinition: true,
            readReferenceEvidence: ReadPage,
            workspaceId: "workspace",
            snapshot,
            continuation: null,
            out _,
            out _);

        Assert.Contains("workspace_id=\"workspace\"", compact, StringComparison.Ordinal);

        string second = TraceTool.Run(
            index,
            ResolverFor(index),
            target: "Alpha",
            scope: null,
            mode: "refs",
            to: null,
            depth: 3,
            limit: 30,
            fullFormat: false,
            json: true,
            referenceKind: null,
            includeDefinition: true,
            readReferenceEvidence: ReadPage,
            workspaceId: "workspace",
            snapshot,
            continuation: token,
            out int secondCount,
            out _);
        using var secondDocument = JsonDocument.Parse(second);

        Assert.InRange(firstCount, 1, 24);
        Assert.Equal(30 - firstCount, secondCount);
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(first), 1, 16 * 1024);
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(second), 1, 16 * 1024);
        Assert.Equal(
            30,
            firstDocument.RootElement.GetProperty("exact_references").GetArrayLength() +
            secondDocument.RootElement.GetProperty("exact_references").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, secondDocument.RootElement.GetProperty("continuation").ValueKind);
    }

    [Fact]
    public void Refs_Json_ContinuationKeepsEachPageWithinSixteenKiB()
    {
        var index = BuildSymbolIndex(
            new[] { ("a", "Alpha", "method", "src/A.cs", 1) },
            Array.Empty<(string, string)>());
        string directory = new('x', 700);
        ReferenceEvidence[] references = Enumerable.Range(1, 30)
            .Select(line => new ReferenceEvidence(
                "a",
                null,
                $"src/{directory}/Caller{line}.cs",
                line,
                1,
                line,
                6,
                line * 10,
                line * 10 + 5,
                ReferenceKind.Call,
                "call",
                ReferenceEvidenceSource.IdentifierDirect,
                null,
                1,
                ReferenceResolutionStatus.Exact,
                "csharp"))
            .ToArray();
        var snapshot = new ReferenceEvidenceSnapshot("artifact", 42);
        ReferenceEvidenceSet ReadPage(IndexedSymbol _, ReferenceEvidenceQuery query)
        {
            ReferenceEvidence[] page = references
                .Skip(query.ExactOffset)
                .Take(query.Bounds.ExactLimit)
                .ToArray();
            return new ReferenceEvidenceSet(
                page,
                [],
                new ReferenceEvidenceCoverage(
                    references.Length,
                    references.Length,
                    page.Length,
                    0,
                    0,
                    1,
                    references.Length > query.ExactOffset + page.Length,
                    false,
                    ReferenceFallbackStatus.NoCandidates),
                snapshot);
        }

        string json = TraceTool.Run(
            index,
            ResolverFor(index),
            target: "Alpha",
            scope: null,
            mode: "refs",
            to: null,
            depth: 3,
            limit: 30,
            fullFormat: false,
            json: true,
            referenceKind: null,
            includeDefinition: true,
            readReferenceEvidence: ReadPage,
            workspaceId: "workspace",
            snapshot,
            continuation: null,
            out int emitted,
            out _);
        using var document = JsonDocument.Parse(json);

        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(json), 1, 16 * 1024);
        Assert.InRange(emitted, 1, 23);
        Assert.Equal(JsonValueKind.String, document.RootElement.GetProperty("continuation").ValueKind);
    }

    [Fact]
    public void Refs_Json_LimitTruncationNeverEscapesSixteenKiBBudget()
    {
        var index = BuildSymbolIndex(
            new[] { ("a", "Alpha", "method", "src/A.cs", 1) },
            Array.Empty<(string, string)>());
        var snapshot = new ReferenceEvidenceSnapshot("artifact", 42);

        for (int directoryLength = 50; directoryLength <= 500; directoryLength++)
        {
            string directory = new('x', directoryLength);
            ReferenceEvidence[] references = Enumerable.Range(1, 21)
                .Select(line => new ReferenceEvidence(
                    "a",
                    null,
                    $"src/{directory}/Caller{line}.cs",
                    line,
                    1,
                    line,
                    6,
                    line * 10,
                    line * 10 + 5,
                    ReferenceKind.Call,
                    "call",
                    ReferenceEvidenceSource.IdentifierDirect,
                    null,
                    1,
                    ReferenceResolutionStatus.Exact,
                    "csharp"))
                .ToArray();
            ReferenceEvidenceSet ReadPage(IndexedSymbol _, ReferenceEvidenceQuery query)
            {
                ReferenceEvidence[] page = references
                    .Skip(query.ExactOffset)
                    .Take(query.Bounds.ExactLimit)
                    .ToArray();
                return new ReferenceEvidenceSet(
                    page,
                    [],
                    new ReferenceEvidenceCoverage(
                        references.Length,
                        references.Length,
                        page.Length,
                        0,
                        0,
                        1,
                        references.Length > query.ExactOffset + page.Length,
                        false,
                        ReferenceFallbackStatus.NoCandidates),
                    snapshot);
            }

            string json = TraceTool.Run(
                index,
                ResolverFor(index),
                target: "Alpha",
                scope: null,
                mode: "refs",
                to: null,
                depth: 3,
                limit: 20,
                fullFormat: false,
                json: true,
                referenceKind: null,
                includeDefinition: true,
                readReferenceEvidence: ReadPage,
                workspaceId: "workspace",
                snapshot,
                continuation: null,
                out _,
                out _);

            Assert.True(
                System.Text.Encoding.UTF8.GetByteCount(json) <= 16 * 1024,
                $"directoryLength={directoryLength}");
        }
    }

    [Fact]
    public void Refs_Compact_UnresolvableContainingRendersNoInSegment()
    {
        var index = BuildSymbolIndex(
            new[] { ("a", "Alpha", "method", "src/A.cs", 1) },
            Array.Empty<(string, string)>());
        var references = new[]
        {
            new SymbolRef("Alpha", "call", "src/Caller.cs", 10, "deadbeefdeadbeefdeadbeefdeadbeef"),
        };

        string outp = TraceTool.Run(index, ResolverFor(index),
            target: "Alpha", scope: null, mode: "refs", to: null, depth: 3, limit: 20,
            fullFormat: false, json: false, referenceKind: "call", includeDefinition: false,
            readReferences: _ => references,
            out int emitted, out int visited);

        Assert.Equal(1, emitted);
        Assert.Contains("src/Caller.cs:10  call", outp);
        Assert.DoesNotContain("in=", outp);
        Assert.DoesNotContain("deadbeef", outp);
    }

    [Fact]
    public void Refs_Compact_MissingLineDoesNotFabricateLineZero()
    {
        var index = BuildSymbolIndex(
            new[] { ("a", "Alpha", "method", "src/A.cs", 1) },
            Array.Empty<(string, string)>());
        var evidence = new ReferenceEvidenceSet(
            [
                new ReferenceEvidence(
                    "a",
                    null,
                    "src/Unknown.cs",
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    ReferenceKind.Call,
                    "call",
                    ReferenceEvidenceSource.IdentifierDirect,
                    null,
                    1,
                    ReferenceResolutionStatus.Exact),
            ],
            [],
            new ReferenceEvidenceCoverage(
                1,
                1,
                1,
                0,
                0,
                1,
                false,
                false,
                ReferenceFallbackStatus.NoCandidates));

        string output = TraceTool.Run(
            index,
            ResolverFor(index),
            target: "Alpha",
            scope: null,
            mode: "refs",
            to: null,
            depth: 3,
            limit: 20,
            fullFormat: false,
            json: false,
            referenceKind: null,
            includeDefinition: false,
            readReferenceEvidence: (_, _) => evidence,
            out _,
            out _);

        Assert.Contains("src/Unknown.cs  call", output, StringComparison.Ordinal);
        Assert.DoesNotContain("src/Unknown.cs:0", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Refs_Json_RendersStructuredReferenceRows()
    {
        var index = BuildSymbolIndex(
            new[]
            {
                ("a", "Alpha", "method", "src/A.cs", 1),
                ("caller", "CallerMethod", "method", "src/Caller.cs", 5),
            },
            Array.Empty<(string, string)>());
        var references = new[]
        {
            new SymbolRef("Alpha", "call", "src/Caller.cs", 10, "caller"),
            new SymbolRef("Alpha", "type_usage", "src/Types.cs", 20, null),
        };

        string json = TraceTool.Run(index, ResolverFor(index),
            target: "Alpha", scope: null, mode: "refs", to: null, depth: 3, limit: 20,
            fullFormat: false, json: true, referenceKind: null, includeDefinition: true,
            readReferences: _ => references,
            out int emitted, out int visited);

        Assert.Equal(2, emitted);
        Assert.Equal(2, visited);
        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        Assert.Equal("refs", root.GetProperty("mode").GetString());
        Assert.True(root.GetProperty("include_definition").GetBoolean());
        Assert.Equal("Alpha", root.GetProperty("resolved_target").GetProperty("name").GetString());
        Assert.Equal("Alpha", Assert.Single(root.GetProperty("nodes").EnumerateArray()).GetProperty("name").GetString());
        Assert.Empty(root.GetProperty("links").EnumerateArray());

        JsonElement[] refs = root.GetProperty("references").EnumerateArray().ToArray();
        Assert.Equal(2, refs.Length);
        Assert.Equal(JsonValueKind.Null, refs[0].GetProperty("target_symbol_id").ValueKind);
        Assert.Equal("call", refs[0].GetProperty("kind").GetString());
        Assert.Equal("src/Caller.cs", refs[0].GetProperty("file").GetString());
        Assert.Equal(10, refs[0].GetProperty("line").GetInt32());
        Assert.Equal("caller", refs[0].GetProperty("containing_symbol_id").GetString());
        Assert.Equal("CallerMethod", refs[0].GetProperty("containing_symbol_name").GetString());
        Assert.Equal("fallback", refs[0].GetProperty("resolution_status").GetString());
        Assert.Equal("name_fallback", refs[0].GetProperty("source").GetString());
        Assert.Equal(0.5, refs[0].GetProperty("confidence").GetDouble());
        Assert.Equal(JsonValueKind.Null, refs[1].GetProperty("containing_symbol_id").ValueKind);
        Assert.Equal(JsonValueKind.Null, refs[1].GetProperty("containing_symbol_name").ValueKind);
        Assert.Empty(root.GetProperty("exact_references").EnumerateArray());
        Assert.Equal(2, root.GetProperty("fallback_references").GetArrayLength());
        Assert.Equal("available", root.GetProperty("reference_coverage").GetProperty("fallback_status").GetString());
    }

    [Fact]
    public void Refs_Json_CanOmitDefinitionNode()
    {
        var index = BuildSymbolIndex(
            new[] { ("a", "Alpha", "method", "src/A.cs", 1) },
            Array.Empty<(string, string)>());

        string json = TraceTool.Run(index, ResolverFor(index),
            target: "Alpha", scope: null, mode: "refs", to: null, depth: 0, limit: 20,
            fullFormat: false, json: true, referenceKind: null, includeDefinition: false,
            readReferences: _ => new[] { new SymbolRef("Alpha", "call", "src/Caller.cs", 10, "caller") },
            out int emitted, out int visited);

        Assert.Equal(1, emitted);
        Assert.Equal(1, visited);
        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        Assert.False(root.GetProperty("include_definition").GetBoolean());
        Assert.Equal(1, root.GetProperty("depth").GetInt32());
        Assert.Empty(root.GetProperty("nodes").EnumerateArray());
        Assert.Single(root.GetProperty("references").EnumerateArray());
    }

    [Fact]
    public void Refs_UnknownReferenceKind_RendersUsage()
    {
        var index = BuildSymbolIndex(
            new[] { ("a", "Alpha", "method", "src/A.cs", 1) },
            Array.Empty<(string, string)>());

        string outp = TraceTool.Run(index, ResolverFor(index),
            target: "Alpha", scope: null, mode: "refs", to: null, depth: 3, limit: 20,
            fullFormat: false, json: false, referenceKind: "definitely-not-a-kind", includeDefinition: true,
            readReferences: _ => Array.Empty<SymbolRef>(),
            out int emitted, out int visited);

        Assert.Equal(0, emitted);
        Assert.Equal(0, visited);
        Assert.Contains("reference_kind must be one of", outp);
    }

    [Fact]
    public void Refs_Empty_RendersRecoveryHint()
    {
        var index = BuildSymbolIndex(
            new[] { ("a", "Alpha", "method", "src/A.cs", 1) },
            Array.Empty<(string, string)>());

        string outp = TraceTool.Run(index, ResolverFor(index),
            target: "Alpha", scope: null, mode: "refs", to: null, depth: 3, limit: 20,
            fullFormat: false, json: false, referenceKind: null, includeDefinition: true,
            readReferences: _ => Array.Empty<SymbolRef>(),
            out int emitted, out int visited);

        Assert.Equal(0, emitted);
        Assert.Equal(0, visited);
        Assert.Contains("No extracted refs for 'Alpha'", outp, StringComparison.Ordinal);
        Assert.Contains("Next:", outp, StringComparison.Ordinal);
        Assert.Contains("search query=\"Alpha\" mode=\"source\"", outp, StringComparison.Ordinal);
        Assert.Contains("inspect target=\"Alpha\" depth=\"full\"", outp, StringComparison.Ordinal);
    }

    [Fact]
    public void Refs_Empty_JsonCarriesNextActions()
    {
        var index = BuildSymbolIndex(
            new[] { ("a", "Alpha", "method", "src/A.cs", 1) },
            Array.Empty<(string, string)>());

        string json = TraceTool.Run(index, ResolverFor(index),
            target: "Alpha", scope: null, mode: "refs", to: null, depth: 3, limit: 20,
            fullFormat: false, json: true, referenceKind: null, includeDefinition: true,
            readReferences: _ => Array.Empty<SymbolRef>(),
            out int emitted, out int visited);

        Assert.Equal(0, emitted);
        Assert.Equal(0, visited);
        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        JsonElement diagnostic = Assert.Single(root.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("no_references", diagnostic.GetProperty("code").GetString());
        JsonElement[] actions = root.GetProperty("next_actions").EnumerateArray().ToArray();
        Assert.Equal(2, actions.Length);
        Assert.Equal("search", actions[0].GetProperty("tool").GetString());
        Assert.Equal("Alpha", actions[0].GetProperty("args").GetProperty("query").GetString());
        Assert.Equal("source", actions[0].GetProperty("args").GetProperty("mode").GetString());
        Assert.Equal("inspect", actions[1].GetProperty("tool").GetString());
        Assert.Equal("Alpha", actions[1].GetProperty("args").GetProperty("target").GetString());
        Assert.Equal("full", actions[1].GetProperty("args").GetProperty("depth").GetString());
    }

    [Fact]
    public void Refs_NonEmpty_AppendsImpactNudge_ForNonTestTarget()
    {
        var index = BuildSymbolIndex(
            new[]
            {
                ("a", "Alpha", "method", "src/A.cs", 1),
                ("caller", "CallerMethod", "method", "src/Caller.cs", 5),
            },
            Array.Empty<(string, string)>());
        var references = new[]
        {
            new SymbolRef("Alpha", "call", "src/Caller.cs", 10, "caller"),
        };

        string outp = TraceTool.Run(index, ResolverFor(index),
            target: "Alpha", scope: null, mode: "refs", to: null, depth: 3, limit: 20,
            fullFormat: false, json: false, referenceKind: null, includeDefinition: true,
            readReferences: _ => references,
            out int emitted, out _);

        Assert.Equal(1, emitted);
        Assert.Contains("fallback (unresolved):", outp, StringComparison.Ordinal);
        Assert.EndsWith("next: impact target=\"Alpha\" — before editing", outp, StringComparison.Ordinal);
        Assert.Equal(1, outp.Split('\n').Count(line => line.StartsWith("next:", StringComparison.Ordinal)));
    }

    [Fact]
    public void Refs_NonEmpty_TruncationNote_KeepsImpactNudgeAsFinalLine()
    {
        var index = BuildSymbolIndex(
            new[] { ("a", "Alpha", "method", "src/A.cs", 1) },
            Array.Empty<(string, string)>());
        var references = new[]
        {
            new SymbolRef("Alpha", "call", "src/One.cs", 10, null),
            new SymbolRef("Alpha", "call", "src/Two.cs", 20, null),
        };

        string outp = TraceTool.Run(index, ResolverFor(index),
            target: "Alpha", scope: null, mode: "refs", to: null, depth: 3, limit: 1,
            fullFormat: false, json: false, referenceKind: null, includeDefinition: false,
            readReferences: _ => references,
            out int emitted, out _);

        Assert.Equal(1, emitted);
        Assert.Contains("reference trace truncated by limit.", outp, StringComparison.Ordinal);
        Assert.EndsWith("next: impact target=\"Alpha\" — before editing", outp, StringComparison.Ordinal);
    }

    [Fact]
    public void Refs_NonEmpty_TestTarget_SuppressesImpactNudge()
    {
        var indexed = new[]
        {
            new IndexedSymbol(
                DocId: 0, SymbolId: "a", Name: "AlphaTests", Signature: "method AlphaTests()",
                Kind: "method", Language: "csharp", FilePath: "tests/A.cs", StartLine: 1, EndLine: 1,
                ParentId: null, IsTest: true),
        };
        var index = MillerRepositoryIndex.Build(indexed, Array.Empty<GraphEdge>());
        var references = new[]
        {
            new SymbolRef("AlphaTests", "call", "tests/Caller.cs", 10, null),
        };

        string outp = TraceTool.Run(index, ResolverFor(index),
            target: "AlphaTests", scope: null, mode: "refs", to: null, depth: 3, limit: 20,
            fullFormat: false, json: false, referenceKind: null, includeDefinition: true,
            readReferences: _ => references,
            out int emitted, out _);

        Assert.Equal(1, emitted);
        Assert.Contains("fallback (unresolved):", outp, StringComparison.Ordinal);
        Assert.DoesNotContain("next:", outp, StringComparison.Ordinal);
    }

    [Fact]
    public void Refs_Empty_HasNoImpactNudge()
    {
        var index = BuildSymbolIndex(
            new[] { ("a", "Alpha", "method", "src/A.cs", 1) },
            Array.Empty<(string, string)>());

        string outp = TraceTool.Run(index, ResolverFor(index),
            target: "Alpha", scope: null, mode: "refs", to: null, depth: 3, limit: 20,
            fullFormat: false, json: false, referenceKind: null, includeDefinition: true,
            readReferences: _ => Array.Empty<SymbolRef>(),
            out int emitted, out _);

        Assert.Equal(0, emitted);
        // The empty-refs path keeps its existing recovery hint / next_actions; no impact nudge.
        Assert.DoesNotContain("next: impact", outp, StringComparison.Ordinal);
    }

    [Fact]
    public void Refs_NonEmpty_Json_HasNoImpactNudge()
    {
        var index = BuildSymbolIndex(
            new[]
            {
                ("a", "Alpha", "method", "src/A.cs", 1),
                ("caller", "CallerMethod", "method", "src/Caller.cs", 5),
            },
            Array.Empty<(string, string)>());
        var references = new[]
        {
            new SymbolRef("Alpha", "call", "src/Caller.cs", 10, "caller"),
        };

        string json = TraceTool.Run(index, ResolverFor(index),
            target: "Alpha", scope: null, mode: "refs", to: null, depth: 3, limit: 20,
            fullFormat: false, json: true, referenceKind: null, includeDefinition: true,
            readReferences: _ => references,
            out int emitted, out _);

        Assert.Equal(1, emitted);
        using var doc = JsonDocument.Parse(json); // still well-formed JSON
        Assert.DoesNotContain("next: impact", json, StringComparison.Ordinal);
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
    public void Bridge_RouteStringTarget_EmitsOnlyEdgesForThatRoute()
    {
        var dismiss = MakeScored(
            BridgeKind.Hits,
            new EdgeRef("api/users/{}", "client", "web/api.ts", Resolved("client")),
            SymbolRef("dismiss", "DismissUser", "src/UsersController.cs"),
            ConfidenceBand.High, 0.9);
        var preview = MakeScored(
            BridgeKind.Hits,
            new EdgeRef("api/users/{}/preview", "client", "web/api.ts", Resolved("client")),
            SymbolRef("preview", "PreviewUser", "src/UsersController.cs"),
            ConfidenceBand.High, 0.9);

        var index = BuildBridgeIndex(
            new[]
            {
                ("client", "usersApi", "web/api.ts", 5),
                ("dismiss", "DismissUser", "src/UsersController.cs", 12),
                ("preview", "PreviewUser", "src/UsersController.cs", 18),
            },
            new[] { dismiss, preview },
            new Dictionary<string, BridgeNode>(StringComparer.Ordinal));

        string outp = TraceTool.Run(index, ResolverFor(index),
            target: "/api/users/{userId}", mode: "bridge", to: null, depth: 2, limit: 20, fullFormat: false,
            out int emitted, out _);

        Assert.Equal(1, emitted);
        Assert.Contains("usersApi  --route-->  DismissUser", outp);
        Assert.DoesNotContain("PreviewUser", outp);
    }

    [Fact]
    public void Bridge_RouteStringTarget_JsonEmitsOnlyEdgesForThatRoute()
    {
        var dismiss = MakeScored(
            BridgeKind.Hits,
            new EdgeRef("api/users/{}", "client", "web/api.ts", Resolved("client")),
            SymbolRef("dismiss", "DismissUser", "src/UsersController.cs"),
            ConfidenceBand.High, 0.9);
        var preview = MakeScored(
            BridgeKind.Hits,
            new EdgeRef("api/users/{}/preview", "client", "web/api.ts", Resolved("client")),
            SymbolRef("preview", "PreviewUser", "src/UsersController.cs"),
            ConfidenceBand.High, 0.9);

        var index = BuildBridgeIndex(
            new[]
            {
                ("client", "usersApi", "web/api.ts", 5),
                ("dismiss", "DismissUser", "src/UsersController.cs", 12),
                ("preview", "PreviewUser", "src/UsersController.cs", 18),
            },
            new[] { dismiss, preview },
            new Dictionary<string, BridgeNode>(StringComparer.Ordinal));

        string json = TraceTool.Run(index, ResolverFor(index),
            target: "/api/users/{userId}", mode: "bridge", to: null, depth: 2, limit: 20, fullFormat: false, json: true,
            out int emitted, out _);

        Assert.Equal(1, emitted);
        using var doc = JsonDocument.Parse(json);
        JsonElement[] links = doc.RootElement.GetProperty("links").EnumerateArray().ToArray();
        JsonElement link = Assert.Single(links);
        Assert.Equal("DismissUser", link.GetProperty("target_display").GetString());
        Assert.DoesNotContain(doc.RootElement.GetProperty("nodes").EnumerateArray(),
            node => node.GetProperty("display").GetString() == "PreviewUser");
    }

    [Fact]
    public void Bridge_ClientRequestEdge_CompactAndJsonAgreeOnKindBandAndFlags()
    {
        // A matched verb-known client-request edge (fetch -> Next.js route handler symbol): High, no flags.
        var hits = MakeScored(
            BridgeKind.Hits,
            SymbolRef("client", "loadMessages", "web/lib/api.ts"),
            SymbolRef("handler", "GET", "web/app/api/messages/route.ts"),
            ConfidenceBand.High, 0.9);
        var index = BuildBridgeIndex(
            new[]
            {
                ("client", "loadMessages", "web/lib/api.ts", 5),
                ("handler", "GET", "web/app/api/messages/route.ts", 3),
            },
            new[] { hits },
            new Dictionary<string, BridgeNode>(StringComparer.Ordinal));

        string compact = TraceTool.Run(index, ResolverFor(index),
            target: "loadMessages", mode: "bridge", to: null, depth: 2, limit: 20, fullFormat: false,
            out int compactEmitted, out _);
        string json = TraceTool.Run(index, ResolverFor(index),
            target: "loadMessages", mode: "bridge", to: null, depth: 2, limit: 20, fullFormat: false, json: true,
            out int jsonEmitted, out _);

        Assert.Equal(1, compactEmitted);
        Assert.Equal(1, jsonEmitted);
        Assert.Contains("loadMessages  --route-->  GET", compact);
        Assert.Contains("0.90 (High)", compact);
        Assert.DoesNotContain("[verb-unknown]", compact);
        Assert.DoesNotContain("[ambiguous]", compact);

        using var doc = JsonDocument.Parse(json);
        JsonElement link = Assert.Single(doc.RootElement.GetProperty("links").EnumerateArray());
        Assert.Equal("hits", link.GetProperty("kind").GetString());
        Assert.Equal("route", link.GetProperty("label").GetString());
        Assert.Equal("high", link.GetProperty("confidence").GetString());
        Assert.Equal(0.9, link.GetProperty("score").GetDouble(), precision: 5);
        Assert.Empty(link.GetProperty("flags").EnumerateArray());
    }

    [Fact]
    public void Bridge_RouteStringTarget_ModuleScopeFetch_MixedProviders_TracesBothLinks()
    {
        // F3 acceptance: one module-scope fetch("/api/x") (no containing symbol) in a mixed repo — an ASP.NET
        // GET endpoint AND a Next.js route handler on the same route. Both providers must synthesize the SAME
        // source node id for the symbol-less client, so trace "/api/x" finds ONE start and renders BOTH links
        // instead of bailing on divergent starts.
        StructuralFactRecord Fact(string id, string patternId, string path, string containingSymbolId, int line,
            Dictionary<string, string> metadata) =>
            new(id, patternId, "typescript", path, CaptureName: "capture", NodeKind: "node",
                ContainingSymbolId: containingSymbolId,
                Span: new StructuralFactSpan(line, 1, line, 1, line * 10, line * 10 + 1),
                Confidence: 1.0, Metadata: metadata);

        var facts = new List<StructuralFactRecord>
        {
            new("fact-httpget", "aspnet.attribute_route.v1", "csharp", "api/XController.cs",
                CaptureName: "capture", NodeKind: "node", ContainingSymbolId: "sym-getx",
                Span: new StructuralFactSpan(10, 1, 10, 1, 100, 101), Confidence: 1.0,
                Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["attribute_kind"] = "http_method",
                    ["verb"] = "GET",
                    ["effective_route_template"] = "/api/x",
                }),
            Fact("fact-next-get", "nextjs.route_handler.v1", "web/app/api/x/route.ts", "sym-handler", 3,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["framework"] = "nextjs",
                    ["router"] = "app",
                    ["route_path"] = "/api/x",
                    ["verb"] = "GET",
                    ["verb_source"] = "attested",
                }),
            Fact("fact-module-fetch", "http.client_request.v1", "web/lib/boot.ts", string.Empty, 5,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["client"] = "fetch",
                    ["framework"] = "fetch",
                    ["target_path"] = "/api/x",
                    ["url_kind"] = "path",
                    ["verb"] = "GET",
                    ["verb_source"] = "default",
                }),
        };
        var details = new List<Miller.Core.Contracts.SymbolDetail>
        {
            new("sym-getx", "GetX", "method", "api/XController.cs", "Task<IResult> GetX()",
                Namespace: "Api.Controllers", IsTest: false, ParentClassName: "XController"),
            new("sym-handler", "GET", "function", "web/app/api/x/route.ts", "GET",
                Namespace: null, IsTest: false, ParentClassName: null),
        };

        var bridge = BridgeGraphBuilder.Build(
            details, typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);
        var indexed = new List<IndexedSymbol>
        {
            new(DocId: 0, SymbolId: "sym-getx", Name: "GetX", Signature: "Task<IResult> GetX()", Kind: "method",
                Language: "csharp", FilePath: "api/XController.cs", StartLine: 10, EndLine: 12, ParentId: null, IsTest: false),
            new(DocId: 1, SymbolId: "sym-handler", Name: "GET", Signature: "function GET", Kind: "function",
                Language: "typescript", FilePath: "web/app/api/x/route.ts", StartLine: 3, EndLine: 5, ParentId: null, IsTest: false),
        };
        var index = MillerRepositoryIndex.Build(indexed, Array.Empty<GraphEdge>(), bridge);

        string outp = TraceTool.Run(index, ResolverFor(index),
            target: "/api/x", mode: "bridge", to: null, depth: 2, limit: 20, fullFormat: false,
            out int emitted, out _);

        // ONE start node with BOTH Hits edges — no divergent-start bail, no false no-bridge-link diagnostic.
        Assert.Equal(2, emitted);
        Assert.Contains("api/x  --route-->  GetX", outp);
        // The handler link ("GET" node, two spaces before the score) — distinct from the "GetX" line.
        Assert.Contains("api/x  --route-->  GET  ", outp);
        Assert.DoesNotContain("Multiple bridge starts", outp);
        Assert.DoesNotContain("no_bridge_link", outp);
    }

    [Fact]
    public void Bridge_NuxtVerbUnknownClientRequestEdge_CompactAndJsonAgreeOnFlags()
    {
        // A suffix-less Nuxt server route answers every method: the edge is honest-Medium and flagged
        // verb-unknown in BOTH compact and JSON output.
        string endpointId = BridgeGraph.SynthesizeId(BridgeNodeKind.Endpoint, "/api/notes");
        var hits = MakeScored(
            BridgeKind.Hits,
            SymbolRef("client", "loadNotes", "app/lib/api.ts"),
            NonSymbolRef("/api/notes"),
            ConfidenceBand.Medium, 0.75, verbUnknown: true);
        var extra = new Dictionary<string, BridgeNode>(StringComparer.Ordinal)
        {
            [endpointId] = new BridgeNode(endpointId, BridgeNodeKind.Endpoint, "/api/notes", "server/api/notes.ts", 1),
        };
        var index = BuildBridgeIndex(
            new[] { ("client", "loadNotes", "app/lib/api.ts", 5) },
            new[] { hits },
            extra);

        string compact = TraceTool.Run(index, ResolverFor(index),
            target: "loadNotes", mode: "bridge", to: null, depth: 2, limit: 20, fullFormat: false,
            out int compactEmitted, out _);
        string json = TraceTool.Run(index, ResolverFor(index),
            target: "loadNotes", mode: "bridge", to: null, depth: 2, limit: 20, fullFormat: false, json: true,
            out int jsonEmitted, out _);

        Assert.Equal(1, compactEmitted);
        Assert.Equal(1, jsonEmitted);
        Assert.Contains("loadNotes  --route-->  /api/notes", compact);
        Assert.Contains("[verb-unknown]", compact);
        Assert.Contains("0.75 (Medium)", compact);

        using var doc = JsonDocument.Parse(json);
        JsonElement link = Assert.Single(doc.RootElement.GetProperty("links").EnumerateArray());
        Assert.Equal("hits", link.GetProperty("kind").GetString());
        Assert.Equal("route", link.GetProperty("label").GetString());
        Assert.Equal("medium", link.GetProperty("confidence").GetString());
        Assert.Equal(new[] { "verb_unknown" },
            link.GetProperty("flags").EnumerateArray().Select(flag => flag.GetString()).ToArray());
        Assert.Contains(doc.RootElement.GetProperty("nodes").EnumerateArray(),
            node => node.GetProperty("kind").GetString() == "endpoint" &&
                    node.GetProperty("display").GetString() == "/api/notes");
    }

    [Fact]
    public void Bridge_RouteStringTarget_NextJsNavigation_StartsFromRouteAndRendersNavigatesTo()
    {
        string referenceId = BridgeGraph.SynthesizeId(BridgeNodeKind.TsType, "/settings");
        string fileRouteId = BridgeGraph.SynthesizeId(BridgeNodeKind.FileRoute, "/settings");
        var navigation = MakeScored(
            BridgeKind.NavigatesTo,
            NonSymbolRef("/settings"),
            NonSymbolRef("/settings"),
            ConfidenceBand.High,
            0.9);
        var extra = new Dictionary<string, BridgeNode>(StringComparer.Ordinal)
        {
            [referenceId] = new BridgeNode(referenceId, BridgeNodeKind.TsType, "/settings", "web/Nav.tsx", 10),
            [fileRouteId] = new BridgeNode(fileRouteId, BridgeNodeKind.FileRoute, "/settings", "web/app/settings/page.tsx", 1),
        };
        var index = BuildBridgeIndex(
            Array.Empty<(string symbolId, string name, string file, int line)>(),
            new[] { navigation },
            extra);

        string outp = TraceTool.Run(index, ResolverFor(index),
            target: "/settings", mode: "bridge", to: null, depth: 2, limit: 20, fullFormat: false,
            out int emitted, out _);

        Assert.Equal(1, emitted);
        Assert.Contains("# trace bridge /settings", outp);
        Assert.Contains("/settings  --navigates_to-->  /settings", outp);
        Assert.DoesNotContain("--route-->", outp);
    }

    [Fact]
    public void Bridge_RouteStringTarget_FileRouteNavigation_JsonUsesNavigatesToAndFileRoute()
    {
        string referenceId = BridgeGraph.SynthesizeId(BridgeNodeKind.TsType, "/settings");
        string fileRouteId = BridgeGraph.SynthesizeId(BridgeNodeKind.FileRoute, "/settings");
        var navigation = MakeScored(
            BridgeKind.NavigatesTo,
            NonSymbolRef("/settings"),
            NonSymbolRef("/settings"),
            ConfidenceBand.High,
            0.9);
        var extra = new Dictionary<string, BridgeNode>(StringComparer.Ordinal)
        {
            [referenceId] = new BridgeNode(referenceId, BridgeNodeKind.TsType, "/settings", "web/Nav.tsx", 10),
            [fileRouteId] = new BridgeNode(fileRouteId, BridgeNodeKind.FileRoute, "/settings", "web/app/settings/page.tsx", 1),
        };
        var index = BuildBridgeIndex(
            Array.Empty<(string symbolId, string name, string file, int line)>(),
            new[] { navigation },
            extra);

        string json = TraceTool.Run(index, ResolverFor(index),
            target: "/settings", mode: "bridge", to: null, depth: 2, limit: 20, fullFormat: false, json: true,
            out int emitted, out _);

        Assert.Equal(1, emitted);
        using var doc = JsonDocument.Parse(json);
        JsonElement link = Assert.Single(doc.RootElement.GetProperty("links").EnumerateArray());
        Assert.Equal("navigates_to", link.GetProperty("kind").GetString());
        Assert.Equal("navigates_to", link.GetProperty("label").GetString());
        Assert.Contains(doc.RootElement.GetProperty("nodes").EnumerateArray(),
            node => node.GetProperty("kind").GetString() == "file_route" &&
                    node.GetProperty("display").GetString() == "/settings");
    }

    [Fact]
    public void Bridge_RouteStringTarget_NuxtReferenceOnly_JsonExplainsNoFileMatch()
    {
        string referenceId = BridgeGraph.SynthesizeId(BridgeNodeKind.TsType, "/about");
        var extra = new Dictionary<string, BridgeNode>(StringComparer.Ordinal)
        {
            [referenceId] = new BridgeNode(referenceId, BridgeNodeKind.TsType, "/about", "app/components/Nav.vue", 8),
        };
        var capability = new BridgeCapabilityReport(
            ActiveProviders: ["nuxt"],
            SkippedProviders: [],
            Notes: [],
            EvidenceCounts: new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["nuxt.routeReferences"] = 1,
                ["nuxt.fileRoutes"] = 0,
                ["nuxt.candidates"] = 0,
                ["nuxt.ambiguousMatches"] = 0,
            });
        var index = BuildBridgeIndex(
            Array.Empty<(string symbolId, string name, string file, int line)>(),
            Array.Empty<ScoredEdge>(),
            extra,
            capability);

        string json = TraceTool.Run(index, ResolverFor(index),
            target: "/about", mode: "bridge", to: null, depth: 2, limit: 20, fullFormat: false, json: true,
            out int emitted, out _);

        Assert.Equal(0, emitted);
        using var doc = JsonDocument.Parse(json);
        JsonElement diagnostic = Assert.Single(doc.RootElement.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("nuxt_route_no_file_match", diagnostic.GetProperty("code").GetString());
        Assert.Contains("Nuxt route reference exists: /about", diagnostic.GetProperty("message").GetString());
        Assert.Contains("no matching file route fact", diagnostic.GetProperty("message").GetString());
    }

    [Fact]
    public void Bridge_RouteStringTarget_NextJsReferenceOnly_JsonExplainsNoFileMatch()
    {
        string referenceId = BridgeGraph.SynthesizeId(BridgeNodeKind.TsType, "/settings");
        var extra = new Dictionary<string, BridgeNode>(StringComparer.Ordinal)
        {
            [referenceId] = new BridgeNode(referenceId, BridgeNodeKind.TsType, "/settings", "web/Nav.tsx", 10),
        };
        var capability = new BridgeCapabilityReport(
            ActiveProviders: ["nextjs"],
            SkippedProviders: [],
            Notes: [],
            EvidenceCounts: new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["nextjs.routeReferences"] = 1,
                ["nextjs.fileRoutes"] = 0,
                ["nextjs.candidates"] = 0,
                ["nextjs.ambiguousMatches"] = 0,
            });
        var index = BuildBridgeIndex(
            Array.Empty<(string symbolId, string name, string file, int line)>(),
            Array.Empty<ScoredEdge>(),
            extra,
            capability);

        string json = TraceTool.Run(index, ResolverFor(index),
            target: "/settings", mode: "bridge", to: null, depth: 2, limit: 20, fullFormat: false, json: true,
            out int emitted, out _);

        Assert.Equal(0, emitted);
        using var doc = JsonDocument.Parse(json);
        JsonElement diagnostic = Assert.Single(doc.RootElement.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("nextjs_route_no_file_match", diagnostic.GetProperty("code").GetString());
        Assert.Contains("Next.js route reference exists: /settings", diagnostic.GetProperty("message").GetString());
        Assert.Contains("no matching file route fact", diagnostic.GetProperty("message").GetString());
    }

    [Fact]
    public void Bridge_RouteStringTarget_NextJsFileRouteOnly_JsonExplainsNoReferenceMatch()
    {
        string fileRouteId = BridgeGraph.SynthesizeId(BridgeNodeKind.FileRoute, "/settings");
        var extra = new Dictionary<string, BridgeNode>(StringComparer.Ordinal)
        {
            [fileRouteId] = new BridgeNode(fileRouteId, BridgeNodeKind.FileRoute, "/settings", "web/app/settings/page.tsx", 1),
        };
        var capability = new BridgeCapabilityReport(
            ActiveProviders: ["nextjs"],
            SkippedProviders: [],
            Notes: [],
            EvidenceCounts: new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["nextjs.routeReferences"] = 0,
                ["nextjs.fileRoutes"] = 1,
                ["nextjs.candidates"] = 0,
                ["nextjs.ambiguousMatches"] = 0,
            });
        var index = BuildBridgeIndex(
            Array.Empty<(string symbolId, string name, string file, int line)>(),
            Array.Empty<ScoredEdge>(),
            extra,
            capability);

        string json = TraceTool.Run(index, ResolverFor(index),
            target: "/settings", mode: "bridge", to: null, depth: 2, limit: 20, fullFormat: false, json: true,
            out int emitted, out _);

        Assert.Equal(0, emitted);
        using var doc = JsonDocument.Parse(json);
        JsonElement diagnostic = Assert.Single(doc.RootElement.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("nextjs_route_no_reference_match", diagnostic.GetProperty("code").GetString());
        Assert.Contains("Next.js file route exists: /settings", diagnostic.GetProperty("message").GetString());
        Assert.Contains("no matching route reference fact", diagnostic.GetProperty("message").GetString());
    }

    [Fact]
    public void Bridge_RouteStringTarget_VueReferenceOnly_JsonExplainsNoDefinitionMatch()
    {
        string referenceId = BridgeGraph.SynthesizeId(BridgeNodeKind.TsType, "/users/42");
        var extra = new Dictionary<string, BridgeNode>(StringComparer.Ordinal)
        {
            [referenceId] = new BridgeNode(referenceId, BridgeNodeKind.TsType, "/users/42", "web/AppHeader.vue", 8),
        };
        var capability = new BridgeCapabilityReport(
            ActiveProviders: ["vue"],
            SkippedProviders: [],
            Notes: [],
            EvidenceCounts: new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["vue.routeReferences"] = 1,
                ["vue.fileRoutes"] = 0,
                ["vue.candidates"] = 0,
                ["vue.ambiguousMatches"] = 0,
            });
        var index = BuildBridgeIndex(
            Array.Empty<(string symbolId, string name, string file, int line)>(),
            Array.Empty<ScoredEdge>(),
            extra,
            capability);

        string json = TraceTool.Run(index, ResolverFor(index),
            target: "/users/42", mode: "bridge", to: null, depth: 2, limit: 20, fullFormat: false, json: true,
            out int emitted, out _);

        Assert.Equal(0, emitted);
        using var doc = JsonDocument.Parse(json);
        JsonElement diagnostic = Assert.Single(doc.RootElement.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("vue_route_no_file_match", diagnostic.GetProperty("code").GetString());
        Assert.Contains("Vue route reference exists: /users/42", diagnostic.GetProperty("message").GetString());
        Assert.Contains("no matching route definition fact", diagnostic.GetProperty("message").GetString());
    }

    [Fact]
    public void Bridge_RouteStringTarget_VueDefinitionOnly_JsonExplainsNoReferenceMatch()
    {
        string routeId = BridgeGraph.SynthesizeId(BridgeNodeKind.FileRoute, "/users/:id");
        var extra = new Dictionary<string, BridgeNode>(StringComparer.Ordinal)
        {
            [routeId] = new BridgeNode(routeId, BridgeNodeKind.FileRoute, "/users/:id", "web/router.ts", 3),
        };
        var capability = new BridgeCapabilityReport(
            ActiveProviders: ["vue"],
            SkippedProviders: [],
            Notes: [],
            EvidenceCounts: new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["vue.routeReferences"] = 0,
                ["vue.fileRoutes"] = 1,
                ["vue.candidates"] = 0,
                ["vue.ambiguousMatches"] = 0,
            });
        var index = BuildBridgeIndex(
            Array.Empty<(string symbolId, string name, string file, int line)>(),
            Array.Empty<ScoredEdge>(),
            extra,
            capability);

        string json = TraceTool.Run(index, ResolverFor(index),
            target: "/users/42", mode: "bridge", to: null, depth: 2, limit: 20, fullFormat: false, json: true,
            out int emitted, out _);

        Assert.Equal(0, emitted);
        using var doc = JsonDocument.Parse(json);
        JsonElement diagnostic = Assert.Single(doc.RootElement.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("vue_route_no_reference_match", diagnostic.GetProperty("code").GetString());
        Assert.Contains("Vue route definition exists: /users/42", diagnostic.GetProperty("message").GetString());
        Assert.Contains("no matching route reference fact", diagnostic.GetProperty("message").GetString());
    }

    [Fact]
    public void Bridge_RouteStringTarget_NextJsAmbiguousFileRoutes_JsonExplainsAmbiguousFileMatch()
    {
        string referenceId = BridgeGraph.SynthesizeId(BridgeNodeKind.TsType, "/users/123");
        string fileRouteId = BridgeGraph.SynthesizeId(BridgeNodeKind.FileRoute, "/users/[id]");
        var extra = new Dictionary<string, BridgeNode>(StringComparer.Ordinal)
        {
            [referenceId] = new BridgeNode(referenceId, BridgeNodeKind.TsType, "/users/123", "web/Nav.tsx", 10),
            [fileRouteId] = new BridgeNode(fileRouteId, BridgeNodeKind.FileRoute, "/users/[id]", "web/app/users/[id]/page.tsx", 1),
        };
        var capability = new BridgeCapabilityReport(
            ActiveProviders: ["nextjs"],
            SkippedProviders: [],
            Notes: [],
            EvidenceCounts: new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["nextjs.routeReferences"] = 1,
                ["nextjs.fileRoutes"] = 2,
                ["nextjs.candidates"] = 0,
                ["nextjs.ambiguousMatches"] = 1,
            });
        var index = BuildBridgeIndex(
            Array.Empty<(string symbolId, string name, string file, int line)>(),
            Array.Empty<ScoredEdge>(),
            extra,
            capability);

        string json = TraceTool.Run(index, ResolverFor(index),
            target: "/users/123", mode: "bridge", to: null, depth: 2, limit: 20, fullFormat: false, json: true,
            out int emitted, out _);

        Assert.Equal(0, emitted);
        using var doc = JsonDocument.Parse(json);
        JsonElement diagnostic = Assert.Single(doc.RootElement.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("nextjs_route_ambiguous_file_match", diagnostic.GetProperty("code").GetString());
        Assert.Contains("multiple matching file route facts", diagnostic.GetProperty("message").GetString());
    }

    [Fact]
    public void Bridge_RouteStringTarget_NextJsReferenceOnly_WithUnrelatedAmbiguity_JsonExplainsNoFileMatch()
    {
        string referenceId = BridgeGraph.SynthesizeId(BridgeNodeKind.TsType, "/settings");
        string unrelatedFileRouteId = BridgeGraph.SynthesizeId(BridgeNodeKind.FileRoute, "/users/[id]");
        var extra = new Dictionary<string, BridgeNode>(StringComparer.Ordinal)
        {
            [referenceId] = new BridgeNode(referenceId, BridgeNodeKind.TsType, "/settings", "web/Nav.tsx", 10),
            [unrelatedFileRouteId] = new BridgeNode(unrelatedFileRouteId, BridgeNodeKind.FileRoute, "/users/[id]", "web/app/users/[id]/page.tsx", 1),
        };
        var capability = new BridgeCapabilityReport(
            ActiveProviders: ["nextjs"],
            SkippedProviders: [],
            Notes: [],
            EvidenceCounts: new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["nextjs.routeReferences"] = 2,
                ["nextjs.fileRoutes"] = 2,
                ["nextjs.candidates"] = 0,
                ["nextjs.ambiguousMatches"] = 1,
            });
        var index = BuildBridgeIndex(
            Array.Empty<(string symbolId, string name, string file, int line)>(),
            Array.Empty<ScoredEdge>(),
            extra,
            capability);

        string json = TraceTool.Run(index, ResolverFor(index),
            target: "/settings", mode: "bridge", to: null, depth: 2, limit: 20, fullFormat: false, json: true,
            out int emitted, out _);

        Assert.Equal(0, emitted);
        using var doc = JsonDocument.Parse(json);
        JsonElement diagnostic = Assert.Single(doc.RootElement.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("nextjs_route_no_file_match", diagnostic.GetProperty("code").GetString());
        Assert.Contains("Next.js route reference exists: /settings", diagnostic.GetProperty("message").GetString());
    }

    [Fact]
    public void Bridge_RouteStringTarget_NextJsApiClientRequestOnly_JsonExplainsNoHandlerMatch()
    {
        // An unmatched fetch("/api/messages") in a Next.js API repo: the client-request observation node exists,
        // a route-handler fact exists elsewhere, no edge. The diagnostic must speak Next.js API nouns.
        string requestId = BridgeGraph.SynthesizeId(BridgeNodeKind.TsType, "/api/messages");
        string handlerId = BridgeGraph.SynthesizeId(BridgeNodeKind.Endpoint, "GET /api/other");
        var extra = new Dictionary<string, BridgeNode>(StringComparer.Ordinal)
        {
            [requestId] = new BridgeNode(requestId, BridgeNodeKind.TsType, "/api/messages", "web/lib/api.ts", 5),
            [handlerId] = new BridgeNode(handlerId, BridgeNodeKind.Endpoint, "GET /api/other", "web/app/api/other/route.ts", 1),
        };
        // Both API providers activate on the shared http.client_request.v1 family; only nextjs-api has
        // handler facts, so the diagnostic must be attributed to Next.js, not Nuxt.
        var capability = new BridgeCapabilityReport(
            ActiveProviders: ["nextjs-api", "nuxt-api"],
            SkippedProviders: [],
            Notes: [],
            EvidenceCounts: new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["nextjs-api.clientRequests"] = 1,
                ["nextjs-api.routeHandlers"] = 1,
                ["nextjs-api.candidates"] = 0,
                ["nextjs-api.ambiguousMatches"] = 0,
                ["nuxt-api.clientRequests"] = 1,
                ["nuxt-api.serverRoutes"] = 0,
                ["nuxt-api.candidates"] = 0,
                ["nuxt-api.ambiguousMatches"] = 0,
            });
        var index = BuildBridgeIndex(
            Array.Empty<(string symbolId, string name, string file, int line)>(),
            Array.Empty<ScoredEdge>(),
            extra,
            capability);

        string json = TraceTool.Run(index, ResolverFor(index),
            target: "/api/messages", mode: "bridge", to: null, depth: 2, limit: 20, fullFormat: false, json: true,
            out int emitted, out _);

        Assert.Equal(0, emitted);
        using var doc = JsonDocument.Parse(json);
        JsonElement diagnostic = Assert.Single(doc.RootElement.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("nextjs-api_route_no_file_match", diagnostic.GetProperty("code").GetString());
        Assert.Contains("Next.js client request exists: /api/messages", diagnostic.GetProperty("message").GetString());
        Assert.Contains("no matching route handler fact", diagnostic.GetProperty("message").GetString());
        Assert.Contains("observed route handlers: /api/other", diagnostic.GetProperty("message").GetString());
    }

    [Fact]
    public void Bridge_RouteStringTarget_NuxtApiClientRequestOnly_JsonExplainsNoServerRouteMatch()
    {
        string requestId = BridgeGraph.SynthesizeId(BridgeNodeKind.TsType, "/api/messages");
        string handlerId = BridgeGraph.SynthesizeId(BridgeNodeKind.Endpoint, "/api/notes");
        var extra = new Dictionary<string, BridgeNode>(StringComparer.Ordinal)
        {
            [requestId] = new BridgeNode(requestId, BridgeNodeKind.TsType, "/api/messages", "app/lib/api.ts", 5),
            [handlerId] = new BridgeNode(handlerId, BridgeNodeKind.Endpoint, "/api/notes", "server/api/notes.ts", 1),
        };
        var capability = new BridgeCapabilityReport(
            ActiveProviders: ["nextjs-api", "nuxt-api"],
            SkippedProviders: [],
            Notes: [],
            EvidenceCounts: new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["nextjs-api.clientRequests"] = 1,
                ["nextjs-api.routeHandlers"] = 0,
                ["nextjs-api.candidates"] = 0,
                ["nextjs-api.ambiguousMatches"] = 0,
                ["nuxt-api.clientRequests"] = 1,
                ["nuxt-api.serverRoutes"] = 1,
                ["nuxt-api.candidates"] = 0,
                ["nuxt-api.ambiguousMatches"] = 0,
            });
        var index = BuildBridgeIndex(
            Array.Empty<(string symbolId, string name, string file, int line)>(),
            Array.Empty<ScoredEdge>(),
            extra,
            capability);

        string json = TraceTool.Run(index, ResolverFor(index),
            target: "/api/messages", mode: "bridge", to: null, depth: 2, limit: 20, fullFormat: false, json: true,
            out int emitted, out _);

        Assert.Equal(0, emitted);
        using var doc = JsonDocument.Parse(json);
        JsonElement diagnostic = Assert.Single(doc.RootElement.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("nuxt-api_route_no_file_match", diagnostic.GetProperty("code").GetString());
        Assert.Contains("Nuxt client request exists: /api/messages", diagnostic.GetProperty("message").GetString());
        Assert.Contains("no matching server route fact", diagnostic.GetProperty("message").GetString());
        Assert.Contains("observed server routes: /api/notes", diagnostic.GetProperty("message").GetString());
    }

    [Fact]
    public void Bridge_RouteStringTarget_NextJsApiVerbMismatch_JsonExplainsNoRouteEdge()
    {
        // Client request and handler share the path but no edge was built (a real verb distinction):
        // both facts exist, so the honest diagnostic is "no route edge was built".
        string requestId = BridgeGraph.SynthesizeId(BridgeNodeKind.TsType, "/api/messages");
        string handlerId = BridgeGraph.SynthesizeId(BridgeNodeKind.Endpoint, "GET /api/messages");
        var extra = new Dictionary<string, BridgeNode>(StringComparer.Ordinal)
        {
            [requestId] = new BridgeNode(requestId, BridgeNodeKind.TsType, "/api/messages", "web/lib/api.ts", 5),
            [handlerId] = new BridgeNode(handlerId, BridgeNodeKind.Endpoint, "GET /api/messages", "web/app/api/messages/route.ts", 1),
        };
        var capability = new BridgeCapabilityReport(
            ActiveProviders: ["nextjs-api"],
            SkippedProviders: [],
            Notes: [],
            EvidenceCounts: new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["nextjs-api.clientRequests"] = 1,
                ["nextjs-api.routeHandlers"] = 1,
                ["nextjs-api.candidates"] = 0,
                ["nextjs-api.ambiguousMatches"] = 0,
            });
        var index = BuildBridgeIndex(
            Array.Empty<(string symbolId, string name, string file, int line)>(),
            Array.Empty<ScoredEdge>(),
            extra,
            capability);

        string json = TraceTool.Run(index, ResolverFor(index),
            target: "/api/messages", mode: "bridge", to: null, depth: 2, limit: 20, fullFormat: false, json: true,
            out int emitted, out _);

        Assert.Equal(0, emitted);
        using var doc = JsonDocument.Parse(json);
        JsonElement diagnostic = Assert.Single(doc.RootElement.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("nextjs-api_route_no_bridge_link", diagnostic.GetProperty("code").GetString());
        Assert.Contains("Next.js client request and route handler facts exist for /api/messages", diagnostic.GetProperty("message").GetString());
        Assert.Contains("no route edge was built", diagnostic.GetProperty("message").GetString());
    }

    [Fact]
    public void Bridge_RouteStringTarget_NuxtApiServerRouteOnly_JsonExplainsNoClientRequestMatch()
    {
        string handlerId = BridgeGraph.SynthesizeId(BridgeNodeKind.Endpoint, "/api/notes");
        var extra = new Dictionary<string, BridgeNode>(StringComparer.Ordinal)
        {
            [handlerId] = new BridgeNode(handlerId, BridgeNodeKind.Endpoint, "/api/notes", "server/api/notes.ts", 1),
        };
        var capability = new BridgeCapabilityReport(
            ActiveProviders: ["nuxt-api"],
            SkippedProviders: [],
            Notes: [],
            EvidenceCounts: new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["nuxt-api.clientRequests"] = 0,
                ["nuxt-api.serverRoutes"] = 1,
                ["nuxt-api.candidates"] = 0,
                ["nuxt-api.ambiguousMatches"] = 0,
            });
        var index = BuildBridgeIndex(
            Array.Empty<(string symbolId, string name, string file, int line)>(),
            Array.Empty<ScoredEdge>(),
            extra,
            capability);

        string json = TraceTool.Run(index, ResolverFor(index),
            target: "/api/notes", mode: "bridge", to: null, depth: 2, limit: 20, fullFormat: false, json: true,
            out int emitted, out _);

        Assert.Equal(0, emitted);
        using var doc = JsonDocument.Parse(json);
        JsonElement diagnostic = Assert.Single(doc.RootElement.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("nuxt-api_route_no_reference_match", diagnostic.GetProperty("code").GetString());
        Assert.Contains("Nuxt server route exists: /api/notes", diagnostic.GetProperty("message").GetString());
        Assert.Contains("no matching client request fact", diagnostic.GetProperty("message").GetString());
    }

    [Fact]
    public void Bridge_RouteStringTarget_NextJsApiAmbiguousHandlers_JsonExplainsAmbiguousMatch()
    {
        string requestId = BridgeGraph.SynthesizeId(BridgeNodeKind.TsType, "/api/users/42");
        string handlerId = BridgeGraph.SynthesizeId(BridgeNodeKind.Endpoint, "GET /api/users/{}");
        var extra = new Dictionary<string, BridgeNode>(StringComparer.Ordinal)
        {
            [requestId] = new BridgeNode(requestId, BridgeNodeKind.TsType, "/api/users/42", "web/lib/api.ts", 5),
            [handlerId] = new BridgeNode(handlerId, BridgeNodeKind.Endpoint, "GET /api/users/{}", "web/app/api/users/[id]/route.ts", 1),
        };
        var capability = new BridgeCapabilityReport(
            ActiveProviders: ["nextjs-api"],
            SkippedProviders: [],
            Notes: [],
            EvidenceCounts: new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["nextjs-api.clientRequests"] = 1,
                ["nextjs-api.routeHandlers"] = 2,
                ["nextjs-api.candidates"] = 0,
                ["nextjs-api.ambiguousMatches"] = 1,
            });
        var index = BuildBridgeIndex(
            Array.Empty<(string symbolId, string name, string file, int line)>(),
            Array.Empty<ScoredEdge>(),
            extra,
            capability);

        string json = TraceTool.Run(index, ResolverFor(index),
            target: "/api/users/42", mode: "bridge", to: null, depth: 2, limit: 20, fullFormat: false, json: true,
            out int emitted, out _);

        Assert.Equal(0, emitted);
        using var doc = JsonDocument.Parse(json);
        JsonElement diagnostic = Assert.Single(doc.RootElement.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("nextjs-api_route_ambiguous_file_match", diagnostic.GetProperty("code").GetString());
        Assert.Contains("multiple matching route handler facts", diagnostic.GetProperty("message").GetString());
        Assert.Contains("no route edge was built", diagnostic.GetProperty("message").GetString());
    }

    [Fact]
    public void Bridge_RouteStringTarget_WithFrontendFactButNoBackendMatch_ExplainsObservedRoutes()
    {
        string frontendRouteId = BridgeGraph.SynthesizeId(BridgeNodeKind.TsType, "calendar");
        string backendRouteId = BridgeGraph.SynthesizeId(BridgeNodeKind.Endpoint, "GET /keep-alive.html");
        var extra = new Dictionary<string, BridgeNode>(StringComparer.Ordinal)
        {
            [frontendRouteId] = new BridgeNode(frontendRouteId, BridgeNodeKind.TsType, "/calendar", "src/AppHeader.vue", 22),
            [backendRouteId] = new BridgeNode(backendRouteId, BridgeNodeKind.Endpoint, "GET /keep-alive.html", "src/KeepAlive.cs", 25),
        };
        var index = BuildBridgeIndex(
            Array.Empty<(string symbolId, string name, string file, int line)>(),
            Array.Empty<ScoredEdge>(),
            extra);

        string outp = TraceTool.Run(index, ResolverFor(index),
            target: "/calendar", mode: "bridge", to: null, depth: 2, limit: 20, fullFormat: false,
            out int emitted, out _);

        Assert.Equal(0, emitted);
        Assert.Contains("frontend route fact exists: /calendar", outp);
        Assert.Contains("no matching backend route fact", outp);
        Assert.Contains("observed backend routes: /keep-alive.html", outp);
    }

    [Fact]
    public void Bridge_RouteStringTarget_WithFrontendFactButNoBackendMatch_JsonExplainsObservedRoutes()
    {
        string frontendRouteId = BridgeGraph.SynthesizeId(BridgeNodeKind.TsType, "calendar");
        string backendRouteId = BridgeGraph.SynthesizeId(BridgeNodeKind.Endpoint, "GET /keep-alive.html");
        var extra = new Dictionary<string, BridgeNode>(StringComparer.Ordinal)
        {
            [frontendRouteId] = new BridgeNode(frontendRouteId, BridgeNodeKind.TsType, "/calendar", "src/AppHeader.vue", 22),
            [backendRouteId] = new BridgeNode(backendRouteId, BridgeNodeKind.Endpoint, "GET /keep-alive.html", "src/KeepAlive.cs", 25),
        };
        var index = BuildBridgeIndex(
            Array.Empty<(string symbolId, string name, string file, int line)>(),
            Array.Empty<ScoredEdge>(),
            extra);

        string json = TraceTool.Run(index, ResolverFor(index),
            target: "/calendar", mode: "bridge", to: null, depth: 2, limit: 20, fullFormat: false, json: true,
            out int emitted, out _);

        Assert.Equal(0, emitted);
        using var doc = JsonDocument.Parse(json);
        JsonElement diagnostic = Assert.Single(doc.RootElement.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("route_no_backend_match", diagnostic.GetProperty("code").GetString());
        Assert.Contains("frontend route fact exists: /calendar", diagnostic.GetProperty("message").GetString());
        Assert.Contains("observed backend routes: /keep-alive.html", diagnostic.GetProperty("message").GetString());
    }

    [Fact]
    public void Bridge_RouteStringTarget_WithBackendFactButNoFrontendMatch_ExplainsObservedRoutes()
    {
        string frontendRouteId = BridgeGraph.SynthesizeId(BridgeNodeKind.TsType, "calendar");
        string backendRouteId = BridgeGraph.SynthesizeId(BridgeNodeKind.Endpoint, "GET /keep-alive.html");
        var extra = new Dictionary<string, BridgeNode>(StringComparer.Ordinal)
        {
            [frontendRouteId] = new BridgeNode(frontendRouteId, BridgeNodeKind.TsType, "/calendar", "src/AppHeader.vue", 22),
            [backendRouteId] = new BridgeNode(backendRouteId, BridgeNodeKind.Endpoint, "GET /keep-alive.html", "src/KeepAlive.cs", 25),
        };
        var index = BuildBridgeIndex(
            Array.Empty<(string symbolId, string name, string file, int line)>(),
            Array.Empty<ScoredEdge>(),
            extra);

        string outp = TraceTool.Run(index, ResolverFor(index),
            target: "/keep-alive.html", mode: "bridge", to: null, depth: 2, limit: 20, fullFormat: false,
            out int emitted, out _);

        Assert.Equal(0, emitted);
        Assert.Contains("backend route fact exists: /keep-alive.html", outp);
        Assert.Contains("no matching frontend route fact", outp);
        Assert.Contains("observed frontend routes: /calendar", outp);
    }

    // ---------- mode: bridge — provenance-scoped route diagnostics ----------
    // These fixtures run the REAL BridgeGraphBuilder so observation nodes carry per-provider provenance:
    // a diagnostic row may only narrate facts its own provider observed (no wrong-noun shadowing, no
    // foreign-endpoint claims).

    [Fact]
    public void Bridge_RouteStringTarget_NextJsPagesAndApi_ApiDiagnosticNotShadowedByNavigationRow()
    {
        // A normal Next.js repo: a /dashboard page (file-route fact), a GET-only route handler at
        // /api/messages, and an attested-POST fetch to /api/messages (verb mismatch -> correctly no edge).
        // The fetch observation node is a CLIENT REQUEST, not a navigation route reference: the nextjs
        // navigation row must not narrate it as "route reference exists ... observed file routes: /dashboard",
        // and the honest nextjs-api story must not be shadowed by row order.
        var handler = DetailFunction("sym-handler", "GET", "web/app/api/messages/route.ts");
        var tsFn = DetailFunction("sym-tsfn", "createMessage", "web/lib/api.ts");
        var facts = new List<StructuralFactRecord>
        {
            StructuralFact("sf-dashboard-page", "nextjs.file_route.v1", "tsx", "web/app/dashboard/page.tsx",
                string.Empty, 1, """{"route_path":"/dashboard"}"""),
            StructuralFact("sf-messages-handler", "nextjs.route_handler.v1", "typescript",
                "web/app/api/messages/route.ts", "sym-handler", 1,
                """{"framework":"nextjs","router":"app","route_path":"/api/messages","verb":"GET","verb_source":"attested"}"""),
            StructuralFact("sf-post-messages", "http.client_request.v1", "typescript", "web/lib/api.ts",
                "sym-tsfn", 8,
                """{"client":"fetch","framework":"fetch","target_path":"/api/messages","url_kind":"path","verb":"POST","verb_source":"attested"}"""),
        };
        var index = BuildBridgeIndexFromStructuralFacts([handler, tsFn], facts);

        string json = TraceTool.Run(index, ResolverFor(index),
            target: "/api/messages", mode: "bridge", to: null, depth: 2, limit: 20, fullFormat: false, json: true,
            out int emitted, out _);

        Assert.Equal(0, emitted);
        using var doc = JsonDocument.Parse(json);
        JsonElement diagnostic = Assert.Single(doc.RootElement.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("nextjs-api_route_no_bridge_link", diagnostic.GetProperty("code").GetString());
        Assert.Contains("Next.js client request and route handler facts exist for /api/messages",
            diagnostic.GetProperty("message").GetString());
    }

    [Fact]
    public void Bridge_RouteStringTarget_NextJsPagesAndApi_NavigationDiagnosticKeepsNavNounForPageRoute()
    {
        // Same mixed repo shape, but the traced route is a real navigation reference (<Link href="/dashboard">)
        // with no matching page: the navigation row still owns that story with navigation nouns.
        var handler = DetailFunction("sym-handler", "GET", "web/app/api/messages/route.ts");
        var tsFn = DetailFunction("sym-tsfn", "createMessage", "web/lib/api.ts");
        var nav = DetailFunction("sym-nav", "Nav", "web/Nav.tsx");
        var facts = new List<StructuralFactRecord>
        {
            StructuralFact("sf-dashboard-link", "nextjs.route_reference.v1", "tsx", "web/Nav.tsx",
                "sym-nav", 10, """{"framework":"nextjs","target_path":"/dashboard"}"""),
            StructuralFact("sf-settings-page", "nextjs.file_route.v1", "tsx", "web/app/settings/page.tsx",
                string.Empty, 1, """{"route_path":"/settings"}"""),
            StructuralFact("sf-messages-handler", "nextjs.route_handler.v1", "typescript",
                "web/app/api/messages/route.ts", "sym-handler", 1,
                """{"framework":"nextjs","router":"app","route_path":"/api/messages","verb":"GET","verb_source":"attested"}"""),
            StructuralFact("sf-post-messages", "http.client_request.v1", "typescript", "web/lib/api.ts",
                "sym-tsfn", 8,
                """{"client":"fetch","framework":"fetch","target_path":"/api/messages","url_kind":"path","verb":"POST","verb_source":"attested"}"""),
        };
        var index = BuildBridgeIndexFromStructuralFacts([handler, tsFn, nav], facts);

        string json = TraceTool.Run(index, ResolverFor(index),
            target: "/dashboard", mode: "bridge", to: null, depth: 2, limit: 20, fullFormat: false, json: true,
            out int emitted, out _);

        Assert.Equal(0, emitted);
        using var doc = JsonDocument.Parse(json);
        JsonElement diagnostic = Assert.Single(doc.RootElement.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("nextjs_route_no_file_match", diagnostic.GetProperty("code").GetString());
        Assert.Contains("Next.js route reference exists: /dashboard", diagnostic.GetProperty("message").GetString());
        Assert.Contains("observed file routes: /settings", diagnostic.GetProperty("message").GetString());
    }

    [Fact]
    public void Bridge_RouteStringTarget_MixedMonorepo_AspNetRouteNotClaimedByNextJsApi()
    {
        // Monorepo: one Next.js handler at /api/health, an ASP.NET attribute-route GET endpoint at /api/orders,
        // and an attested-POST fetch to /api/orders (verb mismatch -> no edge). Nothing Next.js serves
        // /api/orders, so the diagnostic must not claim "Next.js ... route handler facts exist for /api/orders";
        // the pooled generic story is the honest fallback.
        var list = DetailMethod("sym-orders", "List", "OrdersController", "Api/OrdersController.cs");
        var tsFn = DetailFunction("sym-tsfn", "createOrder", "web/src/lib/api.ts");
        var health = DetailFunction("sym-health", "GET", "web/app/api/health/route.ts");
        var facts = new List<StructuralFactRecord>
        {
            StructuralFact("sf-health-handler", "nextjs.route_handler.v1", "typescript",
                "web/app/api/health/route.ts", "sym-health", 1,
                """{"framework":"nextjs","router":"app","route_path":"/api/health","verb":"GET","verb_source":"attested"}"""),
            StructuralFact("sf-orders-endpoint", "aspnet.attribute_route.v1", "csharp",
                "Api/OrdersController.cs", "sym-orders", 12,
                """{"attribute_kind":"http_method","verb":"GET","controller_route_template":"api/[controller]","effective_route_template":"/api/orders","route_tokens":["controller"]}"""),
            StructuralFact("sf-post-orders", "http.client_request.v1", "typescript", "web/src/lib/api.ts",
                "sym-tsfn", 5,
                """{"client":"fetch","framework":"fetch","target_path":"/api/orders","url_kind":"path","verb":"POST","verb_source":"attested"}"""),
        };
        var index = BuildBridgeIndexFromStructuralFacts([list, tsFn, health], facts);

        string json = TraceTool.Run(index, ResolverFor(index),
            target: "/api/orders", mode: "bridge", to: null, depth: 2, limit: 20, fullFormat: false, json: true,
            out int emitted, out _);

        Assert.Equal(0, emitted);
        using var doc = JsonDocument.Parse(json);
        JsonElement diagnostic = Assert.Single(doc.RootElement.GetProperty("diagnostics").EnumerateArray());
        string? message = diagnostic.GetProperty("message").GetString();
        Assert.Equal("route_no_bridge_link", diagnostic.GetProperty("code").GetString());
        Assert.Contains("frontend and backend route facts exist for /api/orders", message);
        Assert.DoesNotContain("Next.js", message);
    }

    [Fact]
    public void Bridge_RouteStringTarget_MixedMonorepo_NextJsHandlerAtTracedRoute_KeepsNextJsApiDiagnostic()
    {
        // Control for the foreign-endpoint case: the Next.js handler actually serves the traced route
        // (GET /api/orders) and the fetch is attested POST (verb mismatch -> no edge). Both Next.js facts
        // genuinely exist for the route, so the provider-framed diagnostic stays.
        var list = DetailMethod("sym-orders", "List", "OrdersController", "Api/OrdersController.cs");
        var tsFn = DetailFunction("sym-tsfn", "createOrder", "web/src/lib/api.ts");
        var handler = DetailFunction("sym-handler", "GET", "web/app/api/orders/route.ts");
        var facts = new List<StructuralFactRecord>
        {
            StructuralFact("sf-orders-handler", "nextjs.route_handler.v1", "typescript",
                "web/app/api/orders/route.ts", "sym-handler", 1,
                """{"framework":"nextjs","router":"app","route_path":"/api/orders","verb":"GET","verb_source":"attested"}"""),
            StructuralFact("sf-orders-endpoint", "aspnet.attribute_route.v1", "csharp",
                "Api/OrdersController.cs", "sym-orders", 12,
                """{"attribute_kind":"http_method","verb":"GET","controller_route_template":"api/[controller]","effective_route_template":"/api/orders","route_tokens":["controller"]}"""),
            StructuralFact("sf-post-orders", "http.client_request.v1", "typescript", "web/src/lib/api.ts",
                "sym-tsfn", 5,
                """{"client":"fetch","framework":"fetch","target_path":"/api/orders","url_kind":"path","verb":"POST","verb_source":"attested"}"""),
        };
        var index = BuildBridgeIndexFromStructuralFacts([list, tsFn, handler], facts);

        string json = TraceTool.Run(index, ResolverFor(index),
            target: "/api/orders", mode: "bridge", to: null, depth: 2, limit: 20, fullFormat: false, json: true,
            out int emitted, out _);

        Assert.Equal(0, emitted);
        using var doc = JsonDocument.Parse(json);
        JsonElement diagnostic = Assert.Single(doc.RootElement.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("nextjs-api_route_no_bridge_link", diagnostic.GetProperty("code").GetString());
        Assert.Contains("Next.js client request and route handler facts exist for /api/orders",
            diagnostic.GetProperty("message").GetString());
    }

    [Fact]
    public void Bridge_CsharpHttpClientRequest_SurfacesRouteEdgeToAspNetEndpoint()
    {
        // Task 5 end-to-end: a NON-test csharp http.client_request.v1 fact + an ASP.NET attribute-route endpoint
        // flow through the REAL BridgeGraphBuilder and surface as a rendered route edge in `trace mode=bridge`.
        // This exercises the whole csharp HttpClient -> ASP.NET pipeline (graph build + render), which the
        // provider-agnostic hand-built render tests above do not cover for the csharp client path.
        var getById = DetailMethod("sym-get", "GetById", "UsersController", "api/UsersController.cs");
        var client = DetailMethod("sym-client", "FetchUser", "UserApiClient", "src/UserApiClient.cs");
        var facts = new List<StructuralFactRecord>
        {
            StructuralFact("fact-httpget-id", "aspnet.attribute_route.v1", "csharp", "api/UsersController.cs",
                "sym-get", 18,
                """{"attribute_kind":"http_method","verb":"GET","route_template":"{id}","controller_route_template":"api/[controller]","effective_route_template":"/api/users/{id}","route_tokens":["controller"]}"""),
            StructuralFact("fact-httpclient-get", "http.client_request.v1", "csharp", "src/UserApiClient.cs",
                "sym-client", 12,
                """{"client":"HttpClient","framework":"httpclient","target_path":"/api/users/{id}","url_kind":"path","verb":"GET","verb_source":"attested"}"""),
        };
        var index = BuildBridgeIndexFromStructuralFacts([getById, client], facts);

        string outp = TraceTool.Run(index, ResolverFor(index),
            target: "/api/users/{id}", mode: "bridge", to: null, depth: 2, limit: 20, fullFormat: false,
            out int emitted, out _);

        Assert.Equal(1, emitted);
        Assert.Contains("--route-->", outp);
        Assert.Contains("GetById", outp);
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
        Assert.Contains("Next:", outp);
        Assert.Contains("trace target=\"Loner\" mode=\"refs\"", outp);
        Assert.Contains("inspect target=\"Loner\" depth=\"full\"", outp);
        Assert.Contains("search query=\"Loner\" mode=\"source\"", outp);
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
        JsonElement[] actions = root.GetProperty("next_actions").EnumerateArray().ToArray();
        Assert.Equal(3, actions.Length);
        Assert.Equal("trace", actions[0].GetProperty("tool").GetString());
        Assert.Equal("refs", actions[0].GetProperty("args").GetProperty("mode").GetString());
        Assert.Equal("inspect", actions[1].GetProperty("tool").GetString());
        Assert.Equal("full", actions[1].GetProperty("args").GetProperty("depth").GetString());
        Assert.Equal("search", actions[2].GetProperty("tool").GetString());
        Assert.Equal("source", actions[2].GetProperty("args").GetProperty("mode").GetString());
    }

    [Fact]
    public void Bridge_NotOnBridge_WithRouteFactEvidence_OffersPatternAudits()
    {
        var capability = new BridgeCapabilityReport(
            ActiveProviders: ["dotnet-web"],
            SkippedProviders: [],
            Notes: [],
            EvidenceCounts: new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["dotnet-web.structuralFacts"] = 6,
                ["dotnet-web.aspnetMinimalRoutes"] = 1,
                ["dotnet-web.htmxCalls"] = 1,
                ["dotnet-web.vueCalls"] = 1,
                ["dotnet-web.reactCalls"] = 1,
                ["dotnet-web.nextjsCalls"] = 1,
                ["dotnet-web.nuxtCalls"] = 1,
            });
        var index = BuildBridgeIndex(
            new[] { ("x", "Loner", "src/Loner.cs", 1) },
            Array.Empty<ScoredEdge>(),
            new Dictionary<string, BridgeNode>(StringComparer.Ordinal),
            capability);

        string outp = TraceTool.Run(index, ResolverFor(index),
            target: "Loner", mode: "bridge", to: null, depth: 3, limit: 20, fullFormat: false,
            out int emitted, out _);

        Assert.Equal(0, emitted);
        Assert.Contains("trace target=\"Loner\" mode=\"refs\"", outp);
        Assert.Contains("inspect target=\"Loner\" depth=\"full\"", outp);
        Assert.Contains("search query=\"Loner\" mode=\"source\"", outp);
        Assert.Contains("patterns operation=\"search\" query=\"route\"", outp);
        Assert.Contains("patterns operation=\"search\" pattern_id=\"htmx.attribute.v1\"", outp);
        Assert.Contains("patterns operation=\"search\" pattern_id=\"vue.route_reference.v1\"", outp);
        Assert.Contains("patterns operation=\"search\" pattern_id=\"react.route_reference.v1\"", outp);
        Assert.Contains("patterns operation=\"search\" query=\"nextjs\"", outp);
        Assert.Contains("patterns operation=\"search\" query=\"nuxt\"", outp);
    }

    [Fact]
    public void Bridge_NotOnBridge_WithRouteFactEvidence_JsonCarriesPatternAudits()
    {
        var capability = new BridgeCapabilityReport(
            ActiveProviders: ["dotnet-web"],
            SkippedProviders: [],
            Notes: [],
            EvidenceCounts: new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["dotnet-web.structuralFacts"] = 6,
                ["dotnet-web.aspnetMinimalRoutes"] = 1,
                ["dotnet-web.htmxCalls"] = 1,
                ["dotnet-web.vueCalls"] = 1,
                ["dotnet-web.reactCalls"] = 1,
                ["dotnet-web.nextjsCalls"] = 1,
                ["dotnet-web.nuxtCalls"] = 1,
            });
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
        JsonElement[] actions = doc.RootElement.GetProperty("next_actions").EnumerateArray().ToArray();
        Assert.Contains(actions, action =>
            action.GetProperty("tool").GetString() == "patterns" &&
            action.GetProperty("args").TryGetProperty("query", out JsonElement query) &&
            query.GetString() == "route");
        Assert.Contains(actions, action =>
            action.GetProperty("tool").GetString() == "patterns" &&
            action.GetProperty("args").TryGetProperty("pattern_id", out JsonElement patternId) &&
            patternId.GetString() == "htmx.attribute.v1");
        Assert.Contains(actions, action =>
            action.GetProperty("tool").GetString() == "patterns" &&
            action.GetProperty("args").TryGetProperty("pattern_id", out JsonElement patternId) &&
            patternId.GetString() == "vue.route_reference.v1");
        Assert.Contains(actions, action =>
            action.GetProperty("tool").GetString() == "patterns" &&
            action.GetProperty("args").TryGetProperty("pattern_id", out JsonElement patternId) &&
            patternId.GetString() == "react.route_reference.v1");
        Assert.Contains(actions, action =>
            action.GetProperty("tool").GetString() == "patterns" &&
            action.GetProperty("args").TryGetProperty("query", out JsonElement query) &&
            query.GetString() == "nextjs");
        Assert.Contains(actions, action =>
            action.GetProperty("tool").GetString() == "patterns" &&
            action.GetProperty("args").TryGetProperty("query", out JsonElement query) &&
            query.GetString() == "nuxt");
    }

    [Fact]
    public void Bridge_CannotStart_WithRouteFactEvidence_JsonCarriesPatternAudits()
    {
        var capability = new BridgeCapabilityReport(
            ActiveProviders: ["dotnet-web"],
            SkippedProviders: [],
            Notes: [],
            EvidenceCounts: new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["dotnet-web.structuralFacts"] = 2,
                ["dotnet-web.vueCalls"] = 1,
            });
        var index = BuildBridgeIndex(
            new[] { ("x", "Loner", "src/Loner.cs", 1) },
            Array.Empty<ScoredEdge>(),
            new Dictionary<string, BridgeNode>(StringComparer.Ordinal),
            capability);

        string json = TraceTool.Run(index, ResolverFor(index),
            target: "MissingRoute", mode: "bridge", to: null, depth: 3, limit: 20, fullFormat: false, json: true,
            out int emitted, out _);

        Assert.Equal(0, emitted);
        using var doc = JsonDocument.Parse(json);
        JsonElement[] actions = doc.RootElement.GetProperty("next_actions").EnumerateArray().ToArray();
        Assert.Contains(actions, action =>
            action.GetProperty("tool").GetString() == "patterns" &&
            action.GetProperty("args").TryGetProperty("query", out JsonElement query) &&
            query.GetString() == "route");
        Assert.Contains(actions, action =>
            action.GetProperty("tool").GetString() == "patterns" &&
            action.GetProperty("args").TryGetProperty("pattern_id", out JsonElement patternId) &&
            patternId.GetString() == "vue.route_reference.v1");
    }

    [Fact]
    public void Bridge_NotOnBridge_WithClientRequestAndAttributeRouteEvidence_OffersPatternAudits()
    {
        // The 2.6.0 boundary keys alone must open the route-fact gate and map to their pattern audits.
        var capability = new BridgeCapabilityReport(
            ActiveProviders: ["dotnet-web"],
            SkippedProviders: [],
            Notes: [],
            EvidenceCounts: new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["dotnet-web.clientRequests"] = 2,
                ["dotnet-web.attributeRoutes"] = 3,
            });
        var index = BuildBridgeIndex(
            new[] { ("x", "Loner", "src/Loner.cs", 1) },
            Array.Empty<ScoredEdge>(),
            new Dictionary<string, BridgeNode>(StringComparer.Ordinal),
            capability);

        string outp = TraceTool.Run(index, ResolverFor(index),
            target: "Loner", mode: "bridge", to: null, depth: 3, limit: 20, fullFormat: false,
            out int emitted, out _);

        Assert.Equal(0, emitted);
        Assert.Contains("patterns operation=\"search\" query=\"route\"", outp);
        Assert.Contains("patterns operation=\"search\" pattern_id=\"http.client_request.v1\"", outp);
        Assert.Contains("patterns operation=\"search\" pattern_id=\"aspnet.attribute_route.v1\"", outp);
    }

    [Fact]
    public void Bridge_NotOnBridge_WithApiProviderEvidence_JsonCarriesPatternAudits()
    {
        var capability = new BridgeCapabilityReport(
            ActiveProviders: ["nextjs-api", "nuxt-api"],
            SkippedProviders: [],
            Notes: [],
            EvidenceCounts: new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["nextjs-api.clientRequests"] = 1,
                ["nextjs-api.routeHandlers"] = 1,
                ["nuxt-api.clientRequests"] = 1,
                ["nuxt-api.serverRoutes"] = 1,
            });
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
        JsonElement[] actions = doc.RootElement.GetProperty("next_actions").EnumerateArray().ToArray();
        Assert.Contains(actions, action =>
            action.GetProperty("tool").GetString() == "patterns" &&
            action.GetProperty("args").TryGetProperty("query", out JsonElement query) &&
            query.GetString() == "route");
        Assert.Contains(actions, action =>
            action.GetProperty("tool").GetString() == "patterns" &&
            action.GetProperty("args").TryGetProperty("pattern_id", out JsonElement patternId) &&
            patternId.GetString() == "http.client_request.v1");
        Assert.Contains(actions, action =>
            action.GetProperty("tool").GetString() == "patterns" &&
            action.GetProperty("args").TryGetProperty("query", out JsonElement query) &&
            query.GetString() == "nextjs");
        Assert.Contains(actions, action =>
            action.GetProperty("tool").GetString() == "patterns" &&
            action.GetProperty("args").TryGetProperty("query", out JsonElement query) &&
            query.GetString() == "nuxt");
    }

    [Fact]
    public void Bridge_NoLinksWithinDepth_RendersFallbackGuidance()
    {
        var mapsTo = MakeScored(
            BridgeKind.MapsTo,
            SymbolRef("dto", "OrderDto", "src/OrderDto.cs"),
            SymbolRef("ent", "Order", "src/Order.cs"),
            ConfidenceBand.High,
            0.95);
        var index = BuildBridgeIndex(
            new[] { ("dto", "OrderDto", "src/OrderDto.cs", 1), ("ent", "Order", "src/Order.cs", 1) },
            new[] { mapsTo },
            new Dictionary<string, BridgeNode>(StringComparer.Ordinal));

        string outp = TraceTool.Run(index, ResolverFor(index),
            target: "OrderDto", mode: "bridge", to: null, depth: 0, limit: 20, fullFormat: false,
            out int emitted, out _);

        Assert.Equal(0, emitted);
        Assert.Contains("No bridge links from 'OrderDto' within 0 hop(s).", outp);
        Assert.Contains("Next:", outp);
        Assert.Contains("trace target=\"OrderDto\" mode=\"refs\"", outp);
        Assert.Contains("inspect target=\"OrderDto\" depth=\"full\"", outp);
        Assert.Contains("search query=\"OrderDto\" mode=\"source\"", outp);
    }

    [Fact]
    public void Bridge_NoLinksWithinDepth_JsonCarriesNextActions()
    {
        var mapsTo = MakeScored(
            BridgeKind.MapsTo,
            SymbolRef("dto", "OrderDto", "src/OrderDto.cs"),
            SymbolRef("ent", "Order", "src/Order.cs"),
            ConfidenceBand.High,
            0.95);
        var index = BuildBridgeIndex(
            new[] { ("dto", "OrderDto", "src/OrderDto.cs", 1), ("ent", "Order", "src/Order.cs", 1) },
            new[] { mapsTo },
            new Dictionary<string, BridgeNode>(StringComparer.Ordinal));

        string json = TraceTool.Run(index, ResolverFor(index),
            target: "OrderDto", mode: "bridge", to: null, depth: 0, limit: 20, fullFormat: false, json: true,
            out int emitted, out _);

        Assert.Equal(0, emitted);
        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        JsonElement diagnostic = Assert.Single(root.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("no_bridge_links", diagnostic.GetProperty("code").GetString());
        JsonElement[] actions = root.GetProperty("next_actions").EnumerateArray().ToArray();
        Assert.Equal(3, actions.Length);
        Assert.Equal("refs", actions[0].GetProperty("args").GetProperty("mode").GetString());
        Assert.Equal("inspect", actions[1].GetProperty("tool").GetString());
        Assert.Equal("full", actions[1].GetProperty("args").GetProperty("depth").GetString());
        Assert.Equal("source", actions[2].GetProperty("args").GetProperty("mode").GetString());
    }

    [Fact]
    public void Bridge_NoLinksWithinDepth_WithHtmxRouteFactEvidence_OffersPatternAudits()
    {
        var mapsTo = MakeScored(
            BridgeKind.MapsTo,
            SymbolRef("dto", "OrderDto", "src/OrderDto.cs"),
            SymbolRef("ent", "Order", "src/Order.cs"),
            ConfidenceBand.High,
            0.95);
        var capability = new BridgeCapabilityReport(
            ActiveProviders: ["dotnet-web"],
            SkippedProviders: [],
            Notes: [],
            EvidenceCounts: new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["dotnet-web.structuralFacts"] = 2,
                ["dotnet-web.aspnetMinimalRoutes"] = 1,
                ["dotnet-web.htmxCalls"] = 1,
            });
        var index = BuildBridgeIndex(
            new[] { ("dto", "OrderDto", "src/OrderDto.cs", 1), ("ent", "Order", "src/Order.cs", 1) },
            new[] { mapsTo },
            new Dictionary<string, BridgeNode>(StringComparer.Ordinal),
            capability);

        string json = TraceTool.Run(index, ResolverFor(index),
            target: "OrderDto", mode: "bridge", to: null, depth: 0, limit: 20, fullFormat: false, json: true,
            out int emitted, out _);

        Assert.Equal(0, emitted);
        using var doc = JsonDocument.Parse(json);
        JsonElement[] actions = doc.RootElement.GetProperty("next_actions").EnumerateArray().ToArray();
        Assert.Contains(actions, action =>
            action.GetProperty("tool").GetString() == "patterns" &&
            action.GetProperty("args").TryGetProperty("query", out JsonElement query) &&
            query.GetString() == "route");
        Assert.Contains(actions, action =>
            action.GetProperty("tool").GetString() == "patterns" &&
            action.GetProperty("args").TryGetProperty("pattern_id", out JsonElement patternId) &&
            patternId.GetString() == "htmx.attribute.v1");
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

        string output = tool.Trace("Alpha", mode: "path", to: "Beta", workspace_id: "target-ws");

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

    [Theory]
    [InlineData("sideways")]
    [InlineData("auto")]
    public void UnknownMode_CleanMessage(string mode)
    {
        var index = BuildSymbolIndex(
            new[] { ("a", "Alpha", "method", "src/A.cs", 1) },
            Array.Empty<(string, string)>());

        string outp = TraceTool.Run(index, ResolverFor(index),
            target: "Alpha", mode: mode, to: null, depth: 3, limit: 20, fullFormat: false,
            out int emitted, out _);

        Assert.Equal(0, emitted);
        Assert.Contains($"Unknown mode '{mode}'", outp);
    }

    [Fact]
    public void NullIndex_Throws()
    {
        var index = BuildSymbolIndex(
            new[] { ("a", "Alpha", "method", "src/A.cs", 1) },
            Array.Empty<(string, string)>());
        var resolver = ResolverFor(index);

        Assert.Throws<ArgumentNullException>(() =>
            TraceTool.Run(null!, resolver, "Alpha", "path", "Beta", 3, 20, false, out _, out _));
        Assert.Throws<ArgumentNullException>(() =>
            TraceTool.Run(index, null!, "Alpha", "path", "Beta", 3, 20, false, out _, out _));
    }

    // ---------- backend-http provider (plan Task 6): route diagnostics, next_actions, render agreement ----------

    [Fact]
    public void Bridge_RouteStringTarget_BackendHttpClientRequestOnly_JsonExplainsNoRouteFactMatch()
    {
        // A backend-http repo: an Express route fact exists for /api/orders, but the traced client request is to a
        // DIFFERENT route /api/users that matches no route fact. The diagnostic must speak backend-http nouns
        // ("Backend" / "client request" / "route fact"), not the generic frontend/backend wording.
        string requestId = BridgeGraph.SynthesizeId(BridgeNodeKind.TsType, "/api/users");
        string routeId = BridgeGraph.SynthesizeId(BridgeNodeKind.Endpoint, "GET /api/orders");
        var extra = new Dictionary<string, BridgeNode>(StringComparer.Ordinal)
        {
            [requestId] = new BridgeNode(requestId, BridgeNodeKind.TsType, "/api/users", "web/lib/api.ts", 5),
            [routeId] = new BridgeNode(routeId, BridgeNodeKind.Endpoint, "GET /api/orders", "server/orders.js", 1),
        };
        // Only backend-http participates: its routeFacts gate is > 0; nextjs-api/nuxt-api handler evidence is absent,
        // so the shared client-request family cannot mis-attribute the diagnostic to a framework API provider.
        var capability = new BridgeCapabilityReport(
            ActiveProviders: ["backend-http"],
            SkippedProviders: [],
            Notes: [],
            EvidenceCounts: new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["backend-http.clientRequests"] = 1,
                ["backend-http.routeFacts"] = 1,
                ["backend-http.candidates"] = 0,
                ["backend-http.ambiguousMatches"] = 0,
            });
        var index = BuildBridgeIndex(
            Array.Empty<(string symbolId, string name, string file, int line)>(),
            Array.Empty<ScoredEdge>(),
            extra,
            capability);

        string json = TraceTool.Run(index, ResolverFor(index),
            target: "/api/users", mode: "bridge", to: null, depth: 2, limit: 20, fullFormat: false, json: true,
            out int emitted, out _);

        Assert.Equal(0, emitted);
        using var doc = JsonDocument.Parse(json);
        JsonElement diagnostic = Assert.Single(doc.RootElement.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("backend-http_route_no_file_match", diagnostic.GetProperty("code").GetString());
        string message = diagnostic.GetProperty("message").GetString()!;
        Assert.Contains("Backend client request exists: /api/users", message);
        Assert.Contains("no matching route fact", message);
        Assert.Contains("observed routes: /api/orders", message);
    }

    [Fact]
    public void Bridge_RouteStringTarget_BackendHttpBothFactsNoEdge_JsonUsesRouteEdgeNoun()
    {
        // A client request AND a backend route fact for the SAME route /api/users exist, but no edge was built.
        // The honest diagnostic uses the backend-http "route edge" edge noun (proving EdgeNoun renders per provider).
        string requestId = BridgeGraph.SynthesizeId(BridgeNodeKind.TsType, "/api/users");
        string routeId = BridgeGraph.SynthesizeId(BridgeNodeKind.Endpoint, "/api/users");
        var extra = new Dictionary<string, BridgeNode>(StringComparer.Ordinal)
        {
            [requestId] = new BridgeNode(requestId, BridgeNodeKind.TsType, "/api/users", "web/lib/api.ts", 5),
            [routeId] = new BridgeNode(routeId, BridgeNodeKind.Endpoint, "/api/users", "server/users.js", 1),
        };
        var capability = new BridgeCapabilityReport(
            ActiveProviders: ["backend-http"],
            SkippedProviders: [],
            Notes: [],
            EvidenceCounts: new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["backend-http.clientRequests"] = 1,
                ["backend-http.routeFacts"] = 1,
                ["backend-http.candidates"] = 0,
                ["backend-http.ambiguousMatches"] = 0,
            });
        var index = BuildBridgeIndex(
            Array.Empty<(string symbolId, string name, string file, int line)>(),
            Array.Empty<ScoredEdge>(),
            extra,
            capability);

        string json = TraceTool.Run(index, ResolverFor(index),
            target: "/api/users", mode: "bridge", to: null, depth: 2, limit: 20, fullFormat: false, json: true,
            out int emitted, out _);

        Assert.Equal(0, emitted);
        using var doc = JsonDocument.Parse(json);
        JsonElement diagnostic = Assert.Single(doc.RootElement.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("backend-http_route_no_bridge_link", diagnostic.GetProperty("code").GetString());
        string message = diagnostic.GetProperty("message").GetString()!;
        Assert.Contains("Backend client request and route facts exist for /api/users", message);
        Assert.Contains("no route edge was built", message);
    }

    [Fact]
    public void Bridge_NotOnBridge_WithBackendHttpEvidence_OffersBackendRouteAndClientRequestAudits()
    {
        // A backend-http repo (route facts + client requests + expanded resource routes) whose traced symbol is not
        // on the bridge: the generic route audit, the shared http.client_request.v1 audit, and the backend-http
        // route-fact audit must all fire off the new evidence keys.
        var capability = new BridgeCapabilityReport(
            ActiveProviders: ["backend-http"],
            SkippedProviders: [],
            Notes: [],
            EvidenceCounts: new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["backend-http.clientRequests"] = 2,
                ["backend-http.routeFacts"] = 3,
                ["backend-http.expandedResourceRoutes"] = 7,
            });
        var index = BuildBridgeIndex(
            new[] { ("x", "Loner", "src/Loner.cs", 1) },
            Array.Empty<ScoredEdge>(),
            new Dictionary<string, BridgeNode>(StringComparer.Ordinal),
            capability);

        string outp = TraceTool.Run(index, ResolverFor(index),
            target: "Loner", mode: "bridge", to: null, depth: 3, limit: 20, fullFormat: false,
            out int emitted, out _);

        Assert.Equal(0, emitted);
        Assert.Contains("patterns operation=\"search\" query=\"route\"", outp);
        Assert.Contains("patterns operation=\"search\" pattern_id=\"http.client_request.v1\"", outp);
        Assert.Contains("audit backend HTTP route structural facts consumed by the backend-http bridge", outp);
    }

    [Fact]
    public void Bridge_NotOnBridge_WithBackendHttpEvidence_JsonCarriesBackendRouteAndClientRequestAudits()
    {
        // Composed-route evidence alone (no direct routeFacts) must open the route-fact gate and fire the
        // backend-http route audit; client-request evidence fires the shared http.client_request.v1 audit.
        var capability = new BridgeCapabilityReport(
            ActiveProviders: ["backend-http"],
            SkippedProviders: [],
            Notes: [],
            EvidenceCounts: new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["backend-http.clientRequests"] = 1,
                ["backend-http.composedRoutes"] = 2,
            });
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
        JsonElement[] actions = doc.RootElement.GetProperty("next_actions").EnumerateArray().ToArray();
        Assert.Contains(actions, action =>
            action.GetProperty("tool").GetString() == "patterns" &&
            action.GetProperty("reason").GetString() == "audit backend HTTP route structural facts consumed by the backend-http bridge" &&
            action.GetProperty("args").TryGetProperty("query", out JsonElement query) &&
            query.GetString() == "route");
        Assert.Contains(actions, action =>
            action.GetProperty("tool").GetString() == "patterns" &&
            action.GetProperty("args").TryGetProperty("pattern_id", out JsonElement patternId) &&
            patternId.GetString() == "http.client_request.v1");
    }

    [Fact]
    public void Bridge_BackendHttpClientRequestEdge_CompactAndJsonAgreeOnKindLabelBandAndFlags()
    {
        // A matched verb-known backend client-request edge (fetch -> Express route handler symbol): High, no flags.
        // Backend edges flow through the same BridgeKind.Hits path as dotnet-web/nextjs-api, so compact renders the
        // "route" arrow and JSON renders kind=hits/label=route with no honesty flags. ASSERT this; the render path
        // needs no backend-specific code.
        var hits = MakeScored(
            BridgeKind.Hits,
            SymbolRef("client", "loadOrders", "web/lib/api.ts"),
            SymbolRef("handler", "getOrders", "server/routes/orders.js"),
            ConfidenceBand.High, 0.9);
        var capability = new BridgeCapabilityReport(
            ActiveProviders: ["backend-http"],
            SkippedProviders: [],
            Notes: [],
            EvidenceCounts: new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["backend-http.clientRequests"] = 1,
                ["backend-http.routeFacts"] = 1,
                ["backend-http.candidates"] = 1,
            });
        var index = BuildBridgeIndex(
            new[]
            {
                ("client", "loadOrders", "web/lib/api.ts", 5),
                ("handler", "getOrders", "server/routes/orders.js", 3),
            },
            new[] { hits },
            new Dictionary<string, BridgeNode>(StringComparer.Ordinal),
            capability);

        string compact = TraceTool.Run(index, ResolverFor(index),
            target: "loadOrders", mode: "bridge", to: null, depth: 2, limit: 20, fullFormat: false,
            out int compactEmitted, out _);
        string json = TraceTool.Run(index, ResolverFor(index),
            target: "loadOrders", mode: "bridge", to: null, depth: 2, limit: 20, fullFormat: false, json: true,
            out int jsonEmitted, out _);

        Assert.Equal(1, compactEmitted);
        Assert.Equal(1, jsonEmitted);
        Assert.Contains("loadOrders  --route-->  getOrders", compact);
        Assert.Contains("0.90 (High)", compact);
        Assert.DoesNotContain("[verb-unknown]", compact);
        Assert.DoesNotContain("[ambiguous]", compact);

        using var doc = JsonDocument.Parse(json);
        JsonElement link = Assert.Single(doc.RootElement.GetProperty("links").EnumerateArray());
        Assert.Equal("hits", link.GetProperty("kind").GetString());
        Assert.Equal("route", link.GetProperty("label").GetString());
        Assert.Equal("high", link.GetProperty("confidence").GetString());
        Assert.Equal(0.9, link.GetProperty("score").GetDouble(), precision: 5);
        Assert.Empty(link.GetProperty("flags").EnumerateArray());
    }
}
