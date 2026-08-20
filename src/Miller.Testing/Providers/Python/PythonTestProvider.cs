using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using Miller.Testing.Parsing;

namespace Miller.Testing;

public sealed class PythonTestProvider : IContinuousTestProvider
{
    private const string TestCaseIdPrefix = "py-test:";
    private static readonly string[] ProjectFileNames =
    [
        "pyproject.toml",
        "pytest.ini",
        "tox.ini",
        "setup.cfg",
        "setup.py",
    ];

    private readonly ITestProcessRunner _runner;

    public PythonTestProvider(ITestProcessRunner runner)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    public Task<IReadOnlyList<ProviderTestCase>> DiscoverAsync(
        ContinuousTestWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var projectRoot = ProjectRoot(workspace);
        if (!Directory.Exists(projectRoot))
            return Task.FromResult<IReadOnlyList<ProviderTestCase>>([]);

        var cases = Directory
            .EnumerateFiles(projectRoot, "*.py", SearchOption.AllDirectories)
            .Select(path => RelativePathOrNull(projectRoot, path))
            .Where(relativePath => relativePath is not null)
            .Select(relativePath => relativePath!)
            .Where(IsDiscoverableTestFile)
            .Order(StringComparer.Ordinal)
            .Select(relativePath => new ProviderTestCase(
                Id: TestCaseId(relativePath),
                DisplayName: relativePath,
                FullyQualifiedName: relativePath,
                Selector: relativePath,
                Framework: RequiredFramework(workspace),
                SourcePath: relativePath,
                Metadata: new Dictionary<string, object?>
                {
                    ["kind"] = "python-test-file",
                }))
            .ToArray();
        return Task.FromResult<IReadOnlyList<ProviderTestCase>>(cases);
    }

    public async Task<ProviderRunResult> RunAsync(
        ContinuousTestProviderRunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var paths = CtGenerationPaths.Allocate(request.Workspace);
        try
        {
            return await RunInGenerationAsync(request, paths, cancellationToken).ConfigureAwait(false);
        }
        catch (ContinuousTestProviderException exception) when (exception.GenerationId is null)
        {
            throw StampGeneration(exception, paths);
        }
    }

    private async Task<ProviderRunResult> RunInGenerationAsync(
        ContinuousTestProviderRunRequest request,
        CtGenerationPaths paths,
        CancellationToken cancellationToken)
    {
        // Resolved once and threaded through: the report path the command is TOLD to write and the
        // path the parser reads back must be the same string, and NewRunId is time-based.
        var runId = request.RunId ?? NewRunId(request);
        var invocations = BuildRunInvocations(request, paths, runId);
        var results = new List<TestProcessResult>(invocations.Count);

        // Sequential on purpose. The invocations share one project root, one generation directory and
        // one pytest cache, so running them at once would have them overwrite each other's output.
        // The CT execution budget already says a workspace runs one thing at a time.
        foreach (var invocation in invocations)
            results.Add(await _runner.RunAsync(invocation.Command, cancellationToken).ConfigureAwait(false));

        var caseResults = MergeRuns(request, invocations, results);

        return new ProviderRunResult(
            RunId: runId,
            Status: RunStatus(caseResults),
            CaseResults: caseResults,
            // The first part that actually reached disk, never part 0 by position. A chunked run whose
            // first part dies before pytest writes its report leaves that path missing, and naming it
            // would report a run with no evidence at all while the surviving parts' junit reports sit
            // in the generation directory. Same rule as JavaScriptTestProvider.
            ResultArtifactPath: invocations
                .Select(static invocation => invocation.ResultArtifactPath)
                .FirstOrDefault(File.Exists),
            CoverageArtifacts: DiscoverCoverageArtifacts(request.Workspace))
        {
            GenerationId = paths.GenerationId,
        };
    }

    /// <summary>
    /// pytest's exit code for "no tests were collected". It is not a test failure: the session ran to
    /// the end and found nothing to run. See <see cref="MergeRuns"/>.
    /// </summary>
    private const int PytestNoTestsCollectedExitCode = 5;

