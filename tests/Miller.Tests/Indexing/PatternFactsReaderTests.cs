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
    public void SearchWithCount_UsesOneFullFilteredPopulationForSqlAndFallbackGlobs()
    {
        using var fx = CreatePatternFixture();
        var reader = new PatternFactsReader();

        PatternMatchResult allHtmx = reader.SearchWithCount(
            fx.DbPath,
            patternId: "htmx.attribute.v1",
            language: null,
            pathGlob: null,
            metadataFilters: null,
            limit: 2);
        PatternMatchResult fallbackGlob = reader.SearchWithCount(
            fx.DbPath,
            patternId: "htmx.attribute.v1",
            language: null,
            pathGlob: "*/Orders.cshtml",
            metadataFilters: [new PatternMetadataFilter("name", "hx-trigger")],
            limit: 2);
        PatternMatchResult multiplePatterns = reader.SearchWithCount(
            fx.DbPath,
            ["htmx.attribute.v1", "future.custom_pattern.v1"],
            language: null,
            pathGlob: null,
            metadataFilters: null,
            limit: 2);
        IReadOnlyDictionary<string, long> groupedFallback = reader.CountMatchesByPatternId(
            fx.DbPath,
            ["htmx.attribute.v1", "future.custom_pattern.v1"],
            language: null,
            pathGlob: "*/Orders.cshtml",
            metadataFilters: null);

        Assert.Equal(4, allHtmx.TotalCount);
        Assert.Equal(2, allHtmx.Rows.Count);
        Assert.Equal(1, fallbackGlob.TotalCount);
        Assert.Equal("fact-hx-trigger", Assert.Single(fallbackGlob.Rows).FactId);
        Assert.Equal(5, multiplePatterns.TotalCount);
        Assert.Equal(2, multiplePatterns.Rows.Count);
        Assert.Equal(2, groupedFallback["htmx.attribute.v1"]);
        Assert.DoesNotContain("future.custom_pattern.v1", groupedFallback);
    }

    [Fact]
    public void SearchExactPage_AllocationTracksPagePayloadInsteadOfPopulationPayload()
    {
        using var fx = CreatePatternFixture();
        Exec(fx.DbPath, """
            WITH RECURSIVE seq(n) AS (
                SELECT 1
                UNION ALL
                SELECT n + 1 FROM seq WHERE n < 1000
            )
            INSERT INTO structural_facts
                (structural_fact_id, file_id, path, language, pattern_id, capture_name, node_kind,
                 containing_symbol_id, start_line, start_column, end_line, end_column, start_byte, end_byte,
                 confidence, metadata_json)
            SELECT
                'fact-page-' || printf('%04d', n), 'file:src/Auth.cs', 'src/Page.cs', 'csharp',
                'page.performance.v1', 'item', 'object', 'sym-auth',
                n, 1, n, 2, n * 2, n * 2 + 1, 1.0,
                json_object('payload', printf('%.*c', 32768, 'x'))
            FROM seq;
            """);
        var reader = new PatternFactsReader();

        long before = GC.GetAllocatedBytesForCurrentThread();
        PatternExactSearchPageResult result = reader.SearchExactPageWithContext(
            fx.DbPath,
            patternId: "page.performance.v1",
            language: null,
            pathGlob: null,
            metadataFilters: null,
            offset: 0,
            limit: 1);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(1000, result.Page.TotalCount);
        Assert.Equal("fact-page-0001", Assert.Single(result.Page.Rows).FactId);
        Assert.True(allocated < 8 * 1024 * 1024, $"Allocated {allocated:N0} bytes.");
    }

    [Fact]
    public void SearchExactWithContext_ReturnsMatchesExistenceAndScopedSuggestionsFromOneRead()
    {
        using var fx = CreatePatternFixture();
        var reader = new PatternFactsReader();

        PatternExactSearchResult filtered = reader.SearchExactWithContext(
            fx.DbPath,
            patternId: "htmx.attribute.v1",
            language: "csharp",
            pathGlob: null,
            metadataFilters: null,
            limit: 2);
        PatternExactSearchResult missing = reader.SearchExactWithContext(
            fx.DbPath,
            patternId: "aspnet.minimal_api.routex.v1",
            language: "razor",
            pathGlob: null,
            metadataFilters: null,
            limit: 2);

        Assert.True(filtered.PatternExists);
        Assert.Equal(0, filtered.Matches.TotalCount);
        Assert.False(missing.PatternExists);
        Assert.Equal(
            new[] { "htmx.attribute.v1" },
            missing.SuggestionPatternIds);
    }

    [Fact]
    public void SearchByQueryWithCount_ReturnsFanoutAndMatchesFromOneRead()
    {
        using var fx = CreatePatternFixture();
        var reader = new PatternFactsReader();

        PatternQueryMatchResult result = reader.SearchByQueryWithCount(
            fx.DbPath,
            query: "attribute",
            language: null,
            pathGlob: null,
            metadataFilters: null,
            limit: 2,
            maxPatternIds: 25);

        Assert.Equal(
            new[] { "future.custom_pattern.v1", "htmx.attribute.v1" },
            result.ConsideredPatternIds);
        Assert.Equal(1, result.MatchedPatternCount);
        Assert.Equal(new[] { "htmx.attribute.v1" }, result.ReturnedPatternIds);
        Assert.Equal(4, result.Matches.TotalCount);
        Assert.Equal(2, result.Matches.Rows.Count);
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

        var ex = Assert.Throws<IncompatibleExtractException>(() => reader.Search(
            fx.DbPath,
            patternId: "htmx.attribute.v1",
            language: null,
            metadataFilter: null,
            limit: 50));

        Assert.Contains("structural_facts", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchExactPage_WithinOneArtifactGeneration_KeepsThePopulationFingerprintStableAcrossPages()
    {
        using var fx = CreatePatternFixture();
        var reader = new PatternFactsReader();

        PatternExactSearchPageResult first = ExactPage(reader, fx.DbPath, offset: 0);
        PatternExactSearchPageResult second = ExactPage(reader, fx.DbPath, offset: 1);

        Assert.Equal(first.Page.PopulationFingerprint, second.Page.PopulationFingerprint);
        Assert.Equal(first.Page.TotalCount, second.Page.TotalCount);
    }

    [Fact]
    public void SearchExactPage_AfterAFullRebuildReplacesTheArtifactId_ChangesThePopulationFingerprint()
    {
        using var fx = CreatePatternFixture();
        var reader = new PatternFactsReader();
        string before = ExactPage(reader, fx.DbPath, offset: 0).Page.PopulationFingerprint;

        Exec(fx.DbPath, "UPDATE artifact_metadata SET value = 'artifact-rebuilt' WHERE key = 'artifact_id';");

        Assert.NotEqual(before, ExactPage(reader, fx.DbPath, offset: 0).Page.PopulationFingerprint);
    }

    [Fact]
    public void SearchExactPage_AfterANewExtractionRevision_ChangesThePopulationFingerprint()
    {
        using var fx = CreatePatternFixture();
        var reader = new PatternFactsReader();
        string before = ExactPage(reader, fx.DbPath, offset: 0).Page.PopulationFingerprint;

        Exec(fx.DbPath, """
            INSERT INTO extraction_revisions
                (revision_id, parent_revision_id, operation, mode, started_at, completed_at,
                 binary_version, extract_contract_version, sqlite_schema_version, input_root, counts_json)
            VALUES (
                (SELECT COALESCE(MAX(revision_id), 0) + 1 FROM extraction_revisions),
                NULL, 'scan', 'incremental', '1970-01-01T00:00:00Z', '1970-01-01T00:00:00Z',
                '2.18.0', '4', '5', NULL, '{}');
            """);

        Assert.NotEqual(before, ExactPage(reader, fx.DbPath, offset: 0).Page.PopulationFingerprint);
    }

    private static PatternExactSearchPageResult ExactPage(
        PatternFactsReader reader,
        string dbPath,
        int offset) =>
        reader.SearchExactPageWithContext(
            dbPath,
            "htmx.attribute.v1",
            language: null,
            pathGlob: null,
            metadataFilters: null,
            offset,
            limit: 1);

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
