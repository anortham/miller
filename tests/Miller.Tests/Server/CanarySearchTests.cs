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
/// Pins the P5 symbol-route canary go-live: the frozen assignment flip, arm serving (control lexical, treatment
/// fused), the field-table facts, the served-result digests, and the eligibility ladder — over the same
/// contract-faithful fake sidecar/store fixtures as the hybrid-search suite, with no vec0 and no real embedder.
/// </summary>
public sealed class CanarySearchTests : IDisposable
{
    private const string Root = "/ws";
    private const string ConceptualQuery = "how does the workspace refresh converge";
    private const string ControlWorkspace = "ws-hex";
    private const string TreatmentWorkspace = "ws-canary";
    private const string UtcDate = "2026-07-20";

    private static readonly SearchRoute SymbolRoute = SearchRoutePlanner.Plan("symbol", regions: null);
    private static readonly SearchRoute FileRoute = SearchRoutePlanner.Plan("file", regions: null);
    private static readonly SemanticEncoderPin Pin = MillerSemanticContract.DefaultEncoder;

    private readonly string _temp = Path.Combine(Path.GetTempPath(), "miller-canary-search-" + Guid.NewGuid());

    public CanarySearchTests() => Directory.CreateDirectory(_temp);

    private static SemanticSessionOptions FastOptions => new()
    {
        RequestTimeout = TimeSpan.FromSeconds(10),
        InitTimeout = TimeSpan.FromSeconds(10),
        ShutdownTimeout = TimeSpan.FromSeconds(1),
        RestartBackoff = TimeSpan.Zero,
        RestartBackoffCap = TimeSpan.Zero,
        Delay = static (_, _) => Task.CompletedTask,
    };

    [Fact]
    public void ClassifierConstants_MatchTheFrozenQueryClassEnum()
    {
        Assert.Equal(CanaryQueryClass.ShortToken, CanaryQueryClassifier.ShortToken);
        Assert.Equal(CanaryQueryClass.Identifier, CanaryQueryClassifier.Identifier);
        Assert.Equal(CanaryQueryClass.Path, CanaryQueryClassifier.Path);
        Assert.Equal(CanaryQueryClass.Prose, CanaryQueryClassifier.Prose);
        Assert.Equal(CanaryQueryClass.DocsLike, CanaryQueryClassifier.DocsLike);
        Assert.Equal(CanaryQueryClass.Mixed, CanaryQueryClassifier.Mixed);
    }

    [Fact]
    public async Task CanaryOff_RendersTheRequestArmByteIdenticalAndStampsNothing()
    {
        RecordingSymbolLookupIndex index = TwoSymbolIndex();
        var port = new RecordingPort { Matches = [Match(1, 0.05, "gadget-symbol", "src/Gadget.cs")] };
        await using SemanticEmbeddingSession session = NewSession();
        SearchRouteExecutionRequest request = Request(ConceptualQuery, json: false, OnArm(port, session));

        SearchTool.SymbolCanaryOutcome outcome = SearchTool.RunSymbolsWithCanary(
            index, SymbolRoute, request, CanaryMode.Off, "auto", semanticDisabled: false,
            TreatmentWorkspace, UtcDate, () => "ready", foreignWorkspace: false, Treatment(port, session));

        Assert.Null(outcome.Facts);
        Assert.Equal(SearchRouteExecutor.RunSymbols(index, SymbolRoute, request).Output, outcome.Result.Output);
    }

    [Fact]
    public async Task EligibleControlUnit_ServesLexicalByteIdenticalAndNeverConsultsTheArm()
    {
        RecordingSymbolLookupIndex index = TwoSymbolIndex();
        var port = new RecordingPort { Matches = [Match(1, 0.05, "gadget-symbol", "src/Gadget.cs")] };
        await using SemanticEmbeddingSession session = NewSession();

        SearchTool.SymbolCanaryOutcome outcome = Run(
            index, ConceptualQuery, ControlWorkspace, Treatment(port, session));

        Assert.Equal(23, Bucket(ControlWorkspace, CanaryQueryClass.Prose));
        Assert.Equal(CanaryArm.Control, Arm(outcome.Facts!));
        Assert.Equal(0, port.OpenCount);
        Assert.Equal(
            SearchRouteExecutor.RunSymbols(index, SymbolRoute, Request(ConceptualQuery, json: false)).Output,
            outcome.Result.Output);

        CanaryCallFacts facts = outcome.Facts!;
        Assert.Equal(CanaryEligibility.Eligible, facts.Eligibility);
        Assert.Equal(2, facts.LexicalResultCount);
        Assert.Null(facts.SemanticResultCount);
        Assert.Equal(CanaryFallbackReason.None, facts.FallbackReason);
        Assert.Equal(CanaryBackend.None, facts.Backend);
        Assert.Equal(CanaryEmbedWarmth.None, facts.EmbedWarmth);
    }

