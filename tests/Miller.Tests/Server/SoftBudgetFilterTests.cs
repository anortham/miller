using System.ComponentModel;
using System.IO.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Miller.Server.Telemetry;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// A reflection-discoverable tool whose only purpose is to return a payload large enough to blow the est-tokens
/// budget, so the soft-budget WARN path in the central filter can be driven end-to-end through the real SDK.
/// </summary>
[McpServerToolType]
public static class BudgetProbeTool
{
    /// <summary>Returns a string of <paramref name="size"/> bytes (drives est_tokens ≈ size/4 over the budget).</summary>
    [McpServerTool(Name = "budget_fat"), Description("Returns a large payload to exceed the token budget.")]
    public static string Fat(int size) => new('x', size);
}

/// <summary>
/// Pins M7 decision-4 wiring: the ONE central <see cref="TelemetryCallToolFilter"/> evaluates
/// <see cref="SoftBudgets"/> after the inner handler and logs a Serilog/ILogger WARN per breach — warn-ONLY
/// (never blocks, never turns the call into an error). Also pins the absent-registration path: with no
/// <see cref="SoftBudgets"/> in DI the filter skips the check silently (a test harness may not register it).
/// In-process + fast → default suite.
/// </summary>
public sealed class SoftBudgetFilterTests
{
    /// <summary>An ILoggerProvider that captures every WARN+ message so the test can assert on the budget log.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Entries);

        public void Dispose() { }

        private sealed class CapturingLogger : ILogger
        {
            private readonly List<(LogLevel, string)> _entries;
            public CapturingLogger(List<(LogLevel, string)> entries) => _entries = entries;
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (_entries)
                    _entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    private static async Task<(CallToolResult result, IReadOnlyList<(LogLevel Level, string Message)> logs)>
        CallThroughFilterAsync(bool registerBudgets, string toolName, IReadOnlyDictionary<string, object?> args)
    {
        var ct = TestContext.Current.CancellationToken;
        var capture = new CapturingLoggerProvider();

        // The central filter short-circuits when no TelemetryLedger is registered, so a real ledger over a temp
        // DB is required to even reach the budget-evaluation path. Owned + disposed within this call.
        string dir = Path.Combine(Path.GetTempPath(), "miller-budgetfilter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var ledger = TelemetryLedger.Open(Path.Combine(dir, "telemetry.db"), workspaceId: "budget-ws");

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(capture);
        });
        services.AddSingleton(ledger);
        if (registerBudgets)
            services.AddSingleton(SoftBudgets.Default);
        services
            .AddMcpServer(o => { o.ServerInfo = new() { Name = "budget", Version = "0" }; })
            .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream())
            .WithToolsFromAssembly(typeof(BudgetProbeTool).Assembly)
            .WithRequestFilters(f => f.AddCallToolFilter(TelemetryCallToolFilter.Create()));

        await using var provider = services.BuildServiceProvider();
        var server = provider.GetRequiredService<McpServer>();
        var serverTask = server.RunAsync(ct);

        var clientTransport = new StreamClientTransport(
            clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream());
        await using var client = await McpClient.CreateAsync(clientTransport, cancellationToken: ct);

        var result = await client.CallToolAsync(toolName, new Dictionary<string, object?>(args)!, cancellationToken: ct);

        await client.DisposeAsync();
        await clientToServer.Writer.CompleteAsync();
        await serverToClient.Writer.CompleteAsync();
        try { await serverTask.WaitAsync(TimeSpan.FromSeconds(5), ct); } catch (Exception) { }

        ledger.Dispose();
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }

        lock (capture.Entries)
            return (result, capture.Entries.ToList());
    }

    [Fact]
    public async Task FatCall_OverTokenBudget_LogsAWarn_ButCallStillSucceeds()
    {
        // 'budget_fat' is not a budgeted tool, so it uses the default budget (8000 tokens). 64KB ≈ 16K tokens,
        // comfortably over. The call must STILL succeed (warn-only) and a WARN naming the breach must be logged.
        var (result, logs) = await CallThroughFilterAsync(
            registerBudgets: true, "budget_fat", new Dictionary<string, object?> { ["size"] = 64 * 1024 });

        // Warn-only: the call is not turned into an error.
        Assert.NotEqual(true, result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.Equal(64 * 1024, text.Text.Length);

        var warn = Assert.Single(logs, e => e.Level == LogLevel.Warning);
        Assert.Contains("budget_fat", warn.Message);
        Assert.Contains("token", warn.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SmallCall_UnderBudget_LogsNoWarn()
    {
        var (result, logs) = await CallThroughFilterAsync(
            registerBudgets: true, "budget_fat", new Dictionary<string, object?> { ["size"] = 16 });

        Assert.NotEqual(true, result.IsError);
        Assert.DoesNotContain(logs, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task NoSoftBudgetsRegistered_SkipsSilently_NoWarn_CallSucceeds()
    {
        // A harness that does not register SoftBudgets must not break: the filter skips the check, the call still
        // returns its (large) payload, and no budget WARN is emitted.
        var (result, logs) = await CallThroughFilterAsync(
            registerBudgets: false, "budget_fat", new Dictionary<string, object?> { ["size"] = 64 * 1024 });

        Assert.NotEqual(true, result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.Equal(64 * 1024, text.Text.Length);
        Assert.DoesNotContain(logs, e => e.Level == LogLevel.Warning);
    }
}
