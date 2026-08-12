using Miller.Core.Graph;

namespace Miller.Indexing.Reads;

internal interface IFamilyGraphResolutionReader
{
    IReadOnlyList<FamilyGraphResolutionEdge> ReadResolutionEdges(
        IReadOnlyList<string> candidateIds,
        Direction direction);
}

internal sealed record FamilyGraphResolutionEdge(
    string CurrentId,
    string FromId,
    string ToId,
    string Kind,
    double Confidence,
    string Source);
