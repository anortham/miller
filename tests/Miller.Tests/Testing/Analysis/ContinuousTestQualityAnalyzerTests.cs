using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Analysis;

public sealed class ContinuousTestQualityAnalyzerTests
{
    [Fact]
    public void Analyze_test_flags_placeholder_body()
    {
        var findings = ContinuousTestQualityAnalyzer.AnalyzeTestQuality(
            "def test_charge():\n    pass\n",
            TestCase(),
            Symbol("test_charge", 1, 2));

        var finding = Assert.Single(findings);
        Assert.Equal("placeholder_test", finding.FindingType);
        Assert.Equal("tc:test_charge", finding.TestCaseId);
        Assert.Equal("sym:test_charge", finding.SymbolName);
    }

    [Fact]
    public void Analyze_test_flags_no_assertion_when_identifier_evidence_exists()
    {
        var findings = ContinuousTestQualityAnalyzer.AnalyzeTestQuality(
            "def test_charge():\n    charge_card()\n",
            TestCase(),
            Symbol("test_charge", 1, 2));

        var finding = Assert.Single(findings);
        Assert.Equal("no_assertion", finding.FindingType);
        Assert.Equal(["charge_card"], StringList(finding.Evidence["identifier_evidence"]));
    }

    [Fact]
    public void Analyze_test_ignores_no_assertion_without_identifier_evidence()
    {
        var findings = ContinuousTestQualityAnalyzer.AnalyzeTestQuality(
            "def test_charge():\n    value = 1\n",
            TestCase(),
            Symbol("test_charge", 1, 2));

        Assert.Empty(findings);
    }

    [Fact]
    public void Analyze_test_flags_tautological_assert_true()
    {
        var findings = ContinuousTestQualityAnalyzer.AnalyzeTestQuality(
            "def test_charge():\n    assert True\n",
            TestCase(),
            Symbol("test_charge", 1, 2));

        Assert.Equal(["tautological_assertion"], FindingTypes(findings));
    }

    [Fact]
    public void Analyze_test_flags_smoke_only_calling_function()
    {
        var findings = ContinuousTestQualityAnalyzer.AnalyzeTestQuality(
            "def test_charge():\n    result = charge_card()\n",
            TestCase(),
            Symbol("test_charge", 1, 2));

        var finding = Assert.Single(findings);
        Assert.Equal("smoke_only", finding.FindingType);
        Assert.Equal(["charge_card"], StringList(finding.Evidence["calls"]));
    }

    [Fact]
    public void Analyze_test_flags_skip_without_reason()
    {
        var findings = ContinuousTestQualityAnalyzer.AnalyzeTestQuality(
            "@pytest.mark.skip\ndef test_charge():\n    assert charge_card() == 1\n",
            TestCase(),
            Symbol("test_charge", 2, 3));

        Assert.Equal(["skip_without_reason"], FindingTypes(findings));
    }

    [Fact]
    public void Analyze_test_flags_copy_paste_duplicate()
    {
        var content =
            "def test_charge_card():\n" +
            "    result = charge_card(123)\n" +
            "    assert result == 200\n" +
            "\n" +
            "def test_charge_card_again():\n" +
            "    result = charge_card(456)\n" +
            "    assert result == 201\n";

        var findings = ContinuousTestQualityAnalyzer.AnalyzeTestQuality(
            content,
            TestCase("test_charge_card"),
            Symbol("test_charge_card", 1, 3));

        var finding = Assert.Single(findings);
        Assert.Equal("copy_paste_test", finding.FindingType);
        Assert.Equal("test_charge_card_again", finding.Evidence["duplicate"]);
    }

    [Fact]
    public void Analyze_test_does_not_score_fixture_setup_as_test_case()
    {
        var findings = ContinuousTestQualityAnalyzer.AnalyzeTestQuality(
            "def fixture_user():\n    pass\n",
            TestCase("fixture_user", ContinuousTestRole.FixtureSetup),
            Symbol("fixture_user", 1, 2));

        Assert.Empty(findings);
    }

    [Fact]
    public void Analyze_test_accepts_snapshot_file_assertion()
    {
        var findings = ContinuousTestQualityAnalyzer.AnalyzeTestQuality(
            "def test_snapshot(snapshot_path):\n    assert snapshot_path.read_text() == 'expected snapshot'\n",
            TestCase("test_snapshot"),
            Symbol("test_snapshot", 1, 2));

        Assert.Empty(findings);
    }

    [Fact]
    public void Analyze_implementation_flags_stub_and_canned_return_bodies()
    {
        var content =
            "def empty():\n" +
            "    pass\n\n" +
            "def elided():\n" +
            "    ...\n\n" +
            "def later():\n" +
            "    raise NotImplementedError\n\n" +
            "def canned():\n" +
            "    return True\n";

        var findings = ContinuousTestQualityAnalyzer.AnalyzeImplementationQuality(
                content,
                Symbol("empty", 1, 2, "src/service.py"))
            .Concat(ContinuousTestQualityAnalyzer.AnalyzeImplementationQuality(
                content,
                Symbol("elided", 4, 5, "src/service.py")))
            .Concat(ContinuousTestQualityAnalyzer.AnalyzeImplementationQuality(
                content,
                Symbol("later", 7, 8, "src/service.py")))
            .Concat(ContinuousTestQualityAnalyzer.AnalyzeImplementationQuality(
                content,
                Symbol("canned", 10, 11, "src/service.py")))
            .ToArray();

        Assert.Equal(
            ["stub_implementation", "stub_implementation", "stub_implementation", "canned_return"],
            FindingTypes(findings));
    }

    [Fact]
    public void Analyzer_reuses_parsed_functions_for_multiple_symbols()
    {
        var analyzer = new ContinuousTestQualityAnalyzer(
            "def empty():\n    pass\n\ndef canned():\n    return True\n");

        var findings = analyzer.AnalyzeImplementation(Symbol("empty", 1, 2, "src/service.py"))
            .Concat(analyzer.AnalyzeImplementation(Symbol("canned", 4, 5, "src/service.py")))
            .ToArray();

        Assert.Equal(["stub_implementation", "canned_return"], FindingTypes(findings));
        Assert.Equal(1, analyzer.ParseCount);
    }

    private static string[] FindingTypes(IEnumerable<ContinuousTestQualityFinding> findings) =>
        findings.Select(finding => finding.FindingType).ToArray();

    private static string[] FindingTypes(IEnumerable<ContinuousImplementationQualityFinding> findings) =>
        findings.Select(finding => finding.FindingType).ToArray();

    private static string[] StringList(object? value) =>
        value is IEnumerable<string> strings ? strings.ToArray() : [];

    private static ContinuousTestCase TestCase(
        string name = "test_charge",
        ContinuousTestRole role = ContinuousTestRole.TestCase) =>
        new(
            Id: $"tc:{name}",
            WorkspaceId: "ws:1",
            Name: name,
            QualifiedName: name,
            Selector: $"tests/test_service.py::{name}",
            FilePath: "tests/test_service.py",
            SymbolName: $"sym:{name}",
            SymbolPath: "tests/test_service.py",
            Framework: "pytest",
            Role: role,
            Source: "extractor_metadata",
            Confidence: 1.0);

    private static ContinuousTestQualitySymbol Symbol(
        string name,
        int startLine,
        int endLine,
        string filePath = "tests/test_service.py") =>
        new(
            Id: $"sym:{name}",
            WorkspaceId: "ws:1",
            Name: name,
            FilePath: filePath,
            StartLine: startLine,
            EndLine: endLine);
}
