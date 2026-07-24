using System.Text.Json;
using Miller.Indexing;
using Miller.Server.Tools;
using Miller.Server.Workspaces;
using Xunit;

namespace Miller.Tests.Server;

public sealed class ToolDiagnosticIntegrationTests
{
    [Fact]
    public void Search_ProviderFailure_RendersTypedJsonDiagnostic()
    {
        var provider = new ThrowingProvider(new IncompatibleExtractException("schema mismatch"));
        var tool = new SearchTool(provider, provider);

        string output = tool.Search("needle", format: "json");

        AssertDiagnostic(output, "schema_incompatible", "corruption");
    }

    [Fact]
    public void Inspect_ProviderFailure_RendersTypedJsonDiagnostic()
    {
        var provider = new ThrowingProvider(new UnauthorizedAccessException("denied"));
        var tool = new InspectTool(provider);

        string output = tool.Inspect("Target", format: "json");

        AssertDiagnostic(output, "permission_denied", "unavailable");
    }

    [Fact]
    public void Context_ProviderFailure_RendersTypedJsonDiagnostic()
    {
        var provider = new ThrowingProvider(new InvalidDataException("invalid artifact"));
        var tool = new ContextTool(provider);

        string output = tool.Context("find entry points", format: "json");

        AssertDiagnostic(output, "artifact_corrupt", "corruption");
    }

    [Fact]
    public void Trace_ProviderFailure_RendersTypedJsonDiagnostic()
    {
        var provider = new ThrowingProvider(new FileNotFoundException("missing artifact"));
        var tool = new TraceTool(provider);

        string output = tool.Trace("Target", format: "json");

        AssertDiagnostic(output, "artifact_missing", "unavailable");
    }

    [Fact]
    public void Impact_ProviderFailure_RendersTypedJsonDiagnostic()
    {
        var provider = new ThrowingProvider(new IOException("artifact busy"));
        var tool = new ImpactTool(provider);

        string output = tool.Impact(target: "Target", format: "json");

        AssertDiagnostic(output, "artifact_unavailable", "unavailable");
    }

    [Fact]
    public void Patterns_ProviderFailure_RendersTypedJsonDiagnostic()
    {
        var provider = new ThrowingProvider(new IncompatibleExtractException("schema mismatch"));
        var tool = new PatternsTool(provider, new PatternFactsReader());

        string output = tool.Patterns(format: "json");

        AssertDiagnostic(output, "schema_incompatible", "corruption");
    }

    [Fact]
    public void Patterns_UnexpectedInvalidOperation_RendersInternalFailure()
    {
        var provider = new ThrowingProvider(new InvalidOperationException("unexpected patterns state"));
        var tool = new PatternsTool(provider, new PatternFactsReader());

        string output = tool.Patterns(format: "json");

        AssertDiagnostic(output, "internal_failure", "internal_failure");
    }

    [Fact]
    public void Search_SidecarCorruption_RendersCorruptionDiagnostic()
    {
        var provider = new ThrowingProvider(new InvalidDataException("search sidecar is corrupt"));
        var tool = new SearchTool(provider, provider);

        string output = tool.Search("needle", format: "json");

        AssertDiagnostic(output, "artifact_corrupt", "corruption");
    }

    private static void AssertDiagnostic(string output, string code, string diagnosticClass)
    {
        using var document = JsonDocument.Parse(output);
        JsonElement root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schema_version").GetInt32());
        Assert.Equal(code, root.GetProperty("diagnostic").GetProperty("code").GetString());
        Assert.Equal(diagnosticClass, root.GetProperty("diagnostic").GetProperty("class").GetString());
        Assert.Equal("error", root.GetProperty("diagnostic").GetProperty("outcome").GetString());
    }

    private sealed class ThrowingProvider(
        Exception exception)
        : IWorkspaceIndexProvider,
          IWorkspaceSearchProvider,
          IWorkspaceSymbolReadProvider,
          IWorkspaceContentSearchProvider,
          IWorkspaceArtifactProvider
    {
        public WorkspaceReadContext Resolve(string? workspaceId, bool ensureFresh) => throw exception;

        public WorkspaceSymbolSearchContext ResolveSymbolSearch(string? workspaceId, bool ensureFresh) =>
            throw exception;

        public WorkspaceSymbolReadContext ResolveSymbolRead(string? workspaceId, bool ensureFresh) =>
            throw exception;

        public WorkspaceContentSearchContext ResolveContentSearch(string? workspaceId, bool ensureFresh) =>
            throw exception;

        public WorkspaceArtifactContext ResolveArtifact(string? workspaceId, bool ensureFresh) =>
            throw exception;
    }
}
