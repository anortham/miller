using Miller.Testing.Parsing;
using Xunit;

namespace Miller.Tests.Testing.Parsing;

public sealed class CargoTestListTests
{
    [Fact]
    public void Parses_lib_unit_test_paths()
    {
        const string stdout = "tests::add_works: test\ntests::add_zero: test\ntests::slow_case: test\n";

        Assert.Equal(
            ["tests::add_works", "tests::add_zero", "tests::slow_case"],
            CargoTestList.ParseTestNames(stdout).ToArray());
    }

    [Fact]
    public void Parses_integration_target_test_names()
    {
        Assert.Equal(["integration_add"], CargoTestList.ParseTestNames("integration_add: test\n").ToArray());
    }

    [Fact]
    public void Harness_false_custom_main_output_yields_zero_names()
    {
        Assert.Empty(CargoTestList.ParseTestNames("custom harness ok\n"));
    }

    [Fact]
    public void Empty_or_null_output_yields_zero_names()
    {
        Assert.Empty(CargoTestList.ParseTestNames(""));
        Assert.Empty(CargoTestList.ParseTestNames(null));
    }

    [Fact]
    public void Benchmark_entries_are_enumerated()
    {
        Assert.Equal(["bench_add"], CargoTestList.ParseTestNames("bench_add: benchmark\n").ToArray());
    }

    [Fact]
    public void Hostile_noise_and_malformed_lines_are_ignored()
    {
        const string stdout =
            "<!DOCTYPE html>\n" +
            "running 1 test\n" +
            "tests::add_works: test\n" +
            "not a list line\n" +
            "tests::add_works: TEST\n";

        Assert.Equal(["tests::add_works"], CargoTestList.ParseTestNames(stdout).ToArray());
    }
}
