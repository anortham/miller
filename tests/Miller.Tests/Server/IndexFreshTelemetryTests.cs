using System.ComponentModel;
using System.IO.Pipelines;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Miller.Indexing;
using Miller.Server.Hosting;
using Miller.Server.Telemetry;
using Miller.Tests.Indexing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// A telemetry-pin probe tool that does nothing but return a constant — used to drive the central filter so we
/// can assert it stamps <c>index_fresh</c> from a registered <see cref="IndexFreshProbe"/>.
/// </summary>
[McpServerToolType]
public sealed class FreshPinTool
{
    [McpServerTool(Name = "fresh_pin"), Description("Returns ok (index_fresh telemetry pin).")]
    public string Ping() => "ok";
}

/// <summary>
/// Pins the M3 <c>index_fresh</c> wiring (decision-8, implementation-order step 10) THROUGH the real SDK
/// pipeline + the production <see cref="TelemetryCallToolFilter"/>: when an <see cref="IndexFreshProbe"/> is
/// registered, the filter stamps the computed boolean on the persisted row; the existing M2 pin proves the
/// complementary case (no probe registered → NULL). This drives a real in-process MCP server (crossed pipes),
/// not the probe in isolation, so it catches a filter that forgets to resolve/apply the probe. Fast → default
/// suite, not Scale.
/// </summary>
public sealed class IndexFreshTelemetryTests : IDisposable
{
    private readonly string _dir;
    private readonly string _telemetryDb;

    public IndexFreshTelemetryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-freshpin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _telemetryDb = Path.Combine(_dir, "telemetry.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Theory]
    // built==latest && queueEmpty -> index_fresh=1; a writer-ahead revision -> index_fresh=0.
    [InlineData(5L, 5L, true, 1)]
    [InlineData(5L, 7L, true, 0)]
    [InlineData(5L, 5L, false, 0)]
    public async Task CentralFilter_StampsIndexFresh_FromTheRegisteredProbe(
        long built, long latest, bool queueEmpty, int expectedFresh)
    {
        var ct = TestContext.Current.CancellationToken;
        using var fx = JulieDbFixture.CreateDefault();
        var holder = new IndexHolder(MillerRepositoryIndex.Build(SqliteSymbolReader.Read(fx.DbPath)), built);
        var probe = new IndexFreshProbe(holder, latestRevision: () => latest, queueEmpty: () => queueEmpty);
        using var ledger = TelemetryLedger.Open(_telemetryDb, workspaceId: "fresh-ws");

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(ledger);
        services.AddSingleton(probe); // THE wiring under test
        services
            .AddMcpServer(o => { o.ServerInfo = new() { Name = "freshpin", Version = "0" }; })
            .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream())
            .WithTools<FreshPinTool>()
            .WithRequestFilters(f => f.AddCallToolFilter(TelemetryCallToolFilter.Create()));

        await using var provider = services.BuildServiceProvider();
        var server = provider.GetRequiredService<McpServer>();
        var serverTask = server.RunAsync(ct);

        var clientTransport = new StreamClientTransport(
            clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream(), LoggerFactory.Create(_ => { }));
        await using var client = await McpClient.CreateAsync(clientTransport, cancellationToken: ct);

        await client.CallToolAsync("fresh_pin", new Dictionary<string, object?>()!, cancellationToken: ct);

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
        cmd.CommandText = "SELECT index_fresh FROM tool_telemetry WHERE tool = 'fresh_pin';";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read(), "the filter did not record a fresh_pin row");
        Assert.False(r.IsDBNull(0), "index_fresh must be populated (not NULL) when a probe is registered");
        Assert.Equal(expectedFresh, r.GetInt32(0));
    }
}
