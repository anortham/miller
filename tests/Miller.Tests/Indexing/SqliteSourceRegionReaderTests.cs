using System.Text;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins the source-region read layer against the julie-extract <c>source_regions</c> table.
/// Fast suite only: temp SQLite fixture, no julie-extract subprocess.
/// </summary>
public sealed class SqliteSourceRegionReaderTests
{
    [Fact]
    public void ReadIndexedRegions_ReadsIndexedKindsJoinedToFiles_InDeterministicOrder()
    {
        const string aPath = "src/A.cs";
        const string bPath = "src/B.cs";
        const string aContent = "class A { string Url = \"http://localhost\"; }\n";
        const string bContent = "// TODO: later\nclass B {}\n";

        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            Array.Empty<JulieDbFixture.SymbolRow>(),
            fileContent: new Dictionary<string, string>
            {
                [aPath] = aContent,
                [bPath] = bContent,
            },
            sourceRegions: new[]
            {
                new JulieDbFixture.SourceRegionRow(
                    "region-c", "file:" + bPath, bPath, "csharp", "comment", "sym-b",
                    1, 1, 1, 15, 0, 14, "{\"scope\":\"line\"}"),
                new JulieDbFixture.SourceRegionRow(
                    "region-b", "file:" + aPath, aPath, "csharp", "string_literal", "sym-a",
                    1, 24, 1, 42, 23, 41, "{\"quote\":\"double\"}"),
                new JulieDbFixture.SourceRegionRow(
                    "region-a", "file:" + aPath, aPath, "csharp", "doc_comment", null,
                    1, 1, 1, 12, 23, 41, null),
                new JulieDbFixture.SourceRegionRow(
                    "region-embedded", "file:" + aPath, aPath, "html", "embedded", "sym-a",
                    1, 1, 1, 42, 0, 42, "{\"embedded_language\":\"javascript\"}"),
            });

        var regions = SqliteSourceRegionReader.ReadIndexedRegions(fx.DbPath);

        Assert.Equal(new[] { "region-a", "region-b", "region-c" },
            regions.Select(r => r.SourceRegionId).ToArray());
        Assert.DoesNotContain(regions, r => r.Kind == "embedded");

        var first = regions[0];
        Assert.Equal("file:" + aPath, first.FileId);
        Assert.Equal(aPath, first.Path);
        Assert.Equal("csharp", first.Language);
        Assert.Equal("doc_comment", first.Kind);
        Assert.Null(first.ContainingSymbolId);
        Assert.Null(first.MetadataJson);
        Assert.Equal(23, first.StartByte);
        Assert.Equal(41, first.EndByte);
        Assert.Equal(Encoding.UTF8.GetByteCount(aContent), first.ContentBytes);
        Assert.StartsWith("blake3:", first.ContentHash);
        Assert.Equal("indexed", first.Status);

        Assert.Equal("{\"quote\":\"double\"}", regions[1].MetadataJson);
        Assert.Equal("sym-b", regions[2].ContainingSymbolId);
        Assert.Equal(Encoding.UTF8.GetByteCount(bContent), regions[2].ContentBytes);
    }

    [Fact]
    public void ReadHasDocComment_ReturnsOnlyRequestedSymbolsWithSymbolsDocComment()
    {
        const string docSymbolId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string noDocSymbolId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string unrequestedDocSymbolId = "cccccccccccccccccccccccccccccccc";

        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                new JulieDbFixture.SymbolRow(docSymbolId, "HasDoc", "method", "csharp",
                    "src/Docs.cs", "void HasDoc()", 10, null) { DocComment = "/// docs" },
                new JulieDbFixture.SymbolRow(noDocSymbolId, "NoDoc", "method", "csharp",
                    "src/Docs.cs", "void NoDoc()", 20, null),
                new JulieDbFixture.SymbolRow(unrequestedDocSymbolId, "UnrequestedDoc", "method", "csharp",
                    "src/Docs.cs", "void UnrequestedDoc()", 30, null) { DocComment = "/// docs" },
            },
            sourceRegions: new[]
            {
                new JulieDbFixture.SourceRegionRow(
                    "region-doc-for-no-doc-symbol", "file:src/Docs.cs", "src/Docs.cs", "csharp", "doc_comment",
                    noDocSymbolId, 19, 1, 19, 9, 100, 108, null),
            });

        var hasDoc = SqliteSourceRegionReader.ReadHasDocComment(
            fx.DbPath,
            new[] { docSymbolId, noDocSymbolId, "dddddddddddddddddddddddddddddddd" });

        Assert.Equal(new[] { docSymbolId }, hasDoc.OrderBy(id => id, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(noDocSymbolId, hasDoc);
        Assert.DoesNotContain(unrequestedDocSymbolId, hasDoc);
    }
}
