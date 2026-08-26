using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Server;
using Miller.Server.Tools;
using Miller.Testing;
using Xunit;

namespace Miller.Tests.Server;

public sealed class TestsFailuresOutputTests : IDisposable
{
    private const string InfraAdviceLine =
        "often an environment difference under CT — verify with a plain provider run";

    private readonly string _dir;
    private readonly string _root;
    private readonly WorkspaceContext _workspace;

    public TestsFailuresOutputTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-tests-failures-" + Guid.NewGuid().ToString("N")[..10]);
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
    public void A_140_red_fixture_with_long_summaries_stays_under_the_tests_byte_cap()
    {
        SeedRedCases(140, index => "System.IO.DirectoryNotFoundException: " + new string('x', 700));

        string output = new TestsTool(_workspace).Tests(operation: "failures", format: "json", limit: 200);

        Assert.True(
            Encoding.UTF8.GetByteCount(output) <= ToolOutputBudget.TestsMcpMaxBytes,
            $"failures output is {Encoding.UTF8.GetByteCount(output)} bytes.");
        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement root = document.RootElement;
        int shown = root.GetProperty("failures").GetArrayLength();
        Assert.InRange(shown, 1, 139);
        Assert.Equal(140, root.GetProperty("total").GetInt32());
        Assert.Equal(140 - shown, root.GetProperty("truncated").GetInt32());
    }

    [Fact]
    public void A_bounded_compact_page_names_the_next_offset_after_shedding_rows()
    {
        SeedRedCases(140, index => "System.IO.DirectoryNotFoundException: " + new string('x', 700));

        string output = new TestsTool(_workspace).Tests(operation: "failures", limit: 200);

        Assert.True(Encoding.UTF8.GetByteCount(output) <= ToolOutputBudget.TestsMcpMaxBytes);
        string truncatedLine = output.Split('\n').Single(line => line.StartsWith("truncated:", StringComparison.Ordinal));
        Assert.Contains("(next: offset=", truncatedLine, StringComparison.Ordinal);
    }

    [Fact]
    public void A_page_under_the_byte_cap_is_byte_identical_to_the_core_render()
    {
        SeedRedCases(5, index => $"boom {index:D3}");

        string toolJson = new TestsTool(_workspace).Tests(operation: "failures", format: "json");
        string coreJson = TestsCore.Failures(CoreRequest()).Render(json: true);

        Assert.Equal(coreJson, toolJson);
    }

    [Fact]
    public void Project_filter_returns_only_that_projects_reds()
    {
        string alpha = WriteProjectFile("tests/Alpha.Tests/Alpha.Tests.csproj");
        string beta = WriteProjectFile("tests/Beta.Tests/Beta.Tests.csproj");
        SeedRedCases(3, index => "boom alpha", projectPath: alpha);
        SeedRedCases(5, index => "boom beta", caseOffset: 100, projectPath: beta);

        TestsFailuresResult result = TestsCore.Failures(
            CoreRequest() with { ProjectPath = "tests/Alpha.Tests/Alpha.Tests.csproj" });

        Assert.Equal(3, result.Total);
        Assert.Equal(3, result.Failures.Count);
        Assert.All(result.Failures, row => Assert.Equal("boom alpha", row.FailureSummary));
    }

