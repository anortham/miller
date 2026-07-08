using Miller.Server;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the build-identity signal <see cref="MillerVersion.Current"/>: it must always resolve to a usable,
/// non-empty version string (never throw, never return empty) so the MCP <c>ServerInfo.Version</c>, the
/// <c>miller version</c> verb, and <c>workspace status</c> always have something honest to show. The exact
/// value (and whether a <c>+sha</c> suffix is present) depends on the build environment, so the assertions
/// stay on the SHAPE, not a literal.
/// </summary>
public sealed class MillerVersionTests
{
    [Fact]
    public void Current_IsNonEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(MillerVersion.Current));
    }

    [Fact]
    public void Current_StartsWithTheBaseVersion()
    {
        // Directory.Build.props pins <Version>1.5.1</Version>; the optional git SHA is a "+<sha>" suffix, so the
        // string starts with the base version whether or not it was stamped.
        Assert.StartsWith("1.5.1", MillerVersion.Current);
    }

    [Fact]
    public void Current_IsStable_AcrossReads()
    {
        // Computed once into a static; two reads return the identical reference/value.
        Assert.Equal(MillerVersion.Current, MillerVersion.Current);
    }
}
