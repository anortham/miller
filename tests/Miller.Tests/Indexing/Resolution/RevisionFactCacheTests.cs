using System.Reflection;
using Microsoft.Data.Sqlite;
using Miller.Core.Resolution;
using Miller.Indexing.Resolution;
using Xunit;

namespace Miller.Tests.Indexing.Resolution;

public sealed class RevisionFactCacheTests
{
    [Fact]
    public void SymbolsNamed_MatchesVisibleStoreRowsAndDropsUnknownKinds()
    {
        using ResolutionStoreFixture fixture = ResolutionStoreFixture.Create();
        fixture.AddFile(1, "a.cs");
        fixture.AddFile(2, "hidden.cs");
        fixture.AddSymbol(1, "visible", "Widget", "class", "a.cs");
        fixture.AddSymbol(1, "dup", "Widget", "function", "a.cs");
        fixture.AddSymbol(1, "mystery", "Widget", "not-a-kind", "a.cs");
        fixture.AddSymbol(2, "old", "Widget", "class", "hidden.cs");
        fixture.ExecuteWrite("DELETE FROM manifest_entries WHERE path='hidden.cs'");

        using var connection = fixture.OpenRead();
        IResolutionFacts facts = RevisionFactCache.Load(connection, fixture.Visibility());

        FactSymbol[] named = [.. facts.SymbolsNamed("Widget")];
        Assert.Equal(2, named.Length);
        Assert.Contains(named, s => s.Key == new FactSymbolKey(1, "visible") && s.Kind == FactSymbolKind.Class);
        Assert.Contains(named, s => s.Key == new FactSymbolKey(1, "dup") && s.Kind == FactSymbolKind.Function);
        Assert.DoesNotContain(named, s => s.Key.SymbolId == "mystery");
        Assert.DoesNotContain(named, s => s.Key.VersionId == 2);
        Assert.Equal(ReadVisibleSymbolIds(connection, fixture, "Widget"), named.Select(s => s.Key.SymbolId).OrderBy(s => s));
    }

    [Fact]
    public void SymbolChildrenAndTopLevel_MatchVisibleHierarchy()
    {
        using ResolutionStoreFixture fixture = ResolutionStoreFixture.Create();
        fixture.AddFile(1, "a.cs");
        fixture.AddSymbol(1, "outer", "Outer", "class", "a.cs");
        fixture.AddSymbol(1, "inner", "Inner", "method", "a.cs", parentId: "outer");
        fixture.AddSymbol(1, "topFn", "Run", "function", "a.cs");

        using var connection = fixture.OpenRead();
        IResolutionFacts facts = RevisionFactCache.Load(connection, fixture.Visibility());

        FactSymbol? outer = facts.Symbol(new FactSymbolKey(1, "outer"));
        Assert.NotNull(outer);
        Assert.Equal("Outer", outer.Name);
        Assert.Null(facts.Symbol(new FactSymbolKey(1, "missing")));

        IReadOnlyList<FactSymbol> children = facts.ChildrenOf(new FactSymbolKey(1, "outer"));
        Assert.Equal(["inner"], children.Select(s => s.Key.SymbolId));
        Assert.Empty(facts.ChildrenOf(new FactSymbolKey(1, "inner")));

        IReadOnlyList<FactSymbol> top = facts.TopLevelOf(1);
        Assert.Equal(["outer", "topFn"], top.Select(s => s.Key.SymbolId).OrderBy(s => s));
        Assert.Empty(facts.TopLevelOf(99));
    }

