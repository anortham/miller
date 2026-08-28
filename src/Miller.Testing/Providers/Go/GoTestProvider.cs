using System.Security.Cryptography;
using System.Text;

namespace Miller.Testing;

public sealed class GoTestProvider : IContinuousTestProvider
{
    private readonly ITestProcessRunner _runner;
    private readonly CtGenerationHandoff _generations = new();

    public GoTestProvider(ITestProcessRunner runner)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    public async Task<IReadOnlyList<ProviderTestCase>> DiscoverAsync(
        ContinuousTestWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        EnsureGo(workspace);

        CtGenerationPaths paths = _generations.AllocateForDiscovery(workspace);
        try
        {
            paths.EnsureDirectories();
            TestProcessResult versionResult = await _runner
                .RunAsync(GoTestTooling.BuildVersionCommand(workspace, paths), cancellationToken)
                .ConfigureAwait(false);
            string versionOutput = versionResult.RequireCompleteStandardOutput("go version");
            if (versionResult.ExitCode != 0)
                throw new ContinuousTestProviderException(DiscoveryFailure(versionResult, "go version"));
            if (!GoTestTooling.TryParseVersion(versionOutput, out Version? version)
                || version is null)
                throw new ContinuousTestProviderException(
                    $"Go continuous testing could not determine the installed Go version from '{versionOutput.Trim()}'.");
            if (!GoTestTooling.IsSupportedVersion(version))
                throw new ContinuousTestProviderException(
                    $"Go continuous testing requires Go {GoTestTooling.MinimumMajor}.{GoTestTooling.MinimumMinor} or newer; "
                    + $"detected {version}.");

            TestProcessResult environmentResult = await _runner
                .RunAsync(GoTestTooling.BuildEnvironmentCommand(workspace, paths), cancellationToken)
                .ConfigureAwait(false);
            string environmentOutput = environmentResult.RequireCompleteStandardOutput("go env");
            if (environmentResult.ExitCode != 0)
                throw new ContinuousTestProviderException(DiscoveryFailure(environmentResult, "go env"));
            IReadOnlyDictionary<string, string> environment = GoTestTooling.ParseEnvironment(environmentOutput);

            string fallbackModule = GoTestTooling.ReadModulePath(workspace.ProjectPath)
                ?? throw new ContinuousTestProviderException(
                    $"Go module '{workspace.ProjectPath}' does not declare a module path.");
            TestProcessResult listResult = await _runner
                .RunAsync(GoTestTooling.BuildListCommand(workspace, paths), cancellationToken)
                .ConfigureAwait(false);
            string listOutput = listResult.RequireCompleteStandardOutput("go list");
            if (listResult.ExitCode != 0)
                throw new ContinuousTestProviderException(DiscoveryFailure(listResult, "go list"));
            IReadOnlyList<GoTestTooling.GoPackageInfo> packages =
                GoTestTooling.ParsePackageList(listOutput, fallbackModule);

            var cases = new List<ProviderTestCase>();
            foreach (GoTestTooling.GoPackageInfo package in packages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TestProcessResult testListResult = await _runner
                    .RunAsync(
                        GoTestTooling.BuildTestListCommand(workspace, paths, package.ImportPath),
                        cancellationToken)
                    .ConfigureAwait(false);
                string testListOutput = testListResult.RequireCompleteStandardOutput("go test -list");
                if (testListResult.ExitCode != 0)
                    throw new ContinuousTestProviderException(
                        DiscoveryFailure(testListResult, $"go test -list for {package.ImportPath}"));
                GoTestListResult listed = GoTestListParser.Parse(testListOutput);
                if (listed.HasMalformedLines)
                    throw new ContinuousTestProviderException(
                        $"go test -list for package '{package.ImportPath}' returned unrecognized output.");
                foreach (string testName in listed.Names)
                    cases.Add(ToProviderCase(workspace, package, testName, version, environment));
            }

            return cases.OrderBy(test => test.Id, StringComparer.Ordinal).ToArray();
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
        EnsureGo(request.Workspace);
        if (!string.IsNullOrWhiteSpace(request.Command ?? request.Workspace.Command))
            throw new ContinuousTestProviderException("Go continuous testing does not accept a custom command.");

        CtGenerationPaths paths = _generations.TakeForRun(request.Workspace);
        try
        {
            paths.EnsureDirectories();
            IReadOnlyList<GoRunGroup> groups = Groups(request);
            if (groups.Count == 0)
                throw new ContinuousTestProviderException(
                    "Go continuous testing received no test case IDs; an empty selection cannot be reported green.");

            DateTimeOffset started = DateTimeOffset.UtcNow;
            string runId = request.RunId ?? NewRunId(request);
            string artifactPath = ResultArtifactPath(paths, runId);
            ResetArtifact(artifactPath);
            var results = new List<ProviderCaseResult>();
            foreach (GoRunGroup group in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyList<string> names = request.WholeSuite
                    ? []
                    : group.Cases.Select(test => test.TestName).ToArray();
                TestProcessCommand command = GoTestTooling.BuildRunCommand(
                    request.Workspace,
                    paths,
                    group.ImportPath,
                    names);
                TestProcessResult processResult = await _runner
                    .RunAsync(command, cancellationToken)
                    .ConfigureAwait(false);
                string output = processResult.RequireCompleteStandardOutput("go test -json");
                AppendArtifact(artifactPath, group.ImportPath, output, processResult.StandardError);
                GoTestJsonParseResult parsed = GoTestJsonParser.Parse(output);
                if (parsed.HasMalformedLines)
                    throw new ContinuousTestProviderException(
                        $"go test -json for package '{group.ImportPath}' returned malformed JSON.")
                    {
                        ResultArtifactPath = artifactPath,
                    };
                results.AddRange(ParseGroup(request, runId, group, processResult, parsed, artifactPath));
            }

            return new ProviderRunResult(
                RunId: runId,
                Status: AggregateStatus(results),
                StartedAt: started,
                EndedAt: DateTimeOffset.UtcNow,
                CaseResults: results,
                ResultArtifactPath: File.Exists(artifactPath) ? artifactPath : null,
                TestDisplayNames: groups
                    .SelectMany(group => group.Cases.Select(test => test.TestName))
                    .Distinct(StringComparer.Ordinal)
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
        EnsureGo(request.Workspace);
        GoRunGroup group = Groups(request).FirstOrDefault()
            ?? throw new ContinuousTestProviderException("Go run request contains no decodable test case IDs.");
        IReadOnlyList<string> names = request.WholeSuite
            ? []
            : group.Cases.Select(test => test.TestName).ToArray();
        return GoTestTooling.BuildRunCommand(
            request.Workspace,
            CtGenerationPaths.ResolveLatestOrFirst(request.Workspace),
            group.ImportPath,
            names);
    }

    public IReadOnlyList<TestProcessCommand> BuildRunCommands(ContinuousTestProviderRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureGo(request.Workspace);
        CtGenerationPaths paths = CtGenerationPaths.ResolveLatestOrFirst(request.Workspace);
        return Groups(request)
            .Select(group => GoTestTooling.BuildRunCommand(
                request.Workspace,
                paths,
                group.ImportPath,
                request.WholeSuite ? [] : group.Cases.Select(test => test.TestName).ToArray()))
            .ToArray();
    }

    public static bool IsGoProjectFile(string path) =>
        string.Equals(Path.GetFileName(path), "go.mod", StringComparison.OrdinalIgnoreCase);

    private static ProviderTestCase ToProviderCase(
        ContinuousTestWorkspace workspace,
        GoTestTooling.GoPackageInfo package,
        string testName,
        Version version,
        IReadOnlyDictionary<string, string> environment)
    {
        string sourcePath = SourcePath(workspace, package.Directory);
        string goVersion = EnvironmentValue(environment, "GOVERSION", $"go{version}")!;
        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["kind"] = "test",
            ["module"] = package.ModulePath,
            ["import_path"] = package.ImportPath,
            ["package_dir"] = sourcePath,
            ["test_name"] = testName,
            ["go_version"] = goVersion,
            ["toolchain"] = goVersion,
            ["gowork"] = EnvironmentValue(environment, "GOWORK", "off"),
            ["goos"] = EnvironmentValue(environment, "GOOS", string.Empty),
            ["goarch"] = EnvironmentValue(environment, "GOARCH", string.Empty),
            ["cgo_enabled"] = EnvironmentValue(environment, "CGO_ENABLED", string.Empty),
            ["goflags"] = EnvironmentValue(environment, "GOFLAGS", string.Empty),
        };
        return new ProviderTestCase(
            Id: GoTestTooling.EncodeCaseId(
                workspace.WorkspaceId,
                workspace.ProjectPath,
                package.ModulePath,
                package.ImportPath,
                testName),
            DisplayName: $"{package.ImportPath}/{testName}",
            FullyQualifiedName: $"{package.ImportPath}/{testName}",
            Selector: testName,
            Framework: GoTestTooling.Framework,
            SourcePath: sourcePath,
            Metadata: metadata,
            SymbolName: testName);
    }

    private static IReadOnlyList<ProviderCaseResult> ParseGroup(
        ContinuousTestProviderRunRequest request,
        string runId,
        GoRunGroup group,
        TestProcessResult processResult,
        GoTestJsonParseResult parsed,
        string artifactPath)
    {
        string? buildFailure = BuildFailure(group, processResult, parsed);
        if (buildFailure is not null)
        {
            return group.Cases
                .Select(test => FailedResult(request, runId, test.Id, buildFailure))
                .ToArray();
        }

        var results = new List<ProviderCaseResult>(group.Cases.Count);
        foreach (GoRunCase test in group.Cases)
        {
            GoTestJsonEvent? terminal = parsed.TestEvents
                .Where(eventRow => string.Equals(eventRow.Package, group.ImportPath, StringComparison.Ordinal)
                    && string.Equals(eventRow.Test, test.TestName, StringComparison.Ordinal)
                    && eventRow.Action is "pass" or "fail" or "skip")
                .LastOrDefault();
            if (terminal is null)
            {
                if (processResult.ExitCode == 0)
                    throw new ContinuousTestProviderException(
                        $"go test -json for package '{group.ImportPath}' did not emit a terminal event for '{test.TestName}'.")
                    {
                        ResultArtifactPath = artifactPath,
                    };
                results.Add(FailedResult(request, runId, test.Id, FailureSummary(processResult, parsed)));
                continue;
            }

            results.Add(new ProviderCaseResult(
                Id: ResultId(runId, test.Id),
                TestCaseId: test.Id,
                Status: terminal.Action switch
                {
                    "pass" => "passed",
                    "skip" => "skipped",
                    _ => "failed",
                },
                ResultRevision: request.SelectedRevision,
                IndexIdentity: request.IndexIdentity,
                DurationSeconds: terminal.Elapsed,
                FailureSummary: terminal.Action == "fail"
                    ? FailureSummary(processResult, parsed, test.TestName)
                    : null));
        }

        if (processResult.ExitCode != 0 && results.All(result => result.Status != "failed"))
            throw new ContinuousTestProviderException(
                $"go test -json for package '{group.ImportPath}' exited with code {processResult.ExitCode} without a failed test result.")
            {
                ResultArtifactPath = artifactPath,
            };
        return results;
    }

    private static string? BuildFailure(
        GoRunGroup group,
        TestProcessResult processResult,
        GoTestJsonParseResult parsed)
    {
        GoBuildJsonEvent? build = parsed.BuildEvents
            .LastOrDefault(eventRow => string.Equals(eventRow.Action, "build-fail", StringComparison.Ordinal));
        if (build is not null)
            return FirstNonEmpty(build.Output, processResult.StandardError, $"package build failed");

        if (parsed.TestEvents.Any(eventRow =>
                string.Equals(eventRow.Package, group.ImportPath, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(eventRow.FailedBuild)))
            return FirstNonEmpty(
                parsed.TestEvents
                    .Where(eventRow => string.Equals(eventRow.Package, group.ImportPath, StringComparison.Ordinal))
                    .Select(eventRow => eventRow.Output)
                    .ToArray())
                ?? FirstNonEmpty(processResult.StandardError, "package build failed");

        return null;
    }

    private static string FailureSummary(
        TestProcessResult processResult,
        GoTestJsonParseResult parsed,
        string? testName = null) =>
        FirstNonEmpty(
            parsed.TestEvents
                .Where(eventRow => testName is null
                    || string.Equals(eventRow.Test, testName, StringComparison.Ordinal))
                .Select(eventRow => eventRow.Output)
                .ToArray())
        ?? FirstNonEmpty(processResult.StandardError, $"go test failed with exit code {processResult.ExitCode}.")
        ?? "go test failed without a diagnostic.";

    private static ProviderCaseResult FailedResult(
        ContinuousTestProviderRunRequest request,
        string runId,
        string testCaseId,
        string failureSummary) =>
        new(
            Id: ResultId(runId, testCaseId),
            TestCaseId: testCaseId,
            Status: "failed",
            ResultRevision: request.SelectedRevision,
            IndexIdentity: request.IndexIdentity,
            FailureSummary: failureSummary);

    private static IReadOnlyList<GoRunGroup> Groups(ContinuousTestProviderRunRequest request)
    {
        var groups = new Dictionary<(string Module, string ImportPath), GoRunGroup>();
        foreach (string id in request.TestCaseIds)
        {
            if (!GoTestTooling.TryDecodeCaseId(id, out GoTestCaseIdentity identity)
                || !string.Equals(identity.WorkspaceId, request.Workspace.WorkspaceId, StringComparison.Ordinal)
                || !PathComparer.Equals(identity.ProjectPath, request.Workspace.ProjectPath))
                throw new ContinuousTestProviderException($"Go test case ID '{id}' is not owned by this project.");

            var key = (identity.ModulePath, identity.ImportPath);
            if (!groups.TryGetValue(key, out GoRunGroup? group))
            {
                group = new GoRunGroup(identity.ModulePath, identity.ImportPath, []);
                groups.Add(key, group);
            }

            if (group.Cases.All(test => !string.Equals(test.Id, id, StringComparison.Ordinal)))
                group.Cases.Add(new GoRunCase(id, identity.TestName));
        }

        return groups.Values.OrderBy(group => group.ImportPath, StringComparer.Ordinal).ToArray();
    }

    private static string AggregateStatus(IReadOnlyList<ProviderCaseResult> results) =>
        results.Any(result => result.Status == "failed")
            ? "failed"
            : results.Count > 0 && results.All(result => result.Status == "skipped")
                ? "skipped"
                : "passed";

    private static string SourcePath(ContinuousTestWorkspace workspace, string directory)
    {
        string root = Path.GetFullPath(workspace.WorkspaceRoot);
        string full = Path.GetFullPath(directory);
        string relative = Path.GetRelativePath(root, full);
        return relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relative)
            ? full
            : relative.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private static string? EnvironmentValue(
        IReadOnlyDictionary<string, string> values,
        string key,
        string? fallback) =>
        values.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;

    private static string DiscoveryFailure(TestProcessResult result, string phase) =>
        FirstNonEmpty(result.StandardError, $"{phase} failed with exit code {result.ExitCode}.")
        ?? $"{phase} failed without a diagnostic.";

    private static string? FirstNonEmpty(params string?[] values) =>
        values.Select(value => value?.Trim()).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string NewRunId(ContinuousTestProviderRunRequest request) =>
        "ct-run:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join("|", request.Workspace.WorkspaceId, request.Workspace.ProjectPath,
                request.SelectedRevision, string.Join(",", request.TestCaseIds))))).ToLowerInvariant()[..24];

    private static string ResultId(string runId, string caseId) =>
        "go-result:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(runId + "|" + caseId)))
            .ToLowerInvariant()[..24];

    private static string ResultArtifactPath(CtGenerationPaths paths, string runId) =>
        Path.Combine(
            paths.ResultsDirectory,
            "run-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(runId))).ToLowerInvariant()
            + ".go.jsonl");

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

    private static void AppendArtifact(
        string artifactPath,
        string package,
        string output,
        string error)
    {
        using var stream = new FileStream(artifactPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream);
        writer.WriteLine($"## package: {package}");
        writer.Write(output);
        if (!output.EndsWith('\n'))
            writer.WriteLine();
        if (!string.IsNullOrWhiteSpace(error))
        {
            writer.WriteLine("## stderr");
            writer.WriteLine(error);
        }
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static void EnsureGo(ContinuousTestWorkspace workspace)
    {
        string? framework = workspace.Framework;
        if (framework is not null
            && !string.Equals(framework, GoTestTooling.Framework, StringComparison.OrdinalIgnoreCase))
            throw UnsupportedFramework(framework, workspace.ProjectPath);
        if (!IsGoProjectFile(workspace.ProjectPath))
            throw new ContinuousTestProviderException(
                $"Go continuous testing requires a go.mod project file: '{workspace.ProjectPath}'.");
    }

    private static ContinuousTestProviderException UnsupportedFramework(string framework, string path) =>
        new($"Go continuous test provider cannot run framework '{framework}' for '{path}'.");

    private static ContinuousTestProviderException StampGeneration(
        ContinuousTestProviderException exception,
        CtGenerationPaths paths) =>
        new(exception.Message, exception)
        {
            GenerationId = paths.GenerationId,
            ResultArtifactPath = exception.ResultArtifactPath,
        };

    private sealed record GoRunCase(string Id, string TestName);

    private sealed record GoRunGroup(string ModulePath, string ImportPath, List<GoRunCase> Cases);
}
