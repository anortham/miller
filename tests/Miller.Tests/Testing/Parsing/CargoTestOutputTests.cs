using Miller.Testing.Parsing;
using Xunit;

namespace Miller.Tests.Testing.Parsing;

public sealed class CargoTestOutputTests
{
    private const string AllPassStdout =
        "\n" +
        "running 2 tests\n" +
        "test tests::slow_path ... ignored, not ready yet\n" +
        "test tests::add_works ... ok\n" +
        "\n" +
        "test result: ok. 1 passed; 0 failed; 1 ignored; 0 measured; 0 filtered out; finished in 0.00s\n" +
        "\n" +
        "\n" +
        "running 1 test\n" +
        "test integration_ok ... ok\n" +
        "\n" +
        "test result: ok. 1 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.00s\n" +
        "\n" +
        "\n" +
        "running 1 test\n" +
        "test src/lib.rs - add (line 3) ... ok\n" +
        "\n" +
        "test result: ok. 1 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.29s\n" +
        "\n";

    private const string FailingStdout =
        "\n" +
        "running 4 tests\n" +
        "test tests::slow_path ... ignored, not ready yet\n" +
        "test tests::add_works ... ok\n" +
        "test tests::explicit_boom ... FAILED\n" +
        "test tests::bad_math ... FAILED\n" +
        "\n" +
        "failures:\n" +
        "\n" +
        "---- tests::explicit_boom stdout ----\n" +
        "\n" +
        "thread 'tests::explicit_boom' (14090144) panicked at src/lib.rs:26:9:\n" +
        "kaboom\n" +
        "note: run with `RUST_BACKTRACE=1` environment variable to display a backtrace\n" +
        "\n" +
        "---- tests::bad_math stdout ----\n" +
        "\n" +
        "thread 'tests::bad_math' (14090143) panicked at src/lib.rs:21:9:\n" +
        "assertion `left == right` failed\n" +
        "  left: 4\n" +
        " right: 5\n" +
        "\n" +
        "\n" +
        "failures:\n" +
        "    tests::bad_math\n" +
        "    tests::explicit_boom\n" +
        "\n" +
        "test result: FAILED. 1 passed; 2 failed; 1 ignored; 0 measured; 0 filtered out; finished in 0.00s\n" +
        "\n";

    private const string CompileErrorStderr =
        "   Compiling fixturelib v0.1.0 (/private/tmp/eros-cargo-fixture.iKEuRm)\n" +
        "error[E0308]: mismatched types\n" +
        "  --> src/lib.rs:11:22\n" +
        "   |\n" +
        "11 |         let x: u32 = add(2, 2);\n" +
        "   |                ---   ^^^^^^^^^ expected `u32`, found `i32`\n" +
        "   |\n" +
        "For more information about this error, try `rustc --explain E0308`.\n" +
        "error: could not compile `fixturelib` (lib test) due to 1 previous error\n";

    private const string NoCaptureGarbledFailStdout =
        "\n" +
        "running 2 tests\n" +
        "test tests::noisy_fail ... GARBLE>>> FAILED\n" +
        "test tests::plain_ok ... ok\n" +
        "\n" +
        "failures:\n" +
        "\n" +
        "failures:\n" +
        "    tests::noisy_fail\n" +
        "\n" +
        "test result: FAILED. 1 passed; 1 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.00s\n" +
        "\n";

    private const string NoCaptureGarbledPassStdout =
        "\n" +
        "running 2 tests\n" +
        "test tests::clean ... ok\n" +
        "test tests::noisy ... GARBLE-no-newlineok\n" +
        "\n" +
        "test result: ok. 2 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.00s\n" +
        "\n";

    [Fact]
    public void All_pass_counts_across_lib_integration_and_doctest_binaries()
    {
        var output = CargoTestOutput.Parse(AllPassStdout);

        Assert.Equal(3, output.Passed);
        Assert.Equal(0, output.Failed);
        Assert.Equal(1, output.Ignored);
        Assert.True(output.HasTestResultLine);
        Assert.Empty(output.FailingTestNames);
        Assert.Empty(output.Failures);
        Assert.Null(output.RunFailureSummary());
    }

