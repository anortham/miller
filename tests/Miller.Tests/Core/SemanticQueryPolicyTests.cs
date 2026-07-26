using Miller.Core.Search;
using Xunit;

namespace Miller.Tests.Core;

public sealed class SemanticQueryPolicyTests
{
    public static TheoryData<string> LexicalOnlyQueries => new()
    {
        "FooBar",
        "foo_bar",
        "foo.Bar",
        "IFooBar",
        "getHTTPResponseCode",
        "src/x/y.cs",
        "x/y",
        "./src/App.cs",
        "~/notes.md",
        "src\\Miller.Core\\Search",
        "a",
        "cfg",
        "id",
        "Run(query)",
        "count < limit",
        "",
        "   ",
    };

    [Theory]
    [MemberData(nameof(LexicalOnlyQueries))]
    public void Route_ShapeRoutedQueries_StayLexicalOnly(string query)
    {
        Assert.False(SemanticQueryPolicy.Route(query).IsHybrid);
    }

    public static TheoryData<string> ConceptualQueries => new()
    {
        "how does indexing convergence work",
        "what is the release process",
        "why do full rebuilds promote instead of merge",
        "where are workspace roots registered",
        "how should an agent pick a search mode",
    };

    [Theory]
    [MemberData(nameof(ConceptualQueries))]
    public void Route_ProseQueries_AreHybridConceptual(string query)
    {
        var route = SemanticQueryPolicy.Route(query);

        Assert.True(route.IsHybrid);
        Assert.Equal(SemanticFusionClass.Conceptual, route.HybridClass);
    }

    [Fact]
    public void Route_ProseQueryNamingAnIdentifier_IsHybridMixed()
    {
        var route = SemanticQueryPolicy.Route("how does FreshnessService detect a rebuild");

        Assert.True(route.IsHybrid);
        Assert.Equal(SemanticFusionClass.Mixed, route.HybridClass);
    }

    [Fact]
    public void Route_AmbiguousQuery_IsHybridWithoutReadingLexicalEvidence()
    {
        var route = SemanticQueryPolicy.Route("vector store");

        Assert.True(route.IsHybrid);
        Assert.Equal(SemanticQueryReason.Ambiguous, route.Reason);
    }

    [Fact]
    public void Route_AmbiguousIdentifierPair_WithWeakEvidence_IsHybridSymbolLookup()
    {
        var route = SemanticQueryPolicy.Route("VectorSidecar TryOpen");

        Assert.True(route.IsHybrid);
        Assert.Equal(SemanticFusionClass.SymbolLookup, route.HybridClass);
    }

    [Fact]
    public void Route_AmbiguousPlainPair_WithWeakEvidence_IsHybridMixed()
    {
        var route = SemanticQueryPolicy.Route("release process");

        Assert.True(route.IsHybrid);
        Assert.Equal(SemanticFusionClass.Mixed, route.HybridClass);
    }

    [Fact]
    public void Route_IsDeterministic_AcrossRepeatedCalls()
    {
        var first = SemanticQueryPolicy.Route("converge queue yield");
        var second = SemanticQueryPolicy.Route("converge queue yield");

        Assert.Equal(first, second);
    }

    [Fact]
    public void Route_TrimsSurroundingWhitespace()
    {
        Assert.Equal(
            SemanticQueryPolicy.Route("how does indexing convergence work"),
            SemanticQueryPolicy.Route("  how does indexing convergence work\t"));
    }

    [Fact]
    public void PolicyVersion_IsTheSingleIntegerV2()
    {
        Assert.Equal(2, SemanticQueryPolicy.PolicyVersion);
    }

    [Theory]
    [InlineData(SemanticFusionClass.SymbolLookup, "symbol_lookup")]
    [InlineData(SemanticFusionClass.Conceptual, "conceptual")]
    [InlineData(SemanticFusionClass.Mixed, "mixed")]
    public void WireName_MatchesTheFusionProfileContract(SemanticFusionClass fusionClass, string expected)
    {
        Assert.Equal(expected, SemanticQueryPolicy.WireName(fusionClass));
    }

    [Fact]
    public void DecideAdmission_ZeroHits_ExpandsWithoutProtection()
    {
        SemanticCandidateAdmission decision = SemanticQueryPolicy.DecideAdmission(LexicalEvidence.None);

        Assert.Equal(SemanticCandidateAdmissionMode.RerankAndExpand, decision.Mode);
        Assert.Equal(0, decision.ProtectedLexicalCount);
        Assert.Equal(SemanticCandidateAdmissionReason.NoLexicalHits, decision.Reason);
    }

    [Fact]
    public void DecideAdmission_OneHit_ExpandsAndProtectsTheLexicalWinner()
    {
        SemanticCandidateAdmission decision = SemanticQueryPolicy.DecideAdmission(
            new LexicalEvidence(HitCount: 1, TopScore: 7.5, RunnerUpScore: 0.0));

        Assert.Equal(SemanticCandidateAdmissionMode.RerankAndExpand, decision.Mode);
        Assert.Equal(1, decision.ProtectedLexicalCount);
        Assert.Equal(SemanticCandidateAdmissionReason.SingleLexicalHit, decision.Reason);
    }

    [Theory]
    [InlineData(2, 5.0, 4.0)]
    [InlineData(8, 10.0, 2.0)]
    public void DecideAdmission_DecisiveMultiHitWithPositiveRunnerUp_ReranksOnly(
        int hitCount,
        double topScore,
        double runnerUpScore)
    {
        SemanticCandidateAdmission decision = SemanticQueryPolicy.DecideAdmission(
            new LexicalEvidence(hitCount, topScore, runnerUpScore));

        Assert.Equal(SemanticCandidateAdmissionMode.RerankOnly, decision.Mode);
        Assert.Equal(0, decision.ProtectedLexicalCount);
        Assert.Equal(SemanticCandidateAdmissionReason.DecisiveMultiHit, decision.Reason);
    }

    [Theory]
    [InlineData(2, 5.0, 4.01)]
    [InlineData(4, 10.0, 0.0)]
    [InlineData(3, 0.0, 0.0)]
    public void DecideAdmission_OtherMultiHitEvidence_ReranksAndExpands(
        int hitCount,
        double topScore,
        double runnerUpScore)
    {
        SemanticCandidateAdmission decision = SemanticQueryPolicy.DecideAdmission(
            new LexicalEvidence(hitCount, topScore, runnerUpScore));

        Assert.Equal(SemanticCandidateAdmissionMode.RerankAndExpand, decision.Mode);
        Assert.Equal(0, decision.ProtectedLexicalCount);
        Assert.Equal(SemanticCandidateAdmissionReason.WeakMultiHit, decision.Reason);
    }
}
