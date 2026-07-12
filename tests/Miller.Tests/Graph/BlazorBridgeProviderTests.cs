using Miller.Core.Contracts;
using Miller.Core.Graph;
using Miller.Core.Resolver;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Graph;

public sealed class BlazorBridgeProviderTests
{
    [Fact]
    public void Build_BlazorProvider_NavigateToMatchesPageDirective()
    {
        var facts = new[]
        {
            Fact(
                "razor-reference",
                BridgeStructuralPatterns.RazorRouteReference,
                "Components/NavMenu.razor",
                new Dictionary<string, string>
                {
                    ["target_path"] = "/edr/form",
                    ["source_kind"] = "navigate_to",
                    ["framework"] = "blazor",
                }),
            Fact(
                "razor-page",
                BridgeStructuralPatterns.RazorPageDirective,
                "Pages/EdrForm.razor",
                new Dictionary<string, string>
                {
                    ["route_template"] = "/edr/form",
                    ["route"] = "/edr/form",
                }),
        };

        var graph = BridgeGraphBuilder.Build(
            [],
            [],
            [],
            [],
            [],
            [FileRouteBridgeProvider.Blazor],
            structuralFacts: facts);

        var edge = Assert.Single(graph.Edges);
        Assert.Equal(BridgeKind.NavigatesTo, edge.Edge.Kind);
        Assert.Equal("/edr/form", edge.Edge.SourceRef.Display);
        Assert.Equal("/edr/form", edge.Edge.TargetRef.Display);
        Assert.Contains("blazor", graph.CapabilityReport.ActiveProviders);
    }

    [Fact]
    public void Build_BlazorProviderWithNoRazorFacts_SkipsProvider()
    {
        var graph = BridgeGraphBuilder.Build([], [], [], [], [], [FileRouteBridgeProvider.Blazor]);

        Assert.Empty(graph.Edges);
        Assert.DoesNotContain("blazor", graph.CapabilityReport.ActiveProviders);
        Assert.Contains(
            graph.CapabilityReport.SkippedProviders,
            provider => provider.ProviderId == "blazor");
    }

    [Fact]
    public void Build_DefaultProviders_IncludesBlazorProvider()
    {
        var graph = BridgeGraphBuilder.Build([], [], [], [], []);

        Assert.Contains(
            graph.CapabilityReport.SkippedProviders,
            provider => provider.ProviderId == "blazor");
    }

    [Fact]
    public void ProvidersForDatabase_NoConfig_IncludesBlazorProvider()
    {
        var root = CreateWorkspace();
        try
        {
            var providers = BridgeProviderSelection.ProvidersForDatabase(Path.Combine(root, ".miller", "symbols.db"));

            Assert.Contains(providers, provider => provider.Id == "blazor");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ProvidersForDatabase_BlazorConfig_SelectsBlazorProvider()
    {
        var root = CreateWorkspace();
        try
        {
            File.WriteAllText(
                Path.Combine(root, "miller.json"),
                """
                {
                  "bridge": {
                    "providers": ["blazor"]
                  }
                }
                """);

            var providers = BridgeProviderSelection.ProvidersForDatabase(Path.Combine(root, ".miller", "symbols.db"));

            var provider = Assert.Single(providers);
            Assert.Equal("blazor", provider.Id);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), $"miller-blazor-provider-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, ".miller"));
        return root;
    }

    private static StructuralFactRecord Fact(
        string id,
        string patternId,
        string path,
        IReadOnlyDictionary<string, string> metadata) =>
        new(
            id,
            patternId,
            "razor",
            path,
            CaptureName: "capture",
            NodeKind: "node",
            ContainingSymbolId: null,
            Span: new StructuralFactSpan(1, 0, 1, 1, 0, 1),
            Confidence: 1.0,
            Metadata: metadata);
}
