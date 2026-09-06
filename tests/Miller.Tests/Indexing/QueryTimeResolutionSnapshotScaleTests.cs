using System.Diagnostics;
using System.Runtime;
using Miller.Core.Resolution;
using Miller.Indexing.Reads;
using Miller.Indexing.Resolution;
using Miller.Tests.Indexing.Resolution;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Miller.Tests.Indexing;

[CollectionDefinition(QueryTimeResolutionSnapshotCollection.Name, DisableParallelization = true)]
public sealed class QueryTimeResolutionSnapshotCollection
{
    public const string Name = "QueryTimeResolutionSnapshots";
}

[Trait("Category", "Scale")]
[Collection(QueryTimeResolutionSnapshotCollection.Name)]
public sealed class QueryTimeResolutionSnapshotScaleTests(ITestOutputHelper output)
{
    public const long IdleBudgetBytes = 350L * 1024 * 1024;
    public const long PeakBudgetBytes = 600L * 1024 * 1024;
    public const double WarmP95BudgetMs = 500;

    [Fact]
    public void MillerSnapshot_FullCorpusParity_SkipsWhenAbsent()
    {
        SnapshotParity(QueryTimeResolutionParity.MillerSnapshotDirectory, assertMemory: false);
    }

    [Fact]
    public void AspnetcoreSnapshot_ParityWarmP95AndMemory_SkipsWhenAbsent()
    {
        SnapshotParity(QueryTimeResolutionParity.AspnetSnapshotDirectory, assertMemory: true);
    }

    [Fact]
    public void SnapshotPair_EvictionIsReportOnly_SkipsWhenAbsent()
    {
        string millerStore = Path.Combine(QueryTimeResolutionParity.MillerSnapshotDirectory, "store.db");
        string aspnetStore = Path.Combine(QueryTimeResolutionParity.AspnetSnapshotDirectory, "store.db");
        if (!File.Exists(millerStore) || !File.Exists(aspnetStore))
            Assert.Skip("One or both snapshot directories are absent.");

        var store = new RevisionFactCacheStore(100L * 1024 * 1024);
        using (SqliteConnection miller = QueryTimeResolutionParity.OpenRead(millerStore))
        {
            StoreVisibility visibility = QueryTimeResolutionParity.ReadExactVisibility(miller, millerStore);
            using (var lease = store.Acquire(
                "miller",
                visibility.ManifestHash + ":" + visibility.ManifestGeneration,
                () => QueryTimeResolutionParity.OpenRead(millerStore),
                visibility))
            {
            }
        }

        int afterFirst = store.ScopeCount;
        long afterFirstBytes = store.ResidentBytes;
        using (SqliteConnection aspnet = QueryTimeResolutionParity.OpenRead(aspnetStore))
        {
            StoreVisibility visibility = QueryTimeResolutionParity.ReadExactVisibility(aspnet, aspnetStore);
            using (var lease = store.Acquire(
                "aspnet",
                visibility.ManifestHash + ":" + visibility.ManifestGeneration,
                () => QueryTimeResolutionParity.OpenRead(aspnetStore),
                visibility))
            {
            }
        }

        output.WriteLine(
            "eviction after_first_scopes={0} after_first_mb={1} after_second_scopes={2} after_second_mb={3}",
            afterFirst,
            QueryTimeResolutionParity.FmtMb(afterFirstBytes),
            store.ScopeCount,
            QueryTimeResolutionParity.FmtMb(store.ResidentBytes));
        Assert.Equal(1, store.ScopeCount);
    }

