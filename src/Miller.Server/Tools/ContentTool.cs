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

[McpServerToolType]
public sealed class ContentTool
{
    private readonly WorkspaceContext _workspace;
    private readonly ContentCorpusExternalStore _store;
    private readonly ContentCorpusExportReader _exportReader = new();

    private sealed record ContentNextAction(
        string Tool,
        string Reason,
        IReadOnlyList<KeyValuePair<string, string>> Args);

    public ContentTool(WorkspaceContext workspace, ContentCorpusExternalStore store)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(store);
        _workspace = workspace;
        _store = store;
    }

    [McpServerTool(Name = "content")]
    [Description(
        "Import, search, read, list, remove, and export text in Miller's content corpus: logs, CI output, web " +
        "markdown, reports, large text files, JSONL feeds. Search hits carry a source_id; pass it to read for a " +
        "bounded line window instead of loading the whole file. Use for any big non-workspace text you'd " +
        "otherwise cat into context. NOT for: workspace source/docs text (search mode=source or mode=content) or " +
        "code symbols (search/inspect). Example: content operation=import path=/tmp/ci.log then content " +
        "operation=search query=\"first failing test\".")]
    public string Content(
        [Description("import|add_markdown|search|read|list|remove|export. Default list.")] string? operation = "list",
        [Description("Path to import for operation=import/add_markdown.")] string? path = null,
        [Description("Search query for operation=search.")] string? query = null,
        [Description("Imported source id for operation=read/remove, of the form external_file:<hash> or web:<hash>. Get it from each `content search` hit or `content list`; a unique display_path is also accepted for read.")] string? source_id = null,
        [Description("URL metadata for operation=add_markdown with web content.")] string? url = null,
        [Description("Human display path/title for imported content. Optional.")] string? display_path = null,
        [Description("Content kind for search/list/export. Default external_file for search/list, all for export.")] string? content_kind = null,
        [Description("Stored workspace_id filter for operation=export. Optional.")] string? content_workspace_id = null,
        [Description("Workspace selector for search/read. Use all only for registered workspace search. Optional.")] string? workspace_id = null,
        [Description("1-based center line for operation=read.")] int? line = null,
        [Description("Context lines before/after the read line. Default 10; total window capped at 200 lines (MaxReadWindowLines); larger is rejected.")] int? context_lines = null,
        [Description("Max search results. Default 6.")] int limit = SearchTool.DefaultLimit,
        [Description("Max import bytes. Required to intentionally import files over the default cap.")] long? max_bytes = null,
        [Description("Output format: compact|json. Default compact.")] string format = "compact")
    {
        var telemetry = TelemetryContext.Current;
        bool json = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);
        string op = string.IsNullOrWhiteSpace(operation) ? "list" : operation.Trim().ToLowerInvariant();
        try
        {
            string contentDbPath = ContentCorpusSidecar.ContentDbPathFor(_workspace.ExtractDbPath);
            if (telemetry is not null)
                telemetry.Op = op;

            return op switch
            {
                "import" or "add" => Import(contentDbPath, path, max_bytes, display_path, json, telemetry),
                "add_markdown" or "add-markdown" or "import_markdown" or "import-markdown" =>
                    AddMarkdown(contentDbPath, path, url, max_bytes, display_path, json, telemetry),
                "search" => Search(contentDbPath, query, ContentKindOrDefault(content_kind, TextContentKind.ExternalFile), limit, workspace_id, json, telemetry),
                "read" => Read(contentDbPath, source_id, workspace_id, line, context_lines, json, telemetry),
                "list" => List(contentDbPath, ContentKindOrDefault(content_kind, TextContentKind.ExternalFile), json, telemetry),
                "remove" or "delete" => Remove(contentDbPath, source_id, json, telemetry),
                "export" => Export(contentDbPath, OptionalContentKind(content_kind), content_workspace_id, telemetry),
                _ => throw new InvalidOperationException("content operation must be import, add_markdown, search, read, list, remove, or export."),
            };
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
            return json
                ? RenderDiagnosticJson(op, ex.Message, diagnosticCode, ReadRecoveryNextActions(source_id))
                : RenderDiagnosticCompact(op, ex.Message, diagnosticCode, ReadRecoveryNextActions(source_id));
        }
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

        string readContentDbPath = ResolveReadContentDbPath(contentDbPath, sourceId, workspaceId);
        string resolvedSourceId = ResolveReadSourceId(readContentDbPath, sourceId);
        readContentDbPath = ResolveReadContentDbPath(readContentDbPath, resolvedSourceId, workspaceId: null);
        ExternalContentReadResult result = _store.ReadWindow(
            readContentDbPath,
            resolvedSourceId,
            line.Value,
            contextLines ?? ContentCorpusExternalStore.DefaultContextLines);
        if (telemetry is not null)
        {
            telemetry.SetTarget(sourceId);
            telemetry.ResultCount = result.Lines.Count;
            telemetry.Outcome = result.Lines.Count == 0 ? TelemetryOutcome.Empty : TelemetryOutcome.Ok;
        }

        return json ? RenderReadJson(result) : RenderReadCompact(result);
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

    private string ResolveReadContentDbPath(string defaultContentDbPath, string sourceId, string? workspaceId)
    {
        if (TryResolveSourceIdWorkspaceContentDbPath(sourceId) is { } routedContentDbPath)
            return routedContentDbPath;

        if (!string.IsNullOrWhiteSpace(workspaceId))
            return ResolveReadWorkspaceContentDbPath(defaultContentDbPath, workspaceId);

        return defaultContentDbPath;
    }

    private string? TryResolveSourceIdWorkspaceContentDbPath(string sourceId)
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
            return row is null
                ? null
                : ContentCorpusSidecar.ContentDbPathFor(row.IndexDbPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            return null;
        }
    }

    private string ResolveReadWorkspaceContentDbPath(string defaultContentDbPath, string workspaceId)
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
            return defaultContentDbPath;
        }

        using var registry = WorkspaceRegistry.Open(_workspace.RegistryDbPath);
        WorkspaceRegistryRow row = WorkspaceRegistrySelector.Resolve(registry, selector);
        return ContentCorpusSidecar.ContentDbPathFor(row.IndexDbPath);
    }

    private string List(string contentDbPath, string contentKind, bool json, TelemetryScope? telemetry)
    {
        IReadOnlyList<ExternalContentSource> sources = _store.List(contentDbPath, contentKind);
        if (telemetry is not null)
        {
            telemetry.ResultCount = sources.Count;
            telemetry.Outcome = sources.Count == 0 ? TelemetryOutcome.Empty : TelemetryOutcome.Ok;
            telemetry.SetMetadata("content_kind", contentKind);
            telemetry.SetMetadata("format", json ? "json" : "compact");
            if (sources.Count == 0)
                telemetry.SetEmptyReason("no_imported_content");
        }

        return json ? RenderListJson(sources) : RenderListCompact(sources);
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

    private string Export(
        string contentDbPath,
        string? contentKind,
        string? contentWorkspaceId,
        TelemetryScope? telemetry)
    {
        IReadOnlyList<ContentCorpusExportRow> rows = _exportReader.Read(contentDbPath, contentKind, contentWorkspaceId);
        if (telemetry is not null)
        {
            telemetry.SetTarget(contentWorkspaceId ?? contentKind ?? "all");
            telemetry.ResultCount = rows.Count;
            telemetry.SourceBytes = rows
                .GroupBy(static row => row.SourceId, StringComparer.Ordinal)
                .Sum(static group => group.Max(static row => row.SourceBytes));
            telemetry.Outcome = rows.Count == 0 ? TelemetryOutcome.Empty : TelemetryOutcome.Ok;
            telemetry.SetMetadata("content_kind", contentKind ?? "all");
            if (rows.Count == 0)
                telemetry.SetEmptyReason("no_export_rows");
        }

        return ContentCorpusExportReader.ToJsonLines(rows);
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
            _ => throw new InvalidOperationException("content_kind must be all, workspace_source, workspace_docs, workspace_config, external_file, or web."),
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

        var blocks = new List<string>(hits.Count);
        foreach (TextContentSearchHit hit in hits)
        {
            var block = new StringBuilder();
            block.Append(hit.DisplayPath).Append(':').Append(hit.Line)
                .Append("  ").Append(hit.ContentKind)
                .Append("  source_id=").Append(hit.SourceId);
            foreach (string line in hit.Snippet.Split('\n'))
                block.Append('\n').Append("  ").Append(line);
            blocks.Add(block.ToString());
        }

        TextContentSearchHit first = hits[0];
        return string.Join("\n\n", blocks)
            + "\n\nread: content read source_id=" + first.SourceId + " line=" + first.Line;
    }

    private static string RenderWorkspaceSearchCompact(IReadOnlyList<WorkspaceContentSearchHit> hits, string query, string contentKind)
    {
        if (hits.Count == 0)
            return RenderNoResultsCompact("search", query, contentKind);

        var blocks = new List<string>(hits.Count);
        foreach (WorkspaceContentSearchHit workspaceHit in hits)
        {
            TextContentSearchHit hit = workspaceHit.Hit;
            var block = new StringBuilder();
            block.Append(workspaceHit.Workspace.DisplayId)
                .Append(" (")
                .Append(workspaceHit.Workspace.WorkspaceId)
                .Append(")  ")
                .Append(hit.DisplayPath)
                .Append(':')
                .Append(hit.Line)
                .Append("  ")
                .Append(hit.ContentKind)
                .Append("  source_id=")
                .Append(hit.SourceId);
            foreach (string line in hit.Snippet.Split('\n'))
                block.Append('\n').Append("  ").Append(line);
            blocks.Add(block.ToString());
        }

        WorkspaceContentSearchHit first = hits[0];
        return string.Join("\n\n", blocks)
            + "\n\nread: content read source_id=" + first.Hit.SourceId
            + " line=" + first.Hit.Line
            + " workspace_id=" + first.Workspace.WorkspaceId;
    }

    private static string RenderReadCompact(ExternalContentReadResult result)
    {
        var sb = new StringBuilder();
        sb.Append(result.DisplayPath).Append(':').Append(result.LineStart).Append('-').Append(result.LineEnd);
        foreach (ExternalContentLine line in result.Lines)
            sb.Append('\n').Append("    ").Append(line.LineNumber).Append(": ").Append(line.Text);
        return sb.ToString();
    }

    private static string RenderListCompact(IReadOnlyList<ExternalContentSource> sources)
    {
        if (sources.Count == 0)
            return "No imported content.";

        var lines = new List<string>(sources.Count);
        foreach (ExternalContentSource source in sources)
        {
            lines.Add(
                $"{source.SourceId}  {source.ContentKind}  {source.SourceBytes} bytes  " +
                $"{source.ChunkCount} chunks  {source.DisplayPath}");
        }

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
        sb.Append(" failed: ").Append(error)
          .Append('\n')
          .Append("diagnostic_code=").Append(diagnosticCode);
        AppendContentNextActions(sb, nextActions);
        return sb.ToString();
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

        if (string.Equals(operation, "read", StringComparison.Ordinal))
        {
            if (message.Contains("requires source_id", StringComparison.OrdinalIgnoreCase))
                return "missing_source_id";
            if (message.Contains("requires line", StringComparison.OrdinalIgnoreCase))
                return "missing_line";
            if (message.Contains("matches multiple imported sources", StringComparison.OrdinalIgnoreCase))
                return "ambiguous_source";
            if (message.Contains("was not found", StringComparison.OrdinalIgnoreCase))
                return "source_not_found";
            if (message.Contains("No content corpus exists", StringComparison.OrdinalIgnoreCase))
                return "content_corpus_missing";
            if (message.Contains("maximum is", StringComparison.OrdinalIgnoreCase))
                return "read_window_too_large";
            if (message.Contains("requested line", StringComparison.OrdinalIgnoreCase))
                return "line_out_of_range";
            if (ex is ArgumentOutOfRangeException && message.Contains("context_lines", StringComparison.OrdinalIgnoreCase))
                return "invalid_context_lines";
            if (ex is ArgumentOutOfRangeException && message.Contains("line", StringComparison.OrdinalIgnoreCase))
                return "invalid_line";
            return "read_error";
        }

        return "content_error";
    }

    private static IReadOnlyList<ContentNextAction> SearchNoResultsNextActions(string query, string contentKind) =>
    [
        NextAction(
            "content",
            "retry against all indexed text kinds when the expected corpus is unclear",
            ("operation", "search"),
            ("query", query),
            ("content_kind", "all-text")),
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
        string query = string.IsNullOrWhiteSpace(sourceId) ? "<query>" : sourceId.Trim();
        return
        [
            NextAction(
                "content",
                "find a valid source_id before reading",
                ("operation", "search"),
                ("query", query),
                ("content_kind", "all-text")),
            NextAction(
                "content",
                "list imported sources and choose an exact source_id",
                ("operation", "list"),
                ("content_kind", "all-text")),
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
            writer.WriteString("operation", operation);
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
            writer.WriteString("operation", operation);
            writer.WriteString("error", error);
            writer.WriteString("diagnostic_code", diagnosticCode);
            writer.WritePropertyName("next_actions");
            WriteNextActions(writer, nextActions);
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
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("source_id", result.SourceId);
            writer.WriteString("display_path", result.DisplayPath);
            writer.WriteNumber("line_start", result.LineStart);
            writer.WriteNumber("line_end", result.LineEnd);
            writer.WriteStartArray("lines");
            foreach (ExternalContentLine line in result.Lines)
            {
                writer.WriteStartObject();
                writer.WriteNumber("line", line.LineNumber);
                writer.WriteString("text", line.Text);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string RenderListJson(IReadOnlyList<ExternalContentSource> sources)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = JsonWriter(buffer))
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
            writer.WriteString("reason", action.Reason);
            writer.WritePropertyName("args");
            writer.WriteStartObject();
            foreach (KeyValuePair<string, string> arg in action.Args)
                writer.WriteString(arg.Key, arg.Value);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static Utf8JsonWriter JsonWriter(ArrayBufferWriter<byte> buffer) =>
        new(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private sealed record WorkspaceContentSearchHit(WorkspaceRegistryRow Workspace, TextContentSearchHit Hit);
}
