using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Miller.Indexing;
using Miller.Server;
using Miller.Server.Hosting;
using Miller.Server.Resolution;
using Miller.Server.Telemetry;
using ModelContextProtocol.Server;
using Serilog;

// Miller MCP server host bootstrap (M2).
// IndexBootstrapService (an IHostedService registered BEFORE the MCP host) builds the in-memory index from
// the julie extract and opens the telemetry ledger before the stdio transport accepts any tools/call. The
// search/inspect tools are auto-discovered from this assembly via WithToolsFromAssembly(); the ONE central
// telemetry CallToolFilter wraps every call.
//
// STDIO PURITY: nothing may touch stdout except the MCP protocol. Serilog Console is routed to stderr; never
// Console.WriteLine anywhere in this process.

var workspacePath = Environment.CurrentDirectory;
var logsPath = Path.Combine(workspacePath, ".miller", "logs");
Directory.CreateDirectory(logsPath);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(standardErrorFromLevel: Serilog.Events.LogEventLevel.Verbose)
    .WriteTo.File(
        Path.Combine(logsPath, "miller-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSerilog();

// The bootstrap holds the built singletons. Registered as a singleton AND as the FIRST hosted service so its
// StartAsync (index build + ledger open + holder seed + canonical-root resolve) completes before any other
// hosted service (the indexer, the freshness poller, the MCP transport) starts.
builder.Services.AddSingleton<IndexBootstrapService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<IndexBootstrapService>());

// The tool dependencies are resolved from the bootstrap holder. The generic host runs hosted services in
// registration order, so the holder is always populated before a tool (constructed per-call) reads it. Tools
// depend on the live IndexHolder (M3 step 10) and read holder.Current per call so a freshness Swap is seen.
builder.Services.AddSingleton(sp => sp.GetRequiredService<IndexBootstrapService>().Holder);
builder.Services.AddSingleton(sp => sp.GetRequiredService<IndexBootstrapService>().Resolver);
builder.Services.AddSingleton(sp => sp.GetRequiredService<IndexBootstrapService>().Workspace);
builder.Services.AddSingleton(sp => sp.GetRequiredService<IndexBootstrapService>().Ledger);

// M3 hosted services (registered AFTER the bootstrap so its StartAsync seeds the holder + canonical root first):
//  - IndexerService: leader-gated FileSystemWatcher -> extract update/delete/scan (the writer side).
//  - FreshnessService: poll canonical_revisions -> rebuild + atomic swap (how every instance picks up writes).
// Both are registered as singletons too so the index_fresh probe can read their live state.
builder.Services.AddSingleton<FreshnessService>();
builder.Services.AddSingleton<IndexerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<FreshnessService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<IndexerService>());

// The index_fresh probe (decision-8) the telemetry filter reads per call: built revision vs. the freshness
// service's last-observed revision AND the indexer's queue-empty state. Cheap — no SQLite on the tool hot path.
builder.Services.AddSingleton(sp =>
{
    var holder = sp.GetRequiredService<IndexHolder>();
    var freshness = sp.GetRequiredService<FreshnessService>();
    var indexer = sp.GetRequiredService<IndexerService>();
    return new IndexFreshProbe(
        holder,
        latestRevision: () => freshness.LatestObservedRevision,
        queueEmpty: () => indexer.QueueEmpty);
});

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "miller", Version = "0.1.0" };
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly()
    // The ONE central telemetry interceptor — wraps every tools/call including reflection-discovered tools.
    .WithRequestFilters(filters => filters.AddCallToolFilter(TelemetryCallToolFilter.Create()));

var host = builder.Build();
await host.RunAsync();
