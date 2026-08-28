using System.Collections.ObjectModel;
using System.Diagnostics;

namespace Miller.Testing;

/// <summary>
/// Coverage instrumentation shape for a run. <see cref="None"/> is the uninstrumented default every
/// foreground and backfill run uses; <see cref="PerTest"/> is the instrumented maintenance shape that
/// produces one coverage map per selected test case.
/// </summary>
public enum ContinuousTestCoverageMode
{
    None,
    PerTest,
}

public sealed class ContinuousTestProviderException : Exception
{
    public ContinuousTestProviderException(string message)
        : base(message)
    {
    }

    public ContinuousTestProviderException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public string? ResultArtifactPath { get; init; }

    public string? GenerationId { get; init; }
}

public sealed record TestProcessCommand
{
    public string FileName { get; init; }
    public IReadOnlyList<string> Arguments { get; init; }
    public string WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string?> Environment { get; init; }
    public ProcessPriorityClass? ProcessPriority { get; init; }

    public TestProcessCommand(
        string FileName,
        IReadOnlyList<string> Arguments,
        string WorkingDirectory,
        IReadOnlyDictionary<string, string?>? Environment = null,
        ProcessPriorityClass? ProcessPriority = null)
    {
        if (string.IsNullOrWhiteSpace(FileName)) throw new ArgumentException("must not be empty", nameof(FileName));
        if (string.IsNullOrWhiteSpace(WorkingDirectory))
            throw new ArgumentException("must not be empty", nameof(WorkingDirectory));
        if (ProcessPriority is { } priority && !Enum.IsDefined(priority))
            throw new ArgumentOutOfRangeException(nameof(ProcessPriority));

        this.FileName = FileName;
        this.Arguments = Arguments;
        this.WorkingDirectory = WorkingDirectory;
        this.Environment = Environment ?? new ReadOnlyDictionary<string, string?>(
            new Dictionary<string, string?>(StringComparer.Ordinal));
        this.ProcessPriority = ProcessPriority;
    }

    public string ToDisplayString() =>
        $"{FileName} {string.Join(" ", Arguments.Select(Quote))}";

    private static string Quote(string value) =>
        value.Contains(' ', StringComparison.Ordinal) ? $"\"{value}\"" : value;
}

/// <summary>
/// One finished child process, as its provider sees it. The two truncation flags say whether the runner's
/// per-stream capture cap elided part of that text; the retained text is a head plus a rolling tail, which is
/// everything a human-facing failure summary needs and NOT enough to parse results from.
/// </summary>
public sealed record TestProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool StandardOutputTruncated = false,
    bool StandardErrorTruncated = false)
{
    /// <summary>
    /// The standard output, guaranteed complete — the accessor every parser that reads RESULTS or an
    /// INVENTORY from stdout must use.
    ///
    /// <para>Both such parsers tolerate lines they do not recognise: the xunit JSONL path skips a line it
    /// cannot parse, and the cargo path ignores any line matching no pattern. So an elided middle would not
    /// fail — it would silently drop test cases, under-report failures, and could turn a red run green. A
    /// truncated stream is therefore refused here. Correctness beats memory: a loud failure an operator can
    /// act on is the honest outcome, and the cap is generous enough that a real run never reaches it.</para>
    /// </summary>
    /// <param name="context">What produced the output, named in the failure message.</param>
    public string RequireCompleteStandardOutput(string context)
    {
        if (!StandardOutputTruncated)
            return StandardOutput;

        throw new ContinuousTestProviderException(
            $"{context} wrote more standard output than the capture cap retains, so part of it was elided. "
            + "Results read from a partial stream would silently omit test cases, so the run fails instead. "
            + "Reduce the console output of the tests, or raise "
            + "TestProcessRunnerOptions.MaxCapturedCharactersPerStream.");
    }
}

public interface ITestProcessRunner
{
    Task<TestProcessResult> RunAsync(TestProcessCommand command, CancellationToken cancellationToken = default);
}

public interface ITestBackgroundProcessRunner
{
    ITestBackgroundProcess Start(TestProcessCommand command);
}

public interface ITestBackgroundProcess : IAsyncDisposable
{
    int ProcessId { get; }

