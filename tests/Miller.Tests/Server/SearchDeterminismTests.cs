using Miller.Core.Search;
using Miller.Indexing;
using Miller.Indexing.Semantic;
using Miller.Server.Cli;
using Miller.Server.Tools;
using Miller.Tests.Support;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// The determinism contract behind the CLI's <c>--arm</c> evaluation lever: with a fixed store and fixed fake
/// vectors, two identical runs of one arm produce byte-identical output. Evaluation compares runs against each
/// other, so any run-to-run drift — dictionary enumeration order, culture-sensitive number formatting, an
/// unstable tie-break — would read as a retrieval-quality change that never happened.
/// </summary>
public sealed class SearchDeterminismTests
{
    private const string Root = "/ws";
    private const string ConceptualQuery = "how does the workspace refresh converge";
    private const string SymbolQuery = "Widget";

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LexicalArm_RepeatedRuns_AreByteIdentical(bool json)
    {
        FakeIndex index = ThreeSymbolIndex();

        Assert.Equal(
            SearchRouteExecutor.RunSymbols(index, SymbolRoute, Request(ConceptualQuery, json)).Output,
            SearchRouteExecutor.RunSymbols(index, SymbolRoute, Request(ConceptualQuery, json)).Output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HybridArm_RepeatedRuns_AreByteIdentical(bool json)
    {
        FakeIndex index = ThreeSymbolIndex();
        await using SemanticEmbeddingSession session = NewSession();
        ForcedHybridFusionArm fusion = ForcedHybrid(FixedVectorPort(), session);

        Assert.Equal(
            SearchRouteExecutor.RunSymbols(index, SymbolRoute, Request(ConceptualQuery, json, fusion)).Output,
            SearchRouteExecutor.RunSymbols(index, SymbolRoute, Request(ConceptualQuery, json, fusion)).Output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SemanticArm_RepeatedRuns_AreByteIdentical(bool json)
    {
        FakeIndex index = ThreeSymbolIndex();
        await using SemanticEmbeddingSession session = NewSession();
        SemanticSearchArm arm = Arm(FixedVectorPort(), session);

        Assert.Equal(
            CliSemanticRender.Symbols(index, Query(arm), ConceptualQuery, limit: 10, json),
            CliSemanticRender.Symbols(index, Query(arm), ConceptualQuery, limit: 10, json));
    }

    [Fact]
    public async Task ForcedHybrid_FusesEvenWhenThePolicyWouldRouteLexicalOnly()
    {
        FakeIndex index = ThreeSymbolIndex();
        await using SemanticEmbeddingSession session = NewSession();
        ForcedHybridFusionArm fusion = ForcedHybrid(FixedVectorPort(), session);

        Assert.False(SemanticQueryPolicy.Route(SymbolQuery, LexicalEvidence.None).IsHybrid);
        Assert.Contains(
            "\"rrf_score\":",
            SearchRouteExecutor.RunSymbols(index, SymbolRoute, Request(SymbolQuery, json: true, fusion)).Output,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForcedHybrid_AbstainsWhenTheArtifactServesNothing()
    {
        FakeIndex index = ThreeSymbolIndex();
        await using SemanticEmbeddingSession session = NewSession();
        var port = new FixedPort { UnavailableReason = "the vector artifact is building" };
        ForcedHybridFusionArm fusion = ForcedHybrid(port, session);

        Assert.Equal(
            SearchRouteExecutor.RunSymbols(index, SymbolRoute, Request(ConceptualQuery, json: true)).Output,
            SearchRouteExecutor.RunSymbols(index, SymbolRoute, Request(ConceptualQuery, json: true, fusion)).Output);
    }

    [Fact]
    public async Task SemanticRender_ReportsRankAndCosineForEveryServedHit()
    {
        FakeIndex index = ThreeSymbolIndex();
        await using SemanticEmbeddingSession session = NewSession();
        SemanticSearchArm arm = Arm(FixedVectorPort(), session);

        string output = CliSemanticRender.Symbols(index, Query(arm), ConceptualQuery, limit: 10, json: true);

        Assert.StartsWith("[{", output, StringComparison.Ordinal);
        Assert.Contains("\"rank\":1", output, StringComparison.Ordinal);
        Assert.Contains("\"cosine\":", output, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"Converge\"", output, StringComparison.Ordinal);
    }

    [Fact]
    public void SemanticRender_NoHits_SaysSoRatherThanRenderingAnEmptyBlock()
    {
        Assert.Contains(
            "no semantic neighbours",
            CliSemanticRender.Symbols(ThreeSymbolIndex(), [], ConceptualQuery, limit: 10, json: false),
            StringComparison.Ordinal);
    }

    [Fact]
    public void SemanticRender_HitsMissingFromTheIndex_AreDroppedRatherThanRenderedBlank()
    {
        FakeIndex index = ThreeSymbolIndex();
        SemanticHit[] hits = [new SemanticHit("ghost-symbol", null, "src/Ghost.cs", 1, 0.9)];

        Assert.DoesNotContain(
            "Ghost",
            CliSemanticRender.Symbols(index, hits, ConceptualQuery, limit: 10, json: false),
            StringComparison.Ordinal);
    }

    [Fact]
    public void SemanticRender_CosineFormatting_IsCultureInvariant()
    {
        FakeIndex index = ThreeSymbolIndex();
        SemanticHit[] hits = [new SemanticHit("widget-symbol", null, "src/Widget.cs", 1, 0.8125)];

        Assert.Contains(
            "cos 0.8125",
            CliSemanticRender.Symbols(index, hits, ConceptualQuery, limit: 10, json: false),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ParseSearchArm_AcceptsTheThreeArmsAndTheAbsentDefault()
    {
        Assert.Equal(CliSearchArm.Policy, Parse(null));
        Assert.Equal(CliSearchArm.Lexical, Parse("lexical"));
        Assert.Equal(CliSearchArm.Lexical, Parse("LEXICAL"));
        Assert.Equal(CliSearchArm.Semantic, Parse(" semantic "));
        Assert.Equal(CliSearchArm.Hybrid, Parse("hybrid"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("vector")]
    [InlineData("both")]
    public void ParseSearchArm_RejectsAnythingElse(string raw) =>
        Assert.False(CliDispatch.TryParseSearchArm(raw, out _));

    private static CliSearchArm Parse(string? raw)
    {
        Assert.True(CliDispatch.TryParseSearchArm(raw, out CliSearchArm parsed));
        return parsed;
    }

    private static IReadOnlyList<SemanticHit> Query(SemanticSearchArm arm) =>
        arm.QuerySymbolsAsync(ConceptualQuery, 10, allow: null).GetAwaiter().GetResult().Hits;

    private static SearchRouteExecutionRequest Request(
        string query, bool json, ISymbolFusionArm? fusionArm = null) =>
        new(query, Limit: 10, Json: json, ExcludeTests: false, FusionArm: fusionArm);

    private static ForcedHybridFusionArm ForcedHybrid(FixedPort port, SemanticEmbeddingSession session)
    {
        SemanticSearchArm arm = Arm(port, session);
        return new ForcedHybridFusionArm(() => arm);
    }

    private static SemanticSearchArm Arm(FixedPort port, SemanticEmbeddingSession session) =>
        new(Root, enabled: true, port.Factory, () => session);

    private static SemanticEmbeddingSession NewSession() =>
        new(FakeSemanticSidecar.InProcessLauncher(), FastOptions);

    private static FixedPort FixedVectorPort() => new()
    {
        Matches =
        [
            new VectorMatch(2, 0.05, "converge-symbol", "src/Converge.cs"),
            new VectorMatch(1, 0.40, "gadget-symbol", "src/Gadget.cs"),
        ],
    };

    private static FakeIndex ThreeSymbolIndex() =>
        new(
            Symbol(0, "widget-symbol", "Widget", "src/Widget.cs"),
            Symbol(1, "gadget-symbol", "Gadget", "src/Gadget.cs"),
            Symbol(2, "converge-symbol", "Converge", "src/Converge.cs"));

    private static IndexedSymbol Symbol(int docId, string symbolId, string name, string path) =>
        new(docId, symbolId, name, "void " + name + "()", "method", "csharp", path, 3, 6, null, false);

    private sealed class FixedPort
    {
        public string? UnavailableReason { get; init; }

        public IReadOnlyList<VectorMatch> Matches { get; init; } = [];

        public IVectorSearchPort? Factory(string workspaceRoot, out string? unavailableReason)
        {
            if (UnavailableReason is not null)
            {
                unavailableReason = UnavailableReason;
                return null;
            }

            unavailableReason = null;
            return new Port(this);
        }

        private sealed class Port(FixedPort owner) : IVectorSearchPort
        {
            public SemanticStorageLane Lane { get; } =
                MillerSemanticContract.ParseStorageSchema(MillerSemanticContract.DefaultEncoder.StorageSchema);

            public IReadOnlyList<VectorMatch> Search(VectorUnitKind kind, ReadOnlySpan<sbyte> query, int k) =>
                [.. owner.Matches.Take(k)];

            public void Dispose()
            {
            }
        }
    }

    private sealed class FakeIndex(params IndexedSymbol[] symbols) : ISymbolLookupIndex
    {
        public int DocumentCount => symbols.Length;

        public IReadOnlySet<string> KnownExtensions { get; } = new HashSet<string>(StringComparer.Ordinal) { ".cs" };

        public IReadOnlyList<SearchHit> Search(string query, int limit = 10, SearchMode mode = SearchMode.Or) =>
        [
            .. symbols
                .Where(static symbol => symbol.DocId < 2)
                .Take(limit)
                .Select(static symbol => new SearchHit(symbol.ToSearchableDocument(), 2.0)),
        ];

        public IndexedSymbol Resolve(int docId) => symbols.Single(symbol => symbol.DocId == docId);

        public IReadOnlyList<IndexedSymbol> FindByName(string name) =>
            [.. symbols.Where(symbol => string.Equals(symbol.Name, name, StringComparison.Ordinal))];

        public IndexedSymbol? FindBySymbolId(string symbolId) =>
            symbols.FirstOrDefault(symbol => string.Equals(symbol.SymbolId, symbolId, StringComparison.Ordinal));

        public IReadOnlyList<IndexedSymbol> FindChildren(string parentId) => [];

        public IReadOnlyList<IndexedSymbol> FindByFilePath(string filePath) =>
            [.. symbols.Where(symbol => string.Equals(symbol.FilePath, filePath, StringComparison.Ordinal))];

        public IReadOnlyList<IndexedSymbol> FindByFilePathFragment(string query, int limit) =>
            [.. symbols.Where(symbol => symbol.FilePath.Contains(query, StringComparison.Ordinal)).Take(limit)];

        public bool IsIndexedFilePath(string path) =>
            symbols.Any(symbol => string.Equals(symbol.FilePath, path, StringComparison.Ordinal));

        public string? ResolveIndexedFilePath(string target) =>
            symbols.FirstOrDefault(symbol => string.Equals(symbol.FilePath, target, StringComparison.Ordinal))
                ?.FilePath;
    }
}
