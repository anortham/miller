using Miller.Core.Search;
using Miller.Indexing;
using Miller.Indexing.Semantic;
using Miller.Server.Tools;
using Miller.Tests.Support;
using Xunit;

namespace Miller.Tests.Server;

public sealed class HybridSearchTests
{
    private const string Root = "/ws";
    private const string ConceptualQuery = "how does the workspace refresh converge";

    private static readonly SearchRoute SymbolRoute = SearchRoutePlanner.Plan("symbol", regions: null);

    private static SemanticSessionOptions FastOptions => new()
    {
        RequestTimeout = TimeSpan.FromSeconds(10),
        InitTimeout = TimeSpan.FromSeconds(10),
        ShutdownTimeout = TimeSpan.FromSeconds(1),
        RestartBackoff = TimeSpan.Zero,
        RestartBackoffCap = TimeSpan.Zero,
        Delay = static (_, _) => Task.CompletedTask,
    };

    [Fact]
    public void NoFusionArm_RendersExactlyThePreFusionOutput()
    {
        RecordingSymbolLookupIndex index = TwoSymbolIndex();

        Assert.Equal(
            Render(index, Request(ConceptualQuery, json: false)),
            SearchRouteExecutor.RunSymbols(index, SymbolRoute, Request(ConceptualQuery, json: false)).Output);
    }

    [Fact]
    public async Task ShadowMode_NeverConsultsTheArmAndRendersLexicalBytes()
    {
        RecordingSymbolLookupIndex index = TwoSymbolIndex();
        var port = new RecordingPort { Matches = [Match(1, 0.1, "gadget-symbol", "src/Gadget.cs")] };
        await using SemanticEmbeddingSession session = NewSession();
        var fusion = new SemanticSymbolFusionArm(
            SemanticMode.Shadow,
            new SemanticSearchArm(Root, enabled: true, port.Factory, () => session));

        string output = SearchRouteExecutor
            .RunSymbols(index, SymbolRoute, Request(ConceptualQuery, json: false, fusion))
            .Output;

        Assert.Equal(Render(index, Request(ConceptualQuery, json: false)), output);
        Assert.Equal(0, port.OpenCount);
    }

    [Fact]
    public async Task LexicalOnlyRoute_NeverConsultsTheArmAndRendersLexicalBytes()
    {
        RecordingSymbolLookupIndex index = TwoSymbolIndex();
        var port = new RecordingPort { Matches = [Match(1, 0.1, "gadget-symbol", "src/Gadget.cs")] };
        await using SemanticEmbeddingSession session = NewSession();
        SemanticSymbolFusionArm fusion = OnArm(port, session);

        string output = SearchRouteExecutor
            .RunSymbols(index, SymbolRoute, Request("Widget", json: false, fusion))
            .Output;

        Assert.Equal(Render(index, Request("Widget", json: false)), output);
        Assert.Equal(0, port.OpenCount);
    }

    [Fact]
    public async Task StructuralContainerWinner_NeverConsultsTheAutomaticArm()
    {
        RecordingSymbolLookupIndex index = TwoSymbolIndex();
        var port = new RecordingPort { Matches = [Match(1, 0.1, "gadget-symbol", "src/Gadget.cs")] };
        await using SemanticEmbeddingSession session = NewSession();
        SemanticSymbolFusionArm fusion = OnArm(port, session);
        SymbolCandidate container = SearchTool.ToCandidate(
            index.FindBySymbolId("widget-symbol")!,
            score: 0) with
        {
            Origin = SymbolCandidateOrigin.Container,
        };

        IReadOnlyList<FusedCandidate>? result = fusion.Fuse(
            index,
            new SymbolFusionRequest(
                ConceptualQuery,
                [container],
                Limit: 6,
                Allows: _ => true,
                WorkspaceRoot: Root));

        Assert.Null(result);
        Assert.Equal(0, port.OpenCount);
    }