    [Fact]
    public void TypeFactsOf_MatchesVisibleRows()
    {
        using ResolutionStoreFixture fixture = ResolutionStoreFixture.Create();
        fixture.AddFile(1, "a.cs");
        fixture.AddSymbol(1, "field", "Count", "field", "a.cs");
        fixture.AddTypeFact(1, "t1", "field", "int");
        fixture.AddTypeFact(1, "t2", "field", "nint", inferred: true);

        using var connection = fixture.OpenRead();
        IResolutionFacts facts = RevisionFactCache.Load(connection, fixture.Visibility());

        IReadOnlyList<FactTypeFact> factsOf = facts.TypeFactsOf(new FactSymbolKey(1, "field"));
        Assert.Equal(2, factsOf.Count);
        Assert.Equal("int", factsOf[0].ResolvedType);
        Assert.False(factsOf[0].IsInferred);
        Assert.Equal("nint", factsOf[1].ResolvedType);
        Assert.True(factsOf[1].IsInferred);
        Assert.Empty(facts.TypeFactsOf(new FactSymbolKey(1, "missing")));
    }

    [Fact]
    public void ImportsOf_ResolvesModuleVersionByCandidatePathAndLanguage()
    {
        using ResolutionStoreFixture fixture = ResolutionStoreFixture.Create();
        fixture.AddFile(1, "src/app.ts", "typescript");
        fixture.AddFile(2, "src/mod.ts", "typescript");
        fixture.AddFile(3, "src/mod.js", "javascript");
        fixture.AddSymbol(
            1,
            "imp1",
            "Widget",
            "import",
            "src/app.ts",
            language: "typescript",
            metadataJson: """{"source":"./mod","imported_name":"Widget"}""");
        fixture.AddSymbol(2, "exp", "Widget", "class", "src/mod.ts", language: "typescript");

        using var connection = fixture.OpenRead();
        IResolutionFacts facts = RevisionFactCache.Load(connection, fixture.Visibility());

        IReadOnlyList<ImportBinding> imports = facts.ImportsOf(1);
        Assert.Single(imports);
        Assert.Equal("Widget", imports[0].LocalName);
        Assert.Equal("Widget", imports[0].ImportedName);
        Assert.Equal("./mod", imports[0].Source);
        Assert.Equal(2, imports[0].ModuleVersionId);
    }

    [Fact]
    public void ImportsOf_UsesExtensionPrecedenceAndSkipsNonRelativeSources()
    {
        using ResolutionStoreFixture fixture = ResolutionStoreFixture.Create();
        fixture.AddFile(1, "src/app.ts", "typescript");
        fixture.AddFile(2, "src/lib.js", "typescript");
        fixture.AddFile(3, "src/lib.ts", "typescript");
        fixture.AddSymbol(
            1,
            "rel",
            "Lib",
            "import",
            "src/app.ts",
            language: "typescript",
            metadataJson: """{"source":"./lib"}""");
        fixture.AddSymbol(
            1,
            "pkg",
            "Fs",
            "import",
            "src/app.ts",
            language: "typescript",
            metadataJson: """{"source":"fs"}""");

        using var connection = fixture.OpenRead();
        IResolutionFacts facts = RevisionFactCache.Load(connection, fixture.Visibility());

        IReadOnlyList<ImportBinding> imports = facts.ImportsOf(1);
        ImportBinding relative = Assert.Single(imports, i => i.LocalName == "Lib");
        Assert.Equal(3, relative.ModuleVersionId);
        ImportBinding bare = Assert.Single(imports, i => i.LocalName == "Fs");
        Assert.Null(bare.ModuleVersionId);
    }

    [Fact]
    public void FailedPreservedFile_IsVisible_FailedWithoutVersion_IsNot()
    {
        using ResolutionStoreFixture fixture = ResolutionStoreFixture.Create();
        fixture.AddFile(1, "ok.cs");
        fixture.AddFile(2, "kept.cs", status: "failed_preserved");
        fixture.AddFailedPath("dead.cs");
        fixture.AddSymbol(1, "ok", "Ok", "class", "ok.cs");
        fixture.AddSymbol(2, "kept", "Kept", "class", "kept.cs");

        using var connection = fixture.OpenRead();
        IResolutionFacts facts = RevisionFactCache.Load(connection, fixture.Visibility());

        Assert.NotNull(facts.Symbol(new FactSymbolKey(1, "ok")));
        Assert.NotNull(facts.Symbol(new FactSymbolKey(2, "kept")));
        Assert.Equal("Kept", Assert.Single(facts.SymbolsNamed("Kept")).Name);
    }

