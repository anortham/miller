using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Core.Graph;
using Miller.Core.References;
using Miller.Core.Search;
using Miller.Indexing;
using Miller.Indexing.Semantic;
using Miller.Server.Resolution;
using Miller.Server.Telemetry;
using Miller.Server.Tools;
using Miller.Tests;
using Miller.Tests.Indexing;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>Pins context pivot ranking, evidence packing, rendering, semantic policy, and task anchors.</summary>
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

    private static ReferenceEvidence FallbackInbound(
        string referenceSiteId,
        string? containingSymbolId,
        string path,
        int line,
        ReferenceKind kind) =>
        new(
            null,
            containingSymbolId,
            path,
            line,
            0,
            line,
            1,
            line * 100,
            line * 100 + 1,
            kind,
            kind == ReferenceKind.Call ? "call" : "type_usage",
            ReferenceEvidenceSource.NameFallback,
            null,
            0.5,
            ReferenceResolutionStatus.Fallback,
            "csharp",
            referenceSiteId,
            true,
            "target_token");

    private static OutgoingReferenceEvidence FallbackOutgoing(
        string referenceSiteId,
        string containingSymbolId,
        string targetName,
        string path,
        int line,
        ReferenceKind kind) =>
        new(
            containingSymbolId,
            null,
            targetName,
            path,
            line,
            0,
            line,
            1,
            line * 100,
            line * 100 + 1,
            kind,
            kind == ReferenceKind.Call ? "call" : "type_usage",
            ReferenceEvidenceSource.NameFallback,
            null,
            0.5,
            ReferenceResolutionStatus.Fallback,
            "csharp",
            referenceSiteId,
            true,
            "target_token");

    private static ReferenceEvidenceSet InboundSet(params ReferenceEvidence[] fallback) =>
        new(
            [],
            fallback,
            new ReferenceEvidenceCoverage(
                0,
                0,
                0,
                fallback.Length,
                fallback.Length,
                1,
                false,
                false,
                fallback.Length == 0
                    ? ReferenceFallbackStatus.NoCandidates
                    : ReferenceFallbackStatus.Available));

    private static OutgoingReferenceEvidenceSet OutgoingSet(params OutgoingReferenceEvidence[] fallback) =>
        new(
            [],
            fallback,
            new OutgoingReferenceEvidenceCoverage(
                0,
                0,
                0,
                fallback.Length,
                fallback.Length,
                false,
                false));

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
            if (selectedCount == 0 &&
                !document.RootElement.TryGetProperty("bundle", out _))
            {
                Assert.Equal("{}", output);
                return;
            }
            Assert.Equal(selectedCount, document.RootElement.GetProperty("bundle").GetArrayLength());
        }
        else if (selectedCount == 0)
        {
            Assert.Equal("No evidence fit token_budget.", output);
        }
        else
        {
            Assert.Contains($"# context bundle ({selectedCount})", output, StringComparison.Ordinal);
        }
    }

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
    public void Run_QueryPivot_PrefersDefinitionOverSameSpanExportAlias()
    {
        var symbols = new List<IndexedSymbol>
        {
            new(0, "export-id", "normalizePathKeyForSafetyCheck", null, "export", "typescript",
                "src/workspace.ts", 165, 170, null, false),
            new(1, "function-id", "normalizePathKeyForSafetyCheck",
                "normalizePathKeyForSafetyCheck(value: string): string", "function", "typescript",
                "src/workspace.ts", 165, 170, null, false),
        };
        MillerRepositoryIndex index = MillerRepositoryIndex.Build(symbols, Array.Empty<GraphEdge>());
        var resolver = new SmartTargetResolver(index);

        string output = ContextTool.Run(
            index,
            resolver,
            query: "Locate the path-key normalizer and exact function definition",
            tokenBudget: 2400,
            maxHops: 0,
            entrySymbols: null,
            failingTest: null,
            stackTrace: null,
            json: true,
            out _,
            out _);

        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement pivot = document.RootElement
            .GetProperty("bundle")
            .EnumerateArray()
            .Single(item =>
                item.GetProperty("item_type").GetString() == "symbol" &&
                item.GetProperty("name").GetString() == "normalizePathKeyForSafetyCheck");
        Assert.Equal("function-id", pivot.GetProperty("symbol_id").GetString());
        Assert.Equal("function", pivot.GetProperty("kind").GetString());
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
        Assert.Empty(output);
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
            if (tokenBudget >= 512)
                Assert.True(Encoding.UTF8.GetByteCount(output) <= tokenBudget * 3);
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
    public void Run_PositiveBudgetBelowEmptyEnvelope_StaysWithinBudget(
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
                readReferenceEvidence: _ => InboundSet(FallbackInbound(
                    "site:file:web/OrderController.cs:1200:1201",
                    ControllerId,
                    "web/OrderController.cs",
                    12,
                    ReferenceKind.TypeUsage)),
                readOutgoingEvidence: _ => OutgoingSet(),
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
        Assert.Equal(json ? "{}" : string.Empty, output);
        Assert.True(TokenEstimator.Count(output) <= 1);
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
        Assert.Equal("symbol", first.GetProperty("item_type").GetString());
        Assert.Equal("pivot", first.GetProperty("role").GetString());
        Assert.Equal("exact", first.GetProperty("confidence").GetString());
        Assert.True(first.TryGetProperty("reason", out _));
        Assert.Equal(0, first.GetProperty("hop").GetInt32());
        Assert.Equal("partial", root.GetProperty("disposition").GetProperty("status").GetString());
        Assert.NotEmpty(root.GetProperty("next_actions").EnumerateArray());
    }

    [Fact]
    public void Run_Compact_CarriesSignatureAndProvenance()
    {
        var (index, resolver) = BuildFixture();

        string output = ContextTool.Run(index, resolver,
            query: "zzz no lexical match zzz", tokenBudget: 100000, maxHops: 1,
            entrySymbols: new[] { "OrderService" }, failingTest: null, stackTrace: null, json: false, out _, out _);

        Assert.Contains("## pivots", output);
        Assert.Contains("OrderService  class  src/OrderService.cs:1  pivot  class OrderService", output);
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
        Assert.Equal(
            "# context bundle (3)\n" +
            "## pivots\n" +
            "Alpha  method  src/Shared.cs:10  pivot  method Alpha()\n" +
            "Beta  method  src/Shared.cs:20  pivot  method Beta()\n" +
            "Gamma  class  src/Gamma.cs:30  pivot  class Gamma\n" +
            "## disposition\n" +
            "evidence=partial  reason=pivot_signature_only\n" +
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

        Assert.Contains("## pivots", output);
        Assert.Contains("OrderService  class  src/OrderService.cs:1  pivot  class OrderService", output);
        Assert.Contains("## neighbours", output);
        Assert.Contains("web/OrderController.cs:\n  :1 OrderController class hop=1", output);
        Assert.Contains("src/OrderRepo.cs:\n  :1 OrderRepo class hop=1", output);
        Assert.Contains("tests/OrderServiceTests.cs:\n  :1 OrderServiceTests class hop=1", output);
        Assert.Contains("## next inspect\ninspect(target=\"OrderService\", scope=\"src/OrderService.cs\", depth=\"overview\")", output);
    }

    [Fact]
    public void Run_Compact_RendersEverySelectedNeighbour()
    {
        var (index, resolver) = BuildWideFixture();

        string output = ContextTool.Run(index, resolver,
            query: "Hub", tokenBudget: 100000, maxHops: 1,
            entrySymbols: null, failingTest: null, stackTrace: null, json: false, out _, out _);

        Assert.Contains("## pivots", output);
        Assert.Contains("Hub  class  src/Hub.cs:1  pivot  class Hub", output);
        Assert.DoesNotContain("neighbours omitted", output, StringComparison.Ordinal);
        Assert.Equal(15, output.Split("hop=1").Length - 1);
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
    public void RunReferenceAware_Json_LabelsFallbackReferencesAndContainingChunks()
    {
        var (index, resolver) = BuildFixture();

        string output = ContextTool.RunReferenceAware(
            index, index.Graph, resolver,
            query: "zzz no lexical match zzz", tokenBudget: 100000, maxHops: 0,
            entrySymbols: new[] { "OrderService" }, failingTest: null, stackTrace: null,
            referenceDepth: 1, excludeTests: false, json: true,
            readReferenceEvidence: symbol => symbol.SymbolId == ServiceId
                ? InboundSet(FallbackInbound(
                    "site:file:web/OrderController.cs:1200:1201",
                    ControllerId,
                    "web/OrderController.cs",
                    12,
                    ReferenceKind.TypeUsage))
                : InboundSet(),
            readOutgoingEvidence: symbol => symbol.SymbolId == ServiceId
                ? OutgoingSet(FallbackOutgoing(
                    "site:file:src/OrderService.cs:2000:2001",
                    ServiceId,
                    "OrderRepo",
                    "src/OrderService.cs",
                    20,
                    ReferenceKind.Call))
                : OutgoingSet(),
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
            && item.GetProperty("reason").GetString() == "entry_symbol"
            && item.GetProperty("role").GetString() == "pivot"
            && item.GetProperty("confidence").GetString() == "exact"
            && item.GetProperty("symbol_id").GetString() == ServiceId);
        Assert.Contains(bundle.EnumerateArray(), item =>
            item.GetProperty("item_type").GetString() == "identifier"
            && item.GetProperty("reason").GetString() == "possible_reference"
            && item.GetProperty("confidence").GetString() == "fallback"
            && item.GetProperty("resolution_status").GetString() == "fallback"
            && item.GetProperty("provenance").GetString() == "name_fallback"
            && item.GetProperty("file").GetString() == "web/OrderController.cs");
        Assert.Contains(bundle.EnumerateArray(), item =>
            item.GetProperty("item_type").GetString() == "identifier"
            && item.GetProperty("reason").GetString() == "unresolved_callee"
            && item.GetProperty("confidence").GetString() == "fallback"
            && item.GetProperty("name").GetString() == "OrderRepo");
        Assert.Contains(bundle.EnumerateArray(), item =>
            item.GetProperty("item_type").GetString() == "content_chunk"
            && item.GetProperty("reason").GetString() == "containing_chunk"
            && item.GetProperty("confidence").GetString() == "exact"
            && item.GetProperty("chunk_id").GetString() == "chunk-a");
    }

    [Fact]
    public void RunReferenceAware_Json_PreservesExactTargetAndProvenance()
    {
        var (index, resolver) = BuildFixture();
        var inbound = new ReferenceEvidence(
            ServiceId,
            ControllerId,
            "web/OrderController.cs",
            12,
            4,
            12,
            16,
            100,
            112,
            ReferenceKind.TypeUsage,
            "type_usage",
            ReferenceEvidenceSource.IdentifierResolution,
            1,
            0.95,
            ReferenceResolutionStatus.Exact,
            Language: "csharp",
            ReferenceSiteId: "site:controller:100:112",
            IsExact: true,
            SiteProvenance: "target_token");
        var outbound = new OutgoingReferenceEvidence(
            ServiceId,
            RepoId,
            "OrderRepo",
            "src/OrderService.cs",
            20,
            8,
            20,
            17,
            200,
            209,
            ReferenceKind.Call,
            "call",
            ReferenceEvidenceSource.IdentifierDirect,
            null,
            1,
            ReferenceResolutionStatus.Exact,
            Language: "csharp",
            ReferenceSiteId: "site:service:200:209",
            IsExact: true,
            SiteProvenance: "target_token");
        var dependency = new OutgoingReferenceEvidence(
            ServiceId,
            ControllerId,
            "OrderController",
            "src/OrderService.cs",
            22,
            8,
            22,
            23,
            220,
            235,
            ReferenceKind.TypeUsage,
            "type_usage",
            ReferenceEvidenceSource.IdentifierDirect,
            null,
            1,
            ReferenceResolutionStatus.Exact,
            Language: "csharp",
            ReferenceSiteId: "site:service:220:235",
            IsExact: true,
            SiteProvenance: "target_token");

        string output = ContextTool.RunReferenceAware(
            index,
            index.Graph,
            resolver,
            query: "zzz no lexical match zzz",
            tokenBudget: 100000,
            maxHops: 0,
            entrySymbols: [ServiceId],
            failingTest: null,
            stackTrace: null,
            referenceDepth: 1,
            excludeTests: false,
            json: true,
            readReferenceEvidence: _ => new ReferenceEvidenceSet(
                [inbound],
                [],
                new ReferenceEvidenceCoverage(1, 1, 1, 0, 0, 1, false, false, ReferenceFallbackStatus.NoCandidates)),
            readOutgoingEvidence: _ => new OutgoingReferenceEvidenceSet(
                [outbound, dependency],
                [],
                new OutgoingReferenceEvidenceCoverage(2, 2, 2, 0, 0, false, false)),
            readContentChunks: (_, _) => [],
            out _,
            out _);

        using var doc = JsonDocument.Parse(output);
        JsonElement bundle = doc.RootElement.GetProperty("bundle");
        Assert.Contains(bundle.EnumerateArray(), item =>
            item.GetProperty("reason").GetString() == "reference"
            && item.GetProperty("target_symbol_id").GetString() == ServiceId
            && item.GetProperty("resolution_status").GetString() == "exact"
            && item.GetProperty("provenance").GetString() == "identifier_resolution");
        Assert.Contains(bundle.EnumerateArray(), item =>
            item.GetProperty("reason").GetString() == "callee"
            && item.GetProperty("target_symbol_id").GetString() == RepoId
            && item.GetProperty("resolution_status").GetString() == "exact"
            && item.GetProperty("provenance").GetString() == "identifier_direct");
        Assert.Contains(bundle.EnumerateArray(), item =>
            item.GetProperty("reason").GetString() == "dependency"
            && item.GetProperty("target_symbol_id").GetString() == ControllerId
            && item.GetProperty("resolution_status").GetString() == "exact");
        Assert.Equal(
            "partial",
            doc.RootElement.GetProperty("disposition").GetProperty("status").GetString());
        Assert.NotEmpty(doc.RootElement.GetProperty("next_actions").EnumerateArray());
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
            readReferenceEvidence: _ => InboundSet(FallbackInbound(
                "site:file:web/OrderController.cs:1200:1201",
                ControllerId,
                "web/OrderController.cs",
                12,
                ReferenceKind.TypeUsage)),
            readOutgoingEvidence: _ => OutgoingSet(),
            readContentChunks: (_, _) => Array.Empty<TextContentSearchHit>(),
            out _, out _);

        Assert.Contains("# context bundle", output);
        Assert.Contains("reason=entry_symbol confidence=exact", output);
        Assert.Contains("role=pivot", output);
        Assert.Contains("reason=possible_reference confidence=fallback", output);
        Assert.Contains("resolution=fallback source=name_fallback", output);
        Assert.Contains("## next inspect", output, StringComparison.Ordinal);
        Assert.Contains(
            "inspect(target=\"OrderService\", scope=\"src/OrderService.cs\", depth=\"overview\")",
            output,
            StringComparison.Ordinal);
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
            readReferenceEvidence: _ => InboundSet(
                FallbackInbound(
                    "site:file:tests/OrderServiceTests.cs:800:801",
                    TestId,
                    "tests/OrderServiceTests.cs",
                    8,
                    ReferenceKind.TypeUsage),
                FallbackInbound(
                    "site:file:web/OrderController.cs:1200:1201",
                    ControllerId,
                    "web/OrderController.cs",
                    12,
                    ReferenceKind.TypeUsage)),
            readOutgoingEvidence: _ => OutgoingSet(),
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
    public void RunReferenceAware_ExcludeTestsFiltersSymbolsByPath()
    {
        var symbol = new IndexedSymbol(
            0,
            "00000000000000000000000000000302",
            "PathOnlyTest",
            "class PathOnlyTest",
            "class",
            "csharp",
            "tests/PathOnlyTest.cs",
            1,
            20,
            null,
            IsTest: false);
        var index = MillerRepositoryIndex.Build([symbol]);
        var resolver = new SmartTargetResolver(index);

        string output = ContextTool.RunReferenceAwareActionable(
            index,
            index.Graph,
            resolver,
            query: string.Empty,
            tokenBudget: 1000,
            maxHops: 0,
            entrySymbols: null,
            editedFiles: null,
            failingTest: null,
            stackTrace: null,
            semanticSeeds: [new ContextTool.ContextSemanticSeed(symbol, 1, 0.91)],
            readBody: null,
            referenceDepth: 0,
            excludeTests: true,
            json: true,
            readReferenceEvidence: _ => new ReferenceEvidenceSet(
                [],
                [],
                new ReferenceEvidenceCoverage(
                    0, 0, 0, 0, 0, 0, false, false, ReferenceFallbackStatus.NoCandidates)),
            readOutgoingEvidence: _ => new OutgoingReferenceEvidenceSet(
                [],
                [],
                new OutgoingReferenceEvidenceCoverage(0, 0, 0, 0, 0, false, false)),
            readContentChunks: (_, _) => [],
            out _,
            out _);

        using var document = JsonDocument.Parse(output);
        Assert.Empty(document.RootElement.GetProperty("bundle").EnumerateArray());
    }

    [Fact]
    public void RunReferenceAware_DedupesDuplicateReferenceRows()
    {
        var (index, resolver) = BuildFixture();
        ReferenceEvidence duplicate = FallbackInbound(
            "site:file:web/OrderController.cs:1200:1201",
            ControllerId,
            "web/OrderController.cs",
            12,
            ReferenceKind.TypeUsage);

        string output = ContextTool.RunReferenceAware(
            index, index.Graph, resolver,
            query: "zzz no lexical match zzz", tokenBudget: 100000, maxHops: 0,
            entrySymbols: new[] { "OrderService" }, failingTest: null, stackTrace: null,
            referenceDepth: 1, excludeTests: false, json: true,
            readReferenceEvidence: _ => InboundSet(duplicate, duplicate),
            readOutgoingEvidence: _ => OutgoingSet(),
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
            readReferenceEvidence: _ => InboundSet(FallbackInbound(
                "site:file:web/OrderController.cs:1200:1201",
                ControllerId,
                "web/OrderController.cs",
                12,
                ReferenceKind.TypeUsage)),
            readOutgoingEvidence: _ => OutgoingSet(),
            readContentChunks: (_, _) => Array.Empty<TextContentSearchHit>(),
            out int count, out _);

        Assert.Equal(0, count);
        Assert.Empty(output);
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
                readReferenceEvidence: _ => InboundSet(Enumerable.Range(1, 8)
                    .Select(i => FallbackInbound(
                        $"site:file:src/Reference{i}.cs:{i * 100}:{i * 100 + 1}",
                        ServiceId,
                        $"src/Reference{i}.cs",
                        i,
                        ReferenceKind.TypeUsage))
                    .ToArray()),
                readOutgoingEvidence: _ => OutgoingSet(),
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
        ReferenceEvidence[] references = Enumerable.Range(1, 2000)
            .Select(i => FallbackInbound(
                $"site:file:src/Reference{i}.cs:{i * 100}:{i * 100 + 1}",
                ServiceId,
                $"src/Reference{i}.cs",
                i,
                ReferenceKind.TypeUsage))
            .ToArray();

        string full = ContextTool.RunReferenceAware(
            index, index.Graph, resolver,
            query: "zzz no lexical match zzz", tokenBudget: int.MaxValue, maxHops: 0,
            entrySymbols: new[] { "OrderService" }, failingTest: null, stackTrace: null,
            referenceDepth: 1, excludeTests: false, json: true,
            readReferenceEvidence: _ => InboundSet(references),
            readOutgoingEvidence: _ => OutgoingSet(),
            readContentChunks: (_, _) => Array.Empty<TextContentSearchHit>(),
            out _, out _);

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        string bounded = ContextTool.RunReferenceAware(
            index, index.Graph, resolver,
            query: "zzz no lexical match zzz", tokenBudget: 40000, maxHops: 0,
            entrySymbols: new[] { "OrderService" }, failingTest: null, stackTrace: null,
            referenceDepth: 1, excludeTests: false, json: true,
            readReferenceEvidence: _ => InboundSet(references),
            readOutgoingEvidence: _ => OutgoingSet(),
            readContentChunks: (_, _) => Array.Empty<TextContentSearchHit>(),
            out int selectedCount, out _);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        using var fullDocument = JsonDocument.Parse(full);
        using var boundedDocument = JsonDocument.Parse(bounded);
        JsonElement.ArrayEnumerator fullItems = fullDocument.RootElement.GetProperty("bundle").EnumerateArray();
        JsonElement.ArrayEnumerator boundedItems = boundedDocument.RootElement.GetProperty("bundle").EnumerateArray();
        string[] expectedPrefix = fullItems.Take(selectedCount).Select(static item => item.GetRawText()).ToArray();
        string[] actualItems = boundedItems.Select(static item => item.GetRawText()).ToArray();

        Assert.Equal(370, selectedCount);
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
            readReferenceEvidence: _ => InboundSet(),
            readOutgoingEvidence: _ => OutgoingSet(),
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
            readReferenceEvidence: _ => InboundSet(),
            readOutgoingEvidence: _ => OutgoingSet(),
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
    public void Context_LowBudgetCrossWorkspaceResponseKeepsBanner()
    {
        var currentIndex = EmptyIndex();
        var targetIndex = EmptyIndex();
        string currentRoot = Path.Combine(Path.GetTempPath(), "miller-current-" + Guid.NewGuid().ToString("N"));
        string targetRoot = Path.Combine(Path.GetTempPath(), "miller-target-" + Guid.NewGuid().ToString("N"));
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(currentIndex, "current.db", "current-ws", currentRoot),
            ("target-ws", ReadToolRoutingTestSupport.ContextFor(targetIndex, "target.db", "target-ws", targetRoot)));
        var tool = new ContextTool(provider);

        string output = tool.Context(
            "MissingEntry",
            token_budget: 32,
            workspace_id: "target-ws");

        Assert.StartsWith("workspace: target-ws", output, StringComparison.Ordinal);
        Assert.True(TokenEstimator.Count(output) <= 32);
    }

    [Fact]
    public void Context_EditedFileAnchorBeatsUnrelatedExactQueryHit()
    {
        var (index, _) = BuildFixture();
        string root = Path.Combine(Path.GetTempPath(), "miller-context-" + Guid.NewGuid().ToString("N"));
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(index, "context.db", "context-ws", root));
        var tool = new ContextTool(provider);

        string output = tool.Context(
            "UnrelatedHelper",
            format: "json",
            edited_files: ["src/OrderRepo.cs"]);

        using var document = JsonDocument.Parse(output);
        JsonElement first = document.RootElement.GetProperty("bundle")[0];
        Assert.Equal("OrderRepo", first.GetProperty("name").GetString());
        Assert.Equal("edited_file", first.GetProperty("reason").GetString());
    }

    [Fact]
    public void RunActionable_EditedFileUsesQueryRankAndExcludesMemberStorage()
    {
        IndexedSymbol[] symbols =
        [
            new(0, "00000000000000000000000000000101", "PivotHost", "class PivotHost", "class", "csharp",
                "src/PivotHost.cs", 1, 40, null, false),
            new(1, "00000000000000000000000000000102", "_first", "string _first", "field", "csharp",
                "src/PivotHost.cs", 2, 2, null, false),
            new(2, "00000000000000000000000000000103", "_second", "string _second", "field", "csharp",
                "src/PivotHost.cs", 3, 3, null, false),
            new(3, "00000000000000000000000000000104", "_third", "string _third", "field", "csharp",
                "src/PivotHost.cs", 4, 4, null, false),
            new(4, "00000000000000000000000000000105", "RankPivots", "void RankPivots()", "method", "csharp",
                "src/PivotHost.cs", 10, 20, null, false),
            new(5, "00000000000000000000000000000106", "PivotHost", "PivotHost()", "constructor", "csharp",
                "src/PivotHost.cs", 21, 24, null, false),
        ];
        var index = MillerRepositoryIndex.Build(symbols);
        var resolver = new SmartTargetResolver(index);

        string output = ContextTool.RunActionable(
            index,
            index.Graph,
            resolver,
            query: "rank pivots",
            tokenBudget: 2000,
            maxHops: 0,
            entrySymbols: null,
            editedFiles: ["src/PivotHost.cs"],
            failingTest: null,
            stackTrace: null,
            semanticSeeds: null,
            readBody: null,
            json: true,
            out _,
            out _);

        using var document = JsonDocument.Parse(output);
        JsonElement bundle = document.RootElement.GetProperty("bundle");
        Assert.Equal("RankPivots", bundle[0].GetProperty("name").GetString());
        Assert.DoesNotContain(bundle.EnumerateArray(), item => item.GetProperty("kind").GetString() == "field");
        Assert.DoesNotContain(bundle.EnumerateArray(), item => item.GetProperty("kind").GetString() == "constructor");
    }

    [Fact]
    public void RunActionable_LongTaskQueryRetrievesTaskTermsThroughSharedSearch()
    {
        IndexedSymbol[] symbols =
        [
            new(0, "00000000000000000000000000000201", "rules", "rules", "property", "javascript",
                "grammar.js", 70, 100, null, false),
            new(1, "00000000000000000000000000000202", "tree_sitter_razor_external_scanner_serialize",
                "unsigned tree_sitter_razor_external_scanner_serialize()", "function", "c",
                "src/scanner.c", 215, 230, null, false),
            new(2, "00000000000000000000000000000203", "tree_sitter_razor_external_scanner_scan",
                "bool tree_sitter_razor_external_scanner_scan()", "function", "c",
                "src/scanner.c", 313, 358, null, false),
            new(3, "00000000000000000000000000000204", "scan_markup_tag",
                "bool scan_markup_tag()", "function", "c",
                "src/scanner.c", 270, 310, null, false),
            new(4, "00000000000000000000000000000205", "TokenType", "enum TokenType", "enum", "c",
                "src/scanner.c", 8, 30, null, false),
            new(5, "00000000000000000000000000000206", "TSLanguage", "struct TSLanguage", "struct", "c",
                "src/tree_sitter/parser.h", 107, 180, null, false),
            new(6, "00000000000000000000000000000207", "BdistWheel", "class BdistWheel", "class", "python",
                "setup.py", 10, 20, null, false),
            new(7, "00000000000000000000000000000208", "scan_razor_marker", "bool scan_razor_marker()",
                "function", "c", "src/scanner.c", 120, 145, null, false),
            new(8, "00000000000000000000000000000209", "tag_names_equal", "bool tag_names_equal()",
                "function", "c", "src/scanner.c", 180, 200, null, false),
        ];
        var index = MillerRepositoryIndex.Build(symbols);
        var resolver = new SmartTargetResolver(index);

        string output = ContextTool.RunActionable(
            index,
            index.Graph,
            resolver,
            query: "Assemble the minimal grammar and scanner context needed to change how Razor markup tag " +
                   "names are recognized and dispatched, then propose the scanner implementation as the edit target.",
            tokenBudget: 4000,
            maxHops: 0,
            entrySymbols: null,
            editedFiles: null,
            failingTest: null,
            stackTrace: null,
            semanticSeeds: null,
            readBody: null,
            json: true,
            out _,
            out _);

        using var document = JsonDocument.Parse(output);
        string[] pivots = document.RootElement.GetProperty("bundle")
            .EnumerateArray()
            .Where(item => item.GetProperty("role").GetString() == "pivot")
            .Select(item => item.GetProperty("name").GetString()!)
            .ToArray();
        Assert.Contains("tree_sitter_razor_external_scanner_scan", pivots);
        Assert.Contains("scan_markup_tag", pivots);
    }

    [Fact]
    public void RunActionable_RescueOnlyPivotBodyRemainsPartial()
    {
        var (index, resolver) = BuildFixture();
        IndexedSymbol repo = Assert.Single(index.FindByName("OrderRepo"));

        string output = ContextTool.RunActionable(
            index,
            index.Graph,
            resolver,
            query: string.Empty,
            tokenBudget: 4000,
            maxHops: 0,
            entrySymbols: null,
            editedFiles: null,
            failingTest: null,
            stackTrace: null,
            semanticSeeds: [new ContextTool.ContextSemanticSeed(repo, 1, 0.91)],
            readBody: symbol => symbol.Name == "OrderRepo"
                ? ExtractReader.BodyReadResult.Available("class OrderRepo { }")
                : ExtractReader.BodyReadResult.Unavailable(ExtractReader.BodyUnavailableReason.NoSpanRecorded),
            json: true,
            out _,
            out _);

        using var document = JsonDocument.Parse(output);
        JsonElement implementation = Assert.Single(
            document.RootElement.GetProperty("bundle").EnumerateArray(),
            item => item.TryGetProperty("body", out _));
        Assert.Equal("semantic_rank_1", implementation.GetProperty("reason").GetString());
        Assert.Equal("partial", document.RootElement.GetProperty("disposition").GetProperty("status").GetString());
    }

    [Fact]
    public void RunActionable_IncludesNearbyFileNeighbourWithoutAGraphEdge()
    {
        IndexedSymbol[] symbols =
        [
            new(0, "00000000000000000000000000000301", "LANGUAGE",
                "pub const LANGUAGE: LanguageFn", "constant", "rust",
                "bindings/rust/lib.rs", 23, 25, null, false),
            new(1, "00000000000000000000000000000302", "test_can_load_grammar",
                "fn test_can_load_grammar()", "function", "rust",
                "bindings/rust/lib.rs", 54, 59, null, true),
        ];
        var index = MillerRepositoryIndex.Build(symbols);
        var resolver = new SmartTargetResolver(index);

        string output = ContextTool.RunActionable(
            index,
            index.Graph,
            resolver,
            query: "test_can_load_grammar",
            tokenBudget: 2000,
            maxHops: 1,
            entrySymbols: null,
            editedFiles: null,
            failingTest: null,
            stackTrace: null,
            semanticSeeds: null,
            readBody: null,
            json: true,
            out _,
            out _);

        using var document = JsonDocument.Parse(output);
        JsonElement neighbour = Assert.Single(
            document.RootElement.GetProperty("bundle").EnumerateArray(),
            item => item.GetProperty("role").GetString() == "neighbour");
        Assert.Equal("LANGUAGE", neighbour.GetProperty("name").GetString());
        Assert.Equal("file_neighbour", neighbour.GetProperty("reason").GetString());
    }

    [Theory]
    [InlineData("at Execute (src/Shared.cs:20)")]
    [InlineData("File \"src/Shared.cs\", line 20, in execute")]
    public void Context_LineAwareStackFrameSelectsContainingSymbol(string stackTrace)
    {
        var (index, _) = BuildSharedFileFixture();
        string root = Path.Combine(Path.GetTempPath(), "miller-context-" + Guid.NewGuid().ToString("N"));
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(index, "context.db", "context-ws", root));
        var tool = new ContextTool(provider);

        string output = tool.Context(
            string.Empty,
            stack_trace: stackTrace,
            format: "json");

        using var document = JsonDocument.Parse(output);
        JsonElement first = document.RootElement.GetProperty("bundle")[0];
        Assert.Equal("Beta", first.GetProperty("name").GetString());
        Assert.Equal("stack_frame", first.GetProperty("reason").GetString());
        Assert.Equal(20, first.GetProperty("anchor_line").GetInt32());
    }

    [Fact]
    public void Context_AmbiguousOrMissingAnchorsAreTypedInOutput()
    {
        var (index, _) = BuildFixture();
        string root = Path.Combine(Path.GetTempPath(), "miller-context-" + Guid.NewGuid().ToString("N"));
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(index, "context.db", "context-ws", root));
        var tool = new ContextTool(provider);

        string output = tool.Context(
            "OrderService",
            entry_symbols: ["MissingAnchor"],
            format: "json");

        using var document = JsonDocument.Parse(output);
        JsonElement diagnostic = Assert.Single(
            document.RootElement.GetProperty("anchor_diagnostics").EnumerateArray());
        Assert.Equal("entry_symbol", diagnostic.GetProperty("kind").GetString());
        Assert.Equal("not_found", diagnostic.GetProperty("reason").GetString());
    }

    [Fact]
    public void Context_CompactLabelsAmbiguousUsedAnchorsAsDiagnostics()
    {
        IndexedSymbol[] matches = Enumerable.Range(0, 20)
            .Select(i => new IndexedSymbol(
                i,
                (10_000 + i).ToString("x32"),
                "Run",
                "void Run()",
                "method",
                "csharp",
                $"src/Run{i}.cs",
                1,
                2,
                null,
                false))
            .ToArray();
        var index = MillerRepositoryIndex.Build(matches);
        string root = Path.Combine(Path.GetTempPath(), "miller-context-" + Guid.NewGuid().ToString("N"));
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(index, "context.db", "context-ws", root));
        var tool = new ContextTool(provider);

        string output = tool.Context(
            string.Empty,
            max_hops: 0,
            entry_symbols: ["Run"]);

        Assert.Contains("## anchor diagnostics", output, StringComparison.Ordinal);
        Assert.DoesNotContain("## ignored anchors", output, StringComparison.Ordinal);
        Assert.Contains("reason=ambiguous_truncated", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Context_CompactReportsExactAmbiguousLimitWithoutTruncation()
    {
        IndexedSymbol[] matches = Enumerable.Range(0, ContextTool.AnchorAmbiguousMatchLimit)
            .Select(i => new IndexedSymbol(
                i,
                (11_000 + i).ToString("x32"),
                "Run",
                "void Run()",
                "method",
                "csharp",
                $"src/Run{i}.cs",
                1,
                2,
                null,
                false))
            .ToArray();
        var index = MillerRepositoryIndex.Build(matches);
        string root = Path.Combine(Path.GetTempPath(), "miller-context-" + Guid.NewGuid().ToString("N"));
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(index, "context.db", "context-ws", root));
        var tool = new ContextTool(provider);

        string output = tool.Context(
            string.Empty,
            max_hops: 0,
            entry_symbols: ["Run"]);

        Assert.Contains("reason=ambiguous", output, StringComparison.Ordinal);
        Assert.DoesNotContain("reason=ambiguous_truncated", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Context_JsonReportsTruncatedFailingTestAnchors()
    {
        IndexedSymbol[] symbols = Enumerable.Range(0, 30)
            .Select(i => new IndexedSymbol(
                i,
                (20_000 + i).ToString("x32"),
                $"Failure{i}",
                $"void Failure{i}()",
                "method",
                "csharp",
                $"tests/Failure{i}.cs",
                1,
                2,
                null,
                true))
            .ToArray();
        var index = MillerRepositoryIndex.Build(symbols);
        string root = Path.Combine(Path.GetTempPath(), "miller-context-" + Guid.NewGuid().ToString("N"));
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(index, "context.db", "context-ws", root));
        var tool = new ContextTool(provider);

        string output = tool.Context(
            string.Empty,
            max_hops: 0,
            failing_test: string.Join(' ', symbols.Select(static symbol => symbol.Name)),
            format: "json");

        using var document = JsonDocument.Parse(output);
        Assert.Contains(
            document.RootElement.GetProperty("anchor_diagnostics").EnumerateArray(),
            diagnostic =>
                diagnostic.GetProperty("kind").GetString() == "failing_test" &&
                diagnostic.GetProperty("reason").GetString() == "truncated");
    }

    [Fact]
    public void Context_EmptyAfterIgnoredAnchorIncludesRecoveryAction()
    {
        var (index, _) = BuildFixture();
        string root = Path.Combine(Path.GetTempPath(), "miller-context-" + Guid.NewGuid().ToString("N"));
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(index, "context.db", "context-ws", root));
        var tool = new ContextTool(provider);

        string output = tool.Context(
            string.Empty,
            entry_symbols: ["MissingAnchor"],
            format: "json");

        using var document = JsonDocument.Parse(output);
        Assert.Empty(document.RootElement.GetProperty("bundle").EnumerateArray());
        Assert.Equal(
            "not_found",
            document.RootElement
                .GetProperty("anchor_diagnostics")[0]
                .GetProperty("reason")
                .GetString());
        Assert.False(document.RootElement.TryGetProperty("next_actions", out _));
        Assert.NotEmpty(
            document.RootElement.GetProperty("diagnostic").GetProperty("next_actions").EnumerateArray());
    }

    [Fact]
    public void Context_ReferenceModeUsage_SeparatesWorkspaceFallbackReferencesAndCallees()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        fx.ExecuteWrite("UPDATE identifiers SET target_symbol_id = NULL;");
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
            item.GetProperty("reason").GetString() == "entry_symbol"
            && item.GetProperty("role").GetString() == "pivot"
            && item.GetProperty("symbol_id").GetString() == JulieDbFixture.GetUserId);
        Assert.Contains(bundle.EnumerateArray(), item =>
            item.GetProperty("reason").GetString() == "possible_reference"
            && item.GetProperty("confidence").GetString() == "fallback"
            && item.GetProperty("file").GetString() == "web/Controller.cs");
        Assert.Contains(bundle.EnumerateArray(), item =>
            item.GetProperty("reason").GetString() == "unresolved_callee"
            && item.GetProperty("confidence").GetString() == "fallback"
            && item.GetProperty("name").GetString() == "Find");
    }

    [Fact]
    public void Context_McpRoute_ClampsLargeRequestedTokenBudget()
    {
        IndexedSymbol[] symbols = Enumerable.Range(0, 50)
            .Select(i => new IndexedSymbol(
                i,
                $"{i + 1:x32}",
                $"Widget{i}",
                $"public sealed class Widget{i}<{new string('X', 900)}>",
                "class",
                "csharp",
                $"src/Widget{i}.cs",
                1,
                20,
                null,
                false))
            .ToArray();
        var index = MillerRepositoryIndex.Build(symbols);
        string root = Path.Combine(Path.GetTempPath(), "miller-context-" + Guid.NewGuid().ToString("N"));
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(index, "context.db", "context-ws", root));
        var tool = new ContextTool(provider);

        string output = tool.Context(
            "Widget",
            token_budget: 100000,
            entry_symbols: symbols.Select(static symbol => symbol.SymbolId).ToArray(),
            format: "json");

        Assert.NotEqual("{}", output);
        Assert.True(TokenEstimator.Count(output) <= ToolOutputBudget.ContextMcpMaxTokens);
    }

    [Fact]
    public void Context_BudgetExcludedCandidatesUseBudgetDiagnostic()
    {
        var symbol = new IndexedSymbol(
            0,
            "00000000000000000000000000000301",
            "OversizedEntry",
            "class OversizedEntry<" + new string('T', 2000) + ">",
            "class",
            "csharp",
            "src/" + new string('p', 2000) + ".cs",
            1,
            20,
            null,
            false);
        var index = MillerRepositoryIndex.Build([symbol]);
        string root = Path.Combine(Path.GetTempPath(), "miller-context-" + Guid.NewGuid().ToString("N"));
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(index, "context.db", "context-ws", root));
        var tool = new ContextTool(provider);

        string output = tool.Context(
            string.Empty,
            token_budget: 300,
            max_hops: 0,
            entry_symbols: ["MissingEntry", "OversizedEntry"],
            format: "json");

        using var document = JsonDocument.Parse(output);
        Assert.Equal(
            "context_budget_exhausted",
            document.RootElement.GetProperty("diagnostic").GetProperty("code").GetString());
        JsonElement ignored = Assert.Single(
            document.RootElement.GetProperty("anchor_diagnostics").EnumerateArray());
        Assert.Equal("MissingEntry", ignored.GetProperty("value").GetString());
        Assert.Equal("not_found", ignored.GetProperty("reason").GetString());
    }

    [Fact]
    public void Context_ZeroJsonBudgetReturnsNoBytes()
    {
        var (index, _) = BuildFixture();
        string root = Path.Combine(Path.GetTempPath(), "miller-context-" + Guid.NewGuid().ToString("N"));
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(index, "context.db", "context-ws", root));
        var tool = new ContextTool(provider);

        string output = tool.Context(
            "OrderService",
            token_budget: 0,
            entry_symbols: ["OrderService"],
            format: "json");

        Assert.Empty(output);
    }

    [Fact]
    public void Context_ZeroCompactBudgetReturnsNoBytes()
    {
        var (index, _) = BuildFixture();
        string root = Path.Combine(Path.GetTempPath(), "miller-context-" + Guid.NewGuid().ToString("N"));
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(index, "context.db", "context-ws", root));
        var tool = new ContextTool(provider);

        string output = tool.Context(
            "order processing",
            token_budget: 0,
            entry_symbols: ["OrderService"]);

        Assert.Empty(output);
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
    public void Context_PivotBodyMakesBundleSufficientAndSuppressesInspectNudge()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var index = MillerRepositoryIndex.Build(SqliteSymbolReader.Read(fx.DbPath));
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(index, fx.DbPath, "current-ws", fx.WorkspaceRoot));
        var tool = new ContextTool(provider);

        string output = tool.Context(
            "GetUser",
            entry_symbols: ["GetUser"],
            max_hops: 0,
            token_budget: 2000);

        Assert.Contains("## implementations", output, StringComparison.Ordinal);
        Assert.Contains("GetUser", output, StringComparison.Ordinal);
        Assert.Contains(
            "evidence=sufficient  reason=pivot_implementation_present",
            output,
            StringComparison.Ordinal);
        Assert.DoesNotContain("## next inspect", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Context_SemanticSeedAnchorsConceptualQueryWhenServed()
    {
        var (index, _) = BuildFixture();
        string root = Path.Combine(Path.GetTempPath(), "miller-context-" + Guid.NewGuid().ToString("N"));
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(index, "context.db", "context-ws", root));
        var arm = new RecordingContextSemanticArm(
            new SemanticQueryResult(
                [new SemanticHit(RepoId, null, "src/OrderRepo.cs", 1, 0.91)],
                UnavailableReason: null));
        var tool = new ContextTool(provider, arm, new VectorSidecar(SemanticMode.On));

        string output = tool.Context(
            "durable persistence boundary",
            max_hops: 0,
            format: "json");

        using var document = JsonDocument.Parse(output);
        JsonElement first = document.RootElement.GetProperty("bundle")[0];
        Assert.Equal("OrderRepo", first.GetProperty("name").GetString());
        Assert.Equal("semantic_rank_1", first.GetProperty("reason").GetString());
        Assert.Equal(1, arm.SymbolCalls);
    }

    [Fact]
    public void Context_ExcludeTestsDoesNotChangeReferenceModeOff()
    {
        var symbol = new IndexedSymbol(
            0,
            "00000000000000000000000000000303",
            "PathOnlyTest",
            "class PathOnlyTest",
            "class",
            "csharp",
            "tests/PathOnlyTest.cs",
            1,
            20,
            null,
            IsTest: false);
        var index = MillerRepositoryIndex.Build([symbol]);
        string root = Path.Combine(Path.GetTempPath(), "miller-context-" + Guid.NewGuid().ToString("N"));
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(index, "context.db", "context-ws", root));
        var arm = new RecordingContextSemanticArm(
            new SemanticQueryResult(
                [new SemanticHit(symbol.SymbolId, null, symbol.FilePath, 1, 0.91)],
                UnavailableReason: null));
        var tool = new ContextTool(provider, arm, new VectorSidecar(SemanticMode.On));

        string output = tool.Context(
            "durable persistence boundary",
            exclude_tests: true,
            format: "json");

        using var document = JsonDocument.Parse(output);
        Assert.Contains(
            document.RootElement.GetProperty("bundle").EnumerateArray(),
            item => item.GetProperty("name").GetString() == "PathOnlyTest");
        Assert.Equal(1, arm.SymbolCalls);
    }

    [Fact]
    public void Context_ExcludeTestsFiltersSemanticSeedsInUsageMode()
    {
        var symbol = new IndexedSymbol(
            0,
            "00000000000000000000000000000303",
            "PathOnlyTest",
            "class PathOnlyTest",
            "class",
            "csharp",
            "tests/PathOnlyTest.cs",
            1,
            20,
            null,
            IsTest: false);
        var index = MillerRepositoryIndex.Build([symbol]);
        string root = Path.Combine(Path.GetTempPath(), "miller-context-" + Guid.NewGuid().ToString("N"));
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(index, "context.db", "context-ws", root));
        var arm = new RecordingContextSemanticArm(
            new SemanticQueryResult(
                [new SemanticHit(symbol.SymbolId, null, symbol.FilePath, 1, 0.91)],
                UnavailableReason: null));
        var tool = new ContextTool(provider, arm, new VectorSidecar(SemanticMode.On));

        string output = tool.Context(
            "durable persistence boundary",
            exclude_tests: true,
            format: "json",
            reference_mode: "usage");

        using var document = JsonDocument.Parse(output);
        Assert.Empty(document.RootElement.GetProperty("bundle").EnumerateArray());
        Assert.Equal(1, arm.SymbolCalls);
    }

    [Fact]
    public void NamedAnchorCandidatesBoundTokensAndMatches()
    {
        IndexedSymbol[] uniqueSymbols = Enumerable.Range(0, 100)
            .Select(i => new IndexedSymbol(
                i,
                (200 + i).ToString("x32"),
                $"Token{i}",
                $"void Token{i}()",
                "method",
                "csharp",
                $"src/Token{i}.cs",
                1,
                2,
                null,
                false))
            .ToArray();
        var uniqueIndex = MillerRepositoryIndex.Build(uniqueSymbols);
        string manyTokens = string.Join(' ', uniqueSymbols.Select(static symbol => symbol.Name));

        IReadOnlyList<IndexedSymbol> tokenBound =
            ContextTool.FindNamedAnchorCandidates(uniqueIndex, manyTokens, out bool tokensTruncated);

        Assert.Equal(ContextTool.AnchorIdentifierTokenLimit, tokenBound.Count);
        Assert.True(tokensTruncated);

        IndexedSymbol[] homonyms = Enumerable.Range(0, 20)
            .Select(i => new IndexedSymbol(
                i,
                (500 + i).ToString("x32"),
                "Run",
                "void Run()",
                "method",
                "csharp",
                $"src/Run{i}.cs",
                1,
                2,
                null,
                false))
            .ToArray();
        var homonymIndex = MillerRepositoryIndex.Build(homonyms);

        IReadOnlyList<IndexedSymbol> matchBound =
            ContextTool.FindNamedAnchorCandidates(homonymIndex, "Run", out bool matchesTruncated);

        Assert.Equal(ContextTool.AnchorMatchesPerToken, matchBound.Count);
        Assert.True(matchesTruncated);
    }

    [Fact]
    public void NamedAnchorCandidates_ExactBoundsAreNotTruncated()
    {
        IndexedSymbol[] uniqueSymbols = Enumerable.Range(0, ContextTool.AnchorIdentifierTokenLimit)
            .Select(i => new IndexedSymbol(
                i,
                (700 + i).ToString("x32"),
                $"Token{i}",
                $"void Token{i}()",
                "method",
                "csharp",
                $"src/Token{i}.cs",
                1,
                2,
                null,
                false))
            .ToArray();
        var uniqueIndex = MillerRepositoryIndex.Build(uniqueSymbols);

        IReadOnlyList<IndexedSymbol> tokenBound = ContextTool.FindNamedAnchorCandidates(
            uniqueIndex,
            string.Join(' ', uniqueSymbols.Select(static symbol => symbol.Name)),
            out bool tokensTruncated);

        Assert.Equal(ContextTool.AnchorIdentifierTokenLimit, tokenBound.Count);
        Assert.False(tokensTruncated);

        IndexedSymbol[] homonyms = Enumerable.Range(0, ContextTool.AnchorMatchesPerToken)
            .Select(i => new IndexedSymbol(
                i,
                (800 + i).ToString("x32"),
                "Run",
                "void Run()",
                "method",
                "csharp",
                $"src/Run{i}.cs",
                1,
                2,
                null,
                false))
            .ToArray();
        var homonymIndex = MillerRepositoryIndex.Build(homonyms);

        IReadOnlyList<IndexedSymbol> matchBound =
            ContextTool.FindNamedAnchorCandidates(homonymIndex, "Run", out bool matchesTruncated);

        Assert.Equal(ContextTool.AnchorMatchesPerToken, matchBound.Count);
        Assert.False(matchesTruncated);
    }

    [Fact]
    public void ParseStackFrames_BoundsFrameAnchors()
    {
        string stackTrace = string.Join(
            '\n',
            Enumerable.Range(1, 100).Select(i => $"at Run{i} (src/File{i}.cs:{i})"));

        IReadOnlyList<(string File, int Line)> frames =
            ContextTool.ParseStackFrames(stackTrace, out bool truncated);

        Assert.Equal(ContextTool.AnchorStackFrameLimit, frames.Count);
        Assert.Equal(("src/File1.cs", 1), frames[0]);
        Assert.True(truncated);
    }

    [Fact]
    public void ParseStackFrames_ExactFrameLimitIsNotTruncated()
    {
        string stackTrace = string.Join(
            '\n',
            Enumerable.Range(1, ContextTool.AnchorStackFrameLimit)
                .Select(i => $"at Run{i} (src/File{i}.cs:{i})"));

        IReadOnlyList<(string File, int Line)> frames =
            ContextTool.ParseStackFrames(stackTrace, out bool truncated);

        Assert.Equal(ContextTool.AnchorStackFrameLimit, frames.Count);
        Assert.False(truncated);
    }

    [Fact]
    public void ParseStackFrames_PythonFrameAfterDotnetLimitReportsTruncation()
    {
        string dotnetFrames = string.Join(
            '\n',
            Enumerable.Range(1, ContextTool.AnchorStackFrameLimit)
                .Select(i => $"at Run{i} (src/File{i}.cs:{i})"));
        string stackTrace = dotnetFrames + "\nFile \"src/python_file.py\", line 25";

        IReadOnlyList<(string File, int Line)> frames =
            ContextTool.ParseStackFrames(stackTrace, out bool truncated);

        Assert.Equal(ContextTool.AnchorStackFrameLimit, frames.Count);
        Assert.True(truncated);
    }

    [Fact]
    public void ParseStackFrames_PreservesMixedLanguageTextOrder()
    {
        string stackTrace =
            "File \"src/first.py\", line 4\n" +
            "at Run (src/second.cs:8)\n" +
            "File \"src/third.py\", line 12";

        IReadOnlyList<(string File, int Line)> frames =
            ContextTool.ParseStackFrames(stackTrace, out bool truncated);

        Assert.Equal(
            [("src/first.py", 4), ("src/second.cs", 8), ("src/third.py", 12)],
            frames);
        Assert.False(truncated);
    }

    [Fact]
    public void Context_CompactReportsTruncatedStackAnchorsWithoutClaimingNoMatch()
    {
        var index = MillerRepositoryIndex.Build(Array.Empty<IndexedSymbol>());
        string root = Path.Combine(Path.GetTempPath(), "miller-context-" + Guid.NewGuid().ToString("N"));
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(index, "context.db", "context-ws", root));
        var tool = new ContextTool(provider);
        string stackTrace = string.Join(
            '\n',
            Enumerable.Range(1, ContextTool.AnchorStackFrameLimit + 1)
                .Select(i => $"at Unknown{i} (src/Missing{i}.cs:{i})"));

        string output = tool.Context(
            string.Empty,
            max_hops: 0,
            stack_trace: stackTrace);

        Assert.Contains("reason=no_frame_match_truncated", output, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"reason=no_frame_match(?:\r?\n|$)", output);
    }

    [Fact]
    public void Context_CompactReportsMatchedTruncatedStackAnchors()
    {
        var symbol = new IndexedSymbol(
            0,
            "00000000000000000000000000000404",
            "Run1",
            "void Run1()",
            "method",
            "csharp",
            "src/File1.cs",
            1,
            2,
            null,
            false);
        var index = MillerRepositoryIndex.Build([symbol]);
        string root = Path.Combine(Path.GetTempPath(), "miller-context-" + Guid.NewGuid().ToString("N"));
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(index, "context.db", "context-ws", root));
        var tool = new ContextTool(provider);
        string stackTrace = string.Join(
            '\n',
            Enumerable.Range(1, ContextTool.AnchorStackFrameLimit + 1)
                .Select(i => $"at Run{i} (src/File{i}.cs:{i})"));

        string output = tool.Context(
            string.Empty,
            max_hops: 0,
            stack_trace: stackTrace);

        Assert.Contains("reason=frames_truncated", output, StringComparison.Ordinal);
        Assert.Contains("reason=symbols_truncated", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Context_CompactReportsTruncatedFailingTestWithoutClaimingCompleteNoMatch()
    {
        var index = MillerRepositoryIndex.Build(Array.Empty<IndexedSymbol>());
        string root = Path.Combine(Path.GetTempPath(), "miller-context-" + Guid.NewGuid().ToString("N"));
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(index, "context.db", "context-ws", root));
        var tool = new ContextTool(provider);
        string failingTest = string.Join(' ', Enumerable.Range(1, 30).Select(i => $"Unknown{i}"));

        string output = tool.Context(
            string.Empty,
            max_hops: 0,
            failing_test: failingTest);

        Assert.Contains("reason=no_symbol_match_truncated", output, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"reason=no_symbol_match(?:\r?\n|$)", output);
    }

    [Fact]
    public void ExtractIdentifierTokens_DeduplicatesBeforeAnchorLimit()
    {
        string hint = string.Join(' ', Enumerable.Repeat("Repeated Unique", 30));

        string[] tokens = ContextTool.ExtractIdentifierTokens(hint).ToArray();

        Assert.Equal(["Repeated", "Unique"], tokens);
    }

    [Fact]
    public void Truncate_DoesNotSplitSurrogatePairs()
    {
        string output = ContextTool.Truncate("abc😀xyz", 5);

        Assert.Equal("abc…", output);
        Assert.DoesNotContain(output.EnumerateRunes(), static rune => rune.Value == 0xFFFD);
    }

    [Fact]
    public void Truncate_NonPositiveLimitReturnsEmpty()
    {
        Assert.Empty(ContextTool.Truncate("value", -1));
        Assert.Empty(ContextTool.Truncate(string.Empty, 0));
        Assert.Empty(ContextTool.Truncate("value", 0));
    }

    [Fact]
    public void Truncate_OneCharacterLimitReturnsEllipsis()
    {
        Assert.Equal("…", ContextTool.Truncate("value", 1));
    }

    [Fact]
    public void Truncate_SurrogatePairAtStartReturnsOnlyEllipsis()
    {
        Assert.Equal("…", ContextTool.Truncate("😀x", 2));
    }

    [Fact]
    public void Context_SemanticSeedConsumptionHonorsRequestedBound()
    {
        var (index, _) = BuildFixture();
        string root = Path.Combine(Path.GetTempPath(), "miller-context-" + Guid.NewGuid().ToString("N"));
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(index, "context.db", "context-ws", root));
        SemanticHit[] hits =
        [
            .. Enumerable.Range(1, 10)
                .Select(rank => new SemanticHit(RepoId, null, "src/OrderRepo.cs", rank, 0.91)),
            new SemanticHit(ServiceId, null, "src/OrderService.cs", 11, 0.90),
        ];
        var arm = new RecordingContextSemanticArm(
            new SemanticQueryResult(hits, UnavailableReason: null));
        var tool = new ContextTool(provider, arm, new VectorSidecar(SemanticMode.On));

        string output = tool.Context(
            "durable persistence boundary",
            max_hops: 0,
            format: "json");

        using var document = JsonDocument.Parse(output);
        JsonElement bundle = document.RootElement.GetProperty("bundle");
        Assert.Contains(bundle.EnumerateArray(), item => item.GetProperty("name").GetString() == "OrderRepo");
        Assert.DoesNotContain(bundle.EnumerateArray(), item => item.GetProperty("name").GetString() == "OrderService");
    }

    [Fact]
    public void Context_SemanticOffPerformsZeroSemanticWork()
    {
        var (index, _) = BuildFixture();
        string root = Path.Combine(Path.GetTempPath(), "miller-context-" + Guid.NewGuid().ToString("N"));
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(index, "context.db", "context-ws", root));
        var arm = new RecordingContextSemanticArm(
            new SemanticQueryResult(
                [new SemanticHit(RepoId, null, "src/OrderRepo.cs", 1, 0.91)],
                UnavailableReason: null));
        var tool = new ContextTool(provider, arm, new VectorSidecar(SemanticMode.Off));

        string output = tool.Context("OrderService", format: "json");
        string repeated = tool.Context("OrderService", format: "json");

        Assert.Contains("OrderService", output, StringComparison.Ordinal);
        Assert.Equal(output, repeated);
        Assert.Equal(0, arm.SymbolCalls);
    }

    [Fact]
    public void Context_ExactIdentifierPolicySkipsSemanticArm()
    {
        var (index, _) = BuildFixture();
        string root = Path.Combine(Path.GetTempPath(), "miller-context-" + Guid.NewGuid().ToString("N"));
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(index, "context.db", "context-ws", root));
        var arm = new RecordingContextSemanticArm(
            new SemanticQueryResult(
                [new SemanticHit(RepoId, null, "src/OrderRepo.cs", 1, 0.91)],
                UnavailableReason: null));
        var tool = new ContextTool(provider, arm, new VectorSidecar(SemanticMode.On));

        string output = tool.Context("OrderService", format: "json");

        Assert.Contains("OrderService", output, StringComparison.Ordinal);
        Assert.Equal(0, arm.SymbolCalls);
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

    private sealed class RecordingContextSemanticArm(SemanticQueryResult result) : ISemanticTextArm
    {
        public int SymbolCalls { get; private set; }

        public SemanticQueryResult QuerySymbols(
            string workspaceRoot,
            string query,
            int k,
            Func<VectorMatch, bool>? allow)
        {
            SymbolCalls++;
            return result;
        }

        public SemanticQueryResult QueryChunks(string workspaceRoot, string query, int k) =>
            SemanticQueryResult.Unavailable("not configured");
    }
}
