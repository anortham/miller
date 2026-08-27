using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Daemon.Engine;

/// <summary>
/// The compiler cache is project-stable and lives beside the per-operation generations under one build
/// output root (finding F7). Three coordinator duties follow from that split:
/// the reap must leave the cache alone, the disk budget must still see it, and a build that fails twice in
/// a row must be able to throw the cache away and try once more.
/// </summary>
public sealed class CtBuildCacheMaintenanceTests : IDisposable
{
    private const string WorkspaceId = "ws:build-cache";
    private const string Identity = "gen-1";
    private const string OwnerToken = "owner:build-cache";

    // 'g' plus twelve lowercase hex characters is what CtGenerationPaths.IsGenerationId accepts.
    private const string GenerationId = "gabcdef012345";

    private readonly string _root =
        Directory.CreateTempSubdirectory("miller-ct-build-cache-").FullName;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task The_reap_removes_a_superseded_generation_and_leaves_the_project_cache()
    {
        ContinuousTestWorkspace workspace = Workspace("project-reap");
        string generationRoot = Path.Combine(workspace.BuildOutputRoot, GenerationId);
        Directory.CreateDirectory(generationRoot);
        await File.WriteAllTextAsync(
            Path.Combine(generationRoot, "stale.txt"), "x", TestContext.Current.CancellationToken);
        string cacheFile = SeedCache(workspace);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        SeedTestCase(store, workspace);
        SeedReapEligibleGeneration(store, workspace);

        ContinuousTestCoordinator coordinator = Coordinator(store, new PassingProvider());
        await coordinator.RunSelectedAsync(RunRequest(workspace), TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(generationRoot));
        Assert.True(File.Exists(cacheFile));
    }

    /// <summary>
    /// The cache is the biggest directory a build output root holds. Leaving it out of the measurement would
    /// make the 20 GB budget blind to it.
    /// </summary>
    [Fact]
    public async Task The_project_cache_counts_against_the_generation_disk_budget()
    {
        var reported = new List<string>();
        ContinuousTestWorkspace workspace = Workspace("project-disk");
        Directory.CreateDirectory(Path.Combine(workspace.BuildOutputRoot, GenerationId));
        SeedCache(workspace);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        SeedTestCase(store, workspace);

        var coordinator = new ContinuousTestCoordinator(
            new PassingProvider(),
            store,
            runIdFactory: static () => "run:1",
            options: new ContinuousTestCoordinatorOptions
            {
                OwnerToken = OwnerToken,
                // One generation plus one cache directory: 8192 measured, over a 6000 budget. Counting the
                // generation alone reports 4096 and stays silent.
                GenerationDiskBudgetBytes = 6000,
                MeasureDirectoryBytes = static _ => 4096,
                LifecycleLog = reported.Add,
            });

        await coordinator.RunSelectedAsync(RunRequest(workspace), TestContext.Current.CancellationToken);

        Assert.Equal("generation_disk_over_budget bytes=8192 budget=6000", Assert.Single(reported));
    }

    [Fact]
    public async Task A_single_provider_failure_leaves_the_project_cache_alone()
    {
        ContinuousTestWorkspace workspace = Workspace("project-one-failure");
        string cacheFile = SeedCache(workspace);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        SeedTestCase(store, workspace);
        var provider = new FailingProvider(failures: 1, cacheFile);
        ContinuousTestCoordinator coordinator = Coordinator(store, provider);

        await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            coordinator.RunSelectedAsync(RunRequest(workspace), TestContext.Current.CancellationToken));

