using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Miller.Core.Freshness;
using Miller.Indexing;
using Miller.Indexing.Store;
using Miller.Server;
using Miller.Server.Hosting;
using Miller.Server.Workspaces;
using Miller.Tests.Support;
using Xunit;

namespace Miller.Tests.Server;

[Collection(StoreEnvironmentCollection.Name)]
public sealed class IndexerServiceWalTests : IDisposable
{
    private readonly string? _original = Environment.GetEnvironmentVariable("MILLER_INDEX_STORE");
    public IndexerServiceWalTests() => Environment.SetEnvironmentVariable("MILLER_INDEX_STORE", null);
    public void Dispose() => Environment.SetEnvironmentVariable("MILLER_INDEX_STORE", _original);

    [Fact]
    public void IdleWalMaintenanceDiscoversDebtAndThrottlesEmptyChecks()
    {
        using StoreFixture fixture = StoreFixture.Create();
        string root = fixture.Binding.WorkspaceRoot;
        StoreWorkspacePointer.Write(root, fixture.Binding);
        var workspace = WorkspaceContext.Create(root, AppContext.BaseDirectory, Path.Combine(fixture.Root, "home")) with
        {
            CanonicalRoot = root,
            WorkspaceId = WorkspaceId.FromCanonicalRoot(root),
            CanonicalExtractDbPath = Path.Combine(root, ".miller", "symbols.db"),
        };
        var phases = new RecordingPhaseSink();
        var bootstrap = new IndexBootstrapService(NullLogger<IndexBootstrapService>.Instance);
        bootstrap.TestHomeDirectoryOverride = Path.Combine(fixture.Root, "home");
        bootstrap.SeedForTest(workspace, new IndexHolder(MillerRepositoryIndex.Build([]), builtRevision: 0));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using var service = new IndexerService(bootstrap, NullLogger<IndexerService>.Instance, NullLoggerFactory.Instance,
            tryAcquireLeadership: _ => null,
            createOps: (_, _, _) => throw new InvalidOperationException("not used"),
            leaderRetryInterval: TimeSpan.FromHours(1), sidecar: SymbolSearchSidecar.Disabled,
            attachFileWatchers: false,
            drainFileConvergeRequests: _ => FileConvergeDrainResult.Empty,
            drainFullScanRequests: _ => FullScanDrainResult.Empty,
            phaseSink: phases, clock: () => now);
        service.PublishOpsForTest(new NoWorkOps());
        string database = Path.Combine(fixture.Binding.StoreRoot, "gen-001", "store.db");
        using var writer = new SqliteConnection($"Data Source={database};Pooling=False");
        writer.Open();
        using var write = writer.CreateCommand();
        write.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE idle_wal(x); INSERT INTO idle_wal VALUES (1);";
        write.ExecuteNonQuery();
        string millerDir = Path.Combine(root, ".miller");
        service.RunDrainTickForTest(millerDir);
        Assert.Equal(0, new FileInfo(database + "-wal").Length);
        Assert.Single(phases.Records, p => p.Phase == "wal_checkpoint");
        write.CommandText = "INSERT INTO idle_wal VALUES (2);";
        write.ExecuteNonQuery();
        now = now.AddSeconds(29);
        service.RunDrainTickForTest(millerDir);
        Assert.True(new FileInfo(database + "-wal").Length > 0);
        now = now.AddSeconds(2);
        service.RunDrainTickForTest(millerDir);
        Assert.Equal(0, new FileInfo(database + "-wal").Length);
    }

    private sealed class RecordingPhaseSink : IIndexerPhaseSink
    {
        public List<IndexerPhaseRecord> Records { get; } = [];
        public void Record(IndexerPhaseRecord record) => Records.Add(record);
    }

    private sealed class NoWorkOps : IExtractOps
    {
        public ExtractReport Scan(ScanIntent intent = ScanIntent.IncrementalReconcile, int? jobs = null) => throw new InvalidOperationException("No scan expected.");
        public ExtractReport Update(string filePath) => throw new InvalidOperationException("No update expected.");
        public ExtractReport Delete(string filePath) => throw new InvalidOperationException("No delete expected.");
    }
}
