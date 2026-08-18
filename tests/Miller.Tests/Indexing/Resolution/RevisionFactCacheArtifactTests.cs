using Microsoft.Data.Sqlite;
using Miller.Core.Resolution;
using Miller.Indexing.Resolution;
using Xunit;

namespace Miller.Tests.Indexing.Resolution;

public sealed class RevisionFactCacheArtifactTests
{
    [Fact]
    public void LoadFromArtifact_MatchesDirectSqlOnCurrentFiles()
    {
        using ResolutionArtifactFixture fixture = ResolutionArtifactFixture.Create();
        fixture.AddFile("1", "a.cs");
        fixture.AddFile("2", "b.cs");
        fixture.AddSymbol("1", "widget", "Widget", "class", "a.cs");
        fixture.AddSymbol("1", "run", "Run", "method", "a.cs", parentId: "widget");
        fixture.AddSymbol("2", "other", "Other", "function", "b.cs");
        fixture.AddTypeFact("tf1", "run", "int");
        fixture.AddSymbol(
            "1",
            "imp",
            "Lib",
            "import",
            "a.cs",
            language: "typescript",
            metadataJson: """{"source":"./lib"}""");

        using var connection = fixture.OpenRead();
        IResolutionFacts facts = RevisionFactCache.LoadFromArtifact(connection);

        Assert.Equal("Widget", facts.Symbol(new FactSymbolKey(1, "widget"))!.Name);
        Assert.Equal(["run"], facts.ChildrenOf(new FactSymbolKey(1, "widget")).Select(s => s.Key.SymbolId));
        Assert.Equal(["imp", "widget"], facts.TopLevelOf(1).Select(s => s.Key.SymbolId).OrderBy(s => s));
        Assert.Equal("int", Assert.Single(facts.TypeFactsOf(new FactSymbolKey(1, "run"))).ResolvedType);
        Assert.Equal(ReadArtifactNames(connection, "Widget"), facts.SymbolsNamed("Widget").Select(s => s.Name).ToArray());
        Assert.Null(Assert.Single(facts.ImportsOf(1)).ModuleVersionId);
    }

    [Fact]
    public void LoadFromArtifact_IgnoresSymbolsWithoutFilesRow()
    {
        using ResolutionArtifactFixture fixture = ResolutionArtifactFixture.Create();
        fixture.AddFile("1", "a.cs");
        fixture.AddSymbol("1", "keep", "Keep", "class", "a.cs");
        fixture.AddSymbol("9", "ghost", "Ghost", "class", "gone.cs");

        using var connection = fixture.OpenRead();
        IResolutionFacts facts = RevisionFactCache.LoadFromArtifact(connection);

        Assert.NotNull(facts.Symbol(new FactSymbolKey(1, "keep")));
        Assert.Empty(facts.SymbolsNamed("Ghost"));
    }

    [Fact]
    public void LoadFromArtifact_DoesNotAdvance()
    {
        using ResolutionArtifactFixture fixture = ResolutionArtifactFixture.Create();
        fixture.AddFile("1", "a.cs");
        fixture.AddSymbol("1", "keep", "Keep", "class", "a.cs");

        using var connection = fixture.OpenRead();
        RevisionFactCache cache = RevisionFactCache.LoadFromArtifact(connection);

        Assert.False(cache.CanAdvance);
        using ResolutionStoreFixture store = ResolutionStoreFixture.Create();
        using var storeRead = store.OpenRead();
        Assert.Throws<InvalidOperationException>(() => cache.Advance(storeRead, store.Visibility()));
    }

    private static string[] ReadArtifactNames(SqliteConnection connection, string name)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT s.name
            FROM symbols AS s
            JOIN files AS f ON f.file_id=s.file_id
            WHERE s.name=$name
            ORDER BY s.symbol_id
            """;
        command.Parameters.AddWithValue("$name", name);
        using SqliteDataReader reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names.ToArray();
    }
}