    [Fact]
    public void EligibleControlUnit_RecordsTheConfiguredGenerationWithoutOpeningTheArm()
    {
        RecordingSymbolLookupIndex index = TwoSymbolIndex();
        SemanticGenerationIdentity identity = MillerSemanticContract.PinnedIdentity(Pin);
        bool armConstructed = false;

        SearchTool.SymbolCanaryOutcome outcome = SearchTool.RunSymbolsWithCanaryProbe(
            index,
            SymbolRoute,
            Request(ConceptualQuery, json: false),
            CanaryMode.On,
            "symbol",
            semanticDisabled: false,
            ControlWorkspace,
            UtcDate,
            () => new CanaryVectorProbe("ready", identity),
            foreignWorkspace: false,
            () =>
            {
                armConstructed = true;
                throw new InvalidOperationException("control must not construct the semantic arm");
            });

        Assert.False(armConstructed);
        Assert.Equal(identity.EncoderFingerprint, outcome.Facts!.EncoderFingerprint);
        Assert.Equal(identity.StorageSchema, outcome.Facts.StorageSchema);
        Assert.Equal(identity.CorpusGeneration, outcome.Facts.CorpusGeneration);
        Assert.Equal(identity.FusionProfile, outcome.Facts.FusionProfile);

        JsonElement metadata = Stamp(outcome.Facts);
        Assert.Equal(identity.EncoderFingerprint["sha256:".Length..][..16],
            metadata.GetProperty("canary_encoder_fingerprint").GetString());
        Assert.Equal(identity.StorageSchema, metadata.GetProperty("canary_storage_schema").GetString());
        Assert.Equal(identity.CorpusGeneration, metadata.GetProperty("canary_corpus_generation").GetString());
        Assert.Equal(identity.FusionProfile, metadata.GetProperty("canary_fusion_profile").GetString());
    }

    [Fact]
    public void EligibleShadowHybrid_ExposesShadowServingPolicyAndServesLexicalBytes()
    {
        RecordingSymbolLookupIndex index = TwoSymbolIndex();
        SearchRouteExecutionRequest request = Request(ConceptualQuery, json: false);
        bool treatmentConstructed = false;

        SearchTool.SymbolCanaryOutcome outcome = SearchTool.RunSymbolsWithCanary(
            index,
            SymbolRoute,
            request,
            CanaryMode.On,
            "symbol",
            semanticDisabled: false,
            TreatmentWorkspace,
            UtcDate,
            () => "ready",
            foreignWorkspace: false,
            () =>
            {
                treatmentConstructed = true;
                throw new InvalidOperationException("shadow must not construct a serving treatment arm");
            },
            semanticMode: SemanticMode.Shadow);

        Assert.Equal(CanaryEligibility.Eligible, outcome.Facts!.Eligibility);
        Assert.Equal(SearchServingPolicy.Shadow, outcome.ServingPolicy);
        Assert.False(treatmentConstructed);
        Assert.Equal(SearchRouteExecutor.RunSymbols(index, SymbolRoute, request).Output, outcome.Result.Output);

        JsonElement metadata = Stamp(outcome.Facts, outcome.ServingPolicy);
        Assert.False(metadata.TryGetProperty("canary_contract_version", out _));
    }

