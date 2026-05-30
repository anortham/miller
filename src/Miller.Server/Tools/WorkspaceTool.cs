using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Miller.Indexing;
using Miller.Server.Hosting;
using Miller.Server.Telemetry;
using ModelContextProtocol.Server;

namespace Miller.Server.Tools;

/// <summary>
/// The <c>workspace</c> tool (miller-toolbox.md §7, M7 decision-1): admin / index lifecycle. It defaults to
/// <c>status</c> (the 80% call — workspace identity + index facts + the telemetry tool-breakdown), and exposes
/// <c>refresh</c>/<c>full</c> (reconcile / from-scratch rebuild, routed through the single-writer leader so two
/// Miller instances never both <c>extract scan</c>), <c>list</c> (the current workspace — Miller serves ONE per
/// process; a multi-workspace registry is eros/commercial-tier, decision-1), <c>open(path)</c> (PRIME a path's
/// index via an extract scan so a future Miller there is warm — NOT a live switch), and <c>remove(path)</c>
/// (delete a workspace's <c>.miller</c> index dir, REFUSING the live one).
///
/// <para>The pure renderers (<see cref="WorkspaceRender"/>) and the remove-safety predicate
/// (<see cref="WorkspaceSafety"/>) carry the formatting + safety logic and are unit-tested; this class
/// orchestrates the live singletons (the holder, the indexer leader, the freshness poller, the extract runner)
/// and the telemetry shell. Every operation reports HONESTLY what happened — a non-leader that cannot force a
/// scan, a scan failure, a refused remove — never a faked success. Mirrors the other tools' shape:
/// ctor-injected singletons, a thin <see cref="Workspace"/> dispatch wrapped in try/catch returning
/// <c>"workspace failed: {msg}"</c>, telemetry via <see cref="TelemetryContext.Current"/>.</para>
/// </summary>
[McpServerToolType]
public sealed class WorkspaceTool
{
    private readonly IndexHolder _holder;
    private readonly WorkspaceContext _workspace;
    private readonly IndexerService _indexer;
    private readonly FreshnessService _freshness;
    private readonly IndexFreshProbe _freshProbe;
    private readonly TelemetryLedger _ledger;
    private readonly JulieExtractRunner _runner;
    private readonly ILogger<WorkspaceTool> _logger;

