using System.ComponentModel;
using System.IO.Pipelines;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Miller.Server.Telemetry;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// A reflection-discoverable tool whose body emits one Serilog line on the call's async flow, so the test can
/// observe the correlation id (<c>cid</c>) that <see cref="TelemetryCallToolFilter"/> pushed onto
/// <see cref="Serilog.Context.LogContext"/> for the duration of the inner handler.
/// </summary>
[McpServerToolType]
public static class CorrelationProbeTool
{
    /// <summary>Logs one line (carrying the ambient cid) and returns a tiny payload.</summary>
    [McpServerTool(Name = "cid_probe"), Description("Emits a log line so the ambient correlation id can be observed.")]
    public static string Probe()
    {
        Log.Information("cid probe ran");
        return "ok";
    }
}

/// <summary>
/// Pins M8 decision-2 (correlation id): the ONE central <see cref="TelemetryCallToolFilter"/> generates a single
/// id per <c>tools/call</c>, pushes it onto Serilog's <see cref="Serilog.Context.LogContext"/> as <c>cid</c> (so
/// every log line on the call's async flow carries it), AND reuses that exact id as the persisted telemetry row
/// <c>id</c>. The same id ties the log lines to the ledger row. Distinct calls get distinct ids, and a direct
/// <see cref="TelemetryLedger.Measure"/> WITHOUT a correlation id still gets a valid unique self-generated row id
/// (the fallback for direct callers/tests is intact). In-process + fast → default suite.
/// </summary>
public sealed class CorrelationFilterTests
{
    /// <summary>A Serilog sink that captures emitted events so the test can read the enriched <c>cid</c>.</summary>
    private sealed class CapturingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = new();

        public void Emit(LogEvent logEvent)
        {
            lock (_events)
                _events.Add(logEvent);
        }

        /// <summary>The scalar string value of property <paramref name="name"/> on the most recent event, or null.</summary>
        public string? LastPropertyValue(string name)
        {
            lock (_events)
            {
                for (int i = _events.Count - 1; i >= 0; i--)
                {
                    if (_events[i].Properties.TryGetValue(name, out var value)
                        && value is ScalarValue { Value: string text })
                    {
                        return text;
                    }
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Drive one real <c>tools/call</c> for <paramref name="toolName"/> through the central filter over a temp-file
    /// ledger, with a Serilog logger that captures events (FromLogContext) for the duration of the call. Returns
    /// the (single) persisted row id and the captured sink so the caller can compare the row id to the logged cid.
    /// </summary>
    private static async Task<(string rowId, CapturingSink sink)> CallThroughFilterAsync(string toolName)
    {
        var ct = TestContext.Current.CancellationToken;

        // The central filter short-circuits when no TelemetryLedger is registered, so a real ledger over a temp
        // DB is required to even reach the cid path. Owned + disposed within this call.
        string dir = Path.Combine(Path.GetTempPath(), "miller-cidfilter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string dbPath = Path.Combine(dir, "telemetry.db");
        using var ledger = TelemetryLedger.Open(dbPath, workspaceId: "cid-ws");

        var sink = new CapturingSink();
        var previousLogger = Log.Logger;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.FromLogContext()
            .WriteTo.Sink(sink)
            .CreateLogger();

        try
        {
            var clientToServer = new Pipe();
            var serverToClient = new Pipe();

            var services = new ServiceCollection();
            services.AddSingleton(ledger);
            services
                .AddMcpServer(o => { o.ServerInfo = new() { Name = "cid", Version = "0" }; })
                .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream())
                .WithToolsFromAssembly(typeof(CorrelationProbeTool).Assembly)
                .WithRequestFilters(f => f.AddCallToolFilter(TelemetryCallToolFilter.Create()));

            await using var provider = services.BuildServiceProvider();
            var server = provider.GetRequiredService<McpServer>();
            var serverTask = server.RunAsync(ct);

            var clientTransport = new StreamClientTransport(
                clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream());
            await using var client = await McpClient.CreateAsync(clientTransport, cancellationToken: ct);

            _ = await client.CallToolAsync(toolName, new Dictionary<string, object?>(), cancellationToken: ct);

            await client.DisposeAsync();
            await clientToServer.Writer.CompleteAsync();
            await serverToClient.Writer.CompleteAsync();
            try { await serverTask.WaitAsync(TimeSpan.FromSeconds(5), ct); } catch (Exception) { }
        }
        finally
        {
            (Log.Logger as IDisposable)?.Dispose();
            Log.Logger = previousLogger;
        }

        string rowId = ReadSingleRowId(dbPath);

        ledger.Dispose();
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }

        return (rowId, sink);
    }

    private static string ReadSingleRowId(string dbPath)
    {
        using var c = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }
                .ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id FROM tool_telemetry;";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        string id = r.GetString(0);
        Assert.False(r.Read()); // exactly one row
        return id;
    }

    [Fact]
    public async Task ToolCall_PersistedRowId_EqualsTheCidCarriedByItsLogLines()
    {
        var (rowId, sink) = await CallThroughFilterAsync("cid_probe");

        string? loggedCid = sink.LastPropertyValue("cid");

        Assert.False(string.IsNullOrEmpty(loggedCid));
        Assert.Equal(loggedCid, rowId);
    }

    [Fact]
    public async Task TwoToolCalls_GetDistinctCorrelationIds()
    {
        var (firstId, _) = await CallThroughFilterAsync("cid_probe");
        var (secondId, _) = await CallThroughFilterAsync("cid_probe");

        Assert.False(string.IsNullOrEmpty(firstId));
        Assert.False(string.IsNullOrEmpty(secondId));
        Assert.NotEqual(firstId, secondId);
    }

    [Fact]
    public void Measure_WithoutCorrelationId_StillGetsAValidUniqueRowId()
    {
        // Direct ledger callers (no filter, no supplied cid) keep the self-generate fallback.
        string dir = Path.Combine(Path.GetTempPath(), "miller-cidfallback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string dbPath = Path.Combine(dir, "telemetry.db");

        try
        {
            using (var ledger = TelemetryLedger.Open(dbPath, workspaceId: "cid-ws"))
            {
                using (var a = ledger.Measure("direct-a", op: null))
                    a.Outcome = TelemetryOutcome.Ok;
                using (var b = ledger.Measure("direct-b", op: null))
                    b.Outcome = TelemetryOutcome.Ok;
            }

            var ids = ReadAllRowIds(dbPath);
            Assert.Equal(2, ids.Count);
            Assert.All(ids, id => Assert.False(string.IsNullOrWhiteSpace(id)));
            Assert.Equal(2, ids.Distinct().Count());
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private static List<string> ReadAllRowIds(string dbPath)
    {
        using var c = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }
                .ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id FROM tool_telemetry;";
        using var r = cmd.ExecuteReader();
        var ids = new List<string>();
        while (r.Read())
            ids.Add(r.GetString(0));
        return ids;
    }
}
