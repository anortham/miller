using Miller.Indexing;
using Miller.Server.Logging;
using Xunit;

namespace Miller.Tests.Logging;

/// <summary>
/// Pins <see cref="ExtractErrorLog.Describe"/> (m8-design §D3): the pure helper that turns a caught extract
/// exception into the <c>codes</c> + bounded <c>stderrTail</c> the <c>IndexerCore</c> catch sites log. The
/// <c>codes</c> wording MUST byte-match the inline string the catch sites used before the helper existed (so the
/// D3 refactor is behavior-preserving); the tail must surface julie's raw stderr that <c>{Exception}</c> drops,
/// bounded at <see cref="ExtractErrorLog.MaxTail"/> with an ellipsis when clipped. Null-safe throughout.
/// </summary>
public sealed class ExtractErrorLogTests
{
    private static ExtractError Err(string code) => new(code, $"message for {code}", Path: null);

    [Fact]
    public void FailedException_WithCodesAndLongStderr_JoinsCodes_AndTruncatesTail()
    {
        // A stderr longer than MaxTail must be clipped to its LAST MaxTail chars, ellipsis-prefixed. The leading
        // run is deliberately well over MaxTail so the head is dropped and only the trailing failure survives.
        string stderr = new string('a', ExtractErrorLog.MaxTail + 100) + "THE_REAL_FAILURE_AT_THE_END";
        var ex = new JulieExtractFailedException(
            "extract failed",
            new[] { Err("code1"), Err("code2") },
            stderr);

        var result = ExtractErrorLog.Describe(ex);

        Assert.Equal("code1, code2", result.Codes);
        // Tail = ellipsis + last MaxTail chars => total length MaxTail + 1.
        Assert.Equal(ExtractErrorLog.MaxTail + 1, result.StderrTail.Length);
        Assert.StartsWith("…", result.StderrTail);
        Assert.EndsWith("THE_REAL_FAILURE_AT_THE_END", result.StderrTail);
        // The clipped head must be gone: the first 'a' run is longer than what survived, but the tail end is kept.
        Assert.Equal(stderr[^ExtractErrorLog.MaxTail..], result.StderrTail[1..]);
    }

    [Fact]
    public void FailedException_WithEmptyErrors_ReportsNoStructuredErrors_ButKeepsStderr()
    {
        var ex = new JulieExtractFailedException("extract failed", Array.Empty<ExtractError>(), "short stderr");

        var result = ExtractErrorLog.Describe(ex);

        Assert.Equal("(no structured errors)", result.Codes);
        Assert.Equal("short stderr", result.StderrTail);
    }

    [Fact]
    public void BaseExtractException_HasNoCodes_ButSurfacesStderrTail()
    {
        // A non-failed extract exception (unexpected exit / usage): no structured codes, stderr is the diagnosis.
        var ex = new JulieExtractException("unexpected exit code 134", "panicked at 'index out of bounds'");

        var result = ExtractErrorLog.Describe(ex);

        Assert.Equal("(no structured errors)", result.Codes);
        Assert.Equal("panicked at 'index out of bounds'", result.StderrTail);
    }

    [Fact]
    public void GenericException_HasNoCodes_AndNoStderr()
    {
        var result = ExtractErrorLog.Describe(new InvalidOperationException("boom"));

        Assert.Equal("(no structured errors)", result.Codes);
        Assert.Equal("", result.StderrTail);
    }

    [Fact]
    public void NullException_IsTreatedAsGeneric()
    {
        var result = ExtractErrorLog.Describe(null);

        Assert.Equal("(no structured errors)", result.Codes);
        Assert.Equal("", result.StderrTail);
    }

    [Fact]
    public void StderrExactlyAtMaxTail_IsNotTruncated()
    {
        string stderr = new string('x', ExtractErrorLog.MaxTail);
        var ex = new JulieExtractException("fail", stderr);

        var result = ExtractErrorLog.Describe(ex);

        // At the boundary the whole string is kept verbatim — no ellipsis.
        Assert.Equal(stderr, result.StderrTail);
        Assert.DoesNotContain("…", result.StderrTail);
    }

    [Fact]
    public void StderrOneOverMaxTail_IsTruncated()
    {
        string stderr = new string('x', ExtractErrorLog.MaxTail + 1);
        var ex = new JulieExtractException("fail", stderr);

        var result = ExtractErrorLog.Describe(ex);

        // One char over => clipped to MaxTail chars + a leading ellipsis.
        Assert.Equal(ExtractErrorLog.MaxTail + 1, result.StderrTail.Length);
        Assert.StartsWith("…", result.StderrTail);
        Assert.Equal(new string('x', ExtractErrorLog.MaxTail), result.StderrTail[1..]);
    }

    [Fact]
    public void NullStderr_YieldsEmptyTail()
    {
        var ex = new JulieExtractFailedException("fail", new[] { Err("only_code") }, standardError: null!);

        var result = ExtractErrorLog.Describe(ex);

        Assert.Equal("only_code", result.Codes);
        Assert.Equal("", result.StderrTail);
    }

    [Fact]
    public void EmptyStderr_YieldsEmptyTail()
    {
        var ex = new JulieExtractException("fail", standardError: "");

        var result = ExtractErrorLog.Describe(ex);

        Assert.Equal("", result.StderrTail);
    }
}
