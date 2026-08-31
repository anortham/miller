using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.ComponentModel.DataAnnotations;
using System.IO.Pipelines;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Miller.Server.Tools;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace Miller.Tests.Server;

public sealed class McpToolSchemaTests
{
    private static class AnnotationProbe
    {
        public static string Probe([Required] string? workspace_id = null) => workspace_id ?? string.Empty;
    }

    [Fact]
    public void RequiredAnnotation_MakesNullableParameterRequired()
    {
        MethodInfo method = typeof(AnnotationProbe).GetMethod(nameof(AnnotationProbe.Probe))!;
        AIFunction function = AIFunctionFactory.Create(method, target: null);

        Assert.True(function.JsonSchema.TryGetProperty("required", out JsonElement required));
        Assert.Contains(required.EnumerateArray(), value => value.GetString() == "workspace_id");
    }

    [Fact]
    public void WorkspaceBoundTools_AdvertiseWorkspaceIdAsRequired()
    {
        Type[] toolTypes =
        [
            typeof(SearchTool),
            typeof(InspectTool),
            typeof(ContextTool),
            typeof(TraceTool),
            typeof(ImpactTool),
            typeof(EditTool),
            typeof(PatternsTool),
            typeof(ContentTool),
            typeof(TestsTool),
        ];

        foreach (Type toolType in toolTypes)
        {
            MethodInfo method = toolType
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Single(static method => method.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolAttribute>() is not null);
            AIFunction function = AIFunctionFactory.Create(method, RuntimeHelpers.GetUninitializedObject(toolType));
            JsonElement schema = function.JsonSchema;

            Assert.True(schema.TryGetProperty("required", out JsonElement required), toolType.Name);
            Assert.Contains(
                required.EnumerateArray(),
                value => value.GetString() == "workspace_id");
        }
    }

    [Fact]
    public async Task ToolsList_AdvertisesWorkspaceIdAsRequiredForWorkspaceBoundTools()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var services = new ServiceCollection();
        services
            .AddMcpServer(options => { options.ServerInfo = new() { Name = "schema", Version = "0" }; })
            .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream())
            .WithTools(
            [
                typeof(SearchTool),
                typeof(InspectTool),
                typeof(ContextTool),
                typeof(TraceTool),
                typeof(ImpactTool),
                typeof(EditTool),
                typeof(PatternsTool),
                typeof(ContentTool),
                typeof(TestsTool),
                typeof(WorkspaceTool),
            ]);

        await using var provider = services.BuildServiceProvider();
        var server = provider.GetRequiredService<McpServer>();
        var serverTask = server.RunAsync(cancellationToken);
        var clientTransport = new StreamClientTransport(
            clientToServer.Writer.AsStream(),
            serverToClient.Reader.AsStream(),
            NullLoggerFactory.Instance);
        await using var client = await McpClient.CreateAsync(clientTransport, cancellationToken: cancellationToken);

        IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
        foreach (string toolName in new[]
        {
            "search", "inspect", "context", "trace", "impact", "edit", "patterns", "content", "tests",
        })
        {
            McpClientTool tool = Assert.Single(tools, candidate => candidate.Name == toolName);
            JsonElement schema = tool.JsonSchema;
            Assert.True(schema.TryGetProperty("required", out JsonElement required), toolName);
            Assert.Contains(required.EnumerateArray(), value => value.GetString() == "workspace_id");
        }

        McpClientTool workspace = Assert.Single(tools, candidate => candidate.Name == "workspace");
        if (workspace.JsonSchema.TryGetProperty("required", out JsonElement requiredWorkspace))
            Assert.DoesNotContain(requiredWorkspace.EnumerateArray(), value => value.GetString() == "workspace_id");

        await client.DisposeAsync();
        await clientToServer.Writer.CompleteAsync();
        await serverToClient.Writer.CompleteAsync();
        try { await serverTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); } catch (Exception) { }
    }

    [Fact]
    public void WorkspaceTool_AdvertisesWorkspaceIdAsOptionalForGlobalOperations()
    {
        MethodInfo method = typeof(WorkspaceTool)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(static method => method.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolAttribute>() is not null);
        AIFunction function = AIFunctionFactory.Create(method, RuntimeHelpers.GetUninitializedObject(typeof(WorkspaceTool)));

        JsonElement schema = function.JsonSchema;
        if (schema.TryGetProperty("required", out JsonElement required))
            Assert.DoesNotContain(required.EnumerateArray(), value => value.GetString() == "workspace_id");
    }
}
