using Miller.Testing;
using Miller.Testing.Parsing;
using Xunit;

namespace Miller.Tests.Testing.Parsing;

public sealed class CargoMetadataTests
{
    private const string MetadataJson = """
    {
      "workspace_members": [
        "path+file:///repo/crates/adder#0.1.0",
        "path+file:///repo/crates/printer#0.1.0"
      ],
      "packages": [
        {
          "name": "adder",
          "id": "path+file:///repo/crates/adder#0.1.0",
          "manifest_path": "/repo/crates/adder/Cargo.toml",
          "targets": [
            { "name": "adder", "kind": ["lib"], "crate_types": ["lib"], "test": true, "doctest": true },
            { "name": "custom_harness", "kind": ["test"], "crate_types": ["bin"], "test": true, "doctest": false },
            { "name": "integration", "kind": ["test"], "crate_types": ["bin"], "test": true, "doctest": false }
          ]
        },
        {
          "name": "printer",
          "id": "path+file:///repo/crates/printer#0.1.0",
          "manifest_path": "/repo/crates/printer/Cargo.toml",
          "targets": [
            { "name": "printer", "kind": ["bin"], "crate_types": ["bin"], "test": true, "doctest": false }
          ]
        },
        {
          "name": "not-a-member",
          "id": "path+file:///elsewhere/dep#2.0.0",
          "manifest_path": "/elsewhere/dep/Cargo.toml",
          "targets": [
            { "name": "dep", "kind": ["lib"], "crate_types": ["lib"], "test": true, "doctest": true }
          ]
        }
      ]
    }
    """;

    [Fact]
    public void Parse_keeps_only_workspace_members()
    {
        var metadata = CargoMetadata.Parse(MetadataJson);

        Assert.Equal(["adder", "printer"], metadata.WorkspaceMembers.Select(p => p.Name).ToArray());
        Assert.DoesNotContain(metadata.WorkspaceMembers, p => p.Name == "not-a-member");
    }

    [Fact]
    public void Package_exposes_root_dir_and_doctest_flag()
    {
        var metadata = CargoMetadata.Parse(MetadataJson);
        var adder = metadata.WorkspaceMembers.Single(p => p.Name == "adder");

        Assert.Equal("/repo/crates/adder/Cargo.toml", adder.ManifestPath);
        Assert.Equal(Path.GetDirectoryName("/repo/crates/adder/Cargo.toml"), adder.PackageRoot);
        Assert.True(adder.HasDoctests);

        var printer = metadata.WorkspaceMembers.Single(p => p.Name == "printer");
        Assert.False(printer.HasDoctests);
    }

    [Fact]
    public void Test_capable_targets_key_off_the_test_boolean_and_map_selectors()
    {
        var metadata = CargoMetadata.Parse(MetadataJson);
        var adder = metadata.WorkspaceMembers.Single(p => p.Name == "adder");

        var byName = adder.TestCapableTargets.ToDictionary(t => t.Name, StringComparer.Ordinal);
        Assert.Equal(["adder", "custom_harness", "integration"], byName.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());

        Assert.Equal("lib", byName["adder"].SelectorKind);
        Assert.Equal(["--lib"], byName["adder"].SelectorArgs().ToArray());
        Assert.Equal("test", byName["custom_harness"].SelectorKind);
        Assert.Equal(["--test", "custom_harness"], byName["custom_harness"].SelectorArgs().ToArray());

        var printer = metadata.WorkspaceMembers.Single(p => p.Name == "printer");
        var bin = printer.TestCapableTargets.Single();
        Assert.Equal(["--bin", "printer"], bin.SelectorArgs().ToArray());
    }

    [Fact]
    public void Proc_macro_and_lib_variant_kinds_are_still_test_capable_as_lib()
    {
        const string json = """
        {
          "workspace_members": ["path+file:///r/m#0.1.0"],
          "packages": [{
            "name": "m", "id": "path+file:///r/m#0.1.0", "manifest_path": "/r/m/Cargo.toml",
            "targets": [{ "name": "m", "kind": ["proc-macro"], "crate_types": ["proc-macro"], "test": true, "doctest": false }]
          }]
        }
        """;

        var target = CargoMetadata.Parse(json).WorkspaceMembers.Single().TestCapableTargets.Single();
        Assert.Equal("lib", target.SelectorKind);
        Assert.Equal(["--lib"], target.SelectorArgs().ToArray());
    }

    [Fact]
    public void Empty_output_throws_provider_exception()
    {
        Assert.Throws<ContinuousTestProviderException>(() => CargoMetadata.Parse(""));
        Assert.Throws<ContinuousTestProviderException>(() => CargoMetadata.Parse("not json"));
    }

    [Fact]
    public void Parse_rejects_member_package_without_a_name()
    {
        const string json = """
        {
          "workspace_members": ["path+file:///r/m#0.1.0"],
          "packages": [{
            "id": "path+file:///r/m#0.1.0",
            "manifest_path": "/r/m/Cargo.toml"
          }]
        }
        """;

        var ex = Assert.Throws<ContinuousTestProviderException>(() => CargoMetadata.Parse(json));
        Assert.Contains("has no name", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_normalizes_windows_drive_manifest_paths_to_backslashes()
    {
        if (!OperatingSystem.IsWindows())
            return;

        const string json = """
        {
          "workspace_members": ["path+file:///C:/repo/crates/adder#0.1.0"],
          "packages": [{
            "name": "adder",
            "id": "path+file:///C:/repo/crates/adder#0.1.0",
            "manifest_path": "C:/repo/crates/adder/Cargo.toml",
            "targets": [{ "name": "adder", "kind": ["lib"], "test": true, "doctest": false }]
          }]
        }
        """;

        var package = CargoMetadata.Parse(json).WorkspaceMembers.Single();

        Assert.Equal(@"C:\repo\crates\adder\Cargo.toml", package.ManifestPath);
        Assert.Equal(@"C:\repo\crates\adder", package.PackageRoot);
    }
}
