using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Miller.Testing.Parsing;

namespace Miller.Testing;

public sealed class JavaScriptTestProvider : IContinuousTestProvider
{
    private const string TestCaseIdPrefix = "js-test:";
    private readonly ITestProcessRunner _runner;

    public JavaScriptTestProvider(ITestProcessRunner runner)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    public Task<IReadOnlyList<ProviderTestCase>> DiscoverAsync(
        ContinuousTestWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var packageRoot = PackageRoot(workspace);
        if (!Directory.Exists(packageRoot))
            return Task.FromResult<IReadOnlyList<ProviderTestCase>>([]);

        var cases = Directory
            .EnumerateFiles(packageRoot, "*", SearchOption.AllDirectories)
            .Select(path => RelativePathOrNull(packageRoot, path))
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
                    ["kind"] = "javascript-test-file",
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
            ? ParseResultArtifact(request, artifactPath)
            : [];

        if (result.ExitCode != 0 && caseResults.Count == 0)
        {
            if (request.TestCaseIds.Count == 0)
                throw new ContinuousTestProviderException(
                    $"JavaScript test run failed with exit code {result.ExitCode}: {FailureSummary(result)}");

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
        var packageRoot = PackageRoot(request.Workspace);
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
        var reporterArgs = IsolationArguments(framework, CacheDirectory(paths))
            .Concat(ReporterArguments(framework, artifactPath))
            .Concat(selectedFiles)
            .ToArray();

        if (!string.IsNullOrWhiteSpace(request.Command))
        {
            var tokens = SplitCommand(request.Command);
            if (tokens.Count == 0)
                throw new ContinuousTestProviderException("JavaScript test command must not be empty.");

            var args = tokens.Skip(1).ToList();
            if (RequiresPackageManagerArgumentSeparator(tokens[0]))
                args.Add("--");
            args.AddRange(reporterArgs);
            return new TestProcessCommand(tokens[0], args, packageRoot, WorkspaceEnvironment(request.Workspace, paths));
        }

        if (DetectPackageScript(packageRoot, framework) is { } scriptName)
        {
            var args = new List<string> { "run", scriptName, "--" };
            args.AddRange(reporterArgs);
            return new TestProcessCommand(
                PackageManager(packageRoot),
                args,
                packageRoot,
                WorkspaceEnvironment(request.Workspace, paths));
        }

        return framework switch
        {
            "vitest" => new TestProcessCommand(
                LocalBin(packageRoot, "vitest"),
                new[] { "run" }.Concat(reporterArgs).ToArray(),
                packageRoot,
                WorkspaceEnvironment(request.Workspace, paths)),
            "jest" => new TestProcessCommand(
                LocalBin(packageRoot, "jest"),
                reporterArgs,
                packageRoot,
                WorkspaceEnvironment(request.Workspace, paths)),
            "node-test" => new TestProcessCommand(
                "node",
                new[] { "--test" }.Concat(reporterArgs).ToArray(),
                packageRoot,
                WorkspaceEnvironment(request.Workspace, paths)),
            _ => throw UnsupportedFramework(framework, request.Workspace.ProjectPath),
        };
    }

    public static string TestCaseId(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) throw new ArgumentException("must not be empty", nameof(relativePath));
        return TestCaseIdPrefix + NormalizeRelativePath(relativePath);
    }

    private static IReadOnlyList<ProviderCaseResult> ParseResultArtifact(
        ContinuousTestProviderRunRequest request,
        string artifactPath)
    {
        var framework = RequiredFramework(request.Workspace);
        return framework switch
        {
            "vitest" or "jest" => ParseJestCompatibleJson(request, artifactPath),
            "node-test" => ParseNodeJunit(request, artifactPath),
            _ => throw UnsupportedFramework(framework, request.Workspace.ProjectPath),
        };
    }

    private static IReadOnlyList<ProviderCaseResult> ParseJestCompatibleJson(
        ContinuousTestProviderRunRequest request,
        string artifactPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(artifactPath));
        if (!document.RootElement.TryGetProperty("testResults", out var testResults)
            || testResults.ValueKind != JsonValueKind.Array)
            throw new ContinuousTestProviderException("JavaScript JSON test output did not contain a testResults array.");

