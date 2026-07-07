using System.IO.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Miller.Server;
using Miller.Server.Hosting;
using Miller.Server.Telemetry;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the workspace-binding CallToolFilter: every tools/call must invoke
/// <see cref="IWorkspaceBindingService.EnsurePrimaryBoundAsync"/> before the tool body runs.
/// </summary>
public sealed class WorkspaceBindingCallToolFilterTests
{
    private sealed class RecordingBindingService : IWorkspaceBindingService
    {
        public int EnsureCalls { get; private set; }

        public int BindingGeneration => 1;

        public bool IsDeferred => true;

        public BootstrapSnapshot Snapshot { get; set; } = BoundSnapshot();

        public Task WaitUntilBoundAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task WaitForRunAsync(int runGeneration, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task EnsurePrimaryBoundAsync(McpServer server, CancellationToken cancellationToken)
        {
            EnsureCalls++;
            return Task.CompletedTask;
        }

        public void MarkRootsDirty() { }
    }

    private sealed class ScriptedBindingService : IWorkspaceBindingService
    {
        public int EnsureCalls { get; private set; }
        public int WaitForRunCalls { get; private set; }
        public int? LastWaitedRunGeneration { get; private set; }
        public Func<CancellationToken, Task>? OnEnsure { get; set; }
        public Func<int, CancellationToken, Task>? OnWaitForRun { get; set; }

        public int BindingGeneration => 1;

        public bool IsDeferred => true;

        public BootstrapSnapshot Snapshot { get; set; } = BoundSnapshot();

        public Task WaitUntilBoundAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task EnsurePrimaryBoundAsync(McpServer server, CancellationToken cancellationToken)
        {
            EnsureCalls++;
            return OnEnsure?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }

        public Task WaitForRunAsync(int runGeneration, CancellationToken cancellationToken)
        {
            WaitForRunCalls++;
            LastWaitedRunGeneration = runGeneration;
            return OnWaitForRun?.Invoke(runGeneration, cancellationToken) ?? Task.CompletedTask;
        }

        public void MarkRootsDirty() { }
    }

    [Fact]
    public async Task BoundSnapshot_CallsNextHandler()
    {
        var binding = new ScriptedBindingService { Snapshot = BoundSnapshot() };
        int nextCalls = 0;

        var result = await InvokeFilterAsync(binding, "search", (_, _) =>
        {
            nextCalls++;
            return Task.FromResult(TextResult("next-result"));
        }, TestContext.Current.CancellationToken);

        Assert.Equal(1, binding.EnsureCalls);
        Assert.Equal(1, nextCalls);
        Assert.Equal("next-result", ResultText(result));
    }

    [Fact]
    public async Task RunningSnapshot_WhenRunCompletesWithinGrace_CallsNextHandler()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var binding = new ScriptedBindingService
        {
            Snapshot = RunningSnapshot("/tmp/miller-running", runGeneration: 7),
        };
        binding.OnWaitForRun = async (_, ct) =>
        {
            await gate.Task.WaitAsync(ct);
            binding.Snapshot = BoundSnapshot("/tmp/miller-running", runGeneration: 7);
        };

        var call = InvokeFilterAsync(
            binding, "search", (_, _) => Task.FromResult(TextResult("after-bind")),
            TestContext.Current.CancellationToken);
        gate.SetResult();
        var result = await call;

        Assert.Equal(1, binding.WaitForRunCalls);
        Assert.Equal(7, binding.LastWaitedRunGeneration);
        Assert.Equal("after-bind", ResultText(result));
    }

    [Fact]
    public async Task RunningSnapshot_WhenGraceExpires_ReturnsNotReadyToolError()
    {
        using var env = ScopedEnvironment.Set("MILLER_BOOTSTRAP_GRACE_SECONDS", "0.01");
        var binding = new ScriptedBindingService
        {
            Snapshot = RunningSnapshot("/tmp/miller-running", runGeneration: 9),
        };
        binding.OnWaitForRun = (_, ct) => Task.Delay(TimeSpan.FromSeconds(5), ct);

        var result = await InvokeFilterAsync(
            binding, "search", (_, _) => Task.FromResult(TextResult("unreachable")),
            TestContext.Current.CancellationToken);

        Assert.Equal(true, result.IsError);
        Assert.Equal(
            "Miller is indexing this workspace for the first time: /tmp/miller-running (started 0s ago). Tool calls will work once indexing completes — retry shortly, or run 'workspace status' for progress.",
            ResultText(result));
    }

