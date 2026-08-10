using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Miller.Indexing;
using Miller.Indexing.Store;
using Miller.Server.Hosting;
using Miller.Server.Workspaces;
using Xunit;

namespace Miller.Tests.Server;

public sealed class IndexerLeadershipCoordinatorTests
{
    private sealed class TestLease : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void TryClaim_Ineligible_NeverInvokesAcquireAndPublishesVerdict()
    {
        bool acquireAttempted = false;
        var coordinator = NewCoordinator(
            tryAcquireLeadership: _ =>
            {
                acquireAttempted = true;
                return new TestLease();
            },
            ownExtractorVersion: () => "2.0.0",
            readArtifactExtractorVersion: _ => "3.0.0");

        IndexerLeadershipClaimResult result = coordinator.TryClaim("/repo/.miller", "/repo/.miller/symbols.db");

        Assert.False(result.Claimed);
        Assert.Null(result.Lease);
        Assert.False(acquireAttempted);
        Assert.NotNull(coordinator.EligibilityVerdict);
        Assert.False(coordinator.EligibilityVerdict!.Eligible);
        Assert.Equal(coordinator.EligibilityVerdict, result.Verdict);
    }

    [Fact]
    public void TryClaim_Eligible_InvokesAcquireAndReturnsLease()
    {
        var lease = new TestLease();
        var coordinator = NewCoordinator(
            tryAcquireLeadership: _ => lease,
            ownExtractorVersion: () => "3.0.0",
            readArtifactExtractorVersion: _ => "2.0.0");

        IndexerLeadershipClaimResult result = coordinator.TryClaim("/repo/.miller", "/repo/.miller/symbols.db");

        Assert.True(result.Claimed);
        Assert.Same(lease, result.Lease);
        Assert.True(result.Verdict.ArtifactOlderThanOwn);
    }

    [Fact]
    public void BeginCooldown_SuppressesClaimUntilRequesterDiesOrExpires()
    {
        DateTimeOffset now = new(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);
        bool requesterAlive = true;
        int acquireAttempts = 0;
        var coordinator = NewCoordinator(
            tryAcquireLeadership: _ =>
            {
                acquireAttempts++;
                return new TestLease();
            },
            ownExtractorVersion: () => "3.0.0",
            clock: () => now,
            processAliveProbe: _ => requesterAlive);

        coordinator.BeginCooldown(requesterPid: 9999);

        Assert.False(coordinator.TryClaim("/repo/.miller", null).Claimed);
        Assert.Equal(0, acquireAttempts);

        requesterAlive = false;
        Assert.True(coordinator.TryClaim("/repo/.miller", null).Claimed);
        Assert.Equal(1, acquireAttempts);
    }

    [Fact]
    public void MaybeRequestYield_NewerThanLiveLeader_EnqueuesOnceWithinTtl()
    {
        var requests = new List<(string MillerDir, string WorkspaceId, int Pid, string Version)>();
        DateTimeOffset now = new(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);
        var coordinator = NewCoordinator(
            ownExtractorVersion: () => "3.0.0",
            requestYield: (millerDir, workspaceId, pid, version) =>
                requests.Add((millerDir, workspaceId, pid, version)),
            readLeaderIdentity: _ => LiveLeader(4242, "2.0.0"),
            leaderAliveProbe: _ => true,
            clock: () => now);
        LeadershipVerdict verdict = coordinator.EvaluateEligibility(extractDbPath: null);

        coordinator.MaybeRequestYield("/repo/.miller", "ws-1", verdict);
        now += TimeSpan.FromSeconds(5);
        coordinator.MaybeRequestYield("/repo/.miller", "ws-1", verdict);

        var request = Assert.Single(requests);
        Assert.Equal("/repo/.miller", request.MillerDir);
        Assert.Equal("ws-1", request.WorkspaceId);
        Assert.Equal(Environment.ProcessId, request.Pid);
        Assert.Equal("3.0.0", request.Version);
    }

    [Fact]
    public void EvaluateYieldRequests_StrictlyNewerRequester_ReturnsDecision()
    {
        var requesterObservedAtUtc = new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);
        var coordinator = NewCoordinator(
            ownExtractorVersion: () => "2.0.0",
            drainYieldRequests: _ => new YieldDrainResult(true, "3.0.0", 9999, requesterObservedAtUtc, 0, 0));

        IndexerLeadershipYieldDecision? decision = coordinator.EvaluateYieldRequests(
            "/repo/.miller",
            logRequestDrainStats: static (_, _, _) => { });

        Assert.NotNull(decision);
        Assert.Equal(9999, decision!.Value.RequesterPid);
        Assert.Equal("3.0.0", decision.Value.RequesterVersion);
        Assert.Equal(requesterObservedAtUtc, decision.Value.RequesterObservedAtUtc);
    }

    [Fact]
    public void EvaluateYieldRequests_EqualVersionRequester_ReturnsNull()
    {
        var coordinator = NewCoordinator(
            ownExtractorVersion: () => "2.0.0",
            drainYieldRequests: _ => new YieldDrainResult(true, "2.0.0", 9999, 0, 0));

        IndexerLeadershipYieldDecision? decision = coordinator.EvaluateYieldRequests(
            "/repo/.miller",
            logRequestDrainStats: static (_, _, _) => { });

        Assert.Null(decision);
    }

    [Fact]
    public void EvaluateEligibility_UnreadableStoreVersionRefusesLeadership()
    {
        var coordinator = NewCoordinator(
            readArtifactExtractorVersion: _ => throw new StoreArtifactVersionReadException(
                "The active family-store version is unreadable; refusing to claim leadership."));

        LeadershipVerdict verdict = coordinator.EvaluateEligibility("/repo/.miller/symbols.db");

        Assert.False(verdict.Eligible);
        Assert.Contains("refusing to claim leadership", verdict.Reason, StringComparison.Ordinal);
    }

    private static IndexerLeadershipCoordinator NewCoordinator(
        Func<string, IDisposable?>? tryAcquireLeadership = null,
        Func<string, YieldDrainResult>? drainYieldRequests = null,
        Func<string, LeaderHandoffDrainResult>? drainLeaderHandoffRequests = null,
        Func<string?>? ownExtractorVersion = null,
        bool allowExtractorDowngrade = false,
        Func<string?, string?>? readArtifactExtractorVersion = null,
        Action<string, string, int, string>? requestYield = null,
        Func<string, LeaderIdentity?>? readLeaderIdentity = null,
        Func<LeaderIdentity, bool>? leaderAliveProbe = null,
        Func<DateTimeOffset>? clock = null,
        Func<int, bool>? processAliveProbe = null,
        ILogger? logger = null) =>
        new(
            logger ?? NullLogger.Instance,
            tryAcquireLeadership ?? (_ => null),
            ownExtractorVersion ?? (() => "3.0.0"),
            allowExtractorDowngrade,
            readArtifactExtractorVersion ?? (_ => null),
            drainYieldRequests ?? (_ => YieldDrainResult.Empty),
            drainLeaderHandoffRequests ?? (_ => LeaderHandoffDrainResult.Empty),
            requestYield ?? ((_, _, _, _) => { }),
            readLeaderIdentity ?? (_ => null),
            leaderAliveProbe ?? (_ => false),
            clock ?? (() => DateTimeOffset.UtcNow),
            processAliveProbe is null ? (_, _) => false : (pid, _) => processAliveProbe(pid));

    private static LeaderIdentity LiveLeader(int pid, string? extractorVersion) => new(
        pid, "0.3.6", null, new DateTimeOffset(2026, 6, 11, 9, 0, 0, TimeSpan.Zero), extractorVersion);
}
