using Miller.Core.Search;
using Miller.Indexing;
using Miller.Server.Tools;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the cascading over-fetch retry in the <c>search</c> tool cores: post-search filters (test hiding,
/// low-signal kinds, <c>file_pattern</c>, <c>language</c>) run AFTER the index window is cut, so without the
/// retry a heavily filtered query returned fewer than <c>limit</c> rows — even "No results." — while matches
/// still existed past the window. Each test builds a corpus where 500+ index-ordered hits are filtered out
/// and asserts the page still fills and the "… N more" overflow note stays accurate. Escalation must preserve
/// index order: each retry re-fetches and re-filters from scratch, never re-sorts.
/// </summary>
public sealed class SearchToolOverFetchEscalationTests
{
    private static IndexedSymbol Symbol(int docId, string name, string path, bool isTest = false, string language = "csharp") =>
        new(docId, $"sym-{docId:d4}", name, Signature: null, "class", language, path,
            StartLine: 1, EndLine: 1, ParentId: null, IsTest: isTest);

    [Fact]
    public void Run_TestHeavyCorpus_FillsPageFromBeyondTheFirstWindow()
    {
        // 520 test rows rank ahead of 80 production rows (identical scores ⇒ DocId order), so every pre-fix
        // window (limit*4+10, then the 500 cap) was ALL tests and the tool rendered "No results.".
        var symbols = new IndexedSymbol[600];
        for (int i = 0; i < symbols.Length; i++)
            symbols[i] = Symbol(i, "AlphaBeta", $"src/F{i:d4}.cs", isTest: i < 520);
        var index = SymbolSearchProjection.Build(symbols);

        string output = SearchTool.Run(
            index, "alpha", SearchToolMode.Symbol, limit: 6, excludeTests: true, json: false, out int rendered);

        Assert.Equal(6, rendered);
        Assert.DoesNotContain("No results", output, StringComparison.Ordinal);
        Assert.Contains("src/F0520.cs", output);
        Assert.Contains("… 74 more", output);
    }

    [Fact]
    public void Run_LanguageFilter_FillsPageAndKeepsOverflowNoteAccurate()
    {
        var symbols = new IndexedSymbol[600];
        for (int i = 0; i < symbols.Length; i++)
            symbols[i] = Symbol(i, "AlphaBeta", $"src/F{i:d4}.cs", language: i < 550 ? "csharp" : "typescript");
        var index = SymbolSearchProjection.Build(symbols);

        string output = SearchTool.Run(
            index, "alpha", SearchToolMode.Symbol, limit: 6, excludeTests: false, json: false, out int rendered,
            language: "typescript");

        Assert.Equal(6, rendered);
        Assert.Contains("src/F0550.cs", output);
        Assert.Contains("… 44 more", output);
    }

    [Fact]
    public void RunContent_FilePatternFilter_EscalatesPastTheFirstWindow()
    {
        var hits = new List<ContentSearchHit>();
        for (int i = 0; i < 550; i++)
            hits.Add(new ContentSearchHit($"docs/a/x{i:d4}.md", Score: 1.0, Line: 1, Snippet: "needle", "markdown", 64));
        for (int i = 0; i < 50; i++)
            hits.Add(new ContentSearchHit($"docs/b/y{i:d4}.md", Score: 1.0, Line: 1, Snippet: "needle", "markdown", 64));
        var index = new WindowedContentIndex(hits);

        string output = SearchTool.RunContent(
            index, "needle", limit: 6, json: false, out int rendered, filePattern: "docs/b/**");

        Assert.Equal(6, rendered);
        Assert.Contains("docs/b/y0000.md", output);
        Assert.Contains("… 44 more", output);
    }

    [Fact]
    public void RunContentCorpus_FilePatternFilter_EscalatesPastTheFirstWindow()
    {
        var hits = new List<TextContentSearchHit>();
        for (int i = 0; i < 550; i++)
            hits.Add(TextHit($"docs/a/x{i:d4}.md", TextContentKind.WorkspaceDocs));
        for (int i = 0; i < 50; i++)
            hits.Add(TextHit($"docs/b/y{i:d4}.md", TextContentKind.WorkspaceDocs));
        var index = new WindowedTextContentIndex(hits);

        string output = SearchTool.RunContentCorpus(
            index, "needle", limit: 6, json: false, out int rendered, filePattern: "docs/b/**");

        Assert.Equal(6, rendered);
        Assert.Contains("docs/b/y0000.md", output);
        Assert.Contains("… 44 more", output);
    }

    [Fact]
    public void RunTextContent_KindsOverload_EscalatesPastTheFirstWindow()
    {
        var index = new WindowedTextContentIndex(SourceSplitCorpus());

        string output = SearchTool.RunTextContent(
            index, "needle", new[] { TextContentKind.WorkspaceSource }, limit: 6, excludeTests: false,
            json: false, out int rendered, out _, filePattern: "src/b/**");

        Assert.Equal(6, rendered);
        Assert.Contains("src/b/y0000.cs", output);
        Assert.Contains("… 44 more", output);
    }

    [Fact]
    public void RunTextContent_SingleKindOverload_EscalatesPastTheFirstWindow()
    {
        var index = new WindowedTextContentIndex(SourceSplitCorpus());

        string output = SearchTool.RunTextContent(
            index, "needle", TextContentKind.WorkspaceSource, limit: 6, excludeTests: false,
            json: false, out int rendered, out long _, filePattern: "src/b/**");

        Assert.Equal(6, rendered);
        Assert.Contains("src/b/y0000.cs", output);
        Assert.Contains("… 44 more", output);
    }

    [Fact]
    public void RunRegions_FilePatternFilter_EscalatesPastTheFirstWindow()
    {
        var hits = new List<RegionSearchHit>();
        for (int i = 0; i < 550; i++)
            hits.Add(RegionHit($"src/a/x{i:d4}.cs", $"region-a-{i:d4}"));
        for (int i = 0; i < 50; i++)
            hits.Add(RegionHit($"src/b/y{i:d4}.cs", $"region-b-{i:d4}"));
        var index = new WindowedRegionIndex(hits);

        string output = SearchTool.RunRegions(
            index, "needle", new HashSet<string>(StringComparer.Ordinal) { "comment" }, limit: 6,
            excludeTests: false, json: false, out int rendered, filePattern: "src/b/**");

        Assert.Equal(6, rendered);
        Assert.Contains("src/b/y0000.cs", output);
        Assert.Contains("… 44 more", output);
    }

    [Fact]
    public void Run_NonFullWindowWithNothingKept_StillReportsNoResults()
    {
        // The escalation trigger requires a FULL window: when the index already returned everything it has and
        // the filters dropped it all, there is nothing more to fetch — render the filtered miss, no retry loop.
        var symbols = new IndexedSymbol[20];
        for (int i = 0; i < symbols.Length; i++)
            symbols[i] = Symbol(i, "AlphaBeta", $"src/F{i:d4}.cs");
        var index = SymbolSearchProjection.Build(symbols);

        string output = SearchTool.Run(
            index, "alpha", SearchToolMode.Symbol, limit: 6, excludeTests: false, json: false, out int rendered,
            language: "typescript");

        Assert.Equal(0, rendered);
        Assert.Contains("No results within language=typescript", output);
    }

    private static List<TextContentSearchHit> SourceSplitCorpus()
    {
        var hits = new List<TextContentSearchHit>();
        for (int i = 0; i < 550; i++)
            hits.Add(TextHit($"src/a/x{i:d4}.cs", TextContentKind.WorkspaceSource, language: "csharp"));
        for (int i = 0; i < 50; i++)
            hits.Add(TextHit($"src/b/y{i:d4}.cs", TextContentKind.WorkspaceSource, language: "csharp"));
        return hits;
    }

    private static TextContentSearchHit TextHit(string path, string kind, string language = "markdown") =>
        new(
            SourceId: kind + ":" + path,
            ChunkId: kind + ":" + path + ":1",
            ContentKind: kind,
            Path: path,
            Url: null,
            DisplayPath: path,
            Language: language,
            Score: 1.0,
            Line: 1,
            LineStart: 1,
            LineEnd: 2,
            ByteStart: 0,
            ByteEnd: 16,
            Snippet: "needle",
            SourceBytes: 64,
            ContainingSymbolId: null,
            ContainingSymbolName: null);

    private static RegionSearchHit RegionHit(string path, string regionId) =>
        new(path, Score: 1.0, Line: 1, Kind: "comment", Snippet: "needle", RawText: "needle",
            RegionId: regionId, ContainingSymbolId: null, ContainingSymbolName: null, Language: "csharp");

    // Window-honoring stubs: returning exactly min(window, corpus) hits is what drives the "full window ⇒
    // maybe more" escalation trigger, so Take(limit) here is load-bearing for these tests.
    private sealed class WindowedContentIndex : IContentSearchIndex
    {
        private readonly IReadOnlyList<ContentSearchHit> _hits;

        public WindowedContentIndex(IReadOnlyList<ContentSearchHit> hits) => _hits = hits;

        public int DocumentCount => _hits.Count;

        public IReadOnlyList<ContentSearchHit> Search(string query, int limit = 10) =>
            _hits.Take(limit).ToArray();
    }

    private sealed class WindowedTextContentIndex : ITextContentSearchIndex
    {
        private readonly IReadOnlyList<TextContentSearchHit> _hits;

        public WindowedTextContentIndex(IReadOnlyList<TextContentSearchHit> hits) => _hits = hits;

        public int DocumentCount => _hits.Count;

        public IReadOnlyList<TextContentSearchHit> Search(
            string query, string contentKind, int limit = 10, bool excludeTests = false) =>
            Search(query, new[] { contentKind }, limit, excludeTests);

        public IReadOnlyList<TextContentSearchHit> Search(
            string query, IReadOnlyCollection<string> contentKinds, int limit = 10, bool excludeTests = false) =>
            _hits.Where(hit => contentKinds.Contains(hit.ContentKind)).Take(limit).ToArray();
    }

    private sealed class WindowedRegionIndex : IRegionSearchIndex
    {
        private readonly IReadOnlyList<RegionSearchHit> _hits;

        public WindowedRegionIndex(IReadOnlyList<RegionSearchHit> hits) => _hits = hits;

        public int DocumentCount => _hits.Count;

        public long Revision { get; } = 1;

        public IReadOnlyList<RegionSearchHit> Search(
            string query, IReadOnlySet<string> kinds, int limit = 10, bool excludeTests = false) =>
            _hits.Where(hit => kinds.Contains(hit.Kind)).Take(limit).ToArray();
    }
}
