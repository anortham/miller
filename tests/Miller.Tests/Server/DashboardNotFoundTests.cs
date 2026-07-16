using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Miller.Dashboard;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// HTTP-level tests for the dashboard's unresolved-workspace 404 page and the shared shell chrome
/// (version footer, new-tab JSON links), driving the EXACT production pipeline
/// (<see cref="DashboardHostPipeline"/>) on an in-memory TestServer.
/// </summary>
public sealed class DashboardNotFoundTests : IDisposable
{
    private readonly string _dir;
    private readonly DashboardPaths _paths;

    public DashboardNotFoundTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-dash-404-" + Guid.NewGuid().ToString("N"));
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
    public async Task WorkspaceGet_WithUnregisteredId_ReturnsStyledHtml404()
    {
        SeedWorkspace("ws-a", "alpha-abcd1234");
        using IHost host = await StartHostAsync();
        HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync(
            "/workspace?workspace_id=bogus", TestContext.Current.CancellationToken);
        string html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(
            "workspace_id 'bogus' is not registered — open / for the workspace list.",
            WebUtility.HtmlDecode(html),
            StringComparison.Ordinal);
        Assert.Contains("href=\"/\"", html, StringComparison.Ordinal);
        Assert.Contains("/dashboard.css", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkspaceGet_WithScriptInjectionId_EscapesIdIntoInertText()
    {
        SeedWorkspace("ws-a", "alpha-abcd1234");
        using IHost host = await StartHostAsync();
        HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync(
            "/workspace?workspace_id=%3Cscript%3Ealert(1)%3C%2Fscript%3E",
            TestContext.Current.CancellationToken);
        string html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("<script>alert(1)</script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkspaceGet_WithRegisteredId_RendersWorkspaceShellNot404()
    {
        SeedWorkspace("ws-a", "alpha-abcd1234");
        using IHost host = await StartHostAsync();
        HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync(
            "/workspace?workspace_id=ws-a", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task WorkspacesShell_RendersVersionFooterAndNewTabJsonLinks()
    {
        SeedWorkspace("ws-a", "alpha-abcd1234");
        using IHost host = await StartHostAsync();
        HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/", TestContext.Current.CancellationToken);
        string html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        AssertVersionFooter(html);
        AssertJsonLinksOpenInNewTab(html);
    }

    [Fact]
    public async Task WorkspaceShell_RendersVersionFooterAndNewTabJsonLinks()
    {
        SeedWorkspace("ws-a", "alpha-abcd1234");
        using IHost host = await StartHostAsync();
        HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync(
            "/workspace?workspace_id=ws-a", TestContext.Current.CancellationToken);
        string html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        AssertVersionFooter(html);
        AssertJsonLinksOpenInNewTab(html);
    }

    private static void AssertVersionFooter(string html)
    {
        Assert.Contains("class=\"site-footer\"", html, StringComparison.Ordinal);
        Assert.Contains(
            "miller " + Miller.Server.MillerVersion.Current,
            WebUtility.HtmlDecode(html),
            StringComparison.Ordinal);
        Assert.Contains("/diagnostics.json", html, StringComparison.Ordinal);
    }

    private static void AssertJsonLinksOpenInNewTab(string html)
    {
        int scanned = 0;
        int index = html.IndexOf("class=\"api-link\"", StringComparison.Ordinal);
        while (index >= 0)
        {
            int end = html.IndexOf('>', index);
            string anchor = html[index..end];
            Assert.Contains("target=\"_blank\"", anchor, StringComparison.Ordinal);
            Assert.Contains("rel=\"noopener\"", anchor, StringComparison.Ordinal);
            scanned++;
            index = html.IndexOf("class=\"api-link\"", end, StringComparison.Ordinal);
        }

        Assert.Equal(4, scanned);
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

    private void SeedWorkspace(string workspaceId, string displayId)
    {
        string root = Path.Combine(_dir, displayId);
        string millerDir = Path.Combine(root, ".miller");
        Directory.CreateDirectory(millerDir);
        File.WriteAllText(Path.Combine(millerDir, "symbols.db"), "stub index payload");
        using var registry = WorkspaceRegistry.Open(_paths.RegistryDbPath);
        registry.UpsertSeen(
            workspaceId,
            displayId,
            root,
            Path.Combine(millerDir, "symbols.db"),
            WorkspaceRegistryState.Current,
            DateTimeOffset.Parse("2026-07-08T10:00:00Z"));
    }
}
