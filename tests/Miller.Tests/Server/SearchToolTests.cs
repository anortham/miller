using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Core.Search;
using Miller.Indexing;
using Miller.Server.Telemetry;
using Miller.Server.Tools;
using Miller.Server.Workspaces;
using Miller.Tests;
using Miller.Tests.Indexing;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the <c>search</c> tool's behavior (M2 §4) against the M1 synthesized fixture index: compact + json
/// rendering, <c>limit</c> + the <c>… N more</c> overflow note (never silently drop), the <c>exclude_tests</c>
/// tri-state (null/true/false), empty → <c>No results.</c>, and ordering preserved (the renderer must NOT
/// re-sort — Core's score-DESC ordering is authoritative). Exercises <see cref="SearchTool.Run"/> directly
/// (the pure core the MCP method delegates to), so it stays in the fast suite.
/// </summary>
public sealed class SearchToolTests
{
    private static MillerRepositoryIndex BuildIndex(JulieDbFixture fx) =>
        MillerRepositoryIndex.Build(SqliteSymbolReader.Read(fx.DbPath));

    private static IContentSearchIndex ContentIndex(params (string Path, string Text)[] docs) =>
        ContentSearchProjection.Build(
            docs.Select((d, i) => new ContentDocument(i, d.Path, d.Text)).ToList());

