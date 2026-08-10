using System.Security.Cryptography;
using System.Text;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Server.Telemetry;
using Miller.Server.Tools;
using Miller.Tests.Indexing;
using Xunit;

namespace Miller.Tests.Server;

public sealed class WorkspaceOnboardingAssemblerTests : IDisposable
{
    private readonly List<string> _roots = [];

    [Fact]
    public void CreateWithReadSession_ResolvesTargetsWhenLegacyArtifactIsMissing()
    {
        using JulieDbFixture fixture = JulieDbFixture.CreateForInspect();
        string root = NewRoot();
        string telemetryPath = Path.Combine(root, "telemetry.db");
        string missingLegacyPath = Path.Combine(root, ".miller", "symbols.db");
        SeedTelemetry(telemetryPath, fixture.WorkspaceRoot, Hash("GetUser"));

        using LegacyArtifactReadSession session = LegacyArtifactReadSession.Open(fixture.DbPath);
        WorkspaceOnboardingFacts onboarding = WorkspaceOnboardingAssembler.Create(
            Facts(fixture.WorkspaceRoot, missingLegacyPath),
            telemetryPath,
            "workspace",
            missingLegacyPath,
            session);

        RecoveredTargetHash target = Assert.Single(onboarding.HotTargets);
        Assert.Equal("GetUser", target.Name);
        Assert.Equal("auth/UserService.cs", target.Path);
    }

    [Fact]
    public void CreateFromWorkspace_LegacyPreservedUsesLegacyArtifact()
    {
        using JulieDbFixture fixture = JulieDbFixture.CreateForInspect();
        string root = NewRoot();
        string telemetryPath = Path.Combine(root, "telemetry.db");
        SeedTelemetry(telemetryPath, fixture.WorkspaceRoot, Hash("GetUser"));

        WorkspaceOnboardingFacts onboarding = WorkspaceOnboardingAssembler.CreateFromWorkspace(
            Facts(fixture.WorkspaceRoot, fixture.DbPath, StoreWorkspaceFacts.Unavailable("ready", "", "")),
            telemetryPath,
            "workspace",
            fixture.WorkspaceRoot,
            fixture.DbPath,
            storeEnabled: false);

        RecoveredTargetHash target = Assert.Single(onboarding.HotTargets);
        Assert.Equal("GetUser", target.Name);
        Assert.Equal("auth/UserService.cs", target.Path);
    }

    [Fact]
    public void CreateFromWorkspace_StoreModeDoesNotFallbackToLegacyArtifact()
    {
        using JulieDbFixture fixture = JulieDbFixture.CreateForInspect();
        string root = NewRoot();
        string telemetryPath = Path.Combine(root, "telemetry.db");
        SeedTelemetry(telemetryPath, fixture.WorkspaceRoot, Hash("GetUser"));

        WorkspaceOnboardingFacts onboarding = WorkspaceOnboardingAssembler.CreateFromWorkspace(
            Facts(fixture.WorkspaceRoot, fixture.DbPath, StoreWorkspaceFacts.Unavailable("failed", "binding_not_ready", "missing")),
            telemetryPath,
            "workspace",
            fixture.WorkspaceRoot,
            fixture.DbPath,
            storeEnabled: true);

        RecoveredTargetHash target = Assert.Single(onboarding.HotTargets);
        Assert.Equal("unresolved_hash", target.Confidence);
        Assert.Null(target.Name);
        Assert.Equal(0, target.CandidateCount);
    }

    public void Dispose()
    {
        foreach (string root in _roots)
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static WorkspaceFacts Facts(string root, string dbPath, StoreWorkspaceFacts? store = null) =>
        new(
            Root: root,
            WorkspaceId: "workspace",
            DbPath: dbPath,
            IsLeader: false,
            DocumentCount: 1,
            KnownExtensionsCount: 1,
            BuiltRevision: 1,
            LatestObservedRevision: 1,
            IndexFresh: true,
            QueueEmpty: true,
            Store: store);

    private static void SeedTelemetry(string telemetryPath, string root, string targetHash)
    {
        using TelemetryLedger ledger = TelemetryLedger.Open(telemetryPath, workspaceId: null);
        ledger.Record(new TelemetryRecord(
            Tool: "search",
            Op: "auto",
            WorkspaceId: "workspace",
            WorkspaceRoot: root,
            DurationMs: 10,
            Outcome: "ok",
            ErrorKind: null,
            ResultCount: 1,
            BytesExamined: 0,
            BytesReturned: 10,
            SourceBytes: 0,
            EstTokens: 1,
            IndexFresh: true,
            TargetHash: targetHash,
            MetadataJson: "{}"));
    }

    private string NewRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "miller-onboarding-assembler-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _roots.Add(root);
        return root;
    }

    private static string Hash(string raw) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
}
