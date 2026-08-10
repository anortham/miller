using System.Text.Json;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Server;
using Miller.Server.Telemetry;
using Miller.Server.Tools;
using Xunit;

namespace Miller.Tests.Server;

public sealed class WorkspaceFactsAssemblerTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "miller-workspace-facts-" + Guid.NewGuid());

    public WorkspaceFactsAssemblerTests() => Directory.CreateDirectory(_temp);

    [Fact]
    public void StoreFactsUseThePinnedSessionGenerationAndTruthfulRollbackState()
    {
        var snapshot = new WorkspaceReadSnapshot(
            WorkspaceRoot: "/repo/worktree",
            WorkspaceId: "workspace-id",
            ArtifactOrStoreId: "11111111-1111-4111-8111-111111111111",
            ViewId: "view-worktree",
            Freshness: new WorkspaceFreshnessToken(
                "11111111-1111-4111-8111-111111111111",
                7,
                "blake3:manifest",
                91,
                "base-1:delta-3:91",
                StoreInstanceId: "11111111-1111-4111-8111-111111111111:GEN-00000000000000000007",
                ViewId: "view-worktree",
                GenerationName: "GEN-00000000000000000007",
                ManifestGeneration: 7,
                IndexLevel: "full",
                LevelStampL1: "l1",
                LevelStampL2: "l2",
                LevelStampL3: "l3"),
            IndexLevel: "full",
            Mode: WorkspaceReadMode.FamilyStore,
            GenerationName: "GEN-00000000000000000007",
            ManifestGeneration: 7,
            ResolutionState: "exact",
            ResolutionBaseId: "base-1",
            ResolutionDeltaGeneration: 3,
            ResolutionExactAt: 91);

        StoreWorkspaceFacts facts = WorkspaceFactsAssembler.StoreFactsFor(snapshot, legacyArtifactPresent: true);

        Assert.Equal("GEN-00000000000000000007", facts.GenerationName);
        Assert.Equal(7, facts.ManifestGeneration);
        Assert.Equal("exact", facts.ResolutionState);
        Assert.Equal("legacy_preserved", facts.MigrationState);
        Assert.Equal("available", facts.RollbackState);
    }

    [Fact]
    public void StoreIndexLevelFactsUseTheReadSessionLevel()
    {
        WorkspaceReadSnapshot snapshot = new(
            "/repo/worktree",
            "workspace-id",
            "family-id",
            "view-worktree",
            new WorkspaceFreshnessToken(
                "family-id",
                7,
                StoreLogSequence: 91,
                IndexLevel: IndexLevels.SymbolsMetadataValue),
            IndexLevels.SymbolsMetadataValue,
            WorkspaceReadMode.FamilyStore);

        IndexLevelFacts? facts = WorkspaceFactsAssembler.IndexLevelFactsFor(snapshot, "progressive");

        Assert.Equal(IndexLevels.SymbolsMetadataValue, facts?.Level);
        Assert.True(facts?.UpgradeOwed);
    }

    [Fact]
    public void StoreModeReportsBindingFailureInsteadOfFallingBackToTheLegacyArtifact()
    {
        using WorkspaceRegistry registry = WorkspaceRegistry.Open(Path.Combine(_temp, "store-failure.db"));
        string root = Path.Combine(_temp, "store-workspace");
        Directory.CreateDirectory(root);
        WorkspaceRegistryRow row = registry.UpsertSeen(
            "ws-store-failure",
            "store-failure",
            root,
            Path.Combine(root, ".miller", "symbols.db"),
            WorkspaceRegistryState.Ready);

        WorkspaceFacts facts = WorkspaceFactsAssembler.FromRegisteredRow(
            registry,
            row,
            WorkspaceRegisteredFactsProfile.CliStatus,
            new SymbolSearchSidecar(enabled: true),
            new ContentCorpusSidecar(),
            vectors: new VectorSidecar(SemanticMode.Off),
            storeEnabled: true);

        Assert.Equal("failed", facts.Store?.State);
        Assert.Equal("binding_not_ready", facts.Store?.Failure);
        Assert.Equal("store_failed", facts.FreshnessStatus);
    }

    [Fact]
    public void RegisteredStatusFacts_CliProfileDoesNotMarkMissingIndex()
    {
        using WorkspaceRegistry registry = WorkspaceRegistry.Open(Path.Combine(_temp, "workspaces.db"));
        string missingDb = Path.Combine(_temp, "missing", "symbols.db");
        WorkspaceRegistryRow row = registry.UpsertSeen(
            "ws-cli",
            "cli",
            Path.Combine(_temp, "workspace-cli"),
            missingDb,
            WorkspaceRegistryState.Ready);

        WorkspaceFacts facts = WorkspaceFactsAssembler.FromRegisteredRow(
            registry,
            row,
            WorkspaceRegisteredFactsProfile.CliStatus,
            new SymbolSearchSidecar(enabled: true),
            new ContentCorpusSidecar());

        Assert.Equal("ready", facts.FreshnessStatus);
        Assert.Null(facts.IndexFresh);
        Assert.Equal("index DB not found: " + missingDb, facts.WarningText);
        Assert.Equal(WorkspaceRegistryState.Ready, registry.Get("ws-cli")!.State);
    }

    [Fact]
    public void RegisteredStatusFacts_McpProfileMarksMissingIndexAndUsesTypedStatus()
    {
        using WorkspaceRegistry registry = WorkspaceRegistry.Open(Path.Combine(_temp, "workspaces.db"));
        string missingDb = Path.Combine(_temp, "missing", "symbols.db");
        WorkspaceRegistryRow row = registry.UpsertSeen(
            "ws-mcp",
            "mcp",
            Path.Combine(_temp, "workspace-mcp"),
            missingDb,
            WorkspaceRegistryState.Ready);

        WorkspaceFacts facts = WorkspaceFactsAssembler.FromRegisteredRow(
            registry,
            row,
            WorkspaceRegisteredFactsProfile.McpStatus,
            new SymbolSearchSidecar(enabled: true),
            new ContentCorpusSidecar());

        Assert.Equal("missing_index", facts.FreshnessStatus);
        Assert.False(facts.IndexFresh);
        Assert.Equal("Workspace index DB not found: " + missingDb, facts.WarningText);
        Assert.Equal(WorkspaceRegistryState.Missing, registry.Get("ws-mcp")!.State);
    }

    [Fact]
    public void RegisteredHealthFacts_CliProfileReportsMissingIndexWithoutMarkingRegistry()
    {
        using WorkspaceRegistry registry = WorkspaceRegistry.Open(Path.Combine(_temp, "workspaces.db"));
        string missingDb = Path.Combine(_temp, "missing-health-cli", "symbols.db");
        WorkspaceRegistryRow row = registry.UpsertSeen(
            "ws-cli-health",
            "clih",
            Path.Combine(_temp, "workspace-cli-health"),
            missingDb,
            WorkspaceRegistryState.Ready);

        WorkspaceFacts facts = WorkspaceFactsAssembler.FromRegisteredRow(
            registry,
            row,
            WorkspaceRegisteredFactsProfile.CliHealth,
            new SymbolSearchSidecar(enabled: true),
            new ContentCorpusSidecar());

        Assert.Equal("missing_index", facts.FreshnessStatus);
        Assert.False(facts.IndexFresh);
        Assert.Equal("index DB not found: " + missingDb, facts.WarningText);
        Assert.Equal(WorkspaceRegistryState.Ready, registry.Get("ws-cli-health")!.State);
    }

    [Fact]
    public void RegisteredHealthFacts_McpProfileReportsMissingIndexAndMarksRegistry()
    {
        using WorkspaceRegistry registry = WorkspaceRegistry.Open(Path.Combine(_temp, "workspaces.db"));
        string missingDb = Path.Combine(_temp, "missing-health-mcp", "symbols.db");
        WorkspaceRegistryRow row = registry.UpsertSeen(
            "ws-mcp-health",
            "mcph",
            Path.Combine(_temp, "workspace-mcp-health"),
            missingDb,
            WorkspaceRegistryState.Ready);

        WorkspaceFacts facts = WorkspaceFactsAssembler.FromRegisteredRow(
            registry,
            row,
            WorkspaceRegisteredFactsProfile.McpHealth,
            new SymbolSearchSidecar(enabled: true),
            new ContentCorpusSidecar());

        Assert.Equal("missing_index", facts.FreshnessStatus);
        Assert.False(facts.IndexFresh);
        Assert.Equal("Workspace index DB not found: " + missingDb, facts.WarningText);
        Assert.Equal(WorkspaceRegistryState.Missing, registry.Get("ws-mcp-health")!.State);
    }

    [Fact]
    public void RegisteredHealthReadError_McpProfileMarksRegistryError()
    {
        using WorkspaceRegistry registry = WorkspaceRegistry.Open(Path.Combine(_temp, "workspaces.db"));
        string dbPath = Path.Combine(_temp, "unreadable", "symbols.db");
        WorkspaceRegistryRow row = registry.UpsertSeen(
            "ws-mcp-unreadable",
            "mcpu",
            Path.Combine(_temp, "workspace-mcp-unreadable"),
            dbPath,
            WorkspaceRegistryState.Ready);

        WorkspaceFacts facts = WorkspaceFactsAssembler.FromRegisteredHealthReadError(
            registry,
            row,
            WorkspaceRegisteredFactsProfile.McpHealth,
            new SymbolSearchSidecar(enabled: true),
            new ContentCorpusSidecar(),
            new InvalidOperationException("schema is incomplete"));

        Assert.Equal("unreadable_index", facts.FreshnessStatus);
        Assert.False(facts.IndexFresh);
        Assert.Equal(
            $"could not read workspace index DB '{dbPath}': schema is incomplete",
            facts.WarningText);
        Assert.Equal(WorkspaceRegistryState.Error, registry.Get("ws-mcp-unreadable")!.State);
    }

    [Fact]
    public void UnregisteredLocalFactsUseCliUnknownFreshness()
    {
        string dbPath = Path.Combine(_temp, "local", "symbols.db");
        var context = new WorkspaceContext(
            WorkspaceRoot: Path.Combine(_temp, "local"),
            ExtractDbPath: dbPath,
            TelemetryDbPath: Path.Combine(_temp, "telemetry.db"),
            RegistryDbPath: Path.Combine(_temp, "workspaces.db"),
            ToolsRoot: Path.Combine(_temp, "tools"),
            WorkspaceId: null);

        WorkspaceFacts facts = WorkspaceFactsAssembler.FromUnregisteredLocal(
            context,
            new WorkspaceIndexFacts(DocumentCount: 17, KnownExtensionsCount: 3),
            new SymbolSearchSidecar(enabled: true),
            new ContentCorpusSidecar());

        Assert.Equal(context.WorkspaceRoot, facts.Root);
        Assert.Null(facts.WorkspaceId);
        Assert.Equal(17, facts.DocumentCount);
        Assert.Equal(3, facts.KnownExtensionsCount);
        Assert.Null(facts.IndexFresh);
        Assert.Equal("unregistered", facts.FreshnessStatus);
        Assert.True(facts.QueueEmpty);
    }

    [Fact]
    public void ToListEntriesUsesCallerCurrentPredicate()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var rows = new[]
        {
            new WorkspaceRegistryRow(
                "ws-a",
                "aaaa",
                "/workspace/a",
                "/workspace/a/.miller/symbols.db",
                now,
                now,
                12,
                WorkspaceRegistryState.Ready,
                LastError: null),
            new WorkspaceRegistryRow(
                "ws-b",
                "bbbb",
                "/workspace/b",
                "/workspace/b/.miller/symbols.db",
                now,
                LastScanAt: null,
                LastRevision: null,
                WorkspaceRegistryState.Missing,
                "gone"),
        };

        IReadOnlyList<WorkspaceListEntry> entries =
            WorkspaceFactsAssembler.ToListEntries(rows, row => row.WorkspaceId == "ws-b");

        Assert.Collection(
            entries,
            first =>
            {
                Assert.Equal("ws-a", first.WorkspaceId);
                Assert.False(first.Current);
                Assert.Equal("ready", first.State);
            },
            second =>
            {
                Assert.Equal("ws-b", second.WorkspaceId);
                Assert.True(second.Current);
                Assert.Equal("gone", second.LastError);
            });
    }

    [Fact]
    public void ToListEntriesCarriesLastSeenAtForRecencyOrdering()
    {
        DateTimeOffset seen = DateTimeOffset.UtcNow.AddMinutes(-42);
        var rows = new[]
        {
            new WorkspaceRegistryRow(
                "ws-a",
                "aaaa",
                "/workspace/a",
                "/workspace/a/.miller/symbols.db",
                seen,
                LastScanAt: null,
                LastRevision: 3,
                WorkspaceRegistryState.Ready,
                LastError: null),
        };

        IReadOnlyList<WorkspaceListEntry> entries =
            WorkspaceFactsAssembler.ToListEntries(rows, _ => false);

        Assert.Equal(seen, Assert.Single(entries).LastSeenAt);
    }

    [Fact]
    public void SemanticOff_FactsReportDisabledVectorsAndAnOffBrokerWithoutDerivingAnEndpoint()
    {
        using WorkspaceRegistry registry = WorkspaceRegistry.Open(Path.Combine(_temp, "workspaces.db"));
        WorkspaceRegistryRow row = registry.UpsertSeen(
            "ws-vec-off",
            "voff",
            Path.Combine(_temp, "workspace-vec-off"),
            Path.Combine(_temp, "workspace-vec-off", ".miller", "symbols.db"),
            WorkspaceRegistryState.Ready);

        WorkspaceFacts facts = WorkspaceFactsAssembler.FromRegisteredRow(
            registry,
            row,
            WorkspaceRegisteredFactsProfile.McpStatus,
            new SymbolSearchSidecar(enabled: true),
            new ContentCorpusSidecar(),
            VectorSidecar.Disabled);

        Assert.Equal("disabled", facts.Vectors!.State);
        Assert.Equal("off", facts.SemanticBroker!.State);
        Assert.Null(facts.SemanticBroker.EndpointIdentity);
        Assert.DoesNotContain("vectors:", WorkspaceRender.Status(facts, TelemetrySummary.Empty, json: false), StringComparison.Ordinal);
        Assert.Contains(
            "semantic_broker: off",
            WorkspaceRender.Status(facts, TelemetrySummary.Empty, json: false),
            StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(WorkspaceRender.Status(facts, TelemetrySummary.Empty, json: true));
        Assert.False(doc.RootElement.GetProperty("index").TryGetProperty("vectors", out _));
        JsonElement broker = doc.RootElement.GetProperty("semantic_broker");
        Assert.Equal("off", broker.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, broker.GetProperty("endpoint_identity").ValueKind);
        Assert.Equal(JsonValueKind.Null, broker.GetProperty("owner_pid").ValueKind);
    }

    [Fact]
    public void SemanticOn_WithoutArtifact_ReportsUnavailableWithReasonInCompactAndJson()
    {
        using WorkspaceRegistry registry = WorkspaceRegistry.Open(Path.Combine(_temp, "workspaces.db"));
        WorkspaceRegistryRow row = registry.UpsertSeen(
            "ws-vec-on",
            "von",
            Path.Combine(_temp, "workspace-vec-on"),
            Path.Combine(_temp, "workspace-vec-on", ".miller", "symbols.db"),
            WorkspaceRegistryState.Ready);

        WorkspaceFacts facts = WorkspaceFactsAssembler.FromRegisteredRow(
            registry,
            row,
            WorkspaceRegisteredFactsProfile.McpStatus,
            new SymbolSearchSidecar(enabled: true),
            new ContentCorpusSidecar(),
            new VectorSidecar(SemanticMode.On));

        Assert.Equal("unavailable", facts.Vectors!.State);
        Assert.Equal("not_started", facts.SemanticBroker!.State);
        Assert.Contains("vectors: unavailable (", WorkspaceRender.Status(facts, TelemetrySummary.Empty, json: false), StringComparison.Ordinal);
        Assert.Contains(
            "semantic_broker: not_started",
            WorkspaceRender.Status(facts, TelemetrySummary.Empty, json: false),
            StringComparison.Ordinal);

        using var doc = JsonDocument.Parse(WorkspaceRender.Status(facts, TelemetrySummary.Empty, json: true));
        JsonElement vectors = doc.RootElement.GetProperty("index").GetProperty("vectors");
        Assert.Equal("unavailable", vectors.GetProperty("state").GetString());
        Assert.False(string.IsNullOrWhiteSpace(vectors.GetProperty("reason").GetString()));
    }

    [Fact]
    public void PendingFiles_CaughtUpCursorIsZeroWithoutReadingTheDeltaJournal()
    {
        var facts = new VectorSidecarFacts("ready", "/repo/.miller/vectors.db", null)
        {
            SymbolCursor = new VectorCursorFacts("symbol", 42, 42, null, null),
            ChunkCursor = new VectorCursorFacts("chunk", 42, 42, null, null),
        };

        VectorSidecarFacts enriched = WorkspaceFactsAssembler.WithPendingFiles(
            facts,
            (_, _) => throw new InvalidOperationException("a caught-up cursor must not read the journal"));

        Assert.Equal(0, enriched.SymbolCursor!.PendingFiles);
        Assert.Equal(0, enriched.ChunkCursor!.PendingFiles);
    }

    [Fact]
    public void PendingFiles_BehindCursorCountsTheChangedPathsSinceItsCompletedRevision()
    {
        var facts = new VectorSidecarFacts("ready", "/repo/.miller/vectors.db", null)
        {
            ArtifactId = "art-1",
            SymbolCursor = new VectorCursorFacts("symbol", 40, 42, null, null),
            ChunkCursor = new VectorCursorFacts("chunk", 42, 42, null, null),
        };

        VectorSidecarFacts enriched = WorkspaceFactsAssembler.WithPendingFiles(
            facts,
            (from, artifactId) =>
            {
                Assert.Equal(40, from);
                Assert.Equal("art-1", artifactId);
                return new RevisionDeltaResult(
                    RevisionDeltaStatus.Complete, from, 42, artifactId, ["a.cs", "b.cs", "c.cs"], "complete");
            });

        Assert.Equal(3, enriched.SymbolCursor!.PendingFiles);
    }

    [Fact]
    public void PendingFiles_UnreconstructableSpanLeavesTheCountUnknownRatherThanGuessingZero()
    {
        var facts = new VectorSidecarFacts("ready", "/repo/.miller/vectors.db", null)
        {
            SymbolCursor = new VectorCursorFacts("symbol", 40, 42, null, null),
        };

        VectorSidecarFacts enriched = WorkspaceFactsAssembler.WithPendingFiles(
            facts,
            (from, artifactId) => new RevisionDeltaResult(
                RevisionDeltaStatus.Unavailable, from, 42, artifactId, [], "pruned_history"));

        Assert.Null(enriched.SymbolCursor!.PendingFiles);
    }

    [Fact]
    public void PendingFiles_DisabledFactsAreLeftUntouched()
    {
        var facts = new VectorSidecarFacts("disabled", "/repo/.miller/vectors.db", null);

        Assert.Same(facts, WorkspaceFactsAssembler.WithPendingFiles(
            facts,
            (_, _) => throw new InvalidOperationException("off means no journal read")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_temp))
            Directory.Delete(_temp, recursive: true);
    }

    // ---- W3 F5/F8: the scan-governor key, and the corroboration the holding_elsewhere fallback needs ----

    private string NewMillerHome() =>
        Directory.CreateDirectory(Path.Combine(_temp, "home-" + Guid.NewGuid().ToString("N"))).FullName;

    private static void WriteOwnerFile(ScanGovernor governor, int pid, string workspaceRoot)
    {
        Directory.CreateDirectory(governor.DirectoryPath);
        File.WriteAllText(
            governor.OwnerFilePath,
            JsonSerializer.Serialize(new
            {
                pid,
                workspace_root = workspaceRoot,
                reason = "leader-ondemand",
                jobs = 4,
                started_at_utc = DateTimeOffset.UtcNow,
            }));
    }

    [Fact]
    public void ScanGovernorFacts_PublishedUnderTheCanonicalRoot_IsFoundByTheUnresolvedRootReader()
    {
        string canonicalRoot = Path.Combine(_temp, "canonical-" + Guid.NewGuid().ToString("N"));
        string unresolvedRoot = Path.Combine(_temp, "unresolved-" + Guid.NewGuid().ToString("N"));
        var context = WorkspaceContext.Create(unresolvedRoot, AppContext.BaseDirectory, _temp) with
        {
            CanonicalRoot = canonicalRoot,
        };
        var request = new ScanGovernorRequest(canonicalRoot, "leader-drain-rescan", 4);
        long id = ScanGovernorState.Shared.EnterWaiting(request);
        try
        {
            WorkspaceFacts facts = WorkspaceFactsAssembler.FromUnregisteredLocal(
                context,
                new WorkspaceIndexFacts(0, 0),
                SymbolSearchSidecar.Disabled,
                new ContentCorpusSidecar(),
                VectorSidecar.Disabled);

            Assert.Equal(ScanGovernorStates.Waiting, facts.ScanGovernor?.State);
        }
        finally
        {
            ScanGovernorState.Shared.Exit(id, canonicalRoot);
        }
    }

    [Fact]
    public void ScanGovernorFacts_WaitingBehindADeadRecordedHolder_StillReportsWaiting_WithoutNamingThePid()
    {
        string root = Path.Combine(_temp, "waiting-" + Guid.NewGuid().ToString("N"));
        long id = ScanGovernorState.Shared.EnterWaiting(
            new ScanGovernorRequest(root, "leader-drain-rescan", 4),
            new ScanGovernorOwner(999_999, "/repo/dead-worktree", "leader-ondemand", 4, DateTimeOffset.UtcNow));
        try
        {
            ScanGovernorSnapshot? facts = WorkspaceFactsAssembler.ScanGovernorFacts(
                root, governor: null, isProcessAlive: (_, _) => false);

            Assert.Equal(ScanGovernorStates.Waiting, facts?.State);
            Assert.Null(facts?.HolderPid);
            Assert.Null(facts?.HolderWorkspaceRoot);
        }
        finally
        {
            ScanGovernorState.Shared.Exit(id, root);
        }
    }

    [Fact]
    public void ScanGovernorFacts_WaitingBehindALiveRecordedHolder_KeepsTheAttribution()
    {
        string root = Path.Combine(_temp, "waiting-" + Guid.NewGuid().ToString("N"));
        long id = ScanGovernorState.Shared.EnterWaiting(
            new ScanGovernorRequest(root, "leader-drain-rescan", 4),
            new ScanGovernorOwner(4242, "/repo/other-worktree", "leader-ondemand", 4, DateTimeOffset.UtcNow));
        try
        {
            ScanGovernorSnapshot? facts = WorkspaceFactsAssembler.ScanGovernorFacts(
                root, governor: null, isProcessAlive: (_, _) => true);

            Assert.Equal(ScanGovernorStates.Waiting, facts?.State);
            Assert.Equal(4242, facts?.HolderPid);
            Assert.Equal("/repo/other-worktree", facts?.HolderWorkspaceRoot);
        }
        finally
        {
            ScanGovernorState.Shared.Exit(id, root);
        }
    }

    [Fact]
    public void ScanGovernorFacts_OwnerFileNamingADeadProcess_RendersNothing()
    {
        ScanGovernor governor = ScanGovernor.ForMillerHome(NewMillerHome());
        WriteOwnerFile(governor, pid: 999_999, workspaceRoot: "/repo/dead-worktree");

        Assert.Null(WorkspaceFactsAssembler.ScanGovernorFacts(
            "/repo/mine", governor, isProcessAlive: (_, _) => false));
    }

    [Fact]
    public void ScanGovernorFacts_OwnerFileNamingThisProcess_IsNeverRenderedAsHoldingElsewhere()
    {
        ScanGovernor governor = ScanGovernor.ForMillerHome(NewMillerHome());
        using ScanGovernorLease? held = governor.TryAcquire(
            new ScanGovernorRequest("/repo/other-of-mine", "leader-ondemand", 4),
            TimeSpan.Zero,
            CancellationToken.None);

        Assert.NotNull(held);
        Assert.Null(WorkspaceFactsAssembler.ScanGovernorFacts("/repo/mine", governor));
    }

    [Fact]
    public void ScanGovernorFacts_LiveForeignHolder_RendersHoldingElsewhere()
    {
        ScanGovernor governor = ScanGovernor.ForMillerHome(NewMillerHome());
        WriteOwnerFile(governor, pid: Environment.ProcessId + 1, workspaceRoot: "/repo/other-worktree");

        ScanGovernorSnapshot? facts = WorkspaceFactsAssembler.ScanGovernorFacts(
            "/repo/mine", governor, isProcessAlive: (_, _) => true);

        Assert.Equal(ScanGovernorStates.HoldingElsewhere, facts?.State);
        Assert.Equal(Environment.ProcessId + 1, facts?.HolderPid);
        Assert.Equal("/repo/other-worktree", facts?.HolderWorkspaceRoot);
    }

    // The display path must corroborate WITHOUT opening the lease: two concurrent status reads would otherwise
    // corroborate each other over a free lease, and each would deny a real acquirer's poll while it probed.
    [Fact]
    public void ScanGovernorFacts_DoesNotTouchTheLease_SoAnAcquirerIsNeverBlocked()
    {
        string home = NewMillerHome();
        ScanGovernor governor = ScanGovernor.ForMillerHome(home);
        WriteOwnerFile(governor, pid: Environment.ProcessId + 1, workspaceRoot: "/repo/other-worktree");
        int probes = 0;

        ScanGovernorSnapshot? facts = WorkspaceFactsAssembler.ScanGovernorFacts(
            "/repo/mine",
            governor,
            isProcessAlive: (_, _) =>
            {
                probes++;
                using ScanGovernorLease? acquirer = ScanGovernor.ForMillerHome(home).TryAcquire(
                    new ScanGovernorRequest("/repo/acquirer", "leader-ondemand", 4),
                    TimeSpan.Zero,
                    CancellationToken.None);
                Assert.NotNull(acquirer);
                return true;
            });

        Assert.Equal(1, probes);
        Assert.Equal(ScanGovernorStates.HoldingElsewhere, facts?.State);
    }
}