        var packageRoot = PackageRoot(request.Workspace);
        var results = new List<ProviderCaseResult>();
        foreach (var fileResult in testResults.EnumerateArray())
        {
            var relativePath = RelativePathFromJsonResult(packageRoot, fileResult);
            var testCaseId = relativePath is null && request.TestCaseIds.Count == 1
                ? request.TestCaseIds[0]
                : relativePath is null
                    ? null
                    : TestCaseId(relativePath);
            if (string.IsNullOrWhiteSpace(testCaseId))
                continue;

            var assertionStatuses = AssertionStatuses(fileResult).ToArray();
            var status = FileStatus(fileResult, assertionStatuses);
            results.Add(new ProviderCaseResult(
                Id: StableId("test_result", request.Workspace.WorkspaceId, testCaseId, request.RunId),
                TestCaseId: testCaseId,
                Status: status,
                ResultRevision: request.SelectedRevision,
                IndexIdentity: request.IndexIdentity,
                DurationSeconds: DurationSeconds(fileResult),
                FailureSummary: FirstFailureSummary(fileResult),
                Metadata: new Dictionary<string, object?>
                {
                    ["artifact_path"] = artifactPath,
                    ["framework"] = RequiredFramework(request.Workspace),
                }));
        }

        return results;
    }

    private static IReadOnlyList<ProviderCaseResult> ParseNodeJunit(
        ContinuousTestProviderRunRequest request,
        string artifactPath)
    {
        var parsed = JunitTestResultParser.Parse(artifactPath);
        if (request.TestCaseIds.Count == 0)
            return [];

        var status = AggregateStatus(parsed.Cases.Select(row => row.Status));
        var duration = parsed.Cases
            .Select(row => row.DurationSeconds)
            .Where(durationSeconds => durationSeconds is not null)
            .Sum(durationSeconds => durationSeconds!.Value);
        var failureSummary = parsed.Cases
            .Select(row => row.FailureText)
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
        return request.TestCaseIds
            .Select(testCaseId => new ProviderCaseResult(
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
                    ["framework"] = RequiredFramework(request.Workspace),
                }))
            .ToArray();
    }

    private static IEnumerable<string> AssertionStatuses(JsonElement fileResult)
    {
        if (!fileResult.TryGetProperty("assertionResults", out var assertions) || assertions.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var assertion in assertions.EnumerateArray())
        {
            if (OptionalString(assertion, "status") is { } status)
                yield return status;
        }
    }

    private static string FileStatus(JsonElement fileResult, IReadOnlyList<string> assertionStatuses)
    {
        if (assertionStatuses.Count > 0)
            return AggregateStatus(assertionStatuses.Select(NormalizeStatus));

        return NormalizeStatus(OptionalString(fileResult, "status") ?? "passed");
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

    private static string RunStatus(IReadOnlyList<ProviderCaseResult> results) =>
        AggregateStatus(results.Select(row => row.Status));

    private static string NormalizeStatus(string status) =>
        status.ToLowerInvariant() switch
        {
            "fail" or "failed" or "failure" => "failed",
            "error" or "errored" => "errored",
            "skip" or "skipped" or "pending" or "todo" => "skipped",
            _ => "passed",
        };

    private static string? FirstFailureSummary(JsonElement fileResult)
    {
        if (!fileResult.TryGetProperty("assertionResults", out var assertions) || assertions.ValueKind != JsonValueKind.Array)
            return OptionalString(fileResult, "message");

        foreach (var assertion in assertions.EnumerateArray())
        {
            if (NormalizeStatus(OptionalString(assertion, "status") ?? "passed") != "failed")
                continue;

            if (assertion.TryGetProperty("failureMessages", out var messages)
                && messages.ValueKind == JsonValueKind.Array)
            {
                foreach (var message in messages.EnumerateArray())
                {
                    if (message.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(message.GetString()))
                        return message.GetString();
                }
            }

            if (OptionalString(assertion, "failureMessage") is { } failureMessage)
                return failureMessage;
        }

        return null;
    }

    private static double? DurationSeconds(JsonElement fileResult)
    {
        if (fileResult.TryGetProperty("perfStats", out var perfStats)
            && perfStats.ValueKind == JsonValueKind.Object
            && perfStats.TryGetProperty("runtime", out var runtime)
            && runtime.ValueKind == JsonValueKind.Number
            && runtime.TryGetDouble(out var runtimeMilliseconds))
            return runtimeMilliseconds / 1000.0;

        return null;
    }

    private static string? RelativePathFromJsonResult(string packageRoot, JsonElement fileResult)
    {
        var path = OptionalString(fileResult, "name")
            ?? OptionalString(fileResult, "testFilePath")
            ?? OptionalString(fileResult, "filepath");
        if (string.IsNullOrWhiteSpace(path))
            return null;

        return RelativePathOrNull(packageRoot, path);
    }

    private static string RequiredFramework(ContinuousTestWorkspace workspace)
    {
        var framework = workspace.Framework?.Trim().ToLowerInvariant()
            ?? DetectFramework(PackageRoot(workspace));
        return string.IsNullOrWhiteSpace(framework)
            ? throw UnsupportedFramework("<unspecified>", workspace.ProjectPath)
            : framework;
    }

    private static string[] ReporterArguments(string framework, string artifactPath) =>
        framework switch
        {
            "vitest" => ["--reporter=json", "--outputFile", artifactPath],
            "jest" => ["--json", "--outputFile", artifactPath],
            "node-test" => ["--test-reporter", "junit", "--test-reporter-destination", artifactPath],
            _ => throw UnsupportedFramework(framework, artifactPath),
        };

    private static string[] IsolationArguments(string framework, string cacheDirectory) =>
        framework switch
        {
            "vitest" => ["--cache.dir", cacheDirectory],
            "jest" => ["--cacheDirectory", cacheDirectory],
            _ => [],
        };

    private static string ResultArtifactPath(ContinuousTestProviderRunRequest request, CtGenerationPaths paths)
    {
        var framework = RequiredFramework(request.Workspace);
        var runKey = request.RunId ?? NewRunId(request);
        var runHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(runKey))).ToLowerInvariant();
        return Path.Combine(
            paths.ResultsDirectory,
            $"run-{runHash}.{(framework == "node-test" ? "xml" : "json")}");
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

    private static string PackageRoot(ContinuousTestWorkspace workspace)
    {
        var projectPath = Path.GetFullPath(workspace.ProjectPath);
        return string.Equals(Path.GetFileName(projectPath), "package.json", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(projectPath)!
            : Directory.Exists(projectPath)
                ? projectPath
                : Path.GetDirectoryName(projectPath)!;
    }

    private static string? DetectFramework(string packageRoot)
    {
        var packageJsonPath = Path.Combine(packageRoot, "package.json");
        if (!File.Exists(packageJsonPath))
            return null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
            var root = document.RootElement;
            if (HasPackage(root, "vitest") || ScriptContains(root, "vitest"))
                return "vitest";
            if (HasPackage(root, "jest")
                || HasPackage(root, "@vue/cli-plugin-unit-jest")
                || ScriptContains(root, "jest")
                || ScriptContains(root, "vue-cli-service test:unit"))
                return "jest";
            if (ScriptContains(root, "node --test")
                || ScriptContains(root, "node:test")
                || ScriptContains(root, "--test"))
                return "node-test";
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? DetectPackageScript(string packageRoot, string framework)
    {
        var packageJsonPath = Path.Combine(packageRoot, "package.json");
        if (!File.Exists(packageJsonPath))
            return null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
            if (!document.RootElement.TryGetProperty("scripts", out var scripts)
                || scripts.ValueKind != JsonValueKind.Object)
                return null;

            return scripts.EnumerateObject()
                .Where(script => script.Value.ValueKind == JsonValueKind.String)
                .Where(script => ScriptMatchesFramework(script.Name, script.Value.GetString() ?? string.Empty, framework))
                .OrderBy(script => ScriptPreference(script.Name))
                .ThenBy(script => script.Name, StringComparer.Ordinal)
                .Select(script => script.Name)
                .FirstOrDefault();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool ScriptMatchesFramework(string name, string command, string framework)
    {
        if (name.Contains("e2e", StringComparison.OrdinalIgnoreCase)
            || command.Contains("cypress", StringComparison.OrdinalIgnoreCase)
            || command.Contains("playwright", StringComparison.OrdinalIgnoreCase))
            return false;

        return framework switch
        {
            "vitest" => command.Contains("vitest", StringComparison.OrdinalIgnoreCase),
            "jest" => command.Contains("jest", StringComparison.OrdinalIgnoreCase)
                || command.Contains("vue-cli-service test:unit", StringComparison.OrdinalIgnoreCase),
            "node-test" => command.Contains("node --test", StringComparison.OrdinalIgnoreCase)
                || command.Contains("node:test", StringComparison.OrdinalIgnoreCase)
                || command.Contains("--test", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static int ScriptPreference(string name) =>
        name switch
        {
            "test" => 0,
            "test:unit" => 1,
            "unit" => 2,
            _ => 10,
        };

    private static string PackageManager(string packageRoot)
    {
        if (File.Exists(Path.Combine(packageRoot, "pnpm-lock.yaml")))
            return "pnpm";
        if (File.Exists(Path.Combine(packageRoot, "yarn.lock")))
            return "yarn";
        return "npm";
    }

    private static bool HasPackage(JsonElement root, string packageName)
    {
        foreach (var propertyName in new[] { "dependencies", "devDependencies", "peerDependencies", "optionalDependencies" })
        {
            if (root.TryGetProperty(propertyName, out var dependencies)
                && dependencies.ValueKind == JsonValueKind.Object
                && dependencies.TryGetProperty(packageName, out _))
                return true;
        }

        return false;
    }

    private static bool ScriptContains(JsonElement root, string value)
    {
        if (!root.TryGetProperty("scripts", out var scripts) || scripts.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var script in scripts.EnumerateObject())
        {
            if (script.Value.ValueKind == JsonValueKind.String
                && script.Value.GetString()?.Contains(value, StringComparison.OrdinalIgnoreCase) == true)
                return true;
        }

        return false;
    }

    private static IReadOnlyList<ProviderCoverageArtifact> DiscoverCoverageArtifacts(ContinuousTestWorkspace workspace)
    {
        var artifacts = new List<ProviderCoverageArtifact>();
        var paths = new HashSet<string>(PathStringComparer);
        var packageRoot = PackageRoot(workspace);
        AddCoverageArtifact(
            artifacts,
            paths,
            Path.Combine(packageRoot, "coverage", "lcov.info"),
            "lcov",
            packageRoot);
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

    private static string LocalBin(string packageRoot, string executableName) =>
        Path.Combine(
            packageRoot,
            "node_modules",
            ".bin",
            executableName + (OperatingSystem.IsWindows() ? ".cmd" : ""));

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
            ["NODE_COMPILE_CACHE"] = cacheDirectory,
        };
    }

    private static string CacheDirectory(CtGenerationPaths paths) =>
        Path.Combine(paths.GenerationRoot, "cache");

    private static string? TestFileFromId(string testCaseId) =>
        testCaseId.StartsWith(TestCaseIdPrefix, StringComparison.Ordinal)
            ? NormalizeRelativePath(testCaseId[TestCaseIdPrefix.Length..])
            : null;

    private static bool IsDiscoverableTestFile(string relativePath)
    {
        var segments = relativePath.Split('/');
        if (segments.Any(IsExcludedSegment))
            return false;

        var extension = Path.GetExtension(relativePath).ToLowerInvariant();
        if (extension is not (".js" or ".jsx" or ".ts" or ".tsx" or ".mjs" or ".cjs" or ".mts" or ".cts"))
            return false;

        var stem = Path.GetFileNameWithoutExtension(relativePath);
        return stem.EndsWith(".test", StringComparison.OrdinalIgnoreCase)
            || stem.EndsWith(".spec", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExcludedSegment(string segment) =>
        segment is "node_modules"
            or ".git"
            or ".claude"
            or "dist"
            or "build"
            or ".next"
            or "coverage"
            or "e2e"
            or "cypress"
            or "playwright";

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
                    ["framework"] = RequiredFramework(request.Workspace),
                    ["exit_code"] = result.ExitCode,
                }))
            .ToArray();
    }

    private static string FailureSummary(TestProcessResult result)
    {
        var text = !string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardError
            : result.StandardOutput;
        text = text.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return $"JavaScript test run failed with exit code {result.ExitCode}.";

        var firstLine = text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(firstLine)
            ? $"JavaScript test run failed with exit code {result.ExitCode}."
            : firstLine;
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

    private static string? OptionalString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

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

    private static bool RequiresPackageManagerArgumentSeparator(string executable)
    {
        var name = Path.GetFileNameWithoutExtension(executable).ToLowerInvariant();
        return name is "npm" or "pnpm" or "yarn";
    }

    private static ContinuousTestProviderException UnsupportedFramework(string framework, string projectPath) =>
        new($"Continuous test framework '{framework}' is unsupported for JavaScript project '{projectPath}'.");

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
            IFormattable formattable => formattable.ToString(format: null, System.Globalization.CultureInfo.InvariantCulture) ?? "",
            _ => part.ToString() ?? "",
        };

    private static StringComparer PathStringComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