    [Fact]
    public async Task EligibleTreatmentUnit_ServesFusedIdenticalToSemanticOnAndRecordsTreatment()
    {
        RecordingSymbolLookupIndex index = TwoSymbolIndex();
        var treatmentPort = new RecordingPort
        {
            Identity = MillerSemanticContract.PinnedIdentity(Pin),
            Matches = [Match(1, 0.05, "gadget-symbol", "src/Gadget.cs")],
        };
        var referencePort = new RecordingPort
        {
            Identity = MillerSemanticContract.PinnedIdentity(Pin),
            Matches = [Match(1, 0.05, "gadget-symbol", "src/Gadget.cs")],
        };
        await using SemanticEmbeddingSession treatmentSession = NewSession();
        await using SemanticEmbeddingSession referenceSession = NewSession();

        SearchTool.SymbolCanaryOutcome outcome = Run(
            index, ConceptualQuery, TreatmentWorkspace, Treatment(treatmentPort, treatmentSession));

        string semanticOn = SearchRouteExecutor.RunSymbols(
            index, SymbolRoute, Request(ConceptualQuery, json: false, OnArm(referencePort, referenceSession))).Output;

        Assert.Equal(94, Bucket(TreatmentWorkspace, CanaryQueryClass.Prose));
        Assert.Equal(CanaryArm.Treatment, Arm(outcome.Facts!));
        Assert.Equal(semanticOn, outcome.Result.Output);

        CanaryCallFacts facts = outcome.Facts!;
        Assert.Equal(2, facts.LexicalResultCount);
        Assert.Equal(1, facts.SemanticResultCount);
        Assert.Equal(2, facts.FusedResultCount);
        Assert.Equal(1, facts.SemanticContributionCount);
        Assert.Equal(CanaryFallbackReason.None, facts.FallbackReason);
        Assert.Equal("cpu", facts.Backend);
        Assert.Equal("cold", facts.EmbedWarmth);
        Assert.NotNull(facts.EmbedLatencyMs);
        Assert.NotNull(facts.KnnLatencyMs);
        Assert.Equal(RrfFusion.FusionProfile, facts.FusionProfile);
        Assert.Equal(MillerSemanticContract.PinnedIdentity(Pin).EncoderFingerprint, facts.EncoderFingerprint);
        Assert.Equal(MillerSemanticContract.PinnedIdentity(Pin).StorageSchema, facts.StorageSchema);
        Assert.Equal(MillerSemanticContract.PinnedIdentity(Pin).CorpusGeneration, facts.CorpusGeneration);
    }

    [Theory]
    [InlineData(ControlWorkspace, CanaryArm.Control)]
    [InlineData(TreatmentWorkspace, CanaryArm.Treatment)]
    public void Decision_PreservesHybridAssignmentAndLexicalResultWithoutAnArm(string workspaceId, string expectedArm)
    {
        RecordingSymbolLookupIndex index = TwoSymbolIndex();
        SearchRouteExecutionRequest request = Request(ConceptualQuery, json: false);

        SearchTool.SymbolCanaryOutcome v2 = SearchTool.RunSymbolsWithCanary(
            index, SymbolRoute, request, CanaryMode.On, "symbol", semanticDisabled: false,
            workspaceId, UtcDate, () => "ready", foreignWorkspace: false, treatmentArmFactory: null);
        SearchTool.SymbolCanaryOutcome v3 = SearchTool.RunSymbolsWithCanary(
            index, SymbolRoute, request, CanaryMode.Decision, "symbol", semanticDisabled: false,
            workspaceId, UtcDate, () => "ready", foreignWorkspace: false, treatmentArmFactory: null);

        Assert.Equal(expectedArm, Arm(v2.Facts!));
        Assert.Equal(expectedArm, Arm(v3.Facts!));
        Assert.Equal(v2.Result.Output, v3.Result.Output);

        JsonElement metadata = Stamp(v3.Facts!, mode: CanaryMode.Decision);
        Assert.Equal(3, metadata.GetProperty("canary_contract_version").GetInt32());
    }

