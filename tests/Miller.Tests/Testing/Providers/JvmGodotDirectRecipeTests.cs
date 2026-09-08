using System.Text.Json;
using Miller.Testing;
using Miller.Testing.Providers.Jvm;
using Xunit;

namespace Miller.Tests.Testing.Providers;

public sealed class JvmGodotDirectRecipeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "miller-jvm-godot-recipe-" + Guid.NewGuid().ToString("N"));

    public JvmGodotDirectRecipeTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("maven", "pom.xml")]
    [InlineData("sbt", "build.sbt")]
    public void Jvm_provider_class_identity_is_not_misrepresented_as_one_method(string backend, string file)
    {
        string project = Path.Combine(_root, file);
        File.WriteAllText(project, string.Empty);
        string id = JvmTestTooling.EncodeCaseId("ws", project, backend, "example.SampleTest", JvmTestBackendIds.ClassCaseSentinel);
        ContinuousTestRunRecipe recipe = ContinuousTestRecipeBuilder.Build(new("ws", _root, project,
            backend, id, Scope: TestSelectorScope.SingleTest, IsExact: true));
        Assert.False(recipe.IsExact);
        Assert.Equal(TestSelectorScope.MatchingTests, recipe.Scope);
        Assert.Contains("example.SampleTest", recipe.PrimaryCommand, StringComparison.Ordinal);
        Assert.DoesNotContain(id, recipe.PrimaryCommand, StringComparison.Ordinal);
        Assert.NotNull(recipe.UnavailableReason);
    }

    [Fact]
    public void Gradle_recipe_respects_configured_command_and_provider_method_identity_without_writes()
    {
        string project = Path.Combine(_root, "build.gradle");
        File.WriteAllText(project, string.Empty);
        string id = JvmTestTooling.EncodeCaseId("ws", project, "gradle", "example.SampleTest", "works");
        ContinuousTestRunRecipe recipe = ContinuousTestRecipeBuilder.Build(new("ws", _root, project,
            "gradle", id, Scope: TestSelectorScope.SingleTest, IsExact: true,
            ConfiguredCommand: "custom-gradle --offline"));
        TestRunStep step = Assert.Single(recipe.Steps);
        Assert.Equal("custom-gradle", step.Executable);
        Assert.Contains("--offline", step.Arguments);
        Assert.Contains("example.SampleTest.works", step.Arguments);
        Assert.True(recipe.IsExact);
        Assert.Equal(TestSelectorScope.SingleTest, recipe.Scope);
        Assert.False(Directory.Exists(Path.Combine(_root, ".miller")));
    }

    [Fact]
    public void Maven_recipe_reuses_workspace_wrapper_for_a_nested_project()
    {
        string wrapper = Path.Combine(_root, OperatingSystem.IsWindows() ? "mvnw.cmd" : "mvnw");
        File.WriteAllText(wrapper, string.Empty);
        string nested = Path.Combine(_root, "service");
        Directory.CreateDirectory(nested);
        string project = Path.Combine(nested, "pom.xml");
        File.WriteAllText(project, string.Empty);
        ContinuousTestRunRecipe recipe = ContinuousTestRecipeBuilder.Build(new("ws", _root, project, "maven"));
        TestRunStep step = Assert.Single(recipe.Steps);
        Assert.Equal(wrapper, step.Executable);
        Assert.Equal(_root, step.WorkingDirectory);
        Assert.Contains(project, step.Arguments);
    }

    [Theory]
    [InlineData("maven", "pom.xml")]
    [InlineData("gradle", "build.gradle")]
    [InlineData("sbt", "build.sbt")]
    [InlineData("gut", "project.godot")]
    public void Unsupported_trait_exclusions_are_refused_instead_of_ignored(string framework, string file)
    {
        string project = Path.Combine(_root, file);
        File.WriteAllText(project, string.Empty);
        ContinuousTestRunRecipe recipe = ContinuousTestRecipeBuilder.Build(new("ws", _root, project,
            framework, ExcludeTraits: ["Category=Slow"]));
        Assert.Empty(recipe.Steps);
        Assert.Contains("exclusion", recipe.UnavailableReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Gut_recipe_imports_before_running_and_preserves_config_while_selecting_exact_script()
    {
        string project = Path.Combine(_root, "project.godot");
        File.WriteAllText(project, string.Empty);
        Directory.CreateDirectory(Path.Combine(_root, "specs"));
        File.WriteAllText(Path.Combine(_root, "specs", "check_one.gd"), "extends GutTest\n");
        File.WriteAllText(Path.Combine(_root, ".gutconfig.json"), "{\"dirs\":[\"res://specs\"],\"prefix\":\"check_\",\"double_strategy\":\"partial\"}");
        ContinuousTestRunRecipe recipe = ContinuousTestRecipeBuilder.Build(new("ws", _root, project,
            "gut", "gut:res://specs/check_one.gd", Scope: TestSelectorScope.SingleTest, IsExact: true,
            ConfiguredCommand: "custom-godot --verbose"));
        Assert.Contains(recipe.Steps, step => step.Arguments.Contains("--import"));
        TestRunStep run = recipe.Steps.Last();
        Assert.Equal("custom-godot", run.Executable);
        Assert.Contains("--verbose", run.Arguments);
        Assert.Contains("-gexit", run.Arguments);
        Assert.Contains(run.Arguments, arg => arg.StartsWith("-gconfig=", StringComparison.Ordinal));
        Assert.DoesNotContain(run.Arguments, arg => arg.StartsWith("-gunit_test_name=", StringComparison.Ordinal));
        Assert.Equal(TestSelectorScope.TestFile, recipe.Scope);
        Assert.True(recipe.IsExact);
        string allArguments = string.Join('\n', recipe.Steps.SelectMany(step => step.Arguments));
        Assert.Contains("res://specs/check_one.gd", allArguments, StringComparison.Ordinal);
        Assert.Contains("double_strategy", allArguments, StringComparison.Ordinal);
        Assert.Contains("partial", allArguments, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_root, ".miller")));
        using var original = JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, ".gutconfig.json")));
        Assert.True(original.RootElement.TryGetProperty("dirs", out _));
    }

    [Theory]
    [InlineData("gdunit4")]
    [InlineData("gut-unsupported")]
    public void Unsupported_godot_frameworks_do_not_receive_a_fake_runner(string framework)
    {
        ContinuousTestRunRecipe recipe = ContinuousTestRecipeBuilder.Build(new("ws", _root,
            Path.Combine(_root, "project.godot"), framework));
        Assert.Empty(recipe.Steps);
        Assert.False(recipe.IsExact);
        Assert.NotNull(recipe.UnavailableReason);
    }

    [Fact]
    public void Jvm_provider_identity_from_another_workspace_is_refused()
    {
        string project = Path.Combine(_root, "build.gradle");
        string id = JvmTestTooling.EncodeCaseId("other", project, "gradle", "example.SampleTest", "works");
        ContinuousTestRunRecipe recipe = ContinuousTestRecipeBuilder.Build(new("ws", _root, project, "gradle", id));
        Assert.Empty(recipe.Steps);
        Assert.Contains("workspace", recipe.UnavailableReason, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
