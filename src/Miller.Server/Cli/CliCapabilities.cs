using System.Buffers;
using System.Text;
using System.Text.Json;
using Miller.Indexing;
using Miller.Server.Telemetry;
using Miller.Server.Tools;

namespace Miller.Server.Cli;

internal static class CliCapabilities
{
    /// <summary>
    /// The negotiated capability wire string for the index-revision delta mode
    /// (<c>miller impact --from-index-revision N</c> → the typed delta envelope; CT revision-delta contract R4).
    /// Eros enables its CT skip/absorption semantics ONLY when this string is present in the top-level
    /// <c>features</c> array; an older Miller without the mechanism simply omits it, so version skew degrades by
    /// negotiation, never by interpreting a failed or legacy-shaped response.
    /// </summary>
    public const string ImpactIndexRevisionDeltaFeature = "impact_index_revision_delta";

    /// <summary>
    /// Whether this build ships the index-revision delta mechanism (the <c>revision_file_changes</c> journal query
    /// in <see cref="Miller.Indexing.RevisionDeltaReader"/>). This build always does; the constant makes the R4
    /// gate explicit and lets a future build that drops the mechanism stop advertising it in one place.
    /// </summary>
    public const bool ImpactIndexRevisionDeltaActive = true;

    /// <summary>
    /// The negotiated capability wire string for the traversal evidence object emitted by
    /// <c>miller impact --json --from-index-revision N</c>. This is separate from delta-journal completeness:
    /// callers can negotiate the changed-path delta and graph-traversal evidence independently.
    /// </summary>
    public const string ImpactTraversalEvidenceFeature = "impact_traversal_evidence";

    /// <summary>
    /// Whether this build ships the bounded traversal evidence object for index-revision impact responses.
    /// </summary>
    public const bool ImpactTraversalEvidenceActive = true;

    /// <summary>
    /// The negotiated capability wire string for per-row positive test-role evidence and its candidate-only
    /// envelope scope in normal and index-revision impact JSON.
    /// </summary>
    public const string ImpactTestRoleEvidenceFeature = "impact_test_role_evidence";

    /// <summary>Whether this build ships the impact test-role evidence contract.</summary>
    public const bool ImpactTestRoleEvidenceActive = true;

    /// <summary>The negotiated capability wire string for stateless MCP impact output paging.</summary>
    public const string ImpactMcpOutputPageFeature = "impact_mcp_output_page";

    /// <summary>Whether this build ships the MCP impact output-page contract.</summary>
    public const bool ImpactMcpOutputPageActive = true;

    /// <summary>
    /// The top-level <c>features</c> array (R4): the negotiated capability strings Eros checks before enabling a
    /// capability-gated behavior. Pure and gated so the "advertise only when active; absent when inactive"
    /// contract is directly testable — a feature appears iff its flag is set.
    /// </summary>
    public static IReadOnlyList<string> NegotiatedFeatures(
        bool impactIndexRevisionDelta,
        bool impactTraversalEvidence,
        bool impactTestRoleEvidence,
        bool impactMcpOutputPage)
    {
        var features = new List<string>();
        if (impactIndexRevisionDelta)
            features.Add(ImpactIndexRevisionDeltaFeature);
        if (impactTraversalEvidence)
            features.Add(ImpactTraversalEvidenceFeature);
        if (impactTestRoleEvidence)
            features.Add(ImpactTestRoleEvidenceFeature);
        if (impactMcpOutputPage)
            features.Add(ImpactMcpOutputPageFeature);
        return features;
    }

    private static readonly string[] JsonCommands =
    [
        "capabilities --json",
        "dashboard --json",
        "search --json",
        "todos --json",
        "inspect --json",
        "context --json",
        "impact --json",
        "trace --json",
        "refresh --json --wait",
        "content import --json",
        "content add-markdown --json",
        "content search --json",
        "content read --json",
        "content list --json",
        "content remove --json",
        "content export",
        "patterns --json",
        "metrics churn --json",
        "metrics clones --json",
        "metrics complexity --json",
        "metrics risk --json",
        "metrics history --json",
        "report --json",
        "telemetry export --jsonl",
        "symbols export --jsonl",
        "references export --jsonl",
        "references candidates --json",
        "complexity export --jsonl",
        "workspace status --json",
        "workspace health --json",
        "workspace onboarding --json",
        "workspace leader --json",
        "workspace list --json",
        "workspace refresh --json",
        "workspace full --json",
        "workspace open --json",
        "workspace remove --json",
    ];

