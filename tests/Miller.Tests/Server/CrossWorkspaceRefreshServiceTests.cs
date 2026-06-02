using Miller.Indexing;
using Miller.Server.Workspaces;
using Xunit;

namespace Miller.Tests.Server;

public sealed class CrossWorkspaceRefreshServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _registryDbPath;

    public CrossWorkspaceRefreshServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-cross-workspace-refresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _registryDbPath = Path.Combine(_dir, "workspaces.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData(WorkspaceRefreshStatus.Refreshed, "refreshed")]
    [InlineData(WorkspaceRefreshStatus.Unchanged, "unchanged")]
    [InlineData(WorkspaceRefreshStatus.LockBusy, "lock_busy")]
    [InlineData(WorkspaceRefreshStatus.MissingRoot, "missing_root")]
    [InlineData(WorkspaceRefreshStatus.MissingIndex, "missing_index")]
    [InlineData(WorkspaceRefreshStatus.Failed, "failed")]
    public void StatusText_ExposesTheApprovedStableRefreshContract(
        WorkspaceRefreshStatus status,
        string expected)
    {
        var result = new WorkspaceRefreshResult(status, "ws", "/work", "/work/.miller/symbols.db");

        Assert.Equal(expected, result.StatusText);
    }

    [Fact]
    public void Refresh_UnlockedTarget_AcquiresIndexerLockScansAndMarksScanned()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("unlocked");
        string dbPath = Path.Combine(root, ".miller", "symbols.db");
        registry.UpsertSeen("target-ws", "target-111111111111", root, dbPath);
        registry.MarkScanned("target-ws", revision: 1);
        int scanCount = 0;
        var service = NewService(
            registry,
            scan: (scanRoot, scanDb, force) =>
            {
                scanCount++;
                Assert.Equal(root, scanRoot);
                Assert.Equal(dbPath, scanDb);
                Assert.False(force);
                return Report(root, dbPath, "target-ws", revision: 5);
            },
            acquireLock: millerDir =>
            {
                Assert.Equal(Path.Combine(root, ".miller"), millerDir);
                return new NoopLease();
            });

        WorkspaceRefreshResult result = service.Refresh("target-ws");

        Assert.Equal(WorkspaceRefreshStatus.Refreshed, result.Status);
        Assert.True(result.Scanned);
        Assert.Equal(5, result.Revision);
        Assert.Equal(1, scanCount);
        Assert.Equal(5, registry.Get("target-ws")?.LastRevision);
        Assert.Equal(WorkspaceRegistryState.Ready, registry.Get("target-ws")?.State);
    }

    [Fact]
    public void Refresh_UnlockedTargetWithPartialReport_SurfacesWarningText()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("partial");
        string dbPath = Path.Combine(root, ".miller", "symbols.db");
        registry.UpsertSeen("target-ws", "target-111111111111", root, dbPath);
        registry.MarkScanned("target-ws", revision: 1);
        var service = NewService(
            registry,
            scan: (_, _, _) => PartialReport(root, dbPath, "target-ws", revision: 5),
            acquireLock: _ => new NoopLease());

        WorkspaceRefreshResult result = service.Refresh("target-ws");

        Assert.Equal(WorkspaceRefreshStatus.Refreshed, result.Status);
        Assert.True(result.Scanned);
        Assert.Equal(5, result.Revision);
        Assert.Contains("PARTIAL artifact", result.WarningText, StringComparison.Ordinal);
        Assert.Contains("Controllers/Broken.cs", result.WarningText, StringComparison.Ordinal);
        Assert.Equal(5, registry.Get("target-ws")?.LastRevision);
    }

    [Fact]
    public void Refresh_UnlockedTargetWithNoNewRevision_ReturnsUnchangedButStillReportsThatItScanned()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("unchanged");
        string dbPath = Path.Combine(root, ".miller", "symbols.db");
        registry.UpsertSeen("target-ws", "target-111111111111", root, dbPath);
        registry.MarkScanned("target-ws", revision: 7);
        int scanCount = 0;
        var service = NewService(
            registry,
            scan: (_, _, force) =>
            {
                scanCount++;
                Assert.False(force);
                return Report(root, dbPath, "target-ws", revision: 7);
            },
            acquireLock: _ => new NoopLease());

        WorkspaceRefreshResult result = service.Refresh("target-ws");

        Assert.Equal(WorkspaceRefreshStatus.Unchanged, result.Status);
        Assert.Equal("unchanged", result.StatusText);
        Assert.True(result.Scanned);
        Assert.Equal(7, result.Revision);
        Assert.Equal(1, scanCount);
        Assert.Equal(7, registry.Get("target-ws")?.LastRevision);
    }

    [Fact]
    public void Refresh_ForceTrue_ThreadsForceThroughTheLockBasedScan()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("force");
        string dbPath = Path.Combine(root, ".miller", "symbols.db");
        registry.UpsertSeen("target-ws", "target-111111111111", root, dbPath);
        registry.MarkScanned("target-ws", revision: 2);
        bool? observedForce = null;
        var service = NewService(
            registry,
            scan: (_, _, force) =>
            {
                observedForce = force;
                return Report(root, dbPath, "target-ws", revision: 9);
            },
            acquireLock: _ => new NoopLease());

        WorkspaceRefreshResult result = service.Refresh("target-ws", force: true);

        Assert.True(observedForce);
        Assert.Equal(WorkspaceRefreshStatus.Refreshed, result.Status);
        Assert.True(result.Scanned);
        Assert.Equal(9, result.Revision);
        Assert.Equal(9, registry.Get("target-ws")?.LastRevision);
    }

    [Fact]
    public void Refresh_LockBusy_DoesNotScanAndReturnsUnconfirmedWhenNoRevisionChangeAppears()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("busy");
        string dbPath = Path.Combine(root, ".miller", "symbols.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        File.WriteAllText(dbPath, "readable index placeholder");
        registry.UpsertSeen("target-ws", "target-111111111111", root, dbPath);
        registry.MarkScanned("target-ws", revision: 7);
        int scanCount = 0;
        var clock = new FakeClock();
        var service = NewService(
            registry,
            scan: (_, _, _) =>
            {
                scanCount++;
                throw new InvalidOperationException("scan should not run while the lock is busy");
            },
            acquireLock: _ => null,
            readLatestRevision: _ => 7,
            clock: clock);

        WorkspaceRefreshResult result = service.Refresh("target-ws");

        Assert.Equal(WorkspaceRefreshStatus.LockBusy, result.Status);
        Assert.False(result.Scanned);
        Assert.Equal(7, result.Revision);
        Assert.Equal(0, scanCount);
        Assert.Contains("busy", result.WarningText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(7, registry.Get("target-ws")?.LastRevision);
    }

    [Fact]
    public void Refresh_LockBusyWithMissingDb_ReturnsMissingIndexBecauseNothingReadableCanBeServed()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("busy-missing-index");
        string dbPath = Path.Combine(root, ".miller", "symbols.db");
        registry.UpsertSeen("target-ws", "target-111111111111", root, dbPath);
        int scanCount = 0;
        var clock = new FakeClock();
        var service = NewService(
            registry,
            scan: (_, _, _) =>
            {
                scanCount++;
                throw new InvalidOperationException("scan should not run while the lock is busy");
            },
            acquireLock: _ => null,
            readLatestRevision: _ => throw new FileNotFoundException("index db missing"),
            clock: clock);

        WorkspaceRefreshResult result = service.Refresh("target-ws");

        Assert.Equal(WorkspaceRefreshStatus.MissingIndex, result.Status);
        Assert.Equal("missing_index", result.StatusText);
        Assert.False(result.Scanned);
        Assert.Null(result.Revision);
        Assert.Equal(0, scanCount);
        Assert.False(File.Exists(dbPath));
    }

    [Fact]
    public void Refresh_LockBusy_PollsForAVisibleRevisionChangeWithoutScanning()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("busy-change");
        string dbPath = Path.Combine(root, ".miller", "symbols.db");
        registry.UpsertSeen("target-ws", "target-111111111111", root, dbPath);
        registry.MarkScanned("target-ws", revision: 7);
        int scanCount = 0;
        int pollCount = 0;
        var clock = new FakeClock();
        var service = NewService(
            registry,
            scan: (_, _, _) =>
            {
                scanCount++;
                throw new InvalidOperationException("scan should not run while the lock is busy");
            },
            acquireLock: _ => null,
            readLatestRevision: _ => ++pollCount < 2 ? 7 : 8,
            clock: clock);

        WorkspaceRefreshResult result = service.Refresh("target-ws");

        Assert.Equal(WorkspaceRefreshStatus.Refreshed, result.Status);
        Assert.False(result.Scanned);
        Assert.Equal(8, result.Revision);
        Assert.Equal(0, scanCount);
        Assert.Equal(8, registry.Get("target-ws")?.LastRevision);
    }

    [Fact]
    public void Refresh_MissingRoot_MarksTheRegistryRowMissing()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string missingRoot = Path.Combine(_dir, "missing-root");
        string dbPath = Path.Combine(missingRoot, ".miller", "symbols.db");
        registry.UpsertSeen("target-ws", "target-111111111111", missingRoot, dbPath);
        var service = NewService(
            registry,
            scan: (_, _, _) => throw new InvalidOperationException("scan should not run for a missing root"),
            acquireLock: _ => throw new InvalidOperationException("lock should not be acquired for a missing root"));

        WorkspaceRefreshResult result = service.Refresh("target-ws");

        Assert.Equal(WorkspaceRefreshStatus.MissingRoot, result.Status);
        Assert.Equal("missing_root", result.StatusText);
        Assert.False(result.Scanned);
        Assert.Equal(WorkspaceRegistryState.Missing, registry.Get("target-ws")?.State);
        Assert.Contains(missingRoot, registry.Get("target-ws")?.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public void Refresh_SensitiveRoot_MarksErrorWithoutLockingOrScanning()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string sensitiveRoot = Path.GetPathRoot(_dir)
            ?? Path.DirectorySeparatorChar.ToString();
        string dbPath = Path.Combine(sensitiveRoot, ".miller", "symbols.db");
        registry.UpsertSeen("target-ws", "target-111111111111", sensitiveRoot, dbPath);
        var service = NewService(
            registry,
            scan: (_, _, _) => throw new InvalidOperationException("scan should not run for a sensitive root"),
            acquireLock: _ => throw new InvalidOperationException("lock should not be acquired for a sensitive root"));

        WorkspaceRefreshResult result = service.Refresh("target-ws");

        Assert.Equal(WorkspaceRefreshStatus.Failed, result.Status);
        Assert.Equal("failed", result.StatusText);
        Assert.False(result.Scanned);
        Assert.Equal(WorkspaceRegistryState.Error, registry.Get("target-ws")?.State);
        Assert.Contains("sensitive system path", registry.Get("target-ws")?.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public void Refresh_MissingDb_IsCreatedByTheScanWhenTheLockIsAvailable()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("missing-db");
        string dbPath = Path.Combine(root, ".miller", "symbols.db");
        registry.UpsertSeen("target-ws", "target-111111111111", root, dbPath);
        var service = NewService(
            registry,
            scan: (scanRoot, scanDb, force) =>
            {
                Assert.Equal(root, scanRoot);
                Assert.Equal(dbPath, scanDb);
                Assert.False(force);
                Directory.CreateDirectory(Path.GetDirectoryName(scanDb)!);
                File.WriteAllText(scanDb, "created by fake scan");
                return Report(root, dbPath, "target-ws", revision: 1);
            },
            acquireLock: _ => new NoopLease());

        WorkspaceRefreshResult result = service.Refresh("target-ws");

        Assert.Equal(WorkspaceRefreshStatus.Refreshed, result.Status);
        Assert.True(File.Exists(dbPath));
        Assert.Equal(1, registry.Get("target-ws")?.LastRevision);
    }

    private CrossWorkspaceRefreshService NewService(
        WorkspaceRegistry registry,
        Func<string, string, bool, ExtractReport> scan,
        Func<string, IDisposable?> acquireLock,
        Func<string, long>? readLatestRevision = null,
        FakeClock? clock = null)
    {
        clock ??= new FakeClock();
        return new CrossWorkspaceRefreshService(
            registry,
            scan,
            acquireLock,
            readLatestRevision ?? (_ => 0),
            lockBusyWait: TimeSpan.FromMilliseconds(250),
            lockBusyPollInterval: TimeSpan.FromMilliseconds(100),
            sleep: clock.Sleep,
            utcNow: clock.UtcNow);
    }

    private string NewRoot(string name)
    {
        string root = Path.Combine(_dir, name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    // workspaceId is retained only for caller readability/registry setup; the nested v1 report carries no
    // workspace_id and CrossWorkspaceRefreshService no longer cross-checks one (E4 removed the echo check).
    private static ExtractReport Report(string root, string dbPath, string workspaceId, long revision) =>
        new(
            ReportSchemaVersion: 1, Status: "ok", Operation: "scan", Mode: "incremental", Input: null,
            Artifact: new ExtractArtifact(
                DbPath: dbPath, RootPath: root, ArtifactId: "a",
                SchemaVersion: MillerExtractContract.ExpectedSchemaVersion,
                ExtractContractVersion: MillerExtractContract.ExpectedExtractContractVersion,
                SqliteSchemaVersion: MillerExtractContract.ExpectedSqliteSchemaVersion,
                JsonlSchemaVersion: 1, HashAlgorithm: MillerExtractContract.ExpectedHashAlgorithm,
                ParserInventoryFingerprint: "p", CapabilitySnapshotFingerprint: "c"),
            Tool: new ExtractTool("julie-extract", "2.0.0"),
            RevisionBlock: new ExtractRevision(revision, revision),
            Counts: new ExtractCounts(1, 1, 0, 0, 0, 0,
                RowsWritten: new ExtractRowCounts(null, 1, null, null, null, null, null, null, null, null),
                Totals: new ExtractRowCounts(1, 1, null, null, null, null, null, null, null, null)),
            Errors: Array.Empty<ReportDiagnostic>(), Warnings: Array.Empty<ReportDiagnostic>());

    private static ExtractReport PartialReport(string root, string dbPath, string workspaceId, long revision) =>
        Report(root, dbPath, workspaceId, revision) with
        {
            Status = "partial",
            Counts = new ExtractCounts(2, 2, 0, 0, 0, 1,
                RowsWritten: new ExtractRowCounts(null, 1, null, null, null, null, null, null, null, null),
                Totals: new ExtractRowCounts(1, 1, null, null, null, null, null, null, null, null)),
            Errors = new[]
            {
                new ReportDiagnostic(
                    "parse_error",
                    "syntax error",
                    Path.Combine(root, "Controllers", "Broken.cs"),
                    "Controllers/Broken.cs",
                    Recoverable: true),
            },
        };

    private sealed class NoopLease : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class FakeClock
    {
        private DateTimeOffset _now = new(2026, 5, 31, 12, 0, 0, TimeSpan.Zero);

        public DateTimeOffset UtcNow() => _now;

        public void Sleep(TimeSpan delay) => _now += delay;
    }
}
