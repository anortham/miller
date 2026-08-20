using System.Globalization;
using Miller.Testing;
using Miller.Tests.Testing.Providers.Dotnet;
using Xunit;

namespace Miller.Tests.Testing.Providers.Python;

public sealed class PythonTestProviderTests : IDisposable
{
    private const string IndexIdentity = "store:test-identity";

    private readonly string _dir =
        Directory.CreateTempSubdirectory("miller-ct-python-provider-tests-").FullName;

    private readonly HashSet<string> _ctTemps = new(StringComparer.Ordinal);

    private string ProjectRoot => Path.Combine(_dir, "project");

    public void Dispose()
    {
        BestEffortDelete(_dir);
        foreach (var temp in _ctTemps)
            BestEffortDelete(temp);
    }

    [Fact]
    public async Task Discover_returns_stable_file_level_cases_and_excludes_generated_dirs()
    {
        WriteProjectFile("pyproject.toml", "[project]\nname = \"sample\"\n");
        WriteProjectFile("tests/test_math.py", "def test_add():\n    assert 1 + 1 == 2\n");
        WriteProjectFile("tests/widgets_test.py", "def test_widget():\n    assert True\n");
        WriteProjectFile("tests/helpers.py", "def helper():\n    pass\n");
        WriteProjectFile(".venv/lib/python3.12/site-packages/test_hidden.py", "def test_hidden():\n    pass\n");
        WriteProjectFile(".pytest_cache/v/cache/test_cache.py", "def test_cache():\n    pass\n");
        WriteProjectFile(".worktrees/shadow/tests/test_shadow.py", "def test_shadow():\n    pass\n");
        WriteProjectFile(".claude/worktrees/shadow/tests/test_claude_shadow.py", "def test_claude_shadow():\n    pass\n");

        var provider = new PythonTestProvider(new FakeTestProcessRunner());

        var cases = await provider.DiscoverAsync(Workspace(framework: null), TestContext.Current.CancellationToken);

        Assert.Equal(
            ["tests/test_math.py", "tests/widgets_test.py"],
            cases.Select(row => row.Selector).ToArray());
        Assert.All(cases, row =>
        {
            Assert.StartsWith("py-test:", row.Id, StringComparison.Ordinal);
            Assert.Equal("pytest", row.Framework);
            Assert.Equal("python-test-file", row.Metadata["kind"]);
        });
    }

    [Fact]
    public void Build_run_command_for_pytest_uses_python_module_and_junit_artifact()
    {
        WriteProjectFile("pyproject.toml", "[project]\nname = \"sample\"\n");
        WriteProjectFile("tests/test_math.py", "def test_add():\n    assert True\n");
        var provider = new PythonTestProvider(new FakeTestProcessRunner());
        var workspace = Workspace("pytest");
        var generation = CtGenerationPaths.ResolveLatestOrFirst(workspace);

        var command = provider.BuildRunCommand(
            Request(workspace, PythonTestProvider.TestCaseId("tests/test_math.py")));

        Assert.Equal(ExpectedPythonExecutable(), command.FileName);
        Assert.Equal(ProjectRoot, command.WorkingDirectory);
        Assert.Equal(["-m", "pytest"], command.Arguments.Take(2).ToArray());
        var artifactArg = Assert.Single(command.Arguments, arg => arg.StartsWith("--junitxml=", StringComparison.Ordinal));
        var artifactPath = artifactArg["--junitxml=".Length..];
        Assert.Contains("tests/test_math.py", command.Arguments);
        AssertUsesGeneration(command, workspace, generation, artifactPath);
        Assert.Equal("-o", command.Arguments[command.Arguments.ToList().IndexOf("-o")]);
        Assert.Equal(
            $"cache_dir={CacheDirectory(generation)}",
            command.Arguments[command.Arguments.ToList().IndexOf("-o") + 1]);
        Assert.Equal(CacheDirectory(generation), command.Environment["PYTHONPYCACHEPREFIX"]);
    }

    [Fact]
    public void Build_run_command_uses_uv_when_uv_lock_is_present()
    {
        WriteProjectFile("pyproject.toml", "[project]\nname = \"sample\"\n");
        WriteProjectFile("uv.lock", string.Empty);
        var provider = new PythonTestProvider(new FakeTestProcessRunner());

        var command = provider.BuildRunCommand(
            Request(Workspace(null), PythonTestProvider.TestCaseId("tests/test_math.py")));

        Assert.Equal("uv", command.FileName);
        Assert.Equal(["run", "python", "-m", "pytest"], command.Arguments.Take(4).ToArray());
    }

