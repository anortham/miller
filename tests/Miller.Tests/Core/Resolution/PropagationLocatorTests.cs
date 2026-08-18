using Miller.Core.Resolution;
using Xunit;

namespace Miller.Tests.Core.Resolution;

public sealed class PropagationLocatorTests
{
    [Fact]
    public void Locate_BothBytesPresent_MatchesStartInsideAndEndNotPast()
    {
        PropagationCandidate[] candidates =
        [
            new("Foo", StartByte: 10, EndByte: 13, StartLine: 1),
            new("Foo", StartByte: 20, EndByte: 23, StartLine: 2),
        ];

        Assert.Equal(0, PropagationLocator.Locate(candidates, "Foo", startByte: 10, endByte: 15, startLine: 9));
    }

    [Fact]
    public void Locate_BothBytesPresent_RejectsStartOutsideOrEndPast()
    {
        PropagationCandidate[] candidates =
        [
            new("Foo", StartByte: 9, EndByte: 12, StartLine: 1),
            new("Foo", StartByte: 10, EndByte: 16, StartLine: 1),
        ];

        Assert.Null(PropagationLocator.Locate(candidates, "Foo", startByte: 10, endByte: 15, startLine: 1));
    }

    [Fact]
    public void Locate_MissingBytes_UsesStartLine()
    {
        PropagationCandidate[] candidates =
        [
            new("Foo", StartByte: 1, EndByte: 4, StartLine: 3),
            new("Foo", StartByte: 10, EndByte: 13, StartLine: 4),
        ];

        Assert.Equal(1, PropagationLocator.Locate(candidates, "Foo", startByte: null, endByte: 13, startLine: 4));
        Assert.Equal(1, PropagationLocator.Locate(candidates, "Foo", startByte: 10, endByte: null, startLine: 4));
    }

    [Fact]
    public void Locate_TwoMatches_ReturnsNull()
    {
        PropagationCandidate[] candidates =
        [
            new("Foo", StartByte: 10, EndByte: 13, StartLine: 1),
            new("Foo", StartByte: 12, EndByte: 14, StartLine: 1),
        ];

        Assert.Null(PropagationLocator.Locate(candidates, "Foo", startByte: 10, endByte: 15, startLine: 1));
    }

    [Fact]
    public void Locate_ZeroMatches_ReturnsNull()
    {
        PropagationCandidate[] candidates =
        [
            new("Foo", StartByte: 1, EndByte: 2, StartLine: 1),
        ];

        Assert.Null(PropagationLocator.Locate(candidates, "Foo", startByte: 10, endByte: 15, startLine: 2));
    }

    [Fact]
    public void Locate_FiltersByName()
    {
        PropagationCandidate[] candidates =
        [
            new("Bar", StartByte: 10, EndByte: 13, StartLine: 1),
            new("Foo", StartByte: 10, EndByte: 13, StartLine: 1),
        ];

        Assert.Equal(1, PropagationLocator.Locate(candidates, "Foo", startByte: 10, endByte: 15, startLine: 1));
    }

    [Fact]
    public void Locate_InclusiveByteBounds()
    {
        PropagationCandidate[] candidates =
        [
            new("Foo", StartByte: 10, EndByte: 15, StartLine: 1),
        ];

        Assert.Equal(0, PropagationLocator.Locate(candidates, "Foo", startByte: 10, endByte: 15, startLine: 99));
    }
}
