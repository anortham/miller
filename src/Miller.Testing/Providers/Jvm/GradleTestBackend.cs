using System.Collections.ObjectModel;
using Miller.Testing.Parsing;
using Miller.Testing.Providers.Shared;

namespace Miller.Testing.Providers.Jvm;

internal sealed class GradleTestBackend : IJvmTestBackend
{
    private readonly ITestProcessRunner _runner;

    public GradleTestBackend(ITestProcessRunner runner)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    public string Discriminator => JvmTestBackendIds.Gradle;

    public Task EnsureBuildAsync(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(paths);
        paths.EnsureDirectories();
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<JvmTestBackendCase>> DiscoverAsync(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(paths);
        paths.EnsureDirectories();
        ClearReports(paths);

        TestProcessResult processResult = await _runner
            .RunAsync(BuildDiscoveryCommand(workspace, paths), cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<string> reportPaths = ReportPaths(paths);
        if (processResult.ExitCode != 0)
            throw Failure($"Gradle test dry-run exited with code {processResult.ExitCode}: {ProcessSummary(processResult)}");

        IReadOnlyList<JvmTestBackendCaseResult> rows = ParseReports(reportPaths, paths);
        if (rows.Count == 0)
            throw Failure("Gradle test dry-run produced no JUnit test cases.");
        return rows
            .Select(row => new JvmTestBackendCase(
                row.ClassName,
                row.MethodName,
                row.Selector,
                SourcePath: row.Metadata?.GetValueOrDefault("source_path") as string,
                Metadata: row.Metadata))
            .OrderBy(test => test.Selector, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<JvmTestBackendRunResult> RunAsync(
        ContinuousTestProviderRunRequest request,
        CtGenerationPaths paths,
        IReadOnlyList<JvmTestSelection> selected,
        bool wholeSuite,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(selected);
        paths.EnsureDirectories();
        if (!wholeSuite && selected.Count == 0)
            throw Failure("Gradle run selected no test cases and did not request the whole suite.");

        IReadOnlyList<TestProcessCommand> commands = BuildRunCommands(request, paths, selected, wholeSuite);
        var rows = new List<JvmTestBackendCaseResult>();
        string? lastArtifactPath = null;
        int exitCode = 0;
        foreach (TestProcessCommand command in commands)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClearReports(paths);
            TestProcessResult processResult = await _runner
                .RunAsync(command, cancellationToken)
                .ConfigureAwait(false);
            exitCode = processResult.ExitCode != 0 ? processResult.ExitCode : exitCode;
            IReadOnlyList<string> reportPaths = ReportPaths(paths);
            if (reportPaths.Count == 0)
                throw Failure(
                    $"Gradle test exited with code {processResult.ExitCode} without writing JUnit reports: "
                    + ProcessSummary(processResult));

            IReadOnlyList<JvmTestBackendCaseResult> invocationRows = ParseReports(reportPaths, paths);
            if (invocationRows.Count == 0)
                throw Failure("Gradle test wrote JUnit reports containing no test cases.");
            rows.AddRange(invocationRows);
            lastArtifactPath = reportPaths[^1];
        }

        if (rows.Count == 0 || lastArtifactPath is null)
            throw Failure("Gradle test produced no JUnit test cases.");
        return new JvmTestBackendRunResult(lastArtifactPath, rows, exitCode);
    }

    public TestProcessCommand BuildDiscoveryCommand(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(paths);
        return BuildCommand(workspace, paths, ["test", "--test-dry-run"]);
    }

    public IReadOnlyList<TestProcessCommand> BuildRunCommands(
        ContinuousTestProviderRunRequest request,
        CtGenerationPaths paths,
        IReadOnlyList<JvmTestSelection> selected,
        bool wholeSuite)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(selected);
        if (!wholeSuite && selected.Count == 0)
            throw Failure("Gradle run selected no test cases and did not request the whole suite.");

        IReadOnlyList<IReadOnlyList<JvmTestSelection>> chunks = wholeSuite
            ? [Array.Empty<JvmTestSelection>()]
            : CtArgvChunking.Chunk(
                selected,
                selection => CtArgvChunking.ArgvCost(["--tests", selection.Selector]));
        return chunks
            .Select(chunk => BuildCommand(
                request.Workspace,
                paths,
                new[] { "test" }.Concat(chunk.SelectMany(selection => new[] { "--tests", selection.Selector })).ToArray()))
            .ToArray();
    }

    private static TestProcessCommand BuildCommand(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths,
        IReadOnlyList<string> taskArguments)
    {
        string projectRoot = JvmTestTooling.ProjectRoot(workspace);
        string initScript = JvmTestTooling.GradleInitScript(paths);
        string gradleHome = JvmTestTooling.GradleUserHome(paths);
        string projectCache = JvmTestTooling.GradleProjectCache(paths);
        string buildRoot = JvmTestTooling.GradleBuildRoot(paths);
        var arguments = new List<string>
        {
            "--no-daemon",
            "--init-script", initScript,
            "--gradle-user-home", gradleHome,
            "--project-cache-dir", projectCache,
            "--console", "plain",
            "-p", projectRoot,
        };
        arguments.AddRange(taskArguments);

        string? wrapper = WrapperPath(workspace, projectRoot);
        string fileName = wrapper ?? "gradle";
        string workingDirectory = wrapper is null
            ? projectRoot
            : Path.GetDirectoryName(wrapper) ?? projectRoot;
        var environment = new ReadOnlyDictionary<string, string?>(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["GRADLE_USER_HOME"] = gradleHome,
                ["MILLER_CT_GRADLE_BUILD_ROOT"] = buildRoot,
            });
        return new TestProcessCommand(fileName, arguments, workingDirectory, environment);
    }

    private static string? WrapperPath(ContinuousTestWorkspace workspace, string projectRoot)
    {
        string wrapperName = OperatingSystem.IsWindows() ? "gradlew.bat" : "gradlew";
        string[] candidates =
        [
            Path.Combine(projectRoot, wrapperName),
            Path.Combine(Path.GetFullPath(workspace.WorkspaceRoot), wrapperName),
        ];
        return candidates
            .Distinct(PathComparer)
            .FirstOrDefault(File.Exists);
    }

    private static IReadOnlyList<string> ReportPaths(CtGenerationPaths paths)
    {
        if (!Directory.Exists(paths.GenerationRoot))
            return [];
        try
        {
            return Directory.EnumerateFiles(paths.GenerationRoot, "TEST-*.xml", SearchOption.AllDirectories)
                .Select(Path.GetFullPath)
                .Where(path => JvmTestTooling.IsInside(paths.GenerationRoot, path))
                .OrderBy(path => path, PathComparer)
                .ToArray();
        }
        catch (IOException exception)
        {
            throw Failure($"Could not enumerate Gradle JUnit reports: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            throw Failure($"Could not enumerate Gradle JUnit reports: {exception.Message}");
        }
    }

    private static IReadOnlyList<JvmTestBackendCaseResult> ParseReports(
        IReadOnlyList<string> reportPaths,
        CtGenerationPaths paths)
    {
        var rows = new List<JvmTestBackendCaseResult>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string reportPath in reportPaths)
        {
            if (!JvmTestTooling.IsInside(paths.GenerationRoot, reportPath))
                throw Failure($"Gradle JUnit report escaped the CT generation: '{reportPath}'.");

            JUnitXmlParseResult report;
            try
            {
                report = JUnitXmlResultParser.ParseFile(reportPath);
            }
            catch (TestArtifactParseException exception)
            {
                throw Failure($"Gradle JUnit report '{reportPath}' was unreadable: {exception.Message}", exception);
            }
            if (report.HasAggregateMismatch)
            {
                throw Failure(
                    $"Gradle JUnit report '{reportPath}' has inconsistent aggregate counts: "
                    + string.Join("; ", report.AggregateMismatches));
            }

            foreach (Miller.Testing.Providers.Shared.JUnitXmlTestCase testCase in report.Cases)
            {
                string className = testCase.ClassName ?? testCase.SuiteName;
                if (string.IsNullOrWhiteSpace(className) || string.Equals(className, "junit", StringComparison.OrdinalIgnoreCase))
                    throw Failure($"Gradle JUnit report '{reportPath}' contained a test case without a class name.");
                string key = JvmTestTooling.Selector(className, testCase.Name);
                if (!seen.Add(key))
                    throw Failure($"Gradle JUnit reports contained duplicate test case '{key}'.");

                var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["report_path"] = reportPath,
                    ["suite_name"] = testCase.SuiteName,
                    ["class_name"] = className,
                    ["method_name"] = testCase.Name,
                    ["status"] = testCase.Status,
                };
                rows.Add(new JvmTestBackendCaseResult(
                    className,
                    testCase.Name,
                    testCase.Status,
                    testCase.DurationSeconds,
                    testCase.FailureText ?? testCase.FailureMessage,
                    metadata));
            }
        }

        return rows;
    }

    private static void ClearReports(CtGenerationPaths paths)
    {
        if (!Directory.Exists(paths.GenerationRoot))
            return;
        foreach (string path in ReportPaths(paths))
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException exception)
            {
                throw Failure($"Could not remove stale Gradle JUnit report '{path}': {exception.Message}");
            }
            catch (UnauthorizedAccessException exception)
            {
                throw Failure($"Could not remove stale Gradle JUnit report '{path}': {exception.Message}");
            }
        }
    }

    private static string ProcessSummary(TestProcessResult result)
    {
        string standardError = result.StandardError.Trim();
        if (standardError.Length > 0)
            return standardError;
        string standardOutput = result.StandardOutput.Trim();
        return standardOutput.Length > 0 ? standardOutput : "no process output";
    }

    private static ContinuousTestProviderException Failure(string message, Exception? inner = null) =>
        inner is null
            ? new ContinuousTestProviderException(message)
            : new ContinuousTestProviderException(message, inner);

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
