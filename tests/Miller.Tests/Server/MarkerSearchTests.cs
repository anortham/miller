using System.Text.Json;
using Miller.Indexing;
using Miller.Server.Tools;
using Miller.Tests.Indexing;
using Xunit;

namespace Miller.Tests.Server;

public sealed class MarkerSearchTests
{
    [Fact]
    public void Run_ReadsOnlyCodeMarkerFacts()
    {
        using var fixture = JulieDbFixture.CreateDefault();
        fixture.AddStructuralFact(
            "marker-todo",
            null,
            "src/Work.cs",
            patternId: "code.marker.v1",
            captureName: "marker",
            nodeKind: "comment",
            metadataJson: """{"marker":"TODO","description":"finish work"}""");
        fixture.AddStructuralFact(
            "not-a-marker",
            null,
            "src/Other.cs",
            patternId: "custom.todo.v1",
            captureName: "marker",
            nodeKind: "comment",
            metadataJson: """{"marker":"TODO","description":"must not surface"}""");

        string output = MarkerSearch.Run(
            fixture.DbPath,
            MarkerSearch.ParseMarkers(null),
            50,
            excludeTests: false,
            json: true,
            compactBanner: null,
            filePattern: null,
            language: null,
            out int count);

        Assert.Equal(1, count);
        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement row = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal("marker-todo", row.GetProperty("region_id").GetString());
        Assert.Equal("TODO: finish work", row.GetProperty("snippet").GetString());
    }

    [Fact]
    public void Run_AppliesMarkerPathAndLanguageFilters()
    {
        using var fixture = JulieDbFixture.CreateDefault();
        fixture.AddStructuralFact(
            "marker-todo",
            null,
            "src/Work.cs",
            patternId: "code.marker.v1",
            captureName: "marker",
            nodeKind: "comment",
            metadataJson: """{"marker":"TODO"}""");
        fixture.AddStructuralFact(
            "marker-fixme",
            null,
            "docs/Work.md",
            language: "markdown",
            patternId: "code.marker.v1",
            captureName: "marker",
            nodeKind: "comment",
            metadataJson: """{"marker":"FIXME"}""");

        string output = MarkerSearch.Run(
            fixture.DbPath,
            MarkerSearch.ParseMarkers("TODO"),
            50,
            excludeTests: false,
            json: false,
            compactBanner: null,
            filePattern: "src/**",
            language: "csharp",
            out int count);

        Assert.Equal(1, count);
        Assert.Contains("src/Work.cs:1", output, StringComparison.Ordinal);
        Assert.DoesNotContain("docs/Work.md", output, StringComparison.Ordinal);
    }

    [Fact]
    public void FindMarkers_AppliesMarkerFilterBeforeLimit()
    {
        using var fixture = JulieDbFixture.CreateDefault();
        for (int i = 0; i < MarkerSearch.MaxLimit; i++)
        {
            fixture.AddStructuralFact(
                $"marker-todo-{i:D3}",
                null,
                $"aaa/{i:D3}.cs",
                patternId: MarkerFactReader.PatternId,
                captureName: "marker",
                nodeKind: "comment",
                metadataJson: """{"marker":"TODO"}""");
        }
        fixture.AddStructuralFact(
            "marker-hack-target",
            null,
            "zzz/Target.cs",
            patternId: MarkerFactReader.PatternId,
            captureName: "marker",
            nodeKind: "comment",
            metadataJson: """{"marker":"HACK"}""");

        MarkerSearchHit hit = Assert.Single(MarkerSearch.FindMarkers(
            fixture.DbPath,
            MarkerSearch.ParseMarkers("HACK"),
            limit: 1,
            excludeTests: false,
            filePattern: null,
            language: null));

        Assert.Equal("marker-hack-target", hit.Region.RegionId);
    }

    [Fact]
    public void FindMarkers_AppliesPathFilterBeforeLimit()
    {
        using var fixture = JulieDbFixture.CreateDefault();
        for (int i = 0; i < MarkerSearch.MaxLimit; i++)
        {
            fixture.AddStructuralFact(
                $"marker-early-{i:D3}",
                null,
                $"aaa/{i:D3}.cs",
                patternId: MarkerFactReader.PatternId,
                captureName: "marker",
                nodeKind: "comment",
                metadataJson: """{"marker":"TODO"}""");
        }
        fixture.AddStructuralFact(
            "marker-path-target",
            null,
            "zzz/Target.cs",
            patternId: MarkerFactReader.PatternId,
            captureName: "marker",
            nodeKind: "comment",
            metadataJson: """{"marker":"TODO"}""");

        MarkerSearchHit hit = Assert.Single(MarkerSearch.FindMarkers(
            fixture.DbPath,
            MarkerSearch.ParseMarkers("TODO"),
            limit: 1,
            excludeTests: false,
            filePattern: "zzz/**",
            language: null));

        Assert.Equal("marker-path-target", hit.Region.RegionId);
    }

    [Fact]
    public void Reader_ExcludeTests_FiltersTopLevelTestPathsBeforeApplyingLimit()
    {
        using var fixture = JulieDbFixture.CreateDefault();
        fixture.AddStructuralFact(
            "marker-test",
            null,
            "tests/AFirstTests.cs",
            patternId: MarkerFactReader.PatternId,
            captureName: "marker",
            nodeKind: "comment",
            metadataJson: """{"marker":"TODO"}""");
        fixture.AddStructuralFact(
            "marker-production",
            null,
            "src/Work.cs",
            patternId: MarkerFactReader.PatternId,
            captureName: "marker",
            nodeKind: "comment",
            metadataJson: """{"marker":"TODO"}""");

        MarkerFactRow row = Assert.Single(MarkerFactReader.Read(fixture.DbPath, excludeTests: true, limit: 1));
        Assert.Equal("marker-production", row.FactId);
    }

    [Theory]
    [InlineData(null, new[] { "TODO", "FIXME", "HACK", "XXX" })]
    [InlineData("fixme,todo", new[] { "FIXME", "TODO" })]
    public void ParseMarkers_NormalizesAllowedVocabulary(string? value, string[] expected) =>
        Assert.Equal(expected, MarkerSearch.ParseMarkers(value));

    [Fact]
    public void ParseMarkers_RejectsUnknownValues()
    {
        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(() => MarkerSearch.ParseMarkers("NOTE"));
        Assert.Contains("TODO, FIXME, HACK, or XXX", exception.Message, StringComparison.Ordinal);
    }
}
