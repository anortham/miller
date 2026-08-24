using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Core.Resolution;
using Miller.Indexing.Resolution;
using Miller.Tests.Support;
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

    [Fact]
    public void ArtifactCatalog_ProducesTheSameOrderedQmlCandidatesAsStore()
    {
        using ResolutionArtifactFixture fixture = ResolutionArtifactFixture.Create();
        QmlVisibilityFixtureSupport.Populate(fixture);

        using SqliteConnection connection = fixture.OpenRead();
        IResolutionFacts facts = RevisionFactCache.LoadFromArtifact(connection);
        long sourceVersion = Assert.Single(facts.SymbolsNamed("source")).Key.VersionId;
        QmlVisibleType[] candidates = facts.QmlTypesVisibleTo(sourceVersion).ToArray();

        Assert.Equal(QmlVisibilityFixtureSupport.ExpectedExportedNames, candidates.Select(candidate => candidate.ExportedName));
        Assert.Equal(
            ["local", "remote", "remote", "theme", "theme"],
            candidates.Select(candidate => candidate.Target.SymbolId));
        Assert.Equal(
            ["", "Components", "EC", "Components", "EC"],
            candidates.Select(candidate => candidate.ImportAlias ?? string.Empty));
        Assert.Equal(
            ["qml.directory", "qmldir", "qmldir", "qmldir", "qmldir"],
            candidates.Select(candidate => candidate.Evidence.Provenance));

        using ResolutionStoreFixture storeFixture = ResolutionStoreFixture.Create();
        QmlVisibilityFixtureSupport.Populate(storeFixture);
        using SqliteConnection storeConnection = storeFixture.OpenRead();
        IResolutionFacts storeFacts = RevisionFactCache.Load(storeConnection, storeFixture.Visibility());
        Assert.Equal(
            JsonSerializer.Serialize(storeFacts.QmlTypesVisibleTo(1)),
            JsonSerializer.Serialize(candidates));
    }

    [Fact]
    public void ArtifactCatalog_KeepsInternalTypesInsideTheirDirectory()
    {
        using ResolutionArtifactFixture fixture = ResolutionArtifactFixture.Create();
        QmlVisibilityFixtureSupport.Populate(fixture);

        using SqliteConnection connection = fixture.OpenRead();
        IResolutionFacts facts = RevisionFactCache.LoadFromArtifact(connection);
        long sourceVersion = Assert.Single(facts.SymbolsNamed("source")).Key.VersionId;
        long remoteVersion = Assert.Single(facts.SymbolsNamed("RemoteCard"), symbol => symbol.Key.SymbolId == "remote").Key.VersionId;

        Assert.DoesNotContain(facts.QmlTypesVisibleTo(sourceVersion), candidate => candidate.ExportedName == "InternalCard");
        QmlVisibleType internalType = Assert.Single(
            facts.QmlTypesVisibleTo(remoteVersion),
            candidate => candidate.ExportedName == "InternalCard");
        Assert.True(internalType.IsInternal);
        Assert.Equal(QmlVisibilityScope.ForDirectory("components"), internalType.Scope);
    }

    [Fact]
    public void ArtifactCatalog_DropsMalformedAndFutureManifestFacts()
    {
        using ResolutionArtifactFixture malformedFixture = ResolutionArtifactFixture.Create();
        QmlVisibilityFixtureSupport.Populate(malformedFixture);
        malformedFixture.ExecuteWrite("UPDATE structural_facts SET metadata_json='not-json' WHERE structural_fact_id='fact-remote';");
        using SqliteConnection malformedConnection = malformedFixture.OpenRead();
        IResolutionFacts malformed = RevisionFactCache.LoadFromArtifact(malformedConnection);
        long malformedSource = Assert.Single(malformed.SymbolsNamed("source")).Key.VersionId;

        Assert.Equal(
            ["LocalCard", "Theme", "Theme"],
            malformed.QmlTypesVisibleTo(malformedSource).Select(candidate => candidate.ExportedName));

        using ResolutionArtifactFixture futureFixture = ResolutionArtifactFixture.Create();
        QmlVisibilityFixtureSupport.Populate(futureFixture);
        futureFixture.ExecuteWrite("UPDATE structural_facts SET pattern_id='qmldir.object_type.v2' WHERE structural_fact_id='fact-remote';");
        using SqliteConnection futureConnection = futureFixture.OpenRead();
        IResolutionFacts future = RevisionFactCache.LoadFromArtifact(futureConnection);
        long futureSource = Assert.Single(future.SymbolsNamed("source")).Key.VersionId;

        Assert.Equal(
            ["LocalCard", "Theme", "Theme"],
            future.QmlTypesVisibleTo(futureSource).Select(candidate => candidate.ExportedName));
    }

    [Fact]
    public void ReleasedQmlArtifact_ProducesDirectoryVisibilityCandidates()
    {
        string path = Path.Combine(
            ScaleTestSupport.RepoRoot(),
            "tests",
            "Miller.Tests",
            "Fixtures",
            "QmlFirstClass",
            "symbols.db");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();

        IResolutionFacts facts = RevisionFactCache.LoadFromArtifact(connection);
        long sourceVersion = Assert.Single(facts.SymbolsNamed("source")).Key.VersionId;
        QmlVisibleType[] candidates = facts.QmlTypesVisibleTo(sourceVersion).ToArray();

        Assert.Equal(["LocalCard", "RemoteCard", "Theme"], candidates.Select(candidate => candidate.ExportedName));
        Assert.All(candidates.Skip(1), candidate => Assert.Equal("Components", candidate.ImportAlias));
        Assert.DoesNotContain(candidates, candidate => candidate.ExportedName == "InternalCard");
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
