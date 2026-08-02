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
/// Pins the production write-through's two convergence transports: the indexer LEADER reindexes inline
/// (<see cref="IndexerService.TryReindexAsLeader"/>), while a READER hands the leader a single-file converge
/// request file the leader's debounce tick drains (<see cref="LeaderScanRequestQueue"/>). The reader path is the
/// load-bearing one for real sessions — most MCP servers are readers (another process holds the writer lock), so
/// without the request transport both post-apply convergence and gate-time stale recovery would silently no-op.
/// Fast suite: faked ops, no subprocess, temp dirs only.
/// </summary>
public sealed class LeaderWriteThroughTests : IDisposable
{
    private readonly string _dir;

    public LeaderWriteThroughTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-write-through-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class RecordingOps : IExtractOps
    {
        public List<string> UpdatePaths { get; } = [];

        public ExtractReport Update(string path)
        {
            UpdatePaths.Add(path);
            return new ExtractReport(
                ReportSchemaVersion: 1, Status: "ok", Operation: "update", Mode: "single_file", Input: null,
                Artifact: null, Tool: new ExtractTool("julie-extract", "2.0.0"),
                RevisionBlock: new ExtractRevision(2, 2), Counts: null,
                Errors: Array.Empty<ReportDiagnostic>(), Warnings: Array.Empty<ReportDiagnostic>());
        }

        public ExtractReport Delete(string path) => throw new NotSupportedException("not exercised here");
        public ExtractReport Scan(ScanIntent intent = ScanIntent.IncrementalReconcile, int? jobs = null) =>
            throw new NotSupportedException("not exercised here");
    }

    private WorkspaceContext CreateWorkspace()
    {
        string root = Path.Combine(_dir, "repo");
        string home = Path.Combine(_dir, "home");
        Directory.CreateDirectory(root);
        string canonicalRoot = Path.GetFullPath(root);
        return WorkspaceContext.Create(root, AppContext.BaseDirectory, home) with
        {
            WorkspaceId = WorkspaceId.FromCanonicalRoot(canonicalRoot),
            CanonicalRoot = canonicalRoot,
            CanonicalExtractDbPath = Path.Combine(canonicalRoot, ".miller", "symbols.db"),
        };
    }

    private static (IndexerService indexer, IndexBootstrapService bootstrap) NewIndexer(WorkspaceContext workspace)
    {
        var bootstrap = new IndexBootstrapService(NullLogger<IndexBootstrapService>.Instance);
        bootstrap.TestHomeDirectoryOverride = Path.GetDirectoryName(Path.GetDirectoryName(workspace.RegistryDbPath));
        bootstrap.SeedForTest(
            workspace,
            new IndexHolder(MillerRepositoryIndex.Build(Array.Empty<IndexedSymbol>()), builtRevision: 0));
        var indexer = new IndexerService(
            bootstrap,
            NullLogger<IndexerService>.Instance,
            NullLoggerFactory.Instance,
            tryAcquireLeadership: _ => null,
            createOps: static (_, _, _) => throw new InvalidOperationException("not used by this test seam"),
            leaderRetryInterval: TimeSpan.FromHours(1),
            SymbolSearchSidecar.Disabled,
            attachFileWatchers: false);
        return (indexer, bootstrap);
    }

    private static string MillerDirOf(WorkspaceContext workspace) =>
        Path.GetDirectoryName(workspace.ExtractDbPath)!;

    private static void RecordLeader(WorkspaceContext workspace, int pid = 4242) =>
        LeaderIdentityFile.Write(MillerDirOf(workspace), new LeaderIdentity(
            pid, MillerVersion.Current, ProcessPath: null, DateTimeOffset.UtcNow));

    [Fact]
    public void TryRecoverStaleFile_AsReader_WithLiveLeader_WritesConvergeRequest_AndReportsRequested()
    {
        WorkspaceContext workspace = CreateWorkspace();
        var (indexer, bootstrap) = NewIndexer(workspace); // no ops published => a reader
        RecordLeader(workspace); // a live, converge-capable leader is recorded
        var wt = new LeaderWriteThrough(
            indexer, bootstrap, NullLogger<LeaderWriteThrough>.Instance, isLeaderAlive: _ => true);
        string changed = Path.Combine(workspace.CanonicalRoot!, "src", "A.cs");

        StaleRecoveryAttempt attempt = wt.TryRecoverStaleFile(changed);

        Assert.Equal(StaleRecoveryAttempt.Requested, attempt);
        Assert.Equal(
            new[] { changed },
            LeaderScanRequestQueue.DrainFileConvergeRequests(MillerDirOf(workspace)).Paths);
    }

