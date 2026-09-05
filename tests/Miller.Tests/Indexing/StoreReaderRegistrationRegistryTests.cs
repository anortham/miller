using Miller.Indexing.Reads;
using Miller.Indexing.Store;
using Xunit;
using static Miller.Tests.Indexing.StoreReaderRegistrationRunnerTests;
using static Miller.Tests.Indexing.StoreReaderRegistrationHandleTests;

namespace Miller.Tests.Indexing;

public sealed class StoreReaderRegistrationRegistryTests
{
    [Fact]
    public void Duplicate_nonce_cannot_create_two_independent_release_owners()
    {
        int calls = 0;
        var runner = new StoreReaderRegistrationRunner((args, _) => { calls++; return args[2] == "release" ? Released() : new(0, Report, ""); });
        using var registry = new StoreReaderRegistrationRegistry(startScheduler: false);
        using var handle = StoreReaderRegistrationHandle.Acquire(runner, Request(), registry, TestContext.Current.CancellationToken);
        Assert.Throws<StoreReaderRegistrationException>(() => StoreReaderRegistrationHandle.Acquire(
            runner, Request(), registry, TestContext.Current.CancellationToken));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Diagnostic_tick_reaches_all_due_handles_within_its_work_budget()
    {
        int renewals = 0;
        DateTimeOffset now = DateTimeOffset.FromUnixTimeMilliseconds(1900000120000) - TimeSpan.FromSeconds(120);
        var runner = new StoreReaderRegistrationRunner((args, _) =>
        {
            if (args[2] == "release") return Released();
            string nonce = args[Array.IndexOf(args.ToArray(), "--nonce") + 1];
            string report = Report.Replace(Nonce, nonce);
            if (args[2] == "renew") { renewals++; report = report.Replace("reader_acquire", "reader_renew").Replace("acquired", "renewed"); }
            return new(0, report, "");
        });
        using var registry = new StoreReaderRegistrationRegistry(startScheduler: false, utcNow: () => now);
        var handles = new List<StoreReaderRegistrationHandle>();
        try
        {
            for (int i = 0; i < 5; i++) handles.Add(StoreReaderRegistrationHandle.Acquire(runner,
                Request() with { OwnerNonce = Nonce + i }, registry, TestContext.Current.CancellationToken));
            now += TimeSpan.FromSeconds(90);
            registry.Tick(now, TestContext.Current.CancellationToken);
            Assert.Equal(5, renewals);
        }
        finally { foreach (var handle in handles) handle.Dispose(); }
    }

    [Fact]
    public void Cancellation_after_lost_reply_keeps_the_exact_request_for_cleanup()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var calls = new List<string[]>();
        var runner = new StoreReaderRegistrationRunner((args, _) =>
        {
            calls.Add(args.ToArray());
            if (calls.Count == 1) { cancel.Cancel(); return new(null, "", "", TransportLost: true); }
            return args[2] == "release" ? Released() : new(0, Report, "");
        });
        using var registry = new StoreReaderRegistrationRegistry(startScheduler: false, utcNow: () => now);
        Assert.ThrowsAny<OperationCanceledException>(() => StoreReaderRegistrationHandle.Acquire(runner, Request(), registry, cancel.Token));
        Assert.Equal(1, registry.Count);
        now += TimeSpan.FromSeconds(30);
        registry.Tick(now, TestContext.Current.CancellationToken);
        Assert.Equal(3, calls.Count);
        Assert.Equal(calls[0], calls[1]);
        Assert.Equal("release", calls[2][2]);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void Exhausted_acquire_retains_request_and_recovers_only_to_release()
    {
        var requests = new List<string[]>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var runner = new StoreReaderRegistrationRunner((args, _) =>
        {
            requests.Add(args.ToArray());
            if (args[2] == "release") return Released();
            return requests.Count <= 3 ? new(null, "", "", TransportLost: true) : new(0, Report, "");
        });
        using var registry = new StoreReaderRegistrationRegistry(startScheduler: false, utcNow: () => now);
        Assert.Throws<StoreReaderRegistrationException>(() => StoreReaderRegistrationHandle.Acquire(
            runner, Request(), registry, TestContext.Current.CancellationToken));
        Assert.Equal(1, registry.Count);
        now += TimeSpan.FromSeconds(30);
        registry.Tick(now, TestContext.Current.CancellationToken);
        Assert.Equal(5, requests.Count);
        Assert.Equal(requests[0], requests[3]);
        Assert.Equal("release", requests[4][2]);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void Capacity_is_reserved_before_acquire_and_does_not_drop_active_handles()
    {
        int calls = 0;
        var runner = new StoreReaderRegistrationRunner((args, _) => { calls++; return args[2] == "release" ? Released() : new(0, Report, ""); });
        using var registry = new StoreReaderRegistrationRegistry(startScheduler: false, capacity: 1);
        using var handle = StoreReaderRegistrationHandle.Acquire(runner, Request(), registry, TestContext.Current.CancellationToken);
        var error = Assert.Throws<StoreReaderRegistrationException>(() => StoreReaderRegistrationHandle.Acquire(
            runner, Request(), registry, TestContext.Current.CancellationToken));
        Assert.Equal(ReaderFailure.RegistryCapacity, error.Failure);
        Assert.Equal(1, calls);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void Registry_shutdown_never_releases_a_live_session()
    {
        int releases = 0;
        var runner = new StoreReaderRegistrationRunner((args, _) =>
        {
            if (args[2] == "release") { releases++; return Released(); }
            return new(0, Report, "");
        });
        var registry = new StoreReaderRegistrationRegistry(startScheduler: false);
        using var handle = StoreReaderRegistrationHandle.Acquire(runner, Request(), registry, TestContext.Current.CancellationToken);
        registry.Dispose();
        Assert.Equal(ReaderLifecycleStatus.Acquired, handle.Status);
        Assert.Equal(0, releases);
        handle.Dispose();
        Assert.Equal(1, releases);
    }
}
