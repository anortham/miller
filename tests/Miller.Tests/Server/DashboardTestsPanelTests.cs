using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Miller.Dashboard;
using Miller.Dashboard.Components;
using Miller.Indexing;
using Miller.Server.Tools;
using Miller.Testing;
using Xunit;

namespace Miller.Tests.Server;

public sealed class DashboardTestsPanelTests : IDisposable
{
    private readonly string _dir;
    private readonly string _root;
    private readonly string _registryDb;
    private readonly string _workspaceId;

    public DashboardTestsPanelTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-dash-tests-" + Guid.NewGuid().ToString("N")[..10]);
        _root = Path.Combine(_dir, "workspace");
        Directory.CreateDirectory(_root);
        _registryDb = Path.Combine(_dir, "home", "workspaces.db");
        Directory.CreateDirectory(Path.GetDirectoryName(_registryDb)!);
        _workspaceId = WorkspaceId.FromCanonicalRoot(_root);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void From_CarriesTheStatusCoreFactsWithoutReinterpretingThem()
    {
        TestsStatusResult status = Status(
            enabled: true,
            verdict: ContinuousTestVerdict.Red,
            selected: new CtFreshnessKey("ctgen1:artifact:abc:blake3", 42),
            staleCount: 3,
            selectedCount: 17,
            lastRun: "2026-08-25T10:00:00.000Z",
            projects: [new TestsStatusProject("p1", "tests/A.csproj", "xunit", "dotnet test", true, [])],
            daemonState: CtDaemonLifecycleState.Running,
            daemonReason: "leader",
            daemonActivity: CtDaemonActivity.Executing);

        DashboardTestsPanel panel = DashboardTestsPanel.From("ws-a", status, Failures("test:one", "Xunit.EqualException: 1 != 2"));

        Assert.Equal("ready", panel.State);
        Assert.True(panel.Enabled);
        Assert.Equal("red", panel.Verdict);
        Assert.Equal("rev 42 (ctgen1:artifact:abc:blak…)", panel.Selected);
        Assert.Equal(3, panel.StaleCount);
        Assert.Equal(17, panel.TrackedCaseCount);
        Assert.Equal("2026-08-25T10:00:00.000Z", panel.LastRun);
        Assert.Equal("running", panel.DaemonState);
        Assert.Equal("leader", panel.DaemonReason);
        Assert.Equal("executing", panel.DaemonActivity);
        DashboardTestsProject project = Assert.Single(panel.Projects);
        Assert.Equal("tests/A.csproj", project.ProjectPath);
        Assert.Equal("xunit", project.Framework);
        Assert.Null(project.UnsupportedReason);
        DashboardTestsFailure failure = Assert.Single(panel.Failures);
        Assert.Equal("test:one", failure.TestCaseId);
        Assert.Equal("Xunit.EqualException: 1 != 2", failure.Summary);
        Assert.True(panel.Watching);
    }

    [Fact]
    public void From_KeepsTheNeverDecidedDiscoveryFlagAndStopsThePolling()
    {
        TestsStatusResult status = Status(
            enabled: false,
            projectsDiscovered: true,
            projects: [new TestsStatusProject("p1", "tests/A.csproj", "xunit", null, true, [])]);

        DashboardTestsPanel panel = DashboardTestsPanel.From("ws-a", status, Failures());

        Assert.False(panel.Enabled);
        Assert.True(panel.ProjectsDiscovered);
        Assert.False(panel.Watching);
    }

    [Fact]
    public void From_ReportsTheUnsupportedReasonOfAnXunitV2Project()
    {
        TestsStatusResult status = Status(
            enabled: false,
            projectsDiscovered: true,
            projects:
            [
                new TestsStatusProject(
                    "p1",
                    "tests/Legacy.csproj",
                    "xunit-v2",
                    null,
                    true,
                    [],
                    UnsupportedReason: "xUnit v2 builds no self-executing test assembly"),
            ]);

        DashboardTestsPanel panel = DashboardTestsPanel.From("ws-a", status, Failures());

        DashboardTestsProject project = Assert.Single(panel.Projects);
        Assert.Equal("xunit-v2", project.Framework);
        Assert.Equal("xUnit v2 builds no self-executing test assembly", project.UnsupportedReason);
    }

