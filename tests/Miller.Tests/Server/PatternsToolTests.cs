using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Server.Tools;
using Miller.Server.Workspaces;
using Miller.Tests.Indexing;
using Xunit;

namespace Miller.Tests.Server;

public sealed class PatternsToolTests
{
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
    public void Patterns_SearchWithoutPattern_ReturnsCleanFailure()
    {
        using var fx = CreatePatternFixture();
        var tool = new PatternsTool(new TestArtifactProvider(ArtifactFor(fx)), new PatternFactsReader());

        string output = tool.Patterns(operation: "search", format: "json");

        Assert.StartsWith("patterns failed:", output, StringComparison.Ordinal);
        Assert.Contains("pattern_id", output, StringComparison.Ordinal);
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
            },
            fileContent: new Dictionary<string, string>
            {
                ["Views/Orders.cshtml"] = "<button hx-get=\"/orders\" hx-trigger=\"click\"></button>\n",
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
