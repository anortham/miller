using Miller.Core.Contracts;
using Miller.Core.Resolver;

namespace Miller.Core.Graph;

/// <summary>
/// Produces bridge candidate edges for one evidence model. Providers own framework-specific reductions; the graph,
/// scorer, and trace renderer stay provider-agnostic.
/// </summary>
public interface IBridgeProvider
{
    string Id { get; }

    BridgeProviderResult BuildCandidates(BridgeProviderContext context);
}

public sealed record BridgeProviderContext(
    IReadOnlyList<SymbolDetail> Symbols,
    IReadOnlyList<TypeArgument> TypeArguments,
    IReadOnlyList<LiteralRecord> Literals,
    IReadOnlyList<SymbolAnnotation> Annotations,
    IReadOnlyList<DbSetProperty> DbSetProperties,
    IReadOnlyList<StructuralFactRecord> StructuralFacts,
    IReadOnlyDictionary<LiteralRecord, LiteralSite>? LiteralSites,
    IReadOnlyDictionary<string, SymbolDetail> SymbolsById,
    SymbolResolver Resolver);

public sealed record BridgeProviderResult(
    IReadOnlyList<CandidateEdge> Candidates,
    IReadOnlyDictionary<string, int> EvidenceCounts,
    string? SkipReason)
{
    public bool Active => SkipReason is null;

    public static BridgeProviderResult ActiveResult(
        IReadOnlyList<CandidateEdge> candidates,
        IReadOnlyDictionary<string, int> evidenceCounts) =>
        new(candidates, evidenceCounts, SkipReason: null);

    public static BridgeProviderResult Skipped(
        string reason,
        IReadOnlyDictionary<string, int> evidenceCounts) =>
        new(Candidates: [], EvidenceCounts: evidenceCounts, SkipReason: reason);
}
