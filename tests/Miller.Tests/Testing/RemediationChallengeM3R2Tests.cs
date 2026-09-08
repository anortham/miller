using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Core.Search;
using Miller.Dashboard;
using Miller.Indexing;
using Miller.Indexing.Testing;
using Miller.Server.Cli;
using Miller.Server.Telemetry;
using Miller.Server.Tools;
using Miller.Server.Workspaces;
using Miller.Testing;
using Miller.Tests.Support;
using Miller.Tests.Testing.Daemon.ControlPlane;
using Miller.Tests.Testing.Daemon.Engine;
using Miller.Tests.Testing.Selection;
using Xunit;

namespace Miller.Tests.Testing;

[Collection(ContinuousTestDaemonAdoptionCollection.Name)]
public sealed class RemediationChallengeM3R2Tests : IDisposable
{
    private readonly string _dir;
    private readonly string _registryDb;
    private readonly string _telemetryDb;
    private readonly string _mainRoot;
    private readonly string _worktreeRoot;

    public RemediationChallengeM3R2Tests()
    {
        _dir = Directory.CreateTempSubdirectory("miller-remediation-challenge-").FullName;
        _registryDb = Path.Combine(_dir, "workspaces.db");
        _telemetryDb = Path.Combine(_dir, "telemetry.db");
        _mainRoot = Path.Combine(_dir, "main");
        _worktreeRoot = Path.Combine(_dir, "wt");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // =========================================================================
    // Challenge 1: Probe C3 explicit runs with 0 projects or 0 impacted tests.
    // Verify ACK reason is "run" (matching request) and state is Completed.
    // =========================================================================

    [Fact]
    public async Task C3_ExplicitRun_WithZeroProjects_AckReasonIsRun_AndStateIsCompleted()
    {
        BuildLinkedWorktree();
        EnableMain();

        var adoption = new ContinuousTestWorktreeAdoptionOptions
        {
            DiscoverRegisteredRoots = () => [_worktreeRoot],
            CreateContext = root => new ContinuousTestWorkspaceContext
            {
                WorkspaceRoot = root,
                WorkspaceId = "ws:wt",
                // Projects list is empty (Count == 0)
            },
            ScanInterval = TimeSpan.Zero,
        };

        using var cts = new CancellationTokenSource();
        Task<ContinuousTestDaemonSnapshot> run = ContinuousTestDaemonHost.RunAsync(
            _mainRoot,
            HostOptions(adoption),
            cts.Token);

        try
        {
            await WaitForWorktreeStatusAsync(state: CtDaemonLifecycleState.Running);

            // Issue an explicit routed run with reason: "run"
            CtDaemonCommandRequest rerun = CtDaemonRouting.WriteRoutedRequest(
                _mainRoot,
                CtDaemonCommandKind.Run,
                reason: "run",
                freshness: null,
                targetWorkspaceRoot: _worktreeRoot);

            CtDaemonCommandAck? rerunAck = await Task.Run(() => CtCommandChannel.WaitForAck(
                _mainRoot,
                rerun.CommandId,
                TimeSpan.FromSeconds(5)));

            Assert.NotNull(rerunAck);
            Assert.Equal("run", rerunAck.Reason);
            Assert.Equal(CtDaemonCommandState.Completed, rerunAck.State);
        }
        finally
        {
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task C3_ExplicitRun_WithCustomReason_AndZeroProjects_AckReasonMatchesRequest()
    {
        BuildLinkedWorktree();
        EnableMain();

        var adoption = new ContinuousTestWorktreeAdoptionOptions
        {
            DiscoverRegisteredRoots = () => [_worktreeRoot],
            CreateContext = root => new ContinuousTestWorkspaceContext
            {
                WorkspaceRoot = root,
                WorkspaceId = "ws:wt",
                // Projects list is empty (Count == 0)
            },
            ScanInterval = TimeSpan.Zero,
        };

        using var cts = new CancellationTokenSource();
        Task<ContinuousTestDaemonSnapshot> run = ContinuousTestDaemonHost.RunAsync(
            _mainRoot,
            HostOptions(adoption),
            cts.Token);

        try
        {
            await WaitForWorktreeStatusAsync(state: CtDaemonLifecycleState.Running);

            // Issue an explicit routed run with custom reason: "custom-trigger"
            CtDaemonCommandRequest rerun = CtDaemonRouting.WriteRoutedRequest(
                _mainRoot,
                CtDaemonCommandKind.Run,
                reason: "custom-trigger",
                freshness: null,
                targetWorkspaceRoot: _worktreeRoot);

            CtDaemonCommandAck? rerunAck = await Task.Run(() => CtCommandChannel.WaitForAck(
                _mainRoot,
                rerun.CommandId,
                TimeSpan.FromSeconds(5)));

            Assert.NotNull(rerunAck);
            Assert.Equal("custom-trigger", rerunAck.Reason);
            Assert.Equal(CtDaemonCommandState.Completed, rerunAck.State);
        }
        finally
        {
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task C3_ExplicitRun_DefaultReasonNull_AckReasonDefaultsToRun()
    {
        BuildLinkedWorktree();
        EnableMain();

        var adoption = new ContinuousTestWorktreeAdoptionOptions
        {
            DiscoverRegisteredRoots = () => [_worktreeRoot],
            CreateContext = root => new ContinuousTestWorkspaceContext
            {
                WorkspaceRoot = root,
                WorkspaceId = "ws:wt",
            },
            ScanInterval = TimeSpan.Zero,
        };

        using var cts = new CancellationTokenSource();
        Task<ContinuousTestDaemonSnapshot> run = ContinuousTestDaemonHost.RunAsync(
            _mainRoot,
            HostOptions(adoption),
            cts.Token);

        try
        {
            await WaitForWorktreeStatusAsync(state: CtDaemonLifecycleState.Running);

            // Issue an explicit routed run with reason: null
            CtDaemonCommandRequest rerun = CtDaemonRouting.WriteRoutedRequest(
                _mainRoot,
                CtDaemonCommandKind.Run,
                reason: null,
                freshness: null,
                targetWorkspaceRoot: _worktreeRoot);

            CtDaemonCommandAck? rerunAck = await Task.Run(() => CtCommandChannel.WaitForAck(
                _mainRoot,
                rerun.CommandId,
                TimeSpan.FromSeconds(5)));

            Assert.NotNull(rerunAck);
            Assert.Equal("run", rerunAck.Reason);
            Assert.Equal(CtDaemonCommandState.Completed, rerunAck.State);
        }
        finally
        {
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task C3_ExplicitRun_WithProjects_ZeroImpactedTests_ReachesTerminalCompleted()
    {
        BuildLinkedWorktree();
        EnableMain();

        string ctDbPath = Path.Combine(_worktreeRoot, ".miller", "ct.db");
        Directory.CreateDirectory(Path.GetDirectoryName(ctDbPath)!);
        using var store = new ContinuousTestStore(ctDbPath);
        var provider = new FakeContinuousTestProvider();
        var queue = new ContinuousTestDaemonQueue(
            store,
            EngineTestSupport.Selector(store, revision: 1),
            new ContinuousTestCoordinator(provider, store));

        var freshness = new CtFreshnessKey(EngineTestSupport.Identity, 1);
        string projectPath = Path.Combine(_worktreeRoot, "tests", "Proj.Tests.csproj");
        var project = new ContinuousTestProject("proj-1", "ws:wt", projectPath, Framework: "xunit");

        var adoption = new ContinuousTestWorktreeAdoptionOptions
        {
            DiscoverRegisteredRoots = () => [_worktreeRoot],
            CreateContext = root => new ContinuousTestWorkspaceContext
            {
                WorkspaceRoot = root,
                WorkspaceId = "ws:wt",
                Projects = [project],
                Queue = queue,
                LatestFreshness = freshness,
                StartedAt = freshness,
            },
            ScanInterval = TimeSpan.Zero,
        };

        using var cts = new CancellationTokenSource();
        Task<ContinuousTestDaemonSnapshot> run = ContinuousTestDaemonHost.RunAsync(
            _mainRoot,
            HostOptions(adoption),
            cts.Token);

        try
        {
            await WaitForWorktreeStatusAsync(state: CtDaemonLifecycleState.Running);

            CtDaemonCommandRequest rerun = CtDaemonRouting.WriteRoutedRequest(
                _mainRoot,
                CtDaemonCommandKind.Run,
                reason: "run",
                freshness: freshness,
                targetWorkspaceRoot: _worktreeRoot);

            CtDaemonCommandAck? rerunAck = null;
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(5))
            {
                rerunAck = CtCommandChannel.TryReadAck(_mainRoot, rerun.CommandId);
                if (rerunAck?.State == CtDaemonCommandState.Completed)
                    break;
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            Assert.NotNull(rerunAck);
            Assert.Equal("completed", rerunAck.Reason);
            Assert.Equal(CtDaemonCommandState.Completed, rerunAck.State);
        }
        finally
        {
            await cts.CancelAsync();
            await run;
        }
    }

    // =========================================================================
    // Challenge 2: Probe W3 warning priority order
    // (availability warnings precede capability gaps).
    // =========================================================================

    [Fact]
    public void W3_WarningPriority_AvailabilityPrecedesCapabilityGaps()
    {
        var status = new WorkspaceFacts(
            Root: "/repo",
            WorkspaceId: "ws-123",
            DbPath: "/repo/.miller/symbols.db",
            IsLeader: true,
            DocumentCount: 10,
            KnownExtensionsCount: 2,
            BuiltRevision: 5,
            LatestObservedRevision: 5,
            IndexFresh: false, // -> index_stale
            QueueEmpty: true)
        {
            WarningText = "scan degraded", // -> index_warning
            SearchSidecar = new SearchSidecarFacts("missing", null, null, 0, null, "Search sidecar artifact missing"), // -> search_sidecar
            ContentCorpus = new ContentCorpusFacts("error", null, null, null, 0, 0, 0, 0, Error: "Content corpus corrupt"), // -> content_corpus
        };

        var capabilityGaps = new[]
        {
            new CapabilityGapGroup("csharp", "unsupported_construct", "open", 5)
        };
        var parseDiagnostics = new[]
        {
            new ParseDiagnosticGroup("csharp", "syntax_error", 10)
        };
        var extraction = new WorkspaceExtractionHealthFacts(
            ParseDiagnostics: HealthFactSection<ParseDiagnosticGroup>.FromRows(parseDiagnostics),
            CapabilityGaps: HealthFactSection<CapabilityGapGroup>.FromRows(capabilityGaps),
            LanguageCapabilities: HealthFactSection<LanguageCapabilitySummary>.FromRows(Array.Empty<LanguageCapabilitySummary>()),
            StructuralFacts: HealthFactSection<StructuralFactGroup>.FromRows(Array.Empty<StructuralFactGroup>()),
            ComplexityMetrics: HealthFactSection<ComplexityMetricGroup>.FromRows(Array.Empty<ComplexityMetricGroup>()),
            Files: HealthFactSection<FileStatusGroup>.FromRows(Array.Empty<FileStatusGroup>()));

        var telemetryHealth = new TelemetryHealthFacts(
            OkCount: 10,
            EmptyCount: 2,
            ErrorCount: 4);

        WorkspaceHealthFacts health = WorkspaceHealthFacts.Create(
            status,
            TelemetrySummary.Empty,
            telemetryHealth,
            extraction);

        // Verify that all availability warnings precede capability_gaps
        List<string> codes = health.Warnings.Select(w => w.Code).ToList();

        int indexWarningIdx = codes.IndexOf("index_warning");
        int indexStaleIdx = codes.IndexOf("index_stale");
        int searchSidecarIdx = codes.IndexOf("search_sidecar");
        int contentCorpusIdx = codes.IndexOf("content_corpus");
        int capabilityGapsIdx = codes.IndexOf("capability_gaps");
        int parseDiagnosticsIdx = codes.IndexOf("parse_diagnostics");
        int telemetryErrorsIdx = codes.IndexOf("telemetry_errors");

        Assert.True(indexWarningIdx >= 0, "index_warning should be present");
        Assert.True(indexStaleIdx >= 0, "index_stale should be present");
        Assert.True(searchSidecarIdx >= 0, "search_sidecar should be present");
        Assert.True(contentCorpusIdx >= 0, "content_corpus should be present");
        Assert.True(capabilityGapsIdx >= 0, "capability_gaps should be present");
        Assert.True(parseDiagnosticsIdx >= 0, "parse_diagnostics should be present");
        Assert.True(telemetryErrorsIdx >= 0, "telemetry_errors should be present");

        // Availability warnings MUST strictly precede capability gaps
        Assert.True(indexWarningIdx < capabilityGapsIdx, "index_warning must precede capability_gaps");
        Assert.True(indexStaleIdx < capabilityGapsIdx, "index_stale must precede capability_gaps");
        Assert.True(searchSidecarIdx < capabilityGapsIdx, "search_sidecar must precede capability_gaps");
        Assert.True(contentCorpusIdx < capabilityGapsIdx, "content_corpus must precede capability_gaps");

        // Capability gaps must precede telemetry errors
        Assert.True(capabilityGapsIdx < telemetryErrorsIdx, "capability_gaps must precede telemetry_errors");
    }

    [Fact]
    public void W3_CompactRender_PrioritizesTopThreeAvailabilityWarnings()
    {
        var status = new WorkspaceFacts(
            Root: "/repo",
            WorkspaceId: "ws-123",
            DbPath: "/repo/.miller/symbols.db",
            IsLeader: true,
            DocumentCount: 10,
            KnownExtensionsCount: 2,
            BuiltRevision: 5,
            LatestObservedRevision: 5,
            IndexFresh: false,
            QueueEmpty: true)
        {
            WarningText = "scan degraded",
            SearchSidecar = new SearchSidecarFacts("missing", null, null, 0, null, "Search sidecar artifact missing"),
            ContentCorpus = new ContentCorpusFacts("error", null, null, null, 0, 0, 0, 0, Error: "Content corpus corrupt"),
        };

        var capabilityGaps = new[]
        {
            new CapabilityGapGroup("csharp", "unsupported_construct", "open", 5)
        };
        var extraction = new WorkspaceExtractionHealthFacts(
            ParseDiagnostics: HealthFactSection<ParseDiagnosticGroup>.FromRows(Array.Empty<ParseDiagnosticGroup>()),
            CapabilityGaps: HealthFactSection<CapabilityGapGroup>.FromRows(capabilityGaps),
            LanguageCapabilities: HealthFactSection<LanguageCapabilitySummary>.FromRows(Array.Empty<LanguageCapabilitySummary>()),
            StructuralFacts: HealthFactSection<StructuralFactGroup>.FromRows(Array.Empty<StructuralFactGroup>()),
            ComplexityMetrics: HealthFactSection<ComplexityMetricGroup>.FromRows(Array.Empty<ComplexityMetricGroup>()),
            Files: HealthFactSection<FileStatusGroup>.FromRows(Array.Empty<FileStatusGroup>()));

        WorkspaceHealthFacts health = WorkspaceHealthFacts.Create(
            status,
            TelemetrySummary.Empty,
            new TelemetryHealthFacts(0, 0, 0),
            extraction);

        string compactJson = WorkspaceRender.Health(health, WorkspaceHealthFormat.JsonSummary);
        using var doc = JsonDocument.Parse(compactJson);
        JsonElement root = doc.RootElement;

        // Total count should be at least 5 (index_warning, index_stale, search_sidecar, content_corpus, capability_gaps)
        int totalWarnings = root.GetProperty("warnings_total_count").GetInt32();
        int omitted = root.GetProperty("warnings_omitted_count").GetInt32();
        Assert.Equal(5, totalWarnings);
        Assert.Equal(2, omitted);

        // Rendered warnings array must be clamped to at most 3
        JsonElement.ArrayEnumerator renderedWarnings = root.GetProperty("warnings").EnumerateArray();
        List<string> topThreeCodes = renderedWarnings.Select(w => w.GetProperty("code").GetString()!).ToList();
        Assert.Equal(3, topThreeCodes.Count);

        // Top three must all be availability warnings
        Assert.Contains("index_warning", topThreeCodes);
        Assert.Contains("index_stale", topThreeCodes);
        Assert.Contains("search_sidecar", topThreeCodes);

        // capability_gaps is lower priority and was omitted from the top 3
        Assert.DoesNotContain("capability_gaps", topThreeCodes);
    }

    // =========================================================================
    // Challenge 3: Probe W4 DashboardData.ReadSnapshot with explicit anchor dates
    // and default UTC time (7-day sliding window for dormant vs active workspaces).
    // =========================================================================

    [Fact]
    public void W4_DashboardSnapshot_DormantWorkspace_DefaultUtc_YieldsSparseTelemetry()
    {
        string wsDir = Path.Combine(_dir, "ws-dormant");
        Directory.CreateDirectory(wsDir);

        using (var registry = WorkspaceRegistry.Open(_registryDb))
        {
            registry.UpsertSeen(
                "ws-dormant",
                "dormant-slug",
                wsDir,
                Path.Combine(wsDir, ".miller", "symbols.db"),
                WorkspaceRegistryState.Ready,
                DateTimeOffset.Parse("2026-05-31T10:00:00Z"));
            registry.MarkScanned("ws-dormant", 1, DateTimeOffset.Parse("2026-05-31T10:01:00Z"));
        }

        // Insert telemetry dated 2026-05-31 (months in the past)
        InsertTelemetryRow("ws-dormant", "search", "ok", "2026-05-31T10:02:00.000Z", durationMs: 10);
        InsertTelemetryRow("ws-dormant", "inspect", "ok", "2026-05-31T10:03:00.000Z", durationMs: 15);
        InsertTelemetryRow("ws-dormant", "context", "ok", "2026-05-31T10:04:00.000Z", durationMs: 25);

        // Read snapshot using default UTC wall-clock time (anchor = null)
        DashboardSnapshot snapshot = DashboardData.ReadSnapshot(
            _registryDb,
            _telemetryDb,
            workspaceId: "ws-dormant",
            anchor: null);

        Assert.NotNull(snapshot.Onboarding);
        Assert.Equal("ws-dormant", snapshot.Onboarding.WorkspaceId);
        // Because events occurred > 7 days ago relative to current UTC, they are outside the sliding window
        Assert.Equal(0, snapshot.Onboarding.TotalCalls);
        Assert.Equal("sparse", snapshot.Onboarding.State);
    }

    [Fact]
    public void W4_DashboardSnapshot_DormantWorkspace_ExplicitAnchor_YieldsPopulatedTelemetry()
    {
        string wsDir = Path.Combine(_dir, "ws-dormant-anchor");
        Directory.CreateDirectory(wsDir);

        using (var registry = WorkspaceRegistry.Open(_registryDb))
        {
            registry.UpsertSeen(
                "ws-dormant-anchor",
                "dormant-anchor-slug",
                wsDir,
                Path.Combine(wsDir, ".miller", "symbols.db"),
                WorkspaceRegistryState.Ready,
                DateTimeOffset.Parse("2026-05-31T10:00:00Z"));
            registry.MarkScanned("ws-dormant-anchor", 1, DateTimeOffset.Parse("2026-05-31T10:01:00Z"));
        }

        InsertTelemetryRow("ws-dormant-anchor", "search", "ok", "2026-05-31T10:02:00.000Z", durationMs: 10);
        InsertTelemetryRow("ws-dormant-anchor", "inspect", "ok", "2026-05-31T10:03:00.000Z", durationMs: 15);
        InsertTelemetryRow("ws-dormant-anchor", "context", "ok", "2026-05-31T10:04:00.000Z", durationMs: 25);

        // Read snapshot using explicit anchor within 7 days of the events
        DateTimeOffset explicitAnchor = DateTimeOffset.Parse("2026-05-31T10:05:00.000Z", CultureInfo.InvariantCulture);
        DashboardSnapshot snapshot = DashboardData.ReadSnapshot(
            _registryDb,
            _telemetryDb,
            workspaceId: "ws-dormant-anchor",
            anchor: explicitAnchor);

        Assert.NotNull(snapshot.Onboarding);
        Assert.Equal("ws-dormant-anchor", snapshot.Onboarding.WorkspaceId);
        Assert.Equal(3, snapshot.Onboarding.TotalCalls);
        Assert.Equal("ready", snapshot.Onboarding.State);
    }

    [Fact]
    public void W4_DashboardSnapshot_ActiveWorkspace_DefaultUtc_YieldsPopulatedTelemetry()
    {
        string wsDir = Path.Combine(_dir, "ws-active");
        Directory.CreateDirectory(wsDir);

        using (var registry = WorkspaceRegistry.Open(_registryDb))
        {
            registry.UpsertSeen(
                "ws-active",
                "active-slug",
                wsDir,
                Path.Combine(wsDir, ".miller", "symbols.db"),
                WorkspaceRegistryState.Ready,
                DateTimeOffset.UtcNow);
            registry.MarkScanned("ws-active", 1, DateTimeOffset.UtcNow);
        }

        // Insert recent events (1 hour ago, within 7-day window)
        string recentTs1 = DateTimeOffset.UtcNow.AddHours(-1).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        string recentTs2 = DateTimeOffset.UtcNow.AddHours(-2).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        string recentTs3 = DateTimeOffset.UtcNow.AddHours(-3).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

        InsertTelemetryRow("ws-active", "search", "ok", recentTs1, durationMs: 10);
        InsertTelemetryRow("ws-active", "inspect", "ok", recentTs2, durationMs: 15);
        InsertTelemetryRow("ws-active", "context", "ok", recentTs3, durationMs: 25);

        // Default UTC wall-clock time
        DashboardSnapshot snapshot = DashboardData.ReadSnapshot(
            _registryDb,
            _telemetryDb,
            workspaceId: "ws-active",
            anchor: null);

        Assert.NotNull(snapshot.Onboarding);
        Assert.Equal("ws-active", snapshot.Onboarding.WorkspaceId);
        Assert.Equal(3, snapshot.Onboarding.TotalCalls);
        Assert.Equal("ready", snapshot.Onboarding.State);
    }

    [Fact]
    public void W4_TelemetryOnboardingReader_SevenDayWindowBoundary_Probed()
    {
        DateTimeOffset anchor = DateTimeOffset.Parse("2026-09-07T12:00:00.000Z", CultureInfo.InvariantCulture);

        // 6 days before anchor -> within 7-day window
        string insideTs = anchor.AddDays(-6).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        // 8 days before anchor -> outside 7-day window
        string outsideTs = anchor.AddDays(-8).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

        InsertTelemetryRow("ws-boundary", "search", "ok", insideTs, durationMs: 10);
        InsertTelemetryRow("ws-boundary", "inspect", "ok", outsideTs, durationMs: 15);

        TelemetryOnboardingFacts facts = TelemetryOnboardingReader.Read(
            _telemetryDb,
            "ws-boundary",
            windowDays: 7,
            anchor: anchor);

        Assert.True(facts.Available);
        Assert.Equal(1, facts.TotalCalls);
    }

    // =========================================================================
    // Challenge 4: Probe R2/R8 hidden test reporting notice
    // ("test results excluded from a bounded candidate window" vs "N test chunks hidden").
    // =========================================================================

    [Fact]
    public void R2_R8_RunTextContent_SaturatedWindow_ReportsBoundedNotice_AndJsonFlag()
    {
        var testHit = CreateSampleHit("src/Service.cs");
        var testIndex = new ChallengeFakeTextContentIndex(
            new TextContentSearchResult([testHit], ExcludedTestCount: 50, WindowSaturated: true));

        // Compact format check
        string compact = SearchTool.RunTextContent(
            testIndex,
            "ProcessOrder",
            "source",
            limit: 10,
            excludeTests: true,
            json: false,
            out _);

        Assert.Contains("note: test results excluded from a bounded candidate window (pass exclude_tests=false to view)", compact, StringComparison.Ordinal);
        Assert.DoesNotContain("test chunks hidden", compact, StringComparison.Ordinal);

        // JSON format check
        string json = SearchTool.RunTextContent(
            testIndex,
            "ProcessOrder",
            "source",
            limit: 10,
            excludeTests: true,
            json: true,
            out _);

        using var doc = JsonDocument.Parse(json);
        JsonElement row = doc.RootElement[0];
        Assert.True(row.GetProperty("tests_excluded_bounded").GetBoolean());
        Assert.False(row.TryGetProperty("tests_hidden", out _));
    }

    [Fact]
    public void R2_R8_RunTextContent_UnsaturatedWindow_ReportsExactCount_AndJsonNumber()
    {
        var testHit = CreateSampleHit("src/Service.cs");
        var testIndex = new ChallengeFakeTextContentIndex(
            new TextContentSearchResult([testHit], ExcludedTestCount: 4, WindowSaturated: false));

        // Compact format check
        string compact = SearchTool.RunTextContent(
            testIndex,
            "ProcessOrder",
            "source",
            limit: 10,
            excludeTests: true,
            json: false,
            out _);

        Assert.Contains("note: 4 test chunks hidden (pass exclude_tests=false to view)", compact, StringComparison.Ordinal);
        Assert.DoesNotContain("test results excluded from a bounded candidate window", compact, StringComparison.Ordinal);

        // JSON format check
        string json = SearchTool.RunTextContent(
            testIndex,
            "ProcessOrder",
            "source",
            limit: 10,
            excludeTests: true,
            json: true,
            out _);

        using var doc = JsonDocument.Parse(json);
        JsonElement row = doc.RootElement[0];
        Assert.Equal(4, row.GetProperty("tests_hidden").GetInt32());
        Assert.False(row.TryGetProperty("tests_excluded_bounded", out _));
    }

    [Fact]
    public void R2_R8_RunTextContent_UnsaturatedWindow_SingleChunk_ReportsSingularChunk()
    {
        var testHit = CreateSampleHit("src/Service.cs");
        var testIndex = new ChallengeFakeTextContentIndex(
            new TextContentSearchResult([testHit], ExcludedTestCount: 1, WindowSaturated: false));

        string compact = SearchTool.RunTextContent(
            testIndex,
            "ProcessOrder",
            "source",
            limit: 10,
            excludeTests: true,
            json: false,
            out _);

        Assert.Contains("note: 1 test chunk hidden (pass exclude_tests=false to view)", compact, StringComparison.Ordinal);
        Assert.DoesNotContain("chunks", compact, StringComparison.Ordinal);
    }

    [Fact]
    public void R2_R8_RunTextContent_ZeroHidden_OmitsBothNotices()
    {
        var testHit = CreateSampleHit("src/Service.cs");
        var testIndex = new ChallengeFakeTextContentIndex(
            new TextContentSearchResult([testHit], ExcludedTestCount: 0, WindowSaturated: false));

        string compact = SearchTool.RunTextContent(
            testIndex,
            "ProcessOrder",
            "source",
            limit: 10,
            excludeTests: true,
            json: false,
            out _);

        Assert.DoesNotContain("test results excluded", compact, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden", compact, StringComparison.Ordinal);

        string json = SearchTool.RunTextContent(
            testIndex,
            "ProcessOrder",
            "source",
            limit: 10,
            excludeTests: true,
            json: true,
            out _);

        using var doc = JsonDocument.Parse(json);
        JsonElement row = doc.RootElement[0];
        Assert.False(row.TryGetProperty("tests_hidden", out _));
        Assert.False(row.TryGetProperty("tests_excluded_bounded", out _));
    }

    [Fact]
    public void R2_R8_RunTextContent_EmptyHits_SaturatedVsUnsaturated()
    {
        // 0 hits returned, but tests were excluded
        var saturatedIndex = new ChallengeFakeTextContentIndex(
            new TextContentSearchResult([], ExcludedTestCount: 20, WindowSaturated: true));

        string saturatedCompact = SearchTool.RunTextContent(
            saturatedIndex,
            "ProcessOrder",
            "source",
            limit: 10,
            excludeTests: true,
            json: false,
            out _);

        Assert.Contains("note: test results excluded from a bounded candidate window (pass exclude_tests=false to view)", saturatedCompact, StringComparison.Ordinal);

        var unsaturatedIndex = new ChallengeFakeTextContentIndex(
            new TextContentSearchResult([], ExcludedTestCount: 4, WindowSaturated: false));

        string unsaturatedCompact = SearchTool.RunTextContent(
            unsaturatedIndex,
            "ProcessOrder",
            "source",
            limit: 10,
            excludeTests: true,
            json: false,
            out _);

        Assert.Contains("note: 4 test chunks hidden (pass exclude_tests=false to view)", unsaturatedCompact, StringComparison.Ordinal);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static TextContentSearchHit CreateSampleHit(string path) => new(
        SourceId: "src-1",
        ChunkId: "chunk-1",
        ContentKind: "source",
        Path: path,
        Url: null,
        DisplayPath: path,
        Language: "csharp",
        Score: 4.0,
        Line: 10,
        LineStart: 1,
        LineEnd: 20,
        ByteStart: 0,
        ByteEnd: 200,
        Snippet: "public void ProcessOrder() { }",
        SourceBytes: 200,
        ContainingSymbolId: null,
        ContainingSymbolName: null);

    private void InsertTelemetryRow(
        string workspaceId,
        string tool,
        string outcome,
        string ts,
        long durationMs)
    {
        using (TelemetryLedger.Open(_telemetryDb, workspaceId, "/repo/test"))
        {
            // Initializes the telemetry schema
        }

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _telemetryDb,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tool_telemetry
                (id, ts, tool, op, workspace_id, workspace_root, duration_ms, outcome, error_kind,
                 error_message, error_detail, bytes_returned, source_bytes, est_tokens)
            VALUES
                ($id, $ts, $tool, $op, $ws, $root, $duration, $outcome, NULL,
                 NULL, NULL, 100, 1000, 50);
            """;
        cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
        cmd.Parameters.AddWithValue("$ts", ts);
        cmd.Parameters.AddWithValue("$tool", tool);
        cmd.Parameters.AddWithValue("$op", DBNull.Value);
        cmd.Parameters.AddWithValue("$ws", workspaceId);
        cmd.Parameters.AddWithValue("$root", "/repo/test");
        cmd.Parameters.AddWithValue("$duration", durationMs);
        cmd.Parameters.AddWithValue("$outcome", outcome);
        cmd.ExecuteNonQuery();
    }

    private void BuildLinkedWorktree()
    {
        string adminDir = Path.Combine(_mainRoot, ".git", "worktrees", "wt");
        Directory.CreateDirectory(adminDir);
        File.WriteAllText(Path.Combine(adminDir, "commondir"), "../..\n");
        Directory.CreateDirectory(_worktreeRoot);
        File.WriteAllText(Path.Combine(_worktreeRoot, ".git"), $"gitdir: {adminDir}\n");
    }

    private void EnableMain()
    {
        string marker = ContinuousTestPolicy.EnabledMarkerPath(_mainRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.WriteAllText(marker, string.Empty);
    }

    private static ContinuousTestDaemonHostOptions HostOptions(ContinuousTestWorktreeAdoptionOptions adoption) =>
        new()
        {
            Enabled = true,
            AcquireLease = false,
            Enqueuer = new RecordingEnqueuer(),
            PollInterval = TimeSpan.FromMilliseconds(5),
            WorktreeAdoption = adoption,
        };

    private async Task<CtDaemonStatusRecord> WaitForWorktreeStatusAsync(CtDaemonLifecycleState state)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        do
        {
            CtDaemonStatusRecord? record = CtDaemonLease.TryReadStatus(_worktreeRoot);
            if (record is not null && record.State == state)
                return record;
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        while (DateTime.UtcNow < deadline);

        throw new TimeoutException($"the worktree status record never reached state {state}");
    }

    private sealed class ChallengeFakeTextContentIndex : ITextContentSearchIndex
    {
        private readonly TextContentSearchResult _result;

        public ChallengeFakeTextContentIndex(TextContentSearchResult result)
        {
            _result = result;
        }

        public int DocumentCount => _result.Hits.Count;

        public IReadOnlyList<TextContentSearchHit> Search(
            string query,
            string contentKind,
            int limit = 10,
            bool excludeTests = false,
            string? sourceId = null) => _result.Hits;

        public IReadOnlyList<TextContentSearchHit> Search(
            string query,
            IReadOnlyCollection<string> contentKinds,
            int limit = 10,
            bool excludeTests = false,
            string? sourceId = null) => _result.Hits;

        public TextContentSearchResult SearchExtended(
            string query,
            IReadOnlyCollection<string> contentKinds,
            int limit = 10,
            bool excludeTests = false,
            string? sourceId = null) => _result;

        public TextContentSearchResult SearchExtended(
            string query,
            string contentKind,
            int limit = 10,
            bool excludeTests = false,
            string? sourceId = null) => _result;
    }
}
