using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Miller.Indexing;
using Miller.Server;
using Miller.Server.Hosting;
using Miller.Server.Logging;
using Miller.Server.Resolution;
using Miller.Server.Telemetry;
using Miller.Server.Tools;
using ModelContextProtocol.Server;
using Serilog;
using Serilog.Core;

// Miller MCP server host bootstrap (M2).
// IndexBootstrapService builds the in-memory index before tools need it — eagerly when cwd/env is safe, or
// deferred until MCP client roots arrive on the first tools/call. MCP transport starts even when deferred.
// [McpServerToolType] tool is registered explicitly for Native AOT; each ctor's deps resolve from DI. The ONE
// central telemetry CallToolFilter wraps every call.
//
// STDIO PURITY: nothing may touch stdout except the MCP protocol. Serilog Console is routed to stderr; never
// Console.WriteLine anywhere in this process.

// CLI FAST-PATH: a non-empty verb other than `serve` runs a one-shot command over the existing index and exits
// — NO MCP host, NO Serilog file logging, NO stdio-purity constraint (the CLI OWNS stdout here). `serve` and the
// no-args launch fall through to the MCP stdio server below (the historical default). The CLI reads the SAME pure
// tool cores the server exposes, so a shell invocation and a tool call agree. Resolved before any filesystem touch.
// STARTUP STAGES: every step below names itself here so the last-resort catch can say WHERE it died. The
// logger does not exist until the build-logger stage, so a failure above it can only be reported by
// StartupFailureLog — which is why the stage name is the whole diagnosis for that region.
string startupStage = "cli-dispatch";
string? resolvedLogsPath = null;

try
{

if (Miller.Server.Cli.CliDispatch.IsCliInvocation(args))
{
    var cliContext = WorkspaceContext.Create(Environment.CurrentDirectory, AppContext.BaseDirectory);
    return Miller.Server.Cli.CliDispatch.Run(args, cliContext, Console.Out, Console.Error);
}

startupStage = "resolve-workspace";
var workspacePath = Environment.CurrentDirectory;
var startupWorkspace = WorkspaceBindingResolver.TryResolveStartup(workspacePath);
bool eagerBootstrap = startupWorkspace is not null;

// SAFETY (eager path only): refuse to run in a sensitive system root when cwd/env is the binding source.
// Deferred MCP startup (bad plugin/global cwd) skips this guard and binds from MCP roots on the first tool call.
startupStage = "safety-guard";
if (startupWorkspace?.Source == WorkspaceBindingResolver.WorkspaceSource.Cwd)
{
    workspacePath = Miller.Server.Tools.WorkspaceRootSafety.CanonicalizeAndRejectSensitiveRoot(
        startupWorkspace.Path, fromCwd: true);
}
else if (startupWorkspace is not null)
{
    workspacePath = startupWorkspace.Path;
}

// Deferred launches log to machine-global ~/.miller/logs until MCP roots bind the primary workspace.
startupStage = "create-logs-dir";
var logsPath = eagerBootstrap
    ? Path.Combine(workspacePath, ".miller", "logs")
    : Path.Combine(MillerHome.ResolveMillerDirectory(), "logs");
resolvedLogsPath = logsPath;
Directory.CreateDirectory(logsPath);

int processId = Environment.ProcessId;

// M8 §D4: the minimum level is dialed from MILLER_LOG_LEVEL at startup (no recompile). An unrecognized value
// falls back to Information and is warned ONCE after the logger is built (so the warning is itself logged).
string? logLevelEnv = Environment.GetEnvironmentVariable("MILLER_LOG_LEVEL");
var levelSwitch = new LoggingLevelSwitch(LogLevelParse.ToLevel(logLevelEnv));

// Shared daily .log + .jsonl sinks, the level switch, and cid/pid/role enrichment — all wired in the ONE shared
// MillerLoggingSetup so this host and the sink test exercise the same configuration. Console stays on stderr
// (STDIO purity: nothing but the MCP protocol may touch stdout). One daily pair is shared across all processes,
// so there is no per-pid file pile-up and no startup sweep to run.
startupStage = "build-logger";

// The rolling file sink opens its file LAZILY on the first write and hands an open failure to SelfLog, which
// is a no-op by default. Without this line a locked file, a denied directory, or an unhydrated cloud folder
// produces a perfectly healthy Miller that writes zero bytes — the silent case that is hardest to diagnose.
Serilog.Debugging.SelfLog.Enable(message => Console.Error.WriteLine("miller serilog: " + message));

Log.Logger = MillerLoggingSetup
    .Configure(new LoggerConfiguration(), logsPath, processId, levelSwitch)
    .CreateLogger();

string breadcrumb = StartupBreadcrumb.Format(
    MillerVersion.Current, processId, logsPath, workspacePath, eagerBootstrap, levelSwitch.MinimumLevel.ToString());
Console.Error.WriteLine("miller: " + breadcrumb);
Log.Information("{Breadcrumb}", breadcrumb);

// One-time warn for a typo'd level (the logger is built, so this line itself is captured). A recognized value or
// an absent variable is silent — Information is the honest default.
if (!string.IsNullOrEmpty(logLevelEnv) && !LogLevelParse.WasRecognized(logLevelEnv))
{
    Log.Warning(
        "unknown MILLER_LOG_LEVEL '{Value}', defaulting to Information.", logLevelEnv);
}

startupStage = "build-host";
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSerilog();

// The full host service graph (bootstrap + holder-backed current-workspace services + the M3 background services + the edit/
// workspace tool deps) lives in ONE testable place so the startup graph cannot drift from what the tests pin.
// LIFECYCLE NOTE: the generic host CONSTRUCTS every hosted service before calling StartAsync on any of them, so
// registration order orders StartAsync, NOT construction — no hosted-service constructor may read a bootstrap
// getter (see MillerServiceRegistration's contract + HostStartupRegistrationTests). The MCP transport is wired
// LAST below so it starts only after the bootstrap's StartAsync has seeded the holder/workspace.
builder.Services.AddMillerServices();

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "miller", Version = MillerVersion.Current };
        // Server-level behavioral-adoption guidance (search-before-read, per-tool one-liners, workflows) the
        // client surfaces to the agent. Embedded in the binary; see AgentInstructions + MILLER_AGENT_INSTRUCTIONS.md.
        options.ServerInstructions = AgentInstructions.Load();
    })
    .WithStdioServerTransport()
    .WithTools<SearchTool>()
    .WithTools<InspectTool>()
    .WithTools<ContextTool>()
    .WithTools<TraceTool>()
    .WithTools<ImpactTool>()
    .WithTools<EditTool>()
    .WithTools<ContentTool>()
    .WithTools<PatternsTool>()
    .WithTools<WorkspaceTool>()
    .WithTools<TestsTool>()
    .WithRequestFilters(filters =>
    {
        filters.AddCallToolFilter(WorkspaceBindingCallToolFilter.Create());
        filters.AddCallToolFilter(TelemetryCallToolFilter.Create());
    });

builder.Services.AddSingleton<WorkspaceRootsNotificationService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WorkspaceRootsNotificationService>());

var host = builder.Build();

startupStage = "run-host";
await host.RunAsync();
return 0;

}
catch (Exception startupFailure)
{
    StartupFailureLog.Write(
        startupFailure,
        startupStage,
        StartupFailureLog.CandidateDirectories(resolvedLogsPath, MillerHome.ResolveMillerDirectory()),
        Console.Error,
        DateTimeOffset.UtcNow,
        Environment.ProcessId);
    return 70;
}
finally
{
    Log.CloseAndFlush();
}
