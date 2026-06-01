using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Miller.Core.Freshness;
using Miller.Indexing;
using Miller.Server;
using Miller.Server.Hosting;
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
public sealed class IndexerServiceScanTests
{
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
        public Exception? ThrowOnScan { get; set; }

        public ExtractReport Update(string path) => throw new NotSupportedException("not exercised here");
        public ExtractReport Delete(string path) => throw new NotSupportedException("not exercised here");

        public ExtractReport Scan(bool force = false)
        {
            lock (_gate)
                _scanForce.Add(force);
            ScanCalled.Set();
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

    // A never-started IndexerService: TryScanAsLeader reads only the published _ops under _opsGate (it never
    // touches the bootstrap), so an un-started instance is the correct, I/O-free unit-test surface.
    private static IndexerService NewService() =>
        new(new IndexBootstrapService(NullLogger<IndexBootstrapService>.Instance),
            NullLogger<IndexerService>.Instance, NullLoggerFactory.Instance);

    private static IndexerService NewStartedService(
        WorkspaceContext workspace,
        Func<string, IDisposable?> tryAcquireLeadership,
        Func<WorkspaceContext, string, string, IExtractOps> createOps)
    {
        var bootstrap = new IndexBootstrapService(NullLogger<IndexBootstrapService>.Instance);
        bootstrap.SeedForTest(
            workspace,
            new IndexHolder(MillerRepositoryIndex.Build(System.Array.Empty<IndexedSymbol>()), builtRevision: 0));

        return new IndexerService(
            bootstrap,
            NullLogger<IndexerService>.Instance,
            NullLoggerFactory.Instance,
            tryAcquireLeadership,
            createOps,
            TimeSpan.FromHours(1));
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

        ScanOutcome outcome = service.TryScanAsLeader(force: false);

        Assert.Equal(ScanOutcome.Kind.NotLeader, outcome.Result);
        Assert.Null(outcome.Report); // a non-leader produced no extract report (it cannot write)
    }

    [Fact]
    public void TryScanAsLeader_WhenLeader_DeltaScan_RunsForceFalse_AndReportsScanned()
    {
        var service = NewService();
        var ops = new RecordingScanOps();
        service.PublishOpsForTest(ops); // become the leader (the production publish happens once leadership wins)

        ScanOutcome outcome = service.TryScanAsLeader(force: false);

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

        ScanOutcome outcome = service.TryScanAsLeader(force: true);

        Assert.Equal(ScanOutcome.Kind.Scanned, outcome.Result);
        Assert.Equal(new[] { true }, ops.ScanForce); // full = from-scratch rebuild (--force)
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
        ScanOutcome outcome = service.TryScanAsLeader(force: true);

        Assert.Equal(ScanOutcome.Kind.Failed, outcome.Result);
        Assert.Null(outcome.Report);
        Assert.Equal(new[] { true }, ops.ScanForce); // the scan WAS attempted (then threw)
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
            Assert.True(ops.ScanCalled.Wait(5000, CancellationToken.None));
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
            Assert.True(acquireAttempted.Wait(5000, CancellationToken.None));
            await service.StopAsync(CancellationToken.None);

            Assert.Equal(0, Volatile.Read(ref factoryCalls));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
