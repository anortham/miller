using System.Text.Json;
using Miller.Core.Graph;
using Miller.Indexing;
using Miller.Server.Git;
using Miller.Server.Resolution;
using Miller.Server.Telemetry;
using Miller.Server.Tools;
using Miller.Tests;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the <c>impact</c> tool (M5 D5/D1/D8/D10) against an in-memory synth index built from symbols + edges via
/// <see cref="MillerRepositoryIndex.Build(IReadOnlyList{IndexedSymbol},IReadOnlyList{GraphEdge})"/> — pure, no
/// SQLite (the Server <c>Run</c> core never touches the DB for impact). Exercises
/// <see cref="ImpactTool.Run"/> directly: reverse-closure correctness, the likely-test partition (julie's
/// cross-language <c>IsTest</c> flag), the exactly-one-input guard (zero → a usage note, not an error), both
/// render formats, a not-found target, the changed_paths seed leg, and the diff seed leg with line-precise
/// intersection + whole-file degradation.
///
/// <para>Graph convention (D2): an edge <c>From → To</c> means "From depends on To". So a reverse-reachability
/// query from a seed returns its <b>dependents</b> (the callers up the chain) — the blast radius of editing the
/// seed. The fixture wires <c>Validate ← Process ← Handle</c> and a test <c>ProcessWorks → Process</c>, so the
/// impact of editing <c>Validate</c> is {Process, Handle, ProcessWorks}, with ProcessWorks a likely test.</para>
/// </summary>
public sealed class ImpactToolTests
{
    private const string ValidateId = "00000000000000000000000000000001";
    private const string ProcessId = "00000000000000000000000000000002";
    private const string HandleId = "00000000000000000000000000000003";
    private const string ProcessWorksId = "00000000000000000000000000000004";
    private const string LonelyId = "00000000000000000000000000000005";
    private const string HelperId = "00000000000000000000000000000006";
    private const string ImportId = "00000000000000000000000000000007";
    private const string ModuleId = "00000000000000000000000000000008";

    // A small dependency graph:
    //   Process   depends on Validate   (Process → Validate)
    //   Handle    depends on Process    (Handle  → Process)
    //   ProcessWorks (a TEST) depends on Process (ProcessWorks → Process)
    //   Lonely depends on nothing and nothing depends on it.
    // Reverse-reachability from Validate therefore reaches Process (hop 1), then Handle + ProcessWorks (hop 2).
    private static (MillerRepositoryIndex index, SmartTargetResolver resolver) BuildFixture()
    {
        var symbols = new List<IndexedSymbol>
        {
            new(0, ValidateId, "Validate", "void Validate()", "method", "csharp", "src/Service.cs", 10, 14, null, false),
            new(1, ProcessId, "Process", "void Process()", "method", "csharp", "src/Service.cs", 20, 30, null, false),
            new(2, HandleId, "Handle", "void Handle()", "method", "csharp", "web/Controller.cs", 5, 9, null, false),
            new(3, ProcessWorksId, "ProcessWorks", "void ProcessWorks()", "method", "csharp",
                "tests/ServiceTests.cs", 8, 12, null, IsTest: true),
            new(4, LonelyId, "Lonely", "void Lonely()", "method", "csharp", "src/Other.cs", 1, 3, null, false),
        };
        var edges = new[]
        {
            new GraphEdge(ProcessId, ValidateId, "calls"),
            new GraphEdge(HandleId, ProcessId, "calls"),
            new GraphEdge(ProcessWorksId, ProcessId, "calls"),
        };
        var index = MillerRepositoryIndex.Build(symbols, edges);
        return (index, new SmartTargetResolver(index));
    }

    private static (MillerRepositoryIndex index, SmartTargetResolver resolver) BuildManyLikelyTestsFixture(int testCount)
    {
        var symbols = new List<IndexedSymbol>
        {
            new(0, ValidateId, "Validate", "void Validate()", "method", "csharp", "src/Service.cs", 10, 14, null, false),
        };
        var edges = new List<GraphEdge>();

        for (int i = 0; i < testCount; i++)
        {
            string id = (i + 10).ToString("x32");
            string name = $"ValidateWorks{i + 1:00}";
            symbols.Add(new(i + 1, id, name, $"void {name}()", "method", "csharp",
                "tests/ServiceTests.cs", i + 1, i + 1, null, IsTest: true));
            edges.Add(new GraphEdge(id, ValidateId, "calls"));
        }

        var index = MillerRepositoryIndex.Build(symbols, edges);
        return (index, new SmartTargetResolver(index));
    }

    private static (MillerRepositoryIndex index, SmartTargetResolver resolver) BuildNoisyImpactFixture()
    {
        var symbols = new List<IndexedSymbol>
        {
            new(0, ValidateId, "Validate", "void Validate()", "method", "csharp", "src/Service.cs", 10, 14, null, false),
            new(1, ProcessId, "Process", "void Process()", "method", "csharp", "src/Service.cs", 20, 24, null, false),
            new(2, HelperId, "Helper", "class Helper", "class", "csharp", "src/Service.cs", 30, 36, null, false),
            new(3, ImportId, "ComponentModel", "using System.ComponentModel;", "import", "csharp", "src/Service.cs", 1, 1, null, false),
            new(4, ModuleId, "Service Module", "Service Module", "module", "markdown", "docs/service.md", 1, 1, null, false),
            new(5, ProcessWorksId, "ProcessWorks", "void ProcessWorks()", "method", "csharp",
                "tests/ServiceTests.cs", 8, 12, null, IsTest: true),
        };
        var edges = new[]
        {
            new GraphEdge(ProcessId, ValidateId, "calls"),
            new GraphEdge(HelperId, ValidateId, "uses"),
            new GraphEdge(ImportId, ValidateId, "uses"),
            new GraphEdge(ModuleId, ValidateId, "contains"),
            new GraphEdge(ProcessWorksId, ValidateId, "calls"),
        };
        var index = MillerRepositoryIndex.Build(symbols, edges);
        return (index, new SmartTargetResolver(index));
    }

    private static MillerRepositoryIndex EmptyIndex() =>
        MillerRepositoryIndex.Build(Array.Empty<IndexedSymbol>(), Array.Empty<GraphEdge>());

