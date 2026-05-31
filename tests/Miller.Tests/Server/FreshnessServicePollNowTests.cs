using System;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Miller.Indexing;
using Miller.Server;
using Miller.Server.Hosting;
using Miller.Tests.Indexing;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins <see cref="FreshnessService.PollNow"/> (M7 decision-3): the on-demand poll-then-swap behind
/// <c>workspace refresh/full</c>, which runs the existing poll once NOW (so the caller sees the result
/// immediately instead of up to one 2s tick later) and returns a typed <see cref="PollResult"/>. It is exercised
/// against a real synthesized extract DB (the production read path — <see cref="FreshnessReader"/> +
/// <see cref="IndexRebuilder"/> + <see cref="FreshnessPoller"/>), WITHOUT the timer-driven hosted shell, so the
/// on-demand trigger is deterministic. Covers: a newer persisted revision swaps + reports it; an equal revision
/// is a no-op; a never-initialised service (the host loop never started) still polls via a transient reader and
/// does NOT throw; and a workspace with no <c>workspace_id</c> (no revision cursor) returns not-swapped.
/// <c>PollNow</c> is the on-demand seam the hosted loop and the tool share; the live timer path is the Scale
/// suite (<see cref="LiveFreshnessTests"/>).
/// </summary>
public sealed class FreshnessServicePollNowTests
{
    private const string Ws = "ws-pollnow-001";

    // A never-started FreshnessService (the host loop's ExecuteAsync never ran), constructed over a bootstrap
    // whose workspace points at the supplied DB. PollNow must build a transient reader/rebuilder from the
    // workspace and poll without requiring the loop to have initialised _reader/_rebuilder.
    private static FreshnessService NewServiceOverDb(string dbPath, string? workspaceId, IndexHolder holder)
    {
        var bootstrap = new IndexBootstrapService(NullLogger<IndexBootstrapService>.Instance);
        var workspace = WorkspaceContext.Create(Path.GetDirectoryName(dbPath)!, AppContext.BaseDirectory) with
        {
            ExtractDbPath = dbPath,
            WorkspaceId = workspaceId,
        };
        bootstrap.SeedForTest(workspace, holder);
        return new FreshnessService(bootstrap, NullLogger<FreshnessService>.Instance);
    }

    private static IndexHolder HolderAt(JulieDbFixture fx, long builtRevision) =>
        new(MillerRepositoryIndex.Build(SqliteSymbolReader.Read(fx.DbPath)), builtRevision);

