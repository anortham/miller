using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Miller.Dashboard;
using Miller.Dashboard.Components;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// The anti-flicker contract: polled fragments are conditional-GET cacheable (ETag/304) and the polled
/// sections opt into idiomorph morph swaps, so an unchanged poll costs no DOM work. HTTP tests drive the
/// EXACT production pipeline (<see cref="DashboardHostPipeline"/>) on an in-memory TestServer; the
/// antiforgery cookie is carried between polls exactly as a browser does, because the fragment embeds
/// per-cookie form tokens and a fresh cookie per request would defeat the hash.
/// </summary>
public sealed class DashboardFragmentCachingTests : IDisposable
{
    private readonly string _dir;
    private readonly DashboardPaths _paths;

    public DashboardFragmentCachingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-dash-etag-" + Guid.NewGuid().ToString("N"));
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
    public async Task FragmentWorkspaces_FirstRequest_CarriesAnETag()
    {
        SeedWorkspace("ws-a", "alpha-abcd1234");
        using IHost host = await StartHostAsync();
        HttpClient client = host.GetTestClient();
        string cookie = await GetAntiforgeryCookieAsync(client);

        HttpResponseMessage response = await GetFragmentAsync(client, cookie, ifNoneMatch: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Headers.ETag);
        Assert.True(response.Headers.ETag!.Tag.Length > 2);
        Assert.False(response.Headers.ETag.IsWeak);
    }

