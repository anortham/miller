using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Server;
using Miller.Server.Tools;
using Miller.Testing;
using Xunit;

namespace Miller.Tests.Server;

public sealed class TestsToolTests : IDisposable
{
    private readonly string _dir;
    private readonly string _root;
    private readonly WorkspaceContext _workspace;

    public TestsToolTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-tests-tool-" + Guid.NewGuid().ToString("N")[..10]);
        _root = Path.Combine(_dir, "workspace");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_dir, "home"));
        _workspace = new WorkspaceContext(
            WorkspaceRoot: _root,
            ExtractDbPath: Path.Combine(_root, ".miller", "symbols.db"),
            TelemetryDbPath: Path.Combine(_dir, "home", "telemetry.db"),
            RegistryDbPath: Path.Combine(_dir, "home", "workspaces.db"),
            ToolsRoot: Path.Combine(_dir, ".tools"),
            WorkspaceId: WorkspaceId.FromCanonicalRoot(_root),
            CanonicalRoot: _root);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Compact_status_renders_a_bounded_selected_line_for_a_long_index_identity()
    {
        var result = new TestsStatusResult(
            Enabled: true,
            KillSwitchOff: false,
            Projects: [],
            DaemonState: CtDaemonLifecycleState.Running,
            DaemonReason: "idle",
            Verdict: ContinuousTestVerdict.Green,
            Selected: new CtFreshnessKey(new string('a', 300), 29158),
            StaleCount: 0,
            SelectedCount: 10,
            LastRun: null,
            BudgetHolder: null);

        string compact = TestsCore.RenderStatusCompact(result);

        string line = compact.Split('\n').Single(row => row.StartsWith("selected:", StringComparison.Ordinal));
        Assert.True(line.Length <= 80, $"selected line is {line.Length} chars: {line}");
        Assert.Contains("29158", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_OnNeverEnabledWorkspace_StartsNothingAndCreatesNoState()
    {
        ProcessStartInfo? seen = null;
        TestsTool tool = CreateTool(info =>
        {
            seen = info;
            return null;
        });

        string json = tool.Tests(operation: "status", format: "json");

        Assert.Null(seen);
        using JsonDocument doc = JsonDocument.Parse(json);
        AssertStatusContractShape(doc.RootElement);
        Assert.False(doc.RootElement.GetProperty("enabled").GetBoolean());
        Assert.Equal("stopped", doc.RootElement.GetProperty("daemon").GetProperty("state").GetString());
        Assert.DoesNotContain("next:", json, StringComparison.Ordinal);
        Assert.False(File.Exists(CtSchema.DbPathFor(_root)));
        Assert.False(Directory.Exists(Path.Combine(_root, ".miller")));
        Assert.False(Directory.Exists(CtDaemonProtocol.RootDirectory(_root)));
        Assert.Null(CtDaemonLease.TryRead(_root));
    }

    [Fact]
    public void Status_DefaultOperation_IsCheapRead()
    {
        TestsTool tool = CreateTool();

        string compact = tool.Tests();

        Assert.Contains("enabled: false", compact, StringComparison.Ordinal);
        Assert.Contains("daemon: stopped", compact, StringComparison.Ordinal);
        Assert.Contains("next: tests operation=enable", compact, StringComparison.Ordinal);
        Assert.False(File.Exists(CtSchema.DbPathFor(_root)));
        Assert.Null(CtDaemonLease.TryRead(_root));
    }

    [Fact]
    public void Status_Json_MatchesTestsCoreAndOmitsNextStepHint()
    {
        TestsTool tool = CreateTool();
        var request = CoreRequest();

        string toolJson = tool.Tests(operation: "status", format: "json");
        string coreJson = TestsCore.Status(request).Render(json: true);

        Assert.Equal(coreJson, toolJson);
        Assert.DoesNotContain("next:", toolJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Start_IsTheOnlyOperationThatCallsSpawnDetached()
    {
        WriteTestProject();
        TestsTool enableTool = CreateTool();
        enableTool.Tests(operation: "enable", format: "json");

        var spawned = new List<string>();
        TestsTool tool = CreateTool(info =>
        {
            spawned.Add(string.Join(' ', info.ArgumentList));
            return null;
        });

        foreach (string operation in new[] { "status", "failures", "enable", "disable", "stop", "run" })
        {
            if (operation == "enable")
                WriteTestProject();
            _ = tool.Tests(operation: operation, format: "json");
        }

        Assert.Empty(spawned);

        enableTool.Tests(operation: "enable", format: "json");
        string startJson = tool.Tests(operation: "start", format: "json");

        Assert.Single(spawned);
        Assert.Contains(CtDaemonLauncher.DaemonVerb, spawned[0], StringComparison.Ordinal);
        using JsonDocument doc = JsonDocument.Parse(startJson);
        Assert.True(doc.RootElement.TryGetProperty("status", out JsonElement status));
        Assert.False(string.IsNullOrWhiteSpace(status.GetString()));
        Assert.DoesNotContain("next:", startJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Start_WithoutEnable_DoesNotSpawn()
    {
        ProcessStartInfo? seen = null;
        TestsTool tool = CreateTool(info =>
        {
            seen = info;
            return null;
        });

        string json = tool.Tests(operation: "start", format: "json");

        Assert.Null(seen);
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal("refused", doc.RootElement.GetProperty("status").GetString());
        Assert.Contains("enable", doc.RootElement.GetProperty("reason").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(CtDaemonProtocol.RootDirectory(_root)));
        Assert.False(File.Exists(CtSchema.DbPathFor(_root)));
    }

    [Fact]
    public void Run_WithoutDaemon_IsForegroundOneShotAndDoesNotSpawn()
    {
        WriteTestProject();
        CreateTool().Tests(operation: "enable", format: "json");

        ProcessStartInfo? seen = null;
        TestsForegroundRunRequest? foreground = null;
        var hooks = new TestsCoreHooks(
            StartProcess: info =>
            {
                seen = info;
                return null;
            },
            ForegroundRun: request =>
            {
                foreground = request;
                return new TestsRunOutcome(
                    CtRunExecution.ForegroundOneShot,
                    ContinuousTestVerdict.Unknown,
                    "stub",
                    Waited: false);
            });
        var tool = new TestsTool(_workspace, hooks);

        string json = tool.Tests(operation: "run", format: "json");

        Assert.Null(seen);
        Assert.NotNull(foreground);
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal("foreground_one_shot", doc.RootElement.GetProperty("execution").GetString());
        Assert.DoesNotContain("next:", json, StringComparison.Ordinal);
        Assert.Null(CtDaemonLease.TryRead(_root));
        Assert.False(Directory.Exists(CtDaemonProtocol.RootDirectory(_root)));
    }

    [Fact]
    public void CompactOutput_CarriesNextStepHint_JsonDoesNot()
    {
        TestsTool tool = CreateTool();

        string compact = tool.Tests(operation: "status", format: "compact");
        string json = tool.Tests(operation: "status", format: "json");

        Assert.Contains(NextStepHint.Render("tests operation=enable", "opt in to continuous testing"), compact);
        Assert.DoesNotContain("next:", json, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownOperation_IsRefusal()
    {
        TestsTool tool = CreateTool();

        string compact = tool.Tests(operation: "resume");

        Assert.Contains("status|failures|start|stop|enable|disable|run", compact, StringComparison.Ordinal);
    }

    /// <summary>
    /// The page used to be five rows with a hard ceiling of twenty and no way to ask for the rest, in compact
    /// AND in JSON. A suite with hundreds of red cases reported five of them.
    /// </summary>
    [Fact]
    public void Failures_returns_a_full_page_not_five_rows()
    {
        SeedRedCases(60);

        TestsFailuresResult result = TestsCore.Failures(CoreRequest());

        Assert.Equal(TestsCore.FailuresDefaultLimit, result.Failures.Count);
        Assert.Equal(60, result.Total);
        Assert.Equal(0, result.Offset);
        Assert.Equal(40, result.Truncated);
    }

    [Fact]
    public void Failures_pages_past_the_first_page_and_never_repeats_a_case()
    {
        SeedRedCases(60);

        TestsFailuresResult first = TestsCore.Failures(CoreRequest(), maxItems: 25);
        TestsFailuresResult second = TestsCore.Failures(CoreRequest(), maxItems: 25, offset: 25);
        TestsFailuresResult last = TestsCore.Failures(CoreRequest(), maxItems: 25, offset: 50);

        Assert.Equal(25, first.Failures.Count);
        Assert.Equal(25, second.Failures.Count);
        Assert.Equal(10, last.Failures.Count);
        Assert.Equal(0, last.Truncated);
        Assert.Equal(
            60,
            first.Failures.Concat(second.Failures).Concat(last.Failures)
                .Select(row => row.TestCaseId)
                .Distinct(StringComparer.Ordinal)
                .Count());
    }

    [Fact]
    public void Failures_clamps_a_limit_above_the_page_ceiling_and_below_one()
    {
        SeedRedCases(3);

        Assert.Equal(3, TestsCore.Failures(CoreRequest(), maxItems: 10_000).Failures.Count);
        Assert.Single(TestsCore.Failures(CoreRequest(), maxItems: 0).Failures);
        Assert.Empty(TestsCore.Failures(CoreRequest(), offset: 99).Failures);
    }

    [Fact]
    public void Failures_compact_names_the_next_offset_so_the_reader_can_ask_for_the_rest()
    {
        SeedRedCases(30);

        string compact = TestsCore.Failures(CoreRequest()).Render(json: false);

        Assert.Contains("# tests failures (20 of 30)", compact, StringComparison.Ordinal);
        Assert.Contains("truncated: 10 (next: offset=20)", compact, StringComparison.Ordinal);
    }

    [Fact]
    public void Failures_json_carries_the_total_and_the_offset()
    {
        SeedRedCases(30);

        using JsonDocument document = JsonDocument.Parse(
            TestsCore.Failures(CoreRequest(), maxItems: 5, offset: 20).Render(json: true));

        JsonElement root = document.RootElement;
        Assert.Equal(5, root.GetProperty("failures").GetArrayLength());
        Assert.Equal(30, root.GetProperty("total").GetInt32());
        Assert.Equal(20, root.GetProperty("offset").GetInt32());
        Assert.Equal(5, root.GetProperty("truncated").GetInt32());
    }

    [Fact]
    public void The_mcp_tool_passes_its_limit_and_offset_through_to_the_core()
    {
        SeedRedCases(30);
        TestsTool tool = CreateTool();

        string toolJson = tool.Tests(operation: "failures", format: "json", limit: 5, offset: 20);
        string coreJson = TestsCore.Failures(CoreRequest(), maxItems: 5, offset: 20).Render(json: true);

        Assert.Equal(coreJson, toolJson);
    }

    /// <summary>
    /// Writes <paramref name="count"/> red cases through the real run-completion path, so the states come from
    /// the same code that produces them in production rather than from a hand-written row.
    /// </summary>
    private void SeedRedCases(int count)
    {
        const string identity = "store:failures";
        string workspaceId = _workspace.WorkspaceId ?? WorkspaceId.FromCanonicalRoot(_root);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        for (int index = 0; index < count; index++)
        {
            // Zero-padded so ordinal order matches numeric order and a paging assertion reads plainly.
            string caseId = $"test:{index:D3}";
            string runId = $"run:{index:D3}";
            store.PutTestCase(new ContinuousTestCase(
                Id: caseId,
                WorkspaceId: workspaceId,
                Name: caseId,
                QualifiedName: caseId,
                Selector: caseId,
                FilePath: "tests/Suite.cs",
                Framework: "xunit"));
            store.StartContinuousTestRun(
                new ContinuousTestRun(
                    Id: runId,
                    WorkspaceId: workspaceId,
                    Status: "running",
                    SelectedRevision: "1",
                    IndexIdentity: identity,
                    Revision: 1),
                [caseId]);
            store.CompleteContinuousTestRun(new ContinuousTestRunCompletion(
                WorkspaceId: workspaceId,
                TestRunId: runId,
                SelectedRevision: "1",
                CurrentRevision: "1",
                IndexIdentity: identity,
                Revision: 1,
                Status: "failed",
                Results:
                [
                    new ContinuousTestResult(
                        Id: runId + ":" + caseId,
                        WorkspaceId: workspaceId,
                        TestCaseId: caseId,
                        TestRunId: runId,
                        Status: "failed",
                        ResultRevision: "1",
                        IndexIdentity: identity,
                        Revision: 1,
                        FailureSummary: "boom " + caseId),
                ]));
        }
    }

    private TestsTool CreateTool(Func<ProcessStartInfo, Process?>? startProcess = null)
    {
        var hooks = new TestsCoreHooks(
            StartProcess: startProcess,
            ForegroundRun: request => new TestsRunOutcome(
                CtRunExecution.ForegroundOneShot,
                ContinuousTestVerdict.Unknown,
                "stub",
                request.Wait));
        return new TestsTool(_workspace, hooks);
    }

    private TestsCoreRequest CoreRequest(TestsCoreHooks? hooks = null) =>
        new(
            WorkspaceRoot: _root,
            WorkspaceId: _workspace.WorkspaceId,
            MillerHome: Path.GetDirectoryName(_workspace.RegistryDbPath),
            Hooks: hooks);

    private string WriteTestProject(string relativePath = "tests/Sample.Tests/Sample.Tests.csproj")
    {
        string path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="xunit" Version="2.9.2" />
              </ItemGroup>
            </Project>
            """);
        return path;
    }

    private static void AssertStatusContractShape(JsonElement root)
    {
        Assert.True(root.TryGetProperty("enabled", out _));
        Assert.True(root.TryGetProperty("projects", out JsonElement projects));
        Assert.Equal(JsonValueKind.Array, projects.ValueKind);
        Assert.True(root.TryGetProperty("daemon", out JsonElement daemon));
        Assert.True(daemon.TryGetProperty("state", out _));
        Assert.True(daemon.TryGetProperty("reason", out _));
        Assert.True(daemon.TryGetProperty("running", out _));
        Assert.True(daemon.TryGetProperty("paused", out _));
        Assert.True(root.TryGetProperty("verdict", out _));
        Assert.True(root.TryGetProperty("selected", out _));
        Assert.True(root.TryGetProperty("stale_count", out _));
        Assert.True(root.TryGetProperty("last_run", out _));
        Assert.True(root.TryGetProperty("budget_holder", out _));
    }
}
