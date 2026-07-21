using System.Text.Json;
using Miller.Core.Search;
using Miller.Indexing;
using Miller.Indexing.Semantic;
using Miller.Server.Telemetry;
using Miller.Server.Tools;
using Miller.Tests.Support;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the P5 identifier shadow population (canary-telemetry-v1 §Shadow Population): the 10% sampling under the
/// non-inferiority experiment id, the serve-first forced-hybrid comparison and its overlap/top1/rank counters,
/// the timeout/error/skipped fault paths that record a status and nothing else, and the exact shadow key set —
/// over the same contract-faithful fake sidecar/store fixtures the hybrid-search suite uses, with no vec0.
/// </summary>
public sealed class CanaryShadowPopulationTests : IDisposable
{
    private const string Root = "/ws";
    private const string IdentifierQuery = "GadgetWidget";
    private const string SampledWorkspace = "ws-004";
    private const string UnsampledWorkspace = "ws-000";
    private const string UtcDate = "2026-07-20";

    private static readonly SearchRoute SymbolRoute = SearchRoutePlanner.Plan("symbol", regions: null);
    private static readonly SemanticEncoderPin Pin = MillerSemanticContract.DefaultEncoder;

    private readonly string _temp = Path.Combine(Path.GetTempPath(), "miller-canary-shadow-" + Guid.NewGuid());

    public CanaryShadowPopulationTests() => Directory.CreateDirectory(_temp);

    private static SemanticSessionOptions FastOptions => new()
    {
        RequestTimeout = TimeSpan.FromSeconds(10),
        InitTimeout = TimeSpan.FromSeconds(10),
        ShutdownTimeout = TimeSpan.FromSeconds(1),
        RestartBackoff = TimeSpan.Zero,
        RestartBackoffCap = TimeSpan.Zero,
        Delay = static (_, _) => Task.CompletedTask,
    };

    [Theory]
    [InlineData(SampledWorkspace, 0)]
    [InlineData("ws-007", 3)]
    [InlineData("ws-010", 1)]
    [InlineData("ws-026", 5)]
    [InlineData(UnsampledWorkspace, 51)]
    [InlineData("ws-002", 28)]
    public void IdentifierBucket_MatchesTheFrozenNoninferiorityDerivation(string workspaceId, int expected) =>
        Assert.Equal(
            expected,
            CanaryAssignment.Bucket(CanaryAssignment.IdentifierExperimentId, workspaceId, UtcDate, CanaryQueryClass.Identifier));

    [Fact]
    public void BucketBelowTen_UpgradesTheIdentifierRowToShadow()
    {
        RecordingSymbolLookupIndex index = ThreeSymbolIndex();
        var probe = new InvocationCounter();

        SearchTool.SymbolCanaryOutcome outcome = RunShadow(index, SampledWorkspace, probe.Wrap(ServedHybrid("sym-a")));

        Assert.Null(outcome.Facts);
        Assert.NotNull(outcome.ShadowFacts);
        Assert.Equal(1, probe.Count);

        JsonElement metadata = Stamp(outcome.ShadowFacts!);
        Assert.Equal(CanaryArm.Shadow, metadata.GetProperty("canary_arm").GetString());
        Assert.Equal(CanaryAssignment.IdentifierExperimentId, metadata.GetProperty("canary_experiment_id").GetString());
        Assert.Equal(CanaryQueryClass.Identifier, metadata.GetProperty("canary_query_class").GetString());
        Assert.Equal(CanaryEligibility.IneligibleQueryClass, metadata.GetProperty("canary_eligibility").GetString());
        Assert.Equal(0, metadata.GetProperty("canary_bucket").GetInt32());
    }

