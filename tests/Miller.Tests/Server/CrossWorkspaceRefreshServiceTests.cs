using Miller.Core.Freshness;
using Miller.Indexing;
using Miller.Indexing.Store;
using Miller.Server.Cli;
using Miller.Server.Workspaces;
using Miller.Tests.Indexing;
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
    [InlineData(WorkspaceRefreshStatus.IneligibleExtractor, "ineligible_extractor")]
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
            scan: (scanRoot, scanDb, force, _, _) =>
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
            scan: (_, _, _, _, _) => PartialReport(root, dbPath, "target-ws", revision: 5),
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
    public void Refresh_NoChangeWithSlowFileWarning_RemainsUnchangedAndSurfacesWarningText()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("slow-warning");
        string dbPath = Path.Combine(root, ".miller", "symbols.db");
        registry.UpsertSeen("target-ws", "target-111111111111", root, dbPath);
        registry.MarkScanned("target-ws", revision: 4);
        ExtractReport report = NoChangeReport(root, dbPath, "target-ws", revision: 4) with
        {
            Warnings = [SlowFileWarning(root)],
        };
        var service = NewService(
            registry,
            scan: (_, _, _, _, _) => report,
            acquireLock: _ => new NoopLease());

        WorkspaceRefreshResult result = service.Refresh("target-ws");

        Assert.Equal(WorkspaceRefreshStatus.Unchanged, result.Status);
        Assert.True(result.Scanned);
        Assert.Equal(4, result.Revision);
        Assert.Contains("slow_file_skipped", result.WarningText, StringComparison.Ordinal);
        Assert.Contains("Generated/Slow.kt", result.WarningText, StringComparison.Ordinal);
        Assert.Equal(4, registry.Get("target-ws")?.LastRevision);
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
            scan: (_, _, force, _, _) =>
            {
                scanCount++;
                Assert.False(force);
                return NoChangeReport(root, dbPath, "target-ws", revision: 7);
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
    public void Refresh_UnlockedTargetWithNoNewRevision_ReturnsScanArtifactId()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("unchanged-artifact");
        string dbPath = Path.Combine(root, ".miller", "symbols.db");
        registry.UpsertSeen("target-ws", "target-111111111111", root, dbPath);
        registry.MarkScanned("target-ws", revision: 7);
        var service = NewService(
            registry,
            scan: (_, _, _, _, _) => NoChangeReport(root, dbPath, "target-ws", revision: 7),
            acquireLock: _ => new NoopLease());

        WorkspaceRefreshResult result = service.Refresh("target-ws");

        Assert.Equal("a", result.ArtifactId);
    }

    [Fact]
    public void Refresh_NoChangeWithReportArtifactMissing_FallsBackToPreScanArtifactId()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("unchanged-prescan-artifact");
        string dbPath = Path.Combine(root, ".miller", "symbols.db");
        registry.UpsertSeen("target-ws", "target-111111111111", root, dbPath);
        registry.MarkScanned("target-ws", revision: 7);
        int artifactReads = 0;
        var service = NewService(
            registry,
            scan: (_, _, _, _, _) =>
            {
                Assert.Equal(1, artifactReads);
                return NoChangeReport(root, dbPath, "target-ws", revision: 7) with { Artifact = null };
            },
            acquireLock: _ => new NoopLease(),
            readArtifactId: _ => ++artifactReads == 1 ? "artifact-before" : null);

        WorkspaceRefreshResult result = service.Refresh("target-ws");

        Assert.Equal(WorkspaceRefreshStatus.Unchanged, result.Status);
        Assert.Equal("artifact-before", result.ArtifactId);
    }

    [Fact]
    public void Refresh_ForceRebuildThatResetsTheRevisionCounter_ReportsRefreshed()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("force-rebuild");
        string dbPath = Path.Combine(root, ".miller", "symbols.db");
        registry.UpsertSeen("target-ws", "target-111111111111", root, dbPath);
        registry.MarkScanned("target-ws", revision: 7);
        var service = NewService(
            registry,
            scan: (_, _, force, _, _) =>
            {
                Assert.True(force);
                // A --force scan of an incompatible artifact deletes and recreates the DB; the fresh
                // artifact's revision counter restarts at 1 even though everything was re-extracted
                // (the 2026-06-11 Eros fleet finding: this used to be misreported as "unchanged").
                return Report(root, dbPath, "target-ws", revision: 1);
            },
            acquireLock: _ => new NoopLease());

        WorkspaceRefreshResult result = service.Refresh("target-ws", force: true);

        Assert.Equal(WorkspaceRefreshStatus.Refreshed, result.Status);
        Assert.True(result.Scanned);
        Assert.Equal(1, result.Revision);
        Assert.Equal(1, registry.Get("target-ws")?.LastRevision);
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
            scan: (_, _, force, _, _) =>
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
    public void Refresh_MalformedStorePointer_ForcesSourceReconciliationBeforeServingLegacyArtifact()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("malformed-store-pointer");
        string dbPath = Path.Combine(root, ".miller", "symbols.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        File.WriteAllText(Path.Combine(root, ".miller", "store.json"), "not-json");
        registry.UpsertSeen("target-ws", "target-111111111111", root, dbPath);
        registry.MarkScanned("target-ws", revision: 1);
        bool? observedForce = null;
        var service = NewService(
            registry,
            scan: (_, _, force, _, _) =>
            {
                observedForce = force;
                return Report(root, dbPath, "target-ws", revision: 2);
            },
            acquireLock: _ => new NoopLease(),
            storeClient: new UnexpectedStoreClient());

        WorkspaceRefreshResult result = service.Refresh("target-ws");

        Assert.Equal(WorkspaceRefreshStatus.Refreshed, result.Status);
        Assert.True(observedForce);
        Assert.Contains("family-store rollback export could not be used", result.WarningText, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(root, ".miller", "store.json")));
    }

    [Fact]
    public void Refresh_MalformedStorePointer_ClearsRollbackMarkersAfterSourceReconciliation()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("malformed-store-pointer-markers");
        string dbPath = Path.Combine(root, ".miller", "symbols.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        File.WriteAllText(Path.Combine(root, ".miller", "store.json"), "not-json");
        File.WriteAllText(Path.Combine(root, ".miller", "store-rollback.pending"), "stale-pending");
        File.WriteAllText(Path.Combine(root, ".miller", "store-rollback.recovery"), "stale-recovery");
        registry.UpsertSeen("target-ws", "target-111111111111", root, dbPath);
        registry.MarkScanned("target-ws", revision: 1);
        var service = NewService(
            registry,
            scan: (_, _, force, _, _) =>
            {
                Assert.True(force);
                return Report(root, dbPath, "target-ws", revision: 2);
            },
            acquireLock: _ => new NoopLease(),
            storeClient: new UnexpectedStoreClient(),
            storeEnabled: static () => false);

        WorkspaceRefreshResult result = service.Refresh("target-ws");

        Assert.Equal(WorkspaceRefreshStatus.Refreshed, result.Status);
        Assert.False(File.Exists(Path.Combine(root, ".miller", "store.json")));
        Assert.False(File.Exists(Path.Combine(root, ".miller", "store-rollback.pending")));
        Assert.False(File.Exists(Path.Combine(root, ".miller", "store-rollback.recovery")));
    }

    [Fact]
    public void Refresh_MalformedStorePointer_HonorsAutomaticBackoffThenReconciles()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("malformed-store-pointer-backoff");
        string dbPath = Path.Combine(root, ".miller", "symbols.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        string pointerPath = Path.Combine(root, ".miller", "store.json");
        File.WriteAllText(pointerPath, "not-json");
        registry.UpsertSeen("target-ws", "target-111111111111", root, dbPath);
        registry.MarkScanned("target-ws", revision: 1);
        DateTimeOffset now = new(2026, 8, 2, 0, 0, 0, TimeSpan.Zero);
        var policy = new InMemoryScanFailurePolicy(utcNow: () => now, jitter: static () => 0);
        policy.RecordFailure(ScanIntent.IncrementalReconcile, ScanFailurePolicy.SigkillExitCode, jobs: 4);
        int scanCount = 0;
        bool? observedForce = null;
        var service = NewService(
            registry,
            scan: (_, _, force, _, _) =>
            {
                scanCount++;
                observedForce = force;
                return Report(root, dbPath, "target-ws", revision: 2);
            },
            acquireLock: _ => new NoopLease(),
            failurePolicyFor: (_, _) => policy,
            storeClient: new UnexpectedStoreClient());

        WorkspaceRefreshResult deferred = service.Refresh("target-ws");

        Assert.Equal(WorkspaceRefreshStatus.MissingIndex, deferred.Status);
        Assert.Equal(0, scanCount);
        Assert.True(File.Exists(pointerPath));

        now += ScanFailurePolicy.MaxJitteredBackoffFor(1);
        WorkspaceRefreshResult repaired = service.Refresh("target-ws");

        Assert.Equal(WorkspaceRefreshStatus.Refreshed, repaired.Status);
        Assert.True(observedForce);
        Assert.Equal(1, scanCount);
        Assert.False(File.Exists(pointerPath));
        Assert.Null(policy.Read());
    }

    [Fact]
    public void Refresh_FailedSourceReconciliationPreservesMalformedStorePointer()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("malformed-store-pointer-failure");
        string dbPath = Path.Combine(root, ".miller", "symbols.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        string pointerPath = Path.Combine(root, ".miller", "store.json");
        File.WriteAllText(pointerPath, "not-json");
        registry.UpsertSeen("target-ws", "target-111111111111", root, dbPath);
        registry.MarkScanned("target-ws", revision: 1);
        var service = NewService(
            registry,
            scan: (_, _, _, _, _) => throw new InvalidOperationException("source scan failed"),
            acquireLock: _ => new NoopLease(),
            storeClient: new UnexpectedStoreClient());

        WorkspaceRefreshResult result = service.Refresh("target-ws");

        Assert.Equal(WorkspaceRefreshStatus.Failed, result.Status);
        Assert.False(result.Scanned);
        Assert.Contains("source scan failed", result.Error, StringComparison.Ordinal);
        Assert.True(File.Exists(pointerPath));
    }

    [Fact]
    public void Refresh_PointerCleanupFailureDoesNotMarkTheRegistryReady()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("malformed-store-pointer-cleanup-failure");
        string dbPath = Path.Combine(root, ".miller", "symbols.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        File.WriteAllText(Path.Combine(root, ".miller", "store.json"), "not-json");
        registry.UpsertSeen("target-ws", "target-111111111111", root, dbPath);
        registry.MarkScanned("target-ws", revision: 1);
        var policy = new InMemoryScanFailurePolicy();
        var service = NewService(
            registry,
            scan: (_, _, force, _, _) =>
            {
                Assert.True(force);
                return Report(root, dbPath, "target-ws", revision: 2);
            },
            acquireLock: _ => new NoopLease(),
            failurePolicyFor: (_, _) => policy,
            storeClient: new UnexpectedStoreClient(),
            deleteStorePointer: _ => throw new IOException("pointer is locked"));

        WorkspaceRefreshResult result = service.Refresh("target-ws");

        Assert.Equal(WorkspaceRefreshStatus.Failed, result.Status);
        Assert.False(result.Scanned);
        Assert.Contains("pointer is locked", result.Error, StringComparison.Ordinal);
        Assert.Null(policy.Read());
        WorkspaceRegistryRow row = Assert.IsType<WorkspaceRegistryRow>(registry.Get("target-ws"));
        Assert.Equal(WorkspaceRegistryState.Error, row.State);
        Assert.Equal(1, row.LastRevision);
        Assert.Contains("pointer is locked", row.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public void Refresh_RollbackExportFailure_MarksRegistryErrorAndPreservesStoreBinding()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("rollback-export-failure");
        string dbPath = Path.Combine(root, ".miller", "symbols.db");
        string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(root);
        StoreFamilyBinding binding = new(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Path.Combine(root, "missing-store"),
            "view-a",
            canonicalRoot,
            StoreBindingState.Ready);
        StoreWorkspacePointer.Write(root, binding);
        registry.UpsertSeen("target-ws", "target-111111111111", root, dbPath);
        registry.MarkScanned("target-ws", revision: 1);
        var service = NewService(
            registry,
            scan: (_, _, _, _, _) => throw new InvalidOperationException("scan must not run after rollback failure"),
            acquireLock: _ => new NoopLease(),
            readArtifactId: _ => "legacy-before",
            storeClient: new UnexpectedStoreClient(),
            storeEnabled: static () => false);

        WorkspaceRefreshResult result = service.Refresh("target-ws");

        Assert.Equal(WorkspaceRefreshStatus.Failed, result.Status);
        Assert.False(result.Scanned);
        Assert.Equal("legacy-before", result.ArtifactId);
        Assert.Contains("Store rollback export failed", result.Error, StringComparison.Ordinal);
        WorkspaceRegistryRow row = Assert.IsType<WorkspaceRegistryRow>(registry.Get("target-ws"));
        Assert.Equal(WorkspaceRegistryState.Error, row.State);
        Assert.Equal(result.Error, row.LastError);
        Assert.NotNull(StoreWorkspacePointer.Read(root));
    }

    [Fact]
    public void Refresh_StoreModeStillAppliesTheEligibilityGate()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("store-ineligible");
        string dbPath = Path.Combine(root, ".miller", "symbols.db");
        registry.UpsertSeen("target-ws", "target-111111111111", root, dbPath);
        int scanCount = 0;
        var service = NewService(
            registry,
            scan: (_, _, _, _, _) =>
            {
                scanCount++;
                return Report(root, dbPath, "target-ws", revision: 2);
            },
            acquireLock: _ => new NoopLease(),
            eligibilityGate: _ => LeadershipEligibility.Evaluate("2.0.0", "3.0.0", allowDowngrade: false),
            storeEnabled: static () => true);

        WorkspaceRefreshResult result = service.Refresh("target-ws");

        Assert.Equal(WorkspaceRefreshStatus.IneligibleExtractor, result.Status);
        Assert.Equal(0, scanCount);
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
            scan: (_, _, _, _, _) =>
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
    public void Refresh_LockBusy_ReportsTheRecordedLiveHolderInTheWarning()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("busy-live-holder");
        string dbPath = Path.Combine(root, ".miller", "symbols.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        File.WriteAllText(dbPath, "readable index placeholder");
        registry.UpsertSeen("target-ws", "target-111111111111", root, dbPath);
        registry.MarkScanned("target-ws", revision: 7);
        int currentPid = Environment.ProcessId;
        Miller.Server.Hosting.LeaderIdentityFile.Write(
            Path.GetDirectoryName(dbPath)!,
            new Miller.Server.Hosting.LeaderIdentity(
                currentPid, "9.9.9-test", ProcessPath: null, StartedAtUtc: DateTimeOffset.UtcNow));
        var service = NewService(
            registry,
            scan: (_, _, _, _, _) => throw new InvalidOperationException("scan should not run while the lock is busy"),
            acquireLock: _ => null,
            readLatestRevision: _ => 7);

        WorkspaceRefreshResult result = service.Refresh("target-ws");

        Assert.Equal(WorkspaceRefreshStatus.LockBusy, result.Status);
        Assert.Contains(currentPid.ToString(), result.WarningText, StringComparison.Ordinal);
        Assert.Contains("9.9.9-test", result.WarningText, StringComparison.Ordinal);
        Assert.Contains("alive", result.WarningText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Refresh_LockBusy_WithNoRecordedIdentity_SaysTheHolderIsUnknown()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("busy-unknown-holder");
        string dbPath = Path.Combine(root, ".miller", "symbols.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        File.WriteAllText(dbPath, "readable index placeholder");
        registry.UpsertSeen("target-ws", "target-111111111111", root, dbPath);
        registry.MarkScanned("target-ws", revision: 7);
        var service = NewService(
            registry,
            scan: (_, _, _, _, _) => throw new InvalidOperationException("scan should not run while the lock is busy"),
            acquireLock: _ => null,
            readLatestRevision: _ => 7);

        WorkspaceRefreshResult result = service.Refresh("target-ws");

        Assert.Equal(WorkspaceRefreshStatus.LockBusy, result.Status);
        Assert.Contains("no leader identity", result.WarningText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Refresh_LockBusy_WithAnExitedRecordedLeader_ReportsTheStaleIdentity()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("busy-stale-holder");
        string dbPath = Path.Combine(root, ".miller", "symbols.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        File.WriteAllText(dbPath, "readable index placeholder");
        registry.UpsertSeen("target-ws", "target-111111111111", root, dbPath);
        registry.MarkScanned("target-ws", revision: 7);
        const int deadPid = 0x7FFFFFF0;
        Miller.Server.Hosting.LeaderIdentityFile.Write(
            Path.GetDirectoryName(dbPath)!,
            new Miller.Server.Hosting.LeaderIdentity(
                deadPid, "9.9.8-test", ProcessPath: null, StartedAtUtc: DateTimeOffset.UtcNow));
        var service = NewService(
            registry,
            scan: (_, _, _, _, _) => throw new InvalidOperationException("scan should not run while the lock is busy"),
            acquireLock: _ => null,
            readLatestRevision: _ => 7);

        WorkspaceRefreshResult result = service.Refresh("target-ws");

        Assert.Equal(WorkspaceRefreshStatus.LockBusy, result.Status);
        Assert.Contains(deadPid.ToString(), result.WarningText, StringComparison.Ordinal);
        Assert.Contains("no longer running", result.WarningText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Refresh_ForceLockBusy_RequestsLeaderFullScanAndReturnsRefreshedWhenRevisionAppears()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("busy-force-request");
        string dbPath = Path.Combine(root, ".miller", "symbols.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        File.WriteAllText(dbPath, "readable index placeholder");
        registry.UpsertSeen("target-ws", "target-111111111111", root, dbPath);
        registry.MarkScanned("target-ws", revision: 7);
        int scanCount = 0;
        int requestCount = 0;
        int pollCount = 0;
        var clock = new FakeClock();
        var service = NewService(
            registry,
            scan: (_, _, _, _, _) =>
            {
                scanCount++;
                throw new InvalidOperationException("scan should not run while the lock is busy");
            },
            acquireLock: _ => null,
            readLatestRevision: _ => ++pollCount < 2 ? 7 : 8,
            clock: clock,
            requestFullScan: (millerDir, workspaceId, baselineRevision) =>
            {
                requestCount++;
                Assert.Equal(Path.Combine(root, ".miller"), millerDir);
                Assert.Equal("target-ws", workspaceId);
                Assert.Equal(7, baselineRevision);
            });

        WorkspaceRefreshResult result = service.Refresh("target-ws", force: true);

        Assert.Equal(WorkspaceRefreshStatus.Refreshed, result.Status);
        Assert.False(result.Scanned);
        Assert.Equal(8, result.Revision);
        Assert.Equal(0, scanCount);
        Assert.Equal(1, requestCount);
        Assert.Equal(8, registry.Get("target-ws")?.LastRevision);
    }

    [Fact]
    public void Refresh_ForceLockBusy_RevisionAdvanceWithoutArtifactReplacement_IsNotReportedAsRefreshed()
    {
        // The leader may legally service our full-scan request as a DOWNGRADED delta (it evaluates the
        // request without bypassBackoff, and a usable artifact downgrades UserFullRebuild). A delta bumps the
        // revision WITHOUT replacing the artifact, so a force waiter that accepts a bare revision advance
        // reports "refreshed" for a rebuild that never ran. With a readable baseline artifact_id, only a
        // CHANGED artifact_id confirms the force; the timeout must say the rebuild is still owed.
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("busy-force-downgraded");
        string dbPath = Path.Combine(root, ".miller", "symbols.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        File.WriteAllText(dbPath, "readable index placeholder");
        registry.UpsertSeen("target-ws", "target-111111111111", root, dbPath);
        registry.MarkScanned("target-ws", revision: 7);
        int pollCount = 0;
        var clock = new FakeClock();
        var service = NewService(
            registry,
            scan: (_, _, _, _, _) => throw new InvalidOperationException("scan should not run while the lock is busy"),
            acquireLock: _ => null,
            readLatestRevision: _ => ++pollCount < 2 ? 7 : 8,
            clock: clock,
            requestFullScan: (_, _, _) => { },
            readArtifactId: _ => "artifact-unchanged");

        WorkspaceRefreshResult result = service.Refresh("target-ws", force: true);

        Assert.Equal(WorkspaceRefreshStatus.LockBusy, result.Status);
        Assert.False(result.Scanned);
        Assert.Equal(8, result.Revision);
        Assert.Contains("still owed", result.WarningText);
        Assert.Contains("scan_failure", result.WarningText);
        Assert.Equal(7, registry.Get("target-ws")?.LastRevision);
    }

    [Fact]
    public void Refresh_ForceLockBusy_LeaderPromotedAFreshArtifactWithARestartedCounter_ReturnsRefreshed()
    {
        // The leader serviced our full-scan request with a build-to-temp PROMOTE (FullRebuildPromotion): the
        // fresh artifact's revision counter RESTARTED below the baseline, so the revision comparison alone can
        // never confirm the rebuild — the changed artifact_id must (2026-06-11 Eros field report #2).
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("busy-force-promote");
        string dbPath = Path.Combine(root, ".miller", "symbols.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        File.WriteAllText(dbPath, "readable index placeholder");
        registry.UpsertSeen("target-ws", "target-111111111111", root, dbPath);
        registry.MarkScanned("target-ws", revision: 7);
        int idReads = 0;
        var clock = new FakeClock();
        var service = NewService(
            registry,
            scan: (_, _, _, _, _) => throw new InvalidOperationException("scan should not run while the lock is busy"),
            acquireLock: _ => null,
            readLatestRevision: _ => 1, // the promoted fresh artifact restarted its counter BELOW the baseline
            clock: clock,
            requestFullScan: (_, _, _) => { },
            readArtifactId: _ => ++idReads == 1 ? "artifact-old" : "artifact-new"); // baseline read, then polls

        WorkspaceRefreshResult result = service.Refresh("target-ws", force: true);

        Assert.Equal(WorkspaceRefreshStatus.Refreshed, result.Status);
        Assert.False(result.Scanned);
        Assert.Equal(1, result.Revision);
        Assert.Equal(1, registry.Get("target-ws")?.LastRevision); // MarkScanned followed the rebuilt artifact
    }

    [Fact]
    public void Refresh_DeltaLockBusy_DoesNotRequestLeaderFullScan()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("busy-delta-no-request");
        string dbPath = Path.Combine(root, ".miller", "symbols.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        File.WriteAllText(dbPath, "readable index placeholder");
        registry.UpsertSeen("target-ws", "target-111111111111", root, dbPath);
        registry.MarkScanned("target-ws", revision: 7);
        int requestCount = 0;
        var clock = new FakeClock();
        var service = NewService(
            registry,
            scan: (_, _, _, _, _) => throw new InvalidOperationException("scan should not run while the lock is busy"),
            acquireLock: _ => null,
            readLatestRevision: _ => 7,
            clock: clock,
            requestFullScan: (_, _, _) => requestCount++);

        WorkspaceRefreshResult result = service.Refresh("target-ws");

        Assert.Equal(WorkspaceRefreshStatus.LockBusy, result.Status);
        Assert.Equal(0, requestCount);
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
            scan: (_, _, _, _, _) =>
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
            scan: (_, _, _, _, _) =>
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
            scan: (_, _, _, _, _) => throw new InvalidOperationException("scan should not run for a missing root"),
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
            scan: (_, _, _, _, _) => throw new InvalidOperationException("scan should not run for a sensitive root"),
            acquireLock: _ => throw new InvalidOperationException("lock should not be acquired for a sensitive root"));

        WorkspaceRefreshResult result = service.Refresh("target-ws");

        Assert.Equal(WorkspaceRefreshStatus.Failed, result.Status);
        Assert.Equal("failed", result.StatusText);
        Assert.False(result.Scanned);
        Assert.Equal(WorkspaceRegistryState.Error, registry.Get("target-ws")?.State);
        Assert.Contains("sensitive system path", registry.Get("target-ws")?.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public void Refresh_RecordsScanAndTotalDurations_OnlyWhenAScanActuallyRan()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("durations");
        string dbPath = Path.Combine(root, ".miller", "symbols.db");
        registry.UpsertSeen("target-ws", "target-111111111111", root, dbPath);

        // Scanned: both durations are measured (real wall clock — assert presence, not magnitude).
        WorkspaceRefreshResult scanned = NewService(
            registry,
            scan: (_, _, _, _, _) => Report(root, dbPath, "target-ws", revision: 5),
            acquireLock: _ => new NoopLease()).Refresh("target-ws");
        Assert.Equal(WorkspaceRefreshStatus.Refreshed, scanned.Status);
        Assert.NotNull(scanned.ScanDuration);
        Assert.NotNull(scanned.TotalDuration);

        // Failed scan: the duration of the thrown attempt is KEPT — a fleet sweep needs it to tell a timeout
        // kill (~the timeout) from an instant hard failure.
        WorkspaceRefreshResult failed = NewService(
            registry,
            scan: (_, _, _, _, _) => throw new JulieExtractException("boom", standardError: string.Empty),
            acquireLock: _ => new NoopLease()).Refresh("target-ws");
        Assert.Equal(WorkspaceRefreshStatus.Failed, failed.Status);
        Assert.NotNull(failed.ScanDuration);
        Assert.NotNull(failed.TotalDuration);

        // Lock busy: no scan ran, so scan duration is null; the wait itself is still measured as the total.
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        File.WriteAllText(dbPath, string.Empty);
        WorkspaceRefreshResult busy = NewService(
            registry,
            scan: (_, _, _, _, _) => throw new InvalidOperationException("scan must not run when the lock is busy"),
            acquireLock: _ => null).Refresh("target-ws");
        Assert.Equal(WorkspaceRefreshStatus.LockBusy, busy.Status);
        Assert.Null(busy.ScanDuration);
        Assert.NotNull(busy.TotalDuration);
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
            scan: (scanRoot, scanDb, force, _, _) =>
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

    [Fact]
    public void Refresh_SidecarEnabled_BuildsSearchDbNextToTheScannedIndexAtTheScannedRevision()
    {
        using var julie = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                new JulieDbFixture.SymbolRow("s1", "IAuthenticationProvider", "interface", "csharp",
                    "src/Auth.cs", "public interface IAuthenticationProvider", 1, ParentId: null),
                new JulieDbFixture.SymbolRow("s2", "Cache", "class", "csharp",
                    "src/Cache.cs", "public class Cache", 1, ParentId: null),
            });
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        registry.UpsertSeen("target-ws", "target-111111111111", julie.WorkspaceRoot, julie.DbPath);
        registry.MarkScanned("target-ws", revision: 1);
        var service = NewService(
            registry,
            scan: (_, _, _, _, _) => Report(julie.WorkspaceRoot, julie.DbPath, "target-ws", revision: 5),
            acquireLock: _ => new NoopLease(),
            sidecar: new SymbolSearchSidecar(enabled: true));

        WorkspaceRefreshResult result = service.Refresh("target-ws");

        Assert.Equal(WorkspaceRefreshStatus.Refreshed, result.Status);
        string searchDb = SymbolSearchSidecar.SearchDbPathFor(julie.DbPath);
        Assert.True(File.Exists(searchDb));
        var index = FtsSymbolSearchIndex.Open(searchDb);
        Assert.Equal(5L, index.Revision);
        Assert.Equal(2, index.DocumentCount);
    }

    [Fact]
    public void Refresh_BuildsContentDbNextToTheScannedIndexAtTheScannedRevision()
    {
        using var julie = JulieSourceDb("KnownSourceError");
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        registry.UpsertSeen("target-ws", "target-111111111111", julie.WorkspaceRoot, julie.DbPath);
        registry.MarkScanned("target-ws", revision: 1);
        var service = NewService(
            registry,
            scan: (_, _, _, _, _) => Report(julie.WorkspaceRoot, julie.DbPath, "target-ws", revision: 5),
            acquireLock: _ => new NoopLease());

        WorkspaceRefreshResult result = service.Refresh("target-ws");

        Assert.Equal(WorkspaceRefreshStatus.Refreshed, result.Status);
        string contentDb = ContentCorpusSidecar.ContentDbPathFor(julie.DbPath);
        Assert.True(File.Exists(contentDb));
        var index = FtsTextContentSearchIndex.Open(contentDb, expectedRevision: 5);
        Assert.Single(index.Search("KnownSourceError", TextContentKind.WorkspaceSource, limit: 10));
    }

    [Fact]
    public void Refresh_SidecarDisabled_DoesNotBuildSearchDb()
    {
        using var julie = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                new JulieDbFixture.SymbolRow("s1", "Cache", "class", "csharp",
                    "src/Cache.cs", "public class Cache", 1, ParentId: null),
            });
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        registry.UpsertSeen("target-ws", "target-111111111111", julie.WorkspaceRoot, julie.DbPath);
        registry.MarkScanned("target-ws", revision: 1);
        var service = NewService(
            registry,
            scan: (_, _, _, _, _) => Report(julie.WorkspaceRoot, julie.DbPath, "target-ws", revision: 5),
            acquireLock: _ => new NoopLease());   // default sidecar = Disabled

        WorkspaceRefreshResult result = service.Refresh("target-ws");

        Assert.Equal(WorkspaceRefreshStatus.Refreshed, result.Status);
        Assert.False(File.Exists(SymbolSearchSidecar.SearchDbPathFor(julie.DbPath)));
    }

    [Fact]
    public void Refresh_SidecarEnabledUnchangedStatusButArtifactMissing_StillBuildsSidecar()
    {
        // The flag flips on for an already-scanned workspace whose search.db doesn't exist yet: an Unchanged
        // refresh (no new revision) must STILL build the missing artifact, not gate the build on Refreshed.
        using var julie = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                new JulieDbFixture.SymbolRow("s1", "Cache", "class", "csharp",
                    "src/Cache.cs", "public class Cache", 1, ParentId: null),
            });
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        registry.UpsertSeen("target-ws", "target-111111111111", julie.WorkspaceRoot, julie.DbPath);
        registry.MarkScanned("target-ws", revision: 5);
        var service = NewService(
            registry,
            scan: (_, _, _, _, _) => NoChangeReport(julie.WorkspaceRoot, julie.DbPath, "target-ws", revision: 5),
            acquireLock: _ => new NoopLease(),
            sidecar: new SymbolSearchSidecar(enabled: true));

        WorkspaceRefreshResult result = service.Refresh("target-ws");

        Assert.Equal(WorkspaceRefreshStatus.Unchanged, result.Status);
        string searchDb = SymbolSearchSidecar.SearchDbPathFor(julie.DbPath);
        Assert.True(File.Exists(searchDb));
        Assert.Equal(5L, FtsSymbolSearchIndex.Open(searchDb).Revision);
    }

    [Fact]
    public void Refresh_SidecarEnabledButBuildFails_StillReportsRefreshedAndDoesNotThrow()
    {
        // A successful scan must never be undone by a sidecar build failure: point the index path at a
        // non-julie file so the build's symbol read throws — the refresh must still report Refreshed.
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("sidecar-build-fails");
        string dbPath = Path.Combine(root, ".miller", "symbols.db");
        registry.UpsertSeen("target-ws", "target-111111111111", root, dbPath);
        registry.MarkScanned("target-ws", revision: 1);
        var service = NewService(
            registry,
            scan: (_, scanDb, _, _, _) =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(scanDb)!);
                File.WriteAllText(scanDb, "not a sqlite database");
                return Report(root, dbPath, "target-ws", revision: 5);
            },
            acquireLock: _ => new NoopLease(),
            sidecar: new SymbolSearchSidecar(enabled: true));

        WorkspaceRefreshResult result = service.Refresh("target-ws");

        Assert.Equal(WorkspaceRefreshStatus.Refreshed, result.Status);
        Assert.True(result.Scanned);
        Assert.Equal(5, result.Revision);
        Assert.False(File.Exists(SymbolSearchSidecar.SearchDbPathFor(dbPath)));
    }

    // ---- version-aware leadership: the one-shot eligibility gate (D2 CLI-side) ----

    [Fact]
    public void Refresh_IneligibleExtractor_RefusesScanWithReasonAndRemedy()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("ineligible");
        string dbPath = Path.Combine(root, ".miller", "symbols.db");
        registry.UpsertSeen("target-ws", "target-111111111111", root, dbPath);
        registry.MarkScanned("target-ws", revision: 1);
        int scanCount = 0;
        var service = NewService(
            registry,
            scan: (_, _, _, _, _) => { scanCount++; return Report(root, dbPath, "target-ws", revision: 5); },
            acquireLock: _ => new NoopLease(),
            eligibilityGate: db =>
            {
                Assert.Equal(dbPath, db);
                return LeadershipEligibility.Evaluate("2.1.3", "2.3.0", allowDowngrade: false);
            });

        WorkspaceRefreshResult result = service.Refresh("target-ws", force: true);

        Assert.Equal(WorkspaceRefreshStatus.IneligibleExtractor, result.Status);
        Assert.False(result.Scanned);
        Assert.Equal(0, scanCount);
        Assert.Contains("older", result.Error);
        Assert.Contains("restore-julie-extract", result.Error);
        Assert.Contains("MILLER_ALLOW_EXTRACTOR_DOWNGRADE", result.Error);
        // The refusal is not a workspace error: the artifact and registry row stay untouched.
        Assert.Equal(1, registry.Get("target-ws")?.LastRevision);
        Assert.Equal(WorkspaceRegistryState.Ready, registry.Get("target-ws")?.State);
    }

    [Fact]
    public void Refresh_DowngradeOverride_AllowsScanDespiteOlderExtractor()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("downgrade-override");
        string dbPath = Path.Combine(root, ".miller", "symbols.db");
        registry.UpsertSeen("target-ws", "target-111111111111", root, dbPath);
        registry.MarkScanned("target-ws", revision: 1);
        int scanCount = 0;
        var service = NewService(
            registry,
            scan: (_, _, _, _, _) => { scanCount++; return Report(root, dbPath, "target-ws", revision: 5); },
            acquireLock: _ => new NoopLease(),
            eligibilityGate: _ => LeadershipEligibility.Evaluate("2.1.3", "2.3.0", allowDowngrade: true));

        WorkspaceRefreshResult result = service.Refresh("target-ws", force: true);

        Assert.Equal(WorkspaceRefreshStatus.Refreshed, result.Status);
        Assert.True(result.Scanned);
        Assert.Equal(1, scanCount);
    }

    [Fact]
    public void Refresh_LockBusy_SkipsGateAndStillRequestsLeaderFullScan()
    {
        // When a live leader holds the lock the one-shot must NOT veto on its own eligibility — the existing
        // enqueue-to-leader behavior stays as is (the live leader enforces its own gate).
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("busy-ineligible");
        string dbPath = Path.Combine(root, ".miller", "symbols.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        File.WriteAllText(dbPath, string.Empty);
        registry.UpsertSeen("target-ws", "target-111111111111", root, dbPath);
        registry.MarkScanned("target-ws", revision: 1);
        bool gateInvoked = false;
        int fullScanRequests = 0;
        var service = NewService(
            registry,
            scan: (_, _, _, _, _) => throw new InvalidOperationException("must not scan while the lock is busy"),
            acquireLock: _ => null,
            readLatestRevision: _ => 1,
            requestFullScan: (_, _, _) => fullScanRequests++,
            eligibilityGate: _ =>
            {
                gateInvoked = true;
                return LeadershipEligibility.Evaluate("2.1.3", "2.3.0", allowDowngrade: false);
            });

        WorkspaceRefreshResult result = service.Refresh("target-ws", force: true);

        Assert.Equal(WorkspaceRefreshStatus.LockBusy, result.Status);
        Assert.False(gateInvoked);
        Assert.Equal(1, fullScanRequests);
    }

    // ---- W3: machine-wide scan admission ----
    // The per-workspace writer lock is per-workspace by construction, so N worktrees each acquire their own and
    // run N concurrent whole-repo extracts. Admission is user-global and capacity 1; a refused refresh must serve
    // the latest readable DB (lock_busy) rather than scan ungoverned, and must NOT poison the registry row.

    private string NewMillerHome() =>
        Directory.CreateDirectory(Path.Combine(_dir, "home-" + Guid.NewGuid().ToString("N"))).FullName;

    // A root whose symbols.db already exists: a governor refusal against it genuinely serves a readable index,
    // which is what lock_busy claims. A root WITHOUT one takes the missing-index branch instead.
    private string NewRootWithIndex(string name, out string dbPath)
    {
        string root = NewRoot(name);
        dbPath = Path.Combine(root, ".miller", "symbols.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        File.WriteAllText(dbPath, "not-a-real-sqlite-file");
        return root;
    }

    private static ScanGovernorLease HoldMachineScanAdmission(string millerHome) =>
        ScanGovernor.ForMillerHome(millerHome).TryAcquire(
            new ScanGovernorRequest("/repo/other-worktree", "test-holder", 4),
            TimeSpan.Zero,
            CancellationToken.None)
        ?? throw new InvalidOperationException("A fresh temp miller home must have a free scan lease.");

    [Fact]
    public void Refresh_WhenMachineScanAdmissionIsBusy_ReportsLockBusy_WithoutScanningOrPoisoningTheRow()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRootWithIndex("governed", out string dbPath);
        registry.UpsertSeen("target-ws", "target-111111111111", root, dbPath);
        registry.MarkScanned("target-ws", revision: 4);
        string home = NewMillerHome();
        using ScanGovernorLease held = HoldMachineScanAdmission(home);
        bool lockAcquired = false;
        var service = NewService(
            registry,
            scan: (_, _, _, _, _) => throw new InvalidOperationException("must not scan without machine-wide admission"),
            acquireLock: _ =>
            {
                lockAcquired = true;
                return new NoopLease();
            },
            governor: ScanGovernor.ForMillerHome(home),
            governorForceWait: TimeSpan.Zero);

        WorkspaceRefreshResult result = service.Refresh("target-ws", force: true);

        Assert.Equal(WorkspaceRefreshStatus.LockBusy, result.Status);
        Assert.False(result.Scanned);
        Assert.Equal(4, result.Revision);
        Assert.Contains("Machine-wide scan admission is busy", result.WarningText, StringComparison.Ordinal);
        Assert.True(lockAcquired); // the workspace writer lock is taken first, then admission (the lock order)

        WorkspaceRegistryRow row = Assert.IsType<WorkspaceRegistryRow>(registry.Get("target-ws"));
        Assert.Equal(WorkspaceRegistryState.Ready, row.State);
        Assert.Null(row.LastError);
    }

    [Fact]
    public void Refresh_NonForce_WaitsOnlyTheShortLockBusyBudgetForMachineScanAdmission()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRootWithIndex("governed-nonforce", out string dbPath);
        registry.UpsertSeen("target-ws", "target-111111111111", root, dbPath);
        registry.MarkScanned("target-ws", revision: 1);
        string home = NewMillerHome();
        using ScanGovernorLease held = HoldMachineScanAdmission(home);
        var service = NewService(
            registry,
            scan: (_, _, _, _, _) => throw new InvalidOperationException("must not scan without machine-wide admission"),
            acquireLock: _ => new NoopLease(),
            governor: ScanGovernor.ForMillerHome(home),
            governorForceWait: TimeSpan.FromMinutes(30));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        WorkspaceRefreshResult result = service.Refresh("target-ws", force: false);
        stopwatch.Stop();

        Assert.Equal(WorkspaceRefreshStatus.LockBusy, result.Status);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"waited {stopwatch.Elapsed}");
    }

    // Two probes bracket the sidecar convergence. A report with no revision routes through readLatestRevision,
    // which the success path calls after the scan and BEFORE TryConvergeSidecar; a report with no artifact block
    // routes through readArtifactId, which it calls AFTER convergence. The workspace writer lock this method
    // holds is what keeps convergence safe once the machine-wide lease is gone.
    [Fact]
    public void Refresh_ReleasesMachineScanAdmission_AsSoonAsTheScanReturns_NotAfterTheSidecarConverges()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("governed-held");
        string dbPath = Path.Combine(root, ".miller", "symbols.db");
        registry.UpsertSeen("target-ws", "target-111111111111", root, dbPath);
        registry.MarkScanned("target-ws", revision: 1);
        string home = NewMillerHome();
        var probe = ScanGovernor.ForMillerHome(home);
        bool freeDuringScan = true;
        bool freeBeforeConvergence = false;
        var freeAtArtifactIdReads = new List<bool>();

        bool AdmissionIsFree()
        {
            using ScanGovernorLease? attempt = probe.TryAcquire(
                new ScanGovernorRequest("/repo/probe", "probe", 4), TimeSpan.Zero, CancellationToken.None);
            return attempt is not null;
        }

        var service = NewService(
            registry,
            scan: (_, _, _, _, _) =>
            {
                freeDuringScan = AdmissionIsFree();
                return Report(root, dbPath, "target-ws", revision: 5) with
                {
                    Artifact = null,
                    RevisionBlock = null,
                };
            },
            acquireLock: _ => new NoopLease(),
            readLatestRevision: _ =>
            {
                freeBeforeConvergence = AdmissionIsFree();
                return 5;
            },
            readArtifactId: _ =>
            {
                freeAtArtifactIdReads.Add(AdmissionIsFree());
                return "artifact-a";
            },
            governor: ScanGovernor.ForMillerHome(home),
            governorForceWait: TimeSpan.Zero);

        WorkspaceRefreshResult result = service.Refresh("target-ws", force: true);

        Assert.Equal(WorkspaceRefreshStatus.Refreshed, result.Status);
        Assert.False(freeDuringScan); // the extract subprocess is still fully inside the admission
        Assert.True(freeBeforeConvergence); // released the moment the scan returned, before the sidecar build
        Assert.Equal(2, freeAtArtifactIdReads.Count); // one before the scan, one after sidecar convergence
        Assert.Equal(new[] { false, true }, freeAtArtifactIdReads);
        Assert.True(AdmissionIsFree());
    }

    [Fact]
    public void Refresh_WhenMachineScanAdmissionIsBusy_AndNoIndexExists_ReportsMissingIndexRatherThanLockBusy()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("governed-cold");
        string dbPath = Path.Combine(root, ".miller", "symbols.db");
        registry.UpsertSeen("target-ws", "target-111111111111", root, dbPath);
        string home = NewMillerHome();
        using ScanGovernorLease held = HoldMachineScanAdmission(home);
        var requested = new List<string>();
        var service = NewService(
            registry,
            scan: (_, _, _, _, _) => throw new InvalidOperationException("must not scan without machine-wide admission"),
            acquireLock: _ => new NoopLease(),
            requestFullScan: (millerDir, _, _) => requested.Add(millerDir),
            governor: ScanGovernor.ForMillerHome(home),
            governorForceWait: TimeSpan.Zero);

        WorkspaceRefreshResult result = service.Refresh("target-ws", force: true);

        Assert.Equal(WorkspaceRefreshStatus.MissingIndex, result.Status);
        Assert.Equal(3, CliDispatch.RefreshExitCode(result.Status));
        Assert.False(result.Scanned);
        Assert.Single(requested);

        WorkspaceRegistryRow row = Assert.IsType<WorkspaceRegistryRow>(registry.Get("target-ws"));
        Assert.Equal(WorkspaceRegistryState.Missing, row.State);
        Assert.NotNull(row.LastError);
    }

    [Fact]
    public void Refresh_WhenMachineScanAdmissionIsBusy_QueuesALeaderFullScanForTheForcedRequest()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRootWithIndex("governed-queue", out string dbPath);
        registry.UpsertSeen("target-ws", "target-111111111111", root, dbPath);
        registry.MarkScanned("target-ws", revision: 7);
        string home = NewMillerHome();
        using ScanGovernorLease held = HoldMachineScanAdmission(home);
        var requested = new List<(string MillerDir, string WorkspaceId, long Baseline)>();
        var service = NewService(
            registry,
            scan: (_, _, _, _, _) => throw new InvalidOperationException("must not scan without machine-wide admission"),
            acquireLock: _ => new NoopLease(),
            requestFullScan: (millerDir, id, baseline) => requested.Add((millerDir, id, baseline)),
            governor: ScanGovernor.ForMillerHome(home),
            governorForceWait: TimeSpan.Zero);

        WorkspaceRefreshResult result = service.Refresh("target-ws", force: true);

        Assert.Equal(WorkspaceRefreshStatus.LockBusy, result.Status);
        Assert.Equal(("target-ws", 7L), (requested.Single().WorkspaceId, requested.Single().Baseline));
    }

    [Fact]
    public void Refresh_NonForceGovernorRefusal_QueuesNothing()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRootWithIndex("governed-noqueue", out string dbPath);
        registry.UpsertSeen("target-ws", "target-111111111111", root, dbPath);
        registry.MarkScanned("target-ws", revision: 7);
        string home = NewMillerHome();
        using ScanGovernorLease held = HoldMachineScanAdmission(home);
        var requested = new List<string>();
        var service = NewService(
            registry,
            scan: (_, _, _, _, _) => throw new InvalidOperationException("must not scan without machine-wide admission"),
            acquireLock: _ => new NoopLease(),
            requestFullScan: (millerDir, _, _) => requested.Add(millerDir),
            governor: ScanGovernor.ForMillerHome(home),
            governorForceWait: TimeSpan.Zero);

        WorkspaceRefreshResult result = service.Refresh("target-ws", force: false);

        Assert.Equal(WorkspaceRefreshStatus.LockBusy, result.Status);
        Assert.Empty(requested);
    }

    [Fact]
    public void Refresh_ForcedCallerBudget_OverridesTheServiceDefault()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRootWithIndex("governed-budget", out string dbPath);
        registry.UpsertSeen("target-ws", "target-111111111111", root, dbPath);
        registry.MarkScanned("target-ws", revision: 1);
        string home = NewMillerHome();
        using ScanGovernorLease held = HoldMachineScanAdmission(home);
        var service = NewService(
            registry,
            scan: (_, _, _, _, _) => throw new InvalidOperationException("must not scan without machine-wide admission"),
            acquireLock: _ => new NoopLease(),
            governor: ScanGovernor.ForMillerHome(home),
            governorForceWait: TimeSpan.FromMinutes(30));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        WorkspaceRefreshResult result = service.Refresh(
            "target-ws", force: true, ScanAdmissionBudget.Of(TimeSpan.Zero));
        stopwatch.Stop();

        Assert.Equal(WorkspaceRefreshStatus.LockBusy, result.Status);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"waited {stopwatch.Elapsed}");
    }

    [Fact]
    public void Refresh_ForcedCallerBudget_WithACancelledToken_DegradesToLockBusyRatherThanThrowing()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRootWithIndex("governed-cancelled", out string dbPath);
        registry.UpsertSeen("target-ws", "target-111111111111", root, dbPath);
        registry.MarkScanned("target-ws", revision: 1);
        string home = NewMillerHome();
        using ScanGovernorLease held = HoldMachineScanAdmission(home);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var service = NewService(
            registry,
            scan: (_, _, _, _, _) => throw new InvalidOperationException("must not scan without machine-wide admission"),
            acquireLock: _ => new NoopLease(),
            governor: ScanGovernor.ForMillerHome(home),
            governorForceWait: TimeSpan.FromMinutes(30));

        WorkspaceRefreshResult result = service.Refresh(
            "target-ws",
            force: true,
            new ScanAdmissionBudget(TimeSpan.FromMinutes(30), cancelled.Token));

        Assert.Equal(WorkspaceRefreshStatus.LockBusy, result.Status);
    }

    private CrossWorkspaceRefreshService NewService(
        WorkspaceRegistry registry,
        Func<string, string, bool, int?, ExtractIndexLevel, ExtractReport> scan,
        Func<string, IDisposable?> acquireLock,
        Func<string, long>? readLatestRevision = null,
        FakeClock? clock = null,
        SymbolSearchSidecar? sidecar = null,
        Action<string, string, long>? requestFullScan = null,
        Func<string, LeadershipVerdict>? eligibilityGate = null,
        Func<string, string?>? readArtifactId = null,
        ScanGovernor? governor = null,
        TimeSpan? governorForceWait = null,
        Func<string, string, IScanFailurePolicy>? failurePolicyFor = null,
        IJulieStoreClient? storeClient = null,
        Func<bool>? storeEnabled = null,
        Action<string>? deleteStorePointer = null)
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
            utcNow: clock.UtcNow,
            sidecar: sidecar ?? SymbolSearchSidecar.Disabled,
            requestFullScan: requestFullScan,
            eligibilityGate: eligibilityGate,
            readArtifactId: readArtifactId ?? (_ => null),
            governor: governor,
            governorForceWait: governorForceWait,
            failurePolicyFor: failurePolicyFor,
            storeClient: storeClient,
            storeEnabled: storeEnabled,
            deleteStorePointer: deleteStorePointer);
    }

    [Fact]
    public void Refresh_WithoutTheDirectUserBypass_HonorsThePersistedScanBackoffInsteadOfRespawningTheExtractor()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRootWithIndex("throttled", out string dbPath);
        registry.UpsertSeen("target-ws", "target-111111111111", root, dbPath);
        registry.MarkScanned("target-ws", revision: 7);
        DateTimeOffset now = new(2026, 8, 2, 0, 0, 0, TimeSpan.Zero);
        var policy = new InMemoryScanFailurePolicy(utcNow: () => now, jitter: static () => 0);
        policy.RecordFailure(ScanIntent.IncrementalReconcile, ScanFailurePolicy.SigkillExitCode, jobs: 4);
        int scanCount = 0;
        var service = NewService(
            registry,
            scan: (_, _, _, _, _) =>
            {
                scanCount++;
                return Report(root, dbPath, "target-ws", revision: 9);
            },
            acquireLock: _ => new NoopLease(),
            failurePolicyFor: (_, _) => policy);

        WorkspaceRefreshResult deferred = service.Refresh("target-ws");

        Assert.Equal(WorkspaceRefreshStatus.LockBusy, deferred.Status);
        Assert.False(deferred.Scanned);
        Assert.Equal(0, scanCount);
        Assert.Equal(WorkspaceRegistryState.Ready, registry.Get("target-ws")?.State);

        WorkspaceRefreshResult direct = service.Refresh("target-ws", bypassBackoff: true);

        Assert.Equal(WorkspaceRefreshStatus.Refreshed, direct.Status);
        Assert.Equal(1, scanCount);
    }

    [Fact]
    public void Refresh_AForcedRebuildThatDowngrades_SaysSoRatherThanReportingTheRebuildAsDone()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRootWithIndex("downgraded", out string dbPath);
        registry.UpsertSeen("target-ws", "target-111111111111", root, dbPath);
        registry.MarkScanned("target-ws", revision: 7);
        DateTimeOffset now = new(2026, 8, 2, 0, 0, 0, TimeSpan.Zero);
        var policy = new InMemoryScanFailurePolicy(
            priorArtifactUsable: () => true, utcNow: () => now, jitter: static () => 0);
        policy.RecordFailure(ScanIntent.UserFullRebuild, ScanFailurePolicy.SigkillExitCode, jobs: 4);
        now += ScanFailurePolicy.MaxJitteredBackoffFor(1);
        bool? ranForced = null;
        var service = NewService(
            registry,
            scan: (_, _, force, _, _) =>
            {
                ranForced = force;
                return Report(root, dbPath, "target-ws", revision: 9);
            },
            acquireLock: _ => new NoopLease(),
            failurePolicyFor: (_, _) => policy);

        WorkspaceRefreshResult result = service.Refresh("target-ws", force: true);

        Assert.False(ranForced);
        Assert.Contains("downgraded", result.WarningText ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, policy.Read()?.ConsecutiveFailures);
    }

    [Fact]
    public void WorkspaceIndexProviderAutomaticRefresh_HonorsTheBackoff_SoReadTrafficCannotRespawnTheExtractor()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRootWithIndex("read-traffic", out string dbPath);
        registry.UpsertSeen("target-ws", "target-111111111111", root, dbPath);
        registry.MarkScanned("target-ws", revision: 7);
        DateTimeOffset now = new(2026, 8, 2, 0, 0, 0, TimeSpan.Zero);
        var policy = new InMemoryScanFailurePolicy(utcNow: () => now, jitter: static () => 0);
        policy.RecordFailure(ScanIntent.IncrementalReconcile, ScanFailurePolicy.SigkillExitCode, jobs: 4);
        int scanCount = 0;
        var service = NewService(
            registry,
            scan: (_, _, _, _, _) =>
            {
                scanCount++;
                return Report(root, dbPath, "target-ws", revision: 9);
            },
            acquireLock: _ => new NoopLease(),
            failurePolicyFor: (_, _) => policy);

        Func<string, WorkspaceRefreshResult> automatic = WorkspaceIndexProvider.AutomaticRefresh(service);

        for (int i = 0; i < 10; i++)
            Assert.Equal(WorkspaceRefreshStatus.LockBusy, automatic("target-ws").Status);

        Assert.Equal(0, scanCount);
    }

    [Fact]
    public void Refresh_ADeltaThatSucceeds_LeavesAForcedRebuildsFailureRecordForTheResidentLeader()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRootWithIndex("routine-refresh", out string dbPath);
        registry.UpsertSeen("target-ws", "target-111111111111", root, dbPath);
        registry.MarkScanned("target-ws", revision: 7);
        DateTimeOffset now = new(2026, 8, 2, 0, 0, 0, TimeSpan.Zero);
        var policy = new InMemoryScanFailurePolicy(utcNow: () => now, jitter: static () => 0);
        policy.RecordFailure(ScanIntent.UserFullRebuild, ScanFailurePolicy.SigkillExitCode, jobs: 4);
        var service = NewService(
            registry,
            scan: (_, _, _, _, _) => Report(root, dbPath, "target-ws", revision: 9),
            acquireLock: _ => new NoopLease(),
            failurePolicyFor: (_, _) => policy);

        service.Refresh("target-ws", bypassBackoff: true);

        Assert.Equal(1, policy.Read()?.ConsecutiveFailures);

        service.Refresh("target-ws", force: true, bypassBackoff: true);

        Assert.Null(policy.Read());
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

    // julie returns status=="no_change" when a scan wrote nothing; Miller's unchanged verdict must come from
    // THIS, not from a revision comparison (a force rebuild restarts the revision counter — see below).
    private static ExtractReport NoChangeReport(string root, string dbPath, string workspaceId, long revision) =>
        Report(root, dbPath, workspaceId, revision) with { Status = "no_change" };

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

    private static ReportDiagnostic SlowFileWarning(string root) =>
        new(
            "slow_file_skipped",
            "file exceeded extraction timeout",
            Path.Combine(root, "Generated", "Slow.kt"),
            "Generated/Slow.kt",
            Recoverable: true);

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

    private sealed class NoopLease : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class UnexpectedStoreClient : IJulieStoreClient
    {
        public StoreRequestResult Submit(StoreRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The malformed pointer must be rejected before invoking julie-extract.");
    }

    private sealed class FakeClock
    {
        private DateTimeOffset _now = new(2026, 5, 31, 12, 0, 0, TimeSpan.Zero);

        public DateTimeOffset UtcNow() => _now;

        public void Sleep(TimeSpan delay) => _now += delay;
    }
}
