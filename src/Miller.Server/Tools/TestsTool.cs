using System.ComponentModel;
using System.Text;
using Miller.Indexing;
using Miller.Server.Hosting;
using Miller.Server.Telemetry;
using Miller.Server.Workspaces;
using Miller.Testing;
using ModelContextProtocol.Server;

namespace Miller.Server.Tools;

/// <summary>
/// MCP wrapper over <see cref="TestsCore"/>. Status is a cheap read. Start is the only daemon spawn.
/// </summary>
[McpServerToolType]
public sealed class TestsTool
{
    private const int McpWaitSecondsDefault = 240;
    private const int McpWaitSecondsMinimum = 1;
    private const int McpWaitSecondsMaximum = 240;

    private readonly WorkspaceContext? _workspace;
    private readonly MillerHostPaths? _hostPaths;
    private readonly WorkspaceRegistry? _registry;
    private readonly TestsCoreHooks? _hooks;

    public TestsTool(WorkspaceContext workspace)
        : this(workspace, hooks: null)
    {
    }

    internal TestsTool(WorkspaceContext workspace, TestsCoreHooks? hooks)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        _workspace = workspace;
        _hooks = hooks;
    }

    /// <summary>Constructs continuous-test operations from global paths before a primary workspace binds.</summary>
    internal TestsTool(
        MillerHostPaths hostPaths,
        WorkspaceRegistry registry,
        TestsCoreHooks? hooks = null)
    {
        ArgumentNullException.ThrowIfNull(hostPaths);
        ArgumentNullException.ThrowIfNull(registry);
        _hostPaths = hostPaths;
        _registry = registry;
        _hooks = hooks;
    }

    [McpServerTool(Name = "tests")]
    [Description(
        "Continuous testing (CT) for this workspace: which cases a change staled and their last verdict. Opt-in " +
        "per workspace. Operations: status (default; cheap, starts nothing) reports the per-project verdict with " +
        "stale and red counts, or enabled=false plus the test projects found when CT is off; failures lists red " +
        "cases; run executes only the stale and red set as an explicit test-ID list (wait=true blocks for the " +
        "verdict); start is the only daemon spawn, stop ends it; enable/disable opt a project in or out. Compact " +
        "adds a next-step line; JSON is unchanged. NOT for: predicting which tests a change affects (impact) or a " +
        "one-off inner-loop run when CT is off (use your test runner). Example: tests operation=status, then " +
        "tests operation=run wait=true after an edit.")]
    public string Tests(
        [Description("status|failures|start|stop|enable|disable|run. Default status. status starts nothing; start is the only spawn.")]
        string operation = "status",
        [Description("Output format: compact|json. Default compact.")]
        string format = "compact",
        [Description("For operation=run, wait for daemon activity completion. Default false.")]
        bool wait = false,
        [Description("For operation=run with wait=true, timeout in seconds. Default 240; allowed range 1-240.")]
        int? wait_seconds = null,
        [Description("Test project path for enable/disable/failures, relative to the workspace or absolute. Optional.")]
        string? project = null,
        [Description("For operation=failures: error_class groups red rows by derived exception class. Optional.")]
        string? group = null,
        [Description("Registered workspace selector: display ID, unique prefix, full ID, or root path. Required for MCP calls.")] [System.ComponentModel.DataAnnotations.Required]
        string? workspace_id = null,
        [Description("For operation=failures, rows per page. 1-200, default 20.")]
        int limit = TestsCore.FailuresDefaultLimit,
        [Description("For operation=failures, red cases to skip. Page with the offset the output names. Default 0.")]
        int offset = 0)
    {
        var telemetry = TelemetryContext.Current;
        bool json = string.Equals(format?.Trim(), "json", StringComparison.OrdinalIgnoreCase);
        string normalized = NormalizeOperation(operation);
        try
        {
            if (NormalizeFormat(format) is not ("compact" or "json"))
            {
                throw new ToolDiagnosticException(ToolDiagnostic.Refusal(
                    "invalid_format",
                    "tests format must be compact or json."));
            }

            if (!IsSupportedOperation(normalized))
            {
                throw new ToolDiagnosticException(ToolDiagnostic.Unsupported(
                    "unsupported_operation",
                    "tests operation must be status|failures|start|stop|enable|disable|run."));
            }

            if (wait_seconds is not null && (normalized != "run" || !wait))
            {
                throw new ToolDiagnosticException(ToolDiagnostic.Refusal(
                    "invalid_wait_seconds",
                    "tests wait_seconds is only valid when operation=run and wait=true."));
            }

            string? normalizedGroup = string.IsNullOrWhiteSpace(group) ? null : group.Trim().ToLowerInvariant();
            if (normalizedGroup is not null && normalized != "failures")
            {
                throw new ToolDiagnosticException(ToolDiagnostic.Refusal(
                    "invalid_group",
                    "tests group is only valid when operation=failures."));
            }

            if (normalizedGroup is not (null or "error_class"))
            {
                throw new ToolDiagnosticException(ToolDiagnostic.Refusal(
                    "invalid_group",
                    "tests group must be error_class."));
            }

            if (wait_seconds is < McpWaitSecondsMinimum or > McpWaitSecondsMaximum)
            {
                throw new ToolDiagnosticException(ToolDiagnostic.Refusal(
                    "invalid_wait_seconds",
                    $"tests wait_seconds must be between {McpWaitSecondsMinimum} and {McpWaitSecondsMaximum} seconds."));
            }

            if (telemetry is not null)
                telemetry.Op = normalized;

            bool mutation = normalized is "start" or "stop" or "enable" or "disable" or "run";
            TestsCoreRequest request = CreateRequest(
                workspace_id,
                json,
                wait,
                wait_seconds,
                project,
                mutation ? WorkspaceSelectorIntent.Mutate : WorkspaceSelectorIntent.Read);
            string output;
            string? hint;
            switch (normalized)
            {
                case "status":
                {
                    TestsStatusResult result = TestsCore.Status(request);
                    output = result.Render(json);
                    hint = StatusHint(result);
                    break;
                }
                case "failures" when normalizedGroup is "error_class":
                {
                    TestsFailureGroupsResult result = TestsCore.FailureGroups(request);
                    output = TestsCore.RenderFailureGroupsWithinByteBudget(
                        result, json, ToolOutputBudget.TestsMcpMaxBytes);
                    hint = result.Groups.Count == 0
                        ? null
                        : result.Groups.Any(g => g.SampleTestCaseId.StartsWith("ct-discovery-failure", StringComparison.Ordinal))
                            ? NextStepHint.Render($"tests operation=failures workspace_id={request.WorkspaceId}", "read project discovery failure details")
                            : NextStepHint.Render("inspect", "open a failing test");
                    break;
                }
                case "failures":
                {
                    TestsFailuresResult result = TestsCore.Failures(request, limit, offset);
                    output = TestsCore.RenderFailuresWithinByteBudget(
                        result, json, ToolOutputBudget.TestsMcpMaxBytes);
                    if (result.Failures.Count == 0)
                    {
                        hint = null;
                    }
                    else if (result.Failures.FirstOrDefault(f => TestsCore.TryGetDiscoveryFailure(result, f, out _, out _, out string art, out _) && !string.IsNullOrWhiteSpace(art)) is { } discRow
                        && TestsCore.TryGetDiscoveryFailure(result, discRow, out _, out _, out string artifactPath, out _))
                    {
                        hint = NextStepHint.Render($"view_file {artifactPath}", "view discovery diagnostic log");
                    }
                    else if (FindDiscoveryArtifactPath(result) is { } discoveryPath)
                    {
                        hint = string.IsNullOrWhiteSpace(discoveryPath)
                            ? null
                            : NextStepHint.Render($"view_file {discoveryPath}", "view discovery diagnostic log");
                    }
                    else
                    {
                        hint = NextStepHint.Render("inspect", "open a failing test");
                    }
                    break;
                }
                case "start":
                {
                    TestsServeResult result = TestsCore.Start(request);
                    output = result.Render(json);
                    hint = result.ExitCode == 0
                        ? NextStepHint.Render("tests operation=status", "confirm daemon state")
                        : null;
                    break;
                }
                case "stop":
                {
                    TestsStopResult result = TestsCore.Stop(request);
                    output = result.Render(json);
                    hint = result.ExitCode == 0
                        ? NextStepHint.Render("tests operation=status", "confirm daemon stopped")
                        : null;
                    break;
                }
                case "enable":
                {
                    TestsMutationResult result = TestsCore.Enable(request);
                    output = result.Render(json);
                    hint = result.Error is null
                        ? NextStepHint.Render("tests operation=start", "start the daemon")
                        : null;
                    break;
                }
                case "disable":
                {
                    TestsMutationResult result = TestsCore.Disable(request);
                    output = result.Render(json);
                    hint = result.Error is null
                        ? NextStepHint.Render("tests operation=status", "confirm projects are off")
                        : null;
                    break;
                }
                case "run":
                {
                    TestsRunResult result = TestsCore.Run(request);
                    output = result.Render(json);
                    hint = result.Wait?.State == TestsWaitState.DaemonStopped
                        ? NextStepHint.Render($"tests operation=start workspace_id={request.WorkspaceId}", "start the daemon before waiting")
                        : result.Wait?.State == TestsWaitState.ProtocolUnknown
                        ? NextStepHint.Render("tests operation=status", "older daemon cannot prove request completion; let its work settle, then explicitly stop/start to use this build")
                        : result.Command is { State: CtDaemonCommandState.Requested or CtDaemonCommandState.Acknowledged
                            or CtDaemonCommandState.Selecting or CtDaemonCommandState.Queued or CtDaemonCommandState.Running } command
                        ? NextStepHint.Render("tests operation=status", $"request {command.CommandId} is {command.State.ToString().ToLowerInvariant()}")
                        : result.ExitCode != 0
                        ? null
                        : result.Paused
                            ? NextStepHint.Render("tests operation=run", "retry once the other workspace finishes")
                            : NextStepHint.Render("tests operation=status", "read the verdict");
                    break;
                }
                default:
                    throw new ToolDiagnosticException(ToolDiagnostic.Unsupported(
                        "unsupported_operation",
                        "tests operation must be status|failures|start|stop|enable|disable|run."));
            }

            if (!json && hint is not null)
                output = output + "\n" + hint;
            return RequireTestsMcpOutput(output);
        }
        catch (Exception ex)
        {
            ToolDiagnostic diagnostic = ToolDiagnostic.FromException(ex);
            if (diagnostic.Outcome == ToolDiagnosticOutcome.Error)
                telemetry?.SetError(ex);
            return ToolDiagnosticRenderer.Render("tests", diagnostic, json, telemetry);
        }
    }

    private TestsCoreRequest CreateRequest(
        string? workspaceId,
        bool json,
        bool wait,
        int? waitSeconds,
        string? project,
        WorkspaceSelectorIntent intent)
    {
        (string root, string? id) = ResolveWorkspace(workspaceId, intent);
        return new TestsCoreRequest(
            WorkspaceRoot: root,
            WorkspaceId: id,
            MillerHome: _hostPaths?.MillerDirectory
                ?? Path.GetDirectoryName(_workspace?.RegistryDbPath),
            KillSwitch: Environment.GetEnvironmentVariable(CtEnvironment.KillSwitch),
            MillerVersion: MillerVersion.Current,
            Hooks: _hooks,
            Json: json,
            Wait: wait,
            ProjectPath: project,
            WaitTimeout: wait
                ? TimeSpan.FromSeconds(waitSeconds ?? McpWaitSecondsDefault)
                : null,
            RequireDaemonWhenWaiting: true);
    }

    private (string Root, string? WorkspaceId) ResolveWorkspace(
        string? workspaceId,
        WorkspaceSelectorIntent intent)
    {
        (string Root, string? WorkspaceId) resolved;
        if (string.IsNullOrWhiteSpace(workspaceId)
            || string.Equals(workspaceId, "current", StringComparison.OrdinalIgnoreCase)
            || string.Equals(workspaceId, "primary", StringComparison.OrdinalIgnoreCase))
        {
            WorkspaceContext current = _workspace
                ?? throw new InvalidOperationException(
                    "tests requires an explicit registered workspace_id when no primary workspace is bound.");
            resolved = (current.CanonicalRoot ?? current.WorkspaceRoot, current.WorkspaceId);
        }
        else
        {
            WorkspaceRegistry registry = _registry ?? WorkspaceRegistry.Open(
                _hostPaths?.RegistryDbPath
                ?? _workspace?.RegistryDbPath
                ?? throw new InvalidOperationException("A workspace registry is not available."));
            try
            {
                WorkspaceRegistryRow row = WorkspaceRegistrySelector.Resolve(registry, workspaceId, intent);
                resolved = (row.CanonicalRoot, row.WorkspaceId);
            }
            finally
            {
                if (_registry is null)
                    registry.Dispose();
            }
        }

        if (!string.IsNullOrWhiteSpace(resolved.WorkspaceId))
            TelemetryContext.Current?.SetWorkspace(resolved.WorkspaceId, resolved.Root);
        return resolved;
    }

    internal static string? StatusHint(TestsStatusResult result)
    {
        if (result.KillSwitchOff)
            return null;

        if (!result.Enabled)
        {
            if (result.DirectRunRecipe is { } recipe && !string.IsNullOrWhiteSpace(recipe.PrimaryCommand))
            {
                string reason = result.Projects.Any(p => p.UnsupportedReason is null)
                    ? "run tests directly (or enable CT: tests operation=enable)"
                    : "run tests directly (framework unsupported under CT)";
                return NextStepHint.Render("run the direct recipe above", reason);
            }
            return null;
        }

        if (result.DaemonState == CtDaemonLifecycleState.Stopped)
        {
            return result.Projects.Any(p => p.UnsupportedReason is null)
                ? NextStepHint.Render("tests operation=start", "start the daemon")
                : null;
        }

        if (result.DaemonActivity == CtDaemonActivity.Selecting || result.DaemonSelection is not null)
        {
            if (result.DaemonLoop is { Stalled: true } selectionHealth)
                return NextStepHint.Render("tests operation=status", $"selection has not reported progress ({selectionHealth.Reason})");
            if (result.DaemonSelection is { } sel)
            {
                int elapsed = (int)Math.Max(0, (DateTimeOffset.UtcNow - sel.StartedAtUtc).TotalSeconds);
                return NextStepHint.Render("tests operation=status", $"selection in progress (phase={sel.Phase}, elapsed={elapsed}s)");
            }
            return NextStepHint.Render("tests operation=status", "selection in progress");
        }

        if (result.DaemonActivity == CtDaemonActivity.Executing || result.DaemonRun is not null)
        {
            if (result.DaemonRun is { } run)
            {
                int elapsed = run.ElapsedSeconds is { } es
                    ? (int)es
                    : (int)Math.Max(0, (DateTimeOffset.UtcNow - run.RunStartedAtUtc).TotalSeconds);
                string projName = !string.IsNullOrEmpty(run.ProjectPath) ? Path.GetFileName(run.ProjectPath) : "test project";
                return NextStepHint.Render("tests operation=status", $"tests executing ({projName}, elapsed={elapsed}s)");
            }
            return NextStepHint.Render("tests operation=status", "tests executing");
        }

        if (result.DaemonActivity == CtDaemonActivity.Queued)
        {
            return NextStepHint.Render("tests operation=status", "command queued, awaiting execution");
        }

        if (result.DaemonLoop is { Stalled: true } loop)
        {
            return NextStepHint.Render("tests operation=status", $"daemon loop unresponsive ({loop.Reason}); inspect daemon diagnostics");
        }

        if (result.DaemonVersion is { Match: CtDaemonVersionMatch.DaemonOlder })
        {
            return NextStepHint.Render("tests operation=start", "replace the older daemon");
        }

        if (result.Verdict == ContinuousTestVerdict.Red)
        {
            if (!string.IsNullOrWhiteSpace(result.DiscoveryFailureArtifactPath))
                return NextStepHint.Render($"view_file {result.DiscoveryFailureArtifactPath}", "view discovery diagnostic log");
            return NextStepHint.Render("tests operation=failures", "inspect red cases");
        }

        if (result.StaleCount > 0)
        {
            bool broadScope = result.Selected is null
                || result.Verdict == ContinuousTestVerdict.Unknown
                || (result.SelectedCount > 0 && result.StaleCount >= (int)(result.SelectedCount * 0.8));
            string reason = broadScope
                ? $"execute {result.StaleCount} stale cases (broad project scope)"
                : $"execute {result.StaleCount} stale cases";
            return NextStepHint.Render("tests operation=run wait=true", reason);
        }

        if (result.DaemonAutoRunsPaused)
        {
            return NextStepHint.Render("tests operation=status", $"auto-runs paused ({result.DaemonPauseReason ?? "unknown"}); check status");
        }

        if (result.Verdict == ContinuousTestVerdict.Green)
        {
            return null;
        }

        return null;
    }

    private static string RequireTestsMcpOutput(string output)
    {
        if (Encoding.UTF8.GetByteCount(output) <= ToolOutputBudget.TestsMcpMaxBytes)
            return output;

        throw new ToolDiagnosticException(ToolDiagnostic.Refusal(
            "output_metadata_too_large",
            "tests output exceeds the 12 KiB MCP budget; narrow with limit, offset, project, or group=error_class."));
    }

    private static string? FindDiscoveryArtifactPath(TestsFailuresResult result)
    {
        foreach (ContinuousTestStatus row in result.Failures)
        {
            if (result.TestCases?.TryGetValue(row.TestCaseId, out ContinuousTestCase? tc) == true)
            {
                if (string.Equals(tc.Source, "ct-project-status", StringComparison.Ordinal)
                    || (tc.Metadata.TryGetValue("kind", out object? kindObj)
                        && string.Equals(kindObj?.ToString(), "ct-project-discovery-failure", StringComparison.Ordinal)))
                {
                    if (tc.Metadata.TryGetValue("artifact_path", out object? pathObj)
                        && pathObj?.ToString() is { Length: > 0 } path)
                    {
                        return path;
                    }

                    return string.Empty;
                }
            }

            if (row.TestCaseId.StartsWith("ct-discovery-failure", StringComparison.Ordinal))
            {
                return string.Empty;
            }
        }

        return null;
    }

    private static string NormalizeOperation(string? operation) =>
        string.IsNullOrWhiteSpace(operation) ? "status" : operation.Trim().ToLowerInvariant();

    private static string NormalizeFormat(string? format) =>
        string.IsNullOrWhiteSpace(format) ? "compact" : format.Trim().ToLowerInvariant();

    private static bool IsSupportedOperation(string operation) =>
        operation is "status" or "failures" or "start" or "stop" or "enable" or "disable" or "run";
}
