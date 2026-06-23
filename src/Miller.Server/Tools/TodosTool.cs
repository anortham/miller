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
public sealed class TodosTool
{
    internal const int DefaultLimit = 50;
    internal const int MaxLimit = 500;
    private static readonly string[] DefaultMarkers = ["TODO", "FIXME", "HACK", "XXX"];
    private static readonly HashSet<string> AllowedMarkers = new(DefaultMarkers, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> CommentKinds = new(StringComparer.Ordinal) { "comment", "doc_comment" };

    private readonly IWorkspaceRegionSearchProvider _regionProvider;

    public TodosTool(IWorkspaceRegionSearchProvider regionProvider)
    {
        ArgumentNullException.ThrowIfNull(regionProvider);
        _regionProvider = regionProvider;
    }

    [McpServerTool(Name = "todos")]
    [Description(
        "List TODO/FIXME/HACK/XXX markers from comment and doc-comment source regions. Returns marker, file:line, " +
        "snippet, and containing symbol when available.")]
    public string Todos(
        [Description("Comma-separated markers to list. Default TODO,FIXME,HACK,XXX.")] string? markers = null,
        [Description("Workspace-relative glob filter, e.g. src/ui/**. Optional.")] string? file_pattern = null,
        [Description("Comma-separated language filter, e.g. csharp,typescript. Optional.")] string? language = null,
        [Description("Hide test code. Default false.")] bool exclude_tests = false,
        [Description("Max marker hits. Default 50, maximum 500.")] int limit = DefaultLimit,
        [Description("Output format: compact|json. Default compact.")] string format = "compact",
        [Description("Workspace selector: display_id, unique prefix, full id, registered root path, current, or primary.")]
        string? workspace_id = null,
        [Description("Refresh selected workspace before reading. Defaults true when workspace_id is supplied.")]
        bool? ensure_fresh = null)
    {
        TelemetryScope? telemetry = TelemetryContext.Current;
        try
        {
            bool json = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);
            IReadOnlyList<string> markerList = ParseMarkers(markers);
            bool ensureFresh = ReadToolWorkspaceRouting.ResolveEnsureFresh(workspace_id, ensure_fresh);

            WorkspaceRegionSearchContext context = _regionProvider.ResolveRegionSearch(workspace_id, ensureFresh);
            string? compactBanner = ReadToolWorkspaceRouting.CompactBanner(context, workspace_id, json);
            string output = Run(
                context.Index,
                markerList,
                limit,
                exclude_tests,
                json,
                compactBanner,
                file_pattern,
                language,
                out int count);

            if (telemetry is not null)
            {
                telemetry.Op = "list";
                telemetry.SetTarget(string.Join(",", markerList));
                telemetry.ResultCount = count;
                telemetry.Outcome = count == 0 ? TelemetryOutcome.Empty : TelemetryOutcome.Ok;
                if (count == 0)
                    telemetry.SetEmptyReason("no_todo_markers");
                ReadToolWorkspaceRouting.ApplyTelemetry(telemetry, context);
            }

            return output;
        }
        catch (Exception ex)
        {
            if (telemetry is not null)
            {
                telemetry.Outcome = TelemetryOutcome.Error;
                telemetry.SetError(ex);
            }
            return $"todos failed: {ex.Message}";
        }
    }

    internal static string Run(
        IRegionSearchIndex index,
        IReadOnlyList<string> markers,
        int limit,
        bool excludeTests,
        bool json,
        string? compactBanner,
        string? filePattern,
        string? language,
        out int renderedCount)
    {
        int boundedLimit = Math.Clamp(limit, 1, MaxLimit);
        IReadOnlyList<TodoMarkerHit> hits = FindMarkers(
            index,
            markers,
            boundedLimit,
            excludeTests,
            filePattern,
            language);
        renderedCount = hits.Count;
        return json
            ? RenderJson(hits)
            : RenderCompact(hits, markers, compactBanner);
    }

    internal static IReadOnlyList<TodoMarkerHit> FindMarkers(
        IRegionSearchIndex index,
        IReadOnlyList<string> markers,
        int limit,
        bool excludeTests,
        string? filePattern,
        string? language)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(markers);
        if (limit < 1) limit = 1;

        ToolSearchFilters filters = ToolSearchFilters.Parse(filePattern, language);
        var byKey = new Dictionary<string, TodoMarkerHit>(StringComparer.Ordinal);
        int fetchLimit = filters.HasAny ? MaxLimit : Math.Min(limit * 4 + 10, MaxLimit);