    [Fact]
    public async Task RunningSnapshot_WithZeroGrace_ReturnsNotReadyWithoutWaiting()
    {
        using var env = ScopedEnvironment.Set("MILLER_BOOTSTRAP_GRACE_SECONDS", "0");
        var binding = new ScriptedBindingService
        {
            Snapshot = RunningSnapshot("/tmp/miller-zero", runGeneration: 11),
        };
        binding.OnWaitForRun = (_, _) => throw new InvalidOperationException("zero grace must not wait");

        var result = await InvokeFilterAsync(
            binding, "search", (_, _) => Task.FromResult(TextResult("unreachable")),
            TestContext.Current.CancellationToken);

        Assert.Equal(true, result.IsError);
        Assert.Equal(0, binding.WaitForRunCalls);
        Assert.Contains("/tmp/miller-zero", ResultText(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunningSnapshot_CancellationDuringGrace_PropagatesCancellation()
    {
        using var env = ScopedEnvironment.Set("MILLER_BOOTSTRAP_GRACE_SECONDS", "5");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));
        var binding = new ScriptedBindingService
        {
            Snapshot = RunningSnapshot("/tmp/miller-cancel", runGeneration: 13),
        };
        binding.OnWaitForRun = (_, ct) => Task.Delay(TimeSpan.FromSeconds(5), ct);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await InvokeFilterAsync(binding, "search", (_, _) => Task.FromResult(TextResult("unreachable")), cts.Token));
    }

    [Fact]
    public async Task FailedRetrySnapshot_ReturnsStoredFailureToolError()
    {
        var binding = new ScriptedBindingService
        {
            Snapshot = FailedSnapshot("/tmp/miller-failed", "synthetic failure", runGeneration: 17),
        };
        binding.OnEnsure = _ =>
        {
            binding.Snapshot = RunningSnapshot(
                "/tmp/miller-failed", runGeneration: 18, lastFailureMessage: "synthetic failure");
            return Task.CompletedTask;
        };

        var result = await InvokeFilterAsync(
            binding, "search", (_, _) => Task.FromResult(TextResult("unreachable")),
            TestContext.Current.CancellationToken);

        Assert.Equal(true, result.IsError);
        Assert.Equal("bootstrap failed: synthetic failure; retry started — call again shortly.", ResultText(result));
    }

    [Fact]
    public async Task UnboundWorkspaceTool_RendersRunningSnapshotWithoutCallingTool()
    {
        var binding = new ScriptedBindingService
        {
            Snapshot = RunningSnapshot("/tmp/miller-workspace", runGeneration: 21),
        };

        var result = await InvokeFilterAsync(
            binding, "workspace", (_, _) => Task.FromResult(TextResult("unreachable")),
            TestContext.Current.CancellationToken);

        Assert.NotEqual(true, result.IsError);
        Assert.Equal("bootstrap: running /tmp/miller-workspace, started 0s ago", ResultText(result));
    }

    [Fact]
    public async Task BoundWorkspaceTool_CallsNextHandler()
    {
        var binding = new ScriptedBindingService
        {
            Snapshot = BoundSnapshot("/tmp/miller-bound-workspace", runGeneration: 23),
        };
        int nextCalls = 0;

        var result = await InvokeFilterAsync(binding, "workspace", (_, _) =>
        {
            nextCalls++;
            return Task.FromResult(TextResult("workspace-status"));
        }, TestContext.Current.CancellationToken);

        Assert.Equal(1, nextCalls);
        Assert.Equal("workspace-status", ResultText(result));
    }

