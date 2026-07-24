namespace Miller.Core.Graph;

public sealed record ImpactRankSignal(
    ReachedNode Evidence,
    string FilePath,
    int StartLine,
    string Name,
    string SymbolId);

public static class ImpactRanker
{
    public static IReadOnlyList<ImpactRankSignal> Rank(IEnumerable<ImpactRankSignal> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return candidates
            .OrderBy(static candidate => candidate.Evidence.Hop)
            .ThenBy(static candidate => RelationshipPriority(candidate.Evidence.EdgeKind))
            .ThenByDescending(static candidate => candidate.Evidence.Centrality)
            .ThenByDescending(static candidate => VisibilityPriority(candidate.Evidence.Visibility))
            .ThenBy(static candidate => candidate.FilePath, StringComparer.Ordinal)
            .ThenBy(static candidate => candidate.StartLine)
            .ThenBy(static candidate => candidate.Name, StringComparer.Ordinal)
            .ThenBy(static candidate => candidate.SymbolId, StringComparer.Ordinal)
            .ToArray();
    }

    public static int RelationshipPriority(string? kind) => kind?.ToLowerInvariant() switch
    {
        "calls" or "call" or "invokes" or "instantiates" or "test_linkage" or "test_coverage" => 0,
        "implements" or "extends" or "inherits" => 1,
        "references" or "uses" or "type_usage" or "member_access" => 2,
        "imports" or "import" => 4,
        _ => 3,
    };

    public static int SourcePriority(string? source) => source?.ToLowerInvariant() switch
    {
        "test_linkage" or "test_coverage" => 0,
        "relationship" => 1,
        "identifier_target" => 2,
        "identifier_resolution" => 3,
        "pending_resolution" => 4,
        "identifier_name" => 5,
        "filename_role" => 6,
        _ => 7,
    };

    public static int VisibilityPriority(string? visibility) => visibility?.ToLowerInvariant() switch
    {
        "public" or "exported" => 3,
        "protected" or "internal" or "protected_internal" => 2,
        "private" => 1,
        _ => 0,
    };

    public static bool IsExactSource(string? source) =>
        SourcePriority(source) <= SourcePriority("pending_resolution");
}
