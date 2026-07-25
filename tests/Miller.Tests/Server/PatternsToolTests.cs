using System.ComponentModel;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Server.Telemetry;
using Miller.Server.Tools;
using Miller.Server.Workspaces;
using Miller.Tests.Indexing;
using Xunit;

namespace Miller.Tests.Server;

public sealed class PatternsToolTests
{
    [Fact]
    public void Patterns_Description_DoesNotFreezeCatalogCounts()
    {
        DescriptionAttribute description = typeof(PatternsTool)
            .GetMethod(nameof(PatternsTool.Patterns))!
            .GetCustomAttribute<DescriptionAttribute>()!;

        Assert.DoesNotMatch(@"\b\d+\s+pattern ids\b", description.Description);
        Assert.DoesNotMatch(@"\b\d+\s+languages\b", description.Description);
    }

    private static (string? Op, string MetadataJson, string Outcome) ReadTelemetryOpMetadata(string dbPath)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT op, metadata_json, outcome FROM tool_telemetry LIMIT 1;";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read(), "expected one telemetry row");
        return (r.IsDBNull(0) ? null : r.GetString(0), r.GetString(1), r.GetString(2));
    }

    [Fact]
    public void Patterns_ListJson_ReturnsObservedPatterns()
    {
        using var fx = CreatePatternFixture();
        var tool = new PatternsTool(new TestArtifactProvider(ArtifactFor(fx)), new PatternFactsReader());

        string json = tool.Patterns(operation: "list", format: "json");

        Assert.DoesNotContain("workspace:", json, StringComparison.OrdinalIgnoreCase);
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal(1, doc.RootElement.GetProperty("schema_version").GetInt32());
        Assert.Equal("list", doc.RootElement.GetProperty("operation").GetString());

        JsonElement htmx = doc.RootElement.GetProperty("patterns").EnumerateArray()
            .Single(row => row.GetProperty("pattern_id").GetString() == "htmx.attribute.v1");
        Assert.Equal(4, htmx.GetProperty("count").GetInt64());
        Assert.Equal(new[] { "html", "razor" }, htmx.GetProperty("languages").EnumerateArray().Select(static value => value.GetString()).ToArray());
        Assert.Equal(new[] { "attribute" }, htmx.GetProperty("captures").EnumerateArray().Select(static value => value.GetString()).ToArray());

        JsonElement future = doc.RootElement.GetProperty("patterns").EnumerateArray()
            .Single(row => row.GetProperty("pattern_id").GetString() == "future.custom_pattern.v1");
        Assert.Equal("observed", future.GetProperty("catalog").GetString());

        JsonElement[] actions = doc.RootElement.GetProperty("next_actions").EnumerateArray().ToArray();
        Assert.Contains(actions, action =>
            action.GetProperty("tool").GetString() == "patterns"
            && action.GetProperty("args").GetProperty("operation").GetString() == "search");
        Assert.Contains(actions, action =>
            action.GetProperty("tool").GetString() == "patterns"
            && action.GetProperty("args").GetProperty("operation").GetString() == "summary");
        Assert.Contains(actions, action =>
            action.GetProperty("tool").GetString() == "patterns"
            && action.GetProperty("args").TryGetProperty("query", out JsonElement query)
            && query.GetString() == "route");
    }

    [Fact]
    public void Patterns_ListCompact_IncludesConcreteNextActions()
    {
        using var fx = CreatePatternFixture();
        var tool = new PatternsTool(new TestArtifactProvider(ArtifactFor(fx)), new PatternFactsReader());

        string output = tool.Patterns(operation: "list");

        Assert.Contains("patterns operation=search pattern_id=", output, StringComparison.Ordinal);
        Assert.Contains("patterns operation=summary pattern_id=", output, StringComparison.Ordinal);
        Assert.Contains("patterns operation=search query=route", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Patterns_SearchJson_FiltersMetadataAndPath()
    {
        using var fx = CreatePatternFixture();
        var tool = new PatternsTool(new TestArtifactProvider(ArtifactFor(fx)), new PatternFactsReader());

        string json = tool.Patterns(
            operation: "search",
            pattern_id: "htmx.attribute.v1",
            path: "Views/**",
            where: "name=hx-get",
            limit: 10,
            format: "json");

        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal("search", doc.RootElement.GetProperty("operation").GetString());
        Assert.Equal("htmx.attribute.v1", doc.RootElement.GetProperty("pattern_id").GetString());

        JsonElement match = Assert.Single(doc.RootElement.GetProperty("matches").EnumerateArray());
        Assert.Equal("fact-hx-get", match.GetProperty("fact_id").GetString());
        Assert.Equal("Views/Orders.cshtml", match.GetProperty("path").GetString());
        Assert.Equal("attribute", match.GetProperty("capture_name").GetString());
        Assert.Equal(1.0, match.GetProperty("confidence").GetDouble());
        Assert.Equal("hx-get", match.GetProperty("metadata").GetProperty("name").GetString());
        Assert.Equal(8, match.GetProperty("span").GetProperty("start_byte").GetInt32());
        JsonElement activeFilters = doc.RootElement.GetProperty("active_filters");
        Assert.Equal("Views/**", activeFilters.GetProperty("path").GetString());
        JsonElement where = Assert.Single(activeFilters.GetProperty("where").EnumerateArray());
        Assert.Equal("name", where.GetProperty("key").GetString());
        Assert.Equal("hx-get", where.GetProperty("value").GetString());
    }

    [Fact]
    public void Patterns_SearchCompact_IncludesWhereMetadataKey()
    {
        using var fx = CreatePatternFixture();
        var tool = new PatternsTool(new TestArtifactProvider(ArtifactFor(fx)), new PatternFactsReader());

        string output = tool.Patterns(
            operation: "search",
            pattern_id: "aspnet.minimal_api.route.v1",
            where: "verb=GET;route_template=/orders",
            limit: 10);

        Assert.Contains("active filters: where=verb=GET, where=route_template=/orders", output);
        Assert.Contains("metadata=verb=GET,route_template=/orders", output);
        Assert.Contains("route_template=/orders", output);
    }

    [Fact]
    public void Patterns_SearchCompact_PreservesEveryFilteredMetadataKey()
    {
        using var fx = CreatePatternFixture();
        Exec(fx.DbPath, """
            INSERT INTO structural_facts
                (structural_fact_id, file_id, path, language, pattern_id, capture_name, node_kind,
                 containing_symbol_id, start_line, start_column, end_line, end_column, start_byte, end_byte,
                 confidence, metadata_json)
            VALUES
                ('fact-five-filters', 'file:Views/Orders.cshtml', 'Views/Orders.cshtml', 'razor',
                 'filtered.metadata.v1', 'property', 'attribute', 'sym-orders',
                 1, 1, 1, 20, 0, 19, 1.0,
                 '{"k1":"a","k2":"b","k3":"c","k4":"d","k5":"e","extra":"hidden"}');
            """);
        var tool = new PatternsTool(new TestArtifactProvider(ArtifactFor(fx)), new PatternFactsReader());

        string output = tool.Patterns(
            operation: "search",
            pattern_id: "filtered.metadata.v1",
            where: "k1=a;k2=b;k3=c;k4=d;k5=e");

        Assert.Contains("metadata=k1=a,k2=b,k3=c,k4=d,k5=e", output, StringComparison.Ordinal);
        Assert.DoesNotContain("extra=hidden", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Patterns_SearchCompact_SuggestsNearMissPatternIds()
    {
        using var fx = CreatePatternFixture();
        var tool = new PatternsTool(new TestArtifactProvider(ArtifactFor(fx)), new PatternFactsReader());

        string output = tool.Patterns(operation: "search", pattern_id: "aspnet.route.v1", limit: 10);

        Assert.Contains("No matches for aspnet.route.v1.", output);
        Assert.Contains("aspnet.minimal_api.route.v1", output);
        Assert.Contains(
            "patterns operation=search pattern_id=aspnet.minimal_api.route.v1",
            output,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Patterns_Search_RecordsOperationShape_InTelemetry()
    {
        using var fx = CreatePatternFixture();
        string telemetryDb = Path.Combine(Path.GetDirectoryName(fx.DbPath)!, "telemetry.db");
        var tool = new PatternsTool(new TestArtifactProvider(ArtifactFor(fx)), new PatternFactsReader());

        using (var ledger = TelemetryLedger.Open(telemetryDb, "workspace-1", Path.GetDirectoryName(Path.GetDirectoryName(fx.DbPath))!))
        {
            using var scope = ledger.Measure("patterns", op: null);
            string output = tool.Patterns(
                operation: "search",
                pattern_id: "htmx.attribute.v1",
                path: "Views/**",
                where: "name=hx-get",
                limit: 10);
            Assert.Contains("Views/Orders.cshtml", output);
            Assert.Contains("hx-get", output);
        }

        var row = ReadTelemetryOpMetadata(telemetryDb);
        Assert.Equal("search", row.Op);
        Assert.Equal("ok", row.Outcome);
        using JsonDocument doc = JsonDocument.Parse(row.MetadataJson);
        Assert.True(doc.RootElement.GetProperty("has_pattern_id").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("has_path").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("has_where").GetBoolean());
        Assert.Equal("6-10", doc.RootElement.GetProperty("limit_bucket").GetString());
        Assert.DoesNotContain("htmx.attribute.v1", row.MetadataJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Patterns_SummaryJson_RespectsPathFilter()
    {
        using var fx = CreatePatternFixture();
        var tool = new PatternsTool(new TestArtifactProvider(ArtifactFor(fx)), new PatternFactsReader());

        string json = tool.Patterns(
            operation: "summary",
            pattern_id: "htmx.attribute.v1",
            path: "Views/**",
            format: "json");

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement group = Assert.Single(doc.RootElement.GetProperty("groups").EnumerateArray());
        Assert.Equal("razor", group.GetProperty("language").GetString());
        Assert.Equal("htmx.attribute.v1", group.GetProperty("pattern_id").GetString());
        Assert.Equal("attribute", group.GetProperty("capture_name").GetString());
        Assert.Equal(2, group.GetProperty("count").GetInt64());
    }

    [Fact]
    public void Patterns_SummaryJson_AllowsWhereWithoutPatternTarget()
    {
        using var fx = CreatePatternFixture();
        var tool = new PatternsTool(new TestArtifactProvider(ArtifactFor(fx)), new PatternFactsReader());

        string json = tool.Patterns(
            operation: "summary",
            where: "name=hx-get",
            format: "json");

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement group = Assert.Single(doc.RootElement.GetProperty("groups").EnumerateArray());
        Assert.Equal("htmx.attribute.v1", group.GetProperty("pattern_id").GetString());
        Assert.Equal(1, group.GetProperty("count").GetInt64());
        JsonElement where = Assert.Single(
            doc.RootElement.GetProperty("active_filters").GetProperty("where").EnumerateArray());
        Assert.Equal("name", where.GetProperty("key").GetString());
        Assert.Equal("hx-get", where.GetProperty("value").GetString());
    }

    [Fact]
    public void Patterns_ListJson_AllowsWhereWithoutPatternTarget()
    {
        using var fx = CreatePatternFixture();
        var tool = new PatternsTool(new TestArtifactProvider(ArtifactFor(fx)), new PatternFactsReader());

        string json = tool.Patterns(
            operation: "list",
            where: "name=hx-get",
            format: "json");

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement pattern = Assert.Single(doc.RootElement.GetProperty("patterns").EnumerateArray());
        Assert.Equal("htmx.attribute.v1", pattern.GetProperty("pattern_id").GetString());
        Assert.Equal(1, pattern.GetProperty("count").GetInt64());
        JsonElement where = Assert.Single(
            doc.RootElement.GetProperty("active_filters").GetProperty("where").EnumerateArray());
        Assert.Equal("name", where.GetProperty("key").GetString());
        Assert.Equal("hx-get", where.GetProperty("value").GetString());
    }

    [Theory]
    [InlineData("list")]
    [InlineData("summary")]
    public void Patterns_CollectionCompact_EmptyResultNamesActiveFilters(string operation)
    {
        using var fx = CreatePatternFixture();
        var tool = new PatternsTool(new TestArtifactProvider(ArtifactFor(fx)), new PatternFactsReader());

        string output = tool.Patterns(
            operation: operation,
            language: "razor",
            path: "Views/**",
            where: "name=missing");

        Assert.Contains(
            "active filters: language=razor, path=Views/**, where=name=missing",
            output,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Patterns_SummaryCompact_EmptyResultNamesGroupingAndFacet()
    {
        using var fx = CreatePatternFixture();
        var tool = new PatternsTool(new TestArtifactProvider(ArtifactFor(fx)), new PatternFactsReader());

        string output = tool.Patterns(
            operation: "summary",
            language: "razor",
            group_by: "file",
            facet: "missing_key");

        Assert.Contains("group_by=file", output, StringComparison.Ordinal);
        Assert.Contains("facet=missing_key", output, StringComparison.Ordinal);
        Assert.Contains("active filters: language=razor", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Patterns_SearchWithoutPattern_ReturnsCleanFailure()
    {
        using var fx = CreatePatternFixture();
        var tool = new PatternsTool(new TestArtifactProvider(ArtifactFor(fx)), new PatternFactsReader());

        string output = tool.Patterns(operation: "search", format: "json");

        using var document = JsonDocument.Parse(output);
        JsonElement diagnostic = document.RootElement.GetProperty("diagnostic");
        Assert.Equal("invalid_request", diagnostic.GetProperty("code").GetString());
        Assert.Equal("refusal", diagnostic.GetProperty("class").GetString());
        Assert.Contains("pattern_id", diagnostic.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Patterns_InvalidRequest_DoesNotRecordErrorTelemetry()
    {
        using var fx = CreatePatternFixture();
        string telemetryDb = Path.Combine(Path.GetDirectoryName(fx.DbPath)!, "telemetry-invalid.db");
        var tool = new PatternsTool(new TestArtifactProvider(ArtifactFor(fx)), new PatternFactsReader());

        using var ledger = TelemetryLedger.Open(
            telemetryDb,
            "workspace-1",
            Path.GetDirectoryName(Path.GetDirectoryName(fx.DbPath))!);
        using var scope = ledger.Measure("patterns", op: null);

        tool.Patterns(operation: "search", format: "json");

        Assert.Equal(TelemetryOutcome.Empty, scope.Outcome);
        Assert.Null(scope.ErrorKind);
        Assert.Null(scope.ErrorMessage);
        Assert.Null(scope.ErrorDetail);
        Assert.DoesNotContain("error_category", scope.MetadataJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Patterns_SearchByQuery_MapsToPatternIdsContainingSubstring()
    {
        using var fx = CreatePatternFixture();
        var tool = new PatternsTool(new TestArtifactProvider(ArtifactFor(fx)), new PatternFactsReader());

        string output = tool.Patterns(operation: "search", query: "route", limit: 10);

        Assert.Contains("# patterns search query='route'", output);
        Assert.Contains("matched_pattern_ids: aspnet.minimal_api.route.v1", output);
        Assert.Contains("aspnet.minimal_api.route.v1", output);
        Assert.Contains("src/Auth.cs", output);
        Assert.Contains("route_template=/orders", output);
    }

    [Fact]
    public void Patterns_SearchByQuery_JsonIncludesQueryAndMatchedPatternIds()
    {
        using var fx = CreatePatternFixture();
        var tool = new PatternsTool(new TestArtifactProvider(ArtifactFor(fx)), new PatternFactsReader());

        string json = tool.Patterns(operation: "search", query: "route", limit: 10, format: "json");

        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal("search", doc.RootElement.GetProperty("operation").GetString());
        Assert.Equal("route", doc.RootElement.GetProperty("query").GetString());
        Assert.Equal(
            new[] { "aspnet.minimal_api.route.v1" },
            doc.RootElement.GetProperty("matched_pattern_ids").EnumerateArray().Select(static value => value.GetString()).ToArray());
        JsonElement match = Assert.Single(doc.RootElement.GetProperty("matches").EnumerateArray());
        Assert.Equal("src/Auth.cs", match.GetProperty("path").GetString());
        Assert.Equal("fact-route", match.GetProperty("fact_id").GetString());
    }

    [Fact]
    public void Patterns_SearchJson_LimitReportsFullFilteredPopulation()
    {
        using var fx = CreatePatternFixture();
        var tool = new PatternsTool(new TestArtifactProvider(ArtifactFor(fx)), new PatternFactsReader());

        string json = tool.Patterns(
            operation: "search",
            pattern_id: "htmx.attribute.v1",
            limit: 2,
            format: "json");

        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal(4, doc.RootElement.GetProperty("matches_total_count").GetInt64());
        Assert.Equal(2, doc.RootElement.GetProperty("matches_returned_count").GetInt64());
        Assert.Equal(2, doc.RootElement.GetProperty("matches_omitted_count").GetInt64());
        Assert.True(doc.RootElement.GetProperty("matches_truncated").GetBoolean());
        Assert.Equal(2, doc.RootElement.GetProperty("matches").GetArrayLength());
    }

    [Fact]
    public void Patterns_SearchByQuery_JsonLimitReportsFullSelectedPopulation()
    {
        using var fx = CreatePatternFixture();
        var tool = new PatternsTool(new TestArtifactProvider(ArtifactFor(fx)), new PatternFactsReader());

        string json = tool.Patterns(
            operation: "search",
            query: "htmx",
            limit: 2,
            format: "json");

        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal(4, doc.RootElement.GetProperty("matches_total_count").GetInt64());
        Assert.Equal(2, doc.RootElement.GetProperty("matches_returned_count").GetInt64());
        Assert.Equal(2, doc.RootElement.GetProperty("matches_omitted_count").GetInt64());
        Assert.True(doc.RootElement.GetProperty("matches_truncated").GetBoolean());
        Assert.Equal(2, doc.RootElement.GetProperty("matches").GetArrayLength());
    }

    [Fact]
    public void Patterns_SearchByQuery_NoMatch_ReturnsRecoveryHint()
    {
        using var fx = CreatePatternFixture();
        var tool = new PatternsTool(new TestArtifactProvider(ArtifactFor(fx)), new PatternFactsReader());

        string output = tool.Patterns(operation: "search", query: "zzz-not-a-pattern");

        Assert.Contains("No patterns match 'zzz-not-a-pattern'", output);
        Assert.Contains(
            "pattern_id_fanout: considered=4 matched=0 returned=0 omitted=0 truncated=false",
            output,
            StringComparison.Ordinal);
        Assert.Contains("patterns operation=list", output);
    }

    [Fact]
    public void Patterns_SearchByQuery_NoMatch_ReturnsNearMatchesAndNextActions()
    {
        using var fx = CreatePatternFixture();
        var tool = new PatternsTool(new TestArtifactProvider(ArtifactFor(fx)), new PatternFactsReader());

        string output = tool.Patterns(operation: "search", query: "markdwn");

        Assert.Contains("No patterns match 'markdwn'", output);
        Assert.Contains("near matches:", output);
        Assert.Contains("markdown.heading.v1", output);
        Assert.Contains("patterns operation=list", output);
        Assert.Contains("patterns operation=search pattern_id=markdown.heading.v1", output);
    }

    [Fact]
    public void Patterns_SearchByQuery_NoMatch_NearMatchesRespectLanguage()
    {
        using var fx = CreatePatternFixture();
        var tool = new PatternsTool(new TestArtifactProvider(ArtifactFor(fx)), new PatternFactsReader());

        string json = tool.Patterns(
            operation: "search",
            query: "aspnet.minimal_api.routex.v1",
            language: "razor",
            format: "json");

        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal(4, doc.RootElement.GetProperty("pattern_ids_considered_count").GetInt32());
        Assert.Empty(doc.RootElement.GetProperty("near_matches").EnumerateArray());
        Assert.DoesNotContain(
            doc.RootElement.GetProperty("next_actions").EnumerateArray(),
            static action => action.GetProperty("args").TryGetProperty("pattern_id", out _));
    }

    [Fact]
    public void Patterns_SearchByQuery_NoMatch_JsonReturnsValidJson()
    {
        using var fx = CreatePatternFixture();
        var tool = new PatternsTool(new TestArtifactProvider(ArtifactFor(fx)), new PatternFactsReader());

        string json = tool.Patterns(operation: "search", query: "zzz-not-a-pattern", format: "json");

        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal("search", doc.RootElement.GetProperty("operation").GetString());
        Assert.Equal("zzz-not-a-pattern", doc.RootElement.GetProperty("query").GetString());
        Assert.Equal(4, doc.RootElement.GetProperty("pattern_ids_considered_count").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("pattern_ids_matched_count").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("pattern_ids_returned_count").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("pattern_ids_omitted_count").GetInt32());
        Assert.False(doc.RootElement.GetProperty("pattern_id_fanout_truncated").GetBoolean());
        Assert.Equal(0, doc.RootElement.GetProperty("matches_total_count").GetInt64());
        Assert.Equal(0, doc.RootElement.GetProperty("matches_returned_count").GetInt64());
        Assert.Equal(0, doc.RootElement.GetProperty("matches_omitted_count").GetInt64());
        Assert.False(doc.RootElement.GetProperty("matches_truncated").GetBoolean());
        Assert.Empty(doc.RootElement.GetProperty("matched_pattern_ids").EnumerateArray());
        Assert.Empty(doc.RootElement.GetProperty("matches").EnumerateArray());
        Assert.Contains("No patterns match", doc.RootElement.GetProperty("note").GetString());
    }

    [Fact]
    public void Patterns_SearchByQuery_NoMatch_JsonIncludesNearMatchesAndNextActions()
    {
        using var fx = CreatePatternFixture();
        var tool = new PatternsTool(new TestArtifactProvider(ArtifactFor(fx)), new PatternFactsReader());

        string json = tool.Patterns(operation: "search", query: "markdwn", format: "json");

        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal(
            new[] { "markdown.heading.v1" },
            doc.RootElement.GetProperty("near_matches").EnumerateArray().Select(static value => value.GetString()).ToArray());
        JsonElement[] actions = doc.RootElement.GetProperty("next_actions").EnumerateArray().ToArray();
        Assert.Contains(actions, action =>
            action.GetProperty("tool").GetString() == "patterns"
            && action.GetProperty("args").GetProperty("operation").GetString() == "list");
        Assert.Contains(actions, action =>
            action.GetProperty("tool").GetString() == "patterns"
            && action.GetProperty("args").TryGetProperty("pattern_id", out JsonElement patternId)
            && patternId.GetString() == "markdown.heading.v1");
    }

    [Fact]
    public void Patterns_SearchByPatternId_NoRowsAfterFilters_NamesActiveFilters()
    {
        using var fx = CreatePatternFixture();
        var tool = new PatternsTool(new TestArtifactProvider(ArtifactFor(fx)), new PatternFactsReader());

        string output = tool.Patterns(
            operation: "search",
            pattern_id: "htmx.attribute.v1",
            language: "csharp",
            where: "name=hx-get;value=/missing",
            path: "Views/**",
            limit: 10);

        Assert.Contains("No matches for htmx.attribute.v1 after filters", output);
        Assert.Contains("language=csharp", output);
        Assert.Contains("path=Views/**", output);
        Assert.Contains("where=name=hx-get", output);
        Assert.Contains("where=value=/missing", output);
        Assert.Contains("loosen language, path, or where", output);
    }

    [Fact]
    public void Patterns_SearchByQuery_WithWhere_FiltersMetadataAcrossMatchedPatternIds()
    {
        using var fx = CreatePatternFixture();
        var tool = new PatternsTool(new TestArtifactProvider(ArtifactFor(fx)), new PatternFactsReader());

        string output = tool.Patterns(operation: "search", query: "route", where: "verb=GET", limit: 10);

        Assert.Contains("matched_pattern_ids: aspnet.minimal_api.route.v1", output);
        Assert.Contains("src/Auth.cs", output);
        Assert.Contains("verb=GET", output);
    }

    [Fact]
    public void Patterns_SearchByQuery_ConsidersEveryMatchedPatternBeforeGlobalLimit()
    {
        using var fx = CreatePatternFixture();
        Exec(fx.DbPath, """
            INSERT INTO structural_facts
                (structural_fact_id, file_id, path, language, pattern_id, capture_name, node_kind,
                 containing_symbol_id, start_line, start_column, end_line, end_column, start_byte, end_byte,
                 confidence, metadata_json)
            VALUES
                ('fact-bulk-route-1', 'file:src/Auth.cs', 'zzz/BulkRoutes.cs', 'csharp',
                 'bulk.route.v1', 'route_call', 'invocation_expression', 'sym-auth',
                 1, 1, 1, 20, 0, 19, 1.0, '{"verb":"GET"}'),
                ('fact-bulk-route-2', 'file:src/Auth.cs', 'zzz/BulkRoutes.cs', 'csharp',
                 'bulk.route.v1', 'route_call', 'invocation_expression', 'sym-auth',
                 2, 1, 2, 20, 20, 39, 1.0, '{"verb":"POST"}');
            """);
        var tool = new PatternsTool(new TestArtifactProvider(ArtifactFor(fx)), new PatternFactsReader());

        string json = tool.Patterns(operation: "search", query: "route", limit: 1, format: "json");

        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal(
            new[] { "bulk.route.v1", "aspnet.minimal_api.route.v1" },
            doc.RootElement.GetProperty("matched_pattern_ids").EnumerateArray().Select(static value => value.GetString()).ToArray());
        JsonElement match = Assert.Single(doc.RootElement.GetProperty("matches").EnumerateArray());
        Assert.Equal("fact-route", match.GetProperty("fact_id").GetString());
        Assert.Equal("src/Auth.cs", match.GetProperty("path").GetString());
    }

    [Fact]
    public void Patterns_SearchByQuery_JsonReportsFanOutTruncation()
    {
        using var fx = CreatePatternFixture();
        Exec(fx.DbPath, """
            WITH RECURSIVE seq(n) AS (
                SELECT 1
                UNION ALL
                SELECT n + 1 FROM seq WHERE n < 30
            )
            INSERT INTO structural_facts
                (structural_fact_id, file_id, path, language, pattern_id, capture_name, node_kind,
                 containing_symbol_id, start_line, start_column, end_line, end_column, start_byte, end_byte,
                 confidence, metadata_json)
            SELECT
                printf('fact-fanout-%02d', n), 'file:src/Auth.cs', 'src/Auth.cs', 'csharp',
                printf('fanout.route.%02d.v1', n), 'route_call', 'invocation_expression', 'sym-auth',
                n, 1, n, 20, n * 20, n * 20 + 19, 1.0, '{"verb":"GET"}'
            FROM seq;
            """);
        var tool = new PatternsTool(new TestArtifactProvider(ArtifactFor(fx)), new PatternFactsReader());

        string json = tool.Patterns(operation: "search", query: "fanout.route", limit: 1, format: "json");

        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal(34, doc.RootElement.GetProperty("pattern_ids_considered_count").GetInt32());
        Assert.Equal(30, doc.RootElement.GetProperty("pattern_ids_matched_count").GetInt32());
        Assert.Equal(25, doc.RootElement.GetProperty("pattern_ids_returned_count").GetInt32());
        Assert.Equal(5, doc.RootElement.GetProperty("pattern_ids_omitted_count").GetInt32());
        Assert.True(doc.RootElement.GetProperty("pattern_id_fanout_truncated").GetBoolean());
        Assert.Equal(25, doc.RootElement.GetProperty("matched_pattern_ids").GetArrayLength());
        Assert.Single(doc.RootElement.GetProperty("matches").EnumerateArray());
    }

    [Fact]
    public void Patterns_SearchByQuery_PrioritizesPatternIdsWithFactsUnderActiveFilters()
    {
        using var fx = CreatePatternFixture();
        Exec(fx.DbPath, """
            WITH RECURSIVE seq(n) AS (
                SELECT 1
                UNION ALL
                SELECT n + 1 FROM seq WHERE n < 50
            )
            INSERT INTO structural_facts
                (structural_fact_id, file_id, path, language, pattern_id, capture_name, node_kind,
                 containing_symbol_id, start_line, start_column, end_line, end_column, start_byte, end_byte,
                 confidence, metadata_json)
            SELECT
                printf('fact-filtered-route-%02d', n), 'file:src/Auth.cs', 'src/Auth.cs', 'csharp',
                printf('filtered.route.%02d.v1', ((n - 1) % 25) + 1), 'route_call',
                'invocation_expression', 'sym-auth',
                n, 1, n, 20, n * 20, n * 20 + 19, 1.0, '{"verb":"GET"}'
            FROM seq;

            INSERT INTO structural_facts
                (structural_fact_id, file_id, path, language, pattern_id, capture_name, node_kind,
                 containing_symbol_id, start_line, start_column, end_line, end_column, start_byte, end_byte,
                 confidence, metadata_json)
            VALUES
                ('fact-filtered-route-razor', 'file:Views/Orders.cshtml', 'Views/Orders.cshtml', 'razor',
                 'filtered.route.99.v1', 'route_call', 'attribute', 'sym-orders',
                 1, 1, 1, 20, 0, 19, 1.0, '{"verb":"GET"}');
            """);
        var tool = new PatternsTool(new TestArtifactProvider(ArtifactFor(fx)), new PatternFactsReader());

        string json = tool.Patterns(
            operation: "search",
            query: "filtered.route",
            language: "razor",
            limit: 1,
            format: "json");

        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal(26, doc.RootElement.GetProperty("pattern_ids_matched_count").GetInt32());
        Assert.Equal(25, doc.RootElement.GetProperty("pattern_ids_returned_count").GetInt32());
        Assert.Contains(
            doc.RootElement.GetProperty("matched_pattern_ids").EnumerateArray(),
            value => value.GetString() == "filtered.route.99.v1");
        JsonElement match = Assert.Single(doc.RootElement.GetProperty("matches").EnumerateArray());
        Assert.Equal("fact-filtered-route-razor", match.GetProperty("fact_id").GetString());
    }

    [Fact]
    public void Patterns_SearchJson_BoundsMcpRowsAndReportsOmissions()
    {
        using var fx = CreatePatternFixture();
        Exec(fx.DbPath, """
            WITH RECURSIVE seq(n) AS (
                SELECT 1
                UNION ALL
                SELECT n + 1 FROM seq WHERE n < 200
            )
            INSERT INTO structural_facts
                (structural_fact_id, file_id, path, language, pattern_id, capture_name, node_kind,
                 containing_symbol_id, start_line, start_column, end_line, end_column, start_byte, end_byte,
                 confidence, metadata_json)
            SELECT
                printf('fact-budget-%03d', n), 'file:src/Auth.cs', printf('src/Generated%03d.cs', n), 'csharp',
                'budget.pattern.v1', 'route_call', 'invocation_expression', 'sym-auth',
                n, 1, n, 20, n * 20, n * 20 + 19, 1.0,
                '{"verb":"GET","route_template":"/generated/abcdefghijklmnopqrstuvwxyz"}'
            FROM seq;
            """);
        var tool = new PatternsTool(new TestArtifactProvider(ArtifactFor(fx)), new PatternFactsReader());

        string json = tool.Patterns(
            operation: "search",
            pattern_id: "budget.pattern.v1",
            language: "csharp",
            path: "src/**",
            where: "verb=GET",
            limit: 500,
            format: "json");

        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.True(Encoding.UTF8.GetByteCount(json) <= ToolOutputBudget.PatternsMcpMaxBytes);
        Assert.True(doc.RootElement.GetProperty("matches_truncated").GetBoolean());
        Assert.Equal(200, doc.RootElement.GetProperty("matches_total_count").GetInt32());
        int returned = doc.RootElement.GetProperty("matches_returned_count").GetInt32();
        Assert.InRange(returned, 1, 199);
        Assert.Equal(200 - returned, doc.RootElement.GetProperty("matches_omitted_count").GetInt32());
        Assert.Equal(returned, doc.RootElement.GetProperty("matches").GetArrayLength());
        JsonElement actionArgs = doc.RootElement.GetProperty("next_actions")[0].GetProperty("args");
        Assert.Equal("budget.pattern.v1", actionArgs.GetProperty("pattern_id").GetString());
        Assert.Equal("csharp", actionArgs.GetProperty("language").GetString());
        Assert.Equal("src/**", actionArgs.GetProperty("path").GetString());
        Assert.Equal("verb=GET", actionArgs.GetProperty("where").GetString());
        Assert.Equal(JsonValueKind.Number, actionArgs.GetProperty("limit").ValueKind);
        Assert.Equal(1, actionArgs.GetProperty("limit").GetInt32());

        string actionResult = tool.Patterns(
            operation: actionArgs.GetProperty("operation").GetString(),
            pattern_id: actionArgs.GetProperty("pattern_id").GetString(),
            language: actionArgs.GetProperty("language").GetString(),
            path: actionArgs.GetProperty("path").GetString(),
            where: actionArgs.GetProperty("where").GetString(),
            limit: int.Parse(
                actionArgs.GetProperty("limit").GetRawText(),
                System.Globalization.CultureInfo.InvariantCulture),
            format: "json");
        using JsonDocument actionDoc = JsonDocument.Parse(actionResult);
        Assert.False(actionDoc.RootElement.TryGetProperty("diagnostic", out _));

        string queryJson = tool.Patterns(
            operation: "search",
            query: "budget.pattern",
            language: "csharp",
            path: "src/**",
            where: "verb=GET",
            limit: 500,
            format: "json");
        using JsonDocument queryDoc = JsonDocument.Parse(queryJson);
        JsonElement queryActionArgs = queryDoc.RootElement.GetProperty("next_actions")[0].GetProperty("args");
        Assert.Equal("budget.pattern", queryActionArgs.GetProperty("query").GetString());
        Assert.Equal("csharp", queryActionArgs.GetProperty("language").GetString());
        Assert.Equal("src/**", queryActionArgs.GetProperty("path").GetString());
        Assert.Equal("verb=GET", queryActionArgs.GetProperty("where").GetString());
        Assert.Equal(JsonValueKind.Number, queryActionArgs.GetProperty("limit").ValueKind);
        Assert.Equal(1, queryActionArgs.GetProperty("limit").GetInt32());

        string queryActionResult = tool.Patterns(
            operation: queryActionArgs.GetProperty("operation").GetString(),
            query: queryActionArgs.GetProperty("query").GetString(),
            language: queryActionArgs.GetProperty("language").GetString(),
            path: queryActionArgs.GetProperty("path").GetString(),
            where: queryActionArgs.GetProperty("where").GetString(),
            limit: int.Parse(
                queryActionArgs.GetProperty("limit").GetRawText(),
                System.Globalization.CultureInfo.InvariantCulture),
            format: "json");
        using JsonDocument queryActionDoc = JsonDocument.Parse(queryActionResult);
        Assert.False(queryActionDoc.RootElement.TryGetProperty("diagnostic", out _));
    }

    [Fact]
    public void Patterns_ListJson_BoundsMcpRowsAndReportsOmissions()
    {
        using var fx = CreatePatternFixture();
        Exec(fx.DbPath, """
            WITH RECURSIVE seq(n) AS (
                SELECT 1
                UNION ALL
                SELECT n + 1 FROM seq WHERE n < 300
            )
            INSERT INTO structural_facts
                (structural_fact_id, file_id, path, language, pattern_id, capture_name, node_kind,
                 containing_symbol_id, start_line, start_column, end_line, end_column, start_byte, end_byte,
                 confidence, metadata_json)
            SELECT
                printf('fact-list-budget-%03d', n), 'file:src/Auth.cs', 'src/Auth.cs', 'csharp',
                printf('generated.long_pattern_identifier_%03d.v1', n), 'generated_capture',
                'invocation_expression', 'sym-auth',
                n, 1, n, 20, n * 20, n * 20 + 19, 1.0, '{"verb":"GET"}'
            FROM seq;
            """);
        var tool = new PatternsTool(new TestArtifactProvider(ArtifactFor(fx)), new PatternFactsReader());

        string json = tool.Patterns(operation: "list", format: "json");

        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.True(Encoding.UTF8.GetByteCount(json) <= ToolOutputBudget.PatternsMcpMaxBytes);
        Assert.Equal(304, doc.RootElement.GetProperty("patterns_total_count").GetInt32());
        int returned = doc.RootElement.GetProperty("patterns_returned_count").GetInt32();
        Assert.InRange(returned, 1, 303);
        Assert.Equal(304 - returned, doc.RootElement.GetProperty("patterns_omitted_count").GetInt32());
        Assert.True(doc.RootElement.GetProperty("patterns_truncated").GetBoolean());
        Assert.Equal(returned, doc.RootElement.GetProperty("patterns").GetArrayLength());
        Assert.Equal(
            "htmx.attribute.v1",
            doc.RootElement.GetProperty("patterns")[0].GetProperty("pattern_id").GetString());
        JsonElement firstAction = doc.RootElement.GetProperty("next_actions")[0];
        Assert.Equal(
            "htmx.attribute.v1",
            firstAction.GetProperty("args").GetProperty("pattern_id").GetString());
        string listNote = doc.RootElement.GetProperty("note").GetString()!;
        Assert.Contains("pattern_id, language, or path", listNote, StringComparison.Ordinal);
        Assert.DoesNotContain("where", listNote, StringComparison.Ordinal);
        Assert.DoesNotContain("group", listNote, StringComparison.Ordinal);

        string compact = tool.Patterns(operation: "list");
        Assert.True(Encoding.UTF8.GetByteCount(compact) <= ToolOutputBudget.PatternsMcpMaxBytes);
        Assert.Contains("patterns: total=304", compact, StringComparison.Ordinal);
        Assert.Contains("truncated=true", compact, StringComparison.Ordinal);
        Assert.Contains(
            "next: refine pattern_id, language, or path to narrow the result.",
            compact,
            StringComparison.Ordinal);
        Assert.DoesNotContain("where", compact, StringComparison.Ordinal);
        Assert.DoesNotContain("grouping", compact, StringComparison.Ordinal);

        WorkspaceArtifactContext current = ArtifactFor(fx);
        WorkspaceArtifactContext target = current with
        {
            WorkspaceId = "target-ws",
            WorkspaceRoot = "/repo/target",
            DisplayId = "target",
        };
        var routedTool = new PatternsTool(
            new TestArtifactProvider(current, ("target-ws", target)),
            new PatternFactsReader());
        string routedCompact = routedTool.Patterns(operation: "list", workspace_id: "target-ws");
        Assert.True(Encoding.UTF8.GetByteCount(routedCompact) <= ToolOutputBudget.PatternsMcpMaxBytes);
    }

    [Fact]
    public void Patterns_SummaryJson_BoundsMcpGroupsAndReportsOmissions()
    {
        using var fx = CreatePatternFixture();
        Exec(fx.DbPath, """
            WITH RECURSIVE seq(n) AS (
                SELECT 1
                UNION ALL
                SELECT n + 1 FROM seq WHERE n < 500
            )
            INSERT INTO structural_facts
                (structural_fact_id, file_id, path, language, pattern_id, capture_name, node_kind,
                 containing_symbol_id, start_line, start_column, end_line, end_column, start_byte, end_byte,
                 confidence, metadata_json)
            SELECT
                printf('fact-summary-budget-%03d', n), 'file:src/Auth.cs',
                printf('src/generated/Feature%03d.cs', n), 'csharp',
                'generated.summary.v1', 'generated_capture', 'invocation_expression', 'sym-auth',
                n, 1, n, 20, n * 20, n * 20 + 19, 1.0, '{"verb":"GET"}'
            FROM seq;

            INSERT INTO structural_facts
                (structural_fact_id, file_id, path, language, pattern_id, capture_name, node_kind,
                 containing_symbol_id, start_line, start_column, end_line, end_column, start_byte, end_byte,
                 confidence, metadata_json)
            VALUES
                ('fact-summary-heavy-01', 'file:src/Auth.cs', 'src/generated/Feature500.cs', 'csharp',
                 'generated.summary.v1', 'generated_capture', 'invocation_expression', 'sym-auth',
                 501, 1, 501, 20, 10020, 10039, 1.0, '{"verb":"GET"}'),
                ('fact-summary-heavy-02', 'file:src/Auth.cs', 'src/generated/Feature500.cs', 'csharp',
                 'generated.summary.v1', 'generated_capture', 'invocation_expression', 'sym-auth',
                 502, 1, 502, 20, 10040, 10059, 1.0, '{"verb":"GET"}');
            """);
        var tool = new PatternsTool(new TestArtifactProvider(ArtifactFor(fx)), new PatternFactsReader());

        string json = tool.Patterns(
            operation: "summary",
            pattern_id: "generated.summary.v1",
            group_by: "file",
            format: "json");

        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.True(Encoding.UTF8.GetByteCount(json) <= ToolOutputBudget.PatternsMcpMaxBytes);
        Assert.Equal(500, doc.RootElement.GetProperty("groups_total_count").GetInt32());
        int returned = doc.RootElement.GetProperty("groups_returned_count").GetInt32();
        Assert.InRange(returned, 1, 499);
        Assert.Equal(500 - returned, doc.RootElement.GetProperty("groups_omitted_count").GetInt32());
        Assert.True(doc.RootElement.GetProperty("groups_truncated").GetBoolean());
        Assert.Equal(returned, doc.RootElement.GetProperty("groups").GetArrayLength());
        Assert.Equal("src/generated/Feature500.cs", doc.RootElement.GetProperty("groups")[0].GetProperty("path").GetString());
        Assert.Equal(3, doc.RootElement.GetProperty("groups")[0].GetProperty("count").GetInt64());
        Assert.Contains(
            "pattern_id, language, path, where, or grouping",
            doc.RootElement.GetProperty("note").GetString(),
            StringComparison.Ordinal);

        string compact = tool.Patterns(
            operation: "summary",
            pattern_id: "generated.summary.v1",
            group_by: "file");
        Assert.True(Encoding.UTF8.GetByteCount(compact) <= ToolOutputBudget.PatternsMcpMaxBytes);
        Assert.Contains("groups: total=500", compact, StringComparison.Ordinal);
        Assert.Contains("truncated=true", compact, StringComparison.Ordinal);
    }

    [Fact]
    public void Patterns_SearchByQuery_CompactReportsFanOutTruncation()
    {
        using var fx = CreatePatternFixture();
        Exec(fx.DbPath, """
            WITH RECURSIVE seq(n) AS (
                SELECT 1
                UNION ALL
                SELECT n + 1 FROM seq WHERE n < 30
            )
            INSERT INTO structural_facts
                (structural_fact_id, file_id, path, language, pattern_id, capture_name, node_kind,
                 containing_symbol_id, start_line, start_column, end_line, end_column, start_byte, end_byte,
                 confidence, metadata_json)
            SELECT
                printf('fact-fanout-%02d', n), 'file:src/Auth.cs', 'src/Auth.cs', 'csharp',
                printf('fanout.route.%02d.v1', n), 'route_call', 'invocation_expression', 'sym-auth',
                n, 1, n, 20, n * 20, n * 20 + 19, 1.0, '{"verb":"GET"}'
            FROM seq;
            """);
        var tool = new PatternsTool(new TestArtifactProvider(ArtifactFor(fx)), new PatternFactsReader());

        string output = tool.Patterns(operation: "search", query: "fanout.route", limit: 1);

        Assert.Contains(
            "pattern_id_fanout: considered=34 matched=30 returned=25 omitted=5 truncated=true",
            output,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Patterns_SummaryJson_DirectoryUsesFullParentPath()
    {
        using var fx = CreatePatternFixture();
        Exec(
            fx.DbPath,
            "UPDATE structural_facts SET path = 'src/features/routes/Auth.cs' WHERE structural_fact_id = 'fact-route';");
        var tool = new PatternsTool(new TestArtifactProvider(ArtifactFor(fx)), new PatternFactsReader());

        string json = tool.Patterns(
            operation: "summary",
            pattern_id: "aspnet.minimal_api.route.v1",
            group_by: "directory",
            format: "json");

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement group = Assert.Single(doc.RootElement.GetProperty("groups").EnumerateArray());
        Assert.Equal("src/features/routes", group.GetProperty("directory").GetString());
    }

    [Fact]
    public void Patterns_SummaryJson_TopDirectoryIsExplicit()
    {
        using var fx = CreatePatternFixture();
        Exec(
            fx.DbPath,
            "UPDATE structural_facts SET path = 'src/features/routes/Auth.cs' WHERE structural_fact_id = 'fact-route';");
        var tool = new PatternsTool(new TestArtifactProvider(ArtifactFor(fx)), new PatternFactsReader());

        string json = tool.Patterns(
            operation: "summary",
            pattern_id: "aspnet.minimal_api.route.v1",
            group_by: "top_directory",
            format: "json");

        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal("top_directory", doc.RootElement.GetProperty("group_by").GetString());
        JsonElement group = Assert.Single(doc.RootElement.GetProperty("groups").EnumerateArray());
        Assert.Equal("src", group.GetProperty("directory").GetString());
    }

    [Fact]
    public void Patterns_SearchByQuery_JsonEmptyAfterFilters_IncludesReasonAndActiveFilters()
    {
        using var fx = CreatePatternFixture();
        var tool = new PatternsTool(new TestArtifactProvider(ArtifactFor(fx)), new PatternFactsReader());

        string json = tool.Patterns(
            operation: "search",
            query: "route",
            language: "razor",
            path: "Views/**",
            where: "verb=GET",
            limit: 10,
            format: "json");

        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal("filtered_out", doc.RootElement.GetProperty("empty_reason").GetString());
        Assert.Empty(doc.RootElement.GetProperty("matches").EnumerateArray());
        JsonElement activeFilters = doc.RootElement.GetProperty("active_filters");
        Assert.Equal("razor", activeFilters.GetProperty("language").GetString());
        Assert.Equal("Views/**", activeFilters.GetProperty("path").GetString());
        JsonElement where = Assert.Single(activeFilters.GetProperty("where").EnumerateArray());
        Assert.Equal("verb", where.GetProperty("key").GetString());
        Assert.Equal("GET", where.GetProperty("value").GetString());
    }

    [Fact]
    public void Patterns_SearchByQuery_RecordsQueryInTelemetry()
    {
        using var fx = CreatePatternFixture();
        string telemetryDb = Path.Combine(Path.GetDirectoryName(fx.DbPath)!, "telemetry.db");
        var tool = new PatternsTool(new TestArtifactProvider(ArtifactFor(fx)), new PatternFactsReader());

        using (var ledger = TelemetryLedger.Open(telemetryDb, "workspace-1", Path.GetDirectoryName(Path.GetDirectoryName(fx.DbPath))!))
        {
            using var scope = ledger.Measure("patterns", op: null);
            string output = tool.Patterns(operation: "search", query: "route", limit: 10);
            Assert.Contains("aspnet.minimal_api.route.v1", output);
        }

        var row = ReadTelemetryOpMetadata(telemetryDb);
        Assert.Equal("search", row.Op);
        Assert.Equal("ok", row.Outcome);
        using JsonDocument doc = JsonDocument.Parse(row.MetadataJson);
        Assert.True(doc.RootElement.GetProperty("has_query").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("has_pattern_id").GetBoolean());
    }

    [Fact]
    public void Patterns_CompactExplicitWorkspace_DefaultsEnsureFreshAndAddsBanner()
    {
        using var fx = CreatePatternFixture();
        WorkspaceArtifactContext current = ArtifactFor(fx);
        WorkspaceArtifactContext target = current with
        {
            WorkspaceId = "target-ws",
            WorkspaceRoot = "/repo/target",
            DisplayId = "target",
        };
        var provider = new TestArtifactProvider(current, ("target-ws", target));
        var tool = new PatternsTool(provider, new PatternFactsReader());

        string output = tool.Patterns(operation: "list", workspace_id: "target-ws");

        Assert.Equal("target-ws", provider.LastWorkspaceId);
        Assert.True(provider.LastEnsureFresh);
        Assert.StartsWith("workspace: target", output, StringComparison.Ordinal);
        Assert.Contains("# patterns", output, StringComparison.Ordinal);
        Assert.Contains("htmx.attribute.v1", output, StringComparison.Ordinal);
    }

    private static WorkspaceArtifactContext ArtifactFor(JulieDbFixture fixture) =>
        new(
            IndexDbPath: fixture.DbPath,
            WorkspaceId: "workspace-1",
            WorkspaceRoot: Path.GetDirectoryName(Path.GetDirectoryName(fixture.DbPath))!,
            Revision: 1,
            IndexFresh: true,
            FreshnessStatus: "current",
            WarningText: null,
            DisplayId: "workspace");

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
                new JulieDbFixture.SymbolRow("sym-doc", "Readme", "document", "markdown",
                    "docs/README.md", null, 1, null),
            },
            fileContent: new Dictionary<string, string>
            {
                ["Views/Orders.cshtml"] = "<button hx-get=\"/orders\" hx-trigger=\"click\"></button>\n",
                ["public/index.html"] = "<button hx-post=\"/orders\"></button>\n",
                ["src/Auth.cs"] = "[Authorize]\npublic class Auth {}\n",
                ["docs/README.md"] = "# Project Docs\n",
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
                 1, 1, 1, 12, 0, 11, 0.75, '{"name":"Authorize"}'),
                ('fact-route', 'file:src/Auth.cs', 'src/Auth.cs', 'csharp',
                 'aspnet.minimal_api.route.v1', 'route_call', 'invocation_expression', 'sym-auth',
                 3, 1, 3, 30, 36, 65, 1.0,
                 '{"query_family":"framework","framework":"aspnet","route_template":"/orders","pattern_version":1,"verb":"GET"}'),
                ('fact-markdown-heading', 'file:docs/README.md', 'docs/README.md', 'markdown',
                 'markdown.heading.v1', 'heading', 'atx_heading', 'sym-doc',
                 1, 1, 1, 14, 0, 13, 1.0, '{"text":"Project Docs","level":1}');
            """);
        return fx;
    }

    [Fact]
    public void Patterns_List_RejectsQuery()
    {
        using var fx = CreatePatternFixture();
        var tool = new PatternsTool(new TestArtifactProvider(ArtifactFor(fx)), new PatternFactsReader());

        string output = tool.Patterns(operation: "list", query: "route");

        Assert.Contains("query is only supported for search", output, StringComparison.Ordinal);
        Assert.Contains("diagnostic_code=invalid_request", output, StringComparison.Ordinal);
        Assert.Contains("diagnostic_class=refusal", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Patterns_SearchJsonEmpty_IncludesNearMatchesAndEmptyReason()
    {
        using var fx = CreatePatternFixture();
        var tool = new PatternsTool(new TestArtifactProvider(ArtifactFor(fx)), new PatternFactsReader());

        string json = tool.Patterns(operation: "search", pattern_id: "aspnet.route.v1", format: "json");

        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal("no_such_pattern_id", doc.RootElement.GetProperty("empty_reason").GetString());
        Assert.Contains(
            doc.RootElement.GetProperty("near_matches").EnumerateArray().Select(static value => value.GetString()),
            match => match == "aspnet.minimal_api.route.v1");
        Assert.Contains(
            doc.RootElement.GetProperty("next_actions").EnumerateArray(),
            static action =>
                action.GetProperty("args").TryGetProperty("pattern_id", out JsonElement patternId)
                && patternId.GetString() == "aspnet.minimal_api.route.v1");
    }

    [Fact]
    public void Patterns_SummaryJson_GroupByFile_ReturnsPathRollups()
    {
        using var fx = CreatePatternFixture();
        var tool = new PatternsTool(new TestArtifactProvider(ArtifactFor(fx)), new PatternFactsReader());

        string json = tool.Patterns(
            operation: "summary",
            pattern_id: "htmx.attribute.v1",
            path: "Views/**",
            group_by: "file",
            format: "json");

        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal("file", doc.RootElement.GetProperty("group_by").GetString());
        JsonElement group = Assert.Single(doc.RootElement.GetProperty("groups").EnumerateArray());
        Assert.Equal("Views/Orders.cshtml", group.GetProperty("path").GetString());
    }

    [Theory]
    [InlineData("search", "not-key-value", null)]
    [InlineData("summary", null, "invalid")]
    public void Patterns_InvalidFilters_JsonAreTypedRefusals(
        string operation,
        string? where,
        string? groupBy)
    {
        using var fx = CreatePatternFixture();
        var tool = new PatternsTool(new TestArtifactProvider(ArtifactFor(fx)), new PatternFactsReader());

        using var document = JsonDocument.Parse(tool.Patterns(
            operation: operation,
            pattern_id: "htmx.attribute.v1",
            where: where,
            group_by: groupBy,
            format: "json"));
        JsonElement diagnostic = document.RootElement.GetProperty("diagnostic");

        Assert.Equal("invalid_request", diagnostic.GetProperty("code").GetString());
        Assert.Equal("refusal", diagnostic.GetProperty("class").GetString());
        Assert.Equal("empty", diagnostic.GetProperty("outcome").GetString());
    }

    [Fact]
    public void Patterns_InvalidFacetAndOversizedInputs_AreTypedRefusals()
    {
        using var fx = CreatePatternFixture();
        var tool = new PatternsTool(new TestArtifactProvider(ArtifactFor(fx)), new PatternFactsReader());

        string[] outputs =
        [
            tool.Patterns(operation: "summary", facet: "xml:lang", format: "json"),
            tool.Patterns(operation: "search", pattern_id: new string('p', 513), format: "json"),
            tool.Patterns(operation: "search", query: new string('q', 1_001), format: "json"),
            tool.Patterns(operation: "summary", language: new string('l', 129), format: "json"),
            tool.Patterns(operation: "summary", path: new string('p', 2_049), format: "json"),
            tool.Patterns(operation: "summary", where: "key=" + new string('v', 2_045), format: "json"),
            tool.Patterns(operation: "summary", facet: new string('f', 257), format: "json"),
        ];

        foreach (string output in outputs)
        {
            using var document = JsonDocument.Parse(output);
            JsonElement diagnostic = document.RootElement.GetProperty("diagnostic");
            Assert.Equal("invalid_request", diagnostic.GetProperty("code").GetString());
            Assert.Equal("refusal", diagnostic.GetProperty("class").GetString());
            Assert.Equal("empty", diagnostic.GetProperty("outcome").GetString());
        }
    }

    [Fact]
    public void Patterns_QueryNoMatchManyFilters_StaysBoundedAndRejectsFilterOverflow()
    {
        using var fx = CreatePatternFixture();
        var tool = new PatternsTool(new TestArtifactProvider(ArtifactFor(fx)), new PatternFactsReader());
        string acceptedWhere = string.Join(
            ';',
            Enumerable.Range(1, 16).Select(static index => $"key{index}={new string('v', 100)}"));

        string bounded = tool.Patterns(
            operation: "search",
            query: new string('q', 1_000),
            path: new string('p', 2_048),
            where: acceptedWhere,
            format: "json");

        Assert.True(Encoding.UTF8.GetByteCount(bounded) <= ToolOutputBudget.PatternsMcpMaxBytes);
        using (JsonDocument boundedDocument = JsonDocument.Parse(bounded))
        {
            Assert.Equal("query_no_match", boundedDocument.RootElement.GetProperty("empty_reason").GetString());
            Assert.Equal(16, boundedDocument.RootElement.GetProperty("active_filters")
                .GetProperty("where").GetArrayLength());
        }

        string boundedCompact = tool.Patterns(
            operation: "search",
            query: new string('q', 1_000),
            path: new string('p', 2_048),
            where: acceptedWhere);
        Assert.True(Encoding.UTF8.GetByteCount(boundedCompact) <= ToolOutputBudget.PatternsMcpMaxBytes);
        Assert.Contains("where=key1=", boundedCompact, StringComparison.Ordinal);
        Assert.Contains("where=key16=", boundedCompact, StringComparison.Ordinal);

        string rejectedWhere = acceptedWhere + ";overflow=value";
        using JsonDocument rejectedDocument = JsonDocument.Parse(tool.Patterns(
            operation: "search",
            query: "no-match",
            where: rejectedWhere,
            format: "json"));
        JsonElement diagnostic = rejectedDocument.RootElement.GetProperty("diagnostic");
        Assert.Equal("invalid_request", diagnostic.GetProperty("code").GetString());
        Assert.Equal("refusal", diagnostic.GetProperty("class").GetString());
    }

    [Fact]
    public void Patterns_QueryNoMatchOversizedArtifactMetadata_IsTypedRefusal()
    {
        using var fx = CreatePatternFixture();
        Exec(fx.DbPath, """
            WITH RECURSIVE seq(n) AS (
                SELECT 1
                UNION ALL
                SELECT n + 1 FROM seq WHERE n < 5
            )
            INSERT INTO structural_facts
                (structural_fact_id, file_id, path, language, pattern_id, capture_name, node_kind,
                 containing_symbol_id, start_line, start_column, end_line, end_column, start_byte, end_byte,
                 confidence, metadata_json)
            SELECT
                printf('fact-oversized-pattern-%d', n), 'file:src/Auth.cs', 'src/Auth.cs', 'csharp',
                printf('near.common.%s.%03d.v1', replace(hex(zeroblob(2500)), '00', 'x'), n),
                'route_call', 'invocation_expression', 'sym-auth',
                n, 1, n, 20, n * 20, n * 20 + 19, 1.0, '{"verb":"GET"}'
            FROM seq;
            """);
        var tool = new PatternsTool(new TestArtifactProvider(ArtifactFor(fx)), new PatternFactsReader());

        string json = tool.Patterns(
            operation: "search",
            query: "near.comon.typo.v1",
            format: "json");

        Assert.True(Encoding.UTF8.GetByteCount(json) <= ToolOutputBudget.PatternsMcpMaxBytes);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement diagnostic = document.RootElement.GetProperty("diagnostic");
        Assert.Equal("output_metadata_too_large", diagnostic.GetProperty("code").GetString());
        Assert.Equal("refusal", diagnostic.GetProperty("class").GetString());
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

    private sealed class TestArtifactProvider : IWorkspaceArtifactProvider
    {
        private readonly WorkspaceArtifactContext _current;
        private readonly Dictionary<string, WorkspaceArtifactContext> _targets;

        public TestArtifactProvider(
            WorkspaceArtifactContext current,
            params (string WorkspaceId, WorkspaceArtifactContext Context)[] targets)
        {
            _current = current;
            _targets = targets.ToDictionary(static target => target.WorkspaceId, static target => target.Context, StringComparer.Ordinal);
        }

        public string? LastWorkspaceId { get; private set; }
        public bool? LastEnsureFresh { get; private set; }

        public WorkspaceArtifactContext ResolveArtifact(string? workspaceId, bool ensureFresh)
        {
            LastWorkspaceId = workspaceId;
            LastEnsureFresh = ensureFresh;

            if (workspaceId is null)
                return _current;

            return _targets.TryGetValue(workspaceId, out WorkspaceArtifactContext? context)
                ? context
                : throw new KeyNotFoundException(workspaceId);
        }
    }
}
