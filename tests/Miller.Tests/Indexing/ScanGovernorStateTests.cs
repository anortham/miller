using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins the process-local scan-admission position <c>workspace status</c>/<c>health</c> render. It is keyed by
/// WORKSPACE ROOT because one process legitimately waits on workspace A while holding for workspace B, and a
/// single-slot state would report the wrong one. <see cref="ScanGovernorAdmission"/> owns the enter/exit pairing,
/// so a refused or throwing acquire must leave no orphan entry.
/// </summary>
public sealed class ScanGovernorStateTests
{
    private sealed class TempMillerDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "miller-scanstate-" + Guid.NewGuid().ToString("N"));

        public TempMillerDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static ScanGovernorRequest Request(string root, string reason = "test") => new(root, reason, 4);

    [Fact]
    public void WaitingOnOneWorkspaceWhileHoldingAnother_ReportsBothPositions()
    {
        var state = new ScanGovernorState();

        state.EnterWaiting(
            Request("/repo/a", "cross-workspace-refresh"),
            new ScanGovernorOwner(4242, "/repo/b", "leader-ondemand", 4, DateTimeOffset.UtcNow));
        ScanGovernorRequest holdingB = Request("/repo/b", "leader-ondemand");
        state.EnterHolding(state.EnterWaiting(holdingB), holdingB);

        ScanGovernorSnapshot? waiting = state.Snapshot("/repo/a");
        ScanGovernorSnapshot? holding = state.Snapshot("/repo/b");

        Assert.Equal(ScanGovernorStates.Waiting, waiting!.State);
        Assert.Equal("cross-workspace-refresh", waiting.Reason);
        Assert.Equal(4242, waiting.HolderPid);
        Assert.Equal("/repo/b", waiting.HolderWorkspaceRoot);
        Assert.Equal(ScanGovernorStates.Holding, holding!.State);
        Assert.Equal("leader-ondemand", holding.Reason);
        Assert.Null(holding.HolderPid);
    }

    [Fact]
    public void Snapshot_ForAnUntrackedWorkspace_IsNull()
    {
        var state = new ScanGovernorState();

        Assert.Null(state.Snapshot("/repo/never-seen"));
    }

    [Fact]
    public void Exit_ReturnsTheWorkspaceToIdle()
    {
        var state = new ScanGovernorState();
        long id = state.EnterWaiting(Request("/repo/a"));
        state.EnterHolding(id, Request("/repo/a"));

        state.Exit(id, "/repo/a");

        Assert.Null(state.Snapshot("/repo/a"));
    }

    [Fact]
    public void ConcurrentEnterAndExit_LeavesEveryWorkspaceIdle()
    {
        var state = new ScanGovernorState();
        string[] roots = ["/repo/a", "/repo/b", "/repo/c", "/repo/d"];

        Parallel.For(0, 400, i =>
        {
            string root = roots[i % roots.Length];
            long id = state.EnterWaiting(Request(root));
            state.EnterHolding(id, Request(root));
            state.Exit(id, root);
        });

        Assert.All(roots, root => Assert.Null(state.Snapshot(root)));
    }

    [Fact]
    public void Admission_WhenGranted_ReportsHolding_AndReturnsToIdleOnDispose()
    {
        using var dir = new TempMillerDir();
        var state = new ScanGovernorState();
        ScanGovernor governor = ScanGovernor.ForMillerHome(dir.Path);

        ScanGovernorAdmission? admission = ScanGovernorAdmission.TryAcquire(
            governor, state, Request("/repo/a", "leader-startup"), TimeSpan.Zero, CancellationToken.None);

        Assert.NotNull(admission);
        Assert.Equal(ScanGovernorStates.Holding, state.Snapshot("/repo/a")!.State);

        admission!.Dispose();

        Assert.Null(state.Snapshot("/repo/a"));
    }

    [Fact]
    public void Admission_WhenRefused_LeavesNoOrphanEntry()
    {
        using var dir = new TempMillerDir();
        var state = new ScanGovernorState();
        using ScanGovernorLease? held = ScanGovernor.ForMillerHome(dir.Path)
            .TryAcquire(Request("/repo/b"), TimeSpan.Zero, CancellationToken.None);
        Assert.NotNull(held);

        ScanGovernorAdmission? refused = ScanGovernorAdmission.TryAcquire(
            ScanGovernor.ForMillerHome(dir.Path),
            state,
            Request("/repo/a"),
            TimeSpan.Zero,
            CancellationToken.None);

        Assert.Null(refused);
        Assert.Null(state.Snapshot("/repo/a"));
    }

    [Fact]
    public void Admission_WhenTheAcquireThrows_LeavesNoOrphanEntry()
    {
        using var dir = new TempMillerDir();
        var state = new ScanGovernorState();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => ScanGovernorAdmission.TryAcquire(
            ScanGovernor.ForMillerHome(dir.Path),
            state,
            Request("/repo/a"),
            TimeSpan.FromMinutes(30),
            cancellation.Token));

        Assert.Null(state.Snapshot("/repo/a"));
    }

    [Fact]
    public void Admission_WithADisabledGovernor_PublishesNoPosition()
    {
        var state = new ScanGovernorState();

        using ScanGovernorAdmission? admission = ScanGovernorAdmission.TryAcquire(
            ScanGovernor.Disabled(), state, Request("/repo/a"), TimeSpan.Zero, CancellationToken.None);

        Assert.NotNull(admission);
        Assert.Null(state.Snapshot("/repo/a"));
    }

    // ---- F12: two concurrent admissions for ONE root ----
    // The debounce drain and an on-demand TryScanAsLeader can both request admission for the same workspace at
    // once. A single-entry map let the waiter overwrite the holder's entry and its refusal Exit delete the
    // holder's outright, so status reported the machine-wide HOLDER as queued behind itself.

    [Fact]
    public void AWaiterForTheSameRoot_DoesNotOverwriteTheLiveHoldingEntry()
    {
        var state = new ScanGovernorState();
        ScanGovernorRequest holder = Request("/repo/a", "leader-ondemand");
        ScanGovernorRequest waiter = Request("/repo/a", "leader-drain-rescan");
        long holderId = state.EnterWaiting(holder);
        state.EnterHolding(holderId, holder);

        state.EnterWaiting(waiter, new ScanGovernorOwner(4242, "/repo/b", "x", 4, DateTimeOffset.UtcNow));

        ScanGovernorSnapshot? snapshot = state.Snapshot("/repo/a");

        Assert.Equal(ScanGovernorStates.Holding, snapshot!.State);
        Assert.Equal("leader-ondemand", snapshot.Reason);
    }

    [Fact]
    public void ARefusedWaiterExit_DoesNotDeleteTheHoldersEntryForTheSameRoot()
    {
        var state = new ScanGovernorState();
        ScanGovernorRequest holder = Request("/repo/a", "leader-ondemand");
        long holderId = state.EnterWaiting(holder);
        state.EnterHolding(holderId, holder);
        long waiterId = state.EnterWaiting(Request("/repo/a", "leader-drain-rescan"));

        state.Exit(waiterId, "/repo/a");

        Assert.Equal(ScanGovernorStates.Holding, state.Snapshot("/repo/a")!.State);

        state.Exit(holderId, "/repo/a");

        Assert.Null(state.Snapshot("/repo/a"));
    }

    [Fact]
    public void ConcurrentAdmissionsForOneRoot_LeaveItIdleOnceEveryOneExits()
    {
        var state = new ScanGovernorState();
        var ids = new System.Collections.Concurrent.ConcurrentBag<long>();

        Parallel.For(0, 200, _ => ids.Add(state.EnterWaiting(Request("/repo/a"))));

        Assert.Equal(200, ids.Distinct().Count());

        Parallel.ForEach(ids, id => state.Exit(id, "/repo/a"));

        Assert.Null(state.Snapshot("/repo/a"));
    }
}
