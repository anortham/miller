using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Miller.Core.Freshness;
using Miller.Indexing;
using Miller.Server;
using Miller.Server.Hosting;
using Miller.Server.Workspaces;
using Miller.Tests.Indexing;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the leader-gated scan trigger behind <c>workspace refresh/full</c> (M7 decision-3): only the indexer
/// LEADER (the instance holding the writer lock, with its <see cref="IExtractOps"/> published) may run an
/// <c>extract scan</c>; a non-leader must NOT scan (the M3 single-writer corruption guard) and reports
/// <see cref="ScanOutcome.Kind.NotLeader"/> honestly. The leader threads <paramref name="force"/> through to the
/// ops (delta vs from-scratch rebuild) and an extract failure surfaces as <see cref="ScanOutcome.Kind.Failed"/>,
/// never thrown into the tool. No FileSystemWatcher, no subprocess, no SQLite — the ops are faked and published
/// through the internal test seam that mirrors the production publish under <c>_opsGate</c>. The live subprocess
/// path is the Scale suite (<see cref="LiveWorkspaceTests"/>).
/// </summary>
public sealed class IndexerServiceScanTests : IDisposable
{
    // Static because the service factories below are static; xUnit runs this class's tests serially,
    // so draining the bag per-test Dispose never races another test in this class.
    private static readonly System.Collections.Concurrent.ConcurrentBag<string> TempHomes = [];

    // The background scan runs on a thread pool the whole fast suite is contending for; a 5s ceiling
    // false-negatives under ambient load. The event fires in ~90ms on a quiet box, so a generous ceiling
    // costs nothing on the happy path and only extends patience when the scheduler is starved.
    private const int ScanSignalTimeoutMs = 30_000;

    private static string CreateTempHome()
    {
        string tempHome = Path.Combine(Path.GetTempPath(), "miller-scan-home-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>A fake <see cref="IExtractOps"/> recording the force value of each scan; can be told to throw.</summary>
    private sealed class RecordingScanOps : IExtractOps
    {
        private readonly object _gate = new();
        private readonly List<bool> _scanForce = new();

        public ManualResetEventSlim ScanCalled { get; } = new();
        public IReadOnlyList<bool> ScanForce
        {
            get
            {
                lock (_gate)
                    return _scanForce.ToArray();
            }
        }

        public long? Revision { get; set; } = 7;
        public long? UpdateRevision { get; set; }
        public Exception? ThrowOnScan { get; set; }
        public Exception? ThrowOnUpdate { get; set; }

        private readonly List<string> _updatePaths = new();

        public IReadOnlyList<string> UpdatePaths
        {
            get
            {
                lock (_gate)
                    return _updatePaths.ToArray();
            }
        }

        public ExtractReport Update(string path)
        {
            lock (_gate)
                _updatePaths.Add(path);
            if (ThrowOnUpdate is not null)
                throw ThrowOnUpdate;
            return Stub(UpdateRevision ?? Revision);
        }

        public ExtractReport Delete(string path) => throw new NotSupportedException("not exercised here");

        /// <summary>Runs while the caller still holds machine-wide scan admission.</summary>
        public Action? WhileScanning { get; set; }

        /// <summary>The explicit --jobs cap of every scan dispatched, in order (null = ambient policy).</summary>
        public IReadOnlyList<int?> ScanJobs
        {
            get
            {
                lock (_gate)
                    return _scanJobs.ToArray();
            }
        }

        private readonly List<int?> _scanJobs = new();

        /// <summary>The intent of every scan dispatched, in order.</summary>
        public IReadOnlyList<ScanIntent> ScanIntents
        {
            get
            {
                lock (_gate)
                    return _scanIntents.ToArray();
            }
        }

        private readonly List<ScanIntent> _scanIntents = new();

        public ExtractReport Scan(ScanIntent intent = ScanIntent.IncrementalReconcile, int? jobs = null)
        {
            lock (_gate)
            {
                _scanForce.Add(ScanIntentPolicy.RequiresForce(intent));
                _scanJobs.Add(jobs);
                _scanIntents.Add(intent);
            }
            ScanCalled.Set();
            WhileScanning?.Invoke();
            if (ThrowOnScan is not null)
                throw ThrowOnScan;
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

    private sealed class TestLease : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    /// <summary>Captures log level + rendered message so a test can pin a log-throttle contract.</summary>
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

    // A never-started IndexerService: TryScanAsLeader reads only the published _ops under _opsGate (it never
    // touches the bootstrap), so an un-started instance is the correct, I/O-free unit-test surface. The sidecar
    // defaults OFF, so the disabled (byte-identical) path is what these no-workspace tests exercise.
    private static IndexerService NewService(
        Func<string, FullScanDrainResult>? drainFullScanRequests = null,
        Func<string, FileConvergeDrainResult>? drainFileConvergeRequests = null,
        ScanGovernor? scanGovernor = null,
        TimeSpan? scanGovernorWait = null)
    {
        string tempHome = CreateTempHome();
        var bootstrap = new IndexBootstrapService(NullLogger<IndexBootstrapService>.Instance);
        bootstrap.TestHomeDirectoryOverride = tempHome;
        return new(
            bootstrap,
            NullLogger<IndexerService>.Instance,
            NullLoggerFactory.Instance,
            tryAcquireLeadership: _ => null,
            createOps: static (_, _, _) => throw new InvalidOperationException("not used by this test seam"),
            leaderRetryInterval: TimeSpan.FromHours(1),
            SymbolSearchSidecar.Disabled,
            attachFileWatchers: false,
            drainFullScanRequests: drainFullScanRequests,
            drainFileConvergeRequests: drainFileConvergeRequests,
            scanGovernor: scanGovernor,
            scanGovernorWait: scanGovernorWait);
    }

    // A leader-capable instance whose bootstrap is SEEDED with a workspace (so TryScanAsLeader can read its
    // CanonicalExtractDbPath for the sidecar build) and whose sidecar gate is the caller's choice. Not started —
    // PublishOpsForTest makes it the leader without the cross-process lock or a subprocess.
    private static IndexerService NewSeededService(
        WorkspaceContext workspace,
        SymbolSearchSidecar sidecar,
        Func<string, FileConvergeDrainResult>? drainFileConvergeRequests = null,
        ScanGovernor? scanGovernor = null,
        TimeSpan? scanGovernorWait = null,
        Func<string, FullScanDrainResult>? drainFullScanRequests = null,
        Func<string?>? ownExtractorVersion = null)
    {
        string tempHome = Path.GetDirectoryName(Path.GetDirectoryName(workspace.RegistryDbPath))!;
        var bootstrap = new IndexBootstrapService(NullLogger<IndexBootstrapService>.Instance);
        bootstrap.TestHomeDirectoryOverride = tempHome;
        bootstrap.SeedForTest(
            workspace,
            new IndexHolder(MillerRepositoryIndex.Build(System.Array.Empty<IndexedSymbol>()), builtRevision: 0));
        if (drainFileConvergeRequests is null && scanGovernor is null && drainFullScanRequests is null &&
            ownExtractorVersion is null)
            return new IndexerService(
                bootstrap, NullLogger<IndexerService>.Instance, NullLoggerFactory.Instance, sidecar);
        return new IndexerService(
            bootstrap,
            NullLogger<IndexerService>.Instance,
            NullLoggerFactory.Instance,
            tryAcquireLeadership: _ => null,
            createOps: static (_, _, _) => throw new InvalidOperationException("not used by this test seam"),
            leaderRetryInterval: TimeSpan.FromHours(1),
            sidecar,
            attachFileWatchers: false,
            drainFullScanRequests: drainFullScanRequests,
            drainFileConvergeRequests: drainFileConvergeRequests,
            ownExtractorVersion: ownExtractorVersion,
            scanGovernor: scanGovernor,
            scanGovernorWait: scanGovernorWait);
    }

    private static IndexerService NewStartedService(
        WorkspaceContext workspace,
        Func<string, IDisposable?> tryAcquireLeadership,
        Func<WorkspaceContext, string, string, IExtractOps> createOps,
        SymbolSearchSidecar? sidecar = null)
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
            createOps,
            TimeSpan.FromHours(1),
            sidecar ?? SymbolSearchSidecar.Disabled,
            attachFileWatchers: false,
            // Stub the own-version probe: the production default execs the bundled julie-extract, which
            // both spawns a subprocess in the fast suite and fails (5s timeout each) when .tools is absent.
            ownExtractorVersion: static () => MillerExtractContract.PinnedJulieExtractVersion);
    }

    // A tiny real julie artifact (synthetic, no subprocess) the sidecar build can read symbols from. The interior
    // 'thenti' trigram inside IAuthen|tica|tion is only recoverable from the disk artifact's trigram arm.
    private static JulieDbFixture JulieDb() => JulieDbFixture.Create(
        JulieDbFixture.PinnedSchema,
        JulieDbFixture.PinnedContract,
        new[]
        {
            new JulieDbFixture.SymbolRow("s1", "IAuthenticationProvider", "interface", "csharp",
                "src/Auth.cs", "public interface IAuthenticationProvider", 1, ParentId: null),
            new JulieDbFixture.SymbolRow("s2", "Cache", "class", "csharp",
                "src/Cache.cs", "public class Cache", 1, ParentId: null),
        });

    private static JulieDbFixture JulieSourceDb(string marker)
    {
        const string path = "src/Source.cs";
        string text = $$"""
            public class Source
            {
                public void Handle()
                {
                    throw new InvalidOperationException("{{marker}}");
                }
            }
            """;
        return JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                new JulieDbFixture.SymbolRow("sym-source", "Source", "class", "csharp",
                    path, "public class Source", 1, ParentId: null)
                {
                    EndLine = 7,
                },
            },
            fileContent: new Dictionary<string, string> { [path] = text });
    }

