using System.Globalization;

namespace Miller.Testing;

public enum ContinuousTestDeltaCompleteness
{
    Unavailable,
    Complete,
}

public sealed record ContinuousTestDaemonChange
{
    public ContinuousTestWorkspace Workspace { get; init; }
    public string CurrentRevision { get; init; }
    public string IndexIdentity { get; init; }
    public IReadOnlyList<string> ChangedPaths { get; init; }
    public IReadOnlyList<ContinuousTestImpactedSymbol> ImpactedSymbols { get; init; }
    public IReadOnlyList<ContinuousTestImpactedTest> ImpactedTests { get; init; }
    public bool WorkspaceScope { get; init; }
    public TimeSpan DebounceDelay { get; init; }
    public DateTimeOffset ObservedAt { get; init; }
    public IReadOnlyList<string> FilterArguments { get; init; }
    public string? Command { get; init; }
    public IReadOnlyList<string> ExcludeTraits { get; init; }
    public string? Framework { get; init; }
    public ContinuousTestDeltaCompleteness DeltaCompleteness { get; }
    public long? DeltaFromRevision { get; }
    public long? DeltaToRevision { get; }

    public CtFreshnessKey Freshness => new(IndexIdentity, ParsedRevision);

    public ContinuousTestDaemonChange(
        ContinuousTestWorkspace Workspace,
        string CurrentRevision,
        string IndexIdentity,
        IReadOnlyList<string>? ChangedPaths = null,
        IReadOnlyList<ContinuousTestImpactedSymbol>? ImpactedSymbols = null,
        IReadOnlyList<ContinuousTestImpactedTest>? ImpactedTests = null,
        bool WorkspaceScope = false,
        TimeSpan? DebounceDelay = null,
        DateTimeOffset? ObservedAt = null,
        IReadOnlyList<string>? FilterArguments = null,
        string? Command = null,
        IReadOnlyList<string>? ExcludeTraits = null,
        string? Framework = null,
        ContinuousTestDeltaCompleteness DeltaCompleteness = ContinuousTestDeltaCompleteness.Unavailable,
        long? DeltaFromRevision = null,
        long? DeltaToRevision = null)
    {
        ArgumentNullException.ThrowIfNull(Workspace);
        if (string.IsNullOrWhiteSpace(CurrentRevision))
            throw new ArgumentException("must not be empty", nameof(CurrentRevision));
        if (string.IsNullOrWhiteSpace(IndexIdentity))
            throw new ArgumentException("must not be empty", nameof(IndexIdentity));
        if (DebounceDelay is { } delay && delay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(DebounceDelay), "must not be negative");
        if (!Enum.IsDefined(DeltaCompleteness))
            throw new ArgumentOutOfRangeException(nameof(DeltaCompleteness));
        if (!long.TryParse(CurrentRevision, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedRevision)
            || parsedRevision < 0)
        {
            throw new ArgumentException("must be a non-negative integer", nameof(CurrentRevision));
        }

        var changedPaths = ChangedPaths ?? [];
        if (DeltaCompleteness == ContinuousTestDeltaCompleteness.Complete
            && (WorkspaceScope || changedPaths.Count == 0 || DeltaFromRevision is null || DeltaToRevision is null))
        {
            throw new ArgumentException("a complete delta requires changed paths, both revision endpoints, and project scope");
        }

        if (DeltaCompleteness == ContinuousTestDeltaCompleteness.Complete
            && (DeltaFromRevision >= DeltaToRevision || DeltaToRevision != parsedRevision))
        {
            throw new ArgumentException("a complete delta requires an increasing interval ending at the numeric current revision");
        }

        if (DeltaCompleteness == ContinuousTestDeltaCompleteness.Unavailable
            && (DeltaFromRevision is not null || DeltaToRevision is not null))
        {
            throw new ArgumentException("an unavailable delta cannot carry revision endpoints");
        }

        this.Workspace = Workspace;
        this.CurrentRevision = CurrentRevision;
        this.IndexIdentity = IndexIdentity;
        this.ChangedPaths = changedPaths;
        this.ImpactedSymbols = ImpactedSymbols ?? [];
        this.ImpactedTests = ImpactedTests ?? [];
        this.WorkspaceScope = WorkspaceScope;
        this.DebounceDelay = DebounceDelay ?? TimeSpan.Zero;
        this.ObservedAt = ObservedAt ?? DateTimeOffset.UtcNow;
        this.FilterArguments = FilterArguments ?? [];
        this.Command = Command ?? Workspace.Command;
        this.ExcludeTraits = ExcludeTraits ?? Workspace.ExcludeTraits;
        this.Framework = Framework ?? Workspace.Framework;
        this.DeltaCompleteness = DeltaCompleteness;
        this.DeltaFromRevision = DeltaFromRevision;
        this.DeltaToRevision = DeltaToRevision;
        ParsedRevision = parsedRevision;
    }

