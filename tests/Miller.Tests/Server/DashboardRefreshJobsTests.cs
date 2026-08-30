using Miller.Dashboard;
using Miller.Server.Workspaces;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// The dashboard's background refresh jobs: a converge can run for minutes, so the Refresh POST starts a
/// job and the page polls for its terminal result. These tests always inject a fake refresh func — a real
/// refresh would spawn julie-extract.
/// </summary>
public sealed class DashboardRefreshJobsTests
{
    [Fact]
    public async Task Start_ReturnsRunningWhileTheRefreshFuncIsStillBlocked()
    {
        string workspaceId = NewWorkspaceId();
        var gate = new TaskCompletionSource();

        DashboardRefreshJobStatus status = DashboardRefreshJobs.Start(workspaceId, GatedRefresh(gate, workspaceId));

        Assert.Equal(DashboardRefreshJobState.Running, status.State);
        Assert.Null(status.Result);
        Assert.False(gate.Task.IsCompleted);

        gate.SetResult();
        await WaitForCompletedAsync(workspaceId);
    }

    [Fact]
    public async Task Start_WhileAJobIsRunning_ReturnsThatJobAndDoesNotRefreshTwice()
    {
        string workspaceId = NewWorkspaceId();
        var gate = new TaskCompletionSource();
        int runs = 0;
        Func<WorkspaceRefreshResult> refresh = () =>
        {
            Interlocked.Increment(ref runs);
            gate.Task.GetAwaiter().GetResult();
            return Refreshed(workspaceId);
        };

        DashboardRefreshJobStatus first = DashboardRefreshJobs.Start(workspaceId, refresh);
        DashboardRefreshJobStatus second = DashboardRefreshJobs.Start(workspaceId, refresh);

        Assert.Equal(DashboardRefreshJobState.Running, first.State);
        Assert.Equal(DashboardRefreshJobState.Running, second.State);

        gate.SetResult();
        await WaitForCompletedAsync(workspaceId);
        Assert.Equal(1, Volatile.Read(ref runs));
    }

    [Fact]
    public void Peek_WorkspaceWithNoJob_ReturnsNull() =>
        Assert.Null(DashboardRefreshJobs.Peek(NewWorkspaceId()));

    [Fact]
    public async Task Peek_WhileRunning_ReturnsRunningWithoutAResult()
    {
        string workspaceId = NewWorkspaceId();
        var gate = new TaskCompletionSource();
        DashboardRefreshJobs.Start(workspaceId, GatedRefresh(gate, workspaceId));

        DashboardRefreshJobStatus? status = DashboardRefreshJobs.Peek(workspaceId);

        Assert.Equal(DashboardRefreshJobState.Running, status?.State);
        Assert.Null(status?.Result);
        Assert.True(status?.Elapsed >= TimeSpan.Zero);

        gate.SetResult();
        await WaitForCompletedAsync(workspaceId);
    }

    [Fact]
    public async Task Peek_AfterCompletion_ReturnsTheResultExactlyOnce()
    {
        string workspaceId = NewWorkspaceId();
        DashboardRefreshJobs.Start(workspaceId, () => Refreshed(workspaceId));

        DashboardRefreshJobStatus completed = await WaitForCompletedAsync(workspaceId);

        Assert.Equal(WorkspaceRefreshStatus.Refreshed, completed.Result?.Status);
        Assert.Equal(43, completed.Result?.Revision);
        Assert.Null(DashboardRefreshJobs.Peek(workspaceId));
    }

