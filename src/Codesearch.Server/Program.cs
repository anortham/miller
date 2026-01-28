using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;
using Serilog;
using Codesearch.Embeddings;
using Codesearch.Server.Memory;
using Codesearch.Server.Registry;
using Codesearch.Server.Services;

// Get workspace path (current directory)
var workspacePath = Environment.CurrentDirectory;
var dbPath = Path.Combine(workspacePath, ".codesearch", "index.lance");
var logsPath = Path.Combine(workspacePath, ".codesearch", "logs");
Directory.CreateDirectory(logsPath);

// Configure Serilog with daily rolling file + stderr console
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(standardErrorFromLevel: Serilog.Events.LogEventLevel.Verbose)
    .WriteTo.File(
        Path.Combine(logsPath, "codesearch-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSerilog();

// Register services with factory methods for non-DI parameters
builder.Services.AddSingleton<EmbeddingService>(sp =>
{
    var service = new EmbeddingService();
    // Start model download in background (non-blocking)
    _ = Task.Run(async () =>
    {
        try
        {
            await service.EnsureReadyAsync();
            Console.Error.WriteLine("Embedding model ready for semantic search.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: Could not load embedding model: {ex.Message}");
            Console.Error.WriteLine("Semantic search unavailable. Text search still works.");
        }
    });
    return service;
});

builder.Services.AddSingleton<SearchService>(sp =>
{
    var embeddingService = sp.GetRequiredService<EmbeddingService>();
    return new SearchService(dbPath, embeddingService);
});

builder.Services.AddSingleton<IndexService>(sp =>
{
    var searchService = sp.GetRequiredService<SearchService>();
    var embeddingService = sp.GetRequiredService<EmbeddingService>();
    return new IndexService(searchService, embeddingService, workspacePath);
});

builder.Services.AddSingleton<ClosureService>(sp =>
{
    var searchService = sp.GetRequiredService<SearchService>();
    return new ClosureService(searchService);
});

builder.Services.AddSingleton<MemoryService>();
builder.Services.AddSingleton<RegistryService>();
builder.Services.AddSingleton<CrossProjectService>();
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
