using System.Globalization;

namespace Miller.Testing;

public readonly record struct CtFreshnessKey
{
    public string IndexIdentity { get; }
    public long Revision { get; }

    public CtFreshnessKey(string IndexIdentity, long Revision)
    {
        if (string.IsNullOrWhiteSpace(IndexIdentity))
            throw new ArgumentException("must not be empty", nameof(IndexIdentity));
        if (Revision < 0)
            throw new ArgumentOutOfRangeException(nameof(Revision), "must not be negative");
        this.IndexIdentity = IndexIdentity;
        this.Revision = Revision;
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{IndexIdentity}@{Revision}");
}

public enum ContinuousTestState
{
    Unknown,
    Green,
    Red,
    Skipped,
    Running,
    Stale,
}

public enum ContinuousTestVerdict
{
    Green,
    Partial,
    Unknown,
    Red,
}

public enum ContinuousTestFlakinessState
{
    Stable,
    Flaky,
    ConsistentlyFailing,
    Unknown,
}

public enum ContinuousTestRole
{
    TestCase,
    ParameterizedTest,
    FixtureSetup,
    FixtureTeardown,
    TestContainer,
}

public sealed record ContinuousTestWatchHealthSnapshot(
    string State,
    string? ObservedRevision,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastErrorAt,
    string? ErrorCode);

public interface IContinuousTestWatchHealthSource
{
    ContinuousTestWatchHealthSnapshot Get(string workspaceId);
}

public static class ContinuousTestFreshness
{
    public static CtFreshnessKey? CompleteAt(IReadOnlyList<ContinuousTestStatus> statuses)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        if (statuses.Count == 0
            || statuses.Any(row =>
                row.ProvenFreshKey is null
                || row.State is ContinuousTestState.Unknown
                    or ContinuousTestState.Running
                    or ContinuousTestState.Stale))
        {
            return null;
        }

        CtFreshnessKey first = statuses[0].ProvenFreshKey!.Value;
        return statuses.All(row => row.ProvenFreshKey == first) ? first : null;
    }

    public static ContinuousTestVerdict Evaluate(
        IReadOnlyList<ContinuousTestStatus> statuses,
        CtFreshnessKey selected,
        bool watchHealthy) =>
        ContinuousTestStatusProjection.Project(selected, statuses, watermarks: null, watchHealthy).Verdict;
}

/// <summary>
/// One workspace's projected live status: the verdict, the key it was judged at, and how many
/// rows need a run to be green at that key. <see cref="SelectedKey"/> is null exactly when no
/// live cursor was available, and then the verdict is <see cref="ContinuousTestVerdict.Unknown"/>.
/// </summary>
public sealed record ContinuousTestProjectedStatus(
    ContinuousTestVerdict Verdict,
    CtFreshnessKey? SelectedKey,
    int StaleCount);

public sealed record ContinuousTestStatusAggregate(
    int Total,
    int Pending,
    int Stale,
    int FreshRed);

