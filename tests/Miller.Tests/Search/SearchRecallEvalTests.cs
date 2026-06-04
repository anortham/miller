using Microsoft.Data.Sqlite;
using Miller.Core.Search;
using Miller.Core.Tokenization;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Search;

/// <summary>
/// Fast-suite methodology lock for the Phase-5 recall eval (<see cref="SearchRecallEval"/>): pins the pure
/// query-class derivation and the recall@5 / MRR math deterministically, and proves the end-to-end superset
/// mechanism on a tiny crafted corpus — the on-disk candidate recovers interior substrings the in-memory
/// baseline cannot, while the word classes stay bit-identical (ranking parity). No julie subprocess and a
/// two-symbol search.db, so it belongs in the fast suite; the at-scale numbers live in the Scale eval.
/// </summary>
public sealed class SearchRecallEvalTests : IDisposable
{
    private readonly string _dir;

    public SearchRecallEvalTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-recall-eval-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // ---- query derivation (pure) ----------------------------------------------------------------------

    [Fact]
    public void Derivation_CamelCaseName_YieldsExactComponentAndInteriorClasses()
    {
        Assert.Equal("IAuthenticationProvider", SearchEvalQueries.TryExact("IAuthenticationProvider"));
        Assert.Equal("authentication", SearchEvalQueries.TryCamel("IAuthenticationProvider"));   // interior component
        Assert.Equal("provider", SearchEvalQueries.TryLasttok("IAuthenticationProvider"));        // last component

        string? interior = SearchEvalQueries.TryInterior("IAuthenticationProvider", _ => 1, popularityCap: 20);
        Assert.NotNull(interior);
        // Interior is an all-letter substring of the collapsed name that equals NO word token (so the word arm
        // can't reach it) — the property the trigram arm exists to recover.
        Assert.Contains(interior!, CollapseName.Of("IAuthenticationProvider"), StringComparison.Ordinal);
        Assert.DoesNotContain(interior, Tokens("IAuthenticationProvider"));
        Assert.InRange(interior!.Length, 4, 6);
        Assert.All(interior!, c => Assert.True(char.IsAsciiLetter(c)));
    }

    [Fact]
    public void Derivation_SnakeCaseName_ComponentsComeFromRuns()
    {
        Assert.Equal("format_external_extract", SearchEvalQueries.TryExact("format_external_extract"));
        Assert.Equal("format", SearchEvalQueries.TryCamel("format_external_extract"));
        Assert.Equal("extract", SearchEvalQueries.TryLasttok("format_external_extract"));
        Assert.NotNull(SearchEvalQueries.TryInterior("format_external_extract", _ => 1, 20));
    }

    [Fact]
    public void Derivation_SkipsDegenerateClasses()
    {
        // Single short word: no component split, too short for an interior window.
        Assert.Null(SearchEvalQueries.TryCamel("Foo"));
        Assert.Null(SearchEvalQueries.TryLasttok("Foo"));
        Assert.Null(SearchEvalQueries.TryInterior("Foo", _ => 1, 20));
        // 1-char exact is degenerate.
        Assert.Null(SearchEvalQueries.TryExact("X"));
        // All-digit / 1-char tails are not usable component queries.
        Assert.Null(SearchEvalQueries.TryLasttok("Vector512"));  // last component "512" is all digits
    }

    [Fact]
    public void Derivation_Interior_SkipsTooPopularFragments()
    {
        // A fragment present in too many collapsed names measures corpus density, not the index — skip it.
        Assert.Null(SearchEvalQueries.TryInterior("IAuthenticationProvider", _ => 1000, popularityCap: 20));
    }

    [Fact]
    public void Derivation_IsDeterministic()
    {
        Assert.Equal(
            SearchEvalQueries.TryInterior("WorkspaceRegistrySelector", _ => 1, 20),
            SearchEvalQueries.TryInterior("WorkspaceRegistrySelector", _ => 1, 20));
    }

    // ---- metrics (pure) -------------------------------------------------------------------------------

    [Fact]
    public void Metrics_RecallAndReciprocalRank_KeyOnSymbolId()
    {
        var index = SymbolSearchProjection.Build(Corpus(
            ("a", "Alpha", "src/A.cs"), ("b", "Beta", "src/B.cs"), ("c", "Gamma", "src/C.cs")));
        // A hand-built ranked list: Beta at rank 1, Gamma at rank 2, Alpha at rank 3.
        var hits = new[] { Hit(index, "b"), Hit(index, "c"), Hit(index, "a") };

        Assert.Equal(1.0, RecallMetrics.RecallAt(hits, "b", index, k: 5));
        Assert.Equal(1.0, RecallMetrics.RecallAt(hits, "a", index, k: 5));
        Assert.Equal(0.0, RecallMetrics.RecallAt(hits, "a", index, k: 2));   // Alpha is rank 3, outside top-2
        Assert.Equal(0.0, RecallMetrics.RecallAt(hits, "missing", index, k: 5));

        Assert.Equal(1.0, RecallMetrics.ReciprocalRank(hits, "b", index));   // rank 1
        Assert.Equal(0.5, RecallMetrics.ReciprocalRank(hits, "c", index));   // rank 2
        Assert.Equal(0.0, RecallMetrics.ReciprocalRank(hits, "missing", index));
    }