    [Fact]
    public void BucketAtOrAboveTen_StaysIneligibleAndNeverRunsShadowWork()
    {
        RecordingSymbolLookupIndex index = ThreeSymbolIndex();
        var probe = new InvocationCounter();

        SearchTool.SymbolCanaryOutcome outcome = RunShadow(index, UnsampledWorkspace, probe.Wrap(ServedHybrid("sym-a")));

        Assert.Null(outcome.ShadowFacts);
        Assert.NotNull(outcome.Facts);
        Assert.Equal(0, probe.Count);
        Assert.Equal(CanaryEligibility.IneligibleQueryClass, outcome.Facts!.Eligibility);
    }

    [Fact]
    public void CanaryOff_RunsNoShadowWorkAndRecordsNothing()
    {
        RecordingSymbolLookupIndex index = ThreeSymbolIndex();
        var probe = new InvocationCounter();

        SearchTool.SymbolCanaryOutcome outcome = SearchTool.RunSymbolsWithCanary(
            index, SymbolRoute, Request(IdentifierQuery), CanaryMode.Off, "symbol", semanticDisabled: false,
            SampledWorkspace, UtcDate, () => "ready", crossWorkspaceNoGeneration: false, treatmentArmFactory: null,
            probe.Wrap(ServedHybrid("sym-a")));

        Assert.Null(outcome.Facts);
        Assert.Null(outcome.ShadowFacts);
        Assert.Equal(0, probe.Count);
    }

    [Fact]
    public void SemanticDisabled_IsInertLikeCanaryOff_RunsNoShadowWorkAndRecordsNothing()
    {
        RecordingSymbolLookupIndex index = ThreeSymbolIndex();
        var probe = new InvocationCounter();

        SearchTool.SymbolCanaryOutcome outcome = SearchTool.RunSymbolsWithCanary(
            index, SymbolRoute, Request(IdentifierQuery), CanaryMode.On, "symbol", semanticDisabled: true,
            SampledWorkspace, UtcDate, () => throw new InvalidOperationException("probe must not run when semantic is off"),
            crossWorkspaceNoGeneration: false, treatmentArmFactory: null, probe.Wrap(ServedHybrid("sym-a")));

        Assert.Null(outcome.Facts);
        Assert.Null(outcome.ShadowFacts);
        Assert.Equal(0, probe.Count);
        Assert.Equal(LexicalOutput(index), outcome.Result.Output);
    }

    [Fact]
    public async Task ShadowOk_RecordsHandComputedOverlapTop1AndRankFromTheForcedHybrid()
    {
        RecordingSymbolLookupIndex index = ThreeSymbolIndex();
        var port = new RecordingPort
        {
            Identity = MillerSemanticContract.PinnedIdentity(Pin),
            Matches = [Match(1, 0.05, "sym-c", "src/Charlie.cs")],
        };
        await using SemanticEmbeddingSession session = NewSession();

        SearchTool.SymbolCanaryOutcome outcome = RunShadow(index, SampledWorkspace, RealShadow(port, session));

        CanaryShadowFacts facts = outcome.ShadowFacts!;
        Assert.Equal(CanaryShadowStatus.Ok, facts.Status);
        Assert.Equal(1, facts.SemanticResultCount);
        Assert.Equal(3, facts.OverlapAt10);
        Assert.True(facts.Top1Changed);
        Assert.Equal(2, facts.LexicalTop1Rank);
        Assert.Equal(MillerSemanticContract.PinnedIdentity(Pin).EncoderFingerprint, facts.EncoderFingerprint);
        Assert.Equal(MillerSemanticContract.PinnedIdentity(Pin).StorageSchema, facts.StorageSchema);
        Assert.Equal(MillerSemanticContract.PinnedIdentity(Pin).CorpusGeneration, facts.CorpusGeneration);

        Assert.Equal(LexicalOutput(index), outcome.Result.Output);
    }

