using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Miller.Core.Search;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Search;

/// <summary>
/// The Phase-5 recall eval against a REAL corpus — Miller's own committed <c>.miller/symbols.db</c> (the only
/// schema-compatible large index available on this machine; OpenClaw/Hermes are an older incompatible artifact
/// and would need a multi-minute re-extraction). It builds the in-memory baseline and the on-disk FTS5 candidate
/// from one shared snapshot, then asserts the candidate is a strict recall SUPERSET (interior substring recall
/// rises by a real margin) with ZERO word-arm ranking regression, and that the routing gate actually TAKES the
/// disk path for a revision-fresh artifact (a silent self-heal to the in-memory index would make every parity
/// check pass trivially). Recall@5 / MRR per class, build time, artifact size and first-search latency are logged
/// as the eval evidence.
///
/// <para><c>[Trait("Category","Scale")]</c>: it reads a ~45 MB DB and builds a full FTS index, so it stays out of
/// the &lt;10s fast suite. It spawns NO julie subprocess (it reads the already-built committed index), so it does
/// not use the Scale launch signal; it SKIPS (never fails) when the index is absent or schema-incompatible.</para>
/// </summary>
[Trait("Category", "Scale")]
public sealed class SymbolSearchEvalScaleTests : IDisposable
{
    // Single-language (C#) certification: the verdict a PASS here yields. The whole corpus is indexed in both
    // backends; only the query SAMPLING FRAME is restricted to C# identifiers (SearchRecallEval.DefaultFrame).
    private const int Seed = 20260604;
    private const int SampleSize = 200;
    private const long ArtifactRevision = 1;
    private const int InteriorRecallFloor_x100 = 10;   // candidate interior recall must beat baseline by >= 0.10
    private const int MinConclusiveN = 30;

    private readonly ITestOutputHelper _output;
    private readonly string _dir;

