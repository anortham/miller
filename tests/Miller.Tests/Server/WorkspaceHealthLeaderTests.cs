using System.Text.Json;
using Miller.Indexing;
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

    private static LeaderIdentity Identity(int pid = 2222, string version = "0.4.0+cafe123") =>
        new(pid, version, "/cache/0.3.6/miller", new DateTimeOffset(2026, 6, 10, 7, 0, 0, TimeSpan.Zero));

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
    public void HealthJson_NoLeaderFacts_OmitsNothingButStaysNull()
    {
        using var doc = JsonDocument.Parse(WorkspaceRender.Health(Health(Facts(), leader: null), json: true));

        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("indexer_leader").ValueKind);
    }
}
