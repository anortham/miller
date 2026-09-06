using Miller.Indexing;

namespace Miller.Server.Tools.Context;

internal readonly record struct Candidate(
    IndexedSymbol Symbol,
    int Hop,
    string Reason = "graph_neighbor",
    bool IsPivot = false,
    int? AnchorLine = null,
    string? Body = null,
    bool BodyTruncated = false,
    string? BodyUnavailableReason = null);

internal sealed record ContextAnchorDiagnostic(string Kind, string Value, string Reason);

internal sealed record ContextSemanticSeed(IndexedSymbol Symbol, int Rank, double Score);

internal sealed record ContextSourceSeed(IndexedSymbol Symbol, int Rank);

internal sealed record ContextEvidenceDisposition(string Status, string Reason);

internal sealed record ReferenceContextItem(
    string ItemType,
    string Reason,
    string Confidence,
    string Name,
    string Kind,
    string File,
    int Line,
    int? Hop = null,
    string? Signature = null,
    string? SymbolId = null,
    string? ContainingSymbolId = null,
    string? SourceId = null,
    string? ChunkId = null,
    int? LineStart = null,
    int? LineEnd = null,
    string? Snippet = null,
    string? TargetSymbolId = null,
    string? ResolutionStatus = null,
    string? Provenance = null,
    double? EvidenceConfidence = null,
    string? AnchorReason = null,
    string? Role = null);

internal readonly record struct ContextReferenceReadCounts(int CandidatesRead, int CandidatesSkipped);

internal sealed record ContextBundleBuildResult(
    IReadOnlyList<Candidate> Candidates,
    IReadOnlyList<ContextAnchorDiagnostic> AnchorDiagnostics,
    int CandidatesExamined);

internal sealed record ContextReferenceBuildResult(
    IReadOnlyList<Candidate> Candidates,
    IReadOnlyList<ReferenceContextItem> Items,
    IReadOnlyList<ContextAnchorDiagnostic> AnchorDiagnostics,
    int CandidatesExamined,
    ContextReferenceReadCounts ReadCounts);

internal static class ContextTextBounds
{
    internal static string Truncate(string value, int max)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (max < 1)
            return string.Empty;
        if (value.Length <= max)
            return value;

        int prefixLength = max - 1;
        if (prefixLength > 0 &&
            char.IsHighSurrogate(value[prefixLength - 1]) &&
            char.IsLowSurrogate(value[prefixLength]))
        {
            prefixLength--;
        }
        return value[..prefixLength] + "…";
    }
}
