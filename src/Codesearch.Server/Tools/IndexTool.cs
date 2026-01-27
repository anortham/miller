using System.ComponentModel;
using ModelContextProtocol.Server;
using Codesearch.Server.Services;

namespace Codesearch.Server.Tools;

[McpServerToolType]
internal static class IndexTool
{
    [McpServerTool]
    [Description("Manage workspace index. Operations: status (check health), refresh (update stale), full (rebuild).")]
    internal static async Task<string> Index(
        IndexService indexService,
        [Description("Operation: status, refresh, or full")] string operation = "status",
        [Description("Specific path to index (optional)")] string? path = null)
    {
        switch (operation.ToLowerInvariant())
        {
            case "status":
                return FormatStatus(indexService.GetStatus());

            case "refresh":
                var refreshResult = await indexService.RefreshAsync(path);
                return FormatResult("Refresh", refreshResult);

            case "full":
                var fullResult = await indexService.FullIndexAsync(path);
                return FormatResult("Full index", fullResult);

            default:
                return $"Unknown operation: {operation}. Use: status, refresh, or full.";
        }
    }

    private static string FormatStatus(IndexStatus status)
    {
        return $"""
            ## Index Status

            - **Symbols**: {status.SymbolCount:N0}
            - **Database**: {status.DbPath}
            - **Workspace**: {status.WorkspaceRoot}
            - **Health**: {(status.IsHealthy ? "OK" : "ERROR")}
            """;
    }

    private static string FormatResult(string operation, IndexResult result)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"## {operation} Complete");
        sb.AppendLine();
        sb.AppendLine($"- **Files indexed**: {result.FilesIndexed}");
        sb.AppendLine($"- **Files skipped**: {result.FilesSkipped}");

        if (result.Errors.Count > 0)
        {
            sb.AppendLine($"- **Errors**: {result.Errors.Count}");
            foreach (var error in result.Errors.Take(5))
            {
                sb.AppendLine($"  - {error}");
            }
            if (result.Errors.Count > 5)
            {
                sb.AppendLine($"  - ... and {result.Errors.Count - 5} more");
            }
        }

        return sb.ToString();
    }
}
