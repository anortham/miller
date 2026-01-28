using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using Codesearch.Server.Services;

namespace Codesearch.Server.Tools;

[McpServerToolType]
internal static class RelationshipTool
{
    [McpServerTool]
    [Description("Find callers, callees, and relationships for symbols. Operations: callers, callees, explain.")]
    internal static string Relationships(
        SearchService searchService,
        [Description("Operation: callers, callees, or explain")] string operation,
        [Description("Symbol ID or name to look up")] string symbol,
        [Description("Maximum results")] int limit = 20)
    {
        // First, find the symbol if given a name instead of ID
        var symbolId = symbol;
        if (!symbol.Contains("::") && !symbol.Contains("/") && !symbol.StartsWith("file_"))
        {
            // Looks like a name, search for it
            var searchResults = searchService.SearchText(symbol, 1);
            if (searchResults.Count == 0)
            {
                return $"No symbol found matching '{symbol}'.";
            }
            symbolId = searchResults[0].id;
        }

        return operation.ToLowerInvariant() switch
        {
            "callers" => GetCallers(searchService, symbolId, limit),
            "callees" => GetCallees(searchService, symbolId, limit),
            "explain" => ExplainSymbol(searchService, symbolId, limit),
            _ => $"Unknown operation: {operation}. Use: callers, callees, or explain."
        };
    }

    private static string GetCallers(SearchService searchService, string symbolId, int limit)
    {
        var callers = searchService.GetCallers(symbolId, (uint)limit);

        if (callers.Count == 0)
        {
            return "No callers found for this symbol.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"## Callers ({callers.Count})");
        sb.AppendLine();

        foreach (var caller in callers)
        {
            sb.AppendLine($"- `{caller.fromSymbolId}` at `{caller.filePath}:{caller.lineNumber}`");
            sb.AppendLine($"  Confidence: {caller.confidence:P0}");
        }

        return sb.ToString();
    }

    private static string GetCallees(SearchService searchService, string symbolId, int limit)
    {
        var callees = searchService.GetCallees(symbolId, (uint)limit);

        if (callees.Count == 0)
        {
            return "No callees found for this symbol.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"## Callees ({callees.Count})");
        sb.AppendLine();

        foreach (var callee in callees)
        {
            sb.AppendLine($"- `{callee.toSymbolId}` at `{callee.filePath}:{callee.lineNumber}`");
            sb.AppendLine($"  Confidence: {callee.confidence:P0}");
        }

        return sb.ToString();
    }

    private static string ExplainSymbol(SearchService searchService, string symbolId, int limit)
    {
        // Get the symbol details
        var searchResults = searchService.SearchText(symbolId, 1);

        var sb = new StringBuilder();
        sb.AppendLine("## Symbol Details");
        sb.AppendLine();

        if (searchResults.Count > 0)
        {
            var sym = searchResults[0];
            sb.AppendLine($"- **Name**: `{sym.name}`");
            sb.AppendLine($"- **Kind**: {sym.kind}");
            sb.AppendLine($"- **Language**: {sym.language}");
            sb.AppendLine($"- **File**: `{sym.filePath}:{sym.startLine}-{sym.endLine}`");
            if (!string.IsNullOrEmpty(sym.signature))
            {
                sb.AppendLine($"- **Signature**: `{sym.signature}`");
            }
            if (!string.IsNullOrEmpty(sym.docComment))
            {
                sb.AppendLine();
                sb.AppendLine("### Documentation");
                sb.AppendLine(sym.docComment);
            }
        }
        else
        {
            sb.AppendLine($"- **ID**: `{symbolId}`");
        }

        sb.AppendLine();

        // Get callers
        var callers = searchService.GetCallers(symbolId, (uint)limit);
        sb.AppendLine($"### Callers ({callers.Count})");
        sb.AppendLine();
        if (callers.Count == 0)
        {
            sb.AppendLine("_No callers found._");
        }
        else
        {
            foreach (var caller in callers.Take(10))
            {
                sb.AppendLine($"- `{caller.fromSymbolId}` ({caller.filePath}:{caller.lineNumber})");
            }
            if (callers.Count > 10)
            {
                sb.AppendLine($"_...and {callers.Count - 10} more_");
            }
        }

        sb.AppendLine();

        // Get callees
        var callees = searchService.GetCallees(symbolId, (uint)limit);
        sb.AppendLine($"### Callees ({callees.Count})");
        sb.AppendLine();
        if (callees.Count == 0)
        {
            sb.AppendLine("_No callees found._");
        }
        else
        {
            foreach (var callee in callees.Take(10))
            {
                sb.AppendLine($"- `{callee.toSymbolId}` ({callee.filePath}:{callee.lineNumber})");
            }
            if (callees.Count > 10)
            {
                sb.AppendLine($"_...and {callees.Count - 10} more_");
            }
        }

        return sb.ToString();
    }
}