/// <summary>
/// THE status projection: the live index cursor plus the stored rows yield the verdict and the
/// staleness. The selected key comes ONLY from the live cursor — a projection that derived it from
/// the rows it judges read uniformly stale rows as green forever, and flipped keys between two
/// consecutive reads (observed live 2026-08-20: rev 32424 in one read, 32161 in the next).
/// Every status path (foreground status, run verdicts, summaries, daemon evaluation) must call
/// this one implementation.
/// </summary>
public static class ContinuousTestStatusProjection
{
    /// <summary>
    /// Projects the verdict and staleness of <paramref name="statuses"/> at the live key.
    ///
    /// <para>A row is fresh when it is committed at the live key
    /// (<see cref="ContinuousTestDurableFreshness.IsCommittedFreshAt"/>) or when it is green and a
    /// per-case watermark covers the live key
    /// (<see cref="ContinuousTestDurableFreshness.IsWatermarkFreshAt"/>). Only green results ride
    /// the watermark; a red stays where it ran until its test reruns. <paramref name="watermarks"/>
    /// maps a test-case id to its fresh watermark, written by
    /// <c>ContinuousTestStore.ApplyRevisionAdvance</c> and read per index identity via
    /// <c>ListContinuousTestFreshWatermarks</c>.</para>
    ///
    /// <para>No live cursor means the verdict is honest <see cref="ContinuousTestVerdict.Unknown"/>
    /// with no key, and the stale count falls back to the rows the store itself marked stale.</para>
    /// </summary>
    public static ContinuousTestProjectedStatus Project(
        CtFreshnessKey? liveKey,
        IReadOnlyList<ContinuousTestStatus> statuses,
        IReadOnlyDictionary<string, CtFreshnessKey>? watermarks = null,
        bool watchHealthy = true)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        if (liveKey is not { } selected)
        {
            return new ContinuousTestProjectedStatus(
                ContinuousTestVerdict.Unknown,
                SelectedKey: null,
                StaleCount: statuses.Count(row => row.State == ContinuousTestState.Stale));
        }

        bool pending = false;
        bool red = false;
        int stale = 0;
        foreach (ContinuousTestStatus row in statuses)
        {
            if (row.State is ContinuousTestState.Unknown or ContinuousTestState.Running)
            {
                pending = true;
            }
            else if (!IsFreshAt(row, selected, watermarks))
            {
                stale++;
            }
            else if (row.State == ContinuousTestState.Red)
            {
                red = true;
            }
        }

        ContinuousTestVerdict verdict;
        if (!watchHealthy || statuses.Count == 0 || pending)
            verdict = ContinuousTestVerdict.Unknown;
        else if (stale > 0)
            verdict = ContinuousTestVerdict.Partial;
        else if (red)
            verdict = ContinuousTestVerdict.Red;
        else
            verdict = ContinuousTestVerdict.Green;

        return new ContinuousTestProjectedStatus(verdict, selected, stale);
    }

    public static ContinuousTestProjectedStatus Project(
        CtFreshnessKey? liveKey,
        ContinuousTestStatusAggregate aggregate,
        bool watchHealthy = true)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        if (liveKey is not { } selected)
        {
            return new ContinuousTestProjectedStatus(
                ContinuousTestVerdict.Unknown,
                SelectedKey: null,
                StaleCount: aggregate.Stale);
        }

        ContinuousTestVerdict verdict;
        if (!watchHealthy || aggregate.Total == 0 || aggregate.Pending > 0)
            verdict = ContinuousTestVerdict.Unknown;
        else if (aggregate.Stale > 0)
            verdict = ContinuousTestVerdict.Partial;
        else if (aggregate.FreshRed > 0)
            verdict = ContinuousTestVerdict.Red;
        else
            verdict = ContinuousTestVerdict.Green;

        return new ContinuousTestProjectedStatus(verdict, selected, aggregate.Stale);
    }

    private static bool IsFreshAt(
        ContinuousTestStatus row,
        CtFreshnessKey selected,
        IReadOnlyDictionary<string, CtFreshnessKey>? watermarks) =>
        ContinuousTestDurableFreshness.IsFreshAt(row, selected, watermarks);
}

public sealed record ContinuousTestOutcome(string Status, DateTimeOffset ObservedAt);

public sealed record ContinuousTestFlakinessScore(
    ContinuousTestFlakinessState State,
    double FailureRate,
    int Transitions,
    int Samples);

public static class ContinuousTestFlakiness
{
    public const int MaxHistory = 50;
    private const int MinTrendSamples = 2;

    private static readonly HashSet<string> FailureStatuses = new(StringComparer.Ordinal)
    {
        "failed",
        "error",
    };

    private static readonly HashSet<string> TransitionStatuses = new(StringComparer.Ordinal)
    {
        "passed",
        "failed",
        "error",
    };

