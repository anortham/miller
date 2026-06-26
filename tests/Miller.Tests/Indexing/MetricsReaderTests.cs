using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class MetricsReaderTests
{
    [Fact]
    public void CloneGroupReader_GroupsDuplicateNonEmptyBodyHashes()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                Row("aa11223344556677889900aabbccdd01", "FirstCopy", "src/A.cs", 3),
                Row("aa11223344556677889900aabbccdd02", "SecondCopy", "src/B.cs", 7),
                Row("aa11223344556677889900aabbccdd03", "Singleton", "src/C.cs", 11),
                Row("aa11223344556677889900aabbccdd04", "EmptyHash", "src/D.cs", 15),
            });
        Exec(fx.DbPath, """
            UPDATE symbols SET body_hash = 'normalized-body-1' WHERE symbol_id IN
                ('aa11223344556677889900aabbccdd01', 'aa11223344556677889900aabbccdd02');
            UPDATE symbols SET body_hash = 'normalized-body-2' WHERE symbol_id = 'aa11223344556677889900aabbccdd03';
            UPDATE symbols SET body_hash = '' WHERE symbol_id = 'aa11223344556677889900aabbccdd04';
            """);

        IReadOnlyList<CloneGroup> groups = CloneGroupReader.Read(fx.DbPath, limit: 10, minCount: 2);

        CloneGroup group = Assert.Single(groups);
        Assert.Equal("normalized-body-1", group.BodyHash);
        Assert.Equal(2, group.Count);
        Assert.Equal(new[] { "FirstCopy", "SecondCopy" }, group.Symbols.Select(static s => s.Name).ToArray());
    }

    [Fact]
    public void CloneGroupReader_BoundsSymbolsPerGroupButKeepsFullCount()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                Row("aa11223344556677889900aabbccde01", "FirstCopy", "src/A.cs", 3),
                Row("aa11223344556677889900aabbccde02", "SecondCopy", "src/B.cs", 7),
                Row("aa11223344556677889900aabbccde03", "ThirdCopy", "src/C.cs", 11),
            });
        Exec(fx.DbPath, """
            UPDATE symbols SET body_hash = 'normalized-body-wide' WHERE symbol_id IN
                ('aa11223344556677889900aabbccde01',
                 'aa11223344556677889900aabbccde02',
                 'aa11223344556677889900aabbccde03');
            """);

        IReadOnlyList<CloneGroup> groups = CloneGroupReader.Read(
            fx.DbPath,
            limit: 10,
            minCount: 2,
            symbolsPerGroup: 2);

        CloneGroup group = Assert.Single(groups);
        Assert.Equal(3, group.Count);
        Assert.Equal(new[] { "FirstCopy", "SecondCopy" }, group.Symbols.Select(static s => s.Name).ToArray());
    }

    [Fact]
    public void ComplexityRankingReader_SortsAndClassifiesHotspots()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                Row("aa11223344556677889900aabbccdd11", "High", "src/High.cs", 5),
                Row("aa11223344556677889900aabbccdd12", "Moderate", "src/Moderate.cs", 8),
                Row("aa11223344556677889900aabbccdd13", "Low", "src/Low.cs", 11),
                Row("aa11223344556677889900aabbccdd14", "HighTest", "tests/HighTests.cs", 14, isTest: true),
            });
        SeedComplexity(fx.DbPath, "metric-high", "src/High.cs", "aa11223344556677889900aabbccdd11", 20, 2);
        SeedComplexity(fx.DbPath, "metric-moderate", "src/Moderate.cs", "aa11223344556677889900aabbccdd12", 8, 4);
        SeedComplexity(fx.DbPath, "metric-low", "src/Low.cs", "aa11223344556677889900aabbccdd13", 3, 1);
        SeedComplexity(fx.DbPath, "metric-test", "tests/HighTests.cs", "aa11223344556677889900aabbccdd14", 30, 8);

        IReadOnlyList<ComplexityHotspot> hotspots = ComplexityRankingReader.Read(
            fx.DbPath,
            limit: 10,
            minSeverity: ComplexitySeverity.Moderate,
            includeTests: false);

        Assert.Equal(new[] { "metric-high", "metric-moderate" },
            hotspots.Select(static h => h.ComplexityMetricId).ToArray());
        Assert.Equal(ComplexitySeverity.High, hotspots[0].Severity);
        Assert.Equal(ComplexitySeverity.Moderate, hotspots[1].Severity);
        Assert.Equal("High", hotspots[0].SymbolName);
        Assert.DoesNotContain(hotspots, static h => h.IsTest);
    }

    private static JulieDbFixture.SymbolRow Row(
        string id,
        string name,
        string path,
        int line,
        bool isTest = false) =>
        new(id, name, "method", "csharp", path, $"void {name}()", line, null)
        {
            EndLine = line + 4,
            StartByte = line * 10,
            EndByte = line * 10 + 40,
            IsTest = isTest,
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
                 10, 100, {decisions}, 1, {nesting}, 0, 1, 0, 10, 0, 0, 100);
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
}
