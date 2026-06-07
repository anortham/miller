using Miller.Core.Search;
using Xunit;

namespace Miller.Tests.Search;

/// <summary>
/// Behavioral pins for the in-memory BM25 index. Each test builds a small, hand-reasoned corpus
/// and asserts the ranking ORDER (and where it matters, the doc identities) that the BM25 math
/// must produce. No smoke-only tests: every test asserts a relationship between concrete results.
/// </summary>
public sealed class MillerSearchIndexTests
{
    private static SearchableDocument Doc(int id, string name, string? signature = null) =>
        new(id, name, signature, Kind: "method", Language: "csharp", FilePath: $"/f/{id}.cs", StartLine: 1);

    private static IReadOnlyList<int> Ids(IReadOnlyList<SearchHit> hits)
    {
        var ids = new List<int>(hits.Count);
        foreach (var h in hits) ids.Add(h.Document.DocId);
        return ids;
    }

    [Fact]
    public void Build_ReportsDocumentAndTermCounts()
    {
        var index = MillerSearchIndex.Build(new[]
        {
            Doc(0, "getUser"),     // tokens: getuser, get, user
            Doc(1, "getUser"),     // same terms
            Doc(2, "parseToken"),  // parsetoken, parse, token
        });

        Assert.Equal(3, index.DocumentCount);
        // distinct terms: getuser, get, user, parsetoken, parse, token = 6
        Assert.Equal(6, index.TermCount);
    }

    [Fact]
    public void Build_DoesNotAssumeDocIdEqualsIndex()
    {
        // Caller-assigned, non-contiguous, out-of-order DocIds must be honored end to end.
        var index = MillerSearchIndex.Build(new[]
        {
            Doc(100, "alpha"),
            Doc(7, "alpha"),
            Doc(42, "alpha"),
        });

        var hits = index.Search("alpha", limit: 10);
        Assert.Equal(3, hits.Count);
        // equal score (identical docs) => tie-break ascending DocId
        Assert.Equal(new[] { 7, 42, 100 }, Ids(hits));
    }

    [Fact]
    public void Search_RarerTermRanksItsDocsAboveCommonTerm_Idf()
    {
        // "rare" appears in 1 doc; "common" appears in many. A doc matched by the rare query
        // term should outscore docs matched only by the common term, for equal length/tf.
        var docs = new List<SearchableDocument> { Doc(0, "rare") };
        for (int i = 1; i <= 8; i++) docs.Add(Doc(i, "common"));

        var rareHit = MillerSearchIndex.Build(docs).Search("rare", limit: 1)[0];
        var commonHit = MillerSearchIndex.Build(docs).Search("common", limit: 1)[0];

        Assert.True(rareHit.Score > commonHit.Score,
            $"rare idf score {rareHit.Score} should exceed common idf score {commonHit.Score}");
    }

    [Fact]
    public void Search_HigherTfRanksAboveLowerTf_ForEqualLength()
    {
        // Equal docLen, differing tf for the query term. Padding terms keep lengths equal.
        // doc 0: "token token"          -> tf(token)=2, docLen=2
        // doc 1: "token alpha"          -> tf(token)=1, docLen=2
        var index = MillerSearchIndex.Build(new[]
        {
            Doc(0, "token token"),
            Doc(1, "token alpha"),
        });

        var hits = index.Search("token", limit: 10);
        Assert.Equal(new[] { 0, 1 }, Ids(hits));
        Assert.True(hits[0].Score > hits[1].Score, "higher tf must score higher");
    }

    [Fact]
    public void Search_ShorterDocRanksAboveLongerDoc_ForEqualTf_LengthNorm()
    {
        // Both docs contain "token" exactly once (tf=1) but doc 1 is longer (more tokens) => penalized.
        var index = MillerSearchIndex.Build(new[]
        {
            Doc(0, "token"),                       // docLen 1
            Doc(1, "token alpha beta gamma delta"),// docLen 5
        });

        var hits = index.Search("token", limit: 10);
        Assert.Equal(new[] { 0, 1 }, Ids(hits));
        Assert.True(hits[0].Score > hits[1].Score, "shorter doc must score higher for equal tf");
    }

    [Fact]
    public void Search_Or_DocMatchingMoreQueryTermsOutranksFewer()
    {
        // Query "user token". doc 0 matches both; doc 1 matches only "user".
        // Score accumulation across terms => doc 0 wins.
        var index = MillerSearchIndex.Build(new[]
        {
            Doc(0, "user token"),
            Doc(1, "user alpha"),
        });

        var hits = index.Search("user token", limit: 10, mode: SearchMode.Or);
        Assert.Equal(new[] { 0, 1 }, Ids(hits));
        Assert.True(hits[0].Score > hits[1].Score, "doc matching 2 query terms must outrank doc matching 1");
    }

