using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Core.DeadCode;
using Miller.Indexing;
using Miller.Server.Git;
using Miller.Server.Hosting;
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
        Run(args, context, stdout, stderr, new DashboardCliLauncher(), new ProcessGitDiffReader());

    internal static int Run(
        IReadOnlyList<string> args,
        WorkspaceContext context,
        TextWriter stdout,
        TextWriter stderr,
        IDashboardLauncher dashboardLauncher) =>
        Run(args, context, stdout, stderr, dashboardLauncher, new ProcessGitDiffReader());

    internal static int Run(
        IReadOnlyList<string> args,
        WorkspaceContext context,
        TextWriter stdout,
        TextWriter stderr,
        IGitDiffReader gitDiffReader) =>
        Run(args, context, stdout, stderr, new DashboardCliLauncher(), gitDiffReader);

    internal static int Run(
        IReadOnlyList<string> args,
        WorkspaceContext context,
        TextWriter stdout,
        TextWriter stderr,
        IDashboardLauncher dashboardLauncher,
        IGitDiffReader gitDiffReader)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        ArgumentNullException.ThrowIfNull(dashboardLauncher);
        ArgumentNullException.ThrowIfNull(gitDiffReader);

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
                case "todos":
                    return Todos(rest, context, stdout, stderr);
                case "content":
                    return Content(rest, context, stdout, stderr);
                case "patterns":
                    if (rest.Count > 0 && rest[0].Equals("export", StringComparison.OrdinalIgnoreCase))
                        return ArtifactExport(rest, context, stdout, stderr,
                            "miller patterns export [--jsonl] [--workspace-id SELECTOR] [--workspace DIR]",
                            PatternFactsExportReader.WriteJsonLines);
                    return Patterns(rest, context, stdout, stderr);
                case "metrics":
                    return Metrics(rest, context, stdout, stderr);
                case "report":
                    return Report(rest, context, stdout, stderr);
                case "telemetry":
                    return Telemetry(rest, context, stdout, stderr);
                case "symbols":
                    return ArtifactExport(rest, context, stdout, stderr,
                        "miller symbols export [--jsonl] [--workspace-id SELECTOR] [--workspace DIR]",
                        SymbolExportReader.WriteJsonLines);
                case "references":
                    // `references candidates` is the deterministic dead-code candidate listing (Miller-owned,
                    // named suppressions); every other op (i.e. `export`) keeps the bulk JSONL fact feed unchanged.
                    if (rest.Count > 0 && rest[0].Equals("candidates", StringComparison.OrdinalIgnoreCase))
                        return ReferencesCandidates(rest.Skip(1).ToList(), context, stdout, stderr);
                    return ArtifactExport(rest, context, stdout, stderr,
                        "miller references export [--jsonl] [--workspace-id SELECTOR] [--workspace DIR]",
                        ReferenceExportReader.WriteJsonLines);
                case "complexity":
                    return ArtifactExport(rest, context, stdout, stderr,
                        "miller complexity export [--jsonl] [--workspace-id SELECTOR] [--workspace DIR]",
                        ComplexityExportReader.WriteJsonLines);
                case "refresh":
                    return Refresh(rest, context, stdout, stderr);
                case "inspect":
                    return Inspect(rest, context, stdout, stderr);
                case "context":
                    return Context(rest, context, stdout, stderr);
                case "impact":
                    return Impact(rest, context, stdout, stderr, gitDiffReader);
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

    private static int Todos(IReadOnlyList<string> args, WorkspaceContext ctx, TextWriter outw, TextWriter err)
    {
        CliOptions o = CliOptions.Parse(args, "json", "exclude-tests");
        if (o.Positionals.Count > 0)
            return Usage(err, "miller todos [--markers TODO,FIXME,HACK,XXX] [--workspace-id SELECTOR] [--workspace DIR] [--file-pattern GLOB] [--language LANG] [--limit N] [--json] [--exclude-tests]");
        if (!TryResolveReadContext(ctx, o, err, out ctx))
            return 2;
        if (!RequireIndex(ctx, err))
            return 3;

        IReadOnlyList<string> markers;
        try
        {
            markers = MarkerSearch.ParseMarkers(o.Value("markers"));
        }
        catch (InvalidOperationException ex)
        {
            err.WriteLine(ex.Message);
            return Usage(err, "miller todos [--markers TODO,FIXME,HACK,XXX] [--workspace-id SELECTOR] [--workspace DIR] [--file-pattern GLOB] [--language LANG] [--limit N] [--json] [--exclude-tests]");
        }

        SymbolSearchSidecar sidecar = SymbolSearchSidecar.FromEnvironment();
        if (!sidecar.Enabled || !sidecar.RegionOptions.Enabled)
        {
            err.WriteLine("region search is disabled. Enable MILLER_SEARCH_SIDECAR and unset MILLER_REGION_INDEX=0, then refresh the workspace.");
            return 3;
        }

        try
        {
            using var freshness = new FreshnessReader(ctx.ExtractDbPath);
            long revision = freshness.LatestRevision();
            string searchDb = SymbolSearchSidecar.SearchDbPathFor(ctx.ExtractDbPath);
            FtsRegionSearchIndex regionIndex = FtsRegionSearchIndex.Open(searchDb, revision);
            outw.WriteLine(MarkerSearch.Run(
                regionIndex,
                markers,
                o.Int("limit", MarkerSearch.DefaultLimit),
                o.Has("exclude-tests"),
                o.Has("json"),
                compactBanner: null,
                filePattern: o.Value("file-pattern"),
                language: o.Value("language"),
                out _));
            return 0;
        }
        catch (Exception ex) when (
            ex is FileNotFoundException or InvalidOperationException or IOException
                or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            err.WriteLine("todos requires a refreshed source-region search sidecar: " + ex.Message);
            return 3;
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
            return Usage(err, "miller search <query> [--workspace-id SELECTOR] [--workspace DIR] [--mode auto|text|symbol|file|markers|content|source|external|web|all-text] [--regions KINDS] [--file-pattern GLOB] [--language LANG] [--limit N] [--json] [--include-tests|--exclude-tests]");
        if (!TryResolveReadContext(ctx, o, err, out ctx))
            return 2;

        bool json = o.Has("json");
        int limit = o.Int("limit", SearchTool.DefaultLimit);
        string requestedMode = o.Value("mode", "auto")!;
        SearchRoute route;
        try
        {
            route = SearchRoutePlanner.Plan(requestedMode, o.Value("regions"));
        }
        catch (InvalidOperationException ex)
        {
            err.WriteLine(ex.Message);
            return 2;
        }
        // exclude_tests tri-state: explicit CLI flags force a choice; otherwise the tool auto-hides for NL.
        bool? excludeTests = o.Has("exclude-tests") ? true : o.Has("include-tests") ? false : null;
        var executionRequest = new SearchRouteExecutionRequest(
            o.Query,
            limit,
            json,
            excludeTests,
            FilePattern: o.Value("file-pattern"),
            Language: o.Value("language"));

        if (route.Kind is SearchRouteKind.Regions or SearchRouteKind.Markers)
        {
            if (!RequireIndex(ctx, err))
                return 3;

            SymbolSearchSidecar sidecar = SymbolSearchSidecar.FromEnvironment();
            if (!sidecar.Enabled || !sidecar.RegionOptions.Enabled)
            {
                err.WriteLine("region search is disabled. Enable MILLER_SEARCH_SIDECAR and unset MILLER_REGION_INDEX=0, then refresh the workspace.");
                return 3;
            }

            try
            {
                using var freshness = new FreshnessReader(ctx.ExtractDbPath);
                long revision = freshness.LatestRevision();
                string searchDb = SymbolSearchSidecar.SearchDbPathFor(ctx.ExtractDbPath);
                FtsRegionSearchIndex regionIndex = FtsRegionSearchIndex.Open(searchDb, revision);
                SearchRouteExecutionResult result = route.Kind == SearchRouteKind.Markers
                    ? SearchRouteExecutor.RunMarkers(regionIndex, route, executionRequest)
                    : SearchRouteExecutor.RunRegions(regionIndex, route, executionRequest);
                outw.WriteLine(result.Output);
                return 0;
            }
            catch (Exception ex) when (
                ex is FileNotFoundException or InvalidOperationException or IOException
                    or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                err.WriteLine("region search requires a refreshed source-region search sidecar: " + ex.Message);
                return 3;
            }
        }

        if (route.Kind == SearchRouteKind.Content)
        {
            if (!RequireIndex(ctx, err))
                return 3;

            try
            {
                using var freshness = new FreshnessReader(ctx.ExtractDbPath);
                long revision = freshness.LatestRevision();
                var contentSidecar = new ContentCorpusSidecar();
                FtsTextContentSearchIndex contentIndex = contentSidecar.OpenRequired(ctx.ExtractDbPath, revision);
                outw.WriteLine(SearchRouteExecutor.RunContent(
                    contentIndex,
                    route,
                    executionRequest).Output);
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

        if (route.Kind == SearchRouteKind.TextContent)
        {
            if (!RequireIndex(ctx, err))
                return 3;

            try
            {
                using var freshness = new FreshnessReader(ctx.ExtractDbPath);
                long revision = freshness.LatestRevision();
                var contentSidecar = new ContentCorpusSidecar();
                FtsTextContentSearchIndex textIndex = contentSidecar.OpenRequired(ctx.ExtractDbPath, revision);
                outw.WriteLine(SearchRouteExecutor.RunTextContent(
                    textIndex,
                    route,
                    executionRequest).Output);
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
        outw.WriteLine(SearchRouteExecutor.RunSymbols(index, route, executionRequest).Output);
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
            return Usage(err, "miller patterns <list|summary|search|export> [--workspace-id SELECTOR] [--workspace DIR] [--pattern ID] [--query TEXT] [--language LANG] [--path GLOB] [--where key=value] [--group-by file|directory] [--facet KEY] [--limit N] [--json]");

        bool firstTokenIsFlag = args.Count > 0 && args[0].StartsWith("--", StringComparison.Ordinal);
        string operation = args.Count == 0 || firstTokenIsFlag ? "list" : args[0].ToLowerInvariant();
        if (operation is "help" or "--help" or "-h")
            return Usage(err, "miller patterns <list|summary|search|export> [--workspace-id SELECTOR] [--workspace DIR] [--pattern ID] [--query TEXT] [--language LANG] [--path GLOB] [--where key=value] [--group-by file|directory] [--facet KEY] [--limit N] [--json]");

        if (operation is not ("list" or "summary" or "summarize" or "search"))
            return Usage(err, "miller patterns <list|summary|search|export> [--workspace-id SELECTOR] [--workspace DIR] [--pattern ID] [--query TEXT] [--language LANG] [--path GLOB] [--where key=value] [--group-by file|directory] [--facet KEY] [--limit N] [--json]");

        IReadOnlyList<string> argTail = (firstTokenIsFlag ? args : args.Skip(1)).ToArray();
        CliOptions o = CliOptions.Parse(argTail, "json");
        string? patternId = o.Value("pattern", o.Value("pattern-id"));
        if (string.IsNullOrWhiteSpace(patternId) && o.Positionals.Count > 0)
            patternId = o.Query;

        string? query = o.Value("query");
        string? where = CombineWhereFilters(CollectRepeatedOptionValues(argTail, "where"));
        if (!string.IsNullOrWhiteSpace(where))
        {
            try
            {
                _ = PatternsTool.ParseWhereFilters(where);
            }
            catch (InvalidOperationException ex)
            {
                err.WriteLine(ex.Message);
                return 2;
            }

            if (string.IsNullOrWhiteSpace(patternId) && string.IsNullOrWhiteSpace(query))
                return Usage(err, "miller patterns search --pattern ID | --query TEXT [--where key=value] [--json]");
        }

        if (operation == "search" && string.IsNullOrWhiteSpace(patternId) && string.IsNullOrWhiteSpace(query))
            return Usage(err, "miller patterns search --pattern ID | --query TEXT [--workspace-id SELECTOR] [--workspace DIR] [--language LANG] [--path GLOB] [--where key=value] [--limit N] [--json]");

        if ((operation is "list" or "summary" or "summarize") && !string.IsNullOrWhiteSpace(query))
        {
            err.WriteLine("patterns query is only supported for search.");
            return 2;
        }

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
                query,
                o.Value("language"),
                o.Value("path", o.Value("file-pattern")),
                where,
                o.Value("group-by", o.Value("group_by")),
                o.Value("facet"),
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

    private static string? CombineWhereFilters(IReadOnlyList<string> whereFilters) =>
        whereFilters.Count switch
        {
            0 => null,
            1 => whereFilters[0],
            _ => string.Join(";", whereFilters),
        };

    private static IReadOnlyList<string> CollectRepeatedOptionValues(IReadOnlyList<string> args, string flagName)
    {
        var values = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            string token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal) || token.Length == 2)
                continue;

            string name = token[2..];
            int equals = name.IndexOf('=', StringComparison.Ordinal);
            if (equals >= 0)
            {
                if (name[..equals].Equals(flagName, StringComparison.OrdinalIgnoreCase))
                    values.Add(name[(equals + 1)..]);
                continue;
            }

            if (!name.Equals(flagName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                values.Add(args[++i]);
        }

        return values;
    }

    private static void WriteOutput(TextWriter writer, string output)
    {
        if (output.EndsWith('\n'))
            writer.Write(output);
        else
            writer.WriteLine(output);
    }

    private static int Metrics(IReadOnlyList<string> args, WorkspaceContext ctx, TextWriter outw, TextWriter err)
    {
        const string usage = "miller metrics <churn|clones|complexity|risk|history> [--workspace-id SELECTOR] [--workspace DIR] [--limit N] [--json] [--range REV..REV] [--include-commits] [--min-count N] [--max-symbols-per-group N] [--min-severity low|moderate|high] [--include-tests|--exclude-tests] [--metric a,b,…]";
        if (args.Count > 0 && args[0] is "--help" or "-h" or "help")
            return Usage(err, usage);

        bool firstTokenIsFlag = args.Count > 0 && args[0].StartsWith("--", StringComparison.Ordinal);
        string operation = args.Count == 0 || firstTokenIsFlag ? "complexity" : args[0].ToLowerInvariant();

        // `history` is a read over the metric-history sidecar (history.db), not a git/symbols metric run: it takes a
        // different flag set (--metric) and renders a trend, so it branches out before the churn/risk recorder path.
        if (operation == "history")
            return MetricsHistory(args.Skip(1).ToList(), ctx, outw, err);

        if (operation is not ("churn" or "clones" or "clone" or "duplicate" or "duplicates" or "complexity" or "hotspots" or "risk"))
            return Usage(err, usage);

        CliOptions o = CliOptions.Parse((firstTokenIsFlag ? args : args.Skip(1)).ToArray(), "json", "include-tests", "exclude-tests", "include-commits");
        if (!TryResolveReadContext(ctx, o, err, out ctx))
            return 2;
        if (!RequireIndex(ctx, err))
            return 3;

        // Only churn/risk record (the git-backed arms); clones/complexity are covered by the leader converge arm.
        // Canonical = default range/limit/test-filter and no --include-commits; capture identity BEFORE computing.
        bool recordable = operation is "churn" or "risk";
        bool canonical = recordable
            && !o.Has("range") && !o.Has("limit")
            && !o.Has("include-tests") && !o.Has("exclude-tests") && !o.Has("include-commits");
        HeavyArmIdentity? identity = canonical ? CaptureHeavyArmIdentity(ctx) : null;

        try
        {
            MetricsToolResult result = MetricsTool.Run(
                ctx.ExtractDbPath,
                operation,
                o.Int("limit", MetricsTool.DefaultLimit),
                o.Has("json"),
                o.Int("min-count", 2),
                o.Int("max-symbols-per-group", MetricsTool.DefaultCloneSymbolsPerGroup),
                o.Value("min-severity", "moderate"),
                includeTests: !o.Has("exclude-tests"),
                workspaceRoot: ctx.WorkspaceRoot,
                range: o.Value("range", "HEAD~20..HEAD"),
                includeCommits: o.Has("include-commits"),
                historyReader: new ProcessGitHistoryReader());
            WriteOutput(outw, result.Output);
            if (recordable)
                RecordHeavyArmSnapshot(
                    ctx,
                    identity,
                    operation == "churn" ? MetricHistoryHeavyArm.ChurnSource : MetricHistoryHeavyArm.RiskSource,
                    result.SnapshotMetrics ?? Array.Empty<MetricHistoryPoint>(),
                    canonical,
                    err);
            return 0;
        }
        catch (Exception ex) when (
            ex is FileNotFoundException or InvalidOperationException or IOException
                or UnauthorizedAccessException or ArgumentException or NotSupportedException
                or SqliteException)
        {
            err.WriteLine("metrics failed: " + ex.Message);
            return 3;
        }
    }

    // `miller metrics history` — read-only trend over the workspace history.db sidecar (no git, no recording). The
    // JSON envelope is the stable metrics-history-v1 contract Eros consumes (docs/contracts/metrics-history-v1.md).
    private static int MetricsHistory(
        IReadOnlyList<string> args, WorkspaceContext ctx, TextWriter outw, TextWriter err)
    {
        const string usage =
            "miller metrics history [--metric a,b,…] [--limit N] [--json] [--workspace-id SELECTOR] [--workspace DIR]";
        if (args.Count > 0 && args[0] is "--help" or "-h" or "help")
            return Usage(err, usage);

        CliOptions o = CliOptions.Parse(args.ToArray(), "json");
        if (o.Positionals.Count > 0)
            return Usage(err, usage);
        if (!TryResolveReadContext(ctx, o, err, out ctx))
            return 2;
        if (!RequireIndex(ctx, err))
            return 3;

        IReadOnlyList<string> metrics = ParseMetricFilter(args);
        int limit = o.Int("limit", MetricsTool.DefaultHistoryLimit);
        string workspaceId = ResolveWorkspaceId(ctx);

        try
        {
            string historyDbPath = MetricSnapshotAggregates.HistoryDbPathFor(ctx.ExtractDbPath);
            MetricsToolResult result = MetricsTool.RunHistory(historyDbPath, workspaceId, metrics, limit, o.Has("json"));
            WriteOutput(outw, result.Output);
            return 0;
        }
        // A PRESENT-but-unreadable history.db is an operational failure (exit 3), NOT the friendly empty-history
        // exit-0 path — an absent file stays empty-success inside RunHistory (see docs/contracts/metrics-history-v1.md).
        catch (Exception ex) when (
            ex is FileNotFoundException or InvalidOperationException or IOException
                or UnauthorizedAccessException or ArgumentException or NotSupportedException
                or SqliteException or MetricHistoryUnreadableException)
        {
            err.WriteLine("metrics failed: " + ex.Message);
            return 3;
        }
    }

    // Collect --metric values, supporting BOTH comma-separated (`--metric a,b`) and repeated (`--metric a --metric b`)
    // forms, de-duplicated in first-seen order. An empty result ⟹ MetricsTool.RunHistory applies its default set.
    private static IReadOnlyList<string> ParseMetricFilter(IReadOnlyList<string> args)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string raw in CollectRepeatedOptionValues(args, "metric"))
        {
            foreach (string part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (seen.Add(part))
                    result.Add(part);
            }
        }
        return result;
    }

    // The workspace id for the metrics-history-v1 envelope: the bootstrap-set id when known, else derived from the
    // canonical root — the same resolution CaptureHeavyArmIdentity uses, so a read and a write agree on identity.
    private static string ResolveWorkspaceId(WorkspaceContext ctx) =>
        !string.IsNullOrWhiteSpace(ctx.WorkspaceId)
            ? ctx.WorkspaceId!
            : WorkspaceId.FromCanonicalRoot(Path.GetFullPath(ctx.CanonicalRoot ?? ctx.WorkspaceRoot));

    private static int Report(IReadOnlyList<string> args, WorkspaceContext ctx, TextWriter outw, TextWriter err)
    {
        const string usage = "miller report [--json] [--workspace-id SELECTOR] [--workspace DIR] [--range REV..REV] [--limit N] [--include-tests|--exclude-tests]";
        if (args.Count > 0 && args[0] is "--help" or "-h" or "help")
            return Usage(err, usage);

        CliOptions o = CliOptions.Parse(args.ToArray(), "json", "include-tests", "exclude-tests");
        if (o.Positionals.Count > 0)
            return Usage(err, usage);
        if (!TryResolveReadContext(ctx, o, err, out ctx))
            return 2;
        if (!RequireIndex(ctx, err))
            return 3;

        // Record only a default-params run (range/limit/test-filter untouched); capture identity BEFORE computing.
        bool canonical = !o.Has("range") && !o.Has("limit") && !o.Has("exclude-tests") && !o.Has("include-tests");
        HeavyArmIdentity? identity = canonical ? CaptureHeavyArmIdentity(ctx) : null;

        try
        {
            // Markers ride the region search sidecar; the section reports itself unavailable when the
            // sidecar is disabled or the search.db cannot be opened, instead of failing the report.
            IRegionSearchIndex? regionIndex = null;
            SymbolSearchSidecar sidecar = SymbolSearchSidecar.FromEnvironment();
            if (sidecar.Enabled && sidecar.RegionOptions.Enabled)
            {
                try
                {
                    using var freshness = new FreshnessReader(ctx.ExtractDbPath);
                    long revision = freshness.LatestRevision();
                    regionIndex = FtsRegionSearchIndex.Open(
                        SymbolSearchSidecar.SearchDbPathFor(ctx.ExtractDbPath), revision);
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or SqliteException)
                {
                    regionIndex = null;
                }
            }

            ReportToolResult result = ReportTool.Run(
                ctx.ExtractDbPath,
                ctx.WorkspaceRoot,
                range: o.Value("range", "HEAD~20..HEAD"),
                sectionLimit: o.Int("limit", ReportTool.DefaultSectionLimit),
                json: o.Has("json"),
                includeTests: !o.Has("exclude-tests"),
                historyReader: new ProcessGitHistoryReader(),
                regionIndex: regionIndex);
            WriteOutput(outw, result.Output);
            RecordHeavyArmSnapshot(
                ctx, identity, MetricHistoryHeavyArm.ReportSource, result.SnapshotMetrics, canonical, err);
            return 0;
        }
        catch (Exception ex) when (
            ex is FileNotFoundException or InvalidOperationException or IOException
                or UnauthorizedAccessException or ArgumentException or NotSupportedException
                or SqliteException)
        {
            err.WriteLine("report failed: " + ex.Message);
            return 3;
        }
    }

    private static int Telemetry(IReadOnlyList<string> args, WorkspaceContext ctx, TextWriter outw, TextWriter err)
    {
        if (args.Count == 0)
            return Usage(err, "miller telemetry export [--jsonl] [--workspace-id ID|all]");

        string operation = args[0].ToLowerInvariant();
        CliOptions o = CliOptions.Parse(args.Skip(1).ToArray(), "jsonl");
        if (operation != "export" || o.Positionals.Count > 0)
            return Usage(err, "miller telemetry export [--jsonl] [--workspace-id ID|all]");

        TelemetryExportReader.WriteJsonLines(ctx.TelemetryDbPath, outw, o.Value("workspace-id"));
        return 0;
    }

    // The bulk artifact JSONL feeds (`symbols export`, `references export`, `complexity export`; cli-eros-v1): a fleet
    // orchestrator's alternative to per-query reads or Miller-private SQLite. One subop (`export`), the
    // standard read-context selectors, and an incompatible artifact surfaces through the shared
    // IncompatibleExtractException → exit-3 mapping in Run.
    private static int ArtifactExport(
        IReadOnlyList<string> args,
        WorkspaceContext ctx,
        TextWriter outw,
        TextWriter err,
        string usage,
        Action<string, TextWriter> export)
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

        export(ctx.ExtractDbPath, outw);
        return 0;
    }

    // The deterministic dead-code candidate listing (`references candidates`; dead-code candidates design rev 2):
    // a fact list with NAMED suppressions, not a verdict. The Indexing reader owns all query-time work (the schema
    // gate, the required-table validation, the four inbound-evidence counts, coverage, and the two-phase literal
    // scan); an incompatible/partial artifact surfaces through the shared IncompatibleExtractException → exit-3
    // mapping in Run. `--limit` bounds ONLY the candidate list; examined / suppressions / literal_scan /
    // language_coverage stay full totals.
    private static int ReferencesCandidates(
        IReadOnlyList<string> args, WorkspaceContext ctx, TextWriter outw, TextWriter err)
    {
        const string usage =
            "miller references candidates [--json] [--limit N] [--workspace-id SELECTOR] [--workspace DIR]";
        if (args.Count > 0 && args[0] is "--help" or "-h" or "help")
            return Usage(err, usage);

        CliOptions o = CliOptions.Parse(args, "json");
        if (o.Positionals.Count > 0)
            return Usage(err, usage);
        if (!TryResolveReadContext(ctx, o, err, out ctx))
            return 2;
        if (!RequireIndex(ctx, err))
            return 3;

        int limit = o.Int("limit", DeadCodeCandidatesDefaultLimit);

        // --limit bounds only the displayed list; the recorded counts are full totals. A default-limit run is
        // canonical; capture identity BEFORE the reader computes so a mid-command rebuild is caught at append time.
        bool canonical = !o.Has("limit");
        HeavyArmIdentity? identity = canonical ? CaptureHeavyArmIdentity(ctx) : null;

        DeadCodeCandidateReport report = DeadCodeCandidateReader.Read(ctx.ExtractDbPath, ctx.WorkspaceRoot);
        outw.WriteLine(o.Has("json")
            ? RenderCandidatesJson(report, limit)
            : RenderCandidatesCompact(report, limit));
        RecordHeavyArmSnapshot(
            ctx, identity, MetricHistoryHeavyArm.CandidatesSource, CandidateSnapshotMetrics(report), canonical, err);
        return 0;
    }

    private const int DeadCodeCandidatesDefaultLimit = 50;

    // Sort the surviving candidates by (path, start_line) and take the first `limit` — the ONLY block `--limit`
    // bounds. A stable OrderBy preserves the reader's symbol_id tiebreak within an equal (path, start_line).
    private static List<DeadCodeCandidate> ShownCandidates(DeadCodeResult result, int limit) =>
        result.Candidates
            .OrderBy(c => c.Path, StringComparer.Ordinal)
            .ThenBy(c => c.StartLine)
            .Take(limit < 0 ? 0 : limit)
            .ToList();

    private static string RenderCandidatesCompact(DeadCodeCandidateReport report, int limit)
    {
        DeadCodeResult result = report.Result;
        List<DeadCodeCandidate> shown = ShownCandidates(result, limit);

        var sb = new StringBuilder();
        sb.Append("candidates: ").Append(result.Candidates.Count).Append(" of ").Append(result.Examined)
            .Append(" symbols examined · resolver: ").Append(report.Artifact.ReferenceResolutionStatus)
            .Append(" — candidates are facts to check, not deletions to make.");

        foreach (DeadCodeCandidate c in shown)
        {
            sb.Append('\n')
                .Append(c.Name).Append(' ').Append(c.Kind).Append(' ').Append(c.Language).Append(' ')
                .Append(c.Path).Append(':').Append(c.StartLine).Append(' ')
                .Append(c.Visibility ?? "unknown").Append(" evidence=").Append(c.EvidenceLabel)
                .Append(" [name_matches=").Append(c.NameMatches)
                .Append(" resolved_in=").Append(c.ResolvedInbound)
                .Append(" pending_in=").Append(c.PendingResolvedInbound)
                .Append(" calls_in=").Append(c.CallsInbound).Append(']');
        }

        if (result.Candidates.Count > shown.Count)
            sb.Append('\n').Append("showing top ").Append(shown.Count).Append(" of ")
                .Append(result.Candidates.Count).Append(" by path");

        sb.Append('\n').Append("suppressed:");
        foreach (string id in DeadCodeCandidates.SuppressionRuleIds)
            sb.Append(' ').Append(id).Append('=').Append(result.Suppressions[id]);

        sb.Append('\n').Append("literal_scan: files_scanned=").Append(report.LiteralScan.FilesScanned)
            .Append(" files_skipped_stale=").Append(report.LiteralScan.FilesSkippedStale);

        sb.Append('\n').Append("coverage:");
        bool first = true;
        foreach (LanguageCoverageRow cov in report.LanguageCoverage)
        {
            double pct = DeadCodeCandidates.ResolvedPercent(cov.IdentifierCount, cov.ResolvedCount);
            sb.Append(first ? " " : "; ");
            first = false;
            sb.Append(cov.Language).Append(": ").Append(pct.ToString("0.0", CultureInfo.InvariantCulture)).Append('%')
                .Append(pct >= 10.0 ? " resolved" : " — name-evidence only");
        }

        return sb.ToString();
    }

    private static string RenderCandidatesJson(DeadCodeCandidateReport report, int limit)
    {
        DeadCodeResult result = report.Result;
        List<DeadCodeCandidate> shown = ShownCandidates(result, limit);

        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer,
            new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteNumber("schema_version", DeadCodeCandidatesSchemaVersion);

            w.WritePropertyName("candidates");
            w.WriteStartArray();
            foreach (DeadCodeCandidate c in shown)
            {
                w.WriteStartObject();
                w.WriteString("symbol_id", c.SymbolId);
                w.WriteString("name", c.Name);
                w.WriteString("kind", c.Kind);
                w.WriteString("language", c.Language);
                w.WriteString("path", c.Path);
                w.WriteNumber("start_line", c.StartLine);
                if (c.Visibility is null) w.WriteNull("visibility"); else w.WriteString("visibility", c.Visibility);
                w.WriteString("evidence_label", c.EvidenceLabel);
                w.WritePropertyName("evidence");
                w.WriteStartObject();
                w.WriteNumber("name_matches", c.NameMatches);
                w.WriteNumber("resolved_inbound", c.ResolvedInbound);
                w.WriteNumber("pending_resolved_inbound", c.PendingResolvedInbound);
                w.WriteNumber("calls_inbound", c.CallsInbound);
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WritePropertyName("suppressions");
            w.WriteStartObject();
            foreach (string id in DeadCodeCandidates.SuppressionRuleIds)
                w.WriteNumber(id, result.Suppressions[id]);
            w.WriteEndObject();

            w.WritePropertyName("literal_scan");
            w.WriteStartObject();
            w.WriteNumber("files_scanned", report.LiteralScan.FilesScanned);
            w.WriteNumber("files_skipped_stale", report.LiteralScan.FilesSkippedStale);
            w.WriteEndObject();

            w.WritePropertyName("language_coverage");
            w.WriteStartArray();
            foreach (LanguageCoverageRow cov in report.LanguageCoverage)
            {
                w.WriteStartObject();
                w.WriteString("language", cov.Language);
                w.WriteNumber("identifiers", cov.IdentifierCount);
                w.WriteNumber("resolved_pct", DeadCodeCandidates.ResolvedPercent(cov.IdentifierCount, cov.ResolvedCount));
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteNumber("examined", result.Examined);

            w.WritePropertyName("artifact");
            w.WriteStartObject();
            if (report.Artifact.ArtifactId is null)
                w.WriteNull("artifact_id");
            else
                w.WriteString("artifact_id", report.Artifact.ArtifactId);
            if (report.Artifact.Revision is null)
                w.WriteNull("revision");
            else
                w.WriteNumber("revision", report.Artifact.Revision.Value);
            w.WriteString("reference_resolution_status", report.Artifact.ReferenceResolutionStatus);
            if (report.Artifact.ReferenceResolutionVersion is null)
                w.WriteNull("reference_resolution_version");
            else
                w.WriteString("reference_resolution_version", report.Artifact.ReferenceResolutionVersion);
            w.WriteEndObject();

            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    // The references-candidates-v1 JSON envelope version (docs/contracts/references-candidates-v1.md).
    private const int DeadCodeCandidatesSchemaVersion = 1;

    // ---------- heavy-arm metric-history recording (report / metrics churn|risk / references candidates) ----------
    //
    // The single CLI-side hook the three heavy commands share. Each command captures the artifact identity BEFORE it
    // computes (below), renders normally, then hands the already-composed metric points here. Recording is
    // best-effort telemetry: a failed history write warns on stderr and NEVER changes the command's output or exit
    // code. Only CANONICAL (default-params) runs record — a non-default run renders as usual and skips, because a
    // trend line that mixes ranges/limits is incomparable. Design: docs/plans/2026-07-07-metric-history-design.md.

    /// <summary>The artifact identity a heavy command captured before computing, for the append-time re-check.</summary>
    internal readonly record struct HeavyArmIdentity(
        string WorkspaceId, string ArtifactId, long Revision, string ExtractorVersion);

    /// <summary>
    /// Capture <c>(workspace_id, artifact_id, revision, extractor_version)</c> from the workspace's <c>symbols.db</c>
    /// BEFORE the command computes, so a full-rebuild promotion mid-command is caught by the append-time re-check.
    /// Returns <c>null</c> — recording is skipped silently — when there is no stable identity to attach history to
    /// (no <c>.miller</c> index, no artifact_id, no revision yet) or the DB cannot be read. Never throws.
    /// </summary>
    internal static HeavyArmIdentity? CaptureHeavyArmIdentity(WorkspaceContext ctx)
    {
        try
        {
            string extractDbPath = ctx.ExtractDbPath;
            if (!File.Exists(extractDbPath))
                return null; // unregistered / no .miller ⟹ nothing to attach history to.

            using var freshness = new FreshnessReader(extractDbPath);
            string? artifactId = freshness.ArtifactId();
            long revision = freshness.LatestRevision();
            if (string.IsNullOrWhiteSpace(artifactId) || revision <= 0)
                return null; // no stable artifact identity / no revision ⟹ cannot key a snapshot.

            string workspaceId = ctx.WorkspaceId
                ?? WorkspaceId.FromCanonicalRoot(Path.GetFullPath(ctx.CanonicalRoot ?? ctx.WorkspaceRoot));
            if (string.IsNullOrWhiteSpace(workspaceId))
                return null;

            string extractorVersion = ExtractBinaryVersionReader.TryRead(extractDbPath) ?? string.Empty;
            return new HeavyArmIdentity(workspaceId, artifactId!, revision, extractorVersion);
        }
        catch (Exception ex) when (
            ex is IOException or InvalidOperationException or UnauthorizedAccessException
                or ArgumentException or SqliteException)
        {
            return null; // identity unreadable ⟹ skip recording, never disturb the command.
        }
    }

    /// <summary>
    /// Append the heavy-arm snapshot the command just computed. No-op when the run was non-canonical, produced no
    /// metrics, or had no capturable identity. The <c>RecordRun</c> re-check re-reads the live identity inside the
    /// append transaction and skips on a mismatch (artifact replaced mid-command). Any failure is swallowed to a
    /// stderr warning — the command's output and exit code are already committed.
    /// </summary>
    internal static MetricHistoryWriteResult? RecordHeavyArmSnapshot(
        WorkspaceContext ctx,
        HeavyArmIdentity? captured,
        string source,
        IReadOnlyList<MetricHistoryPoint> metrics,
        bool canonical,
        TextWriter warn,
        DateTime? recordedAtUtc = null)
    {
        if (!canonical || captured is not { } id || metrics.Count == 0)
            return null;

        try
        {
            var snapshot = new MetricHistorySnapshot(
                WorkspaceId: id.WorkspaceId,
                ArtifactId: id.ArtifactId,
                Revision: id.Revision,
                ExtractorVersion: id.ExtractorVersion,
                MillerVersion: MillerVersion.Current,
                Source: source,
                Metrics: metrics);

            string historyDbPath = MetricSnapshotAggregates.HistoryDbPathFor(ctx.ExtractDbPath);
            return MetricHistoryStore.RecordRun(
                historyDbPath, snapshot, () => RecheckHeavyArmIdentity(ctx.ExtractDbPath), recordedAtUtc);
        }
        catch (Exception ex)
        {
            warn.WriteLine($"metric history: {source} snapshot not recorded ({ex.Message}).");
            return null;
        }
    }

    // The append-time identity re-read: the live (artifact_id, revision) the store compares against the captured
    // snapshot identity. On any read failure it returns a guaranteed-mismatch sentinel so the store skips recording
    // rather than stamping the captured identity onto numbers read from a since-replaced artifact.
    private static (string ArtifactId, long Revision) RecheckHeavyArmIdentity(string extractDbPath)
    {
        try
        {
            using var freshness = new FreshnessReader(extractDbPath);
            return (freshness.ArtifactId() ?? string.Empty, freshness.LatestRevision());
        }
        catch (Exception ex) when (
            ex is IOException or InvalidOperationException or UnauthorizedAccessException
                or ArgumentException or SqliteException)
        {
            return (string.Empty, -1);
        }
    }

    // The heavy-arm `source='candidates'` snapshot: the full dead-code candidate count and suppressed total (both
    // are full totals — `--limit` bounds only the displayed list, never these), with the per-rule suppressed
    // breakdown in detail_json (the count-level surfacing approved 2026-07-07; per-symbol detail stays CLI-only).
    private static IReadOnlyList<MetricHistoryPoint> CandidateSnapshotMetrics(DeadCodeCandidateReport report)
    {
        int suppressedTotal = 0;
        foreach (int count in report.Result.Suppressions.Values)
            suppressedTotal += count;

        return
        [
            new MetricHistoryPoint(MetricHistoryHeavyArm.DeadCodeCandidateCount, report.Result.Candidates.Count, null),
            new MetricHistoryPoint(
                MetricHistoryHeavyArm.DeadCodeSuppressedTotal,
                suppressedTotal,
                SuppressionDetailJson(report.Result.Suppressions)),
        ];
    }

    private static string SuppressionDetailJson(IReadOnlyDictionary<string, int> suppressions)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(
            buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            foreach (string id in DeadCodeCandidates.SuppressionRuleIds)
                w.WriteNumber(id, suppressions.TryGetValue(id, out int count) ? count : 0);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
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
            return Usage(err, "miller inspect <file-or-symbol> [--workspace-id SELECTOR] [--workspace DIR] [--depth summary|overview|full] [--kind K] [--scope FILE] [--limit N] [--json]");
        if (!TryResolveReadContext(ctx, o, err, out ctx))
            return 2;

        string depth = o.Value("depth", "summary")!;
        string output;
        if (string.Equals(depth, "full", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(depth, "overview", StringComparison.OrdinalIgnoreCase))
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
                index, graph, resolver, query: o.Query, tokenBudget: o.Int("token-budget", 2000), maxHops: o.Int("max-hops", 1),
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
                index, graph, resolver, query: o.Query, tokenBudget: o.Int("token-budget", 2000), maxHops: o.Int("max-hops", 1),
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

    private static int Impact(
        IReadOnlyList<string> args,
        WorkspaceContext ctx,
        TextWriter outw,
        TextWriter err,
        IGitDiffReader gitDiffReader)
    {
        CliOptions o = CliOptions.Parse(args, "json", "git", "staged");

        // The index-revision delta channel (CT revision-delta contract R0) is its own mode: it never overloads
        // --base (a git ref), and it emits the typed delta envelope instead of a plain impact result.
        if (o.Has("from-index-revision"))
            return ImpactIndexRevisionDelta(o, ctx, outw, err);

        string? target = string.IsNullOrWhiteSpace(o.Query) ? null : o.Query;
        string[]? changedPaths = ImpactChangedPaths(o);
        string? diff = o.Value("diff");
        bool gitDiff = o.Has("git") || o.Has("staged") || o.Has("base");
        if (o.Has("base") && string.IsNullOrWhiteSpace(o.Value("base")))
            return Usage(err, "miller impact --git --base REF [--staged] [--workspace-id SELECTOR] [--workspace DIR] [--max-depth N] [--limit N] [--json]");

        int provided =
            (target is null ? 0 : 1) +
            (changedPaths is null ? 0 : 1) +
            (string.IsNullOrWhiteSpace(diff) ? 0 : 1) +
            (gitDiff ? 1 : 0);
        if (provided != 1)
            return Usage(err, ImpactUsage);
        if (!TryResolveReadContext(ctx, o, err, out ctx))
            return 2;

        if (gitDiff)
        {
            GitDiffResult result = gitDiffReader.Read(new GitDiffRequest(ctx.WorkspaceRoot, o.Value("base"), o.Has("staged")));
            if (!result.Success)
            {
                err.WriteLine($"git diff failed in {ctx.WorkspaceRoot}: {result.Error ?? "unknown error"}");
                return 3;
            }

            if (string.IsNullOrWhiteSpace(result.Diff))
            {
                outw.WriteLine(o.Has("json")
                    ? ServerJson.Note("No impact — git diff is empty.")
                    : "No impact — git diff is empty.");
                return 0;
            }

            diff = result.Diff;
        }

        if (!TryLoadSymbolSearchIndex(ctx, err, out ISymbolLookupIndex index))
            return 3;

        using var graph = new SqliteSymbolGraphIndex(ctx.ExtractDbPath);
        var resolver = new SmartTargetResolver(index);
        string output = ImpactTool.Run(
            index, graph, resolver, target, changedPaths, diff,
            maxDepth: o.Int("max-depth", 2), limit: o.Int("limit", 100), json: o.Has("json"), out _, out _);
        outw.WriteLine(output);
        return 0;
    }

    private static string[]? ImpactChangedPaths(CliOptions options)
    {
        string? raw = options.Value("changed-paths") ?? options.Value("changed-path");
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        string[] paths = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return paths.Length == 0 ? null : paths;
    }

    private const string ImpactUsage =
        "miller impact <symbol>|--changed-paths PATH[,PATH...]|--diff DIFF|--git [--base REF] [--staged]|" +
        "--from-index-revision N [--from-artifact-id ID] " +
        "[--workspace-id SELECTOR] [--workspace DIR] [--max-depth N] [--limit N] [--json]";

    private const string ImpactDeltaUsage =
        "miller impact --from-index-revision N [--from-artifact-id ID] [--workspace-id SELECTOR] [--workspace DIR] " +
        "[--max-depth N] [--limit N] [--json]";

    // The index-revision delta mode (CT revision-delta contract R0–R3): emit the typed delta envelope
    // (workspace_id/delta_status/from_revision/to_revision/changed_paths + impacted/tests) for the span between
    // the requested base revision and the current index revision, sourced from julie-extract's change journal.
    private static int ImpactIndexRevisionDelta(CliOptions o, WorkspaceContext ctx, TextWriter outw, TextWriter err)
    {
        string? raw = o.Value("from-index-revision");
        if (string.IsNullOrWhiteSpace(raw)
            || !long.TryParse(raw, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out long fromRevision)
            || fromRevision < 0)
            return Usage(err, ImpactDeltaUsage);

        // This channel is exclusive: it must not be combined with a symbol/changed-paths/diff/git base.
        if (!string.IsNullOrWhiteSpace(o.Query) || o.Has("changed-paths") || o.Has("changed-path")
            || o.Has("diff") || o.Has("git") || o.Has("staged") || o.Has("base"))
            return Usage(err, ImpactDeltaUsage);

        if (!TryResolveReadContext(ctx, o, err, out ctx))
            return 2;

        bool json = o.Has("json");
        // Echo the caller's selector verbatim so the envelope's workspace_id always matches what Eros asked for;
        // fall back to the resolved workspace identity when no selector was passed (CLI against the current repo).
        string workspaceId = o.Value("workspace-id")
            ?? ctx.WorkspaceId ?? ctx.CanonicalRoot ?? ctx.WorkspaceRoot;

        string? rawArtifactId = o.Value("from-artifact-id");
        string? fromArtifactId = string.IsNullOrWhiteSpace(rawArtifactId) ? null : rawArtifactId;
        RevisionDeltaResult delta = RevisionDeltaReader.Read(ctx.ExtractDbPath, fromRevision, fromArtifactId);
        bool complete = delta.Status == RevisionDeltaStatus.Complete;

        // R2: exclude ignored/tooling paths before they can reach the envelope (defense-in-depth over the journal).
        IReadOnlyList<string> changedPaths = complete
            ? ImpactTool.FilterWatchedDeltaPaths(ctx.WorkspaceRoot, delta.ChangedPaths)
            : Array.Empty<string>();

        ISymbolLookupIndex? index = null;
        SqliteSymbolGraphIndex? graph = null;
        try
        {
            // Load the symbol index/graph only when there is a truthful, non-empty delta to analyse — the common
            // "no source change since last poll" case emits an empty complete envelope without touching the index.
            if (complete && changedPaths.Count > 0 && TryLoadSymbolSearchIndex(ctx, err, out ISymbolLookupIndex loaded))
            {
                index = loaded;
                graph = new SqliteSymbolGraphIndex(ctx.ExtractDbPath);
            }

            string output = ImpactTool.RenderIndexRevisionDelta(
                workspaceId, complete, fromRevision, delta.ToRevision, changedPaths,
                index, graph, o.Int("max-depth", 2), o.Int("limit", 100), json,
                delta.ArtifactId, fromArtifactId, delta.Reason);
            outw.WriteLine(output);
            return 0;
        }
        finally
        {
            graph?.Dispose();
        }
    }

    private static int Trace(IReadOnlyList<string> args, WorkspaceContext ctx, TextWriter outw, TextWriter err)
    {
        CliOptions o = CliOptions.Parse(args, "full", "json", "no-definition");
        if (string.IsNullOrWhiteSpace(o.Query))
            return Usage(err, "miller trace <symbol> [--workspace-id SELECTOR] [--workspace DIR] [--scope FILE] [--mode auto|path|refs|bridge] [--to SYMBOL] [--reference-kind KIND] [--no-definition] [--depth N] [--limit N] [--full] [--json]");
        if (!TryResolveReadContext(ctx, o, err, out ctx))
            return 2;

        string mode = o.Value("mode", "auto")!;
        bool json = o.Has("json");
        string? referenceKind = o.Value("reference-kind", o.Value("kind"));
        bool includeDefinition = BoolOption(o, "include-definition", fallback: true) && !o.Has("no-definition");
        if (string.Equals(mode, "bridge", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryLoadIndex(ctx, err, out MillerRepositoryIndex fullIndex))
                return 3;

            var fullResolver = new SmartTargetResolver(fullIndex);
            string bridgeOutput = TraceTool.Run(
                fullIndex, fullResolver, target: o.Query, scope: o.Value("scope"), mode: mode, to: o.Value("to"),
                depth: o.Int("depth", 3), limit: o.Int("limit", 20), fullFormat: o.Has("full"), json: json,
                referenceKind, includeDefinition,
                symbol => ExtractReader.ReadReferences(ctx.ExtractDbPath, symbol.Name),
                out _, out _);
            outw.WriteLine(bridgeOutput);
            return 0;
        }

        if (!TryLoadSymbolSearchIndex(ctx, err, out ISymbolLookupIndex index))
            return 3;

        using var graph = new SqliteSymbolGraphIndex(ctx.ExtractDbPath);
        var resolver = new SmartTargetResolver(index);
        string output = TraceTool.RunGraph(
            index, graph, resolver, target: o.Query, scope: o.Value("scope"), mode: mode, to: o.Value("to"),
            depth: o.Int("depth", 3), limit: o.Int("limit", 20), fullFormat: o.Has("full"), json: json,
            referenceKind, includeDefinition,
            symbol => ExtractReader.ReadReferences(ctx.ExtractDbPath, symbol.Name),
            out _, out _);
        outw.WriteLine(output);
        return 0;
    }

    // ---------- workspace verb ----------

    private static int Workspace(IReadOnlyList<string> args, WorkspaceContext ctx, TextWriter outw, TextWriter err)
    {
        CliOptions o = CliOptions.Parse(args, "json", "full", "markdown");
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
                return WorkspaceList(
                    ctx, json, outw,
                    filter: o.Value("filter"),
                    limit: o.Has("limit") ? o.Int("limit", WorkspaceRender.DefaultListLimit) : (int?)null);
            case "status":
                return WorkspaceStatus(ctx, id, path, json, outw, err);
            case "health":
                return WorkspaceHealth(ctx, id, path, json, outw, err);
            case "onboarding":
                return WorkspaceOnboarding(ctx, id, path, json, outw, err);
            case "leader":
                return WorkspaceLeader(ctx, id, path, handoff: o.Has("handoff"), wait: o.Has("wait"), json, outw, err);
            case "refresh":
                return WorkspaceRefresh(ctx, id, path, force: false, json, outw, err);
            case "full":
                return WorkspaceRefresh(ctx, id, path, force: true, json, outw, err);
            case "open":
                return WorkspaceOpen(ctx, path, full: o.Has("full"), json, outw, err);
            case "remove":
                return WorkspaceRemove(ctx, id, path, json, outw, err);
            default:
                err.WriteLine($"unknown workspace operation '{operation}'. Use status|health|onboarding|leader|list|refresh|full|open|remove.");
                return 2;
        }
    }

    private static int WorkspaceList(
        WorkspaceContext ctx, bool json, TextWriter outw, string? filter = null, int? limit = null)
    {
        using WorkspaceRegistry registry = WorkspaceRegistry.Open(ctx.RegistryDbPath);
        IReadOnlyList<WorkspaceRegistryRow> rows = registry.List();
        WorkspaceRegistryRow? currentRow = FindCurrentWorkspaceRow(registry, ctx);
        IReadOnlyList<WorkspaceListEntry> entries = WorkspaceFactsAssembler.ToListEntries(
            rows,
            row => currentRow is not null
                ? string.Equals(row.WorkspaceId, currentRow.WorkspaceId, StringComparison.Ordinal)
                : WorkspaceSafety.IsLiveWorkspace(row.CanonicalRoot, ctx.WorkspaceRoot));
        outw.WriteLine(WorkspaceRender.List(entries, json, filter, limit));
        return 0;
    }

    private static int WorkspaceStatus(
        WorkspaceContext ctx, string? id, string? path, bool json, TextWriter outw, TextWriter err)
    {
        using WorkspaceRegistry registry = WorkspaceRegistry.Open(ctx.RegistryDbPath);
        SymbolSearchSidecar sidecar = SymbolSearchSidecar.FromEnvironment();
        var contentSidecar = new ContentCorpusSidecar();

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
            outw.WriteLine(WorkspaceRender.Status(
                WorkspaceFactsAssembler.FromRegisteredRow(
                    registry,
                    row,
                    WorkspaceRegisteredFactsProfile.CliStatus,
                    sidecar,
                    contentSidecar),
                TelemetrySummary.Empty,
                json));
            return 0;
        }

        // Default: the current workspace. Enrich from its registry row when present, else read the local db.
        WorkspaceRegistryRow? currentRow = FindCurrentWorkspaceRow(registry, ctx);
        if (currentRow is not null)
        {
            outw.WriteLine(WorkspaceRender.Status(
                WorkspaceFactsAssembler.FromRegisteredRow(
                    registry,
                    currentRow,
                    WorkspaceRegisteredFactsProfile.CliStatus,
                    sidecar,
                    contentSidecar),
                TelemetrySummary.Empty,
                json));
            return 0;
        }

        if (!RequireIndex(ctx, err))
            return 3;
        WorkspaceIndexFacts indexFacts = WorkspaceIndexFactsReader.Read(ctx.ExtractDbPath);
        WorkspaceFacts facts = WorkspaceFactsAssembler.FromUnregisteredLocal(
            ctx,
            indexFacts,
            sidecar,
            contentSidecar);
        outw.WriteLine(WorkspaceRender.Status(facts, TelemetrySummary.Empty, json));
        return 0;
    }

    private static int WorkspaceHealth(
        WorkspaceContext ctx, string? id, string? path, bool json, TextWriter outw, TextWriter err)
    {
        using WorkspaceRegistry registry = WorkspaceRegistry.Open(ctx.RegistryDbPath);
        SymbolSearchSidecar sidecar = SymbolSearchSidecar.FromEnvironment();
        var contentSidecar = new ContentCorpusSidecar();

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

            WorkspaceFacts facts = WorkspaceFactsAssembler.FromRegisteredRow(
                registry,
                row,
                WorkspaceRegisteredFactsProfile.CliHealth,
                sidecar,
                contentSidecar);
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
            WorkspaceFacts facts = WorkspaceFactsAssembler.FromRegisteredRow(
                registry,
                currentRow,
                WorkspaceRegisteredFactsProfile.CliHealth,
                sidecar,
                contentSidecar);
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
        WorkspaceFacts localFacts = WorkspaceFactsAssembler.FromUnregisteredLocal(
            ctx,
            indexFacts,
            sidecar,
            contentSidecar);
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

    private static int WorkspaceLeader(
        WorkspaceContext ctx,
        string? id,
        string? path,
        bool handoff,
        bool wait,
        bool json,
        TextWriter outw,
        TextWriter err)
    {
        using WorkspaceRegistry registry = WorkspaceRegistry.Open(ctx.RegistryDbPath);
        SymbolSearchSidecar sidecar = SymbolSearchSidecar.FromEnvironment();
        var contentSidecar = new ContentCorpusSidecar();

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

            WorkspaceFacts facts = WorkspaceFactsAssembler.FromRegisteredRow(
                registry,
                row,
                WorkspaceRegisteredFactsProfile.CliStatus,
                sidecar,
                contentSidecar);
            outw.WriteLine(RenderWorkspaceLeader(facts, row.WorkspaceId, handoff, wait, json));
            return 0;
        }

        WorkspaceRegistryRow? currentRow = FindCurrentWorkspaceRow(registry, ctx);
        if (currentRow is not null)
        {
            WorkspaceFacts facts = WorkspaceFactsAssembler.FromRegisteredRow(
                registry,
                currentRow,
                WorkspaceRegisteredFactsProfile.CliStatus,
                sidecar,
                contentSidecar);
            outw.WriteLine(RenderWorkspaceLeader(facts, currentRow.WorkspaceId, handoff, wait, json));
            return 0;
        }

        if (!RequireIndex(ctx, err))
            return 3;

        WorkspaceIndexFacts indexFacts = WorkspaceIndexFactsReader.Read(ctx.ExtractDbPath);
        WorkspaceFacts localFacts = WorkspaceFactsAssembler.FromUnregisteredLocal(
            ctx,
            indexFacts,
            sidecar,
            contentSidecar);
        string workspaceId = localFacts.WorkspaceId ?? WorkspaceId.FromCanonicalRoot(Path.GetFullPath(localFacts.Root));
        outw.WriteLine(RenderWorkspaceLeader(localFacts, workspaceId, handoff, wait, json));
        return 0;
    }

    private static int WorkspaceOnboarding(
        WorkspaceContext ctx, string? id, string? path, bool json, TextWriter outw, TextWriter err)
    {
        using WorkspaceRegistry registry = WorkspaceRegistry.Open(ctx.RegistryDbPath);
        SymbolSearchSidecar sidecar = SymbolSearchSidecar.FromEnvironment();
        var contentSidecar = new ContentCorpusSidecar();

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

            WorkspaceFacts facts = WorkspaceFactsAssembler.FromRegisteredRow(
                registry,
                row,
                WorkspaceRegisteredFactsProfile.CliHealth,
                sidecar,
                contentSidecar);
            outw.WriteLine(WorkspaceRender.Onboarding(
                WorkspaceOnboardingAssembler.Create(facts, ctx.TelemetryDbPath, row.WorkspaceId, row.IndexDbPath),
                json));
            return 0;
        }

        WorkspaceRegistryRow? currentRow = FindCurrentWorkspaceRow(registry, ctx);
        if (currentRow is not null)
        {
            WorkspaceFacts facts = WorkspaceFactsAssembler.FromRegisteredRow(
                registry,
                currentRow,
                WorkspaceRegisteredFactsProfile.CliHealth,
                sidecar,
                contentSidecar);
            outw.WriteLine(WorkspaceRender.Onboarding(
                WorkspaceOnboardingAssembler.Create(
                    facts,
                    ctx.TelemetryDbPath,
                    currentRow.WorkspaceId,
                    currentRow.IndexDbPath),
                json));
            return 0;
        }

        if (!RequireIndex(ctx, err))
            return 3;

        WorkspaceIndexFacts indexFacts = WorkspaceIndexFactsReader.Read(ctx.ExtractDbPath);
        WorkspaceFacts localFacts = WorkspaceFactsAssembler.FromUnregisteredLocal(
            ctx,
            indexFacts,
            sidecar,
            contentSidecar);
        outw.WriteLine(WorkspaceRender.Onboarding(
            WorkspaceOnboardingAssembler.Create(localFacts, ctx.TelemetryDbPath, ctx.WorkspaceId, ctx.ExtractDbPath),
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

    private static string RenderWorkspaceLeader(
        WorkspaceFacts facts,
        string workspaceId,
        bool handoff,
        bool wait,
        bool json)
    {
        string millerDir = Path.GetDirectoryName(facts.DbPath)!;
        LeaderHealthFacts leader = CliLeaderFacts(facts.DbPath);
        LeaderHandoffRequestReceipt? receipt = null;
        bool observed = false;
        string? handoffNote = null;
        if (handoff)
        {
            receipt = LeaderScanRequestQueue.RequestLeaderHandoff(millerDir, workspaceId, Environment.ProcessId);
            if (wait)
            {
                observed = WaitForCliHandoffObservation(receipt, millerDir, leader.Identity);
                handoffNote = observed
                    ? "leader observed the handoff request"
                    : "handoff request queued but not observed before timeout";
            }
            else
            {
                handoffNote = "handoff request queued";
            }
        }

        return WorkspaceRender.Leader(
            new WorkspaceLeaderResult(
                facts,
                leader,
                CliLeaderRecommendation(facts, leader, handoff),
                HandoffRequested: handoff,
                HandoffWaited: wait && handoff,
                HandoffObserved: observed,
                HandoffRequestId: receipt?.RequestId,
                HandoffNote: handoffNote),
            json);
    }

    private static string CliLeaderRecommendation(WorkspaceFacts facts, LeaderHealthFacts leader, bool handoffRequested)
    {
        if (handoffRequested)
            return "Handoff requested through the local queue; the current leader must drain it before stepping down.";
        if (facts.IsLeader)
            return "No handoff requested; this process is the current indexer leader.";
        if (leader.Identity is null)
            return "No handoff requested; no leader identity is recorded. An older leader may still hold the lock.";
        if (leader.Alive == false)
            return "No handoff requested; recorded leader is not running. Normal lock retry should recover.";
        return "No handoff requested; use --handoff to ask the live leader to step down gracefully.";
    }

    private static bool WaitForCliHandoffObservation(
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

        var action = WorkspaceRefreshAction(result, force, sidecar, registry);
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
        SymbolSearchSidecar sidecar,
        WorkspaceRegistry? registry = null)
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
            DurationMs: (long?)result.TotalDuration?.TotalMilliseconds,
            ArtifactId: ArtifactIdForAction(result, registry));
    }

    private static string? ArtifactIdForAction(WorkspaceRefreshResult result, WorkspaceRegistry? registry)
    {
        if (!string.IsNullOrWhiteSpace(result.ArtifactId))
            return result.ArtifactId;
        if (registry is null)
            return null;

        WorkspaceRegistryRow? row = registry.Get(result.WorkspaceId);
        if (row is null)
            return null;

        return WorkspaceFactsAssembler.FromRegisteredRow(
            registry,
            row,
            WorkspaceRegisteredFactsProfile.CliStatus,
            SymbolSearchSidecar.Disabled,
            new ContentCorpusSidecar()).ArtifactId;
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
            DurationMs: (long?)result.TotalDuration?.TotalMilliseconds,
            ArtifactId: ArtifactIdForAction(result, registry));
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
            WorkspaceRegistryRow? stale =
                WorkspaceRegistryRootMatcher.FindByPossiblyMissingPath(registry.List(), fullPath);
            if (stale is not null)
            {
                registry.Remove(stale.WorkspaceId);
                outw.WriteLine(WorkspaceRender.Remove(
                    WorkspaceRemoveResult.Removed(
                        goneMillerDir,
                        stale.WorkspaceId,
                        stale.CanonicalRoot,
                        indexDirDeleted: false), json));
                return RemoveExitCode(WorkspaceRemoveResult.Outcome.Removed);
            }
            outw.WriteLine(WorkspaceRender.Remove(WorkspaceRemoveResult.NotFound(goneMillerDir), json));
            return RemoveExitCode(WorkspaceRemoveResult.Outcome.NotFound);
        }

        // Existing dir: canonicalize and match a registry row (ordinal canonical root, like the server's
        // FindByCanonicalRoot), falling back to a local .miller cleanup when no row is registered.
        string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(fullPath);
        WorkspaceRegistryRow? match = WorkspaceRegistryRootMatcher.FindByRoot(registry.List(), canonicalRoot);
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
            {
                registry.Remove(workspaceId);
                outw.WriteLine(WorkspaceRender.Remove(
                    WorkspaceRemoveResult.Removed(
                        millerDir,
                        workspaceId,
                        root,
                        indexDirDeleted: false), json));
                return RemoveExitCode(WorkspaceRemoveResult.Outcome.Removed);
            }
            outw.WriteLine(WorkspaceRender.Remove(WorkspaceRemoveResult.NotFound(millerDir, workspaceId, root), json));
            return RemoveExitCode(WorkspaceRemoveResult.Outcome.NotFound);
        }

        // Delete the index data while HOLDING all three workspace-local write leases (indexer → content →
        // history), so no Miller process can start writing this index — nor a CLI content import / history append,
        // which hold content.lock / history.lock WITHOUT the indexer lock — mid-delete. Any lease unavailable ⇒
        // refuse, delete nothing. Only the held lock files are skipped (an open FileShare.None handle cannot be
        // deleted on Windows); after release, the leftover lock files + empty dir are removed best-effort — a
        // writer that sneaks in after release finds an already-empty index and does a clean rebuild.
        using (WorkspaceWriteLeases? leases =
            WorkspaceWriteLeases.TryAcquireForRemove(millerDir, SingleWriterLock.TryAcquire))
        {
            if (leases is null)
            {
                outw.WriteLine(WorkspaceRender.Remove(WorkspaceRemoveResult.RefusedInUse(millerDir, workspaceId, root), json));
                return RemoveExitCode(WorkspaceRemoveResult.Outcome.RefusedInUse);
            }
            SingleWriterLock.DeleteContentsExceptLock(millerDir, WorkspaceWriteLeases.SidecarLockFileNames);
        }

        SingleWriterLock.TryDeleteEmptiedDir(millerDir);
        if (workspaceId is not null)
            registry.Remove(workspaceId);
        outw.WriteLine(WorkspaceRender.Remove(WorkspaceRemoveResult.Removed(millerDir, workspaceId, root), json));
        return RemoveExitCode(WorkspaceRemoveResult.Outcome.Removed);
    }

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

    private static bool BoolOption(CliOptions options, string name, bool fallback)
    {
        if (!options.Has(name))
            return fallback;

        string? value = options.Value(name);
        if (string.IsNullOrWhiteSpace(value))
            return true;

        if (bool.TryParse(value, out bool parsed))
            return parsed;

        return value.Trim() switch
        {
            "1" => true,
            "0" => false,
            var text when string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase) => true,
            var text when string.Equals(text, "no", StringComparison.OrdinalIgnoreCase) => false,
            _ => fallback,
        };
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
                             [--workspace-id SELECTOR] [--workspace DIR] [--mode auto|text|symbol|file|markers|content|source|external|web|all-text] [--regions KINDS] [--file-pattern GLOB] [--language LANG] [--limit N] [--json] [--include-tests|--exclude-tests]
          todos              CLI alias for search --mode markers over TODO/FIXME/HACK/XXX comment markers.
                             [--markers TODO,FIXME,HACK,XXX] [--workspace-id SELECTOR] [--workspace DIR] [--file-pattern GLOB] [--language LANG] [--limit N] [--json] [--exclude-tests]
          content <op>       Import/search/read/list/remove/export external and web text in content.db.
                             import <path> [--max-bytes N] [--display-path NAME] [--json]
                             add-markdown <path> --url URL [--display-path NAME] [--json]
                             search <query> [--kind KIND] [--workspace-id all|SELECTOR] [--limit N] [--json]
                             read --source-id ID --line N [--workspace-id SELECTOR] [--context-lines N] [--json]
                             list [--kind KIND] [--json]
                             remove --source-id ID [--json]
                             export [--kind KIND] [--content-workspace-id ID]   # JSONL
          patterns <op>      List, summarize, or search extractor-recognized code-shape facts.
                             op = list | summary | search
                             [--workspace-id SELECTOR] [--workspace DIR] [--pattern ID] [--query TEXT] [--language LANG] [--path GLOB] [--where key=value] [--limit N] [--json]
          metrics <op>       Report deterministic local metrics, or a recorded metric-history trend.
                             op = churn | clones | complexity | risk | history
                             [--workspace-id SELECTOR] [--workspace DIR] [--limit N] [--json] [--range REV..REV] [--include-commits] [--min-count N] [--max-symbols-per-group N] [--min-severity low|moderate|high] [--include-tests|--exclude-tests]
                             history [--metric a,b,…] [--limit N] [--json] [--workspace-id SELECTOR] [--workspace DIR]
          report             One composed repo-quality report: index counts, extraction health, markers, complexity, clones, churn, risk.
                             [--json] [--workspace-id SELECTOR] [--workspace DIR] [--range REV..REV] [--limit N] [--include-tests|--exclude-tests]
          telemetry <op>     Export machine-global Miller telemetry.
                             export [--jsonl] [--workspace-id ID|all]
          symbols <op>       Bulk-export every symbol row for fleet rollups.   # JSONL
                             export [--jsonl] [--workspace-id SELECTOR] [--workspace DIR]
          references <op>    Bulk-export identifier/reference usage facts, or list dead-code candidates.
                             export     [--jsonl] [--workspace-id SELECTOR] [--workspace DIR]   # JSONL fact feed
                             candidates [--json] [--limit N] [--workspace-id SELECTOR] [--workspace DIR]
          complexity <op>    Bulk-export per-symbol/per-file complexity metrics.   # JSONL
                             export [--jsonl] [--workspace-id SELECTOR] [--workspace DIR]
          refresh            Refresh a registered workspace index and return after convergence attempt.
                             [--json] [--wait] [--workspace-id SELECTOR|--workspace DIR] [--full]
          inspect <target>   List a file's symbols, or show a symbol's definition.
                             [--workspace-id SELECTOR] [--workspace DIR] [--depth summary|overview|full] [--kind K] [--scope FILE] [--limit N] [--json]
          context <query>    Token-budgeted bundle of the most relevant code for a task.
                             [--workspace-id SELECTOR] [--workspace DIR] [--token-budget N] [--max-hops 0-2] [--reference-mode off|usage] [--reference-depth 0-1] [--exclude-tests] [--json]
          impact <input>     Downstream symbols + tests a change would affect.
                             <symbol> | --changed-paths PATH[,PATH...] | --diff DIFF | --git [--base REF] [--staged]
                             [--workspace-id SELECTOR] [--workspace DIR] [--max-depth N] [--limit N] [--json]
          trace <symbol>     Follow callers/callees, references, a path, or a cross-language bridge.
                             [--workspace-id SELECTOR] [--workspace DIR] [--scope FILE] [--mode auto|path|refs|bridge] [--to SYMBOL] [--reference-kind KIND] [--no-definition] [--depth N] [--limit N] [--full] [--json]
          dashboard          Start or reuse the machine-global loopback dashboard.
                             [--port N] [--json]
          workspace [op]     Index lifecycle. op = status (default) | health | onboarding | leader | list | refresh | full | open | remove.
                             open   [--path DIR] [--full]   Register + index a directory (creates .miller/symbols.db).
                             leader [--handoff] [--wait]    Diagnose current leader and optionally request graceful handoff.
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
          onboarding
                   Summarize local tool telemetry into starter guidance for an indexed repo.
          leader   Diagnose the current indexer leader; --handoff queues a graceful abdication request.
          list     List registered workspaces (current first, then most-recently-seen). [--filter SUBSTR] [--limit N]
                   Compact caps at 20 rows (--limit N, <=0 unlimited); --filter narrows by display id or root.
          refresh  Incrementally refresh the index if the working tree changed.
          full     Force a full re-index (ignores the freshness check).
          open     Register + index a directory (creates .miller/symbols.db).  [--path DIR] [--full]
          remove   Delete a workspace's .miller index dir.                     (--id ID | --path DIR)

        Selectors / flags: [--id|--workspace-id SELECTOR] [--path|--workspace DIR] [--json] [--handoff] [--wait]
          --workspace-id aliases --id; --workspace (a directory, resolved against the cwd) aliases --path —
          the same selector flags every read verb accepts.
        """;
}
