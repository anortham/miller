using Microsoft.Extensions.Logging.Abstractions;
using Miller.Core.Freshness;
using Miller.Indexing;
using Miller.Server;
using Miller.Server.Hosting;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Drives the production debounce tick over a real linked-worktree layout on disk: the leader must stop scanning
/// and mark the workspace missing when its root is deleted, reconcile when the SAME checkout returns, and refuse
/// to keep serving when a different checkout is created at the same path. Nothing here mimics the tick — it calls
/// the same body the debounce loop calls, through the real <see cref="WorkspaceRootPresenceMonitor"/> and the real
/// <see cref="WorkspaceRootIdentity"/> reads.
/// </summary>
public sealed class IndexerServiceRootPresenceTests : IDisposable
{
    private const string WorkspaceIdForTest = "0123456789abcdef0123456789abcdef";

    private readonly string _temp = Path.Combine(
        Path.GetTempPath(), "miller-root-presence-" + Guid.NewGuid().ToString("N"));

    public IndexerServiceRootPresenceTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public void ADisappearedRootSuspendsScanningAndMarksTheWorkspaceMissing()
    {
        string worktree = LinkedWorktree(AdminDir("wt"));
        var (service, ops, workspace) = NewLeader(worktree);
        service.SetRootPresenceMonitorForTest(new WorkspaceRootPresenceMonitor(worktree));
        service.RequestWholeRepoScanForTest(ScanIntent.IncrementalReconcile);

        Directory.Delete(worktree, recursive: true);

        Assert.True(service.RunDrainTickForTest(MillerDir(worktree)));
        Assert.Equal(0, ops.ScanCount);
        Assert.Equal(WorkspaceRegistryState.Missing, RegistryState(workspace));
    }

    [Fact]
    public void TheSameCheckoutReturningReconcilesInsteadOfRebootstrapping()
    {
        string adminDir = AdminDir("feature");
        string worktree = LinkedWorktree(adminDir);
        var (service, ops, _) = NewLeader(worktree);
        service.SetRootPresenceMonitorForTest(new WorkspaceRootPresenceMonitor(worktree));

        Directory.Delete(worktree, recursive: true);
        service.RunDrainTickForTest(MillerDir(worktree));
        LinkedWorktree(adminDir);

        Assert.True(service.RunDrainTickForTest(MillerDir(worktree)));
        Assert.Equal(1, ops.ScanCount);
    }

    [Fact]
    public void AWorktreeRemovedAndReAddedAtTheSamePathEndsTheLeadershipSession()
    {
        string adminDir = AdminDir("wt");
        string worktree = LinkedWorktree(adminDir);
        var (service, ops, _) = NewLeader(worktree);
        service.SetRootPresenceMonitorForTest(new WorkspaceRootPresenceMonitor(worktree));

        Directory.Delete(worktree, recursive: true);
        Directory.Delete(adminDir, recursive: true);
        service.RunDrainTickForTest(MillerDir(worktree));
        LinkedWorktree(AdminDir("wt"));

        Assert.False(service.RunDrainTickForTest(MillerDir(worktree)));
        Assert.Equal(0, ops.ScanCount);
    }

    [Fact]
    public void APresentRootRunsTheOrdinaryTick()
    {
        string worktree = LinkedWorktree(AdminDir("wt"));
        var (service, ops, _) = NewLeader(worktree);
        service.SetRootPresenceMonitorForTest(new WorkspaceRootPresenceMonitor(worktree));
        service.RequestWholeRepoScanForTest(ScanIntent.IncrementalReconcile);

        Assert.True(service.RunDrainTickForTest(MillerDir(worktree)));
        Assert.Equal(1, ops.ScanCount);
    }

    private string AdminDir(string name)
    {
        string adminDir = Path.Combine(_temp, "repo", ".git", "worktrees", name);
        Directory.CreateDirectory(adminDir);
        File.WriteAllText(Path.Combine(adminDir, "commondir"), "../..\n");
        return Path.GetFullPath(adminDir);
    }

    private string LinkedWorktree(string adminDir)
    {
        string worktree = Path.Combine(_temp, "wt");
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {adminDir}\n");
        return Path.GetFullPath(worktree);
    }

    private static string MillerDir(string root) => Path.Combine(root, ".miller");

    private (IndexerService Service, RecordingOps Ops, WorkspaceContext Workspace) NewLeader(string root)
    {
        var workspace = new WorkspaceContext(
            root,
            Path.Combine(root, ".miller", "symbols.db"),
            Path.Combine(_temp, "telemetry.db"),
            Path.Combine(_temp, "workspaces.db"),
            Path.Combine(_temp, ".tools"),
            WorkspaceId: WorkspaceIdForTest,
            CanonicalRoot: root,
            CanonicalExtractDbPath: Path.Combine(root, ".miller", "symbols.db"));
        var bootstrap = new IndexBootstrapService(NullLogger<IndexBootstrapService>.Instance)
        {
            TestHomeDirectoryOverride = _temp,
        };
        bootstrap.SeedForTest(
            workspace,
            new IndexHolder(MillerRepositoryIndex.Build(Array.Empty<IndexedSymbol>()), builtRevision: 0));

        var service = new IndexerService(
            bootstrap,
            NullLogger<IndexerService>.Instance,
            NullLoggerFactory.Instance,
            tryAcquireLeadership: _ => null,
            createOps: static (_, _, _) => throw new InvalidOperationException("not used by this test seam"),
            leaderRetryInterval: TimeSpan.FromHours(1),
            SymbolSearchSidecar.Disabled,
            attachFileWatchers: false,
            ownExtractorVersion: static () => MillerExtractContract.PinnedJulieExtractVersion);
        var ops = new RecordingOps();
        service.PublishOpsForTest(ops);
        return (service, ops, workspace);
    }

    private static WorkspaceRegistryState? RegistryState(WorkspaceContext workspace)
    {
        using var registry = WorkspaceRegistry.Open(workspace.RegistryDbPath);
        return registry.Get(workspace.WorkspaceId!)?.State;
    }

    private sealed class RecordingOps : IExtractOps
    {
        public int ScanCount { get; private set; }

        public ExtractReport Update(string path) => Stub();

        public ExtractReport Delete(string path) => Stub();

        public ExtractReport Scan(ScanIntent intent = ScanIntent.IncrementalReconcile, int? jobs = null)
        {
            ScanCount++;
            return Stub();
        }

        private static ExtractReport Stub() => new(
            ReportSchemaVersion: 1, Status: "ok", Operation: "scan", Mode: "incremental", Input: null,
            Artifact: new ExtractArtifact(
                DbPath: "x", RootPath: "/abs/r", ArtifactId: "a",
                SchemaVersion: MillerExtractContract.ExpectedSchemaVersion,
                ExtractContractVersion: MillerExtractContract.ExpectedExtractContractVersion,
                SqliteSchemaVersion: MillerExtractContract.ExpectedSqliteSchemaVersion,
                JsonlSchemaVersion: 1, HashAlgorithm: MillerExtractContract.ExpectedHashAlgorithm,
                ParserInventoryFingerprint: "p", CapabilitySnapshotFingerprint: "c"),
            Tool: new ExtractTool("julie-extract", "2.0.0"),
            RevisionBlock: new ExtractRevision(1, 1),
            Counts: null,
            Errors: Array.Empty<ReportDiagnostic>(), Warnings: Array.Empty<ReportDiagnostic>());
    }
}
