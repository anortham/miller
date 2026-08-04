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
    /// <summary>
    /// Whether an artifact at <paramref name="indexLevel"/> has a reference/facts layer that has not been
    /// extracted yet. This is the form every read tool should ask, because the level travels on the read context
    /// (read from the artifact path) and so answers the same way for a cross-workspace read, which is served by a
    /// lean FTS index rather than a <see cref="MillerRepositoryIndex"/>.
    /// </summary>
    public static bool ReferenceLayerConverging(string? indexLevel) =>
        IsSymbolsLevel(indexLevel);

    /// <summary>Whether <paramref name="index"/> serves a symbols-level artifact whose reference/facts layers
    /// have not been extracted yet.</summary>
    public static bool ReferenceLayerConverging(MillerRepositoryIndex index) =>
        ReferenceLayerConverging(index.IndexLevel);

    /// <summary>The raw-level form for callers that read the artifact directly (patterns, CLI surfaces). Delegates
    /// to <see cref="IndexLevels.IsSymbolsLevel"/> so the comparison has one spelling across the
    /// <c>Miller.Indexing</c>/<c>Miller.Server</c> seam.</summary>
    public static bool IsSymbolsLevel(string? indexLevel) =>
        IndexLevels.IsSymbolsLevel(indexLevel);

    /// <summary>The data-bearing "reference layer converging" diagnostic for read tools: what is missing, what
    /// still works, and how the upgrade happens. The upgrade is NOT promised as automatic — only a session
    /// leading the workspace runs it in the background; a workspace served purely cross-workspace stays at
    /// symbols level until someone forces the upgrade.</summary>
    public static ToolDiagnostic Converging(string missing) =>
        ToolDiagnostic.ExpectedEmpty(
            "reference_layer_converging",
            $"This workspace serves a symbols-level index; the full-level layer has not been extracted yet: "
            + $"{missing} Symbol definitions, search, structure, and relationship edges (inheritance/imports) "
            + "are complete; per-usage identifier results are not yet. A session leading this workspace "
            + "upgrades it in the background; otherwise force it.",
            [
                new ToolDiagnosticAction("workspace(operation=\"status\")", "check index level and upgrade state"),
                new ToolDiagnosticAction("workspace(operation=\"full\")", "force the full-level upgrade now"),
            ]);

    /// <summary>The <c>references export</c> warning. Unlike <c>patterns export</c>, which goes empty at symbols
    /// level and so cannot be mistaken for a complete answer, this feed keeps emitting its relationship-derived
    /// rows: the degradation is PARTIAL, and a consumer streaming stdout has no way to see it. Names the two
    /// emptied arms so the omission is identifiable rather than merely announced.</summary>
    public static ToolDiagnostic ReferenceExportConverging() =>
        ToolDiagnostic.ExpectedEmpty(
            "reference_layer_converging",
            "This workspace serves a symbols-level index: identifiers and identifier_resolutions have not been "
            + "extracted yet, so this feed carries only relationship-derived reference rows. The stream is "
            + "partial, NOT empty — treat it as an undercount, not as this workspace's complete reference set. "
            + "Every emitted row carries index_level=\"symbols\". A session leading this workspace upgrades it in "
            + "the background; otherwise force it.",
            [
                new ToolDiagnosticAction("workspace(operation=\"status\")", "check index level and upgrade state"),
                new ToolDiagnosticAction("workspace(operation=\"full\")", "force the full-level upgrade now"),
            ]);

    /// <summary>The rename refusal: an unproven rename is worse than a delayed one — with an empty identifier
    /// layer the workspace scan cannot see usage sites, so an "exact coverage" claim would be false.</summary>
    public static ToolDiagnostic RenameRefusal() =>
        ToolDiagnostic.Refusal(
            "reference_layer_converging",
            "rename is refused while this workspace serves a symbols-level index: identifier extraction has "
            + "not run yet, so the rename cannot prove it found every usage site (it would rename definitions "
            + "and miss references). Re-run after the full-level upgrade completes — a leading session runs "
            + "it in the background; otherwise force it.",
            [
                new ToolDiagnosticAction("workspace(operation=\"status\")", "check index level and upgrade state"),
                new ToolDiagnosticAction("workspace(operation=\"full\")", "force the full-level upgrade now"),
            ]);

    /// <summary>Stamp the demand counter on a degraded call.</summary>
    public static void MarkDegraded(TelemetryScope? telemetry, string reason)
    {
        telemetry?.SetMetadata("degraded", true);
        telemetry?.SetMetadata("degraded_reason", reason);
    }
}
