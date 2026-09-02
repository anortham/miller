using Miller.Testing.Parsing;
using Miller.Testing.Providers.Php;
using Xunit;

namespace Miller.Tests.Testing.Providers.Php;

public sealed class PhpListTestsXmlParserTests
{
    [Fact]
    public void Parse_reads_phpunit_10_test_case_class_and_data_set_id()
    {
        IReadOnlyList<PhpListedTest> tests = PhpListTestsXmlParser.Parse(PhpUnit10Xml);

        Assert.Equal(2, tests.Count);
        Assert.Equal("Tests\\Unit\\CalculatorTest", tests[0].ClassName);
        Assert.Equal("testAdd", tests[0].MethodName);
        Assert.Equal("Tests\\Unit\\CalculatorTest::testAdd", tests[0].Selector);
        Assert.Equal(
            "Tests\\Unit\\CalculatorTest::testWithDataSet with data set #0",
            tests[1].Selector);
        Assert.Equal("testWithDataSet with data set #0", tests[1].MethodName);
    }

    [Fact]
    public void Parse_reads_phpunit_12_namespace_test_class_and_method_id()
    {
        IReadOnlyList<PhpListedTest> tests = PhpListTestsXmlParser.Parse(PhpUnit12Xml);

        Assert.Equal(2, tests.Count);
        Assert.Equal(
            [
                "Tests\\Unit\\CalculatorTest::testAdd",
                "Tests\\Unit\\CalculatorTest::testWithDataSet with data set \"fast\"",
            ],
            tests.Select(test => test.Selector).ToArray());
    }

    [Fact]
    public void Parse_rejects_a_method_id_that_disagrees_with_its_enclosing_class()
    {
        const string xml = """
            <tests>
              <testCaseClass name="Tests\Unit\CalculatorTest">
                <testCaseMethod id="Other\CalculatorTest::testAdd" name="testAdd" />
              </testCaseClass>
            </tests>
            """;

        Assert.Throws<TestArtifactParseException>(() => PhpListTestsXmlParser.Parse(xml));
    }

    [Fact]
    public void Parse_rejects_dtd_and_external_entities()
    {
        const string xml = """
            <!DOCTYPE tests [<!ENTITY expanded "testAdd">]>
            <tests>
              <testCaseClass name="Tests\Unit\CalculatorTest">
                <testCaseMethod id="Tests\Unit\CalculatorTest::&expanded;" name="testAdd" />
              </testCaseClass>
            </tests>
            """;

        Assert.Throws<TestArtifactParseException>(() => PhpListTestsXmlParser.Parse(xml));
    }

    private const string PhpUnit10Xml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <tests>
          <testCaseClass name="Tests\Unit\CalculatorTest">
            <testCaseMethod id="Tests\Unit\CalculatorTest::testAdd" name="testAdd" groups="" />
            <testCaseMethod id="Tests\Unit\CalculatorTest::testWithDataSet with data set #0" name="testWithDataSet" dataSet="#0" />
          </testCaseClass>
        </tests>
        """;

    private const string PhpUnit12Xml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <testSuite xmlns="https://xml.phpunit.de/testSuite">
          <tests>
            <testClass name="Tests\Unit\CalculatorTest" file="tests/Unit/CalculatorTest.php">
              <testMethod id="Tests\Unit\CalculatorTest::testAdd" name="testAdd" />
              <testMethod id="Tests\Unit\CalculatorTest::testWithDataSet with data set &quot;fast&quot;" name="testWithDataSet" />
            </testClass>
          </tests>
          <groups />
        </testSuite>
        """;
}
