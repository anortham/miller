using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Miller.Core.Search;
using Miller.Indexing;
using Miller.Indexing.Semantic;
using Miller.Server.Tools;
using Miller.Server.Telemetry;
using Miller.Server.Workspaces;
using Xunit;

namespace Miller.Tests.Server;

public sealed class SearchToolRescueTests
{
    private const string Root = "/ws";
    private const string ConceptualQuery = "how does the workspace refresh converge";
    private const string IdentifierQuery = "WorkspaceRefreshService";

    [Fact]
    public void SemanticRescue_WhenLexicalRungsFindNothing_RendersLabelledSemanticRows()
    {
        StubSemanticTextArm arm = ArmWith(
            symbols: [Hit(symbolId: "widget-symbol", path: "src/Widget.cs", rank: 1)]);
        SearchTool tool = ToolWith(arm);

        string output = tool.Search(ConceptualQuery);

        Assert.Contains("Semantic matches also found:", output, StringComparison.Ordinal);
        Assert.Contains("semantic symbol", output, StringComparison.Ordinal);
        Assert.Contains("Widget", output, StringComparison.Ordinal);
    }

    [Fact]
    public void SemanticRescue_WithChunkHitsOnly_LabelsThemSemanticDocs()
    {
        StubSemanticTextArm arm = ArmWith(
            chunks: [Hit(docId: "docs/design.md#1", path: "docs/design.md", rank: 1)]);
        SearchTool tool = ToolWith(arm);

        string output = tool.Search(ConceptualQuery);

        Assert.Contains("semantic docs", output, StringComparison.Ordinal);
        Assert.Contains("docs/design.md", output, StringComparison.Ordinal);
        Assert.DoesNotContain("semantic symbol", output, StringComparison.Ordinal);
    }

    [Fact]
    public void SemanticRescue_EmitsAtMostTwoRowsAndOneAffordance()
    {
        StubSemanticTextArm arm = ArmWith(
            symbols:
            [
                Hit(symbolId: "widget-symbol", path: "src/Widget.cs", rank: 1),
                Hit(symbolId: "gadget-symbol", path: "src/Gadget.cs", rank: 2),
            ],
            chunks:
            [
                Hit(docId: "docs/design.md#1", path: "docs/design.md", rank: 1),
                Hit(docId: "docs/other.md#1", path: "docs/other.md", rank: 2),
            ]);
        SearchTool tool = ToolWith(arm);

        string output = tool.Search(ConceptualQuery);

        string[] rows =
        [
            .. output.Split('\n').Where(line =>
                line.Contains("semantic symbol", StringComparison.Ordinal) ||
                line.Contains("semantic docs", StringComparison.Ordinal)),
        ];
        Assert.Equal(2, rows.Length);
        Assert.Equal(1, output.Split('\n').Count(IsAffordance));
    }

    [Fact]
    public void SemanticRescue_IsNeverConsultedForJsonOutput()
    {
        StubSemanticTextArm arm = ArmWith(
            symbols: [Hit(symbolId: "widget-symbol", path: "src/Widget.cs", rank: 1)]);

        Assert.Equal(
            ToolWith(arm: null).Search(ConceptualQuery, format: "json"),
            ToolWith(arm).Search(ConceptualQuery, format: "json"));
        Assert.Equal(0, arm.SymbolQueries);
    }

    [Fact]
    public void SemanticRescue_IsNeverConsultedForAnIdentifierShapedQuery()
    {
        StubSemanticTextArm arm = ArmWith(
            symbols: [Hit(symbolId: "widget-symbol", path: "src/Widget.cs", rank: 1)]);

        Assert.Equal(
            ToolWith(arm: null).Search(IdentifierQuery),
            ToolWith(arm).Search(IdentifierQuery));
        Assert.Equal(0, arm.SymbolQueries);
    }

