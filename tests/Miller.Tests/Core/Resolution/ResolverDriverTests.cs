using Miller.Core.Resolution;
using Xunit;

namespace Miller.Tests.Core.Resolution;

public sealed class ResolverDriverTests
{
    [Fact]
    public void EmptyName_IsNoContext()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("g", "Widget", FactSymbolKind.Class);
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(Ident(ResolutionRefKind.TypeUsage, name: ""));

        Assert.Equal(ResolutionOutcomeKind.NoContext, outcome.Kind);
    }

    [Fact]
    public void IdentifierInstantiates_IsNoContext()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("c", "Widget", FactSymbolKind.Class);
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(Ident(ResolutionRefKind.Instantiates, "Widget"));

        Assert.Equal(ResolutionOutcomeKind.NoContext, outcome.Kind);
    }

    [Fact]
    public void IdentifierMemberAccessWithoutReceiver_IsNoContext()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("m", "Count", FactSymbolKind.Property);
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(Ident(ResolutionRefKind.MemberAccess, "Count"));

        Assert.Equal(ResolutionOutcomeKind.NoContext, outcome.Kind);
    }

    [Fact]
    public void PendingVariableRef_IsNoContext()
    {
        var resolver = new QueryTimeResolver(new MemoryResolutionFacts());

        ResolutionOutcome outcome = resolver.Resolve(Pend(ResolutionRefKind.VariableRef, "x"));

        Assert.Equal(ResolutionOutcomeKind.NoContext, outcome.Kind);
    }

    [Fact]
    public void PendingMemberAccessWithoutReceiver_AttemptsAndIsMissing()
    {
        var resolver = new QueryTimeResolver(new MemoryResolutionFacts());

        ResolutionOutcome outcome = resolver.Resolve(Pend(ResolutionRefKind.MemberAccess, "Count"));

        Assert.Equal(ResolutionOutcomeKind.Missing, outcome.Kind);
    }

    [Fact]
    public void AttemptedTiersWithNoCandidates_AreMissing()
    {
        var resolver = new QueryTimeResolver(new MemoryResolutionFacts());

        ResolutionOutcome outcome = resolver.Resolve(Ident(ResolutionRefKind.Call, "Missing"));

        Assert.Equal(ResolutionOutcomeKind.Missing, outcome.Kind);
    }

    [Fact]
    public void ImportTier_IsSkippedUnlessTypescriptOrJavascript()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("cs", "Widget", FactSymbolKind.Class, language: "csharp", version: 2);
        facts.Add("tsSym", "Widget", FactSymbolKind.Class, language: "typescript", version: 2);
        facts.Add("jsSym", "Widget", FactSymbolKind.Class, language: "javascript", version: 2);
        facts.AddImport(
            ImportBinding.FromSymbol("Widget", new ImportMetadata(Source: "./w"), moduleVersionId: 2),
            version: 1);
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome csharp = resolver.Resolve(Ident(ResolutionRefKind.TypeUsage, "Widget", language: "csharp"));
        ResolutionOutcome tsx = resolver.Resolve(Ident(ResolutionRefKind.TypeUsage, "Widget", language: "tsx"));
        ResolutionOutcome jsx = resolver.Resolve(Ident(ResolutionRefKind.TypeUsage, "Widget", language: "jsx"));
        ResolutionOutcome ts = resolver.Resolve(Ident(ResolutionRefKind.TypeUsage, "Widget", language: "typescript"));
        ResolutionOutcome js = resolver.Resolve(Ident(ResolutionRefKind.TypeUsage, "Widget", language: "javascript"));

        Assert.Equal(ResolutionOutcomeKind.Resolved, csharp.Kind);
        Assert.Equal(4, csharp.Tier);
        Assert.Equal(ResolutionPolicy.GlobalMethod, csharp.Method);
        Assert.Equal(ResolutionOutcomeKind.Missing, tsx.Kind);
        Assert.Equal(ResolutionOutcomeKind.Missing, jsx.Kind);
        Assert.Equal(ResolutionOutcomeKind.Resolved, ts.Kind);
        Assert.Equal(2, ts.Tier);
        Assert.Equal(ResolutionPolicy.ImportMethod, ts.Method);
        Assert.Equal(ResolutionOutcomeKind.Resolved, js.Kind);
        Assert.Equal(2, js.Tier);
    }

    [Fact]
    public void LaterExactlyOneWin_BeatsEarlierAmbiguousTier()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("a", "dup", FactSymbolKind.Function, language: "typescript", version: 2);
        facts.Add("b", "dup", FactSymbolKind.Function, language: "typescript", version: 2);
        facts.Add("g", "dup", FactSymbolKind.Function, language: "typescript", version: 1);
        facts.AddImport(
            ImportBinding.FromSymbol("dup", new ImportMetadata(Source: "./m"), moduleVersionId: 2),
            version: 1);
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            Ident(ResolutionRefKind.Call, "dup", language: "typescript"));

        Assert.Equal(ResolutionOutcomeKind.Resolved, outcome.Kind);
        Assert.Equal(4, outcome.Tier);
        Assert.Equal(new FactSymbolKey(1, "g"), outcome.Target);
    }

    [Fact]
    public void FirstAmbiguous_IsKeptWhenNoLaterWin()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("a", "dup", FactSymbolKind.Function, language: "typescript", version: 2);
        facts.Add("b", "dup", FactSymbolKind.Function, language: "typescript", version: 2);
        facts.Add("g1", "dup", FactSymbolKind.Function, language: "typescript", version: 1);
        facts.Add("g2", "dup", FactSymbolKind.Function, language: "typescript", version: 1);
        facts.AddImport(
            ImportBinding.FromSymbol("dup", new ImportMetadata(Source: "./m"), moduleVersionId: 2),
            version: 1);
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            Ident(ResolutionRefKind.Call, "dup", language: "typescript"));

        Assert.Equal(ResolutionOutcomeKind.Ambiguous, outcome.Kind);
        Assert.Equal(2, outcome.CandidateCount);
        Assert.Null(outcome.Target);
        Assert.Null(outcome.Tier);
    }

    [Fact]
    public void IdentifierTypeUsageWithReceiver_DoesNotRunReceiverTier()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("scope", "M", FactSymbolKind.Method, signature: "void M()");
        facts.Add("recv", "box", FactSymbolKind.Variable, parentId: "scope");
        facts.Add("Box", "Box", FactSymbolKind.Class);
        facts.Add("member", "Widget", FactSymbolKind.Class, parentId: "Box");
        facts.Add("other", "Widget", FactSymbolKind.Class);
        facts.AddTypeFact("recv", "Box");
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            Ident(ResolutionRefKind.TypeUsage, "Widget", receiver: "box", scope: "scope"));

        Assert.Equal(ResolutionOutcomeKind.Ambiguous, outcome.Kind);
        Assert.Null(outcome.Method);
    }

    [Fact]
    public void IdentifierTypeUsageWithReceiver_StillRunsImportStaticAndGlobal()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("scope", "M", FactSymbolKind.Method);
        facts.Add("recv", "box", FactSymbolKind.Variable, parentId: "scope");
        facts.Add("Box", "Box", FactSymbolKind.Class);
        facts.Add("nested", "Inner", FactSymbolKind.Class, parentId: "Box");
        facts.Add("g", "Widget", FactSymbolKind.Class);
        facts.AddTypeFact("recv", "Box");
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            Ident(ResolutionRefKind.TypeUsage, "Widget", receiver: "box", scope: "scope"));

        Assert.Equal(ResolutionOutcomeKind.Resolved, outcome.Kind);
        Assert.Equal(4, outcome.Tier);
        Assert.Equal(new FactSymbolKey(1, "g"), outcome.Target);
    }

    [Fact]
    public void StoredConfidence_IsMinOfTierAndSource()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("x", "value", FactSymbolKind.Variable);
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome lowSource = resolver.Resolve(
            Ident(ResolutionRefKind.VariableRef, "value", confidence: 0.40));
        ResolutionOutcome highSource = resolver.Resolve(
            Ident(ResolutionRefKind.VariableRef, "value", confidence: 0.99));

        Assert.Equal(0.40, lowSource.Confidence);
        Assert.Equal(ResolutionPolicy.LocalConfidence, highSource.Confidence);
    }

    [Fact]
    public void ChainOrder_ImportBeatsGlobalWhenBothUnique()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("imp", "Widget", FactSymbolKind.Class, language: "typescript", version: 2);
        facts.Add("g", "Widget", FactSymbolKind.Class, language: "typescript", version: 1);
        facts.AddImport(
            ImportBinding.FromSymbol("Widget", new ImportMetadata(Source: "./w"), moduleVersionId: 2),
            version: 1);
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            Ident(ResolutionRefKind.TypeUsage, "Widget", language: "typescript"));

        Assert.Equal(2, outcome.Tier);
        Assert.Equal(new FactSymbolKey(2, "imp"), outcome.Target);
    }

    private static ResolutionInput Ident(
        ResolutionRefKind kind,
        string name,
        string language = "csharp",
        long version = 1,
        string? receiver = null,
        string? qualifier = null,
        string? scope = null,
        double confidence = 1.0) =>
        new(ResolutionOrigin.Identifier, kind, language, version, name, receiver, qualifier, scope, confidence);

    private static ResolutionInput Pend(
        ResolutionRefKind kind,
        string name,
        string language = "csharp",
        long version = 1,
        string? receiver = null,
        string? qualifier = null,
        string? scope = null,
        double confidence = 1.0) =>
        new(ResolutionOrigin.Pending, kind, language, version, name, receiver, qualifier, scope, confidence);
}
