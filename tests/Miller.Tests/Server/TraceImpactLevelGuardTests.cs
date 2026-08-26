using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Server;
using Miller.Server.Cli;
using Miller.Server.Resolution;
using Miller.Server.Telemetry;
using Miller.Server.Tools;
using Miller.Server.Workspaces;
using Miller.Tests.Support;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// <c>trace</c> and <c>impact</c> both answer from the identifier layer a symbols-level scan leaves EMPTY, so an
/// unguarded render reads as "nothing calls this" rather than "the layer has not been extracted yet". Pins the
/// levels diagnostic on both tools in BOTH output formats, and pins the standalone JSON envelope a refusal
/// renders when there is no payload to attach to — a JSON consumer must be able to branch on the diagnostic
/// instead of getting <c>invalid_json_output</c> back (2026-08-11 dogfood).
/// </summary>
public sealed class TraceImpactLevelGuardTests : IDisposable
{
    private readonly string _dir;

    public TraceImpactLevelGuardTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tilg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Theory]
    [InlineData("refs")]
    [InlineData("path")]
    public void McpTrace_AtSymbolsLevel_CarriesTheConvergingDiagnosticIntoJson(string mode)
    {
        string dbPath = SymbolsLevelArtifact.Create(Path.Combine(_dir, "symbols-level"));
        var tool = new TraceTool(ReadProvider(dbPath, IndexLevels.SymbolsMetadataValue));

        string compact = tool.Trace("Alpha", mode: mode, to: "Beta");
        using JsonDocument document = JsonDocument.Parse(
            tool.Trace("Alpha", mode: mode, to: "Beta", format: "json"));
        JsonElement diagnostic = document.RootElement.GetProperty("diagnostic");

        Assert.Contains("diagnostic_code=reference_layer_converging", compact, StringComparison.Ordinal);
        Assert.Equal("reference_layer_converging", diagnostic.GetProperty("code").GetString());
        Assert.Equal("expected_empty", diagnostic.GetProperty("class").GetString());
        Assert.Equal("empty", diagnostic.GetProperty("outcome").GetString());
        Assert.NotEmpty(diagnostic.GetProperty("next_actions").EnumerateArray());
    }

    [Fact]
    public void McpImpact_AtSymbolsLevel_CarriesTheConvergingDiagnosticIntoJson()
    {
        string dbPath = SymbolsLevelArtifact.Create(Path.Combine(_dir, "symbols-level"));
        var tool = new ImpactTool(ReadProvider(dbPath, IndexLevels.SymbolsMetadataValue));

        string compact = tool.Impact(target: "Alpha");
        using JsonDocument document = JsonDocument.Parse(tool.Impact(target: "Alpha", format: "json"));
        JsonElement diagnostic = document.RootElement.GetProperty("diagnostic");

        Assert.Contains("diagnostic_code=reference_layer_converging", compact, StringComparison.Ordinal);
        Assert.Equal("reference_layer_converging", diagnostic.GetProperty("code").GetString());
        Assert.Equal("expected_empty", diagnostic.GetProperty("class").GetString());
        Assert.Equal("empty", diagnostic.GetProperty("outcome").GetString());
        Assert.NotEmpty(diagnostic.GetProperty("next_actions").EnumerateArray());
    }

    [Fact]
    public void McpTrace_AtSymbolsLevel_StampsTheReferenceLayerDemandCounter()
    {
        string dbPath = SymbolsLevelArtifact.Create(Path.Combine(_dir, "symbols-level"));
        var tool = new TraceTool(ReadProvider(dbPath, IndexLevels.SymbolsMetadataValue));

        using TelemetryLedger ledger = OpenLedger();
        using TelemetryScope scope = ledger.Measure("trace", "refs");
        tool.Trace("Alpha");

        using JsonDocument metadata = JsonDocument.Parse(scope.MetadataJson);
        Assert.True(metadata.RootElement.GetProperty("degraded").GetBoolean());
        Assert.Equal("reference_layer_converging", metadata.RootElement.GetProperty("degraded_reason").GetString());
    }

    [Fact]
    public void McpImpact_AtSymbolsLevel_StampsTheReferenceLayerDemandCounter()
    {
        string dbPath = SymbolsLevelArtifact.Create(Path.Combine(_dir, "symbols-level"));
        var tool = new ImpactTool(ReadProvider(dbPath, IndexLevels.SymbolsMetadataValue));

        using TelemetryLedger ledger = OpenLedger();
        using TelemetryScope scope = ledger.Measure("impact", "symbol");
        tool.Impact(target: "Alpha");

        using JsonDocument metadata = JsonDocument.Parse(scope.MetadataJson);
        Assert.True(metadata.RootElement.GetProperty("degraded").GetBoolean());
        Assert.Equal("reference_layer_converging", metadata.RootElement.GetProperty("degraded_reason").GetString());
    }

    [Fact]
    public void McpTrace_AtFullLevel_AttachesNothingAndLeavesTheDemandCounterUnstamped()
    {
        string dbPath = SymbolsLevelArtifact.CreateFull(Path.Combine(_dir, "full-level"));
        var tool = new TraceTool(ReadProvider(dbPath, IndexLevels.FullMetadataValue));

        using TelemetryLedger ledger = OpenLedger();
        using TelemetryScope scope = ledger.Measure("trace", "refs");
        string compact = tool.Trace("Alpha");
        using JsonDocument document = JsonDocument.Parse(tool.Trace("Alpha", format: "json"));

        using JsonDocument metadata = JsonDocument.Parse(scope.MetadataJson);
        Assert.DoesNotContain("reference_layer_converging", compact, StringComparison.Ordinal);
        Assert.False(document.RootElement.TryGetProperty("diagnostic", out _));
        Assert.False(metadata.RootElement.TryGetProperty("degraded", out _));
    }

    [Fact]
    public void McpImpact_AtFullLevel_AttachesNothingAndLeavesTheDemandCounterUnstamped()
    {
        string dbPath = SymbolsLevelArtifact.CreateFull(Path.Combine(_dir, "full-level"));
        var tool = new ImpactTool(ReadProvider(dbPath, IndexLevels.FullMetadataValue));

        using TelemetryLedger ledger = OpenLedger();
        using TelemetryScope scope = ledger.Measure("impact", "symbol");
        string compact = tool.Impact(target: "Alpha");
        using JsonDocument document = JsonDocument.Parse(tool.Impact(target: "Alpha", format: "json"));

        using JsonDocument metadata = JsonDocument.Parse(scope.MetadataJson);
        Assert.DoesNotContain("reference_layer_converging", compact, StringComparison.Ordinal);
        Assert.False(document.RootElement.TryGetProperty("diagnostic", out _));
        Assert.False(metadata.RootElement.TryGetProperty("degraded", out _));
    }

    [Fact]
    public void McpTrace_RefusalInJson_RendersTheStandaloneDiagnosticEnvelope()
    {
        string dbPath = SymbolsLevelArtifact.Create(Path.Combine(_dir, "symbols-level"));
        var tool = new TraceTool(ReadProvider(dbPath, IndexLevels.SymbolsMetadataValue));

        using JsonDocument document = JsonDocument.Parse(tool.Trace("Alpha", mode: "sideways", format: "json"));

        AssertStandaloneEnvelope(document.RootElement, "trace", "invalid_mode");
    }

    [Fact]
    public void McpImpact_RefusalInJson_RendersTheStandaloneDiagnosticEnvelope()
    {
        string dbPath = SymbolsLevelArtifact.Create(Path.Combine(_dir, "symbols-level"));
        var tool = new ImpactTool(ReadProvider(dbPath, IndexLevels.SymbolsMetadataValue));

        using JsonDocument document = JsonDocument.Parse(
            tool.Impact(target: "Alpha", format: "json", continuation: "not-a-token"));

        AssertStandaloneEnvelope(document.RootElement, "impact", "continuation_invalid");
    }

    [Fact]
    public void CliTrace_AtSymbolsLevel_CarriesTheConvergingDiagnosticIntoJson()
    {
        string dbPath = SymbolsLevelArtifact.Create(Path.Combine(_dir, "symbols-level"));

        var (code, outText, errText) = Run(["trace", "Alpha", "--json"], dbPath);

        using JsonDocument document = JsonDocument.Parse(outText);
        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Equal(
            "reference_layer_converging",
            document.RootElement.GetProperty("diagnostic").GetProperty("code").GetString());
    }

    [Fact]
    public void CliImpact_AtSymbolsLevel_CarriesTheConvergingDiagnosticIntoJson()
    {
        string dbPath = SymbolsLevelArtifact.Create(Path.Combine(_dir, "symbols-level"));

        var (code, outText, errText) = Run(["impact", "Alpha", "--json"], dbPath);

        using JsonDocument document = JsonDocument.Parse(outText);
        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Equal(
            "reference_layer_converging",
            document.RootElement.GetProperty("diagnostic").GetProperty("code").GetString());
    }

    [Fact]
    public void CliTrace_AtSymbolsLevel_ReportsReferenceLayerConverging()
    {
        string dbPath = SymbolsLevelArtifact.Create(Path.Combine(_dir, "symbols-level"));

        var (code, outText, errText) = Run(["trace", "Alpha"], dbPath);

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("diagnostic_code=reference_layer_converging", outText, StringComparison.Ordinal);
        Assert.Contains("diagnostic_class=expected_empty", outText, StringComparison.Ordinal);
    }

    [Fact]
    public void CliImpact_AtSymbolsLevel_ReportsReferenceLayerConverging()
    {
        string dbPath = SymbolsLevelArtifact.Create(Path.Combine(_dir, "symbols-level"));

        var (code, outText, errText) = Run(["impact", "Alpha"], dbPath);

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("diagnostic_code=reference_layer_converging", outText, StringComparison.Ordinal);
        Assert.Contains("diagnostic_class=expected_empty", outText, StringComparison.Ordinal);
    }

    [Fact]
    public void CliTrace_AtFullLevel_KeepsTheUnguardedOutput()
    {
        string dbPath = SymbolsLevelArtifact.CreateFull(Path.Combine(_dir, "full-level"));

        var (code, outText, errText) = Run(["trace", "Alpha"], dbPath);

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.DoesNotContain("reference_layer_converging", outText, StringComparison.Ordinal);
    }

    [Fact]
    public void CliImpact_AtFullLevel_KeepsTheUnguardedOutput()
    {
        string dbPath = SymbolsLevelArtifact.CreateFull(Path.Combine(_dir, "full-level"));

        var (code, outText, errText) = Run(["impact", "Alpha"], dbPath);

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.DoesNotContain("reference_layer_converging", outText, StringComparison.Ordinal);
    }

    private static void AssertStandaloneEnvelope(JsonElement root, string tool, string code)
    {
        Assert.Equal(ToolDiagnosticRenderer.SchemaVersion, root.GetProperty("schema_version").GetInt32());
        Assert.Equal(tool, root.GetProperty("tool").GetString());
        Assert.Equal(code, root.GetProperty("diagnostic").GetProperty("code").GetString());
    }

    private TelemetryLedger OpenLedger() =>
        TelemetryLedger.Open(Path.Combine(_dir, "telemetry.db"), workspaceId: "ws-levels");

    private static LevelReadProvider ReadProvider(string dbPath, string indexLevel)
    {
        MillerRepositoryIndex index = RepositoryIndexLoader.Load(dbPath);
        return new LevelReadProvider(new WorkspaceReadContext(
            index,
            new SmartTargetResolver(index),
            dbPath,
            "ws-levels",
            Path.GetDirectoryName(dbPath)!,
            Revision: 1,
            IndexFresh: true,
            FreshnessStatus: "current",
            WarningText: null,
            DisplayId: "levels",
            IndexLevel: indexLevel));
    }

    private (int Code, string Out, string Err) Run(string[] args, string dbPath)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = CliDispatch.Run(
            args,
            new WorkspaceContext(
                WorkspaceRoot: Path.GetDirectoryName(dbPath)!,
                ExtractDbPath: dbPath,
                TelemetryDbPath: Path.Combine(_dir, "telemetry.db"),
                RegistryDbPath: Path.Combine(_dir, "workspaces.db"),
                ToolsRoot: Path.Combine(_dir, ".tools"),
                WorkspaceId: null),
            stdout,
            stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private sealed class LevelReadProvider : IWorkspaceIndexProvider
    {
        private readonly WorkspaceReadContext _read;

        public LevelReadProvider(WorkspaceReadContext read) => _read = read;

        public WorkspaceReadContext Resolve(string? workspaceId, WorkspaceRefreshMode refresh) => _read;
    }
}
