using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Miller.Dashboard;
using Miller.Indexing;
using Miller.Server.Workspaces;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// HTTP-level tests for the Tests-panel action endpoint, driving the production pipeline on an
/// in-memory TestServer with the subprocess seam injected. These prove the CSRF header gate, the
/// action allow-list, the workspace lookup, and that the response is the re-read tests fragment
/// carrying the action's outcome notice — without ever spawning a real miller process.
/// </summary>
public sealed class DashboardTestsActionEndpointTests : IDisposable
{
    private readonly string _dir;
    private readonly DashboardPaths _paths;

    public DashboardTestsActionEndpointTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-dash-ct-act-" + Guid.NewGuid().ToString("N"));
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
        DashboardTestsActions.RunProcessOverride = null;
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Post_WithoutDashboardHeader_Returns400AndRunsNothing()
    {
        SeedWorkspace("ws-a", "alpha-abcd1234");
        bool ran = false;
        DashboardTestsActions.RunProcessOverride = _ =>
        {
            ran = true;
            return new DashboardTestsActionOutcome(true, "should not happen");
        };
        using IHost host = await StartHostAsync();
        HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.PostAsync(
            "/workspaces/ws-a/tests/enable", content: null, TestContext.Current.CancellationToken);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("X-Miller-Dashboard", body, StringComparison.Ordinal);
        Assert.False(ran);
    }

    [Fact]
    public async Task Post_UnknownAction_Returns404AndRunsNothing()
    {
        SeedWorkspace("ws-a", "alpha-abcd1234");
        bool ran = false;
        DashboardTestsActions.RunProcessOverride = _ =>
        {
            ran = true;
            return new DashboardTestsActionOutcome(true, "should not happen");
        };
        using IHost host = await StartHostAsync();
        HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await SendAsync(client, "/workspaces/ws-a/tests/stop");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(ran);
    }

    [Fact]
    public async Task Post_UnknownWorkspace_Returns404AndRunsNothing()
    {
        bool ran = false;
        DashboardTestsActions.RunProcessOverride = _ =>
        {
            ran = true;
            return new DashboardTestsActionOutcome(true, "should not happen");
        };
        using IHost host = await StartHostAsync();
        HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await SendAsync(client, "/workspaces/no-such/tests/enable");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(ran);
    }

    [Fact]
    public async Task Post_Enable_RunsTheVerbInTheWorkspaceRootAndRendersTheOutcome()
    {
        string root = SeedWorkspace("ws-a", "alpha-abcd1234");
        ProcessStartInfo? received = null;
        DashboardTestsActions.RunProcessOverride = startInfo =>
        {
            received = startInfo;
            return new DashboardTestsActionOutcome(true, "enable 2 project(s)");
        };
        using IHost host = await StartHostAsync();
        HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await SendAsync(client, "/workspaces/ws-a/tests/enable");
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(received);
        Assert.Equal(["tests", "enable"], received!.ArgumentList);
        Assert.Equal(PathCanonicalizer.CanonicalizeRoot(root), received.WorkingDirectory);
        Assert.Contains("id=\"workspace-tests-panel\"", body, StringComparison.Ordinal);
        Assert.Contains("enable 2 project(s)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("error-notice", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Post_FailedAction_RendersTheOutcomeAsAnErrorNotice()
    {
        SeedWorkspace("ws-a", "alpha-abcd1234");
        DashboardTestsActions.RunProcessOverride = _ =>
            new DashboardTestsActionOutcome(false, "tests serve refused: not enabled");
        using IHost host = await StartHostAsync();
        HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await SendAsync(client, "/workspaces/ws-a/tests/start");
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("tests serve refused: not enabled", body, StringComparison.Ordinal);
        Assert.Contains("error-notice", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_WithoutAMillerExecutableBesideTheToolsRoot_FailsWithTheHonestReason()
    {
        DashboardTestsActionOutcome outcome = DashboardTestsActions.Run(
            _paths.ToolsRoot,
            _dir,
            "start");

        Assert.False(outcome.Success);
        Assert.Contains("miller executable was not found", outcome.Message, StringComparison.Ordinal);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-Miller-Dashboard", "1");
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
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

    private string SeedWorkspace(string workspaceId, string displayId)
    {
        string root = Path.Combine(_dir, displayId);
        Directory.CreateDirectory(root);
        string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(root);
        using var registry = WorkspaceRegistry.Open(_paths.RegistryDbPath);
        registry.UpsertSeen(
            workspaceId,
            displayId,
            canonicalRoot,
            Path.GetFullPath(Path.Combine(canonicalRoot, ".miller", "symbols.db")),
            WorkspaceRegistryState.Current,
            DateTimeOffset.Parse("2026-07-08T10:00:00Z"));
        return root;
    }
}
