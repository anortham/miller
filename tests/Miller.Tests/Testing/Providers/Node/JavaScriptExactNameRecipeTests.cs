using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Providers.Node;

public sealed class JavaScriptExactNameRecipeTests
{
    [Theory]
    [InlineData("vitest")]
    [InlineData("jest")]
    [InlineData("node-test")]
    public void Proven_name_without_a_file_is_matching_tests_not_one_case(string framework)
    {
        string root = Path.GetFullPath(Path.GetTempPath());
        ContinuousTestRunRecipe recipe = ContinuousTestRecipeBuilder.Build(new("scope", root,
            Path.Combine(root, "package.json"), framework, "target [x]", Scope: TestSelectorScope.SingleTest, IsExact: true));
        Assert.False(recipe.IsExact);
        Assert.Equal(TestSelectorScope.MatchingTests, recipe.Scope);
        string pattern = Assert.Single(recipe.Steps[0].Arguments, argument => argument.StartsWith('^'));
        Assert.Matches(pattern, "target [x]");
        Assert.DoesNotMatch(pattern, "target x");
        Assert.DoesNotMatch(pattern, "prefix target [x] suffix");
        Assert.NotNull(recipe.UnavailableReason);
    }
}