    [Fact]
    public void From_ReportsADaemonBuildMismatchAndAWedgedLoop()
    {
        TestsStatusResult status = Status(enabled: true) with
        {
            DaemonVersion = new CtDaemonVersionVerdict(
                CtDaemonVersionMatch.DaemonOlder,
                "1.21.0",
                "1.22.1",
                Mismatch: true,
                MayReplace: true,
                Reason: "daemon runs 1.21.0, this build is 1.22.1"),
            DaemonLoop = new CtLoopHealthVerdict(CtLoopHealth.LoopStalled, 400, "loop last moved 400s ago"),
        };

        DashboardTestsPanel panel = DashboardTestsPanel.From("ws-a", status, Failures());

        Assert.True(panel.DaemonVersionMismatch);
        Assert.Equal("daemon runs 1.21.0, this build is 1.22.1", panel.DaemonVersionReason);
        Assert.True(panel.DaemonLoopStalled);
        Assert.Equal("loop last moved 400s ago", panel.DaemonLoopReason);
    }

    [Fact]
    public void From_AMatchingDaemonBuildAndHealthyLoopReportNothing()
    {
        TestsStatusResult status = Status(enabled: true) with
        {
            DaemonVersion = new CtDaemonVersionVerdict(
                CtDaemonVersionMatch.Same,
                "1.22.1",
                "1.22.1",
                Mismatch: false,
                MayReplace: false,
                Reason: "same build"),
            DaemonLoop = new CtLoopHealthVerdict(CtLoopHealth.Healthy, 1, "loop is turning"),
        };

        DashboardTestsPanel panel = DashboardTestsPanel.From("ws-a", status, Failures());

        Assert.False(panel.DaemonVersionMismatch);
        Assert.Null(panel.DaemonVersionReason);
        Assert.False(panel.DaemonLoopStalled);
        Assert.Null(panel.DaemonLoopReason);
    }

    [Fact]
    public void From_UnderTheKillSwitchNeverWatches()
    {
        TestsStatusResult status = Status(enabled: false, killSwitchOff: true);

        DashboardTestsPanel panel = DashboardTestsPanel.From("ws-a", status, failures: null);

        Assert.True(panel.KillSwitchOff);
        Assert.False(panel.Watching);
        Assert.Empty(panel.Failures);
    }

    [Fact]
    public void From_NoLiveIndexLeavesTheSelectedKeyNull()
    {
        DashboardTestsPanel panel = DashboardTestsPanel.From("ws-a", Status(enabled: true), Failures());

        Assert.Null(panel.Selected);
        Assert.Equal("unknown", panel.Verdict);
    }

    [Fact]
    public void From_CountsTheFailuresItDoesNotList()
    {
        TestsFailuresResult failures = new(
            [Red("test:one", "boom")],
            Truncated: 12,
            Total: 13,
            Offset: 0);

        DashboardTestsPanel panel = DashboardTestsPanel.From("ws-a", Status(enabled: true), failures);

        Assert.Equal(12, panel.FailuresTruncated);
    }