    public static ContinuousTestFlakinessScore Score(IEnumerable<ContinuousTestOutcome> outcomes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);

        var ordered = outcomes
            .OrderBy(outcome => outcome.ObservedAt)
            .ToArray();
        var transitionStatuses = ordered
            .Select(outcome => NormalizeStatus(outcome.Status))
            .Where(status => status is not null && TransitionStatuses.Contains(status))
            .Cast<string>()
            .ToArray();
        int samples = ordered.Length;
        int failures = transitionStatuses.Count(status => FailureStatuses.Contains(status));
        int passes = transitionStatuses.Count(status => status == "passed");
        double failureRate = transitionStatuses.Length == 0 ? 0.0 : (double)failures / transitionStatuses.Length;
        int transitions = TransitionCount(transitionStatuses);

        ContinuousTestFlakinessState state;
        if (transitionStatuses.Length < MinTrendSamples)
            state = ContinuousTestFlakinessState.Unknown;
        else if (failures == transitionStatuses.Length)
            state = ContinuousTestFlakinessState.ConsistentlyFailing;
        else if (transitionStatuses.Length >= 4 && passes > 0 && failures > 0 && transitions >= 2)
            state = ContinuousTestFlakinessState.Flaky;
        else
            state = ContinuousTestFlakinessState.Stable;

        return new ContinuousTestFlakinessScore(state, failureRate, transitions, samples);
    }

    public static string? NormalizeStatus(string status)
    {
        string normalized = status.Trim().ToLowerInvariant();
        if (normalized == "errored")
            return "error";
        return normalized is "passed" or "failed" or "skipped" or "error" ? normalized : null;
    }

    private static int TransitionCount(IReadOnlyList<string> statuses)
    {
        int transitions = 0;
        string? previous = null;
        foreach (string status in statuses)
        {
            string current = FailureStatuses.Contains(status) ? "failed" : "passed";
            if (previous is not null && !string.Equals(previous, current, StringComparison.Ordinal))
                transitions++;
            previous = current;
        }

        return transitions;
    }
}

public sealed record ContinuousTestProject
{
    public string Id { get; init; }
    public string WorkspaceId { get; init; }
    public string ProjectPath { get; init; }
    public string? Framework { get; init; }
    public string? Command { get; init; }
    public bool Enabled { get; init; }
    public IReadOnlyDictionary<string, object?> Metadata { get; init; }
    public IReadOnlyList<string> ExcludeTraits { get; init; }
    public bool InventoryStale { get; init; }

    public ContinuousTestProject(
        string Id,
        string WorkspaceId,
        string ProjectPath,
        string? Framework = null,
        string? Command = null,
        bool Enabled = true,
        IReadOnlyDictionary<string, object?>? Metadata = null,
        IReadOnlyList<string>? ExcludeTraits = null,
        bool InventoryStale = false)
    {
        if (string.IsNullOrEmpty(Id)) throw new ArgumentException("must not be empty", nameof(Id));
        if (string.IsNullOrEmpty(WorkspaceId)) throw new ArgumentException("must not be empty", nameof(WorkspaceId));
        if (string.IsNullOrEmpty(ProjectPath)) throw new ArgumentException("must not be empty", nameof(ProjectPath));

        this.Id = Id;
        this.WorkspaceId = WorkspaceId;
        this.ProjectPath = Path.GetFullPath(ProjectPath);
        this.Framework = Framework;
        this.Command = Command;
        this.Enabled = Enabled;
        this.Metadata = Metadata ?? new Dictionary<string, object?>();
        this.ExcludeTraits = ExcludeTraits ?? [];
        this.InventoryStale = InventoryStale;
    }
}

