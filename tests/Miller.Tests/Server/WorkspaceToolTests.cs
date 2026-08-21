using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Miller.Core.Freshness;
using Miller.Indexing;
using Miller.Indexing.Semantic;
using Miller.Server;
using Miller.Server.Cli;
using Miller.Server.Hosting;
using Miller.Server.Telemetry;
using Miller.Server.Tools;
using Miller.Server.Workspaces;
using Miller.Tests.Indexing;
using Miller.Tests.Support;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the <c>workspace</c> tool dispatch (M7 decision-1/2/3/7/8) over fakes + a synth index — no live
/// julie-extract, no FileSystemWatcher, no timer-driven hosted loop. The tool's collaborators are real classes
/// driven through their unit seams: a never-started <see cref="IndexerService"/> reports NotLeader (so refresh/
/// full take the non-leader poll-only path), the publish seam makes it the leader; a <see cref="FreshnessService"/>
/// over a synthesized extract DB does a real on-demand poll+swap; a temp <see cref="TelemetryLedger"/> with seeded
/// rows produces the status tool-breakdown. Covers: status assembles + renders both formats; list shows the
/// current workspace; open/remove arg guards (missing path → usage); the remove-live refusal and remove not-found;
/// and the non-leader refresh/full path (poll only, with the honest cannot-force note). The live extract path
/// (full's force-scan + swap on a real repo) is the Scale suite (<see cref="LiveWorkspaceTests"/>).
/// </summary>
[Collection(SemanticActivationEnvironmentCollection.Name)]
public sealed class WorkspaceToolTests : IDisposable
{
    private const string Ws = "ws-tool-001";
    private const string OtherWs = "ws-tool-other-001";

