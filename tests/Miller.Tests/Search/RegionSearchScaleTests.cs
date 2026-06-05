using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Server.Tools;
using Xunit;

namespace Miller.Tests.Search;

/// <summary>
/// Scale proof for the first source-region consumer: the real pinned julie-extract binary emits C#
/// source_regions, Miller builds region tables in the current search sidecar from that artifact, and explicit
/// region search takes the disk region path. This spawns julie-extract, so it is excluded from the fast suite.
/// </summary>
[Trait("Category", "Scale")]
public sealed class RegionSearchScaleTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _work;

    public RegionSearchScaleTests(ITestOutputHelper output)
    {
        _output = output;
        _work = Path.Combine(Path.GetTempPath(), "miller-region-scale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_work);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_work, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void LiveExtract_BuildsRegionSidecar_AndSearchesCommentAndStringLiteralText()
    {
        string binary = ScaleTestSupport.RequireJulieServer();
        string repo = Path.Combine(_work, "repo");
        string db = Path.Combine(_work, ".miller", "symbols.db");
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "RegionProbe.cs"), """
            namespace RegionProbe;

            public sealed class RegionProbe
            {
                // CommentOnlyNeedle lives only inside a comment.
                public string Value => "LiteralOnlyNeedle";
            }
            """);

        var runner = new JulieExtractRunner(binary);
        ExtractReport report = runner.Scan(repo, db, force: true);
        Assert.NotEqual("failed", report.Status);
        long revision = report.Revision ?? ReadLatestRevision(db);

        long sourceRegionCount = Count(db, "SELECT COUNT(*) FROM source_regions;");
        long commentCount = Count(db, "SELECT COUNT(*) FROM source_regions WHERE kind='comment';");
        long stringCount = Count(db, "SELECT COUNT(*) FROM source_regions WHERE kind='string_literal';");
        Assert.True(sourceRegionCount > 0, "real 2.1.1 extract should emit source_regions");
        Assert.True(commentCount > 0, "real 2.1.1 extract should emit C# comment regions");
        Assert.True(stringCount > 0, "real 2.1.1 extract should emit C# string_literal regions");

        string searchDb = SymbolSearchSidecar.SearchDbPathFor(db);
        IReadOnlyList<IndexedSymbol> symbols = SqliteSymbolReader.Read(db);
        var buildSw = Stopwatch.StartNew();
        SearchIndexWriter.Write(searchDb, symbols, revision, db, repo, RegionIndexOptions.EnabledDefault);
        buildSw.Stop();
        long regionRows = Count(searchDb, "SELECT COUNT(*) FROM search_regions;");
        Assert.True(regionRows > 0, "Miller region sidecar should populate search_regions");

        FtsRegionSearchIndex index = FtsRegionSearchIndex.Open(searchDb, revision);
        string commentOutput = SearchTool.RunRegions(
            index,
            "CommentOnlyNeedle",
            new HashSet<string> { "comment" },
            limit: 10,
            excludeTests: false,
            json: false,
            out int commentHits);
        string literalOutput = SearchTool.RunRegions(
            index,
            "LiteralOnlyNeedle",
            new HashSet<string> { "string_literal" },
            limit: 10,
            excludeTests: false,
            json: false,
            out int literalHits);

        Assert.Equal(1, commentHits);
        Assert.Contains("RegionProbe.cs", commentOutput);
        Assert.Contains("CommentOnlyNeedle", commentOutput);
        Assert.Equal(1, literalHits);
        Assert.Contains("RegionProbe.cs", literalOutput);
        Assert.Contains("LiteralOnlyNeedle", literalOutput);

        long searchDbBytes = new FileInfo(searchDb).Length;
        _output.WriteLine(
            $"region_build_ms={buildSw.Elapsed.TotalMilliseconds:F1} search_db_bytes={searchDbBytes} " +
            $"source_regions={sourceRegionCount} search_regions={regionRows}");
    }

    private static long ReadLatestRevision(string dbPath)
    {
        using var reader = new FreshnessReader(dbPath);
        return reader.LatestRevision();
    }

    private static long Count(string dbPath, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }
}