public sealed record ContinuousTestStatus
{
    public string WorkspaceId { get; init; }
    public string TestCaseId { get; init; }
    public ContinuousTestState State { get; init; }
    public string IndexIdentity { get; init; }
    public long Revision { get; init; }
    public string? LastRunRevision { get; init; }
    public string? StaleSinceRevision { get; init; }
    public string? RunningRunId { get; init; }
    public string? RunningRevision { get; init; }
    public string? LastResultStatus { get; init; }
    public DateTimeOffset? LastResultAt { get; init; }
    public string? FailureSummary { get; init; }
    public double FlakinessScore { get; init; }
    public CtFreshnessKey? ProvenFreshKey { get; init; }

    public ContinuousTestStatus(
        string WorkspaceId,
        string TestCaseId,
        ContinuousTestState State,
        string IndexIdentity,
        long Revision,
        string? LastRunRevision = null,
        string? StaleSinceRevision = null,
        string? RunningRunId = null,
        string? RunningRevision = null,
        string? LastResultStatus = null,
        DateTimeOffset? LastResultAt = null,
        string? FailureSummary = null,
        double FlakinessScore = 0.0,
        CtFreshnessKey? ProvenFreshKey = null)
    {
        if (string.IsNullOrEmpty(WorkspaceId)) throw new ArgumentException("must not be empty", nameof(WorkspaceId));
        if (string.IsNullOrEmpty(TestCaseId)) throw new ArgumentException("must not be empty", nameof(TestCaseId));
        if (string.IsNullOrWhiteSpace(IndexIdentity))
            throw new ArgumentException("must not be empty", nameof(IndexIdentity));
        if (Revision < 0) throw new ArgumentOutOfRangeException(nameof(Revision), "must not be negative");
        if (FlakinessScore < 0.0 || FlakinessScore > 1.0)
            throw new ArgumentOutOfRangeException(nameof(FlakinessScore), "must be in [0,1]");

        this.WorkspaceId = WorkspaceId;
        this.TestCaseId = TestCaseId;
        this.State = State;
        this.IndexIdentity = IndexIdentity;
        this.Revision = Revision;
        this.LastRunRevision = LastRunRevision;
        this.StaleSinceRevision = StaleSinceRevision;
        this.RunningRunId = RunningRunId;
        this.RunningRevision = RunningRevision;
        this.LastResultStatus = LastResultStatus;
        this.LastResultAt = LastResultAt;
        this.FailureSummary = FailureSummary;
        this.FlakinessScore = FlakinessScore;
        this.ProvenFreshKey = ProvenFreshKey;
    }
}

public sealed record ContinuousTestDetailRow
{
    public string TestCaseId { get; init; }
    public string Selector { get; init; }
    public string ProjectPath { get; init; }
    public ContinuousTestState State { get; init; }
    public string IndexIdentity { get; init; }
    public long Revision { get; init; }
    public string? FailureSummary { get; init; }
    public string? LastRunRevision { get; init; }
    public string? StaleSinceRevision { get; init; }
    public string? RunningRevision { get; init; }
    public string? CompletedRevision { get; init; }
    public DateTimeOffset? LastResultAt { get; init; }

    public ContinuousTestDetailRow(
        string TestCaseId,
        string Selector,
        string ProjectPath,
        ContinuousTestState State,
        string IndexIdentity,
        long Revision,
        string? FailureSummary = null,
        string? LastRunRevision = null,
        string? StaleSinceRevision = null,
        string? RunningRevision = null,
        string? CompletedRevision = null,
        DateTimeOffset? LastResultAt = null)
    {
        if (string.IsNullOrEmpty(TestCaseId)) throw new ArgumentException("must not be empty", nameof(TestCaseId));
        if (string.IsNullOrEmpty(Selector)) throw new ArgumentException("must not be empty", nameof(Selector));
        if (string.IsNullOrEmpty(ProjectPath)) throw new ArgumentException("must not be empty", nameof(ProjectPath));
        if (string.IsNullOrWhiteSpace(IndexIdentity))
            throw new ArgumentException("must not be empty", nameof(IndexIdentity));
        if (Revision < 0) throw new ArgumentOutOfRangeException(nameof(Revision), "must not be negative");

        this.TestCaseId = TestCaseId;
        this.Selector = Selector;
        this.ProjectPath = ProjectPath;
        this.State = State;
        this.IndexIdentity = IndexIdentity;
        this.Revision = Revision;
        this.FailureSummary = FailureSummary;
        this.LastRunRevision = LastRunRevision;
        this.StaleSinceRevision = StaleSinceRevision;
        this.RunningRevision = RunningRevision;
        this.CompletedRevision = CompletedRevision;
        this.LastResultAt = LastResultAt;
    }
}

