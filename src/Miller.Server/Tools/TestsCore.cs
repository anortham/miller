using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
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
/// </summary>
public sealed record TestsCoreHooks(
    Func<ProcessStartInfo, Process?>? StartProcess = null,
    Func<TestsForegroundRunRequest, TestsRunOutcome>? ForegroundRun = null,
    CtExecutionBudget? Budget = null,
    Func<string, string, IMillerFactSource>? OpenFacts = null,
    IContinuousTestProviderResolver? Providers = null);

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
    TestsBudgetHolder? BudgetHolder)
{
    public string Render(bool json) => json ? TestsCore.RenderStatusJson(this) : TestsCore.RenderStatusCompact(this);
}

public sealed record TestsMutationResult(
    int ExitCode,
    string Operation,
    int EnabledCount,
    IReadOnlyList<TestsStatusProject> Projects,
    string? Error)
{
    public string Render(bool json) => json ? TestsCore.RenderMutationJson(this) : TestsCore.RenderMutationCompact(this);
}

public sealed record TestsServeResult(
    int ExitCode,
    string Status,
    string? Reason,
    int? ProcessId)
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
    bool Paused = false)
{
    public string Render(bool json) => json ? TestsCore.RenderRunJson(this) : TestsCore.RenderRunCompact(this);
}

