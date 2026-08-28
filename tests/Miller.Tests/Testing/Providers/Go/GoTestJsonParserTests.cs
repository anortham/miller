using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Providers.Go;

public sealed class GoTestJsonParserTests
{
    [Fact]
    public void Parse_separates_interleaved_test_and_build_events_and_keeps_unknown_actions()
    {
        GoTestJsonParseResult result = GoTestJsonParser.Parse("""
            {"Action":"start","Package":"example.com/first"}
            {"Action":"run","Package":"example.com/first","Test":"TestAdd"}
            {"Action":"output","Package":"example.com/first","Test":"TestAdd","Output":"ok\n"}
            {"Action":"run","Package":"example.com/second","Test":"TestSub"}
            {"Action":"build-output","ImportPath":"example.com/second","Output":"compile\n"}
            {"Action":"pass","Package":"example.com/first","Test":"TestAdd","Elapsed":0.25}
            {"Action":"future-action","Package":"example.com/second"}
            {"Action":"fail","Package":"example.com/second","Test":"TestSub","FailedBuild":"example.com/second"}
            """);

        Assert.Equal(6, result.TestEvents.Count);
        Assert.Single(result.BuildEvents);
        Assert.Equal("example.com/second", result.BuildEvents[0].ImportPath);
        Assert.Equal(["future-action"], result.UnknownActions);
        Assert.False(result.HasMalformedLines);
    }

    [Fact]
    public void Parse_marks_malformed_records_without_discarding_valid_events()
    {
        GoTestJsonParseResult result = GoTestJsonParser.Parse(
            "{\"Action\":\"start\",\"Package\":\"example.com/math\"}\nnot-json\n");

        Assert.Single(result.TestEvents);
        Assert.True(result.HasMalformedLines);
    }
}
