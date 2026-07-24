using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Miller.Indexing;
using Miller.Indexing.Semantic;
using Miller.SemanticModelEval;
using Miller.Server;
using Miller.Server.Hosting;
using Miller.Server.Telemetry;
using Miller.Server.Tools;
using ModelContextProtocol.Server;

if (args is ["version"])
{
    Console.WriteLine($"miller-semantic-model-eval {MillerVersion.Current}");
    return 0;
}

bool productionAdapter = args is ["--production"];
string configPath = "";
string? evidencePath = null;
string? error = null;
if (!productionAdapter
    && !TryParse(args, out configPath, out evidencePath, out error))
{
    Console.Error.WriteLine(error);
    return 2;
}

SemanticMode mode = SemanticActivation.FromEnvironment();
SemanticEvaluationAdapter? adapter = null;
if (!productionAdapter)
{
    try
    {
        adapter = SemanticEvaluationAdapter.LoadWhenEnabled(mode, configPath);
        if (adapter is not null && evidencePath is not null)
            adapter.WriteEvidence(evidencePath);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
    {
        Console.Error.WriteLine(ex.Message);
        return 2;
    }
}

string workspacePath = Environment.CurrentDirectory;
WorkspaceBindingResolver.ResolvedWorkspace? startupWorkspace =
    WorkspaceBindingResolver.TryResolveStartup(workspacePath);
if (startupWorkspace?.Source == WorkspaceBindingResolver.WorkspaceSource.Cwd)
{
    Miller.Server.Tools.WorkspaceRootSafety.RejectSensitiveRoot(
        startupWorkspace.Path,
        fromCwd: true);
}

HostApplicationBuilder builder = Host.CreateApplicationBuilder([]);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddMillerServices(adapter, mode, startIndexer: false);
builder.Services.AddSingleton<EvaluationWorkspaceLeaseService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<EvaluationWorkspaceLeaseService>());
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "miller-semantic-model-eval", Version = MillerVersion.Current };
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
    .WithRequestFilters(filters =>
    {
        filters.AddCallToolFilter(EvaluationReadOnlyCallToolFilter.Create());
        filters.AddCallToolFilter(WorkspaceBindingCallToolFilter.Create());
        filters.AddCallToolFilter(TelemetryCallToolFilter.Create());
    });
builder.Services.AddSingleton<WorkspaceRootsNotificationService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WorkspaceRootsNotificationService>());

await builder.Build().RunAsync();
return 0;

static bool TryParse(
    IReadOnlyList<string> values,
    out string configPath,
    out string? evidencePath,
    out string? error)
{
    configPath = "";
    evidencePath = null;
    error = null;

    for (int i = 0; i < values.Count; i++)
    {
        if (string.Equals(values[i], "--config", StringComparison.Ordinal)
            && i + 1 < values.Count)
        {
            configPath = values[++i];
            continue;
        }
        if (string.Equals(values[i], "--evidence", StringComparison.Ordinal)
            && i + 1 < values.Count)
        {
            evidencePath = values[++i];
            continue;
        }

        error = $"unknown or incomplete argument '{values[i]}'";
        return false;
    }

    if (string.IsNullOrWhiteSpace(configPath))
    {
        error = "usage: miller-semantic-model-eval --config <runtime.json> [--evidence <identity.json>]";
        return false;
    }

    return true;
}
