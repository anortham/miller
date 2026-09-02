using System.Text.Json;
using Miller.Testing.Parsing;
using Miller.Testing.Providers.Shared;
using Xunit;

namespace Miller.Tests.Testing.Providers.Shared;

public sealed class JUnitXmlResultParserTests
{
    [Theory]
    [InlineData("gradle-junit.xml", "com.example.CalculatorTest", "com.example.CalculatorTest", "adds")]
    [InlineData("maven-surefire.xml", "com.example.CalculatorTest", "com.example.CalculatorTest", "adds")]
    [InlineData("sbt-junit.xml", "com.example.CalculatorSpec", "com.example.CalculatorSpec", "adds")]
    [InlineData("phpunit-junit.xml", @"Tests\\Unit\\CalculatorTest", @"Tests\\Unit\\CalculatorTest", "testAdd")]
    [InlineData("gut-junit.xml", "res://tests/test_math.gd", "res://tests/test_math.gd", "test_add")]
    public void Parse_reads_each_supported_fixture(
        string fixtureName,
        string expectedSuiteName,
        string expectedClassName,
        string expectedName)
    {
        JUnitXmlParseResult result = JUnitXmlResultParser.Parse(ReadFixture(fixtureName));

        Assert.Equal(3, result.Cases.Count);
        Assert.Equal(expectedSuiteName, result.Cases[0].SuiteName);
        Assert.Equal(expectedClassName, result.Cases[0].ClassName);
        Assert.Equal(expectedName, result.Cases[0].Name);
        Assert.Equal("passed", result.Cases[0].Status);
        Assert.False(result.HasAggregateMismatch);
    }

    [Fact]
    public void Parse_maps_failure_error_and_skipped_dialects()
    {
        JUnitXmlParseResult gradle = JUnitXmlResultParser.Parse(ReadFixture("gradle-junit.xml"));
        JUnitXmlParseResult sbt = JUnitXmlResultParser.Parse(ReadFixture("sbt-junit.xml"));
        JUnitXmlParseResult phpunit = JUnitXmlResultParser.Parse(ReadFixture("phpunit-junit.xml"));
        JUnitXmlParseResult gut = JUnitXmlResultParser.Parse(ReadFixture("gut-junit.xml"));

        Assert.Equal(["passed", "failed", "skipped"], gradle.Cases.Select(testCase => testCase.Status).ToArray());
        Assert.Equal(["passed", "errored", "skipped"], sbt.Cases.Select(testCase => testCase.Status).ToArray());
        Assert.Equal(["passed", "failed", "skipped"], phpunit.Cases.Select(testCase => testCase.Status).ToArray());
        Assert.Equal(["passed", "failed", "skipped"], gut.Cases.Select(testCase => testCase.Status).ToArray());

        Assert.Equal("expected 2 but was 3", gradle.Cases[1].FailureMessage);
        Assert.Equal("org.opentest4j.AssertionFailedError: expected: <2> but was: <3>", gradle.Cases[1].FailureText);
        Assert.Equal("fixture setup failed", sbt.Cases[1].FailureMessage);
        Assert.Equal("java.lang.IllegalStateException: fixture setup failed", sbt.Cases[1].FailureText);
        Assert.Equal("Failed asserting that 3 is identical to 2.", phpunit.Cases[1].FailureMessage);
        Assert.Equal("Cannot compare Int[3] to Int[2].\nat line 18", gut.Cases[1].FailureText);
    }

    [Fact]
    public void Parse_preserves_durations_and_missing_time()
    {
        JUnitXmlParseResult result = JUnitXmlResultParser.Parse(ReadFixture("maven-surefire.xml"));
        JUnitXmlParseResult gut = JUnitXmlResultParser.Parse(ReadFixture("gut-junit.xml"));

        Assert.Equal(0.003, result.Cases[0].DurationSeconds);
        Assert.Equal(0.004, result.Cases[1].DurationSeconds);
        Assert.Null(gut.Cases[0].DurationSeconds);
        Assert.Null(gut.Cases[1].DurationSeconds);
    }

    [Fact]
    public void Parse_uses_phpunit_class_attribute_when_classname_is_missing()
    {
        const string xml = """
            <testsuite name="phpunit">
              <testcase name="testAdd" class="Tests\\Unit\\CalculatorTest" time="0.1" />
            </testsuite>
            """;

        JUnitXmlParseResult result = JUnitXmlResultParser.Parse(xml);

        Assert.Equal(@"Tests\\Unit\\CalculatorTest", result.Cases[0].ClassName);
    }

