using System.Diagnostics;
using System.Runtime;
using Microsoft.Data.Sqlite;
using Miller.Core.Resolution;
using Miller.Indexing.Reads;
using Miller.Indexing.Resolution;
using Miller.Tests.Indexing;
using Xunit;

namespace Miller.Tests.Indexing.Resolution;

[Trait("Category", "Scale")]
[Collection(QueryTimeResolutionSnapshotCollection.Name)]
public sealed class RevisionFactCacheMemoryTests(ITestOutputHelper output)
{
    public const string SnapshotDirectory = "/tmp/qtr-aspnet-snapshot";
    public const long IdleBudgetBytes = 350L * 1024 * 1024;
    public const long PeakBudgetBytes = 600L * 1024 * 1024;

    [Fact]
    public void LoadSnapshot_StaysWithinWholeHostMemoryBudgets()
    {
        string storePath = Path.Combine(SnapshotDirectory, "store.db");
        if (!Directory.Exists(SnapshotDirectory) || !File.Exists(storePath))
            Assert.Skip("aspnet snapshot is not present at " + SnapshotDirectory);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long baselineRss = CurrentRss();
        long baselinePss = CurrentPss();
        long peakRss = baselineRss;
        long peakPss = baselinePss;

        RevisionFactCache cache;
        StoreVisibility visibility;
        using (SqliteConnection connection = OpenRead(storePath))
        {
            visibility = ReadVisibility(connection, storePath);
            cache = RevisionFactCache.Load(connection, visibility);
            peakRss = Math.Max(peakRss, CurrentRss());
            peakPss = Math.Max(peakPss, CurrentPss());
        }

        using (SqliteConnection connection = OpenRead(storePath))
        {
            _ = cache.SymbolsNamed("HttpContext").ToArray();
            _ = cache.Symbol(new FactSymbolKey(1, "missing"));
            _ = cache.TopLevelOf(visibility.ManifestGeneration);
            FactSymbol? first = cache.SymbolsNamed("Controller").FirstOrDefault();
            if (first is not null)
            {
                _ = cache.ChildrenOf(first.Key);
                _ = cache.TypeFactsOf(first.Key);
            }

            _ = cache.ImportsOf(1);
            _ = IdentifierSiteReader.SitesNamed(connection, visibility, "HttpContext").Take(8).ToArray();
            peakRss = Math.Max(peakRss, CurrentRss());
            peakPss = Math.Max(peakPss, CurrentPss());
        }

        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        long idleManaged = GC.GetTotalMemory(forceFullCollection: true);
        long idleRss = CurrentRss();
        long idlePss = CurrentPss();
        peakRss = Math.Max(peakRss, idleRss);
        peakPss = Math.Max(peakPss, idlePss);

        output.WriteLine(
            "baseline_rss={0} baseline_pss={1} idle_rss={2} idle_pss={3} peak_rss={4} peak_pss={5} idle_gc={6} cache={7}",
            baselineRss,
            baselinePss,
            idleRss,
            idlePss,
            peakRss,
            peakPss,
            idleManaged,
            cache.ResidentBytes);

        Assert.True(cache.ResidentBytes < IdleBudgetBytes, $"Cache resident {cache.ResidentBytes} exceeded {IdleBudgetBytes}.");
        if (baselinePss > 80L * 1024 * 1024)
        {
            output.WriteLine("absolute PSS not asserted; testhost baseline already {0}", baselinePss);
            return;
        }

        Assert.True(
            idlePss <= IdleBudgetBytes,
            $"Idle PSS {idlePss} exceeded {IdleBudgetBytes}. RSS={idleRss} GC={idleManaged} baselinePSS={baselinePss}.");
        Assert.True(
            peakPss <= PeakBudgetBytes,
            $"Peak PSS {peakPss} exceeded {PeakBudgetBytes}. RSS={peakRss}.");
    }

    private static SqliteConnection OpenRead(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static StoreVisibility ReadVisibility(SqliteConnection connection, string storePath)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT view_id,current_generation FROM views LIMIT 1";
        using SqliteDataReader reader = command.ExecuteReader();
        Assert.True(reader.Read());
        string viewId = reader.GetString(0);
        long generation = reader.GetInt64(1);
        return new StoreVisibility(
            "snapshot",
            SnapshotDirectory,
            "gen-001",
            storePath,
            Path.Combine(SnapshotDirectory, "coord.db"),
            viewId,
            "/tmp/ws",
            generation,
            "snapshot-manifest",
            "exact",
            null,
            null,
            null,
            generation,
            "full",
            "2.34.4",
            "snapshot",
            "1",
            "2",
            "3");
    }

    private static long CurrentRss()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return process.WorkingSet64;
    }

    private static long CurrentPss()
    {
        string path = "/proc/self/smaps_rollup";
        if (!File.Exists(path))
            return CurrentRss();
        foreach (string line in File.ReadLines(path))
        {
            if (!line.StartsWith("Pss:", StringComparison.Ordinal))
                continue;
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && long.TryParse(parts[1], out long kib))
                return kib * 1024;
        }

        return CurrentRss();
    }
}
