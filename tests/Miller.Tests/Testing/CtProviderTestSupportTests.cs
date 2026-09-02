using Xunit;

namespace Miller.Tests.Testing;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ToolLocatorEnvironmentCollection
{
    public const string Name = "tool locator environment";
}

[Collection(ToolLocatorEnvironmentCollection.Name)]
public sealed class CtProviderTestSupportTests
{
    [Fact]
    public void LocateJava_requires_a_compiler_and_returns_the_launcher_path()
    {
        string root = Directory.CreateTempSubdirectory("miller-java-locator-").FullName;
        string java = Path.Combine(root, OperatingSystem.IsWindows() ? "java.exe" : "java");
        string javac = Path.Combine(root, OperatingSystem.IsWindows() ? "javac.exe" : "javac");
        string? previousPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            File.WriteAllText(java, string.Empty);
            Environment.SetEnvironmentVariable("PATH", root);
            Assert.Null(CtProviderTestSupport.LocateJava());

            File.WriteAllText(javac, string.Empty);
            Assert.Equal(java, CtProviderTestSupport.LocateJava());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
            Directory.Delete(root, recursive: true);
        }
    }
}
