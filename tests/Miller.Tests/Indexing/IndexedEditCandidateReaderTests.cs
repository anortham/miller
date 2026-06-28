using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class IndexedEditCandidateReaderTests
{
    [Fact]
    public void FindCandidates_ReturnsChunkForLiteralOldText()
    {
        using var fx = CreateFixture("""
            public class Api
            {
                public string Version => "KnownNeedle";
            }
            """);
        BuildContentDb(fx, revision: 12);

        var result = new IndexedEditCandidateReader().FindCandidates(
            fx.DbPath,
            "src/Api.cs",
            expectedRevision: 12,
            oldText: "KnownNeedle",
            query: null,
            anchor: null,
            line: null,
            limit: 5);

        Assert.Equal(IndexedEditCandidateState.Current, result.State);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("src/Api.cs", candidate.Path);
        Assert.Contains("KnownNeedle", candidate.RawText, StringComparison.Ordinal);
        Assert.Equal(12, candidate.WorkspaceRevision);
        Assert.StartsWith("blake3:", candidate.SourceHash, StringComparison.Ordinal);
        Assert.True(candidate.ByteEnd > candidate.ByteStart);
    }

    [Fact]
    public void FindCandidates_ReturnsChunkForQuerySelector()
    {
        using var fx = CreateFixture("""
            public class Api
            {
                public string Version => "KnownQuerySelector";
            }
            """);
        BuildContentDb(fx, revision: 12);

        var result = new IndexedEditCandidateReader().FindCandidates(
            fx.DbPath,
            "src/Api.cs",
            expectedRevision: 12,
            oldText: null,
            query: "KnownQuerySelector",
            anchor: null,
            line: null,
            limit: 5);

        Assert.Equal(IndexedEditCandidateState.Current, result.State);
        Assert.Single(result.Candidates);
    }

    [Fact]
    public void FindCandidates_NarrowsByLine()
    {
        using var fx = CreateFixture(NumberedLines(220, (2, "duplicate-marker first"), (170, "duplicate-marker second")));
        BuildContentDb(fx, revision: 12);

        var result = new IndexedEditCandidateReader().FindCandidates(
            fx.DbPath,
            "src/Api.cs",
            expectedRevision: 12,
            oldText: "duplicate-marker",
            query: null,
            anchor: null,
            line: 170,
            limit: 5);

        Assert.Equal(IndexedEditCandidateState.Current, result.State);
        var candidate = Assert.Single(result.Candidates);
        Assert.True(candidate.LineStart <= 170);
        Assert.True(candidate.LineEnd >= 170);
        Assert.Contains("duplicate-marker second", candidate.RawText, StringComparison.Ordinal);
        Assert.DoesNotContain("duplicate-marker first", candidate.RawText, StringComparison.Ordinal);
    }

    [Fact]
    public void FindCandidates_NarrowsByAnchor()
    {
        using var fx = CreateFixture(NumberedLines(
            220,
            (2, "target-value alpha-anchor"),
            (170, "target-value beta-anchor")));
        BuildContentDb(fx, revision: 12);

        var result = new IndexedEditCandidateReader().FindCandidates(
            fx.DbPath,
            "src/Api.cs",
            expectedRevision: 12,
            oldText: "target-value",
            query: null,
            anchor: "beta-anchor",
            line: null,
            limit: 5);

        Assert.Equal(IndexedEditCandidateState.Current, result.State);
        var candidate = Assert.Single(result.Candidates);
        Assert.Contains("beta-anchor", candidate.RawText, StringComparison.Ordinal);
        Assert.DoesNotContain("alpha-anchor", candidate.RawText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("stale")]
    [InlineData("corrupt")]
    public void FindCandidates_MissingStaleOrCorruptContentDb_IsUnavailableNotNoMatch(string state)
    {
        using var fx = CreateFixture("public class Api { public string Version => \"KnownNeedle\"; }\n");
        string contentDbPath = ContentCorpusSidecar.ContentDbPathFor(fx.DbPath);
        if (state == "stale")
            BuildContentDb(fx, revision: 11);
        else if (state == "corrupt")
            File.WriteAllText(contentDbPath, "not sqlite");

        var result = new IndexedEditCandidateReader().FindCandidates(
            fx.DbPath,
            "src/Api.cs",
            expectedRevision: 12,
            oldText: "KnownNeedle",
            query: null,
            anchor: null,
            line: null,
            limit: 5);

        Assert.Equal(IndexedEditCandidateState.Unavailable, result.State);
        Assert.Empty(result.Candidates);
        Assert.NotEqual(IndexedEditCandidateState.NoMatch, result.State);
        Assert.Contains(state, result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FindCandidates_SkippedFileWithoutActiveContentSource_IsUnavailable()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [],
            extraFiles:
            [
                new JulieDbFixture.FileSpec("src/Skipped.cs")
                {
                    Language = "csharp",
                    DiskText = "public class Skipped { public string Value => \"KnownNeedle\"; }",
                    StaleHash = true,
                },
            ]);
        BuildContentDb(fx, revision: 12);

        var result = new IndexedEditCandidateReader().FindCandidates(
            fx.DbPath,
            "src/Skipped.cs",
            expectedRevision: 12,
            oldText: "KnownNeedle",
            query: null,
            anchor: null,
            line: null,
            limit: 5);

        Assert.Equal(IndexedEditCandidateState.Unavailable, result.State);
        Assert.Contains("active content source", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FindCandidates_ReturnsBoundedChunksNotFullFile()
    {
        using var fx = CreateFixture(NumberedLines(
            420,
            (2, "marker-value one"),
            (170, "marker-value two"),
            (330, "marker-value three")));
        BuildContentDb(fx, revision: 12);

        var result = new IndexedEditCandidateReader().FindCandidates(
            fx.DbPath,
            "src/Api.cs",
            expectedRevision: 12,
            oldText: "marker-value",
            query: null,
            anchor: null,
            line: null,
            limit: 2);

        Assert.Equal(IndexedEditCandidateState.Current, result.State);
        Assert.Equal(2, result.Candidates.Count);
        Assert.All(result.Candidates, candidate =>
        {
            Assert.True(candidate.LineEnd - candidate.LineStart + 1 <= ContentCorpusChunker.DefaultChunkLines);
            Assert.DoesNotContain("line 420", candidate.RawText, StringComparison.Ordinal);
        });
    }

    private static JulieDbFixture CreateFixture(string sourceText) =>
        JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new JulieDbFixture.SymbolRow("sym-api", "Api", "class", "csharp", "src/Api.cs", "public class Api", 1, null)
                {
                    EndLine = Math.Max(1, sourceText.Count(static ch => ch == '\n') + 1),
                },
            ],
            fileContent: new Dictionary<string, string>
            {
                ["src/Api.cs"] = sourceText,
            });

    private static void BuildContentDb(JulieDbFixture fx, long revision)
    {
        ContentCorpusWriter.Write(
            ContentCorpusSidecar.ContentDbPathFor(fx.DbPath),
            fx.DbPath,
            fx.WorkspaceRoot,
            workspaceId: "workspace-1",
            revision);
    }

    private static string NumberedLines(int count, params (int Line, string Text)[] replacements)
    {
        var byLine = replacements.ToDictionary(static r => r.Line, static r => r.Text);
        return string.Join('\n', Enumerable.Range(1, count).Select(line =>
            byLine.TryGetValue(line, out var text) ? text : "line " + line)) + "\n";
    }
}