public sealed record ContinuousTestCase
{
    public string Id { get; init; }
    public string WorkspaceId { get; init; }
    public string? FilePath { get; init; }
    public string? ContentHash { get; init; }
    public string? SymbolName { get; init; }
    public string? SymbolPath { get; init; }
    public string? SuiteId { get; init; }
    public string Name { get; init; }
    public string QualifiedName { get; init; }
    public string Selector { get; init; }
    public string? Framework { get; init; }
    public ContinuousTestRole Role { get; init; }
    public string Source { get; init; }
    public double Confidence { get; init; }
    public IReadOnlyDictionary<string, object?> Metadata { get; init; }
    public IReadOnlyDictionary<string, object?> Provenance { get; init; }

    public ContinuousTestCase(
        string Id,
        string WorkspaceId,
        string Name,
        string QualifiedName,
        string Selector,
        string? FilePath = null,
        string? ContentHash = null,
        string? SymbolName = null,
        string? SymbolPath = null,
        string? SuiteId = null,
        string? Framework = null,
        ContinuousTestRole Role = ContinuousTestRole.TestCase,
        string Source = "extractor",
        double Confidence = 1.0,
        IReadOnlyDictionary<string, object?>? Metadata = null,
        IReadOnlyDictionary<string, object?>? Provenance = null)
    {
        if (string.IsNullOrEmpty(Id)) throw new ArgumentException("must not be empty", nameof(Id));
        if (string.IsNullOrEmpty(WorkspaceId)) throw new ArgumentException("must not be empty", nameof(WorkspaceId));
        if (string.IsNullOrEmpty(Name)) throw new ArgumentException("must not be empty", nameof(Name));
        if (string.IsNullOrEmpty(QualifiedName)) throw new ArgumentException("must not be empty", nameof(QualifiedName));
        if (string.IsNullOrEmpty(Selector)) throw new ArgumentException("must not be empty", nameof(Selector));
        if (string.IsNullOrEmpty(Source)) throw new ArgumentException("must not be empty", nameof(Source));
        if (Confidence < 0.0 || Confidence > 1.0)
            throw new ArgumentOutOfRangeException(nameof(Confidence), "must be in [0,1]");

        this.Id = Id;
        this.WorkspaceId = WorkspaceId;
        this.FilePath = FilePath;
        this.ContentHash = ContentHash;
        this.SymbolName = SymbolName;
        this.SymbolPath = SymbolPath;
        this.SuiteId = SuiteId;
        this.Name = Name;
        this.QualifiedName = QualifiedName;
        this.Selector = Selector;
        this.Framework = Framework;
        this.Role = Role;
        this.Source = Source;
        this.Confidence = Confidence;
        this.Metadata = Metadata ?? new Dictionary<string, object?>();
        this.Provenance = Provenance ?? new Dictionary<string, object?>();
    }
}