    [Fact]
    public void ShadowOk_HybridEqualToLexical_RecordsFullOverlapUnchangedTop1AndRankOne()
    {
        RecordingSymbolLookupIndex index = ThreeSymbolIndex();

        SearchTool.SymbolCanaryOutcome outcome = RunShadow(
            index, SampledWorkspace, ServedHybrid("sym-a", "sym-b", "sym-c"));

        CanaryShadowFacts facts = outcome.ShadowFacts!;
        Assert.Equal(CanaryShadowStatus.Ok, facts.Status);
        Assert.Equal(3, facts.OverlapAt10);
        Assert.False(facts.Top1Changed);
        Assert.Equal(1, facts.LexicalTop1Rank);
    }

    [Fact]
    public void ShadowOk_ServedTop1AbsentFromHybridTop50_RecordsRankZero()
    {
        RecordingSymbolLookupIndex index = ThreeSymbolIndex();

        SearchTool.SymbolCanaryOutcome outcome = RunShadow(
            index, SampledWorkspace, ServedHybrid("sym-b", "sym-c"));

        CanaryShadowFacts facts = outcome.ShadowFacts!;
        Assert.Equal(CanaryShadowStatus.Ok, facts.Status);
        Assert.Equal(0, facts.LexicalTop1Rank);
        Assert.True(facts.Top1Changed);
        Assert.Equal(2, facts.OverlapAt10);
    }

    [Fact]
    public void ShadowArmThrows_RecordsErrorStatusOnlyAndLeavesTheServedResultUntouched()
    {
        RecordingSymbolLookupIndex index = ThreeSymbolIndex();

        SearchTool.SymbolCanaryOutcome outcome = RunShadow(
            index,
            SampledWorkspace,
            (_, _, _) => throw new InvalidOperationException("shadow arm blew up"));

        Assert.Equal(CanaryShadowStatus.Error, outcome.ShadowFacts!.Status);
        Assert.Null(outcome.ShadowFacts.OverlapAt10);
        Assert.Equal(LexicalOutput(index), outcome.Result.Output);

        JsonElement metadata = Stamp(outcome.ShadowFacts);
        Assert.False(metadata.TryGetProperty("canary_shadow_overlap_at_10", out _));
        Assert.False(metadata.TryGetProperty("canary_encoder_fingerprint", out _));
    }

    [Fact]
    public void ShadowCancels_RecordsErrorStatusOnlyAndLeavesTheServedResultUntouched()
    {
        RecordingSymbolLookupIndex index = ThreeSymbolIndex();

        SearchTool.SymbolCanaryOutcome outcome = RunShadow(
            index,
            SampledWorkspace,
            (_, _, _) => throw new OperationCanceledException("request torn down"));

        Assert.Equal(CanaryShadowStatus.Error, outcome.ShadowFacts!.Status);
        Assert.Equal(LexicalOutput(index), outcome.Result.Output);
    }

    [Fact]
    public void ShadowEmbedTimeout_RecordsTimeoutStatusOnly()
    {
        RecordingSymbolLookupIndex index = ThreeSymbolIndex();

        SearchTool.SymbolCanaryOutcome outcome = RunShadow(
            index, SampledWorkspace, Abstained(SemanticFallbackKind.EmbedTimeout, identity: null));

        Assert.Equal(CanaryShadowStatus.Timeout, outcome.ShadowFacts!.Status);
        Assert.Null(outcome.ShadowFacts.OverlapAt10);
        Assert.Equal(LexicalOutput(index), outcome.Result.Output);
    }

    [Theory]
    [InlineData(SemanticFallbackKind.VectorsMissing)]
    [InlineData(SemanticFallbackKind.VectorsBuilding)]
    [InlineData(SemanticFallbackKind.VectorsIncompatible)]
    [InlineData(SemanticFallbackKind.ModelNotPrepared)]
    [InlineData(SemanticFallbackKind.CircuitOpen)]
    [InlineData(SemanticFallbackKind.DiskBlocked)]
    public void ShadowPrerequisiteUnavailable_RecordsSkippedStatusOnly(SemanticFallbackKind fallback)
    {
        RecordingSymbolLookupIndex index = ThreeSymbolIndex();

        SearchTool.SymbolCanaryOutcome outcome = RunShadow(
            index, SampledWorkspace, Abstained(fallback, identity: null));

        Assert.Equal(CanaryShadowStatus.Skipped, outcome.ShadowFacts!.Status);
        Assert.Null(outcome.ShadowFacts.OverlapAt10);
    }

