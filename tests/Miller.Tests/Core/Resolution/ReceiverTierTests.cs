using Miller.Core.Resolution;
using Xunit;

namespace Miller.Tests.Core.Resolution;

public sealed class ReceiverTierTests
{
    [Fact]
    public void ReceiverTypeFact_BindsEnclosingTypeMemberAt075()
    {
        var facts = ServiceWithPing();
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(ResolutionRefKind.Call, "Ping", receiver: "this", scope: "run", receiverType: "Service"));

        Assert.Equal(ResolutionOutcomeKind.Resolved, outcome.Kind);
        Assert.Equal(3, outcome.Tier);
        Assert.Equal(ResolutionPolicy.ReceiverMethod, outcome.Method);
        Assert.Equal(0.75, outcome.Confidence);
        Assert.Equal(new FactSymbolKey(1, "ping"), outcome.Target);
    }

    [Fact]
    public void ReceiverTypeFact_PendingCallBindsNamedBaseTypeMember()
    {
        var facts = ServiceWithPing();
        facts.Add("Base", "BaseService", FactSymbolKind.Class);
        facts.Add("base-ping", "Ping", FactSymbolKind.Method, parentId: "Base");
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Pend(ResolutionRefKind.Call, "Ping", receiver: "base", scope: "run", receiverType: "BaseService"));

        Assert.Equal(ResolutionOutcomeKind.Resolved, outcome.Kind);
        Assert.Equal(new FactSymbolKey(1, "base-ping"), outcome.Target);
    }

    [Fact]
    public void ReceiverTypeFact_WithoutReceiverName_StillRunsReceiverTier()
    {
        var facts = ServiceWithPing();
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(ResolutionRefKind.Call, "Ping", scope: "run", receiverType: "Service"));

        Assert.Equal(ResolutionOutcomeKind.Resolved, outcome.Kind);
        Assert.Equal(ResolutionPolicy.ReceiverMethod, outcome.Method);
        Assert.Equal(new FactSymbolKey(1, "ping"), outcome.Target);
    }

    [Fact]
    public void ReceiverTypeFact_AmbiguousTypeName_ContributesNothing()
    {
        var facts = ServiceWithPing();
        facts.Add("Service2", "Service", FactSymbolKind.Class, version: 2);
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(ResolutionRefKind.Call, "Ping", receiver: "this", scope: "run", receiverType: "Service"));

        Assert.Equal(ResolutionOutcomeKind.Missing, outcome.Kind);
    }

    [Fact]
    public void ReceiverTypeFact_DoesNotWalkBaseClasses()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("Base", "BaseService", FactSymbolKind.Class);
        facts.Add("base-ping", "Ping", FactSymbolKind.Method, parentId: "Base");
        facts.Add("Service", "Service", FactSymbolKind.Class);
        facts.Add("run", "Run", FactSymbolKind.Method, parentId: "Service");
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(ResolutionRefKind.Call, "Ping", receiver: "this", scope: "run", receiverType: "Service"));

        Assert.Equal(ResolutionOutcomeKind.Missing, outcome.Kind);
    }

    private static MemoryResolutionFacts ServiceWithPing()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("Service", "Service", FactSymbolKind.Class);
        facts.Add("run", "Run", FactSymbolKind.Method, parentId: "Service");
        facts.Add("ping", "Ping", FactSymbolKind.Method, parentId: "Service");
        return facts;
    }

    [Fact]
    public void DeclaredTypeFact_ResolvesDirectChildAt075()
    {
        var facts = BoxWithParse();
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(ResolutionRefKind.MemberAccess, "Parse", receiver: "box", scope: "m"));

        Assert.Equal(ResolutionOutcomeKind.Resolved, outcome.Kind);
        Assert.Equal(3, outcome.Tier);
        Assert.Equal(ResolutionPolicy.ReceiverMethod, outcome.Method);
        Assert.Equal(0.75, outcome.Confidence);
        Assert.Equal(new FactSymbolKey(1, "parse"), outcome.Target);
    }

    [Fact]
    public void InferredTypeFact_Uses065()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("m", "Run", FactSymbolKind.Method);
        facts.Add("box", "box", FactSymbolKind.Variable, parentId: "m");
        facts.Add("Box", "Box", FactSymbolKind.Class);
        facts.Add("parse", "Parse", FactSymbolKind.Method, parentId: "Box");
        facts.AddTypeFact("box", "Box", inferred: true);
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(ResolutionRefKind.MemberAccess, "Parse", receiver: "box", scope: "m"));

        Assert.Equal(0.65, outcome.Confidence);
    }

    [Fact]
    public void MissingReceiver_YieldsNoCandidates()
    {
        var facts = BoxWithParse();
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome pending = resolver.Resolve(
            ResolutionCases.Pend(ResolutionRefKind.MemberAccess, "Parse", scope: "m"));

        Assert.Equal(ResolutionOutcomeKind.Missing, pending.Kind);
    }

    [Fact]
    public void ZeroTypeSymbols_ContributeNothing()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("m", "Run", FactSymbolKind.Method);
        facts.Add("box", "box", FactSymbolKind.Variable, parentId: "m");
        facts.AddTypeFact("box", "MissingType");
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(ResolutionRefKind.MemberAccess, "Parse", receiver: "box", scope: "m"));

        Assert.Equal(ResolutionOutcomeKind.Missing, outcome.Kind);
    }

    [Fact]
    public void TwoTypeSymbols_ContributeNothing()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("m", "Run", FactSymbolKind.Method);
        facts.Add("box", "box", FactSymbolKind.Variable, parentId: "m");
        facts.Add("t1", "Box", FactSymbolKind.Class);
        facts.Add("t2", "Box", FactSymbolKind.Class, version: 2);
        facts.Add("parse", "Parse", FactSymbolKind.Method, parentId: "t1");
        facts.AddTypeFact("box", "Box");
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(ResolutionRefKind.MemberAccess, "Parse", receiver: "box", scope: "m"));

        Assert.Equal(ResolutionOutcomeKind.Missing, outcome.Kind);
    }

    [Fact]
    public void TypeNameIsVerbatim_NoGenericStripping()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("m", "Run", FactSymbolKind.Method);
        facts.Add("box", "box", FactSymbolKind.Variable, parentId: "m");
        facts.Add("raw", "List", FactSymbolKind.Class);
        facts.Add("parse", "Parse", FactSymbolKind.Method, parentId: "raw");
        facts.AddTypeFact("box", "List<int>");
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(ResolutionRefKind.MemberAccess, "Parse", receiver: "box", scope: "m"));

        Assert.Equal(ResolutionOutcomeKind.Missing, outcome.Kind);
    }

    [Fact]
    public void OnlyDirectChildren_AreCandidates()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("m", "Run", FactSymbolKind.Method);
        facts.Add("box", "box", FactSymbolKind.Variable, parentId: "m");
        facts.Add("Box", "Box", FactSymbolKind.Class);
        facts.Add("nested", "Inner", FactSymbolKind.Class, parentId: "Box");
        facts.Add("parse", "Parse", FactSymbolKind.Method, parentId: "nested");
        facts.AddTypeFact("box", "Box");
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(ResolutionRefKind.MemberAccess, "Parse", receiver: "box", scope: "m"));

        Assert.Equal(ResolutionOutcomeKind.Missing, outcome.Kind);
    }

    [Fact]
    public void ReceiverLookup_IgnoresKindFilter()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("m", "Run", FactSymbolKind.Method);
        facts.Add("box", "box", FactSymbolKind.Function, parentId: "m");
        facts.Add("Box", "Box", FactSymbolKind.Class);
        facts.Add("parse", "Parse", FactSymbolKind.Method, parentId: "Box");
        facts.AddTypeFact("box", "Box");
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(ResolutionRefKind.MemberAccess, "Parse", receiver: "box", scope: "m"));

        Assert.Equal(new FactSymbolKey(1, "parse"), outcome.Target);
    }

    [Fact]
    public void TwoDirectChildren_AreAmbiguous()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("m", "Run", FactSymbolKind.Method);
        facts.Add("box", "box", FactSymbolKind.Variable, parentId: "m");
        facts.Add("Box", "Box", FactSymbolKind.Class);
        facts.Add("p1", "Parse", FactSymbolKind.Method, parentId: "Box");
        facts.Add("p2", "Parse", FactSymbolKind.Property, parentId: "Box");
        facts.AddTypeFact("box", "Box");
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(ResolutionRefKind.MemberAccess, "Parse", receiver: "box", scope: "m"));

        Assert.Equal(ResolutionOutcomeKind.Ambiguous, outcome.Kind);
        Assert.Equal(2, outcome.CandidateCount);
    }

    [Fact]
    public void DedupKeepsMaxConfidence()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("m", "Run", FactSymbolKind.Method);
        facts.Add("box", "box", FactSymbolKind.Variable, parentId: "m");
        facts.Add("Box", "Box", FactSymbolKind.Class);
        facts.Add("parse", "Parse", FactSymbolKind.Method, parentId: "Box");
        facts.AddTypeFact("box", "Box", inferred: true);
        facts.AddTypeFact("box", "Box", inferred: false);
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Ident(ResolutionRefKind.MemberAccess, "Parse", receiver: "box", scope: "m"));

        Assert.Equal(0.75, outcome.Confidence);
    }

    [Fact]
    public void PendingCallWithReceiver_UsesReceiverNotImport()
    {
        var facts = BoxWithParse();
        facts.Add("imp", "Parse", FactSymbolKind.Function, language: "csharp", version: 2);
        facts.AddImport(
            ImportBinding.FromSymbol("Parse", new ImportMetadata(Source: "./p"), moduleVersionId: 2));
        var resolver = new QueryTimeResolver(facts);

        ResolutionOutcome outcome = resolver.Resolve(
            ResolutionCases.Pend(ResolutionRefKind.Call, "Parse", receiver: "box", scope: "m"));

        Assert.Equal(ResolutionPolicy.ReceiverMethod, outcome.Method);
        Assert.Equal(new FactSymbolKey(1, "parse"), outcome.Target);
    }

    private static MemoryResolutionFacts BoxWithParse()
    {
        var facts = new MemoryResolutionFacts();
        facts.Add("m", "Run", FactSymbolKind.Method);
        facts.Add("box", "box", FactSymbolKind.Variable, parentId: "m");
        facts.Add("Box", "Box", FactSymbolKind.Class);
        facts.Add("parse", "Parse", FactSymbolKind.Method, parentId: "Box");
        facts.AddTypeFact("box", "Box");
        return facts;
    }
}