    public SymbolSearchEvalScaleTests(ITestOutputHelper output)
    {
        _output = output;
        _dir = Path.Combine(Path.GetTempPath(), "miller-eval-scale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void RecallEval_OnMillerCorpus_IsSupersetWithParity_AndDiskPathIsTaken()
    {
        string symbolsDb = Path.Combine(ScaleTestSupport.RepoRoot(), ".miller", "symbols.db");
        Assert.SkipUnless(File.Exists(symbolsDb),
            $"no committed index at {symbolsDb} — run the indexer first to enable the real-corpus eval.");

        IReadOnlyList<IndexedSymbol> corpus;
        try
        {
            corpus = SqliteSymbolReader.Read(symbolsDb);
        }
        // A pre-schema-2 / partially-written / locked index surfaces as IncompatibleExtractException OR a raw
        // Sqlite/InvalidOperation read failure. All mean "this machine's committed index can't feed the eval" —
        // SKIP (never fail), exactly like the missing-index case above.
        catch (Exception ex) when (ex is IncompatibleExtractException or InvalidOperationException or SqliteException)
        {
            Assert.Skip($"{symbolsDb} is not a readable schema-compatible julie artifact " +
                $"({ex.GetType().Name}: {ex.Message}); rebuild to run the eval.");
            return;
        }
        Assert.SkipUnless(corpus.Count >= 500, $"corpus has only {corpus.Count} symbols — too small for a meaningful eval.");

        // Baseline (in-memory BM25) and candidate (on-disk FTS5) from the SAME snapshot.
        var baseline = SymbolSearchProjection.Build(corpus);

        string searchDb = Path.Combine(_dir, "search.db");
        var buildSw = Stopwatch.StartNew();
        SearchIndexWriter.Write(searchDb, corpus, ArtifactRevision);
        buildSw.Stop();
        long sizeBytes = new FileInfo(searchDb).Length;
        var candidate = FtsSymbolSearchIndex.Open(searchDb);

        // Identity precondition: a divergent DocId->symbol_id map would make recall@5 and parity meaningless.
        Assert.Equal(baseline.DocumentCount, candidate.DocumentCount);
        Assert.Equal(
            Enumerable.Range(0, baseline.DocumentCount).Select(d => baseline.Resolve(d).SymbolId).ToArray(),
            Enumerable.Range(0, candidate.DocumentCount).Select(d => candidate.Resolve(d).SymbolId).ToArray());

        // First-search latency (cold connection open + word arm + trigram arm) on the real artifact.
        var searchSw = Stopwatch.StartNew();
        _ = candidate.Search("Search", limit: 10, SearchMode.Or);
        searchSw.Stop();

        SearchRecallEval.Report report = SearchRecallEval.Evaluate(
            corpus, baseline, candidate,
            new SearchRecallEval.Options(Seed: Seed, SampleSize: SampleSize));

        LogReport(report, corpus.Count, sizeBytes, buildSw.Elapsed, searchSw.Elapsed);

        // ---- Gate 1: word-arm ranking parity is EXACT (the structural Bm25 guarantee on real queries). ----
        Assert.Equal(0, report.Parity.Violations);
        Assert.Null(report.Parity.FirstViolation);

        // ---- Gate 2: word classes do not regress. Parity makes them bit-identical, so assert equality. ----
        foreach (var cls in new[]
        {
            SearchRecallEval.QueryClass.Exact, SearchRecallEval.QueryClass.Camel, SearchRecallEval.QueryClass.Lasttok,
        })
        {
            var stat = report.Class(cls);
            Assert.True(stat.N >= MinConclusiveN, $"{cls} N={stat.N} below the conclusive floor {MinConclusiveN}");
            Assert.Equal(stat.BaselineRecall, stat.CandidateRecall);
        }

        // ---- Gate 3: the routing gate TAKES the disk path for a revision-fresh artifact (no silent fallback). ----
        // Asserted BEFORE Gate 4 so an inconclusive interior sample (Gate 4's SkipUnless) can never abort the
        // disk-path-taken proof — the whole eval is meaningless if reads silently self-heal to the in-memory index.
        var sidecar = new SymbolSearchSidecar(enabled: true);
        // SearchDbPathFor derives the sibling search.db of this dir's symbols.db — which is the artifact we built.
        string siblingSymbolsDb = Path.Combine(_dir, "symbols.db");
        ISymbolLookupIndex? routed = sidecar.TryOpen(siblingSymbolsDb, ArtifactRevision);
        Assert.IsType<FtsSymbolSearchIndex>(routed);
        Assert.NotEmpty(routed!.Search("Search", limit: 5, SearchMode.Or));
        // A stale revision must self-heal to null (the in-memory path), never serve a mismatched artifact.
        Assert.Null(sidecar.TryOpen(siblingSymbolsDb, ArtifactRevision + 1));

        // ---- Gate 4: interior substring recall is a strict, real-margin superset. ----
        var interior = report.Class(SearchRecallEval.QueryClass.Interior);
        Assert.SkipUnless(interior.N >= MinConclusiveN,
            $"interior N={interior.N} is inconclusive on this sample; raise SampleSize or seed.");
        Assert.True(interior.BaselineRecall < 0.95,
            $"baseline already aced interior recall ({interior.BaselineRecall:F3}); the sample is degenerate.");
        double floor = InteriorRecallFloor_x100 / 100.0;
        Assert.True(interior.CandidateRecall >= interior.BaselineRecall + floor,
            $"interior recall gain {interior.CandidateRecall - interior.BaselineRecall:F3} below the {floor:F2} floor " +
            $"(baseline {interior.BaselineRecall:F3} -> candidate {interior.CandidateRecall:F3}).");
    }

    private void LogReport(
        SearchRecallEval.Report report, int corpusSize, long sizeBytes, TimeSpan build, TimeSpan firstSearch)
    {
        _output.WriteLine($"corpus symbols: {corpusSize}   frame (csharp identifiers): {report.FrameSize}   " +
            $"sampled: {report.Sampled}   seed: {report.Seed}");
        _output.WriteLine($"search.db build: {build.TotalSeconds:F2}s   size: {sizeBytes / 1024.0 / 1024.0:F1} MB   " +
            $"first-search: {firstSearch.TotalMilliseconds:F0} ms   workingSet: {Process.GetCurrentProcess().WorkingSet64 / 1024.0 / 1024.0:F0} MB");
        _output.WriteLine($"word-arm parity: {report.Parity.Compared} compared, {report.Parity.Violations} violations");
        _output.WriteLine("class       N   skip   baseR@5  candR@5   baseMRR  candMRR");
        foreach (var s in report.Classes)
            _output.WriteLine(
                $"{s.Class,-9} {s.N,3}   {s.Skipped,4}    {s.BaselineRecall,6:F3}   {s.CandidateRecall,6:F3}   " +
                $"{s.BaselineMrr,6:F3}  {s.CandidateMrr,6:F3}");
    }
}
