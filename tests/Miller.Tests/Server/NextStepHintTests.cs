using Miller.Server.Tools;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the shared success-path nudge format that every tool renders through (<see cref="NextStepHint.Render"/>).
/// Only the FORMAT lives in this seam — the hint decision (which tool, which reason) stays in each tool — so these
/// tests fix the exact shape (<c>next: &lt;toolCall&gt;</c>, optional <c>— &lt;reason&gt;</c> with a real em dash
/// U+2014 padded by spaces), the single-line / no-trailing-newline invariant, and the argument guards that the
/// downstream tools and the format-drift test rely on. Pure string logic, no I/O.
/// </summary>
public sealed class NextStepHintTests
{
    [Fact]
    public void Render_WithoutReason_EmitsBareNext()
    {
        Assert.Equal("next: inspect Foo", NextStepHint.Render("inspect Foo"));
    }

    [Fact]
    public void Render_WithNullReason_EmitsBareNext()
    {
        Assert.Equal("next: inspect Foo", NextStepHint.Render("inspect Foo", null));
    }

    [Fact]
    public void Render_WithEmptyReason_EmitsBareNext()
    {
        Assert.Equal("next: inspect Foo", NextStepHint.Render("inspect Foo", ""));
    }

    [Fact]
    public void Render_WithWhitespaceReason_EmitsBareNext()
    {
        Assert.Equal("next: inspect Foo", NextStepHint.Render("inspect Foo", "   "));
    }

    [Fact]
    public void Render_WithReason_JoinsWithSpacedEmDash()
    {
        // U+2014 EM DASH, padded by a single space on each side.
        Assert.Equal("next: inspect Foo — see its callers", NextStepHint.Render("inspect Foo", "see its callers"));
    }

    [Fact]
    public void Render_ReasonWithSurroundingWhitespace_IsTrimmed()
    {
        Assert.Equal("next: inspect Foo — see its callers", NextStepHint.Render("inspect Foo", "  see its callers  "));
    }

    [Fact]
    public void Render_TrimsToolCall()
    {
        Assert.Equal("next: inspect Foo", NextStepHint.Render("  inspect Foo  "));
    }

    [Fact]
    public void Render_ProducesSingleLineWithNoTrailingNewline()
    {
        string rendered = NextStepHint.Render("inspect Foo", "see its callers");
        Assert.DoesNotContain('\n', rendered);
        Assert.DoesNotContain('\r', rendered);
        Assert.Equal(rendered.TrimEnd(), rendered);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Render_NullOrBlankToolCall_Throws(string? toolCall)
    {
        // ThrowIfNullOrWhiteSpace throws ArgumentNullException for null and ArgumentException for blank — both are
        // ArgumentException, which is the guard the contract promises.
        Assert.ThrowsAny<ArgumentException>(() => NextStepHint.Render(toolCall!));
    }

    [Theory]
    [InlineData("inspect\nFoo")]
    [InlineData("inspect\rFoo")]
    [InlineData("inspect Foo\n")]
    public void Render_ToolCallWithNewline_Throws(string toolCall)
    {
        Assert.Throws<ArgumentException>(() => NextStepHint.Render(toolCall));
    }

    [Theory]
    [InlineData("see\nits callers")]
    [InlineData("see\rits callers")]
    public void Render_ReasonWithNewline_Throws(string reason)
    {
        Assert.Throws<ArgumentException>(() => NextStepHint.Render("inspect Foo", reason));
    }
}
