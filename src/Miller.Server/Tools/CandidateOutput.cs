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

    public static void AppendRemainderNote(StringBuilder sb, int total)
    {
        int remaining = total - CompactLimit;
        if (remaining > 0)
            sb.Append("... ").Append(remaining).Append(" more candidates; refine target to narrow.").Append('\n');
    }

    private static bool SpansMultipleFiles(IReadOnlyList<IndexedSymbol> matches) =>
        matches.Select(static m => m.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any();
}