    [Fact]
    public void Parse_flags_suite_aggregate_counts_that_disagree_with_case_rows()
    {
        const string xml = """
            <testsuite name="inconsistent" tests="2" failures="0" errors="0" skipped="0">
              <testcase name="passes" />
              <testcase name="fails"><failure message="bad">details</failure></testcase>
            </testsuite>
            """;

        JUnitXmlParseResult result = JUnitXmlResultParser.Parse(xml);

        Assert.True(result.HasAggregateMismatch);
        Assert.False(result.IsAggregateConsistent);
        Assert.NotEmpty(result.AggregateMismatches);
        Assert.Contains(result.AggregateMismatches, mismatch =>
            mismatch.AttributeName.Equals("failures", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_flags_root_aggregate_counts_for_nested_suites()
    {
        const string xml = """
            <testsuites tests="2" failures="1" errors="0" skipped="0">
              <testsuite name="one" tests="1"><testcase name="passes" /></testsuite>
              <testsuite name="two" tests="1"><testcase name="fails"><failure>details</failure></testcase></testsuite>
            </testsuites>
            """;

        JUnitXmlParseResult result = JUnitXmlResultParser.Parse(xml);

        Assert.Equal(2, result.Cases.Count);
        Assert.False(result.HasAggregateMismatch);
    }

    [Fact]
    public void Parse_counts_status_attributes_when_child_elements_are_absent()
    {
        const string xml = """
            <testsuite name="gut" tests="3" failures="1" errors="0" skipped="1">
              <testcase name="passes" status="pass" />
              <testcase name="fails" status="fail" />
              <testcase name="pending" status="pending" />
            </testsuite>
            """;

        JUnitXmlParseResult result = JUnitXmlResultParser.Parse(xml);

        Assert.Equal(["passed", "failed", "skipped"], result.Cases.Select(testCase => testCase.Status).ToArray());
        Assert.False(result.HasAggregateMismatch);
    }

    [Fact]
    public void Parse_rejects_malformed_xml_instead_of_returning_empty_result()
    {
        Assert.Throws<TestArtifactParseException>(() => JUnitXmlResultParser.Parse("<testsuite><testcase /></testsuite>"));
        Assert.Throws<TestArtifactParseException>(() => JUnitXmlResultParser.Parse("<testsuites><testsuite>"));
    }

    [Fact]
    public void Parse_reports_unknown_status_as_errored_with_a_diagnostic()
    {
        JUnitXmlParseResult result = JUnitXmlResultParser.Parse(
            "<testsuite><testcase name=\"future\" status=\"aborted\" /></testsuite>");

        Assert.Equal("errored", result.Cases[0].Status);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Contains("unsupported status", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_rejects_external_entities()
    {
        const string xml = """
            <!DOCTYPE testsuite [ <!ENTITY secret "hidden"> ]>
            <testsuite name="unsafe"><testcase name="&secret;" /></testsuite>
            """;

        TestArtifactParseException exception = Assert.Throws<TestArtifactParseException>(() => JUnitXmlResultParser.Parse(xml));

        Assert.Contains("unsafe XML", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseFile_reads_the_report_without_changing_the_parse_contract()
    {
        string path = Path.Combine(Path.GetTempPath(), $"junit-{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, ReadFixture("gradle-junit.xml"));
        try
        {
            JUnitXmlParseResult result = JUnitXmlResultParser.ParseFile(path);

            Assert.Equal("com.example.CalculatorTest", result.Cases[1].ClassName);
            Assert.Equal("failed", result.Cases[1].Status);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Parse_result_is_json_serializable()
    {
        JUnitXmlParseResult result = JUnitXmlResultParser.Parse(ReadFixture("gut-junit.xml"));

        string json = JsonSerializer.Serialize(result);

        Assert.Contains("test_add", json, StringComparison.Ordinal);
        Assert.Contains("res://tests/test_math.gd", json, StringComparison.Ordinal);
    }

    private static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(
            ScaleTestSupport.RepoRoot(),
            "tests",
            "Miller.Tests",
            "Testing",
            "Providers",
            "Fixtures",
            name));
}
