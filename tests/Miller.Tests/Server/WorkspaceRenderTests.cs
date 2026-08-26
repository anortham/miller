using System.Text;
using System.Text.Json;
using Miller.Core.Freshness;
using Miller.Indexing;
using Miller.Indexing.Store;
using Miller.Server.Hosting;
using Miller.Server.Telemetry;
using Miller.Server.Tools;
using Miller.Server.Workspaces;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the PURE <c>workspace</c> renderers (M7 decision-2/6): given already-assembled fact records they produce
/// compact text or JSON, deterministically and with no I/O (the tool does the SQLite/subprocess work and hands
/// the facts in). Covers the status view (workspace identity + index facts + the embedded telemetry breakdown),
/// the list view (the current single workspace, honestly labelled), and the action/open/remove result lines —
/// both formats, including the honesty cases (a non-leader <c>full</c> note, a remove refusal).
/// </summary>
public sealed class WorkspaceRenderTests
{
    private static WorkspaceFacts Facts() => new(
        Root: "/repo",
        WorkspaceId: "ws-123",
        DbPath: "/repo/.miller/symbols.db",
        IsLeader: true,
        DocumentCount: 565,
        KnownExtensionsCount: 7,
        BuiltRevision: 42,
        LatestObservedRevision: 42,
        ArtifactId: "artifact-ws-123",
        IndexFresh: true,
        QueueEmpty: true);

    private static SemanticBrokerFacts BrokerFacts() => new(
        State: "ready",
        EndpointIdentity: "a5d53c7dd92b2107",
        Role: "owner",
        ServerVersion: "1",
        ModelId: "BAAI/bge-small-en-v1.5",
        ModelSha256: "sha256-model",
        Backend: "cuda",
        AcceleratorLeaseHeld: true,
        ReconnectCount: 2,
        SpawnAttempts: 1,
        RetiredOwnerCount: 1,
        OwnershipDegraded: false,
        OwnershipDegradedReason: null,
        BackendDegradedReason: null,
        OwnerProcessId: 4242);

    private static readonly TelemetrySummary Telemetry = new(
        new[]
        {
            new ToolStat("search", 10, 120.0, 250, 400, 1, 5000),
            new ToolStat("inspect", 1, 900.0, 900, 900, 0, 2000),
        },
        TotalCalls: 11, WindowStartTs: "2026-05-01T00:00:00.000Z", WindowEndTs: "2026-05-01T01:00:00.000Z",
        DroppedWrites: 0);

