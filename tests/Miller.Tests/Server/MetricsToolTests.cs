using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Server.Git;
using Miller.Server.Tools;
using Miller.Tests.Indexing;
using Xunit;

namespace Miller.Tests.Server;

public sealed class MetricsToolTests
{
    [Fact]
    public void RunClonesJson_ReturnsCloneGroups()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                Row("aa11223344556677889900aabbccdd01", "CopyA", "src/A.cs", 3),
                Row("aa11223344556677889900aabbccdd02", "CopyB", "src/B.cs", 7),
            });
        Exec(fx.DbPath, """
            UPDATE symbols SET body_hash = 'clone-hash' WHERE symbol_id IN
                ('aa11223344556677889900aabbccdd01', 'aa11223344556677889900aabbccdd02');
            """);

        MetricsToolResult result = MetricsTool.Run(
            fx.DbPath,
            operation: "clones",
            limit: 10,
            json: true,
            minCount: 2,
            maxSymbolsPerGroup: MetricsTool.DefaultCloneSymbolsPerGroup,
            minSeverity: "moderate",
            includeTests: true);

        Assert.Equal(1, result.ResultCount);
        using var doc = JsonDocument.Parse(result.Output);
        JsonElement root = doc.RootElement;
        Assert.Equal("clones", root.GetProperty("operation").GetString());
        Assert.Equal("clone-hash", root.GetProperty("groups")[0].GetProperty("body_hash").GetString());
    }

    [Fact]
    public void RunComplexityCompact_RendersHotspotsWithoutCleanupAdvice()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                Row("aa11223344556677889900aabbccdd11", "HotPath", "src/HotPath.cs", 5),
            });
        SeedComplexity(fx.DbPath, "metric-hot", "src/HotPath.cs", "aa11223344556677889900aabbccdd11", 18, 2);

        MetricsToolResult result = MetricsTool.Run(
            fx.DbPath,
            operation: "complexity",
            limit: 10,
            json: false,
            minCount: 2,
            maxSymbolsPerGroup: MetricsTool.DefaultCloneSymbolsPerGroup,
            minSeverity: "moderate",
            includeTests: true);

        Assert.Equal(1, result.ResultCount);
        Assert.Contains("# complexity hotspots", result.Output);
        Assert.Contains("high", result.Output);
        Assert.Contains("HotPath", result.Output);
        Assert.DoesNotContain("cleanup", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RunChurnJson_MapsChangedHunksToCurrentSymbolsAndFileFallbacks()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                Row("aa11223344556677889900aabbccdd21", "ChangedSymbol", "src/A.cs", 5),
            });
        var history = new StubGitHistoryReader(new GitHistoryResult(
            Success: true,
            Commits:
            [
                new GitHistoryCommit(
                    Commit: "abc1234",
                    AuthorTimeUtc: DateTimeOffset.Parse("2026-06-20T12:00:00Z"),
                    Diff: """
                        diff --git a/src/A.cs b/src/A.cs
                        --- a/src/A.cs
                        +++ b/src/A.cs
                        @@ -5,1 +5,1 @@
                        -old
                        +new
                        diff --git a/src/Missing.cs b/src/Missing.cs
                        --- a/src/Missing.cs
                        +++ b/src/Missing.cs
                        @@ -2,1 +2,1 @@
                        -old
                        +new
                        """),
            ],
            Error: null));

        MetricsToolResult result = MetricsTool.Run(
            fx.DbPath,
            operation: "churn",
            limit: 10,
            json: true,
            minCount: 2,
            maxSymbolsPerGroup: MetricsTool.DefaultCloneSymbolsPerGroup,
            minSeverity: "moderate",
            includeTests: true,
            workspaceRoot: fx.WorkspaceRoot,
            range: "HEAD~1..HEAD",
            includeCommits: true,
            historyReader: history);

        Assert.Equal(2, result.ResultCount);
        using var doc = JsonDocument.Parse(result.Output);
        JsonElement rows = doc.RootElement.GetProperty("rows");
        JsonElement symbolRow = rows.EnumerateArray().Single(row => row.GetProperty("mapping_basis").GetString() == "current_index");
        Assert.Equal("ChangedSymbol", symbolRow.GetProperty("symbol_name").GetString());
        Assert.Equal(1, symbolRow.GetProperty("commit_count").GetInt32());
        Assert.Equal("abc1234", symbolRow.GetProperty("commits")[0].GetString());

        JsonElement fileRow = rows.EnumerateArray().Single(row => row.GetProperty("mapping_basis").GetString() == "file_only");
        Assert.Equal("src/Missing.cs", fileRow.GetProperty("path").GetString());
    }

    [Fact]
    public void RunClonesJson_BoundsSymbolsPerGroupAndReportsTruncation()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                Row("aa11223344556677889900aabbccde01", "CopyA", "src/A.cs", 3),
                Row("aa11223344556677889900aabbccde02", "CopyB", "src/B.cs", 7),
                Row("aa11223344556677889900aabbccde03", "CopyC", "src/C.cs", 11),
            });
        Exec(fx.DbPath, """
            UPDATE symbols SET body_hash = 'wide-clone-hash' WHERE symbol_id IN
                ('aa11223344556677889900aabbccde01',
                 'aa11223344556677889900aabbccde02',
                 'aa11223344556677889900aabbccde03');
            """);

        MetricsToolResult result = MetricsTool.Run(
            fx.DbPath,
            operation: "clones",
            limit: 10,
            json: true,
            minCount: 2,
            maxSymbolsPerGroup: 2,
            minSeverity: "moderate",
            includeTests: true);

        using var doc = JsonDocument.Parse(result.Output);
        JsonElement group = doc.RootElement.GetProperty("groups")[0];
        Assert.Equal(3, group.GetProperty("count").GetInt32());
        Assert.Equal(2, group.GetProperty("symbol_limit").GetInt32());
        Assert.True(group.GetProperty("symbols_truncated").GetBoolean());
        Assert.Equal(2, group.GetProperty("symbols").GetArrayLength());
    }

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
}