    [Fact]
    public void Search_Or_ReturnsDocsMatchingAnyTerm()
    {
        var index = MillerSearchIndex.Build(new[]
        {
            Doc(0, "user"),
            Doc(1, "token"),
            Doc(2, "unrelated"),
        });

        var hits = index.Search("user token", limit: 10, mode: SearchMode.Or);
        Assert.Equal(new[] { 0, 1 }, Ids(hits).OrderBy(x => x));
        Assert.DoesNotContain(2, Ids(hits));
    }

    [Fact]
    public void Search_And_OnlyReturnsDocsMatchingAllDistinctQueryTerms()
    {
        var index = MillerSearchIndex.Build(new[]
        {
            Doc(0, "user token"),  // matches both
            Doc(1, "user alpha"),  // matches only user
            Doc(2, "token beta"),  // matches only token
        });

        var hits = index.Search("user token", limit: 10, mode: SearchMode.And);
        Assert.Equal(new[] { 0 }, Ids(hits));
    }

    [Fact]
    public void Search_And_EmptyWhenNoDocMatchesAllTerms()
    {
        var index = MillerSearchIndex.Build(new[]
        {
            Doc(0, "user alpha"),
            Doc(1, "token beta"),
        });

        Assert.Empty(index.Search("user token", limit: 10, mode: SearchMode.And));
    }

    [Fact]
    public void Search_ExactNameMatch_BoostsDocToFirst()
    {
        // Both docs match the query term "parse" with tf=1 and the SAME docLen (2 tokens each), so
        // their raw BM25 scores are identical. Absent the boost the tie-break (DocId ASC) ranks doc 0
        // first. Only doc 1's Name equals the query exactly, so the 1.5x boost applies to doc 1 alone
        // and must flip it ahead of doc 0 despite doc 1's higher DocId.
        var index = MillerSearchIndex.Build(new[]
        {
            Doc(0, "parse other"),          // text "parse other"  => [parse, other], Name != "parse"
            Doc(1, "parse", "extra"),       // text "parse extra"  => [parse, extra], Name == "parse"
        });

        var hits = index.Search("parse", limit: 10);
        Assert.Equal(2, hits.Count);
        Assert.Equal(1, hits[0].Document.DocId);
        // The boost is real and isolated: equal raw scores => leader gets the exact-name boost and the
        // concrete-definition boost, while the non-exact runner-up gets neither.
        Assert.Equal(
            hits[1].Score * Bm25.ExactNameBoost * Bm25.ExactNameDefinitionKindBoost,
            hits[0].Score,
            precision: 10);
    }

    [Fact]
    public void Search_ExactNameBoost_IsCaseAndWhitespaceInsensitive()
    {
        var index = MillerSearchIndex.Build(new[]
        {
            Doc(0, "Handle", "extra padding tokens here"), // longer; exact-name on trimmed/lowered query
            Doc(1, "handle other"),                        // shorter; not an exact name match
        });

        var hits = index.Search("  HANDLE  ", limit: 10);
        Assert.Equal(0, hits[0].Document.DocId);
    }

    [Fact]
    public void Search_ExactNameDefinitionOutranksImportRows()
    {
        var index = MillerSearchIndex.Build(new[]
        {
            new SearchableDocument(
                1,
                "WorkspacePool",
                "use super::workspace_pool::WorkspacePool;",
                "import",
                "rust",
                "src/daemon/app.rs",
                32),
            new SearchableDocument(
                2,
                "WorkspacePool",
                "pub struct WorkspacePool",
                "struct",
                "rust",
                "src/daemon/workspace_pool.rs",
                59),
        });

        var hits = index.Search("WorkspacePool", limit: 10);

        Assert.Equal(2, hits.Count);
        Assert.Equal("struct", hits[0].Document.Kind);
        Assert.Equal("src/daemon/workspace_pool.rs", hits[0].Document.FilePath);
    }

    [Fact]
    public void Search_ExactNameConcreteDefinitionOutranksManifestProperty()
    {
        var index = MillerSearchIndex.Build(new[]
        {
            new SearchableDocument(
                1,
                "flask",
                "flask = \"flask.cli:main\"",
                "property",
                "toml",
                "pyproject.toml",
                83),
            new SearchableDocument(
                2,
                "Flask",
                "class Flask extends App",
                "class",
                "python",
                "src/flask/app.py",
                109),
        });

        var hits = index.Search("Flask", limit: 10);

        Assert.Equal(2, hits.Count);
        Assert.Equal("class", hits[0].Document.Kind);
        Assert.Equal("src/flask/app.py", hits[0].Document.FilePath);
    }

