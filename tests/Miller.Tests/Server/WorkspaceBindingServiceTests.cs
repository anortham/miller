using Microsoft.Extensions.Logging.Abstractions;
using Miller.Indexing;
using Miller.Server;
using Miller.Server.Hosting;
using Miller.Server.Tools;
using Xunit;

namespace Miller.Tests.Server;

public sealed class WorkspaceBindingServiceTests
{
    [Fact]
    public async Task StartAsync_DefersWhenStartupCwdIsSensitive()
    {
        var bootstrap = new IndexBootstrapService(NullLogger<IndexBootstrapService>.Instance);
        string previous = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            await bootstrap.StartAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }

        Assert.True(bootstrap.IsDeferred);
        Assert.False(bootstrap.IsBound);
    }

    [Fact]
    public void BootstrapForRoot_RejectsSensitiveRootFromMcpRoots()
    {
        // Incident 2026-07-06: sessions launched with cwd=$HOME correctly deferred bootstrap, but the MCP
        // client then offered file://$HOME as its root and the Roots source bound it UNGUARDED — kicking off
        // a full julie-extract scan of the home directory. Every binding source must pass the sensitive-root
        // guard, not just Cwd.
        var bootstrap = new IndexBootstrapService(NullLogger<IndexBootstrapService>.Instance);
        bootstrap.TestBootstrapInterceptor = (_, _) => throw new InvalidOperationException(
            "guard must reject before any bootstrap work runs");

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var ex = Assert.Throws<InvalidOperationException>(
            () => bootstrap.BootstrapForRoot(home, WorkspaceBindingResolver.WorkspaceSource.Roots));
        Assert.Contains("sensitive system path", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BootstrapForRoot_RejectsSensitiveRootFromEnv()
    {
        var bootstrap = new IndexBootstrapService(NullLogger<IndexBootstrapService>.Instance);
        bootstrap.TestBootstrapInterceptor = (_, _) => throw new InvalidOperationException(
            "guard must reject before any bootstrap work runs");

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var ex = Assert.Throws<InvalidOperationException>(
            () => bootstrap.BootstrapForRoot(home, WorkspaceBindingResolver.WorkspaceSource.Env));
        Assert.Contains("sensitive system path", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BootstrapForRoot_DispatchesRunInBackgroundAndPublishesOnlyWhenComplete()
    {
        string project = CreateTempDir();
        var bootstrap = new IndexBootstrapService(NullLogger<IndexBootstrapService>.Instance);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bootstrap.TestRunBootstrapOverride = canonicalRoot =>
        {
            started.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            SeedEmptyWorkspace(bootstrap, canonicalRoot);
        };

        int runGeneration;
        try
        {
            var outcome = bootstrap.BootstrapForRoot(project, WorkspaceBindingResolver.WorkspaceSource.Roots);

            Assert.Equal(BindOutcome.Started, outcome);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.False(bootstrap.IsBound);
            var snapshot = bootstrap.Snapshot;
            runGeneration = snapshot.RunGeneration;
            Assert.Equal(BootstrapPhase.Running, snapshot.Phase);
            Assert.Equal(PathCanonicalizer.CanonicalizeRoot(project), snapshot.CanonicalRoot);
            Assert.NotNull(snapshot.StartedAtUtc);
            Assert.Null(snapshot.FailureMessage);
            Assert.Throws<InvalidOperationException>(() => bootstrap.Holder);
        }
        finally
        {
            release.TrySetResult();
        }

        await bootstrap.WaitForRunAsync(runGeneration, TestContext.Current.CancellationToken);
        Assert.True(bootstrap.IsBound);
        Assert.Equal(BootstrapPhase.Bound, bootstrap.Snapshot.Phase);
        Assert.Equal(PathCanonicalizer.CanonicalizeRoot(project), bootstrap.Workspace.CanonicalRoot);
    }

    [Fact]
    public async Task BootstrapForRoot_WhenRunFails_MarksFailedCompletesRunWaitAndRetries()
    {
        string project = CreateTempDir();
        var bootstrap = new IndexBootstrapService(NullLogger<IndexBootstrapService>.Instance);
        bootstrap.TestRunBootstrapOverride = _ => throw new InvalidOperationException("synthetic async failure");

        var outcome = bootstrap.BootstrapForRoot(project, WorkspaceBindingResolver.WorkspaceSource.Roots);
        Assert.Equal(BindOutcome.Started, outcome);
        int failedRunGeneration = bootstrap.Snapshot.RunGeneration;

        await bootstrap.WaitForRunAsync(failedRunGeneration, TestContext.Current.CancellationToken);

        var failed = bootstrap.Snapshot;
        Assert.Equal(BootstrapPhase.Failed, failed.Phase);
        Assert.Equal("synthetic async failure", failed.FailureMessage);
        Assert.Equal("synthetic async failure", failed.LastFailureMessage);
        Assert.False(bootstrap.IsBound);

        string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(project);
        var failedWorkspace = WorkspaceContext.Create(canonicalRoot, AppContext.BaseDirectory) with
        {
            WorkspaceId = WorkspaceId.FromCanonicalRoot(canonicalRoot),
            CanonicalRoot = canonicalRoot,
            CanonicalExtractDbPath = Path.Combine(canonicalRoot, ".miller", "symbols.db"),
        };
        WorkspaceRegistryRow? row = null;
        await WaitUntilAsync(() =>
        {
            using var registry = WorkspaceRegistry.Open(failedWorkspace.RegistryDbPath);
            row = registry.Get(failedWorkspace.WorkspaceId);
            return row?.State == WorkspaceRegistryState.Error;
        }, TestContext.Current.CancellationToken);
        Assert.Equal(WorkspaceRegistryState.Error, row!.State);
        Assert.Equal("synthetic async failure", row.LastError);

        bootstrap.TestRunBootstrapOverride = canonicalRoot => SeedEmptyWorkspace(bootstrap, canonicalRoot);
        var retryOutcome = bootstrap.BootstrapForRoot(project, WorkspaceBindingResolver.WorkspaceSource.Roots);
        Assert.Equal(BindOutcome.Started, retryOutcome);
        int retryRunGeneration = bootstrap.Snapshot.RunGeneration;

        await bootstrap.WaitForRunAsync(retryRunGeneration, TestContext.Current.CancellationToken);

        Assert.True(bootstrap.IsBound);
        Assert.Equal(BootstrapPhase.Bound, bootstrap.Snapshot.Phase);
        Assert.Equal(PathCanonicalizer.CanonicalizeRoot(project), bootstrap.Workspace.CanonicalRoot);
    }

    [Fact]
    public async Task BootstrapForRoot_WhenSameRootAlreadyRunning_JoinsExistingRun()
    {
        string project = CreateTempDir();
        int runs = 0;
        var bootstrap = new IndexBootstrapService(NullLogger<IndexBootstrapService>.Instance);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bootstrap.TestRunBootstrapOverride = canonicalRoot =>
        {
            Interlocked.Increment(ref runs);
            started.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            SeedEmptyWorkspace(bootstrap, canonicalRoot);
        };

        int runGeneration;
        try
        {
            var first = bootstrap.BootstrapForRoot(project, WorkspaceBindingResolver.WorkspaceSource.Roots);
            Assert.Equal(BindOutcome.Started, first);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            runGeneration = bootstrap.Snapshot.RunGeneration;

            var second = bootstrap.BootstrapForRoot(project, WorkspaceBindingResolver.WorkspaceSource.Roots);

            Assert.Equal(BindOutcome.JoinedRunning, second);
            Assert.Equal(1, runs);
        }
        finally
        {
            release.TrySetResult();
        }

        await bootstrap.WaitForRunAsync(runGeneration, TestContext.Current.CancellationToken);
        Assert.True(bootstrap.IsBound);
    }

    [Fact]
    public async Task StartAsync_WithUsableCwd_ReturnsBeforeBackgroundRunCompletes()
    {
        string project = CreateTempDir();
        var bootstrap = new IndexBootstrapService(NullLogger<IndexBootstrapService>.Instance);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bootstrap.TestRunBootstrapOverride = canonicalRoot =>
        {
            started.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            SeedEmptyWorkspace(bootstrap, canonicalRoot);
        };

        string previous = Directory.GetCurrentDirectory();
        int runGeneration;
        try
        {
            Directory.SetCurrentDirectory(project);
            await bootstrap.StartAsync(TestContext.Current.CancellationToken);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.False(bootstrap.IsDeferred);
            Assert.False(bootstrap.IsBound);
            var snapshot = bootstrap.Snapshot;
            runGeneration = snapshot.RunGeneration;
            Assert.Equal(BootstrapPhase.Running, snapshot.Phase);
            Assert.Equal(PathCanonicalizer.CanonicalizeRoot(project), snapshot.CanonicalRoot);
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
            release.TrySetResult();
        }

        await bootstrap.WaitForRunAsync(runGeneration, TestContext.Current.CancellationToken);
        Assert.True(bootstrap.IsBound);
    }

    [Fact]
    public async Task EnsurePrimaryBoundFromRootsAsync_BindsDeferredBootstrap()
    {
        string project = CreateTempDir();
        var bootstrap = new IndexBootstrapService(NullLogger<IndexBootstrapService>.Instance);
        bootstrap.TestBootstrapInterceptor = (canonicalRoot, source) =>
        {
            Assert.Equal(WorkspaceBindingResolver.WorkspaceSource.Roots, source);
            var workspace = WorkspaceContext.Create(canonicalRoot, AppContext.BaseDirectory) with
            {
                WorkspaceId = WorkspaceId.FromCanonicalRoot(canonicalRoot),
                CanonicalRoot = canonicalRoot,
            };
            bootstrap.SeedForTest(workspace, new IndexHolder(
                MillerRepositoryIndex.Build(System.Array.Empty<IndexedSymbol>()), builtRevision: 0));
            return true;
        };

        string previous = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            await bootstrap.StartAsync(CancellationToken.None);
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }

        Assert.True(bootstrap.IsDeferred);

        var binding = new WorkspaceBindingService(bootstrap, NullLogger<WorkspaceBindingService>.Instance);
        await binding.EnsurePrimaryBoundFromRootsAsync(
            [$"file://{project}"], TestContext.Current.CancellationToken);

        Assert.True(bootstrap.IsBound);
        Assert.Equal(PathCanonicalizer.CanonicalizeRoot(project), bootstrap.Workspace.CanonicalRoot);
    }

    [Fact]
    public async Task EnsurePrimaryBoundFromRootsAsync_ThrowsWhenUnresolvable()
    {
        var bootstrap = new IndexBootstrapService(NullLogger<IndexBootstrapService>.Instance);
        string previous = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            await bootstrap.StartAsync(CancellationToken.None);
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }

        var binding = new WorkspaceBindingService(bootstrap, NullLogger<WorkspaceBindingService>.Instance);

        try
        {
            Directory.SetCurrentDirectory(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await binding.EnsurePrimaryBoundFromRootsAsync(
                    null, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }
    }

    [Fact]
    public async Task MarkRootsDirty_ForcesRebindOnNextEnsure()
    {
        string projectA = CreateTempDir();
        string projectB = CreateTempDir();
        int bindCount = 0;
        var bootstrap = new IndexBootstrapService(NullLogger<IndexBootstrapService>.Instance);
        bootstrap.TestBootstrapInterceptor = (canonicalRoot, _) =>
        {
            bindCount++;
            var workspace = WorkspaceContext.Create(canonicalRoot, AppContext.BaseDirectory) with
            {
                WorkspaceId = WorkspaceId.FromCanonicalRoot(canonicalRoot),
                CanonicalRoot = canonicalRoot,
            };
            bootstrap.SeedForTest(workspace, new IndexHolder(
                MillerRepositoryIndex.Build(System.Array.Empty<IndexedSymbol>()), builtRevision: 0));
            return true;
        };
        var binding = new WorkspaceBindingService(bootstrap, NullLogger<WorkspaceBindingService>.Instance);
        var ct = TestContext.Current.CancellationToken;

        string previous = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            await bootstrap.StartAsync(CancellationToken.None);
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }

        await binding.EnsurePrimaryBoundFromRootsAsync([$"file://{projectA}"], ct);
        int genAfterFirst = bootstrap.BindingGeneration;

        binding.MarkRootsDirty();
        await binding.EnsurePrimaryBoundFromRootsAsync([$"file://{projectB}"], ct);

        Assert.Equal(2, bindCount);
        Assert.True(bootstrap.BindingGeneration > genAfterFirst);
        Assert.Equal(PathCanonicalizer.CanonicalizeRoot(projectB), bootstrap.Workspace.CanonicalRoot);
    }

    [Fact]
    public async Task RebindDeferred_KeepsRootsDirtyUntilInFlightRunCompletes()
    {
        string projectA = CreateTempDir();
        string projectB = CreateTempDir();
        string canonicalA = PathCanonicalizer.CanonicalizeRoot(projectA);
        string canonicalB = PathCanonicalizer.CanonicalizeRoot(projectB);
        var startedRoots = new List<string>();
        var startedA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bootstrap = new IndexBootstrapService(NullLogger<IndexBootstrapService>.Instance);
        bootstrap.TestRunBootstrapOverride = canonicalRoot =>
        {
            lock (startedRoots)
                startedRoots.Add(canonicalRoot);

            if (IndexBootstrapService.RootPathsEqual(canonicalRoot, canonicalA))
            {
                startedA.TrySetResult();
                releaseA.Task.GetAwaiter().GetResult();
            }

            SeedEmptyWorkspace(bootstrap, canonicalRoot);
        };
        var binding = new WorkspaceBindingService(bootstrap, NullLogger<WorkspaceBindingService>.Instance);
        var ct = TestContext.Current.CancellationToken;

        string previous = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            await bootstrap.StartAsync(CancellationToken.None);
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }

        await binding.EnsurePrimaryBoundFromRootsAsync([$"file://{projectA}"], ct);
        await startedA.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        int runA = bootstrap.Snapshot.RunGeneration;

        binding.MarkRootsDirty();
        await binding.EnsurePrimaryBoundFromRootsAsync([$"file://{projectB}"], ct);
        Assert.Equal(BootstrapPhase.Running, bootstrap.Snapshot.Phase);
        Assert.Equal(canonicalA, bootstrap.Snapshot.CanonicalRoot);

        releaseA.TrySetResult();
        await bootstrap.WaitForRunAsync(runA, ct);
        Assert.Equal(canonicalA, bootstrap.Workspace.CanonicalRoot);

        await binding.EnsurePrimaryBoundFromRootsAsync([$"file://{projectB}"], ct);
        await WaitUntilAsync(() =>
        {
            lock (startedRoots)
                return startedRoots.Any(root => IndexBootstrapService.RootPathsEqual(root, canonicalB));
        }, ct);
        await bootstrap.WaitForRunAsync(bootstrap.Snapshot.RunGeneration, ct);

        Assert.Equal(canonicalB, bootstrap.Workspace.CanonicalRoot);
    }

    [Fact]
    public async Task RebindFailureWhileBound_NextEnsureStartsRetry()
    {
        string projectA = CreateTempDir();
        string projectB = CreateTempDir();
        string canonicalA = PathCanonicalizer.CanonicalizeRoot(projectA);
        string canonicalB = PathCanonicalizer.CanonicalizeRoot(projectB);
        var bootstrap = new IndexBootstrapService(NullLogger<IndexBootstrapService>.Instance);
        bootstrap.TestRunBootstrapOverride = canonicalRoot => SeedEmptyWorkspace(bootstrap, canonicalRoot);
        var binding = new WorkspaceBindingService(bootstrap, NullLogger<WorkspaceBindingService>.Instance);
        var ct = TestContext.Current.CancellationToken;

        await binding.EnsurePrimaryBoundFromRootsAsync([$"file://{projectA}"], ct);
        await WaitUntilAsync(() => bootstrap.IsBound, ct);

        bootstrap.TestRunBootstrapOverride = _ => throw new InvalidOperationException("synthetic rebind failure");
        binding.MarkRootsDirty();
        await binding.EnsurePrimaryBoundFromRootsAsync([$"file://{projectB}"], ct);
        await WaitUntilAsync(() => bootstrap.Snapshot.Phase == BootstrapPhase.Failed, ct);

        // The failed rebind keeps the previous workspace serving...
        Assert.True(bootstrap.IsBound);
        Assert.Equal(canonicalA, bootstrap.Workspace.CanonicalRoot);

        // ...and the NEXT ensure call must start the retry (design: Failed → BootstrapForRoot (retry) → Running),
        // not early-return on IsBound and strand the Failed phase with no in-band recovery.
        bootstrap.TestRunBootstrapOverride = canonicalRoot => SeedEmptyWorkspace(bootstrap, canonicalRoot);
        await binding.EnsurePrimaryBoundFromRootsAsync([$"file://{projectB}"], ct);
        await WaitUntilAsync(() =>
            bootstrap.Snapshot.Phase == BootstrapPhase.Bound &&
            IndexBootstrapService.RootPathsEqual(bootstrap.Workspace.CanonicalRoot, canonicalB), ct);
        Assert.Null(bootstrap.Snapshot.LastFailureMessage);
    }

    private static string CreateTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "miller-bindsvc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void SeedEmptyWorkspace(IndexBootstrapService bootstrap, string canonicalRoot)
    {
        var workspace = WorkspaceContext.Create(canonicalRoot, AppContext.BaseDirectory) with
        {
            WorkspaceId = WorkspaceId.FromCanonicalRoot(canonicalRoot),
            CanonicalRoot = canonicalRoot,
        };
        bootstrap.SeedForTest(workspace, new IndexHolder(
            MillerRepositoryIndex.Build(Array.Empty<IndexedSymbol>()), builtRevision: 0));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (condition())
                return;
            await Task.Delay(10, cancellationToken);
        }

        Assert.True(condition(), "condition was not met before the timeout");
    }
}
