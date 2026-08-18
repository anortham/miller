using Miller.Core.Resolution;
using Xunit;

namespace Miller.Tests.Core.Resolution;

public sealed class LocalTierTests
{
    [Fact]
    public void UniqueScopeVariable_ResolvesAtLocal()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("m", "Run", FactSymbolKind.Method);
        facts.Add("x", "value", FactSymbolKind.Variable, parentId: "m");
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(ResolutionRefKind.VariableRef, "value", scope: "m"));

        Assert.Equal(ResolutionOutcomeKind.Resolved, outcome.Kind);
        Assert.Equal(1, outcome.Tier);
        Assert.Equal(ResolutionPolicy.LocalMethod, outcome.Method);
        Assert.Equal(new FactSymbolKey(1, "x"), outcome.Target);
        Assert.Equal(0.95, outcome.Confidence);
    }

    [Fact]
    public void TwoCompatibleInSameScope_AreAmbiguous()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("m", "Run", FactSymbolKind.Method);
        facts.Add("x1", "value", FactSymbolKind.Variable, parentId: "m");
        facts.Add("x2", "value", FactSymbolKind.Field, parentId: "m");
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(ResolutionRefKind.VariableRef, "value", scope: "m"));

        Assert.Equal(ResolutionOutcomeKind.Ambiguous, outcome.Kind);
        Assert.Equal(2, outcome.CandidateCount);
    }

    [Fact]
    public void WalksToParentWhenInnerHasNoCompatibleMatch()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("outer", "Outer", FactSymbolKind.Method);
        facts.Add("inner", "Inner", FactSymbolKind.Method, parentId: "outer");
        facts.Add("x", "value", FactSymbolKind.Variable, parentId: "outer");
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(ResolutionRefKind.VariableRef, "value", scope: "inner"));

        Assert.Equal(new FactSymbolKey(1, "x"), outcome.Target);
    }

    [Fact]
    public void FallsBackToTopLevelWhenScopeChainIsEmpty()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("x", "value", FactSymbolKind.Variable);
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(ResolutionRefKind.VariableRef, "value", scope: "missing"));

        Assert.Equal(new FactSymbolKey(1, "x"), outcome.Target);
    }

    [Fact]
    public void KindIncompatibleNameAtInnerLevel_DoesNotStopTheWalk()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("outer", "Outer", FactSymbolKind.Function);
        facts.Add("inner", "Inner", FactSymbolKind.Function, parentId: "outer");
        facts.Add("methodX", "x", FactSymbolKind.Method, parentId: "inner");
        facts.Add("varX", "x", FactSymbolKind.Variable, parentId: "outer");
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(ResolutionRefKind.VariableRef, "x", scope: "inner"));

        Assert.Equal(ResolutionOutcomeKind.Resolved, outcome.Kind);
        Assert.Equal(new FactSymbolKey(1, "varX"), outcome.Target);
    }

    [Fact]
    public void IdentifierCall_DoesNotUseLocal()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("m", "Run", FactSymbolKind.Method);
        facts.Add("fn", "work", FactSymbolKind.Function, parentId: "m");
        facts.Add("g1", "work", FactSymbolKind.Function);
        facts.Add("g2", "work", FactSymbolKind.Function, version: 2);
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(ResolutionRefKind.Call, "work", scope: "m"));

        Assert.Equal(ResolutionOutcomeKind.Ambiguous, outcome.Kind);
    }

    [Fact]
    public void NoCompatibleCandidates_AreMissing()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("m", "Run", FactSymbolKind.Method);
        facts.Add("fn", "value", FactSymbolKind.Method, parentId: "m");
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(ResolutionRefKind.VariableRef, "value", scope: "m"));

        Assert.Equal(ResolutionOutcomeKind.Missing, outcome.Kind);
    }
}
