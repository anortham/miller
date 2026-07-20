using System.Text.Json;
using Miller.Indexing;
using Miller.Indexing.Semantic;
using Miller.Server.Telemetry;
using Miller.Server.Tools;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the vectors-v1 §Status vocabulary on the pure render seam: the compact line carries exactly the frozen
/// strings and reports the laggier cursor, while exact revisions, identity fields, the serving generation and the
/// retained inventory appear only in JSON.
/// </summary>
public sealed class WorkspaceVectorFactsRenderTests
{
    private static readonly SemanticGenerationIdentity Identity =
        MillerSemanticContract.PinnedIdentity(MillerSemanticContract.DefaultEncoder);

    private static WorkspaceFacts Facts(VectorSidecarFacts? vectors) => new(
        Root: "/repo",
        WorkspaceId: "ws-123",
        DbPath: "/repo/.miller/symbols.db",
        IsLeader: true,
        DocumentCount: 565,
        KnownExtensionsCount: 7,
        BuiltRevision: 42,
        LatestObservedRevision: 42,
        IndexFresh: true,
        QueueEmpty: true,
        Vectors: vectors);

    private static string CompactVectorsLine(VectorSidecarFacts vectors)
    {
        string text = WorkspaceRender.Status(Facts(vectors), TelemetrySummary.Empty, json: false);
        string? line = text.Split('\n').FirstOrDefault(l => l.StartsWith("vectors: ", StringComparison.Ordinal));
        return line ?? string.Empty;
    }

    [Fact]
    public void Compact_Ready_CaughtUp()
    {
        Assert.Equal("vectors: ready", CompactVectorsLine(Ready()));
    }

    [Fact]
    public void Compact_ReadyButBehind_ReportsPendingFilesOfTheLaggierCursor()
    {
        VectorSidecarFacts facts = Ready() with
        {
            SymbolCursor = Cursor("symbol", 40, 42, pendingFiles: 3),
            ChunkCursor = Cursor("chunk", 30, 42, pendingFiles: 11),
        };

        Assert.Equal("vectors: ready (updating; 11 files pending)", CompactVectorsLine(facts));
    }

    [Fact]
    public void Compact_ReadyServedFromARetainedGeneration_StillReportsPlainReady()
    {
        VectorSidecarFacts facts = Ready() with { ServingRole = "retained", ServingTag = "abcd1234abcd1234" };

        Assert.Equal("vectors: ready", CompactVectorsLine(facts));
    }

    [Fact]
    public void Compact_Building_ReportsProgressAndThatItIsNotQueryable()
    {
        VectorSidecarFacts facts = new("building", "/repo/.miller/vectors.db", "still building")
        {
            BuildProgressPercent = 42,
        };

        Assert.Equal("vectors: building 42% (not queryable)", CompactVectorsLine(facts));
    }

    [Fact]
    public void Compact_Unavailable_StatesTheReason()
    {
        var facts = new VectorSidecarFacts("unavailable", "/repo/.miller/vectors.db", "no vector artifact exists");

        Assert.Equal("vectors: unavailable (no vector artifact exists)", CompactVectorsLine(facts));
    }

    [Fact]
    public void Compact_Incompatible_IsBareVocabulary()
    {
        var facts = new VectorSidecarFacts("incompatible", "/repo/.miller/vectors.db", "built by another encoder");

        Assert.Equal("vectors: incompatible", CompactVectorsLine(facts));
    }

    [Fact]
    public void Compact_CircuitOpen_IsBareVocabulary()
    {
        var facts = new VectorSidecarFacts("circuit-open", "/repo/.miller/vectors.db", "restarts exhausted");

        Assert.Equal("vectors: circuit-open", CompactVectorsLine(facts));
    }

    [Fact]
    public void Compact_DiskBlocked_IsBareVocabulary()
    {
        var facts = new VectorSidecarFacts("disk-blocked", "/repo/.miller/vectors.db", "preflight failed");

        Assert.Equal("vectors: disk-blocked", CompactVectorsLine(facts));
    }