    private void SnapshotParity(string directory, bool assertMemory)
    {
        string storePath = Path.Combine(directory, "store.db");
        if (!Directory.Exists(directory) || !File.Exists(storePath))
            Assert.Skip("Snapshot is not present at " + directory);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long baselineRss = QueryTimeResolutionParity.CurrentRss();
        long baselinePss = QueryTimeResolutionParity.CurrentPss();
        long peakRss = baselineRss;
        long peakPss = baselinePss;

        using SqliteConnection store = QueryTimeResolutionParity.OpenRead(storePath);
        StoreVisibility visibility = QueryTimeResolutionParity.ReadExactVisibility(store, storePath);
        string? basePath = QueryTimeResolutionParity.LocateResolutionBase(store, visibility);
        Assert.SkipWhen(basePath is null, "Snapshot has no readable resolution base at " + directory);
        QueryTimeResolutionParity.AttachResolutionBase(store, basePath!);

        long loadStarted = Stopwatch.GetTimestamp();
        RevisionFactCache cache = RevisionFactCache.Load(store, visibility);
        TimeSpan load = Stopwatch.GetElapsedTime(loadStarted);
        peakRss = Math.Max(peakRss, QueryTimeResolutionParity.CurrentRss());
        peakPss = Math.Max(peakPss, QueryTimeResolutionParity.CurrentPss());

        var resolver = new QueryTimeResolver(cache);
        IReadOnlyList<string> names = QueryTimeResolutionParity.WarmNameMix(store, visibility);
        TimeSpan coldFirst = names.Count == 0
            ? TimeSpan.Zero
            : QueryTimeResolutionParity.QueryName(store, visibility, cache, resolver, names[0]);
        peakRss = Math.Max(peakRss, QueryTimeResolutionParity.CurrentRss());
        peakPss = Math.Max(peakPss, QueryTimeResolutionParity.CurrentPss());

        var warm = new List<double>(names.Count);
        foreach (string name in names)
            _ = QueryTimeResolutionParity.QueryName(store, visibility, cache, resolver, name);
        foreach (string name in names)
            warm.Add(QueryTimeResolutionParity.QueryName(store, visibility, cache, resolver, name).TotalMilliseconds);
        warm.Sort();
        double p50 = Percentile(warm, 0.50);
        double p95 = Percentile(warm, 0.95);
        double max = warm.Count == 0 ? 0 : warm[^1];
        peakRss = Math.Max(peakRss, QueryTimeResolutionParity.CurrentRss());
        peakPss = Math.Max(peakPss, QueryTimeResolutionParity.CurrentPss());

        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        long idleManaged = GC.GetTotalMemory(forceFullCollection: true);
        long idleRss = QueryTimeResolutionParity.CurrentRss();
        long idlePss = QueryTimeResolutionParity.CurrentPss();
        peakRss = Math.Max(peakRss, idleRss);
        peakPss = Math.Max(peakPss, idlePss);

        Dictionary<(long VersionId, string Id), QueryTimeResolutionParity.PendingFact> pendings =
            QueryTimeResolutionParity.ReadPendingFacts(store, visibility);
        Dictionary<(long VersionId, string Id), QueryTimeResolutionParity.RelationshipFact> relationships =
            QueryTimeResolutionParity.ReadRelationshipFacts(store, visibility);
        Dictionary<(long VersionId, string Id), StoredResolution> storedIdentifiers =
            QueryTimeResolutionParity.ReadStoredIdentifiers(store, visibility);
        Dictionary<(long VersionId, string Id), StoredResolution> storedPendings =
            QueryTimeResolutionParity.ReadStoredPendings(store, visibility);
        long sweepStarted = Stopwatch.GetTimestamp();
        ParityReport identifiers = QueryTimeResolutionParity.CompareIdentifiers(
            store, visibility, cache, resolver, storedIdentifiers, pendings, relationships);
        ParityReport pendingRows = QueryTimeResolutionParity.ComparePendings(
            cache, resolver, storedPendings, pendings);
        TimeSpan sweep = Stopwatch.GetElapsedTime(sweepStarted);

        output.WriteLine(
            "snapshot={0} view={1} generation={2} load_ms={3} cold_first_ms={4} sweep_ms={5} p50_ms={6} p95_ms={7} max_ms={8}",
            directory,
            visibility.ViewId,
            visibility.ManifestGeneration,
            QueryTimeResolutionParity.FmtMs(load),
            QueryTimeResolutionParity.FmtMs(coldFirst),
            QueryTimeResolutionParity.FmtMs(sweep),
            p50.ToString("0.0"),
            p95.ToString("0.0"),
            max.ToString("0.0"));
        output.WriteLine(
            "memory baseline_pss_mb={0} idle_pss_mb={1} peak_pss_mb={2} idle_rss_mb={3} peak_rss_mb={4} idle_gc_mb={5} cache_mb={6}",
            QueryTimeResolutionParity.FmtMb(baselinePss),
            QueryTimeResolutionParity.FmtMb(idlePss),
            QueryTimeResolutionParity.FmtMb(peakPss),
            QueryTimeResolutionParity.FmtMb(idleRss),
            QueryTimeResolutionParity.FmtMb(peakRss),
            QueryTimeResolutionParity.FmtMb(idleManaged),
            QueryTimeResolutionParity.FmtMb(cache.ResidentBytes));
        output.WriteLine(
            "identifiers compared={0} matched={1} under_resolved={2} divergences={3}",
            identifiers.Compared,
            identifiers.Matched,
            identifiers.UnderResolved,
            identifiers.Divergences.Count);
        output.WriteLine(
            "pendings compared={0} matched={1} under_resolved={2} divergences={3}",
            pendingRows.Compared,
            pendingRows.Matched,
            pendingRows.UnderResolved,
            pendingRows.Divergences.Count);
        foreach (string row in identifiers.UnderResolvedSamples)
            output.WriteLine("under_resolved " + row);
        foreach (string row in pendingRows.UnderResolvedSamples)
            output.WriteLine("under_resolved " + row);
        foreach (string row in identifiers.Divergences)
            output.WriteLine("divergence " + row);
        foreach (string row in pendingRows.Divergences)
            output.WriteLine("divergence " + row);

        Assert.True(identifiers.Passed, string.Join(Environment.NewLine, identifiers.Divergences));
        Assert.True(pendingRows.Passed, string.Join(Environment.NewLine, pendingRows.Divergences));
        Assert.True(identifiers.Compared > 0);
        if (!assertMemory)
            return;

        Assert.True(p95 <= WarmP95BudgetMs, $"Warm refs p95 {p95:0.0} ms exceeded {WarmP95BudgetMs} ms.");
        Assert.True(cache.ResidentBytes < IdleBudgetBytes, $"Cache resident {cache.ResidentBytes} exceeded {IdleBudgetBytes}.");
        if (baselinePss > 80L * 1024 * 1024)
        {
            output.WriteLine("absolute PSS not asserted; testhost baseline already {0}", baselinePss);
            return;
        }

        Assert.True(
            idlePss <= IdleBudgetBytes,
            $"Idle PSS {idlePss} exceeded {IdleBudgetBytes}. RSS={idleRss} GC={idleManaged}.");
        Assert.True(
            peakPss <= PeakBudgetBytes,
            $"Peak PSS {peakPss} exceeded {PeakBudgetBytes}. RSS={peakRss}.");
    }

    private static double Percentile(IReadOnlyList<double> sorted, double fraction)
    {
        if (sorted.Count == 0)
            return 0;
        int index = (int)Math.Ceiling(fraction * sorted.Count) - 1;
        if (index < 0)
            index = 0;
        if (index >= sorted.Count)
            index = sorted.Count - 1;
        return sorted[index];
    }
}
