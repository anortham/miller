using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Server.Git;
using Miller.Server.Tools;
using Miller.Tests.Indexing;
using Xunit;

namespace Miller.Tests.Server;

public sealed class RiskMetricsToolTests
{
    [Fact]
    public void RunRiskJson_JoinsChurnAndComplexityOnSymbolId()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                Row("aa11223344556677889900aabbccdd31", "RiskySymbol", "src/Risky.cs", 5),
            });
        SeedComplexity(fx.DbPath, "metric-risky", "src/Risky.cs", "aa11223344556677889900aabbccdd31", 18, 3);

        MetricsToolResult result = Run(fx, CommitTouching("src/Risky.cs", 5, "abc1234"));

        Assert.Equal(1, result.ResultCount);
        using var doc = JsonDocument.Parse(result.Output);
        JsonElement root = doc.RootElement;
        Assert.Equal("risk", root.GetProperty("operation").GetString());
        Assert.Equal(
            "commit_count * (decision_count + loop_count + max_nesting_depth)",
            root.GetProperty("score_formula").GetString());
        JsonElement row = root.GetProperty("rows")[0];
        Assert.Equal("symbol", row.GetProperty("basis").GetString());
        Assert.Equal("RiskySymbol", row.GetProperty("symbol_name").GetString());
        Assert.Equal(1, row.GetProperty("commit_count").GetInt32());
        Assert.Equal(18, row.GetProperty("decision_count").GetInt32());
        Assert.Equal(3, row.GetProperty("max_nesting_depth").GetInt32());
        // 1 commit * (18 decisions + 2 loops + 3 nesting)
        Assert.Equal(23, row.GetProperty("score").GetInt64());
        Assert.Equal("high", row.GetProperty("severity").GetString());
    }

    [Fact]
    public void RunRiskJson_FileOnlyChurnJoinsPathAggregatedComplexity()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                // Symbol exists in the index but NOT on the changed lines, so churn falls back to file_only.
                Row("aa11223344556677889900aabbccdd41", "ElsewhereSymbol", "src/Blob.cs", 50),
            });
        SeedComplexity(fx.DbPath, "metric-blob-1", "src/Blob.cs", null, 9, 2);
        SeedComplexity(fx.DbPath, "metric-blob-2", "src/Blob.cs", null, 4, 5);

        MetricsToolResult result = Run(fx, CommitTouching("src/Blob.cs", 5, "def5678"));

        Assert.Equal(1, result.ResultCount);
        using var doc = JsonDocument.Parse(result.Output);
        JsonElement row = doc.RootElement.GetProperty("rows")[0];
        Assert.Equal("file", row.GetProperty("basis").GetString());
        Assert.Equal("src/Blob.cs", row.GetProperty("path").GetString());
        // File tier sums decisions/loops and takes max nesting: decisions 9+4=13, loops 2+2=4, nesting max(2,5)=5.
        Assert.Equal(13, row.GetProperty("decision_count").GetInt32());
        Assert.Equal(4, row.GetProperty("loop_count").GetInt32());
        Assert.Equal(5, row.GetProperty("max_nesting_depth").GetInt32());
        Assert.Equal(22, row.GetProperty("score").GetInt64());
    }

    [Fact]
    public void RunRiskJson_OmitsChurnWithoutComplexityAndComplexityWithoutChurn()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                Row("aa11223344556677889900aabbccdd51", "ChurnedOnly", "src/ChurnedOnly.cs", 5),
                Row("aa11223344556677889900aabbccdd52", "ComplexOnly", "src/ComplexOnly.cs", 5),
            });
        // Complexity for the un-churned symbol only.
        SeedComplexity(fx.DbPath, "metric-complex-only", "src/ComplexOnly.cs", "aa11223344556677889900aabbccdd52", 20, 4);

        MetricsToolResult result = Run(fx, CommitTouching("src/ChurnedOnly.cs", 5, "aaa1111"));

        Assert.Equal(0, result.ResultCount);
        using var doc = JsonDocument.Parse(result.Output);
        Assert.Equal(0, doc.RootElement.GetProperty("rows").GetArrayLength());
    }

    [Fact]
    public void RunRiskJson_JoinsBeforeLimiting()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                Row("aa11223344556677889900aabbccdd61", "HotButSimple", "src/HotButSimple.cs", 5),
                Row("aa11223344556677889900aabbccdd62", "WarmButGnarly", "src/WarmButGnarly.cs", 5),
            });
        // HotButSimple: churns in 2 commits but is trivial. WarmButGnarly: churns once but is very complex.
        SeedComplexity(fx.DbPath, "metric-simple", "src/HotButSimple.cs", "aa11223344556677889900aabbccdd61", 1, 1);
        SeedComplexity(fx.DbPath, "metric-gnarly", "src/WarmButGnarly.cs", "aa11223344556677889900aabbccdd62", 30, 6);

        var history = new StubGitHistoryReader(new GitHistoryResult(
            Success: true,
            Commits:
            [
                Commit("c1", "2026-06-20T12:00:00Z", "src/HotButSimple.cs", 5),
                Commit("c2", "2026-06-21T12:00:00Z", "src/HotButSimple.cs", 5),
                Commit("c3", "2026-06-22T12:00:00Z", "src/WarmButGnarly.cs", 5),
            ],
            Error: null));

        MetricsToolResult result = Run(fx, history, limit: 1);

        // Churn ordering alone would put HotButSimple first (2 commits vs 1); risk score must win:
        // HotButSimple: 2 * (1 + 2 + 1) = 8. WarmButGnarly: 1 * (30 + 2 + 6) = 38.
        Assert.Equal(1, result.ResultCount);
        using var doc = JsonDocument.Parse(result.Output);
        JsonElement row = doc.RootElement.GetProperty("rows")[0];
        Assert.Equal("WarmButGnarly", row.GetProperty("symbol_name").GetString());
        Assert.Equal(38, row.GetProperty("score").GetInt64());
    }

    [Fact]
    public void RunRiskJson_ExcludeTestsDropsTestSymbols()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                Row("aa11223344556677889900aabbccdd71", "ProdSymbol", "src/Prod.cs", 5),
                Row("aa11223344556677889900aabbccdd72", "TestSymbol", "tests/Test.cs", 5),
            });
        Exec(fx.DbPath, "UPDATE symbols SET is_test = 1 WHERE symbol_id = 'aa11223344556677889900aabbccdd72';");
        SeedComplexity(fx.DbPath, "metric-prod", "src/Prod.cs", "aa11223344556677889900aabbccdd71", 10, 2);
        SeedComplexity(fx.DbPath, "metric-test", "tests/Test.cs", "aa11223344556677889900aabbccdd72", 10, 2);

        var history = new StubGitHistoryReader(new GitHistoryResult(
            Success: true,
            Commits:
            [
                Commit("c1", "2026-06-20T12:00:00Z", "src/Prod.cs", 5),
                Commit("c2", "2026-06-20T13:00:00Z", "tests/Test.cs", 5),
            ],
            Error: null));

        MetricsToolResult result = Run(fx, history, includeTests: false);

        Assert.Equal(1, result.ResultCount);
        using var doc = JsonDocument.Parse(result.Output);
        Assert.Equal("ProdSymbol", doc.RootElement.GetProperty("rows")[0].GetProperty("symbol_name").GetString());
    }

    [Fact]
    public void RunRiskCompact_RendersHeaderAndRows()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                Row("aa11223344556677889900aabbccdd81", "RiskySymbol", "src/Risky.cs", 5),
            });
        SeedComplexity(fx.DbPath, "metric-risky", "src/Risky.cs", "aa11223344556677889900aabbccdd81", 18, 3);

        MetricsToolResult result = Run(fx, CommitTouching("src/Risky.cs", 5, "abc1234"), json: false);

        Assert.Equal(1, result.ResultCount);
        Assert.Contains("# risk HEAD~1..HEAD", result.Output);
        Assert.Contains("RiskySymbol", result.Output);
        Assert.Contains("src/Risky.cs:5", result.Output);
    }

    private static MetricsToolResult Run(
        JulieDbFixture fx,
        IGitHistoryReader history,
        int limit = 10,
        bool json = true,
        bool includeTests = true) =>
        MetricsTool.Run(
            fx.DbPath,
            operation: "risk",
            limit: limit,
            json: json,
            minCount: 2,
            maxSymbolsPerGroup: MetricsTool.DefaultCloneSymbolsPerGroup,
            minSeverity: "moderate",
            includeTests: includeTests,
            workspaceRoot: fx.WorkspaceRoot,
            range: "HEAD~1..HEAD",
            includeCommits: false,
            historyReader: history);

    private static StubGitHistoryReader CommitTouching(string path, int line, string commit) =>
        new(new GitHistoryResult(
            Success: true,
            Commits: [Commit(commit, "2026-06-20T12:00:00Z", path, line)],
            Error: null));

    private static GitHistoryCommit Commit(string id, string timeUtc, string path, int line) =>
        new(
            Commit: id,
            AuthorTimeUtc: DateTimeOffset.Parse(timeUtc),
            Diff: $"""
                diff --git a/{path} b/{path}
                --- a/{path}
                +++ b/{path}
                @@ -{line},1 +{line},1 @@
                -old
                +new
                """);

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
        string? symbolId,
        int decisions,
        int nesting)
    {
        string symbolIdSql = symbolId is null ? "NULL" : $"'{symbolId}'";
        Exec(dbPath, $"""
            INSERT INTO complexity_metrics
                (complexity_metric_id, file_id, path, language, scope, symbol_id, algorithm_id, covered_lines,
                 covered_bytes, decision_count, loop_count, max_nesting_depth, parameter_count, start_line,
                 start_column, end_line, end_column, start_byte, end_byte)
            VALUES
                ('{metricId}', 'file:{path}', '{path}', 'csharp', 'symbol', {symbolIdSql}, 'julie-ast-complexity-v1',
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
