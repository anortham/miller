using Miller.Testing;
using Miller.Testing.Providers.Php;
using Miller.Testing.Providers.Qml;
using Miller.Testing.Providers.Ruby;
using Miller.Testing.Providers.Jvm;
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
    public void Default_factory_reports_containment_degradation_through_the_supplied_sink()
    {
        var reported = new List<string>();

        var factory = ContinuousTestProviderFactory.CreateDefault(onDiagnostic: reported.Add);

        var runner = Assert.IsType<TestProcessRunner>(factory.DefaultProcessRunner);
        Action<string> sink = Assert.IsType<Action<string>>(runner.Options.OnDiagnostic);
        sink("orphan containment could not be established");

        // The claim is that the sink the CALLER passed is the one the runner reports through. Dropping the
        // argument in CreateDefault leaves OnDiagnostic null and every degradation silent again, which is the
        // state that made an uncontained provider indistinguishable from a contained one.
        Assert.Equal(["orphan containment could not be established"], reported);
    }

    [Fact]
    public void Default_factory_shares_one_runner_across_every_provider()
    {
        // One runner, so one diagnostic sink covers all five providers rather than only the one that happened
        // to be constructed with it.
        var factory = ContinuousTestProviderFactory.CreateDefault(onDiagnostic: _ => { });

        Assert.NotNull(factory.DefaultProcessRunner);
    }

    [Fact]
    public void Default_factory_without_a_sink_stays_silent()
    {
        var factory = ContinuousTestProviderFactory.CreateDefault();

        var runner = Assert.IsType<TestProcessRunner>(factory.DefaultProcessRunner);
        Assert.Null(runner.Options.OnDiagnostic);
    }

    [Fact]
    public void Default_factory_wires_dotnet_rust_js_python_and_qml()
    {
        var factory = ContinuousTestProviderFactory.CreateDefault();
        Assert.IsType<DotnetTestProvider>(factory.Resolve(Workspace("app.csproj", null)).Provider);
        Assert.IsType<RustTestProvider>(factory.Resolve(Workspace("Cargo.toml", null)).Provider);
        Assert.IsType<JavaScriptTestProvider>(factory.Resolve(Workspace("package.json", null)).Provider);
        Assert.IsType<PythonTestProvider>(factory.Resolve(Workspace("pyproject.toml", null)).Provider);
        Assert.IsType<QtQuickTestProvider>(factory.Resolve(Workspace("CMakeLists.txt", "qt-quick-test")).Provider);
        Assert.Equal("ct-provider:qml", factory.Resolve(Workspace("CMakeLists.txt", "qt-quick-test")).ProviderSource);
        Assert.IsType<GoTestProvider>(factory.Resolve(Workspace("go.mod", null)).Provider);
        Assert.Equal("ct-provider:go", factory.Resolve(Workspace("go.mod", "go")).ProviderSource);
        Assert.IsType<RubyTestProvider>(factory.Resolve(Workspace("Gemfile", null)).Provider);
        Assert.Equal("ct-provider:ruby", factory.Resolve(Workspace("Gemfile", "rspec")).ProviderSource);
        Assert.IsType<PhpTestProvider>(factory.Resolve(Workspace("composer.json", null)).Provider);
        Assert.Equal("ct-provider:php", factory.Resolve(Workspace("composer.json", "phpunit")).ProviderSource);
        Assert.Equal("ct-provider:php", factory.Resolve(Workspace("composer.json", "pest")).ProviderSource);
        Assert.IsType<JvmTestProvider>(factory.Resolve(Workspace("build.gradle", "gradle")).Provider);
        Assert.Equal("ct-provider:jvm", factory.Resolve(Workspace("build.gradle", "gradle")).ProviderSource);
        Assert.IsType<JvmTestProvider>(factory.Resolve(Workspace("pom.xml", "maven")).Provider);
        Assert.Equal("ct-provider:jvm", factory.Resolve(Workspace("pom.xml", "maven")).ProviderSource);
    }

    [Fact]
    public void Default_factory_registers_sbt_with_the_jvm_provider()
    {
        var factory = ContinuousTestProviderFactory.CreateDefault();
        ContinuousTestProviderResolution resolution = factory.Resolve(Workspace("build.sbt", "sbt"));

        Assert.IsType<JvmTestProvider>(resolution.Provider);
        Assert.Equal("ct-provider:jvm", resolution.ProviderSource);
    }

    [Fact]
    public void Null_framework_jvm_detection_routes_maven_and_sbt_to_the_jvm_provider()
    {
        var factory = ContinuousTestProviderFactory.CreateDefault();

        Assert.Equal("ct-provider:jvm", factory.Resolve(Workspace("pom.xml", null)).ProviderSource);
        Assert.Equal("ct-provider:jvm", factory.Resolve(Workspace("build.sbt", null)).ProviderSource);
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
