namespace Miller.Core.Graph;

/// <summary>
/// Describes which bridge providers participated in building a <see cref="BridgeGraph"/> and what evidence they saw.
/// This is intentionally metadata: bridge correctness still comes from scored edges and honesty flags, while the report
/// explains why a graph may be empty or provider-limited.
/// </summary>
public sealed record BridgeCapabilityReport(
    IReadOnlyList<string> ActiveProviders,
    IReadOnlyList<BridgeProviderSkip> SkippedProviders,
    IReadOnlyList<string> Notes,
    IReadOnlyDictionary<string, int> EvidenceCounts)
{
    public static BridgeCapabilityReport Empty { get; } = new(
        ActiveProviders: [],
        SkippedProviders: [],
        Notes: [],
        EvidenceCounts: new Dictionary<string, int>(StringComparer.Ordinal));

    public bool HasStatus =>
        ActiveProviders.Count > 0 ||
        SkippedProviders.Count > 0 ||
        Notes.Count > 0 ||
        EvidenceCounts.Count > 0;
}

/// <summary>A bridge provider that did not participate, with the reason surfaced to trace output.</summary>
public sealed record BridgeProviderSkip(string ProviderId, string Reason);