    /// <summary>
    /// How long ago this process last produced output on stdout or stderr. It starts at zero when the
    /// process starts, so a child that has not spoken YET is not mistaken for one that has stopped
    /// speaking.
    ///
    /// <para>This is the stall signal, and it is deliberately not total elapsed time. A test suite is
    /// allowed to take an hour; it is not allowed to go silent for ten minutes. A total-duration cap would
    /// kill the slow suite and miss the wedged one.</para>
    ///
    /// <para>It is on the interface, not private to the owned process, so the stall POLICY can live in
    /// <c>TestProcessRunner.RunCoreAsync</c> beside the cancellation policy and be driven by the same test
    /// stub. A real child cannot be asked to wedge on demand.</para>
    /// </summary>
    TimeSpan SinceLastOutput { get; }

    Task<TestProcessResult> WaitForExitAsync(CancellationToken cancellationToken = default);

    void TerminateProcessTree();
}

public interface IContinuousTestProvider
{
    Task<IReadOnlyList<ProviderTestCase>> DiscoverAsync(
        ContinuousTestWorkspace workspace,
        CancellationToken cancellationToken = default);

    Task<ProviderRunResult> RunAsync(
        ContinuousTestProviderRunRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record ContinuousTestProviderChunkProgress(
    int RequestedUniqueUnitCount,
    int ChunkCount,
    int CurrentPart,
    int CurrentPartUnitCount,
    IReadOnlyList<string> NameSamples,
    string NameDigest,
    bool NamesTruncated);

public sealed record ContinuousTestWorkspace
{
    public string WorkspaceId { get; init; }
    public string WorkspaceRoot { get; init; }
    public string ProjectPath { get; init; }
    public string BuildOutputRoot { get; init; }
    public string? Framework { get; init; }
    public string? Command { get; init; }
    public IReadOnlyList<string> ExcludeTraits { get; init; }
    public IReadOnlyDictionary<string, object?> Metadata { get; init; }

    public ContinuousTestWorkspace(
        string WorkspaceId,
        string WorkspaceRoot,
        string ProjectPath,
        string BuildOutputRoot,
        string? Framework = null,
        string? Command = null,
        IReadOnlyList<string>? ExcludeTraits = null,
        IReadOnlyDictionary<string, object?>? Metadata = null)
    {
        if (string.IsNullOrWhiteSpace(WorkspaceId)) throw new ArgumentException("must not be empty", nameof(WorkspaceId));
        if (string.IsNullOrWhiteSpace(WorkspaceRoot)) throw new ArgumentException("must not be empty", nameof(WorkspaceRoot));
        if (string.IsNullOrWhiteSpace(ProjectPath)) throw new ArgumentException("must not be empty", nameof(ProjectPath));
        if (string.IsNullOrWhiteSpace(BuildOutputRoot))
            throw new ArgumentException("must not be empty", nameof(BuildOutputRoot));

        this.WorkspaceId = WorkspaceId;
        this.WorkspaceRoot = Path.GetFullPath(WorkspaceRoot);
        this.ProjectPath = Path.GetFullPath(ProjectPath);
        this.BuildOutputRoot = Path.GetFullPath(BuildOutputRoot);
        this.Framework = Framework;
        this.Command = Command;
        this.ExcludeTraits = ExcludeTraits ?? [];
        this.Metadata = Metadata ?? new ReadOnlyDictionary<string, object?>(
            new Dictionary<string, object?>(StringComparer.Ordinal));
    }
}

public sealed record ContinuousTestProviderRunRequest
{
    public ContinuousTestWorkspace Workspace { get; init; }
    public string? RunId { get; init; }
    public string SelectedRevision { get; init; }
    public string IndexIdentity { get; init; }
    public IReadOnlyList<string> TestCaseIds { get; init; }
    public IReadOnlyList<string> FilterArguments { get; init; }
    public string? Command { get; init; }
    public IReadOnlyList<string> ExcludeTraits { get; init; }
    public string? Framework { get; init; }
    public IReadOnlyDictionary<string, object?> Metadata { get; init; }
    public ContinuousTestCoverageMode CoverageMode { get; init; }
    public Action<ContinuousTestProviderChunkProgress>? Progress { get; init; }

    /// <summary>
    /// This run covers EVERY test case the store knows for the project, so the provider may run the whole
    /// assembly once instead of spending the selection on argv.
    ///
    /// <para><see cref="TestCaseIds"/> STILL carries the full list. An earlier shape said "whole suite" by
    /// handing the provider an EMPTY list, which every result-artifact provider read as "run everything" —
    /// but the cargo provider's run loop is driven by the id list itself, so an empty list started no
    /// process at all, reported "passed" over zero results, and left every case stale. A flag says the same
    /// thing without taking the plan away (2026-08-21 dogfood finding F6).</para>
    ///
    /// <para>It is set only when the selection covers everything. Running a whole assembly for three
    /// impacted tests is the same mistake in the other direction.</para>
    /// </summary>
    public bool WholeSuite { get; init; }

    public ContinuousTestProviderRunRequest(
        ContinuousTestWorkspace Workspace,
        string SelectedRevision,
        string IndexIdentity,
        string? RunId = null,
        IReadOnlyList<string>? TestCaseIds = null,
        IReadOnlyList<string>? FilterArguments = null,
        string? Command = null,
        IReadOnlyList<string>? ExcludeTraits = null,
        string? Framework = null,
        IReadOnlyDictionary<string, object?>? Metadata = null,
        ContinuousTestCoverageMode CoverageMode = ContinuousTestCoverageMode.None,
        bool WholeSuite = false,
        Action<ContinuousTestProviderChunkProgress>? Progress = null)
    {
        ArgumentNullException.ThrowIfNull(Workspace);
        if (string.IsNullOrWhiteSpace(SelectedRevision))
            throw new ArgumentException("must not be empty", nameof(SelectedRevision));
        if (string.IsNullOrWhiteSpace(IndexIdentity))
            throw new ArgumentException("must not be empty", nameof(IndexIdentity));

        this.Workspace = Workspace;
        this.RunId = RunId;
        this.SelectedRevision = SelectedRevision;
        this.IndexIdentity = IndexIdentity;
        this.TestCaseIds = TestCaseIds ?? [];
        this.FilterArguments = FilterArguments ?? [];
        this.Command = Command ?? Workspace.Command;
        this.ExcludeTraits = ExcludeTraits ?? Workspace.ExcludeTraits;
        this.Framework = Framework ?? Workspace.Framework;
        this.Metadata = Metadata ?? Workspace.Metadata;
        this.CoverageMode = CoverageMode;
        this.WholeSuite = WholeSuite;
        this.Progress = Progress;
    }
}

public sealed record ContinuousTestDiscoveryRequest
{
    public ContinuousTestWorkspace Workspace { get; init; }
    public string? ProviderSource { get; init; }

    public ContinuousTestDiscoveryRequest(
        ContinuousTestWorkspace Workspace,
        string? ProviderSource = null)
    {
        ArgumentNullException.ThrowIfNull(Workspace);
        if (ProviderSource is not null && string.IsNullOrWhiteSpace(ProviderSource))
            throw new ArgumentException("must not be empty", nameof(ProviderSource));
        this.Workspace = Workspace;
        this.ProviderSource = ProviderSource;
    }
}

public sealed record ContinuousTestDiscoveryResult(
    IReadOnlyList<ProviderTestCase> TestCases,
    IReadOnlyList<ContinuousTestStatus> Statuses);

public sealed record ContinuousTestCoordinatorRunRequest
{
    public ContinuousTestWorkspace Workspace { get; init; }
    public string SelectedRevision { get; init; }
    public string CurrentRevision { get; init; }
    public string IndexIdentity { get; init; }
    public IReadOnlyList<string> TestCaseIds { get; init; }
    public IReadOnlyList<string> FilterArguments { get; init; }
    public string? Command { get; init; }
    public IReadOnlyList<string> ExcludeTraits { get; init; }
    public string? Framework { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public Func<string>? CurrentRevisionResolver { get; init; }
    public string? RunId { get; init; }
    public ContinuousTestCoverageMode CoverageMode { get; init; }
    public Action<ContinuousTestProviderResolution>? ProviderResolved { get; init; }
    public Action<ContinuousTestProviderChunkProgress>? Progress { get; init; }

    /// <summary>
    /// This run covers EVERY test case the store knows for the project, so the provider is told it may run
    /// the whole assembly instead of spending the case list on argv.
    ///
    /// <para>Both express the same run. The difference is cost. A per-case selection becomes one
    /// <c>-method</c> pair per id, and Miller's own ~6,000 cases then exceed the command-line limit and split
    /// into roughly 50 processes, each paying host startup and discovery again: 6+ minutes for a subset that
    /// <c>dotnet test</c> runs in 25 seconds. One unfiltered run covers the same tests once, under the seeded
    /// trait exclusions.</para>
    ///
    /// <para><see cref="TestCaseIds"/> STILL carries the full list, and so does the provider request. Only
    /// the provider's argv changes. Losing the list would make a whole-suite run claim to have selected
    /// nothing, freshness at the composite key would go quietly wrong, and a provider whose run loop is
    /// driven by the list (cargo) would execute nothing at all.</para>
    ///
    /// <para>It is set only when the selection covers everything. A workspace-scope run whose already-fresh
    /// cases were dropped may be down to a handful of ids, and running a whole assembly for three tests is the
    /// same mistake in the other direction.</para>
    /// </summary>
    public bool WholeSuite { get; init; }

    public ContinuousTestCoordinatorRunRequest(
        ContinuousTestWorkspace Workspace,
        string SelectedRevision,
        string CurrentRevision,
        string IndexIdentity,
        IReadOnlyList<string> TestCaseIds,
        IReadOnlyList<string>? FilterArguments = null,
        string? Command = null,
        IReadOnlyList<string>? ExcludeTraits = null,
        string? Framework = null,
        DateTimeOffset? StartedAt = null,
        Func<string>? CurrentRevisionResolver = null,
        string? RunId = null,
        ContinuousTestCoverageMode CoverageMode = ContinuousTestCoverageMode.None,
        bool WholeSuite = false,
        Action<ContinuousTestProviderResolution>? ProviderResolved = null,
        Action<ContinuousTestProviderChunkProgress>? Progress = null)
    {
        ArgumentNullException.ThrowIfNull(Workspace);
        if (string.IsNullOrWhiteSpace(SelectedRevision))
            throw new ArgumentException("must not be empty", nameof(SelectedRevision));
        if (string.IsNullOrWhiteSpace(CurrentRevision))
            throw new ArgumentException("must not be empty", nameof(CurrentRevision));
        if (string.IsNullOrWhiteSpace(IndexIdentity))
            throw new ArgumentException("must not be empty", nameof(IndexIdentity));
        ArgumentNullException.ThrowIfNull(TestCaseIds);
        if (TestCaseIds.Count == 0)
            throw new ArgumentException("must contain at least one test case id", nameof(TestCaseIds));
        if (RunId is not null && string.IsNullOrWhiteSpace(RunId))
            throw new ArgumentException("must not be blank when supplied", nameof(RunId));

        this.Workspace = Workspace;
        this.SelectedRevision = SelectedRevision;
        this.CurrentRevision = CurrentRevision;
        this.IndexIdentity = IndexIdentity;
        this.TestCaseIds = TestCaseIds;
        this.FilterArguments = FilterArguments ?? [];
        this.Command = Command ?? Workspace.Command;
        this.ExcludeTraits = ExcludeTraits ?? Workspace.ExcludeTraits;
        this.Framework = Framework ?? Workspace.Framework;
        this.StartedAt = StartedAt;
        this.CurrentRevisionResolver = CurrentRevisionResolver;
        this.RunId = RunId;
        this.CoverageMode = CoverageMode;
        this.WholeSuite = WholeSuite;
        this.ProviderResolved = ProviderResolved;
        this.Progress = Progress;
    }
}

public sealed record ContinuousTestCoordinatorRunResult(
    ProviderRunResult ProviderResult,
    IReadOnlyList<ContinuousTestStatus> Statuses,
    string? ProviderSource = null);

public sealed record ProviderTestCase
{
    public string Id { get; init; }
    public string DisplayName { get; init; }
    public string FullyQualifiedName { get; init; }
    public string Selector { get; init; }
    public string? Framework { get; init; }
    public string? SourcePath { get; init; }
    public IReadOnlyDictionary<string, object?> Metadata { get; init; }
    public string? SymbolName { get; init; }
    public string? SymbolPath { get; init; }

    public ProviderTestCase(
        string Id,
        string DisplayName,
        string FullyQualifiedName,
        string Selector,
        string? Framework = null,
        string? SourcePath = null,
        IReadOnlyDictionary<string, object?>? Metadata = null,
        string? SymbolName = null,
        string? SymbolPath = null)
    {
        if (string.IsNullOrWhiteSpace(Id)) throw new ArgumentException("must not be empty", nameof(Id));
        if (string.IsNullOrWhiteSpace(DisplayName))
            throw new ArgumentException("must not be empty", nameof(DisplayName));
        if (string.IsNullOrWhiteSpace(FullyQualifiedName))
            throw new ArgumentException("must not be empty", nameof(FullyQualifiedName));
        if (string.IsNullOrWhiteSpace(Selector)) throw new ArgumentException("must not be empty", nameof(Selector));

        this.Id = Id;
        this.DisplayName = DisplayName;
        this.FullyQualifiedName = FullyQualifiedName;
        this.Selector = Selector;
        this.Framework = Framework;
        this.SourcePath = SourcePath;
        this.Metadata = Metadata ?? new ReadOnlyDictionary<string, object?>(
            new Dictionary<string, object?>(StringComparer.Ordinal));
        this.SymbolName = SymbolName;
        this.SymbolPath = SymbolPath;
    }
}

public sealed record ProviderCaseResult
{
    public string Id { get; init; }
    public string TestCaseId { get; init; }
    public string Status { get; init; }
    public string ResultRevision { get; init; }
    public string IndexIdentity { get; init; }
    public double? DurationSeconds { get; init; }
    public string? FailureSummary { get; init; }
    public IReadOnlyDictionary<string, object?> Metadata { get; init; }

    public ProviderCaseResult(
        string Id,
        string TestCaseId,
        string Status,
        string ResultRevision,
        string IndexIdentity,
        double? DurationSeconds = null,
        string? FailureSummary = null,
        IReadOnlyDictionary<string, object?>? Metadata = null)
    {
        if (string.IsNullOrWhiteSpace(Id)) throw new ArgumentException("must not be empty", nameof(Id));
        if (string.IsNullOrWhiteSpace(TestCaseId)) throw new ArgumentException("must not be empty", nameof(TestCaseId));
        if (string.IsNullOrWhiteSpace(Status)) throw new ArgumentException("must not be empty", nameof(Status));
        if (string.IsNullOrWhiteSpace(ResultRevision))
            throw new ArgumentException("must not be empty", nameof(ResultRevision));
        if (string.IsNullOrWhiteSpace(IndexIdentity))
            throw new ArgumentException("must not be empty", nameof(IndexIdentity));
        if (DurationSeconds is < 0.0) throw new ArgumentOutOfRangeException(nameof(DurationSeconds));

        this.Id = Id;
        this.TestCaseId = TestCaseId;
        this.Status = Status;
        this.ResultRevision = ResultRevision;
        this.IndexIdentity = IndexIdentity;
        this.DurationSeconds = DurationSeconds;
        this.FailureSummary = FailureSummary;
        this.Metadata = Metadata ?? new ReadOnlyDictionary<string, object?>(
            new Dictionary<string, object?>(StringComparer.Ordinal));
    }
}

public sealed record ProviderCoverageArtifact
{
    public string ArtifactPath { get; init; }
    public string Parser { get; init; }
    public string? ArtifactRoot { get; init; }
    public string? TestCaseId { get; init; }
    public string? GenerationId { get; init; }
    public bool? Complete { get; init; }

    public ProviderCoverageArtifact(
        string ArtifactPath,
        string Parser = "auto",
        string? ArtifactRoot = null,
        string? TestCaseId = null,
        string? GenerationId = null,
        bool? Complete = null)
    {
        if (string.IsNullOrWhiteSpace(ArtifactPath))
            throw new ArgumentException("must not be empty", nameof(ArtifactPath));
        if (string.IsNullOrWhiteSpace(Parser)) throw new ArgumentException("must not be empty", nameof(Parser));
        if (ArtifactRoot is not null && string.IsNullOrWhiteSpace(ArtifactRoot))
            throw new ArgumentException("must not be empty", nameof(ArtifactRoot));
        if (TestCaseId is not null && string.IsNullOrWhiteSpace(TestCaseId))
            throw new ArgumentException("must not be empty", nameof(TestCaseId));
        if (GenerationId is not null && string.IsNullOrWhiteSpace(GenerationId))
            throw new ArgumentException("must not be empty", nameof(GenerationId));

        this.ArtifactPath = ArtifactPath;
        this.Parser = Parser;
        this.ArtifactRoot = ArtifactRoot;
        this.TestCaseId = TestCaseId;
        this.GenerationId = GenerationId;
        this.Complete = Complete;
    }
}

public sealed record ProviderRunResult
{
    public string RunId { get; init; }
    public string Status { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public IReadOnlyList<ProviderCaseResult> CaseResults { get; init; }
    public string? ResultArtifactPath { get; init; }
    public IReadOnlyList<ProviderCoverageArtifact> CoverageArtifacts { get; init; }
    public string? GenerationId { get; init; }
    public IReadOnlyList<string> TestDisplayNames { get; init; }

    public ProviderRunResult(
        string RunId,
        string Status,
        DateTimeOffset? StartedAt = null,
        DateTimeOffset? EndedAt = null,
        IReadOnlyList<ProviderCaseResult>? CaseResults = null,
        string? ResultArtifactPath = null,
        IReadOnlyList<ProviderCoverageArtifact>? CoverageArtifacts = null,
        IReadOnlyList<string>? TestDisplayNames = null)
    {
        if (string.IsNullOrWhiteSpace(RunId)) throw new ArgumentException("must not be empty", nameof(RunId));
        if (string.IsNullOrWhiteSpace(Status)) throw new ArgumentException("must not be empty", nameof(Status));

        this.RunId = RunId;
        this.Status = Status;
        this.StartedAt = StartedAt;
        this.EndedAt = EndedAt;
        this.CaseResults = CaseResults ?? [];
        this.ResultArtifactPath = ResultArtifactPath;
        this.CoverageArtifacts = CoverageArtifacts ?? [];
        this.TestDisplayNames = TestDisplayNames ?? [];
    }
}

public sealed record ContinuousTestProviderRunStart
{
    public string WorkspaceId { get; init; }
    public string RunId { get; init; }
    public string SelectedRevision { get; init; }
    public string IndexIdentity { get; init; }
    public long Revision { get; init; }
    public IReadOnlyList<string> SelectedTestCaseIds { get; init; }
    public string? Command { get; init; }
    public string? Framework { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public IReadOnlyDictionary<string, object?> Metadata { get; init; }

    public ContinuousTestProviderRunStart(
        string WorkspaceId,
        string RunId,
        string SelectedRevision,
        string IndexIdentity,
        long Revision,
        IReadOnlyList<string>? SelectedTestCaseIds = null,
        string? Command = null,
        string? Framework = null,
        DateTimeOffset? StartedAt = null,
        IReadOnlyDictionary<string, object?>? Metadata = null)
    {
        if (string.IsNullOrWhiteSpace(WorkspaceId)) throw new ArgumentException("must not be empty", nameof(WorkspaceId));
        if (string.IsNullOrWhiteSpace(RunId)) throw new ArgumentException("must not be empty", nameof(RunId));
        if (string.IsNullOrWhiteSpace(SelectedRevision))
            throw new ArgumentException("must not be empty", nameof(SelectedRevision));
        if (string.IsNullOrWhiteSpace(IndexIdentity))
            throw new ArgumentException("must not be empty", nameof(IndexIdentity));
        if (Revision < 0)
            throw new ArgumentOutOfRangeException(nameof(Revision), "must not be negative");

        this.WorkspaceId = WorkspaceId;
        this.RunId = RunId;
        this.SelectedRevision = SelectedRevision;
        this.IndexIdentity = IndexIdentity;
        this.Revision = Revision;
        this.SelectedTestCaseIds = SelectedTestCaseIds ?? [];
        this.Command = Command;
        this.Framework = Framework;
        this.StartedAt = StartedAt;
        this.Metadata = Metadata ?? new ReadOnlyDictionary<string, object?>(
            new Dictionary<string, object?>(StringComparer.Ordinal));
    }
}
