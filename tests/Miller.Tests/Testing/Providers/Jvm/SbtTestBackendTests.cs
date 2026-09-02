using Miller.Testing;
using Miller.Testing.Providers.Jvm;
using Miller.Tests.Testing;
using Xunit;

namespace Miller.Tests.Testing.Providers.Jvm;

public sealed class SbtTestBackendTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("miller-ct-sbt-backend-").FullName;

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
    public async Task Discover_parses_single_line_defined_test_names_into_class_cases()
    {
        string project = Path.Combine(_root, "build.sbt");
        File.WriteAllText(project, "ThisBuild / scalaVersion := \"3.3.3\"");
        var runner = new RecordingRunner(ReadFixture("sbt-defined-test-names.txt"));
        ContinuousTestWorkspace workspace = Workspace(project);

        IReadOnlyList<JvmTestBackendCase> cases = await new SbtTestBackend(runner).DiscoverAsync(
            workspace,
            CtGenerationPaths.Allocate(workspace),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ["sample.CalculatorTest", "sample.FinanceTest"],
            cases.Select(test => test.ClassName).ToArray());
        Assert.All(cases, test =>
        {
            Assert.Equal(JvmTestBackendIds.ClassCaseSentinel, test.MethodName);
            Assert.Equal(test.ClassName, test.Selector);
            Assert.Equal(true, test.Metadata!["class_scope"]);
            Assert.Equal(1, test.Metadata["method_count"]);
        });
    }

    [Fact]
    public async Task Discover_parses_pretty_multi_project_defined_test_names()
    {
        string project = Path.Combine(_root, "build.sbt");
        File.WriteAllText(project, "lazy val core = project");
        var runner = new RecordingRunner(ReadFixture("sbt-defined-test-names-multiproject.txt"));
        ContinuousTestWorkspace workspace = Workspace(project);

        IReadOnlyList<JvmTestBackendCase> cases = await new SbtTestBackend(runner).DiscoverAsync(
            workspace,
            CtGenerationPaths.Allocate(workspace),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ["sample.AppTest", "sample.CoreTest"],
            cases.Select(test => test.ClassName).ToArray());
        Assert.Equal(1, cases.Single(test => test.ClassName == "sample.CoreTest").Metadata!["method_count"]);
        Assert.Equal(1, cases.Single(test => test.ClassName == "sample.AppTest").Metadata!["method_count"]);
    }

    [Fact]
    public async Task Discover_refuses_duplicate_class_names_across_sbt_projects()
    {
        string project = Path.Combine(_root, "build.sbt");
        File.WriteAllText(project, "lazy val core = project");
        var runner = new RecordingRunner("""
            [info] core / Test / definedTestNames
            [info] * sample.SharedTest
            [info] app / Test / definedTestNames
            [info] * sample.SharedTest
            """);
        ContinuousTestWorkspace workspace = Workspace(project);

        ContinuousTestProviderException exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            new SbtTestBackend(runner).DiscoverAsync(
                workspace,
                CtGenerationPaths.Allocate(workspace),
                TestContext.Current.CancellationToken));

        Assert.Contains("duplicate", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sample.SharedTest", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discover_accepts_an_unqualified_default_package_class()
    {
        string project = Path.Combine(_root, "build.sbt");
        File.WriteAllText(project, "lazy val core = project");
        var runner = new RecordingRunner("[info] List(DefaultPackageTest)\n");
        ContinuousTestWorkspace workspace = Workspace(project);

        IReadOnlyList<JvmTestBackendCase> cases = await new SbtTestBackend(runner).DiscoverAsync(
            workspace,
            CtGenerationPaths.Allocate(workspace),
            TestContext.Current.CancellationToken);

        JvmTestBackendCase test = Assert.Single(cases);
        Assert.Equal("DefaultPackageTest", test.ClassName);
        Assert.Equal(JvmTestBackendIds.ClassCaseSentinel, test.MethodName);
    }

    [Fact]
    public async Task Discover_advances_the_dependency_candidate_directory_activity_marker()
    {
        string project = Path.Combine(_root, "build.sbt");
        File.WriteAllText(project, "lazy val core = project");
        ContinuousTestWorkspace workspace = Workspace(project);
        SbtWorkspaceShadowResult sync = SbtWorkspaceShadow.Sync(workspace, CancellationToken.None);
        string marker = Path.Combine(sync.DependencyCandidateRoot, ".last-used");
        File.WriteAllText(marker, "old");
        DateTime oldActivity = DateTime.UtcNow.AddHours(-1);
        Directory.SetLastWriteTimeUtc(sync.DependencyCandidateRoot, oldActivity);

        await new SbtTestBackend(new RecordingRunner(ReadFixture("sbt-defined-test-names.txt"))).DiscoverAsync(
            workspace,
            CtGenerationPaths.Allocate(workspace),
            TestContext.Current.CancellationToken);

        Assert.True(
            Directory.GetLastWriteTimeUtc(sync.DependencyCandidateRoot) > oldActivity,
            $"dependency candidate directory activity did not advance from {oldActivity:O}");
    }

    [Fact]
    public async Task Discover_refuses_a_malformed_defined_test_names_list()
    {
        string project = Path.Combine(_root, "build.sbt");
        File.WriteAllText(project, "lazy val core = project");
        var runner = new RecordingRunner("""
            [info] List(sample.ValidTest.runs)
            [info] List(sample.BrokenTest.runs
            """);
        ContinuousTestWorkspace workspace = Workspace(project);

        ContinuousTestProviderException exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            new SbtTestBackend(runner).DiscoverAsync(
                workspace,
                CtGenerationPaths.Allocate(workspace),
                TestContext.Current.CancellationToken));

        Assert.Contains("malformed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Discover_resynchronizes_the_shadow_after_ensure_build()
    {
        string project = Path.Combine(_root, "build.sbt");
        File.WriteAllText(project, "old-value");
        ContinuousTestWorkspace workspace = Workspace(project);
        CtGenerationPaths paths = CtGenerationPaths.Allocate(workspace);
        var backend = new SbtTestBackend(new RecordingRunner(ReadFixture("sbt-defined-test-names.txt")));

        await backend.EnsureBuildAsync(workspace, paths, TestContext.Current.CancellationToken);
        File.WriteAllText(project, "new-value");
        await backend.DiscoverAsync(workspace, paths, TestContext.Current.CancellationToken);

        Assert.Equal("new-value", File.ReadAllText(Path.Combine(backend.LastSync!.ShadowRoot, "build.sbt")));
    }

    [Fact]
    public async Task Run_resynchronizes_the_shadow_before_starting_sbt()
    {
        string project = Path.Combine(_root, "build.sbt");
        File.WriteAllText(project, "old-value");
        ContinuousTestWorkspace workspace = Workspace(project);
        CtGenerationPaths paths = CtGenerationPaths.Allocate(workspace);
        var backend = new SbtTestBackend(new WritingRunner(command =>
        {
            Assert.Equal("new-value", File.ReadAllText(Path.Combine(command.WorkingDirectory, "build.sbt")));
            string report = Path.Combine(command.WorkingDirectory, "target", "test-reports", "result.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, """
                <testsuite name="sample.CalculatorTest" tests="1" failures="0" errors="0" skipped="0">
                  <testcase classname="sample.CalculatorTest" name="runs" />
                </testsuite>
                """);
            return new TestProcessResult(0, string.Empty, string.Empty);
        }));

        await backend.EnsureBuildAsync(workspace, paths, TestContext.Current.CancellationToken);
        File.WriteAllText(project, "new-value");
        JvmTestBackendRunResult result = await backend.RunAsync(
            Request(workspace, "sample.CalculatorTest"),
            paths,
            [new JvmTestSelection(
                "sample.CalculatorTest",
                JvmTestBackendIds.ClassCaseSentinel,
                "sample.CalculatorTest")],
            wholeSuite: false,
            TestContext.Current.CancellationToken);

        Assert.Equal("passed", Assert.Single(result.Cases).Status);
    }

    [Fact]
    public void BuildRunCommands_uses_one_whitespace_separated_testOnly_selection()
    {
        string project = Path.Combine(_root, "build.sbt");
        File.WriteAllText(project, "lazy val core = project");
        ContinuousTestWorkspace workspace = Workspace(project);
        CtGenerationPaths paths = CtGenerationPaths.Allocate(workspace);

        IReadOnlyList<TestProcessCommand> commands = new SbtTestBackend(new RecordingRunner(string.Empty))
            .BuildRunCommands(
                new ContinuousTestProviderRunRequest(
                    Workspace: workspace,
                    SelectedRevision: "rev-sbt",
                    IndexIdentity: "store:sbt",
                    TestCaseIds: ["ignored"]),
                paths,
                [
                    new JvmTestSelection("sample.FirstTest", JvmTestBackendIds.ClassCaseSentinel, "sample.FirstTest"),
                    new JvmTestSelection("sample.SecondTest", JvmTestBackendIds.ClassCaseSentinel, "sample.SecondTest"),
                ],
                wholeSuite: false);

        TestProcessCommand command = Assert.Single(commands);
        Assert.Contains("testOnly sample.FirstTest sample.SecondTest", command.Arguments);
        Assert.DoesNotContain("testOnly", command.Arguments);
    }

    [Fact]
    public void BuildDiscoveryCommand_prefers_the_mirrored_launcher_and_isolates_sbt_caches()
    {
        string project = Path.Combine(_root, "build.sbt");
        File.WriteAllText(project, "lazy val core = project");
        string launcher = Path.Combine(_root, OperatingSystem.IsWindows() ? "sbt.bat" : "sbt");
        File.WriteAllText(launcher, "launcher");
        ContinuousTestWorkspace workspace = Workspace(project);
        CtGenerationPaths paths = CtGenerationPaths.Allocate(workspace);
        SbtWorkspaceShadowResult sync = SbtWorkspaceShadow.Sync(workspace, CancellationToken.None);

        TestProcessCommand command = new SbtTestBackend(new RecordingRunner(string.Empty))
            .BuildDiscoveryCommand(workspace, paths);

        Assert.Equal(Path.Combine(sync.ShadowRoot, Path.GetFileName(launcher)), command.FileName);
        Assert.Equal(sync.ShadowRoot, command.WorkingDirectory);
        Assert.Contains("-batch", command.Arguments);
        Assert.Contains("-Dsbt.supershell=false", command.Arguments);
        Assert.Contains("-Dsbt.color=false", command.Arguments);
        Assert.Contains("-Dsbt.log.noformat=true", command.Arguments);
        Assert.Contains("-Dsbt.server.autostart=false", command.Arguments);
        Assert.Contains("-Dsbt.genbuildprops=false", command.Arguments);
        Assert.Contains(command.Arguments, argument => argument == "-Dsbt.boot.directory=" + Path.Combine(sync.DependencyCandidateRoot, "boot"));
        Assert.Contains(command.Arguments, argument => argument == "-Dsbt.global.base=" + Path.Combine(sync.DependencyCandidateRoot, "global"));
        Assert.Contains(command.Arguments, argument => argument == "-Dsbt.ivy.home=" + Path.Combine(sync.DependencyCandidateRoot, "ivy"));
        Assert.Contains(command.Arguments, argument => argument == "-Dsbt.coursier.home=" + Path.Combine(sync.DependencyCandidateRoot, "coursier"));
        Assert.Equal("show Test/definedTestNames", command.Arguments[^1]);
        Assert.DoesNotContain(command.Environment, pair => pair.Key is "SBT_OPTS" or "JAVA_OPTS");
        Assert.All(command.Environment.Values, value => Assert.True(value is not null && JvmTestTooling.IsInside(paths.TempDirectory, value), value));
    }

    [Fact]
    public async Task Discover_refuses_truncated_stdout()
    {
        string project = Path.Combine(_root, "build.sbt");
        File.WriteAllText(project, "lazy val core = project");
        ContinuousTestWorkspace workspace = Workspace(project);

        ContinuousTestProviderException exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            new SbtTestBackend(new TruncatedRunner(ReadFixture("sbt-defined-test-names.txt"))).DiscoverAsync(
                workspace,
                CtGenerationPaths.Allocate(workspace),
                TestContext.Current.CancellationToken));

        Assert.Contains("partial stream", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Discover_refuses_a_nonzero_sbt_exit()
    {
        string project = Path.Combine(_root, "build.sbt");
        File.WriteAllText(project, "lazy val core = project");
        ContinuousTestWorkspace workspace = Workspace(project);
        var runner = new WritingRunner(_ => new TestProcessResult(7, string.Empty, "sbt failed"));

        ContinuousTestProviderException exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            new SbtTestBackend(runner).DiscoverAsync(
                workspace,
                CtGenerationPaths.Allocate(workspace),
                TestContext.Current.CancellationToken));

        Assert.Contains("code 7", exception.Message, StringComparison.Ordinal);
        Assert.Contains("sbt failed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_copies_and_aggregates_contained_junit_reports_by_class()
    {
        string project = Path.Combine(_root, "build.sbt");
        File.WriteAllText(project, "lazy val core = project");
        var runner = new WritingRunner(command =>
        {
            string report = Path.Combine(
                command.WorkingDirectory,
                "target",
                "test-reports",
                "sample.CalculatorTest.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, """
                <testsuite name="sample.CalculatorTest" tests="2" failures="0" errors="0" skipped="0">
                  <testcase classname="sample.CalculatorTest" name="adds" time="0.01" />
                  <testcase classname="sample.CalculatorTest" name="subtracts" time="0.02" />
                </testsuite>
                """);
            return new TestProcessResult(0, string.Empty, string.Empty);
        });
        ContinuousTestWorkspace workspace = Workspace(project);
        CtGenerationPaths paths = CtGenerationPaths.Allocate(workspace);

        JvmTestBackendRunResult result = await new SbtTestBackend(runner).RunAsync(
            new ContinuousTestProviderRunRequest(
                Workspace: workspace,
                SelectedRevision: "rev-sbt",
                IndexIdentity: "store:sbt",
                TestCaseIds: [JvmTestTooling.EncodeCaseId(
                    workspace.WorkspaceId,
                    workspace.ProjectPath,
                    JvmTestBackendIds.Sbt,
                    "sample.CalculatorTest",
                    JvmTestBackendIds.ClassCaseSentinel)]),
            paths,
            [new JvmTestSelection(
                "sample.CalculatorTest",
                JvmTestBackendIds.ClassCaseSentinel,
                "sample.CalculatorTest")],
            wholeSuite: false,
            TestContext.Current.CancellationToken);

        JvmTestBackendCaseResult test = Assert.Single(result.Cases);
        Assert.Equal("sample.CalculatorTest", test.ClassName);
        Assert.Equal(JvmTestBackendIds.ClassCaseSentinel, test.MethodName);
        Assert.Equal("passed", test.Status);
        Assert.Equal(2, test.Metadata!["method_count"]);
        Assert.True(File.Exists(result.ResultArtifactPath));
        Assert.StartsWith(paths.ResultsDirectory, result.ResultArtifactPath, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(
            CtGenerationPaths.CacheDirectory(workspace, "sbt-deps"),
            ".last-used")));
    }

    [Fact]
    public async Task Run_refuses_a_malformed_or_empty_junit_report()
    {
        string project = Path.Combine(_root, "build.sbt");
        File.WriteAllText(project, "lazy val core = project");
        var runner = new WritingRunner(command =>
        {
            string report = Path.Combine(command.WorkingDirectory, "target", "test-reports", "bad.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, "<not-a-testsuite />");
            return new TestProcessResult(0, string.Empty, string.Empty);
        });
        ContinuousTestWorkspace workspace = Workspace(project);
        CtGenerationPaths paths = CtGenerationPaths.Allocate(workspace);

        ContinuousTestProviderException exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            new SbtTestBackend(runner).RunAsync(
                Request(workspace, "sample.CalculatorTest"),
                paths,
                [new JvmTestSelection(
                    "sample.CalculatorTest",
                    JvmTestBackendIds.ClassCaseSentinel,
                    "sample.CalculatorTest")],
                wholeSuite: false,
                TestContext.Current.CancellationToken));

        Assert.Contains("unreadable", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Run_preserves_subproject_paths_when_report_names_collide()
    {
        string project = Path.Combine(_root, "build.sbt");
        File.WriteAllText(project, "lazy val core = project");
        var runner = new WritingRunner(command =>
        {
            string reportRoot = Path.Combine(command.WorkingDirectory, "core", "target", "test-reports");
            Directory.CreateDirectory(reportRoot);
            File.WriteAllText(Path.Combine(reportRoot, "results.xml"), """
                <testsuite name="core" tests="1" failures="0" errors="0" skipped="0">
                  <testcase classname="sample.CoreTest" name="runs" />
                </testsuite>
                """);
            reportRoot = Path.Combine(command.WorkingDirectory, "app", "target", "test-reports");
            Directory.CreateDirectory(reportRoot);
            File.WriteAllText(Path.Combine(reportRoot, "results.xml"), """
                <testsuite name="app" tests="1" failures="0" errors="0" skipped="0">
                  <testcase classname="sample.AppTest" name="runs" />
                </testsuite>
                """);
            return new TestProcessResult(0, string.Empty, string.Empty);
        });
        ContinuousTestWorkspace workspace = Workspace(project);
        CtGenerationPaths paths = CtGenerationPaths.Allocate(workspace);

        JvmTestBackendRunResult result = await new SbtTestBackend(runner).RunAsync(
            Request(workspace, "sample.CoreTest"),
            paths,
            [
                new JvmTestSelection("sample.CoreTest", JvmTestBackendIds.ClassCaseSentinel, "sample.CoreTest"),
                new JvmTestSelection("sample.AppTest", JvmTestBackendIds.ClassCaseSentinel, "sample.AppTest"),
            ],
            wholeSuite: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Cases.Count);
        string resultRoot = Path.Combine(paths.ResultsDirectory, "sbt");
        Assert.True(File.Exists(Path.Combine(resultRoot, "core", "target", "test-reports", "results.xml")));
        Assert.True(File.Exists(Path.Combine(resultRoot, "app", "target", "test-reports", "results.xml")));
    }

    [Fact]
    public async Task Run_refuses_unexpected_and_missing_partial_classes()
    {
        string project = Path.Combine(_root, "build.sbt");
        File.WriteAllText(project, "lazy val core = project");
        var runner = new WritingRunner(command =>
        {
            string report = Path.Combine(command.WorkingDirectory, "target", "test-reports", "classes.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, """
                <testsuite name="classes" tests="1" failures="0" errors="0" skipped="0">
                  <testcase classname="sample.UnexpectedTest" name="runs" />
                </testsuite>
                """);
            return new TestProcessResult(0, string.Empty, string.Empty);
        });
        ContinuousTestWorkspace workspace = Workspace(project);
        CtGenerationPaths paths = CtGenerationPaths.Allocate(workspace);

        ContinuousTestProviderException exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            new SbtTestBackend(runner).RunAsync(
                Request(workspace, "sample.CalculatorTest"),
                paths,
                [new JvmTestSelection(
                    "sample.CalculatorTest",
                    JvmTestBackendIds.ClassCaseSentinel,
                    "sample.CalculatorTest")],
                wholeSuite: false,
                TestContext.Current.CancellationToken));

        Assert.Contains("unselected", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Run_clears_stale_shadow_reports_before_starting_sbt()
    {
        string project = Path.Combine(_root, "build.sbt");
        File.WriteAllText(project, "lazy val core = project");
        ContinuousTestWorkspace workspace = Workspace(project);
        CtGenerationPaths paths = CtGenerationPaths.Allocate(workspace);
        SbtWorkspaceShadowResult sync = SbtWorkspaceShadow.Sync(workspace, CancellationToken.None);
        string report = Path.Combine(sync.ShadowRoot, "target", "test-reports", "sample.CalculatorTest.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        File.WriteAllText(report, """
            <testsuite name="stale" tests="1" failures="1" errors="0" skipped="0">
              <testcase classname="sample.CalculatorTest" name="stale"><failure /></testcase>
            </testsuite>
            """);
        var runner = new WritingRunner(command =>
        {
            Assert.Empty(Directory.EnumerateFiles(
                Path.Combine(command.WorkingDirectory, "target", "test-reports"),
                "*.xml"));
            File.WriteAllText(report, """
                <testsuite name="fresh" tests="1" failures="0" errors="0" skipped="0">
                  <testcase classname="sample.CalculatorTest" name="fresh" />
                </testsuite>
                """);
            return new TestProcessResult(0, string.Empty, string.Empty);
        });

        JvmTestBackendRunResult result = await new SbtTestBackend(runner).RunAsync(
            Request(workspace, "sample.CalculatorTest"),
            paths,
            [new JvmTestSelection(
                "sample.CalculatorTest",
                JvmTestBackendIds.ClassCaseSentinel,
                "sample.CalculatorTest")],
            wholeSuite: false,
            TestContext.Current.CancellationToken);

        Assert.Equal("passed", Assert.Single(result.Cases).Status);
    }

    [Fact]
    public async Task Run_refuses_duplicate_classes_across_report_files()
    {
        string project = Path.Combine(_root, "build.sbt");
        File.WriteAllText(project, "lazy val core = project");
        var runner = new WritingRunner(command =>
        {
            string reportRoot = Path.Combine(command.WorkingDirectory, "target", "test-reports");
            Directory.CreateDirectory(reportRoot);
            File.WriteAllText(Path.Combine(reportRoot, "first.xml"), """
                <testsuite name="first" tests="1" failures="0" errors="0" skipped="0">
                  <testcase classname="sample.DuplicateTest" name="one" />
                </testsuite>
                """);
            File.WriteAllText(Path.Combine(reportRoot, "second.xml"), """
                <testsuite name="second" tests="1" failures="0" errors="0" skipped="0">
                  <testcase classname="sample.DuplicateTest" name="two" />
                </testsuite>
                """);
            return new TestProcessResult(0, string.Empty, string.Empty);
        });
        ContinuousTestWorkspace workspace = Workspace(project);

        ContinuousTestProviderException exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            new SbtTestBackend(runner).RunAsync(
                Request(workspace, "sample.DuplicateTest"),
                CtGenerationPaths.Allocate(workspace),
                [new JvmTestSelection(
                    "sample.DuplicateTest",
                    JvmTestBackendIds.ClassCaseSentinel,
                    "sample.DuplicateTest")],
                wholeSuite: false,
                TestContext.Current.CancellationToken));

        Assert.Contains("duplicate test class", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Run_ignores_an_internal_directory_link_alias_when_scanning_reports()
    {
        string project = Path.Combine(_root, "build.sbt");
        File.WriteAllText(project, "lazy val core = project");
        ContinuousTestWorkspace workspace = Workspace(project);
        SbtWorkspaceShadowResult sync = SbtWorkspaceShadow.Sync(workspace, CancellationToken.None);
        string target = Path.Combine(sync.ShadowRoot, "target");
        string reportRoot = Path.Combine(target, "test-reports");
        Directory.CreateDirectory(reportRoot);
        File.WriteAllText(Path.Combine(reportRoot, "sample.CalculatorTest.xml"), "stale");
        string alias = Path.Combine(sync.ShadowRoot, "target-alias");
        if (!TryCreateDirectoryLink(alias, target))
            Assert.Skip("Symbolic directory links are unavailable on this host.");

        var runner = new WritingRunner(command =>
        {
            string report = Path.Combine(command.WorkingDirectory, "target", "test-reports", "result.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, """
                <testsuite name="sample.CalculatorTest" tests="1" failures="0" errors="0" skipped="0">
                  <testcase classname="sample.CalculatorTest" name="runs" />
                </testsuite>
                """);
            return new TestProcessResult(0, string.Empty, string.Empty);
        });

        JvmTestBackendRunResult result = await new SbtTestBackend(runner).RunAsync(
            Request(workspace, "sample.CalculatorTest"),
            CtGenerationPaths.Allocate(workspace),
            [new JvmTestSelection(
                "sample.CalculatorTest",
                JvmTestBackendIds.ClassCaseSentinel,
                "sample.CalculatorTest")],
            wholeSuite: false,
            TestContext.Current.CancellationToken);

        Assert.Single(result.Cases);
    }

    private ContinuousTestWorkspace Workspace(string project) =>
        new(
            WorkspaceId: "ws:sbt",
            WorkspaceRoot: _root,
            ProjectPath: project,
            BuildOutputRoot: Path.Combine(_root, ".miller", "ct-sbt"),
            Framework: "sbt");

    private static ContinuousTestProviderRunRequest Request(
        ContinuousTestWorkspace workspace,
        string className) =>
        new(
            Workspace: workspace,
            SelectedRevision: "rev-sbt",
            IndexIdentity: "store:sbt",
            TestCaseIds: [JvmTestTooling.EncodeCaseId(
                workspace.WorkspaceId,
                workspace.ProjectPath,
                JvmTestBackendIds.Sbt,
                className,
                JvmTestBackendIds.ClassCaseSentinel)]);

    private static string ReadFixture(string fileName) =>
        File.ReadAllText(Path.Combine(
            ScaleTestSupport.RepoRoot(),
            "tests",
            "Miller.Tests",
            "Testing",
            "Providers",
            "Fixtures",
            fileName));

    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }

    private sealed class RecordingRunner(string output) : ITestProcessRunner
    {
        public Task<TestProcessResult> RunAsync(
            TestProcessCommand command,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TestProcessResult(0, output, string.Empty));
    }

    private sealed class WritingRunner(
        Func<TestProcessCommand, TestProcessResult> handler) : ITestProcessRunner
    {
        public Task<TestProcessResult> RunAsync(
            TestProcessCommand command,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(handler(command));
    }

    private sealed class TruncatedRunner(string output) : ITestProcessRunner
    {
        public Task<TestProcessResult> RunAsync(
            TestProcessCommand command,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TestProcessResult(0, output, string.Empty, StandardOutputTruncated: true));
    }
}
