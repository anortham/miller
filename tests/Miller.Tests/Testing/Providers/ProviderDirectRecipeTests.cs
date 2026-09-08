using Miller.Testing;
using Miller.Testing.Providers.Php;
using Xunit;

namespace Miller.Tests.Testing.Providers;

public sealed class ProviderDirectRecipeTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("miller-direct-recipes-").FullName;

    [Fact]
    public void Ruby_recipe_preserves_configured_runner_and_treats_colon_in_name_as_literal()
    {
        string project = Path.Combine(_root, "Gemfile");
        File.WriteAllText(project, "source 'https://rubygems.org'\n");
        var request = new ContinuousTestRunRecipeRequest("workspace", _root, project, "rspec",
            "status: [ready]", Scope: TestSelectorScope.SingleTest,
            ConfiguredCommand: "custom-rspec --profile");

        ContinuousTestRunRecipe recipe = ContinuousTestRecipeBuilder.Build(request);

        TestRunStep step = Assert.Single(recipe.Steps);
        Assert.Equal("custom-rspec", step.Executable);
        Assert.Equal(new[] { "--profile", "--example", "status: [ready]" }, step.Arguments);
        Assert.Equal(TestSelectorScope.MatchingTests, recipe.Scope);
        Assert.False(recipe.IsExact);
        Assert.False(Directory.Exists(Path.Combine(_root, ".miller")));
    }

    [Fact]
    public void Php_recipe_escapes_literal_selector_and_uses_project_vendor_runner()
    {
        string project = Path.Combine(_root, "composer.json");
        File.WriteAllText(project, "{}");
        string vendor = Path.Combine(_root, "vendor", "bin");
        Directory.CreateDirectory(vendor);
        string executable = Path.Combine(vendor, "phpunit");
        File.WriteAllText(executable, "");
        var request = new ContinuousTestRunRecipeRequest("workspace", _root, project, "phpunit",
            "test[one]", Scope: TestSelectorScope.SingleTest);

        ContinuousTestRunRecipe recipe = ContinuousTestRecipeBuilder.Build(request);

        TestRunStep step = Assert.Single(recipe.Steps);
        Assert.Equal(executable, step.Executable);
        Assert.Equal(new[] { "--filter", @"test\[one\]" }, step.Arguments);
        Assert.False(recipe.IsExact);
        Assert.Equal(TestSelectorScope.MatchingTests, recipe.Scope);
        Assert.False(Directory.Exists(Path.Combine(_root, ".miller")));
    }

    [Fact]
    public void Php_recipe_preserves_explicit_custom_command_without_requiring_vendor_install()
    {
        string project = Path.Combine(_root, "composer.json");
        File.WriteAllText(project, "{}");

        ContinuousTestRunRecipe recipe = ContinuousTestRecipeBuilder.Build(
            new ContinuousTestRunRecipeRequest("workspace", _root, project, "phpunit", "testAdd",
                ConfiguredCommand: "custom-phpunit --colors=never"));

        TestRunStep step = Assert.Single(recipe.Steps);
        Assert.Equal("custom-phpunit", step.Executable);
        Assert.Equal(new[] { "--colors=never", "--filter", "testAdd" }, step.Arguments);
        Assert.False(Directory.Exists(Path.Combine(_root, ".miller")));
    }

    [Fact]
    public void Php_recipe_reports_missing_vendor_runner_without_inventing_global_fallback()
    {
        string project = Path.Combine(_root, "composer.json");
        File.WriteAllText(project, "{}");

        ContinuousTestRunRecipe recipe = ContinuousTestRecipeBuilder.Build(
            new ContinuousTestRunRecipeRequest("workspace", _root, project, "phpunit"));

        Assert.Empty(recipe.Steps);
        Assert.Contains("composer install", recipe.UnavailableReason, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_root, ".miller")));
    }

    [Theory]
    [InlineData("rspec", true)]
    [InlineData("rspec", false)]
    [InlineData("phpunit", true)]
    [InlineData("phpunit", false)]
    public void Native_identity_from_another_workspace_or_project_is_refused(string framework, bool otherWorkspace)
    {
        string project = Path.Combine(_root, framework == "rspec" ? "Gemfile" : "composer.json");
        string identityWorkspace = otherWorkspace ? "foreign" : "workspace";
        string identityProject = otherWorkspace ? project : Path.Combine(_root, "other", Path.GetFileName(project));
        string selector = framework == "rspec"
            ? RubyTestTooling.EncodeCaseId(identityWorkspace, identityProject, "spec/test_spec.rb", "./spec/test_spec.rb[1:1]")
            : PhpTestTooling.EncodeCaseId(identityWorkspace, identityProject, "Example", "testOne");
        var recipe = ContinuousTestRecipeBuilder.Build(new ContinuousTestRunRecipeRequest(
            "workspace", _root, project, framework, selector, IsExact: true, ConfiguredCommand: "custom-runner"));
        Assert.Empty(recipe.Steps);
        Assert.False(recipe.IsExact);
        Assert.Contains("different workspace or project", recipe.UnavailableReason);
    }

    [Theory]
    [InlineData("MINITEST", "minitest")]
    [InlineData(" RSpec ", "rspec")]
    [InlineData("PEST", "pest")]
    public void Framework_dispatch_passes_the_normalized_framework_to_the_provider(string framework, string expected)
    {
        var recipe = ContinuousTestRecipeBuilder.Build(new ContinuousTestRunRecipeRequest(
            "workspace", _root, Path.Combine(_root, "project"), framework, ConfiguredCommand: "custom-runner"));
        Assert.Equal(expected, recipe.Framework);
    }

    [Fact]
    public void Minitest_empty_quoted_command_reports_unavailable()
    {
        var recipe = ContinuousTestRecipeBuilder.Build(new ContinuousTestRunRecipeRequest(
            "workspace", _root, Path.Combine(_root, "Gemfile"), "minitest", ConfiguredCommand: "''"));
        Assert.Empty(recipe.Steps);
        Assert.Contains("executable", recipe.UnavailableReason);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
