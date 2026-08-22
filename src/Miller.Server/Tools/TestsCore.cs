using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Indexing.Testing;
using Miller.Server;
using Miller.Testing;

namespace Miller.Server.Tools;

/// <summary>
/// Seams for the CT verbs. <c>Budget</c> overrides the user-global execution budget a foreground
/// run takes, the same way <see cref="ContinuousTestDaemonHostOptions.Budget"/> overrides the
/// daemon's: null resolves from the environment, so production behavior is unchanged, while a test
/// binds its own miller home instead of contending on the caller's real one.
/// <para>
/// <c>OpenFacts</c> and <c>Providers</c> are the two seams a foreground drain needs when there is no
/// live index and no test toolchain. <c>OpenFacts</c> replaces <c>OpenLiveFacts</c>; it is called
/// with the workspace root and the workspace id, ONCE PER READ, and each source it returns is
/// disposed as soon as that read ends - which is the property that keeps a minutes-long suite from
/// pinning the served generation. <c>Providers</c> replaces the default five-provider factory, so a
/// drain can be observed without spawning <c>dotnet test</c>. Both are null in production.
/// </para>
/// <para>
/// <c>SubmitRun</c> replaces <see cref="CtDaemonRouting.SubmitRun"/>; it is called with the endpoint
/// root and the target workspace root. It exists because the three answers the channel gives - an
/// acknowledgement, a dead endpoint, and a five-second ack timeout - each pick a different branch,
/// and a test must reach the timeout branch without waiting five seconds for it. Null in production.
/// </para>
/// </summary>
public sealed record TestsCoreHooks(
    Func<ProcessStartInfo, Process?>? StartProcess = null,
    Func<TestsForegroundRunRequest, TestsRunOutcome>? ForegroundRun = null,
    CtExecutionBudget? Budget = null,
    Func<string, string, IMillerFactSource>? OpenFacts = null,
    IContinuousTestProviderResolver? Providers = null,
    Func<string, string, CtRunResult>? SubmitRun = null)
{
    internal TestsWaitProbe? WaitProbe { get; init; }

    internal CtDaemonPublicationProbe? PublicationProbe { get; init; }
}

internal sealed record TestsWaitProbe(
    Func<string, ContinuousTestDaemonSnapshot>? ReadStatus = null,
    Func<string, bool>? IsLeaseLive = null,
    TimeProvider? Clock = null,
    Action<TimeSpan>? Delay = null);

public sealed record TestsForegroundRunRequest(
    string WorkspaceRoot,
    string WorkspaceId,
    IReadOnlyList<ContinuousTestProject> Projects,
    bool Wait);

public sealed record TestsRunOutcome(
    CtRunExecution Execution,
    ContinuousTestVerdict Verdict,
    string? Reason,
    bool Waited,
    bool Paused = false);

public sealed record TestsCoreRequest(
    string WorkspaceRoot,
    string? WorkspaceId = null,
    string? MillerHome = null,
    string? KillSwitch = null,
    string? MillerVersion = null,
    TestsCoreHooks? Hooks = null,
    bool Json = false,
    bool Wait = false,
    string? ProjectPath = null,
    TimeSpan? WaitTimeout = null);

public sealed record TestsStatusProject(
    string Id,
    string ProjectPath,
    string? Framework,
    string? Command,
    bool Enabled,
    IReadOnlyList<string> ExcludeTraits);

public sealed record TestsBudgetHolder(int Pid, string WorkspaceRoot, string Reason);

public sealed record TestsStatusResult(
    bool Enabled,
    bool KillSwitchOff,
    IReadOnlyList<TestsStatusProject> Projects,
    CtDaemonLifecycleState DaemonState,
    string DaemonReason,
    ContinuousTestVerdict Verdict,
    CtFreshnessKey? Selected,
    int StaleCount,
    int SelectedCount,
    string? LastRun,
    TestsBudgetHolder? BudgetHolder,
    CtDaemonActivity DaemonActivity = CtDaemonActivity.Idle,
    CtDaemonRunProgress? DaemonRun = null,

    /// <summary>
    /// Which build the LIVE daemon runs, against this one. Null only under the kill switch, which
    /// guarantees there is no daemon to compare.
    /// </summary>
    CtDaemonVersionVerdict? DaemonVersion = null,

    /// <summary>
    /// Whether the live daemon's MAIN LOOP is still turning. Null only under the kill switch. On an
    /// adopted worktree this judges the FAMILY daemon's record, because a worktree has no periodic
    /// record of its own.
    /// </summary>
    CtLoopHealthVerdict? DaemonLoop = null)
{
    public string Render(bool json) => json ? TestsCore.RenderStatusJson(this) : TestsCore.RenderStatusCompact(this);
}

public sealed record TestsMutationResult(
    int ExitCode,
    string Operation,
    int EnabledCount,
    IReadOnlyList<TestsStatusProject> Projects,
    string? Error,
    // ChangedProjects: the projects whose enabled state THIS call flipped — turned on by an enable,
    // turned off by a disable. EnabledCount and Projects report the enabled set left AFTER the call,
    // which is not what a disable did: reporting only that is how a disable of 1 of 3 projects
    // printed the other 2 under a "disable" heading. Null means none.
    IReadOnlyList<TestsStatusProject>? ChangedProjects = null)
{
    public string Render(bool json) => json ? TestsCore.RenderMutationJson(this) : TestsCore.RenderMutationCompact(this);
}

public sealed record TestsServeResult(
    int ExitCode,
    string Status,
    string? Reason,
    int? ProcessId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    CtDaemonPublicationResult? Publication = null)
{
    public string Render(bool json) => json ? TestsCore.RenderServeJson(this) : TestsCore.RenderServeCompact(this);
}

public sealed record TestsStopResult(int ExitCode, string Status, string? Reason)
{
    public string Render(bool json) => json ? TestsCore.RenderStopJson(this) : TestsCore.RenderStopCompact(this);
}

public sealed record TestsRunResult(
    int ExitCode,
    CtRunExecution Execution,
    ContinuousTestVerdict Verdict,
    string? Reason,
    bool Waited,
    CtFreshnessKey? Selected,
    bool Paused = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    TestsWaitResult? Wait = null)
{
    public string Render(bool json) => json ? TestsCore.RenderRunJson(this) : TestsCore.RenderRunCompact(this);
}

public enum TestsWaitState
{
    Completed,
    QueuedTimeout,
    NotPickedUp,
    WaitTimeout,
    DaemonStopped,
    LeaseLost,
}

public sealed record TestsWaitResult(
    bool WaitComplete,
    TestsWaitState State,
    double ElapsedSeconds,
    double TimeoutSeconds,
    string CommandId,
    string? RunId);

/// <summary>
/// <paramref name="Truncated"/> is how many red cases are left AFTER this page. <paramref name="Total"/> and
/// <paramref name="Offset"/> are what make the page navigable: without them a caller who sees "truncated: 340"
/// cannot ask for the next page, which is how this surface used to strand a reader after five rows.
/// </summary>
public sealed record TestsFailuresResult(
    IReadOnlyList<ContinuousTestStatus> Failures,
    int Truncated,
    int Total = 0,
    int Offset = 0)
{
    public string Render(bool json) => json ? TestsCore.RenderFailuresJson(this) : TestsCore.RenderFailuresCompact(this);
}

/// <summary>
/// Shared CT verb core for the CLI and the MCP <c>tests</c> tool. Status reads never create
/// <c>ct.db</c> or a daemon. Start is explicit.
/// </summary>
public static class TestsCore
{
    public const int JsonSchemaVersion = 1;
    public const string StatusContractName = "tests_status";
    public const string StatusContractDoc = "docs/contracts/tests-cli-v1.md";
    private const int MaxDaemonActivityNames = 8;
    private const string TestsUsage =
        "miller tests <status|failures|serve|run|enable|disable|stop> [--json] [--wait] [--limit N] [--offset N] "
        + "[--project PATH] [--workspace-id SELECTOR] [--workspace DIR]";

    /// <summary>Rows one <c>failures</c> page returns when the caller names no limit.</summary>
    public const int FailuresDefaultLimit = 20;

    /// <summary>
    /// The most rows one <c>failures</c> page returns. A ceiling still exists so a single call cannot dump a
    /// thousand rows into an agent's context, but it is now a page size rather than the end of the list:
    /// <c>offset</c> reaches everything past it.
    /// </summary>
    public const int FailuresMaxLimit = 200;

    // Same request reason the daemon records, so `budget_holder.reason` reads the same whether the
    // suite runs in a daemon or in the foreground.
    private const string ExecutionBudgetReason = "run";

    // Same wording the daemon publishes when it pauses on a held budget. The paused vocabulary is
    // shared on purpose: one held lease, one explanation, whichever path reports it.
    private const string ExecutionBudgetHeldReason = "execution budget held";

    /// <summary>How often <c>--wait</c> re-reads the daemon status.</summary>
    private static readonly TimeSpan WaitPollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// How long <c>--wait</c> lets an accepted run stay unstarted before it believes an idle reading. The
    /// daemon publishes its status once per poll (250 ms by default) and a run command is acknowledged before
    /// the work becomes ready, so a wait that trusted the first reading would return before anything ran.
    /// </summary>
    private static readonly TimeSpan RunPickupGrace = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How long <c>--wait</c> stays blocked on another workspace holding the single execution slot. The
    /// holder may keep it for as long as its own suite takes, so the caller gets an honest "still queued"
    /// instead of the full wait timeout.
    /// </summary>
    private static readonly TimeSpan QueuedWaitLimit = TimeSpan.FromSeconds(30);

