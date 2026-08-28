using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Miller.Testing.Parsing;

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

    /// <remarks>
    /// Deliberately short. The race a CT artifact delete loses is an antivirus scan window or a handle
    /// a just-exited collector has not released yet — both tens of milliseconds — and every one of
    /// these deletes is cleanup, where a long stall only delays the real error.
    /// </remarks>
    private static readonly TimeSpan DeleteRetryBudget = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan DeleteRetryInitialDelay = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan DeleteRetryMaxDelay = TimeSpan.FromMilliseconds(100);

    private readonly ITestProcessRunner _runner;

    /// <summary>
    /// Lets the run reuse the generation the discovery before it built, instead of building the same source
    /// state into a second empty output directory. One per provider instance, keyed by project build root.
    /// </summary>
    private readonly CtGenerationHandoff _generations = new();
    private readonly ITestBackgroundProcessRunner? _backgroundRunner;
    private readonly string _dotnetPath;
    private readonly string _dotnetCoveragePath;
    private readonly TimeSpan _coverageShutdownTimeout;
    private readonly Action<string> _deleteFile;
    private readonly Action<TimeSpan> _deleteRetrySleep;

    public DotnetTestProvider(
        ITestProcessRunner runner,
        string dotnetPath = "dotnet",
        string dotnetCoveragePath = "dotnet-coverage")
        : this(runner, dotnetPath, dotnetCoveragePath, DefaultCoverageShutdownTimeout)
    {
    }

    /// <remarks>
    /// <c>deleteFile</c> and <c>deleteRetrySleep</c> are test seams for every CT artifact delete;
    /// production passes <c>File.Delete</c> and <c>Thread.Sleep</c>. The delete seam exists because a
    /// cleanup delete is only best effort on paper unless a test can MAKE it fail: on Windows a held
    /// handle blocks the delete, but on Linux and macOS <c>FileShare</c> is advisory and <c>unlink</c>
    /// ignores it, so a test built on a real file lock proves nothing off Windows. The sleep seam lets a
    /// test drive the whole retry loop without spending the half-second budget.
    /// </remarks>
    internal DotnetTestProvider(
        ITestProcessRunner runner,
        string dotnetPath,
        string dotnetCoveragePath,
        TimeSpan coverageShutdownTimeout,
        Action<string>? deleteFile = null,
        Action<TimeSpan>? deleteRetrySleep = null)
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
        _deleteFile = deleteFile ?? File.Delete;
        _deleteRetrySleep = deleteRetrySleep ?? Thread.Sleep;
    }

    public async Task<IReadOnlyList<ProviderTestCase>> DiscoverAsync(
        ContinuousTestWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);

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
            DeleteWithRetry(diagnosticPath);
            command = BuildGenericDiscoverCommand(workspace, paths, targetPath, diagnosticPath);
        }
        else
        {
            RequireSelfExecutingTestAssembly(workspace, paths);
            command = BuildDiscoverCommand(workspace, paths);
        }
        var result = await _runner.RunAsync(command, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new ContinuousTestProviderException(
                $"Test discovery failed with exit code {result.ExitCode}: {FailureSummary(result)}");

        if (GenericFramework(workspace.Framework) is { } framework)
            return ParseGenericDiscoveryDiagnostic(diagnosticPath!, framework, workspace.WorkspaceRoot) is { Count: > 0 } cases
                ? cases
                : ParseGenericDiscovery(
                    result.RequireCompleteStandardOutput("Test discovery"), framework);

        return ParseDiscovery(result.RequireCompleteStandardOutput("Test discovery"));
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

        RequireSelfExecutingTestAssembly(request.Workspace, paths);
        var coverage = request.CoverageMode == ContinuousTestCoverageMode.PerTest
            ? await StartCoverageSessionAsync(request.Workspace, paths, cancellationToken).ConfigureAwait(false)
            : null;
        var commands = BuildRunCommands(request, paths, coverage);
        IReadOnlyList<ContinuousTestProviderChunkProgress> progress = BuildXunitChunkProgress(request);
        if (progress.Count != commands.Count)
            throw new InvalidOperationException("xUnit chunk progress did not match the run invocations");
        var resultArtifactPath = XunitResultArtifactPath(request, paths, commands.Count == 1 ? null : 0);
        var results = new List<TestProcessResult>(commands.Count);
        Exception? runFailure = null;
        try
        {
            // Sequential on purpose. The invocations share one test executable, one generation
            // directory, and - under per-test coverage - one collector session, so running them
            // concurrently would have them overwrite each other's output and attribute each other's
            // coverage. The whole point of the CT budget is that a workspace runs one thing at a time.
            for (var index = 0; index < commands.Count; index++)
            {
                request.Progress?.Invoke(progress[index]);
                results.Add(await _runner.RunAsync(commands[index], cancellationToken).ConfigureAwait(false));
            }
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

        if (IsXunitArtifactOnlyRun(request))
            return ValidateXunitArtifactOnlyRun(request, paths, resultArtifactPath, results[0]);

        var runResult = MergeRuns(results, request.SelectedRevision, request.IndexIdentity);

        // Judged across the whole run, not per invocation: a chunked selection is ONE logical run, and
        // a single failing chunk beside several that parsed results is a test failure, not a harness
        // crash. Only a run that produced no case results at all is unparseable.
        var failedInvocation = results.FirstOrDefault(static invocation => invocation.ExitCode != 0);
        if (failedInvocation is not null && runResult.CaseResults.Count == 0)
            throw new ContinuousTestProviderException(
                $"Dotnet test run failed with exit code {failedInvocation.ExitCode}: {FailureSummary(failedInvocation)}");
        if (failedInvocation is null && runResult.CaseResults.Count == 0 && request.TestCaseIds.Count > 0)
            throw new ContinuousTestProviderException(
                "xUnit run selected " + request.TestCaseIds.Count + " test case(s) but executed none: " +
                string.Join(", ", request.TestCaseIds) + ".");

        // A non-zero exit is a FAILED run even when cases parsed. The xUnit parser takes the run status from
        // the `test-assembly-finished` event, so a killed or truncated assembly whose executed tests all
        // passed was recorded "passed" — a wedged run that the stall guard shot looked like a clean one.
        // CLAUDE.md is explicit that green needs COMPLETE results at the selected key.
        if (failedInvocation is not null)
            runResult = runResult with { Status = "failed" };

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

        var args = new List<string> { "-list", "full/json", "-noLogo", "-noColor", PreEnumerateTheories };
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

    /// <summary>
    /// Preview/test seam: every invocation the request would run, in order. A selection that fits one
    /// command line yields exactly one, so this is the same command <see cref="BuildRunCommand"/>
    /// returns; a wider selection yields the chunks it is split into. Production runs never use it -
    /// <see cref="RunAsync"/> allocates its own generation and builds every command from that handle.
    /// </summary>
    public IReadOnlyList<TestProcessCommand> BuildRunCommands(ContinuousTestProviderRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return BuildRunCommands(
            request,
            CtGenerationPaths.ResolveLatestOrFirst(request.Workspace),
            coverage: null);
    }

    private TestProcessCommand BuildRunCommand(
        ContinuousTestProviderRunRequest request,
        CtGenerationPaths paths)
        => BuildRunCommand(request, paths, coverage: null);

    private TestProcessCommand BuildRunCommand(
        ContinuousTestProviderRunRequest request,
        CtGenerationPaths paths,
        CoverageSession? coverage)
        => BuildRunCommands(request, paths, coverage)[0];

    /// <summary>
    /// Builds the invocations for one run. A selection that fits the platform command-line cap is a
    /// single command, byte-identical to what this provider sent before chunking existed; a wider one
    /// is split across several invocations of the same test executable.
    ///
    /// xunit v3 emits two argv elements per selected method (<c>-method &lt;FQN&gt;</c>) and has NO
    /// response-file option, so a wide selection has nowhere to go but across processes. Miller's own
    /// suite is ~6,000 methods averaging ~100-character names: a 644 KB command line against a 32,767
    /// Windows cap, where only ~300 methods fit a single invocation.
    ///
    /// mstest/nunit spend the selection differently — one <c>--filter</c> expression rather than a pair
    /// of elements per test — so they are chunked by <see cref="BuildGenericRunCommands"/> on the
    /// composed expression's byte length instead.
    /// </summary>
    private IReadOnlyList<TestProcessCommand> BuildRunCommands(
        ContinuousTestProviderRunRequest request,
        CtGenerationPaths paths,
        CoverageSession? coverage)
    {
        if (GenericFramework(request.Framework ?? request.Workspace.Framework) is not null)
            return BuildGenericRunCommands(request, paths, TestAssemblyPath(request.Workspace, paths))
                .Select(static invocation => invocation.Command)
                .ToArray();

        // An explicit FilterArguments override is opaque argv: Miller does not know which elements are
        // flags and which are their values, so there is no boundary it can split on safely. It travels
        // as one invocation - splitting it blind could separate a flag from its value and silently run
        // the wrong tests.
        if (request.FilterArguments.Count > 0)
            return [BuildXunitRunCommand(request, paths, coverage, request.FilterArguments, part: null)];

        // A whole-suite run covers every known case, so it runs the assembly ONCE with no selection argv —
        // the same command an empty selection has always produced. The request still carries the full id
        // list; only the argv is unfiltered.
        IReadOnlyList<IReadOnlyList<string>> units = XunitSelectionUnits(request);
        if (request.WholeSuite || units.Count == 0)
            return [BuildXunitRunCommand(request, paths, coverage, [], part: null)];

        IReadOnlyList<IReadOnlyList<IReadOnlyList<string>>> chunks =
            CtArgvChunking.Chunk(units, CtArgvChunking.ArgvCost);
        var commands = new List<TestProcessCommand>(chunks.Count);
        for (var index = 0; index < chunks.Count; index++)
        {
            var selection = chunks[index].SelectMany(static unit => unit).ToArray();
            commands.Add(BuildXunitRunCommand(
                request,
                paths,
                coverage,
                selection,
                part: chunks.Count == 1 ? null : index));
        }

        return commands;
    }

    /// <summary>
    /// The selection as chunkable units. Each unit is the argv elements that must travel together, so
    /// a chunk boundary can never fall between a flag and its value.
    /// </summary>
    private static IReadOnlyList<IReadOnlyList<string>> XunitSelectionUnits(
        ContinuousTestProviderRunRequest request)
    {
        var units = new List<IReadOnlyList<string>>();
        var selectedMethods = new HashSet<string>(StringComparer.Ordinal);
        foreach (var testCaseId in request.TestCaseIds)
        {
            if (XunitMethodFromTestCaseId(testCaseId) is not { } method)
            {
                units.Add(["-id", testCaseId]);
                continue;
            }

            if (!selectedMethods.Add(method))
                continue;

            units.Add(["-method", method]);
        }

        return units;
    }

    private TestProcessCommand BuildXunitRunCommand(
        ContinuousTestProviderRunRequest request,
        CtGenerationPaths paths,
        CoverageSession? coverage,
        IReadOnlyList<string> selection,
        int? part)
    {
        var artifactOnly = IsXunitArtifactOnlyRun(request);
        var args = new List<string> { "-noLogo", "-noColor", "-reporter", artifactOnly ? "verbose" : "json" };
        if (artifactOnly)
            args.Add("-noAutoReporters");
        args.Add(PreEnumerateTheories);
        if (XunitResultArtifactPath(request, paths, part) is { } resultArtifactPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(resultArtifactPath)!);
            args.Add("-jUnit");
            args.Add(resultArtifactPath);
        }

        args.AddRange(selection);

        // Trait exclusions intersect with (never replace) per-test-ID selection: the xunit v3 runner
        // treats -trait- flags as an AND-filter, so they are appended alongside -id args regardless of
        // the FilterArguments override above. They ride on EVERY chunk, because each chunk is a whole
        // run of the same executable and would otherwise filter differently than its siblings.
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

    private static bool IsXunitArtifactOnlyRun(ContinuousTestProviderRunRequest request) =>
        request.WholeSuite
        && request.CoverageMode == ContinuousTestCoverageMode.None
        && GenericFramework(request.Framework ?? request.Workspace.Framework) is null;

    private static ProviderRunResult ValidateXunitArtifactOnlyRun(
        ContinuousTestProviderRunRequest request,
        CtGenerationPaths paths,
        string? resultArtifactPath,
        TestProcessResult processResult)
    {
        if (string.IsNullOrWhiteSpace(resultArtifactPath) || !File.Exists(resultArtifactPath))
        {
            var diagnostic = FailureSummary(processResult);
            var diagnosticSuffix = string.IsNullOrWhiteSpace(diagnostic) ? string.Empty : $" Diagnostic: {diagnostic}";
            throw new ContinuousTestProviderException(
                $"xUnit whole-suite run did not produce a JUnit result artifact at '{resultArtifactPath ?? "<none>"}'."
                + diagnosticSuffix);
        }

        ParsedTestArtifactRun parsed;
        try
        {
            parsed = JunitTestResultParser.Parse(resultArtifactPath);
        }
        catch (Exception exception) when (exception is TestArtifactParseException or IOException or UnauthorizedAccessException)
        {
            throw new ContinuousTestProviderException(
                $"xUnit whole-suite run produced an invalid JUnit result artifact '{resultArtifactPath}': "
                + "could not parse: "
                + exception.Message,
                exception);
        }

        if (parsed.Cases.Count == 0)
            throw new ContinuousTestProviderException(
                $"xUnit whole-suite run produced a JUnit result artifact '{resultArtifactPath}' with no test cases.");

        var artifactStatus = parsed.Cases.Any(static testCase => testCase.Status is "failed" or "errored")
            ? "failed"
            : parsed.Cases.All(static testCase => testCase.Status == "skipped")
                ? "skipped"
                : "passed";
        if (processResult.ExitCode is not (0 or 1))
        {
            var diagnostic = FailureSummary(processResult);
            var diagnosticSuffix = string.IsNullOrWhiteSpace(diagnostic) ? string.Empty : $" Diagnostic: {diagnostic}";
            throw new ContinuousTestProviderException(
                $"xUnit whole-suite run exited with unsupported exit code {processResult.ExitCode}; expected "
                + $"0 for a non-failed result or 1 for test failures.{diagnosticSuffix}");
        }

        if ((processResult.ExitCode == 0 && artifactStatus == "failed")
            || (processResult.ExitCode == 1 && artifactStatus != "failed"))
            throw new ContinuousTestProviderException(
                $"xUnit whole-suite run exit code {processResult.ExitCode} disagreed with JUnit artifact "
                + $"status '{artifactStatus}' at '{resultArtifactPath}'.");

        return new ProviderRunResult(
            RunId: request.RunId ?? $"xunit:{Path.GetFileNameWithoutExtension(resultArtifactPath)}",
            Status: artifactStatus,
            ResultArtifactPath: resultArtifactPath,
            CoverageArtifacts: DiscoverCoverageArtifacts(paths))
        {
            GenerationId = paths.GenerationId,
        };
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

    /// <summary>
    /// Refuses an xunit operation whose build produced the test DLL but no self-executing assembly beside it,
    /// naming the cause instead of letting the spawn fail.
    ///
    /// <para>That pair — dll present, executable absent — is exactly what an xUnit v2 project builds: v2 runs
    /// under VSTest (a dll plus <c>testhost.exe</c>), while CT runs the executable that xUnit v3 /
    /// Microsoft.Testing.Platform produces. Without this the operation reached <c>Process.Start</c> and failed
    /// with the raw OS error for a missing file, which names a path and therefore reads as a broken build. A
    /// user hunted one that did not exist (field report 2026-08-25).</para>
    ///
    /// <para>The check is the SHAPE, not the framework value, so it also covers a v2 project that slipped the
    /// enable-time classification (a csproj whose only xunit package is a runner shared by both generations)
    /// and any future project type that builds the same way. When the DLL is missing too the build itself
    /// failed or wrote somewhere else, which this must not describe as an xunit generation problem — it says
    /// nothing and lets the original error stand.</para>
    /// </summary>
    private static void RequireSelfExecutingTestAssembly(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths)
    {
        string executable = TestExecutablePath(workspace, paths);
        if (File.Exists(executable))
            return;
        string assembly = TestAssemblyPath(workspace, paths);
        if (!File.Exists(assembly))
            return;

        throw new ContinuousTestProviderException(
            $"{ContinuousTestFrameworkSupport.XunitV2Reason}: '{workspace.ProjectPath}' built "
            + $"{Path.GetFileName(assembly)} but no executable at {executable}. "
            + ContinuousTestFrameworkSupport.XunitV2Remedy);
    }

    private static string TestExecutablePath(ContinuousTestWorkspace workspace, CtGenerationPaths paths) =>
        Path.Combine(
            ProjectOutputDirectory(paths, workspace.ProjectPath),
            Path.GetFileNameWithoutExtension(workspace.ProjectPath) + ExecutableExtension());

    private static string TestAssemblyPath(ContinuousTestWorkspace workspace, CtGenerationPaths paths) =>
        Path.Combine(
            ProjectOutputDirectory(paths, workspace.ProjectPath),
            Path.GetFileNameWithoutExtension(workspace.ProjectPath) + ".dll");

    internal static string ProjectOutputDirectory(CtGenerationPaths paths, string projectPath) =>
        Path.Combine(paths.OutDir, Path.GetFileNameWithoutExtension(projectPath));

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
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);

        // The project's OWN run-settings environment block goes in first, so CT's operational variables
        // below overwrite it. CT runs the built test executable rather than `dotnet test`, so nothing else
        // applies that block: a project that declared one ran WITHOUT it, which was the single largest cause
        // of CT calling a green suite red. See RunSettingsEnvironment.
        foreach ((string name, string value) in RunSettingsEnvironment.ForProject(workspace.ProjectPath))
            environment[name] = value;

        // The daemon's own workspace variable must not reach a test process: a `miller` CLI verb run inside
        // a test would bind the DAEMON's workspace instead of the test's own root. The test process inherits
        // it from the daemon, so it has to be removed rather than merely left unset — a null value is how
        // TestProcessRunner.BuildStartInfo spells "remove".
        environment[CtEnvironment.DaemonWorkspaceRoot] = null;

        // CT's own variables win over anything the project declared. TMP/TEMP/TMPDIR are a containment
        // guarantee (every temp file lands under this generation), and the workspace root is how a test
        // finds the repo it is testing.
        environment[CtEnvironment.WorkspaceRoot] = workspace.WorkspaceRoot;
        environment["TMPDIR"] = paths.TempDirectory;
        environment["TMP"] = paths.TempDirectory;
        environment["TEMP"] = paths.TempDirectory;
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
        var invocations = BuildGenericRunCommands(request, paths, targetPath);
        IReadOnlyList<ContinuousTestProviderChunkProgress> progress =
            BuildGenericChunkProgress(request, paths, targetPath);
        if (progress.Count != invocations.Count)
            throw new InvalidOperationException("generic chunk progress did not match the run invocations");
        var parsed = new List<ProviderRunResult>(invocations.Count);
        var diagnostics = new List<string>();

        // Sequential on purpose, exactly as the xunit path: the invocations share one built test
        // assembly and one generation directory, and the CT budget lets one workspace run at a time.
        for (var index = 0; index < invocations.Count; index++)
        {
            request.Progress?.Invoke(progress[index]);
            GenericInvocation invocation = invocations[index];
            var result = await _runner.RunAsync(invocation.Command, cancellationToken).ConfigureAwait(false);

            // An empty or missing artifact is LOCAL to the chunk that produced it. vstest writes the TRX
            // for every invocation that started, so a missing one means THAT chunk never ran: its own
            // selected ids are recorded FAILED against the exit code — never left absent, because a
            // chunk that never ran must not read as "no failures" — and every sibling keeps the verdicts
            // it earned. Failing the whole run here discarded the parts already on disk, skipped the
            // parts not yet started, and the retry reproduced it forever.
            if (!File.Exists(invocation.ResultArtifactPath))
            {
                var diagnostic = result.ExitCode != 0
                    ? $"Dotnet test run failed with exit code {result.ExitCode}: {FailureSummary(result)}"
                    : $"Dotnet test run did not produce TRX result artifact '{invocation.ResultArtifactPath}'.";
                diagnostics.Add(diagnostic);

                // An invocation that selected nothing answers for no ids, so there is no honest row to
                // write for it and its failure stands only for the run as a whole.
                if (invocation.SelectedTestCaseIds.Count > 0)
                    parsed.Add(UnrunPartResult(request, invocation, result, diagnostic));
                continue;
            }

            var part = ParseTrxRun(invocation.ResultArtifactPath, request);

            // A part whose filter matched nothing says nothing about its own ids: they stay unreported,
            // and the store flips an unreported id to stale when the run completes — which is exactly
            // what an unchunked run did with the ids vstest did not match. The diagnostic is kept for
            // the case where NO part produced a single verdict, which is still a run failure.
            if (part.Run.CaseResults.Count == 0 && invocation.SelectedTestCaseIds.Count > 0)
                diagnostics.Add(part.RunError ?? NoSelectedResultsMessage);

            parsed.Add(part.Run);
        }

        if (parsed.Count == 0)
            throw new ContinuousTestProviderException(diagnostics.FirstOrDefault() ?? NoSelectedResultsMessage);

        var runResult = MergeRunResults(parsed);

        // Judged across the whole run, exactly as the xunit path judges its invocations: only a run that
        // produced no case result at all is unparseable.
        if (runResult.CaseResults.Count == 0 && request.TestCaseIds.Count > 0)
            throw new ContinuousTestProviderException(diagnostics.FirstOrDefault() ?? NoSelectedResultsMessage);

        if (request.RunId is not null && !string.Equals(runResult.RunId, request.RunId, StringComparison.Ordinal))
            runResult = runResult with { RunId = request.RunId };

        return runResult with
        {
            // The first part that actually wrote an artifact, which is the unsuffixed single path when
            // the selection fit one command line. Every other part stays on disk beside it, and each
            // case result carries the artifact it came from in its metadata. Naming part 000
            // unconditionally would report a path that does not exist whenever part 000 is the chunk
            // that died, and the coordinator would record no evidence for a run that has plenty.
            ResultArtifactPath = invocations
                .Select(static invocation => invocation.ResultArtifactPath)
                .FirstOrDefault(File.Exists),
            CoverageArtifacts = DiscoverCoverageArtifacts(paths),
            GenerationId = paths.GenerationId,
        };
    }

    private static IReadOnlyList<ContinuousTestProviderChunkProgress> BuildXunitChunkProgress(
        ContinuousTestProviderRunRequest request)
    {
        if (request.FilterArguments.Count > 0 || request.WholeSuite)
            return SingleSelectionProgress(request.TestCaseIds);

        IReadOnlyList<IReadOnlyList<string>> units = XunitSelectionUnits(request);
        if (units.Count == 0)
            return SingleSelectionProgress(request.TestCaseIds);

        IReadOnlyList<IReadOnlyList<IReadOnlyList<string>>> chunks =
            CtArgvChunking.Chunk(units, CtArgvChunking.ArgvCost);
        return Enumerable.Range(1, chunks.Count)
            .Select(part => CtArgvChunking.Describe(chunks, static unit => unit[^1], part))
            .ToArray();
    }

    private IReadOnlyList<ContinuousTestProviderChunkProgress> BuildGenericChunkProgress(
        ContinuousTestProviderRunRequest request,
        CtGenerationPaths paths,
        string targetPath)
    {
        if (request.FilterArguments.Count > 0 || request.WholeSuite)
            return SingleSelectionProgress(request.TestCaseIds);

        var exclusionExpression = GenericExclusionFilter(
            request.Framework ?? request.Workspace.Framework,
            request.ExcludeTraits);
        IReadOnlyList<GenericSelectionUnit> units = GenericSelectionUnits(request);
        if (units.Count == 0)
            return SingleSelectionProgress(request.TestCaseIds);

        string runHash = TrxRunHash(request);
        var chunks = CtArgvChunking.Chunk(
            units,
            static unit => GenericFilterTermCost(unit.Term),
            maxUnits: GenericMaxTermsPerInvocation,
            maxBytes: GenericSelectionBudget(paths, targetPath, runHash, exclusionExpression));
        return Enumerable.Range(1, chunks.Count)
            .Select(part => CtArgvChunking.Describe(chunks, static unit => unit.Term, part))
            .ToArray();
    }

    private static IReadOnlyList<ContinuousTestProviderChunkProgress> SingleSelectionProgress(
        IReadOnlyList<string> testCaseIds)
    {
        IReadOnlyList<string> uniqueIds = testCaseIds.Distinct(StringComparer.Ordinal).ToArray();
        if (uniqueIds.Count == 0)
            return [CtArgvChunking.DescribeEmpty()];

        IReadOnlyList<IReadOnlyList<string>> chunks = [uniqueIds];
        return [CtArgvChunking.Describe(chunks, static id => id, currentPart: 1)];
    }

    private const string NoSelectedResultsMessage =
        "Dotnet test run produced no results for the selected test cases.";

    /// <summary>
    /// The verdicts for a chunk that produced no TRX at all. Its own selected ids are recorded FAILED
    /// with the exit code and the failure text, so a chunk that never ran can neither read as "no
    /// failures" nor vanish from the run — and its siblings keep the verdicts they earned.
    /// </summary>
    private static ProviderRunResult UnrunPartResult(
        ContinuousTestProviderRunRequest request,
        GenericInvocation invocation,
        TestProcessResult result,
        string failureSummary)
    {
        var framework = GenericFramework(request.Framework ?? request.Workspace.Framework) ?? "dotnet";
        var caseResults = invocation.SelectedTestCaseIds
            .Select(testCaseId => new ProviderCaseResult(
                Id: CanonicalTrxResultId(request.Workspace.WorkspaceId, testCaseId, request.RunId),
                TestCaseId: testCaseId,
                Status: "failed",
                ResultRevision: request.SelectedRevision,
                IndexIdentity: request.IndexIdentity,
                FailureSummary: failureSummary,
                Metadata: new Dictionary<string, object?>
                {
                    ["artifact_path"] = invocation.ResultArtifactPath,
                    ["framework"] = framework,
                    ["exit_code"] = result.ExitCode,
                }))
            .ToArray();

        return new ProviderRunResult(
            RunId: request.RunId ?? $"trx:{Path.GetFileNameWithoutExtension(invocation.ResultArtifactPath)}",
            Status: "failed",
            CaseResults: caseResults);
    }

    private TestProcessCommand BuildGenericDiscoverCommand(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths)
    {
        var diagnosticPath = DiscoveryDiagnosticPath(paths);
        DeleteWithRetry(diagnosticPath);
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

    /// <summary>
    /// One <c>dotnet test</c> invocation together with the TRX artifact it writes and the request test
    /// case ids it answers for. All three travel together because a chunked run gives each part its own
    /// artifact and its own slice of the selection: recomputing the path at the call site would read one
    /// that has drifted from the command, and a part that dies before writing a TRX must be reported
    /// against ITS OWN ids only — never the whole selection, which its healthy siblings already
    /// answered for.
    /// </summary>
    private readonly record struct GenericInvocation(
        TestProcessCommand Command,
        string ResultArtifactPath,
        IReadOnlyList<string> SelectedTestCaseIds);

    /// <summary>
    /// One chunkable unit of the selection: the filter term a request id composed, plus every request id
    /// that resolved to it. Terms are deduplicated in request order, exactly as the single-expression
    /// composition did, so a selection that fits one command line still composes the string it always
    /// did — but a term two ids share still answers for both, because a dead chunk must report every id
    /// it carried.
    /// </summary>
    private readonly record struct GenericSelectionUnit(string Term, IReadOnlyList<string> TestCaseIds);

    /// <summary>
    /// Builds the invocations for one mstest/nunit run.
    ///
    /// vstest spends a selection differently from xunit v3: the whole selection is ONE conjunctive
    /// <c>--filter</c> expression in a single argv element, so a wide selection makes that one element
    /// carry the entire command-line budget. Miller's own suite is ~6,000 tests averaging ~100-character
    /// names, which composes a ~600 KB filter string — a single argument twenty times the 32,767
    /// Windows cap. A bound on the number of selected tests cannot see that, so the chunk boundary here
    /// is the composed expression's UTF-8 BYTE length, charged per term including the <c>|</c> that
    /// joins it and net of the fixed argv the expression shares the command line with.
    ///
    /// A selection that already fits stays exactly one invocation, with the same filter string and the
    /// same unsuffixed artifact path it had before chunking existed.
    /// </summary>
    private IReadOnlyList<GenericInvocation> BuildGenericRunCommands(
        ContinuousTestProviderRunRequest request,
        CtGenerationPaths paths,
        string targetPath)
    {
        paths.EnsureDirectories();

        // Resolved ONCE for the whole run: without a RunId the key is seeded from the wall clock, so
        // recomputing it per part would scatter the parts across unrelated artifact names.
        var runHash = TrxRunHash(request);

        // An explicit FilterArguments override is opaque argv, the same rule the xunit path applies:
        // Miller does not know which elements are flags and which are their values, so there is no
        // boundary it can split on safely and the override travels as one invocation.
        if (request.FilterArguments.Count > 0)
            return
            [
                BuildGenericInvocation(
                    request,
                    paths,
                    targetPath,
                    runHash,
                    part: null,
                    filter: null,
                    selectedTestCaseIds: request.TestCaseIds),
            ];

        var exclusionExpression = GenericExclusionFilter(
            request.Framework ?? request.Workspace.Framework,
            request.ExcludeTraits);
        var units = GenericSelectionUnits(request);
        if (request.WholeSuite || units.Count == 0)
        {
            // A whole-suite run, or nothing selectable: vstest runs whatever the exclusion filter leaves,
            // exactly as before. The request's ids still ride along so an outright launch failure is
            // reported against them.
            return
            [
                BuildGenericInvocation(
                    request,
                    paths,
                    targetPath,
                    runHash,
                    part: null,
                    filter: exclusionExpression,
                    selectedTestCaseIds: request.TestCaseIds),
            ];
        }

        var chunks = CtArgvChunking.Chunk(
            units,
            static unit => GenericFilterTermCost(unit.Term),
            maxUnits: GenericMaxTermsPerInvocation,
            maxBytes: GenericSelectionBudget(paths, targetPath, runHash, exclusionExpression));
        var invocations = new List<GenericInvocation>(chunks.Count);
        for (var index = 0; index < chunks.Count; index++)
        {
            var chunk = chunks[index];
            var attributed = chunk
                .SelectMany(static unit => unit.TestCaseIds)
                .ToHashSet(StringComparer.Ordinal);

            // The FIRST invocation also answers for ids that composed no filter term. They never reached
            // an argv, so no chunk selected them, and dropping them would lose them from a failed run.
            if (index == 0)
            {
                foreach (var testCaseId in UnselectableTestCaseIds(request))
                    attributed.Add(testCaseId);
            }

            invocations.Add(BuildGenericInvocation(
                request,
                paths,
                targetPath,
                runHash,
                part: chunks.Count == 1 ? null : index,
                filter: ComposeGenericFilter(chunk, exclusionExpression),
                // Request order with duplicates intact: a one-invocation run must report exactly the
                // rows it reported before chunking existed.
                selectedTestCaseIds: request.TestCaseIds.Where(attributed.Contains).ToArray()));
        }

        return invocations;
    }

    private GenericInvocation BuildGenericInvocation(
        ContinuousTestProviderRunRequest request,
        CtGenerationPaths paths,
        string targetPath,
        string runHash,
        int? part,
        string? filter,
        IReadOnlyList<string> selectedTestCaseIds)
    {
        var resultArtifactPath = TrxResultArtifactPath(paths, runHash, part);
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
        else if (filter is not null)
        {
            args.Add("--filter");
            args.Add(filter);
        }

        return new GenericInvocation(
            new TestProcessCommand(
                _dotnetPath,
                args,
                request.Workspace.WorkspaceRoot,
                WorkspaceEnvironment(request.Workspace, paths)),
            resultArtifactPath,
            selectedTestCaseIds);
    }

    /// <summary>
    /// The selection as chunkable units, in request order and deduplicated by term exactly as the
    /// single-expression composition did, so a selection that fits one command line still composes the
    /// same string it always did.
    /// </summary>
    private static IReadOnlyList<GenericSelectionUnit> GenericSelectionUnits(
        ContinuousTestProviderRunRequest request)
    {
        var framework = GenericFramework(request.Framework ?? request.Workspace.Framework);
        var idsByTerm = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var termsInRequestOrder = new List<string>();
        foreach (var testCaseId in request.TestCaseIds)
        {
            var selector = GenericSelectorFromTestCaseId(testCaseId);
            if (string.IsNullOrWhiteSpace(selector))
                continue;

            var term = GenericFilterTerm(framework, selector);
            if (!idsByTerm.TryGetValue(term, out var testCaseIds))
            {
                testCaseIds = [];
                idsByTerm.Add(term, testCaseIds);
                termsInRequestOrder.Add(term);
            }

            testCaseIds.Add(testCaseId);
        }

        return termsInRequestOrder
            .Select(term => new GenericSelectionUnit(term, idsByTerm[term]))
            .ToArray();
    }

    /// <summary>
    /// Request ids that compose no filter term, because their selector names nothing vstest can match.
    /// They never reach an argv, so no chunk selects them; the first invocation answers for them so a
    /// failed run still reports them instead of dropping them.
    /// </summary>
    private static IEnumerable<string> UnselectableTestCaseIds(ContinuousTestProviderRunRequest request) =>
        request.TestCaseIds.Where(static testCaseId =>
            string.IsNullOrWhiteSpace(GenericSelectorFromTestCaseId(testCaseId)));

    /// <summary>
    /// What one more term costs the composed expression: its UTF-8 bytes plus the single <c>|</c> that
    /// joins it to the previous term. Charging the separator to every term rather than to all but the
    /// first overstates a chunk by one byte, which is the safe direction to be wrong in.
    /// </summary>
    private static int GenericFilterTermCost(string term) => Encoding.UTF8.GetByteCount(term) + 1;

    /// <summary>
    /// Bytes one invocation may spend on its WHOLE command line. This provider launches a real
    /// executable — <c>dotnet</c>, never a <c>.cmd</c> shim — so the bound is the 32,767-character
    /// Windows cap, not the 8,191 <c>cmd.exe</c> cap that <see cref="CtArgvChunking"/>'s shared default
    /// holds back for shim-launched runners. The headroom under the cap covers the quoting the launcher
    /// adds around an argument that carries spaces or quotes, and the byte count is measured in UTF-8,
    /// which is never shorter than the character count the cap applies to.
    ///
    /// Spending the shim-sized default here split a workspace-scope selection of Miller's own ~7,400
    /// tests into ~155 invocations. vstest re-discovers the WHOLE assembly on every invocation before it
    /// applies the filter, so that run breached the coordinator's 30-minute provider timeout and threw
    /// away every finished chunk's verdicts.
    /// </summary>
    private const int GenericCommandLineBudget = 30_000;

    /// <summary>
    /// Terms per invocation. The generic runner spends the whole selection in ONE argv element, so a
    /// count of terms says nothing about how much command line an invocation uses: the byte budget above
    /// is the only bound that can see a 600 KB single argument, and a unit count must not bind before it.
    /// </summary>
    private const int GenericMaxTermsPerInvocation = int.MaxValue;

    /// <summary>
    /// Bytes one invocation may spend on the selection expression: the per-invocation command-line
    /// budget minus everything else riding on the same command line — the <c>dotnet</c> path, the target
    /// assembly, the results directory, the TRX logger, the <c>--filter</c> flag itself, and the
    /// exclusion clause plus the <c>(…)&amp;</c> wrapper that every chunk repeats. Measuring the bound
    /// against the selection alone would let a long generation path push the finished command line back
    /// over the cap the chunking exists to respect.
    /// </summary>
    private int GenericSelectionBudget(
        CtGenerationPaths paths,
        string targetPath,
        string runHash,
        string? exclusionExpression)
    {
        // Part 0's name, which is the LONGER form: a chunked run adds the ".partNNN" suffix that a
        // single-invocation run does not carry.
        var resultArtifactPath = TrxResultArtifactPath(paths, runHash, part: 0);
        var fixedArgv = new List<string>
        {
            _dotnetPath,
            "test",
            targetPath,
            "--nologo",
            "--results-directory",
            paths.ResultsDirectory,
            "--logger",
            $"trx;LogFileName={Path.GetFileName(resultArtifactPath)}",
            "--filter",
        };
        if (exclusionExpression is not null)
            fixedArgv.Add("()&" + exclusionExpression);

        // Chunk() rejects a non-positive bound, and a single over-long term still gets its own
        // invocation rather than being dropped, so clamping here degrades to one term per command
        // instead of failing a run outright.
        return Math.Max(1, GenericCommandLineBudget - CtArgvChunking.ArgvCost(fixedArgv));
    }

    private static string ComposeGenericFilter(
        IReadOnlyList<GenericSelectionUnit> units,
        string? exclusionExpression)
    {
        var selectionExpression = string.Join("|", units.Select(static unit => unit.Term));
        return exclusionExpression is null
            ? selectionExpression
            : $"({selectionExpression})&{exclusionExpression}";
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

        var escaped = VsTestFilterValue.Escape(selector);
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
            .Select(value => $"{property}!={VsTestFilterValue.Escape(value)}")
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

    /// <summary>
    /// The jUnit artifact for one invocation. <paramref name="part"/> is null for a run that fits a
    /// single command line - which keeps the filename byte-identical to the pre-chunking one - and is
    /// the zero-based invocation index when a run is split, so chunk N cannot overwrite chunk N-1's
    /// results and every part stays on disk as evidence.
    /// </summary>
    private static string? XunitResultArtifactPath(
        ContinuousTestProviderRunRequest request,
        CtGenerationPaths paths,
        int? part = null)
    {
        if (string.IsNullOrWhiteSpace(request.RunId))
            return null;

        var runHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.RunId))).ToLowerInvariant();
        var suffix = part is null ? string.Empty : $".part{part.Value.ToString("D3", CultureInfo.InvariantCulture)}";
        return Path.Combine(paths.ResultsDirectory, $"run-{runHash}{suffix}.junit.xml");
    }

    /// <summary>
    /// Identifies the run one set of TRX artifacts belongs to. Without a <c>RunId</c> the key carries
    /// the current tick, so every caller must hash it ONCE and derive each part's name from that hash.
    /// </summary>
    private static string TrxRunHash(ContinuousTestProviderRunRequest request)
    {
        var runKey = request.RunId ?? CanonicalRunKey(request);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(runKey))).ToLowerInvariant();
    }

    /// <summary>
    /// The TRX artifact for one invocation. <paramref name="part"/> is null for a run that fits a
    /// single command line — which keeps the filename byte-identical to the pre-chunking one — and is
    /// the zero-based invocation index when a run is split, so chunk N cannot overwrite chunk N-1's
    /// results and every part stays on disk as evidence.
    /// </summary>
    private static string TrxResultArtifactPath(CtGenerationPaths paths, string runHash, int? part)
    {
        var suffix = part is null ? string.Empty : $".part{part.Value.ToString("D3", CultureInfo.InvariantCulture)}";
        return Path.Combine(paths.ResultsDirectory, $"run-{runHash}{suffix}.trx");
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
            paths.GenerationRoot,
            "-p:ArtifactsBinOutputName=out",
            "-p:CreateHardLinksForCopyFilesToOutputDirectoryIfPossible=true",
            "-p:CreateHardLinksForCopyAdditionalFilesIfPossible=true",
            "-p:CreateHardLinksForCopyLocalIfPossible=true",
            "-p:CreateHardLinksForAdditionalFilesIfPossible=true",
            "-nr:false",
            $"-p:OutDir={paths.OutDir}",
            "-p:GenerateProjectSpecificOutputFolder=true",
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

        DeduplicateOutputFiles(paths.OutDir);
    }

    private static void DeduplicateOutputFiles(string outputDirectory)
    {
        if (!Directory.Exists(outputDirectory))
            return;

        var canonicalByIdentity = new Dictionary<(long Length, string Hash), string>();
        foreach (var path in Directory.EnumerateFiles(outputDirectory, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, PathStringComparer))
        {
            if (!IsRuntimeTreeFile(outputDirectory, path))
                continue;

            var info = new FileInfo(path);
            var identity = (info.Length, FileIdentityHash(path));
            if (!canonicalByIdentity.TryGetValue(identity, out var canonical))
            {
                canonicalByIdentity.Add(identity, path);
                continue;
            }

            TryReplaceWithHardLink(path, canonical);
        }
    }

    private static string FileIdentityHash(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool IsRuntimeTreeFile(string outputDirectory, string path)
    {
        var relative = Path.GetRelativePath(outputDirectory, path);
        return relative
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .Any(static component => string.Equals(component, ".tools", StringComparison.Ordinal)
                || string.Equals(component, "runtimes", StringComparison.Ordinal));
    }

    private static void TryReplaceWithHardLink(string path, string canonical)
    {
        var temporaryPath = path + ".miller-link-" + Guid.NewGuid().ToString("N");
        try
        {
            if (!CreateHardLink(temporaryPath, canonical))
                return;

            var backupPath = path + ".miller-original-" + Guid.NewGuid().ToString("N");
            File.Move(path, backupPath);
            try
            {
                File.Move(temporaryPath, path);
                File.Delete(backupPath);
            }
            catch
            {
                DeleteTemporaryLink(path);
                File.Move(backupPath, path);
                throw;
            }
        }
        catch (IOException)
        {
            DeleteTemporaryLink(temporaryPath);
        }
        catch (UnauthorizedAccessException)
        {
            DeleteTemporaryLink(temporaryPath);
        }
        catch (PlatformNotSupportedException)
        {
            DeleteTemporaryLink(temporaryPath);
        }
        catch (DllNotFoundException)
        {
            DeleteTemporaryLink(temporaryPath);
        }
        catch (EntryPointNotFoundException)
        {
            DeleteTemporaryLink(temporaryPath);
        }
    }

    private static bool CreateHardLink(string path, string canonical)
    {
        if (OperatingSystem.IsWindows())
            return CreateHardLinkWindows(path, canonical, IntPtr.Zero);

        return LinkUnix(canonical, path) == 0;
    }

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkWindows(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);

    [DllImport("libc", EntryPoint = "link", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int LinkUnix(string existingPath, string newPath);

    private static void DeleteTemporaryLink(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
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
                "-p:GenerateProjectSpecificOutputFolder=true",
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
        var projectOutputDirectory = ProjectOutputDirectory(paths, workspace.ProjectPath);
        var relativePath = Path.GetRelativePath(Path.GetFullPath(projectOutputDirectory), targetPath);
        if (Path.IsPathRooted(relativePath)
            || relativePath == ".."
            || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ContinuousTestProviderException(
                $"Evaluated test TargetPath '{targetPath}' is outside CT project output '{projectOutputDirectory}'.");
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
        string framework,
        string workspaceRoot)
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
                        || !identities.Add(
                            fullyQualifiedName + "\u0000" + (OptionalString(row, "DisplayName") ?? fullyQualifiedName)))
                        continue;
                    var displayName = OptionalString(row, "DisplayName") ?? fullyQualifiedName;
                    var (className, methodName) = SplitDiagnosticQualifiedName(
                        fullyQualifiedName,
                        displayName);
                    var sourcePath = NormalizeSourcePath(
                        OptionalString(row, "SourcePath") ?? OptionalString(row, "SourceFile")
                        ?? OptionalString(row, "CodeFilePath"),
                        workspaceRoot);
                    var symbolName = sourcePath is null ? null : OptionalString(row, "SymbolName") ?? methodName;
                    var symbolPath = sourcePath is null
                        ? OptionalString(row, "SymbolPath")
                        : OptionalString(row, "SymbolPath") ?? sourcePath;
                    cases.Add(new ProviderTestCase(
                        Id: GenericTestCaseId(framework, fullyQualifiedName),
                        DisplayName: displayName,
                        FullyQualifiedName: fullyQualifiedName,
                        Selector: fullyQualifiedName,
                        Framework: framework,
                        SourcePath: sourcePath,
                        Metadata: new Dictionary<string, object?>
                        {
                            ["class"] = className,
                            ["method"] = methodName,
                            ["selector_kind"] = "FullyQualifiedName",
                        },
                        SymbolName: symbolName,
                        SymbolPath: symbolPath));
                }
            }

            return cases
                .GroupBy(testCase => testCase.FullyQualifiedName, StringComparer.Ordinal)
                .SelectMany(group => group.Count() == 1
                    ? group
                    : group.Select(testCase => testCase with
                    {
                        Id = GenericTestCaseId(framework, testCase.FullyQualifiedName, testCase.DisplayName),
                    }))
                .ToArray();
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

    private static string? NormalizeSourcePath(string? path, string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path, workspaceRoot);
        }
        catch (ArgumentException)
        {
            return null;
        }

        string relative = Path.GetRelativePath(Path.GetFullPath(workspaceRoot), fullPath);
        if (relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            return null;
        }

        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>
    /// One part's parsed TRX: the verdicts it produced, and the run-level error vstest recorded when it
    /// produced none. The caller decides what an empty part means, because only the caller knows whether
    /// its sibling parts answered for the rest of the selection.
    /// </summary>
    private readonly record struct TrxParseResult(ProviderRunResult Run, string? RunError);

    private static TrxParseResult ParseTrxRun(
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
            .GroupBy(GenericSelectorFromTestCaseId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var selectedIdsByDisplayName = request.TestCaseIds
            .Select(id => (Id: id, DisplayName: GenericDisplayNameFromTestCaseId(id)))
            .Where(row => row.DisplayName is not null)
            .GroupBy(row => row.DisplayName!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(row => row.Id).ToArray(), StringComparer.Ordinal);
        var framework = GenericFramework(request.Framework ?? request.Workspace.Framework) ?? "dotnet";
        var caseResults = new List<ProviderCaseResult>();

        foreach (var row in root.Descendants(ns + "UnitTestResult"))
        {
            var displayName = row.Attribute("testName")?.Value;
            var testName = displayName;
            var testDefinitionId = row.Attribute("testId")?.Value;
            if (testDefinitionId is not null
                && testNamesByDefinitionId.TryGetValue(testDefinitionId, out var definitionName))
                testName = definitionName;
            if (string.IsNullOrWhiteSpace(testName))
                continue;

            var selector = testName;
            string[]? candidates = selectedIdsBySelector.GetValueOrDefault(selector);
            candidates ??= selectedIdsByDisplayName.GetValueOrDefault(displayName ?? string.Empty);
            var testCaseId = candidates?.FirstOrDefault(id =>
                    string.Equals(GenericDisplayNameFromTestCaseId(id), displayName, StringComparison.Ordinal)
                    || string.Equals(GenericDisplayNameFromTestCaseId(id), testName, StringComparison.Ordinal))
                ?? (candidates is { Length: 1 } ? candidates[0]
                    : request.TestCaseIds.Count == 1 ? request.TestCaseIds[0] : GenericTestCaseId(framework, selector));
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

        var runInfoError = root
            .Descendants(ns + "RunInfo")
            .Select(row => row.Element(ns + "Text")?.Value.Trim())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (runInfoError is not null)
        {
            for (var index = 0; index < caseResults.Count; index++)
            {
                var row = caseResults[index];
                if (string.Equals(row.Status, "failed", StringComparison.Ordinal))
                    caseResults[index] = row with
                    {
                        FailureSummary = FoldRunError(row.FailureSummary, runInfoError),
                    };
            }
        }

        // Reported, never thrown. A part that matched nothing is a fact about THAT part's slice of the
        // selection; whether it fails the run depends on what the other parts produced.
        var runError = caseResults.Count > 0 ? null : runInfoError;

        var times = root.Element(ns + "Times");
        var startedAt = TrxDateTimeOffset(times?.Attribute("start")?.Value);
        var endedAt = TrxDateTimeOffset(times?.Attribute("finish")?.Value);
        var runId = request.RunId ?? $"trx:{root.Attribute("id")?.Value ?? Path.GetFileNameWithoutExtension(artifactPath)}";
        return new TrxParseResult(
            new ProviderRunResult(
                RunId: runId,
                Status: AggregateStatus(caseResults.Select(row => row.Status)),
                StartedAt: startedAt,
                EndedAt: endedAt,
                CaseResults: caseResults),
            runError);
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

    /// <summary>
    /// Parses the invocations of one chunked xunit run and folds them back into the single result the
    /// caller asked for. A one-invocation run parses exactly as it did before chunking existed.
    /// </summary>
    private static ProviderRunResult MergeRuns(
        IReadOnlyList<TestProcessResult> results,
        string selectedRevision,
        string indexIdentity)
    {
        // The xunit contract carries the run's results on stdout, one JSONL event per line, and the parser
        // skips a line it cannot read. A capped stream would therefore drop cases in silence and could report
        // a red run as green, so a truncated invocation fails the run instead of being parsed.
        if (results.Count == 1)
            return ParseRun(
                results[0].RequireCompleteStandardOutput("The test run"), selectedRevision, indexIdentity);

        return MergeRunResults(results
            .Select(invocation => ParseRun(
                invocation.RequireCompleteStandardOutput("The test run"), selectedRevision, indexIdentity))
            .ToArray());
    }

    /// <summary>
    /// Folds the parsed invocations of one chunked run into the single result the caller asked for.
    /// Shared by both runners: xunit chunks arrive as parsed stdout, mstest/nunit chunks as parsed TRX
    /// artifacts, and one merge policy keeps the two verdicts comparable.
    ///
    /// Status takes the worst outcome across invocations - a green chunk must never mask a red sibling,
    /// so a failed or errored part, or a single failed row inside one, fails the run. "skipped" is NOT
    /// folded the same way: it is the run's verdict only when NO part ran a test that did anything else.
    /// Treating it as worse than "passed" let one chunk of all-skipped methods report a whole run as
    /// skipped while its siblings really ran and passed, which is a chunking artefact - the unchunked
    /// run of the same selection reported "passed". The window spans min start to max end, because the
    /// run really did last that long.
    /// </summary>
    private static ProviderRunResult MergeRunResults(IReadOnlyList<ProviderRunResult> parsed)
    {
        if (parsed.Count == 1)
            return parsed[0];

        var caseResults = parsed.SelectMany(static run => run.CaseResults).ToArray();
        var displayNames = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var run in parsed)
        {
            foreach (var displayName in run.TestDisplayNames)
                displayNames.Add(displayName);
        }

        var status =
            parsed.Any(static run => run.Status is "failed" or "errored")
                || caseResults.Any(static row => row.Status is "failed" or "errored") ? "failed"
            : caseResults.Any(static row => row.Status != "skipped") ? "passed"
            : parsed.Any(static run => run.Status == "skipped") ? "skipped"
            : parsed[0].Status;

        return new ProviderRunResult(
            parsed[0].RunId,
            status,
            parsed.Select(static run => run.StartedAt).Where(static at => at is not null).Min(),
            parsed.Select(static run => run.EndedAt).Where(static at => at is not null).Max(),
            caseResults,
            TestDisplayNames: displayNames.ToArray());
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
            var sourcePath = OptionalString(row, "SourcePath") ?? OptionalString(row, "SourceFile");
            var symbolName = sourcePath is null ? null : OptionalString(row, "SymbolName") ?? OptionalString(row, "Method");
            var symbolPath = sourcePath is null
                ? OptionalString(row, "SymbolPath")
                : OptionalString(row, "SymbolPath") ?? sourcePath;

            cases.Add(new ProviderTestCase(
                Id: XunitTestCaseId(displayName),
                DisplayName: displayName,
                FullyQualifiedName: displayName,
                Selector: $"-method {XunitMethodName(displayName)}",
                Framework: "xunit",
                SourcePath: sourcePath,
                Metadata: metadata,
                SymbolName: symbolName,
                SymbolPath: symbolPath));
        }

        return cases;
    }

    /// <summary>
    /// Makes xunit v3 enumerate every theory ROW, in discovery AND in a run.
    ///
    /// <para><b>Why both.</b> Without it, <c>-list</c> reports one entry per test METHOD, so a theory with
    /// twelve rows counted as one case. Measured on Miller's own suite: 6,233 discovered against 7,723 run —
    /// a gap of 1,490 that looked like tests CT could not see. It also collapses results: a delay-enumerated
    /// theory emits ONE <c>test-case-starting</c> whose display name has no arguments and one
    /// <c>TestCaseUniqueID</c> shared by every row, so twelve rows folded into one verdict. Pre-enumerated,
    /// each row gets its own case with its arguments in the display name, which is exactly the identity
    /// <see cref="XunitTestCaseId"/> already derives.</para>
    ///
    /// <para>Setting it on only ONE of the two commands would be worse than neither: discovery would record
    /// row ids that a run could never report a result for, and every row would stay unproven forever.</para>
    ///
    /// <para>Cost measured on that same suite: about 100 ms on a 400 ms discovery. The argv does not grow —
    /// <see cref="XunitSelectionUnits"/> already collapses a method's rows to one <c>-method</c> unit. A
    /// theory whose data cannot be enumerated up front stays delay-enumerated, and therefore stays one case
    /// in both commands, which is consistent rather than wrong.</para>
    /// </summary>
    private const string PreEnumerateTheories = "-preEnumerateTheories";

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

    private static string GenericTestCaseId(
        string framework,
        string fullyQualifiedName,
        string? displayName = null) =>
        displayName is null || string.Equals(displayName, fullyQualifiedName, StringComparison.Ordinal)
            ? $"{framework}:{fullyQualifiedName}"
            : $"{framework}:{fullyQualifiedName}::display={displayName}";

    private static string GenericSelectorFromTestCaseId(string testCaseId)
    {
        string selector = testCaseId.StartsWith("mstest:", StringComparison.Ordinal)
            ? testCaseId["mstest:".Length..]
            : testCaseId.StartsWith("nunit:", StringComparison.Ordinal)
                ? testCaseId["nunit:".Length..]
                : testCaseId;
        int displayMarker = selector.IndexOf("::display=", StringComparison.Ordinal);
        return displayMarker >= 0 ? selector[..displayMarker] : selector;
    }

    private static string? GenericDisplayNameFromTestCaseId(string testCaseId)
    {
        int displayMarker = testCaseId.IndexOf("::display=", StringComparison.Ordinal);
        return displayMarker >= 0 ? testCaseId[(displayMarker + "::display=".Length)..] : null;
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

    /// <summary>
    /// The first non-empty Message, widened with the first error-shaped line found anywhere else in the
    /// result's Message/StackTrace text. An NUnit OneTimeSetUp failure carries only its banner in the
    /// Message ("OneTimeSetUp: dotnet failed.") while the actual cause sits in the StackTrace; without
    /// the widened capture the store can never surface the real error.
    /// </summary>
    private static string? TrxFailureSummary(XElement result, XNamespace ns)
    {
        var message = result
            .Descendants(ns + "Message")
            .Select(element => element.Value.Trim())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        string[] messageLines = message is null ? [] : FailureSummaryText.Lines(message);
        var detail = result
            .Descendants()
            .Where(element => element.Name == ns + "Message" || element.Name == ns + "StackTrace")
            .SelectMany(element => FailureSummaryText.Lines(element.Value))
            .FirstOrDefault(line =>
                FailureSummaryText.IsErrorShapedLine(line)
                && !messageLines.Contains(line, StringComparer.Ordinal));
        if (message is null)
            return detail;
        return detail is null ? message : message + "\n" + detail;
    }

    /// <summary>
    /// Appends the run-level error's first error-shaped line to a failed case's summary. vstest records
    /// environment-level causes (a failed pre-run build, a crashed host) as RunInfo text that no case
    /// result carries; a red row that hides that text reads as a test bug instead of a broken run.
    /// </summary>
    private static string? FoldRunError(string? failureSummary, string runInfoError)
    {
        var detail = FailureSummaryText.FirstErrorShapedLine(runInfoError)
            ?? FailureSummaryText.Lines(runInfoError).FirstOrDefault();
        if (detail is null)
            return failureSummary;
        if (failureSummary is null)
            return detail;
        return FailureSummaryText.Lines(failureSummary).Contains(detail, StringComparer.Ordinal)
            ? failureSummary
            : failureSummary + "\n" + detail;
    }

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

        foreach (var assembly in InstrumentableAssemblies(paths, workspace.ProjectPath))
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
        DeleteWithRetry(readinessPath);
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
            // Best effort, and it must STAY best effort: this delete runs while a readiness failure is
            // in flight, and a throw here would replace the coverage error the caller needs with an
            // IOException about a temp file. On Windows whether the handle is still held is decided by
            // an antivirus scan window, so the swap would happen on some runs and not others.
            TryDeleteWithRetry(readinessPath);
        }
    }

    /// <summary>
    /// Deletes a CT artifact, retrying the sharing races a delete loses on Windows: an antivirus scan
    /// or a not-yet-closed collector handle keeps the file open for a few milliseconds and the delete
    /// fails with an IOException that the same call a moment later does not see. Miller.Indexing runs
    /// the same loop for the rebuild promote, but it is private to that assembly and unreachable here.
    /// A delete that never succeeds still throws — a caller that needs the file gone must hear it.
    /// </summary>
    private void DeleteWithRetry(string path) => DeleteWithRetry(path, _deleteFile, _deleteRetrySleep);

    /// <summary>
    /// A delete that CANNOT throw, for a <c>finally</c> block: it swallows the sharing failures
    /// <c>DeleteWithRetry</c> gives up on, and only those, so a leftover temp file never replaces the
    /// failure that sent control through the block.
    /// </summary>
    private void TryDeleteWithRetry(string path) => TryDeleteWithRetry(path, _deleteFile, _deleteRetrySleep);

    internal static void TryDeleteWithRetry(string path, Action<string> deleteFile, Action<TimeSpan> sleep)
    {
        try
        {
            DeleteWithRetry(path, deleteFile, sleep);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal static void DeleteWithRetry(string path, Action<string> deleteFile, Action<TimeSpan> sleep)
    {
        ArgumentNullException.ThrowIfNull(deleteFile);
        ArgumentNullException.ThrowIfNull(sleep);

        if (!File.Exists(path))
            return;

        // The budget is spent in the injected sleep, not read from a clock, so the number of attempts
        // is the same on a loaded machine as on an idle one - and a test can drive the whole loop
        // without waiting half a second.
        var waited = TimeSpan.Zero;
        var delay = DeleteRetryInitialDelay;
        for (;;)
        {
            try
            {
                deleteFile(path);
                return;
            }
            catch (Exception exception) when (
                (exception is IOException or UnauthorizedAccessException)
                && waited + delay <= DeleteRetryBudget)
            {
                sleep(delay);
                waited += delay;
                delay = TimeSpan.FromMilliseconds(
                    Math.Min(delay.TotalMilliseconds * 2, DeleteRetryMaxDelay.TotalMilliseconds));
            }
        }
    }

    /// <remarks>
    /// The test assembly is instrumented alongside the product assemblies on purpose: B-3 narrows by
    /// intersecting changed files with a test's covered set, so a test that does not cover its own
    /// source file would never be selected when that file changes.
    /// </remarks>
    private static IReadOnlyList<string> InstrumentableAssemblies(
        CtGenerationPaths paths,
        string projectPath)
    {
        var outputRoot = Path.GetDirectoryName(ProjectOutputDirectory(paths, projectPath))!;
        var assemblies = Directory
            .EnumerateFiles(outputRoot, "*.dll", SearchOption.AllDirectories)
            .Where(path => File.Exists(Path.ChangeExtension(path, ".pdb")))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (assemblies.Length == 0)
            throw new ContinuousTestProviderException(
                $"Per-test coverage found no instrumentable assemblies (a '*.dll' with a sibling '*.pdb') " +
                $"under '{outputRoot}'.");

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

    private IReadOnlyList<ProviderCoverageArtifact> CompactCoverageSnapshots(
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
            DeleteWithRetry(snapshotPath);
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

        DeleteWithRetry(coverage.SessionArtifactPath);

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

        IReadOnlyDictionary<string, object?> metadata = TestingJson.Object(value);
        return metadata as ReadOnlyDictionary<string, object?>
            ?? new ReadOnlyDictionary<string, object?>(
                new Dictionary<string, object?>(metadata, StringComparer.Ordinal));
    }
}
