using System.Diagnostics;
using Miller.Indexing;
using Miller.Server;
using Miller.Server.Resolution;
using Miller.Server.Tools;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// The M5 D11 end-to-end Scale proof: restore julie-server → scan a temp polyglot repo with a real dependency
/// chain (<c>OrderController → OrderService → OrderRepo</c>) plus an xUnit test that calls <c>OrderService</c> →
/// <see cref="RepositoryIndexLoader.Load"/> the extract (graph included) → drive the REAL
/// <see cref="ImpactTool.Run"/> / <see cref="ContextTool.Run"/> cores and assert:
/// <list type="bullet">
/// <item><c>impact</c> on <c>OrderService</c> returns the expected downstream set including a likely-test
///   (julie's cross-language <c>is_test</c> flag, partitioned out);</item>
/// <item><c>context</c> returns a non-empty, budget-bounded bundle of the cluster;</item>
/// <item>latency: <c>context</c> &lt; 100ms and <c>impact</c> well under 1s at default depths (julie's
///   blast_radius was 5s p95 — the founding adoption thesis), and the graph build stays within the rebuild budget.</item>
/// </list>
/// Depends on the pinned binary + a real extract, so it is <c>[Trait("Category","Scale")]</c> and EXCLUDED from
/// the default suite; it <see cref="Assert.Skip"/>s if <c>.tools/julie-server</c> is absent rather than failing.
/// </summary>
[Trait("Category", "Scale")]
public sealed class LiveContextImpactTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Miller.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repo root (Miller.slnx).");
    }

    private static string? LocateJulieServer()
    {
        string name = OperatingSystem.IsWindows() ? "julie-server.exe" : "julie-server";
        string candidate = Path.Combine(RepoRoot(), ".tools", name);
        return File.Exists(candidate) ? candidate : null;
    }

    [Fact]
    public void Live_ImpactAndContext_OverRealExtract_AreCorrectAndFast()
    {
        string? binary = LocateJulieServer();
        Assert.SkipWhen(binary is null,
            "julie-server not found in .tools/. Run scripts/restore-julie-server.sh to enable the Scale test.");

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

            // --- load the index + graph as one unit (the single production path) and TIME the build (D11). ---
            var buildSw = Stopwatch.StartNew();
            var index = RepositoryIndexLoader.Load(canonicalDb);
            buildSw.Stop();
            // The graph build rides the index build; for this tiny repo it is effectively instant. The 10s rebuild
            // budget (D11/D11-rebuild) is the ceiling — assert we are nowhere near it on a real (if small) extract.
            Assert.True(buildSw.ElapsedMilliseconds < 10_000,
                $"index+graph build took {buildSw.ElapsedMilliseconds}ms (budget 10s).");

            var resolver = new SmartTargetResolver(index);

            // Sanity: the symbols and the dependency edges actually landed.
            Assert.NotEmpty(index.FindByName("Process"));
            Assert.NotEmpty(index.FindByName("OrderService"));
            string processId = index.FindByName("Process").Single().SymbolId;
            // Process is depended-on by the test method (and the Service-side chain) — reverse adjacency is populated.
            Assert.NotEmpty(index.Dependents(processId));

            // === impact("OrderService") — the reverse closure incl. a likely test, and it is FAST. ===
            var impactSw = Stopwatch.StartNew();
            string impactOut = ImpactTool.Run(index, resolver,
                target: "OrderService", changedPaths: null, diff: null, maxDepth: 2, limit: 100, json: false,
                out int impactedCount, out _);
            impactSw.Stop();

            // The xUnit test is reached and partitioned into the likely-tests section.
            Assert.Contains("ProcessWorks", impactOut);
            Assert.Contains("likely tests", impactOut, StringComparison.OrdinalIgnoreCase);
            // SOMETHING downstream is impacted (the Controller-side caller and/or the Service chain).
            Assert.True(impactedCount >= 1, $"expected a non-empty impact set; output:\n{impactOut}");
            // Well under julie's 1.3s avg / 5s p95 — the adoption mandate.
            Assert.True(impactSw.ElapsedMilliseconds < 1_000,
                $"impact took {impactSw.ElapsedMilliseconds}ms (budget 1s).");

            // impact on the precise method also surfaces the test (reverse closure from Process).
            string impactProcess = ImpactTool.Run(index, resolver,
                target: "Process", changedPaths: null, diff: null, maxDepth: 2, limit: 100, json: false, out _, out _);
            Assert.Contains("ProcessWorks", impactProcess);

            // === context("order processing") — a non-empty, budget-bounded bundle, sub-100ms. ===
            // First call doubles as WARMUP (JIT + cold caches) and the non-empty/content correctness check; its
            // latency is deliberately NOT measured — a cold first sample is not representative of steady state.
            string ctxOut = ContextTool.Run(index, resolver,
                query: "order processing", tokenBudget: 4000, maxHops: 1,
                entrySymbols: null, failingTest: null, stackTrace: null, json: false, out int ctxCount, out _);

            Assert.True(ctxCount >= 1, $"expected a non-empty context bundle; output:\n{ctxOut}");
            Assert.Contains("OrderService", ctxOut);

            // The sub-100ms target (julie's get_context was 439ms avg / 1.2s p95). Assert on the MEDIAN of repeated
            // steady-state runs, never a single shot: one GC pause or scheduler preemption (common when the Scale
            // suite runs under CPU load) blows an absolute single-sample wall-clock budget, while the median still
            // proves the in-memory traversal meets the "fast or vestigial" mandate. The generous 100ms ceiling vs.
            // the real sub-millisecond cost leaves ample headroom above the median.
            const int ctxSamples = 21;
            var ctxRunsMs = new double[ctxSamples];
            for (int i = 0; i < ctxSamples; i++)
            {
                var ctxSw = Stopwatch.StartNew();
                _ = ContextTool.Run(index, resolver,
                    query: "order processing", tokenBudget: 4000, maxHops: 1,
                    entrySymbols: null, failingTest: null, stackTrace: null, json: false, out _, out _);
                ctxSw.Stop();
                ctxRunsMs[i] = ctxSw.Elapsed.TotalMilliseconds;
            }
            Array.Sort(ctxRunsMs);
            double ctxMedianMs = ctxRunsMs[ctxSamples / 2];
            Assert.True(ctxMedianMs < 100,
                $"context median {ctxMedianMs:F1}ms over {ctxSamples} runs exceeded the 100ms budget " +
                $"(min {ctxRunsMs[0]:F1}ms, max {ctxRunsMs[ctxSamples - 1]:F1}ms).");

            // A tiny budget truncates the bundle (the budget, not a count, bounds it).
            string tiny = ContextTool.Run(index, resolver,
                query: "order processing", tokenBudget: 20, maxHops: 1,
                entrySymbols: null, failingTest: null, stackTrace: null, json: false, out int tinyCount, out _);
            Assert.True(tinyCount < ctxCount,
                $"a tiny budget should truncate the bundle (tiny={tinyCount}, full={ctxCount}).");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }
}
