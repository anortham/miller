using System.IO.Pipelines;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Miller.Indexing;
using Miller.Server;
using Miller.Server.Resolution;
using Miller.Server.Telemetry;
using Miller.Server.Tools;
using Miller.Server.Workspaces;
using Miller.Tests;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// The M2 end-to-end Scale proof (m2-design L228-229): restore julie-extract → scan a tiny throwaway repo →
/// build the real index → drive <c>search</c> + <c>inspect</c> (summary AND full) THROUGH the SDK with the
/// production tool types + the central telemetry CallToolFilter, and assert the results are correct AND a
/// <c>tool_telemetry</c> row landed per call. This depends on the binary + a real extract, so it is
/// <c>[Trait("Category","Scale")]</c> and EXCLUDED from the default suite; it <see cref="Assert.Skip"/>s if
/// <c>.tools/julie-extract</c> is absent rather than failing.
/// </summary>
[Trait("Category", "Scale")]
public sealed class LiveSearchInspectTests
{
    [Fact]
    public async Task Live_ScanBuildServeSearchAndInspect_WithTelemetryRows()
    {
        var ct = TestContext.Current.CancellationToken;
        string binary = ScaleTestSupport.RequireJulieServer();

        string work = Path.Combine(Path.GetTempPath(), "miller-m2live-" + Guid.NewGuid().ToString("N"));
        string repo = Path.Combine(work, "repo");
        string millerDir = Path.Combine(work, ".miller");
        string db = Path.Combine(millerDir, "symbols.db");
        string telemetryDb = Path.Combine(millerDir, "telemetry.db");
        Directory.CreateDirectory(repo);
        Directory.CreateDirectory(millerDir);

        try
        {
            File.WriteAllText(Path.Combine(repo, "orders.cs"), """
                namespace Shop;

                /// <summary>Processes orders.</summary>
                public sealed class OrderService
                {
                    public int Process(int orderId)
                    {
                        return Validate(orderId);
                    }

                    private int Validate(int orderId) => orderId;
                }
                """);

            // --- scan with the real binary into the Miller-owned .miller/symbols.db ---
            var runner = new JulieExtractRunner(binary!);
            var report = runner.Scan(repo, db, force: true);
            Assert.NotEqual("failed", report.Status);
            Assert.True(report.SymbolsExtracted > 0);

            // --- read → build → holder → resolver (M3: tools depend on the live IndexHolder) ---
            var index = MillerRepositoryIndex.Build(SqliteSymbolReader.Read(db));
            var holder = new IndexHolder(index, builtRevision: 0);
            var resolver = new SmartTargetResolver(holder);
            // v1 stores no workspace_id; the stable id is derived from the canonical root (reconciliation #17).
            string workspaceId = WorkspaceId.FromCanonicalRoot(PathCanonicalizer.CanonicalizeRoot(repo));

            // --- pure-core sanity: search + inspect return correct results ---
            string searchOut = SearchTool.Run(index, "OrderService", SearchToolMode.Auto, limit: 10,
                excludeTests: null, json: false, out int searchCount);
            Assert.True(searchCount >= 1);
            Assert.Contains("OrderService", searchOut);

            string inspectSummary = InspectTool.Run(index, resolver, db, repo, "OrderService",
                depth: "summary", kind: null, scope: null, limit: 50, json: false, out _);
            Assert.Contains("OrderService", inspectSummary);

            string inspectFull = InspectTool.Run(index, resolver, db, repo, "Process",
                depth: "full", kind: null, scope: null, limit: 50, json: false, out _);
            Assert.Contains("Process", inspectFull);
            // full depth on Process should surface its body re-sourced from disk (it calls Validate).
            Assert.Contains("Validate", inspectFull);

            // --- end-to-end through the SDK with the production filter: rows must land per call ---
            using var ledger = TelemetryLedger.Open(telemetryDb, workspaceId);
            var workspace = WorkspaceContext.Create(work, ScaleTestSupport.RepoRoot()) with
            {
                WorkspaceId = workspaceId,
            };
            // Point the workspace's ExtractDbPath at our scanned DB so InspectTool reads the right file.
            workspace = workspace with { ExtractDbPath = db };

            var clientToServer = new Pipe();
            var serverToClient = new Pipe();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(ledger);
            var workspaceProvider = new HolderWorkspaceIndexProvider(holder, db, workspaceId, repo);
            services.AddSingleton<IWorkspaceIndexProvider>(workspaceProvider);
            services.AddSingleton<IWorkspaceSearchProvider>(workspaceProvider);
            services.AddSingleton<IWorkspaceContentSearchProvider>(workspaceProvider);
            services.AddSingleton(workspace);
            services
                .AddMcpServer(o => { o.ServerInfo = new() { Name = "miller-live", Version = "0" }; })
                .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream())
                .WithTools<SearchTool>()
                .WithTools<InspectTool>()
                .WithRequestFilters(f => f.AddCallToolFilter(TelemetryCallToolFilter.Create()));

            await using var provider = services.BuildServiceProvider();
            var server = provider.GetRequiredService<McpServer>();
            var serverTask = server.RunAsync(ct);

            var clientTransport = new StreamClientTransport(
                clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream(), LoggerFactory.Create(_ => { }));
            await using var client = await McpClient.CreateAsync(clientTransport, cancellationToken: ct);

            var searchResult = await client.CallToolAsync(
                "search", new Dictionary<string, object?> { ["query"] = "OrderService" }!, cancellationToken: ct);
            Assert.Contains("OrderService",
                Assert.IsType<TextContentBlock>(Assert.Single(searchResult.Content)).Text);

            var inspectResult = await client.CallToolAsync(
                "inspect", new Dictionary<string, object?> { ["target"] = "OrderService", ["depth"] = "full" }!,
                cancellationToken: ct);
            Assert.Contains("OrderService",
                Assert.IsType<TextContentBlock>(Assert.Single(inspectResult.Content)).Text);

            await client.DisposeAsync();
            await clientToServer.Writer.CompleteAsync();
            await serverToClient.Writer.CompleteAsync();
            try { await serverTask.WaitAsync(TimeSpan.FromSeconds(5), ct); } catch (Exception) { }

            // --- telemetry: one row per tool call, hashed target, correct outcome ---
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = telemetryDb, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
            }.ToString());
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT tool, outcome, target_hash, est_tokens FROM tool_telemetry ORDER BY ts;";
            var rows = new List<(string tool, string outcome, string? hash, long est)>();
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    rows.Add((r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                        r.IsDBNull(3) ? 0 : r.GetInt64(3)));

            Assert.Equal(2, rows.Count);
            Assert.Contains(rows, x => x.tool == "search" && x.outcome == "ok");
            Assert.Contains(rows, x => x.tool == "inspect" && x.outcome == "ok");
            Assert.All(rows, x =>
            {
                Assert.NotNull(x.hash);            // target hashed, never raw
                Assert.Equal(64, x.hash!.Length);  // SHA256 hex
                Assert.True(x.est > 0);            // est_tokens reflects returned bytes
            });
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }
}
