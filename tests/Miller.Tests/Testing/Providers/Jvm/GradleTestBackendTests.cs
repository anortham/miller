using Miller.Testing;
using Miller.Testing.Providers.Jvm;
using Xunit;

namespace Miller.Tests.Testing.Providers.Jvm;

public sealed class GradleTestBackendTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("miller-ct-gradle-backend-").FullName;

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
    public void Discovery_prefers_a_wrapper_beside_the_build_file_and_keeps_all_gradle_paths_in_generation()
    {
        string project = Path.Combine(_root, "module");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "build.gradle"), "plugins { id 'java' }");
        string wrapper = Path.Combine(project, OperatingSystem.IsWindows() ? "gradlew.bat" : "gradlew");
        File.WriteAllText(wrapper, "wrapper");
        ContinuousTestWorkspace workspace = Workspace(Path.Combine(project, "build.gradle"));
        CtGenerationPaths paths = CtGenerationPaths.Allocate(workspace);

        TestProcessCommand command = new GradleTestBackend(new RecordingRunner()).BuildDiscoveryCommand(
            workspace,
            paths);

        Assert.Equal(Path.GetFullPath(wrapper), command.FileName);
        Assert.Equal(project, command.WorkingDirectory);
        Assert.Equal("-p", command.Arguments[command.Arguments.ToList().IndexOf("-p")]);
        Assert.Equal(project, ArgumentAfter(command, "-p"));
        Assert.Contains("--test-dry-run", command.Arguments);
        string initScript = ArgumentAfter(command, "--init-script");
        Assert.Contains("projectsEvaluated", File.ReadAllText(initScript), StringComparison.Ordinal);
        Assert.All(
            command.Environment.Values.Where(value => value is not null),
            value => Assert.True(IsInside(paths.GenerationRoot, value!), value));
        Assert.All(
            command.Arguments.Where(argument => argument.Contains("gradle", StringComparison.OrdinalIgnoreCase)),
            argument => Assert.DoesNotContain(Path.Combine(project, "build"), argument, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Run_uses_gradle_from_path_when_no_wrapper_exists_and_keeps_selection_filters()
    {
        string projectFile = Path.Combine(_root, "build.gradle.kts");
        File.WriteAllText(projectFile, "plugins { java }");
        ContinuousTestWorkspace workspace = Workspace(projectFile);
        CtGenerationPaths paths = CtGenerationPaths.Allocate(workspace);
        string first = JvmTestTooling.EncodeCaseId(
            workspace.WorkspaceId,
            workspace.ProjectPath,
            "gradle",
            "com.example.CalculatorTest",
            "adds");
        string second = JvmTestTooling.EncodeCaseId(
            workspace.WorkspaceId,
            workspace.ProjectPath,
            "gradle",
            "com.example.CalculatorTest",
            "subtracts");

        IReadOnlyList<TestProcessCommand> commands = new GradleTestBackend(new RecordingRunner())
            .BuildRunCommands(
                Request(workspace, first, second),
                paths,
                [
                    new JvmTestSelection("com.example.CalculatorTest", "adds", "com.example.CalculatorTest.adds"),
                    new JvmTestSelection("com.example.CalculatorTest", "subtracts", "com.example.CalculatorTest.subtracts"),
                ],
                wholeSuite: false);

        TestProcessCommand command = Assert.Single(commands);
        Assert.Equal("gradle", command.FileName);
        Assert.Equal(projectFile[..projectFile.LastIndexOf(Path.DirectorySeparatorChar)], command.WorkingDirectory);
        Assert.Equal(
            ["com.example.CalculatorTest.adds", "com.example.CalculatorTest.subtracts"],
            TestsArguments(command));
        Assert.DoesNotContain(command.Arguments, argument =>
            argument.Contains(Path.Combine(_root, "build"), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Discover_parses_all_generation_reports_and_keeps_dry_run_cases_runnable()
    {
        string projectFile = Path.Combine(_root, "build.gradle");
        File.WriteAllText(projectFile, "plugins { id 'java' }");
        ContinuousTestWorkspace workspace = Workspace(projectFile);
        var runner = new RecordingRunner(command =>
        {
            string reportRoot = command.Environment["MILLER_CT_GRADLE_BUILD_ROOT"]!;
            string report = Path.Combine(reportRoot, "root", "test-results", "test", "TEST-Calculator.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, DryRunReport);
            return new TestProcessResult(0, "dry run", string.Empty);
        });

        IReadOnlyList<JvmTestBackendCase> cases = await new GradleTestBackend(runner).DiscoverAsync(
            workspace,
            CtGenerationPaths.Allocate(workspace),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, cases.Count);
        Assert.Equal(["com.example.CalculatorTest.adds", "com.example.CalculatorTest.skipped"],
            cases.Select(test => test.Selector).ToArray());
        Assert.NotNull(cases[1].Metadata);
        Assert.Equal("skipped", cases[1].Metadata!["status"]);
        Assert.All(runner.Calls, command => Assert.Contains("--test-dry-run", command.Arguments));
    }

    [Fact]
    public async Task Run_rejects_duplicate_rows_and_aggregate_mismatches_in_generation_reports()
    {
        string projectFile = Path.Combine(_root, "build.gradle");
        File.WriteAllText(projectFile, "plugins { id 'java' }");
        ContinuousTestWorkspace workspace = Workspace(projectFile);
        var runner = new RecordingRunner(command =>
        {
            string reportRoot = command.Environment["MILLER_CT_GRADLE_BUILD_ROOT"]!;
            string first = Path.Combine(reportRoot, "root", "test-results", "test", "TEST-one.xml");
            string second = Path.Combine(reportRoot, "root", "test-results", "test", "TEST-two.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(first)!);
            File.WriteAllText(first, RunReport);
            File.WriteAllText(second, RunReport);
            return new TestProcessResult(0, string.Empty, string.Empty);
        });
        string id = JvmTestTooling.EncodeCaseId(
            workspace.WorkspaceId,
            workspace.ProjectPath,
            "gradle",
            "com.example.CalculatorTest",
            "adds");

        ContinuousTestProviderException duplicate = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            new GradleTestBackend(runner).RunAsync(
                Request(workspace, id),
                CtGenerationPaths.Allocate(workspace),
                [new JvmTestSelection("com.example.CalculatorTest", "adds", "com.example.CalculatorTest.adds")],
                wholeSuite: false,
                TestContext.Current.CancellationToken));
        Assert.Contains("duplicate", duplicate.Message, StringComparison.OrdinalIgnoreCase);

        var mismatchRunner = new RecordingRunner(command =>
        {
            string reportRoot = command.Environment["MILLER_CT_GRADLE_BUILD_ROOT"]!;
            string report = Path.Combine(reportRoot, "root", "test-results", "test", "TEST-one.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, MismatchReport);
            return new TestProcessResult(0, string.Empty, string.Empty);
        });
        ContinuousTestProviderException mismatch = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            new GradleTestBackend(mismatchRunner).RunAsync(
                Request(workspace, id),
                CtGenerationPaths.Allocate(workspace),
                [new JvmTestSelection("com.example.CalculatorTest", "adds", "com.example.CalculatorTest.adds")],
                wholeSuite: false,
                TestContext.Current.CancellationToken));
        Assert.Contains("aggregate", mismatch.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Discover_rejects_a_malformed_generation_report()
    {
        string projectFile = Path.Combine(_root, "build.gradle");
        File.WriteAllText(projectFile, "plugins { id 'java' }");
        ContinuousTestWorkspace workspace = Workspace(projectFile);
        var runner = new RecordingRunner(command =>
        {
            string reportRoot = command.Environment["MILLER_CT_GRADLE_BUILD_ROOT"]!;
            string report = Path.Combine(reportRoot, "root", "test-results", "test", "TEST-bad.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, "<testsuite><testcase");
            return new TestProcessResult(0, string.Empty, string.Empty);
        });

        ContinuousTestProviderException exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            new GradleTestBackend(runner).DiscoverAsync(
                workspace,
                CtGenerationPaths.Allocate(workspace),
                TestContext.Current.CancellationToken));

        Assert.Contains("unreadable", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Discover_rejects_a_nonzero_dry_run_even_when_a_report_exists()
    {
        string projectFile = Path.Combine(_root, "build.gradle");
        File.WriteAllText(projectFile, "plugins { id 'java' }");
        ContinuousTestWorkspace workspace = Workspace(projectFile);
        var runner = new RecordingRunner(command =>
        {
            string reportRoot = command.Environment["MILLER_CT_GRADLE_BUILD_ROOT"]!;
            string report = Path.Combine(reportRoot, "root", "test-results", "test", "TEST-failed.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, RunReport);
            return new TestProcessResult(1, string.Empty, "Gradle failed");
        });

        ContinuousTestProviderException exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            new GradleTestBackend(runner).DiscoverAsync(
                workspace,
                CtGenerationPaths.Allocate(workspace),
                TestContext.Current.CancellationToken));

        Assert.Contains("exited with code 1", exception.Message, StringComparison.Ordinal);
    }

    private ContinuousTestWorkspace Workspace(string projectPath) =>
        new(
            WorkspaceId: "ws:gradle",
            WorkspaceRoot: _root,
            ProjectPath: projectPath,
            BuildOutputRoot: Path.Combine(_root, ".miller", "ct-gradle"),
            Framework: "gradle");

    private static ContinuousTestProviderRunRequest Request(
        ContinuousTestWorkspace workspace,
        params string[] ids) =>
        new(
            Workspace: workspace,
            SelectedRevision: "rev-gradle",
            IndexIdentity: "store:gradle",
            TestCaseIds: ids);

    private static string ArgumentAfter(TestProcessCommand command, string argument)
    {
        int index = command.Arguments.ToList().IndexOf(argument);
        return command.Arguments[index + 1];
    }

    private static IReadOnlyList<string> TestsArguments(TestProcessCommand command)
    {
        var values = new List<string>();
        for (int index = 0; index < command.Arguments.Count; index++)
        {
            if (command.Arguments[index] == "--tests")
                values.Add(command.Arguments[++index]);
        }

        return values;
    }

    private static bool IsInside(string root, string candidate)
    {
        string rootWithSeparator = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(candidate);
        return full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase)
            || string.Equals(full, rootWithSeparator.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    private const string DryRunReport = """
        <testsuite name="com.example.CalculatorTest" tests="2" failures="0" errors="0" skipped="1">
          <testcase classname="com.example.CalculatorTest" name="adds" time="0" />
          <testcase classname="com.example.CalculatorTest" name="skipped" time="0"><skipped /></testcase>
        </testsuite>
        """;

    private const string RunReport = """
        <testsuite name="com.example.CalculatorTest" tests="1" failures="0" errors="0" skipped="0">
          <testcase classname="com.example.CalculatorTest" name="adds" time="0.01" />
        </testsuite>
        """;

    private const string MismatchReport = """
        <testsuite name="com.example.CalculatorTest" tests="2" failures="0" errors="0" skipped="0">
          <testcase classname="com.example.CalculatorTest" name="adds" time="0.01" />
        </testsuite>
        """;

    private sealed class RecordingRunner : ITestProcessRunner
    {
        private readonly Func<TestProcessCommand, TestProcessResult> _handler;

        public RecordingRunner(Func<TestProcessCommand, TestProcessResult>? handler = null) =>
            _handler = handler ?? (_ => new TestProcessResult(0, string.Empty, string.Empty));

        public List<TestProcessCommand> Calls { get; } = [];

        public Task<TestProcessResult> RunAsync(
            TestProcessCommand command,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(command);
            return Task.FromResult(_handler(command));
        }
    }
}
