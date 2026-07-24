using System.ComponentModel;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Miller.Indexing;
using Miller.Server.Telemetry;
using Miller.Server.Tools;
using Miller.Server.Workspaces;
using Miller.Tests.Indexing;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// A trivial reflection-discoverable tool that exists ONLY for the decision-1 pin. It is discovered via
/// <c>WithToolsFromAssembly(this assembly)</c> to prove the central filter also wraps SDK reflection-discovered
/// tools — not just production's explicitly registered tools or a not-found fallback.
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
                scope.SetError(ex);
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

    /// <summary>
    /// Throws past any internal catch — the filter must RETHROW this (SDK redaction path), not shape it into
    /// a friendly missing-parameter result. Pin for the non-marshalling branch of the central catch.
    /// </summary>
    [McpServerTool(Name = "pin_unhandled"), Description("Throws an unhandled exception (rethrow-path probe).")]
    public static string Unhandled(string who) => throw new InvalidOperationException($"kaboom for {who}");

    [McpServerTool(Name = "pin_typed_hard"), Description("Returns one typed hard diagnostic.")]
    public static string TypedHard(string format = "compact") =>
        ToolDiagnosticRenderer.Render(
            "pin_typed_hard",
            ToolDiagnostic.Corruption("artifact_corrupt", "artifact is corrupt"),
            string.Equals(format, "json", StringComparison.OrdinalIgnoreCase),
            TelemetryContext.Current);

    [McpServerTool(Name = "pin_typed_empty"), Description("Returns one typed expected-empty diagnostic.")]
    public static string TypedEmpty(string format = "compact") =>
        ToolDiagnosticRenderer.Render(
            "pin_typed_empty",
            ToolDiagnostic.ExpectedEmpty("no_matches", "no matches"),
            string.Equals(format, "json", StringComparison.OrdinalIgnoreCase),
            TelemetryContext.Current);

    [McpServerTool(Name = "pin_diagnostic_text"), Description("Returns diagnostic-like source text.")]
    public static string DiagnosticText() =>
        "example source:\ndiagnostic_class=corruption\nnot a tool diagnostic";

    [McpServerTool(Name = "pin_workspace_override"), Description("Sets target workspace telemetry fields.")]
    public static string WorkspaceOverride(string workspaceId, string workspaceRoot)
    {
        var scope = TelemetryContext.Current;
        if (scope is not null)
        {
            scope.SetWorkspace(workspaceId, workspaceRoot);
            scope.SetTarget("workspace override query");
        }
        return "workspace override recorded";
    }
}

