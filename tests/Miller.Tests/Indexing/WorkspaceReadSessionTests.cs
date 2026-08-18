using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Core.Graph;
using Miller.Server.Workspaces;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class WorkspaceReadSessionTests
{
    [Fact]
    public void LegacySessionCarriesOneSnapshotAndLoadsTheSameRepositoryIndex()
    {
        using JulieDbFixture fixture = JulieDbFixture.CreateForInspect();
        using LegacyArtifactReadSession session = LegacyArtifactReadSession.Open(fixture.DbPath);

        MillerRepositoryIndex expected = RepositoryIndexLoader.Load(fixture.DbPath);
        MillerRepositoryIndex actual = RepositoryIndexLoader.LoadSession(session);

        Assert.Equal(WorkspaceReadMode.LegacyArtifact, session.Snapshot.Mode);
        Assert.Equal("legacy", session.Snapshot.ViewId);
        Assert.Equal(expected.FindByName("GetUser"), actual.FindByName("GetUser"));
        Assert.Equal(
            expected.Graph.Reach([JulieDbFixture.GetUserId], 2, 50, Miller.Core.Graph.Direction.Both),
            actual.Graph.Reach([JulieDbFixture.GetUserId], 2, 50, Miller.Core.Graph.Direction.Both));
    }

    [Fact]
    public void DisposedSessionRefusesFurtherReads()
    {
        using JulieDbFixture fixture = JulieDbFixture.CreateForInspect();
        LegacyArtifactReadSession session = LegacyArtifactReadSession.Open(fixture.DbPath);
        session.Dispose();

        Assert.Throws<ObjectDisposedException>(() => session.Read(connection => connection.DataSource));
    }

    [Fact]
    public void LegacyPathConversionReadsTheArtifactSnapshotInsteadOfFabricatingRevisionZero()
    {
        using JulieDbFixture fixture = JulieDbFixture.CreateForInspect();
        using LegacyArtifactReadSession expected = LegacyArtifactReadSession.Open(fixture.DbPath);
        using WorkspaceReadHandle actual = fixture.DbPath;

        Assert.Equal(expected.Snapshot, actual.Snapshot);
    }

    [Fact]
    public void LegacyHandleKeepsCompatibilityGraphReaders()
    {
        using JulieDbFixture fixture = JulieDbFixture.CreateForInspect();
        using var handle = new WorkspaceReadHandle(LegacyArtifactReadSession.Open(fixture.DbPath));
        using var graph = new SqliteSymbolGraphIndex(handle);

        _ = graph.Reach([JulieDbFixture.GetUserId], 1, 50, Direction.Both);

        Assert.Null(handle.FamilyGraphResolutionReader);
        Assert.Null(handle.FamilyGraphUnresolvedNameReader);
        Assert.Null(handle.FamilyGraphRelationshipReader);
        Assert.True(graph.QueryTelemetry.FrontierBatch.Executions > 0);
    }

    [Fact]
    public void FamilyStoreIndexIdentityChangesWhenTheServingManifestChanges()
    {
        var freshness = new WorkspaceFreshnessToken(
            "family-a",
            7,
            ManifestHash: "manifest-a",
            StoreLogSequence: 12);
        var first = new WorkspaceReadSnapshot(
            "/workspace",
            "workspace-a",
            "family-a",
            "view-a",
            freshness,
            IndexLevels.FullMetadataValue,
            WorkspaceReadMode.FamilyStore,
            GenerationName: "gen-001",
            ManifestGeneration: 1);
        WorkspaceReadSnapshot second = first with
        {
            Freshness = freshness with { ManifestHash = "manifest-b" },
            ManifestGeneration = 2,
        };

        Assert.NotEqual(first.IndexIdentity, second.IndexIdentity);
        Assert.Equal("family-a:view-a", first.VectorArtifactId);
        Assert.Equal(12, first.VectorRevision);
    }

    [Fact]
    public void PrimaryReadContextsExposeSessionsInsteadOfRawArtifactPaths()
    {
        Assert.Null(typeof(WorkspaceReadContext).GetProperty("IndexDbPath"));
        Assert.Null(typeof(WorkspaceSymbolReadContext).GetProperty("IndexDbPath"));
        Assert.Null(typeof(WorkspaceSymbolSearchContext).GetProperty("IndexDbPath"));
        Assert.Null(typeof(WorkspaceRegionSearchContext).GetProperty("IndexDbPath"));
        Assert.Null(typeof(WorkspaceArtifactContext).GetProperty("IndexDbPath"));
        Assert.Equal(
            typeof(WorkspaceReadHandle),
            typeof(WorkspaceReadContext).GetProperty("ReadSession")!.PropertyType);
        Assert.Equal(
            typeof(WorkspaceReadHandle),
            typeof(WorkspaceSymbolReadContext).GetProperty("ReadSession")!.PropertyType);
        Assert.Equal(
            typeof(WorkspaceReadHandle),
            typeof(WorkspaceSymbolSearchContext).GetProperty("ReadSession")!.PropertyType);
        Assert.Equal(
            typeof(WorkspaceReadHandle),
            typeof(WorkspaceRegionSearchContext).GetProperty("ReadSession")!.PropertyType);
        Assert.Equal(
            typeof(WorkspaceReadHandle),
            typeof(WorkspaceArtifactContext).GetProperty("ReadSession")!.PropertyType);
    }
}
