using System.Text.Json;
using Miller.Core.Search;
using Miller.Indexing;
using Miller.Server.Tools;
using Xunit;

namespace Miller.Tests.Search;

public sealed class RetrievalEvidenceOrderingTests
{
    private static ContentDocument Doc(int id, string path, string text) => new(id, path, text);

    private static SymbolCandidate Candidate(
        int docId,
        string name,
        string kind,
        string path,
        double score,
        string? signature = null,
        string? language = null,
        bool relaxed = false) =>
        new(
            docId,
            docId.ToString("x32"),
            name,
            signature,
            kind,
            path,
            1,
            score,
            Language: language,
            Relaxed: relaxed);

    // 1. Literal-First Tier in text retrieval (ContentSearchIndex)
    [Fact]
    public void LiteralFirstTier_LiteralMatchOutranksLooseRepeatedTokens()
    {
        // Space-separated query "search query engine":
        // doc0 has the exact literal substring "search query engine"
        // doc1 has individual tokens repeated many times on one line (higher BM25 term frequency), but not the literal substring
        // doc2 has tokens in reversed order on one line
        var index = ContentSearchIndex.Build([
            Doc(0, "/exact.md", "Here is the exact search query engine component."),
            Doc(1, "/loose.md", "search search search search query query query query engine engine engine engine loose repeated terms."),
            Doc(2, "/reversed.md", "engine query search reversed terms on one line.")
        ]);

        var hits = index.Search("search query engine", limit: 10);

        Assert.True(hits.Count >= 2);
        Assert.Equal("/exact.md", hits[0].Path);
        Assert.Equal("/loose.md", hits[1].Path);
    }

    [Fact]
    public void LiteralFirstTier_HyphenatedLiteral_OutranksTokenPhraseMatchWithHigherBm25()
    {
        // Hyphenated query "retry-policy-timeout":
        // doc0 has the exact literal hyphenated match "retry-policy-timeout"
        // doc1 has the token phrase "retry policy timeout" repeated multiple times (higher BM25 tf, matches token phrase), but lacks the literal hyphenated string
        var index = ContentSearchIndex.Build([
            Doc(0, "/literal.md", "This document covers retry-policy-timeout in production."),
            Doc(1, "/phrase.md", "retry policy timeout retry policy timeout retry policy timeout phrase match.")
        ]);

        var hits = index.Search("retry-policy-timeout", limit: 10);

        Assert.True(hits.Count >= 2);
        Assert.Equal("/literal.md", hits[0].Path);
        Assert.Equal("/phrase.md", hits[1].Path);
    }

    // 2. Symbol OR fallback: Distinct meaningful query-term coverage outranks BM25 score
    [Fact]
    public void SymbolOrFallback_DistinctTermCoverageOutranksHighBm25SingleTermRepeat()
    {
        // Candidate 1: matches 3 distinct query terms ("search", "workspace", "query") with low BM25 score (2.0)
        // Candidate 2: matches 1 distinct query term ("search") repeated with very high BM25 score (25.0)
        // Candidate 3: matches 2 distinct query terms ("search", "workspace") with medium score (6.0)
        SymbolCandidate[] candidates =
        [
            Candidate(1, "SearchWorkspaceQueryService", "class", "src/SearchWorkspaceQueryService.cs", 2.0),
            Candidate(2, "SearchSearchSearchSearch", "class", "src/Search.cs", 25.0),
            Candidate(3, "SearchWorkspaceCoordinator", "class", "src/SearchWorkspaceCoordinator.cs", 6.0),
        ];

        IReadOnlyList<SymbolRerankResult> ranked =
            SymbolReranker.Rank("search workspace query", candidates);

        // Candidate 1 (3 distinct terms) should rank first, then Candidate 3 (2 terms), then Candidate 2 (1 term)
        Assert.Equal(1, ranked[0].Candidate.DocId);
        Assert.Equal(3, ranked[0].Features.DistinctCoverage);

        Assert.Equal(3, ranked[1].Candidate.DocId);
        Assert.Equal(2, ranked[1].Features.DistinctCoverage);

        Assert.Equal(2, ranked[2].Candidate.DocId);
        Assert.Equal(1, ranked[2].Features.DistinctCoverage);
    }

