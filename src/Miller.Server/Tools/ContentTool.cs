using System.Buffers;
using System.ComponentModel;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Miller.Core.Search;
using Miller.Indexing;
using Miller.Server.Telemetry;
using Miller.Server.Workspaces;
using ModelContextProtocol.Server;

namespace Miller.Server.Tools;

internal readonly record struct ContentToolExecutionResult(string Output, bool IsError);

[McpServerToolType]
public sealed class ContentTool
{
    private readonly WorkspaceContext _workspace;
    private readonly ContentCorpusExternalStore _store;

    private sealed record ContentNextAction(
        string Tool,
        string Reason,
        IReadOnlyList<KeyValuePair<string, string>> Args);

    private sealed record ContentReadLocation(
        string ContentDbPath,
        string? WorkspaceId,
        string WorkspaceRoot);

    public ContentTool(WorkspaceContext workspace, ContentCorpusExternalStore store)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(store);
        _workspace = workspace;
        _store = store;
    }

    [McpServerTool(Name = "content")]
    [Description(
        "Import, search, read, shape, list, and remove text in Miller's content corpus: logs, CI output, web " +
        "markdown, reports, large text files, JSONL feeds. Search hits carry a source_id; shape gives a bounded " +
        "first look, and read returns a bounded line window. Use for any big non-workspace text you'd otherwise " +
        "cat into context. NOT for: workspace source/docs text (search mode=source or mode=content) or code " +
        "symbols (search/inspect). Example: content operation=import path=/tmp/ci.log then content " +
        "operation=search query=\"first failing test\".")]
    public string Content(
        [Description("import|add_markdown|search|read|shape|list|remove. Default list.")] string? operation = "list",
        [Description("Path to import for operation=import/add_markdown.")] string? path = null,
        [Description("Search query for operation=search.")] string? query = null,
        [Description("Imported source id for operation=read/shape/remove, of the form external_file:<hash> or web:<hash>. A unique display_path is also accepted for read/shape.")] string? source_id = null,
        [Description("URL metadata for operation=add_markdown with web content.")] string? url = null,
        [Description("Human display path/title for imported content. Optional.")] string? display_path = null,
        [Description("Content kind for search/list. Search defaults external_file; bare list inventories external_file and web.")] string? content_kind = null,
        [Description("Workspace selector for search/read. Use all only for registered workspace search. Optional.")] string? workspace_id = null,
        [Description("1-based center line for operation=read.")] int? line = null,
        [Description("Context lines before/after the read line. Default 10. A window over 200 lines (MaxReadWindowLines) is clamped to 200, keeping the requested line; compact output then names the next line to continue from.")] int? context_lines = null,
        [Description("Max search results, or returned list rows per kind. List is capped at 20 per kind. Default 6.")] int limit = SearchTool.DefaultLimit,
        [Description("Max import bytes. Required to intentionally import files over the default cap.")] long? max_bytes = null,
        [Description("Output format: compact|json. Default compact.")] string format = "compact")
    {
        return Execute(
            operation,
            path,
            query,
            source_id,
            url,
            display_path,
            content_kind,
            workspace_id,
            line,
            context_lines,
            limit,
            max_bytes,
            format).Output;
    }

    internal ContentToolExecutionResult Execute(
        string? operation,
        string? path,
        string? query,
        string? sourceId,
        string? url,
        string? displayPath,
        string? contentKind,
        string? workspaceId,
        int? line,
        int? contextLines,
        int limit,
        long? maxBytes,
        string format)
    {
        var telemetry = TelemetryContext.Current;
        bool json = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);
        string op = string.IsNullOrWhiteSpace(operation) ? "list" : operation.Trim().ToLowerInvariant();
        try
        {
            string contentDbPath = ContentCorpusSidecar.ContentDbPathFor(_workspace.ExtractDbPath);
            if (telemetry is not null)
                telemetry.Op = op;

            string output = op switch
            {
                "import" or "add" => Import(contentDbPath, path, maxBytes, displayPath, json, telemetry),
                "add_markdown" or "add-markdown" or "import_markdown" or "import-markdown" =>
                    AddMarkdown(contentDbPath, path, url, maxBytes, displayPath, json, telemetry),
                "search" => Search(contentDbPath, query, ContentKindOrDefault(contentKind, TextContentKind.ExternalFile), limit, workspaceId, json, telemetry),
                "read" => Read(contentDbPath, sourceId, workspaceId, line, contextLines, json, telemetry),
                "shape" => Shape(contentDbPath, sourceId, workspaceId, json, telemetry),
                "list" => List(contentDbPath, OptionalContentKind(contentKind), limit, json, telemetry),
                "remove" or "delete" => Remove(contentDbPath, sourceId, json, telemetry),
                _ => throw new InvalidOperationException("content operation must be import, add_markdown, search, read, shape, list, or remove."),
            };
            return new ContentToolExecutionResult(output, IsError: false);
        }
        catch (Exception ex)
        {
            string diagnosticCode = ContentDiagnosticCode(op, ex);
            if (telemetry is not null)
            {
                telemetry.Outcome = TelemetryOutcome.Error;
                telemetry.SetError(ex);
                telemetry.SetMetadata("diagnostic_code", diagnosticCode);
                telemetry.SetErrorCategory(diagnosticCode);
            }
            string output = RenderFailure(op, ex, diagnosticCode, json, sourceId);
            return new ContentToolExecutionResult(output, IsError: true);
        }
    }

    internal static string RenderFailure(string operation, Exception ex, bool json) =>
        RenderFailure(operation, ex, ContentDiagnosticCode(operation, ex), json, sourceId: null);

    private static string RenderFailure(
        string operation,
        Exception ex,
        string diagnosticCode,
        bool json,
        string? sourceId) =>
        json
            ? RenderDiagnosticJson(operation, ex.Message, diagnosticCode, ReadRecoveryNextActions(sourceId))
            : RenderDiagnosticCompact(operation, ex.Message, diagnosticCode, ReadRecoveryNextActions(sourceId));

    private string Shape(
        string contentDbPath,
        string? sourceId,
        string? workspaceId,
        bool json,
        TelemetryScope? telemetry)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
            throw new InvalidOperationException("content shape requires source_id.");

        var currentLocation = new ContentReadLocation(
            contentDbPath,
            _workspace.WorkspaceId,
            _workspace.CanonicalRoot ?? _workspace.WorkspaceRoot);
        ContentReadLocation shapeLocation = ResolveReadLocation(currentLocation, sourceId, workspaceId);
        string resolvedSourceId = ResolveReadSourceId(shapeLocation.ContentDbPath, sourceId);
        shapeLocation = ResolveReadLocation(shapeLocation, resolvedSourceId, workspaceId: null);
        ExternalContentShape result = _store.Shape(shapeLocation.ContentDbPath, resolvedSourceId);
        if (telemetry is not null)
        {
            if (!string.IsNullOrWhiteSpace(shapeLocation.WorkspaceId))
                telemetry.SetWorkspace(shapeLocation.WorkspaceId, shapeLocation.WorkspaceRoot);
            telemetry.SetTarget(result.DisplayPath);
            telemetry.ResultCount = result.Head.Count + result.Tail.Count;
            telemetry.SourceBytes = result.SourceBytes;
            telemetry.Outcome = TelemetryOutcome.Ok;
            telemetry.SetMetadata("severity_basis", "text_derived");
        }

        string output = json ? RenderShapeJson(result) : RenderShapeCompact(result);
        return EnsureOutputBudget(output, 8_000, "shape");
    }

    private string Import(
        string contentDbPath,
        string? path,
        long? maxBytes,
        string? displayPath,
        bool json,
        TelemetryScope? telemetry)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("content import requires path.");

        ExternalContentImportResult result = _store.Import(contentDbPath, path, maxBytes, displayPath);
        if (telemetry is not null)
        {
            telemetry.SetTarget(result.DisplayPath);
            telemetry.ResultCount = 1;
            telemetry.SourceBytes = result.SourceBytes;
            telemetry.Outcome = TelemetryOutcome.Ok;
        }

        return json ? RenderImportJson(result) : RenderImportCompact(result);
    }

    private string AddMarkdown(
        string contentDbPath,
        string? path,
        string? url,
        long? maxBytes,
        string? displayPath,
        bool json,
        TelemetryScope? telemetry)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("content add_markdown requires path.");
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("content add_markdown requires url.");

        ExternalContentImportResult result = _store.ImportMarkdown(contentDbPath, path, url, maxBytes, displayPath);
        if (telemetry is not null)
        {
            telemetry.SetTarget(result.DisplayPath);
            telemetry.ResultCount = 1;
            telemetry.SourceBytes = result.SourceBytes;
            telemetry.Outcome = TelemetryOutcome.Ok;
        }

        return json ? RenderImportJson(result) : RenderImportCompact(result);
    }

    private string Search(
        string contentDbPath,
        string? query,
        string contentKind,
        int limit,
        string? workspaceId,
        bool json,
        TelemetryScope? telemetry)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new InvalidOperationException("content search requires query.");
        if (limit < 1) limit = 1;
        if (telemetry is not null)
            SetContentSearchTelemetryShape(telemetry, contentKind, json, limit, workspaceId);

        if (!string.IsNullOrWhiteSpace(workspaceId))
            return SearchWorkspaces(query, contentKind, limit, workspaceId, json, telemetry);

        IReadOnlyList<TextContentSearchHit> hits = _store.Search(contentDbPath, query, contentKind, limit);
        if (telemetry is not null)
        {
            telemetry.SetTarget(query);
            telemetry.ResultCount = hits.Count;
            telemetry.SourceBytes = hits
                .GroupBy(static hit => hit.SourceId, StringComparer.Ordinal)
                .Sum(static group => group.Max(static hit => hit.SourceBytes));
            telemetry.Outcome = hits.Count == 0 ? TelemetryOutcome.Empty : TelemetryOutcome.Ok;
            if (hits.Count == 0)
                SetContentSearchEmptyTelemetry(telemetry, query, contentKind);
        }

        return json ? RenderSearchJson(hits, query, contentKind) : RenderSearchCompact(hits, query, contentKind);
    }

    private string SearchWorkspaces(
        string query,
        string contentKind,
        int limit,
        string workspaceId,
        bool json,
        TelemetryScope? telemetry)
    {
        IReadOnlyList<WorkspaceRegistryRow> workspaces = ResolveContentSearchWorkspaces(workspaceId);
        var hits = new List<WorkspaceContentSearchHit>();
        foreach (WorkspaceRegistryRow row in workspaces)
        {
            string contentDbPath = ContentCorpusSidecar.ContentDbPathFor(row.IndexDbPath);
            foreach (TextContentSearchHit hit in SearchWorkspaceContent(row, contentDbPath, query, contentKind, limit))
                hits.Add(new WorkspaceContentSearchHit(row, hit));
        }

        var page = hits
            .OrderByDescending(static hit => hit.Hit.Score)
            .ThenBy(static hit => hit.Workspace.DisplayId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static hit => hit.Hit.DisplayPath, StringComparer.Ordinal)
            .ThenBy(static hit => hit.Hit.Line)
            .Take(limit)
            .ToArray();

        if (telemetry is not null)
        {
            telemetry.SetTarget(workspaceId);
            telemetry.ResultCount = page.Length;
            telemetry.SourceBytes = page
                .GroupBy(static hit => hit.Workspace.WorkspaceId + "\0" + hit.Hit.SourceId, StringComparer.Ordinal)
                .Sum(static group => group.Max(static hit => hit.Hit.SourceBytes));
            telemetry.Outcome = page.Length == 0 ? TelemetryOutcome.Empty : TelemetryOutcome.Ok;
            if (page.Length == 0)
                SetContentSearchEmptyTelemetry(telemetry, query, contentKind);
        }

        return json
            ? RenderWorkspaceSearchJson(page, query, contentKind)
            : RenderWorkspaceSearchCompact(page, query, contentKind);
    }

    private IReadOnlyList<TextContentSearchHit> SearchWorkspaceContent(
        WorkspaceRegistryRow row,
        string contentDbPath,
        string query,
        string contentKind,
        int limit)
    {
        if (!IsWorkspaceContentKind(contentKind))
            return _store.Search(contentDbPath, query, contentKind, limit);

        long expectedRevision = ExpectedWorkspaceRevision(row);
        return FtsTextContentSearchIndex
            .Open(contentDbPath, expectedRevision)
            .Search(query, contentKind, limit, excludeTests: false);
    }

    private static long ExpectedWorkspaceRevision(WorkspaceRegistryRow row)
    {
        if (File.Exists(row.IndexDbPath))
        {
            using var freshness = new FreshnessReader(row.IndexDbPath);
            return freshness.LatestRevision();
        }

        return row.LastRevision ?? 0L;
    }

    private static bool IsWorkspaceContentKind(string contentKind) =>
        string.Equals(contentKind, TextContentKind.WorkspaceSource, StringComparison.Ordinal)
        || string.Equals(contentKind, TextContentKind.WorkspaceDocs, StringComparison.Ordinal)
        || string.Equals(contentKind, TextContentKind.WorkspaceConfig, StringComparison.Ordinal);

    private string Read(
        string contentDbPath,
        string? sourceId,
        string? workspaceId,
        int? line,
        int? contextLines,
        bool json,
        TelemetryScope? telemetry)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
            throw new InvalidOperationException("content read requires source_id.");
        if (line is null)
            throw new InvalidOperationException("content read requires line.");

        int effectiveContextLines = contextLines ?? ContentCorpusExternalStore.DefaultContextLines;
        var currentLocation = new ContentReadLocation(
            contentDbPath,
            _workspace.WorkspaceId,
            _workspace.CanonicalRoot ?? _workspace.WorkspaceRoot);
        ContentReadLocation readLocation = ResolveReadLocation(currentLocation, sourceId, workspaceId);
        string resolvedSourceId = ResolveReadSourceId(readLocation.ContentDbPath, sourceId);
        readLocation = ResolveReadLocation(readLocation, resolvedSourceId, workspaceId: null);
        ExternalContentReadResult result = ReadWindowWithNearestPaths(
            readLocation.ContentDbPath,
            resolvedSourceId,
            line.Value,
            effectiveContextLines);
        if (telemetry is not null)
        {
            if (!string.IsNullOrWhiteSpace(readLocation.WorkspaceId))
                telemetry.SetWorkspace(readLocation.WorkspaceId, readLocation.WorkspaceRoot);
            telemetry.SetTarget(result.DisplayPath);
            telemetry.ResultCount = result.Lines.Count;
            telemetry.Outcome = result.Lines.Count == 0 ? TelemetryOutcome.Empty : TelemetryOutcome.Ok;
        }

        return json ? RenderReadJson(result) : RenderReadCompact(result, effectiveContextLines);
    }

    private string ResolveReadSourceId(string contentDbPath, string sourceId)
    {
        SourceIdResolution resolution = _store.ResolveSourceId(contentDbPath, sourceId);
        if (resolution.Found)
            return resolution.SourceId!;
        if (resolution.Ambiguous)
        {
            string candidates = string.Join(", ", resolution.Candidates);
            throw new InvalidOperationException(
                $"'{sourceId}' is not a source id and matches multiple imported sources by display_path: {candidates}. " +
                "Pass one of their source_id values (from `content list` or `content search`).");
        }
        // Not a known source_id or display_path in this corpus. Fall through with the
        // original value so the existing workspace-routing + not-found error path handles it.
        return sourceId;
    }

    private const int MaxPathSuggestions = 3;

    private ExternalContentReadResult ReadWindowWithNearestPaths(
        string contentDbPath,
        string sourceId,
        int line,
        int contextLines)
    {
        try
        {
            return _store.ReadWindow(contentDbPath, sourceId, line, contextLines);
        }
        catch (KeyNotFoundException ex)
        {
            IReadOnlyList<string> nearest = NearestDisplayPaths(contentDbPath, sourceId);
            if (nearest.Count == 0)
                throw;
            throw new KeyNotFoundException(
                $"{ex.Message} Nearest imported paths: {string.Join(", ", nearest)}. " +
                "Read one of those paths directly, or run `content list` for every imported source.",
                ex);
        }
    }

    private IReadOnlyList<string> NearestDisplayPaths(string contentDbPath, string requested)
    {
        var sources = new List<ExternalContentSource>();
        sources.AddRange(_store.List(contentDbPath, TextContentKind.ExternalFile));
        sources.AddRange(_store.List(contentDbPath, TextContentKind.Web));

        return sources
            .Select(source => (source.DisplayPath, Score: NearPathScore(requested, source.DisplayPath)))
            .Where(static scored => scored.Score > 0)
            .OrderByDescending(static scored => scored.Score)
            .ThenBy(static scored => scored.DisplayPath, StringComparer.Ordinal)
            .Take(MaxPathSuggestions)
            .Select(static scored => scored.DisplayPath)
            .ToArray();
    }

    private static int NearPathScore(string requested, string displayPath)
    {
        string[] requestedSegments = PathSegments(requested);
        string[] candidateSegments = PathSegments(displayPath);

        int sharedTrailing = 0;
        while (sharedTrailing < requestedSegments.Length
            && sharedTrailing < candidateSegments.Length
            && string.Equals(
                requestedSegments[^(sharedTrailing + 1)],
                candidateSegments[^(sharedTrailing + 1)],
                StringComparison.OrdinalIgnoreCase))
        {
            sharedTrailing++;
        }

        int sharedSegments = requestedSegments.Intersect(candidateSegments, StringComparer.OrdinalIgnoreCase).Count();
        bool overlaps = displayPath.Contains(requested, StringComparison.OrdinalIgnoreCase)
            || requested.Contains(displayPath, StringComparison.OrdinalIgnoreCase);

        return (10 * sharedTrailing) + sharedSegments + (overlaps ? 1 : 0);
    }

    private static string[] PathSegments(string value) =>
        value.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);

    private ContentReadLocation ResolveReadLocation(
        ContentReadLocation defaultLocation,
        string sourceId,
        string? workspaceId)
    {
        if (TryResolveSourceIdWorkspace(sourceId) is { } routed)
            return routed;

        if (!string.IsNullOrWhiteSpace(workspaceId))
            return ResolveReadWorkspace(defaultLocation, workspaceId);

        return defaultLocation;
    }

    private ContentReadLocation? TryResolveSourceIdWorkspace(string sourceId)
    {
        int separator = sourceId.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0)
            return null;

        string sourceWorkspaceId = sourceId[..separator];
        try
        {
            using WorkspaceRegistry registry = WorkspaceRegistry.Open(_workspace.RegistryDbPath);
            WorkspaceRegistryRow? row = registry.List()
                .FirstOrDefault(r => string.Equals(r.WorkspaceId, sourceWorkspaceId, StringComparison.Ordinal));
            return row is null ? null : Location(row);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            return null;
        }
    }

    private ContentReadLocation ResolveReadWorkspace(ContentReadLocation defaultLocation, string workspaceId)
    {
        string selector = workspaceId.Trim();
        if (string.Equals(selector, "all", StringComparison.OrdinalIgnoreCase)
            || string.Equals(selector, "registered", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "content read workspace_id must select one workspace. Pass the workspace_id from a content search hit, " +
                "or a specific display ID, unique prefix, full workspace_id, registered root path, current, or primary.");
        }

        if (string.Equals(selector, "current", StringComparison.OrdinalIgnoreCase)
            || string.Equals(selector, "primary", StringComparison.OrdinalIgnoreCase))
        {
            return defaultLocation;
        }

        using var registry = WorkspaceRegistry.Open(_workspace.RegistryDbPath);
        WorkspaceRegistryRow row = WorkspaceRegistrySelector.Resolve(registry, selector);
        return Location(row);
    }

    private static ContentReadLocation Location(WorkspaceRegistryRow row) => new(
        ContentCorpusSidecar.ContentDbPathFor(row.IndexDbPath),
        row.WorkspaceId,
        row.CanonicalRoot);

    private const int MaxListSourcesPerKind = 20;

    private string List(
        string contentDbPath,
        string? contentKind,
        int limit,
        bool json,
        TelemetryScope? telemetry)
    {
        if (limit <= 0)
            throw new InvalidOperationException("content list limit must be > 0.");
        int effectiveLimit = Math.Min(limit, MaxListSourcesPerKind);
        ExternalContentInventory inventory = _store.Inventory(contentDbPath, contentKind, effectiveLimit);
        if (telemetry is not null)
        {
            telemetry.ResultCount = inventory.ReturnedCount;
            telemetry.Outcome = inventory.TotalCount == 0 ? TelemetryOutcome.Empty : TelemetryOutcome.Ok;
            telemetry.SetMetadata("content_kind", contentKind ?? "imported");
            telemetry.SetMetadata("format", json ? "json" : "compact");
            telemetry.SetMetadata("total_count", inventory.TotalCount);
            telemetry.SetMetadata("omitted_count", inventory.OmittedCount);
            if (inventory.TotalCount == 0)
                telemetry.SetEmptyReason("no_imported_content");
        }

        string output = json ? RenderListJson(inventory) : RenderListCompact(inventory);
        return EnsureOutputBudget(output, json ? 48_000 : 16_000, "list");
    }

    private string Remove(string contentDbPath, string? sourceId, bool json, TelemetryScope? telemetry)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
            throw new InvalidOperationException("content remove requires source_id.");

        ExternalContentRemoveResult result = _store.Remove(contentDbPath, sourceId);
        if (telemetry is not null)
        {
            telemetry.SetTarget(sourceId);
            telemetry.ResultCount = result.SourceCount;
            telemetry.Outcome = result.Removed ? TelemetryOutcome.Ok : TelemetryOutcome.Empty;
            if (!result.Removed)
                telemetry.SetEmptyReason("source_not_found");
        }

        return json ? RenderRemoveJson(result) : RenderRemoveCompact(result);
    }

    private IReadOnlyList<WorkspaceRegistryRow> ResolveContentSearchWorkspaces(string workspaceId)
    {
        string selector = workspaceId.Trim();
        using var registry = WorkspaceRegistry.Open(_workspace.RegistryDbPath);
        if (string.Equals(selector, "all", StringComparison.OrdinalIgnoreCase)
            || string.Equals(selector, "registered", StringComparison.OrdinalIgnoreCase))
        {
            return registry.List()
                .Where(static row => row.State is WorkspaceRegistryState.Current
                    or WorkspaceRegistryState.Ready
                    or WorkspaceRegistryState.LoadedExisting)
                .ToArray();
        }

        if (string.Equals(selector, "current", StringComparison.OrdinalIgnoreCase)
            || string.Equals(selector, "primary", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                new WorkspaceRegistryRow(
                    _workspace.WorkspaceId ?? "current",
                    "current",
                    _workspace.CanonicalRoot ?? _workspace.WorkspaceRoot,
                    _workspace.CanonicalExtractDbPath ?? _workspace.ExtractDbPath,
                    DateTimeOffset.UtcNow,
                    LastScanAt: null,
                    LastRevision: null,
                    WorkspaceRegistryState.Current,
                    LastError: null),
            ];
        }

        return [WorkspaceRegistrySelector.Resolve(registry, selector)];
    }

    private static string ContentKindOrDefault(string? value, string fallback) =>
        OptionalContentKind(value) ?? fallback;

    private static void SetContentSearchTelemetryShape(
        TelemetryScope telemetry,
        string contentKind,
        bool json,
        int limit,
        string? workspaceId)
    {
        telemetry.SetMetadata("content_kind", contentKind);
        telemetry.SetMetadata("format", json ? "json" : "compact");
        telemetry.SetMetadata("limit_bucket", LimitBucket(limit));
        telemetry.SetMetadata("workspace_all", string.Equals(workspaceId, "all", StringComparison.OrdinalIgnoreCase));
        telemetry.SetMetadata("has_workspace_selector", !string.IsNullOrWhiteSpace(workspaceId));
    }

    private static void SetContentSearchEmptyTelemetry(TelemetryScope telemetry, string query, string contentKind)
    {
        string queryShape = SearchTool.QueryShapeFor(query);
        telemetry.SetEmptyReason("no_content_hits");
        telemetry.SetMetadata("query_shape", queryShape);
        telemetry.SetMetadata("empty_diagnosis", SearchTool.EmptyDiagnosisForContentSearch(contentKind, queryShape));
    }

    private static string LimitBucket(int limit) => limit switch
    {
        <= 0 => "0",
        <= 5 => "1-5",
        <= 10 => "6-10",
        <= 25 => "11-25",
        <= 50 => "26-50",
        _ => "51+",
    };

    private static string? OptionalContentKind(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            string.Equals(value.Trim(), "all", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "source" or "workspace_source" => TextContentKind.WorkspaceSource,
            "docs" or "doc" or "workspace_docs" => TextContentKind.WorkspaceDocs,
            "config" or "workspace_config" => TextContentKind.WorkspaceConfig,
            "external" or "external_file" or "file" => TextContentKind.ExternalFile,
            "web" => TextContentKind.Web,
            _ => throw new InvalidOperationException(
                "content_kind must be all, workspace_source (alias source), workspace_docs (aliases docs, doc), " +
                "workspace_config (alias config), external_file (aliases external, file), or web."),
        };
    }

    private static string RenderImportCompact(ExternalContentImportResult result) =>
        $"{(result.Replaced ? "replaced" : "imported")} {result.ContentKind}\n" +
        $"source_id: {result.SourceId}\n" +
        $"display_path: {result.DisplayPath}\n" +
        (string.IsNullOrWhiteSpace(result.Url) ? "" : $"url: {result.Url}\n") +
        $"source_bytes: {result.SourceBytes}\n" +
        $"chunks: {result.ChunkCount}";

    private static string RenderSearchCompact(IReadOnlyList<TextContentSearchHit> hits, string query, string contentKind)
    {
        if (hits.Count == 0)
            return RenderNoResultsCompact("search", query, contentKind);

        var blocks = new List<string>();
        foreach (IGrouping<string, TextContentSearchHit> group in GroupBySource(hits))
        {
            TextContentSearchHit head = group.First();
            var block = new StringBuilder();
            block.Append(head.DisplayPath)
                .Append("  ").Append(head.ContentKind)
                .Append("  source_id=").Append(head.SourceId);
            AppendGroupedHitRows(block, group, indent: "  ");
            blocks.Add(block.ToString());
        }

        TextContentSearchHit first = hits[0];
        return string.Join("\n\n", blocks)
            + "\n\nread: content read source_id=" + first.SourceId + " line=" + first.Line;
    }

    private static IEnumerable<IGrouping<string, TextContentSearchHit>> GroupBySource(
        IReadOnlyList<TextContentSearchHit> hits) =>
        hits.GroupBy(static hit => hit.SourceId, StringComparer.Ordinal);

    private static void AppendGroupedHitRows(
        StringBuilder block,
        IEnumerable<TextContentSearchHit> hits,
        string indent)
    {
        foreach (TextContentSearchHit hit in hits)
        {
            string[] snippetLines = hit.Snippet.Split('\n');
            block.Append('\n').Append(indent).Append(':').Append(hit.Line).Append("  ").Append(snippetLines[0]);
            string continuation = indent + new string(' ', hit.Line.ToString().Length + 3);
            for (int i = 1; i < snippetLines.Length; i++)
                block.Append('\n').Append(continuation).Append(snippetLines[i]);
        }
    }

    private static string RenderWorkspaceSearchCompact(IReadOnlyList<WorkspaceContentSearchHit> hits, string query, string contentKind)
    {
        if (hits.Count == 0)
            return RenderNoResultsCompact("search", query, contentKind);

        var blocks = new List<string>();
        foreach (IGrouping<string, WorkspaceContentSearchHit> workspaceGroup in
                 hits.GroupBy(static hit => hit.Workspace.WorkspaceId, StringComparer.Ordinal))
        {
            WorkspaceRegistryRow workspace = workspaceGroup.First().Workspace;
            var block = new StringBuilder();
            block.Append(workspace.DisplayId).Append(" (").Append(workspace.WorkspaceId).Append(')');

            foreach (IGrouping<string, TextContentSearchHit> sourceGroup in
                     GroupBySource([.. workspaceGroup.Select(static hit => hit.Hit)]))
            {
                TextContentSearchHit head = sourceGroup.First();
                block.Append('\n').Append("  ")
                    .Append(head.DisplayPath)
                    .Append("  ").Append(head.ContentKind)
                    .Append("  source_id=").Append(head.SourceId);
                AppendGroupedHitRows(block, sourceGroup, indent: "    ");
            }

            blocks.Add(block.ToString());
        }

        WorkspaceContentSearchHit first = hits[0];
        return string.Join("\n\n", blocks)
            + "\n\nread: content read source_id=" + first.Hit.SourceId
            + " line=" + first.Hit.Line
            + " workspace_id=" + first.Workspace.WorkspaceId;
    }

    private static string RenderReadCompact(ExternalContentReadResult result, int contextLines)
    {
        var lines = result.Lines
            .Select(static line =>
            {
                string text = SearchTool.Truncate(line.Text, MaxReadLineUnits);
                return new RenderedReadLine(line.LineNumber, text, text.Length != line.Text.Length);
            })
            .ToArray();
        int truncatedLineCount = lines.Count(static line => line.Truncated);
        var sb = new StringBuilder();
        if (result.Clamped && result.LineEnd < result.SourceLineCount)
        {
            int requestedLines = (2 * contextLines) + 1;
            // The next window's start is itself clamped to centre − (MaxReadWindowLines − 1), so advancing the
            // centre by more than that would step over unread lines instead of resuming at LineEnd + 1; a centre
            // past the last line would be rejected outright, so the final hop lands on the last line and overlaps.
            int advance = Math.Min(contextLines, ContentCorpusExternalStore.MaxReadWindowLines - 1);
            int nextCenter = Math.Min(result.SourceLineCount, result.LineEnd + advance + 1);
            sb.Append("window clamped to ").Append(ContentCorpusExternalStore.MaxReadWindowLines)
              .Append(" lines (requested ").Append(requestedLines)
              .Append(") — continue with line=").Append(nextCenter)
              .Append(" context_lines=").Append(contextLines)
              .Append('\n');
        }

        if (truncatedLineCount > 0)
        {
            sb.Append("read truncated_lines=").Append(truncatedLineCount)
              .Append(" line_limit=").Append(MaxReadLineUnits)
              .Append('\n');
        }

        sb.Append(SearchTool.Truncate(result.DisplayPath, MaxReadDisplayPathUnits))
          .Append(':').Append(result.LineStart).Append('-').Append(result.LineEnd);
        foreach (RenderedReadLine line in lines)
            sb.Append('\n').Append("    ").Append(line.LineNumber).Append(": ").Append(line.Text);
        return sb.ToString();
    }

    private sealed record RenderedReadLine(int LineNumber, string Text, bool Truncated);

    private const int MaxInventoryDisplayPathChars = 240;
    private const int MaxInventoryUrlChars = 240;
    private const int MaxShapeLineChars = 240;
    private const int MaxReadDisplayPathUnits = 240;
    private const int MaxReadLineUnits = 160;
    private const int MaxDiagnosticOutputChars = 8_000;
    private const int MaxDiagnosticErrorChars = 2_000;
    private const int MaxDiagnosticFallbackErrorChars = 1_000;
    private const int MaxDiagnosticReasonChars = 512;
    private const int MaxDiagnosticArgumentChars = 240;
    private static readonly JavaScriptEncoder ContentJsonEncoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    private static string TruncateForJson(string value, int maxEncodedBytes)
    {
        if (JsonEncodedText.Encode(value.AsSpan(), ContentJsonEncoder).EncodedUtf8Bytes.Length <= maxEncodedBytes)
            return value;

        int ellipsisBytes = Encoding.UTF8.GetByteCount("…");
        int availableBytes = Math.Max(0, maxEncodedBytes - ellipsisBytes);
        int low = 0;
        int high = value.Length;
        int best = 0;
        while (low <= high)
        {
            int midpoint = low + ((high - low) / 2);
            int candidate = midpoint;
            if (candidate < value.Length && candidate > 0
                && char.IsHighSurrogate(value[candidate - 1]) && char.IsLowSurrogate(value[candidate]))
            {
                candidate--;
            }

            int encodedBytes = JsonEncodedText.Encode(value.AsSpan(0, candidate), ContentJsonEncoder)
                .EncodedUtf8Bytes.Length;
            if (encodedBytes <= availableBytes)
            {
                best = candidate;
                low = midpoint + 1;
            }
            else
            {
                high = midpoint - 1;
            }
        }

        return value[..best] + "…";
    }

    private static string EnsureOutputBudget(string output, int maxChars, string operation)
    {
        if (output.Length > maxChars)
        {
            throw new InvalidOperationException(
                $"content {operation} output exceeded its {maxChars}-character contract.");
        }
        return output;
    }

    private static string RenderListCompact(ExternalContentInventory inventory)
    {
        var lines = new List<string>
        {
            inventory.TotalCount == 0
                ? $"No imported content. total=0 returned=0 omitted=0 per_kind_limit={inventory.PerKindLimit}"
                : $"content inventory: total={inventory.TotalCount} returned={inventory.ReturnedCount} " +
                  $"omitted={inventory.OmittedCount} per_kind_limit={inventory.PerKindLimit}",
        };
        foreach (ExternalContentKindInventory kind in inventory.Kinds)
        {
            lines.Add(
                $"{kind.ContentKind}: total={kind.TotalCount} returned={kind.ReturnedCount} omitted={kind.OmittedCount}");
            foreach (ExternalContentSource source in kind.Sources)
            {
                lines.Add(
                    $"  {source.SourceId}  {source.SourceBytes} bytes  {source.ChunkCount} chunks  " +
                    SearchTool.Truncate(source.DisplayPath, MaxInventoryDisplayPathChars));
            }
        }

        return string.Join('\n', lines);
    }

    private static string RenderShapeCompact(ExternalContentShape shape)
    {
        var lines = new List<string>
        {
            $"content shape: {SearchTool.Truncate(shape.DisplayPath, MaxInventoryDisplayPathChars)}",
            $"source_id: {shape.SourceId}",
            $"content_kind: {shape.ContentKind}",
            $"source_bytes: {shape.SourceBytes}",
            $"line_count: {shape.LineCount}",
            $"severity (text-derived): fatal={shape.Severity.Fatal} error={shape.Severity.Error} " +
            $"warning={shape.Severity.Warning} info={shape.Severity.Info} debug={shape.Severity.Debug} " +
            $"other={shape.Severity.Other}",
            "head:",
        };
        lines.AddRange(shape.Head.Select(static line =>
            $"  {line.LineNumber}: {SearchTool.Truncate(line.Text, MaxShapeLineChars)}"));
        lines.Add("tail:");
        lines.AddRange(shape.Tail.Select(static line =>
            $"  {line.LineNumber}: {SearchTool.Truncate(line.Text, MaxShapeLineChars)}"));
        return string.Join('\n', lines);
    }

    private static string RenderRemoveCompact(ExternalContentRemoveResult result) =>
        result.Removed
            ? $"removed {result.SourceId} ({result.ChunkCount} chunks)"
            : $"not found: {result.SourceId}";

    private const int EmptyHintQueryLimit = 40;

    private static string RenderNoResultsCompact(string operation, string query, string contentKind)
    {
        string queryShape = SearchTool.QueryShapeFor(query);
        string diagnosis = SearchTool.EmptyDiagnosisForContentSearch(contentKind, queryShape);
        var sb = new StringBuilder();
        sb.Append("No results for content ").Append(operation).Append(".")
          .Append('\n')
          .Append(NoResultsDiagnosisHint(diagnosis, queryShape, query, contentKind));
        AppendContentNextActions(sb, NoResultsCompactNextActions(diagnosis, queryShape, query, contentKind));
        return sb.ToString();
    }

    private static string NoResultsDiagnosisHint(string diagnosis, string queryShape, string query, string contentKind)
    {
        string q = SearchTool.Truncate(query.Trim(), EmptyHintQueryLimit);
        return (diagnosis, queryShape) switch
        {
            ("mode_mismatch", "source_like") =>
                $"'{q}' looks like source code; {contentKind} holds prose. Source bodies live in search mode=source.",
            ("mode_mismatch", _) =>
                $"'{q}' reads like prose; {contentKind} holds source bodies. Docs and config prose live in search mode=content.",
            ("query_shape", "path_like") =>
                $"'{q}' looks like a path; content search matches text inside files. Paths resolve through search mode=file.",
            ("query_shape", _) =>
                $"'{q}' is too short for lexical matching; use a longer literal phrase, e.g. query=\"connection refused\".",
            (_, "natural_language") =>
                $"No lexical match for '{q}' in {contentKind}. Retry with words that appear literally in the {CorpusNoun(contentKind)} text.",
            _ => $"No lexical match for '{q}' in {contentKind}.",
        };
    }

    private static string CorpusNoun(string contentKind) => contentKind switch
    {
        TextContentKind.WorkspaceDocs => "docs",
        TextContentKind.WorkspaceConfig => "config",
        TextContentKind.WorkspaceSource => "source",
        TextContentKind.Web => "web",
        _ => "imported",
    };

    private static IReadOnlyList<ContentNextAction> NoResultsCompactNextActions(
        string diagnosis,
        string queryShape,
        string query,
        string contentKind)
    {
        if (string.Equals(diagnosis, "mode_mismatch", StringComparison.Ordinal))
        {
            return string.Equals(queryShape, "source_like", StringComparison.Ordinal)
                ? [NextAction("search", "search current workspace source-body text", ("query", query), ("mode", "source"))]
                : [NextAction("search", "search workspace docs and config prose", ("query", query), ("mode", "content"))];
        }

        if (string.Equals(diagnosis, "query_shape", StringComparison.Ordinal))
        {
            return string.Equals(queryShape, "path_like", StringComparison.Ordinal)
                ? [NextAction("search", "resolve a path fragment to indexed files", ("query", query), ("mode", "file"))]
                : [NextAction("search", "retry with a longer literal phrase", ("query", "<phrase>"), ("mode", "all-text"))];
        }

        ContentNextAction widen = NextAction(
            "search",
            "widen to every indexed text kind",
            ("query", query),
            ("mode", "all-text"));

        return IsWorkspaceContentKind(contentKind)
            ? [widen]
            :
            [
                NextAction(
                    "content",
                    "confirm what is imported under this kind",
                    ("operation", "list"),
                    ("content_kind", contentKind)),
                widen,
            ];
    }

    private static string RenderDiagnosticCompact(
        string operation,
        string error,
        string diagnosticCode,
        IReadOnlyList<ContentNextAction> nextActions)
    {
        var sb = new StringBuilder();
        sb.Append("content");
        if (string.Equals(operation, "read", StringComparison.Ordinal))
            sb.Append(" read");
        sb.Append(" failed: ").Append(SearchTool.Truncate(error, MaxDiagnosticErrorChars))
          .Append('\n')
          .Append("diagnostic_code=").Append(diagnosticCode);
        AppendContentNextActions(sb, nextActions);
        string output = sb.ToString();
        return output.Length <= MaxDiagnosticOutputChars
            ? output
            : $"content failed: {SearchTool.Truncate(error, MaxDiagnosticFallbackErrorChars)}\n" +
              $"diagnostic_code={diagnosticCode}";
    }

    private static string ContentDiagnosticCode(string operation, Exception ex)
    {
        string message = ex.Message;
        if (string.Equals(operation, "search", StringComparison.Ordinal))
        {
            if (message.Contains("requires query", StringComparison.OrdinalIgnoreCase))
                return "missing_query";
            return "search_error";
        }

        if (string.Equals(operation, "read", StringComparison.Ordinal)
            || string.Equals(operation, "shape", StringComparison.Ordinal))
        {
            if (message.Contains("requires source_id", StringComparison.OrdinalIgnoreCase))
                return "missing_source_id";
            if (string.Equals(operation, "read", StringComparison.Ordinal)
                && message.Contains("requires line", StringComparison.OrdinalIgnoreCase))
                return "missing_line";
            if (message.Contains("matches multiple imported sources", StringComparison.OrdinalIgnoreCase))
                return "ambiguous_source";
            if (message.Contains("was not found", StringComparison.OrdinalIgnoreCase))
                return "source_not_found";
            if (message.Contains("No content corpus exists", StringComparison.OrdinalIgnoreCase))
                return "content_corpus_missing";
            if (message.Contains("requested line", StringComparison.OrdinalIgnoreCase))
                return "line_out_of_range";
            if (ex is ArgumentOutOfRangeException && message.Contains("context_lines", StringComparison.OrdinalIgnoreCase))
                return "invalid_context_lines";
            if (ex is ArgumentOutOfRangeException && message.Contains("line", StringComparison.OrdinalIgnoreCase))
                return "invalid_line";
            return string.Equals(operation, "shape", StringComparison.Ordinal) ? "shape_error" : "read_error";
        }

        return "content_error";
    }

    private static IReadOnlyList<ContentNextAction> SearchNoResultsNextActions(string query, string contentKind) =>
    [
        NextAction(
            "search",
            "widen to every indexed text kind when the expected corpus is unclear",
            ("query", query),
            ("mode", "all-text")),
        NextAction(
            "content",
            "audit registered workspace source text across workspaces only when that broad scope is intended",
            ("operation", "search"),
            ("query", query),
            ("content_kind", "source"),
            ("workspace_id", "all")),
        NextAction(
            "search",
            "use source search for current workspace source-body text",
            ("query", query),
            ("mode", "source")),
    ];

    private static IReadOnlyList<ContentNextAction> ReadRecoveryNextActions(string? sourceId)
    {
        string query = string.IsNullOrWhiteSpace(sourceId)
            ? "<query>"
            : SearchTool.Truncate(sourceId.Trim(), MaxDiagnosticArgumentChars);
        return
        [
            NextAction(
                "content",
                "find a valid source_id before reading",
                ("operation", "search"),
                ("query", query),
                ("content_kind", "external_file")),
            NextAction(
                "content",
                "list imported sources and choose an exact source_id",
                ("operation", "list"),
                ("content_kind", "external_file")),
        ];
    }

    private static ContentNextAction NextAction(string tool, string reason, params (string Key, string Value)[] args) =>
        new(tool, reason, args.Select(static arg => new KeyValuePair<string, string>(arg.Key, arg.Value)).ToArray());

    private static void AppendContentNextActions(StringBuilder sb, IReadOnlyList<ContentNextAction> actions)
    {
        if (actions.Count == 0)
            return;

        sb.Append('\n').Append("Next:");
        foreach (ContentNextAction action in actions.Take(4))
        {
            sb.Append('\n')
              .Append("  ")
              .Append(FormatContentActionCommand(action))
              .Append(" - ")
              .Append(action.Reason)
              .Append('.');
        }
    }

    private static string FormatContentActionCommand(ContentNextAction action)
    {
        var sb = new StringBuilder(action.Tool);
        string? operation = action.Args.FirstOrDefault(static arg => arg.Key == "operation").Value;
        if (string.Equals(action.Tool, "content", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(operation))
            sb.Append(' ').Append(operation);

        foreach (KeyValuePair<string, string> arg in action.Args)
        {
            if (string.Equals(action.Tool, "content", StringComparison.Ordinal)
                && string.Equals(arg.Key, "operation", StringComparison.Ordinal))
                continue;
            sb.Append(' ').Append(arg.Key).Append('=').Append(arg.Value);
        }

        return sb.ToString();
    }

    private static string RenderImportJson(ExternalContentImportResult result)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("source_id", result.SourceId);
            writer.WriteString("content_kind", result.ContentKind);
            writer.WriteString("display_path", result.DisplayPath);
            if (result.Url is null) writer.WriteNull("url");
            else writer.WriteString("url", result.Url);
            writer.WriteString("content_hash", result.ContentHash);
            writer.WriteNumber("source_bytes", result.SourceBytes);
            writer.WriteNumber("chunk_count", result.ChunkCount);
            writer.WriteBoolean("replaced", result.Replaced);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string RenderNoResultsJson(string operation, string query, string contentKind)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("operation", TruncateForJson(operation, MaxDiagnosticErrorChars));
            writer.WriteString("error", "No results.");
            writer.WriteString("diagnostic_code", "no_results");
            writer.WriteString("content_kind", contentKind);
            writer.WriteStartArray("results");
            writer.WriteEndArray();
            writer.WritePropertyName("next_actions");
            WriteNextActions(writer, SearchNoResultsNextActions(query, contentKind));
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string RenderDiagnosticJson(
        string operation,
        string error,
        string diagnosticCode,
        IReadOnlyList<ContentNextAction> nextActions)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("operation", TruncateForJson(operation, MaxDiagnosticFallbackErrorChars));
            writer.WriteString("error", TruncateForJson(error, MaxDiagnosticErrorChars));
            writer.WriteString("diagnostic_code", diagnosticCode);
            writer.WritePropertyName("next_actions");
            WriteNextActions(writer, nextActions);
            writer.WriteEndObject();
        }
        string output = Encoding.UTF8.GetString(buffer.WrittenSpan);
        return output.Length <= MaxDiagnosticOutputChars
            ? output
            : RenderMinimalDiagnosticJson(operation, error, diagnosticCode);
    }

    private static string RenderMinimalDiagnosticJson(string operation, string error, string diagnosticCode)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("operation", operation);
            writer.WriteString("error", TruncateForJson(error, MaxDiagnosticFallbackErrorChars));
            writer.WriteString("diagnostic_code", diagnosticCode);
            writer.WriteStartArray("next_actions");
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string RenderSearchJson(IReadOnlyList<TextContentSearchHit> hits, string query, string contentKind)
    {
        if (hits.Count == 0)
            return RenderNoResultsJson("search", query, contentKind);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (TextContentSearchHit hit in hits)
            {
                writer.WriteStartObject();
                writer.WriteString("source_id", hit.SourceId);
                writer.WriteString("chunk_id", hit.ChunkId);
                writer.WriteString("content_kind", hit.ContentKind);
                writer.WriteString("display_path", hit.DisplayPath);
                if (hit.Url is null) writer.WriteNull("url");
                else writer.WriteString("url", hit.Url);
                writer.WriteNumber("line", hit.Line);
                writer.WriteNumber("line_start", hit.LineStart);
                writer.WriteNumber("line_end", hit.LineEnd);
                writer.WriteNumber("score", hit.Score);
                writer.WriteString("snippet", hit.Snippet);
                writer.WriteNumber("source_bytes", hit.SourceBytes);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string RenderWorkspaceSearchJson(IReadOnlyList<WorkspaceContentSearchHit> hits, string query, string contentKind)
    {
        if (hits.Count == 0)
            return RenderNoResultsJson("search", query, contentKind);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (WorkspaceContentSearchHit workspaceHit in hits)
            {
                WorkspaceRegistryRow workspace = workspaceHit.Workspace;
                TextContentSearchHit hit = workspaceHit.Hit;
                writer.WriteStartObject();
                writer.WriteString("workspace_id", workspace.WorkspaceId);
                writer.WriteString("display_id", workspace.DisplayId);
                writer.WriteString("workspace_root", workspace.CanonicalRoot);
                writer.WriteString("source_id", hit.SourceId);
                writer.WriteString("chunk_id", hit.ChunkId);
                writer.WriteString("content_kind", hit.ContentKind);
                writer.WriteString("display_path", hit.DisplayPath);
                if (hit.Path is null) writer.WriteNull("path");
                else writer.WriteString("path", hit.Path);
                if (hit.Url is null) writer.WriteNull("url");
                else writer.WriteString("url", hit.Url);
                writer.WriteNumber("line", hit.Line);
                writer.WriteNumber("line_start", hit.LineStart);
                writer.WriteNumber("line_end", hit.LineEnd);
                writer.WriteNumber("score", hit.Score);
                writer.WriteString("snippet", hit.Snippet);
                writer.WriteNumber("source_bytes", hit.SourceBytes);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string RenderReadJson(ExternalContentReadResult result)
    {
        var lines = result.Lines
            .Select(static line =>
            {
                string text = TruncateForJson(line.Text, MaxReadLineUnits);
                return new RenderedReadLine(
                    line.LineNumber,
                    text,
                    !string.Equals(text, line.Text, StringComparison.Ordinal));
            })
            .ToArray();
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("source_id", result.SourceId);
            writer.WriteString("display_path", TruncateForJson(result.DisplayPath, MaxReadDisplayPathUnits));
            writer.WriteNumber("line_start", result.LineStart);
            writer.WriteNumber("line_end", result.LineEnd);
            writer.WriteNumber("truncated_line_count", lines.Count(static line => line.Truncated));
            writer.WriteStartArray("lines");
            foreach (RenderedReadLine line in lines)
            {
                writer.WriteStartObject();
                writer.WriteNumber("line", line.LineNumber);
                writer.WriteString("text", line.Text);
                writer.WriteBoolean("truncated", line.Truncated);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string RenderListJson(ExternalContentInventory inventory)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", 2);
            writer.WriteNumber("per_kind_limit", inventory.PerKindLimit);
            writer.WriteNumber("total_count", inventory.TotalCount);
            writer.WriteNumber("returned_count", inventory.ReturnedCount);
            writer.WriteNumber("omitted_count", inventory.OmittedCount);
            writer.WriteStartArray("kinds");
            foreach (ExternalContentKindInventory kind in inventory.Kinds)
            {
                writer.WriteStartObject();
                writer.WriteString("content_kind", kind.ContentKind);
                writer.WriteNumber("total_count", kind.TotalCount);
                writer.WriteNumber("returned_count", kind.ReturnedCount);
                writer.WriteNumber("omitted_count", kind.OmittedCount);
                writer.WriteStartArray("sources");
                foreach (ExternalContentSource source in kind.Sources)
                {
                    writer.WriteStartObject();
                    writer.WriteString("source_id", source.SourceId);
                    writer.WriteString("content_kind", source.ContentKind);
                    writer.WriteString(
                        "display_path",
                        TruncateForJson(source.DisplayPath, MaxInventoryDisplayPathChars));
                    if (source.Url is null) writer.WriteNull("url");
                    else writer.WriteString("url", TruncateForJson(source.Url, MaxInventoryUrlChars));
                    writer.WriteString("content_hash", source.ContentHash);
                    writer.WriteNumber("source_bytes", source.SourceBytes);
                    writer.WriteNumber("line_count", source.LineCount);
                    writer.WriteNumber("chunk_count", source.ChunkCount);
                    writer.WriteString("indexed_at_utc", source.IndexedAtUtc);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string RenderShapeJson(ExternalContentShape shape)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", 2);
            writer.WriteString("source_id", shape.SourceId);
            writer.WriteString("content_kind", shape.ContentKind);
            writer.WriteString(
                "display_path",
                TruncateForJson(shape.DisplayPath, MaxInventoryDisplayPathChars));
            writer.WriteNumber("source_bytes", shape.SourceBytes);
            writer.WriteNumber("line_count", shape.LineCount);
            writer.WriteString("severity_basis", "text_derived");
            writer.WriteStartObject("severity");
            writer.WriteNumber("fatal", shape.Severity.Fatal);
            writer.WriteNumber("error", shape.Severity.Error);
            writer.WriteNumber("warning", shape.Severity.Warning);
            writer.WriteNumber("info", shape.Severity.Info);
            writer.WriteNumber("debug", shape.Severity.Debug);
            writer.WriteNumber("other", shape.Severity.Other);
            writer.WriteEndObject();
            WriteShapeLines(writer, "head", shape.Head);
            WriteShapeLines(writer, "tail", shape.Tail);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteShapeLines(
        Utf8JsonWriter writer,
        string propertyName,
        IReadOnlyList<ExternalContentLine> lines)
    {
        writer.WriteStartArray(propertyName);
        foreach (ExternalContentLine line in lines)
        {
            writer.WriteStartObject();
            writer.WriteNumber("line", line.LineNumber);
            writer.WriteString("text", TruncateForJson(line.Text, MaxShapeLineChars));
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static string RenderRemoveJson(ExternalContentRemoveResult result)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("source_id", result.SourceId);
            writer.WriteBoolean("removed", result.Removed);
            writer.WriteNumber("source_count", result.SourceCount);
            writer.WriteNumber("chunk_count", result.ChunkCount);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteNextActions(Utf8JsonWriter writer, IReadOnlyList<ContentNextAction> actions)
    {
        writer.WriteStartArray();
        foreach (ContentNextAction action in actions.Take(4))
        {
            writer.WriteStartObject();
            writer.WriteString("tool", action.Tool);
            writer.WriteString("reason", TruncateForJson(action.Reason, MaxDiagnosticReasonChars));
            writer.WritePropertyName("args");
            writer.WriteStartObject();
            foreach (KeyValuePair<string, string> arg in action.Args)
                writer.WriteString(arg.Key, TruncateForJson(arg.Value, MaxDiagnosticArgumentChars));
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static Utf8JsonWriter JsonWriter(ArrayBufferWriter<byte> buffer) =>
        new(buffer, new JsonWriterOptions { Encoder = ContentJsonEncoder });

    private sealed record WorkspaceContentSearchHit(WorkspaceRegistryRow Workspace, TextContentSearchHit Hit);
}
