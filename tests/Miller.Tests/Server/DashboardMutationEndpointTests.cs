using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Miller.Dashboard;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// HTTP-level tests for the dashboard's registry-lifecycle mutation endpoints (ADR-0002), driving the
/// EXACT production pipeline (<see cref="DashboardHostPipeline"/>) on an in-memory TestServer. These
/// prove what the component-render tests cannot: antiforgery validation actually rejects token-less and
/// bad-token posts BEFORE any mutation, a valid token really mutates, and rejection is a 400 — never a
/// 500 through the outer exception wrapper.
/// </summary>
public sealed class DashboardMutationEndpointTests : IDisposable
{
    private readonly string _dir;
    private readonly DashboardPaths _paths;

    public DashboardMutationEndpointTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-dash-http-" + Guid.NewGuid().ToString("N"));
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
    public async Task RemovePost_WithoutAntiforgeryToken_Returns400AndMutatesNothing()
    {
        string millerDir = SeedWorkspace("ws-a", "alpha-abcd1234");
        using IHost host = await StartHostAsync();
        HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.PostAsync(
            "/workspace/remove",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("workspace_id", "ws-a")]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(File.Exists(Path.Combine(millerDir, "symbols.db")), "index data must survive a CSRF post");
        Assert.NotNull(FindRow("ws-a"));
    }

    [Fact]
    public async Task RemovePost_WithGarbageToken_Returns400Not500()
    {
        SeedWorkspace("ws-a", "alpha-abcd1234");
        using IHost host = await StartHostAsync();
        HttpClient client = host.GetTestClient();
        (_, string cookie) = await GetAntiforgeryAsync(client);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/workspace/remove")
        {
            Content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("workspace_id", "ws-a"),
                new KeyValuePair<string, string>("__RequestVerificationToken", "not-a-real-token"),
            ]),
        };
        request.Headers.Add("Cookie", cookie);
        HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(FindRow("ws-a"));
    }

    [Fact]
    public async Task RemovePost_WithValidToken_RemovesRowAndIndexDir()
    {
        string millerDir = SeedWorkspace("ws-a", "alpha-abcd1234");
        using IHost host = await StartHostAsync();
        HttpClient client = host.GetTestClient();
        (string token, string cookie) = await GetAntiforgeryAsync(client);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/workspace/remove")
        {
            Content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("workspace_id", "ws-a"),
                new KeyValuePair<string, string>("__RequestVerificationToken", token),
            ]),
        };
        request.Headers.Add("Cookie", cookie);
        HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("notice=removed", response.Headers.Location!.ToString());
        Assert.False(Directory.Exists(millerDir), "the .miller index dir must be deleted");
        Assert.Null(FindRow("ws-a"));
    }

    [Fact]
    public async Task PrunePost_WithValidToken_RemovesExactlyMissingRootRows()
    {
        SeedWorkspace("ws-live", "live-abcd1234");
        SeedMissingRootWorkspace("ws-gone", "gone-efgh5678");
        using IHost host = await StartHostAsync();
        HttpClient client = host.GetTestClient();
        (string token, string cookie) = await GetAntiforgeryAsync(client);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/workspaces/prune")
        {
            Content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("__RequestVerificationToken", token),
            ]),
        };
        request.Headers.Add("Cookie", cookie);
        HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("notice=pruned", response.Headers.Location!.ToString());
        Assert.Contains("detail=1", response.Headers.Location!.ToString());
        Assert.Null(FindRow("ws-gone"));
        Assert.NotNull(FindRow("ws-live"));
    }

    [Fact]
    public async Task PrunePost_WithoutAntiforgeryToken_Returns400AndKeepsRows()
    {
        SeedMissingRootWorkspace("ws-gone", "gone-efgh5678");
        using IHost host = await StartHostAsync();
        HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.PostAsync(
            "/workspaces/prune", new FormUrlEncodedContent([]), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(FindRow("ws-gone"));
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

    // A real page GET issues the antiforgery cookie and renders the hidden form token; harvest both the
    // way a browser would so the POST is exactly what the SSR form submits.
    private static async Task<(string Token, string Cookie)> GetAntiforgeryAsync(HttpClient client)
    {
        HttpResponseMessage page = await client.GetAsync("/", TestContext.Current.CancellationToken);
        page.EnsureSuccessStatusCode();
        string html = await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Match token = Regex.Match(
            html, "name=\"__RequestVerificationToken\"[^>]*value=\"(?<v>[^\"]+)\"");
        Assert.True(token.Success, "the rendered page must carry an antiforgery form token");
        string? setCookie = page.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values)
            ? values.FirstOrDefault(v => v.StartsWith(".AspNetCore.Antiforgery", StringComparison.Ordinal))
            : null;
        Assert.NotNull(setCookie);
        return (token.Groups["v"].Value, setCookie!.Split(';')[0]);
    }

    private string SeedWorkspace(string workspaceId, string displayId)
    {
        string root = Path.Combine(_dir, displayId);
        string millerDir = Path.Combine(root, ".miller");
        Directory.CreateDirectory(millerDir);
        File.WriteAllText(Path.Combine(millerDir, "symbols.db"), "stub index payload");
        Register(workspaceId, displayId, root, Path.Combine(millerDir, "symbols.db"));
        return millerDir;
    }

    private void SeedMissingRootWorkspace(string workspaceId, string displayId)
    {
        string root = Path.Combine(_dir, displayId + "-deleted");
        Register(workspaceId, displayId, root, Path.Combine(root, ".miller", "symbols.db"));
    }

    private void Register(string workspaceId, string displayId, string root, string indexDbPath)
    {
        using var registry = WorkspaceRegistry.Open(_paths.RegistryDbPath);
        registry.UpsertSeen(
            workspaceId,
            displayId,
            root,
            indexDbPath,
            WorkspaceRegistryState.Current,
            DateTimeOffset.Parse("2026-07-08T10:00:00Z"));
    }

    private WorkspaceRegistryRow? FindRow(string workspaceId)
    {
        using var registry = WorkspaceRegistry.Open(_paths.RegistryDbPath);
        return registry.List().FirstOrDefault(row => row.WorkspaceId == workspaceId);
    }
}
