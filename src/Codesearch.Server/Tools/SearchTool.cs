using System.ComponentModel;
using ModelContextProtocol.Server;
using Codesearch.Server.Services;

namespace Codesearch.Server.Tools;

[McpServerToolType]
internal static class SearchTool
{
    [McpServerTool]
    [Description("Search code and project knowledge. Returns symbols matching the query.")]
    internal static string Search(
        SearchService searchService,
        [Description("Search query (natural language or code pattern)")] string query,
        [Description("Search method: auto, text, semantic, hybrid, pattern")] string method = "auto",
        [Description("Filter by symbol kind (function, class, etc.)")] string? kind = null,
        [Description("Filter by language")] string? language = null,
        [Description("Maximum results")] int limit = 20)
    {
        // Auto-detect method based on query
        var effectiveMethod = method == "auto" ? DetectMethod(query) : method;

        var results = effectiveMethod switch
        {
            "text" or "pattern" => searchService.SearchText(query, (uint)limit),
            // For semantic-only, we'd need embeddings - fall back to text for now
            "semantic" => searchService.SearchText(query, (uint)limit),
            // Hybrid needs vector - use text for now until embeddings integrated
            _ => searchService.SearchText(query, (uint)limit)
        };

        // Apply filters
        if (!string.IsNullOrEmpty(kind))
        {
            results = results.Where(r => r.kind.Equals(kind, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (!string.IsNullOrEmpty(language))
        {
            results = results.Where(r => r.language.Equals(language, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        // Format results
        return FormatResults(results);
    }

    private static string DetectMethod(string query)
    {
        // Pattern indicators: special characters common in code
        if (query.Contains(':') || query.Contains('<') || query.Contains('>') ||
            query.Contains('[') || query.Contains(']') || query.Contains('(') ||
            query.Contains('{') || query.Contains("=>") || query.Contains("?."))
        {
            return "pattern";
        }

        // Default to hybrid for natural language
        return "hybrid";
    }

    private static string FormatResults(List<uniffi.codesearch_ffi.SearchResultOutput> results)
    {
        if (results.Count == 0)
        {
            return "No results found.";
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Found {results.Count} result(s):\n");

        foreach (var r in results)
        {
            sb.AppendLine($"**{r.kind}**: `{r.name}`");
            sb.AppendLine($"  File: {r.filePath}:{r.startLine}");
            if (!string.IsNullOrEmpty(r.signature))
            {
                sb.AppendLine($"  Signature: `{r.signature}`");
            }
            if (!string.IsNullOrEmpty(r.docComment))
            {
                sb.AppendLine($"  Doc: {r.docComment.Split('\n')[0]}");
            }
            sb.AppendLine($"  Score: {r.score:F3}");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
