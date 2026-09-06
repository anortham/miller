using Miller.Core.Resolution;
using Miller.Indexing.Resolution;
using Miller.Tests.Support;
using Xunit;

namespace Miller.Tests.Indexing.Resolution;

public sealed class RevisionFactCacheLeaseTests
{
    [Fact]
    public void Acquire_HoldingLeaseThroughLruEviction_KeepsCacheUsableAndCountedAsEvictedHeld()
    {
        using ResolutionStoreFixture fixtureA = ResolutionStoreFixture.Create();
        fixtureA.AddFile(1, "a.cs");
        fixtureA.AddSymbol(1, "sym-a", "Alpha", "class", "a.cs");

        using ResolutionStoreFixture fixtureB = ResolutionStoreFixture.Create();
        fixtureB.AddFile(1, "b.cs");
        fixtureB.AddSymbol(1, "sym-b", "Beta", "class", "b.cs");

        var store = new RevisionFactCacheStore(byteBudget: 1);

        RevisionFactCacheLease leaseA = store.Acquire("ws-a", "rev-1", fixtureA.OpenRead, fixtureA.Visibility());
        Assert.Equal("ws-a", leaseA.Scope);
        Assert.Equal("rev-1", leaseA.Identity);
        Assert.NotNull(leaseA.Cache);

        CacheResourceSnapshot snapshot1 = store.GetResourceSnapshot();
        Assert.Equal(1, snapshot1.RetainedEntryCount);
        Assert.Equal(1, snapshot1.ActiveLeaseCount);
        Assert.Equal(0, snapshot1.EvictedHeldEntryCount);
        Assert.Equal(1, snapshot1.UniqueLiveEntryCount);

        // Evict ws-a by acquiring ws-b with byteBudget: 1
        RevisionFactCacheLease leaseB = store.Acquire("ws-b", "rev-1", fixtureB.OpenRead, fixtureB.Visibility());
        Assert.Equal(1, store.ScopeCount);

        CacheResourceSnapshot snapshot2 = store.GetResourceSnapshot();
        Assert.Equal(1, snapshot2.RetainedEntryCount);
        Assert.Equal(2, snapshot2.ActiveLeaseCount);
        Assert.Equal(1, snapshot2.EvictedHeldEntryCount);
        Assert.Equal(leaseA.Cache.ResidentBytes, snapshot2.EvictedHeldBytes);
        Assert.Equal(2, snapshot2.UniqueLiveEntryCount);
        Assert.Equal(snapshot2.RetainedBytes + snapshot2.EvictedHeldBytes, snapshot2.UniqueLiveBytes);

        // Evicted cache A remains fully usable
        FactSymbol symbolA = Assert.Single(leaseA.Cache.SymbolsNamed("Alpha"));
        Assert.Equal("Alpha", symbolA.Name);

        // Disposing lease A clears evicted-held count
        leaseA.Dispose();
        CacheResourceSnapshot snapshot3 = store.GetResourceSnapshot();
        Assert.Equal(1, snapshot3.RetainedEntryCount);
        Assert.Equal(1, snapshot3.ActiveLeaseCount);
        Assert.Equal(0, snapshot3.EvictedHeldEntryCount);
        Assert.Equal(0L, snapshot3.EvictedHeldBytes);
        Assert.Equal(1, snapshot3.UniqueLiveEntryCount);

        leaseB.Dispose();
        CacheResourceSnapshot snapshot4 = store.GetResourceSnapshot();
        Assert.Equal(1, snapshot4.RetainedEntryCount);
        Assert.Equal(0, snapshot4.ActiveLeaseCount);
        Assert.Equal(0, snapshot4.EvictedHeldEntryCount);
    }