    [Fact]
    public async Task FragmentWorkspaces_RepeatWithMatchingIfNoneMatch_Returns304WithEmptyBody()
    {
        SeedWorkspace("ws-a", "alpha-abcd1234");
        using IHost host = await StartHostAsync();
        HttpClient client = host.GetTestClient();
        string cookie = await GetAntiforgeryCookieAsync(client);

        HttpResponseMessage first = await GetFragmentAsync(client, cookie, ifNoneMatch: null);
        string etag = first.Headers.ETag!.ToString();

        HttpResponseMessage second = await GetFragmentAsync(client, cookie, ifNoneMatch: etag);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Empty(await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FragmentWorkspaces_SameContentPolledTwice_ProducesTheSameETag()
    {
        SeedWorkspace("ws-a", "alpha-abcd1234");
        using IHost host = await StartHostAsync();
        HttpClient client = host.GetTestClient();
        string cookie = await GetAntiforgeryCookieAsync(client);

        HttpResponseMessage first = await GetFragmentAsync(client, cookie, ifNoneMatch: null);
        HttpResponseMessage second = await GetFragmentAsync(client, cookie, ifNoneMatch: null);

        Assert.Equal(first.Headers.ETag!.ToString(), second.Headers.ETag!.ToString());
    }

    [Fact]
    public async Task FragmentWorkspaces_AfterDataChanges_ReturnsFresh200WithADifferentETag()
    {
        SeedWorkspace("ws-a", "alpha-abcd1234");
        using IHost host = await StartHostAsync();
        HttpClient client = host.GetTestClient();
        string cookie = await GetAntiforgeryCookieAsync(client);

        HttpResponseMessage first = await GetFragmentAsync(client, cookie, ifNoneMatch: null);
        string etag = first.Headers.ETag!.ToString();

        SeedWorkspace("ws-b", "bravo-efgh5678");
        HttpResponseMessage second = await GetFragmentAsync(client, cookie, ifNoneMatch: etag);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.NotEqual(etag, second.Headers.ETag!.ToString());
        Assert.Contains("bravo-efgh5678", await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FragmentActivity_RepeatWithMatchingIfNoneMatch_Returns304WithEmptyBody()
    {
        SeedWorkspace("ws-a", "alpha-abcd1234");
        using IHost host = await StartHostAsync();
        HttpClient client = host.GetTestClient();
        string cookie = await GetAntiforgeryCookieAsync(client);

        HttpResponseMessage first = await GetFragmentAsync(
            client, cookie, ifNoneMatch: null, path: ActivityFragment);
        string etag = first.Headers.ETag!.ToString();

        HttpResponseMessage second = await GetFragmentAsync(
            client, cookie, ifNoneMatch: etag, path: ActivityFragment);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Empty(await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FragmentWorkspaces_WithADifferentAntiforgeryCookie_ReturnsFresh200WithADifferentETag()
    {
        SeedWorkspace("ws-a", "alpha-abcd1234");
        using IHost host = await StartHostAsync();
        HttpClient client = host.GetTestClient();

        string firstCookie = await GetAntiforgeryCookieAsync(client);
        HttpResponseMessage first = await GetFragmentAsync(client, firstCookie, ifNoneMatch: null);
        string etag = first.Headers.ETag!.ToString();

        // A request that carries no antiforgery cookie mints a fresh one — the server's own rotation path.
        string rotatedCookie = await GetAntiforgeryCookieAsync(client);
        Assert.NotEqual(firstCookie, rotatedCookie);

        HttpResponseMessage second = await GetFragmentAsync(client, rotatedCookie, ifNoneMatch: etag);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.NotEqual(etag, second.Headers.ETag!.ToString());
    }

    [Fact]
    public async Task FragmentWorkspaces_ServedBody_ShipsARealTokenNotTheHashingMask()
    {
        SeedWorkspace("ws-a", "alpha-abcd1234");
        using IHost host = await StartHostAsync();
        HttpClient client = host.GetTestClient();
        string cookie = await GetAntiforgeryCookieAsync(client);

        HttpResponseMessage response = await GetFragmentAsync(client, cookie, ifNoneMatch: null);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain("antiforgery-token-masked-for-hashing", body, StringComparison.Ordinal);
        Match token = Regex.Match(body, "name=\"__RequestVerificationToken\"[^>]*value=\"(?<v>[^\"]+)\"");
        Assert.True(token.Success, "the served fragment must still carry a real antiforgery token");
        Assert.StartsWith("CfDJ8", token.Groups["v"].Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonFragmentGet_IsNotETagged()
    {
        SeedWorkspace("ws-a", "alpha-abcd1234");
        using IHost host = await StartHostAsync();
        HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(response.Headers.ETag);
    }

    [Fact]
    public async Task IdiomorphExtensionAsset_IsServedByThePipeline()
    {
        string libDir = Path.Combine(_paths.WebRoot, "lib", "idiomorph");
        Directory.CreateDirectory(libDir);
        await File.WriteAllTextAsync(
            Path.Combine(libDir, "idiomorph-ext.min.js"),
            "var Idiomorph=0;",
            TestContext.Current.CancellationToken);
        using IHost host = await StartHostAsync();
        HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync(
            "/lib/idiomorph/idiomorph-ext.min.js", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/javascript", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(
            "var Idiomorph=0;",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StaticAsset_RevalidatesInsteadOfHeuristicCaching()
    {
        Directory.CreateDirectory(_paths.WebRoot);
        await File.WriteAllTextAsync(
            Path.Combine(_paths.WebRoot, "dashboard.css"), ":root{}", TestContext.Current.CancellationToken);
        using IHost host = await StartHostAsync();
        HttpClient client = host.GetTestClient();

        HttpResponseMessage first = await client.GetAsync("/dashboard.css", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.True(first.Headers.CacheControl?.NoCache);
        Assert.NotNull(first.Content.Headers.LastModified);

        using var conditional = new HttpRequestMessage(HttpMethod.Get, "/dashboard.css");
        conditional.Headers.IfModifiedSince = first.Content.Headers.LastModified;
        HttpResponseMessage second = await client.SendAsync(conditional, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
    }

    [Fact]
    public async Task AssetReferences_CarryTheBuildVersionQuery()
    {
        string head = await RenderComponentAsync<DashboardHead>(new Dictionary<string, object?>());
        string scripts = await RenderComponentAsync<DashboardScripts>(new Dictionary<string, object?>());

        Assert.Contains("/dashboard.css?v=", head, StringComparison.Ordinal);
        Assert.Contains("/lib/htmx/htmx.min.js?v=", head, StringComparison.Ordinal);
        Assert.Contains("/js/theme-init.js?v=", head, StringComparison.Ordinal);
        Assert.Contains("/lib/idiomorph/idiomorph-ext.min.js?v=", scripts, StringComparison.Ordinal);
        Assert.Contains("/js/dashboard-site.js?v=", scripts, StringComparison.Ordinal);
        Assert.Contains("/js/alpine-components.js?v=", scripts, StringComparison.Ordinal);
        Assert.Contains("/lib/alpine/cspalpine.min.js?v=", scripts, StringComparison.Ordinal);
    }

    [Fact]
    public void VendoredIdiomorphExtension_RegistersTheMorphHtmxExtension()
    {
        string vendored = Path.Combine(
            RepoRoot(), "src", "Miller.Dashboard", "wwwroot", "lib", "idiomorph", "idiomorph-ext.min.js");

        Assert.True(File.Exists(vendored), $"idiomorph must be vendored at {vendored}");
        string source = File.ReadAllText(vendored);
        Assert.Contains("htmx.defineExtension(\"morph\"", source, StringComparison.Ordinal);
        Assert.Contains("morph:outerHTML", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DashboardScripts_LoadsIdiomorphBeforeAlpine()
    {
        string markup = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Miller.Dashboard", "Components", "DashboardScripts.razor"));

        int idiomorph = markup.IndexOf("/lib/idiomorph/idiomorph-ext.min.js", StringComparison.Ordinal);
        int alpine = markup.IndexOf("/lib/alpine/cspalpine.min.js", StringComparison.Ordinal);

        Assert.True(idiomorph >= 0, "DashboardScripts must load the idiomorph extension");
        Assert.True(idiomorph < alpine, "idiomorph must load before Alpine");
    }

    [Fact]
    public async Task WorkspaceIndex_PolledSection_OptsIntoMorphSwaps()
    {
        SeedWorkspace("ws-a", "alpha-abcd1234");

        string html = await RenderComponentAsync<WorkspaceIndex>(new Dictionary<string, object?>
        {
            ["Index"] = DashboardData.ReadIndex(_paths.RegistryDbPath),
        });

        Assert.Contains("id=\"workspace-index\"", html, StringComparison.Ordinal);
        Assert.Contains("hx-ext=\"morph\"", html, StringComparison.Ordinal);
        Assert.Contains("hx-swap=\"morph:outerHTML\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActivityFeedPanel_PolledSection_OptsIntoMorphSwaps()
    {
        string html = await RenderComponentAsync<ActivityFeedPanel>(new Dictionary<string, object?>
        {
            ["Feed"] = DashboardData.ReadRecentActivity(_paths.TelemetryDbPath, _paths.RegistryDbPath, "ws-a"),
        });

        Assert.Contains("id=\"activity-feed-panel\"", html, StringComparison.Ordinal);
        Assert.Contains("hx-ext=\"morph\"", html, StringComparison.Ordinal);
        Assert.Contains("hx-swap=\"morph:outerHTML\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TelemetryPanel_PolledSectionAndRefreshButton_OptIntoMorphSwaps()
    {
        string html = await RenderComponentAsync<TelemetryPanel>(new Dictionary<string, object?>
        {
            ["Telemetry"] = DashboardData.ReadTelemetrySummary(_paths.TelemetryDbPath, "ws-a", _paths.RegistryDbPath),
            ["SelectedWorkspaceId"] = "ws-a",
        });

        Assert.Contains("id=\"telemetry-panel\"", html, StringComparison.Ordinal);
        Assert.Contains("hx-ext=\"morph\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("hx-swap=\"outerHTML\"", html, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(html, "hx-swap=\"morph:outerHTML\""));
    }

    [Fact]
    public async Task WorkspaceRemoveConfirm_Details_CarriesAStablePersistOpenKey()
    {
        string html = await RenderComponentAsync<WorkspaceRemoveConfirm>(new Dictionary<string, object?>
        {
            ["WorkspaceId"] = "ws-a",
        });

        Assert.Contains("data-issue-details", html, StringComparison.Ordinal);
        Assert.Contains("data-issue-id=\"remove-ws-a\"", html, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }

    private const string WorkspacesFragment = "/fragments/workspaces";
    private const string ActivityFragment = "/fragments/activity?workspace_id=ws-a";

    private static async Task<HttpResponseMessage> GetFragmentAsync(
        HttpClient client, string cookie, string? ifNoneMatch, string path = WorkspacesFragment)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", cookie);
        if (ifNoneMatch is not null)
        {
            request.Headers.Add("If-None-Match", ifNoneMatch);
        }
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<string> GetAntiforgeryCookieAsync(HttpClient client)
    {
        HttpResponseMessage page = await client.GetAsync("/", TestContext.Current.CancellationToken);
        page.EnsureSuccessStatusCode();
        string? setCookie = page.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values)
            ? values.FirstOrDefault(v => v.StartsWith(".AspNetCore.Antiforgery", StringComparison.Ordinal))
            : null;
        Assert.NotNull(setCookie);
        return setCookie!.Split(';')[0];
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

    /// <summary>
    /// The shared helper, not a private walk up from <c>AppContext.BaseDirectory</c>. Continuous testing
    /// builds this assembly into an out-of-repo directory, so that walk starts outside the repo and never
    /// finds <c>Miller.slnx</c>. <see cref="ScaleTestSupport.RepoRoot"/> falls back to the workspace-root
    /// variable CT sets, which is the only channel that survives xunit resetting the current directory.
    /// </summary>
    private static string RepoRoot() => ScaleTestSupport.RepoRoot();

    private static async Task<string> RenderComponentAsync<TComponent>(Dictionary<string, object?> parameters)
        where TComponent : IComponent
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Microsoft.AspNetCore.Components.Forms.AntiforgeryStateProvider>(
            new FixedAntiforgeryStateProvider());
        IServiceProvider provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(
            provider,
            provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<TComponent>(ParameterView.FromDictionary(parameters));
            return output.ToHtmlString();
        });
    }

    private sealed class FixedAntiforgeryStateProvider :
        Microsoft.AspNetCore.Components.Forms.AntiforgeryStateProvider
    {
        public override Microsoft.AspNetCore.Components.Forms.AntiforgeryRequestToken? GetAntiforgeryToken() =>
            new("test-token-value", "__RequestVerificationToken");
    }
}
