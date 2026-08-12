using Microsoft.Data.Sqlite;
using Miller.Core.Search;
using Miller.Core.Tokenization;
using Miller.Indexing;
using Miller.Server.Tools;
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

    private void SetSearchSymbolNameToNull(string symbolId)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE search_symbols SET name = NULL WHERE symbol_id = $id;";
        command.Parameters.AddWithValue("$id", symbolId);
        command.ExecuteNonQuery();
    }

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
    public void Open_RoundTripsTestRoleAndCurrencyEvidenceThroughDiskLookups()
    {
        var syms = new[]
        {
            new IndexedSymbol(0, "suite", "Suite", "class Suite", "class", "csharp", "tests/Suite.cs",
                StartLine: 1, EndLine: 8, ParentId: null, IsTest: true,
                TestContainer: true, TestLifecycle: false,
                TestEvidenceStatus: TestRoleEvidence.CurrentStatus, TestEvidenceReason: null),
            new IndexedSymbol(1, "hook", "BeforeEach", "void BeforeEach()", "method", "csharp", "tests/Suite.cs",
                StartLine: 2, EndLine: 3, ParentId: "suite", IsTest: true,
                TestContainer: false, TestLifecycle: true,
                TestEvidenceStatus: TestRoleEvidence.UnknownStatus,
                TestEvidenceReason: TestRoleEvidence.ParseDiagnosticsReason),
        };
        SearchIndexWriter.Write(_dbPath, syms, revision: 42);

        var index = FtsSymbolSearchIndex.Open(_dbPath);
        var expected = syms[1].TestEvidence;

        Assert.Equal(expected, index.FindBySymbolId("hook")!.TestEvidence);
        Assert.Equal(expected, index.FindBySymbolIds(["hook"])["hook"].TestEvidence);
        Assert.Equal(expected, Assert.Single(index.FindByName("BeforeEach")).TestEvidence);
        Assert.Equal(expected, Assert.Single(index.FindChildren("suite")).TestEvidence);
        Assert.Equal(expected, index.FindByFilePath("tests/Suite.cs").Single(s => s.SymbolId == "hook").TestEvidence);
        Assert.Equal(expected, index.Resolve(1).TestEvidence);
        SearchHit hit = Assert.Single(index.Search("BeforeEach", limit: 10));
        Assert.Equal(expected, index.Resolve(hit.Document.DocId).TestEvidence);
    }

    [Fact]
    public void Open_UsesStoredDocId_NotSqliteRowid()
    {
        var syms = Corpus(
            ("a", "Alpha", "class Alpha", "class", "csharp", "src/A.cs"),
            ("b", "Beta", "class Beta", "class", "csharp", "src/B.cs"));
        SearchIndexWriter.Write(_dbPath, syms, revision: 42);

        using (var rw = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString()))
        {
            rw.Open();
            using var cmd = rw.CreateCommand();
            cmd.CommandText = "UPDATE search_symbols SET rowid = rowid + 100;";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var index = FtsSymbolSearchIndex.Open(_dbPath);

        SearchHit hit = Assert.Single(index.Search("Alpha", limit: 10));
        Assert.Equal(0, hit.Document.DocId);
        Assert.Equal("Alpha", index.Resolve(0).Name);
    }

    [Fact]
    public void Open_DelegatesLookups()
    {
        var syms = Corpus(
            ("a", "UserService", null, "class", "csharp", "auth/UserService.cs", ParentId: (string?)null),
            ("b", "GetUser", null, "method", "csharp", "auth/UserService.cs", ParentId: "a"),
            ("c", "GetUser", null, "method", "csharp", "auth/Other.cs", ParentId: null),
            ("d", "DeleteUser", null, "method", "csharp", "auth/UserService.cs", ParentId: "a"),
            ("e", "Cache", null, "class", "csharp", "core/Cache.cs", ParentId: null));
        SearchIndexWriter.Write(_dbPath, syms, revision: 1);

        var index = FtsSymbolSearchIndex.Open(_dbPath);

        Assert.Equal(2, index.FindByName("GetUser").Count);
        Assert.Equal("Cache", index.FindBySymbolId("e")!.Name);
        Assert.Equal(["GetUser", "DeleteUser"], index.FindChildren("a").Select(static s => s.Name).ToArray());
        Assert.NotEmpty(index.FindByFilePath("auth/UserService.cs"));
        Assert.Equal("core/Cache.cs", index.ResolveIndexedFilePath("Cache.cs"));
        Assert.Contains(".cs", index.KnownExtensions);
        Assert.NotEmpty(index.FindByFilePathFragment("UserService", limit: 10));
        Assert.Equal(["auth/UserService.cs"], index.FindFilePathsByFragment("UserService", limit: 10));
    }

    [Fact]
    public void FindBySymbolIds_ReturnsRequestedSymbols()
    {
        var syms = Corpus(
            ("a", "Alpha", null, "class", "csharp", "src/A.cs"),
            ("b", "Beta", null, "class", "csharp", "src/B.cs"),
            ("c", "Gamma", null, "class", "csharp", "src/C.cs"));
        SearchIndexWriter.Write(_dbPath, syms, revision: 1);

        var index = FtsSymbolSearchIndex.Open(_dbPath);

        var found = index.FindBySymbolIds(["b", "missing", "a"]);

        Assert.Equal(["b", "a"], found.Keys.ToArray());
        Assert.Equal("Beta", found["b"].Name);
        Assert.Equal("Alpha", found["a"].Name);
    }

    [Fact]
    public void Open_DoesNotEagerlyReadEveryResidentSymbol()
    {
        var syms = Corpus(
            ("a", "AlphaTarget", "class AlphaTarget", "class", "csharp", "src/A.cs"),
            ("b", "BrokenUnused", "class BrokenUnused", "class", "csharp", "src/B.cs"));
        SearchIndexWriter.Write(_dbPath, syms, revision: 1);
        SetSearchSymbolNameToNull("b");

        var index = FtsSymbolSearchIndex.Open(_dbPath);

        Assert.Equal(2, index.DocumentCount);
        SearchHit hit = Assert.Single(index.Search("AlphaTarget", limit: 10));
        Assert.Equal("AlphaTarget", index.Resolve(hit.Document.DocId).Name);
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
    public void Search_HighFanoutWordArm_HydratesOnlyRankedResultsWithExactParity()
    {
        IndexedSymbol[] syms = Enumerable.Range(0, 1_200)
            .Select(index => new IndexedSymbol(
                index,
                $"sym-{index:d4}",
                $"SharedItem{index:d4}",
                $"class SharedItem{index:d4}",
                "class",
                "csharp",
                $"src/S{index:d4}.cs",
                StartLine: 1,
                EndLine: 2,
                ParentId: null,
                IsTest: false))
            .ToArray();
        SearchIndexWriter.Write(_dbPath, syms, 1);
        var observations = new List<FtsSearchQueryObservation>();
        var fts = FtsSymbolSearchIndex.Open(_dbPath, observations.Add);
        var memory = SymbolSearchProjection.Build(syms);

        IReadOnlyList<SearchHit> expected = memory.Search("shared", limit: 7, mode: SearchMode.Or);
        IReadOnlyList<SearchHit> actual = fts.Search("shared", limit: 7, mode: SearchMode.Or);

        Assert.Equal(expected.Select(static hit => hit.Document.DocId), actual.Select(static hit => hit.Document.DocId));
        Assert.Equal(expected.Select(static hit => hit.Score), actual.Select(static hit => hit.Score));
        FtsSearchQueryObservation hydration = Assert.Single(observations, static observation =>
            observation.Family == FtsSearchQueryFamily.WordHydration);
        Assert.InRange(hydration.Rows, 0, 7);
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
    public void SearchTool_RerankAndRelaxation_RankingParity_WithInMemoryProjection()
    {
        var syms = Corpus(
            ("s1", "UserService", "class UserService", "class", "csharp", "svc/UserService.cs"),
            ("s2", "ServiceLocator", "class ServiceLocator", "class", "csharp", "svc/ServiceLocator.cs"),
            ("s3", "AuthService", "class AuthService : Service", "class", "csharp", "svc/AuthService.cs"),
            ("s4", "User", "class User", "class", "csharp", "models/User.cs"));
        SearchIndexWriter.Write(_dbPath, syms, 1);

        var fts = FtsSymbolSearchIndex.Open(_dbPath);
        var memory = SymbolSearchProjection.Build(syms);

        SymbolCandidateSet expected = SearchTool.CollectSymbolCandidates(
            memory,
            "user service",
            SearchToolMode.Symbol,
            limit: 4,
            excludeTests: null);
        SymbolCandidateSet actual = SearchTool.CollectSymbolCandidates(
            fts,
            "user service",
            SearchToolMode.Symbol,
            limit: 4,
            excludeTests: null);

        Assert.Equal(expected.Relaxed, actual.Relaxed);
        Assert.Equal(
            expected.Candidates.Select(static candidate => candidate.SymbolId),
            actual.Candidates.Select(static candidate => candidate.SymbolId));
        Assert.Equal(
            expected.Candidates.Select(static candidate => candidate.Score),
            actual.Candidates.Select(static candidate => candidate.Score));
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
    public void Search_ExactNameDefinitionOutranksImportRows_ParityWithInMemory()
    {
        var syms = Corpus(
            ("a", "WorkspacePool", "use super::workspace_pool::WorkspacePool;", "import", "rust", "src/daemon/app.rs"),
            ("b", "WorkspacePool", "pub struct WorkspacePool", "struct", "rust", "src/daemon/workspace_pool.rs"));
        SearchIndexWriter.Write(_dbPath, syms, 1);

        var fts = FtsSymbolSearchIndex.Open(_dbPath);
        var memory = SymbolSearchProjection.Build(syms);

        var expected = memory.Search("WorkspacePool", limit: 10);
        var actual = fts.Search("WorkspacePool", limit: 10);

        Assert.Equal("struct", actual[0].Document.Kind);
        Assert.Equal("src/daemon/workspace_pool.rs", actual[0].Document.FilePath);
        Assert.Equal(
            expected.Select(h => h.Document.DocId).ToArray(),
            actual.Select(h => h.Document.DocId).ToArray());
        for (int i = 0; i < expected.Count; i++)
            Assert.Equal(expected[i].Score, actual[i].Score, precision: 9);
    }

    [Fact]
    public void Search_ExactNameConcreteDefinitionOutranksManifestProperty_ParityWithInMemory()
    {
        var syms = Corpus(
            ("a", "flask", "flask = \"flask.cli:main\"", "property", "toml", "pyproject.toml"),
            ("b", "Flask", "class Flask extends App", "class", "python", "src/flask/app.py"));
        SearchIndexWriter.Write(_dbPath, syms, 1);

        var fts = FtsSymbolSearchIndex.Open(_dbPath);
        var memory = SymbolSearchProjection.Build(syms);

        var expected = memory.Search("Flask", limit: 10);
        var actual = fts.Search("Flask", limit: 10);

        Assert.Equal("class", actual[0].Document.Kind);
        Assert.Equal("src/flask/app.py", actual[0].Document.FilePath);
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
    public void Search_AndMode_DisjointHighFanoutTermsStopsAfterEmptyIntersectionProbe()
    {
        IndexedSymbol[] syms = Enumerable.Range(0, 2_000)
            .Select(index => new IndexedSymbol(
                index,
                $"sym-{index:d4}",
                index < 1_000 ? $"AlphaItem{index:d4}" : $"BetaItem{index:d4}",
                Signature: null,
                "class",
                "csharp",
                $"src/S{index:d4}.cs",
                StartLine: 1,
                EndLine: 2,
                ParentId: null,
                IsTest: false))
            .ToArray();
        SearchIndexWriter.Write(_dbPath, syms, 1);
        var observations = new List<FtsSearchQueryObservation>();
        var index = FtsSymbolSearchIndex.Open(_dbPath, observations.Add);

        IReadOnlyList<SearchHit> hits = index.Search("alpha beta", limit: 10, mode: SearchMode.And);

        Assert.Empty(hits);
        Assert.Equal(1, observations.Count(static observation =>
            observation.Family == FtsSearchQueryFamily.AndIntersectionProbe));
        Assert.Equal(0, observations.Count(static observation =>
            observation.Family == FtsSearchQueryFamily.WordCandidates));
    }

    [Fact]
    public void Search_AndMode_IntersectingTermsPreserveRankingAndExecuteExistingWordCandidates()
    {
        var syms = Corpus(
            ("a", "AlphaBeta", "class AlphaBeta", "class", "csharp", "src/A.cs"),
            ("b", "AlphaBetaFactory", "class AlphaBetaFactory", "class", "csharp", "src/B.cs"),
            ("c", "AlphaOnly", "class AlphaOnly", "class", "csharp", "src/C.cs"),
            ("d", "BetaOnly", "class BetaOnly", "class", "csharp", "src/D.cs"));
        SearchIndexWriter.Write(_dbPath, syms, 1);
        var observations = new List<FtsSearchQueryObservation>();
        var fts = FtsSymbolSearchIndex.Open(_dbPath, observations.Add);
        var memory = SymbolSearchProjection.Build(syms);

        IReadOnlyList<SearchHit> expected = memory.Search("alpha beta", limit: 10, mode: SearchMode.And);
        IReadOnlyList<SearchHit> actual = fts.Search("alpha beta", limit: 10, mode: SearchMode.And);

        Assert.Equal(expected.Select(static hit => hit.Document.DocId), actual.Select(static hit => hit.Document.DocId));
        Assert.Equal(expected.Select(static hit => hit.Score), actual.Select(static hit => hit.Score));
        Assert.Equal(1, observations.Count(static observation =>
            observation.Family == FtsSearchQueryFamily.AndIntersectionProbe));
        Assert.Equal(1, observations.Count(static observation =>
            observation.Family == FtsSearchQueryFamily.WordCandidates));
    }

    [Fact]
    public void Search_OrMode_ObservesEveryCompletedFixedStageInOrder()
    {
        var syms = Corpus(
            ("a", "AlphaBeta", "class AlphaBeta", "class", "csharp", "src/A.cs"),
            ("b", "Alphabet", "class Alphabet", "class", "csharp", "src/B.cs"));
        SearchIndexWriter.Write(_dbPath, syms, 1);
        var observations = new List<FtsSearchQueryObservation>();
        var index = FtsSymbolSearchIndex.Open(_dbPath, observations.Add);

        _ = index.Search("alpha", limit: 10, mode: SearchMode.Or);

        Assert.Equal(
            [
                FtsSearchQueryFamily.ConnectionOpen,
                FtsSearchQueryFamily.WordCandidates,
                FtsSearchQueryFamily.WordScoring,
                FtsSearchQueryFamily.WordHydration,
                FtsSearchQueryFamily.TrigramCandidates,
                FtsSearchQueryFamily.TrigramScoring,
                FtsSearchQueryFamily.FinalOrdering,
            ],
            observations.Select(static observation => observation.Family));
        Assert.Equal(0, observations[0].Rows);
        Assert.All(observations, static observation => Assert.True(observation.Rows >= 0));
        Assert.All(observations, static observation => Assert.True(observation.Elapsed >= TimeSpan.Zero));
    }

    [Fact]
    public void Search_ObservationIsEmptyForZeroWorkAndStopsAfterCompletedStageThrows()
    {
        var syms = Corpus(("a", "AlphaBeta", "class AlphaBeta", "class", "csharp", "src/A.cs"));
        SearchIndexWriter.Write(_dbPath, syms, 1);
        var observations = new List<FtsSearchQueryObservation>();
        var index = FtsSymbolSearchIndex.Open(
            _dbPath,
            observation =>
            {
                observations.Add(observation);
                if (observation.Family == FtsSearchQueryFamily.WordCandidates)
                    throw new OperationCanceledException("stop after completed word candidate query");
            });

        Assert.Empty(index.Search(string.Empty, limit: 10, mode: SearchMode.Or));
        Assert.Empty(observations);

        Assert.Throws<OperationCanceledException>(() =>
            index.Search("alpha", limit: 10, mode: SearchMode.Or));
        Assert.Equal(
            [FtsSearchQueryFamily.ConnectionOpen, FtsSearchQueryFamily.WordCandidates],
            observations.Select(static observation => observation.Family));
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

    [Fact]
    public void Search_WordHitsExactlyEqualLimit_SkipsTrigramArm()
    {
        var syms = Corpus(
            ("a", "AlphaTarget", null, "class", "csharp", "src/A.cs"),
            ("b", "ZalphaZ", null, "class", "csharp", "src/B.cs"));
        SearchIndexWriter.Write(_dbPath, syms, 1);
        var observations = new List<FtsSearchQueryObservation>();
        var index = FtsSymbolSearchIndex.Open(_dbPath, observations.Add);

        IReadOnlyList<SearchHit> hits = index.Search("alpha", limit: 1, mode: SearchMode.Or);

        Assert.Equal(["AlphaTarget"], hits.Select(static hit => hit.Document.Name));
        Assert.Equal(0, observations.Count(static observation =>
            observation.Family == FtsSearchQueryFamily.TrigramCandidates));
    }

    [Fact]
    public void Search_WordHitsBelowLimit_StillRunsTrigramArmAndFindsInteriorSubstring()
    {
        var syms = Corpus(
            ("a", "AlphaTarget", null, "class", "csharp", "src/A.cs"),
            ("b", "ZalphaZ", null, "class", "csharp", "src/B.cs"));
        SearchIndexWriter.Write(_dbPath, syms, 1);
        var observations = new List<FtsSearchQueryObservation>();
        var index = FtsSymbolSearchIndex.Open(_dbPath, observations.Add);

        IReadOnlyList<SearchHit> hits = index.Search("alpha", limit: 2, mode: SearchMode.Or);

        Assert.Equal(["AlphaTarget", "ZalphaZ"], hits.Select(static hit => hit.Document.Name));
        Assert.Equal(1, Assert.Single(observations, static observation =>
            observation.Family == FtsSearchQueryFamily.WordHydration).Rows);
        Assert.Equal(1, observations.Count(static observation =>
            observation.Family == FtsSearchQueryFamily.TrigramCandidates));
    }

    [Fact]
    public void Search_TrigramWindow_OrdersCandidatesByStoredDocId_NotFtsRowid()
    {
        var syms = Corpus(Enumerable.Range(0, 205)
            .Select(static i =>
            {
                string suffix = i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
                return ($"s{suffix}", $"NeedleMatch{suffix}", (string?)null, "class", "csharp", $"src/{suffix}.cs");
            })
            .ToArray());
        SearchIndexWriter.Write(_dbPath, syms, revision: 1);

        using (var rw = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString()))
        {
            rw.Open();
            using var cmd = rw.CreateCommand();
            cmd.CommandText = """
                DELETE FROM symbols_trigram WHERE symbol_id = 's000';
                INSERT INTO symbols_trigram(symbol_id, name_collapsed, qual_collapsed)
                VALUES ('s000', $collapsed, $collapsed);
                """;
            cmd.Parameters.AddWithValue("$collapsed", CollapseName.Of("NeedleMatch000"));
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var index = FtsSymbolSearchIndex.Open(_dbPath);

        string[] ids = index.Search("eedlem", limit: 200)
            .Select(h => index.Resolve(h.Document.DocId).SymbolId)
            .ToArray();

        Assert.Equal(
            Enumerable.Range(0, 200)
                .Select(static i => "s" + i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture))
                .ToArray(),
            ids);
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
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
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

    [Fact]
    public void Open_SchemaSeven_ThrowsInsteadOfDefaultingTestEvidence()
    {
        SearchIndexWriter.Write(_dbPath, Corpus(("a", "Alpha", null, "class", "csharp", "src/A.cs")), 1);

        using (var rw = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString()))
        {
            rw.Open();
            using var cmd = rw.CreateCommand();
            cmd.CommandText = "UPDATE meta SET schema_version = 7;";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var ex = Assert.Throws<InvalidOperationException>(() => FtsSymbolSearchIndex.Open(_dbPath));
        Assert.Contains("schema_version", ex.Message);
    }

    [Fact]
    public void Open_DuplicateMetaRows_Throws()
    {
        SearchIndexWriter.Write(_dbPath, Corpus(("a", "Alpha", null, "class", "csharp", "src/A.cs")), 1);

        using (var rw = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString()))
        {
            rw.Open();
            using var cmd = rw.CreateCommand();
            cmd.CommandText = """
                INSERT INTO meta(
                    revision, doc_count, avgdl, schema_version, region_count, region_avgdl, region_index_enabled)
                VALUES (1, 1, 1.0, $schema, 0, 0.0, 0);
                """;
            cmd.Parameters.AddWithValue("$schema", SearchIndexWriter.SchemaVersion);
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var ex = Assert.Throws<InvalidOperationException>(() => FtsSymbolSearchIndex.Open(_dbPath));
        Assert.Contains("multiple meta rows", ex.Message);
    }
}
