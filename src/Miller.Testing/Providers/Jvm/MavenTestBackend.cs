using System.Collections.ObjectModel;
using Miller.Testing.Parsing;
using Miller.Testing.Providers.Shared;

namespace Miller.Testing.Providers.Jvm;

internal sealed class MavenTestBackend : IJvmTestBackend
{
    private const string BuildRootName = "maven-build";
    private const string TestClassesName = "test-classes";
    private const string ReportsName = "surefire-reports";
    private const string LocalRepositoryName = "maven-repository";

    private readonly ITestProcessRunner _runner;

    public MavenTestBackend(ITestProcessRunner runner)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    public string Discriminator => JvmTestBackendIds.Maven;

    internal static string ClassCaseSentinel => JvmTestBackendIds.ClassCaseSentinel;

    public Task EnsureBuildAsync(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(paths);
        paths.EnsureDirectories();
        ValidateGenerationPaths(paths);
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
        ValidateGenerationPaths(paths);

        TestProcessResult processResult = await _runner
            .RunAsync(BuildDiscoveryCommand(workspace, paths), cancellationToken)
            .ConfigureAwait(false);
        if (processResult.ExitCode != 0)
            throw Failure(
                $"Maven test-compile exited with code {processResult.ExitCode}: {ProcessSummary(processResult)}");

        string testClasses = TestClassesDirectory(paths);
        if (!Directory.Exists(testClasses))
            throw Failure($"Maven test-compile produced no test-classes directory under '{testClasses}'.");

        IReadOnlyList<string> classFiles = EnumerateTestClasses(testClasses);
        if (classFiles.Count == 0)
            throw Failure("Maven test-compile produced no Surefire test classes.");

        return classFiles
            .Select(path =>
            {
                string className = ClassName(path, testClasses);
                var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["backend"] = Discriminator,
                    ["class_scope"] = true,
                    ["class_name"] = className,
                    ["method_name"] = ClassCaseSentinel,
                };
                return new JvmTestBackendCase(
                    className,
                    ClassCaseSentinel,
                    className,
                    Metadata: metadata);
            })
            .OrderBy(test => test.ClassName, StringComparer.Ordinal)
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
        ValidateGenerationPaths(paths);
        ValidateSelections(selected, wholeSuite);
        if (!wholeSuite && selected.Count == 0)
            throw Failure("Maven run selected no test classes and did not request the whole suite.");