    [Fact]
    public async Task Run_parses_pytest_junit_to_file_level_results()
    {
        WriteProjectFile("pyproject.toml", "[tool.pytest.ini_options]\ntestpaths = [\"tests\"]\n");
        WriteProjectFile("tests/test_math.py", "def test_add():\n    assert True\n");
        WriteProjectFile("tests/widgets_test.py", "def test_widget():\n    assert False\n");
        var runner = new FakeTestProcessRunner();
        runner.Enqueue(exitCode: 1);
        runner.OnRun = command =>
        {
            var artifactPath = command.Arguments
                .Single(arg => arg.StartsWith("--junitxml=", StringComparison.Ordinal))
                ["--junitxml=".Length..];
            Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
            File.WriteAllText(
                artifactPath,
                """
                <testsuite name="pytest" tests="2" failures="1">
                  <testcase classname="tests.test_math" name="test_add" time="0.125" />
                  <testcase classname="tests.widgets_test" name="test_widget" time="0.25">
                    <failure message="assert False">AssertionError: assert False</failure>
                  </testcase>
                </testsuite>
                """);
        };
        var provider = new PythonTestProvider(runner);
        var workspace = Workspace(null);

        var result = await provider.RunAsync(
            Request(
                workspace,
                PythonTestProvider.TestCaseId("tests/test_math.py"),
                PythonTestProvider.TestCaseId("tests/widgets_test.py")),
            TestContext.Current.CancellationToken);

        Assert.Equal("failed", result.Status);
        var byCaseId = result.CaseResults.ToDictionary(row => row.TestCaseId, StringComparer.Ordinal);
        Assert.Equal("passed", byCaseId[PythonTestProvider.TestCaseId("tests/test_math.py")].Status);
        Assert.Equal(0.125, byCaseId[PythonTestProvider.TestCaseId("tests/test_math.py")].DurationSeconds);
        Assert.Equal("failed", byCaseId[PythonTestProvider.TestCaseId("tests/widgets_test.py")].Status);
        Assert.Contains("AssertionError", byCaseId[PythonTestProvider.TestCaseId("tests/widgets_test.py")].FailureSummary);
        Assert.Equal("pytest", byCaseId[PythonTestProvider.TestCaseId("tests/widgets_test.py")].Metadata["framework"]);
        Assert.All(result.CaseResults, row => Assert.Equal(IndexIdentity, row.IndexIdentity));
        Assert.All(result.CaseResults, row => Assert.Equal("rev-1", row.ResultRevision));
        Assert.Equal(CtGenerationPaths.IdForOrdinal(workspace, 1), result.GenerationId);
        Assert.StartsWith(
            CtGenerationPaths.For(workspace, result.GenerationId!).ResultsDirectory,
            result.ResultArtifactPath!,
            StringComparison.Ordinal);
        AssertUsesGeneration(runner.Calls[0], workspace, FirstGeneration(workspace), result.ResultArtifactPath!);
    }

    [Fact]
    public async Task Sequential_runs_allocate_distinct_generation_directories()
    {
        WriteProjectFile("pyproject.toml", "[project]\nname = \"sample\"\n");
        var workspace = Workspace("pytest");
        var runner = new FakeTestProcessRunner();
        runner.OnRun = WriteEmptyPytestArtifact;
        runner.Enqueue(exitCode: 0);
        runner.Enqueue(exitCode: 0);
        var provider = new PythonTestProvider(runner);

        var first = await provider.RunAsync(
            Request(workspace, PythonTestProvider.TestCaseId("tests/test_math.py")),
            TestContext.Current.CancellationToken);
        var second = await provider.RunAsync(
            Request(workspace, PythonTestProvider.TestCaseId("tests/widgets_test.py")),
            TestContext.Current.CancellationToken);

        Assert.Equal(CtGenerationPaths.IdForOrdinal(workspace, 1), first.GenerationId);
        Assert.Equal(CtGenerationPaths.IdForOrdinal(workspace, 2), second.GenerationId);
        Assert.NotEqual(first.ResultArtifactPath, second.ResultArtifactPath);
        Assert.StartsWith(
            CtGenerationPaths.For(workspace, first.GenerationId!).ResultsDirectory,
            first.ResultArtifactPath!,
            StringComparison.Ordinal);
        Assert.StartsWith(
            CtGenerationPaths.For(workspace, second.GenerationId!).ResultsDirectory,
            second.ResultArtifactPath!,
            StringComparison.Ordinal);
        AssertWorkspaceIsolation(workspace);
    }

