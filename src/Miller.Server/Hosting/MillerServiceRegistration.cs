using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Miller.Indexing;
using Miller.Server.Resolution;
using Miller.Server.Telemetry;
using Miller.Server.Workspaces;

namespace Miller.Server.Hosting;

/// <summary>
/// The single source of truth for Miller's host service graph (everything EXCEPT the MCP transport, which
/// <c>Program.cs</c> wires last). Extracted from the top-level program so the startup graph is unit-testable —
/// see <c>HostStartupRegistrationTests</c>, which builds a provider from this and resolves the hosted-service set
/// to prove no hosted-service constructor touches an <see cref="IndexBootstrapService"/> getter before bootstrap.
///
/// LIFECYCLE CONTRACT (load-bearing): the .NET Generic Host CONSTRUCTS every <see cref="IHostedService"/> up
/// front, then calls <c>StartAsync</c> on each in registration order. Registration order therefore orders
/// <c>StartAsync</c>, NOT construction. The bootstrap getters (Holder/Resolver/Workspace/Ledger) throw until
/// <see cref="IndexBootstrapService.StartAsync"/> has populated them, so NO hosted-service CONSTRUCTOR may read
/// them (directly or via an injected singleton built from them) — read them lazily inside <c>ExecuteAsync</c>.
/// Tools are built per-call (well after StartAsync), so the holder-backed singletons below are safe for them.
/// </summary>
public static class MillerServiceRegistration
{
    public static IServiceCollection AddMillerServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The bootstrap holds the built singletons. Registered as a singleton AND as the FIRST hosted service so
        // its StartAsync (index build + ledger open + holder seed + canonical-root resolve) completes before any
        // OTHER hosted service's StartAsync (the indexer, the freshness poller, the MCP transport) runs.
        services.AddSingleton<IndexBootstrapService>();
        services.AddHostedService(sp => sp.GetRequiredService<IndexBootstrapService>());

        // Holder-backed singletons resolved from the bootstrap. These factories read the bootstrap getters, so
        // they MUST only be resolved after StartAsync — i.e. by per-call tools and the lazily-resolved probe
        // below, NEVER by a hosted-service constructor (see the lifecycle contract above). Tools read
        // holder.Current per call so a freshness Swap is seen (M3 step 10).
        services.AddSingleton(sp => sp.GetRequiredService<IndexBootstrapService>().Holder);
        services.AddSingleton(sp => sp.GetRequiredService<IndexBootstrapService>().Resolver);
        services.AddSingleton(sp => sp.GetRequiredService<IndexBootstrapService>().Workspace);
        services.AddSingleton(sp => sp.GetRequiredService<IndexBootstrapService>().Ledger);

        // M3 hosted services (registered AFTER the bootstrap so its StartAsync seeds the holder + canonical root
        // first). BOTH take only the bootstrap and read its getters lazily inside ExecuteAsync — never at
        // construction (the lifecycle contract). Registered as singletons too so index_fresh reads their state.
        services.AddSingleton<FreshnessService>();
        services.AddSingleton<IndexerService>();
        services.AddHostedService(sp => sp.GetRequiredService<FreshnessService>());
        services.AddHostedService(sp => sp.GetRequiredService<IndexerService>());

        // The index_fresh probe (decision-8) the telemetry filter reads per call: built revision vs. the freshness
        // service's last-observed revision AND the indexer's queue-empty state. Resolved lazily (per call / by the
        // filter), well after StartAsync, so its GetRequiredService<IndexHolder>() is safe. Cheap — no SQLite on
        // the tool hot path.
        services.AddSingleton(sp =>
        {
            var holder = sp.GetRequiredService<IndexHolder>();
            var freshness = sp.GetRequiredService<FreshnessService>();
            var indexer = sp.GetRequiredService<IndexerService>();
            return new IndexFreshProbe(
                holder,
                latestRevision: () => freshness.LatestObservedRevision,
                queueEmpty: () => indexer.QueueEmpty);
        });

