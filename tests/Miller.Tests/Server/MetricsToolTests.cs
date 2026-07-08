using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Server;
using Miller.Server.Cli;
using Miller.Server.Git;
using Miller.Server.Tools;
using Miller.Tests.Indexing;
using Xunit;

namespace Miller.Tests.Server;

public sealed class MetricsToolTests
{
    // ---- heavy-arm metric-history: churn fact-surfacing + CLI recorder wiring (Task 3) ----------------------

    [Fact]
    public void RunChurn_SurfacesChurnFilesChangedSnapshotMetric()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[] { Row("aa11223344556677889900aabbccf001", "ChangedSymbol", "src/A.cs", 5) });

        // Two distinct changed paths (src/A.cs maps to a symbol, src/Missing.cs is file-only) ⟹ 2 files changed.
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
            fx.DbPath, operation: "churn", limit: 50, json: true, minCount: 2,
            maxSymbolsPerGroup: MetricsTool.DefaultCloneSymbolsPerGroup, minSeverity: "moderate",
            includeTests: true, workspaceRoot: fx.WorkspaceRoot, range: "HEAD~20..HEAD",
            includeCommits: false, historyReader: history);

        MetricHistoryPoint point = Assert.Single(result.SnapshotMetrics!);
        Assert.Equal("churn_files_changed", point.Metric);
        Assert.Equal(2.0, point.Value);
        Assert.Contains("\"range\":\"HEAD~20..HEAD\"", point.DetailJson!);
        Assert.Contains("\"limit\":50", point.DetailJson!);
    }

    [Fact]
    public void RunClones_LeavesSnapshotMetricsNull()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[] { Row("aa11223344556677889900aabbccf011", "Solo", "src/A.cs", 3) });

        MetricsToolResult result = MetricsTool.Run(
            fx.DbPath, operation: "clones", limit: 10, json: true, minCount: 2,
            maxSymbolsPerGroup: MetricsTool.DefaultCloneSymbolsPerGroup, minSeverity: "moderate", includeTests: true);

        Assert.Null(result.SnapshotMetrics);
    }

    [Fact]
    public void RecordHeavyArmSnapshot_Churn_WritesChurnSnapshotToHistoryDb()
    {
        using var fx = ChurnRecordingFixture();
        MetricsToolResult result = RunChurnOneFile(fx);
        WorkspaceContext ctx = Context(fx);

        CliDispatch.HeavyArmIdentity? identity = CliDispatch.CaptureHeavyArmIdentity(ctx);
        Assert.NotNull(identity);

        var warn = new StringWriter();
        MetricHistoryWriteResult? outcome = CliDispatch.RecordHeavyArmSnapshot(
            ctx, identity, "churn", result.SnapshotMetrics!, canonical: true, warn);

        Assert.Equal(MetricHistoryWriteResult.Recorded, outcome);
        Assert.Empty(warn.ToString());
        var rows = ReadHistoryMetrics(fx);
        Assert.Contains(rows, r => r.Source == "churn" && r.Metric == "churn_files_changed" && r.Value == 1.0);
    }

    [Fact]
    public void RecordHeavyArmSnapshot_NonCanonicalRun_SkipsRecording()
    {
        using var fx = ChurnRecordingFixture();
        MetricsToolResult result = RunChurnOneFile(fx);
        WorkspaceContext ctx = Context(fx);
        CliDispatch.HeavyArmIdentity? identity = CliDispatch.CaptureHeavyArmIdentity(ctx);

        var warn = new StringWriter();
        MetricHistoryWriteResult? outcome = CliDispatch.RecordHeavyArmSnapshot(
            ctx, identity, "churn", result.SnapshotMetrics!, canonical: false, warn);

        Assert.Null(outcome);
        Assert.Empty(warn.ToString());
        Assert.False(File.Exists(MetricSnapshotAggregates.HistoryDbPathFor(fx.DbPath)));
    }

    private static JulieDbFixture ChurnRecordingFixture() =>
        JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[] { Row("aa11223344556677889900aabbccf021", "ChangedSymbol", "src/A.cs", 5) },
            revisions: new[] { new JulieDbFixture.RevisionRow(1) });

    private static MetricsToolResult RunChurnOneFile(JulieDbFixture fx)
    {
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
                        """),
            ],
            Error: null));
        return MetricsTool.Run(
            fx.DbPath, operation: "churn", limit: 50, json: true, minCount: 2,
            maxSymbolsPerGroup: MetricsTool.DefaultCloneSymbolsPerGroup, minSeverity: "moderate",
            includeTests: true, workspaceRoot: fx.WorkspaceRoot, range: "HEAD~20..HEAD",
            includeCommits: false, historyReader: history);
    }

    private static WorkspaceContext Context(JulieDbFixture fx) =>
        new(
            WorkspaceRoot: fx.WorkspaceRoot,
            ExtractDbPath: fx.DbPath,
            TelemetryDbPath: Path.Combine(fx.Directory, "telemetry.db"),
            RegistryDbPath: Path.Combine(fx.Directory, "workspaces.db"),
            ToolsRoot: Path.Combine(fx.Directory, ".tools"),
            WorkspaceId: "ws-test");

    private static List<(string Source, string Metric, double Value, string? Detail)> ReadHistoryMetrics(
        JulieDbFixture fx)
    {
        var rows = new List<(string, string, double, string?)>();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = MetricSnapshotAggregates.HistoryDbPathFor(fx.DbPath),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT s.source, m.metric, m.value, m.detail_json FROM snapshot_metrics m " +
            "JOIN snapshots s ON s.snapshot_id = m.snapshot_id ORDER BY s.source, m.metric;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetDouble(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        return rows;
    }

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