    [Fact]
    public async Task Run_without_result_artifact_returns_failed_results_for_selected_files()
    {
        WriteProjectFile("pyproject.toml", "[project]\nname = \"sample\"\n");
        var runner = new FakeTestProcessRunner();
        runner.Enqueue(standardError: "pytest: command not found", exitCode: 127);
        var provider = new PythonTestProvider(runner);
        var workspace = Workspace(null);

        var result = await provider.RunAsync(
            Request(workspace, PythonTestProvider.TestCaseId("tests/test_missing.py")),
            TestContext.Current.CancellationToken);

        var row = Assert.Single(result.CaseResults);
        Assert.Equal("failed", result.Status);
        Assert.Equal("failed", row.Status);
        Assert.Equal("pytest: command not found", row.FailureSummary);
        Assert.Equal(127, row.Metadata["exit_code"]);
        Assert.Equal(IndexIdentity, row.IndexIdentity);
        Assert.Equal(CtGenerationPaths.IdForOrdinal(workspace, 1), result.GenerationId);
    }

    // ---------------------------------------------------------------- command-line cap

    /// <summary>
    /// Windows caps a process command line at 32,767 characters, and a runner reached through a
    /// <c>.cmd</c>/<c>.bat</c> wrapper is routed through cmd.exe and capped at 8,191. Neither cap
    /// truncates: the over-long launch throws at Process.Start, the coordinator records a failed run,
    /// marks the tests stale, and retries the identical selection forever. The provider must fit the
    /// LOWER cap, because the executable is whatever first token the workspace configured.
    /// </summary>
    private const int CmdShimCommandLineCap = 8191;

    private const string PythonExtension = ".py";

    [Fact]
    public void BuildRunCommands_keeps_a_selection_that_fits_in_one_unchanged_invocation()
    {
        WriteProjectFile("pyproject.toml", "[project]\nname = \"sample\"\n");
        var provider = new PythonTestProvider(new FakeTestProcessRunner());
        var workspace = Workspace("pytest");
        var generation = CtGenerationPaths.ResolveLatestOrFirst(workspace);
        var request = Request(
            workspace,
            PythonTestProvider.TestCaseId("tests/test_math.py"),
            PythonTestProvider.TestCaseId("tests/widgets_test.py"));

        var commands = provider.BuildRunCommands(request);

        var single = Assert.Single(commands);
        var artifactPath = ArtifactPath(single);
        Assert.Equal(ExpectedPythonExecutable(), single.FileName);
        // Byte-identical to the pre-chunking argv: same flags, same order, same single report path.
        Assert.Equal(
            [
                "-m",
                "pytest",
                $"--junitxml={artifactPath}",
                "-o",
                $"cache_dir={CacheDirectory(generation)}",
                "tests/test_math.py",
                "tests/widgets_test.py"
            ],
            single.Arguments.ToArray());
        Assert.DoesNotContain(".part", Path.GetFileName(artifactPath), StringComparison.Ordinal);
        Assert.Equal(single.Arguments.ToArray(), provider.BuildRunCommand(request).Arguments.ToArray());
    }

    [Fact]
    public void BuildRunCommands_splits_a_wide_selection_so_every_invocation_fits_the_shim_cap()
    {
        WriteProjectFile("pyproject.toml", "[project]\nname = \"sample\"\n");
        var provider = new PythonTestProvider(new FakeTestProcessRunner());

        var commands = provider.BuildRunCommands(Request(Workspace("pytest"), LongTestCaseIds(4000)));

        Assert.True(commands.Count > 1, "a 4,000-file selection cannot fit one command line");
        foreach (var command in commands)
        {
            var length = CommandLineLength(command);
            Assert.True(
                length <= CmdShimCommandLineCap,
                $"invocation joined to {length} chars, over the {CmdShimCommandLineCap} cmd.exe cap");
        }
    }

