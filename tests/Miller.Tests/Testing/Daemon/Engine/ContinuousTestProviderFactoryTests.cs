using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Daemon.Engine;

public sealed class ContinuousTestProviderFactoryTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("miller-ct-factory-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Default_factory_wires_dotnet_rust_js_and_python()
    {
        var factory = ContinuousTestProviderFactory.CreateDefault();
        Assert.IsType<DotnetTestProvider>(factory.Resolve(Workspace("app.csproj", null)).Provider);
        Assert.IsType<RustTestProvider>(factory.Resolve(Workspace("Cargo.toml", null)).Provider);
        Assert.IsType<JavaScriptTestProvider>(factory.Resolve(Workspace("package.json", null)).Provider);
        Assert.IsType<PythonTestProvider>(factory.Resolve(Workspace("pyproject.toml", null)).Provider);
    }

    [Fact]
    public async Task Unsupported_framework_throws_on_use()
    {
        var factory = ContinuousTestProviderFactory.CreateDefault();
        ContinuousTestProviderResolution resolution = factory.Resolve(Workspace("unknown.cfg", "cobol"));
        Assert.Equal("ct-provider:unsupported", resolution.ProviderSource);
        await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            resolution.Provider.DiscoverAsync(Workspace("unknown.cfg", "cobol"), TestContext.Current.CancellationToken));
    }

    private ContinuousTestWorkspace Workspace(string fileName, string? framework)
    {
        string project = Path.Combine(_root, fileName);
        File.WriteAllText(project, "x");
        return new ContinuousTestWorkspace(
            "ws:factory",
            _root,
            project,
            Path.Combine(_root, "build-out"),
            Framework: framework);
    }
}
