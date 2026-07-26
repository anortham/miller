using System.Text.Json;
using System.Reflection;
using Miller.Core.Search;
using Miller.Indexing;
using Miller.Indexing.Semantic;
using Miller.Server;
using Miller.Server.Telemetry;
using Miller.Server.Tools;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the P5 content-route canary: op=content assignment over the docs_like promotion, treatment serving the
/// content-mode hybrid forced past the mode gate, control serving byte-identical lexical content, path-only
/// served-result hashes (name and qualified arrays absent), and the eligibility ladder — over a stub content
/// index and a stub chunk arm, no corpus sidecar and no real embedder.
/// </summary>
public sealed class CanaryContentSearchTests : IDisposable
{
    private const string Root = "/ws";
    private const string ConceptualQuery = "how does the workspace refresh converge";
    private const string ControlWorkspace = "ws-hex";
    private const string TreatmentWorkspace = "ws-beta";
    private const string UtcDate = "2026-07-20";

    private readonly string _temp = Path.Combine(Path.GetTempPath(), "miller-canary-content-" + Guid.NewGuid());

    public CanaryContentSearchTests() => Directory.CreateDirectory(_temp);

    [Fact]
    public void CanaryOff_ServesTheProductionRerankByteIdenticalAndStampsNothing()
    {
        ITextContentSearchIndex index = ContentIndex(Docs("docs/a.md", 1, "alpha"), Docs("docs/b.md", 2, "beta"));
        Func<IReadOnlyList<ContentSearchHit>, IReadOnlyList<ContentSearchHit>> reversing = hits => [.. hits.Reverse()];

        SearchTool.ContentCanaryOutcome outcome = Run(
            index, ConceptualQuery, ControlWorkspace, treatmentArm: null, productionRerank: reversing, mode: CanaryMode.Off);

        Assert.Null(outcome.Facts);
        Assert.Empty(outcome.ResultPathHashes);
        Assert.Equal(
            SearchTool.RunContentCorpus(index, ConceptualQuery, 10, json: false, out _, out _, rerank: reversing),
            outcome.Result.Output);
    }

