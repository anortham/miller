using System.ComponentModel;
using System.Text.Json;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Indexing.Store;
using Miller.Tests.Support;
using Miller.Server.Hosting;
using Miller.Server.Telemetry;
using Miller.Server.Tools;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the leader-visibility slice of <c>workspace health</c> (the diagnosis surface for the real-world
/// multi-process pile-up: several Miller servers per workspace, convergence owned by whichever leads — possibly
/// a dead or older-build process). Covers the warning matrix in <see cref="WorkspaceHealthFacts.Create"/> and
/// both render shapes. Leader facts are OPTIONAL: callers that cannot gather them (older paths, tests) keep the
/// exact pre-existing output.
/// </summary>
public sealed class WorkspaceHealthLeaderTests
{
    [Fact]
    public void HealthWarnsAboutOldWalDebtWithoutAttemptingCleanup()
    {
        using StoreFixture fixture = StoreFixture.Create();
        StoreWalCheckpoint.MarkOwed(fixture.Binding.StoreRoot);
        string marker = StoreWalCheckpoint.OwedPath(fixture.Binding.StoreRoot);
        var original = DateTime.UtcNow.AddMinutes(-6);
        File.SetLastWriteTimeUtc(marker, original);
        using var session = FamilyStoreReadSession.Open(fixture.Binding);
        var store = WorkspaceFactsAssembler.StoreFactsFor(session.Snapshot, false, fixture.Binding.StoreRoot);

        WorkspaceHealthFacts health = Health(Facts() with { Store = store }, null);

        Assert.Contains(health.Warnings, w => w.Code == "store_wal_checkpoint_owed");
        Assert.Contains(health.RecommendedActions, a => a.Contains("refresh", StringComparison.Ordinal));
        Assert.Equal(original, File.GetLastWriteTimeUtc(marker));
    }

    private static WorkspaceFacts Facts() => new WorkspaceFacts(
        Root: "/repo",
        WorkspaceId: "ws-123",
        DbPath: "/repo/.miller/symbols.db",
        IsLeader: false,
        DocumentCount: 10,
        KnownExtensionsCount: 2,
        BuiltRevision: 5,
        LatestObservedRevision: 5,
        IndexFresh: true,
        QueueEmpty: true)
        with
    { ServerVersion = "0.4.0+cafe123", ServerProcessId = 1111 };

    private static WorkspaceExtractionHealthFacts EmptyExtraction() => new(
        ParseDiagnostics: HealthFactSection<ParseDiagnosticGroup>.FromRows(Array.Empty<ParseDiagnosticGroup>()),
        CapabilityGaps: HealthFactSection<CapabilityGapGroup>.FromRows(Array.Empty<CapabilityGapGroup>()),
        LanguageCapabilities: HealthFactSection<LanguageCapabilitySummary>.FromRows(Array.Empty<LanguageCapabilitySummary>()),
        StructuralFacts: HealthFactSection<StructuralFactGroup>.FromRows(Array.Empty<StructuralFactGroup>()),
        ComplexityMetrics: HealthFactSection<ComplexityMetricGroup>.FromRows(Array.Empty<ComplexityMetricGroup>()),
        Files: HealthFactSection<FileStatusGroup>.FromRows(Array.Empty<FileStatusGroup>()));

    private static WorkspaceHealthFacts Health(WorkspaceFacts status, LeaderHealthFacts? leader) =>
        WorkspaceHealthFacts.Create(
            status, TelemetrySummary.Empty, new TelemetryHealthFacts(0, 0, 0), EmptyExtraction(), leader);

    private static LeaderIdentity Identity(
        int pid = 2222, string version = "0.4.0+cafe123", string? extractorVersion = null) =>
        new(pid, version, "/cache/0.3.6/miller", new DateTimeOffset(2026, 6, 10, 7, 0, 0, TimeSpan.Zero),
            extractorVersion);

    // ---- warning matrix ----

    [Fact]
    public void Create_DeadLeader_DegradesWithWarning()
    {
        var health = Health(Facts(), new LeaderHealthFacts(Identity(), Alive: false));

        HealthWarning warning = Assert.Single(health.Warnings, w => w.Code == "indexer_leader_dead");
        Assert.Equal("degraded", warning.Severity);
        Assert.Contains("2222", warning.Message);
        Assert.Equal(HealthState.Degraded, health.State);
        Assert.Contains(health.RecommendedActions, a => a.Contains("leader", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Create_LeaderVersionMismatch_WarnsWithBothVersions()
    {
        var health = Health(
            Facts(),
            new LeaderHealthFacts(Identity(version: "0.3.6+dead123"), Alive: true));

        HealthWarning warning = Assert.Single(health.Warnings, w => w.Code == "indexer_leader_version_mismatch");
        Assert.Equal("usable_with_warnings", warning.Severity);
        Assert.Contains("0.3.6+dead123", warning.Message);
        Assert.Contains("0.4.0+cafe123", warning.Message);
    }

    [Fact]
    public void Create_NoIdentityAndNotLeader_WarnsUnknownLeader()
    {
        var health = Health(Facts(), new LeaderHealthFacts(Identity: null, Alive: null));

        HealthWarning warning = Assert.Single(health.Warnings, w => w.Code == "indexer_leader_unknown");
        Assert.Equal("usable_with_warnings", warning.Severity);
    }

    [Fact]
    public void Create_ThisProcessLeads_NoLeaderWarnings()
    {
        var health = Health(
            Facts() with { IsLeader = true },
            new LeaderHealthFacts(Identity(pid: 1111), Alive: true));

        Assert.DoesNotContain(health.Warnings, w => w.Code.StartsWith("indexer_leader", StringComparison.Ordinal));
        Assert.Equal(HealthState.Ready, health.State);
    }

    [Fact]
    public void Create_MatchingLiveLeader_NoLeaderWarnings()
    {
        var health = Health(Facts(), new LeaderHealthFacts(Identity(), Alive: true));

        Assert.DoesNotContain(health.Warnings, w => w.Code.StartsWith("indexer_leader", StringComparison.Ordinal));
    }

    [Fact]
    public void Create_NoLeaderFacts_KeepsHistoricalBehavior()
    {
        var health = Health(Facts(), leader: null);

        Assert.Null(health.Leader);
        Assert.Empty(health.Warnings);
        Assert.Equal(HealthState.Ready, health.State);
    }

    [Fact]
    public void Create_UnavailableDerivedSidecars_KeepTypedWarningsAndRecoveryActions()
    {
        WorkspaceFacts facts = Facts() with
        {
            SearchSidecar = new SearchSidecarFacts(
                "missing",
                "/repo/.miller/search.db",
                Revision: null,
                ExpectedRevision: 5,
                DocumentCount: null,
                Error: "search artifact is missing"),
            ContentCorpus = new ContentCorpusFacts(
                "corrupt",
                "/repo/.miller/content.db",
                SchemaVersion: null,
                WorkspaceRevision: null,
                SourceCount: 0,
                ChunkCount: 0,
                IndexedSourceBytes: 0,
                StoredRawBytes: 0,
                Error: "content artifact is unreadable"),
        };

        WorkspaceHealthFacts health = Health(facts, leader: null);

        Assert.Contains(health.Warnings, warning =>
            warning.Code == "search_sidecar" &&
            warning.Message.Contains("missing", StringComparison.Ordinal));
        Assert.Contains(health.Warnings, warning =>
            warning.Code == "content_corpus" &&
            warning.Message.Contains("corrupt", StringComparison.Ordinal));
        Assert.Contains(health.RecommendedActions, action =>
            action.Contains("workspace refresh", StringComparison.Ordinal));
        Assert.Contains(health.RecommendedActions, action =>
            action.Contains("bounded backoff", StringComparison.Ordinal));
        Assert.DoesNotContain(health.RecommendedActions, action =>
            action.Contains("workspace full", StringComparison.Ordinal));
    }

    [Fact]
    public void Create_ImportsOnlyContentCorpus_RecommendsRefreshWithoutCorruptionRecovery()
    {
        WorkspaceFacts facts = Facts() with
        {
            ContentCorpus = new ContentCorpusFacts(
                "imports_only",
                "/repo/.miller/content.db",
                ContentCorpusSchema.SchemaVersion,
                WorkspaceRevision: null,
                SourceCount: 1,
                ChunkCount: 1,
                IndexedSourceBytes: 10,
                StoredRawBytes: 10),
        };

        WorkspaceHealthFacts health = Health(facts, leader: null);

        HealthWarning warning = Assert.Single(health.Warnings, row => row.Code == "content_corpus");
        Assert.Equal("usable_with_warnings", warning.Severity);
        Assert.Contains(
            health.RecommendedActions,
            action => action.Contains("workspace refresh", StringComparison.Ordinal));
        Assert.DoesNotContain(
            health.RecommendedActions,
            action => action.Contains("workspace full", StringComparison.Ordinal));
    }

    // ---- version-aware leadership warnings (D6): leader_extractor_older_than_artifact ----

    [Fact]
    public void Create_LiveLeaderOlderExtractor_WarnsLeaderExtractorOlderThanArtifact()
    {
        var health = Health(Facts(), new LeaderHealthFacts(
            Identity(extractorVersion: "2.1.3"), Alive: true,
            ArtifactExtractorVersion: "2.3.0"));

        HealthWarning warning = Assert.Single(health.Warnings, w => w.Code == "leader_extractor_older_than_artifact");
        Assert.Equal("usable_with_warnings", warning.Severity);
        Assert.Contains("2.1.3", warning.Message);
        Assert.Contains("2.3.0", warning.Message);
    }

    [Fact]
    public void Create_LiveLeaderCurrentExtractor_NoOutdatedLeaderWarning()
    {
        var health = Health(Facts(), new LeaderHealthFacts(
            Identity(extractorVersion: "2.3.0"), Alive: true, ArtifactExtractorVersion: "2.3.0"));

        Assert.DoesNotContain(health.Warnings, w => w.Code == "leader_extractor_older_than_artifact");
    }

    [Fact]
    public void Create_LeaderExtractorUnknown_NoOutdatedLeaderWarning()
    {
        // A pre-extractor-version identity (older build) records no extractor version — never guess a downgrade.
        var health = Health(Facts(), new LeaderHealthFacts(
            Identity(), Alive: true, ArtifactExtractorVersion: "2.3.0"));

        Assert.DoesNotContain(health.Warnings, w => w.Code == "leader_extractor_older_than_artifact");
    }

    [Fact]
    public void Create_DeadLeaderOlderExtractor_NoOutdatedLeaderWarning()
    {
        // The warning is about a LIVE leader that would regress the artifact; a dead one already warns dead.
        var health = Health(Facts(), new LeaderHealthFacts(
            Identity(extractorVersion: "2.1.3"), Alive: false, ArtifactExtractorVersion: "2.3.0"));

        Assert.DoesNotContain(health.Warnings, w => w.Code == "leader_extractor_older_than_artifact");
        Assert.Single(health.Warnings, w => w.Code == "indexer_leader_dead");
    }

    // ---- version-aware leadership warnings (D6): index_frozen_extractor_outdated ----

    [Fact]
    public void Create_IneligibleAndNoLeader_WarnsIndexFrozen()
    {
        var health = Health(Facts(), new LeaderHealthFacts(
            Identity: null, Alive: null,
            OwnExtractorVersion: "2.1.3",
            ArtifactExtractorVersion: "2.3.0",
            OwnVerdict: LeadershipEligibility.Evaluate("2.1.3", "2.3.0", allowDowngrade: false)));

        HealthWarning warning = Assert.Single(health.Warnings, w => w.Code == "index_frozen_extractor_outdated");
        Assert.Equal("degraded", warning.Severity);
        Assert.Equal(HealthState.Degraded, health.State);
        Assert.Contains(health.RecommendedActions, a =>
            a.Contains("restore-julie-extract") && a.Contains("MILLER_ALLOW_EXTRACTOR_DOWNGRADE"));
    }

    [Fact]
    public void Create_IneligibleAndDeadLeader_WarnsIndexFrozenAndLeaderDead()
    {
        var health = Health(Facts(), new LeaderHealthFacts(
            Identity(extractorVersion: "2.1.3"), Alive: false,
            OwnExtractorVersion: "2.1.3", ArtifactExtractorVersion: "2.3.0",
            OwnVerdict: LeadershipEligibility.Evaluate("2.1.3", "2.3.0", allowDowngrade: false)));

        Assert.Single(health.Warnings, w => w.Code == "index_frozen_extractor_outdated");
        Assert.Single(health.Warnings, w => w.Code == "indexer_leader_dead");
    }

    [Fact]
    public void Create_IneligibleButLiveLeader_NoFrozenWarning()
    {
        // A live (presumably eligible) leader still converges the index — nothing is frozen.
        var health = Health(Facts(), new LeaderHealthFacts(
            Identity(extractorVersion: "2.3.0"), Alive: true,
            OwnExtractorVersion: "2.1.3", ArtifactExtractorVersion: "2.3.0",
            OwnVerdict: LeadershipEligibility.Evaluate("2.1.3", "2.3.0", allowDowngrade: false)));

        Assert.DoesNotContain(health.Warnings, w => w.Code == "index_frozen_extractor_outdated");
    }

    [Fact]
    public void Create_EligibleAndNoLeader_NoFrozenWarning()
    {
        var health = Health(Facts(), new LeaderHealthFacts(
            Identity: null, Alive: null,
            OwnExtractorVersion: "2.3.0", ArtifactExtractorVersion: "2.3.0",
            OwnVerdict: LeadershipEligibility.Evaluate("2.3.0", "2.3.0", allowDowngrade: false)));

        Assert.DoesNotContain(health.Warnings, w => w.Code == "index_frozen_extractor_outdated");
    }

    [Fact]
    public void Create_NoVerdictGathered_NoFrozenWarning()
    {
        // Callers that cannot evaluate eligibility (no IndexerService, no probe) keep historical behavior.
        var health = Health(Facts(), new LeaderHealthFacts(Identity: null, Alive: null));

        Assert.DoesNotContain(health.Warnings, w => w.Code == "index_frozen_extractor_outdated");
    }

    // ---- liveness probe failure (the Windows elevated-pid-reuse crash, M1) ----

    [Fact]
    public void Read_ProbeThrows_DoesNotCrash_AndDoesNotReportLeaderDead()
    {
        string dir = Path.Combine(Path.GetTempPath(), "miller-health-leader-" + Guid.NewGuid().ToString("N"));
        string millerDir = Path.Combine(dir, ".miller");
        try
        {
            LeaderIdentityFile.Write(millerDir, new LeaderIdentity(
                4242, "0.4.0+cafe123", null, DateTimeOffset.UtcNow));

            // Win32Exception is NOT ArgumentException/InvalidOperationException — before the M1 hardening this
            // crashed `workspace health` on Windows when the recorded pid was reused by an elevated process.
            LeaderHealthFacts facts = LeaderHealthFacts.Read(
                millerDir, static _ => throw new Win32Exception(5 /* ERROR_ACCESS_DENIED */));

            Assert.NotNull(facts.Identity);
            // The collapse: a denied probe means a process with the pid exists but cannot be interrogated —
            // never spuriously degrade health with indexer_leader_dead on a mere probe failure.
            Assert.True(facts.Alive);
            var health = Health(Facts(), facts);
            Assert.DoesNotContain(health.Warnings, w => w.Code == "indexer_leader_dead");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    // ---- render ----

    [Fact]
    public void HealthCompact_ShowsLeaderLine()
    {
        string text = WorkspaceRender.Health(
            Health(Facts(), new LeaderHealthFacts(Identity(version: "0.3.6+dead123"), Alive: true)),
            json: false);

        Assert.Contains("leader:", text);
        Assert.Contains("2222", text);
        Assert.Contains("0.3.6+dead123", text);
    }

    [Fact]
    public void HealthJson_HasIndexerLeaderBlock()
    {
        using var doc = JsonDocument.Parse(WorkspaceRender.Health(
            Health(Facts(), new LeaderHealthFacts(Identity(), Alive: false)),
            json: true));
        var leader = doc.RootElement.GetProperty("indexer_leader");

        Assert.Equal(2222, leader.GetProperty("pid").GetInt32());
        Assert.Equal("0.4.0+cafe123", leader.GetProperty("version").GetString());
        Assert.Equal("/cache/0.3.6/miller", leader.GetProperty("process_path").GetString());
        Assert.False(leader.GetProperty("alive").GetBoolean());
        Assert.False(leader.GetProperty("this_process").GetBoolean());
    }

    [Fact]
    public void HealthJson_IndexerLeader_ExposesExtractorVersionAndOwnEligibility()
    {
        using var doc = JsonDocument.Parse(WorkspaceRender.Health(
            Health(Facts(), new LeaderHealthFacts(
                Identity(extractorVersion: "2.2.0"), Alive: true,
                OwnExtractorVersion: "2.1.3", ArtifactExtractorVersion: "2.3.0",
                OwnVerdict: LeadershipEligibility.Evaluate("2.1.3", "2.3.0", allowDowngrade: false))),
            json: true));
        var leader = doc.RootElement.GetProperty("indexer_leader");

        Assert.Equal("2.2.0", leader.GetProperty("extractor_version").GetString());
        Assert.Equal("2.1.3", leader.GetProperty("own_extractor_version").GetString());
        Assert.Equal("2.3.0", leader.GetProperty("artifact_extractor_version").GetString());
        var eligibility = leader.GetProperty("own_eligibility");
        Assert.False(eligibility.GetProperty("eligible").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(eligibility.GetProperty("reason").GetString()));
    }

    [Fact]
    public void HealthJson_IndexerLeader_NullExtractorFieldsWhenNotGathered()
    {
        using var doc = JsonDocument.Parse(WorkspaceRender.Health(
            Health(Facts(), new LeaderHealthFacts(Identity(), Alive: true)),
            json: true));
        var leader = doc.RootElement.GetProperty("indexer_leader");

        Assert.Equal(JsonValueKind.Null, leader.GetProperty("extractor_version").ValueKind);
        Assert.Equal(JsonValueKind.Null, leader.GetProperty("own_extractor_version").ValueKind);
        Assert.Equal(JsonValueKind.Null, leader.GetProperty("artifact_extractor_version").ValueKind);
        Assert.Equal(JsonValueKind.Null, leader.GetProperty("own_eligibility").ValueKind);
    }

    [Fact]
    public void HealthJson_NoLeaderFacts_OmitsNothingButStaysNull()
    {
        using var doc = JsonDocument.Parse(WorkspaceRender.Health(Health(Facts(), leader: null), json: true));

        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("indexer_leader").ValueKind);
    }

    // ---- W3: waiting on machine-wide scan admission ----
    // Queuing behind another worktree's scan is the governor working as designed: the index is still readable and
    // still served, so it must read as "usable with warnings", never as a degraded workspace an agent should
    // distrust. The warning exists so a slow refresh is diagnosable instead of looking like a hang.

    private static WorkspaceHealthFacts HealthWithGovernor(ScanGovernorSnapshot? governor) =>
        WorkspaceHealthFacts.Create(
            Facts() with { IsLeader = true, ScanGovernor = governor },
            TelemetrySummary.Empty,
            new TelemetryHealthFacts(0, 0, 0),
            EmptyExtraction());

    [Fact]
    public void Create_WaitingOnScanGovernor_WarnsWithoutDegrading()
    {
        WorkspaceHealthFacts health = HealthWithGovernor(new ScanGovernorSnapshot(
            ScanGovernorStates.Waiting, "leader-drain-rescan", DateTimeOffset.UtcNow.AddSeconds(-8),
            HolderPid: 4242, HolderWorkspaceRoot: "/repo/other-worktree"));

        HealthWarning warning = Assert.Single(
            health.Warnings, w => w.Code == "scan_waiting_on_machine_governor");
        Assert.Equal("usable_with_warnings", warning.Severity);
        Assert.Contains("4242", warning.Message, StringComparison.Ordinal);
        Assert.Contains("/repo/other-worktree", warning.Message, StringComparison.Ordinal);
        Assert.Equal(HealthState.UsableWithWarnings, health.State);
        Assert.Contains(
            health.RecommendedActions,
            a => a.Contains("MILLER_SCAN_GOVERNOR=0", StringComparison.Ordinal));
    }

    [Fact]
    public void Create_WaitingOnScanGovernorWithNoRecordedHolder_StillWarns()
    {
        WorkspaceHealthFacts health = HealthWithGovernor(new ScanGovernorSnapshot(
            ScanGovernorStates.Waiting, "leader-startup", DateTimeOffset.UtcNow, null, null));

        HealthWarning warning = Assert.Single(
            health.Warnings, w => w.Code == "scan_waiting_on_machine_governor");
        Assert.Contains("no live holder is recorded", warning.Message, StringComparison.Ordinal);
        Assert.Equal(HealthState.UsableWithWarnings, health.State);
    }

    [Fact]
    public void Create_ScanGovernorHoldingOrAbsent_AddsNoWarning()
    {
        ScanGovernorSnapshot?[] quiet =
        [
            null,
            new ScanGovernorSnapshot(
                ScanGovernorStates.Holding, "leader-ondemand", DateTimeOffset.UtcNow, null, null),
            new ScanGovernorSnapshot(
                ScanGovernorStates.HoldingElsewhere, "leader-ondemand", DateTimeOffset.UtcNow, 4242, "/repo/other"),
        ];

        Assert.All(quiet, governor => Assert.DoesNotContain(
            HealthWithGovernor(governor).Warnings, w => w.Code == "scan_waiting_on_machine_governor"));
    }
}
