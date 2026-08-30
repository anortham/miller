using Miller.Server.Workspaces;

namespace Miller.Dashboard;

public enum DashboardRefreshJobState
{
    Running,
    Completed,
}

/// <param name="Elapsed">Wall time since the job started, so a running panel can say how long it has been going.</param>
/// <param name="Result">The refresh outcome; null while the job is still running.</param>
public sealed record DashboardRefreshJobStatus(
    DashboardRefreshJobState State,
    TimeSpan Elapsed,
    WorkspaceRefreshResult? Result);

/// <summary>
/// In-memory, one-per-workspace background refresh jobs for the dashboard's Refresh button. A converge can
/// run for minutes; running it inside the POST would hold the request open until the browser gives up, so
/// the POST starts a job here and the page polls <c>/fragments/refresh-status</c> for the outcome. Local
/// dashboard scope only: no persistence, no queue — a restart simply forgets in-flight jobs.
/// </summary>
public static class DashboardRefreshJobs
{
    private static readonly object StateGate = new();

    private static readonly Dictionary<string, Job> Jobs = new(StringComparer.Ordinal);

    private static readonly Dictionary<string, LastOutcome> LastOutcomes = new(StringComparer.Ordinal);

    /// <summary>
    /// How long a consumed outcome stays readable through <see cref="PeekLastOutcome(string)"/>: long
    /// enough to cover the detail-stack refetch the terminal render triggers, short enough that a page
    /// opened much later does not present an old verdict as news.
    /// </summary>
    private static readonly TimeSpan LastOutcomeRetention = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Returns the job already running for this workspace, or starts one. A second click while a refresh is
    /// in flight must never run <paramref name="refresh"/> again — one workspace cannot converge twice at
    /// once. A finished-but-unobserved job is replaced, so a click after a poll was abandoned refreshes for
    /// real instead of replaying the stale result. New work always returns a running status so the caller
    /// installs a poll even when the task finishes before this method returns.
    /// </summary>
    public static DashboardRefreshJobStatus Start(string workspaceId, Func<WorkspaceRefreshResult> refresh)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentNullException.ThrowIfNull(refresh);

        Job job;
        lock (StateGate)
        {
            if (Jobs.TryGetValue(workspaceId, out Job? running) && !running.IsFinished)
            {
                job = running;
            }
            else
            {
                job = NewJob(workspaceId, refresh);
                Jobs[workspaceId] = job;
            }
        }

        _ = job.Task.Value;
        return DescribeRunning(job);
    }

    /// <summary>
    /// The job's current state: null when the workspace has none. A completed result is CONSUMED by the
    /// observation — the next poll of a terminal job sees no job at all — which is what makes the status
    /// panel render an outcome exactly once. <see cref="PeekLastOutcome(string)"/> is the non-consuming
    /// read that keeps the same outcome renderable for the detail-stack refetch that follows it.
    /// </summary>
    public static DashboardRefreshJobStatus? Peek(string workspaceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        lock (StateGate)
        {
            return ReadCurrentLocked(workspaceId);
        }
    }

    /// <summary>
    /// Reads the detail state as one decision. A running job is returned without consuming it. A completed
    /// current job is claimed and retained. With no current job, the still-fresh most recently claimed
    /// terminal outcome is returned.
    /// </summary>
    public static DashboardRefreshJobStatus? PeekDetail(string workspaceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        lock (StateGate)
        {
            DashboardRefreshJobStatus? current = ReadCurrentLocked(workspaceId);
            return current ?? ReadLastOutcomeLocked(workspaceId, DateTimeOffset.UtcNow);
        }
    }

    /// <summary>
    /// The most recently claimed terminal outcome, WITHOUT consuming it, for
    /// <see cref="LastOutcomeRetention"/> after that observation. The detail-stack refetch that follows a
    /// finished refresh re-renders the status span, and Peek's exactly-once contract would render it empty —
    /// the outcome the reader just saw would be unrecoverable. Null once the retention window has passed.
    /// </summary>
    public static DashboardRefreshJobStatus? PeekLastOutcome(string workspaceId) =>
        PeekLastOutcome(workspaceId, DateTimeOffset.UtcNow);

    internal static DashboardRefreshJobStatus? PeekLastOutcome(string workspaceId, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        lock (StateGate)
        {
            return ReadLastOutcomeLocked(workspaceId, now);
        }
    }

    private static DashboardRefreshJobStatus? ReadCurrentLocked(string workspaceId)
    {
        if (!Jobs.TryGetValue(workspaceId, out Job? job))
            return null;
        if (!job.IsFinished)
            return DescribeRunning(job);

        DashboardRefreshJobStatus status = DescribeCompleted(job);
        Jobs.Remove(workspaceId);
        RetainLocked(workspaceId, status);
        return status;
    }

    private static DashboardRefreshJobStatus? ReadLastOutcomeLocked(string workspaceId, DateTimeOffset now)
    {
        if (!LastOutcomes.TryGetValue(workspaceId, out LastOutcome? outcome))
            return null;
        if (now - outcome.ObservedAt < LastOutcomeRetention)
            return outcome.Status;

        LastOutcomes.Remove(workspaceId);

        return null;
    }

    private static void RetainLocked(string workspaceId, DashboardRefreshJobStatus status) =>
        LastOutcomes[workspaceId] = new LastOutcome(status, DateTimeOffset.UtcNow);

    private static Job NewJob(string workspaceId, Func<WorkspaceRefreshResult> refresh) =>
        new(
            new Lazy<Task<WorkspaceRefreshResult>>(
                () => Task.Run(() => Run(workspaceId, refresh)),
                LazyThreadSafetyMode.ExecutionAndPublication),
            DateTimeOffset.UtcNow);

    // A throwing refresh must land as a Failed outcome the panel can render: an unobserved task exception
    // would leave the panel polling a job that never reaches a terminal state.
    private static WorkspaceRefreshResult Run(string workspaceId, Func<WorkspaceRefreshResult> refresh)
    {
        try
        {
            return refresh();
        }
        catch (Exception ex)
        {
            return new WorkspaceRefreshResult(
                WorkspaceRefreshStatus.Failed,
                workspaceId,
                WorkspaceRoot: string.Empty,
                IndexDbPath: string.Empty,
                Error: ex.Message);
        }
    }

    private static DashboardRefreshJobStatus DescribeRunning(Job job) =>
        new(
            DashboardRefreshJobState.Running,
            DateTimeOffset.UtcNow - job.StartedAt,
            Result: null);

    private static DashboardRefreshJobStatus DescribeCompleted(Job job) =>
        new(
            DashboardRefreshJobState.Completed,
            DateTimeOffset.UtcNow - job.StartedAt,
            job.Task.Value.GetAwaiter().GetResult());

    private sealed record LastOutcome(DashboardRefreshJobStatus Status, DateTimeOffset ObservedAt);

    private sealed record Job(Lazy<Task<WorkspaceRefreshResult>> Task, DateTimeOffset StartedAt)
    {
        // Not yet valued means Start is still publishing it. Treat it as running and never force it here.
        public bool IsFinished => Task.IsValueCreated && Task.Value.IsCompleted;
    }
}