    [Fact]
    public async Task UnavailableArtifact_FailsOpenToTheLexicalBytes()
    {
        RecordingSymbolLookupIndex index = TwoSymbolIndex();
        var port = new RecordingPort { UnavailableReason = "the vector artifact is building" };
        await using SemanticEmbeddingSession session = NewSession();
        SemanticSymbolFusionArm fusion = OnArm(port, session);

        string output = SearchRouteExecutor
            .RunSymbols(index, SymbolRoute, Request(ConceptualQuery, json: false, fusion))
            .Output;

        Assert.Equal(Render(index, Request(ConceptualQuery, json: false)), output);
    }

    [Fact]
    public async Task SemanticFailureMidQuery_LeavesTheLexicalResultUnchanged()
    {
        RecordingSymbolLookupIndex index = TwoSymbolIndex();
        var port = new RecordingPort
        {
            Matches = [Match(1, 0.1, "gadget-symbol", "src/Gadget.cs")],
            SearchFailure = new VectorStoreException("the artifact went away mid-query"),
        };
        await using SemanticEmbeddingSession session = NewSession();
        SemanticSymbolFusionArm fusion = OnArm(port, session);

        string output = SearchRouteExecutor
            .RunSymbols(index, SymbolRoute, Request(ConceptualQuery, json: false, fusion))
            .Output;

        Assert.Equal(Render(index, Request(ConceptualQuery, json: false)), output);
    }

    [Fact]
    public async Task EmptyServedSemanticResult_LeavesTheLexicalOrderAndBytesUnchanged()
    {
        RecordingSymbolLookupIndex index = TwoSymbolIndex();
        var port = new RecordingPort();
        await using SemanticEmbeddingSession session = NewSession();
        SemanticSymbolFusionArm fusion = OnArm(port, session);

        string output = SearchRouteExecutor
            .RunSymbols(index, SymbolRoute, Request(ConceptualQuery, json: true, fusion))
            .Output;

        Assert.Equal(Render(index, Request(ConceptualQuery, json: true)), output);
    }

