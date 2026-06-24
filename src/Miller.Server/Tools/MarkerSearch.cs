using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Miller.Core.Search;
using Miller.Indexing;

namespace Miller.Server.Tools;

internal static class MarkerSearch
{
    internal const int DefaultLimit = 50;
    internal const int MaxLimit = 500;
    private static readonly string[] DefaultMarkers = ["TODO", "FIXME", "HACK", "XXX"];
    private static readonly HashSet<string> AllowedMarkers = new(DefaultMarkers, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> CommentKinds = new(StringComparer.Ordinal) { "comment", "doc_comment" };

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
        IReadOnlyList<MarkerSearchHit> hits = FindMarkers(
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

    internal static IReadOnlyList<MarkerSearchHit> FindMarkers(
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
        var byKey = new Dictionary<string, MarkerSearchHit>(StringComparer.Ordinal);
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
                byKey.TryAdd(key, new MarkerSearchHit(marker, hit));
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
        IReadOnlyList<MarkerSearchHit> hits,
        IReadOnlyList<string> markers,
        string? compactBanner)
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

    private static string RenderJson(IReadOnlyList<MarkerSearchHit> hits)
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

internal sealed record MarkerSearchHit(string Marker, RegionSearchHit Region);
