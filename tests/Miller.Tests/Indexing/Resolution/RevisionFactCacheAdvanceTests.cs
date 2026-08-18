using Miller.Core.Resolution;
using Miller.Indexing.Resolution;
using Xunit;

namespace Miller.Tests.Indexing.Resolution;

public sealed class RevisionFactCacheAdvanceTests
{
    [Fact]
    public void Advance_OneFile_ReloadsOnlyThatVersion()
    {
        using ResolutionStoreFixture fixture = ResolutionStoreFixture.Create();
        fixture.AddFile(1, "keep.cs");
        fixture.AddFile(2, "change.cs");
        fixture.AddSymbol(1, "kept", "Kept", "class", "keep.cs");
        fixture.AddSymbol(2, "old", "Old", "class", "change.cs");

        RevisionFactCache first;
        FactSymbol[] keptBefore;
        using (var firstRead = fixture.OpenRead())
        {
            first = RevisionFactCache.Load(firstRead, fixture.Visibility());
            keptBefore = first.SymbolsOfVersion(1);
        }

        fixture.AddSymbol(3, "neu", "New", "class", "change.cs");
        fixture.FlipManifest(2, [("keep.cs", 1, "csharp", "indexed"), ("change.cs", 3, "csharp", "indexed")]);

        using var secondRead = fixture.OpenRead();
        RevisionFactCache second = first.Advance(secondRead, fixture.Visibility());

        Assert.Same(keptBefore, second.SymbolsOfVersion(1));
        Assert.Equal("Kept", Assert.Single(second.SymbolsNamed("Kept")).Name);
        Assert.Equal("New", Assert.Single(second.SymbolsNamed("New")).Name);
        Assert.Empty(second.SymbolsNamed("Old"));
        Assert.NotSame(first.SymbolsOfVersion(2), second.SymbolsOfVersion(3));
    }

    [Fact]
    public void Advance_AddFile_RebuildsImportModuleVersions()
    {
        using ResolutionStoreFixture fixture = ResolutionStoreFixture.Create();
        fixture.AddFile(1, "src/app.ts", "typescript");
        fixture.AddSymbol(
            1,
            "imp",
            "Lib",
            "import",
            "src/app.ts",
            language: "typescript",
            metadataJson: """{"source":"./lib"}""");

        RevisionFactCache first;
        ImportBinding[] firstImports;
        using (var firstRead = fixture.OpenRead())
        {
            first = RevisionFactCache.Load(firstRead, fixture.Visibility());
            Assert.Null(Assert.Single(first.ImportsOf(1)).ModuleVersionId);
            firstImports = first.ImportArrayOf(1);
        }

        fixture.AddSymbol(2, "lib", "Lib", "class", "src/lib.ts", language: "typescript");
        fixture.FlipManifest(2, [("src/app.ts", 1, "typescript", "indexed"), ("src/lib.ts", 2, "typescript", "indexed")]);

        using var secondRead = fixture.OpenRead();
        RevisionFactCache second = first.Advance(secondRead, fixture.Visibility());

        Assert.Same(first.SymbolsOfVersion(1), second.SymbolsOfVersion(1));
        ImportBinding[] secondImports = second.ImportArrayOf(1);
        Assert.NotSame(firstImports, secondImports);
        Assert.Equal(2, Assert.Single(second.ImportsOf(1)).ModuleVersionId);
    }

    [Fact]
    public void Advance_RemoveFile_ClearsModuleVersionOnUnchangedImporter()
    {
        using ResolutionStoreFixture fixture = ResolutionStoreFixture.Create();
        fixture.AddFile(1, "src/app.ts", "typescript");
        fixture.AddFile(2, "src/lib.ts", "typescript");
        fixture.AddSymbol(
            1,
            "imp",
            "Lib",
            "import",
            "src/app.ts",
            language: "typescript",
            metadataJson: """{"source":"./lib"}""");
        fixture.AddSymbol(2, "lib", "Lib", "class", "src/lib.ts", language: "typescript");

        RevisionFactCache first;
        using (var firstRead = fixture.OpenRead())
        {
            first = RevisionFactCache.Load(firstRead, fixture.Visibility());
            Assert.Equal(2, Assert.Single(first.ImportsOf(1)).ModuleVersionId);
        }

        fixture.FlipManifest(2, [("src/app.ts", 1, "typescript", "indexed")]);

        using var secondRead = fixture.OpenRead();
        RevisionFactCache second = first.Advance(secondRead, fixture.Visibility());

        Assert.Same(first.SymbolsOfVersion(1), second.SymbolsOfVersion(1));
        Assert.Null(Assert.Single(second.ImportsOf(1)).ModuleVersionId);
        Assert.DoesNotContain(second.SymbolsNamed("Lib"), s => s.Kind == FactSymbolKind.Class);
    }

    [Fact]
    public void Advance_ExtensionPrecedence_PrefersTypescriptOverJavascriptAfterAdd()
    {
        using ResolutionStoreFixture fixture = ResolutionStoreFixture.Create();
        fixture.AddFile(1, "src/app.ts", "typescript");
        fixture.AddFile(2, "src/lib.js", "typescript");
        fixture.AddSymbol(
            1,
            "imp",
            "Lib",
            "import",
            "src/app.ts",
            language: "typescript",
            metadataJson: """{"source":"./lib"}""");
        fixture.AddSymbol(2, "js", "Lib", "function", "src/lib.js", language: "typescript");

        RevisionFactCache first;
        using (var firstRead = fixture.OpenRead())
        {
            first = RevisionFactCache.Load(firstRead, fixture.Visibility());
            Assert.Equal(2, Assert.Single(first.ImportsOf(1)).ModuleVersionId);
        }

        fixture.AddSymbol(3, "ts", "Lib", "class", "src/lib.ts", language: "typescript");
        fixture.FlipManifest(2,
        [
            ("src/app.ts", 1, "typescript", "indexed"),
            ("src/lib.js", 2, "typescript", "indexed"),
            ("src/lib.ts", 3, "typescript", "indexed"),
        ]);

        using var secondRead = fixture.OpenRead();
        RevisionFactCache second = first.Advance(secondRead, fixture.Visibility());

        Assert.Equal(3, Assert.Single(second.ImportsOf(1)).ModuleVersionId);
        Assert.Same(first.SymbolsOfVersion(1), second.SymbolsOfVersion(1));
        Assert.Same(first.SymbolsOfVersion(2), second.SymbolsOfVersion(2));
    }
}
