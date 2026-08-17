using Microsoft.Extensions.Logging;
using Miller.Indexing;
using Miller.Indexing.Store;
using Miller.Server.Workspaces;

namespace Miller.Server.Hosting;

internal readonly record struct IndexerLeadershipClaimResult(
    bool Claimed,
    IDisposable? Lease,
    LeadershipVerdict Verdict);

internal readonly record struct IndexerLeadershipYieldDecision(
    int RequesterPid,
    string RequesterVersion,
    DateTimeOffset RequesterObservedAtUtc);

internal readonly record struct IndexerLeadershipHandoffDecision(
    int RequesterPid,
    DateTimeOffset RequesterObservedAtUtc);

internal sealed class IndexerLeadershipCoordinator
{
    private readonly ILogger _logger;
    private readonly Func<string, IDisposable?> _tryAcquireLeadership;
    private readonly Lazy<string?> _ownExtractorVersion;
    private readonly bool _allowExtractorDowngrade;
    private readonly Func<string?, string?> _readArtifactExtractorVersion;
    private readonly Func<string, YieldDrainResult> _drainYieldRequests;
    private readonly Func<string, LeaderHandoffDrainResult> _drainLeaderHandoffRequests;
    private readonly Action<string, string, int, string> _requestYield;
    private readonly Func<string, LeaderIdentity?> _readLeaderIdentity;
    private readonly Func<LeaderIdentity, bool> _leaderAliveProbe;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<int, DateTimeOffset?, bool> _processAliveProbe;
    private readonly YieldCooldown _cooldown;

    private (int LeaderPid, DateTimeOffset LeaderStartedAtUtc, DateTimeOffset SentAtUtc)? _outstandingYield;
    private string? _lastVerdictReasonLogged;

    public IndexerLeadershipCoordinator(
        ILogger logger,
        Func<string, IDisposable?> tryAcquireLeadership,
        Func<string?> ownExtractorVersion,
        bool allowExtractorDowngrade,
        Func<string?, string?> readArtifactExtractorVersion,
        Func<string, YieldDrainResult> drainYieldRequests,
        Func<string, LeaderHandoffDrainResult> drainLeaderHandoffRequests,
        Action<string, string, int, string> requestYield,
        Func<string, LeaderIdentity?> readLeaderIdentity,
        Func<LeaderIdentity, bool> leaderAliveProbe,
        Func<DateTimeOffset> clock,
        Func<int, DateTimeOffset?, bool> processAliveProbe)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(tryAcquireLeadership);
        ArgumentNullException.ThrowIfNull(ownExtractorVersion);
        ArgumentNullException.ThrowIfNull(readArtifactExtractorVersion);
        ArgumentNullException.ThrowIfNull(drainYieldRequests);
        ArgumentNullException.ThrowIfNull(drainLeaderHandoffRequests);
        ArgumentNullException.ThrowIfNull(requestYield);
        ArgumentNullException.ThrowIfNull(readLeaderIdentity);
        ArgumentNullException.ThrowIfNull(leaderAliveProbe);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(processAliveProbe);

