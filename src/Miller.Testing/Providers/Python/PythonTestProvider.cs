using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using Miller.Testing.Parsing;

namespace Miller.Testing;

public sealed class PythonTestProvider : IContinuousTestProvider
{
    private const string TestCaseIdPrefix = "py-test:";
    private static readonly string[] ProjectFileNames =
    [
        "pyproject.toml",
        "pytest.ini",
        "tox.ini",
        "setup.cfg",
        "setup.py",
    ];

    private readonly ITestProcessRunner _runner;

    public PythonTestProvider(ITestProcessRunner runner)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    public Task<IReadOnlyList<ProviderTestCase>> DiscoverAsync(
        ContinuousTestWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var projectRoot = ProjectRoot(workspace);
        if (!Directory.Exists(projectRoot))
            return Task.FromResult<IReadOnlyList<ProviderTestCase>>([]);

        var cases = Directory
            .EnumerateFiles(projectRoot, "*.py", SearchOption.AllDirectories)
            .Select(path => RelativePathOrNull(projectRoot, path))
            .Where(relativePath => relativePath is not null)
            .Select(relativePath => relativePath!)
            .Where(IsDiscoverableTestFile)
            .Order(StringComparer.Ordinal)
            .Select(relativePath => new ProviderTestCase(
                Id: TestCaseId(relativePath),
                DisplayName: relativePath,
                FullyQualifiedName: relativePath,
                Selector: relativePath,
                Framework: RequiredFramework(workspace),
                SourcePath: relativePath,
                Metadata: new Dictionary<string, object?>
                {
                    ["kind"] = "python-test-file",
                }))
            .ToArray();
        return Task.FromResult<IReadOnlyList<ProviderTestCase>>(cases);
    }

    public async Task<ProviderRunResult> RunAsync(
        ContinuousTestProviderRunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var paths = CtGenerationPaths.Allocate(request.Workspace);
        try
        {
            return await RunInGenerationAsync(request, paths, cancellationToken).ConfigureAwait(false);
        }
        catch (ContinuousTestProviderException exception) when (exception.GenerationId is null)
        {
            throw StampGeneration(exception, paths);
        }
    }

    private async Task<ProviderRunResult> RunInGenerationAsync(
        ContinuousTestProviderRunRequest request,
        CtGenerationPaths paths,
        CancellationToken cancellationToken)
    {
        var command = BuildRunCommand(request, paths);
        var result = await _runner.RunAsync(command, cancellationToken).ConfigureAwait(false);
        var artifactPath = ResultArtifactPath(request, paths);
        var caseResults = File.Exists(artifactPath)
            ? ParsePytestJunit(request, artifactPath)
            : [];

        if (result.ExitCode != 0 && caseResults.Count == 0)
        {
            if (request.TestCaseIds.Count == 0)
                throw new ContinuousTestProviderException(
                    $"Python test run failed with exit code {result.ExitCode}: {FailureSummary(result)}");

            caseResults = FailedSelectedCaseResults(request, result, artifactPath);
        }

        return new ProviderRunResult(
            RunId: request.RunId ?? NewRunId(request),
            Status: RunStatus(caseResults),
            CaseResults: caseResults,
            ResultArtifactPath: File.Exists(artifactPath) ? artifactPath : null,
            CoverageArtifacts: DiscoverCoverageArtifacts(request.Workspace))
        {
            GenerationId = paths.GenerationId,
        };
    }

    /// <summary>
    /// Preview/test seam: builds the run command against the latest existing generation (or the
    /// would-be first). Production runs never use it — <see cref="RunAsync"/> allocates its own
    /// generation and builds every command and result path from that one handle.
    /// </summary>
    public TestProcessCommand BuildRunCommand(ContinuousTestProviderRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return BuildRunCommand(request, CtGenerationPaths.ResolveLatestOrFirst(request.Workspace));
    }

    private TestProcessCommand BuildRunCommand(
        ContinuousTestProviderRunRequest request,
        CtGenerationPaths paths)
    {
        var framework = RequiredFramework(request.Workspace);
        if (framework != "pytest")
            throw UnsupportedFramework(framework, request.Workspace.ProjectPath);

        var projectRoot = ProjectRoot(request.Workspace);
        paths.EnsureDirectories();
        Directory.CreateDirectory(CacheDirectory(paths));
        var artifactPath = ResultArtifactPath(request, paths);
        Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);

