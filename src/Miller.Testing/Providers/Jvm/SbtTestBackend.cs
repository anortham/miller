using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Miller.Testing.Parsing;
using Miller.Testing.Providers.Shared;

namespace Miller.Testing.Providers.Jvm;

internal sealed class SbtTestBackend : IJvmTestBackend
{
    private const string WorkspaceCacheName = "sbt-workspace";
    private const string DependencyCacheName = "sbt-deps";
    private const string ShadowDirectoryName = "build";
    private const string LastUsedMarkerName = ".last-used";
    private static readonly Regex ListPattern = new(
        @"^\[info\]\s+List\((?<names>.*)\)\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex BulletPattern = new(
        @"^\[info\]\s+\*\s+(?<name>\S+)\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ProjectPattern = new(
        @"^\[info\]\s+(?<project>.+?)\s+/\s+Test\s+/\s+definedTestNames\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly ITestProcessRunner _runner;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    internal SbtWorkspaceShadowResult? LastSync { get; private set; }

    public SbtTestBackend(ITestProcessRunner runner)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    public string Discriminator => JvmTestBackendIds.Sbt;

    public Task EnsureBuildAsync(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(paths);
        ValidateGenerationPaths(paths);
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
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateGenerationPaths(paths);
            paths.EnsureDirectories();
            SbtWorkspaceShadowResult sync = PrepareShadow(workspace, paths, cancellationToken);
            TestProcessResult result;
            try
            {
                result = await _runner
                    .RunAsync(
                        BuildCommand(workspace, paths, ["show", "Test/definedTestNames"], sync),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                TouchDependencyCache(sync.DependencyCandidateRoot);
            }

            if (result.ExitCode != 0)
                throw Failure($"sbt test discovery exited with code {result.ExitCode}: {ProcessSummary(result)}");

            string output = result.RequireCompleteStandardOutput("sbt test discovery");
            return ParseDefinedTestNames(output);
        }
        finally
        {
            _operationGate.Release();
        }
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
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateGenerationPaths(paths);
            paths.EnsureDirectories();
            ValidateSelections(selected, wholeSuite);
            if (!wholeSuite && selected.Count == 0)
                throw Failure("sbt run selected no test classes and did not request the whole suite.");

            SbtWorkspaceShadowResult sync = PrepareShadow(request.Workspace, paths, cancellationToken);
            ClearReports(sync.ShadowRoot);
            TestProcessCommand command = AssertSingleCommand(
                BuildRunCommands(request, paths, selected, wholeSuite, sync));
            TestProcessResult processResult;
            try
            {
                processResult = await _runner.RunAsync(command, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                TouchDependencyCache(sync.DependencyCandidateRoot);
            }

            IReadOnlyList<string> reportPaths = ReportPaths(sync.ShadowRoot);
            if (reportPaths.Count == 0)
                throw Failure(
                    $"sbt test exited with code {processResult.ExitCode} without writing JUnit reports: "
                    + ProcessSummary(processResult));

            (string artifactPath, IReadOnlyList<string> copiedPaths) = CopyReports(reportPaths, sync.ShadowRoot, paths);
            IReadOnlyList<JvmTestBackendCaseResult> rows = ParseReports(copiedPaths, paths);
            if (rows.Count == 0)
                throw Failure("sbt JUnit reports contained no test cases.", artifactPath);

            ValidateReportedClasses(selected, rows, wholeSuite, artifactPath);
            return new JvmTestBackendRunResult(artifactPath, rows, processResult.ExitCode);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public TestProcessCommand BuildDiscoveryCommand(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(paths);
        return BuildCommand(workspace, paths, ["show", "Test/definedTestNames"]);
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
            throw Failure("sbt run selected no test classes and did not request the whole suite.");

        return BuildRunCommands(request, paths, selected, wholeSuite, sync: null);
    }

    private static IReadOnlyList<TestProcessCommand> BuildRunCommands(
        ContinuousTestProviderRunRequest request,
        CtGenerationPaths paths,
        IReadOnlyList<JvmTestSelection> selected,
        bool wholeSuite,
        SbtWorkspaceShadowResult? sync)
    {
        IReadOnlyList<string> taskArguments = wholeSuite
            ? ["test"]
            : ["testOnly", string.Join(' ', selected.Select(test => test.ClassName))];
        return [BuildCommand(request.Workspace, paths, taskArguments, sync)];
    }

    private SbtWorkspaceShadowResult PrepareShadow(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths,
        CancellationToken cancellationToken)
    {
        SbtWorkspaceShadowResult result = SbtWorkspaceShadow.Sync(workspace, cancellationToken);
        LastSync = result;
        return result;
    }

    private static IReadOnlyList<JvmTestBackendCase> ParseDefinedTestNames(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var names = new List<(string Name, string Project)>();
        string project = "<root>";
        foreach (string line in output.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            string trimmedLine = line.Trim();
            Match projectMatch = ProjectPattern.Match(line);
            if (projectMatch.Success)
            {
                project = projectMatch.Groups["project"].Value.Trim();
                continue;
            }

            Match match = ListPattern.Match(line);
            if (match.Success)
            {
                string text = match.Groups["names"].Value.Trim();
                if (text.Length > 0)
                {
                    names.AddRange(text
                        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                        .Select(name => (name, project)));
                }
                continue;
            }

            match = BulletPattern.Match(line);
            if (match.Success)
            {
                names.Add((match.Groups["name"].Value, project));
                continue;
            }

            if (trimmedLine.StartsWith("[info] List(", StringComparison.Ordinal)
                || trimmedLine.StartsWith("[info] *", StringComparison.Ordinal))
            {
                throw Failure("sbt definedTestNames output contained a malformed discovery row.");
            }
        }

        if (names.Count == 0)
            throw Failure("sbt definedTestNames output contained no test names.");

        var classes = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        var projectByClass = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string rawName, string projectName) in names)
        {
            string name = rawName.Trim();
            int separator = name.LastIndexOf('.');
            if (separator <= 0 || separator == name.Length - 1 || !seenNames.Add(name))
                throw Failure($"sbt definedTestNames output contained malformed or duplicate test name '{name}'.");

            string className = name[..separator];
            string methodName = name[(separator + 1)..];
            if (projectByClass.TryGetValue(className, out string? existingProject)
                && !string.Equals(existingProject, projectName, StringComparison.Ordinal))
            {
                throw Failure($"sbt definedTestNames output contained duplicate class '{className}' across projects.");
            }

            projectByClass[className] = projectName;
            if (!classes.TryGetValue(className, out List<string>? methods))
            {
                methods = [];
                classes.Add(className, methods);
            }

            methods.Add(methodName);
        }

        return classes
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new JvmTestBackendCase(
                pair.Key,
                JvmTestBackendIds.ClassCaseSentinel,
                pair.Key,
                Metadata: new ReadOnlyDictionary<string, object?>(
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["backend"] = JvmTestBackendIds.Sbt,
                        ["class_scope"] = true,
                        ["class_name"] = pair.Key,
                        ["method_name"] = JvmTestBackendIds.ClassCaseSentinel,
                        ["method_count"] = pair.Value.Count,
                        ["defined_test_names"] = pair.Value.ToArray(),
                    })))
            .ToArray();
    }

    private static TestProcessCommand BuildCommand(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths,
        IReadOnlyList<string> taskArguments,
        SbtWorkspaceShadowResult? sync = null)
    {
        ValidateGenerationPaths(paths);
        string shadowRoot = sync?.ShadowRoot ?? ShadowRoot(workspace);
        string shadowProjectPath = sync?.ShadowProjectPath ?? ShadowProjectPath(workspace);
        string dependencyRoot = sync?.DependencyCandidateRoot ?? DependencyRoot(workspace);
        if (!JvmTestTooling.IsInside(shadowRoot, shadowProjectPath))
            throw Failure("sbt shadow project path escaped the mirrored build root.");
        var arguments = new List<string>
        {
            "-batch",
            "-Dsbt.supershell=false",
            "-Dsbt.color=false",
            "-Dsbt.log.noformat=true",
            "-Dsbt.server.autostart=false",
            "-Dsbt.boot.directory=" + Path.Combine(dependencyRoot, "boot"),
            "-Dsbt.global.base=" + Path.Combine(dependencyRoot, "global"),
            "-Dsbt.ivy.home=" + Path.Combine(dependencyRoot, "ivy"),
            "-Dsbt.coursier.home=" + Path.Combine(dependencyRoot, "coursier"),
        };
        arguments.AddRange(taskArguments);

        string? wrapper = WrapperPath(shadowRoot);
        string fileName = wrapper ?? (OperatingSystem.IsWindows() ? "sbt.bat" : "sbt");
        string workingDirectory = Path.GetDirectoryName(shadowProjectPath) ?? shadowRoot;
        var environment = new ReadOnlyDictionary<string, string?>(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["TMPDIR"] = paths.TempDirectory,
                ["TEMP"] = paths.TempDirectory,
                ["TMP"] = paths.TempDirectory,
            });
        return new TestProcessCommand(fileName, arguments, workingDirectory, environment);
    }

    private static string? WrapperPath(string shadowRoot)
    {
        string[] names = OperatingSystem.IsWindows()
            ? ["sbt.bat", "sbt.cmd"]
            : ["sbt"];
        return names
            .Select(name => Path.Combine(shadowRoot, name))
            .FirstOrDefault(File.Exists);
    }

    private static string ShadowRoot(ContinuousTestWorkspace workspace) =>
        Path.Combine(CtGenerationPaths.CacheDirectory(workspace, WorkspaceCacheName), ShadowDirectoryName);

    private static string ShadowProjectPath(ContinuousTestWorkspace workspace)
    {
        string sourceRoot = JvmTestTooling.ProjectRoot(workspace);
        string relative = Path.GetRelativePath(sourceRoot, Path.GetFullPath(workspace.ProjectPath));
        return Path.Combine(ShadowRoot(workspace), relative);
    }

    private static string DependencyRoot(ContinuousTestWorkspace workspace) =>
        CtGenerationPaths.CacheDirectory(workspace, DependencyCacheName);

    private static IReadOnlyList<string> ReportPaths(string shadowRoot)
    {
        if (!Directory.Exists(shadowRoot))
            return [];

        try
        {
            return Directory.EnumerateFiles(shadowRoot, "*.xml", SearchOption.AllDirectories)
                .Select(Path.GetFullPath)
                .Where(path => JvmTestTooling.IsInside(shadowRoot, path))
                .Where(IsTestReportPath)
                .OrderBy(path => path, PathComparer)
                .ToArray();
        }
        catch (IOException exception)
        {
            throw Failure($"Could not enumerate sbt JUnit reports: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            throw Failure($"Could not enumerate sbt JUnit reports: {exception.Message}");
        }
    }

    private static bool IsTestReportPath(string path)
    {
        string[] parts = Path.GetDirectoryName(path)!
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2
            && string.Equals(parts[^1], "test-reports", StringComparison.Ordinal)
            && string.Equals(parts[^2], "target", StringComparison.Ordinal);
    }

    private static void ClearReports(string shadowRoot)
    {
        foreach (string reportPath in ReportPaths(shadowRoot))
        {
            try
            {
                File.Delete(reportPath);
            }
            catch (IOException exception)
            {
                throw Failure($"Could not remove stale sbt JUnit report '{reportPath}': {exception.Message}");
            }
            catch (UnauthorizedAccessException exception)
            {
                throw Failure($"Could not remove stale sbt JUnit report '{reportPath}': {exception.Message}");
            }
        }
    }

    private static (string ArtifactPath, IReadOnlyList<string> CopiedPaths) CopyReports(
        IReadOnlyList<string> reportPaths,
        string shadowRoot,
        CtGenerationPaths paths)
    {
        string resultRoot = ResultRoot(paths);
        Directory.CreateDirectory(resultRoot);
        var copied = new List<string>(reportPaths.Count);
        foreach (string reportPath in reportPaths)
        {
            if (!JvmTestTooling.IsInside(shadowRoot, reportPath))
                throw Failure($"sbt JUnit report escaped the shadow build root: '{reportPath}'.");
            if (File.GetAttributes(reportPath).HasFlag(FileAttributes.ReparsePoint))
                throw Failure($"sbt JUnit report was a reparse point: '{reportPath}'.");

            string relative = Path.GetRelativePath(shadowRoot, reportPath);
            string destination = Path.Combine(resultRoot, relative);
            if (!JvmTestTooling.IsInside(resultRoot, destination))
                throw Failure($"sbt JUnit report escaped the generation results: '{relative}'.");

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            string temporary = destination + ".tmp-" + Path.GetRandomFileName();
            try
            {
                File.Copy(reportPath, temporary, overwrite: true);
                File.Move(temporary, destination, overwrite: true);
            }
            catch (IOException exception)
            {
                throw Failure($"Could not copy sbt JUnit report '{reportPath}': {exception.Message}");
            }
            catch (UnauthorizedAccessException exception)
            {
                throw Failure($"Could not copy sbt JUnit report '{reportPath}': {exception.Message}");
            }
            finally
            {
                try
                {
                    if (File.Exists(temporary))
                        File.Delete(temporary);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            copied.Add(destination);
        }

        return (copied[^1], copied);
    }

    private static IReadOnlyList<JvmTestBackendCaseResult> ParseReports(
        IReadOnlyList<string> reportPaths,
        CtGenerationPaths paths)
    {
        var casesByClass = new Dictionary<string, List<JUnitXmlTestCase>>(StringComparer.Ordinal);
        var seenClasses = new HashSet<string>(StringComparer.Ordinal);
        var seenMethods = new HashSet<string>(StringComparer.Ordinal);
        foreach (string reportPath in reportPaths)
        {
            if (!JvmTestTooling.IsInside(ResultRoot(paths), reportPath))
                throw Failure($"sbt JUnit report escaped the generation results: '{reportPath}'.");

            JUnitXmlParseResult report;
            try
            {
                report = JUnitXmlResultParser.ParseFile(reportPath);
            }
            catch (TestArtifactParseException exception)
            {
                throw Failure($"sbt JUnit report '{reportPath}' was unreadable: {exception.Message}");
            }
            catch (IOException exception)
            {
                throw Failure($"sbt JUnit report '{reportPath}' was unreadable: {exception.Message}");
            }
            catch (UnauthorizedAccessException exception)
            {
                throw Failure($"sbt JUnit report '{reportPath}' was unreadable: {exception.Message}");
            }

            if (report.HasAggregateMismatch)
                throw Failure(
                    $"sbt JUnit report '{reportPath}' has inconsistent aggregate counts: "
                    + string.Join("; ", report.AggregateMismatches));

            var reportClasses = new HashSet<string>(StringComparer.Ordinal);
            foreach (JUnitXmlTestCase testCase in report.Cases)
            {
                string className = testCase.ClassName ?? testCase.SuiteName;
                if (string.IsNullOrWhiteSpace(className)
                    || string.Equals(className, "junit", StringComparison.OrdinalIgnoreCase))
                {
                    throw Failure($"sbt JUnit report '{reportPath}' contained a test case without a class name.");
                }

                reportClasses.Add(className);
                string methodKey = className + "\u0000" + testCase.Name;
                if (!seenMethods.Add(methodKey))
                    throw Failure($"sbt JUnit reports contained duplicate test case '{className}.{testCase.Name}'.");
                if (!casesByClass.TryGetValue(className, out List<JUnitXmlTestCase>? classCases))
                {
                    classCases = [];
                    casesByClass.Add(className, classCases);
                }

                classCases.Add(testCase);
            }

            foreach (string className in reportClasses)
            {
                if (!seenClasses.Add(className))
                    throw Failure($"sbt JUnit reports contained duplicate test class '{className}'.");
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
                return string.IsNullOrWhiteSpace(text) ? test.Name : test.Name + ": " + text;
            })
            .ToArray();
        double? duration = cases.Any(test => test.DurationSeconds is not null)
            ? cases.Sum(test => test.DurationSeconds ?? 0)
            : null;
        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["backend"] = JvmTestBackendIds.Sbt,
            ["class_scope"] = true,
            ["class_name"] = className,
            ["method_count"] = cases.Count,
            ["statuses"] = cases.Select(test => test.Status).ToArray(),
        };
        return new JvmTestBackendCaseResult(
            className,
            JvmTestBackendIds.ClassCaseSentinel,
            status,
            duration,
            failures.Length == 0 ? null : string.Join(Environment.NewLine, failures),
            metadata);
    }

    private static void ValidateReportedClasses(
        IReadOnlyList<JvmTestSelection> selected,
        IReadOnlyList<JvmTestBackendCaseResult> rows,
        bool wholeSuite,
        string artifactPath)
    {
        HashSet<string> selectedClasses = selected
            .Select(test => test.ClassName)
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> reportedClasses = rows
            .Select(test => test.ClassName)
            .ToHashSet(StringComparer.Ordinal);
        if (!wholeSuite)
        {
            string[] unexpected = reportedClasses.Except(selectedClasses, StringComparer.Ordinal).ToArray();
            if (unexpected.Length > 0)
                throw Failure(
                    $"sbt backend reported unselected test classes: {string.Join(", ", unexpected)}",
                    artifactPath);
        }

        string[] missing = selectedClasses.Except(reportedClasses, StringComparer.Ordinal).ToArray();
        if (missing.Length > 0)
            throw Failure(
                $"sbt backend did not report selected test classes: {string.Join(", ", missing)}",
                artifactPath);
    }

    private static void ValidateGenerationPaths(CtGenerationPaths paths)
    {
        if (!JvmTestTooling.IsInside(paths.GenerationRoot, ResultRoot(paths)))
            throw Failure("sbt CT result paths must remain inside the allocated generation.");
        if (string.IsNullOrWhiteSpace(paths.TempDirectory)
            || !JvmTestTooling.IsInside(CtTempPaths.Root, paths.TempDirectory))
        {
            throw Failure("sbt CT temp path must remain in Miller's per-project temp namespace.");
        }
    }

    private static string ResultRoot(CtGenerationPaths paths) =>
        Path.Combine(paths.ResultsDirectory, "sbt");

    private static void TouchDependencyCache(string root)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, LastUsedMarkerName), DateTimeOffset.UtcNow.ToString("O"));
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
        string? artifactPath = null) =>
        new(message) { ResultArtifactPath = artifactPath };

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static TestProcessCommand AssertSingleCommand(IReadOnlyList<TestProcessCommand> commands)
    {
        if (commands.Count != 1)
            throw Failure($"sbt continuous testing requires one process per phase, but built {commands.Count}.");
        return commands[0];
    }

    private static void ValidateSelections(
        IReadOnlyList<JvmTestSelection> selected,
        bool wholeSuite)
    {
        if (selected.Any(test => string.IsNullOrWhiteSpace(test.ClassName)))
            throw Failure("sbt run selection contained an empty test class.");
        if (selected.Any(test => !string.Equals(test.MethodName, JvmTestBackendIds.ClassCaseSentinel, StringComparison.Ordinal)))
            throw Failure("sbt run selections must use the class-level identity sentinel.");
        if (wholeSuite)
            return;
    }
}
