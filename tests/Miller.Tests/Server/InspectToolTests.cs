using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Server;
using Miller.Server.Resolution;
using Miller.Server.Tools;
using Miller.Server.Workspaces;
using Miller.Tests;
using Miller.Tests.Indexing;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the <c>inspect</c> tool (M2 §5) against the inspect fixture: file→symbols (kind filter, limit),
/// symbol→summary (signature + doc_comment via ReadDetail), symbol→full (children via parent_id, name-based
/// refs, one-hop callers/callees, body slice with graceful NULL degradation), ambiguous→candidates (never
/// pick-first), and an unknown path → a note (not an error). Exercises <see cref="InspectTool.Run"/> directly.
///
/// <para>Nudge precedence (compact only; JSON never carries one, and at most one <c>next:</c> line renders per
/// response): refs truncated at the full-depth <c>RefLimit</c> → <c>trace</c>, else ≥<c>ImpactHintMinReferences</c>
/// dependents on a non-test symbol → <c>impact</c>, else none. Truncation wins because those refs are otherwise
/// lost. The overview cap is deliberately NOT a trigger — its omitted line already points at <c>depth=full</c>,
/// which does recover them, so firing there would displace the impact nudge on every hot-symbol overview.</para>
/// </summary>
public sealed class InspectToolTests
{
    private static (MillerRepositoryIndex index, SmartTargetResolver resolver) Build(JulieDbFixture fx)
    {
        var index = MillerRepositoryIndex.Build(SqliteSymbolReader.Read(fx.DbPath));
        return (index, new SmartTargetResolver(index));
    }