    // Seed.cs ← Direct.cs ← Deep.cs, plus DirectTest.cs → Seed.cs. This shape exercises
    // independent depth and limit truncation while retaining the extractor-owned IsTest partition.
    private static MillerRepositoryIndex BuildTraversalEvidenceFixture()
    {
        const string seedId = "30000000000000000000000000000001";
        const string directId = "30000000000000000000000000000002";
        const string directTestId = "30000000000000000000000000000003";
        const string deepId = "30000000000000000000000000000004";
        var symbols = new List<IndexedSymbol>
        {
            new(0, seedId, "Seed", "void Seed()", "method", "csharp", "src/Seed.cs", 1, 3, null, false),
            new(1, directId, "Direct", "void Direct()", "method", "csharp", "src/Direct.cs", 1, 3, null, false),
            new(2, directTestId, "DirectTest", "void DirectTest()", "method", "csharp",
                "tests/DirectTests.cs", 1, 3, null, IsTest: true),
            new(3, deepId, "Deep", "void Deep()", "method", "csharp", "src/Deep.cs", 1, 3, null, false),
        };
        var edges = new[]
        {
            new GraphEdge(directId, seedId, "calls"),
            new GraphEdge(directTestId, seedId, "calls"),
            new GraphEdge(deepId, directId, "calls"),
        };
        return MillerRepositoryIndex.Build(symbols, edges);
    }

    private static JsonElement Traversal(JsonElement root)
    {
        JsonElement traversal = root.GetProperty("traversal");
        Assert.Equal(
            new[]
            {
                "status", "reason", "max_depth", "limit", "reached_count", "returned_count",
                "truncated_by_depth", "truncated_by_limit", "seeded_paths", "unseeded_paths",
            },
            traversal.EnumerateObject().Select(static property => property.Name));
        return traversal;
    }

    private static void AssertReturnedCountMatchesEnvelope(JsonElement root)
    {
        int rendered = root.GetProperty("impacted").GetArrayLength() + root.GetProperty("tests").GetArrayLength();
        Assert.Equal(rendered, Traversal(root).GetProperty("returned_count").GetInt32());
    }