    [Fact]
    public void Failing_run_parses_counts_names_and_panic_summaries()
    {
        var output = CargoTestOutput.Parse(FailingStdout);

        Assert.Equal(1, output.Passed);
        Assert.Equal(2, output.Failed);
        Assert.Equal(1, output.Ignored);
        Assert.Equal(["tests::explicit_boom", "tests::bad_math"], output.FailingTestNames.ToArray());

        var byName = output.Failures.ToDictionary(f => f.TestName, StringComparer.Ordinal);
        Assert.Contains("panicked at", byName["tests::explicit_boom"].Summary, StringComparison.Ordinal);
        Assert.Contains("src/lib.rs:26:9", byName["tests::explicit_boom"].Summary, StringComparison.Ordinal);
        Assert.Contains("panicked at", byName["tests::bad_math"].Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Failing_run_summary_names_failed_tests()
    {
        var output = CargoTestOutput.Parse(FailingStdout);

        Assert.Equal(
            "2 failed / 1 passed: tests::explicit_boom, tests::bad_math",
            output.RunFailureSummary());
    }

    [Fact]
    public void Summary_truncates_name_list_past_the_cap()
    {
        var output = CargoTestOutput.Parse(
            "running 5 tests\n" +
            "test a ... FAILED\n" +
            "test b ... FAILED\n" +
            "test c ... FAILED\n" +
            "test d ... FAILED\n" +
            "test e ... FAILED\n" +
            "\n" +
            "test result: FAILED. 0 passed; 5 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.00s\n");

        Assert.Equal("5 failed / 0 passed: a, b, c (+2 more)", output.RunFailureSummary());
    }

    [Fact]
    public void Compile_error_stdout_has_no_failures_and_stderr_yields_first_error_line()
    {
        var output = CargoTestOutput.Parse(string.Empty);

        Assert.Equal(0, output.Failed);
        Assert.False(output.HasTestResultLine);
        Assert.Null(output.RunFailureSummary());

        Assert.Equal(
            "error[E0308]: mismatched types",
            CargoTestOutput.FirstErrorLine(CompileErrorStderr));
    }

    [Fact]
    public void First_error_line_skips_build_progress_and_returns_null_without_errors()
    {
        Assert.Null(CargoTestOutput.FirstErrorLine(
            "   Compiling fixturelib v0.1.0 (/tmp/x)\n" +
            "    Finished `test` profile [unoptimized + debuginfo] target(s) in 0.67s\n" +
            "     Running unittests src/lib.rs (target/debug/deps/fixturelib-abc)\n"));
    }

    [Fact]
    public void Nocapture_garbled_failure_still_counts_and_names_from_summary_and_list()
    {
        var output = CargoTestOutput.Parse(NoCaptureGarbledFailStdout);

        Assert.Equal(1, output.Passed);
        Assert.Equal(1, output.Failed);
        Assert.Equal(["tests::noisy_fail"], output.FailingTestNames.ToArray());
        Assert.Equal("1 failed / 1 passed: tests::noisy_fail", output.RunFailureSummary());
    }

    [Theory]
    [InlineData("   Compiling fixturelib v0.1.0 (/tmp/x)")]
    [InlineData("    Finished `test` profile [unoptimized + debuginfo] target(s) in 0.67s")]
    [InlineData("     Running unittests src/lib.rs (target/debug/deps/fixturelib-abc)")]
    [InlineData("  Downloading crates ...")]
    public void Build_progress_noise_is_recognized(string line)
    {
        Assert.True(CargoTestOutput.IsBuildProgressNoise(line));
    }

    [Theory]
    [InlineData("error[E0308]: mismatched types")]
    [InlineData("2 failed / 1 passed: tests::noisy_fail")]
    [InlineData("cargo test failed with exit code 101.")]
    public void Honest_summaries_are_not_build_progress_noise(string line)
    {
        Assert.False(CargoTestOutput.IsBuildProgressNoise(line));
    }

    [Fact]
    public void Results_by_name_attributes_each_parsed_line_to_an_outcome()
    {
        var output = CargoTestOutput.Parse(FailingStdout);

        Assert.Equal("passed", output.ResultsByName["tests::add_works"]);
        Assert.Equal("skipped", output.ResultsByName["tests::slow_path"]);
        Assert.Equal("failed", output.ResultsByName["tests::explicit_boom"]);
        Assert.Equal("failed", output.ResultsByName["tests::bad_math"]);
        Assert.False(output.HasParseAnomaly);
        Assert.Contains("panicked at", output.FailureSummaryFor("tests::bad_math")!, StringComparison.Ordinal);
        Assert.Null(output.FailureSummaryFor("tests::add_works"));
    }

    [Fact]
    public void Garbled_pass_line_stays_unattributed_and_flags_a_parse_anomaly()
    {
        var output = CargoTestOutput.Parse(NoCaptureGarbledPassStdout);

        Assert.Equal("passed", output.ResultsByName["tests::clean"]);
        Assert.False(output.ResultsByName.ContainsKey("tests::noisy"));
        Assert.Equal(1, output.AttributedResultCount);
        Assert.Equal(2, output.SummaryTotal);
        Assert.True(output.HasParseAnomaly);
    }

    [Fact]
    public void Garbled_failure_line_is_recovered_from_the_failures_list_without_an_anomaly()
    {
        var output = CargoTestOutput.Parse(NoCaptureGarbledFailStdout);

        Assert.Equal("failed", output.ResultsByName["tests::noisy_fail"]);
        Assert.Equal("passed", output.ResultsByName["tests::plain_ok"]);
        Assert.False(output.HasParseAnomaly);
    }

    [Fact]
    public void Target_duration_sums_finished_in_across_binaries()
    {
        var output = CargoTestOutput.Parse(AllPassStdout);

        Assert.NotNull(output.TargetDurationSeconds);
        Assert.Equal(0.29, output.TargetDurationSeconds!.Value, precision: 2);
    }

    [Fact]
    public void Target_duration_is_null_without_a_result_line()
    {
        Assert.Null(CargoTestOutput.Parse(string.Empty).TargetDurationSeconds);
    }

    [Fact]
    public void First_error_line_matches_a_panicked_stderr_line()
    {
        Assert.Equal(
            "thread 'main' panicked at src/main.rs:3:5:",
            CargoTestOutput.FirstErrorLine(
                "   Compiling x v0.1.0 (/tmp/x)\nthread 'main' panicked at src/main.rs:3:5:\n"));
    }

    [Fact]
    public void Hostile_garbage_does_not_throw_and_attributes_nothing()
    {
        var output = CargoTestOutput.Parse(
            "<!DOCTYPE html><html><?xml version=\"1.0\"?><!ENTITY xxe SYSTEM \"file:///etc/passwd\">\0\n" +
            "test not a real result line\n");

        Assert.Equal(0, output.Passed);
        Assert.Equal(0, output.Failed);
        Assert.False(output.HasTestResultLine);
        Assert.Empty(output.ResultsByName);
        Assert.Null(CargoTestOutput.FirstErrorLine("<html><script>alert(1)</script>"));
    }
}