    private static readonly string[] ContentKinds =
    [
        TextContentKind.WorkspaceSource,
        TextContentKind.WorkspaceDocs,
        TextContentKind.WorkspaceConfig,
        TextContentKind.ExternalFile,
        TextContentKind.Web,
    ];

    private static readonly (string Name, string Command, int SchemaVersion, string Doc)[] JsonContracts =
    [
        ("workspace_status", "workspace status --json", 1, "docs/contracts/workspace-status-v1.md"),
        ("workspace_health", "workspace health --json", 1, "docs/contracts/workspace-health-v1.md"),
        ("workspace_onboarding", "workspace onboarding --json", 1, "docs/contracts/workspace-onboarding-v1.md"),
        ("workspace_leader", "workspace leader --json", 1, "docs/contracts/workspace-leader-json-v1.md"),
        ("refresh_wait", "refresh --json --wait", 1, "docs/contracts/refresh-wait-v1.md"),
        ("trace", "trace --json", 1, "docs/contracts/trace-json-v1.md"),
        ("patterns", "patterns --json", 1, "docs/contracts/patterns-json-v1.md"),
        ("metrics", "metrics <churn|clones|complexity|risk> --json", 1, "docs/contracts/metrics-json-v1.md"),
        ("metrics_history", "metrics history --json", 1, "docs/contracts/metrics-history-v1.md"),
        ("report", "report --json", 1, "docs/contracts/report-json-v1.md"),
        ("impact_index_revision_delta", "impact --json --from-index-revision N --from-artifact-id ID", 1,
            "docs/contracts/impact-index-revision-delta-v1.md"),
        ("impact_traversal_evidence", "impact --json --from-index-revision N --from-artifact-id ID", 1,
            "docs/contracts/impact-traversal-evidence-v1.md"),
        ("impact_test_role_evidence", "impact --json", 1,
            "docs/contracts/impact-test-role-evidence-v1.md"),
        ("impact_mcp_output_page", "impact --json", 1,
            "docs/contracts/impact-mcp-output-page-v1.md"),
        ("references_candidates", "references candidates --json", 1, "docs/contracts/references-candidates-v1.md"),
    ];

    /// <summary>
    /// The versioned JSON contracts advertised by this build. Traversal evidence is omitted when its mechanism
    /// is inactive; every other contract remains independently available.
    /// </summary>
    public static IReadOnlyList<(string Name, string Command, int SchemaVersion, string Doc)>
        NegotiatedJsonContracts(
            bool impactTraversalEvidence,
            bool impactTestRoleEvidence,
            bool impactMcpOutputPage) =>
        JsonContracts
            .Where(contract => impactTraversalEvidence || contract.Name != ImpactTraversalEvidenceFeature)
            .Where(contract => impactTestRoleEvidence || contract.Name != ImpactTestRoleEvidenceFeature)
            .Where(contract => impactMcpOutputPage || contract.Name != ImpactMcpOutputPageFeature)
            .ToArray();

    public static string Render(bool json)
    {
        SymbolSearchSidecar sidecar = SymbolSearchSidecar.FromEnvironment();
        return json ? Json(sidecar) : Compact(sidecar);
    }

