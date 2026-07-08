using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Server;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// End-to-end proof of the metric-history cheap arm over a REAL <c>julie-extract</c>: scan a small temp repo with
/// the pinned binary, run the converge recording arm, and assert a <c>source='converge'</c> snapshot with plausible
/// aggregate values landed in <c>history.db</c>. <c>[Trait("Category","Scale")]</c> so it is excluded from the fast
/// suite and <see cref="Assert.SkipWhen"/>s when <c>.tools/julie-extract</c> is absent.
/// </summary>
[Trait("Category", "Scale")]
public sealed class MetricHistoryConvergeScaleTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (string dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void RealExtract_ConvergeArm_WritesSnapshotWithPlausibleValues()
    {
        string binary = ScaleTestSupport.RequireJulieServer();

        string root = NewTempDir();
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, "src", "Sample.cs"),
            "namespace Demo;\npublic class Sample\n{\n" +
            "    // TODO tidy this branch later\n" +
            "    public int Classify(int n)\n    {\n" +
            "        if (n < 0) return -1;\n        if (n == 0) return 0;\n        return 1;\n    }\n" +
            "    public int Add(int a, int b) => a + b;\n}\n");

        var runner = new JulieExtractRunner(binary);
        string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(root);
        string dbPath = Path.Combine(canonicalRoot, ".miller", "symbols.db");

        ExtractReport scan = runner.Scan(canonicalRoot, dbPath, force: false);
        Assert.NotEqual("failed", scan.Status);

        long revision = scan.Revision ?? ReadRevision(dbPath);
        Assert.True(revision > 0);
        string? workspaceId = WorkspaceId.FromCanonicalRoot(canonicalRoot);

        MetricHistoryWriteResult? result = MetricSnapshotAggregates.RecordConverge(
            dbPath, workspaceId, revision, MillerVersion.Current);

        Assert.Equal(MetricHistoryWriteResult.Recorded, result);

        string historyPath = MetricSnapshotAggregates.HistoryDbPathFor(dbPath);
        Assert.Equal(1L, Scalar(historyPath, "SELECT COUNT(*) FROM snapshots;", r => r.GetInt64(0)));
        Assert.Equal("converge", Scalar(historyPath, "SELECT source FROM snapshots LIMIT 1;", r => r.GetString(0)));

        Assert.True(MetricValue(historyPath, MetricSnapshotAggregates.SymbolCount) > 0);
        Assert.True(MetricValue(historyPath, MetricSnapshotAggregates.FileCount) > 0);
        Assert.True(MetricValue(historyPath, MetricSnapshotAggregates.LanguageCount) >= 1);
        // clone_group_count is always emitted (0 is a real value); complexity is present because the fixture has a
        // branching method.
        Assert.True(MetricCount(historyPath, MetricSnapshotAggregates.CloneGroupCount) == 1);
        Assert.True(MetricCount(historyPath, MetricSnapshotAggregates.ComplexityMax) == 1);
    }

    private static long ReadRevision(string dbPath)
    {
        using var reader = new FreshnessReader(dbPath);
        return reader.LatestRevision();
    }

    private string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "miller-history-scale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static double MetricValue(string historyPath, string metric) =>
        Scalar(historyPath,
            $"SELECT value FROM snapshot_metrics WHERE metric = '{metric}';", r => r.GetDouble(0));

    private static long MetricCount(string historyPath, string metric) =>
        Scalar(historyPath,
            $"SELECT COUNT(*) FROM snapshot_metrics WHERE metric = '{metric}';", r => r.GetInt64(0));

    private static T Scalar<T>(string dbPath, string sql, Func<SqliteDataReader, T> map)
    {
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
        };
        using var connection = new SqliteConnection(csb.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using SqliteDataReader reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return map(reader);
    }
}