    [Theory]
    [InlineData(SemanticFallbackKind.EmbedError)]
    [InlineData(SemanticFallbackKind.KnnError)]
    [InlineData(SemanticFallbackKind.Unknown)]
    public void ShadowExecutionFailure_RecordsErrorStatusOnly(SemanticFallbackKind fallback)
    {
        RecordingSymbolLookupIndex index = ThreeSymbolIndex();

        SearchTool.SymbolCanaryOutcome outcome = RunShadow(
            index, SampledWorkspace, Abstained(fallback, identity: null));

        Assert.Equal(CanaryShadowStatus.Error, outcome.ShadowFacts!.Status);
    }

    [Fact]
    public void ShadowAbstainedAfterOpeningVectors_RecordsIdentityAlongsideTheStatus()
    {
        RecordingSymbolLookupIndex index = ThreeSymbolIndex();
        SemanticGenerationIdentity identity = MillerSemanticContract.PinnedIdentity(Pin);

        SearchTool.SymbolCanaryOutcome outcome = RunShadow(
            index, SampledWorkspace, Abstained(SemanticFallbackKind.CircuitOpen, identity));

        Assert.Equal(CanaryShadowStatus.Skipped, outcome.ShadowFacts!.Status);
        Assert.Equal(identity.EncoderFingerprint, outcome.ShadowFacts.EncoderFingerprint);
    }

    [Fact]
    public void ShadowArmUnavailable_NullRunner_RecordsSkipped()
    {
        RecordingSymbolLookupIndex index = ThreeSymbolIndex();

        SearchTool.SymbolCanaryOutcome outcome = RunShadow(index, SampledWorkspace, runner: null);

        Assert.Equal(CanaryShadowStatus.Skipped, outcome.ShadowFacts!.Status);
        Assert.Null(outcome.ShadowFacts.EncoderFingerprint);
        Assert.Equal(LexicalOutput(index), outcome.Result.Output);
    }

    [Fact]
    public async Task RealArm_KnnFailure_RecordsErrorAndServesLexicalUntouched()
    {
        RecordingSymbolLookupIndex index = ThreeSymbolIndex();
        var port = new RecordingPort
        {
            Identity = MillerSemanticContract.PinnedIdentity(Pin),
            Matches = [Match(1, 0.05, "sym-c", "src/Charlie.cs")],
            SearchFailure = new VectorStoreException("the artifact went away mid-query"),
        };
        await using SemanticEmbeddingSession session = NewSession();

        SearchTool.SymbolCanaryOutcome outcome = RunShadow(index, SampledWorkspace, RealShadow(port, session));

        Assert.Equal(CanaryShadowStatus.Error, outcome.ShadowFacts!.Status);
        Assert.Null(outcome.ShadowFacts.OverlapAt10);
        Assert.Equal(LexicalOutput(index), outcome.Result.Output);
    }

