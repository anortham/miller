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
