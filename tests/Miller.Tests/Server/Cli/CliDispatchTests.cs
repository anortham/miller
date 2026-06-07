using Microsoft.Data.Sqlite;
using System.Text.Json;
using Miller.Indexing;
using Miller.Server;
using Miller.Server.Cli;
using Miller.Server.Tools;
using Miller.Server.Workspaces;
using Miller.Tests.Indexing;
using Xunit;

namespace Miller.Tests.Server.Cli;

/// <summary>
/// Pins the CLI dispatch <see cref="CliDispatch"/> end-to-end in-process (no subprocess, no MCP host): verbs map
/// to the right tool core, exit codes follow the contract (0 ok / 2 usage / 3 no-index), and output flows to the
/// injected writers. The index comes from a real <see cref="JulieDbFixture"/> <c>symbols.db</c> and the registry
/// from a seeded temp DB, so these stay in the fast suite. <see cref="WorkspaceContext"/> is constructed directly
/// (rather than from a CWD) so the tests never chdir — that would race xUnit's parallel collections.
/// </summary>
public sealed class CliDispatchTests : IDisposable
{
    private readonly string _dir;
    private readonly string _registryDb;

    public CliDispatchTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _registryDb = Path.Combine(_dir, "workspaces.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private WorkspaceContext Context(string extractDbPath, string? workspaceRoot = null) =>
        new(
            WorkspaceRoot: workspaceRoot ?? _dir,
            ExtractDbPath: extractDbPath,
            TelemetryDbPath: Path.Combine(_dir, "telemetry.db"),
            RegistryDbPath: _registryDb,
            ToolsRoot: Path.Combine(_dir, ".tools"),
            WorkspaceId: null);

    private static (int Code, string Out, string Err) Run(
        IReadOnlyList<string> args,
        WorkspaceContext ctx,
        IDashboardLauncher? dashboardLauncher = null)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = dashboardLauncher is null
            ? CliDispatch.Run(args, ctx, stdout, stderr)
            : CliDispatch.Run(args, ctx, stdout, stderr, dashboardLauncher);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private static JulieDbFixture DbWithRegion(string path, string text)
    {
        int newline = text.IndexOf('\n', StringComparison.Ordinal);
        int endByte = newline < 0 ? System.Text.Encoding.UTF8.GetByteCount(text) : newline;
        const string symbolId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        return JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                new JulieDbFixture.SymbolRow(symbolId, "TargetType", "class", "csharp",
                    path, "public class TargetType", 2, ParentId: null),
            },
            fileContent: new Dictionary<string, string> { [path] = text },
            sourceRegions: new[]
            {
                new JulieDbFixture.SourceRegionRow(
                    "region-target", "file:" + path, path, "csharp", "comment", symbolId,
                    1, 1, 1, endByte, 0, endByte, null),
            },
            revisions: new[]
            {
                new JulieDbFixture.RevisionRow(1, "fresh"),
            });
    }

    private static void WriteRegionSearchDbFor(JulieDbFixture fixture, long revision) =>
        SearchIndexWriter.Write(
            SymbolSearchSidecar.SearchDbPathFor(fixture.DbPath),
            SqliteSymbolReader.Read(fixture.DbPath),
            revision,
            fixture.DbPath,
            fixture.WorkspaceRoot,
            RegionIndexOptions.EnabledDefault);

    private static JulieDbFixture DbWithSource(string marker, long revision)
    {
        const string path = "src/Source.cs";
        string text = $$"""
            public class Source
            {
                public void Handle()
                {
                    throw new InvalidOperationException("{{marker}}");
                }
            }
            """;
        return JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                new JulieDbFixture.SymbolRow("sym-source", "Source", "class", "csharp",
                    path, "public class Source", 1, ParentId: null)
                {
                    EndLine = 7,
                },
                new JulieDbFixture.SymbolRow("sym-handle", "Handle", "method", "csharp",
                    path, "public void Handle()", 3, ParentId: "sym-source")
                {
                    EndLine = 6,
                },
            },
            fileContent: new Dictionary<string, string> { [path] = text },
            revisions: new[]
            {
                new JulieDbFixture.RevisionRow(revision, "fresh"),
            });
    }

    private static JulieDbFixture DbWithContentDocs(string marker, long revision) =>
        JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new JulieDbFixture.SymbolRow("sym-source", "Source", "class", "csharp",
                    "src/Source.cs", "public class Source", 1, ParentId: null)
                {
                    EndLine = 1,
                },
            ],
            fileContent: new Dictionary<string, string>
            {
                ["src/Source.cs"] = $"public class Source {{ string Marker = \"{marker}\"; }}",
            },
            extraFiles:
            [
                new JulieDbFixture.FileSpec("docs/guide.md")
                {
                    Language = "markdown",
                    DiskText = $"# Guide\n{marker} from docs.\n",
                },
                new JulieDbFixture.FileSpec("miller.json")
                {
                    Language = "json",
                    DiskText = $$"""{"marker":"{{marker}}"}""",
                },
            ],
            revisions:
            [
                new JulieDbFixture.RevisionRow(revision, "fresh"),
            ]);

    private static JulieDbFixture DbWithAmbiguousSymbols() =>
        JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, new[]
        {
            new JulieDbFixture.SymbolRow("b0000000000000000000000000000001", "Duplicate", "method", "csharp",
                "src/One.cs", "public void Duplicate()", 10, null),
            new JulieDbFixture.SymbolRow("b0000000000000000000000000000002", "Duplicate", "method", "csharp",
                "src/Two.cs", "public void Duplicate()", 20, null),
        });

    private static JulieDbFixture DbWithDuplicateWidgets() =>
        JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            Enumerable.Range(0, 8)
                .Select(i => new JulieDbFixture.SymbolRow(
                    $"c{i:0000000000000000000000000000000}"[..32],
                    "Widget",
                    "class",
                    "csharp",
                    $"src/Widget{i}.cs",
                    $"public class Widget{i}",
                    i + 1,
                    ParentId: null))
                .ToArray());

    [Fact]
    public void IsCliInvocation_ServeAndEmptyAreServer_EverythingElseIsCli()
    {
        Assert.False(CliDispatch.IsCliInvocation(Array.Empty<string>()));
        Assert.False(CliDispatch.IsCliInvocation(new[] { "serve" }));
        Assert.False(CliDispatch.IsCliInvocation(new[] { "SERVE" }));   // case-insensitive
        Assert.True(CliDispatch.IsCliInvocation(new[] { "search", "x" }));
        Assert.True(CliDispatch.IsCliInvocation(new[] { "--version" }));
    }

    [Fact]
    public void Version_PrintsBuildVersion()
    {
        var (code, outText, _) = Run(new[] { "version" }, Context(Path.Combine(_dir, "symbols.db")));
        Assert.Equal(0, code);
        Assert.StartsWith("0.1.0", outText.Trim());
    }

    [Fact]
    public void Help_ListsCommands()
    {
        var (code, outText, _) = Run(new[] { "help" }, Context(Path.Combine(_dir, "symbols.db")));
        Assert.Equal(0, code);
        Assert.Contains("Commands:", outText);
        Assert.Contains("search", outText);
        Assert.Contains("serve", outText);
    }

    [Fact]
    public void UnknownVerb_IsUsageErrorExitTwo()
    {
        var (code, _, errText) = Run(new[] { "frobnicate" }, Context(Path.Combine(_dir, "symbols.db")));
        Assert.Equal(2, code);
        Assert.Contains("unknown command", errText);
    }

    [Fact]
    public void Dashboard_ReusesRunningInstanceAndPrintsWorkspaceUrl()
    {
        var launcher = new RecordingDashboardLauncher(
            new DashboardLaunchResult(
                DashboardLaunchOutcome.AlreadyRunning,
                new Uri("http://127.0.0.1:4977/?workspace_id=ws-current"),
                ProcessId: 123,
                Message: "already running"));

        var (code, outText, errText) = Run(
            new[] { "dashboard" },
            Context(Path.Combine(_dir, "symbols.db")),
            launcher);

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("dashboard already running", outText);
        Assert.Contains("http://127.0.0.1:4977/?workspace_id=ws-current", outText);
        Assert.Equal(4977, launcher.Requests.Single().Port);
    }

    [Fact]
    public void Dashboard_PortFlagOverridesDefault()
    {
        var launcher = new RecordingDashboardLauncher(
            new DashboardLaunchResult(
                DashboardLaunchOutcome.Started,
                new Uri("http://127.0.0.1:5001/?workspace_id=ws-current"),
                ProcessId: 456,
                Message: "started"));

        var (code, outText, errText) = Run(
            new[] { "dashboard", "--port", "5001" },
            Context(Path.Combine(_dir, "symbols.db")),
            launcher);

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("dashboard started", outText);
        Assert.Contains("pid 456", outText);
        Assert.Equal(5001, launcher.Requests.Single().Port);
    }

    [Fact]
    public void Dashboard_LaunchFailureIsOperationalExitThree()
    {
        var launcher = new RecordingDashboardLauncher(
            new DashboardLaunchResult(
                DashboardLaunchOutcome.Failed,
                new Uri("http://127.0.0.1:4977/?workspace_id=ws-current"),
                ProcessId: null,
                Message: "dashboard binary not found"));

        var (code, outText, errText) = Run(
            new[] { "dashboard" },
            Context(Path.Combine(_dir, "symbols.db")),
            launcher);

        Assert.Equal(3, code);
        Assert.Empty(outText);
        Assert.Contains("dashboard binary not found", errText);
    }

    [Fact]
    public void Search_NoQuery_IsUsageErrorExitTwo()
    {
        var (code, _, errText) = Run(new[] { "search" }, Context(Path.Combine(_dir, "symbols.db")));
        Assert.Equal(2, code);
        Assert.Contains("usage:", errText);
    }

    [Fact]
    public void Search_NoIndex_ExitsThreeWithGuidance()
    {
        var (code, _, errText) = Run(new[] { "search", "UserService" }, Context(Path.Combine(_dir, "nope.db")));
        Assert.Equal(3, code);
        Assert.Contains("no Miller index", errText);
    }

    [Fact]
    public void Search_FindsAKnownSymbol()
    {
        using var fx = JulieDbFixture.CreateDefault();
        var (code, outText, _) = Run(new[] { "search", "UserService" }, Context(fx.DbPath));
        Assert.Equal(0, code);
        Assert.Contains("UserService", outText);
    }

    [Fact]
    public void Search_UsesSymbolProjectionWithoutFullGraphLoad()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        SqliteFixtureMutator.DropRelationshipsTable(fx.DbPath);

        var (code, outText, errText) = Run(new[] { "search", "GetUser" }, Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("GetUser", outText);
        Assert.Contains("auth/UserService.cs", outText);
    }

    [Fact]
    public void Search_ModeSource_ReadsContentCorpusSidecar()
    {
        using var fx = DbWithSource("KnownSourceError", revision: 7);
        ContentCorpusWriter.Write(
            ContentCorpusSidecar.ContentDbPathFor(fx.DbPath),
            fx.DbPath,
            fx.WorkspaceRoot,
            workspaceId: "current-ws",
            revision: 7);

        var (code, outText, errText) = Run(
            new[] { "search", "KnownSourceError", "--mode", "source" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("src/Source.cs:5  workspace_source  Handle", outText);
        Assert.Contains("KnownSourceError", outText);
    }

    [Fact]
    public void Search_ModeContent_ReadsContentCorpusSidecar()
    {
        using var fx = DbWithContentDocs("KnownContentMarker", revision: 7);
        ContentCorpusWriter.Write(
            ContentCorpusSidecar.ContentDbPathFor(fx.DbPath),
            fx.DbPath,
            fx.WorkspaceRoot,
            workspaceId: "current-ws",
            revision: 7);
        File.Delete(Path.Combine(fx.WorkspaceRoot, "docs", "guide.md"));
        File.Delete(Path.Combine(fx.WorkspaceRoot, "miller.json"));

        var (code, outText, errText) = Run(
            new[] { "search", "KnownContentMarker", "--mode", "content" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("docs/guide.md:2", outText);
        Assert.Contains("KnownContentMarker", outText);
        Assert.DoesNotContain(TextContentKind.WorkspaceDocs, outText);
        Assert.DoesNotContain("src/Source.cs", outText);
    }

    [Fact]
    public void Content_ImportSearchReadListAndRemove_WorkWithoutSymbolsDb()
    {
        string logPath = Path.Combine(_dir, "ci.log");
        File.WriteAllText(logPath, """
            build started
            CliExternalMarker failed in integration
            build finished
            """);
        var ctx = Context(Path.Combine(_dir, ".miller", "symbols.db"), _dir);

        var (importCode, importOut, importErr) = Run(new[] { "content", "import", logPath, "--json" }, ctx);

        Assert.False(File.Exists(ctx.ExtractDbPath));
        Assert.Equal(0, importCode);
        Assert.Empty(importErr);
        Assert.DoesNotContain("CliExternalMarker", importOut);
        using JsonDocument importDoc = JsonDocument.Parse(importOut);
        string sourceId = importDoc.RootElement.GetProperty("source_id").GetString()!;

        var (searchCode, searchOut, searchErr) = Run(new[] { "content", "search", "CliExternalMarker" }, ctx);
        Assert.Equal(0, searchCode);
        Assert.Empty(searchErr);
        Assert.Contains("ci.log:2  external_file", searchOut);
        Assert.Contains("CliExternalMarker failed", searchOut);

        var (readCode, readOut, readErr) = Run(
            new[] { "content", "read", "--source-id", sourceId, "--line", "2", "--context-lines", "0" },
            ctx);
        Assert.Equal(0, readCode);
        Assert.Empty(readErr);
        Assert.Contains("2: CliExternalMarker failed in integration", readOut);
        Assert.DoesNotContain("build started", readOut);

        var (listCode, listOut, listErr) = Run(new[] { "content", "list", "--json" }, ctx);
        Assert.Equal(0, listCode);
        Assert.Empty(listErr);
        using JsonDocument listDoc = JsonDocument.Parse(listOut);
        Assert.Equal(sourceId, listDoc.RootElement[0].GetProperty("source_id").GetString());

        var (removeCode, removeOut, removeErr) = Run(new[] { "content", "remove", "--source-id", sourceId }, ctx);
        Assert.Equal(0, removeCode);
        Assert.Empty(removeErr);
        Assert.Contains("removed", removeOut);
    }

    [Theory]
    [InlineData("search", "GetUser", "auth/UserService.cs")]
    [InlineData("inspect", "GetUser", "Gets a user by id.")]
    [InlineData("context", "GetUser", "# context bundle")]
    [InlineData("impact", "GetUser", "# impacted")]
    [InlineData("trace", "GetUser", "# trace GetUser")]
    public void ReadVerbs_WorkspaceIdSelector_ReadsRegisteredWorkspace(
        string verb,
        string target,
        string expected)
    {
        using var fx = JulieDbFixture.CreateForInspect();
        SeedRegisteredWorkspace("target-ws", "target-111111111111", fx.WorkspaceRoot, fx.DbPath);
        string currentDb = Path.Combine(_dir, "current", ".miller", "symbols.db");

        var (code, outText, errText) = Run(
            new[] { verb, target, "--workspace-id", "target-ws" },
            Context(currentDb));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains(expected, outText);
    }

    [Fact]
    public void Search_WorkspaceAlias_ReadsRegisteredWorkspaceByRelativePath()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        string targetRoot = Path.Combine(_dir, "target-workspace");
        Directory.CreateDirectory(targetRoot);
        SeedRegisteredWorkspace("target-ws", "target-111111111111", targetRoot, fx.DbPath);
        string currentDb = Path.Combine(_dir, "current", ".miller", "symbols.db");

        var (code, outText, errText) = Run(
            new[] { "search", "GetUser", "--workspace", "target-workspace" },
            Context(currentDb));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("auth/UserService.cs", outText);
    }

    [Fact]
    public void Search_WorkspaceIdSelector_UnknownSelectorIsUsageErrorExitTwo()
    {
        string currentDb = Path.Combine(_dir, "current", ".miller", "symbols.db");

        var (code, _, errText) = Run(
            new[] { "search", "GetUser", "--workspace-id", "does-not-exist" },
            Context(currentDb));

        Assert.Equal(2, code);
        Assert.Contains("unknown workspace selector", errText);
        Assert.DoesNotContain("no Miller index", errText);
    }

    [Fact]
    public void Search_FilePatternAndLanguageFlags_FilterResults()
    {
        using var fx = JulieDbFixture.CreateForInspect();

        var (code, outText, errText) = Run(
            new[] { "search", "GetUser", "--file-pattern", "auth/**", "--language", "csharp" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("auth/UserService.cs", outText);
        Assert.DoesNotContain("web/Controller.cs", outText);
    }

    [Fact]
    public void Search_ExcludeTestsFlag_FiltersExactIdentifierTestHits()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                new JulieDbFixture.SymbolRow(
                    "b0000000000000000000000000000001",
                    "Flask",
                    "class",
                    "python",
                    "tests/test_config.py",
                    "class Flask",
                    202,
                    ParentId: null),
                new JulieDbFixture.SymbolRow(
                    "b0000000000000000000000000000002",
                    "Flask",
                    "class",
                    "python",
                    "src/flask/app.py",
                    "class Flask",
                    47,
                    ParentId: null),
            });

        var (code, outText, errText) = Run(
            new[] { "search", "Flask", "--exclude-tests" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("src/flask/app.py", outText);
        Assert.DoesNotContain("tests/test_config.py", outText);
    }

    [Fact]
    public void Search_DefaultLimit_RendersSixActionableRows_WithOverflowNote()
    {
        using var fx = DbWithDuplicateWidgets();

        var (code, outText, errText) = Run(new[] { "search", "Widget" }, Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("src/Widget5.cs", outText);
        Assert.DoesNotContain("src/Widget6.cs", outText);
        Assert.Contains("… 2 more (raise limit)", outText);
    }

    [Fact]
    public void Search_Json_EmitsAJsonArray()
    {
        using var fx = JulieDbFixture.CreateDefault();
        var (code, outText, _) = Run(new[] { "search", "UserService", "--json" }, Context(fx.DbPath));
        Assert.Equal(0, code);
        Assert.StartsWith("[", outText.Trim());
    }

    [Fact]
    public void Search_BadRegions_IsUsageErrorExitTwo()
    {
        using var fx = JulieDbFixture.CreateDefault();

        var (code, _, errText) = Run(new[] { "search", "TODO", "--regions", "unknown" }, Context(fx.DbPath));

        Assert.Equal(2, code);
        Assert.Contains("regions must be", errText);
    }

    [Fact]
    public void Search_Regions_UsesFreshDiskRegionIndex()
    {
        const string path = "src/Target.cs";
        const string text = "// TODO cli region\nclass TargetType {}\n";
        using var fx = DbWithRegion(path, text);
        WriteRegionSearchDbFor(fx, revision: 1);

        string? oldRegion = Environment.GetEnvironmentVariable(RegionIndexOptions.EnvVar);
        try
        {
            Environment.SetEnvironmentVariable(RegionIndexOptions.EnvVar, "1");

            var (code, outText, errText) = Run(
                new[] { "search", "TODO", "--regions", "comment" },
                Context(fx.DbPath, fx.WorkspaceRoot));

            Assert.Equal(0, code);
            Assert.Empty(errText);
            Assert.Contains("src/Target.cs:1  comment  TargetType", outText);
            Assert.Contains("// TODO cli region", outText);
        }
        finally
        {
            Environment.SetEnvironmentVariable(RegionIndexOptions.EnvVar, oldRegion);
        }
    }

    [Fact]
    public void Inspect_File_ListsItsSymbols()
    {
        using var fx = JulieDbFixture.CreateDefault();
        var (code, outText, _) = Run(new[] { "inspect", "auth/UserService.cs" }, Context(fx.DbPath));
        Assert.Equal(0, code);
        Assert.Contains("GetUser", outText);
    }

    [Fact]
    public void Inspect_Summary_UsesSymbolProjectionWithoutFullGraphLoad()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        SqliteFixtureMutator.DropRelationshipsTable(fx.DbPath);

        var (code, outText, errText) = Run(new[] { "inspect", "GetUser" }, Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("Gets a user by id.", outText);
        Assert.Contains("auth/UserService.cs", outText);
    }

    [Fact]
    public void Inspect_Full_AmbiguousTarget_UsesSymbolProjectionWithoutFullGraphLoad()
    {
        using var fx = DbWithAmbiguousSymbols();
        SqliteFixtureMutator.DropRelationshipsTable(fx.DbPath);

        var (code, outText, errText) = Run(
            new[] { "inspect", "Duplicate", "--depth", "full" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("Multiple candidates", outText);
        Assert.Contains("src/One.cs", outText);
        Assert.Contains("src/Two.cs", outText);
    }

    [Fact]
    public void Inspect_Full_UniqueTarget_UsesSymbolProjectionWithoutFullGraphLoad()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        SqliteFixtureMutator.DropRelationshipsTable(fx.DbPath);

        var (code, outText, errText) = Run(
            new[] { "inspect", "GetUser", "--depth", "full" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("## body", outText);
        Assert.Contains("return _repo.Find(id);", outText);
        Assert.Contains("web/Controller.cs:4", outText);
        Assert.Contains("Find", outText);
    }

    [Fact]
    public void Context_FindsRelevantBundle()
    {
        using var fx = JulieDbFixture.CreateForInspect();

        var (code, outText, errText) = Run(
            new[] { "context", "GetUser", "--token-budget", "1200", "--max-hops", "1" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("# context bundle", outText);
        Assert.Contains("GetUser", outText);
        Assert.Contains("auth/UserService.cs", outText);
    }

    [Fact]
    public void Context_UsesSymbolProjectionAndSqliteGraphWithoutFullGraphLoad()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        SqliteFixtureMutator.DropTypeArgumentsTable(fx.DbPath);

        var (code, outText, errText) = Run(
            new[] { "context", "GetUser", "--token-budget", "1200", "--max-hops", "1" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("# context bundle", outText);
        Assert.Contains("GetUser", outText);
        Assert.Contains("Find", outText);
    }

    [Fact]
    public void Impact_Symbol_RendersDependents()
    {
        using var fx = JulieDbFixture.CreateForInspect();

        var (code, outText, errText) = Run(
            new[] { "impact", "GetUser", "--max-depth", "1" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("# impacted", outText);
        Assert.Contains("Controller", outText);
        Assert.Contains("web/Controller.cs", outText);
    }

    [Fact]
    public void Impact_Target_UsesSymbolProjectionAndSqliteGraphWithoutFullGraphLoad()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        SqliteFixtureMutator.DropTypeArgumentsTable(fx.DbPath);

        var (code, outText, errText) = Run(
            new[] { "impact", "GetUser", "--max-depth", "1" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("# impacted", outText);
        Assert.Contains("Controller", outText);
    }

    [Fact]
    public void Trace_Symbol_RendersNeighbourhood()
    {
        using var fx = JulieDbFixture.CreateForInspect();

        var (code, outText, errText) = Run(
            new[] { "trace", "GetUser", "--depth", "1" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("# trace GetUser", outText);
        Assert.Contains("Find", outText);
        Assert.Contains("auth/Repo.cs", outText);
    }

    [Fact]
    public void Trace_Auto_UsesSymbolProjectionAndSqliteGraphWithoutFullGraphLoad()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        SqliteFixtureMutator.DropTypeArgumentsTable(fx.DbPath);

        var (code, outText, errText) = Run(
            new[] { "trace", "GetUser", "--depth", "1" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("# trace GetUser", outText);
        Assert.Contains("Find", outText);
        Assert.Contains("auth/Repo.cs", outText);
    }

    [Fact]
    public void Trace_Path_UsesSymbolProjectionAndSqliteGraphWithoutFullGraphLoad()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        SqliteFixtureMutator.DropTypeArgumentsTable(fx.DbPath);

        var (code, outText, errText) = Run(
            new[] { "trace", "GetUser", "--mode", "path", "--to", "Find", "--depth", "2" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("# trace path GetUser -> Find", outText);
        Assert.Contains("GetUser", outText);
        Assert.Contains("Find", outText);
    }

    [Fact]
    public void Trace_ScopeFlag_IsAccepted()
    {
        using var fx = JulieDbFixture.CreateForInspect();

        var (code, outText, errText) = Run(
            new[] { "trace", "GetUser", "--scope", "auth/UserService.cs", "--depth", "1" },
            Context(fx.DbPath, fx.WorkspaceRoot));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("# trace GetUser", outText);
        Assert.Contains("Find", outText);
    }

    [Fact]
    public void WorkspaceList_RendersSeededRegistryRows()
    {
        using (WorkspaceRegistry registry = WorkspaceRegistry.Open(_registryDb))
        {
            registry.UpsertSeen("ws-aaaaaaaaaaaa", "alpha-ws", Path.Combine(_dir, "alpha"),
                Path.Combine(_dir, "alpha", ".miller", "symbols.db"), WorkspaceRegistryState.Ready);
            registry.UpsertSeen("ws-bbbbbbbbbbbb", "beta-ws", Path.Combine(_dir, "beta"),
                Path.Combine(_dir, "beta", ".miller", "symbols.db"), WorkspaceRegistryState.Ready);
        }
        SqliteConnection.ClearAllPools();

        var (code, outText, _) = Run(new[] { "workspace", "list" }, Context(Path.Combine(_dir, "symbols.db")));
        Assert.Equal(0, code);
        Assert.Contains("alpha-ws", outText);
        Assert.Contains("beta-ws", outText);
    }

    [Fact]
    public void WorkspaceStatus_DefaultsToStatus_AndShowsBuildVersion()
    {
        using var fx = JulieDbFixture.CreateDefault();
        // No registry row matches the current root → the CLI reads the local index directly and stamps THIS
        // binary's version into the status header (the dogfooding "which build is live" signal).
        var (code, outText, _) = Run(new[] { "workspace", "status" }, Context(fx.DbPath));
        Assert.Equal(0, code);
        Assert.Contains("miller 0.1.0", outText);
        Assert.Contains("pid ", outText);
        Assert.Contains("symbols:", outText);
    }

    [Fact]
    public void WorkspaceStatus_UnknownId_IsUsageErrorExitTwo()
    {
        var (code, _, errText) = Run(new[] { "workspace", "status", "--id", "does-not-exist" },
            Context(Path.Combine(_dir, "symbols.db")));
        Assert.Equal(2, code);
        Assert.False(string.IsNullOrWhiteSpace(errText));
    }

    [Theory]
    [InlineData("help")]
    [InlineData("--help")]
    [InlineData("-h")]
    public void WorkspaceHelp_PrintsUsage_DoesNotRunStatus(string token)
    {
        // `workspace help` must show the operation list, NOT silently fall through to `status` (which would
        // open the registry and stamp a version header — surprising for a help request).
        var (code, outText, _) = Run(new[] { "workspace", token }, Context(Path.Combine(_dir, "symbols.db")));
        Assert.Equal(0, code);
        Assert.Contains("status", outText);
        Assert.Contains("list", outText);
        Assert.Contains("refresh", outText);
        Assert.Contains("open", outText);
        Assert.Contains("remove", outText);
        // A status run would print the build-version header; a help run must not.
        Assert.DoesNotContain("symbols:", outText);
    }

    // refresh/full must surface EVERY non-success refresh status as a non-zero exit (a CI `... && deploy` guard):
    // only Refreshed/Unchanged are success. This pins the status→code map directly (the live refresh path itself
    // is exercised by the Scale subprocess test, which spawns julie).
    [Theory]
    [InlineData(WorkspaceRefreshStatus.Refreshed, 0)]
    [InlineData(WorkspaceRefreshStatus.Unchanged, 0)]
    [InlineData(WorkspaceRefreshStatus.MissingRoot, 3)]
    [InlineData(WorkspaceRefreshStatus.MissingIndex, 3)]
    [InlineData(WorkspaceRefreshStatus.LockBusy, 3)]
    [InlineData(WorkspaceRefreshStatus.Failed, 3)]
    public void RefreshExitCode_NonSuccessIsAlwaysNonZero(WorkspaceRefreshStatus status, int expected) =>
        Assert.Equal(expected, CliDispatch.RefreshExitCode(status));

    // ---------- workspace open (bootstrap a fresh dir) ----------

    [Fact]
    public void WorkspaceOpen_NonexistentPath_IsUsageErrorExitTwo()
    {
        string missing = Path.Combine(_dir, "does-not-exist");
        var (code, _, errText) = Run(new[] { "workspace", "open", "--path", missing },
            Context(Path.Combine(_dir, "symbols.db")));
        Assert.Equal(2, code);
        Assert.Contains("no directory", errText);
    }

    [Fact]
    public void WorkspaceOpen_SensitiveRoot_RefusedExitTwo()
    {
        // A filesystem root ("/" — or the drive root on Windows) has no parent → always sensitive. The guard
        // fires before any registry write or julie locate.
        string root = Path.GetPathRoot(Path.GetTempPath())!;
        var (code, _, errText) = Run(new[] { "workspace", "open", "--path", root },
            Context(Path.Combine(_dir, "symbols.db")));
        Assert.Equal(2, code);
        Assert.Contains("sensitive", errText);
        Assert.Empty(ListRegistry());
    }

    [Fact]
    public void WorkspaceOpen_SymlinkToSensitiveRoot_RefusedBeforeRegister()
    {
        // R3: the guard must see the CANONICAL (symlink-resolved) root, not the lexical arg — a symlink whose
        // target is a sensitive root must be refused before UpsertSeen.
        string root = Path.GetPathRoot(Path.GetTempPath())!;
        string link = Path.Combine(_dir, "link-to-root");
        Directory.CreateSymbolicLink(link, root);

        var (code, _, errText) = Run(new[] { "workspace", "open", "--path", link },
            Context(Path.Combine(_dir, "symbols.db")));

        Assert.Equal(2, code);
        Assert.Contains("sensitive", errText);
        Assert.Empty(ListRegistry()); // refused before registration

        Directory.Delete(link); // remove the reparse point ourselves (don't rely on recursive cleanup)
    }

    [Fact]
    public void WorkspaceOpen_MissingTool_ExitsThree_AndDoesNotRegister()
    {
        // R1: julie-extract is located BEFORE registration, so a missing tool fails with no orphan row.
        // ctx.ToolsRoot defaults to <_dir>/.tools, which does not exist. Locate also searches PATH, so skip
        // (don't risk spawning julie) if the binary is resolvable there.
        Assert.SkipWhen(JulieResolvableOnPath(), "julie-extract is on PATH; cannot prove the missing-tool path here.");

        var (code, _, errText) = Run(new[] { "workspace", "open" }, Context(Path.Combine(_dir, "symbols.db")));

        Assert.Equal(3, code);
        Assert.False(string.IsNullOrWhiteSpace(errText));
        Assert.Empty(ListRegistry()); // Locate threw before UpsertSeen
    }

    // ---------- workspace remove ----------

    [Fact]
    public void WorkspaceRemove_NoSelector_IsUsageErrorExitTwo()
    {
        var (code, _, errText) = Run(new[] { "workspace", "remove" }, Context(Path.Combine(_dir, "symbols.db")));
        Assert.Equal(2, code);
        Assert.Contains("--id", errText);   // the remove-specific usage, not the generic unknown-operation note
        Assert.Contains("--path", errText);
    }

    [Fact]
    public void WorkspaceRemove_UnknownId_IsUsageErrorExitTwo()
    {
        var (code, _, errText) = Run(new[] { "workspace", "remove", "--id", "does-not-exist" },
            Context(Path.Combine(_dir, "symbols.db")));
        Assert.Equal(2, code);
        Assert.Contains("does-not-exist", errText);   // the selector is echoed (resolver KeyNotFound), not swallowed
        Assert.Contains("selector", errText);
    }

    [Fact]
    public void WorkspaceRemove_ByPath_DeletesMillerDir()
    {
        string sub = Path.Combine(_dir, "ws-bypath");
        string millerDir = Path.Combine(sub, ".miller");
        Directory.CreateDirectory(millerDir);
        File.WriteAllText(Path.Combine(millerDir, "symbols.db"), "x"); // a stand-in index file

        var (code, outText, _) = Run(new[] { "workspace", "remove", "--path", sub },
            Context(Path.Combine(_dir, "symbols.db")));

        Assert.Equal(0, code);
        Assert.Contains("removed", outText);
        Assert.False(Directory.Exists(millerDir));
    }

    [Fact]
    public void WorkspaceRemove_ByPath_NoMillerDir_NotFoundExitZero()
    {
        string sub = Path.Combine(_dir, "ws-empty");
        Directory.CreateDirectory(sub); // exists, but has no .miller

        var (code, outText, _) = Run(new[] { "workspace", "remove", "--path", sub },
            Context(Path.Combine(_dir, "symbols.db")));

        Assert.Equal(0, code);
        Assert.Contains("not found", outText);
    }

    [Fact]
    public void WorkspaceRemove_ById_DeletesAndUnregisters()
    {
        string sub = Path.Combine(_dir, "ws-byid");
        string millerDir = Path.Combine(sub, ".miller");
        Directory.CreateDirectory(millerDir);
        const string id = "ws-byid-00000000";
        using (WorkspaceRegistry registry = WorkspaceRegistry.Open(_registryDb))
            registry.UpsertSeen(id, "byid-disp", sub, Path.Combine(millerDir, "symbols.db"),
                WorkspaceRegistryState.Ready);
        SqliteConnection.ClearAllPools();

        var (code, outText, _) = Run(new[] { "workspace", "remove", "--id", "byid-disp" },
            Context(Path.Combine(_dir, "symbols.db")));

        Assert.Equal(0, code);
        Assert.Contains("removed", outText);
        Assert.False(Directory.Exists(millerDir));
        using WorkspaceRegistry check = WorkspaceRegistry.Open(_registryDb);
        Assert.Null(check.Get(id));
    }

    [Fact]
    public void WorkspaceRemove_LockHeldByAnotherWriter_RefusedExitThree()
    {
        string sub = Path.Combine(_dir, "ws-locked");
        string millerDir = Path.Combine(sub, ".miller");
        Directory.CreateDirectory(millerDir);

        using (SingleWriterLock? lease = SingleWriterLock.TryAcquire(millerDir))
        {
            Assert.NotNull(lease); // we hold the writer lock
            var (code, outText, _) = Run(new[] { "workspace", "remove", "--path", sub },
                Context(Path.Combine(_dir, "symbols.db")));

            Assert.Equal(3, code);
            Assert.Contains("in use", outText);
            Assert.True(Directory.Exists(millerDir)); // not deleted while held
        }
    }

    [Fact]
    public void WorkspaceRemove_ByPath_GoneDir_PrunesStaleRow()
    {
        // R4: the dir is already deleted but a registry row still points at it. remove --path must prune the
        // row via a lexical match (CanonicalizeRoot cannot run on a missing dir).
        string gone = Path.Combine(_dir, "ws-gone");
        string full = Path.GetFullPath(gone);
        const string id = "ws-gone-000000000";
        using (WorkspaceRegistry registry = WorkspaceRegistry.Open(_registryDb))
            registry.UpsertSeen(id, "gone-disp", full, Path.Combine(full, ".miller", "symbols.db"),
                WorkspaceRegistryState.Ready);
        SqliteConnection.ClearAllPools();

        var (code, outText, _) = Run(new[] { "workspace", "remove", "--path", gone },
            Context(Path.Combine(_dir, "symbols.db")));

        Assert.Equal(0, code);
        Assert.Contains("removed", outText);
        using WorkspaceRegistry check = WorkspaceRegistry.Open(_registryDb);
        Assert.Null(check.Get(id));
    }

    [Fact]
    public void WorkspaceRemove_ByPath_RegisteredDir_DeletesAndUnregisters()
    {
        // The realistic by-path flow: the dir EXISTS and IS registered (the normal state after `open`). Exercises
        // the registry-match arm + the post-delete unregister that the no-row by-path test does not.
        string sub = Path.Combine(_dir, "ws-reg");
        Directory.CreateDirectory(sub);
        string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(sub);
        string millerDir = Path.Combine(canonicalRoot, ".miller");
        Directory.CreateDirectory(millerDir);
        const string id = "ws-reg-00000000000";
        using (WorkspaceRegistry registry = WorkspaceRegistry.Open(_registryDb))
            registry.UpsertSeen(id, "reg-disp", canonicalRoot,
                Path.Combine(canonicalRoot, ".miller", "symbols.db"), WorkspaceRegistryState.Ready);
        SqliteConnection.ClearAllPools();

        var (code, outText, _) = Run(new[] { "workspace", "remove", "--path", canonicalRoot },
            Context(Path.Combine(_dir, "symbols.db")));

        Assert.Equal(0, code);
        Assert.Contains("removed", outText);
        Assert.False(Directory.Exists(millerDir));
        using WorkspaceRegistry check = WorkspaceRegistry.Open(_registryDb);
        Assert.Null(check.Get(id));   // the match arm resolved the row and unregistered it after the delete
    }

    [Fact]
    public void WorkspaceRemove_ById_MissingDir_PrunesOrphanRow()
    {
        // A registered row whose .miller dir was deleted out from under it: remove --id must prune the orphan row
        // and report a clean not-found (exit 0), without a dir to delete.
        string sub = Path.Combine(_dir, "ws-orphan");
        const string id = "ws-orphan-000000000";
        using (WorkspaceRegistry registry = WorkspaceRegistry.Open(_registryDb))
            registry.UpsertSeen(id, "orphan-disp", sub,
                Path.Combine(sub, ".miller", "symbols.db"), WorkspaceRegistryState.Ready);
        SqliteConnection.ClearAllPools();

        var (code, outText, _) = Run(new[] { "workspace", "remove", "--id", "orphan-disp" },
            Context(Path.Combine(_dir, "symbols.db")));

        Assert.Equal(0, code);
        Assert.Contains("not found", outText);
        using WorkspaceRegistry check = WorkspaceRegistry.Open(_registryDb);
        Assert.Null(check.Get(id));
    }

    [Fact]
    public void WorkspaceRemove_ByPath_GoneDir_CaseInsensitiveMatch_PrunesStaleRow()
    {
        // On a case-insensitive volume (macOS/Windows) a remove --path that differs ONLY in case from the
        // registered canonical root is the SAME on-disk dir and must still prune the stale row — mirroring the
        // existing-dir matcher. On case-sensitive POSIX they are genuinely different dirs, so skip.
        Assert.SkipUnless(OperatingSystem.IsMacOS() || OperatingSystem.IsWindows(),
            "case-insensitive path matching only applies on macOS/Windows volumes.");

        string registered = Path.GetFullPath(Path.Combine(_dir, "ws-Case"));   // registered with this casing
        string requested = Path.Combine(_dir, "ws-case");                      // removed with a different casing
        const string id = "ws-case-00000000000";
        using (WorkspaceRegistry registry = WorkspaceRegistry.Open(_registryDb))
            registry.UpsertSeen(id, "case-disp", registered,
                Path.Combine(registered, ".miller", "symbols.db"), WorkspaceRegistryState.Ready);
        SqliteConnection.ClearAllPools();

        var (code, outText, _) = Run(new[] { "workspace", "remove", "--path", requested },
            Context(Path.Combine(_dir, "symbols.db")));

        Assert.Equal(0, code);
        Assert.Contains("removed", outText);
        using WorkspaceRegistry check = WorkspaceRegistry.Open(_registryDb);
        Assert.Null(check.Get(id));   // matched case-insensitively and pruned (Ordinal alone would miss it)
    }

    [Fact]
    public void WorkspaceRemove_ByPath_Json_EmitsResultObject()
    {
        string sub = Path.Combine(_dir, "ws-json");
        Directory.CreateDirectory(Path.Combine(sub, ".miller"));

        var (code, outText, _) = Run(new[] { "workspace", "remove", "--path", sub, "--json" },
            Context(Path.Combine(_dir, "symbols.db")));

        Assert.Equal(0, code);
        Assert.Contains("\"result\"", outText);
        Assert.Contains("removed", outText);
    }

    [Theory]
    [InlineData(WorkspaceRemoveResult.Outcome.Removed, 0)]
    [InlineData(WorkspaceRemoveResult.Outcome.NotFound, 0)]
    [InlineData(WorkspaceRemoveResult.Outcome.RefusedInUse, 3)]
    [InlineData(WorkspaceRemoveResult.Outcome.RefusedLive, 3)]
    public void RemoveExitCode_MapsOutcomesToCodes(WorkspaceRemoveResult.Outcome outcome, int expected) =>
        Assert.Equal(expected, CliDispatch.RemoveExitCode(outcome));

    // ---------- helpers ----------

    private IReadOnlyList<WorkspaceRegistryRow> ListRegistry()
    {
        SqliteConnection.ClearAllPools();
        using WorkspaceRegistry registry = WorkspaceRegistry.Open(_registryDb);
        return registry.List();
    }

    private void SeedRegisteredWorkspace(string workspaceId, string displayId, string root, string dbPath)
    {
        using WorkspaceRegistry registry = WorkspaceRegistry.Open(_registryDb);
        registry.UpsertSeen(workspaceId, displayId, root, dbPath, WorkspaceRegistryState.Ready);
        registry.MarkScanned(workspaceId, revision: 1);
    }

    // Whether `julie-extract` is resolvable on PATH (so an empty ToolsRoot would NOT fail Locate). Used to skip
    // the missing-tool test rather than risk spawning julie in the fast suite.
    private static bool JulieResolvableOnPath()
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return false;
        string[] names = OperatingSystem.IsWindows()
            ? new[] { "julie-extract.exe", "julie-extract" }
            : new[] { "julie-extract" };
        foreach (string dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;
            foreach (string name in names)
            {
                try { if (File.Exists(Path.Combine(dir, name))) return true; }
                catch { /* an unparseable PATH entry is not a julie hit */ }
            }
        }
        return false;
    }

    private sealed class RecordingDashboardLauncher(DashboardLaunchResult result) : IDashboardLauncher
    {
        private readonly DashboardLaunchResult _result = result;
        private readonly List<DashboardLaunchRequest> _requests = new();

        public IReadOnlyList<DashboardLaunchRequest> Requests => _requests;

        public DashboardLaunchResult EnsureRunning(DashboardLaunchRequest request)
        {
            _requests.Add(request);
            return _result;
        }
    }
}
