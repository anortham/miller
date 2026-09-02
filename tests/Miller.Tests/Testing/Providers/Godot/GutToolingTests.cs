using Miller.Testing;
using Miller.Testing.Providers.Godot;
using System.Text.Json;
using Xunit;

namespace Miller.Tests.Testing.Providers.Godot;

[Collection("GodotEnvironment")]
public sealed class GutToolingTests
{
    [Fact]
    public void Version_command_uses_the_mirror_and_isolated_environment()
    {
        string root = Directory.CreateTempSubdirectory("miller-ct-gut-tooling-").FullName;
        try
        {
            var shadow = new GodotProjectShadowResult(
                ProjectCandidateRoot: Path.Combine(root, "workspace"),
                GodotHomeRoot: Path.Combine(root, "home"),
                ProjectMirrorRoot: Path.Combine(root, "workspace", "project"),
                SourceRoot: root,
                MirrorProjectPath: Path.Combine(root, "workspace", "project", "project.godot"),
                ImportStampPath: Path.Combine(root, "workspace", "import.stamp.json"),
                OverBudgetMarkerPath: Path.Combine(root, "workspace", "over-budget.json"),
                ProjectActivityMarkerPath: Path.Combine(root, "workspace", ".last-used"),
                HomeActivityMarkerPath: Path.Combine(root, "home", ".last-used"),
                SourceMetadataDigest: "digest",
                EntriesScanned: 0,
                EntriesCopied: 0,
                EntriesUpdated: 0,
                EntriesDeleted: 0,
                BytesCopied: 0,
                FilesHashed: 0,
                BytesHashed: 0,
                ProjectCandidateBytes: 0,
                GodotHomeCandidateBytes: 0,
                Elapsed: TimeSpan.Zero,
                SourceOwnedStateChanged: false);

            TestProcessCommand command = GutTooling.BuildVersionCommand("/opt/godot", shadow);

            Assert.Equal("/opt/godot", command.FileName);
            Assert.Equal(["--version"], command.Arguments);
            Assert.Equal(shadow.ProjectMirrorRoot, command.WorkingDirectory);
            Assert.Equal(Path.Combine(shadow.GodotHomeRoot, "home"), command.Environment["HOME"]);
            Assert.Equal(Path.Combine(shadow.GodotHomeRoot, "cache"), command.Environment["XDG_CACHE_HOME"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Configuration_defaults_allow_gut_sample_trailing_commas()
    {
        GutConfiguration configuration = GutConfiguration.Parse("""
            {
              "unknown": {"keep": true},
              "tests": ["res://tests/test_math.gd",],
            }
            """);

        Assert.Empty(configuration.Dirs);
        Assert.Equal(["res://tests/test_math.gd"], configuration.Tests);
        Assert.False(configuration.IncludeSubdirs);
        Assert.Equal("test_", configuration.Prefix);
        Assert.Equal(".gd", configuration.Suffix);
    }

    [Theory]
    [InlineData("{\"dirs\":\"tests\"}")]
    [InlineData("{\"tests\":42}")]
    [InlineData("{\"include_subdirs\":\"true\"}")]
    [InlineData("{\"prefix\":42}")]
    [InlineData("{\"suffix\":[]}")]
    public void Configuration_rejects_malformed_field_types(string json)
    {
        Assert.Throws<ContinuousTestProviderException>(() => GutConfiguration.Parse(json));
    }

    [Fact]
    public void Configuration_discovers_explicit_and_recursive_directory_scripts_without_duplicates()
    {
        string root = Directory.CreateTempSubdirectory("miller-ct-gut-config-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "tests", "nested"));
            File.WriteAllText(Path.Combine(root, "tests", "test_one.gd"), "one");
            File.WriteAllText(Path.Combine(root, "tests", "nested", "test_two.gd"), "two");
            File.WriteAllText(Path.Combine(root, "tests", "helper.gd"), "helper");

            GutConfiguration configuration = GutConfiguration.Parse("""
                {
                  "dirs": ["tests"],
                  "tests": ["res://tests/test_one.gd"],
                  "include_subdirs": true,
                  "prefix": "test_",
                  "suffix": ".gd"
                }
                """);

            IReadOnlyList<GutScript> scripts = configuration.DiscoverScripts(root);

            Assert.Equal(["res://tests/nested/test_two.gd", "res://tests/test_one.gd"],
                scripts.Select(script => script.ResPath).ToArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Configuration_rejects_missing_or_escaping_paths()
    {
        GutConfiguration configuration = GutConfiguration.Parse("""
            {"dirs":["../outside"],"tests":[]}
            """);

        string root = Directory.CreateTempSubdirectory("miller-ct-gut-config-").FullName;
        try
        {
            ContinuousTestProviderException exception = Assert.Throws<ContinuousTestProviderException>(() =>
            {
                configuration.DiscoverScripts(root);
            });
            Assert.Contains("escapes", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Configuration_rejects_case_collisions_in_discovered_scripts()
    {
        string root = Directory.CreateTempSubdirectory("miller-ct-gut-config-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "tests"));
            File.WriteAllText(Path.Combine(root, "tests", "test_one.gd"), "one");
            File.WriteAllText(Path.Combine(root, "tests", "Test_one.gd"), "two");
            GutConfiguration configuration = GutConfiguration.Parse("{\"dirs\":[\"tests\"],\"prefix\":\"\"}");

            ContinuousTestProviderException exception = Assert.Throws<ContinuousTestProviderException>(() =>
            {
                configuration.DiscoverScripts(root);
            });

            Assert.Contains("case collision", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Derived_configuration_preserves_unknown_values_and_replaces_runner_owned_values()
    {
        GutConfiguration configuration = GutConfiguration.Parse("""
            {
              "log_level": 2,
              "should_exit": false,
              "exit_on_success": true,
              "disable_colors": false,
              "junit_xml_file": "res://user.xml",
              "junit_xml_timestamp": true,
              "selected": "partial",
              "unit_test_name": "test_one",
              "inner_class": "Inner",
              "dirs": ["tests"],
              "tests": [],
              "include_subdirs": true
            }
            """);

        string json = configuration.SerializeDerived(
            ["res://tests/test_one.gd", "res://tests/test_two.gd"],
            "res://.miller-gut-results/run.xml");
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        Assert.Equal(2, root.GetProperty("log_level").GetInt32());
        Assert.Empty(root.GetProperty("dirs").EnumerateArray());
        Assert.Equal(
            ["res://tests/test_one.gd", "res://tests/test_two.gd"],
            root.GetProperty("tests").EnumerateArray().Select(item => item.GetString()!).ToArray());
        Assert.False(root.GetProperty("include_subdirs").GetBoolean());
        Assert.True(root.GetProperty("should_exit").GetBoolean());
        Assert.False(root.GetProperty("exit_on_success").GetBoolean());
        Assert.True(root.GetProperty("disable_colors").GetBoolean());
        Assert.Equal("res://.miller-gut-results/run.xml", root.GetProperty("junit_xml_file").GetString());
        Assert.False(root.GetProperty("junit_xml_timestamp").GetBoolean());
        Assert.Equal(string.Empty, root.GetProperty("selected").GetString());
        Assert.Equal(string.Empty, root.GetProperty("unit_test_name").GetString());
        Assert.Equal(string.Empty, root.GetProperty("inner_class").GetString());
    }

    [Fact]
    public void Import_and_run_commands_use_only_contained_mirror_paths()
    {
        string root = Directory.CreateTempSubdirectory("miller-ct-gut-tooling-").FullName;
        try
        {
            GodotProjectShadowResult shadow = Shadow(root);
            TestProcessCommand import = GutTooling.BuildImportCommand("godot", shadow);
            TestProcessCommand run = GutTooling.BuildRunCommand(
                "godot",
                shadow,
                "res://.miller-gut-results/miller.gutconfig.json",
                "res://.miller-gut-results/run.xml");

            Assert.Equal(["--headless", "--path", shadow.ProjectMirrorRoot, "--import"], import.Arguments);
            Assert.Equal(shadow.ProjectMirrorRoot, import.WorkingDirectory);
            Assert.Contains("-s", run.Arguments);
            Assert.Contains("-gexit", run.Arguments);
            Assert.Contains("-gdisable_colors", run.Arguments);
            Assert.Contains("-gconfig=res://.miller-gut-results/miller.gutconfig.json", run.Arguments);
            Assert.Contains("-gjunit_xml_file=res://.miller-gut-results/run.xml", run.Arguments);
            Assert.DoesNotContain(run.Arguments, argument => argument.Contains("test_math.gd", StringComparison.Ordinal));
            Assert.Equal(shadow.ProjectMirrorRoot, run.WorkingDirectory);
            Assert.DoesNotContain("PATH", run.Environment.Keys);
            Assert.All(run.Environment.Values, value =>
            {
                Assert.NotNull(value);
                Assert.True(Path.IsPathRooted(value!));
                Assert.StartsWith(Path.GetFullPath(shadow.GodotHomeRoot) + Path.DirectorySeparatorChar,
                    Path.GetFullPath(value!), StringComparison.OrdinalIgnoreCase);
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Godot_resolution_prefers_the_non_empty_GODOT_value()
    {
        string root = Directory.CreateTempSubdirectory("miller-ct-gut-tooling-").FullName;
        string? previousGodot = Environment.GetEnvironmentVariable("GODOT");
        string? previousPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            string configured = Path.Combine(root, "configured-godot");
            string pathGodot = Path.Combine(root, "path-godot");
            File.WriteAllText(configured, string.Empty);
            File.WriteAllText(pathGodot, string.Empty);
            Environment.SetEnvironmentVariable("GODOT", configured);
            Environment.SetEnvironmentVariable("PATH", root);

            Assert.Equal(Path.GetFullPath(configured), GutTooling.ResolveGodotExecutable());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GODOT", previousGodot);
            Environment.SetEnvironmentVariable("PATH", previousPath);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Godot_resolution_uses_deterministic_name_order_across_PATH_entries()
    {
        string root = Directory.CreateTempSubdirectory("miller-ct-gut-tooling-").FullName;
        string? previousGodot = Environment.GetEnvironmentVariable("GODOT");
        string? previousPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            string first = Path.Combine(root, "first");
            string second = Path.Combine(root, "second");
            Directory.CreateDirectory(first);
            Directory.CreateDirectory(second);
            string godot4 = Path.Combine(first, "godot4");
            string godot = Path.Combine(second, "godot");
            File.WriteAllText(godot4, string.Empty);
            File.WriteAllText(godot, string.Empty);
            Environment.SetEnvironmentVariable("GODOT", null);
            Environment.SetEnvironmentVariable("PATH", first + Path.PathSeparator + second);

            Assert.Equal(Path.GetFullPath(godot), GutTooling.ResolveGodotExecutable());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GODOT", previousGodot);
            Environment.SetEnvironmentVariable("PATH", previousPath);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Godot_resolution_falls_back_to_godot4_when_generic_name_is_missing()
    {
        string root = Directory.CreateTempSubdirectory("miller-ct-gut-tooling-").FullName;
        string? previousGodot = Environment.GetEnvironmentVariable("GODOT");
        string? previousPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            string fallback = Path.Combine(root, "godot4");
            File.WriteAllText(fallback, string.Empty);
            Environment.SetEnvironmentVariable("GODOT", null);
            Environment.SetEnvironmentVariable("PATH", root);

            Assert.Equal(Path.GetFullPath(fallback), GutTooling.ResolveGodotExecutable());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GODOT", previousGodot);
            Environment.SetEnvironmentVariable("PATH", previousPath);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Godot_version_and_GUT_plugin_versions_are_parsed_and_floor_is_enforced_by_callers()
    {
        Assert.Equal(4, GutTooling.ParseGodotMajor("Godot Engine v4.7.2.stable.official"));
        Assert.Equal(4, GutTooling.ParseGodotMajor("4.7.2.stable.official.ed1daf0bf"));
        Assert.Equal(4, GutTooling.ParseGodotMajor("helper 3.0\nGodot Engine v4.7.2.stable.official"));
        Assert.Equal(3, GutTooling.ParseGodotMajor("Godot Engine v3.5.2.stable"));
        Assert.Throws<ContinuousTestProviderException>(() => GutTooling.ParseGodotMajor("Godot Engine v4"));
        Assert.Throws<ContinuousTestProviderException>(() => GutTooling.ParseGodotMajor("4.7.2.stable.official."));
        string root = Directory.CreateTempSubdirectory("miller-ct-gut-tooling-").FullName;
        try
        {
            string plugin = Path.Combine(root, "plugin.cfg");
            File.WriteAllText(plugin, "[plugin]\nversion=\"9.7.1\"\n");
            Assert.Equal(9, GutTooling.ReadGutMajor(plugin));
            Assert.Throws<ContinuousTestProviderException>(() =>
                GutTooling.ParseGodotMajor(new TestProcessResult(0, "Godot Engine v4.7.2", string.Empty, true)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static GodotProjectShadowResult Shadow(string root) => new(
        ProjectCandidateRoot: Path.Combine(root, "workspace"),
        GodotHomeRoot: Path.Combine(root, "home"),
        ProjectMirrorRoot: Path.Combine(root, "workspace", "project"),
        SourceRoot: root,
        MirrorProjectPath: Path.Combine(root, "workspace", "project", "project.godot"),
        ImportStampPath: Path.Combine(root, "workspace", "import.stamp.json"),
        OverBudgetMarkerPath: Path.Combine(root, "workspace", "over-budget.json"),
        ProjectActivityMarkerPath: Path.Combine(root, "workspace", ".last-used"),
        HomeActivityMarkerPath: Path.Combine(root, "home", ".last-used"),
        SourceMetadataDigest: "digest",
        EntriesScanned: 0,
        EntriesCopied: 0,
        EntriesUpdated: 0,
        EntriesDeleted: 0,
        BytesCopied: 0,
        FilesHashed: 0,
        BytesHashed: 0,
        ProjectCandidateBytes: 0,
        GodotHomeCandidateBytes: 0,
        Elapsed: TimeSpan.Zero,
        SourceOwnedStateChanged: false);
}

[CollectionDefinition("GodotEnvironment", DisableParallelization = true)]
public sealed class GodotEnvironmentCollection
{
}
