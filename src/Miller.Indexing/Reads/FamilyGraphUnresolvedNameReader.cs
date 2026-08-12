using Miller.Core.Graph;

namespace Miller.Indexing.Reads;

internal interface IFamilyGraphUnresolvedNameReader
{
    IReadOnlyList<FamilyGraphUnresolvedNameEdge> ReadUnresolvedNameEdges(
        IReadOnlyList<string> candidateIds,
        Direction direction,
        Action<GraphStatementObservation>? statementObserver);
}

internal sealed record FamilyGraphUnresolvedNameEdge(
    string CurrentId,
    string FromId,
    string ToId,
    string Kind,
    double Confidence,
    string Source);
