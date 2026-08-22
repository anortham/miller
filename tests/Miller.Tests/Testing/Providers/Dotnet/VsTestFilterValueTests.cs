using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Providers.Dotnet;

/// <summary>
/// The VSTest filter grammar reserves <c>( ) &amp; | = ! ~</c> and the backslash that escapes them, so a
/// value carrying any of them must be escaped before it joins a selection or exclusion expression.
/// Unescaped, a parameterized display name such as <c>Cases (1,2)</c> reads as grouping and either fails
/// the filter parse — a false red — or selects a different set than the caller asked for.
/// </summary>
public sealed class VsTestFilterValueTests
{
    [Fact]
    public void Escape_leaves_an_ordinary_value_byte_identical()
    {
        Assert.Equal(
            "Sample.Tests.CalculatorTests.Adds",
            VsTestFilterValue.Escape("Sample.Tests.CalculatorTests.Adds"));
        Assert.Equal("", VsTestFilterValue.Escape(""));
    }

    [Theory]
    [InlineData("(", "\\(")]
    [InlineData(")", "\\)")]
    [InlineData("&", "\\&")]
    [InlineData("|", "\\|")]
    [InlineData("=", "\\=")]
    [InlineData("!", "\\!")]
    [InlineData("~", "\\~")]
    [InlineData("\\", "\\\\")]
    public void Escape_backslash_escapes_every_reserved_character(string value, string expected) =>
        Assert.Equal(expected, VsTestFilterValue.Escape(value));

    /// <summary>
    /// The backslash is escaped as itself, not re-escaped a second time when it precedes a reserved
    /// character: <c>\(</c> must become <c>\\\(</c>, so vstest reads a literal backslash then a literal
    /// parenthesis.
    /// </summary>
    [Fact]
    public void Escape_does_not_double_escape_an_already_backslashed_reserved_character() =>
        Assert.Equal("\\\\\\(", VsTestFilterValue.Escape("\\("));

    /// <summary>
    /// The comma keeps its historic percent-encoding rather than a backslash: vstest's own guidance for a
    /// parameterized name is <c>%2C</c>, and <c>%2C</c> carries no reserved character of its own.
    /// </summary>
    [Fact]
    public void Escape_percent_encodes_the_comma() =>
        Assert.Equal("Cases \\(1%2C2\\)", VsTestFilterValue.Escape("Cases (1,2)"));

    [Fact]
    public void Escape_handles_a_value_mixing_several_reserved_characters() =>
        Assert.Equal(
            "Slow\\&Flaky \\(nightly\\)\\|weekend",
            VsTestFilterValue.Escape("Slow&Flaky (nightly)|weekend"));
}