    private static JsonElement Json(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static void AssertNoResolutionKeys(JsonElement store)
    {
        Assert.False(store.TryGetProperty("resolution_state", out _));
        Assert.False(store.TryGetProperty("resolution_base_id", out _));
        Assert.False(store.TryGetProperty("resolution_delta_generation", out _));
        Assert.False(store.TryGetProperty("resolution_exact_at", out _));
    }

    private static WorkspaceExtractionHealthFacts ExtractionHealth() => new(
        ParseDiagnostics: HealthFactSection<ParseDiagnosticGroup>.FromRows(new[]
        {
            new ParseDiagnosticGroup("csharp", "parse_error", 2),
        }),
        CapabilityGaps: HealthFactSection<CapabilityGapGroup>.FromRows(new[]
        {
            new CapabilityGapGroup("typescript", "relationships", "open", 1),
        }),
        LanguageCapabilities: HealthFactSection<LanguageCapabilitySummary>.FromRows(new[]
        {
            new LanguageCapabilitySummary("csharp", 8, 7, 3, 2, 1, 1, 6, 5, 2, 1,
                KindCoverage:
                [
                    new KindCoverageDomain(
                        "doc_comments",
                        Supported: ["method"],
                        OpenGaps: [Json("\"property\"")],
                        NotApplicable: []),
                    new KindCoverageDomain(
                        "test_detection",
                        Supported: ["test_case"],
                        OpenGaps:
                        [
                            Json("""
                                {
                                  "kind": "test_lifecycle",
                                  "reason": "fixture lifecycle gap",
                                  "required_closure": "add lifecycle golden evidence",
                                  "planned_closure_task": "docs/plans/test-detection-closure.md"
                                }
                                """),
                            Json("""{"reason": "missing kind"}"""),
                        ],
                        NotApplicable: ["test_container"]),
                ]),
        }),
        StructuralFacts: HealthFactSection<StructuralFactGroup>.FromRows(new[]
        {
            new StructuralFactGroup("typescript", "typescript.await_expression.v1", "await_expression", 2),
        }),
        ComplexityMetrics: HealthFactSection<ComplexityMetricGroup>.FromRows(new[]
        {
            new ComplexityMetricGroup("typescript", "symbol", "julie-ast-complexity-v1", 1, 3, 1, 2, 4),
        }),
        Files: HealthFactSection<FileStatusGroup>.FromRows(new[]
        {
            new FileStatusGroup("csharp", "indexed", 1),
        }));

    private static WorkspaceOnboardingFacts OnboardingFacts() =>
        WorkspaceOnboardingFacts.Create(
            Facts(),
            new TelemetryOnboardingFacts(
                Available: true,
                State: "ready",
                TotalCalls: 4,
                WindowStartTs: "2026-06-23T10:00:00.000Z",
                WindowEndTs: "2026-06-23T10:04:00.000Z",
                ToolMix:
                [
                    new TelemetryToolMix("search", "auto", 2, 1, 1, 0, 67.5, 100, 100, 3, 720, 110),
                    new TelemetryToolMix("inspect", "summary", 1, 1, 0, 0, 40, 40, 40, 1, 300, 45),
                    new TelemetryToolMix("context", null, 1, 1, 0, 0, 900, 900, 900, 5, 3000, 700),
                ],
                SuccessfulFlows:
                [
                    new TelemetryFlow("search:auto", "inspect:summary", 1),
                ],
                TargetHashes:
                [
                    new TargetHashFrequency("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 2),
                ],
                CommonMisses:
                [
                    new TelemetryMiss("search", "auto", "no_symbol_hits", 1),
                ],
                Friction:
                [
                    new TelemetryFriction("context", null, 1, 900, 900, 900, 3000, 700, 0, 0),
                ],
                Error: null),
            [
                new RecoveredTargetHash(
                    "symbol_name_hash",
                    SymbolId: "sym-1",
                    Name: "GetUser",
                    Kind: "method",
                    Path: "auth/UserService.cs",
                    StartLine: 2,
                    Calls: 2,
                    CandidateCount: 1),
            ]);

    // ---- status ----

    [Fact]
    public void Status_Compact_ShowsWorkspaceIndexAndTelemetryFacts()
    {
        const string fullId = "abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890";
        string text = WorkspaceRender.Status(
            Facts() with { Root = "/repo/miller", WorkspaceId = fullId, ServerProcessId = 12345 },
            Telemetry,
            json: false);

        Assert.Contains("miller-abcdef123456", text);
        Assert.Contains("pid 12345", text);
        Assert.DoesNotContain(fullId, text);
        Assert.DoesNotContain("db:", text);
        Assert.Contains("565", text);            // document count
        Assert.Contains("42", text);             // built revision
        Assert.Contains("leader", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("telemetry:", text);      // concise telemetry summary
        Assert.Contains("search", text);         // busiest tool by calls
        Assert.Contains("250", text);            // a telemetry metric (p95)
    }

    // The line used to label the most-called tool `top=`, which readers took to mean "the slow one". The
    // busiest tool and the slowest tool are separate questions, so the line now answers both by name.
    [Fact]
    public void Status_Compact_TelemetryLine_NamesTheBusiestAndTheSlowestToolSeparately()
    {
        var summary = new TelemetrySummary(
            new[]
            {
                new ToolStat("search", 400, 220.0, 191, 900, 3, 5000),
                new ToolStat("inspect", 40, 1307.0, 8953, 20000, 0, 9000),
            },
            TotalCalls: 440, WindowStartTs: null, WindowEndTs: null, DroppedWrites: 0);

        string text = WorkspaceRender.Status(Facts(), summary, json: false);

        Assert.Contains("busiest=search p95=191ms", text, StringComparison.Ordinal);
        Assert.Contains("slowest=inspect p95=8953ms", text, StringComparison.Ordinal);
        Assert.DoesNotContain("top=", text, StringComparison.Ordinal);
    }

    // No tool reaches the call floor and "the busiest tool is also the slowest" both used to render as a line
    // with only `busiest=`, so the reader could not tell which one they were looking at. Say `n/a` for the first.
    [Fact]
    public void Status_Compact_TelemetryLine_SaysSlowestIsUnknown_WhenNoToolMeetsTheCallFloor()
    {
        var summary = new TelemetrySummary(
            new[]
            {
                new ToolStat("search", 4, 220.0, 191, 900, 0, 5000),
                new ToolStat("edit", 1, 90_000.0, 90_000, 90_000, 0, 10),
            },
            TotalCalls: 5, WindowStartTs: null, WindowEndTs: null, DroppedWrites: 0);

        string text = WorkspaceRender.Status(Facts(), summary, json: false);

        Assert.Contains("busiest=search p95=191ms  slowest=n/a", text, StringComparison.Ordinal);
        Assert.DoesNotContain("90000", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_Compact_TelemetryLine_OmitsSlowest_WhenItIsTheBusiestTool()
    {
        string text = WorkspaceRender.Status(Facts(), Telemetry, json: false);

        Assert.Contains("busiest=search p95=250ms", text, StringComparison.Ordinal);
        Assert.DoesNotContain("slowest=", text, StringComparison.Ordinal);
    }

    // A windowed summary must SAY it is windowed; an unlabelled figure reads as lifetime behaviour.
    [Fact]
    public void Status_Compact_TelemetryLine_NamesTheRollingWindow()
    {
        string windowed = WorkspaceRender.Status(
            Facts(), Telemetry with { WindowDays = 7 }, json: false);
        string lifetime = WorkspaceRender.Status(Facts(), Telemetry, json: false);

        Assert.Contains("telemetry: 7d  11 calls", windowed, StringComparison.Ordinal);
        Assert.Contains("telemetry: 11 calls", lifetime, StringComparison.Ordinal);
    }

    // DroppedWrites is a process-lifetime counter. Rendered bare inside a line labelled `7d` it reads as a
    // windowed figure, which is the one number on the line that would still misstate its span.
    [Fact]
    public void Status_Compact_TelemetryLine_SaysDroppedWritesSpanTheProcess_NotTheWindow()
    {
        var summary = Telemetry with { WindowDays = 7, DroppedWrites = 4 };

        string text = WorkspaceRender.Status(Facts(), summary, json: false);

        Assert.Contains("telemetry: 7d  11 calls", text, StringComparison.Ordinal);
        Assert.Contains("dropped=4 since start", text, StringComparison.Ordinal);
    }

    // Zero tool rows is exactly what a ledger whose every write fails looks like, and the line used to return
    // "" on that count before it ever read the drop counter. That hid the one signal that telemetry is broken.
    [Fact]
    public void Status_Compact_TelemetryLine_ReportsDroppedWrites_WhenNoToolRowsSurvived()
    {
        var summary = new TelemetrySummary(
            Array.Empty<ToolStat>(), TotalCalls: 0, WindowStartTs: null, WindowEndTs: null, DroppedWrites: 6)
        {
            WindowDays = 7,
        };

        string text = WorkspaceRender.Status(Facts(), summary, json: false);

        Assert.Contains("telemetry: 7d  0 calls  dropped=6 since start", text, StringComparison.Ordinal);
        Assert.DoesNotContain("busiest=", text, StringComparison.Ordinal);
        Assert.DoesNotContain("slowest=", text, StringComparison.Ordinal);
    }

    // A quiet workspace with a healthy ledger has nothing to say, and a line saying "0 calls" would be noise.
    [Fact]
    public void Status_Compact_TelemetryLine_StaysAbsent_WhenThereAreNoRowsAndNoDrops()
    {
        var summary = new TelemetrySummary(
            Array.Empty<ToolStat>(), TotalCalls: 0, WindowStartTs: null, WindowEndTs: null, DroppedWrites: 0)
        {
            WindowDays = 7,
        };

        string text = WorkspaceRender.Status(Facts(), summary, json: false);

        Assert.DoesNotContain("telemetry:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_Json_TelemetryObject_CarriesTheWindowDays()
    {
        JsonElement windowed = Json(
            WorkspaceRender.Status(Facts(), Telemetry with { WindowDays = 7 }, json: true))
            .GetProperty("telemetry");
        JsonElement lifetime = Json(WorkspaceRender.Status(Facts(), Telemetry, json: true))
            .GetProperty("telemetry");

        Assert.Equal(7, windowed.GetProperty("window_days").GetInt32());
        Assert.Equal(JsonValueKind.Null, lifetime.GetProperty("window_days").ValueKind);
    }

    [Fact]
    public void Status_Compact_ShowsBrokerIdentityAndHealthWithoutPathOrPid()
    {
        string text = WorkspaceRender.Status(
            Facts() with { SemanticBroker = BrokerFacts() },
            TelemetrySummary.Empty,
            json: false);

        Assert.Contains(
            "semantic_broker: ready  endpoint: a5d53c7dd92b2107  role: owner  server: 1  " +
            "model: BAAI/bge-small-en-v1.5  backend: cuda  accelerator_lease: held  " +
            "reconnects: 2  spawns: 1  retired_owners: 1",
            text);
        Assert.DoesNotContain("4242", text);
        Assert.DoesNotContain(".sock", text);
        Assert.DoesNotContain(@"\\.\pipe\", text);
    }

    [Fact]
    public void Status_AndHealth_SurfaceFamilyStoreProvenanceWithoutChangingLegacyOutput()
    {
        var store = new StoreWorkspaceFacts(
            FamilyId: "11111111-1111-4111-8111-111111111111",
            ViewId: "view-worktree",
            GenerationName: "GEN-00000000000000000007",
            ManifestGeneration: 7,
            ManifestHash: "blake3:manifest",
            StoreLogSequence: 91,
            IndexLevel: "full",
            LegacyArtifactPresent: true,
            MigrationState: "legacy_preserved",
            RollbackState: "available",
            StoreRoot: "/family/store",
            MemberDisplayLabels: ["alpha-111111111111", "bravo-222222222222"],
            MemberCount: 6);
        WorkspaceFacts facts = Facts() with { Store = store };

        string compact = WorkspaceRender.Status(facts, TelemetrySummary.Empty, json: false);
        string healthCompact = WorkspaceRender.Health(HealthFacts(facts), json: false);
        JsonElement statusJson = Json(WorkspaceRender.Status(facts, TelemetrySummary.Empty, json: true));
        JsonElement healthJson = Json(WorkspaceRender.Health(HealthFacts(facts), json: true));

        const string expected =
            "store: family=11111111-1111-4111-8111-111111111111  view=view-worktree  " +
            "generation=7  manifest=blake3:manifest  sequence=91  level=full  " +
            "migration=legacy_preserved  rollback=available";
        Assert.Contains(expected, compact);
        Assert.Contains(expected, healthCompact);
        Assert.DoesNotContain("resolution=", compact);
        Assert.DoesNotContain("resolution=", healthCompact);
        Assert.Contains("root=/family/store", compact);
        Assert.Contains("members=alpha-111111111111,bravo-222222222222 (+4 more)", compact);
        Assert.Equal("view-worktree", statusJson.GetProperty("store").GetProperty("view_id").GetString());
        Assert.Equal("/family/store", statusJson.GetProperty("store").GetProperty("store_root").GetString());
        Assert.Equal(6, statusJson.GetProperty("store").GetProperty("member_count").GetInt32());
        Assert.Equal(4, statusJson.GetProperty("store").GetProperty("members_omitted").GetInt32());
        Assert.Equal(
            new string?[] { "alpha-111111111111", "bravo-222222222222" },
            statusJson.GetProperty("store").GetProperty("member_display_labels")
                .EnumerateArray().Select(static value => value.GetString()).ToArray());
        Assert.Equal(7, statusJson.GetProperty("store").GetProperty("manifest_generation").GetInt64());
        AssertNoResolutionKeys(statusJson.GetProperty("store"));
        AssertNoResolutionKeys(healthJson.GetProperty("store"));
        Assert.NotEqual("resolving", statusJson.GetProperty("index").GetProperty("freshness_status").GetString());
        Assert.NotEqual("resolving", healthJson.GetProperty("index").GetProperty("freshness_status").GetString());
        Assert.Equal("available", healthJson.GetProperty("store").GetProperty("rollback_state").GetString());
        Assert.DoesNotContain("\"store\"", WorkspaceRender.Status(Facts(), TelemetrySummary.Empty, json: true));
    }

    [Fact]
    public void Status_AndHealth_SurfaceAWedgedCoordinatorQueue()
    {
        var queue = new StoreCoordinatorQueueFacts(
            QueuedCount: 3,
            ClaimedCount: 1,
            OldestQueuedAgeSeconds: 4200,
            DeadClaimOwner: "cli-4242",
            Groups: [new StoreCoordinatorQueueGroup("update", "queued", 3)],
            WedgedAfterSeconds: 300);
        WorkspaceFacts facts = Facts() with { Store = StoreFacts() with { Queue = queue } };

        string compact = WorkspaceRender.Status(facts, TelemetrySummary.Empty, json: false);
        string healthCompact = WorkspaceRender.Health(HealthFacts(facts), json: false);
        JsonElement statusJson = Json(WorkspaceRender.Status(facts, TelemetrySummary.Empty, json: true));
        JsonElement healthJson = Json(WorkspaceRender.Health(HealthFacts(facts), json: true));
        JsonElement summaryJson = Json(
            WorkspaceRender.Health(HealthFacts(facts), WorkspaceHealthFormat.JsonSummary));

        Assert.Contains("store_queue: WEDGED  queued 3, claimed 1; claim owner 'cli-4242' is gone", compact);
        Assert.Contains("store_queue: WEDGED  queued 3, claimed 1", healthCompact);

        JsonElement statusQueue = statusJson.GetProperty("store").GetProperty("queue");
        Assert.True(statusQueue.GetProperty("wedged").GetBoolean());
        Assert.Equal(3, statusQueue.GetProperty("queued_count").GetInt64());
        Assert.Equal(1, statusQueue.GetProperty("claimed_count").GetInt64());
        Assert.Equal(4200, statusQueue.GetProperty("oldest_queued_age_seconds").GetInt64());
        Assert.Equal("cli-4242", statusQueue.GetProperty("dead_claim_owner").GetString());
        Assert.Equal(300, statusQueue.GetProperty("wedged_after_seconds").GetInt64());
        JsonElement group = Assert.Single(statusQueue.GetProperty("groups").EnumerateArray().ToArray());
        Assert.Equal("update", group.GetProperty("kind").GetString());
        Assert.Equal("queued", group.GetProperty("state").GetString());
        Assert.Equal(3, group.GetProperty("count").GetInt64());

        Assert.True(healthJson.GetProperty("store").GetProperty("queue").GetProperty("wedged").GetBoolean());
        Assert.True(summaryJson.GetProperty("index").GetProperty("queue").GetProperty("wedged").GetBoolean());
    }

    [Fact]
    public void Status_AndHealth_StayByteIdenticalWhenTheCoordinatorQueueIsClean()
    {
        WorkspaceFacts facts = Facts() with { Store = StoreFacts() };

        Assert.Equal(
            WorkspaceRender.Status(facts, TelemetrySummary.Empty, json: false),
            WorkspaceRender.Status(facts with { Store = StoreFacts() with { Queue = null } },
                TelemetrySummary.Empty, json: false));
        Assert.DoesNotContain("store_queue:", WorkspaceRender.Status(facts, TelemetrySummary.Empty, json: false));
        Assert.DoesNotContain("store_queue:", WorkspaceRender.Health(HealthFacts(facts), json: false));
        Assert.DoesNotContain("\"queue\"", WorkspaceRender.Status(facts, TelemetrySummary.Empty, json: true));
        Assert.DoesNotContain("\"queue\"", WorkspaceRender.Health(HealthFacts(facts), json: true));
        Assert.DoesNotContain(
            "\"queue\"",
            WorkspaceRender.Health(HealthFacts(facts), WorkspaceHealthFormat.JsonSummary));
    }

    private static StoreWorkspaceFacts StoreFacts() => new(
        FamilyId: "11111111-1111-4111-8111-111111111111",
        ViewId: "view-worktree",
        GenerationName: "GEN-00000000000000000007",
        ManifestGeneration: 7,
        ManifestHash: "blake3:manifest",
        StoreLogSequence: 91,
        IndexLevel: "full",
        LegacyArtifactPresent: true,
        MigrationState: "legacy_preserved",
        RollbackState: "available",
        StoreRoot: "/family/store");

    [Fact]
    public void Status_SurfacesStoreIncompatibilityWithoutInventingManifestFacts()
    {
        WorkspaceFacts facts = Facts() with
        {
            Store = StoreWorkspaceFacts.Unavailable(
                "incompatible",
                "schema_incompatible",
                "store schema 3 is newer than supported schema 2"),
        };

        string compact = WorkspaceRender.Status(facts, TelemetrySummary.Empty, json: false);
        JsonElement json = Json(WorkspaceRender.Status(facts, TelemetrySummary.Empty, json: true));

        Assert.Contains("store: state=incompatible  failure=schema_incompatible", compact);
        Assert.Equal("incompatible", json.GetProperty("store").GetProperty("state").GetString());
        Assert.False(json.GetProperty("store").TryGetProperty("manifest_generation", out _));
    }

    // ---- W3 machine-wide scan governor (additive, must stay invisible when idle/disabled/absent) ----

    private static ScanGovernorSnapshot WaitingGovernor() => new(
        State: ScanGovernorStates.Waiting,
        Reason: "leader-drain-rescan",
        SinceUtc: DateTimeOffset.UtcNow.AddSeconds(-12),
        HolderPid: 4242,
        HolderWorkspaceRoot: "/repo/other-worktree");

    private static WorkspaceHealthFacts HealthFacts(WorkspaceFacts status) => new(
        StatusFacts: status,
        Telemetry: TelemetrySummary.Empty,
        TelemetryHealth: new TelemetryHealthFacts(0, 0, 0),
        Extraction: ExtractionHealth(),
        Warnings: [],
        RecommendedActions: [],
        State: HealthState.Ready,
        Summary: "ready");

    // Idle, disabled, and an uncorroborated owner record all produce a NULL fact — the only invisible shape
    // production emits, and therefore the only one worth pinning byte-identity against.
    [Fact]
    public void Status_Compact_IsByteIdenticalWhenTheScanGovernorFactIsAbsent()
    {
        string baseline = WorkspaceRender.Status(Facts(), Telemetry, json: false);

        Assert.Equal(
            baseline,
            WorkspaceRender.Status(Facts() with { ScanGovernor = null }, Telemetry, json: false));
    }

    [Fact]
    public void Status_Json_IsByteIdenticalWhenTheScanGovernorFactIsAbsent()
    {
        string baseline = WorkspaceRender.Status(Facts(), Telemetry, json: true);

        Assert.Equal(
            baseline,
            WorkspaceRender.Status(Facts() with { ScanGovernor = null }, Telemetry, json: true));
        Assert.DoesNotContain("scan_governor", baseline, StringComparison.Ordinal);
    }

    [Fact]
    public void Health_Compact_IsByteIdenticalWhenTheScanGovernorFactIsAbsent()
    {
        string baseline = WorkspaceRender.Health(HealthFacts(Facts()), json: false);

        Assert.Equal(
            baseline,
            WorkspaceRender.Health(HealthFacts(Facts() with { ScanGovernor = null }), json: false));
    }

    [Fact]
    public void Health_Json_IsByteIdenticalWhenTheScanGovernorFactIsAbsent()
    {
        string baseline = WorkspaceRender.Health(HealthFacts(Facts()), json: true);

        Assert.Equal(
            baseline,
            WorkspaceRender.Health(HealthFacts(Facts() with { ScanGovernor = null }), json: true));
        Assert.DoesNotContain("scan_governor", baseline, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_Compact_WaitingScanGovernor_NamesTheHolder()
    {
        string text = WorkspaceRender.Status(
            Facts() with { ScanGovernor = WaitingGovernor() }, TelemetrySummary.Empty, json: false);

        Assert.Contains("scan_governor: waiting 1", text, StringComparison.Ordinal);
        Assert.Contains("s (holder pid 4242 /repo/other-worktree)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_Compact_HoldingScanGovernor_ShowsTheReasonInsteadOfAHolder()
    {
        var holding = new ScanGovernorSnapshot(
            ScanGovernorStates.Holding, "leader-startup", DateTimeOffset.UtcNow.AddSeconds(-3), null, null);

        string text = WorkspaceRender.Status(
            Facts() with { ScanGovernor = holding }, TelemetrySummary.Empty, json: false);

        Assert.Contains("scan_governor: holding ", text, StringComparison.Ordinal);
        Assert.Contains("s (leader-startup)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("holder pid", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_Json_WaitingScanGovernor_ExposesTheHolderFacts()
    {
        JsonElement governor = Json(WorkspaceRender.Status(
                Facts() with { ScanGovernor = WaitingGovernor() }, TelemetrySummary.Empty, json: true))
            .GetProperty("scan_governor");

        Assert.Equal("waiting", governor.GetProperty("state").GetString());
        Assert.Equal("leader-drain-rescan", governor.GetProperty("reason").GetString());
        Assert.Equal(4242, governor.GetProperty("holder_pid").GetInt32());
        Assert.Equal("/repo/other-worktree", governor.GetProperty("holder_workspace_root").GetString());
        Assert.InRange(governor.GetProperty("waiting_seconds").GetInt64(), 12, 120);
        Assert.NotEqual(JsonValueKind.Null, governor.GetProperty("since_utc").ValueKind);
    }

    [Fact]
    public void Health_Compact_WaitingScanGovernor_NamesTheHolder()
    {
        string text = WorkspaceRender.Health(
            HealthFacts(Facts() with { ScanGovernor = WaitingGovernor() }), json: false);

        Assert.Contains("scan_governor: waiting 1", text, StringComparison.Ordinal);
        Assert.Contains("s (holder pid 4242 /repo/other-worktree)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Health_Json_WaitingScanGovernor_ExposesTheHolderFacts()
    {
        JsonElement governor = Json(WorkspaceRender.Health(
                HealthFacts(Facts() with { ScanGovernor = WaitingGovernor() }), json: true))
            .GetProperty("scan_governor");

        Assert.Equal("waiting", governor.GetProperty("state").GetString());
        Assert.Equal(4242, governor.GetProperty("holder_pid").GetInt32());
        Assert.Equal("/repo/other-worktree", governor.GetProperty("holder_workspace_root").GetString());
    }

    [Fact]
    public void Health_JsonSummary_OmitsTheScanGovernorEntirely()
    {
        JsonElement root = Json(WorkspaceRender.Health(
            HealthFacts(Facts() with { ScanGovernor = WaitingGovernor() }),
            WorkspaceHealthFormat.JsonSummary));

        Assert.False(root.TryGetProperty("scan_governor", out _));
    }

    // ---- persisted scan-failure record (W8 D6) ----

    private static ScanFailureRecord ScanFailure() => new(
        Intent: ScanIntent.UserFullRebuild,
        ExitCode: 137,
        ConsecutiveFailures: 3,
        Jobs: 1,
        LastFailureAtUtc: new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero),
        NextAttemptAtUtc: DateTimeOffset.UtcNow.AddMinutes(10));

    [Fact]
    public void Status_Compact_IsByteIdenticalWhenNoScanFailureIsRecorded()
    {
        string baseline = WorkspaceRender.Status(Facts(), Telemetry, json: false);

        Assert.Equal(
            baseline,
            WorkspaceRender.Status(Facts() with { ScanFailure = null }, Telemetry, json: false));
        Assert.DoesNotContain("scan_failure", baseline, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_Json_IsByteIdenticalWhenNoScanFailureIsRecorded()
    {
        string baseline = WorkspaceRender.Status(Facts(), Telemetry, json: true);

        Assert.Equal(
            baseline,
            WorkspaceRender.Status(Facts() with { ScanFailure = null }, Telemetry, json: true));
        Assert.DoesNotContain("scan_failure", baseline, StringComparison.Ordinal);
    }

    [Fact]
    public void Health_Json_IsByteIdenticalWhenNoScanFailureIsRecorded()
    {
        string baseline = WorkspaceRender.Health(HealthFacts(Facts()), json: true);

        Assert.Equal(
            baseline,
            WorkspaceRender.Health(HealthFacts(Facts() with { ScanFailure = null }), json: true));
        Assert.DoesNotContain("scan_failure", baseline, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_Compact_ARecordedScanFailure_ShowsTheIntentStreakExitCodeAndRetryTime()
    {
        string text = WorkspaceRender.Status(
            Facts() with { ScanFailure = ScanFailure() }, TelemetrySummary.Empty, json: false);

        Assert.Contains(
            "scan_failure: UserFullRebuild x3 exit 137 jobs 1 retry_at ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_Json_ARecordedScanFailure_ExposesEveryPersistedField()
    {
        JsonElement failure = Json(WorkspaceRender.Status(
                Facts() with { ScanFailure = ScanFailure() }, TelemetrySummary.Empty, json: true))
            .GetProperty("scan_failure");

        Assert.Equal("UserFullRebuild", failure.GetProperty("intent").GetString());
        Assert.Equal(137, failure.GetProperty("exit_code").GetInt32());
        Assert.Equal(3, failure.GetProperty("consecutive_failures").GetInt32());
        Assert.Equal(1, failure.GetProperty("jobs").GetInt32());
        Assert.Equal(
            "2026-08-02T12:00:00.0000000Z", failure.GetProperty("last_failure_utc").GetString());
        Assert.NotEqual(JsonValueKind.Null, failure.GetProperty("next_attempt_utc").ValueKind);
        Assert.InRange(failure.GetProperty("retry_in_seconds").GetInt64(), 500, 600);
    }

    [Fact]
    public void Status_Json_AScanFailureWithNoExitCode_RendersAnExplicitNull()
    {
        JsonElement failure = Json(WorkspaceRender.Status(
                Facts() with { ScanFailure = ScanFailure() with { ExitCode = null } },
                TelemetrySummary.Empty,
                json: true))
            .GetProperty("scan_failure");

        Assert.Equal(JsonValueKind.Null, failure.GetProperty("exit_code").ValueKind);
    }

    [Fact]
    public void Status_Compact_AScanFailureWithNoExitCode_OmitsTheExitClause()
    {
        string text = WorkspaceRender.Status(
            Facts() with { ScanFailure = ScanFailure() with { ExitCode = null } },
            TelemetrySummary.Empty,
            json: false);

        Assert.Contains("scan_failure: UserFullRebuild x3 jobs 1 retry_at ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_Json_APastRetryTime_ReportsZeroSecondsRemainingRatherThanANegative()
    {
        JsonElement failure = Json(WorkspaceRender.Status(
                Facts() with
                {
                    ScanFailure = ScanFailure() with { NextAttemptAtUtc = DateTimeOffset.UtcNow.AddHours(-1) },
                },
                TelemetrySummary.Empty,
                json: true))
            .GetProperty("scan_failure");

        Assert.Equal(0, failure.GetProperty("retry_in_seconds").GetInt64());
    }

    [Fact]
    public void Health_Compact_ARecordedScanFailure_ShowsTheStreak()
    {
        string text = WorkspaceRender.Health(
            HealthFacts(Facts() with { ScanFailure = ScanFailure() }), json: false);

        Assert.Contains("scan_failure: UserFullRebuild x3 exit 137 jobs 1", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Health_Json_ARecordedScanFailure_ExposesTheStreakAndRetryTime()
    {
        JsonElement failure = Json(WorkspaceRender.Health(
                HealthFacts(Facts() with { ScanFailure = ScanFailure() }), json: true))
            .GetProperty("scan_failure");

        Assert.Equal("UserFullRebuild", failure.GetProperty("intent").GetString());
        Assert.Equal(3, failure.GetProperty("consecutive_failures").GetInt32());
        Assert.InRange(failure.GetProperty("retry_in_seconds").GetInt64(), 500, 600);
    }

    // ---- rebind provenance (P3 §8, additive and conditional like scan_failure) ----

    private static RebindProvenanceFacts RebindProvenance() => new(
        SourceRoot: "/repo-main",
        SourceWorkspace: "repo-main-0123456789ab",
        SourceArtifactId: "artifact-source-77",
        ReboundAt: "2026-08-05T09:14:22.123456789Z");

    [Fact]
    public void Status_Compact_IsByteIdenticalWhenTheArtifactWasNeverRebound()
    {
        string baseline = WorkspaceRender.Status(Facts(), Telemetry, json: false);

        Assert.Equal(
            baseline,
            WorkspaceRender.Status(Facts() with { RebindProvenance = null }, Telemetry, json: false));
        Assert.DoesNotContain("rebound_from", baseline, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_Json_IsByteIdenticalWhenTheArtifactWasNeverRebound()
    {
        string baseline = WorkspaceRender.Status(Facts(), Telemetry, json: true);

        Assert.Equal(
            baseline,
            WorkspaceRender.Status(Facts() with { RebindProvenance = null }, Telemetry, json: true));
        Assert.DoesNotContain("rebound_from", baseline, StringComparison.Ordinal);
        Assert.False(Json(baseline).TryGetProperty("rebound_from", out _));
    }

    [Fact]
    public void Health_Compact_IsByteIdenticalWhenTheArtifactWasNeverRebound()
    {
        string baseline = WorkspaceRender.Health(HealthFacts(Facts()), json: false);

        Assert.Equal(
            baseline,
            WorkspaceRender.Health(HealthFacts(Facts() with { RebindProvenance = null }), json: false));
        Assert.DoesNotContain("rebound_from", baseline, StringComparison.Ordinal);
    }

    [Fact]
    public void Health_Json_IsByteIdenticalWhenTheArtifactWasNeverRebound()
    {
        string baseline = WorkspaceRender.Health(HealthFacts(Facts()), json: true);

        Assert.Equal(
            baseline,
            WorkspaceRender.Health(HealthFacts(Facts() with { RebindProvenance = null }), json: true));
        Assert.DoesNotContain("rebound_from", baseline, StringComparison.Ordinal);
        Assert.False(Json(baseline).TryGetProperty("rebound_from", out _));
    }

    [Fact]
    public void Status_Json_AReboundArtifact_ExposesEveryProvenanceField()
    {
        JsonElement rebound = Json(WorkspaceRender.Status(
                Facts() with { RebindProvenance = RebindProvenance() }, TelemetrySummary.Empty, json: true))
            .GetProperty("rebound_from");

        Assert.Equal("/repo-main", rebound.GetProperty("source_root").GetString());
        Assert.Equal("repo-main-0123456789ab", rebound.GetProperty("source_workspace").GetString());
        Assert.Equal("artifact-source-77", rebound.GetProperty("source_artifact_id").GetString());
        Assert.Equal("2026-08-05T09:14:22.123456789Z", rebound.GetProperty("rebound_at").GetString());
    }

    [Fact]
    public void Status_Json_AnUnregisteredSourceRoot_RendersANullSourceWorkspaceAndKeepsTheRoot()
    {
        JsonElement rebound = Json(WorkspaceRender.Status(
                Facts() with { RebindProvenance = RebindProvenance() with { SourceWorkspace = null } },
                TelemetrySummary.Empty,
                json: true))
            .GetProperty("rebound_from");

        Assert.Equal(JsonValueKind.Null, rebound.GetProperty("source_workspace").ValueKind);
        Assert.Equal("/repo-main", rebound.GetProperty("source_root").GetString());
    }

    [Fact]
    public void Status_Json_AProvenanceMissingTheOptionalKeys_RendersExplicitNulls()
    {
        JsonElement rebound = Json(WorkspaceRender.Status(
                Facts() with
                {
                    RebindProvenance =
                        RebindProvenance() with { SourceArtifactId = null, ReboundAt = null },
                },
                TelemetrySummary.Empty,
                json: true))
            .GetProperty("rebound_from");

        Assert.Equal(JsonValueKind.Null, rebound.GetProperty("source_artifact_id").ValueKind);
        Assert.Equal(JsonValueKind.Null, rebound.GetProperty("rebound_at").ValueKind);
    }

    [Fact]
    public void Status_Compact_AReboundArtifact_NamesTheSourceDisplayIdRootAndInstant()
    {
        string text = WorkspaceRender.Status(
            Facts() with { RebindProvenance = RebindProvenance() }, TelemetrySummary.Empty, json: false);

        Assert.Contains(
            "rebound_from: repo-main-0123456789ab (/repo-main) at 2026-08-05T09:14:22.123456789Z",
            text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Status_Compact_AnUnregisteredSourceRoot_NamesTheRawRoot()
    {
        string text = WorkspaceRender.Status(
            Facts() with { RebindProvenance = RebindProvenance() with { SourceWorkspace = null } },
            TelemetrySummary.Empty,
            json: false);

        Assert.Contains(
            "rebound_from: /repo-main at 2026-08-05T09:14:22.123456789Z", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_Compact_AProvenanceWithNoRecordedInstant_OmitsTheAtClause()
    {
        string text = WorkspaceRender.Status(
            Facts() with { RebindProvenance = RebindProvenance() with { ReboundAt = null } },
            TelemetrySummary.Empty,
            json: false);

        Assert.Contains("rebound_from: repo-main-0123456789ab (/repo-main)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("(/repo-main) at", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Health_Compact_AReboundArtifact_NamesTheSourceDisplayIdRootAndInstant()
    {
        string text = WorkspaceRender.Health(
            HealthFacts(Facts() with { RebindProvenance = RebindProvenance() }), json: false);

        Assert.Contains(
            "rebound_from: repo-main-0123456789ab (/repo-main) at 2026-08-05T09:14:22.123456789Z",
            text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Health_Json_AReboundArtifact_MatchesTheStatusJsonObject()
    {
        WorkspaceFacts facts = Facts() with { RebindProvenance = RebindProvenance() };

        JsonElement fromStatus = Json(WorkspaceRender.Status(facts, TelemetrySummary.Empty, json: true))
            .GetProperty("rebound_from");
        JsonElement fromHealth = Json(WorkspaceRender.Health(HealthFacts(facts), json: true))
            .GetProperty("rebound_from");

        Assert.Equal(fromStatus.GetRawText(), fromHealth.GetRawText());
    }

    [Fact]
    public void Health_JsonSummary_OmitsTheRebindProvenanceEntirely()
    {
        JsonElement root = Json(WorkspaceRender.Health(
            HealthFacts(Facts() with { RebindProvenance = RebindProvenance() }),
            WorkspaceHealthFormat.JsonSummary));

        Assert.False(root.TryGetProperty("rebound_from", out _));
    }

    // ---- status role string (version-aware leadership D6) ----

    [Fact]
    public void Status_Compact_OutdatedReader_ExplainsRoleWithVersions()
    {
        var leader = new LeaderHealthFacts(
            Identity: null, Alive: null,
            OwnExtractorVersion: "2.1.3",
            ArtifactExtractorVersion: "2.3.0",
            OwnVerdict: LeadershipEligibility.Evaluate("2.1.3", "2.3.0", allowDowngrade: false));

        string text = WorkspaceRender.Status(Facts() with { IsLeader = false }, Telemetry, json: false, leader);

        Assert.Contains("[reader (extractor outdated: own 2.1.3 < index 2.3.0)]", text);
    }

    [Fact]
    public void Status_Compact_EligibleReader_KeepsPlainReaderRole()
    {
        var leader = new LeaderHealthFacts(
            Identity: null, Alive: null,
            OwnExtractorVersion: "2.3.0",
            ArtifactExtractorVersion: "2.3.0",
            OwnVerdict: LeadershipEligibility.Evaluate("2.3.0", "2.3.0", allowDowngrade: false));

        string text = WorkspaceRender.Status(Facts() with { IsLeader = false }, Telemetry, json: false, leader);

        Assert.Contains("[reader]", text);
    }

    [Fact]
    public void Status_Compact_Leader_KeepsLeaderRoleRegardlessOfLeaderFacts()
    {
        string text = WorkspaceRender.Status(
            Facts(), Telemetry, json: false, new LeaderHealthFacts(Identity: null, Alive: null));

        Assert.Contains("[leader]", text);
    }

    [Fact]
    public void Status_Compact_NoLeaderFacts_KeepsPlainReaderRole()
    {
        string text = WorkspaceRender.Status(Facts() with { IsLeader = false }, Telemetry, json: false);

        Assert.Contains("[reader]", text);
    }

    // ---- onboarding ----

    [Fact]
    public void Onboarding_Compact_RendersTelemetryGuidanceWithoutRawHashes()
    {
        string text = WorkspaceRender.Onboarding(OnboardingFacts(), json: false);

        Assert.Contains("# workspace onboarding", text);
        Assert.Contains("start here:", text);
        Assert.Contains("search:auto -> inspect:summary", text);
        Assert.Contains("GetUser", text);
        Assert.Contains("auth/UserService.cs", text);
        Assert.Contains("no_symbol_hits", text);
        Assert.Contains("raw queries and targets are not stored", text);
        Assert.DoesNotContain("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", text);
    }

    [Fact]
    public void Onboarding_Compact_CollapsesUnresolvedHotTargetsIntoOneAggregateLine()
    {
        WorkspaceOnboardingFacts facts = WorkspaceOnboardingFacts.Create(
            Facts(),
            TelemetryOnboardingFacts.Unavailable("missing_telemetry_db"),
            [
                new RecoveredTargetHash(
                    "symbol_name_hash", SymbolId: "sym-1", Name: "GetUser", Kind: "method",
                    Path: "auth/UserService.cs", StartLine: 2, Calls: 5, CandidateCount: 1),
                new RecoveredTargetHash(
                    "unresolved_hash", SymbolId: null, Name: null, Kind: null,
                    Path: null, StartLine: null, Calls: 3, CandidateCount: 0),
                new RecoveredTargetHash(
                    "unresolved_hash", SymbolId: null, Name: null, Kind: null,
                    Path: null, StartLine: null, Calls: 2, CandidateCount: 0),
            ]);

        string text = WorkspaceRender.Onboarding(facts, json: false);

        // Resolved rows still render individually.
        Assert.Contains("- GetUser  auth/UserService.cs", text);
        // All unresolved rows collapse into exactly one aggregate line (2 rows, 3+2 calls).
        Assert.Contains("- unresolved repeated targets: 2 (5 calls total)", text);
        // The per-row unresolved label is never spent on its own line.
        Assert.DoesNotContain("- unresolved repeated target  unresolved_hash", text);
        Assert.Equal(1, CountOccurrences(text, "unresolved repeated targets:"));
    }

    [Fact]
    public void Onboarding_Compact_AllUnresolvedHotTargets_RendersOnlyAggregateLine()
    {
        WorkspaceOnboardingFacts facts = WorkspaceOnboardingFacts.Create(
            Facts(),
            TelemetryOnboardingFacts.Unavailable("missing_telemetry_db"),
            [
                new RecoveredTargetHash(
                    "unresolved_hash", null, null, null, null, null, Calls: 4, CandidateCount: 0),
                new RecoveredTargetHash(
                    "unresolved_hash", null, null, null, null, null, Calls: 1, CandidateCount: 0),
                new RecoveredTargetHash(
                    "unresolved_hash", null, null, null, null, null, Calls: 1, CandidateCount: 0),
            ]);

        string text = WorkspaceRender.Onboarding(facts, json: false);

        Assert.Contains("hot targets:", text);
        Assert.Contains("- unresolved repeated targets: 3 (6 calls total)", text);
        Assert.Equal(1, CountOccurrences(text, "unresolved repeated target"));
    }

    [Fact]
    public void Onboarding_Json_KeepsUnresolvedHotTargetsPerRow()
    {
        WorkspaceOnboardingFacts facts = WorkspaceOnboardingFacts.Create(
            Facts(),
            TelemetryOnboardingFacts.Unavailable("missing_telemetry_db"),
            [
                new RecoveredTargetHash(
                    "symbol_name_hash", SymbolId: "sym-1", Name: "GetUser", Kind: "method",
                    Path: "auth/UserService.cs", StartLine: 2, Calls: 5, CandidateCount: 1),
                new RecoveredTargetHash(
                    "unresolved_hash", null, null, null, null, null, Calls: 3, CandidateCount: 0),
                new RecoveredTargetHash(
                    "unresolved_hash", null, null, null, null, null, Calls: 2, CandidateCount: 0),
            ]);

        using var doc = JsonDocument.Parse(WorkspaceRender.Onboarding(facts, json: true));

        JsonElement[] targets = doc.RootElement.GetProperty("hot_targets").EnumerateArray().ToArray();
        Assert.Equal(3, targets.Length);
        Assert.Equal(2, targets.Count(t => t.GetProperty("confidence").GetString() == "unresolved_hash"));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal);
             i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    [Fact]
    public void Onboarding_Json_RendersStableSections()
    {
        using var doc = JsonDocument.Parse(WorkspaceRender.Onboarding(OnboardingFacts(), json: true));

        Assert.Equal("onboarding", doc.RootElement.GetProperty("operation").GetString());
        Assert.Equal("ready", doc.RootElement.GetProperty("telemetry").GetProperty("state").GetString());
        Assert.Equal(4, doc.RootElement.GetProperty("telemetry").GetProperty("total_calls").GetInt64());
        Assert.NotEmpty(doc.RootElement.GetProperty("start_here").EnumerateArray());
        Assert.NotEmpty(doc.RootElement.GetProperty("tool_mix").EnumerateArray());
        JsonElement target = Assert.Single(doc.RootElement.GetProperty("hot_targets").EnumerateArray());
        Assert.Equal("symbol_name_hash", target.GetProperty("confidence").GetString());
        Assert.Equal("GetUser", target.GetProperty("name").GetString());
        Assert.False(doc.RootElement.GetProperty("privacy").GetProperty("raw_targets_stored").GetBoolean());
    }

    [Fact]
    public void Onboarding_Json_AgentRowLimitBoundsTelemetrySections()
    {
        WorkspaceOnboardingFacts facts = OnboardingFacts();
        facts = facts with
        {
            Telemetry = facts.Telemetry with
            {
                ToolMixTotal = 7,
                SuccessfulFlowsTotal = 4,
                TargetHashesTotal = 5,
                CommonMissesTotal = 3,
                FrictionTotal = 6,
            },
        };
        using var doc = JsonDocument.Parse(WorkspaceRender.Onboarding(facts, json: true, rowLimit: 2));
        JsonElement root = doc.RootElement;

        Assert.Equal(2, root.GetProperty("tool_mix").GetArrayLength());
        Assert.Equal(7, root.GetProperty("tool_mix_total_count").GetInt32());
        Assert.Equal(5, root.GetProperty("tool_mix_omitted_count").GetInt32());
        Assert.Equal(4, root.GetProperty("successful_flows_total_count").GetInt32());
        Assert.Equal(3, root.GetProperty("successful_flows_omitted_count").GetInt32());
        Assert.Equal(5, root.GetProperty("hot_targets_total_count").GetInt32());
        Assert.Equal(4, root.GetProperty("hot_targets_omitted_count").GetInt32());
        Assert.Equal(3, root.GetProperty("common_misses_total_count").GetInt32());
        Assert.Equal(2, root.GetProperty("common_misses_omitted_count").GetInt32());
        Assert.Equal(6, root.GetProperty("friction_total_count").GetInt32());
        Assert.Equal(5, root.GetProperty("friction_omitted_count").GetInt32());
        Assert.Equal(
            facts.StartHere.Count - root.GetProperty("start_here").GetArrayLength(),
            root.GetProperty("start_here_omitted_count").GetInt32());
        Assert.Equal(
            facts.InstructionNotes.Count - root.GetProperty("instruction_notes").GetArrayLength(),
            root.GetProperty("instruction_notes_omitted_count").GetInt32());
        JsonElement privacy = root.GetProperty("privacy");
        Assert.Equal(facts.PrivacyNotes.Count, privacy.GetProperty("notes").GetArrayLength());
        Assert.Equal(facts.PrivacyNotes.Count, privacy.GetProperty("notes_total_count").GetInt32());
        Assert.Equal(0, privacy.GetProperty("notes_omitted_count").GetInt32());
        Assert.True(Encoding.UTF8.GetByteCount(root.GetRawText()) < 6 * 1024);
    }

    [Fact]
    public void Onboarding_Compact_ReportsExactOmissionsAndKeepsPrivacyComplete()
    {
        WorkspaceOnboardingFacts facts = OnboardingFacts();
        facts = facts with
        {
            Telemetry = facts.Telemetry with
            {
                ToolMixTotal = 7,
                SuccessfulFlowsTotal = 4,
                TargetHashesTotal = 5,
                CommonMissesTotal = 3,
                FrictionTotal = 6,
            },
        };

        string text = WorkspaceRender.Onboarding(facts, json: false, rowLimit: 1);

        Assert.Contains("omitted:", text);
        Assert.Contains("tool mix 6", text);
        Assert.Contains("successful flows 3", text);
        Assert.Contains("hot targets 4", text);
        Assert.Contains("common misses 2", text);
        Assert.Contains("friction 5", text);
        foreach (string privacyNote in facts.PrivacyNotes)
            Assert.Contains(privacyNote, text);
    }

    [Fact]
    public void Onboarding_NoTelemetry_StillRendersGenericStarterGuidance()
    {
        WorkspaceOnboardingFacts facts = WorkspaceOnboardingFacts.Create(
            Facts(),
            TelemetryOnboardingFacts.Unavailable("missing_telemetry_db"),
            []);

        string text = WorkspaceRender.Onboarding(facts, json: false);

        Assert.Contains("workspace health", text);
        Assert.Contains("search to find candidate symbols", text);
        Assert.Contains("inspect depth=overview", text);
        Assert.Contains("use context", text);
        Assert.Contains("use impact before refactors", text);
        Assert.Contains("telemetry is unavailable", text);
    }

    [Fact]
    public void Onboarding_NoTelemetry_JsonIncludesOverviewStarterGuidance()
    {
        WorkspaceOnboardingFacts facts = WorkspaceOnboardingFacts.Create(
            Facts(),
            TelemetryOnboardingFacts.Unavailable("missing_telemetry_db"),
            []);

        using var doc = JsonDocument.Parse(WorkspaceRender.Onboarding(facts, json: true));

        string[] startHere = doc.RootElement.GetProperty("start_here")
            .EnumerateArray()
            .Select(static row => row.GetString() ?? string.Empty)
            .ToArray();
        Assert.Contains(startHere, static line => line.Contains("inspect depth=overview", StringComparison.Ordinal));
    }

    [Fact]
    public void Onboarding_AggregateTelemetry_NotesFullInspectOveruseAndMissingTrace()
    {
        WorkspaceOnboardingFacts facts = WorkspaceOnboardingFacts.Create(
            Facts(),
            new TelemetryOnboardingFacts(
                Available: true,
                State: "ready",
                TotalCalls: 9,
                WindowStartTs: "2026-06-23T10:00:00.000Z",
                WindowEndTs: "2026-06-23T10:09:00.000Z",
                ToolMix:
                [
                    new TelemetryToolMix("search", "auto", 3, 3, 0, 0, 50, 60, 70, 3, 600, 100),
                    new TelemetryToolMix("inspect", "full", 5, 5, 0, 0, 80, 90, 100, 5, 5000, 1000),
                    new TelemetryToolMix("inspect", "overview", 1, 1, 0, 0, 30, 40, 50, 1, 700, 150),
                ],
                SuccessfulFlows:
                [
                    new TelemetryFlow("search:auto", "inspect:full", 2),
                ],
                TargetHashes: [],
                CommonMisses: [],
                Friction: [],
                Error: null),
            []);

        string text = WorkspaceRender.Onboarding(facts, json: false);

        Assert.Contains("inspect depth=overview", text);
        Assert.Contains("trace is available for refs/path questions", text);
        Assert.Contains("use impact before refactors", text);
    }

    [Fact]
    public void Onboarding_AggregateTelemetry_SurfacesTraceContentPatternsGuidance()
    {
        WorkspaceOnboardingFacts facts = WorkspaceOnboardingFacts.Create(
            Facts(),
            new TelemetryOnboardingFacts(
                Available: true,
                State: "ready",
                TotalCalls: 12,
                WindowStartTs: "2026-06-23T10:00:00.000Z",
                WindowEndTs: "2026-06-23T10:09:00.000Z",
                ToolMix:
                [
                    new TelemetryToolMix("trace", "path", 4, 0, 4, 0, 50, 60, 70, 0, 600, 100),
                    new TelemetryToolMix("content", "read", 3, 0, 0, 3, 80, 90, 100, 0, 500, 100),
                    new TelemetryToolMix("search", "auto", 5, 5, 0, 0, 30, 40, 50, 3, 700, 150),
                ],
                SuccessfulFlows: [],
                TargetHashes: [],
                CommonMisses: [],
                Friction: [],
                Error: null),
            []);

        string text = WorkspaceRender.Onboarding(facts, json: false);
        using var doc = JsonDocument.Parse(WorkspaceRender.Onboarding(facts, json: true));
        string[] notes = doc.RootElement.GetProperty("instruction_notes")
            .EnumerateArray()
            .Select(static row => row.GetString() ?? string.Empty)
            .ToArray();

        Assert.Contains("trace mode=refs", text);
        Assert.Contains("search mode=source", text);
        Assert.Contains("source_id from content search", text);
        Assert.Contains("workspace_id", text);
        Assert.Contains("patterns operation=list", text);
        Assert.Contains(notes, static note => note.Contains("trace mode=refs", StringComparison.Ordinal));
        Assert.Contains(notes, static note => note.Contains("source_id from content search", StringComparison.Ordinal));
        Assert.Contains(notes, static note => note.Contains("patterns operation=list", StringComparison.Ordinal));
    }

    [Fact]
    public void Status_Json_HasWorkspaceIndexAndTelemetrySections()
    {
        using var doc = JsonDocument.Parse(
            WorkspaceRender.Status(Facts() with { ServerProcessId = 12345 }, Telemetry, json: true));
        var root = doc.RootElement;

        var ws = root.GetProperty("workspace");
        Assert.Equal("/repo", ws.GetProperty("root").GetString());
        Assert.Equal("ws-123", ws.GetProperty("workspace_id").GetString());
        Assert.True(ws.GetProperty("leader").GetBoolean());
        Assert.Equal(12345, ws.GetProperty("server_pid").GetInt32());

        var idx = root.GetProperty("index");
        Assert.Equal(565, idx.GetProperty("document_count").GetInt64());
        Assert.Equal(7, idx.GetProperty("known_extensions").GetInt64());
        Assert.Equal(42, idx.GetProperty("built_revision").GetInt64());
        Assert.Equal(42, idx.GetProperty("latest_revision").GetInt64());
        Assert.Equal("artifact-ws-123", idx.GetProperty("artifact_id").GetString());
        Assert.True(idx.GetProperty("index_fresh").GetBoolean());
        Assert.True(idx.GetProperty("queue_empty").GetBoolean());

        // The telemetry breakdown is embedded as a nested object (the same shape TelemetryRender.Json emits).
        Assert.Equal(11, root.GetProperty("telemetry").GetProperty("total_calls").GetInt64());
    }

    [Fact]
    public void Status_Json_ExposesExhaustiveBrokerDiagnostics()
    {
        JsonElement broker = Json(WorkspaceRender.Status(
                Facts() with { SemanticBroker = BrokerFacts() },
                TelemetrySummary.Empty,
                json: true))
            .GetProperty("semantic_broker");

        Assert.Equal("ready", broker.GetProperty("state").GetString());
        Assert.Equal("a5d53c7dd92b2107", broker.GetProperty("endpoint_identity").GetString());
        Assert.Equal("owner", broker.GetProperty("role").GetString());
        Assert.Equal("1", broker.GetProperty("server_version").GetString());
        Assert.Equal("BAAI/bge-small-en-v1.5", broker.GetProperty("model_id").GetString());
        Assert.Equal("cuda", broker.GetProperty("backend").GetString());
        Assert.True(broker.GetProperty("accelerator_lease_held").GetBoolean());
        Assert.Equal(2, broker.GetProperty("reconnect_count").GetInt32());
        Assert.Equal(1, broker.GetProperty("spawn_attempts").GetInt32());
        Assert.Equal(1, broker.GetProperty("retired_owner_count").GetInt32());
        Assert.Equal(4242, broker.GetProperty("owner_pid").GetInt32());
    }

    [Fact]
    public void Status_Json_IncludesIndexerLeaderFactsWhenProvided()
    {
        var leader = new LeaderHealthFacts(
            new LeaderIdentity(
                2222,
                "0.5.8+cafe123",
                "/cache/miller",
                new DateTimeOffset(2026, 6, 23, 12, 0, 0, TimeSpan.Zero),
                ExtractorVersion: "2.5.2"),
            Alive: true,
            OwnExtractorVersion: "2.5.1",
            ArtifactExtractorVersion: "2.5.2",
            OwnVerdict: LeadershipEligibility.Evaluate("2.5.1", "2.5.2", allowDowngrade: false));

        using var doc = JsonDocument.Parse(WorkspaceRender.Status(
            Facts() with { IsLeader = false },
            TelemetrySummary.Empty,
            json: true,
            leader));
        Assert.Equal(
            "reader (extractor outdated: own 2.5.1 < index 2.5.2)",
            doc.RootElement.GetProperty("workspace").GetProperty("role").GetString());
        JsonElement leaderJson = doc.RootElement.GetProperty("indexer_leader");

        Assert.False(leaderJson.GetProperty("this_process").GetBoolean());
        Assert.Equal(2222, leaderJson.GetProperty("pid").GetInt32());
        Assert.True(leaderJson.GetProperty("alive").GetBoolean());
        Assert.Equal("2.5.2", leaderJson.GetProperty("extractor_version").GetString());
        Assert.Equal("2.5.1", leaderJson.GetProperty("own_extractor_version").GetString());
        Assert.Equal("2.5.2", leaderJson.GetProperty("artifact_extractor_version").GetString());
        Assert.False(leaderJson.GetProperty("own_eligibility").GetProperty("eligible").GetBoolean());
    }

    [Fact]
    public void Status_Json_EqualExtractorVersions_ArtifactFieldEqualsReasonToken()
    {
        const string version = "2.33.5";
        LeadershipVerdict verdict = LeadershipEligibility.Evaluate(version, version, allowDowngrade: false);
        var leader = new LeaderHealthFacts(
            Identity: null,
            Alive: null,
            OwnExtractorVersion: version,
            ArtifactExtractorVersion: version,
            OwnVerdict: verdict);

        using var doc = JsonDocument.Parse(WorkspaceRender.Status(
            Facts(),
            TelemetrySummary.Empty,
            json: true,
            leader));
        JsonElement leaderJson = doc.RootElement.GetProperty("indexer_leader");
        string? artifactField = leaderJson.GetProperty("artifact_extractor_version").GetString();
        string? reason = leaderJson.GetProperty("own_eligibility").GetProperty("reason").GetString();

        Assert.Equal(version, artifactField);
        Assert.Contains("matches", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("newer", reason, StringComparison.Ordinal);
        Assert.Contains($"index artifact {artifactField}", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_Json_OlderArtifact_ReasonNamesTheDisplayedVersionAndSchedulesUpgrade()
    {
        const string own = "2.33.5";
        const string artifact = "2.33.2";
        LeadershipVerdict verdict = LeadershipEligibility.Evaluate(own, artifact, allowDowngrade: false);
        var leader = new LeaderHealthFacts(
            Identity: null,
            Alive: null,
            OwnExtractorVersion: own,
            ArtifactExtractorVersion: artifact,
            OwnVerdict: verdict);

        using var doc = JsonDocument.Parse(WorkspaceRender.Status(
            Facts(),
            TelemetrySummary.Empty,
            json: true,
            leader));
        JsonElement leaderJson = doc.RootElement.GetProperty("indexer_leader");
        string? artifactField = leaderJson.GetProperty("artifact_extractor_version").GetString();
        string? reason = leaderJson.GetProperty("own_eligibility").GetProperty("reason").GetString();

        Assert.Equal(artifact, artifactField);
        Assert.Contains("newer", reason, StringComparison.Ordinal);
        Assert.Contains($"index artifact {artifactField}", reason, StringComparison.Ordinal);
        Assert.True(verdict.ArtifactOlderThanOwn);
    }

    [Fact]
    public void Status_Json_NullWorkspaceIdAndUnknownFreshness_AreJsonNull()
    {
        var facts = Facts() with { WorkspaceId = null, IndexFresh = null };

        using var doc = JsonDocument.Parse(WorkspaceRender.Status(facts, TelemetrySummary.Empty, json: true));
        var root = doc.RootElement;
        Assert.Equal(JsonValueKind.Null, root.GetProperty("workspace").GetProperty("workspace_id").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("index").GetProperty("index_fresh").ValueKind);
    }

    [Fact]
    public void Status_Compact_StaleIndex_IsCalledOut()
    {
        var facts = Facts() with { BuiltRevision = 40, LatestObservedRevision = 42, IndexFresh = false };

        string text = WorkspaceRender.Status(facts, TelemetrySummary.Empty, json: false);
        Assert.Contains("stale", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Status_Compact_ShowsSearchSidecarState()
    {
        var facts = Facts() with
        {
            SearchSidecar = new SearchSidecarFacts(
                "stale", "/repo/.miller/search.db", Revision: 41, ExpectedRevision: 42, DocumentCount: 565, Error: null),
        };

        string text = WorkspaceRender.Status(facts, TelemetrySummary.Empty, json: false);

        Assert.Contains("search_db:", text);
        Assert.Contains("stale", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("expected 42", text);
    }

    [Fact]
    public void Status_Compact_StaleSearchSidecar_ReportsRevisionDirectionHonestly()
    {
        var facts = Facts() with
        {
            SearchSidecar = new SearchSidecarFacts(
                "stale", "/repo/.miller/search.db", Revision: 44, ExpectedRevision: 42, DocumentCount: 565, Error: null),
        };

        string text = WorkspaceRender.Status(facts, TelemetrySummary.Empty, json: false);

        Assert.Contains("built 44 > expected 42", text);
        Assert.DoesNotContain("built 44 < expected 42", text);
    }

    [Fact]
    public void Status_Compact_ShowsContentCorpusState()
    {
        var facts = Facts() with
        {
            ContentCorpus = new ContentCorpusFacts(
                "stale", "/repo/.miller/content.db", SchemaVersion: 1, WorkspaceRevision: 41,
                SourceCount: 12, ChunkCount: 48, IndexedSourceBytes: 1200, StoredRawBytes: 1600),
        };

        string text = WorkspaceRender.Status(facts, TelemetrySummary.Empty, json: false);

        Assert.Contains("content_db:", text);
        Assert.Contains("stale", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rev 41", text);
        Assert.Contains("chunks 48", text);
    }

    [Fact]
    public void Status_Json_ShowsSearchSidecarObject()
    {
        var facts = Facts() with
        {
            SearchSidecar = new SearchSidecarFacts(
                "current", "/repo/.miller/search.db", Revision: 42, ExpectedRevision: 42, DocumentCount: 565, Error: null),
        };

        using var doc = JsonDocument.Parse(WorkspaceRender.Status(facts, TelemetrySummary.Empty, json: true));
        JsonElement sidecar = doc.RootElement.GetProperty("index").GetProperty("search_sidecar");

        Assert.Equal("current", sidecar.GetProperty("state").GetString());
        Assert.Equal(42, sidecar.GetProperty("revision").GetInt64());
        Assert.Equal(42, sidecar.GetProperty("expected_revision").GetInt64());
        Assert.Equal(565, sidecar.GetProperty("document_count").GetInt32());
    }

    [Fact]
    public void Status_Json_ShowsContentCorpusObject()
    {
        var facts = Facts() with
        {
            ContentCorpus = new ContentCorpusFacts(
                "current", "/repo/.miller/content.db", SchemaVersion: 1, WorkspaceRevision: 42,
                SourceCount: 12, ChunkCount: 48, IndexedSourceBytes: 1200, StoredRawBytes: 1600,
                NonUtf8Skipped: 2),
        };

        using var doc = JsonDocument.Parse(WorkspaceRender.Status(facts, TelemetrySummary.Empty, json: true));
        JsonElement corpus = doc.RootElement.GetProperty("index").GetProperty("content_corpus");

        Assert.Equal("current", corpus.GetProperty("state").GetString());
        Assert.Equal(42, corpus.GetProperty("workspace_revision").GetInt64());
        Assert.Equal(12, corpus.GetProperty("source_count").GetInt32());
        Assert.Equal(48, corpus.GetProperty("chunk_count").GetInt32());
        Assert.Equal(2, corpus.GetProperty("non_utf8_skipped").GetInt32());
    }

    // ---- health ----

    [Fact]
    public void Health_Compact_LeadsWithVerdictAndShortWarnings()
    {
        var health = new WorkspaceHealthFacts(
            StatusFacts: Facts() with
            {
                SearchSidecar = new SearchSidecarFacts(
                    "current", "/repo/.miller/search.db", Revision: 42, ExpectedRevision: 42,
                    DocumentCount: 565, Error: null),
                ContentCorpus = new ContentCorpusFacts(
                    "current", "/repo/.miller/content.db", SchemaVersion: 1, WorkspaceRevision: 42,
                    SourceCount: 12, ChunkCount: 48, IndexedSourceBytes: 1200, StoredRawBytes: 1600),
            },
            Telemetry: TelemetrySummary.Empty,
            TelemetryHealth: new TelemetryHealthFacts(OkCount: 5, EmptyCount: 1, ErrorCount: 2),
            Extraction: ExtractionHealth(),
            Warnings: new[]
            {
                new HealthWarning("parse_diagnostics", "usable_with_warnings", "2 parse diagnostics reported"),
            },
            RecommendedActions: new[] { "inspect parse diagnostics before relying on unsupported language facts" },
            State: HealthState.UsableWithWarnings,
            Summary: "index readable with warnings");

        string text = WorkspaceRender.Health(health, json: false);

        Assert.StartsWith("# workspace health  usable_with_warnings", text, StringComparison.Ordinal);
        Assert.Contains("workspace: ws-123  /repo", text);
        Assert.Contains("index: fresh rev 42", text);
        Assert.Contains("search_db: current rev 42", text);
        Assert.Contains("content_db: current rev 42", text);
        Assert.Contains("quality: 2 parse diagnostics  1 open capability gaps", text);
        Assert.Contains("telemetry: 8 calls  errors=2  empty=1", text);
        Assert.Contains("recommended: inspect parse diagnostics", text);
    }

    [Fact]
    public void Health_Compact_IsBoundedAndReportsEveryOmittedGroupAndRow()
    {
        var health = new WorkspaceHealthFacts(
            StatusFacts: Facts(),
            Telemetry: TelemetrySummary.Empty,
            TelemetryHealth: new TelemetryHealthFacts(OkCount: 5, EmptyCount: 1, ErrorCount: 2),
            Extraction: ExtractionHealth(),
            Warnings:
            [
                new HealthWarning("first", "degraded", "first warning\n" + new string('x', 300)),
                new HealthWarning("second", "degraded", "second warning"),
                new HealthWarning("third", "degraded", "third warning"),
            ],
            RecommendedActions: ["first action\n" + new string('y', 300), "second action", "third action"],
            State: HealthState.Degraded,
            Summary: "workspace readable but degraded");

        string compact = WorkspaceRender.Health(health, WorkspaceHealthFormat.Compact);

        Assert.Contains("omitted: groups=6 unavailable=0 rows=6 warnings=2 actions=2", compact);
        Assert.Contains("first warning", compact);
        Assert.Contains("first action", compact);
        Assert.DoesNotContain("second warning", compact);
        Assert.DoesNotContain("second action", compact);
        Assert.DoesNotContain(new string('x', 241), compact);
        Assert.DoesNotContain(new string('y', 241), compact);
        Assert.True(compact.Split('\n').Length <= 14);
    }

    [Fact]
    public void Health_Compact_MarksUnavailableExtractionSectionsInsteadOfReportingHealthyZeros()
    {
        const string error = "extract table unavailable";
        var extraction = new WorkspaceExtractionHealthFacts(
            HealthFactSection<ParseDiagnosticGroup>.Unavailable(error),
            HealthFactSection<CapabilityGapGroup>.Unavailable(error),
            HealthFactSection<LanguageCapabilitySummary>.Unavailable(error),
            HealthFactSection<StructuralFactGroup>.Unavailable(error),
            HealthFactSection<ComplexityMetricGroup>.Unavailable(error),
            HealthFactSection<FileStatusGroup>.Unavailable(error));
        var health = new WorkspaceHealthFacts(
            Facts(),
            TelemetrySummary.Empty,
            new TelemetryHealthFacts(0, 0, 0),
            extraction,
            [],
            [],
            HealthState.Unavailable,
            error);

        string compact = WorkspaceRender.Health(health, WorkspaceHealthFormat.Compact);

        Assert.Contains("0 parse diagnostics (unavailable)", compact);
        Assert.Contains("0 open capability gaps (unavailable)", compact);
        Assert.Contains("0 structural facts (unavailable)", compact);
        Assert.Contains("0 complexity metrics (unavailable)", compact);
        Assert.Contains("omitted: groups=0 unavailable=6", compact);
    }

    [Fact]
    public void Health_MarkdownAndJsonKeepCompleteHealthDetails()
    {
        var health = new WorkspaceHealthFacts(
            StatusFacts: Facts(),
            Telemetry: TelemetrySummary.Empty,
            TelemetryHealth: new TelemetryHealthFacts(OkCount: 5, EmptyCount: 1, ErrorCount: 2),
            Extraction: ExtractionHealth(),
            Warnings:
            [
                new HealthWarning("first", "degraded", "first warning"),
                new HealthWarning("second", "degraded", "second warning"),
            ],
            RecommendedActions: ["first action", "second action"],
            State: HealthState.Degraded,
            Summary: "workspace readable but degraded");

        string markdown = WorkspaceRender.Health(health, WorkspaceHealthFormat.Markdown);
        using var json = JsonDocument.Parse(WorkspaceRender.Health(health, WorkspaceHealthFormat.Json));

        Assert.Contains("second warning", markdown);
        Assert.Contains("second action", markdown);
        Assert.Contains("\"parse_diagnostics\"", markdown);
        Assert.Equal(2, json.RootElement.GetProperty("warnings").GetArrayLength());
        Assert.Equal(2, json.RootElement.GetProperty("recommended_actions").GetArrayLength());
        Assert.Equal(
            1,
            json.RootElement.GetProperty("extraction_quality")
                .GetProperty("parse_diagnostics")
                .GetProperty("rows")
                .GetArrayLength());
    }

    [Fact]
    public void Health_Json_RendersStableSections()
    {
        var health = new WorkspaceHealthFacts(
            StatusFacts: Facts(),
            Telemetry: TelemetrySummary.Empty,
            TelemetryHealth: new TelemetryHealthFacts(OkCount: 5, EmptyCount: 1, ErrorCount: 2),
            Extraction: ExtractionHealth(),
            Warnings: new[]
            {
                new HealthWarning("parse_diagnostics", "usable_with_warnings", "2 parse diagnostics reported"),
            },
            RecommendedActions: new[] { "inspect parse diagnostics" },
            State: HealthState.UsableWithWarnings,
            Summary: "index readable with warnings");

        using var doc = JsonDocument.Parse(WorkspaceRender.Health(health, json: true));
        JsonElement root = doc.RootElement;

        Assert.Equal("usable_with_warnings", root.GetProperty("verdict").GetProperty("state").GetString());
        Assert.Equal("ws-123", root.GetProperty("workspace").GetProperty("workspace_id").GetString());
        Assert.Equal(565, root.GetProperty("index").GetProperty("document_count").GetInt64());
        Assert.Equal(2, root.GetProperty("telemetry").GetProperty("outcomes").GetProperty("error_count").GetInt64());
        Assert.Equal("csharp", root.GetProperty("extraction_quality")
            .GetProperty("parse_diagnostics").GetProperty("rows")[0].GetProperty("language").GetString());
        Assert.Equal("relationships", root.GetProperty("extraction_quality")
            .GetProperty("capability_gaps").GetProperty("rows")[0].GetProperty("capability").GetString());
        JsonElement capabilityRow = root.GetProperty("extraction_quality")
            .GetProperty("language_capabilities").GetProperty("rows")[0];
        JsonElement docComments = capabilityRow.GetProperty("kind_coverage").GetProperty("doc_comments");
        Assert.Equal("method", docComments.GetProperty("supported")[0].GetString());
        Assert.Equal("property", docComments.GetProperty("open_gaps")[0].GetString());
        Assert.Equal(0, docComments.GetProperty("not_applicable").GetArrayLength());
        JsonElement testDetection = capabilityRow.GetProperty("kind_coverage").GetProperty("test_detection");
        Assert.Equal("test_case", testDetection.GetProperty("supported")[0].GetString());
        Assert.Equal(2, testDetection.GetProperty("open_gaps").GetArrayLength());
        JsonElement structuredGap = testDetection.GetProperty("open_gaps")[0];
        Assert.Equal(JsonValueKind.Object, structuredGap.ValueKind);
        Assert.Equal("test_lifecycle", structuredGap.GetProperty("kind").GetString());
        Assert.Equal("fixture lifecycle gap", structuredGap.GetProperty("reason").GetString());
        Assert.Equal("add lifecycle golden evidence", structuredGap.GetProperty("required_closure").GetString());
        Assert.Equal("docs/plans/test-detection-closure.md",
            structuredGap.GetProperty("planned_closure_task").GetString());
        // A kind-less entry still reaches consumers verbatim rather than silently narrowing the gap set.
        Assert.Equal("missing kind", testDetection.GetProperty("open_gaps")[1].GetProperty("reason").GetString());
        Assert.Equal("test_container", testDetection.GetProperty("not_applicable")[0].GetString());
        Assert.True(root.GetProperty("extraction_quality").TryGetProperty("structural_facts", out JsonElement structural));
        Assert.True(structural.GetProperty("available").GetBoolean());
        Assert.True(root.GetProperty("extraction_quality").TryGetProperty("complexity_metrics", out JsonElement complexity));
        Assert.True(complexity.GetProperty("available").GetBoolean());
        Assert.Equal("parse_diagnostics", root.GetProperty("warnings")[0].GetProperty("code").GetString());
        Assert.Equal("inspect parse diagnostics", root.GetProperty("recommended_actions")[0].GetString());
    }

    [Fact]
    public void Health_Json_ExposesBrokerCpuDegradationTruth()
    {
        SemanticBrokerFacts broker = BrokerFacts() with
        {
            Role = "non_owner",
            Backend = "cpu",
            AcceleratorLeaseHeld = false,
            OwnershipDegraded = false,
            OwnershipDegradedReason = null,
            BackendDegradedReason = "accelerator lease held by another model",
            OwnerProcessId = null,
        };
        var health = new WorkspaceHealthFacts(
            StatusFacts: Facts() with { SemanticBroker = broker },
            Telemetry: TelemetrySummary.Empty,
            TelemetryHealth: new TelemetryHealthFacts(0, 0, 0),
            Extraction: ExtractionHealth(),
            Warnings: [],
            RecommendedActions: [],
            State: HealthState.Ready,
            Summary: "ready");

        JsonElement rendered = Json(WorkspaceRender.Health(health, json: true))
            .GetProperty("semantic_broker");

        Assert.Equal("non_owner", rendered.GetProperty("role").GetString());
        Assert.Equal("cpu", rendered.GetProperty("backend").GetString());
        Assert.False(rendered.GetProperty("accelerator_lease_held").GetBoolean());
        Assert.False(rendered.GetProperty("ownership_degraded").GetBoolean());
        Assert.Equal(
            "accelerator lease held by another model",
            rendered.GetProperty("backend_degraded_reason").GetString());
        Assert.Equal(JsonValueKind.Null, rendered.GetProperty("ownership_degraded_reason").ValueKind);
        Assert.Equal(JsonValueKind.Null, rendered.GetProperty("owner_pid").ValueKind);
    }

    [Fact]
    public void Health_JsonSummary_IsBoundedAndKeepsActionableCounts()
    {
        var health = new WorkspaceHealthFacts(
            StatusFacts: Facts(),
            Telemetry: Telemetry,
            TelemetryHealth: new TelemetryHealthFacts(OkCount: 5, EmptyCount: 1, ErrorCount: 2),
            Extraction: ExtractionHealth(),
            Warnings:
            [
                new HealthWarning("first", "degraded", "first warning"),
                new HealthWarning("second", "degraded", "second warning"),
            ],
            RecommendedActions: ["first action", "second action"],
            State: HealthState.Degraded,
            Summary: "workspace readable but degraded");

        string output = WorkspaceRender.Health(health, WorkspaceHealthFormat.JsonSummary);

        using var json = JsonDocument.Parse(output);
        JsonElement root = json.RootElement;
        Assert.Equal("summary", root.GetProperty("detail").GetString());
        Assert.Equal(565, root.GetProperty("index").GetProperty("document_count").GetInt64());
        Assert.Equal(
            2,
            root.GetProperty("extraction_quality").GetProperty("parse_diagnostic_count").GetInt64());
        Assert.Equal(
            1,
            root.GetProperty("extraction_quality").GetProperty("open_capability_gap_count").GetInt64());
        Assert.Equal("first", root.GetProperty("warnings")[0].GetProperty("code").GetString());
        Assert.Equal("first action", root.GetProperty("recommended_actions")[0].GetString());
        Assert.Equal(2, root.GetProperty("warnings_total_count").GetInt32());
        Assert.Equal(0, root.GetProperty("warnings_omitted_count").GetInt32());
        Assert.Equal(2, root.GetProperty("recommended_actions_total_count").GetInt32());
        Assert.Equal(0, root.GetProperty("recommended_actions_omitted_count").GetInt32());
        Assert.Contains(
            "miller workspace health --json",
            root.GetProperty("next_action").GetString(),
            StringComparison.Ordinal);
        Assert.True(Encoding.UTF8.GetByteCount(output) <= ToolOutputBudget.WorkspaceHealthMcpMaxBytes);
        Assert.False(root.GetProperty("extraction_quality").TryGetProperty("language_capabilities", out _));
    }

    [Fact]
    public void Health_JsonSummary_ReportsExactWarningAndActionOmissions()
    {
        var health = new WorkspaceHealthFacts(
            StatusFacts: Facts(),
            Telemetry: Telemetry,
            TelemetryHealth: new TelemetryHealthFacts(OkCount: 5, EmptyCount: 1, ErrorCount: 2),
            Extraction: ExtractionHealth(),
            Warnings:
            [
                new HealthWarning("one", "degraded", "one"),
                new HealthWarning("two", "degraded", "two"),
                new HealthWarning("three", "degraded", "three"),
                new HealthWarning("four", "degraded", "four"),
            ],
            RecommendedActions: ["one", "two", "three", "four", "five"],
            State: HealthState.Degraded,
            Summary: "workspace readable but degraded");

        using var json = JsonDocument.Parse(
            WorkspaceRender.Health(health, WorkspaceHealthFormat.JsonSummary));

        Assert.Equal(4, json.RootElement.GetProperty("warnings_total_count").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("warnings_omitted_count").GetInt32());
        Assert.Equal(5, json.RootElement.GetProperty("recommended_actions_total_count").GetInt32());
        Assert.Equal(2, json.RootElement.GetProperty("recommended_actions_omitted_count").GetInt32());
    }

    [Fact]
    public void Health_Compact_RendersHistorySidecarPresenceAndCorruptRecovery()
    {
        var present = HealthWithHistory(new MetricHistoryStatus(
            Present: true, SchemaVersion: 1, SnapshotCount: 7, SizeBytes: 4096, CorruptRecovered: false));
        string presentText = WorkspaceRender.Health(present, json: false);
        Assert.Contains("history_db: present  7 snapshots", presentText);
        Assert.Contains("schema v1", presentText);

        var recovered = HealthWithHistory(new MetricHistoryStatus(
            Present: false, SchemaVersion: 0, SnapshotCount: 0, SizeBytes: 0, CorruptRecovered: true));
        string recoveredText = WorkspaceRender.Health(recovered, json: false);
        Assert.Contains("history_db: absent  corrupt-recovered", recoveredText);
    }

    [Fact]
    public void Health_Json_RendersHistorySidecarBlock()
    {
        var health = HealthWithHistory(new MetricHistoryStatus(
            Present: true, SchemaVersion: 1, SnapshotCount: 7, SizeBytes: 4096, CorruptRecovered: true));

        using var doc = JsonDocument.Parse(WorkspaceRender.Health(health, json: true));
        JsonElement history = doc.RootElement.GetProperty("index").GetProperty("history_db");

        Assert.True(history.GetProperty("present").GetBoolean());
        Assert.Equal(1, history.GetProperty("schema_version").GetInt32());
        Assert.Equal(7, history.GetProperty("snapshot_count").GetInt64());
        Assert.Equal(4096, history.GetProperty("size_bytes").GetInt64());
        Assert.True(history.GetProperty("corrupt_recovered").GetBoolean());
    }

    [Fact]
    public void Health_Compact_RendersHistorySidecarUnreadableDistinctly()
    {
        var unreadable = HealthWithHistory(new MetricHistoryStatus(
            Present: true, SchemaVersion: 0, SnapshotCount: 0, SizeBytes: 512, CorruptRecovered: false, Unreadable: true));
        string text = WorkspaceRender.Health(unreadable, json: false);

        // A broken sidecar must read "unreadable", never a healthy-looking "present  0 snapshots".
        Assert.Contains("history_db: unreadable", text);
        Assert.DoesNotContain("present  0 snapshots", text);
    }

    [Fact]
    public void Health_Json_RendersHistorySidecarUnreadableFlag()
    {
        var unreadable = HealthWithHistory(new MetricHistoryStatus(
            Present: true, SchemaVersion: 0, SnapshotCount: 0, SizeBytes: 512, CorruptRecovered: false, Unreadable: true));

        using var doc = JsonDocument.Parse(WorkspaceRender.Health(unreadable, json: true));
        JsonElement history = doc.RootElement.GetProperty("index").GetProperty("history_db");

        Assert.True(history.GetProperty("present").GetBoolean());
        Assert.True(history.GetProperty("unreadable").GetBoolean());
    }

    private static WorkspaceHealthFacts HealthWithHistory(MetricHistoryStatus history) =>
        new(
            StatusFacts: Facts(),
            Telemetry: TelemetrySummary.Empty,
            TelemetryHealth: new TelemetryHealthFacts(OkCount: 5, EmptyCount: 1, ErrorCount: 0),
            Extraction: ExtractionHealth(),
            Warnings: Array.Empty<HealthWarning>(),
            RecommendedActions: Array.Empty<string>(),
            State: HealthState.Ready,
            Summary: "index and sidecars are ready",
            Leader: null,
            History: history);

    // ---- list ----

    [Fact]
    public void List_Compact_ShowsCurrentWorkspace_HonestlyLabelledSingle()
    {
        string text = WorkspaceRender.List(Facts(), json: false);

        // Single-workspace-per-process: the list is the CURRENT workspace, labelled so it is not mistaken for a
        // multi-entry registry (which is eros/commercial-tier, out of Miller's scope).
        Assert.Contains("/repo", text);
        Assert.Contains("ws-123", text);
        Assert.Contains("current", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void List_Json_IsASingleEntryArrayMarkedCurrent()
    {
        using var doc = JsonDocument.Parse(WorkspaceRender.List(Facts(), json: true));
        var root = doc.RootElement;
        var workspaces = root.GetProperty("workspaces");
        Assert.Equal(1, workspaces.GetArrayLength());
        var only = workspaces[0];
        Assert.Equal("/repo", only.GetProperty("root").GetString());
        Assert.True(only.GetProperty("current").GetBoolean());
    }

    [Fact]
    public void List_Compact_RendersRegistryRowsAndMarksOnlyTheCurrentWorkspace()
    {
        var rows = new[]
        {
            new WorkspaceListEntry(
                WorkspaceId: "ws-current",
                DisplayId: "current-111111111111",
                Root: "/repo/current",
                DbPath: "/repo/current/.miller/symbols.db",
                State: "current",
                LastRevision: 12,
                Current: true,
                LastError: null),
            new WorkspaceListEntry(
                WorkspaceId: "ws-other",
                DisplayId: "other-222222222222",
                Root: "/repo/other",
                DbPath: "/repo/other/.miller/symbols.db",
                State: "ready",
                LastRevision: 7,
                Current: false,
                LastError: null),
        };

        string text = WorkspaceRender.List(rows, json: false);

        Assert.Contains("# workspaces (2)", text);
        Assert.Contains("current-111111111111", text);
        Assert.Contains("other-222222222222", text);
        Assert.DoesNotContain("workspace_id:", text);
        Assert.DoesNotContain("/repo/current/.miller/symbols.db", text);
        Assert.DoesNotContain("/repo/other/.miller/symbols.db", text);
        Assert.Contains("[current]", text);
        Assert.Contains("state: ready", text);
    }

    [Fact]
    public void List_Compact_MissingRootsUsesSurfaceNeutralPruneGuidance()
    {
        var rows = new[]
        {
            new WorkspaceListEntry(
                WorkspaceId: "ws-missing",
                DisplayId: "missing-111111111111",
                Root: "/repo/missing",
                DbPath: "/repo/missing/.miller/symbols.db",
                State: "missing",
                LastRevision: null,
                Current: false,
                LastError: "root missing",
                RootMissing: true),
        };

        string text = WorkspaceRender.List(rows, json: false);

        Assert.Contains("preview registry cleanup with a prune dry run", text);
        Assert.DoesNotContain("workspace(operation=", text, StringComparison.Ordinal);
    }

    [Fact]
    public void List_Json_RendersStableRegistryRowShape()
    {
        var rows = new[]
        {
            new WorkspaceListEntry(
                WorkspaceId: "ws-current",
                DisplayId: "current-111111111111",
                Root: "/repo/current",
                DbPath: "/repo/current/.miller/symbols.db",
                State: "current",
                LastRevision: 12,
                Current: true,
                LastError: null),
            new WorkspaceListEntry(
                WorkspaceId: "ws-missing",
                DisplayId: "missing-222222222222",
                Root: "/repo/missing",
                DbPath: "/repo/missing/.miller/symbols.db",
                State: "missing",
                LastRevision: null,
                Current: false,
                LastError: "root missing"),
        };

        using var doc = JsonDocument.Parse(WorkspaceRender.List(rows, json: true));
        var workspaces = doc.RootElement.GetProperty("workspaces");

        Assert.Equal(2, workspaces.GetArrayLength());
        Assert.Equal("ws-current", workspaces[0].GetProperty("workspace_id").GetString());
        Assert.Equal("current-111111111111", workspaces[0].GetProperty("display_id").GetString());
        Assert.Equal("/repo/current", workspaces[0].GetProperty("root").GetString());
        Assert.Equal("/repo/current/.miller/symbols.db", workspaces[0].GetProperty("index_db_path").GetString());
        Assert.Equal("current", workspaces[0].GetProperty("state").GetString());
        Assert.Equal(12, workspaces[0].GetProperty("last_revision").GetInt64());
        Assert.True(workspaces[0].GetProperty("current").GetBoolean());
        Assert.Equal(JsonValueKind.Null, workspaces[0].GetProperty("last_error").ValueKind);
        Assert.Equal(JsonValueKind.Null, workspaces[1].GetProperty("last_revision").ValueKind);
        Assert.Equal("root missing", workspaces[1].GetProperty("last_error").GetString());
    }

    // ---- refresh / full action ----

    [Fact]
    public void Action_Compact_LeaderScannedAndSwapped_ReportsBoth()
    {
        var result = new WorkspaceActionResult(
            Operation: "full", Scanned: true, Swapped: true, Revision: 43, Note: null);

        string text = WorkspaceRender.Action(result, json: false);
        Assert.Contains("full", text);
        Assert.Contains("43", text);
        Assert.Contains("scanned", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("swapped", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Action_Compact_NonLeaderFull_CarriesTheHonestNote()
    {
        var result = new WorkspaceActionResult(
            Operation: "full", Scanned: false, Swapped: false, Revision: 42,
            Note: "Not the indexer leader; cannot force a global rescan here. The leader's watcher keeps the index fresh.");

        string text = WorkspaceRender.Action(result, json: false);
        Assert.Contains("leader", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cannot force", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Action_ADowngradedRebuild_SaysSoInBothRenders_SoScannedIsNeverReadAsRebuilt()
    {
        var result = new WorkspaceActionResult(
            Operation: "full",
            Scanned: true,
            Swapped: false,
            Revision: 43,
            Note: "the full (force) scan was downgraded to a delta reconcile; the rebuild is still owed.",
            Downgraded: true);

        string text = WorkspaceRender.Action(result, json: false);
        using var doc = JsonDocument.Parse(WorkspaceRender.Action(result, json: true));

        Assert.Contains("downgraded: yes", text, StringComparison.Ordinal);
        Assert.Contains("still owed", text, StringComparison.Ordinal);
        Assert.True(doc.RootElement.GetProperty("downgraded").GetBoolean());
    }

    [Fact]
    public void Action_WithoutADowngrade_OmitsTheFlagEntirely_SoTheDefaultContractIsByteIdentical()
    {
        var result = new WorkspaceActionResult(
            Operation: "full", Scanned: true, Swapped: false, Revision: 43, Note: null);

        using var doc = JsonDocument.Parse(WorkspaceRender.Action(result, json: true));

        Assert.DoesNotContain("downgraded", WorkspaceRender.Action(result, json: false), StringComparison.Ordinal);
        Assert.False(doc.RootElement.TryGetProperty("downgraded", out _));
    }

    [Fact]
    public void Action_Json_RoundTripsTheFields()
    {
        var result = new WorkspaceActionResult(
            Operation: "refresh", Scanned: true, Swapped: false, Revision: 42, Note: null);

        using var doc = JsonDocument.Parse(WorkspaceRender.Action(result, json: true));
        var root = doc.RootElement;
        Assert.Equal("refresh", root.GetProperty("operation").GetString());
        Assert.True(root.GetProperty("scanned").GetBoolean());
        Assert.False(root.GetProperty("swapped").GetBoolean());
        Assert.Equal(42, root.GetProperty("revision").GetInt64());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("note").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("artifact_id").ValueKind);
    }

    [Fact]
    public void Action_Json_IncludesArtifactIdWhenPresent()
    {
        var result = new WorkspaceActionResult(
            Operation: "refresh", Scanned: false, Swapped: true, Revision: 99, Note: null,
            ArtifactId: "art-eros-1");

        using var doc = JsonDocument.Parse(WorkspaceRender.Action(result, json: true));
        Assert.Equal("art-eros-1", doc.RootElement.GetProperty("artifact_id").GetString());
    }

    [Fact]
    public void Action_Json_IneligibleExtractorRefusal_IsValidJsonWithReasonNote()
    {
        // The CLI gate's refusal (refresh --full --json with an outdated extractor) must still emit valid JSON.
        var result = new WorkspaceActionResult(
            Operation: "full", Scanned: false, Swapped: false, Revision: 1,
            Note: "extractor 2.1.3 is older than the index artifact 2.3.0; this instance serves reads only. " +
                  "Run scripts/restore-julie-extract.sh (or .ps1) or upgrade miller.",
            WorkspaceId: "ws-123", Root: "/repo", Status: "ineligible_extractor", IndexFresh: false);

        using var doc = JsonDocument.Parse(WorkspaceRender.Action(result, json: true));
        var root = doc.RootElement;
        Assert.Equal("ineligible_extractor", root.GetProperty("status").GetString());
        Assert.False(root.GetProperty("scanned").GetBoolean());
        Assert.Contains("older", root.GetProperty("note").GetString());
    }

    [Fact]
    public void Action_Json_CanCarryPostRefreshArtifactFacts()
    {
        var result = new WorkspaceActionResult(
            Operation: "refresh",
            Scanned: true,
            Swapped: false,
            Revision: 42,
            Note: null,
            WorkspaceId: "ws-123",
            Root: "/repo",
            Status: "refreshed",
            IndexFresh: true,
            SearchSidecar: new SearchSidecarFacts(
                "current", "/repo/.miller/search.db", Revision: 42, ExpectedRevision: 42, DocumentCount: 10, Error: null),
            ContentCorpus: new ContentCorpusFacts(
                "current", "/repo/.miller/content.db", SchemaVersion: 1, WorkspaceRevision: 42,
                SourceCount: 2, ChunkCount: 8, IndexedSourceBytes: 1024, StoredRawBytes: 2048));

        using var doc = JsonDocument.Parse(WorkspaceRender.Action(result, json: true));
        JsonElement root = doc.RootElement;

        Assert.True(root.GetProperty("index_fresh").GetBoolean());
        Assert.Equal("current", root.GetProperty("search_sidecar").GetProperty("state").GetString());
        Assert.Equal("/repo/.miller/search.db", root.GetProperty("search_sidecar").GetProperty("path").GetString());
        Assert.Equal("current", root.GetProperty("content_corpus").GetProperty("state").GetString());
        Assert.Equal(8, root.GetProperty("content_corpus").GetProperty("chunk_count").GetInt32());
    }

    [Fact]
    public void Action_Json_CarriesBoundedSidecarConvergenceFacts()
    {
        var sidecars = new SidecarConvergenceFacts(
            TargetSequence: 42,
            Content: new SidecarConvergenceFact("repaired", true, false, false, null),
            Search: new SidecarConvergenceFact("failed", false, true, false, new string('x', 500)),
            Vector: new SidecarConvergenceFact("leader_required", false, true, true, "resident leader required"));
        var result = new WorkspaceActionResult(
            Operation: "refresh", Scanned: false, Swapped: false, Revision: 42, Note: null,
            Sidecars: sidecars);

        using var doc = JsonDocument.Parse(WorkspaceRender.Action(result, json: true));
        JsonElement root = doc.RootElement;
        JsonElement json = root.GetProperty("sidecars");

        Assert.Equal(42, json.GetProperty("target_sequence").GetInt64());
        Assert.True(json.GetProperty("did_work").GetBoolean());
        Assert.True(json.GetProperty("pending").GetBoolean());
        Assert.True(json.GetProperty("leader_required").GetBoolean());
        Assert.Equal("repaired", json.GetProperty("content").GetProperty("status").GetString());
        Assert.Equal("failed", json.GetProperty("search").GetProperty("status").GetString());
        Assert.Equal(240, json.GetProperty("search").GetProperty("reason").GetString()!.Length);
        Assert.Equal("leader_required", json.GetProperty("vector").GetProperty("status").GetString());
    }

    [Fact]
    public void Action_Compact_PutsBoundedSidecarFactsOnOneLine()
    {
        var result = new WorkspaceActionResult(
            Operation: "refresh", Scanned: false, Swapped: false, Revision: 42, Note: null,
            Sidecars: new SidecarConvergenceFacts(
                42,
                new SidecarConvergenceFact("current", false, false, false, null),
                new SidecarConvergenceFact("failed", false, true, false, "retry in 5s"),
                new SidecarConvergenceFact("queued", false, true, false, null)));

        string compact = WorkspaceRender.Action(result, json: false);
        string line = Assert.Single(compact.Split('\n', StringSplitOptions.RemoveEmptyEntries),
            item => item.StartsWith("sidecars: ", StringComparison.Ordinal));
        Assert.Contains("target=42", line, StringComparison.Ordinal);
        Assert.Contains("content=current", line, StringComparison.Ordinal);
        Assert.Contains("search=failed", line, StringComparison.Ordinal);
        Assert.Contains("vector=queued", line, StringComparison.Ordinal);
        Assert.True(line.Length <= 240, $"sidecar line was {line.Length} chars");
    }

    [Fact]
    public void Action_WithoutSidecarConvergenceFacts_OmitsOptionalField()
    {
        var result = new WorkspaceActionResult(
            Operation: "refresh", Scanned: false, Swapped: false, Revision: 42, Note: null);

        string compact = WorkspaceRender.Action(result, json: false);
        using var doc = JsonDocument.Parse(WorkspaceRender.Action(result, json: true));

        Assert.DoesNotContain("sidecars", compact, StringComparison.Ordinal);
        Assert.False(doc.RootElement.TryGetProperty("sidecars", out _));
    }

    // Durations are cli-eros-v1 sweep telemetry: scan_duration_ms is the julie-extract attempt (set even for a
    // failed/killed scan), duration_ms the whole refresh; both are null on paths that did not measure them.
    [Fact]
    public void Action_Json_CarriesDurations_AndNullsThemWhenUnmeasured()
    {
        var measured = new WorkspaceActionResult(
            Operation: "full", Scanned: true, Swapped: false, Revision: 42, Note: null,
            WorkspaceId: "ws-123", Root: "/repo", Status: "refreshed", IndexFresh: true,
            ScanDurationMs: 91_000, DurationMs: 95_500);
        using (var doc = JsonDocument.Parse(WorkspaceRender.Action(measured, json: true)))
        {
            Assert.Equal(91_000, doc.RootElement.GetProperty("scan_duration_ms").GetInt64());
            Assert.Equal(95_500, doc.RootElement.GetProperty("duration_ms").GetInt64());
        }

        var unmeasured = new WorkspaceActionResult(
            Operation: "refresh", Scanned: false, Swapped: false, Revision: 42, Note: null,
            WorkspaceId: "ws-123", Root: "/repo", Status: "lock_busy", IndexFresh: false);
        using (var doc = JsonDocument.Parse(WorkspaceRender.Action(unmeasured, json: true)))
        {
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("scan_duration_ms").ValueKind);
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("duration_ms").ValueKind);
        }
    }

    [Fact]
    public void Action_Compact_ShowsDurationsOnlyWhenMeasured()
    {
        var measured = new WorkspaceActionResult(
            Operation: "full", Scanned: true, Swapped: false, Revision: 42, Note: null,
            Status: "refreshed", ScanDurationMs: 91_000, DurationMs: 95_500);
        string text = WorkspaceRender.Action(measured, json: false);
        Assert.Contains("scan_duration_ms: 91000", text);
        Assert.Contains("duration_ms: 95500", text);

        var unmeasured = new WorkspaceActionResult(
            Operation: "refresh", Scanned: false, Swapped: false, Revision: 42, Note: null);
        Assert.DoesNotContain("duration_ms", WorkspaceRender.Action(unmeasured, json: false));
    }

    [Fact]
    public void Action_Open_ReportsQueuedWorkspaceState()
    {
        var result = new WorkspaceActionResult(
            Operation: "open",
            Scanned: false,
            Swapped: false,
            Revision: 0,
            Note: "workspace registered and queued for background indexing",
            WorkspaceId: "ws-open",
            Root: "/other/repo",
            Status: "refreshing");

        string compact = WorkspaceRender.Action(result, json: false);
        Assert.Contains("# workspace open", compact);
        Assert.Contains("status: refreshing", compact);
        Assert.Contains("scanned: no", compact);
        Assert.Contains("swapped: no", compact);

        using var document = JsonDocument.Parse(WorkspaceRender.Action(result, json: true));
        Assert.Equal("open", document.RootElement.GetProperty("operation").GetString());
        Assert.False(document.RootElement.GetProperty("scanned").GetBoolean());
        Assert.Equal("refreshing", document.RootElement.GetProperty("status").GetString());
    }

    // ---- open (prime) ----

    [Fact]
    public void Open_Compact_ReportsPrimedPath_AndThatItIsNotALiveSwitch()
    {
        var result = new WorkspaceOpenResult(
            Path: "/other/repo", DbPath: "/other/repo/.miller/symbols.db",
            SymbolsExtracted: 1234, Revision: 1);

        string text = WorkspaceRender.Open(result, json: false);
        Assert.Contains("/other/repo", text);
        Assert.Contains("1234", text);
        // Honest: priming a path is NOT a live switch (the index/watcher are bound to CWD at bootstrap).
        Assert.Contains("not a live switch", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Open_Json_RoundTripsTheFields()
    {
        var result = new WorkspaceOpenResult(
            Path: "/other/repo", DbPath: "/other/repo/.miller/symbols.db",
            SymbolsExtracted: 1234, Revision: 7);

        using var doc = JsonDocument.Parse(WorkspaceRender.Open(result, json: true));
        var root = doc.RootElement;
        Assert.Equal("/other/repo", root.GetProperty("path").GetString());
        Assert.Equal(1234, root.GetProperty("symbols_extracted").GetInt64());
        Assert.Equal(7, root.GetProperty("revision").GetInt64());
    }

    // ---- remove ----

    [Fact]
    public void Remove_Compact_Removed_ReportsDeletion()
    {
        var result = WorkspaceRemoveResult.Removed("/other/repo/.miller");
        string text = WorkspaceRender.Remove(result, json: false);
        Assert.Contains("/other/repo/.miller", text);
        Assert.Contains("removed", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Remove_Compact_RemovedRegistryOnly_ReportsRegistrationCleanup()
    {
        var result = WorkspaceRemoveResult.Removed(
            "/other/repo/.miller",
            workspaceId: "other-ws",
            root: "/other/repo",
            indexDirDeleted: false);

        string text = WorkspaceRender.Remove(result, json: false);

        Assert.Contains("removed", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("registry", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no index dir", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nothing to remove", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Remove_Compact_RefusedLive_IsAClearRefusal()
    {
        var result = WorkspaceRemoveResult.RefusedLive("/repo/.miller");
        string text = WorkspaceRender.Remove(result, json: false);
        Assert.Contains("refus", text, StringComparison.OrdinalIgnoreCase); // refuse/refused
        Assert.Contains("in use", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Remove_Compact_NotFound_IsNotAnError()
    {
        var result = WorkspaceRemoveResult.NotFound("/gone/.miller");
        string text = WorkspaceRender.Remove(result, json: false);
        Assert.Contains("/gone/.miller", text);
        Assert.Contains("not found", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Remove_Json_CarriesTheKind()
    {
        using var doc = JsonDocument.Parse(WorkspaceRender.Remove(WorkspaceRemoveResult.RefusedLive("/repo/.miller"), json: true));
        Assert.Equal("refused_live", doc.RootElement.GetProperty("result").GetString());
    }

    [Fact]
    public void Remove_Json_ReportsWhetherIndexDirWasDeleted()
    {
        using var doc = JsonDocument.Parse(WorkspaceRender.Remove(
            WorkspaceRemoveResult.Removed(
                "/other/repo/.miller",
                workspaceId: "other-ws",
                root: "/other/repo",
                indexDirDeleted: false),
            json: true));

        Assert.Equal("removed", doc.RootElement.GetProperty("result").GetString());
        Assert.False(doc.RootElement.GetProperty("index_dir_deleted").GetBoolean());
    }

    [Fact]
    public void Remove_Json_ReportsTheStoreSidecarReclaim()
    {
        using var doc = JsonDocument.Parse(WorkspaceRender.Remove(
            WorkspaceRemoveResult.Removed(
                "/other/repo/.miller",
                workspaceId: "other-ws",
                root: "/other/repo",
                indexDirDeleted: true,
                sidecarReclaim: new StoreSidecarReclaimResult(6, 367_001_600, 0, null)),
            json: true));

        JsonElement reclaim = doc.RootElement.GetProperty("store_sidecar_reclaim");
        Assert.Equal(6, reclaim.GetProperty("files_deleted").GetInt32());
        Assert.Equal(367_001_600, reclaim.GetProperty("bytes_reclaimed").GetInt64());
        Assert.Equal(0, reclaim.GetProperty("files_retained").GetInt32());
        Assert.Equal(JsonValueKind.Null, reclaim.GetProperty("skip_reason").ValueKind);
    }

    [Fact]
    public void Remove_Json_ReportsWhyASidecarReclaimWasSkipped()
    {
        using var doc = JsonDocument.Parse(WorkspaceRender.Remove(
            WorkspaceRemoveResult.Removed(
                "/other/repo/.miller",
                workspaceId: "other-ws",
                root: "/other/repo",
                indexDirDeleted: true,
                sidecarReclaim: new StoreSidecarReclaimResult(
                    0, 0, 3, StoreSidecarReclaim.LeaseBusyReason)),
            json: true));

        JsonElement reclaim = doc.RootElement.GetProperty("store_sidecar_reclaim");
        Assert.Equal(0, reclaim.GetProperty("files_deleted").GetInt32());
        Assert.Equal(3, reclaim.GetProperty("files_retained").GetInt32());
        Assert.Equal(
            StoreSidecarReclaim.LeaseBusyReason,
            reclaim.GetProperty("skip_reason").GetString());
        Assert.Contains(
            StoreSidecarReclaim.LeaseBusyReason,
            doc.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public void Remove_Json_NoStoreView_StillCarriesTheReclaimObject()
    {
        using var doc = JsonDocument.Parse(WorkspaceRender.Remove(
            WorkspaceRemoveResult.Removed("/other/repo/.miller"),
            json: true));

        JsonElement reclaim = doc.RootElement.GetProperty("store_sidecar_reclaim");
        Assert.Equal(0, reclaim.GetProperty("files_deleted").GetInt32());
        Assert.Equal(0, reclaim.GetProperty("bytes_reclaimed").GetInt64());
        Assert.Equal(JsonValueKind.Null, reclaim.GetProperty("skip_reason").ValueKind);
        Assert.DoesNotContain("reclaimed", doc.RootElement.GetProperty("message").GetString()!);
    }

    // ---- prune ----

    [Fact]
    public void Prune_Json_ReportsTheStoreSidecarReclaim()
    {
        var result = new WorkspacePruneResult(
            DryRun: false,
            Pruned: [new WorkspacePruneEntry("ws-gone-0001", "gone-repo", "/gone/repo")],
            Kept: 2,
            SidecarReclaim: new StoreSidecarReclaimResult(9, 1_024, 0, null));

        using var doc = JsonDocument.Parse(WorkspaceRender.Prune(result, json: true));

        Assert.Equal(2, doc.RootElement.GetProperty("kept").GetInt32());
        JsonElement reclaim = doc.RootElement.GetProperty("store_sidecar_reclaim");
        Assert.Equal(9, reclaim.GetProperty("files_deleted").GetInt32());
        Assert.Equal(1_024, reclaim.GetProperty("bytes_reclaimed").GetInt64());
        Assert.Equal(0, reclaim.GetProperty("files_retained").GetInt32());
        Assert.Equal(JsonValueKind.Null, reclaim.GetProperty("skip_reason").ValueKind);
    }

    [Fact]
    public void Prune_Compact_ReportsTheReclaimOnlyWhenThereIsSomethingToSay()
    {
        var silent = new WorkspacePruneResult(
            DryRun: false,
            Pruned: [new WorkspacePruneEntry("ws-gone-0001", "gone-repo", "/gone/repo")],
            Kept: 0);
        var loud = silent with { SidecarReclaim = new StoreSidecarReclaimResult(4, 512, 0, null) };

        Assert.DoesNotContain("reclaimed", WorkspaceRender.Prune(silent, json: false));
        Assert.Contains("reclaimed 4 store sidecar files", WorkspaceRender.Prune(loud, json: false));
    }

    [Fact]
    public void Prune_ReportsStoreMaintenanceOnlyWhenThereIsSomethingToSay()
    {
        var silent = new WorkspacePruneResult(
            DryRun: false,
            Pruned: [new WorkspacePruneEntry("ws-gone-0001", "gone-repo", "/gone/repo")],
            Kept: 0);
        var loud = silent with { StoreMaintenance = new StoreMaintenanceOutcome(2_163, null) };

        Assert.DoesNotContain("store maintenance", WorkspaceRender.Prune(silent, json: false));
        Assert.DoesNotContain("store_maintenance", WorkspaceRender.Prune(silent, json: true));
        Assert.Contains(
            "store maintenance: pruned 2163 coordinator request rows",
            WorkspaceRender.Prune(loud, json: false));

        using var doc = JsonDocument.Parse(WorkspaceRender.Prune(loud, json: true));
        JsonElement maintenance = doc.RootElement.GetProperty("store_maintenance");
        Assert.Equal(2_163, maintenance.GetProperty("pruned_request_rows").GetInt64());
        Assert.Equal(JsonValueKind.Null, maintenance.GetProperty("error").ValueKind);
    }

    [Fact]
    public void Prune_ReportsAMaintenanceErrorBesideTheRowsItStillPruned()
    {
        var result = new WorkspacePruneResult(
            DryRun: false,
            Pruned: [new WorkspacePruneEntry("ws-gone-0001", "gone-repo", "/gone/repo")],
            Kept: 0,
            StoreMaintenance: new StoreMaintenanceOutcome(4, "store maintenance timed out"));

        using var doc = JsonDocument.Parse(WorkspaceRender.Prune(result, json: true));

        Assert.Equal(1, doc.RootElement.GetProperty("pruned_total").GetInt32());
        Assert.Equal(
            "store maintenance timed out",
            doc.RootElement.GetProperty("store_maintenance").GetProperty("error").GetString());
        Assert.Contains(
            "store maintenance: pruned 4 coordinator request rows; store maintenance timed out",
            WorkspaceRender.Prune(result, json: false));
    }

    [Fact]
    public void Status_ReportsCoordinatorQuantumOverrunsOnlyWhenThereAreSome()
    {
        var quiet = new StoreCoordinatorQueueFacts(
            QueuedCount: 2,
            ClaimedCount: 0,
            OldestQueuedAgeSeconds: 5,
            DeadClaimOwner: null,
            Groups: [new StoreCoordinatorQueueGroup("update", "queued", 2)],
            WedgedAfterSeconds: 300);
        var loud = quiet with { MaxQuantumOverruns = 2 };

        JsonElement quietJson = Json(
            WorkspaceRender.Status(
                Facts() with { Store = StoreFacts() with { Queue = quiet } },
                TelemetrySummary.Empty,
                json: true));
        JsonElement loudJson = Json(
            WorkspaceRender.Status(
                Facts() with { Store = StoreFacts() with { Queue = loud } },
                TelemetrySummary.Empty,
                json: true));

        Assert.False(
            quietJson.GetProperty("store").GetProperty("queue").TryGetProperty("quantum_overruns", out _));
        Assert.Equal(
            2,
            loudJson.GetProperty("store").GetProperty("queue").GetProperty("quantum_overruns").GetInt64());
        Assert.Contains(
            "quantum overruns 2",
            WorkspaceRender.Status(
                Facts() with { Store = StoreFacts() with { Queue = loud } },
                TelemetrySummary.Empty,
                json: false));
    }
}
