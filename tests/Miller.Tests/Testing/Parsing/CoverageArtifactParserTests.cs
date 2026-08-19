using Miller.Testing.Parsing;
using Xunit;

namespace Miller.Tests.Testing.Parsing;

public sealed class CoverageArtifactParserTests : IDisposable
{
    private readonly string _dir =
        Directory.CreateTempSubdirectory("miller-ct-coverage-parser-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Lcov_parser_reads_test_name_file_and_line_hits()
    {
        var artifact = WriteArtifact(
            "lcov.info",
            """
            TN:tests/test_service::test_run
            SF:src/service.py
            DA:1,1
            DA:2,0
            end_of_record
            """);

        var parsed = CoverageArtifactParser.ParseLcov(artifact);

        var file = Assert.Single(parsed.Files);
        Assert.Equal("lcov", file.Format);
        Assert.Equal("src/service.py", file.SourcePath);
        Assert.Equal("tests/test_service::test_run", file.TestName);
        Assert.Equal([(1, 1), (2, 0)], file.LineHits.Select(hit => (hit.LineNumber, hit.Hits)).ToArray());
    }

    [Fact]
    public void Cobertura_parser_reads_file_and_line_hits()
    {
        var artifact = WriteArtifact(
            "cobertura.xml",
            """
            <?xml version="1.0"?>
            <coverage>
              <packages>
                <package name="payments">
                  <classes>
                    <class filename="src/payments/service.py">
                      <lines>
                        <line number="4" hits="1" />
                        <line number="5" hits="0" />
                      </lines>
                    </class>
                  </classes>
                </package>
              </packages>
            </coverage>
            """);

        var parsed = CoverageArtifactParser.ParseCobertura(artifact);

        var file = Assert.Single(parsed.Files);
        Assert.Equal("cobertura", file.Format);
        Assert.Equal("src/payments/service.py", file.SourcePath);
        Assert.Null(file.TestName);
        Assert.Equal([(4, 1), (5, 0)], file.LineHits.Select(hit => (hit.LineNumber, hit.Hits)).ToArray());
    }

    [Fact]
    public void Cobertura_parser_rejects_xml_entities()
    {
        var artifact = WriteArtifact(
            "cobertura.xml",
            """
            <?xml version="1.0"?>
            <!DOCTYPE coverage [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>
            <coverage>
              <packages>
                <package><classes><class filename="&xxe;" /></classes></package>
              </packages>
            </coverage>
            """);

        var ex = Assert.Throws<TestArtifactParseException>(() => CoverageArtifactParser.ParseCobertura(artifact));
        Assert.Equal("test_artifact.parse_error", ex.Code);
        Assert.Contains("unsafe XML", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cobertura_parser_rejects_malformed_xml()
    {
        var artifact = WriteArtifact("broken.xml", "<coverage><packages>");

        var ex = Assert.Throws<TestArtifactParseException>(() => CoverageArtifactParser.ParseCobertura(artifact));
        Assert.Equal("test_artifact.parse_error", ex.Code);
        Assert.Contains("malformed XML", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Lcov_parser_rejects_malformed_da_line()
    {
        var artifact = WriteArtifact(
            "lcov.info",
            """
            SF:src/service.py
            DA:not-a-number,1
            end_of_record
            """);

        var ex = Assert.Throws<TestArtifactParseException>(() => CoverageArtifactParser.ParseLcov(artifact));
        Assert.Equal("test_artifact.parse_error", ex.Code);
        Assert.Contains("invalid LCOV DA line", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Coverage_parser_rejects_unsupported_parser_and_empty_path()
    {
        var artifact = WriteArtifact("lcov.info", "SF:src/a.py\nend_of_record\n");
        var ex = Assert.Throws<TestArtifactParseException>(
            () => CoverageArtifactParser.Parse(artifact, "unknown"));
        Assert.Equal("test_artifact.parse_error", ex.Code);
        Assert.Contains("unsupported coverage parser", ex.Message, StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() => CoverageArtifactParser.ParseLcov(""));
        Assert.Throws<ArgumentException>(() => CoverageArtifactParser.ParseCobertura("   "));
    }

    private string WriteArtifact(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }
}