public sealed record ContinuousTestRun
{
    public string Id { get; init; }
    public string WorkspaceId { get; init; }
    public string IndexIdentity { get; init; }
    public long Revision { get; init; }
    public string? Command { get; init; }
    public string? Framework { get; init; }
    public string Status { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public string SelectedRevision { get; init; }
    public string? CompletedRevision { get; init; }
    public string? ArtifactId { get; init; }
    public IReadOnlyDictionary<string, object?> Metadata { get; init; }

    public ContinuousTestRun(
        string Id,
        string WorkspaceId,
        string Status,
        string SelectedRevision,
        string IndexIdentity,
        long Revision,
        string? Command = null,
        string? Framework = null,
        DateTimeOffset? StartedAt = null,
        DateTimeOffset? EndedAt = null,
        string? CompletedRevision = null,
        string? ArtifactId = null,
        IReadOnlyDictionary<string, object?>? Metadata = null)
    {
        if (string.IsNullOrEmpty(Id)) throw new ArgumentException("must not be empty", nameof(Id));
        if (string.IsNullOrEmpty(WorkspaceId)) throw new ArgumentException("must not be empty", nameof(WorkspaceId));
        if (string.IsNullOrEmpty(Status)) throw new ArgumentException("must not be empty", nameof(Status));
        if (string.IsNullOrEmpty(SelectedRevision)) throw new ArgumentException("must not be empty", nameof(SelectedRevision));
        if (string.IsNullOrWhiteSpace(IndexIdentity))
            throw new ArgumentException("must not be empty", nameof(IndexIdentity));
        if (Revision < 0) throw new ArgumentOutOfRangeException(nameof(Revision), "must not be negative");

        this.Id = Id;
        this.WorkspaceId = WorkspaceId;
        this.IndexIdentity = IndexIdentity;
        this.Revision = Revision;
        this.Command = Command;
        this.Framework = Framework;
        this.Status = Status;
        this.StartedAt = StartedAt;
        this.EndedAt = EndedAt;
        this.SelectedRevision = SelectedRevision;
        this.CompletedRevision = CompletedRevision;
        this.ArtifactId = ArtifactId;
        this.Metadata = Metadata ?? new Dictionary<string, object?>();
    }
}

public sealed record ContinuousTestOrphanedRun(
    string RunId,
    string SelectedRevision,
    string IndexIdentity,
    long Revision,
    int RunningCaseCount);

public sealed record ContinuousTestResult
{
    public string Id { get; init; }
    public string WorkspaceId { get; init; }
    public string TestCaseId { get; init; }
    public string TestRunId { get; init; }
    public string Status { get; init; }
    public string IndexIdentity { get; init; }
    public long Revision { get; init; }
    public string ResultRevision { get; init; }
    public double? DurationSeconds { get; init; }
    public string? FailureSummary { get; init; }
    public string? SourceArtifactId { get; init; }
    public IReadOnlyDictionary<string, object?> Metadata { get; init; }