    [Fact]
    public void Acquire_SwitchingRevision_InstallsNewEntryAndOldLeaseRemainsUsable()
    {
        using ResolutionStoreFixture fixture = ResolutionStoreFixture.Create();
        fixture.AddFile(1, "keep.cs");
        fixture.AddFile(2, "change.cs");
        fixture.AddSymbol(1, "kept", "Kept", "class", "keep.cs");
        fixture.AddSymbol(2, "old", "Old", "class", "change.cs");

        var store = new RevisionFactCacheStore();
        RevisionFactCacheLease leaseRev1 = store.Acquire("ws-a", "rev-1", fixture.OpenRead, fixture.Visibility());

        fixture.AddSymbol(3, "neu", "New", "class", "change.cs");
        fixture.FlipManifest(2, [("keep.cs", 1, "csharp", "indexed"), ("change.cs", 3, "csharp", "indexed")]);

        RevisionFactCacheLease leaseRev2 = store.Acquire("ws-a", "rev-2", fixture.OpenRead, fixture.Visibility());
        Assert.NotSame(leaseRev1.Cache, leaseRev2.Cache);
        Assert.Equal(1, store.ScopeCount);

        CacheResourceSnapshot snapshot = store.GetResourceSnapshot();
        Assert.Equal(1, snapshot.RetainedEntryCount);
        Assert.Equal(2, snapshot.ActiveLeaseCount);
        Assert.Equal(1, snapshot.EvictedHeldEntryCount);
        Assert.Equal(2, snapshot.UniqueLiveEntryCount);

        Assert.Single(leaseRev1.Cache.SymbolsNamed("Old"));
        Assert.Empty(leaseRev1.Cache.SymbolsNamed("New"));
        Assert.Single(leaseRev2.Cache.SymbolsNamed("New"));

        leaseRev1.Dispose();
        CacheResourceSnapshot after = store.GetResourceSnapshot();
        Assert.Equal(0, after.EvictedHeldEntryCount);
        Assert.Equal(1, after.ActiveLeaseCount);
        Assert.Equal(1, after.UniqueLiveEntryCount);

        leaseRev2.Dispose();
    }

    [Fact]
    public void Acquire_DoubleDispose_DecrementsActiveCountOnlyOnce()
    {
        using ResolutionStoreFixture fixture = ResolutionStoreFixture.Create();
        fixture.AddFile(1, "a.cs");
        fixture.AddSymbol(1, "a", "Alpha", "class", "a.cs");

        var store = new RevisionFactCacheStore();
        RevisionFactCacheLease lease = store.Acquire("ws-a", "rev-1", fixture.OpenRead, fixture.Visibility());

        CacheResourceSnapshot s1 = store.GetResourceSnapshot();
        Assert.Equal(1, s1.ActiveLeaseCount);

        lease.Dispose();
        CacheResourceSnapshot s2 = store.GetResourceSnapshot();
        Assert.Equal(0, s2.ActiveLeaseCount);

        lease.Dispose();
        lease.Dispose();
        CacheResourceSnapshot s3 = store.GetResourceSnapshot();
        Assert.Equal(0, s3.ActiveLeaseCount);
    }

    [Fact]
    public void Acquire_ThrowInLazyConstruction_CleansUpScopeAndLeaksNoLease()
    {
        using ResolutionStoreFixture fixture = ResolutionStoreFixture.Create();
        var store = new RevisionFactCacheStore();

        Assert.Throws<InvalidOperationException>(() =>
            store.Acquire(
                "ws-fail",
                "rev-1",
                () => throw new InvalidOperationException("open failed"),
                fixture.Visibility()));

        CacheResourceSnapshot snapshot = store.GetResourceSnapshot();
        Assert.Equal(0, snapshot.RetainedEntryCount);
        Assert.Equal(0, snapshot.ActiveLeaseCount);
        Assert.Equal(0, snapshot.UniqueLiveEntryCount);
        Assert.Equal(0, store.ScopeCount);
    }

    [Fact]
    public async Task Acquire_StaleLoaderCannotOverwriteNewerScope()
    {
        using ResolutionStoreFixture fixture = ResolutionStoreFixture.Create();
        fixture.AddFile(1, "a.cs");
        fixture.AddSymbol(1, "a", "Alpha", "class", "a.cs");

        var store = new RevisionFactCacheStore();
        using var rev1Blocker = new ManualResetEventSlim(initialState: false);
        using var rev1Started = new ManualResetEventSlim(initialState: false);

        Task<RevisionFactCacheLease> rev1Task = Task.Run(() =>
            store.Acquire(
                "ws-race",
                "rev-1",
                () =>
                {
                    rev1Started.Set();
                    rev1Blocker.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                    return fixture.OpenRead();
                },
                fixture.Visibility()));

        rev1Started.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // While rev-1 is in flight, install rev-2
        fixture.AddSymbol(2, "b", "Beta", "class", "a.cs");
        RevisionFactCacheLease leaseRev2 = store.Acquire(
            "ws-race",
            "rev-2",
            fixture.OpenRead,
            fixture.Visibility());

        Assert.Equal("rev-2", leaseRev2.Identity);

        // Unblock rev-1
        rev1Blocker.Set();
        RevisionFactCacheLease leaseRev1 = await rev1Task;

        Assert.Equal("rev-1", leaseRev1.Identity);
        Assert.NotSame(leaseRev1.Cache, leaseRev2.Cache);

        // The store's retained scope must STILL be rev-2!
        Assert.True(store.IsWarm("ws-race", "rev-2"));

        CacheResourceSnapshot snapshot = store.GetResourceSnapshot();
        Assert.Equal(1, snapshot.RetainedEntryCount);
        Assert.Equal(2, snapshot.ActiveLeaseCount);
        Assert.Equal(1, snapshot.EvictedHeldEntryCount); // rev-1 is evicted-held because it was not installed into _scopes
        Assert.Equal(2, snapshot.UniqueLiveEntryCount);

        leaseRev1.Dispose();
        leaseRev2.Dispose();
    }

