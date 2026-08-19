using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Miller.Testing;

public sealed class DotnetTestProvider : IContinuousTestProvider
{
    private const string ContractVersion = "ct-provider-v1";
    private const string XunitIdPrefix = "xunit:";
    private const string CoverageDirectoryName = "coverage";
    /// <remarks>
    /// The collect session is started with <c>-f xml</c> because per-test snapshots inherit the
    /// session's output format: without it they arrive in the binary 'coverage' format regardless
    /// of the snapshot's file extension, and the hit-set parser would honestly record every map as
    /// incomplete. The session artifact keeps a non-<c>.xml</c> name so snapshot compaction, which
    /// enumerates <c>*.xml</c> in the coverage directory, never mistakes it for a per-test snapshot.
    /// </remarks>
    private const string CoverageSessionFileName = "session.coverage";
    private const string CoverageSnapshotExtension = ".xml";
    private const string CoverageFileListExtension = ".covfiles";
    private const string CoverageFileListParser = "covfiles";
    private const string CoverageReadinessFileName = "readiness.coverage";
    private const int CoverageSnapshotKeyLength = 24;
    private static readonly TimeSpan DefaultCoverageShutdownTimeout = TimeSpan.FromSeconds(10);
    private readonly ITestProcessRunner _runner;
    private readonly ITestBackgroundProcessRunner? _backgroundRunner;
    private readonly string _dotnetPath;
    private readonly string _dotnetCoveragePath;
    private readonly TimeSpan _coverageShutdownTimeout;

    public DotnetTestProvider(
        ITestProcessRunner runner,
        string dotnetPath = "dotnet",
        string dotnetCoveragePath = "dotnet-coverage")
        : this(runner, dotnetPath, dotnetCoveragePath, DefaultCoverageShutdownTimeout)
    {
    }

