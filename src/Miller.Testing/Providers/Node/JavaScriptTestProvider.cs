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

        var cases = Directory
            .EnumerateFiles(packageRoot, "*", SearchOption.AllDirectories)
            .Select(path => RelativePathOrNull(packageRoot, path))
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
            results.Add(await _runner.RunAsync(invocation.Command, cancellationToken).ConfigureAwait(false));

        return MergeRuns(request, paths, invocations, results);
    }

    /// <summary>
    /// Folds the invocations of one chunked run back into the single result the caller asked for. A
    /// one-invocation run parses exactly as it did before chunking existed.
    ///
    /// The worst status wins: every chunk's case results are aggregated together by
    /// <see cref="RunStatus"/>, so a green chunk can never mask a red sibling. The exit code is judged
    /// PER invocation, because each chunk is a separate process with its own report - a chunk that
    /// produced no verdicts must report its own selection as failed rather than borrow a sibling's.
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
            IReadOnlyList<ProviderCaseResult> parsed = File.Exists(invocation.ArtifactPath)
                ? ParseResultArtifact(request, invocation.TestCaseIds, invocation.ArtifactPath)
                : [];

            if (result.ExitCode != 0 && parsed.Count == 0)
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
    /// Builds the invocations for one run. A selection that fits the command-line cap is a single
    /// command — same argv and same artifact filename as this provider sent before chunking existed —
    /// and a wider one is split across several invocations of the same runner.
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
        var cacheDirectory = CacheDirectory(paths);
        Directory.CreateDirectory(cacheDirectory);

        var selectedFiles = request.TestCaseIds
            .Select(TestFileFromId)
            .OfType<string>()
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        // An empty file selection is the unfiltered whole-suite run. It carries no selection argv, so
        // there is nothing to chunk and it keeps the whole request's ids.
        if (selectedFiles.Length == 0)
        {
            return [BuildInvocation(
                request, paths, framework, packageRoot, cacheDirectory, [], request.TestCaseIds, part: null)];
        }

        // Each unit is a single argv element — a bare file path with no flag beside it — so the
        // primitive's whole-unit rule costs nothing here. What it buys is the byte budget, shared with
        // every other provider so one bound governs them all.
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
            var tokens = SplitCommand(request.Command);
            if (tokens.Count == 0)
                throw new ContinuousTestProviderException("JavaScript test command must not be empty.");

            var args = tokens.Skip(1).ToList();
            if (RequiresPackageManagerArgumentSeparator(tokens[0]))
                args.Add("--");
            args.AddRange(reporterArgs);
            return new TestProcessCommand(tokens[0], args, packageRoot, WorkspaceEnvironment(request.Workspace, paths));
        }

        if (DetectPackageScript(packageRoot, framework) is { } scriptName)
        {
            var args = new List<string> { "run", scriptName, "--" };
            args.AddRange(reporterArgs);
            return new TestProcessCommand(
                PackageManager(packageRoot),
                args,
                packageRoot,
                WorkspaceEnvironment(request.Workspace, paths));
        }

        return framework switch
        {
            "vitest" => new TestProcessCommand(
                LocalBin(packageRoot, "vitest"),
                new[] { "run" }.Concat(reporterArgs).ToArray(),
                packageRoot,
                WorkspaceEnvironment(request.Workspace, paths)),
            "jest" => new TestProcessCommand(
                LocalBin(packageRoot, "jest"),
                reporterArgs,
                packageRoot,
                WorkspaceEnvironment(request.Workspace, paths)),
            "node-test" => new TestProcessCommand(
                "node",
                new[] { "--test" }.Concat(reporterArgs).ToArray(),
                packageRoot,
                WorkspaceEnvironment(request.Workspace, paths)),
            _ => throw UnsupportedFramework(framework, request.Workspace.ProjectPath),
        };
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

    private static IReadOnlyList<ProviderCaseResult> ParseNodeJunit(
        ContinuousTestProviderRunRequest request,
        IReadOnlyList<string> testCaseIds,
        string artifactPath)
    {
        var parsed = JunitTestResultParser.Parse(artifactPath);
        if (testCaseIds.Count == 0)
            return [];

        var status = AggregateStatus(parsed.Cases.Select(row => row.Status));
        var duration = parsed.Cases
            .Select(row => row.DurationSeconds)
            .Where(durationSeconds => durationSeconds is not null)
            .Sum(durationSeconds => durationSeconds!.Value);
        var failureSummary = parsed.Cases
            .Select(row => row.FailureText)
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
        return testCaseIds
            .Select(testCaseId => new ProviderCaseResult(
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
                    ["framework"] = RequiredFramework(request.Workspace),
                }))
            .ToArray();
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
        var framework = workspace.Framework?.Trim().ToLowerInvariant()
            ?? DetectFramework(PackageRoot(workspace));
        return string.IsNullOrWhiteSpace(framework)
            ? throw UnsupportedFramework("<unspecified>", workspace.ProjectPath)
            : framework;
    }

    private static string[] ReporterArguments(string framework, string artifactPath) =>
        framework switch
        {
            "vitest" => ["--reporter=json", "--outputFile", artifactPath],
            "jest" => ["--json", "--outputFile", artifactPath],
            "node-test" => ["--test-reporter", "junit", "--test-reporter-destination", artifactPath],
            _ => throw UnsupportedFramework(framework, artifactPath),
        };

    /// <summary>
    /// The flags that move a framework's cache into this generation's private directory.
    ///
    /// Vitest's dot-notation options (<c>--cache.dir</c>) arrived in vitest 1.x. Vitest 0.29.8 stops at
    /// its CLI parser with <c>CACError: Unknown option `--cache`</c> and runs NOTHING, so every selected
    /// file came back failed on a real 0.x workspace. The flag therefore goes on the command line only
    /// when the INSTALLED major is 1 or newer.
    ///
    /// When the installed version cannot be read or parsed, the flag is omitted. The run then shares
    /// vitest's default cache directory instead of this generation's private one, which loses cache
    /// isolation between concurrent generations — a recoverable loss, where a rejected flag is a
    /// guaranteed failed run.
    /// </summary>
    private static string[] IsolationArguments(string framework, string packageRoot, string cacheDirectory) =>
        framework switch
        {
            "vitest" => InstalledPackageMajorVersion(packageRoot, "vitest") is int major && major >= 1
                ? ["--cache.dir", cacheDirectory]
                : [],
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

    private static string? DetectPackageScript(string packageRoot, string framework)
    {
        var packageJsonPath = Path.Combine(packageRoot, "package.json");
        if (!File.Exists(packageJsonPath))
            return null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
            if (!document.RootElement.TryGetProperty("scripts", out var scripts)
                || scripts.ValueKind != JsonValueKind.Object)
                return null;

            return scripts.EnumerateObject()
                .Where(script => script.Value.ValueKind == JsonValueKind.String)
                .Where(script => ScriptMatchesFramework(script.Name, script.Value.GetString() ?? string.Empty, framework))
                .OrderBy(script => ScriptPreference(script.Name))
                .ThenBy(script => script.Name, StringComparer.Ordinal)
                .Select(script => script.Name)
                .FirstOrDefault();
        }
        catch (JsonException)
        {
            return null;
        }
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
            ["NODE_COMPILE_CACHE"] = cacheDirectory,
        };
    }

    private static string CacheDirectory(CtGenerationPaths paths) =>
        Path.Combine(paths.GenerationRoot, "cache");

    private static string? TestFileFromId(string testCaseId) =>
        testCaseId.StartsWith(TestCaseIdPrefix, StringComparison.Ordinal)
            ? NormalizeRelativePath(testCaseId[TestCaseIdPrefix.Length..])
            : null;

    private static bool IsDiscoverableTestFile(string relativePath)
    {
        var segments = relativePath.Split('/');
        if (segments.Any(IsExcludedSegment))
            return false;

        var extension = Path.GetExtension(relativePath).ToLowerInvariant();
        if (extension is not (".js" or ".jsx" or ".ts" or ".tsx" or ".mjs" or ".cjs" or ".mts" or ".cts"))
            return false;

        var stem = Path.GetFileNameWithoutExtension(relativePath);
        return stem.EndsWith(".test", StringComparison.OrdinalIgnoreCase)
            || stem.EndsWith(".spec", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExcludedSegment(string segment) =>
        segment is "node_modules"
            or ".git"
            or ".claude"
            or "dist"
            or "build"
            or ".next"
            or "coverage"
            or "e2e"
            or "cypress"
            or "playwright";

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

    private static bool RequiresPackageManagerArgumentSeparator(string executable)
    {
        var name = Path.GetFileNameWithoutExtension(executable).ToLowerInvariant();
        return name is "npm" or "pnpm" or "yarn";
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
