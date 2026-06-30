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
        Assert.Equal("htmx attribute", row.Label);
        Assert.Equal("known", row.Catalog);
        Assert.Equal("An htmx attribute usage", row.Description);
        Assert.Equal(new[] { "htmx", "html" }, row.Tags);
    }

    [Fact]
    public void Export_EmitsDeterministicJsonLines()
    {
        using var fx = CreatePatternFixture();
        string first = PatternFactsExportReader.ExportJsonLines(fx.DbPath);
        string second = PatternFactsExportReader.ExportJsonLines(fx.DbPath);
        Assert.Equal(first, second);
        Assert.Contains("\"structural_fact_id\":\"fact-hx-get\"", first, StringComparison.Ordinal);
    }

    [Fact]
    public void Export_MissingStructuralFactsTableThrowsCleanError()
    {
        using var fx = CreatePatternFixture();
        Exec(fx.DbPath, "DROP TABLE structural_facts;");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
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
