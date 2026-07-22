using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Core.Graph;
using Miller.Core.Search;
using Miller.Indexing;
using Miller.Server.Resolution;
using Miller.Server.Telemetry;
using Miller.Server.Tools;
using Miller.Tests;
using Miller.Tests.Indexing;
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

    private static TextContentSearchHit SourceHit(
        string path,
        int line,
        string snippet,
        string sourceId = "source-a",
        string chunkId = "chunk-a",
        string? containingSymbolId = null,
        string? containingSymbolName = null) =>
        new(
            sourceId,
            chunkId,
            TextContentKind.WorkspaceSource,
            path,
            Url: null,
            DisplayPath: path,
            Language: "csharp",
            Score: 0.0,
            Line: line,
            LineStart: line,
            LineEnd: line,
            ByteStart: 0,
            ByteEnd: snippet.Length,
            Snippet: snippet,
            SourceBytes: snippet.Length,
            ContainingSymbolId: containingSymbolId,
            ContainingSymbolName: containingSymbolName);

    private static void WriteContentChunk(
        string contentDbPath,
        string chunkId,
        string path,
        string rawText,
        string containingSymbolId,
        string containingSymbolName)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = contentDbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        connection.Open();
        using (var schema = connection.CreateCommand())
        {
            schema.CommandText = ContentCorpusSchema.SchemaDdl;
            schema.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO content_chunks(
                chunk_id, source_id, content_kind, path, url, display_path, language,
                line_start, line_end, byte_start, byte_end, raw_text, doc_len, is_test,
                source_bytes, containing_symbol_id, containing_symbol_name)
            VALUES(
                $chunk_id, $source_id, $content_kind, $path, NULL, $path, 'csharp',
                2, 4, 20, 100, $raw_text, 12, 0,
                $source_bytes, $containing_symbol_id, $containing_symbol_name);
            """;
        command.Parameters.AddWithValue("$chunk_id", chunkId);
        command.Parameters.AddWithValue("$source_id", "source-" + path);
        command.Parameters.AddWithValue("$content_kind", TextContentKind.WorkspaceSource);
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$raw_text", rawText);
        command.Parameters.AddWithValue("$source_bytes", rawText.Length);
        command.Parameters.AddWithValue("$containing_symbol_id", containingSymbolId);
        command.Parameters.AddWithValue("$containing_symbol_name", containingSymbolName);
        command.ExecuteNonQuery();
    }

    private static (MillerRepositoryIndex index, SmartTargetResolver resolver) BuildSharedFileFixture()
    {
        var symbols = new List<IndexedSymbol>
        {
            new(0, "shared-alpha", "Alpha", "method Alpha()", "method", "csharp",
                "src/Shared.cs", 10, 10, null, false),
            new(1, "shared-beta", "Beta", "method Beta()", "method", "csharp",
                "src/Shared.cs", 20, 20, null, false),
            new(2, "other-gamma", "Gamma", "class Gamma", "class", "csharp",
                "src/Gamma.cs", 30, 30, null, false),
        };
        var index = MillerRepositoryIndex.Build(symbols, Array.Empty<GraphEdge>());
        return (index, new SmartTargetResolver(index));
    }

    // A wide, shallow cluster: one seed (Hub) with 15 direct neighbours, each in its own file. Used to exercise the
    // neighbour render cap (MaxNeighbourCandidates) and the omission note.
    private static (MillerRepositoryIndex index, SmartTargetResolver resolver) BuildWideFixture()
    {
        const string hubId = "00000000000000000000000000000001";
        var symbols = new List<IndexedSymbol>
        {
            new(0, hubId, "Hub", "class Hub", "class", "csharp", "src/Hub.cs", 1, 10, null, false),
        };
        var edges = new List<GraphEdge>();
        for (int i = 1; i <= 15; i++)
        {
            string id = string.Format("000000000000000000000000000000{0:X2}", i + 1);
            symbols.Add(new IndexedSymbol(i, id, "N" + i, "class N" + i, "class", "csharp",
                "src/N" + i + ".cs", 1, 10, null, false));
            edges.Add(new GraphEdge(hubId, id, "uses"));
        }
        var index = MillerRepositoryIndex.Build(symbols, edges);
        return (index, new SmartTargetResolver(index));
    }

    private static (MillerRepositoryIndex index, SmartTargetResolver resolver) BuildRenderBudgetFixture()
    {
        const string rootId = "00000000000000000000000000000001";
        string signature = "method BudgetRoot(" + new string('x', 5000) + ")";
        var symbols = new List<IndexedSymbol>
        {
            new(0, rootId, "BudgetRoot", signature, "method", "csharp", "src/BudgetRoot.cs", 1, 10, null, false),
        };
        var edges = new List<GraphEdge>();
        for (int i = 1; i <= 8; i++)
        {
            string id = (i + 1).ToString("D32");
            symbols.Add(new IndexedSymbol(i, id, "BudgetNeighbour" + i, signature, "method", "csharp",
                "src/BudgetNeighbour" + i + ".cs", i + 1, i + 1, null, false));
            edges.Add(new GraphEdge(rootId, id, "uses"));
        }
        var index = MillerRepositoryIndex.Build(symbols, edges);
        return (index, new SmartTargetResolver(index));
    }

    private static void AssertRenderedCount(string output, bool json, int selectedCount)
    {
        if (json)
        {
            using var document = JsonDocument.Parse(output);
            Assert.Equal(selectedCount, document.RootElement.GetProperty("bundle").GetArrayLength());
        }
        else if (selectedCount == 0)
        {
            Assert.Equal("Bundle empty — raise token_budget.", output);
        }
        else
        {
            Assert.Contains($"# context bundle ({selectedCount})", output, StringComparison.Ordinal);
        }
    }

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

        string full = ContextTool.Run(index, resolver,
            query: "zzz no lexical match zzz", tokenBudget: 100000, maxHops: 1,
            entrySymbols: new[] { "OrderService" }, failingTest: null, stackTrace: null, json: false, out int fullCount, out _);

        string seedOnly = ContextTool.Run(index, resolver,
            query: "zzz no lexical match zzz", tokenBudget: 100000, maxHops: 0,
            entrySymbols: new[] { "OrderService" }, failingTest: null, stackTrace: null, json: false, out int seedCount, out _);
        int tinyBudget = checked((int)TokenEstimator.Count(seedOnly));

        string tiny = ContextTool.Run(index, resolver,
            query: "zzz no lexical match zzz", tokenBudget: tinyBudget, maxHops: 1,
            entrySymbols: new[] { "OrderService" }, failingTest: null, stackTrace: null, json: false, out int tinyCount, out _);

        Assert.Equal(1, seedCount);
        Assert.Equal(seedOnly, tiny);
        Assert.Equal(seedCount, tinyCount);
        Assert.True(tinyCount < fullCount);
        Assert.True(TokenEstimator.Count(tiny) <= tinyBudget);
        Assert.Contains("OrderService", tiny, StringComparison.Ordinal);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Run_RenderedOutputFitsPositiveTokenBudget(bool json)
    {
        var (index, resolver) = BuildRenderBudgetFixture();

        foreach (int tokenBudget in new[] { 16, 32, 64, 128, 256, 512 })
        {
            string output = ContextTool.Run(index, resolver,
                query: "BudgetRoot", tokenBudget, maxHops: 1,
                entrySymbols: null, failingTest: null, stackTrace: null, json, out int selectedCount, out _);

            Assert.True(TokenEstimator.Count(output) <= tokenBudget);
            AssertRenderedCount(output, json, selectedCount);
            if (selectedCount > 0)
                Assert.Contains("BudgetRoot", output, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Run_PositiveBudgetBelowEmptyEnvelope_ReturnsCanonicalEmptyBundle(
        bool referenceAware,
        bool json)
    {
        var (index, resolver) = BuildFixture();

        string output;
        int selectedCount;
        if (referenceAware)
        {
            output = ContextTool.RunReferenceAware(
                index, index.Graph, resolver,
                query: "zzz no lexical match zzz", tokenBudget: 1, maxHops: 0,
                entrySymbols: new[] { "OrderService" }, failingTest: null, stackTrace: null,
                referenceDepth: 1, excludeTests: false, json,
                readReferences: _ => new[]
                {
                    new SymbolRef("OrderService", "type_usage", "web/OrderController.cs", 12, ControllerId),
                },
                readCallees: _ => Array.Empty<SymbolRef>(),
                readContentChunks: (_, _) => Array.Empty<TextContentSearchHit>(),
                out selectedCount, out _);
        }
        else
        {
            output = ContextTool.Run(index, resolver,
                query: "OrderService", tokenBudget: 1, maxHops: 0,
                entrySymbols: null, failingTest: null, stackTrace: null, json,
                out selectedCount, out _);
        }

        Assert.Equal(0, selectedCount);
        Assert.Equal(json ? "{\"bundle\":[]}" : "Bundle empty — raise token_budget.", output);
        Assert.True(TokenEstimator.Count(output) > 1);
    }

    [Fact]
    public void Run_Json_PreservesFullSignatureWhenItFitsAndBoundsItWhenNeeded()
    {
        var (index, resolver) = BuildRenderBudgetFixture();

        string full = ContextTool.Run(index, resolver,
            query: "BudgetRoot", tokenBudget: 100000, maxHops: 0,
            entrySymbols: null, failingTest: null, stackTrace: null, json: true, out _, out _);
        string bounded = ContextTool.Run(index, resolver,
            query: "BudgetRoot", tokenBudget: 512, maxHops: 0,
            entrySymbols: null, failingTest: null, stackTrace: null, json: true, out int boundedCount, out _);

        using var fullDocument = JsonDocument.Parse(full);
        using var boundedDocument = JsonDocument.Parse(bounded);
        Assert.Equal(5019, fullDocument.RootElement.GetProperty("bundle")[0].GetProperty("signature").GetString()!.Length);
        Assert.Equal(ToolRenderLimits.SignatureMaxLength,
            boundedDocument.RootElement.GetProperty("bundle")[0].GetProperty("signature").GetString()!.Length);
        Assert.Equal(boundedCount, boundedDocument.RootElement.GetProperty("bundle").GetArrayLength());
        Assert.True(TokenEstimator.Count(bounded) <= 512);
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
            query: "zzz no lexical match zzz", tokenBudget: 100000, maxHops: 1,
            entrySymbols: new[] { "OrderService" }, failingTest: null, stackTrace: null, json: false, out _, out _);

        // Opinionated render: hop-0 seeds lead with a "seed" reason and full provenance on one line; only graph
        // neighbours (hop >= 1) carry a hop label. The JSON shape still carries hop for every candidate.
        Assert.Contains("## seeds", output);
        Assert.Contains("OrderService  class  src/OrderService.cs:1  seed  class OrderService", output);
        Assert.Contains("## neighbours", output);
        Assert.Contains("hop=1", output);
        Assert.Contains("class OrderService", output);
        Assert.DoesNotContain("hop=0", output);
    }

    [Fact]
    public void Run_Compact_RendersSeedsFirstWithInspectFooter()
    {
        var (index, resolver) = BuildSharedFileFixture();

        string output = ContextTool.Run(index, resolver,
            query: "", tokenBudget: 100000, maxHops: 0,
            entrySymbols: new[] { "Alpha", "Beta", "Gamma" }, failingTest: null, stackTrace: null,
            json: false, out int count, out _);

        Assert.Equal(3, count);
        // maxHops=0 → every candidate is a seed; no neighbours section. Seeds are listed first, each on one line
        // with the "seed" reason, followed by copyable overview-first inspect calls for the top 3 seeds.
        Assert.Equal(
            "# context bundle (3)\n" +
            "## seeds\n" +
            "Alpha  method  src/Shared.cs:10  seed  method Alpha()\n" +
            "Beta  method  src/Shared.cs:20  seed  method Beta()\n" +
            "Gamma  class  src/Gamma.cs:30  seed  class Gamma\n" +
            "## next inspect\n" +
            "inspect(target=\"Alpha\", scope=\"src/Shared.cs\", depth=\"overview\")\n" +
            "inspect(target=\"Beta\", scope=\"src/Shared.cs\", depth=\"overview\")\n" +
            "inspect(target=\"Gamma\", scope=\"src/Gamma.cs\", depth=\"overview\")",
            output);
    }

    [Fact]
    public void Run_Compact_GroupsNeighboursByFileAfterSeeds()
    {
        var (index, resolver) = BuildFixture();

        string output = ContextTool.Run(index, resolver,
            query: "zzz no lexical match zzz", tokenBudget: 100000, maxHops: 1,
            entrySymbols: new[] { "OrderService" }, failingTest: null, stackTrace: null, json: false, out _, out _);

        // Seeds section leads with the anchor; neighbours follow, grouped by file in reach order (hop asc, id asc).
        Assert.Contains("## seeds", output);
        Assert.Contains("OrderService  class  src/OrderService.cs:1  seed  class OrderService", output);
        Assert.Contains("## neighbours", output);
        Assert.Contains("web/OrderController.cs:\n  :1 OrderController class hop=1", output);
        Assert.Contains("src/OrderRepo.cs:\n  :1 OrderRepo class hop=1", output);
        Assert.Contains("tests/OrderServiceTests.cs:\n  :1 OrderServiceTests class hop=1", output);
        // Footer points at the single seed with a copyable overview-first inspect call.
        Assert.Contains("## next inspect\ninspect(target=\"OrderService\", scope=\"src/OrderService.cs\", depth=\"overview\")", output);
    }

    [Fact]
    public void Run_Compact_CapsNeighboursAndNotesOmission()
    {
        var (index, resolver) = BuildWideFixture();

        string output = ContextTool.Run(index, resolver,
            query: "Hub", tokenBudget: 100000, maxHops: 1,
            entrySymbols: null, failingTest: null, stackTrace: null, json: false, out _, out _);

        // 1 seed (Hub) + 15 hop-1 neighbours; the neighbour section is capped at 12 with an omission note.
        Assert.Contains("## seeds", output);
        Assert.Contains("Hub  class  src/Hub.cs:1  seed  class Hub", output);
        Assert.Contains("... 3 more neighbours omitted — inspect a seed for the full graph.", output);
        Assert.Equal(12, output.Split("hop=1").Length - 1);
        Assert.Contains("## next inspect\ninspect(target=\"Hub\", scope=\"src/Hub.cs\", depth=\"overview\")", output);
    }

    // ---- neighbour relevance ranking (pre-release audit finding) ----

    // A relevance-ranking fixture reproducing the audit failure: one anchor seed with several equal-hop
    // neighbours where the UNRELATED neighbour has the smallest symbol id. Id-order alone (the old behaviour)
    // surfaced the unrelated neighbour first; relevance ranking must put the same-file / name-overlap neighbours
    // ahead of it, while hop still dominates score.
    //   Anchor  Order        (src/Orders/Order.cs, id ..0A)
    //     → Helper      (util/Helper.cs,      id ..01)  unrelated                       score 0
    //     → OrderRepo   (src/Orders/Order.cs, id ..02)  same file + name "Order": +2+1+1 = 4
    //     → OrderQueue  (src/Queue/Queue.cs,  id ..03)  name "Order" only:          +2   = 2
    //     Helper → OrderDeep (util/Deep.cs,   id ..04)  name "Order" but hop 2:     +2   = 2 (hop 2)
    private static (MillerRepositoryIndex index, SmartTargetResolver resolver) BuildRelevanceFixture()
    {
        const string anchorId = "0000000000000000000000000000000A";
        const string helperId = "00000000000000000000000000000001";
        const string repoId = "00000000000000000000000000000002";
        const string queueId = "00000000000000000000000000000003";
        const string deepId = "00000000000000000000000000000004";
        var symbols = new List<IndexedSymbol>
        {
            new(0, anchorId, "Order", "class Order", "class", "csharp", "src/Orders/Order.cs", 1, 50, null, false),
            new(1, helperId, "Helper", "class Helper", "class", "csharp", "util/Helper.cs", 1, 10, null, false),
            new(2, repoId, "OrderRepo", "class OrderRepo", "class", "csharp", "src/Orders/Order.cs", 60, 90, null, false),
            new(3, queueId, "OrderQueue", "class OrderQueue", "class", "csharp", "src/Queue/Queue.cs", 1, 20, null, false),
            new(4, deepId, "OrderDeep", "class OrderDeep", "class", "csharp", "util/Deep.cs", 1, 15, null, false),
        };
        var edges = new[]
        {
            new GraphEdge(anchorId, helperId, "uses"),
            new GraphEdge(anchorId, repoId, "uses"),
            new GraphEdge(anchorId, queueId, "uses"),
            new GraphEdge(helperId, deepId, "uses"),
        };
        var index = MillerRepositoryIndex.Build(symbols, edges);
        return (index, new SmartTargetResolver(index));
    }

    private static List<string> BundleNames(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("bundle").EnumerateArray()
            .Select(item => item.GetProperty("name").GetString()!)
            .ToList();
    }

    [Fact]
    public void Run_Neighbours_RelevanceBeatsLowerIdUnrelated_AtEqualHop()
    {
        var (index, resolver) = BuildRelevanceFixture();

        // Anchor the bundle on Order (empty query → no BM25 seeds; the seed name "Order" is the scoring token).
        // All three neighbours sit at hop 1. Under the old (hop, id) order the unrelated Helper (smallest id ..01)
        // led; relevance ranking must surface the same-file OrderRepo and the name-overlap OrderQueue ahead of it.
        string output = ContextTool.Run(index, resolver,
            query: "", tokenBudget: 100000, maxHops: 1,
            entrySymbols: new[] { "Order" }, failingTest: null, stackTrace: null, json: true, out _, out _);

        var names = BundleNames(output);
        Assert.Equal("Order", names[0]); // hop-0 seed leads
        // Relevance order for the hop-1 neighbours: OrderRepo (4) > OrderQueue (2) > Helper (0). Helper has the
        // SMALLEST id, so id-order alone would have put it first — the exact case the audit flagged.
        Assert.Equal(new[] { "OrderRepo", "OrderQueue", "Helper" }, names.Skip(1).ToArray());
        Assert.True(names.IndexOf("OrderRepo") < names.IndexOf("Helper"));
        Assert.True(names.IndexOf("OrderQueue") < names.IndexOf("Helper"));
    }

    [Fact]
    public void Run_Neighbours_HopStillDominatesRelevance()
    {
        var (index, resolver) = BuildRelevanceFixture();

        // OrderDeep (hop 2) shares the "Order" token (score 2) yet must still sort AFTER the unrelated hop-1
        // Helper (score 0): hop dominates relevance, so a hop-2 relevant neighbour never leapfrogs a hop-1 one.
        string output = ContextTool.Run(index, resolver,
            query: "", tokenBudget: 100000, maxHops: 2,
            entrySymbols: new[] { "Order" }, failingTest: null, stackTrace: null, json: true, out _, out _);

        var names = BundleNames(output);
        Assert.Equal(new[] { "Order", "OrderRepo", "OrderQueue", "Helper", "OrderDeep" }, names.ToArray());
        Assert.True(names.IndexOf("Helper") < names.IndexOf("OrderDeep"),
            "a hop-1 neighbour must precede a hop-2 neighbour regardless of relevance score.");
    }

    [Fact]
    public void Run_Json_KeepsPerCandidateFileFields()
    {
        var (index, resolver) = BuildSharedFileFixture();

        string output = ContextTool.Run(index, resolver,
            query: "", tokenBudget: 100000, maxHops: 0,
            entrySymbols: new[] { "Alpha", "Beta" }, failingTest: null, stackTrace: null,
            json: true, out int count, out _);

        Assert.Equal(2, count);
        using var doc = JsonDocument.Parse(output);
        var bundle = doc.RootElement.GetProperty("bundle");
        Assert.Equal("src/Shared.cs", bundle[0].GetProperty("file").GetString());
        Assert.Equal("src/Shared.cs", bundle[1].GetProperty("file").GetString());
    }

    // ---- reference-aware usage mode ----

    [Fact]
    public void RunReferenceAware_Json_LabelsNameBasedReferencesAndContainingChunks()
    {
        var (index, resolver) = BuildFixture();

        string output = ContextTool.RunReferenceAware(
            index, index.Graph, resolver,
            query: "zzz no lexical match zzz", tokenBudget: 100000, maxHops: 0,
            entrySymbols: new[] { "OrderService" }, failingTest: null, stackTrace: null,
            referenceDepth: 1, excludeTests: false, json: true,
            readReferences: symbol => symbol.SymbolId == ServiceId
                ? new[] { new SymbolRef("OrderService", "type_usage", "web/OrderController.cs", 12, ControllerId) }
                : Array.Empty<SymbolRef>(),
            readCallees: symbol => symbol.SymbolId == ServiceId
                ? new[] { new SymbolRef("OrderRepo", "call", "src/OrderService.cs", 20, ServiceId) }
                : Array.Empty<SymbolRef>(),
            readContentChunks: (symbols, _) => new[]
            {
                SourceHit(
                    "src/OrderService.cs",
                    line: 15,
                    snippet: "public void PlaceOrder() { _repo.Save(); }",
                    containingSymbolId: ServiceId,
                    containingSymbolName: "OrderService"),
            },
            out int count,
            out int candidatesExamined);

        Assert.True(count >= 4);
        Assert.Equal(1, candidatesExamined);
        using var doc = JsonDocument.Parse(output);
        var bundle = doc.RootElement.GetProperty("bundle");
        Assert.Contains(bundle.EnumerateArray(), item =>
            item.GetProperty("item_type").GetString() == "symbol"
            && item.GetProperty("reason").GetString() == "definition"
            && item.GetProperty("confidence").GetString() == "exact"
            && item.GetProperty("symbol_id").GetString() == ServiceId);
        Assert.Contains(bundle.EnumerateArray(), item =>
            item.GetProperty("item_type").GetString() == "identifier"
            && item.GetProperty("reason").GetString() == "possible_reference"
            && item.GetProperty("confidence").GetString() == "name_based"
            && item.GetProperty("file").GetString() == "web/OrderController.cs");
        Assert.Contains(bundle.EnumerateArray(), item =>
            item.GetProperty("item_type").GetString() == "identifier"
            && item.GetProperty("reason").GetString() == "callee_identifier"
            && item.GetProperty("confidence").GetString() == "containing_symbol"
            && item.GetProperty("name").GetString() == "OrderRepo");
        Assert.Contains(bundle.EnumerateArray(), item =>
            item.GetProperty("item_type").GetString() == "content_chunk"
            && item.GetProperty("reason").GetString() == "containing_chunk"
            && item.GetProperty("confidence").GetString() == "exact"
            && item.GetProperty("chunk_id").GetString() == "chunk-a");
    }

    [Fact]
    public void RunReferenceAware_Compact_RendersReasonsAndConfidence()
    {
        var (index, resolver) = BuildFixture();

        string output = ContextTool.RunReferenceAware(
            index, index.Graph, resolver,
            query: "zzz no lexical match zzz", tokenBudget: 100000, maxHops: 0,
            entrySymbols: new[] { "OrderService" }, failingTest: null, stackTrace: null,
            referenceDepth: 1, excludeTests: false, json: false,
            readReferences: _ => new[] { new SymbolRef("OrderService", "type_usage", "web/OrderController.cs", 12, ControllerId) },
            readCallees: _ => Array.Empty<SymbolRef>(),
            readContentChunks: (_, _) => Array.Empty<TextContentSearchHit>(),
            out _, out _);

        Assert.Contains("# context bundle", output);
        Assert.Contains("reason=definition confidence=exact", output);
        Assert.Contains("reason=possible_reference confidence=name_based", output);
    }

    [Fact]
    public void RunReferenceAware_ExcludeTests_FiltersTestSymbolsReferencesAndChunks()
    {
        var (index, resolver) = BuildFixture();

        string output = ContextTool.RunReferenceAware(
            index, index.Graph, resolver,
            query: "zzz no lexical match zzz", tokenBudget: 100000, maxHops: 1,
            entrySymbols: new[] { "OrderService" }, failingTest: null, stackTrace: null,
            referenceDepth: 1, excludeTests: true, json: false,
            readReferences: _ => new[]
            {
                new SymbolRef("OrderService", "type_usage", "tests/OrderServiceTests.cs", 8, TestId),
                new SymbolRef("OrderService", "type_usage", "web/OrderController.cs", 12, ControllerId),
            },
            readCallees: _ => Array.Empty<SymbolRef>(),
            readContentChunks: (_, excludeTests) => new[]
            {
                SourceHit("tests/OrderServiceTests.cs", 5, "OrderService test reference"),
                SourceHit("src/OrderService.cs", 15, "OrderService production chunk"),
            }.Where(hit => !excludeTests || !IsTestPath.Check(hit.Path ?? hit.DisplayPath)).ToArray(),
            out _, out _);

        Assert.DoesNotContain("OrderServiceTests", output);
        Assert.DoesNotContain("tests/OrderServiceTests.cs", output);
        Assert.Contains("web/OrderController.cs", output);
        Assert.Contains("src/OrderService.cs", output);
    }

    [Fact]
    public void RunReferenceAware_DedupesDuplicateReferenceRows()
    {
        var (index, resolver) = BuildFixture();
        var duplicate = new SymbolRef("OrderService", "type_usage", "web/OrderController.cs", 12, ControllerId);

        string output = ContextTool.RunReferenceAware(
            index, index.Graph, resolver,
            query: "zzz no lexical match zzz", tokenBudget: 100000, maxHops: 0,
            entrySymbols: new[] { "OrderService" }, failingTest: null, stackTrace: null,
            referenceDepth: 1, excludeTests: false, json: true,
            readReferences: _ => new[] { duplicate, duplicate },
            readCallees: _ => Array.Empty<SymbolRef>(),
            readContentChunks: (_, _) => Array.Empty<TextContentSearchHit>(),
            out _, out _);

        using var doc = JsonDocument.Parse(output);
        int possibleReferenceCount = doc.RootElement.GetProperty("bundle").EnumerateArray()
            .Count(item => item.GetProperty("reason").GetString() == "possible_reference");
        Assert.Equal(1, possibleReferenceCount);
    }

    [Fact]
    public void RunReferenceAware_ZeroBudget_ReturnsEmptyBundle()
    {
        var (index, resolver) = BuildFixture();

        string output = ContextTool.RunReferenceAware(
            index, index.Graph, resolver,
            query: "zzz no lexical match zzz", tokenBudget: 0, maxHops: 0,
            entrySymbols: new[] { "OrderService" }, failingTest: null, stackTrace: null,
            referenceDepth: 1, excludeTests: false, json: false,
            readReferences: _ => new[] { new SymbolRef("OrderService", "type_usage", "web/OrderController.cs", 12, ControllerId) },
            readCallees: _ => Array.Empty<SymbolRef>(),
            readContentChunks: (_, _) => Array.Empty<TextContentSearchHit>(),
            out int count, out _);

        Assert.Equal(0, count);
        Assert.Equal("Bundle empty — raise token_budget.", output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RunReferenceAware_RenderedOutputFitsPositiveTokenBudget(bool json)
    {
        var (index, resolver) = BuildFixture();
        string longSnippet = new('x', 5000);

        foreach (int tokenBudget in new[] { 16, 32, 64, 128, 256, 512 })
        {
            string output = ContextTool.RunReferenceAware(
                index, index.Graph, resolver,
                query: "zzz no lexical match zzz", tokenBudget, maxHops: 0,
                entrySymbols: new[] { "OrderService" }, failingTest: null, stackTrace: null,
                referenceDepth: 1, excludeTests: false, json,
                readReferences: _ => Enumerable.Range(1, 8)
                    .Select(i => new SymbolRef("Reference" + i, "type_usage", "src/Reference" + i + ".cs", i, ServiceId))
                    .ToArray(),
                readCallees: _ => Array.Empty<SymbolRef>(),
                readContentChunks: (_, _) => new[]
                {
                    SourceHit("src/OrderService.cs", 1, longSnippet, containingSymbolId: ServiceId,
                        containingSymbolName: "OrderService"),
                },
                out int selectedCount, out _);

            Assert.True(TokenEstimator.Count(output) <= tokenBudget);
            AssertRenderedCount(output, json, selectedCount);
            if (selectedCount > 0)
                Assert.Contains("OrderService", output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RunReferenceAware_Json_TrimmingKeepsLargestPriorityPrefixWithBoundedAllocations()
    {
        var (index, resolver) = BuildFixture();
        SymbolRef[] references = Enumerable.Range(1, 2000)
            .Select(i => new SymbolRef(
                "Reference" + i,
                "type_usage",
                "src/Reference" + i + ".cs",
                i,
                ServiceId))
            .ToArray();

        string full = ContextTool.RunReferenceAware(
            index, index.Graph, resolver,
            query: "zzz no lexical match zzz", tokenBudget: int.MaxValue, maxHops: 0,
            entrySymbols: new[] { "OrderService" }, failingTest: null, stackTrace: null,
            referenceDepth: 1, excludeTests: false, json: true,
            readReferences: _ => references,
            readCallees: _ => Array.Empty<SymbolRef>(),
            readContentChunks: (_, _) => Array.Empty<TextContentSearchHit>(),
            out _, out _);

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        string bounded = ContextTool.RunReferenceAware(
            index, index.Graph, resolver,
            query: "zzz no lexical match zzz", tokenBudget: 40000, maxHops: 0,
            entrySymbols: new[] { "OrderService" }, failingTest: null, stackTrace: null,
            referenceDepth: 1, excludeTests: false, json: true,
            readReferences: _ => references,
            readCallees: _ => Array.Empty<SymbolRef>(),
            readContentChunks: (_, _) => Array.Empty<TextContentSearchHit>(),
            out int selectedCount, out _);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        using var fullDocument = JsonDocument.Parse(full);
        using var boundedDocument = JsonDocument.Parse(bounded);
        JsonElement.ArrayEnumerator fullItems = fullDocument.RootElement.GetProperty("bundle").EnumerateArray();
        JsonElement.ArrayEnumerator boundedItems = boundedDocument.RootElement.GetProperty("bundle").EnumerateArray();
        string[] expectedPrefix = fullItems.Take(selectedCount).Select(static item => item.GetRawText()).ToArray();
        string[] actualItems = boundedItems.Select(static item => item.GetRawText()).ToArray();

        Assert.Equal(668, selectedCount);
        Assert.Equal(expectedPrefix, actualItems);
        Assert.True(TokenEstimator.Count(bounded) <= 40000);
        Assert.True(allocated < 32_000_000, $"Context rendering allocated {allocated:N0} bytes.");
    }

    [Fact]
    public void RunReferenceAware_Json_PreservesFullSnippetWhenItFitsAndBoundsItWhenNeeded()
    {
        var (index, resolver) = BuildFixture();
        string longSnippet = new('x', 5000);

        string full = ContextTool.RunReferenceAware(
            index, index.Graph, resolver,
            query: "zzz no lexical match zzz", tokenBudget: 100000, maxHops: 0,
            entrySymbols: new[] { "OrderService" }, failingTest: null, stackTrace: null,
            referenceDepth: 0, excludeTests: false, json: true,
            readReferences: _ => Array.Empty<SymbolRef>(),
            readCallees: _ => Array.Empty<SymbolRef>(),
            readContentChunks: (_, _) => new[]
            {
                SourceHit("src/OrderService.cs", 1, longSnippet, containingSymbolId: ServiceId,
                    containingSymbolName: "OrderService"),
            },
            out _, out _);
        string bounded = ContextTool.RunReferenceAware(
            index, index.Graph, resolver,
            query: "zzz no lexical match zzz", tokenBudget: 512, maxHops: 0,
            entrySymbols: new[] { "OrderService" }, failingTest: null, stackTrace: null,
            referenceDepth: 0, excludeTests: false, json: true,
            readReferences: _ => Array.Empty<SymbolRef>(),
            readCallees: _ => Array.Empty<SymbolRef>(),
            readContentChunks: (_, _) => new[]
            {
                SourceHit("src/OrderService.cs", 1, longSnippet, containingSymbolId: ServiceId,
                    containingSymbolName: "OrderService"),
            },
            out int boundedCount, out _);

        using var fullDocument = JsonDocument.Parse(full);
        using var boundedDocument = JsonDocument.Parse(bounded);
        JsonElement fullChunk = fullDocument.RootElement.GetProperty("bundle").EnumerateArray()
            .Single(item => item.GetProperty("item_type").GetString() == "content_chunk");
        JsonElement boundedChunk = boundedDocument.RootElement.GetProperty("bundle").EnumerateArray()
            .Single(item => item.GetProperty("item_type").GetString() == "content_chunk");
        Assert.Equal(5000, fullChunk.GetProperty("snippet").GetString()!.Length);
        Assert.Equal(ToolRenderLimits.SignatureMaxLength, boundedChunk.GetProperty("snippet").GetString()!.Length);
        Assert.Equal(boundedCount, boundedDocument.RootElement.GetProperty("bundle").GetArrayLength());
        Assert.True(TokenEstimator.Count(bounded) <= 512);
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
    public void Context_ReferenceModeUsage_ReadsNameReferencesAndCalleesFromWorkspaceArtifact()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var index = MillerRepositoryIndex.Build(SqliteSymbolReader.Read(fx.DbPath));
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(index, fx.DbPath, "current-ws", fx.WorkspaceRoot));
        var tool = new ContextTool(provider);

        string output = tool.Context(
            "zzz no lexical match zzz",
            entry_symbols: new[] { "GetUser" },
            max_hops: 0,
            token_budget: 100000,
            format: "json",
            reference_mode: "usage");

        using var doc = JsonDocument.Parse(output);
        var bundle = doc.RootElement.GetProperty("bundle");
        Assert.Contains(bundle.EnumerateArray(), item =>
            item.GetProperty("reason").GetString() == "definition"
            && item.GetProperty("symbol_id").GetString() == JulieDbFixture.GetUserId);
        Assert.Contains(bundle.EnumerateArray(), item =>
            item.GetProperty("reason").GetString() == "possible_reference"
            && item.GetProperty("confidence").GetString() == "name_based"
            && item.GetProperty("file").GetString() == "web/Controller.cs");
        Assert.Contains(bundle.EnumerateArray(), item =>
            item.GetProperty("reason").GetString() == "callee_identifier"
            && item.GetProperty("confidence").GetString() == "containing_symbol"
            && item.GetProperty("name").GetString() == "Find");
    }

    [Fact]
    public void Context_ReferenceModeUsage_ReadsContainingChunksFromContentSidecar()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        WriteContentChunk(
            ContentCorpusSidecar.ContentDbPathFor(fx.DbPath),
            chunkId: "chunk-get-user",
            path: "auth/UserService.cs",
            rawText: "public User GetUser(int id)\n{\n    return _repo.Find(id);\n}",
            containingSymbolId: JulieDbFixture.GetUserId,
            containingSymbolName: "GetUser");
        var index = MillerRepositoryIndex.Build(SqliteSymbolReader.Read(fx.DbPath));
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(index, fx.DbPath, "current-ws", fx.WorkspaceRoot));
        var tool = new ContextTool(provider);

        string output = tool.Context(
            "zzz no lexical match zzz",
            entry_symbols: new[] { "GetUser" },
            max_hops: 0,
            token_budget: 100000,
            format: "json",
            reference_mode: "usage");

        using var doc = JsonDocument.Parse(output);
        var bundle = doc.RootElement.GetProperty("bundle");
        Assert.Contains(bundle.EnumerateArray(), item =>
            item.GetProperty("item_type").GetString() == "content_chunk"
            && item.GetProperty("reason").GetString() == "containing_chunk"
            && item.GetProperty("confidence").GetString() == "exact"
            && item.GetProperty("chunk_id").GetString() == "chunk-get-user"
            && item.GetProperty("snippet").GetString()!.Contains("GetUser", StringComparison.Ordinal));
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
