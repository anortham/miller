using Miller.Core.Resolution;
using Xunit;

namespace Miller.Tests.Core.Resolution;

public sealed class SpikeRegressionTests
{
    [Fact]
    public void SpanLocate_UsesBytesWhenBothPresent_NotReferenceSiteId()
    {
        PropagationCandidate[] candidates =
        [
            new("Parse", StartByte: 40, EndByte: 45, StartLine: 10),
            new("Parse", StartByte: 80, EndByte: 85, StartLine: 10),
        ];

        Assert.Equal(1, PropagationLocator.Locate(candidates, "Parse", startByte: 70, endByte: 90, startLine: 10));
        Assert.Equal(1, PropagationLocator.Locate(candidates, "Parse", startByte: 70, endByte: 90, startLine: 99));
    }

    [Fact]
    public void DottedNamespaceFlattening_AllowsQualifierSuffixMatch()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("root", "Miller", FactSymbolKind.Namespace);
        facts.Add("leaf", "Server.Cli", FactSymbolKind.Namespace, parentId: "root");
        facts.Add("Text", "Text", FactSymbolKind.Class, parentId: "leaf", visibility: "public");
        facts.Add("parse", "Parse", FactSymbolKind.Method, parentId: "Text", isStatic: true);
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(
                ResolutionRefKind.MemberAccess,
                "Parse",
                receiver: "Text",
                qualifier: "Server.Cli"));

        Assert.Equal(ResolutionOutcomeKind.Resolved, outcome.Kind);
        Assert.Equal(new FactSymbolKey(1, "parse"), outcome.Target);
    }

    [Fact]
    public void ScopeWalkStop_CountsOnlyKindCompatibleCandidates()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("file", "bundle", FactSymbolKind.Function, language: "javascript");
        facts.Add("inner", "min", FactSymbolKind.Function, language: "javascript", parentId: "file");
        facts.Add("fnX", "x", FactSymbolKind.Function, language: "javascript", parentId: "inner");
        facts.Add("varX", "x", FactSymbolKind.Variable, language: "javascript", parentId: "file");
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(ResolutionRefKind.VariableRef, "x", language: "javascript", scope: "inner"));

        Assert.Equal(ResolutionOutcomeKind.Resolved, outcome.Kind);
        Assert.Equal(new FactSymbolKey(1, "varX"), outcome.Target);
    }
}
