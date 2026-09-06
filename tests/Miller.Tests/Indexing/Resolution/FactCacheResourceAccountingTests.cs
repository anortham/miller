using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Core.Graph;
using Miller.Core.Resolution;
using Miller.Indexing.Reads;
using Miller.Indexing.Resolution;
using Xunit;

namespace Miller.Tests.Indexing.Resolution;

/// <summary>
/// Real SQLite I/O accounting and full/bounded parity tests.
/// Verifies full/bounded parity after shared eviction, old revision lease during switch,
/// duplicate loads, and oversized entry.
/// </summary>
public sealed class FactCacheResourceAccountingTests
{
    private static readonly string[] TestNames = ["App", "Run", "Helper", "Other", "Extra"];
    private static readonly long[] TestVersions = [1, 2];

    [Fact]
    public void FullAndBoundedParity_PreservedAfterSharedEviction()
    {
        using ResolutionStoreFixture fixture1 = PopulateFixture("view-1", "src/Ws1");
        using ResolutionStoreFixture fixture2 = PopulateFixture("view-2", "src/Ws2");

        // Measure fixture 1 resident size
        long size1;
        var probeStore = new RevisionFactCacheStore();
        using (var probeLease = probeStore.Acquire("ws-1", "rev-1", fixture1.OpenRead, fixture1.Visibility()))
        {
            size1 = probeLease.Cache.ResidentBytes;
        }

        Assert.True(size1 > 0);

        // Budget fits exactly one entry. Loading ws-2 will evict ws-1 from retained map.
        var store = new RevisionFactCacheStore(byteBudget: size1);

        var lease1 = store.Acquire("ws-1", "rev-1", fixture1.OpenRead, fixture1.Visibility());
        Assert.Equal(1, store.GetResourceSnapshot().RetainedEntryCount);
        Assert.Equal(0, store.GetResourceSnapshot().EvictedHeldEntryCount);

        var lease2 = store.Acquire("ws-2", "rev-1", fixture2.OpenRead, fixture2.Visibility());

        CacheResourceSnapshot snapshot = store.GetResourceSnapshot();
        Assert.Equal(1, snapshot.RetainedEntryCount);
        Assert.Equal(1, snapshot.EvictedHeldEntryCount);
        Assert.Equal(2, snapshot.ActiveLeaseCount);
        Assert.Equal(2, snapshot.UniqueLiveEntryCount);
        Assert.Equal(lease1.Cache.ResidentBytes + lease2.Cache.ResidentBytes, snapshot.UniqueLiveBytes);

        // Verify full/bounded parity for evicted-held lease1
        using SqliteConnection boundedConn = fixture1.OpenRead();
        RevisionFactCache bounded1 = RevisionFactCache.LoadBounded(boundedConn, fixture1.Visibility());
        AssertParity(lease1.Cache, bounded1, fixture1);

        // Verify query time resolution reader edges match identically
        using SqliteConnection fullConn = fixture1.OpenRead();
        var readerFull = new QueryTimeResolutionReader(lease1.Cache, fixture1.Visibility());
        var readerBounded = new QueryTimeResolutionReader(bounded1, fixture1.Visibility());
        Assert.Equal(
            JsonSerializer.Serialize(readerFull.ReadResolutionEdges(fullConn, ["sym-run"], Direction.Both, null)),
            JsonSerializer.Serialize(readerBounded.ReadResolutionEdges(boundedConn, ["sym-run"], Direction.Both, null)));

        lease1.Dispose();
        CacheResourceSnapshot afterRelease1 = store.GetResourceSnapshot();
        Assert.Equal(0, afterRelease1.EvictedHeldEntryCount);
        Assert.Equal(1, afterRelease1.ActiveLeaseCount);
        Assert.Equal(1, afterRelease1.RetainedEntryCount);
        Assert.Equal(1, afterRelease1.UniqueLiveEntryCount);

        lease2.Dispose();
        CacheResourceSnapshot afterRelease2 = store.GetResourceSnapshot();
        Assert.Equal(0, afterRelease2.ActiveLeaseCount);
        Assert.Equal(1, afterRelease2.RetainedEntryCount);
    }

