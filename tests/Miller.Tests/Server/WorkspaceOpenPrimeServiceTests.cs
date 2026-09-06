using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Miller.Core.Freshness;
using Miller.Indexing;
using Miller.Server;
using Miller.Server.Hosting;
using Miller.Server.Workspaces;
using Xunit;

namespace Miller.Tests.Server;

public sealed class WorkspaceOpenPrimeServiceTests
{
    [Fact]
    public void TryEnqueue_DeduplicatesQueuedWorkspaceIds()
    {
        using var bootstrap = NewBootstrap();
        using var provider = new ServiceCollection().BuildServiceProvider();
        using var service = new WorkspaceOpenPrimeService(
            bootstrap,
            provider,
            NullLogger<WorkspaceOpenPrimeService>.Instance);

        Assert.Equal(WorkspaceOpenPrimeEnqueueResult.Queued, service.TryEnqueue("workspace-1"));
        Assert.Equal(WorkspaceOpenPrimeEnqueueResult.AlreadyQueued, service.TryEnqueue("workspace-1"));
    }

    [Fact]
    public void TryEnqueue_ReturnsFullAndDoesNotKeepRejectedWorkspaceActive()
    {
        using var bootstrap = NewBootstrap();
        using var provider = new ServiceCollection().BuildServiceProvider();
        using var service = new WorkspaceOpenPrimeService(
            bootstrap,
            provider,
            NullLogger<WorkspaceOpenPrimeService>.Instance);

        for (int i = 0; i < 64; i++)
            Assert.Equal(WorkspaceOpenPrimeEnqueueResult.Queued, service.TryEnqueue($"workspace-{i}"));

        Assert.Equal(WorkspaceOpenPrimeEnqueueResult.Full, service.TryEnqueue("overflow"));
        Assert.Equal(WorkspaceOpenPrimeEnqueueResult.Full, service.TryEnqueue("overflow"));
    }

    [Fact]
    public async Task TryEnqueue_ReturnsStoppingAfterStopAsyncCompletesTheWriter()
    {
        using var bootstrap = NewBootstrap();
        using var provider = new ServiceCollection().BuildServiceProvider();
        using var service = new WorkspaceOpenPrimeService(
            bootstrap,
            provider,
            NullLogger<WorkspaceOpenPrimeService>.Instance);

        await service.StopAsync(CancellationToken.None);

        Assert.Equal(WorkspaceOpenPrimeEnqueueResult.Stopping, service.TryEnqueue("workspace-1"));
    }

    [Fact]
    public async Task ExecuteAsync_DeduplicatesRunningIdsAndReleasesTheIdAfterRefresh()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        int scanCount = 0;
        using var harness = new PrimeHarness(
            (root, db, _, _, _) =>
            {
                Interlocked.Increment(ref scanCount);
                entered.TrySetResult();
                release.Wait();
                return Report(root, db, revision: 1);
            });
        WorkspaceTarget target = harness.AddTarget("one");

        await harness.Service.StartAsync(CancellationToken.None);
        Assert.Equal(WorkspaceOpenPrimeEnqueueResult.Queued, harness.Service.TryEnqueue(target.Id));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(WorkspaceOpenPrimeEnqueueResult.AlreadyQueued, harness.Service.TryEnqueue(target.Id));

        release.Set();
        await WaitUntilAsync(() => harness.Registry.Get(target.Id)?.State == WorkspaceRegistryState.Ready);
        Assert.Equal(1, scanCount);

