using System.Diagnostics;
using Miller.Indexing;
using Miller.Server;
using Miller.Server.Resolution;
using Miller.Server.Telemetry;
using Miller.Server.Tools;
using Xunit;

namespace Miller.Tests.Server;

[CollectionDefinition(nameof(ContextPerformanceCollection), DisableParallelization = true)]
public sealed class ContextPerformanceCollection;

/// <summary>Proves actionable context and impact behavior over a real extractor artifact.</summary>
[Trait("Category", "Scale")]
[Collection(nameof(ContextPerformanceCollection))]
public sealed class LiveContextImpactTests
{
    [Fact]
    public void Live_ImpactAndContext_OverRealExtract_AreCorrect()
    {
        string binary = ScaleTestSupport.RequireJulieServer();

        string work = Path.Combine(Path.GetTempPath(), "miller-m5live-" + Guid.NewGuid().ToString("N"));
        string repo = Path.Combine(work, "repo");
        string millerDir = Path.Combine(repo, ".miller");
        Directory.CreateDirectory(repo);
        Directory.CreateDirectory(millerDir);

        try
        {
            // A real C# dependency chain: Controller calls Service.Process, Service.Process calls Repo.Load, and
            // an xUnit test calls Service.Process. julie's identifiers (dense) carry the call edges; is_test flags
            // the xUnit method. So the REVERSE closure of Process = {Controller-side caller, the test}.
            File.WriteAllText(Path.Combine(repo, "OrderRepo.cs"), """
                namespace Shop;

                public sealed class OrderRepo
                {
                    public int Load(int id) => id;
                }
                """);
            File.WriteAllText(Path.Combine(repo, "OrderService.cs"), """
                namespace Shop;

                public sealed class OrderService
                {
                    private readonly OrderRepo _repo = new();

                    public int Process(int id)
                    {
                        return _repo.Load(id);
                    }
                }
                """);
            File.WriteAllText(Path.Combine(repo, "OrderController.cs"), """
                namespace Shop;

                public sealed class OrderController
                {
                    private readonly OrderService _service = new();

                    public int Handle(int id)
                    {
                        return _service.Process(id);
                    }
                }
                """);
            // An xUnit test that exercises Process — julie marks it is_test=true (the likely-test leg).
            File.WriteAllText(Path.Combine(repo, "OrderServiceTests.cs"), """
                using Xunit;

                namespace Shop.Tests;

                public sealed class OrderServiceTests
                {
                    [Fact]
                    public void ProcessWorks()
                    {
                        var service = new OrderService();
                        Assert.Equal(7, service.Process(7));
                    }
                }
                """);

            // --- scan with the real binary into the Miller-owned .miller/symbols.db ---
            string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(repo);
            string canonicalDb = Path.Combine(canonicalRoot, ".miller", "symbols.db");
            var runner = new JulieExtractRunner(binary!);
            var report = runner.Scan(canonicalRoot, canonicalDb, force: true);
            Assert.NotEqual("failed", report.Status);
            Assert.True(report.SymbolsExtracted > 0);

            var buildSw = Stopwatch.StartNew();
            var index = RepositoryIndexLoader.Load(canonicalDb);
            buildSw.Stop();
            Console.WriteLine($"[local-performance] index+graph build: {buildSw.ElapsedMilliseconds}ms");

            var resolver = new SmartTargetResolver(index);

            // Sanity: the symbols and the dependency edges actually landed.
            Assert.NotEmpty(index.FindByName("Process"));
            Assert.NotEmpty(index.FindByName("OrderService"));
            string processId = index.FindByName("Process").Single().SymbolId;
            // Process is depended-on by the test method (and the Service-side chain) — reverse adjacency is populated.
            Assert.NotEmpty(index.Dependents(processId));

            var impactSw = Stopwatch.StartNew();
            string impactOut = ImpactTool.Run(index, resolver,
                target: "OrderService", changedPaths: null, diff: null, maxDepth: 2, limit: 100, json: false,
                out int impactedCount, out _);
            impactSw.Stop();

            Assert.Contains("ProcessWorks", impactOut);
            Assert.Contains("likely tests", impactOut, StringComparison.OrdinalIgnoreCase);
            Assert.True(impactedCount >= 1, $"expected a non-empty impact set; output:\n{impactOut}");
            Console.WriteLine($"[local-performance] impact: {impactSw.ElapsedMilliseconds}ms");

            // impact on the precise method also surfaces the test (reverse closure from Process).
            string impactProcess = ImpactTool.Run(index, resolver,
                target: "Process", changedPaths: null, diff: null, maxDepth: 2, limit: 100, json: false, out _, out _);
            Assert.Contains("ProcessWorks", impactProcess);

            string legacyContext = ContextPipelineTestDriver.Run(index, resolver,
                query: "order processing", tokenBudget: 4000, maxHops: 1,
                entrySymbols: null, failingTest: null, stackTrace: null, json: false, out _, out _);

            string actionableContext = ContextPipelineTestDriver.RunActionable(
                index,
                index.Graph,
                resolver,
                query: "order processing",
                tokenBudget: 4000,
                maxHops: 1,
                entrySymbols: ["OrderService"],
                editedFiles: null,
                failingTest: null,
                stackTrace: null,
                semanticSeeds: null,
                readBody: symbol => ExtractReader.ReadBody(canonicalDb, canonicalRoot, symbol),
                json: false,
                out int actionableCount,
                out _);

            Assert.True(actionableCount >= 1, $"expected a non-empty context bundle; output:\n{actionableContext}");
            Assert.Contains("OrderService", actionableContext);
            Assert.Contains("return _repo.Load(id);", actionableContext);
            Assert.Contains("evidence=sufficient", actionableContext);
            Assert.DoesNotContain("## next inspect", actionableContext);
            Assert.True(TokenEstimator.Count(actionableContext) <= 4000);

            string legacyFollowUp = InspectTool.Run(
                index,
                resolver,
                canonicalDb,
                canonicalRoot,
                target: "OrderService",
                depth: "overview",
                kind: null,
                scope: null,
                limit: 50,
                json: false,
                out _);
            Assert.True(
                TokenEstimator.Count(actionableContext) <
                TokenEstimator.Count(legacyContext) + TokenEstimator.Count(legacyFollowUp));

            const int ctxSamples = 21;
            var ctxRunsMs = new double[ctxSamples];
            for (int i = 0; i < ctxSamples; i++)
            {
                var ctxSw = Stopwatch.StartNew();
                _ = ContextPipelineTestDriver.RunActionable(
                    index,
                    index.Graph,
                    resolver,
                    query: "order processing",
                    tokenBudget: 4000,
                    maxHops: 1,
                    entrySymbols: ["OrderService"],
                    editedFiles: null,
                    failingTest: null,
                    stackTrace: null,
                    semanticSeeds: null,
                    readBody: symbol => ExtractReader.ReadBody(canonicalDb, canonicalRoot, symbol),
                    json: false,
                    out _,
                    out _);
                ctxSw.Stop();
                ctxRunsMs[i] = ctxSw.Elapsed.TotalMilliseconds;
            }
            Array.Sort(ctxRunsMs);
            double ctxP95Ms = ctxRunsMs[(int)Math.Ceiling(ctxSamples * 0.95) - 1];
            Console.WriteLine(
                $"[local-performance] actionable context p95 over {ctxSamples} runs: {ctxP95Ms:F1}ms " +
                $"(min {ctxRunsMs[0]:F1}ms, max {ctxRunsMs[ctxSamples - 1]:F1}ms)");

            string tiny = ContextPipelineTestDriver.RunActionable(
                index,
                index.Graph,
                resolver,
                query: "order processing",
                tokenBudget: 20,
                maxHops: 1,
                entrySymbols: ["OrderService"],
                editedFiles: null,
                failingTest: null,
                stackTrace: null,
                semanticSeeds: null,
                readBody: symbol => ExtractReader.ReadBody(canonicalDb, canonicalRoot, symbol),
                json: false,
                out int tinyCount,
                out _);
            Assert.True(TokenEstimator.Count(tiny) <= 20);
            Assert.True(tinyCount < actionableCount);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }
}