    [Fact]
    public void FullAndBoundedParity_PreservedDuringRevisionSwitch()
    {
        using ResolutionStoreFixture fixture = PopulateFixture("view-switch", "src/App");
        var store = new RevisionFactCacheStore();

        // Acquire rev-1
        var leaseRev1 = store.Acquire("ws-switch", "rev-1", fixture.OpenRead, fixture.Visibility());
        Assert.Equal(1, store.GetResourceSnapshot().RetainedEntryCount);

        // Advance fixture to revision 2 with an extra symbol
        fixture.FlipManifest(2, [
            ("src/App/App.cs", 1, "csharp", "indexed"),
            ("src/App/Other.cs", 2, "csharp", "indexed"),
            ("src/App/Extra.cs", 3, "csharp", "indexed")
        ]);
        fixture.AddSymbol(3, "sym-extra", "Extra", "class", "src/App/Extra.cs");
        fixture.AddIdentifier(3, "id-extra", "Extra", "src/App/Extra.cs", kind: "type_usage", containingSymbolId: "sym-extra");

        StoreVisibility visibility2 = fixture.Visibility();

        // Acquire rev-2 under same workspace scope
        var leaseRev2 = store.Acquire("ws-switch", "rev-2", fixture.OpenRead, visibility2);

        // Accounting: rev-2 replaced rev-1 in scopes; rev-1 is evicted-held
        CacheResourceSnapshot snapshot = store.GetResourceSnapshot();
        Assert.Equal(1, snapshot.RetainedEntryCount);
        Assert.Equal(1, snapshot.EvictedHeldEntryCount);
        Assert.Equal(2, snapshot.ActiveLeaseCount);
        Assert.Equal(2, snapshot.UniqueLiveEntryCount);
        Assert.Equal(leaseRev1.Cache.ResidentBytes + leaseRev2.Cache.ResidentBytes, snapshot.UniqueLiveBytes);

        // Verify parity for rev-1
        using (SqliteConnection conn1 = fixture.OpenRead())
        {
            StoreVisibility visibility1 = new(
                fixture.Visibility().FamilyId,
                fixture.Root,
                "gen-001",
                fixture.StorePath,
                System.IO.Path.Combine(fixture.Root, "coord.db"),
                fixture.ViewId,
                "/tmp/ws",
                1,
                "manifest-1",
                "unbound",
                null, null, null,
                1, "full", "2.31.0", "store-1", "1", "2", "3");

            RevisionFactCache boundedRev1 = RevisionFactCache.LoadBounded(conn1, visibility1);
            AssertParity(leaseRev1.Cache, boundedRev1, fixture, versions: [1, 2]);
            Assert.Empty(leaseRev1.Cache.SymbolsNamed("Extra"));
        }

        // Verify parity for rev-2
        using (SqliteConnection conn2 = fixture.OpenRead())
        {
            RevisionFactCache boundedRev2 = RevisionFactCache.LoadBounded(conn2, visibility2);
            AssertParity(leaseRev2.Cache, boundedRev2, fixture, versions: [1, 2, 3]);
            Assert.Single(leaseRev2.Cache.SymbolsNamed("Extra"));
            Assert.Single(boundedRev2.SymbolsNamed("Extra"));
        }

        leaseRev1.Dispose();
        Assert.Equal(0, store.GetResourceSnapshot().EvictedHeldEntryCount);

        leaseRev2.Dispose();
        Assert.Equal(0, store.GetResourceSnapshot().ActiveLeaseCount);
        Assert.Equal(1, store.GetResourceSnapshot().RetainedEntryCount);
    }

    [Fact]
    public void DuplicateLoads_CoalesceAndShareSingleObjectAccounting()
    {
        using ResolutionStoreFixture fixture = PopulateFixture("view-dup", "src/App");
        var store = new RevisionFactCacheStore();

        var leaseA = store.Acquire("ws-dup", "rev-1", fixture.OpenRead, fixture.Visibility());
        var leaseB = store.Acquire("ws-dup", "rev-1", fixture.OpenRead, fixture.Visibility());
        var leaseC = store.Acquire("ws-dup", "rev-1", fixture.OpenRead, fixture.Visibility());

        Assert.Same(leaseA.Cache, leaseB.Cache);
        Assert.Same(leaseB.Cache, leaseC.Cache);

        CacheResourceSnapshot snapshot = store.GetResourceSnapshot();
        Assert.Equal(1, snapshot.LoadCount);
        Assert.Equal(3, snapshot.ActiveLeaseCount);
        Assert.Equal(leaseA.Cache.ResidentBytes, snapshot.ActiveBytes);
        Assert.Equal(1, snapshot.RetainedEntryCount);
        Assert.Equal(leaseA.Cache.ResidentBytes, snapshot.RetainedBytes);
        Assert.Equal(1, snapshot.UniqueLiveEntryCount);
        Assert.Equal(leaseA.Cache.ResidentBytes, snapshot.UniqueLiveBytes);
        Assert.Equal(0, snapshot.EvictedHeldEntryCount);

        using SqliteConnection boundedConn = fixture.OpenRead();
        RevisionFactCache bounded = RevisionFactCache.LoadBounded(boundedConn, fixture.Visibility());
        AssertParity(leaseA.Cache, bounded, fixture);

        leaseA.Dispose();
        leaseB.Dispose();
        Assert.Equal(1, store.GetResourceSnapshot().ActiveLeaseCount);
        Assert.Equal(leaseA.Cache.ResidentBytes, store.GetResourceSnapshot().ActiveBytes);

        leaseC.Dispose();
        Assert.Equal(0, store.GetResourceSnapshot().ActiveLeaseCount);
        Assert.Equal(0L, store.GetResourceSnapshot().ActiveBytes);
        Assert.Equal(1, store.GetResourceSnapshot().RetainedEntryCount);
    }

