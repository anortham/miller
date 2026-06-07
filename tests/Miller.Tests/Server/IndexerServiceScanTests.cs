using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Miller.Core.Freshness;
using Miller.Indexing;
using Miller.Server;
using Miller.Server.Hosting;
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
        public long? UpdateRevision { get; set; }
        public Exception? ThrowOnScan { get; set; }
        public Exception? ThrowOnUpdate { get; set; }

        public ExtractReport Update(string path)
        {
            if (ThrowOnUpdate is not null)
                throw ThrowOnUpdate;
            return Stub(UpdateRevision ?? Revision);
        }

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
    // touches the bootstrap), so an un-started instance is the correct, I/O-free unit-test surface. The sidecar
    // defaults OFF, so the disabled (byte-identical) path is what these no-workspace tests exercise.
    private static IndexerService NewService() =>
        new(new IndexBootstrapService(NullLogger<IndexBootstrapService>.Instance),
            NullLogger<IndexerService>.Instance, NullLoggerFactory.Instance, SymbolSearchSidecar.Disabled);

    // A leader-capable instance whose bootstrap is SEEDED with a workspace (so TryScanAsLeader can read its
    // CanonicalExtractDbPath for the sidecar build) and whose sidecar gate is the caller's choice. Not started —
    // PublishOpsForTest makes it the leader without the cross-process lock or a subprocess.
    private static IndexerService NewSeededService(WorkspaceContext workspace, SymbolSearchSidecar sidecar)
    {
        var bootstrap = new IndexBootstrapService(NullLogger<IndexBootstrapService>.Instance);
        bootstrap.SeedForTest(
            workspace,
            new IndexHolder(MillerRepositoryIndex.Build(System.Array.Empty<IndexedSymbol>()), builtRevision: 0));
        return new IndexerService(
            bootstrap, NullLogger<IndexerService>.Instance, NullLoggerFactory.Instance, sidecar);
    }

    private static IndexerService NewStartedService(
        WorkspaceContext workspace,
        Func<string, IDisposable?> tryAcquireLeadership,
        Func<WorkspaceContext, string, string, IExtractOps> createOps,
        SymbolSearchSidecar? sidecar = null)
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
            TimeSpan.FromHours(1),
            sidecar ?? SymbolSearchSidecar.Disabled,
            attachFileWatchers: false);
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

    // ---- Phase 1: the leader builds the on-disk search.db sidecar after its scans (enabled), best-effort -----

    [Fact]
    public void TryScanAsLeader_WhenEnabledLeader_BuildsRevisionFreshSearchSidecar()
    {
        using var julie = JulieDb();
        var sidecar = new SymbolSearchSidecar(enabled: true);
        var service = NewSeededService(WorkspaceWithDb(julie.DbPath), sidecar);
        service.PublishOpsForTest(new RecordingScanOps { Revision = 9 });

        ScanOutcome outcome = service.TryScanAsLeader(force: false);

        Assert.Equal(ScanOutcome.Kind.Scanned, outcome.Result);
        string searchDb = SymbolSearchSidecar.SearchDbPathFor(julie.DbPath);
        Assert.True(File.Exists(searchDb), $"expected the leader to build {searchDb}");
        // The artifact is usable AND stamped with the scanned revision (the strict-equality routing contract).
        FtsSymbolSearchIndex? index = sidecar.TryOpen(julie.DbPath, expectedRevision: 9);
        Assert.NotNull(index);
        Assert.Equal(9L, index!.Revision);
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
        }, revision: 1);
        CreateSentinelTable(searchDb);
        var sidecar = new SymbolSearchSidecar(enabled: true);
        var service = NewSeededService(WorkspaceWithDb(julie.DbPath), sidecar);
        service.PublishOpsForTest(new RecordingScanOps { Revision = 2 });

        ScanOutcome outcome = service.TryScanAsLeader(force: false);

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
        }, revision: 1);
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

    [Fact]
    public void TryScanAsLeader_WhenSidecarDisabled_DoesNotBuildSearchSidecar()
    {
        using var julie = JulieDb();
        var service = NewSeededService(WorkspaceWithDb(julie.DbPath), SymbolSearchSidecar.Disabled);
        service.PublishOpsForTest(new RecordingScanOps { Revision = 9 });

        ScanOutcome outcome = service.TryScanAsLeader(force: false);

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

            ScanOutcome outcome = service.TryScanAsLeader(force: false);

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
        Assert.True(ops.ScanCalled.Wait(5000, CancellationToken.None));
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
            Assert.True(ops.ScanCalled.Wait(5000, CancellationToken.None));
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
