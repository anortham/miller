using Miller.Testing.Parsing;
using Xunit;

namespace Miller.Tests.Testing.Parsing;

public sealed class RustTestCaseIdTests
{
    [Fact]
    public void Per_test_id_round_trips_through_encode_and_parse()
    {
        var id = RustTestCaseId.ForTest("julie-core", "lib", "julie-core", "index::tests::rebuild_is_idempotent");

        Assert.Equal(
            "rust-test:julie-core::lib/julie-core::index::tests::rebuild_is_idempotent",
            id.Encode());

        Assert.True(RustTestCaseId.TryParse(id.Encode(), out var parsed));
        Assert.Equal("julie-core", parsed.Package);
        Assert.Equal("lib", parsed.Kind);
        Assert.Equal("julie-core", parsed.TargetName);
        Assert.Equal("index::tests::rebuild_is_idempotent", parsed.TestName);
        Assert.True(parsed.IsPerTest);
        Assert.False(parsed.IsWholeTarget);
        Assert.False(parsed.IsDoc);
        Assert.Equal(("julie-core", "lib", "julie-core"), parsed.GroupKey());
    }

    [Fact]
    public void Whole_target_aggregate_id_round_trips_without_a_test_path()
    {
        var id = RustTestCaseId.ForWholeTarget("adder", "test", "custom_harness");

        Assert.Equal("rust-test:adder::test/custom_harness", id.Encode());
        Assert.True(RustTestCaseId.TryParse(id.Encode(), out var parsed));
        Assert.True(parsed.IsWholeTarget);
        Assert.Null(parsed.TestName);
        Assert.Equal(["--test", "custom_harness"], parsed.SelectorArgs().ToArray());
    }

    [Fact]
    public void Doc_aggregate_id_round_trips()
    {
        var id = RustTestCaseId.ForDoc("adder");

        Assert.Equal("rust-test:adder::doc", id.Encode());
        Assert.True(RustTestCaseId.TryParse(id.Encode(), out var parsed));
        Assert.True(parsed.IsDoc);
        Assert.Equal("adder", parsed.Package);
        Assert.Null(parsed.TargetName);
        Assert.Equal(["--doc"], parsed.SelectorArgs().ToArray());
    }

    [Theory]
    [InlineData("lib", "--lib")]
    public void Lib_selector_omits_the_target_name(string kind, string flag)
    {
        var id = RustTestCaseId.ForTest("pkg", kind, "pkg", "t");
        Assert.Equal([flag], id.SelectorArgs().ToArray());
    }

    [Theory]
    [InlineData("bin")]
    [InlineData("test")]
    [InlineData("bench")]
    [InlineData("example")]
    public void Non_lib_selector_carries_the_target_name(string kind)
    {
        var id = RustTestCaseId.ForTest("pkg", kind, "widget", "t");
        Assert.Equal([$"--{kind}", "widget"], id.SelectorArgs().ToArray());
    }

    [Theory]
    [InlineData("rust-test:Cargo.toml")]
    [InlineData("rust-test:tests/api.rs")]
    [InlineData("rust-test:tests/common/mod.rs")]
    [InlineData("dotnet:Some.Test")]
    [InlineData("rust-test:")]
    [InlineData("rust-test:pkg::")]
    [InlineData("rust-test:pkg::badkind/name")]
    [InlineData("rust-test:pkg::lib")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("rust-test:pkg::lib/name::")]
    [InlineData("../rust-test:pkg::lib/name")]
    public void Legacy_and_malformed_ids_do_not_parse(string? id)
    {
        Assert.False(RustTestCaseId.TryParse(id, out _));
    }

    [Fact]
    public void Hyphenated_crate_and_target_names_parse_unambiguously()
    {
        var id = RustTestCaseId.ForTest("my-crate", "test", "integration-suite", "cases::alpha");

        Assert.True(RustTestCaseId.TryParse(id.Encode(), out var parsed));
        Assert.Equal("my-crate", parsed.Package);
        Assert.Equal("integration-suite", parsed.TargetName);
        Assert.Equal("cases::alpha", parsed.TestName);
    }
}
