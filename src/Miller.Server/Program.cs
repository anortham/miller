using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;
using Serilog;

// Miller MCP server host bootstrap.
// M0 skeleton: Serilog + an empty MCP stdio host. Indexing services are wired in M1;
// the search/inspect tools (and the rest of the 7-tool surface) arrive from M2 onward and are
// auto-discovered from this assembly via WithToolsFromAssembly().

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

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "miller", Version = "0.1.0" };
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

var host = builder.Build();
await host.RunAsync();
