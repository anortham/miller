using Miller.Core.Freshness;
using Xunit;

namespace Miller.Tests.Freshness;

public sealed class ExtractSourceLimitsTests
{
    [Fact]
    public void TheDefaultCeilingMirrorsJulieExtract()
    {
        Assert.Equal(1024 * 1024, ExtractSourceLimits.DefaultMaxSourceFileBytes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-number")]
    [InlineData("0")]
    [InlineData("-1")]
    public void AnUnusableOverrideFallsBackToTheDefault(string? raw)
    {
        Assert.Equal(
            ExtractSourceLimits.DefaultMaxSourceFileBytes,
            ExtractSourceLimits.ParseMaxSourceFileBytes(raw));
    }

    [Theory]
    [InlineData("1", 1L)]
    [InlineData(" 4194304 ", 4194304L)]
    public void APositiveOverrideIsHonoredVerbatim(string raw, long expected)
    {
        Assert.Equal(expected, ExtractSourceLimits.ParseMaxSourceFileBytes(raw));
    }

    [Fact]
    public void AFileOfExactlyTheCeilingIsNotOversized()
    {
        long ceiling = ExtractSourceLimits.DefaultMaxSourceFileBytes;
        Assert.False(ExtractSourceLimits.IsOversized(ceiling, ceiling));
        Assert.True(ExtractSourceLimits.IsOversized(ceiling + 1, ceiling));
    }

    [Theory]
    [InlineData("wwwroot/site.min.js")]
    [InlineData("wwwroot/site.bundle.js")]
    [InlineData("src/schema.generated.js")]
    [InlineData("src/schema.generated.jsx")]
    [InlineData("src/schema.generated.ts")]
    [InlineData("src/schema.generated.tsx")]
    [InlineData("src/schema.generated.d.ts")]
    public void GeneratedSuffixesMatchJulieExtract(string path)
    {
        Assert.True(ExtractSourceLimits.HasGeneratedSuffix(path, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("wwwroot/site.js")]
    [InlineData("src/generated.ts")]
    [InlineData("src/index.d.ts")]
    [InlineData("src/min.js.cs")]
    public void OrdinarySourceKeepsItsGeneratedLookalikeName(string path)
    {
        Assert.False(ExtractSourceLimits.HasGeneratedSuffix(path, StringComparison.Ordinal));
    }
}