        // One failure is ordinary: a compile error in the tree fails the same way. Wiping the cache here
        // would turn every red build into a cold rebuild.
        Assert.Equal(1, provider.Calls);
        Assert.True(File.Exists(cacheFile));
    }

    [Fact]
    public async Task Two_consecutive_provider_failures_wipe_the_project_cache_and_retry_once()
    {
        ContinuousTestWorkspace workspace = Workspace("project-two-failures");
        string cacheFile = SeedCache(workspace);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        SeedTestCase(store, workspace);
        var provider = new FailingProvider(failures: 2, cacheFile);
        ContinuousTestCoordinator coordinator = Coordinator(store, provider);

        await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            coordinator.RunSelectedAsync(RunRequest(workspace), TestContext.Current.CancellationToken));
        ContinuousTestCoordinatorRunResult second = await coordinator.RunSelectedAsync(
            RunRequest(workspace), TestContext.Current.CancellationToken);

        // Call 1 fails. Call 2 fails, which wipes the cache and retries once as call 3.
        Assert.Equal(3, provider.Calls);
        Assert.Equal([true, true, false], provider.CacheSeen);
        Assert.False(Directory.Exists(CtGenerationPaths.CacheRoot(workspace)));
        Assert.Equal("passed", second.ProviderResult.Status);
    }

    [Fact]
    public async Task Two_consecutive_discovery_failures_wipe_the_project_cache_and_retry_once()
    {
        ContinuousTestWorkspace workspace = Workspace("project-discovery-failures");
        string cacheFile = SeedCache(workspace);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        SeedTestCase(store, workspace);
        var provider = new FailingProvider(failures: 2, cacheFile);
        ContinuousTestCoordinator coordinator = Coordinator(store, provider);
        var request = new ContinuousTestDiscoveryRequest(workspace);

        await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            coordinator.DiscoverAsync(request, TestContext.Current.CancellationToken));
        await coordinator.DiscoverAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(3, provider.Calls);
        Assert.False(Directory.Exists(CtGenerationPaths.CacheRoot(workspace)));
    }

    [Fact]
    public async Task A_cache_reap_failure_is_debt_and_does_not_fail_the_verdict()
    {
        ContinuousTestWorkspace workspace = Workspace("project-cache-debt");
        string cacheFile = SeedCache(workspace);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        SeedTestCase(store, workspace);
        var reported = new List<string>();
        var janitor = new CtBuildCacheJanitor(
            Path.Combine(_root, "machine", "build"),
            workspaceBudgetBytes: 1,
            machineBudgetBytes: 1,
            inactivity: TimeSpan.FromDays(7),
            utcNow: static () => DateTimeOffset.UtcNow,
            report: reported.Add,
            reap: static _ => CtReapOutcome.RenameFailed);
        var coordinator = new ContinuousTestCoordinator(
            new PassingProvider(),
            store,
            runIdFactory: static () => "run:cache-debt",
            options: new ContinuousTestCoordinatorOptions
            {
                OwnerToken = OwnerToken,
                BuildCacheJanitor = janitor,
            });

        ContinuousTestCoordinatorRunResult result = await coordinator.RunSelectedAsync(
            RunRequest(workspace), TestContext.Current.CancellationToken);

        Assert.Equal("passed", result.ProviderResult.Status);
        Assert.True(File.Exists(cacheFile));
        CtGenerationReapDebtRecord debt = Assert.Single(store.ListCtGenerationReapDebt());
        Assert.Equal("cache/cargo", debt.DirectoryName);
        Assert.Contains(reported, message => message.Contains("cache_reap_failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_cache_delete_failure_persists_the_existing_reap_remnant()
    {
        ContinuousTestWorkspace workspace = Workspace("project-cache-remnant");
        string cacheFile = SeedCache(workspace);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        SeedTestCase(store, workspace);
        var janitor = new CtBuildCacheJanitor(
            Path.Combine(_root, "machine", "build"),
            workspaceBudgetBytes: 1,
            machineBudgetBytes: 1,
            inactivity: TimeSpan.FromDays(7),
            utcNow: static () => DateTimeOffset.UtcNow,
            reap: path => CtGenerationPaths.TryReapDetailed(
                path,
                static (source, destination) => Directory.Move(source, destination),
                static _ => throw new IOException("sharing violation")));
        var coordinator = new ContinuousTestCoordinator(
            new PassingProvider(),
            store,
            runIdFactory: static () => "run:cache-remnant",
            options: new ContinuousTestCoordinatorOptions
            {
                OwnerToken = OwnerToken,
                BuildCacheJanitor = janitor,
            });

        ContinuousTestCoordinatorRunResult result = await coordinator.RunSelectedAsync(
            RunRequest(workspace), TestContext.Current.CancellationToken);

        Assert.Equal("passed", result.ProviderResult.Status);
        Assert.False(File.Exists(cacheFile));
        CtGenerationReapDebtRecord debt = Assert.Single(store.ListCtGenerationReapDebt());
        string remnant = Path.Combine(workspace.BuildOutputRoot, debt.DirectoryName);
        Assert.StartsWith("cache/cargo.reap-", debt.DirectoryName, StringComparison.Ordinal);
        Assert.False(Directory.Exists(CtGenerationPaths.CacheDirectory(workspace, "cargo")));
        Assert.True(Directory.Exists(remnant));
    }

    [Fact]
    public async Task Disk_accounting_counts_only_ct_prefixed_peer_roots_under_the_miller_sidecar()
    {
        ContinuousTestWorkspace workspace = MigratedWorkspace("ct-aaaabbbbcccc");
        Directory.CreateDirectory(Path.Combine(workspace.BuildOutputRoot, GenerationId));
        string peer = Path.Combine(_root, ".miller", "ct-ddddeeeeffff");
        Directory.CreateDirectory(Path.Combine(peer, GenerationId));
        Directory.CreateDirectory(Path.Combine(_root, ".miller", "spool", "cache"));
        Directory.CreateDirectory(Path.Combine(_root, ".miller", "logs"));
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        SeedTestCase(store, workspace);
        ContinuousTestCoordinator coordinator = Coordinator(store, new PassingProvider());

        await coordinator.RunSelectedAsync(RunRequest(workspace), TestContext.Current.CancellationToken);

        string[] measured = store.ListCtGenerationDisk()
            .Select(row => row.BuildOutputRoot)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            new[] { workspace.BuildOutputRoot, peer }.Order(StringComparer.Ordinal).ToArray(),
            measured);
    }

    [Fact]
    public async Task The_maintenance_tail_reclaims_an_idle_legacy_build_tree()
    {
        ContinuousTestWorkspace workspace = MigratedWorkspace("ct-aaaabbbbcccc");
        string legacyRoot = SeedLegacyRoot("0123456789ab", withLockFile: true);
        string daemonStatus = Path.Combine(_root, ".miller", "ct", "daemon.status.json");
        File.WriteAllText(daemonStatus, "{}");
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        SeedTestCase(store, workspace);
        ContinuousTestCoordinator coordinator = Coordinator(store, new PassingProvider());

        await coordinator.RunSelectedAsync(RunRequest(workspace), TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(legacyRoot));
        Assert.False(Directory.Exists(Path.Combine(_root, ".miller", "ct", "build")));
        Assert.True(File.Exists(daemonStatus));
    }

    [Fact]
    public async Task A_legacy_build_root_a_live_process_holds_survives_the_sweep()
    {
        ContinuousTestWorkspace workspace = MigratedWorkspace("ct-aaaabbbbcccc");
        string legacyRoot = SeedLegacyRoot("0123456789ab", withLockFile: true);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        SeedTestCase(store, workspace);
        ContinuousTestCoordinator coordinator = Coordinator(store, new PassingProvider());
        using (CtBuildRootOperationLease.Acquire(legacyRoot, TestContext.Current.CancellationToken))
        {
            await coordinator.RunSelectedAsync(RunRequest(workspace), TestContext.Current.CancellationToken);
        }

        Assert.True(File.Exists(Path.Combine(legacyRoot, GenerationId, "stale.bin")));
    }

    [Fact]
    public async Task A_legacy_build_root_without_an_operation_lock_file_is_left_alone()
    {
        ContinuousTestWorkspace workspace = MigratedWorkspace("ct-aaaabbbbcccc");
        string legacyRoot = SeedLegacyRoot("0123456789ab", withLockFile: false);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        SeedTestCase(store, workspace);
        ContinuousTestCoordinator coordinator = Coordinator(store, new PassingProvider());

        await coordinator.RunSelectedAsync(RunRequest(workspace), TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(legacyRoot, GenerationId, "stale.bin")));
    }

    [Fact]
    public async Task A_workspace_still_building_under_the_legacy_tree_never_sweeps_it()
    {
        string ownRoot = Path.Combine(_root, ".miller", "ct", "build", "aaaaaaaaaaaa");
        ContinuousTestWorkspace workspace = WorkspaceAt(ownRoot);
        string siblingRoot = SeedLegacyRoot("bbbbbbbbbbbb", withLockFile: true);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        SeedTestCase(store, workspace);
        ContinuousTestCoordinator coordinator = Coordinator(store, new PassingProvider());

        await coordinator.RunSelectedAsync(RunRequest(workspace), TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(siblingRoot, GenerationId, "stale.bin")));
    }

    private string SeedLegacyRoot(string projectSegment, bool withLockFile)
    {
        string legacyRoot = Path.Combine(_root, ".miller", "ct", "build", projectSegment);
        Directory.CreateDirectory(Path.Combine(legacyRoot, GenerationId));
        File.WriteAllText(Path.Combine(legacyRoot, GenerationId, "stale.bin"), "x");
        if (withLockFile)
            File.WriteAllText(Path.Combine(legacyRoot, CtBuildRootOperationLease.LockFileName), string.Empty);
        return legacyRoot;
    }

    private ContinuousTestWorkspace MigratedWorkspace(string buildRootName) =>
        WorkspaceAt(Path.Combine(_root, ".miller", buildRootName));

    private ContinuousTestWorkspace WorkspaceAt(string buildOutputRoot)
    {
        string project = Path.Combine(_root, "src", "App.Tests.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        return new ContinuousTestWorkspace(
            WorkspaceId,
            _root,
            project,
            buildOutputRoot);
    }

    private ContinuousTestCoordinator Coordinator(ContinuousTestStore store, IContinuousTestProvider provider) =>
        new(
            provider,
            store,
            runIdFactory: static () => "run:1",
            options: new ContinuousTestCoordinatorOptions { OwnerToken = OwnerToken });

    private static string SeedCache(ContinuousTestWorkspace workspace)
    {
        string cacheDirectory = CtGenerationPaths.CacheDirectory(workspace, "cargo");
        Directory.CreateDirectory(cacheDirectory);
        string cacheFile = Path.Combine(cacheDirectory, "fingerprint.bin");
        File.WriteAllText(cacheFile, "warm");
        return cacheFile;
    }

    private ContinuousTestWorkspace Workspace(string buildOutputName)
    {
        string project = Path.Combine(_root, "src", "App.Tests.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        return new ContinuousTestWorkspace(
            WorkspaceId,
            _root,
            project,
            Path.Combine(_root, "ct-build", buildOutputName));
    }

    private static ContinuousTestCoordinatorRunRequest RunRequest(ContinuousTestWorkspace workspace) =>
        new(
            Workspace: workspace,
            SelectedRevision: "2",
            CurrentRevision: "2",
            IndexIdentity: Identity,
            TestCaseIds: ["test:app"]);

    private static void SeedTestCase(ContinuousTestStore store, ContinuousTestWorkspace workspace) =>
        store.PutTestCase(new ContinuousTestCase(
            Id: "test:app",
            WorkspaceId: WorkspaceId,
            Name: "AppTests",
            QualifiedName: "App.Tests.AppTests",
            Selector: "App.Tests.AppTests",
            FilePath: "tests/AppTests.cs",
            Framework: "xunit",
            Role: ContinuousTestRole.TestCase,
            Source: "ct-provider:dotnet",
            Confidence: 1.0,
            Metadata: new Dictionary<string, object?> { ["ct_project_path"] = workspace.ProjectPath }));

    private static void SeedReapEligibleGeneration(ContinuousTestStore store, ContinuousTestWorkspace workspace)
    {
        store.PutCtGenerationAllocated(new CtGenerationRecord(
            GenerationId: GenerationId,
            BuildOutputRoot: workspace.BuildOutputRoot,
            State: CtGenerationStates.Allocated,
            OwnerToken: OwnerToken,
            AllocatedAt: DateTimeOffset.UtcNow,
            CompletedAt: null));
        Assert.True(store.MarkCtGenerationReapEligible(workspace.BuildOutputRoot, GenerationId, OwnerToken));
    }

    private sealed class PassingProvider : IContinuousTestProvider
    {
        public Task<IReadOnlyList<ProviderTestCase>> DiscoverAsync(
            ContinuousTestWorkspace workspace,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProviderTestCase>>([]);

        public Task<ProviderRunResult> RunAsync(
            ContinuousTestProviderRunRequest request,
            CancellationToken cancellationToken = default)
        {
            string runId = request.RunId ?? "run:1";
            return Task.FromResult(new ProviderRunResult(
                runId,
                "passed",
                CaseResults: request.TestCaseIds
                    .Select(testCaseId => new ProviderCaseResult(
                        Id: $"{runId}:{testCaseId}",
                        TestCaseId: testCaseId,
                        Status: "passed",
                        ResultRevision: request.SelectedRevision,
                        IndexIdentity: request.IndexIdentity))
                    .ToArray()));
        }
    }

    /// <summary>A provider whose build gate fails the first <c>failures</c> times, then succeeds.</summary>
    private sealed class FailingProvider : IContinuousTestProvider
    {
        private readonly int _failures;
        private readonly string _cacheFile;
        private readonly PassingProvider _inner = new();

        public FailingProvider(int failures, string cacheFile)
        {
            _failures = failures;
            _cacheFile = cacheFile;
        }

        public int Calls { get; private set; }

        public List<bool> CacheSeen { get; } = [];

        public Task<IReadOnlyList<ProviderTestCase>> DiscoverAsync(
            ContinuousTestWorkspace workspace,
            CancellationToken cancellationToken = default)
        {
            Observe();
            return Calls <= _failures
                ? Task.FromException<IReadOnlyList<ProviderTestCase>>(BuildGateFailure())
                : _inner.DiscoverAsync(workspace, cancellationToken);
        }

        public Task<ProviderRunResult> RunAsync(
            ContinuousTestProviderRunRequest request,
            CancellationToken cancellationToken = default)
        {
            Observe();
            return Calls <= _failures
                ? Task.FromException<ProviderRunResult>(BuildGateFailure())
                : _inner.RunAsync(request, cancellationToken);
        }

        private void Observe()
        {
            Calls++;
            CacheSeen.Add(File.Exists(_cacheFile));
        }

        private static ContinuousTestProviderException BuildGateFailure() =>
            new("cargo test --no-run exited 101.");
    }
}