        var selectedFiles = request.TestCaseIds
            .Select(TestFileFromId)
            .OfType<string>()
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var pytestArgs = new[]
            {
                $"--junitxml={artifactPath}",
                "-o",
                $"cache_dir={CacheDirectory(paths)}",
            }
            .Concat(selectedFiles)
            .ToArray();

        if (!string.IsNullOrWhiteSpace(request.Command))
        {
            var tokens = SplitCommand(request.Command);
            if (tokens.Count == 0)
                throw new ContinuousTestProviderException("Python test command must not be empty.");

            return new TestProcessCommand(
                tokens[0],
                tokens.Skip(1).Concat(pytestArgs).ToArray(),
                projectRoot,
                WorkspaceEnvironment(request.Workspace, paths));
        }

        if (File.Exists(Path.Combine(projectRoot, "uv.lock")))
        {
            return new TestProcessCommand(
                "uv",
                new[] { "run", "python", "-m", "pytest" }.Concat(pytestArgs).ToArray(),
                projectRoot,
                WorkspaceEnvironment(request.Workspace, paths));
        }

        return new TestProcessCommand(
            LocalPython(projectRoot),
            new[] { "-m", "pytest" }.Concat(pytestArgs).ToArray(),
            projectRoot,
            WorkspaceEnvironment(request.Workspace, paths));
    }

    public static string TestCaseId(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) throw new ArgumentException("must not be empty", nameof(relativePath));
        return TestCaseIdPrefix + NormalizeRelativePath(relativePath);
    }

    public static bool IsPythonProjectFile(string path) =>
        ProjectFileNames.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<ProviderCaseResult> ParsePytestJunit(
        ContinuousTestProviderRunRequest request,
        string artifactPath)
    {
        var parsed = JunitTestResultParser.Parse(artifactPath);
        var projectRoot = ProjectRoot(request.Workspace);
        var selectedIds = request.TestCaseIds.ToHashSet(StringComparer.Ordinal);
        var groups = new Dictionary<string, List<ParsedTestArtifactCase>>(StringComparer.Ordinal);

        foreach (var testCase in parsed.Cases)
        {
            var testCaseId = TestCaseIdFromParsedCase(projectRoot, testCase)
                ?? (selectedIds.Count == 1 ? selectedIds.Single() : null);
            if (testCaseId is null)
                continue;
            if (selectedIds.Count > 0 && !selectedIds.Contains(testCaseId))
                continue;
            if (!groups.TryGetValue(testCaseId, out var cases))
            {
                cases = [];
                groups[testCaseId] = cases;
            }

            cases.Add(testCase);
        }

        return groups
            .OrderBy(row => row.Key, StringComparer.Ordinal)
            .Select(row => ToProviderResult(request, artifactPath, row.Key, row.Value))
            .ToArray();
    }

    private static ProviderCaseResult ToProviderResult(
        ContinuousTestProviderRunRequest request,
        string artifactPath,
        string testCaseId,
        IReadOnlyList<ParsedTestArtifactCase> cases)
    {
        var status = AggregateStatus(cases.Select(row => row.Status));
        var duration = cases
            .Select(row => row.DurationSeconds)
            .Where(durationSeconds => durationSeconds is not null)
            .Sum(durationSeconds => durationSeconds!.Value);
        var failureSummary = cases
            .Select(row => row.FailureText)
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
        return new ProviderCaseResult(
            Id: StableId("test_result", request.Workspace.WorkspaceId, testCaseId, request.RunId),
            TestCaseId: testCaseId,
            Status: status,
            ResultRevision: request.SelectedRevision,
            IndexIdentity: request.IndexIdentity,
            DurationSeconds: duration,
            FailureSummary: failureSummary,
            Metadata: new Dictionary<string, object?>
            {
                ["artifact_path"] = artifactPath,
                ["framework"] = "pytest",
            });
    }

    private static string? TestCaseIdFromParsedCase(string projectRoot, ParsedTestArtifactCase testCase)
    {
        if (string.IsNullOrWhiteSpace(testCase.ClassName))
            return null;

        var segments = testCase.ClassName
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var count = segments.Length; count >= 1; count--)
        {
            var relativePath = string.Join("/", segments.Take(count)) + ".py";
            if (File.Exists(Path.Combine(projectRoot, relativePath)))
                return TestCaseId(relativePath);
        }

        return null;
    }

    private static IReadOnlyList<ProviderCaseResult> FailedSelectedCaseResults(
        ContinuousTestProviderRunRequest request,
        TestProcessResult result,
        string artifactPath)
    {
        var summary = FailureSummary(result);
        return request.TestCaseIds
            .Select(testCaseId => new ProviderCaseResult(
                Id: StableId("test_result", request.Workspace.WorkspaceId, testCaseId, request.RunId),
                TestCaseId: testCaseId,
                Status: "failed",
                ResultRevision: request.SelectedRevision,
                IndexIdentity: request.IndexIdentity,
                FailureSummary: summary,
                Metadata: new Dictionary<string, object?>
                {
                    ["artifact_path"] = artifactPath,
                    ["framework"] = "pytest",
                    ["exit_code"] = result.ExitCode,
                }))
            .ToArray();
    }

    private static IReadOnlyList<ProviderCoverageArtifact> DiscoverCoverageArtifacts(ContinuousTestWorkspace workspace)
    {
        var artifacts = new List<ProviderCoverageArtifact>();
        var paths = new HashSet<string>(PathStringComparer);
        var projectRoot = ProjectRoot(workspace);
        AddCoverageArtifact(
            artifacts,
            paths,
            Path.Combine(projectRoot, "coverage.xml"),
            "cobertura",
            projectRoot);
        AddCoverageArtifact(
            artifacts,
            paths,
            Path.Combine(projectRoot, "coverage", "lcov.info"),
            "lcov",
            projectRoot);
        AddCoverageArtifact(
            artifacts,
            paths,
            Path.Combine(workspace.BuildOutputRoot, "coverage.xml"),
            "cobertura",
            workspace.BuildOutputRoot);
        AddCoverageArtifact(
            artifacts,
            paths,
            Path.Combine(workspace.BuildOutputRoot, "coverage", "lcov.info"),
            "lcov",
            workspace.BuildOutputRoot);
        return artifacts;
    }

    private static void AddCoverageArtifact(
        List<ProviderCoverageArtifact> artifacts,
        HashSet<string> paths,
        string artifactPath,
        string parser,
        string artifactRoot)
    {
        if (!File.Exists(artifactPath))
            return;

        var fullPath = Path.GetFullPath(artifactPath);
        if (!paths.Add(fullPath))
            return;

        artifacts.Add(new ProviderCoverageArtifact(
            ArtifactPath: fullPath,
            Parser: parser,
            ArtifactRoot: artifactRoot));
    }

    private static string RequiredFramework(ContinuousTestWorkspace workspace)
    {
        var framework = workspace.Framework?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(framework) || framework == "python")
            return "pytest";
        return framework;
    }

    private static string ProjectRoot(ContinuousTestWorkspace workspace)
    {
        var projectPath = Path.GetFullPath(workspace.ProjectPath);
        return IsPythonProjectFile(projectPath)
            ? Path.GetDirectoryName(projectPath)!
            : Directory.Exists(projectPath)
                ? projectPath
                : Path.GetDirectoryName(projectPath)!;
    }

    private static string ResultArtifactPath(ContinuousTestProviderRunRequest request, CtGenerationPaths paths)
    {
        var runKey = request.RunId ?? NewRunId(request);
        var runHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(runKey))).ToLowerInvariant();
        return Path.Combine(paths.ResultsDirectory, $"run-{runHash}.xml");
    }

    private static ContinuousTestProviderException StampGeneration(
        ContinuousTestProviderException exception,
        CtGenerationPaths paths) =>
        new(exception.Message, exception)
        {
            GenerationId = paths.GenerationId,
            ResultArtifactPath = exception.ResultArtifactPath,
        };

    private static string NewRunId(ContinuousTestProviderRunRequest request) =>
        StableId(
            "ct_run",
            request.Workspace.WorkspaceId,
            request.Workspace.ProjectPath,
            request.SelectedRevision,
            DateTimeOffset.UtcNow.UtcTicks);

    private static string RunStatus(IReadOnlyList<ProviderCaseResult> results) =>
        AggregateStatus(results.Select(row => row.Status));

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

    private static string FailureSummary(TestProcessResult result)
    {
        var text = !string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardError
            : result.StandardOutput;
        text = text.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return $"Python test run failed with exit code {result.ExitCode}.";

        var firstLine = text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(firstLine)
            ? $"Python test run failed with exit code {result.ExitCode}."
            : firstLine;
    }

    private static string? TestFileFromId(string testCaseId) =>
        testCaseId.StartsWith(TestCaseIdPrefix, StringComparison.Ordinal)
            ? NormalizeRelativePath(testCaseId[TestCaseIdPrefix.Length..])
            : null;

    private static bool IsDiscoverableTestFile(string relativePath)
    {
        var segments = relativePath.Split('/');
        if (segments.Any(IsExcludedSegment))
            return false;

        var fileName = Path.GetFileName(relativePath);
        return fileName.StartsWith("test_", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith("_test.py", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExcludedSegment(string segment) =>
        segment is ".git"
            or ".hg"
            or ".svn"
            or ".claude"
            or ".venv"
            or "venv"
            or "env"
            or "__pycache__"
            or ".pytest_cache"
            or ".mypy_cache"
            or ".ruff_cache"
            or ".tox"
            or ".worktrees"
            or "build"
            or "dist"
            or "node_modules";

    private static IReadOnlyDictionary<string, string?> WorkspaceEnvironment(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths)
    {
        Directory.CreateDirectory(paths.TempDirectory);
        var cacheDirectory = CacheDirectory(paths);
        Directory.CreateDirectory(cacheDirectory);
        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [CtEnvironment.WorkspaceRoot] = workspace.WorkspaceRoot,
            ["TMPDIR"] = paths.TempDirectory,
            ["TMP"] = paths.TempDirectory,
            ["TEMP"] = paths.TempDirectory,
            ["PYTHONPYCACHEPREFIX"] = cacheDirectory,
        };
    }

    private static string CacheDirectory(CtGenerationPaths paths) =>
        Path.Combine(paths.GenerationRoot, "cache");

    private static string LocalPython(string projectRoot)
    {
        var venvPython = OperatingSystem.IsWindows()
            ? Path.Combine(projectRoot, ".venv", "Scripts", "python.exe")
            : Path.Combine(projectRoot, ".venv", "bin", "python");
        if (File.Exists(venvPython))
            return venvPython;
        return OperatingSystem.IsWindows() ? "python" : "python3";
    }

    private static string? RelativePathOrNull(string root, string path)
    {
        var fullPath = Path.GetFullPath(path);
        var relativePath = Path.GetRelativePath(Path.GetFullPath(root), fullPath);
        if (relativePath == "."
            || relativePath.StartsWith("..", PathComparison)
            || Path.IsPathRooted(relativePath))
            return null;

        return NormalizeRelativePath(relativePath);
    }

    private static string NormalizeRelativePath(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');

    private static IReadOnlyList<string> SplitCommand(string command)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuote = false;
        char quoteChar = '\0';
        foreach (var c in command)
        {
            if ((c == '"' || c == '\'') && (!inQuote || c == quoteChar))
            {
                inQuote = !inQuote;
                quoteChar = inQuote ? c : '\0';
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuote)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
            result.Add(current.ToString());
        return result;
    }

    private static ContinuousTestProviderException UnsupportedFramework(string framework, string projectPath) =>
        new($"Continuous test framework '{framework}' is unsupported for Python project '{projectPath}'.");

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;


    private static string StableId(string @namespace, params object?[] parts)
    {
        var normalized = string.Join("\x1f", parts.Select(PartToString));
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        var hex = Convert.ToHexString(digest).ToLowerInvariant()[..24];
        return $"{@namespace}:{hex}";
    }

    private static string PartToString(object? part) =>
        part switch
        {
            null => "",
            IFormattable formattable => formattable.ToString(format: null, CultureInfo.InvariantCulture) ?? "",
            _ => part.ToString() ?? "",
        };

    private static StringComparer PathStringComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