    [Fact]
    public void TryRecoverStaleFile_AsReader_NoLeaderIdentity_ReportsNone_AndWritesNoRequest()
    {
        WorkspaceContext workspace = CreateWorkspace();
        var (indexer, bootstrap) = NewIndexer(workspace);
        // No leader.json: either no leader runs, or a pre-identity (and therefore pre-drain) build leads. A
        // Requested here would make the gate burn its whole recovery poll with zero chance of converging.
        var wt = new LeaderWriteThrough(
            indexer, bootstrap, NullLogger<LeaderWriteThrough>.Instance, isLeaderAlive: _ => true);
        string changed = Path.Combine(workspace.CanonicalRoot!, "src", "A.cs");

        StaleRecoveryAttempt attempt = wt.TryRecoverStaleFile(changed);

        Assert.Equal(StaleRecoveryAttempt.None, attempt);
        Assert.Empty(LeaderScanRequestQueue.DrainFileConvergeRequests(MillerDirOf(workspace)).Paths);
    }

    [Fact]
    public void TryRecoverStaleFile_AsReader_DeadLeader_ReportsNone_AndWritesNoRequest()
    {
        WorkspaceContext workspace = CreateWorkspace();
        var (indexer, bootstrap) = NewIndexer(workspace);
        RecordLeader(workspace); // recorded, but the probe says the process is gone (crashed leader)
        var wt = new LeaderWriteThrough(
            indexer, bootstrap, NullLogger<LeaderWriteThrough>.Instance, isLeaderAlive: _ => false);
        string changed = Path.Combine(workspace.CanonicalRoot!, "src", "A.cs");

        StaleRecoveryAttempt attempt = wt.TryRecoverStaleFile(changed);

        Assert.Equal(StaleRecoveryAttempt.None, attempt);
        Assert.Empty(LeaderScanRequestQueue.DrainFileConvergeRequests(MillerDirOf(workspace)).Paths);
    }

    [Fact]
    public void TryRecoverStaleFile_AsReader_LivenessProbeThrows_AssumesCapable_AndReportsRequested()
    {
        WorkspaceContext workspace = CreateWorkspace();
        var (indexer, bootstrap) = NewIndexer(workspace);
        RecordLeader(workspace);
        // Probe-unknown must collapse to "assume capable": a diagnostics hiccup never regresses the happy path.
        var wt = new LeaderWriteThrough(
            indexer, bootstrap, NullLogger<LeaderWriteThrough>.Instance,
            isLeaderAlive: _ => throw new InvalidOperationException("probe denied"));
        string changed = Path.Combine(workspace.CanonicalRoot!, "src", "A.cs");

        StaleRecoveryAttempt attempt = wt.TryRecoverStaleFile(changed);

        Assert.Equal(StaleRecoveryAttempt.Requested, attempt);
        Assert.Equal(
            new[] { changed },
            LeaderScanRequestQueue.DrainFileConvergeRequests(MillerDirOf(workspace)).Paths);
    }

    [Fact]
    public void Converge_AsReader_WritesConvergeRequestForEveryChangedFile()
    {
        WorkspaceContext workspace = CreateWorkspace();
        var (indexer, bootstrap) = NewIndexer(workspace);
        var wt = new LeaderWriteThrough(indexer, bootstrap, NullLogger<LeaderWriteThrough>.Instance);
        string a = Path.Combine(workspace.CanonicalRoot!, "src", "A.cs");
        string b = Path.Combine(workspace.CanonicalRoot!, "src", "B.cs");

        wt.Converge([a, b]);

        Assert.Equal(
            new[] { a, b },
            LeaderScanRequestQueue.DrainFileConvergeRequests(MillerDirOf(workspace)).Paths);
    }

    [Fact]
    public void TryRecoverStaleFile_AsLeader_ReindexesInline_AndReportsConverged()
    {
        WorkspaceContext workspace = CreateWorkspace();
        var (indexer, bootstrap) = NewIndexer(workspace);
        var ops = new RecordingOps();
        indexer.PublishOpsForTest(ops); // become the leader
        var wt = new LeaderWriteThrough(indexer, bootstrap, NullLogger<LeaderWriteThrough>.Instance);
        string changed = Path.Combine(workspace.CanonicalRoot!, "src", "A.cs");

        StaleRecoveryAttempt attempt = wt.TryRecoverStaleFile(changed);

        Assert.Equal(StaleRecoveryAttempt.Converged, attempt);
        Assert.Equal(new[] { changed }, ops.UpdatePaths);
        // No request file: the inline reindex already converged it.
        Assert.Empty(LeaderScanRequestQueue.DrainFileConvergeRequests(MillerDirOf(workspace)).Paths);
    }
}
