using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Providers.Dotnet;

public sealed class MtpTestListParserTests
{
    [Fact]
    public void ParseText_requires_the_documented_header_and_returns_stable_cases()
    {
        var cases = MtpTestListParser.Parse(
            """
            The following Tests are available:
                VbMtpScale.UnitTests.Adds
                VbMtpScale.UnitTests.Positive (1)
            """,
            new MtpVersion(1, 7, 0),
            "mstest");

        Assert.Equal(
            [
                "mstest:VbMtpScale.UnitTests.Adds",
                "mstest:VbMtpScale.UnitTests.Positive (1)",
            ],
            cases.Select(testCase => testCase.Id).ToArray());
        Assert.All(cases, testCase => Assert.Equal("FullyQualifiedName", testCase.Metadata["selector_kind"]));
    }

    [Fact]
    public void ParseJson_requires_the_supported_shape_and_preserves_display_identity()
    {
        var cases = MtpTestListParser.Parse(
            """
            {"schemaVersion":1,"tests":[{"fullyQualifiedName":"VbMtpScale.UnitTests.Positive","displayName":"Positive (1)"},{"fullyQualifiedName":"VbMtpScale.UnitTests.Positive","displayName":"Positive (2)"}]}
            """,
            new MtpVersion(2, 3, 0),
            "mstest");

        Assert.Equal(
            [
                "mstest:VbMtpScale.UnitTests.Positive::display=Positive (1)",
                "mstest:VbMtpScale.UnitTests.Positive::display=Positive (2)",
            ],
            cases.Select(testCase => testCase.Id).ToArray());
        Assert.Equal("Positive (1)", cases[0].DisplayName);
    }

    [Fact]
    public void ParseJson_reads_the_platform_schema_type_identity()
    {
        var cases = MtpTestListParser.Parse(
            """
            {
              "schemaVersion": 1,
              "tests": [
                {
                  "uid": "case-1",
                  "displayName": "Adds",
                  "type": {
                    "namespace": "VbMtpScale",
                    "typeName": "UnitTests",
                    "methodName": "Adds"
                  }
                }
              ]
            }
            """,
            new MtpVersion(2, 3, 0),
            "mstest");

        var testCase = Assert.Single(cases);
        Assert.Equal("mstest:VbMtpScale.UnitTests.Adds", testCase.Id);
        Assert.Equal("VbMtpScale.UnitTests.Adds", testCase.FullyQualifiedName);
    }

    [Theory]
    [InlineData("", false, "header")]
    [InlineData("The following Tests are available:\n", false, "test case")]
    [InlineData("Test discovery summary: Zero tests ran\n", false, "only the discovery summary")]
    [InlineData("not a list", false, "header")]
    [InlineData("{\"tests\":[}", false, "JSON")]
    public void Parse_rejects_incomplete_or_malformed_output(string output, bool truncated, string expected)
    {
        var exception = Assert.Throws<ContinuousTestProviderException>(() => MtpTestListParser.Parse(
            output,
            output.StartsWith("{", StringComparison.Ordinal) ? new MtpVersion(2, 3, 0) : new MtpVersion(1, 7, 0),
            "mstest",
            truncated));

        Assert.Contains(expected, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_rejects_json_before_the_json_contract_version()
    {
        var exception = Assert.Throws<ContinuousTestProviderException>(() => MtpTestListParser.Parse(
            "{\"tests\":[{\"fullyQualifiedName\":\"Sample.Tests.Adds\"}]}",
            new MtpVersion(1, 7, 0),
            "mstest"));

        Assert.Contains("2.3", exception.Message, StringComparison.Ordinal);
    }
}
