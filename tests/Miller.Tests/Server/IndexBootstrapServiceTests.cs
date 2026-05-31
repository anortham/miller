using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Server;
using Miller.Server.Telemetry;
using Miller.Tests.Indexing;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the bootstrap's seed-revision discipline (finding-5, decision-10): a MISSING extract DB is the only
/// safe degrade-to-revision-0 case (the workspace genuinely has no revision yet). A present-but-unreadable DB
/// (corruption, the WAL writable-dir violation, a lock) is an operator/config error that must surface LOUDLY
/// rather than silently seeding revision 0 — which would mask the problem and trigger a spurious first-tick
/// rebuild. <see cref="IndexBootstrapService.ReadLatestRevisionOrZero"/> is the testable seam (the full
/// <c>Run()</c> needs the live binary + CWD and is exercised by the Scale suite).
/// </summary>
public sealed class IndexBootstrapServiceTests
{
    [Fact]
    public void DecideBootstrapScan_MissingDb_DeltaScansBeforeFirstLoad()
    {
        var decision = IndexBootstrapService.DecideBootstrapScan(
            dbExists: false,
            existingWorkspaceId: null,
            stableWorkspaceId: WorkspaceId.FromCanonicalRoot("/work/repo"));

        Assert.True(decision.ShouldScan);
        Assert.False(decision.Force);
        Assert.Equal(WorkspaceRegistryState.Ready, decision.RegistryStateAfterLoad);
    }