    [Fact]
    public async Task RebindRunningWhileBound_WorkspaceToolCallsNextHandler()
    {
        var binding = new ScriptedBindingService
        {
            Snapshot = RunningSnapshot("/tmp/miller-rebind-b", runGeneration: 31, isBound: true),
        };
        int nextCalls = 0;

        var result = await InvokeFilterAsync(binding, "workspace", (_, _) =>
        {
            nextCalls++;
            return Task.FromResult(TextResult("workspace-status-with-rebind-notice"));
        }, TestContext.Current.CancellationToken);

        Assert.Equal(1, nextCalls);
        Assert.Equal("workspace-status-with-rebind-notice", ResultText(result));
    }

    [Fact]
    public async Task RebindFailedWhileBound_WorkspaceToolCallsNextHandler()
    {
        var binding = new ScriptedBindingService
        {
            Snapshot = FailedSnapshot("/tmp/miller-rebind-b", "rebind failure", runGeneration: 33, isBound: true),
        };
        int nextCalls = 0;

        var result = await InvokeFilterAsync(binding, "workspace", (_, _) =>
        {
            nextCalls++;
            return Task.FromResult(TextResult("workspace-status-after-failed-rebind"));
        }, TestContext.Current.CancellationToken);

        Assert.Equal(1, nextCalls);
        Assert.Equal("workspace-status-after-failed-rebind", ResultText(result));
    }

    [Fact]
    public async Task RebindRunningWhileBound_NonWorkspaceToolStillReturnsNotReady()
    {
        using var env = ScopedEnvironment.Set("MILLER_BOOTSTRAP_GRACE_SECONDS", "0");
        var binding = new ScriptedBindingService
        {
            Snapshot = RunningSnapshot("/tmp/miller-rebind-b", runGeneration: 35, isBound: true),
        };

        var result = await InvokeFilterAsync(
            binding, "search", (_, _) => Task.FromResult(TextResult("stale-answer-from-old-root")),
            TestContext.Current.CancellationToken);

        Assert.Equal(true, result.IsError);
        Assert.Contains("/tmp/miller-rebind-b", ResultText(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallToolFilter_InvokesBindingBeforeToolHandler()
    {
        var ct = TestContext.Current.CancellationToken;
        var binding = new RecordingBindingService();

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var services = new ServiceCollection();
        services.AddSingleton<IWorkspaceBindingService>(binding);
        services
            .AddMcpServer(o => { o.ServerInfo = new() { Name = "bind-filter", Version = "0" }; })
            .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream())
            .WithToolsFromAssembly(typeof(PinProbeTool).Assembly)
            .WithRequestFilters(f =>
            {
                f.AddCallToolFilter(WorkspaceBindingCallToolFilter.Create());
            });

        await using var provider = services.BuildServiceProvider();
        var server = provider.GetRequiredService<McpServer>();
        var serverTask = server.RunAsync(ct);

        var clientTransport = new StreamClientTransport(
            clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream(), NullLoggerFactory.Instance);
        await using var client = await McpClient.CreateAsync(clientTransport, cancellationToken: ct);

        await client.CallToolAsync(
            "pin_greet", new Dictionary<string, object?> { ["who"] = "binding" }!, cancellationToken: ct);

        await client.DisposeAsync();
        await clientToServer.Writer.CompleteAsync();
        await serverToClient.Writer.CompleteAsync();
        try { await serverTask.WaitAsync(TimeSpan.FromSeconds(5), ct); } catch (Exception) { }

        Assert.Equal(1, binding.EnsureCalls);
    }

    [Fact]
    public async Task BindingFilter_ComposesOutsideTelemetry_ForUnboundResponses()
    {
        using var env = ScopedEnvironment.Set("MILLER_BOOTSTRAP_GRACE_SECONDS", "0");
        var ct = TestContext.Current.CancellationToken;
        var binding = new ScriptedBindingService
        {
            Snapshot = RunningSnapshot("/tmp/miller-order", runGeneration: 29),
        };

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var services = new ServiceCollection();
        services.AddSingleton<IWorkspaceBindingService>(binding);
        services.AddSingleton<TelemetryLedger>(_ => throw new InvalidOperationException(
            "telemetry filter ran before binding produced the unbound response"));
        services
            .AddMcpServer(o => { o.ServerInfo = new() { Name = "bind-filter-order", Version = "0" }; })
            .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream())
            .WithToolsFromAssembly(typeof(PinProbeTool).Assembly)
            .WithRequestFilters(f =>
            {
                f.AddCallToolFilter(WorkspaceBindingCallToolFilter.Create());
                f.AddCallToolFilter(TelemetryCallToolFilter.Create());
            });

        await using var provider = services.BuildServiceProvider();
        var server = provider.GetRequiredService<McpServer>();
        var serverTask = server.RunAsync(ct);

        var clientTransport = new StreamClientTransport(
            clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream(), NullLoggerFactory.Instance);
        await using var client = await McpClient.CreateAsync(clientTransport, cancellationToken: ct);

        var result = await client.CallToolAsync(
            "pin_greet", new Dictionary<string, object?> { ["who"] = "order" }!, cancellationToken: ct);

        await client.DisposeAsync();
        await clientToServer.Writer.CompleteAsync();
        await serverToClient.Writer.CompleteAsync();
        try { await serverTask.WaitAsync(TimeSpan.FromSeconds(5), ct); } catch (Exception) { }

        Assert.Equal(true, result.IsError);
        Assert.Contains("/tmp/miller-order", ResultText(result), StringComparison.Ordinal);
    }

    private static async Task<CallToolResult> InvokeFilterAsync(
        IWorkspaceBindingService binding,
        string toolName,
        Func<RequestContext<CallToolRequestParams>, CancellationToken, Task<CallToolResult>> next,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken == default)
            cancellationToken = TestContext.Current.CancellationToken;

        var services = new ServiceCollection();
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        services.AddSingleton(binding);
        services.AddSingleton<IWorkspaceBindingService>(binding);
        services
            .AddMcpServer(o => { o.ServerInfo = new() { Name = "bind-filter-direct", Version = "0" }; })
            .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());

