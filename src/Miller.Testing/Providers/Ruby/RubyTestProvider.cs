using System.Security.Cryptography;
using System.Text;
using Miller.Testing.Parsing;

namespace Miller.Testing.Providers.Ruby;

public sealed class RubyTestProvider : IContinuousTestProvider
{
    private readonly ITestProcessRunner _runner;
    private readonly CtGenerationHandoff _generations = new();

    public RubyTestProvider(ITestProcessRunner runner)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    public async Task<IReadOnlyList<ProviderTestCase>> DiscoverAsync(
        ContinuousTestWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        EnsureRuby(workspace);

        CtGenerationPaths paths = _generations.AllocateForDiscovery(workspace);
        try
        {
            paths.EnsureDirectories();
            TestProcessResult processResult = await _runner
                .RunAsync(RubyTestTooling.BuildDiscoveryCommand(workspace, paths), cancellationToken)
                .ConfigureAwait(false);
            string output = processResult.RequireCompleteStandardOutput("rspec --dry-run");
            if (processResult.ExitCode != 0)
                throw new ContinuousTestProviderException(DiscoveryFailure(processResult));

            RspecJsonParseResult report;
            try
            {
                report = RspecJsonParser.Parse(output);
            }
            catch (TestArtifactParseException exception)
            {
                throw new ContinuousTestProviderException(
                    $"RSpec discovery returned malformed JSON: {exception.Message}",
                    exception);
            }
            EnsureReportIsUsable(report, "RSpec discovery");
            return CasesFromReport(workspace, report);
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
        EnsureRuby(request.Workspace, request.Framework);

        CtGenerationPaths paths = _generations.TakeForRun(request.Workspace);
        try
        {
            paths.EnsureDirectories();
            IReadOnlyList<CaseBinding> selections = DecodeSelections(request);
            if (selections.Count == 0)
                throw new ContinuousTestProviderException(
                    "RSpec run request selected no test case IDs; an empty selection cannot be reported green.");

            DateTimeOffset started = DateTimeOffset.UtcNow;
            string runId = request.RunId ?? NewRunId(request);
            string artifactPath = RubyTestTooling.ResultArtifactPath(paths, runId);
            ResetArtifact(artifactPath);
            ContinuousTestWorkspace commandWorkspace = request.Workspace with
            {
                Command = request.Command,
                Framework = request.Framework,
            };
            TestProcessCommand command = RubyTestTooling.BuildRunCommand(
                commandWorkspace,
                paths,
                artifactPath,
                request.WholeSuite
                    ? []
                    : selections.Select(static selection => selection.Selector).ToArray(),
                request.WholeSuite);
            TestProcessResult processResult = await _runner
                .RunAsync(command, cancellationToken)
                .ConfigureAwait(false);

            if (!File.Exists(artifactPath))
                throw new ContinuousTestProviderException(
                    $"RSpec test run exited with code {processResult.ExitCode} without writing its JSON report: "
                    + FailureSummary(processResult))
                {
                    ResultArtifactPath = artifactPath,
                };

            RspecJsonParseResult report;
            try
            {
                report = RspecJsonParser.ParseFile(artifactPath);
            }
            catch (TestArtifactParseException exception)
            {
                throw new ContinuousTestProviderException(
                    $"RSpec test run wrote an unreadable JSON report: {exception.Message}",
                    exception)
                {
                    ResultArtifactPath = artifactPath,
                };
            }

            EnsureReportIsUsable(report, "RSpec test run", artifactPath);
            IReadOnlyList<ProviderCaseResult> caseResults = MapResults(
                request,
                selections,
                report,
                processResult,
                artifactPath,
                runId);
            if (processResult.ExitCode != 0
                && caseResults.All(static result => result.Status != "failed"))
            {
                throw new ContinuousTestProviderException(
                    $"RSpec test run exited with code {processResult.ExitCode} without a failed selected example: "
                    + FailureSummary(processResult))
                {
                    ResultArtifactPath = artifactPath,
                };
            }

            return new ProviderRunResult(
                RunId: runId,
                Status: AggregateStatus(caseResults),
                StartedAt: started,
                EndedAt: DateTimeOffset.UtcNow,
                CaseResults: caseResults,
                ResultArtifactPath: artifactPath,
                TestDisplayNames: caseResults
                    .Select(static result => result.Metadata.TryGetValue("full_description", out object? value)
                        && value is string description
                            ? description
                            : result.TestCaseId)
                    .ToArray())
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
        EnsureRuby(request.Workspace, request.Framework);
        IReadOnlyList<CaseBinding> selections = DecodeSelections(request);
        if (selections.Count == 0)
            throw new ContinuousTestProviderException(
                "RSpec run request selected no test case IDs; an empty selection cannot be reported green.");

        CtGenerationPaths paths = CtGenerationPaths.ResolveLatestOrFirst(request.Workspace);
        string runId = request.RunId ?? NewRunId(request);
        return RubyTestTooling.BuildRunCommand(
            request.Workspace with { Command = request.Command, Framework = request.Framework },
            paths,
            RubyTestTooling.ResultArtifactPath(paths, runId),
            request.WholeSuite
                ? []
                : selections.Select(static selection => selection.Selector).ToArray(),
            request.WholeSuite);
    }

    public IReadOnlyList<TestProcessCommand> BuildRunCommands(ContinuousTestProviderRunRequest request) =>
        [BuildRunCommand(request)];

    public static bool IsRubyProjectFile(string path) =>
        string.Equals(Path.GetFileName(path), RubyTestTooling.ProjectFileName, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<ProviderTestCase> CasesFromReport(
        ContinuousTestWorkspace workspace,
        RspecJsonParseResult report)
    {
        string projectRoot = RubyTestTooling.ProjectRoot(workspace);
        var usedSelectors = new HashSet<string>(StringComparer.Ordinal);
        var cases = new List<ProviderTestCase>(report.Examples.Count);
        foreach (RspecJsonExample example in report.Examples)
        {
            string relativePath = RelativeSpecPath(projectRoot, example);
            string locationSelector = LocationSelector(relativePath, example);
            string selector;
            if (usedSelectors.Add(locationSelector))
            {
                selector = locationSelector;
            }
            else
            {
                selector = example.Id;
                if (!usedSelectors.Add(selector))
                    throw new ContinuousTestProviderException(
                        $"RSpec discovery returned duplicate example ID '{example.Id}'.");
            }

            var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["kind"] = "rspec-example",
                ["example_id"] = example.Id,
                ["file_path"] = relativePath,
                ["line_number"] = example.LineNumber,
                ["rspec_version"] = report.Version,
                ["status"] = example.Status,
            };
            cases.Add(new ProviderTestCase(
                Id: RubyTestTooling.EncodeCaseId(
                    workspace.WorkspaceId,
                    workspace.ProjectPath,
                    relativePath,
                    example.Id,
                    selector),
                DisplayName: example.FullDescription,
                FullyQualifiedName: example.FullDescription,
                Selector: selector,
                Framework: RubyTestTooling.Framework,
                SourcePath: relativePath,
                Metadata: metadata,
                SymbolName: example.FullDescription));
        }

        return cases.OrderBy(static test => test.Id, StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<ProviderCaseResult> MapResults(
        ContinuousTestProviderRunRequest request,
        IReadOnlyList<CaseBinding> selections,
        RspecJsonParseResult report,
        TestProcessResult processResult,
        string artifactPath,
        string runId)
    {
        string projectRoot = RubyTestTooling.ProjectRoot(request.Workspace);
        var reportedCaseIds = new HashSet<string>(StringComparer.Ordinal);
        var usedSelectors = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<ProviderCaseResult>(Math.Max(selections.Count, report.Examples.Count));
        foreach (RspecJsonExample example in report.Examples)
        {
            string relativePath = RelativeSpecPath(projectRoot, example);
            CaseBinding? selection = selections.FirstOrDefault(
                candidate => ExampleMatches(projectRoot, candidate, example));
            string selector;
            string testCaseId;
            if (selection is not null)
            {
                selector = selection.Selector;
                testCaseId = selection.Id;
                if (!usedSelectors.Add(selector))
                    throw new ContinuousTestProviderException(
                        $"RSpec test report matched selector '{selector}' more than once.")
                    {
                        ResultArtifactPath = artifactPath,
                    };
            }
            else
            {
                selector = LocationSelector(relativePath, example);
                if (!usedSelectors.Add(selector))
                {
                    selector = example.Id;
                    if (!usedSelectors.Add(selector))
                        throw new ContinuousTestProviderException(
                            $"RSpec test report returned duplicate example ID '{example.Id}'.")
                        {
                            ResultArtifactPath = artifactPath,
                        };
                }

                testCaseId = RubyTestTooling.EncodeCaseId(
                    request.Workspace.WorkspaceId,
                    request.Workspace.ProjectPath,
                    relativePath,
                    example.Id,
                    selector);
            }

            if (!reportedCaseIds.Add(testCaseId))
                throw new ContinuousTestProviderException(
                    $"RSpec test report returned duplicate selected example '{testCaseId}'.")
                {
                    ResultArtifactPath = artifactPath,
                };

            results.Add(new ProviderCaseResult(
                Id: ResultId(runId, testCaseId),
                TestCaseId: testCaseId,
                Status: example.Status,
                ResultRevision: request.SelectedRevision,
                IndexIdentity: request.IndexIdentity,
                DurationSeconds: example.RunTime,
                FailureSummary: example.Status == "failed"
                    ? FirstNonEmpty(example.FailureMessage, processResult.StandardError)
                    : null,
                Metadata: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["artifact_path"] = artifactPath,
                    ["framework"] = RubyTestTooling.Framework,
                    ["example_id"] = example.Id,
                    ["full_description"] = example.FullDescription,
                }));
        }

        foreach (CaseBinding selection in selections)
        {
            if (reportedCaseIds.Contains(selection.Id))
                continue;
            if (processResult.ExitCode != 0)
            {
                results.Add(FailedResult(
                    request,
                    runId,
                    selection.Id,
                    FailureSummary(processResult),
                    artifactPath));
                continue;
            }

            throw new ContinuousTestProviderException(
                $"RSpec test report did not include selected example '{selection.Id}'.")
            {
                ResultArtifactPath = artifactPath,
            };
        }

        return results;
    }

    private static bool ExampleMatches(
        string projectRoot,
        CaseBinding selection,
        RspecJsonExample example)
    {
        if (!RubyTestTooling.TryRelativeSpecPath(projectRoot, example.FilePath, out string relativePath)
            || !PathComparer.Equals(relativePath, selection.SpecFilePath))
        {
            return false;
        }

        if (string.Equals(example.Id, selection.ExampleId, StringComparison.Ordinal))
            return true;

        return selection.Selector is { Length: > 0 }
            && string.Equals(LocationSelector(relativePath, example), selection.Selector, StringComparison.Ordinal);
    }

    private static string RelativeSpecPath(string projectRoot, RspecJsonExample example)
    {
        if (!RubyTestTooling.TryRelativeSpecPath(projectRoot, example.FilePath, out string relativePath))
            throw new ContinuousTestProviderException(
                $"RSpec reported an example outside the project root: '{example.FilePath}'.");
        return relativePath;
    }

    private static string LocationSelector(string relativePath, RspecJsonExample example) =>
        example.LineNumber is { } line
            ? $"{relativePath}:{line}"
            : example.Id;

    private static IReadOnlyList<CaseBinding> DecodeSelections(ContinuousTestProviderRunRequest request)
    {
        string projectRoot = RubyTestTooling.ProjectRoot(request.Workspace);
        var selections = new List<CaseBinding>(request.TestCaseIds.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in request.TestCaseIds)
        {
            if (!RubyTestTooling.TryDecodeCaseId(id, out RubyTestCaseIdentity identity)
                || !string.Equals(identity.WorkspaceId, request.Workspace.WorkspaceId, StringComparison.Ordinal)
                || !PathComparer.Equals(identity.ProjectPath, request.Workspace.ProjectPath)
                || !RubyTestTooling.TryRelativeSpecPath(projectRoot, identity.SpecFilePath, out string relativePath)
                || !string.Equals(relativePath, identity.SpecFilePath, StringComparison.Ordinal)
                || !seen.Add(id))
            {
                throw new ContinuousTestProviderException($"RSpec test case ID '{id}' is not owned by this project.");
            }

            selections.Add(new CaseBinding(
                id,
                identity.SpecFilePath,
                identity.ExampleId,
                identity.Selector ?? identity.ExampleId));
        }

        return selections;
    }

    private static void EnsureRuby(ContinuousTestWorkspace workspace, string? frameworkOverride = null)
    {
        string? framework = (frameworkOverride ?? workspace.Framework)?.Trim().ToLowerInvariant();
        if (framework is not null && framework != RubyTestTooling.Framework)
            throw new ContinuousTestProviderException(
                $"Ruby continuous test provider cannot run framework '{framework}' for '{workspace.ProjectPath}'.");
        if (!IsRubyProjectFile(workspace.ProjectPath))
            throw new ContinuousTestProviderException(
                $"Ruby continuous testing requires a Gemfile project file: '{workspace.ProjectPath}'.");
    }

    private static void EnsureReportIsUsable(
        RspecJsonParseResult report,
        string operation,
        string? artifactPath = null)
    {
        if (report.Diagnostics.Count > 0)
            throw ReportFailure(operation, string.Join(" ", report.Diagnostics), artifactPath);
        if (report.ErrorsOutsideExamplesCount > 0)
        {
            throw ReportFailure(
                operation,
                $"RSpec reported {report.ErrorsOutsideExamplesCount} error(s) outside examples.",
                artifactPath);
        }
    }

    private static ContinuousTestProviderException ReportFailure(
        string operation,
        string message,
        string? artifactPath) =>
        new($"{operation} produced an unusable report: {message}")
        {
            ResultArtifactPath = artifactPath,
        };

    private static ProviderCaseResult FailedResult(
        ContinuousTestProviderRunRequest request,
        string runId,
        string testCaseId,
        string failureSummary,
        string artifactPath) =>
        new(
            Id: ResultId(runId, testCaseId),
            TestCaseId: testCaseId,
            Status: "failed",
            ResultRevision: request.SelectedRevision,
            IndexIdentity: request.IndexIdentity,
            FailureSummary: failureSummary,
            Metadata: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["artifact_path"] = artifactPath,
                ["framework"] = RubyTestTooling.Framework,
            });

    private static string AggregateStatus(IReadOnlyList<ProviderCaseResult> results) =>
        results.Any(static result => result.Status == "failed")
            ? "failed"
            : results.Count > 0 && results.All(static result => result.Status == "skipped")
                ? "skipped"
                : "passed";

    private static string DiscoveryFailure(TestProcessResult result) =>
        $"RSpec discovery failed with exit code {result.ExitCode}: {FailureSummary(result)}";

    private static string FailureSummary(TestProcessResult result) =>
        FirstNonEmpty(result.StandardError, result.StandardOutput)
        ?? $"RSpec test run failed with exit code {result.ExitCode}.";

    private static string? FirstNonEmpty(params string?[] values) =>
        values.Select(static value => value?.Trim()).FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

    private static string NewRunId(ContinuousTestProviderRunRequest request) =>
        "ct-run:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join("|", request.Workspace.WorkspaceId, request.Workspace.ProjectPath,
                request.SelectedRevision, string.Join(",", request.TestCaseIds))))).ToLowerInvariant()[..24];

    private static string ResultId(string runId, string caseId) =>
        "ruby-result:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(runId + "|" + caseId)))
            .ToLowerInvariant()[..24];

    private static void ResetArtifact(string artifactPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
        try
        {
            if (File.Exists(artifactPath))
                File.Delete(artifactPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

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

    private sealed record CaseBinding(
        string Id,
        string SpecFilePath,
        string ExampleId,
        string Selector);
}
