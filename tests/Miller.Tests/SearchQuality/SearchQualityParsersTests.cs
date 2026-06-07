using Miller.SearchQuality;
using Xunit;

namespace Miller.Tests.SearchQuality;

public sealed class SearchQualityParsersTests
{
    [Fact]
    public void ParseMillerJson_NormalizesSymbolAndContentHits()
    {
        const string json = """
            [
              {"name":"Flask","kind":"class","file":"src/flask/app.py","line":109,"score":7.03},
              {"file":"docs/search.md","line":12,"score":3.5,"snippet":"search quality"}
            ]
            """;

        IReadOnlyList<SearchQualityHit> hits = SearchQualityParsers.ParseMillerJson("miller", json);

        Assert.Collection(
            hits,
            first =>
            {
                Assert.Equal("miller", first.Provider);
                Assert.Equal("Flask", first.Title);
                Assert.Equal("Flask", first.Name);
                Assert.Equal("class", first.Kind);
                Assert.Equal("src/flask/app.py", first.Path);
                Assert.Equal(109, first.Line);
                Assert.Equal(7.03, first.Score);
            },
            second =>
            {
                Assert.Equal("docs/search.md", second.Title);
                Assert.Null(second.Name);
                Assert.Equal("content", second.Kind);
                Assert.Equal("docs/search.md", second.Path);
                Assert.Equal(12, second.Line);
            });
    }

    [Fact]
    public void ParseJulieStandaloneJson_ExtractsDefinitionAndOtherMatches()
    {
        const string output = """
            Using workspace from CLI argument: "/Users/murphy/source/flask"
            julie: workspace /Users/murphy/source/flask
            julie: mode=standalone, elapsed=0.1s
            {
              "content": [
                {
                  "text": "Definition found: Flask\n  tests/test_apps/cliapp/inner1/inner2/flask.py (file, python)\n\nOther matches:\n\nsrc/flask/app.py:109\n  class Flask extends App\n\nsrc/flask/sansio/README.md (file, markdown)",
                  "type": "text"
                }
              ],
              "isError": false
            }
            """;

        IReadOnlyList<SearchQualityHit> hits = SearchQualityParsers.ParseJulieStandaloneJson("julie", output);

        Assert.Collection(
            hits,
            first =>
            {
                Assert.Equal("Flask", first.Title);
                Assert.Equal("Flask", first.Name);
                Assert.Equal("file", first.Kind);
                Assert.Equal("tests/test_apps/cliapp/inner1/inner2/flask.py", first.Path);
            },
            second =>
            {
                Assert.Equal("class Flask extends App", second.Title);
                Assert.Equal("src/flask/app.py", second.Path);
                Assert.Equal(109, second.Line);
            },
            third =>
            {
                Assert.Equal("src/flask/sansio/README.md", third.Title);
                Assert.Equal("file", third.Kind);
                Assert.Equal("src/flask/sansio/README.md", third.Path);
            });
    }

    [Fact]
    public void ParseJulieStandaloneJson_SplitsLineNumberFromDefinitionFileRows()
    {
        const string output = """
            julie: mode=standalone, elapsed=0.1s
            {
              "content": [
                {
                  "text": "Definition found: TrajectoryCompressor\n  trajectory_compressor.py:332 (class, python)",
                  "type": "text"
                }
              ],
              "isError": false
            }
            """;

        IReadOnlyList<SearchQualityHit> hits = SearchQualityParsers.ParseJulieStandaloneJson("julie", output);

        SearchQualityHit hit = Assert.Single(hits);
        Assert.Equal("TrajectoryCompressor", hit.Title);
        Assert.Equal("TrajectoryCompressor", hit.Name);
        Assert.Equal("class", hit.Kind);
        Assert.Equal("trajectory_compressor.py", hit.Path);
        Assert.Equal(332, hit.Line);
    }

    [Fact]
    public void ParseErosJson_ExtractsRankedResults()
    {
        const string json = """
            {
              "tool": "search_code",
              "results": [
                {"id":"one","title":"Flask","path":"src/flask/app.py","score":0.98,"backend":"lancedb-hybrid-coderank"},
                {"id":"two","title":"README.md","path":"README.md","score":0.12,"backend":"sqlite"}
              ],
              "notices": []
            }
            """;

        IReadOnlyList<SearchQualityHit> hits = SearchQualityParsers.ParseErosJson("eros:lancedb-hybrid-coderank", json);

        Assert.Collection(
            hits,
            first =>
            {
                Assert.Equal("eros:lancedb-hybrid-coderank", first.Provider);
                Assert.Equal("Flask", first.Title);
                Assert.Equal("src/flask/app.py", first.Path);
                Assert.Equal(0.98, first.Score);
            },
            second =>
            {
                Assert.Equal("README.md", second.Title);
                Assert.Equal("README.md", second.Path);
            });
    }
}
