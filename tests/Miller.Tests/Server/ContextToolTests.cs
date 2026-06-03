using System.Text.Json;
using Miller.Core.Graph;
using Miller.Indexing;
using Miller.Server.Resolution;
using Miller.Server.Tools;
using Miller.Tests;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the <c>context</c> tool (M5 D6/D1/D8/D10) against an in-memory synth index (symbols + edges, no SQLite —
/// the Server <c>Run</c> core is DB-free for context). Exercises <see cref="ContextTool.Run"/> directly: the
/// seed union (BM25 search ∪ resolved entry_symbols ∪ identifier tokens parsed from failing_test / stack_trace),
/// both-direction neighbour expansion to max_hops, the token-budget pack (a tiny budget truncates the bundle),
/// both render formats, the max_hops clamp, and empty-query handling.
/// </summary>
public sealed class ContextToolTests
{
    private const string ControllerId = "00000000000000000000000000000001";
    private const string ServiceId = "00000000000000000000000000000002";
    private const string RepoId = "00000000000000000000000000000003";
    private const string UnrelatedId = "00000000000000000000000000000004";
    private const string TestId = "00000000000000000000000000000005";

    // A dependency cluster around "order processing":
    //   OrderController depends on OrderService   (Controller → Service)
    //   OrderService    depends on OrderRepo       (Service → Repo)
    //   OrderServiceTests (a test) depends on OrderService (Tests → Service)
    //   UnrelatedHelper is in its own file, no edges.
    private static (MillerRepositoryIndex index, SmartTargetResolver resolver) BuildFixture()
    {
        var symbols = new List<IndexedSymbol>
        {
            new(0, ControllerId, "OrderController", "class OrderController", "class", "csharp",
                "web/OrderController.cs", 1, 40, null, false),
            new(1, ServiceId, "OrderService", "class OrderService", "class", "csharp",
                "src/OrderService.cs", 1, 60, null, false),
            new(2, RepoId, "OrderRepo", "class OrderRepo", "class", "csharp",
                "src/OrderRepo.cs", 1, 30, null, false),
            new(3, UnrelatedId, "UnrelatedHelper", "class UnrelatedHelper", "class", "csharp",
                "util/Helper.cs", 1, 10, null, false),
            new(4, TestId, "OrderServiceTests", "class OrderServiceTests", "class", "csharp",
                "tests/OrderServiceTests.cs", 1, 20, null, IsTest: true),
        };
        var edges = new[]
        {
            new GraphEdge(ControllerId, ServiceId, "uses"),
            new GraphEdge(ServiceId, RepoId, "uses"),
            new GraphEdge(TestId, ServiceId, "uses"),
        };
        var index = MillerRepositoryIndex.Build(symbols, edges);
        return (index, new SmartTargetResolver(index));
    }

    private static MillerRepositoryIndex EmptyIndex() =>
        MillerRepositoryIndex.Build(Array.Empty<IndexedSymbol>(), Array.Empty<GraphEdge>());

    // ---- seeds + expansion ----

    [Fact]
    public void Run_SearchSeed_ExpandsBothDirections_IncludesTheCluster()
    {
        var (index, resolver) = BuildFixture();

        // "OrderService" search-seeds OrderService; both-direction expansion at 1 hop reaches its dependency
        // (OrderRepo), its dependents (OrderController), and the test (OrderServiceTests).
        string output = ContextTool.Run(index, resolver,
            query: "OrderService", tokenBudget: 4000, maxHops: 1,
            entrySymbols: null, failingTest: null, stackTrace: null, json: false, out int count, out _);

        Assert.Contains("OrderService", output);
        Assert.Contains("OrderController", output);
        Assert.Contains("OrderRepo", output);
        Assert.Contains("OrderServiceTests", output);
        Assert.DoesNotContain("UnrelatedHelper", output); // unconnected, not search-matched
        Assert.True(count >= 4);
    }

    [Fact]
    public void Run_MaxHopsZero_ReturnsOnlyTheSeeds_NoExpansion()
    {
        var (index, resolver) = BuildFixture();

        // Seed exactly OrderRepo (via entry_symbols, with a non-matching query) so the assertion isolates the
        // zero-hop behaviour: no neighbour expansion, only the seed itself. (A lexical "OrderService" query would
        // BM25-match every "Order*" symbol — that is correct search behaviour but not what this test pins.)
        string output = ContextTool.Run(index, resolver,
            query: "zzz no lexical match zzz", tokenBudget: 4000, maxHops: 0,
            entrySymbols: new[] { "OrderRepo" }, failingTest: null, stackTrace: null, json: false, out _, out _);

        Assert.Contains("OrderRepo", output);
        // Zero-hop means no neighbour expansion: OrderService (a dependent of OrderRepo) is NOT pulled in.
        Assert.DoesNotContain("OrderService", output);
        Assert.DoesNotContain("OrderController", output);
    }

    [Fact]
    public void Run_EntrySymbols_AreFoldedIntoSeeds()
    {
        var (index, resolver) = BuildFixture();

        // A query that matches nothing lexical, but OrderRepo passed as an entry symbol seeds the cluster.
        string output = ContextTool.Run(index, resolver,
            query: "zzz no lexical match zzz", tokenBudget: 4000, maxHops: 1,
            entrySymbols: new[] { "OrderRepo" }, failingTest: null, stackTrace: null, json: false, out int count, out _);

        Assert.Contains("OrderRepo", output);
        // OrderRepo's dependent (OrderService) is reached at 1 hop.
        Assert.Contains("OrderService", output);
        Assert.True(count >= 2);
    }

    [Fact]
    public void Run_FailingTest_SymbolTokensFoldedIntoSeeds()
    {
        var (index, resolver) = BuildFixture();

        // A failing-test hint mentioning OrderController as an identifier token → seeds it even with a vague query.
        string output = ContextTool.Run(index, resolver,
            query: "something broke", tokenBudget: 4000, maxHops: 1,
            entrySymbols: null, failingTest: "OrderController.PlaceOrder threw", stackTrace: null,
            json: false, out _, out _);

        Assert.Contains("OrderController", output);
        // Its dependency OrderService is reached at 1 hop.
        Assert.Contains("OrderService", output);
    }

    [Fact]
    public void Run_StackTrace_SymbolTokensFoldedIntoSeeds()
    {
        var (index, resolver) = BuildFixture();

        string output = ContextTool.Run(index, resolver,
            query: "npe", tokenBudget: 4000, maxHops: 0,
            entrySymbols: null, failingTest: null,
            stackTrace: "at OrderRepo.Load(int id) in OrderRepo.cs:line 12", json: false, out _, out _);

        Assert.Contains("OrderRepo", output);
    }

    // ---- token budget ----

    [Fact]
    public void Run_TinyBudget_TruncatesTheBundle()
    {
        var (index, resolver) = BuildFixture();

        // A budget large enough for one or two lines but not the whole cluster. The pack honours priority order
        // (the search seed first), so the bundle is non-empty but strictly smaller than the unbounded one.
        string full = ContextTool.Run(index, resolver,
            query: "OrderService", tokenBudget: 100000, maxHops: 1,
            entrySymbols: null, failingTest: null, stackTrace: null, json: false, out int fullCount, out _);

        // Each candidate render line costs ~16–20 tokens (chars/4). A budget of 25 admits the top-priority seed
        // (one line) but not the whole cluster — a deterministic truncation independent of the exact estimate.
        string tiny = ContextTool.Run(index, resolver,
            query: "OrderService", tokenBudget: 25, maxHops: 1,
            entrySymbols: null, failingTest: null, stackTrace: null, json: false, out int tinyCount, out _);

        Assert.True(tinyCount >= 1, "a tiny but non-zero budget should still admit the highest-priority seed.");
        Assert.True(tinyCount < fullCount, "a tiny budget must truncate the bundle below the unbounded count.");
        Assert.Contains("OrderService", tiny); // the top-priority seed (exact-name BM25 boost) survives
    }

    [Fact]
    public void Run_ZeroBudget_ReturnsEmptyBundle()
    {
        var (index, resolver) = BuildFixture();

        string output = ContextTool.Run(index, resolver,
            query: "OrderService", tokenBudget: 0, maxHops: 1,
            entrySymbols: null, failingTest: null, stackTrace: null, json: false, out int count, out _);

        Assert.Equal(0, count);
    }

    // ---- max_hops clamp ----

    [Fact]
    public void Run_MaxHopsAboveTwo_IsClampedToTwo()
    {
        var (index, resolver) = BuildFixture();

        // maxHops 99 clamps to 2 — but it must still WORK (not throw / not return empty). The 2-hop closure from
        // OrderController reaches OrderService (1) then OrderRepo + OrderServiceTests (2).
        string output = ContextTool.Run(index, resolver,
            query: "OrderController", tokenBudget: 100000, maxHops: 99,
            entrySymbols: null, failingTest: null, stackTrace: null, json: false, out _, out _);

        Assert.Contains("OrderController", output);
        Assert.Contains("OrderService", output);
        Assert.Contains("OrderRepo", output);     // 2 hops away
        Assert.Contains("OrderServiceTests", output);
    }

    // ---- empty query ----

    [Fact]
    public void Run_EmptyQuery_NoOtherSeeds_ReturnsNote()
    {
        var (index, resolver) = BuildFixture();

        string output = ContextTool.Run(index, resolver,
            query: "   ", tokenBudget: 4000, maxHops: 1,
            entrySymbols: null, failingTest: null, stackTrace: null, json: false, out int count, out _);

        Assert.Equal(0, count);
        // No throw; a clear "nothing to anchor on" note rather than an exception.
        Assert.Contains("no", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Run_EmptyQuery_ButEntrySymbolsPresent_StillSeeds()
    {
        var (index, resolver) = BuildFixture();

        // An empty query is fine when other seeds (entry_symbols) are present — the query is not the only anchor.
        string output = ContextTool.Run(index, resolver,
            query: "", tokenBudget: 4000, maxHops: 0,
            entrySymbols: new[] { "OrderService" }, failingTest: null, stackTrace: null, json: false, out int count, out _);

        Assert.Contains("OrderService", output);
        Assert.True(count >= 1);
    }

    // ---- D10 telemetry work-proxy (candidates examined) ----

    [Fact]
    public void Run_SurfacesCandidatesExamined_CoveringSeedsPlusReached()
    {
        var (index, resolver) = BuildFixture();

        // entry_symbols seeds OrderService (1 seed); both-direction 1-hop expansion reaches OrderController,
        // OrderRepo, OrderServiceTests (3 reached). candidatesExamined is the D10 bytes_examined work proxy: the
        // total candidate set the packer considered = seeds + reached = 4. Pins finding 4 (was silently 0). A
        // large budget keeps every candidate so the selected count matches the examined count here.
        ContextTool.Run(index, resolver,
            query: "zzz no lexical match zzz", tokenBudget: 100000, maxHops: 1,
            entrySymbols: new[] { "OrderService" }, failingTest: null, stackTrace: null, json: false,
            out int selectedCount, out int candidatesExamined);

        Assert.Equal(4, candidatesExamined);          // OrderService + Controller + Repo + Tests
        Assert.Equal(candidatesExamined, selectedCount); // unbounded budget keeps them all
    }

    [Fact]
    public void Run_CandidatesExamined_CountsTheWork_EvenWhenTheBudgetTruncatesTheBundle()
    {
        var (index, resolver) = BuildFixture();

        // A tiny budget truncates the SELECTED bundle, but the work proxy reflects the candidates considered, not
        // the smaller selected set — so candidatesExamined > selectedCount under a truncating budget.
        ContextTool.Run(index, resolver,
            query: "zzz no lexical match zzz", tokenBudget: 25, maxHops: 1,
            entrySymbols: new[] { "OrderService" }, failingTest: null, stackTrace: null, json: false,
            out int selectedCount, out int candidatesExamined);

        Assert.Equal(4, candidatesExamined);
        Assert.True(selectedCount < candidatesExamined,
            $"a tiny budget should truncate the bundle below the examined count (selected={selectedCount}, examined={candidatesExamined}).");
    }

    [Fact]
    public void Run_CandidatesExamined_IsZero_WhenNoSeeds()
    {
        var (index, resolver) = BuildFixture();

        ContextTool.Run(index, resolver,
            query: "   ", tokenBudget: 4000, maxHops: 1,
            entrySymbols: null, failingTest: null, stackTrace: null, json: false,
            out _, out int candidatesExamined);

        Assert.Equal(0, candidatesExamined); // no seeds → no candidates examined
    }

    // ---- formats ----

    [Fact]
    public void Run_Json_IsWellFormedWithProvenance()
    {
        var (index, resolver) = BuildFixture();

        string output = ContextTool.Run(index, resolver,
            query: "OrderService", tokenBudget: 100000, maxHops: 1,
            entrySymbols: null, failingTest: null, stackTrace: null, json: true, out _, out _);

        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        var bundle = root.GetProperty("bundle");
        Assert.Equal(JsonValueKind.Array, bundle.ValueKind);
        Assert.True(bundle.GetArrayLength() >= 4);

        var first = bundle[0];
        Assert.True(first.TryGetProperty("name", out _));
        Assert.True(first.TryGetProperty("kind", out _));
        Assert.True(first.TryGetProperty("file", out _));
        Assert.True(first.TryGetProperty("line", out _));
        Assert.True(first.TryGetProperty("hop", out _));
        // The search seed is hop 0 and sorts first in priority order.
        Assert.Equal(0, first.GetProperty("hop").GetInt32());
    }

    [Fact]
    public void Run_Compact_CarriesSignatureAndProvenance()
    {
        var (index, resolver) = BuildFixture();

        string output = ContextTool.Run(index, resolver,
            query: "OrderService", tokenBudget: 100000, maxHops: 1,
            entrySymbols: null, failingTest: null, stackTrace: null, json: false, out _, out _);

        // Provenance: name, file:line, hop annotation, and the signature.
        Assert.Contains("OrderService", output);
        Assert.Contains("src/OrderService.cs:1", output);
        Assert.Contains("class OrderService", output);
        Assert.Contains("hop", output, StringComparison.OrdinalIgnoreCase);
    }

    // ---- routed wrapper / ctor shape ----

    [Fact]
    public void Context_ExplicitWorkspaceId_DefaultsEnsureFreshTrue_AndRoutesToTargetIndex()
    {
        var currentIndex = EmptyIndex();
        var (targetIndex, _) = BuildFixture();
        string currentRoot = Path.Combine(Path.GetTempPath(), "miller-current-" + Guid.NewGuid().ToString("N"));
        string targetRoot = Path.Combine(Path.GetTempPath(), "miller-target-" + Guid.NewGuid().ToString("N"));
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(currentIndex, "current.db", "current-ws", currentRoot),
            ("target-ws", ReadToolRoutingTestSupport.ContextFor(targetIndex, "target.db", "target-ws", targetRoot)));
        var tool = new ContextTool(provider);

        string output = tool.Context("OrderService", workspace_id: "target-ws");

        Assert.Equal("target-ws", provider.LastWorkspaceId);
        Assert.True(provider.LastEnsureFresh);
        Assert.StartsWith("workspace: target-ws\n", output);
        Assert.DoesNotContain(targetRoot, output);
        Assert.Contains("OrderService", output);
    }

    [Fact]
    public void Ctor_RequiresWorkspaceIndexProvider()
    {
        var (index, _) = BuildFixture();
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(index, "current.db", "current-ws", "/current"));

        var tool = new ContextTool(provider);
        Assert.NotNull(tool);

        Assert.Throws<ArgumentNullException>(() => new ContextTool(null!));
    }
}
