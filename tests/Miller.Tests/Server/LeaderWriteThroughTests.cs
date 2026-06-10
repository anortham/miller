using Microsoft.Extensions.Logging.Abstractions;
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
        public ExtractReport Scan(bool force = false) => throw new NotSupportedException("not exercised here");
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

    [Fact]
    public void TryRecoverStaleFile_AsReader_WritesConvergeRequest_AndReportsRequested()
    {
        WorkspaceContext workspace = CreateWorkspace();
        var (indexer, bootstrap) = NewIndexer(workspace); // no ops published => a reader
        var wt = new LeaderWriteThrough(indexer, bootstrap, NullLogger<LeaderWriteThrough>.Instance);
        string changed = Path.Combine(workspace.CanonicalRoot!, "src", "A.cs");

        StaleRecoveryAttempt attempt = wt.TryRecoverStaleFile(changed);

        Assert.Equal(StaleRecoveryAttempt.Requested, attempt);
        Assert.Equal(
            new[] { changed },
            LeaderScanRequestQueue.DrainFileConvergeRequests(MillerDirOf(workspace)));
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
            LeaderScanRequestQueue.DrainFileConvergeRequests(MillerDirOf(workspace)));
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
        Assert.Empty(LeaderScanRequestQueue.DrainFileConvergeRequests(MillerDirOf(workspace)));
    }
}
