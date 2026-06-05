using Microsoft.Data.Sqlite;
using Miller.Core.Search;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins the on-disk <see cref="FtsSymbolSearchIndex"/>: a pure <c>search.db</c> reader that drops into the
/// <see cref="ISymbolLookupIndex"/> seam. Tests build a real <c>search.db</c> with
/// <see cref="SearchIndexWriter.Write"/> (no julie subprocess — stays in the fast suite) and assert the
/// reader's recall and, critically, RANKING PARITY with the in-memory <see cref="SymbolSearchProjection"/>:
/// word-arm queries must reproduce the in-memory top-N exactly; the trigram arm adds interior-substring
/// recall floored below the word hits.
/// </summary>
public sealed class FtsSymbolSearchIndexTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public FtsSymbolSearchIndexTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-ftsread-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "search.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // Build an IndexedSymbol[] in the SAME (path, start_line, symbol_id) order the reader re-derives from
    // search_symbols, with DocId == ordinal — so the reader and the in-memory projection assign identical
    // DocIds (parity depends on it). start_line is fixed at 1, so the order is (path, symbol_id) Ordinal.
    private static IndexedSymbol[] Corpus(
        params (string Id, string Name, string? Sig, string Kind, string Lang, string Path, string? ParentId)[] rows)
    {
        var ordered = rows
            .OrderBy(r => r.Path, StringComparer.Ordinal)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
            .ToArray();
        var syms = new IndexedSymbol[ordered.Length];
        for (int i = 0; i < ordered.Length; i++)
        {
            var r = ordered[i];
            syms[i] = new IndexedSymbol(i, r.Id, r.Name, r.Sig, r.Kind, r.Lang, r.Path,
                StartLine: 1, EndLine: 2, ParentId: r.ParentId, IsTest: false);
        }
        return syms;
    }

    private static IndexedSymbol[] Corpus(
        params (string Id, string Name, string? Sig, string Kind, string Lang, string Path)[] rows) =>
        Corpus(rows.Select(static r => (r.Id, r.Name, r.Sig, r.Kind, r.Lang, r.Path, ParentId: (string?)null)).ToArray());

    [Fact]
    public void Open_ExposesDocumentCountRevisionAndResolvesFullSymbol()
    {
        var syms = Corpus(
            ("a", "Alpha", "interface Alpha", "interface", "csharp", "src/A.cs"),
            ("b", "Beta", null, "class", "csharp", "src/B.cs"));
        SearchIndexWriter.Write(_dbPath, syms, revision: 42);

        var index = FtsSymbolSearchIndex.Open(_dbPath);

        Assert.Equal(2, index.DocumentCount);
        Assert.Equal(42L, index.Revision);

        IndexedSymbol alpha = index.FindBySymbolId("a")!;
        Assert.Equal("Alpha", index.Resolve(alpha.DocId).Name);
        // Self-contained artifact: the raw signature round-trips, so Resolve returns the full symbol.
        Assert.Equal("interface Alpha", index.Resolve(alpha.DocId).Signature);
    }

    [Fact]
    public void Open_DelegatesLookups()
    {
        var syms = Corpus(
            ("a", "GetUser", null, "method", "csharp", "auth/UserService.cs"),
            ("b", "GetUser", null, "method", "csharp", "auth/Other.cs"),
            ("c", "Cache", null, "class", "csharp", "core/Cache.cs"));
        SearchIndexWriter.Write(_dbPath, syms, revision: 1);

        var index = FtsSymbolSearchIndex.Open(_dbPath);

        Assert.Equal(2, index.FindByName("GetUser").Count);
        Assert.Equal("Cache", index.FindBySymbolId("c")!.Name);
        Assert.NotEmpty(index.FindByFilePath("auth/UserService.cs"));
        Assert.Equal("core/Cache.cs", index.ResolveIndexedFilePath("Cache.cs"));
        Assert.Contains(".cs", index.KnownExtensions);
        Assert.NotEmpty(index.FindByFilePathFragment("UserService", limit: 10));
    }

    [Fact]
    public void Search_WordArm_MatchesComponentTokenInsideCamelCaseIdentifier()
    {
        var syms = Corpus(
            ("a", "IAuthenticationProvider", null, "interface", "csharp", "src/A.cs"),
            ("b", "Unrelated", null, "class", "csharp", "src/B.cs"));
        SearchIndexWriter.Write(_dbPath, syms, 1);

        var index = FtsSymbolSearchIndex.Open(_dbPath);

        var names = index.Search("authentication", limit: 10)
            .Select(h => index.Resolve(h.Document.DocId).Name).ToList();

        Assert.Contains("IAuthenticationProvider", names);
        Assert.DoesNotContain("Unrelated", names);
    }

    [Fact]
    public void Search_WordArm_RankingParity_WithInMemoryProjection()
    {
        // 'service' is a shared component token across several symbols. The FTS word arm must reproduce the
        // in-memory BM25 ranking EXACTLY — identical DocId order and identical scores (same DF/TF/doc-len/
        // avgdl, the 1.5x exact-name boost, and the score-DESC/DocId-ASC tie-break). For these queries the
        // trigram arm adds no candidate the word arm misses, so the full result lists must be identical.
        var syms = Corpus(
            ("s1", "UserService", "class UserService", "class", "csharp", "svc/UserService.cs"),
            ("s2", "ServiceLocator", "class ServiceLocator", "class", "csharp", "svc/ServiceLocator.cs"),
            ("s3", "AuthService", "class AuthService : Service", "class", "csharp", "svc/AuthService.cs"),
            ("s4", "Service", null, "class", "csharp", "svc/Service.cs"),
            ("s5", "Cache", "class Cache", "class", "csharp", "core/Cache.cs"));
        SearchIndexWriter.Write(_dbPath, syms, 1);

        var fts = FtsSymbolSearchIndex.Open(_dbPath);
        var memory = SymbolSearchProjection.Build(syms);

        foreach (string q in new[] { "service", "Service", "user service", "auth" })
        {
            var expected = memory.Search(q, limit: 10, mode: SearchMode.Or);
            var actual = fts.Search(q, limit: 10, mode: SearchMode.Or);

            Assert.Equal(
                expected.Select(h => h.Document.DocId).ToArray(),
                actual.Select(h => h.Document.DocId).ToArray());
            Assert.Equal(expected.Count, actual.Count);
            for (int i = 0; i < expected.Count; i++)
                Assert.Equal(expected[i].Score, actual[i].Score, precision: 9);
        }
    }

    [Fact]
    public void Search_WordArm_RankingParity_NonAsciiIdentifiers_WithInMemoryProjection()
    {
        // Accent-collision parity (the Phase-5 caveat): 'Café' and 'Cafe' are DISTINCT terms to the in-memory
        // index (Ordinal token equality), so a query 'cafe' matches only 'Cafe'. If symbols_fts folded
        // diacritics, MATCH "cafe" would also hit 'Café', inflating the in-FTS document frequency and drifting
        // 'Cafe''s BM25 score off the in-memory value. With the writer pinning remove_diacritics 0 the DF — and
        // thus the full ranked list and every score — must match the in-memory projection exactly, and the
        // accented row must NOT surface (recall stays exact: the reader's C# re-tokenization drops it).
        var syms = Corpus(
            ("a", "Café", null, "class", "csharp", "src/Cafe_accented.cs"),
            ("b", "Cafe", null, "class", "csharp", "src/Cafe_plain.cs"),
            ("c", "Latte", null, "class", "csharp", "src/Latte.cs"));
        SearchIndexWriter.Write(_dbPath, syms, 1);

        var fts = FtsSymbolSearchIndex.Open(_dbPath);
        var memory = SymbolSearchProjection.Build(syms);

        var expected = memory.Search("cafe", limit: 10, mode: SearchMode.Or);
        var actual = fts.Search("cafe", limit: 10, mode: SearchMode.Or);

        Assert.Equal(
            expected.Select(h => h.Document.DocId).ToArray(),
            actual.Select(h => h.Document.DocId).ToArray());
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
            Assert.Equal(expected[i].Score, actual[i].Score, precision: 9);

        Assert.DoesNotContain("Café", actual.Select(h => fts.Resolve(h.Document.DocId).Name));
    }

    [Fact]
    public void Search_AndMode_RankingParity_WithInMemoryProjection()
    {
        // AND mode shares the same BM25 scoring + boost + tie-break as OR — only the candidate filter differs.
        // Pin parity for multi-result AND queries so the AND path can't silently drift from the in-memory index.
        var syms = Corpus(
            ("s1", "UserService", "class UserService", "class", "csharp", "svc/UserService.cs"),
            ("s2", "ServiceLocator", "class ServiceLocator", "class", "csharp", "svc/ServiceLocator.cs"),
            ("s3", "AuthService", "class AuthService : Service", "class", "csharp", "svc/AuthService.cs"),
            ("s4", "Service", null, "class", "csharp", "svc/Service.cs"),
            ("s5", "Cache", "class Cache", "class", "csharp", "core/Cache.cs"));
        SearchIndexWriter.Write(_dbPath, syms, 1);

        var fts = FtsSymbolSearchIndex.Open(_dbPath);
        var memory = SymbolSearchProjection.Build(syms);

        foreach (string q in new[] { "service", "user service", "auth service", "class service" })
        {
            var expected = memory.Search(q, limit: 10, mode: SearchMode.And);
            var actual = fts.Search(q, limit: 10, mode: SearchMode.And);

            Assert.Equal(
                expected.Select(h => h.Document.DocId).ToArray(),
                actual.Select(h => h.Document.DocId).ToArray());
            Assert.Equal(expected.Count, actual.Count);
            for (int i = 0; i < expected.Count; i++)
                Assert.Equal(expected[i].Score, actual[i].Score, precision: 9);
        }
    }

    [Fact]
    public void Search_SignatureOnlyMatch_FoundWithoutExactNameBoost_ParityWithInMemory()
    {
        // 'Dot' matches 'vector512' only through its signature (name != query → NO 1.5x boost); 'Vector512'
        // matches by name (boost applies). The FTS path must find both, withhold the boost from Dot, and rank
        // identically to the in-memory index.
        var syms = Corpus(
            ("a", "Dot", "double Dot(Vector512 v)", "method", "csharp", "m/Dot.cs"),
            ("b", "Vector512", null, "struct", "csharp", "m/Vector512.cs"));
        SearchIndexWriter.Write(_dbPath, syms, 1);

        var fts = FtsSymbolSearchIndex.Open(_dbPath);
        var memory = SymbolSearchProjection.Build(syms);

        var expected = memory.Search("vector512", limit: 10);
        var actual = fts.Search("vector512", limit: 10);

        // Boosted exact-name match ranks above the signature-only match; identical order in both backends.
        Assert.Equal(new[] { "Vector512", "Dot" },
            actual.Select(h => fts.Resolve(h.Document.DocId).Name).ToArray());
        Assert.Equal(
            expected.Select(h => h.Document.DocId).ToArray(),
            actual.Select(h => h.Document.DocId).ToArray());
        for (int i = 0; i < expected.Count; i++)
            Assert.Equal(expected[i].Score, actual[i].Score, precision: 9);
    }

    [Fact]
    public void Search_AndMode_RequiresAllDistinctTerms()
    {
        var syms = Corpus(
            ("a", "ServeHttp", "void ServeHttp()", "method", "csharp", "net/A.cs"),   // serve + http
            ("b", "ServeGrpc", "void ServeGrpc()", "method", "csharp", "net/B.cs"),   // serve only
            ("c", "HttpClient", "class HttpClient", "class", "csharp", "net/C.cs"));  // http only
        SearchIndexWriter.Write(_dbPath, syms, 1);

        var index = FtsSymbolSearchIndex.Open(_dbPath);

        var names = index.Search("serve http", limit: 10, mode: SearchMode.And)
            .Select(h => index.Resolve(h.Document.DocId).Name).ToList();

        Assert.Equal(new[] { "ServeHttp" }, names);
    }

    [Fact]
    public void Search_TrigramArm_FindsInteriorAndBoundaryCrossingSubstring()
    {
        var syms = Corpus(
            ("a", "IAuthenticationProvider", null, "interface", "csharp", "src/A.cs"),
            ("b", "format_external_extract", null, "function", "python", "src/B.py"),
            ("c", "Unrelated", null, "class", "csharp", "src/C.cs"));
        SearchIndexWriter.Write(_dbPath, syms, 1);

        var index = FtsSymbolSearchIndex.Open(_dbPath);

        // 'thenti' is interior to authentica|tion — no word token equals it; only the trigram arm matches.
        Assert.Equal(new[] { "IAuthenticationProvider" },
            index.Search("thenti", limit: 10).Select(h => index.Resolve(h.Document.DocId).Name).ToArray());

        // 'matexter' spans for|mat..exter|nal — a boundary-crossing fragment contiguous only once collapsed.
        Assert.Equal(new[] { "format_external_extract" },
            index.Search("matexter", limit: 10).Select(h => index.Resolve(h.Document.DocId).Name).ToArray());
    }

    [Fact]
    public void Search_TrigramArm_FindsCollapsedQualifiedParentChildSubstring()
    {
        var syms = Corpus(
            ("parent", "AuthProvider", null, "class", "csharp", "src/Auth.cs", ParentId: (string?)null),
            ("child", "ResolveToken", null, "method", "csharp", "src/Auth.cs", ParentId: "parent"),
            ("other", "Unrelated", null, "class", "csharp", "src/Other.cs", ParentId: (string?)null));
        SearchIndexWriter.Write(_dbPath, syms, 1);

        var index = FtsSymbolSearchIndex.Open(_dbPath);

        // Spans AuthProvider.ResolveToken across the parent-child boundary; neither bare name contains it.
        Assert.Equal(new[] { "ResolveToken" },
            index.Search("providerreso", limit: 10).Select(h => index.Resolve(h.Document.DocId).Name).ToArray());
    }

    [Fact]
    public void Search_TrigramOnlyHits_RankAfterWordHits()
    {
        // 'auth' is a whole component token of AuthProvider (word hit) but only an interior substring of
        // IAuthenticationProvider (its component is "authentication", not "auth") — a trigram-only hit.
        var syms = Corpus(
            ("a", "AuthProvider", null, "class", "csharp", "src/A.cs"),
            ("b", "IAuthenticationProvider", null, "interface", "csharp", "src/B.cs"));
        SearchIndexWriter.Write(_dbPath, syms, 1);

        var index = FtsSymbolSearchIndex.Open(_dbPath);

        var hits = index.Search("auth", limit: 10);
        Assert.Equal(
            new[] { "AuthProvider", "IAuthenticationProvider" },
            hits.Select(h => index.Resolve(h.Document.DocId).Name).ToArray());
        // The word hit carries a real BM25 score; the trigram-only hit is floored beneath it.
        Assert.True(hits[0].Score > 0.0);
        Assert.True(hits[1].Score < hits[0].Score);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("__")]   // tokenless and uncollapsible: separators only
    public void Search_EmptyOrTokenlessQuery_ReturnsEmpty(string query)
    {
        SearchIndexWriter.Write(_dbPath, Corpus(("a", "Alpha", null, "class", "csharp", "src/A.cs")), 1);
        var index = FtsSymbolSearchIndex.Open(_dbPath);
        Assert.Empty(index.Search(query, limit: 10));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Search_NonPositiveLimit_ReturnsEmpty(int limit)
    {
        SearchIndexWriter.Write(_dbPath, Corpus(("a", "Alpha", null, "class", "csharp", "src/A.cs")), 1);
        var index = FtsSymbolSearchIndex.Open(_dbPath);
        Assert.Empty(index.Search("alpha", limit: limit));
    }

    [Fact]
    public void Resolve_OutOfRange_Throws()
    {
        SearchIndexWriter.Write(_dbPath, Corpus(("a", "Alpha", null, "class", "csharp", "src/A.cs")), 1);
        var index = FtsSymbolSearchIndex.Open(_dbPath);
        Assert.Throws<ArgumentOutOfRangeException>(() => index.Resolve(index.DocumentCount));
        Assert.Throws<ArgumentOutOfRangeException>(() => index.Resolve(-1));
    }

    [Fact]
    public void Open_SchemaVersionMismatch_Throws()
    {
        // An incompatible (future) writer schema must be rejected so Phase 3 self-heals to the in-memory
        // projection instead of mis-reading the artifact.
        SearchIndexWriter.Write(_dbPath, Corpus(("a", "Alpha", null, "class", "csharp", "src/A.cs")), 1);

        using (var rw = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
        }.ToString()))
        {
            rw.Open();
            using var cmd = rw.CreateCommand();
            cmd.CommandText = "UPDATE meta SET schema_version = 999;";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var ex = Assert.Throws<InvalidOperationException>(() => FtsSymbolSearchIndex.Open(_dbPath));
        Assert.Contains("schema_version", ex.Message);
    }
}
