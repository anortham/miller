using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Codesearch.Server.Memory;
using Codesearch.Server.Registry;
using Codesearch.Server.Services;

var builder = Host.CreateApplicationBuilder(args);

// Log to stderr (MCP uses stdout for protocol)
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

// Register services
builder.Services.AddSingleton<SearchService>();
builder.Services.AddSingleton<IndexService>();
builder.Services.AddSingleton<MemoryService>();
builder.Services.AddSingleton<RegistryService>();
builder.Services.AddHostedService<FileWatcherService>();

// Configure MCP server
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new()
        {
            Name = "codesearch",
            Version = "0.1.0"
        };
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

var host = builder.Build();

// Register current project in central registry
var registry = host.Services.GetRequiredService<RegistryService>();
registry.RegisterProject(Environment.CurrentDirectory);

await host.RunAsync();