        IReadOnlyList<TestProcessCommand> commands = BuildRunCommands(request, paths, selected, wholeSuite);
        var rows = new List<JvmTestBackendCaseResult>();
        var seenClasses = new HashSet<string>(StringComparer.Ordinal);
        int exitCode = 0;
        string? lastArtifactPath = null;
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
                    $"Maven test exited with code {processResult.ExitCode} without writing Surefire reports: "
                    + ProcessSummary(processResult));

            IReadOnlyList<JvmTestBackendCaseResult> invocationRows = AggregateReports(reportPaths, paths);
            if (invocationRows.Count == 0)
                throw Failure("Maven Surefire reports contained no test cases.");
            foreach (JvmTestBackendCaseResult row in invocationRows)
            {
                if (!seenClasses.Add(row.ClassName))
                    throw Failure($"Maven Surefire reports contained duplicate test class '{row.ClassName}'.");
                rows.Add(row);
            }

            lastArtifactPath = reportPaths[^1];
        }

        if (rows.Count == 0 || lastArtifactPath is null)
            throw Failure("Maven test produced no Surefire test cases.");

        if (!wholeSuite)
        {
            HashSet<string> selectedClasses = selected
                .Select(test => test.ClassName)
                .ToHashSet(StringComparer.Ordinal);
            string[] unexpected = rows
                .Select(test => test.ClassName)
                .Except(selectedClasses, StringComparer.Ordinal)
                .ToArray();
            if (unexpected.Length > 0)
                throw Failure(
                    $"Maven backend reported unselected test classes: {string.Join(", ", unexpected)}",
                    lastArtifactPath);

            string[] missing = selectedClasses
                .Except(rows.Select(test => test.ClassName), StringComparer.Ordinal)
                .ToArray();
            if (missing.Length > 0)
                throw Failure(
                    $"Maven backend did not report selected test classes: {string.Join(", ", missing)}",
                    lastArtifactPath);
        }

        return new JvmTestBackendRunResult(lastArtifactPath, rows, exitCode);
    }

    public TestProcessCommand BuildDiscoveryCommand(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(paths);
        return BuildCommand(workspace, paths, ["test-compile"]);
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
        ValidateSelections(selected, wholeSuite);
        if (!wholeSuite && selected.Count == 0)
            throw Failure("Maven run selected no test classes and did not request the whole suite.");

        IReadOnlyList<IReadOnlyList<JvmTestSelection>> chunks = wholeSuite
            ? [Array.Empty<JvmTestSelection>()]
            : CtArgvChunking.Chunk(
                selected,
                selection => CtArgvChunking.ArgvCost(["-Dtest=" + selection.ClassName]));
        return chunks
            .Select(chunk =>
            {
                var taskArguments = new List<string>();
                if (!wholeSuite)
                {
                    taskArguments.Add(
                        "-Dtest=" + string.Join(',', chunk.Select(selection => selection.ClassName)));
                }
                taskArguments.Add("test");

                return BuildCommand(request.Workspace, paths, taskArguments);
            })
            .ToArray();
    }

    private static TestProcessCommand BuildCommand(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths,
        IReadOnlyList<string> taskArguments)
    {
        ValidateGenerationPaths(paths);
        string projectRoot = JvmTestTooling.ProjectRoot(workspace);
        string projectPath = Path.GetFullPath(workspace.ProjectPath);
        string buildRoot = BuildRoot(paths);
        string output = Path.Combine(buildRoot, "classes");
        string testOutput = TestClassesDirectory(paths);
        string reports = ReportsDirectory(paths);
        string localRepository = LocalRepository(paths);

        var arguments = new List<string>
        {
            "-q",
            "-B",
            "-f",
            projectPath,
            "-Dmaven.repo.local=" + localRepository,
            "-Dproject.build.directory=" + buildRoot,
            "-Dproject.build.outputDirectory=" + output,
            "-Dproject.build.testOutputDirectory=" + testOutput,
            "-Dmaven.compiler.outputDirectory=" + output,
            "-Dmaven.compiler.testOutputDirectory=" + testOutput,
            "-Dsurefire.reportsDirectory=" + reports,
            "-Djava.io.tmpdir=" + paths.TempDirectory,
        };
        arguments.AddRange(taskArguments);

        string? wrapper = WrapperPath(workspace, projectRoot);
        string fileName = wrapper ?? (OperatingSystem.IsWindows() ? "mvn.cmd" : "mvn");
        string workingDirectory = wrapper is null
            ? projectRoot
            : Path.GetDirectoryName(wrapper) ?? projectRoot;
        var environment = new ReadOnlyDictionary<string, string?>(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["TMPDIR"] = paths.TempDirectory,
                ["TEMP"] = paths.TempDirectory,
                ["TMP"] = paths.TempDirectory,
            });
        return new TestProcessCommand(fileName, arguments, workingDirectory, environment);
    }

    private static string? WrapperPath(ContinuousTestWorkspace workspace, string projectRoot)
    {
        string wrapperName = OperatingSystem.IsWindows() ? "mvnw.cmd" : "mvnw";
        string[] candidates =
        [
            Path.Combine(projectRoot, wrapperName),
            Path.Combine(Path.GetFullPath(workspace.WorkspaceRoot), wrapperName),
        ];
        return candidates
            .Distinct(PathComparer)
            .FirstOrDefault(File.Exists);
    }

    private static IReadOnlyList<string> EnumerateTestClasses(string testClasses)
    {
        try
        {
            return Directory.EnumerateFiles(testClasses, "*", SearchOption.AllDirectories)
                .Select(Path.GetFullPath)
                .Where(path => JvmTestTooling.IsInside(testClasses, path))
                .Where(path => string.Equals(Path.GetExtension(path), ".class", StringComparison.OrdinalIgnoreCase))
                .Where(path => !Path.GetFileNameWithoutExtension(path).Contains('$', StringComparison.Ordinal))
                .Where(path => IsSurefireDefaultInclude(Path.GetFileNameWithoutExtension(path)))
                .OrderBy(path => path, PathComparer)
                .ToArray();
        }
        catch (IOException exception)
        {
            throw Failure($"Could not enumerate Maven test classes: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            throw Failure($"Could not enumerate Maven test classes: {exception.Message}");
        }
    }

    private static bool IsSurefireDefaultInclude(string className) =>
        className.StartsWith("Test", StringComparison.Ordinal)
        || className.EndsWith("Test", StringComparison.Ordinal)
        || className.EndsWith("Tests", StringComparison.Ordinal)
        || className.EndsWith("TestCase", StringComparison.Ordinal);

    private static string ClassName(string classFile, string testClasses)
    {
        if (!JvmTestTooling.IsInside(testClasses, classFile))
            throw Failure($"Maven test class escaped the generation test-classes directory: '{classFile}'.");
        string relative = Path.GetRelativePath(testClasses, classFile);
        string withoutExtension = relative[..^Path.GetExtension(relative).Length];
        return withoutExtension
            .Replace(Path.DirectorySeparatorChar, '.')
            .Replace(Path.AltDirectorySeparatorChar, '.');
    }

    private static IReadOnlyList<string> ReportPaths(CtGenerationPaths paths)
    {
        string reports = ReportsDirectory(paths);
        if (!Directory.Exists(reports))
            return [];
        try
        {
            return Directory.EnumerateFiles(reports, "TEST-*.xml", SearchOption.AllDirectories)
                .Select(Path.GetFullPath)
                .Where(path => JvmTestTooling.IsInside(reports, path))
                .OrderBy(path => path, PathComparer)
                .ToArray();
        }
        catch (IOException exception)
        {
            throw Failure($"Could not enumerate Maven Surefire reports: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            throw Failure($"Could not enumerate Maven Surefire reports: {exception.Message}");
        }
    }

    private static IReadOnlyList<JvmTestBackendCaseResult> AggregateReports(
        IReadOnlyList<string> reportPaths,
        CtGenerationPaths paths)
    {
        var casesByClass = new Dictionary<string, List<JUnitXmlTestCase>>(StringComparer.Ordinal);
        var seenMethods = new HashSet<string>(StringComparer.Ordinal);
        string reports = ReportsDirectory(paths);
        foreach (string reportPath in reportPaths)
        {
            if (!JvmTestTooling.IsInside(reports, reportPath))
                throw Failure($"Maven Surefire report escaped its generation report directory: '{reportPath}'.");

            JUnitXmlParseResult report;
            try
            {
                report = JUnitXmlResultParser.ParseFile(reportPath);
            }
            catch (TestArtifactParseException exception)
            {
                throw Failure($"Maven Surefire report '{reportPath}' was unreadable: {exception.Message}", inner: exception);
            }
            catch (IOException exception)
            {
                throw Failure($"Maven Surefire report '{reportPath}' was unreadable: {exception.Message}", inner: exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw Failure($"Maven Surefire report '{reportPath}' was unreadable: {exception.Message}", inner: exception);
            }
            if (report.HasAggregateMismatch)
            {
                throw Failure(
                    $"Maven Surefire report '{reportPath}' has inconsistent aggregate counts: "
                    + string.Join("; ", report.AggregateMismatches));
            }

            foreach (JUnitXmlTestCase testCase in report.Cases)
            {
                string className = testCase.ClassName ?? testCase.SuiteName;
                if (string.IsNullOrWhiteSpace(className)
                    || string.Equals(className, "junit", StringComparison.OrdinalIgnoreCase))
                {
                    throw Failure($"Maven Surefire report '{reportPath}' contained a test case without a class name.");
                }

                string methodKey = className + "\u0000" + testCase.Name;
                if (!seenMethods.Add(methodKey))
                    throw Failure($"Maven Surefire reports contained duplicate test case '{className}.{testCase.Name}'.");
                if (!casesByClass.TryGetValue(className, out List<JUnitXmlTestCase>? classCases))
                {
                    classCases = [];
                    casesByClass.Add(className, classCases);
                }

                classCases.Add(testCase);
            }
        }

        return casesByClass
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => AggregateClass(pair.Key, pair.Value))
            .ToArray();
    }

    private static JvmTestBackendCaseResult AggregateClass(
        string className,
        IReadOnlyList<JUnitXmlTestCase> cases)
    {
        string status = cases.Any(test => test.Status is "failed" or "errored")
            ? "failed"
            : cases.All(test => test.Status == "skipped")
                ? "skipped"
                : "passed";
        string[] failures = cases
            .Where(test => test.Status is "failed" or "errored")
            .Select(test =>
            {
                string? text = test.FailureText ?? test.FailureMessage;
                return string.IsNullOrWhiteSpace(text)
                    ? test.Name
                    : test.Name + ": " + text;
            })
            .ToArray();
        double? duration = cases.Any(test => test.DurationSeconds is not null)
            ? cases.Sum(test => test.DurationSeconds ?? 0)
            : null;
        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["backend"] = JvmTestBackendIds.Maven,
            ["class_scope"] = true,
            ["class_name"] = className,
            ["method_count"] = cases.Count,
            ["statuses"] = cases.Select(test => test.Status).ToArray(),
        };
        return new JvmTestBackendCaseResult(
            className,
            ClassCaseSentinel,
            status,
            duration,
            failures.Length == 0 ? null : string.Join(Environment.NewLine, failures),
            metadata);
    }

    private static void ValidateSelections(
        IReadOnlyList<JvmTestSelection> selected,
        bool wholeSuite)
    {
        if (selected.Any(test => string.IsNullOrWhiteSpace(test.ClassName)))
            throw Failure("Maven run selection contained an empty test class.");
        if (selected.Any(test => !string.Equals(test.MethodName, ClassCaseSentinel, StringComparison.Ordinal)))
            throw Failure("Maven run selections must use the class-level identity sentinel.");
        if (wholeSuite)
            return;
    }

    private static void ClearReports(CtGenerationPaths paths)
    {
        foreach (string path in ReportPaths(paths))
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException exception)
            {
                throw Failure($"Could not remove stale Maven Surefire report '{path}': {exception.Message}");
            }
            catch (UnauthorizedAccessException exception)
            {
                throw Failure($"Could not remove stale Maven Surefire report '{path}': {exception.Message}");
            }
        }
    }

    private static void ValidateGenerationPaths(CtGenerationPaths paths)
    {
        string[] generationPaths =
        [
            BuildRoot(paths),
            TestClassesDirectory(paths),
            ReportsDirectory(paths),
            LocalRepository(paths),
        ];
        if (generationPaths.Any(path => !JvmTestTooling.IsInside(paths.GenerationRoot, path)))
            throw Failure("Maven CT output paths must remain inside the allocated generation.");
        if (string.IsNullOrWhiteSpace(paths.TempDirectory)
            || !JvmTestTooling.IsInside(CtTempPaths.Root, paths.TempDirectory))
        {
            throw Failure("Maven CT temp path must remain in Miller's per-project temp namespace.");
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

    private static ContinuousTestProviderException Failure(
        string message,
        string? artifactPath = null,
        Exception? inner = null) =>
        inner is null
            ? new ContinuousTestProviderException(message) { ResultArtifactPath = artifactPath }
            : new ContinuousTestProviderException(message, inner) { ResultArtifactPath = artifactPath };

    private static string BuildRoot(CtGenerationPaths paths) =>
        Path.Combine(paths.GenerationRoot, BuildRootName);

    private static string TestClassesDirectory(CtGenerationPaths paths) =>
        Path.Combine(BuildRoot(paths), TestClassesName);

    private static string ReportsDirectory(CtGenerationPaths paths) =>
        Path.Combine(BuildRoot(paths), ReportsName);

    private static string LocalRepository(CtGenerationPaths paths) =>
        Path.Combine(BuildRoot(paths), LocalRepositoryName);

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
