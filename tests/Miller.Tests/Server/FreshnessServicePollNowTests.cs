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
        string tempHome = Path.Combine(Path.GetTempPath(), "miller-freshness-home-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempHome);
        var bootstrap = new IndexBootstrapService(NullLogger<IndexBootstrapService>.Instance);
        bootstrap.TestHomeDirectoryOverride = tempHome;
        var workspace = WorkspaceContext.Create(Path.GetDirectoryName(dbPath)!, AppContext.BaseDirectory, tempHome) with
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
            revisions: new[] { new JulieDbFixture.RevisionRow(5) });
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
            revisions: new[] { new JulieDbFixture.RevisionRow(4) });
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
            revisions: new[] { new JulieDbFixture.RevisionRow(9) });
        var holder = HolderAt(fx, builtRevision: 2);
        var service = NewServiceOverDb(fx.DbPath, Ws, holder);

        PollResult result = service.PollNow(); // no exception, real transient read

        Assert.True(result.Swapped);
        Assert.Equal(9, result.Revision);
        Assert.Equal(9, holder.BuiltRevision);
    }

    [Fact]
    public void PollNow_ArtifactFileReplacedWithARestartedRevisionCounter_StillSwaps()
    {
        // The full-rebuild promote (FullRebuildPromotion, 2026-06-11 Eros field report #2): a force scan
        // REPLACES symbols.db with a fresh artifact whose revision counter restarts — here landing BELOW the
        // held revision, where the historical revision-only rule would keep serving the pre-rebuild index
        // forever. The poll must detect the replacement via the changed artifact_id. The transient per-poll
        // reader is load-bearing too: a long-lived connection would still be reading the OLD unlinked inode.
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, JulieDbFixture.DefaultRows, workspaceId: Ws,
            revisions: new[] { new JulieDbFixture.RevisionRow(7) });
        var holder = new IndexHolder(
            MillerRepositoryIndex.Build(SqliteSymbolReader.Read(fx.DbPath)), builtRevision: 7,
            builtArtifactId: "artifact-" + Ws); // the fixture's stamped id — what the bootstrap seeds
        var service = NewServiceOverDb(fx.DbPath, Ws, holder);

        // Replace the file wholesale, as a promote does: fresh artifact, NEW artifact_id, counter back at 1.
        string dbPath = fx.DbPath;
        using (var rebuilt = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, JulieDbFixture.DefaultRows,
            workspaceId: "ws-pollnow-rebuilt",
            revisions: new[] { new JulieDbFixture.RevisionRow(1) }))
        {
            File.Copy(rebuilt.DbPath, dbPath, overwrite: true);
        }

        PollResult result = service.PollNow();

        Assert.True(result.Swapped);
        Assert.Equal(1, result.Revision);
        Assert.Equal(1, holder.BuiltRevision);
        Assert.Equal("artifact-ws-pollnow-rebuilt", holder.BuiltArtifactId); // the held identity follows the swap
    }

    [Fact]
    public void PollNow_NoWorkspaceId_ReturnsNotSwapped()
    {
        // No workspace_id => no revision cursor to poll (a never-scanned/static extract). PollNow must honestly
        // report not-swapped at the held revision rather than fabricate a convergence.
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
