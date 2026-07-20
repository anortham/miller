using Miller.Indexing.Semantic;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class SymbolCardBuilderTests
{
    [Fact]
    public void Build_EmitsCardTextV1FieldOrder()
    {
        string card = SymbolCardBuilder.Build(new SymbolCardInput(
            SymbolId: "s1",
            Name: "Run",
            Kind: "method",
            Path: "src/Miller.Server/Tools/SearchTool.cs",
            IsTest: false,
            Signature: "public static string Run(SearchRequest request)",
            DocComment: "/// <summary>\n/// Runs the search.\n/// </summary>",
            Container: "SearchTool"));

        Assert.Equal(
            "method SearchTool.Run public static string Run(SearchRequest request) "
            + "<summary> Runs the search. </summary> in: SearchTool src/Miller.Server/Tools/SearchTool.cs",
            card);
    }

    [Fact]
    public void Build_WithoutContainer_UsesBareNameAndOmitsTheContainerSlot()
    {
        string card = SymbolCardBuilder.Build(new SymbolCardInput(
            SymbolId: "s1", Name: "Main", Kind: "function", Path: "src/Program.cs", IsTest: false));

        Assert.Equal("function Main in: src/Program.cs", card);
    }

    [Fact]
    public void Build_UsesOnlyTheFirstLineOfASignature()
    {
        string card = SymbolCardBuilder.Build(new SymbolCardInput(
            SymbolId: "s1", Name: "Run", Kind: "method", Path: "a.cs", IsTest: false,
            Signature: "public void Run(\n    int a,\n    int b)"));

        Assert.Equal("method Run public void Run( in: a.cs", card);
    }

    [Theory]
    [InlineData("/// A doc comment.", "A doc comment.")]
    [InlineData("// A doc comment.", "A doc comment.")]
    [InlineData("/** A doc comment. */", "A doc comment.")]
    [InlineData("/**\n * A doc comment.\n */", "A doc comment.")]
    [InlineData("# A doc comment.", "A doc comment.")]
    [InlineData("\"\"\"A doc comment.\"\"\"", "A doc comment.")]
    [InlineData("'''A doc comment.'''", "A doc comment.")]
    [InlineData("--- A doc comment.", "A doc comment.")]
    [InlineData("<!-- A doc comment. -->", "A doc comment.")]
    [InlineData("///   spaced    out   ", "spaced out")]
    public void DocExcerpt_StripsCommentMarkersAndCollapsesWhitespace(string raw, string expected) =>
        Assert.Equal(expected, SymbolCardBuilder.DocExcerpt(raw));

    [Fact]
    public void DocExcerpt_TruncatesAtAWordBoundaryWithinTheDocBudget()
    {
        string raw = string.Join(' ', Enumerable.Repeat("alpha", 200));

        string excerpt = SymbolCardBuilder.DocExcerpt(raw);

        Assert.True(excerpt.Length <= SymbolCardBuilder.DocExcerptBudget);
        Assert.EndsWith("alpha", excerpt, StringComparison.Ordinal);
        Assert.DoesNotContain("alph ", excerpt, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_TruncatesTheWholeCardAtAWordBoundaryWithinTheCardBudget()
    {
        string card = SymbolCardBuilder.Build(new SymbolCardInput(
            SymbolId: "s1",
            Name: "Run",
            Kind: "method",
            Path: "a.cs",
            IsTest: false,
            Signature: string.Join(' ', Enumerable.Repeat("parameter", 400))));

        Assert.True(card.Length <= SymbolCardBuilder.CardBudget);
        Assert.EndsWith("parameter", card, StringComparison.Ordinal);
    }

    [Fact]
    public void Truncate_WithNoBoundaryBeforeTheBudget_HardCuts()
    {
        string card = SymbolCardBuilder.TruncateOnWordBoundary(new string('x', 50), 10);

        Assert.Equal(new string('x', 10), card);
    }

    [Fact]
    public void Truncate_ShorterThanTheBudget_IsUnchanged() =>
        Assert.Equal("short text", SymbolCardBuilder.TruncateOnWordBoundary("short text", 100));

    [Theory]
    [InlineData("function", true)]
    [InlineData("method", true)]
    [InlineData("class", true)]
    [InlineData("interface", true)]
    [InlineData("struct", true)]
    [InlineData("enum", true)]
    [InlineData("constructor", true)]
    [InlineData("delegate", true)]
    [InlineData("trait", true)]
    [InlineData("Method", true)]
    [InlineData("variable", false)]
    [InlineData("property", false)]
    [InlineData("field", false)]
    [InlineData("constant", false)]
    [InlineData("import", false)]
    [InlineData("module", false)]
    [InlineData("enum_member", false)]
    [InlineData("namespace", false)]
    [InlineData("export", false)]
    [InlineData("", false)]
    public void IsEligible_IsKindDrivenNotLanguageDriven(string kind, bool eligible) =>
        Assert.Equal(eligible, SymbolCardBuilder.IsEligible(kind));

    [Fact]
    public void TestSymbols_GetCardsAndCarryIsTest()
    {
        var input = new SymbolCardInput(
            SymbolId: "s1", Name: "Run_Works", Kind: "method", Path: "tests/A.cs", IsTest: true,
            Container: "ATests");

        Assert.True(SymbolCardBuilder.IsEligible(input.Kind));
        Assert.True(input.IsTest);
        Assert.Equal("method ATests.Run_Works in: ATests tests/A.cs", SymbolCardBuilder.Build(input));
    }

    [Fact]
    public void EmbedTextHash_IsStableAndTextSensitive()
    {
        string first = SymbolCardBuilder.EmbedTextHash("method A.B in: A a.cs");
        string second = SymbolCardBuilder.EmbedTextHash("method A.B in: A a.cs");

        Assert.Equal(first, second);
        Assert.StartsWith("sha256:", first, StringComparison.Ordinal);
        Assert.NotEqual(first, SymbolCardBuilder.EmbedTextHash("method A.C in: A a.cs"));
    }
}
