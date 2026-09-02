using System.Security.Cryptography;
using System.Text;
using Miller.Testing.Parsing;
using Miller.Testing.Providers.Shared;

namespace Miller.Testing.Providers.Php;

public sealed class PhpTestProvider : IContinuousTestProvider
{
    private readonly ITestProcessRunner _runner;
    private readonly CtGenerationHandoff _generations = new();

    public PhpTestProvider(ITestProcessRunner runner)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    public async Task<IReadOnlyList<ProviderTestCase>> DiscoverAsync(
        ContinuousTestWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        string framework = EnsurePhp(workspace);
        CtGenerationPaths paths = _generations.AllocateForDiscovery(workspace);
        try
        {
            TestProcessResult processResult = await _runner
                .RunAsync(PhpTestTooling.BuildDiscoveryCommand(workspace, paths, framework), cancellationToken)
                .ConfigureAwait(false);
            if (processResult.ExitCode != 0)
                throw new ContinuousTestProviderException(
                    $"PHP {framework} discovery failed with exit code {processResult.ExitCode}: {FailureSummary(processResult)}");

            string artifactPath = Path.Combine(paths.ResultsDirectory, "php-discovery.xml");
            if (!File.Exists(artifactPath))
                throw new ContinuousTestProviderException(
                    $"PHP {framework} discovery exited with code {processResult.ExitCode} without writing its XML report.")
                {
                    ResultArtifactPath = artifactPath,
                };

            IReadOnlyList<PhpListedTest> listedTests = ParseListing(
                artifactPath,
                $"PHP {framework} discovery");
            return CasesFromListing(workspace, framework, listedTests);
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
        string framework = EnsurePhp(request.Workspace, request.Framework);
        CtGenerationPaths paths = _generations.TakeForRun(request.Workspace);
        try
        {
            paths.EnsureDirectories();
            IReadOnlyList<CaseBinding> selections = DecodeSelections(request);
            if (selections.Count == 0)
                throw EmptySelection();

            string runId = request.RunId ?? NewRunId(request);
            DateTimeOffset started = DateTimeOffset.UtcNow;
            IReadOnlyList<PhpInvocation> invocations = BuildInvocations(
                request,
                paths,
                framework,
                selections,
                runId);
            var results = new List<ProviderCaseResult>(selections.Count);
            TestProcessResult? firstNonZeroResult = null;
            for (var index = 0; index < invocations.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PhpInvocation invocation = invocations[index];
                request.Progress?.Invoke(CtArgvChunking.Describe(
                    invocations.Select(static item => item.Units).ToArray(),
                    static selection => selection.Selector,
                    index + 1));
                TestProcessResult processResult = await _runner
                    .RunAsync(invocation.Command, cancellationToken)
                    .ConfigureAwait(false);
                if (processResult.ExitCode != 0 && firstNonZeroResult is null)
                    firstNonZeroResult = processResult;

                if (!File.Exists(invocation.ResultArtifactPath))
                    throw new ContinuousTestProviderException(
                        $"PHP {framework} test run exited with code {processResult.ExitCode} without writing its JUnit report: "
                        + FailureSummary(processResult))
                    {
                        ResultArtifactPath = invocation.ResultArtifactPath,
                    };

                JUnitXmlParseResult report;
                try
                {
                    report = ParseReport(
                        invocation.ResultArtifactPath,
                        $"PHP {framework} test run",
                        invocation.ResultArtifactPath);
                }
                catch (ContinuousTestProviderException exception)
                    when (!request.WholeSuite
                        && exception.Message.Contains("zero test cases", StringComparison.OrdinalIgnoreCase))
                {
                    throw ReportFailure(
                        $"PHP test report did not report selected test case '{invocation.Selections[0].Id}'.",
                        invocation.ResultArtifactPath);
                }
                results.AddRange(MapResults(
                    request,
                    framework,
                    invocation,
                    report,
                    processResult,
                    runId));
            }

            EnsureAllSelectionsReported(selections, results, invocations[^1].ResultArtifactPath);
            if (results.Count == 0)
                throw new ContinuousTestProviderException(
                    $"PHP {framework} test run reported no selected test cases.")
                {
                    ResultArtifactPath = invocations[^1].ResultArtifactPath,
                };

            if (results.All(static result => result.Status != "failed")
                && firstNonZeroResult is not null)
            {
                throw new ContinuousTestProviderException(
                    $"PHP {framework} test run exited nonzero without a failed test result: "
                    + FailureSummary(firstNonZeroResult))
                {
                    ResultArtifactPath = invocations[^1].ResultArtifactPath,
                };
            }

            return new ProviderRunResult(
                RunId: runId,
                Status: AggregateStatus(results),
                StartedAt: started,
                EndedAt: DateTimeOffset.UtcNow,
                CaseResults: results,
                ResultArtifactPath: results.Count == 0 ? null : invocations[^1].ResultArtifactPath,
                TestDisplayNames: results.Select(static result => result.TestCaseId).ToArray())
            {
                GenerationId = paths.GenerationId,
            };
        }
        catch (ContinuousTestProviderException exception) when (exception.GenerationId is null)
        {
            throw StampGeneration(exception, paths);
        }
    }

    public TestProcessCommand BuildRunCommand(ContinuousTestProviderRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return BuildRunCommands(request)[0];
    }

    public IReadOnlyList<TestProcessCommand> BuildRunCommands(ContinuousTestProviderRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string framework = EnsurePhp(request.Workspace, request.Framework);
        IReadOnlyList<CaseBinding> selections = DecodeSelections(request);
        if (selections.Count == 0)
            throw EmptySelection();

        CtGenerationPaths paths = CtGenerationPaths.ResolveLatestOrFirst(request.Workspace);
        string runId = request.RunId ?? NewRunId(request);
        return BuildInvocations(request, paths, framework, selections, runId)
            .Select(static invocation => invocation.Command)
            .ToArray();
    }

    public static bool IsPhpProjectFile(string path) =>
        PhpTestTooling.IsPhpProjectFile(path);

    private static IReadOnlyList<ProviderTestCase> CasesFromListing(
        ContinuousTestWorkspace workspace,
        string framework,
        IReadOnlyList<PhpListedTest> listedTests)
    {
        var cases = new List<ProviderTestCase>(listedTests.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (PhpListedTest listedTest in listedTests)
        {
            string? sourcePath = NormalizeListingPath(workspace, listedTest.FilePath);
            string id = PhpTestTooling.EncodeCaseId(
                workspace.WorkspaceId,
                workspace.ProjectPath,
                listedTest.ClassName,
                listedTest.MethodName);
            if (!ids.Add(id))
                throw new ContinuousTestProviderException(
                    $"PHP {framework} discovery returned duplicate test case '{listedTest.Selector}'.");

            cases.Add(new ProviderTestCase(
                Id: id,
                DisplayName: listedTest.Selector,
                FullyQualifiedName: listedTest.Selector,
                Selector: listedTest.Selector,
                Framework: framework,
                SourcePath: sourcePath,
                Metadata: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["kind"] = "php-test",
                    ["framework"] = framework,
                    ["class_name"] = listedTest.ClassName,
                    ["class"] = listedTest.ClassName,
                    ["method_name"] = listedTest.MethodName,
                    ["file_path"] = sourcePath,
                    ["source_path"] = sourcePath,
                },
                SymbolName: listedTest.BaseMethodName,
                SymbolPath: sourcePath));
        }

        return cases.OrderBy(static test => test.Id, StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<ProviderCaseResult> MapResults(
        ContinuousTestProviderRunRequest request,
        string framework,
        PhpInvocation invocation,
        JUnitXmlParseResult report,
        TestProcessResult processResult,
        string runId)
    {
        var results = new List<ProviderCaseResult>(report.Cases.Count);
        var reported = new HashSet<string>(StringComparer.Ordinal);
        var selectionBySelector = invocation.Selections.ToDictionary(
            static selection => selection.Selector,
            StringComparer.Ordinal);
        foreach (JUnitXmlTestCase testCase in report.Cases)
        {
            PhpTestCaseIdentity identity = IdentityFromReport(testCase);
            CaseBinding? selection = FindSelection(selectionBySelector, identity);
            string testCaseId;
            if (selection is null)
            {
                if (!request.WholeSuite)
                    throw ReportFailure(
                        $"PHP {framework} test report included '{identity.Selector}', which was not selected.",
                        invocation.ResultArtifactPath);
                testCaseId = PhpTestTooling.EncodeCaseId(
                    request.Workspace.WorkspaceId,
                    request.Workspace.ProjectPath,
                    identity.ClassName,
                    identity.MethodName);
            }
            else
            {
                testCaseId = selection.Id;
            }

            if (!reported.Add(testCaseId))
                throw ReportFailure(
                    $"PHP {framework} test report returned duplicate test case '{testCaseId}'.",
                    invocation.ResultArtifactPath);

            string status = NormalizeStatus(testCase.Status);
            results.Add(new ProviderCaseResult(
                Id: ResultId(runId, testCaseId),
                TestCaseId: testCaseId,
                Status: status,
                ResultRevision: request.SelectedRevision,
                IndexIdentity: request.IndexIdentity,
                DurationSeconds: testCase.DurationSeconds,
                FailureSummary: status == "failed"
                    ? FirstNonEmpty(testCase.FailureMessage, testCase.FailureText, processResult.StandardError)
                    : null,
                Metadata: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["artifact_path"] = invocation.ResultArtifactPath,
                    ["framework"] = framework,
                    ["class_name"] = identity.ClassName,
                    ["method_name"] = identity.MethodName,
                }));
        }

        return results;
    }

    private static IReadOnlyList<PhpInvocation> BuildInvocations(
        ContinuousTestProviderRunRequest request,
        CtGenerationPaths paths,
        string framework,
        IReadOnlyList<CaseBinding> selections,
        string runId)
    {
        if (request.WholeSuite)
        {
            string artifactPath = PhpTestTooling.ResultArtifactPath(paths, runId);
            return [new PhpInvocation(
                PhpTestTooling.BuildRunCommand(
                    request.Workspace,
                    paths,
                    framework,
                    artifactPath,
                    [],
                    wholeSuite: true),
                artifactPath,
                selections,
                selections)];
        }

        IReadOnlyList<IReadOnlyList<CaseBinding>> chunks = CtArgvChunking.Chunk(
            selections,
            static selection => RegexCost(selection.Selector));
        var invocations = new List<PhpInvocation>(chunks.Count);
        for (var index = 0; index < chunks.Count; index++)
        {
            IReadOnlyList<CaseBinding> chunk = chunks[index];
            string artifactPath = PhpTestTooling.ResultArtifactPath(paths, runId, chunks.Count == 1 ? null : index);
            invocations.Add(new PhpInvocation(
                PhpTestTooling.BuildRunCommand(
                    request.Workspace,
                    paths,
                    framework,
                    artifactPath,
                    chunk.Select(static selection => selection.Selector).ToArray(),
                    wholeSuite: false),
                artifactPath,
                chunk,
                chunk));
        }

        return invocations;
    }

    private static int RegexCost(string selector)
    {
        const string metacharacters = @"\\^$.*+?()[]{}|/";
        var builder = new StringBuilder(selector.Length * 2);
        foreach (char character in selector)
        {
            if (metacharacters.IndexOf(character) >= 0)
                builder.Append('\\');
            builder.Append(character);
        }
        return CtArgvChunking.ArgvCost(["--filter", builder.ToString()]) + 5;
    }

    private static CaseBinding? FindSelection(
        IReadOnlyDictionary<string, CaseBinding> selections,
        PhpTestCaseIdentity identity)
    {
        if (selections.TryGetValue(identity.Selector, out CaseBinding? exact))
            return exact;

        return selections.Values
            .Where(selection =>
                string.Equals(selection.ClassName, identity.ClassName, StringComparison.Ordinal)
                && identity.MethodName.StartsWith(selection.MethodName, StringComparison.Ordinal)
                && identity.MethodName.Length > selection.MethodName.Length)
            .OrderByDescending(static selection => selection.MethodName.Length)
            .FirstOrDefault();
    }

    private static PhpTestCaseIdentity IdentityFromReport(JUnitXmlTestCase testCase)
    {
        string className = string.IsNullOrWhiteSpace(testCase.ClassName)
            ? testCase.SuiteName
            : testCase.ClassName!;
        string methodName = testCase.Name;
        if (PhpTestTooling.TrySplitSelector(methodName, out string fromNameClass, out string fromNameMethod))
        {
            className = fromNameClass;
            methodName = fromNameMethod;
        }
        else if (PhpTestTooling.TrySplitSelector(className, out string fromClass, out string fromClassMethod))
        {
            className = fromClass;
            methodName = string.IsNullOrWhiteSpace(methodName) ? fromClassMethod : methodName;
        }

        if (string.IsNullOrWhiteSpace(className) || string.Equals(className, "junit", StringComparison.OrdinalIgnoreCase))
            className = "pest";
        return new PhpTestCaseIdentity(
            WorkspaceId: string.Empty,
            ProjectPath: string.Empty,
            ClassName: PhpTestTooling.NormalizeClassName(className),
            MethodName: PhpTestTooling.NormalizeMethodName(methodName));
    }

    private static JUnitXmlParseResult ParseReport(
        string artifactPath,
        string operation,
        string? resultArtifactPath = null)
    {
        JUnitXmlParseResult report;
        try
        {
            report = JUnitXmlResultParser.ParseFile(artifactPath);
        }
        catch (TestArtifactParseException exception)
        {
            throw ReportFailure($"{operation} wrote an unreadable JUnit report: {exception.Message}", resultArtifactPath ?? artifactPath, exception);
        }

        if (report.Diagnostics.Count > 0)
            throw ReportFailure(
                $"{operation} produced an unusable JUnit report: {string.Join(" ", report.Diagnostics)}",
                resultArtifactPath ?? artifactPath);
        if (report.HasAggregateMismatch)
            throw ReportFailure(
                $"{operation} produced an aggregate-inconsistent JUnit report: "
                + string.Join(" ", report.AggregateMismatches.Select(static mismatch => mismatch.ToString())),
                resultArtifactPath ?? artifactPath);
        return report;
    }

    private static IReadOnlyList<PhpListedTest> ParseListing(
        string artifactPath,
        string operation)
    {
        try
        {
            return PhpListTestsXmlParser.Parse(File.ReadAllText(artifactPath));
        }
        catch (TestArtifactParseException exception)
        {
            throw ReportFailure(
                $"{operation} wrote an unreadable PHPUnit listing: {exception.Message}",
                artifactPath,
                exception);
        }
    }

    private static void EnsureAllSelectionsReported(
        IReadOnlyList<CaseBinding> selections,
        IReadOnlyList<ProviderCaseResult> results,
        string artifactPath)
    {
        var reported = results.Select(static result => result.TestCaseId).ToHashSet(StringComparer.Ordinal);
        foreach (CaseBinding selection in selections)
        {
            if (!reported.Contains(selection.Id))
                throw ReportFailure(
                    $"PHP test report did not report selected test case '{selection.Id}'.",
                    artifactPath);
        }
    }

    private static IReadOnlyList<CaseBinding> DecodeSelections(ContinuousTestProviderRunRequest request)
    {
        var selections = new List<CaseBinding>(request.TestCaseIds.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in request.TestCaseIds)
        {
            if (!PhpTestTooling.TryDecodeCaseId(id, out PhpTestCaseIdentity identity)
                || !string.Equals(identity.WorkspaceId, request.Workspace.WorkspaceId, StringComparison.Ordinal)
                || !PathComparer.Equals(identity.ProjectPath, request.Workspace.ProjectPath)
                || !seen.Add(id))
            {
                throw new ContinuousTestProviderException(
                    $"PHP test case ID '{id}' is not owned by this project.");
            }

            selections.Add(new CaseBinding(id, identity.ClassName, identity.MethodName));
        }

        return selections;
    }

    private static string EnsurePhp(ContinuousTestWorkspace workspace, string? frameworkOverride = null)
    {
        if (!IsPhpProjectFile(workspace.ProjectPath))
            throw new ContinuousTestProviderException(
                $"PHP continuous testing requires a composer.json project file: '{workspace.ProjectPath}'.");

        string? detected = PhpTestTooling.DetectFramework(workspace.ProjectPath);
        string? requested = (frameworkOverride ?? workspace.Framework)?.Trim().ToLowerInvariant();
        string? framework = requested ?? detected;
        if (framework is not (PhpTestTooling.PhpUnitFramework or PhpTestTooling.PestFramework))
            throw new ContinuousTestProviderException(
                $"PHP continuous testing requires composer.json to declare phpunit/phpunit or pestphp/pest: '{workspace.ProjectPath}'.");
        if (detected is not null && !string.Equals(framework, detected, StringComparison.Ordinal))
            throw new ContinuousTestProviderException(
                $"PHP continuous test provider cannot run framework '{framework}' for '{workspace.ProjectPath}'; composer.json selects '{detected}'.");

        PhpTestTooling.RunnerPath(workspace, framework);
        return framework;
    }

    private static ContinuousTestProviderException EmptySelection() =>
        new("PHP test run request selected no test case IDs; an empty selection cannot be reported green.");

    private static string? NormalizeListingPath(
        ContinuousTestWorkspace workspace,
        string? reportedPath)
    {
        if (string.IsNullOrWhiteSpace(reportedPath))
            return null;

        string projectRoot = Path.GetFullPath(PhpTestTooling.ProjectRoot(workspace));
        string workspaceRoot = Path.GetFullPath(workspace.WorkspaceRoot);
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(
                Path.IsPathRooted(reportedPath)
                    ? reportedPath
                    : Path.Combine(projectRoot, reportedPath));
        }
        catch (ArgumentException exception)
        {
            throw new ContinuousTestProviderException(
                $"PHP discovery reported an invalid test file path: '{reportedPath}'.",
                exception);
        }
        catch (NotSupportedException exception)
        {
            throw new ContinuousTestProviderException(
                $"PHP discovery reported an invalid test file path: '{reportedPath}'.",
                exception);
        }

        if (!IsInsideRoot(projectRoot, fullPath) || !IsInsideRoot(workspaceRoot, fullPath))
            throw new ContinuousTestProviderException(
                $"PHP discovery reported a test file outside the workspace/project root: '{reportedPath}'.");

        string relative = Path.GetRelativePath(workspaceRoot, fullPath);
        if (relative == "." || Path.IsPathRooted(relative))
            throw new ContinuousTestProviderException(
                $"PHP discovery reported a test file outside the workspace/project root: '{reportedPath}'.");

        return relative.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private static bool IsInsideRoot(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path);
        return relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, PathComparison)
            && !relative.StartsWith("../", StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }

    private static ContinuousTestProviderException ReportFailure(
        string message,
        string artifactPath,
        Exception? innerException = null) =>
        innerException is null
            ? new ContinuousTestProviderException(message) { ResultArtifactPath = artifactPath }
            : new ContinuousTestProviderException(message, innerException) { ResultArtifactPath = artifactPath };

    private static string NormalizeStatus(string status) =>
        status switch
        {
            "errored" => "failed",
            "failed" => "failed",
            "skipped" => "skipped",
            _ => "passed",
        };

    private static string AggregateStatus(IReadOnlyList<ProviderCaseResult> results) =>
        results.Any(static result => result.Status == "failed")
            ? "failed"
            : results.Count > 0 && results.All(static result => result.Status == "skipped")
                ? "skipped"
                : "passed";

    private static string FailureSummary(TestProcessResult result) =>
        FirstNonEmpty(result.StandardError, result.StandardOutput)
        ?? $"PHP test run failed with exit code {result.ExitCode}.";

    private static string? FirstNonEmpty(params string?[] values) =>
        values.Select(static value => value?.Trim())
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

    private static string NewRunId(ContinuousTestProviderRunRequest request) =>
        "ct-run:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join("|", request.Workspace.WorkspaceId, request.Workspace.ProjectPath,
                request.SelectedRevision, string.Join(",", request.TestCaseIds))))).ToLowerInvariant()[..24];

    private static string ResultId(string runId, string caseId) =>
        "php-result:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(runId + "|" + caseId)))
            .ToLowerInvariant()[..24];

    private static ContinuousTestProviderException StampGeneration(
        ContinuousTestProviderException exception,
        CtGenerationPaths paths) =>
        new(exception.Message, exception)
        {
            GenerationId = paths.GenerationId,
            ResultArtifactPath = exception.ResultArtifactPath,
        };

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed record CaseBinding(string Id, string ClassName, string MethodName)
    {
        public string Selector => $"{ClassName}::{MethodName}";
    }

    private sealed record PhpInvocation(
        TestProcessCommand Command,
        string ResultArtifactPath,
        IReadOnlyList<CaseBinding> Selections,
        IReadOnlyList<CaseBinding> Units)
    {
    }
}
