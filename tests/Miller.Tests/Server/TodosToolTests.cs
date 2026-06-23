using System.Text.Json;
using Miller.Core.Search;
using Miller.Indexing;
using Miller.Server.Tools;
using Miller.Server.Workspaces;
using Miller.Tests;
using Miller.Tests.Indexing;
using Xunit;

namespace Miller.Tests.Server;

public sealed class TodosToolTests
{
    [Fact]
    public void Todos_DefaultMarkers_ReturnsCommentMarkersWithContainingSymbol()
    {
        using var fx = FixtureWithSymbol("current-ws", "CurrentType");
        var index = new StubRegionSearchIndex(
            Hit("src/A.cs", 12, "comment", "// TODO move this", "TODO", "A.Run"),
            Hit("src/B.cs", 18, "doc_comment", "/// FIXME document edge case", "FIXME", "B.Build"),
            Hit("src/C.cs", 22, "string_literal", "\"TODO not a comment\"", "TODO", "C.Text"));
        var provider = ProviderFor(fx, index);
        var tool = new TodosTool(provider);

        string output = tool.Todos();

        Assert.Equal(1, provider.RegionSearchResolveCount);
        Assert.Contains("src/A.cs:12  TODO  comment  A.Run", output);
        Assert.Contains("// TODO move this", output);
        Assert.Contains("src/B.cs:18  FIXME  doc_comment  B.Build", output);
        Assert.DoesNotContain("string_literal", output);
    }

    [Fact]
    public void Todos_MarkersFilter_LimitsSearchToRequestedMarkers()
    {
        using var fx = FixtureWithSymbol("current-ws", "CurrentType");
        var index = new StubRegionSearchIndex(
            Hit("src/A.cs", 12, "comment", "// TODO move this", "TODO", "A.Run"),
            Hit("src/B.cs", 18, "comment", "// HACK temporary bypass", "HACK", "B.Build"));
        var provider = ProviderFor(fx, index);
        var tool = new TodosTool(provider);

        string output = tool.Todos(markers: "hack");

        Assert.Contains("src/B.cs:18  HACK  comment  B.Build", output);
        Assert.DoesNotContain("TODO", output);
    }

    [Fact]
    public void Todos_AppliesFileAndLanguageFilters()
    {
        using var fx = FixtureWithSymbol("current-ws", "CurrentType");
        var index = new StubRegionSearchIndex(
            Hit("src/ui/A.ts", 7, "comment", "// TODO frontend", "TODO", "A", language: "typescript"),
            Hit("src/api/A.cs", 9, "comment", "// TODO backend", "TODO", "Api.Handle", language: "csharp"));
        var provider = ProviderFor(fx, index);
        var tool = new TodosTool(provider);

        string output = tool.Todos(file_pattern: "src/api/**", language: "csharp");

        Assert.Contains("src/api/A.cs:9  TODO  comment  Api.Handle", output);
        Assert.DoesNotContain("frontend", output);
    }

    [Fact]
    public void Todos_Json_IncludesMarkerLocationAndContainingSymbol()
    {
        using var fx = FixtureWithSymbol("current-ws", "CurrentType");
        var index = new StubRegionSearchIndex(
            Hit("src/A.cs", 12, "comment", "// XXX remove this fallback", "XXX", "A.Run"));
        var provider = ProviderFor(fx, index);
        var tool = new TodosTool(provider);

        using JsonDocument doc = JsonDocument.Parse(tool.Todos(format: "json"));
        JsonElement item = Assert.Single(doc.RootElement.EnumerateArray());

        Assert.Equal("XXX", item.GetProperty("marker").GetString());
        Assert.Equal("src/A.cs", item.GetProperty("file").GetString());
        Assert.Equal(12, item.GetProperty("line").GetInt32());
        Assert.Equal("comment", item.GetProperty("kind").GetString());
        Assert.Equal("A.Run", item.GetProperty("containing_symbol_name").GetString());
        Assert.Equal("// XXX remove this fallback", item.GetProperty("snippet").GetString());
    }

    [Fact]
    public void Todos_WorkspaceId_RoutesToRegisteredWorkspaceAndRefreshesByDefault()
    {
        using var current = FixtureWithSymbol("current-ws", "CurrentType");
        using var target = FixtureWithSymbol("target-ws", "TargetType");
        var currentIndex = new StubRegionSearchIndex(
            Hit("src/Current.cs", 1, "comment", "// TODO current", "TODO", "CurrentType"));
        var targetIndex = new StubRegionSearchIndex(
            Hit("src/Target.cs", 2, "comment", "// TODO target", "TODO", "TargetType"));
        string currentRoot = TempRoot("current");
        string targetRoot = TempRoot("target");
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(BuildIndex(current), current.DbPath, "current-ws", currentRoot),
            ReadToolRoutingTestSupport.RegionContextFor(currentIndex, current.DbPath, "current-ws", currentRoot),
            new[]
            {
                ("target-ws", ReadToolRoutingTestSupport.RegionContextFor(
                    targetIndex, target.DbPath, "target-ws", targetRoot)),
            });
        var tool = new TodosTool(provider);

        string output = tool.Todos(workspace_id: "target-ws");

        Assert.Equal("target-ws", provider.LastWorkspaceId);
        Assert.True(provider.LastEnsureFresh);
        Assert.Contains("workspace: target-ws", output);
        Assert.Contains("src/Target.cs:2  TODO  comment  TargetType", output);
        Assert.DoesNotContain("current", output);
    }

    private static RecordingWorkspaceIndexProvider ProviderFor(JulieDbFixture fx, IRegionSearchIndex index)
    {
        string root = TempRoot("current");
        return new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(BuildIndex(fx), fx.DbPath, "current-ws", root),
            ReadToolRoutingTestSupport.RegionContextFor(index, fx.DbPath, "current-ws", root),
            regionTargets: Array.Empty<(string, WorkspaceRegionSearchContext)>());
    }

    private static JulieDbFixture FixtureWithSymbol(string workspaceId, string name) =>
        JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                new JulieDbFixture.SymbolRow(
                    workspaceId + "-sym",
                    name,
                    "class",
                    "csharp",
                    "src/" + name + ".cs",
                    "public class " + name,
                    1,
                    ParentId: null),
            });

    private static MillerRepositoryIndex BuildIndex(JulieDbFixture fx) =>
        MillerRepositoryIndex.Build(SqliteSymbolReader.Read(fx.DbPath));

    private static RegionSearchHit Hit(
        string path,
        int line,
        string kind,
        string text,
        string marker,
        string containingSymbol,
        string language = "csharp") =>
        new(
            path,
            Score: 2.0,
            line,
            kind,
            text,
            text,
            "region-" + marker.ToLowerInvariant() + "-" + line.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "sym-" + marker.ToLowerInvariant(),
            containingSymbol,
            language);

    private static string TempRoot(string name) =>
        Path.Combine(Path.GetTempPath(), "miller-todos-" + name + "-" + Guid.NewGuid().ToString("N"));

    private sealed class StubRegionSearchIndex : IRegionSearchIndex
    {
        private readonly IReadOnlyList<RegionSearchHit> _hits;

        public StubRegionSearchIndex(params RegionSearchHit[] hits) => _hits = hits;

        public int DocumentCount => _hits.Count;

        public long Revision { get; } = 1;

        public IReadOnlyList<RegionSearchHit> Search(
            string query,
            IReadOnlySet<string> kinds,
            int limit = 10,
            bool excludeTests = false) =>
            _hits
                .Where(hit => kinds.Contains(hit.Kind)
                    && hit.RawText.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Take(limit)
                .ToArray();
    }
}