    [Fact]
    public async Task TreatmentUnit_WhenTheArtifactVanishesMidQuery_ServesLexicalAndRecordsTheFallback()
    {
        RecordingSymbolLookupIndex index = TwoSymbolIndex();
        var port = new RecordingPort
        {
            Identity = MillerSemanticContract.PinnedIdentity(Pin),
            Matches = [Match(1, 0.05, "gadget-symbol", "src/Gadget.cs")],
            SearchFailure = new VectorStoreException("the artifact went away mid-query"),
        };
        await using SemanticEmbeddingSession session = NewSession();

        SearchTool.SymbolCanaryOutcome outcome = Run(index, ConceptualQuery, TreatmentWorkspace, Treatment(port, session));

        Assert.Equal(CanaryArm.Treatment, Arm(outcome.Facts!));
        Assert.Equal(
            SearchRouteExecutor.RunSymbols(index, SymbolRoute, Request(ConceptualQuery, json: false)).Output,
            outcome.Result.Output);

        CanaryCallFacts facts = outcome.Facts!;
        Assert.Equal("knn_error", facts.FallbackReason);
        Assert.Null(facts.SemanticResultCount);
        Assert.Null(facts.FusedResultCount);
    }

    [Fact]
    public async Task OffSurfaceCall_RecordsIneligibleSurfaceAndServesLexicalWithoutTouchingTheArm()
    {
        RecordingSymbolLookupIndex index = TwoSymbolIndex();
        var port = new RecordingPort { Matches = [Match(1, 0.05, "gadget-symbol", "src/Gadget.cs")] };
        await using SemanticEmbeddingSession session = NewSession();

        SearchTool.SymbolCanaryOutcome outcome = SearchTool.RunSymbolsWithCanary(
            index, FileRoute, Request("Widget", json: false), CanaryMode.On, "file", semanticDisabled: false,
            TreatmentWorkspace, UtcDate, () => "ready", foreignWorkspace: false, Treatment(port, session));

        Assert.Equal(0, port.OpenCount);
        JsonElement metadata = Stamp(outcome.Facts!);
        Assert.Equal("ineligible", metadata.GetProperty("canary_arm").GetString());
        Assert.Equal(CanaryEligibility.IneligibleSurface, metadata.GetProperty("canary_eligibility").GetString());
        Assert.False(metadata.TryGetProperty("canary_bucket", out _));
        Assert.False(metadata.TryGetProperty("canary_lexical_result_count", out _));
        Assert.False(metadata.TryGetProperty("canary_result_name_hashes", out _));
    }

    [Theory]
    [InlineData("markers", false, CanaryQueryClass.Prose, "ready", false, CanaryEligibility.IneligibleSurface)]
    [InlineData("auto", true, CanaryQueryClass.Prose, "ready", false, CanaryEligibility.IneligibleSemanticDisabled)]
    [InlineData("auto", false, CanaryQueryClass.Identifier, "ready", false, CanaryEligibility.IneligibleQueryClass)]
    [InlineData("auto", false, CanaryQueryClass.Path, "ready", false, CanaryEligibility.IneligibleQueryClass)]
    [InlineData("auto", false, CanaryQueryClass.ShortToken, "ready", false, CanaryEligibility.IneligibleQueryClass)]
    [InlineData("auto", false, CanaryQueryClass.Prose, "unavailable", false, CanaryEligibility.IneligibleVectorsUnavailable)]
    [InlineData("auto", false, CanaryQueryClass.Prose, "building", false, CanaryEligibility.IneligibleVectorsUnavailable)]
    [InlineData("auto", false, CanaryQueryClass.Prose, "downloading", false, CanaryEligibility.IneligibleVectorsUnavailable)]
    [InlineData("auto", false, CanaryQueryClass.Prose, "disk-blocked", false, CanaryEligibility.IneligibleVectorsUnavailable)]
    [InlineData("auto", false, CanaryQueryClass.Prose, "incompatible", false, CanaryEligibility.IneligibleVectorsIncompatible)]
    [InlineData("auto", false, CanaryQueryClass.Prose, "circuit-open", false, CanaryEligibility.IneligibleCircuitOpen)]
    [InlineData("auto", false, CanaryQueryClass.Prose, "unavailable", true, CanaryEligibility.IneligibleCrossWorkspaceNoGeneration)]
    [InlineData("auto", false, CanaryQueryClass.Prose, "ready", true, CanaryEligibility.IneligibleCrossWorkspaceNoGeneration)]
    [InlineData("auto", false, CanaryQueryClass.Prose, "ready", false, CanaryEligibility.Eligible)]
    public void EligibilityLadder_FirstMatchWins(
        string op, bool semanticDisabled, string queryClass, string vectorState, bool crossWorkspace, string expected)
    {
        Assert.Equal(expected, CanaryEligibility.Resolve(op, semanticDisabled, queryClass, vectorState, crossWorkspace));
    }

