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
        Assert.False(SemanticQueryPolicy.Route(query, LexicalEvidence.None).IsHybrid);
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
        var route = SemanticQueryPolicy.Route(query, LexicalEvidence.None);

        Assert.True(route.IsHybrid);
        Assert.Equal(SemanticFusionClass.Conceptual, route.HybridClass);
    }

    [Fact]
    public void Route_ProseQueryNamingAnIdentifier_IsHybridMixed()
    {
        var route = SemanticQueryPolicy.Route("how does FreshnessService detect a rebuild", LexicalEvidence.None);

        Assert.True(route.IsHybrid);
        Assert.Equal(SemanticFusionClass.Mixed, route.HybridClass);
    }

    [Fact]
    public void Route_AmbiguousQuery_WithWeakLexicalEvidence_GoesHybrid()
    {
        var route = SemanticQueryPolicy.Route("vector store", LexicalEvidence.None);

        Assert.True(route.IsHybrid);
        Assert.Equal(SemanticQueryReason.AmbiguousWeakLexical, route.Reason);
    }

    [Fact]
    public void Route_AmbiguousQuery_WithDominantLexicalHit_StaysLexicalOnly()
    {
        var evidence = new LexicalEvidence(HitCount: 4, TopScore: 9.0, RunnerUpScore: 2.0);

        var route = SemanticQueryPolicy.Route("vector store", evidence);

        Assert.False(route.IsHybrid);
        Assert.Equal(SemanticQueryReason.AmbiguousStrongLexical, route.Reason);
    }

    [Fact]
    public void Route_AmbiguousQuery_WithFlatLexicalScores_GoesHybrid()
    {
        var evidence = new LexicalEvidence(HitCount: 12, TopScore: 3.0, RunnerUpScore: 2.9);

        var route = SemanticQueryPolicy.Route("vector store", evidence);

        Assert.True(route.IsHybrid);
        Assert.Equal(SemanticQueryReason.AmbiguousWeakLexical, route.Reason);
    }

    [Fact]
    public void Route_AmbiguousIdentifierPair_WithWeakEvidence_IsHybridSymbolLookup()
    {
        var route = SemanticQueryPolicy.Route("VectorSidecar TryOpen", LexicalEvidence.None);

        Assert.True(route.IsHybrid);
        Assert.Equal(SemanticFusionClass.SymbolLookup, route.HybridClass);
    }

    [Fact]
    public void Route_AmbiguousPlainPair_WithWeakEvidence_IsHybridMixed()
    {
        var route = SemanticQueryPolicy.Route("release process", LexicalEvidence.None);

        Assert.True(route.IsHybrid);
        Assert.Equal(SemanticFusionClass.Mixed, route.HybridClass);
    }

    [Fact]
    public void Route_StrongLexicalEvidence_DoesNotOverrideProseQueries()
    {
        var evidence = new LexicalEvidence(HitCount: 6, TopScore: 20.0, RunnerUpScore: 1.0);

        var route = SemanticQueryPolicy.Route("how does indexing convergence work", evidence);

        Assert.True(route.IsHybrid);
        Assert.Equal(SemanticQueryReason.Prose, route.Reason);
    }

    [Fact]
    public void Route_StrongLexicalEvidence_DoesNotOverrideShapeRoutedLexicalOnly()
    {
        var evidence = new LexicalEvidence(HitCount: 0, TopScore: 0.0, RunnerUpScore: 0.0);

        Assert.False(SemanticQueryPolicy.Route("src/x/y.cs", evidence).IsHybrid);
        Assert.False(SemanticQueryPolicy.Route("FooBar", evidence).IsHybrid);
    }

    [Fact]
    public void Route_NullEvidence_BehavesAsNoEvidence()
    {
        var withNull = SemanticQueryPolicy.Route("vector store", evidence: null);
        var withNone = SemanticQueryPolicy.Route("vector store", LexicalEvidence.None);

        Assert.Equal(withNone, withNull);
    }

    [Fact]
    public void Route_IsDeterministic_AcrossRepeatedCalls()
    {
        var evidence = new LexicalEvidence(HitCount: 3, TopScore: 5.0, RunnerUpScore: 4.9);

        var first = SemanticQueryPolicy.Route("converge queue yield", evidence);
        var second = SemanticQueryPolicy.Route("converge queue yield", evidence);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Route_TrimsSurroundingWhitespace()
    {
        Assert.Equal(
            SemanticQueryPolicy.Route("how does indexing convergence work", LexicalEvidence.None),
            SemanticQueryPolicy.Route("  how does indexing convergence work\t", LexicalEvidence.None));
    }

    [Fact]
    public void PolicyVersion_IsTheFrozenV1Token()
    {
        Assert.Equal("policy-v1", SemanticQueryPolicy.PolicyVersion);
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
    public void LexicalEvidence_None_IsWeak()
    {
        Assert.False(LexicalEvidence.None.IsStrong);
    }

    [Fact]
    public void LexicalEvidence_SingleDominantHit_IsStrong()
    {
        Assert.True(new LexicalEvidence(HitCount: 1, TopScore: 7.5, RunnerUpScore: 0.0).IsStrong);
    }

    [Fact]
    public void LexicalEvidence_ZeroHits_IsWeakEvenWithScores()
    {
        Assert.False(new LexicalEvidence(HitCount: 0, TopScore: 7.5, RunnerUpScore: 0.0).IsStrong);
    }
}
