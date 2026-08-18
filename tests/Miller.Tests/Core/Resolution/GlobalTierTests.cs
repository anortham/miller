using Miller.Core.Resolution;
using Xunit;

namespace Miller.Tests.Core.Resolution;

public sealed class GlobalTierTests
{
    [Fact]
    public void UniqueFunction_ResolvesAt055()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("fn", "Run", FactSymbolKind.Function);
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(ResolutionCases.Ident(ResolutionRefKind.Call, "Run"));

        Assert.Equal(ResolutionOutcomeKind.Resolved, outcome.Kind);
        Assert.Equal(4, outcome.Tier);
        Assert.Equal(ResolutionPolicy.GlobalMethod, outcome.Method);
        Assert.Equal(0.55, outcome.Confidence);
        Assert.Equal(new FactSymbolKey(1, "fn"), outcome.Target);
    }

    [Fact]
    public void TwoFunctions_AreAmbiguous()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("a", "Run", FactSymbolKind.Function);
        facts.Add("b", "Run", FactSymbolKind.Function, version: 2);
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(ResolutionCases.Ident(ResolutionRefKind.Call, "Run"));

        Assert.Equal(ResolutionOutcomeKind.Ambiguous, outcome.Kind);
        Assert.Equal(2, outcome.CandidateCount);
    }

    [Fact]
    public void Call_ExcludesMethods()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("m", "Run", FactSymbolKind.Method);
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(ResolutionCases.Ident(ResolutionRefKind.Call, "Run"));

        Assert.Equal(ResolutionOutcomeKind.Missing, outcome.Kind);
    }

    [Fact]
    public void EsModule_RestrictsToSameVersion()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("other", "Run", FactSymbolKind.Function, language: "typescript", version: 2);
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(ResolutionRefKind.Call, "Run", language: "typescript"));

        Assert.Equal(ResolutionOutcomeKind.Missing, outcome.Kind);
    }

    [Fact]
    public void EsModule_SameVersionFunction_Resolves()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("fn", "Run", FactSymbolKind.Function, language: "typescript");
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(ResolutionRefKind.Call, "Run", language: "typescript"));

        Assert.Equal(new FactSymbolKey(1, "fn"), outcome.Target);
    }

    [Fact]
    public void MemberAccessAndVariableRef_DisableGlobal()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("p", "Count", FactSymbolKind.Property);
        facts.Add("v", "value", FactSymbolKind.Variable);
        var resolver = new QueryTimeResolver(facts);

        Assert.Equal(
            ResolutionOutcomeKind.NoContext,
            resolver.Resolve(ResolutionCases.Ident(ResolutionRefKind.MemberAccess, "Count")).Kind);
        Assert.Equal(
            ResolutionOutcomeKind.Resolved,
            resolver.Resolve(ResolutionCases.Ident(ResolutionRefKind.VariableRef, "value")).Kind);
        Assert.Equal(
            1,
            resolver.Resolve(ResolutionCases.Ident(ResolutionRefKind.VariableRef, "value")).Tier);
    }

    [Fact]
    public void PendingInstantiates_UsesClassStructConstructor()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("c", "Widget", FactSymbolKind.Class);
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Pend(ResolutionRefKind.Instantiates, "Widget"));

        Assert.Equal(4, outcome.Tier);
        Assert.Equal(new FactSymbolKey(1, "c"), outcome.Target);
    }

    [Fact]
    public void PendingTypeUsage_UsesTypeLike()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("i", "IWidget", FactSymbolKind.Interface);
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Pend(ResolutionRefKind.TypeUsage, "IWidget"));

        Assert.Equal(new FactSymbolKey(1, "i"), outcome.Target);
    }
}
