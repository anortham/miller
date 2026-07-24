using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins Miller's read-only structural-fact reader. These tests use the synthetic julie-extract artifact fixture;
/// they do not spawn julie-extract.
/// </summary>
public sealed class PatternFactsReaderTests
{
    [Fact]
    public void List_GroupsObservedPatternsWithoutCatalogEntries()
    {
        using var fx = CreatePatternFixture();
        var reader = new PatternFactsReader();

        IReadOnlyList<PatternListRow> rows = reader.List(fx.DbPath);

        PatternListRow htmx = Assert.Single(rows, row => row.PatternId == "htmx.attribute.v1");
        Assert.Equal("htmx.attribute.v1", htmx.Label);
        Assert.Equal("observed", htmx.Catalog);
        Assert.Equal(4, htmx.Count);
        Assert.Equal(new[] { "html", "razor" }, htmx.Languages);
        Assert.Equal(new[] { "attribute" }, htmx.Captures);

        PatternListRow unknown = Assert.Single(rows, row => row.PatternId == "future.custom_pattern.v1");
        Assert.Equal("future.custom_pattern.v1", unknown.Label);
        Assert.Equal(new[] { "csharp" }, unknown.Languages);
    }

    [Fact]
    public void Search_FiltersTopLevelMetadataAndSkipsMalformedMetadata()
    {
        using var fx = CreatePatternFixture();
        var reader = new PatternFactsReader();

        IReadOnlyList<PatternMatchRow> rows = reader.Search(
            fx.DbPath,
            patternId: "htmx.attribute.v1",
            language: null,
            metadataFilter: new PatternMetadataFilter("name", "hx-get"),
            limit: 50);

        PatternMatchRow row = Assert.Single(rows);
        Assert.Equal("fact-hx-get", row.FactId);
        Assert.Equal("Views/Orders.cshtml", row.Path);
        Assert.Equal("razor", row.Language);
        Assert.Equal("hx-get", row.Metadata.GetProperty("name").GetString());
        Assert.Equal("/orders", row.Metadata.GetProperty("value").GetString());
        Assert.Null(row.MetadataError);
    }

    [Fact]
    public void Search_UnfilteredRowsReportMalformedMetadataWithoutCrashing()
    {
        using var fx = CreatePatternFixture();
        var reader = new PatternFactsReader();

        IReadOnlyList<PatternMatchRow> rows = reader.Search(
            fx.DbPath,
            patternId: "htmx.attribute.v1",
            language: null,
            metadataFilter: null,
            limit: 50);

        PatternMatchRow malformed = Assert.Single(rows, row => row.FactId == "fact-malformed");
        Assert.Equal("{bad-json", malformed.MetadataJson);
        Assert.NotNull(malformed.MetadataError);
        Assert.Equal(default, malformed.Metadata);
    }

    [Fact]
    public void Summary_GroupsByLanguagePatternAndCapture()
    {
        using var fx = CreatePatternFixture();
        var reader = new PatternFactsReader();

        IReadOnlyList<PatternSummaryRow> rows = reader.Summary(fx.DbPath, patternId: null, language: null);

        Assert.Contains(rows, row =>
            row.Language == "razor" &&
            row.PatternId == "htmx.attribute.v1" &&
            row.CaptureName == "attribute" &&
            row.Count == 2);
        Assert.Contains(rows, row =>
            row.Language == "html" &&
            row.PatternId == "htmx.attribute.v1" &&
            row.CaptureName == "attribute" &&
            row.Count == 2);
    }

