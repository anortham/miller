using Microsoft.Data.Sqlite;
using Miller.Indexing.Testing;
using Miller.Testing;
using Miller.Tests.Testing.Daemon.Engine;
using Miller.Tests.Testing.Selection;
using Xunit;

namespace Miller.Tests.Testing.Daemon.ControlPlane;

/// <summary>
/// One family daemon serves every registered, opted-in worktree of its repo. The host keeps one
/// loop and one lease; each adopted worktree is a CONTEXT the loop iterates - its own store,
/// queue, poller, and status record - sharing only the execution budget.
///
/// <para>Fixtures build the real linked-worktree pointer-file shape by hand, exactly like
/// <see cref="Engine.ContinuousTestWorktreePolicyTests"/>: a <c>.git</c> FILE holding
/// <c>gitdir: &lt;admin dir&gt;</c> plus the admin dir's <c>commondir</c> pointer. No git
/// subprocess, no julie, no real CT provider.</para>
/// </summary>
public sealed class ContinuousTestDaemonWorktreeAdoptionTests : IDisposable
{
    private readonly string _dir =
        Directory.CreateTempSubdirectory("miller-ct-adopt-").FullName;

    private string MainRoot => Path.Combine(_dir, "main");
    private string WorktreeRoot => Path.Combine(_dir, "wt");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task An_optedin_family_worktree_is_adopted_once_and_its_status_record_names_the_serving_daemon()
    {
        BuildLinkedWorktree();
        EnableMain();
        var disposed = new DisposeFlag();
        int created = 0;
        var adoption = new ContinuousTestWorktreeAdoptionOptions
        {
            DiscoverRegisteredRoots = () => [WorktreeRoot],
            CreateContext = root =>
            {
                Interlocked.Increment(ref created);
                return new ContinuousTestWorkspaceContext
                {
                    WorkspaceRoot = root,
                    WorkspaceId = "ws:wt",
                    Owned = disposed,
                };
            },
            ScanInterval = TimeSpan.Zero,
        };

        using var cts = new CancellationTokenSource();
        Task<ContinuousTestDaemonSnapshot> run = ContinuousTestDaemonHost.RunAsync(
            MainRoot,
            HostOptions(adoption),
            cts.Token);
        try
        {
            CtDaemonStatusRecord record = await WaitForWorktreeStatusAsync(
                state: CtDaemonLifecycleState.Running);
            Assert.Contains(Path.GetFullPath(MainRoot), record.Reason, StringComparison.Ordinal);
            await WaitPassesAsync(3);
            Assert.Equal(1, Volatile.Read(ref created));
            Assert.False(run.IsCompleted, "adoption ended the daemon loop");
        }
        finally
        {
            await cts.CancelAsync();
            await run;
        }

        // The shutdown tail releases every adopted context and marks its record stopped.
        Assert.True(disposed.Disposed, "the adopted context was not disposed on shutdown");
        CtDaemonStatusRecord? final = CtDaemonLease.TryReadStatus(WorktreeRoot);
        Assert.Equal(CtDaemonLifecycleState.Stopped, final?.State);
    }

