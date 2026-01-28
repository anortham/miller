using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using Codesearch.Server.Services;

namespace Codesearch.Server.Tools;

[McpServerToolType]
internal static class NavigationTool
{
    [McpServerTool]
    [Description("Navigate code: find references, go to definition, browse symbols. Operations: references, definition, symbols.")]
    internal static string Navigate(
        SearchService searchService,
        [Description("Operation: references, definition, or symbols")] string operation,
        [Description("Symbol ID or name")] string? symbol = null,
        [Description("File path (for symbols operation)")] string? file = null,
        [Description("Symbol kind filter (function, class, method, etc.)")] string? kind = null,
        [Description("Maximum results")] int limit = 50)
    {
        return operation.ToLowerInvariant() switch
        {
            "references" => FindReferences(searchService, symbol ?? "", limit),
            "definition" => GoToDefinition(searchService, symbol ?? ""),
            "symbols" => BrowseSymbols(searchService, file, kind, limit),
            _ => $"Unknown operation: {operation}. Use: references, definition, or symbols."
        };
    }

    private static string FindReferences(SearchService searchService, string symbol, int limit)
    {
        if (string.IsNullOrEmpty(symbol))
        {
            return "Error: symbol parameter required for references operation.";
        }

        // Resolve symbol name to ID if needed
        var symbolId = ResolveSymbolId(searchService, symbol);
        if (symbolId == null)
        {
            return $"No symbol found matching '{symbol}'.";
        }

        var references = searchService.GetReferences(symbolId, (uint)limit);

        if (references.Count == 0)
        {
            return $"No references found for '{symbol}'.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"## References to `{symbol}` ({references.Count} found)");
        sb.AppendLine();

        // Group by file
        var byFile = references.GroupBy(r => r.filePath).OrderBy(g => g.Key);

        foreach (var fileGroup in byFile)
        {
            sb.AppendLine($"### {fileGroup.Key}");
            foreach (var r in fileGroup.OrderBy(r => r.lineNumber))
            {
                sb.AppendLine($"- Line {r.lineNumber}:{r.column} - `{r.name}` ({r.kind})");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string GoToDefinition(SearchService searchService, string symbol)
    {
        if (string.IsNullOrEmpty(symbol))
        {
            return "Error: symbol parameter required for definition operation.";
        }

        // Try direct ID lookup first
        var symbolInfo = searchService.GetSymbolById(symbol);

        if (symbolInfo == null)
        {
            // Try searching by name
            var searchResults = searchService.SearchText(symbol, 5);
            if (searchResults.Count == 0)
            {
                return $"No definition found for '{symbol}'.";
            }

            // Return top matches
            var sb = new StringBuilder();
            sb.AppendLine($"## Definitions matching `{symbol}`");
            sb.AppendLine();

            foreach (var result in searchResults)
            {
                sb.AppendLine($"### {result.name} ({result.kind})");
                var lineRange = result.startLine.HasValue && result.endLine.HasValue
                    ? $":{result.startLine}-{result.endLine}"
                    : "";
                sb.AppendLine($"- **File**: `{result.filePath}{lineRange}`");
                sb.AppendLine($"- **Language**: {result.language}");
                if (!string.IsNullOrEmpty(result.signature))
                {
                    sb.AppendLine($"- **Signature**: `{result.signature}`");
                }
                sb.AppendLine();
            }

            return sb.ToString();
        }

        // Format single definition
        var output = new StringBuilder();
        output.AppendLine($"## Definition of `{symbolInfo.name}`");
        output.AppendLine();
        output.AppendLine($"- **Kind**: {symbolInfo.kind}");
        output.AppendLine($"- **Language**: {symbolInfo.language}");
        output.AppendLine($"- **File**: `{symbolInfo.filePath}:{symbolInfo.startLine}-{symbolInfo.endLine}`");

        if (!string.IsNullOrEmpty(symbolInfo.signature))
        {
            output.AppendLine($"- **Signature**: `{symbolInfo.signature}`");
        }

        if (!string.IsNullOrEmpty(symbolInfo.docComment))
        {
            output.AppendLine();
            output.AppendLine("### Documentation");
            output.AppendLine(symbolInfo.docComment);
        }

        return output.ToString();
    }

    private static string BrowseSymbols(SearchService searchService, string? file, string? kind, int limit)
    {
        List<uniffi.codesearch_ffi.SymbolInfo> symbols;
        string filterDesc;

        if (!string.IsNullOrEmpty(file))
        {
            symbols = searchService.GetSymbolsByFile(file, (uint)limit);
            filterDesc = $"in `{file}`";
        }
        else if (!string.IsNullOrEmpty(kind))
        {
            symbols = searchService.GetSymbolsByKind(kind, (uint)limit);
            filterDesc = $"of kind `{kind}`";
        }
        else
        {
            return "Error: Provide either 'file' or 'kind' parameter for symbols operation.";
        }

        if (symbols.Count == 0)
        {
            return $"No symbols found {filterDesc}.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"## Symbols {filterDesc} ({symbols.Count} found)");
        sb.AppendLine();

        // Group by kind when filtering by file, by file when filtering by kind
        if (!string.IsNullOrEmpty(file))
        {
            var byKind = symbols.GroupBy(s => s.kind).OrderBy(g => g.Key);
            foreach (var group in byKind)
            {
                sb.AppendLine($"### {group.Key} ({group.Count()})");
                foreach (var s in group.OrderBy(s => s.startLine))
                {
                    var sig = !string.IsNullOrEmpty(s.signature) ? $" - `{s.signature}`" : "";
                    sb.AppendLine($"- `{s.name}` (line {s.startLine}){sig}");
                }
                sb.AppendLine();
            }
        }
        else
        {
            var byFile = symbols.GroupBy(s => s.filePath).OrderBy(g => g.Key);
            foreach (var group in byFile)
            {
                sb.AppendLine($"### {group.Key}");
                foreach (var s in group.OrderBy(s => s.startLine))
                {
                    sb.AppendLine($"- `{s.name}` (line {s.startLine})");
                }
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private static string? ResolveSymbolId(SearchService searchService, string symbol)
    {
        // If it looks like an ID (contains :: or /, or starts with file_), use directly
        if (symbol.Contains("::") || symbol.Contains("/") || symbol.StartsWith("file_"))
        {
            return symbol;
        }

        // Otherwise search for it
        var results = searchService.SearchText(symbol, 1);
        return results.Count > 0 ? results[0].id : null;
    }
}
