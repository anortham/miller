using Miller.Indexing.Resolution;
using Xunit;

namespace Miller.Tests.Indexing.Resolution;

public sealed class IdentifierSiteReaderTests
{
    [Fact]
    public void SitesNamed_StreamsVisibleStoreRowsWithoutCachingThem()
    {
        using ResolutionStoreFixture fixture = ResolutionStoreFixture.Create();
        fixture.AddFile(1, "a.cs");
        fixture.AddFile(2, "b.cs");
        fixture.AddSymbol(1, "from", "From", "method", "a.cs");
        fixture.AddIdentifier(
            1,
            "id1",
            "Foo",
            "a.cs",
            containingSymbolId: "from",
            startByte: 10,
            endByte: 13,
            metadataJson: """{"receiver":"this","receiver_qualifier":"Outer"}""");
        fixture.AddIdentifier(2, "id2", "Foo", "b.cs", startByte: 20, endByte: 23);
        fixture.ExecuteWrite("DELETE FROM manifest_entries WHERE path='b.cs'");

        using var connection = fixture.OpenRead();
        RevisionFactCache cache = RevisionFactCache.Load(connection, fixture.Visibility());
        IdentifierSite[] sites = [.. IdentifierSiteReader.SitesNamed(connection, fixture.Visibility(), "Foo")];

        Assert.Single(sites);
        Assert.Equal(1, sites[0].VersionId);
        Assert.Equal("id1", sites[0].IdentifierId);
        Assert.Equal("Foo", sites[0].Name);
        Assert.Equal("call", sites[0].Kind);
        Assert.Equal("this", sites[0].Receiver);
        Assert.Equal("Outer", sites[0].ReceiverQualifier);
        Assert.Equal("from", sites[0].ContainingSymbolId);
        Assert.Equal(10, sites[0].StartByte);
        Assert.DoesNotContain(
            typeof(RevisionFactCache).GetFields(
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Public),
            field => field.FieldType.Name.Contains("IdentifierSite", StringComparison.Ordinal));
        _ = cache;
    }

    [Fact]
    public void SitesWithinSymbols_FiltersByContainingSymbolId()
    {
        using ResolutionStoreFixture fixture = ResolutionStoreFixture.Create();
        fixture.AddFile(1, "a.cs");
        fixture.AddSymbol(1, "from", "From", "method", "a.cs");
        fixture.AddSymbol(1, "other", "Other", "method", "a.cs");
        fixture.AddIdentifier(1, "id1", "Foo", "a.cs", containingSymbolId: "from");
        fixture.AddIdentifier(1, "id2", "Bar", "a.cs", containingSymbolId: "other");

        using var connection = fixture.OpenRead();
        IdentifierSite[] sites = [.. IdentifierSiteReader.SitesWithinSymbols(connection, fixture.Visibility(), ["from"])];

        Assert.Equal(["id1"], sites.Select(s => s.IdentifierId));
        Assert.Empty(IdentifierSiteReader.SitesWithinSymbols(connection, fixture.Visibility(), []));
    }

    [Fact]
    public void ArtifactSitesNamed_JoinsCurrentFiles()
    {
        using ResolutionArtifactFixture fixture = ResolutionArtifactFixture.Create();
        fixture.AddFile("file-9e7a11", "a.cs");
        fixture.AddSymbol("file-9e7a11", "from", "From", "method", "a.cs");
        fixture.AddIdentifier("file-9e7a11", "id1", "Foo", "a.cs", containingSymbolId: "from", startByte: 4, endByte: 7);
        fixture.AddIdentifier("file-gone99", "ghost", "Foo", "gone.cs");

        using var connection = fixture.OpenRead();
        IdentifierSite[] named = [.. IdentifierSiteReader.SitesNamed(connection, "Foo")];
        IdentifierSite[] within = [.. IdentifierSiteReader.SitesWithinSymbols(connection, ["from"])];

        Assert.Equal(["id1"], named.Select(s => s.IdentifierId));
        Assert.Equal(1, named[0].VersionId);
        Assert.Equal(["id1"], within.Select(s => s.IdentifierId));
    }
}
