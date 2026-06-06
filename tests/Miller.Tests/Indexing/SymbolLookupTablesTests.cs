using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins <see cref="SymbolLookupTables"/> — the symbol-id / name / file lookup maps shared by the
/// in-memory <see cref="SymbolSearchProjection"/> and the on-disk <c>FtsSymbolSearchIndex</c>, WITHOUT
/// the BM25 postings. Both backends must expose identical lookup behavior, so the maps live in one place.
/// </summary>
public sealed class SymbolLookupTablesTests
{
    private static IndexedSymbol Sym(int docId, string id, string name, string path, string? parentId = null) =>
        new(docId, id, name, Signature: null, Kind: "method", Language: "csharp", FilePath: path,
            StartLine: 1, EndLine: 2, ParentId: parentId, IsTest: false);

    private static SymbolLookupTables Build(params IndexedSymbol[] symbols) =>
        SymbolLookupTables.Build(symbols);

    [Fact]
    public void Build_CountsDocumentsAndResolvesByDocId()
    {
        var t = Build(
            Sym(0, "a", "Alpha", "src/A.cs"),
            Sym(1, "b", "Beta", "src/B.cs"));

        Assert.Equal(2, t.DocumentCount);
        Assert.Equal("Beta", t.Resolve(1).Name);
    }

    [Fact]
    public void Resolve_OutOfRange_Throws()
    {
        var t = Build(Sym(0, "a", "Alpha", "src/A.cs"));
        Assert.Throws<ArgumentOutOfRangeException>(() => t.Resolve(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => t.Resolve(-1));
    }

    [Fact]
    public void Build_NonContiguousDocId_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => Build(
            Sym(0, "a", "Alpha", "src/A.cs"),
            Sym(5, "b", "Beta", "src/B.cs")));   // DocId 5 at position 1
        Assert.Equal("symbols", ex.ParamName);
    }

    [Fact]
    public void FindByName_ReturnsAllMatches_EmptyWhenAbsent()
    {
        var t = Build(
            Sym(0, "a", "Handle", "src/A.cs"),
            Sym(1, "b", "Handle", "src/B.cs"),
            Sym(2, "c", "Other", "src/C.cs"));

        Assert.Equal(2, t.FindByName("Handle").Count);
        Assert.Empty(t.FindByName("Missing"));
        Assert.Throws<ArgumentNullException>(() => t.FindByName(null!));
    }

    [Fact]
    public void FindBySymbolId_ResolvesOrNull()
    {
        var t = Build(Sym(0, "sid-0", "Alpha", "src/A.cs"));
        Assert.Equal("Alpha", t.FindBySymbolId("sid-0")!.Name);
        Assert.Null(t.FindBySymbolId("nope"));
    }

    [Fact]
    public void FindChildren_ReturnsDirectChildrenByParentId()
    {
        var t = Build(
            Sym(0, "parent", "Parent", "src/A.cs"),
            Sym(1, "child-a", "ChildA", "src/A.cs", parentId: "parent"),
            Sym(2, "child-b", "ChildB", "src/A.cs", parentId: "parent"),
            Sym(3, "other", "Other", "src/B.cs"));

        Assert.Equal(["ChildA", "ChildB"], t.FindChildren("parent").Select(static s => s.Name).ToArray());
        Assert.Empty(t.FindChildren("missing"));
    }

    [Fact]
    public void FindByFilePath_And_IsIndexedFilePath()
    {
        var t = Build(
            Sym(0, "a", "Alpha", "auth/UserService.cs"),
            Sym(1, "b", "Beta", "auth/UserService.cs"));

        Assert.Equal(2, t.FindByFilePath("auth/UserService.cs").Count);
        Assert.True(t.IsIndexedFilePath("auth/UserService.cs"));
        Assert.False(t.IsIndexedFilePath("nope.cs"));
    }

    [Fact]
    public void ResolveIndexedFilePath_ExactUniqueOrAmbiguous()
    {
        var t = Build(
            Sym(0, "a", "Alpha", "auth/UserService.cs"),
            Sym(1, "b", "Beta", "core/Cache.cs"),
            Sym(2, "c", "Gamma", "alt/Cache.cs"));

        Assert.Equal("auth/UserService.cs", t.ResolveIndexedFilePath("auth/UserService.cs")); // exact path
        Assert.Equal("auth/UserService.cs", t.ResolveIndexedFilePath("UserService.cs"));       // unique filename
        Assert.Null(t.ResolveIndexedFilePath("Cache.cs"));                                     // ambiguous filename
        Assert.Null(t.ResolveIndexedFilePath("nope.cs"));                                      // unknown
    }

    [Fact]
    public void KnownExtensions_CollectsDistinctExtensions()
    {
        var t = Build(
            Sym(0, "a", "Alpha", "src/A.cs"),
            Sym(1, "b", "Beta", "web/app.ts"));

        Assert.Contains(".cs", t.KnownExtensions);
        Assert.Contains(".ts", t.KnownExtensions);
    }

    [Fact]
    public void FindByFilePathFragment_RanksExactFileNameFirst()
    {
        var t = Build(
            Sym(0, "a", "Alpha", "auth/UserService.cs"),
            Sym(1, "b", "Beta", "core/Other.cs"));

        var hits = t.FindByFilePathFragment("UserService.cs", limit: 10);
        Assert.NotEmpty(hits);
        Assert.Equal("Alpha", hits[0].Name);
    }

    [Fact]
    public void FindByFilePathFragment_ReturnsOneSymbolPerFileBeforeExtraSymbols()
    {
        var t = Build(
            Sym(0, "a", "Alpha", "src/a/mod.rs"),
            Sym(1, "b", "Beta", "src/a/mod.rs"),
            Sym(2, "c", "Gamma", "src/b/mod.rs"));

        var hits = t.FindByFilePathFragment("mod.rs", limit: 2);

        Assert.Equal(["Alpha", "Gamma"], hits.Select(static hit => hit.Name).ToArray());
    }
}
