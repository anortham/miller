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
            await WaitForAsync(() => disposed.Disposed, "the detached context to be disposed");

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

            IReadOnlyList<ContinuousTestStatus>? routedStatuses = null;
            CtDaemonCommandAck? routedAck = null;
            await WaitForAsync(
                () =>
                {
                    IReadOnlyList<ContinuousTestStatus> statuses =
                        wtStore.ListContinuousTestStatuses(wtId);
                    CtDaemonCommandAck? ack = CtCommandChannel.TryReadAck(MainRoot, request.CommandId);
                    if (statuses.Count == 0 ||
                        ack is not { State: CtDaemonCommandState.Acknowledged, Reason: "run" })
                        return false;

                    routedStatuses = statuses;
                    routedAck = ack;
                    return true;
                },
                "the routed run to land a status in the worktree store");

            Assert.NotNull(routedStatuses);
            Assert.NotEmpty(routedStatuses);
            Assert.True(File.Exists(CtSchema.DbPathFor(WorktreeRoot)));
            Assert.Equal(CtDaemonCommandState.Acknowledged, routedAck?.State);
            Assert.Equal("run", routedAck?.Reason);

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
    /// Review finding F8: adoption's authorization gate is registration (<c>workspace open</c>).
    /// A routed run naming a directory that LOOKS like a family worktree but was never registered
    /// must be refused with the existing <c>not-adopted</c> rejection, not attached and executed.
    /// </summary>
    [Fact]
    public async Task A_routed_run_naming_an_unregistered_worktree_is_rejected_and_attaches_nothing()
    {
        BuildLinkedWorktree();
        EnableMain();
        int created = 0;
        var adoption = new ContinuousTestWorktreeAdoptionOptions
        {
            // The registry has never seen the worktree.
            DiscoverRegisteredRoots = () => [],
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
            CtDaemonCommandRequest request = CtDaemonRouting.WriteRoutedRequest(
                MainRoot,
                CtDaemonCommandKind.Run,
                reason: "run",
                freshness: new CtFreshnessKey("gen-1", 1),
                targetWorkspaceRoot: WorktreeRoot);
            CtDaemonCommandAck? ack = await Task.Run(() => CtCommandChannel.WaitForAck(
                MainRoot,
                request.CommandId,
                TimeSpan.FromSeconds(5)));

            Assert.Equal(CtDaemonCommandState.Rejected, ack?.State);
            Assert.Equal("not-adopted", ack?.Reason);
            Assert.Equal(0, Volatile.Read(ref created));
            Assert.False(run.IsCompleted, "a refused routed run ended the daemon loop");
        }
        finally
        {
            await cts.CancelAsync();
            await run;
        }
    }

    /// <summary>
    /// Review finding F5, first half: <c>workspace remove</c> takes a worktree out of the
    /// registry, so the next scan must detach it - a directory that still exists must not keep an
    /// adoption the user revoked.
    /// </summary>
    [Fact]
    public async Task A_worktree_removed_from_the_registry_detaches_on_the_next_scan()
    {
        BuildLinkedWorktree();
        EnableMain();
        var disposed = new DisposeFlag();
        bool registered = true;
        var adoption = new ContinuousTestWorktreeAdoptionOptions
        {
            DiscoverRegisteredRoots = () => Volatile.Read(ref registered) ? [WorktreeRoot] : [],
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

            Volatile.Write(ref registered, false);
            await WaitForAsync(() => disposed.Disposed, "the detached context to be disposed");

            Assert.True(disposed.Disposed, "the unregistered worktree stayed adopted");
            Assert.False(run.IsCompleted, "an unregistered worktree ended the daemon loop");
            // DetachWorktree disposes the context BEFORE it writes the Stopped record, so the
            // dispose flag alone does not prove the record landed - poll for the state too.
            CtDaemonStatusRecord record = await WaitForWorktreeStatusAsync(CtDaemonLifecycleState.Stopped);
            Assert.Equal("detached", record.Reason);
        }
        finally
        {
            await cts.CancelAsync();
            await run;
        }
    }

    /// <summary>
    /// Review finding F5, second half: a discovery FAILURE is "cannot read the registry", never
    /// "nothing registered". A transient registry error must keep the current adopted set intact
    /// instead of detaching every worktree.
    /// </summary>
    [Fact]
    public async Task A_discovery_failure_detaches_no_adopted_worktree()
    {
        BuildLinkedWorktree();
        EnableMain();
        var disposed = new DisposeFlag();
        bool fail = false;
        var adoption = new ContinuousTestWorktreeAdoptionOptions
        {
            DiscoverRegisteredRoots = () =>
            {
                if (Volatile.Read(ref fail))
                    throw new InvalidOperationException("registry read failed");
                return [WorktreeRoot];
            },
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

            Volatile.Write(ref fail, true);
            await WaitPassesAsync(5);

            Assert.False(disposed.Disposed, "a discovery failure detached an adopted worktree");
            Assert.False(run.IsCompleted, "a discovery failure ended the daemon loop");
        }
        finally
        {
            await cts.CancelAsync();
            await run;
        }
    }

    /// <summary>
    /// Review finding F9: a routed request whose <c>workspace_root</c> cannot be normalized used
    /// to escape the loop and kill the daemon - and the unacknowledged file crashed every restart
    /// too. It must be rejected as <c>invalid-request</c>, the daemon must keep serving the next
    /// command, and a fresh daemon must start over the leftover file.
    /// </summary>
    [Fact]
    public async Task A_malformed_routed_request_is_rejected_and_the_daemon_survives_and_restarts()
    {
        BuildLinkedWorktree();
        EnableMain();
        var adoption = new ContinuousTestWorktreeAdoptionOptions
        {
            DiscoverRegisteredRoots = () => [WorktreeRoot],
            CreateContext = _ => null,
            ScanInterval = TimeSpan.Zero,
        };

        using var cts = new CancellationTokenSource();
        Task<ContinuousTestDaemonSnapshot> run = ContinuousTestDaemonHost.RunAsync(
            MainRoot,
            HostOptions(adoption),
            cts.Token);
        try
        {
            // An embedded NUL makes Path.GetFullPath throw ArgumentException on every platform.
            // Written straight to the file: the daemon must survive ANY request file, and the
            // routing helper's own normalization would refuse to write this one.
            var bad = new CtDaemonCommandRequest(
                "badcmd1",
                CtDaemonCommandKind.Run,
                DateTimeOffset.UtcNow,
                "run",
                Freshness: null,
                WorkspaceRoot: "bad\0root");
            CtDaemonJson.WriteAtomic(
                CtDaemonProtocol.CommandRequestPath(MainRoot, bad.CommandId),
                bad,
                CtDaemonJsonContext.Default.CtDaemonCommandRequest);
            CtDaemonCommandAck? ack = await Task.Run(() => CtCommandChannel.WaitForAck(
                MainRoot,
                bad.CommandId,
                TimeSpan.FromSeconds(5)));

            Assert.Equal(CtDaemonCommandState.Rejected, ack?.State);
            Assert.Equal("invalid-request", ack?.Reason);
            Assert.False(run.IsCompleted, "one malformed request killed the daemon");

            // The daemon still serves the NEXT command: a live stop lands normally.
            CtDaemonCommandRequest stop = CtCommandChannel.WriteRequest(
                MainRoot,
                CtDaemonCommandKind.Stop,
                reason: "stop",
                freshness: null);
            ContinuousTestDaemonSnapshot stopped = await run.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
            Assert.Equal(CtDaemonLifecycleState.Stopped, stopped.State);
            Assert.Equal("stopping", CtCommandChannel.TryReadAck(MainRoot, stop.CommandId)?.Reason);
        }
        catch
        {
            await cts.CancelAsync();

            // Await without rethrowing: pre-fix the daemon task faults, and its exception must
            // not mask the assertion that brought us here.
            await Task.WhenAny(run);
            _ = run.Exception;
            throw;
        }

        // A restart re-reads the SAME command directory: the malformed file is still on disk and
        // must not crash the fresh daemon either.
        using var restartCts = new CancellationTokenSource();
        Task<ContinuousTestDaemonSnapshot> rerun = ContinuousTestDaemonHost.RunAsync(
            MainRoot,
            HostOptions(adoption),
            restartCts.Token);
        try
        {
            await WaitPassesAsync(5);
            Assert.False(rerun.IsCompleted, "the restarted daemon crashed on the leftover malformed request");
        }
        finally
        {
            await restartCts.CancelAsync();
            await rerun;
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
            await WaitForAsync(() => source.RefreshCount >= 3, "the source to refresh three times");

            CtDaemonCommandRequest request = CtCommandChannel.WriteRequest(
                MainRoot,
                CtDaemonCommandKind.Run,
                reason: "run",
                freshness: null);
            await WaitForAsync(
                () => CtCommandChannel.TryReadAck(MainRoot, request.CommandId) is not null,
                "the routed request to be acknowledged");
            await WaitForAsync(() => EnqueueLines(enqueueLog).Count > 0, "an enqueue to be logged");

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
            await WaitForAsync(() => source.RefreshCount >= 3, "the source to refresh three times");

            CtDaemonCommandRequest request = CtDaemonRouting.WriteRoutedRequest(
                MainRoot,
                CtDaemonCommandKind.Run,
                reason: "run",
                freshness: null,
                targetWorkspaceRoot: WorktreeRoot);
            await WaitForAsync(
                () => CtCommandChannel.TryReadAck(MainRoot, request.CommandId) is not null,
                "the routed request to be acknowledged");
            await WaitForAsync(() => EnqueueLines(enqueueLog).Count > 0, "an enqueue to be logged");

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
            await WaitForAsync(
                () => CtCommandChannel.TryReadAck(MainRoot, request.CommandId) is not null,
                "the routed request to be acknowledged");

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

    /// <summary>
    /// A lost status write used to be remembered as published, so the record on disk never caught up.
    /// The daemon marked the record published BEFORE it tried to write, the attach loop skips a root
    /// already adopted, and nothing else writes that file — so one failed write was permanent.
    ///
    /// <para>What that costs: the record names the daemon that serves the worktree, and a one-shot
    /// <c>tests status</c> probes that identity for liveness. A worktree left holding a DEAD
    /// predecessor daemon's record therefore reports "daemon gone" while a live family daemon is
    /// serving it.</para>
    /// </summary>
    [Fact]
    public async Task A_failed_adopted_status_write_is_retried_and_the_landed_record_names_the_live_daemon()
    {
        BuildLinkedWorktree();
        EnableMain();
        var adoption = new ContinuousTestWorktreeAdoptionOptions
        {
            DiscoverRegisteredRoots = () => [WorktreeRoot],
            CreateContext = root => new ContinuousTestWorkspaceContext
            {
                WorkspaceRoot = root,
                WorkspaceId = "ws:wt",
            },
            ScanInterval = TimeSpan.Zero,
        };

        int calls = 0;
        bool Writer(string root, CtDaemonStatusRecord record, CtDaemonWriteMode mode)
        {
            // The first three attempts are lost, exactly as a sharing violation loses them.
            if (Interlocked.Increment(ref calls) <= 3)
                return false;
            CtDaemonLease.WriteStatus(root, record, mode);
            return true;
        }

        using var cts = new CancellationTokenSource();
        Task<ContinuousTestDaemonSnapshot> run = ContinuousTestDaemonHost.RunAsync(
            MainRoot,
            HostOptions(adoption, acquireLease: true, adoptedStatusWriter: Writer),
            cts.Token);
        try
        {
            CtDaemonStatusRecord record = await WaitForWorktreeStatusAsync(
                state: CtDaemonLifecycleState.Running);

            // The identity, not just the state: an honest stale record from a dead predecessor also
            // says Running, and that one must still read as "daemon gone".
            Assert.Equal(CtDaemonLease.CurrentIdentity(), record.Identity);
            Assert.Contains(Path.GetFullPath(MainRoot), record.Reason, StringComparison.Ordinal);
            Assert.Equal(4, Volatile.Read(ref calls));

            // Once the record has landed the dedupe guard suppresses every rewrite, so the daemon
            // does not republish an identical record on every scan pass.
            await WaitPassesAsync(5);
            Assert.Equal(4, Volatile.Read(ref calls));
        }
        finally
        {
            await cts.CancelAsync();
            await run;
        }
    }

    /// <summary>
    /// The retry is bounded. An unwritable root must cost a minute of attempts, not a spin for the
    /// life of the daemon — each attempt blocks the loop thread inside the write's own retry budget.
    /// </summary>
    [Fact]
    public async Task A_permanently_failing_adopted_status_write_stops_at_the_attempt_cap()
    {
        BuildLinkedWorktree();
        EnableMain();
        var adoption = new ContinuousTestWorktreeAdoptionOptions
        {
            DiscoverRegisteredRoots = () => [WorktreeRoot],
            CreateContext = root => new ContinuousTestWorkspaceContext
            {
                WorkspaceRoot = root,
                WorkspaceId = "ws:wt",
            },
            ScanInterval = TimeSpan.Zero,
        };

        int calls = 0;
        bool Writer(string root, CtDaemonStatusRecord record, CtDaemonWriteMode mode)
        {
            Interlocked.Increment(ref calls);
            return false;
        }

        using var cts = new CancellationTokenSource();
        Task<ContinuousTestDaemonSnapshot> run = ContinuousTestDaemonHost.RunAsync(
            MainRoot,
            HostOptions(adoption, adoptedStatusWriter: Writer),
            cts.Token);
        ContinuousTestDaemonSnapshot snapshot;
        try
        {
            // One attach attempt plus the twelve retries the cap allows.
            await WaitForAsync(
                () => Volatile.Read(ref calls) >= 13,
                "the adopted status write to reach its attempt cap");
            await WaitPassesAsync(10);
            Assert.Equal(13, Volatile.Read(ref calls));
        }
        finally
        {
            await cts.CancelAsync();
            snapshot = await run;
        }

        Assert.Equal(CtDaemonLifecycleState.Stopped, snapshot.State);
    }

    /// <summary>
    /// A detach record says nothing serves this root any more. Writing it used to CREATE
    /// <c>&lt;worktree&gt;/.miller/ct/</c>, so the daemon re-minted the tree it was detaching from —
    /// which defeated <c>git worktree remove</c> twice on 2026-08-21, because the recreated directory
    /// left the worktree untracked-dirty and git refused.
    /// </summary>
    [Fact]
    public async Task A_scan_detach_never_recreates_a_control_plane_the_worktree_no_longer_has()
    {
        BuildLinkedWorktree();
        EnableMain();
        var disposed = new DisposeFlag();
        var registered = new List<string> { WorktreeRoot };
        var adoption = new ContinuousTestWorktreeAdoptionOptions
        {
            DiscoverRegisteredRoots = () =>
            {
                lock (registered)
                    return registered.ToArray();
            },
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
            // The attach record proves the control plane existed before the test removed it.
            await WaitForWorktreeStatusAsync(state: CtDaemonLifecycleState.Running);

            // Stand in for `git worktree remove`, which deletes the tree the daemon still holds a
            // context for. The worktree directory itself stays, so the root check cannot mask the bug.
            Directory.Delete(Path.Combine(WorktreeRoot, ".miller"), recursive: true);
            lock (registered)
                registered.Clear();

            await WaitForAsync(() => disposed.Disposed, "the worktree context to be detached");
            await WaitPassesAsync(5);

            Assert.True(Directory.Exists(WorktreeRoot), "the test deleted the control plane, not the worktree");
            Assert.False(
                Directory.Exists(Path.Combine(WorktreeRoot, ".miller")),
                "the detach recreated the control plane it was tearing down");
        }
        finally
        {
            await cts.CancelAsync();
            await run;
        }
    }

    /// <summary>
    /// The second caller of the same detach write. This path needs no race and no
    /// <c>git worktree remove</c> — a routed <c>tests stop</c> reaches it directly.
    /// </summary>
    [Fact]
    public async Task A_routed_stop_never_recreates_a_control_plane_the_worktree_no_longer_has()
    {
        BuildLinkedWorktree();
        EnableMain();
        var adoption = new ContinuousTestWorktreeAdoptionOptions
        {
            DiscoverRegisteredRoots = () => [WorktreeRoot],
            CreateContext = root => new ContinuousTestWorkspaceContext
            {
                WorkspaceRoot = root,
                WorkspaceId = "ws:wt",
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
            Directory.Delete(Path.Combine(WorktreeRoot, ".miller"), recursive: true);

            CtDaemonCommandAck? ack = await Task.Run(() => CtDaemonRouting.RequestDetach(
                MainRoot,
                WorktreeRoot,
                ackTimeout: TimeSpan.FromSeconds(5)));

            Assert.Equal("detached", ack?.Reason);
            Assert.False(
                Directory.Exists(Path.Combine(WorktreeRoot, ".miller")),
                "the routed stop recreated the control plane it was tearing down");
        }
        finally
        {
            await cts.CancelAsync();
            await run;
        }
    }

    /// <summary>
    /// The retry writes an ATTACH record, and an attach record may CREATE the control plane. So the
    /// retry must run AFTER the detach pass, never before it: a retry that fires on the pass that was
    /// about to drop the root re-mints the very tree the removal deleted, which is the resurrect the
    /// replace-only mode exists to stop — reintroduced through the repair path.
    ///
    /// <para>The failing write is the synchronization point: the worktree is removed from disk and
    /// from the registry inside the failed attempt, so the next pass runs the exact race.</para>
    /// </summary>
    [Fact]
    public async Task An_owed_attach_record_is_not_retried_onto_a_worktree_that_has_been_removed()
    {
        BuildLinkedWorktree();
        EnableMain();
        var disposed = new DisposeFlag();
        var registered = new List<string> { WorktreeRoot };
        var adoption = new ContinuousTestWorktreeAdoptionOptions
        {
            DiscoverRegisteredRoots = () =>
            {
                lock (registered)
                    return registered.ToArray();
            },
            CreateContext = root => new ContinuousTestWorkspaceContext
            {
                WorkspaceRoot = root,
                WorkspaceId = "ws:wt",
                Owned = disposed,
            },
            ScanInterval = TimeSpan.Zero,
        };

        int calls = 0;
        bool Writer(string root, CtDaemonStatusRecord record, CtDaemonWriteMode mode)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                // The worktree leaves the registry DURING the attempt that fails, so a record is owed
                // for a root that no longer qualifies. Every later attempt writes for real, which is
                // what makes a mis-ordered retry visible: it would create the control plane here.
                lock (registered)
                    registered.Clear();
                return false;
            }

            CtDaemonLease.WriteStatus(root, record, mode);
            return true;
        }

        using var cts = new CancellationTokenSource();
        Task<ContinuousTestDaemonSnapshot> run = ContinuousTestDaemonHost.RunAsync(
            MainRoot,
            HostOptions(adoption, adoptedStatusWriter: Writer),
            cts.Token);
        try
        {
            await WaitForAsync(() => disposed.Disposed, "the removed worktree to be detached");
            await WaitPassesAsync(10);

            Assert.True(Directory.Exists(WorktreeRoot), "the test deleted the control plane, not the worktree");
            Assert.False(
                Directory.Exists(Path.Combine(WorktreeRoot, ".miller")),
                "the owed attach record was retried onto a removed worktree and recreated its control plane");
        }
        finally
        {
            await cts.CancelAsync();
            await run;
        }
    }

    /// <summary>
    /// A detach record is un-retryable by construction: the context leaves <c>_adopted</c> before the
    /// write, so no later pass can reach it. A lost detach write therefore used to leave the worktree
    /// holding an <c>adopted by …</c> record naming a daemon that is still ALIVE — which the liveness
    /// probe cannot contradict, so status reported a running daemon for a worktree nothing watches.
    ///
    /// <para>Removing the stale record is the honest repair: an absent record reads as stopped, which
    /// is what a detached worktree is.</para>
    /// </summary>
    [Fact]
    public async Task A_detach_record_that_cannot_be_written_removes_the_stale_one_instead()
    {
        BuildLinkedWorktree();
        EnableMain();
        var disposed = new DisposeFlag();
        var registered = new List<string> { WorktreeRoot };
        var adoption = new ContinuousTestWorktreeAdoptionOptions
        {
            DiscoverRegisteredRoots = () =>
            {
                lock (registered)
                    return registered.ToArray();
            },
            CreateContext = root => new ContinuousTestWorkspaceContext
            {
                WorkspaceRoot = root,
                WorkspaceId = "ws:wt",
                Owned = disposed,
            },
            ScanInterval = TimeSpan.Zero,
        };

        // The attach record lands for real; only the detach write is lost.
        bool Writer(string root, CtDaemonStatusRecord record, CtDaemonWriteMode mode)
        {
            if (record.State != CtDaemonLifecycleState.Running)
                return false;
            CtDaemonLease.WriteStatus(root, record, mode);
            return true;
        }

        using var cts = new CancellationTokenSource();
        Task<ContinuousTestDaemonSnapshot> run = ContinuousTestDaemonHost.RunAsync(
            MainRoot,
            HostOptions(adoption, adoptedStatusWriter: Writer),
            cts.Token);
        try
        {
            await WaitForWorktreeStatusAsync(state: CtDaemonLifecycleState.Running);
            lock (registered)
                registered.Clear();

            await WaitForAsync(() => disposed.Disposed, "the worktree context to be detached");
            await WaitForAsync(
                () => CtDaemonLease.TryReadStatus(WorktreeRoot) is null,
                "the stale adopted record to be removed");

            // An absent record is the honest "stopped", and the read must not create anything.
            Assert.Equal(
                CtDaemonLifecycleState.Stopped,
                ContinuousTestDaemonHost.ReadLiveStatus(WorktreeRoot).State);
        }
        finally
        {
            await cts.CancelAsync();
            await run;
        }
    }

    private ContinuousTestDaemonHostOptions HostOptions(
        ContinuousTestWorktreeAdoptionOptions adoption,
        bool acquireLease = false,
        Func<string, CtDaemonStatusRecord, CtDaemonWriteMode, bool>? adoptedStatusWriter = null) =>
        new()
        {
            Enabled = true,
            AcquireLease = acquireLease,
            Enqueuer = new RecordingEnqueuer(),
            PollInterval = TimeSpan.FromMilliseconds(5),
            WorktreeAdoption = adoption,
            AdoptedStatusWriter = adoptedStatusWriter,
        };

    /// <summary>
    /// The cap on a wait for something that MUST happen. It is wall-clock, not a count of attempts:
    /// 400 attempts of <c>Task.Delay(10)</c> is four seconds only on an idle thread pool, and under the
    /// full suite's parallelism each delay stretches, so the real bound moved with the load.
    ///
    /// <para>That is how <c>A_routed_run_reaches_only_the_worktrees_own_store</c> went red in the suite
    /// while passing alone in two seconds (observed 2026-08-21, with a CT daemon running alongside).
    /// These waits end the moment their condition holds, so a generous cap costs nothing when healthy.</para>
    /// </summary>
    private static readonly TimeSpan PositiveWait = TimeSpan.FromSeconds(30);

    private async Task<CtDaemonStatusRecord> WaitForWorktreeStatusAsync(CtDaemonLifecycleState state)
    {
        DateTime deadline = DateTime.UtcNow + PositiveWait;
        do
        {
            CtDaemonStatusRecord? record = CtDaemonLease.TryReadStatus(WorktreeRoot);
            if (record is not null && record.State == state)
                return record;
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        while (DateTime.UtcNow < deadline);

        throw new TimeoutException($"the worktree status record never reached state {state}");
    }

    /// <summary>
    /// Throws when the condition never holds. It used to return quietly, which turned every timeout into
    /// whichever assertion ran next — a routed run that never landed reported "Assert.NotEmpty() Failure:
    /// Collection was empty", naming the symptom instead of the wait that gave up.
    /// </summary>
    private static async Task WaitForAsync(Func<bool> predicate, string what)
    {
        DateTime deadline = DateTime.UtcNow + PositiveWait;
        do
        {
            if (predicate())
                return;
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        while (DateTime.UtcNow < deadline);

        throw new TimeoutException($"timed out after {PositiveWait.TotalSeconds:0}s waiting for {what}");
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
