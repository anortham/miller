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

    public ContentTool(WorkspaceContext workspace, ContentCorpusExternalStore store)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(store);
        _workspace = workspace;
        _store = store;
    }

    [McpServerTool(Name = "content")]
    [Description(
        "Import, search, read, list, remove, and export text in Miller's content corpus. Use for logs, " +
        "CI output, web markdown, reports, large text files, and Eros JSONL chunk feeds.")]
    public string Content(
        [Description("import|add_markdown|search|read|list|remove|export.")] string operation,
        [Description("Path to import for operation=import/add_markdown.")] string? path = null,
        [Description("Search query for operation=search.")] string? query = null,
        [Description("Imported source id for operation=read/remove.")] string? source_id = null,
        [Description("URL metadata for operation=add_markdown with web content.")] string? url = null,
        [Description("Human display path/title for imported content. Optional.")] string? display_path = null,
        [Description("Content kind for search/list/export. Default external_file for search/list, all for export.")] string? content_kind = null,
        [Description("Stored workspace_id filter for operation=export. Optional.")] string? content_workspace_id = null,
        [Description("Workspace selector for search; use all for registered workspace search. Optional.")] string? workspace_id = null,
        [Description("1-based center line for operation=read.")] int? line = null,
        [Description("Context lines before/after the read line. Default 10, maximum bounded by Miller.")] int? context_lines = null,
        [Description("Max search results. Default 6.")] int limit = SearchTool.DefaultLimit,
        [Description("Max import bytes. Required to intentionally import files over the default cap.")] long? max_bytes = null,
        [Description("Output format: compact|json. Default compact.")] string format = "compact")
    {
        var telemetry = TelemetryContext.Current;
        try
        {
            string contentDbPath = ContentCorpusSidecar.ContentDbPathFor(_workspace.ExtractDbPath);
            bool json = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);
            string op = string.IsNullOrWhiteSpace(operation) ? "list" : operation.Trim().ToLowerInvariant();

            return op switch
            {
                "import" or "add" => Import(contentDbPath, path, max_bytes, display_path, json, telemetry),
                "add_markdown" or "add-markdown" or "import_markdown" or "import-markdown" =>
                    AddMarkdown(contentDbPath, path, url, max_bytes, display_path, json, telemetry),
                "search" => Search(contentDbPath, query, ContentKindOrDefault(content_kind, TextContentKind.ExternalFile), limit, workspace_id, json, telemetry),
                "read" => Read(contentDbPath, source_id, line, context_lines, json, telemetry),
                "list" => List(contentDbPath, ContentKindOrDefault(content_kind, TextContentKind.ExternalFile), json, telemetry),
                "remove" or "delete" => Remove(contentDbPath, source_id, json, telemetry),
                "export" => Export(contentDbPath, OptionalContentKind(content_kind), content_workspace_id, telemetry),
                _ => throw new InvalidOperationException("content operation must be import, add_markdown, search, read, list, remove, or export."),
            };
        }
        catch (Exception ex)
        {
            if (telemetry is not null)
            {
                telemetry.Outcome = TelemetryOutcome.Error;
                telemetry.ErrorKind = ex.GetType().Name;
            }
            return $"content failed: {ex.Message}";
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
        }

        return json ? RenderSearchJson(hits) : RenderSearchCompact(hits);
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
        }

        return json ? RenderWorkspaceSearchJson(page) : RenderWorkspaceSearchCompact(page);
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
        int? line,
        int? contextLines,
        bool json,
        TelemetryScope? telemetry)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
            throw new InvalidOperationException("content read requires source_id.");
        if (line is null)
            throw new InvalidOperationException("content read requires line.");

        ExternalContentReadResult result = _store.ReadWindow(
            contentDbPath,
            sourceId,
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

    private string List(string contentDbPath, string contentKind, bool json, TelemetryScope? telemetry)
    {
        IReadOnlyList<ExternalContentSource> sources = _store.List(contentDbPath, contentKind);
        if (telemetry is not null)
        {
            telemetry.ResultCount = sources.Count;
            telemetry.Outcome = sources.Count == 0 ? TelemetryOutcome.Empty : TelemetryOutcome.Ok;
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

    private static string RenderSearchCompact(IReadOnlyList<TextContentSearchHit> hits)
    {
        if (hits.Count == 0)
            return "No results.";

        var blocks = new List<string>(hits.Count);
        foreach (TextContentSearchHit hit in hits)
        {
            var block = new StringBuilder();
            block.Append(hit.DisplayPath).Append(':').Append(hit.Line).Append("  ").Append(hit.ContentKind);
            foreach (string line in hit.Snippet.Split('\n'))
                block.Append('\n').Append("    ").Append(line);
            blocks.Add(block.ToString());
        }

        return string.Join("\n\n", blocks);
    }

    private static string RenderWorkspaceSearchCompact(IReadOnlyList<WorkspaceContentSearchHit> hits)
    {
        if (hits.Count == 0)
            return "No results.";

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
                .Append(hit.ContentKind);
            foreach (string line in hit.Snippet.Split('\n'))
                block.Append('\n').Append("    ").Append(line);
            blocks.Add(block.ToString());
        }

        return string.Join("\n\n", blocks);
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

    private static string RenderSearchJson(IReadOnlyList<TextContentSearchHit> hits)
    {
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

    private static string RenderWorkspaceSearchJson(IReadOnlyList<WorkspaceContentSearchHit> hits)
    {
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

    private static Utf8JsonWriter JsonWriter(ArrayBufferWriter<byte> buffer) =>
        new(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private sealed record WorkspaceContentSearchHit(WorkspaceRegistryRow Workspace, TextContentSearchHit Hit);
}