    private static void CreateSentinelTable(string searchDb)
    {
        using (var rw = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = searchDb, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
        }.ToString()))
        {
            rw.Open();
            using var cmd = rw.CreateCommand();
            cmd.CommandText = "CREATE TABLE incremental_sentinel(value INTEGER);";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
    }

    private static bool TableExists(string searchDb, string tableName)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = searchDb, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
        }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name;";
        cmd.Parameters.AddWithValue("$name", tableName);
        return cmd.ExecuteScalar() is not null;
    }

    // A workspace whose CanonicalExtractDbPath points at <paramref name="dbPath"/> (the symbols.db the sidecar
    // build reads + writes its search.db sibling next to). The repo root is a real sibling dir so a started
    // instance's FileSystemWatcher can attach; it lives under the db's dir so the fixture's cleanup removes it.
    private static WorkspaceContext WorkspaceWithDb(string dbPath)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(dbPath))!;
        string root = Path.Combine(dir, "repo");
        string home = Path.Combine(dir, "home");
        Directory.CreateDirectory(root);
        string canonicalRoot = Path.GetFullPath(root);
        return WorkspaceContext.Create(root, AppContext.BaseDirectory, home) with
        {
            WorkspaceId = WorkspaceId.FromCanonicalRoot(canonicalRoot),
            CanonicalRoot = canonicalRoot,
            CanonicalExtractDbPath = dbPath,
        };
    }

    private static WorkspaceContext CreateWorkspace(string dir)
    {
        string root = Path.Combine(dir, "repo");
        string home = Path.Combine(dir, "home");
        Directory.CreateDirectory(root);
        string canonicalRoot = Path.GetFullPath(root);
        string stableId = WorkspaceId.FromCanonicalRoot(canonicalRoot);
        return WorkspaceContext.Create(root, AppContext.BaseDirectory, home) with
        {
            WorkspaceId = stableId,
            CanonicalRoot = canonicalRoot,
            CanonicalExtractDbPath = Path.Combine(canonicalRoot, ".miller", "symbols.db"),
        };
    }

    [Fact]
    public void TryScanAsLeader_WhenNotLeader_DoesNotScan_AndReportsNotLeader()
    {
        var service = NewService(); // no ops published => not the leader

        ScanOutcome outcome = service.TryScanAsLeader(ScanIntent.IncrementalReconcile, bypassBackoff: true);

        Assert.Equal(ScanOutcome.Kind.NotLeader, outcome.Result);
        Assert.Null(outcome.Report); // a non-leader produced no extract report (it cannot write)
    }

    [Fact]
    public void TryScanAsLeader_WhenLeader_DeltaScan_RunsForceFalse_AndReportsScanned()
    {
        var service = NewService();
        var ops = new RecordingScanOps();
        service.PublishOpsForTest(ops); // become the leader (the production publish happens once leadership wins)

        ScanOutcome outcome = service.TryScanAsLeader(ScanIntent.IncrementalReconcile, bypassBackoff: true);

        Assert.Equal(ScanOutcome.Kind.Scanned, outcome.Result);
        Assert.Equal(new[] { false }, ops.ScanForce); // refresh = delta reconcile (no --force)
        Assert.NotNull(outcome.Report);
        Assert.Equal(7, outcome.Report!.Revision);
    }

    [Fact]
    public void TryScanAsLeader_WhenLeader_ForceTrue_ThreadsForceThrough()
    {
        var service = NewService();
        var ops = new RecordingScanOps();
        service.PublishOpsForTest(ops);

        ScanOutcome outcome = service.TryScanAsLeader(ScanIntent.UserFullRebuild, bypassBackoff: true);

        Assert.Equal(ScanOutcome.Kind.Scanned, outcome.Result);
        Assert.Equal(new[] { true }, ops.ScanForce); // full = from-scratch rebuild (--force)
    }

    [Fact]
    public void ProcessLeaderFullScanRequests_WhenRequestExists_RunsForceScanAsLeader()
    {
        var service = NewService(drainFullScanRequests: _ => new FullScanDrainResult(true, 0, 0));
        var ops = new RecordingScanOps { Revision = 12 };
        service.PublishOpsForTest(ops);

        bool processed = service.ProcessLeaderFullScanRequestsForTest("/repo/.miller");

        Assert.True(processed);
        Assert.Equal(new[] { true }, ops.ScanForce);
    }

    [Fact]
    public void ProcessFileConvergeRequests_AsLeader_ReindexesEachRequestedFile()
    {
        using var julie = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                new JulieDbFixture.SymbolRow("edit-new", "UpdatedType", "class", "csharp",
                    "src/Edit.cs", "public class UpdatedType", 1, ParentId: null),
            },
            revisions: new[]
            {
                new JulieDbFixture.RevisionRow(1),
                new JulieDbFixture.RevisionRow(2, Kind: "single_file"),
            },
            fileChanges: new[]
            {
                new JulieDbFixture.RevisionFileChangeRow(2, "src/Edit.cs", "updated"),
            });
        var service = NewSeededService(
            WorkspaceWithDb(julie.DbPath),
            SymbolSearchSidecar.Disabled,
            drainFileConvergeRequests: _ => new FileConvergeDrainResult(
                new[] { "src/Edit.cs", "src/Keep.cs" }, 0, 0));
        var ops = new RecordingScanOps { UpdateRevision = 2 };
        service.PublishOpsForTest(ops);

        // The drain enqueues into the core's coalescing queue; the same tick's debounce drain runs the extracts.
        bool processed = service.ProcessFileConvergeRequestsForTest("/repo/.miller");
        service.DrainForTest(headChanged: false);

        Assert.True(processed);
        Assert.Equal(new[] { "src/Edit.cs", "src/Keep.cs" }, ops.UpdatePaths);
        Assert.Empty(ops.ScanForce); // single-file converge must never escalate to a whole-repo scan
    }

    [Fact]
    public void ProcessFileConvergeRequests_RequestAndWatcherEventForSameFile_RunExactlyOneReindex()
    {
        // M3: a reader's converge request and the FileSystemWatcher event for the SAME file write land on the
        // same debounce tick. Routing the drained request through the core's coalescing WatchEventQueue (instead
        // of an immediate TryReindexAsLeader) must collapse them into ONE extract, not two.
        using var julie = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                new JulieDbFixture.SymbolRow("edit-new", "UpdatedType", "class", "csharp",
                    "src/Edit.cs", "public class UpdatedType", 1, ParentId: null),
            },
            revisions: new[]
            {
                new JulieDbFixture.RevisionRow(1),
                new JulieDbFixture.RevisionRow(2, Kind: "single_file"),
            },
            fileChanges: new[]
            {
                new JulieDbFixture.RevisionFileChangeRow(2, "src/Edit.cs", "updated"),
            });
        var service = NewSeededService(
            WorkspaceWithDb(julie.DbPath),
            SymbolSearchSidecar.Disabled,
            drainFileConvergeRequests: _ => new FileConvergeDrainResult(new[] { "src/Edit.cs" }, 0, 0));
        var ops = new RecordingScanOps { UpdateRevision = 2 };
        service.PublishOpsForTest(ops);

        // The watcher already queued a Modified event for the file write that made the reader's index stale...
        service.EnqueueForTest(new WatchEvent("src/Edit.cs", WatchEventKind.Modified));
        // ...and the same tick drains the reader's converge request, then the queue.
        Assert.True(service.ProcessFileConvergeRequestsForTest("/repo/.miller"));
        service.DrainForTest(headChanged: false);

        string updated = Assert.Single(ops.UpdatePaths); // exactly ONE extract, not request + watcher = two
        Assert.Equal("src/Edit.cs", updated);
        Assert.Empty(ops.ScanForce);
    }

    [Fact]
    public async Task StartAsync_AsLeader_RecordsLeaderIdentity_AndRemovesItOnStop()
    {
        using var julie = JulieDb();
        var lease = new TestLease();
        var ops = new RecordingScanOps { Revision = 13 };
        WorkspaceContext workspace = WorkspaceWithDb(julie.DbPath);
        string millerDir = Path.GetDirectoryName(workspace.ExtractDbPath)!;
        var service = NewStartedService(workspace, _ => lease, (_, _, _) => ops);

        await service.StartAsync(CancellationToken.None);
        Assert.True(ops.ScanCalled.Wait(ScanSignalTimeoutMs, CancellationToken.None));

        LeaderIdentity? identity = LeaderIdentityFile.TryRead(millerDir);
        Assert.NotNull(identity);
        Assert.Equal(Environment.ProcessId, identity!.Pid);
        Assert.Equal(MillerVersion.Current, identity.Version);

        await service.StopAsync(CancellationToken.None);
        // Graceful step-down removes the identity so a later health probe never sees OUR pid as a stale leader.
        Assert.Null(LeaderIdentityFile.TryRead(millerDir));
    }

    [Fact]
    public async Task StartAsync_AsLeader_WhenIdentityWriteFails_ClearsPredecessorIdentity()
    {
        using var julie = JulieDb();
        var lease = new TestLease();
        var ops = new RecordingScanOps { Revision = 13 };
        WorkspaceContext workspace = WorkspaceWithDb(julie.DbPath);
        string millerDir = Path.GetDirectoryName(workspace.ExtractDbPath)!;
        // A crashed predecessor's identity is on disk...
        LeaderIdentityFile.Write(millerDir, new LeaderIdentity(
            987654, "0.0.1+dead123", null, DateTimeOffset.UtcNow.AddHours(-3)));
        // ...and OUR identity write is sabotaged: the atomic-write temp slot is occupied by a directory, so
        // File.WriteAllText throws UnauthorizedAccessException (the caught set in the leader's write guard).
        Directory.CreateDirectory(LeaderIdentityFile.PathFor(millerDir) + ".tmp");
        var service = NewStartedService(workspace, _ => lease, (_, _, _) => ops);

        await service.StartAsync(CancellationToken.None);
        Assert.True(ops.ScanCalled.Wait(ScanSignalTimeoutMs, CancellationToken.None));

        // L1: a failed write must never leave the predecessor's stale identity as the visible truth — health
        // would report a dead/mismatched leader while a healthy one runs.
        Assert.Null(LeaderIdentityFile.TryRead(millerDir));
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void ProcessFileConvergeRequests_ClaimSkips_WarnOnceThenDebug()
    {
        // M4: an unclaimable (wedged) request is a real diagnostic the first time, but it recurs every 250ms
        // tick until the TTL sweep clears it — the repeat must drop to Debug, not warn forever.
        var logger = new RecordingLogger<IndexerService>();
        string tempHome = CreateTempHome();
        var bootstrap = new IndexBootstrapService(NullLogger<IndexBootstrapService>.Instance);
        bootstrap.TestHomeDirectoryOverride = tempHome;
        var service = new IndexerService(
            bootstrap,
            logger,
            NullLoggerFactory.Instance,
            tryAcquireLeadership: _ => null,
            createOps: static (_, _, _) => throw new InvalidOperationException("not used by this test seam"),
            leaderRetryInterval: TimeSpan.FromHours(1),
            SymbolSearchSidecar.Disabled,
            attachFileWatchers: false,
            drainFileConvergeRequests: _ => new FileConvergeDrainResult(System.Array.Empty<string>(), 0, 1));
        service.PublishOpsForTest(new RecordingScanOps());

        service.ProcessFileConvergeRequestsForTest("/repo/.miller");
        service.ProcessFileConvergeRequestsForTest("/repo/.miller");

        var skipLogs = logger.Entries.Where(e => e.Message.Contains("could not be claimed")).ToArray();
        Assert.Equal(2, skipLogs.Length);
        Assert.Equal(LogLevel.Warning, skipLogs[0].Level);
        Assert.Equal(LogLevel.Debug, skipLogs[1].Level);
    }

    [Fact]
    public void ProcessFileConvergeRequests_WhenNotLeader_DoesNotDrainRequests()
    {
        // A non-leader CANNOT reindex; draining would consume (delete) the request files without servicing
        // them, losing the converge for the real leader. The drain must not even run.
        bool drained = false;
        var service = NewService(drainFileConvergeRequests: _ =>
        {
            drained = true;
            return new FileConvergeDrainResult(new[] { "src/Edit.cs" }, 0, 0);
        });

        Assert.False(service.ProcessFileConvergeRequestsForTest("/repo/.miller"));
        Assert.False(drained);
    }

    [Fact]
    public void WatcherDirectoryDelete_ForcesDeltaScan()
    {
        string dir = Path.Combine(Path.GetTempPath(), "miller-indexer-dir-delete-" + Guid.NewGuid().ToString("N"));
        try
        {
            WorkspaceContext workspace = CreateWorkspace(dir);
            var service = NewSeededService(workspace, SymbolSearchSidecar.Disabled);
            var ops = new RecordingScanOps { Revision = 13 };
            service.PublishOpsForTest(ops);

            service.HandleDirectoryChangedForTest(Path.Combine(workspace.CanonicalRoot!, "src"));
            service.DrainForTest(headChanged: false);

            Assert.Equal(new[] { false }, ops.ScanForce);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void WatcherExistingDirectoryChange_WithExtensionGate_ForcesDeltaScan()
    {
        string dir = Path.Combine(Path.GetTempPath(), "miller-indexer-dir-change-" + Guid.NewGuid().ToString("N"));
        try
        {
            WorkspaceContext workspace = CreateWorkspace(dir);
            string srcDir = Path.Combine(workspace.CanonicalRoot!, "src");
            Directory.CreateDirectory(srcDir);
            var service = NewSeededService(workspace, SymbolSearchSidecar.Disabled);
            var ops = new RecordingScanOps { Revision = 13 };
            service.PublishOpsForTest(ops);
            service.SetSupportedExtensionsForTest(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "cs" });

            service.HandleChangedForTest(WatcherChangeTypes.Changed, srcDir);
            service.DrainForTest(headChanged: false);

            Assert.Equal(new[] { false }, ops.ScanForce);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void WatcherDirectoryRename_ForcesDeltaScan()
    {
        string dir = Path.Combine(Path.GetTempPath(), "miller-indexer-dir-rename-" + Guid.NewGuid().ToString("N"));
        try
        {
            WorkspaceContext workspace = CreateWorkspace(dir);
            var service = NewSeededService(workspace, SymbolSearchSidecar.Disabled);
            var ops = new RecordingScanOps { Revision = 13 };
            service.PublishOpsForTest(ops);

            service.HandleDirectoryRenamedForTest(
                Path.Combine(workspace.CanonicalRoot!, "old"),
                Path.Combine(workspace.CanonicalRoot!, "new"));
            service.DrainForTest(headChanged: false);

            Assert.Equal(new[] { false }, ops.ScanForce);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void WatcherIgnorePolicyFileChange_ForcesDeltaScan()
    {
        string dir = Path.Combine(Path.GetTempPath(), "miller-indexer-ignore-policy-" + Guid.NewGuid().ToString("N"));
        try
        {
            WorkspaceContext workspace = CreateWorkspace(dir);
            var service = NewSeededService(workspace, SymbolSearchSidecar.Disabled);
            var ops = new RecordingScanOps { Revision = 13 };
            service.PublishOpsForTest(ops);

            service.HandleChangedForTest(
                WatcherChangeTypes.Changed,
                Path.Combine(workspace.CanonicalRoot!, ".gitignore"));
            service.DrainForTest(headChanged: false);

            Assert.Equal(new[] { false }, ops.ScanForce);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void WatcherDrain_MarksRegistryAtLatestRevision()
    {
        using var julie = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                new JulieDbFixture.SymbolRow("edit-new", "UpdatedType", "class", "csharp",
                    "src/Edit.cs", "public class UpdatedType", 1, ParentId: null),
            },
            revisions: new[]
            {
                new JulieDbFixture.RevisionRow(1),
                new JulieDbFixture.RevisionRow(2, Kind: "single_file"),
            },
            fileChanges: new[]
            {
                new JulieDbFixture.RevisionFileChangeRow(2, "src/Edit.cs", "updated"),
            });
        WorkspaceContext workspace = WorkspaceWithDb(julie.DbPath);
        string workspaceId = workspace.WorkspaceId!;
        IndexBootstrapService.RegisterBootstrapWorkspace(
            workspace, workspaceId, WorkspaceRegistryState.LoadedExisting, revision: 1);
        var service = NewSeededService(workspace, SymbolSearchSidecar.Disabled);
        service.PublishOpsForTest(new RecordingScanOps { UpdateRevision = 2 });

        service.HandleChangedForTest(
            WatcherChangeTypes.Changed,
            Path.Combine(workspace.CanonicalRoot!, "src", "Edit.cs"));
        service.DrainForTest(headChanged: false);

        using var registry = WorkspaceRegistry.Open(workspace.RegistryDbPath);
        WorkspaceRegistryRow row = Assert.IsType<WorkspaceRegistryRow>(registry.Get(workspaceId));
        Assert.Equal(WorkspaceRegistryState.Ready, row.State);
        Assert.Equal(2, row.LastRevision);
    }

    [Fact]
    public void TryScanAsLeader_WhenLeaderScanThrows_ReportsFailed_NeverThrows()
    {
        var service = NewService();
        var ops = new RecordingScanOps
        {
            ThrowOnScan = new JulieExtractException("boom", standardError: "disk full"),
        };
        service.PublishOpsForTest(ops);

        // Best-effort: an extract failure is logged + returned as Failed, never thrown into the caller (the tool).
        ScanOutcome outcome = service.TryScanAsLeader(ScanIntent.UserFullRebuild, bypassBackoff: true);

        Assert.Equal(ScanOutcome.Kind.Failed, outcome.Result);
        Assert.Null(outcome.Report);
        Assert.Equal(new[] { true }, ops.ScanForce); // the scan WAS attempted (then threw)
    }

    // ---- W3: machine-wide scan admission on the leader's whole-repo paths ----
    // The per-workspace writer lock cannot stop N worktrees from each running a whole-repo extract at once (that
    // is the OOM in the 2026-08-01 field report). Admission is user-global and capacity 1, so a leader that
    // cannot get it must degrade to the prior index rather than scan ungoverned. Per-file update/delete stays
    // exempt — it is cheap and blocking it would stall interactive write-through.

    private static ScanGovernorLease HoldMachineScanAdmission(string millerHome) =>
        ScanGovernor.ForMillerHome(millerHome).TryAcquire(
            new ScanGovernorRequest("/repo/other-worktree", "test-holder", 4),
            TimeSpan.Zero,
            CancellationToken.None)
        ?? throw new InvalidOperationException("A fresh temp miller home must have a free scan lease.");

    // A refusal is routine under the short interactive budget, so reporting it as Failed sent agents hunting an
    // extract error that was never logged while the rebuild they asked for was dropped.
    [Fact]
    public void TryScanAsLeader_WhenMachineScanAdmissionIsBusy_ReportsQueued_WithoutScanning()
    {
        string home = CreateTempHome();
        var service = NewService(
            scanGovernor: ScanGovernor.ForMillerHome(home), scanGovernorWait: TimeSpan.Zero);
        var ops = new RecordingScanOps();
        service.PublishOpsForTest(ops);

        ScanOutcome outcome;
        using (ScanGovernorLease held = HoldMachineScanAdmission(home))
            outcome = service.TryScanAsLeader(ScanIntent.UserFullRebuild, bypassBackoff: true);

        Assert.Equal(ScanOutcome.Kind.Queued, outcome.Result);
        Assert.Null(outcome.Report);
        Assert.NotNull(outcome.HolderDescription);
        Assert.Empty(ops.ScanForce);
    }

    [Fact]
    public void TryScanAsLeader_WhenMachineScanAdmissionIsBusy_RearmsTheForcedScan()
    {
        using var julie = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, System.Array.Empty<JulieDbFixture.SymbolRow>());
        string home = CreateTempHome();
        var service = NewSeededService(
            WorkspaceWithDb(julie.DbPath),
            SymbolSearchSidecar.Disabled,
            scanGovernor: ScanGovernor.ForMillerHome(home),
            scanGovernorWait: TimeSpan.Zero);
        var ops = new RecordingScanOps();
        service.PublishOpsForTest(ops);

        using (ScanGovernorLease held = HoldMachineScanAdmission(home))
            service.TryScanAsLeader(ScanIntent.UserFullRebuild, bypassBackoff: true);

        Assert.False(service.QueueEmpty);

        service.DrainForTest(headChanged: false);

        Assert.Equal(new[] { true }, ops.ScanForce);
    }

    [Fact]
    public void TryScanAsLeader_WhenMachineScanAdmissionIsFree_Scans_AndReleasesTheLeaseAfterwards()
    {
        string home = CreateTempHome();
        var service = NewService(
            scanGovernor: ScanGovernor.ForMillerHome(home), scanGovernorWait: TimeSpan.Zero);
        service.PublishOpsForTest(new RecordingScanOps());

        ScanOutcome outcome = service.TryScanAsLeader(ScanIntent.IncrementalReconcile, bypassBackoff: true);

        Assert.Equal(ScanOutcome.Kind.Scanned, outcome.Result);

        using ScanGovernorLease? afterwards = ScanGovernor.ForMillerHome(home).TryAcquire(
            new ScanGovernorRequest("/repo/other-worktree", "probe", 4), TimeSpan.Zero, CancellationToken.None);
        Assert.NotNull(afterwards);
    }

    [Fact]
    public void TryScanAsLeader_WhenNotLeader_NeverWaitsForMachineScanAdmission()
    {
        string home = CreateTempHome();
        using ScanGovernorLease held = HoldMachineScanAdmission(home);
        var service = NewService(
            scanGovernor: ScanGovernor.ForMillerHome(home), scanGovernorWait: TimeSpan.FromMinutes(10));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        ScanOutcome outcome = service.TryScanAsLeader(ScanIntent.IncrementalReconcile, bypassBackoff: true);
        stopwatch.Stop();

        Assert.Equal(ScanOutcome.Kind.NotLeader, outcome.Result);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"waited {stopwatch.Elapsed}");
    }

    [Fact]
    public void ProcessLeaderFullScanRequests_WhenMachineScanAdmissionIsBusy_DoesNotScan_AndRearmsTheRescan()
    {
        using var julie = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                new JulieDbFixture.SymbolRow("edit-new", "UpdatedType", "class", "csharp",
                    "src/Edit.cs", "public class UpdatedType", 1, ParentId: null),
            });
        string home = CreateTempHome();
        var service = NewSeededService(
            WorkspaceWithDb(julie.DbPath),
            SymbolSearchSidecar.Disabled,
            scanGovernor: ScanGovernor.ForMillerHome(home),
            scanGovernorWait: TimeSpan.Zero,
            drainFullScanRequests: _ => new FullScanDrainResult(true, 0, 0));
        var ops = new RecordingScanOps();
        service.PublishOpsForTest(ops);

        bool processed;
        using (ScanGovernorLease held = HoldMachineScanAdmission(home))
            processed = service.ProcessLeaderFullScanRequestsForTest("/repo/.miller");

        Assert.False(processed);
        Assert.Empty(ops.ScanForce);

        // The drain already deleted the request file, so the requester's rebuild would be lost unless the refusal
        // re-armed the core's latch — the next debounce tick must run it, and it must still be FORCED.
        service.DrainForTest(headChanged: false);

        Assert.Equal(new[] { true }, ops.ScanForce);
    }

    [Fact]
    public void TryReindexAsLeader_TakesNoMachineScanAdmission()
    {
        using var julie = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                new JulieDbFixture.SymbolRow("edit-new", "UpdatedType", "class", "csharp",
                    "src/Edit.cs", "public class UpdatedType", 1, ParentId: null),
            });
        string home = CreateTempHome();
        using ScanGovernorLease held = HoldMachineScanAdmission(home);
        var service = NewSeededService(
            WorkspaceWithDb(julie.DbPath),
            SymbolSearchSidecar.Disabled,
            scanGovernor: ScanGovernor.ForMillerHome(home),
            scanGovernorWait: TimeSpan.FromMinutes(10));
        var ops = new RecordingScanOps { UpdateRevision = 2 };
        service.PublishOpsForTest(ops);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        bool reindexed = service.TryReindexAsLeader("src/Edit.cs");
        stopwatch.Stop();

        Assert.True(reindexed);
        Assert.Equal(new[] { "src/Edit.cs" }, ops.UpdatePaths);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"waited {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task StartAsync_WhenLeader_RunsExactlyOneStartupDeltaScan_AndMarksRegistryScanned()
    {
        string dir = Path.Combine(Path.GetTempPath(), "miller-indexer-startup-leader-" + Guid.NewGuid().ToString("N"));
        var lease = new TestLease();
        var ops = new RecordingScanOps { Revision = 11 };
        try
        {
            var workspace = CreateWorkspace(dir);
            string workspaceId = workspace.WorkspaceId!;
            IndexBootstrapService.RegisterBootstrapWorkspace(
                workspace, workspaceId, WorkspaceRegistryState.LoadedExisting, revision: 4);
            var service = NewStartedService(
                workspace,
                _ => lease,
                (_, root, db) =>
                {
                    Assert.Equal(workspace.CanonicalRoot, root);
                    Assert.Equal(workspace.CanonicalExtractDbPath, db);
                    return ops;
                });

            await service.StartAsync(CancellationToken.None);
            Assert.True(ops.ScanCalled.Wait(ScanSignalTimeoutMs, CancellationToken.None));
            await service.StopAsync(CancellationToken.None);

            Assert.Equal(new[] { false }, ops.ScanForce);
            using var registry = WorkspaceRegistry.Open(workspace.RegistryDbPath);
            var row = registry.Get(workspaceId);
            Assert.NotNull(row);
            Assert.Equal(WorkspaceRegistryState.Ready, row.State);
            Assert.Equal(11, row.LastRevision);
            Assert.NotNull(row.LastScanAt);
            Assert.True(lease.Disposed);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    // ---- Phase 1: the leader builds the on-disk search.db sidecar after its scans (enabled), best-effort -----

    [Fact]
    public void TryScanAsLeader_WhenEnabledLeader_BuildsRevisionFreshSearchSidecar()
    {
        using var julie = JulieDb();
        var sidecar = new SymbolSearchSidecar(enabled: true);
        var service = NewSeededService(WorkspaceWithDb(julie.DbPath), sidecar);
        service.PublishOpsForTest(new RecordingScanOps { Revision = 9 });

        ScanOutcome outcome = service.TryScanAsLeader(ScanIntent.IncrementalReconcile, bypassBackoff: true);

        Assert.Equal(ScanOutcome.Kind.Scanned, outcome.Result);
        string searchDb = SymbolSearchSidecar.SearchDbPathFor(julie.DbPath);
        Assert.True(File.Exists(searchDb), $"expected the leader to build {searchDb}");
        // The artifact is usable AND stamped with the scanned revision (the strict-equality routing contract).
        FtsSymbolSearchIndex? index = sidecar.TryOpen(julie.DbPath, expectedRevision: 9);
        Assert.NotNull(index);
        Assert.Equal(9L, index!.Revision);
    }

    [Fact]
    public void TryScanAsLeader_BuildsRevisionFreshContentCorpusSidecar()
    {
        using var julie = JulieSourceDb("KnownSourceError");
        WorkspaceContext workspace = WorkspaceWithDb(julie.DbPath) with
        {
            WorkspaceRoot = julie.WorkspaceRoot,
            CanonicalRoot = julie.WorkspaceRoot,
        };
        var service = NewSeededService(workspace, SymbolSearchSidecar.Disabled);
        service.PublishOpsForTest(new RecordingScanOps { Revision = 9 });

        ScanOutcome outcome = service.TryScanAsLeader(ScanIntent.IncrementalReconcile, bypassBackoff: true);

        Assert.Equal(ScanOutcome.Kind.Scanned, outcome.Result);
        string contentDb = ContentCorpusSidecar.ContentDbPathFor(julie.DbPath);
        Assert.True(File.Exists(contentDb), $"expected the leader to build {contentDb}");
        FtsTextContentSearchIndex index = FtsTextContentSearchIndex.Open(contentDb, expectedRevision: 9);
        Assert.Single(index.Search("KnownSourceError", TextContentKind.WorkspaceSource, limit: 10));
    }

    [Fact]
    public void TryScanAsLeader_WhenEnabledLeader_RebuildsStaleSearchSidecarAfterScan()
    {
        using var julie = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                new JulieDbFixture.SymbolRow("edit-new", "UpdatedType", "class", "csharp",
                    "src/Edit.cs", "public class UpdatedType", 1, ParentId: null),
                new JulieDbFixture.SymbolRow("keep", "Anchor", "class", "csharp",
                    "src/Keep.cs", "public class Anchor", 1, ParentId: null),
            },
            revisions: new[]
            {
                new JulieDbFixture.RevisionRow(1),
                new JulieDbFixture.RevisionRow(2, Kind: "full"),
            },
            fileChanges: new[]
            {
                new JulieDbFixture.RevisionFileChangeRow(2, "src/Edit.cs", "updated"),
            });
        string searchDb = SymbolSearchSidecar.SearchDbPathFor(julie.DbPath);
        SearchIndexWriter.Write(searchDb, new[]
        {
            new IndexedSymbol(0, "edit-old", "LegacyWidget", "public class LegacyWidget", "class",
                "csharp", "src/Edit.cs", 1, 1, ParentId: null, IsTest: false),
            new IndexedSymbol(1, "keep", "Anchor", "public class Anchor", "class",
                "csharp", "src/Keep.cs", 1, 1, ParentId: null, IsTest: false),
        }, revision: 1, symbolsDbPath: julie.DbPath, workspaceRoot: julie.WorkspaceRoot,
            regionOptions: RegionIndexOptions.EnabledDefault);
        CreateSentinelTable(searchDb);
        var sidecar = new SymbolSearchSidecar(enabled: true);
        var service = NewSeededService(WorkspaceWithDb(julie.DbPath), sidecar);
        service.PublishOpsForTest(new RecordingScanOps { Revision = 2 });

        ScanOutcome outcome = service.TryScanAsLeader(ScanIntent.IncrementalReconcile, bypassBackoff: true);

        Assert.Equal(ScanOutcome.Kind.Scanned, outcome.Result);
        Assert.False(TableExists(searchDb, "incremental_sentinel"));
        FtsSymbolSearchIndex index = Assert.IsType<FtsSymbolSearchIndex>(
            sidecar.TryOpen(julie.DbPath, expectedRevision: 2));
        Assert.Empty(index.Search("LegacyWidget", limit: 10));
        Assert.Equal("UpdatedType", index.Resolve(Assert.Single(index.Search("UpdatedType", limit: 10)).Document.DocId).Name);
        Assert.Equal("Anchor", index.Resolve(Assert.Single(index.Search("Anchor", limit: 10)).Document.DocId).Name);
    }

    [Fact]
    public void TryReindexAsLeader_WhenEnabledLeader_ConvergesSearchSidecarAfterSingleFileUpdate()
    {
        using var julie = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                new JulieDbFixture.SymbolRow("edit-new", "UpdatedType", "class", "csharp",
                    "src/Edit.cs", "public class UpdatedType", 1, ParentId: null),
                new JulieDbFixture.SymbolRow("keep", "Anchor", "class", "csharp",
                    "src/Keep.cs", "public class Anchor", 1, ParentId: null),
            },
            revisions: new[]
            {
                new JulieDbFixture.RevisionRow(1),
                new JulieDbFixture.RevisionRow(2, Kind: "single_file"),
            },
            fileChanges: new[]
            {
                new JulieDbFixture.RevisionFileChangeRow(2, "src/Edit.cs", "updated"),
            });
        string searchDb = SymbolSearchSidecar.SearchDbPathFor(julie.DbPath);
        SearchIndexWriter.Write(searchDb, new[]
        {
            new IndexedSymbol(0, "edit-old", "LegacyWidget", "public class LegacyWidget", "class",
                "csharp", "src/Edit.cs", 1, 1, ParentId: null, IsTest: false),
            new IndexedSymbol(1, "keep", "Anchor", "public class Anchor", "class",
                "csharp", "src/Keep.cs", 1, 1, ParentId: null, IsTest: false),
        }, revision: 1, symbolsDbPath: julie.DbPath, workspaceRoot: julie.WorkspaceRoot,
            regionOptions: RegionIndexOptions.EnabledDefault);
        CreateSentinelTable(searchDb);
        var sidecar = new SymbolSearchSidecar(enabled: true);
        var service = NewSeededService(WorkspaceWithDb(julie.DbPath), sidecar);
        service.PublishOpsForTest(new RecordingScanOps { UpdateRevision = 2 });

        Assert.True(service.TryReindexAsLeader("src/Edit.cs"));

        Assert.True(TableExists(searchDb, "incremental_sentinel"));
        FtsSymbolSearchIndex index = Assert.IsType<FtsSymbolSearchIndex>(
            sidecar.TryOpen(julie.DbPath, expectedRevision: 2));
        Assert.Empty(index.Search("LegacyWidget", limit: 10));
        Assert.Equal("UpdatedType", index.Resolve(Assert.Single(index.Search("UpdatedType", limit: 10)).Document.DocId).Name);
        Assert.Equal("Anchor", index.Resolve(Assert.Single(index.Search("Anchor", limit: 10)).Document.DocId).Name);
    }

    // Empty an FTS5 shadow table so the artifact's meta still reads fine (revision intact) but any later FTS
    // write fails with SQLITE_CORRUPT ("database disk image is malformed") — the one corruption shape the
    // incremental converge path would otherwise retry into forever (M5).
    private static void CorruptFtsShadowData(string searchDb)
    {
        using (var rw = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = searchDb, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
        }.ToString()))
        {
            rw.Open();
            using var cmd = rw.CreateCommand();
            cmd.CommandText = "DELETE FROM symbols_fts_data;";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
    }

    private static void DropTrigramTable(string searchDb)
    {
        using (var rw = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = searchDb, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
        }.ToString()))
        {
            rw.Open();
            using var cmd = rw.CreateCommand();
            cmd.CommandText = "DROP TABLE symbols_trigram;";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
    }

    private static JulieDbFixture SingleFileUpdateFixture() => JulieDbFixture.Create(
        JulieDbFixture.PinnedSchema,
        JulieDbFixture.PinnedContract,
        new[]
        {
            new JulieDbFixture.SymbolRow("edit-new", "UpdatedType", "class", "csharp",
                "src/Edit.cs", "public class UpdatedType", 1, ParentId: null),
            new JulieDbFixture.SymbolRow("keep", "Anchor", "class", "csharp",
                "src/Keep.cs", "public class Anchor", 1, ParentId: null),
        },
        revisions: new[]
        {
            new JulieDbFixture.RevisionRow(1),
            new JulieDbFixture.RevisionRow(2, Kind: "single_file"),
        },
        fileChanges: new[]
        {
            new JulieDbFixture.RevisionFileChangeRow(2, "src/Edit.cs", "updated"),
        });

    private static void WriteRevisionOneArtifact(JulieDbFixture fixture, string searchDb) =>
        SearchIndexWriter.Write(searchDb, new[]
        {
            new IndexedSymbol(0, "edit-old", "LegacyWidget", "public class LegacyWidget", "class",
                "csharp", "src/Edit.cs", 1, 1, ParentId: null, IsTest: false),
            new IndexedSymbol(1, "keep", "Anchor", "public class Anchor", "class",
                "csharp", "src/Keep.cs", 1, 1, ParentId: null, IsTest: false),
        }, revision: 1, symbolsDbPath: fixture.DbPath, workspaceRoot: fixture.WorkspaceRoot,
            regionOptions: RegionIndexOptions.EnabledDefault);

    [Fact]
    public void TryReindexAsLeader_CorruptSearchSidecar_IsDeletedAndRebuilt_AndReaderCanOpenIt()
    {
        using var julie = SingleFileUpdateFixture();
        string searchDb = SymbolSearchSidecar.SearchDbPathFor(julie.DbPath);
        WriteRevisionOneArtifact(julie, searchDb);
        CorruptFtsShadowData(searchDb); // meta intact at revision 1; the FTS body is corrupt
        var sidecar = new SymbolSearchSidecar(enabled: true);
        var service = NewSeededService(WorkspaceWithDb(julie.DbPath), sidecar);
        service.PublishOpsForTest(new RecordingScanOps { UpdateRevision = 2 });

        Assert.True(service.TryReindexAsLeader("src/Edit.cs"));

        // M5: the incremental converge hit SQLITE_CORRUPT; the escalation deleted the derived artifact and
        // rebuilt it from scratch within the SAME converge call, so a reader's next open succeeds — instead of
        // every converge warning forever while every reader gets the stale-sidecar error.
        FtsSymbolSearchIndex? index = sidecar.TryOpen(julie.DbPath, expectedRevision: 2);
        Assert.NotNull(index);
        Assert.Equal(
            "UpdatedType",
            index!.Resolve(Assert.Single(index.Search("UpdatedType", limit: 10)).Document.DocId).Name);
    }

    [Fact]
    public void TryReindexAsLeader_NonCorruptionSidecarFailure_DoesNotDeleteOrRebuild()
    {
        using var julie = SingleFileUpdateFixture();
        string searchDb = SymbolSearchSidecar.SearchDbPathFor(julie.DbPath);
        WriteRevisionOneArtifact(julie, searchDb);
        // A converge failure that is NOT corruption-shaped ("no such table", SQLITE_ERROR): the artifact file
        // must be left alone — delete/rebuild is reserved for corruption, everything else keeps warn-and-retry.
        DropTrigramTable(searchDb);
        var sidecar = new SymbolSearchSidecar(enabled: true);
        var service = NewSeededService(WorkspaceWithDb(julie.DbPath), sidecar);
        service.PublishOpsForTest(new RecordingScanOps { UpdateRevision = 2 });

        Assert.True(service.TryReindexAsLeader("src/Edit.cs")); // converge failures never fail the reindex

        Assert.True(File.Exists(searchDb));
        // No rebuild happened: a from-scratch rebuild would have recreated symbols_trigram and stamped rev 2.
        Assert.False(TableExists(searchDb, "symbols_trigram"));
        Assert.Null(sidecar.TryOpen(julie.DbPath, expectedRevision: 2));
    }

    [Fact]
    public void TryReindexAsLeader_MarksRegistryAtUpdateRevision()
    {
        using var julie = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                new JulieDbFixture.SymbolRow("edit-new", "UpdatedType", "class", "csharp",
                    "src/Edit.cs", "public class UpdatedType", 1, ParentId: null),
            },
            revisions: new[]
            {
                new JulieDbFixture.RevisionRow(1),
                new JulieDbFixture.RevisionRow(2, Kind: "single_file"),
            },
            fileChanges: new[]
            {
                new JulieDbFixture.RevisionFileChangeRow(2, "src/Edit.cs", "updated"),
            });
        WorkspaceContext workspace = WorkspaceWithDb(julie.DbPath);
        string workspaceId = workspace.WorkspaceId!;
        IndexBootstrapService.RegisterBootstrapWorkspace(
            workspace, workspaceId, WorkspaceRegistryState.LoadedExisting, revision: 1);
        var service = NewSeededService(workspace, SymbolSearchSidecar.Disabled);
        service.PublishOpsForTest(new RecordingScanOps { UpdateRevision = 2 });

        Assert.True(service.TryReindexAsLeader("src/Edit.cs"));

        using var registry = WorkspaceRegistry.Open(workspace.RegistryDbPath);
        WorkspaceRegistryRow row = Assert.IsType<WorkspaceRegistryRow>(registry.Get(workspaceId));
        Assert.Equal(WorkspaceRegistryState.Ready, row.State);
        Assert.Equal(2, row.LastRevision);
    }

    [Fact]
    public void TryScanAsLeader_WhenSidecarDisabled_DoesNotBuildSearchSidecar()
    {
        using var julie = JulieDb();
        var service = NewSeededService(WorkspaceWithDb(julie.DbPath), SymbolSearchSidecar.Disabled);
        service.PublishOpsForTest(new RecordingScanOps { Revision = 9 });

        ScanOutcome outcome = service.TryScanAsLeader(ScanIntent.IncrementalReconcile, bypassBackoff: true);

        // OFF path is byte-identical to pre-feature behavior: a successful scan and NO derived artifact.
        Assert.Equal(ScanOutcome.Kind.Scanned, outcome.Result);
        Assert.False(File.Exists(SymbolSearchSidecar.SearchDbPathFor(julie.DbPath)));
    }

    [Fact]
    public void TryScanAsLeader_WhenEnabledButSidecarSourceUnreadable_StillReportsScanned()
    {
        string dir = Path.Combine(Path.GetTempPath(), "miller-indexer-sidecar-fail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // A symbols.db the sidecar build cannot read (not a SQLite file). The scan itself is faked + succeeds.
            string brokenDb = Path.Combine(dir, "symbols.db");
            File.WriteAllText(brokenDb, "this is not a sqlite database");
            var service = NewSeededService(WorkspaceWithDb(brokenDb), new SymbolSearchSidecar(enabled: true));
            service.PublishOpsForTest(new RecordingScanOps { Revision = 9 });

            ScanOutcome outcome = service.TryScanAsLeader(ScanIntent.IncrementalReconcile, bypassBackoff: true);

            // Best-effort: a sidecar build failure NEVER turns a successful scan into a failure (reads self-heal).
            Assert.Equal(ScanOutcome.Kind.Scanned, outcome.Result);
            Assert.False(File.Exists(SymbolSearchSidecar.SearchDbPathFor(brokenDb)));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task StartAsync_WhenEnabledLeader_BuildsSearchSidecarAfterStartupScan()
    {
        using var julie = JulieDb();
        var lease = new TestLease();
        var ops = new RecordingScanOps { Revision = 13 };
        var sidecar = new SymbolSearchSidecar(enabled: true);
        var service = NewStartedService(WorkspaceWithDb(julie.DbPath), _ => lease, (_, _, _) => ops, sidecar);

        await service.StartAsync(CancellationToken.None);
        Assert.True(ops.ScanCalled.Wait(ScanSignalTimeoutMs, CancellationToken.None));
        await service.StopAsync(CancellationToken.None); // awaits ExecuteAsync, so RunStartupDeltaScan has finished

        string searchDb = SymbolSearchSidecar.SearchDbPathFor(julie.DbPath);
        Assert.True(File.Exists(searchDb), $"expected the startup delta scan to build {searchDb}");
        Assert.NotNull(sidecar.TryOpen(julie.DbPath, expectedRevision: 13));
    }

    [Fact]
    public async Task StartAsync_WhenEnabledLeaderAndSidecarBuildFails_StillMarksRegistryScanned()
    {
        string dir = Path.Combine(
            Path.GetTempPath(), "miller-indexer-sidecar-startup-fail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var lease = new TestLease();
        var ops = new RecordingScanOps { Revision = 11 };
        try
        {
            // A symbols.db the sidecar build cannot read; the faked startup delta scan still succeeds at rev 11.
            // The build call sits INSIDE RunStartupDeltaScan's outer try whose catch calls MarkRegistryError, so
            // this pins that a build failure is fully absorbed and never poisons the registry as an errored scan.
            string brokenDb = Path.Combine(dir, "symbols.db");
            File.WriteAllText(brokenDb, "this is not a sqlite database");
            var workspace = WorkspaceWithDb(brokenDb);
            string workspaceId = workspace.WorkspaceId!;
            IndexBootstrapService.RegisterBootstrapWorkspace(
                workspace, workspaceId, WorkspaceRegistryState.LoadedExisting, revision: 4);
            var service = NewStartedService(
                workspace, _ => lease, (_, _, _) => ops, new SymbolSearchSidecar(enabled: true));

            await service.StartAsync(CancellationToken.None);
            Assert.True(ops.ScanCalled.Wait(ScanSignalTimeoutMs, CancellationToken.None));
            await service.StopAsync(CancellationToken.None); // awaits ExecuteAsync ⇒ RunStartupDeltaScan finished

            using var registry = WorkspaceRegistry.Open(workspace.RegistryDbPath);
            var row = registry.Get(workspaceId);
            Assert.NotNull(row);
            Assert.Equal(WorkspaceRegistryState.Ready, row!.State); // Scanned, NOT errored by the failed build
            Assert.Equal(11, row.LastRevision);
            Assert.False(File.Exists(SymbolSearchSidecar.SearchDbPathFor(brokenDb)));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task StartAsync_WhenNotLeader_DoesNotCreateOpsOrRunStartupScan()
    {
        string dir = Path.Combine(Path.GetTempPath(), "miller-indexer-startup-reader-" + Guid.NewGuid().ToString("N"));
        var acquireAttempted = new ManualResetEventSlim(false);
        int factoryCalls = 0;
        try
        {
            var workspace = CreateWorkspace(dir);
            var service = NewStartedService(
                workspace,
                _ =>
                {
                    acquireAttempted.Set();
                    return null;
                },
                (_, _, _) =>
                {
                    Interlocked.Increment(ref factoryCalls);
                    return new RecordingScanOps();
                });

            await service.StartAsync(CancellationToken.None);
            Assert.True(acquireAttempted.Wait(ScanSignalTimeoutMs, CancellationToken.None));
            await service.StopAsync(CancellationToken.None);

            Assert.Equal(0, Volatile.Read(ref factoryCalls));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    // ---- W3 F10: the production admission expression, driven through a real drain tick ----
    // Every wholeRepoScanAdmitted assertion used to live in IndexerCoreTests with the boolean supplied by hand,
    // so the expression that COMPUTES it in IndexerService was guarded by nothing. These drive RunDrainTick.

    private static IndexerService NewGovernedDrainService(string home, string dbPath) =>
        NewSeededService(
            WorkspaceWithDb(dbPath),
            SymbolSearchSidecar.Disabled,
            drainFileConvergeRequests: _ => FileConvergeDrainResult.Empty,
            scanGovernor: ScanGovernor.ForMillerHome(home),
            scanGovernorWait: TimeSpan.Zero,
            drainFullScanRequests: _ => FullScanDrainResult.Empty);

    [Fact]
    public void DrainTick_WithASignalledRescan_AndTheGovernorHeldElsewhere_RunsNoExtract_ButStillAppliesEdits()
    {
        using var julie = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, System.Array.Empty<JulieDbFixture.SymbolRow>());
        string home = CreateTempHome();
        var service = NewGovernedDrainService(home, julie.DbPath);
        var ops = new RecordingScanOps();
        service.PublishOpsForTest(ops);
        service.RequestWholeRepoScanForTest(ScanIntent.IncrementalReconcile);
        service.EnqueueForTest(new WatchEvent("/repo/a.cs", WatchEventKind.Modified));

        using (ScanGovernorLease held = HoldMachineScanAdmission(home))
            service.RunDrainTickForTest(Path.Combine(home, "requests"));

        Assert.Empty(ops.ScanForce);
        Assert.Equal(new[] { "/repo/a.cs" }, ops.UpdatePaths);

        service.RunDrainTickForTest(Path.Combine(home, "requests"));

        Assert.Equal(new[] { false }, ops.ScanForce);
    }

    [Fact]
    public void DrainTick_WithACheckoutSizedBatch_AndTheGovernorHeldElsewhere_RunsNoExtractAtAll()
    {
        using var julie = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, System.Array.Empty<JulieDbFixture.SymbolRow>());
        string home = CreateTempHome();
        var service = NewGovernedDrainService(home, julie.DbPath);
        var ops = new RecordingScanOps();
        service.PublishOpsForTest(ops);
        service.RequestWholeRepoScanForTest(ScanIntent.IncrementalReconcile);
        for (int i = 0; i <= IndexerCore.MaxDeferredScanDrain; i++)
            service.EnqueueForTest(new WatchEvent($"/repo/{i}.cs", WatchEventKind.Modified));

        using (ScanGovernorLease held = HoldMachineScanAdmission(home))
            service.RunDrainTickForTest(Path.Combine(home, "requests"));

        Assert.Empty(ops.ScanForce);
        Assert.Empty(ops.UpdatePaths);

        service.RunDrainTickForTest(Path.Combine(home, "requests"));

        Assert.Equal(new[] { false }, ops.ScanForce);
        Assert.Empty(ops.UpdatePaths);
    }

    [Fact]
    public void DrainTick_WhenARescanArrivesAfterTheAdmissionPeek_RefusesRatherThanScanningUngoverned()
    {
        using var julie = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, System.Array.Empty<JulieDbFixture.SymbolRow>());
        string home = CreateTempHome();
        var service = NewGovernedDrainService(home, julie.DbPath);
        var ops = new RecordingScanOps();
        service.PublishOpsForTest(ops);
        service.BetweenScanPeekAndDrainForTest = () => service.RequestWholeRepoScanForTest(ScanIntent.IncrementalReconcile);

        using (ScanGovernorLease held = HoldMachineScanAdmission(home))
            service.RunDrainTickForTest(Path.Combine(home, "requests"));

        Assert.Empty(ops.ScanForce);

        service.BetweenScanPeekAndDrainForTest = null;
        service.RunDrainTickForTest(Path.Combine(home, "requests"));

        Assert.Equal(new[] { false }, ops.ScanForce);
    }

    [Fact]
    public void RunStartupDeltaScan_WhenMachineScanAdmissionIsBusy_DoesNotScan_AndRearmsTheLatch()
    {
        using var julie = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, System.Array.Empty<JulieDbFixture.SymbolRow>());
        string home = CreateTempHome();
        WorkspaceContext workspace = WorkspaceWithDb(julie.DbPath);
        var service = NewGovernedDrainService(home, julie.DbPath);
        var ops = new RecordingScanOps();
        service.PublishOpsForTest(ops);

        using (ScanGovernorLease held = HoldMachineScanAdmission(home))
            service.RunStartupDeltaScanForTest(workspace);

        Assert.Empty(ops.ScanForce);

        service.RunDrainTickForTest(Path.Combine(home, "requests"));

        Assert.Equal(new[] { false }, ops.ScanForce);
    }

    [Fact]
    public void ExtractorUpgradeRescan_WhenMachineScanAdmissionIsBusy_RearmsAForcedScan()
    {
        using var julie = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, System.Array.Empty<JulieDbFixture.SymbolRow>());
        string home = CreateTempHome();
        var service = NewSeededService(
            WorkspaceWithDb(julie.DbPath),
            SymbolSearchSidecar.Disabled,
            drainFileConvergeRequests: _ => FileConvergeDrainResult.Empty,
            scanGovernor: ScanGovernor.ForMillerHome(home),
            scanGovernorWait: TimeSpan.Zero,
            drainFullScanRequests: _ => FullScanDrainResult.Empty,
            ownExtractorVersion: () => "2.21.0");
        var ops = new RecordingScanOps();
        service.PublishOpsForTest(ops);

        using (ScanGovernorLease held = HoldMachineScanAdmission(home))
            service.RunExtractorUpgradeRescanForTest();

        Assert.Empty(ops.ScanForce);

        service.RunDrainTickForTest(Path.Combine(home, "requests"));

        Assert.Equal(new[] { true }, ops.ScanForce);
    }

    [Fact]
    public void RunStartupDeltaScan_WhenTheScanFails_RearmsTheLatchRatherThanLosingTheReconcile()
    {
        using var julie = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, System.Array.Empty<JulieDbFixture.SymbolRow>());
        string home = CreateTempHome();
        WorkspaceContext workspace = WorkspaceWithDb(julie.DbPath);
        var service = NewGovernedDrainService(home, julie.DbPath);
        DateTimeOffset now = new(2026, 8, 2, 0, 0, 0, TimeSpan.Zero);
        service.PublishFailurePolicyForTest(
            new InMemoryScanFailurePolicy(utcNow: () => now, jitter: static () => 0));
        var ops = new RecordingScanOps { ThrowOnScan = new JulieExtractException("crashed (exit 137)", "") };
        service.PublishOpsForTest(ops);

        service.RunStartupDeltaScanForTest(workspace);

        Assert.Equal(new[] { false }, ops.ScanForce);
        Assert.False(service.QueueEmpty);

        ops.ThrowOnScan = null;
        service.RunDrainTickForTest(Path.Combine(home, "requests"));

        Assert.Equal(new[] { false }, ops.ScanForce);
        Assert.False(service.QueueEmpty);

        now += ScanFailurePolicy.FirstBackoff;
        service.RunDrainTickForTest(Path.Combine(home, "requests"));

        Assert.Equal(new[] { false, false }, ops.ScanForce);
        Assert.True(service.QueueEmpty);
    }

    // ---- W3 G2: out-of-band scans must report completion, intent-aware ----
    // Every whole-repo scan this service runs itself bypasses IndexerCore's drain, so without a completion signal
    // the latch that would have run it survives and the same tick rebuilds the repo a second time. The signal has
    // to carry INTENT: a delta that satisfies a pending FORCED request would drop the from-scratch rebuild.

    [Fact]
    public void LeaderRequestedFullScan_ThatSucceeds_ClearsAPendingForcedLatch_SoTheTickDoesNotRebuildTwice()
    {
        using var julie = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, System.Array.Empty<JulieDbFixture.SymbolRow>());
        string home = CreateTempHome();
        var service = NewSeededService(
            WorkspaceWithDb(julie.DbPath),
            SymbolSearchSidecar.Disabled,
            drainFileConvergeRequests: _ => FileConvergeDrainResult.Empty,
            scanGovernor: ScanGovernor.ForMillerHome(home),
            scanGovernorWait: TimeSpan.Zero,
            drainFullScanRequests: _ => new FullScanDrainResult(true, 0, 0));
        var ops = new RecordingScanOps();
        service.PublishOpsForTest(ops);
        service.RequestWholeRepoScanForTest(ScanIntent.UserFullRebuild);

        service.RunDrainTickForTest(Path.Combine(home, "requests"));

        Assert.Equal(new[] { true }, ops.ScanForce);
        Assert.True(service.QueueEmpty);
    }

    [Fact]
    public void StartupDeltaScan_ThatSucceeds_NeverSatisfiesAPendingForcedRebuild()
    {
        using var julie = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, System.Array.Empty<JulieDbFixture.SymbolRow>());
        string home = CreateTempHome();
        WorkspaceContext workspace = WorkspaceWithDb(julie.DbPath);
        var service = NewGovernedDrainService(home, julie.DbPath);
        var ops = new RecordingScanOps();
        service.PublishOpsForTest(ops);
        service.RequestWholeRepoScanForTest(ScanIntent.UserFullRebuild);

        service.RunStartupDeltaScanForTest(workspace);

        Assert.Equal(new[] { false }, ops.ScanForce);
        Assert.False(service.QueueEmpty);

        service.RunDrainTickForTest(Path.Combine(home, "requests"));

        Assert.Equal(new[] { false, true }, ops.ScanForce);
        Assert.True(service.QueueEmpty);
    }

    [Fact]
    public void OnDemandScan_ThatSucceeds_ClearsTheLatchItWouldOtherwiseLeaveArmed()
    {
        using var julie = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, System.Array.Empty<JulieDbFixture.SymbolRow>());
        string home = CreateTempHome();
        var service = NewGovernedDrainService(home, julie.DbPath);
        var ops = new RecordingScanOps();
        service.PublishOpsForTest(ops);
        service.RequestWholeRepoScanForTest(ScanIntent.UserFullRebuild);

        Assert.Equal(ScanOutcome.Kind.Scanned, service.TryScanAsLeader(ScanIntent.UserFullRebuild, bypassBackoff: true).Result);

        Assert.True(service.QueueEmpty);

        service.RunDrainTickForTest(Path.Combine(home, "requests"));

        Assert.Equal(new[] { true }, ops.ScanForce);
    }

    // A scan cannot service a request that did not exist when it started. TryScanAsLeader's refusal path arms the
    // latch without _opsGate, so an MCP caller told "queued" mid-rebuild must still get its rebuild.
    [Fact]
    public void OnDemandScan_WithAForcedRequestArmedMidScan_StillRunsThatRequestOnTheNextTick()
    {
        using var julie = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, System.Array.Empty<JulieDbFixture.SymbolRow>());
        string home = CreateTempHome();
        var service = NewGovernedDrainService(home, julie.DbPath);
        var ops = new RecordingScanOps();
        ops.WhileScanning = () =>
        {
            ops.WhileScanning = null;
            service.RequestWholeRepoScanForTest(ScanIntent.UserFullRebuild);
        };
        service.PublishOpsForTest(ops);

        Assert.Equal(ScanOutcome.Kind.Scanned, service.TryScanAsLeader(ScanIntent.UserFullRebuild, bypassBackoff: true).Result);
        Assert.False(service.QueueEmpty);

        service.RunDrainTickForTest(Path.Combine(home, "requests"));

        Assert.Equal(new[] { true, true }, ops.ScanForce);
        Assert.True(service.QueueEmpty);
    }

    // ---- W3 G7: admission state is published under a key readers look up, or not at all ----

    [Fact]
    public void ScanAdmission_PublishesThisProcessesPosition_UnderTheCanonicalRoot()
    {
        using var julie = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, System.Array.Empty<JulieDbFixture.SymbolRow>());
        string home = CreateTempHome();
        WorkspaceContext workspace = WorkspaceWithDb(julie.DbPath);
        var service = NewGovernedDrainService(home, julie.DbPath);
        ScanGovernorSnapshot? seen = null;
        var ops = new RecordingScanOps
        {
            WhileScanning = () => seen = ScanGovernorState.Shared.Snapshot(workspace.CanonicalRoot!),
        };
        service.PublishOpsForTest(ops);

        service.TryScanAsLeader(ScanIntent.IncrementalReconcile, bypassBackoff: true);

        Assert.Equal(ScanGovernorStates.Holding, seen?.State);
        Assert.Null(ScanGovernorState.Shared.Snapshot(workspace.CanonicalRoot!));
    }

    [Fact]
    public void ScanAdmission_WhenNoWorkspaceRootIsKnown_PublishesNothingUnderAnInventedKey()
    {
        string home = CreateTempHome();
        var service = NewService(
            scanGovernor: ScanGovernor.ForMillerHome(home), scanGovernorWait: TimeSpan.Zero);
        ScanGovernorOwner? owner = null;
        var ops = new RecordingScanOps
        {
            WhileScanning = () => owner = ScanGovernor.ForMillerHome(home).TryReadOwner(),
        };
        service.PublishOpsForTest(ops);

        service.TryScanAsLeader(ScanIntent.IncrementalReconcile, bypassBackoff: true);

        Assert.Equal(IndexerService.UnknownWorkspaceRootLabel, owner?.WorkspaceRoot);
        Assert.Null(ScanGovernorState.Shared.Snapshot(IndexerService.UnknownWorkspaceRootLabel));
    }

    // ---- W8 G2/G3: a downgrade is a third outcome, and a direct request is never downgraded ----

    [Fact]
    public void LeaderRequestedFullScan_ThatIsDowngraded_RearmsTheRebuildInsteadOfReportingItDone()
    {
        using var julie = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, System.Array.Empty<JulieDbFixture.SymbolRow>());
        string home = CreateTempHome();
        var service = NewSeededService(
            WorkspaceWithDb(julie.DbPath),
            SymbolSearchSidecar.Disabled,
            drainFileConvergeRequests: _ => FileConvergeDrainResult.Empty,
            scanGovernor: ScanGovernor.ForMillerHome(home),
            scanGovernorWait: TimeSpan.Zero,
            drainFullScanRequests: _ => new FullScanDrainResult(true, 0, 0));
        DateTimeOffset now = new(2026, 8, 2, 0, 0, 0, TimeSpan.Zero);
        var policy = new InMemoryScanFailurePolicy(
            priorArtifactUsable: static () => true, utcNow: () => now, jitter: static () => 0);
        policy.RecordFailure(ScanIntent.UserFullRebuild, ScanFailurePolicy.SigkillExitCode, jobs: 4);
        now += ScanFailurePolicy.FirstBackoff;
        service.PublishFailurePolicyForTest(policy);
        var ops = new RecordingScanOps();
        service.PublishOpsForTest(ops);

        service.RunDrainTickForTest(Path.Combine(home, "requests"));

        Assert.Equal(new[] { false }, ops.ScanForce);
        Assert.False(service.QueueEmpty);
        Assert.Equal(1, policy.Read()?.ConsecutiveFailures);

        now += ScanFailurePolicy.FirstBackoff;
        policy.RecordSuccess(ScanIntent.UserFullRebuild);
        service.RunDrainTickForTest(Path.Combine(home, "requests"));

        Assert.Equal(new[] { false, true }, ops.ScanForce);
        Assert.True(service.QueueEmpty);
    }

    [Fact]
    public void OnDemandFullScan_WithTheDirectUserBypass_RunsTheRealForceScanRatherThanADowngradedDelta()
    {
        using var julie = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, System.Array.Empty<JulieDbFixture.SymbolRow>());
        string home = CreateTempHome();
        var service = NewGovernedDrainService(home, julie.DbPath);
        DateTimeOffset now = new(2026, 8, 2, 0, 0, 0, TimeSpan.Zero);
        var policy = new InMemoryScanFailurePolicy(
            priorArtifactUsable: static () => true, utcNow: () => now, jitter: static () => 0);
        policy.RecordFailure(ScanIntent.UserFullRebuild, ScanFailurePolicy.SigkillExitCode, jobs: 4);
        service.PublishFailurePolicyForTest(policy);
        var ops = new RecordingScanOps();
        service.PublishOpsForTest(ops);

        ScanOutcome outcome = service.TryScanAsLeader(ScanIntent.UserFullRebuild, bypassBackoff: true);

        Assert.Equal(ScanOutcome.Kind.Scanned, outcome.Result);
        Assert.Equal(new[] { true }, ops.ScanForce);
        Assert.Null(policy.Read());
    }

    [Fact]
    public void OnDemandFullScan_OnTheAutomaticPath_ReportsTheDowngradeAndKeepsTheRebuildOwed()
    {
        using var julie = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, System.Array.Empty<JulieDbFixture.SymbolRow>());
        string home = CreateTempHome();
        var service = NewGovernedDrainService(home, julie.DbPath);
        DateTimeOffset now = new(2026, 8, 2, 0, 0, 0, TimeSpan.Zero);
        var policy = new InMemoryScanFailurePolicy(
            priorArtifactUsable: static () => true, utcNow: () => now, jitter: static () => 0);
        policy.RecordFailure(ScanIntent.UserFullRebuild, ScanFailurePolicy.SigkillExitCode, jobs: 4);
        now += ScanFailurePolicy.FirstBackoff;
        service.PublishFailurePolicyForTest(policy);
        var ops = new RecordingScanOps();
        service.PublishOpsForTest(ops);

        ScanOutcome outcome = service.TryScanAsLeader(ScanIntent.UserFullRebuild);

        Assert.Equal(ScanOutcome.Kind.Downgraded, outcome.Result);
        Assert.Equal(new[] { false }, ops.ScanForce);
        Assert.Contains("still owed", outcome.DowngradeReason, StringComparison.Ordinal);
        Assert.False(service.QueueEmpty);
        Assert.Equal(1, policy.Read()?.ConsecutiveFailures);
    }

    [Fact]
    public void ExtractorUpgradeRescan_WhenTheForcedScanFails_RearmsAForcedScanRatherThanLosingIt()
    {
        using var julie = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, System.Array.Empty<JulieDbFixture.SymbolRow>());
        string home = CreateTempHome();
        var service = NewSeededService(
            WorkspaceWithDb(julie.DbPath),
            SymbolSearchSidecar.Disabled,
            drainFileConvergeRequests: _ => FileConvergeDrainResult.Empty,
            scanGovernor: ScanGovernor.ForMillerHome(home),
            scanGovernorWait: TimeSpan.Zero,
            drainFullScanRequests: _ => FullScanDrainResult.Empty,
            ownExtractorVersion: () => "2.21.0");
        DateTimeOffset now = new(2026, 8, 2, 0, 0, 0, TimeSpan.Zero);
        service.PublishFailurePolicyForTest(
            new InMemoryScanFailurePolicy(utcNow: () => now, jitter: static () => 0));
        var ops = new RecordingScanOps { ThrowOnScan = new JulieExtractException("crashed (exit 137)", "") };
        service.PublishOpsForTest(ops);

        service.RunExtractorUpgradeRescanForTest();

        Assert.Equal(new[] { true }, ops.ScanForce);
        Assert.False(service.QueueEmpty);

        ops.ThrowOnScan = null;
        now += ScanFailurePolicy.FirstBackoff;
        service.RunDrainTickForTest(Path.Combine(home, "requests"));

        Assert.Equal(new[] { true, true }, ops.ScanForce);
        Assert.Equal(
            new[] { ScanIntent.ExtractorUpgrade, ScanIntent.ExtractorUpgrade }, ops.ScanIntents);
        Assert.True(service.QueueEmpty);
    }
}