    internal DotnetTestProvider(
        ITestProcessRunner runner,
        string dotnetPath,
        string dotnetCoveragePath,
        TimeSpan coverageShutdownTimeout)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _backgroundRunner = runner as ITestBackgroundProcessRunner;
        if (string.IsNullOrWhiteSpace(dotnetPath)) throw new ArgumentException("must not be empty", nameof(dotnetPath));
        if (string.IsNullOrWhiteSpace(dotnetCoveragePath))
            throw new ArgumentException("must not be empty", nameof(dotnetCoveragePath));
        if (coverageShutdownTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(coverageShutdownTimeout));
        _dotnetPath = dotnetPath;
        _dotnetCoveragePath = dotnetCoveragePath;
        _coverageShutdownTimeout = coverageShutdownTimeout;
    }

    public async Task<IReadOnlyList<ProviderTestCase>> DiscoverAsync(
        ContinuousTestWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        var paths = CtGenerationPaths.Allocate(workspace);
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

    private async Task<IReadOnlyList<ProviderTestCase>> DiscoverInGenerationAsync(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths,
        CancellationToken cancellationToken)
    {
        await BuildProjectAsync(workspace, paths, cancellationToken).ConfigureAwait(false);

        string? diagnosticPath = null;
        TestProcessCommand command;
        if (GenericFramework(workspace.Framework) is not null)
        {
            var targetPath = await ResolveGenericTargetPathAsync(workspace, paths, cancellationToken)
                .ConfigureAwait(false);
            diagnosticPath = DiscoveryDiagnosticPath(paths);
            if (File.Exists(diagnosticPath))
                File.Delete(diagnosticPath);
            command = BuildGenericDiscoverCommand(workspace, paths, targetPath, diagnosticPath);
        }
        else
        {
            command = BuildDiscoverCommand(workspace, paths);
        }
        var result = await _runner.RunAsync(command, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new ContinuousTestProviderException(
                $"Test discovery failed with exit code {result.ExitCode}: {FailureSummary(result)}");

        if (GenericFramework(workspace.Framework) is { } framework)
            return ParseGenericDiscoveryDiagnostic(diagnosticPath!, framework) is { Count: > 0 } cases
                ? cases
                : ParseGenericDiscovery(result.StandardOutput, framework);

        return ParseDiscovery(result.StandardOutput);
    }

    private async Task<ProviderRunResult> RunInGenerationAsync(
        ContinuousTestProviderRunRequest request,
        CtGenerationPaths paths,
        CancellationToken cancellationToken)
    {
        var genericFramework = GenericFramework(request.Framework ?? request.Workspace.Framework);
        if (request.CoverageMode == ContinuousTestCoverageMode.PerTest && genericFramework is not null)
            throw new ContinuousTestProviderException(
                $"Per-test coverage instrumentation is not supported for framework '{genericFramework}': the " +
                "per-test snapshot hook is an xunit v3 BeforeAfterTestAttribute and has no mstest/nunit equivalent.");

        await BuildProjectAsync(request.Workspace, paths, cancellationToken).ConfigureAwait(false);

        if (genericFramework is not null)
        {
            var targetPath = await ResolveGenericTargetPathAsync(request.Workspace, paths, cancellationToken)
                .ConfigureAwait(false);
            return await RunGenericDotnetTestAsync(request, paths, targetPath, cancellationToken)
                .ConfigureAwait(false);
        }

        var coverage = request.CoverageMode == ContinuousTestCoverageMode.PerTest
            ? await StartCoverageSessionAsync(request.Workspace, paths, cancellationToken).ConfigureAwait(false)
            : null;
        var resultArtifactPath = XunitResultArtifactPath(request, paths);
        var command = BuildRunCommand(request, paths, coverage);
        TestProcessResult? result = null;
        Exception? runFailure = null;
        try
        {
            result = await _runner.RunAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            runFailure = exception;
        }

        Exception? cleanupFailure = null;
        if (coverage is not null)
        {
            try
            {
                await ShutdownCoverageSessionAsync(request.Workspace, paths, coverage).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                cleanupFailure = exception;
            }
        }

        if (runFailure is not null && cleanupFailure is not null)
            throw CombinedRunAndCleanupFailure(runFailure, cleanupFailure);
        if (cleanupFailure is not null)
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        if (runFailure is not null)
            ExceptionDispatchInfo.Capture(runFailure).Throw();

        var runResult = ParseRun(result!.StandardOutput, request.SelectedRevision, request.IndexIdentity);
        if (result.ExitCode != 0 && runResult.CaseResults.Count == 0)
            throw new ContinuousTestProviderException(
                $"Dotnet test run failed with exit code {result.ExitCode}: {FailureSummary(result)}");
        if (result.ExitCode == 0 && runResult.CaseResults.Count == 0 && request.TestCaseIds.Count > 0)
            throw new ContinuousTestProviderException(
                "xUnit run selected " + request.TestCaseIds.Count + " test case(s) but executed none: " +
                string.Join(", ", request.TestCaseIds) + ".");

        if (request.RunId is not null)
            runResult = runResult with { RunId = request.RunId };
        if (resultArtifactPath is not null && File.Exists(resultArtifactPath))
            runResult = runResult with { ResultArtifactPath = resultArtifactPath };
        return runResult with
        {
            CoverageArtifacts = coverage is null
                ? DiscoverCoverageArtifacts(paths)
                : CompactCoverageSnapshots(request, paths, coverage, runResult),
            GenerationId = paths.GenerationId,
        };
    }

    /// <summary>
    /// Preview/test seam: builds the discover command against the latest existing generation (or the
    /// would-be first). Production discovery never uses it — <see cref="DiscoverAsync"/> allocates its
    /// own generation and builds every command from that one handle.
    /// </summary>
    public TestProcessCommand BuildDiscoverCommand(ContinuousTestWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        return BuildDiscoverCommand(workspace, CtGenerationPaths.ResolveLatestOrFirst(workspace));
    }

    private TestProcessCommand BuildDiscoverCommand(ContinuousTestWorkspace workspace, CtGenerationPaths paths)
    {
        if (GenericFramework(workspace.Framework) is not null)
            return BuildGenericDiscoverCommand(workspace, paths);

        var args = new List<string> { "-list", "full/json", "-noLogo", "-noColor" };
        AppendXunitTraitExclusions(args, workspace.ExcludeTraits);
        return new TestProcessCommand(
            TestExecutablePath(workspace, paths),
            args,
            workspace.WorkspaceRoot,
            WorkspaceEnvironment(workspace, paths));
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
        => BuildRunCommand(request, paths, coverage: null);

    private TestProcessCommand BuildRunCommand(
        ContinuousTestProviderRunRequest request,
        CtGenerationPaths paths,
        CoverageSession? coverage)
    {
        if (GenericFramework(request.Framework ?? request.Workspace.Framework) is not null)
            return BuildGenericRunCommand(request, paths);

        var args = new List<string> { "-noLogo", "-noColor", "-reporter", "json" };
        if (XunitResultArtifactPath(request, paths) is { } resultArtifactPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(resultArtifactPath)!);
            args.Add("-jUnit");
            args.Add(resultArtifactPath);
        }
        if (request.FilterArguments.Count > 0)
        {
            args.AddRange(request.FilterArguments);
        }
        else
        {
            var selectedMethods = new HashSet<string>(StringComparer.Ordinal);
            foreach (var testCaseId in request.TestCaseIds)
            {
                if (XunitMethodFromTestCaseId(testCaseId) is not { } method)
                {
                    args.Add("-id");
                    args.Add(testCaseId);
                    continue;
                }

                if (!selectedMethods.Add(method))
                    continue;

                args.Add("-method");
                args.Add(method);
            }
        }

        // Trait exclusions intersect with (never replace) per-test-ID selection: the xunit v3 runner
        // treats -trait- flags as an AND-filter, so they are appended alongside -id args regardless of
        // the FilterArguments override above.
        AppendXunitTraitExclusions(args, request.ExcludeTraits);

        if (coverage is not null)
        {
            // The snapshot hook resets one shared collector session after each test, so two tests
            // running concurrently would attribute each other's coverage.
            args.Add("-parallel");
            args.Add("none");
        }

        return new TestProcessCommand(
            TestExecutablePath(request.Workspace, paths),
            args,
            request.Workspace.WorkspaceRoot,
            WorkspaceEnvironment(request.Workspace, paths, coverage),
            coverage is null ? null : ProcessPriorityClass.BelowNormal);
    }

    /// <summary>
    /// Preview/test seam: the test executable inside the latest existing generation (or the would-be
    /// first). Production commands resolve it from the generation their operation allocated.
    /// </summary>
    public static string TestExecutablePath(ContinuousTestWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        return TestExecutablePath(workspace, CtGenerationPaths.ResolveLatestOrFirst(workspace));
    }

    /// <summary>
    /// Preview/test seam: the built test assembly inside the latest existing generation (or the
    /// would-be first). Production commands resolve it from the generation their operation allocated.
    /// </summary>
    internal static string TestAssemblyPath(ContinuousTestWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        return TestAssemblyPath(workspace, CtGenerationPaths.ResolveLatestOrFirst(workspace));
    }

    private static string TestExecutablePath(ContinuousTestWorkspace workspace, CtGenerationPaths paths) =>
        Path.Combine(
            paths.OutDir,
            Path.GetFileNameWithoutExtension(workspace.ProjectPath) + ExecutableExtension());

    private static string TestAssemblyPath(ContinuousTestWorkspace workspace, CtGenerationPaths paths) =>
        Path.Combine(paths.OutDir, Path.GetFileNameWithoutExtension(workspace.ProjectPath) + ".dll");

    private static string ExecutableExtension() => OperatingSystem.IsWindows() ? ".exe" : "";

    /// <summary>
    /// Re-throws a provider failure carrying the generation the operation allocated. Stamping at the
    /// operation boundary — rather than at each of the provider's ~20 throw sites, several of which sit
    /// in static parse helpers — is the only placement that cannot miss a throw after allocation.
    /// </summary>
    private static ContinuousTestProviderException StampGeneration(
        ContinuousTestProviderException exception,
        CtGenerationPaths paths) =>
        new(exception.Message, exception)
        {
            GenerationId = paths.GenerationId,
            ResultArtifactPath = exception.ResultArtifactPath,
        };

    private static IReadOnlyDictionary<string, string?> WorkspaceEnvironment(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths,
        CoverageSession? coverage = null)
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [CtEnvironment.WorkspaceRoot] = workspace.WorkspaceRoot,
            ["TMPDIR"] = paths.TempDirectory,
            ["TMP"] = paths.TempDirectory,
            ["TEMP"] = paths.TempDirectory,
        };
        if (coverage is not null)
        {
            environment["MILLER_CT_COVERAGE_SESSION"] = coverage.SessionId;
            environment["MILLER_CT_COVERAGE_DIR"] = coverage.CoverageDirectory;
            environment["MILLER_CT_COVERAGE_TOOL"] = coverage.ToolPath;
        }

        return environment;
    }

    private async Task<ProviderRunResult> RunGenericDotnetTestAsync(
        ContinuousTestProviderRunRequest request,
        CtGenerationPaths paths,
        string targetPath,
        CancellationToken cancellationToken)
    {
        var resultArtifactPath = TrxResultArtifactPath(request, paths);
        var command = BuildGenericRunCommand(request, paths, targetPath, resultArtifactPath);
        var result = await _runner.RunAsync(command, cancellationToken).ConfigureAwait(false);
        if (!File.Exists(resultArtifactPath))
        {
            if (result.ExitCode != 0)
                throw new ContinuousTestProviderException(
                    $"Dotnet test run failed with exit code {result.ExitCode}: {FailureSummary(result)}");

            throw new ContinuousTestProviderException(
                $"Dotnet test run did not produce TRX result artifact '{resultArtifactPath}'.");
        }

        var runResult = ParseTrxRun(resultArtifactPath, request);
        if (request.RunId is not null && !string.Equals(runResult.RunId, request.RunId, StringComparison.Ordinal))
            runResult = runResult with { RunId = request.RunId };

        return runResult with
        {
            ResultArtifactPath = resultArtifactPath,
            CoverageArtifacts = DiscoverCoverageArtifacts(paths),
            GenerationId = paths.GenerationId,
        };
    }

    private TestProcessCommand BuildGenericDiscoverCommand(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths)
    {
        var diagnosticPath = DiscoveryDiagnosticPath(paths);
        if (File.Exists(diagnosticPath))
            File.Delete(diagnosticPath);
        return BuildGenericDiscoverCommand(workspace, paths, TestAssemblyPath(workspace, paths), diagnosticPath);
    }

    private TestProcessCommand BuildGenericDiscoverCommand(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths,
        string targetPath,
        string diagnosticPath)
    {
        paths.EnsureDirectories();
        var args = new List<string>
        {
            "test",
            targetPath,
            "--nologo",
            "--list-tests",
            "--results-directory",
            paths.ResultsDirectory,
            "--diag",
            diagnosticPath,
        };
        if (GenericExclusionFilter(workspace.Framework, workspace.ExcludeTraits) is { } exclusionFilter)
        {
            args.Add("--filter");
            args.Add(exclusionFilter);
        }
        return new TestProcessCommand(
            _dotnetPath,
            args,
            workspace.WorkspaceRoot,
            WorkspaceEnvironment(workspace, paths));
    }

    private TestProcessCommand BuildGenericRunCommand(
        ContinuousTestProviderRunRequest request,
        CtGenerationPaths paths)
        => BuildGenericRunCommand(request, paths, TestAssemblyPath(request.Workspace, paths));

    private TestProcessCommand BuildGenericRunCommand(
        ContinuousTestProviderRunRequest request,
        CtGenerationPaths paths,
        string targetPath,
        string? resultArtifactPath = null)
    {
        paths.EnsureDirectories();
        resultArtifactPath ??= TrxResultArtifactPath(request, paths);
        var args = new List<string>
        {
            "test",
            targetPath,
            "--nologo",
            "--results-directory",
            paths.ResultsDirectory,
            "--logger",
            $"trx;LogFileName={Path.GetFileName(resultArtifactPath)}",
        };
        if (request.FilterArguments.Count > 0)
        {
            // FilterArguments is a whole-expression override: it wins byte-identically and exclusions
            // are NOT merged into user-supplied filter arguments on the generic path.
            args.AddRange(request.FilterArguments);
        }
        else if (ComposeGenericFilter(request) is { } filter)
        {
            args.Add("--filter");
            args.Add(filter);
        }

        return new TestProcessCommand(
            _dotnetPath,
            args,
            request.Workspace.WorkspaceRoot,
            WorkspaceEnvironment(request.Workspace, paths));
    }

    private static string? ComposeGenericFilter(ContinuousTestProviderRunRequest request)
    {
        var framework = GenericFramework(request.Framework ?? request.Workspace.Framework);
        var selectors = request.TestCaseIds
            .Select(GenericSelectorFromTestCaseId)
            .Where(selector => !string.IsNullOrWhiteSpace(selector))
            .Select(selector => GenericFilterTerm(framework, selector))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var selectionExpression = selectors.Length > 0 ? string.Join("|", selectors) : null;
        var exclusionExpression = GenericExclusionFilter(
            request.Framework ?? request.Workspace.Framework,
            request.ExcludeTraits);

        if (selectionExpression is not null && exclusionExpression is not null)
            return $"({selectionExpression})&{exclusionExpression}";

        return selectionExpression ?? exclusionExpression;
    }

    private static string GenericFilterTerm(string? framework, string selector)
    {
        var filterOperator = "=";
        if (framework == "nunit")
        {
            var parameterStart = selector.IndexOf('(');
            if (parameterStart >= 0)
                selector = selector[..parameterStart];

            var methodSeparator = selector.LastIndexOf('.');
            var displaySuffixStart = selector.IndexOf(' ', methodSeparator + 1);
            if (displaySuffixStart >= 0)
            {
                selector = selector[..displaySuffixStart];
                filterOperator = "~";
            }
        }

        var escaped = selector.Replace(",", "%2C", StringComparison.Ordinal);
        return $"{GenericFilterProperty(selector)}{filterOperator}{escaped}";
    }

    private static string GenericFilterProperty(string selector)
    {
        var parameterStart = selector.IndexOf('(');
        var identity = parameterStart < 0 ? selector : selector[..parameterStart];
        return identity.Contains('.', StringComparison.Ordinal) ? "FullyQualifiedName" : "Name";
    }

    private static string? GenericExclusionFilter(string? framework, IReadOnlyList<string> excludeTraits)
    {
        if (excludeTraits.Count == 0)
            return null;

        // nunit uses [Category], mstest uses [TestCategory]; both map from the trait's Value part.
        var property = GenericFramework(framework) == "nunit" ? "Category" : "TestCategory";
        var clauses = excludeTraits
            .Select(TraitValue)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => $"{property}!={value}")
            .ToArray();
        return clauses.Length > 0 ? string.Join("&", clauses) : null;
    }

    private static void AppendXunitTraitExclusions(List<string> args, IReadOnlyList<string> excludeTraits)
    {
        foreach (var trait in excludeTraits)
        {
            if (string.IsNullOrWhiteSpace(trait))
                continue;

            // xunit v3 exclusion flag: `-trait- "Name=Value"` (two argv elements). NOT xunit v2's
            // `-notrait`, which the v3 runner removed. Filters both -list discovery and run output.
            args.Add("-trait-");
            args.Add(trait);
        }
    }

    private static string TraitValue(string trait)
    {
        var separator = trait.IndexOf('=', StringComparison.Ordinal);
        return separator >= 0 ? trait[(separator + 1)..] : trait;
    }

    private static string? XunitResultArtifactPath(
        ContinuousTestProviderRunRequest request,
        CtGenerationPaths paths)
    {
        if (string.IsNullOrWhiteSpace(request.RunId))
            return null;

        var runHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.RunId))).ToLowerInvariant();
        return Path.Combine(paths.ResultsDirectory, $"run-{runHash}.junit.xml");
    }

    private static string TrxResultArtifactPath(
        ContinuousTestProviderRunRequest request,
        CtGenerationPaths paths)
    {
        var runKey = request.RunId ?? CanonicalRunKey(request);
        var runHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(runKey))).ToLowerInvariant();
        return Path.Combine(paths.ResultsDirectory, $"run-{runHash}.trx");
    }

    private static string CanonicalRunKey(ContinuousTestProviderRunRequest request) =>
        string.Join(
            ":",
            request.Workspace.WorkspaceId,
            request.Workspace.ProjectPath,
            request.SelectedRevision,
            DateTimeOffset.UtcNow.UtcTicks);

    /// <summary>
    /// Preview/test seam: allocates a fresh generation and pins the build command to it. Production
    /// operations never call it — <see cref="DiscoverAsync"/> and <see cref="RunAsync"/> allocate once
    /// and build every command from that one handle, so a build command here is a generation of its own.
    /// </summary>
    public TestProcessCommand BuildProjectCommand(ContinuousTestWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        return BuildProjectCommand(workspace, CtGenerationPaths.Allocate(workspace));
    }

    private TestProcessCommand BuildProjectCommand(ContinuousTestWorkspace workspace, CtGenerationPaths paths)
    {
        paths.EnsureDirectories();
        var args = new List<string>
        {
            "build",
            workspace.ProjectPath,
            "--nologo",
            "--disable-build-servers",
            "--artifacts-path",
            workspace.BuildOutputRoot,
            "-nr:false",
            $"-p:OutDir={paths.OutDir}",
            $"-p:ResultsDirectory={paths.ResultsDirectory}",
            $"-bl:{paths.BinlogPath};ProjectImports=None",
        };
        return new TestProcessCommand(
            _dotnetPath,
            args,
            workspace.WorkspaceRoot,
            WorkspaceEnvironment(workspace, paths));
    }

    private async Task BuildProjectAsync(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths,
        CancellationToken cancellationToken)
    {
        var command = BuildProjectCommand(workspace, paths);
        var result = await _runner.RunAsync(command, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new ContinuousTestProviderException(
                $"Dotnet test project build failed with exit code {result.ExitCode}: {FailureSummary(result)}");
    }

    private async Task<string> ResolveGenericTargetPathAsync(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths,
        CancellationToken cancellationToken)
    {
        paths.EnsureDirectories();
        var command = new TestProcessCommand(
            _dotnetPath,
            [
                "msbuild",
                workspace.ProjectPath,
                "-nologo",
                "-getProperty:TargetPath",
                $"-p:OutDir={paths.OutDir}",
                $"-p:ResultsDirectory={paths.ResultsDirectory}",
            ],
            workspace.WorkspaceRoot,
            WorkspaceEnvironment(workspace, paths));
        var result = await _runner.RunAsync(command, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new ContinuousTestProviderException(
                $"Test target-path evaluation failed with exit code {result.ExitCode}: {FailureSummary(result)}");

        var evaluatedPath = result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
        if (string.IsNullOrWhiteSpace(evaluatedPath))
            throw new ContinuousTestProviderException("Test target-path evaluation returned an empty TargetPath.");

        var targetPath = Path.GetFullPath(evaluatedPath, workspace.WorkspaceRoot);
        var relativePath = Path.GetRelativePath(Path.GetFullPath(workspace.BuildOutputRoot), targetPath);
        if (Path.IsPathRooted(relativePath)
            || relativePath == ".."
            || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ContinuousTestProviderException(
                $"Evaluated test TargetPath '{targetPath}' is outside CT build root '{workspace.BuildOutputRoot}'.");
        }
        if (!File.Exists(targetPath))
            throw new ContinuousTestProviderException(
                $"Evaluated test TargetPath '{targetPath}' does not exist after the isolated build.");

        return targetPath;
    }

    private static string DiscoveryDiagnosticPath(CtGenerationPaths paths)
    {
        var path = Path.Combine(paths.GenerationRoot, "logs", "discovery.diag.log");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    private static string FailureSummary(TestProcessResult result)
    {
        var text = !string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardError
            : result.StandardOutput;
        text = text.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var lines = text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
        return lines.FirstOrDefault(IsErrorLine)
            ?? lines.FirstOrDefault()
            ?? string.Empty;
    }

    private static bool IsErrorLine(string line) =>
        line.Contains(": error ", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("error ", StringComparison.OrdinalIgnoreCase)
        || line.Contains(" error ", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<ProviderTestCase> ParseGenericDiscovery(string output, string framework)
    {
        var cases = new List<ProviderTestCase>();
        var collecting = false;
        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;
            if (line.StartsWith("The following Tests are available", StringComparison.OrdinalIgnoreCase))
            {
                collecting = true;
                continue;
            }
            if (!collecting)
                continue;
            if (line.StartsWith("No test", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Test run for ", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("VSTest ", StringComparison.OrdinalIgnoreCase))
                continue;

            var qualifiedName = line;
            var (className, methodName) = SplitQualifiedName(qualifiedName);
            cases.Add(new ProviderTestCase(
                Id: GenericTestCaseId(framework, qualifiedName),
                DisplayName: qualifiedName,
                FullyQualifiedName: qualifiedName,
                Selector: qualifiedName,
                Framework: framework,
                Metadata: new Dictionary<string, object?>
                {
                    ["class"] = className,
                    ["method"] = methodName,
                    ["selector_kind"] = "FullyQualifiedName",
                }));
        }

        return cases;
    }

    private static IReadOnlyList<ProviderTestCase> ParseGenericDiscoveryDiagnostic(
        string diagnosticPath,
        string framework)
    {
        if (!File.Exists(diagnosticPath))
            return [];

        try
        {
            var cases = new List<ProviderTestCase>();
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in File.ReadLines(diagnosticPath))
            {
                var jsonStart = line.IndexOf('{', StringComparison.Ordinal);
                if (jsonStart < 0)
                    continue;

                using var document = JsonDocument.Parse(line[jsonStart..]);
                var root = document.RootElement;
                var messageType = OptionalString(root, "MessageType");
                if (messageType is not "TestDiscovery.TestCasesFound" and not "TestDiscovery.Completed")
                    continue;
                if (!root.TryGetProperty("Payload", out var payload))
                    continue;

                var propertyName = messageType == "TestDiscovery.TestCasesFound"
                    ? "TestCases"
                    : "LastDiscoveredTests";
                var rows = payload;
                if (payload.ValueKind == JsonValueKind.Object
                    && !payload.TryGetProperty(propertyName, out rows))
                    continue;
                if (rows.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var row in rows.EnumerateArray())
                {
                    var fullyQualifiedName = OptionalString(row, "FullyQualifiedName");
                    if (string.IsNullOrWhiteSpace(fullyQualifiedName)
                        || !identities.Add(fullyQualifiedName))
                        continue;
                    var displayName = OptionalString(row, "DisplayName") ?? fullyQualifiedName;
                    var (className, methodName) = SplitDiagnosticQualifiedName(
                        fullyQualifiedName,
                        displayName);
                    cases.Add(new ProviderTestCase(
                        Id: GenericTestCaseId(framework, fullyQualifiedName),
                        DisplayName: displayName,
                        FullyQualifiedName: fullyQualifiedName,
                        Selector: fullyQualifiedName,
                        Framework: framework,
                        Metadata: new Dictionary<string, object?>
                        {
                            ["class"] = className,
                            ["method"] = methodName,
                            ["selector_kind"] = "FullyQualifiedName",
                        }));
                }
            }

            return cases;
        }
        catch (IOException)
        {
            return [];
        }
        catch (JsonException)
        {
            return [];
        }
        catch (ContinuousTestProviderException)
        {
            return [];
        }
    }

    private static (string? ClassName, string MethodName) SplitDiagnosticQualifiedName(
        string fullyQualifiedName,
        string displayName)
    {
        var suffix = "." + displayName;
        if (fullyQualifiedName.EndsWith(suffix, StringComparison.Ordinal))
        {
            var methodSeparator = displayName.IndexOf('(', StringComparison.Ordinal);
            var methodName = methodSeparator < 0 ? displayName : displayName[..methodSeparator];
            return (fullyQualifiedName[..^suffix.Length], methodName);
        }

        return SplitQualifiedName(fullyQualifiedName);
    }

    private static ProviderRunResult ParseTrxRun(
        string artifactPath,
        ContinuousTestProviderRunRequest request)
    {
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(
                artifactPath,
                new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                });
            document = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new ContinuousTestProviderException("Malformed TRX result artifact: " + ex.Message, ex);
        }

        var root = document.Root
            ?? throw new ContinuousTestProviderException("TRX result artifact is empty.");
        var ns = root.Name.Namespace;
        var testNamesByDefinitionId = root
            .Descendants(ns + "UnitTest")
            .Select(row => new
            {
                Id = row.Attribute("id")?.Value,
                Name = TrxDefinitionName(row, ns),
            })
            .Where(row => !string.IsNullOrWhiteSpace(row.Id) && !string.IsNullOrWhiteSpace(row.Name))
            .ToDictionary(row => row.Id!, row => row.Name!, StringComparer.Ordinal);
        var selectedIdsBySelector = request.TestCaseIds
            .ToDictionary(GenericSelectorFromTestCaseId, id => id, StringComparer.Ordinal);
        var framework = GenericFramework(request.Framework ?? request.Workspace.Framework) ?? "dotnet";
        var caseResults = new List<ProviderCaseResult>();

        foreach (var row in root.Descendants(ns + "UnitTestResult"))
        {
            var testName = row.Attribute("testName")?.Value;
            var testDefinitionId = row.Attribute("testId")?.Value;
            if (testDefinitionId is not null
                && testNamesByDefinitionId.TryGetValue(testDefinitionId, out var definitionName))
                testName = definitionName;
            if (string.IsNullOrWhiteSpace(testName))
                continue;

            var selector = testName;
            var testCaseId = selectedIdsBySelector.GetValueOrDefault(selector)
                ?? (request.TestCaseIds.Count == 1 ? request.TestCaseIds[0] : GenericTestCaseId(framework, selector));
            var status = TrxStatus(row.Attribute("outcome")?.Value);
            var duration = TrxDurationSeconds(row.Attribute("duration")?.Value);
            caseResults.Add(new ProviderCaseResult(
                Id: row.Attribute("executionId")?.Value
                    ?? CanonicalTrxResultId(request.Workspace.WorkspaceId, testCaseId, request.RunId),
                TestCaseId: testCaseId,
                Status: status,
                ResultRevision: request.SelectedRevision,
                IndexIdentity: request.IndexIdentity,
                DurationSeconds: duration,
                FailureSummary: TrxFailureSummary(row, ns),
                Metadata: new Dictionary<string, object?>
                {
                    ["artifact_path"] = artifactPath,
                    ["framework"] = framework,
                    ["outcome"] = row.Attribute("outcome")?.Value,
                }));
        }

        if (caseResults.Count == 0 && request.TestCaseIds.Count > 0)
        {
            var runError = root
                .Descendants(ns + "RunInfo")
                .Select(row => row.Element(ns + "Text")?.Value.Trim())
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            throw new ContinuousTestProviderException(
                runError ?? "Dotnet test run produced no results for the selected test cases.");
        }

        var times = root.Element(ns + "Times");
        var startedAt = TrxDateTimeOffset(times?.Attribute("start")?.Value);
        var endedAt = TrxDateTimeOffset(times?.Attribute("finish")?.Value);
        var runId = request.RunId ?? $"trx:{root.Attribute("id")?.Value ?? Path.GetFileNameWithoutExtension(artifactPath)}";
        return new ProviderRunResult(
            RunId: runId,
            Status: AggregateStatus(caseResults.Select(row => row.Status)),
            StartedAt: startedAt,
            EndedAt: endedAt,
            CaseResults: caseResults);
    }

    private static IReadOnlyList<ProviderTestCase> ParseDiscovery(string output)
    {
        var trimmed = output.TrimStart();
        if (trimmed.StartsWith("[", StringComparison.Ordinal))
            return ParseXunitDiscovery(trimmed);

        var cases = new List<ProviderTestCase>();
        foreach (var line in JsonLines(output))
        {
            using var document = ParseJsonLine(line);
            var root = document.RootElement;
            RequireContractVersion(root);
            var eventName = RequiredString(root, "event");
            if (!string.Equals(eventName, "test_case", StringComparison.Ordinal))
                throw new ContinuousTestProviderException($"Unexpected discovery event '{eventName}'.");

            cases.Add(new ProviderTestCase(
                Id: RequiredString(root, "id"),
                DisplayName: RequiredString(root, "display_name"),
                FullyQualifiedName: RequiredString(root, "fully_qualified_name"),
                Selector: RequiredString(root, "selector"),
                Framework: OptionalString(root, "framework"),
                SourcePath: OptionalString(root, "source_path"),
                Metadata: Metadata(root)));
        }

        return cases;
    }

    private static ProviderRunResult ParseRun(string output, string selectedRevision, string indexIdentity)
    {
        if (!JsonLines(output).All(line => line.Contains("\"contract_version\"", StringComparison.Ordinal)))
            return ParseXunitRun(output, selectedRevision, indexIdentity);

        string? runId = null;
        string? status = null;
        DateTimeOffset? startedAt = null;
        DateTimeOffset? endedAt = null;
        var caseResults = new List<ProviderCaseResult>();

        foreach (var line in JsonLines(output))
        {
            using var document = ParseJsonLine(line);
            var root = document.RootElement;
            RequireContractVersion(root);
            var eventName = RequiredString(root, "event");
            switch (eventName)
            {
                case "run_started":
                    runId = RequiredString(root, "run_id");
                    startedAt = OptionalDateTimeOffset(root, "started_at");
                    break;
                case "case_started":
                    RequiredString(root, "test_case_id");
                    break;
                case "case_result":
                    caseResults.Add(new ProviderCaseResult(
                        Id: RequiredString(root, "id"),
                        TestCaseId: RequiredString(root, "test_case_id"),
                        Status: RequiredString(root, "status"),
                        ResultRevision: OptionalString(root, "result_revision") ?? selectedRevision,
                        IndexIdentity: indexIdentity,
                        DurationSeconds: OptionalDouble(root, "duration_seconds"),
                        FailureSummary: OptionalString(root, "failure_summary"),
                        Metadata: Metadata(root)));
                    break;
                case "run_finished":
                    var finishedRunId = RequiredString(root, "run_id");
                    if (runId is not null && !string.Equals(runId, finishedRunId, StringComparison.Ordinal))
                        throw new ContinuousTestProviderException(
                            $"Run finished id '{finishedRunId}' did not match started id '{runId}'.");

                    runId = finishedRunId;
                    status = RequiredString(root, "status");
                    endedAt = OptionalDateTimeOffset(root, "ended_at");
                    break;
                default:
                    throw new ContinuousTestProviderException($"Unexpected run event '{eventName}'.");
            }
        }

        if (runId is null)
            throw new ContinuousTestProviderException("Provider output did not include a run id.");
        if (status is null)
            throw new ContinuousTestProviderException("Provider output did not include a run_finished event.");

        return new ProviderRunResult(runId, status, startedAt, endedAt, caseResults);
    }

    private static IReadOnlyList<ProviderTestCase> ParseXunitDiscovery(string output)
    {
        using var document = ParseJsonLine(output);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new ContinuousTestProviderException("xUnit discovery output must be a JSON array.");

        var cases = new List<ProviderTestCase>();
        foreach (var row in document.RootElement.EnumerateArray())
        {
            // The runner's own `ID` hashes the assembly path, so it changes with every build
            // generation. Identity is derived from the display name, which does not.
            var displayName = RequiredString(row, "DisplayName");
            var metadata = new Dictionary<string, object?>(StringComparer.Ordinal);
            AddOptionalMetadata(metadata, "assembly", row, "Assembly");
            AddOptionalMetadata(metadata, "class", row, "Class");
            AddOptionalMetadata(metadata, "method", row, "Method");

            cases.Add(new ProviderTestCase(
                Id: XunitTestCaseId(displayName),
                DisplayName: displayName,
                FullyQualifiedName: displayName,
                Selector: $"-method {XunitMethodName(displayName)}",
                Framework: "xunit",
                Metadata: metadata));
        }

        return cases;
    }

    private static string XunitTestCaseId(string displayName) => $"{XunitIdPrefix}{displayName}";

    private static string XunitMethodName(string displayName)
    {
        var arguments = displayName.IndexOf('(', StringComparison.Ordinal);
        return arguments > 0 ? displayName[..arguments] : displayName;
    }

    private static string? XunitMethodFromTestCaseId(string testCaseId) =>
        testCaseId.StartsWith(XunitIdPrefix, StringComparison.Ordinal)
            ? XunitMethodName(testCaseId[XunitIdPrefix.Length..])
            : null;

    private static ProviderRunResult ParseXunitRun(string output, string selectedRevision, string indexIdentity)
    {
        string? assemblyId = null;
        DateTimeOffset? startedAt = null;
        DateTimeOffset? endedAt = null;
        var failed = false;
        var skipped = false;
        var caseResults = new List<ProviderCaseResult>();
        var displayNamesByUniqueId = new Dictionary<string, string>(StringComparer.Ordinal);
        var testDisplayNames = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var line in JsonLines(output))
        {
            using var document = TryParseJsonLine(line);
            if (document is null)
                continue;

            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("$type", out var typeValue) ||
                typeValue.ValueKind != JsonValueKind.String)
                continue;

            var eventName = typeValue.GetString()!;
            switch (eventName)
            {
                case "test-assembly-starting":
                    assemblyId = RequiredString(root, "AssemblyUniqueID");
                    startedAt = OptionalDateTimeOffset(root, "StartTime");
                    break;
                case "test-case-starting":
                    displayNamesByUniqueId[RequiredString(root, "TestCaseUniqueID")] =
                        RequiredString(root, "TestCaseDisplayName");
                    break;
                case "test-starting":
                    if (OptionalString(root, "TestDisplayName") is { } testDisplayName)
                        testDisplayNames.Add(testDisplayName);
                    break;
                case "test-passed":
                    caseResults.Add(XunitCaseResult(root, "passed", selectedRevision, indexIdentity, displayNamesByUniqueId));
                    break;
                case "test-failed":
                    failed = true;
                    caseResults.Add(XunitCaseResult(root, "failed", selectedRevision, indexIdentity, displayNamesByUniqueId));
                    break;
                case "test-skipped":
                    skipped = true;
                    caseResults.Add(XunitCaseResult(root, "skipped", selectedRevision, indexIdentity, displayNamesByUniqueId));
                    break;
                case "test-assembly-finished":
                    failed = failed || OptionalInt(root, "TestsFailed") > 0;
                    skipped = !failed
                        && (skipped || OptionalInt(root, "TestsSkipped") > 0)
                        && OptionalInt(root, "TestsTotal") == OptionalInt(root, "TestsSkipped");
                    endedAt = OptionalDateTimeOffset(root, "FinishTime");
                    break;
            }
        }

        if (assemblyId is null)
            throw new ContinuousTestProviderException("xUnit run output did not include an assembly id.");

        var runId = $"xunit:{assemblyId}:{RunStamp(startedAt)}";
        var status = failed ? "failed" : skipped ? "skipped" : "passed";
        return new ProviderRunResult(
            runId,
            status,
            startedAt,
            endedAt,
            caseResults,
            TestDisplayNames: testDisplayNames.ToArray());
    }

    private static JsonDocument? TryParseJsonLine(string line)
    {
        try
        {
            return JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ProviderCaseResult XunitCaseResult(
        JsonElement root,
        string status,
        string selectedRevision,
        string indexIdentity,
        IReadOnlyDictionary<string, string> displayNamesByUniqueId)
    {
        var uniqueId = RequiredString(root, "TestCaseUniqueID");

        // TestCaseUniqueID hashes the assembly path and so is generation-scoped; the preceding
        // test-case-starting event is the only channel carrying the stable display name.
        if (!displayNamesByUniqueId.TryGetValue(uniqueId, out var displayName))
            throw new ContinuousTestProviderException(
                $"xUnit run output attributed a '{status}' result to test case '{uniqueId}' with no " +
                "preceding test-case-starting event.");

        var testCaseId = XunitTestCaseId(displayName);
        return new ProviderCaseResult(
            Id: OptionalString(root, "TestUniqueID") ?? $"{testCaseId}:{status}",
            TestCaseId: testCaseId,
            Status: status,
            ResultRevision: selectedRevision,
            IndexIdentity: indexIdentity,
            DurationSeconds: OptionalDouble(root, "ExecutionTime"),
            FailureSummary: XunitFailureSummary(root) ?? OptionalString(root, "Reason"),
            Metadata: new Dictionary<string, object?>
            {
                ["finish_time"] = OptionalString(root, "FinishTime"),
            });
    }

    // xunit v3 `-reporter json` test-failed events carry `Messages`/`ExceptionTypes`/`StackTraces`
    // string arrays — there is no `FailureMessages` key. Prefix the first message with the first
    // exception type when present so agents see e.g. "System.InvalidOperationException: ...".
    private static string? XunitFailureSummary(JsonElement root)
    {
        var message = FirstString(root, "Messages");
        if (message is null)
            return null;

        var exceptionType = FirstString(root, "ExceptionTypes");
        return string.IsNullOrWhiteSpace(exceptionType)
            ? message
            : $"{exceptionType}: {message}";
    }

    private static string? GenericFramework(string? framework)
    {
        var normalized = framework?.Trim().ToLowerInvariant();
        return normalized is "mstest" or "nunit" ? normalized : null;
    }

    private static string GenericTestCaseId(string framework, string fullyQualifiedName) =>
        $"{framework}:{fullyQualifiedName}";

    private static string GenericSelectorFromTestCaseId(string testCaseId)
    {
        if (testCaseId.StartsWith("mstest:", StringComparison.Ordinal))
            return testCaseId["mstest:".Length..];
        if (testCaseId.StartsWith("nunit:", StringComparison.Ordinal))
            return testCaseId["nunit:".Length..];
        return testCaseId;
    }

    private static (string? ClassName, string MethodName) SplitQualifiedName(string qualifiedName)
    {
        var separator = qualifiedName.LastIndexOf('.');
        if (separator <= 0 || separator >= qualifiedName.Length - 1)
            return (null, qualifiedName);
        return (qualifiedName[..separator], qualifiedName[(separator + 1)..]);
    }

    private static string? TrxDefinitionName(XElement unitTest, XNamespace ns)
    {
        var testMethod = unitTest.Element(ns + "TestMethod");
        var className = testMethod?.Attribute("className")?.Value;
        var methodName = testMethod?.Attribute("name")?.Value;
        if (!string.IsNullOrWhiteSpace(className) && !string.IsNullOrWhiteSpace(methodName))
        {
            return methodName.StartsWith(className + ".", StringComparison.Ordinal)
                ? methodName
                : $"{className}.{methodName}";
        }

        return unitTest.Attribute("name")?.Value;
    }

    private static string TrxStatus(string? outcome) =>
        outcome?.Trim().ToLowerInvariant() switch
        {
            "passed" => "passed",
            "notexecuted" or "skipped" => "skipped",
            _ => "failed",
        };

    private static double? TrxDurationSeconds(string? value) =>
        TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var duration)
            ? duration.TotalSeconds
            : null;

    private static DateTimeOffset? TrxDateTimeOffset(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

    private static string? TrxFailureSummary(XElement result, XNamespace ns) =>
        result
            .Descendants(ns + "Message")
            .Select(message => message.Value.Trim())
            .FirstOrDefault(message => !string.IsNullOrWhiteSpace(message));

    private static string AggregateStatus(IEnumerable<string> statuses)
    {
        var statusSet = statuses.ToHashSet(StringComparer.Ordinal);
        if (statusSet.Count == 0)
            return "passed";
        if (statusSet.Contains("failed") || statusSet.Contains("errored"))
            return "failed";
        if (statusSet.SetEquals(["skipped"]))
            return "skipped";
        return "passed";
    }

    private static string CanonicalTrxResultId(string workspaceId, string testCaseId, string? runId)
    {
        var seed = $"{workspaceId}:{testCaseId}:{runId ?? string.Empty}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed))).ToLowerInvariant();
        return $"trx-result:{hash}";
    }

    private sealed record CoverageSession(
        string SessionId,
        string CoverageDirectory,
        string ToolPath,
        ITestBackgroundProcess Process)
    {
        public string SessionArtifactPath => Path.Combine(CoverageDirectory, CoverageSessionFileName);
    }

    private async Task<CoverageSession> StartCoverageSessionAsync(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths,
        CancellationToken cancellationToken)
    {
        paths.EnsureDirectories();
        var sessionId = $"miller-ct-{paths.GenerationId}";
        var coverageDirectory = Path.Combine(paths.ResultsDirectory, CoverageDirectoryName);
        Directory.CreateDirectory(coverageDirectory);

        foreach (var assembly in InstrumentableAssemblies(paths))
        {
            await RunCoverageCommandAsync(
                workspace,
                paths,
                ["instrument", "-id", sessionId, assembly],
                cancellationToken).ConfigureAwait(false);
        }

        if (_backgroundRunner is null)
            throw new ContinuousTestProviderException(
                "Per-test coverage requires a process runner that can retain and terminate the collector process.");

        var command = new TestProcessCommand(
            _dotnetCoveragePath,
            [
                "collect",
                "--server-mode",
                "-id",
                sessionId,
                "-f",
                "xml",
                "-o",
                Path.Combine(coverageDirectory, CoverageSessionFileName),
            ],
            workspace.WorkspaceRoot,
            WorkspaceEnvironment(workspace, paths),
            ProcessPriorityClass.BelowNormal);
        var process = _backgroundRunner.Start(command);
        var coverage = new CoverageSession(sessionId, coverageDirectory, _dotnetCoveragePath, process);
        try
        {
            await ProbeCoverageSessionAsync(workspace, paths, coverage, cancellationToken).ConfigureAwait(false);
            return coverage;
        }
        catch (Exception startupFailure)
        {
            return await FailCoverageSessionStartupAsync(coverage, startupFailure).ConfigureAwait(false);
        }
    }

    private async Task<CoverageSession> FailCoverageSessionStartupAsync(
        CoverageSession coverage,
        Exception startupFailure)
    {
        Exception? cleanupFailure = null;
        try
        {
            coverage.Process.TerminateProcessTree();
            using var terminationCancellation = new CancellationTokenSource(_coverageShutdownTimeout);
            await coverage.Process.WaitForExitAsync(terminationCancellation.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            cleanupFailure = exception;
        }

        try
        {
            await coverage.Process.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            cleanupFailure = cleanupFailure is null
                ? exception
                : new AggregateException(cleanupFailure, exception);
        }

        if (cleanupFailure is not null)
            throw new ContinuousTestProviderException(
                startupFailure.Message + " Collector startup cleanup also failed: " + cleanupFailure.Message,
                new AggregateException(startupFailure, cleanupFailure));

        ExceptionDispatchInfo.Capture(startupFailure).Throw();
        throw new UnreachableException();
    }

    private async Task ProbeCoverageSessionAsync(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths,
        CoverageSession coverage,
        CancellationToken cancellationToken)
    {
        var readinessPath = Path.Combine(paths.ResultsDirectory, CoverageReadinessFileName);
        if (File.Exists(readinessPath))
            File.Delete(readinessPath);
        var timeoutMilliseconds = Math.Max(1, (long)Math.Ceiling(_coverageShutdownTimeout.TotalMilliseconds));
        var command = new TestProcessCommand(
            _dotnetCoveragePath,
            [
                "snapshot",
                coverage.SessionId,
                "-o",
                readinessPath,
                "-t",
                timeoutMilliseconds.ToString(CultureInfo.InvariantCulture),
            ],
            workspace.WorkspaceRoot,
            WorkspaceEnvironment(workspace, paths),
            ProcessPriorityClass.BelowNormal);
        using var readinessCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readinessCancellation.CancelAfter(_coverageShutdownTimeout);
        try
        {
            var result = await _runner.RunAsync(command, readinessCancellation.Token).ConfigureAwait(false);
            if (result.ExitCode != 0)
                throw new ContinuousTestProviderException(
                    $"Coverage collector readiness failed with exit code {result.ExitCode}: " +
                    FailureSummary(result));
            if (!File.Exists(readinessPath))
                throw new ContinuousTestProviderException(
                    $"Coverage collector readiness produced no snapshot at '{readinessPath}'.");
        }
        finally
        {
            if (File.Exists(readinessPath))
                File.Delete(readinessPath);
        }
    }

    /// <remarks>
    /// The test assembly is instrumented alongside the product assemblies on purpose: B-3 narrows by
    /// intersecting changed files with a test's covered set, so a test that does not cover its own
    /// source file would never be selected when that file changes.
    /// </remarks>
    private static IReadOnlyList<string> InstrumentableAssemblies(CtGenerationPaths paths)
    {
        var assemblies = Directory
            .EnumerateFiles(paths.OutDir, "*.dll")
            .Where(path => File.Exists(Path.ChangeExtension(path, ".pdb")))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (assemblies.Length == 0)
            throw new ContinuousTestProviderException(
                $"Per-test coverage found no instrumentable assemblies (a '*.dll' with a sibling '*.pdb') " +
                $"under '{paths.OutDir}'.");

        return assemblies;
    }

    private async Task RunCoverageCommandAsync(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var command = new TestProcessCommand(
            _dotnetCoveragePath,
            arguments,
            workspace.WorkspaceRoot,
            WorkspaceEnvironment(workspace, paths),
            ProcessPriorityClass.BelowNormal);
        var result = await _runner.RunAsync(command, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new ContinuousTestProviderException(
                $"Coverage command '{command.ToDisplayString()}' failed with exit code {result.ExitCode}: " +
                FailureSummary(result));
    }

    private async Task ShutdownCoverageSessionAsync(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths,
        CoverageSession coverage)
    {
        var command = new TestProcessCommand(
            _dotnetCoveragePath,
            ["shutdown", coverage.SessionId],
            workspace.WorkspaceRoot,
            WorkspaceEnvironment(workspace, paths),
            ProcessPriorityClass.BelowNormal);
        Exception? shutdownFailure = null;
        using var shutdownCancellation = new CancellationTokenSource(_coverageShutdownTimeout);
        try
        {
            var shutdownResult = await _runner.RunAsync(command, shutdownCancellation.Token).ConfigureAwait(false);
            if (shutdownResult.ExitCode != 0)
                throw new ContinuousTestProviderException(
                    $"Coverage collector shutdown failed with exit code {shutdownResult.ExitCode}: " +
                    FailureSummary(shutdownResult));

            var collectorResult = await coverage.Process.WaitForExitAsync(shutdownCancellation.Token)
                .ConfigureAwait(false);
            if (collectorResult.ExitCode != 0)
                throw new ContinuousTestProviderException(
                    $"Coverage collector exited with code {collectorResult.ExitCode}: " +
                    FailureSummary(collectorResult));
        }
        catch (OperationCanceledException) when (shutdownCancellation.IsCancellationRequested)
        {
            shutdownFailure = new ContinuousTestProviderException(
                $"Coverage collector shutdown exceeded {_coverageShutdownTimeout} for session '{coverage.SessionId}'.");
        }
        catch (Exception exception)
        {
            shutdownFailure = exception;
        }

        if (shutdownFailure is null)
        {
            await coverage.Process.DisposeAsync().ConfigureAwait(false);
            return;
        }

        Exception? terminationFailure = null;
        try
        {
            coverage.Process.TerminateProcessTree();
            using var terminationCancellation = new CancellationTokenSource(_coverageShutdownTimeout);
            await coverage.Process.WaitForExitAsync(terminationCancellation.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            terminationFailure = exception;
        }

        try
        {
            await coverage.Process.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            terminationFailure = terminationFailure is null
                ? exception
                : new AggregateException(terminationFailure, exception);
        }

        if (terminationFailure is not null)
            throw new ContinuousTestProviderException(
                shutdownFailure.Message + " Collector process-tree termination also failed: " + terminationFailure.Message,
                new AggregateException(shutdownFailure, terminationFailure));

        ExceptionDispatchInfo.Capture(shutdownFailure).Throw();
    }

    private static ContinuousTestProviderException CombinedRunAndCleanupFailure(
        Exception runFailure,
        Exception cleanupFailure) =>
        new(
            runFailure.Message + " Coverage collector cleanup also failed: " + cleanupFailure.Message,
            new AggregateException(runFailure, cleanupFailure));

    private static IReadOnlyList<ProviderCoverageArtifact> CompactCoverageSnapshots(
        ContinuousTestProviderRunRequest request,
        CtGenerationPaths paths,
        CoverageSession coverage,
        ProviderRunResult runResult)
    {
        var testCaseIdsByKey = request.TestCaseIds
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(CoverageSnapshotKey, id => id, StringComparer.Ordinal);
        var observedIdsByKey = ObservedCaseIdsBySnapshotKey(request, runResult);
        var artifacts = new List<ProviderCoverageArtifact>();
        var filesByTestCaseId = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var completeByTestCaseId = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var snapshotPath in Directory
                     .EnumerateFiles(coverage.CoverageDirectory, "*" + CoverageSnapshotExtension)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            var key = Path.GetFileNameWithoutExtension(snapshotPath);
            if (!testCaseIdsByKey.TryGetValue(key, out var testCaseId)
                && !observedIdsByKey.TryGetValue(key, out testCaseId))
                throw new ContinuousTestProviderException(
                    $"Per-test coverage snapshot '{Path.GetFileName(snapshotPath)}' matches none of the " +
                    $"{request.TestCaseIds.Count} selected test case id(s).");

            var (filePaths, complete) = ReadSnapshotFilePaths(snapshotPath, request.Workspace.WorkspaceRoot);
            if (!filesByTestCaseId.TryGetValue(testCaseId, out var files))
            {
                files = new SortedSet<string>(StringComparer.Ordinal);
                filesByTestCaseId.Add(testCaseId, files);
                completeByTestCaseId.Add(testCaseId, value: true);
            }

            files.UnionWith(filePaths);
            completeByTestCaseId[testCaseId] &= complete;
            File.Delete(snapshotPath);
        }

        foreach (var (testCaseId, files) in filesByTestCaseId)
            artifacts.Add(WriteCoverageFileList(
                coverage,
                paths,
                testCaseId,
                files.ToArray(),
                completeByTestCaseId[testCaseId] && files.Count > 0));

        foreach (var testCaseId in testCaseIdsByKey.Values.Where(id => !filesByTestCaseId.ContainsKey(id)))
            artifacts.Add(WriteCoverageFileList(coverage, paths, testCaseId, [], complete: false));

        if (File.Exists(coverage.SessionArtifactPath))
            File.Delete(coverage.SessionArtifactPath);

        return artifacts
            .OrderBy(artifact => artifact.TestCaseId, StringComparer.Ordinal)
            .ToArray();
    }

    private static ProviderCoverageArtifact WriteCoverageFileList(
        CoverageSession coverage,
        CtGenerationPaths paths,
        string testCaseId,
        IReadOnlyList<string> filePaths,
        bool complete)
    {
        var fileListPath = Path.Combine(
            coverage.CoverageDirectory,
            CoverageSnapshotKey(testCaseId) + CoverageFileListExtension);
        File.WriteAllText(fileListPath, string.Concat(filePaths.Select(path => path + "\n")));
        return new ProviderCoverageArtifact(
            ArtifactPath: fileListPath,
            Parser: CoverageFileListParser,
            ArtifactRoot: paths.GenerationRoot,
            TestCaseId: testCaseId,
            GenerationId: paths.GenerationId,
            Complete: complete);
    }

    /// <remarks>
    /// A theory whose data is not enumerable at discovery time is selected by its method-level id,
    /// but the snapshot hook keys by each executed test's display name, which carries the argument
    /// list and reaches the parser only via test-starting events. Mapping the observed names back
    /// to the selected method id lets their snapshots union into that method's map instead of
    /// failing the unmatched-snapshot guard.
    /// </remarks>
    private static IReadOnlyDictionary<string, string> ObservedCaseIdsBySnapshotKey(
        ContinuousTestProviderRunRequest request,
        ProviderRunResult runResult)
    {
        var selectedIds = new HashSet<string>(request.TestCaseIds, StringComparer.Ordinal);
        var observedIdsByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var observedId in runResult.CaseResults
                     .Select(row => row.TestCaseId)
                     .Concat(runResult.TestDisplayNames.Select(XunitTestCaseId))
                     .Distinct(StringComparer.Ordinal))
        {
            var selectedId = selectedIds.Contains(observedId)
                ? observedId
                : XunitMethodFromTestCaseId(observedId) is { } method
                    && selectedIds.Contains(XunitTestCaseId(method))
                    ? XunitTestCaseId(method)
                    : null;
            if (selectedId is not null)
                observedIdsByKey[CoverageSnapshotKey(observedId)] = selectedId;
        }

        return observedIdsByKey;
    }

    private static (IReadOnlyList<string> FilePaths, bool Complete) ReadSnapshotFilePaths(
        string snapshotPath,
        string workspaceRoot)
    {
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(
                snapshotPath,
                new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                });
            document = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return ([], false);
        }

        var filePaths = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var module in document.Descendants("module"))
        {
            var sourcePathsById = module
                .Descendants("source_file")
                .Where(row => row.Attribute("id") is not null && row.Attribute("path") is not null)
                .GroupBy(row => row.Attribute("id")!.Value, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().Attribute("path")!.Value, StringComparer.Ordinal);
            if (sourcePathsById.Count == 0)
                continue;

            foreach (var range in module.Descendants("range"))
            {
                if (!IsCoveredRange(range.Attribute("covered")?.Value))
                    continue;
                if (range.Attribute("source_id")?.Value is not { } sourceId
                    || !sourcePathsById.TryGetValue(sourceId, out var sourcePath))
                    continue;
                if (WorkspaceRelativePath(workspaceRoot, sourcePath) is { } relativePath)
                    filePaths.Add(relativePath);
            }
        }

        return (filePaths.ToArray(), true);
    }

    private static bool IsCoveredRange(string? covered) =>
        string.Equals(covered, "yes", StringComparison.OrdinalIgnoreCase)
        || string.Equals(covered, "partial", StringComparison.OrdinalIgnoreCase);

    private static string? WorkspaceRelativePath(string workspaceRoot, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            return null;

        var fullPath = Path.GetFullPath(sourcePath, workspaceRoot);
        var relativePath = Path.GetRelativePath(Path.GetFullPath(workspaceRoot), fullPath);
        if (Path.IsPathRooted(relativePath)
            || relativePath == ".."
            || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            return null;

        return relativePath
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
    }

    /// <remarks>
    /// Store test-case ids contain <c>:</c>, which is not a legal Windows path segment. The snapshot
    /// file name is the id's digest — the same truncated lowercase-hex SHA-256 idiom
    /// <c>CanonicalIds.StableId</c> uses — and the xunit hook derives it identically.
    /// </remarks>
    internal static string CoverageSnapshotKey(string testCaseId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(testCaseId)))
            .ToLowerInvariant()[..CoverageSnapshotKeyLength];

    private static IReadOnlyList<ProviderCoverageArtifact> DiscoverCoverageArtifacts(CtGenerationPaths generation)
    {
        if (!Directory.Exists(generation.GenerationRoot))
            return [];

        var paths = new HashSet<string>(PathStringComparer);
        var artifacts = new List<ProviderCoverageArtifact>();
        AddCoverageArtifacts(
            artifacts,
            paths,
            generation.GenerationRoot,
            "coverage.cobertura.xml",
            "cobertura");
        AddCoverageArtifacts(
            artifacts,
            paths,
            generation.GenerationRoot,
            "*.cobertura.xml",
            "cobertura");
        AddCoverageArtifacts(
            artifacts,
            paths,
            generation.GenerationRoot,
            "coverage.info",
            "lcov");
        AddCoverageArtifacts(
            artifacts,
            paths,
            generation.GenerationRoot,
            "lcov.info",
            "lcov");
        return artifacts;
    }

    private static void AddCoverageArtifacts(
        List<ProviderCoverageArtifact> artifacts,
        HashSet<string> paths,
        string artifactRoot,
        string pattern,
        string parser)
    {
        foreach (var path in Directory.EnumerateFiles(artifactRoot, pattern, SearchOption.AllDirectories))
        {
            var fullPath = Path.GetFullPath(path);
            if (!paths.Add(fullPath))
                continue;

            artifacts.Add(new ProviderCoverageArtifact(
                ArtifactPath: fullPath,
                Parser: parser,
                ArtifactRoot: artifactRoot));
        }
    }

    private static IEnumerable<string> JsonLines(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static JsonDocument ParseJsonLine(string line)
    {
        try
        {
            return JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            throw new ContinuousTestProviderException($"Invalid provider JSONL line: {line}", ex);
        }
    }

    private static void RequireContractVersion(JsonElement root)
    {
        var version = RequiredString(root, "contract_version");
        if (!string.Equals(version, ContractVersion, StringComparison.Ordinal))
            throw new ContinuousTestProviderException(
                $"Unsupported provider contract version '{version}', expected '{ContractVersion}'.");
    }

    private static string RequiredString(JsonElement root, string propertyName)
    {
        var value = OptionalString(root, propertyName);
        if (string.IsNullOrWhiteSpace(value))
            throw new ContinuousTestProviderException($"Provider JSONL missing required '{propertyName}'.");

        return value;
    }

    private static string? OptionalString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;

        if (value.ValueKind != JsonValueKind.String)
            throw new ContinuousTestProviderException($"Provider JSONL property '{propertyName}' must be a string.");

        return value.GetString();
    }

    private static double? OptionalDouble(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number))
            throw new ContinuousTestProviderException($"Provider JSONL property '{propertyName}' must be a number.");

        return number;
    }

    private static int OptionalInt(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
            return 0;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
            throw new ContinuousTestProviderException($"Provider JSONL property '{propertyName}' must be an integer.");

        return number;
    }

    private static DateTimeOffset? OptionalDateTimeOffset(JsonElement root, string propertyName)
    {
        var value = OptionalString(root, propertyName);
        return value is null
            ? null
            : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
    }

    private static string? FirstString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
                return item.GetString();
        }

        return null;
    }

    private static string RunStamp(DateTimeOffset? startedAt) =>
        startedAt?.UtcDateTime.ToString("yyyyMMddHHmmssffff", CultureInfo.InvariantCulture)
        ?? "unknown";

    private static StringComparer PathStringComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static void AddOptionalMetadata(
        Dictionary<string, object?> metadata,
        string metadataName,
        JsonElement root,
        string jsonName)
    {
        var value = OptionalString(root, jsonName);
        if (value is not null)
            metadata[metadataName] = value;
    }

    private static IReadOnlyDictionary<string, object?> Metadata(JsonElement root)
    {
        if (!root.TryGetProperty("metadata", out var value) || value.ValueKind == JsonValueKind.Null)
            return new ReadOnlyDictionary<string, object?>(
                new Dictionary<string, object?>(StringComparer.Ordinal));
        if (value.ValueKind != JsonValueKind.Object)
            throw new ContinuousTestProviderException("Provider JSONL property 'metadata' must be an object.");

        var metadata = JsonSerializer.Deserialize<Dictionary<string, object?>>(value.GetRawText())
            ?? new Dictionary<string, object?>(StringComparer.Ordinal);
        return new ReadOnlyDictionary<string, object?>(metadata);
    }
}
