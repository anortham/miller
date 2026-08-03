using Miller.Core.Freshness;
using Xunit;

namespace Miller.Tests.Freshness;

/// <summary>
/// Pins the full event→op routing table (spec §Components/1). The router is pure: file existence is
/// injected as a <c>Func&lt;string,bool&gt;</c> stat predicate, so no real file system is touched. Routing:
/// <list type="bullet">
/// <item><c>NeedsRescan</c> or HEAD-changed → a single <see cref="ScanOp"/>, every other event dropped.</item>
/// <item>Created/Modified with exists==true → <see cref="UpdateOp"/>.</item>
/// <item>Deleted, or any event whose affected path no longer exists → <see cref="DeleteOp"/>.</item>
/// <item>Renamed → <see cref="DeleteOp"/>(old) then route the new path through the same exists check.</item>
/// </list>
/// </summary>
public sealed class WatchEventRouterTests
{
    private static readonly Func<string, bool> AllExist = static _ => true;
    private static readonly Func<string, bool> NoneExist = static _ => false;
    private static Func<string, bool> ExistsOnly(params string[] present)
    {
        var set = new HashSet<string>(present, StringComparer.Ordinal);
        return p => set.Contains(p);
    }

    private static UpdateOp AssertUpdate(ExtractOp op)
    {
        var u = Assert.IsType<UpdateOp>(op);
        return u;
    }

    private static DeleteOp AssertDelete(ExtractOp op)
    {
        var d = Assert.IsType<DeleteOp>(op);
        return d;
    }

    [Theory]
    [InlineData(WatchEventKind.Created)]
    [InlineData(WatchEventKind.Modified)]
    public void Route_CreatedOrModified_Exists_EmitsUpdate(WatchEventKind kind)
    {
        var ops = WatchEventRouter.Route(
            new[] { new WatchEvent("/repo/a.cs", kind) }, AllExist, wholeRepoScan: null);

        var op = Assert.Single(ops);
        Assert.Equal("/repo/a.cs", AssertUpdate(op).Path);
    }

    [Theory]
    [InlineData(WatchEventKind.Created)]
    [InlineData(WatchEventKind.Modified)]
    public void Route_CreatedOrModified_NotExists_DegradesToDelete(WatchEventKind kind)
    {
        // The file was created/modified then vanished before the drain (a race). Routing it as Update
        // would make julie no-op or error on a missing file; the affected path is gone => Delete.
        var ops = WatchEventRouter.Route(
            new[] { new WatchEvent("/repo/a.cs", kind) }, NoneExist, wholeRepoScan: null);

        var op = Assert.Single(ops);
        Assert.Equal("/repo/a.cs", AssertDelete(op).Path);
    }

    [Fact]
    public void Route_Deleted_EmitsDelete_RegardlessOfExists()
    {
        // A Deleted event always deletes even if the stat (racily) reports the path back; the index
        // entry must be removed to match the observed deletion.
        var ops = WatchEventRouter.Route(
            new[] { new WatchEvent("/repo/a.cs", WatchEventKind.Deleted) }, AllExist, wholeRepoScan: null);

        var op = Assert.Single(ops);
        Assert.Equal("/repo/a.cs", AssertDelete(op).Path);
    }

    [Fact]
    public void Route_Renamed_NewExists_EmitsDeleteOldThenUpdateNew_InOrder()
    {
        var ops = WatchEventRouter.Route(
            new[] { WatchEvent.Renamed("/repo/old.cs", "/repo/new.cs") },
            ExistsOnly("/repo/new.cs"), wholeRepoScan: null);

        Assert.Equal(2, ops.Count);
        Assert.Equal("/repo/old.cs", AssertDelete(ops[0]).Path); // delete the source first
        Assert.Equal("/repo/new.cs", AssertUpdate(ops[1]).Path); // then index the destination
    }

    [Fact]
    public void Route_Renamed_NewMissing_EmitsDeleteOldThenDeleteNew()
    {
        // Rename target also vanished (rename-away then removed). Both paths route to Delete.
        var ops = WatchEventRouter.Route(
            new[] { WatchEvent.Renamed("/repo/old.cs", "/repo/new.cs") },
            NoneExist, wholeRepoScan: null);

        Assert.Equal(2, ops.Count);
        Assert.Equal("/repo/old.cs", AssertDelete(ops[0]).Path);
        Assert.Equal("/repo/new.cs", AssertDelete(ops[1]).Path);
    }

