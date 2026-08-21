using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Core.Search;
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
/// Markers are <c>structural_facts</c> rows carrying <c>code.marker.v1</c> — a table a symbols-level scan leaves
/// EMPTY — so a zero-hit there means "not extracted yet", never the definitive "this repo has no markers" that
/// <c>no_todo_markers</c> asserts. Pins ONE level decision across all three marker surfaces (the MCP
/// <c>search mode=markers</c> route, the CLI <c>search --mode markers</c> route, and the <c>todos</c> verb) and
/// pins the full-level artifact as byte-unchanged, since <c>MILLER_INDEX_LEVELS=full</c> is a permanent
/// zero-behavior-change guarantee.
/// </summary>
public sealed class MarkerLevelGuardTests : IDisposable
{
    private readonly string _dir;

    public MarkerLevelGuardTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mlg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void McpMarkerSearch_AtSymbolsLevel_ReportsConvergingInsteadOfNoTodoMarkers()
    {
        string dbPath = SymbolsLevelArtifact.Create(Path.Combine(_dir, "symbols-level"));
        MarkerSearchProvider provider = ProviderFor(dbPath, IndexLevels.SymbolsMetadataValue);
        var tool = new SearchTool(provider, provider);

        string compact = tool.Search("TODO", mode: "markers");
        using JsonDocument document = JsonDocument.Parse(tool.Search("TODO", mode: "markers", format: "json"));
        JsonElement diagnostic = document.RootElement.GetProperty("diagnostic");

        Assert.Contains("diagnostic_code=reference_layer_converging", compact, StringComparison.Ordinal);
        Assert.DoesNotContain("no_todo_markers", compact, StringComparison.Ordinal);
        Assert.Equal("reference_layer_converging", diagnostic.GetProperty("code").GetString());
        Assert.Equal("expected_empty", diagnostic.GetProperty("class").GetString());
        Assert.Contains("marker", diagnostic.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public void McpMarkerSearch_AtSymbolsLevel_StampsTheFactsLayerDemandCounter()
    {
        string dbPath = SymbolsLevelArtifact.Create(Path.Combine(_dir, "symbols-level"));
        MarkerSearchProvider provider = ProviderFor(dbPath, IndexLevels.SymbolsMetadataValue);
        var tool = new SearchTool(provider, provider);

        using TelemetryLedger ledger = OpenLedger();
        using TelemetryScope scope = ledger.Measure("search", "markers");
        tool.Search("TODO", mode: "markers");

        using JsonDocument metadata = JsonDocument.Parse(scope.MetadataJson);
        Assert.True(metadata.RootElement.GetProperty("degraded").GetBoolean());
        Assert.Equal("facts_layer_converging", metadata.RootElement.GetProperty("degraded_reason").GetString());
    }

    [Fact]
    public void McpMarkerSearch_AtFullLevel_StillReportsNoTodoMarkersOnAGenuineZeroHit()
    {
        string dbPath = SymbolsLevelArtifact.CreateFull(Path.Combine(_dir, "full-level"));
        MarkerSearchProvider provider = ProviderFor(dbPath, IndexLevels.FullMetadataValue);
        var tool = new SearchTool(provider, provider);

        string compact = tool.Search("TODO", mode: "markers");
        using JsonDocument document = JsonDocument.Parse(tool.Search("TODO", mode: "markers", format: "json"));
        JsonElement diagnostic = document.RootElement.GetProperty("diagnostic");

        Assert.Contains("diagnostic_code=no_todo_markers", compact, StringComparison.Ordinal);
        Assert.DoesNotContain("reference_layer_converging", compact, StringComparison.Ordinal);
        Assert.Equal("no_todo_markers", diagnostic.GetProperty("code").GetString());
        Assert.Equal("No requested source markers were found.", diagnostic.GetProperty("message").GetString());
    }

    [Fact]
    public void McpMarkerSearch_AtFullLevel_LeavesTheDemandCounterUnstamped()
    {
        string dbPath = SymbolsLevelArtifact.CreateFull(Path.Combine(_dir, "full-level"));
        MarkerSearchProvider provider = ProviderFor(dbPath, IndexLevels.FullMetadataValue);
        var tool = new SearchTool(provider, provider);

        using TelemetryLedger ledger = OpenLedger();
        using TelemetryScope scope = ledger.Measure("search", "markers");
        tool.Search("TODO", mode: "markers");

        using JsonDocument metadata = JsonDocument.Parse(scope.MetadataJson);
        Assert.False(metadata.RootElement.TryGetProperty("degraded", out _));
        Assert.False(metadata.RootElement.TryGetProperty("degraded_reason", out _));
    }

    [Fact]
    public void CliSearchModeMarkers_AtSymbolsLevel_ReportsConvergingInsteadOfNoMarkers()
    {
        string dbPath = SymbolsLevelArtifact.Create(Path.Combine(_dir, "symbols-level"));

        var (code, outText, errText) = Run(["search", "TODO", "--mode", "markers"], dbPath);

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("diagnostic_code=reference_layer_converging", outText, StringComparison.Ordinal);
        Assert.Contains("diagnostic_class=expected_empty", outText, StringComparison.Ordinal);
    }

    [Fact]
    public void CliSearchModeMarkers_AtSymbolsLevel_CarriesTheDiagnosticIntoJson()
    {
        string dbPath = SymbolsLevelArtifact.Create(Path.Combine(_dir, "symbols-level"));

        var (code, outText, _) = Run(["search", "TODO", "--mode", "markers", "--json"], dbPath);

        using JsonDocument document = JsonDocument.Parse(outText);
        Assert.Equal(0, code);
        Assert.Empty(document.RootElement.GetProperty("results").EnumerateArray());
        Assert.Equal(
            "reference_layer_converging",
            document.RootElement.GetProperty("diagnostic").GetProperty("code").GetString());
    }

    [Fact]
    public void CliSearchModeMarkers_AtFullLevel_KeepsTheUnguardedZeroHitOutput()
    {
        string dbPath = SymbolsLevelArtifact.CreateFull(Path.Combine(_dir, "full-level"));

        var (code, outText, errText) = Run(["search", "TODO", "--mode", "markers"], dbPath);

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Equal("No TODO markers.", outText.Trim());
    }

    [Fact]
    public void CliTodos_AtSymbolsLevel_ReportsTheSameConvergingDiagnosticAsSearchMarkers()
    {
        string dbPath = SymbolsLevelArtifact.Create(Path.Combine(_dir, "symbols-level"));

        var (code, outText, errText) = Run(["todos"], dbPath);
        var (_, markerText, _) = Run(["search", "TODO", "--mode", "markers"], dbPath);

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains("diagnostic_code=reference_layer_converging", outText, StringComparison.Ordinal);
        Assert.Contains("diagnostic_class=expected_empty", outText, StringComparison.Ordinal);
        Assert.Equal(DiagnosticTail(markerText), DiagnosticTail(outText));
    }

    [Fact]
    public void CliTodos_AtSymbolsLevel_CarriesTheDiagnosticIntoJson()
    {
        string dbPath = SymbolsLevelArtifact.Create(Path.Combine(_dir, "symbols-level"));

        var (code, outText, _) = Run(["todos", "--json"], dbPath);

        using JsonDocument document = JsonDocument.Parse(outText);
        Assert.Equal(0, code);
        Assert.Empty(document.RootElement.GetProperty("results").EnumerateArray());
        Assert.Equal(
            "reference_layer_converging",
            document.RootElement.GetProperty("diagnostic").GetProperty("code").GetString());
    }

    [Fact]
    public void CliTodos_AtFullLevel_KeepsTheUnguardedZeroHitOutput()
    {
        string dbPath = SymbolsLevelArtifact.CreateFull(Path.Combine(_dir, "full-level"));

        var (code, outText, errText) = Run(["todos"], dbPath);

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Equal("No TODO/FIXME/HACK/XXX markers.", outText.Trim());
    }

    private static string DiagnosticTail(string output)
    {
        int index = output.IndexOf("diagnostic_code=", StringComparison.Ordinal);
        return index < 0 ? string.Empty : output[index..].Trim();
    }

    private TelemetryLedger OpenLedger() =>
        TelemetryLedger.Open(Path.Combine(_dir, "telemetry.db"), workspaceId: "ws-markers");

    private static MarkerSearchProvider ProviderFor(string dbPath, string indexLevel) =>
        new(new WorkspaceSymbolSearchContext(
            new EmptySymbolLookupIndex(),
            dbPath,
            "ws-markers",
            Path.GetDirectoryName(dbPath)!,
            Revision: 1,
            IndexFresh: true,
            FreshnessStatus: "current",
            WarningText: null,
            DisplayId: "markers",
            IsCurrent: true,
            IndexLevel: indexLevel));

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

    private sealed class MarkerSearchProvider(WorkspaceSymbolSearchContext context)
        : IWorkspaceSearchProvider, IWorkspaceContentSearchProvider
    {
        public WorkspaceSymbolSearchContext ResolveSymbolSearch(string? workspaceId, WorkspaceRefreshMode refresh) => context;

        public WorkspaceContentSearchContext ResolveContentSearch(string? workspaceId, WorkspaceRefreshMode refresh) =>
            throw new NotSupportedException("the markers route never resolves content search");
    }

    private sealed class EmptySymbolLookupIndex : ISymbolLookupIndex
    {
        public int DocumentCount => 0;

        public IReadOnlySet<string> KnownExtensions { get; } = new HashSet<string>(StringComparer.Ordinal) { ".cs" };

        public IReadOnlyList<SearchHit> Search(string query, int limit = 10, SearchMode mode = SearchMode.Or) => [];

        public IndexedSymbol Resolve(int docId) => throw new KeyNotFoundException();

        public IReadOnlyList<IndexedSymbol> FindByName(string name) => [];

        public IndexedSymbol? FindBySymbolId(string symbolId) => null;

        public IReadOnlyList<IndexedSymbol> FindChildren(string parentId) => [];

        public IReadOnlyList<IndexedSymbol> FindByFilePath(string filePath) => [];

        public IReadOnlyList<IndexedSymbol> FindByFilePathFragment(string query, int limit) => [];

        public bool IsIndexedFilePath(string path) => false;

        public string? ResolveIndexedFilePath(string target) => null;
    }
}
