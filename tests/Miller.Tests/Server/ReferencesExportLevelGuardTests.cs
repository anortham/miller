using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Server;
using Miller.Server.Cli;
using Miller.Tests.Support;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// <c>references export</c> degrades DIFFERENTLY from <c>patterns export</c>. Its query is a union of four arms;
/// a symbols-level scan empties the <c>identifiers</c> and <c>identifier_resolutions</c> arms while leaving the
/// <c>relationships</c> arm populated, so the feed is PARTIAL rather than empty. A consumer streaming stdout sees
/// real rows and no signal that a whole class of references is missing, which is why this feed carries both the
/// out-of-band stderr warning and an in-band per-row <c>index_level</c>.
/// </summary>
public sealed class ReferencesExportLevelGuardTests : IDisposable
{
    private readonly string _dir;

    public ReferencesExportLevelGuardTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "rex-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void CliReferencesExport_AtSymbolsLevel_WarnsOnStderrThatIdentifierDerivedReferencesAreAbsent()
    {
        string dbPath = SymbolsLevelArtifact.Create(Path.Combine(_dir, "symbols-level"));

        var (code, outText, errText) = Run(["references", "export", "--jsonl"], dbPath);

        Assert.Equal(0, code);
        Assert.NotEmpty(outText);
        Assert.Contains("diagnostic_code=reference_layer_converging", errText, StringComparison.Ordinal);
        Assert.Contains("identifiers", errText, StringComparison.Ordinal);
        Assert.Contains("identifier_resolutions", errText, StringComparison.Ordinal);
        Assert.Contains("partial", errText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CliReferencesExport_AtSymbolsLevel_KeepsStdoutAPureJsonlStreamOfReferenceRows()
    {
        string dbPath = SymbolsLevelArtifact.Create(Path.Combine(_dir, "symbols-level"));

        var (_, outText, _) = Run(["references", "export", "--jsonl"], dbPath);
        string[] lines = outText.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.NotEmpty(lines);
        Assert.All(lines, line =>
        {
            using JsonDocument row = JsonDocument.Parse(line);
            Assert.NotNull(row.RootElement.GetProperty("reference_site_id").GetString());
            Assert.Equal(ReferenceExportReader.SchemaVersion, row.RootElement.GetProperty("schema_version").GetInt32());
        });
    }

    [Fact]
    public void CliReferencesExport_AtSymbolsLevel_StampsEveryRowWithTheSymbolsIndexLevel()
    {
        string dbPath = SymbolsLevelArtifact.Create(Path.Combine(_dir, "symbols-level"));

        var (_, outText, _) = Run(["references", "export", "--jsonl"], dbPath);
        string[] lines = outText.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.NotEmpty(lines);
        Assert.All(lines, line =>
        {
            using JsonDocument row = JsonDocument.Parse(line);
            Assert.Equal(
                IndexLevels.SymbolsMetadataValue,
                row.RootElement.GetProperty("index_level").GetString());
        });
    }

    [Fact]
    public void CliReferencesExport_AtFullLevel_EmitsTheFeedWithNoWarning()
    {
        string dbPath = SymbolsLevelArtifact.CreateFull(Path.Combine(_dir, "full-level"));

        var (code, outText, errText) = Run(["references", "export", "--jsonl"], dbPath);
        string[] lines = outText.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Equal(ReferenceExportReader.ExportJsonLines(dbPath), outText);
        Assert.NotEmpty(lines);
        Assert.All(lines, line =>
        {
            using JsonDocument row = JsonDocument.Parse(line);
            Assert.Equal(
                IndexLevels.FullMetadataValue,
                row.RootElement.GetProperty("index_level").GetString());
        });
    }

    [Fact]
    public void CliReferencesExport_AtSymbolsLevel_OmitsTheIdentifierDerivedProvenanceItStillReportsAtFullLevel()
    {
        string symbolsDb = SymbolsLevelArtifact.Create(Path.Combine(_dir, "symbols-level"));
        string fullDb = SymbolsLevelArtifact.CreateFull(Path.Combine(_dir, "full-level"));

        var (_, symbolsOut, _) = Run(["references", "export", "--jsonl"], symbolsDb);
        var (_, fullOut, _) = Run(["references", "export", "--jsonl"], fullDb);

        Assert.DoesNotContain("identifier_resolution", symbolsOut, StringComparison.Ordinal);
        Assert.Contains("identifier_resolution", fullOut, StringComparison.Ordinal);
    }

    [Fact]
    public void CliComplexityExport_AtSymbolsLevel_StaysUnwarned()
    {
        string dbPath = SymbolsLevelArtifact.Create(Path.Combine(_dir, "symbols-level"));

        var (code, outText, errText) = Run(["complexity", "export", "--jsonl"], dbPath);

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.NotEmpty(outText);
    }

    [Fact]
    public void CliSymbolsExport_AtSymbolsLevel_CarriesNoIndexLevelField()
    {
        string dbPath = SymbolsLevelArtifact.Create(Path.Combine(_dir, "symbols-level"));

        var (_, outText, _) = Run(["symbols", "export", "--jsonl"], dbPath);

        Assert.All(
            outText.Split('\n', StringSplitOptions.RemoveEmptyEntries),
            line => Assert.False(JsonDocument.Parse(line).RootElement.TryGetProperty("index_level", out _)));
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
}
