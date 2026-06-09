using System.Buffers;
using System.Text;
using System.Text.Json;
using Miller.Indexing;
using Miller.Server.Telemetry;
using Miller.Server.Tools;

namespace Miller.Server.Cli;

internal static class CliCapabilities
{
    private static readonly string[] JsonCommands =
    [
        "capabilities --json",
        "dashboard --json",
        "search --json",
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
        "telemetry export --jsonl",
        "workspace status --json",
        "workspace health --json",
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
        ("workspace_health", "workspace health --json", 1, "docs/contracts/workspace-health-v1.md"),
        ("trace", "trace --json", 1, "docs/contracts/trace-json-v1.md"),
    ];

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
        sb.AppendLine("supported_export_formats:");
        sb.AppendLine("  - content_corpus jsonl via `miller content export`");
        sb.AppendLine("  - telemetry jsonl via `miller telemetry export --jsonl`");
        sb.AppendLine("json_commands:");
        foreach (string command in JsonCommands)
            sb.AppendLine("  - " + command);
        sb.AppendLine("json_contracts:");
        foreach (var jsonContract in JsonContracts)
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
            w.WriteBoolean("dashboard", true);
            w.WriteEndObject();

            w.WritePropertyName("json_commands");
            w.WriteStartArray();
            foreach (string command in JsonCommands)
                w.WriteStringValue(command);
            w.WriteEndArray();

            w.WritePropertyName("json_contracts");
            w.WriteStartArray();
            foreach (var jsonContract in JsonContracts)
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
            w.WriteEndArray();

            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static Utf8JsonWriter NewWriter(ArrayBufferWriter<byte> buffer) =>
        new(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}
