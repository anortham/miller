using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
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
/// HTTP-level tests for the dashboard's registry-lifecycle mutation endpoints (ADR-0002) and the
/// loopback hardening around them, driving the EXACT production pipeline
/// (<see cref="DashboardHostPipeline"/>) on an in-memory TestServer. These prove what the
/// component-render tests cannot: antiforgery validation actually rejects token-less and bad-token
/// posts BEFORE any mutation, a valid token really mutates, rejection is a 400 — never a 500 through
/// the outer exception wrapper — a foreign <c>Host</c> is refused before any handler runs, and the
/// antiforgery-free POSTs demand the <c>X-Miller-Dashboard</c> header a cross-origin form cannot send.
/// </summary>
public sealed class DashboardMutationEndpointTests : IDisposable
{
    // htmx 2.0.4's "stop polling" code; System.Net.HttpStatusCode has no name for it.
    private const int PollingStopped = 286;

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

    [Fact]
    public async Task Request_WithForeignHost_Returns403BeforeAnyHandler()
    {
        SeedWorkspace("ws-a", "alpha-abcd1234");
        using IHost host = await StartHostAsync();
        HttpClient client = host.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "http://evil.example/");
        HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("http://localhost/")]
    [InlineData("http://127.0.0.1:4977/")]
    [InlineData("http://[::1]:4977/")]
    public async Task Request_WithLoopbackHost_IsServed(string url)
    {
        SeedWorkspace("ws-a", "alpha-abcd1234");
        using IHost host = await StartHostAsync();
        HttpClient client = host.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task FragmentRefreshPost_WithoutDashboardHeader_Returns400()
    {
        SeedWorkspace("ws-a", "alpha-abcd1234");
        using IHost host = await StartHostAsync();
        HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.PostAsync(
            "/fragments/refresh?workspace_id=ws-a", content: null, TestContext.Current.CancellationToken);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("X-Miller-Dashboard", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FragmentRefreshPost_WithDashboardHeader_RendersDetailStack()
    {
        SeedWorkspace("ws-a", "alpha-abcd1234");
        using IHost host = await StartHostAsync();
        HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await SendWithDashboardHeaderAsync(
            client, HttpMethod.Post, "/fragments/refresh?workspace_id=ws-a");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task FragmentRefreshPost_AnswersWhileTheRefreshIsStillRunning()
    {
        string workspaceId = NewWorkspaceId();
        SeedWorkspace(workspaceId, "alpha-abcd1234");
        var gate = new TaskCompletionSource();
        DashboardRefreshJobs.Start(workspaceId, GatedRefresh(gate, workspaceId));
        using IHost host = await StartHostAsync();
        HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await SendWithDashboardHeaderAsync(
            client, HttpMethod.Post, $"/fragments/refresh?workspace_id={workspaceId}");
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(gate.Task.IsCompleted);
        Assert.Contains($"hx-get=\"/fragments/refresh-status?workspace_id={workspaceId}\"", body, StringComparison.Ordinal);
        Assert.Contains("hx-trigger=\"every 2s\"", body, StringComparison.Ordinal);
        Assert.Contains("hx-target=\"this\"", body, StringComparison.Ordinal);
        Assert.Contains("hx-swap=\"morph:outerHTML\"", body, StringComparison.Ordinal);
        Assert.Contains("hx-trigger=\"miller:refresh-finished from:body\"", body, StringComparison.Ordinal);
        Assert.Contains("Refreshing", body, StringComparison.Ordinal);

        gate.SetResult();
    }

    // The whole stop: htmx captures the poll's element, target and URL in a closure at process time and
    // clears the timer only when it re-inits THAT element. An ancestor target never re-inits the span, so
    // the poll outlives the job — forever, against a route that used to answer with all ten panels.
    [Fact]
    public async Task RefreshStatusFragment_RunningRender_SelfTargetsSoHtmxClearsItsOwnPollTimer()
    {
        string workspaceId = NewWorkspaceId();
        SeedWorkspace(workspaceId, "alpha-abcd1234");
        var gate = new TaskCompletionSource();
        DashboardRefreshJobs.Start(workspaceId, GatedRefresh(gate, workspaceId));
        using IHost host = await StartHostAsync();
        HttpClient client = host.GetTestClient();

        (HttpResponseMessage response, string body) = await ReadAsync(
            client, $"/fragments/refresh-status?workspace_id={workspaceId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("hx-target=\"this\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("#workspace-detail-stack", body, StringComparison.Ordinal);

        gate.SetResult();
    }

    [Fact]
    public async Task RefreshStatusFragment_RendersTheStatusSpanWithoutTheTenPanelStack()
    {
        string workspaceId = NewWorkspaceId();
        SeedWorkspace(workspaceId, "alpha-abcd1234");
        var gate = new TaskCompletionSource();
        DashboardRefreshJobs.Start(workspaceId, GatedRefresh(gate, workspaceId));
        using IHost host = await StartHostAsync();
        HttpClient client = host.GetTestClient();

        string body = await GetBodyAsync(client, $"/fragments/refresh-status?workspace_id={workspaceId}");

        Assert.Contains("id=\"refresh-status\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"workspace-detail-stack\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Index transparency", body, StringComparison.Ordinal);

        gate.SetResult();
    }

    [Fact]
    public async Task RefreshStatusFragment_TerminalResponse_FiresTheDetailStackRefetchEvent()
    {
        string workspaceId = NewWorkspaceId();
        SeedWorkspace(workspaceId, "alpha-abcd1234");
        using IHost host = await StartHostAsync();
        HttpClient client = host.GetTestClient();

        (HttpResponseMessage response, _) = await ReadAsync(
            client, $"/fragments/refresh-status?workspace_id={workspaceId}");

        Assert.Equal(PollingStopped, (int)response.StatusCode);
        Assert.True(response.Headers.TryGetValues("HX-Trigger", out IEnumerable<string>? trigger));
        Assert.Equal("miller:refresh-finished", Assert.Single(trigger!));
    }

    [Fact]
    public async Task WorkspaceDetailStack_RefetchesItselfWhenARefreshFinishes()
    {
        string workspaceId = NewWorkspaceId();
        SeedWorkspace(workspaceId, "alpha-abcd1234");
        using IHost host = await StartHostAsync();
        HttpClient client = host.GetTestClient();

        string body = await GetBodyAsync(client, $"/workspace?workspace_id={workspaceId}");

        Assert.Contains("hx-trigger=\"miller:refresh-finished from:body\"", body, StringComparison.Ordinal);
        Assert.Contains(
            $"hx-get=\"/fragments/detail-stack?workspace_id={workspaceId}\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkspaceDetailStack_RefreshAndRefetchRequestsHaveOrderedSyncStrategies()
    {
        string workspaceId = NewWorkspaceId();
        SeedWorkspace(workspaceId, "alpha-abcd1234");
        using IHost host = await StartHostAsync();
        HttpClient client = host.GetTestClient();

        string body = await GetBodyAsync(client, $"/workspace?workspace_id={workspaceId}");

        Assert.Contains("hx-sync=\"#workspace-detail-stack:drop\"", body, StringComparison.Ordinal);
        Assert.Contains("hx-sync=\"#workspace-detail-stack:replace\"", body, StringComparison.Ordinal);
    }

    // An empty selector would render "workspace_id=", which the detail-stack route cannot resolve.
    [Fact]
    public async Task WorkspaceDetailStack_WithNoWorkspaceSelected_RendersNoRefetchTrigger()
    {
        using IHost host = await StartHostAsync();
        HttpClient client = host.GetTestClient();

        string body = await GetBodyAsync(client, "/workspace");

        Assert.Contains("id=\"workspace-detail-stack\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("miller:refresh-finished", body, StringComparison.Ordinal);
        Assert.DoesNotContain("/fragments/detail-stack", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DetailStackFragment_RendersTheOutcomeTheStatusPollAlreadyConsumed()
    {
        string workspaceId = NewWorkspaceId();
        SeedWorkspace(workspaceId, "alpha-abcd1234");
        var gate = new TaskCompletionSource();
        DashboardRefreshJobs.Start(workspaceId, GatedRefresh(gate, workspaceId));
        using IHost host = await StartHostAsync();
        HttpClient client = host.GetTestClient();

        gate.SetResult();
        await PollUntilAsync(
            client,
            $"/fragments/refresh-status?workspace_id={workspaceId}",
            body => body.Contains("rev 43", StringComparison.Ordinal));
        string stack = await GetBodyAsync(client, $"/fragments/detail-stack?workspace_id={workspaceId}");

        Assert.Contains("id=\"workspace-detail-stack\"", stack, StringComparison.Ordinal);
        Assert.Contains("rev 43", stack, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DetailStackFragment_ClaimsAnUnobservedTerminalJob()
    {
        string workspaceId = NewWorkspaceId();
        SeedWorkspace(workspaceId, "alpha-abcd1234");
        using IHost host = await StartHostAsync();
        HttpClient client = host.GetTestClient();

        var gate = new TaskCompletionSource();
        DashboardRefreshJobs.Start(workspaceId, GatedRefresh(gate, workspaceId));
        gate.SetResult();
        string stack = (await PollUntilAsync(
            client,
            $"/fragments/detail-stack?workspace_id={workspaceId}",
            body => body.Contains("rev 43", StringComparison.Ordinal))).Body;

        Assert.Contains("rev 43", stack, StringComparison.Ordinal);
        Assert.Null(DashboardRefreshJobs.Peek(workspaceId));
    }

    [Fact]
    public async Task PeekLastOutcome_SurvivesThePeekThatConsumedTheJob()
    {
        string workspaceId = NewWorkspaceId();
        var gate = new TaskCompletionSource();
        DashboardRefreshJobs.Start(workspaceId, GatedRefresh(gate, workspaceId));
        gate.SetResult();
        await PeekUntilCompletedAsync(workspaceId);

        Assert.Null(DashboardRefreshJobs.Peek(workspaceId));
        Assert.Equal(43L, DashboardRefreshJobs.PeekLastOutcome(workspaceId)?.Result?.Revision);
    }

    [Fact]
    public async Task PeekLastOutcome_ExpiresAfterTheRetentionWindow()
    {
        string workspaceId = NewWorkspaceId();
        var gate = new TaskCompletionSource();
        DashboardRefreshJobs.Start(workspaceId, GatedRefresh(gate, workspaceId));
        gate.SetResult();
        await PeekUntilCompletedAsync(workspaceId);

        Assert.NotNull(DashboardRefreshJobs.PeekLastOutcome(workspaceId));
        Assert.Null(DashboardRefreshJobs.PeekLastOutcome(
            workspaceId, DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5)));
    }

    // 286 is a success code, so EnsureSuccessStatusCode proves nothing about the stop; the terminal status
    // code is asserted on its own.
    [Fact]
    public async Task RefreshStatusFragment_RendersRunningThenTheTerminalResultExactlyOnce()
    {
        string workspaceId = NewWorkspaceId();
        SeedWorkspace(workspaceId, "alpha-abcd1234");
        var gate = new TaskCompletionSource();
        DashboardRefreshJobs.Start(workspaceId, GatedRefresh(gate, workspaceId));
        using IHost host = await StartHostAsync();
        HttpClient client = host.GetTestClient();
        string url = $"/fragments/refresh-status?workspace_id={workspaceId}";

        (HttpResponseMessage runningResponse, string running) = await ReadAsync(client, url);
        Assert.Equal(HttpStatusCode.OK, runningResponse.StatusCode);
        Assert.Contains("hx-trigger=\"every 2s\"", running, StringComparison.Ordinal);
        Assert.DoesNotContain("rev 43", running, StringComparison.Ordinal);

        gate.SetResult();
        (HttpResponseMessage terminalResponse, string terminal) =
            await PollUntilAsync(client, url, body => body.Contains("rev 43", StringComparison.Ordinal));
        Assert.Equal(PollingStopped, (int)terminalResponse.StatusCode);
        Assert.Contains("refreshed", terminal, StringComparison.Ordinal);
        Assert.DoesNotContain("hx-trigger=\"every 2s\"", terminal, StringComparison.Ordinal);
        Assert.DoesNotContain("/fragments/refresh-status", terminal, StringComparison.Ordinal);

        (HttpResponseMessage consumedResponse, string afterConsumed) = await ReadAsync(client, url);
        Assert.Equal(PollingStopped, (int)consumedResponse.StatusCode);
        Assert.DoesNotContain("rev 43", afterConsumed, StringComparison.Ordinal);
        Assert.DoesNotContain("hx-trigger=\"every 2s\"", afterConsumed, StringComparison.Ordinal);
    }

    // The status route's terminal render is delivered exactly once, so a 304 would drop the outcome and the
    // event that refreshes the panels — it must stay out of the fragment ETag cache. The activity fragment is
    // the control: it proves the middleware is live for every other fragment.
    [Fact]
    public async Task RefreshStatusFragment_IsExcludedFromFragmentETagCaching()
    {
        string workspaceId = NewWorkspaceId();
        SeedWorkspace(workspaceId, "alpha-abcd1234");
        using IHost host = await StartHostAsync();
        HttpClient client = host.GetTestClient();

        HttpResponseMessage status = await SendWithIfNoneMatchAsync(
            client, $"/fragments/refresh-status?workspace_id={workspaceId}");
        HttpResponseMessage activity = await SendWithIfNoneMatchAsync(
            client, $"/fragments/activity?workspace_id={workspaceId}");

        Assert.Equal(PollingStopped, (int)status.StatusCode);
        Assert.Null(status.Headers.ETag);
        Assert.Equal(HttpStatusCode.NotModified, activity.StatusCode);
    }

    [Fact]
    public async Task OpenFolderPost_WithoutDashboardHeader_Returns400()
    {
        using IHost host = await StartHostAsync();
        HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.PostAsync(
            "/workspaces/ws-unregistered/open-folder", content: null, TestContext.Current.CancellationToken);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("X-Miller-Dashboard", body, StringComparison.Ordinal);
    }

    // An unregistered id: the header gate is proven passed by reaching the registry lookup's 404, without
    // Process.Start opening a real file browser window on the machine running the suite.
    [Fact]
    public async Task OpenFolderPost_WithDashboardHeader_ReachesRegistryLookup()
    {
        using IHost host = await StartHostAsync();
        HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await SendWithDashboardHeaderAsync(
            client, HttpMethod.Post, "/workspaces/ws-unregistered/open-folder");
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Workspace not found in registry.", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JsonRefreshPost_WithoutDashboardHeader_Returns400()
    {
        SeedWorkspace("ws-a", "alpha-abcd1234");
        using IHost host = await StartHostAsync();
        HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.PostAsync(
            "/workspaces/ws-a/refresh", content: null, TestContext.Current.CancellationToken);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("X-Miller-Dashboard", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JsonRefreshPost_WithDashboardHeader_KeepsResponseShape()
    {
        using IHost host = await StartHostAsync();
        HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await SendWithDashboardHeaderAsync(
            client, HttpMethod.Post, "/workspaces/does-not-exist/refresh");
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using JsonDocument doc = JsonDocument.Parse(body);
        Assert.Equal("does-not-exist", doc.RootElement.GetProperty("WorkspaceId").GetString());
        Assert.Equal("failed", doc.RootElement.GetProperty("StatusText").GetString());
    }

    private static async Task<HttpResponseMessage> SendWithDashboardHeaderAsync(
        HttpClient client, HttpMethod method, string url)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Miller-Dashboard", "1");
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<HttpResponseMessage> SendWithIfNoneMatchAsync(HttpClient client, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("If-None-Match", "*");
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<(HttpResponseMessage Response, string Body)> ReadAsync(HttpClient client, string url)
    {
        HttpResponseMessage response = await client.GetAsync(url, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (response, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private static async Task<string> GetBodyAsync(HttpClient client, string url) =>
        (await ReadAsync(client, url)).Body;

    private static async Task<(HttpResponseMessage Response, string Body)> PollUntilAsync(
        HttpClient client, string url, Func<string, bool> matches)
    {
        for (int attempt = 0; attempt < 3000; attempt++)
        {
            (HttpResponseMessage response, string body) = await ReadAsync(client, url);
            if (matches(body))
                return (response, body);
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"{url} never rendered the expected state within 30s.");
    }

    private static async Task PeekUntilCompletedAsync(string workspaceId)
    {
        for (int attempt = 0; attempt < 3000; attempt++)
        {
            if (DashboardRefreshJobs.Peek(workspaceId) is { State: DashboardRefreshJobState.Completed })
                return;
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"the refresh job for {workspaceId} never completed within 30s.");
    }

    private static Func<WorkspaceRefreshResult> GatedRefresh(TaskCompletionSource gate, string workspaceId) =>
        () =>
        {
            gate.Task.GetAwaiter().GetResult();
            return new WorkspaceRefreshResult(
                WorkspaceRefreshStatus.Refreshed,
                workspaceId,
                "/repo/a",
                "/repo/a/.miller/symbols.db",
                Revision: 43,
                Scanned: true);
        };

    private static string NewWorkspaceId() => "ws-http-" + Guid.NewGuid().ToString("N");

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
        if (Directory.Exists(root))
        {
            string relativeIndexPath = Path.GetRelativePath(root, indexDbPath);
            root = PathCanonicalizer.CanonicalizeRoot(root);
            indexDbPath = Path.GetFullPath(Path.Combine(root, relativeIndexPath));
        }

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