        await using var provider = services.BuildServiceProvider();
        var server = provider.GetRequiredService<McpServer>();
        var request = new RequestContext<CallToolRequestParams>(
            server,
            new JsonRpcRequest { Method = "tools/call" },
            new CallToolRequestParams { Name = toolName });
        var filtered = WorkspaceBindingCallToolFilter.Create()(async (ctx, ct) => await next(ctx, ct));
        return await filtered(request, cancellationToken);
    }

    private static BootstrapSnapshot BoundSnapshot(
        string canonicalRoot = "/tmp/miller-bound", int runGeneration = 1) =>
        new(
            BootstrapPhase.Bound,
            canonicalRoot,
            StartedAtUtc: null,
            FailureMessage: null,
            LastFailureMessage: null,
            runGeneration,
            IsBound: true);

    private static BootstrapSnapshot RunningSnapshot(
        string canonicalRoot,
        int runGeneration,
        string? lastFailureMessage = null,
        bool isBound = false) =>
        new(
            BootstrapPhase.Running,
            canonicalRoot,
            DateTimeOffset.UtcNow.AddSeconds(1),
            FailureMessage: null,
            lastFailureMessage,
            runGeneration,
            isBound);

    private static BootstrapSnapshot FailedSnapshot(
        string canonicalRoot,
        string failureMessage,
        int runGeneration,
        bool isBound = false) =>
        new(
            BootstrapPhase.Failed,
            canonicalRoot,
            StartedAtUtc: null,
            failureMessage,
            failureMessage,
            runGeneration,
            isBound);

    private static CallToolResult TextResult(string text, bool isError = false) =>
        new()
        {
            IsError = isError,
            Content = [new TextContentBlock { Text = text }],
        };

    private static string ResultText(CallToolResult result) =>
        Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    private sealed class ScopedEnvironment : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        private ScopedEnvironment(string name, string? value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public static ScopedEnvironment Set(string name, string? value) => new(name, value);

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }
}
