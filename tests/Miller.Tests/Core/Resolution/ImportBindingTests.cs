using Miller.Core.Resolution;
using Xunit;

namespace Miller.Tests.Core.Resolution;

public sealed class ImportBindingTests
{
    [Fact]
    public void FromSymbol_UsesAliasThenLocalNameThenSymbolName()
    {
        var aliased = ImportBinding.FromSymbol("sym", new ImportMetadata(Alias: "A", LocalName: "L"));
        var local = ImportBinding.FromSymbol("sym", new ImportMetadata(LocalName: "L"));
        var fallback = ImportBinding.FromSymbol("sym", new ImportMetadata());

        Assert.Equal("A", aliased.LocalName);
        Assert.Equal("L", local.LocalName);
        Assert.Equal("sym", fallback.LocalName);
    }

    [Fact]
    public void FromSymbol_PrefersImportedNameThenImportedThenImportedNameCamel()
    {
        var named = ImportBinding.FromSymbol(
            "sym",
            new ImportMetadata(ImportedName: "N", Imported: "I", ImportedNameCamel: "C"));
        var imported = ImportBinding.FromSymbol("sym", new ImportMetadata(Imported: "I", ImportedNameCamel: "C"));
        var camel = ImportBinding.FromSymbol("sym", new ImportMetadata(ImportedNameCamel: "C"));

        Assert.Equal("N", named.ImportedName);
        Assert.Equal("I", imported.ImportedName);
        Assert.Equal("C", camel.ImportedName);
    }

    [Fact]
    public void FromSymbol_UsesSymbolNameWhenLocalNameDiffersAndNoImportedField()
    {
        var binding = ImportBinding.FromSymbol("Widget", new ImportMetadata(Alias: "W"));

        Assert.Equal("W", binding.LocalName);
        Assert.Equal("Widget", binding.ImportedName);
    }

    [Fact]
    public void FromSymbol_LeavesImportedNameEmptyWhenLocalNameMatchesSymbol()
    {
        var binding = ImportBinding.FromSymbol("Widget", new ImportMetadata(LocalName: "Widget"));

        Assert.Null(binding.ImportedName);
    }

    [Fact]
    public void FromSymbol_DropsEmptySource()
    {
        var empty = ImportBinding.FromSymbol("sym", new ImportMetadata(Source: ""));
        var present = ImportBinding.FromSymbol("sym", new ImportMetadata(Source: "./mod"));

        Assert.Null(empty.Source);
        Assert.Equal("./mod", present.Source);
    }

    [Fact]
    public void FromSymbol_ORsBooleanFlags()
    {
        var typeOnly = ImportBinding.FromSymbol("sym", new ImportMetadata(IsTypeOnlySnake: true));
        var def = ImportBinding.FromSymbol("sym", new ImportMetadata(IsDefault: true));
        var ns = ImportBinding.FromSymbol("sym", new ImportMetadata(IsNamespaceSnake: true));

        Assert.True(typeOnly.IsTypeOnly);
        Assert.True(def.IsDefault);
        Assert.True(ns.IsNamespace);
        Assert.False(typeOnly.IsDefault);
    }

    [Fact]
    public void FromSymbol_CarriesCallerResolvedModuleVersion()
    {
        var binding = ImportBinding.FromSymbol("sym", new ImportMetadata(Source: "./mod"), moduleVersionId: 9);

        Assert.Equal(9, binding.ModuleVersionId);
    }

    [Fact]
    public void FromSymbol_NullMetadata_UsesSymbolName()
    {
        var binding = ImportBinding.FromSymbol("sym", metadata: null);

        Assert.Equal("sym", binding.LocalName);
        Assert.Null(binding.ImportedName);
        Assert.Null(binding.Source);
        Assert.False(binding.IsTypeOnly);
        Assert.False(binding.IsDefault);
        Assert.False(binding.IsNamespace);
        Assert.Null(binding.ModuleVersionId);
    }

    [Fact]
    public void ModuleCandidates_NonRelativeSpecifier_YieldsNone()
    {
        Assert.Empty(ImportBinding.ModuleCandidates("src/a.ts", "lodash", "typescript"));
        Assert.Empty(ImportBinding.ModuleCandidates("src/a.ts", "/abs/mod", "typescript"));
    }

    [Fact]
    public void ModuleCandidates_LastSegmentHasDot_YieldsNormalizedPathOnly()
    {
        Assert.Equal(
            ["src/lib/mod.ts"],
            ImportBinding.ModuleCandidates("src/app.ts", "./lib/mod.ts", "typescript"));
    }

    [Fact]
    public void ModuleCandidates_Typescript_TriesExtThenIndex()
    {
        Assert.Equal(
            [
                "src/mod.ts",
                "src/mod.tsx",
                "src/mod.js",
                "src/mod.jsx",
                "src/mod/index.ts",
                "src/mod/index.tsx",
                "src/mod/index.js",
                "src/mod/index.jsx",
            ],
            ImportBinding.ModuleCandidates("src/app.ts", "./mod", "typescript"));
    }

    [Fact]
    public void ModuleCandidates_Javascript_TriesExtThenIndex()
    {
        Assert.Equal(
            [
                "lib/mod.js",
                "lib/mod.jsx",
                "lib/mod.ts",
                "lib/mod.tsx",
                "lib/mod/index.js",
                "lib/mod/index.jsx",
                "lib/mod/index.ts",
                "lib/mod/index.tsx",
            ],
            ImportBinding.ModuleCandidates("lib/app.js", "./mod", "javascript"));
    }

    [Fact]
    public void ModuleCandidates_OtherLanguage_YieldsNone()
    {
        Assert.Empty(ImportBinding.ModuleCandidates("src/App.cs", "./mod", "csharp"));
    }

    [Fact]
    public void ModuleCandidates_ParentPop_NormalizesAgainstImportingDir()
    {
        Assert.Equal(
            ["sibling.ts"],
            ImportBinding.ModuleCandidates("src/app.ts", "../sibling.ts", "typescript"));
    }

    [Fact]
    public void ModuleCandidates_PopPastRoot_YieldsNone()
    {
        Assert.Empty(ImportBinding.ModuleCandidates("app.ts", "../outside.ts", "typescript"));
    }

    [Fact]
    public void ModuleCandidates_DotAndEmptySegments_AreIgnored()
    {
        Assert.Equal(
            ["src/mod.ts"],
            ImportBinding.ModuleCandidates("src/app.ts", "././mod.ts", "typescript"));
    }
}