    [Fact]
    public void SymbolOrFallback_ExactIdentifierBoostPreservedAheadOfCoverage()
    {
        // Exact identifier match has Exactness >= 4.0 and must remain first
        SymbolCandidate[] candidates =
        [
            Candidate(1, "Query", "class", "src/Query.cs", 10.0),
            Candidate(2, "QueryRunnerExecutor", "class", "src/QueryRunnerExecutor.cs", 12.0),
        ];

        IReadOnlyList<SymbolRerankResult> ranked =
            SymbolReranker.Rank("Query", candidates);

        Assert.Equal(1, ranked[0].Candidate.DocId);
        Assert.True(ranked[0].Features.Exactness >= 4.0);
    }

    // 3. Truthful relaxed=or labeling in SearchTool
    [Fact]
    public void TruthfulRelaxedOr_StrictOnlyPage_OmitsNoteAndJsonFlag()
    {
        // Candidate 0 is strict (Relaxed = false), Candidate 1 is relaxed (Relaxed = true)
        SymbolCandidate[] list =
        [
            Candidate(1, "StrictMatch", "class", "src/Strict.cs", 10.0, relaxed: false),
            Candidate(2, "RelaxedMatch", "class", "src/Relaxed.cs", 8.0, relaxed: true),
        ];

        var set = new SymbolCandidateSet(
            list,
            OutsideScope: [],
            EmptySuggestions: [],
            FileMode: false,
            ToolSearchFilters.Parse(null, null),
            Visibility: null,
            Relaxed: true);

        // Limit = 1: only Candidate 0 is served
        string compact = SearchTool.RenderSymbolCandidates(
            set,
            "strict match",
            SearchToolMode.Symbol,
            limit: 1,
            json: false,
            out _);

        Assert.DoesNotContain("note: relaxed=or", compact, StringComparison.Ordinal);

        string json = SearchTool.RenderSymbolCandidates(
            set,
            "strict match",
            SearchToolMode.Symbol,
            limit: 1,
            json: true,
            out _);

        using var doc = JsonDocument.Parse(json);
        JsonElement[] rows = doc.RootElement.EnumerateArray().ToArray();
        Assert.Single(rows);
        Assert.False(rows[0].TryGetProperty("relaxed", out _));
    }