    [Fact]
    public void BuildRunCommands_selects_every_requested_file_exactly_once_across_invocations()
    {
        WriteProjectFile("pyproject.toml", "[project]\nname = \"sample\"\n");
        var provider = new PythonTestProvider(new FakeTestProcessRunner());
        var ids = LongTestCaseIds(4000);

        var commands = provider.BuildRunCommands(Request(Workspace("pytest"), ids));

        var selected = commands.SelectMany(SelectedFiles).ToArray();
        // Nothing dropped and nothing duplicated.
        Assert.Equal(ids.Length, selected.Length);
        Assert.Equal(ids.Length, selected.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            ids.Select(id => id["py-test:".Length..]).OrderBy(file => file, StringComparer.Ordinal),
            selected.OrderBy(file => file, StringComparer.Ordinal));
        // An invocation with no selection runs whatever the project configures - an unfiltered
        // superset of the requested set, whose extra verdicts cannot be committed against it.
        Assert.All(commands, command => Assert.NotEmpty(SelectedFiles(command)));
    }

    [Fact]
    public void BuildRunCommands_gives_each_invocation_its_own_junit_report_path()
    {
        WriteProjectFile("pyproject.toml", "[project]\nname = \"sample\"\n");
        var provider = new PythonTestProvider(new FakeTestProcessRunner());

        var commands = provider.BuildRunCommands(Request(Workspace("pytest"), LongTestCaseIds(4000)));

        Assert.True(commands.Count > 1);
        var artifacts = commands.Select(ArtifactPath).ToArray();
        // One shared path would let the last invocation overwrite every earlier one's report.
        Assert.Equal(artifacts.Length, artifacts.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void BuildRunCommands_keeps_a_single_over_long_file_in_its_own_invocation()
    {
        WriteProjectFile("pyproject.toml", "[project]\nname = \"sample\"\n");
        var provider = new PythonTestProvider(new FakeTestProcessRunner());
        var wideFile = "tests/" + new string('a', 7000) + "/test_wide.py";

        var commands = provider.BuildRunCommands(Request(
            Workspace("pytest"),
            PythonTestProvider.TestCaseId(wideFile),
            PythonTestProvider.TestCaseId("tests/test_small.py")));

        // The over-long file cannot fit beside anything, but it is still run, not discarded.
        Assert.Equal(2, commands.Count);
        var selected = commands.SelectMany(SelectedFiles).ToArray();
        Assert.Contains(wideFile, selected);
        Assert.Contains("tests/test_small.py", selected);
    }

    [Fact]
    public async Task Run_reads_every_chunk_report_and_the_worst_status_wins()
    {
        WriteProjectFile("pyproject.toml", "[project]\nname = \"sample\"\n");
        var files = ManyTestFiles(130);
        var runner = new FakeTestProcessRunner();
        runner.OnRun = command => WriteJunitForSelection(command, failingFile: files[0]);
        runner.Enqueue(exitCode: 1);
        runner.Enqueue(exitCode: 0);
        var provider = new PythonTestProvider(runner);

        var result = await provider.RunAsync(
            Request(Workspace("pytest"), files.Select(PythonTestProvider.TestCaseId).ToArray()),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, runner.Calls.Count);
        Assert.Equal(files.Length, result.CaseResults.Count);
        var byCaseId = result.CaseResults.ToDictionary(row => row.TestCaseId, StringComparer.Ordinal);
        // The last chunk's verdicts survive the fold: reading one report would have lost them.
        Assert.Equal("failed", byCaseId[PythonTestProvider.TestCaseId(files[0])].Status);
        Assert.Equal("passed", byCaseId[PythonTestProvider.TestCaseId(files[^1])].Status);
        // A green chunk must never mask a red sibling.
        Assert.Equal("failed", result.Status);
        Assert.Equal(2, runner.Calls.Select(ArtifactPath).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Run_fails_only_the_chunk_that_produced_no_report()
    {
        WriteProjectFile("pyproject.toml", "[project]\nname = \"sample\"\n");
        var files = ManyTestFiles(130);
        var runner = new FakeTestProcessRunner();
        runner.OnRun = command =>
        {
            // Only the first invocation reports; the second dies before it writes anything.
            if (runner.Calls.Count == 1)
                WriteJunitForSelection(command, failingFile: null);
        };
        runner.Enqueue(exitCode: 0);
        runner.Enqueue(standardError: "pytest: internal error", exitCode: 2);
        var provider = new PythonTestProvider(runner);

        var result = await provider.RunAsync(
            Request(Workspace("pytest"), files.Select(PythonTestProvider.TestCaseId).ToArray()),
            TestContext.Current.CancellationToken);

        Assert.Equal(files.Length, result.CaseResults.Count);
        var byCaseId = result.CaseResults.ToDictionary(row => row.TestCaseId, StringComparer.Ordinal);
        // The healthy chunk's verdicts stand - a blanket failure over the whole selection would
        // report 120 tests red that actually passed.
        Assert.Equal("passed", byCaseId[PythonTestProvider.TestCaseId(files[0])].Status);
        // The dead chunk's tests are reported failed, not silently dropped as never-run.
        var dead = byCaseId[PythonTestProvider.TestCaseId(files[^1])];
        Assert.Equal("failed", dead.Status);
        Assert.Equal("pytest: internal error", dead.FailureSummary);
        Assert.Equal(2, dead.Metadata["exit_code"]);
        Assert.Equal("failed", result.Status);
    }

    /// <summary>
    /// pytest exits 5 (NO_TESTS_COLLECTED) when a selection collects no test at all. Its junitxml
    /// plugin still writes the report, because that runs in <c>pytest_sessionfinish</c>.
    /// </summary>
    private const int PytestNoTestsCollectedExitCode = 5;

    [Fact]
    public async Task Run_records_no_verdict_for_a_chunk_that_ran_and_collected_nothing()
    {
        WriteProjectFile("pyproject.toml", "[project]\nname = \"sample\"\n");
        // The shape a parser or linter repo has: a fixture tree of files named test_*.py that hold no
        // test function. Chunks are consecutive in ordinal path order, so those files fill a whole
        // chunk, pytest collects nothing from it and exits 5 - and still writes its report.
        var files = ManyTestFiles(130);
        var runner = new FakeTestProcessRunner();
        runner.OnRun = command =>
        {
            if (runner.Calls.Count == 1)
                WriteJunitForSelection(command, failingFile: null);
            else
                WriteEmptyPytestArtifact(command);
        };
        runner.Enqueue(exitCode: 0);
        runner.Enqueue(standardError: "no tests ran in 0.01s", exitCode: PytestNoTestsCollectedExitCode);
        var provider = new PythonTestProvider(runner);

        var result = await provider.RunAsync(
            Request(Workspace("pytest"), files.Select(PythonTestProvider.TestCaseId).ToArray()),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, runner.Calls.Count);
        var barren = SelectedFiles(runner.Calls[1]).Select(PythonTestProvider.TestCaseId).ToArray();
        Assert.NotEmpty(barren);
        var reported = result.CaseResults.Select(row => row.TestCaseId).ToArray();
        // Collecting nothing says nothing about those files. They must carry NO row - the store then
        // marks them stale - and above all no failed row on a commit that changed nothing.
        Assert.All(barren, id => Assert.DoesNotContain(id, reported));
        Assert.DoesNotContain("failed", result.CaseResults.Select(row => row.Status).ToArray());
        // Every verdict the healthy sibling produced survives untouched.
        Assert.Equal(SelectedFiles(runner.Calls[0]).Count, result.CaseResults.Count);
        Assert.Equal("passed", result.Status);
    }

    [Fact]
    public async Task Run_where_every_chunk_collected_nothing_reports_no_result_and_no_failure()
    {
        WriteProjectFile("pyproject.toml", "[project]\nname = \"sample\"\n");
        var files = TestFileNames(130);
        var runner = new FakeTestProcessRunner();
        runner.OnRun = WriteEmptyPytestArtifact;
        runner.Enqueue(standardError: "no tests ran in 0.01s", exitCode: PytestNoTestsCollectedExitCode);
        runner.Enqueue(standardError: "no tests ran in 0.01s", exitCode: PytestNoTestsCollectedExitCode);
        var provider = new PythonTestProvider(runner);

        var result = await provider.RunAsync(
            Request(Workspace("pytest"), files.Select(PythonTestProvider.TestCaseId).ToArray()),
            TestContext.Current.CancellationToken);

        // Nothing to run is neither a harness failure nor a verdict: no row is committed, so the
        // store marks the whole selection stale instead of turning 130 files red.
        Assert.Equal(2, runner.Calls.Count);
        Assert.Empty(result.CaseResults);
    }

    [Fact]
    public async Task Run_that_produced_no_verdict_at_all_fails_instead_of_reporting_an_empty_pass()
    {
        WriteProjectFile("pyproject.toml", "[project]\nname = \"sample\"\n");
        var files = TestFileNames(130);
        var runner = new FakeTestProcessRunner();
        // Every chunk finished and wrote a report, but no report names a selected test and the exit
        // code is not "collected nothing". The run proved nothing, so it must not read as an empty
        // pass that the daemon commits.
        runner.OnRun = WriteEmptyPytestArtifact;
        runner.Enqueue(standardError: "INTERNALERROR> RuntimeError: plugin crashed", exitCode: 2);
        runner.Enqueue(standardError: "INTERNALERROR> RuntimeError: plugin crashed", exitCode: 2);
        var provider = new PythonTestProvider(runner);

        var exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            provider.RunAsync(
                Request(Workspace("pytest"), files.Select(PythonTestProvider.TestCaseId).ToArray()),
                TestContext.Current.CancellationToken));

        Assert.Contains("exit code 2", exception.Message, StringComparison.Ordinal);
        Assert.Contains("INTERNALERROR", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_reports_the_first_part_report_on_disk_when_chunk_zero_wrote_none()
    {
        WriteProjectFile("pyproject.toml", "[project]\nname = \"sample\"\n");
        var files = ManyTestFiles(130);
        var runner = new FakeTestProcessRunner();
        runner.OnRun = command =>
        {
            // The mirror of the dead-chunk case: chunk 0 dies before it writes anything, chunk 1
            // reports normally.
            if (runner.Calls.Count == 2)
                WriteJunitForSelection(command, failingFile: null);
        };
        runner.Enqueue(standardError: "pytest: internal error", exitCode: 2);
        runner.Enqueue(exitCode: 0);
        var provider = new PythonTestProvider(runner);

        var result = await provider.RunAsync(
            Request(Workspace("pytest"), files.Select(PythonTestProvider.TestCaseId).ToArray()),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, runner.Calls.Count);
        Assert.False(File.Exists(ArtifactPath(runner.Calls[0])), "chunk 0 must leave no report");
        // The run's evidence is the first report ON DISK, not part 0 by position. Naming the missing
        // part 0 reports a run with no artifact at all while chunk 1's junit sits in the generation
        // directory, so no run_artifacts row is written and no surface can show the evidence.
        Assert.Equal(ArtifactPath(runner.Calls[1]), result.ResultArtifactPath!);
        Assert.True(File.Exists(result.ResultArtifactPath!), "the reported artifact must exist on disk");
        Assert.Contains(".part001", Path.GetFileName(result.ResultArtifactPath!), StringComparison.Ordinal);
        // A chunk that produced no report still answers for its own ids only.
        var byCaseId = result.CaseResults.ToDictionary(row => row.TestCaseId, StringComparer.Ordinal);
        Assert.Equal("failed", byCaseId[PythonTestProvider.TestCaseId(files[0])].Status);
        Assert.Equal("passed", byCaseId[PythonTestProvider.TestCaseId(files[^1])].Status);
    }

    private static string[] LongTestCaseIds(int count)
    {
        // Shaped like a real pytest node id in a deep tree, ~95 characters.
        const string Prefix = "tests/integration/a_package_with_a_realistically_long_directory_name/test_module_number_";
        return Enumerable.Range(0, count)
            .Select(index => PythonTestProvider.TestCaseId(
                Prefix + index.ToString("D4", CultureInfo.InvariantCulture) + PythonExtension))
            .ToArray();
    }

    private static string[] TestFileNames(int count) =>
        Enumerable.Range(0, count)
            .Select(index => "tests/test_" + index.ToString("D3", CultureInfo.InvariantCulture) + PythonExtension)
            .ToArray();

    private string[] ManyTestFiles(int count)
    {
        var files = TestFileNames(count);
        foreach (var file in files)
            WriteProjectFile(file, "def test_x():\n    assert True\n");
        return files;
    }

    private static int CommandLineLength(TestProcessCommand command) =>
        command.FileName.Length + 1 + command.Arguments.Sum(argument => argument.Length + 1);

    private static string ArtifactPath(TestProcessCommand command)
    {
        var argument = command.Arguments.Single(arg => arg.StartsWith("--junitxml=", StringComparison.Ordinal));
        return argument["--junitxml=".Length..];
    }

    /// <summary>The node ids this invocation selected: everything after the fixed pytest flags.</summary>
    private static IReadOnlyList<string> SelectedFiles(TestProcessCommand command)
    {
        var arguments = command.Arguments.ToList();
        var cacheOption = arguments.FindIndex(arg => arg.StartsWith("cache_dir=", StringComparison.Ordinal));
        return arguments.Skip(cacheOption + 1).ToArray();
    }

    private static void WriteJunitForSelection(TestProcessCommand command, string? failingFile)
    {
        var artifactPath = ArtifactPath(command);
        Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
        var cases = string.Concat(SelectedFiles(command).Select(file =>
        {
            var className = file[..^PythonExtension.Length].Replace('/', '.');
            return string.Equals(file, failingFile, StringComparison.Ordinal)
                ? $"""<testcase classname="{className}" name="test_x" time="0.5"><failure message="boom">AssertionError: boom</failure></testcase>"""
                : $"""<testcase classname="{className}" name="test_x" time="0.25" />""";
        }));
        File.WriteAllText(artifactPath, $"""<testsuite name="pytest">{cases}</testsuite>""");
    }

    private ContinuousTestWorkspace Workspace(string? framework)
    {
        var workspace = new ContinuousTestWorkspace(
            WorkspaceId: "ws:1",
            WorkspaceRoot: ProjectRoot,
            ProjectPath: Path.Combine(ProjectRoot, "pyproject.toml"),
            BuildOutputRoot: Path.Combine(_dir, "ct-build"),
            Framework: framework);
        _ctTemps.Add(CtTempPaths.ForWorkspace(workspace));
        return workspace;
    }

    private static ContinuousTestProviderRunRequest Request(
        ContinuousTestWorkspace workspace,
        params string[] testCaseIds) =>
        new(
            Workspace: workspace,
            SelectedRevision: "rev-1",
            IndexIdentity: IndexIdentity,
            RunId: "run:pytest",
            TestCaseIds: testCaseIds);

    private void WriteProjectFile(string relativePath, string contents)
    {
        var path = Path.Combine(ProjectRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    private static string ExpectedPythonExecutable() =>
        OperatingSystem.IsWindows() ? "python" : "python3";

    private static void WriteEmptyPytestArtifact(TestProcessCommand command)
    {
        var artifactArg = command.Arguments.Single(arg => arg.StartsWith("--junitxml=", StringComparison.Ordinal));
        var artifactPath = artifactArg["--junitxml=".Length..];
        Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
        File.WriteAllText(artifactPath, """<testsuite name="pytest" tests="0" />""");
    }

    private static void AssertUsesGeneration(
        TestProcessCommand command,
        ContinuousTestWorkspace workspace,
        CtGenerationPaths generation,
        string artifactPath)
    {
        Assert.Equal(generation.TempDirectory, command.Environment["TMPDIR"]);
        Assert.Equal(generation.TempDirectory, command.Environment["TMP"]);
        Assert.Equal(generation.TempDirectory, command.Environment["TEMP"]);
        Assert.True(Directory.Exists(generation.TempDirectory));
        Assert.Equal(workspace.WorkspaceRoot, command.Environment[CtEnvironment.WorkspaceRoot]);
        Assert.StartsWith(generation.ResultsDirectory, artifactPath, StringComparison.Ordinal);
        AssertWorkspaceIsolation(workspace);
    }

    private static void AssertWorkspaceIsolation(ContinuousTestWorkspace workspace)
    {
        var repoBin = Path.Combine(workspace.WorkspaceRoot, "bin");
        var repoObj = Path.Combine(workspace.WorkspaceRoot, "obj");
        var repoTestResults = Path.Combine(workspace.WorkspaceRoot, "TestResults");
        Assert.False(Directory.Exists(repoBin) && Directory.EnumerateFileSystemEntries(repoBin).Any());
        Assert.False(Directory.Exists(repoObj) && Directory.EnumerateFileSystemEntries(repoObj).Any());
        Assert.False(Directory.Exists(repoTestResults));
    }

    private static string CacheDirectory(CtGenerationPaths generation) =>
        Path.Combine(generation.GenerationRoot, "cache");

    private static CtGenerationPaths FirstGeneration(ContinuousTestWorkspace workspace) =>
        CtGenerationPaths.For(workspace, CtGenerationPaths.IdForOrdinal(workspace, 1));

    private static void BestEffortDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
