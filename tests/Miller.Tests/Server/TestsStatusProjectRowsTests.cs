using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Indexing.Testing;
using Miller.Server.Tools;
using Miller.Testing;
using Miller.Tests.Testing.Selection;
using Xunit;

namespace Miller.Tests.Server;

public sealed class TestsStatusProjectRowsTests : IDisposable
{
    private static readonly DateTimeOffset RunEndedAt = new(2026, 8, 22, 9, 31, 0, TimeSpan.Zero);
    private const string RunEndedAtIso = "2026-08-22T09:31:00.0000000+00:00";

    private readonly string _dir;
    private readonly string _root;
    private readonly string _workspaceId;

    public TestsStatusProjectRowsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-tests-projrows-" + Guid.NewGuid().ToString("N")[..10]);
        _root = Path.Combine(_dir, "workspace");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_dir, "home"));
        _workspaceId = WorkspaceId.FromCanonicalRoot(_root);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Status_reports_each_projects_counts_verdict_and_last_run()
    {
        string green = PutProject("p-green", "tests/Green.Tests/Green.Tests.csproj");
        string silent = PutProject("p-silent", "tests/Silent.Tests/Silent.Tests.csproj");
        SeedCases(green, count: 2, resultStatus: "passed", identity: "gen-live", revision: 50);

        TestsStatusResult status = TestsCore.Status(Request(FactsHooks("gen-live", 50)));

        TestsStatusProject greenRow = status.Projects.Single(row => row.ProjectPath == green);
        Assert.Equal(2, greenRow.CaseCount);
        Assert.Equal(0, greenRow.StaleCount);
        Assert.Equal(0, greenRow.RedCount);
        Assert.Equal(ContinuousTestVerdict.Green, greenRow.Verdict);
        Assert.Equal(RunEndedAt, greenRow.LastRunAt);

        TestsStatusProject silentRow = status.Projects.Single(row => row.ProjectPath == silent);
        Assert.Equal(0, silentRow.CaseCount);
        Assert.Equal(ContinuousTestVerdict.Unknown, silentRow.Verdict);
        Assert.Null(silentRow.LastRunAt);
    }

    [Fact]
    public void Status_json_projects_carry_the_additive_per_project_fields()
    {
        string green = PutProject("p-green", "tests/Green.Tests/Green.Tests.csproj");
        string silent = PutProject("p-silent", "tests/Silent.Tests/Silent.Tests.csproj");
        SeedCases(green, count: 2, resultStatus: "passed", identity: "gen-live", revision: 50);

        TestsStatusResult status = TestsCore.Status(Request(FactsHooks("gen-live", 50)));

        using JsonDocument document = JsonDocument.Parse(TestsCore.RenderStatusJson(status));
        JsonElement greenRow = ProjectEntry(document, green);
        Assert.Equal(2, greenRow.GetProperty("case_count").GetInt32());
        Assert.Equal(0, greenRow.GetProperty("stale_count").GetInt32());
        Assert.Equal(0, greenRow.GetProperty("red_count").GetInt32());
        Assert.Equal("green", greenRow.GetProperty("verdict").GetString());
        Assert.Equal(RunEndedAtIso, greenRow.GetProperty("last_run_at").GetString());

        JsonElement silentRow = ProjectEntry(document, silent);
        Assert.Equal(0, silentRow.GetProperty("case_count").GetInt32());
        Assert.Equal("unknown", silentRow.GetProperty("verdict").GetString());
        Assert.Equal(JsonValueKind.Null, silentRow.GetProperty("last_run_at").ValueKind);
    }

    [Fact]
    public void Compact_status_extends_each_project_line_and_reads_never_for_a_project_with_no_rows()
    {
        string green = PutProject("p-green", "tests/Green.Tests/Green.Tests.csproj");
        string silent = PutProject("p-silent", "tests/Silent.Tests/Silent.Tests.csproj");
        SeedCases(green, count: 2, resultStatus: "passed", identity: "gen-live", revision: 50);

        string compact = TestsCore.RenderStatusCompact(TestsCore.Status(Request(FactsHooks("gen-live", 50))));

        Assert.Contains(
            $"  - {green} (xunit) verdict=green cases=2 stale=0 red=0 last_run={RunEndedAtIso}",
            compact,
            StringComparison.Ordinal);
        Assert.Contains(
            $"  - {silent} (xunit) verdict=unknown cases=0 stale=0 red=0 last_run=never",
            compact,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_red_project_and_a_stale_project_each_name_their_own_fault()
    {
        string red = PutProject("p-red", "tests/Red.Tests/Red.Tests.csproj");
        string stale = PutProject("p-stale", "tests/Stale.Tests/Stale.Tests.csproj");
        SeedCases(red, count: 1, resultStatus: "failed", identity: "gen-live", revision: 50);
        SeedCases(stale, count: 1, resultStatus: "passed", identity: "gen-live", revision: 40, caseOffset: 100);

        TestsStatusResult status = TestsCore.Status(Request(FactsHooks("gen-live", 50)));

        TestsStatusProject redRow = status.Projects.Single(row => row.ProjectPath == red);
        Assert.Equal(ContinuousTestVerdict.Red, redRow.Verdict);
        Assert.Equal(1, redRow.RedCount);
        Assert.Equal(0, redRow.StaleCount);

        TestsStatusProject staleRow = status.Projects.Single(row => row.ProjectPath == stale);
        Assert.Equal(ContinuousTestVerdict.Partial, staleRow.Verdict);
        Assert.Equal(1, staleRow.StaleCount);
        Assert.Equal(0, staleRow.RedCount);
    }

    [Fact]
    public void Compact_covers_all_names_the_project_it_applies_to_and_json_keeps_its_field_name()
    {
        TestsStatusResult status = StatusWithSelection(coversAll: true);

        string compact = TestsCore.RenderStatusCompact(status);
        Assert.Contains("covers_all(Sample.Tests.csproj)=true", compact, StringComparison.Ordinal);
        Assert.DoesNotContain(" covers_all=", compact, StringComparison.Ordinal);

        string json = TestsCore.RenderStatusJson(status);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement selection = document.RootElement.GetProperty("daemon").GetProperty("run").GetProperty("selection");
        Assert.True(selection.GetProperty("covers_every_known_case").GetBoolean());
        Assert.DoesNotContain("covers_all", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_project_row_without_per_project_facts_keeps_the_old_bytes()
    {
        var result = new TestsStatusResult(
            Enabled: true,
            KillSwitchOff: false,
            Projects: [new TestsStatusProject("p1", "tests/A.csproj", "xunit", null, true, [])],
            DaemonState: CtDaemonLifecycleState.Stopped,
            DaemonReason: "not started",
            Verdict: ContinuousTestVerdict.Unknown,
            Selected: null,
            StaleCount: 0,
            SelectedCount: 0,
            LastRun: null,
            BudgetHolder: null);

        Assert.Contains("\n  - tests/A.csproj (xunit)\n", TestsCore.RenderStatusCompact(result) + "\n", StringComparison.Ordinal);

        using JsonDocument document = JsonDocument.Parse(TestsCore.RenderStatusJson(result));
        JsonElement project = document.RootElement.GetProperty("projects")[0];
        Assert.False(project.TryGetProperty("case_count", out _));
        Assert.False(project.TryGetProperty("verdict", out _));
        Assert.False(project.TryGetProperty("last_run_at", out _));
    }

    [Fact]
    public void Mutation_renders_carry_no_per_project_status_fields()
    {
        var result = new TestsMutationResult(
            0,
            "enable",
            1,
            [new TestsStatusProject("p1", "tests/A.csproj", "xunit", null, true, [])],
            null);

        string json = TestsCore.RenderMutationJson(result);
        Assert.DoesNotContain("case_count", json, StringComparison.Ordinal);
        Assert.DoesNotContain("last_run_at", json, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unsupported_reason_still_ends_the_extended_project_line()
    {
        string v2 = PutProject("p-v2", "tests/V2.Tests/V2.Tests.csproj", framework: "xunit-v2");

        string compact = TestsCore.RenderStatusCompact(TestsCore.Status(Request(FactsHooks("gen-live", 50))));

        Assert.Contains(
            $"  - {v2} (xunit-v2) verdict=unknown cases=0 stale=0 red=0 last_run=never — ",
            compact,
            StringComparison.Ordinal);
    }

    private static TestsStatusResult StatusWithSelection(bool coversAll)
    {
        var selection = new ContinuousTestDaemonSelectionFacts(
            ContinuousTestSelectionOutcome.WorkspaceScope,
            ContinuousTestRunLane.Foreground,
            KnownCount: 200,
            PreTrimSelectedCount: 205,
            PostTrimSelectedCount: 200,
            RetainedRedCount: 2,
            CoversEveryKnownCase: coversAll,
            Eligible: true,
            ReasonCode: "eligible",
            SelectionDigest: "selection-digest");
        var run = new CtDaemonRunProgress(
            ProjectPath: "/repo/tests/Sample.Tests/Sample.Tests.csproj",
            RunId: "run:42",
            SelectedCaseCount: 200,
            RunStartedAtUtc: new DateTimeOffset(2026, 8, 22, 9, 30, 0, TimeSpan.Zero),
            Activity: CtRunActivity.Active,
            Selection: selection);
        return new TestsStatusResult(
            Enabled: true,
            KillSwitchOff: false,
            Projects: [],
            DaemonState: CtDaemonLifecycleState.Running,
            DaemonReason: "executing",
            Verdict: ContinuousTestVerdict.Partial,
            Selected: null,
            StaleCount: 1,
            SelectedCount: 200,
            LastRun: null,
            BudgetHolder: null,
            DaemonActivity: CtDaemonActivity.Executing,
            DaemonRun: run);
    }

    private static JsonElement ProjectEntry(JsonDocument document, string projectPath) =>
        document.RootElement.GetProperty("projects").EnumerateArray()
            .Single(entry => entry.GetProperty("project_path").GetString() == projectPath);

    private static TestsCoreHooks FactsHooks(string identity, long revision) =>
        new(OpenFacts: (_, _) => new FakeMillerFactSource
        {
            Current = new CtIndexCursor(identity, revision),
        });

    private TestsCoreRequest Request(TestsCoreHooks hooks) =>
        new(
            WorkspaceRoot: _root,
            WorkspaceId: _workspaceId,
            MillerHome: Path.Combine(_dir, "home"),
            Hooks: hooks);

    private string PutProject(string id, string relativePath, string framework = "xunit")
    {
        string fullPath = Path.GetFullPath(Path.Combine(_root, relativePath));
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        store.Transaction(() => store.PutContinuousTestProject(new ContinuousTestProject(
            Id: id,
            WorkspaceId: _workspaceId,
            ProjectPath: fullPath,
            Framework: framework,
            Command: "dotnet test",
            Enabled: true)));
        return fullPath;
    }

    private void SeedCases(
        string projectPath,
        int count,
        string resultStatus,
        string identity,
        long revision,
        int caseOffset = 0)
    {
        string revisionText = revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        store.Transaction(() =>
        {
            for (int offset = 0; offset < count; offset++)
            {
                int index = caseOffset + offset;
                string caseId = $"test:{index:D3}";
                string runId = $"run:{index:D3}";
                store.PutTestCase(new ContinuousTestCase(
                    Id: caseId,
                    WorkspaceId: _workspaceId,
                    Name: caseId,
                    QualifiedName: caseId,
                    Selector: caseId,
                    FilePath: "tests/Suite.cs",
                    Framework: "xunit",
                    Metadata: new Dictionary<string, object?> { ["ct_project_path"] = projectPath }));
                store.StartContinuousTestRun(
                    new ContinuousTestRun(
                        Id: runId,
                        WorkspaceId: _workspaceId,
                        Status: "running",
                        SelectedRevision: revisionText,
                        IndexIdentity: identity,
                        Revision: revision),
                    [caseId]);
                store.CompleteContinuousTestRun(new ContinuousTestRunCompletion(
                    WorkspaceId: _workspaceId,
                    TestRunId: runId,
                    SelectedRevision: revisionText,
                    CurrentRevision: revisionText,
                    IndexIdentity: identity,
                    Revision: revision,
                    Status: resultStatus,
                    EndedAt: RunEndedAt,
                    Results:
                    [
                        new ContinuousTestResult(
                            Id: runId + ":" + caseId,
                            WorkspaceId: _workspaceId,
                            TestCaseId: caseId,
                            TestRunId: runId,
                            Status: resultStatus,
                            ResultRevision: revisionText,
                            IndexIdentity: identity,
                            Revision: revision,
                            FailureSummary: resultStatus == "failed" ? "boom " + caseId : null),
                    ]));
            }
        });
    }
}
