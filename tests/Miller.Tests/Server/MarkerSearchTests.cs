using System.Text.Json;
using Miller.Core.Search;
using Miller.Indexing;
using Miller.Server.Tools;
using Xunit;

namespace Miller.Tests.Server;

public sealed class MarkerSearchTests
{
    [Fact]
    public void Run_DefaultMarkers_ReturnsCommentMarkersWithContainingSymbol()
    {
        var index = new StubRegionSearchIndex(
            Hit("src/A.cs", 12, "comment", "// TODO move this", "TODO", "A.Run"),
            Hit("src/B.cs", 18, "doc_comment", "/// FIXME document edge case", "FIXME", "B.Build"),
            Hit("src/C.cs", 22, "string_literal", "\"TODO not a comment\"", "TODO", "C.Text"));

        string output = MarkerSearch.Run(
            index,
            MarkerSearch.ParseMarkers(null),
            MarkerSearch.DefaultLimit,
            excludeTests: false,
            json: false,
            compactBanner: null,
            filePattern: null,
            language: null,
            out int count);

        Assert.Equal(2, count);
        Assert.Contains("src/A.cs:12  TODO  comment  A.Run", output);
        Assert.Contains("// TODO move this", output);
        Assert.Contains("src/B.cs:18  FIXME  doc_comment  B.Build", output);
        Assert.DoesNotContain("string_literal", output);
    }

    [Fact]
    public void Run_MarkersFilter_LimitsSearchToRequestedMarkers()
    {
        var index = new StubRegionSearchIndex(
            Hit("src/A.cs", 12, "comment", "// TODO move this", "TODO", "A.Run"),
            Hit("src/B.cs", 18, "comment", "// HACK temporary bypass", "HACK", "B.Build"));

        string output = MarkerSearch.Run(
            index,
            MarkerSearch.ParseMarkers("hack"),
            MarkerSearch.DefaultLimit,
            excludeTests: false,
            json: false,
            compactBanner: null,
            filePattern: null,
            language: null,
            out int count);

        Assert.Equal(1, count);
        Assert.Contains("src/B.cs:18  HACK  comment  B.Build", output);
        Assert.DoesNotContain("TODO", output);
    }

    [Fact]
    public void Run_MultipleMarkersInOneRegion_CollapsesToSingleBlock()
    {
        var index = new StubRegionSearchIndex(
            Hit("src/A.cs", 5, "comment", "// TODO FIXME HACK do all three", "TODO", "A.Run"));

        string output = MarkerSearch.Run(
            index,
            MarkerSearch.ParseMarkers("TODO,FIXME,HACK"),
            MarkerSearch.DefaultLimit,
            excludeTests: false,
            json: false,
            compactBanner: null,
            filePattern: null,
            language: null,
            out int count);

        Assert.Equal(1, count);
        Assert.Contains("src/A.cs:5  TODO,FIXME,HACK  comment  A.Run", output);
        // Exactly one block — the region header appears once, not once per matched marker.
        Assert.Equal(1, output.Split("src/A.cs:5").Length - 1);
    }

    [Fact]
    public void Run_Limit_CountsDistinctRegionsNotMarkerPairs()
    {
        var index = new StubRegionSearchIndex(
            Hit("src/A.cs", 5, "comment", "// TODO FIXME first", "TODO", "A.Run"),
            Hit("src/B.cs", 9, "comment", "// TODO FIXME second", "TODO", "B.Run"));

        string output = MarkerSearch.Run(
            index,
            MarkerSearch.ParseMarkers("TODO,FIXME"),
            limit: 1,
            excludeTests: false,
            json: false,
            compactBanner: null,
            filePattern: null,
            language: null,
            out int count);

        // Two regions each match two markers (4 marker/region pairs); limit=1 keeps one region.
        Assert.Equal(1, count);
        Assert.Contains("src/A.cs:5  TODO,FIXME  comment", output);
        Assert.DoesNotContain("src/B.cs", output);
    }

