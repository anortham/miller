using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Server.Resolution;
using Miller.Server.Telemetry;
using Miller.Server.Tools;
using Miller.Server.Workspaces;

namespace Miller.Server.Cli;

/// <summary>
/// Miller's command-line surface: a thin one-shot dispatch over the SAME pure tool cores the MCP server exposes
/// (each tool's <c>Run(...)</c> + the <see cref="WorkspaceRender"/> renderers), so a shell/CI invocation and a
/// tool call produce identical output. Read verbs load the smallest index shape they need from the current
/// workspace's <c>.miller/symbols.db</c>: symbol search and inspect use the symbol lookup projection, graph-only
/// verbs use on-demand SQLite graph reachability, and bridge trace still uses the full bridge graph. There is NO
/// MCP host, NO background services, NO Serilog file logging. <c>serve</c> and no-args are NOT CLI invocations (see <see cref="IsCliInvocation"/>);
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
    public static int Run(IReadOnlyList<string> args, WorkspaceContext context, TextWriter stdout, TextWriter stderr) =>
        Run(args, context, stdout, stderr, new DashboardCliLauncher());

    internal static int Run(
        IReadOnlyList<string> args,
        WorkspaceContext context,
        TextWriter stdout,
        TextWriter stderr,
        IDashboardLauncher dashboardLauncher)
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
                case "capabilities":
                    return Capabilities(rest, stdout, stderr);
                case "search":
                    return Search(rest, context, stdout, stderr);
                case "content":
                    return Content(rest, context, stdout, stderr);
                case "patterns":
                    return Patterns(rest, context, stdout, stderr);
                case "telemetry":
                    return Telemetry(rest, context, stdout, stderr);
                case "symbols":
                    return ArtifactExport(rest, context, stdout, stderr,
                        "miller symbols export [--jsonl] [--workspace-id SELECTOR] [--workspace DIR]",
                        SymbolExportReader.ExportJsonLines);
                case "complexity":
                    return ArtifactExport(rest, context, stdout, stderr,
                        "miller complexity export [--jsonl] [--workspace-id SELECTOR] [--workspace DIR]",
                        ComplexityExportReader.ExportJsonLines);
                case "refresh":
                    return Refresh(rest, context, stdout, stderr);
                case "inspect":
                    return Inspect(rest, context, stdout, stderr);
                case "context":
                    return Context(rest, context, stdout, stderr);
                case "impact":
                    return Impact(rest, context, stdout, stderr);
                case "trace":
                    return Trace(rest, context, stdout, stderr);
                case "dashboard":
                    return Dashboard(rest, context, stdout, stderr, dashboardLauncher);
                case "workspace":
                    return Workspace(rest, context, stdout, stderr);
                default:
                    stderr.WriteLine($"unknown command '{verb}'.");
                    stderr.WriteLine(HelpText);
                    return 2;
            }
        }
        catch (IncompatibleExtractException ex)
        {
            // cli-eros-v1 exit contract: an unusable index (schema/contract/hash mismatch) is an OPERATIONAL
            // failure (3) the caller answers with a rebuild — not an unexpected failure (1) that pages someone.
            stderr.WriteLine($"{verb} failed: {ex.Message}");
            return 3;
        }
        catch (Exception ex)
        {
            // Mirror the tools' "<verb> failed: <msg>" contract: a clean line + a non-zero code, never a raw throw.
            stderr.WriteLine($"{verb} failed: {ex.Message}");
            return 1;
        }
    }

    private static int Capabilities(IReadOnlyList<string> args, TextWriter outw, TextWriter err)
    {
        CliOptions o = CliOptions.Parse(args, "json");
        if (o.Positionals.Count > 0)
            return Usage(err, "miller capabilities [--json]");

        outw.WriteLine(CliCapabilities.Render(o.Has("json")));
        return 0;
    }

    private static int Dashboard(
        IReadOnlyList<string> args,
        WorkspaceContext ctx,
        TextWriter outw,
        TextWriter err,
        IDashboardLauncher launcher)
    {
        CliOptions o = CliOptions.Parse(args, "json");
        if (o.Positionals.Count > 0)
            return Usage(err, "miller dashboard [--port N] [--json]");

        int port = o.Int("port", DashboardCliLauncher.DefaultPort);
        DashboardLaunchResult result = launcher.EnsureRunning(
            new DashboardLaunchRequest(ctx, port, StartupTimeout: TimeSpan.FromSeconds(5)));
        if (o.Has("json"))
        {
            outw.WriteLine(ServerJson.Serialize(new DashboardLaunchJson(
                Status: result.Outcome.ToString().ToLowerInvariant(),
                Url: result.Url.ToString(),
                Pid: result.ProcessId,
                Message: result.Message)));
            return result.Success ? 0 : 3;
        }

        if (!result.Success)
        {
            err.WriteLine(result.Message ?? "dashboard failed");
            return 3;
        }

        string status = result.Outcome == DashboardLaunchOutcome.Started
            ? "dashboard started"
            : "dashboard already running";
        if (result.ProcessId is { } pid)
            outw.WriteLine($"{status} (pid {pid}): {result.Url}");
        else
            outw.WriteLine($"{status}: {result.Url}");
        return 0;
    }

    // ---------- read verbs (over the current workspace's symbols.db) ----------

    private static int Search(IReadOnlyList<string> args, WorkspaceContext ctx, TextWriter outw, TextWriter err)
    {
        CliOptions o = CliOptions.Parse(args, "json", "include-tests", "exclude-tests");
        if (string.IsNullOrWhiteSpace(o.Query))
            return Usage(err, "miller search <query> [--workspace-id SELECTOR] [--workspace DIR] [--mode auto|text|symbol|file|content|source|external|web|all-text] [--regions KINDS] [--file-pattern GLOB] [--language LANG] [--limit N] [--json] [--include-tests|--exclude-tests]");
        if (!TryResolveReadContext(ctx, o, err, out ctx))
            return 2;

        bool json = o.Has("json");
        int limit = o.Int("limit", SearchTool.DefaultLimit);
        string requestedMode = o.Value("mode", "auto")!;
        SearchToolMode mode = SearchTool.ParseMode(requestedMode);
        IReadOnlySet<string>? regionKinds;
        try
        {
            regionKinds = SearchTool.ParseRegionKinds(o.Value("regions"));
        }
        catch (InvalidOperationException ex)
        {
            err.WriteLine(ex.Message);
            return 2;
        }
        // exclude_tests tri-state: explicit CLI flags force a choice; otherwise the tool auto-hides for NL.
        bool? excludeTests = o.Has("exclude-tests") ? true : o.Has("include-tests") ? false : null;

        if (regionKinds is not null)
        {
            if (!RequireIndex(ctx, err))
                return 3;

            SymbolSearchSidecar sidecar = SymbolSearchSidecar.FromEnvironment();
            if (!sidecar.Enabled || !sidecar.RegionOptions.Enabled)
            {
                err.WriteLine("region search requires MILLER_REGION_INDEX=1 and a refreshed search sidecar.");
                return 3;
            }

            try
            {
                using var freshness = new FreshnessReader(ctx.ExtractDbPath);
                long revision = freshness.LatestRevision();
                string searchDb = SymbolSearchSidecar.SearchDbPathFor(ctx.ExtractDbPath);
                FtsRegionSearchIndex regionIndex = FtsRegionSearchIndex.Open(searchDb, revision);
                bool hideTests = SearchTool.ResolveExcludeTests(excludeTests, o.Query, mode);
                string? modeNote = mode == SearchToolMode.Auto
                    ? null
                    : $"mode={requestedMode} ignored; regions search uses source-region text.";
                outw.WriteLine(SearchTool.RunRegions(regionIndex, o.Query, regionKinds, limit, hideTests, json, out _,
                    modeNote: modeNote, filePattern: o.Value("file-pattern"), language: o.Value("language")));
                return 0;
            }
            catch (Exception ex) when (
                ex is FileNotFoundException or InvalidOperationException or IOException
                    or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                err.WriteLine("region search requires MILLER_REGION_INDEX=1 and a refreshed search sidecar: " + ex.Message);
                return 3;
            }
        }

        if (mode == SearchToolMode.Content)
        {
            if (!RequireIndex(ctx, err))
                return 3;

            try
            {
                using var freshness = new FreshnessReader(ctx.ExtractDbPath);
                long revision = freshness.LatestRevision();
                var contentSidecar = new ContentCorpusSidecar();
                FtsTextContentSearchIndex contentIndex = contentSidecar.OpenRequired(ctx.ExtractDbPath, revision);
                outw.WriteLine(SearchTool.RunContentCorpus(
                    contentIndex,
                    o.Query,
                    limit,
                    json,
                    out _,
                    filePattern: o.Value("file-pattern"),
                    language: o.Value("language")));
                return 0;
            }
            catch (Exception ex) when (
                ex is FileNotFoundException or InvalidOperationException or IOException
                    or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                err.WriteLine("content search requires a refreshed content corpus: " + ex.Message);
                return 3;
            }
        }

        if (mode is SearchToolMode.Source or SearchToolMode.External or SearchToolMode.Web or SearchToolMode.AllText)
        {
            if (!RequireIndex(ctx, err))
                return 3;

            try
            {
                using var freshness = new FreshnessReader(ctx.ExtractDbPath);
                long revision = freshness.LatestRevision();
                var contentSidecar = new ContentCorpusSidecar();
                FtsTextContentSearchIndex textIndex = contentSidecar.OpenRequired(ctx.ExtractDbPath, revision);
                bool hideTests = SearchTool.ResolveExcludeTests(excludeTests, o.Query, mode);
                IReadOnlyCollection<string> contentKinds = mode == SearchToolMode.Source
                    ? [TextContentKind.WorkspaceSource]
                    : SearchTool.ContentKindsForMode(mode);
                outw.WriteLine(SearchTool.RunTextContent(
                    textIndex,
                    o.Query,
                    contentKinds,
                    limit,
                    hideTests,
                    json,
                    out _,
                    out _,
                    filePattern: o.Value("file-pattern"),
                    language: o.Value("language")));
                return 0;
            }
            catch (Exception ex) when (
                ex is FileNotFoundException or InvalidOperationException or IOException
                    or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                err.WriteLine("text content search requires a refreshed content corpus: " + ex.Message);
                return 3;
            }
        }

        if (!TryLoadSymbolSearchIndex(ctx, err, out ISymbolLookupIndex index))
            return 3;
        outw.WriteLine(SearchTool.Run(index, o.Query, mode, limit, excludeTests, json, out _,
            filePattern: o.Value("file-pattern"), language: o.Value("language")));
        return 0;
    }

    private static int Content(IReadOnlyList<string> args, WorkspaceContext ctx, TextWriter outw, TextWriter err)
    {
        if (args.Count == 0)
            return Usage(err, "miller content <import|add-markdown|search|read|list|remove|export> [args] [--json]");

        string operation = args[0];
        CliOptions o = CliOptions.Parse(args.Skip(1).ToArray(), "json");
        bool json = o.Has("json");

        string? path = null;
        string? query = null;
        if (string.Equals(operation, "import", StringComparison.OrdinalIgnoreCase)
            || string.Equals(operation, "add", StringComparison.OrdinalIgnoreCase))
        {
            path = o.Query;
            if (string.IsNullOrWhiteSpace(path))
                return Usage(err, "miller content import <path> [--max-bytes N] [--json]");
        }
        else if (string.Equals(operation, "add-markdown", StringComparison.OrdinalIgnoreCase)
            || string.Equals(operation, "add_markdown", StringComparison.OrdinalIgnoreCase)
            || string.Equals(operation, "import-markdown", StringComparison.OrdinalIgnoreCase)
            || string.Equals(operation, "import_markdown", StringComparison.OrdinalIgnoreCase))
        {
            path = o.Query;
            if (string.IsNullOrWhiteSpace(path))
                return Usage(err, "miller content add-markdown <path> --url URL [--display-path NAME] [--json]");
            if (string.IsNullOrWhiteSpace(o.Value("url")))
                return Usage(err, "miller content add-markdown <path> --url URL [--display-path NAME] [--json]");
        }
        else if (string.Equals(operation, "search", StringComparison.OrdinalIgnoreCase))
        {
            query = o.Query;
            if (string.IsNullOrWhiteSpace(query))
                return Usage(err, "miller content search <query> [--kind KIND] [--workspace-id all|SELECTOR] [--limit N] [--json]");
        }

        var tool = new ContentTool(ctx, new ContentCorpusExternalStore());
        string output = tool.Content(
            operation,
            path: path,
            query: query,
            source_id: o.Value("source-id"),
            url: o.Value("url"),
            display_path: o.Value("display-path"),
            content_kind: o.Value("kind", o.Value("content-kind")),
            content_workspace_id: o.Value("content-workspace-id"),
            workspace_id: o.Value("workspace-id"),
            line: o.Has("line") ? o.Int("line", 0) : null,
            context_lines: o.Has("context-lines") ? o.Int("context-lines", ContentCorpusExternalStore.DefaultContextLines) : null,
            limit: o.Int("limit", SearchTool.DefaultLimit),
            max_bytes: LongOption(o, "max-bytes"),
            format: json ? "json" : "compact");

        if (output.StartsWith("content failed:", StringComparison.Ordinal))
        {
            err.WriteLine(output);
            return 3;
        }

        WriteOutput(outw, output);
        return 0;
    }

    private static int Patterns(IReadOnlyList<string> args, WorkspaceContext ctx, TextWriter outw, TextWriter err)
    {
        if (args.Count > 0 && args[0] is "--help" or "-h")
            return Usage(err, "miller patterns <list|summary|search> [--workspace-id SELECTOR] [--workspace DIR] [--pattern ID] [--language LANG] [--path GLOB] [--where key=value] [--limit N] [--json]");

        bool firstTokenIsFlag = args.Count > 0 && args[0].StartsWith("--", StringComparison.Ordinal);
        string operation = args.Count == 0 || firstTokenIsFlag ? "list" : args[0].ToLowerInvariant();
        if (operation is "help" or "--help" or "-h")
            return Usage(err, "miller patterns <list|summary|search> [--workspace-id SELECTOR] [--workspace DIR] [--pattern ID] [--language LANG] [--path GLOB] [--where key=value] [--limit N] [--json]");

        if (operation is not ("list" or "summary" or "summarize" or "search"))
            return Usage(err, "miller patterns <list|summary|search> [--workspace-id SELECTOR] [--workspace DIR] [--pattern ID] [--language LANG] [--path GLOB] [--where key=value] [--limit N] [--json]");

        CliOptions o = CliOptions.Parse((firstTokenIsFlag ? args : args.Skip(1)).ToArray(), "json");
        string? patternId = o.Value("pattern", o.Value("pattern-id"));
        if (string.IsNullOrWhiteSpace(patternId) && o.Positionals.Count > 0)
            patternId = o.Query;

        string? where = o.Value("where");
        if (!string.IsNullOrWhiteSpace(where))
        {
            try
            {
                PatternsTool.ParseWhere(where);
            }
            catch (InvalidOperationException ex)
            {
                err.WriteLine(ex.Message);
                return 2;
            }

            if (string.IsNullOrWhiteSpace(patternId))
                return Usage(err, "miller patterns search --pattern ID [--where key=value] [--json]");
        }

        if (operation == "search" && string.IsNullOrWhiteSpace(patternId))
            return Usage(err, "miller patterns search --pattern ID [--workspace-id SELECTOR] [--workspace DIR] [--language LANG] [--path GLOB] [--where key=value] [--limit N] [--json]");

        if (!TryResolveReadContext(ctx, o, err, out ctx))
            return 2;
        if (!RequireIndex(ctx, err))
            return 3;

        try
        {
            PatternToolResult result = PatternsTool.Run(
                new PatternFactsReader(),
                ctx.ExtractDbPath,
                operation,
                patternId,
                o.Value("language"),
                o.Value("path", o.Value("file-pattern")),
                where,
                o.Int("limit", PatternsTool.DefaultLimit),
                o.Has("json"));
            WriteOutput(outw, result.Output);
            return 0;
        }
        catch (Exception ex) when (
            ex is FileNotFoundException or InvalidOperationException or IOException
                or UnauthorizedAccessException or ArgumentException or NotSupportedException
                or SqliteException)
        {
            err.WriteLine("patterns failed: " + ex.Message);
            return 3;
        }
    }

    private static void WriteOutput(TextWriter writer, string output)
    {
        if (output.EndsWith('\n'))
            writer.Write(output);
        else
            writer.WriteLine(output);
    }

    private static int Telemetry(IReadOnlyList<string> args, WorkspaceContext ctx, TextWriter outw, TextWriter err)
    {
        if (args.Count == 0)
            return Usage(err, "miller telemetry export [--jsonl] [--workspace-id ID|all]");

        string operation = args[0].ToLowerInvariant();
        CliOptions o = CliOptions.Parse(args.Skip(1).ToArray(), "jsonl");
        if (operation != "export" || o.Positionals.Count > 0)
            return Usage(err, "miller telemetry export [--jsonl] [--workspace-id ID|all]");

        string output = TelemetryExportReader.ExportJsonLines(ctx.TelemetryDbPath, o.Value("workspace-id"));
        if (output.Length > 0)
            outw.Write(output);
        return 0;
    }

    // The bulk artifact JSONL feeds (`symbols export`, `complexity export`; cli-eros-v1): a fleet
    // orchestrator's alternative to per-query reads or Miller-private SQLite. One subop (`export`), the
    // standard read-context selectors, and an incompatible artifact surfaces through the shared
    // IncompatibleExtractException → exit-3 mapping in Run.
    private static int ArtifactExport(
        IReadOnlyList<string> args,
        WorkspaceContext ctx,
        TextWriter outw,
        TextWriter err,
        string usage,
        Func<string, string> export)
    {
        if (args.Count == 0 || args[0] is "--help" or "-h" or "help")
            return Usage(err, usage);
        string operation = args[0].ToLowerInvariant();
        CliOptions o = CliOptions.Parse(args.Skip(1).ToArray(), "jsonl");
        if (operation != "export" || o.Positionals.Count > 0)
            return Usage(err, usage);
        if (!TryResolveReadContext(ctx, o, err, out ctx))
            return 2;
        if (!RequireIndex(ctx, err))
            return 3;

        string output = export(ctx.ExtractDbPath);
        if (output.Length > 0)
            outw.Write(output);
        return 0;
    }

    private static int Refresh(IReadOnlyList<string> args, WorkspaceContext ctx, TextWriter outw, TextWriter err)
    {
        CliOptions o = CliOptions.Parse(args, "json", "wait", "full");
        if (o.Positionals.Count > 0)
            return Usage(err, "miller refresh [--json] [--wait] [--workspace-id SELECTOR|--workspace DIR] [--full]");

        string? id = o.Value("workspace-id", o.Value("id"));
        string? path = o.Value("workspace", o.Value("path"));
        if (!string.IsNullOrWhiteSpace(path) && o.Has("workspace"))
            path = Path.GetFullPath(path, ctx.WorkspaceRoot);

        // The CLI refresh path is already synchronous: it returns only after the lock-holding refresh attempt
        // either converges, observes another writer, or reports an operational failure. --wait is accepted as the
        // Eros-facing contract flag and does not need a second code path.
        return WorkspaceRefresh(ctx, id, path, force: o.Has("full"), json: o.Has("json"), outw, err);
    }

    private static int Inspect(IReadOnlyList<string> args, WorkspaceContext ctx, TextWriter outw, TextWriter err)
    {
        CliOptions o = CliOptions.Parse(args, "json");
        if (string.IsNullOrWhiteSpace(o.Query))
            return Usage(err, "miller inspect <file-or-symbol> [--workspace-id SELECTOR] [--workspace DIR] [--depth summary|full] [--kind K] [--scope FILE] [--limit N] [--json]");
        if (!TryResolveReadContext(ctx, o, err, out ctx))
            return 2;

        string depth = o.Value("depth", "summary")!;
        string output;
        if (string.Equals(depth, "full", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryLoadSymbolSearchIndex(ctx, err, out ISymbolLookupIndex index))
                return 3;

            output = InspectTool.RunLookup(
                index, ctx.ExtractDbPath, ctx.WorkspaceRoot,
                target: o.Query, depth, kind: o.Value("kind"), scope: o.Value("scope"),
                limit: o.Int("limit", 50), json: o.Has("json"), out _);
        }
        else
        {
            if (!TryLoadSymbolSearchIndex(ctx, err, out ISymbolLookupIndex index))
                return 3;

            output = InspectTool.RunSummary(
                index, ctx.ExtractDbPath, ctx.WorkspaceRoot,
                target: o.Query, kind: o.Value("kind"), scope: o.Value("scope"),
                limit: o.Int("limit", 50), json: o.Has("json"), out _);
        }
        outw.WriteLine(output);
        return 0;
    }

    private static int Context(IReadOnlyList<string> args, WorkspaceContext ctx, TextWriter outw, TextWriter err)
    {
        CliOptions o = CliOptions.Parse(args, "json", "exclude-tests");
        if (string.IsNullOrWhiteSpace(o.Query))
            return Usage(err, "miller context <query> [--workspace-id SELECTOR] [--workspace DIR] [--token-budget N] [--max-hops 0-2] [--reference-mode off|usage] [--reference-depth 0-1] [--exclude-tests] [--json]");
        if (!TryResolveReadContext(ctx, o, err, out ctx))
            return 2;

        if (!TryLoadSymbolSearchIndex(ctx, err, out ISymbolLookupIndex index))
            return 3;

        using var graph = new SqliteSymbolGraphIndex(ctx.ExtractDbPath);
        var resolver = new SmartTargetResolver(index);
        string referenceMode = o.Value("reference-mode", "off")!;
        string output;
        if (string.Equals(referenceMode, "usage", StringComparison.OrdinalIgnoreCase))
        {
            output = ContextTool.RunReferenceAware(
                index, graph, resolver, query: o.Query, tokenBudget: o.Int("token-budget", 4000), maxHops: o.Int("max-hops", 1),
                entrySymbols: null, failingTest: null, stackTrace: null,
                referenceDepth: o.Int("reference-depth", 1), excludeTests: o.Has("exclude-tests"), json: o.Has("json"),
                readReferences: symbol => ExtractReader.ReadReferences(ctx.ExtractDbPath, symbol.Name).Take(ContextTool.ReferenceRowsPerSymbol).ToArray(),
                readCallees: symbol => ExtractReader.ReadCallees(ctx.ExtractDbPath, symbol.SymbolId).Take(ContextTool.ReferenceRowsPerSymbol).ToArray(),
                readContentChunks: (symbols, excludeTests) => ContentCorpusContextReader.ReadContainingSymbolChunks(
                    ContentCorpusSidecar.ContentDbPathFor(ctx.ExtractDbPath),
                    symbols,
                    excludeTests,
                    ContextTool.ContentChunksPerSymbol),
                out _, out _);
        }
        else if (string.Equals(referenceMode, "off", StringComparison.OrdinalIgnoreCase))
        {
            output = ContextTool.Run(
                index, graph, resolver, query: o.Query, tokenBudget: o.Int("token-budget", 4000), maxHops: o.Int("max-hops", 1),
                entrySymbols: null, failingTest: null, stackTrace: null, json: o.Has("json"), out _, out _);
        }
        else
        {
            err.WriteLine("reference-mode must be off or usage.");
            return 2;
        }
        outw.WriteLine(output);
        return 0;
    }

    private static int Impact(IReadOnlyList<string> args, WorkspaceContext ctx, TextWriter outw, TextWriter err)
    {
        CliOptions o = CliOptions.Parse(args, "json");
        if (string.IsNullOrWhiteSpace(o.Query))
            return Usage(err, "miller impact <symbol> [--workspace-id SELECTOR] [--workspace DIR] [--max-depth N] [--limit N] [--json]");
        if (!TryResolveReadContext(ctx, o, err, out ctx))
            return 2;

        if (!TryLoadSymbolSearchIndex(ctx, err, out ISymbolLookupIndex index))
            return 3;

        using var graph = new SqliteSymbolGraphIndex(ctx.ExtractDbPath);
        var resolver = new SmartTargetResolver(index);
        string output = ImpactTool.Run(
            index, graph, resolver, target: o.Query, changedPaths: null, diff: null,
            maxDepth: o.Int("max-depth", 2), limit: o.Int("limit", 100), json: o.Has("json"), out _, out _);
        outw.WriteLine(output);
        return 0;
    }

    private static int Trace(IReadOnlyList<string> args, WorkspaceContext ctx, TextWriter outw, TextWriter err)
    {
        CliOptions o = CliOptions.Parse(args, "full", "json");
        if (string.IsNullOrWhiteSpace(o.Query))
            return Usage(err, "miller trace <symbol> [--workspace-id SELECTOR] [--workspace DIR] [--scope FILE] [--mode auto|path|bridge] [--to SYMBOL] [--depth N] [--limit N] [--full] [--json]");
        if (!TryResolveReadContext(ctx, o, err, out ctx))
            return 2;

        string mode = o.Value("mode", "auto")!;
        bool json = o.Has("json");
        if (string.Equals(mode, "bridge", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryLoadIndex(ctx, err, out MillerRepositoryIndex fullIndex))
                return 3;

            var fullResolver = new SmartTargetResolver(fullIndex);
            string bridgeOutput = TraceTool.Run(
                fullIndex, fullResolver, target: o.Query, scope: o.Value("scope"), mode: mode, to: o.Value("to"),
                depth: o.Int("depth", 3), limit: o.Int("limit", 20), fullFormat: o.Has("full"), json: json, out _, out _);
            outw.WriteLine(bridgeOutput);
            return 0;
        }

        if (!TryLoadSymbolSearchIndex(ctx, err, out ISymbolLookupIndex index))
            return 3;

        using var graph = new SqliteSymbolGraphIndex(ctx.ExtractDbPath);
        var resolver = new SmartTargetResolver(index);
        string output = TraceTool.RunGraph(
            index, graph, resolver, target: o.Query, scope: o.Value("scope"), mode: mode, to: o.Value("to"),
            depth: o.Int("depth", 3), limit: o.Int("limit", 20), fullFormat: o.Has("full"), json: json, out _, out _);
        outw.WriteLine(output);
        return 0;
    }

    // ---------- workspace verb ----------

    private static int Workspace(IReadOnlyList<string> args, WorkspaceContext ctx, TextWriter outw, TextWriter err)
    {
        CliOptions o = CliOptions.Parse(args, "json", "full");
        string operation = (o.Query.Length > 0 ? o.Query : "status").ToLowerInvariant();
        bool json = o.Has("json");

        // A help request must NOT fall through to `status` (which opens the registry and stamps a version
        // header). Cover all three spellings: `workspace help` (positional), `workspace --help` (flag, leaves
        // operation defaulting to status), and `workspace -h` (single-dash positional).
        if (operation is "help" or "-h" || o.Has("help"))
        {
            outw.WriteLine(WorkspaceHelpText);
            return 0;
        }

        // Selector parity with the read verbs (cli-eros-v1): --workspace-id aliases --id and --workspace
        // (a directory, resolved against the CLI's cwd root) aliases --path. A selector flag present without
        // a value is a usage error — silently falling back to the current workspace would run the operation
        // against the WRONG repo, the worst outcome for a fleet orchestrator (2026-06-11 Eros finding).
        foreach (string flag in new[] { "id", "workspace-id", "path", "workspace" })
        {
            if (o.Has(flag) && string.IsNullOrWhiteSpace(o.Value(flag)))
            {
                err.WriteLine($"--{flag} requires a value.");
                return 2;
            }
        }

        string? id = o.Value("id") ?? o.Value("workspace-id");
        string? path = o.Value("path");
        if (path is null && o.Value("workspace") is { } workspaceDir)
            path = Path.GetFullPath(workspaceDir, ctx.WorkspaceRoot);

        switch (operation)
        {
            case "list":
                return WorkspaceList(ctx, json, outw);
            case "status":
                return WorkspaceStatus(ctx, id, path, json, outw, err);
            case "health":
                return WorkspaceHealth(ctx, id, path, json, outw, err);
            case "refresh":
                return WorkspaceRefresh(ctx, id, path, force: false, json, outw, err);
            case "full":
                return WorkspaceRefresh(ctx, id, path, force: true, json, outw, err);
            case "open":
                return WorkspaceOpen(ctx, path, full: o.Has("full"), json, outw, err);
            case "remove":
                return WorkspaceRemove(ctx, id, path, json, outw, err);
            default:
                err.WriteLine($"unknown workspace operation '{operation}'. Use status|health|list|refresh|full|open|remove.");
                return 2;
        }
    }

    private static int WorkspaceList(WorkspaceContext ctx, bool json, TextWriter outw)
    {
        using WorkspaceRegistry registry = WorkspaceRegistry.Open(ctx.RegistryDbPath);
        IReadOnlyList<WorkspaceRegistryRow> rows = registry.List();
        WorkspaceRegistryRow? currentRow = FindCurrentWorkspaceRow(registry, ctx);
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
                Current: currentRow is not null
                    ? string.Equals(row.WorkspaceId, currentRow.WorkspaceId, StringComparison.Ordinal)
                    : WorkspaceSafety.IsLiveWorkspace(row.CanonicalRoot, ctx.WorkspaceRoot),
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
        WorkspaceRegistryRow? currentRow = FindCurrentWorkspaceRow(registry, ctx);
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
            ServerVersion: MillerVersion.Current,
            ServerProcessId: Environment.ProcessId,
            SearchSidecar: SymbolSearchSidecar.FromEnvironment().Inspect(ctx.ExtractDbPath, expectedRevision: 0),
            ContentCorpus: new ContentCorpusSidecar().Inspect(ctx.ExtractDbPath, expectedRevision: 0));
        outw.WriteLine(WorkspaceRender.Status(facts, TelemetrySummary.Empty, json));
        return 0;
    }

    private static int WorkspaceHealth(
        WorkspaceContext ctx, string? id, string? path, bool json, TextWriter outw, TextWriter err)
    {
        using WorkspaceRegistry registry = WorkspaceRegistry.Open(ctx.RegistryDbPath);

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

            WorkspaceFacts facts = FactsFromRowForHealth(registry, ctx, row);
            WorkspaceExtractionHealthFacts extraction = ReadHealthOrUnavailable(row.IndexDbPath, facts.WarningText);
            outw.WriteLine(WorkspaceRender.Health(
                WorkspaceHealthFacts.Create(
                    facts, TelemetrySummary.Empty, new TelemetryHealthFacts(0, 0, 0), extraction,
                    CliLeaderFacts(row.IndexDbPath)),
                json));
            return 0;
        }

        WorkspaceRegistryRow? currentRow = FindCurrentWorkspaceRow(registry, ctx);
        if (currentRow is not null)
        {
            WorkspaceFacts facts = FactsFromRowForHealth(registry, ctx, currentRow);
            WorkspaceExtractionHealthFacts extraction = ReadHealthOrUnavailable(currentRow.IndexDbPath, facts.WarningText);
            outw.WriteLine(WorkspaceRender.Health(
                WorkspaceHealthFacts.Create(
                    facts, TelemetrySummary.Empty, new TelemetryHealthFacts(0, 0, 0), extraction,
                    CliLeaderFacts(currentRow.IndexDbPath)),
                json));
            return 0;
        }

        if (!RequireIndex(ctx, err))
            return 3;
        WorkspaceIndexFacts indexFacts = WorkspaceIndexFactsReader.Read(ctx.ExtractDbPath);
        var localFacts = new WorkspaceFacts(
            Root: ctx.WorkspaceRoot,
            WorkspaceId: null,
            DbPath: ctx.ExtractDbPath,
            IsLeader: false,
            DocumentCount: indexFacts.DocumentCount,
            KnownExtensionsCount: indexFacts.KnownExtensionsCount,
            BuiltRevision: 0,
            LatestObservedRevision: 0,
            IndexFresh: null,
            QueueEmpty: true,
            FreshnessStatus: "unregistered",
            ServerVersion: MillerVersion.Current,
            ServerProcessId: Environment.ProcessId,
            SearchSidecar: SymbolSearchSidecar.FromEnvironment().Inspect(ctx.ExtractDbPath, expectedRevision: 0),
            ContentCorpus: new ContentCorpusSidecar().Inspect(ctx.ExtractDbPath, expectedRevision: 0));
        outw.WriteLine(WorkspaceRender.Health(
            WorkspaceHealthFacts.Create(
                localFacts,
                TelemetrySummary.Empty,
                new TelemetryHealthFacts(0, 0, 0),
                WorkspaceHealthReader.Read(ctx.ExtractDbPath),
                CliLeaderFacts(ctx.ExtractDbPath)),
            json));
        return 0;
    }

    // The one-shot CLI's leader facts: identity + liveness + the artifact's recorded binary_version (cheap
    // SQLite read — lets `leader_extractor_older_than_artifact` fire from the CLI too). The CLI does NOT probe
    // its own bundled extractor here (that would spawn a subprocess per status/health call); its own eligibility
    // is enforced — and explained — by the refresh/full gate when it actually tries to write.
    private static LeaderHealthFacts CliLeaderFacts(string indexDbPath) =>
        LeaderHealthFacts.Read(Path.GetDirectoryName(indexDbPath)!) with
        {
            ArtifactExtractorVersion = ExtractBinaryVersionReader.TryRead(indexDbPath),
        };

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
            ServerVersion: MillerVersion.Current,
            ServerProcessId: Environment.ProcessId,
            SearchSidecar: SymbolSearchSidecar.FromEnvironment().Inspect(row.IndexDbPath, revision),
            ContentCorpus: new ContentCorpusSidecar().Inspect(row.IndexDbPath, revision));
    }

    private static WorkspaceFacts FactsFromRowForHealth(WorkspaceRegistry registry, WorkspaceContext ctx, WorkspaceRegistryRow row)
    {
        try
        {
            WorkspaceFacts facts = FactsFromRow(registry, ctx, row);
            if (facts.WarningText is not null &&
                facts.WarningText.StartsWith("index DB not found:", StringComparison.OrdinalIgnoreCase))
            {
                return facts with { IndexFresh = false, FreshnessStatus = "missing_index" };
            }

            return facts;
        }
        catch (Exception ex) when (ex is FileNotFoundException || IsHealthIndexReadException(ex))
        {
            string error = ex is FileNotFoundException
                ? $"index DB not found: {row.IndexDbPath}"
                : $"could not read workspace index DB '{row.IndexDbPath}': {ex.Message}";
            return new WorkspaceFacts(
                Root: row.CanonicalRoot,
                WorkspaceId: row.WorkspaceId,
                DbPath: row.IndexDbPath,
                IsLeader: false,
                DocumentCount: 0,
                KnownExtensionsCount: 0,
                BuiltRevision: row.LastRevision ?? 0,
                LatestObservedRevision: row.LastRevision ?? 0,
                IndexFresh: false,
                QueueEmpty: true,
                FreshnessStatus: ex is FileNotFoundException ? "missing_index" : "unreadable_index",
                WarningText: error,
                DisplayId: row.DisplayId,
                ServerVersion: MillerVersion.Current,
                ServerProcessId: Environment.ProcessId,
                SearchSidecar: SymbolSearchSidecar.FromEnvironment().Inspect(row.IndexDbPath, row.LastRevision ?? 0),
                ContentCorpus: new ContentCorpusSidecar().Inspect(row.IndexDbPath, row.LastRevision ?? 0));
        }
    }

    private static WorkspaceExtractionHealthFacts ReadHealthOrUnavailable(string dbPath, string? error)
    {
        try
        {
            return WorkspaceHealthReader.Read(dbPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException || IsHealthIndexReadException(ex))
        {
            return UnavailableExtraction(string.IsNullOrWhiteSpace(error) ? ex.Message : error);
        }
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
            WorkspaceRegistryRow? currentRow = FindCurrentWorkspaceRow(registry, ctx);
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
        JulieExtractRunner runner;
        try
        {
            runner = JulieExtractRunner.Locate(ctx.ToolsRoot);
        }
        catch (FileNotFoundException ex)
        {
            err.WriteLine($"cannot refresh: {ex.Message}");
            return 3;
        }
        var sidecar = SymbolSearchSidecar.FromEnvironment();
        var refresh = new CrossWorkspaceRefreshService(registry, runner, sidecar);
        WorkspaceRefreshResult result = refresh.Refresh(row.WorkspaceId, force);

        var action = WorkspaceRefreshAction(result, force, sidecar);
        outw.WriteLine(WorkspaceRender.Action(action, json));
        return RefreshExitCode(result.Status);
    }

    private static WorkspaceRegistryRow? FindCurrentWorkspaceRow(WorkspaceRegistry registry, WorkspaceContext ctx)
    {
        string currentRoot = CurrentRootForRegistrySelection(ctx);
        string stableId = WorkspaceId.FromCanonicalRoot(currentRoot);
        WorkspaceRegistryRow? stableRow = registry.Get(stableId);
        if (stableRow is not null && WorkspaceSafety.IsLiveWorkspace(stableRow.CanonicalRoot, currentRoot))
            return stableRow;

        return registry.List()
            .Where(r => WorkspaceSafety.IsLiveWorkspace(r.CanonicalRoot, currentRoot))
            .OrderByDescending(r => r.LastSeenAt)
            .ThenBy(r => r.DisplayId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.WorkspaceId, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static string CurrentRootForRegistrySelection(WorkspaceContext ctx)
    {
        if (!string.IsNullOrWhiteSpace(ctx.CanonicalRoot))
            return ctx.CanonicalRoot;

        try
        {
            return PathCanonicalizer.CanonicalizeRoot(ctx.WorkspaceRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            return ctx.WorkspaceRoot;
        }
    }

    private static WorkspaceActionResult WorkspaceRefreshAction(
        WorkspaceRefreshResult result,
        bool force,
        SymbolSearchSidecar sidecar)
    {
        long revision = result.Revision ?? 0;
        bool? indexFresh = result.Status switch
        {
            WorkspaceRefreshStatus.Refreshed or WorkspaceRefreshStatus.Unchanged => true,
            WorkspaceRefreshStatus.LockBusy
                or WorkspaceRefreshStatus.MissingRoot
                or WorkspaceRefreshStatus.MissingIndex
                or WorkspaceRefreshStatus.Failed
                or WorkspaceRefreshStatus.IneligibleExtractor => false,
            _ => null,
        };

        return new WorkspaceActionResult(
            Operation: force ? "full" : "refresh",
            Scanned: result.Scanned,
            Swapped: false,
            Revision: revision,
            Note: result.Error ?? result.WarningText,
            WorkspaceId: result.WorkspaceId,
            Root: result.WorkspaceRoot,
            Status: result.StatusText,
            IndexFresh: indexFresh,
            SearchSidecar: sidecar.Inspect(result.IndexDbPath, revision),
            ContentCorpus: new ContentCorpusSidecar().Inspect(result.IndexDbPath, revision),
            ScanDurationMs: (long?)result.ScanDuration?.TotalMilliseconds,
            DurationMs: (long?)result.TotalDuration?.TotalMilliseconds);
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
            Status: result.StatusText,
            ScanDurationMs: (long?)result.ScanDuration?.TotalMilliseconds,
            DurationMs: (long?)result.TotalDuration?.TotalMilliseconds);
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

        // Delete the index data while HOLDING the writer lock, so no Miller process can start writing this
        // index mid-delete. Only the held lock file itself is skipped (an open FileShare.None handle cannot be
        // deleted on Windows); after release, the leftover lock file + empty dir are removed best-effort — a
        // writer that sneaks in after release finds an already-empty index and does a clean rebuild.
        using (IDisposable? lease = SingleWriterLock.TryAcquire(millerDir))
        {
            if (lease is null)
            {
                outw.WriteLine(WorkspaceRender.Remove(WorkspaceRemoveResult.RefusedInUse(millerDir, workspaceId, root), json));
                return RemoveExitCode(WorkspaceRemoveResult.Outcome.RefusedInUse);
            }
            SingleWriterLock.DeleteContentsExceptLock(millerDir);
        }

        SingleWriterLock.TryDeleteEmptiedDir(millerDir);
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

    // Map a refresh/full outcome to a process exit code (cli-eros-v1: exit 0 = ingestable payload, exit 3 =
    // genuinely unusable index). LockBusy is exit 0: the latest readable DB IS being served and a LIVE leader
    // owns convergence — the payload says so (`status: lock_busy`, `index_fresh: false`), so a consumer that
    // needs CONFIRMED freshness must gate on those fields, not the exit code (2026-06-11 Eros ask; previously 3,
    // which forced Eros to parse exit-3 stdout against the "non-zero = non-ingestable" rule). A missing
    // root/index, a hard failure, or an ineligible extractor (nothing usable was served or the rebuild broke)
    // stay operational failures (3); any future status is unexpected (1).
    internal static int RefreshExitCode(WorkspaceRefreshStatus status) => status switch
    {
        WorkspaceRefreshStatus.Refreshed
            or WorkspaceRefreshStatus.Unchanged
            or WorkspaceRefreshStatus.LockBusy => 0,
        WorkspaceRefreshStatus.MissingRoot
            or WorkspaceRefreshStatus.MissingIndex
            or WorkspaceRefreshStatus.Failed
            or WorkspaceRefreshStatus.IneligibleExtractor => 3,
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

    private static bool TryLoadSymbolSearchIndex(WorkspaceContext ctx, TextWriter err, out ISymbolLookupIndex index)
    {
        if (!RequireIndex(ctx, err))
        {
            index = null!;
            return false;
        }

        SymbolSearchSidecar sidecar = SymbolSearchSidecar.FromEnvironment();
        if (sidecar.Enabled)
        {
            FtsSymbolSearchIndex? sidecarIndex = TryOpenFreshSymbolSearchSidecar(ctx.ExtractDbPath, sidecar);
            if (sidecarIndex is not null)
            {
                index = sidecarIndex;
                return true;
            }
        }

        index = SymbolSearchProjectionLoader.Load(ctx.ExtractDbPath);
        return true;
    }

    private static FtsSymbolSearchIndex? TryOpenFreshSymbolSearchSidecar(string dbPath, SymbolSearchSidecar sidecar)
    {
        try
        {
            using var freshness = new FreshnessReader(dbPath);
            return sidecar.TryOpen(dbPath, freshness.LatestRevision());
        }
        catch (Exception ex) when (
            ex is FileNotFoundException or InvalidOperationException or IOException
                or UnauthorizedAccessException or ArgumentException or NotSupportedException
                or Microsoft.Data.Sqlite.SqliteException)
        {
            return null;
        }
    }

    private static bool TryResolveReadContext(
        WorkspaceContext ctx,
        CliOptions options,
        TextWriter err,
        out WorkspaceContext readContext)
    {
        readContext = ctx;
        bool idFlagPresent = options.Has("workspace-id");
        bool pathFlagPresent = options.Has("workspace");
        if (!idFlagPresent && !pathFlagPresent)
            return true;

        // A valueless selector flag is a usage error in every combination — it must never be masked by the
        // other flag or fall back silently to the current workspace (the lifecycle verbs already enforce this).
        string? selector = options.Value("workspace-id");
        if (idFlagPresent && string.IsNullOrWhiteSpace(selector))
        {
            err.WriteLine("--workspace-id requires a value.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(selector))
        {
            string? path = options.Value("workspace");
            if (string.IsNullOrWhiteSpace(path))
            {
                err.WriteLine("--workspace requires a value.");
                return false;
            }

            selector = Path.GetFullPath(path, ctx.WorkspaceRoot);
        }

        if (IsCurrentReadSelector(ctx, selector))
            return true;

        using WorkspaceRegistry registry = WorkspaceRegistry.Open(ctx.RegistryDbPath);
        WorkspaceRegistryRow row;
        try
        {
            row = WorkspaceRegistrySelector.Resolve(registry, selector);
        }
        catch (KeyNotFoundException ex)
        {
            err.WriteLine(ex.Message);
            return false;
        }

        readContext = ctx with
        {
            WorkspaceRoot = row.CanonicalRoot,
            ExtractDbPath = row.IndexDbPath,
            WorkspaceId = row.WorkspaceId,
            CanonicalRoot = row.CanonicalRoot,
            CanonicalExtractDbPath = row.IndexDbPath,
        };
        return true;
    }

    private static bool IsCurrentReadSelector(WorkspaceContext ctx, string selector)
    {
        string trimmed = selector.Trim();
        if (string.Equals(trimmed, "current", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "primary", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrWhiteSpace(ctx.WorkspaceId) &&
            string.Equals(trimmed, ctx.WorkspaceId, StringComparison.Ordinal))
            return true;

        string root = ctx.CanonicalRoot ?? ctx.WorkspaceRoot;
        if (Path.IsPathRooted(trimmed) && WorkspaceSafety.IsLiveWorkspace(trimmed, root))
            return true;

        if (string.IsNullOrWhiteSpace(ctx.WorkspaceId))
            return false;

        try
        {
            string displayId = WorkspaceId.Display(root, ctx.WorkspaceId);
            return string.Equals(trimmed, displayId, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool RequireIndex(WorkspaceContext ctx, TextWriter err)
    {
        if (File.Exists(ctx.ExtractDbPath))
            return true;
        err.WriteLine($"no Miller index at {ctx.ExtractDbPath}.");
        err.WriteLine("Build it with `miller workspace full`, or open this folder in the Miller MCP server.");
        return false;
    }

    private static long? LongOption(CliOptions options, string name)
    {
        string? value = options.Value(name);
        return long.TryParse(
            value,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out long parsed)
            ? parsed
            : null;
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
          capabilities      Print Miller build, extract-contract, optional feature, and export-format facts.
                             [--json]
          search <query>     Find code by name, identifier, or phrase.
                             [--workspace-id SELECTOR] [--workspace DIR] [--mode auto|text|symbol|file|content|source|external|web|all-text] [--regions KINDS] [--file-pattern GLOB] [--language LANG] [--limit N] [--json] [--include-tests|--exclude-tests]
          content <op>       Import/search/read/list/remove/export external and web text in content.db.
                             import <path> [--max-bytes N] [--display-path NAME] [--json]
                             add-markdown <path> --url URL [--display-path NAME] [--json]
                             search <query> [--kind KIND] [--workspace-id all|SELECTOR] [--limit N] [--json]
                             read --source-id ID --line N [--context-lines N] [--json]
                             list [--kind KIND] [--json]
                             remove --source-id ID [--json]
                             export [--kind KIND] [--content-workspace-id ID]   # JSONL
          patterns <op>      List, summarize, or search extractor-recognized code-shape facts.
                             op = list | summary | search
                             [--workspace-id SELECTOR] [--workspace DIR] [--pattern ID] [--language LANG] [--path GLOB] [--where key=value] [--limit N] [--json]
          telemetry <op>     Export machine-global Miller telemetry.
                             export [--jsonl] [--workspace-id ID|all]
          symbols <op>       Bulk-export every symbol row for fleet rollups.   # JSONL
                             export [--jsonl] [--workspace-id SELECTOR] [--workspace DIR]
          complexity <op>    Bulk-export per-symbol/per-file complexity metrics.   # JSONL
                             export [--jsonl] [--workspace-id SELECTOR] [--workspace DIR]
          refresh            Refresh a registered workspace index and return after convergence attempt.
                             [--json] [--wait] [--workspace-id SELECTOR|--workspace DIR] [--full]
          inspect <target>   List a file's symbols, or show a symbol's definition.
                             [--workspace-id SELECTOR] [--workspace DIR] [--depth summary|full] [--kind K] [--scope FILE] [--limit N] [--json]
          context <query>    Token-budgeted bundle of the most relevant code for a task.
                             [--workspace-id SELECTOR] [--workspace DIR] [--token-budget N] [--max-hops 0-2] [--reference-mode off|usage] [--reference-depth 0-1] [--exclude-tests] [--json]
          impact <symbol>    Downstream symbols + tests a change would affect.
                             [--workspace-id SELECTOR] [--workspace DIR] [--max-depth N] [--limit N] [--json]
          trace <symbol>     Follow callers/callees, a path, or a cross-language bridge.
                             [--workspace-id SELECTOR] [--workspace DIR] [--scope FILE] [--mode auto|path|bridge] [--to SYMBOL] [--depth N] [--limit N] [--full] [--json]
          dashboard          Start or reuse the machine-global loopback dashboard.
                             [--port N] [--json]
          workspace [op]     Index lifecycle. op = status (default) | health | list | refresh | full | open | remove.
                             open   [--path DIR] [--full]   Register + index a directory (creates .miller/symbols.db).
                             remove (--id ID | --path DIR)  Delete a workspace's .miller index dir.
                             [--id|--workspace-id SELECTOR] [--path|--workspace DIR] [--json]
          version            Print the build version (e.g. 0.3.2+<sha>).
          help               Show this help.
          serve              Run the MCP stdio server (the default when launched with no arguments).
        """;

    private const string WorkspaceHelpText =
        """
        miller workspace — index lifecycle for the current (or a selected) workspace.

        Usage: miller workspace [op] [args]

        Operations:
          status   Show the live index status + build version (the default when no op is given).
          health   Show a short workspace readiness verdict plus stable JSON with quality warnings.
          list     List every registered workspace in ~/.miller/workspaces.db.
          refresh  Incrementally refresh the index if the working tree changed.
          full     Force a full re-index (ignores the freshness check).
          open     Register + index a directory (creates .miller/symbols.db).  [--path DIR] [--full]
          remove   Delete a workspace's .miller index dir.                     (--id ID | --path DIR)

        Selectors / flags: [--id|--workspace-id SELECTOR] [--path|--workspace DIR] [--json]
          --workspace-id aliases --id; --workspace (a directory, resolved against the cwd) aliases --path —
          the same selector flags every read verb accepts.
        """;
}