    [Fact]
    public void StampShadow_OkPath_WritesExactlyTheShadowKeySet()
    {
        JsonElement metadata = Stamp(OkFacts());

        foreach (string present in new[]
        {
            "canary_contract_version", "canary_experiment_id", "canary_assignment_version", "canary_query_class",
            "canary_eligibility", "canary_policy_version", "canary_arm", "canary_bucket", "canary_shadow_status",
            "canary_semantic_result_count", "canary_shadow_overlap_at_10", "canary_shadow_top1_changed",
            "canary_shadow_lexical_top1_rank", "canary_encoder_fingerprint", "canary_storage_schema",
            "canary_corpus_generation",
        })
        {
            Assert.True(metadata.TryGetProperty(present, out _), present);
        }

        foreach (string absent in new[]
        {
            "canary_backend", "canary_embed_warmth", "canary_embed_latency_bucket", "canary_knn_latency_bucket",
            "canary_lexical_result_count", "canary_fused_result_count",
            "canary_semantic_contribution_count", "canary_fallback_reason", "canary_rescue_kind",
            "canary_fusion_profile", "canary_result_name_hashes", "canary_result_path_hashes",
            "canary_result_qualified_hashes", "canary_result_hash_truncated",
        })
        {
            Assert.False(metadata.TryGetProperty(absent, out _), absent);
        }

        Assert.Equal(CanaryArm.Shadow, metadata.GetProperty("canary_arm").GetString());
        Assert.Equal(CanaryAssignment.IdentifierExperimentId, metadata.GetProperty("canary_experiment_id").GetString());
        Assert.Equal(4, metadata.GetProperty("canary_semantic_result_count").GetInt32());
        Assert.True(metadata.GetProperty("canary_shadow_top1_changed").GetBoolean());
        Assert.Equal(2, metadata.GetProperty("canary_shadow_lexical_top1_rank").GetInt32());
    }

    [Fact]
    public void StampShadow_OkPath_WithZeroSemanticHits_WritesTheCountAsZeroNotAbsent()
    {
        JsonElement metadata = Stamp(OkFacts() with { SemanticResultCount = 0 });

        Assert.True(metadata.TryGetProperty("canary_semantic_result_count", out JsonElement count));
        Assert.Equal(0, count.GetInt32());
    }

    [Fact]
    public void StampShadow_NonOkPath_OmitsSemanticResultCount()
    {
        JsonElement metadata = Stamp(OkFacts() with
        {
            Status = CanaryShadowStatus.Timeout,
            SemanticResultCount = null,
            OverlapAt10 = null,
            Top1Changed = null,
            LexicalTop1Rank = null,
        });

        Assert.False(metadata.TryGetProperty("canary_semantic_result_count", out _));
    }

    [Fact]
    public void StampShadow_NonOkPath_WritesStatusOnlyPlusIdentityWhenOpened()
    {
        JsonElement metadata = Stamp(OkFacts() with
        {
            Status = CanaryShadowStatus.Skipped,
            OverlapAt10 = null,
            Top1Changed = null,
            LexicalTop1Rank = null,
        });

        Assert.Equal(CanaryShadowStatus.Skipped, metadata.GetProperty("canary_shadow_status").GetString());
        Assert.False(metadata.TryGetProperty("canary_shadow_overlap_at_10", out _));
        Assert.False(metadata.TryGetProperty("canary_shadow_top1_changed", out _));
        Assert.False(metadata.TryGetProperty("canary_shadow_lexical_top1_rank", out _));
        Assert.True(metadata.TryGetProperty("canary_encoder_fingerprint", out _));
    }

    [Fact]
    public void StampShadow_NonOkWithoutOpenedVectors_OmitsIdentity()
    {
        JsonElement metadata = Stamp(new CanaryShadowFacts
        {
            WorkspaceId = SampledWorkspace,
            UtcDate = UtcDate,
            QueryClass = CanaryQueryClass.Identifier,
            Eligibility = CanaryEligibility.IneligibleQueryClass,
            Status = CanaryShadowStatus.Timeout,
        });

        Assert.Equal(CanaryShadowStatus.Timeout, metadata.GetProperty("canary_shadow_status").GetString());
        Assert.False(metadata.TryGetProperty("canary_encoder_fingerprint", out _));
        Assert.False(metadata.TryGetProperty("canary_storage_schema", out _));
        Assert.False(metadata.TryGetProperty("canary_corpus_generation", out _));
    }