        Assert.Equal(WorkspaceOpenPrimeEnqueueResult.Queued, WaitForRetry(harness.Service, target.Id));
        await harness.Service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_DrainsRegisteredWorkspaceWithoutPrimaryBinding()
    {
        using var harness = new PrimeHarness(
            (root, db, _, _, _) => Report(root, db, revision: 1),
            seedBootstrap: false);
        WorkspaceTarget target = harness.AddTarget("unbound");

        Assert.False(harness.Bootstrap.IsBound);
        await harness.Service.StartAsync(CancellationToken.None);
        Assert.Equal(WorkspaceOpenPrimeEnqueueResult.Queued, harness.Service.TryEnqueue(target.Id));

        await WaitUntilAsync(() => harness.Registry.Get(target.Id)?.State == WorkspaceRegistryState.Ready);
        Assert.False(harness.Bootstrap.IsBound);

        await harness.Service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_ReconcilesLockBusyWithReadableIndexAsLoadedExisting()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var harness = new PrimeHarness(
            (root, db, _, _, _) => Report(root, db, revision: 2),
            acquireLock: _ => null,
            sleep: _ => completed.TrySetResult());
        WorkspaceTarget target = harness.AddTarget("lock-busy-readable");
        File.WriteAllBytes(target.DbPath, Array.Empty<byte>());

        await harness.Service.StartAsync(CancellationToken.None);
        Assert.Equal(WorkspaceOpenPrimeEnqueueResult.Queued, harness.Service.TryEnqueue(target.Id));
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(SpinWait.SpinUntil(
            () => harness.Registry.Get(target.Id)?.State == WorkspaceRegistryState.LoadedExisting,
            TimeSpan.FromSeconds(5)));

        WorkspaceRegistryRow row = harness.Registry.Get(target.Id)!;
        Assert.Equal(WorkspaceRegistryState.LoadedExisting, row.State);
        Assert.Equal(0, row.LastRevision);
        await harness.Service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_ReconcilesIneligibleWithoutAnIndexAsError()
    {
        using var harness = new PrimeHarness(
            (root, db, _, _, _) => Report(root, db, revision: 2),
            eligibilityGate: _ => new LeadershipVerdict(false, false, "synthetic ineligible"));
        WorkspaceTarget target = harness.AddTarget("ineligible-absent");

        await harness.Service.StartAsync(CancellationToken.None);
        Assert.Equal(WorkspaceOpenPrimeEnqueueResult.Queued, harness.Service.TryEnqueue(target.Id));

        Assert.True(SpinWait.SpinUntil(
            () => harness.Registry.Get(target.Id)?.State == WorkspaceRegistryState.Error,
            TimeSpan.FromSeconds(5)));
        WorkspaceRegistryRow row = harness.Registry.Get(target.Id)!;
        Assert.Contains("synthetic ineligible", row.LastError);
        await harness.Service.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(WorkspaceRegistryState.Ready)]
    [InlineData(WorkspaceRegistryState.LoadedExisting)]
    [InlineData(WorkspaceRegistryState.Error)]
    [InlineData(WorkspaceRegistryState.Missing)]
    public async Task ExecuteAsync_TerminalOutcomePreservesDurableState(WorkspaceRegistryState state)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var harness = new PrimeHarness(
            (root, db, _, _, _) => Report(root, db, revision: 2),
            acquireLock: _ => null,
            sleep: _ => completed.TrySetResult());
        WorkspaceTarget target = harness.AddTarget("state-" + state, state);
        File.WriteAllBytes(target.DbPath, Array.Empty<byte>());

        await harness.Service.StartAsync(CancellationToken.None);
        Assert.Equal(WorkspaceOpenPrimeEnqueueResult.Queued, harness.Service.TryEnqueue(target.Id));
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(state, harness.Registry.Get(target.Id)!.State);
        await harness.Service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_MarksUnexpectedExceptionsAndContinuesWithTheNextId()
    {
        int policyCalls = 0;
        int scanCount = 0;
        using var harness = new PrimeHarness(
            (root, db, _, _, _) =>
            {
                Interlocked.Increment(ref scanCount);
                return Report(root, db, revision: 2);
            },
            (_, _) =>
            {
                if (Interlocked.Increment(ref policyCalls) == 1)
                    throw new InvalidOperationException("synthetic prime failure");
                return new InMemoryScanFailurePolicy();
            });
        WorkspaceTarget failed = harness.AddTarget("failed");
        WorkspaceTarget ready = harness.AddTarget("ready");

        await harness.Service.StartAsync(CancellationToken.None);
        Assert.Equal(WorkspaceOpenPrimeEnqueueResult.Queued, harness.Service.TryEnqueue(failed.Id));
        Assert.Equal(WorkspaceOpenPrimeEnqueueResult.Queued, harness.Service.TryEnqueue(ready.Id));

        await WaitUntilAsync(
            () => harness.Registry.Get(failed.Id)?.State == WorkspaceRegistryState.Error &&
                harness.Registry.Get(ready.Id)?.State == WorkspaceRegistryState.Ready);
        Assert.Contains("synthetic prime failure", harness.Registry.Get(failed.Id)?.LastError);
        Assert.Equal(1, scanCount);
        Assert.Equal(WorkspaceOpenPrimeEnqueueResult.Queued, WaitForRetry(harness.Service, failed.Id));
        await harness.Service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_ResolvesDependenciesPerItemAndContinuesAfterRefreshResolutionFailure()
    {
        int scanCount = 0;
        using var harness = new PrimeHarness(
            (root, db, _, _, _) =>
            {
                Interlocked.Increment(ref scanCount);
                return Report(root, db, revision: 2);
            },
            servicesFactory: (registry, refresh) => new MutableServiceProvider(registry, refresh));
        WorkspaceTarget failed = harness.AddTarget("resolution-failed");
        WorkspaceTarget ready = harness.AddTarget("resolution-ready");

        await harness.Service.StartAsync(CancellationToken.None);
        Assert.Equal(WorkspaceOpenPrimeEnqueueResult.Queued, harness.Service.TryEnqueue(failed.Id));
        Assert.Equal(WorkspaceOpenPrimeEnqueueResult.Queued, harness.Service.TryEnqueue(ready.Id));

        await WaitUntilAsync(
            () => harness.Registry.Get(failed.Id)?.State == WorkspaceRegistryState.Error &&
                harness.Registry.Get(ready.Id)?.State == WorkspaceRegistryState.Ready);
        Assert.Contains("synthetic refresh resolution failure", harness.Registry.Get(failed.Id)?.LastError);
        Assert.Equal(1, scanCount);
        Assert.Equal(WorkspaceOpenPrimeEnqueueResult.Queued, WaitForRetry(harness.Service, failed.Id));
        Assert.Equal(WorkspaceOpenPrimeEnqueueResult.Queued, WaitForRetry(harness.Service, ready.Id));
        await harness.Service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotOverwriteReadyRowAfterLateFailure()
    {
        var failureEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PrimeHarness? holder = null;
        using var harness = new PrimeHarness(
            (root, db, _, _, _) => Report(root, db, revision: 2),
            (_, canonicalRoot) =>
            {
                holder!.Registry.UpsertSeen(
                    "workspace-late-failure",
                    "late-failure",
                    canonicalRoot,
                    Path.Combine(canonicalRoot, ".miller", "symbols.db"),
                    state: WorkspaceRegistryState.Ready);
                failureEntered.TrySetResult();
                throw new InvalidOperationException("synthetic late failure");
            });
        holder = harness;
        WorkspaceTarget target = harness.AddTarget("late-failure");

        await harness.Service.StartAsync(CancellationToken.None);
        Assert.Equal(WorkspaceOpenPrimeEnqueueResult.Queued, harness.Service.TryEnqueue(target.Id));
        await failureEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await harness.Service.StopAsync(CancellationToken.None);

        WorkspaceRegistryRow row = harness.Registry.Get(target.Id)!;
        Assert.Equal(WorkspaceRegistryState.Ready, row.State);
        Assert.Null(row.LastError);
    }

    [Fact]
    public async Task StopAsync_HonorsCancellationBudgetWhileRefreshIsSynchronous()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var harness = new PrimeHarness(
            (root, db, _, _, _) =>
            {
                entered.TrySetResult();
                release.Wait();
                return Report(root, db, revision: 3);
            });
        WorkspaceTarget target = harness.AddTarget("stopping");

        await harness.Service.StartAsync(CancellationToken.None);
        Assert.Equal(WorkspaceOpenPrimeEnqueueResult.Queued, harness.Service.TryEnqueue(target.Id));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        using var budget = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        Stopwatch stopwatch = Stopwatch.StartNew();
        await harness.Service.StopAsync(budget.Token);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"StopAsync took {stopwatch.Elapsed}.");
        release.Set();
        await harness.Service.StopAsync(CancellationToken.None);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(10, deadline.Token);
    }

    private static WorkspaceOpenPrimeEnqueueResult WaitForRetry(
        WorkspaceOpenPrimeService service,
        string workspaceId)
    {
        WorkspaceOpenPrimeEnqueueResult result = WorkspaceOpenPrimeEnqueueResult.AlreadyQueued;
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (result == WorkspaceOpenPrimeEnqueueResult.AlreadyQueued && stopwatch.Elapsed < TimeSpan.FromSeconds(5))
        {
            result = service.TryEnqueue(workspaceId);
            if (result == WorkspaceOpenPrimeEnqueueResult.AlreadyQueued)
                Thread.Yield();
        }

        return result;
    }

    private static ExtractReport Report(string root, string dbPath, long revision) =>
        new(
            ReportSchemaVersion: 1,
            Status: "ok",
            Operation: "scan",
            Mode: "incremental",
            Input: null,
            Artifact: new ExtractArtifact(
                DbPath: dbPath,
                RootPath: root,
                ArtifactId: "artifact",
                SchemaVersion: MillerExtractContract.ExpectedSchemaVersion,
                ExtractContractVersion: MillerExtractContract.ExpectedExtractContractVersion,
                SqliteSchemaVersion: MillerExtractContract.ExpectedSqliteSchemaVersion,
                JsonlSchemaVersion: 1,
                HashAlgorithm: MillerExtractContract.ExpectedHashAlgorithm,
                ParserInventoryFingerprint: "parser",
                CapabilitySnapshotFingerprint: "capabilities"),
            Tool: new ExtractTool("julie-extract", "2.0.0"),
            RevisionBlock: new ExtractRevision(revision, revision),
            Counts: null,
            Errors: Array.Empty<ReportDiagnostic>(),
            Warnings: Array.Empty<ReportDiagnostic>());

    private static IndexBootstrapService NewBootstrap(string? home = null)
    {
        home ??= Path.Combine(Path.GetTempPath(), "miller-open-prime-home-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        return new IndexBootstrapService(NullLogger<IndexBootstrapService>.Instance)
        {
            TestHomeDirectoryOverride = home,
        };
    }

    private sealed class PrimeHarness : IDisposable
    {
        private readonly string _root;

        public PrimeHarness(
            Func<string, string, bool, int?, ExtractIndexLevel, ExtractReport> scan,
            Func<string, string, IScanFailurePolicy>? failurePolicyFor = null,
            Func<WorkspaceRegistry, CrossWorkspaceRefreshService, IServiceProvider>? servicesFactory = null,
            Func<string, IDisposable?>? acquireLock = null,
            Func<string, LeadershipVerdict>? eligibilityGate = null,
            Action<TimeSpan>? sleep = null,
            bool seedBootstrap = true)
        {
            _root = Path.Combine(Path.GetTempPath(), "miller-open-prime-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            string registryPath = Path.Combine(_root, "registry.db");
            Registry = WorkspaceRegistry.Open(registryPath);
            Bootstrap = NewBootstrap(Path.Combine(_root, "home"));
            if (seedBootstrap)
            {
                string bootstrapDb = Path.Combine(_root, ".miller", "symbols.db");
                Directory.CreateDirectory(Path.GetDirectoryName(bootstrapDb)!);
                WorkspaceContext workspace = WorkspaceContext.Create(_root, AppContext.BaseDirectory, _root) with
                {
                    CanonicalRoot = _root,
                    ExtractDbPath = bootstrapDb,
                    CanonicalExtractDbPath = bootstrapDb,
                };
                Bootstrap.SeedForTest(
                    workspace,
                    new IndexHolder(MillerRepositoryIndex.Build(Array.Empty<IndexedSymbol>()), builtRevision: 0));
            }

            Refresh = new CrossWorkspaceRefreshService(
                Registry,
                scan,
                acquireLock: acquireLock ?? (_ => new NoopLease()),
                readLatestRevision: _ => 0,
                lockBusyWait: TimeSpan.FromMilliseconds(1),
                lockBusyPollInterval: TimeSpan.FromMilliseconds(1),
                sleep: sleep ?? (_ => { }),
                utcNow: () => DateTimeOffset.UtcNow,
                sidecar: SymbolSearchSidecar.Disabled,
                eligibilityGate: eligibilityGate,
                failurePolicyFor: failurePolicyFor ?? ((_, _) => new InMemoryScanFailurePolicy()),
                storeEnabled: static () => false);
            Provider = servicesFactory?.Invoke(Registry, Refresh) ?? new ServiceCollection()
                .AddSingleton(Registry)
                .AddSingleton(Refresh)
                .BuildServiceProvider();
            Service = new WorkspaceOpenPrimeService(
                Bootstrap,
                Provider,
                NullLogger<WorkspaceOpenPrimeService>.Instance);
        }

        public IndexBootstrapService Bootstrap { get; }
        public WorkspaceRegistry Registry { get; }
        public CrossWorkspaceRefreshService Refresh { get; }
        public IServiceProvider Provider { get; }
        public WorkspaceOpenPrimeService Service { get; }

        public WorkspaceTarget AddTarget(
            string name,
            WorkspaceRegistryState state = WorkspaceRegistryState.Refreshing)
        {
            string id = "workspace-" + name;
            string root = Path.Combine(_root, name);
            string dbPath = Path.Combine(root, ".miller", "symbols.db");
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            Registry.UpsertSeen(
                id,
                name,
                root,
                dbPath,
                state: state);
            return new WorkspaceTarget(id, root, dbPath);
        }

        public void Dispose()
        {
            Service.Dispose();
            if (Provider is IDisposable disposable)
                disposable.Dispose();
            Registry.Dispose();
            Bootstrap.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class NoopLease : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class MutableServiceProvider(
        WorkspaceRegistry registry,
        CrossWorkspaceRefreshService refresh) : IServiceProvider
    {
        private int _refreshResolutionFailures = 1;

        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(WorkspaceRegistry))
                return registry;
            if (serviceType == typeof(CrossWorkspaceRefreshService))
            {
                if (Interlocked.Exchange(ref _refreshResolutionFailures, 0) == 1)
                    throw new InvalidOperationException("synthetic refresh resolution failure");
                return refresh;
            }

            return null;
        }
    }

    private readonly record struct WorkspaceTarget(string Id, string Root, string DbPath);
}
