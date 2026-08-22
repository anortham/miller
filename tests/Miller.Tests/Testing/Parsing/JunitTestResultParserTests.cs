using Miller.Testing.Parsing;
using Xunit;

namespace Miller.Tests.Testing.Parsing;

public sealed class JunitTestResultParserTests : IDisposable
{
    private readonly string _dir =
        Directory.CreateTempSubdirectory("miller-ct-junit-parser-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Junit_parser_reads_pass_fail_skip_and_duration()
    {
        var artifact = WriteArtifact(
            "junit_pytest.xml",
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <testsuite name="pytest" tests="3" failures="1" skipped="1" time="0.126">
              <testcase classname="tests.test_billing" name="test_charge_card" time="0.041" />
              <testcase classname="tests.test_billing" name="test_declined_card" time="0.052">
                <failure message="assert False">AssertionError: card declined</failure>
              </testcase>
              <testcase classname="tests.test_billing" name="test_refund_card" time="0.033">
                <skipped message="not implemented" />
              </testcase>
            </testsuite>
            """);

        var parsed = JunitTestResultParser.Parse(artifact);

        Assert.Equal("pytest", parsed.Framework);
        Assert.Equal(
            [
                ("test_charge_card", "passed"),
                ("test_declined_card", "failed"),
                ("test_refund_card", "skipped"),
            ],
            parsed.Cases.Select(testCase => (testCase.Name, testCase.Status)).ToArray());
        Assert.Equal("tests/test_billing::test_charge_card", parsed.Cases[0].Selector);
        Assert.Equal(0.052, parsed.Cases[1].DurationSeconds);
        Assert.Equal("AssertionError: card declined", parsed.Cases[1].FailureText);
    }

    /// <summary>
    /// Node's junit reporter names the source file of every case in a <c>file</c> attribute, and puts the
    /// same <c>classname</c> ("test", the directory) on all of them. The file attribute is therefore the
    /// only thing in the report that tells one test file from another — without it a partially red suite
    /// collapses to one verdict for every file (dogfood finding F9, 2026-08-21).
    /// </summary>
    [Fact]
    public void Junit_parser_captures_the_file_attribute_of_each_testcase()
    {
        var artifact = WriteArtifact(
            "junit_node.xml",
            """
            <?xml version="1.0" encoding="utf-8"?>
            <testsuites>
              <testcase name="a ok" time="0.001" classname="test" file="/repo/test/a.test.js" />
              <testcase name="b ok" time="0.001" classname="test" file="/repo/test/b.test.js" />
              <testsuite name="group" time="0.002" tests="1" failures="0">
                <testcase name="c ok" time="0.001" classname="test" file="/repo/test/c.test.js" />
              </testsuite>
              <testcase name="d fails" time="0.002" classname="test" file="/repo/test/d.test.js">
                <failure type="testCodeFailure" message="one is not two">one is not two</failure>
              </testcase>
            </testsuites>
            """);

        var parsed = JunitTestResultParser.Parse(artifact);

        Assert.Equal(
            [
                ("a ok", "passed", "/repo/test/a.test.js"),
                ("b ok", "passed", "/repo/test/b.test.js"),
                ("c ok", "passed", "/repo/test/c.test.js"),
                ("d fails", "failed", "/repo/test/d.test.js"),
            ],
            parsed.Cases.Select(testCase => (testCase.Name, testCase.Status, testCase.File)).ToArray());
        Assert.Equal("one is not two", parsed.Cases[3].FailureText);
    }

    /// <summary>A report whose cases carry no file attribute still parses; the file is simply unknown.</summary>
    [Fact]
    public void Junit_parser_reports_no_file_when_the_testcase_names_none()
    {
        var artifact = WriteArtifact(
            "junit_nofile.xml",
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <testsuite name="node" tests="1" failures="0">
              <testcase classname="math" name="adds" time="0.01" />
            </testsuite>
            """);

        var parsed = JunitTestResultParser.Parse(artifact);

        Assert.Null(Assert.Single(parsed.Cases).File);
    }

    [Fact]
    public void Junit_parser_rejects_xml_entities()
    {
        var artifact = WriteArtifact(
            "junit.xml",
            """
            <?xml version="1.0"?>
            <!DOCTYPE testsuite [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>
            <testsuite name="pytest"><testcase name="&xxe;" /></testsuite>
            """);

        var ex = Assert.Throws<TestArtifactParseException>(() => JunitTestResultParser.Parse(artifact));
        Assert.Equal("test_artifact.parse_error", ex.Code);
        Assert.Contains("unsafe XML", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Junit_parser_rejects_malformed_xml()
    {
        var artifact = WriteArtifact("broken.xml", "<testsuite><testcase name=\"oops\"");

        var ex = Assert.Throws<TestArtifactParseException>(() => JunitTestResultParser.Parse(artifact));
        Assert.Equal("test_artifact.parse_error", ex.Code);
        Assert.Contains("malformed XML", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Junit_parser_rejects_empty_path()
    {
        Assert.Throws<ArgumentException>(() => JunitTestResultParser.Parse(""));
        Assert.Throws<ArgumentException>(() => JunitTestResultParser.Parse("   "));
    }

    [Fact]
    public void Xunit_parser_maps_framework_names_to_selectors()
    {
        var artifact = WriteArtifact(
            "xunit_dotnet.xml",
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <assembly name="Payments.Tests" test-framework="xUnit.net" total="2" failed="1" skipped="0">
              <collection name="Payments">
                <test name="Payments.Tests.ChargeTests.ApprovesValidCard"
                      type="Payments.Tests.ChargeTests"
                      method="ApprovesValidCard"
                      time="0.014"
                      result="Pass" />
                <test name="Payments.Tests.ChargeTests.DeclinesExpiredCard"
                      type="Payments.Tests.ChargeTests"
                      method="DeclinesExpiredCard"
                      time="0.027"
                      result="Fail">
                  <failure exception-type="Xunit.Sdk.EqualException">
                    <message>Expected decline reason</message>
                  </failure>
                </test>
              </collection>
            </assembly>
            """);

        var parsed = JunitTestResultParser.Parse(artifact);

        Assert.Equal("xunit", parsed.Framework);
        Assert.Equal(
            [
                ("ApprovesValidCard", "passed"),
                ("DeclinesExpiredCard", "failed"),
            ],
            parsed.Cases.Select(testCase => (testCase.Name, testCase.Status)).ToArray());
        Assert.Equal("Payments/Tests/ChargeTests::DeclinesExpiredCard", parsed.Cases[1].Selector);
        Assert.Equal("Expected decline reason", parsed.Cases[1].FailureText);
    }

    private string WriteArtifact(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }
}