    [Fact]
    public void Route_NeedsRescan_EmitsSingleScan_AndDropsEverythingElse()
    {
        var events = new[]
        {
            new WatchEvent("/repo/a.cs", WatchEventKind.Created),
            new WatchEvent("/repo/b.cs", WatchEventKind.Deleted),
            WatchEvent.Renamed("/repo/c.cs", "/repo/d.cs"),
        };

        var ops = WatchEventRouter.Route(events, AllExist, ScanOp.Instance);

        var op = Assert.Single(ops);
        Assert.IsType<ScanOp>(op);
    }

    [Fact]
    public void Route_HeadChanged_EmitsSingleScan_EvenWithNoEvents()
    {
        // .git/HEAD change forces a reconcile via the same flag; no per-file events needed.
        var ops = WatchEventRouter.Route(
            Array.Empty<WatchEvent>(), AllExist, ScanOp.Instance);

        Assert.IsType<ScanOp>(Assert.Single(ops));
    }

    [Fact]
    public void Route_EmptyEvents_NoRescan_EmitsNothing()
    {
        Assert.Empty(WatchEventRouter.Route(Array.Empty<WatchEvent>(), AllExist, wholeRepoScan: null));
    }

    [Theory]
    [InlineData(ScanIntent.IncrementalReconcile, false)]
    [InlineData(ScanIntent.UserFullRebuild, true)]
    [InlineData(ScanIntent.RootRebind, true)]
    [InlineData(ScanIntent.SchemaHeal, true)]
    [InlineData(ScanIntent.CorruptionHeal, true)]
    [InlineData(ScanIntent.ExtractorUpgrade, true)]
    public void Route_EmitsTheSuppliedScanOpVerbatim(ScanIntent intent, bool expectedForce)
    {
        var ops = WatchEventRouter.Route(
            Array.Empty<WatchEvent>(), AllExist, ScanOp.For(intent, jobs: 1));

        var scan = Assert.IsType<ScanOp>(Assert.Single(ops));
        Assert.Equal(intent, scan.Intent);
        Assert.Equal(expectedForce, scan.Force);
        Assert.Equal(1, scan.Jobs);
    }

    [Fact]
    public void Route_MixedBatch_ProducesOpsInEventOrder()
    {
        var events = new[]
        {
            new WatchEvent("/repo/keep.cs", WatchEventKind.Modified),  // exists -> Update
            new WatchEvent("/repo/gone.cs", WatchEventKind.Deleted),   // -> Delete
            WatchEvent.Renamed("/repo/from.cs", "/repo/to.cs"),        // -> Delete(from) + Update(to)
        };
        var exists = ExistsOnly("/repo/keep.cs", "/repo/to.cs");

        var ops = WatchEventRouter.Route(events, exists, wholeRepoScan: null);

        Assert.Equal(4, ops.Count);
        Assert.Equal("/repo/keep.cs", AssertUpdate(ops[0]).Path);
        Assert.Equal("/repo/gone.cs", AssertDelete(ops[1]).Path);
        Assert.Equal("/repo/from.cs", AssertDelete(ops[2]).Path);
        Assert.Equal("/repo/to.cs", AssertUpdate(ops[3]).Path);
    }

    [Fact]
    public void Route_NullEvents_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            WatchEventRouter.Route(null!, AllExist, wholeRepoScan: null));
    }

    [Fact]
    public void Route_NullExistsPredicate_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            WatchEventRouter.Route(Array.Empty<WatchEvent>(), null!, wholeRepoScan: null));
    }

    [Fact]
    public void ExtractOps_ExposeValueEquality()
    {
        // Ops are compared in tests and dedup logic; pin record value-equality + path payloads.
        Assert.Equal(new UpdateOp("/x"), new UpdateOp("/x"));
        Assert.Equal(new DeleteOp("/x"), new DeleteOp("/x"));
        Assert.NotEqual<ExtractOp>(new UpdateOp("/x"), new DeleteOp("/x"));
        Assert.Equal(ScanOp.Instance, ScanOp.Instance);
    }
}
