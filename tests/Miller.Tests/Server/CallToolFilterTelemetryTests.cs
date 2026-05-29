using System.ComponentModel;
using System.IO.Pipelines;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Miller.Server.Telemetry;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// A trivial reflection-discoverable tool that exists ONLY for the decision-1 pin. It is discovered via
/// <c>WithToolsFromAssembly(this assembly)</c> exactly like Miller's real tools, proving the central filter
/// wraps reflection-discovered tools — not just a not-found fallback.
/// </summary>
[McpServerToolType]
public static class PinProbeTool
{
    [McpServerTool(Name = "pin_greet"), Description("Returns a greeting (telemetry-pin probe).")]
    public static string Greet(string who) => $"hello {who}";

    /// <summary>
    /// Mirrors the real tools' catch pattern (SearchTool/InspectTool): an internal exception is caught, the
    /// scope's Outcome is set to Error + ErrorKind, and a CLEAN string is returned (so the SDK result is NOT an
    /// error result and IsError is false). The filter must persist 'error', not overwrite it back to 'ok'.
    /// </summary>
    [McpServerTool(Name = "pin_boom"), Description("Sets Outcome=Error in its own catch, returns a clean string.")]
    public static string Boom(string who)
    {
        var scope = TelemetryContext.Current;
        try
        {
            throw new InvalidOperationException("kaboom");
        }
        catch (Exception ex)
        {
            if (scope is not null)
            {
                scope.Outcome = TelemetryOutcome.Error;
                scope.ErrorKind = ex.GetType().Name;
            }
            return $"pin_boom failed: {ex.Message}"; // clean string, not a thrown/error result
        }
    }

    /// <summary>Mirrors a zero-result tool path: classifies Outcome=Empty on the scope (decision: empty != ok).</summary>
    [McpServerTool(Name = "pin_empty"), Description("Sets Outcome=Empty (zero results) on the scope.")]
    public static string Empty()
    {
        var scope = TelemetryContext.Current;
        if (scope is not null)
        {
            scope.ResultCount = 0;
            scope.Outcome = TelemetryOutcome.Empty;
        }
        return "no results";
    }
}

/// <summary>
/// THE decision-1 pin (m2-design L20-26, L221-225): the ONE central <c>CallToolFilter</c> must fire for a
/// reflection-discovered (<c>WithToolsFromAssembly</c>) tool. This stands up a real in-process MCP server
/// (crossed-pipe stream transport) wired exactly as production — <c>WithToolsFromAssembly</c> + the telemetry
/// <c>CallToolFilter</c> + a real <see cref="TelemetryLedger"/> — invokes the discovered tool through the SDK
/// client, and asserts a <c>tool_telemetry</c> row was recorded for it. If THIS fails, the per-tool
/// <c>using Measure()</c> fallback is taken (documented in deviations). It passes, so the central filter
/// stands. In-process and fast → default suite, not Scale.
/// </summary>
public sealed class CallToolFilterTelemetryTests : IDisposable
{
    private readonly string _dir;
    private readonly string _telemetryDb;

