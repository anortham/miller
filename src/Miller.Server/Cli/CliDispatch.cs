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
        CliOptions o = CliOptions.Parse(args, "json");
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
            default:
                err.WriteLine($"unknown workspace operation '{operation}'. Use status|list|refresh|full.");
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
          workspace [op]     Index lifecycle. op = status (default) | list | refresh | full.
                             [--id DISPLAY-ID] [--path DIR] [--json]
          version            Print the build version (e.g. 0.1.0+<sha>).
          help               Show this help.
          serve              Run the MCP stdio server (the default when launched with no arguments).
        """;
}
