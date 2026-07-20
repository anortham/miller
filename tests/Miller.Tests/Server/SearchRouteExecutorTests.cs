using Miller.Core.Search;
using Miller.Indexing;
using Miller.Server.Tools;
using Xunit;

namespace Miller.Tests.Server;

public sealed class SearchRouteExecutorTests
{
    [Fact]
    public void RunContent_UsesLegacyContentKindsAndIgnoresExcludeTests()
    {
        SearchRoute route = SearchRoutePlanner.Plan("content", regions: null);
        var index = new RecordingTextContentSearchIndex(
            TextHit("docs", TextContentKind.WorkspaceDocs, "docs/guide.md", "markdown", 50),
            TextHit("source", TextContentKind.WorkspaceSource, "src/Guide.cs", "csharp", 100));

        SearchRouteExecutionResult result = SearchRouteExecutor.RunContent(
            index,
            route,
            new SearchRouteExecutionRequest(
                Query: "guide",
                Limit: 5,
                Json: false,
                ExcludeTests: true,
                FilePattern: "docs/**",
                Language: "markdown"));

        Assert.Equal(1, result.Count);
        Assert.Equal(50, result.SourceBytes);
        Assert.Contains("docs/guide.md:7", result.Output);
        Assert.Equal([TextContentKind.WorkspaceDocs, TextContentKind.WorkspaceConfig], index.LastKinds);
        Assert.False(index.LastExcludeTests);
    }

    [Fact]
    public void RunTextContent_UsesRouteKindsAndSharedExcludeTestPolicy()
    {
        SearchRoute route = SearchRoutePlanner.Plan("source", regions: null);
        var index = new RecordingTextContentSearchIndex(
            TextHit("src", TextContentKind.WorkspaceSource, "src/Widget.cs", "csharp", 120),
            TextHit("test", TextContentKind.WorkspaceSource, "tests/WidgetTests.cs", "csharp", 80));

        SearchRouteExecutionResult result = SearchRouteExecutor.RunTextContent(
            index,
            route,
            new SearchRouteExecutionRequest(
                Query: "widget behavior",
                Limit: 5,
                Json: false,
                ExcludeTests: true,
                FilePattern: "src/**",
                Language: "csharp"));

        Assert.Equal(1, result.Count);
        Assert.Equal(120, result.SourceBytes);
        Assert.Contains("src/Widget.cs:7", result.Output);
        Assert.DoesNotContain("tests/WidgetTests.cs", result.Output);
        Assert.Equal([TextContentKind.WorkspaceSource], index.LastKinds);
        Assert.True(index.LastExcludeTests);
    }

    [Fact]
    public void RunRegions_UsesRouteKindsModeNoteAndSharedExcludeTestPolicy()
    {
        SearchRoute route = SearchRoutePlanner.Plan("source", "comment");
        var index = new RecordingRegionSearchIndex(
            new RegionSearchHit(
                "src/Widget.cs",
                2.0,
                12,
                "comment",
                "explains widget behavior",
                "explains widget behavior",
                "region-1",
                ContainingSymbolId: null,
                ContainingSymbolName: null,
                Language: "csharp"));

        SearchRouteExecutionResult result = SearchRouteExecutor.RunRegions(
            index,
            route,
            new SearchRouteExecutionRequest(
                Query: "widget behavior",
                Limit: 5,
                Json: false,
                ExcludeTests: null,
                FilePattern: "src/**",
                Language: "csharp"));

        Assert.Equal(1, result.Count);
        Assert.Contains("mode=source ignored; regions search uses source-region text.", result.Output);
        Assert.Contains("src/Widget.cs:12", result.Output);
        Assert.Equal(["comment"], index.LastKinds);
        Assert.True(index.LastExcludeTests);
    }

    [Fact]
    public void RunSymbols_UsesRouteModeAndDocLookup()
    {
        SearchRoute route = SearchRoutePlanner.Plan("file", regions: null);
        var index = new RecordingSymbolLookupIndex(
            Symbol(0, "widget-symbol", "Widget", "src/Widget.cs", isTest: false));
        IReadOnlyCollection<string>? docLookupIds = null;

        SearchRouteExecutionResult result = SearchRouteExecutor.RunSymbols(
            index,
            route,
            new SearchRouteExecutionRequest(
                Query: "src/Widget.cs",
                Limit: 5,
                Json: true,
                ExcludeTests: null,
                HasDocLookup: ids =>
                {
                    docLookupIds = ids;
                    return ids.ToHashSet(StringComparer.Ordinal);
                }));

        Assert.Equal(1, result.Count);
        Assert.Equal("src/Widget.cs", index.LastFileQuery);
        Assert.Equal(["widget-symbol"], docLookupIds);
        Assert.Contains("\"has_doc\":true", result.Output);
    }