    [Fact]
    public void Search_TieBreak_EqualScoreOrdersByDocIdAscending()
    {
        var index = MillerSearchIndex.Build(new[]
        {
            Doc(5, "alpha"),
            Doc(2, "alpha"),
            Doc(9, "alpha"),
        });

        var hits = index.Search("alpha", limit: 10);
        Assert.Equal(new[] { 2, 5, 9 }, Ids(hits));
    }

    [Fact]
    public void Search_IsDeterministicAcrossRepeatedCalls()
    {
        var docs = new[]
        {
            Doc(3, "user service"),
            Doc(1, "user token"),
            Doc(2, "user handler"),
            Doc(4, "user service handler"),
        };
        var index = MillerSearchIndex.Build(docs);

        var first = Ids(index.Search("user service", limit: 10));
        for (int rep = 0; rep < 5; rep++)
            Assert.Equal(first, Ids(index.Search("user service", limit: 10)));
    }

    [Fact]
    public void Search_RespectsLimit()
    {
        var docs = new List<SearchableDocument>();
        for (int i = 0; i < 20; i++) docs.Add(Doc(i, "alpha"));
        var index = MillerSearchIndex.Build(docs);

        Assert.Equal(5, index.Search("alpha", limit: 5).Count);
    }

    [Fact]
    public void Search_LimitLargerThanMatches_ReturnsAllMatches()
    {
        var index = MillerSearchIndex.Build(new[] { Doc(0, "alpha"), Doc(1, "alpha") });
        Assert.Equal(2, index.Search("alpha", limit: 100).Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("___")]
    public void Search_EmptyOrTokenlessQuery_ReturnsEmpty(string query)
    {
        var index = MillerSearchIndex.Build(new[] { Doc(0, "alpha") });
        Assert.Empty(index.Search(query, limit: 10));
    }

    [Fact]
    public void Search_NoMatch_ReturnsEmpty()
    {
        var index = MillerSearchIndex.Build(new[] { Doc(0, "alpha") });
        Assert.Empty(index.Search("nonexistent", limit: 10));
    }

    [Fact]
    public void Search_MatchesComponentTokenInsideCamelCaseIdentifier()
    {
        // The whole point of the code tokenizer: "http" should hit getHTTPResponseCode.
        var index = MillerSearchIndex.Build(new[]
        {
            Doc(0, "getHTTPResponseCode"),
            Doc(1, "unrelated"),
        });

        var hits = index.Search("http", limit: 10);
        Assert.Equal(new[] { 0 }, Ids(hits));
    }

    [Fact]
    public void Search_IndexesSignatureTokens()
    {
        // Decision D3: index name + signature. A token only present in the signature must be findable.
        var index = MillerSearchIndex.Build(new[]
        {
            Doc(0, "Handle", signature: "(CancellationToken ct)"),
            Doc(1, "Other"),
        });

        var hits = index.Search("cancellationtoken", limit: 10);
        Assert.Equal(new[] { 0 }, Ids(hits));
    }

    [Fact]
    public void Search_HitCarriesFullDocumentMetadata()
    {
        var doc = new SearchableDocument(42, "Handle", "(int x)", "method", "csharp", "/src/H.cs", 17);
        var index = MillerSearchIndex.Build(new[] { doc });

        var hit = index.Search("handle", limit: 1)[0];
        Assert.Equal(doc, hit.Document);
        Assert.Equal("/src/H.cs", hit.Document.FilePath);
        Assert.Equal(17, hit.Document.StartLine);
    }

    [Fact]
    public void Build_DuplicateDocId_Throws()
    {
        // Build documents a unique-DocId contract (XML doc on Build). A regression to
        // last-write-wins would silently corrupt result identity, so pin the throw, the
        // offending parameter, and that the message names the colliding id.
        var ex = Assert.Throws<ArgumentException>(() =>
            MillerSearchIndex.Build(new[] { Doc(1, "alpha"), Doc(1, "beta") }));
        Assert.Equal("documents", ex.ParamName);
        Assert.Contains("1", ex.Message);
    }

    [Fact]
    public void Build_EmptyCorpus_SearchReturnsEmpty()
    {
        var index = MillerSearchIndex.Build(Array.Empty<SearchableDocument>());
        Assert.Equal(0, index.DocumentCount);
        Assert.Equal(0, index.TermCount);
        Assert.Empty(index.Search("anything", limit: 10));
    }
}