    [Fact]
    public void A_missing_project_path_is_refused_not_answered_empty()
    {
        SeedRedCases(2, index => "boom");

        var exception = Assert.Throws<ToolDiagnosticException>(
            () => TestsCore.Failures(CoreRequest() with { ProjectPath = "tests/Nope/Nope.csproj" }));

        Assert.Contains("not found", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Group_error_class_separates_infra_shaped_rows_from_assertion_failures()
    {
        SeedRedCases(6, index => "System.IO.DirectoryNotFoundException: Could not find a part of the path '/x'");
        SeedRedCases(3, index => "Xunit.Sdk.EqualException: Assert.Equal() Failure", caseOffset: 100);
        SeedRedCases(2, index => "expected true but got false", caseOffset: 200);

        TestsFailureGroupsResult result = TestsCore.FailureGroups(CoreRequest());

        Assert.Equal(11, result.Total);
        Assert.Equal(3, result.Groups.Count);
        TestsFailureGroup infra = result.Groups.Single(
            group => group.ErrorClass == "System.IO.DirectoryNotFoundException");
        Assert.Equal(6, infra.Count);
        Assert.True(infra.InfraShaped);
        Assert.Equal("test:000", infra.SampleTestCaseId);
        TestsFailureGroup assertion = result.Groups.Single(group => group.ErrorClass == "Xunit.Sdk.EqualException");
        Assert.Equal(3, assertion.Count);
        Assert.False(assertion.InfraShaped);
        TestsFailureGroup unclassified = result.Groups.Single(group => group.ErrorClass == "unclassified");
        Assert.Equal(2, unclassified.Count);
        Assert.False(unclassified.InfraShaped);
    }

    [Fact]
    public void Group_json_replaces_failures_with_groups_and_carries_no_advice()
    {
        SeedRedCases(4, index => "System.IO.DirectoryNotFoundException: gone");

        string output = new TestsTool(_workspace).Tests(operation: "failures", format: "json", group: "error_class");

        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement root = document.RootElement;
        Assert.False(root.TryGetProperty("failures", out _));
        JsonElement group = root.GetProperty("groups")[0];
        Assert.Equal("System.IO.DirectoryNotFoundException", group.GetProperty("error_class").GetString());
        Assert.Equal(4, group.GetProperty("count").GetInt32());
        Assert.True(group.GetProperty("infra_shaped").GetBoolean());
        Assert.Equal("test:000", group.GetProperty("sample").GetProperty("test_case_id").GetString());
        Assert.Equal(4, root.GetProperty("total").GetInt32());
        Assert.DoesNotContain(InfraAdviceLine, output, StringComparison.Ordinal);
    }

    [Fact]
    public void Group_compact_appends_the_advice_line_only_for_infra_shaped_groups()
    {
        SeedRedCases(2, index => "System.IO.DirectoryNotFoundException: gone");
        SeedRedCases(2, index => "Xunit.Sdk.EqualException: Assert.Equal() Failure", caseOffset: 100);

        string compact = TestsCore.FailureGroups(CoreRequest()).Render(json: false);

        string[] adviceLines = compact.Split('\n')
            .Where(line => line.Contains(InfraAdviceLine, StringComparison.Ordinal))
            .ToArray();
        Assert.Single(adviceLines);
        int infraAt = compact.IndexOf("System.IO.DirectoryNotFoundException", StringComparison.Ordinal);
        Assert.True(compact.IndexOf(InfraAdviceLine, StringComparison.Ordinal) > infraAt);
    }

    [Fact]
    public void A_group_argument_outside_failures_is_refused()
    {
        string output = new TestsTool(_workspace).Tests(operation: "status", group: "error_class");

        Assert.Contains("operation=failures", output, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_group_value_is_refused()
    {
        string output = new TestsTool(_workspace).Tests(operation: "failures", group: "bogus");

        Assert.Contains("error_class", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Failure_summaries_are_truncated_per_row_in_both_renderers()
    {
        var row = new ContinuousTestStatus(
            WorkspaceId: "workspace",
            TestCaseId: "xunit:Sample.Tests.Fails",
            State: ContinuousTestState.Red,
            IndexIdentity: "gen-2",
            Revision: 42,
            FailureSummary: new string('x', 1000));
        var result = new TestsFailuresResult([row], Truncated: 0, Total: 1);

        using JsonDocument document = JsonDocument.Parse(result.Render(json: true));
        string jsonSummary = document.RootElement.GetProperty("failures")[0]
            .GetProperty("failure_summary").GetString()!;
        Assert.True(Encoding.UTF8.GetByteCount(jsonSummary) <= TestsCore.FailureSummaryMaxBytes);
        Assert.EndsWith("…", jsonSummary, StringComparison.Ordinal);

        string compact = result.Render(json: false);
        Assert.Contains("…", compact, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 500), compact, StringComparison.Ordinal);
    }

    [Fact]
    public void A_short_failure_summary_passes_through_untouched()
    {
        var row = new ContinuousTestStatus(
            WorkspaceId: "workspace",
            TestCaseId: "xunit:Sample.Tests.Fails",
            State: ContinuousTestState.Red,
            IndexIdentity: "gen-2",
            Revision: 42,
            FailureSummary: "boom");
        var result = new TestsFailuresResult([row], Truncated: 0, Total: 1);

        Assert.Equal(
            "{\"failures\":[{\"test_case_id\":\"xunit:Sample.Tests.Fails\","
            + "\"state\":\"red\",\"index_identity\":\"gen-2\",\"revision\":42,"
            + "\"failure_summary\":\"boom\"}],\"truncated\":0,\"total\":1,\"offset\":0}",
            result.Render(json: true));
    }

    [Theory]
    [InlineData("System.IO.DirectoryNotFoundException: gone", "System.IO.DirectoryNotFoundException")]
    [InlineData("DirectoryNotFoundException: gone", "DirectoryNotFoundException")]
    [InlineData("Assert failed: expected 5", "unclassified")]
    [InlineData("expected true but got false", "unclassified")]
    [InlineData(null, "unclassified")]
    [InlineData("System..IO: broken segments", "unclassified")]
    public void Error_classes_derive_from_dotted_type_name_prefixes_only(string? summary, string expected) =>
        Assert.Equal(expected, TestsCore.DeriveErrorClass(summary));

    [Theory]
    [InlineData("System.IO.DirectoryNotFoundException", true)]
    [InlineData("System.IO.FileNotFoundException", true)]
    [InlineData("System.DllNotFoundException", true)]
    [InlineData("System.UnauthorizedAccessException", true)]
    [InlineData("IOException", true)]
    [InlineData("Xunit.Sdk.EqualException", false)]
    [InlineData("unclassified", false)]
    public void Infra_shaped_matches_on_the_last_dotted_segment(string errorClass, bool expected) =>
        Assert.Equal(expected, TestsCore.IsInfraShaped(errorClass));

    private TestsCoreRequest CoreRequest() =>
        new(
            WorkspaceRoot: _root,
            WorkspaceId: _workspace.WorkspaceId,
            MillerHome: Path.GetDirectoryName(_workspace.RegistryDbPath));

    private string WriteProjectFile(string relativePath)
    {
        string path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        return path;
    }

    private void SeedRedCases(
        int count,
        Func<int, string> summary,
        int caseOffset = 0,
        string? projectPath = null)
    {
        string workspaceId = _workspace.WorkspaceId ?? WorkspaceId.FromCanonicalRoot(_root);
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
                    WorkspaceId: workspaceId,
                    Name: caseId,
                    QualifiedName: caseId,
                    Selector: caseId,
                    FilePath: "tests/Suite.cs",
                    Framework: "xunit",
                    Metadata: projectPath is null
                        ? null
                        : new Dictionary<string, object?> { ["ct_project_path"] = projectPath }));
                store.StartContinuousTestRun(
                    new ContinuousTestRun(
                        Id: runId,
                        WorkspaceId: workspaceId,
                        Status: "running",
                        SelectedRevision: "1",
                        IndexIdentity: "store:failures",
                        Revision: 1),
                    [caseId]);
                store.CompleteContinuousTestRun(new ContinuousTestRunCompletion(
                    WorkspaceId: workspaceId,
                    TestRunId: runId,
                    SelectedRevision: "1",
                    CurrentRevision: "1",
                    IndexIdentity: "store:failures",
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
                            IndexIdentity: "store:failures",
                            Revision: 1,
                            FailureSummary: summary(index)),
                    ]));
            }
        });
    }
}