    private static JulieDbFixture EmptyFixture(string workspaceId) =>
        JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            Array.Empty<JulieDbFixture.SymbolRow>(),
            workspaceId: workspaceId);

    private static JulieDbFixture FixtureWithNoisyFileSummary() =>
        JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, new[]
        {
            new JulieDbFixture.SymbolRow("a0000000000000000000000000000001", "System", "import", "csharp",
                "src/SearchTool.cs", "using System;", 1, null),
            new JulieDbFixture.SymbolRow("a0000000000000000000000000000002", "Tools", "module", "csharp",
                "src/SearchTool.cs", "namespace Miller.Server.Tools", 2, null),
            new JulieDbFixture.SymbolRow("a0000000000000000000000000000003", "SearchTool", "class", "csharp",
                "src/SearchTool.cs", "public sealed class SearchTool", 10, null),
            new JulieDbFixture.SymbolRow("a0000000000000000000000000000004", "_workspaceProvider", "field", "csharp",
                "src/SearchTool.cs", "private readonly IWorkspaceSearchProvider _workspaceProvider", 11, "a0000000000000000000000000000003"),
            new JulieDbFixture.SymbolRow("a0000000000000000000000000000005", "Run", "method", "csharp",
                "src/SearchTool.cs", "public static string Run(...)", 20, "a0000000000000000000000000000003"),
            new JulieDbFixture.SymbolRow("a0000000000000000000000000000006", "RenderCompact", "method", "csharp",
                "src/SearchTool.cs", "private static string RenderCompact(...)", 30, "a0000000000000000000000000000003"),
        });

    private static JulieDbFixture FixtureWithAmbiguousSymbols() =>
        JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, new[]
        {
            new JulieDbFixture.SymbolRow("b0000000000000000000000000000001", "Duplicate", "method", "csharp",
                "src/One.cs", "public void Duplicate()", 10, null),
            new JulieDbFixture.SymbolRow("b0000000000000000000000000000002", "Duplicate", "method", "csharp",
                "src/Two.cs", "public void Duplicate()", 20, null),
        });

    // ---- File listing ----

    [Fact]
    public void Run_FileSummary_ListsTheFilesSymbols()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "auth/UserService.cs", depth: "summary", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.Contains("UserService", output);
        Assert.Contains("GetUser", output);
        Assert.Contains("DeleteUser", output);
    }

    [Fact]
    public void Run_FileSummary_FiltersByKind()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "auth/UserService.cs", depth: "summary", kind: "method", scope: null, limit: 50, json: false, out _);

        // Only the methods (GetUser, DeleteUser); the class UserService is filtered out.
        Assert.Contains("GetUser", output);
        Assert.Contains("DeleteUser", output);
        Assert.DoesNotContain("class", output);
    }

    [Fact]
    public void Run_FileSummary_RespectsLimit()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "auth/UserService.cs", depth: "summary", kind: null, scope: null, limit: 1, json: false, out int count);

        Assert.Equal(1, count);
        Assert.Contains("more", output); // overflow note
    }

    [Fact]
    public void Run_FileSummary_Compact_GroupsByKindAndHidesLowSignalRows()
    {
        using var fx = FixtureWithNoisyFileSummary();
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "src/SearchTool.cs", depth: "summary", kind: null, scope: null, limit: 50, json: false, out int count);

        Assert.Equal(4, count);
        Assert.Equal(
            "# src/SearchTool.cs\n" +
            "class (1)\n" +
            "  SearchTool  :10  public sealed class SearchTool\n" +
            "method (2)\n" +
            "  Run  :20  public static string Run(...)\n" +
            "  RenderCompact  :30  private static string RenderCompact(...)\n" +
            "field (1)\n" +
            "  _workspaceProvider  :11  private readonly IWorkspaceSearchProvider _workspaceProvider\n" +
            "low_signal hidden: 1 import, 1 module (pass kind=import/module)",
            output);
        Assert.DoesNotContain("using System;", output);
        Assert.DoesNotContain("namespace Miller.Server.Tools", output);
        Assert.DoesNotContain("SearchTool  class  src/SearchTool.cs", output);
    }

    [Fact]
    public void Run_FileSummary_Compact_OmitsSignatureThatJustEchoesTheName()
    {
        // Some extractors emit signature == name (e.g. bare fields); the row should not repeat it.
        using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, new[]
        {
            new JulieDbFixture.SymbolRow("a0000000000000000000000000000001", "MaxRetries", "field", "csharp",
                "src/Config.cs", "MaxRetries", 5, null),
        });
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "src/Config.cs", depth: "summary", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.Contains("MaxRetries  :5", output);
        Assert.DoesNotContain("MaxRetries  :5  MaxRetries", output);
    }

    [Fact]
    public void Run_FileSummary_Compact_NormalizesMultilineSignatures()
    {
        using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, new[]
        {
            new JulieDbFixture.SymbolRow("a0000000000000000000000000000011", "Search", "method", "csharp",
                "src/SearchTool.cs", "public void Search(\n    string query)", 10, null),
        });
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "src/SearchTool.cs", depth: "summary", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.Contains("public void Search( string query)", output);
        Assert.DoesNotContain("\n    string query", output);
    }

    [Fact]
    public void Run_FileSummary_Compact_KindFilterShowsLowSignalRows()
    {
        using var fx = FixtureWithNoisyFileSummary();
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "src/SearchTool.cs", depth: "summary", kind: "import", scope: null, limit: 50, json: false, out int count);

        Assert.Equal(1, count);
        Assert.Equal(
            "# src/SearchTool.cs\n" +
            "import (1)\n" +
            "  System  :1  using System;",
            output);
        Assert.DoesNotContain("low_signal hidden", output);
    }

    [Fact]
    public void Run_FileSummary_Json_KeepsLowSignalChildren()
    {
        using var fx = FixtureWithNoisyFileSummary();
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "src/SearchTool.cs", depth: "summary", kind: null, scope: null, limit: 50, json: true, out int count);

        Assert.Equal(6, count);
        using var doc = JsonDocument.Parse(output);
        var children = doc.RootElement.GetProperty("children");
        Assert.Equal(6, children.GetArrayLength());
        Assert.Equal("System", children[0].GetProperty("name").GetString());
        Assert.Equal("import", children[0].GetProperty("kind").GetString());
        Assert.Equal("using System;", children[0].GetProperty("signature").GetString());
    }

    [Fact]
    public void Run_UnknownPath_ReturnsNote_NotError()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "does/not/exist.cs", depth: "summary", kind: null, scope: null, limit: 50, json: false, out int count);

        Assert.Equal(0, count);
        Assert.Contains("No indexed symbols in does/not/exist.cs", output);
    }

    // ---- Symbol summary ----

    [Fact]
    public void Run_SymbolSummary_ShowsSignatureAndDocComment()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "GetUser", depth: "summary", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.Contains("GetUser", output);
        Assert.Contains("public User GetUser(int id)", output);
        Assert.Contains("Gets a user by id.", output);    // doc_comment via ReadDetail
        Assert.Contains("auth/UserService.cs:2", output); // file:line
    }

    // ---- Symbol full ----

    [Fact]
    public void Run_SymbolFull_IncludesChildrenRefsCallersCalleesAndBody()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var (index, resolver) = Build(fx);

        // Inspect the parent class at full depth: children = GetUser + DeleteUser.
        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "UserService", depth: "full", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.Contains("GetUser", output);   // child
        Assert.Contains("DeleteUser", output); // child
    }

    [Fact]
    public void Run_SymbolFull_OnMethod_ShowsRefsCallersCalleesBody()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "GetUser", depth: "full", kind: null, scope: null, limit: 50, json: false, out _);

        // refs: GetUser is referenced in Controller.cs:4 and Repo.cs:9.
        Assert.Contains("web/Controller.cs:4", output);
        Assert.Contains("auth/Repo.cs:9", output);
        // callees: GetUser calls Find.
        Assert.Contains("Find", output);
        // body: sliced out of files.content.
        Assert.Contains("return _repo.Find(id);", output);
    }

    [Fact]
    public void Run_SymbolFull_WithComplexityRow_RendersComplexityLine()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        ExtractReaderTests.SeedComplexityRow(fx.DbPath, JulieDbFixture.GetUserId, parameterCount: 1);
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "GetUser", depth: "full", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.Contains("complexity: decisions=2  loops=1  nesting=2  params=1  lines=3", output);
    }

    [Fact]
    public void Run_SymbolFull_NoComplexityRow_OmitsComplexityLine()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "GetUser", depth: "full", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.DoesNotContain("complexity:", output);
    }

    // A symbol with many same-file references and repeated callee names, to pin compact grouping/dedup.
    // References to "Cmp": three sites in a.cs (lines 5, 8, 12) + one in b.cs (line 3) — grouped one line per
    // file. Callees FROM Cmp: TryParse ×2, nameof ×2, Validate, Check — four distinct names from six call
    // sites, deduped first-location-wins. Reference rows carry a NULL containing id so no callers section is
    // emitted (keeps the assertions about refs/callees isolated).
    private static JulieDbFixture GroupAndDedupFixture()
    {
        const string cmpId = "aa000000000000000000000000000001";
        var rows = new[]
        {
            new JulieDbFixture.SymbolRow(cmpId, "Cmp", "method", "csharp",
                "src/a.cs", "public int Cmp(string x)", 2, null),
        };
        var identifiers = new[]
        {
            // Name-based references to "Cmp" (ReadReferences), ordered by path/line: a.cs 5,8,12 then b.cs 3.
            new JulieDbFixture.IdentifierRow("bb00000000000000000000000000000a", "Cmp", "call", "csharp", "src/a.cs", 5, null),
            new JulieDbFixture.IdentifierRow("bb00000000000000000000000000000b", "Cmp", "call", "csharp", "src/a.cs", 8, null),
            new JulieDbFixture.IdentifierRow("bb00000000000000000000000000000c", "Cmp", "call", "csharp", "src/a.cs", 12, null),
            new JulieDbFixture.IdentifierRow("bb00000000000000000000000000000d", "Cmp", "call", "csharp", "src/b.cs", 3, null),
            // Callees FROM Cmp (containing == Cmp, kind 'call'), ordered by line 3..8: repeated names collapse.
            new JulieDbFixture.IdentifierRow("cc00000000000000000000000000000a", "TryParse", "call", "csharp", "src/a.cs", 3, cmpId),
            new JulieDbFixture.IdentifierRow("cc00000000000000000000000000000b", "nameof", "call", "csharp", "src/a.cs", 4, cmpId),
            new JulieDbFixture.IdentifierRow("cc00000000000000000000000000000c", "TryParse", "call", "csharp", "src/a.cs", 5, cmpId),
            new JulieDbFixture.IdentifierRow("cc00000000000000000000000000000d", "nameof", "call", "csharp", "src/a.cs", 6, cmpId),
            new JulieDbFixture.IdentifierRow("cc00000000000000000000000000000e", "Validate", "call", "csharp", "src/a.cs", 7, cmpId),
            new JulieDbFixture.IdentifierRow("cc00000000000000000000000000000f", "Check", "call", "csharp", "src/a.cs", 8, cmpId),
        };
        return JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract,
            rows, identifiers: identifiers, workspaceId: "ws-inspect-group");
    }

    [Fact]
    public void Run_SymbolFull_GroupsReferencesByFile_AndDedupsCallees()
    {
        using var fx = GroupAndDedupFixture();
        var (index, resolver) = Build(fx);

        string full = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "Cmp", depth: "full", kind: null, scope: null, limit: 50, json: false, out _);

        // References: one comma-joined line per file, in path/line order.
        Assert.Contains("src/a.cs:5,8,12", full);
        Assert.Contains("src/b.cs:3", full);
        // No un-grouped one-ref-per-line rendering survives.
        Assert.DoesNotContain("src/a.cs:5\n", full);

        // Callees: unique by name, first location wins, ×N only when a name recurs.
        Assert.Contains("TryParse ×2  src/a.cs:3", full);
        Assert.Contains("nameof ×2  src/a.cs:4", full);
        Assert.Contains("Validate  src/a.cs:7", full);
        Assert.Contains("Check  src/a.cs:8", full);
        // A single-occurrence callee never gets a count annotation.
        Assert.DoesNotContain("Validate ×", full);
        Assert.DoesNotContain("Check ×", full);
        // All four distinct callees fit under the 50-row full limit — no omitted line.
        Assert.DoesNotContain("more callees", full);
    }

    [Fact]
    public void Run_SymbolOverview_OmittedCounts_UseRefsAndDistinctCallees()
    {
        using var fx = GroupAndDedupFixture();
        var (index, resolver) = Build(fx);

        string overview = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "Cmp", depth: "overview", kind: null, scope: null, limit: 50, json: false, out _);

        // Overview relation limit is 3. Refs: first 3 (all in a.cs) render as one grouped line; b.cs is past
        // the limit; the omitted count still counts underlying refs (4 total → 1 hidden), not files.
        Assert.Contains("src/a.cs:5,8,12", overview);
        Assert.DoesNotContain("src/b.cs", overview);
        Assert.Contains("... 1 more refs", overview);

        // Callees: dedup happens BEFORE the limit. 4 distinct names, 3 shown → "1 more" (distinct count),
        // NOT "3 more" that the 6 raw call sites would have produced.
        Assert.Contains("... 1 more callees", overview);
        Assert.DoesNotContain("... 3 more callees", overview);
    }

    [Fact]
    public void Run_SymbolFull_Json_KeepsRawUndedupedRefsAndCallees()
    {
        using var fx = GroupAndDedupFixture();
        var (index, resolver) = Build(fx);

        string json = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "Cmp", depth: "full", kind: null, scope: null, limit: 50, json: true, out _);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        // JSON is untouched: one object per raw reference (4) and per raw call site (6), no ×N collapsing.
        Assert.Equal(4, root.GetProperty("refs").GetArrayLength());
        Assert.Equal(6, root.GetProperty("callees").GetArrayLength());
        Assert.DoesNotContain("×", json);
    }

    [Fact]
    public void Run_SymbolOverview_IncludesBoundedContextAndBodyPreview()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        ExtractReaderTests.SeedComplexityRow(fx.DbPath, JulieDbFixture.GetUserId, parameterCount: 1);
        var (index, resolver) = Build(fx);

        string full = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "GetUser", depth: "full", kind: null, scope: null, limit: 50, json: false, out _);
        string overview = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "GetUser", depth: "overview", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.Contains("public User GetUser(int id)", overview);
        Assert.Contains("Gets a user by id.", overview);
        Assert.Contains("complexity: decisions=2  loops=1  nesting=2  params=1  lines=3", overview);
        Assert.Contains("web/Controller.cs:4", overview);
        Assert.Contains("Find", overview);
        Assert.Contains("## body preview", overview);
        Assert.Contains("return _repo.Find(id);", overview);
        Assert.DoesNotContain("## body\n", overview);
        Assert.Contains("## body\n", full);
    }

    // A container whose body interleaves doc-comment lines (dropped from the overview preview) with ordinary
    // comments and code (kept). Body span covers the whole file so BodyPreview sees every line.
    private const string DocCommentBodyContent =
        "public class Widget {\n" +                        // L1  kept (code)
        "  /// <summary>Adds numbers.</summary>\n" +       // L2  dropped (///)
        "  public int Add(int a) {\n" +                    // L3  kept
        "    return a + 1;\n" +                            // L4  kept
        "  }\n" +                                          // L5  kept
        "  /** block doc open\n" +                         // L6  dropped (/** opens block)
        "   * block doc middle\n" +                        // L7  dropped (inside block)
        "   */\n" +                                        // L8  dropped (closes block)
        "  //! inner doc line\n" +                         // L9  dropped (//!)
        "  // ordinary kept comment\n" +                   // L10 kept (plain //)
        "  # hash kept comment\n" +                        // L11 kept (#)
        "  public int Sub(int a) { return a - 1; }\n" +    // L12 kept
        "}\n";                                             // L13 kept

    private static JulieDbFixture DocCommentBodyFixture()
    {
        var rows = new[]
        {
            new JulieDbFixture.SymbolRow("c0000000000000000000000000000abc", "Widget", "class", "csharp",
                "src/Widget.cs", "public class Widget", 1, null)
            {
                Visibility = "public",
                DocComment = "A widget.",
                BodyStartByte = 0,
                BodyEndByte = System.Text.Encoding.UTF8.GetByteCount(DocCommentBodyContent),
                BodyStartLine = 1,
                BodyEndLine = 13,
            },
        };
        var content = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["src/Widget.cs"] = DocCommentBodyContent,
        };
        return JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract,
            rows, fileContent: content, workspaceId: "ws-inspect-doc");
    }

    [Fact]
    public void Run_SymbolOverview_BodyPreview_DropsDocCommentLines_KeepsCodeAndPlainComments()
    {
        using var fx = DocCommentBodyFixture();
        var (index, resolver) = Build(fx);

        string overview = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "Widget", depth: "overview", kind: null, scope: null, limit: 50, json: false, out _);

        // /// and //! doc-comment lines are gone.
        Assert.DoesNotContain("Adds numbers", overview);
        Assert.DoesNotContain("inner doc line", overview);
        // The /** ... */ block is dropped, inclusive of every line.
        Assert.DoesNotContain("block doc open", overview);
        Assert.DoesNotContain("block doc middle", overview);
        // Plain // and # comments and real code survive.
        Assert.Contains("// ordinary kept comment", overview);
        Assert.Contains("# hash kept comment", overview);
        Assert.Contains("public int Add(int a)", overview);
        Assert.Contains("public int Sub(int a)", overview);
        // Filtering alone (few lines, small chars) is NOT truncation.
        Assert.DoesNotContain("body preview truncated", overview);
    }

    [Fact]
    public void Run_SymbolFull_Body_IsByteIdenticalIncludingDocComments()
    {
        using var fx = DocCommentBodyFixture();
        var (index, resolver) = Build(fx);

        string full = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "Widget", depth: "full", kind: null, scope: null, limit: 50, json: false, out _);

        // depth=full renders the raw body verbatim — every doc-comment line is still present.
        Assert.Contains("## body\n", full);
        Assert.Contains("/// <summary>Adds numbers.</summary>", full);
        Assert.Contains("/** block doc open", full);
        Assert.Contains("* block doc middle", full);
        Assert.Contains("//! inner doc line", full);
        Assert.Contains("// ordinary kept comment", full);
    }

    [Fact]
    public void Run_SymbolFull_Json_ExposesComplexityOrNull()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var (index, resolver) = Build(fx);

        string withoutRow = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "GetUser", depth: "full", kind: null, scope: null, limit: 50, json: true, out _);
        using (var doc = JsonDocument.Parse(withoutRow))
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("complexity").ValueKind);

        ExtractReaderTests.SeedComplexityRow(fx.DbPath, JulieDbFixture.GetUserId, parameterCount: 1);
        string withRow = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "GetUser", depth: "full", kind: null, scope: null, limit: 50, json: true, out _);

        using var withDoc = JsonDocument.Parse(withRow);
        JsonElement complexity = withDoc.RootElement.GetProperty("complexity");
        Assert.Equal("julie-ast-complexity-v1", complexity.GetProperty("algorithm_id").GetString());
        Assert.Equal(2, complexity.GetProperty("decision_count").GetInt64());
        Assert.Equal(1, complexity.GetProperty("loop_count").GetInt64());
        Assert.Equal(2, complexity.GetProperty("max_nesting_depth").GetInt64());
        Assert.Equal(1, complexity.GetProperty("parameter_count").GetInt64());
        Assert.Equal(3, complexity.GetProperty("covered_lines").GetInt64());
    }

    [Fact]
    public void Run_FullDepth_FreshFile_RendersBody()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var (index, resolver) = Build(fx);

        // The fixture materializes auth/UserService.cs under WorkspaceRoot; a fresh disk read matches the
        // stored content_hash, so full-depth body renders from disk.
        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "GetUser", depth: "full", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.Contains("return _repo.Find(id);", output);
    }

    [Fact]
    public void Run_FullDepth_DriftedFile_RendersStaleFileReason()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        // Mutate the on-disk file so its blake3 no longer matches the stored content_hash.
        File.WriteAllText(Path.Combine(fx.WorkspaceRoot, "auth/UserService.cs"), "changed\n");
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "GetUser", depth: "full", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.Contains("body unavailable", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("stale file", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no span recorded", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("changed", output); // never slices the drifted file
    }

    [Fact]
    public void Run_FullDepth_MissingDiskFile_RendersMissingFileReason()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        File.Delete(Path.Combine(fx.WorkspaceRoot, "auth/UserService.cs"));
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "GetUser", depth: "full", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.Contains("body unavailable", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("missing file", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no span recorded", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Run_FullDepth_MissingFileHash_RendersMissingHashReason()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        DeleteFileRow(fx.DbPath, "auth/UserService.cs");
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "GetUser", depth: "full", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.Contains("body unavailable", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("file hash unavailable", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no span recorded", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Run_FullDepth_UnsafeSymbolPath_RendersUnsafePathReason()
    {
        string escapedName = "miller-inspect-escape-" + Guid.NewGuid().ToString("N") + ".cs";
        string escapingPath = Path.Combine("..", escapedName);
        string content = "void UnsafeBody() {}\n";
        string? escapedAbs = null;

        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                new JulieDbFixture.SymbolRow(
                    "ab000000000000000000000000000001", "UnsafeBody", "method", "csharp",
                    escapingPath, "void UnsafeBody()", 1, null)
                {
                    BodyStartByte = 0, BodyEndByte = content.Length,
                    BodyStartLine = 1, BodyEndLine = 1,
                },
            },
            fileContent: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [escapingPath] = content,
            });

        try
        {
            escapedAbs = Path.GetFullPath(Path.Combine(fx.WorkspaceRoot, escapingPath));
            var (index, resolver) = Build(fx);

            string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
                "UnsafeBody", depth: "full", kind: null, scope: null, limit: 50, json: false, out _);

            Assert.Contains("body unavailable", output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("unsafe path", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("no span recorded", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(content, output);
        }
        finally
        {
            if (escapedAbs is not null)
                File.Delete(escapedAbs);
        }
    }

    [Fact]
    public void Run_SymbolFull_NullBodySpans_DegradesGracefullyWithNoSpanNote()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var (index, resolver) = Build(fx);

        // DeleteUser has NULL body spans → body section is a note, not a crash.
        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "DeleteUser", depth: "full", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.Contains("DeleteUser", output);
        Assert.Contains("body unavailable", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no span recorded", output, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Impact nudge (high-dependent symbols) ----

    // A single symbol "Hot" with a controllable number of name-based references (dependents) and an
    // is_test flag, so the impact-nudge threshold/test-suppression can be pinned in isolation. The refs
    // carry a NULL containing id so no callers section is emitted, keeping the output focused.
    private static JulieDbFixture HotSymbolFixture(int refCount, bool isTest, string name = "Hot")
    {
        const string hotId = "dd000000000000000000000000000001";
        var rows = new[]
        {
            new JulieDbFixture.SymbolRow(hotId, name, "method", "csharp",
                "src/Hot.cs", $"public void {name}()", 2, null)
            {
                IsTest = isTest,
            },
        };
        var identifiers = Enumerable.Range(0, refCount)
            .Select(i => new JulieDbFixture.IdentifierRow(
                "ee" + i.ToString("x30"), name, "call", "csharp", "src/Caller.cs", 10 + i, null))
            .ToArray();
        return JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract,
            rows, identifiers: identifiers, workspaceId: "ws-inspect-hot");
    }

    [Fact]
    public void Run_SymbolOverview_HighDependents_AppendsImpactHintLast()
    {
        using var fx = HotSymbolFixture(refCount: 4, isTest: false);
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "Hot", depth: "overview", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.Contains("next: impact target=\"Hot\" — 4 dependents", output);
        // Rendered as the final line of the response.
        Assert.EndsWith("next: impact target=\"Hot\" — 4 dependents", output);
    }

    [Fact]
    public void Run_SymbolFull_HighDependents_AppendsImpactHintLast()
    {
        using var fx = HotSymbolFixture(refCount: 4, isTest: false);
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "Hot", depth: "full", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.Contains("next: impact target=\"Hot\" — 4 dependents", output);
        Assert.EndsWith("next: impact target=\"Hot\" — 4 dependents", output);
    }

    [Fact]
    public void Run_SymbolOverview_ThreeDependents_NoImpactHint()
    {
        using var fx = HotSymbolFixture(refCount: 3, isTest: false);
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "Hot", depth: "overview", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.DoesNotContain("next:", output);
    }

    [Fact]
    public void Run_SymbolSummary_HighDependents_NoImpactHint()
    {
        using var fx = HotSymbolFixture(refCount: 4, isTest: false);
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "Hot", depth: "summary", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.DoesNotContain("next:", output);
    }

    [Fact]
    public void Run_SymbolOverview_TestSymbol_NoImpactHint()
    {
        using var fx = HotSymbolFixture(refCount: 4, isTest: true);
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "Hot", depth: "overview", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.DoesNotContain("next:", output);
    }

    [Fact]
    public void Run_FileListing_HighDependents_NoImpactHint()
    {
        using var fx = FixtureWithNoisyFileSummary();
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "src/SearchTool.cs", depth: "overview", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.DoesNotContain("next:", output);
    }

    [Fact]
    public void Run_SymbolOverview_HighDependents_Json_OmitsImpactHint()
    {
        using var fx = HotSymbolFixture(refCount: 4, isTest: false);
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "Hot", depth: "overview", kind: null, scope: null, limit: 50, json: true, out _);

        Assert.DoesNotContain("next:", output);
        using var doc = JsonDocument.Parse(output); // still valid JSON, byte-shape unchanged
        Assert.Equal("Hot", doc.RootElement.GetProperty("symbol").GetProperty("name").GetString());
    }

    [Fact]
    public void Run_SymbolFull_RefsTruncatedAtRefLimit_AppendsTraceHintLast()
    {
        using var fx = HotSymbolFixture(refCount: 51, isTest: false);
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "Hot", depth: "full", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.Contains("... 1 more refs", output);
        Assert.EndsWith("next: trace target=\"Hot\" mode=refs limit=51 — full reference list", output);
    }

    [Fact]
    public void Run_SymbolFull_RefsTruncatedAtRefLimit_TraceHintReplacesImpactHint()
    {
        using var fx = HotSymbolFixture(refCount: 51, isTest: false);
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "Hot", depth: "full", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.DoesNotContain("next: impact", output);
        Assert.Equal(1, NextLineCount(output));
    }

    [Fact]
    public void Run_SymbolFull_RefsExactlyAtRefLimit_KeepsImpactHint()
    {
        using var fx = HotSymbolFixture(refCount: 50, isTest: false);
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "Hot", depth: "full", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.DoesNotContain("more refs", output);
        Assert.EndsWith("next: impact target=\"Hot\" — 50 dependents", output);
    }

    [Fact]
    public void Run_SymbolOverview_RefsTruncatedAtPreviewLimit_KeepsImpactHint()
    {
        using var fx = HotSymbolFixture(refCount: 51, isTest: false);
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "Hot", depth: "overview", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.Contains("... 48 more refs (use depth=full)", output);
        Assert.DoesNotContain("next: trace", output);
        Assert.EndsWith("next: impact target=\"Hot\" — 51 dependents", output);
    }

    [Fact]
    public void Run_SymbolFull_TestSymbolWithRefsTruncatedAtRefLimit_StillAppendsTraceHint()
    {
        using var fx = HotSymbolFixture(refCount: 51, isTest: true);
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "Hot", depth: "full", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.EndsWith("next: trace target=\"Hot\" mode=refs limit=51 — full reference list", output);
    }

    [Fact]
    public void Run_SymbolFull_RefsTruncatedAtRefLimit_Json_OmitsTraceHint()
    {
        using var fx = HotSymbolFixture(refCount: 51, isTest: false);
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "Hot", depth: "full", kind: null, scope: null, limit: 50, json: true, out _);

        Assert.DoesNotContain("next:", output);
        using var doc = JsonDocument.Parse(output);
        Assert.Equal("Hot", doc.RootElement.GetProperty("symbol").GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("overview", 3, false)]
    [InlineData("overview", 4, false)]
    [InlineData("overview", 51, false)]
    [InlineData("overview", 4, true)]
    [InlineData("full", 3, false)]
    [InlineData("full", 4, false)]
    [InlineData("full", 50, false)]
    [InlineData("full", 51, false)]
    [InlineData("full", 51, true)]
    [InlineData("summary", 51, false)]
    public void Run_Symbol_RendersAtMostOneNextLine(string depth, int refCount, bool isTest)
    {
        using var fx = HotSymbolFixture(refCount, isTest);
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "Hot", depth, kind: null, scope: null, limit: 50, json: false, out _);

        Assert.InRange(NextLineCount(output), 0, 1);
    }

    [Fact]
    public void Run_SymbolFull_RefsTruncatedAtRefLimit_EscapesSymbolNameInTraceHint()
    {
        using var fx = HotSymbolFixture(refCount: 51, isTest: false, name: "A\"C");
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "A\"C", depth: "full", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.EndsWith(
            "next: trace target=\"A\\\"C\" mode=refs limit=51 — full reference list", output);
    }

    [Fact]
    public void Run_SymbolOverview_HighDependents_EscapesSymbolNameInImpactHint()
    {
        using var fx = HotSymbolFixture(refCount: 4, isTest: false, name: "A\\B\"C");
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "A\\B\"C", depth: "overview", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.EndsWith("next: impact target=\"A\\\\B\\\"C\" — 4 dependents", output);
    }

    private static int NextLineCount(string output) =>
        output.Split('\n').Count(line => line.StartsWith("next: ", StringComparison.Ordinal));

    // ---- Ambiguity ----

    [Fact]
    public void Run_AmbiguousName_ReturnsCandidates_NeverPicksFirst()
    {
        using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, new[]
        {
            new JulieDbFixture.SymbolRow("aa11223344556677889900aabbccddee", "Handle", "method", "csharp",
                "a/First.cs", "void Handle()", 3, null),
            new JulieDbFixture.SymbolRow("bb11223344556677889900aabbccddee", "Handle", "method", "csharp",
                "b/Second.cs", "void Handle()", 7, null),
        });
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "Handle", depth: "summary", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.Contains("a/First.cs", output);
        Assert.Contains("b/Second.cs", output);
        Assert.Contains("candidate", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("inspect target=\"Handle\" scope=\"a/First.cs\"", output);
        Assert.Contains("inspect target=\"Handle\" scope=\"b/Second.cs\"", output);
    }

    [Fact]
    public void Run_AmbiguousName_CompactCapsCandidatesWithRemainderNote()
    {
        var rows = Enumerable.Range(1, 25)
            .Select(i => new JulieDbFixture.SymbolRow(
                i.ToString("x32"),
                "Search",
                "method",
                "csharp",
                $"src/File{i:00}.cs",
                "public void Search()",
                i,
                null))
            .ToArray();
        using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, rows);
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "Search", depth: "summary", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.Contains("src/File20.cs", output);
        Assert.DoesNotContain("src/File21.cs", output);
        Assert.Contains("5 more candidates", output);
    }

    [Fact]
    public void Run_AmbiguousName_Json_IncludesRerunExamples()
    {
        using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, new[]
        {
            new JulieDbFixture.SymbolRow("aa11223344556677889900aabbccddee", "Handle", "method", "csharp",
                "a/First.cs", "void Handle()", 3, null),
            new JulieDbFixture.SymbolRow("bb11223344556677889900aabbccddee", "Handle", "method", "csharp",
                "b/Second.cs", "void Handle()", 7, null),
        });
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "Handle", depth: "summary", kind: null, scope: null, limit: 50, json: true, out _);

        using var doc = JsonDocument.Parse(output);
        JsonElement examples = doc.RootElement.GetProperty("rerun_examples");
        Assert.Equal(JsonValueKind.Array, examples.ValueKind);
        Assert.Equal("inspect target=\"Handle\" scope=\"a/First.cs\"", examples[0].GetString());
        Assert.Equal("inspect target=\"Handle\" scope=\"b/Second.cs\"", examples[1].GetString());
    }

    [Fact]
    public void Run_AmbiguousScopedName_AsksForMoreSpecificTarget()
    {
        using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, new[]
        {
            new JulieDbFixture.SymbolRow("cc11223344556677889900aabbccddee", "SearchTool", "class", "csharp",
                "src/SearchTool.cs", "public sealed class SearchTool", 10, null),
            new JulieDbFixture.SymbolRow("dd11223344556677889900aabbccddee", "SearchTool", "constructor", "csharp",
                "src/SearchTool.cs", "public SearchTool()", 12, null),
        });
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "SearchTool", depth: "summary", kind: null, scope: "src/SearchTool.cs", limit: 50, json: false, out _);

        Assert.Contains("more specific target", output);
        Assert.DoesNotContain("pass scope=<file>", output);
        Assert.DoesNotContain("scope=\"src/SearchTool.cs\"", output);
    }

    [Fact]
    public void Run_NotFoundName_ReturnsNote()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "NoSuchSymbol", depth: "summary", kind: null, scope: null, limit: 50, json: false, out int count);

        Assert.Equal(0, count);
        Assert.Contains("not found", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Run_MisspelledName_SuggestsNearMissesInNote()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var (index, resolver) = Build(fx);

        // Truncated typo of "GetUser" — the note must offer the close name so the agent self-corrects.
        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "GetUse", depth: "summary", kind: null, scope: null, limit: 50, json: false, out int count);

        Assert.Equal(0, count);
        Assert.Contains("not found", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Closest:", output);
        Assert.Contains("GetUser", output);
    }

    [Fact]
    public void Run_WrongFileScope_SurfacesWhereTheSymbolActuallyLives()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "GetUser", depth: "summary", kind: null, scope: "wrong/Other.cs", limit: 50, json: false,
            out int count);

        Assert.Equal(0, count);
        Assert.Contains("not found", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("auth/UserService.cs", output);
    }

    // ---- JSON ----

    [Fact]
    public void Run_SymbolFull_Json_HasStructuredShape()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "GetUser", depth: "full", kind: null, scope: null, limit: 50, json: true, out _);

        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        Assert.Equal("GetUser", root.GetProperty("symbol").GetProperty("name").GetString());
        Assert.True(root.TryGetProperty("refs", out var refs));
        Assert.Equal(JsonValueKind.Array, refs.ValueKind);
        Assert.True(root.TryGetProperty("callees", out _));
        Assert.True(root.TryGetProperty("callers", out _));
        Assert.True(root.TryGetProperty("body", out _));
        Assert.False(root.TryGetProperty("body_unavailable_reason", out _));
    }

    [Fact]
    public void Run_SymbolOverview_Json_HasPreviewShape()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "GetUser", depth: "overview", kind: null, scope: null, limit: 50, json: true, out _);

        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        Assert.Equal("GetUser", root.GetProperty("symbol").GetProperty("name").GetString());
        Assert.True(root.TryGetProperty("refs", out var refs));
        Assert.Equal(JsonValueKind.Array, refs.ValueKind);
        Assert.True(root.TryGetProperty("body_preview", out var preview));
        Assert.Contains("return _repo.Find(id);", preview.GetString());
        Assert.True(root.TryGetProperty("body_preview_truncated", out var truncated));
        Assert.False(truncated.GetBoolean());
        Assert.False(root.TryGetProperty("body", out _));
    }

    [Fact]
    public void Run_SymbolFull_Json_DriftedFile_ExposesBodyUnavailableReason()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        File.WriteAllText(Path.Combine(fx.WorkspaceRoot, "auth/UserService.cs"), "changed\n");
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "GetUser", depth: "full", kind: null, scope: null, limit: 50, json: true, out _);

        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        Assert.Equal(JsonValueKind.Null, root.GetProperty("body").ValueKind);
        Assert.Equal("stale_file", root.GetProperty("body_unavailable_reason").GetString());
    }

    [Fact]
    public void Run_FileSummary_Json_IsAFileListing()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "auth/UserService.cs", depth: "summary", kind: null, scope: null, limit: 50, json: true, out _);

        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        Assert.Equal("auth/UserService.cs", root.GetProperty("file").GetString());
        var children = root.GetProperty("children");
        Assert.Equal(JsonValueKind.Array, children.ValueKind);
        Assert.True(children.GetArrayLength() >= 3);
    }

    [Fact]
    public void Inspect_ExplicitWorkspaceId_UsesTargetIndexResolverAndDbPath_AndPrefixesFreshness()
    {
        using var current = EmptyFixture("current-ws");
        using var target = JulieDbFixture.CreateForInspect();
        var (currentIndex, _) = Build(current);
        var (targetIndex, _) = Build(target);
        string currentRoot = Path.Combine(Path.GetTempPath(), "miller-current-" + Guid.NewGuid().ToString("N"));
        string targetRoot = Path.Combine(Path.GetTempPath(), "miller-target-" + Guid.NewGuid().ToString("N"));
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(currentIndex, current.DbPath, "current-ws", currentRoot),
            ("target-ws", ReadToolRoutingTestSupport.ContextFor(
                targetIndex,
                target.DbPath,
                "target-ws",
                targetRoot,
                indexFresh: false,
                freshnessStatus: "unconfirmed_lock_busy")));
        var tool = new InspectTool(provider, provider);

        string output = tool.Inspect(
            "GetUser",
            depth: "summary",
            workspace_id: "target-ws",
            ensure_fresh: false);

        Assert.Equal("target-ws", provider.LastWorkspaceId);
        Assert.False(provider.LastEnsureFresh);
        Assert.StartsWith("workspace: target-ws\n", output);
        Assert.DoesNotContain(targetRoot, output);
        Assert.Contains("freshness: unconfirmed_lock_busy", output);
        Assert.Contains("Gets a user by id.", output);
    }

    [Fact]
    public void Inspect_Summary_RegisteredWorkspace_UsesSymbolProjectionWithoutFullLoad()
    {
        using var current = EmptyFixture("current-ws");
        using var target = JulieDbFixture.CreateForInspect();
        string dir = Path.Combine(Path.GetTempPath(), "miller-inspect-projection-" + Guid.NewGuid().ToString("N"));
        string currentRoot = Path.Combine(dir, "current");
        string registryDb = Path.Combine(dir, "workspaces.db");
        Directory.CreateDirectory(currentRoot);

        try
        {
            using var registry = WorkspaceRegistry.Open(registryDb);
            registry.UpsertSeen("target-ws", "target-111111111111", target.WorkspaceRoot, target.DbPath);
            registry.MarkScanned("target-ws", revision: 1);

            int fullLoadCount = 0;
            int symbolLoadCount = 0;
            var workspace = new WorkspaceContext(
                currentRoot,
                current.DbPath,
                Path.Combine(dir, "telemetry.db"),
                registryDb,
                AppContext.BaseDirectory,
                "current-ws",
                currentRoot,
                current.DbPath);
            var provider = new WorkspaceIndexProvider(
                new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1),
                workspace,
                registry,
                refresh: _ => throw new InvalidOperationException("refresh was not expected"),
                loadIndex: _ =>
                {
                    fullLoadCount++;
                    throw new InvalidOperationException("full loader was not expected");
                },
                loadSymbolSearch: path =>
                {
                    symbolLoadCount++;
                    return SymbolSearchProjectionLoader.Load(path);
                },
                loadContentSearch: (_, _) =>
                    throw new InvalidOperationException("content loader was not expected"),
                loadTextContentSearch: (_, _) =>
                    throw new InvalidOperationException("text content loader was not expected"),
                loadRegionSearch: (_, _) =>
                    throw new InvalidOperationException("region loader was not expected"),
                currentIndexFresh: _ => true,
                sidecar: SymbolSearchSidecar.Disabled);
            var tool = new InspectTool(provider, provider);

            string output = tool.Inspect(
                "GetUser",
                depth: "summary",
                workspace_id: "target-ws",
                ensure_fresh: false);

            Assert.DoesNotContain("inspect failed", output);
            Assert.Contains("Gets a user by id.", output);
            Assert.Equal(0, fullLoadCount);
            Assert.Equal(1, symbolLoadCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Inspect_Full_RegisteredWorkspace_UsesSymbolProjectionWithoutFullLoad()
    {
        using var current = EmptyFixture("current-ws");
        using var target = JulieDbFixture.CreateForInspect();
        var targetIndex = RepositoryIndexLoader.Load(target.DbPath);
        string currentRoot = Path.Combine(Path.GetTempPath(), "miller-current-" + Guid.NewGuid().ToString("N"));
        string targetRoot = target.WorkspaceRoot;

        int fullResolveCount = 0;
        int searchResolveCount = 0;
        var provider = new FullInspectRecordingProvider(
            ReadToolRoutingTestSupport.ContextFor(
                MillerRepositoryIndex.Build(SqliteSymbolReader.Read(current.DbPath)),
                current.DbPath,
                "current-ws",
                currentRoot),
            ReadToolRoutingTestSupport.ContextFor(
                targetIndex,
                target.DbPath,
                "target-ws",
                targetRoot),
            () => fullResolveCount++,
            () => searchResolveCount++);
        var tool = new InspectTool(provider, provider);

        string output = tool.Inspect(
            "GetUser",
            depth: "full",
            workspace_id: "target-ws",
            ensure_fresh: false);

        Assert.Contains("## body", output);
        Assert.Equal(0, fullResolveCount);
        Assert.Equal(1, searchResolveCount);
    }

    [Fact]
    public void Inspect_Full_RegisteredWorkspace_AmbiguousTarget_UsesSymbolProjectionWithoutFullLoad()
    {
        using var current = EmptyFixture("current-ws");
        using var target = FixtureWithAmbiguousSymbols();
        var targetIndex = RepositoryIndexLoader.Load(target.DbPath);
        string currentRoot = Path.Combine(Path.GetTempPath(), "miller-current-" + Guid.NewGuid().ToString("N"));
        string targetRoot = target.WorkspaceRoot;

        int fullResolveCount = 0;
        int searchResolveCount = 0;
        var provider = new FullInspectRecordingProvider(
            ReadToolRoutingTestSupport.ContextFor(
                MillerRepositoryIndex.Build(SqliteSymbolReader.Read(current.DbPath)),
                current.DbPath,
                "current-ws",
                currentRoot),
            ReadToolRoutingTestSupport.ContextFor(
                targetIndex,
                target.DbPath,
                "target-ws",
                targetRoot),
            () => fullResolveCount++,
            () => searchResolveCount++);
        var tool = new InspectTool(provider, provider);

        string output = tool.Inspect(
            "Duplicate",
            depth: "full",
            workspace_id: "target-ws",
            ensure_fresh: false);

        Assert.Contains("Multiple candidates", output);
        Assert.Contains("src/One.cs", output);
        Assert.Contains("src/Two.cs", output);
        Assert.Equal(0, fullResolveCount);
        Assert.Equal(1, searchResolveCount);
    }

    private static void DeleteFileRow(string dbPath, string filePath)
    {
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        };

        using var conn = new SqliteConnection(csb.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM files WHERE path = $path;";
        cmd.Parameters.AddWithValue("$path", filePath);
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    private sealed class FullInspectRecordingProvider : IWorkspaceIndexProvider, IWorkspaceSearchProvider
    {
        private readonly WorkspaceReadContext _current;
        private readonly WorkspaceReadContext _target;
        private readonly Action _onFullResolve;
        private readonly Action _onSearchResolve;

        public FullInspectRecordingProvider(
            WorkspaceReadContext current,
            WorkspaceReadContext target,
            Action onFullResolve,
            Action onSearchResolve)
        {
            _current = current;
            _target = target;
            _onFullResolve = onFullResolve;
            _onSearchResolve = onSearchResolve;
        }

        public WorkspaceReadContext Resolve(string? workspaceId, bool ensureFresh)
        {
            _onFullResolve();
            return workspaceId is null ? _current : _target;
        }

        public WorkspaceSymbolSearchContext ResolveSymbolSearch(string? workspaceId, bool ensureFresh)
        {
            _onSearchResolve();
            return ReadToolRoutingTestSupport.SearchContextFor(workspaceId is null ? _current : _target);
        }
    }
}
