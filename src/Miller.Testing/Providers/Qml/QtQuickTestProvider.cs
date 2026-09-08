using System.Collections.Immutable;
using System.Text;

namespace Miller.Testing.Providers.Qml;

public sealed class QtQuickTestProvider : IContinuousTestProvider
{
    private const string Framework = "qt-quick-test";
    private const string ProviderSource = "ct-provider:qml";
    private const string ProjectIdMetadataKey = "project_id";

    private readonly IReadOnlyDictionary<string, IQtQuickTestBackend> _backends;
    private readonly string _defaultBackend;
    private readonly CtGenerationHandoff _generations = new();

    public QtQuickTestProvider(
        ITestProcessRunner runner,
        string cmakePath = "cmake",
        string ctestPath = "ctest",
        string? qmakePath = null,
        string? makePath = null)
        : this([
            new CMakeQtQuickTestBackend(runner, cmakePath, ctestPath),
            new QmakeQtQuickTestBackend(
                runner,
                qmakePath ?? QmakeQuickTestTooling.ResolveQmakePath(),
                makePath ?? QmakeQuickTestTooling.ResolveMakePath()),
        ])
    {
    }

    internal QtQuickTestProvider(IQtQuickTestBackend backend)
        : this([backend])
    {
    }

    internal QtQuickTestProvider(IEnumerable<IQtQuickTestBackend> backends)
    {
        ArgumentNullException.ThrowIfNull(backends);
        var map = new Dictionary<string, IQtQuickTestBackend>(StringComparer.OrdinalIgnoreCase);
        foreach (IQtQuickTestBackend backend in backends)
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
        _defaultBackend = map.ContainsKey(QtQuickTestBackendIds.CMake)
            ? QtQuickTestBackendIds.CMake
            : map.Keys.Order(StringComparer.OrdinalIgnoreCase).First();
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

    internal static ContinuousTestRunRecipe BuildDirectRunRecipe(ContinuousTestRunRecipeRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.ConfiguredCommand))
            return new ContinuousTestRunRecipe(request.WorkspaceId, request.ProjectPath,
                request.Framework ?? "unknown", request.WorkspaceRoot, [], request.Scope,
                UnavailableReason: "This provider has no mapped custom-command contract; generating a default command would discard the configured override.");
        if (request.ExcludeTraits is { Count: > 0 })
            return new ContinuousTestRunRecipe(request.WorkspaceId, request.ProjectPath,
                request.Framework ?? "qml", request.WorkspaceRoot, [], request.Scope,
                UnavailableReason: "QML providers have no mapped trait-exclusion contract; a default command would discard configured exclusions.");
        string projectDirectory = Path.GetDirectoryName(request.ProjectPath) ?? request.WorkspaceRoot;
        string buildDirectory = Path.Combine(request.WorkspaceRoot, ".miller", "manual-tests", Guid.NewGuid().ToString("N"));
        var metadata = request.ProjectMetadata is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(request.ProjectMetadata);
        metadata.TryAdd("configure_root", projectDirectory);
        bool qmake = metadata.GetValueOrDefault("backend")?.ToString() == QtQuickTestBackendIds.Qmake
            || Path.GetExtension(request.ProjectPath).Equals(".pro", StringComparison.OrdinalIgnoreCase);
        string? platform = Environment.GetEnvironmentVariable("QT_QPA_PLATFORM");
        IReadOnlyDictionary<string, string?> qtEnvironment = QtQuickTestTooling.WithDefaultQtPlatform(
            string.IsNullOrWhiteSpace(platform) ? null : new Dictionary<string, string?> { ["QT_QPA_PLATFORM"] = platform });
        var steps = new List<TestRunStep>();
        bool exact = request.IsExact && !string.IsNullOrWhiteSpace(request.TestSelector);
        if (qmake)
        {
            steps.Add(OperatingSystem.IsWindows()
                ? new TestRunStep("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command",
                    $"New-Item -ItemType Directory -Force -Path {TestRunStep.QuoteArgument(buildDirectory, true)} | Out-Null"], projectDirectory, "prepare")
                : new TestRunStep("mkdir", ["-p", "--", buildDirectory], projectDirectory, "prepare"));
            steps.Add(new TestRunStep("qmake", QmakeQuickTestTooling.BuildConfigureArguments(request.ProjectPath, buildDirectory), buildDirectory, "configure"));
            steps.Add(new TestRunStep("make", QmakeQuickTestTooling.BuildBuildArguments(), buildDirectory, "build"));
            steps.Add(new TestRunStep("make", ["check"], buildDirectory, Environment: qtEnvironment));
            exact = false;
        }
        else
        {
            string? configuration = metadata.GetValueOrDefault("configuration")?.ToString()
                ?? (OperatingSystem.IsWindows() ? "Release" : null);
            string configureRoot = metadata["configure_root"]?.ToString() ?? projectDirectory;
            steps.Add(new TestRunStep("cmake", QtQuickTestTooling.BuildCMakeConfigureArguments(configureRoot, buildDirectory, configuration), configureRoot, "configure"));
            steps.Add(new TestRunStep("cmake", QtQuickTestTooling.BuildCMakeBuildArguments(buildDirectory, configuration), configureRoot, "build"));
            steps.Add(new TestRunStep("ctest", QtQuickTestTooling.BuildCTestRunArguments(buildDirectory,
                Path.Combine(buildDirectory, "results.xml"), exact ? [request.TestSelector!] : [], !exact, configuration), buildDirectory, Environment: qtEnvironment));
        }
        return new ContinuousTestRunRecipe(request.WorkspaceId, request.ProjectPath, "qt-quick-test", projectDirectory,
            steps, exact ? TestSelectorScope.SingleTest : TestSelectorScope.ProjectSuite,
            exact ? request.TestSelector : null,
            Prerequisite: qmake ? "Qt qmake and make installed; runs the project's check target" : "CMake 3.21+ and the project's Qt SDK installed",
            ExcludeTraits: request.ExcludeTraits, IsExact: exact,
            UnavailableReason: !exact && request.TestSelector is not null
                ? "Source names do not identify a CTest or qmake target; recipe runs the project suite."
                : null);
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
        IQtQuickTestBackend backend = BackendFor(workspace);
        await backend.EnsureBuildAsync(workspace, paths, cancellationToken).ConfigureAwait(false);
        var discovery = await backend.DiscoverAsync(workspace, paths, cancellationToken).ConfigureAwait(false);
        return discovery
            .Select(test => ToProviderTestCase(workspace, backend.Discriminator, test))
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
        IQtQuickTestBackend backend = BackendFor(request.Workspace);
        await backend.EnsureBuildAsync(request.Workspace, paths, cancellationToken).ConfigureAwait(false);

        string artifactPath = ResultArtifactPath(paths, runId);
        DeleteArtifact(artifactPath);

        var selectedNames = request.WholeSuite
            ? []
            : request.TestCaseIds
                .Select(id => DecodeTestCaseId(request.Workspace, id))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToImmutableArray();
        var backendResult = await backend.RunAsync(
            request,
            paths,
            artifactPath,
            selectedNames,
            request.WholeSuite,
            cancellationToken).ConfigureAwait(false);
        if (backendResult.Cases.Count == 0)
            throw Failure("Qt Quick Test backend returned zero test cases.", backendResult.ResultArtifactPath);

        var selectedIds = request.TestCaseIds.ToHashSet(StringComparer.Ordinal);
        var results = backendResult.Cases
            .GroupBy(testCase => testCase.Name, StringComparer.Ordinal)
            .Select(group => ToProviderCaseResult(
                request,
                runId,
                backendResult.ResultArtifactPath,
                group.Key,
                group,
                selectedIds))
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
                $"Qt Quick Test backend did not report selected test cases: {string.Join(", ", missing)}",
                backendResult.ResultArtifactPath);
        if (results.Length == 0)
            throw Failure("Qt Quick Test backend returned no selected test cases.", backendResult.ResultArtifactPath);

        return new ProviderRunResult(
            RunId: runId,
            Status: AggregateStatus(results.Select(result => result.Status)),
            StartedAt: started,
            EndedAt: DateTimeOffset.UtcNow,
            CaseResults: results,
            ResultArtifactPath: backendResult.ResultArtifactPath,
            TestDisplayNames: backendResult.Cases
                .Select(testCase => testCase.Name)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray())
        {
            GenerationId = paths.GenerationId,
        };
    }

    private static ProviderTestCase ToProviderTestCase(
        ContinuousTestWorkspace workspace,
        string backendDiscriminator,
        QtQuickTestCase test)
    {
        var metadata = new Dictionary<string, object?>(test.Metadata, StringComparer.Ordinal);
        metadata["language"] = "qml";
        metadata["provider_source"] = ProviderSource;
        metadata["backend"] = backendDiscriminator;
        metadata["test_name"] = test.Name;
        metadata["command"] = test.Command;
        metadata["labels"] = test.Labels;
        metadata["working_directory"] = test.WorkingDirectory;

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
        IEnumerable<QtQuickTestBackendCaseResult> cases,
        IReadOnlySet<string> selectedIds)
    {
        string testCaseId = TestCaseId(request.Workspace, testName);
        if (selectedIds.Count > 0 && !selectedIds.Contains(testCaseId))
            return null;

        var rows = cases.ToArray();
        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["artifact_path"] = artifactPath,
            ["framework"] = Framework,
            ["provider_source"] = ProviderSource,
            ["test_name"] = testName,
        };
        foreach (QtQuickTestBackendCaseResult row in rows)
        {
            foreach ((string key, object? value) in row.Metadata)
            {
                if (!metadata.ContainsKey(key))
                    metadata[key] = value;
            }
        }

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
            Metadata: metadata);
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
            throw Failure($"Could not remove previous Qt Quick Test result artifact '{path}': {exception.Message}", path, exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw Failure($"Could not remove previous Qt Quick Test result artifact '{path}': {exception.Message}", path, exception);
        }
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

    private IQtQuickTestBackend BackendFor(ContinuousTestWorkspace workspace)
    {
        string discriminator = _defaultBackend;
        if (workspace.Metadata.TryGetValue("backend", out object? value)
            && value is string requested
            && !string.IsNullOrWhiteSpace(requested))
        {
            discriminator = requested.Trim();
        }

        if (_backends.TryGetValue(discriminator, out IQtQuickTestBackend? backend))
            return backend;

        throw new ContinuousTestProviderException(
            $"Qt Quick Test backend '{discriminator}' is not available in this Miller build.");
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