    [Fact]
    public void Run_Json_MultiMarkerRegion_HasFirstMarkerAndOrderedMarkersArray()
    {
        var index = new StubRegionSearchIndex(
            Hit("src/A.cs", 5, "comment", "// HACK FIXME TODO out of order", "TODO", "A.Run"));

        using JsonDocument doc = JsonDocument.Parse(MarkerSearch.Run(
            index,
            MarkerSearch.ParseMarkers("TODO,FIXME,HACK"),
            MarkerSearch.DefaultLimit,
            excludeTests: false,
            json: true,
            compactBanner: null,
            filePattern: null,
            language: null,
            out int count));
        JsonElement item = Assert.Single(doc.RootElement.EnumerateArray());

        Assert.Equal(1, count);
        // marker keeps its contract meaning: the first marker by canonical rank.
        Assert.Equal("TODO", item.GetProperty("marker").GetString());
        string[] markers = item.GetProperty("markers")
            .EnumerateArray()
            .Select(static e => e.GetString())
            .ToArray()!;
        Assert.Equal(new[] { "TODO", "FIXME", "HACK" }, markers);
    }

    [Fact]
    public void Run_AppliesFileAndLanguageFilters()
    {
        var index = new StubRegionSearchIndex(
            Hit("src/ui/A.ts", 7, "comment", "// TODO frontend", "TODO", "A", language: "typescript"),
            Hit("src/api/A.cs", 9, "comment", "// TODO backend", "TODO", "Api.Handle", language: "csharp"));

        string output = MarkerSearch.Run(
            index,
            MarkerSearch.ParseMarkers(null),
            MarkerSearch.DefaultLimit,
            excludeTests: false,
            json: false,
            compactBanner: null,
            filePattern: "src/api/**",
            language: "csharp",
            out int count);

        Assert.Equal(1, count);
        Assert.Contains("src/api/A.cs:9  TODO  comment  Api.Handle", output);
        Assert.DoesNotContain("frontend", output);
    }

    [Fact]
    public void Run_Json_IncludesMarkerLocationAndContainingSymbol()
    {
        var index = new StubRegionSearchIndex(
            Hit("src/A.cs", 12, "comment", "// XXX remove this fallback", "XXX", "A.Run"));

        using JsonDocument doc = JsonDocument.Parse(MarkerSearch.Run(
            index,
            MarkerSearch.ParseMarkers(null),
            MarkerSearch.DefaultLimit,
            excludeTests: false,
            json: true,
            compactBanner: null,
            filePattern: null,
            language: null,
            out int count));
        JsonElement item = Assert.Single(doc.RootElement.EnumerateArray());

        Assert.Equal(1, count);
        Assert.Equal("XXX", item.GetProperty("marker").GetString());
        Assert.Equal("src/A.cs", item.GetProperty("file").GetString());
        Assert.Equal(12, item.GetProperty("line").GetInt32());
        Assert.Equal("comment", item.GetProperty("kind").GetString());
        Assert.Equal("A.Run", item.GetProperty("containing_symbol_name").GetString());
        Assert.Equal("// XXX remove this fallback", item.GetProperty("snippet").GetString());
    }

    private static RegionSearchHit Hit(
        string path,
        int line,
        string kind,
        string text,
        string marker,
        string containingSymbol,
        string language = "csharp") =>
        new(
            path,
            Score: 2.0,
            line,
            kind,
            text,
            text,
            "region-" + marker.ToLowerInvariant() + "-" + line.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "sym-" + marker.ToLowerInvariant(),
            containingSymbol,
            language);

    private sealed class StubRegionSearchIndex : IRegionSearchIndex
    {
        private readonly IReadOnlyList<RegionSearchHit> _hits;

        public StubRegionSearchIndex(params RegionSearchHit[] hits) => _hits = hits;

        public int DocumentCount => _hits.Count;

        public long Revision { get; } = 1;

        public IReadOnlyList<RegionSearchHit> Search(
            string query,
            IReadOnlySet<string> kinds,
            int limit = 10,
            bool excludeTests = false) =>
            _hits
                .Where(hit => kinds.Contains(hit.Kind)
                    && hit.RawText.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Take(limit)
                .ToArray();
    }
}
