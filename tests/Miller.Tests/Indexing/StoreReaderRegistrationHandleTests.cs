using Miller.Indexing.Reads;
using Miller.Indexing.Store;
using Xunit;
using static Miller.Tests.Indexing.StoreReaderRegistrationRunnerTests;

namespace Miller.Tests.Indexing;

public sealed class StoreReaderRegistrationHandleTests
{
    [Fact]
    public void Manual_renewal_uses_expiry_deadline_and_leaves_ample_lease_untouched()
    {
        int renewals = 0;
        DateTimeOffset expiry = DateTimeOffset.FromUnixTimeMilliseconds(1900000120000);
        var runner = new StoreReaderRegistrationRunner((args, _) =>
        {
            if (args[2] == "release") return Released();
            if (args[2] == "renew") { renewals++; return new(0, Report.Replace("reader_acquire", "reader_renew").Replace("acquired", "renewed"), ""); }
            return new(0, Report, "");
        });
        using var registry = new StoreReaderRegistrationRegistry(startScheduler: false, utcNow: () => expiry - TimeSpan.FromSeconds(120));
        using var handle = StoreReaderRegistrationHandle.Acquire(runner, Request(), registry, TestContext.Current.CancellationToken);
        handle.RenewIfBefore(expiry - TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.Equal(0, renewals);
        handle.RenewIfBefore(expiry, TestContext.Current.CancellationToken);
        Assert.Equal(1, renewals);
    }

    [Fact]
    public void Retained_worker_keeps_same_pin_until_its_connection_owner_closes()
    {
        int releases = 0;
        var runner = new StoreReaderRegistrationRunner((args, _) =>
        {
            if (args[2] == "release") { releases++; return Released(); }
            return new(0, Report, "");
        });
        using var registry = new StoreReaderRegistrationRegistry(startScheduler: false);
        var handle = StoreReaderRegistrationHandle.Acquire(runner, Request(), registry, TestContext.Current.CancellationToken);
        IDisposable retained = handle.Retain();
        handle.Dispose();
        Assert.Equal(0, releases);
        Assert.Equal(ReaderLifecycleStatus.Acquired, handle.Status);
        Assert.Throws<ObjectDisposedException>(() => handle.Retain());
        retained.Dispose(); retained.Dispose();
        Assert.Equal(1, releases);
        Assert.Equal(ReaderLifecycleStatus.Released, handle.Status);
    }

    internal static ReaderProcessResult Released(bool removed = true) => new(0,
        $$"""{"report_schema_version":1,"operation":"reader_release","state":"released","family_id":"{{Family}}","pin_id":"pin-42","released":{{removed.ToString().ToLowerInvariant()}},"failure_class":null,"error":null,"warning":null} """, "");

    [Fact]
    public void Legacy_disposal_is_zero_work_and_stays_distinct()
    {
        var handle = StoreReaderRegistrationHandle.Legacy();
        handle.Dispose(); handle.Dispose();
        Assert.Equal(ReaderLifecycleStatus.Legacy, handle.Status);
    }

    [Fact]
    public void Disposal_releases_once_and_accepts_already_absent()
    {
        int releases = 0;
        var runner = new StoreReaderRegistrationRunner((args, _) =>
        {
            if (args[2] == "release") { releases++; return Released(false); }
            return new(0, Report, "");
        });
        using var registry = new StoreReaderRegistrationRegistry(startScheduler: false);
        var handle = StoreReaderRegistrationHandle.Acquire(runner, Request(), registry, TestContext.Current.CancellationToken);
        handle.Dispose(); handle.Dispose();
        Assert.Equal(ReaderLifecycleStatus.Released, handle.Status);
        Assert.Equal(1, releases);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void Failed_release_remains_owed_until_scheduled_retry()
    {
        int releases = 0;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var runner = new StoreReaderRegistrationRunner((args, _) => args[2] != "release" ? new(0, Report, "")
            : ++releases == 1 ? new(null, "", "", TransportLost: true) : Released());
        using var registry = new StoreReaderRegistrationRegistry(startScheduler: false, utcNow: () => now);
        var handle = StoreReaderRegistrationHandle.Acquire(runner, Request(), registry, TestContext.Current.CancellationToken);
        handle.Dispose(); handle.Dispose();
        Assert.Equal(ReaderLifecycleStatus.ReleaseOwed, handle.Status);
        Assert.Equal(1, releases);
        Assert.Equal(1, registry.Count);
        now += TimeSpan.FromSeconds(30);
        registry.Tick(now, TestContext.Current.CancellationToken);
        Assert.Equal(ReaderLifecycleStatus.Released, handle.Status);
        Assert.Equal(2, releases);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void Failed_renewal_preserves_snapshot_and_later_renewal_recovers()
    {
        int renewals = 0;
        DateTimeOffset now = DateTimeOffset.FromUnixTimeMilliseconds(1900000120000) - TimeSpan.FromSeconds(120);
        var runner = new StoreReaderRegistrationRunner((args, _) => args[2] switch
        {
            "release" => Released(),
            "renew" => ++renewals == 1 ? new(null, "", "", TransportLost: true)
                : new(0, Report.Replace("reader_acquire", "reader_renew").Replace("acquired", "renewed"), ""),
            _ => new(0, Report, "")
        });
        using var registry = new StoreReaderRegistrationRegistry(startScheduler: false, utcNow: () => now);
        using var handle = StoreReaderRegistrationHandle.Acquire(runner, Request(), registry, TestContext.Current.CancellationToken);
        var snapshot = handle.Snapshot;
        now += TimeSpan.FromSeconds(90);
        registry.Tick(now, TestContext.Current.CancellationToken);
        Assert.Equal(ReaderLifecycleStatus.RenewDegraded, handle.Status);
        Assert.Equal(snapshot, handle.Snapshot);
        now += TimeSpan.FromSeconds(30);
        registry.Tick(now, TestContext.Current.CancellationToken);
        Assert.Equal(ReaderLifecycleStatus.Acquired, handle.Status);
        Assert.Equal(2, renewals);
    }
}
