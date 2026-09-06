using Xunit;

namespace Miller.Tests.Conventions;

public sealed class ReferenceEvidenceOwnershipConventionTests
{
    [Theory]
    [InlineData("src/Miller.Server/Tools/Context/ContextBundleBuilder.cs")]
    [InlineData("src/Miller.Server/Tools/Context/ContextQueryService.cs")]
    [InlineData("src/Miller.Server/Tools/TraceTool.cs")]
    public void ReferenceToolsDoNotFabricateLegacyProducerEvidence(string relativePath)
    {
        string source = File.ReadAllText(Path.Combine(ScaleTestSupport.RepoRoot(), relativePath));

        Assert.DoesNotContain("legacy_name_projection", source, StringComparison.Ordinal);
        Assert.DoesNotContain("$\"legacy:", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Func<IndexedSymbol, IReadOnlyList<SymbolRef>>", source, StringComparison.Ordinal);
    }
}
