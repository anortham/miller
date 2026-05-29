using Miller.Core.Tokenization;
using Xunit;

namespace Miller.Tests.Tokenization;

/// <summary>
/// Pins the exact token stream emitted by <see cref="CodeTokenizer"/>. The tokenizer is the
/// foundation of both index build (docLen counts every emitted token) and query parsing, so its
/// behavior is contract: full lowercased word first, then component parts only when a split occurred,
/// in left-to-right scan order. These tests assert the FULL ordered output, not just membership.
/// </summary>
public sealed class CodeTokenizerTests
{
    private static List<string> Tokenize(string text)
    {
        var output = new List<string>();
        CodeTokenizer.Tokenize(text, output);
        return output;
    }

    public static TheoryData<string, string[]> ExpectedOutputTable() => new()
    {
        // camelCase + embedded acronym: full word, then get|http|response|code
        { "getHTTPResponseCode", new[] { "gethttpresponsecode", "get", "http", "response", "code" } },
        // leading single-letter + acronym-led PascalCase
        { "IUserService", new[] { "iuserservice", "i", "user", "service" } },
        // snake_case: '_' is a delimiter, so no full re-emit of segments that were never joined
        { "parse_http_header", new[] { "parse", "http", "header" } },
        // letter<->digit boundary
        { "Vector512", new[] { "vector512", "vector", "512" } },
        // PascalCase tail
        { "EmbedBatchAsync", new[] { "embedbatchasync", "embed", "batch", "async" } },
        // letter->digit on a lowercase word
        { "utf8", new[] { "utf8", "utf", "8" } },
        // single-segment word must NOT be re-emitted as a duplicate
        { "user", new[] { "user" } },
    };

    [Theory]
    [MemberData(nameof(ExpectedOutputTable))]
    public void Tokenize_MatchesExpectedOutputTable(string input, string[] expected)
    {
        Assert.Equal(expected, Tokenize(input));
    }

    [Fact]
    public void Tokenize_EmptySpan_EmitsNothing()
    {
        Assert.Empty(Tokenize(""));
    }

    [Theory]
    [InlineData("___")]
    [InlineData("...")]
    [InlineData("---")]
    [InlineData("   ")]
    [InlineData("()[]{}")]
    public void Tokenize_DelimitersOnly_EmitsNothing(string input)
    {
        Assert.Empty(Tokenize(input));
    }

    [Fact]
    public void Tokenize_DottedPath_SplitsOnDots()
    {
        // '.' is a delimiter; each dotted segment is its own single-segment word (no re-emit).
        Assert.Equal(new[] { "system", "text", "json" }, Tokenize("System.Text.Json"));
    }

    [Fact]
    public void Tokenize_HyphenAndDot_AreDelimiters()
    {
        Assert.Equal(new[] { "foo", "bar", "baz" }, Tokenize("foo-bar.baz"));
    }

    [Fact]
    public void Tokenize_UnicodeLetterIdentifier_StaysOneWord()
    {
        // Greek 'café' style: unicode letters are word chars (c>127 && IsLetterOrDigit), no split.
        var result = Tokenize("naïveQuery");
        // camelCase boundary still applies at 'Q'; the full word + the two camel parts.
        Assert.Equal(new[] { "naïvequery", "naïve", "query" }, result);
    }

    [Fact]
    public void Tokenize_PureUnicodeWord_EmitsLoweredWordOnce()
    {
        // A unicode-letter-only identifier with no boundaries is a single token, emitted once.
        Assert.Equal(new[] { "café" }, Tokenize("café"));
    }

    [Fact]
    public void Tokenize_AcronymFollowedByPascal_SplitsBeforeTrailingUpper()
    {
        // HTTPServer -> http|server (UPPER UPPER lower rule).
        Assert.Equal(new[] { "httpserver", "http", "server" }, Tokenize("HTTPServer"));
    }

    [Fact]
    public void Tokenize_AppendsToExistingList_DoesNotClear()
    {
        var output = new List<string> { "preexisting" };
        CodeTokenizer.Tokenize("getUser", output);
        Assert.Equal(new[] { "preexisting", "getuser", "get", "user" }, output);
    }

    [Fact]
    public void Tokenize_LongWordOverStackallocThreshold_LowercasesViaHeap()
    {
        // >256 chars forces the heap path of the lowercase buffer; assert correctness, not internals.
        var longWord = new string('A', 300);
        var result = Tokenize(longWord);
        Assert.Single(result);
        Assert.Equal(new string('a', 300), result[0]);
    }

    [Fact]
    public void Tokenize_DigitToLetterTransition_Splits()
    {
        // digit -> letter boundary (3d -> 3|d after the camel/digit rule).
        Assert.Equal(new[] { "v8engine", "v", "8", "engine" }, Tokenize("v8Engine"));
    }
}