    [Fact]
    public void Summary_DirectoryGroupingCountsMoreThanTenThousandFactsExactly()
    {
        using var fx = CreatePatternFixture();
        Exec(fx.DbPath, """
            WITH RECURSIVE seq(n) AS (
                SELECT 1
                UNION ALL
                SELECT n + 1 FROM seq WHERE n < 10005
            )
            INSERT INTO structural_facts
                (structural_fact_id, file_id, path, language, pattern_id, capture_name, node_kind,
                 containing_symbol_id, start_line, start_column, end_line, end_column, start_byte, end_byte,
                 confidence, metadata_json)
            SELECT
                'fact-large-' || n, 'file:src/Auth.cs', 'src/features/routes/Auth.cs', 'csharp',
                'large.route.v1', 'route_call', 'invocation_expression', 'sym-auth',
                n, 1, n, 20, n * 20, n * 20 + 19, 1.0, '{"verb":"GET"}'
            FROM seq;
            """);
        var reader = new PatternFactsReader();

        IReadOnlyList<PatternSummaryRow> rows = reader.Summary(
            fx.DbPath,
            patternId: "large.route.v1",
            language: null,
            groupBy: PatternSummaryGroupBy.Directory);

        PatternSummaryRow row = Assert.Single(rows);
        Assert.Equal("src/features/routes", row.Directory);
        Assert.Equal(10005, row.Count);
    }

    [Fact]
    public void Search_MissingStructuralFactsTableThrowsCleanError()
    {
        using var fx = CreatePatternFixture();
        Exec(fx.DbPath, "DROP TABLE structural_facts;");
        var reader = new PatternFactsReader();

        var ex = Assert.Throws<InvalidOperationException>(() => reader.Search(
            fx.DbPath,
            patternId: "htmx.attribute.v1",
            language: null,
            metadataFilter: null,
            limit: 50));

        Assert.Contains("structural_facts", ex.Message, StringComparison.Ordinal);
    }

    private static JulieDbFixture CreatePatternFixture()
    {
        var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                new JulieDbFixture.SymbolRow("sym-orders", "OrdersView", "view", "razor",
                    "Views/Orders.cshtml", null, 1, null),
                new JulieDbFixture.SymbolRow("sym-index", "Index", "function", "html",
                    "public/index.html", null, 1, null),
                new JulieDbFixture.SymbolRow("sym-auth", "AuthorizeHandler", "method", "csharp",
                    "src/Auth.cs", "void AuthorizeHandler()", 1, null),
            },
            fileContent: new Dictionary<string, string>
            {
                ["Views/Orders.cshtml"] = "<button hx-get=\"/orders\"></button>\n",
                ["public/index.html"] = "<button hx-post=\"/orders\"></button>\n",
                ["src/Auth.cs"] = "[Authorize]\npublic class Auth {}\n",
            });
        Exec(fx.DbPath, """
            INSERT INTO structural_facts
                (structural_fact_id, file_id, path, language, pattern_id, capture_name, node_kind,
                 containing_symbol_id, start_line, start_column, end_line, end_column, start_byte, end_byte,
                 confidence, metadata_json)
            VALUES
                ('fact-hx-get', 'file:Views/Orders.cshtml', 'Views/Orders.cshtml', 'razor',
                 'htmx.attribute.v1', 'attribute', 'attribute', 'sym-orders',
                 1, 9, 1, 25, 8, 24, 1.0, '{"name":"hx-get","value":"/orders"}'),
                ('fact-hx-trigger', 'file:Views/Orders.cshtml', 'Views/Orders.cshtml', 'razor',
                 'htmx.attribute.v1', 'attribute', 'attribute', 'sym-orders',
                 1, 26, 1, 45, 25, 44, 1.0, '{"name":"hx-trigger","value":"click"}'),
                ('fact-hx-post', 'file:public/index.html', 'public/index.html', 'html',
                 'htmx.attribute.v1', 'attribute', 'attribute', 'sym-index',
                 1, 9, 1, 26, 8, 25, 1.0, '{"name":"hx-post","value":"/orders"}'),
                ('fact-malformed', 'file:public/index.html', 'public/index.html', 'html',
                 'htmx.attribute.v1', 'attribute', 'attribute', 'sym-index',
                 2, 1, 2, 10, 26, 35, 1.0, '{bad-json'),
                ('fact-future', 'file:src/Auth.cs', 'src/Auth.cs', 'csharp',
                 'future.custom_pattern.v1', 'custom', 'attribute', 'sym-auth',
                 1, 1, 1, 12, 0, 11, 0.75, '{"name":"Authorize"}');
            """);
        return fx;
    }

    private static void Exec(string dbPath, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
