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
    public void Status_WhenNotOptedIn_DiscoversTestProjectsInsteadOfReportingZero()
    {
        WriteTestProject();

        var (code, outText, _) = Run("tests", "status", "--json");

        Assert.Equal(0, code);
        using JsonDocument doc = JsonDocument.Parse(outText);
        JsonElement root = doc.RootElement;
        Assert.False(root.GetProperty("enabled").GetBoolean());
        Assert.True(root.GetProperty("projects_discovered").GetBoolean());
        JsonElement projects = root.GetProperty("projects");
        Assert.Equal(1, projects.GetArrayLength());
        Assert.Equal("xunit", projects[0].GetProperty("framework").GetString());
    }

    [Fact]
    public void Status_WhenNotOptedIn_DiscoveryCreatesNothing()
    {
        WriteTestProject();

        Assert.Equal(0, Run("tests", "status", "--json").Code);

        Assert.False(File.Exists(CtSchema.DbPathFor(_root)));
        Assert.False(Directory.Exists(CtDaemonProtocol.RootDirectory(_root)));
        Assert.Null(CtDaemonLease.TryRead(_root));
    }

    [Fact]
    public void Status_WhenNotOptedIn_CompactNamesTheCheapPathBeforeEnabling()
    {
        WriteTestProject();

        var (code, outText, _) = Run("tests", "status");

        Assert.Equal(0, code);
        Assert.Contains("enabled: false (continuous testing is opt-in here)", outText, StringComparison.Ordinal);
        Assert.Contains("projects: 1 discovered (not tracked yet)", outText, StringComparison.Ordinal);
        int directRun = outText.IndexOf("run these tests directly", StringComparison.Ordinal);
        int enableLadder = outText.IndexOf("tests operation=enable", StringComparison.Ordinal);
        Assert.True(directRun >= 0 && enableLadder > directRun);
    }

    [Fact]
    public void Status_WhenNotOptedIn_WithNoTestProjects_SaysSoRatherThanOfferingTheLadder()
    {
        var (code, outText, _) = Run("tests", "status");

        Assert.Equal(0, code);
        Assert.Contains("projects: 0", outText, StringComparison.Ordinal);
        Assert.DoesNotContain("discovered (not tracked yet)", outText, StringComparison.Ordinal);
        Assert.Contains("no test projects found", outText, StringComparison.Ordinal);
        Assert.DoesNotContain("tests operation=enable", outText, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_WhenOptedIn_ReportsRecordedRowsNotAScan()
    {
        WriteTestProject();
        Assert.Equal(0, Run("tests", "enable").Code);

        var (code, outText, _) = Run("tests", "status", "--json");

        Assert.Equal(0, code);
        using JsonDocument doc = JsonDocument.Parse(outText);
        Assert.True(doc.RootElement.GetProperty("enabled").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("projects_discovered").GetBoolean());
        Assert.DoesNotContain("continuous testing is opt-in here", Run("tests", "status").Out, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_AfterAnExplicitDisable_DoesNotReofferTheProjectsItWasToldToDrop()
    {
        string project = WriteTestProject();
        Assert.Equal(0, Run("tests", "enable").Code);
        Assert.Equal(0, Run("tests", "disable", "--project", project).Code);

        var (code, outText, _) = Run("tests", "status", "--json");

        Assert.Equal(0, code);
        using JsonDocument doc = JsonDocument.Parse(outText);
        Assert.False(doc.RootElement.GetProperty("enabled").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("projects_discovered").GetBoolean());
        Assert.Empty(doc.RootElement.GetProperty("projects").EnumerateArray());
        Assert.DoesNotContain("tests operation=enable", Run("tests", "status").Out, StringComparison.Ordinal);
    }

    [Fact]
    public void Enable_WithNoSupportedTestProjects_RefusesAndWritesNothing()
    {
        File.WriteAllText(Path.Combine(_root, "go.mod"), "module example.com/thing\ngo 1.23\n");

        var (code, _, errText) = Run("tests", "enable");

        Assert.Equal(3, code);
        Assert.Contains("no supported test projects found", errText, StringComparison.Ordinal);
        Assert.Contains("Rust (cargo)", errText, StringComparison.Ordinal);
        Assert.False(File.Exists(ContinuousTestPolicy.EnabledMarkerPath(_root)));
        Assert.False(File.Exists(CtSchema.DbPathFor(_root)));
    }

    [Fact]
    public void Enable_ByProject_RefusesAFileWithNoKnownFramework()
    {
        string goMod = Path.Combine(_root, "go.mod");
        File.WriteAllText(goMod, "module example.com/thing\ngo 1.23\n");

        var (code, _, errText) = Run("tests", "enable", "--project", goMod);

        Assert.Equal(3, code);
        Assert.Contains("not a supported test project", errText, StringComparison.Ordinal);
        Assert.False(File.Exists(ContinuousTestPolicy.EnabledMarkerPath(_root)));
    }

    [Fact]
    public void Enable_WithOnlyXunitV2Projects_RefusesNamesTheReasonAndWritesNothing()
    {
        string project = WriteTestProject(frameworkHint: "xunit-v2");

        var (code, _, errText) = Run("tests", "enable");

        Assert.Equal(3, code);
        Assert.Contains(ContinuousTestFrameworkSupport.XunitV2Reason, errText, StringComparison.Ordinal);
        Assert.Contains(Path.GetFullPath(project), errText, StringComparison.Ordinal);
        Assert.Contains("xunit.v3", errText, StringComparison.Ordinal);
        Assert.Contains("Nothing was enabled and nothing was written.", errText, StringComparison.Ordinal);
        Assert.False(File.Exists(ContinuousTestPolicy.EnabledMarkerPath(_root)));
        Assert.False(File.Exists(CtSchema.DbPathFor(_root)));
    }

    [Fact]
    public void Enable_ByProject_RefusesAnXunitV2ProjectNamedDirectly()
    {
        string project = WriteTestProject(frameworkHint: "xunit-v2");

        var (code, _, errText) = Run("tests", "enable", "--project", project);

        Assert.Equal(3, code);
        Assert.Contains(ContinuousTestFrameworkSupport.XunitV2Reason, errText, StringComparison.Ordinal);
        Assert.Contains(Path.GetFullPath(project), errText, StringComparison.Ordinal);
        Assert.False(File.Exists(ContinuousTestPolicy.EnabledMarkerPath(_root)));
        Assert.False(File.Exists(CtSchema.DbPathFor(_root)));
    }

    [Fact]
    public void Enable_OnAMixedRepository_EnablesTheSupportedProjectsAndReportsTheRest()
    {
        string v2 = WriteTestProject("tests/Old.Tests/Old.Tests.csproj", frameworkHint: "xunit-v2");
        string v3 = WriteTestProject("tests/New.Tests/New.Tests.csproj");

        var (code, outText, errText) = Run("tests", "enable", "--json");

        Assert.Equal(0, code);
        Assert.Empty(errText);
        using JsonDocument doc = JsonDocument.Parse(outText);
        Assert.Equal(1, doc.RootElement.GetProperty("enabled_count").GetInt32());
        Assert.Equal(
            Path.GetFullPath(v3),
            Assert.Single(doc.RootElement.GetProperty("projects").EnumerateArray())
                .GetProperty("project_path").GetString());

        Assert.Equal(1, doc.RootElement.GetProperty("unsupported_count").GetInt32());
        JsonElement unsupported = Assert.Single(doc.RootElement.GetProperty("unsupported_projects").EnumerateArray());
        Assert.Equal(Path.GetFullPath(v2), unsupported.GetProperty("project_path").GetString());
        Assert.Equal(ContinuousTestFrameworkSupport.XunitV2, unsupported.GetProperty("framework").GetString());
        Assert.Equal(
            ContinuousTestFrameworkSupport.XunitV2Reason,
            unsupported.GetProperty("unsupported_reason").GetString());

        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        ContinuousTestProject row = Assert.Single(
            store.ListContinuousTestProjects(WorkspaceId.FromCanonicalRoot(_root), includeDisabled: true));
        Assert.Equal(Path.GetFullPath(v3), row.ProjectPath);
    }

    [Fact]
    public void Enable_OnAMixedRepository_SaysWhyTheUnsupportedProjectWasLeftOut()
    {
        string v2 = WriteTestProject("tests/Old.Tests/Old.Tests.csproj", frameworkHint: "xunit-v2");
        WriteTestProject("tests/New.Tests/New.Tests.csproj");

        var (code, outText, _) = Run("tests", "enable");

        Assert.Equal(0, code);
        Assert.Contains("unsupported: 1 project(s)", outText, StringComparison.Ordinal);
        Assert.Contains(Path.GetFullPath(v2), outText, StringComparison.Ordinal);
        Assert.Contains(ContinuousTestFrameworkSupport.XunitV2Reason, outText, StringComparison.Ordinal);
    }

    [Fact]
    public void Enable_Compact_EndsWithTheStartAndRunHint()
    {
        string project = WriteTestProject("tests/New.Tests/New.Tests.csproj");

        var (code, outText, errText) = Run("tests", "enable");

        Assert.Equal(0, code);
        Assert.Empty(errText);
        string[] lines = CompactLines(outText);
        Assert.Equal("enable 1 project(s)", lines[0]);
        Assert.Equal("  - " + Path.GetFullPath(project), lines[1]);
        Assert.Equal(
            "next: tests operation=start watches for changes; operation=run executes the owed backlog now.",
            lines[2]);
        Assert.Equal(3, lines.Length);
    }

    [Fact]
    public void Enable_Json_CarriesNoHint()
    {
        WriteTestProject("tests/New.Tests/New.Tests.csproj");

        var (code, outText, _) = Run("tests", "enable", "--json");

        Assert.Equal(0, code);
        Assert.DoesNotContain("next:", outText, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_OnANeverDecidedWorkspace_NamesTheXunitV2ReasonAndOffersNoEnableLadder()
    {
        string project = WriteTestProject(frameworkHint: "xunit-v2");

        var (code, outText, _) = Run("tests", "status");

        Assert.Equal(0, code);
        Assert.Contains(Path.GetFullPath(project), outText, StringComparison.Ordinal);
        Assert.Contains(ContinuousTestFrameworkSupport.XunitV2Reason, outText, StringComparison.Ordinal);
        Assert.Contains("run these tests directly", outText, StringComparison.Ordinal);
        Assert.DoesNotContain("tests operation=enable", outText, StringComparison.Ordinal);

        var json = Run("tests", "status", "--json");
        using JsonDocument doc = JsonDocument.Parse(json.Out);
        JsonElement row = Assert.Single(doc.RootElement.GetProperty("projects").EnumerateArray());
        Assert.Equal(ContinuousTestFrameworkSupport.XunitV2, row.GetProperty("framework").GetString());
        Assert.Equal(
            ContinuousTestFrameworkSupport.XunitV2Reason,
            row.GetProperty("unsupported_reason").GetString());
    }

    [Fact]
    public void Status_OnANeverDecidedWorkspaceWithARunnableProject_StillOffersTheEnableLadder()
    {
        WriteTestProject("tests/Old.Tests/Old.Tests.csproj", frameworkHint: "xunit-v2");
        WriteTestProject("tests/New.Tests/New.Tests.csproj");

        var (code, outText, _) = Run("tests", "status");

        Assert.Equal(0, code);
        Assert.Contains("tests operation=enable", outText, StringComparison.Ordinal);
    }

    [Fact]
    public void Enable_ByProject_StillAcceptsADotnetProjectWithNoRecognizedPackageReference()
    {
        string project = Path.Combine(_root, "App.Tests.csproj");
        File.WriteAllText(project, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);

        var (code, outText, _) = Run("tests", "enable", "--project", project);

        Assert.Equal(0, code);
        Assert.Contains("App.Tests.csproj", outText, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_WhenEnabledWithNothingToWatch_SaysItWillNeverReportAVerdict()
    {
        string marker = ContinuousTestPolicy.EnabledMarkerPath(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.WriteAllText(marker, string.Empty);

        var (code, outText, _) = Run("tests", "status");

        Assert.Equal(0, code);
        Assert.Contains("CT is on but has no projects to watch", outText, StringComparison.Ordinal);
        Assert.Contains("tests operation=disable", outText, StringComparison.Ordinal);
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

        // A full disable leaves the opt-out tombstone so the decision sticks even where an
        // enablement could be inherited (a linked worktree of an enabled main checkout).
        Assert.True(File.Exists(ContinuousTestPolicy.DisabledMarkerPath(_root)));
        Assert.False(File.Exists(ContinuousTestPolicy.EnabledMarkerPath(_root)));
    }

    [Fact]
    public void Disable_OnInheritedEnabledWorktree_SticksWithoutTouchingMain()
    {
        var (main, wt) = BuildLinkedWorktreeOfEnabledMain();
        Assert.True(ContinuousTestPolicy.IsWorkspaceOptedIn(wt));

        TestsMutationResult result = TestsCore.Disable(new TestsCoreRequest(wt));

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(ContinuousTestPolicy.DisabledMarkerPath(wt)));
        Assert.False(File.Exists(ContinuousTestPolicy.EnabledMarkerPath(wt)));
        Assert.False(ContinuousTestPolicy.IsWorkspaceOptedIn(wt));
        // The MAIN checkout's marker is not this worktree's to touch.
        Assert.True(File.Exists(ContinuousTestPolicy.EnabledMarkerPath(main)));
        Assert.True(ContinuousTestPolicy.IsWorkspaceOptedIn(main));
    }

    [Fact]
    public void Enable_OnWorktree_RemovesTombstoneAndWritesLocalMarker()
    {
        var (_, wt) = BuildLinkedWorktreeOfEnabledMain();
        Assert.Equal(0, TestsCore.Disable(new TestsCoreRequest(wt)).ExitCode);
        Assert.False(ContinuousTestPolicy.IsWorkspaceOptedIn(wt));

        TestsMutationResult result = TestsCore.Enable(new TestsCoreRequest(wt));

        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(ContinuousTestPolicy.DisabledMarkerPath(wt)));
        Assert.True(File.Exists(ContinuousTestPolicy.EnabledMarkerPath(wt)));
        Assert.True(ContinuousTestPolicy.IsWorkspaceOptedIn(wt));
    }

    [Fact]
    public void Disable_ProjectScoped_WithARemainingProject_DoesNotOptTheWorkspaceOut()
    {
        string first = WriteTestProject("tests/One.Tests/One.Tests.csproj");
        WriteTestProject("tests/Two.Tests/Two.Tests.csproj");
        Assert.Equal(0, Run("tests", "enable").Code);

        var (code, _, errText) = Run("tests", "disable", "--project", first, "--json");

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.False(File.Exists(ContinuousTestPolicy.DisabledMarkerPath(_root)));
        Assert.True(File.Exists(ContinuousTestPolicy.EnabledMarkerPath(_root)));
        Assert.True(ContinuousTestPolicy.IsWorkspaceOptedIn(_root));
    }

    [Fact]
    public void Disable_ProjectScoped_Json_ReportsTheProjectItTurnedOff()
    {
        string first = WriteTestProject("tests/One.Tests/One.Tests.csproj");
        string second = WriteTestProject("tests/Two.Tests/Two.Tests.csproj");
        Assert.Equal(0, Run("tests", "enable").Code);

        var (code, outText, errText) = Run("tests", "disable", "--project", first, "--json");

        Assert.Equal(0, code);
        Assert.Empty(errText);
        using JsonDocument doc = JsonDocument.Parse(outText);
        JsonElement root = doc.RootElement;
        Assert.Equal("disable", root.GetProperty("operation").GetString());
        // What the call DID: it turned one project off.
        Assert.Equal(1, root.GetProperty("changed_count").GetInt32());
        JsonElement changed = Assert.Single(root.GetProperty("changed_projects").EnumerateArray());
        Assert.Equal(Path.GetFullPath(first), changed.GetProperty("project_path").GetString());
        Assert.False(changed.GetProperty("enabled").GetBoolean());
        // What is left: the other project stays enabled.
        Assert.Equal(1, root.GetProperty("enabled_count").GetInt32());
        JsonElement remaining = Assert.Single(root.GetProperty("projects").EnumerateArray());
        Assert.Equal(Path.GetFullPath(second), remaining.GetProperty("project_path").GetString());
        Assert.True(remaining.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public void Disable_ProjectScoped_Compact_HeadsWhatItDisabledAndLabelsTheRemainder()
    {
        string first = WriteTestProject("tests/One.Tests/One.Tests.csproj");
        string second = WriteTestProject("tests/Two.Tests/Two.Tests.csproj");
        Assert.Equal(0, Run("tests", "enable").Code);

        var (code, outText, errText) = Run("tests", "disable", "--project", first);

        Assert.Equal(0, code);
        Assert.Empty(errText);
        string[] lines = CompactLines(outText);
        Assert.Equal("disable 1 project(s)", lines[0]);
        Assert.Equal("  - " + Path.GetFullPath(first), lines[1]);
        Assert.Equal("remaining enabled: 1", lines[2]);
        Assert.Equal("  - " + Path.GetFullPath(second), lines[3]);
        Assert.Equal(4, lines.Length);
    }

    [Fact]
    public void Disable_WholeWorkspace_Compact_CountsEveryProjectItTurnedOff()
    {
        string first = WriteTestProject("tests/One.Tests/One.Tests.csproj");
        string second = WriteTestProject("tests/Two.Tests/Two.Tests.csproj");
        Assert.Equal(0, Run("tests", "enable").Code);

        var (code, outText, errText) = Run("tests", "disable");

        Assert.Equal(0, code);
        Assert.Empty(errText);
        string[] lines = CompactLines(outText);
        Assert.Equal("disable 2 project(s)", lines[0]);
        Assert.Contains("  - " + Path.GetFullPath(first), lines);
        Assert.Contains("  - " + Path.GetFullPath(second), lines);
        Assert.Equal("remaining enabled: 0", lines[3]);
        Assert.Equal(4, lines.Length);
    }

    [Fact]
    public void Disable_OfAnAlreadyDisabledProject_ReportsNoChange()
    {
        string project = WriteTestProject();
        Assert.Equal(0, Run("tests", "enable").Code);
        Assert.Equal(0, Run("tests", "disable", "--project", project).Code);

        var (code, outText, errText) = Run("tests", "disable", "--project", project, "--json");

        Assert.Equal(0, code);
        Assert.Empty(errText);
        using JsonDocument doc = JsonDocument.Parse(outText);
        Assert.Equal(0, doc.RootElement.GetProperty("changed_count").GetInt32());
        Assert.Empty(doc.RootElement.GetProperty("changed_projects").EnumerateArray());
        Assert.Equal(0, doc.RootElement.GetProperty("enabled_count").GetInt32());
    }

    [Fact]
    public void Enable_Json_ReportsWhatItTurnedOnBesideEverythingEnabled()
    {
        WriteTestProject("tests/One.Tests/One.Tests.csproj");
        Assert.Equal(0, Run("tests", "enable").Code);
        string second = WriteTestProject("tests/Two.Tests/Two.Tests.csproj");

        var (code, outText, errText) = Run("tests", "enable", "--json");

        Assert.Equal(0, code);
        Assert.Empty(errText);
        using JsonDocument doc = JsonDocument.Parse(outText);
        JsonElement root = doc.RootElement;
        Assert.Equal("enable", root.GetProperty("operation").GetString());
        // Both projects are enabled now; only the second one was turned on by this call.
        Assert.Equal(2, root.GetProperty("enabled_count").GetInt32());
        Assert.Equal(2, root.GetProperty("projects").GetArrayLength());
        Assert.Equal(1, root.GetProperty("changed_count").GetInt32());
        JsonElement changed = Assert.Single(root.GetProperty("changed_projects").EnumerateArray());
        Assert.Equal(Path.GetFullPath(second), changed.GetProperty("project_path").GetString());
        Assert.True(changed.GetProperty("enabled").GetBoolean());
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
        string[] lines = outText.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("ct daemon start ", lines[0], StringComparison.Ordinal);
        using JsonDocument doc = JsonDocument.Parse(string.Join(Environment.NewLine, lines.Skip(1)));
        Assert.Equal("disabled", doc.RootElement.GetProperty("reason").GetString());
        Assert.False(File.Exists(CtSchema.DbPathFor(_root)));
        Assert.False(Directory.Exists(CtDaemonProtocol.RootDirectory(_root)));
    }

    [Fact]
    public void CtDaemonVerb_PrintsExactlyOneStartupBreadcrumbNamingTheSharedLog()
    {
        var (_, outText, _) = Run("ct-daemon");

        string[] breadcrumbs = outText
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("ct daemon start ", StringComparison.Ordinal))
            .ToArray();
        string breadcrumb = Assert.Single(breadcrumbs);
        Assert.Contains($"version={MillerVersion.Current}", breadcrumb);
        Assert.Contains($"pid={Environment.ProcessId}", breadcrumb);
        Assert.Contains(Path.Combine(_root, ".miller", "logs", "miller-"), breadcrumb);
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
    public void Failures_group_error_class_is_a_cli_flag()
    {
        SeedRedCases(3);

        var (code, outText, errText) = Run("tests", "failures", "--group", "error_class", "--json");

        Assert.Equal(0, code);
        Assert.Empty(errText);
        using JsonDocument document = JsonDocument.Parse(outText);
        JsonElement root = document.RootElement;
        Assert.False(root.TryGetProperty("failures", out _));
        JsonElement group = root.GetProperty("groups")[0];
        Assert.Equal("unclassified", group.GetProperty("error_class").GetString());
        Assert.Equal(3, group.GetProperty("count").GetInt32());
        Assert.False(group.GetProperty("infra_shaped").GetBoolean());
        Assert.Equal(3, root.GetProperty("total").GetInt32());
    }

    [Fact]
    public void An_unknown_group_value_is_a_usage_error()
    {
        var (code, _, errText) = Run("tests", "failures", "--group", "bogus");

        Assert.Equal(2, code);
        Assert.Contains("error_class", errText, StringComparison.Ordinal);
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

    private static string[] CompactLines(string output) =>
        output.Trim().Split('\n').Select(static line => line.TrimEnd('\r')).ToArray();

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

    /// <summary>
    /// A hand-built linked-worktree layout (a <c>.git</c> FILE with <c>gitdir:</c> plus the admin
    /// dir's <c>commondir</c> pointer — no git subprocess) whose main checkout carries
    /// <c>.miller/ct.enabled</c>, so the worktree starts inherited-enabled.
    /// </summary>
    private (string MainRoot, string WorktreeRoot) BuildLinkedWorktreeOfEnabledMain()
    {
        string main = Path.Combine(_dir, "main");
        string wt = Path.Combine(_dir, "wt");
        string adminDir = Path.Combine(main, ".git", "worktrees", "wt");
        Directory.CreateDirectory(adminDir);
        File.WriteAllText(Path.Combine(adminDir, "commondir"), "../..\n");
        Directory.CreateDirectory(wt);
        File.WriteAllText(Path.Combine(wt, ".git"), $"gitdir: {adminDir}\n");
        string marker = ContinuousTestPolicy.EnabledMarkerPath(main);
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.WriteAllText(marker, string.Empty);
        return (main, wt);
    }

    private string WriteTestProject(string relativePath = "tests/Sample.Tests/Sample.Tests.csproj", string frameworkHint = "xunit")
    {
        string path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string package = frameworkHint switch
        {
            "nunit" => "NUnit",
            "mstest" => "MSTest.TestFramework",
            "xunit-v2" => "xunit",
            _ => "xunit.v3",
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

    /// <summary>
    /// The daemon's lease has always recorded which Miller build it runs, and nothing read it — so an
    /// upgraded Miller kept the old daemon and status called it healthy. Status now reports the
    /// comparison, and with no daemon the four keys read as the honest "nothing to compare".
    /// </summary>
    [Fact]
    public void Status_Json_ReportsTheDaemonBuildVersionFields()
    {
        WriteTestProject();
        Assert.Equal(0, Run("tests", "enable").Code);

        var (code, outText, _) = Run("tests", "status", "--json");

        Assert.Equal(0, code);
        using JsonDocument doc = JsonDocument.Parse(outText);
        JsonElement root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("schema_version").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("miller_version").GetString()));

        JsonElement daemon = root.GetProperty("daemon");
        Assert.Equal(JsonValueKind.Null, daemon.GetProperty("miller_version").ValueKind);
        Assert.Equal("none", daemon.GetProperty("version_match").GetString());
        Assert.False(daemon.GetProperty("version_mismatch").GetBoolean());
        Assert.Equal("no live daemon", daemon.GetProperty("version_reason").GetString());
    }

    /// <summary>
    /// The kill switch guarantees there is no daemon, so the comparison is "none" — and the new lease
    /// read must not break the zero-WORK guarantee, which is stronger than zero-creation.
    ///
    /// <para>The workspace is seeded with a LIVE lease held by this very process, so the test cannot
    /// pass by accident: a version read that ran before the short-circuit would find that lease and
    /// report a build, not "none". Asserting only that nothing was created would leave the
    /// short-circuit free to move.</para>
    ///
    /// <para>Driven through <see cref="TestsCore"/> rather than the CLI on purpose: the switch is read
    /// from the environment, and xUnit runs test classes in parallel, so setting a process-wide
    /// variable here would reach into every other class running beside this one.</para>
    /// </summary>
    [Fact]
    public void Status_UnderTheKillSwitch_ReadsNoLeaseEvenWhenOneIsLive()
    {
        WriteTestProject();
        using CtDaemonLease? lease = CtDaemonLease.TryAcquire(_root, "1.9.0+aaa");
        Assert.NotNull(lease);
        Assert.NotNull(CtDaemonLease.TryReadLive(_root));

        TestsStatusResult result = TestsCore.Status(
            new TestsCoreRequest(
                _root,
                MillerHome: Path.GetDirectoryName(_registryDb),
                KillSwitch: "off",
                MillerVersion: "1.13.0+bbb"));

        using JsonDocument doc = JsonDocument.Parse(result.Render(json: true));
        JsonElement root = doc.RootElement;
        Assert.True(root.GetProperty("kill_switch").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("miller_version").GetString()));

        JsonElement daemon = root.GetProperty("daemon");
        Assert.Equal(JsonValueKind.Null, daemon.GetProperty("miller_version").ValueKind);
        Assert.Equal("none", daemon.GetProperty("version_match").GetString());
        Assert.False(daemon.GetProperty("version_mismatch").GetBoolean());
        Assert.Equal("no live daemon", daemon.GetProperty("version_reason").GetString());
        Assert.Equal("disabled", daemon.GetProperty("reason").GetString());
    }

    /// <summary>
    /// A takeover is a SUCCESS, end to end. Until this landed, an upgraded Miller met the old
    /// daemon's live lease, answered exit 0, and started nothing — so the tree kept being watched by
    /// the code you replaced. This drives the whole chain: the lease is parked on a real child
    /// process, so the stop that the replace performs is the real one.
    /// </summary>
    [Fact]
    public void Start_WhenTheLiveDaemonRunsAnOlderBuild_ReplacesItAndReportsSuccess()
    {
        WriteTestProject();
        Assert.Equal(0, Run("tests", "enable").Code);

        using Process stub = StartStub();
        var older = new CtDaemonLeaseIdentity(stub.Id, new DateTimeOffset(stub.StartTime.ToUniversalTime()));
        using CtDaemonLease? lease = CtDaemonLease.TryAcquire(_root, "1.9.0+aaa", older);
        Assert.NotNull(lease);

        ProcessStartInfo? spawned = null;
        TestsServeResult result = TestsCore.Start(new TestsCoreRequest(
            _root,
            MillerHome: Path.GetDirectoryName(_registryDb),
            MillerVersion: "1.13.0+bbb",
            Hooks: new TestsCoreHooks
            {
                StartProcess = info =>
                {
                    spawned = info;
                    return stub;
                },
            }));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("replaced", result.Status);
        Assert.Contains("1.9.0+aaa", result.Reason, StringComparison.Ordinal);
        Assert.NotNull(spawned);
    }

    [Fact]
    public void Status_SurfacesTheAutoRunPauseFromALiveDaemonRecord()
    {
        EnableWorkspace();
        WriteDaemonStatusRecord(CtDaemonJson.Serialize(new CtDaemonStatusRecord(
            CtDaemonLifecycleState.Running,
            "auto-runs paused: impact unavailable (moving_cursor)",
            CurrentProcessIdentity(),
            DateTimeOffset.UtcNow,
            AutoRunsPaused: true,
            PauseReason: "impact unavailable (moving_cursor)")));

        var (code, outText, _) = Run("tests", "status", "--json");

        Assert.Equal(0, code);
        using JsonDocument doc = JsonDocument.Parse(outText);
        JsonElement daemon = doc.RootElement.GetProperty("daemon");
        Assert.True(daemon.GetProperty("auto_runs_paused").GetBoolean());
        Assert.Equal("impact unavailable (moving_cursor)", daemon.GetProperty("pause_reason").GetString());
        Assert.Equal("running", daemon.GetProperty("state").GetString());
        Assert.False(daemon.GetProperty("paused").GetBoolean());

        var (compactCode, compactOut, _) = Run("tests", "status");
        Assert.Equal(0, compactCode);
        Assert.Contains(
            CompactLines(compactOut),
            line => line == "auto-runs paused: impact unavailable (moving_cursor)");
    }

    [Fact]
    public void Status_ReadsARecordWithoutThePauseFieldsAsNotPaused()
    {
        EnableWorkspace();
        CtDaemonLeaseIdentity identity = CurrentProcessIdentity();
        WriteDaemonStatusRecord(
            $$"""
            {"state":"running","reason":"idle",
             "identity":{"pid":{{identity.Pid}},"process_start_time_utc":"{{identity.ProcessStartTimeUtc:O}}"},
             "updated_at_utc":"{{DateTimeOffset.UtcNow:O}}"}
            """);

        var (code, outText, _) = Run("tests", "status", "--json");

        Assert.Equal(0, code);
        using JsonDocument doc = JsonDocument.Parse(outText);
        JsonElement daemon = doc.RootElement.GetProperty("daemon");
        Assert.Equal("running", daemon.GetProperty("state").GetString());
        Assert.False(daemon.GetProperty("auto_runs_paused").GetBoolean());
        Assert.Equal(JsonValueKind.Null, daemon.GetProperty("pause_reason").ValueKind);
        Assert.DoesNotContain("auto-runs paused:", Run("tests", "status").Out, StringComparison.Ordinal);
    }

    private void EnableWorkspace()
    {
        string marker = ContinuousTestPolicy.EnabledMarkerPath(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.WriteAllText(marker, string.Empty);
    }

    private void WriteDaemonStatusRecord(string json)
    {
        Directory.CreateDirectory(CtDaemonProtocol.RootDirectory(_root));
        File.WriteAllText(CtDaemonProtocol.StatusPath(_root), json);
    }

    private static CtDaemonLeaseIdentity CurrentProcessIdentity()
    {
        using var process = Process.GetCurrentProcess();
        return new CtDaemonLeaseIdentity(
            process.Id, new DateTimeOffset(process.StartTime.ToUniversalTime()));
    }

    /// <summary>A stub that outlives the test, so a parked lease reads as live.</summary>
    private static Process StartStub()
    {
        var info = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true };
        if (OperatingSystem.IsWindows())
        {
            info.FileName = "cmd.exe";
            info.ArgumentList.Add("/c");
            info.ArgumentList.Add("ping -n 30 127.0.0.1 >nul");
        }
        else
        {
            info.FileName = "sleep";
            info.ArgumentList.Add("30");
        }

        return Process.Start(info) ?? throw new InvalidOperationException("stub process did not start");
    }

    private static void AssertStatusContractShape(JsonElement root)
    {
        Assert.True(root.TryGetProperty("schema_version", out _));
        Assert.True(root.TryGetProperty("miller_version", out _));
        Assert.True(root.TryGetProperty("enabled", out _));
        Assert.True(root.TryGetProperty("projects", out JsonElement projects));
        Assert.Equal(JsonValueKind.Array, projects.ValueKind);
        Assert.True(root.TryGetProperty("daemon", out JsonElement daemon));
        Assert.True(daemon.TryGetProperty("state", out _));
        Assert.True(daemon.TryGetProperty("reason", out _));
        Assert.True(daemon.TryGetProperty("running", out _));
        Assert.True(daemon.TryGetProperty("paused", out _));
        Assert.True(daemon.TryGetProperty("auto_runs_paused", out _));
        Assert.True(daemon.TryGetProperty("pause_reason", out _));
        Assert.True(daemon.TryGetProperty("miller_version", out _));
        Assert.True(daemon.TryGetProperty("version_match", out _));
        Assert.True(daemon.TryGetProperty("version_mismatch", out _));
        Assert.True(daemon.TryGetProperty("version_reason", out _));
        Assert.True(root.TryGetProperty("verdict", out _));
        Assert.True(root.TryGetProperty("selected", out _));
        Assert.True(root.TryGetProperty("stale_count", out _));
        Assert.True(root.TryGetProperty("last_run", out _));
        Assert.True(root.TryGetProperty("budget_holder", out _));
    }
}