    [Fact]
    public void TruthfulRelaxedOr_PageWithRelaxedCandidate_EmitsNoteAndJsonFlag()
    {
        SymbolCandidate[] list =
        [
            Candidate(1, "StrictMatch", "class", "src/Strict.cs", 10.0, relaxed: false),
            Candidate(2, "RelaxedMatch", "class", "src/Relaxed.cs", 8.0, relaxed: true),
        ];

        var set = new SymbolCandidateSet(
            list,
            OutsideScope: [],
            EmptySuggestions: [],
            FileMode: false,
            ToolSearchFilters.Parse(null, null),
            Visibility: null,
            Relaxed: true);

        // Limit = 2: both strict and relaxed are served
        string compact = SearchTool.RenderSymbolCandidates(
            set,
            "strict match",
            SearchToolMode.Symbol,
            limit: 2,
            json: false,
            out _);

        Assert.Contains("note: relaxed=or", compact, StringComparison.Ordinal);

        string json = SearchTool.RenderSymbolCandidates(
            set,
            "strict match",
            SearchToolMode.Symbol,
            limit: 2,
            json: true,
            out _);

        using var doc = JsonDocument.Parse(json);
        JsonElement[] rows = doc.RootElement.EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);
        Assert.False(rows[0].TryGetProperty("relaxed", out _));
        Assert.True(rows[1].GetProperty("relaxed").GetBoolean());
    }

    [Fact]
    public void TruthfulRelaxedOr_EmptyResults_NeverEmitsRelaxedTrue()
    {
        var set = new SymbolCandidateSet(
            Candidates: [],
            OutsideScope: [],
            EmptySuggestions: [],
            FileMode: false,
            ToolSearchFilters.Parse(null, null),
            Visibility: null,
            Relaxed: true);

        string json = SearchTool.RenderSymbolCandidates(
            set,
            "search workspace",
            SearchToolMode.Symbol,
            limit: 10,
            json: true,
            out _);

        Assert.Equal("[]", json);
        Assert.DoesNotContain("relaxed", json, StringComparison.OrdinalIgnoreCase);
    }

    // 4. Hybrid content JSON contract and protected lexical leaders
    [Fact]
    public void HybridContentJson_EmitsRrfScoreAndRankingMethod()
    {
        var testHit = new TextContentSearchHit(
            SourceId: "src-1",
            ChunkId: "chunk-1",
            ContentKind: "docs",
            Path: "docs/hybrid.md",
            Url: null,
            DisplayPath: "docs/hybrid.md",
            Language: "markdown",
            Score: 4.5,
            Line: 10,
            LineStart: 8,
            LineEnd: 15,
            ByteStart: 100,
            ByteEnd: 200,
            Snippet: "fused hybrid snippet for testing",
            SourceBytes: 500,
            ContainingSymbolId: null,
            ContainingSymbolName: null);

        var testIndex = new FakeTextContentIndex(new TextContentSearchResult([testHit]));

        // Provide a rerank delegate simulating hybrid fusion
        var rerank = new Func<IReadOnlyList<ContentSearchHit>, IReadOnlyList<ContentSearchHit>>(hits =>
            [hits[0] with { RrfScore = 0.042, RankingMethod = "rrf" }]);

        string json = SearchTool.RunContentCorpus(
            testIndex,
            "testing",
            limit: 5,
            json: true,
            out _,
            out _,
            rerank: rerank);

        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        JsonElement row = root[0];

        // Original score preserved
        Assert.Equal(4.5, row.GetProperty("score").GetDouble(), precision: 2);
        // Hybrid metadata present
        Assert.Equal(1, row.GetProperty("final_rank").GetInt32());
        Assert.Equal("rrf", row.GetProperty("ranking_method").GetString());
        Assert.Equal(0.042, row.GetProperty("rrf_score").GetDouble(), precision: 3);
    }

    [Fact]
    public void HybridContentJson_ProtectedLexicalLeader_EmitsProtectedLexicalLeader()
    {
        var testHit = new TextContentSearchHit(
            SourceId: "src-1",
            ChunkId: "chunk-1",
            ContentKind: "docs",
            Path: "docs/leader.md",
            Url: null,
            DisplayPath: "docs/leader.md",
            Language: "markdown",
            Score: 5.0,
            Line: 1,
            LineStart: 1,
            LineEnd: 5,
            ByteStart: 0,
            ByteEnd: 100,
            Snippet: "protected lexical leader snippet",
            SourceBytes: 200,
            ContainingSymbolId: null,
            ContainingSymbolName: null);

        var testIndex = new FakeTextContentIndex(new TextContentSearchResult([testHit]));

        var rerank = new Func<IReadOnlyList<ContentSearchHit>, IReadOnlyList<ContentSearchHit>>(hits =>
            [hits[0] with { RrfScore = 0.038, RankingMethod = "protected_lexical_leader" }]);

        string json = SearchTool.RunContentCorpus(
            testIndex,
            "leader",
            limit: 5,
            json: true,
            out _,
            out _,
            rerank: rerank);

        using var doc = JsonDocument.Parse(json);
        JsonElement row = doc.RootElement[0];
        Assert.Equal("protected_lexical_leader", row.GetProperty("ranking_method").GetString());
    }

    [Fact]
    public void PureLexicalContentJson_HasNoSemanticMetadata()
    {
        var testHit = new TextContentSearchHit(
            SourceId: "src-1",
            ChunkId: "chunk-1",
            ContentKind: "docs",
            Path: "docs/pure.md",
            Url: null,
            DisplayPath: "docs/pure.md",
            Language: "markdown",
            Score: 3.2,
            Line: 5,
            LineStart: 1,
            LineEnd: 10,
            ByteStart: 0,
            ByteEnd: 150,
            Snippet: "pure lexical match with no semantic rerank",
            SourceBytes: 150,
            ContainingSymbolId: null,
            ContainingSymbolName: null);

        var testIndex = new FakeTextContentIndex(new TextContentSearchResult([testHit]));

        // No rerank function provided (pure lexical)
        string json = SearchTool.RunContentCorpus(
            testIndex,
            "pure",
            limit: 5,
            json: true,
            out _,
            out _,
            rerank: null);

        using var doc = JsonDocument.Parse(json);
        JsonElement row = doc.RootElement[0];

        Assert.Equal(3.2, row.GetProperty("score").GetDouble(), precision: 2);
        Assert.False(row.TryGetProperty("final_rank", out _));
        Assert.False(row.TryGetProperty("ranking_method", out _));
        Assert.False(row.TryGetProperty("rrf_score", out _));
    }

    // 5. Bounded hidden-test reporting in SearchTool
    [Fact]
    public void BoundedHiddenTestReporting_UnsaturatedWindow_ReportsExactCount()
    {
        var testHit = new TextContentSearchHit(
            SourceId: "src-1",
            ChunkId: "chunk-1",
            ContentKind: "source",
            Path: "src/Service.cs",
            Url: null,
            DisplayPath: "src/Service.cs",
            Language: "csharp",
            Score: 4.0,
            Line: 10,
            LineStart: 1,
            LineEnd: 20,
            ByteStart: 0,
            ByteEnd: 200,
            Snippet: "public void ProcessOrder() { }",
            SourceBytes: 200,
            ContainingSymbolId: null,
            ContainingSymbolName: null);

        var testIndex = new FakeTextContentIndex(
            new TextContentSearchResult([testHit], ExcludedTestCount: 4, WindowSaturated: false));

        string compact = SearchTool.RunTextContent(
            testIndex,
            "ProcessOrder",
            "source",
            limit: 10,
            excludeTests: true,
            json: false,
            out _);

        Assert.Contains("4 test chunks hidden", compact, StringComparison.Ordinal);

        string json = SearchTool.RunTextContent(
            testIndex,
            "ProcessOrder",
            "source",
            limit: 10,
            excludeTests: true,
            json: true,
            out _);

        using var doc = JsonDocument.Parse(json);
        JsonElement row = doc.RootElement[0];
        Assert.Equal(4, row.GetProperty("tests_hidden").GetInt32());
        Assert.False(row.TryGetProperty("tests_excluded_bounded", out _));
    }

    [Fact]
    public void BoundedHiddenTestReporting_SaturatedWindow_ReportsBoundedNotice()
    {
        var testHit = new TextContentSearchHit(
            SourceId: "src-1",
            ChunkId: "chunk-1",
            ContentKind: "source",
            Path: "src/Service.cs",
            Url: null,
            DisplayPath: "src/Service.cs",
            Language: "csharp",
            Score: 4.0,
            Line: 10,
            LineStart: 1,
            LineEnd: 20,
            ByteStart: 0,
            ByteEnd: 200,
            Snippet: "public void ProcessOrder() { }",
            SourceBytes: 200,
            ContainingSymbolId: null,
            ContainingSymbolName: null);

        var testIndex = new FakeTextContentIndex(
            new TextContentSearchResult([testHit], ExcludedTestCount: 5000, WindowSaturated: true));

        string compact = SearchTool.RunTextContent(
            testIndex,
            "ProcessOrder",
            "source",
            limit: 10,
            excludeTests: true,
            json: false,
            out _);

        Assert.Contains("test results excluded from a bounded candidate window", compact, StringComparison.Ordinal);

        string json = SearchTool.RunTextContent(
            testIndex,
            "ProcessOrder",
            "source",
            limit: 10,
            excludeTests: true,
            json: true,
            out _);

        using var doc = JsonDocument.Parse(json);
        JsonElement row = doc.RootElement[0];
        Assert.True(row.GetProperty("tests_excluded_bounded").GetBoolean());
        Assert.False(row.TryGetProperty("tests_hidden", out _));
    }

    [Fact]
    public void BoundedHiddenTestReporting_ZeroHiddenTests_OmitsNotice()
    {
        var testHit = new TextContentSearchHit(
            SourceId: "src-1",
            ChunkId: "chunk-1",
            ContentKind: "source",
            Path: "src/Service.cs",
            Url: null,
            DisplayPath: "src/Service.cs",
            Language: "csharp",
            Score: 4.0,
            Line: 10,
            LineStart: 1,
            LineEnd: 20,
            ByteStart: 0,
            ByteEnd: 200,
            Snippet: "public void ProcessOrder() { }",
            SourceBytes: 200,
            ContainingSymbolId: null,
            ContainingSymbolName: null);

        var testIndex = new FakeTextContentIndex(
            new TextContentSearchResult([testHit], ExcludedTestCount: 0, WindowSaturated: false));

        string compact = SearchTool.RunTextContent(
            testIndex,
            "ProcessOrder",
            "source",
            limit: 10,
            excludeTests: true,
            json: false,
            out _);

        Assert.DoesNotContain("hidden", compact, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bounded", compact, StringComparison.OrdinalIgnoreCase);

        string json = SearchTool.RunTextContent(
            testIndex,
            "ProcessOrder",
            "source",
            limit: 10,
            excludeTests: true,
            json: true,
            out _);

        using var doc = JsonDocument.Parse(json);
        JsonElement row = doc.RootElement[0];
        Assert.False(row.TryGetProperty("tests_hidden", out _));
        Assert.False(row.TryGetProperty("tests_excluded_bounded", out _));
    }

    // 6. Diagnostics & Query Plan
    [Fact]
    public void TextSearchQueryPlan_HyphenRequiresTokenPhrase()
    {
        var plan = TextSearchQueryPlan.Create("foo-bar");
        Assert.NotNull(plan);
        Assert.True(plan.RequiresTokenPhrase);
        Assert.Contains("foo", plan.QueryTokens);
        Assert.Contains("bar", plan.QueryTokens);
    }

    [Fact]
    public void TextSearchQueryPlan_ToString_ReflectsStrictAndAndRequiredCoverage()
    {
        var plan = TextSearchQueryPlan.Create("alpha beta");
        Assert.NotNull(plan);
        Assert.Contains("StrictAND", plan.ToString(), StringComparison.Ordinal);
        Assert.Contains("alpha AND beta", plan.ToString(), StringComparison.Ordinal);
    }

    private sealed class FakeTextContentIndex : ITextContentSearchIndex
    {
        private readonly TextContentSearchResult _result;

        public FakeTextContentIndex(TextContentSearchResult result)
        {
            _result = result;
        }

        public int DocumentCount => _result.Hits.Count;

        public IReadOnlyList<TextContentSearchHit> Search(
            string query,
            string contentKind,
            int limit = 10,
            bool excludeTests = false,
            string? sourceId = null) => _result.Hits;

        public IReadOnlyList<TextContentSearchHit> Search(
            string query,
            IReadOnlyCollection<string> contentKinds,
            int limit = 10,
            bool excludeTests = false,
            string? sourceId = null) => _result.Hits;

        public TextContentSearchResult SearchExtended(
            string query,
            IReadOnlyCollection<string> contentKinds,
            int limit = 10,
            bool excludeTests = false,
            string? sourceId = null) => _result;

        public TextContentSearchResult SearchExtended(
            string query,
            string contentKind,
            int limit = 10,
            bool excludeTests = false,
            string? sourceId = null) => _result;
    }
}