        foreach (string marker in markers)
        {
            IReadOnlyList<RegionSearchHit> hits = index.Search(marker, CommentKinds, fetchLimit, excludeTests);
            foreach (RegionSearchHit hit in hits)
            {
                if (!filters.Allows(hit.Path, hit.Language))
                    continue;
                if (!ContainsMarker(hit.RawText, marker) && !ContainsMarker(hit.Snippet, marker))
                    continue;

                string key = marker + "\0" + hit.RegionId;
                byKey.TryAdd(key, new TodoMarkerHit(marker, hit));
            }
        }

        return byKey.Values
            .OrderBy(static hit => hit.Region.Path, StringComparer.Ordinal)
            .ThenBy(static hit => hit.Region.Line)
            .ThenBy(static hit => Array.IndexOf(DefaultMarkers, hit.Marker))
            .ThenBy(static hit => hit.Marker, StringComparer.Ordinal)
            .Take(Math.Min(limit, MaxLimit))
            .ToArray();
    }

    internal static IReadOnlyList<string> ParseMarkers(string? markers)
    {
        if (string.IsNullOrWhiteSpace(markers))
            return DefaultMarkers;

        string[] parts = markers
            .Split(new[] { ',', ';', ' ', '\t', '\n', '\r' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static marker => marker.ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (parts.Length == 0)
            return DefaultMarkers;

        foreach (string marker in parts)
        {
            if (!AllowedMarkers.Contains(marker))
                throw new InvalidOperationException("markers must be TODO, FIXME, HACK, or XXX.");
        }
        return parts;
    }

    private static bool ContainsMarker(string text, string marker)
    {
        int start = 0;
        while (start < text.Length)
        {
            int index = text.IndexOf(marker, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                return false;

            int before = index - 1;
            int after = index + marker.Length;
            bool leftBoundary = before < 0 || !IsMarkerWordChar(text[before]);
            bool rightBoundary = after >= text.Length || !IsMarkerWordChar(text[after]);
            if (leftBoundary && rightBoundary)
                return true;

            start = index + marker.Length;
        }
        return false;
    }

    private static bool IsMarkerWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static string RenderCompact(
        IReadOnlyList<TodoMarkerHit> hits,
        IReadOnlyList<string> markers,
        string? compactBanner)
    {
        if (hits.Count == 0)
        {
            string markerText = string.Join("/", markers);
            return ReadToolWorkspaceRouting.PrefixCompact($"No {markerText} markers.", compactBanner);
        }

        var blocks = new List<string>(hits.Count);
        foreach (TodoMarkerHit hit in hits)
        {
            RegionSearchHit region = hit.Region;
            var block = new StringBuilder();
            block.Append(region.Path).Append(':').Append(region.Line)
                .Append("  ").Append(hit.Marker)
                .Append("  ").Append(region.Kind);
            if (!string.IsNullOrWhiteSpace(region.ContainingSymbolName))
                block.Append("  ").Append(region.ContainingSymbolName);
            foreach (string line in region.Snippet.Split('\n'))
                block.Append('\n').Append("    ").Append(line);
            blocks.Add(block.ToString());
        }

        string body = string.Join("\n\n", blocks);
        return ReadToolWorkspaceRouting.PrefixCompact(body, compactBanner);
    }

    private static string RenderJson(IReadOnlyList<TodoMarkerHit> hits)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(
            buffer,
            new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartArray();
            foreach (TodoMarkerHit hit in hits)
            {
                RegionSearchHit region = hit.Region;
                writer.WriteStartObject();
                writer.WriteString("marker", hit.Marker);
                writer.WriteString("file", region.Path);
                writer.WriteNumber("line", region.Line);
                writer.WriteString("kind", region.Kind);
                writer.WriteString("language", region.Language);
                writer.WriteString("snippet", region.Snippet);
                writer.WriteString("region_id", region.RegionId);
                if (region.ContainingSymbolId is null) writer.WriteNull("containing_symbol_id");
                else writer.WriteString("containing_symbol_id", region.ContainingSymbolId);
                if (region.ContainingSymbolName is null) writer.WriteNull("containing_symbol_name");
                else writer.WriteString("containing_symbol_name", region.ContainingSymbolName);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}

internal sealed record TodoMarkerHit(string Marker, RegionSearchHit Region);