    private static JulieDbFixture FixtureWithSymbol(string workspaceId, string symbolName) =>
        JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, new[]
        {
            new JulieDbFixture.SymbolRow(
                Guid.NewGuid().ToString("N"),
                symbolName,
                "class",
                "csharp",
                $"src/{symbolName}.cs",
                $"public class {symbolName}",
                1,
                ParentId: null),
        }, workspaceId: workspaceId);

    private static (string? WorkspaceId, string? WorkspaceRoot, bool? IndexFresh) ReadTelemetryRow(string dbPath)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT workspace_id, workspace_root, index_fresh FROM tool_telemetry LIMIT 1;";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read(), "expected one telemetry row");
        return (
            r.IsDBNull(0) ? null : r.GetString(0),
            r.IsDBNull(1) ? null : r.GetString(1),
            r.IsDBNull(2) ? null : r.GetInt64(2) == 1);
    }

    // A fixture proving the FULL cross-language predicate (decision-4): exclude_tests must hide BOTH a
    // path-flagged test row (tests/auth/AuthServiceTests.cs — not is_test) AND a julie-is_test row whose path
    // is NOT test-shaped (src/auth/AuthHelper.cs with the typed is_test column set, a [Fact] method julie flagged).
    // The third row is the one the path-only filter would miss; it pins the sym.IsTest branch of the predicate.
    private static JulieDbFixture FixtureWithTestPaths() => JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, new[]
    {
        new JulieDbFixture.SymbolRow("a0001122334455667788990a1b2c3d4e", "AuthService", "class", "csharp",
            "src/auth/AuthService.cs", "public class AuthService", 1, null),
        new JulieDbFixture.SymbolRow("b0001122334455667788990a1b2c3d4e", "AuthServiceTests", "class", "csharp",
            "tests/auth/AuthServiceTests.cs", "public class AuthServiceTests", 1, null),
        // julie-flagged test method in a PRODUCTION-named path: only sym.IsTest can hide this, not the path rule.
        // v1 carries the test signal in the typed is_test column (NOT a metadata-JSON parse).
        new JulieDbFixture.SymbolRow("c0001122334455667788990a1b2c3d4e", "AuthService_Smoke", "method", "csharp",
            "src/auth/AuthHelper.cs", "public void AuthService_Smoke()", 1, null)
        { IsTest = true },
    });

    [Fact]
    public void Run_Compact_RendersOneLinePerHit_WithNameKindFileLine()
    {
        using var fx = JulieDbFixture.CreateDefault();
        var index = BuildIndex(fx);

        string output = SearchTool.Run(index, "parseToken", SearchToolMode.Auto, limit: 10,
            excludeTests: null, json: false, out int count);

        Assert.True(count >= 1);
        var first = output.Split('\n')[0];
        Assert.Contains("parseToken", first);
        Assert.Contains("function", first);
        Assert.Contains("auth/token.ts:3", first);
        // Compact output has no blank lines.
        Assert.DoesNotContain("\n\n", output);
    }

    [Fact]
    public void Run_Empty_ReturnsNoResultsSentinel()
    {
        using var fx = JulieDbFixture.CreateDefault();
        var index = BuildIndex(fx);

        string output = SearchTool.Run(index, "ZZTopNothingMatches", SearchToolMode.Auto, limit: 10,
            excludeTests: null, json: false, out int count);

        Assert.Equal(0, count);
        Assert.Equal("No results.", output.Trim());
    }

    [Fact]
    public void Run_Json_IsAParseableArrayWithTheExpectedShape()
    {
        using var fx = JulieDbFixture.CreateDefault();
        var index = BuildIndex(fx);

        string output = SearchTool.Run(index, "GetUser", SearchToolMode.Auto, limit: 10,
            excludeTests: null, json: true, out int count);

        Assert.True(count >= 1);
        using var doc = JsonDocument.Parse(output);
        var arr = doc.RootElement;
        Assert.Equal(JsonValueKind.Array, arr.ValueKind);
        var first = arr[0];
        Assert.Equal("GetUser", first.GetProperty("name").GetString());
        Assert.Equal("method", first.GetProperty("kind").GetString());
        Assert.Equal("auth/UserService.cs", first.GetProperty("file").GetString());
        Assert.Equal(5, first.GetProperty("line").GetInt32());
        Assert.False(string.IsNullOrEmpty(first.GetProperty("symbol_id").GetString()));
        Assert.True(first.TryGetProperty("score", out _));
    }

    [Fact]
    public void Run_OverLimit_AppendsMoreNote_AndDoesNotDrop()
    {
        // 5 symbols all share a token; limit=2 must show 2 rows + a "… N more" note.
        var rows = Enumerable.Range(0, 5).Select(i => new JulieDbFixture.SymbolRow(
            $"{i:x32}".PadLeft(32, '0')[..32], $"Widget{i}", "class", "csharp",
            $"src/Widget{i}.cs", $"public class Widget{i}", 1, null)).ToArray();
        using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, rows);
        var index = BuildIndex(fx);

        string output = SearchTool.Run(index, "widget", SearchToolMode.Auto, limit: 2,
            excludeTests: false, json: false, out int count);

        // count is the number actually rendered (the page), the note reports the remainder.
        Assert.Equal(2, count);
        Assert.Contains("more", output);
        Assert.Contains("raise limit", output);
    }

    [Fact]
    public void Run_ExcludeTestsTrue_AlwaysHidesTestPaths()
    {
        using var fx = FixtureWithTestPaths();
        var index = BuildIndex(fx);

        string output = SearchTool.Run(index, "AuthService", SearchToolMode.Auto, limit: 10,
            excludeTests: true, json: false, out int count);

        Assert.Contains("src/auth/AuthService.cs", output);
        Assert.DoesNotContain("tests/auth/AuthServiceTests.cs", output);
    }

    [Fact]
    public void Run_ExcludeTestsTrue_HidesJulieIsTestRow_InNonTestPath()
    {
        // The sym.IsTest branch of the predicate: a [Fact]-style method julie flagged is_test, living in a
        // PRODUCTION-named file (src/auth/AuthHelper.cs). The path rule would keep it; sym.IsTest must hide it.
        // This case fails if SearchTool consults only IsTestPath.Check and ignores the persisted is_test signal.
        using var fx = FixtureWithTestPaths();
        var index = BuildIndex(fx);

        string output = SearchTool.Run(index, "AuthService", SearchToolMode.Auto, limit: 10,
            excludeTests: true, json: false, out _);

        Assert.DoesNotContain("AuthService_Smoke", output);
        Assert.DoesNotContain("src/auth/AuthHelper.cs", output);
        // The non-test production symbol is still present.
        Assert.Contains("src/auth/AuthService.cs", output);
    }

    [Fact]
    public void Run_ExcludeTestsFalse_AlwaysIncludesTestPaths()
    {
        using var fx = FixtureWithTestPaths();
        var index = BuildIndex(fx);

        string output = SearchTool.Run(index, "AuthService", SearchToolMode.Auto, limit: 10,
            excludeTests: false, json: false, out int count);

        Assert.Contains("src/auth/AuthService.cs", output);
        Assert.Contains("tests/auth/AuthServiceTests.cs", output);
        // exclude_tests=false includes even the julie-is_test row.
        Assert.Contains("AuthService_Smoke", output);
    }

    [Fact]
    public void Run_ExcludeTestsNull_HidesTestPaths_ForNaturalLanguagePhrase()
    {
        using var fx = FixtureWithTestPaths();
        var index = BuildIndex(fx);

        // Multi-word NL phrase, no test/def intent → default hides test paths.
        string output = SearchTool.Run(index, "auth service", SearchToolMode.Auto, limit: 10,
            excludeTests: null, json: false, out _);

        Assert.Contains("src/auth/AuthService.cs", output);
        Assert.DoesNotContain("tests/auth/AuthServiceTests.cs", output);
    }

    [Fact]
    public void Run_ExcludeTestsNull_KeepsTestPaths_ForSingleIdentifierQuery()
    {
        using var fx = FixtureWithTestPaths();
        var index = BuildIndex(fx);

        // A single identifier-ish token is NOT an NL phrase → null defaults to include (don't auto-hide).
        string output = SearchTool.Run(index, "AuthService", SearchToolMode.Auto, limit: 10,
            excludeTests: null, json: false, out _);

        Assert.Contains("src/auth/AuthService.cs", output);
        Assert.Contains("tests/auth/AuthServiceTests.cs", output);
    }

    [Fact]
    public void Run_ExcludeTestsNull_KeepsTestPaths_WhenPhraseHasTestIntent()
    {
        using var fx = FixtureWithTestPaths();
        var index = BuildIndex(fx);

        // An NL phrase that explicitly mentions "test" intent → do not auto-hide test paths.
        string output = SearchTool.Run(index, "auth service test", SearchToolMode.Auto, limit: 10,
            excludeTests: null, json: false, out _);

        Assert.Contains("tests/auth/AuthServiceTests.cs", output);
    }

    [Fact]
    public void Run_PreservesIndexOrdering_DoesNotReSort()
    {
        using var fx = JulieDbFixture.CreateDefault();
        var index = BuildIndex(fx);

        // Compare the tool's rendered order against the raw index order for the same query.
        var rawOrder = index.Search("http", limit: 20)
            .Select(h => index.Resolve(h.Document.DocId).Name)
            .ToList();

        string output = SearchTool.Run(index, "http", SearchToolMode.Auto, limit: 20,
            excludeTests: false, json: false, out _);

        var renderedNames = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !l.StartsWith('…') && !l.Contains("more"))
            .Select(l => l.Split("  ", StringSplitOptions.RemoveEmptyEntries)[0].Trim())
            .ToList();

        Assert.Equal(rawOrder, renderedNames);
    }

    [Fact]
    public void Search_ExplicitWorkspaceId_DefaultsEnsureFreshTrue_AndRoutesToTargetIndex()
    {
        using var current = FixtureWithSymbol("current-ws", "CurrentOnly");
        using var target = FixtureWithSymbol("target-ws", "TargetOnly");
        string currentRoot = Path.Combine(Path.GetTempPath(), "miller-current-" + Guid.NewGuid().ToString("N"));
        string targetRoot = Path.Combine(Path.GetTempPath(), "miller-target-" + Guid.NewGuid().ToString("N"));
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(BuildIndex(current), current.DbPath, "current-ws", currentRoot),
            ("target-ws", ReadToolRoutingTestSupport.ContextFor(BuildIndex(target), target.DbPath, "target-ws", targetRoot)));
        var tool = new SearchTool(provider, provider);

        string output = tool.Search("TargetOnly", workspace_id: "target-ws");

        Assert.Equal("target-ws", provider.LastWorkspaceId);
        Assert.True(provider.LastEnsureFresh);
        Assert.StartsWith("workspace: target-ws ", output);
        Assert.Contains(targetRoot, output);
        Assert.Contains("TargetOnly", output);
    }

    [Fact]
    public void Search_EnsureFreshFalse_PassesThrough_AndTelemetryUsesProviderWorkspaceAndFreshness()
    {
        using var current = FixtureWithSymbol("current-ws", "CurrentOnly");
        using var target = FixtureWithSymbol("target-ws", "TargetOnly");
        string dir = Path.Combine(Path.GetTempPath(), "miller-search-routing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string telemetryDb = Path.Combine(dir, "telemetry.db");
        string currentRoot = Path.Combine(dir, "current");
        string targetRoot = Path.Combine(dir, "target");
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(BuildIndex(current), current.DbPath, "current-ws", currentRoot),
            ("target-ws", ReadToolRoutingTestSupport.ContextFor(
                BuildIndex(target),
                target.DbPath,
                "target-ws",
                targetRoot,
                indexFresh: false,
                freshnessStatus: "loaded_existing")));
        var tool = new SearchTool(provider, provider);

        try
        {
            using (var ledger = TelemetryLedger.Open(telemetryDb, workspaceId: "current-ws", currentRoot))
            {
                using var scope = ledger.Measure("search", op: "auto");
                string output = tool.Search("TargetOnly", workspace_id: "target-ws", ensure_fresh: false);

                Assert.Equal("target-ws", provider.LastWorkspaceId);
                Assert.False(provider.LastEnsureFresh);
                Assert.Contains("freshness: loaded_existing", output);
                Assert.Contains("TargetOnly", output);
            }

            var row = ReadTelemetryRow(telemetryDb);
            Assert.Equal("target-ws", row.WorkspaceId);
            Assert.Equal(targetRoot, row.WorkspaceRoot);
            Assert.False(row.IndexFresh);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    // ----- mode=content (phase 3) -----

    [Fact]
    public void RunContent_Compact_RendersPathLineAndSnippet()
    {
        var index = ContentIndex(
            ("docs/guide.md", "# Guide\nThe freshness gate verifies blake3 before reading.\nMore text.\n"));

        string output = SearchTool.RunContent(index, "freshness", limit: 10, json: false, out int count);

        Assert.Equal(1, count);
        Assert.Contains("docs/guide.md:2", output); // path + best line (1-based)
        Assert.Contains("The freshness gate verifies blake3", output); // snippet window
    }

    [Fact]
    public void RunContent_Empty_ReturnsNoResultsSentinel()
    {
        var index = ContentIndex(("docs/guide.md", "nothing relevant on this page"));

        string output = SearchTool.RunContent(index, "zzzznotpresent", limit: 10, json: false, out int count);

        Assert.Equal(0, count);
        Assert.Equal("No results.", output.Trim());
    }

    [Fact]
    public void RunContent_Json_HasContentShape_NeverFakeSymbols()
    {
        var index = ContentIndex(("docs/guide.md", "alpha freshness beta\n"));

        string output = SearchTool.RunContent(index, "freshness", limit: 10, json: true, out int count);

        Assert.True(count >= 1);
        using var doc = JsonDocument.Parse(output);
        var first = doc.RootElement[0];
        Assert.Equal("docs/guide.md", first.GetProperty("file").GetString());
        Assert.Equal(1, first.GetProperty("line").GetInt32());
        Assert.True(first.TryGetProperty("score", out _));
        Assert.False(string.IsNullOrEmpty(first.GetProperty("snippet").GetString()));
        // Content hits are a distinct result kind — NOT fake symbols.
        Assert.False(first.TryGetProperty("symbol_id", out _));
        Assert.False(first.TryGetProperty("kind", out _));
        Assert.False(first.TryGetProperty("name", out _));
    }

    [Fact]
    public void RunContent_OverLimit_AppendsMoreNote_AndDoesNotDrop()
    {
        var docs = Enumerable.Range(0, 5)
            .Select(i => ($"docs/d{i}.md", $"widget content number {i}"))
            .ToArray();
        var index = ContentIndex(docs);

        string output = SearchTool.RunContent(index, "widget", limit: 2, json: false, out int count);

        Assert.Equal(2, count);
        Assert.Contains("more", output);
        Assert.Contains("raise limit", output);
    }

    [Fact]
    public void Search_ModeContent_RoutesToContentProvider_AndRendersContentHits()
    {
        using var current = FixtureWithSymbol("current-ws", "CurrentOnly");
        string currentRoot = Path.Combine(Path.GetTempPath(), "miller-current-" + Guid.NewGuid().ToString("N"));
        string targetRoot = Path.Combine(Path.GetTempPath(), "miller-target-" + Guid.NewGuid().ToString("N"));
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(BuildIndex(current), current.DbPath, "current-ws", currentRoot),
            currentContent: ReadToolRoutingTestSupport.ContentContextFor(
                ContentIndex(("docs/none.md", "irrelevant")), current.DbPath, "current-ws", currentRoot),
            contentTargets: new[]
            {
                ("target-ws", ReadToolRoutingTestSupport.ContentContextFor(
                    ContentIndex(("docs/guide.md", "# Guide\nThe freshness gate verifies blake3.\n")),
                    "target.db", "target-ws", targetRoot)),
            });
        var tool = new SearchTool(provider, provider);

        string output = tool.Search("freshness", mode: "content", workspace_id: "target-ws");

        Assert.Equal("target-ws", provider.LastWorkspaceId);
        Assert.True(provider.LastEnsureFresh); // explicit workspace_id defaults ensure_fresh=true
        Assert.Equal(1, provider.ContentSearchResolveCount);
        Assert.Equal(0, provider.SymbolSearchResolveCount); // content mode never touches the symbol provider
        Assert.StartsWith("workspace: target-ws ", output);
        Assert.Contains(targetRoot, output);
        Assert.Contains("docs/guide.md:2", output);
        Assert.Contains("The freshness gate verifies blake3", output);
    }

    [Fact]
    public void Search_ModeDocs_AliasesContent()
    {
        using var current = FixtureWithSymbol("current-ws", "CurrentOnly");
        string currentRoot = Path.Combine(Path.GetTempPath(), "miller-current-" + Guid.NewGuid().ToString("N"));
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(BuildIndex(current), current.DbPath, "current-ws", currentRoot),
            currentContent: ReadToolRoutingTestSupport.ContentContextFor(
                ContentIndex(("docs/readme.md", "alpha docsalias beta")), current.DbPath, "current-ws", currentRoot),
            contentTargets: Array.Empty<(string, WorkspaceContentSearchContext)>());
        var tool = new SearchTool(provider, provider);

        string output = tool.Search("docsalias", mode: "docs");

        Assert.Equal(1, provider.ContentSearchResolveCount);
        Assert.Equal(0, provider.SymbolSearchResolveCount);
        Assert.Contains("docs/readme.md", output);
    }

    [Fact]
    public void Search_ModeContent_ExcludeTestsIsNoOp()
    {
        using var current = FixtureWithSymbol("current-ws", "CurrentOnly");
        string currentRoot = Path.Combine(Path.GetTempPath(), "miller-current-" + Guid.NewGuid().ToString("N"));
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(BuildIndex(current), current.DbPath, "current-ws", currentRoot),
            currentContent: ReadToolRoutingTestSupport.ContentContextFor(
                ContentIndex(("docs/guide.md", "alpha freshness beta")), current.DbPath, "current-ws", currentRoot),
            contentTargets: Array.Empty<(string, WorkspaceContentSearchContext)>());
        var tool = new SearchTool(provider, provider);

        string withExclude = tool.Search("freshness", mode: "content", exclude_tests: true);
        string withoutExclude = tool.Search("freshness", mode: "content", exclude_tests: false);

        Assert.Equal(withExclude, withoutExclude); // exclude_tests does not filter content results
        Assert.Contains("docs/guide.md", withExclude);
    }

    [Fact]
    public void Search_NonContentMode_DoesNotResolveContentProvider()
    {
        using var current = FixtureWithSymbol("current-ws", "CurrentOnly");
        string currentRoot = Path.Combine(Path.GetTempPath(), "miller-current-" + Guid.NewGuid().ToString("N"));
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(BuildIndex(current), current.DbPath, "current-ws", currentRoot));
        var tool = new SearchTool(provider, provider);

        string output = tool.Search("CurrentOnly"); // mode defaults to auto

        Assert.Equal(0, provider.ContentSearchResolveCount);
        Assert.Equal(1, provider.SymbolSearchResolveCount);
        Assert.Contains("CurrentOnly", output);
    }
}
