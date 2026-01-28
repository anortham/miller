using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using Codesearch.Server.Services;

namespace Codesearch.Server.Tools;

[McpServerToolType]
internal static class SearchTool
{
    [McpServerTool]
    [Description("Search for symbols in the codebase. Modes: text (exact matching), semantic (meaning-based), hybrid (combined - default).")]
    internal static string Search(
        SearchService searchService,
        [Description("Search query")] string query,
        [Description("Search mode: text, semantic, or hybrid")] string mode = "hybrid",
        [Description("Maximum number of results")] int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return "Error: query parameter is required.";
        }

        List<uniffi.codesearch_ffi.SearchResultOutput> results;
        string modeUsed;

        try
        {
            (results, modeUsed) = mode.ToLowerInvariant() switch
            {
                "text" => (searchService.SearchText(query, (uint)limit), "text"),
                "semantic" => (searchService.SearchSemantic(query, limit), "semantic"),
                "hybrid" => (searchService.SearchHybrid(query, limit),
                            searchService.IsSemanticSearchAvailable ? "hybrid" : "text (fallback)"),
                _ => (searchService.SearchHybrid(query, limit),
                     searchService.IsSemanticSearchAvailable ? "hybrid" : "text (fallback)")
            };
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Embedding"))
        {
            // Semantic requested but not available
            results = searchService.SearchText(query, (uint)limit);
            modeUsed = "text (embeddings unavailable)";
        }

        if (results.Count == 0)
        {
            return $"No results found for '{query}' (mode: {modeUsed}).";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"## Search Results for `{query}` ({results.Count} found, mode: {modeUsed})");
        sb.AppendLine();

        foreach (var result in results)
        {
            var score = $" (score: {result.score:F3})";
            sb.AppendLine($"### {result.name} ({result.kind}){score}");
            sb.AppendLine($"- **File**: `{result.filePath}:{result.startLine}-{result.endLine}`");
            sb.AppendLine($"- **Language**: {result.language}");
            if (!string.IsNullOrEmpty(result.signature))
            {
                sb.AppendLine($"- **Signature**: `{result.signature}`");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