    [Fact]
    public async Task ConceptualQuery_ExtendsTheCandidateListWithASemanticOnlySymbol()
    {
        RecordingSymbolLookupIndex index = new(
            Symbol(0, "widget-symbol", "Widget", "src/Widget.cs"),
            Symbol(1, "gadget-symbol", "Gadget", "src/Gadget.cs"),
            Symbol(2, "converge-symbol", "Converge", "src/Converge.cs"));
        index.LexicalDocIds = [0, 1];
        var port = new RecordingPort { Matches = [Match(1, 0.05, "converge-symbol", "src/Converge.cs")] };
        await using SemanticEmbeddingSession session = NewSession();
        SemanticSymbolFusionArm fusion = OnArm(port, session);

        SearchRouteExecutionResult result = SearchRouteExecutor.RunSymbols(
            index, SymbolRoute, Request(ConceptualQuery, json: false, fusion));

        Assert.Contains("Converge", result.Output, StringComparison.Ordinal);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task ConceptualQuery_ReordersLexicalCandidatesTowardTheSemanticArm()
    {
        RecordingSymbolLookupIndex index = TwoSymbolIndex();
        var port = new RecordingPort { Matches = [Match(1, 0.05, "gadget-symbol", "src/Gadget.cs")] };
        await using SemanticEmbeddingSession session = NewSession();
        SemanticSymbolFusionArm fusion = OnArm(port, session);

        SearchRouteExecutionResult result = SearchRouteExecutor.RunSymbols(
            index, SymbolRoute, Request(ConceptualQuery, json: true, fusion));

        Assert.StartsWith("[{\"name\":\"Gadget\"", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FusedJsonRows_CarryAdditivePerArmRanksAndTheFusedScore()
    {
        RecordingSymbolLookupIndex index = TwoSymbolIndex();
        var port = new RecordingPort { Matches = [Match(1, 0.05, "gadget-symbol", "src/Gadget.cs")] };
        await using SemanticEmbeddingSession session = NewSession();
        SemanticSymbolFusionArm fusion = OnArm(port, session);

        string output = SearchRouteExecutor
            .RunSymbols(index, SymbolRoute, Request(ConceptualQuery, json: true, fusion))
            .Output;

        Assert.Contains("\"rrf_score\":", output, StringComparison.Ordinal);
        Assert.Contains("\"semantic_rank\":1", output, StringComparison.Ordinal);
        Assert.Contains("\"lexical_rank\":2", output, StringComparison.Ordinal);
        Assert.StartsWith("[", output, StringComparison.Ordinal);
    }

    [Fact]
    public void LexicalOnlyJsonRows_CarryNoFusionFields()
    {
        RecordingSymbolLookupIndex index = TwoSymbolIndex();

        string output = SearchRouteExecutor
            .RunSymbols(index, SymbolRoute, Request(ConceptualQuery, json: true))
            .Output;

        Assert.DoesNotContain("rrf_score", output, StringComparison.Ordinal);
        Assert.DoesNotContain("lexical_rank", output, StringComparison.Ordinal);
        Assert.DoesNotContain("semantic_rank", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SemanticHitsOutsideTheFileFilter_AreNeverRendered()
    {
        RecordingSymbolLookupIndex index = new(
            Symbol(0, "widget-symbol", "Widget", "src/Widget.cs"),
            Symbol(1, "gadget-symbol", "Gadget", "src/Gadget.cs"),
            Symbol(2, "vendor-symbol", "Vendor", "vendor/Vendor.cs"));
        index.LexicalDocIds = [0, 1];
        var port = new RecordingPort { Matches = [Match(1, 0.05, "vendor-symbol", "vendor/Vendor.cs")] };
        await using SemanticEmbeddingSession session = NewSession();
        SemanticSymbolFusionArm fusion = OnArm(port, session);

        SearchRouteExecutionResult result = SearchRouteExecutor.RunSymbols(
            index,
            SymbolRoute,
            Request(ConceptualQuery, json: false, fusion) with { FilePattern = "src/**" });

        Assert.DoesNotContain("Vendor", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SemanticHitsForTestSymbols_AreNeverRenderedWhenTestsAreHidden()
    {
        RecordingSymbolLookupIndex index = new(
            Symbol(0, "widget-symbol", "Widget", "src/Widget.cs"),
            Symbol(1, "gadget-symbol", "Gadget", "src/Gadget.cs"),
            Symbol(2, "spec-symbol", "WidgetSpec", "tests/WidgetSpec.cs", isTest: true));
        index.LexicalDocIds = [0, 1];
        var port = new RecordingPort { Matches = [Match(1, 0.05, "spec-symbol", "tests/WidgetSpec.cs")] };
        await using SemanticEmbeddingSession session = NewSession();
        SemanticSymbolFusionArm fusion = OnArm(port, session);

        SearchRouteExecutionResult result = SearchRouteExecutor.RunSymbols(
            index,
            SymbolRoute,
            Request(ConceptualQuery, json: false, fusion) with { ExcludeTests = true });

        Assert.DoesNotContain("WidgetSpec", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SemanticHitsMissingFromTheIndex_AreDroppedRatherThanRenderedBlank()
    {
        RecordingSymbolLookupIndex index = TwoSymbolIndex();
        var port = new RecordingPort { Matches = [Match(1, 0.05, "ghost-symbol", "src/Ghost.cs")] };
        await using SemanticEmbeddingSession session = NewSession();
        SemanticSymbolFusionArm fusion = OnArm(port, session);

        SearchRouteExecutionResult result = SearchRouteExecutor.RunSymbols(
            index, SymbolRoute, Request(ConceptualQuery, json: false, fusion));

        Assert.DoesNotContain("Ghost", result.Output, StringComparison.Ordinal);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task FusedCompactOutput_KeepsTheLexicalLayoutAndOnlyChangesOrder()
    {
        RecordingSymbolLookupIndex index = TwoSymbolIndex();
        var port = new RecordingPort { Matches = [Match(1, 0.05, "gadget-symbol", "src/Gadget.cs")] };
        await using SemanticEmbeddingSession session = NewSession();
        SemanticSymbolFusionArm fusion = OnArm(port, session);

        string fused = SearchRouteExecutor
            .RunSymbols(index, SymbolRoute, Request(ConceptualQuery, json: false, fusion))
            .Output;
        string lexical = Render(index, Request(ConceptualQuery, json: false));

        Assert.Equal(SortedResultLines(lexical), SortedResultLines(fused));
        Assert.EndsWith("next: inspect target=\"Widget\" depth=overview", lexical, StringComparison.Ordinal);
        Assert.EndsWith("next: inspect target=\"Gadget\" depth=overview", fused, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyLexicalResult_StillRendersTheLexicalMissPath()
    {
        var index = new RecordingSymbolLookupIndex();
        var port = new RecordingPort { Matches = [Match(1, 0.05, "ghost-symbol", "src/Ghost.cs")] };
        await using SemanticEmbeddingSession session = NewSession();
        SemanticSymbolFusionArm fusion = OnArm(port, session);

        string output = SearchRouteExecutor
            .RunSymbols(index, SymbolRoute, Request(ConceptualQuery, json: true, fusion))
            .Output;

        Assert.Equal(Render(index, Request(ConceptualQuery, json: true)), output);
    }

    [Fact]
    public async Task FusionArm_OpensTheArmForTheRequestWorkspaceRootNotTheAmbientOne()
    {
        const string targetRoot = "/ws-b";
        RecordingSymbolLookupIndex index = TwoSymbolIndex();
        var port = new RecordingPort { Matches = [Match(1, 0.05, "gadget-symbol", "src/Gadget.cs")] };
        await using SemanticEmbeddingSession session = NewSession();
        var opened = new List<string>();
        var fusion = new SemanticSymbolFusionArm(
            SemanticMode.On,
            root =>
            {
                opened.Add(root);
                return new SemanticSearchArm(root, enabled: true, port.Factory, () => session);
            });

        SearchRouteExecutor.RunSymbols(
            index,
            SymbolRoute,
            Request(ConceptualQuery, json: false, fusion, workspaceRoot: targetRoot));

        Assert.Equal([targetRoot], opened);
        Assert.DoesNotContain(Root, opened);
    }

    private static IReadOnlyList<string> SortedResultLines(string output) =>
    [
        .. output.Split('\n')
            .Where(static line => !line.StartsWith("next: inspect", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal),
    ];

    private static string Render(ISymbolLookupIndex index, SearchRouteExecutionRequest request) =>
        SearchTool.RenderSymbolCandidates(
            SearchRouteExecutor.CollectSymbolCandidates(index, SymbolRoute, request),
            request.Query,
            SymbolRoute.Mode,
            request.Limit,
            request.Json,
            out _);

    private static SearchRouteExecutionRequest Request(
        string query, bool json, ISymbolFusionArm? fusionArm = null, string workspaceRoot = Root) =>
        new(query, Limit: 10, Json: json, ExcludeTests: false, FusionArm: fusionArm, WorkspaceRoot: workspaceRoot);

    private static SemanticSymbolFusionArm OnArm(RecordingPort port, SemanticEmbeddingSession session) =>
        new(SemanticMode.On, new SemanticSearchArm(Root, enabled: true, port.Factory, () => session));

    private static SemanticEmbeddingSession NewSession() =>
        new(FakeSemanticSidecar.InProcessLauncher(), FastOptions);

    private static VectorMatch Match(long rowId, double distance, string unitId, string path) =>
        new(rowId, distance, unitId, path);

    private static RecordingSymbolLookupIndex TwoSymbolIndex() =>
        new(
            Symbol(0, "widget-symbol", "Widget", "src/Widget.cs"),
            Symbol(1, "gadget-symbol", "Gadget", "src/Gadget.cs"));

    private static IndexedSymbol Symbol(int docId, string symbolId, string name, string path, bool isTest = false) =>
        new(
            docId,
            symbolId,
            name,
            "void " + name + "()",
            "method",
            "csharp",
            path,
            3,
            6,
            ParentId: null,
            IsTest: isTest);

    private sealed class RecordingPort
    {
        public string? UnavailableReason { get; init; }

        public IReadOnlyList<VectorMatch> Matches { get; init; } = [];

        public SemanticStorageLane Lane { get; init; } =
            MillerSemanticContract.ParseStorageSchema(MillerSemanticContract.DefaultEncoder.StorageSchema);

        public Exception? SearchFailure { get; init; }

        public int OpenCount { get; private set; }

        public IVectorSearchPort? Factory(string workspaceRoot, out string? unavailableReason)
        {
            OpenCount++;
            if (UnavailableReason is not null)
            {
                unavailableReason = UnavailableReason;
                return null;
            }

            unavailableReason = null;
            return new Port(this);
        }

        private sealed class Port(RecordingPort owner) : IVectorSearchPort
        {
            public SemanticStorageLane Lane => owner.Lane;

            public IReadOnlyList<VectorMatch> Search(VectorUnitKind kind, ReadOnlySpan<sbyte> query, int k)
            {
                if (owner.SearchFailure is { } failure)
                    throw failure;

                return [.. owner.Matches.Take(k)];
            }

            public void Dispose()
            {
            }
        }
    }

    private sealed class RecordingSymbolLookupIndex : ISymbolLookupIndex
    {
        private readonly IReadOnlyList<IndexedSymbol> _symbols;

        public RecordingSymbolLookupIndex(params IndexedSymbol[] symbols) => _symbols = symbols;

        /// <summary>Which rows the lexical arm returns, so a semantic-only hit can be modelled.</summary>
        public IReadOnlyList<int>? LexicalDocIds { get; set; }

        public int DocumentCount => _symbols.Count;

        public IReadOnlySet<string> KnownExtensions { get; } = new HashSet<string>(StringComparer.Ordinal) { ".cs" };

        public IReadOnlyList<SearchHit> Search(string query, int limit = 10, SearchMode mode = SearchMode.Or) =>
            [.. Lexical().Take(limit).Select(symbol => new SearchHit(symbol.ToSearchableDocument(), 2.0))];

        public IndexedSymbol Resolve(int docId) => _symbols.Single(symbol => symbol.DocId == docId);

        public IReadOnlyList<IndexedSymbol> FindByName(string name) =>
            [.. _symbols.Where(symbol => string.Equals(symbol.Name, name, StringComparison.Ordinal))];

        public IndexedSymbol? FindBySymbolId(string symbolId) =>
            _symbols.FirstOrDefault(symbol => string.Equals(symbol.SymbolId, symbolId, StringComparison.Ordinal));

        public IReadOnlyList<IndexedSymbol> FindChildren(string parentId) => [];

        public IReadOnlyList<IndexedSymbol> FindByFilePath(string filePath) =>
            [.. _symbols.Where(symbol => string.Equals(symbol.FilePath, filePath, StringComparison.Ordinal))];

        public IReadOnlyList<IndexedSymbol> FindByFilePathFragment(string query, int limit) =>
            [.. _symbols.Where(symbol => symbol.FilePath.Contains(query, StringComparison.Ordinal)).Take(limit)];

        public bool IsIndexedFilePath(string path) =>
            _symbols.Any(symbol => string.Equals(symbol.FilePath, path, StringComparison.Ordinal));

        public string? ResolveIndexedFilePath(string target) =>
            _symbols.FirstOrDefault(symbol => string.Equals(symbol.FilePath, target, StringComparison.Ordinal))
                ?.FilePath;

        private IEnumerable<IndexedSymbol> Lexical() =>
            LexicalDocIds is null ? _symbols : _symbols.Where(symbol => LexicalDocIds.Contains(symbol.DocId));
    }
}
