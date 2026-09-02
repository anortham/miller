using System.Collections.ObjectModel;
using Miller.Testing.Providers.Shared;

namespace Miller.Testing.Providers.Jvm;

public sealed class JvmTestProvider : IContinuousTestProvider
{
    private const string ProviderSource = "ct-provider:jvm";
    private readonly IReadOnlyDictionary<string, IJvmTestBackend> _backends;
    private readonly string _defaultBackend;
    private readonly CtGenerationHandoff _generations = new();

    public JvmTestProvider(ITestProcessRunner runner)
        : this([new GradleTestBackend(runner)])
    {
    }

    internal JvmTestProvider(IJvmTestBackend backend)
        : this([backend])
    {
    }

    internal JvmTestProvider(IEnumerable<IJvmTestBackend> backends)
    {
        ArgumentNullException.ThrowIfNull(backends);
        var map = new Dictionary<string, IJvmTestBackend>(StringComparer.OrdinalIgnoreCase);
        foreach (IJvmTestBackend backend in backends)
        {
            ArgumentNullException.ThrowIfNull(backend);
            if (string.IsNullOrWhiteSpace(backend.Discriminator))
                throw new ArgumentException("backend discriminator must not be empty", nameof(backends));
            if (!map.TryAdd(backend.Discriminator, backend))
                throw new ArgumentException(
                    $"backend discriminator '{backend.Discriminator}' was registered more than once.",
                    nameof(backends));
        }

        if (map.Count == 0)
            throw new ArgumentException("at least one backend is required", nameof(backends));
        _backends = map;
        _defaultBackend = map.ContainsKey(JvmTestBackendIds.Gradle)
            ? JvmTestBackendIds.Gradle
            : map.Keys.Order(StringComparer.OrdinalIgnoreCase).First();
    }

