using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Core.Search;
using Miller.Indexing;
using Miller.Server.Git;
using Miller.Server.Tools;
using Miller.Tests.Indexing;
using Xunit;

namespace Miller.Tests.Server;

public sealed class ReportToolTests
{
    [Fact]
    public void RunJson_ComposesIndexHealthComplexityClonesChurnAndRisk()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                Row("aa11223344556677889900aabbccee01", "RiskySymbol", "src/Risky.cs", 5),
                Row("aa11223344556677889900aabbccee02", "CopyA", "src/A.cs", 3),
                Row("aa11223344556677889900aabbccee03", "CopyB", "src/B.cs", 7),
            });
        Exec(fx.DbPath, """
            UPDATE symbols SET body_hash = 'report-clone-hash' WHERE symbol_id IN
                ('aa11223344556677889900aabbccee02', 'aa11223344556677889900aabbccee03');
            """);
        SeedComplexity(fx.DbPath, "metric-risky", "src/Risky.cs", "aa11223344556677889900aabbccee01", 18, 3);

        ReportToolResult result = ReportTool.Run(
            fx.DbPath,
            fx.WorkspaceRoot,
            range: "HEAD~1..HEAD",
            sectionLimit: 5,
            json: true,
            includeTests: true,
            historyReader: CommitTouching("src/Risky.cs", 5, "abc1234"),
            regionIndex: null);

        using var doc = JsonDocument.Parse(result.Output);
        JsonElement root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("schema_version").GetInt32());
        Assert.Equal("report", root.GetProperty("operation").GetString());

        JsonElement index = root.GetProperty("index");
        Assert.True(index.GetProperty("available").GetBoolean());
        Assert.Equal(3, index.GetProperty("symbols").GetInt64());
        Assert.Equal(3, index.GetProperty("files").GetInt64());
        Assert.Equal(1, index.GetProperty("languages").GetInt64());

        Assert.True(root.GetProperty("extraction_health").GetProperty("available").GetBoolean());

        JsonElement markers = root.GetProperty("markers");
        Assert.False(markers.GetProperty("available").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(markers.GetProperty("reason").GetString()));

        JsonElement complexity = root.GetProperty("complexity");
        Assert.True(complexity.GetProperty("available").GetBoolean());
        Assert.Equal("RiskySymbol",
            complexity.GetProperty("hotspots")[0].GetProperty("symbol_name").GetString());

        JsonElement clones = root.GetProperty("clones");
        Assert.True(clones.GetProperty("available").GetBoolean());
        Assert.Equal("report-clone-hash",
            clones.GetProperty("groups")[0].GetProperty("body_hash").GetString());

        JsonElement churn = root.GetProperty("churn");
        Assert.True(churn.GetProperty("available").GetBoolean());
        Assert.Equal(1, churn.GetProperty("rows").GetArrayLength());

        JsonElement risk = root.GetProperty("risk");
        Assert.True(risk.GetProperty("available").GetBoolean());
        Assert.Equal("RiskySymbol", risk.GetProperty("rows")[0].GetProperty("symbol_name").GetString());
        Assert.Equal(23, risk.GetProperty("rows")[0].GetProperty("score").GetInt64());

        // Consensus: no dead-code section until reference resolution earns confidence.
        Assert.False(root.TryGetProperty("dead_code", out _));
    }

    [Fact]
    public void RunJson_GitFailureMarksChurnAndRiskUnavailableWithoutFailingTheReport()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                Row("aa11223344556677889900aabbccee11", "Symbol", "src/S.cs", 5),
            });

        ReportToolResult result = ReportTool.Run(
            fx.DbPath,
            fx.WorkspaceRoot,
            range: "HEAD~1..HEAD",
            sectionLimit: 5,
            json: true,
            includeTests: true,
            historyReader: new StubGitHistoryReader(
                new GitHistoryResult(Success: false, Commits: [], Error: "not a git repository")),
            regionIndex: null);

        using var doc = JsonDocument.Parse(result.Output);
        JsonElement churn = doc.RootElement.GetProperty("churn");
        Assert.False(churn.GetProperty("available").GetBoolean());
        Assert.Contains("not a git repository", churn.GetProperty("reason").GetString());
        JsonElement risk = doc.RootElement.GetProperty("risk");
        Assert.False(risk.GetProperty("available").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("index").GetProperty("available").GetBoolean());
    }

    [Fact]
    public void RunJson_CountsMarkersPerMarkerWhenRegionIndexAvailable()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                Row("aa11223344556677889900aabbccee21", "Symbol", "src/S.cs", 5),
            });

        var regionIndex = new StubRegionSearchIndex(
            Hit("src/S.cs", 10, "TODO fix the widget"),
            Hit("src/S.cs", 20, "TODO handle nulls"),
            Hit("src/Other.cs", 4, "HACK temporary shim"));

        ReportToolResult result = ReportTool.Run(
            fx.DbPath,
            fx.WorkspaceRoot,
            range: "HEAD~1..HEAD",
            sectionLimit: 5,
            json: true,
            includeTests: true,
            historyReader: new StubGitHistoryReader(
                new GitHistoryResult(Success: false, Commits: [], Error: "no git")),
            regionIndex: regionIndex);

        using var doc = JsonDocument.Parse(result.Output);
        JsonElement markers = doc.RootElement.GetProperty("markers");
        Assert.True(markers.GetProperty("available").GetBoolean());
        var counts = markers.GetProperty("counts").EnumerateArray()
            .ToDictionary(
                item => item.GetProperty("marker").GetString()!,
                item => item.GetProperty("count").GetInt32());
        Assert.Equal(2, counts["TODO"]);
        Assert.Equal(1, counts["HACK"]);
        Assert.Equal(3, markers.GetProperty("total").GetInt32());
    }

    [Fact]
    public void RunCompact_RendersSectionsReadably()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                Row("aa11223344556677889900aabbccee31", "RiskySymbol", "src/Risky.cs", 5),
            });
        SeedComplexity(fx.DbPath, "metric-risky", "src/Risky.cs", "aa11223344556677889900aabbccee31", 18, 3);

        ReportToolResult result = ReportTool.Run(
            fx.DbPath,
            fx.WorkspaceRoot,
            range: "HEAD~1..HEAD",
            sectionLimit: 5,
            json: false,
            includeTests: true,
            historyReader: CommitTouching("src/Risky.cs", 5, "abc1234"),
            regionIndex: null);

        Assert.Contains("# miller report", result.Output);
        Assert.Contains("## index", result.Output);
        Assert.Contains("## complexity", result.Output);
        Assert.Contains("## risk", result.Output);
        Assert.Contains("RiskySymbol", result.Output);
        Assert.Contains("markers: unavailable", result.Output);
    }

    private static StubGitHistoryReader CommitTouching(string path, int line, string commit) =>
        new(new GitHistoryResult(
            Success: true,
            Commits:
            [
                new GitHistoryCommit(
                    Commit: commit,
                    AuthorTimeUtc: DateTimeOffset.Parse("2026-06-20T12:00:00Z"),
                    Diff: $"""
                        diff --git a/{path} b/{path}
                        --- a/{path}
                        +++ b/{path}
                        @@ -{line},1 +{line},1 @@
                        -old
                        +new
                        """),
            ],
            Error: null));

    private static RegionSearchHit Hit(string path, int line, string text) =>
        new(path, 1.0, line, "comment", text, text, $"region-{path}-{line}",
            ContainingSymbolId: null, ContainingSymbolName: null, Language: "csharp");

    private static JulieDbFixture.SymbolRow Row(string id, string name, string path, int line) =>
        new(id, name, "method", "csharp", path, $"void {name}()", line, null)
        {
            EndLine = line + 4,
            StartByte = line * 10,
            EndByte = line * 10 + 40,
        };

    private static void SeedComplexity(
        string dbPath,
        string metricId,
        string path,
        string symbolId,
        int decisions,
        int nesting)
    {
        Exec(dbPath, $"""
            INSERT INTO complexity_metrics
                (complexity_metric_id, file_id, path, language, scope, symbol_id, algorithm_id, covered_lines,
                 covered_bytes, decision_count, loop_count, max_nesting_depth, parameter_count, start_line,
                 start_column, end_line, end_column, start_byte, end_byte)
            VALUES
                ('{metricId}', 'file:{path}', '{path}', 'csharp', 'symbol', '{symbolId}', 'julie-ast-complexity-v1',
                 12, 120, {decisions}, 2, {nesting}, 0, 1, 0, 12, 0, 0, 120);
            """);
    }

    private static void Exec(string dbPath, string sql)
    {
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        };
        using var connection = new SqliteConnection(csb.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed class StubGitHistoryReader(GitHistoryResult result) : IGitHistoryReader
    {
        public GitHistoryResult Read(GitHistoryRequest request) => result;
    }

    private sealed class StubRegionSearchIndex(params RegionSearchHit[] hits) : IRegionSearchIndex
    {
        public int DocumentCount => hits.Length;

        public long Revision => 1;

        public IReadOnlyList<RegionSearchHit> Search(
            string query,
            IReadOnlySet<string> kinds,
            int limit = 10,
            bool excludeTests = false) =>
            hits.Where(hit => hit.RawText.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Take(limit)
                .ToArray();
    }
}
