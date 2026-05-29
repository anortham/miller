using Miller.Core.Freshness;
using Xunit;

namespace Miller.Tests.Freshness;

/// <summary>
/// Pins the per-path coalescing merge state machine and the bounded-overflow drain, ported from
/// julie's <c>src/watcher/queue.rs</c> (the authoritative source). Every merge transition in julie's
/// table is asserted on the resulting <see cref="WatchEventKind"/>, plus the overflow contract:
/// a NEW distinct path that would exceed <see cref="WatchEventQueue.MaxQueue"/> drains the queue down
/// to <see cref="WatchEventQueue.OverflowTarget"/> from the front and sets <see cref="WatchEventQueue.NeedsRescan"/>.
/// Coalescing merges (same path) never consume a slot and never overflow.
/// </summary>
public sealed class WatchEventQueueTests
{
    private static WatchEvent Ev(string path, WatchEventKind kind) => new(path, kind);

    // ---- merge state machine (julie's merge_file_change table) ----

    [Theory]
    // (existing, incoming) -> coalesced kind
    [InlineData(WatchEventKind.Modified, WatchEventKind.Modified, WatchEventKind.Modified)]  // idempotent modify
    [InlineData(WatchEventKind.Created, WatchEventKind.Modified, WatchEventKind.Created)]     // create then edit = still create
    [InlineData(WatchEventKind.Deleted, WatchEventKind.Created, WatchEventKind.Modified)]     // delete then recreate = modify
    [InlineData(WatchEventKind.Deleted, WatchEventKind.Modified, WatchEventKind.Modified)]    // delete then modify = modify
    [InlineData(WatchEventKind.Created, WatchEventKind.Deleted, WatchEventKind.Deleted)]      // create then delete = delete
    // last-write-wins fallthrough cases:
    [InlineData(WatchEventKind.Modified, WatchEventKind.Created, WatchEventKind.Created)]
    [InlineData(WatchEventKind.Modified, WatchEventKind.Deleted, WatchEventKind.Deleted)]
    [InlineData(WatchEventKind.Created, WatchEventKind.Created, WatchEventKind.Created)]
    [InlineData(WatchEventKind.Deleted, WatchEventKind.Deleted, WatchEventKind.Deleted)]
    public void Enqueue_SamePath_CoalescesPerMergeTable(
        WatchEventKind existing, WatchEventKind incoming, WatchEventKind expected)
    {
        var queue = new WatchEventQueue();
        queue.Enqueue(Ev("/repo/a.cs", existing));
        queue.Enqueue(Ev("/repo/a.cs", incoming));

        var drained = queue.Drain();

        var only = Assert.Single(drained);
        Assert.Equal("/repo/a.cs", only.Path);
        Assert.Equal(expected, only.Kind);
    }

    [Fact]
    public void Enqueue_RenamedThenModified_KeepsRenameAndOldPath()
    {
        // julie: (Renamed{from,to}, Modified) -> Renamed{from,to}. The rename must survive a trailing
        // modify so the router still emits Delete(old)+Update(new).
        var queue = new WatchEventQueue();
        queue.Enqueue(WatchEvent.Renamed("/repo/old.cs", "/repo/new.cs"));
        queue.Enqueue(Ev("/repo/new.cs", WatchEventKind.Modified)); // keyed on the affected (new) path

        var only = Assert.Single(queue.Drain());
        Assert.Equal(WatchEventKind.Renamed, only.Kind);
        Assert.Equal("/repo/new.cs", only.Path);
        Assert.Equal("/repo/old.cs", only.OldPath);
    }

