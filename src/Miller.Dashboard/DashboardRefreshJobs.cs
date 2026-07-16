using System.Collections.Concurrent;
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
    private static readonly ConcurrentDictionary<string, Job> Jobs = new(StringComparer.Ordinal);

    /// <summary>
    /// Returns the job already running for this workspace, or starts one. A second click while a refresh is
    /// in flight must never run <paramref name="refresh"/> again — one workspace cannot converge twice at
    /// once. A finished-but-unobserved job is replaced, so a click after a poll was abandoned refreshes for
    /// real instead of replaying the stale result.
    /// </summary>
    public static DashboardRefreshJobStatus Start(string workspaceId, Func<WorkspaceRefreshResult> refresh)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentNullException.ThrowIfNull(refresh);

        Job job = Jobs.AddOrUpdate(
            workspaceId,
            _ => NewJob(workspaceId, refresh),
            (_, running) => running.IsFinished ? NewJob(workspaceId, refresh) : running);
        _ = job.Task.Value;
        return Describe(job);
    }

    /// <summary>
    /// The job's current state: null when the workspace has none. A completed result is CONSUMED by the
    /// observation — the next poll of a terminal job sees no job at all, which is what makes the status
    /// panel render an outcome exactly once and then stop polling.
    /// </summary>
    public static DashboardRefreshJobStatus? Peek(string workspaceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        if (!Jobs.TryGetValue(workspaceId, out Job? job))
            return null;
        if (!job.IsFinished)
            return Describe(job);

        // Remove only this job: a Start racing the observation must keep its fresh job.
        Jobs.TryRemove(new KeyValuePair<string, Job>(workspaceId, job));
        return Describe(job);
    }

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

    private static DashboardRefreshJobStatus Describe(Job job) =>
        job.IsFinished
            ? new DashboardRefreshJobStatus(
                DashboardRefreshJobState.Completed,
                DateTimeOffset.UtcNow - job.StartedAt,
                job.Task.Value.GetAwaiter().GetResult())
            : new DashboardRefreshJobStatus(
                DashboardRefreshJobState.Running,
                DateTimeOffset.UtcNow - job.StartedAt,
                Result: null);

    private sealed record Job(Lazy<Task<WorkspaceRefreshResult>> Task, DateTimeOffset StartedAt)
    {
        // Not yet valued means Start is still publishing it — running, and never force it here: only the
        // publishing Start may start the work.
        public bool IsFinished => Task.IsValueCreated && Task.Value.IsCompleted;
    }
}