    /// <summary>
    /// Folds the invocations of one chunked run back into the single result set the caller asked for.
    /// A one-invocation run parses exactly as it did before chunking existed.
    ///
    /// Every part's report is read, so no chunk's verdicts are lost, and the worst status wins: the
    /// run status is aggregated over the union of the case results, so a green chunk can never mask a
    /// red sibling.
    ///
    /// Two events look alike from a distance and must NOT be judged alike:
    ///
    /// <list type="bullet">
    /// <item>A chunk whose process wrote NO report answered for nothing it selected, so ITS test case
    /// ids are recorded failed - never the whole selection, which the healthy chunks already answered
    /// for.</item>
    /// <item>A chunk that RAN and collected nothing wrote its report and exited
    /// <see cref="PytestNoTestsCollectedExitCode"/>. A repo with a fixture tree of <c>test_*.py</c>
    /// input files holds no test functions, and ordinal chunking puts those files together, so a whole
    /// chunk can legitimately collect nothing. Failing its ids would turn dozens of tests red on a
    /// commit that changed nothing, and every later run would repeat it. Those ids get NO verdict
    /// here, so the store marks them stale - which is what the one unchunked pytest process produced
    /// for the same files before chunking existed.</item>
    /// </list>
    ///
    /// The discriminator is the report on disk plus the exit code, never the exit code alone: pytest's
    /// junitxml plugin writes the report in <c>pytest_sessionfinish</c>, so a report means the session
    /// finished.
    ///
    /// The no-verdicts case is judged ACROSS THE RUN, as <c>DotnetTestProvider</c> does, never per
    /// chunk: a chunk that finished, failed, and named no test this run selected records nothing and
    /// lets its siblings stand, but a run where NO chunk produced a single verdict and at least one
    /// chunk failed for a reason other than "collected nothing" throws, so an unexplained run can
    /// never be committed as a silent empty pass.
    /// </summary>
    private static IReadOnlyList<ProviderCaseResult> MergeRuns(
        ContinuousTestProviderRunRequest request,
        IReadOnlyList<PytestInvocation> invocations,
        IReadOnlyList<TestProcessResult> results)
    {
        var caseResults = new List<ProviderCaseResult>();
        TestProcessResult? unexplainedFailure = null;
        for (var index = 0; index < invocations.Count; index++)
        {
            var invocation = invocations[index];
            var result = results[index];
            bool reported = File.Exists(invocation.ResultArtifactPath);
            IReadOnlyList<ProviderCaseResult> parsed = reported
                ? ParsePytestJunit(request, invocation.ResultArtifactPath)
                : [];

            caseResults.AddRange(parsed);
            if (parsed.Count > 0 || result.ExitCode == 0)
                continue;

            if (reported)
            {
                // The session finished and produced no verdict for anything this chunk selected.
                // "Collected nothing" is the ordinary fixture-tree shape and says nothing at all;
                // any other failing exit code is unexplained and is judged across the run below.
                if (result.ExitCode != PytestNoTestsCollectedExitCode)
                    unexplainedFailure ??= result;
                continue;
            }

            if (invocation.SelectedTestCaseIds.Count == 0)
                throw new ContinuousTestProviderException(
                    $"Python test run failed with exit code {result.ExitCode}: {FailureSummary(result)}");

            caseResults.AddRange(FailedSelectedCaseResults(
                request,
                invocation.SelectedTestCaseIds,
                result,
                invocation.ResultArtifactPath));
        }

        if (caseResults.Count == 0 && unexplainedFailure is not null)
            throw new ContinuousTestProviderException(
                $"Python test run failed with exit code {unexplainedFailure.ExitCode}: " +
                FailureSummary(unexplainedFailure));

        return caseResults;
    }

    /// <summary>
    /// Preview/test seam: builds the run command against the latest existing generation (or the
    /// would-be first). Production runs never use it — <see cref="RunAsync"/> allocates its own
    /// generation and builds every command and result path from that one handle.
    /// </summary>
    public TestProcessCommand BuildRunCommand(ContinuousTestProviderRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return BuildRunCommands(request)[0];
    }

