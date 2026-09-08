using System.Text;
using Miller.Indexing;

namespace Miller.Server.Tools;

internal static class CandidateOutput
{
    public const int CompactLimit = 20;

    public static string Header(IReadOnlyList<IndexedSymbol> matches, bool supportsScope, string fallback)
    {
        if (supportsScope && SpansMultipleFiles(matches))
            return "Multiple candidates — pass scope=<file> to disambiguate:";

        return fallback;
    }

    public static IEnumerable<IndexedSymbol> Visible(IReadOnlyList<IndexedSymbol> matches) =>
        matches.Take(CompactLimit);

    public static IReadOnlyList<string> RerunExamples(
        string target,
        IReadOnlyList<IndexedSymbol> matches,
        bool supportsScope,
        string command = "inspect",
        ISymbolLookupIndex? index = null)
    {
        string escapedCommand = EscapeShellishArgument(command);
        if (supportsScope && SpansMultipleFiles(matches))
        {
            string escapedTarget = EscapeShellishArgument(target);
            return matches
                .Select(static match => match.FilePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .Select(path => $"{escapedCommand} target=\"{escapedTarget}\" scope=\"{EscapeShellishArgument(path)}\"")
                .ToArray();
        }

        var overloadGroups = matches
            .GroupBy(static m => (m.FilePath, m.ParentId, m.Name))
            .ToDictionary(static g => g.Key, static g => g.Count());

        return matches
            .Take(3)
            .Select(match =>
            {
                string? parentName = null;
                if (index is not null && match.ParentId is not null)
                {
                    parentName = index.FindBySymbolId(match.ParentId)?.Name;
                }

                bool isOverload = overloadGroups.TryGetValue((match.FilePath, match.ParentId, match.Name), out int count) && count > 1;

                if (!isOverload && !string.IsNullOrWhiteSpace(parentName))
                {
                    return $"{escapedCommand} target=\"{EscapeShellishArgument($"{parentName}.{match.Name}")}\"";
                }

                return $"{escapedCommand} target=\"{EscapeShellishArgument(match.SymbolId)}\"";
            })
            .ToArray();
    }

    public static void AppendRerunExamples(
        StringBuilder sb,
        string target,
        IReadOnlyList<IndexedSymbol> matches,
        bool supportsScope,
        string command = "inspect",
        ISymbolLookupIndex? index = null)
    {
        IReadOnlyList<string> examples = RerunExamples(target, matches, supportsScope, command, index);
        if (examples.Count == 0)
            return;

        sb.Append("Try:").Append('\n');
        foreach (string example in examples)
            sb.Append("  ").Append(example).Append('\n');
    }

    public static void AppendRemainderNote(StringBuilder sb, int total)
    {
        int remaining = total - CompactLimit;
        if (remaining > 0)
            sb.Append("... ").Append(remaining).Append(" more candidates; refine target to narrow.").Append('\n');
    }

    private static bool SpansMultipleFiles(IReadOnlyList<IndexedSymbol> matches) =>
        matches.Select(static m => m.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any();

    private static string EscapeShellishArgument(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