    public async Task<IReadOnlyList<ProviderTestCase>> DiscoverAsync(
        ContinuousTestWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        IJvmTestBackend backend = BackendFor(workspace);
        ValidateWorkspace(workspace, backend);
        CtGenerationPaths paths = _generations.AllocateForDiscovery(workspace);
        try
        {
            await backend.EnsureBuildAsync(workspace, paths, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<JvmTestBackendCase> discovered = await backend
                .DiscoverAsync(workspace, paths, cancellationToken)
                .ConfigureAwait(false);
            return discovered
                .Select(test => ToProviderCase(workspace, backend.Discriminator, test))
                .OrderBy(test => test.Id, StringComparer.Ordinal)
                .ToArray();
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
        IJvmTestBackend backend = BackendFor(request.Workspace);
        ValidateWorkspace(request.Workspace, backend);
        if (request.CoverageMode != ContinuousTestCoverageMode.None)
            throw new ContinuousTestProviderException("JVM continuous testing does not support coverage instrumentation.");

        CtGenerationPaths paths = _generations.TakeForRun(request.Workspace);
        try
        {
            await backend.EnsureBuildAsync(request.Workspace, paths, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<JvmTestSelection> selected = DecodeSelections(request, backend.Discriminator);
            if (!request.WholeSuite && selected.Count == 0)
                throw new ContinuousTestProviderException(
                    "JVM run request selected no test case IDs; an empty selection cannot be reported green.");

            DateTimeOffset started = DateTimeOffset.UtcNow;
            string runId = request.RunId ?? NewRunId(request, paths.GenerationId);
            JvmTestBackendRunResult backendResult = await backend
                .RunAsync(
                    request,
                    paths,
                    request.WholeSuite ? Array.Empty<JvmTestSelection>() : selected,
                    request.WholeSuite,
                    cancellationToken)
                .ConfigureAwait(false);
            IReadOnlyList<ProviderCaseResult> results = MapResults(
                request,
                backend.Discriminator,
                runId,
                backendResult);
            if (results.Count == 0)
                throw Failure("JVM backend returned no test cases.", backendResult.ResultArtifactPath);
            if (backendResult.ExitCode != 0 && results.All(result => result.Status is "passed" or "skipped"))
                throw Failure(
                    $"JVM test run exited with code {backendResult.ExitCode} without a failed test case.",
                    backendResult.ResultArtifactPath);

            return new ProviderRunResult(
                RunId: runId,
                Status: AggregateStatus(results.Select(result => result.Status)),
                StartedAt: started,
                EndedAt: DateTimeOffset.UtcNow,
                CaseResults: results,
                ResultArtifactPath: backendResult.ResultArtifactPath,
                TestDisplayNames: backendResult.Cases.Select(test => test.Selector).ToArray())
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
        IReadOnlyList<TestProcessCommand> commands = BuildRunCommands(request);
        return commands.Count == 1
            ? commands[0]
            : throw new ContinuousTestProviderException(
                $"JVM selection requires {commands.Count} invocations; use BuildRunCommands instead.");
    }

    public IReadOnlyList<TestProcessCommand> BuildRunCommands(ContinuousTestProviderRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        IJvmTestBackend backend = BackendFor(request.Workspace);
        ValidateWorkspace(request.Workspace, backend);
        IReadOnlyList<JvmTestSelection> selected = DecodeSelections(request, backend.Discriminator);
        if (!request.WholeSuite && selected.Count == 0)
            throw new ContinuousTestProviderException(
                "JVM run request selected no test case IDs; an empty selection cannot be reported green.");
        CtGenerationPaths paths = CtGenerationPaths.ResolveLatestOrFirst(request.Workspace);
        return backend.BuildRunCommands(
            request,
            paths,
            request.WholeSuite ? Array.Empty<JvmTestSelection>() : selected,
            request.WholeSuite);
    }

    public static bool IsJvmProjectFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string name = Path.GetFileName(path);
        return string.Equals(name, "pom.xml", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "build.gradle", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "build.gradle.kts", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "build.sbt", StringComparison.OrdinalIgnoreCase);
    }

    internal static string? FrameworkForProject(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetFileName(path).ToLowerInvariant() switch
        {
            "build.gradle" or "build.gradle.kts" => "gradle",
            "pom.xml" => "maven",
            "build.sbt" => "sbt",
            _ => null,
        };
    }

    private IJvmTestBackend BackendFor(ContinuousTestWorkspace workspace)
    {
        string discriminator = _defaultBackend;
        if (workspace.Metadata.TryGetValue("backend", out object? value)
            && value is string requested
            && !string.IsNullOrWhiteSpace(requested))
        {
            discriminator = requested.Trim();
        }
        else if (!string.IsNullOrWhiteSpace(workspace.Framework))
        {
            discriminator = workspace.Framework.Trim();
        }

        if (_backends.TryGetValue(discriminator, out IJvmTestBackend? backend))
            return backend;
        throw new ContinuousTestProviderException(
            $"JVM test backend '{discriminator}' is not available in this Miller build.");
    }

    private static void ValidateWorkspace(ContinuousTestWorkspace workspace, IJvmTestBackend backend)
    {
        if (!IsJvmProjectFile(workspace.ProjectPath))
            throw new ContinuousTestProviderException(
                $"JVM continuous testing requires a JVM build file: '{workspace.ProjectPath}'.");
        if (!string.Equals(workspace.Framework, backend.Discriminator, StringComparison.OrdinalIgnoreCase)
            && workspace.Framework is not null)
        {
            throw new ContinuousTestProviderException(
                $"JVM backend '{backend.Discriminator}' cannot run framework '{workspace.Framework}'.");
        }
    }

    private static ProviderTestCase ToProviderCase(
        ContinuousTestWorkspace workspace,
        string backend,
        JvmTestBackendCase test)
    {
        string? language = JvmTestTooling.LanguageLabel(test.SourcePath);
        var metadata = new Dictionary<string, object?>(
            test.Metadata ?? new Dictionary<string, object?>(),
            StringComparer.Ordinal)
        {
            ["language_family"] = "jvm",
            ["provider_source"] = ProviderSource,
            ["backend"] = backend,
            ["class_name"] = test.ClassName,
            ["method_name"] = test.MethodName,
            ["selector"] = test.Selector,
        };
        if (language is not null)
            metadata["language"] = language;

        return new ProviderTestCase(
            Id: JvmTestTooling.EncodeCaseId(
                workspace.WorkspaceId,
                workspace.ProjectPath,
                backend,
                test.ClassName,
                test.MethodName),
            DisplayName: test.DisplayName,
            FullyQualifiedName: test.Selector,
            Selector: test.Selector,
            Framework: backend,
            SourcePath: test.SourcePath,
            Metadata: metadata,
            SymbolName: test.MethodName,
            SymbolPath: test.SourcePath);
    }

    private static IReadOnlyList<JvmTestSelection> DecodeSelections(
        ContinuousTestProviderRunRequest request,
        string backend)
    {
        var selections = new List<JvmTestSelection>(request.TestCaseIds.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in request.TestCaseIds)
        {
            if (!JvmTestTooling.TryDecodeCaseId(id, out JvmTestCaseIdentity identity)
                || !string.Equals(identity.WorkspaceId, request.Workspace.WorkspaceId, StringComparison.Ordinal)
                || !PathsEqual(identity.ProjectPath, request.Workspace.ProjectPath)
                || !string.Equals(identity.Backend, backend, StringComparison.OrdinalIgnoreCase))
            {
                throw new ContinuousTestProviderException(
                    $"JVM test case id '{id}' does not belong to this project or is malformed.");
            }

            string selector = JvmTestTooling.Selector(identity.ClassName, identity.MethodName);
            if (seen.Add(selector))
            {
                selections.Add(new JvmTestSelection(identity.ClassName, identity.MethodName, selector));
            }
        }
        return selections;
    }

    private static IReadOnlyList<ProviderCaseResult> MapResults(
        ContinuousTestProviderRunRequest request,
        string backend,
        string runId,
        JvmTestBackendRunResult backendResult)
    {
        var rows = new List<ProviderCaseResult>(backendResult.Cases.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JvmTestBackendCaseResult testCase in backendResult.Cases)
        {
            string selector = testCase.Selector;
            if (!seen.Add(selector))
                throw Failure($"JVM backend reported duplicate test case '{selector}'.", backendResult.ResultArtifactPath);
            string id = JvmTestTooling.EncodeCaseId(
                request.Workspace.WorkspaceId,
                request.Workspace.ProjectPath,
                backend,
                testCase.ClassName,
                testCase.MethodName);
            string status = NormalizeStatus(testCase.Status);
            var metadata = new Dictionary<string, object?>(
                testCase.Metadata ?? new Dictionary<string, object?>(),
                StringComparer.Ordinal)
            {
                ["artifact_path"] = backendResult.ResultArtifactPath,
                ["backend"] = backend,
                ["class_name"] = testCase.ClassName,
                ["method_name"] = testCase.MethodName,
                ["raw_status"] = testCase.Status,
            };
            rows.Add(new ProviderCaseResult(
                Id: CtStableIds.StableId("test_result", request.Workspace.WorkspaceId, id, runId),
                TestCaseId: id,
                Status: status,
                ResultRevision: request.SelectedRevision,
                IndexIdentity: request.IndexIdentity,
                DurationSeconds: testCase.DurationSeconds,
                FailureSummary: testCase.FailureText,
                Metadata: metadata));
        }

        HashSet<string> selected = request.TestCaseIds
            .Select(id =>
            {
                if (!JvmTestTooling.TryDecodeCaseId(id, out JvmTestCaseIdentity identity))
                    return id;
                return JvmTestTooling.Selector(identity.ClassName, identity.MethodName);
            })
            .ToHashSet(StringComparer.Ordinal);
        if (!request.WholeSuite)
        {
            string[] reported = backendResult.Cases.Select(test => test.Selector).ToArray();
            string[] unexpected = reported.Except(selected, StringComparer.Ordinal).ToArray();
            if (unexpected.Length > 0)
                throw Failure(
                    $"JVM backend reported unselected test cases: {string.Join(", ", unexpected)}",
                    backendResult.ResultArtifactPath);
            string[] missing = selected.Except(reported, StringComparer.Ordinal).ToArray();
            if (missing.Length > 0)
                throw Failure(
                    $"JVM backend did not report selected test cases: {string.Join(", ", missing)}",
                    backendResult.ResultArtifactPath);
        }

        return rows.OrderBy(row => row.TestCaseId, StringComparer.Ordinal).ToArray();
    }

    private static string AggregateStatus(IEnumerable<string> statuses)
    {
        HashSet<string> statusSet = statuses.Select(NormalizeStatus).ToHashSet(StringComparer.Ordinal);
        if (statusSet.Count == 0 || statusSet.Contains("failed"))
            return statusSet.Count == 0 ? "passed" : "failed";
        return statusSet.SetEquals(["skipped"]) ? "skipped" : "passed";
    }

    private static string NormalizeStatus(string status) =>
        status.ToLowerInvariant() switch
        {
            "fail" or "failed" or "failure" or "error" or "errored" => "failed",
            "skip" or "skipped" or "pending" or "notrun" => "skipped",
            _ => "passed",
        };

    private static string NewRunId(ContinuousTestProviderRunRequest request, string generationId) =>
        CtStableIds.StableId(
            "ct_run",
            request.Workspace.WorkspaceId,
            request.Workspace.ProjectPath,
            request.SelectedRevision,
            generationId);

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static ContinuousTestProviderException Failure(string message, string? artifactPath = null) =>
        new(message) { ResultArtifactPath = artifactPath };

    private static ContinuousTestProviderException StampGeneration(
        ContinuousTestProviderException exception,
        CtGenerationPaths paths) =>
        new(exception.Message, exception)
        {
            GenerationId = paths.GenerationId,
            ResultArtifactPath = exception.ResultArtifactPath,
        };
}