    /// <summary>
    /// Preview/test seam: every invocation the request would run, in order. A selection that fits one
    /// command line yields exactly one, so this is the same command <see cref="BuildRunCommand"/>
    /// returns; a wider selection yields the chunks it is split into. Production runs never use it —
    /// <see cref="RunAsync"/> allocates its own generation and builds every command from that handle.
    /// </summary>
    public IReadOnlyList<TestProcessCommand> BuildRunCommands(ContinuousTestProviderRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return BuildRunInvocations(
                request,
                CtGenerationPaths.ResolveLatestOrFirst(request.Workspace),
                request.RunId ?? NewRunId(request))
            .Select(static invocation => invocation.Command)
            .ToArray();
    }

    /// <summary>
    /// One pytest invocation: the command, the <c>--junitxml</c> report it writes, and the request's
    /// test case ids it answers for. The three travel together because a chunked run gives each part
    /// its own report path, and a part that dies before writing one must be reported failed against
    /// its own ids only.
    /// </summary>
    private sealed record PytestInvocation(
        TestProcessCommand Command,
        string ResultArtifactPath,
        IReadOnlyList<string> SelectedTestCaseIds);

    /// <summary>
    /// One chunkable unit of the selection: the argv elements that must travel together, plus the
    /// request ids that resolved to them.
    /// </summary>
    private sealed record PytestSelectionUnit(
        IReadOnlyList<string> Argv,
        IReadOnlyList<string> TestCaseIds);

    /// <summary>
    /// Builds the invocations for one run. A selection that fits the platform command-line cap is a
    /// single command with the unchanged single report path — byte-identical to what this provider
    /// sent before chunking existed; a wider selection is split across several pytest invocations.
    ///
    /// pytest takes one argv element per selected node id and has no response-file option, so a wide
    /// selection has nowhere to go but across processes. Windows caps a command line at 32,767
    /// characters, and this provider's executable is the caller's own first token — a workspace that
    /// names a <c>.cmd</c>/<c>.bat</c> wrapper is routed through <c>cmd.exe</c> and capped at 8,191
    /// instead. Neither cap truncates: the over-long launch throws at <c>Process.Start</c>, the
    /// coordinator records a failed run, marks the tests stale, and retries the identical selection
    /// forever. <see cref="CtArgvChunking"/>'s default byte budget already assumes the lower cap.
    /// </summary>
    private static IReadOnlyList<PytestInvocation> BuildRunInvocations(
        ContinuousTestProviderRunRequest request,
        CtGenerationPaths paths,
        string runKey)
    {
        var framework = RequiredFramework(request.Workspace);
        if (framework != "pytest")
            throw UnsupportedFramework(framework, request.Workspace.ProjectPath);

        paths.EnsureDirectories();
        Directory.CreateDirectory(CacheDirectory(paths));

        IReadOnlyList<PytestSelectionUnit> units = SelectionUnits(request);
        if (units.Count == 0)
        {
            // Nothing selectable: pytest runs whatever the project configures, exactly as before. The
            // request's ids still ride along so an outright launch failure is reported against them.
            return [BuildInvocation(request, paths, runKey, [], request.TestCaseIds, part: null)];
        }

        IReadOnlyList<IReadOnlyList<PytestSelectionUnit>> chunks =
            CtArgvChunking.Chunk(units, static unit => CtArgvChunking.ArgvCost(unit.Argv));
        var invocations = new List<PytestInvocation>(chunks.Count);
        for (var index = 0; index < chunks.Count; index++)
        {
            var chunk = chunks[index];
            var attributed = chunk
                .SelectMany(static unit => unit.TestCaseIds)
                .ToHashSet(StringComparer.Ordinal);

            // The FIRST invocation also answers for ids that name no test file. They never reached the
            // argv, so no chunk selected them, and dropping them would lose them from a failed run.
            if (index == 0)
            {
                foreach (var testCaseId in UnselectableTestCaseIds(request))
                    attributed.Add(testCaseId);
            }

            invocations.Add(BuildInvocation(
                request,
                paths,
                runKey,
                chunk.SelectMany(static unit => unit.Argv).ToArray(),
                // Request order with duplicates intact: a one-invocation run must report exactly the
                // rows it reported before chunking existed.
                request.TestCaseIds.Where(attributed.Contains).ToArray(),
                part: chunks.Count == 1 ? null : index));
        }

        return invocations;
    }