    private static SearchTool.SymbolCanaryOutcome RunShadow(
        ISymbolLookupIndex index,
        string workspaceId,
        Func<ISymbolLookupIndex, SearchRoute, SearchRouteExecutionRequest, SearchTool.ShadowExecution>? runner) =>
        SearchTool.RunSymbolsWithCanary(
            index, SymbolRoute, Request(IdentifierQuery), CanaryMode.On, "symbol", semanticDisabled: false,
            workspaceId, UtcDate, () => "ready", crossWorkspaceNoGeneration: false, treatmentArmFactory: null, runner);

    private static Func<ISymbolLookupIndex, SearchRoute, SearchRouteExecutionRequest, SearchTool.ShadowExecution> RealShadow(
        RecordingPort port, SemanticEmbeddingSession session) =>
        SearchTool.ShadowRunnerFor(_ => new SemanticSearchArm(Root, enabled: true, port.Factory, () => session));

    private static Func<ISymbolLookupIndex, SearchRoute, SearchRouteExecutionRequest, SearchTool.ShadowExecution> ServedHybrid(
        params string[] hybridSymbolIds) =>
        (_, _, _) => new SearchTool.ShadowExecution(
            Served: true, Fallback: SemanticFallbackKind.None, hybridSymbolIds, Identity: null);

    private static Func<ISymbolLookupIndex, SearchRoute, SearchRouteExecutionRequest, SearchTool.ShadowExecution> Abstained(
        SemanticFallbackKind fallback, SemanticGenerationIdentity? identity) =>
        (_, _, _) => new SearchTool.ShadowExecution(Served: false, fallback, [], identity);

    private static string LexicalOutput(ISymbolLookupIndex index) =>
        SearchRouteExecutor.RunSymbols(index, SymbolRoute, Request(IdentifierQuery)).Output;

    private CanaryShadowFacts OkFacts() => new()
    {
        WorkspaceId = SampledWorkspace,
        UtcDate = UtcDate,
        QueryClass = CanaryQueryClass.Identifier,
        Eligibility = CanaryEligibility.IneligibleQueryClass,
        Status = CanaryShadowStatus.Ok,
        SemanticResultCount = 4,
        OverlapAt10 = 3,
        Top1Changed = true,
        LexicalTop1Rank = 2,
        EncoderFingerprint = MillerSemanticContract.PinnedIdentity(Pin).EncoderFingerprint,
        StorageSchema = MillerSemanticContract.PinnedIdentity(Pin).StorageSchema,
        CorpusGeneration = MillerSemanticContract.PinnedIdentity(Pin).CorpusGeneration,
    };

    private JsonElement Stamp(CanaryShadowFacts facts)
    {
        using TelemetryLedger ledger =
            TelemetryLedger.Open(Path.Combine(_temp, "telemetry-" + Guid.NewGuid() + ".db"), "ws-shadow", _temp);
        using TelemetryScope scope = ledger.Measure("search", "symbol");
        CanaryTelemetry.StampShadow(scope, facts);
        return JsonDocument.Parse(scope.MetadataJson).RootElement.Clone();
    }

    private static SearchRouteExecutionRequest Request(string query) =>
        new(query, Limit: 10, Json: false, ExcludeTests: false, WorkspaceRoot: Root);

    private static SemanticEmbeddingSession NewSession() =>
        new(FakeSemanticSidecar.InProcessLauncher(), FastOptions);

    private static VectorMatch Match(long rowId, double distance, string unitId, string path) =>
        new(rowId, distance, unitId, path);

    private static RecordingSymbolLookupIndex ThreeSymbolIndex() =>
        new(
            Symbol(0, "sym-a", "Alpha", "src/Alpha.cs"),
            Symbol(1, "sym-b", "Bravo", "src/Bravo.cs"),
            Symbol(2, "sym-c", "Charlie", "src/Charlie.cs"));

