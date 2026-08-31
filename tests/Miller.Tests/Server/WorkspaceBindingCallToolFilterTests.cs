using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Miller.Server.Telemetry;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace Miller.Tests.Server;

public sealed class WorkspaceBindingCallToolFilterTests
{
    [McpServerToolType]
    public sealed class PolicyProbeTool
    {
        public static int Calls;

        [McpServerTool(Name = "search")]
        public static string Search(string? workspace_id = null)
        {
            Interlocked.Increment(ref Calls);
            return workspace_id ?? "handled";
        }
    }

    [Fact]
    public async Task MissingWorkspaceId_ReturnsTypedErrorBeforeToolConstruction()
    {
        int nextCalls = 0;

        CallToolResult result = await InvokeFilterAsync(
            "search",
            arguments: null,
            (_, _) =>
            {
                nextCalls++;
                return Task.FromResult(TextResult("unreachable"));
            });

        Assert.True(result.IsError);
        Assert.Equal(0, nextCalls);
        Assert.Contains("diagnostic_code=workspace_id_required", ResultText(result), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("current")]
    [InlineData("primary")]
    public async Task ImplicitWorkspaceSelector_ReturnsTypedErrorBeforeToolConstruction(string selector)
    {
        int nextCalls = 0;

        CallToolResult result = await InvokeFilterAsync(
            "inspect",
            Arguments(("workspace_id", selector)),
            (_, _) =>
            {
                nextCalls++;
                return Task.FromResult(TextResult("unreachable"));
            });

        Assert.True(result.IsError);
        Assert.Equal(0, nextCalls);
        Assert.Contains(
            "diagnostic_code=implicit_workspace_selector_refused",
            ResultText(result),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplicitWorkspaceSelector_InvokesToolHandler()
    {
        int nextCalls = 0;

        CallToolResult result = await InvokeFilterAsync(
            "context",
            Arguments(("workspace_id", "registered-workspace")),
            (_, _) =>
            {
                nextCalls++;
                return Task.FromResult(TextResult("handled"));
            });

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(1, nextCalls);
        Assert.Equal("handled", ResultText(result));
    }

    [Fact]
    public async Task WorkspaceOpenWithoutWorkspaceId_InvokesToolHandler()
    {
        int nextCalls = 0;

        CallToolResult result = await InvokeFilterAsync(
            "workspace",
            Arguments(("operation", "open"), ("path", "/tmp/registered-workspace")),
            (_, _) =>
            {
                nextCalls++;
                return Task.FromResult(TextResult("opened"));
            });

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(1, nextCalls);
        Assert.Equal("opened", ResultText(result));
    }

    [Fact]
    public async Task ContentAllSearch_InvokesToolHandler()
    {
        int nextCalls = 0;

        CallToolResult result = await InvokeFilterAsync(
            "content",
            Arguments(("operation", "search"), ("workspace_id", "all")),
            (_, _) =>
            {
                nextCalls++;
                return Task.FromResult(TextResult("searched"));
            });

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(1, nextCalls);
        Assert.Equal("searched", ResultText(result));
    }

    [Fact]
    public async Task UnknownTool_IsNotSubjectToWorkspaceTargetPolicy()
    {
        int nextCalls = 0;

        CallToolResult result = await InvokeFilterAsync(
            "pin_greet",
            Arguments(("workspace_id", "current")),
            (_, _) =>
            {
                nextCalls++;
                return Task.FromResult(TextResult("greeted"));
            });

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(1, nextCalls);
        Assert.Equal("greeted", ResultText(result));
    }

    [Fact]
    public async Task JsonDiagnostic_UsesStructuredRendererOutput()
    {
        CallToolResult result = await InvokeFilterAsync(
            "search",
            Arguments(("format", "json")),
            (_, _) => Task.FromResult(TextResult("unreachable")));

        using JsonDocument json = JsonDocument.Parse(ResultText(result));
        Assert.Equal("search", json.RootElement.GetProperty("tool").GetString());
        Assert.Equal(
            "workspace_id_required",
            json.RootElement.GetProperty("diagnostic").GetProperty("code").GetString());
    }

    [Fact]
    public async Task SdkCallWithoutWorkspaceId_ReturnsBeforeToolConstruction()
    {
        Interlocked.Exchange(ref PolicyProbeTool.Calls, 0);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var services = new ServiceCollection();
        services
            .AddMcpServer(options => { options.ServerInfo = new() { Name = "target-filter-sdk", Version = "0" }; })
            .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream())
            .WithTools<PolicyProbeTool>()
            .WithRequestFilters(filters => filters.AddCallToolFilter(WorkspaceBindingCallToolFilter.Create()));

        await using var provider = services.BuildServiceProvider();
        var server = provider.GetRequiredService<McpServer>();
        var serverTask = server.RunAsync(cancellationToken);
        var clientTransport = new StreamClientTransport(
            clientToServer.Writer.AsStream(),
            serverToClient.Reader.AsStream(),
            NullLoggerFactory.Instance);
        await using var client = await McpClient.CreateAsync(clientTransport, cancellationToken: cancellationToken);

        CallToolResult result = await client.CallToolAsync(
            "search",
            new Dictionary<string, object?>(),
            cancellationToken: cancellationToken);

        Assert.True(result.IsError);
        Assert.Equal(0, Volatile.Read(ref PolicyProbeTool.Calls));
        Assert.Contains("diagnostic_code=workspace_id_required", ResultText(result), StringComparison.Ordinal);

        await client.DisposeAsync();
        await clientToServer.Writer.CompleteAsync();
        await serverToClient.Writer.CompleteAsync();
        try { await serverTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); } catch (Exception) { }
    }

    private static async Task<CallToolResult> InvokeFilterAsync(
        string toolName,
        Dictionary<string, JsonElement>? arguments,
        Func<RequestContext<CallToolRequestParams>, CancellationToken, Task<CallToolResult>> next)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var services = new ServiceCollection();
        services
            .AddMcpServer(options => { options.ServerInfo = new() { Name = "target-filter", Version = "0" }; })
            .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());

        await using var provider = services.BuildServiceProvider();
        var server = provider.GetRequiredService<McpServer>();
        var request = new RequestContext<CallToolRequestParams>(
            server,
            new JsonRpcRequest { Method = "tools/call" },
            new CallToolRequestParams { Name = toolName, Arguments = arguments });
        var filtered = WorkspaceBindingCallToolFilter.Create()(
            async (context, ct) => await next(context, ct));
        return await filtered(request, cancellationToken);
    }

    private static Dictionary<string, JsonElement> Arguments(params (string Name, object? Value)[] values) =>
        values.ToDictionary(
            static pair => pair.Name,
            static pair => JsonSerializer.SerializeToElement(pair.Value));

    private static CallToolResult TextResult(string text) =>
        new()
        {
            Content = [new TextContentBlock { Text = text }],
        };

    private static string ResultText(CallToolResult result) =>
        Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
}
