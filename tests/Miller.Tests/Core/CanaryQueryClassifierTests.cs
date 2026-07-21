using Miller.Core.Search;
using Xunit;

namespace Miller.Tests.Core;

public sealed class CanaryQueryClassifierTests
{
    [Theory]
    [InlineData("auto", "", CanaryQueryClassifier.ShortToken)]
    [InlineData("auto", "abc", CanaryQueryClassifier.ShortToken)]
    [InlineData("auto", "GetUser", CanaryQueryClassifier.Identifier)]
    [InlineData("auto", "foo()", CanaryQueryClassifier.Identifier)]
    [InlineData("auto", "user_id", CanaryQueryClassifier.Identifier)]
    [InlineData("auto", "src/Widget.cs", CanaryQueryClassifier.Path)]
    [InlineData("auto", "./relative/path", CanaryQueryClassifier.Path)]
    [InlineData("auto", "how does the workspace refresh converge", CanaryQueryClassifier.Prose)]
    [InlineData("auto", "getUser count", CanaryQueryClassifier.Mixed)]
    public void Classify_MapsEachPolicyRouteToItsFrozenClass(string op, string query, string expected)
    {
        SemanticQueryRoute route = SemanticQueryPolicy.Route(query, LexicalEvidence.None);

        Assert.Equal(expected, CanaryQueryClassifier.Classify(op, query, route));
    }

    [Theory]
    [InlineData("where is the readme file")]
    [InlineData("how do I install the guide")]
    [InlineData("the configuration for setup")]
    [InlineData("update the changelog and license")]
    [InlineData("read the documentation tutorial faq")]
    public void Classify_PromotesProseWithDocsVocabularyToDocsLike(string query)
    {
        SemanticQueryRoute route = SemanticQueryPolicy.Route(query, LexicalEvidence.None);

        Assert.Equal(SemanticQueryReason.Prose, route.Reason);
        Assert.Equal(CanaryQueryClassifier.DocsLike, CanaryQueryClassifier.Classify("auto", query, route));
    }

    [Fact]
    public void Classify_PromotesAnyProseToDocsLikeUnderTheContentOp()
    {
        const string query = "how does the retry loop recover";
        SemanticQueryRoute route = SemanticQueryPolicy.Route(query, LexicalEvidence.None);

        Assert.Equal(SemanticQueryReason.Prose, route.Reason);
        Assert.Equal(CanaryQueryClassifier.Prose, CanaryQueryClassifier.Classify("auto", query, route));
        Assert.Equal(CanaryQueryClassifier.DocsLike, CanaryQueryClassifier.Classify("content", query, route));
    }

    [Fact]
    public void Classify_ProseWithoutVocabularyOrContentOpStaysProse()
    {
        const string query = "how does the workspace refresh converge";
        SemanticQueryRoute route = SemanticQueryPolicy.Route(query, LexicalEvidence.None);

        Assert.Equal(CanaryQueryClassifier.Prose, CanaryQueryClassifier.Classify("symbol", query, route));
    }

    [Theory]
    [InlineData(SemanticQueryReason.AmbiguousWeakLexical)]
    [InlineData(SemanticQueryReason.AmbiguousStrongLexical)]
    public void Classify_ResolvesBothAmbiguousReasonsToMixed(SemanticQueryReason reason)
    {
        var route = new SemanticQueryRoute(reason != SemanticQueryReason.AmbiguousStrongLexical, SemanticFusionClass.Mixed, reason);

        Assert.Equal(CanaryQueryClassifier.Mixed, CanaryQueryClassifier.Classify("auto", "widget parse", route));
    }

    [Fact]
    public void Classify_DocsVocabularyIsCaseInsensitiveAndWholeWord()
    {
        SemanticQueryRoute upper = SemanticQueryPolicy.Route("where is the README", LexicalEvidence.None);
        Assert.Equal(CanaryQueryClassifier.DocsLike, CanaryQueryClassifier.Classify("auto", "where is the README", upper));

        SemanticQueryRoute substring = SemanticQueryPolicy.Route("how does the readmexyz work", LexicalEvidence.None);
        Assert.Equal(CanaryQueryClassifier.Prose, CanaryQueryClassifier.Classify("auto", "how does the readmexyz work", substring));
    }
}
