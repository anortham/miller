using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Server.Tools;
using Miller.Tests.Support;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins the level-awareness of the two metric producers that would otherwise fabricate a zero against a
/// symbols-level artifact: the converge arm's append into the APPEND-ONLY <c>history.db</c>, and the composed
/// <c>miller report</c>. Only facts-derived counters may be affected — complexity, clones and the index counts read
/// tables a symbols-level scan populates, so they must keep being recorded and reported normally.
/// </summary>
public sealed class MetricSnapshotLevelTests : IDisposable
{
    private readonly string _dir;

    public MetricSnapshotLevelTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-metric-level-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void ReadConvergeMetrics_OmitsMarkerTotal_ForSymbolsLevelArtifact()
    {
        IReadOnlyList<MetricHistoryPoint> metrics =
            MetricSnapshotAggregates.ReadConvergeMetrics(SymbolsLevel());

        Assert.DoesNotContain(metrics, m => m.Metric == MetricSnapshotAggregates.MarkerTotal);
    }

    [Fact]
    public void ReadConvergeMetrics_KeepsPopulatedTableMetrics_ForSymbolsLevelArtifact()
    {
        IReadOnlyList<MetricHistoryPoint> metrics =
            MetricSnapshotAggregates.ReadConvergeMetrics(SymbolsLevel());

        Assert.Equal(3d, ValueOf(metrics, MetricSnapshotAggregates.SymbolCount));
        Assert.Equal(2d, ValueOf(metrics, MetricSnapshotAggregates.FileCount));
        Assert.Equal(1d, ValueOf(metrics, MetricSnapshotAggregates.LanguageCount));
        Assert.Equal(0d, ValueOf(metrics, MetricSnapshotAggregates.CloneGroupCount));
        Assert.Equal(1d, ValueOf(metrics, MetricSnapshotAggregates.ComplexityP50));
        Assert.Equal(1d, ValueOf(metrics, MetricSnapshotAggregates.ComplexityP90));
        Assert.Equal(1d, ValueOf(metrics, MetricSnapshotAggregates.ComplexityMax));
    }

    [Fact]
    public void ReadConvergeMetrics_RecordsGenuineZeroMarkerTotal_ForFullLevelArtifact()
    {
        IReadOnlyList<MetricHistoryPoint> metrics =
            MetricSnapshotAggregates.ReadConvergeMetrics(FullLevel());

        MetricHistoryPoint marker = Assert.Single(
            metrics, m => m.Metric == MetricSnapshotAggregates.MarkerTotal);
        Assert.Equal(0d, marker.Value);
        Assert.Equal("{\"TODO\":0,\"FIXME\":0,\"HACK\":0,\"XXX\":0}", marker.DetailJson);
    }

    [Fact]
    public void RecordConverge_AppendsNoMarkerMetricRow_ForSymbolsLevelArtifact()
    {
        string dbPath = SymbolsLevel();

        MetricHistoryWriteResult? result = MetricSnapshotAggregates.RecordConverge(
            dbPath, workspaceId: "ws-symbols", revision: 1, millerVersion: "0.0.0-test");

        Assert.Equal(MetricHistoryWriteResult.Recorded, result);
        IReadOnlyList<string> recorded = RecordedMetricNames(dbPath);
        Assert.DoesNotContain(MetricSnapshotAggregates.MarkerTotal, recorded);
        Assert.Contains(MetricSnapshotAggregates.SymbolCount, recorded);
        Assert.Contains(MetricSnapshotAggregates.CloneGroupCount, recorded);
        Assert.Contains(MetricSnapshotAggregates.ComplexityP90, recorded);
    }

    [Fact]
    public void RecordConverge_AppendsMarkerMetricRow_ForFullLevelArtifact()
    {
        string dbPath = FullLevel();

        MetricHistoryWriteResult? result = MetricSnapshotAggregates.RecordConverge(
            dbPath, workspaceId: "ws-full", revision: 1, millerVersion: "0.0.0-test");

        Assert.Equal(MetricHistoryWriteResult.Recorded, result);
        Assert.Contains(MetricSnapshotAggregates.MarkerTotal, RecordedMetricNames(dbPath));
    }

