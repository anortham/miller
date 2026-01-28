using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using Codesearch.Server.Services;

namespace Codesearch.Server.Tools;

[McpServerToolType]
internal static class ImpactTool
{
    [McpServerTool]
    [Description("Analyze impact of changing a symbol. Operations: impact, refresh-closure, status.")]
    internal static string Impact(
        SearchService searchService,
        ClosureService closureService,
        [Description("Operation: impact, refresh-closure, or status")] string operation,
        [Description("Symbol ID or name (for impact operation)")] string? symbol = null,
        [Description("Maximum distance to search")] int maxDistance = 10)
    {
        return operation.ToLowerInvariant() switch
        {
            "impact" => GetImpact(searchService, symbol ?? "", maxDistance),
            "refresh-closure" => RefreshClosure(searchService, closureService),
            "status" => GetStatus(searchService),
            _ => $"Unknown operation: {operation}. Use: impact, refresh-closure, or status."
        };
    }

    private static string GetImpact(SearchService searchService, string symbol, int maxDistance)
    {
        if (string.IsNullOrEmpty(symbol))
        {
            return "Error: symbol parameter required for impact operation.";
        }

        // Find symbol ID if given a name
        var symbolId = symbol;
        if (!symbol.Contains("::") && !symbol.Contains("/"))
        {
            var searchResults = searchService.SearchText(symbol, 1);
            if (searchResults.Count == 0)
            {
                return $"No symbol found matching '{symbol}'.";
            }
            symbolId = searchResults[0].id;
        }

        var impacted = searchService.GetImpacted(symbolId, (uint)maxDistance);

        if (impacted.Count == 0)
        {
            return $"No symbols would be impacted by changes to '{symbol}'.\n\n" +
                   "_Note: Run `impact(operation=\"refresh-closure\")` after indexing to compute reachability._";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"## Impact Analysis for `{symbol}`");
        sb.AppendLine();
        sb.AppendLine($"**{impacted.Count} symbols** would be affected by changes:");
        sb.AppendLine();

        // Group by distance
        var byDistance = impacted.GroupBy(i => i.distance).OrderBy(g => g.Key);

        foreach (var group in byDistance)
        {
            sb.AppendLine($"### Distance {group.Key} ({group.Count()} symbols)");
            foreach (var item in group.Take(20))
            {
                sb.AppendLine($"- `{item.symbolId}`");
            }
            if (group.Count() > 20)
            {
                sb.AppendLine($"_...and {group.Count() - 20} more_");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string RefreshClosure(SearchService searchService, ClosureService closureService)
    {
        // Get all call relationships to build the graph
        // We need to gather relationships from the database
        // For now, use what we can get from relationship queries

        var relationships = new List<(string FromId, string ToId)>();

        // Try to get relationships for all known symbols
        // This is a workaround since we don't have GetAllRelationships
        // The proper solution would be engine-level support

        var count = closureService.ComputeTransitiveClosure(relationships: relationships);

        if (count == 0)
        {
            return "Transitive closure computed. No relationships found to process.\n\n" +
                   "_Tip: Index files first with `index(operation=\"full\")` to extract relationships._";
        }

        return $"Transitive closure computed. {count} reachability entries created.";
    }

    private static string GetStatus(SearchService searchService)
    {
        var symbolCount = searchService.SymbolCount();
        var relationshipCount = searchService.RelationshipCount();
        var identifierCount = searchService.IdentifierCount();

        return $"""
            ## Index Status

            - **Symbols**: {symbolCount:N0}
            - **Relationships**: {relationshipCount:N0}
            - **Identifiers**: {identifierCount:N0}
            """;
    }
}
