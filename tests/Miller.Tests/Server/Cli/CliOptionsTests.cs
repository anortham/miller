using Miller.Server.Cli;
using Xunit;

namespace Miller.Tests.Server.Cli;

/// <summary>
/// Pins the CLI argument parser <see cref="CliOptions"/>: positional collection, <c>--name value</c> /
/// <c>--name=value</c> value flags, and presence-only boolean flags that must NOT swallow a following positional
/// (the <c>search --json foo</c> trap). Pure — no I/O.
/// </summary>
public sealed class CliOptionsTests
{
    [Fact]
    public void Positionals_AreCollectedInOrder_AndJoinedAsQuery()
    {
        CliOptions o = CliOptions.Parse(new[] { "find", "the", "thing" });
        Assert.Equal(new[] { "find", "the", "thing" }, o.Positionals);
        Assert.Equal("find the thing", o.Query);
    }

    [Fact]
    public void ValueFlag_SpaceForm_IsCaptured()
    {
        CliOptions o = CliOptions.Parse(new[] { "Foo", "--limit", "25", "--mode", "symbol" });
        Assert.Equal("Foo", o.Query);
        Assert.Equal(25, o.Int("limit", 10));
        Assert.Equal("symbol", o.Value("mode"));
    }

    [Fact]
    public void ValueFlag_EqualsForm_IsCaptured()
    {
        CliOptions o = CliOptions.Parse(new[] { "--limit=7", "--mode=file" });
        Assert.Equal(7, o.Int("limit", 10));
        Assert.Equal("file", o.Value("mode"));
    }

    [Fact]
    public void BooleanFlag_DoesNotConsumeTheFollowingPositional()
    {
        // The load-bearing case: --json is a declared boolean, so "Foo" stays the query rather than json's value.
        CliOptions o = CliOptions.Parse(new[] { "--json", "Foo" }, "json");
        Assert.True(o.Has("json"));
        Assert.Equal("Foo", o.Query);
    }

    [Fact]
    public void UndeclaredFlag_BeforeAPositional_ConsumesItAsAValue()
    {
        // Without declaring "mode" boolean, it takes the next token as its value (the normal value-flag rule).
        CliOptions o = CliOptions.Parse(new[] { "--mode", "symbol", "Foo" });
        Assert.Equal("symbol", o.Value("mode"));
        Assert.Equal("Foo", o.Query);
    }

    [Fact]
    public void Int_FallsBackOnMissingOrUnparseable()
    {
        CliOptions o = CliOptions.Parse(new[] { "--limit", "notanumber" });
        Assert.Equal(10, o.Int("limit", 10));     // unparseable → fallback
        Assert.Equal(50, o.Int("depth", 50));     // absent → fallback
    }

    [Fact]
    public void Value_FallsBackWhenAbsentOrValueless()
    {
        CliOptions o = CliOptions.Parse(new[] { "--json" }, "json");
        Assert.True(o.Has("json"));
        Assert.Equal("auto", o.Value("json", "auto"));   // present but valueless → fallback
        Assert.Equal("auto", o.Value("mode", "auto"));   // absent → fallback
    }

    [Fact]
    public void LoneDoubleDash_IsTreatedAsPositional()
    {
        CliOptions o = CliOptions.Parse(new[] { "--", "--weird-query" });
        Assert.Contains("--", o.Positionals);
    }
}
