using System.Threading;
using Microsoft.Data.Sqlite;
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
/// Pins the admission boundary the 2026-08-06 P4 scale validation (§3) moved: the machine-wide scan governor
/// covers the <c>julie-extract</c> subprocess and NOTHING after it. Every leader path used to hold the
/// user-global lease through sidecar convergence — ~200s of per-workspace SQLite work — which serialized an
/// 8-worktree fleet on one queue and starved the last bootstrap past its admission cap. So each governed site
/// must release admission the moment the scan returns, while still running the scan itself fully inside it
/// (the one-extractor invariant) and still running convergence inside <c>_opsGate</c>.
///
/// <para>Pure: fake ops, no subprocess, and a temp miller home per test so the real user-global
/// <c>~/.miller/scan</c> lease is never touched.</para>
/// </summary>
public sealed class IndexerSidecarAdmissionTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (string dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // ---- the governed leader sites ----

    [Fact]
    public void DrainRescan_RunsTheScanUnderAdmission_AndConvergesTheSidecarAfterReleasingIt()
    {
        using var julie = SeededJulieDb();
        string home = NewMillerHome();
        WorkspaceContext workspace = WorkspaceWithDb(julie.DbPath);
        IndexerService service = NewGovernedService(home, workspace);
        AdmissionProbe probe = AttachProbe(service, home, workspace);
        service.PublishOpsForTest(new FakeScanOps { WhileScanning = probe.RecordDuringScan });
        service.RequestWholeRepoScanForTest(ScanIntent.UserFullRebuild);

        service.RunDrainTickForTest(Path.Combine(home, "requests"));

        AssertScanHeldAdmissionAndConvergenceDidNot(probe);
    }

    [Fact]
    public void OnDemandScan_RunsTheScanUnderAdmission_AndConvergesTheSidecarAfterReleasingIt()
    {
        using var julie = SeededJulieDb();
        string home = NewMillerHome();
        WorkspaceContext workspace = WorkspaceWithDb(julie.DbPath);
        IndexerService service = NewGovernedService(home, workspace);
        AdmissionProbe probe = AttachProbe(service, home, workspace);
        service.PublishOpsForTest(new FakeScanOps { WhileScanning = probe.RecordDuringScan });

        ScanOutcome outcome = service.TryScanAsLeader(ScanIntent.IncrementalReconcile, bypassBackoff: true);

        Assert.Equal(ScanOutcome.Kind.Scanned, outcome.Result);
        AssertScanHeldAdmissionAndConvergenceDidNot(probe);
    }

    [Fact]
    public void LeaderRequestedFullScan_RunsTheScanUnderAdmission_AndConvergesTheSidecarAfterReleasingIt()
    {
        using var julie = SeededJulieDb();
        string home = NewMillerHome();
        WorkspaceContext workspace = WorkspaceWithDb(julie.DbPath);
        IndexerService service = NewGovernedService(
            home, workspace, drainFullScanRequests: _ => new FullScanDrainResult(true, 0, 0));
        AdmissionProbe probe = AttachProbe(service, home, workspace);
        service.PublishOpsForTest(new FakeScanOps { WhileScanning = probe.RecordDuringScan });

        Assert.True(service.ProcessLeaderFullScanRequestsForTest(Path.Combine(home, "requests")));

        AssertScanHeldAdmissionAndConvergenceDidNot(probe);
    }

    [Fact]
    public void ExtractorUpgradeRescan_RunsTheScanUnderAdmission_AndConvergesTheSidecarAfterReleasingIt()
    {
        using var julie = SeededJulieDb();
        string home = NewMillerHome();
        WorkspaceContext workspace = WorkspaceWithDb(julie.DbPath);
        IndexerService service = NewGovernedService(home, workspace, ownExtractorVersion: () => "2.27.0");
        AdmissionProbe probe = AttachProbe(service, home, workspace);
        service.PublishOpsForTest(new FakeScanOps { WhileScanning = probe.RecordDuringScan });

        service.RunExtractorUpgradeRescanForTest();

        AssertScanHeldAdmissionAndConvergenceDidNot(probe);
    }

    [Fact]
    public void StartupDeltaScan_RunsTheScanUnderAdmission_AndConvergesTheSidecarAfterReleasingIt()
    {
        using var julie = SeededJulieDb();
        string home = NewMillerHome();
        WorkspaceContext workspace = WorkspaceWithDb(julie.DbPath);
        IndexerService service = NewGovernedService(home, workspace);
        AdmissionProbe probe = AttachProbe(service, home, workspace);
        service.PublishOpsForTest(new FakeScanOps { WhileScanning = probe.RecordDuringScan });

        service.RunStartupDeltaScanForTest(workspace);

        AssertScanHeldAdmissionAndConvergenceDidNot(probe);
    }

    // ---- the release itself ----

    [Fact]
    public void ScanGovernorAdmission_Dispose_IsIdempotent_SoAnEarlyReleaseLeavesTheUsingEpilogueANoOp()
    {
        string home = NewMillerHome();
        var governor = ScanGovernor.ForMillerHome(home);
        var state = new ScanGovernorState();
        var request = new ScanGovernorRequest("/repo/idempotent", "test", 4);
        ScanGovernorAdmission admission = Assert.IsType<ScanGovernorAdmission>(
            ScanGovernorAdmission.TryAcquire(governor, state, request, TimeSpan.Zero, CancellationToken.None));

        admission.Dispose();
        Assert.Null(state.Snapshot(request.WorkspaceRoot));
        Assert.True(MachineAdmissionIsFree(home));

        // A second acquire between the two disposes: a non-idempotent Dispose would tear the SECOND holder's
        // lease and state entry down, which is exactly what the `using` epilogue would do at every governed site.
        using ScanGovernorAdmission? sibling = ScanGovernorAdmission.TryAcquire(
            governor, state, request, TimeSpan.Zero, CancellationToken.None);
        Assert.NotNull(sibling);

        admission.Dispose();

        Assert.Equal(ScanGovernorStates.Holding, state.Snapshot(request.WorkspaceRoot)?.State);
        Assert.False(MachineAdmissionIsFree(home));
    }

    // ---- shared assertion ----

    // The whole contract in one place: the extract subprocess ran with the machine-wide lease held (the
    // one-extractor invariant), convergence actually happened, and by then the lease AND this process's
    // published position were both gone.
    private static void AssertScanHeldAdmissionAndConvergenceDidNot(AdmissionProbe probe)
    {
        Assert.True(probe.ScanObserved, "the governed site never ran a scan");
        Assert.Equal(ScanGovernorStates.Holding, probe.PositionDuringScan?.State);
        Assert.False(probe.LeaseFreeDuringScan, "the scan ran without holding machine-wide admission");

        Assert.True(probe.ConvergeObserved, "the governed site never converged a sidecar");
        Assert.Null(probe.PositionDuringConverge);
        Assert.True(probe.LeaseFreeDuringConverge, "sidecar convergence still held machine-wide admission");
    }

    // ---- probe ----

    /// <summary>
    /// Reads BOTH admission facts at a moment in time: the OS lease (a second governor over the same miller home
    /// can only take it when the real holder released it) and this process's published
    /// <see cref="ScanGovernorState.Shared"/> position, which status/health render.
    /// </summary>
    private sealed class AdmissionProbe(string millerHome, string workspaceRoot)
    {
        public bool ScanObserved { get; private set; }
        public ScanGovernorSnapshot? PositionDuringScan { get; private set; }
        public bool LeaseFreeDuringScan { get; private set; }

        public bool ConvergeObserved { get; private set; }
        public ScanGovernorSnapshot? PositionDuringConverge { get; private set; }
        public bool LeaseFreeDuringConverge { get; private set; }

        public void RecordDuringScan()
        {
            ScanObserved = true;
            PositionDuringScan = ScanGovernorState.Shared.Snapshot(workspaceRoot);
            LeaseFreeDuringScan = MachineAdmissionIsFree(millerHome);
        }

        public void RecordDuringConverge()
        {
            ConvergeObserved = true;
            PositionDuringConverge = ScanGovernorState.Shared.Snapshot(workspaceRoot);
            LeaseFreeDuringConverge = MachineAdmissionIsFree(millerHome);
        }
    }

    private static AdmissionProbe AttachProbe(IndexerService service, string home, WorkspaceContext workspace)
    {
        var probe = new AdmissionProbe(home, workspace.CanonicalRoot!);
        service.BeforeSidecarConvergeForTest = probe.RecordDuringConverge;
        return probe;
    }

    // A fresh governor instance, so the re-entrancy guard (which is per instance, per thread) never fires and the
    // OS lease handle is the only thing that can refuse this.
    private static bool MachineAdmissionIsFree(string millerHome)
    {
        using ScanGovernorLease? attempt = ScanGovernor.ForMillerHome(millerHome).TryAcquire(
            new ScanGovernorRequest("/repo/admission-probe", "probe", 4), TimeSpan.Zero, CancellationToken.None);
        return attempt is not null;
    }

    // ---- fakes and fixtures ----

    private sealed class FakeScanOps : IExtractOps
    {
        private const long ScannedRevision = 7;

        public Action? WhileScanning { get; init; }

        public ExtractReport Scan(ScanIntent intent = ScanIntent.IncrementalReconcile, int? jobs = null)
        {
            WhileScanning?.Invoke();
            return Report();
        }

        public ExtractReport Update(string path) => Report();

        public ExtractReport Delete(string path) => Report();

        private static ExtractReport Report() => new(
            ReportSchemaVersion: 1, Status: "ok", Operation: "scan", Mode: "incremental", Input: null,
            Artifact: new ExtractArtifact(
                DbPath: "x", RootPath: "/abs/r", ArtifactId: "a",
                SchemaVersion: MillerExtractContract.ExpectedSchemaVersion,
                ExtractContractVersion: MillerExtractContract.ExpectedExtractContractVersion,
                SqliteSchemaVersion: MillerExtractContract.ExpectedSqliteSchemaVersion,
                JsonlSchemaVersion: 1, HashAlgorithm: MillerExtractContract.ExpectedHashAlgorithm,
                ParserInventoryFingerprint: "p", CapabilitySnapshotFingerprint: "c"),
            Tool: new ExtractTool("julie-extract", "2.27.0"),
            RevisionBlock: new ExtractRevision(ScannedRevision, ScannedRevision),
            Counts: null,
            Errors: Array.Empty<ReportDiagnostic>(), Warnings: Array.Empty<ReportDiagnostic>());
    }

    // A revision row is what makes the drain path's TryConvergeSidecarToLatest reach the converger at all: it
    // converges to FreshnessReader.LatestRevision(), and revision 0 is the "nothing to stamp" early return.
    private static JulieDbFixture SeededJulieDb() => JulieDbFixture.Create(
        JulieDbFixture.PinnedSchema,
        JulieDbFixture.PinnedContract,
        Array.Empty<JulieDbFixture.SymbolRow>(),
        revisions: [new JulieDbFixture.RevisionRow(1)]);

    private string NewMillerHome()
    {
        string home = Path.Combine(Path.GetTempPath(), "miller-admission-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        _tempDirs.Add(home);
        return home;
    }

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

    // A never-started leader: PublishOpsForTest makes it the leader without the cross-process writer lock or a
    // subprocess, and the governor is a REAL lease under a per-test temp home so the probe measures the real
    // thing. The sidecar itself stays disabled — this pins the admission boundary around convergence, not what
    // convergence builds.
    private static IndexerService NewGovernedService(
        string millerHome,
        WorkspaceContext workspace,
        Func<string, FullScanDrainResult>? drainFullScanRequests = null,
        Func<string?>? ownExtractorVersion = null)
    {
        string bootstrapHome = Path.GetDirectoryName(Path.GetDirectoryName(workspace.RegistryDbPath))!;
        var bootstrap = new IndexBootstrapService(NullLogger<IndexBootstrapService>.Instance)
        {
            TestHomeDirectoryOverride = bootstrapHome,
        };
        bootstrap.SeedForTest(
            workspace,
            new IndexHolder(MillerRepositoryIndex.Build(Array.Empty<IndexedSymbol>()), builtRevision: 0));

        return new IndexerService(
            bootstrap,
            NullLogger<IndexerService>.Instance,
            NullLoggerFactory.Instance,
            tryAcquireLeadership: _ => null,
            createOps: static (_, _, _) => throw new InvalidOperationException("not used by this test seam"),
            leaderRetryInterval: TimeSpan.FromHours(1),
            SymbolSearchSidecar.Disabled,
            attachFileWatchers: false,
            drainFullScanRequests: drainFullScanRequests ?? (_ => FullScanDrainResult.Empty),
            drainFileConvergeRequests: _ => FileConvergeDrainResult.Empty,
            ownExtractorVersion: ownExtractorVersion,
            scanGovernor: ScanGovernor.ForMillerHome(millerHome),
            scanGovernorWait: TimeSpan.Zero);
    }
}