    [Fact]
    public void SemanticRescue_WhenTheArmCannotServe_IsByteIdenticalToLexicalOnly()
    {
        var arm = new StubSemanticTextArm { Unavailable = "the vector artifact is building" };

        Assert.Equal(
            ToolWith(arm: null).Search(ConceptualQuery),
            ToolWith(arm).Search(ConceptualQuery));
    }

    [Fact]
    public void SemanticRescue_WhenTheArmServesNothing_IsByteIdenticalToLexicalOnly()
    {
        var arm = new StubSemanticTextArm();

        Assert.Equal(
            ToolWith(arm: null).Search(ConceptualQuery),
            ToolWith(arm).Search(ConceptualQuery));
    }

    [Fact]
    public void SemanticRescue_NeverPreemptsALexicalSourceRescue()
    {
        StubSemanticTextArm arm = ArmWith(
            symbols: [Hit(symbolId: "widget-symbol", path: "src/Widget.cs", rank: 1)]);
        SearchTool tool = ToolWith(
            arm,
            TextContentIndex(SourceHit("src/Api.cs", line: 42, snippet: "converge the workspace refresh")));

        string output = tool.Search(ConceptualQuery);

        Assert.Contains("Source matches also found:", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Semantic matches also found:", output, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceMode_AppendsTheNotIndexedNote_WhenTheArmWasConsultable()
    {
        StubSemanticTextArm arm = ArmWith(
            chunks: [Hit(docId: "docs/design.md#1", path: "docs/design.md", rank: 1)]);
        SearchTool tool = ToolWith(
            arm,
            TextContentIndex(SourceHit("src/Api.cs", line: 42, snippet: "converge the workspace refresh")));

        string output = tool.Search(ConceptualQuery, mode: "source");

        Assert.Contains("source_chunks_not_indexed", output, StringComparison.Ordinal);
        Assert.DoesNotContain("semantic symbol", output, StringComparison.Ordinal);
        Assert.DoesNotContain("semantic docs", output, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceMode_OmitsTheNote_WhenTheArmCouldNotBeConsulted()
    {
        var arm = new StubSemanticTextArm { Unavailable = "the vector artifact is building" };
        ITextContentSearchIndex content =
            TextContentIndex(SourceHit("src/Api.cs", line: 42, snippet: "converge the workspace refresh"));

        Assert.Equal(
            ToolWith(arm: null, content).Search(ConceptualQuery, mode: "source"),
            ToolWith(arm, content).Search(ConceptualQuery, mode: "source"));
    }

    [Fact]
    public void SourceMode_OmitsTheNote_ForAnIdentifierShapedQuery()
    {
        StubSemanticTextArm arm = ArmWith(
            chunks: [Hit(docId: "docs/design.md#1", path: "docs/design.md", rank: 1)]);
        ITextContentSearchIndex content =
            TextContentIndex(SourceHit("src/Api.cs", line: 42, snippet: IdentifierQuery));

        Assert.Equal(
            ToolWith(arm: null, content).Search(IdentifierQuery, mode: "source"),
            ToolWith(arm, content).Search(IdentifierQuery, mode: "source"));
    }

    [Fact]
    public void SourceMode_LeavesJsonUntouched()
    {
        StubSemanticTextArm arm = ArmWith(
            chunks: [Hit(docId: "docs/design.md#1", path: "docs/design.md", rank: 1)]);
        ITextContentSearchIndex content =
            TextContentIndex(SourceHit("src/Api.cs", line: 42, snippet: "converge the workspace refresh"));

        Assert.Equal(
            ToolWith(arm: null, content).Search(ConceptualQuery, mode: "source", format: "json"),
            ToolWith(arm, content).Search(ConceptualQuery, mode: "source", format: "json"));
    }

    [Fact]
    public void ContentMode_HybridPromotesTheSemanticallyNearestChunk()
    {
        StubSemanticTextArm arm = ArmWith(
            chunks: [Hit(docId: "docs/converge.md#1", path: "docs/converge.md", rank: 1)]);
        ITextContentSearchIndex content = TextContentIndex(
            DocsHit("docs/first.md", line: 3, snippet: "refresh", score: 9.0),
            DocsHit("docs/converge.md", line: 7, snippet: "converge", score: 1.0));

        string output = ToolWith(arm, content).Search(ConceptualQuery, mode: "content");

        Assert.True(
            output.IndexOf("docs/converge.md", StringComparison.Ordinal)
                < output.IndexOf("docs/first.md", StringComparison.Ordinal),
            "the semantically nearest chunk should lead the content page");
    }

    [Fact]
    public void ContentMode_KeepsMembership_WhenTheSemanticArmReordersIt()
    {
        StubSemanticTextArm arm = ArmWith(
            chunks: [Hit(docId: "docs/converge.md#1", path: "docs/converge.md", rank: 1)]);
        ITextContentSearchIndex content = TextContentIndex(
            DocsHit("docs/first.md", line: 3, snippet: "refresh", score: 9.0),
            DocsHit("docs/converge.md", line: 7, snippet: "converge", score: 1.0));

        string output = ToolWith(arm, content).Search(ConceptualQuery, mode: "content");

        Assert.Contains("docs/first.md", output, StringComparison.Ordinal);
        Assert.Contains("docs/converge.md", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ContentMode_WhenTheArmCannotServe_IsByteIdenticalToLexicalOnly()
    {
        var arm = new StubSemanticTextArm { Unavailable = "the vector artifact is building" };
        ITextContentSearchIndex content = TextContentIndex(
            DocsHit("docs/first.md", line: 3, snippet: "refresh", score: 9.0),
            DocsHit("docs/converge.md", line: 7, snippet: "converge", score: 1.0));

        Assert.Equal(
            ToolWith(arm: null, content).Search(ConceptualQuery, mode: "content"),
            ToolWith(arm, content).Search(ConceptualQuery, mode: "content"));
    }

    [Fact]
    public void ContentMode_IsNeverConsultedForAnIdentifierShapedQuery()
    {
        StubSemanticTextArm arm = ArmWith(
            chunks: [Hit(docId: "docs/converge.md#1", path: "docs/converge.md", rank: 1)]);
        ITextContentSearchIndex content = TextContentIndex(
            DocsHit("docs/first.md", line: 3, snippet: IdentifierQuery, score: 9.0),
            DocsHit("docs/converge.md", line: 7, snippet: IdentifierQuery, score: 1.0));

        Assert.Equal(
            ToolWith(arm: null, content).Search(IdentifierQuery, mode: "content"),
            ToolWith(arm, content).Search(IdentifierQuery, mode: "content"));
        Assert.Equal(0, arm.ChunkQueries);
    }

    [Fact]
    public void SemanticTextArm_UnderShadowMode_NeverOpensTheArm()
    {
        var arm = new SemanticTextArm(
            SemanticMode.Shadow,
            _ => throw new InvalidOperationException("shadow must never open the semantic arm"));

        Assert.False(arm.QuerySymbols(Root, ConceptualQuery, 4, allow: null).Served);
        Assert.False(arm.QueryChunks(Root, ConceptualQuery, 4).Served);
    }

    [Fact]
    public void SemanticTextArm_UnderOffMode_NeverOpensTheArm()
    {
        var arm = new SemanticTextArm(
            SemanticMode.Off,
            _ => throw new InvalidOperationException("off must never open the semantic arm"));

        Assert.False(arm.QuerySymbols(Root, ConceptualQuery, 4, allow: null).Served);
        Assert.False(arm.QueryChunks(Root, ConceptualQuery, 4).Served);
    }

    [Theory]
    [InlineData(true, false, "semantic_symbol")]
    [InlineData(false, true, "semantic_docs")]
    [InlineData(true, true, "semantic_mixed")]
    public void SemanticRescue_StampsItsKind_WithoutRecordingTheQuery(
        bool withSymbols, bool withChunks, string expectedKind)
    {
        StubSemanticTextArm arm = ArmWith(
            symbols: withSymbols ? [Hit(symbolId: "widget-symbol", path: "src/Widget.cs", rank: 1)] : [],
            chunks: withChunks ? [Hit(docId: "docs/design.md#1", path: "docs/design.md", rank: 1)] : []);
        string dir = Path.Combine(Path.GetTempPath(), "miller-rescue-kind-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string telemetryDb = Path.Combine(dir, "telemetry.db");

        try
        {
            using (var ledger = TelemetryLedger.Open(telemetryDb, workspaceId: "current-ws", Root))
            {
                using var scope = ledger.Measure("search", op: "auto");
                ToolWith(arm).Search(ConceptualQuery);
            }

            string metadata = ReadTelemetryMetadata(telemetryDb);
            Assert.Contains($"\"auto_rescue_kind\":\"{expectedKind}\"", metadata, StringComparison.Ordinal);
            Assert.DoesNotContain(ConceptualQuery, metadata, StringComparison.Ordinal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private static string ReadTelemetryMetadata(string dbPath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT metadata_json FROM tool_telemetry LIMIT 1;";
        return Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
    }

    [Fact]
    public void SearchTool_ActivatesOverTheRegisteredSemanticServices()
    {
        var provider = new RescueSearchProvider(
            SymbolSearchProjection.Build([Symbol(0, "widget-symbol", "Widget", "src/Widget.cs")]),
            TextContentIndex());
        var services = new ServiceCollection();
        services.AddSingleton<IWorkspaceSearchProvider>(provider);
        services.AddSingleton<IWorkspaceContentSearchProvider>(provider);
        services.AddSingleton<IWorkspaceRegionSearchProvider>(provider);
        services.AddSingleton<IWorkspaceTextContentSearchProvider>(provider);
        services.AddSingleton(_ => VectorSidecar.FromEnvironment());
        services.AddSingleton(_ => new Lazy<SemanticEmbeddingSession?>(() => null));
        using ServiceProvider built = services.BuildServiceProvider();

        Assert.NotNull(ActivatorUtilities.CreateInstance<SearchTool>(built));
    }

    private static bool IsAffordance(string line) =>
        line.StartsWith("next: ", StringComparison.Ordinal) ||
        line.StartsWith("Try: ", StringComparison.Ordinal) ||
        line.StartsWith("Rerun with ", StringComparison.Ordinal);

    private static StubSemanticTextArm ArmWith(
        IReadOnlyList<SemanticHit>? symbols = null,
        IReadOnlyList<SemanticHit>? chunks = null) =>
        new() { Symbols = symbols ?? [], Chunks = chunks ?? [] };

    private static SemanticHit Hit(
        string path,
        int rank,
        string? symbolId = null,
        string? docId = null) =>
        new(symbolId, docId, path, rank, Cosine: 0.9 - (rank * 0.01));

    private static SearchTool ToolWith(ISemanticTextArm? arm, ITextContentSearchIndex? content = null)
    {
        var provider = new RescueSearchProvider(
            SymbolSearchProjection.Build([Symbol(0, "widget-symbol", "Widget", "src/Widget.cs"),
                Symbol(1, "gadget-symbol", "Gadget", "src/Gadget.cs")]),
            content ?? TextContentIndex());
        return new SearchTool(provider, provider, provider, provider, fusionArm: null, semanticArm: arm);
    }

    private static IndexedSymbol Symbol(int docId, string symbolId, string name, string path) =>
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
            IsTest: false);

    private static ITextContentSearchIndex TextContentIndex(params TextContentSearchHit[] hits) =>
        new StubTextContentIndex(hits);

    private static TextContentSearchHit SourceHit(string path, int line, string snippet, double score = 2.0) =>
        CorpusHit(path, TextContentKind.WorkspaceSource, line, snippet, "csharp", score);

    private static TextContentSearchHit DocsHit(string path, int line, string snippet, double score = 2.0) =>
        CorpusHit(path, TextContentKind.WorkspaceDocs, line, snippet, "markdown", score);

    private static TextContentSearchHit CorpusHit(
        string path,
        string contentKind,
        int line,
        string snippet,
        string language,
        double score) =>
        new(
            contentKind + ":" + path,
            contentKind + ":" + path + ":" + line,
            contentKind,
            path,
            Url: null,
            DisplayPath: path,
            language,
            score,
            line,
            LineStart: Math.Max(1, line - 1),
            LineEnd: line + 1,
            ByteStart: 0,
            ByteEnd: 64,
            snippet,
            SourceBytes: 128,
            ContainingSymbolId: null,
            ContainingSymbolName: null);

    private sealed class StubSemanticTextArm : ISemanticTextArm
    {
        public IReadOnlyList<SemanticHit> Symbols { get; init; } = [];

        public IReadOnlyList<SemanticHit> Chunks { get; init; } = [];

        public string? Unavailable { get; init; }

        public int SymbolQueries { get; private set; }

        public int ChunkQueries { get; private set; }

        public SemanticQueryResult QuerySymbols(
            string workspaceRoot, string query, int k, Func<VectorMatch, bool>? allow)
        {
            SymbolQueries++;
            return Unavailable is { } reason
                ? SemanticQueryResult.Unavailable(reason)
                : new SemanticQueryResult([.. Symbols.Take(k)], null);
        }

        public SemanticQueryResult QueryChunks(string workspaceRoot, string query, int k)
        {
            ChunkQueries++;
            return Unavailable is { } reason
                ? SemanticQueryResult.Unavailable(reason)
                : new SemanticQueryResult([.. Chunks.Take(k)], null);
        }
    }

    private sealed class StubTextContentIndex(IReadOnlyList<TextContentSearchHit> hits) : ITextContentSearchIndex
    {
        public int DocumentCount => hits.Count;

        public IReadOnlyList<TextContentSearchHit> Search(
            string query, string contentKind, int limit, bool excludeTests) =>
            Search(query, [contentKind], limit, excludeTests);

        public IReadOnlyList<TextContentSearchHit> Search(
            string query, IReadOnlyCollection<string> contentKinds, int limit, bool excludeTests) =>
            [.. hits.Where(hit => contentKinds.Contains(hit.ContentKind)).Take(limit)];
    }

    private sealed class RescueSearchProvider(ISymbolLookupIndex symbols, ITextContentSearchIndex content)
        : IWorkspaceSearchProvider,
            IWorkspaceContentSearchProvider,
            IWorkspaceTextContentSearchProvider,
            IWorkspaceRegionSearchProvider
    {
        public WorkspaceRegionSearchContext ResolveRegionSearch(string? workspaceId, bool ensureFresh) =>
            throw new NotSupportedException("RescueSearchProvider serves no region route.");


        public WorkspaceSymbolSearchContext ResolveSymbolSearch(string? workspaceId, bool ensureFresh) =>
            new(symbols, "symbols.db", "current-ws", Root, Revision: 1, IndexFresh: true, "current",
                WarningText: null, DisplayId: "current-ws");

        public WorkspaceContentSearchContext ResolveContentSearch(string? workspaceId, bool ensureFresh) =>
            throw new NotSupportedException("RescueSearchProvider serves the corpus, not the legacy projection.");

        public WorkspaceTextContentSearchContext ResolveTextContentSearch(string? workspaceId, bool ensureFresh) =>
            new(content, "content.db", "current-ws", Root, Revision: 1, IndexFresh: true, "current",
                WarningText: null, DisplayId: "current-ws");
    }
}
