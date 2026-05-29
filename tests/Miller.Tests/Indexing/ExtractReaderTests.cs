using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins the M2 on-demand read layer (<see cref="ExtractReader"/>): per-inspect detail (doc/visibility/body
/// spans), name-based references (identifiers — <c>target_symbol_id</c> is always NULL), and the body slice
/// out of <c>files.content</c> with graceful NULL-span degradation. Driven against the inspect fixture; opens
/// the DB Mode=ReadOnly like the M1 reader. Fast suite (no julie-server binary).
/// </summary>
public sealed class ExtractReaderTests
{
    // ---- ReadDetail ----

    [Fact]
    public void ReadDetail_ReturnsDocVisibilityAndBodySpans()
    {
        using var fx = JulieDbFixture.CreateForInspect();

        var detail = ExtractReader.ReadDetail(fx.DbPath, JulieDbFixture.GetUserId);

        Assert.NotNull(detail);
        Assert.Equal("Gets a user by id.", detail!.DocComment);
        Assert.Equal("public", detail.Visibility);
        Assert.Equal("public User GetUser(int id) { ... }", detail.CodeContext);
        Assert.NotNull(detail.BodyStartByte);
        Assert.NotNull(detail.BodyEndByte);
        Assert.Equal(2, detail.BodyStartLine);
        Assert.Equal(4, detail.BodyEndLine);
    }

    [Fact]
    public void ReadDetail_NullColumns_MapToNull()
    {
        using var fx = JulieDbFixture.CreateForInspect();

        // DeleteUser has NULL doc_comment, NULL code_context, NULL body spans.
        var detail = ExtractReader.ReadDetail(fx.DbPath, "c3d4e5f6001122334455667788990a1b");

        Assert.NotNull(detail);
        Assert.Null(detail!.DocComment);
        Assert.Null(detail.CodeContext);
        Assert.Null(detail.BodyStartByte);
        Assert.Null(detail.BodyEndByte);
        Assert.Null(detail.BodyStartLine);
        Assert.Equal("public", detail.Visibility);
    }

    [Fact]
    public void ReadDetail_UnknownId_ReturnsNull()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        Assert.Null(ExtractReader.ReadDetail(fx.DbPath, "ffffffffffffffffffffffffffffffff"));
    }

    // ---- ReadReferences (name-based) ----

    [Fact]
    public void ReadReferences_ByName_ReturnsEveryIdentifierWithThatName()
    {
        using var fx = JulieDbFixture.CreateForInspect();

        var refs = ExtractReader.ReadReferences(fx.DbPath, "GetUser");

        // Two refs to GetUser were recorded (Controller.cs:4 and Repo.cs:9).
        Assert.Equal(2, refs.Count);
        Assert.Contains(refs, r => r.FilePath == "web/Controller.cs" && r.StartLine == 4);
        Assert.Contains(refs, r => r.FilePath == "auth/Repo.cs" && r.StartLine == 9);
        // Each carries its enclosing (containing) symbol id — the callers source.
        Assert.All(refs, r => Assert.False(string.IsNullOrEmpty(r.ContainingSymbolId)));
    }

    [Fact]
    public void ReadReferences_CallsFromASymbol_AreFoundByContainingId()
    {
        using var fx = JulieDbFixture.CreateForInspect();

        // Callees one-hop: identifiers with containing_symbol_id == GetUser AND kind == 'call'.
        var callees = ExtractReader.ReadCallees(fx.DbPath, JulieDbFixture.GetUserId);

        var callee = Assert.Single(callees);
        Assert.Equal("Find", callee.Name);
        Assert.Equal("auth/UserService.cs", callee.FilePath);
        Assert.Equal(3, callee.StartLine);
    }

    [Fact]
    public void ReadReferences_UnknownName_ReturnsEmpty()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        Assert.Empty(ExtractReader.ReadReferences(fx.DbPath, "NoSuchIdentifier"));
    }

    // ---- ReadBody ----

    [Fact]
    public void ReadBody_SlicesTheByteRangeOutOfFileContent()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var detail = ExtractReader.ReadDetail(fx.DbPath, JulieDbFixture.GetUserId)!;

        string? body = ExtractReader.ReadBody(
            fx.DbPath, "auth/UserService.cs", detail.BodyStartByte, detail.BodyEndByte,
            detail.BodyStartLine, detail.BodyEndLine);

        Assert.NotNull(body);
        // The slice must start at the method signature and end at its closing brace.
        Assert.StartsWith("public User GetUser(int id)", body);
        Assert.EndsWith("}", body!.TrimEnd());
        Assert.Contains("return _repo.Find(id);", body);
    }

    [Fact]
    public void ReadBody_NullByteSpans_FallsBackToLineSlice()
    {
        using var fx = JulieDbFixture.CreateForInspect();

        // No byte spans, but line range 2..4 → line-based fallback slice of the content.
        string? body = ExtractReader.ReadBody(
            fx.DbPath, "auth/UserService.cs",
            startByte: null, endByte: null, startLine: 2, endLine: 4);

        Assert.NotNull(body);
        Assert.Contains("GetUser", body!);
        Assert.Contains("return _repo.Find(id);", body);
    }

    [Fact]
    public void ReadBody_NoByteAndNoLineSpans_ReturnsNull()
    {
        using var fx = JulieDbFixture.CreateForInspect();

        string? body = ExtractReader.ReadBody(
            fx.DbPath, "auth/UserService.cs",
            startByte: null, endByte: null, startLine: null, endLine: null);

        Assert.Null(body);
    }

    [Fact]
    public void ReadBody_EmptyFileContent_ReturnsNull()
    {
        // The default fixture writes content='' for every file; a byte slice of empty content has nothing.
        using var fx = JulieDbFixture.CreateDefault();

        string? body = ExtractReader.ReadBody(
            fx.DbPath, "auth/UserService.cs", startByte: 0, endByte: 10, startLine: 1, endLine: 1);

        Assert.Null(body);
    }

    [Fact]
    public void ReadWorkspaceId_ReturnsTheMetadataValue()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        Assert.Equal("ws-inspect-001", ExtractReader.ReadWorkspaceId(fx.DbPath));
    }

    [Fact]
    public void ReadWorkspaceId_AbsentKey_ReturnsNull()
    {
        using var fx = JulieDbFixture.CreateDefault(); // no workspace_id written
        Assert.Null(ExtractReader.ReadWorkspaceId(fx.DbPath));
    }
}
