using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Server;
using Miller.Server.Cli;
using Miller.Server.Tools;
using Miller.Server.Workspaces;
using Miller.Tests.Support;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// A file target that never reached the index and an indexed file that genuinely holds no symbols produced the
/// same <c>no_file_symbols</c> answer, so a typo'd path read as a real, empty file and the agent moved on. Pins
/// the <c>files</c>-table evidence that separates them, the recovery action on the not-indexed case, and
/// <c>no_file_symbols</c> as unchanged wherever the path IS indexed.
/// </summary>
public sealed class InspectMissingFileTests : IDisposable
{
    private readonly string _dir;

    public InspectMissingFileTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "imf-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Inspect_PathAbsentFromTheIndex_ReportsFileNotIndexedRatherThanNoFileSymbols()
    {
        InspectTool tool = ToolOver(SymbolsLevelArtifact.Create(Workspace()));

        string compact = tool.Inspect("src/Aplha.cs");

        Assert.Contains("diagnostic_code=file_not_indexed", compact, StringComparison.Ordinal);
        Assert.Contains("diagnostic_class=expected_empty", compact, StringComparison.Ordinal);
        Assert.DoesNotContain("no_file_symbols", compact, StringComparison.Ordinal);
    }

    [Fact]
    public void Inspect_PathAbsentFromTheIndex_NamesThePathAsNotIndexedInJson()
    {
        InspectTool tool = ToolOver(SymbolsLevelArtifact.Create(Workspace()));

        using JsonDocument document = JsonDocument.Parse(tool.Inspect("src/Aplha.cs", format: "json"));
        JsonElement diagnostic = document.RootElement.GetProperty("diagnostic");

        Assert.Equal("file_not_indexed", diagnostic.GetProperty("code").GetString());
        Assert.Equal("expected_empty", diagnostic.GetProperty("class").GetString());
        Assert.Equal("empty", diagnostic.GetProperty("outcome").GetString());
        Assert.Contains("src/Aplha.cs", diagnostic.GetProperty("message").GetString()!, StringComparison.Ordinal);
        Assert.Contains(
            "not in the index",
            diagnostic.GetProperty("message").GetString()!,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Inspect_PathAbsentFromTheIndex_OffersAFileSearchForTheIntendedPath()
    {
        InspectTool tool = ToolOver(SymbolsLevelArtifact.Create(Workspace()));

        string compact = tool.Inspect("src/Aplha.cs");
        using JsonDocument document = JsonDocument.Parse(tool.Inspect("src/Aplha.cs", format: "json"));
        JsonElement action = document.RootElement
            .GetProperty("diagnostic")
            .GetProperty("next_actions")[0];

        Assert.Contains("search(query=\"Aplha.cs\", mode=\"file\")", compact, StringComparison.Ordinal);
        Assert.Equal("search(query=\"Aplha.cs\", mode=\"file\")", action.GetProperty("call").GetString());
        Assert.Equal("find the intended path", action.GetProperty("reason").GetString());
    }

    [Fact]
    public void Inspect_IndexedFileCarryingNoSymbols_KeepsNoFileSymbolsUnchanged()
    {
        string workspace = Workspace();
        string dbPath = SymbolsLevelArtifact.Create(workspace);
        AddSymbolFreeIndexedFile(dbPath, "src/Empty.cs");
        InspectTool tool = ToolOver(dbPath);

        string compact = tool.Inspect("src/Empty.cs");
        using JsonDocument document = JsonDocument.Parse(tool.Inspect("src/Empty.cs", format: "json"));
        JsonElement diagnostic = document.RootElement.GetProperty("diagnostic");

        Assert.Contains("diagnostic_code=no_file_symbols", compact, StringComparison.Ordinal);
        Assert.DoesNotContain("file_not_indexed", compact, StringComparison.Ordinal);
        Assert.Equal("no_file_symbols", diagnostic.GetProperty("code").GetString());
        Assert.Equal("expected_empty", diagnostic.GetProperty("class").GetString());
        Assert.Empty(diagnostic.GetProperty("next_actions").EnumerateArray());
    }

    [Fact]
    public void Inspect_IndexedFileWhoseSymbolsTheKindFilterExcludes_KeepsNoFileSymbolsUnchanged()
    {
        InspectTool tool = ToolOver(SymbolsLevelArtifact.Create(Workspace()));

        string compact = tool.Inspect("src/Alpha.cs", kind: "interface");

        Assert.Contains("diagnostic_code=no_file_symbols", compact, StringComparison.Ordinal);
        Assert.DoesNotContain("file_not_indexed", compact, StringComparison.Ordinal);
    }

    [Fact]
    public void Inspect_PathPresentOnDiskButAbsentFromTheIndex_StillReportsFileNotIndexed()
    {
        string workspace = Workspace();
        string dbPath = SymbolsLevelArtifact.Create(workspace);
        Directory.CreateDirectory(Path.Combine(workspace, "src"));
        File.WriteAllText(Path.Combine(workspace, "src", "Ignored.cs"), "public class Ignored { }\n");
        InspectTool tool = ToolOver(dbPath);

        string compact = tool.Inspect("src/Ignored.cs");

        Assert.Contains("diagnostic_code=file_not_indexed", compact, StringComparison.Ordinal);
    }

    [Fact]
    public void Inspect_IndexedFileWithSymbols_AttachesNoDiagnostic()
    {
        InspectTool tool = ToolOver(SymbolsLevelArtifact.Create(Workspace()));

        string compact = tool.Inspect("src/Alpha.cs");

        Assert.Contains("Alpha", compact, StringComparison.Ordinal);
        Assert.DoesNotContain("diagnostic_code=", compact, StringComparison.Ordinal);
    }

    [Fact]
    public void Inspect_OverALeanProjection_StillSeparatesTheNotIndexedPathFromTheSymbolFreeOne()
    {
        string dbPath = SymbolsLevelArtifact.Create(Workspace());
        AddSymbolFreeIndexedFile(dbPath, "src/Empty.cs");
        ISymbolLookupIndex index = SymbolSearchProjectionLoader.Load(dbPath);
        var tool = new InspectTool(new SymbolReadProvider(ReadContext(index, dbPath)));

        string missing = tool.Inspect("src/Aplha.cs");
        string empty = tool.Inspect("src/Empty.cs");

        Assert.IsNotType<MillerRepositoryIndex>(index);
        Assert.Contains("diagnostic_code=file_not_indexed", missing, StringComparison.Ordinal);
        Assert.Contains("diagnostic_code=no_file_symbols", empty, StringComparison.Ordinal);
    }

    [Fact]
    public void Inspect_PathAbsentFromTheIndexWithAnUnreadableArtifact_FallsBackToNoFileSymbols()
    {
        string workspace = Workspace();
        string dbPath = SymbolsLevelArtifact.Create(workspace);
        InspectTool tool = ToolOver(dbPath, dbPathOverride: Path.Combine(workspace, "absent.db"));

        string compact = tool.Inspect("src/Aplha.cs");

        Assert.Contains("diagnostic_code=no_file_symbols", compact, StringComparison.Ordinal);
        Assert.DoesNotContain("file_not_indexed", compact, StringComparison.Ordinal);
    }

    [Fact]
    public void CliInspect_PathAbsentFromTheIndex_ReportsFileNotIndexedJustLikeTheMcpTool()
    {
        string dbPath = SymbolsLevelArtifact.Create(Workspace());

        (int code, string output) = Cli(dbPath, ["inspect", "src/Aplha.cs"]);

        Assert.Equal(0, code);
        Assert.Contains("diagnostic_code=file_not_indexed", output, StringComparison.Ordinal);
        Assert.Contains("search(query=\"Aplha.cs\", mode=\"file\")", output, StringComparison.Ordinal);
    }

    [Fact]
    public void CliInspect_PathAbsentFromTheIndexAtOverviewDepth_PrefersFileNotIndexedOverTheLevelGuard()
    {
        string dbPath = SymbolsLevelArtifact.Create(Workspace());

        (_, string output) = Cli(dbPath, ["inspect", "src/Aplha.cs", "--depth", "overview"]);

        Assert.Contains("diagnostic_code=file_not_indexed", output, StringComparison.Ordinal);
        Assert.DoesNotContain("reference_layer_converging", output, StringComparison.Ordinal);
    }

    [Fact]
    public void CliInspect_PathAbsentFromTheIndex_NamesThePathAsNotIndexedInJson()
    {
        string dbPath = SymbolsLevelArtifact.Create(Workspace());

        (_, string output) = Cli(dbPath, ["inspect", "src/Aplha.cs", "--json"]);

        JsonElement diagnostic = JsonDocument.Parse(output).RootElement.GetProperty("diagnostic");
        Assert.Equal("file_not_indexed", diagnostic.GetProperty("code").GetString());
        Assert.Equal("expected_empty", diagnostic.GetProperty("class").GetString());
    }

    [Fact]
    public void CliInspect_IndexedSymbolAtOverviewDepth_MarksUsageEvidenceUnavailable()
    {
        string dbPath = SymbolsLevelArtifact.Create(Workspace());

        (_, string output) = Cli(dbPath, ["inspect", "Alpha", "--depth", "overview"]);

        Assert.Contains("usage_evidence=unavailable", output, StringComparison.Ordinal);
        Assert.DoesNotContain("diagnostic_code=", output, StringComparison.Ordinal);
    }

    [Fact]
    public void CliInspect_IndexedFileCarryingNoSymbols_KeepsNoFileSymbols()
    {
        string dbPath = SymbolsLevelArtifact.Create(Workspace());
        AddSymbolFreeIndexedFile(dbPath, "src/Empty.cs");

        (_, string output) = Cli(dbPath, ["inspect", "src/Empty.cs"]);

        Assert.Contains("diagnostic_code=no_file_symbols", output, StringComparison.Ordinal);
        Assert.DoesNotContain("file_not_indexed", output, StringComparison.Ordinal);
    }

    [Fact]
    public void CliInspect_IndexedFileWithSymbols_AttachesNoDiagnostic()
    {
        string dbPath = SymbolsLevelArtifact.Create(Workspace());

        (_, string output) = Cli(dbPath, ["inspect", "src/Alpha.cs"]);

        Assert.Contains("Alpha", output, StringComparison.Ordinal);
        Assert.DoesNotContain("diagnostic_code=", output, StringComparison.Ordinal);
    }

    private (int Code, string Out) Cli(string dbPath, string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var context = new WorkspaceContext(
            WorkspaceRoot: Path.GetDirectoryName(dbPath)!,
            ExtractDbPath: dbPath,
            TelemetryDbPath: Path.Combine(_dir, "telemetry.db"),
            RegistryDbPath: Path.Combine(_dir, "workspaces.db"),
            ToolsRoot: Path.Combine(_dir, ".tools"),
            WorkspaceId: null);

        int code = CliDispatch.Run(args, context, stdout, stderr);
        return (code, stdout.ToString() + stderr.ToString());
    }

    private string Workspace() => Path.Combine(_dir, "symbols-level");

    private static InspectTool ToolOver(string dbPath, string? dbPathOverride = null) =>
        new(new SymbolReadProvider(
            ReadContext(RepositoryIndexLoader.Load(dbPath), dbPath, dbPathOverride)));

    private static WorkspaceSymbolReadContext ReadContext(
        ISymbolLookupIndex index,
        string dbPath,
        string? dbPathOverride = null) =>
        new(
            index,
            dbPathOverride ?? dbPath,
            "ws-missing-file",
            Path.GetDirectoryName(dbPath)!,
            Revision: 1,
            IndexFresh: true,
            FreshnessStatus: "current",
            WarningText: null,
            DisplayId: "missing",
            IsCurrent: true,
            IndexLevel: IndexLevels.SymbolsMetadataValue);

    private static void AddSymbolFreeIndexedFile(string dbPath, string path)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO files
                (file_id, path, language, content_hash, content_bytes, line_count,
                 indexed_at, last_revision_id, status, metadata_json)
            VALUES ($fid, $path, 'csharp', $hash, 0, 0, '1970-01-01T00:00:00Z', 1, 'indexed', NULL);
            """;
        command.Parameters.AddWithValue("$fid", "file:" + path);
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$hash", "blake3:" + ContentHasher.Blake3Hex([]));
        command.ExecuteNonQuery();
    }

    private sealed class SymbolReadProvider : IWorkspaceSymbolReadProvider
    {
        private readonly WorkspaceSymbolReadContext _context;

        public SymbolReadProvider(WorkspaceSymbolReadContext context) => _context = context;

        public WorkspaceSymbolReadContext ResolveSymbolRead(string? workspaceId, bool ensureFresh) => _context;
    }
}
