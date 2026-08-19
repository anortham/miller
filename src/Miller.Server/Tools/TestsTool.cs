using System.ComponentModel;
using Miller.Indexing;
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
    private readonly WorkspaceContext _workspace;
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

    [McpServerTool(Name = "tests")]
    [Description(
        "Read continuous-test status (cheap: starts nothing). start is the only daemon start; enable is opt-in. " +
        "Operations: status (default), failures, start, stop, enable, disable, run. Compact adds a next-step line; " +
        "JSON is unchanged. NOT for: finding which tests a change would affect (impact) or running your inner-loop " +
        "suite (use your test runner). Example: tests operation=status.")]
    public string Tests(
        [Description("status|failures|start|stop|enable|disable|run. Default status. status starts nothing; start is the only spawn.")]
        string operation = "status",
        [Description("Output format: compact|json. Default compact.")]
        string format = "compact",
        [Description("For operation=run, wait for a green/red/partial verdict. Default false.")]
        bool wait = false,
        [Description("Test project path for enable/disable, relative to the workspace or absolute. Optional.")]
        string? project = null,
        [Description("Workspace selector: display_id, unique prefix, full id, registered root path, current, or primary.")]
        string? workspace_id = null)
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

            if (telemetry is not null)
                telemetry.Op = normalized;

            TestsCoreRequest request = CreateRequest(workspace_id, json, wait, project);
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
                case "failures":
                {
                    TestsFailuresResult result = TestsCore.Failures(request);
                    output = result.Render(json);
                    hint = result.Failures.Count == 0
                        ? NextStepHint.Render("tests operation=status", "re-check verdict")
                        : NextStepHint.Render("inspect", "open a failing test");
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
                    hint = result.ExitCode == 0
                        ? NextStepHint.Render("tests operation=status", "read the verdict")
                        : null;
                    break;
                }
                default:
                    throw new ToolDiagnosticException(ToolDiagnostic.Unsupported(
                        "unsupported_operation",
                        "tests operation must be status|failures|start|stop|enable|disable|run."));
            }

            if (!json && hint is not null)
                output = output + "\n" + hint;
            return output;
        }
        catch (Exception ex)
        {
            ToolDiagnostic diagnostic = ToolDiagnostic.FromException(ex);
            if (diagnostic.Outcome == ToolDiagnosticOutcome.Error)
                telemetry?.SetError(ex);
            return ToolDiagnosticRenderer.Render("tests", diagnostic, json, telemetry);
        }
    }

    private TestsCoreRequest CreateRequest(string? workspaceId, bool json, bool wait, string? project)
    {
        (string root, string? id) = ResolveWorkspace(workspaceId);
        return new TestsCoreRequest(
            WorkspaceRoot: root,
            WorkspaceId: id,
            MillerHome: Path.GetDirectoryName(_workspace.RegistryDbPath),
            KillSwitch: Environment.GetEnvironmentVariable(CtEnvironment.KillSwitch),
            MillerVersion: MillerVersion.Current,
            Hooks: _hooks,
            Json: json,
            Wait: wait,
            ProjectPath: project);
    }

    private (string Root, string? WorkspaceId) ResolveWorkspace(string? workspaceId)
    {
        if (string.IsNullOrWhiteSpace(workspaceId)
            || string.Equals(workspaceId, "current", StringComparison.OrdinalIgnoreCase)
            || string.Equals(workspaceId, "primary", StringComparison.OrdinalIgnoreCase))
        {
            return (_workspace.CanonicalRoot ?? _workspace.WorkspaceRoot, _workspace.WorkspaceId);
        }

        using WorkspaceRegistry registry = WorkspaceRegistry.Open(_workspace.RegistryDbPath);
        WorkspaceRegistryRow row = WorkspaceRegistrySelector.Resolve(registry, workspaceId);
        return (row.CanonicalRoot, row.WorkspaceId);
    }

    private static string? StatusHint(TestsStatusResult result)
    {
        if (result.KillSwitchOff)
            return null;
        if (!result.Enabled)
            return NextStepHint.Render("tests operation=enable", "opt in to continuous testing");
        if (result.DaemonState == CtDaemonLifecycleState.Stopped)
            return NextStepHint.Render("tests operation=start", "start the daemon");
        if (result.Verdict == ContinuousTestVerdict.Red)
            return NextStepHint.Render("tests operation=failures", "inspect red cases");
        return NextStepHint.Render("tests operation=failures", "inspect recent results");
    }

    private static string NormalizeOperation(string? operation) =>
        string.IsNullOrWhiteSpace(operation) ? "status" : operation.Trim().ToLowerInvariant();

    private static string NormalizeFormat(string? format) =>
        string.IsNullOrWhiteSpace(format) ? "compact" : format.Trim().ToLowerInvariant();

    private static bool IsSupportedOperation(string operation) =>
        operation is "status" or "failures" or "start" or "stop" or "enable" or "disable" or "run";
}