    [Fact]
    public void Compact_Downloading_IsBareVocabulary()
    {
        var facts = new VectorSidecarFacts("downloading", "/repo/.miller/vectors.db", null);

        Assert.Equal("vectors: downloading", CompactVectorsLine(facts));
    }

    [Fact]
    public void Compact_Disabled_RendersNothingAndLeavesOutputByteIdentical()
    {
        string withoutSemantic = WorkspaceRender.Status(Facts(null), TelemetrySummary.Empty, json: false);
        string disabled = WorkspaceRender.Status(
            Facts(new VectorSidecarFacts("disabled", "/repo/.miller/vectors.db", null)),
            TelemetrySummary.Empty,
            json: false);

        Assert.Equal(withoutSemantic, disabled);
    }

    [Fact]
    public void Json_Disabled_LeavesTheDocumentByteIdentical()
    {
        string withoutSemantic = WorkspaceRender.Status(Facts(null), TelemetrySummary.Empty, json: true);
        string disabled = WorkspaceRender.Status(
            Facts(new VectorSidecarFacts("disabled", "/repo/.miller/vectors.db", null)),
            TelemetrySummary.Empty,
            json: true);

        Assert.Equal(withoutSemantic, disabled);
    }

    [Fact]
    public void Json_CarriesExactRevisionsCoverageIdentityAndGenerations()
    {
        VectorSidecarFacts facts = Ready() with
        {
            SymbolCursor = Cursor("symbol", 40, 42, pendingFiles: 3, lastError: "embed failed",
                lastErrorAt: "2026-07-20T10:00:00Z"),
            ChunkCursor = Cursor("chunk", 30, 42, pendingFiles: 11),
            ServingRole = "retained",
            ServingTag = "abcd1234abcd1234",
            Retained = [new VectorGenerationFacts("abcd1234abcd1234", "/repo/.miller/vectors.gen-abcd1234abcd1234.db")],
        };

        using var doc = JsonDocument.Parse(WorkspaceRender.Status(Facts(facts), TelemetrySummary.Empty, json: true));
        JsonElement vectors = doc.RootElement.GetProperty("index").GetProperty("vectors");

        Assert.Equal("ready", vectors.GetProperty("state").GetString());
        Assert.Equal("retained", vectors.GetProperty("serving_role").GetString());
        Assert.Equal("abcd1234abcd1234", vectors.GetProperty("serving_tag").GetString());
        Assert.Equal("art-1", vectors.GetProperty("artifact_id").GetString());

        JsonElement symbol = vectors.GetProperty("symbol_cursor");
        Assert.Equal(40, symbol.GetProperty("completed_revision").GetInt64());
        Assert.Equal(42, symbol.GetProperty("target_revision").GetInt64());
        Assert.Equal(3, symbol.GetProperty("pending_files").GetInt64());
        Assert.Equal("embed failed", symbol.GetProperty("last_error").GetString());
        Assert.Equal("2026-07-20T10:00:00Z", symbol.GetProperty("last_error_at").GetString());
        Assert.Equal(11, vectors.GetProperty("chunk_cursor").GetProperty("pending_files").GetInt64());

        JsonElement identity = vectors.GetProperty("identity");
        Assert.Equal(Identity.EncoderFingerprint, identity.GetProperty("encoder_fingerprint").GetString());
        Assert.Equal(Identity.StorageSchema, identity.GetProperty("storage_schema").GetString());
        Assert.Equal(Identity.CorpusGeneration, identity.GetProperty("corpus_generation").GetString());
        Assert.Equal(Identity.WriterVersion, identity.GetProperty("writer_version").GetString());
        Assert.Equal(Identity.MinReaderVersion, identity.GetProperty("min_reader_version").GetString());
        Assert.Equal(Identity.FusionProfile, identity.GetProperty("fusion_profile").GetString());

        JsonElement retained = Assert.Single(vectors.GetProperty("retained_generations").EnumerateArray().ToList());
        Assert.Equal("abcd1234abcd1234", retained.GetProperty("tag").GetString());
    }