    [Fact]
    public void Enqueue_RenamedThenDeleted_FallsThroughToDeleted()
    {
        // Not in julie's explicit arms -> last-write-wins => Deleted (the new path is gone again).
        var queue = new WatchEventQueue();
        queue.Enqueue(WatchEvent.Renamed("/repo/old.cs", "/repo/new.cs"));
        queue.Enqueue(Ev("/repo/new.cs", WatchEventKind.Deleted));

        var only = Assert.Single(queue.Drain());
        Assert.Equal(WatchEventKind.Deleted, only.Kind);
        Assert.Equal("/repo/new.cs", only.Path);
    }

    [Fact]
    public void Enqueue_RenamedCoalescesOnAffectedNewPath()
    {
        // affected_path of a Renamed event is the `to` path; a Renamed is keyed there for coalescing.
        var queue = new WatchEventQueue();
        queue.Enqueue(Ev("/repo/dest.cs", WatchEventKind.Modified));
        queue.Enqueue(WatchEvent.Renamed("/repo/src.cs", "/repo/dest.cs")); // same affected path -> merges

        var only = Assert.Single(queue.Drain());
        // (Modified, Renamed) is a fallthrough -> last-write-wins = Renamed.
        Assert.Equal(WatchEventKind.Renamed, only.Kind);
        Assert.Equal("/repo/dest.cs", only.Path);
        Assert.Equal("/repo/src.cs", only.OldPath);
    }

    [Fact]
    public void Enqueue_DistinctPaths_DoNotCoalesce_AndPreserveFifoOrder()
    {
        var queue = new WatchEventQueue();
        queue.Enqueue(Ev("/repo/a.cs", WatchEventKind.Created));
        queue.Enqueue(Ev("/repo/b.cs", WatchEventKind.Modified));
        queue.Enqueue(Ev("/repo/c.cs", WatchEventKind.Deleted));

        var drained = queue.Drain();
        Assert.Equal(3, drained.Count);
        Assert.Equal(new[] { "/repo/a.cs", "/repo/b.cs", "/repo/c.cs" }, drained.Select(e => e.Path).ToArray());
    }

    [Fact]
    public void Enqueue_MergeUpdatesInPlace_WithoutMovingQueuePosition()
    {
        // julie merges the most-recent matching entry in place (rposition + index assignment), so a
        // later modify of an earlier path must NOT reorder it behind newer distinct paths.
        var queue = new WatchEventQueue();
        queue.Enqueue(Ev("/repo/a.cs", WatchEventKind.Created));
        queue.Enqueue(Ev("/repo/b.cs", WatchEventKind.Created));
        queue.Enqueue(Ev("/repo/a.cs", WatchEventKind.Modified)); // merges into a.cs's existing slot

        var drained = queue.Drain();
        Assert.Equal(2, drained.Count);
        Assert.Equal(new[] { "/repo/a.cs", "/repo/b.cs" }, drained.Select(e => e.Path).ToArray());
        Assert.Equal(WatchEventKind.Created, drained[0].Kind); // (Created,Modified)->Created
    }