    public ContinuousTestResult(
        string Id,
        string WorkspaceId,
        string TestCaseId,
        string TestRunId,
        string Status,
        string ResultRevision,
        string IndexIdentity,
        long Revision,
        double? DurationSeconds = null,
        string? FailureSummary = null,
        string? SourceArtifactId = null,
        IReadOnlyDictionary<string, object?>? Metadata = null)
    {
        if (string.IsNullOrEmpty(Id)) throw new ArgumentException("must not be empty", nameof(Id));
        if (string.IsNullOrEmpty(WorkspaceId)) throw new ArgumentException("must not be empty", nameof(WorkspaceId));
        if (string.IsNullOrEmpty(TestCaseId)) throw new ArgumentException("must not be empty", nameof(TestCaseId));
        if (string.IsNullOrEmpty(TestRunId)) throw new ArgumentException("must not be empty", nameof(TestRunId));
        if (string.IsNullOrEmpty(Status)) throw new ArgumentException("must not be empty", nameof(Status));
        if (string.IsNullOrEmpty(ResultRevision)) throw new ArgumentException("must not be empty", nameof(ResultRevision));
        if (string.IsNullOrWhiteSpace(IndexIdentity))
            throw new ArgumentException("must not be empty", nameof(IndexIdentity));
        if (Revision < 0) throw new ArgumentOutOfRangeException(nameof(Revision), "must not be negative");
        if (DurationSeconds is < 0.0)
            throw new ArgumentOutOfRangeException(nameof(DurationSeconds), "must be non-negative");

        this.Id = Id;
        this.WorkspaceId = WorkspaceId;
        this.TestCaseId = TestCaseId;
        this.TestRunId = TestRunId;
        this.Status = Status;
        this.IndexIdentity = IndexIdentity;
        this.Revision = Revision;
        this.ResultRevision = ResultRevision;
        this.DurationSeconds = DurationSeconds;
        this.FailureSummary = FailureSummary;
        this.SourceArtifactId = SourceArtifactId;
        this.Metadata = Metadata ?? new Dictionary<string, object?>();
    }
}

public sealed record ContinuousTestRunCompletion
{
    public string WorkspaceId { get; init; }
    public string TestRunId { get; init; }
    public string SelectedRevision { get; init; }
    public string CurrentRevision { get; init; }
    public string IndexIdentity { get; init; }
    public long Revision { get; init; }
    public string Status { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public IReadOnlyList<ContinuousTestResult> Results { get; init; }

    public ContinuousTestRunCompletion(
        string WorkspaceId,
        string TestRunId,
        string SelectedRevision,
        string CurrentRevision,
        string IndexIdentity,
        long Revision,
        string Status,
        DateTimeOffset? EndedAt = null,
        IReadOnlyList<ContinuousTestResult>? Results = null)
    {
        if (string.IsNullOrEmpty(WorkspaceId)) throw new ArgumentException("must not be empty", nameof(WorkspaceId));
        if (string.IsNullOrEmpty(TestRunId)) throw new ArgumentException("must not be empty", nameof(TestRunId));
        if (string.IsNullOrEmpty(SelectedRevision)) throw new ArgumentException("must not be empty", nameof(SelectedRevision));
        if (string.IsNullOrEmpty(CurrentRevision)) throw new ArgumentException("must not be empty", nameof(CurrentRevision));
        if (string.IsNullOrWhiteSpace(IndexIdentity))
            throw new ArgumentException("must not be empty", nameof(IndexIdentity));
        if (Revision < 0) throw new ArgumentOutOfRangeException(nameof(Revision), "must not be negative");
        if (string.IsNullOrEmpty(Status)) throw new ArgumentException("must not be empty", nameof(Status));

        this.WorkspaceId = WorkspaceId;
        this.TestRunId = TestRunId;
        this.SelectedRevision = SelectedRevision;
        this.CurrentRevision = CurrentRevision;
        this.IndexIdentity = IndexIdentity;
        this.Revision = Revision;
        this.Status = Status;
        this.EndedAt = EndedAt;
        this.Results = Results ?? [];
    }
}

public sealed record ContinuousTestRunArtifact
{
    public string Id { get; init; }
    public string WorkspaceId { get; init; }
    public string Kind { get; init; }
    public string? Path { get; init; }
    public IReadOnlyDictionary<string, object?> Payload { get; init; }
    public DateTimeOffset CreatedAt { get; init; }

    public ContinuousTestRunArtifact(
        string Id,
        string WorkspaceId,
        string Kind,
        string? Path = null,
        IReadOnlyDictionary<string, object?>? Payload = null,
        DateTimeOffset? CreatedAt = null)
    {
        if (string.IsNullOrEmpty(Id)) throw new ArgumentException("must not be empty", nameof(Id));
        if (string.IsNullOrEmpty(WorkspaceId)) throw new ArgumentException("must not be empty", nameof(WorkspaceId));
        if (string.IsNullOrEmpty(Kind)) throw new ArgumentException("must not be empty", nameof(Kind));

        this.Id = Id;
        this.WorkspaceId = WorkspaceId;
        this.Kind = Kind;
        this.Path = Path;
        this.Payload = Payload ?? new Dictionary<string, object?>();
        this.CreatedAt = CreatedAt ?? DateTimeOffset.UtcNow;
    }
}
