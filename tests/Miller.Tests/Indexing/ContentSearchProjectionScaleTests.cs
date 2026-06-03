using System.Diagnostics;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Scale lock for the phase-3 content projection: build the docs-like index from a large on-disk corpus and
/// assert it stays correct and within a generous wall-clock ceiling. This is the "large-repo content build
/// cost" gate from the search-projections design — it re-sources every file from disk and BLAKE3-verifies it
/// (the production loader path), so a pathological cost regression (e.g. an accidental O(N²) in the BM25 build
/// or per-file hashing) trips the ceiling rather than silently slowing every content search.
///
/// <para><c>[Trait("Category","Scale")]</c>: it materializes hundreds of files on disk and builds a sizable
/// in-memory index, so it is grouped with the Scale suite to keep the fast suite pure logic. It spawns NO
/// julie-extract (it builds a synthetic v1 fixture directly), so it does not use the Scale launch signal.</para>
/// </summary>
[Trait("Category", "Scale")]
public sealed class ContentSearchProjectionScaleTests
{
    private const int DocCount = 1000;
    private const double BuildCeilingSeconds = 30.0;

    private readonly ITestOutputHelper _output;

    public ContentSearchProjectionScaleTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Load_LargeDocsCorpus_BuildsCorrectlyWithinCeiling()
    {
        // A shared token present in every doc + a unique token per doc, so we can assert both a broad match
        // (every doc) and an exact pinpoint (one doc) survive the at-scale build.
        var files = new JulieDbFixture.FileSpec[DocCount];
        for (int i = 0; i < DocCount; i++)
        {
            files[i] = new JulieDbFixture.FileSpec($"docs/page{i:D5}.md")
            {
                Language = "markdown",
                DiskText = $"# Page {i}\nshared corpustoken and a unique marker uniquetoken{i:D5} here.\n",
            };
        }

        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            Array.Empty<JulieDbFixture.SymbolRow>(),
            extraFiles: files);

        var sw = Stopwatch.StartNew();
        ContentSearchProjection projection = ContentSearchProjectionLoader.Load(fx.DbPath, fx.WorkspaceRoot);
        sw.Stop();

        _output.WriteLine($"content projection: built {projection.DocumentCount} docs in {sw.Elapsed.TotalSeconds:F2}s");

        Assert.Equal(DocCount, projection.DocumentCount);
        Assert.True(
            sw.Elapsed.TotalSeconds < BuildCeilingSeconds,
            $"content build took {sw.Elapsed.TotalSeconds:F2}s, exceeding the {BuildCeilingSeconds:F0}s ceiling");

        // The shared token matches the whole corpus; the page caps at the requested limit (never silently drops
        // beyond it), so the top page is full and ranked.
        Assert.Equal(25, projection.Search("corpustoken", limit: 25).Count);

        // The doc carrying the exact per-page marker ranks #1 for that marker (the digit-specific subtoken
        // dominates the shared `uniquetoken`/`corpustoken` components every doc holds), with its line + snippet.
        var top = projection.Search("uniquetoken00777", limit: 10)[0];
        Assert.Equal("docs/page00777.md", top.Path);
        Assert.Equal(2, top.Line);
        Assert.Contains("uniquetoken00777", top.Snippet);
    }
}