    [Fact]
    public void EligibilityLadder_SurfaceRungPrecedesEveryOtherReason()
    {
        Assert.Equal(
            CanaryEligibility.IneligibleSurface,
            CanaryEligibility.Resolve("file", semanticDisabled: true, CanaryQueryClass.Identifier, "unavailable", crossWorkspaceNoGeneration: true));
    }

    [Fact]
    public void ElevenServedResults_CapTheHashArraysAtTenWithTheSharedTruncationFlag()
    {
        var results = Enumerable.Range(0, 11)
            .Select(i => new CanaryServedResult($"Name{i}", $"src/File{i}.cs", $"Type{i}.Name{i}"))
            .ToList();

        JsonElement metadata = Stamp(EligibleFacts() with { ResultCount = 11, ServedResults = results });

        Assert.Equal(10, metadata.GetProperty("canary_result_name_hashes").GetArrayLength());
        Assert.Equal(10, metadata.GetProperty("canary_result_path_hashes").GetArrayLength());
        Assert.Equal(10, metadata.GetProperty("canary_result_qualified_hashes").GetArrayLength());
        Assert.True(metadata.GetProperty("canary_result_hash_truncated").GetBoolean());
    }

    [Fact]
    public void RescuePaths_ExtendThePathArrayInServedOrderWithoutAddingNameEntries()
    {
        var served = new[]
        {
            new CanaryServedResult("Alpha", "src/Alpha.cs", null),
            new CanaryServedResult("Bravo", "src/Bravo.cs", null),
        };

        JsonElement metadata = Stamp(EligibleFacts() with
        {
            ResultCount = 4,
            ServedResults = served,
            AdditionalServedPaths = ["docs/rescue-a.md", "docs/rescue-b.md"],
        });

        Assert.Equal(2, metadata.GetProperty("canary_result_name_hashes").GetArrayLength());
        Assert.Equal(4, metadata.GetProperty("canary_result_path_hashes").GetArrayLength());
        Assert.False(metadata.GetProperty("canary_result_hash_truncated").GetBoolean());
        Assert.Contains(TargetHash("docs/rescue-a.md"), Hashes(metadata, "canary_result_path_hashes"));
        Assert.Contains(TargetHash("docs/rescue-b.md"), Hashes(metadata, "canary_result_path_hashes"));
        Assert.DoesNotContain(TargetHash("docs/rescue-a.md"), Hashes(metadata, "canary_result_name_hashes"));
    }

    [Fact]
    public void RescuePaths_ZeroPrimaryPage_HashesOnlyTheRescueRowsAndNoNameArray()
    {
        JsonElement metadata = Stamp(EligibleFacts() with
        {
            ResultCount = 2,
            ServedResults = [],
            AdditionalServedPaths = ["docs/only-a.md", "docs/only-b.md"],
        });

        Assert.Equal(2, metadata.GetProperty("canary_result_path_hashes").GetArrayLength());
        Assert.False(metadata.TryGetProperty("canary_result_name_hashes", out _));
        Assert.False(metadata.TryGetProperty("canary_result_qualified_hashes", out _));
        Assert.False(metadata.GetProperty("canary_result_hash_truncated").GetBoolean());
        Assert.Contains(TargetHash("docs/only-a.md"), Hashes(metadata, "canary_result_path_hashes"));
    }

    [Fact]
    public void RescuePaths_ShareTheTenCapWithTheServedResultsAndSetTheTruncationFlag()
    {
        var served = Enumerable.Range(0, 8)
            .Select(i => new CanaryServedResult($"Name{i}", $"src/File{i}.cs", null))
            .ToList();

        JsonElement metadata = Stamp(EligibleFacts() with
        {
            ResultCount = 12,
            ServedResults = served,
            AdditionalServedPaths = ["docs/r0.md", "docs/r1.md", "docs/r2.md", "docs/r3.md"],
        });

        Assert.Equal(8, metadata.GetProperty("canary_result_name_hashes").GetArrayLength());
        Assert.Equal(10, metadata.GetProperty("canary_result_path_hashes").GetArrayLength());
        Assert.True(metadata.GetProperty("canary_result_hash_truncated").GetBoolean());
        Assert.Contains(TargetHash("docs/r0.md"), Hashes(metadata, "canary_result_path_hashes"));
        Assert.Contains(TargetHash("docs/r1.md"), Hashes(metadata, "canary_result_path_hashes"));
        Assert.DoesNotContain(TargetHash("docs/r2.md"), Hashes(metadata, "canary_result_path_hashes"));
    }

