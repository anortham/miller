using Miller.Indexing;
using Miller.Server.Telemetry;

namespace Miller.Server.Tools;

/// <summary>
/// The one place reference-dependent tools ask "is this workspace's reference layer still converging?" and get
/// the matching diagnostic + telemetry stamp. A symbols-level artifact has complete symbols, relationships, and
/// search, but EMPTY identifier/region/facts tables — indistinguishable from "no references exist" without this
/// check, which is exactly the silent-wrong-answer trap the levels design guards against.
///
/// <para>The <c>degraded</c>/<c>degraded_reason</c> telemetry metadata is the DEMAND COUNTER from the levels
/// program plan: it measures how often agents hit converging layers, which decides whether query-triggered
/// extraction is ever worth building. Telemetry <c>outcome</c> is CHECK-constrained to ok|empty|error, so the
/// counter rides <c>metadata_json</c>, never a new outcome value.</para>
/// </summary>
internal static class IndexLevelGuard
{
    /// <summary>Whether <paramref name="index"/> serves a symbols-level artifact whose reference/facts layers
    /// have not been extracted yet.</summary>
    public static bool ReferenceLayerConverging(MillerRepositoryIndex index) =>
        IsSymbolsLevel(index.IndexLevel);

    /// <summary>The raw-level form for callers that read the artifact directly (patterns, CLI surfaces).</summary>
    public static bool IsSymbolsLevel(string? indexLevel) =>
        string.Equals(indexLevel, IndexLevels.SymbolsMetadataValue, StringComparison.Ordinal);

    /// <summary>The data-bearing "reference layer converging" diagnostic for read tools: what is missing, what
    /// still works, and where to watch progress.</summary>
    public static ToolDiagnostic Converging(string missing) =>
        ToolDiagnostic.ExpectedEmpty(
            "reference_layer_converging",
            $"This workspace serves a symbols-level index while the full-level rebuild converges in the "
            + $"background: {missing} Symbol definitions, search, structure, and relationship edges "
            + "(inheritance/imports) are complete; per-usage identifier results are not yet.",
            [new ToolDiagnosticAction("workspace(operation=\"status\")", "check level-upgrade progress")]);

    /// <summary>The rename refusal: an unproven rename is worse than a delayed one — with an empty identifier
    /// layer the workspace scan cannot see usage sites, so an "exact coverage" claim would be false.</summary>
    public static ToolDiagnostic RenameRefusal() =>
        ToolDiagnostic.Refusal(
            "reference_layer_converging",
            "rename is refused while this workspace serves a symbols-level index: identifier extraction has "
            + "not run yet, so the rename cannot prove it found every usage site (it would rename definitions "
            + "and miss references). Re-run after the background full-level upgrade completes.",
            [new ToolDiagnosticAction("workspace(operation=\"status\")", "check level-upgrade progress")]);

    /// <summary>Stamp the demand counter on a degraded call.</summary>
    public static void MarkDegraded(TelemetryScope? telemetry, string reason)
    {
        telemetry?.SetMetadata("degraded", true);
        telemetry?.SetMetadata("degraded_reason", reason);
    }
}
