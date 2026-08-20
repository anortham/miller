using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Server;
using Miller.Server.Cli;
using Miller.Server.Git;
using Miller.Server.Tools;
using Miller.Testing;
using Xunit;

namespace Miller.Tests.Server.Cli;

public sealed class TestsCliTests : IDisposable
{
    private readonly string _dir;
    private readonly string _root;
    private readonly string _registryDb;

    public TestsCliTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-tests-cli-" + Guid.NewGuid().ToString("N")[..10]);
        _root = Path.Combine(_dir, "workspace");
        Directory.CreateDirectory(_root);
        _registryDb = Path.Combine(_dir, "home", "workspaces.db");
        Directory.CreateDirectory(Path.GetDirectoryName(_registryDb)!);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Status_OnNeverEnabledWorkspace_IsCheapHonestReadAndCreatesNothing()
    {
        var (code, outText, errText) = Run("tests", "status", "--json");

        Assert.Equal(0, code);
        Assert.Empty(errText);
        using JsonDocument doc = JsonDocument.Parse(outText);
        JsonElement root = doc.RootElement;
        AssertStatusContractShape(root);
        Assert.False(root.GetProperty("enabled").GetBoolean());
        Assert.Equal("unknown", root.GetProperty("verdict").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("selected").ValueKind);
        Assert.Equal(0, root.GetProperty("stale_count").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("last_run").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("budget_holder").ValueKind);
        Assert.Empty(root.GetProperty("projects").EnumerateArray());
        JsonElement daemon = root.GetProperty("daemon");
        Assert.Equal("stopped", daemon.GetProperty("state").GetString());
        Assert.False(daemon.GetProperty("running").GetBoolean());
        Assert.False(daemon.GetProperty("paused").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(daemon.GetProperty("reason").GetString()));

        Assert.False(File.Exists(CtSchema.DbPathFor(_root)));
        Assert.False(Directory.Exists(Path.Combine(_root, ".miller")));
        Assert.False(Directory.Exists(CtDaemonProtocol.RootDirectory(_root)));
        Assert.Null(CtDaemonLease.TryRead(_root));
    }

    [Fact]
    public void Status_OnStoppedDaemon_StartsNothing()
    {
        WriteTestProject();
        Assert.Equal(0, Run("tests", "enable").Code);

        var (code, outText, errText) = Run("tests", "status", "--json");

        Assert.Equal(0, code);
        Assert.Empty(errText);
        using JsonDocument doc = JsonDocument.Parse(outText);
        JsonElement root = doc.RootElement;
        Assert.True(root.GetProperty("enabled").GetBoolean());
        Assert.Equal("stopped", root.GetProperty("daemon").GetProperty("state").GetString());
        Assert.False(root.GetProperty("daemon").GetProperty("running").GetBoolean());
        Assert.Null(CtDaemonLease.TryRead(_root));
        Assert.False(Directory.Exists(CtDaemonProtocol.RootDirectory(_root)));
    }

    [Fact]
    public void Capabilities_Json_AdvertisesTestsSurface()
    {
        var (code, outText, errText) = Run("capabilities", "--json");

        Assert.Equal(0, code);
        Assert.Empty(errText);
        using JsonDocument doc = JsonDocument.Parse(outText);
        JsonElement root = doc.RootElement;
        string[] commands = root.GetProperty("json_commands")
            .EnumerateArray()
            .Select(static item => item.GetString()!)
            .ToArray();
        Assert.Contains("tests status --json", commands);
        Assert.Contains("tests run --json", commands);

        JsonElement contract = Assert.Single(
            root.GetProperty("json_contracts").EnumerateArray(),
            item => item.GetProperty("name").GetString() == "tests_status");
        Assert.Equal("tests status --json", contract.GetProperty("command").GetString());
        Assert.Equal(1, contract.GetProperty("schema_version").GetInt32());
        Assert.Equal("docs/contracts/tests-cli-v1.md", contract.GetProperty("doc").GetString());
    }

    [Fact]
    public void Enable_DiscoversProjectsAndPersistsRows()
    {
        string project = WriteTestProject();

        var (code, outText, errText) = Run("tests", "enable", "--json");

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.True(File.Exists(ContinuousTestPolicy.EnabledMarkerPath(_root)));
        Assert.True(File.Exists(CtSchema.DbPathFor(_root)));
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        ContinuousTestProject row = Assert.Single(store.ListContinuousTestProjects(WorkspaceId.FromCanonicalRoot(_root)));
        Assert.Equal(Path.GetFullPath(project), row.ProjectPath);
        Assert.True(row.Enabled);
        Assert.Equal("xunit", row.Framework);

        using JsonDocument doc = JsonDocument.Parse(outText);
        Assert.Equal(1, doc.RootElement.GetProperty("enabled_count").GetInt32());
        Assert.Equal(Path.GetFullPath(project), Assert.Single(doc.RootElement.GetProperty("projects").EnumerateArray()).GetProperty("project_path").GetString());

        var status = Run("tests", "status", "--json");
        using JsonDocument statusDoc = JsonDocument.Parse(status.Out);
        JsonElement statusProject = Assert.Single(statusDoc.RootElement.GetProperty("projects").EnumerateArray());
        Assert.True(statusProject.GetProperty("enabled").GetBoolean());
        Assert.Equal(Path.GetFullPath(project), statusProject.GetProperty("project_path").GetString());
    }

