using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Miller.Testing.Parsing;

namespace Miller.Testing;

public sealed class JavaScriptTestProvider : IContinuousTestProvider
{
    private const string TestCaseIdPrefix = "js-test:";
    private readonly ITestProcessRunner _runner;
    private readonly Func<string, string?> _findPackageManagerOnPath;

    public JavaScriptTestProvider(ITestProcessRunner runner)
        : this(runner, FindPackageManagerOnSystemPath)
    {
    }

    /// <summary>
    /// Test seam for the package-manager probe. <paramref name="findPackageManagerOnPath"/> takes a bare
    /// manager name ("npm", "pnpm", "yarn") and returns the launchable file PATH really holds, or null
    /// when it holds none. A test injects it because the real answer depends on what the developer's
    /// machine installed - npm's own <c>.cmd</c> shim, a Volta or Chocolatey <c>.exe</c> shim, or
    /// nothing at all - and a provider that guessed one of those broke the others.
    /// </summary>
    internal JavaScriptTestProvider(
        ITestProcessRunner runner,
        Func<string, string?> findPackageManagerOnPath)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _findPackageManagerOnPath = findPackageManagerOnPath
            ?? throw new ArgumentNullException(nameof(findPackageManagerOnPath));
    }

    public Task<IReadOnlyList<ProviderTestCase>> DiscoverAsync(
        ContinuousTestWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var packageRoot = PackageRoot(workspace);
        if (!Directory.Exists(packageRoot))
            return Task.FromResult<IReadOnlyList<ProviderTestCase>>([]);

        var framework = ResolvedFrameworkOrNull(workspace);
        ValidateSupportedDiscoveryVersion(framework, packageRoot);
        Func<string, bool> matches = string.Equals(framework, "node-test", StringComparison.Ordinal)
            ? NodeTestFileDiscovery.ForPackage(packageRoot).IsMatch
            : JsFrameworkTestFileDiscovery.ForFramework(framework, packageRoot).IsMatch;

        var cases = Directory
            .EnumerateFiles(packageRoot, "*", SearchOption.AllDirectories)
            .Select(path => RelativePathOrNull(packageRoot, path))
            .Where(relativePath => relativePath is not null)
            .Select(relativePath => relativePath!)
            .Where(relativePath => IsDiscoverableTestFile(relativePath, matches))
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
                    ["kind"] = "javascript-test-file",
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
        var invocations = BuildRunInvocations(request, paths);
        var results = new List<TestProcessResult>(invocations.Count);

        // Sequential on purpose. The invocations share one package root, one generation directory and
        // one compile cache, so running them together would have them overwrite each other's output.
        // The CT execution budget already runs one workspace at a time.
        foreach (var invocation in invocations)
            results.Add(await RunOneInvocationAsync(invocation.Command, cancellationToken).ConfigureAwait(false));

        return MergeRuns(request, paths, invocations, results);
    }

    /// <summary>
    /// Runs one invocation and makes a launch that never happened SAY SO.
    ///
    /// <para>A missing package manager reaches this provider as a raw platform exception - on Windows a
    /// <c>Win32Exception</c> naming nothing but "the system cannot find the file specified" - which is not
    /// a provider failure, carries no generation, and reached the dogfood run's operator only as a line in
    /// the daemon log while the run itself read as a bare <c>partial</c>. Restating it as a provider
    /// failure puts the executable, the directory and the platform's own reason on the run, where the
    /// coordinator stamps the generation onto it and terminalizes the run honestly.</para>
    ///
    /// <para>Cancellation and failures that already name themselves travel unchanged.</para>
    /// </summary>
    private async Task<TestProcessResult> RunOneInvocationAsync(
        TestProcessCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _runner.RunAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ContinuousTestProviderException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ContinuousTestProviderException(
                $"The JavaScript test run failed to launch '{command.FileName}' in "
                + $"'{command.WorkingDirectory}': {exception.Message.Trim()}",
                exception);
        }
    }

    /// <summary>
    /// Folds the invocations of one chunked run back into the single result the caller asked for. A
    /// one-invocation run parses exactly as it did before chunking existed.
    ///
    /// The worst status wins: every chunk's case results are aggregated together by
    /// <see cref="RunStatus"/>, so a green chunk can never mask a red sibling. A missing report fails
    /// its selection; a valid empty Node report remains unreported because no file-level verdict was emitted.
    /// </summary>
    private static ProviderRunResult MergeRuns(
        ContinuousTestProviderRunRequest request,
        CtGenerationPaths paths,
        IReadOnlyList<RunInvocation> invocations,
        IReadOnlyList<TestProcessResult> results)
    {
        var caseResults = new List<ProviderCaseResult>();
        for (var index = 0; index < invocations.Count; index++)
        {
            var invocation = invocations[index];
            var result = results[index];
            var artifactExists = File.Exists(invocation.ArtifactPath);
            IReadOnlyList<ProviderCaseResult> parsed = artifactExists
                ? ParseResultArtifact(request, invocation.TestCaseIds, invocation.ArtifactPath)
                : [];

            if (result.ExitCode != 0
                && parsed.Count == 0
                && (!artifactExists
                    || !string.Equals(RequiredFramework(request.Workspace), "node-test", StringComparison.Ordinal)))
            {
                if (invocation.TestCaseIds.Count == 0)
                    throw new ContinuousTestProviderException(
                        $"JavaScript test run failed with exit code {result.ExitCode}: {FailureSummary(result)}");

                parsed = FailedSelectedCaseResults(
                    request,
                    invocation.TestCaseIds,
                    result,
                    invocation.ArtifactPath);
            }

            caseResults.AddRange(parsed);
        }

        return new ProviderRunResult(
            RunId: request.RunId ?? NewRunId(request),
            Status: RunStatus(caseResults),
            CaseResults: caseResults,
            ResultArtifactPath: invocations
                .Select(static run => run.ArtifactPath)
                .FirstOrDefault(File.Exists),
            CoverageArtifacts: DiscoverCoverageArtifacts(request.Workspace))
        {
            GenerationId = paths.GenerationId,
        };
    }

    /// <summary>
    /// Preview/test seam: builds the run command against the latest existing generation (or the
    /// would-be first). Production runs never use it — <see cref="RunAsync"/> allocates its own
    /// generation and builds every command and result path from that one handle.
    /// </summary>
    public TestProcessCommand BuildRunCommand(ContinuousTestProviderRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return BuildRunCommand(request, CtGenerationPaths.ResolveLatestOrFirst(request.Workspace));
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

        return BuildRunInvocations(request, CtGenerationPaths.ResolveLatestOrFirst(request.Workspace))
            .Select(static invocation => invocation.Command)
            .ToArray();
    }

    private TestProcessCommand BuildRunCommand(
        ContinuousTestProviderRunRequest request,
        CtGenerationPaths paths)
        => BuildRunInvocations(request, paths)[0].Command;

    /// <summary>
    /// One invocation of a run: the command to launch, the result artifact it writes, and the selected
    /// test case ids that invocation alone is answerable for.
    /// </summary>
    private sealed record RunInvocation(
        TestProcessCommand Command,
        string ArtifactPath,
        IReadOnlyList<string> TestCaseIds);

    /// <summary>
    /// Builds the invocations for one run. Jest and Vitest selections that fit the command-line cap stay
    /// in one invocation, and wider selections split across invocations of the same runner. Node's JUnit
    /// reporter does not identify source files, so known node-test selections always use one invocation per
    /// file.
    ///
    /// The cap that matters here is 8,191, not the 32,767 Windows allows: npm, pnpm and yarn ship as
    /// <c>.cmd</c> shims, and cmd.exe applies its own much lower limit. It neither truncates nor
    /// throws — the shim exits 1 with "The command line is too long." on stderr and writes no report
    /// at all, which this provider would otherwise read as every selected test having failed.
    /// <see cref="CtArgvChunking.MaxSelectionBytesPerInvocation"/> is already sized under that cap.
    /// A machine whose manager resolves to an <c>.exe</c> shim instead (see
    /// <see cref="PackageManager"/>) gets the larger CreateProcessW cap, so the same budget is merely
    /// conservative there — the budget deliberately does NOT depend on which kind the probe found.
    /// </summary>
    private IReadOnlyList<RunInvocation> BuildRunInvocations(
        ContinuousTestProviderRunRequest request,
        CtGenerationPaths paths)
    {
        var framework = RequiredFramework(request.Workspace);
        var packageRoot = PackageRoot(request.Workspace);
        paths.EnsureDirectories();
        var cacheDirectory = CacheDirectory(request.Workspace);
        Directory.CreateDirectory(cacheDirectory);

        var selectedFiles = request.TestCaseIds
            .Select(TestFileFromId)
            .OfType<string>()
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        if (string.Equals(framework, "node-test", StringComparison.Ordinal) && selectedFiles.Length > 0)
        {
            var nodeInvocations = new List<RunInvocation>(selectedFiles.Length);
            for (var index = 0; index < selectedFiles.Length; index++)
            {
                var file = selectedFiles[index];
                var testCaseIds = request.TestCaseIds
                    .Where(testCaseId =>
                        string.Equals(TestFileFromId(testCaseId), file, StringComparison.Ordinal)
                        || (index == 0 && TestFileFromId(testCaseId) is null))
                    .ToArray();
                nodeInvocations.Add(BuildInvocation(
                    request,
                    paths,
                    framework,
                    packageRoot,
                    cacheDirectory,
                    [file],
                    testCaseIds,
                    part: selectedFiles.Length == 1 ? null : index));
            }

            return nodeInvocations;
        }

        if (request.WholeSuite || selectedFiles.Length == 0)
        {
            return [BuildInvocation(
                request, paths, framework, packageRoot, cacheDirectory, [], request.TestCaseIds, part: null)];
        }

        IReadOnlyList<IReadOnlyList<string>> chunks = CtArgvChunking.Chunk(
            selectedFiles,
            static file => CtArgvChunking.ArgvCost([file]));
        if (chunks.Count == 1)
        {
            return [BuildInvocation(
                request, paths, framework, packageRoot, cacheDirectory, chunks[0], request.TestCaseIds, part: null)];
        }

        var placedFiles = new HashSet<string>(selectedFiles, StringComparer.Ordinal);
        var invocations = new List<RunInvocation>(chunks.Count);
        for (var index = 0; index < chunks.Count; index++)
        {
            invocations.Add(BuildInvocation(
                request,
                paths,
                framework,
                packageRoot,
                cacheDirectory,
                chunks[index],
                InvocationTestCaseIds(request, chunks[index], placedFiles, isFirstInvocation: index == 0),
                part: index));
        }

        return invocations;
    }

    /// <summary>
    /// The selected ids one invocation of a split run is answerable for. Each id follows its own file,
    /// so a chunk that fails reports only the tests it tried to run, and the single-selected-id parse
    /// fallback cannot attribute one chunk's output to another chunk's test. An id that names no file
    /// in the selection cannot be placed by path, so it rides with the first invocation rather than
    /// being dropped from the report.
    /// </summary>
    private static IReadOnlyList<string> InvocationTestCaseIds(
        ContinuousTestProviderRunRequest request,
        IReadOnlyList<string> chunkFiles,
        IReadOnlySet<string> placedFiles,
        bool isFirstInvocation)
    {
        var files = new HashSet<string>(chunkFiles, StringComparer.Ordinal);
        return request.TestCaseIds
            .Where(testCaseId => TestFileFromId(testCaseId) is { } file && placedFiles.Contains(file)
                ? files.Contains(file)
                : isFirstInvocation)
            .ToArray();
    }

    private RunInvocation BuildInvocation(
        ContinuousTestProviderRunRequest request,
        CtGenerationPaths paths,
        string framework,
        string packageRoot,
        string cacheDirectory,
        IReadOnlyList<string> selectedFiles,
        IReadOnlyList<string> testCaseIds,
        int? part)
    {
        var artifactPath = ResultArtifactPath(request, paths, part);
        Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);

        var reporterArgs = IsolationArguments(framework, packageRoot, cacheDirectory)
            .Concat(ReporterArguments(framework, artifactPath))
            .Concat(selectedFiles)
            .ToArray();

        return new RunInvocation(
            BuildCommand(request, paths, framework, packageRoot, reporterArgs),
            artifactPath,
            testCaseIds);
    }

    private TestProcessCommand BuildCommand(
        ContinuousTestProviderRunRequest request,
        CtGenerationPaths paths,
        string framework,
        string packageRoot,
        string[] reporterArgs)
    {
        if (!string.IsNullOrWhiteSpace(request.Command))
        {
            var tokens = NodeCommandLine.SplitCommand(request.Command);
            if (tokens.Count == 0)
                throw new ContinuousTestProviderException("JavaScript test command must not be empty.");

            var args = tokens.Skip(1).ToList();
            if (RequiresPackageManagerArgumentSeparator(tokens[0]))
                args.Add("--");
            args.AddRange(reporterArgs);
            return new TestProcessCommand(tokens[0], args, packageRoot, WorkspaceEnvironment(request.Workspace, paths));
        }

        var selection = SelectPackageScript(packageRoot, framework);
        if (selection.Script is { } script)
        {
            var packageManager = PackageManager(packageRoot);
            var args = new List<string> { "run", script.Name };
            if (RequiresPackageManagerArgumentSeparator(packageManager))
                args.Add("--");
            args.AddRange(reporterArgs);
            return new TestProcessCommand(
                packageManager,
                args,
                packageRoot,
                WorkspaceEnvironment(request.Workspace, paths));
        }

        return BuildDirectRunnerCommand(
            request, paths, framework, packageRoot, reporterArgs, selection.RejectedScriptReason);
    }

    /// <summary>
    /// The run that goes straight to the runner binary, with no package script between it and the
    /// reporter arguments. Reached when the manifest names no usable script — either because it names none
    /// at all, or because <see cref="SelectPackageScript"/> refused the ones it names.
    /// </summary>
    private TestProcessCommand BuildDirectRunnerCommand(
        ContinuousTestProviderRunRequest request,
        CtGenerationPaths paths,
        string framework,
        string packageRoot,
        string[] reporterArgs,
        string? rejectedScriptReason) =>
        framework switch
        {
            "vitest" => new TestProcessCommand(
                RequiredLocalBin(packageRoot, "vitest", framework, request, rejectedScriptReason),
                new[] { "run" }.Concat(reporterArgs).ToArray(),
                packageRoot,
                WorkspaceEnvironment(request.Workspace, paths)),
            "jest" => new TestProcessCommand(
                RequiredLocalBin(packageRoot, "jest", framework, request, rejectedScriptReason),
                reporterArgs,
                packageRoot,
                WorkspaceEnvironment(request.Workspace, paths)),
            // node's runner needs no install: the same node that runs the project runs its tests.
            "node-test" => new TestProcessCommand(
                "node",
                new[] { "--test" }.Concat(reporterArgs).ToArray(),
                packageRoot,
                WorkspaceEnvironment(request.Workspace, paths)),
            _ => throw UnsupportedFramework(framework, request.Workspace.ProjectPath),
        };

    /// <summary>
    /// The workspace-local runner binary to launch.
    ///
    /// <para>When a package script was REJECTED, a missing binary means the run has no way to reach the
    /// runner at all, and it says so. Spawning the missing name instead would fail the run and hand every
    /// selected test file a failure summary taken from the launcher's own banner — a red attributed to
    /// tests that never ran, which is exactly the shape dogfood finding F10 recorded. When no script was
    /// rejected the name is returned unchecked, which keeps the long-standing behaviour: the spawn itself
    /// then reports what is missing, now with the reason on the run (see
    /// <see cref="RunOneInvocationAsync"/>).</para>
    /// </summary>
    private static string RequiredLocalBin(
        string packageRoot,
        string executableName,
        string framework,
        ContinuousTestProviderRunRequest request,
        string? rejectedScriptReason)
    {
        var localBin = LocalBin(packageRoot, executableName);
        if (rejectedScriptReason is null || File.Exists(localBin))
            return localBin;

        throw new ContinuousTestProviderException(
            $"Continuous testing cannot run the {framework} suite in '{request.Workspace.ProjectPath}': "
            + rejectedScriptReason
            + $" Running {framework} directly instead needs '{localBin}', which is not installed. "
            + "Install the project's dependencies, or set an explicit test command for this project.");
    }

    public static string TestCaseId(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) throw new ArgumentException("must not be empty", nameof(relativePath));
        return TestCaseIdPrefix + NormalizeRelativePath(relativePath);
    }

    /// <summary>
    /// Parses one invocation's report. <paramref name="testCaseIds"/> is the selection THAT invocation
    /// ran, not the whole request's: a chunk must only ever claim its own tests.
    /// </summary>
    private static IReadOnlyList<ProviderCaseResult> ParseResultArtifact(
        ContinuousTestProviderRunRequest request,
        IReadOnlyList<string> testCaseIds,
        string artifactPath)
    {
        var framework = RequiredFramework(request.Workspace);
        return framework switch
        {
            "vitest" or "jest" => ParseJestCompatibleJson(request, testCaseIds, artifactPath),
            "node-test" => ParseNodeJunit(request, testCaseIds, artifactPath),
            _ => throw UnsupportedFramework(framework, request.Workspace.ProjectPath),
        };
    }

    private static IReadOnlyList<ProviderCaseResult> ParseJestCompatibleJson(
        ContinuousTestProviderRunRequest request,
        IReadOnlyList<string> testCaseIds,
        string artifactPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(artifactPath));
        if (!document.RootElement.TryGetProperty("testResults", out var testResults)
            || testResults.ValueKind != JsonValueKind.Array)
            throw new ContinuousTestProviderException("JavaScript JSON test output did not contain a testResults array.");

        var packageRoot = PackageRoot(request.Workspace);
        var results = new List<ProviderCaseResult>();
        foreach (var fileResult in testResults.EnumerateArray())
        {
            var relativePath = RelativePathFromJsonResult(packageRoot, fileResult);
            var testCaseId = relativePath is null && testCaseIds.Count == 1
                ? testCaseIds[0]
                : relativePath is null
                    ? null
                    : TestCaseId(relativePath);
            if (string.IsNullOrWhiteSpace(testCaseId))
                continue;

            var assertionStatuses = AssertionStatuses(fileResult).ToArray();
            var status = FileStatus(fileResult, assertionStatuses);
            results.Add(new ProviderCaseResult(
                Id: StableId("test_result", request.Workspace.WorkspaceId, testCaseId, request.RunId),
                TestCaseId: testCaseId,
                Status: status,
                ResultRevision: request.SelectedRevision,
                IndexIdentity: request.IndexIdentity,
                DurationSeconds: DurationSeconds(fileResult),
                FailureSummary: FirstFailureSummary(fileResult),
                Metadata: new Dictionary<string, object?>
                {
                    ["artifact_path"] = artifactPath,
                    ["framework"] = RequiredFramework(request.Workspace),
                }));
        }

        return results;
    }

    /// <summary>
    /// Parses one node:test JUnit report for the selected invocation. Node's built-in reporter does not emit
    /// source file attributes, so known file selections are isolated to one invocation per file before this
    /// method is called. A report with file attributes remains supported for alternate reporters.
    ///
    /// <para>A selected file the report never names gets no result. An unattributed report is aggregated only
    /// when this invocation owns one selected case; multiple selected cases fail closed instead of sharing one
    /// verdict across file-level cases.</para>
    /// </summary>
    private static IReadOnlyList<ProviderCaseResult> ParseNodeJunit(
        ContinuousTestProviderRunRequest request,
        IReadOnlyList<string> testCaseIds,
        string artifactPath)
    {
        var parsed = JunitTestResultParser.Parse(artifactPath);
        if (testCaseIds.Count == 0)
            return [];

        var packageRoot = PackageRoot(request.Workspace);
        var casesByTestCaseId = new Dictionary<string, List<ParsedTestArtifactCase>>(StringComparer.Ordinal);
        var unattributedCases = new List<ParsedTestArtifactCase>();
        foreach (var row in parsed.Cases)
        {
            var relativePath = RelativePathFromReportedFile(packageRoot, row.File);
            if (relativePath is null)
            {
                unattributedCases.Add(row);
                continue;
            }

            var testCaseId = TestCaseId(relativePath);
            if (!casesByTestCaseId.TryGetValue(testCaseId, out var fileCases))
                casesByTestCaseId[testCaseId] = fileCases = [];
            fileCases.Add(row);
        }

        if (unattributedCases.Count > 0 && testCaseIds.Count > 1)
        {
            throw new ContinuousTestProviderException(
                "Node JUnit report did not include file attribution for multiple selected test cases; "
                + "run node-test one file per invocation.");
        }

        var results = new List<ProviderCaseResult>(testCaseIds.Count);
        foreach (var testCaseId in testCaseIds)
        {
            var cases = casesByTestCaseId.TryGetValue(testCaseId, out var fileCases)
                ? fileCases
                : unattributedCases;
            if (cases.Count == 0)
                continue;

            results.Add(new ProviderCaseResult(
                Id: StableId("test_result", request.Workspace.WorkspaceId, testCaseId, request.RunId),
                TestCaseId: testCaseId,
                Status: AggregateStatus(cases.Select(row => row.Status)),
                ResultRevision: request.SelectedRevision,
                IndexIdentity: request.IndexIdentity,
                DurationSeconds: cases
                    .Select(row => row.DurationSeconds)
                    .Where(durationSeconds => durationSeconds is not null)
                    .Sum(durationSeconds => durationSeconds!.Value),
                FailureSummary: cases
                    .Select(row => row.FailureText)
                    .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)),
                Metadata: new Dictionary<string, object?>
                {
                    ["artifact_path"] = artifactPath,
                    ["framework"] = RequiredFramework(request.Workspace),
                }));
        }

        return results;
    }

    /// <summary>
    /// The package-relative path of a file a report named, or null when it named none or named one outside
    /// the package. node writes the ABSOLUTE path; a relative one is read against the package root, which
    /// is the directory the run was launched in — never the calling process's working directory.
    /// </summary>
    private static string? RelativePathFromReportedFile(string packageRoot, string? reportedFile)
    {
        if (string.IsNullOrWhiteSpace(reportedFile))
            return null;

        var path = Path.IsPathRooted(reportedFile)
            ? reportedFile
            : Path.Combine(packageRoot, reportedFile);
        return RelativePathOrNull(packageRoot, path);
    }

    private static IEnumerable<string> AssertionStatuses(JsonElement fileResult)
    {
        if (!fileResult.TryGetProperty("assertionResults", out var assertions) || assertions.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var assertion in assertions.EnumerateArray())
        {
            if (OptionalString(assertion, "status") is { } status)
                yield return status;
        }
    }

    private static string FileStatus(JsonElement fileResult, IReadOnlyList<string> assertionStatuses)
    {
        if (assertionStatuses.Count > 0)
            return AggregateStatus(assertionStatuses.Select(NormalizeStatus));

        return NormalizeStatus(OptionalString(fileResult, "status") ?? "passed");
    }

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

    private static string RunStatus(IReadOnlyList<ProviderCaseResult> results) =>
        AggregateStatus(results.Select(row => row.Status));

    private static string NormalizeStatus(string status) =>
        status.ToLowerInvariant() switch
        {
            "fail" or "failed" or "failure" => "failed",
            "error" or "errored" => "errored",
            "skip" or "skipped" or "pending" or "todo" => "skipped",
            _ => "passed",
        };

    private static string? FirstFailureSummary(JsonElement fileResult)
    {
        if (!fileResult.TryGetProperty("assertionResults", out var assertions) || assertions.ValueKind != JsonValueKind.Array)
            return OptionalString(fileResult, "message");

        foreach (var assertion in assertions.EnumerateArray())
        {
            if (NormalizeStatus(OptionalString(assertion, "status") ?? "passed") != "failed")
                continue;

            if (assertion.TryGetProperty("failureMessages", out var messages)
                && messages.ValueKind == JsonValueKind.Array)
            {
                foreach (var message in messages.EnumerateArray())
                {
                    if (message.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(message.GetString()))
                        return message.GetString();
                }
            }

            if (OptionalString(assertion, "failureMessage") is { } failureMessage)
                return failureMessage;
        }

        return null;
    }

    private static double? DurationSeconds(JsonElement fileResult)
    {
        if (fileResult.TryGetProperty("perfStats", out var perfStats)
            && perfStats.ValueKind == JsonValueKind.Object
            && perfStats.TryGetProperty("runtime", out var runtime)
            && runtime.ValueKind == JsonValueKind.Number
            && runtime.TryGetDouble(out var runtimeMilliseconds))
            return runtimeMilliseconds / 1000.0;

        return null;
    }

    private static string? RelativePathFromJsonResult(string packageRoot, JsonElement fileResult)
    {
        var path = OptionalString(fileResult, "name")
            ?? OptionalString(fileResult, "testFilePath")
            ?? OptionalString(fileResult, "filepath");
        if (string.IsNullOrWhiteSpace(path))
            return null;

        return RelativePathOrNull(packageRoot, path);
    }

    private static string RequiredFramework(ContinuousTestWorkspace workspace)
    {
        var framework = ResolvedFrameworkOrNull(workspace);
        return string.IsNullOrWhiteSpace(framework)
            ? throw UnsupportedFramework("<unspecified>", workspace.ProjectPath)
            : framework;
    }

    /// <summary>
    /// The framework this workspace runs, or null when neither the workspace nor the manifest names one.
    /// Discovery asks in this non-throwing form: a directory with no manifest and no test file has no
    /// framework and no cases, and that is an empty answer rather than an error.
    /// </summary>
    private static string? ResolvedFrameworkOrNull(ContinuousTestWorkspace workspace) =>
        workspace.Framework?.Trim().ToLowerInvariant() ?? DetectFramework(PackageRoot(workspace));

    private static string[] ReporterArguments(string framework, string artifactPath) =>
        framework switch
        {
            "vitest" => ["--reporter=json", "--outputFile", artifactPath],
            "jest" => ["--json", "--outputFile", artifactPath],
            "node-test" => ["--test-reporter", "junit", "--test-reporter-destination", artifactPath],
            _ => throw UnsupportedFramework(framework, artifactPath),
        };

    /// <summary>
    /// The flags that isolate a framework's cache state, using a private directory where supported and disabling it otherwise.
    ///
    /// Vitest 0.x omits cache flags because its CLI rejects them. Installed Vitest 1.x through 3.x uses
    /// <c>--cache.dir</c>; Vitest 4.x and newer use the supported boolean <c>--cache=false</c>.
    ///
    /// When the installed version cannot be read or parsed, the flag is omitted. The run then shares
    /// vitest's default cache directory instead of this generation's private one, which loses cache
    /// isolation between concurrent generations — a recoverable loss, where a rejected flag is a
    /// guaranteed failed run.
    /// </summary>
    private static string[] IsolationArguments(string framework, string packageRoot, string cacheDirectory) =>
        framework switch
        {
            "vitest" => InstalledPackageMajorVersion(packageRoot, "vitest") switch
            {
                >= 4 => ["--cache=false"],
                >= 1 => ["--cache.dir", cacheDirectory],
                _ => [],
            },
            "jest" => ["--cacheDirectory", cacheDirectory],
            _ => [],
        };

    /// <summary>
    /// The result artifact for one invocation. <paramref name="part"/> is null for a run that fits a
    /// single command line — which keeps the filename byte-identical to the pre-chunking one — and is
    /// the zero-based invocation index when a run is split, so chunk N cannot overwrite chunk N-1's
    /// report and every part stays on disk as evidence.
    /// </summary>
    private static string ResultArtifactPath(
        ContinuousTestProviderRunRequest request,
        CtGenerationPaths paths,
        int? part = null)
    {
        var framework = RequiredFramework(request.Workspace);
        var runKey = request.RunId ?? NewRunId(request);
        var runHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(runKey))).ToLowerInvariant();
        var suffix = part is null ? string.Empty : $".part{part.Value.ToString("D3", CultureInfo.InvariantCulture)}";
        return Path.Combine(
            paths.ResultsDirectory,
            $"run-{runHash}{suffix}.{(framework == "node-test" ? "xml" : "json")}");
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

    private static string PackageRoot(ContinuousTestWorkspace workspace)
    {
        var projectPath = Path.GetFullPath(workspace.ProjectPath);
        return string.Equals(Path.GetFileName(projectPath), "package.json", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(projectPath)!
            : Directory.Exists(projectPath)
                ? projectPath
                : Path.GetDirectoryName(projectPath)!;
    }

    private static string? DetectFramework(string packageRoot)
    {
        var packageJsonPath = Path.Combine(packageRoot, "package.json");
        if (!File.Exists(packageJsonPath))
            return null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
            var root = document.RootElement;
            if (HasPackage(root, "vitest") || ScriptContains(root, "vitest"))
                return "vitest";
            if (HasPackage(root, "jest")
                || HasPackage(root, "@vue/cli-plugin-unit-jest")
                || ScriptContains(root, "jest")
                || ScriptContains(root, "vue-cli-service test:unit"))
                return "jest";
            if (AnyScript(root, IsNodeTestRunnerCommand))
                return "node-test";
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void ValidateSupportedDiscoveryVersion(string? framework, string packageRoot)
    {
        var packageName = framework switch
        {
            "jest" => "jest",
            "vitest" => "vitest",
            _ => null,
        };
        if (packageName is null)
            return;

        var evidence = JsTestConfigPatterns.ReadRunnerVersionEvidence(packageRoot, packageName);
        if (evidence.InstalledManifestFound && IsSupportedDiscoveryVersion(framework!, evidence.InstalledVersion))
            return;
        if (!evidence.InstalledManifestFound
            && evidence.Diagnostic is null
            && evidence.DependencyRanges.Count > 0
            && evidence.DependencyRanges.All(range => IsSupportedDependencyRange(framework!, range)))
            return;

        var detected = evidence.InstalledManifestFound
            ? string.IsNullOrWhiteSpace(evidence.InstalledVersion) ? "unknown" : evidence.InstalledVersion
            : evidence.DependencyRanges.Count == 0 ? "unknown" : string.Join(", ", evidence.DependencyRanges);
        var reason = evidence.Diagnostic is null
            ? "the installed version or dependency range cannot be proven safe"
            : evidence.Diagnostic;
        if (!evidence.InstalledManifestFound && evidence.DependencyRanges.Count > 0)
            reason = "the dependency range is not wholly inside the supported version interval";
        if (evidence.InstalledManifestFound && evidence.InstalledVersion is not null)
            reason = "the installed version is outside the supported version interval";
        var supported = framework == "jest" ? "29.x or 30.x" : "0.34.x through 4.x";
        throw new ContinuousTestProviderException(
            $"JavaScript {framework} runner version evidence '{detected}' is unsupported for CT discovery: {reason}."
            + $" Supported versions are {supported}; install a supported {framework} version under '{packageRoot}'.");
    }

    private static bool IsSupportedDiscoveryVersion(string framework, string? version)
    {
        if (!TryParseVersion(version, out var major, out var minor, out _))
            return false;

        return IsSupportedVersion(framework, major, minor);
    }

    private static bool IsSupportedDependencyRange(string framework, string range)
    {
        var value = range.Trim();
        var operatorLength = value.StartsWith('^') || value.StartsWith('~') ? 1 : 0;
        var hasRangeOperator = operatorLength > 0;
        if (!hasRangeOperator && value.Any(character => character is '*' or 'x' or 'X' or '|' or '>' or '<' or '='))
            return false;
        if (!TryParseVersion(value[operatorLength..], out var major, out var minor, out var patch))
            return false;

        if (!hasRangeOperator)
            return IsSupportedVersion(framework, major, minor);
        if (!IsSupportedVersion(framework, major, minor))
            return false;

        var upperMajor = major;
        var upperMinor = minor;
        if (value[0] == '^')
        {
            if (major == 0)
                upperMinor++;
            else
                upperMajor++;
        }
        else
        {
            upperMinor++;
        }

        return framework switch
        {
            "jest" => upperMajor <= 31,
            "vitest" => major == 0
                ? minor >= 34 && upperMajor == 0
                : upperMajor <= 5,
            _ => false,
        };
    }

    private static bool IsSupportedVersion(string framework, int major, int minor) =>
        framework switch
        {
            "jest" => major is 29 or 30,
            "vitest" => major is >= 1 and <= 4 || major == 0 && minor >= 34,
            _ => false,
        };

    private static bool TryParseVersion(string? text, out int major, out int minor, out int patch)
    {
        major = 0;
        minor = 0;
        patch = 0;
        if (string.IsNullOrWhiteSpace(text) || text.Contains('-') || text.Contains('+'))
            return false;
        var core = text;
        var parts = core.Split('.');
        return parts.Length == 3
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out major)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minor)
            && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out patch);
    }

    /// <summary>One package script: the name a package manager runs, and what it runs.</summary>
    private sealed record PackageScript(string Name, string Command);

    /// <summary>
    /// The package script a run may be routed through, or the reason there is none. Both are null when the
    /// manifest simply names no script for this framework — an absence, not a refusal.
    /// </summary>
    private sealed record PackageScriptSelection(PackageScript? Script, string? RejectedScriptReason);

    /// <summary>
    /// Chooses the package script to route a run through, or refuses.
    ///
    /// <para>A package manager appends everything after <c>--</c> to the END of the script, so a script is
    /// usable only when arguments appended there reach the runner. Three shapes fail that test:</para>
    /// <list type="bullet">
    /// <item>a script that CHAINS commands — <c>a &amp;&amp; b</c> hands the reporter flags to <c>b</c>
    /// alone, so every other command in the chain runs unreported and the report the provider reads is
    /// never written;</item>
    /// <item>a script the chained <c>test</c> entry point invokes — the halves of a chain are fragments of
    /// one suite, and running a fragment silently covers part of it under whatever environment that
    /// fragment configures;</item>
    /// <item>a node:test script that already names a test path — Node stops reading options at the first
    /// positional argument, so the appended reporter flags arrive as more paths
    /// (<see cref="NodeTestFileDiscovery.SuppliesPositionalArguments"/>).</item>
    /// </list>
    /// <para>vercel/ms is the first two shapes at once: <c>test</c> chains <c>test:nodejs</c> and
    /// <c>test:edge</c>, and this provider ran <c>test:edge</c> alone, produced no report, and marked all
    /// four of its test files red with the launcher's banner as every failure summary — while both halves
    /// pass by hand (dogfood finding F10, 2026-08-21). A rejection sends the run to the runner binary
    /// instead.</para>
    /// </summary>
    private static PackageScriptSelection SelectPackageScript(string packageRoot, string framework)
    {
        var scripts = ReadPackageScripts(packageRoot);
        if (scripts.Count == 0)
            return new PackageScriptSelection(null, null);

        var entryPoint = scripts.FirstOrDefault(script => script.Name == "test");
        var chainedEntryPoint = entryPoint is not null && NodeCommandLine.IsChained(entryPoint.Command)
            ? entryPoint
            : null;
        var fragments = chainedEntryPoint is null
            ? []
            : ChainedScriptFragments(chainedEntryPoint.Command);

        var candidates = scripts
            .Where(script => ScriptMatchesFramework(script.Name, script.Command, framework))
            .OrderBy(script => ScriptPreference(script.Name))
            .ThenBy(script => script.Name, StringComparer.Ordinal)
            .ToArray();

        var usable = candidates
            .Where(script => !fragments.Contains(script.Name)
                && ScriptRejectionReason(script, framework) is null)
            .ToArray();
        if (usable.Length > 0)
            return new PackageScriptSelection(usable[0], null);

        // The chained entry point names the real obstacle when there is one: the sibling scripts are only
        // unusable because they are its fragments.
        var reason = chainedEntryPoint is not null
            ? ScriptRejectionReason(chainedEntryPoint, framework)
            : candidates
                .Select(script => ScriptRejectionReason(script, framework))
                .FirstOrDefault(text => text is not null);
        return new PackageScriptSelection(null, reason);
    }

    /// <summary>
    /// Why this script cannot carry the appended reporter and isolation arguments, or null when it can.
    /// </summary>
    private static string? ScriptRejectionReason(PackageScript script, string framework)
    {
        if (NodeCommandLine.IsChained(script.Command))
        {
            return $"the '{script.Name}' package script chains commands (\"{script.Command}\"), so the "
                + $"reporter and isolation arguments continuous testing appends cannot reach {framework}.";
        }

        if (framework == "node-test" && NodeTestFileDiscovery.SuppliesPositionalArguments(script.Command))
        {
            return $"the '{script.Name}' package script already names test paths (\"{script.Command}\") and "
                + "node stops reading options at the first path, so the reporter arguments continuous "
                + "testing appends cannot reach the runner.";
        }

        return null;
    }

    private static IReadOnlyList<PackageScript> ReadPackageScripts(string packageRoot)
    {
        var packageJsonPath = Path.Combine(packageRoot, "package.json");
        if (!File.Exists(packageJsonPath))
            return [];

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
            if (!document.RootElement.TryGetProperty("scripts", out var scripts)
                || scripts.ValueKind != JsonValueKind.Object)
                return [];

            return scripts.EnumerateObject()
                .Where(script => script.Value.ValueKind == JsonValueKind.String)
                .Select(script => new PackageScript(script.Name, script.Value.GetString() ?? string.Empty))
                .ToArray();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// The script names a chained script invokes, from the <c>run &lt;name&gt;</c> pair each of its
    /// segments carries. Those names are the fragments of one suite, not suites of their own.
    /// </summary>
    private static HashSet<string> ChainedScriptFragments(string command)
    {
        var fragments = new HashSet<string>(StringComparer.Ordinal);
        foreach (var segment in NodeCommandLine.SplitChainedSegments(command))
        {
            var tokens = NodeCommandLine.SplitCommand(segment);
            for (var index = 0; index + 1 < tokens.Count; index++)
            {
                if (tokens[index] is "run" or "run-script")
                    fragments.Add(tokens[index + 1]);
            }
        }

        return fragments;
    }

    private static bool ScriptMatchesFramework(string name, string command, string framework)
    {
        if (name.Contains("e2e", StringComparison.OrdinalIgnoreCase)
            || command.Contains("cypress", StringComparison.OrdinalIgnoreCase)
            || command.Contains("playwright", StringComparison.OrdinalIgnoreCase))
            return false;

        return framework switch
        {
            "vitest" => command.Contains("vitest", StringComparison.OrdinalIgnoreCase),
            "jest" => command.Contains("jest", StringComparison.OrdinalIgnoreCase)
                || command.Contains("vue-cli-service test:unit", StringComparison.OrdinalIgnoreCase),
            "node-test" => IsNodeTestRunnerCommand(command),
            _ => false,
        };
    }

    private static int ScriptPreference(string name) =>
        name switch
        {
            "test" => 0,
            "test:unit" => 1,
            "unit" => 2,
            _ => 10,
        };

    /// <summary>
    /// The package manager to run the test script through, as a launchable file name.
    ///
    /// On Windows a bare <c>"npm"</c> reaches <c>Process.Start</c> with <c>UseShellExecute=false</c>,
    /// which searches PATH for that exact name and for <c>npm.exe</c> - and the extensionless file the
    /// Node MSI installs beside <c>npm.cmd</c> is a shell script for MSYS/Git Bash, not a Windows
    /// executable. So the bare name found nothing and every script-based Node CT run failed to start
    /// with <c>Win32Exception: The system cannot find the file specified</c>.
    ///
    /// A hard-coded <c>.cmd</c> suffix does not fix that; it only moves the failure. A machine that
    /// manages Node with Volta or Chocolatey has <c>npm.exe</c> on PATH and no <c>npm.cmd</c> anywhere,
    /// and a name that already carries an extension stops CreateProcessW appending <c>.exe</c> - so the
    /// suffix breaks the installs that used to work. Probe PATH for what is really there instead, and
    /// fall back to the bare name, which is exactly the pre-suffix behaviour, when nothing is found.
    /// <see cref="LocalBin"/> keeps its unconditional <c>.cmd</c>: it names a file inside
    /// node_modules/.bin, where npm always writes a <c>.cmd</c> shim, so there is nothing to probe.
    /// </summary>
    private string PackageManager(string packageRoot)
    {
        string manager =
            File.Exists(Path.Combine(packageRoot, "pnpm-lock.yaml")) ? "pnpm"
            : File.Exists(Path.Combine(packageRoot, "yarn.lock")) ? "yarn"
            : "npm";
        return _findPackageManagerOnPath(manager) ?? manager;
    }

    /// <summary>
    /// Extensions a Windows package-manager shim can carry, in probe order. <c>.cmd</c> comes first
    /// because npm, pnpm and yarn author their own <c>.cmd</c> shims, and because the chunk budget this
    /// provider splits selections under assumes the 8,191-character cmd.exe cap. Resolving an
    /// <c>.exe</c> shim instead only leaves that budget conservative, which is safe.
    /// </summary>
    private static readonly string[] WindowsPackageManagerExtensions = [".cmd", ".exe", ".bat"];

    /// <summary>
    /// The default probe: on Windows, the launchable package-manager file PATH really holds; elsewhere
    /// null, because a bare name is resolved through PATH by the platform itself.
    /// </summary>
    private static string? FindPackageManagerOnSystemPath(string manager) =>
        OperatingSystem.IsWindows()
            ? FindPackageManagerOnPath(manager, SystemPathDirectories(), File.Exists)
            : null;

    /// <summary>
    /// Finds the launchable file for a bare package-manager name in <paramref name="searchDirectories"/>,
    /// or null when none of them holds one. Pure and injectable so a test can state the PATH and the
    /// files on it rather than depend on the developer's Node install.
    /// </summary>
    internal static string? FindPackageManagerOnPath(
        string manager,
        IReadOnlyList<string> searchDirectories,
        Func<string, bool> fileExists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manager);
        ArgumentNullException.ThrowIfNull(searchDirectories);
        ArgumentNullException.ThrowIfNull(fileExists);

        foreach (var extension in WindowsPackageManagerExtensions)
        {
            foreach (var directory in searchDirectories)
            {
                if (string.IsNullOrWhiteSpace(directory))
                    continue;

                // Path.Join, not Path.Combine: it never validates and never throws, so one malformed
                // PATH entry cannot take down every Node run.
                var candidate = Path.Join(directory.Trim('"'), manager + extension);
                if (fileExists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> SystemPathDirectories() =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool HasPackage(JsonElement root, string packageName)
    {
        foreach (var propertyName in new[] { "dependencies", "devDependencies", "peerDependencies", "optionalDependencies" })
        {
            if (root.TryGetProperty(propertyName, out var dependencies)
                && dependencies.ValueKind == JsonValueKind.Object
                && dependencies.TryGetProperty(packageName, out _))
                return true;
        }

        return false;
    }

    /// <summary>
    /// True when a package script command launches node's OWN test runner. The runner is named by the
    /// <c>--test</c> flag or by the <c>node:test</c> module, so the flag is matched as a WHOLE argument:
    /// other runners spell options that start with the same six characters
    /// (<c>--testPathPattern</c>, <c>--testNamePattern</c>) and none of them starts node's runner. The
    /// bare word "node" is never the signal either — <c>node build.js</c> builds.
    ///
    /// One rule, three readers: this decides the framework of a project whose framework is unspecified,
    /// which package script a run goes through, and — through
    /// <see cref="ContinuousTestProjectInventory"/> — whether the project is discovered at all. They must
    /// not drift.
    /// </summary>
    internal static bool IsNodeTestRunnerCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return false;

        if (command.Contains("node:test", StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var token in command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(token, "--test", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool AnyScript(JsonElement root, Func<string?, bool> predicate)
    {
        if (!root.TryGetProperty("scripts", out var scripts) || scripts.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var script in scripts.EnumerateObject())
        {
            if (script.Value.ValueKind == JsonValueKind.String && predicate(script.Value.GetString()))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The major version of a package as INSTALLED under <paramref name="packageRoot"/>, or null when no
    /// installed manifest is there and when its version cannot be read or parsed. The installed manifest
    /// is the only honest source: a dependency range such as <c>"^0.29.8"</c> in the workspace manifest
    /// names what was asked for, not what the install resolved.
    /// </summary>
    internal static int? InstalledPackageMajorVersion(string packageRoot, string packageName)
    {
        var manifestPath = Path.Combine(packageRoot, "node_modules", packageName, "package.json");
        if (!File.Exists(manifestPath))
            return null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!document.RootElement.TryGetProperty("version", out var version)
                || version.ValueKind != JsonValueKind.String)
                return null;

            var text = version.GetString() ?? string.Empty;
            var separator = text.IndexOf('.');
            var head = separator < 0 ? text : text[..separator];
            return int.TryParse(head, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
                ? major
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool ScriptContains(JsonElement root, string value)
    {
        if (!root.TryGetProperty("scripts", out var scripts) || scripts.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var script in scripts.EnumerateObject())
        {
            if (script.Value.ValueKind == JsonValueKind.String
                && script.Value.GetString()?.Contains(value, StringComparison.OrdinalIgnoreCase) == true)
                return true;
        }

        return false;
    }

    private static IReadOnlyList<ProviderCoverageArtifact> DiscoverCoverageArtifacts(ContinuousTestWorkspace workspace)
    {
        var artifacts = new List<ProviderCoverageArtifact>();
        var paths = new HashSet<string>(PathStringComparer);
        var packageRoot = PackageRoot(workspace);
        AddCoverageArtifact(
            artifacts,
            paths,
            Path.Combine(packageRoot, "coverage", "lcov.info"),
            "lcov",
            packageRoot);
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

    private static string LocalBin(string packageRoot, string executableName) =>
        Path.Combine(
            packageRoot,
            "node_modules",
            ".bin",
            executableName + (OperatingSystem.IsWindows() ? ".cmd" : ""));

    private static IReadOnlyDictionary<string, string?> WorkspaceEnvironment(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths)
    {
        Directory.CreateDirectory(paths.TempDirectory);
        var cacheDirectory = CacheDirectory(workspace);
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
            ["NODE_COMPILE_CACHE"] = cacheDirectory,
        };
    }

    /// <summary>
    /// The transform/compile cache for vitest, jest and node, PROJECT-stable rather than per-generation.
    ///
    /// <para>It used to live inside the generation, so every operation started from an empty cache and
    /// re-transformed every file — the node half of finding F7. It now sits beside the generations under the
    /// build output root, where each framework's own cache keys decide what stays valid. Results, reports and
    /// temp stay per-operation.</para>
    /// </summary>
    private static string CacheDirectory(ContinuousTestWorkspace workspace) =>
        CtGenerationPaths.CacheDirectory(workspace, "node");

    private static string? TestFileFromId(string testCaseId) =>
        testCaseId.StartsWith(TestCaseIdPrefix, StringComparison.Ordinal)
            ? NormalizeRelativePath(testCaseId[TestCaseIdPrefix.Length..])
            : null;

    /// <summary>
    /// Whether one workspace-relative file is a test case of this project.
    ///
    /// <para>The generated-directory exclusions apply first. Everything else is
    /// <paramref name="matches"/>: node:test uses Node's documented patterns (dogfood finding F8);
    /// jest and vitest use theirs, including jest's <c>__tests__/</c> default and a literal config
    /// array when one is readable.</para>
    /// </summary>
    private static bool IsDiscoverableTestFile(string relativePath, Func<string, bool> matches)
    {
        var segments = relativePath.Split('/');
        if (segments.Any(IsExcludedSegment))
            return false;

        return matches(relativePath);
    }

    private static bool IsExcludedSegment(string segment) =>
        segment.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
        || segment.Equals(".git", StringComparison.OrdinalIgnoreCase)
        || segment.Equals(".miller", StringComparison.OrdinalIgnoreCase)
        || segment.Equals(".claude", StringComparison.OrdinalIgnoreCase);

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
                    ["framework"] = RequiredFramework(request.Workspace),
                    ["exit_code"] = result.ExitCode,
                }))
            .ToArray();
    }

    private static string FailureSummary(TestProcessResult result)
    {
        var text = !string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardError
            : result.StandardOutput;
        text = text.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return $"JavaScript test run failed with exit code {result.ExitCode}.";

        var firstLine = text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(firstLine)
            ? $"JavaScript test run failed with exit code {result.ExitCode}."
            : firstLine;
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

    private static string? OptionalString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static bool RequiresPackageManagerArgumentSeparator(string executable)
    {
        var name = Path.GetFileNameWithoutExtension(executable).ToLowerInvariant();
        return name is "npm";
    }

    private static ContinuousTestProviderException UnsupportedFramework(string framework, string projectPath) =>
        new($"Continuous test framework '{framework}' is unsupported for JavaScript project '{projectPath}'.");

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
            IFormattable formattable => formattable.ToString(format: null, System.Globalization.CultureInfo.InvariantCulture) ?? "",
            _ => part.ToString() ?? "",
        };

    private static StringComparer PathStringComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