    // ---- end-to-end superset on a crafted corpus ------------------------------------------------------

    [Fact]
    public void Evaluate_OnDiskCandidate_IsRecallSupersetWithWordArmParity()
    {
        var corpus = Corpus(
            Row("AuthenticationProvider"), Row("ServiceLocatorFactory"), Row("UserAccountRepository"),
            Row("RequestPipelineBuilder"), Row("ConfigurationManagerStore"), Row("DependencyResolverScope"),
            Row("BackgroundTaskScheduler"), Row("DistributedCacheRegistry"), Row("TelemetryAggregatorSink"),
            Row("WorkspaceRegistrySelector"));

        var baseline = SymbolSearchProjection.Build(corpus);
        string searchDb = Path.Combine(_dir, "search.db");
        SearchIndexWriter.Write(searchDb, corpus, revision: 1);
        var candidate = FtsSymbolSearchIndex.Open(searchDb);

        // Identity precondition: both backends must assign identical DocId->symbol_id maps (same snapshot).
        Assert.Equal(baseline.DocumentCount, candidate.DocumentCount);
        for (int d = 0; d < baseline.DocumentCount; d++)
            Assert.Equal(baseline.Resolve(d).SymbolId, candidate.Resolve(d).SymbolId);

        var report = SearchRecallEval.Evaluate(
            corpus, baseline, candidate,
            new SearchRecallEval.Options(Seed: 1, SampleSize: corpus.Length, PopularityCap: 20));

        Assert.Equal(corpus.Length, report.FrameSize);
        Assert.Equal(corpus.Length, report.Sampled);

        // Word arm: bit-identical ranking, so recall is exactly equal and there are zero parity violations.
        Assert.Equal(0, report.Parity.Violations);
        foreach (var cls in new[] { SearchRecallEval.QueryClass.Exact, SearchRecallEval.QueryClass.Camel, SearchRecallEval.QueryClass.Lasttok })
        {
            var stat = report.Class(cls);
            Assert.True(stat.N > 0, $"{cls} produced no queries");
            Assert.Equal(stat.BaselineRecall, stat.CandidateRecall);  // parity => identical recall
        }

        // Interior arm: the candidate strictly improves recall the baseline cannot reach.
        var interior = report.Class(SearchRecallEval.QueryClass.Interior);
        Assert.True(interior.N >= 6, $"interior N={interior.N} too small to be meaningful");
        Assert.True(interior.BaselineRecall <= 0.2, $"baseline interior recall {interior.BaselineRecall} unexpectedly high");
        Assert.True(interior.CandidateRecall >= 0.8, $"candidate interior recall {interior.CandidateRecall} too low");
        Assert.True(interior.CandidateRecall > interior.BaselineRecall, "interior recall did not improve");
    }

    // ---- helpers --------------------------------------------------------------------------------------

    private static (string Id, string Name, string Path) Row(string name) => (name.ToLowerInvariant(), name, $"src/{name}.cs");

    // Build an IndexedSymbol[] in (path, start_line, symbol_id) order with DocId == ordinal, so the in-memory
    // projection and the search.db reader assign identical DocIds (parity + identity require it).
    private static IndexedSymbol[] Corpus(params (string Id, string Name, string Path)[] rows)
    {
        var ordered = rows
            .OrderBy(r => r.Path, StringComparer.Ordinal)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
            .ToArray();
        var syms = new IndexedSymbol[ordered.Length];
        for (int i = 0; i < ordered.Length; i++)
        {
            var r = ordered[i];
            syms[i] = new IndexedSymbol(i, r.Id, r.Name, Signature: null, "class", "csharp", r.Path,
                StartLine: 1, EndLine: 2, ParentId: null, IsTest: false);
        }
        return syms;
    }

    private static SearchHit Hit(ISymbolLookupIndex index, string symbolId)
    {
        IndexedSymbol s = index.FindBySymbolId(symbolId)!;
        return new SearchHit(s.ToSearchableDocument(), 1.0);
    }

    private static HashSet<string> Tokens(string name)
    {
        var list = new List<string>();
        CodeTokenizer.Tokenize(name, list);
        return new HashSet<string>(list, StringComparer.Ordinal);
    }
}
