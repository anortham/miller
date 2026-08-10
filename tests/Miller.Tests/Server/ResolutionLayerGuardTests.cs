using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Server.Resolution;
using Miller.Server.Tools;
using Miller.Server.Workspaces;
using Miller.Tests.Support;
using Xunit;

namespace Miller.Tests.Server;

public sealed class ResolutionLayerGuardTests : IDisposable
{
    private readonly string _directory;
    private readonly string _databasePath;
    private readonly MillerRepositoryIndex _index;
    private readonly ISymbolLookupIndex _lookupIndex;
    private readonly WorkspaceReadSnapshot _snapshot;

    public ResolutionLayerGuardTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "miller-resolution-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _databasePath = SymbolsLevelArtifact.CreateFull(Path.Combine(_directory, "full-level"));
        _index = RepositoryIndexLoader.Load(_databasePath);
        _lookupIndex = SymbolSearchProjectionLoader.Load(_databasePath);
        _snapshot = new WorkspaceReadSnapshot(
            Path.GetDirectoryName(_databasePath)!,
            "workspace-1",
            "family-1",
            "view-1",
            new WorkspaceFreshnessToken(
                "family-1",
                1,
                ManifestHash: "manifest-1",
                StoreLogSequence: 1,
                ResolutionStamp: "base-1:delta-1",
                StoreInstanceId: "instance-1",
                ViewId: "view-1",
                GenerationName: "generation-1",
                ManifestGeneration: 1,
                IndexLevel: IndexLevels.FullMetadataValue,
                LevelStampL1: "l1",
                LevelStampL2: "l2",
                LevelStampL3: "l3"),
            IndexLevels.FullMetadataValue,
            WorkspaceReadMode.FamilyStore,
            GenerationName: "generation-1",
            ManifestGeneration: 1,
            ResolutionState: "converging",
            ResolutionBaseId: "base-1",
            ResolutionDeltaGeneration: 1);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    [Fact]
    public void TraceRefusesAConvergingStoreResolutionLayer()
    {
        string output = new TraceTool(new StoreReadProvider(_index, _lookupIndex, _snapshot)).Trace("Alpha");

        Assert.Contains("diagnostic_code=resolution_converging", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ContextUsageRefusesAConvergingStoreResolutionLayer()
    {
        string output = new ContextTool(new StoreReadProvider(_index, _lookupIndex, _snapshot))
            .Context("Alpha", reference_mode: "usage");

        Assert.Contains("diagnostic_code=resolution_converging", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("overview")]
    [InlineData("full")]
    public void InspectReferenceDepthRefusesAConvergingStoreResolutionLayer(string depth)
    {
        string output = new InspectTool(new StoreReadProvider(_index, _lookupIndex, _snapshot))
            .Inspect("Alpha", depth: depth);

        Assert.Contains("diagnostic_code=resolution_converging", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ImpactRefusesAConvergingStoreResolutionLayer()
    {
        string output = new ImpactTool(new StoreReadProvider(_index, _lookupIndex, _snapshot))
            .Impact(target: "Alpha");

        Assert.Contains("diagnostic_code=resolution_converging", output, StringComparison.Ordinal);
    }

    private sealed class StoreReadProvider : IWorkspaceIndexProvider, IWorkspaceSymbolReadProvider
    {
        private readonly MillerRepositoryIndex _index;
        private readonly ISymbolLookupIndex _lookupIndex;
        private readonly WorkspaceReadSnapshot _snapshot;

        public StoreReadProvider(
            MillerRepositoryIndex index,
            ISymbolLookupIndex lookupIndex,
            WorkspaceReadSnapshot snapshot)
        {
            _index = index;
            _lookupIndex = lookupIndex;
            _snapshot = snapshot;
        }

        public WorkspaceReadContext Resolve(string? workspaceId, bool ensureFresh) =>
            new(
                _index,
                new SmartTargetResolver(_index),
                new WorkspaceReadHandle(new ThrowingReadSession(_snapshot)),
                "workspace-1",
                _snapshot.WorkspaceRoot,
                _snapshot.Freshness.Revision,
                true,
                "current",
                null,
                "workspace-1",
                IndexLevels.FullMetadataValue);

        public WorkspaceSymbolReadContext ResolveSymbolRead(string? workspaceId, bool ensureFresh) =>
            new(
                _lookupIndex,
                new WorkspaceReadHandle(new ThrowingReadSession(_snapshot)),
                "workspace-1",
                _snapshot.WorkspaceRoot,
                _snapshot.Freshness.Revision,
                true,
                "current",
                null,
                "workspace-1",
                true,
                IndexLevels.FullMetadataValue);
    }

    private sealed class ThrowingReadSession : IWorkspaceReadSession
    {
        public ThrowingReadSession(WorkspaceReadSnapshot snapshot) => Snapshot = snapshot;

        public WorkspaceReadSnapshot Snapshot { get; }

        public TResult Read<TResult>(Func<SqliteConnection, TResult> query) =>
            throw new InvalidOperationException("The resolution guard must run before any store read.");

        public void Dispose()
        {
        }
    }
}
