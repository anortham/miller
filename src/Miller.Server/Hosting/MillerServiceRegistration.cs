using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Miller.Indexing;
using Miller.Indexing.Semantic;
using Miller.Server.Git;
using Miller.Server.Tools;
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
    /// Tools are built per-call (well after StartAsync), so holder-backed current-workspace services can resolve
    /// the latest bootstrap binding for each call.
/// </summary>
public static class MillerServiceRegistration
{
    public static IServiceCollection AddMillerServices(
        this IServiceCollection services,
        SemanticEvaluationAdapter? evaluationAdapter = null,
        SemanticMode? semanticMode = null,
        bool startIndexer = true)
    {
        ArgumentNullException.ThrowIfNull(services);
        SemanticMode activeSemanticMode = semanticMode ?? SemanticActivation.FromEnvironment();

        // Machine-wide (per-user) admission over whole-repo scans. Registered BEFORE its consumers, and built
        // from the user profile directly: the governor is workspace-independent, and reading an
        // IndexBootstrapService getter here would throw (the host constructs every hosted service before any
        // StartAsync — the lifecycle contract above).
        services.AddSingleton(_ => ScanGovernor.FromEnvironment(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".miller")));

        // The bootstrap holds the built current-workspace state. Registered as a singleton AND as the FIRST hosted service so
        // its StartAsync (index build + ledger open + holder seed + canonical-root resolve) completes before any
        // OTHER hosted service's StartAsync (the indexer, the freshness poller, the MCP transport) runs.
        services.AddSingleton<IndexBootstrapService>();
        services.AddHostedService(sp => sp.GetRequiredService<IndexBootstrapService>());

        services.AddSingleton<WorkspaceBindingService>();
        services.AddSingleton<IWorkspaceBindingService>(sp => sp.GetRequiredService<WorkspaceBindingService>());

        // Holder-backed current-workspace services resolved from the bootstrap. These factories read the bootstrap getters, so
        // they MUST only be resolved after StartAsync — i.e. by per-call tools and the lazily-resolved probe
        // below, NEVER by a hosted-service constructor (see the lifecycle contract above). They are transient
        // rather than singleton so MCP roots/list_changed rebinds resolve the latest primary workspace.
        services.AddTransient(sp => sp.GetRequiredService<IndexBootstrapService>().Holder);
        services.AddTransient(sp => sp.GetRequiredService<IndexBootstrapService>().Resolver);
        services.AddTransient(sp => sp.GetRequiredService<IndexBootstrapService>().Workspace);
        services.AddSingleton(sp => sp.GetRequiredService<IndexBootstrapService>().Ledger);

        // M3 hosted services (registered AFTER the bootstrap so its StartAsync seeds the holder + canonical root
        // first). BOTH take only the bootstrap and read its getters lazily inside ExecuteAsync — never at
        // construction (the lifecycle contract). Registered as singletons too so index_fresh reads their state.
        services.AddSingleton<FreshnessService>();
        services.AddSingleton<IndexerService>();
        services.AddHostedService(sp => sp.GetRequiredService<FreshnessService>());
        if (startIndexer)
            services.AddHostedService(sp => sp.GetRequiredService<IndexerService>());

        // Optional local semantic retrieval (ADR-0003). The drain loop follows the SAME lazy-bootstrap-getter
        // discipline as the M3 services above, and under MILLER_SEMANTIC=off its ExecuteAsync returns before
        // waiting, opening, or stating anything — the vectors-v1 zero-work guarantee. The wake signal is the
        // process-wide instance IndexerSidecarConverger stamps.
        services.AddSingleton(_ =>
            evaluationAdapter?.CreateVectorSidecar(activeSemanticMode)
            ?? new VectorSidecar(activeSemanticMode));
        services.AddSingleton(_ => VectorConvergeSignal.Shared);
        if (evaluationAdapter is null && activeSemanticMode is not SemanticMode.Off)
        {
            services.AddSingleton(sp =>
            {
                WorkspaceContext workspace =
                    sp.GetRequiredService<IndexBootstrapService>().Workspace;
                string millerHome = Path.GetDirectoryName(workspace.RegistryDbPath)
                    ?? throw new InvalidOperationException(
                        $"Registry path '{workspace.RegistryDbPath}' has no parent directory.");
                return new SharedSemanticBrokerConnectionFactory(
                    workspace.ToolsRoot,
                    millerHome,
                    SemanticEncoderSelection.Active);
            });
        }

        services.AddSingleton(sp => new SemanticEmbeddingSessionBroker(
            sp.GetRequiredService<VectorSidecar>().Enabled,
            () =>
            {
                if (evaluationAdapter is not null)
                    return evaluationAdapter.CreateSession();

                WorkspaceContext workspace =
                    sp.GetRequiredService<IndexBootstrapService>().Workspace;
                string executable = SemanticSidecarLayout.ExecutablePath(workspace.ToolsRoot);
                return File.Exists(executable)
                    ? SemanticSearchArm.ProcessSession(
                        sp.GetRequiredService<SharedSemanticBrokerConnectionFactory>(),
                        SemanticEncoderSelection.Active)
                    : null;
            },
            () => sp.GetService<SharedSemanticBrokerConnectionFactory>()?.Snapshot));
        services.AddSingleton<VectorConvergeService>();
        services.AddHostedService(sp => sp.GetRequiredService<VectorConvergeService>());

