using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Miller.Testing.Parsing;

namespace Miller.Testing;

/// <summary>
/// The cargo/Rust CT provider. Discovery enumerates workspace members from <c>cargo metadata</c> and
/// per-target <c>-- --list</c>, emitting target-scoped per-test cases keyed by <see cref="RustTestCaseId"/>;
/// runs group the requested IDs by (package, target) and issue one <c>cargo test</c> invocation per
/// group (full target unfiltered / partial via <c>--exact</c>, chunked past the Windows argv cap),
/// with honest degradation tiers. Legacy slice-3 aggregate IDs (which no longer parse) route through a
/// single <c>cargo test --workspace</c> fallback so a targeted enqueue racing the inventory upgrade
/// still yields an honest result. The custom <c>--command</c> path (nextest) stays a single aggregate.
/// </summary>
public sealed class RustTestProvider : IContinuousTestProvider
{
    private const string ProjectSelector = "Cargo.toml";

    // Windows argv cap (first-class): a partial group whose --exact filter list exceeds either bound
    // is chunked into multiple invocations of the same target — never dropped, never widened to an
    // unfiltered superset (whose extra results could not be safely committed and would waste minutes).
    internal const int MaxFiltersPerInvocation = 120;
    internal const int MaxFilterBytesPerInvocation = 16 * 1024;

    private readonly ITestProcessRunner _runner;

    /// <summary>
    /// Lets the run reuse the generation the discovery before it built, instead of building the same source
    /// state into a second empty output directory. One per provider instance, keyed by project build root.
    /// </summary>
    private readonly CtGenerationHandoff _generations = new();

    public RustTestProvider(ITestProcessRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
    }

    // ---------------------------------------------------------------- discovery

    public async Task<IReadOnlyList<ProviderTestCase>> DiscoverAsync(
        ContinuousTestWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        EnsureCargo(workspace);

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

    private async Task<IReadOnlyList<ProviderTestCase>> DiscoverInGenerationAsync(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths,
        CancellationToken cancellationToken)
    {
        var projectRoot = ProjectRoot(workspace);
        var manifestPath = Path.Combine(projectRoot, ProjectSelector);
        EnsureGenerationDirectories(paths);

        // 1. cargo metadata — enumerate workspace members + targets (keyed off test/doctest booleans).
        var metaResult = await _runner.RunAsync(MetadataCommand(workspace, paths, manifestPath), cancellationToken)
            .ConfigureAwait(false);
        if (metaResult.ExitCode != 0)
            throw new ContinuousTestProviderException(DiscoveryFailureReason(metaResult, "cargo metadata"));
        var metadata = CargoMetadata.Parse(metaResult.RequireCompleteStandardOutput("cargo metadata"));

        // 2. explicit build gate — a compile failure here feeds RecordDiscoveryFailure (self-recovers).
        var buildResult = await _runner.RunAsync(BuildGateCommand(workspace, paths, manifestPath), cancellationToken)
            .ConfigureAwait(false);
        if (buildResult.ExitCode != 0)
            throw new ContinuousTestProviderException(DiscoveryFailureReason(buildResult, "cargo test --no-run"));

        // 3 + 4. per test-capable target: --list; per package with doctests: one doc aggregate.
        var cases = new List<ProviderTestCase>();
        foreach (var package in metadata.WorkspaceMembers)
        {
            foreach (var target in package.TestCapableTargets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var listResult = await _runner
                    .RunAsync(ListCommand(workspace, paths, manifestPath, package.Name, target.SelectorArgs()), cancellationToken)
                    .ConfigureAwait(false);
                var testNames = CargoTestList.ParseTestNames(listResult.RequireCompleteStandardOutput("The cargo test listing"));
                if (testNames.Count > 0)
                {
                    foreach (var name in testNames)
                        cases.Add(PerTestCase(package, target, name));
                }
                else
                {
                    // Un-enumerable (harness = false custom main, or a target with no libtest tests):
                    // one aggregate whole-target case keeps whole-surface coverage without a false per-test.
                    cases.Add(WholeTargetCase(package, target));
                }
            }

            if (package.HasDoctests)
                cases.Add(DocCase(package));
        }

        return cases.OrderBy(row => row.Id, StringComparer.Ordinal).ToArray();
    }

    private static ProviderTestCase PerTestCase(CargoPackage package, CargoTarget target, string testName) =>
        CaseFor(
            RustTestCaseId.ForTest(package.Name, target.SelectorKind!, target.Name, testName),
            package,
            targetName: target.Name,
            testName: testName,
            displayName: testName,
            fullyQualifiedName: $"{package.Name}::{target.Name}::{testName}",
            kind: "rust-per-test");

    private static ProviderTestCase WholeTargetCase(CargoPackage package, CargoTarget target) =>
        CaseFor(
            RustTestCaseId.ForWholeTarget(package.Name, target.SelectorKind!, target.Name),
            package,
            targetName: target.Name,
            testName: null,
            displayName: $"{target.Name} (whole target)",
            fullyQualifiedName: $"{package.Name}::{target.Name}",
            kind: "rust-target-aggregate");

    private static ProviderTestCase DocCase(CargoPackage package)
    {
        var id = RustTestCaseId.ForDoc(package.Name);
        return new ProviderTestCase(
            Id: id.Encode(),
            DisplayName: $"{package.Name} doc-tests",
            FullyQualifiedName: $"{package.Name}::doc",
            Selector: id.Encode(),
            Framework: "cargo",
            SourcePath: package.PackageRoot,
            Metadata: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["kind"] = "rust-doc-tests",
                ["package"] = package.Name,
                ["target_kind"] = RustTestCaseId.DocKind,
                ["target_name"] = null,
                ["test_name"] = null,
                ["manifest_path"] = package.ManifestPath,
            });
    }

