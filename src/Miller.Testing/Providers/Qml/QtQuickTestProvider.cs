using System.Collections.Immutable;
using System.Text;
using Miller.Testing.Parsing;

namespace Miller.Testing.Providers.Qml;

public sealed class QtQuickTestProvider : IContinuousTestProvider
{
    private const string Framework = "qt-quick-test";
    private const string ProviderSource = "ct-provider:qml";
    private const string ProjectIdMetadataKey = "project_id";
    private const string ConfigurationMetadataKey = "configuration";

    private readonly ITestProcessRunner _runner;
    private readonly string _cmakePath;
    private readonly string _ctestPath;
    private readonly CtGenerationHandoff _generations = new();

    public QtQuickTestProvider(
        ITestProcessRunner runner,
        string cmakePath = "cmake",
        string ctestPath = "ctest")
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        if (string.IsNullOrWhiteSpace(cmakePath))
            throw new ArgumentException("must not be empty", nameof(cmakePath));
        if (string.IsNullOrWhiteSpace(ctestPath))
            throw new ArgumentException("must not be empty", nameof(ctestPath));
        _cmakePath = cmakePath;
        _ctestPath = ctestPath;
    }

    public async Task<IReadOnlyList<ProviderTestCase>> DiscoverAsync(
        ContinuousTestWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ValidateWorkspace(workspace);

        var paths = _generations.AllocateForDiscovery(workspace);
        try
        {
            return await DiscoverInGenerationAsync(workspace, paths, cancellationToken).ConfigureAwait(false);
        }
        catch (ContinuousTestProviderException exception) when (exception.GenerationId is null)
        {
            throw StampGeneration(exception, paths);
        }
    }

    public async Task<ProviderRunResult> RunAsync(
        ContinuousTestProviderRunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateWorkspace(request.Workspace);
        if (request.CoverageMode != ContinuousTestCoverageMode.None)
            throw new ContinuousTestProviderException(
                "Qt Quick Test continuous testing does not support coverage instrumentation.");
        if (!request.WholeSuite && request.TestCaseIds.Count == 0)
            throw new ContinuousTestProviderException(
                "Qt Quick Test run selected no test cases and did not request the whole suite.");

        var paths = _generations.TakeForRun(request.Workspace);
        try
        {
            return await RunInGenerationAsync(request, paths, cancellationToken).ConfigureAwait(false);
        }
        catch (ContinuousTestProviderException exception) when (exception.GenerationId is null)
        {
            throw StampGeneration(exception, paths);
        }
    }

    public static string TestCaseId(ContinuousTestWorkspace workspace, string testName)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return TestCaseId(ProjectIdentity(workspace), testName);
    }

    public static string TestCaseId(string projectId, string testName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(testName);
        return $"qml-test:{CtStableIds.StableId("qml-project", projectId)}:{Encode(testName)}";
    }

    private async Task<IReadOnlyList<ProviderTestCase>> DiscoverInGenerationAsync(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths,
        CancellationToken cancellationToken)
    {
        await EnsureBuildAsync(workspace, paths, cancellationToken).ConfigureAwait(false);
        var discovery = await DiscoverTargetsAsync(workspace, paths, cancellationToken).ConfigureAwait(false);
        return discovery.Tests
            .Select(test => ToProviderTestCase(workspace, test))
            .OrderBy(test => test.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<ProviderRunResult> RunInGenerationAsync(
        ContinuousTestProviderRunRequest request,
        CtGenerationPaths paths,
        CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        string runId = request.RunId ?? NewRunId(request, paths.GenerationId);
        await EnsureBuildAsync(request.Workspace, paths, cancellationToken).ConfigureAwait(false);

        string artifactPath = ResultArtifactPath(paths, runId);
        DeleteArtifact(artifactPath);

        var selectedNames = request.WholeSuite
            ? []
            : request.TestCaseIds
                .Select(id => DecodeTestCaseId(request.Workspace, id))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToImmutableArray();
        var command = new TestProcessCommand(
            _ctestPath,
            QtQuickTestTooling.BuildCTestRunArguments(
                paths.OutDir,
                artifactPath,
                selectedNames,
                request.WholeSuite,
                ConfigurationFor(request.Workspace)),
            paths.OutDir,
            EnvironmentFor(request.Workspace, paths));
        var processResult = await RunProcessAsync(command, cancellationToken).ConfigureAwait(false);
        RequireComplete(processResult, "CTest run");
        if (processResult.ExitCode != 0)
            throw Failure(
                $"CTest run failed with exit code {processResult.ExitCode}: {FailureSummary(processResult)}",
                artifactPath);
        if (!File.Exists(artifactPath))
            throw Failure($"CTest run completed without producing JUnit artifact '{artifactPath}'.", artifactPath);

        ParsedTestArtifactRun parsed;
        try
        {
            parsed = JunitTestResultParser.Parse(artifactPath);
        }
        catch (Exception exception) when (exception is TestArtifactParseException or IOException or UnauthorizedAccessException)
        {
            throw Failure($"CTest JUnit artifact '{artifactPath}' could not be parsed: {exception.Message}", artifactPath, exception);
        }

        if (parsed.Cases.Count == 0)
            throw Failure("CTest JUnit artifact contained zero test cases.", artifactPath);

        var selectedIds = request.TestCaseIds.ToHashSet(StringComparer.Ordinal);
        var results = parsed.Cases
            .GroupBy(testCase => testCase.Name, StringComparer.Ordinal)
            .Select(group => ToProviderCaseResult(request, runId, artifactPath, group.Key, group, selectedIds))
            .Where(result => result is not null)
            .Select(result => result!)
            .OrderBy(result => result.TestCaseId, StringComparer.Ordinal)
            .ToArray();
        var missing = request.TestCaseIds
            .Except(results.Select(result => result.TestCaseId), StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (missing.Length > 0)
            throw Failure(
                $"CTest JUnit artifact did not report selected test cases: {string.Join(", ", missing)}",
                artifactPath);
        if (results.Length == 0)
            throw Failure("CTest JUnit artifact contained no selected test cases.", artifactPath);

        return new ProviderRunResult(
            RunId: runId,
            Status: AggregateStatus(results.Select(result => result.Status)),
            StartedAt: started,
            EndedAt: DateTimeOffset.UtcNow,
            CaseResults: results,
            ResultArtifactPath: artifactPath,
            TestDisplayNames: parsed.Cases.Select(testCase => testCase.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray())
        {
            GenerationId = paths.GenerationId,
        };
    }

    private async Task EnsureBuildAsync(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths,
        CancellationToken cancellationToken)
    {
        paths.EnsureDirectories();
        if (HasValidBuildTree(paths))
            return;

        string? configuration = ConfigurationFor(workspace);

        var version = await RunProcessAsync(
            new TestProcessCommand(
                _cmakePath,
                QtQuickTestTooling.BuildCMakeVersionArguments(),
                ConfigureRoot(workspace),
                EnvironmentFor(workspace, paths)),
            cancellationToken).ConfigureAwait(false);
        QtQuickTestTooling.ParseCMakeVersion(version);

        var configure = await RunProcessAsync(
            new TestProcessCommand(
                _cmakePath,
                QtQuickTestTooling.BuildCMakeConfigureArguments(
                    ConfigureRoot(workspace),
                    paths.OutDir,
                    configuration),
                ConfigureRoot(workspace),
                EnvironmentFor(workspace, paths)),
            cancellationToken).ConfigureAwait(false);
        RequireComplete(configure, "CMake configure");
        if (configure.ExitCode != 0)
            throw Failure(
                $"CMake configure failed with exit code {configure.ExitCode}: {FailureSummary(configure)}");

        var build = await RunProcessAsync(
            new TestProcessCommand(
                _cmakePath,
                QtQuickTestTooling.BuildCMakeBuildArguments(paths.OutDir, configuration),
                ConfigureRoot(workspace),
                EnvironmentFor(workspace, paths)),
            cancellationToken).ConfigureAwait(false);
        RequireComplete(build, "CMake build");
        if (build.ExitCode != 0)
            throw Failure($"CMake build failed with exit code {build.ExitCode}: {FailureSummary(build)}");

        if (!HasValidBuildTree(paths))
            throw Failure($"CMake build did not produce a valid CTest build tree under '{paths.OutDir}'.");
    }

    private async Task<TestProcessResult> RunProcessAsync(
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
                $"The Qt Quick Test command failed to launch '{command.FileName}' in "
                + $"'{command.WorkingDirectory}': {exception.Message.Trim()}",
                exception);
        }
    }

    private async Task<CTestDiscoveryResult> DiscoverTargetsAsync(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths,
        CancellationToken cancellationToken)
    {
        var result = await RunProcessAsync(
            new TestProcessCommand(
                _ctestPath,
                QtQuickTestTooling.BuildCTestDiscoveryArguments(paths.OutDir, ConfigurationFor(workspace)),
                paths.OutDir,
                EnvironmentFor(workspace, paths)),
            cancellationToken).ConfigureAwait(false);
        return CTestDiscoveryParser.Parse(result);
    }

    private static ProviderTestCase ToProviderTestCase(
        ContinuousTestWorkspace workspace,
        CTestDiscoveredTest test)
    {
        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["language"] = "qml",
            ["provider_source"] = ProviderSource,
            ["ctest_name"] = test.Name,
            ["command"] = test.Command,
            ["labels"] = test.Labels,
            ["working_directory"] = test.WorkingDirectory,
        };
        foreach ((string key, object? value) in test.Metadata)
            metadata[$"ctest.{key}"] = value;

        return new ProviderTestCase(
            Id: TestCaseId(workspace, test.Name),
            DisplayName: test.Name,
            FullyQualifiedName: test.Name,
            Selector: test.Name,
            Framework: Framework,
            SourcePath: workspace.Metadata.TryGetValue("evidence_root", out object? evidenceRoot)
                ? evidenceRoot as string
                : null,
            Metadata: metadata);
    }

    private static ProviderCaseResult? ToProviderCaseResult(
        ContinuousTestProviderRunRequest request,
        string runId,
        string artifactPath,
        string testName,
        IEnumerable<ParsedTestArtifactCase> cases,
        IReadOnlySet<string> selectedIds)
    {
        string testCaseId = TestCaseId(request.Workspace, testName);
        if (selectedIds.Count > 0 && !selectedIds.Contains(testCaseId))
            return null;

        var rows = cases.ToArray();
        return new ProviderCaseResult(
            Id: CtStableIds.StableId("test_result", request.Workspace.WorkspaceId, testCaseId, runId),
            TestCaseId: testCaseId,
            Status: AggregateStatus(rows.Select(row => row.Status)),
            ResultRevision: request.SelectedRevision,
            IndexIdentity: request.IndexIdentity,
            DurationSeconds: rows
                .Select(row => row.DurationSeconds)
                .Where(value => value is not null)
                .Sum(value => value ?? 0),
            FailureSummary: rows.Select(row => row.FailureText).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)),
            Metadata: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["artifact_path"] = artifactPath,
                ["framework"] = Framework,
                ["provider_source"] = ProviderSource,
                ["ctest_name"] = testName,
            });
    }

    private static string DecodeTestCaseId(ContinuousTestWorkspace workspace, string id)
    {
        string prefix = $"qml-test:{CtStableIds.StableId("qml-project", ProjectIdentity(workspace))}:";
        if (!id.StartsWith(prefix, StringComparison.Ordinal))
            throw new ContinuousTestProviderException($"Qt Quick Test case id '{id}' does not belong to this project.");

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(
                id[prefix.Length..].Replace('-', '+').Replace('_', '/')
                + new string('=', (4 - id[prefix.Length..].Length % 4) % 4)));
        }
        catch (FormatException exception)
        {
            throw new ContinuousTestProviderException($"Qt Quick Test case id '{id}' is malformed.", exception);
        }
    }

    private static string Encode(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static string ProjectIdentity(ContinuousTestWorkspace workspace) =>
        workspace.Metadata.TryGetValue(ProjectIdMetadataKey, out object? value)
        && value is string projectId
        && !string.IsNullOrWhiteSpace(projectId)
            ? projectId
            : workspace.BuildOutputRoot;

    private static string? ConfigurationFor(ContinuousTestWorkspace workspace)
    {
        if (workspace.Metadata.TryGetValue(ConfigurationMetadataKey, out object? value)
            && value is string configuration
            && !string.IsNullOrWhiteSpace(configuration))
        {
            return configuration.Trim();
        }

        return OperatingSystem.IsWindows() ? "Release" : null;
    }

    private static string ConfigureRoot(ContinuousTestWorkspace workspace)
    {
        if (!workspace.Metadata.TryGetValue("configure_root", out object? value)
            || value is not string root
            || string.IsNullOrWhiteSpace(root))
            throw new ContinuousTestProviderException(
                "Qt Quick Test project metadata is missing configure_root.");
        string fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot))
            throw new ContinuousTestProviderException(
                $"Qt Quick Test configure root '{fullRoot}' does not exist.");
        return fullRoot;
    }

    private static IReadOnlyDictionary<string, string?> EnvironmentFor(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths)
    {
        Directory.CreateDirectory(paths.TempDirectory);
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [CtEnvironment.WorkspaceRoot] = workspace.WorkspaceRoot,
            [CtEnvironment.DaemonWorkspaceRoot] = null,
            ["TMPDIR"] = paths.TempDirectory,
            ["TMP"] = paths.TempDirectory,
            ["TEMP"] = paths.TempDirectory,
        };
        string? platform = Environment.GetEnvironmentVariable("QT_QPA_PLATFORM");
        if (platform is not null)
            environment["QT_QPA_PLATFORM"] = platform;
        return QtQuickTestTooling.WithDefaultQtPlatform(environment);
    }

    private static bool HasValidBuildTree(CtGenerationPaths paths) =>
        File.Exists(Path.Combine(paths.OutDir, "CMakeCache.txt"))
        && File.Exists(Path.Combine(paths.OutDir, "CTestTestfile.cmake"));

    private static void ValidateWorkspace(ContinuousTestWorkspace workspace)
    {
        if (!string.Equals(workspace.Framework, Framework, StringComparison.OrdinalIgnoreCase))
            throw new ContinuousTestProviderException(
                $"Qt Quick Test provider requires framework '{Framework}'.");
        _ = ConfigureRoot(workspace);
    }

    private static string ResultArtifactPath(CtGenerationPaths paths, string runId) =>
        Path.Combine(paths.ResultsDirectory, $"run-{CtStableIds.StableId("qml-run", runId).Split(':')[1]}.junit.xml");

    private static string NewRunId(ContinuousTestProviderRunRequest request, string generationId) =>
        CtStableIds.StableId(
            "ct_run",
            request.Workspace.WorkspaceId,
            request.Workspace.ProjectPath,
            request.SelectedRevision,
            generationId);

    private static void DeleteArtifact(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException exception)
        {
            throw Failure($"Could not remove previous CTest JUnit artifact '{path}': {exception.Message}", path, exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw Failure($"Could not remove previous CTest JUnit artifact '{path}': {exception.Message}", path, exception);
        }
    }

    private static void RequireComplete(TestProcessResult result, string context)
    {
        if (result.StandardOutputTruncated)
            _ = result.RequireCompleteStandardOutput(context);
        if (result.StandardErrorTruncated)
            throw new ContinuousTestProviderException(
                $"{context} wrote more standard error than the capture cap retains, so part of it was elided.");
    }

    private static ContinuousTestProviderException Failure(
        string message,
        string? artifactPath = null,
        Exception? innerException = null)
        => innerException is null
            ? new ContinuousTestProviderException(message)
            {
                ResultArtifactPath = artifactPath,
            }
            : new ContinuousTestProviderException(message, innerException)
            {
                ResultArtifactPath = artifactPath,
            };

    private static ContinuousTestProviderException StampGeneration(
        ContinuousTestProviderException exception,
        CtGenerationPaths paths) =>
        new(exception.Message, exception)
        {
            GenerationId = paths.GenerationId,
            ResultArtifactPath = exception.ResultArtifactPath,
        };

    private static string FailureSummary(TestProcessResult result)
    {
        var text = !string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardError
            : result.StandardOutput;
        return string.IsNullOrWhiteSpace(text) ? "no diagnostic output" : text.Trim();
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

    private static string NormalizeStatus(string status) =>
        status.ToLowerInvariant() switch
        {
            "fail" or "failed" or "failure" => "failed",
            "error" or "errored" => "errored",
            "skip" or "skipped" or "pending" or "todo" => "skipped",
            _ => "passed",
        };
}
