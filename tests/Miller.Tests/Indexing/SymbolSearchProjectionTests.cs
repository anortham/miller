using Miller.Core.Search;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class SymbolSearchProjectionTests
{
    private static SymbolSearchProjection BuildFromFixture(JulieDbFixture fx) =>
        SymbolSearchProjection.Build(SqliteSymbolReader.Read(fx.DbPath));

    [Fact]
    public void Build_IndexesSymbolsWithoutGraphData()
    {
        using var fx = JulieDbFixture.CreateDefault();
        var projection = BuildFromFixture(fx);

        Assert.Equal(JulieDbFixture.DefaultRows.Count, projection.DocumentCount);

        var hits = projection.Search("vector512", limit: 10);
        var names = hits.Select(h => projection.Resolve(h.Document.DocId).Name).ToList();

        Assert.Contains("Vector512", names);
        Assert.Contains("dot", names);
    }

    [Fact]
    public void Build_DuplicateAliasIds_UsesLowestOrdinalPathAndOneLogicalDocument()
    {
        var rows = new[]
        {
            new IndexedSymbol(42, "alias", "Alias", "class Alias", "class", "csharp", "z/Alias.cs",
                StartLine: 1, EndLine: 2, ParentId: null, IsTest: false),
            new IndexedSymbol(7, "alias", "Alias", "class Alias", "class", "csharp", "a/Alias.cs",
                StartLine: 1, EndLine: 2, ParentId: null, IsTest: false),
        };

        foreach (IndexedSymbol[] input in new[] { rows, rows.Reverse().ToArray() })
        {
            var projection = SymbolSearchProjection.Build(input);

            Assert.Equal(1, projection.DocumentCount);
            Assert.Equal("a/Alias.cs", projection.FindBySymbolId("alias")!.FilePath);
            Assert.Equal(0, Assert.Single(projection.Search("Alias", limit: 10)).Document.DocId);
        }
    }

    [Fact]
    public void Build_DivergentDuplicateId_ThrowsActionableInvalidDataException()
    {
        var rows = new[]
        {
            new IndexedSymbol(0, "conflict", "First", null, "class", "csharp", "src/First.cs",
                StartLine: 1, EndLine: 2, ParentId: null, IsTest: false),
            new IndexedSymbol(1, "conflict", "Second", null, "class", "csharp", "src/Second.cs",
                StartLine: 1, EndLine: 2, ParentId: null, IsTest: false),
        };

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => SymbolSearchProjection.Build(rows));

        Assert.Contains("conflict", error.Message);
        Assert.Contains("src/First.cs", error.Message);
        Assert.Contains("src/Second.cs", error.Message);
    }

    [Fact]
    public void Resolve_OutOfRangeDocId_Throws()
    {
        using var fx = JulieDbFixture.CreateDefault();
        var projection = BuildFromFixture(fx);

        Assert.Throws<ArgumentOutOfRangeException>(() => projection.Resolve(projection.DocumentCount));
        Assert.Throws<ArgumentOutOfRangeException>(() => projection.Resolve(-1));
    }

    [Fact]
    public void Search_AndMode_RequiresAllTerms()
    {
        using var fx = JulieDbFixture.CreateDefault();
        var projection = BuildFromFixture(fx);

        var hits = projection.Search("serve http", limit: 10, mode: SearchMode.And);

        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.Equal("ServeHTTP", projection.Resolve(h.Document.DocId).Name));
    }

    [Fact]
    public void Lookup_ResolvesNamesIdsAndIndexedFiles()
    {
        using var fx = JulieDbFixture.CreateDefault();
        var projection = BuildFromFixture(fx);

        Assert.Contains(projection.FindByName("GetUser"), s => s.SymbolId == JulieDbFixture.GetUserId);
        Assert.Equal("GetUser", projection.FindBySymbolId(JulieDbFixture.GetUserId)!.Name);
        Assert.Contains(projection.FindChildren(JulieDbFixture.UserServiceId), s => s.SymbolId == JulieDbFixture.GetUserId);
        Assert.NotEmpty(projection.FindByFilePath("auth/UserService.cs"));
        Assert.Equal("auth/UserService.cs", projection.ResolveIndexedFilePath("UserService.cs"));
        Assert.Contains(".cs", projection.KnownExtensions);
    }
}
