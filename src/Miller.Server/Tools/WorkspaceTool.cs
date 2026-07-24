using System.ComponentModel;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Miller.Indexing;
using Miller.Server.Cli;
using Miller.Server.Hosting;
using Miller.Server.Logging;
using Miller.Server.Telemetry;
using Miller.Server.Workspaces;
using ModelContextProtocol.Server;

namespace Miller.Server.Tools;

/// <summary>
/// The <c>workspace</c> tool (miller-toolbox.md §7, M7 decision-1): admin / index lifecycle. It defaults to
/// <c>status</c> (the 80% call — workspace identity + index facts + the telemetry tool-breakdown), and exposes
/// <c>refresh</c>/<c>full</c> (reconcile / from-scratch rebuild, routed through the single-writer leader so two
/// Miller instances never both <c>extract scan</c>), <c>list</c> (the current workspace — Miller serves ONE per
/// process; a multi-workspace registry is eros/commercial-tier, decision-1), <c>open(path)</c> (PRIME a path's
/// index via an extract scan so a future Miller there is warm — NOT a live switch), <c>remove(path)</c>
/// (delete a workspace's <c>.miller</c> index dir, REFUSING the live one), and <c>dashboard</c> (start/reuse the
/// local loopback transparency dashboard from an MCP session).
///
/// <para>The pure renderers (<see cref="WorkspaceRender"/>) and the remove-safety predicate
/// (<see cref="WorkspaceSafety"/>) carry the formatting + safety logic and are unit-tested; this class
/// orchestrates the live singletons (the holder, the indexer leader, the freshness poller, the extract runner)
/// and the telemetry shell. Every operation reports HONESTLY what happened — a non-leader that cannot force a
/// scan, a scan failure, a refused remove — never a faked success. Mirrors the other tools' shape:
/// ctor-injected singletons, a thin <see cref="Workspace"/> dispatch with typed diagnostics, and telemetry via
/// <see cref="TelemetryContext.Current"/>.</para>
/// </summary>
[McpServerToolType]
public sealed class WorkspaceTool
{
    private readonly IndexHolder _holder;
    private readonly WorkspaceContext _workspace;
    private readonly IndexerService _indexer;
    private readonly FreshnessService _freshness;
    private readonly IndexFreshProbe _freshProbe;
    private readonly IndexBootstrapService _bootstrap;
    private readonly TelemetryLedger _ledger;
    private readonly WorkspaceRegistry _registry;
    private readonly CrossWorkspaceRefreshService _crossWorkspaceRefresh;
    private readonly SymbolSearchSidecar _sidecar;
    private readonly ContentCorpusSidecar _contentSidecar = new();
    private readonly VectorSidecar _vectors;
    private readonly Func<string, string, bool, ExtractReport> _scanForOpen;
    private readonly Func<string, IDisposable?> _acquireWriterLock;
    private readonly IDashboardLauncher _dashboardLauncher;
    private readonly ILogger<WorkspaceTool> _logger;

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
        JulieExtractRunner runner,
        WorkspaceRegistry registry,
        CrossWorkspaceRefreshService crossWorkspaceRefresh,
        SymbolSearchSidecar sidecar,
        VectorSidecar vectors,
        ILogger<WorkspaceTool> logger)
        : this(
            holder,
            workspace,
            indexer,
            freshness,
            freshProbe,
            bootstrap,
            ledger,
            runner,
            registry,
            crossWorkspaceRefresh,
            sidecar,
            vectors,
            (root, db, force) => runner.Scan(root, db, force),
            millerDir => SingleWriterLock.TryAcquire(millerDir),
            new DashboardCliLauncher(),
            logger)
    {
    }

    internal WorkspaceTool(
        IndexHolder holder,
        WorkspaceContext workspace,
        IndexerService indexer,
        FreshnessService freshness,
        IndexFreshProbe freshProbe,
        IndexBootstrapService bootstrap,
        TelemetryLedger ledger,
        JulieExtractRunner runner,
        WorkspaceRegistry registry,
        CrossWorkspaceRefreshService crossWorkspaceRefresh,
        SymbolSearchSidecar sidecar,
        VectorSidecar vectors,
        Func<string, string, bool, ExtractReport> scanForOpen,
        Func<string, IDisposable?> acquireWriterLock,
        IDashboardLauncher dashboardLauncher,
        ILogger<WorkspaceTool> logger)
    {
        ArgumentNullException.ThrowIfNull(holder);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(indexer);
        ArgumentNullException.ThrowIfNull(freshness);
        ArgumentNullException.ThrowIfNull(freshProbe);
        ArgumentNullException.ThrowIfNull(bootstrap);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(crossWorkspaceRefresh);
        ArgumentNullException.ThrowIfNull(sidecar);
        ArgumentNullException.ThrowIfNull(vectors);
        ArgumentNullException.ThrowIfNull(scanForOpen);
        ArgumentNullException.ThrowIfNull(acquireWriterLock);
        ArgumentNullException.ThrowIfNull(dashboardLauncher);
        ArgumentNullException.ThrowIfNull(logger);
        _holder = holder;
        _workspace = workspace;
        _indexer = indexer;
        _freshness = freshness;
        _freshProbe = freshProbe;
        _bootstrap = bootstrap;
        _ledger = ledger;
        _registry = registry;
        _crossWorkspaceRefresh = crossWorkspaceRefresh;
        _sidecar = sidecar;
        _vectors = vectors;
        _scanForOpen = scanForOpen;
        _acquireWriterLock = acquireWriterLock;
        _dashboardLauncher = dashboardLauncher;
        _logger = logger;
    }

    [McpServerTool(Name = "workspace")]
    [Description(
        "Manage the workspace index. Defaults to status (freshness, revision, leader). refresh updates stale " +
        "files; full forces a rebuild; health reports readiness + extraction quality; onboarding gives " +
        "telemetry-derived guidance for this repo; list shows registered workspaces (filter/limit, " +
        "recency-ordered); open registers another repo for cross-workspace reads; prune removes registry rows " +
        "whose roots are gone; leader diagnoses/hands off the indexer lock; dashboard starts/opens the local " +
        "dashboard. Use when results look stale, before cross-workspace queries, or at session start (onboarding). " +
        "NOT for: reading code (search/inspect). Example: workspace operation=list filter=eros limit=10.")]
    public string Workspace(
        [Description("status|refresh|full|list|open|remove|prune|health|onboarding|leader|dashboard. Default status.")] string operation = "status",
        [Description("Workspace selector: display_id, unique prefix, full id, registered root path, current, or primary.")]
        string? workspace_id = null,
        [Description("A workspace root path. Required for open; optional for status/health/onboarding/refresh/full/remove.")]
        string? path = null,
        [Description("Output format: compact|json|markdown. Default compact.")] string format = "compact",
        [Description("Dashboard launch port. Used only with operation=dashboard when no dashboard is already running.")]
        int? port = null,
        [Description("For operation=leader, queue an explicit graceful leadership handoff request.")]
        bool handoff = false,
        [Description("For operation=leader with handoff=true, wait briefly for the live leader to observe the request.")]
        bool wait = false,
        [Description("operation=list only: case-insensitive substring filter on display id or root, applied before the cap. Default null (no filter).")]
        string? filter = null,
        [Description("operation=list only: max compact entries before the omitted-count tail. Default 20; <=0 unlimited. JSON is unlimited unless set to a positive value.")]
        int? limit = null,
        [Description("operation=prune only: list candidates without removing registry rows. Default false.")]
        bool dry_run = false)
    {
        var telemetry = TelemetryContext.Current;
        // D7: stamp the operation sub-axis onto the ambient scope so the central filter's row records
        // op=<operation> (status/refresh/full/list/open/remove) instead of NULL — workspace is in the
        // tool-breakdown WITH its per-operation axis. Normalised to lowercase to match the dispatch keys.
        if (telemetry is not null)
            telemetry.Op = (operation ?? "status").ToLowerInvariant();
        bool json = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);
        WorkspaceHealthFormat healthFormat = json
            ? WorkspaceHealthFormat.Json
            : string.Equals(format, "markdown", StringComparison.OrdinalIgnoreCase)
                ? WorkspaceHealthFormat.Markdown
                : WorkspaceHealthFormat.Compact;
        try
        {
            (string output, int resultCount, TelemetryOutcome outcome) = Dispatch(
                operation, workspace_id, path, port, json, healthFormat, handoff, wait, filter, limit, dry_run);

            if (telemetry is not null)
            {
                telemetry.ResultCount = resultCount;
                telemetry.Outcome = outcome;
            }
            if (outcome is TelemetryOutcome.Empty or TelemetryOutcome.Error)
            {
                output = ToolDiagnosticRenderer.Attach(
                    "workspace",
                    output,
                    WorkspaceDiagnostic(operation, outcome, output),
                    json,
                    telemetry);
            }
            return output;
        }
        catch (Exception ex)
        {
            ToolDiagnostic diagnostic = ToolDiagnostic.FromException(ex);
            if (diagnostic.Outcome == ToolDiagnosticOutcome.Error)
                telemetry?.SetError(ex);
            _logger.LogError(ex, "workspace {Operation} failed.", operation);
            return ToolDiagnosticRenderer.Render(
                "workspace",
                diagnostic,
                json,
                telemetry);
        }
    }

    private static ToolDiagnostic WorkspaceDiagnostic(
        string? operation,
        TelemetryOutcome outcome,
        string output)
    {
        string op = string.IsNullOrWhiteSpace(operation) ? "status" : operation.Trim().ToLowerInvariant();
        if (outcome == TelemetryOutcome.Error)
            return ToolDiagnostic.Unavailable(
                $"workspace_{op}_failed",
                $"Workspace operation '{op}' could not complete.");

        bool supported = op is "status" or "health" or "onboarding" or "leader" or "list" or
            "refresh" or "full" or "open" or "remove" or "prune" or "dashboard";
        bool refused = output.Contains("refus", StringComparison.OrdinalIgnoreCase) ||
            output.Contains(" requires ", StringComparison.OrdinalIgnoreCase) ||
            (op == "open" &&
             (output.Contains("does not prime", StringComparison.OrdinalIgnoreCase) ||
              output.Contains("not priming", StringComparison.OrdinalIgnoreCase)));
        if (supported && refused)
        {
            return ToolDiagnostic.Refusal(
                $"workspace_{op}_refused",
                $"Workspace operation '{op}' was refused by a safety or input constraint.");
        }
        return supported
            ? ToolDiagnostic.ExpectedEmpty(
                $"workspace_{op}_empty",
                $"Workspace operation '{op}' returned no result.")
            : ToolDiagnostic.Unsupported(
                "unsupported_operation",
                $"Workspace operation '{op}' is not supported.",
                [new ToolDiagnosticAction("workspace(operation=\"status\")", "show current workspace status")]);
    }

    // The pure-ish dispatch: route the operation to its handler, returning the rendered output, a result count
    // (for the telemetry KPI), and the outcome. An unknown operation is a usage note (Empty, not an error).
    private (string output, int resultCount, TelemetryOutcome outcome) Dispatch(
        string? operation, string? workspaceId, string? path, int? port, bool json,
        WorkspaceHealthFormat healthFormat, bool handoff, bool wait,
        string? filter = null, int? limit = null, bool dryRun = false)
    {
        switch (operation?.ToLowerInvariant())
        {
            case null or "" or "status":
                return RenderTargetStatus(workspaceId, path, json);
            case "health":
                return RenderTargetHealth(workspaceId, path, json, healthFormat);
            case "onboarding":
                return RenderTargetOnboarding(workspaceId, path, json);
            case "leader":
                return RenderTargetLeader(workspaceId, path, json, handoff, wait);
            case "list":
                return (RenderRegistryList(json, filter, limit), _registry.List().Count, TelemetryOutcome.Ok);
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
                return (UsageNote(operation!, json), 0, TelemetryOutcome.Empty);
        }
    }

    // ---------- status / list facts ----------

    private string RenderStatus(bool json) =>
        WorkspaceRender.Status(
            AssembleFacts(),
            _ledger.Summarize(),
            json,
            ReadLeaderFacts(_workspace.ExtractDbPath, ownWorkspace: true),
            _bootstrap.Snapshot);

    private string RenderHealth(WorkspaceHealthFormat format)
    {
        WorkspaceHealthFacts health = WorkspaceHealthFacts.Create(
            AssembleFacts(),
            _ledger.Summarize(),
            _ledger.SummarizeOutcomes(),
            WorkspaceHealthReader.Read(_workspace.ExtractDbPath),
            ReadLeaderFacts(_workspace.ExtractDbPath, ownWorkspace: true),
            ReadHistoryStatus(_workspace.ExtractDbPath));
        return WorkspaceRender.Health(health, format);
    }

    private string RenderOnboarding(bool json)
    {
        WorkspaceFacts facts = AssembleFacts();
        WorkspaceOnboardingFacts onboarding = WorkspaceOnboardingAssembler.Create(
            facts,
            _ledger.DbPath,
            facts.WorkspaceId,
            facts.DbPath);
        return WorkspaceRender.Onboarding(onboarding, json);
    }

    // Leader facts enriched with the version-aware-leadership view (D6): the recorded identity + liveness, this
    // process's probed extractor version, and the artifact's recorded binary_version. The IndexerService verdict
    // applies only to OUR workspace's artifact, so for a cross-workspace target it stays null (this process is
    // not a writer candidate there — its eligibility says nothing about that workspace's convergence).
    // Best-effort status of the workspace's append-only metric history sidecar (sibling of symbols.db). Never
    // throws — a missing/unreadable history.db degrades to an absent-present status the health surface renders.
    private static MetricHistoryStatus ReadHistoryStatus(string indexDbPath) =>
        MetricHistoryStore.ReadStatus(MetricSnapshotAggregates.HistoryDbPathFor(indexDbPath));

    private LeaderHealthFacts ReadLeaderFacts(string indexDbPath, bool ownWorkspace) =>
        LeaderHealthFacts.Read(Path.GetDirectoryName(indexDbPath)!) with
        {
            OwnExtractorVersion = _indexer.OwnExtractorVersion,
            ArtifactExtractorVersion = ExtractBinaryVersionReader.TryRead(indexDbPath),
            OwnVerdict = ownWorkspace ? _indexer.EligibilityVerdict : null,
        };

    private (string output, int resultCount, TelemetryOutcome outcome) RenderTargetStatus(
        string? workspaceId, string? path, bool json)
    {
        TargetWorkspace target = ResolveTarget(workspaceId, path);
        if (target.UnknownNote is { } note)
            return (Note(note, json), 0, TelemetryOutcome.Empty);

        if (target.IsCurrent)
            return (RenderStatus(json), 1, TelemetryOutcome.Ok);

        WorkspaceRegistryRow row = target.Row
            ?? throw new InvalidOperationException($"Workspace registry row '{target.WorkspaceId}' was not resolved.");
        VerifyRegisteredRoot(row);

        WorkspaceFacts facts = WorkspaceFactsAssembler.FromRegisteredRow(
            _registry,
            row,
            WorkspaceRegisteredFactsProfile.McpStatus,
            _sidecar,
            _contentSidecar);
        TelemetryOutcome outcome = string.Equals(facts.FreshnessStatus, "missing_index", StringComparison.Ordinal)
            ? TelemetryOutcome.Empty
            : TelemetryOutcome.Ok;
        return (WorkspaceRender.Status(facts, _ledger.SummarizeForWorkspace(row.WorkspaceId), json),
            1, outcome);
    }

    private (string output, int resultCount, TelemetryOutcome outcome) RenderTargetHealth(
        string? workspaceId, string? path, bool json, WorkspaceHealthFormat format)
    {
        TargetWorkspace target = ResolveTarget(workspaceId, path);
        if (target.UnknownNote is { } note)
            return (Note(note, json), 0, TelemetryOutcome.Empty);

        if (target.IsCurrent)
            return (RenderHealth(format), 1, TelemetryOutcome.Ok);

        WorkspaceRegistryRow row = target.Row
            ?? throw new InvalidOperationException($"Workspace registry row '{target.WorkspaceId}' was not resolved.");
        VerifyRegisteredRoot(row);
        WorkspaceFacts statusFacts = WorkspaceFactsAssembler.FromRegisteredRow(
            _registry,
            row,
            WorkspaceRegisteredFactsProfile.McpHealth,
            _sidecar,
            _contentSidecar);
        WorkspaceExtractionHealthFacts extraction;
        if (statusFacts.FreshnessStatus is "missing_index" or "unreadable_index")
        {
            extraction = UnavailableExtraction(statusFacts.WarningText ?? statusFacts.FreshnessStatus);
        }
        else
        {
            try
            {
                extraction = WorkspaceHealthReader.Read(row.IndexDbPath);
            }
            catch (Exception ex) when (IsHealthIndexReadException(ex))
            {
                statusFacts = WorkspaceFactsAssembler.FromRegisteredHealthReadError(
                    _registry,
                    row,
                    WorkspaceRegisteredFactsProfile.McpHealth,
                    _sidecar,
                    _contentSidecar,
                    ex);
                extraction = UnavailableExtraction(statusFacts.WarningText ?? ex.Message);
            }
        }

        WorkspaceHealthFacts health = WorkspaceHealthFacts.Create(
            statusFacts,
            _ledger.SummarizeForWorkspace(row.WorkspaceId),
            _ledger.SummarizeOutcomesForWorkspace(row.WorkspaceId),
            extraction,
            ReadLeaderFacts(row.IndexDbPath, ownWorkspace: false),
            ReadHistoryStatus(row.IndexDbPath));
        return (WorkspaceRender.Health(health, format), 1, TelemetryOutcome.Ok);
    }

    private (string output, int resultCount, TelemetryOutcome outcome) RenderTargetOnboarding(
        string? workspaceId, string? path, bool json)
    {
        TargetWorkspace target = ResolveTarget(workspaceId, path);
        if (target.UnknownNote is { } note)
            return (Note(note, json), 0, TelemetryOutcome.Empty);

        if (target.IsCurrent)
            return (RenderOnboarding(json), 1, TelemetryOutcome.Ok);

        WorkspaceRegistryRow row = target.Row
            ?? throw new InvalidOperationException($"Workspace registry row '{target.WorkspaceId}' was not resolved.");
        VerifyRegisteredRoot(row);
        WorkspaceFacts statusFacts = WorkspaceFactsAssembler.FromRegisteredRow(
            _registry,
            row,
            WorkspaceRegisteredFactsProfile.McpHealth,
            _sidecar,
            _contentSidecar);
        WorkspaceOnboardingFacts onboarding = WorkspaceOnboardingAssembler.Create(
            statusFacts,
            _ledger.DbPath,
            row.WorkspaceId,
            row.IndexDbPath);
        TelemetryOutcome outcome = onboarding.Telemetry.TotalCalls == 0
            ? TelemetryOutcome.Empty
            : TelemetryOutcome.Ok;
        return (WorkspaceRender.Onboarding(onboarding, json), 1, outcome);
    }

    private (string output, int resultCount, TelemetryOutcome outcome) RenderTargetLeader(
        string? workspaceId, string? path, bool json, bool handoff, bool wait)
    {
        TargetWorkspace target = ResolveTarget(workspaceId, path);
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
        return (WorkspaceRender.Leader(result, json), 1, TelemetryOutcome.Ok);
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

    private string RenderRegistryList(bool json, string? filter, int? limit)
    {
        IReadOnlyList<WorkspaceRegistryRow> rows = _registry.List();
        int? activeLimit = limit ?? (json ? null : WorkspaceRender.DefaultListLimit);
        WorkspaceListFacts facts =
            WorkspaceFactsAssembler.ToListFacts(rows, IsCurrentWorkspace, filter, activeLimit);
        return WorkspaceRender.List(facts, json);
    }

    // Gather the live facts the status/list views render. Reads the holder (index facts), the workspace context
    // (identity), the indexer (leadership + queue), and the freshness service / probe (revision + freshness).
    private WorkspaceFacts AssembleFacts()
    {
        var (index, builtRevision) = _holder.Snapshot();
        return new WorkspaceFacts(
            Root: _workspace.WorkspaceRoot,
            WorkspaceId: _workspace.WorkspaceId,
            DbPath: _workspace.ExtractDbPath,
            IsLeader: _indexer.IsLeader,
            DocumentCount: index.DocumentCount,
            KnownExtensionsCount: index.KnownExtensions.Count,
            BuiltRevision: builtRevision,
            LatestObservedRevision: _freshness.LatestObservedRevision,
            IndexFresh: _freshProbe.Compute(),
            QueueEmpty: _indexer.QueueEmpty,
            ArtifactId: CurrentArtifactId(),
            FreshnessStatus: "current",
            DisplayId: CurrentDisplayId(),
            ServerVersion: MillerVersion.Current,
            ServerProcessId: Environment.ProcessId,
            SearchSidecar: _sidecar.Inspect(_workspace.ExtractDbPath, builtRevision),
            ContentCorpus: _contentSidecar.Inspect(_workspace.ExtractDbPath, builtRevision),
            Vectors: WorkspaceFactsAssembler.WithPendingFiles(
                _vectors.Inspect(_workspace.WorkspaceRoot),
                _workspace.ExtractDbPath));
    }

    private string? CurrentArtifactId()
    {
        if (!string.IsNullOrWhiteSpace(_holder.BuiltArtifactId))
            return _holder.BuiltArtifactId;

        try
        {
            using var reader = new FreshnessReader(_workspace.ExtractDbPath);
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
    private string RenderAction(string operation, bool force, bool json)
    {
        string? artifactIdBeforeScan = CurrentArtifactId();
        ScanOutcome scan = _indexer.TryScanAsLeader(force);

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
            _ => null,
        };
        if (note is null && scan.Report is { } report)
            note = ExtractReportLog.DescribeWarning(report);

        // Always poll+swap after the scan attempt so the held index reflects the newest persisted revision NOW
        // (a leader's own scan, or a non-leader picking up the leader's writes). Best-effort; never throws.
        PollResult poll = _freshness.PollNow();

        bool scanned = scan.Result == ScanOutcome.Kind.Scanned;
        var result = new WorkspaceActionResult(
            operation,
            scanned,
            poll.Swapped,
            poll.Revision,
            note,
            ArtifactId: CurrentArtifactId() ?? artifactIdBeforeScan);
        return WorkspaceRender.Action(result, json);
    }

    private (string output, int resultCount, TelemetryOutcome outcome) RenderTargetAction(
        string operation, string? workspaceId, string? path, bool force, bool json)
    {
        TargetWorkspace target = ResolveTarget(workspaceId, path);
        if (target.UnknownNote is { } note)
            return (Note(note, json), 0, TelemetryOutcome.Empty);

        if (target.IsCurrent)
            return (RenderAction(operation, force, json), 1, TelemetryOutcome.Ok);

        WorkspaceRefreshResult refresh = _crossWorkspaceRefresh.Refresh(target.WorkspaceId, force);
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
            ArtifactId: artifactId);
        return (WorkspaceRender.Action(result, json), 1, TelemetryOutcome.Ok);
    }

    // ---------- open (prime) ----------

    // Prime a path's index (decision-1): run an extract scan AT `path` so a future Miller launched there starts
    // warm. NOT a live switch — the served index/watcher/telemetry stay bound to this process's CWD. The scan
    // writes under `<path>/.miller/symbols.db`, the M2 convention. The path's root is canonicalized so julie's
    // inside-root check passes (verified-fact 4), exactly as the bootstrap does.
    private (string output, int resultCount, TelemetryOutcome outcome) Open(string? path, bool json)
    {
        if (string.IsNullOrWhiteSpace(path))
            return (UsageNote("open", json), 0, TelemetryOutcome.Empty);

        // A non-null but non-existent target is a clean not-found, not a tool failure. Guard here so
        // PathCanonicalizer.CanonicalizeRoot's DirectoryNotFoundException cannot become a hard diagnostic.
        if (!Directory.Exists(path))
        {
            string note = $"cannot prime: no directory at '{path}'.";
            string output = json ? ServerJson.Note(note) : note;
            return (output, 0, TelemetryOutcome.Empty);
        }

        // Canonicalize (symlink-resolved) BEFORE the safety checks so a symlink whose target is a sensitive root
        // cannot slip past the lexical guard. WorkspaceRootSafety/Normalize expect an already-canonical root (see
        // their doc); the CLI prime path canonicalizes first for the same reason.
        string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(path);

        // SAFETY: refuse to prime a sensitive system root (home, a filesystem/drive root, a system dir) — the
        // same guard the bootstrap applies to its cwd, here for an agent-supplied path. Return an honest note
        // (symmetric with the live-workspace / already-served guards below) rather than scanning the home tree.
        if (WorkspaceRootSafety.IsSensitiveRoot(canonicalRoot, WorkspaceRootSafety.SensitiveRootCandidates()))
        {
            string note =
                $"refusing to prime sensitive system path '{canonicalRoot}': choose a project " +
                "directory or pass a narrower path.";
            string output = json ? ServerJson.Note(note) : note;
            return (output, 0, TelemetryOutcome.Empty);
        }

        // SAFETY (decision-2/3/8): refuse to prime the LIVE workspace. open() runs a direct `extract scan`
        // outside the leader's _opsGate serialization (it is meant to prime a DIFFERENT, cold path so a Miller
        // launched there later starts warm). Spawning that scan against the in-use `.miller/symbols.db` would be
        // a second writer against the DB this process's indexer leader already owns — the exact M3 single-writer
        // hazard refresh/full route through the leader to avoid. Mirror the remove-live guard: return an honest
        // note pointing at refresh/full (which DO route through the leader) rather than scanning the live DB.
        // The check uses the pure WorkspaceSafety predicate (canonical, symlink-resolved) before canonicalizing.
        if (WorkspaceSafety.IsLiveWorkspace(path, _workspace.WorkspaceRoot))
        {
            string note =
                "that path IS the live workspace this process is serving; open does not prime the in-use " +
                "index. Use workspace(operation=\"refresh\") (or \"full\" to force a rebuild) — they reconcile " +
                "it through the indexer leader, keeping every write on the single-writer path.";
            string output = json ? ServerJson.Note(note) : note;
            return (output, 0, TelemetryOutcome.Empty);
        }

        string millerDir = Path.Combine(canonicalRoot, ".miller");
        string dbPath = Path.Combine(millerDir, "symbols.db");
        string stableWorkspaceId = WorkspaceId.FromCanonicalRoot(canonicalRoot);
        string displayId = WorkspaceId.Display(canonicalRoot, stableWorkspaceId);

        // SAFETY (decision-3, the M3 single-writer guard): another live Miller may ALREADY be the leader of this
        // (cold-to-us) target path, keeping its index fresh. Best-effort try-acquire the target's cross-process
        // SingleWriterLock before priming so we never run a second `extract scan` against a DB another leader
        // owns. If we cannot acquire it, that path is already served — report it HONESTLY (no faked prime) and do
        // NOT scan; the existing leader's watcher keeps it warm. We hold the lease only for the duration of the
        // prime scan, then release it so a Miller launched there later can take leadership.
        using IDisposable? lease = _acquireWriterLock(millerDir);
        if (lease is null)
        {
            string note =
                $"a Miller instance is already serving '{canonicalRoot}' (it holds the writer lock and keeps " +
                "that index fresh) — not priming it. A Miller launched there will use the live index directly.";
            string served = json ? ServerJson.Note(note) : note;
            return (served, 0, TelemetryOutcome.Empty);
        }

        // force:false — a prime is a from-current scan (julie creates the DB on the first scan of a fresh root,
        // or delta-reconciles an existing one); --force is reserved for the live workspace's `full` rebuild.
        ExtractReport report = _scanForOpen(canonicalRoot, dbPath, false);

        // v1 has no echoed workspace_id to cross-check: julie-extract self-rejects a DB built for a different
        // root (exit 3 RootMismatch, design §4.1), so a wrong-DB prime fails the scan above and surfaces through
        // the outer catch. The id we register is Miller's own stable id for canonicalRoot.
        long revision = report.Revision
            ?? IndexBootstrapService.ReadLatestRevisionOrZero(dbPath, stableWorkspaceId);
        _registry.UpsertSeen(stableWorkspaceId, displayId, canonicalRoot, dbPath, WorkspaceRegistryState.Ready);
        _registry.MarkScanned(stableWorkspaceId, revision);

        var result = new WorkspaceOpenResult(
            Path: canonicalRoot, DbPath: dbPath,
            // SymbolsExtracted is julie's unsigned count; Revision is null when the scan produced no cursor bump
            // (an unchanged delta) — report 0 honestly rather than fabricate a revision.
            SymbolsExtracted: (long)report.SymbolsExtracted,
            Revision: revision,
            WorkspaceId: stableWorkspaceId,
            DisplayId: displayId,
            WarningText: ExtractReportLog.DescribeWarning(report));
        return (WorkspaceRender.Open(result, json), 1, TelemetryOutcome.Ok);
    }

    // ---------- dashboard ----------

    private (string output, int resultCount, TelemetryOutcome outcome) Dashboard(int? port, bool json)
    {
        int launchPort = port is > 0 and <= 65535 ? port.Value : DashboardCliLauncher.DefaultPort;
        DashboardLaunchResult launch = _dashboardLauncher.EnsureRunning(
            new DashboardLaunchRequest(_workspace, launchPort, StartupTimeout: TimeSpan.FromSeconds(5)));
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
        DashboardLaunchOutcome.Failed => "failed",
        _ => outcome.ToString().ToLowerInvariant(),
    };

    // ---------- prune ----------

    // Remove registry rows whose canonical_root no longer exists. Never prunes the current workspace row (guarded
    // by workspace_id). Does not open symbols.db or spawn julie-extract.
    private (string output, int resultCount, TelemetryOutcome outcome) Prune(bool json, bool dryRun)
    {
        WorkspaceRegistryPrune.Result result =
            WorkspaceRegistryPrune.Run(_registry, _workspace.WorkspaceId, dryRun);
        var rendered = new WorkspacePruneResult(
            result.DryRun,
            result.Pruned.Select(e => new WorkspacePruneEntry(e.WorkspaceId, e.DisplayId, e.Root)).ToArray(),
            result.Kept);
        int count = result.Pruned.Count;
        return (WorkspaceRender.Prune(rendered, json), count, count > 0 ? TelemetryOutcome.Ok : TelemetryOutcome.Empty);
    }

    // ---------- remove ----------

    // Delete a workspace's `.miller` index dir (decision-1/8). SAFETY: refuse the live workspace (it is in use —
    // a half-delete would corrupt the index this process is serving). A path with no `.miller` dir is a clean
    // not-found (not an error). The is-live decision is the pure WorkspaceSafety predicate (unit-tested).
    private (string output, int resultCount, TelemetryOutcome outcome) Remove(
        string? workspaceId, string? path, bool json)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) && string.IsNullOrWhiteSpace(path))
            return (UsageNote("remove", json), 0, TelemetryOutcome.Empty);

        if (!string.IsNullOrWhiteSpace(workspaceId))
        {
            TargetWorkspace target = ResolveTarget(workspaceId, path: null);
            if (target.UnknownNote is { } note)
                return (Note(note, json), 0, TelemetryOutcome.Empty);

            return RemoveResolvedTarget(target, json);
        }

        if (WorkspaceSafety.IsLiveWorkspace(path!, _workspace.WorkspaceRoot))
            return RefuseLiveRemove(json);

        TargetWorkspace pathTarget = ResolveTarget(workspaceId: null, path);
        if (pathTarget.UnknownNote is null)
            return RemoveResolvedTarget(pathTarget, json);

        IReadOnlyList<WorkspaceRegistryRow> rows = _registry.List();
        WorkspaceRegistryRow? stale =
            WorkspaceRegistryRootMatcher.FindByPossiblyMissingPath(rows, path!);
        if (stale is not null)
            return RemoveResolvedTarget(TargetWorkspace.Registered(stale, IsCurrentWorkspace(stale)), json);

        // Backward-compatible cleanup path: allow deleting an unregistered local .miller dir by path. Unknown
        // workspace guidance still applies to targeted registry operations; remove can clean stale local indexes.
        string millerDir = Path.Combine(Path.GetFullPath(path!), ".miller");
        if (!Directory.Exists(millerDir))
        {
            var notFound = WorkspaceRemoveResult.NotFound(millerDir);
            return (WorkspaceRender.Remove(notFound, json), 0, TelemetryOutcome.Empty);
        }

        WorkspaceWriteLeases? leases = WorkspaceWriteLeases.TryAcquireForRemove(millerDir, _acquireWriterLock);
        if (leases is null)
        {
            var refused = WorkspaceRemoveResult.RefusedInUse(millerDir);
            return (WorkspaceRender.Remove(refused, json), 0, TelemetryOutcome.Empty);
        }
        // Delete the index data while HOLDING all three workspace-local write leases (indexer → content →
        // history) so no other instance — nor a CLI content import / history append that holds only the sidecar
        // lock — can start writing here mid-delete. Only the held lock files are skipped (a FileShare.None handle
        // blocks deleting the open file on Windows); after release, the leftover lock files + empty dir are
        // removed best-effort — a writer that sneaks in after release finds an already-empty index and rebuilds.
        try
        {
            SingleWriterLock.DeleteContentsExceptLock(millerDir, WorkspaceWriteLeases.SidecarLockFileNames);
        }
        finally
        {
            leases.Dispose();
        }

        SingleWriterLock.TryDeleteEmptiedDir(millerDir);
        _logger.LogInformation("workspace remove: deleted index dir {Dir}.", millerDir);
        var removed = WorkspaceRemoveResult.Removed(millerDir);
        return (WorkspaceRender.Remove(removed, json), 1, TelemetryOutcome.Ok);
    }

    private (string output, int resultCount, TelemetryOutcome outcome) RemoveResolvedTarget(
        TargetWorkspace target, bool json)
    {
        if (target.IsCurrent)
            return RefuseLiveRemove(json);

        WorkspaceRegistryRow row = target.Row
            ?? throw new InvalidOperationException("Registered workspace target is missing its registry row.");
        string millerDir = Path.GetDirectoryName(row.IndexDbPath)
            ?? throw new InvalidOperationException(
                $"Cannot determine the .miller directory for index DB path '{row.IndexDbPath}'.");

        if (!Directory.Exists(millerDir))
        {
            _registry.Remove(row.WorkspaceId);
            _logger.LogInformation(
                "workspace remove: unregistered {WorkspaceId}; index dir {Dir} was already missing.",
                row.WorkspaceId, millerDir);
            var staleRemoved = WorkspaceRemoveResult.Removed(
                millerDir,
                row.WorkspaceId,
                row.CanonicalRoot,
                indexDirDeleted: false);
            return (WorkspaceRender.Remove(staleRemoved, json), 1, TelemetryOutcome.Ok);
        }

        WorkspaceWriteLeases? leases = WorkspaceWriteLeases.TryAcquireForRemove(millerDir, _acquireWriterLock);
        if (leases is null)
        {
            var refused = WorkspaceRemoveResult.RefusedInUse(millerDir, row.WorkspaceId, row.CanonicalRoot);
            return (WorkspaceRender.Remove(refused, json), 0, TelemetryOutcome.Empty);
        }
        // Delete index data under all three held leases (indexer → content → history), then best-effort remove
        // the lock files + empty dir after release. See the path-only Remove branch for the full rationale.
        try
        {
            SingleWriterLock.DeleteContentsExceptLock(millerDir, WorkspaceWriteLeases.SidecarLockFileNames);
        }
        finally
        {
            leases.Dispose();
        }

        SingleWriterLock.TryDeleteEmptiedDir(millerDir);
        _registry.Remove(row.WorkspaceId);
        _logger.LogInformation(
            "workspace remove: unregistered {WorkspaceId} and deleted index dir {Dir}.",
            row.WorkspaceId, millerDir);
        var removed = WorkspaceRemoveResult.Removed(millerDir, row.WorkspaceId, row.CanonicalRoot);
        return (WorkspaceRender.Remove(removed, json), 1, TelemetryOutcome.Ok);
    }

    private (string output, int resultCount, TelemetryOutcome outcome) RefuseLiveRemove(bool json)
    {
        string liveMillerDir = Path.GetDirectoryName(_workspace.ExtractDbPath)!;
        var refused = WorkspaceRemoveResult.RefusedLive(
            liveMillerDir,
            _workspace.WorkspaceId,
            _workspace.CanonicalRoot ?? _workspace.WorkspaceRoot);
        return (WorkspaceRender.Remove(refused, json), 0, TelemetryOutcome.Empty);
    }

    private TargetWorkspace ResolveTarget(string? workspaceId, string? path)
    {
        if (!string.IsNullOrWhiteSpace(workspaceId))
        {
            if (IsCurrentSelector(workspaceId))
                return TargetWorkspace.Current(_workspace.WorkspaceId);

            try
            {
                WorkspaceRegistryRow row = WorkspaceRegistrySelector.Resolve(_registry, workspaceId);
                return TargetWorkspace.Registered(row, IsCurrentWorkspace(row));
            }
            catch (KeyNotFoundException ex)
            {
                return TargetWorkspace.Unknown(ex.Message);
            }
        }

        if (!string.IsNullOrWhiteSpace(path))
        {
            if (WorkspaceSafety.IsLiveWorkspace(path, _workspace.WorkspaceRoot))
                return TargetWorkspace.Current(_workspace.WorkspaceId);

            if (!Directory.Exists(path))
                return TargetWorkspace.Unknown(UnknownWorkspacePathNote(path));

            string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(path);
            WorkspaceRegistryRow? row = FindByCanonicalRoot(canonicalRoot);
            return row is null
                ? TargetWorkspace.Unknown(UnknownWorkspacePathNote(canonicalRoot))
                : TargetWorkspace.Registered(row, IsCurrentWorkspace(row));
        }

        return TargetWorkspace.Current(_workspace.WorkspaceId);
    }

    private WorkspaceRegistryRow? FindByCanonicalRoot(string canonicalRoot)
    {
        IReadOnlyList<WorkspaceRegistryRow> rows = _registry.List();
        return WorkspaceRegistryRootMatcher.FindByRoot(rows, canonicalRoot);
    }

    private bool IsCurrentWorkspace(WorkspaceRegistryRow row) =>
        string.Equals(row.WorkspaceId, _workspace.WorkspaceId, StringComparison.Ordinal)
        || WorkspaceSafety.IsLiveWorkspace(row.CanonicalRoot, _workspace.WorkspaceRoot);

    private bool IsCurrentSelector(string selector)
    {
        string trimmed = selector.Trim();
        if (string.Equals(trimmed, "current", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "primary", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrWhiteSpace(_workspace.WorkspaceId) &&
            string.Equals(trimmed, _workspace.WorkspaceId, StringComparison.Ordinal))
            return true;

        string? displayId = CurrentDisplayId();
        return !string.IsNullOrWhiteSpace(displayId) &&
               string.Equals(trimmed, displayId, StringComparison.OrdinalIgnoreCase);
    }

    private string? CurrentDisplayId()
    {
        if (string.IsNullOrWhiteSpace(_workspace.WorkspaceId))
            return null;

        string root = _workspace.CanonicalRoot ?? _workspace.WorkspaceRoot;
        try
        {
            return WorkspaceId.Display(root, _workspace.WorkspaceId);
        }
        catch (ArgumentException)
        {
            return _workspace.WorkspaceId;
        }
    }

    private void VerifyRegisteredRoot(WorkspaceRegistryRow row)
    {
        if (!Directory.Exists(row.CanonicalRoot))
        {
            string error = $"Workspace root not found: {row.CanonicalRoot}";
            _registry.MarkMissing(row.WorkspaceId, error);
            throw new DirectoryNotFoundException(error);
        }

        try
        {
            WorkspaceRootSafety.RejectSensitiveRoot(row.CanonicalRoot, fromCwd: false);
        }
        catch (InvalidOperationException ex)
        {
            _registry.MarkError(row.WorkspaceId, ex.Message);
            throw;
        }
    }

    private static string UnknownWorkspacePathNote(string path) =>
        $"unknown workspace path '{path}'. Run workspace(operation=\"open\", path=\"{path}\") " +
        "to register it first.";

    private static string Note(string message, bool json) =>
        json ? ServerJson.Note(message) : message;

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
                      "It primes that path's index (an extract scan) — not a live switch.",
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

    private static bool IsHealthIndexReadException(Exception ex) =>
        ex is SqliteException or InvalidOperationException;
}
