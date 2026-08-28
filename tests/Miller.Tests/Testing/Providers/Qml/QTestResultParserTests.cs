using Miller.Testing;
using Miller.Testing.Parsing;
using Miller.Testing.Providers.Qml;
using Xunit;

namespace Miller.Tests.Testing.Providers.Qml;

public sealed class QTestResultParserTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("miller-qtest-parser-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Parse_accepts_qt5_xunitxml_and_preserves_status_duration_and_failure()
    {
        string path = Write("qt5.xml", """
            <testsuite name="qmake-test" tests="2" failures="1">
              <testcase classname="Smoke" name="test_pass" time="0.125" />
              <testcase classname="Smoke" name="test_fail" time="0.250" result="fail">
                <failure message="expected true">actual false</failure>
              </testcase>
            </testsuite>
            """);

        ParsedTestArtifactRun result = QTestResultParser.Parse(path);

        Assert.Equal(2, result.Cases.Count);
        Assert.Equal("passed", result.Cases[0].Status);
        Assert.Equal(0.125, result.Cases[0].DurationSeconds);
        Assert.Equal("failed", result.Cases[1].Status);
        Assert.Equal("actual false", result.Cases[1].FailureText);
    }

    [Fact]
    public void Parse_maps_qtest_result_attributes_without_junit_child_elements()
    {
        string path = Write("results.xml", """
            <testsuite name="qml">
              <testcase name="test_pass" result="pass" />
              <testcase name="test_skip" result="skip" />
            </testsuite>
            """);

        ParsedTestArtifactRun result = QTestResultParser.Parse(path);

        Assert.Equal(["passed", "skipped"], result.Cases.Select(test => test.Status));
    }

    [Fact]
    public void Parse_accepts_qt6_junitxml_with_nested_suites()
    {
        string path = Write("qt6.xml", """
            <testsuites>
              <testsuite name="qml">
                <testcase classname="Smoke" name="test_pass" />
                <testcase classname="Smoke" name="test_skip"><skipped /></testcase>
              </testsuite>
            </testsuites>
            """);

        ParsedTestArtifactRun result = QTestResultParser.Parse(path);

        Assert.Equal(["passed", "skipped"], result.Cases.Select(test => test.Status));
    }

    [Fact]
    public void Parse_rejects_malformed_or_empty_reports()
    {
        string malformed = Write("malformed.xml", "<testsuite>");
        string empty = Write("empty.xml", "<testsuite name=\"qml\" />");

        var malformedException = Assert.Throws<TestArtifactParseException>(() => QTestResultParser.Parse(malformed));
        var emptyException = Assert.Throws<ContinuousTestProviderException>(() => QTestResultParser.Parse(empty));

        Assert.Contains("malformed XML", malformedException.Message, StringComparison.Ordinal);
        Assert.Contains("zero", emptyException.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_rejects_external_entities()
    {
        string path = Write("unsafe.xml", """
            <!DOCTYPE testsuite [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>
            <testsuite><testcase name="&xxe;" /></testsuite>
            """);

        var exception = Assert.Throws<TestArtifactParseException>(() => QTestResultParser.Parse(path));

        Assert.Contains("unsafe XML", exception.Message, StringComparison.Ordinal);
    }

    private string Write(string name, string content)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }
}
