using Miller.Testing;
using Miller.Testing.Providers.Jvm;
using Miller.Testing.Providers.Shared;
using Xunit;

namespace Miller.Tests.Testing.Providers.Jvm;

public sealed class JvmTestProviderTests : IDisposable
{
    private const string IndexIdentity = "store:jvm-identity";

    private readonly string _root =
        Directory.CreateTempSubdirectory("miller-ct-jvm-provider-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task Discover_returns_one_case_per_junit_method_with_owned_ids()
    {
        var backend = new RecordingBackend
        {
            DiscoveryCases =
            [
                new JvmTestBackendCase(
                    "com.example.CalculatorTest",
                    "adds",
                    "com.example.CalculatorTest.adds",
                    "tests/CalculatorTest.java"),
                new JvmTestBackendCase(
                    "com.example.CalculatorTest",
                    "subtracts",
                    "com.example.CalculatorTest.subtracts",
                    "tests/CalculatorTest.java"),
            ],
        };
        ContinuousTestWorkspace workspace = Workspace("gradle");

        IReadOnlyList<ProviderTestCase> cases = await new JvmTestProvider(backend).DiscoverAsync(
            workspace,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, cases.Count);
        Assert.Equal(
            ["com.example.CalculatorTest.adds", "com.example.CalculatorTest.subtracts"],
            cases.Select(test => test.Selector).ToArray());
        Assert.All(cases, test =>
        {
            Assert.StartsWith("jvm-test:", test.Id, StringComparison.Ordinal);
            Assert.Equal("gradle", test.Framework);
            Assert.Equal("jvm", test.Metadata["language_family"]);
            Assert.Equal("gradle", test.Metadata["backend"]);
            Assert.Equal(test.Metadata["class_name"], test.Metadata["class"]);
            Assert.Equal("tests/CalculatorTest.java", test.SourcePath);
        });
    }

    [Fact]
    public async Task Run_maps_junit_error_to_failed_and_preserves_partial_selection()
    {
        ContinuousTestWorkspace workspace = Workspace("gradle");
        var backend = new RecordingBackend
        {
            DiscoveryCases =
            [
                new JvmTestBackendCase("com.example.CalculatorTest", "adds", "com.example.CalculatorTest.adds", "tests/CalculatorTest.java"),
                new JvmTestBackendCase("com.example.CalculatorTest", "fails", "com.example.CalculatorTest.fails", "tests/CalculatorTest.java"),
            ],
            RunCases =
            [
                new JvmTestBackendCaseResult("com.example.CalculatorTest", "fails", "errored", 0.25, "setup failed"),
            ],
        };
        var provider = new JvmTestProvider(backend);
        IReadOnlyList<ProviderTestCase> discovered = await provider.DiscoverAsync(
            workspace,
            TestContext.Current.CancellationToken);
        ProviderTestCase selected = Assert.Single(discovered, test => test.Selector.EndsWith(".fails", StringComparison.Ordinal));

        ProviderRunResult result = await provider.RunAsync(
            Request(workspace, selected.Id),
            TestContext.Current.CancellationToken);

        ProviderCaseResult row = Assert.Single(result.CaseResults);
        Assert.Equal("failed", row.Status);
        Assert.Equal("setup failed", row.FailureSummary);
        Assert.Equal(0.25, row.DurationSeconds);
        Assert.Equal(IndexIdentity, row.IndexIdentity);
        Assert.Equal("rev-jvm", row.ResultRevision);
        Assert.Equal([selected.Selector], backend.LastSelected.Select(selection => selection.Selector).ToArray());
    }

    [Fact]
    public async Task Run_rejects_a_case_id_owned_by_another_workspace()
    {
        ContinuousTestWorkspace workspace = Workspace("gradle");
        string foreign = JvmTestTooling.EncodeCaseId(
            "ws:foreign",
            workspace.ProjectPath,
            "gradle",
            "com.example.CalculatorTest",
            "adds");

        await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            new JvmTestProvider(new RecordingBackend()).RunAsync(
                Request(workspace, foreign),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Run_rejects_an_unselected_report_case_on_a_partial_run()
    {
        ContinuousTestWorkspace workspace = Workspace("gradle");
        string selectedId = JvmTestTooling.EncodeCaseId(
            workspace.WorkspaceId,
            workspace.ProjectPath,
            "gradle",
            "com.example.CalculatorTest",
            "adds");
        var backend = new RecordingBackend
        {
            RunCases =
            [
                new JvmTestBackendCaseResult("com.example.CalculatorTest", "adds", "passed", 0.01, null),
                new JvmTestBackendCaseResult("com.example.CalculatorTest", "subtracts", "passed", 0.01, null),
            ],
        };

        ContinuousTestProviderException exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            new JvmTestProvider(backend).RunAsync(
                Request(workspace, selectedId),
                TestContext.Current.CancellationToken));

        Assert.Contains("unselected", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Run_rejects_empty_selection_unless_whole_suite_is_requested()
    {
        ContinuousTestWorkspace workspace = Workspace("gradle");
        var backend = new RecordingBackend
        {
            RunCases =
            [new JvmTestBackendCaseResult("com.example.CalculatorTest", "adds", "passed", 0.01, null)],
        };
        var provider = new JvmTestProvider(backend);

        ContinuousTestProviderException exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            provider.RunAsync(Request(workspace), TestContext.Current.CancellationToken));
        Assert.Contains("selected no test case IDs", exception.Message, StringComparison.Ordinal);

        ContinuousTestProviderException emptyWholeSuite = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            provider.RunAsync(
                Request(workspace) with { WholeSuite = true },
                TestContext.Current.CancellationToken));
        Assert.Contains("selected no test case IDs", emptyWholeSuite.Message, StringComparison.Ordinal);

        string id = JvmTestTooling.EncodeCaseId(
            workspace.WorkspaceId,
            workspace.ProjectPath,
            "gradle",
            "com.example.CalculatorTest",
            "adds");
        ProviderRunResult result = await provider.RunAsync(
            Request(workspace, id) with { WholeSuite = true },
            TestContext.Current.CancellationToken);
        Assert.True(backend.LastWholeSuite);
        Assert.Equal("passed", result.Status);
    }

    [Fact]
    public async Task Whole_suite_rejects_missing_selected_cases()
    {
        ContinuousTestWorkspace workspace = Workspace("gradle");
        string first = JvmTestTooling.EncodeCaseId(
            workspace.WorkspaceId, workspace.ProjectPath, "gradle", "com.example.CalculatorTest", "adds");
        string second = JvmTestTooling.EncodeCaseId(
            workspace.WorkspaceId, workspace.ProjectPath, "gradle", "com.example.CalculatorTest", "subtracts");
        var backend = new RecordingBackend
        {
            RunCases =
            [new JvmTestBackendCaseResult("com.example.CalculatorTest", "adds", "passed", 0.01, null)],
        };

        ContinuousTestProviderException exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            new JvmTestProvider(backend).RunAsync(
                Request(workspace, first, second) with { WholeSuite = true },
                TestContext.Current.CancellationToken));

        Assert.Contains("did not report selected", exception.Message, StringComparison.Ordinal);
        Assert.Contains("subtracts", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_fails_closed_for_an_unknown_backend_status()
    {
        ContinuousTestWorkspace workspace = Workspace("gradle");
        string id = JvmTestTooling.EncodeCaseId(
            workspace.WorkspaceId, workspace.ProjectPath, "gradle", "com.example.CalculatorTest", "adds");
        var backend = new RecordingBackend
        {
            RunCases =
            [new JvmTestBackendCaseResult("com.example.CalculatorTest", "adds", "future-status", 0.01, null)],
        };

        ProviderRunResult result = await new JvmTestProvider(backend).RunAsync(
            Request(workspace, id),
            TestContext.Current.CancellationToken);

        Assert.Equal("failed", Assert.Single(result.CaseResults).Status);
        Assert.Equal("future-status", Assert.Single(result.CaseResults).Metadata["raw_status"]);
    }

    [Fact]
    public void Build_run_commands_pass_selection_to_the_backend_and_whole_suite_omits_it()
    {
        ContinuousTestWorkspace workspace = Workspace("gradle");
        string id = JvmTestTooling.EncodeCaseId(
            workspace.WorkspaceId,
            workspace.ProjectPath,
            "gradle",
            "com.example.CalculatorTest",
            "adds");
        var backend = new RecordingBackend
        {
            Commands = [new TestProcessCommand("gradle", ["test"], _root)],
        };
        var provider = new JvmTestProvider(backend);

        IReadOnlyList<TestProcessCommand> selected = provider.BuildRunCommands(Request(workspace, id));
        Assert.Single(selected);
        Assert.Equal(["com.example.CalculatorTest.adds"], backend.LastSelected.Select(selection => selection.Selector).ToArray());

        provider.BuildRunCommands(Request(workspace, id) with { WholeSuite = true });
        Assert.True(backend.LastWholeSuite);
        Assert.Equal(["com.example.CalculatorTest.adds"], backend.LastSelected.Select(selection => selection.Selector).ToArray());
    }

    [Fact]
    public void IsJvmProjectFile_covers_gradle_maven_and_sbt_build_files()
    {
        Assert.True(JvmTestProvider.IsJvmProjectFile(Path.Combine(_root, "build.gradle")));
        Assert.True(JvmTestProvider.IsJvmProjectFile(Path.Combine(_root, "build.gradle.kts")));
        Assert.True(JvmTestProvider.IsJvmProjectFile(Path.Combine(_root, "pom.xml")));
        Assert.True(JvmTestProvider.IsJvmProjectFile(Path.Combine(_root, "build.sbt")));
        Assert.False(JvmTestProvider.IsJvmProjectFile(Path.Combine(_root, "settings.gradle")));
    }

    private ContinuousTestWorkspace Workspace(string framework) =>
        new(
            WorkspaceId: "ws:jvm",
            WorkspaceRoot: _root,
            ProjectPath: Path.Combine(_root, "build.gradle"),
            BuildOutputRoot: Path.Combine(_root, ".miller", "ct-jvm"),
            Framework: framework);

    private static ContinuousTestProviderRunRequest Request(
        ContinuousTestWorkspace workspace,
        params string[] ids) =>
        new(
            Workspace: workspace,
            SelectedRevision: "rev-jvm",
            IndexIdentity: IndexIdentity,
            RunId: "run:jvm",
            TestCaseIds: ids);

    private sealed class RecordingBackend : IJvmTestBackend
    {
        public string Discriminator => "gradle";

        public IReadOnlyList<JvmTestBackendCase> DiscoveryCases { get; init; } = [];

        public IReadOnlyList<JvmTestBackendCaseResult> RunCases { get; init; } = [];

        public IReadOnlyList<TestProcessCommand> Commands { get; init; } = [];

        public IReadOnlyList<JvmTestSelection> LastSelected { get; private set; } = [];

        public bool LastWholeSuite { get; private set; }

        public Task EnsureBuildAsync(
            ContinuousTestWorkspace workspace,
            CtGenerationPaths paths,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<JvmTestBackendCase>> DiscoverAsync(
            ContinuousTestWorkspace workspace,
            CtGenerationPaths paths,
            CancellationToken cancellationToken) =>
            Task.FromResult(DiscoveryCases);

        public Task<JvmTestBackendRunResult> RunAsync(
            ContinuousTestProviderRunRequest request,
            CtGenerationPaths paths,
            IReadOnlyList<JvmTestSelection> selected,
            bool wholeSuite,
            CancellationToken cancellationToken)
        {
            LastSelected = selected;
            LastWholeSuite = wholeSuite;
            return Task.FromResult(new JvmTestBackendRunResult(
                Path.Combine(paths.ResultsDirectory, "jvm.junit.xml"),
                RunCases));
        }

        public TestProcessCommand BuildDiscoveryCommand(
            ContinuousTestWorkspace workspace,
            CtGenerationPaths paths) =>
            Commands.Count > 0 ? Commands[0] : new TestProcessCommand("gradle", ["test"], workspace.WorkspaceRoot);

        public IReadOnlyList<TestProcessCommand> BuildRunCommands(
            ContinuousTestProviderRunRequest request,
            CtGenerationPaths paths,
            IReadOnlyList<JvmTestSelection> selected,
            bool wholeSuite)
        {
            LastSelected = selected;
            LastWholeSuite = wholeSuite;
            return Commands.Count > 0 ? Commands : [new TestProcessCommand("gradle", ["test"], request.Workspace.WorkspaceRoot)];
        }
    }
}
