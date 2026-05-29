using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Miller.Indexing;
using Miller.Server.Resolution;
using Miller.Server.Telemetry;

namespace Miller.Server;

/// <summary>
/// The startup bootstrap (M2 §7). Registered as an <see cref="IHostedService"/> BEFORE the MCP host so its
/// <see cref="StartAsync"/> runs to completion — building the in-memory index and opening the telemetry
/// ledger — before <c>WithStdioServerTransport</c>'s own hosted service starts accepting <c>tools/call</c>.
/// It also holds the built singletons (index, resolver, workspace context, ledger) which the DI container
/// resolves through factory delegates; because the generic host runs hosted services in registration order,
/// the holder is populated before any tool is constructed.
///
/// Sequence: resolve the <see cref="WorkspaceContext"/> → create <c>&lt;root&gt;/.miller</c> → locate the
/// pinned julie-server (fail loudly if absent) → one-time scan ONLY if the extract DB does not already exist
/// (<see cref="File.Exists(string)"/>, not <c>FileInfo</c>) → read symbols → build the index → read the
/// workspace id → open the telemetry ledger + prune. Re-scan / watcher / incremental freshness is M3.
/// </summary>
public sealed class IndexBootstrapService : IHostedService, IDisposable
{
    private readonly ILogger<IndexBootstrapService> _logger;
    private readonly object _gate = new();

    private MillerRepositoryIndex? _index;
    private SmartTargetResolver? _resolver;
    private WorkspaceContext? _workspace;
    private TelemetryLedger? _ledger;

    public IndexBootstrapService(ILogger<IndexBootstrapService> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>The built repository index. Throws if accessed before <see cref="StartAsync"/> completes.</summary>
    public MillerRepositoryIndex Index =>
        _index ?? throw new InvalidOperationException("Index requested before bootstrap completed.");

    public SmartTargetResolver Resolver =>
        _resolver ?? throw new InvalidOperationException("Resolver requested before bootstrap completed.");

    public WorkspaceContext Workspace =>
        _workspace ?? throw new InvalidOperationException("WorkspaceContext requested before bootstrap completed.");

    public TelemetryLedger Ledger =>
        _ledger ?? throw new InvalidOperationException("TelemetryLedger requested before bootstrap completed.");

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Synchronous work (SQLite reads, a subprocess scan) wrapped in a Task — the host awaits this before
        // the transport's hosted service starts, so the index is ready when the first tool call arrives.
        Run();
        return Task.CompletedTask;
    }

    private void Run()
    {
        lock (_gate)
        {
            if (_index is not null)
                return; // idempotent

            var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            var ctx = WorkspaceContext.Create(Environment.CurrentDirectory, AppContext.BaseDirectory);

            string millerDir = Path.GetDirectoryName(ctx.ExtractDbPath)!;
            Directory.CreateDirectory(millerDir);

            // Locate the pinned julie-server under the tools root (NOT the repo cwd). Absent → fail loudly
            // (FileNotFoundException carrying the restore-script message) — Miller cannot index without it.
            var runner = JulieExtractRunner.Locate(ctx.ToolsRoot);

            // One-time initial scan ONLY if the extract DB does not yet exist. File.Exists (NOT FileInfo) per
            // the spec — a re-scan/watcher is M3.
            if (!File.Exists(ctx.ExtractDbPath))
            {
                _logger.LogInformation("No extract DB at {Db}; scanning {Root}…", ctx.ExtractDbPath, ctx.WorkspaceRoot);
                var report = runner.Scan(ctx.WorkspaceRoot, ctx.ExtractDbPath);
                _logger.LogInformation(
                    "Scan complete: {Symbols} symbols extracted.", report.SymbolsExtracted);
            }
            else
            {
                _logger.LogInformation("Reusing existing extract DB at {Db}.", ctx.ExtractDbPath);
            }

            // Read → build the in-memory index.
            var symbols = SqliteSymbolReader.Read(ctx.ExtractDbPath);
            var index = MillerRepositoryIndex.Build(symbols);

            // Resolve the workspace id (for telemetry scoping) and finalize the context.
            string? workspaceId = ExtractReader.ReadWorkspaceId(ctx.ExtractDbPath);
            var workspace = ctx with { WorkspaceId = workspaceId };

            // Open the SEPARATE, writable telemetry ledger (never the read-only extract) + prune old rows.
            var ledger = TelemetryLedger.Open(workspace.TelemetryDbPath, workspaceId);
            int pruned = ledger.Prune(retentionDays: 30);

            _index = index;
            _resolver = new SmartTargetResolver(index);
            _workspace = workspace;
            _ledger = ledger;

            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(startedAt);
            _logger.LogInformation(
                "Bootstrap ready: {Count} symbols indexed, workspace_id={Ws}, {Pruned} telemetry rows pruned, in {Ms}ms.",
                index.DocumentCount, workspaceId ?? "(unknown)", pruned, (long)elapsed.TotalMilliseconds);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => _ledger?.Dispose();
}
