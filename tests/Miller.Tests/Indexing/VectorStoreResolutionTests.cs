using Miller.Indexing.Semantic;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Packaged-layout resolution of the sqlite-vec extension. A release archive carries
/// <c>.tools/vec0.&lt;ext&gt;</c> next to the binary, but <see cref="VectorStore.ExtensionPathEnvVar"/> keeps
/// absolute precedence, and neither present must stay a stated null rather than a guessed path.
/// </summary>
[Collection(SqliteVecEnvironment.Name)]
public sealed class VectorStoreResolutionTests : IDisposable
{
    private readonly string? _originalEnv =
        Environment.GetEnvironmentVariable(VectorStore.ExtensionPathEnvVar);

    private readonly string _baseDirectory =
        Path.Combine(Path.GetTempPath(), "miller-vec-resolution-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(VectorStore.ExtensionPathEnvVar, _originalEnv);

        if (Directory.Exists(_baseDirectory))
        {
            Directory.Delete(_baseDirectory, recursive: true);
        }
    }

    [Fact]
    public void EnvironmentOverrideWinsOverThePackagedExtension()
    {
        string packaged = WritePackagedExtension();
        Environment.SetEnvironmentVariable(VectorStore.ExtensionPathEnvVar, "/somewhere/else/vec0.dylib");

        string? resolved = VectorStore.ResolveExtensionPath(_baseDirectory);

        Assert.Equal("/somewhere/else/vec0.dylib", resolved);
        Assert.NotEqual(packaged, resolved);
    }

    [Fact]
    public void PackagedExtensionServesWhenTheEnvironmentOverrideIsUnset()
    {
        string packaged = WritePackagedExtension();
        Environment.SetEnvironmentVariable(VectorStore.ExtensionPathEnvVar, null);

        Assert.Equal(packaged, VectorStore.ResolveExtensionPath(_baseDirectory));
    }

    [Fact]
    public void EmptyEnvironmentOverrideFallsThroughToThePackagedExtension()
    {
        string packaged = WritePackagedExtension();
        Environment.SetEnvironmentVariable(VectorStore.ExtensionPathEnvVar, string.Empty);

        Assert.Equal(packaged, VectorStore.ResolveExtensionPath(_baseDirectory));
    }

    [Fact]
    public void NeitherOverrideNorPackagedExtensionResolvesToNull()
    {
        Directory.CreateDirectory(Path.Combine(_baseDirectory, ".tools"));
        Environment.SetEnvironmentVariable(VectorStore.ExtensionPathEnvVar, null);

        Assert.Null(VectorStore.ResolveExtensionPath(_baseDirectory));
    }

    [Fact]
    public void MissingBaseDirectoryResolvesToNull()
    {
        Environment.SetEnvironmentVariable(VectorStore.ExtensionPathEnvVar, null);

        Assert.Null(VectorStore.ResolveExtensionPath(_baseDirectory));
    }

    [Fact]
    public void PackagedExtensionFileNameMatchesTheHostPlatformLoadableSuffix()
    {
        string expected = OperatingSystem.IsWindows() ? "vec0.dll"
            : OperatingSystem.IsMacOS() ? "vec0.dylib"
            : "vec0.so";

        Assert.Equal(expected, VectorStore.PackagedExtensionFileName);
    }

    private string WritePackagedExtension()
    {
        string tools = Path.Combine(_baseDirectory, ".tools");
        Directory.CreateDirectory(tools);
        string packaged = Path.Combine(tools, VectorStore.PackagedExtensionFileName);
        File.WriteAllBytes(packaged, []);
        return packaged;
    }
}
