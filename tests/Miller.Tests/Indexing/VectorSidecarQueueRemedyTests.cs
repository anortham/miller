using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Indexing.Semantic;
using Miller.Indexing.Store;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class VectorSidecarQueueRemedyTests : IDisposable
{
    private readonly string _storeRoot =
        Path.Combine(Path.GetTempPath(), "miller-vector-queue-" + Guid.NewGuid().ToString("N"));

    public VectorSidecarQueueRemedyTests() => Directory.CreateDirectory(_storeRoot);

    public void Dispose() => Directory.Delete(_storeRoot, recursive: true);

    [Fact]
    public void AnUnconvergedViewWithAHealthyQueueStillRecommendsARefresh()
    {
        VectorSidecarFacts facts = Inspect(queue: null);

        Assert.Equal("unavailable", facts.State);
        Assert.Contains("miller workspace refresh", facts.Reason);
        Assert.DoesNotContain(StoreCoordinatorQueueReader.BlockedQueueMarker, facts.Reason);
    }

    [Fact]
    public void AnUnconvergedViewBehindAWedgedQueueNamesTheQueueInsteadOfARefresh()
    {
        VectorSidecarFacts facts = Inspect(WedgedQueue());

        Assert.Equal("unavailable", facts.State);
        Assert.Contains(StoreCoordinatorQueueReader.BlockedQueueMarker, facts.Reason);
        Assert.Contains("cannot converge it", facts.Reason);
        Assert.DoesNotContain("Run `miller workspace refresh`", facts.Reason);
    }

    [Fact]
    public void APendingButHealthyQueueIsNotAReasonToChangeTheRemedy()
    {
        VectorSidecarFacts facts = Inspect(new StoreCoordinatorQueueFacts(1, 0, 5, null, [], 300));

        Assert.Contains("miller workspace refresh", facts.Reason);
    }

    private VectorSidecarFacts Inspect(StoreCoordinatorQueueFacts? queue) =>
        new VectorSidecar(
            SemanticMode.On,
            SystemVectorFileProbe.Instance,
            queueReader: _ => queue)
            .InspectStore(_storeRoot, Snapshot());

    private static StoreCoordinatorQueueFacts WedgedQueue() =>
        new(
            QueuedCount: 3,
            ClaimedCount: 1,
            OldestQueuedAgeSeconds: 4200,
            DeadClaimOwner: "cli-4242",
            Groups: [new StoreCoordinatorQueueGroup("update", "queued", 3)],
            WedgedAfterSeconds: 300);

    private WorkspaceReadSnapshot Snapshot() =>
        new(
            _storeRoot,
            "workspace-a",
            "family-a",
            "view-a",
            new WorkspaceFreshnessToken(
                "family-a",
                3,
                "blake3:manifest",
                91,
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
}