    private static IndexedSymbol Symbol(int docId, string symbolId, string name, string path) =>
        new(docId, symbolId, name, "void " + name + "()", "method", "csharp", path, 3, 6, ParentId: null, IsTest: false);

    public void Dispose()
    {
        if (Directory.Exists(_temp))
            Directory.Delete(_temp, recursive: true);
    }

    private sealed class InvocationCounter
    {
        public int Count { get; private set; }

        public Func<ISymbolLookupIndex, SearchRoute, SearchRouteExecutionRequest, SearchTool.ShadowExecution> Wrap(
            Func<ISymbolLookupIndex, SearchRoute, SearchRouteExecutionRequest, SearchTool.ShadowExecution> inner) =>
            (index, route, request) =>
            {
                Count++;
                return inner(index, route, request);
            };
    }

    private sealed class RecordingPort
    {
        public IReadOnlyList<VectorMatch> Matches { get; init; } = [];

        public SemanticStorageLane Lane { get; init; } =
            MillerSemanticContract.ParseStorageSchema(MillerSemanticContract.DefaultEncoder.StorageSchema);

        public SemanticGenerationIdentity? Identity { get; init; }

        public Exception? SearchFailure { get; init; }

        public IVectorSearchPort? Factory(string workspaceRoot, out string? unavailableReason)
        {
            unavailableReason = null;
            return new Port(this);
        }

        private sealed class Port(RecordingPort owner) : IVectorSearchPort
        {
            public SemanticStorageLane Lane => owner.Lane;

            public SemanticGenerationIdentity? Identity => owner.Identity;

            public IReadOnlyList<VectorMatch> Search(VectorUnitKind kind, ReadOnlySpan<sbyte> query, int k)
            {
                if (owner.SearchFailure is { } failure)
                    throw failure;

                return [.. owner.Matches.Take(k)];
            }

            public void Dispose()
            {
            }
        }
    }

    private sealed class RecordingSymbolLookupIndex(params IndexedSymbol[] symbols) : ISymbolLookupIndex
    {
        private readonly IReadOnlyList<IndexedSymbol> _symbols = symbols;

        public int DocumentCount => _symbols.Count;

        public IReadOnlySet<string> KnownExtensions { get; } = new HashSet<string>(StringComparer.Ordinal) { ".cs" };

        public IReadOnlyList<SearchHit> Search(string query, int limit = 10, SearchMode mode = SearchMode.Or) =>
            [.. _symbols.Take(limit).Select(symbol => new SearchHit(symbol.ToSearchableDocument(), 2.0))];

        public IndexedSymbol Resolve(int docId) => _symbols.Single(symbol => symbol.DocId == docId);

        public IReadOnlyList<IndexedSymbol> FindByName(string name) =>
            [.. _symbols.Where(symbol => string.Equals(symbol.Name, name, StringComparison.Ordinal))];

        public IndexedSymbol? FindBySymbolId(string symbolId) =>
            _symbols.FirstOrDefault(symbol => string.Equals(symbol.SymbolId, symbolId, StringComparison.Ordinal));

        public IReadOnlyList<IndexedSymbol> FindChildren(string parentId) => [];

        public IReadOnlyList<IndexedSymbol> FindByFilePath(string filePath) =>
            [.. _symbols.Where(symbol => string.Equals(symbol.FilePath, filePath, StringComparison.Ordinal))];

        public IReadOnlyList<IndexedSymbol> FindByFilePathFragment(string query, int limit) =>
            [.. _symbols.Where(symbol => symbol.FilePath.Contains(query, StringComparison.Ordinal)).Take(limit)];

        public bool IsIndexedFilePath(string path) =>
            _symbols.Any(symbol => string.Equals(symbol.FilePath, path, StringComparison.Ordinal));

        public string? ResolveIndexedFilePath(string target) =>
            _symbols.FirstOrDefault(symbol => string.Equals(symbol.FilePath, target, StringComparison.Ordinal))?.FilePath;
    }
}
