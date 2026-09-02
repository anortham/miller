using Miller.Testing;
using Miller.Testing.Providers.Jvm;
using Xunit;

namespace Miller.Tests.Testing.Providers.Jvm;

[Trait("Category", "Scale")]
public sealed class JvmTestProviderScaleTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("miller-ct-jvm-scale-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task Gradle_smoke_discovers_and_runs_two_junit5_methods_without_source_build_output()
    {
        string java = CtProviderTestSupport.RequireJava();
        string gradle = CtProviderTestSupport.RequireGradle();
        Assert.True(File.Exists(java));
        Assert.True(File.Exists(gradle));
        File.WriteAllText(Path.Combine(_root, "build.gradle"), """
            plugins {
                id 'java'
            }
            buildDir = file("${rootDir}/build")
            test {
                reports.junitXml.outputLocation = file("${rootDir}/build/test-results/test")
            }
            repositories {
                mavenCentral()
            }
            dependencies {
                testImplementation 'org.junit.jupiter:junit-jupiter:5.10.2'
            }
            test {
                useJUnitPlatform()
            }
            """);
        string testPath = Path.Combine(_root, "src", "test", "java", "sample", "CalculatorTest.java");
        Directory.CreateDirectory(Path.GetDirectoryName(testPath)!);
        File.WriteAllText(testPath, """
            package sample;

            import static org.junit.jupiter.api.Assertions.assertTrue;
            import org.junit.jupiter.api.Test;

            class CalculatorTest {
                @Test void adds() { assertTrue(true); }
                @Test void subtracts() { assertTrue(true); }
            }
            """);
        string sourceHash = HashTree(_root);
        var workspace = new ContinuousTestWorkspace(
            WorkspaceId: "ws:jvm-scale",
            WorkspaceRoot: _root,
            ProjectPath: Path.Combine(_root, "build.gradle"),
            BuildOutputRoot: Path.Combine(_root, ".miller", "ct-jvm-scale"),
            Framework: "gradle");
        var provider = new JvmTestProvider(new TestProcessRunner());

        IReadOnlyList<ProviderTestCase> discovered = await provider.DiscoverAsync(
            workspace,
            TestContext.Current.CancellationToken);
        Assert.Equal(2, discovered.Count);
        Assert.All(discovered, test => Assert.Equal("jvm", test.Metadata["language_family"]));

        ProviderRunResult result = await provider.RunAsync(
            new ContinuousTestProviderRunRequest(
                Workspace: workspace,
                SelectedRevision: "rev-jvm-scale",
                IndexIdentity: "store:jvm-scale",
                TestCaseIds: discovered.Select(test => test.Id).ToArray()),
            TestContext.Current.CancellationToken);

        Assert.Equal("passed", result.Status);
        Assert.Equal(2, result.CaseResults.Count);
        Assert.Equal(sourceHash, HashTree(_root));
        Assert.False(Directory.Exists(Path.Combine(_root, "build")));
        Assert.All(result.CaseResults, row => Assert.StartsWith(workspace.BuildOutputRoot, row.Metadata["artifact_path"]!.ToString()!, StringComparison.Ordinal));
    }

    private static string HashTree(string root)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Where(path => !path.Contains(Path.Combine(".miller", "ct-jvm-scale"), StringComparison.Ordinal)))
        {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(Path.GetRelativePath(root, path)));
            hash.AppendData(File.ReadAllBytes(path));
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
