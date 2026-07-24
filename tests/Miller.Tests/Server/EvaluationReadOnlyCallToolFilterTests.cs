using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Miller.SemanticModelEval;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace Miller.Tests.Server;

public sealed class EvaluationReadOnlyCallToolFilterTests
{
    [Theory]
    [InlineData("edit", null, null)]
    [InlineData("workspace", "operation", "refresh")]
    [InlineData("workspace", "operation", "full")]
    [InlineData("content", "operation", "import")]
    [InlineData("search", "workspace_id", "another-workspace")]
    public async Task MutationAndRefreshPaths_AreRefused(
        string tool,
        string? argumentName,
        string? argumentValue)
    {
        CallToolResult result = await InvokeAsync(tool, argumentName, argumentValue);

        Assert.True(result.IsError);
        Assert.Contains(
            "Semantic model evaluation is read-only",
            Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("search", null, null)]
    [InlineData("workspace", "operation", "health")]
    [InlineData("content", "operation", "search")]
    [InlineData("patterns", "workspace_id", "current")]
    public async Task ReadOnlyPaths_ReachTheTool(
        string tool,
        string? argumentName,
        string? argumentValue)
    {
        CallToolResult result = await InvokeAsync(tool, argumentName, argumentValue);

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(
            "next",
            Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }

    private static async Task<CallToolResult> InvokeAsync(
        string tool,
        string? argumentName,
        string? argumentValue)
    {
        var services = new ServiceCollection();
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        services
            .AddMcpServer(options =>
                options.ServerInfo = new() { Name = "evaluation-filter-test", Version = "0" })
            .WithStreamServerTransport(
                clientToServer.Reader.AsStream(),
                serverToClient.Writer.AsStream());

        await using var provider = services.BuildServiceProvider();
        var server = provider.GetRequiredService<McpServer>();
        Dictionary<string, JsonElement>? arguments = argumentName is null
            ? null
            : new Dictionary<string, JsonElement>
            {
                [argumentName] = JsonSerializer.SerializeToElement(argumentValue),
            };
        var request = new RequestContext<CallToolRequestParams>(
            server,
            new JsonRpcRequest { Method = "tools/call" },
            new CallToolRequestParams { Name = tool, Arguments = arguments });
        var filtered = EvaluationReadOnlyCallToolFilter.Create()(
            (_, _) => ValueTask.FromResult(
                new CallToolResult
                {
                    Content = [new TextContentBlock { Text = "next" }],
                }));

        return await filtered(request, TestContext.Current.CancellationToken);
    }
}
