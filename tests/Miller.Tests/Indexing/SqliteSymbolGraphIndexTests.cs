using Miller.Core.Graph;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class SqliteSymbolGraphIndexTests
{
    [Fact]
    public void Reach_MatchesRepositoryGraphForInspectFixture()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        using var sqliteGraph = new SqliteSymbolGraphIndex(fx.DbPath);
        var full = RepositoryIndexLoader.Load(fx.DbPath);

        foreach (Direction direction in new[] { Direction.Forward, Direction.Reverse, Direction.Both })
        {
            var expected = full.Graph.Reach([JulieDbFixture.GetUserId], maxDepth: 2, limit: 50, direction);
            var actual = sqliteGraph.Reach([JulieDbFixture.GetUserId], maxDepth: 2, limit: 50, direction);

            Assert.Equal(
                expected.Select(static n => (n.Id, n.Hop)).ToArray(),
                actual.Select(static n => (n.Id, n.Hop)).ToArray());
        }
    }

    [Fact]
    public void ShortestPath_MatchesRepositoryGraphForInspectFixture()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        using var sqliteGraph = new SqliteSymbolGraphIndex(fx.DbPath);
        var full = RepositoryIndexLoader.Load(fx.DbPath);
        string findId = SqliteSymbolReader.Read(fx.DbPath).Single(static s => s.Name == "Find").SymbolId;

        Assert.Equal(
            full.Graph.ShortestPath(JulieDbFixture.GetUserId, findId, maxDepth: 2),
            sqliteGraph.ShortestPath(JulieDbFixture.GetUserId, findId, maxDepth: 2));
    }
}