        _logger = logger;
        _tryAcquireLeadership = tryAcquireLeadership;
        _ownExtractorVersion = new Lazy<string?>(ownExtractorVersion);
        _allowExtractorDowngrade = allowExtractorDowngrade;
        _readArtifactExtractorVersion = readArtifactExtractorVersion;
        _drainYieldRequests = drainYieldRequests;
        _drainLeaderHandoffRequests = drainLeaderHandoffRequests;
        _requestYield = requestYield;
        _readLeaderIdentity = readLeaderIdentity;
        _leaderAliveProbe = leaderAliveProbe;
        _clock = clock;
        _processAliveProbe = processAliveProbe;
        _cooldown = new YieldCooldown(_clock, processAliveProbe);
    }

    public LeadershipVerdict? EligibilityVerdict { get; private set; }

    public string? OwnExtractorVersion =>
        _ownExtractorVersion.IsValueCreated ? _ownExtractorVersion.Value : null;

    public string? ProbeOwnExtractorVersion() => _ownExtractorVersion.Value;

    /// <summary>
    /// One claim attempt, gated by the D2 eligibility verdict and the D4 post-yield cooldown. The acquire func
    /// is invoked only when both gates pass.
    /// </summary>
    public IndexerLeadershipClaimResult TryClaim(string millerDir, string? extractDbPath)
    {
        LeadershipVerdict verdict = EvaluateEligibility(extractDbPath);
        if (!verdict.Eligible)
        {
            if (_lastVerdictReasonLogged != verdict.Reason)
            {
                _lastVerdictReasonLogged = verdict.Reason;
                _logger.LogInformation("Not claiming indexer leadership: {Reason}.", verdict.Reason);
            }
            else
            {
                _logger.LogDebug("Still ineligible for indexer leadership: {Reason}.", verdict.Reason);
            }
            return new IndexerLeadershipClaimResult(false, null, verdict);
        }

        _lastVerdictReasonLogged = null; // a NEW ineligibility period after this re-announces at Information
        if (_cooldown.SuppressesClaim())
        {
            _logger.LogDebug(
                "Suppressing a leadership claim during the post-yield cooldown (the newer instance should win the re-race).");
            return new IndexerLeadershipClaimResult(false, null, verdict);
        }

        IDisposable? lease = _tryAcquireLeadership(millerDir);
        return new IndexerLeadershipClaimResult(lease is not null, lease, verdict);
    }

    /// <summary>Evaluate the D2 verdict and publish it for status/health rendering.</summary>
    public LeadershipVerdict EvaluateEligibility(string? extractDbPath)
    {
        LeadershipVerdict verdict;
        try
        {
            // Production wires ReadForEligibility / ReadForLeadership as this reader so
            // display and Evaluate name one token. Tests inject the artifact version.
            string? artifactBinaryVersion = _readArtifactExtractorVersion(extractDbPath);
            verdict = LeadershipEligibility.Evaluate(
                _ownExtractorVersion.Value,
                artifactBinaryVersion,
                _allowExtractorDowngrade);
        }
        catch (StoreArtifactVersionReadException ex)
        {
            verdict = new LeadershipVerdict(false, false, ex.Message);
        }

        EligibilityVerdict = verdict;
        return verdict;
    }

    /// <summary>
    /// D4 requester side: if this instance is eligible and a live leader's extractor is older, write at most one
    /// outstanding yield request per observed leader until the request TTL elapses or the leader identity changes.
    /// </summary>
    public void MaybeRequestYield(string millerDir, string? workspaceId, LeadershipVerdict verdict)
    {
        if (!verdict.Eligible || workspaceId is null)
            return; // an ineligible challenger dethroning a working leader could freeze the index
        if (_ownExtractorVersion.Value is not { } ownVersion)
            return;
        if (_readLeaderIdentity(millerDir) is not { ExtractorVersion: { } leaderVersion } leader)
            return; // no identity, or a pre-feature leader (D5): it could not drain a yield request anyway
        if (!_leaderAliveProbe(leader))
            return; // stale identity from a crash — the normal lock retry wins the lease instead

        int comparison;
        try
        {
            comparison = LeadershipEligibility.CompareVersions(ownVersion, leaderVersion);
        }
        catch (ArgumentException)
        {
            return; // unparseable recorded version: cannot prove superiority, so do not challenge
        }
        if (comparison <= 0)
            return; // equal versions never yield (D4): same-version swarms must not thrash leadership

        DateTimeOffset now = _clock();
        if (_outstandingYield is { } outstanding
            && outstanding.LeaderPid == leader.Pid
            && outstanding.LeaderStartedAtUtc == leader.StartedAtUtc
            && now - outstanding.SentAtUtc < LeaderScanRequestQueue.RequestTtl)
        {
            return; // one outstanding request per leader; re-enqueue only after TTL or leader change
        }

        try
        {
            _requestYield(millerDir, workspaceId, Environment.ProcessId, ownVersion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogWarning(ex, "Could not write a leadership yield request; will retry on a later tick.");
            return; // not recorded as outstanding, so the next retry tick tries again
        }

        _outstandingYield = (leader.Pid, leader.StartedAtUtc, now);
        _logger.LogInformation(
            "Requested leadership yield: own extractor {OwnVersion} is newer than leader pid {LeaderPid}'s {LeaderVersion}.",
            ownVersion, leader.Pid, leaderVersion);
    }

    /// <summary>
    /// D4 leader side: drain pending yield requests and decide whether the strongest challenger justifies
    /// abdication. Strictly greater than own wins; equal or lower is ignored.
    /// </summary>
    public IndexerLeadershipYieldDecision? EvaluateYieldRequests(
        string millerDir,
        Action<string, int, int> logRequestDrainStats)
    {
        ArgumentNullException.ThrowIfNull(logRequestDrainStats);

        YieldDrainResult result;
        try
        {
            result = _drainYieldRequests(millerDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogWarning(ex, "Leader yield request drain failed; will retry on a later tick.");
            return null;
        }

        logRequestDrainStats("yield", result.ExpiredDiscarded, result.ClaimSkipped);
        if (!result.Requested || result.MaxRequesterVersion is not { } requesterVersion)
            return null;

        if (_ownExtractorVersion.Value is not { } ownVersion)
        {
            // Only reachable when leading with an unprobeable binary under the explicit downgrade override:
            // the operator forced this instance to index, and a challenger cannot prove it is newer than unknown.
            _logger.LogDebug(
                "Ignoring a yield request from pid {RequesterPid} (extractor {RequesterVersion}): own extractor version is unknown.",
                result.RequesterPid, requesterVersion);
            return null;
        }

        int comparison;
        try
        {
            comparison = LeadershipEligibility.CompareVersions(requesterVersion, ownVersion);
        }
        catch (ArgumentException)
        {
            return null; // the drain already filters unparseable versions; defensive
        }

        if (comparison <= 0)
        {
            _logger.LogDebug(
                "Ignoring a yield request from pid {RequesterPid}: requester extractor {RequesterVersion} is not newer than own {OwnVersion}.",
                result.RequesterPid, requesterVersion, ownVersion);
            return null;
        }

        return new IndexerLeadershipYieldDecision(
            result.RequesterPid,
            requesterVersion,
            result.RequesterObservedAtUtc ?? _clock());
    }

    public IndexerLeadershipHandoffDecision? EvaluateLeaderHandoffRequests(
        string millerDir,
        Action<string, int, int> logRequestDrainStats)
    {
        ArgumentNullException.ThrowIfNull(logRequestDrainStats);

        LeaderHandoffDrainResult result;
        try
        {
            result = _drainLeaderHandoffRequests(millerDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogWarning(ex, "Leader handoff request drain failed; will retry on a later tick.");
            return null;
        }

        logRequestDrainStats("leader_handoff", result.ExpiredDiscarded, result.ClaimSkipped);
        if (!result.Requested || result.RequesterPid <= 0)
            return null;

        DateTimeOffset observedAtUtc = result.RequesterObservedAtUtc ?? _clock();
        if (!_processAliveProbe(result.RequesterPid, observedAtUtc))
        {
            _logger.LogDebug(
                "Ignoring explicit leader handoff request from pid {RequesterPid}: requester is not running.",
                result.RequesterPid);
            return null;
        }

        return new IndexerLeadershipHandoffDecision(result.RequesterPid, observedAtUtc);
    }

    public void BeginCooldown(int requesterPid, DateTimeOffset requesterObservedAtUtc) =>
        _cooldown.Begin(requesterPid, requesterObservedAtUtc);

    public void BeginCooldown(int requesterPid) => _cooldown.Begin(requesterPid);
}
