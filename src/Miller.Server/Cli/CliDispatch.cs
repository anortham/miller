using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Core.Graph;
using Miller.Core.References;
using Miller.Core.Search;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Indexing.Semantic;
using Miller.Indexing.Store;
using Miller.Server.Git;
using Miller.Server.Hosting;
using Miller.Server.Resolution;
using Miller.Server.Telemetry;
using Miller.Server.Tools;
using Miller.Server.Workspaces;
using Miller.Testing;

namespace Miller.Server.Cli;

/// <summary>
/// Miller's command-line surface: a thin one-shot dispatch over the SAME pure tool cores the MCP server exposes
/// (each tool's <c>Run(...)</c> + the <see cref="WorkspaceRender"/> renderers), so a shell/CI invocation and a
/// tool call produce identical output. Read verbs pin one workspace read session and load the store or legacy view it
/// names: symbol search and inspect use the symbol lookup projection, graph verbs use the same session's immutable
/// repository graph, and bridge trace builds its bridge graph through the session-scoped
/// <see cref="SessionBridgeGraphLoader"/> without hydrating the repository dependency graph. There is NO
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
        IGitDiffReader gitDiffReader,
        TestsCoreHooks? testsHooks = null)
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
                case "rules":
                    return Rules(rest, stdout, stderr);
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
                            PatternFactsExportReader.WriteJsonLines,
                            PatternsExportLevelWarning);
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
                    return ArtifactExport(rest, context, stdout, stderr,
                        "miller references export [--jsonl] [--workspace-id SELECTOR] [--workspace DIR]",
                        ReferenceExportReader.WriteJsonLines,
                        ReferencesExportLevelWarning);
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
                case "tests":
                    return Tests(rest, context, stdout, stderr, testsHooks);
                case "ct-daemon":
                    return TestsDaemon(rest, context, stdout, stderr);
                case "semantic":
                    return Semantic(rest, context, stdout, stderr);
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

        if (!TryOpenCliReadScope(ctx, err, out CliReadScope readScope, "todos read failed"))
            return 3;

        using (readScope)
        try
        {
            string output = MarkerSearch.Run(
                readScope.Session,
                markers,
                o.Int("limit", MarkerSearch.DefaultLimit),
                o.Has("exclude-tests"),
                o.Has("json"),
                compactBanner: null,
                filePattern: o.Value("file-pattern"),
                language: o.Value("language"),
                out _);
            // The CLI calls the marker core directly, so the MCP wrapper's level check never runs here; without
            // this a symbols-level artifact answers "no markers" about facts nobody has extracted.
            if (SearchTool.MarkerLevelDiagnostic(readScope.Session.Snapshot.IndexLevel) is { } converging)
                output = ToolDiagnosticRenderer.Attach("todos", output, converging, o.Has("json"));
            outw.WriteLine(output);
            return 0;
        }
        catch (Exception ex) when (
            ex is FileNotFoundException or InvalidOperationException or IOException
                or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            err.WriteLine("todos failed to read code.marker.v1 facts: " + ex.Message);
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

    // `rules` is a version/help-class verb: it prints guidance embedded in this assembly and never touches a
    // workspace, so it stays above every index-loading verb. The rendered file goes to stdout alone, so
    // `miller rules --harness cursor > .cursor/rules/miller.mdc` writes a usable file; the target path is a
    // stderr note rather than a stdout header for exactly that reason.
    private static int Rules(IReadOnlyList<string> args, TextWriter outw, TextWriter err)
    {
        string usage = $"miller rules [--harness {RulesRender.HarnessChoices}]";
        CliOptions o = CliOptions.Parse(args);
        if (o.Positionals.Count > 0)
            return Usage(err, usage);

        // Unlike the read verbs, rules stdout is redirected into harness config files, so a misspelled option
        // (--harnes cursor) must fail loudly rather than silently emit the unframed block into a rules file.
        foreach (string flag in o.FlagNames)
        {
            if (!flag.Equals("harness", StringComparison.OrdinalIgnoreCase))
            {
                err.WriteLine($"unknown option '--{flag}'.");
                return Usage(err, usage);
            }
        }

        if (!o.Has("harness"))
        {
            outw.WriteLine(RulesRender.Render());
            return 0;
        }

        string? requested = o.Value("harness");
        if (string.IsNullOrWhiteSpace(requested))
            return Usage(err, usage);

        RulesRender.Harness? harness = RulesRender.FindHarness(requested);
        if (harness is null)
        {
            err.WriteLine($"unknown harness '{requested}'. Supported: {RulesRender.HarnessNames}.");
            return Usage(err, usage);
        }

        err.WriteLine($"write to: {harness.TargetPath} — {harness.Note}");
        outw.WriteLine(RulesRender.Render(harness));
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

    // `semantic prepare` is the explicit, consented model-download entry point: it shells out to the pinned
    // julie-semantic-sidecar's `prepare` subcommand (all download mechanics sidecar-owned, design §4.4), streams
    // its progress, and passes its exit status through. Like `version`/`dashboard` it loads NO index — it needs
    // only the tools root (where the sidecar ships) and the workspace `.miller` dir (where the progress marker
    // lives). Running the verb IS the consent act; Miller never auto-downloads.
    private static int Semantic(IReadOnlyList<string> args, WorkspaceContext ctx, TextWriter outw, TextWriter err)
    {
        const string usage = "miller semantic prepare [--model <id>] [--json]";
        if (args.Count == 0 || args[0] is "--help" or "-h" or "help")
            return Usage(err, usage);

        string operation = args[0].ToLowerInvariant();
        if (operation != "prepare")
        {
            err.WriteLine($"unknown semantic operation '{args[0]}'.");
            return Usage(err, usage);
        }

        CliOptions o = CliOptions.Parse(args.Skip(1).ToArray(), "json");
        if (o.Positionals.Count > 0)
            return Usage(err, usage);
        foreach (string flag in o.FlagNames)
        {
            if (!flag.Equals("model", StringComparison.OrdinalIgnoreCase) &&
                !flag.Equals("json", StringComparison.OrdinalIgnoreCase))
            {
                err.WriteLine($"unknown option '--{flag}'.");
                return Usage(err, usage);
            }
        }

        string millerDir = Path.GetDirectoryName(ctx.ExtractDbPath)
            ?? throw new InvalidOperationException("Cannot determine the workspace .miller directory.");
        return SemanticPrepareCli.Production(ctx.ToolsRoot, MillerHomeFor(ctx)).Run(
            new SemanticPrepareRequest(o.Value("model"), o.Has("json")),
            ctx.ToolsRoot,
            millerDir,
            outw,
            err);
    }

    // ---------- read verbs (over the current workspace's symbols.db) ----------

    private static int Search(IReadOnlyList<string> args, WorkspaceContext ctx, TextWriter outw, TextWriter err)
    {
        CliOptions o = CliOptions.Parse(args, "json", "include-tests", "exclude-tests");
        if (string.IsNullOrWhiteSpace(o.Query))
            return Usage(err, SearchUsage);
        string invocationRoot = ctx.CanonicalRoot ?? ctx.WorkspaceRoot;
        if (!TryResolveReadContext(ctx, o, err, out ctx))
            return 2;
        bool foreignWorkspace = !WorkspaceSafety.IsLiveWorkspace(
            invocationRoot,
            ctx.CanonicalRoot ?? ctx.WorkspaceRoot);
        if (!TryParseSearchArm(o.Value("arm"), out CliSearchArm requestedArm))
        {
            err.WriteLine("--arm must be auto, lexical, semantic, or hybrid.");
            return Usage(err, SearchUsage);
        }

        bool json = o.Has("json");
        int limit = o.Int("limit", SearchTool.DefaultLimit);
        string requestedMode = o.Value("mode", "auto")!;
        SearchRoute route;
        try
        {
            route = SearchRoutePlanner.Plan(requestedMode, o.Value("regions"), o.Query);
        }
        catch (ArgumentException ex)
        {
            err.WriteLine(ex.Message);
            return Usage(err, SearchUsage);
        }
        catch (InvalidOperationException ex)
        {
            err.WriteLine(ex.Message);
            return 2;
        }
        if (requestedArm is CliSearchArm.Semantic or CliSearchArm.Hybrid &&
            route.Kind != SearchRouteKind.Symbols)
        {
            err.WriteLine(
                $"--arm {WireName(requestedArm)} applies to the symbol search route only; " +
                $"mode {requestedMode} resolves to a different route.");
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
            if (route.Kind == SearchRouteKind.Markers)
            {
                if (!TryOpenCliReadScope(
                        ctx,
                        err,
                        out CliReadScope markerScope,
                        "search read failed",
                        requireSearchSidecar: false))
                    return 3;

                using CliReadScope markerReadScope = markerScope;
                try
                {
                    SearchRouteExecutionResult markerResult =
                        SearchRouteExecutor.RunMarkers(markerReadScope.Session, route, executionRequest);
                    string markerOutput = markerResult.Output;
                    // Same level decision the MCP markers route makes, reached through the same helper so the two
                    // surfaces cannot drift.
                    if (SearchTool.MarkerLevelDiagnostic(markerReadScope.Session.Snapshot.IndexLevel)
                        is { } converging)
                    {
                        markerOutput = ToolDiagnosticRenderer.Attach("search", markerOutput, converging, json);
                    }
                    outw.WriteLine(markerOutput);
                    return 0;
                }
                catch (Exception ex) when (
                    ex is FileNotFoundException or InvalidOperationException or IOException
                        or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    err.WriteLine("marker search failed to read code.marker.v1 facts: " + ex.Message);
                    return 3;
                }
            }

            SymbolSearchSidecar sidecar = SymbolSearchSidecar.FromEnvironment();
            if (!sidecar.Enabled || !sidecar.RegionOptions.Enabled)
            {
                err.WriteLine("region search is disabled. Enable MILLER_SEARCH_SIDECAR and unset MILLER_REGION_INDEX=0, then refresh the workspace.");
                return 3;
            }

            if (!TryOpenCliReadScope(
                    ctx,
                    err,
                    out CliReadScope regionScope,
                    "search read failed",
                    requireSearchSidecar: false))
                return 3;

            using CliReadScope regionReadScope = regionScope;
            try
            {
                FtsRegionSearchIndex regionIndex = regionReadScope.OpenRegionIndex();
                SearchRouteExecutionResult result =
                    SearchRouteExecutor.RunRegions(regionIndex, route, executionRequest);
                string regionOutput = result.Output;
                // Same level decision the MCP regions route makes, reached through the same helper so the two
                // surfaces cannot drift.
                if (SearchTool.RegionLevelDiagnostic(regionReadScope.Session.Snapshot.IndexLevel)
                    is { } converging)
                {
                    regionOutput = ToolDiagnosticRenderer.Attach("search", regionOutput, converging, json);
                }
                outw.WriteLine(regionOutput);
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
            if (!TryOpenCliReadScope(
                    ctx,
                    err,
                    out CliReadScope contentScope,
                    "search read failed",
                    requireSearchSidecar: false))
                return 3;

            using (contentScope)
            try
            {
                FtsTextContentSearchIndex contentIndex = contentScope.OpenContentIndex();
                if (requestedArm is CliSearchArm.Lexical)
                {
                    outw.WriteLine(SearchRouteExecutor.RunContent(contentIndex, route, executionRequest).Output);
                    return 0;
                }

                VectorSidecar vectors = VectorSidecar.FromEnvironment();
                CanaryMode canaryMode = CanaryActivation.FromEnvironment();
                using TelemetryLedger? ledger = TryOpenCliCanaryLedger(ctx, canaryMode, vectors.Mode);
                using TelemetryScope? telemetry = ledger?.Measure("search", CliModeName(route.Mode));
                using CliSemanticSession? semanticSession = vectors.Mode is SemanticMode.On
                    ? CreateCliSemanticSession(ctx)
                    : null;
                ISemanticTextArm? productionArm = vectors.Mode is SemanticMode.On
                    ? new SemanticTextArm(
                        SemanticMode.On,
                        root => new SemanticSearchArm(root, vectors, semanticSession!.Open))
                    : null;
                Func<ISemanticTextArm?>? treatmentArmFactory = vectors.Mode is SemanticMode.On
                    ? () => new SemanticTextArm(
                        SemanticMode.On,
                        root => new SemanticSearchArm(root, vectors, semanticSession!.Open))
                    : null;
                SearchTool.ContentCanaryOutcome outcome = RunNormalContentRoute(
                    contentIndex,
                    route,
                    executionRequest,
                    vectors.Mode,
                    canaryMode,
                    ctx.WorkspaceId ?? string.Empty,
                    ctx.WorkspaceRoot,
                    CanaryUtcDate(telemetry),
                    () => CanaryVectorProbe.From(vectors.Inspect(ctx.WorkspaceRoot)),
                    productionArm,
                    treatmentArmFactory,
                    foreignWorkspace,
                    telemetry);
                outw.WriteLine(outcome.Result.Output);
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
            if (!TryOpenCliReadScope(
                    ctx,
                    err,
                    out CliReadScope textContentScope,
                    "search read failed",
                    requireSearchSidecar: false))
                return 3;

            using (textContentScope)
            try
            {
                FtsTextContentSearchIndex textIndex = textContentScope.OpenContentIndex();
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

        if (!TryOpenCliReadScope(ctx, err, out CliReadScope readScope))
            return 3;

        using (readScope)
        {
            return RunSymbolRoute(
                readScope.SymbolIndex,
                route,
                executionRequest,
                requestedArm,
                foreignWorkspace,
                ctx,
                outw,
                err);
        }
    }

    private const string SearchUsage =
        "miller search <query> [--workspace-id SELECTOR] [--workspace DIR] " +
        "[--mode auto|text|symbol|file|markers|content|source|external|web|all-text] [--regions KINDS] " +
        "[--file-pattern GLOB] [--language LANG] [--arm auto|lexical|semantic|hybrid] [--limit N] [--json] " +
        "[--include-tests|--exclude-tests]";

    /// <summary>
    /// Runs the symbol route under the requested arm. The absent flag composes exactly what the MCP host
        /// composes — the policy-routed production arm. Semantic retrieval is on by default; explicit
        /// <c>MILLER_SEMANTIC=off</c> prevents the semantic arm from being built at all, so a CLI run and a tool
        /// call answer one query the same way.
    /// </summary>
    /// <remarks>
    /// <c>--arm</c> is an evaluation lever, so a forced semantic/hybrid run that cannot reach a serving artifact
    /// exits non-zero with the reason rather than quietly returning the lexical answer; a silent fallback would
    /// make an evaluation report a retrieval quality it never measured.
    /// </remarks>
    private static int RunSymbolRoute(
        ISymbolLookupIndex index,
        SearchRoute route,
        SearchRouteExecutionRequest request,
        CliSearchArm requestedArm,
        bool foreignWorkspace,
        WorkspaceContext ctx,
        TextWriter outw,
        TextWriter err)
    {
        if (requestedArm is CliSearchArm.Lexical)
        {
            outw.WriteLine(SearchRouteExecutor.RunSymbols(index, route, request).Output);
            return 0;
        }

        var sidecar = VectorSidecar.FromEnvironment();
        if (requestedArm is CliSearchArm.Policy)
        {
            using CliSemanticSession? policySession = sidecar.Mode is SemanticMode.Off
                ? null
                : CreateCliSemanticSession(ctx);
            Func<SemanticSymbolFusionArm>? armFactory = sidecar.Mode is SemanticMode.Off
                ? null
                : () => new SemanticSymbolFusionArm(
                    sidecar.Mode,
                    root => new SemanticSearchArm(root, sidecar, policySession!.Open));
            CanaryMode canaryMode = CanaryActivation.FromEnvironment();
            using TelemetryLedger? ledger = TryOpenCliCanaryLedger(ctx, canaryMode, sidecar.Mode);
            using TelemetryScope? telemetry = ledger?.Measure("search", CliModeName(route.Mode));
            SearchTool.SymbolCanaryOutcome outcome = RunNormalSymbolRoute(
                index,
                route,
                request,
                sidecar.Mode,
                canaryMode,
                ctx.WorkspaceId ?? string.Empty,
                ctx.WorkspaceRoot,
                CanaryUtcDate(telemetry),
                () => CanaryVectorProbe.From(sidecar.Inspect(ctx.WorkspaceRoot)),
                armFactory,
                foreignWorkspace,
                telemetry);
            outw.WriteLine(outcome.Result.Output);
            return 0;
        }

        string wire = WireName(requestedArm);
        if (sidecar.Mode is not SemanticMode.On)
        {
            err.WriteLine(
                $"--arm {wire} requires {VectorSidecar.EnvVar}=on; semantic retrieval is currently " +
                $"{sidecar.Mode.ToString().ToLowerInvariant()}.");
            return 3;
        }

        using (VectorStore? probe = sidecar.TryOpen(ctx.WorkspaceRoot, out string? unavailableReason))
        {
            if (probe is null)
            {
                err.WriteLine($"--arm {wire} needs a serving vector artifact: {unavailableReason}");
                return 3;
            }
        }

        using var session = CreateCliSemanticSession(ctx);
        if (session.Open() is null)
        {
            err.WriteLine(
                $"--arm {wire} needs the pinned julie-semantic-sidecar binary under '{ctx.ToolsRoot}'; " +
                "run the restore script and retry.");
            return 3;
        }

        return RunForcedArm(
            requestedArm,
            index,
            route,
            request,
            new SemanticSearchArm(ctx.WorkspaceRoot, sidecar, session.Open),
            outw,
            err);
    }

    internal static SearchTool.SymbolCanaryOutcome RunNormalSymbolRoute(
        ISymbolLookupIndex index,
        SearchRoute route,
        SearchRouteExecutionRequest request,
        SemanticMode semanticMode,
        CanaryMode canaryMode,
        string workspaceId,
        string workspaceRoot,
        string utcDate,
        Func<CanaryVectorProbe> vectorStateProbe,
        Func<SemanticSymbolFusionArm>? armFactory,
        bool foreignWorkspace,
        TelemetryScope? telemetry)
    {
        SemanticSymbolFusionArm? productionArm = semanticMode is SemanticMode.On
            ? armFactory?.Invoke()
            : null;
        SearchTool.SymbolCanaryOutcome outcome = SearchTool.RunSymbolsWithCanaryProbe(
            index,
            route,
            request with { FusionArm = productionArm, WorkspaceRoot = workspaceRoot },
            canaryMode,
            CliModeName(route.Mode),
            semanticDisabled: semanticMode is SemanticMode.Off,
            workspaceId,
            utcDate,
            vectorStateProbe,
            foreignWorkspace,
            armFactory,
            shadowRunner: null,
            semanticMode);

        if (telemetry is not null)
        {
            if (outcome.ShadowFacts is { } shadowFacts)
                CanaryTelemetry.StampShadow(telemetry, canaryMode, shadowFacts);
            else if (outcome.Facts is { } facts)
                SearchTool.StampSymbolCanary(
                    telemetry,
                    canaryMode,
                    facts,
                    outcome.ServingPolicy);
            CompleteCliSearchTelemetry(telemetry, request.Query, outcome.Result);
        }

        return outcome;
    }

    internal static SearchTool.ContentCanaryOutcome RunNormalContentRoute(
        ITextContentSearchIndex index,
        SearchRoute route,
        SearchRouteExecutionRequest request,
        SemanticMode semanticMode,
        CanaryMode canaryMode,
        string workspaceId,
        string workspaceRoot,
        string utcDate,
        Func<CanaryVectorProbe> vectorStateProbe,
        ISemanticTextArm? productionArm,
        Func<ISemanticTextArm?>? treatmentArmFactory,
        bool foreignWorkspace,
        TelemetryScope? telemetry)
    {
        Func<IReadOnlyList<ContentSearchHit>, IReadOnlyList<ContentSearchHit>>? productionRerank =
            productionArm is null
                ? null
                : SearchTool.BuildContentRerank(
                    productionArm,
                    request.Query,
                    workspaceRoot,
                    onConsult: null,
                    index,
                    contentKinds: null,
                    excludeTests: request.ExcludeTests is true,
                    request.FilePattern,
                    request.Language,
                    request.Limit);
        SearchTool.ContentCanaryOutcome outcome = SearchTool.RunContentWithCanaryProbe(
            index,
            request.Query,
            request.Limit,
            request.Json,
            request.CompactBanner,
            request.FilePattern,
            request.Language,
            request.SuggestionLookup,
            productionRerank,
            canaryMode,
            CliModeName(route.Mode),
            semanticDisabled: semanticMode is SemanticMode.Off,
            workspaceId,
            workspaceRoot,
            utcDate,
            vectorStateProbe,
            foreignWorkspace,
            treatmentArmFactory,
            excludeTests: request.ExcludeTests is true,
            semanticMode: semanticMode);

        if (telemetry is not null)
        {
            if (outcome.Facts is { } facts)
                SearchTool.StampContentCanary(
                    telemetry,
                    canaryMode,
                    facts,
                    outcome.ResultPathHashes,
                    outcome.ResultHashTruncated,
                    outcome.ServingPolicy);
            CompleteCliSearchTelemetry(telemetry, request.Query, outcome.Result);
        }

        return outcome;
    }

    private static TelemetryLedger? TryOpenCliCanaryLedger(
        WorkspaceContext ctx,
        CanaryMode canaryMode,
        SemanticMode semanticMode)
    {
        if (canaryMode is CanaryMode.Off || semanticMode is SemanticMode.Off ||
            string.IsNullOrWhiteSpace(ctx.WorkspaceId))
            return null;

        try
        {
            return TelemetryLedger.Open(ctx.TelemetryDbPath, ctx.WorkspaceId, ctx.WorkspaceRoot);
        }
        catch (Exception ex) when (
            ex is SqliteException or IOException or UnauthorizedAccessException
                or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private static void CompleteCliSearchTelemetry(
        TelemetryScope telemetry,
        string query,
        SearchRouteExecutionResult result)
    {
        telemetry.SetTarget(query);
        telemetry.ResultCount = result.Count;
        telemetry.SourceBytes = result.SourceBytes;
        telemetry.BytesReturned = Encoding.UTF8.GetByteCount(result.Output);
        telemetry.Outcome = result.Count == 0 ? TelemetryOutcome.Empty : TelemetryOutcome.Ok;
    }

    private static string CanaryUtcDate(TelemetryScope? telemetry) =>
        telemetry?.UtcDate ?? DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string CliModeName(SearchToolMode mode) => mode switch
    {
        SearchToolMode.AllText => "all-text",
        _ => mode.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// Runs <c>--arm semantic</c> or <c>--arm hybrid</c> against an already-opened arm.
    /// </summary>
    /// <remarks>
    /// The pre-query probe proves an artifact existed, not that the query was served: a handshake failure, an
    /// open circuit, a KNN fault, or an artifact promoted between the probe and the query all leave the arm
    /// unserved. Forced arms exist to measure retrieval, so an unserved query exits 3 with the reason and prints
    /// nothing — an arm that ran and found nothing is a real answer and renders the lexical bytes.
    /// </remarks>
    internal static int RunForcedArm(
        CliSearchArm requestedArm,
        ISymbolLookupIndex index,
        SearchRoute route,
        SearchRouteExecutionRequest request,
        SemanticSearchArm arm,
        TextWriter outw,
        TextWriter err)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(arm);
        ArgumentNullException.ThrowIfNull(outw);
        ArgumentNullException.ThrowIfNull(err);

        string wire = WireName(requestedArm);
        if (route.Mode == SearchToolMode.File || route.Mixed)
        {
            string routeName = route.Mixed ? "mixed file/symbol route" : "file route";
            err.WriteLine(
                $"--arm {wire} does not support the {routeName}; use --arm lexical.");
            return 3;
        }

        if (requestedArm is CliSearchArm.Hybrid)
        {
            var forced = new ForcedHybridFusionArm(() => arm);
            string fusedOutput = SearchRouteExecutor
                .RunSymbols(index, route, request with { FusionArm = forced })
                .Output;

            if (forced.UnservedReason is { } fusionReason)
            {
                err.WriteLine($"--arm {wire} could not query the vector artifact: {fusionReason}");
                return 3;
            }

            if (!forced.Queried)
            {
                err.WriteLine(
                    $"--arm {wire} never reached the semantic arm: this query resolved to a file-name lookup, " +
                    "which the symbol vector corpus does not serve.");
                return 3;
            }

            outw.WriteLine(fusedOutput);
            return 0;
        }

        SymbolCandidateSet candidates = SearchRouteExecutor.CollectSymbolCandidates(index, route, request);
        SemanticQueryResult result = arm
            .QuerySymbolsAsync(request.Query, request.Limit, AdmitsUnder(index, candidates.Visibility))
            .GetAwaiter()
            .GetResult();
        if (!result.Served)
        {
            err.WriteLine($"--arm {wire} could not query the vector artifact: {result.UnavailableReason}");
            return 3;
        }

        outw.WriteLine(CliSemanticRender.Symbols(index, result.Hits, request.Query, request.Limit, request.Json));
        return 0;
    }

    /// <summary>
    /// The lexical stage's own visibility rules as a vector-match predicate, so <c>--arm semantic</c> hides the
    /// test symbols and out-of-filter files the same query answered lexically would have hidden — and, because
    /// the arm answers a rejecting filter by fetching deeper, a rejected neighbour is refilled rather than
    /// spending a slot.
    /// </summary>
    private static Func<VectorMatch, bool> AdmitsUnder(
        ISymbolLookupIndex index,
        SymbolVisibilityPolicy? visibility) =>
        match => index.FindBySymbolId(match.UnitId) is { } symbol && (visibility?.Allows(symbol) ?? true);

    /// <summary>Parses the <c>--arm</c> value; an absent flag is <see cref="CliSearchArm.Policy"/>.</summary>
    internal static bool TryParseSearchArm(string? raw, out CliSearchArm arm)
    {
        if (raw is null)
        {
            arm = CliSearchArm.Policy;
            return true;
        }

        switch (raw.Trim().ToLowerInvariant())
        {
            case "auto":
                arm = CliSearchArm.Policy;
                return true;
            case "lexical":
                arm = CliSearchArm.Lexical;
                return true;
            case "semantic":
                arm = CliSearchArm.Semantic;
                return true;
            case "hybrid":
                arm = CliSearchArm.Hybrid;
                return true;
            default:
                arm = CliSearchArm.Policy;
                return false;
        }
    }

    private static string WireName(CliSearchArm arm) => arm switch
    {
        CliSearchArm.Lexical => "lexical",
        CliSearchArm.Semantic => "semantic",
        CliSearchArm.Hybrid => "hybrid",
        _ => "policy",
    };

    private static int Content(IReadOnlyList<string> args, WorkspaceContext ctx, TextWriter outw, TextWriter err)
    {
        if (args.Count == 0)
            return Usage(err, "miller content <import|add-markdown|search|read|shape|list|remove|export> [args] [--json]");

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

        var store = new ContentCorpusExternalStore();
        var tool = new ContentTool(ctx, store);
        ContentCorpusReadLocation contentLocation = ContentCorpusReadLocator.Resolve(
            ctx.ExtractDbPath,
            ctx.CanonicalRoot ?? ctx.WorkspaceRoot,
            ctx.WorkspaceId);
        if (string.Equals(operation, "list", StringComparison.OrdinalIgnoreCase))
        {
            string contentDbPath = contentLocation.ContentDbPath;
            string? requestedKind = o.Value("kind", o.Value("content-kind"));
            string? contentKind;
            try
            {
                contentKind = ContentListKind(requestedKind);
            }
            catch (InvalidOperationException ex)
            {
                err.WriteLine(ContentTool.RenderFailure(
                    operation.Trim().ToLowerInvariant(),
                    ex,
                    json));
                return 3;
            }
            IReadOnlyList<ExternalContentSource> sources = contentKind is null
                ? [
                    .. store.List(contentDbPath, TextContentKind.ExternalFile),
                    .. store.List(contentDbPath, TextContentKind.Web),
                ]
                : store.List(contentDbPath, contentKind);
            WriteOutput(outw, json ? RenderCliContentListJson(sources) : RenderCliContentListCompact(sources));
            return 0;
        }

        if (string.Equals(operation, "export", StringComparison.OrdinalIgnoreCase))
        {
            string contentDbPath = contentLocation.ContentDbPath;
            string? requestedKind = o.Value("kind", o.Value("content-kind"));
            if (!string.IsNullOrWhiteSpace(requestedKind))
            {
                try
                {
                    _ = ContentListKind(requestedKind);
                }
                catch (InvalidOperationException ex)
                {
                    err.WriteLine(ContentTool.RenderFailure(
                        operation.Trim().ToLowerInvariant(),
                        ex,
                        json));
                    return 3;
                }
            }
            // Export is a published Eros-facing contract, so workspace-derived rows must not come from a
            // superseded extract generation. Imported external/web rows have no symbols.db counterpart and stay
            // exportable regardless.
            if (ExportCoversWorkspaceKinds(requestedKind)
                && File.Exists(contentDbPath)
                && !ContentCorpusReadLocator.IsCurrent(contentLocation, ctx.ExtractDbPath))
            {
                err.WriteLine(ContentTool.RenderFailure(
                    "export",
                    new InvalidOperationException(
                        $"Content corpus sidecar at '{contentDbPath}' was built from a different index " +
                        "generation (the workspace was fully rebuilt). Run `miller workspace refresh` before " +
                        "exporting workspace content."),
                    json));
                return 3;
            }

            var reader = new ContentCorpusExportReader();
            reader.WriteJsonLines(
                contentDbPath,
                outw,
                requestedKind,
                o.Value("content-workspace-id"));
            return 0;
        }

        ContentToolExecutionResult result = tool.Execute(
            operation,
            path,
            query,
            o.Value("source-id"),
            o.Value("url"),
            o.Value("display-path"),
            o.Value("kind", o.Value("content-kind")),
            o.Value("workspace-id"),
            o.Has("line") ? o.Int("line", 0) : null,
            o.Has("context-lines") ? o.Int("context-lines", ContentCorpusExternalStore.DefaultContextLines) : null,
            o.Int("limit", SearchTool.DefaultLimit),
            LongOption(o, "max-bytes"),
            json ? "json" : "compact");

        if (result.IsError)
        {
            err.WriteLine(result.Output);
            return 3;
        }

        WriteOutput(outw, result.Output);
        return 0;
    }

    // Unset or "all" exports every kind, so both cover workspace-derived rows. An unrecognised value is left to
    // the reader's own validation rather than silently treated as import-only.
    private static bool ExportCoversWorkspaceKinds(string? requestedKind) =>
        string.IsNullOrWhiteSpace(requestedKind) ||
        requestedKind.Trim().ToLowerInvariant() switch
        {
            "all" or "source" or "workspace_source" or "docs" or "doc" or "workspace_docs"
                or "config" or "workspace_config" => true,
            "external" or "external_file" or "file" or "web" => false,
            _ => true,
        };

    private static string? ContentListKind(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? TextContentKind.ExternalFile
            : value.Trim().ToLowerInvariant() switch
            {
                "all" => null,
                "external" or "external_file" or "file" => TextContentKind.ExternalFile,
                "source" or "workspace_source" => TextContentKind.WorkspaceSource,
                "docs" or "doc" or "workspace_docs" => TextContentKind.WorkspaceDocs,
                "config" or "workspace_config" => TextContentKind.WorkspaceConfig,
                "web" => TextContentKind.Web,
                _ => throw new InvalidOperationException(
                    "content_kind must be all, workspace_source, workspace_docs, workspace_config, external_file, or web."),
            };

    private static string RenderCliContentListCompact(IReadOnlyList<ExternalContentSource> sources) =>
        sources.Count == 0
            ? "No imported content."
            : string.Join(
                '\n',
                sources.Select(static source =>
                    $"{source.SourceId}  {source.ContentKind}  {source.SourceBytes} bytes  " +
                    $"{source.ChunkCount} chunks  {source.DisplayPath}"));

    private static string RenderCliContentListJson(IReadOnlyList<ExternalContentSource> sources)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(
            buffer,
            new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartArray();
            foreach (ExternalContentSource source in sources)
            {
                writer.WriteStartObject();
                writer.WriteString("source_id", source.SourceId);
                writer.WriteString("content_kind", source.ContentKind);
                writer.WriteString("display_path", source.DisplayPath);
                if (source.Url is null) writer.WriteNull("url");
                else writer.WriteString("url", source.Url);
                writer.WriteString("content_hash", source.ContentHash);
                writer.WriteNumber("source_bytes", source.SourceBytes);
                writer.WriteNumber("line_count", source.LineCount);
                writer.WriteNumber("chunk_count", source.ChunkCount);
                writer.WriteString("indexed_at_utc", source.IndexedAtUtc);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static int Patterns(IReadOnlyList<string> args, WorkspaceContext ctx, TextWriter outw, TextWriter err)
    {
        if (args.Count > 0 && args[0] is "--help" or "-h")
            return Usage(err, "miller patterns <list|summary|search|export> [--workspace-id SELECTOR] [--workspace DIR] [--pattern ID] [--query TEXT] [--language LANG] [--path GLOB] [--where key=value] [--group-by file|directory|top_directory] [--facet KEY] [--limit N] [--continuation TOKEN] [--json]");

        bool firstTokenIsFlag = args.Count > 0 && args[0].StartsWith("--", StringComparison.Ordinal);
        string operation = args.Count == 0 || firstTokenIsFlag ? "list" : args[0].ToLowerInvariant();
        if (operation is "help" or "--help" or "-h")
            return Usage(err, "miller patterns <list|summary|search|export> [--workspace-id SELECTOR] [--workspace DIR] [--pattern ID] [--query TEXT] [--language LANG] [--path GLOB] [--where key=value] [--group-by file|directory|top_directory] [--facet KEY] [--limit N] [--continuation TOKEN] [--json]");

        if (operation is not ("list" or "summary" or "summarize" or "search"))
            return Usage(err, "miller patterns <list|summary|search|export> [--workspace-id SELECTOR] [--workspace DIR] [--pattern ID] [--query TEXT] [--language LANG] [--path GLOB] [--where key=value] [--group-by file|directory|top_directory] [--facet KEY] [--limit N] [--continuation TOKEN] [--json]");

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
            catch (ToolDiagnosticException ex) when (
                ex.Diagnostic.Class is ToolDiagnosticClass.Refusal or ToolDiagnosticClass.Unsupported)
            {
                err.WriteLine(ex.Diagnostic.Message);
                return 2;
            }

        }

        if (operation == "search" && string.IsNullOrWhiteSpace(patternId) && string.IsNullOrWhiteSpace(query))
            return Usage(err, "miller patterns search --pattern ID | --query TEXT [--workspace-id SELECTOR] [--workspace DIR] [--language LANG] [--path GLOB] [--where key=value] [--limit N] [--continuation TOKEN] [--json]");

        if ((operation is "list" or "summary" or "summarize") && !string.IsNullOrWhiteSpace(query))
        {
            err.WriteLine("patterns query is only supported for search.");
            return 2;
        }

        if (!TryResolveReadContext(ctx, o, err, out ctx))
            return 2;
        if (!TryOpenCliReadScope(ctx, err, out CliReadScope readScope, "patterns failed"))
            return 3;

        using (readScope)
        {
            try
            {
                PatternFactsReader patternReader = new();
                PatternToolResult result = readScope.IsFamilyStore
                    ? PatternsTool.Run(
                        patternReader,
                        readScope.Session,
                        operation,
                        patternId,
                        query,
                        o.Value("language"),
                        o.Value("path", o.Value("file-pattern")),
                        where,
                        o.Value("group-by", o.Value("group_by")),
                        o.Value("facet"),
                        o.Int("limit", PatternsTool.DefaultLimit),
                        o.Has("json"),
                        workspaceId: ctx.WorkspaceId ?? "current",
                        continuation: o.Value("continuation"))
                    : PatternsTool.Run(
                        patternReader,
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
                        o.Has("json"),
                        workspaceId: ctx.WorkspaceId ?? "current",
                        continuation: o.Value("continuation"));
                string output = result.LevelDiagnostic is { } converging
                    ? ToolDiagnosticRenderer.Attach("patterns", result.Output, converging, o.Has("json"))
                    : result.Output;
                WriteOutput(outw, output);
                return 0;
            }
            catch (ToolDiagnosticException ex) when (
                ex.Diagnostic.Class is ToolDiagnosticClass.Refusal or ToolDiagnosticClass.Unsupported)
            {
                err.WriteLine(ex.Diagnostic.Message);
                return 2;
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
        const string usage = "miller metrics <churn|clones|complexity|risk|history> [--workspace-id SELECTOR] [--workspace DIR] [--limit N] [--json] [--range REV..REV] [--include-commits] [--min-count N] [--max-symbols-per-group N] [--near-duplicates] [--min-severity low|moderate|high] [--include-tests|--exclude-tests] [--metric a,b,…]";
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

        CliOptions o = CliOptions.Parse((firstTokenIsFlag ? args : args.Skip(1)).ToArray(), "json", "include-tests", "exclude-tests", "include-commits", "near-duplicates");
        if (!TryResolveReadContext(ctx, o, err, out ctx))
            return 2;
        if (!TryOpenCliReadScope(ctx, err, out CliReadScope readScope, "metrics failed", requireSearchSidecar: false))
            return 3;

        // churn/risk record the git-backed arms; a clones run records ONLY when the opt-in Type-2 arm actually ran
        // (exact clone counts stay owned by the leader converge arm). Capture identity BEFORE computing.
        // Canonical for churn/risk = default range/limit/test-filter and no --include-commits. A clones run is
        // ALWAYS canonical: near_duplicate_group_count is the exact group count of a fixed-bound scan, so no
        // clones flag can change it — and a truncated scan is suppressed at the source (MetricsTool) rather than
        // here, because only the scan knows whether it saw everything.
        bool clonesRecordable =
            (operation is "clones" or "clone" or "duplicate" or "duplicates") && o.Has("near-duplicates");
        bool gitArm = operation is "churn" or "risk";
        bool recordable = gitArm || clonesRecordable;
        bool canonical = clonesRecordable
            || (gitArm
                && !o.Has("range") && !o.Has("limit")
                && !o.Has("include-tests") && !o.Has("exclude-tests") && !o.Has("include-commits"));
        using (readScope)
        {
            HeavyArmIdentity? identity = canonical ? CaptureHeavyArmIdentity(ctx, readScope.Session) : null;
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
                    historyReader: new ProcessGitHistoryReader(),
                    nearDuplicates: o.Has("near-duplicates"),
                    readSession: readScope.Session);
                WriteOutput(outw, result.Output);
                if (recordable)
                    RecordHeavyArmSnapshot(
                        ctx,
                        identity,
                        clonesRecordable
                            ? MetricHistoryHeavyArm.ClonesSource
                            : operation == "churn"
                                ? MetricHistoryHeavyArm.ChurnSource
                                : MetricHistoryHeavyArm.RiskSource,
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
        const string usage = "miller report [--json] [--workspace-id SELECTOR] [--workspace DIR] [--range REV..REV] [--limit N] [--include-tests|--exclude-tests] [--near-duplicates]";
        if (args.Count > 0 && args[0] is "--help" or "-h" or "help")
            return Usage(err, usage);

        CliOptions o = CliOptions.Parse(args.ToArray(), "json", "include-tests", "exclude-tests", "near-duplicates");
        if (o.Positionals.Count > 0)
            return Usage(err, usage);
        if (!TryResolveReadContext(ctx, o, err, out ctx))
            return 2;
        if (!TryOpenCliReadScope(
                ctx,
                err,
                out CliReadScope readScope,
                "report read failed",
                requireSearchSidecar: false))
            return 3;

        using (readScope)
        {
            bool canonical = !o.Has("range") && !o.Has("limit") && !o.Has("exclude-tests") && !o.Has("include-tests");
            HeavyArmIdentity? identity = canonical ? CaptureHeavyArmIdentity(ctx) : null;

            try
            {
                IRegionSearchIndex? regionIndex = null;
                SymbolSearchSidecar sidecar = SymbolSearchSidecar.FromEnvironment();
                if (sidecar.Enabled && sidecar.RegionOptions.Enabled)
                {
                    try
                    {
                        regionIndex = readScope.OpenRegionIndex();
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
                    regionIndex: regionIndex,
                    nearDuplicates: o.Has("near-duplicates"),
                    readSession: readScope.Session);
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
    }

    private const string TelemetryUsage =
        "miller telemetry export [--jsonl] [--workspace-id ID|all] | " +
        "miller telemetry canary [--json] [--contract 2|3] [--source-id ID] [--from YYYY-MM-DD] [--to YYYY-MM-DD] | " +
        "miller telemetry canary --gate [--json] [--contract 2|3] | " +
        "miller telemetry canary combine <export.json>... [--json]";

    private static int Telemetry(IReadOnlyList<string> args, WorkspaceContext ctx, TextWriter outw, TextWriter err)
    {
        if (args.Count == 0)
            return Usage(err, TelemetryUsage);

        string operation = args[0].ToLowerInvariant();
        IReadOnlyList<string> tail = args.Skip(1).ToArray();

        if (operation == "export")
        {
            CliOptions o = CliOptions.Parse(tail, "jsonl");
            if (o.Positionals.Count > 0)
                return Usage(err, TelemetryUsage);
            TelemetryExportReader.WriteJsonLines(ctx.TelemetryDbPath, outw, o.Value("workspace-id"));
            return 0;
        }

        if (operation == "canary")
            return Canary(tail, ctx, outw, err);

        return Usage(err, TelemetryUsage);
    }

    private static int Canary(IReadOnlyList<string> tail, WorkspaceContext ctx, TextWriter outw, TextWriter err)
    {
        if (tail.Count > 0 && tail[0].Equals("combine", StringComparison.OrdinalIgnoreCase))
            return CanaryCombine(tail.Skip(1).ToArray(), outw, err);

        CliOptions o = CliOptions.Parse(tail, "json", "gate");
        if (o.Positionals.Count > 0 || o.FlagNames.Any(static name => !IsCanaryFlag(name)))
            return Usage(err, TelemetryUsage);

        int contractVersion = o.Value("contract") switch
        {
            null when !o.Has("contract") => CanaryContractProfile.V2ContractVersion,
            "2" => CanaryContractProfile.V2ContractVersion,
            "3" => CanaryContractProfile.V3ContractVersion,
            _ => 0,
        };
        if (contractVersion == 0)
            return Usage(err, TelemetryUsage);

        if (o.Has("gate"))
        {
            if (o.Has("from") || o.Has("to") || o.Has("source-id"))
                return Usage(err, TelemetryUsage);
            outw.WriteLine(CanaryGateReport.Render(ctx.TelemetryDbPath, o.Has("json"), contractVersion));
            return 0;
        }

        string? sourceId = o.Value("source-id");
        if (contractVersion == CanaryContractProfile.V2ContractVersion && o.Has("source-id"))
            return Usage(err, TelemetryUsage);
        if (contractVersion == CanaryContractProfile.V3ContractVersion && !CanaryExport.IsValidSourceId(sourceId))
            return Usage(err, TelemetryUsage);

        DateOnly to = DateOnly.FromDateTime(DateTime.UtcNow);
        DateOnly from = to.AddDays(-30);
        if (o.Value("to") is { } toText && !TryParseIsoDate(toText, out to))
            return Usage(err, TelemetryUsage);
        if (o.Value("from") is { } fromText && !TryParseIsoDate(fromText, out from))
            return Usage(err, TelemetryUsage);
        if (!o.Has("from") && o.Has("to"))
            from = to.AddDays(-30);
        if (from > to)
            return Usage(err, TelemetryUsage);

        var generatedAt = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        outw.WriteLine(CanaryExport.BuildJson(
            ctx.TelemetryDbPath, from, to, generatedAt, contractVersion, sourceId));
        return 0;
    }

    private static int CanaryCombine(IReadOnlyList<string> tail, TextWriter outw, TextWriter err)
    {
        CliOptions options = CliOptions.Parse(tail, "json");
        if (options.Positionals.Count == 0
            || options.FlagNames.Any(static name => !name.Equals("json", StringComparison.OrdinalIgnoreCase)))
        {
            return Usage(err, TelemetryUsage);
        }

        try
        {
            string[] documents = options.Positionals.Select(File.ReadAllText).ToArray();
            CanaryAggregateReport report = CanaryAggregate.Combine(documents);
            outw.WriteLine(CanaryAggregate.Render(report, options.Has("json")));
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            err.WriteLine("canary combine failed: an export document could not be read.");
            return 3;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or JsonException)
        {
            err.WriteLine("canary combine failed: " + ex.Message);
            return 3;
        }
    }

    private static bool IsCanaryFlag(string name) =>
        name.Equals("json", StringComparison.OrdinalIgnoreCase)
        || name.Equals("gate", StringComparison.OrdinalIgnoreCase)
        || name.Equals("contract", StringComparison.OrdinalIgnoreCase)
        || name.Equals("source-id", StringComparison.OrdinalIgnoreCase)
        || name.Equals("from", StringComparison.OrdinalIgnoreCase)
        || name.Equals("to", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseIsoDate(string value, out DateOnly date) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

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
        Action<WorkspaceReadHandle, TextWriter> export,
        Func<WorkspaceReadHandle, string?>? levelWarning = null)
    {
        if (args.Count == 0 || args[0] is "--help" or "-h" or "help")
            return Usage(err, usage);
        string operation = args[0].ToLowerInvariant();
        CliOptions o = CliOptions.Parse(args.Skip(1).ToArray(), "jsonl");
        if (operation != "export" || o.Positionals.Count > 0)
            return Usage(err, usage);
        if (!TryResolveReadContext(ctx, o, err, out ctx))
            return 2;
        if (!TryOpenCliReadScope(
                ctx,
                err,
                out CliReadScope readScope,
                $"{usage["miller ".Length..].Split(' ', 2)[0]} failed",
                requireSearchSidecar: false))
            return 3;

        using (readScope)
        {
            // Degradation is announced on stderr BEFORE the rows, never as a line inside them: stdout is a JSONL
            // contract a fleet consumer parses line by line, so a prose or differently-shaped line there breaks it.
            // The warning is per-feed because only some source tables are left empty by a symbols-level scan.
            if (levelWarning?.Invoke(readScope.Session) is { } warning)
                err.WriteLine(warning);

            export(readScope.Session, outw);
            return 0;
        }
    }

    private static string? PatternsExportLevelWarning(WorkspaceReadHandle session) =>
        PatternsTool.FactsLevelDiagnostic(session.Snapshot.IndexLevel) is { } converging
            ? ToolDiagnosticRenderer.Render("patterns", converging, json: false)
            : null;

    private static string? ReferencesExportLevelWarning(WorkspaceReadHandle session) =>
        IndexLevelGuard.IsSymbolsLevel(session.Snapshot.IndexLevel)
            ? ToolDiagnosticRenderer.Render("references", IndexLevelGuard.ReferenceExportConverging(), json: false)
            : null;

    // ---------- heavy-arm metric-history recording (report / metrics churn|risk) ----------
    //
    // The single CLI-side hook the heavy commands share. Each command captures the artifact identity BEFORE it
    // computes (below), renders normally, then hands the already-composed metric points here. Recording is
    // best-effort telemetry: a failed history write warns on stderr and NEVER changes the command's output or exit
    // code. Only CANONICAL (default-params) runs record — a non-default run renders as usual and skips, because a
    // trend line that mixes ranges/limits is incomparable. Design: docs/plans/2026-07-07-metric-history-design.md.

    /// <summary>The artifact identity a heavy command captured before computing, for the append-time re-check.</summary>
    internal readonly record struct HeavyArmIdentity(
        string WorkspaceId, string ArtifactId, long Revision, string ExtractorVersion);

    /// <summary>
    /// Capture <c>(workspace_id, artifact_id, revision, extractor_version)</c> from the serving read session.
    /// BEFORE the command computes, so a full-rebuild promotion mid-command is caught by the append-time re-check.
    /// Returns <c>null</c> — recording is skipped silently — when there is no stable identity to attach history to
    /// (no <c>.miller</c> index, no artifact_id, no revision yet) or the DB cannot be read. Never throws.
    /// </summary>
    internal static HeavyArmIdentity? CaptureHeavyArmIdentity(
        WorkspaceContext ctx,
        IWorkspaceReadSession? readSession = null)
    {
        try
        {
            if (readSession is null && StoreReadExpected(ctx))
            {
                using WorkspaceReadHandle storeSession = WorkspaceReadSessionFactory.Open(
                    ctx.ExtractDbPath,
                    ctx.CanonicalRoot ?? ctx.WorkspaceRoot,
                    ctx.WorkspaceId);
                return CaptureHeavyArmIdentity(ctx, storeSession);
            }

            if (readSession is not null)
            {
                WorkspaceFreshnessToken sessionFreshness = readSession.Snapshot.Freshness;
                long historyRevision = sessionFreshness.StoreLogSequence ?? sessionFreshness.Revision;
                string sessionWorkspaceId = ctx.WorkspaceId
                    ?? readSession.Snapshot.WorkspaceId
                    ?? WorkspaceId.FromCanonicalRoot(Path.GetFullPath(ctx.CanonicalRoot ?? ctx.WorkspaceRoot));
                if (string.IsNullOrWhiteSpace(sessionFreshness.ArtifactOrStoreId)
                    || historyRevision <= 0
                    || string.IsNullOrWhiteSpace(sessionWorkspaceId))
                    return null;

                string sessionExtractorVersion = readSession.Read(
                    static connection => ExtractBinaryVersionReader.TryRead(connection)) ?? string.Empty;
                return new HeavyArmIdentity(
                    sessionWorkspaceId,
                    sessionFreshness.ArtifactOrStoreId,
                    historyRevision,
                    sessionExtractorVersion);
            }

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
                historyDbPath, snapshot, () => RecheckHeavyArmIdentity(ctx), recordedAtUtc);
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
    private static (string ArtifactId, long Revision) RecheckHeavyArmIdentity(WorkspaceContext ctx)
    {
        try
        {
            if (StoreReadExpected(ctx))
            {
                using WorkspaceReadHandle session = WorkspaceReadSessionFactory.Open(
                    ctx.ExtractDbPath,
                    ctx.CanonicalRoot ?? ctx.WorkspaceRoot,
                    ctx.WorkspaceId);
                return (
                    session.Snapshot.Freshness.ArtifactOrStoreId,
                    session.Snapshot.Freshness.StoreLogSequence
                    ?? session.Snapshot.Freshness.Revision);
            }

            string extractDbPath = ctx.ExtractDbPath;
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

    private static bool StoreReadExpected(WorkspaceContext ctx) =>
        WorkspaceReadSessionFactory.StoreEnabledFromEnvironment()
        || StoreWorkspacePointer.Read(ctx.CanonicalRoot ?? ctx.WorkspaceRoot) is not null;

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
            return Usage(err, "miller inspect <file-or-symbol> [--workspace-id SELECTOR] [--workspace DIR] [--depth summary|overview|full] [--kind K] [--scope FILE] [--limit N] [--continuation TOKEN] [--json]");
        if (!TryResolveReadContext(ctx, o, err, out ctx))
            return 2;

        string depth = o.Value("depth", "summary")!;
        string? continuation = o.Value("continuation");
        if (o.Has("continuation") &&
            (string.IsNullOrWhiteSpace(continuation) ||
             !string.Equals(depth, "full", StringComparison.OrdinalIgnoreCase)))
        {
            return Usage(err, "--continuation requires a token and --depth full.");
        }
        string output;
        try
        {
            if (!TryOpenCliReadScope(ctx, err, out CliReadScope readScope))
                return 3;
            using (readScope)
            {
                ISymbolLookupIndex index = readScope.SymbolIndex;

                bool rendersReferences =
                    string.Equals(depth, "full", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(depth, "overview", StringComparison.OrdinalIgnoreCase);
                ToolDiagnostic? diagnostic;

                if (rendersReferences)
                {
                    output = InspectTool.RunLookup(
                        index, readScope.Session, ctx.WorkspaceRoot,
                        target: o.Query, depth, kind: o.Value("kind"), scope: o.Value("scope"),
                        limit: o.Int("limit", 50), json: o.Has("json"), out _, out diagnostic,
                        continuation: continuation);
                }
                else
                {
                    output = InspectTool.RunSummary(
                        index, readScope.Session, ctx.WorkspaceRoot,
                        target: o.Query, kind: o.Value("kind"), scope: o.Value("scope"),
                        limit: o.Int("limit", 50), json: o.Has("json"), out _, out diagnostic);
                }

                if (diagnostic is null
                    && rendersReferences
                    && IndexLevelGuard.IsSymbolsLevel(readScope.Session.Snapshot.IndexLevel))
                {
                    output = InspectTool.AttachUsageEvidenceUnavailable(output, o.Has("json"));
                }

                if (diagnostic is not null)
                    output = ToolDiagnosticRenderer.Attach("inspect", output, diagnostic, o.Has("json"));
            }
        }
        catch (ToolDiagnosticException ex) when (
            ex.Diagnostic.Class is ToolDiagnosticClass.Refusal or ToolDiagnosticClass.Unsupported)
        {
            err.WriteLine(ex.Diagnostic.Message);
            return 2;
        }
        outw.WriteLine(output);
        return 0;
    }

    private static int Context(IReadOnlyList<string> args, WorkspaceContext ctx, TextWriter outw, TextWriter err)
    {
        CliOptions o = CliOptions.Parse(args, "json", "exclude-tests");
        if (string.IsNullOrWhiteSpace(o.Query))
            return Usage(err, "miller context <query> [--workspace-id SELECTOR] [--workspace DIR] [--token-budget N] [--max-hops 0-2] [--entry-symbol NAME] [--edited-files PATHS] [--failing-test TEXT] [--stack-trace TEXT] [--reference-mode off|usage] [--reference-depth 0-1] [--exclude-tests] [--json]");
        int tokenBudget = o.Int("token-budget", 2000);
        if (tokenBudget <= 0)
            return 0;
        if (!TryResolveReadContext(ctx, o, err, out ctx))
            return 2;

        if (!TryOpenCliReadScope(ctx, err, out CliReadScope readScope))
            return 3;

        using (readScope)
        {
        ISymbolLookupIndex index = readScope.SymbolIndex;
        ISymbolGraphReachability graph = readScope.Graph;
        var resolver = new SmartTargetResolver(index);
        string referenceMode = o.Value("reference-mode", "off")!;
        string[]? entrySymbols = OptionValues(o.Value("entry-symbol"));
        string[]? editedFiles = OptionValues(o.Value("edited-files"));
        string? failingTest = o.Value("failing-test");
        string? stackTrace = o.Value("stack-trace");
        bool json = o.Has("json");
        bool excludeTestsFlag = o.Has("exclude-tests");
        bool rescueExcludeTests = string.Equals(referenceMode, "usage", StringComparison.OrdinalIgnoreCase)
            ? excludeTestsFlag
            : SearchTool.ResolveExcludeTests(null, o.Query, SearchToolMode.Symbol);
        IReadOnlyList<ContextTool.ContextSourceSeed> sourceSeeds = LoadContextSourceRescueSeeds(
            index,
            ctx,
            o.Query,
            rescueExcludeTests);
        int selectedCount;
        int candidatesExamined;
        string output;
        if (string.Equals(referenceMode, "usage", StringComparison.OrdinalIgnoreCase))
        {
            output = ContextTool.RunReferenceAwareActionable(
                index, graph, resolver, query: o.Query, tokenBudget, maxHops: o.Int("max-hops", 1),
                    entrySymbols, editedFiles, failingTest, stackTrace, semanticSeeds: null,
                    sourceSeeds: sourceSeeds,
                    readBody: symbol => ContextTool.ReadPivotBody(
                        readScope.Session,
                        ctx.WorkspaceRoot,
                        symbol),
                    referenceDepth: o.Int("reference-depth", 1), excludeTests: excludeTestsFlag, json,
                    readReferenceEvidence: symbol => ReferenceEvidenceReader.Read(
                        readScope.Session,
                        symbol.SymbolId,
                        new ReferenceEvidenceBounds(
                            ContextTool.ReferenceRowsPerSymbol,
                            ContextTool.ReferenceRowsPerSymbol)),
                    readOutgoingEvidence: symbol => ReferenceEvidenceReader.ReadOutgoing(
                        readScope.Session,
                        symbol.SymbolId,
                        new ReferenceEvidenceBounds(
                            ContextTool.ReferenceRowsPerSymbol,
                            ContextTool.ReferenceRowsPerSymbol)),
                readContentChunks: (symbols, excludeTests) => ReadContextContentChunks(
                    ctx,
                    symbols,
                    excludeTests),
                out selectedCount, out candidatesExamined);
        }
        else if (string.Equals(referenceMode, "off", StringComparison.OrdinalIgnoreCase))
        {
            output = ContextTool.RunActionable(
                index, graph, resolver, query: o.Query, tokenBudget, maxHops: o.Int("max-hops", 1),
                entrySymbols, editedFiles, failingTest, stackTrace, semanticSeeds: null,
                sourceSeeds: sourceSeeds,
                readBody: symbol => ContextTool.ReadPivotBody(
                    readScope.Session,
                    ctx.WorkspaceRoot,
                    symbol),
                readOutgoingMany: symbolIds => ReadContextOutgoingEvidence(readScope.Session, symbolIds),
                json, out selectedCount, out candidatesExamined);
        }
        else
        {
            err.WriteLine("reference-mode must be off or usage.");
            return 2;
        }
        if (selectedCount == 0)
        {
            ToolDiagnostic diagnostic = ContextTool.EmptyDiagnostic(
                o.Query,
                tokenBudget,
                candidatesExamined,
                entrySymbols,
                int.MaxValue);
            output = ToolDiagnosticRenderer.Attach("context", output, diagnostic, json);
        }
        // The CLI calls the pure tool cores directly, so the MCP wrapper's converging check never runs here;
        // both reference modes read reference evidence, so a symbols-level artifact silently drops usage
        // enrichment from a bundle that otherwise looks complete.
        else if (IndexLevelGuard.IsSymbolsLevel(readScope.Session.Snapshot.IndexLevel))
        {
            output = ToolDiagnosticRenderer.Attach(
                "context", output,
                IndexLevelGuard.Converging(
                    "the bundle carries no usage or outgoing-reference evidence pending identifier extraction."),
                json);
        }
        outw.WriteLine(output);
        }
        return 0;
    }

    /// <summary>
    /// Open the content corpus for context source-rescue seeds when present; fail soft to empty seeds.
    /// </summary>
    private static IReadOnlyList<ContextTool.ContextSourceSeed> LoadContextSourceRescueSeeds(
        ISymbolLookupIndex index,
        WorkspaceContext ctx,
        string query,
        bool excludeTests)
    {
        ContentCorpusReadLocation location = ContentCorpusReadLocator.Resolve(
            ctx.ExtractDbPath,
            ctx.CanonicalRoot ?? ctx.WorkspaceRoot,
            ctx.WorkspaceId);
        string contentDbPath = location.ContentDbPath;
        if (!File.Exists(contentDbPath))
            return ContextTool.LoadSourceRescueSeeds(index, contentIndex: null, query, excludeTests);

        try
        {
            FtsTextContentSearchIndex contentIndex;
            if (location.StoreSnapshot is { } snapshot)
            {
                contentIndex = ContentCorpusSidecar.OpenStoreGenerationChecked(location.StoreRoot!, snapshot);
            }
            else
            {
                using var freshness = new FreshnessReader(ctx.ExtractDbPath);
                contentIndex = new ContentCorpusSidecar().OpenRequired(
                    ctx.ExtractDbPath,
                    freshness.LatestRevision());
            }
            return ContextTool.LoadSourceRescueSeeds(index, contentIndex, query, excludeTests);
        }
        catch (Exception ex) when (
            ex is FileNotFoundException or InvalidOperationException or IOException
                or UnauthorizedAccessException or ArgumentException or NotSupportedException
                or Microsoft.Data.Sqlite.SqliteException)
        {
            return [];
        }
    }

    /// <summary>Outgoing evidence for context's term-rescue promotion set, in one batched read.</summary>
    /// <summary>
    /// The CLI mirror of <c>ContextTool.ReadOutgoingBatch</c>: one outgoing-only batch read, with an id the
    /// read session cannot resolve absent from the result rather than denying the whole promotion set.
    /// </summary>
    private static IReadOnlyDictionary<string, OutgoingReferenceEvidenceSet> ReadContextOutgoingEvidence(
        IWorkspaceReadSession session,
        IReadOnlyList<string> symbolIds) =>
        ReferenceEvidenceReader.ReadOutgoingMany(
            session,
            symbolIds,
            new ReferenceEvidenceQuery(
                new ReferenceEvidenceBounds(
                    ContextTool.ReferenceRowsPerSymbol,
                    ContextTool.ReferenceRowsPerSymbol)));

    private static IReadOnlyList<TextContentSearchHit> ReadContextContentChunks(
        WorkspaceContext context,
        IReadOnlyList<IndexedSymbol> symbols,
        bool excludeTests)
    {
        ContentCorpusReadLocation location = ContentCorpusReadLocator.Resolve(
            context.ExtractDbPath,
            context.CanonicalRoot ?? context.WorkspaceRoot,
            context.WorkspaceId);
        return location.StoreSnapshot is { } snapshot
            ? ContentCorpusContextReader.ReadContainingSymbolChunks(
                location.StoreRoot!,
                snapshot,
                symbols,
                excludeTests,
                ContextTool.ContentChunksPerSymbol)
            : ContentCorpusContextReader.ReadContainingSymbolChunks(
                location.ContentDbPath,
                context.ExtractDbPath,
                symbols,
                excludeTests,
                ContextTool.ContentChunksPerSymbol);
    }

    private static string[]? OptionValues(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

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

        if (!TryOpenCliReadScope(ctx, err, out CliReadScope readScope))
            return 3;

        using (readScope)
        {
            ISymbolLookupIndex index = readScope.SymbolIndex;
            ISymbolGraphReachability graph = readScope.Graph;
            var resolver = new SmartTargetResolver(index);
            string output = ImpactTool.Run(
                index, graph, resolver, target, changedPaths, diff,
                maxDepth: o.Int("max-depth", 2), limit: o.Int("limit", 100), json: o.Has("json"), out _, out _);
            // The CLI calls the pure tool core directly, so the MCP wrapper's converging check never runs
            // here; without this, a symbols-level artifact renders an authoritative-looking undercount.
            if (IndexLevelGuard.IsSymbolsLevel(readScope.Session.Snapshot.IndexLevel))
                output = ToolDiagnosticRenderer.Attach(
                    "impact", output,
                    IndexLevelGuard.Converging(
                        "call-site edges are missing, so the impacted-symbol set undercounts."),
                    o.Has("json"));
            outw.WriteLine(output);
        }
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
        if (!TryOpenCliReadScope(ctx, err, out CliReadScope? readScope))
            return 3;

        using (readScope)
        {
            ImpactRevisionDeltaSnapshot snapshot = ImpactTool.PrepareIndexRevisionDelta(
                workspaceId,
                ctx.WorkspaceRoot,
                readScope.Session,
                fromRevision,
                fromArtifactId);

            ISymbolLookupIndex? index = snapshot.Complete && snapshot.ChangedPaths.Count > 0
                ? readScope.SymbolIndex
                : null;
            ISymbolGraphReachability? graph = index is null ? null : readScope.Graph;

            string output = ImpactTool.RunIndexRevisionDelta(
                snapshot,
                index,
                graph,
                o.Int("max-depth", 2),
                o.Int("limit", 100),
                json,
                indexAvailable: index is not null);
            outw.WriteLine(output);
            return 0;
        }
    }

    private static int Trace(IReadOnlyList<string> args, WorkspaceContext ctx, TextWriter outw, TextWriter err)
    {
        CliOptions o = CliOptions.Parse(args, "full", "json", "no-definition");
        if (string.IsNullOrWhiteSpace(o.Query))
            return Usage(err, "miller trace <symbol> [--workspace-id SELECTOR] [--workspace DIR] [--scope FILE] [--mode refs|path|bridge] [--to SYMBOL] [--path-kind call|dependency] [--reference-kind KIND] [--no-definition] [--depth N] [--limit N] [--continuation TOKEN] [--full] [--json]");
        if (!TryResolveReadContext(ctx, o, err, out ctx))
            return 2;

        string mode = o.Value("mode", "refs")!;
        bool json = o.Has("json");
        string? referenceKind = o.Value("reference-kind", o.Value("kind"));
        bool includeDefinition = BoolOption(o, "include-definition", fallback: true) && !o.Has("no-definition");
        // The CLI calls the pure tool cores directly, so the MCP wrapper's converging check never runs
        // here; without this, a symbols-level artifact renders authoritative-looking empty trace output.
        bool referenceLayerConverging;
        try
        {
            if (string.Equals(mode, "bridge", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryOpenCliReadScope(ctx, err, out CliReadScope bridgeScope))
                    return 3;
                using (bridgeScope)
                {
                referenceLayerConverging = IndexLevelGuard.IsSymbolsLevel(bridgeScope.Session.Snapshot.IndexLevel);

                BridgeGraph bridgeGraph = SessionBridgeGraphLoader.Load(bridgeScope.Session);
                var bridgeResolver = new SmartTargetResolver(bridgeScope.SymbolIndex);
                string bridgeOutput = TraceTool.RunBridge(
                    bridgeScope.SymbolIndex,
                    bridgeGraph,
                    bridgeResolver,
                    target: o.Query,
                    scope: o.Value("scope"),
                    depth: o.Int("depth", 3),
                    limit: o.Int("limit", 20),
                    fullFormat: o.Has("full"),
                    json: json,
                    out _,
                    out _);
                if (referenceLayerConverging)
                    bridgeOutput = ToolDiagnosticRenderer.Attach(
                        "trace", bridgeOutput,
                        IndexLevelGuard.Converging(
                            "identifier-level usage sites are missing from refs/path/bridge results."),
                        json);
                outw.WriteLine(bridgeOutput);
                return 0;
                }
            }

            if (!TryOpenCliReadScope(ctx, err, out CliReadScope readScope))
                return 3;
            using (readScope)
            {
            ISymbolLookupIndex index = readScope.SymbolIndex;
            ISymbolGraphReachability graph = readScope.Graph;
            referenceLayerConverging = IndexLevelGuard.IsSymbolsLevel(readScope.Session.Snapshot.IndexLevel);

            var resolver = new SmartTargetResolver(index);
            string output = string.Equals(mode, "path", StringComparison.OrdinalIgnoreCase)
                ? TraceTool.RunGraph(
                    index,
                    graph,
                    resolver,
                    target: o.Query,
                    scope: o.Value("scope"),
                    mode: mode,
                    to: o.Value("to"),
                    depth: o.Int("depth", 3),
                    limit: o.Int("limit", 20),
                    fullFormat: o.Has("full"),
                    json: json,
                    pathKind: o.Value("path-kind", "call")!,
                    out _,
                    out _)
                : TraceTool.RunGraph(
                    index, graph, resolver, target: o.Query, scope: o.Value("scope"), mode: mode, to: o.Value("to"),
                    depth: o.Int("depth", 3), limit: o.Int("limit", 20), fullFormat: o.Has("full"), json: json,
                    referenceKind, includeDefinition,
                    (symbol, query) => ReferenceEvidenceReader.Read(readScope.Session, symbol.SymbolId, query),
                    ctx.WorkspaceId ?? "current",
                    string.Equals(mode, "refs", StringComparison.OrdinalIgnoreCase)
                        ? ReferenceEvidenceReader.ReadSnapshot(readScope.Session)
                        : null,
                    o.Value("continuation"),
                    out _,
                    out _);
            if (referenceLayerConverging)
                output = ToolDiagnosticRenderer.Attach(
                    "trace", output,
                    IndexLevelGuard.Converging(
                        "identifier-level usage sites are missing from refs/path/bridge results."),
                    json);
            outw.WriteLine(output);
            return 0;
            }
        }
        catch (ToolDiagnosticException ex) when (
            ex.Diagnostic.Class is ToolDiagnosticClass.Refusal or ToolDiagnosticClass.Unsupported)
        {
            string rendered = ToolDiagnosticRenderer.Render("trace", ex.Diagnostic, json);
            if (json)
                outw.WriteLine(rendered);
            else
                err.WriteLine(rendered);
            return 2;
        }
    }

    // ---------- workspace verb ----------

    private static int Workspace(IReadOnlyList<string> args, WorkspaceContext ctx, TextWriter outw, TextWriter err)
    {
        CliOptions o = CliOptions.Parse(args, "json", "full", "markdown");
        string operation = (o.Query.Length > 0 ? o.Query : "status").ToLowerInvariant();
        bool json = o.Has("json");
        WorkspaceHealthFormat healthFormat = json
            ? WorkspaceHealthFormat.Json
            : o.Has("markdown")
                ? WorkspaceHealthFormat.Markdown
                : WorkspaceHealthFormat.Compact;

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
                return WorkspaceHealth(ctx, id, path, healthFormat, outw, err);
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
            case "levels":
                return WorkspaceLevels(ctx, id, path, set: o.Value("set"), clear: o.Has("clear"), json, outw, err);
            case "prune":
                if (id is not null || path is not null)
                {
                    err.WriteLine(
                        "workspace prune is registry-wide and takes no selector; it removes every row whose " +
                        "root is missing. Drop --id/--path (use `workspace remove` for a single workspace).");
                    return 2;
                }
                return WorkspacePrune(ctx, json, dryRun: o.Has("dry-run"), outw);
            default:
                err.WriteLine($"unknown workspace operation '{operation}'. Use status|health|onboarding|leader|list|refresh|full|open|remove|levels|prune.");
                return 2;
        }
    }

    private static int Tests(
        IReadOnlyList<string> args,
        WorkspaceContext ctx,
        TextWriter outw,
        TextWriter err,
        TestsCoreHooks? testsHooks)
    {
        CliOptions o = CliOptions.Parse(args, "json", "wait");
        if (o.Positionals.Count > 1)
            return Usage(err, TestsCore.Usage);

        string operation = o.Positionals.Count == 0 ? "status" : o.Positionals[0].ToLowerInvariant();
        if (operation is "help" or "-h" || o.Has("help"))
        {
            outw.WriteLine(TestsHelpText);
            return 0;
        }

        foreach (string flag in new[] { "workspace-id", "workspace", "project" })
        {
            if (o.Has(flag) && string.IsNullOrWhiteSpace(o.Value(flag)))
            {
                err.WriteLine($"--{flag} requires a value.");
                return 2;
            }
        }

        if (!TryResolveTestsWorkspace(ctx, o, err, out string root))
            return 2;

        var request = new TestsCoreRequest(
            WorkspaceRoot: root,
            MillerHome: MillerHomeFor(ctx),
            KillSwitch: Environment.GetEnvironmentVariable(CtEnvironment.KillSwitch),
            MillerVersion: MillerVersion.Current,
            Hooks: testsHooks,
            Json: o.Has("json"),
            Wait: o.Has("wait"),
            ProjectPath: o.Value("project"));

        switch (operation)
        {
            case "status":
                outw.WriteLine(TestsCore.Status(request).Render(request.Json));
                return 0;
            case "failures":
                outw.WriteLine(TestsCore.Failures(
                        request,
                        o.Int("limit", TestsCore.FailuresDefaultLimit),
                        o.Int("offset", 0))
                    .Render(request.Json));
                return 0;
            case "serve":
            {
                TestsServeResult result = TestsCore.Start(request);
                if (result.ExitCode != 0 && result.Reason is not null)
                    err.WriteLine(result.Reason);
                outw.WriteLine(result.Render(request.Json));
                return result.ExitCode;
            }
            case "run":
            {
                TestsRunResult result = TestsCore.Run(request);
                if (result.ExitCode != 0 && result.Reason is not null && !request.Json)
                    err.WriteLine(result.Reason);
                outw.WriteLine(result.Render(request.Json));
                return result.ExitCode;
            }
            case "enable":
            {
                TestsMutationResult result = TestsCore.Enable(request);
                if (result.ExitCode != 0 && result.Error is not null)
                    err.WriteLine(result.Error);
                if (result.ExitCode == 0 || request.Json)
                    outw.WriteLine(result.Render(request.Json));
                return result.ExitCode;
            }
            case "disable":
            {
                TestsMutationResult result = TestsCore.Disable(request);
                if (result.ExitCode != 0 && result.Error is not null)
                    err.WriteLine(result.Error);
                if (result.ExitCode == 0 || request.Json)
                    outw.WriteLine(result.Render(request.Json));
                return result.ExitCode;
            }
            case "stop":
            {
                TestsStopResult result = TestsCore.Stop(request);
                if (result.ExitCode != 0 && result.Reason is not null)
                    err.WriteLine(result.Reason);
                outw.WriteLine(result.Render(request.Json));
                return result.ExitCode;
            }
            default:
                err.WriteLine($"unknown tests operation '{operation}'.");
                return Usage(err, TestsCore.Usage);
        }
    }

    private static int TestsDaemon(IReadOnlyList<string> args, WorkspaceContext ctx, TextWriter outw, TextWriter err)
    {
        _ = args;
        _ = err;
        string root = CtEnvironment.ResolveDaemonWorkspaceRoot(
            ctx.CanonicalRoot ?? ctx.WorkspaceRoot,
            Environment.GetEnvironmentVariable)!;
        TestsServeResult result = TestsCore.ServeHost(new TestsCoreRequest(
            WorkspaceRoot: root,
            MillerHome: MillerHomeFor(ctx),
            KillSwitch: Environment.GetEnvironmentVariable(CtEnvironment.KillSwitch),
            MillerVersion: MillerVersion.Current,
            Json: true));
        outw.WriteLine(result.Render(json: true));
        return result.ExitCode;
    }

    private static bool TryResolveTestsWorkspace(
        WorkspaceContext ctx, CliOptions o, TextWriter err, out string root)
    {
        string? id = o.Value("workspace-id");
        string? workspaceDir = o.Value("workspace");
        if (!string.IsNullOrWhiteSpace(workspaceDir))
        {
            root = Path.GetFullPath(workspaceDir, ctx.WorkspaceRoot);
            return true;
        }

        if (!string.IsNullOrWhiteSpace(id))
        {
            using WorkspaceRegistry registry = WorkspaceRegistry.Open(ctx.RegistryDbPath);
            try
            {
                WorkspaceRegistryRow row = WorkspaceRegistrySelector.Resolve(registry, id);
                root = row.CanonicalRoot;
                return true;
            }
            catch (KeyNotFoundException ex)
            {
                err.WriteLine(ex.Message);
                root = ctx.WorkspaceRoot;
                return false;
            }
        }

        root = ctx.CanonicalRoot ?? ctx.WorkspaceRoot;
        return true;
    }

    private static int WorkspaceLevels(
        WorkspaceContext ctx, string? id, string? path, string? set, bool clear, bool json,
        TextWriter outw, TextWriter err)
    {
        if (set is not null && clear)
        {
            err.WriteLine("--set and --clear are mutually exclusive.");
            return 2;
        }

        string? normalizedSet = null;
        if (set is not null)
        {
            normalizedSet = set.Trim().ToLowerInvariant();
            if (normalizedSet is not ("progressive" or "full" or "symbols-only"))
            {
                err.WriteLine($"unknown level policy '{set}'. Use progressive|full|symbols-only.");
                return 2;
            }
        }

        using WorkspaceRegistry registry = WorkspaceRegistry.Open(ctx.RegistryDbPath);
        WorkspaceRegistryRow? row;
        if (!string.IsNullOrWhiteSpace(id) || !string.IsNullOrWhiteSpace(path))
        {
            try
            {
                row = WorkspaceRegistrySelector.Resolve(registry, (id ?? path)!);
            }
            catch (KeyNotFoundException ex)
            {
                err.WriteLine(ex.Message);
                return 2;
            }
        }
        else
        {
            row = FindCurrentWorkspaceRow(registry, ctx);
        }

        if ((normalizedSet is not null || clear) && row is null)
        {
            err.WriteLine(
                "this workspace is not registered yet, so there is no row to store the policy on; run " +
                "`miller workspace open --path <dir>` first (or select a registered one with --id/--path).");
            return 2;
        }

        string? changed = null;
        if (normalizedSet is not null)
        {
            row = registry.SetLevelPolicy(row!.WorkspaceId, normalizedSet);
            changed = "set";
        }
        else if (clear)
        {
            row = registry.SetLevelPolicy(row!.WorkspaceId, null);
            changed = "cleared";
        }

        string? envRaw = Environment.GetEnvironmentVariable(IndexLevels.EnvVar);
        bool inPlace = Environment.GetEnvironmentVariable("MILLER_FULL_REBUILD_INPLACE") == "1";
        string? registryPolicy = row?.LevelPolicy;
        IndexLevelPolicy effective = IndexLevels.Resolve(envRaw, inPlace ? "1" : null, registryPolicy);
        string source = inPlace ? "forced by MILLER_FULL_REBUILD_INPLACE=1"
            : !string.IsNullOrWhiteSpace(envRaw) ? $"env {IndexLevels.EnvVar}"
            : !string.IsNullOrWhiteSpace(registryPolicy) ? "registry"
            : "default";

        string dbPath = row?.IndexDbPath ?? ctx.ExtractDbPath;
        string workspaceRoot = row?.CanonicalRoot ?? ctx.CanonicalRoot ?? ctx.WorkspaceRoot;
        string? workspaceId = row?.WorkspaceId ?? ctx.WorkspaceId;
        string? indexLevel = null;
        try
        {
            using WorkspaceReadHandle readSession = WorkspaceReadSessionFactory.Open(dbPath, workspaceRoot, workspaceId);
            indexLevel = readSession.Snapshot.IndexLevel;
        }
        catch (Exception ex) when (
            ex is FileNotFoundException or DirectoryNotFoundException or IOException
                or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            indexLevel = null;
        }
        bool upgradeOwed = indexLevel is not null && IndexLevels.UpgradeOwed(indexLevel, effective);

        outw.WriteLine(WorkspaceRender.Levels(
            new WorkspaceLevelsResult(
                row?.WorkspaceId,
                row?.DisplayId,
                row?.CanonicalRoot ?? ctx.WorkspaceRoot,
                IndexLevels.StorageValue(effective),
                source,
                registryPolicy,
                indexLevel,
                upgradeOwed,
                changed),
            json));
        return 0;
    }

    private static int WorkspaceList(
        WorkspaceContext ctx, bool json, TextWriter outw, string? filter = null, int? limit = null)
    {
        using WorkspaceRegistry registry = WorkspaceRegistry.Open(ctx.RegistryDbPath);
        IReadOnlyList<WorkspaceRegistryRow> rows = registry.List();
        WorkspaceRegistryRow? currentRow = FindCurrentWorkspaceRow(registry, ctx);
        int? activeLimit = limit ?? (json ? null : WorkspaceRender.DefaultListLimit);
        WorkspaceListFacts facts = WorkspaceFactsAssembler.ToListFacts(
            rows,
            row => currentRow is not null
                ? string.Equals(row.WorkspaceId, currentRow.WorkspaceId, StringComparison.Ordinal)
                : WorkspaceSafety.IsLiveWorkspace(row.CanonicalRoot, ctx.WorkspaceRoot),
            filter,
            activeLimit);
        outw.WriteLine(WorkspaceRender.List(facts, json));
        return 0;
    }

    // CLI mirror of the MCP lifecycle hook (WorkspaceTool.ReapStagingOrphans): the sidecar writers reap staging
    // orphans only as a build STARTS, so a workspace whose scans never reach them needs a lifecycle path to
    // reclaim the disk. `miller workspace status` is the likeliest way a person pokes at a stuck workspace.
    private static void ReapStagingOrphans(string? indexDbPath)
    {
        SidecarStagingReaper.ReapWorkspaceStaging(
            string.IsNullOrWhiteSpace(indexDbPath) ? null : Path.GetDirectoryName(indexDbPath),
            SidecarStagingReaper.DefaultStaleAge);
    }

    private static int WorkspaceStatus(
        WorkspaceContext ctx, string? id, string? path, bool json, TextWriter outw, TextWriter err)
    {
        using WorkspaceRegistry registry = WorkspaceRegistry.Open(ctx.RegistryDbPath);
        SymbolSearchSidecar sidecar = SymbolSearchSidecar.FromEnvironment();
        var contentSidecar = new ContentCorpusSidecar();
        VectorSidecar vectors = VectorSidecar.FromEnvironment();
        SemanticBrokerFacts semanticBroker = CliSemanticBrokerFacts(ctx, vectors.Mode);

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
            ReapStagingOrphans(row.IndexDbPath);
            WorkspaceFacts selectedFacts = WorkspaceFactsAssembler.FromRegisteredRow(
                    registry,
                    row,
                    WorkspaceRegisteredFactsProfile.CliStatus,
                    sidecar,
                    contentSidecar,
                    vectors,
                    semanticBroker,
                    CliScanGovernor(ctx));
            outw.WriteLine(WorkspaceRender.Status(
                selectedFacts,
                TelemetrySummary.Empty,
                json,
                CliLeaderFacts(selectedFacts.DbPath)));
            return 0;
        }

        // Default: the current workspace. Enrich from its registry row when present, else read the local db.
        // Reap ahead of the RequireIndex bail below: a workspace with no artifact at all is exactly the leak case.
        ReapStagingOrphans(ctx.ExtractDbPath);
        WorkspaceRegistryRow? currentRow = FindCurrentWorkspaceRow(registry, ctx);
        if (currentRow is not null)
        {
            WorkspaceFacts currentFacts = WorkspaceFactsAssembler.FromRegisteredRow(
                    registry,
                    currentRow,
                    WorkspaceRegisteredFactsProfile.CliStatus,
                    sidecar,
                    contentSidecar,
                    vectors,
                    semanticBroker,
                    CliScanGovernor(ctx));
            outw.WriteLine(WorkspaceRender.Status(
                currentFacts,
                TelemetrySummary.Empty,
                json,
                CliLeaderFacts(currentFacts.DbPath)));
            return 0;
        }

        if (!RequireIndex(ctx, err))
            return 3;
        using WorkspaceReadHandle readSession = WorkspaceReadSessionFactory.Open(
            ctx.ExtractDbPath,
            ctx.CanonicalRoot ?? ctx.WorkspaceRoot,
            ctx.WorkspaceId);
        WorkspaceIndexFacts indexFacts = WorkspaceIndexFactsReader.ReadSession(readSession);
        WorkspaceFacts facts = WorkspaceFactsAssembler.FromUnregisteredLocal(
            ctx,
            indexFacts,
            sidecar,
            contentSidecar,
            vectors,
            semanticBroker,
            CliScanGovernor(ctx));
        outw.WriteLine(WorkspaceRender.Status(
            facts,
            TelemetrySummary.Empty,
            json,
            CliLeaderFacts(facts.DbPath)));
        return 0;
    }

    private static int WorkspaceHealth(
        WorkspaceContext ctx,
        string? id,
        string? path,
        WorkspaceHealthFormat format,
        TextWriter outw,
        TextWriter err)
    {
        using WorkspaceRegistry registry = WorkspaceRegistry.Open(ctx.RegistryDbPath);
        SymbolSearchSidecar sidecar = SymbolSearchSidecar.FromEnvironment();
        var contentSidecar = new ContentCorpusSidecar();
        VectorSidecar vectors = VectorSidecar.FromEnvironment();
        SemanticBrokerFacts semanticBroker = CliSemanticBrokerFacts(ctx, vectors.Mode);

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

            ReapStagingOrphans(row.IndexDbPath);
            WorkspaceFacts facts = WorkspaceFactsAssembler.FromRegisteredRow(
                registry,
                row,
                WorkspaceRegisteredFactsProfile.CliHealth,
                sidecar,
                contentSidecar,
                vectors,
                semanticBroker,
                CliScanGovernor(ctx));
            WorkspaceExtractionHealthFacts extraction = ReadHealthOrUnavailable(row, facts.WarningText);
            outw.WriteLine(WorkspaceRender.Health(
                WorkspaceHealthFacts.Create(
                    facts, TelemetrySummary.Empty, new TelemetryHealthFacts(0, 0, 0), extraction,
                    CliLeaderFacts(row.IndexDbPath),
                    CliHistoryStatus(row.IndexDbPath)),
                format));
            return 0;
        }

        // Reap ahead of the RequireIndex bail below: a workspace with no artifact at all is exactly the leak case.
        ReapStagingOrphans(ctx.ExtractDbPath);
        WorkspaceRegistryRow? currentRow = FindCurrentWorkspaceRow(registry, ctx);
        if (currentRow is not null)
        {
            WorkspaceFacts facts = WorkspaceFactsAssembler.FromRegisteredRow(
                registry,
                currentRow,
                WorkspaceRegisteredFactsProfile.CliHealth,
                sidecar,
                contentSidecar,
                vectors,
                semanticBroker,
                CliScanGovernor(ctx));
            WorkspaceExtractionHealthFacts extraction = ReadHealthOrUnavailable(currentRow, facts.WarningText);
            outw.WriteLine(WorkspaceRender.Health(
                WorkspaceHealthFacts.Create(
                    facts, TelemetrySummary.Empty, new TelemetryHealthFacts(0, 0, 0), extraction,
                    CliLeaderFacts(currentRow.IndexDbPath),
                    CliHistoryStatus(currentRow.IndexDbPath)),
                format));
            return 0;
        }

        if (!RequireIndex(ctx, err))
            return 3;
        using WorkspaceReadHandle readSession = WorkspaceReadSessionFactory.Open(
            ctx.ExtractDbPath,
            ctx.CanonicalRoot ?? ctx.WorkspaceRoot,
            ctx.WorkspaceId);
        WorkspaceIndexFacts indexFacts = WorkspaceIndexFactsReader.ReadSession(readSession);
        WorkspaceFacts localFacts = WorkspaceFactsAssembler.FromUnregisteredLocal(
            ctx,
            indexFacts,
            sidecar,
            contentSidecar,
            vectors,
            semanticBroker,
            CliScanGovernor(ctx));
        outw.WriteLine(WorkspaceRender.Health(
            WorkspaceHealthFacts.Create(
                localFacts,
                TelemetrySummary.Empty,
                new TelemetryHealthFacts(0, 0, 0),
                WorkspaceHealthReader.Read(readSession),
                CliLeaderFacts(ctx.ExtractDbPath),
                CliHistoryStatus(ctx.ExtractDbPath)),
            format));
        return 0;
    }

    // Same best-effort history-sidecar status the MCP health surface reports (WorkspaceTool.ReadHistoryStatus);
    // never throws — absent/unreadable degrades to a status the render can show.
    private static MetricHistoryStatus CliHistoryStatus(string indexDbPath) =>
        MetricHistoryStore.ReadStatus(MetricSnapshotAggregates.HistoryDbPathFor(indexDbPath));

    private static SemanticBrokerFacts CliSemanticBrokerFacts(
        WorkspaceContext context,
        SemanticMode mode)
    {
        if (mode is SemanticMode.Off)
            return SemanticBrokerFacts.From(mode, null);

        var factory = new SharedSemanticBrokerConnectionFactory(
            context.ToolsRoot,
            MillerHomeFor(context),
            SemanticEncoderSelection.Active);
        try
        {
            SemanticBrokerSnapshot? snapshot = factory
                .ObserveExistingAsync(TimeSpan.FromMilliseconds(500))
                .AsTask()
                .GetAwaiter()
                .GetResult();
            return SemanticBrokerFacts.From(mode, snapshot);
        }
        finally
        {
            factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
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

        using WorkspaceReadHandle readSession = WorkspaceReadSessionFactory.Open(
            ctx.ExtractDbPath,
            ctx.CanonicalRoot ?? ctx.WorkspaceRoot,
            ctx.WorkspaceId);
        WorkspaceIndexFacts indexFacts = WorkspaceIndexFactsReader.ReadSession(readSession);
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
                WorkspaceOnboardingAssembler.CreateFromWorkspace(
                    facts,
                    ctx.TelemetryDbPath,
                    row.WorkspaceId,
                    row.CanonicalRoot,
                    row.IndexDbPath,
                    storeEnabled: facts.Store is not null),
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
                WorkspaceOnboardingAssembler.CreateFromWorkspace(
                    facts,
                    ctx.TelemetryDbPath,
                    currentRow.WorkspaceId,
                    currentRow.CanonicalRoot,
                    currentRow.IndexDbPath,
                    storeEnabled: facts.Store is not null),
                json));
            return 0;
        }

        if (!RequireIndex(ctx, err))
            return 3;

        using WorkspaceReadHandle readSession = WorkspaceReadSessionFactory.Open(
            ctx.ExtractDbPath,
            ctx.CanonicalRoot ?? ctx.WorkspaceRoot,
            ctx.WorkspaceId);
        WorkspaceIndexFacts indexFacts = WorkspaceIndexFactsReader.ReadSession(readSession);
        WorkspaceFacts localFacts = WorkspaceFactsAssembler.FromUnregisteredLocal(
            ctx,
            indexFacts,
            sidecar,
            contentSidecar);
        outw.WriteLine(WorkspaceRender.Onboarding(
            WorkspaceOnboardingAssembler.CreateFromWorkspace(
                localFacts,
                ctx.TelemetryDbPath,
                ctx.WorkspaceId,
                localFacts.Root,
                ctx.ExtractDbPath,
                storeEnabled: localFacts.Store is not null),
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
            ArtifactExtractorVersion = WorkspaceReadSessionFactory.StoreEnabledFromEnvironment()
                ? StoreArtifactVersionReader.TryReadOrFallback(indexDbPath, ExtractBinaryVersionReader.TryRead)
                : ExtractBinaryVersionReader.TryRead(indexDbPath),
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

    private static WorkspaceExtractionHealthFacts ReadHealthOrUnavailable(WorkspaceRegistryRow row, string? error)
    {
        try
        {
            using WorkspaceReadHandle session = WorkspaceReadSessionFactory.Open(
                row.IndexDbPath,
                row.CanonicalRoot,
                row.WorkspaceId);
            return WorkspaceHealthReader.Read(session);
        }
        catch (Exception ex) when (
            ex is FileNotFoundException or DirectoryNotFoundException or IOException
                || IsHealthIndexReadException(ex))
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
        var refresh = new CrossWorkspaceRefreshService(registry, runner, sidecar, CliScanGovernor(ctx));
        WorkspaceRefreshResult result = refresh.Refresh(row.WorkspaceId, force, bypassBackoff: true);

        bool currentWorkspace = WorkspaceSafety.IsLiveWorkspace(
            row.CanonicalRoot,
            CurrentRootForRegistrySelection(ctx));
        string? vectorNote = VectorRefreshNote(SemanticActivation.FromEnvironment(), currentWorkspace);
        var action = WorkspaceRefreshAction(result, force, sidecar, registry, vectorNote);
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
        WorkspaceRegistry? registry = null,
        string? vectorNote = null)
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

        var refreshSidecars = OpenRefreshSidecarFacts(result, sidecar, revision);

        return new WorkspaceActionResult(
            Operation: force ? "full" : "refresh",
            Scanned: result.Scanned,
            Swapped: false,
            Revision: revision,
            Note: JoinNotes(result.Error ?? result.WarningText, vectorNote),
            WorkspaceId: result.WorkspaceId,
            Root: result.WorkspaceRoot,
            Status: result.StatusText,
            IndexFresh: indexFresh,
            SearchSidecar: refreshSidecars.Search,
            ContentCorpus: refreshSidecars.Content,
            ScanDurationMs: (long?)result.ScanDuration?.TotalMilliseconds,
            DurationMs: (long?)result.TotalDuration?.TotalMilliseconds,
            ArtifactId: ArtifactIdForAction(result, registry),
            Sidecars: result.Sidecars);
    }

    /// <summary>
    /// Opens the refreshed workspace's read session so the reported sidecars are the ones it actually
    /// serves. Any read failure falls back to the artifact-relative paths rather than failing the refresh:
    /// the sidecar block is reporting, not the result.
    /// </summary>
    private static (SearchSidecarFacts Search, ContentCorpusFacts Content) OpenRefreshSidecarFacts(
        WorkspaceRefreshResult result,
        SymbolSearchSidecar sidecar,
        long revision)
    {
        var contentSidecar = new ContentCorpusSidecar();
        try
        {
            using WorkspaceReadHandle session = WorkspaceReadSessionFactory.Open(
                result.IndexDbPath,
                result.WorkspaceRoot,
                result.WorkspaceId);
            return RefreshSidecarFacts(
                session.FamilyStoreRoot,
                session.Snapshot,
                result.IndexDbPath,
                revision,
                sidecar,
                contentSidecar);
        }
        catch (Exception ex) when (
            ex is FamilyStoreReadException or SqliteException or InvalidOperationException
                or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return RefreshSidecarFacts(null, null, result.IndexDbPath, revision, sidecar, contentSidecar);
        }
    }

    /// <summary>
    /// Sidecar facts for a refresh response. A family-store workspace keeps its search and content sidecars
    /// under the store's <c>sidecars/</c> directory, NOT under <c>&lt;workspace&gt;/.miller/</c>. Reading the
    /// workspace path there reported two present, healthy sidecars as <c>missing</c> at paths holding no such
    /// file, which is what every Eros reader of <c>refresh --json</c> saw
    /// (<c>docs/contracts/cli-eros-v1.md</c>). A legacy workspace keeps the artifact-relative paths.
    /// </summary>
    internal static (SearchSidecarFacts Search, ContentCorpusFacts Content) RefreshSidecarFacts(
        string? familyStoreRoot,
        WorkspaceReadSnapshot? snapshot,
        string indexDbPath,
        long revision,
        SymbolSearchSidecar sidecar,
        ContentCorpusSidecar contentSidecar)
    {
        ArgumentNullException.ThrowIfNull(sidecar);
        ArgumentNullException.ThrowIfNull(contentSidecar);

        if (snapshot?.Mode == WorkspaceReadMode.FamilyStore && !string.IsNullOrWhiteSpace(familyStoreRoot))
        {
            return (
                sidecar.InspectStore(familyStoreRoot, snapshot),
                contentSidecar.InspectStore(familyStoreRoot, snapshot));
        }

        return (
            sidecar.Inspect(indexDbPath, revision),
            contentSidecar.Inspect(indexDbPath, revision));
    }

    private static string? VectorRefreshNote(SemanticMode mode, bool currentWorkspace)
    {
        if (mode is SemanticMode.Off)
            return null;

        return currentWorkspace
            ? "vector convergence requires a resident Miller leader; this one-shot CLI refresh does not generate embeddings"
            : "foreign workspace refresh never generates embeddings; run a resident Miller leader in that workspace to converge vectors";
    }

    private static string? JoinNotes(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first))
            return second;
        if (string.IsNullOrWhiteSpace(second))
            return first;
        return first + "; " + second;
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

        // Reap ahead of the julie-extract locate below: a machine with no restored extractor never builds a
        // sidecar here, which is precisely the workspace that would otherwise keep its orphans forever.
        ReapStagingOrphans(dbPath);

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
        // `Refreshing` is written only for a row this open CREATES: it advertises "an index is being built", which
        // is a lie about an ALREADY-registered workspace and would demote a healthy `ready` row. An existing row
        // keeps its own state until the refresh below moves it (MarkScanned ⇒ ready, MarkError ⇒ error,
        // MarkMissing ⇒ missing), and the statuses that deliberately leave it untouched are reconciled after.
        WorkspaceRegistryRow? priorRow = registry.Get(id);
        registry.UpsertSeen(
            id, display, canonicalRoot, dbPath, priorRow?.State ?? WorkspaceRegistryState.Refreshing);

        var refresh = new CrossWorkspaceRefreshService(registry, runner, sidecar, CliScanGovernor(ctx));
        WorkspaceRefreshResult result = refresh.Refresh(id, force: full, bypassBackoff: true);
        ReconcileOpenedRegistryRow(registry, id, dbPath, isNewRow: priorRow is null, result);

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

    // Give a row this open CREATED a real state when the refresh deliberately left it alone. `lock_busy` (a live
    // Miller owns the workspace) and `ineligible_extractor` (the index is valid for whoever built it with a NEWER
    // binary) are not workspace errors, so the refresh does not write them — which would strand a brand-new row at
    // `refreshing`, a state nothing sweeps and that drops the workspace out of every fleet consumer's
    // Current|Ready|LoadedExisting filter. A row that ALREADY existed needs nothing here: it kept its own state
    // through the upsert above, and demoting a healthy `ready` workspace because this open could not scan it would
    // break a workspace the open was only meant to register.
    private static void ReconcileOpenedRegistryRow(
        WorkspaceRegistry registry,
        string workspaceId,
        string dbPath,
        bool isNewRow,
        WorkspaceRefreshResult result)
    {
        if (!isNewRow)
            return;
        if (result.Status is not (WorkspaceRefreshStatus.LockBusy or WorkspaceRefreshStatus.IneligibleExtractor))
            return;

        if (File.Exists(dbPath))
            registry.MarkLoadedExisting(workspaceId, result.Revision ?? 0);
        else
            registry.MarkError(
                workspaceId,
                result.Error ?? result.WarningText
                    ?? "workspace open did not produce an index and nothing is scanning this workspace.");
    }

    // ---------- prune (registry GC for gone roots) ----------

    // Remove registry rows whose canonical_root no longer exists. Never prunes the current workspace row (guarded
    // by workspace_id). Does not open symbols.db. A real prune also runs julie-extract's family-store
    // maintenance, which is the only thing that reclaims the coordinator's terminal request rows.
    private static int WorkspacePrune(
        WorkspaceContext ctx, bool json, bool dryRun, TextWriter outw)
    {
        using WorkspaceRegistry registry = WorkspaceRegistry.Open(ctx.RegistryDbPath);
        WorkspaceRegistryRow? currentRow = FindCurrentWorkspaceRow(registry, ctx);
        WorkspaceRegistryPrune.Result result = WorkspaceRegistryPrune.Run(
            registry,
            currentRow?.WorkspaceId,
            dryRun,
            maintainStore: StoreMaintenanceRunner.ForToolsRoot(ctx.ToolsRoot));
        var rendered = new WorkspacePruneResult(
            result.DryRun,
            result.Pruned.Select(e => new WorkspacePruneEntry(e.WorkspaceId, e.DisplayId, e.Root)).ToArray(),
            result.Kept,
            result.SidecarReclaim,
            result.StoreMaintenance);
        outw.WriteLine(WorkspaceRender.Prune(rendered, json));
        return 0;
    }

    // ---------- remove (delete a workspace's .miller index dir) ----------

    // Delete a workspace's `.miller` index dir + unregister it. The removal semantics (gone-root prune, in-use
    // lock refusal, lease co-holding) live in the shared WorkspaceRemoval core; this verb only parses the
    // selector, renders the result, and maps the exit code. liveRoot is null — the one-shot CLI serves nothing
    // in-process, so the cross-process single-writer lock is the only guard against deleting a dir a running
    // Miller owns. Requires an explicit selector (--id or --path); there is no current-dir default (deleting the
    // dir you stand in by accident is a foot-gun).
    private static int WorkspaceRemove(
        WorkspaceContext ctx, string? id, string? path, bool json, TextWriter outw, TextWriter err)
    {
        if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(path))
        {
            err.WriteLine("workspace remove requires a selector: --id <display-id> or --path <dir>.");
            return 2;
        }

        using WorkspaceRegistry registry = WorkspaceRegistry.Open(ctx.RegistryDbPath);

        WorkspaceRemoveResult result;
        if (!string.IsNullOrWhiteSpace(id))
        {
            try
            {
                result = WorkspaceRemoval.RemoveById(
                    registry,
                    id!,
                    liveRoot: null,
                    protectedMillerDir: Path.GetDirectoryName(ctx.RegistryDbPath));
            }
            catch (KeyNotFoundException ex)
            {
                err.WriteLine(ex.Message);
                return 2;
            }
        }
        else
        {
            result = WorkspaceRemoval.RemoveByPath(
                registry,
                path!,
                liveRoot: null,
                protectedMillerDir: Path.GetDirectoryName(ctx.RegistryDbPath));
        }

        outw.WriteLine(WorkspaceRender.Remove(result, json));
        return RemoveExitCode(result.Result);
    }

    // Map a refresh/full outcome to a process exit code (cli-eros-v1: exit 0 = ingestable payload, exit 3 =
    // genuinely unusable index). LockBusy is exit 0 because the latest readable DB IS being served — the payload
    // says so (`status: lock_busy`, `index_fresh: false`), so a consumer that needs CONFIRMED freshness must gate
    // on those fields, not the exit code (2026-06-11 Eros ask; previously 3, which forced Eros to parse exit-3
    // stdout against the "non-zero = non-ingestable" rule). Exit 0 does NOT promise a live converger: a busy
    // WRITER lock means a leader owns convergence, while a busy machine-wide scan governor means nobody is
    // scanning right now, and a governor refusal with no readable index reports MissingIndex instead so this
    // code can never advertise a Ready workspace with no symbols.db. A missing root/index, a hard failure, or an
    // ineligible extractor stay operational failures (3); any future status is unexpected (1).
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

    // Removed and NotFound are successful command outcomes; NotFound is an idempotent no-op and may leave an
    // unregistered .miller directory untouched. Every refusal preserves the registry row and index data.
    internal static int RemoveExitCode(WorkspaceRemoveResult.Outcome outcome) => outcome switch
    {
        WorkspaceRemoveResult.Outcome.Removed or WorkspaceRemoveResult.Outcome.NotFound => 0,
        WorkspaceRemoveResult.Outcome.RefusedInUse
            or WorkspaceRemoveResult.Outcome.RefusedLive
            or WorkspaceRemoveResult.Outcome.RefusedSensitive
            or WorkspaceRemoveResult.Outcome.RefusedInvalidRegistration => 3,
        _ => 1,
    };

    // ---------- helpers ----------

    private sealed class CliReadScope : IDisposable
    {
        public CliReadScope(
            WorkspaceReadHandle session,
            ISymbolLookupIndex symbolIndex,
            ISymbolGraphReachability graph,
            IDisposable? graphOwner)
        {
            Session = session;
            SymbolIndex = symbolIndex;
            Graph = graph;
            _graphOwner = graphOwner;
        }

        private readonly IDisposable? _graphOwner;

        public WorkspaceReadHandle Session { get; }
        public ISymbolLookupIndex SymbolIndex { get; }
        public ISymbolGraphReachability Graph { get; }
        public bool IsFamilyStore => Session.Snapshot.Mode == WorkspaceReadMode.FamilyStore;

        public FtsTextContentSearchIndex OpenContentIndex()
        {
            if (IsFamilyStore)
            {
                return ContentCorpusSidecar.OpenStoreGenerationChecked(
                    Session.FamilyStoreRoot
                        ?? throw new InvalidOperationException(
                            "The family-store read session has no family root."),
                    Session.Snapshot);
            }

            return new ContentCorpusSidecar().OpenRequired(
                Session.LegacyArtifactPath
                    ?? throw new InvalidOperationException(
                        "The legacy read session has no artifact path."),
                Session.Snapshot.Freshness.Revision);
        }

        public FtsRegionSearchIndex OpenRegionIndex()
        {
            if (IsFamilyStore)
            {
                return FtsRegionSearchIndex.OpenStore(
                    Session.FamilyStoreRoot
                        ?? throw new InvalidOperationException(
                            "The family-store read session has no family root."),
                    Session.Snapshot);
            }

            string databasePath = Session.LegacyArtifactPath
                ?? throw new InvalidOperationException("The legacy read session has no artifact path.");
            using var freshness = new FreshnessReader(databasePath);
            return FtsRegionSearchIndex.Open(
                SymbolSearchSidecar.SearchDbPathFor(databasePath),
                freshness.LatestRevision(),
                new SymbolsArtifactIdentity(
                    freshness.LatestRevision(),
                    freshness.ArtifactId(),
                    freshness.HasArtifactMetadata()
                        ? ArtifactStampState.Present
                        : ArtifactStampState.Absent));
        }

        public void Dispose()
        {
            _graphOwner?.Dispose();
            Session.Dispose();
        }
    }

    private static bool TryOpenCliReadScope(
        WorkspaceContext ctx,
        TextWriter err,
        out CliReadScope scope,
        string failurePrefix = "Miller read failed",
        bool requireSearchSidecar = true)
    {
        scope = null!;
        if (!TryEnsureCliReadArtifact(ctx, err))
            return false;

        WorkspaceReadHandle? session = null;
        try
        {
            // The one CLI read scope every read verb pins, and the ONLY place Miller asks for bounded reference
            // facts: this process answers one command and exits, so it has nobody to reuse a whole-generation
            // load. Resident processes open the plain factory entry point.
            session = WorkspaceReadSessionFactory.OpenForOneShotCli(
                ctx.ExtractDbPath,
                ctx.CanonicalRoot ?? ctx.WorkspaceRoot,
                ctx.WorkspaceId);
            SymbolSearchSidecar sidecar = SymbolSearchSidecar.FromEnvironment();
            if (session.Snapshot.Mode == WorkspaceReadMode.FamilyStore)
            {
                string storeRoot = session.FamilyStoreRoot
                    ?? throw new InvalidOperationException("The family-store read session has no family root.");
                ISymbolLookupIndex symbolIndex = requireSearchSidecar && sidecar.Enabled
                    ? sidecar.OpenStoreRequired(storeRoot, session.Snapshot)
                    : SymbolSearchProjectionLoader.LoadSession(session);
                // No supplemental-edge cache delegate ON PURPOSE. The delegate exists to SHARE one edge load
                // across the many graph instances a resident server builds; this process answers one command
                // and exits, so a cache here can never be hit. The edges themselves are identical either way —
                // both paths run SqliteSymbolGraphIndex.ReadSupplementalEdges, whose test-linkage arm is gated
                // by a LIMIT 1 existence probe and whose endpoints resolve in one batched statement.
                var graph = new SqliteSymbolGraphIndex(session);
                scope = new CliReadScope(session, symbolIndex, graph, graph);
            }
            else
            {
                ISymbolLookupIndex symbolIndex = requireSearchSidecar && sidecar.Enabled
                    ? (ISymbolLookupIndex?)TryOpenFreshSymbolSearchSidecar(ctx.ExtractDbPath, sidecar)
                        ?? SymbolSearchProjectionLoader.LoadSession(session)
                    : SymbolSearchProjectionLoader.LoadSession(session);
                var graph = new SqliteSymbolGraphIndex(ctx.ExtractDbPath);
                scope = new CliReadScope(session, symbolIndex, graph, graph);
            }
            session = null;
            return true;
        }
        catch (Exception ex) when (
            ex is FileNotFoundException or DirectoryNotFoundException or IOException
                or UnauthorizedAccessException or InvalidOperationException or ArgumentException
                or NotSupportedException or IncompatibleExtractException or SqliteException)
        {
            session?.Dispose();
            err.WriteLine(failurePrefix + ": " + ex.Message);
            return false;
        }
    }

    private static bool TryEnsureCliReadArtifact(WorkspaceContext ctx, TextWriter err)
    {
        bool storeEnabled;
        try
        {
            storeEnabled = WorkspaceReadSessionFactory.StoreEnabledFromEnvironment();
        }
        catch (InvalidOperationException ex)
        {
            err.WriteLine(ex.Message);
            return false;
        }

        string workspaceRoot = ctx.CanonicalRoot ?? ctx.WorkspaceRoot;
        StoreWorkspacePointerDocument? pointer;
        try
        {
            pointer = Directory.Exists(workspaceRoot)
                ? StoreWorkspacePointer.Read(workspaceRoot)
                : null;
        }
        catch (StorePointerFormatException ex)
        {
            err.WriteLine(
                $"The store pointer is malformed ({ex.Message}); workspace reconciliation is required before any CLI read.");
            return TryReconcileCliSource(ctx, err, storeEnabled);
        }

        if (storeEnabled)
        {
            if (pointer is null)
            {
                err.WriteLine(
                    $"Store mode is enabled but workspace '{ctx.CanonicalRoot ?? ctx.WorkspaceRoot}' has no store pointer.");
                return false;
            }

            return true;
        }

        if (pointer is null)
        {
            if (File.Exists(ctx.ExtractDbPath))
                return true;
            err.WriteLine($"no Miller index at {ctx.ExtractDbPath}.");
            err.WriteLine("Build it with `miller workspace full`, or open this folder in the Miller MCP server.");
            return false;
        }

        try
        {
            StoreRollbackExportResult rollback = StoreRollbackExporter.ExportIfRequired(
                ctx.CanonicalRoot ?? ctx.WorkspaceRoot,
                ctx.ExtractDbPath,
                JulieStoreClient.Locate(ctx.ToolsRoot));
            if (rollback.Warning is { } warning)
                err.WriteLine(warning);
            if (rollback.RequiresPointerCleanup)
            {
                err.WriteLine("The legacy artifact is ready, but the store pointer cleanup must succeed before serving it.");
                return false;
            }
            if (rollback.RequiresSourceRebuild)
            {
                err.WriteLine("The legacy artifact is unavailable until source reconciliation completes.");
                return TryReconcileCliSource(ctx, err);
            }

            return StoreWorkspacePointer.Read(workspaceRoot) is null && File.Exists(ctx.ExtractDbPath);
        }
        catch (Exception ex) when (StoreRollbackExporter.IsOperationalFailure(ex))
        {
            err.WriteLine("Store rollback export failed; the CLI will not serve legacy bytes: " + ex.Message);
            return false;
        }
    }

    private static bool TryReconcileCliSource(WorkspaceContext ctx, TextWriter err, bool storeEnabled = false)
    {
        try
        {
            using WorkspaceRegistry registry = WorkspaceRegistry.Open(ctx.RegistryDbPath);
            WorkspaceRegistryRow? row = FindCurrentWorkspaceRow(registry, ctx);
            if (row is null)
            {
                err.WriteLine(
                    "The current workspace is not registered, so Miller cannot reconcile the malformed store " +
                    "pointer automatically. Run `miller workspace open` first.");
                return false;
            }

            JulieExtractRunner runner = JulieExtractRunner.Locate(ctx.ToolsRoot);
            var refresh = new CrossWorkspaceRefreshService(
                registry,
                runner,
                SymbolSearchSidecar.FromEnvironment(),
                CliScanGovernor(ctx));
            WorkspaceRefreshResult result = refresh.Refresh(row.WorkspaceId, force: true, bypassBackoff: true);
            if (result.Scanned && result.Status is WorkspaceRefreshStatus.Refreshed or WorkspaceRefreshStatus.Unchanged)
            {
                if (!storeEnabled)
                    return File.Exists(ctx.ExtractDbPath);

                StoreWorkspacePointerDocument? repaired = StoreWorkspacePointer.Read(
                    ctx.CanonicalRoot ?? ctx.WorkspaceRoot);
                if (repaired is null)
                    return false;
                var binding = new StoreFamilyBinding(
                    repaired.FamilyId,
                    repaired.StoreRoot,
                    repaired.ViewId,
                    repaired.WorkspaceRoot,
                    StoreBindingState.Ready);
                using FamilyStoreReadSession session = FamilyStoreReadSession.Open(binding, row.WorkspaceId);
                return true;
            }

            err.WriteLine(
                $"Source reconciliation did not complete; the CLI will not serve legacy bytes: " +
                $"{result.Error ?? result.WarningText ?? result.StatusText}.");
            return false;
        }
        catch (Exception ex) when (StoreRollbackExporter.IsOperationalFailure(ex))
        {
            err.WriteLine(
                "Source reconciliation failed; the CLI will not serve legacy bytes: " + ex.Message);
            return false;
        }
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
        => TryEnsureCliReadArtifact(ctx, err);

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
          rules              Print the Miller routing block for agent-instruction files. The rendered file goes to
                             stdout and the target path to stderr, so `miller rules --harness cursor > FILE` works.
                             [--harness cursor|windsurf|cline|kiro|copilot|agents]
          search <query>     Find code by name, identifier, or phrase.
                             [--workspace-id SELECTOR] [--workspace DIR] [--mode auto|text|symbol|file|markers|content|source|external|web|all-text] [--regions KINDS] [--file-pattern GLOB] [--language LANG] [--arm auto|lexical|semantic|hybrid] [--limit N] [--json] [--include-tests|--exclude-tests]
                             --arm selects the retrieval policy for this call (symbol route only); absent or auto = normal policy routing.
                             semantic retrieval is on by default; semantic|hybrid need a serving vector artifact and fail loudly rather than answering lexically.
          todos              CLI alias for search --mode markers over TODO/FIXME/HACK/XXX comment markers.
                             [--markers TODO,FIXME,HACK,XXX] [--workspace-id SELECTOR] [--workspace DIR] [--file-pattern GLOB] [--language LANG] [--limit N] [--json] [--exclude-tests]
          content <op>       Import/search/read/shape/list/remove/export external and web text in content.db.
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
          telemetry <op>     Export machine-global Miller telemetry, or the semantic-canary aggregate/gate.
                             export [--jsonl] [--workspace-id ID|all]
                             canary [--json] [--contract 2|3] [--source-id ID] [--from YYYY-MM-DD] [--to YYYY-MM-DD]
                             canary --gate [--json] [--contract 2|3]                 # local gate verdict per semantic-identity cohort
                             canary combine <export.json>... [--json]               # privacy-safe v3 multi-source aggregate
          symbols <op>       Bulk-export every symbol row for fleet rollups.   # JSONL
                             export [--jsonl] [--workspace-id SELECTOR] [--workspace DIR]
          references <op>    Bulk-export identifier/reference usage facts.
                             export     [--jsonl] [--workspace-id SELECTOR] [--workspace DIR]   # JSONL fact feed
          complexity <op>    Bulk-export per-symbol/per-file complexity metrics.   # JSONL
                             export [--jsonl] [--workspace-id SELECTOR] [--workspace DIR]
          refresh            Refresh a registered workspace index and return after convergence attempt.
                             [--json] [--wait] [--workspace-id SELECTOR|--workspace DIR] [--full]
          inspect <target>   List a file's symbols, or show a symbol's definition.
                             [--workspace-id SELECTOR] [--workspace DIR] [--depth summary|overview|full] [--kind K] [--scope FILE] [--limit N] [--json]
          context <query>    Token-budgeted bundle of the most relevant code for a task.
                             [--workspace-id SELECTOR] [--workspace DIR] [--token-budget N] [--max-hops 0-2] [--entry-symbol NAME] [--edited-files PATHS] [--failing-test TEXT] [--stack-trace TEXT] [--reference-mode off|usage] [--reference-depth 0-1] [--exclude-tests] [--json]
          impact <input>     Downstream symbols + tests a change would affect.
                             <symbol> | --changed-paths PATH[,PATH...] | --diff DIFF | --git [--base REF] [--staged]
                             [--workspace-id SELECTOR] [--workspace DIR] [--max-depth N] [--limit N] [--json]
          trace <symbol>     Follow exact references, a dependency path, or a cross-language bridge.
                             [--workspace-id SELECTOR] [--workspace DIR] [--scope FILE] [--mode refs|path|bridge] [--to SYMBOL] [--path-kind call|dependency] [--reference-kind KIND] [--no-definition] [--depth N] [--limit N] [--continuation TOKEN] [--full] [--json]
          dashboard          Start or reuse the machine-global loopback dashboard.
                             [--port N] [--json]
          workspace [op]     Index lifecycle. op = status (default) | health | onboarding | leader | list | refresh | full | open | remove | prune.
                             open   [--path DIR] [--full]   Register + index a directory (creates .miller/symbols.db).
                             leader [--handoff] [--wait]    Diagnose current leader and optionally request graceful handoff.
                             remove (--id ID | --path DIR)  Delete a workspace's .miller index dir.
                             prune  [--dry-run]              Remove registry rows whose roots no longer exist.
                             [--id|--workspace-id SELECTOR] [--path|--workspace DIR] [--json]
          tests [op]         Continuous testing. op = status (default) | serve | run | enable | disable | stop.
                             status [--json]                Cheap read: projects, daemon, verdict. Creates nothing.
                             serve                          Start the detached CT daemon (explicit only).
                             run [--wait] [--json]          Daemon command channel, or a foreground one-shot.
                             enable [--project PATH]        Discover and persist test projects in ct.db.
                             disable [--project PATH]       Disable stored project rows.
                             stop                           Graceful daemon stop.
                             [--workspace-id SELECTOR] [--workspace DIR]
          semantic <op>      Optional semantic retrieval lifecycle. op = prepare.
                             prepare [--model <id>] [--json]   Consent to and run the pinned sidecar's model
                             download (sha256-verified, into the shared cache). Streams progress; exits with the
                             sidecar's status. Running this verb IS the consent — Miller never auto-downloads.
          version            Print the build version (e.g. 0.3.2+<sha>).
          help               Show this help.
          serve              Run the MCP stdio server (the default when launched with no arguments).
        """;

    private static CliSemanticSession CreateCliSemanticSession(WorkspaceContext workspace) =>
        new(workspace.ToolsRoot, MillerHomeFor(workspace));

    /// <summary>The machine-global <c>.miller</c> directory: the registry's own parent.</summary>
    private static string MillerHomeFor(WorkspaceContext workspace) =>
        Path.GetDirectoryName(workspace.RegistryDbPath)
        ?? throw new InvalidOperationException(
            $"Registry path '{workspace.RegistryDbPath}' has no parent directory.");

    // One governor per (miller home, kill-switch value), not one per call site: ScanGovernor holds a ThreadLocal
    // that reserves a slot for the instance's lifetime, and the CLI builds one at eight sites per process.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Home, string Env), ScanGovernor>
        CliScanGovernors = new();

    private static ScanGovernor CliScanGovernor(WorkspaceContext workspace)
    {
        string home = MillerHomeFor(workspace);
        string env = Environment.GetEnvironmentVariable(ScanGovernor.EnvVar) ?? string.Empty;
        return CliScanGovernors.GetOrAdd((home, env), _ => ScanGovernor.FromEnvironment(home));
    }

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
          levels   Show or set the progressive-indexing policy.                [--set progressive|full|symbols-only] [--clear]
          prune    Remove registry rows whose roots no longer exist.           [--dry-run]

        Selectors / flags: [--id|--workspace-id SELECTOR] [--path|--workspace DIR] [--json] [--handoff] [--wait] [--dry-run]
          --workspace-id aliases --id; --workspace (a directory, resolved against the cwd) aliases --path —
          the same selector flags every read verb accepts.
        """;

    private const string TestsHelpText =
        """
        miller tests — continuous testing for the current (or a selected) workspace.

        Usage: miller tests [op] [args]

        Operations:
          status   Cheap honest status. Creates nothing. Default when no op is given. [--json]
          failures One page of red test cases, newest verdict per case. [--limit N] [--offset N] [--json]
          serve    Start the detached CT daemon. Explicit start only.
          run      Live daemon: command channel. No daemon: foreground one-shot. [--wait] [--json]
          enable   Discover test projects and persist rows in ct.db. [--project PATH]
          disable  Disable stored project rows. [--project PATH]
          stop     Graceful daemon stop.

        Selectors / flags: [--workspace-id SELECTOR] [--workspace DIR] [--json] [--wait] [--project PATH]
                           [--limit N] [--offset N]
        """;
}

/// <summary>Which retrieval arm the CLI <c>search</c> verb was told to run. CLI-only — the MCP tool has no
/// equivalent parameter (ADR-0003 / MCP-stinginess).</summary>
internal enum CliSearchArm
{
    /// <summary>No <c>--arm</c> flag: the query is routed by <see cref="SemanticQueryPolicy"/>, exactly as the
    /// MCP host routes it.</summary>
    Policy,

    /// <summary>Force today's lexical-only path, whatever the mode and artifact would allow.</summary>
    Lexical,

    /// <summary>Render the semantic arm's own hits with rank and cosine, for evaluation.</summary>
    Semantic,

    /// <summary>Force fusion even for a query the policy would route lexical-only.</summary>
    Hybrid,
}

/// <summary>
/// The one-shot CLI's embedding session: opened at most once per invocation and shut down before the verb
/// returns. The server owns a process-wide singleton instead, because a resident child process, its restart
/// count and an open circuit are state a per-query session would silently reset — a CLI process has no queries
/// after this one, so the same reasoning ends at disposal.
/// </summary>
internal sealed class CliSemanticSession(string toolsRoot, string millerHome) : IDisposable
{
    private SemanticEmbeddingSession? _session;
    private bool _opened;

    public SemanticEmbeddingSession? Open()
    {
        if (_opened)
            return _session;

        _opened = true;
        _session = SemanticSearchArm.ProcessSession(toolsRoot, millerHome);
        return _session;
    }

    public void Dispose() => _session?.DisposeAsync().AsTask().GetAwaiter().GetResult();
}

/// <summary>
/// The <c>--arm hybrid</c> fusion arm: <see cref="SemanticSymbolFusionArm"/> without the mode and route gates.
/// Forcing fusion is the whole point of the flag — an evaluator comparing arms on a symbol-lookup query needs
/// the fused ranking the policy would have declined to produce — so this deliberately does NOT reuse the
/// production arm, whose abstentions are what keep policy-routed output byte-identical to lexical.
/// </summary>
internal sealed class ForcedHybridFusionArm(Func<SemanticSearchArm> openArm) : ISymbolFusionArm
{
    private const int MinimumRecall = 10;

    /// <summary>Whether the executor offered this arm the query at all — a file-name candidate set never does.</summary>
    public bool Queried { get; private set; }

    /// <summary>
    /// Why the semantic query was not served, or null when it ran. The interface answers an unserved query with
    /// the same <c>null</c> a genuinely empty one returns, which is the fail-open the production arm wants and
    /// the silent lexical fallback a forced evaluation run must never make; keeping the reason here lets the CLI
    /// tell the two apart.
    /// </summary>
    public string? UnservedReason { get; private set; }

    public IReadOnlyList<FusedCandidate>? Fuse(ISymbolLookupIndex index, SymbolFusionRequest request)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(request);

        Queried = true;
        int k = Math.Clamp(request.Limit * 2, MinimumRecall, SemanticSearchArm.MaxCandidates);
        SemanticQueryResult result = openArm()
            .QuerySymbolsAsync(request.Query, k, match => Admits(index, request, match))
            .GetAwaiter()
            .GetResult();

        if (!result.Served)
        {
            UnservedReason = result.UnavailableReason ?? "the semantic arm did not serve this query.";
            return null;
        }

        if (result.Hits.Count == 0)
            return null;

        var semantic = new List<SemanticRankedCandidate>(result.Hits.Count);
        foreach (SemanticHit hit in result.Hits)
        {
            if (hit.SymbolId is { } symbolId && index.FindBySymbolId(symbolId) is { } symbol)
                semantic.Add(new SemanticRankedCandidate(SearchTool.ToCandidate(symbol, score: 0), hit.Rank));
        }

        // The class still comes from the policy even though the hybrid decision does not: the frozen fusion-v1
        // weights are keyed on query shape, so a forced run must be scored under the same profile a routed one
        // would have used or the comparison measures the weights rather than the arms.
        SemanticFusionClass fusionClass = SemanticQueryPolicy.Route(request.Query).HybridClass;
        SemanticCandidateAdmission admission =
            SemanticQueryPolicy.DecideAdmission(SearchRouteExecutor.EvidenceFrom(request.Candidates));
        return semantic.Count == 0
            ? null
            : SearchRouteExecutor.ApplyAdmission(
                request.Candidates,
                semantic,
                RrfFusion.WeightsFor(fusionClass),
                admission);
    }

    private static bool Admits(ISymbolLookupIndex index, SymbolFusionRequest request, VectorMatch match) =>
        index.FindBySymbolId(match.UnitId) is { } symbol && request.Allows(symbol);
}

/// <summary>
/// Renders the semantic arm's own ranking for <c>--arm semantic</c>. Evaluation compares runs against each
/// other, so every field is derived from the hit itself and cosine is formatted invariantly — a culture-sensitive
/// decimal separator alone would make two identical runs differ.
/// </summary>
internal static class CliSemanticRender
{
    public static string Symbols(
        ISymbolLookupIndex index,
        IReadOnlyList<SemanticHit> hits,
        string query,
        int limit,
        bool json)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(hits);

        var rows = new List<(SemanticHit Hit, IndexedSymbol Symbol)>(hits.Count);
        foreach (SemanticHit hit in hits)
        {
            if (rows.Count == limit)
                break;
            if (hit.SymbolId is { } symbolId && index.FindBySymbolId(symbolId) is { } symbol)
                rows.Add((hit, symbol));
        }

        return json ? Json(rows) : Compact(rows, query);
    }

    private static string Json(IReadOnlyList<(SemanticHit Hit, IndexedSymbol Symbol)> rows)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(
            buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartArray();
            foreach ((SemanticHit hit, IndexedSymbol symbol) in rows)
            {
                w.WriteStartObject();
                w.WriteNumber("rank", hit.Rank);
                w.WriteNumber("cosine", Math.Round(hit.Cosine, CosineDigits, MidpointRounding.ToEven));
                w.WriteString("symbol_id", symbol.SymbolId);
                w.WriteString("name", symbol.Name);
                w.WriteString("kind", symbol.Kind);
                w.WriteString("language", symbol.Language);
                w.WriteString("path", symbol.FilePath);
                w.WriteNumber("start_line", symbol.StartLine);
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string Compact(IReadOnlyList<(SemanticHit Hit, IndexedSymbol Symbol)> rows, string query)
    {
        var sb = new StringBuilder();
        sb.Append("semantic symbols for \"").Append(query).Append("\" (").Append(rows.Count).Append(")");
        if (rows.Count == 0)
            return sb.Append("\nno semantic neighbours in the serving vector artifact.").ToString();

        foreach ((SemanticHit hit, IndexedSymbol symbol) in rows)
        {
            sb.Append("\n  ")
                .Append(hit.Rank.ToString(CultureInfo.InvariantCulture))
                .Append("  cos ")
                .Append(Cosine(hit.Cosine))
                .Append("  ")
                .Append(symbol.Name)
                .Append("  ")
                .Append(symbol.Kind)
                .Append("  ")
                .Append(symbol.FilePath)
                .Append(':')
                .Append(symbol.StartLine.ToString(CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    private const int CosineDigits = 4;

    private static string Cosine(double cosine) =>
        cosine.ToString("F" + CosineDigits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
}
