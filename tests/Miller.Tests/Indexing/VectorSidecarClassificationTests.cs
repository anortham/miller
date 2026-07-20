using Miller.Indexing;
using Miller.Indexing.Semantic;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class VectorSidecarClassificationTests
{
    private const string Root = "/ws";

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

    private sealed class FakeProbe(IReadOnlyList<string> retained, IReadOnlyList<string> existing) : IVectorFileProbe
    {
        public bool FileExists(string path) => existing.Contains(path, StringComparer.Ordinal);

        public IReadOnlyList<string> EnumerateRetainedGenerations(string millerDir) => retained;
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