    private static ProviderTestCase CaseFor(
        RustTestCaseId id,
        CargoPackage package,
        string targetName,
        string? testName,
        string displayName,
        string fullyQualifiedName,
        string kind)
    {
        var encoded = id.Encode();
        return new ProviderTestCase(
            Id: encoded,
            DisplayName: displayName,
            FullyQualifiedName: fullyQualifiedName,
            Selector: encoded,
            Framework: "cargo",
            // SourcePath is the package root dir — the granularity the impact selector narrows on
            // (crates/julie-index/** → julie-index cases), NOT a per-test file.
            SourcePath: package.PackageRoot,
            Metadata: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["kind"] = kind,
                ["package"] = package.Name,
                ["target_kind"] = id.Kind,
                ["target_name"] = targetName,
                ["test_name"] = testName,
                ["manifest_path"] = package.ManifestPath,
            });
    }

    // ---------------------------------------------------------------- run

    public async Task<ProviderRunResult> RunAsync(
        ContinuousTestProviderRunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureCargo(request.Workspace);

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

    private async Task<ProviderRunResult> RunInGenerationAsync(
        ContinuousTestProviderRunRequest request,
        CtGenerationPaths paths,
        CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var runId = request.RunId ?? NewRunId(request);
        var artifactPath = ResultArtifactPath(paths, runId);
        ResetArtifact(artifactPath);

        var projectRoot = ProjectRoot(request.Workspace);
        var manifestPath = Path.Combine(projectRoot, ProjectSelector);
        EnsureGenerationDirectories(paths);

        // GUARD. Without a custom command this provider's plan IS the id list: no ids and no whole-suite
        // flag means no cargo process would start, and an empty result set reads as "passed". A run must
        // never return empty-and-passed, so an unrunnable request fails loudly instead (finding F6).
        if (request.TestCaseIds.Count == 0
            && !request.WholeSuite
            && string.IsNullOrWhiteSpace(request.Command ?? request.Workspace.Command))
        {
            throw new ContinuousTestProviderException(
                "cargo run request selected no test cases, carried no whole-suite flag and named no custom " +
                "command, so it would execute nothing.");
        }

        var results = new List<ProviderCaseResult>();

        async Task<(TestProcessResult Result, double Wall)> RunAndLog(TestProcessCommand command)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stopwatch = Stopwatch.StartNew();
            var processResult = await _runner.RunAsync(command, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            // Append each invocation's log as it completes, so the trail exists even if a later
            // invocation or the completion transaction throws (F4 through the coordinator catch path).
            AppendInvocationLog(artifactPath, command, processResult, stopwatch.Elapsed.TotalSeconds);
            return (processResult, stopwatch.Elapsed.TotalSeconds);
        }

        if (request.CoverageMode == ContinuousTestCoverageMode.PerTest)
        {
            return await RunPerTestCoverageAsync(
                    request, paths, runId, started, manifestPath, artifactPath, RunAndLog, cancellationToken)
                .ConfigureAwait(false);
        }

        // Custom command (nextest, etc.): unchanged single aggregate project case.
        if (!string.IsNullOrWhiteSpace(request.Command ?? request.Workspace.Command))
        {
            var (customResult, customWall) = await RunAndLog(BuildCustomCommand(request, paths)).ConfigureAwait(false);
            var customOutput = CargoTestOutput.Parse(customResult.RequireCompleteStandardOutput("The cargo test run"));
            ThrowIfHarnessCrash(customResult, customOutput, artifactPath);
            foreach (var id in request.TestCaseIds)
                results.Add(AggregateResult(request, runId, id, customResult, customOutput));
            return RunResult(request, paths, runId, started, results, artifactPath);
        }

        var parseable = new List<RunCase>();
        var legacy = new List<string>();
        foreach (var id in request.TestCaseIds)
        {
            if (RustTestCaseId.TryParse(id, out var parsed))
                parseable.Add(new RunCase(id, parsed));
            else
                legacy.Add(id);
        }

        // Legacy fallback: every un-parseable rust-test: ID (rust-test:Cargo.toml, rust-test:tests/*.rs)
        // groups into ONE `cargo test --workspace` invocation mapped back to those IDs — an honest
        // exit-code verdict for a targeted enqueue that raced the inventory upgrade.
        if (legacy.Count > 0)
        {
            var (legacyResult, legacyWall) = await RunAndLog(WorkspaceCommand(request.Workspace, paths, manifestPath))
                .ConfigureAwait(false);
            var legacyOutput = CargoTestOutput.Parse(legacyResult.RequireCompleteStandardOutput("The cargo test run"));
            ThrowIfHarnessCrash(legacyResult, legacyOutput, artifactPath);
            foreach (var id in legacy)
                results.Add(AggregateResult(request, runId, id, legacyResult, legacyOutput));
        }

        // Grouped path: one build gate, then one sequential invocation per (package, target) group.
        if (parseable.Count > 0)
        {
            var (gate, _) = await RunAndLog(BuildGateCommand(request.Workspace, paths, manifestPath))
                .ConfigureAwait(false);
            if (gate.ExitCode != 0)
                throw new ContinuousTestProviderException(DiscoveryFailureReason(gate, "cargo test --no-run"))
                {
                    ResultArtifactPath = File.Exists(artifactPath) ? artifactPath : null,
                };

            foreach (var group in GroupCases(parseable))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await RunGroupAsync(request, paths, runId, group, manifestPath, artifactPath, results, RunAndLog)
                    .ConfigureAwait(false);
            }
        }

        return RunResult(request, paths, runId, started, results, artifactPath);
    }

    private async Task RunGroupAsync(
        ContinuousTestProviderRunRequest request,
        CtGenerationPaths paths,
        string runId,
        RunGroup group,
        string manifestPath,
        string artifactPath,
        List<ProviderCaseResult> results,
        Func<TestProcessCommand, Task<(TestProcessResult Result, double Wall)>> runAndLog)
    {
        var selector = group.Cases[0].Parsed.SelectorArgs();

        // Doc aggregate group: `cargo test -p <pkg> --doc`, exit-code status (package-level verdict).
        if (group.Cases[0].Parsed.IsDoc)
        {
            var (docResult, _) = await runAndLog(RunCommand(request.Workspace, paths, manifestPath, group.Package, selector, filters: null))
                .ConfigureAwait(false);
            var docOutput = CargoTestOutput.Parse(docResult.RequireCompleteStandardOutput("The cargo doc-test run"));
            ThrowIfHarnessCrash(docResult, docOutput, artifactPath);
            foreach (var runCase in group.Cases)
                results.Add(AggregateResult(request, runId, runCase.Id, docResult, docOutput));
            return;
        }

        var perTestNames = group.Cases.Where(c => c.Parsed.IsPerTest).Select(c => c.Parsed.TestName!).ToList();
        var hasWholeTarget = group.Cases.Any(c => c.Parsed.IsWholeTarget);

        // Full-target: an aggregate whole-target case runs the target unfiltered; any per-test cases
        // selected alongside it are attributed by name from the same output.
        //
        // A WHOLE-SUITE run takes the same path for every group. It already covers every case the target
        // holds, so `-- --exact <names…>` would only spell out the target's own inventory and chunk it
        // across extra processes (63 unfiltered invocations against 90 chunked ones on julie-extractors).
        // Attribution is unchanged: each per-test case is still matched by its libtest name.
        if (request.WholeSuite || hasWholeTarget)
        {
            var (result, _) = await runAndLog(RunCommand(request.Workspace, paths, manifestPath, group.Package, selector, filters: null))
                .ConfigureAwait(false);
            var output = CargoTestOutput.Parse(result.RequireCompleteStandardOutput("The cargo test run"));
            ThrowIfHarnessCrash(result, output, artifactPath);
            var requested = perTestNames.ToHashSet(StringComparer.Ordinal);
            var unrequested = output.ResultsByName.Keys.Count(name => !requested.Contains(name));
            foreach (var runCase in group.Cases)
            {
                if (runCase.Parsed.IsWholeTarget)
                    results.Add(AggregateResult(request, runId, runCase.Id, result, output, unrequested));
                else
                    EmitPerTest(request, runId, runCase, output, result, unrequested, results);
            }

            return;
        }

        // Partial group: `-- --exact <names…>`, chunked past the argv cap. Each chunk attributes only
        // its own filtered names.
        foreach (var chunk in ChunkFilters(perTestNames))
        {
            var (result, _) = await runAndLog(RunCommand(request.Workspace, paths, manifestPath, group.Package, selector, chunk))
                .ConfigureAwait(false);
            var output = CargoTestOutput.Parse(result.RequireCompleteStandardOutput("The cargo test run"));
            ThrowIfHarnessCrash(result, output, artifactPath);
            var chunkSet = chunk.ToHashSet(StringComparer.Ordinal);
            var unrequested = output.ResultsByName.Keys.Count(name => !chunkSet.Contains(name));
            foreach (var runCase in group.Cases.Where(c => c.Parsed.IsPerTest && chunkSet.Contains(c.Parsed.TestName!)))
                EmitPerTest(request, runId, runCase, output, result, unrequested, results);
        }
    }

    // Attributes a per-test case by its libtest name. A case with no parsed result line is NOT emitted
    // (tier-(b)): it keeps its running marker and is flipped to stale by MarkUnreportedRunCasesStale —
    // a selected case is never reported passed without a parsed line.
    private static void EmitPerTest(
        ContinuousTestProviderRunRequest request,
        string runId,
        RunCase runCase,
        CargoTestOutput output,
        TestProcessResult result,
        int unrequested,
        List<ProviderCaseResult> results)
    {
        if (!output.ResultsByName.TryGetValue(runCase.Parsed.TestName!, out var status))
            return;

        var failureSummary = string.Equals(status, "failed", StringComparison.Ordinal)
            ? output.FailureSummaryFor(runCase.Parsed.TestName!) ?? $"{runCase.Parsed.TestName} failed"
            : null;

        results.Add(new ProviderCaseResult(
            Id: StableId("test_result", request.Workspace.WorkspaceId, runCase.Id, runId),
            TestCaseId: runCase.Id,
            Status: status,
            ResultRevision: request.SelectedRevision,
            IndexIdentity: request.IndexIdentity,
            DurationSeconds: output.SummaryTotal == 1 ? output.TargetDurationSeconds : null,
            FailureSummary: failureSummary,
            Metadata: CaseMetadata(result.ExitCode, output, unrequested)));
    }

    private static ProviderCaseResult AggregateResult(
        ContinuousTestProviderRunRequest request,
        string runId,
        string testCaseId,
        TestProcessResult result,
        CargoTestOutput output,
        int unrequested = 0)
    {
        var passed = result.ExitCode == 0;
        return new ProviderCaseResult(
            Id: StableId("test_result", request.Workspace.WorkspaceId, testCaseId, runId),
            TestCaseId: testCaseId,
            Status: passed ? "passed" : "failed",
            ResultRevision: request.SelectedRevision,
            IndexIdentity: request.IndexIdentity,
            DurationSeconds: output.SummaryTotal == 1 ? output.TargetDurationSeconds : null,
            FailureSummary: passed ? null : FailureSummary(output, result),
            Metadata: CaseMetadata(result.ExitCode, output, unrequested));
    }

    private static IReadOnlyDictionary<string, object?> CaseMetadata(int exitCode, CargoTestOutput output, int unrequested)
    {
        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["framework"] = "cargo",
            ["exit_code"] = exitCode,
            ["passed"] = output.Passed,
            ["failed"] = output.Failed,
            ["ignored"] = output.Ignored,
            ["target_duration_seconds"] = output.TargetDurationSeconds,
        };
        if (unrequested > 0)
            metadata["unrequested_results"] = unrequested;
        if (output.HasParseAnomaly)
            metadata["parse_anomaly"] = true;
        return metadata;
    }

    private ProviderRunResult RunResult(
        ContinuousTestProviderRunRequest request,
        CtGenerationPaths paths,
        string runId,
        DateTimeOffset started,
        IReadOnlyList<ProviderCaseResult> results,
        string artifactPath,
        IReadOnlyList<ProviderCoverageArtifact>? coverageArtifacts = null) =>
        new(
            RunId: runId,
            Status: RunStatus(results),
            StartedAt: started,
            EndedAt: DateTimeOffset.UtcNow,
            CaseResults: results,
            ResultArtifactPath: File.Exists(artifactPath) ? artifactPath : null,
            CoverageArtifacts: coverageArtifacts ?? DiscoverCoverageArtifacts(paths))
        {
            GenerationId = paths.GenerationId,
        };

    /// <summary>
    /// A single representative run command for the request — the C1/C2/C6 conformance subject and a
    /// unit-test seam, built against the latest existing generation (or the would-be first). Production
    /// runs never use it: <see cref="RunAsync"/> allocates its own generation and builds every command,
    /// artifact and temp path from that one handle.
    /// </summary>
    public TestProcessCommand BuildRunCommand(ContinuousTestProviderRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureCargo(request.Workspace);

        return BuildRunCommand(request, CtGenerationPaths.ResolveLatestOrFirst(request.Workspace));
    }

    private TestProcessCommand BuildRunCommand(ContinuousTestProviderRunRequest request, CtGenerationPaths paths)
    {
        var projectRoot = ProjectRoot(request.Workspace);
        var manifestPath = Path.Combine(projectRoot, ProjectSelector);
        EnsureGenerationDirectories(paths);

        if (!string.IsNullOrWhiteSpace(request.Command ?? request.Workspace.Command))
            return BuildCustomCommand(request, paths);

        var parseable = new List<RunCase>();
        foreach (var id in request.TestCaseIds)
        {
            if (RustTestCaseId.TryParse(id, out var parsed))
                parseable.Add(new RunCase(id, parsed));
        }

        if (parseable.Count == 0)
            return WorkspaceCommand(request.Workspace, paths, manifestPath);

        var group = GroupCases(parseable)[0];
        var selector = group.Cases[0].Parsed.SelectorArgs();
        if (group.Cases[0].Parsed.IsDoc || group.Cases.Any(c => c.Parsed.IsWholeTarget))
            return RunCommand(request.Workspace, paths, manifestPath, group.Package, selector, filters: null);

        var filters = group.Cases.Where(c => c.Parsed.IsPerTest).Select(c => c.Parsed.TestName!).ToList();
        return RunCommand(request.Workspace, paths, manifestPath, group.Package, selector, filters);
    }

    public static bool IsRustProjectFile(string path) =>
        string.Equals(Path.GetFileName(path), ProjectSelector, StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- command builders

    private TestProcessCommand MetadataCommand(
        ContinuousTestWorkspace workspace, CtGenerationPaths paths, string manifestPath) =>
        new(
            FileName: "cargo",
            Arguments: ["metadata", "--no-deps", "--format-version", "1", "--manifest-path", manifestPath],
            WorkingDirectory: ProjectRoot(workspace),
            Environment: WorkspaceEnvironment(workspace, paths));

    private TestProcessCommand BuildGateCommand(
        ContinuousTestWorkspace workspace, CtGenerationPaths paths, string manifestPath) =>
        new(
            FileName: "cargo",
            Arguments:
            [
                "test", "--no-run", "--workspace", "--manifest-path", manifestPath, "--target-dir", TargetDir(paths),
            ],
            WorkingDirectory: ProjectRoot(workspace),
            Environment: WorkspaceEnvironment(workspace, paths));

    private TestProcessCommand ListCommand(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths,
        string manifestPath,
        string package,
        IReadOnlyList<string> selector)
    {
        var args = new List<string> { "test", "-p", package };
        args.AddRange(selector);
        args.Add("--manifest-path");
        args.Add(manifestPath);
        args.Add("--target-dir");
        args.Add(TargetDir(paths));
        args.Add("--");
        args.Add("--list");
        args.Add("--format");
        args.Add("terse");
        return new("cargo", args, ProjectRoot(workspace), WorkspaceEnvironment(workspace, paths));
    }

    private TestProcessCommand RunCommand(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths,
        string manifestPath,
        string package,
        IReadOnlyList<string> selector,
        IReadOnlyList<string>? filters)
    {
        var args = new List<string> { "test", "-p", package };
        args.AddRange(selector);
        args.Add("--manifest-path");
        args.Add(manifestPath);
        args.Add("--target-dir");
        args.Add(TargetDir(paths));
        args.Add("--no-fail-fast");
        if (filters is { Count: > 0 })
        {
            args.Add("--");
            args.Add("--exact");
            args.AddRange(filters);
        }

        return new("cargo", args, ProjectRoot(workspace), WorkspaceEnvironment(workspace, paths));
    }

    private TestProcessCommand WorkspaceCommand(
        ContinuousTestWorkspace workspace, CtGenerationPaths paths, string manifestPath) =>
        new(
            FileName: "cargo",
            Arguments:
            [
                "test", "--manifest-path", manifestPath, "--target-dir", TargetDir(paths), "--no-fail-fast", "--workspace",
            ],
            WorkingDirectory: ProjectRoot(workspace),
            Environment: WorkspaceEnvironment(workspace, paths));

    private TestProcessCommand BuildCustomCommand(ContinuousTestProviderRunRequest request, CtGenerationPaths paths)
    {
        var projectRoot = ProjectRoot(request.Workspace);
        var manifestPath = Path.Combine(projectRoot, ProjectSelector);
        EnsureGenerationDirectories(paths);

        var parts = SplitCommand((request.Command ?? request.Workspace.Command)!);
        if (parts.Count == 0)
            throw new ContinuousTestProviderException("Rust test command cannot be empty.");

        var args = parts.Skip(1).ToList();
        if (!ContainsOption(args, "--manifest-path"))
        {
            args.Add("--manifest-path");
            args.Add(manifestPath);
        }

        if (!ContainsOption(args, "--target-dir"))
        {
            args.Add("--target-dir");
            args.Add(TargetDir(paths));
        }

        return new(parts[0], args, projectRoot, WorkspaceEnvironment(request.Workspace, paths));
    }

    // ---------------------------------------------------------------- grouping / chunking

    /// <summary>
    /// Splits a partial group's filter names into invocations bounded by the Windows argv cap. Never
    /// drops a filter (a single over-long name still gets its own chunk).
    /// </summary>
    internal static IReadOnlyList<IReadOnlyList<string>> ChunkFilters(IReadOnlyList<string> names)
    {
        var chunks = new List<IReadOnlyList<string>>();
        var current = new List<string>();
        var bytes = 0;
        foreach (var name in names)
        {
            var cost = Encoding.UTF8.GetByteCount(name) + 1;
            if (current.Count > 0
                && (current.Count >= MaxFiltersPerInvocation || bytes + cost > MaxFilterBytesPerInvocation))
            {
                chunks.Add(current);
                current = [];
                bytes = 0;
            }

            current.Add(name);
            bytes += cost;
        }

        if (current.Count > 0)
            chunks.Add(current);

        return chunks;
    }

    private static IReadOnlyList<RunGroup> GroupCases(IEnumerable<RunCase> cases)
    {
        var groups = new List<RunGroup>();
        var index = new Dictionary<(string, string, string?), RunGroup>();
        foreach (var runCase in cases)
        {
            var key = runCase.Parsed.GroupKey();
            if (!index.TryGetValue(key, out var group))
            {
                group = new RunGroup(key.Package, []);
                index[key] = group;
                groups.Add(group);
            }

            group.Cases.Add(runCase);
        }

        return groups;
    }

    private sealed record RunCase(string Id, RustTestCaseId Parsed);

    private sealed record RunGroup(string Package, List<RunCase> Cases);

    // ---------------------------------------------------------------- degradation / summaries

    private static void ThrowIfHarnessCrash(TestProcessResult result, CargoTestOutput output, string artifactPath)
    {
        // Tier-(a): a nonzero exit with NO parsed libtest output at all (compile/link/harness crash) is
        // not a test failure — throw so the run lands visible-stale-with-reason, artifact attached.
        if (result.ExitCode != 0
            && !output.HasTestResultLine
            && output.Passed == 0
            && output.Failed == 0
            && output.Ignored == 0)
        {
            throw new ContinuousTestProviderException(FailureSummary(output, result))
            {
                ResultArtifactPath = File.Exists(artifactPath) ? artifactPath : null,
            };
        }
    }

    // Honest FailureSummary (F5): parsed libtest failures → first rustc `^error(\[|:)`/panic line →
    // exit-code text. Never build-progress noise.
    private static string FailureSummary(CargoTestOutput output, TestProcessResult result)
    {
        var parsed = output.RunFailureSummary();
        if (parsed is not null && !CargoTestOutput.IsBuildProgressNoise(parsed))
            return parsed;

        var errorLine = CargoTestOutput.FirstErrorLine(result.StandardError);
        if (errorLine is not null && !CargoTestOutput.IsBuildProgressNoise(errorLine))
            return errorLine;

        return $"cargo test failed with exit code {result.ExitCode}.";
    }

    private static string DiscoveryFailureReason(TestProcessResult result, string phase)
    {
        var errorLine = CargoTestOutput.FirstErrorLine(result.StandardError);
        if (errorLine is not null)
            return errorLine;

        var output = CargoTestOutput.Parse(result.StandardOutput);
        var parsed = output.RunFailureSummary();
        if (parsed is not null && !CargoTestOutput.IsBuildProgressNoise(parsed))
            return parsed;

        return $"{phase} failed with exit code {result.ExitCode}.";
    }

    private static string RunStatus(IReadOnlyList<ProviderCaseResult> results) =>
        results.Count == 0 || results.All(row => string.Equals(row.Status, "passed", StringComparison.OrdinalIgnoreCase))
            ? "passed"
            : "failed";

    // ---------------------------------------------------------------- per-test coverage

    internal const string InstrumentCoverageFlag = "-C instrument-coverage";

    private const string ProfileFileVariable = "LLVM_PROFILE_FILE";
    private const string CoverageParser = "covfiles";

    /// <summary>
    /// One instrumented run: an instrumented build gate, then one process per selected test with its
    /// profiles redirected into a per-test directory, then an off-hot-path llvm export compacted to a
    /// workspace-relative file list. Every command runs BelowNormal — the maintenance lane yields the
    /// machine to foreground work.
    /// </summary>
    private async Task<ProviderRunResult> RunPerTestCoverageAsync(
        ContinuousTestProviderRunRequest request,
        CtGenerationPaths paths,
        string runId,
        DateTimeOffset started,
        string manifestPath,
        string artifactPath,
        Func<TestProcessCommand, Task<(TestProcessResult Result, double Wall)>> runAndLog,
        CancellationToken cancellationToken)
    {
        var cases = CoverageCases(request);
        var workspace = request.Workspace;

        async Task<TestProcessResult> RunTool(TestProcessCommand command)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var toolResult = await _runner.RunAsync(command, cancellationToken).ConfigureAwait(false);
            AppendToolLog(artifactPath, command, toolResult);
            return toolResult;
        }

        var tools = await ResolveLlvmToolsAsync(workspace, paths, RunTool).ConfigureAwait(false);

        var metadataResult = await RunTool(Lowered(MetadataCommand(workspace, paths, manifestPath))).ConfigureAwait(false);
        if (metadataResult.ExitCode != 0)
            throw new ContinuousTestProviderException(DiscoveryFailureReason(metadataResult, "cargo metadata"))
            {
                ResultArtifactPath = File.Exists(artifactPath) ? artifactPath : null,
            };

        var packagesByManifest = CargoMetadata.Parse(metadataResult.RequireCompleteStandardOutput("cargo metadata")).WorkspaceMembers
            .ToDictionary(package => Path.GetFullPath(package.ManifestPath), package => package.Name, PathStringComparer);

        var buildProfileDir = Path.Combine(CoverageRoot(paths), "build");
        var (gate, _) = await runAndLog(
                InstrumentedBuildCommand(workspace, paths, manifestPath, buildProfileDir))
            .ConfigureAwait(false);
        if (gate.ExitCode != 0)
            throw new ContinuousTestProviderException(DiscoveryFailureReason(gate, "cargo test --no-run"))
            {
                ResultArtifactPath = File.Exists(artifactPath) ? artifactPath : null,
            };

        var executables = TestExecutables(gate.RequireCompleteStandardOutput("The cargo build"), packagesByManifest);

        var results = new List<ProviderCaseResult>();
        var artifacts = new List<ProviderCoverageArtifact>();
        foreach (var runCase in cases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var digest = CoverageDigest(runCase.Id);
            var profileDir = Path.Combine(CoverageRoot(paths), digest);

            var (result, _) = await runAndLog(
                    InstrumentedRunCommand(workspace, paths, manifestPath, runCase.Parsed, profileDir))
                .ConfigureAwait(false);
            var output = CargoTestOutput.Parse(result.RequireCompleteStandardOutput("The cargo test run"));
            ThrowIfHarnessCrash(result, output, artifactPath);
            if (!output.ResultsByName.ContainsKey(runCase.Parsed.TestName!))
            {
                throw new ContinuousTestProviderException(
                    $"Rust per-test coverage did not emit the exact selected test '{runCase.Parsed.TestName}'.")
                {
                    ResultArtifactPath = File.Exists(artifactPath) ? artifactPath : null,
                };
            }

            var unrequested = output.ResultsByName.Keys
                .Count(name => !string.Equals(name, runCase.Parsed.TestName, StringComparison.Ordinal));
            EmitPerTest(request, runId, runCase, output, result, unrequested, results);

            executables.TryGetValue(runCase.Parsed.GroupKey(), out var executable);
            var files = await ExportCoverageAsync(workspace, tools, profileDir, executable, RunTool).ConfigureAwait(false);
            artifacts.Add(WriteCoverageArtifact(paths, runCase.Id, digest, files));
            DeleteDirectory(profileDir);
        }

        DeleteDirectory(buildProfileDir);
        return RunResult(request, paths, runId, started, results, artifactPath, artifacts);
    }

    /// <summary>
    /// The per-test cases an instrumented run can attribute. Anything else — an aggregate id, a legacy
    /// id, a custom test command — is a capability failure, never a silently downgraded whole-target map.
    /// </summary>
    private static IReadOnlyList<RunCase> CoverageCases(ContinuousTestProviderRunRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Command ?? request.Workspace.Command))
            throw UnsupportedCoverage("a custom test command");

        var cases = new List<RunCase>();
        foreach (var id in request.TestCaseIds)
        {
            if (!RustTestCaseId.TryParse(id, out var parsed) || !parsed.IsPerTest)
                throw UnsupportedCoverage($"test case '{id}'");
            cases.Add(new RunCase(id, parsed));
        }

        if (cases.Count == 0)
            throw UnsupportedCoverage("an empty test selection");

        return cases;
    }

    private async Task<LlvmTools> ResolveLlvmToolsAsync(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths,
        Func<TestProcessCommand, Task<TestProcessResult>> runTool)
    {
        // llvm-profdata/llvm-cov must come from the same toolchain as the compiler that wrote the
        // profiles (raw-profile format versions are not compatible across LLVM releases), so they are
        // resolved from rustc's own sysroot — never from PATH.
        var libDirResult = await runTool(
                Lowered(new TestProcessCommand(
                    "rustc",
                    ["--print", "target-libdir"],
                    ProjectRoot(workspace),
                    WorkspaceEnvironment(workspace, paths))))
            .ConfigureAwait(false);

        var libDir = libDirResult.RequireCompleteStandardOutput("The rustc target-libdir probe").Trim();
        if (libDirResult.ExitCode != 0 || string.IsNullOrWhiteSpace(libDir))
            throw UnsupportedCoverage("this toolchain (rustc --print target-libdir failed)");

        var binDir = Path.GetFullPath(Path.Combine(libDir, "..", "bin"));
        var suffix = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        var profdata = Path.Combine(binDir, $"llvm-profdata{suffix}");
        var cov = Path.Combine(binDir, $"llvm-cov{suffix}");
        if (!File.Exists(profdata) || !File.Exists(cov))
            throw UnsupportedCoverage("this toolchain (run `rustup component add llvm-tools`)");

        return new LlvmTools(profdata, cov);
    }

    /// <summary>
    /// Merges the test's raw profiles and exports its hit-set, returning the workspace-relative files
    /// the test covered. Any missing input or failing tool yields an empty list — recorded as an
    /// incomplete map by the caller, never as a complete one.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ExportCoverageAsync(
        ContinuousTestWorkspace workspace,
        LlvmTools tools,
        string profileDir,
        string? executable,
        Func<TestProcessCommand, Task<TestProcessResult>> runTool)
    {
        if (executable is null || !Directory.Exists(profileDir))
            return [];

        var profiles = Directory.GetFiles(profileDir, "*.profraw");
        if (profiles.Length == 0)
            return [];

        Array.Sort(profiles, StringComparer.Ordinal);
        var merged = Path.Combine(profileDir, "merged.profdata");
        var mergeArgs = new List<string> { "merge", "-sparse", "-o", merged };
        mergeArgs.AddRange(profiles);
        var mergeResult = await runTool(
                Lowered(new TestProcessCommand(tools.Profdata, mergeArgs, profileDir)))
            .ConfigureAwait(false);
        if (mergeResult.ExitCode != 0)
            return [];

        var exportResult = await runTool(
                Lowered(new TestProcessCommand(
                    tools.Cov,
                    [
                        "export",
                        $"-instr-profile={merged}",
                        "-format=text",
                        "-summary-only",
                        "-object",
                        executable,
                    ],
                    profileDir)))
            .ConfigureAwait(false);

        return exportResult.ExitCode != 0
            ? []
            : CoveredWorkspaceFiles(
                exportResult.RequireCompleteStandardOutput("The coverage export"), workspace.WorkspaceRoot);
    }

    /// <summary>
    /// The workspace files an <c>llvm-cov export -summary-only</c> document reports as hit, compacted
    /// to sorted workspace-relative paths. Zero-hit files and files outside the workspace (registry
    /// dependencies, the standard library) are dropped.
    /// </summary>
    internal static IReadOnlyList<string> CoveredWorkspaceFiles(string? exportJson, string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(exportJson))
            return [];

        var root = Path.GetFullPath(workspaceRoot);
        var files = new SortedSet<string>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(exportJson);
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return [];

            foreach (var export in data.EnumerateArray())
            {
                if (!export.TryGetProperty("files", out var exportFiles) || exportFiles.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var file in exportFiles.EnumerateArray())
                {
                    if (!file.TryGetProperty("filename", out var filename)
                        || filename.ValueKind != JsonValueKind.String
                        || CoveredLines(file) <= 0)
                        continue;

                    var relative = WorkspaceRelativePath(root, filename.GetString()!);
                    if (relative is not null)
                        files.Add(relative);
                }
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return files.ToArray();
    }

    private static long CoveredLines(JsonElement file) =>
        file.TryGetProperty("summary", out var summary)
        && summary.TryGetProperty("lines", out var lines)
        && lines.TryGetProperty("covered", out var covered)
        && covered.ValueKind == JsonValueKind.Number
        && covered.TryGetInt64(out var value)
            ? value
            : 0;

    private static string? WorkspaceRelativePath(string workspaceRoot, string filename)
    {
        var relative = Path.GetRelativePath(workspaceRoot, Path.GetFullPath(filename));
        if (Path.IsPathRooted(relative)
            || relative.StartsWith("..", StringComparison.Ordinal)
            || string.Equals(relative, ".", StringComparison.Ordinal))
            return null;

        return relative
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private static ProviderCoverageArtifact WriteCoverageArtifact(
        CtGenerationPaths paths,
        string testCaseId,
        string digest,
        IReadOnlyList<string> files)
    {
        var path = Path.Combine(paths.ResultsDirectory, $"{digest}.{CoverageParser}");
        // LF-terminated on every platform: the file list is a parsed contract shared with the dotnet
        // provider's artifacts, not host-local text.
        File.WriteAllText(path, string.Concat(files.Select(file => file + "\n")));
        return new ProviderCoverageArtifact(
            ArtifactPath: path,
            Parser: CoverageParser,
            ArtifactRoot: paths.GenerationRoot,
            TestCaseId: testCaseId,
            GenerationId: paths.GenerationId,
            Complete: files.Count > 0);
    }

    /// <summary>
    /// Maps each instrumented test binary from the build gate's <c>--message-format=json</c> stream onto
    /// the (package, kind, target) key a case id parses to — the object llvm-cov needs to export a
    /// profile against.
    /// </summary>
    private static Dictionary<(string, string, string?), string> TestExecutables(
        string? buildOutput,
        IReadOnlyDictionary<string, string> packagesByManifest)
    {
        var executables = new Dictionary<(string, string, string?), string>();
        if (string.IsNullOrWhiteSpace(buildOutput))
            return executables;

        foreach (var line in buildOutput.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] != '{')
                continue;

            try
            {
                using var document = JsonDocument.Parse(trimmed);
                var root = document.RootElement;
                if (!root.TryGetProperty("reason", out var reason)
                    || reason.GetString() != "compiler-artifact"
                    || !root.TryGetProperty("executable", out var executable)
                    || executable.ValueKind != JsonValueKind.String
                    || !root.TryGetProperty("manifest_path", out var manifest)
                    || manifest.ValueKind != JsonValueKind.String
                    || !root.TryGetProperty("target", out var target))
                    continue;

                if (!packagesByManifest.TryGetValue(Path.GetFullPath(manifest.GetString()!), out var package))
                    continue;

                var kinds = target.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.Array
                    ? kind.EnumerateArray().Select(value => value.GetString() ?? string.Empty).ToArray()
                    : [];
                var name = target.TryGetProperty("name", out var targetName) ? targetName.GetString() : null;
                var selectorKind = new CargoTarget(name ?? string.Empty, kinds, IsTest: true, IsDoctest: false).SelectorKind;
                if (name is null || selectorKind is null)
                    continue;

                executables[(package, selectorKind, name)] = executable.GetString()!;
            }
            catch (JsonException)
            {
            }
        }

        return executables;
    }

    /// <summary>Lowercase hex SHA-256 of the test-case id, truncated to 24 chars (the repo's stable-id
    /// digest shape). Raw ids carry <c>:</c>, <c>::</c> and <c>/</c> — never safe as path segments.</summary>
    internal static string CoverageDigest(string testCaseId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(testCaseId))).ToLowerInvariant()[..24];

    private static string CoverageRoot(CtGenerationPaths paths) => Path.Combine(paths.GenerationRoot, "coverage");

    private TestProcessCommand InstrumentedBuildCommand(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths,
        string manifestPath,
        string profileDir) =>
        Lowered(new TestProcessCommand(
            FileName: "cargo",
            Arguments:
            [
                "test", "--no-run", "--workspace", "--manifest-path", manifestPath, "--target-dir", TargetDir(paths),
                "--message-format=json",
            ],
            WorkingDirectory: ProjectRoot(workspace),
            Environment: InstrumentedEnvironment(workspace, paths, profileDir)));

    private TestProcessCommand InstrumentedRunCommand(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths,
        string manifestPath,
        RustTestCaseId parsed,
        string profileDir)
    {
        var args = new List<string> { "test", "-p", parsed.Package };
        args.AddRange(parsed.SelectorArgs());
        args.Add("--manifest-path");
        args.Add(manifestPath);
        args.Add("--target-dir");
        args.Add(TargetDir(paths));
        args.Add("--no-fail-fast");
        args.Add("--");
        args.Add("--exact");
        args.Add(parsed.TestName!);
        args.Add("--test-threads=1");
        return Lowered(new TestProcessCommand(
            "cargo",
            args,
            ProjectRoot(workspace),
            InstrumentedEnvironment(workspace, paths, profileDir)));
    }

    private static IReadOnlyDictionary<string, string?> InstrumentedEnvironment(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths,
        string profileDir)
    {
        Directory.CreateDirectory(profileDir);
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var entry in WorkspaceEnvironment(workspace, paths))
            environment[entry.Key] = entry.Value;
        foreach (var entry in RustCoverageFlagPolicy.Create(ProjectRoot(workspace)))
            environment[entry.Key] = entry.Value;
        environment[ProfileFileVariable] = Path.Combine(profileDir, "%p.profraw");
        return environment;
    }

    private static TestProcessCommand Lowered(TestProcessCommand command) =>
        command with { ProcessPriority = ProcessPriorityClass.BelowNormal };

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static ContinuousTestProviderException UnsupportedCoverage(string subject) =>
        new($"Rust continuous test provider cannot produce per-test coverage for {subject}.");

    private sealed record LlvmTools(string Profdata, string Cov);

    // ---------------------------------------------------------------- coverage / artifact

    /// <summary>
    /// Coverage scan of the operation's own generation only — a cargo profile writes its lcov/cobertura
    /// under <c>CARGO_TARGET_DIR</c>, which is generation-scoped, so a run can never adopt an older
    /// generation's (or a stale project-root) coverage file and report it as this run's evidence.
    /// </summary>
    private static IReadOnlyList<ProviderCoverageArtifact> DiscoverCoverageArtifacts(CtGenerationPaths generation)
    {
        if (!Directory.Exists(generation.GenerationRoot))
            return [];

        var artifacts = new List<ProviderCoverageArtifact>();
        var paths = new HashSet<string>(PathStringComparer);
        AddCoverageArtifacts(artifacts, paths, generation.GenerationRoot, "lcov.info", "lcov");
        AddCoverageArtifacts(artifacts, paths, generation.GenerationRoot, "cobertura.xml", "cobertura");
        AddCoverageArtifacts(artifacts, paths, generation.GenerationRoot, "*.cobertura.xml", "cobertura");
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

            artifacts.Add(new ProviderCoverageArtifact(fullPath, parser, artifactRoot));
        }
    }

    // <GenerationRoot>/TestResults/run-<sha256hex(runId)>.cargo.log — mirrors the dotnet artifact
    // location/naming (only the extension differs). Appended per invocation with FileShare.Read.
    private static string ResultArtifactPath(CtGenerationPaths paths, string runId)
    {
        var runHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(runId))).ToLowerInvariant();
        return Path.Combine(paths.ResultsDirectory, $"run-{runHash}.cargo.log");
    }

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

    private static void AppendInvocationLog(
        string artifactPath,
        TestProcessCommand command,
        TestProcessResult result,
        double wallSeconds)
    {
        using var stream = new FileStream(artifactPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream);
        writer.WriteLine($"## {command.ToDisplayString()}");
        writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"exit code: {result.ExitCode}"));
        writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"wall seconds: {wallSeconds:F3}"));
        writer.WriteLine();
        writer.WriteLine("### stdout");
        writer.WriteLine(result.StandardOutput);
        writer.WriteLine("### stderr");
        writer.WriteLine(result.StandardError);
        writer.WriteLine();
    }

    /// <summary>
    /// Logs a coverage tool invocation without its stdout: an llvm-cov export document is the raw
    /// material for the compacted artifact, not run evidence, and would bloat the log by megabytes.
    /// </summary>
    private static void AppendToolLog(string artifactPath, TestProcessCommand command, TestProcessResult result)
    {
        using var stream = new FileStream(artifactPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream);
        writer.WriteLine($"## {command.ToDisplayString()}");
        writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"exit code: {result.ExitCode}"));
        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            writer.WriteLine("### stderr");
            writer.WriteLine(result.StandardError);
        }

        writer.WriteLine();
    }

    // ---------------------------------------------------------------- environment / paths

    private static void EnsureCargo(ContinuousTestWorkspace workspace)
    {
        var framework = RequiredFramework(workspace);
        if (!string.Equals(framework, "cargo", StringComparison.OrdinalIgnoreCase))
            throw UnsupportedFramework(framework, workspace.ProjectPath);
    }

    private static string NewRunId(ContinuousTestProviderRunRequest request) =>
        StableId(
            "ct_run",
            request.Workspace.WorkspaceId,
            request.Workspace.ProjectPath,
            request.SelectedRevision,
            string.Join("|", request.TestCaseIds));

    private static string RequiredFramework(ContinuousTestWorkspace workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace.Framework) || string.Equals(workspace.Framework, "rust", StringComparison.OrdinalIgnoreCase))
            return "cargo";

        return workspace.Framework!;
    }

    private static string ProjectRoot(ContinuousTestWorkspace workspace) =>
        IsRustProjectFile(workspace.ProjectPath)
            ? Path.GetDirectoryName(workspace.ProjectPath) ?? workspace.WorkspaceRoot
            : workspace.ProjectPath;

    /// <summary>
    /// Re-throws a provider failure carrying the generation the operation allocated. Stamping at the
    /// operation boundary — rather than at each throw site, several of which sit in static helpers — is
    /// the only placement that cannot miss a throw after allocation (mirrors the dotnet provider).
    /// </summary>
    private static ContinuousTestProviderException StampGeneration(
        ContinuousTestProviderException exception,
        CtGenerationPaths paths) =>
        new(exception.Message, exception)
        {
            GenerationId = paths.GenerationId,
            ResultArtifactPath = exception.ResultArtifactPath,
        };

    private static void EnsureGenerationDirectories(CtGenerationPaths paths)
    {
        paths.EnsureDirectories();
        Directory.CreateDirectory(TargetDir(paths));
    }

    // Cargo documents target/ as a shared internal cache with no cross-process stability contract, so
    // each generation gets its own: a dying test process can never collide with the next run's rebuild.
    private static string TargetDir(CtGenerationPaths paths) => Path.Combine(paths.GenerationRoot, "target");

    private static IReadOnlyDictionary<string, string?> WorkspaceEnvironment(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths)
    {
        Directory.CreateDirectory(paths.TempDirectory);
        return new Dictionary<string, string?>
        {
            [CtEnvironment.WorkspaceRoot] = workspace.WorkspaceRoot,
            // Removed, not merely unset: the test process inherits it from the daemon, and a `miller` CLI
            // verb run inside a test would bind the DAEMON's workspace. See DotnetTestProvider for the note.
            [CtEnvironment.DaemonWorkspaceRoot] = null,
            ["CARGO_TARGET_DIR"] = TargetDir(paths),
            // Parseable libtest lines must never be ANSI-wrapped.
            ["CARGO_TERM_COLOR"] = "never",
            ["TMPDIR"] = paths.TempDirectory,
            ["TMP"] = paths.TempDirectory,
            ["TEMP"] = paths.TempDirectory,
        };
    }

    private static bool ContainsOption(IReadOnlyList<string> args, string option) =>
        args.Any(arg =>
            string.Equals(arg, option, StringComparison.Ordinal)
            || arg.StartsWith(option + "=", StringComparison.Ordinal));

    private static IReadOnlyList<string> SplitCommand(string command)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var inSingle = false;
        var inDouble = false;
        var escaping = false;
        foreach (var ch in command)
        {
            if (escaping)
            {
                current.Append(ch);
                escaping = false;
                continue;
            }

            if (ch == '\\')
            {
                escaping = true;
                continue;
            }

            if (ch == '\'' && !inDouble)
            {
                inSingle = !inSingle;
                continue;
            }

            if (ch == '"' && !inSingle)
            {
                inDouble = !inDouble;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !inSingle && !inDouble)
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (escaping)
            current.Append('\\');

        if (current.Length > 0)
            parts.Add(current.ToString());

        return parts;
    }

    private static ContinuousTestProviderException UnsupportedFramework(string framework, string projectPath) =>
        new($"Rust continuous test provider does not support framework '{framework}' for '{projectPath}'. Expected cargo or rust.");


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