    [Fact]
    public async Task Peek_RefreshFuncThrew_ReportsAFailedResultCarryingTheMessage()
    {
        string workspaceId = NewWorkspaceId();
        DashboardRefreshJobs.Start(
            workspaceId,
            () => throw new InvalidOperationException("julie-extract is missing"));

        DashboardRefreshJobStatus completed = await WaitForCompletedAsync(workspaceId);

        Assert.Equal(WorkspaceRefreshStatus.Failed, completed.Result?.Status);
        Assert.Equal(workspaceId, completed.Result?.WorkspaceId);
        Assert.Contains("julie-extract is missing", completed.Result?.Error ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_AfterACompletionNobodyObserved_RunsAFreshRefresh()
    {
        string workspaceId = NewWorkspaceId();
        var gate = new TaskCompletionSource();
        int firstRuns = 0;
        int secondRuns = 0;

        DashboardRefreshJobs.Start(workspaceId, () =>
        {
            Interlocked.Increment(ref firstRuns);
            return Refreshed(workspaceId);
        });
        await WaitAsync(() =>
        {
            DashboardRefreshJobs.Start(workspaceId, () =>
            {
                Interlocked.Increment(ref secondRuns);
                gate.Task.GetAwaiter().GetResult();
                return Refreshed(workspaceId);
            });
            return Volatile.Read(ref secondRuns) == 1;
        });

        Assert.Equal(1, Volatile.Read(ref firstRuns));
        Assert.Equal(DashboardRefreshJobState.Running, DashboardRefreshJobs.Peek(workspaceId)?.State);

        gate.SetResult();
        await WaitForCompletedAsync(workspaceId);
    }

    [Fact]
    public async Task PeekRunning_ReportsARunningJobWithoutConsumingIt()
    {
        string workspaceId = NewWorkspaceId();
        var gate = new TaskCompletionSource();
        DashboardRefreshJobs.Start(workspaceId, GatedRefresh(gate, workspaceId));

        Assert.Equal(DashboardRefreshJobState.Running, DashboardRefreshJobs.PeekRunning(workspaceId)?.State);
        Assert.Equal(DashboardRefreshJobState.Running, DashboardRefreshJobs.PeekRunning(workspaceId)?.State);

        gate.SetResult();
        Assert.Equal(43L, (await WaitForCompletedAsync(workspaceId)).Result?.Revision);
    }

    [Fact]
    public async Task PeekRunning_OnceTheJobFinished_ReportsNothingSoPeekStillOwnsTheOutcome()
    {
        string workspaceId = NewWorkspaceId();
        var gate = new TaskCompletionSource();
        DashboardRefreshJobs.Start(workspaceId, GatedRefresh(gate, workspaceId));
        gate.SetResult();
        await WaitAsync(() => DashboardRefreshJobs.PeekRunning(workspaceId) is null);

        Assert.Null(DashboardRefreshJobs.PeekRunning(workspaceId));
        Assert.Equal(43L, DashboardRefreshJobs.Peek(workspaceId)?.Result?.Revision);
    }

    [Fact]
    public async Task PeekRunning_AfterANewRefreshStarts_OutranksTheRetainedOutcomeTheRefetchWouldShow()
    {
        string workspaceId = NewWorkspaceId();
        var first = new TaskCompletionSource();
        DashboardRefreshJobs.Start(workspaceId, GatedRefresh(first, workspaceId));
        first.SetResult();
        await WaitForCompletedAsync(workspaceId);
        Assert.NotNull(DashboardRefreshJobs.PeekLastOutcome(workspaceId));

        var second = new TaskCompletionSource();
        DashboardRefreshJobs.Start(workspaceId, GatedRefresh(second, workspaceId));

        DashboardRefreshJobStatus? rendered =
            DashboardRefreshJobs.PeekRunning(workspaceId) ?? DashboardRefreshJobs.PeekLastOutcome(workspaceId);

        Assert.Equal(DashboardRefreshJobState.Running, rendered?.State);

        second.SetResult();
        await WaitForCompletedAsync(workspaceId);
    }

    private static Func<WorkspaceRefreshResult> GatedRefresh(TaskCompletionSource gate, string workspaceId) =>
        () =>
        {
            gate.Task.GetAwaiter().GetResult();
            return Refreshed(workspaceId);
        };

    private static WorkspaceRefreshResult Refreshed(string workspaceId) =>
        new(
            WorkspaceRefreshStatus.Refreshed,
            workspaceId,
            "/repo/a",
            "/repo/a/.miller/symbols.db",
            Revision: 43,
            Scanned: true);

    private static string NewWorkspaceId() => "ws-jobs-" + Guid.NewGuid().ToString("N");

    private static async Task<DashboardRefreshJobStatus> WaitForCompletedAsync(string workspaceId)
    {
        DashboardRefreshJobStatus? observed = null;
        await WaitAsync(() =>
        {
            observed = DashboardRefreshJobs.Peek(workspaceId);
            return observed is { State: DashboardRefreshJobState.Completed };
        });
        return observed!;
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 3000; attempt++)
        {
            if (condition())
                return;
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("The refresh job never reached the expected state within 30s.");
    }
}
