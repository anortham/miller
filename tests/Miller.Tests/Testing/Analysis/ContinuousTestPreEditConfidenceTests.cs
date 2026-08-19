using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Analysis;

public sealed class ContinuousTestPreEditConfidenceTests
{
    [Fact]
    public void Pre_edit_confidence_verification_uses_pytest_for_python_file_selectors()
    {
        var pack = ContinuousTestPreEditConfidence.BuildPreEditConfidencePack(
            workspaceId: "ws:1",
            likelyTests:
            [
                new Dictionary<string, object?>
                {
                    ["selector"] = "tests/payments/test_service.py::test_charge",
                    ["path"] = "tests/payments/test_service.py",
                    ["tier"] = "coverage",
                    ["confidence"] = 0.9,
                    ["explanation"] = "coverage artifact covers src/payments/service.py:1",
                    ["test_case_id"] = "tc:charge",
                },
            ],
            confidenceSummary: new Dictionary<string, object?>());

        var verification = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(pack["verification"]);
        Assert.Equal("pytest tests/payments/test_service.py::test_charge -q", verification["command"]);
        Assert.Equal(
            ["tests/payments/test_service.py::test_charge"],
            Assert.IsAssignableFrom<IReadOnlyList<string>>(verification["selectors"]));
    }

    [Fact]
    public void Pre_edit_confidence_does_not_emit_pytest_for_non_file_selectors()
    {
        var pack = ContinuousTestPreEditConfidence.BuildPreEditConfidencePack(
            workspaceId: "ws:1",
            likelyTests:
            [
                new Dictionary<string, object?>
                {
                    ["selector"] = "tests/js_payments_test::test_js_charge_card",
                    ["path"] = "tests/js_payments_test",
                    ["tier"] = "coverage",
                    ["confidence"] = 0.9,
                    ["explanation"] = "coverage artifact covers src/js_payments/service.js:1",
                    ["test_case_id"] = "tc:js",
                },
                new Dictionary<string, object?>
                {
                    ["selector"] = "coverage:src/payments/service.cs:5",
                    ["path"] = "src/payments/service.cs",
                    ["tier"] = "coverage",
                    ["confidence"] = 0.72,
                    ["explanation"] = "aggregate coverage artifact covers src/payments/service.cs:5",
                    ["coverage"] = new Dictionary<string, object?> { ["covered_lines"] = 1, ["hit_lines"] = 1 },
                },
                new Dictionary<string, object?>
                {
                    ["selector"] = "src/payments/service.py::test_charge",
                    ["path"] = "src/payments/service.py",
                    ["tier"] = "coverage",
                    ["confidence"] = 0.9,
                    ["explanation"] = "misreported artifact selector points at source file",
                    ["test_case_id"] = "tc:source",
                },
            ],
            confidenceSummary: new Dictionary<string, object?>());

        var verification = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(pack["verification"]);
        Assert.Null(verification["command"]);
        Assert.Equal([], Assert.IsAssignableFrom<IReadOnlyList<string>>(verification["selectors"]));
        Assert.Equal("no pytest-compatible selectors were identified", verification["reason"]);
    }
}