    [Fact]
    public void OversizedEntry_ServesNormallyAndReportsInAccounting()
    {
        using ResolutionStoreFixture fixture = PopulateFixture("view-over", "src/App");
        // Budget of 1 byte forces the entry to be oversized
        var store = new RevisionFactCacheStore(byteBudget: 1);

        var lease = store.Acquire("ws-over", "rev-1", fixture.OpenRead, fixture.Visibility());

        CacheResourceSnapshot snapshot = store.GetResourceSnapshot();
        Assert.True(snapshot.OversizedEntryCount >= 1);
        Assert.Equal(1, snapshot.ActiveLeaseCount);
        Assert.Equal(lease.Cache.ResidentBytes, snapshot.ActiveBytes);
        Assert.Equal(1, snapshot.UniqueLiveEntryCount);
        Assert.Equal(lease.Cache.ResidentBytes, snapshot.UniqueLiveBytes);

        using SqliteConnection boundedConn = fixture.OpenRead();
        RevisionFactCache bounded = RevisionFactCache.LoadBounded(boundedConn, fixture.Visibility());
        AssertParity(lease.Cache, bounded, fixture);

        using SqliteConnection fullConn = fixture.OpenRead();
        var readerFull = new QueryTimeResolutionReader(lease.Cache, fixture.Visibility());
        var readerBounded = new QueryTimeResolutionReader(bounded, fixture.Visibility());
        Assert.Equal(
            JsonSerializer.Serialize(readerFull.ReadResolutionEdges(fullConn, ["sym-run"], Direction.Both, null)),
            JsonSerializer.Serialize(readerBounded.ReadResolutionEdges(boundedConn, ["sym-run"], Direction.Both, null)));

        lease.Dispose();
        Assert.Equal(0, store.GetResourceSnapshot().ActiveLeaseCount);
    }

    [Fact]
    public void Benchmark_FactCacheResources()
        => RunBenchmark(Environment.GetEnvironmentVariable);

