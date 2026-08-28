using Miller.Testing.Parsing;

namespace Miller.Testing.Providers.Qml;

internal sealed class QmakeQtQuickTestBackend : IQtQuickTestBackend
{
    private const string VersionFileName = ".qt-version";
    private const int MaxProjectCharacters = 64 * 1024;

    private readonly ITestProcessRunner _runner;
    private readonly string _qmakePath;
    private readonly string _makePath;

    public QmakeQtQuickTestBackend(
        ITestProcessRunner runner,
        string qmakePath = "qmake",
        string makePath = "make")
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        if (string.IsNullOrWhiteSpace(qmakePath))
            throw new ArgumentException("must not be empty", nameof(qmakePath));
        if (string.IsNullOrWhiteSpace(makePath))
            throw new ArgumentException("must not be empty", nameof(makePath));
        _qmakePath = qmakePath;
        _makePath = makePath;
    }

    public string Discriminator => QtQuickTestBackendIds.Qmake;

    public async Task EnsureBuildAsync(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(paths);
        ValidateProject(workspace.ProjectPath);
        _ = RequireProjectModel(workspace.ProjectPath);
        paths.EnsureDirectories();
        if (HasValidBuildTree(paths))
            return;

        TestProcessResult qmakeVersionResult = await RunProcessAsync(
            new TestProcessCommand(
                _qmakePath,
                QmakeQuickTestTooling.BuildVersionArguments(),
                paths.OutDir,
                EnvironmentFor(workspace, paths)),
            cancellationToken).ConfigureAwait(false);
        RequireComplete(qmakeVersionResult, "qmake -v");
        QtVersion qmakeVersion = QmakeQuickTestTooling.ParseQmakeVersion(qmakeVersionResult);

        TestProcessResult qtVersionResult = await RunProcessAsync(
            new TestProcessCommand(
                _qmakePath,
                QmakeQuickTestTooling.BuildQtVersionArguments(),
                paths.OutDir,
                EnvironmentFor(workspace, paths)),
            cancellationToken).ConfigureAwait(false);
        RequireComplete(qtVersionResult, "qmake Qt version query");
        QtVersion queriedVersion = QmakeQuickTestTooling.ParseQtVersion(qtVersionResult);
        if (qmakeVersion != queriedVersion)
            throw Failure(
                $"qmake reported Qt {qmakeVersion}, but qmake -query reported Qt {queriedVersion}; "
                + "the Qt toolchain is inconsistent.");

        TestProcessResult makeVersionResult = await RunProcessAsync(
            new TestProcessCommand(
                _makePath,
                QmakeQuickTestTooling.BuildMakeVersionArguments(_makePath),
                paths.OutDir,
                EnvironmentFor(workspace, paths)),
            cancellationToken).ConfigureAwait(false);
        RequireComplete(makeVersionResult, "make --version");
        if (makeVersionResult.ExitCode != 0)
            throw Failure(
                $"make --version failed with exit code {makeVersionResult.ExitCode}: {FailureSummary(makeVersionResult)}");

        TestProcessResult configure = await RunProcessAsync(
            new TestProcessCommand(
                _qmakePath,
                QmakeQuickTestTooling.BuildConfigureArguments(workspace.ProjectPath, paths.OutDir),
                paths.OutDir,
                EnvironmentFor(workspace, paths)),
            cancellationToken).ConfigureAwait(false);
        RequireComplete(configure, "qmake configure");
        if (configure.ExitCode != 0)
            throw Failure(
                $"qmake configure failed with exit code {configure.ExitCode}: {FailureSummary(configure)}");

        string makefilePath = Path.Combine(paths.OutDir, "Makefile");
        string makefile = ReadBounded(makefilePath, "generated qmake Makefile");
        if (!QmakeQuickTestTooling.HasCheckTarget(makefile))
            throw Failure(
                $"qmake project '{workspace.ProjectPath}' did not generate a usable check target. "
                + "Add CONFIG += testcase (or CONFIG += qmltestcase) and use a supported qmake project.");

        TestProcessResult build = await RunProcessAsync(
            new TestProcessCommand(
                _makePath,
                QmakeQuickTestTooling.BuildBuildArguments(),
                paths.OutDir,
                EnvironmentFor(workspace, paths)),
            cancellationToken).ConfigureAwait(false);
        RequireComplete(build, "qmake build");
        if (build.ExitCode != 0)
            throw Failure($"qmake build failed with exit code {build.ExitCode}: {FailureSummary(build)}");
        WriteVersion(paths, queriedVersion);
    }

    public Task<IReadOnlyList<QtQuickTestCase>> DiscoverAsync(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(paths);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateProject(workspace.ProjectPath);

        string makefile = ReadBounded(Path.Combine(paths.OutDir, "Makefile"), "generated qmake Makefile");
        if (!QmakeQuickTestTooling.HasCheckTarget(makefile))
            throw Failure(
                $"qmake project '{workspace.ProjectPath}' has no usable generated check target; build capability was not proven.");

        QmakeProjectModel project = RequireProjectModel(workspace.ProjectPath);
        string target = QmakeQuickTestTooling.ParseTarget(project.EffectiveText, project.RootPath);
        string executable = Path.Combine(
            paths.OutDir,
            OperatingSystem.IsWindows() ? target + ".exe" : target);
        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["backend"] = Discriminator,
            ["qmake_target"] = target,
            ["qmake_project"] = workspace.ProjectPath,
            ["imports"] = QmakeQuickTestTooling.ParseImportPaths(project.EffectiveText, project.RootDirectory),
        };
        return Task.FromResult<IReadOnlyList<QtQuickTestCase>>(
        [
            new QtQuickTestCase(
                target,
                [executable],
                [],
                paths.OutDir,
                metadata),
        ]);
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
        if (!IsInside(paths.ResultsDirectory, artifactPath))
            throw Failure(
                $"qmake QTest result artifact '{artifactPath}' must remain under generation results '{paths.ResultsDirectory}'.",
                artifactPath);
        if (!wholeSuite && selectedNames.Count == 0)
            throw Failure("qmake Quick Test run selected no target and did not request the whole suite.", artifactPath);

        QmakeProjectModel project = RequireProjectModel(request.Workspace.ProjectPath);
        string target = QmakeQuickTestTooling.ParseTarget(project.EffectiveText, project.RootPath);
        if (!wholeSuite && selectedNames.Any(name => !string.Equals(name, target, StringComparison.Ordinal)))
            throw Failure(
                $"qmake Quick Test cannot select a target that is not the generated check target '{target}'.",
                artifactPath);

        QtVersion version = ReadVersion(paths);
        var processResult = await RunProcessAsync(
            new TestProcessCommand(
                _makePath,
                QmakeQuickTestTooling.BuildCheckArguments(
                    artifactPath,
                    version,
                    QmakeQuickTestTooling.ParseImportPaths(project.EffectiveText, project.RootDirectory)),
                paths.OutDir,
                EnvironmentFor(request.Workspace, paths)),
            cancellationToken).ConfigureAwait(false);
        RequireComplete(processResult, "qmake check");
        if (processResult.ExitCode != 0 && !File.Exists(artifactPath))
            throw Failure(
                $"qmake check failed with exit code {processResult.ExitCode}: {FailureSummary(processResult)}",
                artifactPath);
        if (!File.Exists(artifactPath))
            throw Failure(
                $"qmake check completed without producing QTest artifact '{artifactPath}'.",
                artifactPath);

        ParsedTestArtifactRun parsed;
        try
        {
            parsed = QTestResultParser.Parse(artifactPath);
        }
        catch (Exception exception) when (exception is ContinuousTestProviderException or TestArtifactParseException or IOException or UnauthorizedAccessException)
        {
            throw Failure(
                $"qmake QTest artifact '{artifactPath}' could not be parsed: {exception.Message}",
                artifactPath,
                exception);
        }

        if (processResult.ExitCode != 0 && !parsed.Cases.Any(testCase =>
                testCase.Status is "failed" or "errored"))
        {
            throw Failure(
                $"qmake check failed with exit code {processResult.ExitCode}: {FailureSummary(processResult)}",
                artifactPath);
        }

        return new QtQuickTestBackendRunResult(
            artifactPath,
            [new QtQuickTestBackendCaseResult(
                target,
                AggregateStatus(parsed.Cases.Select(testCase => testCase.Status)),
                parsed.Cases
                    .Select(testCase => testCase.DurationSeconds)
                    .Where(duration => duration is not null)
                    .Sum(duration => duration ?? 0),
                parsed.Cases
                    .Select(testCase => testCase.FailureText)
                    .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)),
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["qmake_target"] = target,
                    ["qtest_selectors"] = parsed.Cases.Select(testCase => testCase.Selector).ToArray(),
                })]);
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
                $"The Qt Quick Test qmake command failed to launch '{command.FileName}' in "
                + $"'{command.WorkingDirectory}': {exception.Message.Trim()}",
                exception);
        }
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

    private static void ValidateProject(string path)
    {
        if (!File.Exists(path))
            throw Failure($"qmake Quick Test project '{path}' does not exist.");
        if (!string.Equals(Path.GetExtension(path), ".pro", StringComparison.OrdinalIgnoreCase))
            throw Failure($"qmake Quick Test project '{path}' is not a .pro file.");
    }

    private static string ReadBounded(string path, string context)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > MaxProjectCharacters)
                throw Failure($"{context} '{path}' exceeds the {MaxProjectCharacters}-character evidence limit.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (FileNotFoundException exception)
        {
            throw Failure($"{context} '{path}' was not produced by the qmake build: {exception.Message}", innerException: exception);
        }
        catch (DirectoryNotFoundException exception)
        {
            throw Failure($"{context} '{path}' was not produced by the qmake build: {exception.Message}", innerException: exception);
        }
        catch (IOException exception)
        {
            throw Failure($"Could not read {context} '{path}': {exception.Message}", innerException: exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw Failure($"Could not read {context} '{path}': {exception.Message}", innerException: exception);
        }
    }

    private static QmakeProjectModel RequireProjectModel(string path)
    {
        if (QmakeQuickTestTooling.TryReadProjectModel(path, out QmakeProjectModel? model)
            && model is not null)
            return model;
        throw Failure(
            $"qmake project '{path}' could not be read as a bounded effective project model. "
            + "Only literal in-root .pri includes are supported; dynamic, out-of-root, missing, or oversized includes are unavailable.");
    }

    private static bool HasValidBuildTree(CtGenerationPaths paths) =>
        File.Exists(Path.Combine(paths.OutDir, "Makefile"))
        && File.Exists(Path.Combine(paths.GenerationRoot, VersionFileName));

    private static void WriteVersion(CtGenerationPaths paths, QtVersion version)
    {
        try
        {
            File.WriteAllText(Path.Combine(paths.GenerationRoot, VersionFileName), version.ToString());
        }
        catch (IOException exception)
        {
            throw Failure($"Could not record the qmake Qt version under '{paths.GenerationRoot}': {exception.Message}", innerException: exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw Failure($"Could not record the qmake Qt version under '{paths.GenerationRoot}': {exception.Message}", innerException: exception);
        }
    }

    private static QtVersion ReadVersion(CtGenerationPaths paths)
    {
        string text = ReadBounded(Path.Combine(paths.GenerationRoot, VersionFileName), "qmake Qt version marker");
        return QmakeQuickTestTooling.ParseQtVersion(text);
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

    private static string FailureSummary(TestProcessResult result)
    {
        var text = !string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardError
            : result.StandardOutput;
        return string.IsNullOrWhiteSpace(text) ? "no diagnostic output" : text.Trim();
    }

    private static bool IsInside(string root, string path)
    {
        string relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative == "."
            || (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative));
    }

    private static string AggregateStatus(IEnumerable<string> statuses)
    {
        var statusSet = statuses.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (statusSet.Count == 0)
            throw new ContinuousTestProviderException(
                "Qt Quick Test qmake run produced no case results; an empty run cannot be reported green.");
        if (statusSet.Contains("failed") || statusSet.Contains("errored"))
            return "failed";
        if (statusSet.Count > 0 && statusSet.All(status => status is "skipped" or "skip"))
            return "skipped";
        return "passed";
    }
}
