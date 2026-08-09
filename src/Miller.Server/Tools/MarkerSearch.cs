using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Miller.Core.Search;
using Miller.Indexing;
using Miller.Indexing.Reads;

namespace Miller.Server.Tools;

internal static class MarkerSearch
{
    internal const int DefaultLimit = 50;
    internal const int MaxLimit = 500;
    private static readonly string[] DefaultMarkers = ["TODO", "FIXME", "HACK", "XXX"];
    private static readonly HashSet<string> AllowedMarkers = new(DefaultMarkers, StringComparer.OrdinalIgnoreCase);
    internal static string Run(
        WorkspaceReadHandle dbPath,
        IReadOnlyList<string> markers,
        int limit,
        bool excludeTests,
        bool json,
        string? compactBanner,
        string? filePattern,
        string? language,
        out int renderedCount) =>
        Run(
            dbPath,
            markers,
            limit,
            excludeTests,
            json,
            compactBanner,
            filePattern,
            language,
            boundAgentOutput: false,
            out renderedCount);

    internal static string Run(
        WorkspaceReadHandle dbPath,
        IReadOnlyList<string> markers,
        int limit,
        bool excludeTests,
        bool json,
        string? compactBanner,
        string? filePattern,
        string? language,
        bool boundAgentOutput,
        out int renderedCount)
    {
        int boundedLimit = Math.Clamp(limit, 1, MaxLimit);
        IReadOnlyList<MarkerSearchHit> hits = FindMarkers(
            dbPath,
            markers,
            boundedLimit,
            excludeTests,
            filePattern,
            language);
        renderedCount = hits.Count;
        return json
            ? RenderJson(hits, boundAgentOutput)
            : RenderCompact(hits, markers, compactBanner, boundAgentOutput);
    }

    internal static IReadOnlyList<MarkerSearchHit> FindMarkers(
        WorkspaceReadHandle dbPath,
        IReadOnlyList<string> markers,
        int limit,
        bool excludeTests,
        string? filePattern,
        string? language)
    {
        ArgumentNullException.ThrowIfNull(dbPath);
        ArgumentNullException.ThrowIfNull(markers);
        if (limit < 1) limit = 1;

        ToolSearchFilters filters = ToolSearchFilters.Parse(filePattern, language);
        HashSet<string> wanted = new(markers, StringComparer.OrdinalIgnoreCase);
        return MarkerFactReader.Read(
                dbPath,
                excludeTests,
                Math.Min(limit, MaxLimit),
                hit => wanted.Contains(hit.Marker) && filters.Allows(hit.Path, hit.Language))
            .Select(static hit => new MarkerSearchHit(
                [hit.Marker.ToUpperInvariant()],
                new RegionSearchHit(
                    hit.Path,
                    1.0,
                    hit.StartLine,
                    hit.NodeKind,
                    MarkerSnippet(hit),
                    MarkerSnippet(hit),
                    hit.FactId,
                    hit.ContainingSymbolId,
                    hit.ContainingSymbolName,
                    hit.Language)))
            .OrderBy(static hit => hit.Region.Path, StringComparer.Ordinal)
            .ThenBy(static hit => hit.Region.Line)
            .ThenBy(static hit => Array.IndexOf(DefaultMarkers, hit.Markers[0]))
            .ThenBy(static hit => hit.Markers[0], StringComparer.Ordinal)
            .Take(Math.Min(limit, MaxLimit))
            .ToArray();
    }

    private static string MarkerSnippet(MarkerFactRow hit) =>
        hit.Description is null
            ? hit.Marker
            : hit.Owner is null
                ? $"{hit.Marker}: {hit.Description}"
                : $"{hit.Marker}({hit.Owner}): {hit.Description}";

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

    private static string RenderCompact(
        IReadOnlyList<MarkerSearchHit> hits,
        IReadOnlyList<string> markers,
        string? compactBanner,
        bool boundAgentOutput)
    {
        if (hits.Count == 0)
        {
            string markerText = string.Join("/", markers);
            return ReadToolWorkspaceRouting.PrefixCompact($"No {markerText} markers.", compactBanner);
        }

        var blocks = new List<string>(hits.Count);
        foreach (MarkerSearchHit hit in hits)
        {
            RegionSearchHit region = hit.Region;
            var block = new StringBuilder();
            block.Append(region.Path).Append(':').Append(region.Line)
                .Append("  ").Append(string.Join(",", hit.Markers))
                .Append("  ").Append(region.Kind);
            if (!string.IsNullOrWhiteSpace(region.ContainingSymbolName))
                block.Append("  ").Append(region.ContainingSymbolName);
            string snippet = ToolOutputBudget.BoundSearchSnippet(
                region.Snippet,
                boundAgentOutput,
                out _);
            foreach (string line in snippet.Split('\n'))
                block.Append('\n').Append("    ").Append(line);
            blocks.Add(block.ToString());
        }

        string body = string.Join("\n\n", blocks);
        return ReadToolWorkspaceRouting.PrefixCompact(body, compactBanner);
    }

    private static string RenderJson(
        IReadOnlyList<MarkerSearchHit> hits,
        bool boundAgentOutput)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(
            buffer,
            new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartArray();
            foreach (MarkerSearchHit hit in hits)
            {
                RegionSearchHit region = hit.Region;
                writer.WriteStartObject();
                writer.WriteString("marker", hit.Markers[0]);
                writer.WriteStartArray("markers");
                foreach (string marker in hit.Markers)
                    writer.WriteStringValue(marker);
                writer.WriteEndArray();
                writer.WriteString("file", region.Path);
                writer.WriteNumber("line", region.Line);
                writer.WriteString("kind", region.Kind);
                writer.WriteString("language", region.Language);
                string snippet = ToolOutputBudget.BoundSearchSnippet(
                    region.Snippet,
                    boundAgentOutput,
                    out bool snippetTruncated);
                writer.WriteString("snippet", snippet);
                if (snippetTruncated)
                    writer.WriteBoolean("snippet_truncated", true);
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

internal sealed record MarkerSearchHit(IReadOnlyList<string> Markers, RegionSearchHit Region);
