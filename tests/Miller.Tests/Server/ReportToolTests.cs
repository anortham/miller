using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Core.Search;
using Miller.Indexing;
using Miller.Server;
using Miller.Server.Cli;
using Miller.Server.Git;
using Miller.Server.Tools;
using Miller.Tests.Indexing;
using Xunit;

namespace Miller.Tests.Server;

public sealed class ReportToolTests
{
    // ---- heavy-arm metric-history: report fact-surfacing + CLI recording end-to-end (Task 3) ----------------

    [Fact]
    public void Run_SurfacesIndexAndGitSnapshotMetrics_ButNotBoundedCloneOrMarkerCounts()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                Row("aa11223344556677889900aabbcd0001", "RiskySymbol", "src/Risky.cs", 5),
                Row("aa11223344556677889900aabbcd0002", "CopyA", "src/A.cs", 3),
                Row("aa11223344556677889900aabbcd0003", "CopyB", "src/B.cs", 7),
            });
        Exec(fx.DbPath, """
            UPDATE symbols SET body_hash = 'report-clone-hash' WHERE symbol_id IN
                ('aa11223344556677889900aabbcd0002', 'aa11223344556677889900aabbcd0003');
            """);
        SeedComplexity(fx.DbPath, "metric-risky", "src/Risky.cs", "aa11223344556677889900aabbcd0001", 18, 3);

        ReportToolResult result = ReportTool.Run(
            fx.DbPath, fx.WorkspaceRoot, range: "HEAD~20..HEAD", sectionLimit: 10, json: true,
            includeTests: true, historyReader: CommitTouching("src/Risky.cs", 5, "abc1234"),
            regionIndex: new StubRegionSearchIndex(Hit("src/S.cs", 10, "TODO fix the widget")));

        var byName = result.SnapshotMetrics.ToDictionary(p => p.Metric, p => p);
        Assert.Equal(3.0, byName["symbol_count"].Value);
        Assert.Equal(3.0, byName["file_count"].Value);
        Assert.Equal(1.0, byName["language_count"].Value);
        // clone_group_count and marker_total are NOT recorded by the report arm: the report's clone list is bounded
        // to SectionLimit and its marker set is capped at a FINAL 500 (vs converge's per-marker cap), so the leader
        // converge arm owns both exact series under the same metric names.
        Assert.DoesNotContain("clone_group_count", byName.Keys);
        Assert.DoesNotContain("marker_total", byName.Keys);
        Assert.Equal(1.0, byName["churn_files_changed"].Value);
        Assert.Equal(23.0, byName["risk_top_score"].Value);
    }

    [Fact]
    public void Run_ChurnFilesChangedIsExact_NotBoundedBySectionLimit()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[] { Row("aa11223344556677889900aabbcd0021", "ChangedSymbol", "src/A.cs", 5) });

        // Three distinct changed paths with sectionLimit: 1 ⟹ display rows truncate to one, but the recorded
        // churn_files_changed must be the EXACT pre-truncation count (3) — the same value `metrics churn` records
        // for this range regardless of either command's row limit (ReadTrend flattens by metric name).
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
                        diff --git a/src/B.cs b/src/B.cs
                        --- a/src/B.cs
                        +++ b/src/B.cs
                        @@ -2,1 +2,1 @@
                        -old
                        +new
                        diff --git a/src/C.cs b/src/C.cs
                        --- a/src/C.cs
                        +++ b/src/C.cs
                        @@ -9,1 +9,1 @@
                        -old
                        +new
                        """),
            ],
            Error: null));

        ReportToolResult result = ReportTool.Run(
            fx.DbPath, fx.WorkspaceRoot, range: "HEAD~20..HEAD", sectionLimit: 1, json: true,
            includeTests: true, historyReader: history, regionIndex: null);

        var byName = result.SnapshotMetrics.ToDictionary(p => p.Metric, p => p);
        Assert.Equal(3.0, byName["churn_files_changed"].Value);
    }

    [Fact]
    public void Run_GitAndMarkersUnavailable_SnapshotMetricsHoldOnlyIndexCounts()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[] { Row("aa11223344556677889900aabbcd0011", "Symbol", "src/S.cs", 5) });

        ReportToolResult result = ReportTool.Run(
            fx.DbPath, fx.WorkspaceRoot, range: "HEAD~20..HEAD", sectionLimit: 10, json: true,
            includeTests: true,
            historyReader: new StubGitHistoryReader(new GitHistoryResult(false, [], "no git")),
            regionIndex: null);

        var names = result.SnapshotMetrics.Select(p => p.Metric).ToHashSet();
        // Only the exact index scalars survive: no git ⟹ no churn/risk rows; no region index ⟹ no marker_total;
        // clone_group_count is converge-owned (exact) and never recorded by the report arm.
        Assert.Contains("symbol_count", names);
        Assert.Contains("file_count", names);
        Assert.Contains("language_count", names);
        Assert.DoesNotContain("clone_group_count", names);
        Assert.DoesNotContain("marker_total", names);
        Assert.DoesNotContain("churn_files_changed", names);
        Assert.DoesNotContain("risk_top_score", names);
    }

    [Fact]
    public void Cli_Report_DefaultParams_WritesReportSnapshot()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[] { Row("aa11223344556677889900aabbcd0021", "Symbol", "src/S.cs", 5) },
            revisions: new[] { new JulieDbFixture.RevisionRow(1) });

        var (code, _, errText) = RunCli(new[] { "report" }, Context(fx));

        Assert.Equal(0, code);
        var rows = ReadHistoryMetrics(fx);
        // Git sections are unavailable (temp dir is not a git repo), but the index scalars are recorded.
        Assert.Contains(rows, r => r.Source == "report" && r.Metric == "symbol_count" && r.Value == 1.0);
        Assert.Contains(rows, r => r.Source == "report" && r.Metric == "file_count");
        Assert.Contains(rows, r => r.Source == "report" && r.Metric == "language_count");
        Assert.DoesNotContain("metric history", errText); // no warning line on a clean write
    }

    [Fact]
    public void Cli_Report_NonDefaultRange_SkipsRecording()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[] { Row("aa11223344556677889900aabbcd0031", "Symbol", "src/S.cs", 5) },
            revisions: new[] { new JulieDbFixture.RevisionRow(1) });

        var (code, _, _) = RunCli(new[] { "report", "--range", "HEAD~5..HEAD" }, Context(fx));

        Assert.Equal(0, code);
        Assert.False(File.Exists(MetricSnapshotAggregates.HistoryDbPathFor(fx.DbPath)));
    }

    [Fact]
    public void Cli_Candidates_DefaultParams_WritesCandidatesSnapshot()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                new JulieDbFixture.SymbolRow(
                    "sym-cand", "UnusedHelper", "method", "csharp", "src/Helper.cs", "sig UnusedHelper", 1, null)
                {
                    Visibility = "private", StartByte = 0, EndByte = 40,
                },
            },
            identifiers: new[]
            {
                new JulieDbFixture.IdentifierRow("id-benign", "SomethingElse", "call", "csharp", "src/Other.cs", 1, null)
                {
                    StartByte = 100, EndByte = 110,
                },
            },
            revisions: new[] { new JulieDbFixture.RevisionRow(1) });

        var (code, outText, errText) = RunCli(new[] { "references", "candidates" }, Context(fx));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("candidates:", outText);
        var rows = ReadHistoryMetrics(fx);
        Assert.Contains(rows, r => r.Source == "candidates" && r.Metric == "dead_code_candidate_count");
        var suppressed = Assert.Single(rows, r => r.Metric == "dead_code_suppressed_total");
        Assert.Equal("candidates", suppressed.Source);
        Assert.Contains("public_api", suppressed.Detail!); // per-rule suppressed breakdown in detail_json
    }

    [Fact]
    public void Cli_Candidates_HistoryWriteFailure_LeavesOutputAndExitCodeUnchangedAndWarns()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                new JulieDbFixture.SymbolRow(
                    "sym-cand", "UnusedHelper", "method", "csharp", "src/Helper.cs", "sig UnusedHelper", 1, null)
                {
                    Visibility = "private", StartByte = 0, EndByte = 40,
                },
            },
            identifiers: new[]
            {
                new JulieDbFixture.IdentifierRow("id-benign", "SomethingElse", "call", "csharp", "src/Other.cs", 1, null)
                {
                    StartByte = 100, EndByte = 110,
                },
            },
            revisions: new[] { new JulieDbFixture.RevisionRow(1) });

        // Make the history sidecar path unwritable: a DIRECTORY where history.db must be a file ⟹ the append throws,
        // which the recorder swallows to a warning without touching the command's stdout or exit code.
        Directory.CreateDirectory(MetricSnapshotAggregates.HistoryDbPathFor(fx.DbPath));

        var (code, outText, errText) = RunCli(new[] { "references", "candidates" }, Context(fx));

        Assert.Equal(0, code);
        Assert.Contains("candidates:", outText);
        Assert.Contains("metric history", errText);
    }

    private static (int Code, string Out, string Err) RunCli(IReadOnlyList<string> args, WorkspaceContext ctx)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = CliDispatch.Run(args, ctx, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
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
