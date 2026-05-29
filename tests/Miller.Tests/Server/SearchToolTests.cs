using System.Text.Json;
using Miller.Indexing;
using Miller.Server.Tools;
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

    // A fixture proving the FULL cross-language predicate (decision-4): exclude_tests must hide BOTH a
    // path-flagged test row (tests/auth/AuthServiceTests.cs — no metadata) AND a julie-is_test row whose path
    // is NOT test-shaped (src/auth/AuthHelper.cs carrying {"is_test":true}, a [Fact] method julie flagged).
    // The third row is the one the path-only filter would miss; it pins the sym.IsTest branch of the predicate.
    private static JulieDbFixture FixtureWithTestPaths() => JulieDbFixture.Create(26, "1", new[]
    {
        new JulieDbFixture.SymbolRow("a0001122334455667788990a1b2c3d4e", "AuthService", "class", "csharp",
            "src/auth/AuthService.cs", "public class AuthService", 1, null),
        new JulieDbFixture.SymbolRow("b0001122334455667788990a1b2c3d4e", "AuthServiceTests", "class", "csharp",
            "tests/auth/AuthServiceTests.cs", "public class AuthServiceTests", 1, null),
        // julie-flagged test method in a PRODUCTION-named path: only sym.IsTest can hide this, not the path rule.
        new JulieDbFixture.SymbolRow("c0001122334455667788990a1b2c3d4e", "AuthService_Smoke", "method", "csharp",
            "src/auth/AuthHelper.cs", "public void AuthService_Smoke()", 1, null)
        { Metadata = "{\"is_test\":true}" },
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
        using var fx = JulieDbFixture.Create(26, "1", rows);
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
}