    [Fact]
    public void ParseIsStatic_ConvertsJsonBoolToPolicyStrings()
    {
        using ResolutionStoreFixture fixture = ResolutionStoreFixture.Create();
        fixture.AddFile(1, "a.cs");
        fixture.AddSymbol(1, "yes", "A", "method", "a.cs", metadataJson: """{"isStatic":true}""");
        fixture.AddSymbol(1, "no", "B", "method", "a.cs", metadataJson: """{"isStatic":false}""");
        fixture.AddSymbol(1, "text", "C", "method", "a.cs", metadataJson: """{"isStatic":"true"}""");
        fixture.AddSymbol(1, "unk", "D", "method", "a.cs", metadataJson: """{"isStatic":1}""");

        using var connection = fixture.OpenRead();
        IResolutionFacts facts = RevisionFactCache.Load(connection, fixture.Visibility());

        Assert.True(facts.Symbol(new FactSymbolKey(1, "yes"))!.IsStatic);
        Assert.False(facts.Symbol(new FactSymbolKey(1, "no"))!.IsStatic);
        Assert.True(facts.Symbol(new FactSymbolKey(1, "text"))!.IsStatic);
        Assert.Null(facts.Symbol(new FactSymbolKey(1, "unk"))!.IsStatic);
    }

    [Fact]
    public void InternsRepeatedNames_AndExposesReusableVersionSegments()
    {
        using ResolutionStoreFixture fixture = ResolutionStoreFixture.Create();
        fixture.AddFile(1, "a.cs");
        fixture.AddSymbol(1, "one", "Shared", "class", "a.cs");
        fixture.AddSymbol(1, "two", "Shared", "function", "a.cs");

        using var connection = fixture.OpenRead();
        RevisionFactCache cache = RevisionFactCache.Load(connection, fixture.Visibility());
        FactSymbol[] named = [.. cache.SymbolsNamed("Shared")];

        Assert.Equal(2, named.Length);
        Assert.True(ReferenceEquals(named[0].Name, named[1].Name));
        Assert.Same(cache.SymbolsOfVersion(1), cache.SymbolsOfVersion(1));
    }

    [Fact]
    public void CacheType_HasNoIdentifierRowCollection()
    {
        AssertNoIdentifierCollection(typeof(RevisionFactCache));
        AssertNoIdentifierCollection(typeof(VersionSlice));
        AssertNoIdentifierCollection(typeof(PropagationIndex));
    }

    [Fact]
    public void Propagation_LocatesPendingThenRelationshipLastWriteWins()
    {
        using ResolutionStoreFixture fixture = ResolutionStoreFixture.Create();
        fixture.AddFile(1, "a.cs");
        fixture.AddSymbol(1, "from", "From", "method", "a.cs");
        fixture.AddSymbol(1, "to", "Target", "function", "a.cs");
        fixture.AddIdentifier(1, "id1", "Target", "a.cs", startByte: 10, endByte: 16, startLine: 2);
        fixture.AddPending(1, "pend1", "from", "Target", "a.cs", startByte: 10, endByte: 20, startLine: 2);
        fixture.AddRelationship(1, "rel1", "from", "to", "a.cs", startByte: 10, endByte: 20, startLine: 2);

        using var connection = fixture.OpenRead();
        RevisionFactCache cache = RevisionFactCache.Load(connection, fixture.Visibility());
        long rowId = ReadIdentifierRowId(connection, 1, "id1");

        Assert.True(cache.Propagation.TryGetOverride(1, rowId, out PropagationSource source));
        Assert.Equal(PropagationOrigin.Relationship, source.Origin);
        Assert.Equal("rel1", source.RowId);
    }

