using System.IO.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Miller.Server.Hosting;
using Miller.Server.Telemetry;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the workspace-binding CallToolFilter: every tools/call must invoke
/// <see cref="IWorkspaceBindingService.EnsurePrimaryBoundAsync"/> before the tool body runs.
/// </summary>
public sealed class WorkspaceBindingCallToolFilterTests
{
    private sealed class RecordingBindingService : IWorkspaceBindingService
    {
        public int EnsureCalls { get; private set; }

        public int BindingGeneration => 1;

        public bool IsDeferred => true;

        public Task WaitUntilBoundAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task EnsurePrimaryBoundAsync(McpServer server, CancellationToken cancellationToken)
        {
            EnsureCalls++;
            return Task.CompletedTask;
        }

        public void MarkRootsDirty() { }
    }

    [Fact]
    public async Task CallToolFilter_InvokesBindingBeforeToolHandler()
    {
        var ct = TestContext.Current.CancellationToken;
        var binding = new RecordingBindingService();

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var services = new ServiceCollection();
        services.AddSingleton<IWorkspaceBindingService>(binding);
        services
            .AddMcpServer(o => { o.ServerInfo = new() { Name = "bind-filter", Version = "0" }; })
            .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream())
            .WithToolsFromAssembly(typeof(PinProbeTool).Assembly)
            .WithRequestFilters(f =>
            {
                f.AddCallToolFilter(WorkspaceBindingCallToolFilter.Create());
            });

        await using var provider = services.BuildServiceProvider();
        var server = provider.GetRequiredService<McpServer>();
        var serverTask = server.RunAsync(ct);

        var clientTransport = new StreamClientTransport(
            clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream(), NullLoggerFactory.Instance);
        await using var client = await McpClient.CreateAsync(clientTransport, cancellationToken: ct);

        await client.CallToolAsync(
            "pin_greet", new Dictionary<string, object?> { ["who"] = "binding" }!, cancellationToken: ct);

        await client.DisposeAsync();
        await clientToServer.Writer.CompleteAsync();
        await serverToClient.Writer.CompleteAsync();
        try { await serverTask.WaitAsync(TimeSpan.FromSeconds(5), ct); } catch (Exception) { }

        Assert.Equal(1, binding.EnsureCalls);
    }
}
