using Microsoft.Data.Sqlite;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Miller.Core.Freshness;
using Miller.Indexing;
using Miller.Server;
using Miller.Server.Hosting;
using Miller.Server.Workspaces;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the version-aware leadership orchestration in <see cref="IndexerService"/> (design D2–D4): the claim
/// gate (an ineligible instance never even attempts the writer lock), the auto-upgrade forced rescan on claim,
/// the requester side of the yield protocol (one outstanding request per leader per TTL), the leader side
/// (abdicate only to a strictly newer extractor), and the post-yield cooldown integration. Everything runs
/// through injected funcs — no real lock files, no subprocess, no wall-clock timers; ticks are driven via the
/// <c>*ForTest</c> seams and clocks are injected.
/// </summary>
public sealed class IndexerServiceLeadershipTests : IDisposable
{
    // The signal fires in ~80ms on a quiet box; the generous ceiling only extends patience under
    // scheduler starvation (same de-flake as IndexerServiceScanTests.ScanSignalTimeoutMs).
    private const int ScanSignalTimeoutMs = 30_000;

    // Static because the service factory below is static; xUnit runs this class's tests serially,
    // so draining the bag per-test Dispose never races another test in this class.
    private static readonly System.Collections.Concurrent.ConcurrentBag<string> TempHomes = [];

