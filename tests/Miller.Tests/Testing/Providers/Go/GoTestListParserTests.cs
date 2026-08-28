using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Providers.Go;

public sealed class GoTestListParserTests
{
    [Fact]
    public void Parse_returns_only_top_level_test_functions()
    {
        GoTestListResult result = GoTestListParser.Parse("""
            TestAdd
            Test
            Test1
            Test_Name
            TestÄ
            TestΩ
            TestAdd/subtest
            Testa
            ExampleAdd
            BenchmarkAdd
            FuzzAdd
            ok example.com/math 0.001s
            """);

        Assert.Equal(["TestAdd", "Test", "Test1", "Test_Name", "TestÄ", "TestΩ"], result.Names);
        Assert.False(result.HasMalformedLines);
    }

    [Fact]
    public void Parse_marks_unrecognized_listing_lines_as_incomplete()
    {
        GoTestListResult result = GoTestListParser.Parse("TestAdd\nTestAdd: compiler noise\n");

        Assert.Equal(["TestAdd"], result.Names);
        Assert.True(result.HasMalformedLines);
    }
}