    [Fact]
    public void AttributionCase_BareTargetMatchesTheNameHash()
    {
        JsonElement metadata = StampSaved();
        Assert.Contains(TargetHash("Save"), Hashes(metadata, "canary_result_name_hashes"));
    }

    [Fact]
    public void AttributionCase_QualifiedTargetMatchesOnlyTheQualifiedHash()
    {
        JsonElement metadata = StampSaved();
        Assert.Contains(TargetHash("LedgerWriter.Save"), Hashes(metadata, "canary_result_qualified_hashes"));
        Assert.DoesNotContain(TargetHash("LedgerWriter.Save"), Hashes(metadata, "canary_result_name_hashes"));
    }

    [Fact]
    public void AttributionCase_PathTargetMatchesThePathHash()
    {
        JsonElement metadata = StampSaved();
        Assert.Contains(TargetHash("src/Miller.Server/Telemetry/LedgerWriter.cs"), Hashes(metadata, "canary_result_path_hashes"));
    }

    [Fact]
    public void AttributionCase_TopLevelResultContributesNameAndPathButNoQualifiedEntry()
    {
        JsonElement metadata = Stamp(EligibleFacts() with
        {
            ResultCount = 1,
            ServedResults = [new CanaryServedResult("LedgerWriter", "src/Miller.Server/Telemetry/LedgerWriter.cs", null)],
        });

        Assert.Contains(TargetHash("LedgerWriter"), Hashes(metadata, "canary_result_name_hashes"));
        Assert.Single(Hashes(metadata, "canary_result_path_hashes"));
        Assert.False(metadata.TryGetProperty("canary_result_qualified_hashes", out _));
    }

    [Fact]
    public void AttributionCase_DeeperSpellingIsNeverHashedSoItCountsAsNoConversion()
    {
        JsonElement metadata = StampSaved();
        string deeper = TargetHash("Miller.Server.Telemetry.LedgerWriter.Save");

        Assert.DoesNotContain(deeper, Hashes(metadata, "canary_result_name_hashes"));
        Assert.DoesNotContain(deeper, Hashes(metadata, "canary_result_path_hashes"));
        Assert.DoesNotContain(deeper, Hashes(metadata, "canary_result_qualified_hashes"));
    }

    [Fact]
    public void AttributionCase_AHashInTwoArraysStaysOneEntryPerArray()
    {
        JsonElement metadata = Stamp(EligibleFacts() with
        {
            ResultCount = 1,
            ServedResults = [new CanaryServedResult("Save", "Save", "LedgerWriter.Save")],
        });

        Assert.Single(Hashes(metadata, "canary_result_name_hashes"));
        Assert.Single(Hashes(metadata, "canary_result_path_hashes"));
        Assert.Equal(TargetHash("Save"), Assert.Single(Hashes(metadata, "canary_result_name_hashes")));
        Assert.Equal(TargetHash("Save"), Assert.Single(Hashes(metadata, "canary_result_path_hashes")));
    }

    private static SearchTool.SymbolCanaryOutcome Run(
        ISymbolLookupIndex index, string query, string workspaceId, Func<SemanticSymbolFusionArm> treatment) =>
        SearchTool.RunSymbolsWithCanary(
            index, SymbolRoute, Request(query, json: false), CanaryMode.On, "symbol", semanticDisabled: false,
            workspaceId, UtcDate, () => "ready", foreignWorkspace: false, treatment);

    private static int Bucket(string workspaceId, string queryClass) =>
        CanaryAssignment.Bucket(CanaryAssignment.HybridExperimentId, workspaceId, UtcDate, queryClass);