    private static string ReadTelemetryMetadata(string telemetryDb)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = telemetryDb,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT metadata_json FROM tool_telemetry WHERE tool = 'impact';";
        return (string)cmd.ExecuteScalar()!;
    }

    // ---- reverse-closure correctness + test partition ----

    [Fact]
    public void Run_TargetSymbol_ReturnsReverseClosure_PartitionsLikelyTests()
    {
        var (index, resolver) = BuildFixture();

        string output = ImpactTool.Run(index, resolver,
            target: "Validate", changedPaths: null, diff: null, maxDepth: 2, limit: 100, json: false,
            out int impactedCount, out _);

        // Process (hop 1), Handle (hop 2) are impacted; ProcessWorks is partitioned into the tests section.
        Assert.Contains("Process", output);
        Assert.Contains("Handle", output);
        Assert.Contains("ProcessWorks", output);
        // Lonely depends on nothing in the closure → never listed.
        Assert.DoesNotContain("Lonely", output);
        // Impacted count excludes the likely-test (Process + Handle = 2).
        Assert.Equal(2, impactedCount);
    }

    [Fact]
    public void Run_PartitionsTestsIntoTheirOwnSection()
    {
        var (index, resolver) = BuildFixture();

        string output = ImpactTool.Run(index, resolver,
            target: "Validate", changedPaths: null, diff: null, maxDepth: 2, limit: 100, json: true,
            out _, out _);

        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;

        var impacted = root.GetProperty("impacted");
        var impactedNames = impacted.EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToList();
        Assert.Contains("Process", impactedNames);
        Assert.Contains("Handle", impactedNames);
        Assert.DoesNotContain("ProcessWorks", impactedNames); // tests are NOT in impacted

        var tests = root.GetProperty("tests");
        var testNames = tests.EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToList();
        Assert.Contains("ProcessWorks", testNames);
    }

    [Fact]
    public void Run_Compact_CarriesProvenance_NameKindFileLineAndHop()
    {
        var (index, resolver) = BuildFixture();

        string output = ImpactTool.Run(index, resolver,
            target: "Validate", changedPaths: null, diff: null, maxDepth: 2, limit: 100, json: false, out _, out _);

        // Process is reached at hop 1, in src/Service.cs:20.
        Assert.Contains("Process", output);
        Assert.Contains("method", output);
        Assert.Contains("src/Service.cs:", output);
        Assert.Contains(":20 Process method hop=1", output);
    }

    [Fact]
    public void Run_Compact_GroupsByFileAndHidesLowSignalRows()
    {
        var (index, resolver) = BuildNoisyImpactFixture();

        string output = ImpactTool.Run(index, resolver,
            target: "Validate", changedPaths: null, diff: null, maxDepth: 1, limit: 100, json: false,
            out int impactedCount, out _);

        Assert.Equal(4, impactedCount);
        Assert.Equal(
            "# impacted (4)\n" +
            "src/Service.cs:\n" +
            "  :20 Process method hop=1\n" +
            "  :30 Helper class hop=1\n" +
            "low_signal hidden: 2 imports/modules (use format=json for full list.)\n" +
            "\n" +
            "# likely tests (1)\n" +
            "tests/ServiceTests.cs:\n" +
            "  :8 ProcessWorks method hop=1",
            output);
        Assert.DoesNotContain("ComponentModel", output);
        Assert.DoesNotContain("Service Module", output);
        Assert.DoesNotContain("Process  method  src/Service.cs", output);
    }

    [Fact]
    public void Run_Json_KeepsLowSignalImpactedRows()
    {
        var (index, resolver) = BuildNoisyImpactFixture();

        string output = ImpactTool.Run(index, resolver,
            target: "Validate", changedPaths: null, diff: null, maxDepth: 1, limit: 100, json: true,
            out int impactedCount, out _);

        Assert.Equal(4, impactedCount);
        using var doc = JsonDocument.Parse(output);
        var names = doc.RootElement.GetProperty("impacted")
            .EnumerateArray()
            .Select(e => e.GetProperty("name").GetString())
            .ToList();
        Assert.Contains("Process", names);
        Assert.Contains("Helper", names);
        Assert.Contains("ComponentModel", names);
        Assert.Contains("Service Module", names);
    }

    [Fact]
    public void Run_Compact_CapsLikelyTests_ButJsonKeepsFullList()
    {
        var (index, resolver) = BuildManyLikelyTestsFixture(testCount: 25);

        string compact = ImpactTool.Run(index, resolver,
            target: "Validate", changedPaths: null, diff: null, maxDepth: 1, limit: 100, json: false, out _, out _);

        Assert.Contains("# likely tests (25)", compact);
        Assert.Contains("ValidateWorks20", compact);
        Assert.DoesNotContain("ValidateWorks21", compact);
        Assert.DoesNotContain("ValidateWorks25", compact);
        Assert.Contains("... 5 more likely tests; use format=json for full list.", compact);

        string json = ImpactTool.Run(index, resolver,
            target: "Validate", changedPaths: null, diff: null, maxDepth: 1, limit: 100, json: true, out _, out _);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(25, doc.RootElement.GetProperty("tests").GetArrayLength());
    }

    [Fact]
    public void Run_MaxDepthOne_StopsAtDirectDependents()
    {
        var (index, resolver) = BuildFixture();

        string output = ImpactTool.Run(index, resolver,
            target: "Validate", changedPaths: null, diff: null, maxDepth: 1, limit: 100, json: false,
            out int impactedCount, out _);

        // Only Process (the direct dependent) at depth 1; Handle/ProcessWorks are two hops away.
        Assert.Contains("Process", output);
        Assert.DoesNotContain("Handle", output);
        Assert.DoesNotContain("ProcessWorks", output);
        Assert.Equal(1, impactedCount);
    }

    [Fact]
    public void Run_NothingDependsOnTheTarget_ReportsEmptyImpact()
    {
        var (index, resolver) = BuildFixture();

        // Lonely has no dependents → the impact set is empty.
        string output = ImpactTool.Run(index, resolver,
            target: "Lonely", changedPaths: null, diff: null, maxDepth: 2, limit: 100, json: false,
            out int impactedCount, out _);

        Assert.Equal(0, impactedCount);
        Assert.Contains("nothing", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Resolved seed symbols: Lonely method src/Other.cs:1", output);
        Assert.Contains("Try trace Lonely", output);
    }

    // ---- exactly-one-input guard ----

    [Fact]
    public void Run_ZeroInputs_ReturnsUsageNote_NotError()
    {
        var (index, resolver) = BuildFixture();

        string output = ImpactTool.Run(index, resolver,
            target: null, changedPaths: null, diff: null, maxDepth: 2, limit: 100, json: false,
            out int impactedCount, out _);

        Assert.Equal(0, impactedCount);
        // A clear usage message naming the three mutually-exclusive inputs.
        Assert.Contains("target", output);
        Assert.Contains("changed_paths", output);
        Assert.Contains("diff", output);
    }

    [Fact]
    public void Run_MoreThanOneInput_ReturnsUsageNote()
    {
        var (index, resolver) = BuildFixture();

        string output = ImpactTool.Run(index, resolver,
            target: "Validate", changedPaths: new[] { "src/Service.cs" }, diff: null,
            maxDepth: 2, limit: 100, json: false, out int impactedCount, out _);

        Assert.Equal(0, impactedCount);
        Assert.Contains("exactly one", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Run_UsageGuardJson_UsesNoteKey_NotErrorKey()
    {
        var (index, resolver) = BuildFixture();

        // The usage guard (zero inputs here) is guidance, NOT a failure: the wrapper records it as the Empty
        // outcome, never Error. A JSON consumer keying off "error" must therefore NOT see a failure shape — the
        // guidance must carry the SAME "note" key the not-found path uses (intra-tool + repo convention: an
        // "error" key is paired with the error outcome only). Pins finding 2.
        string output = ImpactTool.Run(index, resolver,
            target: null, changedPaths: null, diff: null, maxDepth: 2, limit: 100, json: true,
            out int impactedCount, out _);

        Assert.Equal(0, impactedCount);
        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        Assert.False(root.TryGetProperty("error", out _), "usage guidance must not emit an 'error' key at an Empty outcome.");
        Assert.True(root.TryGetProperty("note", out var note), "usage guidance must use the 'note' key (the Empty-outcome convention).");
        // The message still names the three mutually-exclusive inputs so the hint is actionable.
        string? message = note.GetString();
        Assert.Contains("target", message);
        Assert.Contains("changed_paths", message);
        Assert.Contains("diff", message);
    }

    // ---- not found ----

    [Fact]
    public void Run_TargetNotFound_ReturnsNote_NotError()
    {
        var (index, resolver) = BuildFixture();

        string output = ImpactTool.Run(index, resolver,
            target: "NoSuchSymbol", changedPaths: null, diff: null, maxDepth: 2, limit: 100, json: false,
            out int impactedCount, out _);

        Assert.Equal(0, impactedCount);
        Assert.Contains("not found", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Run_AmbiguousTarget_CompactCapsCandidatesWithRemainderNote()
    {
        var symbols = Enumerable.Range(1, 25)
            .Select(i => new IndexedSymbol(
                i - 1,
                i.ToString("x32"),
                "Search",
                "void Search()",
                "method",
                "csharp",
                $"src/File{i:00}.cs",
                i,
                i,
                ParentId: null,
                IsTest: false))
            .ToArray();
        var index = MillerRepositoryIndex.Build(symbols, Array.Empty<GraphEdge>());
        var resolver = new SmartTargetResolver(index);

        string output = ImpactTool.Run(index, resolver,
            target: "Search", changedPaths: null, diff: null, maxDepth: 2, limit: 100, json: false,
            out int impactedCount, out _);

        Assert.Equal(0, impactedCount);
        Assert.Contains("src/File20.cs", output);
        Assert.DoesNotContain("src/File21.cs", output);
        Assert.Contains("5 more candidates", output);
    }

    [Fact]
    public void Run_MisspelledTarget_SuggestsNearMissesInNote()
    {
        var (index, resolver) = BuildFixture();

        // Wrong-case miss of "Validate" — the note must offer the close name for a one-turn correction.
        string output = ImpactTool.Run(index, resolver,
            target: "validate", changedPaths: null, diff: null, maxDepth: 2, limit: 100, json: false,
            out int impactedCount, out _);

        Assert.Equal(0, impactedCount);
        Assert.Contains("not found", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Closest:", output);
        Assert.Contains("Validate", output);
    }

    [Fact]
    public void Run_TargetFile_SeedsAllSymbolsInTheFile()
    {
        var (index, resolver) = BuildFixture();

        // src/Service.cs holds Validate + Process. Their union of dependents: Handle + ProcessWorks (depend on
        // Process) + Process (depends on Validate). The starts are excluded from the closure, so impacted =
        // {Handle}; ProcessWorks is the test. (Process and Validate are seeds, not impacted.)
        string output = ImpactTool.Run(index, resolver,
            target: "src/Service.cs", changedPaths: null, diff: null, maxDepth: 2, limit: 100, json: false,
            out int impactedCount, out _);

        Assert.Contains("Handle", output);
        Assert.Contains("ProcessWorks", output);
        Assert.DoesNotContain("Lonely", output);
        Assert.Equal(1, impactedCount); // only Handle (Process/Validate are seeds; ProcessWorks is a test)
    }

    // ---- changed_paths leg ----

    [Fact]
    public void Run_ChangedPaths_SeedsSymbolsInEachFile()
    {
        var (index, resolver) = BuildFixture();

        string output = ImpactTool.Run(index, resolver,
            target: null, changedPaths: new[] { "src/Service.cs" }, diff: null,
            maxDepth: 2, limit: 100, json: false, out int impactedCount, out _);

        // Same seeds as the file target → Handle impacted, ProcessWorks a test.
        Assert.Contains("Handle", output);
        Assert.Contains("ProcessWorks", output);
        Assert.Equal(1, impactedCount);
    }

    [Fact]
    public void Run_ChangedPaths_UnknownFile_IsEmptyNotError()
    {
        var (index, resolver) = BuildFixture();

        string output = ImpactTool.Run(index, resolver,
            target: null, changedPaths: new[] { "does/not/exist.cs" }, diff: null,
            maxDepth: 2, limit: 100, json: false, out int impactedCount, out _);

        Assert.Equal(0, impactedCount);
        Assert.Contains("nothing", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No indexed symbols matched changed path(s): does/not/exist.cs", output);
        Assert.Contains("Try search mode=file", output);
    }

    [Fact]
    public void Impact_EmptyTelemetry_DistinguishesNoSeedSymbols()
    {
        var (index, _) = BuildFixture();
        string dir = Path.Combine(Path.GetTempPath(), "miller-impact-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string telemetryDb = Path.Combine(dir, "telemetry.db");
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(index, "current.db", "current-ws", dir));
        var tool = new ImpactTool(provider);

        try
        {
            using (var ledger = TelemetryLedger.Open(telemetryDb, "current-ws", dir))
            {
                using var scope = ledger.Measure("impact", op: null);
                string output = tool.Impact(changed_paths: new[] { "does/not/exist.cs" });
                Assert.Contains("No indexed symbols matched changed path(s): does/not/exist.cs", output);
            }

            using var doc = JsonDocument.Parse(ReadTelemetryMetadata(telemetryDb));
            Assert.Equal("no_seed_symbols", doc.RootElement.GetProperty("empty_reason").GetString());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    // ---- diff leg ----

    [Fact]
    public void Run_Diff_LinePreciseIntersection_SeedsOnlyTheTouchedSymbol()
    {
        var (index, resolver) = BuildFixture();

        // A hunk touching new-side line 11 (inside Validate's [10,14], NOT inside Process's [20,30]). So only
        // Validate is seeded → impacted closure is {Process, Handle} with ProcessWorks the test.
        string diff = """
            --- a/src/Service.cs
            +++ b/src/Service.cs
            @@ -11,1 +11,1 @@
            -    old
            +    new
            """;

        string output = ImpactTool.Run(index, resolver,
            target: null, changedPaths: null, diff: diff, maxDepth: 2, limit: 100, json: false,
            out int impactedCount, out _);

        Assert.Contains("Process", output);
        Assert.Contains("Handle", output);
        Assert.Contains("ProcessWorks", output);
        Assert.Equal(2, impactedCount); // Process + Handle
    }

    [Fact]
    public void Run_Diff_NoIntersectingSymbol_DegradesToWholeFile_WithNote()
    {
        var (index, resolver) = BuildFixture();

        // A hunk on line 99 — past every symbol's span in src/Service.cs. No symbol intersects, so the parser
        // degrades to ALL symbols in the file (Validate + Process) and notes the degradation.
        string diff = """
            --- a/src/Service.cs
            +++ b/src/Service.cs
            @@ -99,1 +99,1 @@
            -    old
            +    new
            """;

        string output = ImpactTool.Run(index, resolver,
            target: null, changedPaths: null, diff: diff, maxDepth: 2, limit: 100, json: false,
            out int impactedCount, out _);

        // Whole-file degradation seeds Validate + Process → impacted = {Handle}; ProcessWorks the test.
        Assert.Contains("Handle", output);
        Assert.Equal(1, impactedCount);
        Assert.Contains("whole file", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Run_Diff_PlusPlusAdditionBody_DoesNotOverwriteThePathToPhantom_StillSeedsRealFile()
    {
        var (index, resolver) = BuildFixture();

        // A single-file diff to src/Service.cs whose hunk adds a "++"-prefixed line (e.g. a C++ "++x" / a doc
        // bullet). On the diff line that addition reads "+++ bullet added".
        //
        // With the pre-fix prefix-only parser, that body line was misread as a NEW-side file header and
        // OVERWROTE the current path from "src/Service.cs" to "bullet added". impact then resolved "bullet added"
        // to no indexed symbols and reported "No impact" for a file that genuinely changed Validate (line 11) —
        // a silently EMPTY result from valid input. With the hunk-body fix, the "+++ bullet added" line stays a
        // body line, the path stays src/Service.cs, Validate is seeded → impacted = {Process, Handle}.
        string diff =
            "--- a/src/Service.cs\n" +
            "+++ b/src/Service.cs\n" +
            "@@ -11,1 +11,2 @@\n" +
            "-    legacy\n" +
            "+    updated\n" +
            "+++ bullet added\n";

        string output = ImpactTool.Run(index, resolver,
            target: null, changedPaths: null, diff: diff, maxDepth: 2, limit: 100, json: false,
            out int impactedCount, out _);

        // Validate seeded (line 11 ∈ [10,14]) → dependents Process (hop 1), Handle + ProcessWorks (hop 2).
        Assert.Contains("Process", output);
        Assert.Contains("Handle", output);
        Assert.Contains("ProcessWorks", output);
        Assert.Equal(2, impactedCount); // Process + Handle (ProcessWorks is the test)
        // The phantom path must never have been adopted.
        Assert.DoesNotContain("bullet added", output);
    }

    // ---- both formats well-formed ----

    [Fact]
    public void Run_Json_IsWellFormedWithImpactedAndTestsArrays()
    {
        var (index, resolver) = BuildFixture();

        string output = ImpactTool.Run(index, resolver,
            target: "Validate", changedPaths: null, diff: null, maxDepth: 2, limit: 100, json: true, out _, out _);

        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        Assert.Equal(JsonValueKind.Array, root.GetProperty("impacted").ValueKind);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("tests").ValueKind);
        // Provenance on each impacted item.
        var first = root.GetProperty("impacted")[0];
        Assert.True(first.TryGetProperty("name", out _));
        Assert.True(first.TryGetProperty("kind", out _));
        Assert.True(first.TryGetProperty("file", out _));
        Assert.True(first.TryGetProperty("line", out _));
        Assert.True(first.TryGetProperty("hop", out _));
    }

    // ---- D10 telemetry work-proxy (nodes visited) ----

    [Fact]
    public void Run_SurfacesNodesVisited_EqualToTheReverseClosureSize()
    {
        var (index, resolver) = BuildFixture();

        // Reverse closure from Validate at depth 2 reaches Process (hop 1), Handle + ProcessWorks (hop 2) = 3
        // nodes (impacted + tests, before partition). nodesVisited is the D10 bytes_examined work proxy. Pins
        // finding 4: the count must be surfaced (was silently left 0).
        ImpactTool.Run(index, resolver,
            target: "Validate", changedPaths: null, diff: null, maxDepth: 2, limit: 100, json: false,
            out int impactedCount, out int nodesVisited);

        Assert.Equal(2, impactedCount);   // Process + Handle (ProcessWorks is the test)
        Assert.Equal(3, nodesVisited);    // Process + Handle + ProcessWorks (the whole reached set)
    }

    [Fact]
    public void Run_NodesVisited_IsZero_WhenNothingDependsOnTheTarget()
    {
        var (index, resolver) = BuildFixture();

        ImpactTool.Run(index, resolver,
            target: "Lonely", changedPaths: null, diff: null, maxDepth: 2, limit: 100, json: false,
            out _, out int nodesVisited);

        Assert.Equal(0, nodesVisited); // no dependents → no work beyond the seed
    }

    [Fact]
    public void Run_LimitCapsTheImpactedSet()
    {
        var (index, resolver) = BuildFixture();

        // limit 1 caps the reverse closure to a single reached node (Process, the nearest). The graph's Reach
        // applies the cap BEFORE the test partition, so with limit 1 only Process is reached.
        string output = ImpactTool.Run(index, resolver,
            target: "Validate", changedPaths: null, diff: null, maxDepth: 2, limit: 1, json: false,
            out int impactedCount, out _);

        Assert.Contains("Process", output);
        Assert.DoesNotContain("Handle", output);
        Assert.Equal(1, impactedCount);
    }

    // ---- routed wrapper / ctor shape ----

    [Fact]
    public void Impact_ExplicitWorkspaceId_DefaultsEnsureFreshTrue_AndRoutesToTargetIndex()
    {
        var currentIndex = EmptyIndex();
        var (targetIndex, _) = BuildFixture();
        string currentRoot = Path.Combine(Path.GetTempPath(), "miller-current-" + Guid.NewGuid().ToString("N"));
        string targetRoot = Path.Combine(Path.GetTempPath(), "miller-target-" + Guid.NewGuid().ToString("N"));
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(currentIndex, "current.db", "current-ws", currentRoot),
            ("target-ws", ReadToolRoutingTestSupport.ContextFor(targetIndex, "target.db", "target-ws", targetRoot)));
        var tool = new ImpactTool(provider);

        string output = tool.Impact(target: "Validate", workspace_id: "target-ws");

        Assert.Equal("target-ws", provider.LastWorkspaceId);
        Assert.True(provider.LastEnsureFresh);
        Assert.StartsWith("workspace: target-ws\n", output);
        Assert.DoesNotContain(targetRoot, output);
        Assert.Contains("Process", output);
    }

    [Fact]
    public void Impact_GitFlag_UsesWorkspaceRootDiff()
    {
        var (index, _) = BuildFixture();
        string root = Path.Combine(Path.GetTempPath(), "miller-git-impact-" + Guid.NewGuid().ToString("N"));
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(index, "current.db", "current-ws", root));
        var git = new RecordingGitDiffReader(GitDiffResult.Ok(ValidateDiff()));
        var tool = new ImpactTool(provider, git);

        string output = tool.Impact(git: true, max_depth: 2);

        Assert.Single(git.Requests);
        Assert.Equal(root, git.Requests[0].WorkspaceRoot);
        Assert.Null(git.Requests[0].BaseRef);
        Assert.False(git.Requests[0].Staged);
        Assert.Contains("Process", output);
        Assert.Contains("Handle", output);
        Assert.Contains("ProcessWorks", output);
    }

    [Fact]
    public void Impact_GitBaseAndStaged_ImplyGitAndRouteToSelectedWorkspace()
    {
        var currentIndex = EmptyIndex();
        var (targetIndex, _) = BuildFixture();
        string currentRoot = Path.Combine(Path.GetTempPath(), "miller-current-" + Guid.NewGuid().ToString("N"));
        string targetRoot = Path.Combine(Path.GetTempPath(), "miller-target-" + Guid.NewGuid().ToString("N"));
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(currentIndex, "current.db", "current-ws", currentRoot),
            ("target-ws", ReadToolRoutingTestSupport.ContextFor(targetIndex, "target.db", "target-ws", targetRoot)));
        var git = new RecordingGitDiffReader(GitDiffResult.Ok(ValidateDiff()));
        var tool = new ImpactTool(provider, git);

        string output = tool.Impact(@base: "origin/main", staged: true, workspace_id: "target-ws");

        Assert.Equal("target-ws", provider.LastWorkspaceId);
        Assert.True(provider.LastEnsureFresh);
        Assert.Single(git.Requests);
        Assert.Equal(targetRoot, git.Requests[0].WorkspaceRoot);
        Assert.Equal("origin/main", git.Requests[0].BaseRef);
        Assert.True(git.Requests[0].Staged);
        Assert.StartsWith("workspace: target-ws\n", output);
        Assert.Contains("Process", output);
    }

    [Fact]
    public void Impact_GitFlag_EmptyDiffReturnsNoImpactNote()
    {
        var (index, _) = BuildFixture();
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(index, "current.db", "current-ws", "/repo"));
        var git = new RecordingGitDiffReader(GitDiffResult.Ok(""));
        var tool = new ImpactTool(provider, git);

        string output = tool.Impact(git: true);

        Assert.Single(git.Requests);
        Assert.Contains("No impact", output);
        Assert.Contains("git diff is empty", output);
    }

    [Fact]
    public void Impact_GitFlag_FailedDiffReturnsFailure()
    {
        var (index, _) = BuildFixture();
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(index, "current.db", "current-ws", "/repo"));
        var git = new RecordingGitDiffReader(GitDiffResult.Fail("fatal: not a git repository"));
        var tool = new ImpactTool(provider, git);

        string output = tool.Impact(git: true);

        Assert.Single(git.Requests);
        Assert.Contains("impact failed: git diff failed", output);
        Assert.Contains("fatal: not a git repository", output);
    }

    [Fact]
    public void Impact_GitFlag_WithAnotherInputReturnsUsage()
    {
        var (index, _) = BuildFixture();
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(index, "current.db", "current-ws", "/repo"));
        var git = new RecordingGitDiffReader(GitDiffResult.Ok(ValidateDiff()));
        var tool = new ImpactTool(provider, git);

        string output = tool.Impact(target: "Validate", git: true);

        Assert.Empty(git.Requests);
        Assert.Contains("exactly one", output);
    }

    [Fact]
    public void Impact_NoArgs_DefaultsToWorkingTreeGitDiff()
    {
        var (index, _) = BuildFixture();
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(index, "current.db", "current-ws", "/repo"));
        var git = new RecordingGitDiffReader(GitDiffResult.Ok(ValidateDiff()));
        var tool = new ImpactTool(provider, git);

        string output = tool.Impact();

        Assert.Single(git.Requests);
        Assert.Equal("/repo", git.Requests[0].WorkspaceRoot);
        Assert.Null(git.Requests[0].BaseRef);
        Assert.False(git.Requests[0].Staged);
        Assert.Contains("Process", output);
        Assert.Contains("Handle", output);
        Assert.Contains("ProcessWorks", output);
    }

    [Fact]
    public void Impact_NoArgs_NonGitWorkspace_ReturnsUsageNotError()
    {
        var (index, _) = BuildFixture();
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(index, "current.db", "current-ws", "/repo"));
        var git = new RecordingGitDiffReader(GitDiffResult.Fail("fatal: not a git repository"));
        var tool = new ImpactTool(provider, git);

        string output = tool.Impact();

        Assert.Single(git.Requests);
        Assert.DoesNotContain("impact failed", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("target", output);
        Assert.Contains("changed_paths", output);
    }

    [Fact]
    public void Impact_NoArgs_EmptyDiff_ReturnsNoImpactNote()
    {
        var (index, _) = BuildFixture();
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(index, "current.db", "current-ws", "/repo"));
        var git = new RecordingGitDiffReader(GitDiffResult.Ok(""));
        var tool = new ImpactTool(provider, git);

        string output = tool.Impact();

        Assert.Single(git.Requests);
        Assert.Contains("git diff is empty", output);
    }

    [Fact]
    public void Ctor_RequiresWorkspaceIndexProvider()
    {
        var (index, _) = BuildFixture();
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(index, "current.db", "current-ws", "/current"));

        var tool = new ImpactTool(provider);
        Assert.NotNull(tool);

        Assert.Throws<ArgumentNullException>(() => new ImpactTool(null!));
    }

    // ---- Rust cross-language classification (CT revision-delta design §2 / Task 3) ----
    //
    // julie's test_detection.rs flags rust #[test]/#[tokio::test] functions is_test=1 (verified live against
    // julie's own self-extract: crates/julie-tools/src/tests/blast_radius_formatting_tests.rs and
    // src/tests/tools/spillover_tests.rs both carry is_test=1 on every attributed fn). Miller's read layer
    // (SqliteSymbolReader) and this partition (symbol.IsTest ? tests : impacted) are both a verbatim,
    // language-agnostic column read/branch — there is no per-language gate to add. These fixtures pin that the
    // existing chain already classifies rust correctly end-to-end (both the legacy Run path and Task 2's
    // index-revision delta renderer), and that a non-attributed helper living in the same test module is not
    // over-classified.
    private const string RustParseConfigId = "20000000000000000000000000000001";
    private const string RustTestId = "20000000000000000000000000000002";
    private const string RustHelperId = "20000000000000000000000000000003";

    // parse_config (production) ← test_parse_config_rejects_empty_input (a #[test] fn that calls it)
    //                            ← make_fixture_config (a non-test helper in the same test file that calls it)
    private static (MillerRepositoryIndex index, SmartTargetResolver resolver) BuildRustFixture()
    {
        var symbols = new List<IndexedSymbol>
        {
            new(0, RustParseConfigId, "parse_config", "fn parse_config(input: &str) -> Config", "function", "rust",
                "crates/config/src/parser.rs", 10, 14, null, false),
            new(1, RustTestId, "test_parse_config_rejects_empty_input",
                "fn test_parse_config_rejects_empty_input()", "function", "rust",
                "crates/config/src/tests/parser_tests.rs", 8, 12, null, IsTest: true),
            new(2, RustHelperId, "make_fixture_config", "fn make_fixture_config() -> Config", "function", "rust",
                "crates/config/src/tests/parser_tests.rs", 20, 24, null, false),
        };
        var edges = new[]
        {
            new GraphEdge(RustTestId, RustParseConfigId, "calls"),
            new GraphEdge(RustHelperId, RustParseConfigId, "calls"),
        };
        var index = MillerRepositoryIndex.Build(symbols, edges);
        return (index, new SmartTargetResolver(index));
    }

    [Fact]
    public void Run_RustAttributedTestFunction_ReachedByGraph_ClassifiesAsTest_NonTestHelperExcluded()
    {
        var (index, resolver) = BuildRustFixture();

        string output = ImpactTool.Run(index, resolver,
            target: "parse_config", changedPaths: null, diff: null, maxDepth: 2, limit: 100, json: true,
            out int impactedCount, out _);

        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;

        var testNames = root.GetProperty("tests").EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()).ToList();
        var impactedNames = root.GetProperty("impacted").EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()).ToList();

        Assert.Contains("test_parse_config_rejects_empty_input", testNames);
        Assert.DoesNotContain("test_parse_config_rejects_empty_input", impactedNames);

        // A non-attributed helper reached via the same edge shape must stay out of tests[].
        Assert.Contains("make_fixture_config", impactedNames);
        Assert.DoesNotContain("make_fixture_config", testNames);
        Assert.Equal(1, impactedCount);

        // Payload shape matches C#: name + file (kind/line/hop travel too, but name+file is the pinned minimum).
        var testEntry = root.GetProperty("tests").EnumerateArray()
            .Single(e => e.GetProperty("name").GetString() == "test_parse_config_rejects_empty_input");
        Assert.Equal("crates/config/src/tests/parser_tests.rs", testEntry.GetProperty("file").GetString());
    }

    [Fact]
    public void RenderIndexRevisionDelta_RustAttributedTestFunction_ClassifiesAsTest()
    {
        // Exercises Task 2's delta renderer directly — the SAME symbol.IsTest bit drives this path.
        var (index, _) = BuildRustFixture();

        string output = ImpactTool.RenderIndexRevisionDelta(
            workspaceId: "current",
            complete: true,
            fromRevision: 1,
            toRevision: 2,
            changedPaths: new[] { "crates/config/src/parser.rs" },
            index: index,
            graph: index.Graph,
            maxDepth: 2,
            limit: 100,
            json: true);

        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;

        var testNames = root.GetProperty("tests").EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()).ToList();
        var impactedNames = root.GetProperty("impacted").EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()).ToList();

        Assert.Contains("test_parse_config_rejects_empty_input", testNames);
        Assert.Contains("make_fixture_config", impactedNames);
        Assert.DoesNotContain("make_fixture_config", testNames);
        Assert.Equal("exhausted", Traversal(root).GetProperty("status").GetString());
        AssertReturnedCountMatchesEnvelope(root);
    }

    [Fact]
    public void RenderIndexRevisionDelta_CompleteEmptyDelta_ReportsNoChangesAndEffectiveBounds()
    {
        string output = ImpactTool.RenderIndexRevisionDelta(
            "current", complete: true, 4, 4, Array.Empty<string>(),
            index: null, graph: null, maxDepth: 0, limit: 0, json: true);

        using var doc = JsonDocument.Parse(output);
        JsonElement traversal = Traversal(doc.RootElement);
        Assert.Equal("not_run", traversal.GetProperty("status").GetString());
        Assert.Equal("no_changes", traversal.GetProperty("reason").GetString());
        Assert.Equal(1, traversal.GetProperty("max_depth").GetInt32());
        Assert.Equal(1, traversal.GetProperty("limit").GetInt32());
        Assert.Equal(0, traversal.GetProperty("reached_count").GetInt32());
        Assert.Equal(0, traversal.GetProperty("returned_count").GetInt32());
        Assert.False(traversal.GetProperty("truncated_by_depth").GetBoolean());
        Assert.False(traversal.GetProperty("truncated_by_limit").GetBoolean());
        Assert.Empty(traversal.GetProperty("seeded_paths").EnumerateArray());
        Assert.Empty(traversal.GetProperty("unseeded_paths").EnumerateArray());
    }

    [Fact]
    public void RenderIndexRevisionDelta_UnavailableDelta_ReportsDeltaUnavailable()
    {
        string output = ImpactTool.RenderIndexRevisionDelta(
            "current", complete: false, 9, 2, new[] { "src/Seed.cs" },
            index: null, graph: null, maxDepth: 2, limit: 100, json: true,
            deltaReason: "from_after_current");

        using var doc = JsonDocument.Parse(output);
        JsonElement root = doc.RootElement;
        JsonElement traversal = Traversal(root);
        Assert.Equal("unavailable", root.GetProperty("delta_status").GetString());
        Assert.Empty(root.GetProperty("changed_paths").EnumerateArray());
        Assert.Equal("not_run", traversal.GetProperty("status").GetString());
        Assert.Equal("delta_unavailable", traversal.GetProperty("reason").GetString());
    }

    [Fact]
    public void RenderIndexRevisionDelta_ChangedPathsWithoutLoadedIndex_ReportsIndexUnavailable()
    {
        MillerRepositoryIndex index = BuildTraversalEvidenceFixture();
        string output = ImpactTool.RenderIndexRevisionDelta(
            "current", complete: true, 1, 2, new[] { "src/Seed.cs" },
            index, index.Graph, maxDepth: 2, limit: 100, json: true,
            indexAvailable: false);

        using var doc = JsonDocument.Parse(output);
        JsonElement traversal = Traversal(doc.RootElement);
        Assert.Equal("not_run", traversal.GetProperty("status").GetString());
        Assert.Equal("index_unavailable", traversal.GetProperty("reason").GetString());
        Assert.Empty(traversal.GetProperty("seeded_paths").EnumerateArray());
        Assert.Empty(traversal.GetProperty("unseeded_paths").EnumerateArray());
    }

    [Fact]
    public void RenderIndexRevisionDelta_AllUnseededPaths_ReportsEveryPathAndNoSeeds()
    {
        MillerRepositoryIndex index = EmptyIndex();
        string[] paths = ["config/settings.json", "src/Deleted.cs"];
        string output = ImpactTool.RenderIndexRevisionDelta(
            "current", complete: true, 1, 2, paths,
            index, index.Graph, maxDepth: 2, limit: 100, json: true);

        using var doc = JsonDocument.Parse(output);
        JsonElement traversal = Traversal(doc.RootElement);
        Assert.Equal("not_run", traversal.GetProperty("status").GetString());
        Assert.Equal("no_seeds", traversal.GetProperty("reason").GetString());
        Assert.Empty(traversal.GetProperty("seeded_paths").EnumerateArray());
        Assert.Equal(paths, traversal.GetProperty("unseeded_paths").EnumerateArray()
            .Select(static item => item.GetString()));
    }

    [Fact]
    public void RenderIndexRevisionDelta_MixedSeededAndUnseededPaths_CanExhaustGraph()
    {
        MillerRepositoryIndex index = BuildTraversalEvidenceFixture();
        string output = ImpactTool.RenderIndexRevisionDelta(
            "current", complete: true, 1, 2, new[] { "src/Seed.cs", "config/settings.json" },
            index, index.Graph, maxDepth: 5, limit: 100, json: true);

        using var doc = JsonDocument.Parse(output);
        JsonElement root = doc.RootElement;
        JsonElement traversal = Traversal(root);
        Assert.Equal("exhausted", traversal.GetProperty("status").GetString());
        Assert.Equal("complete", traversal.GetProperty("reason").GetString());
        Assert.Equal(3, traversal.GetProperty("reached_count").GetInt32());
        Assert.Equal(new[] { "src/Seed.cs" }, traversal.GetProperty("seeded_paths").EnumerateArray()
            .Select(static item => item.GetString()));
        Assert.Equal(new[] { "config/settings.json" }, traversal.GetProperty("unseeded_paths").EnumerateArray()
            .Select(static item => item.GetString()));
        AssertReturnedCountMatchesEnvelope(root);
    }

    [Fact]
    public void RenderIndexRevisionDelta_DepthBoundary_ReportsDepthTruncation()
    {
        MillerRepositoryIndex index = BuildTraversalEvidenceFixture();
        string output = ImpactTool.RenderIndexRevisionDelta(
            "current", true, 1, 2, new[] { "src/Seed.cs" }, index, index.Graph, 1, 100, true);

        using var doc = JsonDocument.Parse(output);
        JsonElement traversal = Traversal(doc.RootElement);
        Assert.Equal("truncated", traversal.GetProperty("status").GetString());
        Assert.Equal("depth", traversal.GetProperty("reason").GetString());
        Assert.Equal(2, traversal.GetProperty("reached_count").GetInt32());
        Assert.True(traversal.GetProperty("truncated_by_depth").GetBoolean());
        Assert.False(traversal.GetProperty("truncated_by_limit").GetBoolean());
        AssertReturnedCountMatchesEnvelope(doc.RootElement);
    }

    [Fact]
    public void RenderIndexRevisionDelta_LimitBoundary_ReportsPreLimitCount()
    {
        MillerRepositoryIndex index = BuildTraversalEvidenceFixture();
        string output = ImpactTool.RenderIndexRevisionDelta(
            "current", true, 1, 2, new[] { "src/Seed.cs" }, index, index.Graph, 5, 2, true);

        using var doc = JsonDocument.Parse(output);
        JsonElement traversal = Traversal(doc.RootElement);
        Assert.Equal("truncated", traversal.GetProperty("status").GetString());
        Assert.Equal("limit", traversal.GetProperty("reason").GetString());
        Assert.Equal(3, traversal.GetProperty("reached_count").GetInt32());
        Assert.False(traversal.GetProperty("truncated_by_depth").GetBoolean());
        Assert.True(traversal.GetProperty("truncated_by_limit").GetBoolean());
        AssertReturnedCountMatchesEnvelope(doc.RootElement);
    }

    [Fact]
    public void RenderIndexRevisionDelta_DepthAndLimitBoundaries_ReportCombinedTruncation()
    {
        MillerRepositoryIndex index = BuildTraversalEvidenceFixture();
        string output = ImpactTool.RenderIndexRevisionDelta(
            "current", true, 1, 2, new[] { "src/Seed.cs" }, index, index.Graph, 1, 1, true);

        using var doc = JsonDocument.Parse(output);
        JsonElement traversal = Traversal(doc.RootElement);
        Assert.Equal("truncated", traversal.GetProperty("status").GetString());
        Assert.Equal("depth_and_limit", traversal.GetProperty("reason").GetString());
        Assert.Equal(2, traversal.GetProperty("reached_count").GetInt32());
        Assert.True(traversal.GetProperty("truncated_by_depth").GetBoolean());
        Assert.True(traversal.GetProperty("truncated_by_limit").GetBoolean());
        AssertReturnedCountMatchesEnvelope(doc.RootElement);
    }

    private sealed class RecordingGitDiffReader(params GitDiffResult[] results) : IGitDiffReader
    {
        private readonly Queue<GitDiffResult> _results = new(results);
        private readonly List<GitDiffRequest> _requests = new();

        public IReadOnlyList<GitDiffRequest> Requests => _requests;

        public GitDiffResult Read(GitDiffRequest request)
        {
            _requests.Add(request);
            return _results.Count == 0 ? GitDiffResult.Ok("") : _results.Dequeue();
        }
    }

    private static string ValidateDiff() =>
        """
        --- a/src/Service.cs
        +++ b/src/Service.cs
        @@ -11,1 +11,1 @@
        -    old
        +    new
        """;
}