    [Fact]
    public void Drain_EmptiesTheQueue_SecondDrainIsEmpty()
    {
        var queue = new WatchEventQueue();
        queue.Enqueue(Ev("/repo/a.cs", WatchEventKind.Created));

        Assert.Single(queue.Drain());
        Assert.Empty(queue.Drain());
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void Count_ReflectsDistinctPaths_NotEventVolume()
    {
        var queue = new WatchEventQueue();
        queue.Enqueue(Ev("/repo/a.cs", WatchEventKind.Created));
        queue.Enqueue(Ev("/repo/a.cs", WatchEventKind.Modified)); // coalesces, no new slot
        queue.Enqueue(Ev("/repo/b.cs", WatchEventKind.Created));

        Assert.Equal(2, queue.Count);
    }

    // ---- bounded overflow drain ----

    [Fact]
    public void Defaults_MatchJulieConstants()
    {
        Assert.Equal(1000, WatchEventQueue.MaxQueue);
        Assert.Equal(750, WatchEventQueue.OverflowTarget);
    }

    [Fact]
    public void Enqueue_BelowCap_DoesNotSetNeedsRescan()
    {
        var queue = new WatchEventQueue();
        for (int i = 0; i < WatchEventQueue.MaxQueue; i++) // exactly fills to MaxQueue, no overflow yet
            queue.Enqueue(Ev($"/repo/f{i}.cs", WatchEventKind.Created));

        Assert.Equal(WatchEventQueue.MaxQueue, queue.Count);
        Assert.False(queue.NeedsRescan);
    }

    [Fact]
    public void Enqueue_AtCap_NewDistinctPath_DrainsToTarget_AndSetsNeedsRescan()
    {
        var queue = new WatchEventQueue();
        for (int i = 0; i < WatchEventQueue.MaxQueue; i++)
            queue.Enqueue(Ev($"/repo/f{i}.cs", WatchEventKind.Created));
        Assert.False(queue.NeedsRescan);

        // The 1001st distinct path triggers the drain: front entries popped until count == OverflowTarget,
        // then the new one is pushed.
        queue.Enqueue(Ev("/repo/overflow.cs", WatchEventKind.Created));

        Assert.True(queue.NeedsRescan);
        Assert.Equal(WatchEventQueue.OverflowTarget + 1, queue.Count); // drained to 750, then +1 pushed
    }

    [Fact]
    public void Enqueue_OverflowDropsOldestFromFront_KeepsNewest()
    {
        var queue = new WatchEventQueue();
        for (int i = 0; i < WatchEventQueue.MaxQueue; i++)
            queue.Enqueue(Ev($"/repo/f{i}.cs", WatchEventKind.Created));

        queue.Enqueue(Ev("/repo/newest.cs", WatchEventKind.Created));

        var drained = queue.Drain();
        // Oldest dropped: f0..f249 (250 popped to go 1000 -> 750). f250 is the new front.
        Assert.DoesNotContain(drained, e => e.Path == "/repo/f0.cs");
        Assert.DoesNotContain(drained, e => e.Path == "/repo/f249.cs");
        Assert.Contains(drained, e => e.Path == "/repo/f250.cs");
        Assert.Contains(drained, e => e.Path == "/repo/newest.cs");
        Assert.Equal("/repo/newest.cs", drained[^1].Path); // newest is last (FIFO tail)
    }

    [Fact]
    public void Enqueue_AtCap_CoalescingExistingPath_DoesNotOverflow()
    {
        // A merge into an existing path consumes no slot, so it must NOT trigger the overflow drain
        // even when the queue is full (julie returns drained:0 on the merge branch).
        var queue = new WatchEventQueue();
        for (int i = 0; i < WatchEventQueue.MaxQueue; i++)
            queue.Enqueue(Ev($"/repo/f{i}.cs", WatchEventKind.Created));

        queue.Enqueue(Ev("/repo/f0.cs", WatchEventKind.Modified)); // existing path -> merge, no drain

        Assert.False(queue.NeedsRescan);
        Assert.Equal(WatchEventQueue.MaxQueue, queue.Count);
    }

    [Fact]
    public void NeedsRescan_IsStickyUntilCleared()
    {
        var queue = new WatchEventQueue();
        for (int i = 0; i < WatchEventQueue.MaxQueue; i++)
            queue.Enqueue(Ev($"/repo/f{i}.cs", WatchEventKind.Created));
        queue.Enqueue(Ev("/repo/overflow.cs", WatchEventKind.Created));
        Assert.True(queue.NeedsRescan);

        // Draining the queue does not by itself clear the rescan flag (the router consumes it).
        queue.Drain();
        Assert.True(queue.NeedsRescan);

        queue.ClearNeedsRescan();
        Assert.False(queue.NeedsRescan);
    }

    [Fact]
    public void Enqueue_NullEvent_Throws()
    {
        var queue = new WatchEventQueue();
        Assert.Throws<ArgumentNullException>(() => queue.Enqueue(null!));
    }
}
