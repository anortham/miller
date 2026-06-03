using Miller.Core.Search;
using Xunit;

namespace Miller.Tests.Search;

/// <summary>
/// Behavioral pins for the in-memory content BM25 index (phase 3). Each test builds a small,
/// hand-reasoned multi-line corpus and asserts ranking ORDER plus the best-line/snippet-window
/// contract. No smoke-only tests: every test asserts a concrete relationship between results.
/// </summary>
public sealed class ContentSearchIndexTests
{
    private static ContentDocument Doc(int id, string path, string text) => new(id, path, text);

    private static ContentSearchIndex Build(params ContentDocument[] docs) =>
        ContentSearchIndex.Build(docs);

    [Fact]
    public void Build_ReportsDocumentCount()
    {
        var index = Build(
            Doc(0, "/a.md", "alpha"),
            Doc(1, "/b.md", "beta"));

        Assert.Equal(2, index.DocumentCount);
    }

    [Fact]
    public void Search_HigherTermFrequencyRanksFirst_ForEqualLength()
    {
        // doc0: tf(alpha)=2, docLen=3 ; doc1: tf(alpha)=1, docLen=3 (equal length, higher tf wins)
        var index = Build(
            Doc(0, "/high.md", "alpha alpha beta"),
            Doc(1, "/low.md", "alpha gamma delta"));

        var hits = index.Search("alpha", limit: 10);

        Assert.Equal(2, hits.Count);
        Assert.Equal("/high.md", hits[0].Path);
        Assert.True(hits[0].Score > hits[1].Score,
            $"higher-tf doc score {hits[0].Score} should exceed {hits[1].Score}");
    }

    [Fact]
    public void Search_RarerTermOutranksCommonTerm_Idf()
    {
        var docs = new List<ContentDocument> { Doc(0, "/rare.md", "freshness") };
        for (int i = 1; i <= 8; i++) docs.Add(Doc(i, $"/c{i}.md", "common"));

        var rareHit = ContentSearchIndex.Build(docs).Search("freshness", limit: 1)[0];
        var commonHit = ContentSearchIndex.Build(docs).Search("common", limit: 1)[0];

        Assert.True(rareHit.Score > commonHit.Score,
            $"rare idf score {rareHit.Score} should exceed common idf score {commonHit.Score}");
    }

    [Fact]
    public void Search_MultiTermQueryRanksDocumentMatchingBothTermsFirst()
    {
        var index = Build(
            Doc(0, "/both.md", "freshness gate"),
            Doc(1, "/one.md", "freshness freshness unrelated"));

        var hits = index.Search("freshness gate", limit: 10);

        Assert.Equal(2, hits.Count);
        Assert.Equal("/both.md", hits[0].Path);
        Assert.True(hits[0].Score > hits[1].Score,
            $"two-term score {hits[0].Score} should exceed one-term score {hits[1].Score}");
    }

    [Fact]
    public void Search_ReturnsBestMatchingLine_OneBased()
    {
        var index = Build(Doc(0, "/doc.md",
            "intro line\n" +                 // line 1
            "mentions freshness once\n" +    // line 2: 1 hit
            "no match here\n" +              // line 3
            "freshness freshness twice"));   // line 4: 2 hits -> best

        var hit = index.Search("freshness", limit: 10)[0];

        Assert.Equal(4, hit.Line);
    }

    [Fact]
    public void Search_SnippetIncludesSurroundingWindow_ExcludesOutsideLines()
    {
        var index = Build(Doc(0, "/doc.md",
            "intro line\n" +                 // line 1 (outside the ±2 window of line 4)
            "mentions freshness once\n" +    // line 2
            "no match here\n" +              // line 3
            "freshness freshness twice"));   // line 4 (best)

        var snippet = index.Search("freshness", limit: 10)[0].Snippet;

        Assert.Contains("freshness freshness twice", snippet);
        Assert.Contains("no match here", snippet);
        Assert.Contains("mentions freshness once", snippet);
        Assert.DoesNotContain("intro line", snippet);
        Assert.Contains("\n", snippet); // newline-joined window
    }

    [Fact]
    public void Search_WindowClampsAtFileStart_NoLeadingBlank()
    {
        var index = Build(Doc(0, "/top.md",
            "freshness at top\n" +   // line 1 (best)
            "second line\n" +        // line 2
            "third line"));          // line 3

        var hit = index.Search("freshness", limit: 10)[0];

        Assert.Equal(1, hit.Line);
        Assert.StartsWith("freshness at top", hit.Snippet);
        Assert.Contains("third line", hit.Snippet);
    }

    [Fact]
    public void Search_WindowClampsAtFileEnd()
    {
        var index = Build(Doc(0, "/bottom.md",
            "alpha\n" +          // line 1
            "bravo\n" +          // line 2 (outside window of line 5)
            "charlie\n" +        // line 3
            "delta\n" +          // line 4
            "freshness end"));   // line 5 (best)

        var hit = index.Search("freshness", limit: 10)[0];

        Assert.Equal(5, hit.Line);
        Assert.Contains("charlie", hit.Snippet);
        Assert.EndsWith("freshness end", hit.Snippet);
        Assert.DoesNotContain("bravo", hit.Snippet);
    }

    [Fact]
    public void Search_EmptyOrWhitespaceQuery_ReturnsEmpty()
    {
        var index = Build(Doc(0, "/a.md", "alpha"));

        Assert.Empty(index.Search("", limit: 10));
        Assert.Empty(index.Search("   ", limit: 10));
    }

    [Fact]
    public void Search_NoMatch_ReturnsEmpty()
    {
        var index = Build(Doc(0, "/a.md", "alpha beta gamma"));

        Assert.Empty(index.Search("zzznomatch", limit: 10));
    }

    [Fact]
    public void Search_TieBreaksByDocIdAscending()
    {
        // Identical text, non-contiguous out-of-order DocIds: equal score => ascending DocId.
        var index = Build(
            Doc(100, "/p100.md", "alpha"),
            Doc(7, "/p7.md", "alpha"),
            Doc(42, "/p42.md", "alpha"));

        var paths = index.Search("alpha", limit: 10).Select(h => h.Path).ToList();

        Assert.Equal(new[] { "/p7.md", "/p42.md", "/p100.md" }, paths);
    }

    [Fact]
    public void Search_LimitTruncates()
    {
        var docs = new List<ContentDocument>();
        for (int i = 0; i < 5; i++) docs.Add(Doc(i, $"/d{i}.md", "alpha"));

        var hits = ContentSearchIndex.Build(docs).Search("alpha", limit: 2);

        Assert.Equal(2, hits.Count);
    }
}
