using Miller.Indexing;
using Miller.Indexing.Reads;
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
}