    [Fact]
    public void Enable_ProjectFlag_ScopesOneProject()
    {
        string first = WriteTestProject("tests/One.Tests/One.Tests.csproj");
        string second = WriteTestProject("tests/Two.Tests/Two.Tests.csproj", frameworkHint: "nunit");

        var (code, _, errText) = Run("tests", "enable", "--project", first, "--json");

        Assert.Equal(0, code);
        Assert.Empty(errText);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        ContinuousTestProject row = Assert.Single(store.ListContinuousTestProjects(WorkspaceId.FromCanonicalRoot(_root)));
        Assert.Equal(Path.GetFullPath(first), row.ProjectPath);
        Assert.NotEqual(Path.GetFullPath(second), row.ProjectPath);
    }

    [Fact]
    public void Disable_PersistsAndStatusReflectsIt()
    {
        string project = WriteTestProject();
        Assert.Equal(0, Run("tests", "enable").Code);

        var (code, _, errText) = Run("tests", "disable", "--project", project, "--json");

        Assert.Equal(0, code);
        Assert.Empty(errText);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        ContinuousTestProject row = Assert.Single(
            store.ListContinuousTestProjects(WorkspaceId.FromCanonicalRoot(_root), includeDisabled: true));
        Assert.False(row.Enabled);

        var status = Run("tests", "status", "--json");
        using JsonDocument doc = JsonDocument.Parse(status.Out);
        Assert.Empty(doc.RootElement.GetProperty("projects").EnumerateArray());
        Assert.False(doc.RootElement.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public void Run_WithoutDaemon_IsForegroundOneShot()
    {
        WriteTestProject();
        Assert.Equal(0, Run("tests", "enable").Code);

        TestsForegroundRunRequest? seen = null;
        var hooks = new TestsCoreHooks(
            ForegroundRun: request =>
            {
                seen = request;
                return new TestsRunOutcome(
                    CtRunExecution.ForegroundOneShot,
                    ContinuousTestVerdict.Unknown,
                    "stub",
                    Waited: false);
            });

        var (code, outText, errText) = Run(hooks, "tests", "run", "--json");

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.NotNull(seen);
        Assert.Equal(Path.GetFullPath(_root), seen!.WorkspaceRoot);
        using JsonDocument doc = JsonDocument.Parse(outText);
        Assert.Equal("foreground_one_shot", doc.RootElement.GetProperty("execution").GetString());
        Assert.Equal("unknown", doc.RootElement.GetProperty("verdict").GetString());
        Assert.Null(CtDaemonLease.TryRead(_root));
        Assert.False(Directory.Exists(CtDaemonProtocol.RootDirectory(_root)));
    }

    [Fact]
    public void Serve_WithoutEnable_DoesNotSpawn()
    {
        ProcessStartInfo? seen = null;
        var hooks = new TestsCoreHooks(
            StartProcess: info =>
            {
                seen = info;
                return null;
            });

        var (code, _, errText) = Run(hooks, "tests", "serve", "--json");

        Assert.Equal(3, code);
        Assert.Contains("enable", errText, StringComparison.OrdinalIgnoreCase);
        Assert.Null(seen);
        Assert.False(Directory.Exists(CtDaemonProtocol.RootDirectory(_root)));
        Assert.False(File.Exists(CtSchema.DbPathFor(_root)));
    }

    [Fact]
    public void Serve_WhenEnabled_SpawnsDetachedDaemon()
    {
        WriteTestProject();
        Assert.Equal(0, Run("tests", "enable").Code);

        ProcessStartInfo? seen = null;
        var hooks = new TestsCoreHooks(
            StartProcess: info =>
            {
                seen = info;
                return null;
            });

        var (code, outText, _) = Run(hooks, "tests", "serve", "--json");

        Assert.NotEqual(2, code);
        Assert.NotNull(seen);
        Assert.Contains(CtDaemonLauncher.DaemonVerb, seen!.ArgumentList);
        using JsonDocument doc = JsonDocument.Parse(outText);
        Assert.True(doc.RootElement.TryGetProperty("status", out JsonElement status));
        Assert.False(string.IsNullOrWhiteSpace(status.GetString()));
    }

    [Fact]
    public void Stop_WithNoDaemon_IsAlreadyStopped()
    {
        var (code, outText, errText) = Run("tests", "stop", "--json");

        Assert.Equal(0, code);
        Assert.Empty(errText);
        using JsonDocument doc = JsonDocument.Parse(outText);
        Assert.Equal("already_stopped", doc.RootElement.GetProperty("status").GetString());
        Assert.False(Directory.Exists(Path.Combine(_root, ".miller")));
    }

    [Fact]
    public void UnknownOperation_IsUsageError()
    {
        var (code, _, errText) = Run("tests", "resume");

        Assert.Equal(2, code);
        Assert.Contains("usage:", errText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ServeHost_reopens_live_facts_per_selector_read()
    {
        string source = File.ReadAllText(Path.Combine(
            ScaleTestSupport.RepoRoot(),
            "src",
            "Miller.Server",
            "Tools",
            "TestsCore.cs"));
        int serveHost = source.IndexOf("public static TestsServeResult ServeHost", StringComparison.Ordinal);
        int nextMethod = source.IndexOf("public static TestsStopResult Stop", StringComparison.Ordinal);
        Assert.True(serveHost >= 0 && nextMethod > serveHost);
        string body = source[serveHost..nextMethod];
        Assert.Contains("new ReopeningMillerFactSource(() => OpenLiveFacts", body, StringComparison.Ordinal);
        Assert.DoesNotContain("using IOwnedFactSource facts = OpenLiveFacts", body, StringComparison.Ordinal);
    }

    [Fact]
    public void CtDaemonVerb_WhenNotEnabled_ReturnsWithoutCreatingState()
    {
        var (code, outText, errText) = Run("ct-daemon");

        Assert.Equal(0, code);
        Assert.Empty(errText);
        using JsonDocument doc = JsonDocument.Parse(outText);
        Assert.Equal("disabled", doc.RootElement.GetProperty("reason").GetString());
        Assert.False(File.Exists(CtSchema.DbPathFor(_root)));
        Assert.False(Directory.Exists(CtDaemonProtocol.RootDirectory(_root)));
    }

    private WorkspaceContext Context() =>
        new(
            WorkspaceRoot: _root,
            ExtractDbPath: Path.Combine(_root, ".miller", "symbols.db"),
            TelemetryDbPath: Path.Combine(_dir, "home", "telemetry.db"),
            RegistryDbPath: _registryDb,
            ToolsRoot: Path.Combine(_dir, ".tools"),
            WorkspaceId: WorkspaceId.FromCanonicalRoot(_root),
            CanonicalRoot: _root);

    /// <summary>
    /// <c>failures</c> was reachable only from the MCP tool. An operator who could see "verdict: red" in
    /// <c>miller tests status</c> had no CLI verb that would name the red cases.
    /// </summary>
    [Fact]
    public void Failures_is_a_cli_verb_and_pages_with_limit_and_offset()
    {
        SeedRedCases(30);

        var (code, outText, errText) = Run("tests", "failures", "--limit", "5", "--offset", "20", "--json");

        Assert.Equal(0, code);
        Assert.Empty(errText);
        using JsonDocument document = JsonDocument.Parse(outText);
        JsonElement root = document.RootElement;
        Assert.Equal(5, root.GetProperty("failures").GetArrayLength());
        Assert.Equal(30, root.GetProperty("total").GetInt32());
        Assert.Equal(20, root.GetProperty("offset").GetInt32());
        Assert.Equal(5, root.GetProperty("truncated").GetInt32());
    }

    [Fact]
    public void Failures_compact_output_names_the_next_offset()
    {
        SeedRedCases(30);

        var (code, outText, _) = Run("tests", "failures");

        Assert.Equal(0, code);
        Assert.Contains("# tests failures (20 of 30)", outText, StringComparison.Ordinal);
        Assert.Contains("truncated: 10 (next: offset=20)", outText, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_tests_operation_reports_the_verb_list_and_exits_non_zero()
    {
        var (code, _, errText) = Run("tests", "bogus");

        Assert.Equal(2, code);
        Assert.Contains("unknown tests operation 'bogus'", errText, StringComparison.Ordinal);
        Assert.Contains("failures", errText, StringComparison.Ordinal);
    }

    private void SeedRedCases(int count)
    {
        const string identity = "store:failures";
        string workspaceId = WorkspaceId.FromCanonicalRoot(_root);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        for (int index = 0; index < count; index++)
        {
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

    private (int Code, string Out, string Err) Run(params string[] args) => Run(hooks: null, args);

    private (int Code, string Out, string Err) Run(TestsCoreHooks? hooks, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = hooks is null
            ? CliDispatch.Run(args, Context(), stdout, stderr)
            : CliDispatch.Run(args, Context(), stdout, stderr, new DashboardCliLauncher(), new ProcessGitDiffReader(), hooks);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private string WriteTestProject(string relativePath = "tests/Sample.Tests/Sample.Tests.csproj", string frameworkHint = "xunit")
    {
        string path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string package = frameworkHint switch
        {
            "nunit" => "NUnit",
            "mstest" => "MSTest.TestFramework",
            _ => "xunit",
        };
        File.WriteAllText(path, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="{package}" Version="2.9.2" />
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