    [Fact]
    public async Task Acquire_CoalescesConcurrentLoadsForSameScopeAndIdentity()
    {
        using ResolutionStoreFixture fixture = ResolutionStoreFixture.Create();
        fixture.AddFile(1, "a.cs");
        fixture.AddSymbol(1, "a", "Alpha", "class", "a.cs");

        var store = new RevisionFactCacheStore();
        using var blocker = new ManualResetEventSlim(initialState: false);
        using var started = new ManualResetEventSlim(initialState: false);
        int opens = 0;

        Func<Microsoft.Data.Sqlite.SqliteConnection> blockingOpen = () =>
        {
            if (Interlocked.Increment(ref opens) == 1)
                started.Set();
            blocker.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            return fixture.OpenRead();
        };

        Task<RevisionFactCacheLease> task1 = Task.Run(() =>
            store.Acquire("ws-coalesce", "rev-1", blockingOpen, fixture.Visibility()));

        started.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Task<RevisionFactCacheLease> task2 = Task.Run(() =>
            store.Acquire("ws-coalesce", "rev-1", blockingOpen, fixture.Visibility()));
        Task<RevisionFactCacheLease> task3 = Task.Run(() =>
            store.Acquire("ws-coalesce", "rev-1", blockingOpen, fixture.Visibility()));

        Assert.True(
            SpinWait.SpinUntil(
                () => store.GetResourceSnapshot().CoalescedLoadCount == 2,
                TimeSpan.FromSeconds(5)));

        blocker.Set();

        RevisionFactCacheLease[] leases = await Task.WhenAll(task1, task2, task3);

        Assert.Same(leases[0].Cache, leases[1].Cache);
        Assert.Same(leases[0].Cache, leases[2].Cache);

        CacheResourceSnapshot snapshot = store.GetResourceSnapshot();
        Assert.Equal(1, snapshot.LoadCount);
        Assert.Equal(2, snapshot.CoalescedLoadCount);
        Assert.Equal(3, snapshot.ActiveLeaseCount);
        Assert.Equal(leases[0].Cache.ResidentBytes, snapshot.ActiveBytes); // Counted once!
        Assert.Equal(leases[0].Cache.ResidentBytes, snapshot.UniqueLiveBytes); // Counted once!

        foreach (RevisionFactCacheLease lease in leases)
            lease.Dispose();
    }

    [Fact]
    public void Acquire_OversizedEntry_PermitsAndReportsWithoutFailure()
    {
        using ResolutionStoreFixture fixture = ResolutionStoreFixture.Create();
        fixture.AddFile(1, "a.cs");
        fixture.AddSymbol(1, "a", "Alpha", "class", "a.cs");

        // Set byteBudget tiny so entry is oversized
        var store = new RevisionFactCacheStore(byteBudget: 10);
        using RevisionFactCacheLease lease = store.Acquire("ws-over", "rev-1", fixture.OpenRead, fixture.Visibility());

        CacheResourceSnapshot snapshot = store.GetResourceSnapshot();
        Assert.Equal(1, snapshot.OversizedEntryCount);
        Assert.Equal(1, snapshot.RetainedEntryCount);
        Assert.Equal(1, snapshot.UniqueLiveEntryCount);
        Assert.True(snapshot.UniqueLiveBytes > 10);

        Assert.Single(lease.Cache.SymbolsNamed("Alpha"));
    }

