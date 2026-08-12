using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Indexing.Semantic;
using Miller.Server.Cli;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class VectorSidecarClassificationTests : IDisposable
{
    private const string Root = "/ws";

    private readonly string _storeRoot =
        Path.Combine(Path.GetTempPath(), "miller-vector-classify-" + Guid.NewGuid());

    public VectorSidecarClassificationTests() => Directory.CreateDirectory(_storeRoot);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_storeRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static readonly SemanticGenerationIdentity Pinned =
        MillerSemanticContract.PinnedIdentity(MillerSemanticContract.DefaultEncoder);

    private static readonly SemanticReaderIdentity CompatibleReader =
        new(Pinned.EncoderFingerprint, MillerSemanticContract.MinReaderVersion);

    [Fact]
    public void Ready_CaughtUpCursors_ReportsReadyWithBothCursorsAndIdentity()
    {
        VectorSidecarFacts facts = Classify(Meta());

        Assert.Equal("ready", facts.State);
        Assert.Equal(7, facts.SymbolCursor!.CompletedRevision);
        Assert.Equal(7, facts.SymbolCursor.TargetRevision);
        Assert.Equal(7, facts.ChunkCursor!.CompletedRevision);
        Assert.Equal(Pinned.EncoderFingerprint, facts.Identity!.EncoderFingerprint);
        Assert.Equal("art-1", facts.ArtifactId);
        Assert.Equal(MillerSemanticContract.GenerationTag(Pinned), facts.ServingTag);
        Assert.Equal("active", facts.ServingRole);
    }

    [Fact]
    public void Ready_LaggingChunkCursor_ExposesPerCursorRevisionsAndPicksTheLaggierCursor()
    {
        VectorSidecarFacts facts = Classify(Meta(symbolCompleted: 7, chunkCompleted: 4));

        Assert.Equal("ready", facts.State);
        Assert.Equal("chunk", facts.LaggierCursor!.Name);
        Assert.Equal(3, facts.LaggierCursor.RevisionLag);
    }

    [Fact]
    public void Cursor_LastErrorsAreReportedPerCursor()
    {
        var meta = Meta();
        meta["symbol_last_error"] = "embed failed";
        meta["symbol_last_error_at"] = "2026-07-20T10:00:00Z";

        VectorSidecarFacts facts = Classify(meta);

        Assert.Equal("embed failed", facts.SymbolCursor!.LastError);
        Assert.Equal("2026-07-20T10:00:00Z", facts.SymbolCursor.LastErrorAt);
        Assert.Null(facts.ChunkCursor!.LastError);
    }

    [Fact]
    public void Building_ReportsProgressPercent()
    {
        var meta = Meta();
        meta["build_state"] = "building";
        meta["build_progress_percent"] = "42";

        VectorSidecarFacts facts = Classify(meta);

        Assert.Equal("building", facts.State);
        Assert.Equal(42, facts.BuildProgressPercent);
    }

    [Fact]
    public void CircuitOpenPause_OverridesReady()
    {
        var meta = Meta();
        meta["converge_pause_state"] = "circuit-open";
        meta["converge_pause_reason"] = "sidecar restarts exhausted";

        VectorSidecarFacts facts = Classify(meta);

        Assert.Equal("circuit-open", facts.State);
        Assert.Equal("sidecar restarts exhausted", facts.Reason);
    }

    [Fact]
    public void ModelNotPreparedPause_OverridesReadyAndPreservesTheReason()
    {
        var meta = Meta();
        meta["converge_pause_state"] = "model-not-prepared";
        meta["converge_pause_reason"] = "sidecar reported ready=false (model_not_prepared)";

        VectorSidecarFacts facts = Classify(meta);

        Assert.Equal("model-not-prepared", facts.State);
        Assert.Equal("sidecar reported ready=false (model_not_prepared)", facts.Reason);
    }

    [Fact]
    public void DiskBlockedPause_OverridesBuilding()
    {
        var meta = Meta();
        meta["build_state"] = "building";
        meta["converge_pause_state"] = "disk-blocked";

        VectorSidecarFacts facts = Classify(meta);

        Assert.Equal("disk-blocked", facts.State);
    }

    [Fact]
    public void UnknownPauseState_IsIgnoredRatherThanRenderedVerbatim()
    {
        var meta = Meta();
        meta["converge_pause_state"] = "wat";

        Assert.Equal("ready", Classify(meta).State);
    }

    [Fact]
    public void IncompatibleActive_ServesFromACompatibleRetainedGeneration()
    {
        string retainedPath = Path.Combine(Root, ".miller", "vectors.gen-abcd1234abcd1234.db");
        var opener = new FakeOpener();
        opener.Metas[VectorSidecar.PathFor(Root)] = Meta(fingerprint: "sha256:" + new string('e', 64));
        opener.Metas[retainedPath] = Meta();

        VectorSidecarFacts facts = Classify(opener, [retainedPath]);

        Assert.Equal("ready", facts.State);
        Assert.Equal("retained", facts.ServingRole);
        Assert.Equal("abcd1234abcd1234", facts.ServingTag);
        Assert.Equal(retainedPath, facts.Path);
        Assert.Equal("vectors.gen-abcd1234abcd1234.db", Path.GetFileName(Assert.Single(facts.Retained).Path));
    }

    [Fact]
    public void IncompatibleActive_WithNoCompatibleRetainedGeneration_StaysIncompatible()
    {
        string retainedPath = Path.Combine(Root, ".miller", "vectors.gen-abcd1234abcd1234.db");
        var opener = new FakeOpener();
        opener.Metas[VectorSidecar.PathFor(Root)] = Meta(fingerprint: "sha256:" + new string('e', 64));
        opener.Metas[retainedPath] = Meta(fingerprint: "sha256:" + new string('f', 64));

        VectorSidecarFacts facts = Classify(opener, [retainedPath]);

        Assert.Equal("incompatible", facts.State);
        Assert.Null(facts.ServingRole);
    }

    [Fact]
    public void MissingActive_WithCompatibleRetainedGeneration_ServesFromRetained()
    {
        string retainedPath = Path.Combine(Root, ".miller", "vectors.gen-abcd1234abcd1234.db");
        var opener = new FakeOpener();
        opener.Metas[retainedPath] = Meta();

        VectorSidecarFacts facts = Classify(opener, [retainedPath], activeExists: false);

        Assert.Equal("ready", facts.State);
        Assert.Equal("retained", facts.ServingRole);
    }

    [Fact]
    public void Disabled_ReportsNoGenerationFactsAndTouchesNothing()
    {
        VectorSidecarFacts facts = VectorSidecar.Disabled.Inspect(Root);

        Assert.Equal("disabled", facts.State);
        Assert.Null(facts.SymbolCursor);
        Assert.Null(facts.Identity);
        Assert.Empty(facts.Retained);
    }

    [Fact]
    public void LivePrepareMarker_WithNoArtifact_ReportsDownloadingAndSurfacesTheModel()
    {
        VectorSidecarFacts facts = ClassifyMissingActive(
            MarkerJson("qwen3-0.6b-f16", pid: 4242), markerPidAlive: true);

        Assert.Equal("downloading", facts.State);
        Assert.Equal("qwen3-0.6b-f16", facts.DownloadingModel);
    }

    [Fact]
    public void StalePrepareMarker_DeadPid_LeavesClassificationUnavailable()
    {
        VectorSidecarFacts facts = ClassifyMissingActive(
            MarkerJson("qwen3-0.6b-f16", pid: 4242), markerPidAlive: false);

        Assert.Equal("unavailable", facts.State);
        Assert.Null(facts.DownloadingModel);
    }

    [Fact]
    public void MalformedPrepareMarker_IsIgnored_LeavesClassificationUnavailable()
    {
        VectorSidecarFacts facts = ClassifyMissingActive("{ not json", markerPidAlive: true);

        Assert.Equal("unavailable", facts.State);
        Assert.Null(facts.DownloadingModel);
    }

    [Fact]
    public void PauseState_BeatsALivePrepareMarker()
    {
        var meta = Meta();
        meta["converge_pause_state"] = "circuit-open";
        meta["converge_pause_reason"] = "sidecar restarts exhausted";

        var opener = new FakeOpener();
        opener.Metas[VectorSidecar.PathFor(Root)] = meta;
        var probe = new FakeProbe(
            [], [VectorSidecar.PathFor(Root)], MarkerJson("qwen3-0.6b-f16", pid: 4242), markerPidAlive: true);
        var sidecar = new VectorSidecar(SemanticMode.On, probe, opener, CompatibleReader);

        VectorSidecarFacts facts = sidecar.Inspect(Root);

        Assert.Equal("circuit-open", facts.State);
    }

    [Fact]
    public void Disabled_NeverProbesThePrepareMarker()
    {
        var probe = new FakeProbe([], [], marker: null, markerPidAlive: false) { ThrowOnMarkerRead = true };
        var sidecar = new VectorSidecar(SemanticMode.Off, probe, new FakeOpener(), CompatibleReader);

        VectorSidecarFacts facts = sidecar.Inspect(Root);

        Assert.Equal("disabled", facts.State);
    }

    [Fact]
    public void MarkerFileName_MirrorsTheProducerContract()
    {
        Assert.Equal(SemanticPrepareCli.MarkerFileName, SemanticPrepareMarker.FileName);
    }

    [Fact]
    public void InspectStore_UnstampedArtifactRecordingAModelNotPreparedPause_ReportsThePauseNotTheStampRefusal()
    {
        WorkspaceReadSnapshot snapshot = StoreSnapshot();
        string path = VectorSidecar.PathForStore(_storeRoot, snapshot.ViewId);
        var meta = Meta();
        meta["converge_pause_state"] = "model-not-prepared";
        meta["converge_pause_reason"] = "sidecar reported ready=false (model_not_prepared)";
        var opener = new FakeOpener();
        opener.Metas[path] = meta;
        var sidecar = new VectorSidecar(SemanticMode.On, new FakeProbe([], [path]), opener, CompatibleReader);

        VectorSidecarFacts facts = sidecar.InspectStore(_storeRoot, snapshot);

        Assert.Equal("model-not-prepared", facts.State);
        Assert.Equal("sidecar reported ready=false (model_not_prepared)", facts.Reason);
    }

    [Fact]
    public void InspectStore_UnstampedArtifactWithNoRecordedPause_StillRefusesWithTheStampReason()
    {
        WorkspaceReadSnapshot snapshot = StoreSnapshot();
        string path = VectorSidecar.PathForStore(_storeRoot, snapshot.ViewId);
        var opener = new FakeOpener();
        opener.Metas[path] = Meta();
        var sidecar = new VectorSidecar(SemanticMode.On, new FakeProbe([], [path]), opener, CompatibleReader);

        VectorSidecarFacts facts = sidecar.InspectStore(_storeRoot, snapshot);

        Assert.Equal("unavailable", facts.State);
        Assert.Contains("stamp", facts.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    private WorkspaceReadSnapshot StoreSnapshot() =>
        new(
            _storeRoot,
            "workspace-a",
            "family-a",
            "view-a",
            new WorkspaceFreshnessToken(
                "family-a",
                3,
                "manifest-a",
                17,
                "resolution-a",
                StoreInstanceId: "family-a:gen-001",
                ViewId: "view-a",
                GenerationName: "gen-001",
                ManifestGeneration: 3,
                IndexLevel: IndexLevels.FullMetadataValue,
                LevelStampL1: "l1-a",
                LevelStampL2: "l2-a",
                LevelStampL3: "l3-a"),
            IndexLevels.FullMetadataValue,
            WorkspaceReadMode.FamilyStore,
            GenerationName: "gen-001",
            ManifestGeneration: 3);

    private static VectorSidecarFacts ClassifyMissingActive(string marker, bool markerPidAlive)
    {
        var probe = new FakeProbe([], [], marker, markerPidAlive);
        var sidecar = new VectorSidecar(SemanticMode.On, probe, new FakeOpener(), CompatibleReader);
        return sidecar.Inspect(Root);
    }

    private static string MarkerJson(string model, int pid) =>
        $"{{\"model\":\"{model}\",\"pid\":{pid},\"createdUtc\":\"2026-07-20T18:30:00.0000000Z\"}}";

    private static VectorSidecarFacts Classify(Dictionary<string, string> meta)
    {
        var opener = new FakeOpener();
        opener.Metas[VectorSidecar.PathFor(Root)] = meta;
        return Classify(opener, []);
    }

    private static VectorSidecarFacts Classify(
        FakeOpener opener,
        IReadOnlyList<string> retained,
        bool activeExists = true)
    {
        var probe = new FakeProbe(retained, activeExists ? [VectorSidecar.PathFor(Root)] : []);
        var sidecar = new VectorSidecar(SemanticMode.On, probe, opener, CompatibleReader);
        return sidecar.Inspect(Root);
    }

    private static Dictionary<string, string> Meta(
        string? fingerprint = null,
        long symbolCompleted = 7,
        long chunkCompleted = 7) =>
        new(StringComparer.Ordinal)
        {
            ["contract_version"] = MillerSemanticContract.ContractVersion,
            ["encoder_fingerprint"] = fingerprint ?? Pinned.EncoderFingerprint,
            ["storage_schema"] = Pinned.StorageSchema,
            ["corpus_generation"] = Pinned.CorpusGeneration,
            ["writer_version"] = Pinned.WriterVersion,
            ["min_reader_version"] = Pinned.MinReaderVersion,
            ["fusion_profile"] = Pinned.FusionProfile,
            ["artifact_id"] = "art-1",
            ["build_state"] = "ready",
            ["symbol_completed_revision"] = symbolCompleted.ToString(),
            ["symbol_target_revision"] = "7",
            ["chunk_completed_revision"] = chunkCompleted.ToString(),
            ["chunk_target_revision"] = "7",
        };

    private sealed class FakeProbe(
        IReadOnlyList<string> retained,
        IReadOnlyList<string> existing,
        string? marker = null,
        bool markerPidAlive = false) : IVectorFileProbe
    {
        public bool ThrowOnMarkerRead { get; init; }

        public bool FileExists(string path) => existing.Contains(path, StringComparer.Ordinal);

        public IReadOnlyList<string> EnumerateRetainedGenerations(string millerDir) => retained;

        public string? ReadPrepareMarker(string millerDir) =>
            ThrowOnMarkerRead ? throw new InvalidOperationException("off-mode must not probe the marker") : marker;

        public bool IsProcessAlive(int pid) => markerPidAlive;
    }

    private sealed class FakeOpener : IVectorStoreOpener
    {
        public Dictionary<string, IReadOnlyDictionary<string, string>> Metas { get; } = new(StringComparer.Ordinal);

        public bool TryReadMeta(string path, out IReadOnlyDictionary<string, string> meta, out string failureReason)
        {
            if (Metas.TryGetValue(path, out IReadOnlyDictionary<string, string>? found))
            {
                meta = found;
                failureReason = string.Empty;
                return true;
            }

            meta = new Dictionary<string, string>(StringComparer.Ordinal);
            failureReason = "no artifact";
            return false;
        }

        public VectorStore? OpenStore(string path, out string failureReason)
        {
            failureReason = "tests never open a real store";
            return null;
        }
    }
}
