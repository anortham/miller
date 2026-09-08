using Miller.Testing;
using Miller.Testing.Providers.Shared;
using Miller.Testing.Providers.Jvm;
using Xunit;

namespace Miller.Tests.Testing.Providers;

[Collection("GodotEnvironment")]
[Trait("Category", "Scale")]
public sealed class OtherProviderDirectRecipeScaleTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("miller-direct-native-").FullName;

    [Fact]
    public async Task Maven_recipe_runs_native_class_scope_and_excludes_unrelated_class()
    {
        CtProviderTestSupport.RequireJava();
        CtProviderTestSupport.RequireMaven();
        string project = Path.Combine(_root, "pom.xml");
        File.WriteAllText(project, """
            <project xmlns="http://maven.apache.org/POM/4.0.0">
              <modelVersion>4.0.0</modelVersion>
              <groupId>sample</groupId><artifactId>recipe</artifactId><version>1</version>
              <properties><maven.compiler.release>17</maven.compiler.release></properties>
              <dependencies><dependency>
                <groupId>org.junit.jupiter</groupId><artifactId>junit-jupiter</artifactId>
                <version>5.10.2</version><scope>test</scope>
              </dependency></dependencies>
              <build><plugins>
                <plugin><groupId>org.apache.maven.plugins</groupId><artifactId>maven-compiler-plugin</artifactId><version>3.13.0</version></plugin>
                <plugin><groupId>org.apache.maven.plugins</groupId><artifactId>maven-surefire-plugin</artifactId><version>3.2.5</version></plugin>
              </plugins></build>
            </project>
            """);
        string sources = Path.Combine(_root, "src", "test", "java", "sample");
        Directory.CreateDirectory(sources);
        File.WriteAllText(Path.Combine(sources, "SelectedTest.java"), """
            package sample;
            import org.junit.jupiter.api.Test;
            class SelectedTest {
                @Test void first() {}
                @Test void second() {}
            }
            """);
        File.WriteAllText(Path.Combine(sources, "UnrelatedTest.java"), """
            package sample;
            import org.junit.jupiter.api.Test;
            class UnrelatedTest {
                @Test void fails() { throw new AssertionError("must not run"); }
            }
            """);
        string identity = JvmTestTooling.EncodeCaseId("maven-recipe", project, "maven", "sample.SelectedTest", "first");
        ContinuousTestRunRecipe recipe = ContinuousTestRecipeBuilder.Build(
            new ContinuousTestRunRecipeRequest("maven-recipe", _root, project, "maven", identity));
        Assert.Equal(TestSelectorScope.MatchingTests, recipe.Scope);
        Assert.False(recipe.IsExact);
        TestRunStep step = Assert.Single(recipe.Steps);

        TestProcessResult result = await new TestProcessRunner().RunAsync(
            new TestProcessCommand(step.Executable, step.Arguments, step.WorkingDirectory, step.Environment),
            TestContext.Current.CancellationToken);

        Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
        string report = Assert.Single(Directory.GetFiles(Path.Combine(_root, "target", "surefire-reports"), "TEST-*.xml"));
        Assert.Equal(2, JUnitXmlResultParser.Parse(File.ReadAllText(report)).Cases.Count);
    }

    [Fact]
    public async Task Gut_recipe_prepares_imports_and_runs_only_selected_script()
    {
        CtProviderTestSupport.RequireGodot();
        string addon = Path.Combine(CtProviderTestSupport.RequireGut(), "addons", "gut");
        foreach (string source in Directory.EnumerateFiles(addon, "*", SearchOption.AllDirectories))
        {
            string destination = Path.Combine(_root, "addons", "gut", Path.GetRelativePath(addon, source));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination);
        }
        string project = Path.Combine(_root, "project.godot");
        File.WriteAllText(project, "config_version=5\n[application]\nconfig/name=\"Recipe\"\n[rendering]\nrenderer/rendering_method=\"gl_compatibility\"\n");
        File.WriteAllText(Path.Combine(_root, ".gutconfig.json"), "{\"dirs\":[\"res://tests\"],\"should_exit\":true}");
        Directory.CreateDirectory(Path.Combine(_root, "tests"));
        File.WriteAllText(Path.Combine(_root, "tests", "test_selected.gd"), "extends GutTest\nfunc test_passes():\n\tassert_true(true)\n");
        File.WriteAllText(Path.Combine(_root, "tests", "test_unrelated.gd"), "extends GutTest\nfunc test_fails():\n\tassert_true(false)\n");
        ContinuousTestRunRecipe recipe = ContinuousTestRecipeBuilder.Build(
            new ContinuousTestRunRecipeRequest("gut-recipe", _root, project, "gut", "gut:res://tests/test_selected.gd"));
        Assert.False(Directory.Exists(Path.Combine(_root, ".miller")));
        Assert.True(recipe.IsExact);

        TestProcessResult result = await new TestProcessRunner().RunAsync(
            OperatingSystem.IsWindows()
                ? new TestProcessCommand("powershell", ["-NoProfile", "-Command", recipe.PrimaryCommand], _root)
                : new TestProcessCommand("sh", ["-c", recipe.PrimaryCommand], _root),
            TestContext.Current.CancellationToken);

        Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
        string report = Path.Combine(_root, ".miller", "manual-tests", "gut.xml");
        Assert.Single(JUnitXmlResultParser.Parse(File.ReadAllText(report)).Cases);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