    [Fact]
    public void DecideBootstrapScan_ExistingDbWithStableId_LoadsExistingWithoutScan()
    {
        string stable = WorkspaceId.FromCanonicalRoot("/work/repo");

        var decision = IndexBootstrapService.DecideBootstrapScan(
            dbExists: true,
            existingWorkspaceId: stable,
            stableWorkspaceId: stable);

        Assert.False(decision.ShouldScan);
        Assert.False(decision.Force);
        Assert.Equal(WorkspaceRegistryState.LoadedExisting, decision.RegistryStateAfterLoad);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("550e8400-e29b-41d4-a716-446655440000")]
    [InlineData("legacy-non-stable-id")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void DecideBootstrapScan_ExistingDbWithMissingLegacyOrMismatchedId_ForceScansBeforeLoad(string? existingWorkspaceId)
    {
        string stable = WorkspaceId.FromCanonicalRoot("/work/repo");

        var decision = IndexBootstrapService.DecideBootstrapScan(
            dbExists: true,
            existingWorkspaceId: existingWorkspaceId,
            stableWorkspaceId: stable);

        Assert.True(decision.ShouldScan);
        Assert.True(decision.Force);
        Assert.Equal(WorkspaceRegistryState.Ready, decision.RegistryStateAfterLoad);
    }

    [Fact]
    public void RegisterBootstrapWorkspace_LoadedExisting_RecordsStableIdentityAndRevisionWithoutScanTimestamp()
    {
        string dir = Path.Combine(Path.GetTempPath(), "miller-bootstrap-registry-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(dir, "repo");
        string home = Path.Combine(dir, "home");
        Directory.CreateDirectory(root);
        try
        {
            string canonicalRoot = Path.GetFullPath(root);
            string stable = WorkspaceId.FromCanonicalRoot(canonicalRoot);
            string canonicalDb = Path.Combine(canonicalRoot, ".miller", "symbols.db");
            var workspace = WorkspaceContext.Create(root, AppContext.BaseDirectory, home) with
            {
                WorkspaceId = stable,
                CanonicalRoot = canonicalRoot,
                CanonicalExtractDbPath = canonicalDb,
            };

            var row = IndexBootstrapService.RegisterBootstrapWorkspace(
                workspace, stable, WorkspaceRegistryState.LoadedExisting, revision: 42);

            Assert.Equal(stable, row.WorkspaceId);
            Assert.Equal(WorkspaceId.Display(canonicalRoot, stable), row.DisplayId);
            Assert.Equal(canonicalRoot, row.CanonicalRoot);
            Assert.Equal(canonicalDb, row.IndexDbPath);
            Assert.Equal(WorkspaceRegistryState.LoadedExisting, row.State);
            Assert.Equal("loaded_existing", row.StateText);
            Assert.Equal(42, row.LastRevision);
            Assert.Null(row.LastScanAt);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void MarkRegistryScanned_RecordsReadyScanRevision()
    {
        string dir = Path.Combine(Path.GetTempPath(), "miller-bootstrap-scanned-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(dir, "repo");
        string home = Path.Combine(dir, "home");
        Directory.CreateDirectory(root);
        try
        {
            string canonicalRoot = Path.GetFullPath(root);
            string stable = WorkspaceId.FromCanonicalRoot(canonicalRoot);
            string canonicalDb = Path.Combine(canonicalRoot, ".miller", "symbols.db");
            var workspace = WorkspaceContext.Create(root, AppContext.BaseDirectory, home) with
            {
                WorkspaceId = stable,
                CanonicalRoot = canonicalRoot,
                CanonicalExtractDbPath = canonicalDb,
            };

            var row = IndexBootstrapService.MarkRegistryScanned(workspace, stable, revision: 9);

            Assert.Equal(WorkspaceRegistryState.Ready, row.State);
            Assert.Equal(9, row.LastRevision);
            Assert.NotNull(row.LastScanAt);
            Assert.Equal(canonicalRoot, row.CanonicalRoot);
            Assert.Equal(canonicalDb, row.IndexDbPath);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void ReadLatestRevisionOrZero_NullWorkspaceId_ReturnsZero()
    {
        // No workspace id known yet (a brand-new DB before the metadata is read) → no revision to seed.
        using var fx = JulieDbFixture.CreateDefault();
        Assert.Equal(0L, IndexBootstrapService.ReadLatestRevisionOrZero(fx.DbPath, workspaceId: null));
    }

    [Fact]
    public void ReadLatestRevisionOrZero_MissingDbFile_DegradesToZero()
    {
        // The DB file does not exist → the workspace has no persisted revision; safe to start fresh at 0.
        string missing = Path.Combine(
            Path.GetTempPath(), "miller-bootstrap-missing-" + Guid.NewGuid().ToString("N"), "symbols.db");
        Assert.Equal(0L, IndexBootstrapService.ReadLatestRevisionOrZero(missing, "ws-1"));
    }

    [Fact]
    public void ReadLatestRevisionOrZero_ReusedDbWithRevisions_ReturnsTheMaxForTheWorkspace()
    {
        // The happy path: a reused DB with persisted revisions seeds the holder from the MAX (so the freshness
        // poll does not rebuild on the first tick).
        using var fx = JulieDbFixture.Create(
            schemaVersion: JulieDbFixture.PinnedSchema, contractValue: JulieDbFixture.PinnedContract,
            rows: System.Array.Empty<JulieDbFixture.SymbolRow>(),
            workspaceId: "ws-seed",
            revisions: new[]
            {
                new JulieDbFixture.RevisionRow(3, "ws-seed"),
                new JulieDbFixture.RevisionRow(7, "ws-seed"),
                new JulieDbFixture.RevisionRow(5, "ws-other"), // another workspace must not leak in
            });

        Assert.Equal(7L, IndexBootstrapService.ReadLatestRevisionOrZero(fx.DbPath, "ws-seed"));
    }

    [Fact]
    public void ReadLatestRevisionOrZero_CorruptDb_ThrowsLoudly_NotDegradeToZero()
    {
        // A present-but-corrupt DB file is an operator/config error: decision-10 says surface loudly. The
        // narrowed catch (FileNotFoundException only) must let the SqliteException propagate rather than hide it
        // as revision 0 (which would mask corruption and trigger a spurious rebuild).
        string dir = Path.Combine(Path.GetTempPath(), "miller-bootstrap-corrupt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string dbPath = Path.Combine(dir, "symbols.db");
        try
        {
            // Not a valid SQLite file — opening + querying it raises SqliteException, not FileNotFound.
            File.WriteAllText(dbPath, "this is not a sqlite database header at all, just garbage bytes");

            Assert.Throws<SqliteException>(
                () => IndexBootstrapService.ReadLatestRevisionOrZero(dbPath, "ws-1"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    // ---- finding-4: a prune failure after the ledger is opened must dispose it (no leak) ----

    [Fact]
    public void OpenAndPrune_Success_ReturnsLiveLedger_ThatStillRecords()
    {
        // The happy path: the returned ledger is OPEN (the caller owns it) and prune returns a count.
        string dir = Path.Combine(Path.GetTempPath(), "miller-openprune-ok-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string dbPath = Path.Combine(dir, "telemetry.db");
        try
        {
            var ledger = IndexBootstrapService.OpenAndPrune(dbPath, "ws-1", "/repo/work", retentionDays: 30, out int pruned);
            using (ledger)
            {
                Assert.Equal(0, pruned); // empty DB → nothing to prune
                // A live (undisposed) ledger records without dropping (a disposed one would increment Dropped).
                ledger.Record(new TelemetryRecord(
                    Tool: "probe", Op: null, WorkspaceId: "ws-1", DurationMs: 0, Outcome: "ok",
                    ErrorKind: null, ResultCount: null, BytesExamined: 0, BytesReturned: 0, SourceBytes: 0,
                    EstTokens: null, IndexFresh: null, TargetHash: null, MetadataJson: "{}"));
                Assert.Equal(0, ledger.DroppedWrites);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void OpenAndPrune_PruneThrows_DisposesTheLedger_AndRethrows()
    {
        // finding-4 (end-to-end on the real ledger): a negative retentionDays makes Prune throw AFTER the
        // ledger is opened. OpenAndPrune must dispose the just-opened ledger and rethrow rather than leak it.
        string dir = Path.Combine(Path.GetTempPath(), "miller-openprune-throw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string dbPath = Path.Combine(dir, "telemetry.db");
        try
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => IndexBootstrapService.OpenAndPrune(dbPath, "ws-1", "/repo/work", retentionDays: -1, out _));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>A disposable spy that records whether Dispose was called — the discriminating observation.</summary>
    private sealed class DisposeSpy : IDisposable
    {
        public int DisposeCount { get; private set; }
        public bool Disposed => DisposeCount > 0;
        public void Dispose() => DisposeCount++;
    }

    [Fact]
    public void PrimeOrDispose_PrimeThrows_DisposesTheResource_AndRethrowsTheSameException()
    {
        // The discriminating unit test for finding-4's disposal contract: when priming throws, the resource is
        // disposed exactly once and the ORIGINAL exception propagates (not a dispose-time error).
        var spy = new DisposeSpy();
        var boom = new InvalidOperationException("prime failed");

        var thrown = Assert.Throws<InvalidOperationException>(
            () => IndexBootstrapService.PrimeOrDispose(spy, _ => throw boom));

        Assert.Same(boom, thrown);
        Assert.True(spy.Disposed);
        Assert.Equal(1, spy.DisposeCount);
    }

    [Fact]
    public void PrimeOrDispose_PrimeSucceeds_ReturnsTheLiveResource_Undisposed()
    {
        // The happy path: the resource is returned to the caller still OPEN (the caller owns disposal); priming
        // ran exactly once.
        var spy = new DisposeSpy();
        int primeCalls = 0;

        var returned = IndexBootstrapService.PrimeOrDispose(spy, _ => primeCalls++);

        Assert.Same(spy, returned);
        Assert.False(spy.Disposed); // NOT disposed on the success path
        Assert.Equal(1, primeCalls);
    }

    [Fact]
    public void ReadLatestRevisionOrZero_NonWritableDbDirectory_ThrowsLoudly_NotDegradeToZero()
    {
        // The WAL writable-dir guard (D4) raises InvalidOperationException. It is a config error → propagate
        // loudly (decision-10), NOT degrade to revision 0. POSIX-only (dir-permission semantics).
        if (OperatingSystem.IsWindows())
            return;

        using var fx = JulieDbFixture.Create(
            schemaVersion: JulieDbFixture.PinnedSchema, contractValue: JulieDbFixture.PinnedContract,
            rows: System.Array.Empty<JulieDbFixture.SymbolRow>(),
            workspaceId: "ws-1",
            revisions: new[] { new JulieDbFixture.RevisionRow(2, "ws-1") });

        string dir = fx.Directory;
        var original = File.GetUnixFileMode(dir);
        try
        {
            File.SetUnixFileMode(dir,
                UnixFileMode.UserRead | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            Assert.Throws<InvalidOperationException>(
                () => IndexBootstrapService.ReadLatestRevisionOrZero(fx.DbPath, "ws-1"));
        }
        finally
        {
            File.SetUnixFileMode(dir, original);
        }
    }
}