    /// <summary>How often <c>--wait</c> checks that the daemon it is waiting on is still alive.</summary>
    private static readonly TimeSpan LivenessProbeInterval = TimeSpan.FromSeconds(2);

    public static string Usage => TestsUsage;

    public static TestsStatusResult Status(TestsCoreRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string root = RequireRoot(request);

        // MILLER_CT=off is a zero-WORK guarantee, not merely zero-creation: no ct.db open, no
        // live-index read, no daemon-status read, no budget read. The earlier shape did the reads
        // and masked the rendered fields, which honored zero-creation but not zero-work. The
        // payload is the never-enabled workspace shape with the kill switch reported.
        if (ContinuousTestPolicy.IsKillSwitchOff(request.KillSwitch))
        {
            return new TestsStatusResult(
                Enabled: false,
                KillSwitchOff: true,
                Projects: [],
                DaemonState: CtDaemonLifecycleState.Stopped,
                DaemonReason: "disabled",
                Verdict: ContinuousTestVerdict.Unknown,
                Selected: null,
                StaleCount: 0,
                SelectedCount: 0,
                LastRun: null,
                BudgetHolder: null);
        }

        string workspaceId = ResolveWorkspaceId(request, root);
        bool optedIn = ContinuousTestPolicy.IsWorkspaceOptedIn(root);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(root));
        IReadOnlyList<ContinuousTestProject> stored = store.ListContinuousTestProjects(workspaceId, includeDisabled: false);
        IReadOnlyList<ContinuousTestStatus> statuses = store.ListContinuousTestStatuses(workspaceId);
        // Probed, not merely published: a daemon that died without a clean shutdown leaves its last
        // "running" record on disk, and reporting it would tell the reader CT watches the tree when
        // nothing does. The wait loop keeps the cheap unprobed read and probes on its own clock.
        ContinuousTestDaemonSnapshot snapshot = ContinuousTestDaemonHost.ReadLiveStatus(root);
        // The selected key comes from the LIVE index cursor, never from the stored rows. A key
        // derived from the rows it judges reads uniformly stale rows as green forever, and flips
        // between consecutive reads when rows carry mixed keys (observed live 2026-08-20).
        CtFreshnessKey? liveKey = TryReadLiveFreshness(request, root, workspaceId);
        ContinuousTestProjectedStatus projected = ContinuousTestStatusProjection.Project(
            liveKey,
            statuses,
            liveKey is { } live
                ? store.ListContinuousTestFreshWatermarks(workspaceId, live.IndexIdentity)
                : null);
        TestsBudgetHolder? budget = ReadBudgetHolder(request.MillerHome);
        // The build the daemon runs lives in its LEASE, not its status record, and an adopted
        // worktree's lease lives on the repo's main checkout — so the endpoint resolver is the right
        // read, and it creates nothing. Without this the status was silent about a daemon still
        // running the code you replaced. The loop-health read takes the same route, for the same
        // reason: one resolve, two facts about the same live daemon.
        CtDaemonEndpoint? endpoint = CtDaemonRouting.ResolveLiveEndpoint(root);
        CtDaemonVersionVerdict version = CtDaemonVersion.ForLease(
            request.MillerVersion ?? MillerVersion.Current,
            endpoint?.Lease);
        CtLoopHealthVerdict loop = ResolveLoopHealth(root, endpoint, snapshot);
        return new TestsStatusResult(
            Enabled: optedIn || stored.Count > 0,
            KillSwitchOff: false,
            Projects: stored.Select(ToStatusProject).ToArray(),
            DaemonState: snapshot.State,
            DaemonReason: snapshot.Reason,
            Verdict: projected.Verdict,
            Selected: projected.SelectedKey,
            StaleCount: projected.StaleCount,
            SelectedCount: statuses.Count,
            LastRun: store.LatestTestRunAt(workspaceId),
            BudgetHolder: budget,
            DaemonActivity: snapshot.Activity,
            DaemonRun: snapshot.Run,
            DaemonVersion: version,
            DaemonLoop: loop);
    }

    /// <summary>
    /// Whether the daemon that serves <paramref name="root"/> is still turning its main loop.
    ///
    /// <para>An adopted worktree has no periodic record of its own — the family daemon writes that
    /// worktree's <c>daemon.status.json</c> on TRANSITIONS only, so its timestamps stand still while a
    /// perfectly healthy daemon serves it. Judging that record would report every adopted worktree as
    /// wedged. The live endpoint is the daemon that actually runs the loop, so its record is the one to
    /// read; when the endpoint is this root, the snapshot already carries the verdict and no second file
    /// read is needed.</para>
    /// </summary>
    private static CtLoopHealthVerdict ResolveLoopHealth(
        string root,
        CtDaemonEndpoint? endpoint,
        ContinuousTestDaemonSnapshot snapshot)
    {
        if (endpoint is null)
            return CtDaemonLoopHealth.Unknown("no live daemon");
        if (PathsEqual(endpoint.EndpointRoot, root))
            return snapshot.LoopHealth ?? CtDaemonLoopHealth.Unknown("no status record");

        return CtDaemonLoopHealth.Evaluate(CtDaemonLease.TryReadStatus(endpoint.EndpointRoot));
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    /// <summary>
    /// One page of the red cases, ordered by test-case id so paging is stable between calls.
    ///
    /// <para>The page used to be five rows with a hard ceiling of twenty and no way to ask for the rest. A
    /// suite with hundreds of failures reported five of them and the count of the ones it would not show,
    /// which is not enough to act on. <paramref name="offset"/> pages through the rest.</para>
    /// </summary>
    public static TestsFailuresResult Failures(
        TestsCoreRequest request,
        int maxItems = FailuresDefaultLimit,
        int offset = 0)
    {
        ArgumentNullException.ThrowIfNull(request);
        string root = RequireRoot(request);
        string workspaceId = ResolveWorkspaceId(request, root);
        int limit = Math.Clamp(maxItems, 1, FailuresMaxLimit);
        int skip = Math.Max(0, offset);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(root));
        ContinuousTestStatus[] red = store.ListContinuousTestStatuses(workspaceId)
            .Where(row => row.State == ContinuousTestState.Red)
            .OrderBy(row => row.TestCaseId, StringComparer.Ordinal)
            .ToArray();
        ContinuousTestStatus[] page = red.Skip(skip).Take(limit).ToArray();
        return new TestsFailuresResult(
            page,
            Math.Max(0, red.Length - skip - page.Length),
            red.Length,
            skip);
    }

    public static TestsMutationResult Enable(TestsCoreRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string root = RequireRoot(request);
        if (ContinuousTestPolicy.IsKillSwitchOff(request.KillSwitch))
            return MutationError("enable", "continuous testing is disabled (MILLER_CT=off)");

        string workspaceId = ResolveWorkspaceId(request, root);
        IReadOnlyList<ContinuousTestProject> discovered;
        if (!string.IsNullOrWhiteSpace(request.ProjectPath))
        {
            if (!TryResolveProject(root, request.ProjectPath, out string full, out string? error))
                return MutationError("enable", error!);
            ContinuousTestProject? identified = ContinuousTestProjectInventory.Identify(root, workspaceId, full);
            if (identified is null)
                return MutationError("enable", $"test project not found: {full}");
            discovered = [identified];
        }
        else
        {
            discovered = ContinuousTestProjectInventory.Discover(root, workspaceId);
        }

        string marker = ContinuousTestPolicy.EnabledMarkerPath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.WriteAllText(marker, string.Empty);
        // An earlier `tests disable` may have left the opt-out tombstone, and the tombstone beats
        // the marker just written. Enable must reverse the opt-out or it silently does nothing.
        string tombstone = ContinuousTestPolicy.DisabledMarkerPath(root);
        if (File.Exists(tombstone))
            File.Delete(tombstone);

        using var store = new ContinuousTestStore(CtSchema.DbPathFor(root));
        HashSet<string> enabledBefore = store.ListContinuousTestProjects(workspaceId)
            .Select(static project => project.ProjectPath)
            .ToHashSet(StringComparer.Ordinal);
        store.Transaction(() =>
        {
            foreach (ContinuousTestProject project in discovered)
                store.PutContinuousTestProject(project with { Enabled = true });
        });

        IReadOnlyList<ContinuousTestProject> enabled = store.ListContinuousTestProjects(workspaceId);
        TestsStatusProject[] turnedOn = enabled
            .Where(project => !enabledBefore.Contains(project.ProjectPath))
            .Select(ToStatusProject)
            .ToArray();
        return new TestsMutationResult(
            0,
            "enable",
            enabled.Count,
            enabled.Select(ToStatusProject).ToArray(),
            null,
            turnedOn);
    }

    public static TestsMutationResult Disable(TestsCoreRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string root = RequireRoot(request);
        // MILLER_CT=off is a permanent ZERO-WORK guarantee, and it binds this verb like every other one.
        // Opening the store CREATES ct.db when none exists, and the loop below writes rows into it, so a
        // disable request under the kill switch used to create and modify the very file the switch promises
        // Miller never touches. Nothing is lost by refusing: the switch already disables continuous testing
        // everywhere, so the caller's intent is satisfied before this method runs.
        if (ContinuousTestPolicy.IsKillSwitchOff(request.KillSwitch))
            return MutationError("disable", "continuous testing is disabled (MILLER_CT=off)");

        string workspaceId = ResolveWorkspaceId(request, root);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(root));
        IReadOnlyList<ContinuousTestProject> enabledBefore = store.ListContinuousTestProjects(workspaceId);
        if (!string.IsNullOrWhiteSpace(request.ProjectPath))
        {
            if (!TryResolveProject(root, request.ProjectPath, out string full, out string? error))
                return MutationError("disable", error!);
            store.SetContinuousTestProjectEnabled(workspaceId, full, enabled: false);
        }
        else
        {
            foreach (ContinuousTestProject project in store.ListContinuousTestProjects(workspaceId, includeDisabled: true))
                store.SetContinuousTestProjectEnabled(workspaceId, project.ProjectPath, enabled: false);
        }

        IReadOnlyList<ContinuousTestProject> remaining = store.ListContinuousTestProjects(workspaceId);
        if (remaining.Count == 0)
        {
            string marker = ContinuousTestPolicy.EnabledMarkerPath(root);
            if (File.Exists(marker))
                File.Delete(marker);
            // Deleting the local marker is not enough on a linked worktree: the main checkout's
            // marker re-enables it on the next probe. The tombstone makes THIS root's opt-out
            // stick, and never touches the main checkout. Written only on a full disable — a
            // project-scoped disable that leaves rows enabled keeps the workspace opted in.
            string tombstone = ContinuousTestPolicy.DisabledMarkerPath(root);
            Directory.CreateDirectory(Path.GetDirectoryName(tombstone)!);
            File.WriteAllText(tombstone, string.Empty);
        }

        // Report what the call DID, not only what survived it. The disabled set is the enabled rows
        // that this call left un-enabled; their rows now read disabled, so the rendered projects say so.
        HashSet<string> stillEnabled = remaining
            .Select(static project => project.ProjectPath)
            .ToHashSet(StringComparer.Ordinal);
        TestsStatusProject[] turnedOff = enabledBefore
            .Where(project => !stillEnabled.Contains(project.ProjectPath))
            .Select(static project => ToStatusProject(project with { Enabled = false }))
            .ToArray();
        return new TestsMutationResult(
            0,
            "disable",
            remaining.Count,
            remaining.Select(ToStatusProject).ToArray(),
            null,
            turnedOff);
    }

    public static TestsServeResult Start(TestsCoreRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string root = RequireRoot(request);
        if (ContinuousTestPolicy.IsKillSwitchOff(request.KillSwitch))
            return new TestsServeResult(3, "refused", "continuous testing is disabled (MILLER_CT=off)", null);
        if (!ContinuousTestPolicy.IsWorkspaceOptedIn(root))
            return new TestsServeResult(3, "refused", "not enabled; run miller tests enable first", null);

        // A worktree start anchors the FAMILY daemon at the repo's main checkout: one daemon per
        // repo adopts every family worktree, so a worktree must not mint a sibling-blind second one.
        string anchor = CtDaemonLauncher.ResolveSpawnRoot(root);
        // Passing this build turns on the version check: a live daemon running code this binary
        // replaced is stopped and started again here, rather than answering exit 0 and leaving the
        // old daemon watching the tree. A replace is a success, so it takes the same exit code.
        CtDaemonSpawnResult spawned = CtDaemonLauncher.SpawnDetached(
            anchor,
            request.Hooks?.StartProcess,
            request.MillerVersion ?? MillerVersion.Current,
            publication: request.Hooks?.PublicationProbe);
        int exit = spawned.Status is CtDaemonSpawnStatus.Started
            or CtDaemonSpawnStatus.AlreadyRunning
            or CtDaemonSpawnStatus.Replaced ? 0 : 3;
        string? reason = spawned.Reason;
        if (!string.Equals(anchor, root, StringComparison.Ordinal))
        {
            reason = reason is null
                ? $"family daemon at {anchor}"
                : $"{reason} (family daemon at {anchor})";
        }

        return new TestsServeResult(
            exit,
            spawned.Status.ToString().ToLowerInvariant(),
            reason,
            spawned.ProcessId,
            spawned.Publication);
    }

    public static TestsServeResult ServeHost(TestsCoreRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string root = RequireRoot(request);
        string workspaceId = ResolveWorkspaceId(request, root);
        if (ContinuousTestPolicy.IsKillSwitchOff(request.KillSwitch)
            || !ContinuousTestPolicy.IsWorkspaceOptedIn(root))
        {
            ContinuousTestDaemonSnapshot disabled = ContinuousTestDaemonHost.RunAsync(
                root,
                new ContinuousTestDaemonHostOptions
                {
                    KillSwitch = request.KillSwitch,
                    Enabled = false,
                    AcquireLease = false,
                    MillerVersion = request.MillerVersion ?? MillerVersion.Current,

                    // Wired on the DISABLED branch too, and deliberately. The zero-work guarantee must
                    // hold because the branch returns before the loop starts, not because this caller
                    // remembered to leave the sink null. The host never invokes it here.
                    Diagnostic = message => CtDaemonLog.Write(root, message),
                }).GetAwaiter().GetResult();
            return new TestsServeResult(0, disabled.State.ToString().ToLowerInvariant(), disabled.Reason, null);
        }

        using var store = new ContinuousTestStore(CtSchema.DbPathFor(root));
        IReadOnlyList<ContinuousTestProject> projects = store.ListContinuousTestProjects(workspaceId);
        var selector = new ContinuousTestImpactSelector(
            store,
            new ReopeningMillerFactSource(() => OpenLiveFacts(root, workspaceId)));
        ContinuousTestProviderFactory providers = ContinuousTestProviderFactory.CreateDefault(
            onDiagnostic: message => CtDaemonLog.Write(root, message));

        // One activity cell, three holders: the provider factory's shared runner stamps every line the child
        // writes, the queue marks each run's start and end, and the daemon loop publishes both in
        // daemon.status.json. Built by the factory because that is where the stall bound is resolved.
        CtRunActivityCell runActivity = providers.RunActivity;
        var coordinator = new ContinuousTestCoordinator(
            providers,
            store,
            onDiagnostic: message => CtDaemonLog.Write(root, message));
        var queue = new ContinuousTestDaemonQueue(
            store,
            selector,
            coordinator,
            lifecycleLog: message => CtDaemonLog.Write(root, message),
            runActivity: runActivity);
        var poller = new ContinuousTestRevisionPoller(
            new MillerArtifactRevisionSource(),
            new MillerFactImpactSource(workspace => OpenLiveFacts(workspace, workspaceId)));

        // Family-worktree adoption: the daemon scans the machine-global registry through its
        // NON-CREATING read path and serves every registered, opted-in worktree of this repo
        // through a context bound to that worktree's own index and ct.db.
        string millerHome = ResolveMillerHome(request);
        var adoption = new ContinuousTestWorktreeAdoptionOptions
        {
            DiscoverRegisteredRoots = () =>
                ReadRegisteredWorkspaceRoots(Path.Combine(millerHome, "workspaces.db")),
            CreateContext = worktreeRoot => CreateWorktreeContext(worktreeRoot, providers, runActivity),
        };
        ContinuousTestDaemonSnapshot snapshot = ContinuousTestDaemonHost.RunAsync(
            root,
            new ContinuousTestDaemonHostOptions
            {
                KillSwitch = request.KillSwitch,
                Enabled = true,
                WorkspaceId = workspaceId,
                MillerVersion = request.MillerVersion ?? MillerVersion.Current,
                Store = store,
                Queue = queue,
                Poller = poller,
                Projects = projects,
                Budget = CtExecutionBudget.FromEnvironment(millerHome),
                RunActivity = runActivity,
                Diagnostic = message => CtDaemonLog.Write(root, message),
                WorktreeAdoption = adoption,
            }).GetAwaiter().GetResult();
        return new TestsServeResult(
            0,
            snapshot.State.ToString().ToLowerInvariant(),
            snapshot.Reason,
            null);
    }

    public static TestsStopResult Stop(TestsCoreRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string root = RequireRoot(request);

        // A worktree served by the FAMILY daemon detaches its own context only. Stopping the whole
        // daemon from a worktree would take down the main root and every sibling; a root with its
        // own live daemon (Adopting is false) keeps the full-stop semantics below.
        if (CtDaemonRouting.ResolveLiveEndpoint(root) is { Adopting: true } endpoint)
        {
            CtDaemonCommandAck? ack = CtDaemonRouting.RequestDetach(endpoint.EndpointRoot, root);
            return ack switch
            {
                null => new TestsStopResult(3, "failed", "detach request not acknowledged"),
                { State: CtDaemonCommandState.Rejected } => new TestsStopResult(
                    0,
                    "not_adopted",
                    $"daemon at {endpoint.EndpointRoot} does not serve this worktree"),
                _ => new TestsStopResult(
                    0,
                    "detached",
                    $"detached from family daemon at {endpoint.EndpointRoot}"),
            };
        }

        CtDaemonStopResult stopped = CtCommandChannel.Stop(root);
        int exit = stopped.Status == CtDaemonStopStatus.Failed ? 3 : 0;
        return new TestsStopResult(exit, Snake(stopped.Status.ToString()), stopped.Reason);
    }

    public static TestsRunResult Run(TestsCoreRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string root = RequireRoot(request);
        if (ContinuousTestPolicy.IsKillSwitchOff(request.KillSwitch))
            return new TestsRunResult(3, CtRunExecution.ForegroundOneShot, ContinuousTestVerdict.Unknown, "disabled", false, null);

        string workspaceId = ResolveWorkspaceId(request, root);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(root));
        IReadOnlyList<ContinuousTestProject> projects = store.ListContinuousTestProjects(workspaceId);
        CtRunDisposition disposition = CtDaemonLauncher.ResolveRun(root);
        if (disposition.Execution == CtRunExecution.Daemon)
        {
            // The endpoint may be the repo's main checkout: a worktree served by the FAMILY daemon
            // submits there, with its own root in the payload, and its command reaches its own
            // context's queue and ct.db.
            string endpointRoot = disposition.EndpointRoot ?? root;
            CtRunResult submitted = request.Hooks?.SubmitRun is { } submit
                ? submit(endpointRoot, root)
                : CtDaemonRouting.SubmitRun(endpointRoot, root, "run");

            // The lease died between the disposition read and the submit, so NOTHING holds the
            // request. Fall through to the foreground one-shot the caller would have gotten had the
            // disposition seen no lease. The channel answers that with ForegroundOneShot, so this
            // reads its own verdict rather than matching its reason text.
            if (submitted.Execution == CtRunExecution.Daemon)
            {
                // An ACKNOWLEDGED ack is the only proof a daemon took the request. A null ack - the
                // five-second ack timeout, which a daemon whose loop is inside a whole-suite drain
                // reaches easily - and a rejection both leave this process knowing nothing about a
                // run. Reporting exit 0 plus the standing store verdict then describes a run that
                // may never have started, and `tests run --json` promises the exit code and
                // `verdict` as the two fields a script reads. Same rule as the paused path below:
                // verdict unknown, null selected, the channel reason in the payload. An unacked
                // submit does NOT fall through to a foreground run - the daemon most likely HAS the
                // request, and a duplicate would run the suite twice.
                if (submitted.Ack is not { State: CtDaemonCommandState.Acknowledged })
                {
                    return new TestsRunResult(
                        ExitCode: 3,
                        Execution: CtRunExecution.Daemon,
                        Verdict: ContinuousTestVerdict.Unknown,
                        Reason: submitted.Reason ?? submitted.Ack?.Reason ?? "not acknowledged",
                        Waited: false,
                        Selected: null);
                }

                (TestsStatusResult status, TestsWaitResult? wait) = request.Wait
                    ? WaitForDaemonToSettle(request, endpointRoot, submitted.Ack.CommandId)
                    : (Status(request), null);
                return new TestsRunResult(
                    0,
                    CtRunExecution.Daemon,
                    status.Verdict,
                    submitted.Reason ?? submitted.Ack?.Reason,
                    request.Wait,
                    status.Selected,
                    Wait: wait);
            }
        }

        TestsRunOutcome outcome;
        if (request.Hooks?.ForegroundRun is { } hook)
        {
            outcome = WithExecutionBudget(
                request,
                root,
                () => hook(new TestsForegroundRunRequest(root, workspaceId, projects, request.Wait)));
        }
        else if (projects.Count == 0)
        {
            // Nothing executes, so nothing takes the user-global budget.
            outcome = new TestsRunOutcome(CtRunExecution.ForegroundOneShot, ContinuousTestVerdict.Unknown, "no enabled projects", request.Wait);
        }
        else
        {
            outcome = WithExecutionBudget(
                request,
                root,
                () => RunForeground(request, root, workspaceId, store, projects));
        }

        // A paused run executed NOTHING, so it holds no results at the selected key and must not
        // report the verdict an earlier revision stored. CLAUDE.md states the CT invariant plainly:
        // "Green requires complete results at the selected composite key." Zero results is not
        // green. A consumer of `tests run --json` reads the exit code and `verdict` - the two fields
        // docs/contracts/tests-cli-v1.md lists for this verb - so a stored green here passes a
        // change that no test ever saw. `paused` and the reason stay in the payload. `selected` is
        // null: the key is the LIVE index cursor, and a paused run is a total deferral that opens
        // nothing - not even the index it would have run against. `waited` is false because nothing
        // waited: the budget was already held when the request arrived. The exit code stays 0 - a
        // held budget is a deferral, not a failure.
        if (outcome.Paused)
        {
            return new TestsRunResult(
                ExitCode: 0,
                Execution: outcome.Execution,
                Verdict: ContinuousTestVerdict.Unknown,
                Reason: outcome.Reason,
                Waited: false,
                Selected: null,
                Paused: true);
        }

        TestsStatusResult after = Status(request);
        return new TestsRunResult(
            0,
            outcome.Execution,
            after.Selected is null ? outcome.Verdict : after.Verdict,
            outcome.Reason,
            outcome.Waited || request.Wait,
            after.Selected,
            outcome.Paused);
    }

    /// <summary>
    /// The run the daemon is executing, or JSON null. <c>child</c> is the daemon's own reading of how lively
    /// the test process is, so a reader never has to subtract timestamps to tell slow from wedged.
    /// </summary>
    private static void WriteDaemonRun(Utf8JsonWriter writer, CtDaemonRunProgress? run)
    {
        if (run is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("project_path", run.ProjectPath);
        writer.WriteString("run_id", run.RunId);
        writer.WriteNumber("selected_case_count", run.SelectedCaseCount);
        writer.WriteString("started_at", run.RunStartedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        writer.WriteString("child", Snake(run.Activity.ToString()));
        if (run.ProviderSource is { } provider)
            writer.WriteString("provider", provider);
        if (run.Selection is { } selection)
        {
            writer.WritePropertyName("selection");
            WriteDaemonSelection(writer, selection);
        }

        if (run.ElapsedSeconds is { } elapsed)
            writer.WriteNumber("elapsed_seconds", elapsed);
        if (run.RequestedUniqueUnitCount is { } requested)
            writer.WriteNumber("requested_unique_unit_count", requested);
        if (run.ChunkCount is { } chunks)
            writer.WriteNumber("chunk_count", chunks);
        if (run.CurrentPart is { } part)
            writer.WriteNumber("current_part", part);
        if (run.CurrentPartUnitCount is { } partUnits)
            writer.WriteNumber("current_part_unit_count", partUnits);
        if (run.NameSamples is { } names)
        {
            IReadOnlyList<string> boundedNames = names.Take(MaxDaemonActivityNames).ToArray();
            writer.WritePropertyName("case_names");
            writer.WriteStartArray();
            foreach (string name in boundedNames)
                writer.WriteStringValue(name);
            writer.WriteEndArray();
            if (run.NamesTruncated is not null || names.Count > boundedNames.Count)
                writer.WriteBoolean("names_truncated", (run.NamesTruncated ?? false) || names.Count > boundedNames.Count);
        }
        else if (run.NamesTruncated is { } truncated)
        {
            writer.WriteBoolean("names_truncated", truncated);
        }

        if (run.NameDigest is { } digest)
            writer.WriteString("name_digest", digest);
        writer.WriteEndObject();
    }

    private static void WriteDaemonSelection(Utf8JsonWriter writer, ContinuousTestDaemonSelectionFacts selection)
    {
        writer.WriteStartObject();
        writer.WriteString("scope", Snake(selection.Scope.ToString()));
        writer.WriteString("lane", Snake(selection.Lane.ToString()));
        writer.WriteNumber("known_count", selection.KnownCount);
        writer.WriteNumber("pre_trim_selected_count", selection.PreTrimSelectedCount);
        writer.WriteNumber("post_trim_selected_count", selection.PostTrimSelectedCount);
        writer.WriteNumber("retained_red_count", selection.RetainedRedCount);
        writer.WriteBoolean("covers_every_known_case", selection.CoversEveryKnownCase);
        writer.WriteBoolean("eligible", selection.Eligible);
        writer.WriteString("reason_code", selection.ReasonCode);
        writer.WriteString("selection_digest", selection.SelectionDigest);
        writer.WriteEndObject();
    }

    /// <summary>
    /// Which build the live daemon runs. Always four keys, so a reader never has to branch on their
    /// presence: with no daemon they read null / <c>none</c> / false / "no live daemon".
    /// </summary>
    private static void WriteDaemonVersion(Utf8JsonWriter writer, CtDaemonVersionVerdict? version)
    {
        if (version is null)
        {
            writer.WriteNull("miller_version");
            writer.WriteString("version_match", Snake(CtDaemonVersionMatch.None.ToString()));
            writer.WriteBoolean("version_mismatch", false);
            writer.WriteString("version_reason", "no live daemon");
            return;
        }

        if (version.DaemonVersion is { Length: > 0 } daemonVersion)
            writer.WriteString("miller_version", daemonVersion);
        else
            writer.WriteNull("miller_version");
        writer.WriteString("version_match", Snake(version.Match.ToString()));
        writer.WriteBoolean("version_mismatch", version.Mismatch);
        writer.WriteString("version_reason", version.Reason);
    }

    /// <summary>
    /// Whether the daemon's main loop is turning. Always both keys: <c>loop_stalled</c> is what a reader
    /// acts on, and <c>loop_stall_seconds</c> is the measured lag between the record's own two stamps. The
    /// lag is null — never zero — when the record carries no loop tick to subtract, because a build that
    /// predates the field proves nothing and a false "0" would read as proof of health.
    /// </summary>
    private static void WriteDaemonLoop(Utf8JsonWriter writer, CtLoopHealthVerdict? loop)
    {
        writer.WriteBoolean("loop_stalled", loop?.Stalled ?? false);
        if (loop?.LagSeconds is { } seconds)
            writer.WriteNumber("loop_stall_seconds", seconds);
        else
            writer.WriteNull("loop_stall_seconds");
    }

    internal static string RenderStatusJson(TestsStatusResult result)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = NewWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", JsonSchemaVersion);
            writer.WriteString("miller_version", result.DaemonVersion?.OwnVersion ?? MillerVersion.Current);
            writer.WriteBoolean("enabled", result.Enabled);
            writer.WriteBoolean("kill_switch", result.KillSwitchOff);
            writer.WritePropertyName("projects");
            WriteProjects(writer, result.Projects);
            writer.WritePropertyName("daemon");
            writer.WriteStartObject();
            writer.WriteString("state", Snake(result.DaemonState.ToString()));
            writer.WriteString("reason", result.DaemonReason);
            writer.WriteBoolean("running", result.DaemonState == CtDaemonLifecycleState.Running);
            writer.WriteBoolean("paused", result.DaemonState == CtDaemonLifecycleState.Paused);
            writer.WriteString("activity", Snake(result.DaemonActivity.ToString()));
            writer.WritePropertyName("run");
            WriteDaemonRun(writer, result.DaemonRun);
            WriteDaemonVersion(writer, result.DaemonVersion);
            WriteDaemonLoop(writer, result.DaemonLoop);
            writer.WriteEndObject();
            writer.WriteString("verdict", Snake(result.Verdict.ToString()));
            writer.WritePropertyName("selected");
            WriteSelected(writer, result.Selected);
            writer.WriteNumber("stale_count", result.StaleCount);
            writer.WriteNumber("selected_count", result.SelectedCount);
            if (result.LastRun is null)
                writer.WriteNull("last_run");
            else
                writer.WriteString("last_run", result.LastRun);
            writer.WritePropertyName("budget_holder");
            WriteBudget(writer, result.BudgetHolder);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// Human-facing freshness key: the full index identity is a store cursor that can run to
    /// hundreds of characters, so compact output keeps the revision and a recognizable identity
    /// prefix. JSON output carries the full identity.
    /// </summary>
    internal static string CompactFreshness(CtFreshnessKey key)
    {
        const int identityPrefixLength = 24;
        string identity = key.IndexIdentity.Length <= identityPrefixLength
            ? key.IndexIdentity
            : key.IndexIdentity[..identityPrefixLength] + "…";
        return $"rev {key.Revision.ToString(CultureInfo.InvariantCulture)} ({identity})";
    }

    internal static string RenderStatusCompact(TestsStatusResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# tests");
        sb.AppendLine("enabled: " + (result.Enabled ? "true" : "false"));
        sb.AppendLine($"daemon: {Snake(result.DaemonState.ToString())} ({result.DaemonReason})");
        sb.AppendLine("activity: " + Snake(result.DaemonActivity.ToString()));
        if (result.DaemonRun is { } running)
        {
            sb.AppendLine($"  run: {running.ProjectPath} cases="
                + running.SelectedCaseCount.ToString(CultureInfo.InvariantCulture)
                + $" started={running.RunStartedAtUtc:O} child={Snake(running.Activity.ToString())}");
            AppendDaemonRunFacts(sb, running);
        }
        // Only when a live daemon disagrees with this build. The reason names both builds, so the
        // line needs no second copy of the version.
        if (result.DaemonVersion is { Mismatch: true } version)
            sb.AppendLine("daemon_build: " + version.Reason);
        // Only when the loop is provably wedged. A healthy loop, an unproven one, and a daemon that is
        // not running all stay silent: this line exists to name a fault, not to certify health.
        if (result.DaemonLoop is { Stalled: true } loop)
            sb.AppendLine($"daemon_loop: {Snake(loop.Health.ToString())} — {loop.Reason}");
        sb.AppendLine("verdict: " + Snake(result.Verdict.ToString()));
        sb.AppendLine("selected: " + (result.Selected is { } selectedKey
            ? CompactFreshness(selectedKey)
            : "none (no live index)"));
        sb.AppendLine($"stale: {result.StaleCount.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine("last_run: " + (result.LastRun ?? "-"));
        sb.AppendLine("budget: " + (result.BudgetHolder is { } holder
            ? $"pid={holder.Pid.ToString(CultureInfo.InvariantCulture)} {holder.WorkspaceRoot}"
            : "-"));
        sb.AppendLine($"projects: {result.Projects.Count.ToString(CultureInfo.InvariantCulture)}");
        foreach (TestsStatusProject project in result.Projects)
            sb.AppendLine($"  - {project.ProjectPath} ({project.Framework ?? "unknown"})");
        return sb.ToString().TrimEnd();
    }

    private static void AppendDaemonRunFacts(StringBuilder sb, CtDaemonRunProgress run)
    {
        if (run.ProviderSource is { } provider)
            sb.AppendLine("    provider: " + provider);
        if (run.Selection is { } selection)
        {
            sb.AppendLine("    selection: scope=" + Snake(selection.Scope.ToString())
                + " lane=" + Snake(selection.Lane.ToString())
                + " known=" + selection.KnownCount.ToString(CultureInfo.InvariantCulture)
                + " pre_trim=" + selection.PreTrimSelectedCount.ToString(CultureInfo.InvariantCulture)
                + " post_trim=" + selection.PostTrimSelectedCount.ToString(CultureInfo.InvariantCulture)
                + " retained_red=" + selection.RetainedRedCount.ToString(CultureInfo.InvariantCulture)
                + " covers_all=" + (selection.CoversEveryKnownCase ? "true" : "false")
                + " eligible=" + (selection.Eligible ? "true" : "false")
                + " reason=" + selection.ReasonCode
                + " digest=" + selection.SelectionDigest);
        }

        if (run.ElapsedSeconds is not null
            || run.RequestedUniqueUnitCount is not null
            || run.ChunkCount is not null
            || run.CurrentPart is not null
            || run.CurrentPartUnitCount is not null)
        {
            sb.Append("    progress:");
            if (run.ElapsedSeconds is { } elapsedSeconds)
                sb.Append(" elapsed=" + FormatSeconds(elapsedSeconds) + "s");
            if (run.RequestedUniqueUnitCount is { } requested)
                sb.Append(" requested=" + requested.ToString(CultureInfo.InvariantCulture));
            if (run.ChunkCount is { } chunks)
                sb.Append(" chunks=" + chunks.ToString(CultureInfo.InvariantCulture));
            if (run.CurrentPart is { } part && run.ChunkCount is { } totalChunks)
                sb.Append(" part=" + part.ToString(CultureInfo.InvariantCulture)
                    + "/" + totalChunks.ToString(CultureInfo.InvariantCulture));
            else if (run.CurrentPart is { } partOnly)
                sb.Append(" part=" + partOnly.ToString(CultureInfo.InvariantCulture));
            if (run.CurrentPartUnitCount is { } partUnits)
                sb.Append(" units=" + partUnits.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine();
        }

        if (run.NameSamples is { } names)
        {
            IReadOnlyList<string> boundedNames = names.Take(MaxDaemonActivityNames).ToArray();
            sb.AppendLine("    case_names: " + string.Join(", ", boundedNames));
            if (run.NamesTruncated is not null || names.Count > boundedNames.Count)
                sb.AppendLine("    names_truncated: " + (((run.NamesTruncated ?? false) || names.Count > boundedNames.Count) ? "true" : "false"));
        }
        else if (run.NamesTruncated is { } truncated)
        {
            sb.AppendLine("    names_truncated: " + (truncated ? "true" : "false"));
        }

        if (run.NameDigest is { } digest)
            sb.AppendLine("    name_digest: " + digest);
    }

    internal static string RenderMutationJson(TestsMutationResult result)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = NewWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("operation", result.Operation);
            writer.WriteNumber("enabled_count", result.EnabledCount);
            writer.WritePropertyName("projects");
            WriteProjects(writer, result.Projects);
            IReadOnlyList<TestsStatusProject> changed = result.ChangedProjects ?? [];
            writer.WriteNumber("changed_count", changed.Count);
            writer.WritePropertyName("changed_projects");
            WriteProjects(writer, changed);
            if (result.Error is not null)
                writer.WriteString("error", result.Error);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    internal static string RenderMutationCompact(TestsMutationResult result)
    {
        if (result.Error is not null)
            return result.Error;
        var sb = new StringBuilder();
        if (string.Equals(result.Operation, "disable", StringComparison.Ordinal))
        {
            // The heading counts what the call turned OFF. It used to count the projects that stayed
            // enabled, so disabling 1 of 3 read "disable 2 project(s)" over the other two.
            IReadOnlyList<TestsStatusProject> turnedOff = result.ChangedProjects ?? [];
            sb.AppendLine($"disable {turnedOff.Count.ToString(CultureInfo.InvariantCulture)} project(s)");
            foreach (TestsStatusProject project in turnedOff)
                sb.AppendLine($"  - {project.ProjectPath}");
            sb.AppendLine($"remaining enabled: {result.EnabledCount.ToString(CultureInfo.InvariantCulture)}");
            foreach (TestsStatusProject project in result.Projects)
                sb.AppendLine($"  - {project.ProjectPath}");
            return sb.ToString().TrimEnd();
        }

        sb.AppendLine($"{result.Operation} {result.EnabledCount.ToString(CultureInfo.InvariantCulture)} project(s)");
        foreach (TestsStatusProject project in result.Projects)
            sb.AppendLine($"  - {project.ProjectPath}");
        return sb.ToString().TrimEnd();
    }

    internal static string RenderServeJson(TestsServeResult result)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = NewWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("status", result.Status);
            if (result.Reason is null)
                writer.WriteNull("reason");
            else
                writer.WriteString("reason", result.Reason);
            if (result.ProcessId is { } pid)
                writer.WriteNumber("pid", pid);
            else
                writer.WriteNull("pid");
            if (result.Publication is { } publication)
            {
                writer.WritePropertyName("publication");
                writer.WriteStartObject();
                writer.WriteString("readiness", Snake(publication.Readiness.ToString()));
                writer.WriteNumber("elapsed_seconds", publication.Elapsed.TotalSeconds);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    internal static string RenderServeCompact(TestsServeResult result)
    {
        string output = result.Reason is null ? $"tests serve {result.Status}" : $"tests serve {result.Status}: {result.Reason}";
        if (result.Publication is { } publication)
        {
            output += $" publication: {Snake(publication.Readiness.ToString())}"
                + $" elapsed={FormatSeconds(publication.Elapsed.TotalSeconds)}s";
        }

        return output;
    }

    internal static string RenderStopJson(TestsStopResult result)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = NewWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("status", result.Status);
            if (result.Reason is null)
                writer.WriteNull("reason");
            else
                writer.WriteString("reason", result.Reason);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    internal static string RenderStopCompact(TestsStopResult result) =>
        result.Reason is null ? $"tests stop {result.Status}" : $"tests stop {result.Status}: {result.Reason}";

    internal static string RenderRunJson(TestsRunResult result)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = NewWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("execution", result.Execution == CtRunExecution.Daemon ? "daemon" : "foreground_one_shot");
            writer.WriteString("verdict", Snake(result.Verdict.ToString()));
            if (result.Reason is null)
                writer.WriteNull("reason");
            else
                writer.WriteString("reason", result.Reason);
            writer.WriteBoolean("waited", result.Waited);
            writer.WriteBoolean("paused", result.Paused);
            writer.WritePropertyName("selected");
            WriteSelected(writer, result.Selected);
            if (result.Wait is { } wait)
            {
                writer.WritePropertyName("wait");
                writer.WriteStartObject();
                writer.WriteBoolean("wait_complete", wait.WaitComplete);
                writer.WriteString("state", Snake(wait.State.ToString()));
                writer.WriteNumber("elapsed_seconds", wait.ElapsedSeconds);
                writer.WriteNumber("timeout_seconds", wait.TimeoutSeconds);
                writer.WriteString("command_id", wait.CommandId);
                if (wait.RunId is null)
                    writer.WriteNull("run_id");
                else
                    writer.WriteString("run_id", wait.RunId);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    internal static string RenderRunCompact(TestsRunResult result)
    {
        string execution = result.Execution == CtRunExecution.Daemon ? "daemon" : "foreground";
        string state = result.Paused ? " paused" : string.Empty;
        string output = $"tests run {execution}{state} verdict={Snake(result.Verdict.ToString())} {(result.Reason ?? string.Empty)}".TrimEnd();
        if (result.Wait is { } wait)
        {
            output += $" wait: {Snake(wait.State.ToString())} complete={(wait.WaitComplete ? "true" : "false")}"
                + $" elapsed={FormatSeconds(wait.ElapsedSeconds)}s/{FormatSeconds(wait.TimeoutSeconds)}s"
                + $" command={wait.CommandId} run={wait.RunId ?? "-"}";
        }

        return output;
    }

    internal static string RenderFailuresJson(TestsFailuresResult result)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = NewWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("failures");
            writer.WriteStartArray();
            foreach (ContinuousTestStatus row in result.Failures)
            {
                writer.WriteStartObject();
                writer.WriteString("test_case_id", row.TestCaseId);
                writer.WriteString("state", Snake(row.State.ToString()));
                writer.WriteString("index_identity", row.IndexIdentity);
                writer.WriteNumber("revision", row.Revision);
                if (row.FailureSummary is null)
                    writer.WriteNull("failure_summary");
                else
                    writer.WriteString("failure_summary", row.FailureSummary);
                WriteFailureCorrelation(writer, row);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteNumber("truncated", result.Truncated);
            writer.WriteNumber("total", result.Total);
            writer.WriteNumber("offset", result.Offset);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    internal static string RenderFailuresCompact(TestsFailuresResult result)
    {
        var sb = new StringBuilder();
        string shown = result.Failures.Count.ToString(CultureInfo.InvariantCulture);
        sb.AppendLine(result.Total > result.Failures.Count
            ? $"# tests failures ({shown} of {result.Total.ToString(CultureInfo.InvariantCulture)})"
            : $"# tests failures ({shown})");
        foreach (ContinuousTestStatus row in result.Failures)
        {
            sb.Append("  - ").Append(row.TestCaseId).Append(": ").Append(row.FailureSummary ?? row.State.ToString());
            AppendFailureCorrelation(sb, row);
            sb.AppendLine();
        }
        if (result.Truncated > 0)
        {
            // Names the next offset, so the reader can ask for the rest instead of only learning it exists.
            int next = result.Offset + result.Failures.Count;
            sb.AppendLine($"truncated: {result.Truncated.ToString(CultureInfo.InvariantCulture)}"
                + $" (next: offset={next.ToString(CultureInfo.InvariantCulture)})");
        }

        return sb.ToString().TrimEnd();
    }

    private static void WriteFailureCorrelation(Utf8JsonWriter writer, ContinuousTestStatus row)
    {
        if (row.RunningRunId is { } runningRunId)
            writer.WriteString("running_run_id", runningRunId);
        if (row.RunningRevision is { } runningRevision)
            writer.WriteString("running_revision", runningRevision);
        if (row.LastRunRevision is { } lastRunRevision)
            writer.WriteString("last_run_revision", lastRunRevision);
        if (row.LastResultStatus is { } lastResultStatus)
            writer.WriteString("last_result_status", lastResultStatus);
        if (row.LastResultAt is { } lastResultAt)
            writer.WriteString("last_result_at", lastResultAt.ToString("O", CultureInfo.InvariantCulture));
    }

    private static void AppendFailureCorrelation(StringBuilder sb, ContinuousTestStatus row)
    {
        if (row.RunningRunId is { } runningRunId)
            sb.Append(" running_run_id=").Append(runningRunId);
        if (row.RunningRevision is { } runningRevision)
            sb.Append(" running_revision=").Append(runningRevision);
        if (row.LastRunRevision is { } lastRunRevision)
            sb.Append(" last_run_revision=").Append(lastRunRevision);
        if (row.LastResultStatus is { } lastResultStatus)
            sb.Append(" last_result_status=").Append(lastResultStatus);
        if (row.LastResultAt is { } lastResultAt)
            sb.Append(" last_result_at=").Append(lastResultAt.ToString("O", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Admission for a foreground one-shot. CLAUDE.md's CT safety spec allows at most one workspace
    /// to execute tests at a time, and the daemon already honors it; a foreground run executes the
    /// same suites, so it must take the same user-global lease or two workspaces thrash one machine.
    /// A held lease reports paused instead of running anyway or failing. The <c>using</c> scope is
    /// the release path the daemon uses: it releases on a normal return and on a throw alike.
    /// <c>MILLER_CT_EXEC_BUDGET=off</c> yields a disabled budget whose acquire is a no-op.
    /// </summary>
    private static TestsRunOutcome WithExecutionBudget(
        TestsCoreRequest request,
        string root,
        Func<TestsRunOutcome> execute)
    {
        CtExecutionBudget budget = request.Hooks?.Budget
            ?? CtExecutionBudget.FromEnvironment(ResolveMillerHome(request));
        using CtExecutionBudgetLease? admission = budget.TryAcquire(
            new CtExecutionBudgetRequest(root, ExecutionBudgetReason),
            TimeSpan.Zero,
            CancellationToken.None);
        if (admission is null)
        {
            return new TestsRunOutcome(
                CtRunExecution.ForegroundOneShot,
                ContinuousTestVerdict.Unknown,
                ExecutionBudgetHeldReason,
                request.Wait,
                Paused: true);
        }

        return execute();
    }

    private static TestsRunOutcome RunForeground(
        TestsCoreRequest request,
        string root,
        string workspaceId,
        ContinuousTestStore store,
        IReadOnlyList<ContinuousTestProject> projects)
    {
        // A suite runs for minutes. One family-store connection held open across the drain pins the
        // served generation for that whole time, so a rebuild cannot promote until the run ends.
        // Read through the same reopening source the daemon uses: each read opens and closes its own
        // handle, so nothing is pinned while tests execute.
        Func<string, string, IMillerFactSource>? openFacts = request.Hooks?.OpenFacts;
        var facts = new ReopeningMillerFactSource(() => openFacts is null
            ? OpenLiveFacts(root, workspaceId)
            : openFacts(root, workspaceId));
        var selector = new ContinuousTestImpactSelector(store, facts);
        IContinuousTestProviderResolver providers = request.Hooks?.Providers
            ?? ContinuousTestProviderFactory.CreateDefault(
                onDiagnostic: message => CtDaemonLog.Write(root, message));
        var coordinator = new ContinuousTestCoordinator(
            providers,
            store,
            onDiagnostic: message => CtDaemonLog.Write(root, message));
        var queue = new ContinuousTestDaemonQueue(
            store,
            selector,
            coordinator,
            lifecycleLog: message => CtDaemonLog.Write(root, message));
        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Read the freshness key ONCE: every work item in this one-shot is enqueued at the same
        // generation, and a reopening source would otherwise open the store once per project.
        // No readable index means no key to run at — refuse honestly instead of enqueuing at a
        // fabricated sentinel key that stored results nobody can ever match again.
        CtFreshnessKey? live = TryFreshness(facts);
        if (live is not { } freshness)
        {
            return new TestsRunOutcome(
                CtRunExecution.ForegroundOneShot,
                ContinuousTestVerdict.Unknown,
                "no readable index",
                request.Wait);
        }

        // An explicit run executes exactly the CURRENT stale set. EnqueueExplicit trims cases
        // committed fresh at this key before any stale marking, so a green result survives a
        // `tests run` that has nothing to prove about it; when nothing is stale, nothing runs and
        // the verdict below reports the standing green. On a first run (no inventory yet) the
        // refresh path discovers the suite and the stale set IS everything — that is expected.
        foreach (ContinuousTestProjectWorkItem item in ContinuousTestProjectInventory.MaterializeProjectWorkItems(projects, root))
        {
            queue.EnqueueExplicit(new ContinuousTestDaemonChange(
                item.Workspace,
                freshness.Revision.ToString(CultureInfo.InvariantCulture),
                freshness.IndexIdentity,
                WorkspaceScope: true,
                ObservedAt: now,
                Command: item.Project.Command,
                Framework: item.Project.Framework));
        }

        if (queue.HasReadyWork(now))
            queue.DrainReadyAsync(now, CancellationToken.None).GetAwaiter().GetResult();

        IReadOnlyList<ContinuousTestStatus> statuses = store.ListContinuousTestStatuses(workspaceId);
        // Judge at the key this one-shot ran at. The caller (`Run`) re-reads a live status right
        // after and prefers ITS verdict, so a generation that moved mid-run is reported there.
        ContinuousTestProjectedStatus projected = ContinuousTestStatusProjection.Project(
            freshness,
            statuses,
            store.ListContinuousTestFreshWatermarks(workspaceId, freshness.IndexIdentity));
        return new TestsRunOutcome(CtRunExecution.ForegroundOneShot, projected.Verdict, "foreground", request.Wait);
    }

    /// <summary>
    /// Waits for the daemon to FINISH the accepted run, then reports whatever verdict is true at that moment.
    ///
    /// <para><b>Why it does not wait for a verdict.</b> It used to return as soon as the verdict was Green,
    /// Red, or Partial. Accepting a run marks the selected cases stale, which makes the verdict Partial
    /// immediately — so <c>--wait</c> returned within milliseconds, before a single test had run, and reported
    /// a mid-run answer as the result. A verdict is a description of the store, not a completion signal.
    /// This wait tests daemon ACTIVITY instead, so a run that is genuinely partial at rest still returns
    /// partial, and one that is partial because it just started does not.</para>
    ///
    /// <para>Six ways out, all bounded: the run completes, remains queued, is never picked up, reaches its
    /// wait limit, stops, or loses its lease.
    /// It never waits on a value that the work itself might never produce.</para>
    /// </summary>
    /// <summary>
    /// The daemon state is read from the ENDPOINT root - for a worktree served by the family
    /// daemon, that is the repo's main checkout, whose status file carries the live per-poll
    /// activity. The returned verdict always comes from the requested workspace's own store.
    /// </summary>
    private static (TestsStatusResult Status, TestsWaitResult Wait) WaitForDaemonToSettle(
        TestsCoreRequest request,
        string endpointRoot,
        string commandId)
    {
        TimeSpan timeout = request.WaitTimeout ?? TimeSpan.FromMinutes(10);
        TestsWaitProbe probe = request.Hooks?.WaitProbe ?? new TestsWaitProbe();
        Func<string, ContinuousTestDaemonSnapshot> readStatus =
            probe.ReadStatus ?? ContinuousTestDaemonHost.ReadStatus;
        Func<string, bool> isLeaseLive =
            probe.IsLeaseLive ?? (root => CtDaemonLease.TryReadLive(root) is not null);
        TimeProvider clock = probe.Clock ?? TimeProvider.System;
        Action<TimeSpan> delay = probe.Delay ?? (duration => Thread.Sleep(duration));
        long started = clock.GetTimestamp();
        long? queuedAt = null;
        long lastLivenessProbe = started;
        bool leaseLive = true;
        bool leaseProbed = false;
        ContinuousTestDaemonSnapshot snapshot = readStatus(endpointRoot);
        bool sawExecuting = false;
        string? runId = null;

        while (true)
        {
            TimeSpan elapsed = clock.GetElapsedTime(started);
            if (!leaseProbed || clock.GetElapsedTime(lastLivenessProbe) >= LivenessProbeInterval)
            {
                leaseLive = isLeaseLive(endpointRoot);
                leaseProbed = true;
                lastLivenessProbe = clock.GetTimestamp();
            }

            if (IsExecuting(snapshot))
                runId ??= snapshot.Run?.RunId;

            if (snapshot.State == CtDaemonLifecycleState.Stopped)
                return WaitResult(request, TestsWaitState.DaemonStopped, false, elapsed, timeout, commandId, runId);
            if (!leaseLive)
                return WaitResult(request, TestsWaitState.LeaseLost, false, elapsed, timeout, commandId, runId);

            if (IsExecuting(snapshot))
            {
                sawExecuting = true;
                queuedAt = null;
            }
            else if (IsQueued(snapshot))
            {
                runId ??= snapshot.Run?.RunId;
                queuedAt ??= clock.GetTimestamp();
                if (clock.GetElapsedTime(queuedAt.Value) >= QueuedWaitLimit)
                    return WaitResult(request, TestsWaitState.QueuedTimeout, false, elapsed, timeout, commandId, runId);
            }
            else if (sawExecuting || elapsed >= RunPickupGrace)
            {
                if (sawExecuting)
                {
                    if (isLeaseLive(endpointRoot))
                        return WaitResult(request, TestsWaitState.Completed, true, elapsed, timeout, commandId, runId);
                    return WaitResult(request, TestsWaitState.LeaseLost, false, elapsed, timeout, commandId, runId);
                }

                return WaitResult(request, TestsWaitState.NotPickedUp, false, elapsed, timeout, commandId, runId);
            }

            if (elapsed >= timeout)
                return WaitResult(request, TestsWaitState.WaitTimeout, false, elapsed, timeout, commandId, runId);

            TimeSpan remaining = timeout - elapsed;
            delay(remaining < WaitPollInterval ? remaining : WaitPollInterval);
            snapshot = readStatus(endpointRoot);
        }
    }

    private static (TestsStatusResult Status, TestsWaitResult Wait) WaitResult(
        TestsCoreRequest request,
        TestsWaitState state,
        bool complete,
        TimeSpan elapsed,
        TimeSpan timeout,
        string commandId,
        string? runId) =>
        (
            Status(request),
            new TestsWaitResult(
                complete,
                state,
                elapsed.TotalSeconds,
                timeout.TotalSeconds,
                commandId,
                runId));

    /// <summary>
    /// A run is in flight. The activity field is authoritative; the reason string is the fallback for a
    /// status file written by an older daemon that has no activity field.
    /// </summary>
    private static bool IsExecuting(ContinuousTestDaemonSnapshot snapshot) =>
        snapshot.Activity == CtDaemonActivity.Executing
        || (snapshot.Activity == CtDaemonActivity.Idle
            && snapshot.State == CtDaemonLifecycleState.Running
            && string.Equals(snapshot.Reason, "executing", StringComparison.Ordinal));

    private static bool IsQueued(ContinuousTestDaemonSnapshot snapshot) =>
        snapshot.Activity == CtDaemonActivity.Queued
        || (snapshot.Activity == CtDaemonActivity.Idle
            && string.Equals(snapshot.Reason, ExecutionBudgetHeldReason, StringComparison.Ordinal));

    /// <summary>
    /// Registered workspace roots from the machine-global registry, through the NON-CREATING read
    /// path - the scan must never repair or create the registry. An ABSENT registry is an honest
    /// empty list: nothing was ever registered. A read FAILURE (locked database, foreign schema,
    /// denied access) THROWS instead of degrading to empty: the daemon host treats a throw as
    /// "cannot read the registry" and keeps its current adopted set, where an empty list reads as
    /// "nothing registered" and detaches every adopted worktree.
    /// </summary>
    private static IReadOnlyList<string> ReadRegisteredWorkspaceRoots(string registryDbPath)
    {
        using WorkspaceRegistry? registry = WorkspaceRegistry.TryOpenReadOnly(registryDbPath);
        if (registry is not null)
            return registry.List().Select(row => row.CanonicalRoot).ToArray();
        if (!File.Exists(registryDbPath))
            return [];
        throw new IOException(
            $"the workspace registry at {registryDbPath} exists but could not be opened for reading");
    }

    /// <summary>
    /// The per-worktree machinery, mirroring the primary wiring in <see cref="ServeHost"/>: the
    /// worktree's OWN ct.db, a selector and poller bound to the worktree's OWN index, and the
    /// SHARED provider resolver and run-activity cell (one child runs at a time under the global
    /// budget). Projects come from the worktree's stored inventory when it has one, else from a
    /// fresh read-only discovery - a new worktree of an enabled repo must run with zero manual
    /// calls, and discovery persists nothing.
    /// </summary>
    private static ContinuousTestWorkspaceContext? CreateWorktreeContext(
        string worktreeRoot,
        IContinuousTestProviderResolver providers,
        CtRunActivityCell runActivity)
    {
        string root = Path.GetFullPath(worktreeRoot);
        string workspaceId = WorkspaceId.FromCanonicalRoot(root);
        var store = new ContinuousTestStore(CtSchema.DbPathFor(root));
        try
        {
            IReadOnlyList<ContinuousTestProject> projects = store.ListContinuousTestProjects(workspaceId);
            if (projects.Count == 0)
                projects = ContinuousTestProjectInventory.Discover(root, workspaceId);
            var selector = new ContinuousTestImpactSelector(
                store,
                new ReopeningMillerFactSource(() => OpenLiveFacts(root, workspaceId)));
            var coordinator = new ContinuousTestCoordinator(
                providers,
                store,
                onDiagnostic: message => CtDaemonLog.Write(root, message));
            var queue = new ContinuousTestDaemonQueue(
                store,
                selector,
                coordinator,
                lifecycleLog: message => CtDaemonLog.Write(root, message),
                runActivity: runActivity);
            var poller = new ContinuousTestRevisionPoller(
                new MillerArtifactRevisionSource(),
                new MillerFactImpactSource(workspace => OpenLiveFacts(workspace, workspaceId)));
            return new ContinuousTestWorkspaceContext
            {
                WorkspaceRoot = root,
                WorkspaceId = workspaceId,
                Store = store,
                Queue = queue,
                Poller = poller,
                Projects = projects,
                Owned = store,
            };
        }
        catch
        {
            store.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens the live index for CT facts. Throws when no readable index exists; callers that can
    /// degrade honestly go through <see cref="TryFreshness"/> / <see cref="TryReadLiveFreshness"/>
    /// instead of receiving a fabricated sentinel cursor.
    /// </summary>
    private static IOwnedFactSource OpenLiveFacts(string workspaceRoot, string workspaceId)
    {
        string dbPath = Path.Combine(workspaceRoot, CtSchema.MillerDirectoryName, "symbols.db");
        WorkspaceReadHandle handle = WorkspaceReadSessionFactory.Open(dbPath, workspaceRoot, workspaceId);
        return new OwningMillerFactSource(handle);
    }

    /// <summary>
    /// The live index's freshness key, or null when no readable index exists. The selected key
    /// NEVER comes from stored <c>ct.db</c> rows.
    /// </summary>
    private static CtFreshnessKey? TryReadLiveFreshness(TestsCoreRequest request, string root, string workspaceId)
    {
        Func<string, string, IMillerFactSource>? openFacts = request.Hooks?.OpenFacts;
        var facts = new ReopeningMillerFactSource(() => openFacts is null
            ? OpenLiveFacts(root, workspaceId)
            : openFacts(root, workspaceId));
        return TryFreshness(facts);
    }

    private static CtFreshnessKey? TryFreshness(IMillerFactSource facts)
    {
        try
        {
            return facts.Freshness;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or InvalidOperationException or FamilyStoreReadException)
        {
            return null;
        }
    }

    private static TestsBudgetHolder? ReadBudgetHolder(string? millerHome)
    {
        string home = string.IsNullOrWhiteSpace(millerHome)
            ? MillerHome.ResolveMillerDirectory()
            : millerHome;
        CtExecutionBudgetOwner? owner = CtExecutionBudget.FromEnvironment(home).TryReadOwner();
        return owner is null ? null : new TestsBudgetHolder(owner.Pid, owner.WorkspaceRoot, owner.Reason);
    }

    private static TestsStatusProject ToStatusProject(ContinuousTestProject project) =>
        new(project.Id, project.ProjectPath, project.Framework, project.Command, project.Enabled, project.ExcludeTraits);

    private static TestsMutationResult MutationError(string operation, string error) =>
        new(3, operation, 0, [], error);

    private static bool TryResolveProject(string workspaceRoot, string projectPath, out string fullPath, out string? error)
    {
        fullPath = Path.IsPathRooted(projectPath)
            ? Path.GetFullPath(projectPath)
            : Path.GetFullPath(projectPath, workspaceRoot);
        string relative = Path.GetRelativePath(workspaceRoot, fullPath);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            error = "project path must live inside the workspace root";
            return false;
        }

        if (!File.Exists(fullPath))
        {
            error = $"test project not found: {fullPath}";
            return false;
        }

        error = null;
        return true;
    }

    private static string RequireRoot(TestsCoreRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceRoot);
        return Path.GetFullPath(request.WorkspaceRoot);
    }

    private static string ResolveWorkspaceId(TestsCoreRequest request, string root) =>
        string.IsNullOrWhiteSpace(request.WorkspaceId)
            ? WorkspaceId.FromCanonicalRoot(root)
            : request.WorkspaceId;

    private static string ResolveMillerHome(TestsCoreRequest request) =>
        string.IsNullOrWhiteSpace(request.MillerHome)
            ? MillerHome.ResolveMillerDirectory()
            : request.MillerHome;

    private static void WriteProjects(Utf8JsonWriter writer, IReadOnlyList<TestsStatusProject> projects)
    {
        writer.WriteStartArray();
        foreach (TestsStatusProject project in projects)
        {
            writer.WriteStartObject();
            writer.WriteString("id", project.Id);
            writer.WriteString("project_path", project.ProjectPath);
            if (project.Framework is null)
                writer.WriteNull("framework");
            else
                writer.WriteString("framework", project.Framework);
            if (project.Command is null)
                writer.WriteNull("command");
            else
                writer.WriteString("command", project.Command);
            writer.WriteBoolean("enabled", project.Enabled);
            writer.WritePropertyName("exclude_traits");
            writer.WriteStartArray();
            foreach (string trait in project.ExcludeTraits)
                writer.WriteStringValue(trait);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteSelected(Utf8JsonWriter writer, CtFreshnessKey? selected)
    {
        if (selected is not { } key)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("index_identity", key.IndexIdentity);
        writer.WriteNumber("revision", key.Revision);
        writer.WriteEndObject();
    }

    private static void WriteBudget(Utf8JsonWriter writer, TestsBudgetHolder? holder)
    {
        if (holder is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteNumber("pid", holder.Pid);
        writer.WriteString("workspace_root", holder.WorkspaceRoot);
        writer.WriteString("reason", holder.Reason);
        writer.WriteEndObject();
    }

    private static Utf8JsonWriter NewWriter(ArrayBufferWriter<byte> buffer) =>
        new(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private static string Snake(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;
        var sb = new StringBuilder(value.Length + 4);
        for (int i = 0; i < value.Length; i++)
        {
            char ch = value[i];
            if (char.IsUpper(ch) && i > 0)
                sb.Append('_');
            sb.Append(char.ToLowerInvariant(ch));
        }

        return sb.ToString();
    }

    private static string FormatSeconds(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private interface IOwnedFactSource : IMillerFactSource, IDisposable;

    private sealed class OwningMillerFactSource : IOwnedFactSource
    {
        private readonly WorkspaceReadHandle _handle;
        private readonly CtFactAdapter _adapter;

        public OwningMillerFactSource(WorkspaceReadHandle handle)
        {
            _handle = handle;
            _adapter = new CtFactAdapter(handle);
        }

        public CtIndexCursor Current => _adapter.Current;

        public IReadOnlyList<CtSymbolFact> SymbolsForChangedFiles(IReadOnlyList<string> changedPaths) =>
            _adapter.SymbolsForChangedFiles(changedPaths);

        public IReadOnlyList<CtReferenceFact> ReferencesTo(IReadOnlyList<string> symbolIds) =>
            _adapter.ReferencesTo(symbolIds);

        public IReadOnlyList<CtReferenceFact> IdentifierEvidenceTo(IReadOnlyList<string> symbolIds) =>
            _adapter.IdentifierEvidenceTo(symbolIds);

        public CtImpactResult Impact(IReadOnlyList<string> seedSymbolIds, int maxDepth = 2, int limit = 100) =>
            _adapter.Impact(seedSymbolIds, maxDepth, limit);

        public void Dispose()
        {
            _adapter.Dispose();
            _handle.Dispose();
        }
    }
}
