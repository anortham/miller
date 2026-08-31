using System.ComponentModel;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Miller.Core.Freshness;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Indexing.Semantic;
using Miller.Indexing.Store;
using Miller.Server.Cli;
using Miller.Server.Hosting;
using Miller.Server.Logging;
using Miller.Server.Telemetry;
using Miller.Server.Workspaces;
using ModelContextProtocol.Server;

namespace Miller.Server.Tools;

/// <summary>
/// Provides bounded workspace status, lifecycle, registry, health, onboarding, leadership, and dashboard
/// operations from machine-global registry state, with primary-only facts used when a workspace is bound.
/// </summary>
[McpServerToolType]
public sealed class WorkspaceTool
{
    private readonly IndexHolder? _holder;
    private readonly WorkspaceContext? _workspace;
    private readonly IndexerService? _indexer;
    private readonly FreshnessService? _freshness;
    private readonly IndexFreshProbe? _freshProbe;
    private readonly IndexBootstrapService? _bootstrap;
    private readonly IndexBootstrapService? _primary;
    private readonly TelemetryLedger _ledger;
    private readonly WorkspaceRegistry _registry;
    private readonly Func<CrossWorkspaceRefreshService> _crossWorkspaceRefresh;
    private readonly SymbolSearchSidecar _sidecar;
    private readonly ContentCorpusSidecar _contentSidecar = new();
    private readonly VectorSidecar _vectors;
    private readonly SemanticEmbeddingSessionBroker? _semanticBroker;
    private readonly Func<string, WorkspaceOpenPrimeEnqueueResult> _enqueueOpenPrime;
    private readonly Func<string, IDisposable?> _acquireWriterLock;
    private readonly IDashboardLauncher _dashboardLauncher;
    private readonly ILogger<WorkspaceTool> _logger;
    private readonly ScanGovernor _governor;
    private readonly MillerHostPaths _hostPaths;