    [Fact]
    public void Propagation_RequiresExactlyOneMatchingIdentifier()
    {
        using ResolutionStoreFixture fixture = ResolutionStoreFixture.Create();
        fixture.AddFile(1, "a.cs");
        fixture.AddSymbol(1, "from", "From", "method", "a.cs");
        fixture.AddIdentifier(1, "id1", "Foo", "a.cs", startByte: 10, endByte: 13, startLine: 1);
        fixture.AddIdentifier(1, "id2", "Foo", "a.cs", startByte: 12, endByte: 15, startLine: 1);
        fixture.AddPending(1, "pend1", "from", "Foo", "a.cs", startByte: 10, endByte: 20, startLine: 1);

        using var connection = fixture.OpenRead();
        RevisionFactCache cache = RevisionFactCache.Load(connection, fixture.Visibility());
        long first = ReadIdentifierRowId(connection, 1, "id1");
        long second = ReadIdentifierRowId(connection, 1, "id2");

        Assert.False(cache.Propagation.TryGetOverride(1, first, out _));
        Assert.False(cache.Propagation.TryGetOverride(1, second, out _));
    }

    private static IEnumerable<string> ReadVisibleSymbolIds(SqliteConnection connection, ResolutionStoreFixture fixture, string name)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT s.symbol_id
            FROM main.symbols AS s
            JOIN main.manifest_entries AS e ON e.version_id=s.version_id
            WHERE e.view_id=$view_id AND e.generation=$generation AND s.name=$name
              AND s.kind IN ('class','interface','function','method','variable','constant','property','enum',
                             'enum_member','module','namespace','type','trait','struct','union','field',
                             'constructor','destructor','operator','import','export','event','delegate')
            ORDER BY s.symbol_id
            """;
        command.Parameters.AddWithValue("$view_id", fixture.ViewId);
        command.Parameters.AddWithValue("$generation", fixture.Generation);
        command.Parameters.AddWithValue("$name", name);
        using SqliteDataReader reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read())
            ids.Add(reader.GetString(0));
        return ids;
    }

    private static long ReadIdentifierRowId(SqliteConnection connection, long versionId, string identifierId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT rowid FROM identifiers WHERE version_id=$v AND identifier_id=$id";
        command.Parameters.AddWithValue("$v", versionId);
        command.Parameters.AddWithValue("$id", identifierId);
        return (long)command.ExecuteScalar()!;
    }

    private static void AssertNoIdentifierCollection(Type type)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (FieldInfo field in type.GetFields(flags))
        {
            Assert.DoesNotContain("identifier", field.Name, StringComparison.OrdinalIgnoreCase);
            Assert.False(IsIdentifierCollection(field.FieldType), field.Name);
        }

        foreach (PropertyInfo property in type.GetProperties(flags))
        {
            if (property.Name is "Propagation" or "CanAdvance" or "ResidentBytes")
                continue;
            Assert.DoesNotContain("identifier", property.Name, StringComparison.OrdinalIgnoreCase);
            Assert.False(IsIdentifierCollection(property.PropertyType), property.Name);
        }
    }

    private static bool IsIdentifierCollection(Type type)
    {
        if (type.IsArray && type.GetElementType() is { } element
            && element.Name.Contains("Identifier", StringComparison.Ordinal))
        {
            return true;
        }

        if (type.IsGenericType)
        {
            foreach (Type argument in type.GetGenericArguments())
            {
                if (argument.Name.Contains("Identifier", StringComparison.Ordinal)
                    && (type.Name.Contains("List", StringComparison.Ordinal)
                        || type.Name.Contains("Array", StringComparison.Ordinal)
                        || type.Name.Contains("Collection", StringComparison.Ordinal)))
                {
                    return true;
                }
            }
        }

        return type.Name.Contains("IdentifierSite", StringComparison.Ordinal)
            || type.Name.Contains("IdentifierRow", StringComparison.Ordinal);
    }
}