    [Fact]
    public void Health_RendersTheSameVocabularyAndFacts()
    {
        VectorSidecarFacts vectors = Ready() with
        {
            SymbolCursor = Cursor("symbol", 40, 42, pendingFiles: 5),
            ChunkCursor = Cursor("chunk", 42, 42, pendingFiles: 0),
        };
        var health = new WorkspaceHealthFacts(
            StatusFacts: Facts(vectors),
            Telemetry: TelemetrySummary.Empty,
            TelemetryHealth: new TelemetryHealthFacts(OkCount: 1, EmptyCount: 0, ErrorCount: 0),
            Extraction: EmptyExtractionHealth(),
            Warnings: [],
            RecommendedActions: [],
            State: HealthState.Ready,
            Summary: "index readable");

        Assert.Contains("vectors: ready (updating; 5 files pending)",
            WorkspaceRender.Health(health, json: false), StringComparison.Ordinal);

        using var doc = JsonDocument.Parse(WorkspaceRender.Health(health, json: true));
        Assert.Equal(40, doc.RootElement.GetProperty("index").GetProperty("vectors")
            .GetProperty("symbol_cursor").GetProperty("completed_revision").GetInt64());
    }

    [Fact]
    public void Health_Disabled_LeavesBothFormatsByteIdentical()
    {
        WorkspaceHealthFacts Build(VectorSidecarFacts? vectors) => new(
            StatusFacts: Facts(vectors),
            Telemetry: TelemetrySummary.Empty,
            TelemetryHealth: new TelemetryHealthFacts(OkCount: 1, EmptyCount: 0, ErrorCount: 0),
            Extraction: EmptyExtractionHealth(),
            Warnings: [],
            RecommendedActions: [],
            State: HealthState.Ready,
            Summary: "index readable");

        var disabled = new VectorSidecarFacts("disabled", "/repo/.miller/vectors.db", null);

        Assert.Equal(
            WorkspaceRender.Health(Build(null), json: false),
            WorkspaceRender.Health(Build(disabled), json: false));
        Assert.Equal(
            WorkspaceRender.Health(Build(null), json: true),
            WorkspaceRender.Health(Build(disabled), json: true));
    }

    private static WorkspaceExtractionHealthFacts EmptyExtractionHealth() => new(
        ParseDiagnostics: HealthFactSection<ParseDiagnosticGroup>.FromRows([]),
        CapabilityGaps: HealthFactSection<CapabilityGapGroup>.FromRows([]),
        LanguageCapabilities: HealthFactSection<LanguageCapabilitySummary>.FromRows([]),
        StructuralFacts: HealthFactSection<StructuralFactGroup>.FromRows([]),
        ComplexityMetrics: HealthFactSection<ComplexityMetricGroup>.FromRows([]),
        Files: HealthFactSection<FileStatusGroup>.FromRows([]));

    private static VectorSidecarFacts Ready() => new("ready", "/repo/.miller/vectors.db", null)
    {
        SymbolCursor = Cursor("symbol", 42, 42, pendingFiles: 0),
        ChunkCursor = Cursor("chunk", 42, 42, pendingFiles: 0),
        Identity = Identity,
        ArtifactId = "art-1",
        ServingRole = "active",
        ServingTag = MillerSemanticContract.GenerationTag(Identity),
    };

    private static VectorCursorFacts Cursor(
        string name,
        long completed,
        long target,
        long? pendingFiles = null,
        string? lastError = null,
        string? lastErrorAt = null) =>
        new(name, completed, target, lastError, lastErrorAt) { PendingFiles = pendingFiles };
}
