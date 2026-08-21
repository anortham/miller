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

    // The bounded fact cache locates through the name-bucketed index instead of rescanning every candidate
    // per source row. It must answer what the scan answers for every probe, including the "two matches means
    // no match" rule and the line-mode fallback, or a reference's propagation override would differ.
    [Fact]
    public void CandidateIndex_AnswersWhatTheScanAnswers()
    {
        PropagationCandidate[] candidates =
        [
            new("Foo", StartByte: 10, EndByte: 13, StartLine: 1),
            new("Foo", StartByte: 12, EndByte: 14, StartLine: 1),
            new("Foo", StartByte: 40, EndByte: 43, StartLine: 4),
            new("Bar", StartByte: 10, EndByte: 13, StartLine: 1),
            new("Bar", StartByte: 20, EndByte: 26, StartLine: 2),
            new("Baz", StartByte: 30, EndByte: 33, StartLine: 3),
            new("Baz", StartByte: 30, EndByte: 33, StartLine: 3),
        ];
        var index = new PropagationCandidateIndex(candidates);
        string[] names = ["Foo", "Bar", "Baz", "Absent"];
        long?[] bytes = [null, 0, 10, 12, 20, 26, 30, 40, 43];
        long?[] lines = [null, 0, 1, 2, 3, 4];

        int probes = 0;
        foreach (string name in names)
        {
            foreach (long? startByte in bytes)
            {
                foreach (long? endByte in bytes)
                {
                    foreach (long? startLine in lines)
                    {
                        probes++;
                        Assert.Equal(
                            PropagationLocator.Locate(candidates, name, startByte, endByte, startLine),
                            index.Locate(name, startByte, endByte, startLine));
                    }
                }
            }
        }

        Assert.Equal(names.Length * bytes.Length * bytes.Length * lines.Length, probes);
    }

    [Fact]
    public void CandidateIndex_EmptyCandidateListNeverLocates()
    {
        var index = new PropagationCandidateIndex([]);

        Assert.Null(index.Locate("Foo", startByte: 10, endByte: 15, startLine: 1));
    }
}