    public CallToolFilterTelemetryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-filterpin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _telemetryDb = Path.Combine(_dir, "telemetry.db");
    }

    public void Dispose()
    {
        // No process-global ClearAllPools() — it would race parallel tests' live connections. The verify
        // connection below is Pooling=false, so nothing is pooled and the temp dir deletes cleanly.
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task CentralFilter_Fires_ForReflectionDiscoveredTool_AndRecordsARow()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ledger = TelemetryLedger.Open(_telemetryDb, workspaceId: "pin-ws");

        // Crossed pipes: client writes → server reads; server writes → client reads.
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(ledger);
        services
            .AddMcpServer(o => { o.ServerInfo = new() { Name = "pin", Version = "0" }; })
            .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream())
            .WithToolsFromAssembly(typeof(PinProbeTool).Assembly)
            .WithRequestFilters(f => f.AddCallToolFilter(TelemetryCallToolFilter.Create()));

        await using var provider = services.BuildServiceProvider();
        var server = provider.GetRequiredService<McpServer>();
        var serverTask = server.RunAsync(ct);

        var clientTransport = new StreamClientTransport(
            clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream(), NullLoggerFactory.Instance);
        await using var client = await McpClient.CreateAsync(clientTransport, cancellationToken: ct);

        var result = await client.CallToolAsync(
            "pin_greet", new Dictionary<string, object?> { ["who"] = "miller" }!, cancellationToken: ct);

        // The tool itself worked end-to-end through the SDK.
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.Equal("hello miller", text.Text);

        // The telemetry row is committed when the filter's scope disposes — inside the call pipeline, before
        // CallToolAsync's result returns above. So the row already exists; shutting the server down is just
        // cleanup. Complete the pipes to unblock the server loop, then best-effort await it.
        await client.DisposeAsync();
        await clientToServer.Writer.CompleteAsync();
        await serverToClient.Writer.CompleteAsync();
        try { await serverTask.WaitAsync(TimeSpan.FromSeconds(5), ct); }
        catch (Exception) { /* server loop teardown is not what this test asserts */ }

        // THE PIN: the central filter recorded exactly one telemetry row for the discovered tool.
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _telemetryDb, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
        }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT tool, outcome, est_tokens, duration_ms FROM tool_telemetry;";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read(), "the central CallToolFilter did not record a telemetry row");
        Assert.Equal("pin_greet", reader.GetString(0));
        Assert.Equal("ok", reader.GetString(1));
        Assert.True(reader.GetInt64(2) > 0, "est_tokens should reflect the returned bytes");
        Assert.True(reader.GetInt64(3) >= 0);
        Assert.False(reader.Read(), "exactly one row expected (the filter must fire once per call)");
    }

    [Fact]
    public async Task CentralFilter_PreservesToolClassifiedOutcomes_ErrorAndEmpty_NotOverwrittenToOk()
    {
        // Regression pin: a tool that catches internally sets Outcome=Error and returns a CLEAN string (so the
        // SDK result is not an error result and ResultCount stays null). The filter must NOT rewrite that Error
        // back to 'ok'. Likewise an explicit Empty must survive. Drives the real SDK pipeline + real filter.
        var ct = TestContext.Current.CancellationToken;
        using var ledger = TelemetryLedger.Open(_telemetryDb, workspaceId: "pin-ws");

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(ledger);
        services
            .AddMcpServer(o => { o.ServerInfo = new() { Name = "pin", Version = "0" }; })
            .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream())
            .WithToolsFromAssembly(typeof(PinProbeTool).Assembly)
            .WithRequestFilters(f => f.AddCallToolFilter(TelemetryCallToolFilter.Create()));

        await using var provider = services.BuildServiceProvider();
        var server = provider.GetRequiredService<McpServer>();
        var serverTask = server.RunAsync(ct);

        var clientTransport = new StreamClientTransport(
            clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream(), NullLoggerFactory.Instance);
        await using var client = await McpClient.CreateAsync(clientTransport, cancellationToken: ct);

        // pin_boom: caught internally → clean string result, scope Outcome=Error.
        var boom = await client.CallToolAsync(
            "pin_boom", new Dictionary<string, object?> { ["who"] = "x" }!, cancellationToken: ct);
        Assert.Contains("pin_boom failed",
            Assert.IsType<TextContentBlock>(Assert.Single(boom.Content)).Text);
        Assert.NotEqual(true, boom.IsError); // NOT an MCP error result — the tool returned a clean string

        // pin_empty: zero results → scope Outcome=Empty.
        await client.CallToolAsync("pin_empty", new Dictionary<string, object?>()!, cancellationToken: ct);

        await client.DisposeAsync();
        await clientToServer.Writer.CompleteAsync();
        await serverToClient.Writer.CompleteAsync();
        try { await serverTask.WaitAsync(TimeSpan.FromSeconds(5), ct); } catch (Exception) { }

        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _telemetryDb, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
        }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT tool, outcome, error_kind, index_fresh FROM tool_telemetry ORDER BY tool;";
        var rows = new List<(string tool, string outcome, string? errorKind, bool indexFreshIsNull)>();
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                rows.Add((r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3)));

        var boomRow = Assert.Single(rows, x => x.tool == "pin_boom");
        Assert.Equal("error", boomRow.outcome);                       // NOT 'ok' — the fix
        Assert.Equal("InvalidOperationException", boomRow.errorKind); // error_kind survives
        Assert.True(boomRow.indexFreshIsNull, "index_fresh must be NULL (unknown) for M2, not a fabricated 1");

        var emptyRow = Assert.Single(rows, x => x.tool == "pin_empty");
        Assert.Equal("empty", emptyRow.outcome);
    }
}

file static class NullLoggerFactory
{
    public static ILoggerFactory Instance { get; } = LoggerFactory.Create(_ => { });
}