        // The query-time half of ADR-0003: the semantic arm the search tool's symbol route may fuse with. The
        // broker is a singleton because a resident child process, its restart count and an open circuit are
        // exactly the state a per-query session would silently reset. The arm itself is transient (resolved per tool call, well after
        // StartAsync) so its WorkspaceContext read follows the same rebind rules as the other per-call services.
        // Under MILLER_SEMANTIC=off nothing here resolves a workspace, a path, or a process.
        services.AddTransient<ISymbolFusionArm>(sp =>
        {
            var sidecar = sp.GetRequiredService<VectorSidecar>();
            var broker = sp.GetRequiredService<SemanticEmbeddingSessionBroker>();
            // The root comes from the request, not from WorkspaceContext: a workspace_id-routed search ranks
            // another workspace's index, and pairing it with the ambient workspace's vectors fuses the wrong
            // artifact. Only a request that carried no root falls back to the ambient one.
            return new SemanticSymbolFusionArm(sidecar.Mode, root =>
            {
                string workspaceRoot = string.IsNullOrEmpty(root)
                    ? sp.GetRequiredService<WorkspaceContext>().WorkspaceRoot
                    : root;
                return new SemanticSearchArm(workspaceRoot, sidecar, broker);
            });
        });

        // The index_fresh probe (decision-8) the telemetry filter reads per call: built revision vs. the freshness
        // service's last-observed revision AND the indexer's queue-empty state. Resolved lazily (per call / by the
        // filter), well after StartAsync, so its GetRequiredService<IndexHolder>() is safe. Cheap — no SQLite on
        // the tool hot path.
        services.AddTransient(sp =>
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
        services.AddTransient(sp =>
        {
            var workspace = sp.GetRequiredService<WorkspaceContext>();
            string millerDir = Path.GetDirectoryName(workspace.ExtractDbPath)!;
            return new EditApplier(() => EditWriteLock.TryAcquire(millerDir));
        });
        services.AddSingleton<IEditWriteThrough>(sp =>
            new LeaderWriteThrough(
                sp.GetRequiredService<IndexerService>(),
                sp.GetRequiredService<IndexBootstrapService>(),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<LeaderWriteThrough>()));

        // Soft budgets (M7 decision-4): per-tool latency + est-token warn thresholds the central telemetry filter
        // evaluates after each call, logging a WARN per breach (warn-only — never blocks or errors the call). The
        // production defaults live on SoftBudgets.Default.
        services.AddSingleton(SoftBudgets.Default);

        // M7 workspace tool (decision-1): the admin/index-lifecycle tool. Explicitly registered in Program.cs for
        // Native AOT; it resolves the current holder/workspace/indexer/freshness/probe/ledger services above plus a
        // JulieExtractRunner for the open(path) prime scan. The runner is located
        // under the SAME tools root the bootstrap + indexer use (the pinned julie-extract ships there, NOT the repo
        // cwd), so a missing binary fails loudly via JulieExtractRunner.Locate's restore-script message rather than
        // silently degrading.
        services.AddTransient(sp =>
        {
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(JulieExtractRunner));
            return JulieExtractRunner.Locate(
                sp.GetRequiredService<WorkspaceContext>().ToolsRoot,
                reason => logger.LogWarning(
                    "julie-extract is running WITHOUT Windows orphan containment: {Reason}. The scan " +
                    "proceeds, but if this Miller is killed the extractor can outlive it.", reason));
        });

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
        services.AddSingleton<IGitDiffReader, ProcessGitDiffReader>();
        services.AddSingleton<IGitHistoryReader, ProcessGitHistoryReader>();
        services.AddSingleton<CrossWorkspaceRefreshService>();
        services.AddTransient<WorkspaceIndexProvider>();
        services.AddTransient<IWorkspaceIndexProvider>(sp => sp.GetRequiredService<WorkspaceIndexProvider>());
        services.AddTransient<IWorkspaceArtifactProvider>(sp => sp.GetRequiredService<WorkspaceIndexProvider>());
        services.AddTransient<IWorkspaceSearchProvider>(sp => sp.GetRequiredService<WorkspaceIndexProvider>());
        services.AddTransient<IWorkspaceSymbolReadProvider>(sp => sp.GetRequiredService<WorkspaceIndexProvider>());
        services.AddTransient<IWorkspaceContentSearchProvider>(sp => sp.GetRequiredService<WorkspaceIndexProvider>());
        services.AddTransient<IWorkspaceRegionSearchProvider>(sp => sp.GetRequiredService<WorkspaceIndexProvider>());
        services.AddTransient<IWorkspaceTextContentSearchProvider>(sp => sp.GetRequiredService<WorkspaceIndexProvider>());

        return services;
    }
}