    [Fact]
    public async Task Panel_WithNoWorkspaceRendersTheSelectionPromptAndNoError()
    {
        string html = await RenderAsync(null);

        Assert.Contains("Select a workspace to view tests.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("error-notice", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Panel_UnavailableRendersTheReasonInsteadOfFailing()
    {
        string html = await RenderAsync(DashboardTestsPanel.Unavailable("ws-a", "ct.db is present but could not be read"));

        Assert.Contains("Tests unavailable", html, StringComparison.Ordinal);
        Assert.Contains("ct.db is present but could not be read", html, StringComparison.Ordinal);
        Assert.DoesNotContain("hx-get", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Panel_NeverDecidedWorkspaceNamesTheDiscoveredProjectsAndDoesNotPoll()
    {
        DashboardTestsPanel panel = DashboardTestsPanel.From(
            "ws-a",
            Status(
                enabled: false,
                projectsDiscovered: true,
                projects:
                [
                    new TestsStatusProject("p1", "tests/A.csproj", "xunit", null, true, []),
                    new TestsStatusProject("p2", "tests/B.csproj", "xunit", null, true, []),
                ]),
            Failures());

        string html = await RenderAsync(panel);

        Assert.Contains("Continuous testing is not enabled here", html, StringComparison.Ordinal);
        Assert.Contains("2 test projects discovered", html, StringComparison.Ordinal);
        Assert.Contains("discovered, not tracked", html, StringComparison.Ordinal);
        Assert.DoesNotContain("hx-get", html, StringComparison.Ordinal);
        Assert.DoesNotContain("live-dot", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Panel_KillSwitchRendersTheOffStateOnly()
    {
        string html = await RenderAsync(DashboardTestsPanel.From("ws-a", Status(enabled: false, killSwitchOff: true), null));

        Assert.Contains("MILLER_CT=off", html, StringComparison.Ordinal);
        Assert.DoesNotContain("hx-get", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Panel_EnabledWorkspacePollsTheTestsFragment()
    {
        DashboardTestsPanel panel = DashboardTestsPanel.From("ws-a", Status(enabled: true), Failures());

        string html = await RenderAsync(panel);

        Assert.Contains("hx-get=\"/fragments/tests?workspace_id=ws-a\"", html, StringComparison.Ordinal);
        Assert.Contains("data-poll-trigger=\"every 5s\"", html, StringComparison.Ordinal);
        Assert.Contains("hx-swap=\"morph:outerHTML\"", html, StringComparison.Ordinal);
        Assert.Contains("live-dot", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ContinuousTestVerdict.Green, "ok")]
    [InlineData(ContinuousTestVerdict.Red, "bad")]
    [InlineData(ContinuousTestVerdict.Partial, "warn")]
    [InlineData(ContinuousTestVerdict.Unknown, "neutral")]
    public async Task Panel_ChipToneFollowsTheVerdict(ContinuousTestVerdict verdict, string tone)
    {
        DashboardTestsPanel panel = DashboardTestsPanel.From("ws-a", Status(enabled: true, verdict: verdict), Failures());

        string html = await RenderAsync(panel);

        Assert.Contains($"class=\"workspace-state {tone}\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Panel_RendersFailuresInTheOneLineShape()
    {
        DashboardTestsPanel panel = DashboardTestsPanel.From(
            "ws-a",
            Status(enabled: true, verdict: ContinuousTestVerdict.Red),
            new TestsFailuresResult(
                [Red("Miller.Tests.FooTests.Bar", "Xunit.EqualException: expected 1, actual 2")],
                Truncated: 4,
                Total: 5,
                Offset: 0));

        string html = await RenderAsync(panel);

        Assert.Contains("Miller.Tests.FooTests.Bar", html, StringComparison.Ordinal);
        Assert.Contains("Xunit.EqualException: expected 1, actual 2", html, StringComparison.Ordinal);
        Assert.Contains("4 more failures not shown", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Panel_RendersTheRunInFlight()
    {
        TestsStatusResult status = Status(enabled: true, daemonActivity: CtDaemonActivity.Executing) with
        {
            DaemonRun = new CtDaemonRunProgress(
                "tests/A.csproj",
                "run-1",
                SelectedCaseCount: 7,
                RunStartedAtUtc: DateTimeOffset.Parse("2026-08-25T10:00:00Z"),
                Activity: CtRunActivity.Active),
        };

        string html = await RenderAsync(DashboardTestsPanel.From("ws-a", status, Failures()));

        Assert.Contains("tests/A.csproj", html, StringComparison.Ordinal);
        Assert.Contains("7 cases", html, StringComparison.Ordinal);
        Assert.Contains("child active", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadTests_UnregisteredWorkspaceIdReturnsNoPanel()
    {
        Register();

        Assert.Null(DashboardData.ReadTests(_registryDb, "not-a-workspace"));
        Assert.Null(DashboardData.ReadTests(_registryDb, null));
    }

    [Fact]
    public void ReadTests_OnANeverDecidedWorkspaceDiscoversProjectsAndCreatesNothing()
    {
        Register();
        WriteTestProject();

        DashboardTestsPanel? panel = DashboardData.ReadTests(_registryDb, _workspaceId);

        Assert.NotNull(panel);
        Assert.Equal("ready", panel.State);
        Assert.False(panel.Enabled);
        Assert.True(panel.ProjectsDiscovered);
        Assert.EndsWith(
            Path.Combine("tests", "Sample.Tests", "Sample.Tests.csproj"),
            Assert.Single(panel.Projects).ProjectPath,
            StringComparison.Ordinal);
        Assert.False(File.Exists(CtSchema.DbPathFor(_root)));
        Assert.False(Directory.Exists(Path.Combine(_root, ".miller")));
        Assert.False(Directory.Exists(CtDaemonProtocol.RootDirectory(_root)));
    }

    [Fact]
    public void ReadTests_ReadsRecordedRedCasesFromTheCtSidecar()
    {
        Register();
        SeedRedCase("Miller.Tests.FooTests.Bar", "Xunit.EqualException: expected 1, actual 2");

        DashboardTestsPanel? panel = DashboardData.ReadTests(_registryDb, _workspaceId);

        Assert.NotNull(panel);
        Assert.Equal("ready", panel.State);
        DashboardTestsFailure failure = Assert.Single(panel.Failures);
        Assert.Equal("Miller.Tests.FooTests.Bar", failure.TestCaseId);
        Assert.Equal("Xunit.EqualException: expected 1, actual 2", failure.Summary);
    }

    [Fact]
    public void ReadTests_AWorkspaceWithNoCtFactsAtAllStillRenders()
    {
        Register();

        DashboardTestsPanel? panel = DashboardData.ReadTests(_registryDb, _workspaceId);

        Assert.NotNull(panel);
        Assert.Equal("ready", panel.State);
        Assert.Null(panel.Error);
        Assert.Empty(panel.Projects);
        Assert.Empty(panel.Failures);
        Assert.Equal("unknown", panel.Verdict);
    }

    [Fact]
    public async Task FragmentTests_ServesThePanelForARegisteredWorkspace()
    {
        Register();
        WriteTestProject();
        using IHost host = await StartHostAsync();

        HttpResponseMessage response = await host.GetTestClient().GetAsync(
            "/fragments/tests?workspace_id=" + _workspaceId,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("id=\"workspace-tests-panel\"", body, StringComparison.Ordinal);
        Assert.Contains("Continuous testing is not enabled here", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FragmentTests_AnUnknownWorkspaceIdRendersTheEmptyStateNotAFault()
    {
        Register();
        using IHost host = await StartHostAsync();

        HttpResponseMessage response = await host.GetTestClient().GetAsync(
            "/fragments/tests?workspace_id=not-a-workspace",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Select a workspace to view tests.", body, StringComparison.Ordinal);
    }

    private async Task<IHost> StartHostAsync()
    {
        var paths = new DashboardPaths(
            _registryDb,
            Path.Combine(_dir, "home", "telemetry.db"),
            Path.Combine(_dir, ".tools"),
            Path.Combine(_dir, "wwwroot"),
            "http://127.0.0.1:0");
        IHost host = new HostBuilder()
            .ConfigureWebHost(webBuilder => webBuilder
                .UseTestServer()
                .ConfigureServices(DashboardHostPipeline.ConfigureServices)
                .Configure(app => DashboardHostPipeline.Configure(app, paths, _dir)))
            .Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        return host;
    }

    private void Register()
    {
        using var registry = WorkspaceRegistry.Open(_registryDb);
        registry.UpsertSeen(
            _workspaceId,
            "dash-tests-1234",
            _root,
            Path.Combine(_root, ".miller", "symbols.db"),
            WorkspaceRegistryState.Ready,
            DateTimeOffset.Parse("2026-08-25T09:00:00Z"));
    }

    private void WriteTestProject()
    {
        string path = Path.Combine(_root, "tests", "Sample.Tests", "Sample.Tests.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="xunit.v3" Version="2.0.0" />
              </ItemGroup>
            </Project>
            """);
    }

    private void SeedRedCase(string caseId, string summary)
    {
        const string identity = "ctgen1:artifact:seed:blake3";
        const string runId = "run:seed";
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        store.PutTestCase(new ContinuousTestCase(
            Id: caseId,
            WorkspaceId: _workspaceId,
            Name: caseId,
            QualifiedName: caseId,
            Selector: caseId,
            FilePath: "tests/Suite.cs",
            Framework: "xunit"));
        store.StartContinuousTestRun(
            new ContinuousTestRun(
                Id: runId,
                WorkspaceId: _workspaceId,
                Status: "running",
                SelectedRevision: "1",
                IndexIdentity: identity,
                Revision: 1),
            [caseId]);
        store.CompleteContinuousTestRun(new ContinuousTestRunCompletion(
            WorkspaceId: _workspaceId,
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
                    WorkspaceId: _workspaceId,
                    TestCaseId: caseId,
                    TestRunId: runId,
                    Status: "failed",
                    ResultRevision: "1",
                    IndexIdentity: identity,
                    Revision: 1,
                    FailureSummary: summary),
            ]));
    }

    private static TestsStatusResult Status(
        bool enabled,
        bool killSwitchOff = false,
        bool projectsDiscovered = false,
        IReadOnlyList<TestsStatusProject>? projects = null,
        ContinuousTestVerdict verdict = ContinuousTestVerdict.Unknown,
        CtFreshnessKey? selected = null,
        int staleCount = 0,
        int selectedCount = 0,
        string? lastRun = null,
        CtDaemonLifecycleState daemonState = CtDaemonLifecycleState.Stopped,
        string daemonReason = "not running",
        CtDaemonActivity daemonActivity = CtDaemonActivity.Idle) =>
        new(
            enabled,
            killSwitchOff,
            projects ?? [],
            daemonState,
            daemonReason,
            verdict,
            selected,
            staleCount,
            selectedCount,
            lastRun,
            BudgetHolder: null,
            DaemonActivity: daemonActivity,
            ProjectsDiscovered: projectsDiscovered);

    private static TestsFailuresResult Failures(string? caseId = null, string? summary = null) =>
        caseId is null
            ? new TestsFailuresResult([], Truncated: 0)
            : new TestsFailuresResult([Red(caseId, summary ?? "boom")], Truncated: 0, Total: 1, Offset: 0);

    private static ContinuousTestStatus Red(string caseId, string summary) =>
        new(
            WorkspaceId: "ws-a",
            TestCaseId: caseId,
            State: ContinuousTestState.Red,
            IndexIdentity: "ctgen1:artifact:abc:blake3",
            Revision: 1,
            FailureSummary: summary);

    private static async Task<string> RenderAsync(DashboardTestsPanel? tests)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        IServiceProvider provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<WorkspaceTestsPanel>(
                ParameterView.FromDictionary(new Dictionary<string, object?> { ["Tests"] = tests }));
            return output.ToHtmlString();
        });
    }
}
