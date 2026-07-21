using System.Text.Json;
using Miller.Core.Search;
using Miller.Indexing;
using Miller.Indexing.Semantic;
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
    public void EligibleTreatmentUnit_ServesHybridIdenticalToSemanticOnAndRecordsTreatment()
    {
        ITextContentSearchIndex index = ContentIndex(
            Docs("docs/a.md", 1, "alpha"), Docs("docs/b.md", 2, "beta"), Docs("docs/c.md", 3, "gamma"));
        SemanticQueryDiagnostics diagnostics = ServedDiagnostics();
        var treatmentArm = ContentArm(chunks: [Chunk("docs/c.md", 1), Chunk("docs/b.md", 2)], diagnostics: diagnostics);
        var referenceArm = ContentArm(chunks: [Chunk("docs/c.md", 1), Chunk("docs/b.md", 2)], diagnostics: diagnostics);

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
    public void SemanticContributionCount_CountsOnlyRowsWhereSemanticOutranksLexical()
    {
        ITextContentSearchIndex index = ContentIndex(
            Docs("docs/a.md", 1, "alpha"), Docs("docs/b.md", 2, "beta"), Docs("docs/c.md", 3, "gamma"));
        var arm = ContentArm(chunks: [Chunk("docs/b.md", 1), Chunk("docs/a.md", 3)], diagnostics: ServedDiagnostics());

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

    [Fact]
    public void SemanticDisabled_IsInertLikeCanaryOff_NoProbeNoFactsByteIdentical()
    {
        ITextContentSearchIndex index = ContentIndex(Docs("docs/a.md", 1, "alpha"), Docs("docs/b.md", 2, "beta"));

        SearchTool.ContentCanaryOutcome outcome = SearchTool.RunContentWithCanary(
            index, ConceptualQuery, 10, json: false, compactBanner: null, filePattern: null, language: null,
            suggestionLookup: null, productionRerank: null, CanaryMode.On, "content", semanticDisabled: true,
            ControlWorkspace, Root, UtcDate,
            () => throw new InvalidOperationException("the vector probe must not run when semantic is off"),
            crossWorkspaceNoGeneration: false, treatmentArm: null);

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
        int limit = 10) =>
        SearchTool.RunContentWithCanary(
            index, query, limit, json: false, compactBanner: null, filePattern: null, language: null,
            suggestionLookup: null, productionRerank, mode, "content", semanticDisabled, workspaceId, Root, UtcDate,
            () => vectorState, crossWorkspaceNoGeneration: false, treatmentArm);

    private static int Bucket(string workspaceId) =>
        CanaryAssignment.Bucket(CanaryAssignment.HybridExperimentId, workspaceId, UtcDate, CanaryQueryClass.DocsLike);

    private static string Arm(CanaryCallFacts facts) => CanaryAssignment.ResolveArm(
        CanaryAssignment.Bucket(CanaryAssignment.HybridExperimentId, facts.WorkspaceId, facts.UtcDate, facts.QueryClass));

    private JsonElement Stamp(SearchTool.ContentCanaryOutcome outcome)
    {
        using TelemetryLedger ledger =
            TelemetryLedger.Open(Path.Combine(_temp, "telemetry-" + Guid.NewGuid() + ".db"), "ws-content", _temp);
        using TelemetryScope scope = ledger.Measure("search", "content");
        SearchTool.StampContentCanary(
            scope, CanaryMode.On, outcome.Facts!, outcome.ResultPathHashes, outcome.ResultHashTruncated);
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

    private static SemanticHit Chunk(string path, int rank) =>
        new(SymbolId: null, DocId: path + "#" + rank, path, rank, Cosine: 0.9 - (rank * 0.01));

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
        new(
            SemanticFallbackKind.VectorsBuilding,
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

        public SemanticQueryResult QuerySymbols(string workspaceRoot, string query, int k, Func<VectorMatch, bool>? allow) =>
            throw new NotSupportedException("The content arm serves chunk hits only.");

        public SemanticQueryResult QueryChunks(string workspaceRoot, string query, int k)
        {
            ChunkQueries++;
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
}
