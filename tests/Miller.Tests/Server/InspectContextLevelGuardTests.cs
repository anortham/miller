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
/// <c>inspect depth=overview|full</c> and <c>context</c> both render reference evidence — the
/// <c>identifiers</c>/<c>identifier_resolutions</c> layer a symbols-level scan leaves EMPTY — so an unguarded
/// render is indistinguishable from "nothing calls this symbol". Pins the level decision on the MCP routes
/// (current-workspace AND cross-workspace, where a lean projection rather than a
/// <see cref="MillerRepositoryIndex"/> serves the read) and on the CLI verbs, plus the full-level artifact as
/// unchanged, since <c>MILLER_INDEX_LEVELS=full</c> is a permanent zero-behavior-change guarantee.
/// </summary>
public sealed class InspectContextLevelGuardTests : IDisposable
{
    private readonly string _dir;

    public InspectContextLevelGuardTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "iclg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Theory]
    [InlineData("overview")]
    [InlineData("full")]
    public void McpInspect_CurrentWorkspaceAtSymbolsLevel_ReportsReferenceLayerConverging(string depth)
    {
        string dbPath = SymbolsLevelArtifact.Create(Path.Combine(_dir, "symbols-level"));
        var tool = new InspectTool(CurrentSymbolReadProvider(dbPath, IndexLevels.SymbolsMetadataValue));

        string compact = tool.Inspect("Alpha", depth: depth);
        using JsonDocument document = JsonDocument.Parse(tool.Inspect("Alpha", depth: depth, format: "json"));
        JsonElement diagnostic = document.RootElement.GetProperty("diagnostic");

        Assert.Contains("diagnostic_code=reference_layer_converging", compact, StringComparison.Ordinal);
        Assert.Contains("diagnostic_class=expected_empty", compact, StringComparison.Ordinal);
        Assert.Equal("reference_layer_converging", diagnostic.GetProperty("code").GetString());
        Assert.Contains(
            "refs/callers/callees",
            diagnostic.GetProperty("message").GetString()!,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("overview")]
    [InlineData("full")]
    public void McpInspect_CrossWorkspaceAtSymbolsLevel_ReportsConvergingThoughNoRepositoryIndexServesTheRead(
        string depth)
    {
        string dbPath = SymbolsLevelArtifact.Create(Path.Combine(_dir, "symbols-level"));
        ISymbolLookupIndex index = SymbolSearchProjectionLoader.Load(dbPath);
        var tool = new InspectTool(
            new LevelReadProvider(
                SymbolReadContext(index, dbPath, IndexLevels.SymbolsMetadataValue, isCurrent: false)));

        string compact = tool.Inspect("Alpha", depth: depth, workspace_id: "levels");

        Assert.IsNotType<MillerRepositoryIndex>(index);
        Assert.Contains("diagnostic_code=reference_layer_converging", compact, StringComparison.Ordinal);
    }

    [Fact]
    public void McpInspect_AtSymbolsLevel_StampsTheReferenceLayerDemandCounter()
    {
        string dbPath = SymbolsLevelArtifact.Create(Path.Combine(_dir, "symbols-level"));
        var tool = new InspectTool(CurrentSymbolReadProvider(dbPath, IndexLevels.SymbolsMetadataValue));

        using TelemetryLedger ledger = OpenLedger();
        using TelemetryScope scope = ledger.Measure("inspect", "overview");
        tool.Inspect("Alpha", depth: "overview");

        using JsonDocument metadata = JsonDocument.Parse(scope.MetadataJson);
        Assert.True(metadata.RootElement.GetProperty("degraded").GetBoolean());
        Assert.Equal("reference_layer_converging", metadata.RootElement.GetProperty("degraded_reason").GetString());
    }

    [Fact]
    public void McpInspectSummary_AtSymbolsLevel_StaysUnguarded()
    {
        string dbPath = SymbolsLevelArtifact.Create(Path.Combine(_dir, "symbols-level"));
        var tool = new InspectTool(CurrentSymbolReadProvider(dbPath, IndexLevels.SymbolsMetadataValue));

        using TelemetryLedger ledger = OpenLedger();
        using TelemetryScope scope = ledger.Measure("inspect", "summary");
        string compact = tool.Inspect("Alpha");

        using JsonDocument metadata = JsonDocument.Parse(scope.MetadataJson);
        Assert.DoesNotContain("reference_layer_converging", compact, StringComparison.Ordinal);
        Assert.False(metadata.RootElement.TryGetProperty("degraded", out _));
    }

    [Theory]
    [InlineData("summary")]
    [InlineData("overview")]
    [InlineData("full")]
    public void McpInspect_AtFullLevel_AttachesNothingAndLeavesTheDemandCounterUnstamped(string depth)
    {
        string dbPath = SymbolsLevelArtifact.CreateFull(Path.Combine(_dir, "full-level"));
        var tool = new InspectTool(CurrentSymbolReadProvider(dbPath, IndexLevels.FullMetadataValue));

        using TelemetryLedger ledger = OpenLedger();
        using TelemetryScope scope = ledger.Measure("inspect", depth);
        string compact = tool.Inspect("Alpha", depth: depth);
        using JsonDocument document = JsonDocument.Parse(tool.Inspect("Alpha", depth: depth, format: "json"));

        using JsonDocument metadata = JsonDocument.Parse(scope.MetadataJson);
        Assert.DoesNotContain("diagnostic_code=", compact, StringComparison.Ordinal);
        Assert.False(document.RootElement.TryGetProperty("diagnostic", out _));
        Assert.False(metadata.RootElement.TryGetProperty("degraded", out _));
    }

    [Fact]
    public void McpInspect_CrossWorkspaceAtFullLevel_AttachesNothing()
    {
        string dbPath = SymbolsLevelArtifact.CreateFull(Path.Combine(_dir, "full-level"));
        ISymbolLookupIndex index = SymbolSearchProjectionLoader.Load(dbPath);
        var tool = new InspectTool(
            new LevelReadProvider(
                SymbolReadContext(index, dbPath, IndexLevels.FullMetadataValue, isCurrent: false)));

        string compact = tool.Inspect("Alpha", depth: "overview", workspace_id: "levels");

        Assert.DoesNotContain("diagnostic_code=", compact, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("usage")]
    public void McpContext_AtSymbolsLevel_ReportsReferenceLayerConverging(string referenceMode)
    {
        string dbPath = SymbolsLevelArtifact.Create(Path.Combine(_dir, "symbols-level"));
        var tool = new ContextTool(ReadProvider(dbPath, IndexLevels.SymbolsMetadataValue));

        string compact = tool.Context("Alpha", reference_mode: referenceMode);
        using JsonDocument document = JsonDocument.Parse(
            tool.Context("Alpha", reference_mode: referenceMode, format: "json"));
        JsonElement diagnostic = document.RootElement.GetProperty("diagnostic");

        Assert.Contains("diagnostic_code=reference_layer_converging", compact, StringComparison.Ordinal);
        Assert.Contains("diagnostic_class=expected_empty", compact, StringComparison.Ordinal);
        Assert.Equal("reference_layer_converging", diagnostic.GetProperty("code").GetString());
    }

    [Fact]
    public void McpContext_AtSymbolsLevel_StampsTheReferenceLayerDemandCounter()
    {
        string dbPath = SymbolsLevelArtifact.Create(Path.Combine(_dir, "symbols-level"));
        var tool = new ContextTool(ReadProvider(dbPath, IndexLevels.SymbolsMetadataValue));

        using TelemetryLedger ledger = OpenLedger();
        using TelemetryScope scope = ledger.Measure("context", "off");
        tool.Context("Alpha");

        using JsonDocument metadata = JsonDocument.Parse(scope.MetadataJson);
        Assert.True(metadata.RootElement.GetProperty("degraded").GetBoolean());
        Assert.Equal("reference_layer_converging", metadata.RootElement.GetProperty("degraded_reason").GetString());
    }

    [Theory]
    [InlineData("off")]
    [InlineData("usage")]
    public void McpContext_AtFullLevel_AttachesNothingAndLeavesTheDemandCounterUnstamped(string referenceMode)
    {
        string dbPath = SymbolsLevelArtifact.CreateFull(Path.Combine(_dir, "full-level"));
        var tool = new ContextTool(ReadProvider(dbPath, IndexLevels.FullMetadataValue));

        using TelemetryLedger ledger = OpenLedger();
        using TelemetryScope scope = ledger.Measure("context", referenceMode);
        string compact = tool.Context("Alpha", reference_mode: referenceMode);

        using JsonDocument metadata = JsonDocument.Parse(scope.MetadataJson);
        Assert.DoesNotContain("diagnostic_code=", compact, StringComparison.Ordinal);
        Assert.False(metadata.RootElement.TryGetProperty("degraded", out _));
    }

    [Theory]
    [InlineData("overview")]
    [InlineData("full")]
    public void CliInspect_AtSymbolsLevel_ReportsReferenceLayerConverging(string depth)
    {
        string dbPath = SymbolsLevelArtifact.Create(Path.Combine(_dir, "symbols-level"));

        var (code, outText, errText) = Run(["inspect", "Alpha", "--depth", depth], dbPath);

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("diagnostic_code=reference_layer_converging", outText, StringComparison.Ordinal);
        Assert.Contains("diagnostic_class=expected_empty", outText, StringComparison.Ordinal);
    }

    [Fact]
    public void CliInspect_AtSymbolsLevel_CarriesTheDiagnosticIntoJson()
    {
        string dbPath = SymbolsLevelArtifact.Create(Path.Combine(_dir, "symbols-level"));

        var (code, outText, _) = Run(["inspect", "Alpha", "--depth", "overview", "--json"], dbPath);

        using JsonDocument document = JsonDocument.Parse(outText);
        Assert.Equal(0, code);
        Assert.Equal(
            "reference_layer_converging",
            document.RootElement.GetProperty("diagnostic").GetProperty("code").GetString());
    }

    [Fact]
    public void CliInspectSummary_AtSymbolsLevel_StaysUnguarded()
    {
        string dbPath = SymbolsLevelArtifact.Create(Path.Combine(_dir, "symbols-level"));

        var (code, outText, errText) = Run(["inspect", "Alpha"], dbPath);

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.DoesNotContain("reference_layer_converging", outText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("summary")]
    [InlineData("overview")]
    [InlineData("full")]
    public void CliInspect_AtFullLevel_KeepsTheUnguardedOutput(string depth)
    {
        string dbPath = SymbolsLevelArtifact.CreateFull(Path.Combine(_dir, "full-level"));

        var (code, outText, errText) = Run(["inspect", "Alpha", "--depth", depth], dbPath);

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.DoesNotContain("diagnostic_code=", outText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("usage")]
    public void CliContext_AtSymbolsLevel_ReportsReferenceLayerConverging(string referenceMode)
    {
        string dbPath = SymbolsLevelArtifact.Create(Path.Combine(_dir, "symbols-level"));

        var (code, outText, errText) = Run(
            ["context", "Alpha", "--reference-mode", referenceMode],
            dbPath);

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("diagnostic_code=reference_layer_converging", outText, StringComparison.Ordinal);
        Assert.Contains("diagnostic_class=expected_empty", outText, StringComparison.Ordinal);
    }

    [Fact]
    public void CliContext_AtSymbolsLevel_CarriesTheDiagnosticIntoJson()
    {
        string dbPath = SymbolsLevelArtifact.Create(Path.Combine(_dir, "symbols-level"));

        var (code, outText, _) = Run(["context", "Alpha", "--reference-mode", "usage", "--json"], dbPath);

        using JsonDocument document = JsonDocument.Parse(outText);
        Assert.Equal(0, code);
        Assert.Equal(
            "reference_layer_converging",
            document.RootElement.GetProperty("diagnostic").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("off")]
    [InlineData("usage")]
    public void CliContext_AtFullLevel_KeepsTheUnguardedOutput(string referenceMode)
    {
        string dbPath = SymbolsLevelArtifact.CreateFull(Path.Combine(_dir, "full-level"));

        var (code, outText, errText) = Run(["context", "Alpha", "--reference-mode", referenceMode], dbPath);

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.DoesNotContain("diagnostic_code=", outText, StringComparison.Ordinal);
    }

    private TelemetryLedger OpenLedger() =>
        TelemetryLedger.Open(Path.Combine(_dir, "telemetry.db"), workspaceId: "ws-levels");

    private static LevelReadProvider CurrentSymbolReadProvider(string dbPath, string indexLevel) =>
        new(SymbolReadContext(RepositoryIndexLoader.Load(dbPath), dbPath, indexLevel, isCurrent: true));

    private static LevelReadProvider ReadProvider(string dbPath, string indexLevel)
    {
        MillerRepositoryIndex index = RepositoryIndexLoader.Load(dbPath);
        return new LevelReadProvider(
            SymbolReadContext(index, dbPath, indexLevel, isCurrent: true),
            new WorkspaceReadContext(
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

    private static WorkspaceSymbolReadContext SymbolReadContext(
        ISymbolLookupIndex index,
        string dbPath,
        string indexLevel,
        bool isCurrent) =>
        new(
            index,
            dbPath,
            "ws-levels",
            Path.GetDirectoryName(dbPath)!,
            Revision: 1,
            IndexFresh: true,
            FreshnessStatus: "current",
            WarningText: null,
            DisplayId: "levels",
            IsCurrent: isCurrent,
            IndexLevel: indexLevel);

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

    private sealed class LevelReadProvider : IWorkspaceSymbolReadProvider, IWorkspaceIndexProvider
    {
        private readonly WorkspaceSymbolReadContext _symbolRead;
        private readonly WorkspaceReadContext? _read;

        public LevelReadProvider(WorkspaceSymbolReadContext symbolRead, WorkspaceReadContext? read = null)
        {
            _symbolRead = symbolRead;
            _read = read;
        }

        public WorkspaceSymbolReadContext ResolveSymbolRead(string? workspaceId, bool ensureFresh) => _symbolRead;

        public WorkspaceReadContext Resolve(string? workspaceId, bool ensureFresh) =>
            _read ?? throw new NotSupportedException("this provider serves symbol reads only");
    }
}