    private static string Compact(SymbolSearchSidecar sidecar)
    {
        MillerContractFacts contract = MillerContractFacts.Current;
        var sb = new StringBuilder();
        sb.AppendLine("# capabilities");
        sb.AppendLine($"miller_version: {MillerVersion.Current}");
        sb.AppendLine($"julie_extract: {contract.PinnedJulieExtractVersion}");
        sb.AppendLine($"sqlite_schema_version: {contract.SqliteSchemaVersion}");
        sb.AppendLine($"extract_contract_version: {contract.ExtractContractVersion}");
        sb.AppendLine($"report_schema_version: {contract.ReportSchemaVersion}");
        sb.AppendLine($"hash_algorithm: {contract.HashAlgorithm}");
        sb.AppendLine($"search_sidecar_schema_version: {SearchIndexWriter.SchemaVersion}");
        sb.AppendLine($"content_corpus_schema_version: {ContentCorpusSchema.SchemaVersion}");
        sb.AppendLine($"content_corpus_chunker_version: {ContentCorpusSchema.ChunkerVersion}");
        sb.AppendLine($"symbol_search_sidecar: {(sidecar.Enabled ? "enabled" : "disabled")}");
        sb.AppendLine($"source_region_index: {(sidecar.RegionOptions.Enabled ? "enabled" : "disabled")}");
        sb.AppendLine("reference_aware_context: enabled");
        sb.AppendLine("references_candidates: enabled");
        sb.AppendLine("features:");
        foreach (string feature in NegotiatedFeatures(
                     ImpactIndexRevisionDeltaActive,
                     ImpactTraversalEvidenceActive,
                     ImpactTestRoleEvidenceActive,
                     ImpactMcpOutputPageActive))
            sb.AppendLine("  - " + feature);
        sb.AppendLine("supported_export_formats:");
        sb.AppendLine("  - content_corpus jsonl via `miller content export`");
        sb.AppendLine("  - telemetry jsonl via `miller telemetry export --jsonl`");
        sb.AppendLine("  - symbols jsonl via `miller symbols export --jsonl`");
        sb.AppendLine("  - references jsonl via `miller references export --jsonl`");
        sb.AppendLine("  - complexity_metrics jsonl via `miller complexity export --jsonl`");
        sb.AppendLine("  - structural_facts jsonl via `miller patterns export --jsonl`");
        sb.AppendLine("json_commands:");
        foreach (string command in JsonCommands)
            sb.AppendLine("  - " + command);
        sb.AppendLine("json_contracts:");
        foreach (var jsonContract in NegotiatedJsonContracts(
                     ImpactTraversalEvidenceActive,
                     ImpactTestRoleEvidenceActive,
                     ImpactMcpOutputPageActive))
            sb.AppendLine($"  - {jsonContract.Name} v{jsonContract.SchemaVersion}: `{jsonContract.Command}` ({jsonContract.Doc})");
        return sb.ToString().TrimEnd();
    }

