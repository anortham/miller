using Miller.Server.Hosting;
using Miller.Tests.Indexing;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins <see cref="IndexRebuilder"/> (the production rebuild factory <see cref="FreshnessService"/> hands to the
/// poller): a fresh <c>SqliteSymbolReader.Read</c> + <c>MillerRepositoryIndex.Build</c> off the extract DB,
/// producing an index whose symbols reflect the DB's current contents. Reads a synthesized fixture (no live
/// julie binary) so it stays in the fast suite; the live read-after-write convergence is the Scale suite.
/// </summary>
public sealed class IndexRebuilderTests
{
    [Fact]
    public void Rebuild_ReadsTheDb_AndBuildsAnIndexOverItsSymbols()
    {
        using var fx = JulieDbFixture.CreateDefault();
        var rebuilder = new IndexRebuilder(fx.DbPath);

        var index = rebuilder.Rebuild();

        Assert.Equal(fx.Rows.Count, index.DocumentCount);
        // A known default-fixture symbol is searchable in the rebuilt index.
        var hits = index.Search("parseToken", limit: 10);
        Assert.Contains(hits, h => index.Resolve(h.Document.DocId).Name == "parseToken");
    }

    [Fact]
    public void Rebuild_IsRepeatable_ProducingAnEquivalentIndexEachTime()
    {
        using var fx = JulieDbFixture.CreateDefault();
        var rebuilder = new IndexRebuilder(fx.DbPath);

        var first = rebuilder.Rebuild();
        var second = rebuilder.Rebuild();

        Assert.NotSame(first, second); // a genuinely new immutable index per rebuild (for the atomic swap)
        Assert.Equal(first.DocumentCount, second.DocumentCount);
    }

    [Fact]
    public void Ctor_NullOrEmptyDbPath_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new IndexRebuilder(null!));
        Assert.Throws<ArgumentException>(() => new IndexRebuilder("  "));
    }
}