    private readonly List<IDisposable> _disposables = [];
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var d in _disposables)
        {
            try { d.Dispose(); } catch (ObjectDisposedException) { }
        }
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private string NewTempDir(string label)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"miller-wstool-{label}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        dir = PathCanonicalizer.CanonicalizeRoot(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    // Build a real WorkspaceTool over a synthesized extract DB whose .miller dir IS the served workspace root's
    // .miller (so the workspace context's paths are self-consistent for the live-remove safety check). The
    // indexer is never-started (NotLeader) unless the caller publishes ops; the freshness service polls the synth
    // DB on demand; the ledger is a temp DB the test can seed.
    private (WorkspaceTool tool, IndexerService indexer, TelemetryLedger ledger, string root) BuildTool(
        JulieDbFixture fx, long builtRevision, string? workspaceId)
    {
        WorkspaceToolHarness harness = BuildHarness(fx, builtRevision, workspaceId);
        return (harness.Tool, harness.Indexer, harness.Ledger, harness.Root);
    }

    private WorkspaceToolHarness BuildHarness(
        JulieDbFixture fx,
        long builtRevision,
        string? workspaceId,
        Func<string, string, bool, int?, ExtractIndexLevel, ExtractReport>? crossWorkspaceScan = null,
        Func<string, IDisposable?>? acquireLock = null,
        Func<string, long>? readLatestRevision = null,
        IDashboardLauncher? dashboardLauncher = null,
        VectorSidecar? vectors = null,
        SemanticEmbeddingSessionBroker? semanticBroker = null,
        ScanGovernor? scanGovernor = null,
        Func<string, WorkspaceOpenPrimeEnqueueResult>? enqueueOpenPrime = null,
        IndexHolder? holderOverride = null)
    {
        // The served workspace root is the fixture dir's parent of .miller; point ExtractDbPath at the fixture DB.
        string root = Path.GetDirectoryName(fx.DbPath)!;
        string home = NewTempDir("home");
        string canonicalRoot = Path.GetFullPath(root);
        var workspace = WorkspaceContext.Create(root, AppContext.BaseDirectory, home) with
        {
            ExtractDbPath = fx.DbPath,
            CanonicalRoot = canonicalRoot,
            CanonicalExtractDbPath = fx.DbPath,
            WorkspaceId = workspaceId,
        };

        var holder = holderOverride
            ?? new IndexHolder(MillerRepositoryIndex.Build(SqliteSymbolReader.Read(fx.DbPath)), builtRevision);

        var bootstrap = new IndexBootstrapService(NullLogger<IndexBootstrapService>.Instance);
        bootstrap.TestHomeDirectoryOverride = home;
        bootstrap.SeedForTest(workspace, holder);

        string indexerHome = NewTempDir("indexer-home");
        var indexerBootstrap = new IndexBootstrapService(NullLogger<IndexBootstrapService>.Instance);
        indexerBootstrap.TestHomeDirectoryOverride = indexerHome;
        var indexer = new IndexerService(
            indexerBootstrap,
            NullLogger<IndexerService>.Instance,
            NullLoggerFactory.Instance,
            tryAcquireLeadership: _ => null,
            createOps: static (_, _, _) => throw new InvalidOperationException("not used by this test seam"),
            leaderRetryInterval: TimeSpan.FromHours(1),
            SymbolSearchSidecar.Disabled,
            attachFileWatchers: false,
            scanGovernor: scanGovernor,
            scanGovernorWait: TimeSpan.Zero);
        var freshness = new FreshnessService(bootstrap, NullLogger<FreshnessService>.Instance);

        var probe = new IndexFreshProbe(
            holder,
            latestRevision: () => freshness.LatestObservedRevision,
            queueEmpty: () => indexer.QueueEmpty);

        var ledger = TelemetryLedger.Open(Path.Combine(NewTempDir("ledger"), "telemetry.db"), workspaceId);
        _disposables.Add(ledger);

        var registry = WorkspaceRegistry.Open(workspace.RegistryDbPath);
        _disposables.Add(registry);
        if (!string.IsNullOrWhiteSpace(workspaceId))
        {
            registry.UpsertSeen(
                workspaceId,
                DisplayFor(canonicalRoot, workspaceId),
                canonicalRoot,
                fx.DbPath,
                WorkspaceRegistryState.Current);
            registry.MarkScanned(workspaceId, builtRevision);
        }

        var crossRefresh = new CrossWorkspaceRefreshService(
            registry,
            crossWorkspaceScan ?? ((_, _, _, _, _) => throw new InvalidOperationException("cross-workspace scan was not expected")),
            acquireLock ?? (millerDir => SingleWriterLock.TryAcquire(millerDir)),
            readLatestRevision ?? (_ => 0),
            lockBusyWait: TimeSpan.Zero,
            lockBusyPollInterval: TimeSpan.FromMilliseconds(1),
            sleep: _ => { },
            utcNow: () => DateTimeOffset.UtcNow,
            sidecar: SymbolSearchSidecar.Disabled);
        var tool = new WorkspaceTool(
            holder, workspace, indexer, freshness, probe, bootstrap, ledger, registry, () => crossRefresh,
            SymbolSearchSidecar.Disabled,
            vectors ?? VectorSidecar.Disabled,
            acquireLock ?? (millerDir => SingleWriterLock.TryAcquire(millerDir)),
            enqueueOpenPrime ?? (_ => WorkspaceOpenPrimeEnqueueResult.Stopping),
            dashboardLauncher ?? new RecordingDashboardLauncher(new DashboardLaunchResult(
                DashboardLaunchOutcome.AlreadyRunning,
                new Uri("http://127.0.0.1:4977/workspace?workspace_id=ws-tool-001"),
                ProcessId: null,
                Message: "already running")),
            NullLogger<WorkspaceTool>.Instance,
            semanticBroker,
            scanGovernor);
        return new WorkspaceToolHarness(tool, indexer, ledger, root, workspace, registry, bootstrap);
    }

    private static string DisplayFor(string canonicalRoot, string workspaceId) =>
        workspaceId.Length >= 12 ? WorkspaceId.Display(canonicalRoot, workspaceId) : workspaceId;

    private sealed record WorkspaceToolHarness(
        WorkspaceTool Tool,
        IndexerService Indexer,
        TelemetryLedger Ledger,
        string Root,
        WorkspaceContext Workspace,
        WorkspaceRegistry Registry,
        IndexBootstrapService Bootstrap);

    private static JulieDbFixture CreateSynth(long revision, string? workspaceId) =>
        JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, JulieDbFixture.DefaultRows, workspaceId: workspaceId,
            revisions: workspaceId is null
                ? null
                : new[] { new JulieDbFixture.RevisionRow(revision) });

    private static void SeedOnboardingTelemetry(TelemetryLedger ledger, string workspaceId, string workspaceRoot)
    {
        ledger.Record(TelemetryRow("search", "auto", workspaceId, workspaceRoot, "ok", 100, 3, Hash("GetUser")));
        ledger.Record(TelemetryRow("inspect", "summary", workspaceId, workspaceRoot, "ok", 40, 1, Hash("GetUser")));
        ledger.Record(TelemetryRow(
            "search",
            "auto",
            workspaceId,
            workspaceRoot,
            "empty",
            30,
            0,
            targetHash: null,
            metadataJson: """{"empty_reason":"no_symbol_hits"}"""));
    }

    private static TelemetryRecord TelemetryRow(
        string tool,
        string? op,
        string workspaceId,
        string workspaceRoot,
        string outcome,
        long durationMs,
        int resultCount,
        string? targetHash,
        string metadataJson = "{}") => new(
            Tool: tool,
            Op: op,
            WorkspaceId: workspaceId,
            WorkspaceRoot: workspaceRoot,
            DurationMs: durationMs,
            Outcome: outcome,
            ErrorKind: null,
            ResultCount: resultCount,
            BytesExamined: 0,
            BytesReturned: 100,
            SourceBytes: 0,
            EstTokens: 25,
            IndexFresh: true,
            TargetHash: targetHash,
            MetadataJson: metadataJson);

    private static string Hash(string raw) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

    private static void SeedHealthRows(string dbPath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();

        Exec(connection, """
            INSERT INTO parse_diagnostics
                (diagnostic_id, file_id, path, language, kind, message, start_line, start_column,
                 end_line, end_column, start_byte, end_byte, metadata_json)
            VALUES
                ('diag-1', 'file:src/A.cs', 'src/A.cs', 'csharp', 'parse_error', 'first', 1, 1, 1, 1, 0, 1, NULL),
                ('diag-2', 'file:src/A.cs', 'src/A.cs', 'csharp', 'parse_error', 'second', 2, 1, 2, 1, 2, 3, NULL);
            """);
        Exec(connection, """
            INSERT INTO language_capability_gaps
                (gap_id, language, capability, status, reason, required_closure, evidence_json)
            VALUES
                ('gap-1', 'csharp', 'relationships', 'open', 'fixture missing', 'fixture', '{}');
            """);
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    // A fake leader: publish recording ops so the indexer reports Scanned for refresh/full.
    private sealed class RecordingScanOps : IExtractOps
    {
        public List<bool> ScanForce { get; } = [];
        public ExtractReport Update(string path) => throw new NotSupportedException();
        public ExtractReport Delete(string path) => throw new NotSupportedException();
        public ExtractReport Scan(ScanIntent intent = ScanIntent.IncrementalReconcile, int? jobs = null)
        {
            ScanForce.Add(ScanIntentPolicy.RequiresForce(intent));
            return new ExtractReport(
                ReportSchemaVersion: 1, Status: "ok", Operation: "scan", Mode: "incremental", Input: null,
                Artifact: new ExtractArtifact(
                    DbPath: "x", RootPath: "/abs/r", ArtifactId: "a",
                    SchemaVersion: MillerExtractContract.ExpectedSchemaVersion,
                    ExtractContractVersion: MillerExtractContract.ExpectedExtractContractVersion,
                    SqliteSchemaVersion: MillerExtractContract.ExpectedSqliteSchemaVersion,
                    JsonlSchemaVersion: 1, HashAlgorithm: MillerExtractContract.ExpectedHashAlgorithm,
                    ParserInventoryFingerprint: "p", CapabilitySnapshotFingerprint: "c"),
                Tool: new ExtractTool("julie-extract", "2.0.0"),
                RevisionBlock: new ExtractRevision(99, 99),
                Counts: null, Errors: [], Warnings: []);
        }
    }

    private sealed class RecordingPartialScanOps(string root, string dbPath) : IExtractOps
    {
        public List<bool> ScanForce { get; } = [];
        public ExtractReport Update(string path) => throw new NotSupportedException();
        public ExtractReport Delete(string path) => throw new NotSupportedException();
        public ExtractReport Scan(ScanIntent intent = ScanIntent.IncrementalReconcile, int? jobs = null)
        {
            ScanForce.Add(ScanIntentPolicy.RequiresForce(intent));
            return PartialReport(root, dbPath, Ws, revision: 99);
        }
    }

    // workspaceId is retained only for caller readability/registry setup; the nested v1 report carries no
    // workspace_id and WorkspaceTool no longer cross-checks one (E3 removed the echo check).
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

    private static ExtractReport SlowWarningReport(
        string root,
        string dbPath,
        string workspaceId,
        long revision) =>
        Report(root, dbPath, workspaceId, revision) with
        {
            Status = "no_change",
            Warnings =
            [
                new ReportDiagnostic(
                    "slow_file_skipped",
                    "file exceeded extraction timeout",
                    Path.Combine(root, "Generated", "Slow.kt"),
                    "Generated/Slow.kt",
                    Recoverable: true),
            ],
        };

    private sealed class NoopLease : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class RecordingDashboardLauncher(DashboardLaunchResult result) : IDashboardLauncher
    {
        private readonly DashboardLaunchResult _result = result;
        private readonly List<DashboardLaunchRequest> _requests = [];

        public IReadOnlyList<DashboardLaunchRequest> Requests => _requests;

        public DashboardLaunchResult EnsureRunning(DashboardLaunchRequest request)
        {
            _requests.Add(request);
            return _result;
        }
    }

    private sealed class ThrowingDashboardLauncher(Exception exception) : IDashboardLauncher
    {
        public DashboardLaunchResult EnsureRunning(DashboardLaunchRequest request) => throw exception;
    }

    // ---- status ----

    [Fact]
    public void Status_Default_AssemblesFactsAndTelemetry_Compact()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        var (tool, _, ledger, root) = BuildTool(fx, builtRevision: 4, workspaceId: Ws);
        ledger.InsertRawForTest(Guid.NewGuid().ToString(), DateTime.UtcNow, "search");

        string output = tool.Workspace(); // operation defaults to status, compact

        Assert.Contains(root, output);
        Assert.Contains(Ws, output);
        Assert.Contains("# workspace", output);
        Assert.Contains("symbols:", output);
        // A non-leader (never-started indexer) reads as "reader", not leader — honest about this process's role.
        Assert.Contains("reader", output, StringComparison.OrdinalIgnoreCase);
        // The seeded telemetry row shows in the embedded breakdown.
        Assert.Contains("search", output);
        Assert.DoesNotContain("rebinding:", output, StringComparison.OrdinalIgnoreCase);
    }

    // The only test that crosses the WorkspaceTool → ledger seam. Without it a revert to the lifetime
    // Summarize() leaves every other telemetry-window test green while the status line silently reports a month.
    [Fact]
    public void Status_Compact_TelemetrySummarizesTheSevenDayWindow()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        var (tool, _, ledger, _) = BuildTool(fx, builtRevision: 4, workspaceId: Ws);
        DateTime now = DateTime.UtcNow;
        // 20 days old: inside the 30-day retention (so Prune is not a factor) and outside the 7-day window.
        ledger.InsertRawForTest(Guid.NewGuid().ToString(), now.AddDays(-20), "aged-caller");
        ledger.InsertRawForTest(Guid.NewGuid().ToString(), now, "search");

        string output = tool.Workspace(operation: "status");

        Assert.Contains("telemetry: 7d  1 calls", output, StringComparison.Ordinal);
        Assert.Contains("busiest=search", output, StringComparison.Ordinal);
        Assert.DoesNotContain("aged-caller", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_Json_TelemetryCarriesTheSevenDayWindow()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        var (tool, _, ledger, _) = BuildTool(fx, builtRevision: 4, workspaceId: Ws);
        DateTime now = DateTime.UtcNow;
        ledger.InsertRawForTest(Guid.NewGuid().ToString(), now.AddDays(-20), "aged-caller");
        ledger.InsertRawForTest(Guid.NewGuid().ToString(), now, "search");

        using var doc = JsonDocument.Parse(tool.Workspace(operation: "status", format: "json"));
        JsonElement telemetry = doc.RootElement.GetProperty("telemetry");

        Assert.Equal(7, telemetry.GetProperty("window_days").GetInt32());
        Assert.Equal(1, telemetry.GetProperty("total_calls").GetInt64());
        Assert.Equal("search", Assert.Single(telemetry.GetProperty("tools").EnumerateArray())
            .GetProperty("tool").GetString());
    }

    [Fact]
    public void Status_CurrentStoreFactsDoNotMaterializeTheHolderRepository()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        int loads = 0;
        var holder = new IndexHolder(
            () =>
            {
                loads++;
                throw new InvalidOperationException("status materialized the holder repository");
            },
            builtRevision: 4,
            documentCount: JulieDbFixture.DefaultRows.Count,
            knownExtensionsCount: 1);
        WorkspaceToolHarness harness = BuildHarness(
            fx,
            builtRevision: 4,
            workspaceId: Ws,
            holderOverride: holder);

        string output = harness.Tool.Workspace(operation: "status");

        Assert.Contains("symbols:", output, StringComparison.Ordinal);
        Assert.Equal(0, loads);
    }

    [Fact]
    public void Status_ReapsStaleSidecarStagingOrphansWithoutASidecarBuild()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        var (tool, _, _, root) = BuildTool(fx, builtRevision: 4, workspaceId: Ws);
        string orphan = StageStagingFile(root, ".search-build-orphan.db", TimeSpan.FromHours(2));
        string liveBuild = StageStagingFile(root, ".content-build-live.db", TimeSpan.Zero);

        tool.Workspace();

        Assert.False(File.Exists(orphan));
        Assert.True(File.Exists(liveBuild));
        Assert.False(File.Exists(Path.Combine(root, "search.db")));
    }

    [Fact]
    public void Status_ReapsStaleSidecarStagingOrphansWhenTheWorkspaceIsStuckInAScanError()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        WorkspaceToolHarness harness = BuildHarness(fx, builtRevision: 4, workspaceId: Ws);
        harness.Registry.MarkError(Ws, "julie-extract reported a usage/argv error (exit 2)");
        string orphan = StageStagingFile(harness.Root, ".search-build-stuck.db", TimeSpan.FromHours(2));

        harness.Tool.Workspace();

        Assert.False(File.Exists(orphan));
    }

    private static string StageStagingFile(string sidecarDir, string name, TimeSpan age)
    {
        string path = Path.Combine(sidecarDir, name);
        File.WriteAllText(path, "staging content");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
        return path;
    }

    [Fact]
    public void Status_WhenRebindRunning_RendersCompactNotice()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        var harness = BuildHarness(fx, builtRevision: 4, workspaceId: Ws);
        string rebindRoot = NewTempDir("rebind-running");
        string canonicalRebindRoot = PathCanonicalizer.CanonicalizeRoot(rebindRoot);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Bootstrap.TestRunBootstrapOverride = _ =>
        {
            release.Task.GetAwaiter().GetResult();
        };

        try
        {
            var outcome = harness.Bootstrap.BootstrapForRoot(
                rebindRoot, WorkspaceBindingResolver.WorkspaceSource.Roots);
            Assert.Equal(BindOutcome.Started, outcome);

            string output = harness.Tool.Workspace(operation: "status");

            Assert.Contains($"rebinding: {canonicalRebindRoot} (started 0s ago)", output);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public void Status_Json_HasWorkspaceIndexAndTelemetrySections()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        var (tool, _, _, root) = BuildTool(fx, builtRevision: 4, workspaceId: Ws);

        using var doc = JsonDocument.Parse(tool.Workspace(operation: "status", format: "json"));
        var rootEl = doc.RootElement;
        Assert.Equal(root, rootEl.GetProperty("workspace").GetProperty("root").GetString());
        Assert.False(rootEl.GetProperty("workspace").GetProperty("leader").GetBoolean());
        Assert.True(rootEl.GetProperty("index").GetProperty("document_count").GetInt64() > 0);
        Assert.Equal(4, rootEl.GetProperty("index").GetProperty("built_revision").GetInt64());
        Assert.True(rootEl.TryGetProperty("telemetry", out _));
    }

    [Fact]
    public void StatusAndHealth_PropagateTheLiveBrokerSnapshotWithoutCompactPidLeakage()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        var snapshot = new SemanticBrokerSnapshot(
            "ready",
            "a5d53c7dd92b2107",
            IsOwner: true,
            OwnershipDegraded: false,
            OwnershipDegradedReason: null,
            ReconnectCount: 3,
            SpawnAttempts: 1,
            OwnerProcessId: 4242,
            ModelId: "BAAI/bge-small-en-v1.5",
            ModelSha256: "sha256-model",
            Backend: "cuda",
            Accelerated: true,
            RetiredOwnerCount: 2,
            ServerVersion: "1",
            AcceleratorLeaseHeld: true);
        var broker = new SemanticEmbeddingSessionBroker(
            enabled: true,
            sessionFactory: () => null,
            snapshotProvider: () => snapshot);
        Assert.False(broker.Available);
        WorkspaceToolHarness harness = BuildHarness(
            fx,
            builtRevision: 4,
            workspaceId: Ws,
            vectors: new VectorSidecar(SemanticMode.On),
            semanticBroker: broker);

        string compact = harness.Tool.Workspace(operation: "status");
        using var status = JsonDocument.Parse(harness.Tool.Workspace(operation: "status", format: "json"));
        using var health = JsonDocument.Parse(harness.Tool.Workspace(operation: "health", format: "json"));

        Assert.Contains("semantic_broker: ready", compact, StringComparison.Ordinal);
        Assert.Contains("endpoint: a5d53c7dd92b2107", compact, StringComparison.Ordinal);
        Assert.DoesNotContain("4242", compact, StringComparison.Ordinal);
        Assert.Equal(4242, status.RootElement.GetProperty("semantic_broker").GetProperty("owner_pid").GetInt32());
        Assert.Equal("cuda", health.RootElement.GetProperty("semantic_broker").GetProperty("backend").GetString());
    }

    [Fact]
    public void Health_Markdown_RefusesAndPointsToTheExhaustiveCli()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        var (tool, _, _, _) = BuildTool(fx, builtRevision: 4, workspaceId: Ws);

        string output = tool.Workspace(operation: "health", format: "markdown");

        Assert.Contains("diagnostic_code=invalid_format", output, StringComparison.Ordinal);
        Assert.Contains("CLI-only", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StatusAndHealth_CurrentWorkspaceReportUnavailableVectorsWhenSemanticIsOn()
    {
        string? previous = Environment.GetEnvironmentVariable(VectorSidecar.EnvVar);
        Environment.SetEnvironmentVariable(VectorSidecar.EnvVar, "on");
        try
        {
            using var fx = CreateSynth(revision: 4, workspaceId: Ws);
            WorkspaceTool tool = BuildHarness(
                fx,
                builtRevision: 4,
                workspaceId: Ws,
                vectors: VectorSidecar.FromEnvironment()).Tool;

            string status = tool.Workspace(operation: "status");
            string health = tool.Workspace(operation: "health");
            using var statusJson = JsonDocument.Parse(tool.Workspace(operation: "status", format: "json"));
            using var healthJson = JsonDocument.Parse(tool.Workspace(operation: "health", format: "json"));

            Assert.Contains("vectors: unavailable (", status, StringComparison.Ordinal);
            Assert.Contains("vectors: unavailable (", health, StringComparison.Ordinal);
            Assert.Equal("unavailable", statusJson.RootElement.GetProperty("index").GetProperty("vectors")
                .GetProperty("state").GetString());
            Assert.Equal("unavailable", healthJson.RootElement.GetProperty("index").GetProperty("vectors")
                .GetProperty("state").GetString());
            Assert.Contains(
                healthJson.RootElement.GetProperty("warnings").EnumerateArray(),
                warning => warning.GetProperty("code").GetString() == "vectors_unavailable" &&
                    warning.GetProperty("message").GetString()!.Contains("vector retrieval is unavailable", StringComparison.Ordinal));
            Assert.Contains(
                healthJson.RootElement.GetProperty("recommended_actions").EnumerateArray(),
                action => action.GetString()!.Contains("resident Miller leader", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable(VectorSidecar.EnvVar, previous);
        }
    }

    // ---- list ----

    [Fact]
    public void List_ShowsCurrentWorkspace()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        var (tool, _, _, root) = BuildTool(fx, builtRevision: 4, workspaceId: Ws);

        string output = tool.Workspace(operation: "list");
        Assert.Contains(root, output);
        Assert.Contains("current", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void List_ReadsRegistryRowsAndMarksOnlyTheServedWorkspaceCurrent()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        WorkspaceToolHarness harness = BuildHarness(fx, builtRevision: 4, workspaceId: Ws);
        string otherRoot = NewTempDir("registered-other");
        string otherDb = Path.Combine(otherRoot, ".miller", "symbols.db");
        harness.Registry.UpsertSeen(OtherWs, "other-111111111111", otherRoot, otherDb, WorkspaceRegistryState.Ready);
        harness.Registry.MarkScanned(OtherWs, revision: 9);

        using var doc = JsonDocument.Parse(harness.Tool.Workspace(operation: "list", format: "json"));
        var rows = doc.RootElement.GetProperty("workspaces").EnumerateArray().ToArray();

        Assert.Equal(2, rows.Length);
        Assert.Contains(rows, row =>
            row.GetProperty("workspace_id").GetString() == Ws
            && row.GetProperty("current").GetBoolean());
        Assert.Contains(rows, row =>
            row.GetProperty("workspace_id").GetString() == OtherWs
            && !row.GetProperty("current").GetBoolean()
            && row.GetProperty("state").GetString() == "ready"
            && row.GetProperty("last_revision").GetInt64() == 9
            && !row.GetProperty("root_missing").GetBoolean());
    }

    [Fact]
    public void List_Json_ReportsMissingRootsWithoutOpeningTheirIndexes()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        WorkspaceToolHarness harness = BuildHarness(fx, builtRevision: 4, workspaceId: Ws);
        SeedOtherWorkspaces(harness, count: 2, baseSeenAt: DateTimeOffset.UtcNow.AddDays(-1));

        using var doc = JsonDocument.Parse(harness.Tool.Workspace(operation: "list", format: "json"));
        JsonElement root = doc.RootElement;
        JsonElement[] missing = root.GetProperty("workspaces")
            .EnumerateArray()
            .Where(static row => row.GetProperty("root_missing").GetBoolean())
            .ToArray();

        Assert.Equal(2, root.GetProperty("registered_missing").GetInt32());
        Assert.Equal(2, root.GetProperty("matched_missing").GetInt32());
        Assert.Equal(2, root.GetProperty("returned_missing").GetInt32());
        Assert.Equal(2, missing.Length);
    }

    // Seed N registered non-current rows with ascending last-seen stamps (i=1 oldest .. i=N newest) so the
    // recency order is deterministic: higher i renders earlier (right after the current workspace).
    private static void SeedOtherWorkspaces(
        WorkspaceToolHarness harness, int count, DateTimeOffset baseSeenAt,
        Func<int, WorkspaceRegistryState>? stateFor = null)
    {
        for (int i = 1; i <= count; i++)
        {
            string root = $"/seed/repo-{i:D3}";
            harness.Registry.UpsertSeen(
                $"ws-seed-{i:D3}",
                $"seed-{i:D3}-aaaaaaaa",
                root,
                root + "/.miller/symbols.db",
                stateFor?.Invoke(i) ?? WorkspaceRegistryState.Ready,
                seenAtUtc: baseSeenAt.AddMinutes(i));
        }
    }

    [Fact]
    public void List_Compact_CapsAtDefaultLimitAndRendersOmittedTail_CurrentFirst()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        WorkspaceToolHarness harness = BuildHarness(fx, builtRevision: 4, workspaceId: Ws);
        SeedOtherWorkspaces(harness, count: 25, baseSeenAt: DateTimeOffset.UtcNow.AddDays(-1));

        string output = harness.Tool.Workspace(operation: "list");

        // 26 registered (1 current + 25 seeded); compact caps at 20 with a 6-row omitted tail.
        Assert.Contains("# workspaces (20 of 26)", output);
        Assert.Contains("… 6 more — raise limit or pass filter=<substring>", output);
        // Current workspace is always first.
        string firstRow = output.Split('\n').First(line => line.StartsWith("* ", StringComparison.Ordinal));
        Assert.Contains("[current]", firstRow);
        // The newest seeded row survives the cap; the oldest is omitted.
        Assert.Contains("seed-025-aaaaaaaa", output);
        Assert.DoesNotContain("seed-001-aaaaaaaa", output);
    }

    [Fact]
    public void List_Compact_FilterNarrowsByDisplayIdOrRoot()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        WorkspaceToolHarness harness = BuildHarness(fx, builtRevision: 4, workspaceId: Ws);
        SeedOtherWorkspaces(harness, count: 5, baseSeenAt: DateTimeOffset.UtcNow.AddDays(-1));

        // "repo-004" matches only one seeded row's root substring (case-insensitive).
        string output = harness.Tool.Workspace(operation: "list", filter: "REPO-004");

        Assert.Contains("seed-004-aaaaaaaa", output);
        Assert.DoesNotContain("seed-003-aaaaaaaa", output);
        Assert.Contains("filter=\"REPO-004\"", output);
    }

    [Fact]
    public void List_Compact_FilterNoMatchRendersHelpfulLine()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        WorkspaceToolHarness harness = BuildHarness(fx, builtRevision: 4, workspaceId: Ws);
        SeedOtherWorkspaces(harness, count: 3, baseSeenAt: DateTimeOffset.UtcNow.AddDays(-1));

        string output = harness.Tool.Workspace(operation: "list", filter: "zzz-no-such-workspace");

        Assert.False(string.IsNullOrWhiteSpace(output));
        Assert.Contains("no workspace matches filter \"zzz-no-such-workspace\"", output);
        Assert.Contains("4 registered", output); // 1 current + 3 seeded
    }

    [Fact]
    public void List_EmptyRegistrySuggestsOpeningAWorkspace()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: null);
        WorkspaceToolHarness harness = BuildHarness(fx, builtRevision: 4, workspaceId: null);

        using var doc = JsonDocument.Parse(harness.Tool.Workspace(operation: "list", format: "json"));

        JsonElement diagnostic = doc.RootElement.GetProperty("diagnostic");
        Assert.Equal("workspace_list_empty", diagnostic.GetProperty("code").GetString());
        Assert.Contains(
            "workspace(operation=\"open\"",
            diagnostic.GetProperty("next_actions")[0].GetProperty("call").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void List_Compact_OmittedErrorStateProducesErrorSummary()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        WorkspaceToolHarness harness = BuildHarness(fx, builtRevision: 4, workspaceId: Ws);
        // The oldest seeded row (i=1) is error-state; being oldest it falls outside the default cap.
        SeedOtherWorkspaces(
            harness, count: 25, baseSeenAt: DateTimeOffset.UtcNow.AddDays(-1),
            stateFor: i => i == 1 ? WorkspaceRegistryState.Error : WorkspaceRegistryState.Ready);

        string output = harness.Tool.Workspace(operation: "list");

        Assert.DoesNotContain("seed-001-aaaaaaaa", output); // omitted past the cap
        Assert.Contains("errors: 1 workspace(s) in error state — filter or raise limit to see them", output);
    }

    [Fact]
    public void List_Json_UsesTheAgentDefaultLimitAndAddsLastSeenAt()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        WorkspaceToolHarness harness = BuildHarness(fx, builtRevision: 4, workspaceId: Ws);
        SeedOtherWorkspaces(harness, count: 25, baseSeenAt: DateTimeOffset.UtcNow.AddDays(-1));

        using var doc = JsonDocument.Parse(harness.Tool.Workspace(operation: "list", format: "json"));
        var rows = doc.RootElement.GetProperty("workspaces").EnumerateArray().ToArray();

        Assert.Equal(20, rows.Length);
        Assert.Equal(26, doc.RootElement.GetProperty("registered").GetInt32());
        Assert.Equal(6, doc.RootElement.GetProperty("omitted").GetInt32());
        // Additive last_seen_at present and ISO-8601 parseable on every row.
        foreach (JsonElement row in rows)
            Assert.True(DateTimeOffset.TryParse(
                row.GetProperty("last_seen_at").GetString(), out _));
        // Current workspace is ordered first.
        Assert.True(rows[0].GetProperty("current").GetBoolean());
    }

    [Fact]
    public void List_Json_LongRowsStayWithinTheWorkspaceBudgetAndReportExactOmissions()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        WorkspaceToolHarness harness = BuildHarness(fx, builtRevision: 4, workspaceId: Ws);
        for (int i = 0; i < 100; i++)
        {
            string root = "/seed/" + new string('x', 700) + i.ToString("D3");
            string workspaceId = $"ws-long-{i:D3}";
            harness.Registry.UpsertSeen(
                workspaceId,
                $"long-{i:D3}",
                root,
                root + "/.miller/symbols.db",
                WorkspaceRegistryState.Ready);
            harness.Registry.MarkError(workspaceId, new string('e', 700));
        }

        string output;
        using (TelemetryScope scope = harness.Ledger.Measure("workspace", op: null))
        {
            output = harness.Tool.Workspace(operation: "list", format: "json", limit: 100);
            using var measured = JsonDocument.Parse(output);
            Assert.Equal(
                measured.RootElement.GetProperty("returned").GetInt32(),
                scope.ResultCount);
        }

        Assert.True(Encoding.UTF8.GetByteCount(output) <= ToolOutputBudget.WorkspaceMcpMaxBytes);
        using var doc = JsonDocument.Parse(output);
        JsonElement rootElement = doc.RootElement;
        int returned = rootElement.GetProperty("returned").GetInt32();
        Assert.InRange(returned, 1, 99);
        Assert.Equal(101, rootElement.GetProperty("registered").GetInt32());
        Assert.Equal(101 - returned, rootElement.GetProperty("omitted").GetInt32());
        Assert.Equal(returned, rootElement.GetProperty("workspaces").GetArrayLength());
        Assert.False(rootElement.TryGetProperty("diagnostic", out _));
    }

    [Fact]
    public void List_Json_RespectsExplicitLimit()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        WorkspaceToolHarness harness = BuildHarness(fx, builtRevision: 4, workspaceId: Ws);
        SeedOtherWorkspaces(harness, count: 25, baseSeenAt: DateTimeOffset.UtcNow.AddDays(-1));

        using var doc = JsonDocument.Parse(
            harness.Tool.Workspace(operation: "list", format: "json", limit: 3));

        Assert.Equal(3, doc.RootElement.GetProperty("workspaces").GetArrayLength());
    }

    [Fact]
    public void List_Json_ReportsExactRegistrySelectionTotals()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        WorkspaceToolHarness harness = BuildHarness(fx, builtRevision: 4, workspaceId: Ws);
        SeedOtherWorkspaces(harness, count: 5, baseSeenAt: DateTimeOffset.UtcNow.AddDays(-1));

        using var doc = JsonDocument.Parse(harness.Tool.Workspace(
            operation: "list",
            format: "json",
            filter: "repo-",
            limit: 2));
        JsonElement root = doc.RootElement;

        Assert.Equal(6, root.GetProperty("registered").GetInt32());
        Assert.Equal(5, root.GetProperty("matched").GetInt32());
        Assert.Equal(2, root.GetProperty("returned").GetInt32());
        Assert.Equal(3, root.GetProperty("omitted").GetInt32());
        Assert.Equal("repo-", root.GetProperty("filter").GetString());
        Assert.Equal(2, root.GetProperty("limit").GetInt32());
        Assert.Equal(2, root.GetProperty("workspaces").GetArrayLength());
    }

    [Fact]
    public void List_Compact_ReportsExactDefaultSelectionTotals()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        WorkspaceToolHarness harness = BuildHarness(fx, builtRevision: 4, workspaceId: Ws);
        SeedOtherWorkspaces(harness, count: 25, baseSeenAt: DateTimeOffset.UtcNow.AddDays(-1));

        string output = harness.Tool.Workspace(operation: "list");

        Assert.Contains("totals: registered=26 matched=26 returned=20 omitted=6", output);
        Assert.Contains("selection: filter=(none) limit=20", output);
    }

    [Fact]
    public void Status_ByWorkspaceId_ReadsRegisteredSchemaFiveFacts()
    {
        using var current = CreateSynth(revision: 4, workspaceId: Ws);
        using var other = CreateSynth(revision: 9, workspaceId: OtherWs);
        WorkspaceToolHarness harness = BuildHarness(current, builtRevision: 4, workspaceId: Ws);
        string otherRoot = Path.GetDirectoryName(other.DbPath)!;
        harness.Registry.UpsertSeen(OtherWs, "other-111111111111", otherRoot, other.DbPath, WorkspaceRegistryState.Ready);
        harness.Registry.MarkScanned(OtherWs, revision: 9);

        using var doc = JsonDocument.Parse(harness.Tool.Workspace(
            operation: "status",
            workspace_id: OtherWs,
            format: "json"));

        Assert.Equal(OtherWs, doc.RootElement.GetProperty("workspace").GetProperty("workspace_id").GetString());
        Assert.Equal(otherRoot, doc.RootElement.GetProperty("workspace").GetProperty("root").GetString());
        Assert.Equal(other.DbPath, doc.RootElement.GetProperty("workspace").GetProperty("db").GetString());
        Assert.True(doc.RootElement.GetProperty("index").GetProperty("document_count").GetInt64() > 0);
        Assert.Equal(9, doc.RootElement.GetProperty("index").GetProperty("built_revision").GetInt64());
        Assert.Equal("ready", doc.RootElement.GetProperty("index").GetProperty("freshness_status").GetString());
        JsonElement leader = doc.RootElement.GetProperty("indexer_leader");
        Assert.Equal(JsonValueKind.Object, leader.ValueKind);
        Assert.True(leader.TryGetProperty("own_extractor_version", out _));
        Assert.True(leader.TryGetProperty("artifact_extractor_version", out _));
    }

    [Fact]
    public void Status_ByDisplayId_ReadsRegisteredWorkspace()
    {
        using var current = CreateSynth(revision: 4, workspaceId: Ws);
        using var other = CreateSynth(revision: 9, workspaceId: OtherWs);
        WorkspaceToolHarness harness = BuildHarness(current, builtRevision: 4, workspaceId: Ws);
        string otherRoot = Path.GetDirectoryName(other.DbPath)!;
        harness.Registry.UpsertSeen(OtherWs, "other-111111111111", otherRoot, other.DbPath, WorkspaceRegistryState.Ready);
        harness.Registry.MarkScanned(OtherWs, revision: 9);

        using var doc = JsonDocument.Parse(harness.Tool.Workspace(
            operation: "status",
            workspace_id: "other-111111111111",
            format: "json"));

        Assert.Equal(OtherWs, doc.RootElement.GetProperty("workspace").GetProperty("workspace_id").GetString());
        Assert.Equal(otherRoot, doc.RootElement.GetProperty("workspace").GetProperty("root").GetString());
        Assert.Equal(9, doc.RootElement.GetProperty("index").GetProperty("built_revision").GetInt64());
    }

    [Fact]
    public void Status_ByWorkspaceId_DoesNotRenderCurrentWorkspaceTelemetry()
    {
        using var current = CreateSynth(revision: 4, workspaceId: Ws);
        using var other = CreateSynth(revision: 9, workspaceId: OtherWs);
        WorkspaceToolHarness harness = BuildHarness(current, builtRevision: 4, workspaceId: Ws);
        harness.Ledger.Record(new TelemetryRecord(
            Tool: "current-search",
            Op: null,
            WorkspaceId: Ws,
            WorkspaceRoot: harness.Root,
            DurationMs: 10,
            Outcome: "ok",
            ErrorKind: null,
            ResultCount: 1,
            BytesExamined: 0,
            BytesReturned: 0,
            SourceBytes: 0,
            EstTokens: 1,
            IndexFresh: true,
            TargetHash: null,
            MetadataJson: "{}"));
        string otherRoot = Path.GetDirectoryName(other.DbPath)!;
        harness.Registry.UpsertSeen(OtherWs, "other-111111111111", otherRoot, other.DbPath, WorkspaceRegistryState.Ready);
        harness.Registry.MarkScanned(OtherWs, revision: 9);
        harness.Ledger.Record(new TelemetryRecord(
            Tool: "target-search",
            Op: null,
            WorkspaceId: OtherWs,
            WorkspaceRoot: otherRoot,
            DurationMs: 20,
            Outcome: "ok",
            ErrorKind: null,
            ResultCount: 2,
            BytesExamined: 0,
            BytesReturned: 0,
            SourceBytes: 0,
            EstTokens: 2,
            IndexFresh: true,
            TargetHash: null,
            MetadataJson: "{}"));

        using var doc = JsonDocument.Parse(harness.Tool.Workspace(
            operation: "status",
            workspace_id: OtherWs,
            format: "json"));
        string[] tools = doc.RootElement
            .GetProperty("telemetry")
            .GetProperty("tools")
            .EnumerateArray()
            .Select(tool => tool.GetProperty("tool").GetString()!)
            .ToArray();

        Assert.Contains("target-search", tools);
        Assert.DoesNotContain("current-search", tools);
    }

    [Fact]
    public void Status_ByWorkspaceId_MissingIndexDbRendersTypedMissingIndexAndMarksRegistry()
    {
        using var current = CreateSynth(revision: 4, workspaceId: Ws);
        WorkspaceToolHarness harness = BuildHarness(current, builtRevision: 4, workspaceId: Ws);
        string otherRoot = NewTempDir("status-missing-db");
        string missingDb = Path.Combine(otherRoot, ".miller", "symbols.db");
        harness.Registry.UpsertSeen(OtherWs, "other-111111111111", otherRoot, missingDb, WorkspaceRegistryState.Ready);
        harness.Registry.MarkScanned(OtherWs, revision: 9);

        string output = harness.Tool.Workspace(operation: "status", workspace_id: OtherWs);

        Assert.DoesNotContain("workspace failed", output, StringComparison.Ordinal);
        Assert.Contains("missing_index", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(missingDb, output, StringComparison.Ordinal);
        WorkspaceRegistryRow? row = harness.Registry.Get(OtherWs);
        Assert.NotNull(row);
        Assert.Equal(WorkspaceRegistryState.Missing, row.State);
        Assert.Contains(missingDb, row.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_ByWorkspaceId_MissingIndexDbReturnsUnavailableDiagnostic()
    {
        using var current = CreateSynth(revision: 4, workspaceId: Ws);
        WorkspaceToolHarness harness = BuildHarness(current, builtRevision: 4, workspaceId: Ws);
        string otherRoot = NewTempDir("status-missing-diagnostic");
        string missingDb = Path.Combine(otherRoot, ".miller", "symbols.db");
        harness.Registry.UpsertSeen(OtherWs, "other-111111111111", otherRoot, missingDb, WorkspaceRegistryState.Ready);

        using var doc = JsonDocument.Parse(harness.Tool.Workspace(
            operation: "status",
            workspace_id: OtherWs,
            format: "json"));

        Assert.Equal(
            "workspace_index_unavailable",
            doc.RootElement.GetProperty("diagnostic").GetProperty("code").GetString());
    }

    [Fact]
    public void Status_CurrentWorkspaceMissingIndexReturnsUnavailableDiagnostic()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        WorkspaceToolHarness harness = BuildHarness(fx, builtRevision: 4, workspaceId: Ws);
        File.Delete(fx.DbPath);

        using var doc = JsonDocument.Parse(harness.Tool.Workspace(operation: "status", format: "json"));

        Assert.Equal(
            "workspace_index_unavailable",
            doc.RootElement.GetProperty("diagnostic").GetProperty("code").GetString());
    }

    [Fact]
    public void Status_CurrentWorkspaceUnreadableIndexReturnsUnavailableDiagnostic()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        WorkspaceToolHarness harness = BuildHarness(fx, builtRevision: 4, workspaceId: Ws);
        SqliteConnection.ClearAllPools();
        File.WriteAllText(fx.DbPath, "not a sqlite database");

        using var doc = JsonDocument.Parse(harness.Tool.Workspace(operation: "status", format: "json"));

        Assert.Equal("unreadable_index", doc.RootElement
            .GetProperty("index")
            .GetProperty("freshness_status")
            .GetString());
        Assert.Equal(
            "workspace_index_unavailable",
            doc.RootElement.GetProperty("diagnostic").GetProperty("code").GetString());
    }

    [Fact]
    public void Status_ByPath_ResolvesRegisteredWorkspaceByCanonicalRoot()
    {
        using var current = CreateSynth(revision: 4, workspaceId: Ws);
        using var other = CreateSynth(revision: 9, workspaceId: OtherWs);
        WorkspaceToolHarness harness = BuildHarness(current, builtRevision: 4, workspaceId: Ws);
        string otherRoot = Path.GetDirectoryName(other.DbPath)!;
        harness.Registry.UpsertSeen(OtherWs, "other-111111111111", otherRoot, other.DbPath, WorkspaceRegistryState.Ready);
        harness.Registry.MarkScanned(OtherWs, revision: 9);

        string output = harness.Tool.Workspace(operation: "status", path: otherRoot);

        Assert.Contains(otherRoot, output);
        Assert.Contains("other-111111111111", output);
        Assert.DoesNotContain(OtherWs, output);
        Assert.Contains("freshness: ready", output, StringComparison.OrdinalIgnoreCase);
    }

    // ---- health ----

    [Fact]
    public void Health_CurrentWorkspace_RendersStatusSidecarsExtractionWarningsAndTelemetry()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        SeedHealthRows(fx.DbPath);
        WorkspaceToolHarness harness = BuildHarness(fx, builtRevision: 4, workspaceId: Ws);
        harness.Ledger.Record(new TelemetryRecord(
            Tool: "search",
            Op: null,
            WorkspaceId: Ws,
            WorkspaceRoot: harness.Root,
            DurationMs: 10,
            Outcome: "ok",
            ErrorKind: null,
            ResultCount: 1,
            BytesExamined: 0,
            BytesReturned: 0,
            SourceBytes: 0,
            EstTokens: 1,
            IndexFresh: true,
            TargetHash: null,
            MetadataJson: "{}"));
        harness.Ledger.Record(new TelemetryRecord(
            Tool: "trace",
            Op: null,
            WorkspaceId: Ws,
            WorkspaceRoot: harness.Root,
            DurationMs: 20,
            Outcome: "error",
            ErrorKind: "InvalidOperationException",
            ResultCount: 0,
            BytesExamined: 0,
            BytesReturned: 0,
            SourceBytes: 0,
            EstTokens: 1,
            IndexFresh: true,
            TargetHash: null,
            MetadataJson: "{}"));

        using var doc = JsonDocument.Parse(harness.Tool.Workspace(
            operation: "health",
            format: "json"));
        JsonElement root = doc.RootElement;

        Assert.Equal("usable_with_warnings", root.GetProperty("verdict").GetProperty("state").GetString());
        Assert.Equal(Ws, root.GetProperty("workspace").GetProperty("workspace_id").GetString());
        Assert.Equal(1, root.GetProperty("telemetry").GetProperty("error_count").GetInt64());
        Assert.Equal(2, root.GetProperty("extraction_quality")
            .GetProperty("parse_diagnostic_count").GetInt64());
        Assert.Equal("capability_gaps", root.GetProperty("warnings")[0].GetProperty("code").GetString());
    }

    [Fact]
    public void Leader_Json_RendersLeaderDiagnostics()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        WorkspaceToolHarness harness = BuildHarness(fx, builtRevision: 4, workspaceId: Ws);
        LeaderIdentityFile.Write(Path.GetDirectoryName(fx.DbPath)!, new LeaderIdentity(
            Environment.ProcessId,
            "1.0.0",
            ProcessPath: null,
            StartedAtUtc: DateTimeOffset.UtcNow,
            ExtractorVersion: "2.3.0"));

        using var doc = JsonDocument.Parse(harness.Tool.Workspace(operation: "leader", format: "json"));
        JsonElement root = doc.RootElement;

        Assert.Equal(1, root.GetProperty("schema_version").GetInt32());
        Assert.Equal(Ws, root.GetProperty("workspace").GetProperty("workspace_id").GetString());
        Assert.Equal(Environment.ProcessId, root.GetProperty("indexer_leader").GetProperty("pid").GetInt32());
        Assert.Equal("2.3.0", root.GetProperty("indexer_leader").GetProperty("extractor_version").GetString());
        Assert.False(root.GetProperty("handoff").GetProperty("requested").GetBoolean());
        Assert.Contains("No handoff requested", root.GetProperty("recommendation").GetString());
    }

    [Fact]
    public void Leader_Handoff_QueuesGracefulRequest()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        WorkspaceToolHarness harness = BuildHarness(fx, builtRevision: 4, workspaceId: Ws);

        using var doc = JsonDocument.Parse(harness.Tool.Workspace(
            operation: "leader",
            format: "json",
            handoff: true));
        JsonElement root = doc.RootElement;

        Assert.True(root.GetProperty("handoff").GetProperty("requested").GetBoolean());
        Assert.False(root.GetProperty("handoff").GetProperty("observed").GetBoolean());
        Assert.NotEqual(string.Empty, root.GetProperty("handoff").GetProperty("request_id").GetString());
        Assert.Single(Directory.EnumerateFiles(Path.Combine(Path.GetDirectoryName(fx.DbPath)!, "requests"), "*.leader-handoff.json"));
    }

    [Fact]
    public void Leader_MissingSelectedIndexReturnsUnavailableDiagnostic()
    {
        using var current = CreateSynth(revision: 4, workspaceId: Ws);
        WorkspaceToolHarness harness = BuildHarness(current, builtRevision: 4, workspaceId: Ws);
        string otherRoot = NewTempDir("leader-missing-index");
        string missingDb = Path.Combine(otherRoot, ".miller", "symbols.db");
        harness.Registry.UpsertSeen(OtherWs, "other-111111111111", otherRoot, missingDb, WorkspaceRegistryState.Ready);

        using var doc = JsonDocument.Parse(harness.Tool.Workspace(
            operation: "leader",
            workspace_id: OtherWs,
            format: "json"));

        Assert.Equal(
            "workspace_index_unavailable",
            doc.RootElement.GetProperty("diagnostic").GetProperty("code").GetString());
    }

    [Fact]
    public void Onboarding_CurrentWorkspace_RendersTelemetryGuidanceAndRecoveredTargets()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        WorkspaceToolHarness harness = BuildHarness(fx, builtRevision: 1, workspaceId: Ws);
        SeedOnboardingTelemetry(harness.Ledger, Ws, harness.Root);

        using var doc = JsonDocument.Parse(harness.Tool.Workspace(operation: "onboarding", format: "json"));
        JsonElement root = doc.RootElement;

        Assert.Equal("onboarding", root.GetProperty("operation").GetString());
        Assert.Equal(Ws, root.GetProperty("workspace").GetProperty("workspace_id").GetString());
        Assert.Equal("ready", root.GetProperty("telemetry").GetProperty("state").GetString());
        Assert.Equal(3, root.GetProperty("telemetry").GetProperty("total_calls").GetInt64());
        Assert.Contains(root.GetProperty("start_here").EnumerateArray(),
            item => item.GetString()!.Contains("search", StringComparison.OrdinalIgnoreCase));
        JsonElement hotTarget = Assert.Single(root.GetProperty("hot_targets").EnumerateArray());
        Assert.Equal("GetUser", hotTarget.GetProperty("name").GetString());
        Assert.Equal("auth/UserService.cs", hotTarget.GetProperty("path").GetString());
        JsonElement miss = Assert.Single(root.GetProperty("common_misses").EnumerateArray());
        Assert.Equal("no_symbol_hits", miss.GetProperty("reason").GetString());
    }

    [Fact]
    public void Onboarding_WithoutTelemetryRemainsASuccessfulColdStartReport()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        WorkspaceToolHarness harness = BuildHarness(fx, builtRevision: 1, workspaceId: Ws);

        using var doc = JsonDocument.Parse(
            harness.Tool.Workspace(operation: "onboarding", format: "json"));

        Assert.Equal(0, doc.RootElement.GetProperty("telemetry").GetProperty("total_calls").GetInt64());
        Assert.False(doc.RootElement.TryGetProperty("diagnostic", out _));
    }

    [Fact]
    public void Onboarding_MissingTelemetryDatabaseReturnsUnavailableDiagnostic()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        WorkspaceToolHarness harness = BuildHarness(fx, builtRevision: 1, workspaceId: Ws);
        string telemetryDb = harness.Ledger.DbPath;
        harness.Ledger.Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(telemetryDb);

        using var doc = JsonDocument.Parse(
            harness.Tool.Workspace(operation: "onboarding", format: "json"));

        Assert.Equal(
            "workspace_onboarding_telemetry_unavailable",
            doc.RootElement.GetProperty("diagnostic").GetProperty("code").GetString());
    }

    [Fact]
    public void Health_ByWorkspaceId_ReadsRegisteredSchemaFiveFactsWithoutHydratingFullIndex()
    {
        using var current = CreateSynth(revision: 4, workspaceId: Ws);
        using var other = CreateSynth(revision: 9, workspaceId: OtherWs);
        SeedHealthRows(other.DbPath);
        WorkspaceToolHarness harness = BuildHarness(current, builtRevision: 4, workspaceId: Ws);
        string otherRoot = Path.GetDirectoryName(other.DbPath)!;
        harness.Registry.UpsertSeen(OtherWs, "other-111111111111", otherRoot, other.DbPath, WorkspaceRegistryState.Ready);
        harness.Registry.MarkScanned(OtherWs, revision: 9);

        using var doc = JsonDocument.Parse(harness.Tool.Workspace(
            operation: "health",
            workspace_id: OtherWs,
            format: "json"));

        Assert.Equal(OtherWs, doc.RootElement.GetProperty("workspace").GetProperty("workspace_id").GetString());
        Assert.Equal(9, doc.RootElement.GetProperty("index").GetProperty("built_revision").GetInt64());
        Assert.Equal(2, doc.RootElement.GetProperty("extraction_quality")
            .GetProperty("parse_diagnostic_count").GetInt64());
    }

    [Fact]
    public void Health_MissingIndex_ReturnsUnavailableVerdictAndMarksRegistry()
    {
        using var current = CreateSynth(revision: 4, workspaceId: Ws);
        WorkspaceToolHarness harness = BuildHarness(current, builtRevision: 4, workspaceId: Ws);
        string otherRoot = NewTempDir("health-missing-db");
        string missingDb = Path.Combine(otherRoot, ".miller", "symbols.db");
        harness.Registry.UpsertSeen(OtherWs, "other-111111111111", otherRoot, missingDb, WorkspaceRegistryState.Ready);
        harness.Registry.MarkScanned(OtherWs, revision: 9);

        using var doc = JsonDocument.Parse(harness.Tool.Workspace(
            operation: "health",
            workspace_id: OtherWs,
            format: "json"));

        Assert.Equal("unavailable", doc.RootElement.GetProperty("verdict").GetProperty("state").GetString());
        Assert.Contains(missingDb, doc.RootElement.GetProperty("warnings")[0].GetProperty("message").GetString(),
            StringComparison.Ordinal);
        WorkspaceRegistryRow? row = harness.Registry.Get(OtherWs);
        Assert.NotNull(row);
        Assert.Equal(WorkspaceRegistryState.Missing, row.State);
    }

    [Fact]
    public void Health_CorruptIndex_ReturnsUnavailableVerdict()
    {
        using var current = CreateSynth(revision: 4, workspaceId: Ws);
        WorkspaceToolHarness harness = BuildHarness(current, builtRevision: 4, workspaceId: Ws);
        string otherRoot = NewTempDir("health-corrupt-db");
        string millerDir = Path.Combine(otherRoot, ".miller");
        Directory.CreateDirectory(millerDir);
        string corruptDb = Path.Combine(millerDir, "symbols.db");
        File.WriteAllText(corruptDb, "not a sqlite database");
        harness.Registry.UpsertSeen(OtherWs, "other-111111111111", otherRoot, corruptDb, WorkspaceRegistryState.Ready);
        harness.Registry.MarkScanned(OtherWs, revision: 9);

        string output = harness.Tool.Workspace(
            operation: "health",
            workspace_id: OtherWs,
            format: "json");
        using var doc = JsonDocument.Parse(output);

        Assert.DoesNotContain("workspace failed", output, StringComparison.Ordinal);
        Assert.Equal("unavailable", doc.RootElement.GetProperty("verdict").GetProperty("state").GetString());
        Assert.Contains("could not read", doc.RootElement.GetProperty("warnings")[0].GetProperty("message").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }

    // ---- open / remove arg guards ----

    [Fact]
    public void Open_MissingPath_ReturnsUsage_DoesNotScan()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        var (tool, _, _, _) = BuildTool(fx, builtRevision: 4, workspaceId: Ws);

        string output = tool.Workspace(operation: "open", path: null);
        Assert.Contains("requires a path", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Remove_MissingPath_ReturnsUsage()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        var (tool, _, _, _) = BuildTool(fx, builtRevision: 4, workspaceId: Ws);

        string output = tool.Workspace(operation: "remove", path: null);
        Assert.Contains("requires a path", output, StringComparison.OrdinalIgnoreCase);
    }

    // ---- open safety (decision-2/3/8) ----

    [Fact]
    public void Open_LiveWorkspace_IsRefused_DoesNotScan()
    {
        // A live workspace remains owned by this process; refresh/full are the supported lifecycle operations.
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        var (tool, _, _, root) = BuildTool(fx, builtRevision: 4, workspaceId: Ws);

        string output = tool.Workspace(operation: "open", path: root);

        Assert.Contains("live workspace", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("refresh", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("primed", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Open_LiveWorkspace_Json_IsTypedRefusal()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        var (tool, _, _, root) = BuildTool(fx, builtRevision: 4, workspaceId: Ws);

        using var document = JsonDocument.Parse(tool.Workspace(operation: "open", path: root, format: "json"));
        JsonElement diagnostic = document.RootElement.GetProperty("diagnostic");

        Assert.Equal("workspace_open_refused", diagnostic.GetProperty("code").GetString());
        Assert.Equal("refusal", diagnostic.GetProperty("class").GetString());
        Assert.Equal("empty", diagnostic.GetProperty("outcome").GetString());
    }

    [Fact]
    public void Open_SymlinkToSensitiveRoot_IsRefused_NotPrimed()
    {
        // The sensitive-root guard must inspect the canonical symlink target before registration or queue admission.
        Assert.SkipWhen(OperatingSystem.IsWindows(), "directory symlink creation needs privilege on Windows.");
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        WorkspaceToolHarness harness = BuildHarness(
            fx,
            builtRevision: 4,
            workspaceId: Ws,
            acquireLock: _ => new NoopLease());

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.False(string.IsNullOrEmpty(home));
        string link = Path.Combine(NewTempDir("sensitive-link"), "homelink");
        Directory.CreateSymbolicLink(link, home);

        string output = harness.Tool.Workspace(operation: "open", path: link);

        Assert.Contains("sensitive", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("primed", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Open_NonExistentPath_IsACleanNotFound_NotAToolFailure()
    {
        // Symmetric with remove's not-found: open against a non-null path that does not exist on disk must give
        // clear "no directory at <path>" guidance (not a generic "workspace failed" from the canonicalizer's
        // DirectoryNotFoundException leaking to the outer catch).
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        var (tool, _, _, _) = BuildTool(fx, builtRevision: 4, workspaceId: Ws);
        string missing = Path.Combine(Path.GetTempPath(), "miller-open-missing-" + Guid.NewGuid().ToString("N"));
        Assert.False(Directory.Exists(missing));

        string output = tool.Workspace(operation: "open", path: missing);

        Assert.DoesNotContain("workspace failed", output, StringComparison.Ordinal);
        Assert.Contains("no directory", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(missing, output, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_DoesNotAcquireTheTargetWriterLock()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        WorkspaceToolHarness harness = BuildHarness(
            fx,
            builtRevision: 4,
            workspaceId: Ws,
            acquireLock: _ => throw new InvalidOperationException("writer lock must not be requested"),
            enqueueOpenPrime: _ => WorkspaceOpenPrimeEnqueueResult.Queued);
        string target = NewTempDir("open-served");

        string output = harness.Tool.Workspace(operation: "open", path: target);

        Assert.Contains("queued", output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            WorkspaceRegistryState.Refreshing,
            harness.Registry.Get(WorkspaceId.FromCanonicalRoot(target))?.State);
    }

    [Fact]
    public async Task Open_ReturnsWhileBackgroundPrimeIsGated()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        using var release = new ManualResetEventSlim();
        var backgroundStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? backgroundPrime = null;
        WorkspaceToolHarness harness = BuildHarness(
            fx,
            builtRevision: 4,
            workspaceId: Ws,
            acquireLock: _ => new NoopLease(),
            enqueueOpenPrime: _ =>
            {
                backgroundPrime = Task.Run(() =>
                {
                    backgroundStarted.TrySetResult();
                    release.Wait();
                });
                return WorkspaceOpenPrimeEnqueueResult.Queued;
            });
        string target = NewTempDir("open-nonblocking");

        Task<string> open = Task.Run(() => harness.Tool.Workspace(
            operation: "open",
            path: target,
            format: "json"));
        try
        {
            await backgroundStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Task completed = await Task.WhenAny(
                open,
                Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken));
            Assert.Same(open, completed);
            Assert.NotNull(backgroundPrime);
            Assert.False(backgroundPrime.IsCompleted);
            using var document = JsonDocument.Parse(await open);
            Assert.Equal("open", document.RootElement.GetProperty("operation").GetString());
            Assert.False(document.RootElement.GetProperty("scanned").GetBoolean());
            Assert.Equal("refreshing", document.RootElement.GetProperty("status").GetString());
            Assert.Contains("status", document.RootElement.GetProperty("note").GetString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            release.Set();
            await open;
            if (backgroundPrime is not null)
                await backgroundPrime;
        }
    }

    [Theory]
    [InlineData("full", "workspace_open_queue_full")]
    [InlineData("stopping", "workspace_open_stopping")]
    public void Open_QueueRefusalMarksOnlyNewRowsError(
        string enqueueName,
        string diagnosticCode)
    {
        WorkspaceOpenPrimeEnqueueResult enqueueResult = enqueueName == "full"
            ? WorkspaceOpenPrimeEnqueueResult.Full
            : WorkspaceOpenPrimeEnqueueResult.Stopping;
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        WorkspaceToolHarness harness = BuildHarness(
            fx,
            builtRevision: 4,
            workspaceId: Ws,
            enqueueOpenPrime: _ => enqueueResult);
        string freshTarget = NewTempDir("open-queue-refused");
        string freshId = WorkspaceId.FromCanonicalRoot(freshTarget);

        using (JsonDocument document = JsonDocument.Parse(
                   harness.Tool.Workspace(operation: "open", path: freshTarget, format: "json")))
        {
            Assert.Equal(diagnosticCode, document.RootElement.GetProperty("diagnostic").GetProperty("code").GetString());
        }

        WorkspaceRegistryRow? freshRow = harness.Registry.Get(freshId);
        Assert.NotNull(freshRow);
        Assert.Equal(WorkspaceRegistryState.Error, freshRow.State);

        string existingTarget = NewTempDir("open-queue-existing");
        string existingId = WorkspaceId.FromCanonicalRoot(existingTarget);
        string existingDb = Path.Combine(existingTarget, ".miller", "symbols.db");
        harness.Registry.UpsertSeen(
            existingId,
            WorkspaceId.Display(existingTarget, existingId),
            existingTarget,
            existingDb,
            WorkspaceRegistryState.Ready);
        harness.Registry.MarkScanned(existingId, revision: 23);

        using (JsonDocument document = JsonDocument.Parse(
                   harness.Tool.Workspace(operation: "open", path: existingTarget, format: "json")))
        {
            Assert.Equal(diagnosticCode, document.RootElement.GetProperty("diagnostic").GetProperty("code").GetString());
            Assert.Equal("ready", document.RootElement.GetProperty("status").GetString());
            Assert.Equal(23, document.RootElement.GetProperty("revision").GetInt64());
        }

        WorkspaceRegistryRow? existingRow = harness.Registry.Get(existingId);
        Assert.NotNull(existingRow);
        Assert.Equal(WorkspaceRegistryState.Ready, existingRow.State);
        Assert.Equal(23, existingRow.LastRevision);
        Assert.Null(existingRow.LastError);
    }

    [Theory]
    [InlineData("full", "workspace_open_queue_full")]
    [InlineData("stopping", "workspace_open_stopping")]
    public void Open_QueueRefusalPreservesRowAdvancedDuringEnqueue(
        string enqueueName,
        string diagnosticCode)
    {
        WorkspaceOpenPrimeEnqueueResult enqueueResult = enqueueName == "full"
            ? WorkspaceOpenPrimeEnqueueResult.Full
            : WorkspaceOpenPrimeEnqueueResult.Stopping;
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        string target = NewTempDir("open-queue-race");
        string workspaceId = WorkspaceId.FromCanonicalRoot(target);
        WorkspaceToolHarness harness = null!;
        harness = BuildHarness(
            fx,
            builtRevision: 4,
            workspaceId: Ws,
            enqueueOpenPrime: _ =>
            {
                harness.Registry.MarkScanned(workspaceId, revision: 41, scannedAtUtc: new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));
                return enqueueResult;
            });

        using JsonDocument document = JsonDocument.Parse(
            harness.Tool.Workspace(operation: "open", path: target, format: "json"));

        Assert.Equal(diagnosticCode, document.RootElement.GetProperty("diagnostic").GetProperty("code").GetString());
        Assert.Equal("ready", document.RootElement.GetProperty("status").GetString());
        Assert.Equal(41, document.RootElement.GetProperty("revision").GetInt64());
        WorkspaceRegistryRow? row = harness.Registry.Get(workspaceId);
        Assert.NotNull(row);
        Assert.Equal(WorkspaceRegistryState.Ready, row.State);
        Assert.Equal(41, row.LastRevision);
        Assert.Null(row.LastError);
    }

    [Fact]
    public void Open_RegistersAndQueuesTheStableWorkspaceId()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        WorkspaceToolHarness harness = BuildHarness(
            fx,
            builtRevision: 4,
            workspaceId: Ws,
            enqueueOpenPrime: _ => WorkspaceOpenPrimeEnqueueResult.Queued);
        string target = NewTempDir("open-registers");
        string canonicalTarget = PathCanonicalizer.CanonicalizeRoot(target);
        string stableWorkspaceId = WorkspaceId.FromCanonicalRoot(canonicalTarget);

        using var doc = JsonDocument.Parse(harness.Tool.Workspace(operation: "open", path: target, format: "json"));

        Assert.Equal(stableWorkspaceId, doc.RootElement.GetProperty("workspace_id").GetString());
        WorkspaceRegistryRow? row = harness.Registry.Get(stableWorkspaceId);
        Assert.NotNull(row);
        Assert.Equal(canonicalTarget, row.CanonicalRoot);
        Assert.Equal(Path.Combine(canonicalTarget, ".miller", "symbols.db"), row.IndexDbPath);
        Assert.Null(row.LastRevision);
        Assert.Equal(WorkspaceRegistryState.Refreshing, row.State);
        Assert.False(doc.RootElement.GetProperty("scanned").GetBoolean());
        Assert.Equal("refreshing", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public void Open_DoesNotRunThePrimeScanDelegate()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        WorkspaceToolHarness harness = BuildHarness(
            fx,
            builtRevision: 4,
            workspaceId: Ws,
            enqueueOpenPrime: _ => WorkspaceOpenPrimeEnqueueResult.Queued);
        string target = NewTempDir("open-partial");

        string output = harness.Tool.Workspace(operation: "open", path: target);

        Assert.Contains("scanned: no", output, StringComparison.Ordinal);
        Assert.Contains("status: refreshing", output, StringComparison.Ordinal);
        Assert.Contains("queued", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Open_PreservesExistingReadyStateWhenQueuedAgain()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        int enqueueCalls = 0;
        WorkspaceToolHarness harness = BuildHarness(
            fx,
            builtRevision: 4,
            workspaceId: Ws,
            enqueueOpenPrime: _ => Interlocked.Increment(ref enqueueCalls) == 1
                ? WorkspaceOpenPrimeEnqueueResult.Queued
                : WorkspaceOpenPrimeEnqueueResult.AlreadyQueued);
        string target = NewTempDir("open-ready");
        string workspaceId = WorkspaceId.FromCanonicalRoot(target);
        string dbPath = Path.Combine(target, ".miller", "symbols.db");

        harness.Tool.Workspace(operation: "open", path: target);
        harness.Registry.MarkScanned(workspaceId, revision: 13);
        string output = harness.Tool.Workspace(operation: "open", path: target);

        WorkspaceRegistryRow? row = harness.Registry.Get(workspaceId);
        Assert.NotNull(row);
        Assert.Equal(dbPath, row.IndexDbPath);
        Assert.Equal(13, row.LastRevision);
        Assert.Equal(WorkspaceRegistryState.Ready, row.State);
        Assert.Contains("status: ready", output, StringComparison.Ordinal);
        Assert.Equal(2, enqueueCalls);
    }

    [Fact]
    public void Open_PreservesExistingErrorStateWhenQueuedAgain()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        WorkspaceToolHarness harness = BuildHarness(
            fx,
            builtRevision: 4,
            workspaceId: Ws,
            enqueueOpenPrime: _ => WorkspaceOpenPrimeEnqueueResult.AlreadyQueued);
        string target = NewTempDir("open-error");
        string workspaceId = WorkspaceId.FromCanonicalRoot(target);
        string dbPath = Path.Combine(target, ".miller", "symbols.db");
        harness.Registry.UpsertSeen(
            workspaceId,
            WorkspaceId.Display(target, workspaceId),
            target,
            dbPath,
            WorkspaceRegistryState.Ready);
        harness.Registry.MarkScanned(workspaceId, revision: 17);
        harness.Registry.MarkError(workspaceId, "prior failure");

        string output = harness.Tool.Workspace(operation: "open", path: target);

        Assert.Contains("status: error", output, StringComparison.Ordinal);
        WorkspaceRegistryRow? row = harness.Registry.Get(workspaceId);
        Assert.NotNull(row);
        Assert.Equal(WorkspaceRegistryState.Error, row.State);
        Assert.Equal("prior failure", row.LastError);
        Assert.Equal(17, row.LastRevision);
    }

    // ---- telemetry op sub-axis (decision-7) ----

    [Fact]
    public void Workspace_StampsTheOperationOntoTheAmbientTelemetryScope()
    {
        // D7: workspace is in the tool-breakdown WITH its operation sub-axis. The tool must stamp the ambient
        // scope's Op so the central filter's row records op=<operation> instead of NULL. The central filter opens
        // the scope with op=null; here a real Measure scope stands in for it as TelemetryContext.Current.
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        var (tool, _, ledger, _) = BuildTool(fx, builtRevision: 4, workspaceId: Ws);

        using (var scope = ledger.Measure("workspace", op: null))
        {
            tool.Workspace(operation: "full"); // non-leader path — deterministic, no live julie spawn
            Assert.Equal("full", scope.Op);
        }
    }

    [Fact]
    public void Workspace_DoesNotPersistAnUnvalidatedOperation()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        var (tool, _, ledger, _) = BuildTool(fx, builtRevision: 4, workspaceId: Ws);

        using var scope = ledger.Measure("workspace", op: null);
        tool.Workspace(operation: new string('x', 33));

        Assert.Null(scope.Op);
    }

    // ---- dashboard ----

    [Fact]
    public void Dashboard_StartsOrReusesDashboardFromMcpWorkspaceTool()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        var launcher = new RecordingDashboardLauncher(new DashboardLaunchResult(
            DashboardLaunchOutcome.Started,
            new Uri("http://127.0.0.1:4977/workspace?workspace_id=ws-tool-001"),
            ProcessId: 4242,
            Message: "started"));
        WorkspaceToolHarness harness = BuildHarness(
            fx,
            builtRevision: 4,
            workspaceId: Ws,
            dashboardLauncher: launcher);

        using var doc = JsonDocument.Parse(harness.Tool.Workspace(operation: "dashboard", format: "json"));

        JsonElement root = doc.RootElement;
        Assert.Equal("dashboard", root.GetProperty("operation").GetString());
        Assert.Equal("started", root.GetProperty("status").GetString());
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal("http://127.0.0.1:4977/workspace?workspace_id=ws-tool-001", root.GetProperty("url").GetString());
        Assert.Equal(4242, root.GetProperty("pid").GetInt32());
        DashboardLaunchRequest request = launcher.Requests.Single();
        Assert.Equal(4977, request.Port);
        Assert.Equal(harness.Workspace, request.Context);
    }

    [Fact]
    public void Dashboard_CorruptSidecarFailure_Json_IsTypedCorruption()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        WorkspaceToolHarness harness = BuildHarness(
            fx,
            builtRevision: 4,
            workspaceId: Ws,
            dashboardLauncher: new ThrowingDashboardLauncher(
                new InvalidDataException("dashboard sidecar is corrupt")));

        using var document = JsonDocument.Parse(
            harness.Tool.Workspace(operation: "dashboard", format: "json"));
        JsonElement diagnostic = document.RootElement.GetProperty("diagnostic");

        Assert.Equal("artifact_corrupt", diagnostic.GetProperty("code").GetString());
        Assert.Equal("corruption", diagnostic.GetProperty("class").GetString());
        Assert.Equal("error", diagnostic.GetProperty("outcome").GetString());
    }

    // ---- remove safety ----

    [Fact]
    public void Remove_LiveWorkspace_IsRefused_AndDoesNotDelete()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        var (tool, _, _, root) = BuildTool(fx, builtRevision: 4, workspaceId: Ws);
        string liveMiller = Path.Combine(root, ".miller");
        Directory.CreateDirectory(liveMiller);
        File.WriteAllText(Path.Combine(liveMiller, "sentinel.txt"), "live");

        string output = tool.Workspace(operation: "remove", path: root);

        Assert.Contains("refus", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("in use", output, StringComparison.OrdinalIgnoreCase);
        // The live .miller dir is untouched — the refusal is real, not a half-delete.
        Assert.True(File.Exists(Path.Combine(liveMiller, "sentinel.txt")));
    }

    [Fact]
    public void Remove_NotFound_IsNotAnError_NoMillerDir()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        var (tool, _, _, _) = BuildTool(fx, builtRevision: 4, workspaceId: Ws);
        string other = NewTempDir("other-empty"); // exists, but has no .miller

        string output = tool.Workspace(operation: "remove", path: other);
        Assert.Contains("not found", output, StringComparison.OrdinalIgnoreCase);
        Assert.False(output.StartsWith("workspace failed", StringComparison.Ordinal));
    }

    [Fact]
    public void Remove_UnregisteredPathWithMillerDirRefusesImplicitDeletion()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        WorkspaceToolHarness harness = BuildHarness(
            fx,
            builtRevision: 4,
            workspaceId: Ws,
            acquireLock: _ => new NoopLease());
        string other = NewTempDir("other-with-index");
        string otherMiller = Path.Combine(other, ".miller");
        Directory.CreateDirectory(otherMiller);
        File.WriteAllText(Path.Combine(otherMiller, "symbols.db"), "stub");

        string output = harness.Tool.Workspace(operation: "remove", path: other);

        Assert.Contains("not found", output, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(otherMiller));
    }

    [Fact]
    public void Remove_RegisteredWorkspaceId_UnregistersAndDeletesItsIndexDirWhenUnlocked()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        WorkspaceToolHarness harness = BuildHarness(
            fx,
            builtRevision: 4,
            workspaceId: Ws,
            acquireLock: _ => new NoopLease());
        string other = NewTempDir("remove-registered");
        string otherMiller = Path.Combine(other, ".miller");
        string otherDb = Path.Combine(otherMiller, "symbols.db");
        Directory.CreateDirectory(otherMiller);
        File.WriteAllText(otherDb, "stub");
        harness.Registry.UpsertSeen(OtherWs, "other-111111111111", other, otherDb, WorkspaceRegistryState.Ready);
        harness.Registry.MarkScanned(OtherWs, revision: 9);

        string output = harness.Tool.Workspace(operation: "remove", workspace_id: OtherWs);

        Assert.Contains("removed", output, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(otherMiller));
        Assert.Null(harness.Registry.Get(OtherWs));
    }

    [Fact]
    public void Remove_RegisteredWorkspaceWithMismatchedIndexPathRefusesWithoutDeleting()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        WorkspaceToolHarness harness = BuildHarness(
            fx,
            builtRevision: 4,
            workspaceId: Ws,
            acquireLock: _ => new NoopLease());
        string registeredRoot = NewTempDir("remove-corrupt-registry-root");
        string victimRoot = NewTempDir("remove-corrupt-registry-victim");
        string victimMiller = Path.Combine(victimRoot, ".miller");
        string victimDb = Path.Combine(victimMiller, "symbols.db");
        Directory.CreateDirectory(victimMiller);
        File.WriteAllText(victimDb, "do not delete");
        harness.Registry.UpsertSeen(
            OtherWs,
            "other-111111111111",
            registeredRoot,
            victimDb,
            WorkspaceRegistryState.Ready);

        using var doc = JsonDocument.Parse(harness.Tool.Workspace(
            operation: "remove",
            workspace_id: OtherWs,
            format: "json"));

        Assert.Equal(
            "workspace_remove_invalid_registration",
            doc.RootElement.GetProperty("diagnostic").GetProperty("code").GetString());
        Assert.Equal(
            "refused_invalid_registration",
            doc.RootElement.GetProperty("result").GetString());
        Assert.True(File.Exists(victimDb));
        Assert.NotNull(harness.Registry.Get(OtherWs));
    }

    [Fact]
    public void Remove_RegisteredWorkspaceId_MissingIndexDir_UnregistersAndReportsRegistryCleanup()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        WorkspaceToolHarness harness = BuildHarness(fx, builtRevision: 4, workspaceId: Ws);
        string other = NewTempDir("remove-missing-index");
        string otherMiller = Path.Combine(other, ".miller");
        string otherDb = Path.Combine(otherMiller, "symbols.db");
        harness.Registry.UpsertSeen(OtherWs, "other-111111111111", other, otherDb, WorkspaceRegistryState.Ready);
        harness.Registry.MarkScanned(OtherWs, revision: 9);

        string output = harness.Tool.Workspace(operation: "remove", workspace_id: OtherWs);

        Assert.Contains("removed", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("registry", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no index dir", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nothing to remove", output, StringComparison.OrdinalIgnoreCase);
        Assert.Null(harness.Registry.Get(OtherWs));
    }

    [Fact]
    public void Remove_RegisteredPath_GoneRoot_UnregistersAndReportsRegistryCleanup()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        WorkspaceToolHarness harness = BuildHarness(fx, builtRevision: 4, workspaceId: Ws);
        string other = NewTempDir("remove-gone-root");
        string otherMiller = Path.Combine(other, ".miller");
        string otherDb = Path.Combine(otherMiller, "symbols.db");
        harness.Registry.UpsertSeen(OtherWs, "other-111111111111", other, otherDb, WorkspaceRegistryState.Ready);
        harness.Registry.MarkScanned(OtherWs, revision: 9);
        Directory.Delete(other);

        string output = harness.Tool.Workspace(operation: "remove", path: other);

        Assert.Contains("removed", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("registry", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no index dir", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nothing to remove", output, StringComparison.OrdinalIgnoreCase);
        Assert.Null(harness.Registry.Get(OtherWs));
    }

    [Fact]
    public void Remove_RegisteredPath_GoneRootThroughSymlink_UnregistersAndReportsRegistryCleanup()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("Symbolic-link creation requires elevation / Developer Mode on Windows; POSIX-only test.");

        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        WorkspaceToolHarness harness = BuildHarness(fx, builtRevision: 4, workspaceId: Ws);
        string realParent = NewTempDir("remove-real-parent");
        string realRoot = Path.Combine(realParent, "workspace");
        Directory.CreateDirectory(realRoot);
        string linkParent = NewTempDir("remove-link-parent");
        string linkRoot = Path.Combine(linkParent, "alias");
        Directory.CreateSymbolicLink(linkRoot, realParent);
        string pathThroughLink = Path.Combine(linkRoot, "workspace");
        string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(pathThroughLink);
        string otherMiller = Path.Combine(canonicalRoot, ".miller");
        string otherDb = Path.Combine(otherMiller, "symbols.db");
        harness.Registry.UpsertSeen(OtherWs, "other-111111111111", canonicalRoot, otherDb, WorkspaceRegistryState.Ready);
        harness.Registry.MarkScanned(OtherWs, revision: 9);
        Directory.Delete(realRoot);

        string output = harness.Tool.Workspace(operation: "remove", path: pathThroughLink);

        Assert.Contains("removed", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("registry", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no index dir", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nothing to remove", output, StringComparison.OrdinalIgnoreCase);
        Assert.Null(harness.Registry.Get(OtherWs));
    }

    [Fact]
    public void Remove_CurrentWorkspaceId_IsRefusedAndKeepsRegistryRow()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        WorkspaceToolHarness harness = BuildHarness(fx, builtRevision: 4, workspaceId: Ws);
        string liveMiller = Path.Combine(harness.Root, ".miller");
        Directory.CreateDirectory(liveMiller);
        File.WriteAllText(Path.Combine(liveMiller, "sentinel.txt"), "live");

        string output = harness.Tool.Workspace(operation: "remove", workspace_id: Ws);

        Assert.Contains("refus", output, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(liveMiller, "sentinel.txt")));
        Assert.NotNull(harness.Registry.Get(Ws));
    }

    [Fact]
    public void Remove_RegisteredWorkspaceId_IsRefusedWhenAnotherWriterLockIsHeld()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        WorkspaceToolHarness harness = BuildHarness(fx, builtRevision: 4, workspaceId: Ws);
        string other = NewTempDir("remove-busy");
        string otherMiller = Path.Combine(other, ".miller");
        string otherDb = Path.Combine(otherMiller, "symbols.db");
        Directory.CreateDirectory(otherMiller);
        File.WriteAllText(otherDb, "stub");
        harness.Registry.UpsertSeen(OtherWs, "other-111111111111", other, otherDb, WorkspaceRegistryState.Ready);
        using SingleWriterLock? heldLease = SingleWriterLock.TryAcquire(otherMiller);
        Assert.NotNull(heldLease);

        string output = harness.Tool.Workspace(operation: "remove", workspace_id: OtherWs);

        Assert.Contains("in use", output, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(otherMiller));
        Assert.NotNull(harness.Registry.Get(OtherWs));
    }

    // ---- refresh / full: non-leader path (poll only, honest note) ----

    [Fact]
    public void Refresh_NonLeader_PollsOnly_AndConverges()
    {
        // The writer (another instance) has moved to revision 9; this reader's held index is at 2. A non-leader
        // refresh does NOT scan (it cannot — no writer lock) but its PollNow picks up the writer's persisted
        // revision and swaps. So: scanned=no, swapped=yes, revision=9.
        using var fx = CreateSynth(revision: 9, workspaceId: Ws);
        var (tool, _, _, _) = BuildTool(fx, builtRevision: 2, workspaceId: Ws);

        string output = tool.Workspace(operation: "refresh");

        Assert.Contains("scanned: no", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("swapped: yes", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("9", output);
    }

    [Fact]
    public void Refresh_CurrentWorkspaceId_StillUsesIndexerLeaderPath()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        WorkspaceToolHarness harness = BuildHarness(
            fx,
            builtRevision: 4,
            workspaceId: Ws,
            crossWorkspaceScan: (_, _, _, _, _) => throw new InvalidOperationException("current refresh must not use cross-workspace scan"));
        var ops = new RecordingScanOps();
        harness.Indexer.PublishOpsForTest(ops);

        string output = harness.Tool.Workspace(operation: "refresh", workspace_id: Ws);

        Assert.Equal(new[] { false }, ops.ScanForce);
        Assert.Contains("scanned: yes", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Refresh_CurrentWorkspaceJson_IncludesArtifactIdFromIndexDb()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        var (tool, _, _, _) = BuildTool(fx, builtRevision: 4, workspaceId: Ws);

        string output = tool.Workspace(operation: "refresh", format: "json");

        using var doc = JsonDocument.Parse(output);
        Assert.Equal("artifact-" + Ws, doc.RootElement.GetProperty("artifact_id").GetString());
    }

    [Fact]
    public void Refresh_RegisteredWorkspaceId_UsesCrossWorkspaceRefreshService()
    {
        using var current = CreateSynth(revision: 4, workspaceId: Ws);
        using var other = CreateSynth(revision: 9, workspaceId: OtherWs);
        bool? observedForce = null;
        WorkspaceToolHarness harness = BuildHarness(
            current,
            builtRevision: 4,
            workspaceId: Ws,
            crossWorkspaceScan: (root, db, force, _, _) =>
            {
                observedForce = force;
                return Report(root, db, OtherWs, revision: 10);
            });
        string otherRoot = Path.GetDirectoryName(other.DbPath)!;
        harness.Registry.UpsertSeen(OtherWs, "other-111111111111", otherRoot, other.DbPath, WorkspaceRegistryState.Ready);
        harness.Registry.MarkScanned(OtherWs, revision: 9);

        string output = harness.Tool.Workspace(operation: "refresh", workspace_id: OtherWs);

        Assert.False(observedForce);
        Assert.Contains("scanned: yes", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("status: refreshed", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("10", output);
    }

    [Fact]
    public void Refresh_RegisteredWorkspaceJson_FillsMissingArtifactIdFromStatusFacts()
    {
        using var current = CreateSynth(revision: 4, workspaceId: Ws);
        using var other = CreateSynth(revision: 9, workspaceId: OtherWs);
        WorkspaceToolHarness harness = BuildHarness(
            current,
            builtRevision: 4,
            workspaceId: Ws,
            crossWorkspaceScan: (root, db, _, _, _) => Report(root, db, OtherWs, revision: 9) with
            {
                Status = "no_change",
                Artifact = null,
            });
        string otherRoot = Path.GetDirectoryName(other.DbPath)!;
        harness.Registry.UpsertSeen(OtherWs, "other-111111111111", otherRoot, other.DbPath, WorkspaceRegistryState.Ready);
        harness.Registry.MarkScanned(OtherWs, revision: 9);

        string output = harness.Tool.Workspace(operation: "refresh", workspace_id: OtherWs, format: "json");

        using var doc = JsonDocument.Parse(output);
        Assert.Equal("artifact-" + OtherWs, doc.RootElement.GetProperty("artifact_id").GetString());
    }

    [Fact]
    public void Refresh_RegisteredWorkspaceId_PartialScanSurfacesWarningInOutput()
    {
        using var current = CreateSynth(revision: 4, workspaceId: Ws);
        using var other = CreateSynth(revision: 9, workspaceId: OtherWs);
        WorkspaceToolHarness harness = BuildHarness(
            current,
            builtRevision: 4,
            workspaceId: Ws,
            crossWorkspaceScan: (root, db, _, _, _) => PartialReport(root, db, OtherWs, revision: 10));
        string otherRoot = Path.GetDirectoryName(other.DbPath)!;
        harness.Registry.UpsertSeen(OtherWs, "other-111111111111", otherRoot, other.DbPath, WorkspaceRegistryState.Ready);
        harness.Registry.MarkScanned(OtherWs, revision: 9);

        string output = harness.Tool.Workspace(operation: "refresh", workspace_id: OtherWs);

        Assert.Contains("status: refreshed", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PARTIAL artifact", output, StringComparison.Ordinal);
        Assert.Contains("Controllers/Broken.cs", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Refresh_RegisteredWorkspaceId_NoChangeSlowFileWarningSurfacesWarningInOutput()
    {
        using var current = CreateSynth(revision: 4, workspaceId: Ws);
        using var other = CreateSynth(revision: 9, workspaceId: OtherWs);
        WorkspaceToolHarness harness = BuildHarness(
            current,
            builtRevision: 4,
            workspaceId: Ws,
            crossWorkspaceScan: (root, db, _, _, _) => SlowWarningReport(root, db, OtherWs, revision: 9));
        string otherRoot = Path.GetDirectoryName(other.DbPath)!;
        harness.Registry.UpsertSeen(OtherWs, "other-111111111111", otherRoot, other.DbPath, WorkspaceRegistryState.Ready);
        harness.Registry.MarkScanned(OtherWs, revision: 9);

        string output = harness.Tool.Workspace(operation: "refresh", workspace_id: OtherWs);

        Assert.Contains("status: unchanged", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("slow_file_skipped", output, StringComparison.Ordinal);
        Assert.Contains("Generated/Slow.kt", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Refresh_RegisteredWorkspaceFailureKeepsActionFactsAndAttachesTypedFailure()
    {
        using var current = CreateSynth(revision: 4, workspaceId: Ws);
        using var other = CreateSynth(revision: 9, workspaceId: OtherWs);
        WorkspaceToolHarness harness = BuildHarness(
            current,
            builtRevision: 4,
            workspaceId: Ws,
            crossWorkspaceScan: (_, _, _, _, _) => throw new IOException("extract unavailable"));
        string otherRoot = Path.GetDirectoryName(other.DbPath)!;
        harness.Registry.UpsertSeen(
            OtherWs,
            "other-111111111111",
            otherRoot,
            other.DbPath,
            WorkspaceRegistryState.Ready);
        harness.Registry.MarkScanned(OtherWs, revision: 9);

        using var doc = JsonDocument.Parse(harness.Tool.Workspace(
            operation: "refresh",
            workspace_id: OtherWs,
            format: "json"));

        Assert.Equal("failed", doc.RootElement.GetProperty("status").GetString());
        JsonElement diagnostic = doc.RootElement.GetProperty("diagnostic");
        Assert.Equal("workspace_refresh_failed", diagnostic.GetProperty("code").GetString());
        Assert.Equal("unavailable", diagnostic.GetProperty("class").GetString());
        Assert.Equal("error", diagnostic.GetProperty("outcome").GetString());
    }

    [Fact]
    public void Full_RegisteredWorkspacePath_UsesForceScanThroughCrossWorkspaceRefreshService()
    {
        using var current = CreateSynth(revision: 4, workspaceId: Ws);
        using var other = CreateSynth(revision: 9, workspaceId: OtherWs);
        bool? observedForce = null;
        WorkspaceToolHarness harness = BuildHarness(
            current,
            builtRevision: 4,
            workspaceId: Ws,
            crossWorkspaceScan: (root, db, force, _, _) =>
            {
                observedForce = force;
                return Report(root, db, OtherWs, revision: 11);
            });
        string otherRoot = Path.GetDirectoryName(other.DbPath)!;
        harness.Registry.UpsertSeen(OtherWs, "other-111111111111", otherRoot, other.DbPath, WorkspaceRegistryState.Ready);
        harness.Registry.MarkScanned(OtherWs, revision: 9);

        string output = harness.Tool.Workspace(operation: "full", path: otherRoot);

        Assert.True(observedForce);
        Assert.Contains("scanned: yes", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("status: refreshed", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("11", output);
    }

    // ---- W3 G1: a busy machine-wide governor is queued, not failed ----
    // Under the short interactive budget a refusal is routine in a fleet, so rendering it as a failed extract
    // sent agents hunting a log error that was never written while the rebuild was silently dropped.

    private static ScanGovernorLease HoldScanAdmission(string millerHome) =>
        ScanGovernor.ForMillerHome(millerHome).TryAcquire(
            new ScanGovernorRequest("/repo/other-worktree", "test-holder", 4),
            TimeSpan.Zero,
            CancellationToken.None)
        ?? throw new InvalidOperationException("A fresh temp miller home must have a free scan lease.");

    [Fact]
    public void Full_AsLeader_WithTheScanGovernorBusy_ReportsQueuedNotFailed()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        string governorHome = NewTempDir("governor-home");
        WorkspaceToolHarness harness = BuildHarness(
            fx, builtRevision: 4, workspaceId: Ws, scanGovernor: ScanGovernor.ForMillerHome(governorHome));
        var ops = new RecordingScanOps();
        harness.Indexer.PublishOpsForTest(ops);

        string output;
        using (ScanGovernorLease held = HoldScanAdmission(governorHome))
            output = harness.Tool.Workspace(operation: "full");

        Assert.Empty(ops.ScanForce);
        Assert.Contains("scanned: no", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("queued and will run without a retry", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("scan-governor owner", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("scan failed", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Check the Miller log", output, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("The recorded scan-governor owner is miller pid 4242 scanning '/repo/other'.")]
    [InlineData("This Miller instance is already busy with another operation on this workspace's index.")]
    public void QueuedRendering_NamesOnlyTheReportedHolder_SoASecondQueuedCauseIsNeverMisattributed(
        string holderDescription)
    {
        string note = WorkspaceTool.QueuedNote("full", holderDescription);
        ToolDiagnostic refusal = WorkspaceTool.QueuedRefusal("full", holderDescription);

        Assert.Contains(holderDescription, note, StringComparison.Ordinal);
        Assert.Contains(holderDescription, refusal.Message, StringComparison.Ordinal);
        foreach (string text in new[] { note, refusal.Message })
        {
            Assert.DoesNotContain("admission", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("another scan on this machine", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Full_AsLeader_WithTheScanGovernorBusy_LatchesTheRebuildInsteadOfDroppingIt()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        string governorHome = NewTempDir("governor-latch-home");
        WorkspaceToolHarness harness = BuildHarness(
            fx, builtRevision: 4, workspaceId: Ws, scanGovernor: ScanGovernor.ForMillerHome(governorHome));
        harness.Indexer.PublishOpsForTest(new RecordingScanOps());

        Assert.True(harness.Indexer.QueueEmpty);

        using (ScanGovernorLease held = HoldScanAdmission(governorHome))
            harness.Tool.Workspace(operation: "full");

        Assert.False(harness.Indexer.QueueEmpty);
    }

    [Fact]
    public void Full_AsLeader_WithTheScanGovernorBusy_IsNotAnErrorOutcome()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        string governorHome = NewTempDir("governor-json-home");
        WorkspaceToolHarness harness = BuildHarness(
            fx, builtRevision: 4, workspaceId: Ws, scanGovernor: ScanGovernor.ForMillerHome(governorHome));
        harness.Indexer.PublishOpsForTest(new RecordingScanOps());

        string json;
        using (ScanGovernorLease held = HoldScanAdmission(governorHome))
            json = harness.Tool.Workspace(operation: "full", format: "json");

        using var doc = JsonDocument.Parse(json);

        Assert.False(doc.RootElement.GetProperty("scanned").GetBoolean());
        JsonElement diagnostic = doc.RootElement.GetProperty("diagnostic");

        Assert.Equal("workspace_full_queued", diagnostic.GetProperty("code").GetString());
        Assert.Equal("refusal", diagnostic.GetProperty("class").GetString());
        Assert.NotEqual("error", diagnostic.GetProperty("outcome").GetString());
    }

    // ---- workspace open queue admission ----

    [Fact]
    public void Open_DoesNotWaitForTheScanGovernor()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        string governorHome = NewTempDir("governor-open-home");
        WorkspaceToolHarness harness = BuildHarness(
            fx,
            builtRevision: 4,
            workspaceId: Ws,
            enqueueOpenPrime: _ => WorkspaceOpenPrimeEnqueueResult.Queued,
            scanGovernor: ScanGovernor.ForMillerHome(governorHome));
        string target = NewTempDir("open-governor-busy");

        string output;
        using (ScanGovernorLease held = HoldScanAdmission(governorHome))
            output = harness.Tool.Workspace(operation: "open", path: target);

        Assert.Contains("status: refreshing", output, StringComparison.Ordinal);
        Assert.Contains("queued", output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            WorkspaceRegistryState.Refreshing,
            harness.Registry.Get(WorkspaceId.FromCanonicalRoot(PathCanonicalizer.CanonicalizeRoot(target)))?.State);
    }

    [Fact]
    public void Full_NonLeader_CannotForceRescan_ReportsHonestNote()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        var (tool, _, _, _) = BuildTool(fx, builtRevision: 4, workspaceId: Ws);

        string output = tool.Workspace(operation: "full");

        Assert.Contains("scanned: no", output, StringComparison.OrdinalIgnoreCase);
        // Honest: a non-leader cannot force a global rescan — never a faked success.
        Assert.Contains("not the indexer leader", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cannot force", output, StringComparison.OrdinalIgnoreCase);
    }

    // ---- refresh / full: leader path (scan runs through the published ops) ----

    [Fact]
    public void Full_AsLeader_ForceScans_ThenPollsAndSwaps()
    {
        // Publish recording ops so the indexer is the leader; full → TryScanAsLeader(force:true). The writer has
        // persisted revision 7; the held index is at 1, so the subsequent PollNow swaps to 7.
        using var fx = CreateSynth(revision: 7, workspaceId: Ws);
        var (tool, indexer, _, _) = BuildTool(fx, builtRevision: 1, workspaceId: Ws);
        var ops = new RecordingScanOps();
        indexer.PublishOpsForTest(ops);

        string output = tool.Workspace(operation: "full");

        Assert.Equal(new[] { true }, ops.ScanForce);                 // full = --force scan ran
        Assert.Contains("scanned: yes", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("swapped: yes", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("7", output);                                 // converged on the persisted revision
    }

    [Fact]
    public void Refresh_AsLeader_DeltaScans_ForceFalse()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        var (tool, indexer, _, _) = BuildTool(fx, builtRevision: 4, workspaceId: Ws);
        var ops = new RecordingScanOps();
        indexer.PublishOpsForTest(ops);

        string output = tool.Workspace(operation: "refresh");

        Assert.Equal(new[] { false }, ops.ScanForce);  // refresh = delta reconcile (no --force)
        Assert.Contains("scanned: yes", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Refresh_AsLeader_PartialScanSurfacesWarningInOutput()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        var (tool, indexer, _, root) = BuildTool(fx, builtRevision: 4, workspaceId: Ws);
        var ops = new RecordingPartialScanOps(root, fx.DbPath);
        indexer.PublishOpsForTest(ops);

        string output = tool.Workspace(operation: "refresh");

        Assert.Equal(new[] { false }, ops.ScanForce);
        Assert.Contains("PARTIAL artifact", output, StringComparison.Ordinal);
        Assert.Contains("Controllers/Broken.cs", output, StringComparison.Ordinal);
    }

    // ---- unknown operation ----

    [Fact]
    public void UnknownOperation_ReturnsUsageNote_NotAnError()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        var (tool, _, _, _) = BuildTool(fx, builtRevision: 4, workspaceId: Ws);

        string output = tool.Workspace(operation: "frobnicate");
        Assert.Contains("unknown workspace operation", output, StringComparison.OrdinalIgnoreCase);
        Assert.False(output.StartsWith("workspace failed", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownOperation_Json_AttachesTypedUnsupportedDiagnostic()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        var (tool, _, _, _) = BuildTool(fx, builtRevision: 4, workspaceId: Ws);

        string output = tool.Workspace(operation: "frobnicate", format: "json");

        using var document = JsonDocument.Parse(output);
        JsonElement diagnostic = document.RootElement.GetProperty("diagnostic");
        Assert.Equal("unsupported_operation", diagnostic.GetProperty("code").GetString());
        Assert.Equal("unsupported", diagnostic.GetProperty("class").GetString());
        Assert.Equal("empty", diagnostic.GetProperty("outcome").GetString());
    }

    [Fact]
    public void UnknownOperation_WithPathStillReturnsUnsupportedOperation()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        var (tool, _, _, root) = BuildTool(fx, builtRevision: 4, workspaceId: Ws);

        using var doc = JsonDocument.Parse(tool.Workspace(
            operation: "delete",
            path: root,
            format: "json"));

        Assert.Equal(
            "unsupported_operation",
            doc.RootElement.GetProperty("diagnostic").GetProperty("code").GetString());
    }

    [Fact]
    public void InvalidFormat_ReturnsTypedRefusalInsteadOfSilentlyUsingCompact()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        var (tool, _, _, _) = BuildTool(fx, builtRevision: 4, workspaceId: Ws);

        string output = tool.Workspace(format: "yaml");

        Assert.Contains("diagnostic_code=invalid_format", output, StringComparison.Ordinal);
        Assert.DoesNotContain("# workspace status", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ConflictingWorkspaceSelectors_ReturnTypedJsonRefusal()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        var (tool, _, _, root) = BuildTool(fx, builtRevision: 4, workspaceId: Ws);

        using var doc = JsonDocument.Parse(tool.Workspace(
            operation: "status",
            workspace_id: Ws,
            path: root,
            format: "json"));

        Assert.Equal(
            "conflicting_workspace_selectors",
            doc.RootElement.GetProperty("diagnostic").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void List_InvalidAgentLimit_ReturnsTypedJsonRefusal(int limit)
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        var (tool, _, _, _) = BuildTool(fx, builtRevision: 4, workspaceId: Ws);

        using var doc = JsonDocument.Parse(tool.Workspace(
            operation: "list",
            format: "json",
            limit: limit));

        Assert.Equal(
            "invalid_list_limit",
            doc.RootElement.GetProperty("diagnostic").GetProperty("code").GetString());
    }

    [Fact]
    public void Dashboard_InvalidPortRefusesBeforeLaunching()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        var launcher = new RecordingDashboardLauncher(new DashboardLaunchResult(
            DashboardLaunchOutcome.Started,
            new Uri("http://127.0.0.1:4977/"),
            ProcessId: 42,
            Message: "started"));
        WorkspaceToolHarness harness = BuildHarness(
            fx,
            builtRevision: 4,
            workspaceId: Ws,
            dashboardLauncher: launcher);

        using var doc = JsonDocument.Parse(harness.Tool.Workspace(
            operation: "dashboard",
            format: "json",
            port: 70000));

        Assert.Equal(
            "invalid_dashboard_port",
            doc.RootElement.GetProperty("diagnostic").GetProperty("code").GetString());
        Assert.Empty(launcher.Requests);
    }

    [Fact]
    public void UnknownWorkspaceId_GuidesToWorkspaceOpenPath()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        var (tool, _, _, _) = BuildTool(fx, builtRevision: 4, workspaceId: Ws);

        string output = tool.Workspace(operation: "status", workspace_id: "missing-workspace-id");

        Assert.Contains("unknown workspace selector", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("workspace(operation=\"list\"", output, StringComparison.OrdinalIgnoreCase);
        Assert.False(output.StartsWith("workspace failed", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownWorkspacePath_GuidesToWorkspaceOpenPath()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        var (tool, _, _, _) = BuildTool(fx, builtRevision: 4, workspaceId: Ws);
        string unregistered = NewTempDir("unregistered");

        string output = tool.Workspace(operation: "refresh", path: unregistered);

        Assert.Contains("unknown workspace path", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("workspace(operation=\"open\"", output, StringComparison.OrdinalIgnoreCase);
        Assert.False(output.StartsWith("workspace failed", StringComparison.Ordinal));
    }
}