    private static string CreateTempHome()
    {
        string tempHome = Path.Combine(Path.GetTempPath(), "miller-leadership-home-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempHome);
        TempHomes.Add(tempHome);
        return tempHome;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools(); // pooled handles under a deleted dir crash the WAL checkpoint at exit
        while (TempHomes.TryTake(out string? dir))
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class TestLease : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    /// <summary>A fake <see cref="IExtractOps"/> recording scan force flags; signals when N scans ran.</summary>
    private sealed class RecordingOps : IExtractOps
    {
        private readonly object _gate = new();
        private readonly List<bool> _scanForce = new();
        private readonly List<ScanIntent> _scanIntents = new();

        public ManualResetEventSlim ScansReached { get; } = new();
        public int SignalAtScanCount { get; init; } = 1;
        public long? Revision { get; set; } = 7;

        public IReadOnlyList<bool> ScanForce
        {
            get
            {
                lock (_gate)
                    return _scanForce.ToArray();
            }
        }

        public IReadOnlyList<ScanIntent> ScanIntents
        {
            get
            {
                lock (_gate)
                    return _scanIntents.ToArray();
            }
        }

        public ExtractReport Update(string path) => Stub(Revision);
        public ExtractReport Delete(string path) => Stub(Revision);

        public ExtractReport Scan(ScanIntent intent = ScanIntent.IncrementalReconcile, int? jobs = null)
        {
            lock (_gate)
            {
                _scanForce.Add(ScanIntentPolicy.RequiresForce(intent));
                _scanIntents.Add(intent);
                if (_scanForce.Count >= SignalAtScanCount)
                    ScansReached.Set();
            }
            return Stub(Revision);
        }

        private static ExtractReport Stub(long? revision) => new(
            ReportSchemaVersion: 1, Status: "ok", Operation: "scan", Mode: "incremental", Input: null,
            Artifact: new ExtractArtifact(
                DbPath: "x", RootPath: "/abs/r", ArtifactId: "a",
                SchemaVersion: MillerExtractContract.ExpectedSchemaVersion,
                ExtractContractVersion: MillerExtractContract.ExpectedExtractContractVersion,
                SqliteSchemaVersion: MillerExtractContract.ExpectedSqliteSchemaVersion,
                JsonlSchemaVersion: 1, HashAlgorithm: MillerExtractContract.ExpectedHashAlgorithm,
                ParserInventoryFingerprint: "p", CapabilitySnapshotFingerprint: "c"),
            Tool: new ExtractTool("julie-extract", "2.0.0"),
            RevisionBlock: new ExtractRevision(revision, revision),
            Counts: null,
            Errors: System.Array.Empty<ReportDiagnostic>(), Warnings: System.Array.Empty<ReportDiagnostic>());
    }

    /// <summary>Captures log level + rendered message so a test can pin the once-at-Information contract.</summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly object _gate = new();
        private readonly List<(LogLevel Level, string Message)> _entries = new();

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get
            {
                lock (_gate)
                    return _entries.ToArray();
            }
        }

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
                _entries.Add((logLevel, formatter(state, exception)));
        }
    }

    // A never-started service over an UNSEEDED bootstrap: every leadership input is an injected func, so the
    // claim gate / yield / cooldown seams run with zero I/O and never touch the bootstrap getters.
    private static IndexerService NewService(
        Func<string, IDisposable?>? tryAcquireLeadership = null,
        Func<string, YieldDrainResult>? drainYieldRequests = null,
        Func<string, LeaderHandoffDrainResult>? drainLeaderHandoffRequests = null,
        Func<string?>? ownExtractorVersion = null,
        bool? allowExtractorDowngrade = null,
        Func<string?, string?>? readArtifactExtractorVersion = null,
        Action<string, string, int, string>? requestYield = null,
        Func<string, LeaderIdentity?>? readLeaderIdentity = null,
        Func<LeaderIdentity, bool>? leaderAliveProbe = null,
        Func<DateTimeOffset>? clock = null,
        Func<int, bool>? processAliveProbe = null,
        Func<int, DateTimeOffset?, bool>? processAliveProbeWithObserved = null,
        ILogger<IndexerService>? logger = null)
    {
        string tempHome = CreateTempHome();
        var bootstrap = new IndexBootstrapService(NullLogger<IndexBootstrapService>.Instance);
        bootstrap.TestHomeDirectoryOverride = tempHome;
        return new(
            bootstrap,
            logger ?? NullLogger<IndexerService>.Instance,
            NullLoggerFactory.Instance,
            tryAcquireLeadership ?? (_ => null),
            createOps: static (_, _, _) => throw new InvalidOperationException("not used by this test seam"),
            leaderRetryInterval: TimeSpan.FromHours(1),
            SymbolSearchSidecar.Disabled,
            attachFileWatchers: false,
            drainYieldRequests: drainYieldRequests,
            drainLeaderHandoffRequests: drainLeaderHandoffRequests,
            ownExtractorVersion: ownExtractorVersion ?? (() => "3.0.0"),
            allowExtractorDowngrade: allowExtractorDowngrade ?? false,
            readArtifactExtractorVersion: readArtifactExtractorVersion ?? (_ => null),
            requestYield: requestYield,
            readLeaderIdentity: readLeaderIdentity,
            leaderAliveProbe: leaderAliveProbe,
            clock: clock,
            processAliveProbe: processAliveProbeWithObserved
                ?? (processAliveProbe is null ? null : (pid, _) => processAliveProbe(pid)));
    }

    private static WorkspaceContext CreateWorkspace(string dir)
    {
        string root = Path.Combine(dir, "repo");
        string home = Path.Combine(dir, "home");
        Directory.CreateDirectory(root);
        string canonicalRoot = Path.GetFullPath(root);
        return WorkspaceContext.Create(root, AppContext.BaseDirectory, home) with
        {
            WorkspaceId = WorkspaceId.FromCanonicalRoot(canonicalRoot),
            CanonicalRoot = canonicalRoot,
            CanonicalExtractDbPath = Path.Combine(canonicalRoot, ".miller", "symbols.db"),
        };
    }

    // A startable leader-capable service over a SEEDED bootstrap with every drain stubbed pure, so a started
    // test only exercises the claim → identity write → startup scan → upgrade rescan path.
    private static IndexerService NewStartedService(
        WorkspaceContext workspace,
        Func<string, IDisposable?> tryAcquireLeadership,
        IExtractOps ops,
        string? ownVersion,
        string? artifactVersion,
        Func<WorkspaceContext, string?>? readIndexLevel = null)
    {
        string tempHome = Path.GetDirectoryName(Path.GetDirectoryName(workspace.RegistryDbPath))!;
        var bootstrap = new IndexBootstrapService(NullLogger<IndexBootstrapService>.Instance);
        bootstrap.TestHomeDirectoryOverride = tempHome;
        bootstrap.SeedForTest(
            workspace,
            new IndexHolder(MillerRepositoryIndex.Build(System.Array.Empty<IndexedSymbol>()), builtRevision: 0));
        return new IndexerService(
            bootstrap,
            NullLogger<IndexerService>.Instance,
            NullLoggerFactory.Instance,
            tryAcquireLeadership,
            createOps: (_, _, _) => ops,
            leaderRetryInterval: TimeSpan.FromHours(1),
            SymbolSearchSidecar.Disabled,
            attachFileWatchers: false,
            drainFullScanRequests: _ => FullScanDrainResult.Empty,
            drainFileConvergeRequests: _ => FileConvergeDrainResult.Empty,
            drainYieldRequests: _ => YieldDrainResult.Empty,
            drainLeaderHandoffRequests: _ => LeaderHandoffDrainResult.Empty,
            ownExtractorVersion: () => ownVersion,
            allowExtractorDowngrade: false,
            readArtifactExtractorVersion: _ => artifactVersion,
            readIndexLevel: readIndexLevel);
    }

    // ---- D2: the claim gate -------------------------------------------------------------------------------

    [Fact]
    public void AttemptClaim_OwnExtractorOlderThanArtifact_NeverInvokesAcquire()
    {
        bool acquireAttempted = false;
        var service = NewService(
            tryAcquireLeadership: _ =>
            {
                acquireAttempted = true;
                return new TestLease();
            },
            ownExtractorVersion: () => "2.0.0",
            readArtifactExtractorVersion: _ => "3.0.0");

        Assert.False(service.AttemptClaimForTest("/repo/.miller"));

        Assert.False(acquireAttempted); // the gate sits BEFORE the lock: an outdated instance never even tries
        Assert.NotNull(service.EligibilityVerdict);
        Assert.False(service.EligibilityVerdict!.Eligible);
        Assert.False(service.IsLeader);
    }

    [Fact]
    public void AttemptClaim_OwnVersionUnknown_NeverInvokesAcquire()
    {
        bool acquireAttempted = false;
        var service = NewService(
            tryAcquireLeadership: _ =>
            {
                acquireAttempted = true;
                return new TestLease();
            },
            ownExtractorVersion: () => null,
            readArtifactExtractorVersion: _ => "2.0.0");

        Assert.False(service.AttemptClaimForTest("/repo/.miller"));
        Assert.False(acquireAttempted); // cannot index anyway (binary missing/unprobeable) — reads only
    }

    [Fact]
    public void AttemptClaim_EligibleNewerThanArtifact_InvokesAcquire()
    {
        var lease = new TestLease();
        var service = NewService(
            tryAcquireLeadership: _ => lease,
            ownExtractorVersion: () => "3.0.0",
            readArtifactExtractorVersion: _ => "2.0.0");

        Assert.True(service.AttemptClaimForTest("/repo/.miller"));
        Assert.True(service.IsLeader);
        Assert.True(service.EligibilityVerdict!.ArtifactOlderThanOwn); // feeds the D3 upgrade rescan
    }

    [Fact]
    public void AttemptClaim_DowngradeOverride_InvokesAcquireDespiteOlderOwnVersion()
    {
        var service = NewService(
            tryAcquireLeadership: _ => new TestLease(),
            ownExtractorVersion: () => "2.0.0",
            allowExtractorDowngrade: true,
            readArtifactExtractorVersion: _ => "3.0.0");

        Assert.True(service.AttemptClaimForTest("/repo/.miller")); // the explicit escape hatch, nothing else
    }

    [Fact]
    public void AttemptClaim_Ineligible_LogsVerdictReasonOnceAtInformation_ThenDebug()
    {
        var logger = new RecordingLogger<IndexerService>();
        var service = NewService(
            ownExtractorVersion: () => "2.0.0",
            readArtifactExtractorVersion: _ => "3.0.0",
            logger: logger);

        service.AttemptClaimForTest("/repo/.miller");
        service.AttemptClaimForTest("/repo/.miller");

        var verdictLogs = logger.Entries.Where(e => e.Message.Contains("2.0.0")).ToArray();
        Assert.Equal(2, verdictLogs.Length);
        Assert.Equal(LogLevel.Information, verdictLogs[0].Level); // the meaningful transition, once
        Assert.Equal(LogLevel.Debug, verdictLogs[1].Level);       // the steady state must not spam
    }

    // ---- D3: auto-upgrade rescan on claim -----------------------------------------------------------------

    [Fact]
    public async Task StartAsync_ArtifactOlderThanOwn_RunsExactlyOneForcedUpgradeScan_AndStampsExtractorVersion()
    {
        string dir = Path.Combine(Path.GetTempPath(), "miller-leadership-upgrade-" + Guid.NewGuid().ToString("N"));
        var lease = new TestLease();
        var ops = new RecordingOps { Revision = 11 };
        try
        {
            WorkspaceContext workspace = CreateWorkspace(dir);
            string millerDir = Path.GetDirectoryName(workspace.ExtractDbPath)!;
            var service = NewStartedService(workspace, _ => lease, ops, ownVersion: "3.0.0", artifactVersion: "2.0.0");

            await service.StartAsync(CancellationToken.None);
            Assert.True(ops.ScansReached.Wait(ScanSignalTimeoutMs, CancellationToken.None));

            // D5: leader.json carries the extractor version so readers can compare fitness against a LIVE leader.
            LeaderIdentity? identity = LeaderIdentityFile.TryRead(millerDir);
            Assert.NotNull(identity);
            Assert.Equal("3.0.0", identity!.ExtractorVersion);

            await service.StopAsync(CancellationToken.None);

            Assert.Equal(new[] { ScanIntent.ExtractorUpgrade }, ops.ScanIntents);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task StartAsync_ArtifactMatchesOwn_RunsOnlyTheStartupDeltaScan()
    {
        string dir = Path.Combine(Path.GetTempPath(), "miller-leadership-noupgrade-" + Guid.NewGuid().ToString("N"));
        var lease = new TestLease();
        var ops = new RecordingOps { Revision = 11 };
        try
        {
            WorkspaceContext workspace = CreateWorkspace(dir);
            var service = NewStartedService(workspace, _ => lease, ops, ownVersion: "3.0.0", artifactVersion: "3.0.0");

            await service.StartAsync(CancellationToken.None);
            Assert.True(ops.ScansReached.Wait(ScanSignalTimeoutMs, CancellationToken.None));
            await service.StopAsync(CancellationToken.None);

            Assert.Equal(new[] { ScanIntent.IncrementalReconcile }, ops.ScanIntents);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task StartAsync_StoreL1UnderProgressivePolicy_SchedulesFullLevelUpgrade()
    {
        string dir = Path.Combine(Path.GetTempPath(), "miller-store-level-upgrade-" + Guid.NewGuid().ToString("N"));
        var lease = new TestLease();
        var ops = new RecordingOps { SignalAtScanCount = 2, Revision = 11 };
        try
        {
            WorkspaceContext workspace = CreateWorkspace(dir);
            var service = NewStartedService(
                workspace,
                _ => lease,
                ops,
                ownVersion: "3.0.0",
                artifactVersion: "3.0.0",
                readIndexLevel: _ => IndexLevels.SymbolsMetadataValue);
            using (var registry = WorkspaceRegistry.Open(workspace.RegistryDbPath))
            {
                registry.UpsertSeen(
                    workspace.WorkspaceId!,
                    WorkspaceId.Display(workspace.CanonicalRoot!, workspace.WorkspaceId!),
                    workspace.CanonicalRoot!,
                    workspace.CanonicalExtractDbPath!);
                registry.SetLevelPolicy(workspace.WorkspaceId!, "progressive");
            }

            await service.StartAsync(CancellationToken.None);
            Assert.True(ops.ScansReached.Wait(ScanSignalTimeoutMs, CancellationToken.None));
            await service.StopAsync(CancellationToken.None);

            Assert.Equal(new[] { false, true }, ops.ScanForce);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task ReaderLoop_WhenPrimaryWorkspaceRebinds_RestartsClaimAttemptsForNewRoot()
    {
        string dir = Path.Combine(Path.GetTempPath(), "miller-leadership-rebind-" + Guid.NewGuid().ToString("N"));
        var attempts = new List<string>();
        var attemptsGate = new object();
        try
        {
            string tempHome = Path.Combine(dir, "home");
            Directory.CreateDirectory(tempHome);
            var bootstrap = new IndexBootstrapService(NullLogger<IndexBootstrapService>.Instance);
            bootstrap.TestHomeDirectoryOverride = tempHome;
            bootstrap.TestBootstrapInterceptor = (canonicalRoot, _) =>
            {
                var workspace = WorkspaceContext.Create(canonicalRoot, AppContext.BaseDirectory, tempHome) with
                {
                    WorkspaceId = WorkspaceId.FromCanonicalRoot(canonicalRoot),
                    CanonicalRoot = canonicalRoot,
                    CanonicalExtractDbPath = Path.Combine(canonicalRoot, ".miller", "symbols.db"),
                };
                bootstrap.SeedForTest(
                    workspace,
                    new IndexHolder(
                        MillerRepositoryIndex.Build(System.Array.Empty<IndexedSymbol>()),
                        builtRevision: bootstrap.BindingGeneration + 1));
                return true;
            };

            string rootA = Path.Combine(dir, "repo-a");
            string rootB = Path.Combine(dir, "repo-b");
            Directory.CreateDirectory(rootA);
            Directory.CreateDirectory(rootB);
            bootstrap.BootstrapForRoot(rootA, WorkspaceBindingResolver.WorkspaceSource.Roots);

            var service = new IndexerService(
                bootstrap,
                NullLogger<IndexerService>.Instance,
                NullLoggerFactory.Instance,
                tryAcquireLeadership: millerDir =>
                {
                    lock (attemptsGate)
                        attempts.Add(millerDir);
                    return null;
                },
                createOps: (_, _, _) => new RecordingOps(),
                leaderRetryInterval: TimeSpan.FromMilliseconds(20),
                SymbolSearchSidecar.Disabled,
                attachFileWatchers: false,
                drainFullScanRequests: _ => FullScanDrainResult.Empty,
                drainFileConvergeRequests: _ => FileConvergeDrainResult.Empty,
                drainYieldRequests: _ => YieldDrainResult.Empty,
                ownExtractorVersion: () => "3.0.0",
                allowExtractorDowngrade: false,
                readArtifactExtractorVersion: _ => "3.0.0");

            await service.StartAsync(CancellationToken.None);
            await WaitUntilAsync(
                () => AttemptsContain(Path.Combine(PathCanonicalizer.CanonicalizeRoot(rootA), ".miller")),
                TestContext.Current.CancellationToken);

            bootstrap.BootstrapForRoot(rootB, WorkspaceBindingResolver.WorkspaceSource.Roots);

            await WaitUntilAsync(
                () => AttemptsContain(Path.Combine(PathCanonicalizer.CanonicalizeRoot(rootB), ".miller")),
                TestContext.Current.CancellationToken);

            await service.StopAsync(CancellationToken.None);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }

        bool AttemptsContain(string expected)
        {
            lock (attemptsGate)
                return attempts.Any(path => string.Equals(path, expected, StringComparison.Ordinal));
        }
    }

    // ---- D4 requester side: yield request dedup -----------------------------------------------------------

    private static LeaderIdentity LiveLeader(int pid, string? extractorVersion) => new(
        pid, "0.3.6", null, new DateTimeOffset(2026, 6, 11, 9, 0, 0, TimeSpan.Zero), extractorVersion);

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        while (!condition())
            await Task.Delay(10, linked.Token).ConfigureAwait(false);
    }

    [Fact]
    public void MaybeRequestYield_NewerThanLiveLeader_EnqueuesOnce_NoRepeatWithinTtl()
    {
        var requests = new List<(string MillerDir, string WorkspaceId, int Pid, string Version)>();
        DateTimeOffset now = new(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);
        var service = NewService(
            ownExtractorVersion: () => "3.0.0",
            requestYield: (millerDir, ws, pid, version) => requests.Add((millerDir, ws, pid, version)),
            readLeaderIdentity: _ => LiveLeader(4242, "2.0.0"),
            leaderAliveProbe: _ => true,
            clock: () => now);

        service.MaybeRequestYieldForTest("/repo/.miller", "ws-1");
        now += TimeSpan.FromSeconds(5); // the 5s reader retry tick fires again well within the TTL
        service.MaybeRequestYieldForTest("/repo/.miller", "ws-1");

        var request = Assert.Single(requests); // at most ONE outstanding yield toward the same leader
        Assert.Equal("/repo/.miller", request.MillerDir);
        Assert.Equal("ws-1", request.WorkspaceId);
        Assert.Equal(Environment.ProcessId, request.Pid);
        Assert.Equal("3.0.0", request.Version);
    }

    [Fact]
    public void MaybeRequestYield_AfterTtlElapsed_ReEnqueues()
    {
        int requests = 0;
        DateTimeOffset now = new(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);
        var service = NewService(
            ownExtractorVersion: () => "3.0.0",
            requestYield: (_, _, _, _) => requests++,
            readLeaderIdentity: _ => LiveLeader(4242, "2.0.0"),
            leaderAliveProbe: _ => true,
            clock: () => now);

        service.MaybeRequestYieldForTest("/repo/.miller", "ws-1");
        now += LeaderScanRequestQueue.RequestTtl; // the original request has rotted; the leader never drained it
        service.MaybeRequestYieldForTest("/repo/.miller", "ws-1");

        Assert.Equal(2, requests);
    }

    [Fact]
    public void MaybeRequestYield_ObservedLeaderChanged_ReEnqueuesImmediately()
    {
        int requests = 0;
        LeaderIdentity leader = LiveLeader(4242, "2.0.0");
        var service = NewService(
            ownExtractorVersion: () => "3.0.0",
            requestYield: (_, _, _, _) => requests++,
            readLeaderIdentity: _ => leader,
            leaderAliveProbe: _ => true,
            clock: () => new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.Zero));

        service.MaybeRequestYieldForTest("/repo/.miller", "ws-1");
        leader = LiveLeader(5151, "2.1.0"); // a DIFFERENT outdated leader took over; the old request died with its target
        service.MaybeRequestYieldForTest("/repo/.miller", "ws-1");

        Assert.Equal(2, requests);
    }

    [Fact]
    public void MaybeRequestYield_EqualVersions_NeverEnqueues()
    {
        int requests = 0;
        var service = NewService(
            ownExtractorVersion: () => "3.0.0",
            requestYield: (_, _, _, _) => requests++,
            readLeaderIdentity: _ => LiveLeader(4242, "3.0.0"),
            leaderAliveProbe: _ => true);

        service.MaybeRequestYieldForTest("/repo/.miller", "ws-1");

        Assert.Equal(0, requests); // same-version swarms must never thrash leadership (D4/D7)
    }

    [Fact]
    public void MaybeRequestYield_DeadLeaderIdentity_DoesNotEnqueue()
    {
        int requests = 0;
        var service = NewService(
            ownExtractorVersion: () => "3.0.0",
            requestYield: (_, _, _, _) => requests++,
            readLeaderIdentity: _ => LiveLeader(4242, "2.0.0"),
            leaderAliveProbe: _ => false); // stale leader.json from a crash — the normal lock retry wins instead

        service.MaybeRequestYieldForTest("/repo/.miller", "ws-1");

        Assert.Equal(0, requests);
    }

    [Fact]
    public void MaybeRequestYield_LeaderWithoutExtractorVersion_DoesNotEnqueue()
    {
        int requests = 0;
        var service = NewService(
            ownExtractorVersion: () => "3.0.0",
            requestYield: (_, _, _, _) => requests++,
            readLeaderIdentity: _ => LiveLeader(4242, extractorVersion: null), // pre-feature leader (D5 back-compat)
            leaderAliveProbe: _ => true);

        service.MaybeRequestYieldForTest("/repo/.miller", "ws-1");

        Assert.Equal(0, requests); // it could not drain the request anyway; the claim gate catches it on restart
    }

    [Fact]
    public void MaybeRequestYield_WhenSelfIneligible_DoesNotEnqueue()
    {
        int requests = 0;
        var service = NewService(
            ownExtractorVersion: () => "3.0.0",
            readArtifactExtractorVersion: _ => "4.0.0", // the artifact is already ahead of us: we must stay a reader
            requestYield: (_, _, _, _) => requests++,
            readLeaderIdentity: _ => LiveLeader(4242, "2.0.0"),
            leaderAliveProbe: _ => true);

        service.MaybeRequestYieldForTest("/repo/.miller", "ws-1");

        Assert.Equal(0, requests); // an ineligible instance asking a leader to step down could freeze the index
    }

    // ---- D4 leader side: abdication + cooldown -------------------------------------------------------------

    [Fact]
    public void ProcessYieldRequests_StrictlyNewerChallenger_Abdicates()
    {
        string dir = Path.Combine(Path.GetTempPath(), "miller-leadership-yield-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            LeaderIdentityFile.Write(dir, new LeaderIdentity(
                Environment.ProcessId, "0.3.6", null, DateTimeOffset.UtcNow, "2.0.0"));
            var lease = new TestLease();
            var service = NewService(
                drainYieldRequests: _ => new YieldDrainResult(true, "3.0.0", 9999, 0, 0),
                ownExtractorVersion: () => "2.0.0",
                processAliveProbe: _ => true);
            service.PublishOpsForTest(new RecordingOps());
            service.AssumeLeadershipForTest(lease);

            Assert.True(service.ProcessYieldRequestsForTest(dir));

            Assert.True(lease.Disposed);   // indexer.lock released so the challenger's retry can win it
            Assert.False(service.IsLeader);
            Assert.Null(LeaderIdentityFile.TryRead(dir)); // leader.json removed before the successor writes its own
            Assert.Equal(ScanOutcome.Kind.NotLeader, service.TryScanAsLeader(ScanIntent.UserFullRebuild).Result); // ops reset
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void ProcessYieldRequests_EqualVersionChallenger_DoesNotAbdicate()
    {
        string dir = Path.Combine(Path.GetTempPath(), "miller-leadership-yield-eq-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            LeaderIdentityFile.Write(dir, new LeaderIdentity(
                Environment.ProcessId, "0.3.6", null, DateTimeOffset.UtcNow, "2.0.0"));
            var lease = new TestLease();
            var service = NewService(
                drainYieldRequests: _ => new YieldDrainResult(true, "2.0.0", 9999, 0, 0),
                ownExtractorVersion: () => "2.0.0");
            service.PublishOpsForTest(new RecordingOps());
            service.AssumeLeadershipForTest(lease);

            Assert.False(service.ProcessYieldRequestsForTest(dir));

            Assert.False(lease.Disposed); // equal versions never yield — no leadership thrash under swarms
            Assert.True(service.IsLeader);
            Assert.NotNull(LeaderIdentityFile.TryRead(dir));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void ProcessLeaderHandoffRequests_ValidRequest_Abdicates()
    {
        string dir = Path.Combine(Path.GetTempPath(), "miller-leadership-explicit-handoff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var requesterObservedAtUtc = new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);
            LeaderIdentityFile.Write(dir, new LeaderIdentity(
                Environment.ProcessId, "0.3.6", null, DateTimeOffset.UtcNow, "2.0.0"));
            var lease = new TestLease();
            var service = NewService(
                drainLeaderHandoffRequests: _ => new LeaderHandoffDrainResult(
                    true,
                    RequesterPid: 9999,
                    RequesterObservedAtUtc: requesterObservedAtUtc,
                    ExpiredDiscarded: 0,
                    ClaimSkipped: 0),
                ownExtractorVersion: () => "2.0.0",
                processAliveProbe: _ => true);
            service.PublishOpsForTest(new RecordingOps());
            service.AssumeLeadershipForTest(lease);

            Assert.True(service.ProcessLeaderHandoffRequestsForTest(dir));

            Assert.True(lease.Disposed);
            Assert.False(service.IsLeader);
            Assert.Null(LeaderIdentityFile.TryRead(dir));
            Assert.Equal(ScanOutcome.Kind.NotLeader, service.TryScanAsLeader(ScanIntent.UserFullRebuild).Result);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void ProcessLeaderHandoffRequests_DeadRequester_DoesNotAbdicate()
    {
        var lease = new TestLease();
        var service = NewService(
            drainLeaderHandoffRequests: _ => new LeaderHandoffDrainResult(
                true,
                RequesterPid: 9999,
                RequesterObservedAtUtc: DateTimeOffset.UtcNow,
                ExpiredDiscarded: 0,
                ClaimSkipped: 0),
            processAliveProbe: _ => false);
        service.PublishOpsForTest(new RecordingOps());
        service.AssumeLeadershipForTest(lease);

        Assert.False(service.ProcessLeaderHandoffRequestsForTest("/repo/.miller"));
        Assert.False(lease.Disposed);
        Assert.True(service.IsLeader);
    }

    [Fact]
    public void ProcessYieldRequests_OwnVersionUnknown_DoesNotAbdicate()
    {
        // Only reachable when leading under MILLER_ALLOW_EXTRACTOR_DOWNGRADE=1 with an unprobeable binary: the
        // operator explicitly forced this instance to index, so a challenger cannot prove it is newer.
        var lease = new TestLease();
        var service = NewService(
            drainYieldRequests: _ => new YieldDrainResult(true, "3.0.0", 9999, 0, 0),
            ownExtractorVersion: () => null,
            allowExtractorDowngrade: true);
        service.PublishOpsForTest(new RecordingOps());
        service.AssumeLeadershipForTest(lease);

        Assert.False(service.ProcessYieldRequestsForTest("/repo/.miller"));
        Assert.False(lease.Disposed);
    }

    [Fact]
    public void Abdication_Cooldown_BlocksReclaimWhileRequesterAlive_ResumesAfterExpiry()
    {
        string dir = Path.Combine(Path.GetTempPath(), "miller-leadership-cooldown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            DateTimeOffset now = new(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);
            bool acquireAttempted = false;
            var service = NewService(
                tryAcquireLeadership: _ =>
                {
                    acquireAttempted = true;
                    return new TestLease();
                },
                drainYieldRequests: _ => new YieldDrainResult(true, "3.0.0", 9999, 0, 0),
                ownExtractorVersion: () => "2.0.0",
                clock: () => now,
                processAliveProbe: _ => true);
            service.PublishOpsForTest(new RecordingOps());
            service.AssumeLeadershipForTest(new TestLease());
            Assert.True(service.ProcessYieldRequestsForTest(dir));

            now += TimeSpan.FromSeconds(59);
            Assert.False(service.AttemptClaimForTest(dir));
            Assert.False(acquireAttempted); // the challenger is alive and the 60s window is open: do not re-race it

            now += TimeSpan.FromSeconds(2); // 61s after the yield: the challenger never claimed — resume retries
            Assert.True(service.AttemptClaimForTest(dir));
            Assert.True(acquireAttempted);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Abdication_Cooldown_ProbesRequesterAgainstYieldRequestTimestamp()
    {
        string dir = Path.Combine(Path.GetTempPath(), "miller-leadership-cooldown-pid-reuse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var requesterObservedAtUtc = new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);
            DateTimeOffset? probedObservedAtUtc = null;
            bool acquireAttempted = false;
            var service = NewService(
                tryAcquireLeadership: _ =>
                {
                    acquireAttempted = true;
                    return new TestLease();
                },
                drainYieldRequests: _ => new YieldDrainResult(
                    true,
                    "3.0.0",
                    9999,
                    requesterObservedAtUtc,
                    0,
                    0),
                ownExtractorVersion: () => "2.0.0",
                processAliveProbeWithObserved: (_, observedAtUtc) =>
                {
                    probedObservedAtUtc = observedAtUtc;
                    return false;
                });
            service.PublishOpsForTest(new RecordingOps());
            service.AssumeLeadershipForTest(new TestLease());
            Assert.True(service.ProcessYieldRequestsForTest(dir));

            Assert.True(service.AttemptClaimForTest(dir));
            Assert.True(acquireAttempted);
            Assert.Equal(requesterObservedAtUtc, probedObservedAtUtc);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Abdication_Cooldown_ResumesWhenRequesterDies()
    {
        string dir = Path.Combine(Path.GetTempPath(), "miller-leadership-cooldown-dead-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            DateTimeOffset now = new(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);
            bool requesterAlive = true;
            bool acquireAttempted = false;
            var service = NewService(
                tryAcquireLeadership: _ =>
                {
                    acquireAttempted = true;
                    return new TestLease();
                },
                drainYieldRequests: _ => new YieldDrainResult(true, "3.0.0", 9999, 0, 0),
                ownExtractorVersion: () => "2.0.0",
                clock: () => now,
                processAliveProbe: _ => requesterAlive);
            service.PublishOpsForTest(new RecordingOps());
            service.AssumeLeadershipForTest(new TestLease());
            Assert.True(service.ProcessYieldRequestsForTest(dir));

            Assert.False(service.AttemptClaimForTest(dir));
            Assert.False(acquireAttempted);

            requesterAlive = false; // the challenger died without claiming — the workspace must not freeze
            Assert.True(service.AttemptClaimForTest(dir));
            Assert.True(acquireAttempted);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