    [Fact]
    public void Report_RendersMarkersAsUnavailablePendingUpgrade_ForSymbolsLevelArtifact()
    {
        string output = RunReport(SymbolsLevel(), json: false);

        Assert.Contains("markers: unavailable", output, StringComparison.Ordinal);
        Assert.Contains("symbols-level", output, StringComparison.Ordinal);
        Assert.Contains("miller workspace status", output, StringComparison.Ordinal);
        Assert.Contains("miller workspace full", output, StringComparison.Ordinal);
        Assert.DoesNotContain("TODO 0", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportJson_MarksMarkersUnavailableWithNoCounts_ForSymbolsLevelArtifact()
    {
        using JsonDocument doc = JsonDocument.Parse(RunReport(SymbolsLevel(), json: true));
        JsonElement markers = doc.RootElement.GetProperty("markers");

        Assert.False(markers.GetProperty("available").GetBoolean());
        Assert.Contains("symbols-level", markers.GetProperty("reason").GetString()!, StringComparison.Ordinal);
        Assert.False(markers.TryGetProperty("counts", out _));
        Assert.False(markers.TryGetProperty("total", out _));
    }

    [Fact]
    public void ReportJson_KeepsComplexityAndCloneSectionsAvailable_ForSymbolsLevelArtifact()
    {
        using JsonDocument doc = JsonDocument.Parse(RunReport(SymbolsLevel(), json: true));

        Assert.True(doc.RootElement.GetProperty("complexity").GetProperty("available").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("clones").GetProperty("available").GetBoolean());
        Assert.Equal(3, doc.RootElement.GetProperty("index").GetProperty("symbols").GetInt32());
    }

    [Fact]
    public void ReportJson_KeepsMarkersAvailableWithZeroCounts_ForFullLevelArtifact()
    {
        using JsonDocument doc = JsonDocument.Parse(RunReport(FullLevel(), json: true));
        JsonElement markers = doc.RootElement.GetProperty("markers");

        Assert.True(markers.GetProperty("available").GetBoolean());
        Assert.Equal(0, markers.GetProperty("total").GetInt32());
        Assert.Equal(
            new[] { "TODO", "FIXME", "HACK", "XXX" },
            markers.GetProperty("counts").EnumerateArray()
                .Select(c => c.GetProperty("marker").GetString())
                .ToArray());
        Assert.All(
            markers.GetProperty("counts").EnumerateArray(),
            c => Assert.Equal(0, c.GetProperty("count").GetInt32()));
    }

    [Fact]
    public void Report_SnapshotMetricNamesAreTheSame_ForSymbolsAndFullLevelArtifacts()
    {
        IReadOnlyList<MetricHistoryPoint> symbols = ReportMetrics(SymbolsLevel());
        IReadOnlyList<MetricHistoryPoint> full = ReportMetrics(FullLevel());

        Assert.Equal(
            full.Select(p => p.Metric).ToArray(),
            symbols.Select(p => p.Metric).ToArray());
        Assert.DoesNotContain(MetricSnapshotAggregates.MarkerTotal, symbols.Select(p => p.Metric));
    }

    private string SymbolsLevel() => SymbolsLevelArtifact.Create(Path.Combine(_dir, "symbols"));

    private string FullLevel() => SymbolsLevelArtifact.CreateFull(Path.Combine(_dir, "full"));

    private static string RunReport(string dbPath, bool json) => Report(dbPath, json).Output;

    private static IReadOnlyList<MetricHistoryPoint> ReportMetrics(string dbPath) =>
        Report(dbPath, json: true).SnapshotMetrics;

    private static ReportToolResult Report(string dbPath, bool json) =>
        ReportTool.Run(
            dbPath,
            workspaceRoot: Path.GetDirectoryName(dbPath),
            range: null,
            sectionLimit: ReportTool.DefaultSectionLimit,
            json: json,
            includeTests: true,
            historyReader: null,
            regionIndex: null);

    private static double ValueOf(IReadOnlyList<MetricHistoryPoint> metrics, string name) =>
        Assert.Single(metrics, m => m.Metric == name).Value;

    private static IReadOnlyList<string> RecordedMetricNames(string symbolsDbPath)
    {
        var names = new List<string>();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = MetricSnapshotAggregates.HistoryDbPathFor(symbolsDbPath),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT metric FROM snapshot_metrics;";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names;
    }
}