    /// <summary>Construct over the live admin singletons (production).</summary>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public WorkspaceTool(
        IndexHolder holder,
        WorkspaceContext workspace,
        IndexerService indexer,
        FreshnessService freshness,
        IndexFreshProbe freshProbe,
        TelemetryLedger ledger,
        JulieExtractRunner runner,
        ILogger<WorkspaceTool> logger)
    {
        ArgumentNullException.ThrowIfNull(holder);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(indexer);
        ArgumentNullException.ThrowIfNull(freshness);
        ArgumentNullException.ThrowIfNull(freshProbe);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(logger);
        _holder = holder;
        _workspace = workspace;
        _indexer = indexer;
        _freshness = freshness;
        _freshProbe = freshProbe;
        _ledger = ledger;
        _runner = runner;
        _logger = logger;
    }

    [McpServerTool(Name = "workspace")]
    [Description(
        "Manage the workspace index. Defaults to status. Use refresh to update stale files, full to rebuild " +
        "from scratch, list to see registered workspaces.")]
    public string Workspace(
        [Description("status|refresh|full|list|open|remove. Default status.")] string operation = "status",
        [Description("A workspace root path. Required for open/remove; ignored by status/list.")] string? path = null,
        [Description("Output format: compact|json. Default compact.")] string format = "compact")
    {
        var telemetry = TelemetryContext.Current;
        // D7: stamp the operation sub-axis onto the ambient scope so the central filter's row records
        // op=<operation> (status/refresh/full/list/open/remove) instead of NULL — workspace is in the
        // tool-breakdown WITH its per-operation axis. Normalised to lowercase to match the dispatch keys.
        if (telemetry is not null)
            telemetry.Op = (operation ?? "status").ToLowerInvariant();
        try
        {
            bool json = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);

            (string output, int resultCount, TelemetryOutcome outcome) = Dispatch(operation, path, json);

            if (telemetry is not null)
            {
                telemetry.ResultCount = resultCount;
                telemetry.Outcome = outcome;
            }
            return output;
        }
        catch (Exception ex)
        {
            if (telemetry is not null)
            {
                telemetry.Outcome = TelemetryOutcome.Error;
                telemetry.ErrorKind = ex.GetType().Name;
            }
            _logger.LogError(ex, "workspace {Operation} failed.", operation);
            return $"workspace failed: {ex.Message}";
        }
    }

    // The pure-ish dispatch: route the operation to its handler, returning the rendered output, a result count
    // (for the telemetry KPI), and the outcome. An unknown operation is a usage note (Empty, not an error).
    private (string output, int resultCount, TelemetryOutcome outcome) Dispatch(
        string? operation, string? path, bool json)
    {
        switch (operation?.ToLowerInvariant())
        {
            case null or "" or "status":
                return (RenderStatus(json), 1, TelemetryOutcome.Ok);
            case "list":
                return (WorkspaceRender.List(AssembleFacts(), json), 1, TelemetryOutcome.Ok);
            case "refresh":
                return (RenderAction("refresh", force: false, json), 1, TelemetryOutcome.Ok);
            case "full":
                return (RenderAction("full", force: true, json), 1, TelemetryOutcome.Ok);
            case "open":
                return Open(path, json);
            case "remove":
                return Remove(path, json);
            default:
                return (UsageNote(operation!, json), 0, TelemetryOutcome.Empty);
        }
    }

    // ---------- status / list facts ----------

    private string RenderStatus(bool json) =>
        WorkspaceRender.Status(AssembleFacts(), _ledger.Summarize(), json);

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
            QueueEmpty: _indexer.QueueEmpty);
    }

    // ---------- refresh / full ----------

    // Reconcile NOW (decision-3): the leader runs an extract scan (delta for refresh, --force for full) then an
    // immediate poll+swap; a non-leader cannot scan (the M3 single-writer guard), so it only polls+swaps to pick
    // up the leader's writes and reports HONESTLY that it could not force a scan here. Either path ends with the
    // in-memory index current without waiting for the 2s loop tick.
    private string RenderAction(string operation, bool force, bool json)
    {
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

        // Always poll+swap after the scan attempt so the held index reflects the newest persisted revision NOW
        // (a leader's own scan, or a non-leader picking up the leader's writes). Best-effort; never throws.
        PollResult poll = _freshness.PollNow();

        bool scanned = scan.Result == ScanOutcome.Kind.Scanned;
        var result = new WorkspaceActionResult(operation, scanned, poll.Swapped, poll.Revision, note);
        return WorkspaceRender.Action(result, json);
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

        // A non-null but non-existent target is a clean not-found, NOT a tool failure (symmetric with remove's
        // not-found). Guard here so PathCanonicalizer.CanonicalizeRoot's DirectoryNotFoundException never leaks to
        // the outer catch as a generic "workspace failed"; give clear "cannot prime" guidance instead (Empty).
        if (!Directory.Exists(path))
        {
            string note = $"cannot prime: no directory at '{path}'.";
            string output = json ? $"{{\"note\":{System.Text.Json.JsonSerializer.Serialize(note)}}}" : note;
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
            string output = json ? $"{{\"note\":{System.Text.Json.JsonSerializer.Serialize(note)}}}" : note;
            return (output, 0, TelemetryOutcome.Empty);
        }

        string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(path);
        string millerDir = Path.Combine(canonicalRoot, ".miller");
        string dbPath = Path.Combine(millerDir, "symbols.db");

        // SAFETY (decision-3, the M3 single-writer guard): another live Miller may ALREADY be the leader of this
        // (cold-to-us) target path, keeping its index fresh. Best-effort try-acquire the target's cross-process
        // SingleWriterLock before priming so we never run a second `extract scan` against a DB another leader
        // owns. If we cannot acquire it, that path is already served — report it HONESTLY (no faked prime) and do
        // NOT scan; the existing leader's watcher keeps it warm. We hold the lease only for the duration of the
        // prime scan, then release it so a Miller launched there later can take leadership.
        using SingleWriterLock? lease = SingleWriterLock.TryAcquire(millerDir);
        if (lease is null)
        {
            string note =
                $"a Miller instance is already serving '{canonicalRoot}' (it holds the writer lock and keeps " +
                "that index fresh) — not priming it. A Miller launched there will use the live index directly.";
            string served = json ? $"{{\"note\":{System.Text.Json.JsonSerializer.Serialize(note)}}}" : note;
            return (served, 0, TelemetryOutcome.Empty);
        }

        // force:false — a prime is a from-current scan (julie creates the DB on the first scan of a fresh root,
        // or delta-reconciles an existing one); --force is reserved for the live workspace's `full` rebuild.
        ExtractReport report = _runner.Scan(canonicalRoot, dbPath, force: false);

        var result = new WorkspaceOpenResult(
            Path: canonicalRoot, DbPath: dbPath,
            // SymbolsExtracted is julie's unsigned count; Revision is null when the scan produced no cursor bump
            // (an unchanged delta) — report 0 honestly rather than fabricate a revision.
            SymbolsExtracted: (long)report.SymbolsExtracted, Revision: report.Revision ?? 0);
        return (WorkspaceRender.Open(result, json), 1, TelemetryOutcome.Ok);
    }

    // ---------- remove ----------

    // Delete a workspace's `.miller` index dir (decision-1/8). SAFETY: refuse the live workspace (it is in use —
    // a half-delete would corrupt the index this process is serving). A path with no `.miller` dir is a clean
    // not-found (not an error). The is-live decision is the pure WorkspaceSafety predicate (unit-tested).
    private (string output, int resultCount, TelemetryOutcome outcome) Remove(string? path, bool json)
    {
        if (string.IsNullOrWhiteSpace(path))
            return (UsageNote("remove", json), 0, TelemetryOutcome.Empty);

        // The .miller dir under the requested path. We do NOT canonicalize the path here (it may not exist, and
        // we must report a faithful not-found); WorkspaceSafety canonicalizes both sides for the live compare.
        string millerDir = Path.Combine(Path.GetFullPath(path), ".miller");

        if (WorkspaceSafety.IsLiveWorkspace(path, _workspace.WorkspaceRoot))
        {
            // Refuse — the live index is in use. Report the LIVE .miller path so the refusal is unambiguous.
            string liveMillerDir = Path.GetDirectoryName(_workspace.ExtractDbPath)!;
            var refused = WorkspaceRemoveResult.RefusedLive(liveMillerDir);
            return (WorkspaceRender.Remove(refused, json), 0, TelemetryOutcome.Empty);
        }

        if (!Directory.Exists(millerDir))
        {
            var notFound = WorkspaceRemoveResult.NotFound(millerDir);
            return (WorkspaceRender.Remove(notFound, json), 0, TelemetryOutcome.Empty);
        }

        Directory.Delete(millerDir, recursive: true);
        _logger.LogInformation("workspace remove: deleted index dir {Dir}.", millerDir);
        var removed = WorkspaceRemoveResult.Removed(millerDir);
        return (WorkspaceRender.Remove(removed, json), 1, TelemetryOutcome.Ok);
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
                 "Use status|refresh|full|list|open|remove (default status).",
        };
        return json ? $"{{\"note\":{System.Text.Json.JsonSerializer.Serialize(message)}}}" : message;
    }
}