    [Fact]
    public void NormalCliContentRoute_MatchesTheProductionContentArm()
    {
        MethodInfo? method = typeof(Miller.Server.Cli.CliDispatch).GetMethod(
            "RunNormalContentRoute",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        ITextContentSearchIndex index = ContentIndex(
            Docs("docs/a.md", 1, "alpha"),
            Docs("docs/b.md", 2, "beta"),
            Docs("docs/c.md", 3, "gamma"));
        var cliArm = ContentArm(chunks:
        [
            Chunk("docs/c.md", 1, DocsChunkId("docs/c.md", 3)),
            Chunk("docs/b.md", 2, DocsChunkId("docs/b.md", 2)),
        ]);
        var referenceArm = ContentArm(chunks:
        [
            Chunk("docs/c.md", 1, DocsChunkId("docs/c.md", 3)),
            Chunk("docs/b.md", 2, DocsChunkId("docs/b.md", 2)),
        ]);
        SearchRoute route = SearchRoutePlanner.Plan("content", regions: null);
        var request = new SearchRouteExecutionRequest(
            ConceptualQuery,
            Limit: 10,
            Json: false,
            ExcludeTests: null,
            WorkspaceRoot: Root);

        var outcome = Assert.IsType<SearchTool.ContentCanaryOutcome>(method.Invoke(null,
        [
            index, route, request, SemanticMode.On, CanaryMode.Off, TreatmentWorkspace, Root,
            UtcDate, (Func<CanaryVectorProbe>)(() => new CanaryVectorProbe("ready", Identity: null)), cliArm,
            (Func<ISemanticTextArm?>)(() => cliArm), false, null,
        ]));
        string expected = SearchTool.RunContentCorpus(
            index,
            ConceptualQuery,
            10,
            json: false,
            out _,
            out _,
            rerank: SearchTool.BuildContentRerank(referenceArm, ConceptualQuery, Root));

        Assert.Equal(expected, outcome.Result.Output);
        Assert.Equal(SearchServingPolicy.Production, outcome.ServingPolicy);
    }

    [Fact]
    public void NormalCliContentRoute_CarriesExplicitTestExclusionIntoSemanticMaterialization()
    {
        MethodInfo? method = typeof(Miller.Server.Cli.CliDispatch).GetMethod(
            "RunNormalContentRoute",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        TextContentSearchHit semanticOnly = Docs("docs/semantic.md", 7, "semantic metadata snippet");
        var index = new MaterializingContentIndex([], [semanticOnly]);
        var arm = ContentArm(chunks: [Chunk(semanticOnly.DisplayPath, 1, semanticOnly.ChunkId)]);
        SearchRoute route = SearchRoutePlanner.Plan("content", regions: null);
        var request = new SearchRouteExecutionRequest(
            ConceptualQuery,
            Limit: 10,
            Json: false,
            ExcludeTests: true,
            WorkspaceRoot: Root);

        var outcome = Assert.IsType<SearchTool.ContentCanaryOutcome>(method.Invoke(null,
        [
            index, route, request, SemanticMode.On, CanaryMode.Off, TreatmentWorkspace, Root,
            UtcDate, (Func<CanaryVectorProbe>)(() => new CanaryVectorProbe("ready", Identity: null)), arm,
            (Func<ISemanticTextArm?>)(() => arm), false, null,
        ]));

        Assert.Contains("docs/semantic.md", outcome.Result.Output, StringComparison.Ordinal);
        Assert.True(index.LastMaterializeExcludeTests);
    }

    [Fact]
    public void EligibleControlUnit_ServesLexicalByteIdenticalAndNeverConsultsTheArm()
    {
        ITextContentSearchIndex index = ContentIndex(Docs("docs/a.md", 1, "alpha"), Docs("docs/b.md", 2, "beta"));
        var arm = ContentArm(chunks: [Chunk("docs/b.md", 1)]);

        SearchTool.ContentCanaryOutcome outcome = Run(index, ConceptualQuery, ControlWorkspace, arm);

        Assert.Equal(4, Bucket(ControlWorkspace));
        Assert.Equal(CanaryArm.Control, Arm(outcome.Facts!));
        Assert.Equal(0, arm.ChunkQueries);
        Assert.Equal(
            SearchTool.RunContentCorpus(index, ConceptualQuery, 10, json: false, out _, out _),
            outcome.Result.Output);

        CanaryCallFacts facts = outcome.Facts!;
        Assert.Equal(CanaryEligibility.Eligible, facts.Eligibility);
        Assert.Equal(CanaryQueryClass.DocsLike, facts.QueryClass);
        Assert.Equal(2, facts.LexicalResultCount);
        Assert.Null(facts.SemanticResultCount);
        Assert.Equal(CanaryFallbackReason.None, facts.FallbackReason);
        Assert.Equal(CanaryBackend.None, facts.Backend);
        Assert.Equal(CanaryEmbedWarmth.None, facts.EmbedWarmth);
        Assert.Equal(2, outcome.ResultPathHashes.Count);
    }

    [Fact]
    public void Decision_PreservesContentAssignmentAndServedBytesWhileStampingV3()
    {
        ITextContentSearchIndex index = ContentIndex(Docs("docs/a.md", 1, "alpha"));

        SearchTool.ContentCanaryOutcome v2 = Run(index, ConceptualQuery, ControlWorkspace, treatmentArm: null);
        SearchTool.ContentCanaryOutcome v3 = Run(
            index, ConceptualQuery, ControlWorkspace, treatmentArm: null, mode: CanaryMode.Decision);

        Assert.Equal(CanaryArm.Control, Arm(v2.Facts!));
        Assert.Equal(CanaryArm.Control, Arm(v3.Facts!));
        Assert.Equal(v2.Result.Output, v3.Result.Output);

        JsonElement metadata = Stamp(v3, CanaryMode.Decision);
        Assert.Equal(3, metadata.GetProperty("canary_contract_version").GetInt32());
    }

    [Fact]
    public void EligibleSemanticShadow_ServesLexicalAndStampsNoHybridExperimentArm()
    {
        ITextContentSearchIndex index = ContentIndex(Docs("docs/a.md", 1, "alpha"));

        SearchTool.ContentCanaryOutcome outcome = Run(
            index,
            ConceptualQuery,
            TreatmentWorkspace,
            treatmentArm: null,
            semanticMode: SemanticMode.Shadow);

        Assert.Equal(SearchServingPolicy.Shadow, outcome.ServingPolicy);
        Assert.Equal(CanaryEligibility.Eligible, outcome.Facts!.Eligibility);
        Assert.Equal(
            SearchTool.RunContentCorpus(index, ConceptualQuery, 10, json: false, out _, out _),
            outcome.Result.Output);
        Assert.False(Stamp(outcome).TryGetProperty("canary_contract_version", out _));
    }

    [Fact]
    public void EligibleTreatmentUnit_ServesHybridIdenticalToSemanticOnAndRecordsTreatment()
    {
        ITextContentSearchIndex index = ContentIndex(
            Docs("docs/a.md", 1, "alpha"), Docs("docs/b.md", 2, "beta"), Docs("docs/c.md", 3, "gamma"));
        SemanticQueryDiagnostics diagnostics = ServedDiagnostics();
        var treatmentArm = ContentArm(chunks:
        [
            Chunk("docs/c.md", 1, DocsChunkId("docs/c.md", 3)),
            Chunk("docs/b.md", 2, DocsChunkId("docs/b.md", 2)),
        ], diagnostics: diagnostics);
        var referenceArm = ContentArm(chunks:
        [
            Chunk("docs/c.md", 1, DocsChunkId("docs/c.md", 3)),
            Chunk("docs/b.md", 2, DocsChunkId("docs/b.md", 2)),
        ], diagnostics: diagnostics);

        SearchTool.ContentCanaryOutcome outcome = Run(index, ConceptualQuery, TreatmentWorkspace, treatmentArm);

        string semanticOn = SearchTool.RunContentCorpus(
            index, ConceptualQuery, 10, json: false, out _, out _,
            rerank: SearchTool.BuildContentRerank(referenceArm, ConceptualQuery, Root));

        Assert.Equal(85, Bucket(TreatmentWorkspace));
        Assert.Equal(CanaryArm.Treatment, Arm(outcome.Facts!));
        Assert.Equal(semanticOn, outcome.Result.Output);

        CanaryCallFacts facts = outcome.Facts!;
        Assert.Equal(3, facts.LexicalResultCount);
        Assert.Equal(2, facts.SemanticResultCount);
        Assert.Equal(3, facts.FusedResultCount);
        Assert.Equal(1, facts.SemanticContributionCount);
        Assert.Equal(RrfFusion.FusionProfile, facts.FusionProfile);
        Assert.Equal("cpu", facts.Backend);
        Assert.Equal("warm", facts.EmbedWarmth);
        Assert.Equal(5, facts.EmbedLatencyMs);
        Assert.Equal(3, facts.KnnLatencyMs);
        Assert.Equal("sha256:abcdef0123456789aa", facts.EncoderFingerprint);
        Assert.Equal("cards-v1", facts.StorageSchema);
        Assert.Equal("gen-7", facts.CorpusGeneration);
    }

    [Fact]
    public void TreatmentWithLexicalZero_MaterializesAndServesSemanticOnlyChunk()
    {
        TextContentSearchHit semanticOnly = Docs("docs/semantic.md", 7, "semantic metadata snippet");
        ITextContentSearchIndex index = new MaterializingContentIndex([], [semanticOnly]);
        var arm = ContentArm(chunks: [Chunk("docs/semantic.md", 1, semanticOnly.ChunkId)], diagnostics: ServedDiagnostics());

        SearchTool.ContentCanaryOutcome outcome = Run(index, ConceptualQuery, TreatmentWorkspace, arm);

        Assert.Contains("docs/semantic.md", outcome.Result.Output, StringComparison.Ordinal);
        Assert.Contains("semantic metadata snippet", outcome.Result.Output, StringComparison.Ordinal);
        Assert.Equal(0, outcome.Facts!.LexicalResultCount);
        Assert.Equal(1, outcome.Facts.SemanticResultCount);
        Assert.Equal(1, outcome.Facts.FusedResultCount);
    }

    [Fact]
    public void OneLexicalHit_StaysFirstWhileSemanticContentExpands()
    {
        TextContentSearchHit lexicalSource = Docs("docs/lexical.md", 2, "lexical section");
        TextContentSearchHit semanticSource = Docs("docs/semantic.md", 7, "semantic section");
        var index = new MaterializingContentIndex([lexicalSource], [semanticSource]);
        var arm = ContentArm(chunks: [Chunk(semanticSource.DisplayPath, 1, semanticSource.ChunkId)]);
        Func<IReadOnlyList<ContentSearchHit>, IReadOnlyList<ContentSearchHit>> rerank =
            SearchTool.BuildContentRerank(
                arm,
                ConceptualQuery,
                Root,
                onConsult: null,
                index,
                contentKinds: null,
                excludeTests: false,
                filePattern: null,
                language: null,
                candidateLimit: 10)!;

        IReadOnlyList<ContentSearchHit> result = rerank([Candidate(lexicalSource, score: 7.5)]);

        Assert.Equal("docs/lexical.md", result[0].Path);
        Assert.Contains(result, hit => hit.Path == "docs/semantic.md");
    }

    [Theory]
    [InlineData("VectorSidecar TryOpen")]
    [InlineData("release process")]
    [InlineData(ConceptualQuery)]
    public void DecisiveMultiHit_ExcludesSemanticOnlyContentForEveryHybridClass(string query)
    {
        TextContentSearchHit firstSource = Docs("docs/first.md", 2, "first section");
        TextContentSearchHit secondSource = Docs("docs/second.md", 4, "second section");
        TextContentSearchHit semanticSource = Docs("docs/semantic.md", 7, "semantic section");
        var index = new MaterializingContentIndex([firstSource, secondSource], [semanticSource]);
        var arm = ContentArm(chunks: [Chunk(semanticSource.DisplayPath, 1, semanticSource.ChunkId)]);
        Func<IReadOnlyList<ContentSearchHit>, IReadOnlyList<ContentSearchHit>> rerank =
            SearchTool.BuildContentRerank(
                arm,
                query,
                Root,
                onConsult: null,
                index,
                contentKinds: null,
                excludeTests: false,
                filePattern: null,
                language: null,
                candidateLimit: 10)!;

        IReadOnlyList<ContentSearchHit> result = rerank(
            [Candidate(firstSource, score: 10.0), Candidate(secondSource, score: 2.0)]);

        Assert.DoesNotContain(result, hit => hit.Path == "docs/semantic.md");
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void TreatmentWithOneLexicalChunk_ProtectsItAheadOfTheSemanticChunk()
    {
        TextContentSearchHit lexical = Docs("docs/shared.md", 2, "lexical section");
        TextContentSearchHit semantic = Docs("docs/shared.md", 80, "semantic section");
        ITextContentSearchIndex index = new MaterializingContentIndex([lexical], [semantic]);
        var arm = ContentArm(chunks: [Chunk(semantic.DisplayPath, 1, semantic.ChunkId)], diagnostics: ServedDiagnostics());

        SearchTool.ContentCanaryOutcome outcome = Run(index, ConceptualQuery, TreatmentWorkspace, arm);

        Assert.True(
            outcome.Result.Output.IndexOf("lexical section", StringComparison.Ordinal) <
            outcome.Result.Output.IndexOf("semantic section", StringComparison.Ordinal));
        Assert.Equal(1, outcome.Facts!.SemanticContributionCount);
    }

    [Fact]
    public void TreatmentWithFilteredLeadingSemanticHits_RefillsToAnAllowedChunk()
    {
        TextContentSearchHit[] blocked = Enumerable.Range(1, 6)
            .Select(i => Docs($"docs/blocked/{i}.md", i, $"blocked {i}"))
            .ToArray();
        TextContentSearchHit allowed = Docs("docs/allowed/hit.md", 20, "allowed semantic result");
        var index = new MaterializingContentIndex([], [.. blocked, allowed]);
        var arm = ContentArm(
            chunks:
            [
                .. blocked.Select((hit, i) => Chunk(hit.DisplayPath, i + 1, hit.ChunkId)),
                Chunk(allowed.DisplayPath, 7, allowed.ChunkId),
            ],
            diagnostics: ServedDiagnostics());

        SearchTool.ContentCanaryOutcome outcome = SearchTool.RunContentWithCanary(
            index, ConceptualQuery, 6, json: false, compactBanner: null,
            filePattern: "docs/allowed/**", language: null,
            suggestionLookup: null, productionRerank: null, CanaryMode.On, "content", semanticDisabled: false,
            TreatmentWorkspace, Root, UtcDate, () => "ready", foreignWorkspace: false,
            treatmentArmFactory: () => arm, excludeTests: true);

        Assert.Contains("docs/allowed/hit.md", outcome.Result.Output, StringComparison.Ordinal);
        Assert.True(index.LastMaterializeExcludeTests);
        Assert.True(arm.LastChunkLimit >= 7);
    }

    [Fact]
    public void SemanticOnlyServedPath_ReceivesContentReadFollowUpAttribution()
    {
        const string displayPath = "docs/semantic.md";
        TextContentSearchHit semanticOnly = Docs(displayPath, 1, "semantic metadata snippet");
        ITextContentSearchIndex index = new MaterializingContentIndex([], [semanticOnly]);
        var arm = ContentArm(chunks: [Chunk(displayPath, 1, semanticOnly.ChunkId)], diagnostics: ServedDiagnostics());
        SearchTool.ContentCanaryOutcome outcome = Run(index, ConceptualQuery, TreatmentWorkspace, arm);
        string telemetryDb = Path.Combine(_temp, "semantic-only-telemetry.db");
        string symbolsDb = Path.Combine(_temp, ".miller", "symbols.db");
        string sourcePath = Path.Combine(_temp, "semantic-source.md");
        File.WriteAllText(sourcePath, "semantic metadata snippet");
        var workspace = new WorkspaceContext(
            _temp,
            symbolsDb,
            telemetryDb,
            Path.Combine(_temp, "workspaces.db"),
            Path.Combine(_temp, ".tools"),
            TreatmentWorkspace);
        var store = new ContentCorpusExternalStore();
        ExternalContentImportResult imported = store.Import(
            ContentCorpusSidecar.ContentDbPathFor(symbolsDb), sourcePath, displayPath: displayPath);
        var content = new ContentTool(workspace, store);

        using (var ledger = TelemetryLedger.Open(telemetryDb, TreatmentWorkspace, _temp))
        {
            using (TelemetryScope search = ledger.Measure("search", "content"))
            {
                SearchTool.StampContentCanary(
                    search,
                    CanaryMode.On,
                    outcome.Facts!,
                    outcome.ResultPathHashes,
                    outcome.ResultHashTruncated,
                    outcome.ServingPolicy);
                search.ResultCount = 1;
                search.Outcome = TelemetryOutcome.Ok;
            }

            using TelemetryScope read = ledger.Measure("content", null);
            string output = content.Content("read", source_id: imported.SourceId, line: 1, context_lines: 0);
            Assert.Contains("semantic metadata snippet", output, StringComparison.Ordinal);
        }

        IReadOnlyList<CanaryRow> rows = CanaryLedgerReader.ReadCanaryRows(telemetryDb);
        IReadOnlyList<CanaryFollowUp> followUps = CanaryLedgerReader.ReadFollowUps(telemetryDb);
        IReadOnlySet<string> attributed = CanaryLedgerReader.AttributedRowIds(rows, followUps);

        CanaryRow row = Assert.Single(rows);
        Assert.Contains(row.Id, attributed);
    }

    [Fact]
    public void TreatmentWithoutMaterializer_AndSemanticFallbacksStayLexicalByteIdentical()
    {
        ITextContentSearchIndex index = ContentIndex(Docs("docs/lexical.md", 1, "lexical"));
        string lexical = SearchTool.RunContentCorpus(index, ConceptualQuery, 10, json: false, out _, out _);

        SearchTool.ContentCanaryOutcome noMaterializer = Run(
            index,
            ConceptualQuery,
            TreatmentWorkspace,
            ContentArm(chunks: [Chunk("docs/semantic.md", 1)], diagnostics: ServedDiagnostics()));
        SearchTool.ContentCanaryOutcome unavailable = Run(
            index,
            ConceptualQuery,
            TreatmentWorkspace,
            ContentArm(unavailable: "building", diagnostics: BuildingDiagnostics()));
        SearchTool.ContentCanaryOutcome empty = Run(
            index,
            ConceptualQuery,
            TreatmentWorkspace,
            ContentArm(diagnostics: ServedDiagnostics()));

        Assert.Equal(lexical, noMaterializer.Result.Output);
        Assert.Equal(lexical, unavailable.Result.Output);
        Assert.Equal(lexical, empty.Result.Output);
    }

    [Fact]
    public void SemanticContributionCount_CountsOnlyRowsWhereSemanticOutranksLexical()
    {
        ITextContentSearchIndex index = ContentIndex(
            Docs("docs/a.md", 1, "alpha"), Docs("docs/b.md", 2, "beta"), Docs("docs/c.md", 3, "gamma"));
        var arm = ContentArm(chunks:
        [
            Chunk("docs/b.md", 1, DocsChunkId("docs/b.md", 2)),
            Chunk("docs/a.md", 3, DocsChunkId("docs/a.md", 1)),
        ], diagnostics: ServedDiagnostics());

        SearchTool.ContentCanaryOutcome outcome = Run(index, ConceptualQuery, TreatmentWorkspace, arm);

        CanaryCallFacts facts = outcome.Facts!;
        Assert.Equal(CanaryArm.Treatment, Arm(facts));
        Assert.Equal(2, facts.SemanticResultCount);
        Assert.Equal(1, facts.SemanticContributionCount);
    }

    [Fact]
    public void TreatmentUnit_WhenTheArtifactIsUnavailable_ServesLexicalAndRecordsTheFallback()
    {
        ITextContentSearchIndex index = ContentIndex(Docs("docs/a.md", 1, "alpha"), Docs("docs/b.md", 2, "beta"));
        var arm = ContentArm(unavailable: "the vector artifact is building", diagnostics: BuildingDiagnostics());

        SearchTool.ContentCanaryOutcome outcome = Run(index, ConceptualQuery, TreatmentWorkspace, arm);

        Assert.Equal(CanaryArm.Treatment, Arm(outcome.Facts!));
        Assert.Equal(1, arm.ChunkQueries);
        Assert.Equal(
            SearchTool.RunContentCorpus(index, ConceptualQuery, 10, json: false, out _, out _),
            outcome.Result.Output);

        CanaryCallFacts facts = outcome.Facts!;
        Assert.Equal("vectors_building", facts.FallbackReason);
        Assert.Null(facts.SemanticResultCount);
        Assert.Null(facts.FusedResultCount);
        Assert.Null(facts.FusionProfile);
    }

    [Theory]
    [InlineData(SemanticFallbackKind.VectorsMissing, "vectors_missing")]
    [InlineData(SemanticFallbackKind.VectorsStale, "vectors_stale")]
    [InlineData(SemanticFallbackKind.VectorsIncompatible, "vectors_incompatible")]
    [InlineData(SemanticFallbackKind.VectorsBuilding, "vectors_building")]
    [InlineData(SemanticFallbackKind.DiskBlocked, "disk_blocked")]
    [InlineData(SemanticFallbackKind.EmbedTimeout, "embed_timeout")]
    [InlineData(SemanticFallbackKind.CircuitOpen, "circuit_open")]
    public void TypedVectorFallback_ReachesTheLedgerAndAggregateExportUnchanged(
        SemanticFallbackKind fallbackKind,
        string expectedReason)
    {
        ITextContentSearchIndex index = ContentIndex(Docs("docs/a.md", 1, "alpha"));
        SearchTool.ContentCanaryOutcome outcome = Run(
            index,
            ConceptualQuery,
            TreatmentWorkspace,
            ContentArm(unavailable: "typed vector fallback", diagnostics: FallbackDiagnostics(fallbackKind)));
        string telemetryDb = Path.Combine(_temp, $"fallback-{fallbackKind}.db");

        using (var ledger = TelemetryLedger.Open(telemetryDb, TreatmentWorkspace, _temp))
        {
            for (int i = 0; i < 5; i++)
            {
                using TelemetryScope scope = ledger.Measure("search", "content");
                SearchTool.StampContentCanary(
                    scope,
                    CanaryMode.On,
                    outcome.Facts!,
                    outcome.ResultPathHashes,
                    outcome.ResultHashTruncated,
                    outcome.ServingPolicy);
                scope.ResultCount = 1;
                scope.Outcome = TelemetryOutcome.Ok;
            }
        }

        using JsonDocument export = JsonDocument.Parse(CanaryExport.BuildJson(
            telemetryDb,
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 12, 31),
            new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        JsonElement unit = Assert.Single(export.RootElement.GetProperty("units").EnumerateArray());

        Assert.Equal(expectedReason, outcome.Facts!.FallbackReason);
        Assert.Equal(5, unit.GetProperty("fallback_reason_counts").GetProperty(expectedReason).GetInt32());
    }

    [Fact]
    public void ContentRow_CarriesPathHashesOnly_NameAndQualifiedArraysAbsent()
    {
        ITextContentSearchIndex index = ContentIndex(Docs("docs/a.md", 1, "alpha"), Docs("docs/b.md", 2, "beta"));
        SearchTool.ContentCanaryOutcome outcome = Run(index, ConceptualQuery, ControlWorkspace, treatmentArm: null);

        JsonElement metadata = Stamp(outcome);

        Assert.Equal(2, metadata.GetProperty("canary_result_path_hashes").GetArrayLength());
        Assert.False(metadata.TryGetProperty("canary_result_name_hashes", out _));
        Assert.False(metadata.TryGetProperty("canary_result_qualified_hashes", out _));
        Assert.False(metadata.GetProperty("canary_result_hash_truncated").GetBoolean());
    }

    [Fact]
    public void ElevenContentRows_CapThePathHashesAtTenWithTheTruncationFlagAndStillNoNameArray()
    {
        ContentSearchHitFixture[] rows = Enumerable.Range(0, 11)
            .Select(i => new ContentSearchHitFixture($"docs/f{i}.md", i + 1, $"word{i}"))
            .ToArray();
        ITextContentSearchIndex index = ContentIndex([.. rows.Select(r => Docs(r.Path, r.Line, r.Word))]);

        SearchTool.ContentCanaryOutcome outcome = Run(index, ConceptualQuery, ControlWorkspace, treatmentArm: null, limit: 11);

        Assert.Equal(10, outcome.ResultPathHashes.Count);
        Assert.True(outcome.ResultHashTruncated);

        JsonElement metadata = Stamp(outcome);
        Assert.Equal(10, metadata.GetProperty("canary_result_path_hashes").GetArrayLength());
        Assert.True(metadata.GetProperty("canary_result_hash_truncated").GetBoolean());
        Assert.False(metadata.TryGetProperty("canary_result_name_hashes", out _));
    }

    [Fact]
    public void IneligibleContentCall_RecordsEligibilityOnlyWithoutBucketCountersOrHashes()
    {
        ITextContentSearchIndex index = ContentIndex(Docs("docs/a.md", 1, "alpha"), Docs("docs/b.md", 2, "beta"));
        SearchTool.ContentCanaryOutcome outcome = Run(
            index, ConceptualQuery, ControlWorkspace, treatmentArm: null, vectorState: "unavailable");

        Assert.Empty(outcome.ResultPathHashes);

        JsonElement metadata = Stamp(outcome);
        Assert.Equal("ineligible", metadata.GetProperty("canary_arm").GetString());
        Assert.Equal(CanaryEligibility.IneligibleVectorsUnavailable, metadata.GetProperty("canary_eligibility").GetString());
        Assert.False(metadata.TryGetProperty("canary_bucket", out _));
        Assert.False(metadata.TryGetProperty("canary_lexical_result_count", out _));
        Assert.False(metadata.TryGetProperty("canary_result_path_hashes", out _));
    }

    [Theory]
    [InlineData(CanaryMode.On)]
    [InlineData(CanaryMode.Decision)]
    public void SemanticDisabled_IsInertLikeCanaryOff_NoProbeNoFactsByteIdentical(CanaryMode mode)
    {
        ITextContentSearchIndex index = ContentIndex(Docs("docs/a.md", 1, "alpha"), Docs("docs/b.md", 2, "beta"));

        SearchTool.ContentCanaryOutcome outcome = SearchTool.RunContentWithCanary(
            index, ConceptualQuery, 10, json: false, compactBanner: null, filePattern: null, language: null,
            suggestionLookup: null, productionRerank: null, mode, "content", semanticDisabled: true,
            ControlWorkspace, Root, UtcDate,
            () => throw new InvalidOperationException("the vector probe must not run when semantic is off"),
            foreignWorkspace: false, treatmentArmFactory: null);

        Assert.Null(outcome.Facts);
        Assert.Empty(outcome.ResultPathHashes);
        Assert.Equal(
            SearchTool.RunContentCorpus(index, ConceptualQuery, 10, json: false, out _, out _),
            outcome.Result.Output);
    }

    [Fact]
    public void ServedPathHash_MatchesAContentReadTargetHashForThatPath()
    {
        ITextContentSearchIndex index = ContentIndex(Docs("docs/design.md", 1, "alpha"), Docs("docs/other.md", 2, "beta"));
        SearchTool.ContentCanaryOutcome outcome = Run(index, ConceptualQuery, ControlWorkspace, treatmentArm: null);

        Assert.Contains(TargetHash("docs/design.md"), outcome.ResultPathHashes);
    }

    private static SearchTool.ContentCanaryOutcome Run(
        ITextContentSearchIndex index,
        string query,
        string workspaceId,
        ISemanticTextArm? treatmentArm,
        Func<IReadOnlyList<ContentSearchHit>, IReadOnlyList<ContentSearchHit>>? productionRerank = null,
        CanaryMode mode = CanaryMode.On,
        bool semanticDisabled = false,
        string vectorState = "ready",
        int limit = 10,
        SemanticMode semanticMode = SemanticMode.On) =>
        SearchTool.RunContentWithCanary(
            index, query, limit, json: false, compactBanner: null, filePattern: null, language: null,
            suggestionLookup: null, productionRerank, mode, "content", semanticDisabled, workspaceId, Root, UtcDate,
            () => vectorState,
            foreignWorkspace: false,
            treatmentArmFactory: treatmentArm is null ? null : () => treatmentArm,
            semanticMode: semanticMode);

    private static int Bucket(string workspaceId) =>
        CanaryAssignment.Bucket(CanaryAssignment.HybridExperimentId, workspaceId, UtcDate, CanaryQueryClass.DocsLike);

    private static string Arm(CanaryCallFacts facts) => CanaryAssignment.ResolveArm(
        CanaryAssignment.Bucket(CanaryAssignment.HybridExperimentId, facts.WorkspaceId, facts.UtcDate, facts.QueryClass));

    private JsonElement Stamp(SearchTool.ContentCanaryOutcome outcome, CanaryMode mode = CanaryMode.On)
    {
        using TelemetryLedger ledger =
            TelemetryLedger.Open(Path.Combine(_temp, "telemetry-" + Guid.NewGuid() + ".db"), "ws-content", _temp);
        using TelemetryScope scope = ledger.Measure("search", "content");
        SearchTool.StampContentCanary(
            scope,
            mode,
            outcome.Facts!,
            outcome.ResultPathHashes,
            outcome.ResultHashTruncated,
            outcome.ServingPolicy);
        return JsonDocument.Parse(scope.MetadataJson).RootElement.Clone();
    }

    private string TargetHash(string raw)
    {
        using TelemetryLedger ledger =
            TelemetryLedger.Open(Path.Combine(_temp, "target-" + Guid.NewGuid() + ".db"), "ws-content", _temp);
        using TelemetryScope scope = ledger.Measure("content", "read");
        scope.SetTarget(raw);
        return scope.TargetHash!;
    }

    private static ITextContentSearchIndex ContentIndex(params TextContentSearchHit[] hits) => new StubContentIndex(hits);

    private static FakeContentArm ContentArm(
        IReadOnlyList<SemanticHit>? chunks = null,
        string? unavailable = null,
        SemanticQueryDiagnostics? diagnostics = null) =>
        new() { Chunks = chunks ?? [], Unavailable = unavailable, Diagnostics = diagnostics };

    private static SemanticHit Chunk(string path, int rank, string? docId = null) =>
        new(SymbolId: null, DocId: docId ?? path + "#" + rank, path, rank, Cosine: 0.9 - (rank * 0.01));

    private static string DocsChunkId(string path, int line) =>
        TextContentKind.WorkspaceDocs + ":" + path + ":" + line;

    private static SemanticQueryDiagnostics ServedDiagnostics() =>
        new(
            SemanticFallbackKind.None,
            "cpu",
            ColdEmbed: false,
            EmbedMs: 5,
            KnnMs: 3,
            new SemanticGenerationIdentity("sha256:abcdef0123456789aa", "cards-v1", "gen-7", "w1", "r1", RrfFusion.FusionProfile),
            FusionProfile: null);

    private static SemanticQueryDiagnostics BuildingDiagnostics() =>
        FallbackDiagnostics(SemanticFallbackKind.VectorsBuilding);

    private static SemanticQueryDiagnostics FallbackDiagnostics(SemanticFallbackKind fallbackKind) =>
        new(
            fallbackKind,
            CanaryBackend.None,
            ColdEmbed: false,
            EmbedMs: null,
            KnnMs: null,
            Identity: null,
            FusionProfile: null);

    private static TextContentSearchHit Docs(string path, int line, string snippet) =>
        new(
            TextContentKind.WorkspaceDocs + ":" + path,
            TextContentKind.WorkspaceDocs + ":" + path + ":" + line,
            TextContentKind.WorkspaceDocs,
            path,
            Url: null,
            DisplayPath: path,
            "markdown",
            Score: 2.0,
            line,
            LineStart: Math.Max(1, line - 1),
            LineEnd: line + 1,
            ByteStart: 0,
            ByteEnd: 64,
            snippet,
            SourceBytes: 128,
            ContainingSymbolId: null,
            ContainingSymbolName: null);

    private static ContentSearchHit Candidate(TextContentSearchHit hit, double score) =>
        new(
            hit.DisplayPath,
            score,
            hit.Line,
            hit.Snippet,
            hit.Language,
            hit.SourceBytes,
            hit.ChunkId);

    public void Dispose()
    {
        if (Directory.Exists(_temp))
            Directory.Delete(_temp, recursive: true);
    }

    private sealed record ContentSearchHitFixture(string Path, int Line, string Word);

    private sealed class FakeContentArm : ISemanticTextArm
    {
        public IReadOnlyList<SemanticHit> Chunks { get; init; } = [];

        public string? Unavailable { get; init; }

        public SemanticQueryDiagnostics? Diagnostics { get; init; }

        public int ChunkQueries { get; private set; }

        public int LastChunkLimit { get; private set; }

        public SemanticQueryResult QuerySymbols(string workspaceRoot, string query, int k, Func<VectorMatch, bool>? allow) =>
            throw new NotSupportedException("The content arm serves chunk hits only.");

        public SemanticQueryResult QueryChunks(string workspaceRoot, string query, int k)
        {
            ChunkQueries++;
            LastChunkLimit = k;
            SemanticQueryResult result = Unavailable is { } reason
                ? SemanticQueryResult.Unavailable(reason)
                : new SemanticQueryResult([.. Chunks.Take(k)], null);
            return Diagnostics is { } diagnostics ? result with { Diagnostics = diagnostics } : result;
        }
    }

    private sealed class StubContentIndex(IReadOnlyList<TextContentSearchHit> hits) : ITextContentSearchIndex
    {
        public int DocumentCount => hits.Count;

        public IReadOnlyList<TextContentSearchHit> Search(string query, string contentKind, int limit, bool excludeTests) =>
            Search(query, [contentKind], limit, excludeTests);

        public IReadOnlyList<TextContentSearchHit> Search(
            string query, IReadOnlyCollection<string> contentKinds, int limit, bool excludeTests) =>
            [.. hits.Where(hit => contentKinds.Contains(hit.ContentKind)).Take(limit)];
    }

    private sealed class MaterializingContentIndex(
        IReadOnlyList<TextContentSearchHit> lexical,
        IReadOnlyList<TextContentSearchHit> materialized) : ITextContentSearchIndex, ISemanticContentLookup
    {
        public int DocumentCount => lexical.Count + materialized.Count;

        public bool LastMaterializeExcludeTests { get; private set; }

        public IReadOnlyList<TextContentSearchHit> Search(
            string query, string contentKind, int limit, bool excludeTests) =>
            Search(query, [contentKind], limit, excludeTests);

        public IReadOnlyList<TextContentSearchHit> Search(
            string query, IReadOnlyCollection<string> contentKinds, int limit, bool excludeTests) =>
            [.. lexical.Where(hit => contentKinds.Contains(hit.ContentKind)).Take(limit)];

        public IReadOnlyList<TextContentSearchHit> Materialize(
            IReadOnlyCollection<string> chunkIds,
            IReadOnlyCollection<string> contentKinds,
            bool excludeTests = false)
        {
            LastMaterializeExcludeTests = excludeTests;
            return [.. materialized.Where(hit => chunkIds.Contains(hit.ChunkId) && contentKinds.Contains(hit.ContentKind))];
        }
    }
}
