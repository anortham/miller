using Microsoft.Data.Sqlite;
using Miller.Core.Search;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Hardening pins for the on-disk <see cref="FtsSymbolSearchIndex"/> (real <c>search.db</c> via
/// <see cref="SearchIndexWriter.Write"/>, no julie subprocess — fast suite):
/// (1) the collapsed-trigram candidate window must be selected by RELEVANCE (FTS5 bm25 rank, shortest-name
/// tie-break), not by <c>doc_id</c> — on a corpus with more trigram matches than the window, a doc_id-ordered
/// window arbitrarily cut the best interior-substring match;
/// (2) query terms containing FTS5 syntax (<c>AND</c>/<c>OR</c>/<c>NOT</c>/<c>NEAR</c>, parens, quotes,
/// <c>*</c>, <c>^</c>, <c>-</c>) must never throw and must be treated as literal text by both MATCH arms.
/// </summary>
public sealed class FtsSymbolSearchIndexHardeningTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public FtsSymbolSearchIndexHardeningTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-ftshard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "search.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private FtsSymbolSearchIndex OpenWith(IReadOnlyList<IndexedSymbol> symbols)
    {
        SearchIndexWriter.Write(_dbPath, symbols, revision: 1);
        return FtsSymbolSearchIndex.Open(_dbPath);
    }

    private static IndexedSymbol Sym(int docId, string name, string path) =>
        new(docId, $"sym-{docId:d4}", name, Signature: null, "function", "csharp", path,
            StartLine: 1, EndLine: 2, ParentId: null, IsTest: false);

    [Fact]
    public void Search_TrigramWindowOverflow_BestInteriorMatchSurvivesTheWindow()
    {
        // 230 long all-lowercase names contain "widget" only as an interior substring (single token each, so
        // the word arm never matches and only the trigram arm can recall them). The BEST match — the shortest
        // name with the same interior substring — is given the LAST doc_id, so a doc_id-ordered 200-row window
        // cut it arbitrarily. The window must instead be relevance-ordered (FTS5 rank: a shorter collapsed
        // name has higher trigram density, hence a better bm25 for the same matched phrase).
        var rows = new List<IndexedSymbol>(231);
        for (int i = 0; i < 230; i++)
            rows.Add(Sym(i, $"verylongprefix{i:d3}widgetverylongsuffix", $"src/a{i:d3}.cs"));
        rows.Add(Sym(230, "awidgetb", "src/zzz.cs"));
        FtsSymbolSearchIndex index = OpenWith(rows);

        IReadOnlyList<SearchHit> hits = index.Search("widget", limit: 250);

        Assert.Contains(hits, hit => hit.Document.Name == "awidgetb");
    }

    [Theory]
    [InlineData("(foo)")]
    [InlineData("AND")]
    [InlineData("OR")]
    [InlineData("NOT")]
    [InlineData("NEAR")]
    [InlineData("NOT (alpha OR beta)")]
    [InlineData("alpha-beta")]
    [InlineData("say \"hello\" twice")]
    [InlineData("wild*")]
    [InlineData("^caret")]
    [InlineData("()")]
    [InlineData("\"")]
    [InlineData("a AND b OR c NOT d NEAR e")]
    public void Search_QueryWithFts5Syntax_DoesNotThrow(string query)
    {
        FtsSymbolSearchIndex index = OpenWith(NastyCorpus());

        Exception? ex = Record.Exception(() => index.Search(query, limit: 10));

        Assert.Null(ex);
    }

    [Theory]
    [InlineData("AND", "AndOrNot")]    // reserved words are literal terms, not boolean operators
    [InlineData("OR", "AndOrNot")]
    [InlineData("NOT", "AndOrNot")]
    [InlineData("NEAR", "NearMiss")]
    [InlineData("(foo)", "FooBar")]    // parens stripped by tokenization, term quoted for FTS
    [InlineData("alpha-beta", "AlphaBeta")]
    [InlineData("wild*", "WildPower")] // '*' must not act as an FTS prefix operator
    [InlineData("^caret", "CaretHat")] // '^' must not act as a first-token anchor
    [InlineData("\"quoted\"", "QuotedValue")]
    public void Search_QueryWithFts5Syntax_MatchesTermsLiterally(string query, string expectedName)
    {
        FtsSymbolSearchIndex index = OpenWith(NastyCorpus());

        IReadOnlyList<SearchHit> hits = index.Search(query, limit: 10);

        Assert.Contains(hits, hit => hit.Document.Name == expectedName);
    }

    private static IReadOnlyList<IndexedSymbol> NastyCorpus() =>
    [
        Sym(0, "AndOrNot", "src/a.cs"),
        Sym(1, "NearMiss", "src/b.cs"),
        Sym(2, "FooBar", "src/c.cs"),
        Sym(3, "AlphaBeta", "src/d.cs"),
        Sym(4, "WildPower", "src/e.cs"),
        Sym(5, "CaretHat", "src/f.cs"),
        Sym(6, "QuotedValue", "src/g.cs"),
    ];
}
