using Miller.Indexing;

namespace Miller.Tests.Indexing;

internal readonly record struct Task7BArm(ReferenceEvidenceReadPhase Phase, string Name);

internal static class Task7BArmMapping
{
    public static IReadOnlyList<Task7BArm> ForDirection(string direction) => direction switch
    {
        "reverse" =>
        [
            new(ReferenceEvidenceReadPhase.InboundExact, "inbound-exact"),
            new(ReferenceEvidenceReadPhase.InboundFallback, "inbound-fallback"),
        ],
        "forward" =>
        [
            new(ReferenceEvidenceReadPhase.OutgoingExact, "outgoing-exact"),
            new(ReferenceEvidenceReadPhase.OutgoingFallback, "outgoing-fallback"),
        ],
        _ => throw new ArgumentException($"Unknown Task 7B direction '{direction}'.", nameof(direction)),
    };
}
