using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins the <c>--jobs</c> cap that bounds julie-extract's extraction pool. Only the pure seams are exercised:
/// resolving from the real environment would leak across xUnit's parallel collections.
/// </summary>
public sealed class ExtractJobsPolicyTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 1)]
    [InlineData(4, 2)]
    [InlineData(8, 4)]
    [InlineData(24, 4)]
    [InlineData(128, 4)]
    public void DefaultFor_HalvesProcessorCountCappedAtFour(int processorCount, int expected) =>
        Assert.Equal(expected, ExtractJobsPolicy.DefaultFor(processorCount));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FromEnvValue_UnsetOrBlank_UsesDefault(string? raw) =>
        Assert.Equal(ExtractJobsPolicy.DefaultFor(16), ExtractJobsPolicy.FromEnvValue(raw, processorCount: 16));

    [Fact]
    public void FromEnvValue_ExplicitZero_OptsIntoRayonAuto() =>
        Assert.Equal(ExtractJobsPolicy.RayonAuto, ExtractJobsPolicy.FromEnvValue("0", processorCount: 16));

    [Theory]
    [InlineData("1", 1)]
    [InlineData(" 6 ", 6)]
    [InlineData("32", 32)]
    public void FromEnvValue_ExplicitCount_HonoredEvenAboveTheDefaultCap(string raw, int expected) =>
        Assert.Equal(expected, ExtractJobsPolicy.FromEnvValue(raw, processorCount: 16));

    [Theory]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("4.5")]
    [InlineData("1e3")]
    [InlineData("99999999999999999999")]
    [InlineData("4,5")]
    public void FromEnvValue_InvalidValue_FallsBackToDefault(string raw) =>
        Assert.Equal(ExtractJobsPolicy.DefaultFor(16), ExtractJobsPolicy.FromEnvValue(raw, processorCount: 16));
}
