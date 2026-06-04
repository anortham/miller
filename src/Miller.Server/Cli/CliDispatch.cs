using Miller.Indexing;
using Miller.Server.Resolution;
using Miller.Server.Telemetry;
using Miller.Server.Tools;
using Miller.Server.Workspaces;

namespace Miller.Server.Cli;

/// <summary>
/// Miller's command-line surface: a thin one-shot dispatch over the SAME pure tool cores the MCP server exposes
/// (each tool's <c>Run(...)</c> + the <see cref="WorkspaceRender"/> renderers), so a shell/CI invocation and a
/// tool call produce identical output. The index is loaded once from the current workspace's
/// <c>.miller/symbols.db</c> via <see cref="RepositoryIndexLoader"/> — NO MCP host, NO background services, NO
/// Serilog file logging. <c>serve</c> and no-args are NOT CLI invocations (see <see cref="IsCliInvocation"/>);
/// they fall through to the stdio MCP server in <c>Program.cs</c>, which keeps its STDIO-purity contract. The CLI
/// OWNS stdout here, so it writes results to the injected <c>stdout</c> writer (Console in production, a capture
/// buffer in tests).
/// </summary>
public static class CliDispatch
{
    /// <summary>
    /// Whether <paramref name="args"/> is a CLI one-shot rather than an MCP server launch. Empty args (the
    /// historical launch) and an explicit <c>serve</c> verb are NOT CLI invocations — they run the MCP host.
    /// Anything else (a known verb, an unknown verb, or a <c>--flag</c>) is handled by <see cref="Run"/>, which
    /// usage-errors a bad verb rather than silently starting a server with junk arguments.
    /// </summary>
    public static bool IsCliInvocation(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Count > 0 && !string.Equals(args[0], "serve", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Execute the CLI verb in <paramref name="args"/> against <paramref name="context"/>, writing results to
    /// <paramref name="stdout"/> and diagnostics to <paramref name="stderr"/>. Returns a process exit code:
    /// 0 success, 2 usage error (bad/missing args or unknown verb), 3 no index / operational failure,
    /// 1 an unexpected error. NEVER throws — every failure becomes an exit code + a written message.
    /// </summary>
    public static int Run(IReadOnlyList<string> args, WorkspaceContext context, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        string verb = args.Count > 0 ? args[0] : "help";
        var rest = args.Skip(1).ToList();

        try
        {
            switch (verb.ToLowerInvariant())
            {
                case "version" or "--version" or "-v":
                    stdout.WriteLine(MillerVersion.Current);
                    return 0;
                case "help" or "--help" or "-h":
                    stdout.WriteLine(HelpText);
                    return 0;
                case "search":
                    return Search(rest, context, stdout, stderr);
                case "inspect":
                    return Inspect(rest, context, stdout, stderr);
                case "context":
                    return Context(rest, context, stdout, stderr);
                case "impact":
                    return Impact(rest, context, stdout, stderr);
                case "trace":
                    return Trace(rest, context, stdout, stderr);
                case "workspace":
                    return Workspace(rest, context, stdout, stderr);
                default:
                    stderr.WriteLine($"unknown command '{verb}'.");
                    stderr.WriteLine(HelpText);
                    return 2;
            }
        }
        catch (Exception ex)
        {
            // Mirror the tools' "<verb> failed: <msg>" contract: a clean line + a non-zero code, never a raw throw.
            stderr.WriteLine($"{verb} failed: {ex.Message}");
            return 1;
        }
    }

    // ---------- read verbs (over the current workspace's symbols.db) ----------

    private static int Search(IReadOnlyList<string> args, WorkspaceContext ctx, TextWriter outw, TextWriter err)
    {
        CliOptions o = CliOptions.Parse(args, "json", "include-tests");
        if (string.IsNullOrWhiteSpace(o.Query))
            return Usage(err, "miller search <query> [--mode auto|text|symbol|file|content] [--limit N] [--json] [--include-tests]");

        bool json = o.Has("json");
        int limit = o.Int("limit", 10);
        SearchToolMode mode = SearchTool.ParseMode(o.Value("mode", "auto")!);
        // exclude_tests tri-state: --include-tests forces them in; otherwise leave unset (the tool auto-hides for NL).
        bool? excludeTests = o.Has("include-tests") ? false : null;

        if (mode == SearchToolMode.Content)
        {
            if (!RequireIndex(ctx, err))
                return 3;
            ContentSearchProjection content = ContentSearchProjectionLoader.Load(ctx.ExtractDbPath, ctx.WorkspaceRoot);
            outw.WriteLine(SearchTool.RunContent(content, o.Query, limit, json, out _));
            return 0;
        }

        if (!TryLoadIndex(ctx, err, out MillerRepositoryIndex index))
            return 3;
        outw.WriteLine(SearchTool.Run(index, o.Query, mode, limit, excludeTests, json, out _));
        return 0;
    }

    private static int Inspect(IReadOnlyList<string> args, WorkspaceContext ctx, TextWriter outw, TextWriter err)
    {
        CliOptions o = CliOptions.Parse(args, "json");
        if (string.IsNullOrWhiteSpace(o.Query))
            return Usage(err, "miller inspect <file-or-symbol> [--depth summary|full] [--kind K] [--scope FILE] [--limit N] [--json]");

        if (!TryLoadIndex(ctx, err, out MillerRepositoryIndex index))
            return 3;

        var resolver = new SmartTargetResolver(index);
        string output = InspectTool.Run(
            index, resolver, ctx.ExtractDbPath, ctx.WorkspaceRoot,
            target: o.Query, depth: o.Value("depth", "summary")!, kind: o.Value("kind"), scope: o.Value("scope"),
            limit: o.Int("limit", 50), json: o.Has("json"), out _);
        outw.WriteLine(output);
        return 0;
    }

    private static int Context(IReadOnlyList<string> args, WorkspaceContext ctx, TextWriter outw, TextWriter err)
    {
        CliOptions o = CliOptions.Parse(args, "json");
        if (string.IsNullOrWhiteSpace(o.Query))
            return Usage(err, "miller context <query> [--token-budget N] [--max-hops 0-2] [--json]");

        if (!TryLoadIndex(ctx, err, out MillerRepositoryIndex index))
            return 3;

        var resolver = new SmartTargetResolver(index);
        string output = ContextTool.Run(
            index, resolver, query: o.Query, tokenBudget: o.Int("token-budget", 4000), maxHops: o.Int("max-hops", 1),
            entrySymbols: null, failingTest: null, stackTrace: null, json: o.Has("json"), out _, out _);
        outw.WriteLine(output);
        return 0;
    }

    private static int Impact(IReadOnlyList<string> args, WorkspaceContext ctx, TextWriter outw, TextWriter err)
    {
        CliOptions o = CliOptions.Parse(args, "json");
        if (string.IsNullOrWhiteSpace(o.Query))
            return Usage(err, "miller impact <symbol> [--max-depth N] [--limit N] [--json]");

        if (!TryLoadIndex(ctx, err, out MillerRepositoryIndex index))
            return 3;

        var resolver = new SmartTargetResolver(index);
        string output = ImpactTool.Run(
            index, resolver, target: o.Query, changedPaths: null, diff: null,
            maxDepth: o.Int("max-depth", 2), limit: o.Int("limit", 100), json: o.Has("json"), out _, out _);
        outw.WriteLine(output);
        return 0;
    }

    private static int Trace(IReadOnlyList<string> args, WorkspaceContext ctx, TextWriter outw, TextWriter err)
    {
        // trace is text-only: --full selects the compact|full form (full adds per-bridge-link signals). There is
        // no JSON output for trace, so --json is intentionally not a flag here.
        CliOptions o = CliOptions.Parse(args, "full");
        if (string.IsNullOrWhiteSpace(o.Query))
            return Usage(err, "miller trace <symbol> [--mode auto|path|bridge] [--to SYMBOL] [--depth N] [--limit N] [--full]");

        if (!TryLoadIndex(ctx, err, out MillerRepositoryIndex index))
            return 3;

        var resolver = new SmartTargetResolver(index);
        string output = TraceTool.Run(
            index, resolver, target: o.Query, mode: o.Value("mode", "auto")!, to: o.Value("to"),
            depth: o.Int("depth", 3), limit: o.Int("limit", 20), fullFormat: o.Has("full"), out _, out _);
        outw.WriteLine(output);
        return 0;
    }

    // ---------- workspace verb ----------

    private static int Workspace(IReadOnlyList<string> args, WorkspaceContext ctx, TextWriter outw, TextWriter err)
    {
        CliOptions o = CliOptions.Parse(args, "json", "full");
        string operation = (o.Query.Length > 0 ? o.Query : "status").ToLowerInvariant();
        bool json = o.Has("json");
        string? id = o.Value("id");
        string? path = o.Value("path");

        switch (operation)
        {
            case "list":
                return WorkspaceList(ctx, json, outw);
            case "status":
                return WorkspaceStatus(ctx, id, path, json, outw, err);
            case "refresh":
                return WorkspaceRefresh(ctx, id, path, force: false, json, outw, err);
            case "full":
                return WorkspaceRefresh(ctx, id, path, force: true, json, outw, err);
            case "open":
                return WorkspaceOpen(ctx, path, full: o.Has("full"), json, outw, err);
            case "remove":
                return WorkspaceRemove(ctx, id, path, json, outw, err);
            default:
                err.WriteLine($"unknown workspace operation '{operation}'. Use status|list|refresh|full|open|remove.");
                return 2;
        }
    }

    private static int WorkspaceList(WorkspaceContext ctx, bool json, TextWriter outw)
    {
        using WorkspaceRegistry registry = WorkspaceRegistry.Open(ctx.RegistryDbPath);
        IReadOnlyList<WorkspaceRegistryRow> rows = registry.List();
        var entries = new List<WorkspaceListEntry>(rows.Count);
        foreach (WorkspaceRegistryRow row in rows)
        {
            entries.Add(new WorkspaceListEntry(
                WorkspaceId: row.WorkspaceId,
                DisplayId: row.DisplayId,
                Root: row.CanonicalRoot,
                DbPath: row.IndexDbPath,
                State: row.StateText,
                LastRevision: row.LastRevision,
                Current: WorkspaceSafety.IsLiveWorkspace(row.CanonicalRoot, ctx.WorkspaceRoot),
                LastError: row.LastError));
        }
        outw.WriteLine(WorkspaceRender.List(entries, json));
        return 0;
    }

    private static int WorkspaceStatus(
        WorkspaceContext ctx, string? id, string? path, bool json, TextWriter outw, TextWriter err)
    {
        using WorkspaceRegistry registry = WorkspaceRegistry.Open(ctx.RegistryDbPath);

        // A registry-targeted status (an --id or --path) renders the registered row's index facts.
        if (!string.IsNullOrWhiteSpace(id) || !string.IsNullOrWhiteSpace(path))
        {
            WorkspaceRegistryRow row;
            try
            {
                row = WorkspaceRegistrySelector.Resolve(registry, (id ?? path)!);
            }
            catch (KeyNotFoundException ex)
            {
                err.WriteLine(ex.Message);
                return 2;
            }
            outw.WriteLine(WorkspaceRender.Status(FactsFromRow(registry, ctx, row), TelemetrySummary.Empty, json));
            return 0;
        }

        // Default: the current workspace. Enrich from its registry row when present, else read the local db.
        WorkspaceRegistryRow? currentRow = registry.List()
            .FirstOrDefault(r => WorkspaceSafety.IsLiveWorkspace(r.CanonicalRoot, ctx.WorkspaceRoot));
        if (currentRow is not null)
        {
            outw.WriteLine(WorkspaceRender.Status(FactsFromRow(registry, ctx, currentRow), TelemetrySummary.Empty, json));
            return 0;
        }

        if (!RequireIndex(ctx, err))
            return 3;
        WorkspaceIndexFacts indexFacts = WorkspaceIndexFactsReader.Read(ctx.ExtractDbPath);
        var facts = new WorkspaceFacts(
            Root: ctx.WorkspaceRoot,
            WorkspaceId: null,
            DbPath: ctx.ExtractDbPath,
            IsLeader: false,
            DocumentCount: indexFacts.DocumentCount,
            KnownExtensionsCount: indexFacts.KnownExtensionsCount,
            BuiltRevision: 0,
            LatestObservedRevision: 0,
            IndexFresh: null,                 // a one-shot CLI cannot poll freshness — honestly unknown
            QueueEmpty: true,
            FreshnessStatus: "unregistered",
            ServerVersion: MillerVersion.Current);
        outw.WriteLine(WorkspaceRender.Status(facts, TelemetrySummary.Empty, json));
        return 0;
    }

    // Facts for a registered workspace: identity + revision/state from the registry row, counts from its index db.
    // Freshness is "unknown" (null) — the CLI is one-shot and does not run the freshness poller. ServerVersion is
    // THIS binary's (the responder), set so `miller workspace status` shows which build produced the output.
    private static WorkspaceFacts FactsFromRow(WorkspaceRegistry registry, WorkspaceContext ctx, WorkspaceRegistryRow row)
    {
        long revision = row.LastRevision ?? 0;
        long documentCount = 0;
        int knownExtensions = 0;
        string? warning = row.LastError;
        try
        {
            WorkspaceIndexFacts facts = WorkspaceIndexFactsReader.Read(row.IndexDbPath);
            documentCount = facts.DocumentCount;
            knownExtensions = facts.KnownExtensionsCount;
        }
        catch (FileNotFoundException)
        {
            warning = $"index DB not found: {row.IndexDbPath}";
        }

        return new WorkspaceFacts(
            Root: row.CanonicalRoot,
            WorkspaceId: row.WorkspaceId,
            DbPath: row.IndexDbPath,
            IsLeader: false,
            DocumentCount: documentCount,
            KnownExtensionsCount: knownExtensions,
            BuiltRevision: revision,
            LatestObservedRevision: revision,
            IndexFresh: null,
            QueueEmpty: true,
            FreshnessStatus: row.StateText,
            WarningText: warning,
            DisplayId: row.DisplayId,
            ServerVersion: MillerVersion.Current);
    }

    private static int WorkspaceRefresh(
        WorkspaceContext ctx, string? id, string? path, bool force, bool json, TextWriter outw, TextWriter err)
    {
        using WorkspaceRegistry registry = WorkspaceRegistry.Open(ctx.RegistryDbPath);

        string? selector = id ?? path;
        WorkspaceRegistryRow row;
        if (!string.IsNullOrWhiteSpace(selector))
        {
            try
            {
                row = WorkspaceRegistrySelector.Resolve(registry, selector);
            }
            catch (KeyNotFoundException ex)
            {
                err.WriteLine(ex.Message);
                return 2;
            }
        }
        else
        {
            WorkspaceRegistryRow? currentRow = registry.List()
                .FirstOrDefault(r => WorkspaceSafety.IsLiveWorkspace(r.CanonicalRoot, ctx.WorkspaceRoot));
            if (currentRow is null)
            {
                err.WriteLine(
                    "the current workspace is not registered, so there is nothing to refresh by id. Open it in the " +
                    "Miller MCP server first, or pass --id <display-id> (see `miller workspace list`).");
                return 2;
            }
            row = currentRow;
        }

        // The lock-holding cross-workspace refresh path (same one the dashboard uses): acquire the workspace
        // single-writer lock, run julie-extract, rebuild the search sidecar. If a live Miller already holds the
        // lock, Refresh reports lock_busy honestly rather than racing a second writer.
        var runner = JulieExtractRunner.Locate(ctx.ToolsRoot);
        var sidecar = SymbolSearchSidecar.FromEnvironment();
        var refresh = new CrossWorkspaceRefreshService(registry, runner, sidecar);
        WorkspaceRefreshResult result = refresh.Refresh(row.WorkspaceId, force);

        string? note = result.Error ?? result.WarningText;
        var action = new WorkspaceActionResult(
            Operation: force ? "full" : "refresh",
            Scanned: result.Scanned,
            Swapped: false,
            Revision: result.Revision ?? 0,
            Note: note,
            WorkspaceId: result.WorkspaceId,
            Root: result.WorkspaceRoot,
            Status: result.StatusText);
        outw.WriteLine(WorkspaceRender.Action(action, json));
        return RefreshExitCode(result.Status);
    }

    // ---------- open (bootstrap a fresh directory) ----------

    // Register the target directory and index it from the CLI — the bootstrap path a one-shot/CI flow needs
    // (`cd repo && miller workspace open`). Target = --path, else the current workspace. We canonicalize FIRST
    // so the sensitive-root guard sees the symlink-resolved root, locate julie-extract BEFORE registering (a
    // missing tool must not leave an orphan "ready" row), then drive the SAME lock-holding refresh machinery
    // `refresh`/`full` use — it acquires the single-writer lock, runs the scan (creating .miller/symbols.db on
    // first run), builds the search sidecar (best-effort, when enabled), and marks the row scanned. Rendered as
    // an "open" action and mapped through the shared RefreshExitCode so a failure is a non-zero CI signal.
    private static int WorkspaceOpen(
        WorkspaceContext ctx, string? path, bool full, bool json, TextWriter outw, TextWriter err)
    {
        string targetRoot = string.IsNullOrWhiteSpace(path) ? ctx.WorkspaceRoot : path!;

        if (!Directory.Exists(targetRoot))
        {
            err.WriteLine($"cannot open: no directory at '{targetRoot}'.");
            return 2;
        }

        // Canonicalize before the safety check so a symlink whose target is a sensitive root cannot slip past
        // the lexical predicate, and before any registry write.
        string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(targetRoot);
        if (WorkspaceRootSafety.IsSensitiveRoot(canonicalRoot, WorkspaceRootSafety.SensitiveRootCandidates()))
        {
            err.WriteLine($"refusing to index sensitive system path '{canonicalRoot}': choose a project directory.");
            return 2;
        }

        string millerDir = Path.Combine(canonicalRoot, ".miller");
        string dbPath = Path.Combine(millerDir, "symbols.db");
        string id = WorkspaceId.FromCanonicalRoot(canonicalRoot);
        string display = WorkspaceId.Display(canonicalRoot, id);

        // Locate julie-extract BEFORE registering — absent ⇒ exit 3 with the restore message and NO orphan row.
        JulieExtractRunner runner;
        try
        {
            runner = JulieExtractRunner.Locate(ctx.ToolsRoot);
        }
        catch (FileNotFoundException ex)
        {
            err.WriteLine($"cannot open: {ex.Message}");
            return 3;
        }

        var sidecar = SymbolSearchSidecar.FromEnvironment();
        using WorkspaceRegistry registry = WorkspaceRegistry.Open(ctx.RegistryDbPath);
        registry.UpsertSeen(id, display, canonicalRoot, dbPath, WorkspaceRegistryState.Ready);

        var refresh = new CrossWorkspaceRefreshService(registry, runner, sidecar);
        WorkspaceRefreshResult result = refresh.Refresh(id, force: full);

        // Rendered via the Action view (NOT WorkspaceRender.Open, whose "primed / not a live switch" copy is
        // server semantics — false for the CLI). A scan failure has marked the just-registered row error.
        var action = new WorkspaceActionResult(
            "open",
            Scanned: result.Scanned,
            Swapped: false,
            Revision: result.Revision ?? 0,
            Note: result.Error ?? result.WarningText,
            WorkspaceId: result.WorkspaceId,
            Root: result.WorkspaceRoot,
            Status: result.StatusText);
        outw.WriteLine(WorkspaceRender.Action(action, json));
        return RefreshExitCode(result.Status);
    }

    // ---------- remove (delete a workspace's .miller index dir) ----------

    // Delete a workspace's `.miller` index dir + unregister it. Ported from the server's WorkspaceTool.Remove
    // minus the in-process "live workspace" refusal — the one-shot CLI serves nothing in-process, so the
    // cross-process single-writer lock is the only guard against deleting a dir a running Miller owns. Requires
    // an explicit selector (--id or --path); there is no current-dir default (deleting the dir you stand in by
    // accident is a foot-gun).
    private static int WorkspaceRemove(
        WorkspaceContext ctx, string? id, string? path, bool json, TextWriter outw, TextWriter err)
    {
        if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(path))
        {
            err.WriteLine("workspace remove requires a selector: --id <display-id> or --path <dir>.");
            return 2;
        }

        using WorkspaceRegistry registry = WorkspaceRegistry.Open(ctx.RegistryDbPath);

        // By id: resolve the registry row, then delete its .miller dir + unregister.
        if (!string.IsNullOrWhiteSpace(id))
        {
            WorkspaceRegistryRow row;
            try
            {
                row = WorkspaceRegistrySelector.Resolve(registry, id!);
            }
            catch (KeyNotFoundException ex)
            {
                err.WriteLine(ex.Message);
                return 2;
            }
            string millerDir = Path.GetDirectoryName(row.IndexDbPath)
                ?? throw new InvalidOperationException(
                    $"Cannot determine the .miller directory for index DB path '{row.IndexDbPath}'.");
            return RemoveMillerDir(registry, row.WorkspaceId, row.CanonicalRoot, millerDir, json, outw);
        }

        // By path. A GONE dir cannot be canonicalized, so best-effort prune a registry row whose canonical root
        // lexically matches the full path (R4 — lets a CI teardown clean the registry after deleting the repo).
        string fullPath = Path.GetFullPath(path!);
        if (!Directory.Exists(fullPath))
        {
            string goneMillerDir = Path.Combine(fullPath, ".miller");
            WorkspaceRegistryRow? stale = registry.List().FirstOrDefault(r => RootMatches(r, fullPath));
            if (stale is not null)
            {
                registry.Remove(stale.WorkspaceId);
                outw.WriteLine(WorkspaceRender.Remove(
                    WorkspaceRemoveResult.Removed(goneMillerDir, stale.WorkspaceId, stale.CanonicalRoot), json));
                return RemoveExitCode(WorkspaceRemoveResult.Outcome.Removed);
            }
            outw.WriteLine(WorkspaceRender.Remove(WorkspaceRemoveResult.NotFound(goneMillerDir), json));
            return RemoveExitCode(WorkspaceRemoveResult.Outcome.NotFound);
        }

        // Existing dir: canonicalize and match a registry row (ordinal canonical root, like the server's
        // FindByCanonicalRoot), falling back to a local .miller cleanup when no row is registered.
        string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(fullPath);
        WorkspaceRegistryRow? match = registry.List().FirstOrDefault(r => RootMatches(r, canonicalRoot));
        string millerDirByPath = match is { } m
            ? Path.GetDirectoryName(m.IndexDbPath) ?? Path.Combine(canonicalRoot, ".miller")
            : Path.Combine(canonicalRoot, ".miller");
        return RemoveMillerDir(registry, match?.WorkspaceId, match?.CanonicalRoot ?? canonicalRoot, millerDirByPath, json, outw);
    }

    // Delete one `.miller` dir under the cross-process writer lock. Missing dir ⇒ a clean not-found (prune any
    // stale row); lock held by another writer ⇒ refused, NOT deleted; otherwise delete + unregister.
    private static int RemoveMillerDir(
        WorkspaceRegistry registry, string? workspaceId, string? root, string millerDir, bool json, TextWriter outw)
    {
        if (!Directory.Exists(millerDir))
        {
            if (workspaceId is not null)
                registry.Remove(workspaceId);
            outw.WriteLine(WorkspaceRender.Remove(WorkspaceRemoveResult.NotFound(millerDir, workspaceId, root), json));
            return RemoveExitCode(WorkspaceRemoveResult.Outcome.NotFound);
        }

        // Acquire the writer lock ONLY to prove no live Miller owns this workspace, then RELEASE it before the
        // delete. `indexer.lock` lives inside millerDir and is held FileShare.None, so on Windows our OWN handle
        // would block Directory.Delete (it would throw and the CLI would wrongly report exit 1 instead of 0).
        // Acquire-release-delete keeps the no-concurrent-writer guard on every platform.
        using (IDisposable? lease = SingleWriterLock.TryAcquire(millerDir))
        {
            if (lease is null)
            {
                outw.WriteLine(WorkspaceRender.Remove(WorkspaceRemoveResult.RefusedInUse(millerDir, workspaceId, root), json));
                return RemoveExitCode(WorkspaceRemoveResult.Outcome.RefusedInUse);
            }
        }

        Directory.Delete(millerDir, recursive: true);
        if (workspaceId is not null)
            registry.Remove(workspaceId);
        outw.WriteLine(WorkspaceRender.Remove(WorkspaceRemoveResult.Removed(millerDir, workspaceId, root), json));
        return RemoveExitCode(WorkspaceRemoveResult.Outcome.Removed);
    }

    // Whether a registry row's canonical root identifies the given root. Ordinal first (the common exact case),
    // then the OS-case-aware WorkspaceSafety fallback so a case-only difference on a case-insensitive volume
    // (macOS/Windows) still matches — and, for a GONE dir, IsLiveWorkspace degrades to a lexical full-path compare.
    private static bool RootMatches(WorkspaceRegistryRow row, string root) =>
        string.Equals(row.CanonicalRoot, root, StringComparison.Ordinal)
        || WorkspaceSafety.IsLiveWorkspace(row.CanonicalRoot, root);

    // Map a refresh/full outcome to a process exit code. EVERY non-success terminal state must be non-zero so a
    // script (`miller workspace full && deploy`) can't proceed on a broken/never-refreshed workspace: only
    // Refreshed/Unchanged (the index is current) are success (0); a missing root/index, a hard failure, or a busy
    // single-writer lock (the refresh did NOT run) are operational failures (3); any future status is unexpected (1).
    internal static int RefreshExitCode(WorkspaceRefreshStatus status) => status switch
    {
        WorkspaceRefreshStatus.Refreshed or WorkspaceRefreshStatus.Unchanged => 0,
        WorkspaceRefreshStatus.MissingRoot
            or WorkspaceRefreshStatus.MissingIndex
            or WorkspaceRefreshStatus.LockBusy
            or WorkspaceRefreshStatus.Failed => 3,
        _ => 1,
    };

    // Map a remove outcome to a process exit code. Removed and NotFound are both success (the index dir is gone
    // — NotFound is an idempotent no-op); a refusal (another writer holds the lock, or the live workspace) did
    // NOT delete, so it is an operational failure (3) a CI teardown must see.
    internal static int RemoveExitCode(WorkspaceRemoveResult.Outcome outcome) => outcome switch
    {
        WorkspaceRemoveResult.Outcome.Removed or WorkspaceRemoveResult.Outcome.NotFound => 0,
        WorkspaceRemoveResult.Outcome.RefusedInUse or WorkspaceRemoveResult.Outcome.RefusedLive => 3,
        _ => 1,
    };

    // ---------- helpers ----------

    private static bool TryLoadIndex(WorkspaceContext ctx, TextWriter err, out MillerRepositoryIndex index)
    {
        if (!RequireIndex(ctx, err))
        {
            index = null!;
            return false;
        }
        index = RepositoryIndexLoader.Load(ctx.ExtractDbPath);
        return true;
    }

    private static bool RequireIndex(WorkspaceContext ctx, TextWriter err)
    {
        if (File.Exists(ctx.ExtractDbPath))
            return true;
        err.WriteLine($"no Miller index at {ctx.ExtractDbPath}.");
        err.WriteLine("Build it with `miller workspace full`, or open this folder in the Miller MCP server.");
        return false;
    }

    private static int Usage(TextWriter err, string usage)
    {
        err.WriteLine("usage: " + usage);
        return 2;
    }

    private const string HelpText =
        """
        miller — code-intelligence CLI over a julie-extract index (.miller/symbols.db in the current directory).

        Usage: miller <command> [args]

        Commands:
          search <query>     Find code by name, identifier, or phrase.
                             [--mode auto|text|symbol|file|content] [--limit N] [--json] [--include-tests]
          inspect <target>   List a file's symbols, or show a symbol's definition.
                             [--depth summary|full] [--kind K] [--scope FILE] [--limit N] [--json]
          context <query>    Token-budgeted bundle of the most relevant code for a task.
                             [--token-budget N] [--max-hops 0-2] [--json]
          impact <symbol>    Downstream symbols + tests a change would affect.
                             [--max-depth N] [--limit N] [--json]
          trace <symbol>     Follow callers/callees, a path, or a cross-language bridge.
                             [--mode auto|path|bridge] [--to SYMBOL] [--depth N] [--limit N] [--full]
          workspace [op]     Index lifecycle. op = status (default) | list | refresh | full | open | remove.
                             open   [--path DIR] [--full]   Register + index a directory (creates .miller/symbols.db).
                             remove (--id ID | --path DIR)  Delete a workspace's .miller index dir.
                             [--id DISPLAY-ID] [--path DIR] [--json]
          version            Print the build version (e.g. 0.1.0+<sha>).
          help               Show this help.
          serve              Run the MCP stdio server (the default when launched with no arguments).
        """;
}
