using Miller.Testing;
using Miller.Testing.Parsing;
using Xunit;

namespace Miller.Tests.Testing.Parsing;

public sealed class RustCoverageFlagPolicyTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("miller-ct-rust-flags-").FullName;

    [Fact]
    public void Encoded_flags_win_and_are_preserved_when_instrumentation_is_appended()
    {
        const string existing = "-C\u001ftarget-cpu=native";
        var environment = EnvironmentWithCargoHome(
            ("CARGO_ENCODED_RUSTFLAGS", existing),
            ("RUSTFLAGS", "--cfg ignored_by_cargo_precedence"));

        var overlay = RustCoverageFlagPolicy.Create(_dir, environment);

        Assert.Equal(existing + "\u001f-C\u001finstrument-coverage", overlay["CARGO_ENCODED_RUSTFLAGS"]);
        Assert.DoesNotContain("RUSTFLAGS", overlay.Keys);
    }

    [Fact]
    public void Encoded_trailing_separator_is_preserved_without_adding_an_empty_argument()
    {
        const string existing = "-C\u001ftarget-cpu=native\u001f";

        var overlay = RustCoverageFlagPolicy.Create(
            _dir,
            EnvironmentWithCargoHome(("CARGO_ENCODED_RUSTFLAGS", existing)));

        Assert.Equal(existing + "-C\u001finstrument-coverage", overlay["CARGO_ENCODED_RUSTFLAGS"]);
    }

    [Fact]
    public void Plain_flags_are_preserved_when_instrumentation_is_appended()
    {
        const string existing = "--cfg custom -C target-cpu=native  ";
        var environment = EnvironmentWithCargoHome(("RUSTFLAGS", existing));

        var overlay = RustCoverageFlagPolicy.Create(_dir, environment);

        Assert.Equal(existing + "-C instrument-coverage", overlay["RUSTFLAGS"]);
        Assert.DoesNotContain("CARGO_ENCODED_RUSTFLAGS", overlay.Keys);
    }

    [Fact]
    public void Missing_flags_use_plain_instrumentation()
    {
        var overlay = RustCoverageFlagPolicy.Create(_dir, EnvironmentWithCargoHome());

        Assert.Equal("-C instrument-coverage", overlay["RUSTFLAGS"]);
    }

    [Theory]
    [InlineData("CARGO_ENCODED_RUSTFLAGS", "-C\u001finstrument-coverage")]
    [InlineData("RUSTFLAGS", "--cfg custom -C instrument-coverage")]
    public void Existing_instrumentation_is_not_appended_again(string variable, string existing)
    {
        var overlay = RustCoverageFlagPolicy.Create(
            _dir,
            EnvironmentWithCargoHome((variable, existing)));

        Assert.Equal(existing, overlay[variable]);
    }

    [Theory]
    [InlineData(
        "CARGO_ENCODED_RUSTFLAGS",
        "--cfg\u001finstrument-coverage-disabled",
        "--cfg\u001finstrument-coverage-disabled\u001f-C\u001finstrument-coverage")]
    [InlineData(
        "RUSTFLAGS",
        "--cfg instrument-coverage-disabled",
        "--cfg instrument-coverage-disabled -C instrument-coverage")]
    public void Similar_flag_values_do_not_hide_missing_instrumentation(
        string variable,
        string existing,
        string expected)
    {
        var overlay = RustCoverageFlagPolicy.Create(
            _dir,
            EnvironmentWithCargoHome((variable, existing)));

        Assert.Equal(expected, overlay[variable]);
    }

    [Fact]
    public void Target_specific_environment_flags_fail_closed()
    {
        var environment = EnvironmentWithCargoHome(
            ("CARGO_TARGET_X86_64_UNKNOWN_LINUX_GNU_RUSTFLAGS", "-C target-feature=+crt-static"));

        var exception = Assert.Throws<ContinuousTestProviderException>(
            () => RustCoverageFlagPolicy.Create(_dir, environment));

        Assert.Contains("CARGO_TARGET_X86_64_UNKNOWN_LINUX_GNU_RUSTFLAGS", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Windows_encoded_flags_win_with_case_insensitive_environment_names()
    {
        const string existing = "-C\u001ftarget-cpu=native";
        var environment = EnvironmentWithCargoHome(
            ("cargo_encoded_rustflags", existing),
            ("RUSTFLAGS", "--cfg ignored_by_cargo_precedence"));

        var overlay = RustCoverageFlagPolicy.Create(_dir, environment, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(existing + "\u001f-C\u001finstrument-coverage", overlay["CARGO_ENCODED_RUSTFLAGS"]);
        Assert.DoesNotContain("RUSTFLAGS", overlay.Keys);
    }

    [Fact]
    public void Windows_plain_flags_are_found_with_case_insensitive_environment_names()
    {
        const string existing = "--cfg custom";
        var environment = EnvironmentWithCargoHome(("RustFlags", existing));

        var overlay = RustCoverageFlagPolicy.Create(
            _dir,
            environment,
            StringComparison.OrdinalIgnoreCase);

        Assert.Equal(existing + " -C instrument-coverage", overlay["RUSTFLAGS"]);
    }

    [Fact]
    public void Windows_target_flags_fail_closed_with_case_insensitive_environment_names()
    {
        const string variable = "Cargo_Target_X86_64_Pc_Windows_Msvc_RustFlags";
        var environment = EnvironmentWithCargoHome((variable, "-C target-feature=+crt-static"));

        var exception = Assert.Throws<ContinuousTestProviderException>(
            () => RustCoverageFlagPolicy.Create(
                _dir,
                environment,
                StringComparison.OrdinalIgnoreCase));

        Assert.Contains(variable, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unix_environment_names_remain_case_sensitive()
    {
        var environment = EnvironmentWithCargoHome(("rustflags", "--cfg lowercase"));

        var overlay = RustCoverageFlagPolicy.Create(
            _dir,
            environment,
            StringComparison.Ordinal);

        Assert.Equal("-C instrument-coverage", overlay["RUSTFLAGS"]);
    }

    [Theory]
    [InlineData("config.toml", "[build]\nrustflags = ['--cfg', 'custom']")]
    [InlineData("config", "target.'cfg(unix)'.rustflags = '-C target-cpu=native'")]
    public void Ancestor_cargo_config_flags_fail_closed(string fileName, string contents)
    {
        var repository = Path.Combine(_dir, "repository");
        var project = Path.Combine(repository, "crates", "crate-a");
        var configDirectory = Directory.CreateDirectory(Path.Combine(repository, ".cargo")).FullName;
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(configDirectory, fileName), contents);

        var exception = Assert.Throws<ContinuousTestProviderException>(
            () => RustCoverageFlagPolicy.Create(project, EnvironmentWithCargoHome()));

        Assert.Contains(fileName, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Inline_table_rustflags_fail_closed()
    {
        var cargoDirectory = Directory.CreateDirectory(Path.Combine(_dir, ".cargo")).FullName;
        var configPath = Path.Combine(cargoDirectory, "config.toml");
        File.WriteAllText(configPath, "build = { rustflags = ['--cfg', 'custom'] }");

        var exception = Assert.Throws<ContinuousTestProviderException>(
            () => RustCoverageFlagPolicy.Create(_dir, EnvironmentWithCargoHome()));

        Assert.Contains(configPath, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Full_line_comment_with_rustflags_is_ignored()
    {
        var cargoDirectory = Directory.CreateDirectory(Path.Combine(_dir, ".cargo")).FullName;
        File.WriteAllText(
            Path.Combine(cargoDirectory, "config.toml"),
            "# build = { rustflags = ['--cfg', 'disabled'] }");

        var overlay = RustCoverageFlagPolicy.Create(_dir, EnvironmentWithCargoHome());

        Assert.Equal("-C instrument-coverage", overlay["RUSTFLAGS"]);
    }

    [Fact]
    public void Cargo_home_config_flags_fail_closed()
    {
        var cargoHome = Directory.CreateDirectory(Path.Combine(_dir, "custom-cargo-home")).FullName;
        var configPath = Path.Combine(cargoHome, "config.toml");
        File.WriteAllText(configPath, "[target.'cfg(windows)']\nrustflags = ['-C', 'target-feature=+crt-static']");
        var environment = EnvironmentWithCargoHome(("CARGO_HOME", cargoHome));

        var exception = Assert.Throws<ContinuousTestProviderException>(
            () => RustCoverageFlagPolicy.Create(Path.Combine(_dir, "project"), environment));

        Assert.Contains(configPath, exception.Message, StringComparison.Ordinal);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private Dictionary<string, string?> EnvironmentWithCargoHome(
        params (string Key, string? Value)[] entries)
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["CARGO_HOME"] = Path.Combine(_dir, "cargo-home"),
        };
        foreach (var (key, value) in entries)
            environment[key] = value;
        return environment;
    }
}