    [Fact]
    public async Task A_worktree_of_a_never_enabled_repo_is_not_adopted_and_nothing_appears_in_it()
    {
        BuildLinkedWorktree();
        int created = 0;
        var adoption = new ContinuousTestWorktreeAdoptionOptions
        {
            DiscoverRegisteredRoots = () => [WorktreeRoot],
            CreateContext = _ =>
            {
                Interlocked.Increment(ref created);
                return null;
            },
            ScanInterval = TimeSpan.Zero,
        };

        using var cts = new CancellationTokenSource();
        Task<ContinuousTestDaemonSnapshot> run = ContinuousTestDaemonHost.RunAsync(
            MainRoot,
            HostOptions(adoption),
            cts.Token);
        try
        {
            await WaitPassesAsync(5);
            Assert.Equal(0, Volatile.Read(ref created));
            Assert.False(Directory.Exists(Path.Combine(WorktreeRoot, ".miller")));
        }
        finally
        {
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task A_worktree_holding_its_own_live_daemon_lease_is_not_adopted()
    {
        BuildLinkedWorktree();
        EnableMain();
        using CtDaemonLease? own = CtDaemonLease.TryAcquire(WorktreeRoot, "test");
        Assert.NotNull(own);

        int created = 0;
        var adoption = new ContinuousTestWorktreeAdoptionOptions
        {
            DiscoverRegisteredRoots = () => [WorktreeRoot],
            CreateContext = _ =>
            {
                Interlocked.Increment(ref created);
                return null;
            },
            ScanInterval = TimeSpan.Zero,
        };

        using var cts = new CancellationTokenSource();
        Task<ContinuousTestDaemonSnapshot> run = ContinuousTestDaemonHost.RunAsync(
            MainRoot,
            HostOptions(adoption),
            cts.Token);
        try
        {
            await WaitPassesAsync(5);
            Assert.Equal(0, Volatile.Read(ref created));
        }
        finally
        {
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task A_root_outside_the_family_is_not_adopted()
    {
        BuildLinkedWorktree();
        EnableMain();

        // A second, unrelated repo with its own linked worktree - same shape, different main.
        string otherMain = Path.Combine(_dir, "other-main");
        string otherWt = Path.Combine(_dir, "other-wt");
        BuildLinkedWorktreeAt(otherMain, otherWt, "other");
        WriteMarker(ContinuousTestPolicy.EnabledMarkerPath(otherMain));

        int created = 0;
        var adoption = new ContinuousTestWorktreeAdoptionOptions
        {
            // The daemon's own root and the foreign worktree: neither may attach.
            DiscoverRegisteredRoots = () => [MainRoot, otherWt],
            CreateContext = _ =>
            {
                Interlocked.Increment(ref created);
                return null;
            },
            ScanInterval = TimeSpan.Zero,
        };

        using var cts = new CancellationTokenSource();
        Task<ContinuousTestDaemonSnapshot> run = ContinuousTestDaemonHost.RunAsync(
            MainRoot,
            HostOptions(adoption),
            cts.Token);
        try
        {
            await WaitPassesAsync(5);
            Assert.Equal(0, Volatile.Read(ref created));
        }
        finally
        {
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task A_missing_worktree_root_detaches_the_context_and_the_loop_keeps_running()
    {
        BuildLinkedWorktree();
        EnableMain();
        var disposed = new DisposeFlag();
        var adoption = new ContinuousTestWorktreeAdoptionOptions
        {
            DiscoverRegisteredRoots = () => [WorktreeRoot],
            CreateContext = root => new ContinuousTestWorkspaceContext
            {
                WorkspaceRoot = root,
                WorkspaceId = "ws:wt",
                Owned = disposed,
            },
            ScanInterval = TimeSpan.Zero,
        };

        using var cts = new CancellationTokenSource();
        Task<ContinuousTestDaemonSnapshot> run = ContinuousTestDaemonHost.RunAsync(
            MainRoot,
            HostOptions(adoption),
            cts.Token);
        try
        {
            await WaitForWorktreeStatusAsync(state: CtDaemonLifecycleState.Running);

            Directory.Delete(WorktreeRoot, recursive: true);
            await WaitForAsync(() => disposed.Disposed);

            Assert.True(disposed.Disposed, "the context of the missing root was not disposed");
            Assert.False(run.IsCompleted, "a missing worktree root ended the daemon loop");
            await WaitPassesAsync(3);
            // Detach never recreates anything under the vanished root.
            Assert.False(Directory.Exists(WorktreeRoot));
        }
        finally
        {
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task A_routed_run_reaches_only_the_worktrees_own_store()
    {
        BuildLinkedWorktree();
        EnableMain();
        const string wtId = "ws:wt";
        string wtProject = Path.Combine(WorktreeRoot, "src", "App.Tests.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(wtProject)!);
        File.WriteAllText(wtProject, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        using var wtStore = new ContinuousTestStore(CtSchema.DbPathFor(WorktreeRoot));
        wtStore.PutTestCase(new ContinuousTestCase(
            Id: "test:wt",
            WorkspaceId: wtId,
            Name: "test:wt",
            QualifiedName: "test:wt",
            Selector: "test:wt",
            FilePath: "tests/AppTests.cs",
            Source: "ct-provider:dotnet",
            Metadata: new Dictionary<string, object?> { ["ct_project_path"] = Path.GetFullPath(wtProject) }));

        var facts = new FakeMillerFactSource { Current = new CtIndexCursor("gen-1", 2) };
        facts.Symbols.Add(FakeMillerFactSource.Symbol("sym:wt", "App", "src/App.cs"));
        facts.Tests.Add(FakeMillerFactSource.Hit("test:wt", "AppTests", "tests/AppTests.cs", isTest: true));
        var provider = new FakeContinuousTestProvider
        {
            RunResult = new ProviderRunResult(
                "run:wt",
                "passed",
                CaseResults: [new ProviderCaseResult("r1", "test:wt", "passed", "2", "gen-1")]),
        };
        var wtQueue = new ContinuousTestDaemonQueue(
            wtStore,
            new ContinuousTestImpactSelector(wtStore, facts),
            new ContinuousTestCoordinator(provider, wtStore));

        var primaryEnqueuer = new RecordingEnqueuer();
        var adoption = new ContinuousTestWorktreeAdoptionOptions
        {
            DiscoverRegisteredRoots = () => [WorktreeRoot],
            CreateContext = root => new ContinuousTestWorkspaceContext
            {
                WorkspaceRoot = root,
                WorkspaceId = wtId,
                Store = wtStore,
                Queue = wtQueue,
                Projects = [new ContinuousTestProject("proj:wt", wtId, wtProject, Framework: "xunit")],
            },
            ScanInterval = TimeSpan.Zero,
        };
        var options = new ContinuousTestDaemonHostOptions
        {
            Enabled = true,
            AcquireLease = false,
            PollInterval = TimeSpan.FromMilliseconds(5),
            WorktreeAdoption = adoption,
            Enqueuer = primaryEnqueuer,
            Budget = CtExecutionBudget.ForMillerHome(Path.Combine(_dir, "budget-home")),
        };

        using var cts = new CancellationTokenSource();
        Task<ContinuousTestDaemonSnapshot> run = ContinuousTestDaemonHost.RunAsync(
            MainRoot,
            options,
            cts.Token);
        try
        {
            await WaitForWorktreeStatusAsync(state: CtDaemonLifecycleState.Running);

            CtDaemonCommandRequest request = CtDaemonRouting.WriteRoutedRequest(
                MainRoot,
                CtDaemonCommandKind.Run,
                reason: "run",
                freshness: new CtFreshnessKey("gen-1", 2),
                targetWorkspaceRoot: WorktreeRoot);

            await WaitForAsync(() => wtStore.ListContinuousTestStatuses(wtId).Count > 0);

            // The run landed in the WORKTREE's ct.db under the worktree's id...
            Assert.NotEmpty(wtStore.ListContinuousTestStatuses(wtId));
            Assert.True(File.Exists(CtSchema.DbPathFor(WorktreeRoot)));
            Assert.Equal("run", CtCommandChannel.TryReadAck(MainRoot, request.CommandId)?.Reason);

            // ...and nothing crossed into the primary context: no main ct.db, no primary enqueue.
            Assert.False(File.Exists(CtSchema.DbPathFor(MainRoot)));
            Assert.Empty(primaryEnqueuer.Changes);
        }
        finally
        {
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task A_routed_stop_detaches_the_worktree_only_and_a_routed_run_reattaches_it()
    {
        BuildLinkedWorktree();
        EnableMain();
        int created = 0;
        var adoption = new ContinuousTestWorktreeAdoptionOptions
        {
            DiscoverRegisteredRoots = () => [WorktreeRoot],
            CreateContext = root =>
            {
                Interlocked.Increment(ref created);
                return new ContinuousTestWorkspaceContext
                {
                    WorkspaceRoot = root,
                    WorkspaceId = "ws:wt",
                };
            },
            ScanInterval = TimeSpan.Zero,
        };

        using var cts = new CancellationTokenSource();
        Task<ContinuousTestDaemonSnapshot> run = ContinuousTestDaemonHost.RunAsync(
            MainRoot,
            HostOptions(adoption),
            cts.Token);
        try
        {
            await WaitForWorktreeStatusAsync(state: CtDaemonLifecycleState.Running);

            CtDaemonCommandAck? ack = await Task.Run(() => CtDaemonRouting.RequestDetach(
                MainRoot,
                WorktreeRoot,
                ackTimeout: TimeSpan.FromSeconds(5)));

            Assert.Equal("detached", ack?.Reason);
            Assert.False(run.IsCompleted, "a worktree stop killed the family daemon");
            CtDaemonStatusRecord? record = CtDaemonLease.TryReadStatus(WorktreeRoot);
            Assert.Equal(CtDaemonLifecycleState.Stopped, record?.State);
            Assert.Equal("detached", record?.Reason);

            // A stop-detached worktree stays detached: the scan must not re-adopt it...
            int adoptedBeforeWait = Volatile.Read(ref created);
            await WaitPassesAsync(5);
            Assert.Equal(adoptedBeforeWait, Volatile.Read(ref created));

            // ...until an explicit routed run asks for it again.
            CtDaemonCommandRequest rerun = CtDaemonRouting.WriteRoutedRequest(
                MainRoot,
                CtDaemonCommandKind.Run,
                reason: "run",
                freshness: null,
                targetWorkspaceRoot: WorktreeRoot);
            CtDaemonCommandAck? rerunAck = await Task.Run(() => CtCommandChannel.WaitForAck(
                MainRoot,
                rerun.CommandId,
                TimeSpan.FromSeconds(5)));
            Assert.Equal("run", rerunAck?.Reason);
            Assert.Equal(adoptedBeforeWait + 1, Volatile.Read(ref created));
        }
        finally
        {
            await cts.CancelAsync();
            await run;
        }
    }

    /// <summary>
    /// Defect D2: the explicit-run handler fell back to <c>StartedAt</c>, so every
    /// <c>tests run</c> after an index advance selected at the daemon's BIRTH revision. The store
    /// rightly records stale-revision results as history-only, so the verdict never converged and
    /// only a daemon restart recovered. An explicit run must select at the LATEST key the poller
    /// observed. This is the PRIMARY context; the routed test below proves the shared path.
    /// </summary>
    [Fact]
    public async Task An_explicit_run_selects_the_latest_observed_key_not_the_daemons_start_key()
    {
        Directory.CreateDirectory(MainRoot);
        const string mainId = "ws:main";
        string project = Path.Combine(MainRoot, "src", "App.Tests.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        using var store = new ContinuousTestStore(CtSchema.DbPathFor(MainRoot));
        var enqueueLog = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var queue = new ContinuousTestDaemonQueue(
            store,
            new ContinuousTestImpactSelector(store, new FakeMillerFactSource
            {
                Current = new CtIndexCursor("gen-1", 27),
            }),
            new ContinuousTestCoordinator(new FakeContinuousTestProvider(), store),
            lifecycleLog: enqueueLog.Enqueue);
        var source = new ScriptedRevisionSource();
        source.Observations.Enqueue(Observation(mainId, revision: 21));
        source.Observations.Enqueue(Observation(mainId, revision: 27));
        var options = new ContinuousTestDaemonHostOptions
        {
            Enabled = true,
            AcquireLease = false,
            WorkspaceId = mainId,
            Store = store,
            Queue = queue,
            Poller = new ContinuousTestRevisionPoller(source),
            Projects = [new ContinuousTestProject("proj:main", mainId, project, Framework: "xunit")],
            Budget = CtExecutionBudget.ForMillerHome(Path.Combine(_dir, "budget-home")),
            PollInterval = TimeSpan.FromMilliseconds(5),
        };

        using var cts = new CancellationTokenSource();
        Task<ContinuousTestDaemonSnapshot> run = ContinuousTestDaemonHost.RunAsync(
            MainRoot,
            options,
            cts.Token);
        try
        {
            // Two observations landed and a third pass began, so the host has stored 27 as the
            // latest key before the command arrives.
            await WaitForAsync(() => source.RefreshCount >= 3);

            CtDaemonCommandRequest request = CtCommandChannel.WriteRequest(
                MainRoot,
                CtDaemonCommandKind.Run,
                reason: "run",
                freshness: null);
            await WaitForAsync(() => CtCommandChannel.TryReadAck(MainRoot, request.CommandId) is not null);
            await WaitForAsync(() => EnqueueLines(enqueueLog).Count > 0);

            IReadOnlyList<string> enqueued = EnqueueLines(enqueueLog);
            Assert.Contains(enqueued, line => line.Contains("revision=27", StringComparison.Ordinal));
            Assert.DoesNotContain(enqueued, line => line.Contains("revision=21", StringComparison.Ordinal));
        }
        finally
        {
            await cts.CancelAsync();
            await run;
        }
    }

    /// <summary>Defect D2, routed shape: the same key selection serves an ADOPTED context.</summary>
    [Fact]
    public async Task A_routed_explicit_run_selects_the_worktrees_latest_observed_key()
    {
        BuildLinkedWorktree();
        EnableMain();
        const string wtId = "ws:wt";
        string project = Path.Combine(WorktreeRoot, "src", "App.Tests.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        using var wtStore = new ContinuousTestStore(CtSchema.DbPathFor(WorktreeRoot));
        var enqueueLog = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var wtQueue = new ContinuousTestDaemonQueue(
            wtStore,
            new ContinuousTestImpactSelector(wtStore, new FakeMillerFactSource
            {
                Current = new CtIndexCursor("gen-1", 27),
            }),
            new ContinuousTestCoordinator(new FakeContinuousTestProvider(), wtStore),
            lifecycleLog: enqueueLog.Enqueue);
        var source = new ScriptedRevisionSource();
        source.Observations.Enqueue(Observation(wtId, revision: 21));
        source.Observations.Enqueue(Observation(wtId, revision: 27));
        var adoption = new ContinuousTestWorktreeAdoptionOptions
        {
            DiscoverRegisteredRoots = () => [WorktreeRoot],
            CreateContext = root => new ContinuousTestWorkspaceContext
            {
                WorkspaceRoot = root,
                WorkspaceId = wtId,
                Store = wtStore,
                Queue = wtQueue,
                Poller = new ContinuousTestRevisionPoller(source),
                Projects = [new ContinuousTestProject("proj:wt", wtId, project, Framework: "xunit")],
            },
            ScanInterval = TimeSpan.Zero,
        };
        var options = new ContinuousTestDaemonHostOptions
        {
            Enabled = true,
            AcquireLease = false,
            Enqueuer = new RecordingEnqueuer(),
            PollInterval = TimeSpan.FromMilliseconds(5),
            WorktreeAdoption = adoption,
            Budget = CtExecutionBudget.ForMillerHome(Path.Combine(_dir, "budget-home")),
        };

        using var cts = new CancellationTokenSource();
        Task<ContinuousTestDaemonSnapshot> run = ContinuousTestDaemonHost.RunAsync(
            MainRoot,
            options,
            cts.Token);
        try
        {
            await WaitForWorktreeStatusAsync(state: CtDaemonLifecycleState.Running);
            await WaitForAsync(() => source.RefreshCount >= 3);

            CtDaemonCommandRequest request = CtDaemonRouting.WriteRoutedRequest(
                MainRoot,
                CtDaemonCommandKind.Run,
                reason: "run",
                freshness: null,
                targetWorkspaceRoot: WorktreeRoot);
            await WaitForAsync(() => CtCommandChannel.TryReadAck(MainRoot, request.CommandId) is not null);
            await WaitForAsync(() => EnqueueLines(enqueueLog).Count > 0);

            IReadOnlyList<string> enqueued = EnqueueLines(enqueueLog);
            Assert.Contains(enqueued, line => line.Contains("revision=27", StringComparison.Ordinal));
            Assert.DoesNotContain(enqueued, line => line.Contains("revision=21", StringComparison.Ordinal));
        }
        finally
        {
            await cts.CancelAsync();
            await run;
        }
    }

    /// <summary>
    /// The <c>("unspecified", 0)</c> sentinel tail is GONE (the watermark-freshness design removes
    /// it everywhere): a run enqueued at a fabricated key can never produce committed-fresh
    /// results, so it burned the whole suite to store history nobody can match. With no observed
    /// key yet, the daemon refuses the run honestly and enqueues nothing.
    /// </summary>
    [Fact]
    public async Task An_explicit_run_before_any_observed_key_is_rejected_and_enqueues_nothing()
    {
        Directory.CreateDirectory(MainRoot);
        const string mainId = "ws:main";
        string project = Path.Combine(MainRoot, "src", "App.Tests.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        using var store = new ContinuousTestStore(CtSchema.DbPathFor(MainRoot));
        var enqueueLog = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var queue = new ContinuousTestDaemonQueue(
            store,
            new ContinuousTestImpactSelector(store, new FakeMillerFactSource()),
            new ContinuousTestCoordinator(new FakeContinuousTestProvider(), store),
            lifecycleLog: enqueueLog.Enqueue);
        var options = new ContinuousTestDaemonHostOptions
        {
            Enabled = true,
            AcquireLease = false,
            WorkspaceId = mainId,
            Store = store,
            Queue = queue,

            // No poller: no key has ever been observed and none ever will be.
            Projects = [new ContinuousTestProject("proj:main", mainId, project, Framework: "xunit")],
            Budget = CtExecutionBudget.ForMillerHome(Path.Combine(_dir, "budget-home")),
            PollInterval = TimeSpan.FromMilliseconds(5),
        };

        using var cts = new CancellationTokenSource();
        Task<ContinuousTestDaemonSnapshot> run = ContinuousTestDaemonHost.RunAsync(
            MainRoot,
            options,
            cts.Token);
        try
        {
            CtDaemonCommandRequest request = CtCommandChannel.WriteRequest(
                MainRoot,
                CtDaemonCommandKind.Run,
                reason: "run",
                freshness: null);
            await WaitForAsync(() => CtCommandChannel.TryReadAck(MainRoot, request.CommandId) is not null);

            CtDaemonCommandAck? ack = CtCommandChannel.TryReadAck(MainRoot, request.CommandId);
            Assert.Equal(CtDaemonCommandState.Rejected, ack?.State);
            Assert.Equal("no-live-key", ack?.Reason);
            Assert.False(run.IsCompleted, "a refused run ended the daemon loop");
            Assert.Empty(EnqueueLines(enqueueLog));
            Assert.False(File.Exists(CtSchema.DbPathFor(MainRoot)));
        }
        finally
        {
            await cts.CancelAsync();
            await run;
        }
    }

    private static ContinuousTestRevisionObservation Observation(string workspaceId, long revision) =>
        new(
            workspaceId,
            new CtFreshnessKey("gen-1", revision),
            IndexFresh: true,
            Status: "fresh",
            ObservedAt: DateTimeOffset.UtcNow);

    private static IReadOnlyList<string> EnqueueLines(IEnumerable<string> lifecycleLog) =>
        lifecycleLog.Where(line => line.StartsWith("ct enqueue", StringComparison.Ordinal)).ToArray();

    [Fact]
    public async Task The_kill_switch_stops_the_daemon_before_any_discovery_or_filesystem_read()
    {
        BuildLinkedWorktree();
        int discoveries = 0;
        var adoption = new ContinuousTestWorktreeAdoptionOptions
        {
            DiscoverRegisteredRoots = () =>
            {
                Interlocked.Increment(ref discoveries);
                return [WorktreeRoot];
            },
            CreateContext = _ => null,
            ScanInterval = TimeSpan.Zero,
        };

        ContinuousTestDaemonSnapshot snapshot = await ContinuousTestDaemonHost.RunAsync(
            MainRoot,
            new ContinuousTestDaemonHostOptions
            {
                KillSwitch = "off",
                AcquireLease = false,
                WorktreeAdoption = adoption,
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(CtDaemonLifecycleState.Stopped, snapshot.State);
        Assert.Equal("disabled", snapshot.Reason);
        Assert.Equal(0, Volatile.Read(ref discoveries));
        Assert.False(Directory.Exists(Path.Combine(MainRoot, ".miller")));
        Assert.False(Directory.Exists(Path.Combine(WorktreeRoot, ".miller")));
    }

    private ContinuousTestDaemonHostOptions HostOptions(ContinuousTestWorktreeAdoptionOptions adoption) =>
        new()
        {
            Enabled = true,
            AcquireLease = false,
            Enqueuer = new RecordingEnqueuer(),
            PollInterval = TimeSpan.FromMilliseconds(5),
            WorktreeAdoption = adoption,
        };

    private async Task<CtDaemonStatusRecord> WaitForWorktreeStatusAsync(CtDaemonLifecycleState state)
    {
        for (int attempt = 0; attempt < 400; attempt++)
        {
            CtDaemonStatusRecord? record = CtDaemonLease.TryReadStatus(WorktreeRoot);
            if (record is not null && record.State == state)
                return record;
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"the worktree status record never reached state {state}");
    }

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        for (int attempt = 0; attempt < 400; attempt++)
        {
            if (predicate())
                return;
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>A handful of poll intervals, so "nothing happened" had every chance to happen.</summary>
    private static Task WaitPassesAsync(int passes) =>
        Task.Delay(TimeSpan.FromMilliseconds(20 * passes), TestContext.Current.CancellationToken);

    private void BuildLinkedWorktree() => BuildLinkedWorktreeAt(MainRoot, WorktreeRoot, "wt");

    private static void BuildLinkedWorktreeAt(string mainRoot, string worktreeRoot, string name)
    {
        string adminDir = Path.Combine(mainRoot, ".git", "worktrees", name);
        Directory.CreateDirectory(adminDir);
        File.WriteAllText(Path.Combine(adminDir, "commondir"), "../..\n");
        Directory.CreateDirectory(worktreeRoot);
        File.WriteAllText(Path.Combine(worktreeRoot, ".git"), $"gitdir: {adminDir}\n");
    }

    private void EnableMain() => WriteMarker(ContinuousTestPolicy.EnabledMarkerPath(MainRoot));

    private static void WriteMarker(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
    }

    private sealed class DisposeFlag : IDisposable
    {
        private int _disposed;

        public bool Disposed => Volatile.Read(ref _disposed) != 0;

        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }
}
