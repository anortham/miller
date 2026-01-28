using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using Codesearch.Server.Memory;

namespace Codesearch.Server.Tools;

[McpServerToolType]
internal static class MemoryTool
{
    [McpServerTool]
    [Description("Remember and recall project knowledge. Operations: remember (create checkpoint/plan/decision/learning), recall (search memories), status (show memory stats).")]
    internal static async Task<string> Memory(
        MemoryService memoryService,
        [Description("Operation: remember, recall, or status")] string operation,
        [Description("Content to remember (for remember operation)")] string? content = null,
        [Description("Memory type: checkpoint, plan, decision, learning")] string type = "checkpoint",
        [Description("Tags for categorization (comma-separated)")] string? tags = null,
        [Description("Title (for plans/decisions)")] string? title = null,
        [Description("Search query (for recall)")] string? query = null,
        [Description("Time range in days (for recall)")] int days = 7,
        [Description("Maximum results")] int limit = 20)
    {
        var memoryType = type.ToLowerInvariant() switch
        {
            "plan" => MemoryType.Plan,
            "decision" => MemoryType.Decision,
            "learning" => MemoryType.Learning,
            _ => MemoryType.Checkpoint
        };

        var tagList = string.IsNullOrWhiteSpace(tags)
            ? new List<string>()
            : tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        switch (operation.ToLowerInvariant())
        {
            case "remember":
                if (string.IsNullOrWhiteSpace(content))
                {
                    return "Error: content is required for remember operation.";
                }
                return await RememberAsync(memoryService, content, memoryType, tagList, title);

            case "recall":
                return await RecallAsync(memoryService, memoryType, tagList, days, limit, query);

            case "status":
                return await GetStatusAsync(memoryService);

            default:
                return $"Unknown operation: {operation}. Use: remember, recall, or status.";
        }
    }

    private static async Task<string> RememberAsync(
        MemoryService memoryService,
        string content,
        MemoryType type,
        List<string> tags,
        string? title)
    {
        var entry = await memoryService.RememberAsync(content, type, tags, title);

        var sb = new StringBuilder();
        sb.AppendLine($"## Memory Created");
        sb.AppendLine();
        sb.AppendLine($"- **ID**: `{entry.Metadata.Id}`");
        sb.AppendLine($"- **Type**: {entry.Metadata.Type}");
        sb.AppendLine($"- **File**: `{entry.FilePath}`");
        if (entry.Metadata.Tags.Count > 0)
        {
            sb.AppendLine($"- **Tags**: {string.Join(", ", entry.Metadata.Tags)}");
        }
        if (entry.Metadata.Git != null)
        {
            sb.AppendLine($"- **Git**: {entry.Metadata.Git.Branch} @ {entry.Metadata.Git.Commit}");
        }

        return sb.ToString();
    }

    private static async Task<string> RecallAsync(
        MemoryService memoryService,
        MemoryType type,
        List<string> tags,
        int days,
        int limit,
        string? query)
    {
        // For now, use filesystem recall (semantic search requires embeddings integration)
        // Only filter by type if a non-default type was explicitly specified
        MemoryType? typeFilter = type != MemoryType.Checkpoint ? type : null;
        var result = await memoryService.RecallAsync(
            type: typeFilter,
            tags: tags.Count > 0 ? tags : null,
            days: days,
            limit: limit);

        if (result.Entries.Count == 0)
        {
            return "No memories found matching the criteria.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"## Found {result.Entries.Count} Memory/Memories");
        sb.AppendLine();

        foreach (var entry in result.Entries)
        {
            var timestamp = DateTimeOffset.FromUnixTimeSeconds(entry.Metadata.Timestamp);
            sb.AppendLine($"### {entry.Metadata.Type}: {timestamp:yyyy-MM-dd HH:mm}");
            sb.AppendLine();
            sb.AppendLine($"**File**: `{entry.FilePath}`");
            if (entry.Metadata.Tags.Count > 0)
            {
                sb.AppendLine($"**Tags**: {string.Join(", ", entry.Metadata.Tags)}");
            }
            sb.AppendLine();

            // Show first 500 chars of content
            var preview = entry.Content.Length > 500
                ? entry.Content[..500] + "..."
                : entry.Content;
            sb.AppendLine(preview);
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static async Task<string> GetStatusAsync(MemoryService memoryService)
    {
        var result = await memoryService.RecallAsync(days: 365, limit: 1000);

        var byType = result.Entries
            .GroupBy(e => e.Metadata.Type)
            .ToDictionary(g => g.Key, g => g.Count());

        var recent = result.Entries
            .Where(e => e.Metadata.Timestamp > DateTimeOffset.UtcNow.AddDays(-7).ToUnixTimeSeconds())
            .Count();

        var sb = new StringBuilder();
        sb.AppendLine("## Memory Status");
        sb.AppendLine();
        sb.AppendLine($"- **Total memories**: {result.TotalCount}");
        sb.AppendLine($"- **Last 7 days**: {recent}");
        sb.AppendLine();
        sb.AppendLine("### By Type");
        foreach (var (memType, count) in byType.OrderByDescending(kv => kv.Value))
        {
            sb.AppendLine($"- {memType}: {count}");
        }

        return sb.ToString();
    }
}