/// <summary>
/// THE decision-1 pin (m2-design L20-26, L221-225): the ONE central <c>CallToolFilter</c> must fire for a
/// reflection-discovered (<c>WithToolsFromAssembly</c>) tool. This stands up a real in-process MCP server
/// (crossed-pipe stream transport) wired with <c>WithToolsFromAssembly</c> + the telemetry
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

    private static string Sha256Hex(string raw) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

    [Fact]
    public async Task CentralFilter_Fires_ForReflectionDiscoveredTool_AndRecordsARow()
    {
        var ct = TestContext.Current.CancellationToken;
        string currentRoot = Path.Combine(_dir, "current-workspace");
        using var ledger = TelemetryLedger.Open(_telemetryDb, workspaceId: "pin-ws", currentRoot);

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
        cmd.CommandText =
            "SELECT tool, outcome, est_tokens, duration_ms, workspace_id, workspace_root FROM tool_telemetry;";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read(), "the central CallToolFilter did not record a telemetry row");
        Assert.Equal("pin_greet", reader.GetString(0));
        Assert.Equal("ok", reader.GetString(1));
        Assert.True(reader.GetInt64(2) > 0, "est_tokens should reflect the returned bytes");
        Assert.True(reader.GetInt64(3) >= 0);
        Assert.Equal("pin-ws", reader.GetString(4));
        Assert.Equal(currentRoot, reader.GetString(5));
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

        var typedHard = await client.CallToolAsync(
            "pin_typed_hard",
            new Dictionary<string, object?> { ["format"] = "json" }!,
            cancellationToken: ct);
        Assert.True(typedHard.IsError);
        using (JsonDocument typedJson = JsonDocument.Parse(
            Assert.IsType<TextContentBlock>(Assert.Single(typedHard.Content)).Text))
        {
            Assert.Equal(
                "artifact_corrupt",
                typedJson.RootElement.GetProperty("diagnostic").GetProperty("code").GetString());
        }

        var typedEmpty = await client.CallToolAsync(
            "pin_typed_empty",
            new Dictionary<string, object?> { ["format"] = "json" }!,
            cancellationToken: ct);
        Assert.NotEqual(true, typedEmpty.IsError);
        using (JsonDocument typedEmptyJson = JsonDocument.Parse(
            Assert.IsType<TextContentBlock>(Assert.Single(typedEmpty.Content)).Text))
        {
            Assert.Equal(
                "no_matches",
                typedEmptyJson.RootElement.GetProperty("diagnostic").GetProperty("code").GetString());
        }

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
        cmd.CommandText =
            "SELECT tool, outcome, error_kind, error_message, error_detail, index_fresh, metadata_json " +
            "FROM tool_telemetry ORDER BY tool;";
        var rows = new List<(
            string tool,
            string outcome,
            string? errorKind,
            string? errorMessage,
            string? errorDetail,
            bool indexFreshIsNull,
            string metadataJson)>();
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                rows.Add((
                    r.GetString(0),
                    r.GetString(1),
                    r.IsDBNull(2) ? null : r.GetString(2),
                    r.IsDBNull(3) ? null : r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4),
                    r.IsDBNull(5),
                    r.GetString(6)));

        var boomRow = Assert.Single(rows, x => x.tool == "pin_boom");
        Assert.Equal("error", boomRow.outcome);                       // NOT 'ok' — the fix
        Assert.Equal("InvalidOperationException", boomRow.errorKind); // error_kind survives
        Assert.Equal("kaboom", boomRow.errorMessage);
        Assert.Contains("System.InvalidOperationException: kaboom", boomRow.errorDetail);
        Assert.Contains(nameof(PinProbeTool.Boom), boomRow.errorDetail);
        Assert.True(boomRow.indexFreshIsNull, "index_fresh must be NULL (unknown) for M2, not a fabricated 1");

        var emptyRow = Assert.Single(rows, x => x.tool == "pin_empty");
        Assert.Equal("empty", emptyRow.outcome);
        var typedHardRow = Assert.Single(rows, x => x.tool == "pin_typed_hard");
        Assert.Equal("error", typedHardRow.outcome);
        using (JsonDocument metadata = JsonDocument.Parse(typedHardRow.metadataJson))
        {
            Assert.Equal("artifact_corrupt", metadata.RootElement.GetProperty("diagnostic_code").GetString());
            Assert.Equal("corruption", metadata.RootElement.GetProperty("diagnostic_class").GetString());
        }
        var typedEmptyRow = Assert.Single(rows, x => x.tool == "pin_typed_empty");
        Assert.Equal("empty", typedEmptyRow.outcome);
        using (JsonDocument metadata = JsonDocument.Parse(typedEmptyRow.metadataJson))
        {
            Assert.Equal("no_matches", metadata.RootElement.GetProperty("diagnostic_code").GetString());
            Assert.Equal("expected_empty", metadata.RootElement.GetProperty("diagnostic_class").GetString());
        }
    }

    [Fact]
    public async Task CentralFilter_TypedHardDiagnostic_UsesErrorChannelWithoutTelemetryLedger()
    {
        var ct = TestContext.Current.CancellationToken;
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var services = new ServiceCollection();
        services.AddLogging();
        services
            .AddMcpServer(o => { o.ServerInfo = new() { Name = "pin", Version = "0" }; })
            .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream())
            .WithToolsFromAssembly(typeof(PinProbeTool).Assembly)
            .WithRequestFilters(f => f.AddCallToolFilter(TelemetryCallToolFilter.Create()));

        await using var provider = services.BuildServiceProvider();
        var server = provider.GetRequiredService<McpServer>();
        var serverTask = server.RunAsync(ct);
        var transport = new StreamClientTransport(
            clientToServer.Writer.AsStream(),
            serverToClient.Reader.AsStream(),
            NullLoggerFactory.Instance);
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: ct);

        var hard = await client.CallToolAsync(
            "pin_typed_hard",
            new Dictionary<string, object?> { ["format"] = "json" }!,
            cancellationToken: ct);
        var empty = await client.CallToolAsync(
            "pin_typed_empty",
            new Dictionary<string, object?> { ["format"] = "json" }!,
            cancellationToken: ct);
        var sourceText = await client.CallToolAsync(
            "pin_diagnostic_text",
            cancellationToken: ct);

        Assert.True(hard.IsError);
        Assert.NotEqual(true, empty.IsError);
        Assert.NotEqual(true, sourceText.IsError);

        await client.DisposeAsync();
        await clientToServer.Writer.CompleteAsync();
        await serverToClient.Writer.CompleteAsync();
        try { await serverTask.WaitAsync(TimeSpan.FromSeconds(5), ct); } catch (Exception) { }
    }

    [Fact]
    public async Task CentralFilter_MissingRequiredParam_OnInspect_ReturnsFriendlyToolError_AndRecordsErrorRow()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ledger = TelemetryLedger.Open(_telemetryDb, workspaceId: "pin-ws");

        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            Array.Empty<JulieDbFixture.SymbolRow>(),
            workspaceId: "pin-ws");
        var index = MillerRepositoryIndex.Build(SqliteSymbolReader.Read(fx.DbPath));
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(index, fx.DbPath, "pin-ws", _dir));

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(ledger);
        services.AddSingleton<IWorkspaceIndexProvider>(provider);
        services.AddSingleton<IWorkspaceSearchProvider>(provider);
        services.AddSingleton<IWorkspaceSymbolReadProvider>(provider);
        services
            .AddMcpServer(o => { o.ServerInfo = new() { Name = "pin", Version = "0" }; })
            .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream())
            .WithTools<InspectTool>()
            .WithRequestFilters(f => f.AddCallToolFilter(TelemetryCallToolFilter.Create()));

        await using var provider2 = services.BuildServiceProvider();
        var server = provider2.GetRequiredService<McpServer>();
        var serverTask = server.RunAsync(ct);

        var clientTransport = new StreamClientTransport(
            clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream(), NullLoggerFactory.Instance);
        await using var client = await McpClient.CreateAsync(clientTransport, cancellationToken: ct);

        var result = await client.CallToolAsync(
            "inspect", new Dictionary<string, object?>(), cancellationToken: ct);

        Assert.Equal(true, result.IsError);
        string text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Contains("'target'", text);
        Assert.Contains("inspect", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Example:", text);

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
        cmd.CommandText = "SELECT tool, outcome, error_kind, error_message, error_detail, metadata_json FROM tool_telemetry;";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read(), "the central CallToolFilter did not record a telemetry row");
        Assert.Equal("inspect", reader.GetString(0));
        Assert.Equal("error", reader.GetString(1));
        Assert.Equal("ArgumentException", reader.GetString(2));
        Assert.Contains("missing a value for the required parameter 'target'", reader.GetString(3));
        Assert.Contains("System.ArgumentException", reader.GetString(4));
        Assert.Contains("required parameter 'target'", reader.GetString(4));
        using (JsonDocument metadata = JsonDocument.Parse(reader.GetString(5)))
            Assert.Equal("bad_input", metadata.RootElement.GetProperty("error_category").GetString());
        Assert.False(reader.Read(), "exactly one row expected (the filter must fire once per call)");
    }

    [Fact]
    public async Task CentralFilter_NonMarshallingException_StillRethrows_NoFriendlyHint_ErrorRowRecorded()
    {
        // The friendly catch is ONLY for the marshaller's missing-required-parameter shape. Any other
        // exception keeps the existing behavior: record an error row, rethrow for SDK redaction.
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

        // pin_unhandled throws InvalidOperationException past the filter. Depending on the SDK layer the
        // client sees either an error result or a protocol exception — either is fine, as long as it is NOT
        // shaped into the friendly missing-parameter hint.
        string? clientVisibleText = null;
        try
        {
            var result = await client.CallToolAsync(
                "pin_unhandled", new Dictionary<string, object?> { ["who"] = "x" }!, cancellationToken: ct);
            Assert.Equal(true, result.IsError);
            clientVisibleText = result.Content is [TextContentBlock t] ? t.Text : null;
        }
        catch (McpException ex)
        {
            clientVisibleText = ex.Message;
        }

        if (clientVisibleText is not null)
        {
            Assert.DoesNotContain("missing required parameter", clientVisibleText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Example:", clientVisibleText);
        }

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
        cmd.CommandText =
            "SELECT tool, outcome, error_kind, error_message, error_detail " +
            "FROM tool_telemetry WHERE tool = 'pin_unhandled';";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read(), "the rethrow path must still record a telemetry error row");
        Assert.Equal("error", reader.GetString(1));
        Assert.Equal("InvalidOperationException", reader.GetString(2));
        Assert.Equal("kaboom for x", reader.GetString(3));
        Assert.Contains("System.InvalidOperationException: kaboom for x", reader.GetString(4));
        Assert.Contains(nameof(PinProbeTool.Unhandled), reader.GetString(4));
    }

    [Fact]
    public async Task CentralFilter_PersistsWorkspaceOverride_FromToolScope()
    {
        var ct = TestContext.Current.CancellationToken;
        string currentRoot = Path.Combine(_dir, "current-workspace");
        string targetRoot = Path.Combine(_dir, "target-workspace");
        using var ledger = TelemetryLedger.Open(_telemetryDb, workspaceId: "current-ws", currentRoot);

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
            "pin_workspace_override",
            new Dictionary<string, object?> { ["workspaceId"] = "target-ws", ["workspaceRoot"] = targetRoot }!,
            cancellationToken: ct);
        Assert.Equal("workspace override recorded", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);

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
        cmd.CommandText = "SELECT workspace_id, workspace_root, target_hash FROM tool_telemetry;";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read(), "the central CallToolFilter did not record a telemetry row");
        Assert.Equal("target-ws", reader.GetString(0));
        Assert.Equal(targetRoot, reader.GetString(1));
        string targetHash = reader.GetString(2);
        Assert.Equal(Sha256Hex("workspace override query"), targetHash);
        Assert.DoesNotContain("workspace override query", targetHash, StringComparison.OrdinalIgnoreCase);
        Assert.False(reader.Read(), "exactly one row expected (the filter must fire once per call)");
    }
}

file static class NullLoggerFactory
{
    public static ILoggerFactory Instance { get; } = LoggerFactory.Create(_ => { });
}