    private long ParsedRevision { get; }
}

public enum ContinuousTestRunLane
{
    Foreground,
    Backfill,
    Maintenance,
}

public sealed record ContinuousTestDaemonPendingRun
{
    public ContinuousTestWorkspace Workspace { get; init; }
    public string SelectedRevision { get; init; }
    public string CurrentRevision { get; init; }
    public string IndexIdentity { get; init; }
    public IReadOnlyList<string> TestCaseIds { get; init; }
    public IReadOnlyList<string> FilterArguments { get; init; }
    public string? Command { get; init; }
    public string? Framework { get; init; }
    public bool RefreshInventory { get; init; }
    public DateTimeOffset ObservedAt { get; init; }
    public DateTimeOffset ReadyAt { get; init; }
    public ContinuousTestRunLane Lane { get; init; } = ContinuousTestRunLane.Foreground;
    public IReadOnlyList<string> ExcludeTraits { get; init; } = [];
    public int ImpactPriority { get; init; } = ContinuousTestImpactPriority.WorkspaceScope;
    public ContinuousTestCoverageMode CoverageMode { get; init; } = ContinuousTestCoverageMode.None;

    public CtFreshnessKey Freshness => new(IndexIdentity, ParsedRevision);

    public ContinuousTestDaemonPendingRun(
        ContinuousTestWorkspace Workspace,
        string SelectedRevision,
        string CurrentRevision,
        string IndexIdentity,
        IReadOnlyList<string> TestCaseIds,
        IReadOnlyList<string> FilterArguments,
        string? Command,
        string? Framework,
        bool RefreshInventory,
        DateTimeOffset ObservedAt,
        DateTimeOffset ReadyAt)
    {
        ArgumentNullException.ThrowIfNull(Workspace);
        if (string.IsNullOrWhiteSpace(SelectedRevision))
            throw new ArgumentException("must not be empty", nameof(SelectedRevision));
        if (string.IsNullOrWhiteSpace(CurrentRevision))
            throw new ArgumentException("must not be empty", nameof(CurrentRevision));
        if (string.IsNullOrWhiteSpace(IndexIdentity))
            throw new ArgumentException("must not be empty", nameof(IndexIdentity));
        if (!long.TryParse(CurrentRevision, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
            || parsed < 0)
        {
            throw new ArgumentException("must be a non-negative integer", nameof(CurrentRevision));
        }

        this.Workspace = Workspace;
        this.SelectedRevision = SelectedRevision;
        this.CurrentRevision = CurrentRevision;
        this.IndexIdentity = IndexIdentity;
        this.TestCaseIds = TestCaseIds;
        this.FilterArguments = FilterArguments;
        this.Command = Command;
        this.Framework = Framework;
        this.RefreshInventory = RefreshInventory;
        this.ObservedAt = ObservedAt;
        this.ReadyAt = ReadyAt;
        ParsedRevision = parsed;
    }

    private long ParsedRevision { get; }
}

public static class ContinuousTestImpactPriority
{
    public const int WorkspaceScope = int.MaxValue;

    public static int ForConfidence(double confidence) =>
        confidence <= 0 ? 100 : Math.Clamp(100 - (int)Math.Round(confidence * 100), 0, 100);
}

public sealed record ContinuousTestDaemonEnqueueResult(
    ContinuousTestSelectionResult Selection,
    ContinuousTestDaemonPendingRun Pending);

public sealed record ContinuousTestDaemonDrainResult(
    ContinuousTestDaemonPendingRun Pending,
    ContinuousTestCoordinatorRunResult CoordinatorResult);