    [Fact]
    public void CollectSymbolCandidates_ProjectsIndexRowsToTypedCandidates()
    {
        SearchRoute route = SearchRoutePlanner.Plan("symbol", regions: null);
        var index = new RecordingSymbolLookupIndex(
            Symbol(0, "widget-symbol", "Widget", "src/Widget.cs", isTest: false),
            Symbol(1, "gadget-symbol", "Gadget", "src/Gadget.cs", isTest: false));

        SymbolCandidateSet candidates = SearchRouteExecutor.CollectSymbolCandidates(
            index,
            route,
            new SearchRouteExecutionRequest(
                Query: "Widget",
                Limit: 5,
                Json: false,
                ExcludeTests: null));

        Assert.Collection(
            candidates.Candidates,
            first =>
            {
                Assert.Equal("widget-symbol", first.SymbolId);
                Assert.Equal("Widget", first.Name);
                Assert.Equal("src/Widget.cs", first.FilePath);
                Assert.Equal("method", first.Kind);
                Assert.Equal(3, first.StartLine);
                Assert.Equal("void Widget()", first.Signature);
                Assert.Equal(2.0, first.Score);
                Assert.Equal(0, first.DocId);
            },
            second => Assert.Equal("gadget-symbol", second.SymbolId));
        Assert.Empty(candidates.OutsideScope);
        Assert.False(candidates.FileMode);
    }

    [Fact]
    public void CollectSymbolCandidates_RejectsNonSymbolRoute()
    {
        SearchRoute route = SearchRoutePlanner.Plan("content", regions: null);
        var index = new RecordingSymbolLookupIndex(
            Symbol(0, "widget-symbol", "Widget", "src/Widget.cs", isTest: false));

        Assert.Throws<InvalidOperationException>(() => SearchRouteExecutor.CollectSymbolCandidates(
            index,
            route,
            new SearchRouteExecutionRequest(Query: "Widget", Limit: 5, Json: false, ExcludeTests: null)));
    }

    [Fact]
    public void RunSymbols_RendersOnlyWhatTheCandidateListCarries()
    {
        SearchRoute route = SearchRoutePlanner.Plan("symbol", regions: null);
        var index = new RecordingSymbolLookupIndex(
            Symbol(0, "widget-symbol", "Widget", "src/Widget.cs", isTest: false),
            Symbol(1, "gadget-symbol", "Gadget", "src/Gadget.cs", isTest: false));
        var request = new SearchRouteExecutionRequest(
            Query: "Widget",
            Limit: 5,
            Json: true,
            ExcludeTests: null);

        SymbolCandidateSet candidates = SearchRouteExecutor.CollectSymbolCandidates(index, route, request);
        var reordered = candidates with
        {
            Candidates = [candidates.Candidates[1], candidates.Candidates[0]],
        };

        string output = SearchTool.RenderSymbolCandidates(
            reordered,
            request.Query,
            route.Mode,
            request.Limit,
            request.Json,
            out int count);

        Assert.Equal(2, count);
        Assert.StartsWith("[{\"name\":\"Gadget\"", output, StringComparison.Ordinal);
        Assert.Equal(
            SearchRouteExecutor.RunSymbols(index, route, request).Output,
            SearchTool.RenderSymbolCandidates(
                candidates, request.Query, route.Mode, request.Limit, request.Json, out _));
    }

    private static TextContentSearchHit TextHit(
        string id,
        string contentKind,
        string path,
        string language,
        long sourceBytes) =>
        new(
            SourceId: id,
            ChunkId: id + "-chunk",
            ContentKind: contentKind,
            Path: path,
            Url: null,
            DisplayPath: path,
            Language: language,
            Score: 1.5,
            Line: 7,
            LineStart: 7,
            LineEnd: 8,
            ByteStart: 0,
            ByteEnd: 20,
            Snippet: "matched guide content",
            SourceBytes: sourceBytes,
            ContainingSymbolId: null,
            ContainingSymbolName: null);

    private static IndexedSymbol Symbol(int docId, string symbolId, string name, string path, bool isTest) =>
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

