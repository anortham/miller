using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Indexing.Testing;
using Miller.Server;
using Miller.Server.Tools;
using Miller.Testing;
using Miller.Tests.Testing.Selection;
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

    /// <summary>
    /// The MISMATCH half of the daemon build report. The lease has always recorded which build the
    /// daemon runs and nothing read it, so an upgraded Miller kept the old daemon and status called
    /// it healthy. Both renderers must name the older build, not merely flag a difference.
    /// </summary>
    [Fact]
    public void A_daemon_on_an_older_build_is_named_in_both_renderers()
    {
        var result = new TestsStatusResult(
            Enabled: true,
            KillSwitchOff: false,
            Projects: [],
            DaemonState: CtDaemonLifecycleState.Running,
            DaemonReason: "idle",
            Verdict: ContinuousTestVerdict.Green,
            Selected: null,
            StaleCount: 0,
            SelectedCount: 0,
            LastRun: null,
            BudgetHolder: null,
            DaemonVersion: CtDaemonVersion.Evaluate("1.13.0+bbb", "1.9.0+aaa"));

        using JsonDocument doc = JsonDocument.Parse(TestsCore.RenderStatusJson(result));
        JsonElement daemon = doc.RootElement.GetProperty("daemon");
        Assert.Equal("1.9.0+aaa", daemon.GetProperty("miller_version").GetString());
        Assert.Equal("daemon_older", daemon.GetProperty("version_match").GetString());
        Assert.True(daemon.GetProperty("version_mismatch").GetBoolean());
        Assert.Equal("1.13.0+bbb", doc.RootElement.GetProperty("miller_version").GetString());

        string compact = TestsCore.RenderStatusCompact(result);
        string line = compact.Split('\n').Single(row => row.StartsWith("daemon_build:", StringComparison.Ordinal));
        Assert.Contains("1.9.0+aaa", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The agreeing case writes no compact line. A daemon on this build is the normal state, and a
    /// line on every status read would train the reader to ignore the one that matters.
    /// </summary>
    [Fact]
    public void A_daemon_on_this_build_adds_no_compact_line()
    {
        var result = new TestsStatusResult(
            Enabled: true,
            KillSwitchOff: false,
            Projects: [],
            DaemonState: CtDaemonLifecycleState.Running,
            DaemonReason: "idle",
            Verdict: ContinuousTestVerdict.Green,
            Selected: null,
            StaleCount: 0,
            SelectedCount: 0,
            LastRun: null,
            BudgetHolder: null,
            DaemonVersion: CtDaemonVersion.Evaluate("1.13.0+bbb", "1.13.0+bbb"));

        Assert.DoesNotContain("daemon_build:", TestsCore.RenderStatusCompact(result), StringComparison.Ordinal);
    }

    /// <summary>
    /// Two builds of the SAME release — two worktrees of one repo on different commits — is a
    /// symmetric verdict: each reads the other as build_differs. Miller must report it and must NOT
    /// nudge either side to act, or both follow the nudge and take the daemon from each other in a
    /// loop, killing every suite in flight. Only a proven direction earns the hint.
    /// </summary>
    [Fact]
    public void A_symmetric_build_difference_is_reported_but_never_nudged()
    {
        CtDaemonVersionVerdict forward = CtDaemonVersion.Evaluate("1.13.0+aaa", "1.13.0+bbb");
        CtDaemonVersionVerdict backward = CtDaemonVersion.Evaluate("1.13.0+bbb", "1.13.0+aaa");

        Assert.Equal(CtDaemonVersionMatch.BuildDiffers, forward.Match);
        Assert.Equal(forward.Match, backward.Match);
        Assert.True(forward.Mismatch && backward.Mismatch, "a build difference must stay visible");

        string? compact = TestsTool.StatusHint(StatusWith(forward));
        Assert.DoesNotContain("replace the older daemon", compact ?? "", StringComparison.Ordinal);

        string? older = TestsTool.StatusHint(StatusWith(CtDaemonVersion.Evaluate("1.13.0+aaa", "1.9.0+zzz")));
        Assert.Contains("replace the older daemon", older ?? "", StringComparison.Ordinal);
    }

    private static TestsStatusResult StatusWith(CtDaemonVersionVerdict version) =>
        new(
            Enabled: true,
            KillSwitchOff: false,
            Projects: [],
            DaemonState: CtDaemonLifecycleState.Running,
            DaemonReason: "idle",
            Verdict: ContinuousTestVerdict.Green,
            Selected: null,
            StaleCount: 0,
            SelectedCount: 0,
            LastRun: null,
            BudgetHolder: null,
            DaemonVersion: version);

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
    /// The selected key must come from the LIVE index cursor. The old <c>SelectedFrom</c> derived it
    /// from the stored rows themselves, so rows uniformly committed at an old key judged themselves
    /// fresh and read green forever.
    /// </summary>
    [Fact]
    public void Status_rows_committed_at_an_old_key_with_a_newer_live_cursor_are_stale_never_green()
    {
        SeedCases(3, resultStatus: "passed", identity: "gen-1", revision: 41);

        TestsStatusResult status = TestsCore.Status(CoreRequest(FactsHooks("gen-1", 58)));

        Assert.Equal(ContinuousTestVerdict.Partial, status.Verdict);
        Assert.Equal(new CtFreshnessKey("gen-1", 58), status.Selected);
        Assert.Equal(3, status.StaleCount);
    }

    /// <summary>
    /// The old row-derived key flipped between consecutive reads (observed live: rev 32424 in one
    /// read, 32161 in the next). With no index writes between two reads, the reported key must not
    /// move — and it must be the live cursor, not either stored row key.
    /// </summary>
    [Fact]
    public void Status_two_consecutive_reads_with_no_index_writes_report_the_same_live_key()
    {
        SeedCases(2, resultStatus: "passed", identity: "gen-a", revision: 32161);
        SeedCases(2, resultStatus: "passed", identity: "gen-b", revision: 32424, caseOffset: 100);
        TestsCoreHooks hooks = FactsHooks("gen-live", 32500);

        TestsStatusResult first = TestsCore.Status(CoreRequest(hooks));
        TestsStatusResult second = TestsCore.Status(CoreRequest(hooks));

        Assert.Equal(new CtFreshnessKey("gen-live", 32500), first.Selected);
        Assert.Equal(first.Selected, second.Selected);
        Assert.Equal(first.Verdict, second.Verdict);
    }

    [Fact]
    public void Status_with_no_live_index_is_unknown_with_no_key_even_when_stored_rows_are_green()
    {
        SeedCases(2, resultStatus: "passed", identity: "gen-old", revision: 41);

        TestsStatusResult status = TestsCore.Status(CoreRequest());

        Assert.Equal(ContinuousTestVerdict.Unknown, status.Verdict);
        Assert.Null(status.Selected);
        Assert.Contains(
            "selected: none (no live index)",
            TestsCore.RenderStatusCompact(status),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Writes <paramref name="count"/> red cases through the real run-completion path, so the states come from
    /// the same code that produces them in production rather than from a hand-written row.
    /// </summary>
    private void SeedRedCases(int count) =>
        SeedCases(count, resultStatus: "failed", identity: "store:failures", revision: 1);

    private static TestsCoreHooks FactsHooks(string identity, long revision) =>
        new(OpenFacts: (_, _) => new FakeMillerFactSource
        {
            Current = new CtIndexCursor(identity, revision),
        });

    private void SeedCases(int count, string resultStatus, string identity, long revision, int caseOffset = 0)
    {
        string workspaceId = _workspace.WorkspaceId ?? WorkspaceId.FromCanonicalRoot(_root);
        string revisionText = revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        for (int offset = 0; offset < count; offset++)
        {
            int index = caseOffset + offset;
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
                    SelectedRevision: revisionText,
                    IndexIdentity: identity,
                    Revision: revision),
                [caseId]);
            store.CompleteContinuousTestRun(new ContinuousTestRunCompletion(
                WorkspaceId: workspaceId,
                TestRunId: runId,
                SelectedRevision: revisionText,
                CurrentRevision: revisionText,
                IndexIdentity: identity,
                Revision: revision,
                Status: resultStatus,
                Results:
                [
                    new ContinuousTestResult(
                        Id: runId + ":" + caseId,
                        WorkspaceId: workspaceId,
                        TestCaseId: caseId,
                        TestRunId: runId,
                        Status: resultStatus,
                        ResultRevision: revisionText,
                        IndexIdentity: identity,
                        Revision: revision,
                        FailureSummary: resultStatus == "failed" ? "boom " + caseId : null),
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
