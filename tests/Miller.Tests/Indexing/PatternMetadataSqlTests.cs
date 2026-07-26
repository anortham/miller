using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class PatternMetadataSqlTests
{
    [Fact]
    public void BundledSqlite_SupportsJsonExtractAndJsonValid()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        using var insert = connection.CreateCommand();
        insert.CommandText = """
            CREATE TABLE t(metadata_json TEXT);
            INSERT INTO t VALUES ('{"name":"hx-get","verb":42,"enabled":true}');
            """;
        insert.ExecuteNonQuery();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT json_valid(metadata_json),
                   json_extract(metadata_json, '$.name'),
                   json_extract(metadata_json, '$.verb'),
                   json_type(metadata_json, '$.enabled')
            FROM t;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal("hx-get", reader.GetString(1));
        Assert.Equal(42L, reader.GetInt64(2));
        Assert.Equal("true", reader.GetString(3));
    }

    [Fact]
    public void Search_MultiWhereAndFiltersMalformedMetadata()
    {
        using var fx = CreatePatternFixture();
        var reader = new PatternFactsReader();

        IReadOnlyList<PatternMatchRow> rows = reader.Search(
            fx.DbPath,
            patternId: "htmx.attribute.v1",
            language: null,
            pathGlob: "Views/**",
            metadataFilters: new[]
            {
                new PatternMetadataFilter("name", "hx-get"),
            },
            limit: 50);

        PatternMatchRow row = Assert.Single(rows);
        Assert.Equal("fact-hx-get", row.FactId);
    }

    [Fact]
    public void Search_PathGlobWildcardDoesNotCrossDirectorySeparators()
    {
        using var fx = CreatePatternFixture();
        Exec(fx.DbPath, """
            INSERT INTO structural_facts
                (structural_fact_id, file_id, path, language, pattern_id, capture_name, node_kind,
                 containing_symbol_id, start_line, start_column, end_line, end_column, start_byte, end_byte,
                 confidence, metadata_json)
            VALUES
                ('fact-nested', 'file:Views/Orders.cshtml', 'Views/Nested/Orders.cshtml', 'razor',
                 'htmx.attribute.v1', 'attribute', 'attribute', 'sym-orders',
                 1, 9, 1, 25, 8, 24, 1.0, '{"name":"hx-nested"}');
            """);
        var reader = new PatternFactsReader();

        IReadOnlyList<PatternMatchRow> directChildren = reader.Search(
            fx.DbPath,
            patternId: "htmx.attribute.v1",
            language: null,
            pathGlob: "Views/*.cshtml",
            metadataFilters: null,
            limit: 50);
        IReadOnlyList<PatternMatchRow> basenameStyle = reader.Search(
            fx.DbPath,
            patternId: "htmx.attribute.v1",
            language: null,
            pathGlob: "Views*.cshtml",
            metadataFilters: null,
            limit: 50);

        Assert.Equal("fact-hx-get", Assert.Single(directChildren).FactId);
        Assert.Empty(basenameStyle);
    }

    [Fact]
    public void Search_MetadataNullMatchesRawJsonNullValue()
    {
        using var fx = CreatePatternFixture();
        Exec(fx.DbPath, """
            INSERT INTO structural_facts
                (structural_fact_id, file_id, path, language, pattern_id, capture_name, node_kind,
                 containing_symbol_id, start_line, start_column, end_line, end_column, start_byte, end_byte,
                 confidence, metadata_json)
            VALUES
                ('fact-null-name', 'file:Views/Orders.cshtml', 'Views/Orders.cshtml', 'razor',
                 'htmx.attribute.v1', 'attribute', 'attribute', 'sym-orders',
                 1, 9, 1, 25, 8, 24, 1.0, '{"name":null}');
            """);
        var reader = new PatternFactsReader();

        IReadOnlyList<PatternMatchRow> rows = reader.Search(
            fx.DbPath,
            patternId: "htmx.attribute.v1",
            language: null,
            pathGlob: "Views/**",
            metadataFilters: new[]
            {
                new PatternMetadataFilter("name", "null"),
            },
            limit: 50);

        Assert.Equal("fact-null-name", Assert.Single(rows).FactId);
    }

    [Theory]
    [InlineData("enabled", "true", "fact-metadata-true")]
    [InlineData("enabled", "false", "fact-metadata-false")]
    [InlineData("count", "42", "fact-metadata-number")]
    [InlineData("payload", "{\"x\":1}", "fact-metadata-object")]
    [InlineData("payload", "[1,2]", "fact-metadata-array")]
    public void Search_MetadataSqlPreservesJsonScalarAndRawValueSemantics(
        string key,
        string value,
        string expectedFactId)
    {
        using var fx = CreatePatternFixture();
        Exec(fx.DbPath, """
            INSERT INTO structural_facts
                (structural_fact_id, file_id, path, language, pattern_id, capture_name, node_kind,
                 containing_symbol_id, start_line, start_column, end_line, end_column, start_byte, end_byte,
                 confidence, metadata_json)
            VALUES
                ('fact-metadata-true', 'file:Views/Orders.cshtml', 'Views/Orders.cshtml', 'razor',
                 'metadata.values.v1', 'value', 'attribute', 'sym-orders',
                 10, 1, 10, 10, 100, 109, 1.0, '{"enabled":true}'),
                ('fact-metadata-false', 'file:Views/Orders.cshtml', 'Views/Orders.cshtml', 'razor',
                 'metadata.values.v1', 'value', 'attribute', 'sym-orders',
                 11, 1, 11, 10, 110, 119, 1.0, '{"enabled":false}'),
                ('fact-metadata-number', 'file:Views/Orders.cshtml', 'Views/Orders.cshtml', 'razor',
                 'metadata.values.v1', 'value', 'attribute', 'sym-orders',
                 12, 1, 12, 10, 120, 129, 1.0, '{"count":42}'),
                ('fact-metadata-object', 'file:Views/Orders.cshtml', 'Views/Orders.cshtml', 'razor',
                 'metadata.values.v1', 'value', 'attribute', 'sym-orders',
                 13, 1, 13, 10, 130, 139, 1.0, '{"payload":{"x":1}}'),
                ('fact-metadata-array', 'file:Views/Orders.cshtml', 'Views/Orders.cshtml', 'razor',
                 'metadata.values.v1', 'value', 'attribute', 'sym-orders',
                 14, 1, 14, 10, 140, 149, 1.0, '{"payload":[1,2]}');
            """);
        var reader = new PatternFactsReader();

        IReadOnlyList<PatternMatchRow> rows = reader.Search(
            fx.DbPath,
            patternId: "metadata.values.v1",
            language: null,
            pathGlob: null,
            metadataFilters: [new PatternMetadataFilter(key, value)],
            limit: 50);

        Assert.Equal(expectedFactId, Assert.Single(rows).FactId);
    }

    [Fact]
    public void List_AppliesCatalogOverlayWhenTablePresent()
    {
        using var fx = CreatePatternFixture();
        Exec(fx.DbPath, """
            INSERT INTO pattern_catalog(pattern_id, label, description, tags_json, expected_metadata_keys_json)
            VALUES ('htmx.attribute.v1', 'htmx attribute', 'An htmx attribute usage', '["htmx","html"]', '["name","value"]');
            """);
        var reader = new PatternFactsReader();

        PatternListRow row = Assert.Single(reader.List(fx.DbPath), item => item.PatternId == "htmx.attribute.v1");
        PatternListRow filtered = Assert.Single(reader.List(
            fx.DbPath,
            patternId: "htmx.attribute.v1",
            language: "razor",
            pathGlob: "Views/*.cshtml",
            metadataFilters: [new PatternMetadataFilter("name", "hx-get")]));
        Assert.Equal("htmx attribute", row.Label);
        Assert.Equal("known", row.Catalog);
        Assert.Equal("An htmx attribute usage", row.Description);
        Assert.Equal(new[] { "htmx", "html" }, row.Tags);
        Assert.Equal("htmx attribute", filtered.Label);
        Assert.Equal(1, filtered.Count);
    }

    [Fact]
    public void Summary_FacetPreservesJsonValueSemanticsAndSkipsMalformedRows()
    {
        using var fx = CreatePatternFixture();
        Exec(fx.DbPath, """
            INSERT INTO structural_facts
                (structural_fact_id, file_id, path, language, pattern_id, capture_name, node_kind,
                 containing_symbol_id, start_line, start_column, end_line, end_column, start_byte, end_byte,
                 confidence, metadata_json)
            VALUES
                ('fact-facet-number', 'file:Views/Orders.cshtml', 'Views/Orders.cshtml', 'razor',
                 'htmx.attribute.v1', 'attribute', 'attribute', 'sym-orders',
                 2, 1, 2, 10, 25, 34, 1.0, '{"facet":42}'),
                ('fact-facet-number-duplicate', 'file:Views/Orders.cshtml', 'Views/Orders.cshtml', 'razor',
                 'htmx.attribute.v1', 'attribute', 'attribute', 'sym-orders',
                 3, 1, 3, 10, 35, 44, 1.0, '{"facet":42}'),
                ('fact-facet-bool', 'file:Views/Orders.cshtml', 'Views/Orders.cshtml', 'razor',
                 'htmx.attribute.v1', 'attribute', 'attribute', 'sym-orders',
                 4, 1, 4, 10, 45, 54, 1.0, '{"facet":true}'),
                ('fact-facet-null', 'file:Views/Orders.cshtml', 'Views/Orders.cshtml', 'razor',
                 'htmx.attribute.v1', 'attribute', 'attribute', 'sym-orders',
                 5, 1, 5, 10, 55, 64, 1.0, '{"facet":null}'),
                ('fact-facet-object', 'file:Views/Orders.cshtml', 'Views/Orders.cshtml', 'razor',
                 'htmx.attribute.v1', 'attribute', 'attribute', 'sym-orders',
                 6, 1, 6, 10, 65, 74, 1.0, '{"facet":{"nested":1}}'),
                ('fact-facet-malformed', 'file:Views/Orders.cshtml', 'Views/Orders.cshtml', 'razor',
                 'htmx.attribute.v1', 'attribute', 'attribute', 'sym-orders',
                 7, 1, 7, 10, 75, 84, 1.0, '{bad-json');
            """);
        var reader = new PatternFactsReader();

        IReadOnlyList<PatternSummaryRow> fallbackRows = reader.Summary(
            fx.DbPath,
            patternId: "htmx.attribute.v1",
            language: null,
            pathGlob: "Views/*.cshtml",
            metadataFilters: null,
            groupBy: PatternSummaryGroupBy.File,
            facetKey: "facet");
        IReadOnlyList<PatternSummaryRow> sqlRows = reader.Summary(
            fx.DbPath,
            patternId: "htmx.attribute.v1",
            language: null,
            pathGlob: "Views/Orders.cshtml",
            metadataFilters: null,
            groupBy: PatternSummaryGroupBy.File,
            facetKey: "facet");

        Assert.Equal(
            new[] { "42", "null", "true", "{\"nested\":1}" },
            fallbackRows.Select(static row => row.FacetValue).ToArray());
        Assert.Equal(2, fallbackRows[0].Count);
        Assert.All(fallbackRows.Skip(1), static row => Assert.Equal(1, row.Count));
        Assert.Equal(fallbackRows, sqlRows);
    }

    [Fact]
    public void Export_EmitsDeterministicJsonLines()
    {
        using var fx = CreatePatternFixture();
        string first = PatternFactsExportReader.ExportJsonLines(fx.DbPath);
        string second = PatternFactsExportReader.ExportJsonLines(fx.DbPath);
        Assert.Equal(first, second);
        using JsonDocument row = JsonDocument.Parse(first);
        Assert.Equal(2, row.RootElement.GetProperty("schema_version").GetInt32());
        Assert.Contains("\"structural_fact_id\":\"fact-hx-get\"", first, StringComparison.Ordinal);
    }

    [Fact]
    public void Export_WriteJsonLines_MatchesStringExport()
    {
        using var fx = CreatePatternFixture();
        using var writer = new StringWriter();

        PatternFactsExportReader.WriteJsonLines(fx.DbPath, writer);

        Assert.Equal(PatternFactsExportReader.ExportJsonLines(fx.DbPath), writer.ToString());
    }

    [Fact]
    public void Export_MissingStructuralFactsTableThrowsCleanError()
    {
        using var fx = CreatePatternFixture();
        Exec(fx.DbPath, "DROP TABLE structural_facts;");

        IncompatibleExtractException ex = Assert.Throws<IncompatibleExtractException>(
            () => PatternFactsExportReader.ExportJsonLines(fx.DbPath));
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
            },
            fileContent: new Dictionary<string, string>
            {
                ["Views/Orders.cshtml"] = "<button hx-get=\"/orders\"></button>\n",
            });
        Exec(fx.DbPath, """
            INSERT INTO structural_facts
                (structural_fact_id, file_id, path, language, pattern_id, capture_name, node_kind,
                 containing_symbol_id, start_line, start_column, end_line, end_column, start_byte, end_byte,
                 confidence, metadata_json)
            VALUES
                ('fact-hx-get', 'file:Views/Orders.cshtml', 'Views/Orders.cshtml', 'razor',
                 'htmx.attribute.v1', 'attribute', 'attribute', 'sym-orders',
                 1, 9, 1, 25, 8, 24, 1.0, '{"name":"hx-get","value":"/orders"}');
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