    /// <summary>Construct over the live admin singletons (production).</summary>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public WorkspaceTool(
        IndexHolder holder,
        WorkspaceContext workspace,
        IndexerService indexer,
        FreshnessService freshness,
        IndexFreshProbe freshProbe,
        IndexBootstrapService bootstrap,
        TelemetryLedger ledger,
        WorkspaceRegistry registry,
        Func<CrossWorkspaceRefreshService> crossWorkspaceRefresh,
        SymbolSearchSidecar sidecar,
        VectorSidecar vectors,
        ScanGovernor governor,
        ILogger<WorkspaceTool> logger,
        WorkspaceOpenPrimeService openPrimeService,
        SemanticEmbeddingSessionBroker? semanticBroker = null)
        : this(
            holder,
            workspace,
            indexer,
            freshness,
            freshProbe,
            bootstrap,
            ledger,
            registry,
            crossWorkspaceRefresh,
            sidecar,
            vectors,
            acquireWriterLock: millerDir => SingleWriterLock.TryAcquire(millerDir),
            enqueueOpenPrime: OpenPrimeEnqueue(openPrimeService),
            dashboardLauncher: new DashboardCliLauncher(),
            logger: logger,
            semanticBroker: semanticBroker,
            governor: governor)
    {
    }

    private static Func<string, WorkspaceOpenPrimeEnqueueResult> OpenPrimeEnqueue(
        WorkspaceOpenPrimeService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        return service.TryEnqueue;
    }

    internal WorkspaceTool(
        IndexHolder? holder,
        WorkspaceContext? workspace,
        IndexerService? indexer,
        FreshnessService? freshness,
        IndexFreshProbe? freshProbe,
        IndexBootstrapService? bootstrap,
        TelemetryLedger ledger,
        WorkspaceRegistry registry,
        Func<CrossWorkspaceRefreshService> crossWorkspaceRefresh,
        SymbolSearchSidecar sidecar,
        VectorSidecar vectors,
        Func<string, IDisposable?> acquireWriterLock,
        Func<string, WorkspaceOpenPrimeEnqueueResult> enqueueOpenPrime,
        IDashboardLauncher dashboardLauncher,
        ILogger<WorkspaceTool> logger,
        SemanticEmbeddingSessionBroker? semanticBroker = null,
        ScanGovernor? governor = null,
        MillerHostPaths? hostPaths = null,
        IndexBootstrapService? primary = null)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(crossWorkspaceRefresh);
        ArgumentNullException.ThrowIfNull(sidecar);
        ArgumentNullException.ThrowIfNull(vectors);
        ArgumentNullException.ThrowIfNull(enqueueOpenPrime);
        ArgumentNullException.ThrowIfNull(acquireWriterLock);
        ArgumentNullException.ThrowIfNull(dashboardLauncher);
        ArgumentNullException.ThrowIfNull(logger);
        if (hostPaths is null && workspace is null)
            throw new ArgumentException("An unbound workspace tool requires machine-global host paths.", nameof(hostPaths));
        _hostPaths = hostPaths ?? HostPathsFor(workspace!);
        _holder = holder;
        _workspace = workspace;
        _indexer = indexer;
        _freshness = freshness;
        _freshProbe = freshProbe;
        _bootstrap = bootstrap;
        _primary = primary;
        _ledger = ledger;
        _registry = registry;
        _crossWorkspaceRefresh = crossWorkspaceRefresh;
        _sidecar = sidecar;
        _vectors = vectors;
        _semanticBroker = semanticBroker;
        _enqueueOpenPrime = enqueueOpenPrime;
        _acquireWriterLock = acquireWriterLock;
        _dashboardLauncher = dashboardLauncher;
        _logger = logger;
        _governor = governor ?? ScanGovernor.Disabled();
    }

    /// <summary>Constructs lifecycle operations from machine-global state before a primary workspace binds.</summary>
    internal WorkspaceTool(
        MillerHostPaths hostPaths,
        WorkspaceRegistry registry,
        TelemetryLedger ledger,
        Func<CrossWorkspaceRefreshService> crossWorkspaceRefresh,
        SymbolSearchSidecar sidecar,
        VectorSidecar vectors,
        Func<string, IDisposable?> acquireWriterLock,
        Func<string, WorkspaceOpenPrimeEnqueueResult> enqueueOpenPrime,
        IDashboardLauncher dashboardLauncher,
        ILogger<WorkspaceTool> logger,
        IndexerService? indexer = null,
        FreshnessService? freshness = null,
        IndexBootstrapService? primary = null,
        SemanticEmbeddingSessionBroker? semanticBroker = null,
        ScanGovernor? governor = null)
        : this(
            holder: null,
            workspace: null,
            indexer,
            freshness,
            freshProbe: null,
            bootstrap: primary,
            ledger,
            registry,
            crossWorkspaceRefresh,
            sidecar,
            vectors,
            acquireWriterLock,
            enqueueOpenPrime,
            dashboardLauncher,
            logger,
            semanticBroker,
            governor,
            hostPaths,
            primary)
    {
    }

    private static MillerHostPaths HostPathsFor(WorkspaceContext workspace)
    {
        string millerDirectory = Path.GetDirectoryName(workspace.RegistryDbPath)
            ?? throw new ArgumentException("The workspace registry path has no parent directory.", nameof(workspace));
        return new MillerHostPaths(
            millerDirectory,
            workspace.RegistryDbPath,
            workspace.TelemetryDbPath,
            workspace.ToolsRoot);
    }

    [McpServerTool(Name = "workspace")]
    [Description(
        "Manage the workspace index. Defaults to status (freshness, revision, leader). refresh updates stale " +
        "files; full forces a rebuild; health reports readiness + extraction quality; status/health expose vector " +
        "and semantic-broker readiness, role, backend, accelerator lease, reconnects, and degradation; onboarding gives " +
        "telemetry-derived guidance for this repo; list shows registered workspaces (filter/limit, " +
        "recency-ordered); open registers another repo for cross-workspace reads; prune removes registry rows " +
        "whose roots are gone; leader diagnoses/hands off the indexer lock; dashboard starts/opens the local " +
        "dashboard. Use when results look stale, before cross-workspace queries, or at session start (onboarding). " +
        "NOT for: reading code (search/inspect). Example: workspace operation=list filter=eros limit=10.")]
    public string Workspace(
        [Description("status|refresh|full|list|open|remove|prune|health|onboarding|leader|dashboard. Default status.")] string operation = "status",
        [Description("Registered workspace selector: display ID, unique prefix, full ID, or root path. Current and primary are not valid MCP selectors.")]
        string? workspace_id = null,
        [Description("A workspace root path. Required for open; optional for status/health/onboarding/refresh/full/remove.")]
        string? path = null,
        [Description("Output format: compact|json. Exhaustive health JSON/markdown stays CLI-only. Default compact.")]
        string format = "compact",
        [Description("Dashboard launch port. Used only with operation=dashboard when no dashboard is already running.")]
        int? port = null,
        [Description("For operation=leader, queue an explicit graceful leadership handoff request.")]
        bool handoff = false,
        [Description("For operation=leader with handoff=true, wait briefly for the live leader to observe the request.")]
        bool wait = false,
        [Description("operation=list only: case-insensitive substring filter on display id, root, or state, applied before the cap. Default null (no filter).")]
        string? filter = null,
        [Description("operation=list only: max entries before the exact omitted-count tail. Range 1-100; default 20.")]
        int? limit = null,
        [Description("operation=prune only: list candidates without removing registry rows. Default false.")]
        bool dry_run = false)
    {
        var telemetry = TelemetryContext.Current;
        string normalizedOperation = NormalizeOperation(operation);
        bool json = string.Equals(format?.Trim(), "json", StringComparison.OrdinalIgnoreCase);
        try
        {
            ValidateRequest(
                normalizedOperation,
                workspace_id,
                path,
                format,
                port,
                handoff,
                wait,
                filter,
                limit,
                dry_run);
            if (telemetry is not null)
                telemetry.Op = normalizedOperation;
            WorkspaceOperationResult result = Dispatch(
                normalizedOperation,
                workspace_id,
                path,
                port,
                json,
                handoff,
                wait,
                filter,
                limit,
                dry_run);

            if (telemetry is not null)
            {
                telemetry.ResultCount = result.ResultCount;
                telemetry.Outcome = result.Outcome;
            }
            string output = result.Output;
            if (!json && result.Hint is { } hint)
                output = output + "\n" + hint;
            ToolDiagnostic? diagnostic = result.Diagnostic;
            if (diagnostic is null && result.Outcome is TelemetryOutcome.Empty or TelemetryOutcome.Error)
            {
                diagnostic = WorkspaceDiagnostic(normalizedOperation, result.Outcome);
            }
            if (diagnostic is not null)
                output = ToolDiagnosticRenderer.Attach("workspace", output, diagnostic, json, telemetry);
            return EnforceMcpBudget(output, normalizedOperation, json, telemetry);
        }
        catch (Exception ex)
        {
            ToolDiagnostic diagnostic = ToolDiagnostic.FromException(ex);
            if (diagnostic.Outcome == ToolDiagnosticOutcome.Error)
            {
                telemetry?.SetError(ex);
                _logger.LogError(ex, "workspace {Operation} failed.", operation);
            }
            else
            {
                _logger.LogDebug(
                    "workspace {Operation} was not executed: {Message}",
                    operation,
                    diagnostic.Message);
            }
            string output = ToolDiagnosticRenderer.Render(
                "workspace",
                diagnostic,
                json,
                telemetry);
            return EnforceMcpBudget(output, normalizedOperation, json, telemetry);
        }
    }

    private static ToolDiagnostic WorkspaceDiagnostic(
        string operation,
        TelemetryOutcome outcome)
    {
        if (outcome == TelemetryOutcome.Error)
            return ToolDiagnostic.Unavailable(
                $"workspace_{operation}_failed",
                $"Workspace operation '{operation}' could not complete.");

        return ToolDiagnostic.ExpectedEmpty(
            $"workspace_{operation}_empty",
            $"Workspace operation '{operation}' returned no result.");
    }

    private static string NormalizeOperation(string? operation) =>
        string.IsNullOrWhiteSpace(operation)
            ? "status"
            : operation.Trim().ToLowerInvariant();

    private static void ValidateRequest(
        string operation,
        string? workspaceId,
        string? path,
        string? format,
        int? port,
        bool handoff,
        bool wait,
        string? filter,
        int? limit,
        bool dryRun)
    {
        ValidateLength(operation, 32, "operation");
        if (!IsSupportedOperation(operation))
            throw new ToolDiagnosticException(UnsupportedOperationDiagnostic(operation));

        ValidateLength(workspaceId, 512, "workspace_id");
        ValidateLength(path, 4096, "path");
        ValidateLength(filter, 256, "filter");

        string normalizedFormat = string.IsNullOrWhiteSpace(format)
            ? "compact"
            : format.Trim().ToLowerInvariant();
        if (normalizedFormat is not ("compact" or "json"))
        {
            throw Refusal(
                "invalid_format",
                "Workspace MCP output format must be compact or json. Exhaustive health JSON and markdown are CLI-only.");
        }

        if (!string.IsNullOrWhiteSpace(workspaceId) && !string.IsNullOrWhiteSpace(path))
        {
            throw Refusal(
                "conflicting_workspace_selectors",
                "Pass either workspace_id or path, not both.");
        }

        bool supportsWorkspaceId = operation is
            "status" or "health" or "onboarding" or "leader" or "refresh" or "full" or "remove";
        bool supportsPath = operation is
            "status" or "health" or "onboarding" or "leader" or "refresh" or "full" or "open" or "remove";
        if (!supportsWorkspaceId && !string.IsNullOrWhiteSpace(workspaceId))
            throw Refusal("workspace_id_not_supported", $"workspace_id is not valid for operation '{operation}'.");
        if (!supportsPath && !string.IsNullOrWhiteSpace(path))
            throw Refusal("path_not_supported", $"path is not valid for operation '{operation}'.");

        if (operation == "list")
        {
            if (limit is < 1 or > 100)
                throw Refusal("invalid_list_limit", "workspace list limit must be between 1 and 100.");
        }
        else
        {
            if (limit is not null)
                throw Refusal("limit_not_supported", $"limit is not valid for operation '{operation}'.");
            if (!string.IsNullOrWhiteSpace(filter))
                throw Refusal("filter_not_supported", $"filter is not valid for operation '{operation}'.");
        }

        if (operation == "dashboard")
        {
            if (port is < 1 or > 65535)
                throw Refusal("invalid_dashboard_port", "Dashboard port must be between 1 and 65535.");
        }
        else if (port is not null)
        {
            throw Refusal("port_not_supported", $"port is not valid for operation '{operation}'.");
        }

        if (operation == "leader")
        {
            if (wait && !handoff)
                throw Refusal("invalid_leader_wait", "wait=true requires handoff=true.");
        }
        else if (handoff || wait)
        {
            throw Refusal(
                "leader_options_not_supported",
                $"handoff and wait are not valid for operation '{operation}'.");
        }

        if (operation != "prune" && dryRun)
            throw Refusal("dry_run_not_supported", $"dry_run is not valid for operation '{operation}'.");
    }

    private static bool IsSupportedOperation(string operation) =>
        operation is
            "status" or "refresh" or "full" or "list" or "open" or "remove" or "prune" or
            "health" or "onboarding" or "leader" or "dashboard";

    private static ToolDiagnostic UnsupportedOperationDiagnostic(string operation) =>
        ToolDiagnostic.Unsupported(
            "unsupported_operation",
            $"Unknown workspace operation '{operation}'.",
            [new ToolDiagnosticAction(
                "workspace(operation=\"status\")",
                "show current workspace status")]);

    private static void ValidateLength(string? value, int maxLength, string name)
    {
        if (value is not null && value.Length > maxLength)
            throw Refusal("input_too_long", $"{name} exceeds the {maxLength}-character workspace input limit.");
    }

    private static ToolDiagnosticException Refusal(string code, string message) =>
        new(ToolDiagnostic.Refusal(code, message));

    private static string EnforceMcpBudget(
        string output,
        string operation,
        bool json,
        TelemetryScope? telemetry)
    {
        if (Encoding.UTF8.GetByteCount(output) <= ToolOutputBudget.WorkspaceMcpMaxBytes)
            return output;

        ToolDiagnostic diagnostic = ToolDiagnostic.Refusal(
            "workspace_output_budget_exceeded",
            $"Workspace operation '{operation}' exceeded the 12 KiB MCP response budget.",
            operation switch
            {
                "list" =>
                [new ToolDiagnosticAction(
                    "workspace(operation=\"list\", limit=10, filter=\"<substring>\")",
                    "narrow the registry rows")],
                "health" =>
                [new ToolDiagnosticAction(
                    "miller workspace health --json",
                    "read the exhaustive report through the CLI")],
                _ =>
                [new ToolDiagnosticAction(
                    "workspace(operation=\"status\")",
                    "return to the bounded workspace summary")],
            });
        return ToolDiagnosticRenderer.Render("workspace", diagnostic, json, telemetry);
    }

    private static WorkspaceOperationResult HealthResult(WorkspaceHealthFacts health, bool json) =>
        new(
            WorkspaceRender.Health(
                health,
                json ? WorkspaceHealthFormat.JsonSummary : WorkspaceHealthFormat.Compact),
            1,
            TelemetryOutcome.Ok);

    // The pure-ish dispatch: route the operation to its handler, returning the rendered output, a result count
    // (for the telemetry KPI), and the outcome. An unknown operation is a usage note (Empty, not an error).
    private WorkspaceOperationResult Dispatch(
        string operation, string? workspaceId, string? path, int? port, bool json,
        bool handoff, bool wait,
        string? filter = null, int? limit = null, bool dryRun = false)
    {
        switch (operation)
        {
            case "status":
                return RenderTargetStatus(workspaceId, path, json);
            case "health":
                return RenderTargetHealth(workspaceId, path, json);
            case "onboarding":
                return RenderTargetOnboarding(workspaceId, path, json);
            case "leader":
                return RenderTargetLeader(workspaceId, path, json, handoff, wait);
            case "list":
                return RenderRegistryList(json, filter, limit);
            case "refresh":
                return RenderTargetAction("refresh", workspaceId, path, force: false, json);
            case "full":
                return RenderTargetAction("full", workspaceId, path, force: true, json);
            case "open":
                return Open(path, json);
            case "remove":
                return Remove(workspaceId, path, json);
            case "prune":
                return Prune(json, dryRun);
            case "dashboard":
                return Dashboard(port, json);
            default:
                return new WorkspaceOperationResult(
                    UsageNote(operation, json),
                    0,
                    TelemetryOutcome.Empty,
                    UnsupportedOperationDiagnostic(operation));
        }
    }

    // ---------- status / list facts ----------

    private WorkspaceHealthFacts ReadCurrentHealth()
    {
        WorkspaceContext current = CurrentWorkspace;
        return WorkspaceHealthFacts.Create(
            AssembleFacts(),
            _ledger.SummarizeRecentForWorkspace(current.WorkspaceId, TelemetryHighlights.RecentWindowDays),
            _ledger.SummarizeOutcomesForWorkspace(current.WorkspaceId, TelemetryHighlights.RecentWindowDays),
            ReadExtractionHealth(
                current.ExtractDbPath,
                current.CanonicalRoot ?? current.WorkspaceRoot,
                current.WorkspaceId),
            ReadLeaderFacts(current.ExtractDbPath, ownWorkspace: true),
            ReadHistoryStatus(current.ExtractDbPath));
    }

    private WorkspaceOperationResult RenderCurrentOnboarding(bool json)
    {
        WorkspaceFacts facts = AssembleFacts();
        WorkspaceOnboardingFacts onboarding = WorkspaceOnboardingAssembler.CreateFromWorkspace(
            facts,
            _ledger.DbPath,
            facts.WorkspaceId,
            facts.Root,
            facts.DbPath,
            storeEnabled: facts.Store is not null);
        return OnboardingResult(onboarding, json, StaleRegistryHint(json));
    }

    // Leader facts: identity + liveness, this process's extractor, and the same artifact version Evaluate uses.
    // Re-evaluate own-workspace eligibility from that displayed string so the reason cannot name a stale token.
    // Cross-workspace targets leave OwnVerdict null (this process is not a writer candidate there).
    // Best-effort status of the workspace's append-only metric history sidecar (sibling of symbols.db). Never
    // throws — a missing/unreadable history.db degrades to an absent-present status the health surface renders.
    private static MetricHistoryStatus ReadHistoryStatus(string indexDbPath) =>
        MetricHistoryStore.ReadStatus(MetricSnapshotAggregates.HistoryDbPathFor(indexDbPath));

    private LeaderHealthFacts ReadLeaderFacts(string indexDbPath, bool ownWorkspace)
    {
        string? ownVersion = _indexer?.OwnExtractorVersion;
        string? artifactVersion;
        LeadershipVerdict? verdict = null;
        try
        {
            artifactVersion = ReadArtifactExtractorVersion(indexDbPath);
            if (ownWorkspace && ownVersion is not null)
            {
                verdict = LeadershipEligibility.Evaluate(
                    ownVersion,
                    artifactVersion,
                    allowDowngrade: Environment.GetEnvironmentVariable("MILLER_ALLOW_EXTRACTOR_DOWNGRADE") == "1");
            }
            else if (ownWorkspace)
            {
                verdict = _indexer?.EligibilityVerdict;
            }
        }
        catch (StoreArtifactVersionReadException ex)
        {
            artifactVersion = null;
            if (ownWorkspace)
                verdict = new LeadershipVerdict(false, false, ex.Message);
        }

        return LeaderHealthFacts.Read(Path.GetDirectoryName(indexDbPath)!) with
        {
            OwnExtractorVersion = ownVersion,
            ArtifactExtractorVersion = artifactVersion,
            OwnVerdict = verdict,
        };
    }

    private static string? ReadArtifactExtractorVersion(string indexDbPath) =>
        WorkspaceReadSessionFactory.StoreEnabledFromEnvironment()
            ? StoreArtifactVersionReader.ReadForEligibility(indexDbPath, ExtractBinaryVersionReader.TryRead)
            : ExtractBinaryVersionReader.TryRead(indexDbPath);

    // The sidecar writers reap their own staging orphans only as a build STARTS, so a workspace whose scans never
    // reach them keeps every `.search-build-*.db` forever (1.18 GB observed in the field on a workspace stuck in a
    // julie-extract exit-2 scan error). Lifecycle reads already touch that directory, and the reaper is total, so a
    // reap here reclaims the leak without a background timer and cannot fail the call.
    private void ReapStagingOrphans(TargetWorkspace target)
    {
        WorkspaceContext? current = CurrentWorkspaceOrNull;
        string? indexDbPath = target.IsCurrent ? current?.ExtractDbPath : target.Row?.IndexDbPath;
        ReapStagingOrphans(string.IsNullOrWhiteSpace(indexDbPath) ? null : Path.GetDirectoryName(indexDbPath));
    }

    private static void ReapStagingOrphans(string? sidecarDirectory)
    {
        SidecarStagingReaper.ReapWorkspaceStaging(sidecarDirectory, SidecarStagingReaper.DefaultStaleAge);
    }

    private WorkspaceOperationResult RenderTargetStatus(
        string? workspaceId, string? path, bool json)
    {
        TargetWorkspace target = ResolveTarget(workspaceId, path, WorkspaceSelectorIntent.Read);
        if (target.UnknownNote is { } note)
            return (Note(note, json), 0, TelemetryOutcome.Empty);

        ReapStagingOrphans(target);

        if (target.IsCurrent)
        {
            WorkspaceContext current = CurrentWorkspace;
            WorkspaceFacts currentFacts = AssembleFacts();
            return StatusResult(
                WorkspaceRender.Status(
                    currentFacts,
                    _ledger.SummarizeRecentForWorkspace(current.WorkspaceId, TelemetryHighlights.RecentWindowDays),
                    json,
                    ReadLeaderFacts(current.ExtractDbPath, ownWorkspace: true),
                    CurrentBootstrapSnapshot),
                currentFacts,
                "workspace(operation=\"health\")",
                StaleRegistryHint(json));
        }

        WorkspaceRegistryRow row = target.Row
            ?? throw new InvalidOperationException($"Workspace registry row '{target.WorkspaceId}' was not resolved.");
        VerifyRegisteredRoot(row);

        WorkspaceFacts facts = WorkspaceFactsAssembler.FromRegisteredRow(
            _registry,
            row,
            WorkspaceRegisteredFactsProfile.McpStatus,
            _sidecar,
            _contentSidecar,
            _vectors,
            CurrentSemanticBrokerFacts(),
            _governor);
        LeaderHealthFacts leader = ReadLeaderFacts(row.IndexDbPath, ownWorkspace: false);
        return StatusResult(
            WorkspaceRender.Status(
                facts,
                _ledger.SummarizeRecentForWorkspace(row.WorkspaceId, TelemetryHighlights.RecentWindowDays),
                json,
                leader),
            facts,
            "workspace(operation=\"health\", workspace_id=\"" + row.DisplayId + "\")",
            StaleRegistryHint(json));
    }

    private static WorkspaceOperationResult StatusResult(
        string output,
        WorkspaceFacts facts,
        string healthAction,
        string? hint)
    {
        bool unavailable = facts.FreshnessStatus is "missing_index" or "unreadable_index";
        return new WorkspaceOperationResult(
            output,
            unavailable ? 0 : 1,
            unavailable ? TelemetryOutcome.Error : TelemetryOutcome.Ok,
            unavailable
                ? ToolDiagnostic.Unavailable(
                    "workspace_index_unavailable",
                    "The workspace index is missing or unreadable.",
                    [new ToolDiagnosticAction(
                        healthAction,
                        "inspect the workspace artifacts")])
                : null,
            hint);
    }

    private const int StaleRegistryHintMinimumDeadRows = 10;
    private const int StaleRegistryHintMinimumPercent = 25;

    /// <summary>
    /// The compact-only nudge that a registry has accumulated rows whose roots are gone. Agent harnesses create
    /// and delete worktrees without ever calling <c>workspace remove</c>, and a dead row keeps answering
    /// selectors — it can shadow the short name of the live repository it was branched from. Null for JSON output
    /// and null below the threshold, so the JSON contract stays byte-identical (ADR-0001) and a registry with a
    /// normal amount of churn says nothing.
    /// </summary>
    private string? StaleRegistryHint(bool json)
    {
        if (json)
            return null;

        int registered;
        int dead;
        try
        {
            IReadOnlyList<WorkspaceRegistryRow> rows = _registry.List();
            registered = rows.Count;
            dead = rows.Count(static row => !Directory.Exists(row.CanonicalRoot));
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return ShouldHintStaleRegistry(dead, registered)
            ? NextStepHint.Render(
                "workspace(operation=\"prune\", dry_run=true)",
                $"{dead} of {registered} registered roots are gone from disk")
            : null;
    }

    /// <summary>
    /// Whether a registry is stale enough to be worth a nudge: at least
    /// <see cref="StaleRegistryHintMinimumDeadRows"/> dead rows AND at least
    /// <see cref="StaleRegistryHintMinimumPercent"/> of the registry. Both bars exist so neither a small registry
    /// with one dead row nor a large one with a handful of them interrupts a healthy workspace.
    /// </summary>
    internal static bool ShouldHintStaleRegistry(int deadRows, int registeredRows) =>
        deadRows >= StaleRegistryHintMinimumDeadRows
        && registeredRows > 0
        && deadRows * 100 >= registeredRows * StaleRegistryHintMinimumPercent;

    private WorkspaceOperationResult RenderTargetHealth(
        string? workspaceId, string? path, bool json)
    {
        TargetWorkspace target = ResolveTarget(workspaceId, path, WorkspaceSelectorIntent.Read);
        if (target.UnknownNote is { } note)
            return (Note(note, json), 0, TelemetryOutcome.Empty);

        ReapStagingOrphans(target);

        if (target.IsCurrent)
        {
            WorkspaceHealthFacts currentHealth = ReadCurrentHealth();
            return HealthResult(currentHealth, json);
        }

        WorkspaceRegistryRow row = target.Row
            ?? throw new InvalidOperationException($"Workspace registry row '{target.WorkspaceId}' was not resolved.");
        VerifyRegisteredRoot(row);
        WorkspaceFacts statusFacts = WorkspaceFactsAssembler.FromRegisteredRow(
            _registry,
            row,
            WorkspaceRegisteredFactsProfile.McpHealth,
            _sidecar,
            _contentSidecar,
            _vectors,
            CurrentSemanticBrokerFacts(),
            _governor);
        WorkspaceExtractionHealthFacts extraction;
        if (statusFacts.FreshnessStatus is "missing_index" or "unreadable_index")
        {
            extraction = UnavailableExtraction(statusFacts.WarningText ?? statusFacts.FreshnessStatus);
        }
        else
        {
            try
            {
                extraction = ReadExtractionHealth(row.IndexDbPath, row.CanonicalRoot, row.WorkspaceId);
            }
            catch (Exception ex) when (ex is IOException || IsHealthIndexReadException(ex))
            {
                statusFacts = WorkspaceFactsAssembler.FromRegisteredHealthReadError(
                    _registry,
                    row,
                    WorkspaceRegisteredFactsProfile.McpHealth,
                    _sidecar,
                    _contentSidecar,
                    ex,
                    _vectors,
                    CurrentSemanticBrokerFacts(),
                    _governor);
                extraction = UnavailableExtraction(statusFacts.WarningText ?? ex.Message);
            }
        }

        WorkspaceHealthFacts health = WorkspaceHealthFacts.Create(
            statusFacts,
            _ledger.SummarizeRecentForWorkspace(row.WorkspaceId, TelemetryHighlights.RecentWindowDays),
            _ledger.SummarizeOutcomesForWorkspace(row.WorkspaceId, TelemetryHighlights.RecentWindowDays),
            extraction,
            ReadLeaderFacts(row.IndexDbPath, ownWorkspace: false),
            ReadHistoryStatus(row.IndexDbPath));
        return HealthResult(health, json);
    }

    private WorkspaceOperationResult RenderTargetOnboarding(
        string? workspaceId, string? path, bool json)
    {
        TargetWorkspace target = ResolveTarget(workspaceId, path, WorkspaceSelectorIntent.Read);
        if (target.UnknownNote is { } note)
            return (Note(note, json), 0, TelemetryOutcome.Empty);

        if (target.IsCurrent)
            return RenderCurrentOnboarding(json);

        WorkspaceRegistryRow row = target.Row
            ?? throw new InvalidOperationException($"Workspace registry row '{target.WorkspaceId}' was not resolved.");
        VerifyRegisteredRoot(row);
        WorkspaceFacts statusFacts = WorkspaceFactsAssembler.FromRegisteredRow(
            _registry,
            row,
            WorkspaceRegisteredFactsProfile.McpHealth,
            _sidecar,
            _contentSidecar,
            _vectors,
            CurrentSemanticBrokerFacts(),
            _governor);
        WorkspaceOnboardingFacts onboarding = WorkspaceOnboardingAssembler.CreateFromWorkspace(
            statusFacts,
            _ledger.DbPath,
            row.WorkspaceId,
            row.CanonicalRoot,
            row.IndexDbPath,
            storeEnabled: statusFacts.Store is not null);
        return OnboardingResult(onboarding, json, StaleRegistryHint(json));
    }

    private static WorkspaceOperationResult OnboardingResult(
        WorkspaceOnboardingFacts onboarding,
        bool json,
        string? hint)
    {
        bool unavailable = !onboarding.Telemetry.Available;
        return new WorkspaceOperationResult(
            WorkspaceRender.Onboarding(
                onboarding,
                json,
                ToolOutputBudget.WorkspaceOnboardingMcpRowLimit),
            unavailable ? 0 : 1,
            unavailable ? TelemetryOutcome.Error : TelemetryOutcome.Ok,
            unavailable
                ? ToolDiagnostic.Unavailable(
                    "workspace_onboarding_telemetry_unavailable",
                    "Workspace onboarding telemetry is unavailable.",
                    [new ToolDiagnosticAction(
                        "workspace(operation=\"status\")",
                        "inspect the workspace and telemetry state")])
                : null,
            hint);
    }

    private WorkspaceOperationResult RenderTargetLeader(
        string? workspaceId, string? path, bool json, bool handoff, bool wait)
    {
        TargetWorkspace target = ResolveTarget(workspaceId, path, WorkspaceSelectorIntent.Mutate);
        if (target.UnknownNote is { } note)
            return (Note(note, json), 0, TelemetryOutcome.Empty);

        WorkspaceFacts facts;
        bool ownWorkspace;
        if (target.IsCurrent)
        {
            facts = AssembleFacts();
            ownWorkspace = true;
        }
        else
        {
            WorkspaceRegistryRow row = target.Row
                ?? throw new InvalidOperationException($"Workspace registry row '{target.WorkspaceId}' was not resolved.");
            VerifyRegisteredRoot(row);
            facts = WorkspaceFactsAssembler.FromRegisteredRow(
                _registry,
                row,
                WorkspaceRegisteredFactsProfile.McpStatus,
                _sidecar,
                _contentSidecar);
            ownWorkspace = false;
        }

        string millerDir = Path.GetDirectoryName(facts.DbPath)!;
        LeaderHealthFacts leader = ReadLeaderFacts(facts.DbPath, ownWorkspace);
        LeaderHandoffRequestReceipt? receipt = null;
        bool observed = false;
        string? handoffNote = null;
        if (handoff)
        {
            receipt = LeaderScanRequestQueue.RequestLeaderHandoff(
                millerDir,
                facts.WorkspaceId ?? WorkspaceId.FromCanonicalRoot(Path.GetFullPath(facts.Root)),
                Environment.ProcessId);
            if (wait)
            {
                observed = WaitForHandoffObservation(receipt, millerDir, leader.Identity);
                handoffNote = observed
                    ? "leader observed the handoff request"
                    : "handoff request queued but not observed before timeout";
            }
            else
            {
                handoffNote = "handoff request queued";
            }
        }

        var result = new WorkspaceLeaderResult(
            facts,
            leader,
            RecommendationForLeader(facts, leader, handoff),
            HandoffRequested: handoff,
            HandoffWaited: wait && handoff,
            HandoffObserved: observed,
            HandoffRequestId: receipt?.RequestId,
            HandoffNote: handoffNote);
        return StatusResult(
            WorkspaceRender.Leader(result, json),
            facts,
            target.IsCurrent
                ? "workspace(operation=\"health\")"
                : "workspace(operation=\"health\", workspace_id=\"" + facts.DisplayId + "\")",
            hint: null);
    }

    private static string RecommendationForLeader(WorkspaceFacts facts, LeaderHealthFacts leader, bool handoffRequested)
    {
        if (handoffRequested)
            return "Handoff requested through the local queue; the current leader must drain it before stepping down.";
        if (facts.IsLeader)
            return "No handoff requested; this process is the current indexer leader.";
        if (leader.Identity is null)
            return "No handoff requested; no leader identity is recorded. An older leader may still hold the lock.";
        if (leader.Alive == false)
            return "No handoff requested; recorded leader is not running. Normal lock retry should recover.";
        return "No handoff requested; use handoff=true to ask the live leader to step down gracefully.";
    }

    private static bool WaitForHandoffObservation(
        LeaderHandoffRequestReceipt receipt,
        string millerDir,
        LeaderIdentity? before)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!File.Exists(receipt.RequestPath) && !File.Exists(receipt.RequestPath + ".claimed"))
                return true;

            LeaderIdentity? current = LeaderIdentityFile.TryRead(millerDir);
            if (before is not null
                && (current is null
                    || current.Pid != before.Pid
                    || current.StartedAtUtc != before.StartedAtUtc))
            {
                return true;
            }

            Thread.Sleep(TimeSpan.FromMilliseconds(100));
        }

        return false;
    }

    private WorkspaceOperationResult RenderRegistryList(bool json, string? filter, int? limit)
    {
        IReadOnlyList<WorkspaceRegistryRow> rows = _registry.List();
        int activeLimit = limit ?? WorkspaceRender.DefaultListLimit;
        WorkspaceListFacts facts =
            WorkspaceFactsAssembler.ToListFacts(rows, IsCurrentWorkspace, filter, activeLimit);
        BoundedPrefixRender bounded = WorkspaceRender.ListWithinBudget(
            facts,
            json,
            ToolOutputBudget.WorkspaceMcpMaxBytes);
        return new WorkspaceOperationResult(
            bounded.Output,
            bounded.RetainedCount,
            facts.Matched == 0 ? TelemetryOutcome.Empty : TelemetryOutcome.Ok,
            facts.Matched == 0
                ? string.IsNullOrWhiteSpace(filter)
                    ? ToolDiagnostic.ExpectedEmpty(
                        "workspace_list_empty",
                        "No workspaces are registered.",
                        [new ToolDiagnosticAction(
                            "workspace(operation=\"open\", path=\"<project-root>\")",
                            "register and prime a workspace")])
                    : ToolDiagnostic.ExpectedEmpty(
                        "workspace_list_no_matches",
                        "No registered workspace matched the filter.",
                        [new ToolDiagnosticAction(
                            "workspace(operation=\"list\")",
                            "list registered workspaces without a filter")])
                : null);
    }

    // Gather the live facts the status/list views render. Reads the holder (index facts), the workspace context
    // (identity), the indexer (leadership + queue), and the freshness service / probe (revision + freshness).
    private WorkspaceFacts AssembleFacts()
    {
        WorkspaceContext current = CurrentWorkspace;
        IndexHolder holder = CurrentHolder;
        IndexerService indexer = CurrentIndexer;
        FreshnessService freshness = CurrentFreshness;
        IndexFreshProbe freshProbe = CurrentFreshProbe;
        if (TryAssembleCurrentStoreFacts() is { } storeFacts)
            return storeFacts;

        IndexHolderMetadata holderMetadata = holder.MetadataSnapshot();
        (string diskStatus, string? diskWarning) = CurrentIndexDiskStatus();
        bool indexAvailable = diskStatus == "current";
        return new WorkspaceFacts(
            Root: current.WorkspaceRoot,
            WorkspaceId: current.WorkspaceId,
            DbPath: current.ExtractDbPath,
            IsLeader: indexer.IsLeader,
            DocumentCount: holderMetadata.DocumentCount,
            KnownExtensionsCount: holderMetadata.KnownExtensionsCount,
            BuiltRevision: holderMetadata.Revision,
            LatestObservedRevision: freshness.LatestObservedRevision,
            IndexFresh: indexAvailable ? freshProbe.Compute() : false,
            QueueEmpty: indexer.QueueEmpty,
            ArtifactId: CurrentArtifactId(),
            FreshnessStatus: diskStatus,
            WarningText: diskWarning,
            DisplayId: CurrentDisplayId(),
            ServerVersion: MillerVersion.Current,
            ServerProcessId: Environment.ProcessId,
            SearchSidecar: _sidecar.Inspect(current.ExtractDbPath, holderMetadata.Revision),
            ContentCorpus: _contentSidecar.Inspect(current.ExtractDbPath, holderMetadata.Revision),
            Vectors: WorkspaceFactsAssembler.WithPendingFiles(
                _vectors.Inspect(current.WorkspaceRoot),
                current.ExtractDbPath),
            SemanticBroker: CurrentSemanticBrokerFacts(),
            ScanGovernor: WorkspaceFactsAssembler.ScanGovernorFacts(
                ScanGovernorKey.For(current) ?? current.WorkspaceRoot, _governor),
            ScanFailure: WorkspaceFactsAssembler.ScanFailureFacts(current.ExtractDbPath),
            RebindProvenance: WorkspaceFactsAssembler.RebindProvenanceFactsFor(
                current.ExtractDbPath, _registry));
    }

    private WorkspaceFacts? TryAssembleCurrentStoreFacts()
    {
        if (!WorkspaceReadSessionFactory.StoreEnabledFromEnvironment())
            return null;

        WorkspaceContext current = CurrentWorkspace;
        IndexHolder holder = CurrentHolder;
        IndexerService indexer = CurrentIndexer;
        FreshnessService freshness = CurrentFreshness;
        string workspaceId = current.WorkspaceId
            ?? WorkspaceId.FromCanonicalRoot(current.CanonicalRoot ?? current.WorkspaceRoot);
        WorkspaceRegistryRow? row = _registry.Get(workspaceId);
        if (row is null)
            return null;

        WorkspaceFacts facts = WorkspaceFactsAssembler.FromRegisteredRow(
            _registry,
            row,
            WorkspaceRegisteredFactsProfile.McpStatus,
            _sidecar,
            _contentSidecar,
            _vectors,
            CurrentSemanticBrokerFacts(),
            _governor,
            storeEnabled: true);
        IndexHolderMetadata holderMetadata = holder.MetadataSnapshot();
        bool isStoreFacts = facts.Store is not null;
        return facts with
        {
            IsLeader = indexer.IsLeader,
            DocumentCount = holderMetadata.DocumentCount,
            KnownExtensionsCount = holderMetadata.KnownExtensionsCount,
            BuiltRevision = isStoreFacts ? facts.BuiltRevision : holderMetadata.Revision,
            LatestObservedRevision = isStoreFacts ? facts.LatestObservedRevision : freshness.LatestObservedRevision,
            QueueEmpty = indexer.QueueEmpty,
        };
    }

    private SemanticBrokerFacts CurrentSemanticBrokerFacts() =>
        SemanticBrokerFacts.From(_vectors.Mode, _semanticBroker?.BrokerSnapshot);

    private (string Status, string? Warning) CurrentIndexDiskStatus()
    {
        WorkspaceContext current = CurrentWorkspace;
        if (!File.Exists(current.ExtractDbPath))
            return ("missing_index", $"Workspace index DB not found: {current.ExtractDbPath}");

        try
        {
            using var reader = new FreshnessReader(current.ExtractDbPath);
            _ = reader.LatestRevision();
            return ("current", null);
        }
        catch (Exception ex) when (
            ex is FileNotFoundException or IOException or InvalidOperationException or SqliteException)
        {
            return (
                "unreadable_index",
                $"Could not read workspace index DB '{current.ExtractDbPath}': {ex.Message}");
        }
    }

    private string? CurrentArtifactId()
    {
        IndexHolder holder = CurrentHolder;
        WorkspaceContext current = CurrentWorkspace;
        if (!string.IsNullOrWhiteSpace(holder.BuiltArtifactId))
            return holder.BuiltArtifactId;

        try
        {
            using var reader = new FreshnessReader(current.ExtractDbPath);
            return reader.ArtifactId();
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or InvalidOperationException
                                       or SqliteException)
        {
            return null;
        }
    }

    // ---------- refresh / full ----------

    // Reconcile NOW (decision-3): the leader runs an extract scan (delta for refresh, --force for full) then an
    // immediate poll+swap; a non-leader cannot scan (the M3 single-writer guard), so it only polls+swaps to pick
    // up the leader's writes and reports HONESTLY that it could not force a scan here. Either path ends with the
    // in-memory index current without waiting for the 2s loop tick.
    private WorkspaceOperationResult RenderAction(string operation, bool force, bool json)
    {
        IndexerService indexer = CurrentIndexer;
        FreshnessService freshness = CurrentFreshness;
        string? artifactIdBeforeScan = CurrentArtifactId();
        // bypassBackoff: this is a person asking directly, which is not the automatic path the persisted
        // scan-failure backoff exists to throttle. The attempt is still recorded, and it still carries the
        // post-SIGKILL jobs clamp.
        ScanOutcome scan = indexer.TryScanAsLeader(
            force ? ScanIntent.UserFullRebuild : ScanIntent.IncrementalReconcile, bypassBackoff: true);

        string? note = scan.Result switch
        {
            // A leader force-scan that FAILED (full only — refresh delta also surfaces it) must not look like a
            // success: the prior index is kept and the next scan/poll reconciles.
            ScanOutcome.Kind.Failed =>
                $"the {operation} scan failed (the prior index is kept; the watcher/next poll reconciles). " +
                "Check the Miller log for the extract error.",
            // A non-leader cannot force a global rescan — another instance owns the writer lock (decision-8). It
            // still polls+swaps below to converge on whatever the leader has already written.
            ScanOutcome.Kind.NotLeader =>
                "not the indexer leader; cannot force a global rescan here. " +
                "The leader's watcher keeps the index fresh — polled + swapped to pick up its latest writes.",
            ScanOutcome.Kind.Queued => QueuedNote(operation, scan.HolderDescription),
            // A delta ran where a from-scratch rebuild was asked for. Reporting that as a completed rebuild is
            // exactly the lie the third outcome exists to prevent, so it is said out loud and the rebuild stays
            // owed (the indexer re-armed it).
            ScanOutcome.Kind.Downgraded => scan.DowngradeReason,
            _ => null,
        };
        if (note is null && scan.Report is { } report)
            note = ExtractReportLog.DescribeWarning(report);

        // Always poll+swap after the scan attempt so the held index reflects the newest persisted revision NOW
        // (a leader's own scan, or a non-leader picking up the leader's writes). Best-effort; never throws.
        PollResult poll = freshness.PollNow();

        bool downgraded = scan.Result == ScanOutcome.Kind.Downgraded;
        bool scanned = scan.Result == ScanOutcome.Kind.Scanned || downgraded;
        var result = new WorkspaceActionResult(
            operation,
            scanned,
            poll.Swapped,
            poll.Revision,
            note,
            ArtifactId: CurrentArtifactId() ?? artifactIdBeforeScan,
            Downgraded: downgraded);
        return scan.Result switch
        {
            ScanOutcome.Kind.Failed => new WorkspaceOperationResult(
                WorkspaceRender.Action(result, json),
                0,
                TelemetryOutcome.Error,
                ToolDiagnostic.Unavailable(
                    $"workspace_{operation}_failed",
                    $"The current workspace {operation} scan failed.",
                    [new ToolDiagnosticAction(
                        "workspace(operation=\"health\")",
                        "inspect the current workspace before retrying")])),
            ScanOutcome.Kind.NotLeader => new WorkspaceOperationResult(
                WorkspaceRender.Action(result, json),
                0,
                TelemetryOutcome.Empty,
                ToolDiagnostic.Refusal(
                    $"workspace_{operation}_not_leader",
                    $"This Miller process is not the indexer leader and did not run the {operation} scan.",
                    [new ToolDiagnosticAction(
                        "workspace(operation=\"leader\")",
                        "inspect or gracefully hand off indexer leadership")])),
            ScanOutcome.Kind.Queued => new WorkspaceOperationResult(
                WorkspaceRender.Action(result, json),
                0,
                TelemetryOutcome.Empty,
                QueuedRefusal(operation, scan.HolderDescription)),
            ScanOutcome.Kind.Downgraded => new WorkspaceOperationResult(
                WorkspaceRender.Action(result, json),
                0,
                TelemetryOutcome.Empty,
                ToolDiagnostic.Refusal(
                    $"workspace_{operation}_downgraded",
                    $"Repeated whole-repo scan failures downgraded the {operation} scan to a delta reconcile; " +
                    "the prior index is served with degraded freshness and the rebuild is still owed.",
                    [new ToolDiagnosticAction(
                        "workspace(operation=\"health\")",
                        "read scan_failure for the streak and the next attempt time")])),
            _ => new WorkspaceOperationResult(
                WorkspaceRender.Action(result, json),
                1,
                TelemetryOutcome.Ok),
        };
    }

    /// <summary>
    /// The agent-facing text for a scan that was QUEUED rather than run. Deliberately cause-NEUTRAL: machine-wide
    /// scan admission is only one of the things a queued scan can be waiting behind — this instance's own
    /// in-flight extract is another — and <see cref="ScanOutcome.HolderDescription"/> is the member that names
    /// the actual one. Naming a mechanism here instead sent an agent to a <c>scan_governor</c> object that is
    /// absent whenever the wait was not an admission wait.
    /// </summary>
    internal static string QueuedNote(string operation, string? holderDescription) =>
        $"the {operation} scan is queued and will run without a retry (the prior index is kept and still " +
        $"served). {holderDescription}";

    /// <inheritdoc cref="QueuedNote"/>
    internal static ToolDiagnostic QueuedRefusal(string operation, string? holderDescription) =>
        ToolDiagnostic.Refusal(
            $"workspace_{operation}_queued",
            $"{holderDescription} The {operation} scan is queued and will run without a retry.",
            [new ToolDiagnosticAction(
                "workspace(operation=\"status\")",
                "confirm the revision converged; scan_governor appears there only while a scan on this machine " +
                "actually holds or waits on admission")]);

    private WorkspaceOperationResult RenderTargetAction(
        string operation, string? workspaceId, string? path, bool force, bool json)
    {
        TargetWorkspace target = ResolveTarget(workspaceId, path, WorkspaceSelectorIntent.Mutate);
        if (target.UnknownNote is { } note)
            return (Note(note, json), 0, TelemetryOutcome.Empty);

        // An explicit workspace_id that names the bound primary keeps the in-process fast path: this process
        // already holds the primary's index and indexer, so queueing itself a leader request only adds latency.
        if (target.IsCurrent || (target.Row is { } targetRow && IsCurrentWorkspace(targetRow)))
            return RenderAction(operation, force, json);

        // A live MCP call gets the SHORT scan-admission budget, not the operator one: subagents share the lead's
        // Miller connection, so one call stuck behind another workspace's scan would jam the whole fleet.
        WorkspaceRefreshResult refresh = _crossWorkspaceRefresh().Refresh(
            target.WorkspaceId,
            force,
            ScanAdmissionBudget.Of(IndexerService.DefaultScanAdmissionWait),
            bypassBackoff: true);
        WorkspaceRegistryRow row = target.Row
            ?? throw new InvalidOperationException($"Workspace registry row '{target.WorkspaceId}' was not resolved.");
        string? artifactId = refresh.ArtifactId;
        if (string.IsNullOrWhiteSpace(artifactId))
        {
            artifactId = WorkspaceFactsAssembler.FromRegisteredRow(
                _registry,
                row,
                WorkspaceRegisteredFactsProfile.McpStatus,
                _sidecar,
                _contentSidecar).ArtifactId;
        }

        string? noteText = refresh.Error ?? refresh.WarningText;
        var result = new WorkspaceActionResult(
            operation,
            Scanned: refresh.Scanned,
            Swapped: false,
            Revision: refresh.Revision ?? 0,
            Note: noteText,
            WorkspaceId: refresh.WorkspaceId,
            Root: refresh.WorkspaceRoot,
            Status: refresh.StatusText,
            ScanDurationMs: (long?)refresh.ScanDuration?.TotalMilliseconds,
            DurationMs: (long?)refresh.TotalDuration?.TotalMilliseconds,
            ArtifactId: artifactId,
            Sidecars: refresh.Sidecars);
        return refresh.Status switch
        {
            WorkspaceRefreshStatus.Refreshed or WorkspaceRefreshStatus.Unchanged =>
                new WorkspaceOperationResult(
                    WorkspaceRender.Action(result, json),
                    1,
                    TelemetryOutcome.Ok),
            WorkspaceRefreshStatus.LockBusy =>
                new WorkspaceOperationResult(
                    WorkspaceRender.Action(result, json),
                    0,
                    TelemetryOutcome.Empty,
                    ToolDiagnostic.Refusal(
                        $"workspace_{operation}_lock_busy",
                        $"The selected workspace {operation} did not run because another process holds the writer lock.",
                        [new ToolDiagnosticAction(
                            "workspace(operation=\"leader\", workspace_id=\"" + refresh.WorkspaceId + "\")",
                            "inspect the live workspace leader")])),
            WorkspaceRefreshStatus.IneligibleExtractor =>
                new WorkspaceOperationResult(
                    WorkspaceRender.Action(result, json),
                    0,
                    TelemetryOutcome.Empty,
                    ToolDiagnostic.Refusal(
                        $"workspace_{operation}_extractor_ineligible",
                        $"The selected workspace {operation} was refused because this extractor cannot rewrite the artifact.",
                        [new ToolDiagnosticAction(
                            "workspace(operation=\"leader\", workspace_id=\"" + refresh.WorkspaceId + "\")",
                            "inspect extractor leadership compatibility")])),
            WorkspaceRefreshStatus.MissingRoot or WorkspaceRefreshStatus.MissingIndex =>
                new WorkspaceOperationResult(
                    WorkspaceRender.Action(result, json),
                    0,
                    TelemetryOutcome.Error,
                    ToolDiagnostic.Unavailable(
                        $"workspace_{operation}_{refresh.StatusText}",
                        $"The selected workspace {operation} could not run because its root or index is unavailable.",
                        [new ToolDiagnosticAction(
                            "workspace(operation=\"list\")",
                            "inspect registered workspace state")])),
            WorkspaceRefreshStatus.Failed =>
                new WorkspaceOperationResult(
                    WorkspaceRender.Action(result, json),
                    0,
                    TelemetryOutcome.Error,
                    ToolDiagnostic.Unavailable(
                        $"workspace_{operation}_failed",
                        $"The selected workspace {operation} failed.",
                        [new ToolDiagnosticAction(
                            "workspace(operation=\"health\", workspace_id=\"" + refresh.WorkspaceId + "\")",
                            "inspect the selected workspace artifacts")])),
            _ => throw new InvalidOperationException(
                $"Unknown workspace refresh status '{refresh.Status}'."),
        };
    }

    private WorkspaceOperationResult Open(string? path, bool json)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new WorkspaceOperationResult(
                UsageNote("open", json),
                0,
                TelemetryOutcome.Empty,
                ToolDiagnostic.Refusal(
                    "workspace_open_path_required",
                    "workspace open requires a project path."));
        }

        if (!Directory.Exists(path))
        {
            string missingNote = $"cannot prime: no directory at '{path}'.";
            string output = json ? ServerJson.Note(missingNote) : missingNote;
            return new WorkspaceOperationResult(
                output,
                0,
                TelemetryOutcome.Empty,
                ToolDiagnostic.ExpectedEmpty(
                    "workspace_open_path_missing",
                    "The requested workspace path does not exist."));
        }

        string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(path);

        if (WorkspaceRootSafety.IsSensitiveRoot(canonicalRoot, WorkspaceRootSafety.SensitiveRootCandidates()))
        {
            string sensitiveNote =
                $"refusing to prime sensitive system path '{canonicalRoot}': choose a project " +
                "directory or pass a narrower path.";
            string output = json ? ServerJson.Note(sensitiveNote) : sensitiveNote;
            return new WorkspaceOperationResult(
                output,
                0,
                TelemetryOutcome.Empty,
                ToolDiagnostic.Refusal(
                    "workspace_open_refused",
                    "The requested path is a sensitive system root."));
        }

        WorkspaceContext? current = CurrentWorkspaceOrNull;
        if (current is not null && WorkspaceSafety.IsLiveWorkspace(path, current.WorkspaceRoot))
        {
            string liveNote =
                "that path IS the live workspace this process is serving; open does not prime the in-use " +
                "index. Use workspace(operation=\"refresh\") (or \"full\" to force a rebuild) — they reconcile " +
                "it through the indexer leader, keeping every write on the single-writer path.";
            string output = json ? ServerJson.Note(liveNote) : liveNote;
            return new WorkspaceOperationResult(
                output,
                0,
                TelemetryOutcome.Empty,
                ToolDiagnostic.Refusal(
                    "workspace_open_refused",
                    "The requested path is already served by this Miller process."));
        }

        string dbPath = Path.Combine(canonicalRoot, ".miller", "symbols.db");
        string stableWorkspaceId = WorkspaceId.FromCanonicalRoot(canonicalRoot);
        string displayId = WorkspaceId.Display(canonicalRoot, stableWorkspaceId);

        (WorkspaceRegistryRow row, bool created) = _registry.RegisterRefreshing(
            stableWorkspaceId,
            displayId,
            canonicalRoot,
            dbPath,
            lineage: IndexBootstrapService.CaptureLineage(canonicalRoot));
        StampWorkspace(row.WorkspaceId, row.CanonicalRoot);
        WorkspaceOpenPrimeEnqueueResult enqueue = _enqueueOpenPrime(stableWorkspaceId);

        string note;
        ToolDiagnostic? diagnostic = null;
        int resultCount = 1;
        TelemetryOutcome outcome = TelemetryOutcome.Ok;
        if (enqueue is WorkspaceOpenPrimeEnqueueResult.Full or WorkspaceOpenPrimeEnqueueResult.Stopping)
        {
            string reason = enqueue == WorkspaceOpenPrimeEnqueueResult.Full
                ? "The background workspace-open queue is full."
                : "The background workspace-open service is stopping.";
            bool markedError = false;
            if (created)
                (row, markedError) = _registry.MarkErrorIfRefreshing(stableWorkspaceId, reason);
            note = reason + (created && markedError
                ? " The new workspace was recorded as error."
                : " The existing workspace state was preserved.");
            diagnostic = ToolDiagnostic.Unavailable(
                enqueue == WorkspaceOpenPrimeEnqueueResult.Full
                    ? "workspace_open_queue_full"
                    : "workspace_open_stopping",
                reason,
                [new ToolDiagnosticAction(
                    $"workspace(operation=\"status\", workspace_id=\"{stableWorkspaceId}\")",
                    "inspect the registered workspace state")]);
            resultCount = 0;
            outcome = TelemetryOutcome.Error;
        }
        else
        {
            note = enqueue == WorkspaceOpenPrimeEnqueueResult.AlreadyQueued
                ? "background indexing is already queued for this workspace; use workspace status or list to follow it."
                : "workspace registered and queued for background indexing; use workspace status or list to follow it.";
        }

        var result = new WorkspaceActionResult(
            Operation: "open",
            Scanned: false,
            Swapped: false,
            Revision: row.LastRevision ?? 0,
            Note: note,
            WorkspaceId: row.WorkspaceId,
            Root: row.CanonicalRoot,
            Status: row.StateText);
        return new WorkspaceOperationResult(
            WorkspaceRender.Action(result, json),
            resultCount,
            outcome,
            diagnostic);

    }

    // ---------- dashboard ----------

    private (string output, int resultCount, TelemetryOutcome outcome) Dashboard(int? port, bool json)
    {
        int launchPort = port is > 0 and <= 65535 ? port.Value : DashboardCliLauncher.DefaultPort;
        DashboardLaunchResult launch = _dashboardLauncher.EnsureRunning(new DashboardLaunchRequest(
            DashboardContext(),
            launchPort,
            StartupTimeout: TimeSpan.FromSeconds(5),
            OwnVersion: MillerVersion.Current,
            OpenWorkspaceView: CurrentWorkspaceOrNull is not null));
        var result = new WorkspaceDashboardResult(
            DashboardStatus(launch.Outcome),
            launch.Success,
            launch.Url.ToString(),
            launch.ProcessId,
            launch.Message);
        return (
            WorkspaceRender.Dashboard(result, json),
            launch.Success ? 1 : 0,
            launch.Success ? TelemetryOutcome.Ok : TelemetryOutcome.Error);
    }

    private static string DashboardStatus(DashboardLaunchOutcome outcome) => outcome switch
    {
        DashboardLaunchOutcome.AlreadyRunning => "already_running",
        DashboardLaunchOutcome.Started => "started",
        DashboardLaunchOutcome.Replaced => "replaced",
        DashboardLaunchOutcome.Failed => "failed",
        _ => outcome.ToString().ToLowerInvariant(),
    };

    private WorkspaceContext DashboardContext() =>
        CurrentWorkspaceOrNull
        ?? new WorkspaceContext(
            _hostPaths.MillerDirectory,
            Path.Combine(_hostPaths.MillerDirectory, "symbols.db"),
            _hostPaths.TelemetryDbPath,
            _hostPaths.RegistryDbPath,
            _hostPaths.ToolsRoot,
            WorkspaceId: null,
            CanonicalRoot: null,
            CanonicalExtractDbPath: null);

    // ---------- prune ----------

    // Remove registry rows whose canonical_root no longer exists. Never prunes the current workspace row (guarded
    // by workspace_id). Does not open symbols.db. A real prune also runs julie-extract's family-store
    // maintenance, which is the only thing that reclaims the coordinator's terminal request rows.
    private WorkspaceOperationResult Prune(bool json, bool dryRun)
    {
        WorkspaceContext? current = CurrentWorkspaceOrNull;
        WorkspaceRegistryPrune.Result result = WorkspaceRegistryPrune.Run(
            _registry,
            current?.WorkspaceId,
            dryRun,
            maintainStore: StoreMaintenanceRunner.ForToolsRoot(_hostPaths.ToolsRoot),
            retireView: StoreViewRetirementRunner.ForToolsRoot(_hostPaths.ToolsRoot));
        var rendered = new WorkspacePruneResult(
            result.DryRun,
            result.Pruned.Select(e => new WorkspacePruneEntry(e.WorkspaceId, e.DisplayId, e.Root)).ToArray(),
            result.Kept,
            result.SidecarReclaim,
            result.StoreMaintenance,
            result.RetirementFailures
                .Select(e => new WorkspacePruneRetirementFailure(e.WorkspaceId, e.DisplayId, e.Root, e.Outcome))
                .ToArray());
        int count = result.Pruned.Count;
        return new WorkspaceOperationResult(
            WorkspaceRender.PruneWithinBudget(
                rendered,
                json,
                ToolOutputBudget.WorkspaceMcpMaxBytes),
            count,
            count > 0 ? TelemetryOutcome.Ok : TelemetryOutcome.Empty,
            result.RetirementFailures.Count == 0
                ? null
                : ToolDiagnostic.Refusal(
                    "workspace_prune_retirement_failed",
                    "Producer view retirement failed for one or more stale workspaces. Their registry entries were kept; retry after resolving the producer error."));
    }

    // ---------- remove ----------

    // Delete a workspace's `.miller` index dir (decision-1/8). SAFETY: refuse the live workspace (it is in use —
    // a half-delete would corrupt the index this process is serving). A path with no `.miller` dir is a clean
    // not-found (not an error). The is-live decision is the pure WorkspaceSafety predicate (unit-tested).
    private WorkspaceOperationResult Remove(
        string? workspaceId, string? path, bool json)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) && string.IsNullOrWhiteSpace(path))
        {
            return new WorkspaceOperationResult(
                UsageNote("remove", json),
                0,
                TelemetryOutcome.Empty,
                ToolDiagnostic.Refusal(
                    "workspace_remove_selector_required",
                    "workspace remove requires workspace_id or a registered workspace path."));
        }

        WorkspaceRemoveResult result;
        WorkspaceContext? current = CurrentWorkspaceOrNull;
        if (!string.IsNullOrWhiteSpace(workspaceId))
        {
            // Resolving here rather than inside RemoveById means the row is already unambiguous by the time
            // WorkspaceRemoval sees it, so its own Mutate guard can never fire. The intent has to travel.
            TargetWorkspace target = ResolveTarget(workspaceId, path: null, WorkspaceSelectorIntent.Mutate);
            if (target.UnknownNote is { } note)
                return (Note(note, json), 0, TelemetryOutcome.Empty);

            result = target.IsCurrent
                ? WorkspaceRemoveResult.RefusedLive(
                    Path.GetDirectoryName(current?.ExtractDbPath ?? string.Empty) ?? string.Empty,
                    current?.WorkspaceId,
                    current?.CanonicalRoot ?? current?.WorkspaceRoot ?? string.Empty)
                : WorkspaceRemoval.RemoveById(
                    _registry,
                    target.WorkspaceId,
                    liveRoot: current?.WorkspaceRoot,
                    protectedMillerDir: _hostPaths.MillerDirectory,
                    acquireWriterLock: _acquireWriterLock,
                    retireView: StoreViewRetirementRunner.ForToolsRoot(_hostPaths.ToolsRoot));
        }
        else
        {
            result = WorkspaceRemoval.RemoveByPath(
                _registry,
                path!,
                liveRoot: current?.WorkspaceRoot,
                protectedMillerDir: _hostPaths.MillerDirectory,
                acquireWriterLock: _acquireWriterLock,
                retireView: StoreViewRetirementRunner.ForToolsRoot(_hostPaths.ToolsRoot));
        }

        StampWorkspace(result.WorkspaceId, result.Root);

        return RemoveResult(result, json);
    }

    private static WorkspaceOperationResult RemoveResult(WorkspaceRemoveResult result, bool json) =>
        result.Result switch
        {
            WorkspaceRemoveResult.Outcome.Removed => new WorkspaceOperationResult(
                WorkspaceRender.Remove(result, json),
                1,
                TelemetryOutcome.Ok),
            WorkspaceRemoveResult.Outcome.NotFound => new WorkspaceOperationResult(
                WorkspaceRender.Remove(result, json),
                0,
                TelemetryOutcome.Empty,
                ToolDiagnostic.ExpectedEmpty(
                    "workspace_remove_not_found",
                    "No registered workspace index matched the removal target.")),
            WorkspaceRemoveResult.Outcome.RefusedLive => new WorkspaceOperationResult(
                WorkspaceRender.Remove(result, json),
                0,
                TelemetryOutcome.Empty,
                ToolDiagnostic.Refusal(
                    "workspace_remove_live",
                    "The workspace removal target is served by this Miller process.")),
            WorkspaceRemoveResult.Outcome.RefusedInUse => new WorkspaceOperationResult(
                WorkspaceRender.Remove(result, json),
                0,
                TelemetryOutcome.Empty,
                ToolDiagnostic.Refusal(
                    "workspace_remove_in_use",
                    "Another Miller writer is using the workspace removal target.")),
            WorkspaceRemoveResult.Outcome.RefusedSensitive => new WorkspaceOperationResult(
                WorkspaceRender.Remove(result, json),
                0,
                TelemetryOutcome.Empty,
                ToolDiagnostic.Refusal(
                    "workspace_remove_sensitive",
                    "The workspace removal target is a sensitive or machine-global Miller directory.")),
            WorkspaceRemoveResult.Outcome.RefusedInvalidRegistration => new WorkspaceOperationResult(
                WorkspaceRender.Remove(result, json),
                0,
                TelemetryOutcome.Empty,
                ToolDiagnostic.Refusal(
                    "workspace_remove_invalid_registration",
                    "The workspace registry entry does not map to its canonical index directory.")),
            WorkspaceRemoveResult.Outcome.RefusedRetirement => new WorkspaceOperationResult(
                WorkspaceRender.Remove(result, json),
                0,
                TelemetryOutcome.Empty,
                ToolDiagnostic.Refusal(
                    "workspace_remove_retirement_failed",
                    "The producer family-store view could not be retired. The registry entry was kept for retry.")),
            _ => throw new InvalidOperationException(
                $"Unknown workspace removal outcome '{result.Result}'."),
        };

    private TargetWorkspace ResolveTarget(
        string? workspaceId,
        string? path,
        WorkspaceSelectorIntent intent)
    {
        TargetWorkspace target;
        if (!string.IsNullOrWhiteSpace(workspaceId))
        {
            if (IsCurrentSelector(workspaceId))
            {
                target = CurrentTarget();
            }
            else
            {
                try
                {
                    WorkspaceRegistryRow row = WorkspaceRegistrySelector.Resolve(_registry, workspaceId, intent);
                    target = TargetWorkspace.Registered(row, isCurrent: false);
                }
                catch (KeyNotFoundException ex)
                {
                    target = TargetWorkspace.Unknown(ex.Message);
                }
            }
        }
        else if (!string.IsNullOrWhiteSpace(path))
        {
            WorkspaceContext? current = CurrentWorkspaceOrNull;
            if (current is not null && WorkspaceSafety.IsLiveWorkspace(path, current.WorkspaceRoot))
                target = TargetWorkspace.Current(current.WorkspaceId);
            else if (!Directory.Exists(path))
                target = TargetWorkspace.Unknown(UnknownWorkspacePathNote(path));
            else
            {
                string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(path);
                WorkspaceRegistryRow? row = FindByCanonicalRoot(canonicalRoot);
                target = row is null
                    ? TargetWorkspace.Unknown(UnknownWorkspacePathNote(canonicalRoot))
                    : TargetWorkspace.Registered(row, IsCurrentWorkspace(row));
            }
        }
        else
        {
            target = CurrentTarget();
        }

        StampTarget(target);
        return target;
    }

    private void StampTarget(TargetWorkspace target)
    {
        if (target.UnknownNote is not null)
            return;

        StampWorkspace(
            target.WorkspaceId,
            target.Row?.CanonicalRoot
                ?? (target.IsCurrent
                    ? CurrentWorkspaceOrNull?.CanonicalRoot ?? CurrentWorkspaceOrNull?.WorkspaceRoot
                    : null));
    }

    private static void StampWorkspace(string? workspaceId, string? workspaceRoot)
    {
        if (!string.IsNullOrWhiteSpace(workspaceId)
            && !string.IsNullOrWhiteSpace(workspaceRoot))
        {
            TelemetryContext.Current?.SetWorkspace(workspaceId, workspaceRoot);
        }
    }

    private WorkspaceRegistryRow? FindByCanonicalRoot(string canonicalRoot)
    {
        IReadOnlyList<WorkspaceRegistryRow> rows = _registry.List();
        return WorkspaceRegistryRootMatcher.FindByRoot(rows, canonicalRoot);
    }

    private WorkspaceContext? CurrentWorkspaceOrNull =>
        _workspace ?? (_primary?.IsBound == true ? _primary.Workspace : null);

    private WorkspaceContext CurrentWorkspace =>
        CurrentWorkspaceOrNull
        ?? throw new InvalidOperationException("A primary workspace is not bound.");

    private IndexHolder CurrentHolder =>
        _holder ?? (_primary?.IsBound == true ? _primary.Holder : null)
        ?? throw new InvalidOperationException("A primary workspace is not bound.");

    private IndexerService CurrentIndexer =>
        _indexer ?? throw new InvalidOperationException("A primary workspace indexer is not available.");

    private FreshnessService CurrentFreshness =>
        _freshness ?? throw new InvalidOperationException("A primary workspace freshness service is not available.");

    private IndexFreshProbe CurrentFreshProbe
    {
        get
        {
            if (_freshProbe is not null)
                return _freshProbe;
            if (_freshness is null || _indexer is null)
                throw new InvalidOperationException("A primary workspace freshness probe is not available.");

            return new IndexFreshProbe(
                CurrentHolder,
                () => _freshness.LatestObservedRevision,
                () => _indexer.QueueEmpty);
        }
    }

    private BootstrapSnapshot CurrentBootstrapSnapshot =>
        _bootstrap?.Snapshot
        ?? throw new InvalidOperationException("A primary workspace bootstrap is not available.");

    private TargetWorkspace CurrentTarget()
    {
        WorkspaceContext? current = CurrentWorkspaceOrNull;
        return current is null
            ? TargetWorkspace.Unknown(
                "no primary workspace is bound; pass a registered workspace_id for this operation")
            : TargetWorkspace.Current(current.WorkspaceId);
    }

    private bool IsCurrentWorkspace(WorkspaceRegistryRow row) =>
        CurrentWorkspaceOrNull is { } current
        && (string.Equals(row.WorkspaceId, current.WorkspaceId, StringComparison.Ordinal)
            || WorkspaceSafety.IsLiveWorkspace(row.CanonicalRoot, current.WorkspaceRoot));

    private bool IsCurrentSelector(string selector)
    {
        string trimmed = selector.Trim();
        return string.Equals(trimmed, "current", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "primary", StringComparison.OrdinalIgnoreCase);
    }

    private string? CurrentDisplayId()
    {
        WorkspaceContext? current = CurrentWorkspaceOrNull;
        if (string.IsNullOrWhiteSpace(current?.WorkspaceId))
            return null;

        string root = current.CanonicalRoot ?? current.WorkspaceRoot;
        try
        {
            return WorkspaceId.Display(root, current.WorkspaceId!);
        }
        catch (ArgumentException)
        {
            return current.WorkspaceId;
        }
    }

    private void VerifyRegisteredRoot(WorkspaceRegistryRow row)
    {
        if (!Directory.Exists(row.CanonicalRoot))
        {
            string error = $"Workspace root not found: {row.CanonicalRoot}";
            _registry.MarkMissing(row.WorkspaceId, error);
            throw new ToolDiagnosticException(ToolDiagnostic.Refusal(
                "workspace_root_missing",
                "The selected registered workspace root no longer exists.",
                [
                    new ToolDiagnosticAction(
                        "workspace(operation=\"prune\", dry_run=true)",
                        "preview stale registry cleanup"),
                    new ToolDiagnosticAction(
                        "workspace(operation=\"remove\", workspace_id=\"" + row.DisplayId + "\")",
                        "remove the stale registry entry"),
                ]));
        }

        try
        {
            WorkspaceRootSafety.RejectSensitiveRoot(row.CanonicalRoot, fromCwd: false);
        }
        catch (InvalidOperationException ex)
        {
            _registry.MarkError(row.WorkspaceId, ex.Message);
            throw new ToolDiagnosticException(ToolDiagnostic.Refusal(
                "sensitive_workspace_root",
                "The selected registry row points at a sensitive system root."));
        }
    }

    private static string UnknownWorkspacePathNote(string path) =>
        $"unknown workspace path '{path}'. Run workspace(operation=\"open\", path=\"{path}\") " +
        "to register it first.";

    private static string Note(string message, bool json) =>
        json ? ServerJson.Note(message) : message;

    private readonly record struct WorkspaceOperationResult(
        string Output,
        int ResultCount,
        TelemetryOutcome Outcome,
        ToolDiagnostic? Diagnostic = null,
        string? Hint = null)
    {
        public static implicit operator WorkspaceOperationResult(
            (string Output, int ResultCount, TelemetryOutcome Outcome) value) =>
            new(value.Output, value.ResultCount, value.Outcome);
    }

    private sealed record TargetWorkspace(
        bool IsCurrent,
        string WorkspaceId,
        WorkspaceRegistryRow? Row,
        string? UnknownNote)
    {
        public static TargetWorkspace Current(string? workspaceId) =>
            new(IsCurrent: true, workspaceId ?? string.Empty, Row: null, UnknownNote: null);

        public static TargetWorkspace Registered(WorkspaceRegistryRow row, bool isCurrent) =>
            new(isCurrent, row.WorkspaceId, row, UnknownNote: null);

        public static TargetWorkspace Unknown(string note) =>
            new(IsCurrent: false, WorkspaceId: string.Empty, Row: null, UnknownNote: note);
    }

    // ---------- usage ----------

    // A clear usage message for a missing required arg / an unknown operation. Rendered as a note (Empty), never
    // an error — guidance, not a fault (mirrors the other tools' usage convention).
    private static string UsageNote(string operation, bool json)
    {
        string message = operation switch
        {
            "open" => "workspace open requires a path: workspace(operation=\"open\", path=\"/repo\"). " +
                      "It registers and queues background indexing — not a live switch.",
            "remove" => "workspace remove requires a path: workspace(operation=\"remove\", path=\"/repo\"). " +
                        "It deletes that path's .miller index dir (the live workspace is refused).",
            _ => $"unknown workspace operation '{operation}'. " +
                 "Use status|refresh|full|list|open|remove|prune|health|onboarding|leader|dashboard (default status).",
        };
        return json ? ServerJson.Note(message) : message;
    }

    private static WorkspaceExtractionHealthFacts UnavailableExtraction(string error) => new(
        ParseDiagnostics: HealthFactSection<ParseDiagnosticGroup>.Unavailable(error),
        CapabilityGaps: HealthFactSection<CapabilityGapGroup>.Unavailable(error),
        LanguageCapabilities: HealthFactSection<LanguageCapabilitySummary>.Unavailable(error),
        StructuralFacts: HealthFactSection<StructuralFactGroup>.Unavailable(error),
        ComplexityMetrics: HealthFactSection<ComplexityMetricGroup>.Unavailable(error),
        Files: HealthFactSection<FileStatusGroup>.Unavailable(error));

    private static WorkspaceExtractionHealthFacts ReadExtractionHealth(
        string dbPath,
        string workspaceRoot,
        string? workspaceId)
    {
        using WorkspaceReadHandle session = WorkspaceReadSessionFactory.Open(dbPath, workspaceRoot, workspaceId);
        return WorkspaceHealthReader.Read(session);
    }

    private static bool IsHealthIndexReadException(Exception ex) =>
        ex is SqliteException or InvalidOperationException;
}