    [Theory]
    [InlineData("FIXTURE", "does-not-exist")]
    [InlineData("FIXTURE", "")]
    [InlineData("RUNS", "0")]
    [InlineData("RUNS", "-1")]
    [InlineData("RUNS", "garbage")]
    [InlineData("RUNS", "2147483648")]
    [InlineData("RUNS", "2147483647")]
    [InlineData("RUNS", "")]
    [InlineData("RUNS", "1.5")]
    [InlineData("RUNS", "+1")]
    [InlineData("WORKSPACES", "0")]
    [InlineData("WORKSPACES", "-1")]
    [InlineData("WORKSPACES", "garbage")]
    [InlineData("WORKSPACES", "2147483648")]
    [InlineData("REVISIONS", "0")]
    [InlineData("REVISIONS", "-1")]
    [InlineData("REVISIONS", "garbage")]
    [InlineData("REVISIONS", "2147483638")]
    [InlineData("BUDGET_MB", "0")]
    [InlineData("BUDGET_MB", "-1")]
    [InlineData("BUDGET_MB", "garbage")]
    [InlineData("BUDGET_MB", "8796093022208")]
    [InlineData("BUDGET_MB", "9223372036854775808")]
    public void Benchmark_InvalidInputIsRejectedBeforeOutputChanges(string option, string value)
    {
        string output = Path.GetTempFileName();
        try
        {
            File.WriteAllText(output, "existing report");
            var inputs = new Dictionary<string, string>
            {
                ["BENCH_FACT_CACHE_OUTPUT"] = output,
                ["BENCH_FACT_CACHE_RUNS"] = "1",
                ["BENCH_FACT_CACHE_WORKSPACES"] = "1",
                ["BENCH_FACT_CACHE_REVISIONS"] = "1",
                [$"BENCH_FACT_CACHE_{option}"] = value
            };
            var error = Assert.Throws<ArgumentException>(() => RunBenchmark(name => inputs.GetValueOrDefault(name)));
            Assert.Contains($"BENCH_FACT_CACHE_{option}", error.Message);
            Assert.Equal("existing report", File.ReadAllText(output));
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Theory]
    [InlineData("1", 1L, 1048576L)]
    [InlineData("0001", 1L, 1048576L)]
    [InlineData("8796093022207", 8796093022207L, 9223372036853727232L)]
    public void Benchmark_ValidInputsProduceMatchingOptionsAndRuns(string budget, long expectedMb, long expectedBytes)
    {
        string output = Path.GetTempFileName();
        try
        {
            var inputs = new Dictionary<string, string>
            {
                ["BENCH_FACT_CACHE_OUTPUT"] = output,
                ["BENCH_FACT_CACHE_FIXTURE"] = "sqlite-synthetic",
                ["BENCH_FACT_CACHE_RUNS"] = "2",
                ["BENCH_FACT_CACHE_WORKSPACES"] = "1",
                ["BENCH_FACT_CACHE_REVISIONS"] = "1",
                ["BENCH_FACT_CACHE_BUDGET_MB"] = budget
            };
            RunBenchmark(name => inputs.GetValueOrDefault(name));
            using var report = JsonDocument.Parse(File.ReadAllText(output));
            JsonElement options = report.RootElement.GetProperty("options");
            Assert.Equal("sqlite-synthetic", options.GetProperty("fixture").GetString());
            Assert.Equal(2, options.GetProperty("runs").GetInt32());
            Assert.Equal(1, options.GetProperty("workspaces").GetInt32());
            Assert.Equal(1, options.GetProperty("revisions").GetInt32());
            Assert.Equal(expectedMb, options.GetProperty("budget_mb").GetInt64());
            Assert.Equal(expectedBytes, report.RootElement.GetProperty("diagnostics").GetProperty("cache_budget_bytes").GetInt64());
            Assert.Equal(2, report.RootElement.GetProperty("runs").GetArrayLength());
            Assert.Equal(1, report.RootElement.GetProperty("deterministic_summary").GetProperty("loads").GetInt32());
        }
        finally
        {
            File.Delete(output);
        }
    }

    private static void RunBenchmark(Func<string, string?> readArgument)
    {
        string? outputPath = readArgument("BENCH_FACT_CACHE_OUTPUT");
        int runs = (int)ReadPositiveArgument(readArgument, "RUNS", 5, int.MaxValue - 1);
        int workspaces = (int)ReadPositiveArgument(readArgument, "WORKSPACES", 2, int.MaxValue - 1);
        int revisions = (int)ReadPositiveArgument(readArgument, "REVISIONS", 2, int.MaxValue - 10);
        long budgetMb = ReadPositiveArgument(readArgument, "BUDGET_MB", 256, long.MaxValue / (1024L * 1024L));
        string fixtureType = readArgument("BENCH_FACT_CACHE_FIXTURE") ?? "sqlite-synthetic";
        if (fixtureType != "sqlite-synthetic")
            throw new ArgumentException("BENCH_FACT_CACHE_FIXTURE must be sqlite-synthetic.");

        long budgetBytes = checked(budgetMb * 1024L * 1024L);

        var runResults = new List<BenchmarkRunRecord>();

        for (int runIndex = 1; runIndex <= runs; runIndex++)
        {
            BenchmarkRunRecord runRecord = ExecuteBenchmarkRun(runIndex, workspaces, revisions, budgetBytes);
            runResults.Add(runRecord);
        }

        // Assert deterministic counters across all runs
        if (runResults.Count > 1)
        {
            BenchmarkRunRecord first = runResults[0];
            for (int i = 1; i < runResults.Count; i++)
            {
                BenchmarkRunRecord current = runResults[i];
                Assert.Equal(first.RetainedBytes, current.RetainedBytes);
                Assert.Equal(first.ActiveUniqueBytes, current.ActiveUniqueBytes);
                Assert.Equal(first.EvictedHeldBytes, current.EvictedHeldBytes);
                Assert.Equal(first.UnionBytes, current.UnionBytes);
                Assert.Equal(first.Loads, current.Loads);
                Assert.Equal(first.CoalescedLoads, current.CoalescedLoads);
                Assert.Equal(first.OversizedCount, current.OversizedCount);
            }
        }

        if (!string.IsNullOrEmpty(outputPath))
        {
            var report = new
            {
                options = new
                {
                    fixture = fixtureType,
                    workspaces = workspaces,
                    revisions = revisions,
                    budget_mb = budgetMb,
                    runs = runs
                },
                deterministic_summary = new
                {
                    retained_bytes = runResults[0].RetainedBytes,
                    active_unique_bytes = runResults[0].ActiveUniqueBytes,
                    evicted_held_bytes = runResults[0].EvictedHeldBytes,
                    union_bytes = runResults[0].UnionBytes,
                    loads = runResults[0].Loads,
                    coalesced_loads = runResults[0].CoalescedLoads,
                    oversized_count = runResults[0].OversizedCount
                },
                diagnostics = new
                {
                    rss_sampled_from_process = true,
                    rss_inferred_from_cache = false,
                    cache_budget_bytes = budgetBytes,
                    budget_is_soft = true
                },
                runs = runResults.Select(result => new
                {
                    run = result.Run,
                    retained_bytes = result.RetainedBytes,
                    active_unique_bytes = result.ActiveUniqueBytes,
                    evicted_held_bytes = result.EvictedHeldBytes,
                    union_bytes = result.UnionBytes,
                    loads = result.Loads,
                    coalesced_loads = result.CoalescedLoads,
                    oversized_count = result.OversizedCount,
                    wall_time_ms = result.WallTimeMs,
                    process_rss_bytes = result.ProcessRssBytes
                }).ToArray()
            };

            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            string? dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(outputPath, json);
        }
    }

    private static long ReadPositiveArgument(Func<string, string?> readArgument, string option, long defaultValue, long maximum)
    {
        string name = $"BENCH_FACT_CACHE_{option}";
        string? value = readArgument(name);
        if (value is null)
            return defaultValue;
        if (value.Length == 0 || value.Any(c => c is < '0' or > '9')
            || !long.TryParse(value, out long parsed) || parsed < 1 || parsed > maximum)
            throw new ArgumentException($"{name} must be a positive integer no greater than {maximum}.");
        return parsed;
    }

    private static BenchmarkRunRecord ExecuteBenchmarkRun(
        int runIndex,
        int workspaceCount,
        int revisionCount,
        long budgetBytes)
    {
        var sw = Stopwatch.StartNew();
        var store = new RevisionFactCacheStore(byteBudget: budgetBytes);
        var fixtures = new List<ResolutionStoreFixture>();
        var heldLeases = new List<RevisionFactCacheLease>();

        try
        {
            for (int w = 0; w < workspaceCount; w++)
            {
                var fixture = PopulateFixture($"bench-ws-{w}", $"src/Ws{w}");
                fixtures.Add(fixture);

                // Revision 1
                var lease1 = store.Acquire($"scope-{w}", "rev-1", fixture.OpenRead, fixture.Visibility());
                _ = lease1.Cache.SymbolsNamed("App").ToArray();

                if (w == 0)
                {
                    // Hold revision 1 of workspace 0 so it becomes evicted-held when revision 2 is loaded
                    heldLeases.Add(lease1);
                }
                else
                {
                    lease1.Dispose();
                }

                // Additional revisions
                for (int r = 2; r <= revisionCount; r++)
                {
                    fixture.FlipManifest(r, [
                        ($"src/Ws{w}/App.cs", 1, "csharp", "indexed"),
                        ($"src/Ws{w}/Other.cs", 2, "csharp", "indexed"),
                        ($"src/Ws{w}/Rev{r}.cs", r + 10, "csharp", "indexed")
                    ]);
                    fixture.AddSymbol(r + 10, $"sym-rev-{r}", $"RevSymbol{r}", "class", $"src/Ws{w}/Rev{r}.cs");

                    var leaseR = store.Acquire($"scope-{w}", $"rev-{r}", fixture.OpenRead, fixture.Visibility());
                    _ = leaseR.Cache.SymbolsNamed($"RevSymbol{r}").ToArray();

                    if (w == 0 && r == revisionCount)
                    {
                        // Keep current revision lease active as well
                        heldLeases.Add(leaseR);
                    }
                    else
                    {
                        leaseR.Dispose();
                    }
                }
            }

            CacheResourceSnapshot snapshot = store.GetResourceSnapshot();
            sw.Stop();

            Process.GetCurrentProcess().Refresh();
            long processRss = Process.GetCurrentProcess().WorkingSet64;

            return new BenchmarkRunRecord(
                Run: runIndex,
                RetainedBytes: snapshot.RetainedBytes,
                ActiveUniqueBytes: snapshot.ActiveBytes,
                EvictedHeldBytes: snapshot.EvictedHeldBytes,
                UnionBytes: snapshot.UniqueLiveBytes,
                Loads: snapshot.LoadCount,
                CoalescedLoads: snapshot.CoalescedLoadCount,
                OversizedCount: snapshot.OversizedEntryCount,
                WallTimeMs: Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                ProcessRssBytes: processRss);
        }
        finally
        {
            foreach (var lease in heldLeases)
                lease.Dispose();
            foreach (var fixture in fixtures)
                fixture.Dispose();
        }
    }

    private sealed record BenchmarkRunRecord(
        int Run,
        long RetainedBytes,
        long ActiveUniqueBytes,
        long EvictedHeldBytes,
        long UnionBytes,
        int Loads,
        int CoalescedLoads,
        int OversizedCount,
        double WallTimeMs,
        long ProcessRssBytes);

    private static void AssertParity(
        RevisionFactCache full,
        RevisionFactCache bounded,
        ResolutionStoreFixture fixture,
        long[]? versions = null)
    {
        foreach (string name in TestNames)
        {
            Assert.Equal(
                JsonSerializer.Serialize(full.SymbolsNamed(name)),
                JsonSerializer.Serialize(bounded.SymbolsNamed(name)));
        }

        foreach (long versionId in versions ?? TestVersions)
        {
            Assert.Equal(
                JsonSerializer.Serialize(full.SymbolsOfVersion(versionId)),
                JsonSerializer.Serialize(bounded.SymbolsOfVersion(versionId)));
            Assert.Equal(
                JsonSerializer.Serialize(full.TopLevelOf(versionId)),
                JsonSerializer.Serialize(bounded.TopLevelOf(versionId)));
            Assert.Equal(
                JsonSerializer.Serialize(full.ImportsOf(versionId)),
                JsonSerializer.Serialize(bounded.ImportsOf(versionId)));
            Assert.Equal(full.Slice(versionId)?.Language, bounded.Slice(versionId)?.Language);
            Assert.Equal(full.Slice(versionId)?.Path, bounded.Slice(versionId)?.Path);
        }
    }

    private static ResolutionStoreFixture PopulateFixture(string viewId, string pathPrefix)
    {
        ResolutionStoreFixture fixture = ResolutionStoreFixture.Create();
        fixture.ExecuteWrite($"UPDATE views SET view_id='{viewId}'; UPDATE manifests SET view_id='{viewId}';");

        fixture.WriteTransaction(() =>
        {
            fixture.AddFile(1, $"{pathPrefix}/App.cs");
            fixture.AddFile(2, $"{pathPrefix}/Other.cs");

            fixture.AddSymbol(1, "sym-app", "App", "class", $"{pathPrefix}/App.cs", visibility: "public");
            fixture.AddSymbol(1, "sym-run", "Run", "method", $"{pathPrefix}/App.cs", parentId: "sym-app", signature: "void Run()");
            fixture.AddSymbol(1, "sym-help", "Helper", "function", $"{pathPrefix}/App.cs", parentId: "sym-app");
            fixture.AddIdentifier(1, "id-help", "Helper", $"{pathPrefix}/App.cs", kind: "call", containingSymbolId: "sym-run", startByte: 10, endByte: 16);
            fixture.AddPending(1, "pend-help", "sym-run", "Helper", $"{pathPrefix}/App.cs", startByte: 10, endByte: 16);
            fixture.AddRelationship(1, "rel-help", "sym-run", "sym-help", $"{pathPrefix}/App.cs", startByte: 20, endByte: 26);

            fixture.AddSymbol(2, "sym-other", "Other", "class", $"{pathPrefix}/Other.cs");
            fixture.AddTypeFact(2, "tf-other", "sym-other", "App");
            fixture.AddIdentifier(2, "id-app-ref", "App", $"{pathPrefix}/Other.cs", kind: "type_usage", containingSymbolId: "sym-other", startByte: 5, endByte: 8);
        });

        return fixture;
    }
}
