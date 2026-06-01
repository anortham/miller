using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class WorkspaceIndexFactsReaderTests
{
    [Fact]
    public void Read_ReturnsCountsWithoutReadingGraphTables()
    {
        using var fx = JulieDbFixture.CreateDefault();
        SqliteFixtureMutator.DropRelationshipsTable(fx.DbPath);

        WorkspaceIndexFacts facts = WorkspaceIndexFactsReader.Read(fx.DbPath);

        Assert.Equal(JulieDbFixture.DefaultRows.Count, facts.DocumentCount);
        Assert.True(facts.KnownExtensionsCount > 0);
    }

    [Fact]
    public void Read_MissingDbThrowsFileNotFound()
    {
        string missing = Path.Combine(
            Path.GetTempPath(), "miller-facts-missing-" + Guid.NewGuid().ToString("N"), "symbols.db");

        Assert.Throws<FileNotFoundException>(() => WorkspaceIndexFactsReader.Read(missing));
    }

}