    private static string Json(SymbolSearchSidecar sidecar)
    {
        MillerContractFacts contract = MillerContractFacts.Current;
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = NewWriter(buffer))
        {
            w.WriteStartObject();

            w.WritePropertyName("miller");
            w.WriteStartObject();
            w.WriteString("version", MillerVersion.Current);
            w.WriteEndObject();

            w.WritePropertyName("julie_extract");
            w.WriteStartObject();
            w.WriteString("pinned_version", contract.PinnedJulieExtractVersion);
            w.WriteNumber("schema_version", contract.SchemaVersion);
            w.WriteNumber("sqlite_schema_version", contract.SqliteSchemaVersion);
            w.WriteNumber("extract_contract_version", contract.ExtractContractVersion);
            w.WriteNumber("report_schema_version", contract.ReportSchemaVersion);
            w.WriteString("hash_algorithm", contract.HashAlgorithm);
            w.WriteEndObject();

            w.WritePropertyName("artifacts");
            w.WriteStartObject();
            w.WriteNumber("search_sidecar_schema_version", SearchIndexWriter.SchemaVersion);
            w.WriteNumber("content_corpus_schema_version", ContentCorpusSchema.SchemaVersion);
            w.WriteString("content_corpus_chunker_version", ContentCorpusSchema.ChunkerVersion);
            w.WriteEndObject();

            w.WritePropertyName("optional_features");
            w.WriteStartObject();
            w.WriteBoolean("symbol_search_sidecar", sidecar.Enabled);
            w.WriteBoolean("source_region_index", sidecar.RegionOptions.Enabled);
            w.WriteNumber("source_region_max_bytes", sidecar.RegionOptions.MaxRegionBytes);
            w.WriteBoolean("content_corpus", true);
            w.WriteBoolean("reference_aware_context", true);
            w.WriteBoolean("references_candidates", true);
            w.WriteBoolean("dashboard", true);
            w.WriteEndObject();

            // The negotiated feature strings Eros gates capability-specific behavior on (R4). A feature appears
            // here only when its mechanism is active in this build; an older Miller omits it.
            w.WritePropertyName("features");
            w.WriteStartArray();
            foreach (string feature in NegotiatedFeatures(
                         ImpactIndexRevisionDeltaActive,
                         ImpactTraversalEvidenceActive,
                         ImpactTestRoleEvidenceActive,
                         ImpactMcpOutputPageActive))
                w.WriteStringValue(feature);
            w.WriteEndArray();

            w.WritePropertyName("json_commands");
            w.WriteStartArray();
            foreach (string command in JsonCommands)
                w.WriteStringValue(command);
            w.WriteEndArray();

            w.WritePropertyName("json_contracts");
            w.WriteStartArray();
            foreach (var jsonContract in NegotiatedJsonContracts(
                         ImpactTraversalEvidenceActive,
                         ImpactTestRoleEvidenceActive,
                         ImpactMcpOutputPageActive))
            {
                w.WriteStartObject();
                w.WriteString("name", jsonContract.Name);
                w.WriteString("command", jsonContract.Command);
                w.WriteNumber("schema_version", jsonContract.SchemaVersion);
                w.WriteString("doc", jsonContract.Doc);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WritePropertyName("supported_export_formats");
            w.WriteStartArray();
            w.WriteStartObject();
            w.WriteString("name", "content_corpus");
            w.WriteString("command", "miller content export");
            w.WriteString("format", "jsonl");
            w.WriteNumber("schema_version", ContentCorpusSchema.SchemaVersion);
            w.WriteString("chunker_version", ContentCorpusSchema.ChunkerVersion);
            w.WritePropertyName("filters");
            w.WriteStartArray();
            w.WriteStringValue("--kind");
            w.WriteStringValue("--content-workspace-id");
            w.WriteEndArray();
            w.WritePropertyName("content_kinds");
            w.WriteStartArray();
            foreach (string kind in ContentKinds)
                w.WriteStringValue(kind);
            w.WriteEndArray();
            w.WriteEndObject();

            w.WriteStartObject();
            w.WriteString("name", "telemetry");
            w.WriteString("command", "miller telemetry export --jsonl");
            w.WriteString("format", "jsonl");
            w.WriteNumber("schema_version", TelemetryExportReader.SchemaVersion);
            w.WritePropertyName("filters");
            w.WriteStartArray();
            w.WriteStringValue("--workspace-id");
            w.WriteEndArray();
            w.WriteEndObject();

            w.WriteStartObject();
            w.WriteString("name", "symbols");
            w.WriteString("command", "miller symbols export --jsonl");
            w.WriteString("format", "jsonl");
            w.WriteNumber("schema_version", SymbolExportReader.SchemaVersion);
            w.WritePropertyName("filters");
            w.WriteStartArray();
            w.WriteStringValue("--workspace-id");
            w.WriteStringValue("--workspace");
            w.WriteEndArray();
            w.WriteEndObject();

            w.WriteStartObject();
            w.WriteString("name", "references");
            w.WriteString("command", "miller references export --jsonl");
            w.WriteString("format", "jsonl");
            w.WriteNumber("schema_version", ReferenceExportReader.SchemaVersion);
            w.WritePropertyName("filters");
            w.WriteStartArray();
            w.WriteStringValue("--workspace-id");
            w.WriteStringValue("--workspace");
            w.WriteEndArray();
            w.WriteEndObject();

            w.WriteStartObject();
            w.WriteString("name", "complexity_metrics");
            w.WriteString("command", "miller complexity export --jsonl");
            w.WriteString("format", "jsonl");
            w.WriteNumber("schema_version", ComplexityExportReader.SchemaVersion);
            w.WritePropertyName("filters");
            w.WriteStartArray();
            w.WriteStringValue("--workspace-id");
            w.WriteStringValue("--workspace");
            w.WriteEndArray();
            w.WriteEndObject();

            w.WriteStartObject();
            w.WriteString("name", "structural_facts");
            w.WriteString("command", "miller patterns export --jsonl");
            w.WriteString("format", "jsonl");
            w.WriteNumber("schema_version", PatternFactsExportReader.SchemaVersion);
            w.WritePropertyName("filters");
            w.WriteStartArray();
            w.WriteStringValue("--workspace-id");
            w.WriteStringValue("--workspace");
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();

            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static Utf8JsonWriter NewWriter(ArrayBufferWriter<byte> buffer) =>
        new(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}