    [Fact]
    public async Task Acquire_AdvancingScopeWithDisposedPriorLease_TracksPreviousCacheAsEvictedHeldUntilAdvanceCompletes()
    {
        using ResolutionStoreFixture fixture = ResolutionStoreFixture.Create();
        fixture.AddFile(1, "keep.cs");
        fixture.AddFile(2, "change.cs");
        fixture.AddSymbol(1, "kept", "Kept", "class", "keep.cs");
        fixture.AddSymbol(2, "old", "Old", "class", "change.cs");

        var store = new RevisionFactCacheStore();

        // 1. Initial acquire and dispose
        using (RevisionFactCacheLease initialLease = store.Acquire("ws-a", "rev-1", fixture.OpenRead, fixture.Visibility()))
        {
            Assert.Equal(1, store.GetResourceSnapshot().ActiveLeaseCount);
        }

        CacheResourceSnapshot s1 = store.GetResourceSnapshot();
        Assert.Equal(1, s1.RetainedEntryCount);
        Assert.Equal(0, s1.ActiveLeaseCount);
        Assert.Equal(0, s1.EvictedHeldEntryCount);
        Assert.Equal(1, s1.UniqueLiveEntryCount);
        long rev1Bytes = s1.RetainedBytes;
        Assert.True(rev1Bytes > 0);

        // 2. Prepare revision delta
        fixture.AddSymbol(3, "neu", "New", "class", "change.cs");
        fixture.FlipManifest(2, [("keep.cs", 1, "csharp", "indexed"), ("change.cs", 3, "csharp", "indexed")]);

        using var advanceStarted = new ManualResetEventSlim(initialState: false);
        using var advanceBlocker = new ManualResetEventSlim(initialState: false);

        Task<RevisionFactCacheLease> advanceTask = Task.Run(() =>
        {
            return store.Acquire(
                "ws-a",
                "rev-2",
                () =>
                {
                    advanceStarted.Set();
                    advanceBlocker.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                    return fixture.OpenRead();
                },
                fixture.Visibility());
        });

        advanceStarted.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // 3. While advance is blocked in openRead, the previous cache must be tracked as evicted-held
        CacheResourceSnapshot inFlight = store.GetResourceSnapshot();
        Assert.Equal(0, inFlight.RetainedEntryCount);
        Assert.Equal(1, inFlight.ActiveLeaseCount); // loader holds previousHold
        Assert.Equal(rev1Bytes, inFlight.ActiveBytes);
        Assert.Equal(1, inFlight.EvictedHeldEntryCount);
        Assert.Equal(rev1Bytes, inFlight.EvictedHeldBytes);
        Assert.Equal(1, inFlight.UniqueLiveEntryCount);
        Assert.Equal(rev1Bytes, inFlight.UniqueLiveBytes);

        // 4. Unblock advance and verify completion
        advanceBlocker.Set();
        using RevisionFactCacheLease advancedLease = await advanceTask;

        CacheResourceSnapshot completed = store.GetResourceSnapshot();
        Assert.Equal(1, completed.RetainedEntryCount);
        Assert.Equal(1, completed.ActiveLeaseCount); // advancedLease
        Assert.Equal(0, completed.EvictedHeldEntryCount);
        Assert.Equal(0L, completed.EvictedHeldBytes);
        Assert.Equal(1, completed.UniqueLiveEntryCount);
        Assert.Equal(advancedLease.Cache.ResidentBytes, completed.UniqueLiveBytes);
        Assert.Single(advancedLease.Cache.SymbolsNamed("New"));
    }

    [Fact]
    public void Acquire_AdvancingScopeFailure_ReleasesTrackedPreviousCache()
    {
        using ResolutionStoreFixture fixture = ResolutionStoreFixture.Create();
        fixture.AddFile(1, "keep.cs");
        fixture.AddSymbol(1, "kept", "Kept", "class", "keep.cs");

        var store = new RevisionFactCacheStore();

        using (RevisionFactCacheLease initialLease = store.Acquire("ws-fail", "rev-1", fixture.OpenRead, fixture.Visibility()))
        {
        }

        Assert.Equal(1, store.GetResourceSnapshot().RetainedEntryCount);

        Assert.Throws<InvalidOperationException>(() =>
            store.Acquire(
                "ws-fail",
                "rev-2",
                () => throw new InvalidOperationException("openRead failed"),
                fixture.Visibility()));

        CacheResourceSnapshot snapshot = store.GetResourceSnapshot();
        Assert.Equal(0, snapshot.RetainedEntryCount);
        Assert.Equal(0, snapshot.ActiveLeaseCount);
        Assert.Equal(0, snapshot.EvictedHeldEntryCount);
        Assert.Equal(0L, snapshot.EvictedHeldBytes);
        Assert.Equal(0, snapshot.UniqueLiveEntryCount);
        Assert.Equal(0L, snapshot.UniqueLiveBytes);
        Assert.Equal(0, store.ScopeCount);
    }
}