        // M6 edit tool dependencies:
        //  - EditApplier: the atomic apply transaction (writer-lock + TOCTOU + rollback). Its writer-lock seam
        //    binds to the dedicated EditWriteLock on <.miller>/edit.lock (separate from the indexer lock so an
        //    edit never deadlocks against the running indexer leader — see EditWriteLock).
        //  - IEditWriteThrough: post-apply convergence — the leader reindexes inline, else the watcher reconciles.
        // EditTool itself is explicitly registered in Program.cs for Native AOT; it resolves these.
        services.AddSingleton(sp =>
        {
            var workspace = sp.GetRequiredService<WorkspaceContext>();
            string millerDir = Path.GetDirectoryName(workspace.ExtractDbPath)!;
            return new EditApplier(() => EditWriteLock.TryAcquire(millerDir));
        });
        services.AddSingleton<IEditWriteThrough>(sp =>
            new LeaderWriteThrough(
                sp.GetRequiredService<IndexerService>(),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<LeaderWriteThrough>()));

        // Soft budgets (M7 decision-4): per-tool latency + est-token warn thresholds the central telemetry filter
        // evaluates after each call, logging a WARN per breach (warn-only — never blocks or errors the call). The
        // production defaults live on SoftBudgets.Default.
        services.AddSingleton(SoftBudgets.Default);

        // M7 workspace tool (decision-1): the admin/index-lifecycle tool. Explicitly registered in Program.cs for
        // Native AOT; it resolves the holder/workspace/indexer/freshness/probe/ledger singletons above plus a
        // JulieExtractRunner for the open(path) prime scan. The runner is located
        // under the SAME tools root the bootstrap + indexer use (the pinned julie-extract ships there, NOT the repo
        // cwd), so a missing binary fails loudly via JulieExtractRunner.Locate's restore-script message rather than
        // silently degrading.
        services.AddSingleton(sp =>
            JulieExtractRunner.Locate(sp.GetRequiredService<WorkspaceContext>().ToolsRoot));

        // Task 5 cross-workspace read seam. The registry is machine-global (<home>/.miller/workspaces.db); target
        // indexes remain local to their owning workspace and are loaded through WorkspaceIndexProvider only.
        services.AddSingleton(sp =>
            WorkspaceRegistry.Open(sp.GetRequiredService<WorkspaceContext>().RegistryDbPath));
        // Search sidecar flag — default ON (the Phase-5 recall eval cleared it: interior recall up, zero word-arm
        // regression, ranking parity exact). The lock-holding writer (IndexerService leader / CrossWorkspaceRefreshService)
        // converges the on-disk search.db; reads require it when enabled so missing/stale artifacts are visible.
        // Opt out with MILLER_SEARCH_SIDECAR=0 (or false/off/no) to use the in-memory search path. Registered
        // before both consumers.
        services.AddSingleton(_ => SymbolSearchSidecar.FromEnvironment());
        services.AddSingleton<ContentCorpusSidecar>();
        services.AddSingleton<ContentCorpusExternalStore>();
        services.AddSingleton<PatternFactsReader>();
        services.AddSingleton<CrossWorkspaceRefreshService>();
        services.AddSingleton<WorkspaceIndexProvider>();
        services.AddSingleton<IWorkspaceIndexProvider>(sp => sp.GetRequiredService<WorkspaceIndexProvider>());
        services.AddSingleton<IWorkspaceArtifactProvider>(sp => sp.GetRequiredService<WorkspaceIndexProvider>());
        services.AddSingleton<IWorkspaceSearchProvider>(sp => sp.GetRequiredService<WorkspaceIndexProvider>());
        services.AddSingleton<IWorkspaceContentSearchProvider>(sp => sp.GetRequiredService<WorkspaceIndexProvider>());
        services.AddSingleton<IWorkspaceRegionSearchProvider>(sp => sp.GetRequiredService<WorkspaceIndexProvider>());
        services.AddSingleton<IWorkspaceTextContentSearchProvider>(sp => sp.GetRequiredService<WorkspaceIndexProvider>());

        return services;
    }
}
