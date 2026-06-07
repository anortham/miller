using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Server;
using Miller.Server.Tools;
using Xunit;

namespace Miller.Tests.Server;

public sealed class ContentToolTests : IDisposable
{
    private readonly string _dir;
    private readonly WorkspaceContext _workspace;

    public ContentToolTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-content-tool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _workspace = new WorkspaceContext(
            WorkspaceRoot: _dir,
            ExtractDbPath: Path.Combine(_dir, ".miller", "symbols.db"),
            TelemetryDbPath: Path.Combine(_dir, "telemetry.db"),
            RegistryDbPath: Path.Combine(_dir, "workspaces.db"),
            ToolsRoot: Path.Combine(_dir, ".tools"),
            WorkspaceId: "workspace-1");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Content_ImportSearchReadListAndRemove_UsesBoundedExternalFileOutput()
    {
        string logPath = Path.Combine(_dir, "ci.log");
        File.WriteAllText(logPath, """
            build started
            SecretToken42 failed in integration
            build finished
            """);
        var tool = new ContentTool(_workspace, new ContentCorpusExternalStore());

        string importJson = tool.Content("import", path: logPath, format: "json");

        Assert.DoesNotContain("SecretToken42", importJson);
        using JsonDocument importedDoc = JsonDocument.Parse(importJson);
        string sourceId = importedDoc.RootElement.GetProperty("source_id").GetString()!;
        Assert.Equal(TextContentKind.ExternalFile, importedDoc.RootElement.GetProperty("content_kind").GetString());
        Assert.True(importedDoc.RootElement.GetProperty("source_bytes").GetInt64() > 0);

        string search = tool.Content("search", query: "SecretToken42", limit: 5);
        Assert.Contains("ci.log:2  external_file", search);
        Assert.Contains("SecretToken42 failed", search);

        string read = tool.Content("read", source_id: sourceId, line: 2, context_lines: 0);
        Assert.Contains("ci.log:2-2", read);
        Assert.Contains("2: SecretToken42 failed in integration", read);
        Assert.DoesNotContain("build started", read);
        Assert.DoesNotContain("build finished", read);

        string listJson = tool.Content("list", format: "json");
        using JsonDocument listDoc = JsonDocument.Parse(listJson);
        Assert.Equal(sourceId, listDoc.RootElement[0].GetProperty("source_id").GetString());
        Assert.Equal("ci.log", Path.GetFileName(listDoc.RootElement[0].GetProperty("display_path").GetString()));

        string removed = tool.Content("remove", source_id: sourceId);
        Assert.Contains("removed", removed);

        string afterRemove = tool.Content("search", query: "SecretToken42");
        Assert.Equal("No results.", afterRemove.Trim());
    }
}
