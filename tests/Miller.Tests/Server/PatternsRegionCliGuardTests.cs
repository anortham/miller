using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Server;
using Miller.Server.Cli;
using Miller.Server.Telemetry;
using Miller.Server.Tools;
using Miller.Server.Workspaces;
using Miller.Tests.Support;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// <c>structural_facts</c> and <c>source_regions</c> are two of the tables a symbols-level scan leaves EMPTY, so
/// a zero-row patterns or region-search result there means "not extracted yet", never "this workspace has no
/// such facts". The MCP tool already said so; the CLI — which is the documented Eros-facing contract surface —
/// did not, and handed a fleet consumer a clean, empty, authoritative-looking feed. Pins ONE level decision
/// across the MCP tool, the three CLI patterns verbs, the <c>patterns export</c> JSONL feed, and CLI region
/// search, and pins the full-level artifact as byte-unchanged, since <c>MILLER_INDEX_LEVELS=full</c> is a
/// permanent zero-behavior-change guarantee.
/// </summary>
public sealed class PatternsRegionCliGuardTests : IDisposable
{
    private readonly string _dir;

    public PatternsRegionCliGuardTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "prg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void McpPatterns_AtSymbolsLevel_ReportsTheConvergingDiagnostic()
    {
        string dbPath = SymbolsLevelArtifact.Create(Path.Combine(_dir, "symbols-level"));
        PatternsTool tool = ToolFor(dbPath, IndexLevels.SymbolsMetadataValue);

        string compact = tool.Patterns(operation: "list");
        using JsonDocument document = JsonDocument.Parse(tool.Patterns(operation: "list", format: "json"));
        JsonElement diagnostic = document.RootElement.GetProperty("diagnostic");

        Assert.Contains("diagnostic_code=reference_layer_converging", compact, StringComparison.Ordinal);
        Assert.Equal("reference_layer_converging", diagnostic.GetProperty("code").GetString());
        Assert.Equal("expected_empty", diagnostic.GetProperty("class").GetString());
        Assert.Contains("structural facts", diagnostic.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public void McpPatterns_AtSymbolsLevel_StampsTheFactsLayerDemandCounter()
    {
        string dbPath = SymbolsLevelArtifact.Create(Path.Combine(_dir, "symbols-level"));
        PatternsTool tool = ToolFor(dbPath, IndexLevels.SymbolsMetadataValue);

        using TelemetryLedger ledger = TelemetryLedger.Open(
            Path.Combine(_dir, "telemetry.db"), workspaceId: "ws-patterns");
        using TelemetryScope scope = ledger.Measure("patterns", "list");
        tool.Patterns(operation: "list");

        using JsonDocument metadata = JsonDocument.Parse(scope.MetadataJson);
        Assert.True(metadata.RootElement.GetProperty("degraded").GetBoolean());
        Assert.Equal("facts_layer_converging", metadata.RootElement.GetProperty("degraded_reason").GetString());
    }

    [Fact]
    public void McpPatterns_AtFullLevel_LeavesTheDemandCounterUnstampedAndTheFactsVisible()
    {
        string dbPath = SymbolsLevelArtifact.CreateFull(Path.Combine(_dir, "full-level"));
        PatternsTool tool = ToolFor(dbPath, IndexLevels.FullMetadataValue);

        using TelemetryLedger ledger = TelemetryLedger.Open(
            Path.Combine(_dir, "telemetry.db"), workspaceId: "ws-patterns");
        using TelemetryScope scope = ledger.Measure("patterns", "list");
        string compact = tool.Patterns(operation: "list");

        using JsonDocument metadata = JsonDocument.Parse(scope.MetadataJson);
        Assert.Contains("csharp.class.v1", compact, StringComparison.Ordinal);
        Assert.DoesNotContain("reference_layer_converging", compact, StringComparison.Ordinal);
        Assert.False(metadata.RootElement.TryGetProperty("degraded", out _));
        Assert.False(metadata.RootElement.TryGetProperty("degraded_reason", out _));
    }

    [Theory]
    [InlineData("list")]
    [InlineData("summary")]
    [InlineData("search")]
    public void CliPatterns_AtSymbolsLevel_ReportsTheConvergingDiagnostic(string operation)
    {
        string dbPath = SymbolsLevelArtifact.Create(Path.Combine(_dir, "symbols-level"));

        var (code, outText, errText) = Run(PatternsArgs(operation), dbPath);

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("diagnostic_code=reference_layer_converging", outText, StringComparison.Ordinal);
        Assert.Contains("diagnostic_class=expected_empty", outText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("summary")]
    [InlineData("search")]
    public void CliPatterns_AtSymbolsLevel_CarriesTheDiagnosticIntoJson(string operation)
    {
        string dbPath = SymbolsLevelArtifact.Create(Path.Combine(_dir, "symbols-level"));

        var (code, outText, _) = Run([.. PatternsArgs(operation), "--json"], dbPath);

        using JsonDocument document = JsonDocument.Parse(outText);
        Assert.Equal(0, code);
        Assert.Equal(
            "reference_layer_converging",
            document.RootElement.GetProperty("diagnostic").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("list")]
    [InlineData("summary")]
    [InlineData("search")]
    public void CliPatterns_AtFullLevel_EmitsTheCoreRenderByteForByte(string operation)
    {
        string dbPath = SymbolsLevelArtifact.CreateFull(Path.Combine(_dir, "full-level"));

        var (code, outText, errText) = Run(PatternsArgs(operation), dbPath);

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Equal(CoreRender(dbPath, operation), outText.TrimEnd('\n'));
        Assert.DoesNotContain("diagnostic_", outText, StringComparison.Ordinal);
    }

    [Fact]
    public void CliPatternsExport_AtSymbolsLevel_WarnsOnStderrAndLeavesStdoutAnUncorruptedJsonlStream()
    {
        string dbPath = SymbolsLevelArtifact.Create(Path.Combine(_dir, "symbols-level"));

        var (code, outText, errText) = Run(["patterns", "export", "--jsonl"], dbPath);

        Assert.Equal(0, code);
        Assert.Equal(string.Empty, outText);
        Assert.Contains("diagnostic_code=reference_layer_converging", errText, StringComparison.Ordinal);
        Assert.Contains("diagnostic_class=expected_empty", errText, StringComparison.Ordinal);
    }

    [Fact]
    public void CliPatternsExport_AtFullLevel_EmitsTheFeedWithNoWarning()
    {
        string dbPath = SymbolsLevelArtifact.CreateFull(Path.Combine(_dir, "full-level"));

        var (code, outText, errText) = Run(["patterns", "export", "--jsonl"], dbPath);
        string[] lines = outText.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Equal(PatternFactsExportReader.ExportJsonLines(dbPath), outText);
        Assert.All(lines, line =>
        {
            using JsonDocument row = JsonDocument.Parse(line);
            Assert.Equal("csharp.class.v1", row.RootElement.GetProperty("pattern_id").GetString());
        });
    }

    [Fact]
    public void CliPatternsExport_AtSymbolsLevel_KeepsEveryStdoutLineParseableAsAFactRow()
    {
        string dbPath = SymbolsLevelArtifact.Create(Path.Combine(_dir, "symbols-level"));

        var (_, outText, _) = Run(["patterns", "export", "--jsonl"], dbPath);

        Assert.All(
            outText.Split('\n', StringSplitOptions.RemoveEmptyEntries),
            line => Assert.NotNull(JsonDocument.Parse(line).RootElement.GetProperty("structural_fact_id").GetString()));
    }

    [Fact]
    public void CliSymbolsExport_AtSymbolsLevel_StaysUnwarned()
    {
        string dbPath = SymbolsLevelArtifact.Create(Path.Combine(_dir, "symbols-level"));

        var (code, outText, errText) = Run(["symbols", "export", "--jsonl"], dbPath);

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.NotEmpty(outText);
    }

    [Fact]
    public void CliSearchRegions_AtSymbolsLevel_ReportsTheConvergingDiagnostic()
    {
        string dbPath = SymbolsLevelArtifact.Create(Path.Combine(_dir, "symbols-level"));
        WriteRegionSearchDb(dbPath);

        var (code, outText, errText) = Run(["search", "Alpha", "--regions", "comment"], dbPath);

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("diagnostic_code=reference_layer_converging", outText, StringComparison.Ordinal);
        Assert.Contains("diagnostic_class=expected_empty", outText, StringComparison.Ordinal);
    }

    [Fact]
    public void CliSearchRegions_AtSymbolsLevel_CarriesTheDiagnosticIntoJson()
    {
        string dbPath = SymbolsLevelArtifact.Create(Path.Combine(_dir, "symbols-level"));
        WriteRegionSearchDb(dbPath);

        var (code, outText, _) = Run(["search", "Alpha", "--regions", "comment", "--json"], dbPath);

        using JsonDocument document = JsonDocument.Parse(outText);
        Assert.Equal(0, code);
        Assert.Equal(
            "reference_layer_converging",
            document.RootElement.GetProperty("diagnostic").GetProperty("code").GetString());
    }

    [Fact]
    public void CliSearchRegions_AtSymbolsLevel_MatchesTheMcpRegionRouteDiagnostic()
    {
        string dbPath = SymbolsLevelArtifact.Create(Path.Combine(_dir, "symbols-level"));
        WriteRegionSearchDb(dbPath);
        RegionSearchProvider provider = RegionProviderFor(dbPath, IndexLevels.SymbolsMetadataValue);
        var mcp = new SearchTool(provider, provider);

        var (_, cliText, _) = Run(["search", "Alpha", "--regions", "comment"], dbPath);
        string mcpText = mcp.Search("Alpha", regions: "comment");

        Assert.Equal(DiagnosticTail(mcpText), DiagnosticTail(cliText));
        Assert.NotEqual(string.Empty, DiagnosticTail(cliText));
    }

    [Fact]
    public void CliSearchRegions_AtFullLevel_KeepsTheUnguardedRegionOutput()
    {
        string dbPath = SymbolsLevelArtifact.CreateFull(Path.Combine(_dir, "full-level"));
        WriteRegionSearchDb(dbPath);

        var (code, outText, errText) = Run(["search", "Alpha", "--regions", "comment"], dbPath);

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("src/Alpha.cs:1", outText, StringComparison.Ordinal);
        Assert.DoesNotContain("diagnostic_", outText, StringComparison.Ordinal);
    }

    private static string[] PatternsArgs(string operation) => operation switch
    {
        "search" => ["patterns", "search", "--pattern", "csharp.class.v1"],
        _ => ["patterns", operation],
    };

    private static string CoreRender(string dbPath, string operation) =>
        PatternsTool.Run(
            new PatternFactsReader(),
            dbPath,
            operation,
            operation == "search" ? "csharp.class.v1" : null,
            query: null,
            language: null,
            path: null,
            where: null,
            groupBy: null,
            facet: null,
            limit: PatternsTool.DefaultLimit,
            json: false).Output.TrimEnd('\n');

    private static string DiagnosticTail(string output)
    {
        int index = output.IndexOf("diagnostic_code=", StringComparison.Ordinal);
        return index < 0 ? string.Empty : output[index..].Trim();
    }

    private static void WriteRegionSearchDb(string dbPath) =>
        SearchIndexWriter.Write(
            SymbolSearchSidecar.SearchDbPathFor(dbPath),
            SqliteSymbolReader.Read(dbPath),
            revision: 1,
            dbPath,
            Path.GetDirectoryName(dbPath)!,
            RegionIndexOptions.EnabledDefault);

    private static PatternsTool ToolFor(string dbPath, string indexLevel) =>
        new(
            new SingleArtifactProvider(new WorkspaceArtifactContext(
                IndexDbPath: dbPath,
                WorkspaceId: "ws-patterns",
                WorkspaceRoot: Path.GetDirectoryName(dbPath)!,
                Revision: 1,
                IndexFresh: true,
                FreshnessStatus: "current",
                WarningText: null,
                DisplayId: "patterns",
                IndexLevel: indexLevel)),
            new PatternFactsReader());

    private static RegionSearchProvider RegionProviderFor(string dbPath, string indexLevel)
    {
        using var freshness = new FreshnessReader(dbPath);
        var identity = new SymbolsArtifactIdentity(1, freshness.ArtifactId(), ArtifactStampState.Present);
        return new RegionSearchProvider(new WorkspaceRegionSearchContext(
            FtsRegionSearchIndex.Open(SymbolSearchSidecar.SearchDbPathFor(dbPath), 1, identity),
            dbPath,
            "ws-regions",
            Path.GetDirectoryName(dbPath)!,
            Revision: 1,
            IndexFresh: true,
            FreshnessStatus: "current",
            WarningText: null,
            DisplayId: "regions",
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

    private sealed class SingleArtifactProvider(WorkspaceArtifactContext context) : IWorkspaceArtifactProvider
    {
        public WorkspaceArtifactContext ResolveArtifact(string? workspaceId, bool ensureFresh) => context;
    }

    private sealed class RegionSearchProvider(WorkspaceRegionSearchContext context)
        : IWorkspaceSearchProvider, IWorkspaceRegionSearchProvider, IWorkspaceContentSearchProvider
    {
        public WorkspaceRegionSearchContext ResolveRegionSearch(string? workspaceId, bool ensureFresh) => context;

        public WorkspaceSymbolSearchContext ResolveSymbolSearch(string? workspaceId, bool ensureFresh) =>
            throw new NotSupportedException("the regions route never resolves symbol search");

        public WorkspaceContentSearchContext ResolveContentSearch(string? workspaceId, bool ensureFresh) =>
            throw new NotSupportedException("the regions route never resolves content search");
    }
}