    private sealed class RecordingTextContentSearchIndex : ITextContentSearchIndex
    {
        private readonly IReadOnlyList<TextContentSearchHit> _hits;

        public RecordingTextContentSearchIndex(params TextContentSearchHit[] hits) => _hits = hits;

        public int DocumentCount => _hits.Count;

        public IReadOnlyCollection<string>? LastKinds { get; private set; }

        public bool? LastExcludeTests { get; private set; }

        public IReadOnlyList<TextContentSearchHit> Search(
            string query,
            string contentKind,
            int limit = 10,
            bool excludeTests = false) =>
            Search(query, [contentKind], limit, excludeTests);

        public IReadOnlyList<TextContentSearchHit> Search(
            string query,
            IReadOnlyCollection<string> contentKinds,
            int limit = 10,
            bool excludeTests = false)
        {
            LastKinds = contentKinds.ToArray();
            LastExcludeTests = excludeTests;
            return _hits
                .Where(hit => contentKinds.Contains(hit.ContentKind, StringComparer.Ordinal))
                .Where(hit => !excludeTests || !hit.DisplayPath.Contains("Tests", StringComparison.Ordinal))
                .Take(limit)
                .ToArray();
        }
    }

    private sealed class RecordingRegionSearchIndex : IRegionSearchIndex
    {
        private readonly IReadOnlyList<RegionSearchHit> _hits;

        public RecordingRegionSearchIndex(params RegionSearchHit[] hits) => _hits = hits;

        public int DocumentCount => _hits.Count;

        public long Revision => 42;

        public IReadOnlySet<string>? LastKinds { get; private set; }

        public bool? LastExcludeTests { get; private set; }

        public IReadOnlyList<RegionSearchHit> Search(
            string query,
            IReadOnlySet<string> kinds,
            int limit = 10,
            bool excludeTests = false)
        {
            LastKinds = kinds.ToHashSet(StringComparer.Ordinal);
            LastExcludeTests = excludeTests;
            return _hits
                .Where(hit => kinds.Contains(hit.Kind))
                .Where(hit => !excludeTests || !hit.Path.Contains("Tests", StringComparison.Ordinal))
                .Take(limit)
                .ToArray();
        }
    }

    private sealed class RecordingSymbolLookupIndex : ISymbolLookupIndex
    {
        private readonly IReadOnlyList<IndexedSymbol> _symbols;

        public RecordingSymbolLookupIndex(params IndexedSymbol[] symbols) => _symbols = symbols;

        public int DocumentCount => _symbols.Count;

        public IReadOnlySet<string> KnownExtensions { get; } = new HashSet<string>(StringComparer.Ordinal)
        {
            ".cs",
        };

        public string? LastFileQuery { get; private set; }

        public IReadOnlyList<SearchHit> Search(string query, int limit = 10, SearchMode mode = SearchMode.Or) =>
            _symbols
                .Take(limit)
                .Select(symbol => new SearchHit(symbol.ToSearchableDocument(), 2.0))
                .ToArray();

        public IndexedSymbol Resolve(int docId) => _symbols.Single(symbol => symbol.DocId == docId);

        public IReadOnlyList<IndexedSymbol> FindByName(string name) =>
            _symbols.Where(symbol => string.Equals(symbol.Name, name, StringComparison.Ordinal)).ToArray();

        public IndexedSymbol? FindBySymbolId(string symbolId) =>
            _symbols.FirstOrDefault(symbol => string.Equals(symbol.SymbolId, symbolId, StringComparison.Ordinal));

        public IReadOnlyList<IndexedSymbol> FindChildren(string parentId) => [];

        public IReadOnlyList<IndexedSymbol> FindByFilePath(string filePath) =>
            _symbols.Where(symbol => string.Equals(symbol.FilePath, filePath, StringComparison.Ordinal)).ToArray();

        public IReadOnlyList<IndexedSymbol> FindByFilePathFragment(string query, int limit)
        {
            LastFileQuery = query;
            return _symbols
                .Where(symbol => symbol.FilePath.Contains(query, StringComparison.Ordinal))
                .Take(limit)
                .ToArray();
        }

        public bool IsIndexedFilePath(string path) =>
            _symbols.Any(symbol => string.Equals(symbol.FilePath, path, StringComparison.Ordinal));

        public string? ResolveIndexedFilePath(string target) =>
            _symbols.FirstOrDefault(symbol => string.Equals(symbol.FilePath, target, StringComparison.Ordinal))
                ?.FilePath;
    }
}
