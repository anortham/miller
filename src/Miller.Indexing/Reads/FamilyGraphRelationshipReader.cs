using Miller.Core.Graph;

namespace Miller.Indexing.Reads;

internal interface IFamilyGraphRelationshipReader
{
    IReadOnlyList<FamilyGraphRelationshipEdge> ReadRelationshipEdges(
        IReadOnlyList<string> candidateIds,
        Direction direction,
        Action<GraphStatementObservation>? statementObserver);
}

internal sealed record FamilyGraphRelationshipEdge(
    string CurrentId,
    string FromId,
    string ToId,
    string Kind,
    double Confidence,
    string Source);