    [Fact]
    public void PollNow_NewerRevisionPersisted_SwapsAndReportsTheNewRevision()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, JulieDbFixture.DefaultRows, workspaceId: Ws,
            revisions: new[] { new JulieDbFixture.RevisionRow(5, Ws) });
        var holder = HolderAt(fx, builtRevision: 1); // the writer has moved to 5, the held index is at 1
        var service = NewServiceOverDb(fx.DbPath, Ws, holder);

        PollResult result = service.PollNow();

        Assert.True(result.Swapped);
        Assert.Equal(5, result.Revision);
        Assert.Equal(5, holder.BuiltRevision); // the index was rebuilt + swapped to the persisted revision
    }

    [Fact]
    public void PollNow_EqualRevision_IsANoOp()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, JulieDbFixture.DefaultRows, workspaceId: Ws,
            revisions: new[] { new JulieDbFixture.RevisionRow(4, Ws) });
        var holder = HolderAt(fx, builtRevision: 4); // already current
        var service = NewServiceOverDb(fx.DbPath, Ws, holder);
        var beforeIndex = holder.Current;

        PollResult result = service.PollNow();

        Assert.False(result.Swapped);
        Assert.Equal(4, result.Revision);
        Assert.Same(beforeIndex, holder.Current); // no churn while the writer is idle
    }

    [Fact]
    public void PollNow_OnANeverStartedService_DoesNotThrow_AndConverges()
    {
        // The host loop's ExecuteAsync never ran (so _reader/_rebuilder are null). PollNow must build a transient
        // reader/rebuilder from the workspace and still converge — never throw into the tool.
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, JulieDbFixture.DefaultRows, workspaceId: Ws,
            revisions: new[] { new JulieDbFixture.RevisionRow(9, Ws) });
        var holder = HolderAt(fx, builtRevision: 2);
        var service = NewServiceOverDb(fx.DbPath, Ws, holder);

        PollResult result = service.PollNow(); // no exception, real transient read

        Assert.True(result.Swapped);
        Assert.Equal(9, result.Revision);
        Assert.Equal(9, holder.BuiltRevision);
    }

    [Fact]
    public void DisposeOfReader_SerializesAgainstAnInFlightPoll_NeverClosesUnderAQuery()
    {
        // The dispose-vs-read race fix: ExecuteAsync's shutdown disposal of the single-connection reader must run
        // UNDER _pollGate, the same lock PollNow holds while it drives that reader. Otherwise a shutdown can close
        // the SqliteConnection out from under an in-flight query. We stand in for an in-flight poll by holding the
        // gate (RunUnderPollGateForTest) and assert a concurrent disposal cannot complete until the gate releases.
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, JulieDbFixture.DefaultRows, workspaceId: Ws,
            revisions: new[] { new JulieDbFixture.RevisionRow(1, Ws) });
        var holder = HolderAt(fx, builtRevision: 1);
        var service = NewServiceOverDb(fx.DbPath, Ws, holder);
        service.InitReaderForTest();

        var pollHoldsGate = new ManualResetEventSlim(false);
        var releaseGate = new ManualResetEventSlim(false);
        var disposeReturned = new ManualResetEventSlim(false);
        bool gateStillHeldWhenDisposeReturned = false;

        var pollThread = new Thread(() => service.RunUnderPollGateForTest(() =>
        {
            pollHoldsGate.Set();      // we now hold _pollGate, as an in-flight PollNow would
            releaseGate.Wait();       // keep holding it until the test lets go
        }));
        pollThread.Start();
        // These waits are the test's own deliberate deadlines, not cancellation points; pass CancellationToken.None
        // explicitly (matching the disposeReturned waits below) so the xUnit1051 analyzer is satisfied.
        Assert.True(pollHoldsGate.Wait(5000, CancellationToken.None));

        var disposeThread = new Thread(() =>
        {
            service.DisposeReaderForTest();
            // Capture, at the instant the dispose returns, whether the poll still held the gate. With the fix the
            // dispose blocks on _pollGate until releaseGate is set, so this reads false; the buggy (lock-free)
            // disposal returns immediately and reads true (it closed the reader mid-poll — the race).
            gateStillHeldWhenDisposeReturned = !releaseGate.IsSet;
            disposeReturned.Set();
        });
        disposeThread.Start();

        // The dispose must NOT complete while the poll still holds the gate. If it signals within this window, the
        // serialization is missing (the race). 300ms is ample for the lock-free disposal to return.
        Assert.False(disposeReturned.Wait(300, CancellationToken.None),
            "dispose completed while a poll held _pollGate — it closed the reader mid-query (the race).");

        releaseGate.Set();
        pollThread.Join();
        Assert.True(disposeReturned.Wait(5000, CancellationToken.None));
        disposeThread.Join();
        Assert.False(gateStillHeldWhenDisposeReturned);
    }

    [Fact]
    public void PollNow_NoWorkspaceId_ReturnsNotSwapped()
    {
        // No workspace_id => no canonical_revisions cursor to poll (a never-scanned/static extract). PollNow must
        // honestly report not-swapped at the held revision rather than fabricate a convergence.
        using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, JulieDbFixture.DefaultRows); // no workspaceId, no revisions
        var holder = HolderAt(fx, builtRevision: 3);
        var service = NewServiceOverDb(fx.DbPath, workspaceId: null, holder);
        var beforeIndex = holder.Current;

        PollResult result = service.PollNow();

        Assert.False(result.Swapped);
        Assert.Equal(3, result.Revision); // the held built revision — nothing to converge on
        Assert.Same(beforeIndex, holder.Current); // no churn — the held index was not rebuilt/swapped
    }
}
