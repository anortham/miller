using Miller.Testing.Parsing;

namespace Miller.Testing.Providers.Qml;

internal sealed class CMakeQtQuickTestBackend : IQtQuickTestBackend
{
    private const string BuildCompletionMarkerFileName = ".cmake-build-complete";
    private readonly ITestProcessRunner _runner;
    private readonly string _cmakePath;
    private readonly string _ctestPath;

    public CMakeQtQuickTestBackend(
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

    public string Discriminator => QtQuickTestBackendIds.CMake;

    public async Task EnsureBuildAsync(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(paths);
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

        if (!HasConfiguredBuildTree(paths))
            throw Failure($"CMake build did not produce a valid CTest build tree under '{paths.OutDir}'.");
        File.WriteAllText(Path.Combine(paths.GenerationRoot, BuildCompletionMarkerFileName), string.Empty);
    }

    public async Task<IReadOnlyList<QtQuickTestCase>> DiscoverAsync(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(paths);
        var result = await RunProcessAsync(
            new TestProcessCommand(
                _ctestPath,
                QtQuickTestTooling.BuildCTestDiscoveryArguments(paths.OutDir, ConfigurationFor(workspace)),
                paths.OutDir,
                EnvironmentFor(workspace, paths)),
            cancellationToken).ConfigureAwait(false);
        var discovery = CTestDiscoveryParser.Parse(result);
        return discovery.Tests
            .Select(test =>
            {
                var metadata = new Dictionary<string, object?>(test.Metadata, StringComparer.Ordinal)
                {
                    ["ctest_name"] = test.Name,
                };
                foreach ((string key, object? value) in test.Metadata)
                    metadata[$"ctest.{key}"] = value;
                return new QtQuickTestCase(
                    test.Name,
                    test.Command,
                    test.Labels,
                    test.WorkingDirectory,
                    metadata);
            })
            .ToArray();
    }

    public async Task<QtQuickTestBackendRunResult> RunAsync(
        ContinuousTestProviderRunRequest request,
        CtGenerationPaths paths,
        string artifactPath,
        IReadOnlyList<string> selectedNames,
        bool wholeSuite,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactPath);
        ArgumentNullException.ThrowIfNull(selectedNames);

        var processResult = await RunProcessAsync(
            new TestProcessCommand(
                _ctestPath,
                QtQuickTestTooling.BuildCTestRunArguments(
                    paths.OutDir,
                    artifactPath,
                    selectedNames,
                    wholeSuite,
                    ConfigurationFor(request.Workspace)),
                paths.OutDir,
                EnvironmentFor(request.Workspace, paths)),
            cancellationToken).ConfigureAwait(false);
        RequireComplete(processResult, "CTest run");
        if (processResult.ExitCode != 0 && !File.Exists(artifactPath))
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
            throw Failure(
                $"CTest JUnit artifact '{artifactPath}' could not be parsed: {exception.Message}",
                artifactPath,
                exception);
        }

        if (parsed.Cases.Count == 0)
            throw Failure("CTest JUnit artifact contained zero test cases.", artifactPath);
        if (processResult.ExitCode != 0 && !parsed.Cases.Any(testCase =>
                testCase.Status is "failed" or "errored"))
            throw Failure(
                $"CTest run failed with exit code {processResult.ExitCode}: {FailureSummary(processResult)}",
                artifactPath);

        return new QtQuickTestBackendRunResult(
            artifactPath,
            parsed.Cases
                .Select(testCase => new QtQuickTestBackendCaseResult(
                    testCase.Name,
                    testCase.Status,
                    testCase.DurationSeconds,
                    testCase.FailureText,
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["ctest_name"] = testCase.Name,
                    }))
                .ToArray());
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

    private static string? ConfigurationFor(ContinuousTestWorkspace workspace)
    {
        if (workspace.Metadata.TryGetValue("configuration", out object? value)
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
        HasConfiguredBuildTree(paths)
        && File.Exists(Path.Combine(paths.GenerationRoot, BuildCompletionMarkerFileName));

    private static bool HasConfiguredBuildTree(CtGenerationPaths paths) =>
        File.Exists(Path.Combine(paths.OutDir, "CMakeCache.txt"))
        && File.Exists(Path.Combine(paths.OutDir, "CTestTestfile.cmake"));

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

    private static string FailureSummary(TestProcessResult result)
    {
        var text = !string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardError
            : result.StandardOutput;
        return string.IsNullOrWhiteSpace(text) ? "no diagnostic output" : text.Trim();
    }
}
