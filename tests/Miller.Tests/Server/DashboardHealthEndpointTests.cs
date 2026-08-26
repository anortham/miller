using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Miller.Dashboard;
using Miller.Server;
using Miller.Server.Cli;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// The <c>/healthz</c> body is a contract between two assemblies that never call each other: the
/// dashboard writes it, and the launcher in Miller.Server decides from it whether a dashboard is up.
/// The launcher matches a PREFIX so the body could grow to carry the build, so the prefix is the part
/// that must never move — reword it and every probe reads every dashboard as dead, and every launch
/// starts another one. This drives the production pipeline on an in-memory server to hold the two
/// halves together.
/// </summary>
public sealed class DashboardHealthEndpointTests : IDisposable
{
    private readonly string _dir;
    private readonly DashboardPaths _paths;

    public DashboardHealthEndpointTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-dash-healthz-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _paths = new DashboardPaths(
            Path.Combine(_dir, "workspaces.db"),
            Path.Combine(_dir, "telemetry.db"),
            Path.Combine(_dir, ".tools"),
            Path.Combine(_dir, "wwwroot"),
            "http://127.0.0.1:0");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Healthz_KeepsThePrefixTheLauncherProbesForAndNamesTheBuild()
    {
        using IHost host = await StartHostAsync();
        HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/healthz", TestContext.Current.CancellationToken);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        Assert.StartsWith(DashboardCliLauncher.HealthBody, body.Trim(), StringComparison.Ordinal);
        Assert.Contains(MillerVersion.Current, body, StringComparison.Ordinal);
    }

    private async Task<IHost> StartHostAsync()
    {
        IHost host = new HostBuilder()
            .ConfigureWebHost(webBuilder => webBuilder
                .UseTestServer()
                .ConfigureServices(DashboardHostPipeline.ConfigureServices)
                .Configure(app => DashboardHostPipeline.Configure(app, _paths, _dir)))
            .Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        return host;
    }
}