public sealed record TestsFailuresResult(IReadOnlyList<ContinuousTestStatus> Failures, int Truncated)
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
    private const string TestsUsage =
        "miller tests <status|serve|run|enable|disable|stop> [--json] [--wait] [--project PATH] [--workspace-id SELECTOR] [--workspace DIR]";

    // Same request reason the daemon records, so `budget_holder.reason` reads the same whether the
    // suite runs in a daemon or in the foreground.
    private const string ExecutionBudgetReason = "run";

    // Same wording the daemon publishes when it pauses on a held budget. The paused vocabulary is
    // shared on purpose: one held lease, one explanation, whichever path reports it.
    private const string ExecutionBudgetHeldReason = "execution budget held";

    public static string Usage => TestsUsage;

    public static TestsStatusResult Status(TestsCoreRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string root = RequireRoot(request);
        string workspaceId = ResolveWorkspaceId(request, root);
        bool killSwitchOff = ContinuousTestPolicy.IsKillSwitchOff(request.KillSwitch);
        bool optedIn = ContinuousTestPolicy.IsWorkspaceOptedIn(root);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(root));
        IReadOnlyList<ContinuousTestProject> stored = store.ListContinuousTestProjects(workspaceId, includeDisabled: false);
        IReadOnlyList<ContinuousTestStatus> statuses = store.ListContinuousTestStatuses(workspaceId);
        ContinuousTestDaemonSnapshot snapshot = ContinuousTestDaemonHost.ReadStatus(root);
        CtFreshnessKey? selected = SelectedFrom(statuses);
        ContinuousTestVerdict verdict = selected is { } key
            ? ContinuousTestFreshness.Evaluate(statuses, key, watchHealthy: true)
            : ContinuousTestVerdict.Unknown;
        TestsBudgetHolder? budget = ReadBudgetHolder(request.MillerHome);
        bool enabled = !killSwitchOff && (optedIn || stored.Count > 0);
        return new TestsStatusResult(
            Enabled: enabled,
            KillSwitchOff: killSwitchOff,
            Projects: stored.Select(ToStatusProject).ToArray(),
            DaemonState: snapshot.State,
            DaemonReason: killSwitchOff ? "disabled" : snapshot.Reason,
            Verdict: verdict,
            Selected: selected,
            StaleCount: statuses.Count(row => row.State == ContinuousTestState.Stale),
            SelectedCount: statuses.Count,
            LastRun: store.LatestTestRunAt(workspaceId),
            BudgetHolder: budget);
    }

    public static TestsFailuresResult Failures(TestsCoreRequest request, int maxItems = 5)
    {
        ArgumentNullException.ThrowIfNull(request);
        string root = RequireRoot(request);
        string workspaceId = ResolveWorkspaceId(request, root);
        int limit = Math.Clamp(maxItems, 1, 20);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(root));
        ContinuousTestStatus[] red = store.ListContinuousTestStatuses(workspaceId)
            .Where(row => row.State == ContinuousTestState.Red)
            .OrderBy(row => row.TestCaseId, StringComparer.Ordinal)
            .ToArray();
        return new TestsFailuresResult(red.Take(limit).ToArray(), Math.Max(0, red.Length - limit));
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

        using var store = new ContinuousTestStore(CtSchema.DbPathFor(root));
        store.Transaction(() =>
        {
            foreach (ContinuousTestProject project in discovered)
                store.PutContinuousTestProject(project with { Enabled = true });
        });

        IReadOnlyList<ContinuousTestProject> enabled = store.ListContinuousTestProjects(workspaceId);
        return new TestsMutationResult(0, "enable", enabled.Count, enabled.Select(ToStatusProject).ToArray(), null);
    }

    public static TestsMutationResult Disable(TestsCoreRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string root = RequireRoot(request);
        string workspaceId = ResolveWorkspaceId(request, root);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(root));
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
        }

        return new TestsMutationResult(
            0,
            "disable",
            remaining.Count,
            remaining.Select(ToStatusProject).ToArray(),
            null);
    }

    public static TestsServeResult Start(TestsCoreRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string root = RequireRoot(request);
        if (ContinuousTestPolicy.IsKillSwitchOff(request.KillSwitch))
            return new TestsServeResult(3, "refused", "continuous testing is disabled (MILLER_CT=off)", null);
        if (!ContinuousTestPolicy.IsWorkspaceOptedIn(root))
            return new TestsServeResult(3, "refused", "not enabled; run miller tests enable first", null);

        CtDaemonSpawnResult spawned = CtDaemonLauncher.SpawnDetached(root, request.Hooks?.StartProcess);
        int exit = spawned.Status is CtDaemonSpawnStatus.Started or CtDaemonSpawnStatus.AlreadyRunning ? 0 : 3;
        return new TestsServeResult(
            exit,
            spawned.Status.ToString().ToLowerInvariant(),
            spawned.Reason,
            spawned.ProcessId);
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
                }).GetAwaiter().GetResult();
            return new TestsServeResult(0, disabled.State.ToString().ToLowerInvariant(), disabled.Reason, null);
        }

        using var store = new ContinuousTestStore(CtSchema.DbPathFor(root));
        IReadOnlyList<ContinuousTestProject> projects = store.ListContinuousTestProjects(workspaceId);
        var selector = new ContinuousTestImpactSelector(
            store,
            new ReopeningMillerFactSource(() => OpenLiveFacts(root, workspaceId)));
        var coordinator = new ContinuousTestCoordinator(
            ContinuousTestProviderFactory.CreateDefault(
                onDiagnostic: message => CtDaemonLog.Write(root, message)),
            store,
            onDiagnostic: message => CtDaemonLog.Write(root, message));
        var queue = new ContinuousTestDaemonQueue(store, selector, coordinator);
        var poller = new ContinuousTestRevisionPoller(
            new MillerArtifactRevisionSource(),
            new MillerFactImpactSource(workspace => OpenLiveFacts(workspace, workspaceId)));
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
                Budget = CtExecutionBudget.FromEnvironment(ResolveMillerHome(request)),
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
            CtRunResult submitted = CtCommandChannel.Run(root, "run");
            TestsStatusResult status = request.Wait
                ? WaitForVerdict(request, root)
                : Status(request);
            return new TestsRunResult(
                submitted.Ack is { State: CtDaemonCommandState.Rejected } ? 3 : 0,
                CtRunExecution.Daemon,
                status.Verdict,
                submitted.Reason ?? submitted.Ack?.Reason,
                request.Wait,
                status.Selected);
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

        TestsStatusResult after = Status(request);

        // A paused run executed NOTHING, so it holds no results at the selected key and must not
        // report the verdict an earlier revision stored. CLAUDE.md states the CT invariant plainly:
        // "Green requires complete results at the selected composite key." Zero results is not
        // green. A consumer of `tests run --json` reads the exit code and `verdict` - the two fields
        // docs/contracts/tests-cli-v1.md lists for this verb - so a stored green here passes a
        // change that no test ever saw. `paused` and the reason stay in the payload, and `selected`
        // still names the key the stored rows carry, so a person still sees what CT knows. `waited`
        // is false because nothing waited: the budget was already held when the request arrived.
        // The exit code stays 0 - a held budget is a deferral, not a failure.
        if (outcome.Paused)
        {
            return new TestsRunResult(
                ExitCode: 0,
                Execution: outcome.Execution,
                Verdict: ContinuousTestVerdict.Unknown,
                Reason: outcome.Reason,
                Waited: false,
                Selected: after.Selected,
                Paused: true);
        }

        return new TestsRunResult(
            0,
            outcome.Execution,
            after.Selected is null ? outcome.Verdict : after.Verdict,
            outcome.Reason,
            outcome.Waited || request.Wait,
            after.Selected,
            outcome.Paused);
    }

    internal static string RenderStatusJson(TestsStatusResult result)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = NewWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", JsonSchemaVersion);
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
        sb.AppendLine("verdict: " + Snake(result.Verdict.ToString()));
        sb.AppendLine("selected: " + (result.Selected is { } selectedKey ? CompactFreshness(selectedKey) : "-"));
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
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    internal static string RenderServeCompact(TestsServeResult result) =>
        result.Reason is null ? $"tests serve {result.Status}" : $"tests serve {result.Status}: {result.Reason}";

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
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    internal static string RenderRunCompact(TestsRunResult result)
    {
        string execution = result.Execution == CtRunExecution.Daemon ? "daemon" : "foreground";
        string state = result.Paused ? " paused" : string.Empty;
        return $"tests run {execution}{state} verdict={Snake(result.Verdict.ToString())} {(result.Reason ?? string.Empty)}".TrimEnd();
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
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteNumber("truncated", result.Truncated);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    internal static string RenderFailuresCompact(TestsFailuresResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# tests failures ({result.Failures.Count.ToString(CultureInfo.InvariantCulture)})");
        foreach (ContinuousTestStatus row in result.Failures)
            sb.AppendLine($"  - {row.TestCaseId}: {row.FailureSummary ?? row.State.ToString()}");
        if (result.Truncated > 0)
            sb.AppendLine($"truncated: {result.Truncated.ToString(CultureInfo.InvariantCulture)}");
        return sb.ToString().TrimEnd();
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
        var queue = new ContinuousTestDaemonQueue(store, selector, coordinator);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Read the freshness key ONCE: every work item in this one-shot is enqueued at the same
        // generation, and a reopening source would otherwise open the store once per project.
        CtFreshnessKey freshness = facts.Freshness;
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
        CtFreshnessKey? selected = SelectedFrom(statuses) ?? freshness;
        ContinuousTestVerdict verdict = selected is { } key
            ? ContinuousTestFreshness.Evaluate(statuses, key, watchHealthy: true)
            : ContinuousTestVerdict.Unknown;
        return new TestsRunOutcome(CtRunExecution.ForegroundOneShot, verdict, "foreground", request.Wait);
    }

    private static TestsStatusResult WaitForVerdict(TestsCoreRequest request, string root)
    {
        TimeSpan timeout = request.WaitTimeout ?? TimeSpan.FromMinutes(10);
        var clock = Stopwatch.StartNew();
        TestsStatusResult status = Status(request);
        while (clock.Elapsed < timeout)
        {
            if (status.Verdict is ContinuousTestVerdict.Green or ContinuousTestVerdict.Red or ContinuousTestVerdict.Partial)
                return status;
            if (status.DaemonState == CtDaemonLifecycleState.Stopped)
                return status;
            Thread.Sleep(50);
            status = Status(request with { WorkspaceRoot = root });
        }

        return status;
    }

    private static IOwnedFactSource OpenLiveFacts(string workspaceRoot, string workspaceId)
    {
        string dbPath = Path.Combine(workspaceRoot, CtSchema.MillerDirectoryName, "symbols.db");
        try
        {
            WorkspaceReadHandle handle = WorkspaceReadSessionFactory.Open(dbPath, workspaceRoot, workspaceId);
            return new OwningMillerFactSource(handle);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or InvalidOperationException or FamilyStoreReadException)
        {
            return new EmptyMillerFactSource();
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

    private static CtFreshnessKey? SelectedFrom(IReadOnlyList<ContinuousTestStatus> statuses)
    {
        if (statuses.Count == 0)
            return null;
        ContinuousTestStatus row = statuses
            .OrderByDescending(item => item.Revision)
            .ThenBy(item => item.IndexIdentity, StringComparer.Ordinal)
            .First();
        return new CtFreshnessKey(row.IndexIdentity, row.Revision);
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

    private sealed class EmptyMillerFactSource : IOwnedFactSource
    {
        public CtIndexCursor Current => new("unspecified", 0);

        public IReadOnlyList<CtSymbolFact> SymbolsForChangedFiles(IReadOnlyList<string> changedPaths) => [];

        public IReadOnlyList<CtReferenceFact> ReferencesTo(IReadOnlyList<string> symbolIds) => [];

        public IReadOnlyList<CtReferenceFact> IdentifierEvidenceTo(IReadOnlyList<string> symbolIds) => [];

        public CtImpactResult Impact(IReadOnlyList<string> seedSymbolIds, int maxDepth = 2, int limit = 100) =>
            new([], [], 0, false, false);

        public void Dispose()
        {
        }
    }
}
