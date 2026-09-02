using Miller.Testing;
using Miller.Testing.Providers.Jvm;
using Xunit;

namespace Miller.Tests.Testing.Providers.Jvm;

public sealed class MavenTestBackendTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("miller-ct-maven-backend-").FullName;

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
    public void Discovery_prefers_module_wrapper_then_workspace_wrapper_then_path()
    {
        string module = Path.Combine(_root, "module");
        Directory.CreateDirectory(module);
        string project = Path.Combine(module, "pom.xml");
        File.WriteAllText(project, "<project />");
        string moduleWrapper = Path.Combine(module, OperatingSystem.IsWindows() ? "mvnw.cmd" : "mvnw");
        string workspaceWrapper = Path.Combine(_root, OperatingSystem.IsWindows() ? "mvnw.cmd" : "mvnw");
        File.WriteAllText(moduleWrapper, "wrapper");
        File.WriteAllText(workspaceWrapper, "wrapper");
        ContinuousTestWorkspace workspace = Workspace(project);
        CtGenerationPaths paths = CtGenerationPaths.Allocate(workspace);

        TestProcessCommand command = new MavenTestBackend(new RecordingRunner()).BuildDiscoveryCommand(
            workspace,
            paths);

        Assert.Equal(Path.GetFullPath(moduleWrapper), command.FileName);
        Assert.Equal(module, command.WorkingDirectory);
        Assert.Contains("-q", command.Arguments);
        Assert.Contains("test-compile", command.Arguments);
        Assert.Equal(Path.GetFullPath(project), ArgumentAfter(command, "-f"));
        AssertEnvironmentOwned(command, paths);
        Assert.DoesNotContain(command.Arguments, argument =>
            argument.Contains("target", StringComparison.OrdinalIgnoreCase));

        File.Delete(moduleWrapper);
        command = new MavenTestBackend(new RecordingRunner()).BuildDiscoveryCommand(workspace, paths);
        Assert.Equal(Path.GetFullPath(workspaceWrapper), command.FileName);

        File.Delete(workspaceWrapper);
        command = new MavenTestBackend(new RecordingRunner()).BuildDiscoveryCommand(workspace, paths);
        Assert.Equal(OperatingSystem.IsWindows() ? "mvn.cmd" : "mvn", command.FileName);
    }

    [Fact]
    public async Task Discover_scans_surefire_default_class_patterns_and_excludes_inner_classes()
    {
        string project = Path.Combine(_root, "pom.xml");
        File.WriteAllText(project, "<project />");
        ContinuousTestWorkspace workspace = Workspace(project);
        var runner = new RecordingRunner(_ =>
        {
            string classes = TestClasses(CapturedPaths);
            Directory.CreateDirectory(Path.Combine(classes, "sample"));
            foreach (string name in new[]
            {
                "TestAlpha.class",
                "AlphaTest.class",
                "AlphaTests.class",
                "AlphaTestCase.class",
                "Alpha.class",
                "AlphaTest$Nested.class",
            })
            {
                File.WriteAllBytes(Path.Combine(classes, "sample", name), []);
            }

            return new TestProcessResult(0, string.Empty, string.Empty);
        });
        CtGenerationPaths paths = CtGenerationPaths.Allocate(workspace);
        CapturedPaths = paths;

        IReadOnlyList<JvmTestBackendCase> cases = await new MavenTestBackend(runner).DiscoverAsync(
            workspace,
            paths,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                "sample.AlphaTest",
                "sample.AlphaTestCase",
                "sample.AlphaTests",
                "sample.TestAlpha",
            ],
            cases.Select(test => test.ClassName).ToArray());
        Assert.All(cases, test => Assert.Equal(MavenTestBackend.ClassCaseSentinel, test.MethodName));
        Assert.All(cases, test => Assert.Equal(test.ClassName, test.DisplayName));
        Assert.All(cases, test => Assert.Equal(test.ClassName, test.Selector));
        Assert.Single(runner.Calls);
        Assert.Contains("test-compile", runner.Calls[0].Arguments);
    }

    [Fact]
    public void BuildRunCommands_chunks_class_selection_and_never_emits_the_internal_sentinel()
    {
        string project = Path.Combine(_root, "pom.xml");
        File.WriteAllText(project, "<project />");
        ContinuousTestWorkspace workspace = Workspace(project);
        CtGenerationPaths paths = CtGenerationPaths.Allocate(workspace);
        var selected = Enumerable.Range(0, 121)
            .Select(index => new JvmTestSelection(
                $"sample.Class{index}",
                MavenTestBackend.ClassCaseSentinel,
                $"sample.Class{index}"))
            .ToArray();

        IReadOnlyList<TestProcessCommand> commands = new MavenTestBackend(new RecordingRunner()).BuildRunCommands(
            Request(workspace),
            paths,
            selected,
            wholeSuite: false);

        Assert.Equal(2, commands.Count);
        string[] classes = commands.SelectMany(command =>
                command.Arguments
                    .Where(argument => argument.StartsWith("-Dtest=", StringComparison.Ordinal))
                    .SelectMany(argument => argument["-Dtest=".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries)))
            .ToArray();
        Assert.Equal(selected.Length, classes.Length);
        Assert.Equal(selected.Select(test => test.ClassName), classes);
        Assert.DoesNotContain(commands.SelectMany(command => command.Arguments), argument =>
            argument.Contains(MavenTestBackend.ClassCaseSentinel, StringComparison.Ordinal));
        Assert.All(commands, command => AssertEnvironmentOwned(command, paths));
    }

    [Fact]
    public void BuildRunCommands_for_whole_suite_omits_the_class_filter()
    {
        string project = Path.Combine(_root, "pom.xml");
        File.WriteAllText(project, "<project />");
        ContinuousTestWorkspace workspace = Workspace(project);
        CtGenerationPaths paths = CtGenerationPaths.Allocate(workspace);

        IReadOnlyList<TestProcessCommand> commands = new MavenTestBackend(new RecordingRunner()).BuildRunCommands(
            Request(workspace),
            paths,
            [new JvmTestSelection("sample.Class", MavenTestBackend.ClassCaseSentinel, "sample.Class")],
            wholeSuite: true);

        Assert.Single(commands);
        Assert.Contains("test", commands[0].Arguments);
        Assert.DoesNotContain(commands[0].Arguments, argument =>
            argument.StartsWith("-Dtest=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_aggregates_methods_to_class_verdict_duration_and_joined_failures()
    {
        string project = Path.Combine(_root, "pom.xml");
        File.WriteAllText(project, "<project />");
        ContinuousTestWorkspace workspace = Workspace(project);
        var runner = new RecordingRunner(_ =>
        {
            string reports = Reports(CapturedPaths);
            Directory.CreateDirectory(reports);
            File.WriteAllText(Path.Combine(reports, "TEST-sample.xml"), """
                <testsuite name="maven" tests="5" failures="2" errors="0" skipped="1">
                  <testcase classname="sample.PassTest" name="first" time="0.10" />
                  <testcase classname="sample.PassTest" name="second" time="0.20" />
                  <testcase classname="sample.FailTest" name="first" time="0.30"><failure message="assertion">first failure</failure></testcase>
                  <testcase classname="sample.FailTest" name="second" time="0.40"><failure message="comparison">second failure</failure></testcase>
                  <testcase classname="sample.SkipTest" name="only" time="0.50"><skipped /></testcase>
                </testsuite>
                """);
            return new TestProcessResult(1, string.Empty, "tests failed");
        });
        CtGenerationPaths paths = CtGenerationPaths.Allocate(workspace);
        CapturedPaths = paths;
        JvmTestSelection[] selected =
        [
            new("sample.PassTest", MavenTestBackend.ClassCaseSentinel, "sample.PassTest"),
            new("sample.FailTest", MavenTestBackend.ClassCaseSentinel, "sample.FailTest"),
            new("sample.SkipTest", MavenTestBackend.ClassCaseSentinel, "sample.SkipTest"),
        ];

        JvmTestBackendRunResult result = await new MavenTestBackend(runner).RunAsync(
            Request(workspace),
            paths,
            selected,
            wholeSuite: false,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(3, result.Cases.Count);
        JvmTestBackendCaseResult passed = Assert.Single(result.Cases, test => test.ClassName == "sample.PassTest");
        Assert.Equal("passed", passed.Status);
        Assert.Equal(0.30, passed.DurationSeconds!.Value, precision: 6);
        JvmTestBackendCaseResult failed = Assert.Single(result.Cases, test => test.ClassName == "sample.FailTest");
        Assert.Equal("failed", failed.Status);
        Assert.Contains("first failure", failed.FailureText, StringComparison.Ordinal);
        Assert.Contains("second failure", failed.FailureText, StringComparison.Ordinal);
        Assert.Equal(0.70, failed.DurationSeconds!.Value, precision: 6);
        JvmTestBackendCaseResult skipped = Assert.Single(result.Cases, test => test.ClassName == "sample.SkipTest");
        Assert.Equal("skipped", skipped.Status);
        Assert.Equal(0.50, skipped.DurationSeconds!.Value, precision: 6);
        Assert.All(result.Cases, test => Assert.Equal(test.ClassName, test.Selector));
    }

    [Fact]
    public async Task Run_rejects_reports_that_do_not_match_the_partial_selection()
    {
        string project = Path.Combine(_root, "pom.xml");
        File.WriteAllText(project, "<project />");
        ContinuousTestWorkspace workspace = Workspace(project);
        var runner = new RecordingRunner(_ =>
        {
            string reports = Reports(CapturedPaths);
            Directory.CreateDirectory(reports);
            File.WriteAllText(Path.Combine(reports, "TEST-sample.xml"), """
                <testsuite name="maven" tests="1" failures="0" errors="0" skipped="0">
                  <testcase classname="sample.OtherTest" name="only" />
                </testsuite>
                """);
            return new TestProcessResult(0, string.Empty, string.Empty);
        });
        CtGenerationPaths paths = CtGenerationPaths.Allocate(workspace);
        CapturedPaths = paths;

        ContinuousTestProviderException exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            new MavenTestBackend(runner).RunAsync(
                Request(workspace),
                paths,
                [new JvmTestSelection("sample.SelectedTest", MavenTestBackend.ClassCaseSentinel, "sample.SelectedTest")],
                wholeSuite: false,
                TestContext.Current.CancellationToken));

        Assert.Contains("unselected", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private CtGenerationPaths CapturedPaths { get; set; } = null!;

    private ContinuousTestWorkspace Workspace(string projectPath) =>
        new(
            WorkspaceId: "ws:maven",
            WorkspaceRoot: _root,
            ProjectPath: projectPath,
            BuildOutputRoot: Path.Combine(_root, ".miller", "ct-maven"),
            Framework: "maven");

    private static ContinuousTestProviderRunRequest Request(ContinuousTestWorkspace workspace) =>
        new(
            Workspace: workspace,
            SelectedRevision: "rev-maven",
            IndexIdentity: "store:maven",
            TestCaseIds: ["maven-test"]);

    private static string TestClasses(CtGenerationPaths paths) =>
        Path.Combine(paths.GenerationRoot, "maven-build", "test-classes");

    private static string Reports(CtGenerationPaths paths) =>
        Path.Combine(paths.GenerationRoot, "maven-build", "surefire-reports");

    private static string ArgumentAfter(TestProcessCommand command, string argument)
    {
        int index = command.Arguments.ToList().IndexOf(argument);
        return command.Arguments[index + 1];
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

    private static bool IsOwnedPath(CtGenerationPaths paths, string candidate) =>
        IsInside(paths.GenerationRoot, candidate)
        || IsInside(paths.TempDirectory, candidate);

    private static void AssertEnvironmentOwned(TestProcessCommand command, CtGenerationPaths paths)
    {
        foreach ((string key, string? value) in command.Environment)
        {
            if (value is null)
                continue;
            if (key == "MAVEN_OPTS")
                Assert.Contains(paths.TempDirectory, value, StringComparison.Ordinal);
            else
                Assert.True(IsOwnedPath(paths, value), value);
        }
    }

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
