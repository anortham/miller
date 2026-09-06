using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Miller.Core.Graph;
using Miller.Core.Search;
using Miller.Indexing;
using Miller.Indexing.Semantic;
using Miller.Server.Resolution;
using Miller.Server.Tools;
using Miller.Server.Workspaces;
using Miller.Tests.Indexing;
using Xunit;

namespace Miller.Tests.Server;

public sealed partial class ContextToolTests
{
    private const string ContextCharacterizationFixtureId = "context-tool-public-boundary-v1";
    private const string ContextCharacterizationBaseCommit = "a2979482a10f160cc092651fbf320a8aaff209da";

    [Fact]
    public void Public_context_outputs_match_pre_move_goldens()
    {
        ContextGoldenSet goldenSet = LoadContextGoldens();

        Assert.Equal(ContextCharacterizationFixtureId, goldenSet.FixtureId);
        Assert.Equal(ContextCharacterizationBaseCommit, goldenSet.BaseCommit);
        Assert.Equal(16, goldenSet.Cases.Count);

        foreach (ContextGolden golden in goldenSet.Cases)
        {
            string actual = InvokeCharacterizationCase(golden.Case, golden.Format);

            Assert.Equal(golden.Output, actual);
            Assert.Equal(golden.CharacterCount, actual.Length);
            Assert.Equal(golden.Utf8ByteCount, Encoding.UTF8.GetByteCount(actual));
            Assert.Equal(golden.Sha256, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(actual))));
            if (golden.Format == "json")
                AssertJsonCharacterization(golden.Case, actual);
        }
    }

    [Fact]
    public void Public_context_reuses_term_rescue_retrievals_with_exact_cold_and_warm_counts()
    {
        var (index, _) = BuildFixture();
        var measured = new MeasuredSymbolLookupIndex(index);
        var readTelemetry = new ReadPhaseTelemetry(measured, graph: null, providerCacheEntries: 0);
        WorkspaceReadContext context = ReadToolRoutingTestSupport.ContextFor(
            index, "context.db", "context-ws", "/repo") with
        {
            Index = measured,
            Resolver = new SmartTargetResolver(measured),
            ReadTelemetry = readTelemetry,
        };
        var observations = new List<ContextLookupPhaseObservation>();
        var tool = new ContextTool(
            new RecordingWorkspaceIndexProvider(context),
            semanticArm: null,
            semanticSidecar: null,
            lookupPhaseObserver: observations.Add);

        _ = tool.Context(
            "how family store read context resolves symbols and graph",
            token_budget: 1200,
            max_hops: 1,
            entry_symbols: ["WorkspaceIndexProvider"]);

        Assert.Equal(
            [
                ContextLookupPhase.SourceRescue,
                ContextLookupPhase.QueryRetrieval,
                ContextLookupPhase.TermRetrieval,
                ContextLookupPhase.AnchorResolution,
            ],
            observations.Select(static observation => observation.Phase));

        ContextLookupPhaseObservation query = observations[1];
        Assert.Equal(3, query.SearchDelta.TotalCallCount);
        Assert.Equal(1, query.SearchDelta.FirstQuery.CallCount);
        Assert.Equal(0, query.SearchDelta.CacheHit.CallCount);

        ContextLookupPhaseObservation coldTermRescue = observations[2];
        Assert.Equal(16, coldTermRescue.SearchDelta.TotalCallCount);
        Assert.Equal(8, coldTermRescue.SearchDelta.FirstQuery.CallCount);
        Assert.Equal(0, coldTermRescue.SearchDelta.CacheHit.CallCount);
        Assert.Equal(19, coldTermRescue.SearchTotal.TotalCallCount);

        ContextLookupPhaseObservation warmAnchorResolution = observations[3];
        Assert.Equal(17, warmAnchorResolution.SearchDelta.TotalCallCount);
        Assert.Equal(1, warmAnchorResolution.SearchDelta.FirstQuery.CallCount);
        Assert.Equal(16, warmAnchorResolution.SearchDelta.CacheHit.CallCount);
        Assert.Equal(36, warmAnchorResolution.SearchTotal.TotalCallCount);
    }

    [Fact]
    public void Public_context_source_rescue_reads_the_content_index_once()
    {
        var fixture = SourceRescueTool();

        string output = fixture.Tool.Context(
            "how does a derived sidecar prove which extract generation it was built from",
            token_budget: 900,
            max_hops: 0,
            format: "json");

        Assert.Equal(1, fixture.Provider.TextContentSearchResolveCount);
        Assert.Equal(1, fixture.Content.SearchCalls);
        using JsonDocument document = JsonDocument.Parse(output);
        Assert.Equal(
            "source_rescue_1",
            document.RootElement.GetProperty("bundle")[0].GetProperty("reason").GetString());
    }

    [Fact]
    public void Public_context_semantic_off_performs_zero_semantic_work()
    {
        var fixture = SemanticOffTool();

        _ = fixture.Tool.Context(
            "durable persistence boundary",
            token_budget: 700,
            max_hops: 0,
            format: "json");

        Assert.Equal(0, fixture.Arm.SymbolCalls);
    }

    [Fact]
    public void Public_context_cancellation_reports_only_completed_phases_in_order()
    {
        var (index, _) = BuildFixture();
        var measured = new MeasuredSymbolLookupIndex(index);
        var readTelemetry = new ReadPhaseTelemetry(measured, graph: null, providerCacheEntries: 0);
        WorkspaceReadContext context = ReadToolRoutingTestSupport.ContextFor(
            index, "context.db", "context-ws", "/repo") with
        {
            Index = measured,
            Resolver = new SmartTargetResolver(measured),
            ReadTelemetry = readTelemetry,
        };
        using var cancellation = new CancellationTokenSource();
        var phases = new List<string>();
        var lookupPhases = new List<ContextLookupPhaseObservation>();
        var tool = new ContextTool(
            new RecordingWorkspaceIndexProvider(context),
            semanticArm: null,
            semanticSidecar: null,
            phaseObserver: phases.Add,
            lookupPhaseObserver: observation =>
            {
                lookupPhases.Add(observation);
                if (observation.Phase == ContextLookupPhase.AnchorResolution)
                    cancellation.Cancel();
            });

        Assert.Throws<OperationCanceledException>(() => tool.ContextWithCancellation(
            "OrderService",
            token_budget: 1200,
            max_hops: 1,
            cancellationToken: cancellation.Token));

        Assert.Equal(
            [
                "resolve",
                "semantic_seeds",
                "source_rescue",
                "query_retrieval",
                "term_retrieval",
                "anchor_resolution",
                "pivot_ranking",
            ],
            phases);
        Assert.Equal(
            [
                ContextLookupPhase.SourceRescue,
                ContextLookupPhase.QueryRetrieval,
                ContextLookupPhase.TermRetrieval,
                ContextLookupPhase.AnchorResolution,
            ],
            lookupPhases.Select(static observation => observation.Phase));
    }

    private static string InvokeCharacterizationCase(string caseName, string format) =>
        caseName switch
        {
            "ordinary" => CallPublic(
                FixtureTool(BuildFixture().index), format, string.Empty, 700, 0, ["OrderService"]),
            "actionable" => CallPublic(
                FixtureTool(BuildFixture().index), format, string.Empty, 1000, 1, ["OrderService"]),
            "source-rescue" => CallPublic(
                SourceRescueTool().Tool,
                format,
                "how does a derived sidecar prove which extract generation it was built from",
                900,
                0),
            "reference-aware" => ReferenceAwareOutput(format),
            "semantic-off" => CallPublic(
                SemanticOffTool().Tool, format, "durable persistence boundary", 700, 0),
            "budget-bounded" => CallPublic(
                FixtureTool(BuildRenderBudgetFixture().index), format, "BudgetRoot", 256, 1),
            "empty" => CallPublic(FixtureTool(EmptyIndex()), format, "   ", 500, 1),
            "ambiguous" => CallPublic(
                FixtureTool(AmbiguousFixture()), format, string.Empty, 700, 0, ["DuplicateService"]),
            _ => throw new InvalidOperationException(caseName),
        };

    private static string CallPublic(
        ContextTool tool,
        string format,
        string query,
        int tokenBudget,
        int maxHops,
        string[]? entrySymbols = null,
        string referenceMode = "off") =>
        format == "compact"
            ? tool.Context(
                query,
                token_budget: tokenBudget,
                max_hops: maxHops,
                entry_symbols: entrySymbols,
                format: format,
                reference_mode: referenceMode)
            : tool.ContextWithCancellation(
                query,
                token_budget: tokenBudget,
                max_hops: maxHops,
                entry_symbols: entrySymbols,
                format: format,
                reference_mode: referenceMode,
                cancellationToken: CancellationToken.None);

    private static ContextTool FixtureTool(MillerRepositoryIndex index)
    {
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(index, "context.db", "context-ws", "/repo"));
        return new ContextTool(provider);
    }

    private static (ContextTool Tool, CountingTextContentSearchIndex Content, RecordingWorkspaceIndexProvider Provider)
        SourceRescueTool()
    {
        const string correctId = "0000000000000000000000000000b102";
        MillerRepositoryIndex index = MillerRepositoryIndex.Build(
        [
            new IndexedSymbol(0, "0000000000000000000000000000b101", "SidecarExtract", "class SidecarExtract", "class", "csharp",
                "src/SidecarExtract.cs", 1, 40, null, false),
            new IndexedSymbol(1, correctId, "SymbolsArtifactIdentity", "class SymbolsArtifactIdentity", "class", "csharp",
                "src/SymbolsArtifactIdentity.cs", 1, 80, null, false),
        ]);
        var content = new CountingTextContentSearchIndex(
            SourceHit(
                "src/SymbolsArtifactIdentity.cs",
                20,
                "derived sidecar proves which extract generation it was built from",
                sourceId: "src-b1",
                chunkId: "chunk-b1",
                containingSymbolId: correctId,
                containingSymbolName: "SymbolsArtifactIdentity"));
        WorkspaceReadContext readContext = ReadToolRoutingTestSupport.ContextFor(
            index, "context.db", "context-ws", "/repo");
        var provider = new RecordingWorkspaceIndexProvider(
            readContext,
            ReadToolRoutingTestSupport.TextContentContextFor(content, "content.db", "context-ws", "/repo"),
            []);
        return (new ContextTool(provider), content, provider);
    }

    private static string ReferenceAwareOutput(string format)
    {
        using var fixture = JulieDbFixture.CreateForInspect();
        WriteContentChunk(
            ContentCorpusSidecar.ContentDbPathFor(fixture.DbPath),
            chunkId: "chunk-get-user",
            path: "auth/UserService.cs",
            rawText: "public User GetUser(int id)\n{\n    return _repo.Find(id);\n}",
            containingSymbolId: JulieDbFixture.GetUserId,
            containingSymbolName: "GetUser",
            symbolsDbPath: fixture.DbPath);
        MillerRepositoryIndex index = MillerRepositoryIndex.Build(SqliteSymbolReader.Read(fixture.DbPath));
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(index, fixture.DbPath, "context-ws", fixture.WorkspaceRoot));
        return CallPublic(
            new ContextTool(provider),
            format,
            "zzz no lexical match zzz",
            1000,
            0,
            ["GetUser"],
            "usage");
    }

    private static (ContextTool Tool, RecordingContextSemanticArm Arm) SemanticOffTool()
    {
        MillerRepositoryIndex index = BuildFixture().index;
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(index, "context.db", "context-ws", "/repo"));
        var arm = new RecordingContextSemanticArm(
            new SemanticQueryResult(
                [new SemanticHit(RepoId, null, "src/OrderRepo.cs", 1, 0.91)],
                UnavailableReason: null));
        return (new ContextTool(provider, arm, new VectorSidecar(SemanticMode.Off)), arm);
    }

    private static MillerRepositoryIndex AmbiguousFixture() =>
        MillerRepositoryIndex.Build(
        [
            new IndexedSymbol(0, "0000000000000000000000000000f101", "DuplicateService", "class DuplicateService", "class", "csharp",
                "src/One.cs", 1, 20, null, false),
            new IndexedSymbol(1, "0000000000000000000000000000f102", "DuplicateService", "class DuplicateService", "class", "csharp",
                "src/Two.cs", 1, 20, null, false),
        ],
        Array.Empty<GraphEdge>());

    private static ContextGoldenSet LoadContextGoldens()
    {
        string path = Path.Combine(
            ScaleTestSupport.RepoRoot(),
            "tests",
            "Miller.Tests",
            "Fixtures",
            "ContextCharacterization",
            "public-boundary-v1.tsv");
        string[] lines = File.ReadAllLines(path);
        string fixtureId = lines[0].Split('\t')[1];
        string baseCommit = lines[1].Split('\t')[1];
        ContextGolden[] cases = lines
            .Skip(3)
            .Where(static line => line.Length > 0)
            .Select(static line =>
            {
                string[] fields = line.Split('\t');
                return new ContextGolden(
                    fields[0],
                    fields[1],
                    int.Parse(fields[2], System.Globalization.CultureInfo.InvariantCulture),
                    int.Parse(fields[3], System.Globalization.CultureInfo.InvariantCulture),
                    fields[4],
                    Encoding.UTF8.GetString(Convert.FromBase64String(fields[5])));
            })
            .ToArray();
        return new ContextGoldenSet(fixtureId, baseCommit, cases);
    }

    private static void AssertJsonCharacterization(string caseName, string output)
    {
        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement root = document.RootElement;
        string status = root.GetProperty("disposition").GetProperty("status").GetString()!;
        string[] pivots = root.GetProperty("bundle")
            .EnumerateArray()
            .Where(static item => item.TryGetProperty("role", out JsonElement role) && role.GetString() == "pivot")
            .Select(static item => item.GetProperty("name").GetString()!)
            .ToArray();

        switch (caseName)
        {
            case "ordinary":
                Assert.Equal("partial", status);
                Assert.Equal(["OrderService"], pivots);
                break;
            case "actionable":
                Assert.Equal("partial", status);
                Assert.Equal(["OrderService"], pivots);
                Assert.Equal(4, root.GetProperty("bundle").GetArrayLength());
                break;
            case "source-rescue":
                Assert.Equal("partial", status);
                Assert.Equal(["SymbolsArtifactIdentity", "SidecarExtract"], pivots);
                break;
            case "reference-aware":
                Assert.Equal("sufficient", status);
                Assert.Equal(["GetUser"], pivots);
                break;
            case "semantic-off":
            case "empty":
                Assert.Equal("insufficient", status);
                Assert.Empty(pivots);
                Assert.Equal("no_context_symbols", root.GetProperty("diagnostic").GetProperty("code").GetString());
                break;
            case "budget-bounded":
                Assert.Equal("partial", status);
                Assert.Equal(["BudgetRoot"], pivots);
                break;
            case "ambiguous":
                Assert.Equal("partial", status);
                Assert.Equal(["DuplicateService", "DuplicateService"], pivots);
                Assert.Equal(
                    "ambiguous",
                    root.GetProperty("anchor_diagnostics")[0].GetProperty("reason").GetString());
                break;
            default:
                throw new InvalidOperationException(caseName);
        }
    }

    private sealed class CountingTextContentSearchIndex(params TextContentSearchHit[] hits) : ITextContentSearchIndex
    {
        public int DocumentCount => hits.Length;

        public int SearchCalls { get; private set; }

        public IReadOnlyList<TextContentSearchHit> Search(
            string query,
            string contentKind,
            int limit = 10,
            bool excludeTests = false) =>
            Search(query, [contentKind], limit, excludeTests);

        public IReadOnlyList<TextContentSearchHit> Search(
            string query,
            IReadOnlyCollection<string> contentKinds,
            int limit = 10,
            bool excludeTests = false)
        {
            SearchCalls++;
            return hits
                .Where(hit => contentKinds.Contains(hit.ContentKind))
                .Where(hit => !excludeTests || !IsTestPath.Check(hit.Path ?? hit.DisplayPath))
                .Take(limit)
                .ToArray();
        }
    }

    private sealed record ContextGoldenSet(string FixtureId, string BaseCommit, IReadOnlyList<ContextGolden> Cases);

    private sealed record ContextGolden(
        string Case,
        string Format,
        int CharacterCount,
        int Utf8ByteCount,
        string Sha256,
        string Output);
}