    private static string Arm(CanaryCallFacts facts) => CanaryAssignment.ResolveArm(
        CanaryAssignment.Bucket(CanaryAssignment.HybridExperimentId, facts.WorkspaceId, facts.UtcDate, facts.QueryClass));

    private static CanaryCallFacts EligibleFacts() => new()
    {
        WorkspaceId = ControlWorkspace,
        UtcDate = UtcDate,
        QueryClass = CanaryQueryClass.Prose,
        Eligibility = CanaryEligibility.Eligible,
    };

    private JsonElement StampSaved() => Stamp(EligibleFacts() with
    {
        ResultCount = 1,
        ServedResults = [new CanaryServedResult("Save", "src/Miller.Server/Telemetry/LedgerWriter.cs", "LedgerWriter.Save")],
    });

    private JsonElement Stamp(
        CanaryCallFacts facts,
        SearchServingPolicy servingPolicy = SearchServingPolicy.Lexical,
        CanaryMode mode = CanaryMode.On)
    {
        using TelemetryLedger ledger =
            TelemetryLedger.Open(Path.Combine(_temp, "telemetry-" + Guid.NewGuid() + ".db"), "ws-canary", _temp);
        using TelemetryScope scope = ledger.Measure("search", "auto");
        SearchTool.StampSymbolCanary(scope, mode, facts, servingPolicy);
        return JsonDocument.Parse(scope.MetadataJson).RootElement.Clone();
    }

    private string TargetHash(string raw)
    {
        using TelemetryLedger ledger =
            TelemetryLedger.Open(Path.Combine(_temp, "target-" + Guid.NewGuid() + ".db"), "ws-canary", _temp);
        using TelemetryScope scope = ledger.Measure("inspect", null);
        scope.SetTarget(raw);
        return scope.TargetHash!;
    }

    private static IReadOnlyList<string> Hashes(JsonElement metadata, string key) =>
        [.. metadata.GetProperty(key).EnumerateArray().Select(e => e.GetString()!)];

    private static SearchRouteExecutionRequest Request(
        string query, bool json, ISymbolFusionArm? fusionArm = null) =>
        new(query, Limit: 10, Json: json, ExcludeTests: false, FusionArm: fusionArm, WorkspaceRoot: Root);

    private static Func<SemanticSymbolFusionArm> Treatment(RecordingPort port, SemanticEmbeddingSession session) =>
        () => OnArm(port, session);

    private static SemanticSymbolFusionArm OnArm(RecordingPort port, SemanticEmbeddingSession session) =>
        new(SemanticMode.On, new SemanticSearchArm(Root, enabled: true, port.Factory, () => session));

    private static SemanticEmbeddingSession NewSession() =>
        new(FakeSemanticSidecar.InProcessLauncher(), FastOptions);

    private static VectorMatch Match(long rowId, double distance, string unitId, string path) =>
        new(rowId, distance, unitId, path);

    private static RecordingSymbolLookupIndex TwoSymbolIndex() =>
        new(
            Symbol(0, "widget-symbol", "Widget", "src/Widget.cs"),
            Symbol(1, "gadget-symbol", "Gadget", "src/Gadget.cs"));

    private static IndexedSymbol Symbol(int docId, string symbolId, string name, string path) =>
        new(docId, symbolId, name, "void " + name + "()", "method", "csharp", path, 3, 6, ParentId: null, IsTest: false);

    public void Dispose()
    {
        if (Directory.Exists(_temp))
            Directory.Delete(_temp, recursive: true);
    }

    private sealed class RecordingPort
    {
        public string? UnavailableReason { get; init; }

        public IReadOnlyList<VectorMatch> Matches { get; init; } = [];

        public SemanticStorageLane Lane { get; init; } =
            MillerSemanticContract.ParseStorageSchema(MillerSemanticContract.DefaultEncoder.StorageSchema);

        public SemanticGenerationIdentity? Identity { get; init; }

        public Exception? SearchFailure { get; init; }

        public int OpenCount { get; private set; }

        public IVectorSearchPort? Factory(string workspaceRoot, out string? unavailableReason)
        {
            OpenCount++;
            if (UnavailableReason is not null)
            {
                unavailableReason = UnavailableReason;
                return null;
            }

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
