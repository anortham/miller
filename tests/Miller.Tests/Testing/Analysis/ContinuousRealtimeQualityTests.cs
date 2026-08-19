using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Analysis;

public sealed class ContinuousRealtimeQualityTests
{
    [Fact]
    public void Analyze_new_text_flags_new_no_assertion_test()
    {
        var findings = ContinuousRealtimeQuality.AnalyzeNewText(
            path: "tests/test_demo.py",
            language: "python",
            newText: "def test_demo():\n    run_demo()\n");

        Assert.Contains(findings, finding => finding.Code == "test_no_assertion");
    }

    [Fact]
    public void Analyze_new_text_flags_new_stub_body()
    {
        var findings = ContinuousRealtimeQuality.AnalyzeNewText(
            path: "app.py",
            language: "python",
            newText: "def run():\n    pass\n");

        Assert.Contains(findings, finding => finding.Code == "implementation_stub");
    }

    [Fact]
    public void Analyze_diff_flags_zero_context_test_hunk_without_function_header()
    {
        var diff =
            "diff --git a/tests/test_demo.py b/tests/test_demo.py\n" +
            "--- a/tests/test_demo.py\n" +
            "+++ b/tests/test_demo.py\n" +
            "@@ -1,0 +2 @@\n" +
            "+    run_demo()\n";

        var findings = ContinuousRealtimeQuality.AnalyzeDiff(diff);

        Assert.Contains(findings, finding => finding.Code == "test_no_assertion");
    }

    [Fact]
    public void Analyze_diff_flags_zero_context_source_hunk_without_function_header()
    {
        var diff =
            "diff --git a/src/app.py b/src/app.py\n" +
            "--- a/src/app.py\n" +
            "+++ b/src/app.py\n" +
            "@@ -1,0 +2 @@\n" +
            "+    pass\n";

        var findings = ContinuousRealtimeQuality.AnalyzeDiff(diff);

        Assert.Contains(findings, finding => finding.Code == "implementation_stub");
    }
}
