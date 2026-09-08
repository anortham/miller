using Miller.Testing;
using Miller.Testing.Providers.Jvm;
using Miller.Testing.Providers.Shared;
using Xunit;

namespace Miller.Tests.Testing.Providers;

[Trait("Category", "Scale")]
public sealed class JvmDirectRecipeNativeScaleTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("miller-jvm-direct-native-").FullName;

    [Theory]
    [InlineData("gradle", 1)]
    [InlineData("sbt", 2)]
    public async Task Generated_recipe_executes_declared_scope_and_excludes_unrelated_failures(string backend, int count)
    {
        CtProviderTestSupport.RequireJava();
        if (backend == "gradle") CtProviderTestSupport.RequireGradle();
        else CtProviderTestSupport.RequireSbt();
        string project = Path.Combine(_root, backend == "gradle" ? "build.gradle" : "build.sbt");
        if (backend == "gradle")
        {
            File.WriteAllText(project, "plugins { id 'java' }\nrepositories { mavenCentral() }\ndependencies { testImplementation 'junit:junit:4.13.2' }\ntest { useJUnit() }\n");
            File.WriteAllText(Path.Combine(_root, "settings.gradle"), "rootProject.name = 'recipe'\n");
        }
        else
        {
            File.WriteAllText(project, "scalaVersion := \"2.13.16\"\nlibraryDependencies += \"com.github.sbt\" % \"junit-interface\" % \"0.13.3\" % Test\n");
            Directory.CreateDirectory(Path.Combine(_root, "project"));
            File.WriteAllText(Path.Combine(_root, "project", "build.properties"), "sbt.version=1.11.7\n");
        }
        string sources = Path.Combine(_root, "src", "test", "java", "sample");
        Directory.CreateDirectory(sources);
        File.WriteAllText(Path.Combine(sources, "SelectedTest.java"), """
            package sample;
            public class SelectedTest {
                @org.junit.Test public void first() {}
                @org.junit.Test public void second() {}
            }
            """);
        File.WriteAllText(Path.Combine(sources, "UnrelatedTest.java"), """
            package sample;
            public class UnrelatedTest {
                @org.junit.Test public void fails() { throw new AssertionError("must not run"); }
            }
            """);
        string identity = JvmTestTooling.EncodeCaseId("native", project, backend, "sample.SelectedTest", "first");
        var recipe = ContinuousTestRecipeBuilder.Build(new ContinuousTestRunRecipeRequest("native", _root, project, backend, identity));
        Assert.Equal(backend == "gradle", recipe.IsExact);
        Assert.Equal(backend == "gradle" ? TestSelectorScope.SingleTest : TestSelectorScope.MatchingTests, recipe.Scope);
        Assert.False(Directory.Exists(Path.Combine(_root, ".miller")));
        var step = Assert.Single(recipe.Steps);
        var result = await new TestProcessRunner().RunAsync(
            new TestProcessCommand(step.Executable, step.Arguments, step.WorkingDirectory, step.Environment),
            TestContext.Current.CancellationToken);
        Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
        string reports = Path.Combine(_root, backend == "gradle" ? "build/test-results/test" : "target/test-reports");
        string report = Assert.Single(Directory.GetFiles(reports, "TEST-*.xml"));
        Assert.Equal(count, JUnitXmlResultParser.Parse(File.ReadAllText(report)).Cases.Count);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
