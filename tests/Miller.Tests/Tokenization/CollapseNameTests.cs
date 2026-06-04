using Miller.Core.Tokenization;
using Xunit;

namespace Miller.Tests.Tokenization;

/// <summary>
/// Pins <see cref="CollapseName.Of"/>: lowercase, keep only word characters (letters/digits),
/// drop every separator (<c>_ - . :: </c>, whitespace, punctuation) so an identifier becomes one
/// separator-free run. This is the load-bearing fix for language-uniform interior-substring recall —
/// snake_case and camelCase spellings of the same name must collapse to the SAME key, which the word
/// tokenizer cannot do today (<c>_</c> is a delimiter, so <c>format_external_extract</c> never yields
/// a <c>formatexternalextract</c> token). See docs/plans/2026-06-04-symbol-search-collapsed-trigram-design.md.
/// </summary>
public sealed class CollapseNameTests
{
    public static TheoryData<string, string> ExpectedCollapseTable() => new()
    {
        // snake_case and camelCase collapse to the same separator-free run
        { "format_external_extract", "formatexternalextract" },
        { "FormatExternalExtract", "formatexternalextract" },
        // dotted qualified name: dots are separators
        { "JsonParser.parseToken", "jsonparserparsetoken" },
        // embedded acronym: just case-folded, no splitting
        { "getHTTPResponseCode", "gethttpresponsecode" },
        // digits are kept
        { "Vector512", "vector512" },
        { "utf8", "utf8" },
        // already a separator-free lowercase run: unchanged
        { "provider", "provider" },
        // mixed separators all drop
        { "foo-bar.baz::qux", "foobarbazqux" },
        { "parse_http_header", "parsehttpheader" },
        // whitespace is a separator too
        { "format external extract", "formatexternalextract" },
        // unicode letters are word chars; case-folded, kept
        { "naïveQuery", "naïvequery" },
    };

    [Theory]
    [MemberData(nameof(ExpectedCollapseTable))]
    public void Of_MatchesExpectedCollapse(string input, string expected)
    {
        Assert.Equal(expected, CollapseName.Of(input));
    }

    [Fact]
    public void Of_SnakeAndCamel_ProduceTheSameKey()
    {
        // The asymmetry fix: both spellings map to one recall key.
        Assert.Equal(CollapseName.Of("format_external_extract"), CollapseName.Of("FormatExternalExtract"));
    }

    [Fact]
    public void Of_BoundaryCrossingFragment_IsContiguousInCollapsedForm()
    {
        // Why collapse matters: 'tionprov' spans authentica|tion..prov|ider, unreachable by token
        // search, but is a contiguous substring of the collapsed run (the trigram arm can match it).
        Assert.Contains("tionprov", CollapseName.Of("IAuthenticationProvider"));
    }

    [Fact]
    public void Of_Empty_ReturnsEmpty()
    {
        Assert.Equal("", CollapseName.Of(""));
    }

    [Theory]
    [InlineData("___")]
    [InlineData("   ")]
    [InlineData("::")]
    [InlineData("()[]{}")]
    public void Of_SeparatorsOnly_ReturnsEmpty(string input)
    {
        Assert.Equal("", CollapseName.Of(input));
    }

    [Fact]
    public void Of_PureUnicodeWord_CaseFoldedOnce()
    {
        Assert.Equal("café", CollapseName.Of("café"));
    }
}
