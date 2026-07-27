using System.Globalization;
using Microsoft.Data.Sqlite;
using Miller.Core.Search;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class MetricSnapshotAggregatesTests
{
    [Fact]
    public void ReadConvergeMetrics_ReadsSymbolCloneAndComplexityAggregates()
    {
        using var fx = NewFixture();

        IReadOnlyList<MetricHistoryPoint> metrics =
            MetricSnapshotAggregates.ReadConvergeMetrics(fx.DbPath);

        Assert.Equal(4d, ValueOf(metrics, MetricSnapshotAggregates.SymbolCount));
        Assert.Equal(4d, ValueOf(metrics, MetricSnapshotAggregates.FileCount));
        Assert.Equal(1d, ValueOf(metrics, MetricSnapshotAggregates.LanguageCount));
        // Alpha + Beta share a body_hash ⟹ one clone group.
        Assert.Equal(1d, ValueOf(metrics, MetricSnapshotAggregates.CloneGroupCount));
        // decision_counts {1,5,10,20}: type-7 percentiles p50=7.5, p90=17, max=20.
        Assert.Equal(7.5d, ValueOf(metrics, MetricSnapshotAggregates.ComplexityP50));
        Assert.Equal(17d, ValueOf(metrics, MetricSnapshotAggregates.ComplexityP90));
        Assert.Equal(20d, ValueOf(metrics, MetricSnapshotAggregates.ComplexityMax));
    }

    [Fact]
    public void ReadConvergeMetrics_IncludesZeroMarkerMetricFromArtifact()
    {
        using var fx = NewFixture();

        IReadOnlyList<MetricHistoryPoint> metrics =
            MetricSnapshotAggregates.ReadConvergeMetrics(fx.DbPath);

        Assert.Equal(0d, ValueOf(metrics, MetricSnapshotAggregates.MarkerTotal));
    }

    [Fact]
    public void ReadConvergeMetrics_IncludesMarkerTotalAndBreakdown_WhenRegionIndexAvailable()
    {
        using var fx = NewFixture();
        fx.AddStructuralFact(
            "r1", null, "src/A.cs", patternId: MarkerFactReader.PatternId,
            captureName: "marker", nodeKind: "comment", metadataJson: """{"marker":"TODO"}""");
        fx.AddStructuralFact(
            "r2", null, "src/B.cs", patternId: MarkerFactReader.PatternId,
            captureName: "marker", nodeKind: "comment", metadataJson: """{"marker":"FIXME"}""");
        fx.AddStructuralFact(
            "r3", null, "src/C.cs", patternId: MarkerFactReader.PatternId,
            captureName: "marker", nodeKind: "comment", metadataJson: """{"marker":"TODO"}""");
        fx.AddStructuralFact(
            "r4", null, "src/D.cs", patternId: MarkerFactReader.PatternId,
            captureName: "marker", nodeKind: "comment", metadataJson: """{"marker":"FIXME"}""");

        IReadOnlyList<MetricHistoryPoint> metrics =
            MetricSnapshotAggregates.ReadConvergeMetrics(fx.DbPath);

        MetricHistoryPoint marker = Assert.Single(
            metrics, m => m.Metric == MetricSnapshotAggregates.MarkerTotal);
        Assert.Equal(4d, marker.Value);
        Assert.Equal("{\"TODO\":2,\"FIXME\":2,\"HACK\":0,\"XXX\":0}", marker.DetailJson);
    }

    [Fact]
    public void ReadConvergeMetrics_MarkerCountsAreExactAboveSearchLimit()
    {
        using var fx = NewFixture();
        for (int i = 0; i <= 500; i++)
        {
            fx.AddStructuralFact(
                $"marker-{i:D3}",
                null,
                $"src/{i:D3}.cs",
                patternId: MarkerFactReader.PatternId,
                captureName: "marker",
                nodeKind: "comment",
                metadataJson: """{"marker":"TODO"}""");
        }
        fx.AddStructuralFact(
            "marker-unknown",
            null,
            "src/unknown.cs",
            patternId: MarkerFactReader.PatternId,
            captureName: "marker",
            nodeKind: "comment",
            metadataJson: """{"marker":"NOTE"}""");

        IReadOnlyList<MetricHistoryPoint> metrics =
            MetricSnapshotAggregates.ReadConvergeMetrics(fx.DbPath);

        MetricHistoryPoint marker = Assert.Single(
            metrics, m => m.Metric == MetricSnapshotAggregates.MarkerTotal);
        Assert.Equal(501d, marker.Value);
        Assert.Equal("{\"TODO\":501,\"FIXME\":0,\"HACK\":0,\"XXX\":0}", marker.DetailJson);
    }

    [Fact]
    public void ReadConvergeMetrics_MarkerTotalIsZero_WhenArtifactHasNoMarkers()
    {
        using var fx = NewFixture();

        IReadOnlyList<MetricHistoryPoint> metrics =
            MetricSnapshotAggregates.ReadConvergeMetrics(fx.DbPath);

        MetricHistoryPoint marker = Assert.Single(
            metrics, m => m.Metric == MetricSnapshotAggregates.MarkerTotal);
        Assert.Equal(0d, marker.Value);
    }

    [Fact]
    public void ReadConvergeMetrics_OmitsComplexity_WhenNoComplexityRows()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract,
            new[] { Row("id-solo", "Solo", "src/Solo.cs") });

        IReadOnlyList<MetricHistoryPoint> metrics =
            MetricSnapshotAggregates.ReadConvergeMetrics(fx.DbPath);

        // Complexity facts empty ⟹ absent rows, never 0 (absent-vs-zero rule).
        Assert.DoesNotContain(metrics, m => m.Metric == MetricSnapshotAggregates.ComplexityP50);
        Assert.DoesNotContain(metrics, m => m.Metric == MetricSnapshotAggregates.ComplexityP90);
        Assert.DoesNotContain(metrics, m => m.Metric == MetricSnapshotAggregates.ComplexityMax);
        // But the always-available counts are still present.
        Assert.Equal(1d, ValueOf(metrics, MetricSnapshotAggregates.SymbolCount));
        Assert.Equal(0d, ValueOf(metrics, MetricSnapshotAggregates.CloneGroupCount));
    }

    [Fact]
    public void RecordConverge_WritesOneConvergeSnapshotWithMetrics()
    {
        using var fx = NewFixture();

        MetricHistoryWriteResult? result = MetricSnapshotAggregates.RecordConverge(
            fx.DbPath, workspaceId: "ws-1", revision: 5, millerVersion: "miller-test");

        Assert.Equal(MetricHistoryWriteResult.Recorded, result);

        string historyPath = MetricSnapshotAggregates.HistoryDbPathFor(fx.DbPath);
        Assert.Equal(1, SnapshotCount(historyPath));
        (string source, string workspaceId, long revision, string artifactId) = SnapshotIdentity(historyPath);
        Assert.Equal("converge", source);
        Assert.Equal("ws-1", workspaceId);
        Assert.Equal(5, revision);
        Assert.Equal("artifact-default", artifactId);
        Assert.Equal(4d, MetricValue(historyPath, MetricSnapshotAggregates.SymbolCount));
    }

    [Fact]
    public void RecordConverge_SameRevision_RecordsNothingNew()
    {
        using var fx = NewFixture();
        string historyPath = MetricSnapshotAggregates.HistoryDbPathFor(fx.DbPath);

        Assert.Equal(MetricHistoryWriteResult.Recorded,
            MetricSnapshotAggregates.RecordConverge(fx.DbPath, "ws-1", 5, "miller-test"));
        Assert.Equal(MetricHistoryWriteResult.SkippedDuplicate,
            MetricSnapshotAggregates.RecordConverge(fx.DbPath, "ws-1", 5, "miller-test"));

        Assert.Equal(1, SnapshotCount(historyPath));
    }

    [Fact]
    public void RecordConverge_SkipsWithoutThrowing_OnMissingIdentityOrArguments()
    {
        using var fx = NewFixture();

        Assert.Null(MetricSnapshotAggregates.RecordConverge(fx.DbPath, workspaceId: null, revision: 5, "v"));
        Assert.Null(MetricSnapshotAggregates.RecordConverge(fx.DbPath, "ws-1", revision: 0, "v"));
        Assert.Null(MetricSnapshotAggregates.RecordConverge("   ", "ws-1", 5, "v"));

        Assert.False(File.Exists(MetricSnapshotAggregates.HistoryDbPathFor(fx.DbPath)));
    }

    [Fact]
    public void RecordConverge_NeverThrows_AndReportsError_OnUnreadableDb()
    {
        string dir = Path.Combine(Path.GetTempPath(), "miller-agg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string missing = Path.Combine(dir, "symbols.db"); // never created ⟹ read throws
            Exception? seen = null;

            MetricHistoryWriteResult? result = MetricSnapshotAggregates.RecordConverge(
                missing, "ws-1", 5, "v", onError: ex => seen = ex);

            Assert.Null(result);
            Assert.NotNull(seen);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- helpers ---------------------------------------------------------------------------------------------

    private static JulieDbFixture NewFixture()
    {
        var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                Row("id-alpha", "Alpha", "src/A.cs"),
                Row("id-beta", "Beta", "src/B.cs"),
                Row("id-gamma", "Gamma", "src/C.cs"),
                Row("id-delta", "Delta", "src/D.cs"),
            });

        Exec(fx.DbPath, """
            UPDATE symbols SET body_hash = 'shared-body' WHERE symbol_id IN ('id-alpha', 'id-beta');
            UPDATE symbols SET body_hash = 'body-gamma' WHERE symbol_id = 'id-gamma';
            UPDATE symbols SET body_hash = 'body-delta' WHERE symbol_id = 'id-delta';
            """);
        SeedComplexity(fx.DbPath, "cm-alpha", "src/A.cs", "id-alpha", 1);
        SeedComplexity(fx.DbPath, "cm-beta", "src/B.cs", "id-beta", 5);
        SeedComplexity(fx.DbPath, "cm-gamma", "src/C.cs", "id-gamma", 10);
        SeedComplexity(fx.DbPath, "cm-delta", "src/D.cs", "id-delta", 20);
        return fx;
    }

    private static JulieDbFixture.SymbolRow Row(string id, string name, string path) =>
        new(id, name, "method", "csharp", path, $"void {name}()", 3, null)
        {
            EndLine = 7,
            StartByte = 30,
            EndByte = 70,
        };

    private static double ValueOf(IReadOnlyList<MetricHistoryPoint> metrics, string metric) =>
        Assert.Single(metrics, m => m.Metric == metric).Value;

    private static RegionSearchHit Hit(string regionId, string rawText) =>
        new("src/Marked.cs", 1.0, 3, "comment", rawText, rawText, regionId, null, null, "csharp");

    private sealed class FakeRegionIndex : IRegionSearchIndex
    {
        private readonly Dictionary<string, List<RegionSearchHit>> _byMarker = new(StringComparer.Ordinal);

        public int DocumentCount => 0;
        public long Revision => 1;

        public void Add(string marker, RegionSearchHit hit)
        {
            if (!_byMarker.TryGetValue(marker, out List<RegionSearchHit>? list))
            {
                list = new List<RegionSearchHit>();
                _byMarker[marker] = list;
            }
            list.Add(hit);
        }

        public IReadOnlyList<RegionSearchHit> Search(
            string query, IReadOnlySet<string> kinds, int limit = 10, bool excludeTests = false) =>
            _byMarker.TryGetValue(query, out List<RegionSearchHit>? list)
                ? list
                : Array.Empty<RegionSearchHit>();
    }

    private static void SeedComplexity(string dbPath, string metricId, string path, string symbolId, int decisions) =>
        Exec(dbPath, $"""
            INSERT INTO complexity_metrics
                (complexity_metric_id, file_id, path, language, scope, symbol_id, algorithm_id, covered_lines,
                 covered_bytes, decision_count, loop_count, max_nesting_depth, parameter_count, start_line,
                 start_column, end_line, end_column, start_byte, end_byte)
            VALUES
                ('{metricId}', 'file:{path}', '{path}', 'csharp', 'symbol', '{symbolId}', 'julie-ast-complexity-v1',
                 10, 100, {decisions}, 1, 1, 0, 1, 0, 10, 0, 0, 100);
            """);

    private static int SnapshotCount(string historyPath) =>
        (int)Query(historyPath, "SELECT COUNT(*) FROM snapshots;", r => r.GetInt64(0));

    private static (string Source, string WorkspaceId, long Revision, string ArtifactId) SnapshotIdentity(
        string historyPath) =>
        Query(historyPath,
            "SELECT source, workspace_id, revision, artifact_id FROM snapshots LIMIT 1;",
            r => (r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3)));

    private static double MetricValue(string historyPath, string metric) =>
        Query(historyPath,
            $"SELECT value FROM snapshot_metrics WHERE metric = '{metric}' LIMIT 1;",
            r => r.GetDouble(0));

    private static T Query<T>(string dbPath, string sql, Func<SqliteDataReader, T> map)
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

    private static void Exec(string dbPath, string sql)
    {
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
        };
        using var connection = new SqliteConnection(csb.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
