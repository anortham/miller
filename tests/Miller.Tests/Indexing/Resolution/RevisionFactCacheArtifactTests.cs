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
        fixture.AddFile("file-9e7a11", "a.cs");
        fixture.AddFile("file-c04d22", "b.cs");
        fixture.AddSymbol("file-9e7a11", "widget", "Widget", "class", "a.cs");
        fixture.AddSymbol("file-9e7a11", "run", "Run", "method", "a.cs", parentId: "widget");
        fixture.AddSymbol("file-c04d22", "other", "Other", "function", "b.cs");
        fixture.AddTypeFact("tf1", "run", "int");
        fixture.AddSymbol(
            "file-9e7a11",
            "imp",
            "Lib",
            "import",
            "a.cs",
            language: "typescript",
            metadataJson: """{"source":"./lib"}""");

        using var connection = fixture.OpenRead();
        IResolutionFacts facts = RevisionFactCache.LoadFromArtifact(connection);

        long widgetVersion = Assert.Single(facts.SymbolsNamed("Widget")).Key.VersionId;
        long otherVersion = Assert.Single(facts.SymbolsNamed("Other")).Key.VersionId;
        Assert.NotEqual(widgetVersion, otherVersion);
        Assert.Equal("Widget", facts.Symbol(new FactSymbolKey(widgetVersion, "widget"))!.Name);
        Assert.Equal(["run"], facts.ChildrenOf(new FactSymbolKey(widgetVersion, "widget")).Select(s => s.Key.SymbolId));
        Assert.Equal(["imp", "widget"], facts.TopLevelOf(widgetVersion).Select(s => s.Key.SymbolId).OrderBy(s => s));
        Assert.Equal(["other"], facts.TopLevelOf(otherVersion).Select(s => s.Key.SymbolId));
        Assert.Equal("int", Assert.Single(facts.TypeFactsOf(new FactSymbolKey(widgetVersion, "run"))).ResolvedType);
        Assert.Equal(ReadArtifactNames(connection, "Widget"), facts.SymbolsNamed("Widget").Select(s => s.Name).ToArray());
        Assert.Null(Assert.Single(facts.ImportsOf(widgetVersion)).ModuleVersionId);
    }

    [Fact]
    public void LoadFromArtifact_IgnoresSymbolsWithoutFilesRow()
    {
        using ResolutionArtifactFixture fixture = ResolutionArtifactFixture.Create();
        fixture.AddFile("file-9e7a11", "a.cs");
        fixture.AddSymbol("file-9e7a11", "keep", "Keep", "class", "a.cs");
        fixture.AddSymbol("file-gone99", "ghost", "Ghost", "class", "gone.cs");

        using var connection = fixture.OpenRead();
        IResolutionFacts facts = RevisionFactCache.LoadFromArtifact(connection);

        long keepVersion = Assert.Single(facts.SymbolsNamed("Keep")).Key.VersionId;
        Assert.NotNull(facts.Symbol(new FactSymbolKey(keepVersion, "keep")));
        Assert.Empty(facts.SymbolsNamed("Ghost"));
    }

    [Fact]
    public void LoadFromArtifact_DoesNotAdvance()
    {
        using ResolutionArtifactFixture fixture = ResolutionArtifactFixture.Create();
        fixture.AddFile("file-9e7a11", "a.cs");
        fixture.AddSymbol("file-9e7a11", "keep", "Keep", "class", "a.cs");

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