    /// <summary>
    /// The selection as chunkable units, one per distinct test file in ordinal order — the same argv
    /// the provider built before chunking existed. pytest selects by path, so a unit is a single argv
    /// element, but it still carries every request id that resolved to that file, because two ids can
    /// name one file and a failed chunk must report both.
    /// </summary>
    private static IReadOnlyList<PytestSelectionUnit> SelectionUnits(
        ContinuousTestProviderRunRequest request)
    {
        var byFile = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var testCaseId in request.TestCaseIds)
        {
            var file = TestFileFromId(testCaseId);
            if (string.IsNullOrWhiteSpace(file))
                continue;

            if (!byFile.TryGetValue(file, out var testCaseIds))
            {
                testCaseIds = [];
                byFile[file] = testCaseIds;
            }

            testCaseIds.Add(testCaseId);
        }

        return byFile
            .OrderBy(row => row.Key, StringComparer.Ordinal)
            .Select(row => new PytestSelectionUnit([row.Key], row.Value))
            .ToArray();
    }

    private static IEnumerable<string> UnselectableTestCaseIds(ContinuousTestProviderRunRequest request) =>
        request.TestCaseIds.Where(static testCaseId => string.IsNullOrWhiteSpace(TestFileFromId(testCaseId)));

    private static PytestInvocation BuildInvocation(
        ContinuousTestProviderRunRequest request,
        CtGenerationPaths paths,
        string runKey,
        IReadOnlyList<string> selection,
        IReadOnlyList<string> selectedTestCaseIds,
        int? part)
    {
        var projectRoot = ProjectRoot(request.Workspace);
        var artifactPath = ResultArtifactPath(paths, runKey, part);
        Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);

        var pytestArgs = new[]
            {
                $"--junitxml={artifactPath}",
                "-o",
                $"cache_dir={CacheDirectory(paths)}",
            }
            .Concat(selection)
            .ToArray();

        return new PytestInvocation(
            BuildCommand(request, paths, projectRoot, pytestArgs),
            artifactPath,
            selectedTestCaseIds);
    }

    private static TestProcessCommand BuildCommand(
        ContinuousTestProviderRunRequest request,
        CtGenerationPaths paths,
        string projectRoot,
        IReadOnlyList<string> pytestArgs)
    {
        if (!string.IsNullOrWhiteSpace(request.Command))
        {
            var tokens = SplitCommand(request.Command);
            if (tokens.Count == 0)
                throw new ContinuousTestProviderException("Python test command must not be empty.");

            return new TestProcessCommand(
                tokens[0],
                tokens.Skip(1).Concat(pytestArgs).ToArray(),
                projectRoot,
                WorkspaceEnvironment(request.Workspace, paths));
        }

        if (File.Exists(Path.Combine(projectRoot, "uv.lock")))
        {
            return new TestProcessCommand(
                "uv",
                new[] { "run", "python", "-m", "pytest" }.Concat(pytestArgs).ToArray(),
                projectRoot,
                WorkspaceEnvironment(request.Workspace, paths));
        }

        return new TestProcessCommand(
            LocalPython(projectRoot),
            new[] { "-m", "pytest" }.Concat(pytestArgs).ToArray(),
            projectRoot,
            WorkspaceEnvironment(request.Workspace, paths));
    }

    public static string TestCaseId(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) throw new ArgumentException("must not be empty", nameof(relativePath));
        return TestCaseIdPrefix + NormalizeRelativePath(relativePath);
    }

    public static bool IsPythonProjectFile(string path) =>
        ProjectFileNames.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<ProviderCaseResult> ParsePytestJunit(
        ContinuousTestProviderRunRequest request,
        string artifactPath)
    {
        var parsed = JunitTestResultParser.Parse(artifactPath);
        var projectRoot = ProjectRoot(request.Workspace);
        var selectedIds = request.TestCaseIds.ToHashSet(StringComparer.Ordinal);
        var groups = new Dictionary<string, List<ParsedTestArtifactCase>>(StringComparer.Ordinal);

        foreach (var testCase in parsed.Cases)
        {
            var testCaseId = TestCaseIdFromParsedCase(projectRoot, testCase)
                ?? (selectedIds.Count == 1 ? selectedIds.Single() : null);
            if (testCaseId is null)
                continue;
            if (selectedIds.Count > 0 && !selectedIds.Contains(testCaseId))
                continue;
            if (!groups.TryGetValue(testCaseId, out var cases))
            {
                cases = [];
                groups[testCaseId] = cases;
            }

            cases.Add(testCase);
        }

        return groups
            .OrderBy(row => row.Key, StringComparer.Ordinal)
            .Select(row => ToProviderResult(request, artifactPath, row.Key, row.Value))
            .ToArray();
    }

    private static ProviderCaseResult ToProviderResult(
        ContinuousTestProviderRunRequest request,
        string artifactPath,
        string testCaseId,
        IReadOnlyList<ParsedTestArtifactCase> cases)
    {
        var status = AggregateStatus(cases.Select(row => row.Status));
        var duration = cases
            .Select(row => row.DurationSeconds)
            .Where(durationSeconds => durationSeconds is not null)
            .Sum(durationSeconds => durationSeconds!.Value);
        var failureSummary = cases
            .Select(row => row.FailureText)
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
        return new ProviderCaseResult(
            Id: StableId("test_result", request.Workspace.WorkspaceId, testCaseId, request.RunId),
            TestCaseId: testCaseId,
            Status: status,
            ResultRevision: request.SelectedRevision,
            IndexIdentity: request.IndexIdentity,
            DurationSeconds: duration,
            FailureSummary: failureSummary,
            Metadata: new Dictionary<string, object?>
            {
                ["artifact_path"] = artifactPath,
                ["framework"] = "pytest",
            });
    }

    private static string? TestCaseIdFromParsedCase(string projectRoot, ParsedTestArtifactCase testCase)
    {
        if (string.IsNullOrWhiteSpace(testCase.ClassName))
            return null;

        var segments = testCase.ClassName
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var count = segments.Length; count >= 1; count--)
        {
            var relativePath = string.Join("/", segments.Take(count)) + ".py";
            if (File.Exists(Path.Combine(projectRoot, relativePath)))
                return TestCaseId(relativePath);
        }

        return null;
    }

    /// <summary>
    /// The verdict for an invocation that failed without writing a report: every test case id THAT
    /// invocation selected is failed, because the run proved nothing about any of them. A chunked run
    /// passes only the failing chunk's ids, so a healthy sibling's parsed verdicts stand.
    /// </summary>
    private static IReadOnlyList<ProviderCaseResult> FailedSelectedCaseResults(
        ContinuousTestProviderRunRequest request,
        IReadOnlyList<string> testCaseIds,
        TestProcessResult result,
        string artifactPath)
    {
        var summary = FailureSummary(result);
        return testCaseIds
            .Select(testCaseId => new ProviderCaseResult(
                Id: StableId("test_result", request.Workspace.WorkspaceId, testCaseId, request.RunId),
                TestCaseId: testCaseId,
                Status: "failed",
                ResultRevision: request.SelectedRevision,
                IndexIdentity: request.IndexIdentity,
                FailureSummary: summary,
                Metadata: new Dictionary<string, object?>
                {
                    ["artifact_path"] = artifactPath,
                    ["framework"] = "pytest",
                    ["exit_code"] = result.ExitCode,
                }))
            .ToArray();
    }

    private static IReadOnlyList<ProviderCoverageArtifact> DiscoverCoverageArtifacts(ContinuousTestWorkspace workspace)
    {
        var artifacts = new List<ProviderCoverageArtifact>();
        var paths = new HashSet<string>(PathStringComparer);
        var projectRoot = ProjectRoot(workspace);
        AddCoverageArtifact(
            artifacts,
            paths,
            Path.Combine(projectRoot, "coverage.xml"),
            "cobertura",
            projectRoot);
        AddCoverageArtifact(
            artifacts,
            paths,
            Path.Combine(projectRoot, "coverage", "lcov.info"),
            "lcov",
            projectRoot);
        AddCoverageArtifact(
            artifacts,
            paths,
            Path.Combine(workspace.BuildOutputRoot, "coverage.xml"),
            "cobertura",
            workspace.BuildOutputRoot);
        AddCoverageArtifact(
            artifacts,
            paths,
            Path.Combine(workspace.BuildOutputRoot, "coverage", "lcov.info"),
            "lcov",
            workspace.BuildOutputRoot);
        return artifacts;
    }

    private static void AddCoverageArtifact(
        List<ProviderCoverageArtifact> artifacts,
        HashSet<string> paths,
        string artifactPath,
        string parser,
        string artifactRoot)
    {
        if (!File.Exists(artifactPath))
            return;

        var fullPath = Path.GetFullPath(artifactPath);
        if (!paths.Add(fullPath))
            return;

        artifacts.Add(new ProviderCoverageArtifact(
            ArtifactPath: fullPath,
            Parser: parser,
            ArtifactRoot: artifactRoot));
    }

    private static string RequiredFramework(ContinuousTestWorkspace workspace)
    {
        var framework = workspace.Framework?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(framework) || framework == "python")
            return "pytest";
        return framework;
    }

    private static string ProjectRoot(ContinuousTestWorkspace workspace)
    {
        var projectPath = Path.GetFullPath(workspace.ProjectPath);
        return IsPythonProjectFile(projectPath)
            ? Path.GetDirectoryName(projectPath)!
            : Directory.Exists(projectPath)
                ? projectPath
                : Path.GetDirectoryName(projectPath)!;
    }

    /// <summary>
    /// The junit report for one invocation. <paramref name="part"/> is null for a run that fits a
    /// single command line — which keeps the filename byte-identical to the pre-chunking one — and is
    /// the zero-based invocation index when a run is split, so chunk N cannot overwrite chunk N-1's
    /// report and every part stays on disk as evidence.
    /// </summary>
    private static string ResultArtifactPath(CtGenerationPaths paths, string runKey, int? part)
    {
        var runHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(runKey))).ToLowerInvariant();
        var suffix = part is null ? string.Empty : $".part{part.Value.ToString("D3", CultureInfo.InvariantCulture)}";
        return Path.Combine(paths.ResultsDirectory, $"run-{runHash}{suffix}.xml");
    }

    private static ContinuousTestProviderException StampGeneration(
        ContinuousTestProviderException exception,
        CtGenerationPaths paths) =>
        new(exception.Message, exception)
        {
            GenerationId = paths.GenerationId,
            ResultArtifactPath = exception.ResultArtifactPath,
        };

    private static string NewRunId(ContinuousTestProviderRunRequest request) =>
        StableId(
            "ct_run",
            request.Workspace.WorkspaceId,
            request.Workspace.ProjectPath,
            request.SelectedRevision,
            DateTimeOffset.UtcNow.UtcTicks);

    private static string RunStatus(IReadOnlyList<ProviderCaseResult> results) =>
        AggregateStatus(results.Select(row => row.Status));

    private static string AggregateStatus(IEnumerable<string> statuses)
    {
        var statusSet = statuses.Select(NormalizeStatus).ToHashSet(StringComparer.Ordinal);
        if (statusSet.Count == 0)
            return "passed";
        if (statusSet.Contains("failed") || statusSet.Contains("errored"))
            return "failed";
        if (statusSet.SetEquals(["skipped"]))
            return "skipped";
        return "passed";
    }

    private static string NormalizeStatus(string status) =>
        status.ToLowerInvariant() switch
        {
            "fail" or "failed" or "failure" => "failed",
            "error" or "errored" => "errored",
            "skip" or "skipped" or "pending" or "todo" => "skipped",
            _ => "passed",
        };

    private static string FailureSummary(TestProcessResult result)
    {
        var text = !string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardError
            : result.StandardOutput;
        text = text.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return $"Python test run failed with exit code {result.ExitCode}.";

        var firstLine = text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(firstLine)
            ? $"Python test run failed with exit code {result.ExitCode}."
            : firstLine;
    }

    private static string? TestFileFromId(string testCaseId) =>
        testCaseId.StartsWith(TestCaseIdPrefix, StringComparison.Ordinal)
            ? NormalizeRelativePath(testCaseId[TestCaseIdPrefix.Length..])
            : null;

    private static bool IsDiscoverableTestFile(string relativePath)
    {
        var segments = relativePath.Split('/');
        if (segments.Any(IsExcludedSegment))
            return false;

        var fileName = Path.GetFileName(relativePath);
        return fileName.StartsWith("test_", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith("_test.py", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExcludedSegment(string segment) =>
        segment is ".git"
            or ".hg"
            or ".svn"
            or ".claude"
            or ".venv"
            or "venv"
            or "env"
            or "__pycache__"
            or ".pytest_cache"
            or ".mypy_cache"
            or ".ruff_cache"
            or ".tox"
            or ".worktrees"
            or "build"
            or "dist"
            or "node_modules";

    private static IReadOnlyDictionary<string, string?> WorkspaceEnvironment(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths)
    {
        Directory.CreateDirectory(paths.TempDirectory);
        var cacheDirectory = CacheDirectory(paths);
        Directory.CreateDirectory(cacheDirectory);
        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [CtEnvironment.WorkspaceRoot] = workspace.WorkspaceRoot,
            // Removed, not merely unset: the test process inherits it from the daemon, and a `miller` CLI
            // verb run inside a test would bind the DAEMON's workspace. See DotnetTestProvider for the note.
            [CtEnvironment.DaemonWorkspaceRoot] = null,
            ["TMPDIR"] = paths.TempDirectory,
            ["TMP"] = paths.TempDirectory,
            ["TEMP"] = paths.TempDirectory,
            ["PYTHONPYCACHEPREFIX"] = cacheDirectory,
        };
    }

    private static string CacheDirectory(CtGenerationPaths paths) =>
        Path.Combine(paths.GenerationRoot, "cache");

    private static string LocalPython(string projectRoot)
    {
        var venvPython = OperatingSystem.IsWindows()
            ? Path.Combine(projectRoot, ".venv", "Scripts", "python.exe")
            : Path.Combine(projectRoot, ".venv", "bin", "python");
        if (File.Exists(venvPython))
            return venvPython;
        return OperatingSystem.IsWindows() ? "python" : "python3";
    }

    private static string? RelativePathOrNull(string root, string path)
    {
        var fullPath = Path.GetFullPath(path);
        var relativePath = Path.GetRelativePath(Path.GetFullPath(root), fullPath);
        if (relativePath == "."
            || relativePath.StartsWith("..", PathComparison)
            || Path.IsPathRooted(relativePath))
            return null;

        return NormalizeRelativePath(relativePath);
    }

    private static string NormalizeRelativePath(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');

    private static IReadOnlyList<string> SplitCommand(string command)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuote = false;
        char quoteChar = '\0';
        foreach (var c in command)
        {
            if ((c == '"' || c == '\'') && (!inQuote || c == quoteChar))
            {
                inQuote = !inQuote;
                quoteChar = inQuote ? c : '\0';
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuote)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
            result.Add(current.ToString());
        return result;
    }

    private static ContinuousTestProviderException UnsupportedFramework(string framework, string projectPath) =>
        new($"Continuous test framework '{framework}' is unsupported for Python project '{projectPath}'.");

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;


    private static string StableId(string @namespace, params object?[] parts)
    {
        var normalized = string.Join("\x1f", parts.Select(PartToString));
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        var hex = Convert.ToHexString(digest).ToLowerInvariant()[..24];
        return $"{@namespace}:{hex}";
    }

    private static string PartToString(object? part) =>
        part switch
        {
            null => "",
            IFormattable formattable => formattable.ToString(format: null, CultureInfo.InvariantCulture) ?? "",
            _ => part.ToString() ?? "",
        };

    private static StringComparer PathStringComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
