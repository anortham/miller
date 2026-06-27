using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Server;
using Miller.Server.Telemetry;
using Miller.Server.Tools;
using Miller.Tests.Indexing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
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

    private static (string? Op, string MetadataJson, string Outcome) ReadTelemetryOpMetadata(string dbPath)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT op, metadata_json, outcome FROM tool_telemetry LIMIT 1;";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read(), "expected one telemetry row");
        return (r.IsDBNull(0) ? null : r.GetString(0), r.GetString(1), r.GetString(2));
    }

    [Fact]
    public async Task Content_McpCallWithNoArguments_DefaultsToListInsteadOfThrowing()
    {
        var ct = TestContext.Current.CancellationToken;

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_workspace);
        services.AddSingleton(new ContentCorpusExternalStore());
        services
            .AddMcpServer(o => { o.ServerInfo = new() { Name = "content-test", Version = "0" }; })
            .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream())
            .WithTools<ContentTool>();

        await using var provider = services.BuildServiceProvider();
        var server = provider.GetRequiredService<McpServer>();
        var serverTask = server.RunAsync(ct);

        var clientTransport = new StreamClientTransport(
            clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream(), NullLoggerFactory.Instance);
        await using var client = await McpClient.CreateAsync(clientTransport, cancellationToken: ct);

        var result = await client.CallToolAsync("content", new Dictionary<string, object?>(), cancellationToken: ct);

        string text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.NotEqual(true, result.IsError);
        Assert.Contains("No imported content", text, StringComparison.OrdinalIgnoreCase);

        await client.DisposeAsync();
        await clientToServer.Writer.CompleteAsync();
        await serverToClient.Writer.CompleteAsync();
        try { await serverTask.WaitAsync(TimeSpan.FromSeconds(5), ct); }
        catch (Exception) { /* server loop teardown is not what this test asserts */ }
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

    [Fact]
    public void Content_Search_RecordsOperationShapeAndEmptyReason_InTelemetry()
    {
        string logPath = Path.Combine(_dir, "ci.log");
        File.WriteAllText(logPath, "Known marker appears here.");
        var tool = new ContentTool(_workspace, new ContentCorpusExternalStore());
        tool.Content("import", path: logPath);

        using (var ledger = TelemetryLedger.Open(_workspace.TelemetryDbPath, _workspace.WorkspaceId, _workspace.WorkspaceRoot))
        {
            using var scope = ledger.Measure("content", op: null);
            string output = tool.Content("search", query: "MissingSecretValue", content_kind: "web", limit: 7);
            Assert.Equal("No results.", output.Trim());
        }

        var row = ReadTelemetryOpMetadata(_workspace.TelemetryDbPath);
        Assert.Equal("search", row.Op);
        Assert.Equal("empty", row.Outcome);
        using JsonDocument doc = JsonDocument.Parse(row.MetadataJson);
        Assert.Equal("web", doc.RootElement.GetProperty("content_kind").GetString());
        Assert.Equal("compact", doc.RootElement.GetProperty("format").GetString());
        Assert.Equal("6-10", doc.RootElement.GetProperty("limit_bucket").GetString());
        Assert.False(doc.RootElement.GetProperty("workspace_all").GetBoolean());
        Assert.Equal("no_content_hits", doc.RootElement.GetProperty("empty_reason").GetString());
        Assert.DoesNotContain("MissingSecretValue", row.MetadataJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Content_AddMarkdownSearchAndRead_WebKind_StaysOutOfDocsWeb()
    {
        string markdownPath = Path.Combine(_dir, "page.md");
        File.WriteAllText(markdownPath, """
            # Example Page

            WebToolMarker appears in markdown.
            """);
        string logPath = Path.Combine(_dir, "ci.log");
        File.WriteAllText(logPath, "WebToolMarker appears in an external log.");
        var tool = new ContentTool(_workspace, new ContentCorpusExternalStore());
        tool.Content("import", path: logPath);

        string importJson = tool.Content(
            "add_markdown",
            path: markdownPath,
            url: "https://example.test/web-tool",
            display_path: "Example Web Tool",
            format: "json");

        Assert.DoesNotContain("WebToolMarker", importJson);
        Assert.False(Directory.Exists(Path.Combine(_dir, "docs", "web")));
        using JsonDocument importedDoc = JsonDocument.Parse(importJson);
        string sourceId = importedDoc.RootElement.GetProperty("source_id").GetString()!;
        Assert.Equal(TextContentKind.Web, importedDoc.RootElement.GetProperty("content_kind").GetString());
        Assert.Equal("https://example.test/web-tool", importedDoc.RootElement.GetProperty("url").GetString());

        string webSearch = tool.Content("search", query: "WebToolMarker", content_kind: TextContentKind.Web);
        Assert.Contains("Example Web Tool:3  web", webSearch);
        Assert.Contains("WebToolMarker appears in markdown", webSearch);
        Assert.DoesNotContain("ci.log", webSearch);

        string read = tool.Content("read", source_id: sourceId, line: 3, context_lines: 0);
        Assert.Contains("Example Web Tool:3-3", read);
        Assert.Contains("3: WebToolMarker appears in markdown.", read);

        string listJson = tool.Content("list", content_kind: TextContentKind.Web, format: "json");
        using JsonDocument listDoc = JsonDocument.Parse(listJson);
        Assert.Equal(sourceId, listDoc.RootElement[0].GetProperty("source_id").GetString());
        Assert.Equal("https://example.test/web-tool", listDoc.RootElement[0].GetProperty("url").GetString());
    }

    [Fact]
    public void Content_Export_ReturnsJsonLinesScopedByKind()
    {
        string logPath = Path.Combine(_dir, "ci.log");
        File.WriteAllText(logPath, "ExternalToolExportMarker appears here.");
        string markdownPath = Path.Combine(_dir, "page.md");
        File.WriteAllText(markdownPath, "WebToolExportMarker appears here.");
        var tool = new ContentTool(_workspace, new ContentCorpusExternalStore());
        tool.Content("import", path: logPath);
        tool.Content(
            "add_markdown",
            path: markdownPath,
            url: "https://example.test/export-tool",
            display_path: "Tool Export Page");

        string jsonl = tool.Content("export", content_kind: TextContentKind.Web);

        string line = Assert.Single(jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        using JsonDocument doc = JsonDocument.Parse(line);
        JsonElement row = doc.RootElement;
        Assert.Equal(1, row.GetProperty("schema_version").GetInt32());
        Assert.Equal(TextContentKind.Web, row.GetProperty("content_kind").GetString());
        Assert.Equal("https://example.test/export-tool", row.GetProperty("url").GetString());
        Assert.Equal("Tool Export Page", row.GetProperty("display_path").GetString());
        Assert.Contains("WebToolExportMarker", row.GetProperty("chunk_text").GetString());
        Assert.DoesNotContain("ExternalToolExportMarker", jsonl);
    }

    [Fact]
    public void Content_SearchAllRegisteredWorkspaces_ReportsWorkspacePerHit()
    {
        string alphaRoot = Path.Combine(_dir, "alpha");
        string betaRoot = Path.Combine(_dir, "beta");
        Directory.CreateDirectory(alphaRoot);
        Directory.CreateDirectory(betaRoot);
        string alphaSymbols = Path.Combine(alphaRoot, ".miller", "symbols.db");
        string betaSymbols = Path.Combine(betaRoot, ".miller", "symbols.db");
        string alphaLog = Path.Combine(alphaRoot, "alpha.log");
        string betaLog = Path.Combine(betaRoot, "beta.log");
        File.WriteAllText(alphaLog, "CrossWorkspaceNeedle in alpha.");
        File.WriteAllText(betaLog, "CrossWorkspaceNeedle in beta.");
        var store = new ContentCorpusExternalStore();
        store.Import(ContentCorpusSidecar.ContentDbPathFor(alphaSymbols), alphaLog, displayPath: "alpha.log");
        store.Import(ContentCorpusSidecar.ContentDbPathFor(betaSymbols), betaLog, displayPath: "beta.log");
        using (var registry = WorkspaceRegistry.Open(_workspace.RegistryDbPath))
        {
            registry.UpsertSeen("ws-alpha", "alpha", alphaRoot, alphaSymbols);
            registry.MarkScanned("ws-alpha", revision: 1);
            registry.UpsertSeen("ws-beta", "beta", betaRoot, betaSymbols);
            registry.MarkScanned("ws-beta", revision: 1);
        }
        var tool = new ContentTool(_workspace, store);

        string compact = tool.Content(
            "search",
            query: "CrossWorkspaceNeedle",
            workspace_id: "all",
            limit: 10);

        Assert.Contains("alpha (ws-alpha)  alpha.log:1  external_file", compact);
        Assert.Contains("beta (ws-beta)  beta.log:1  external_file", compact);
        Assert.Matches(
            @"\nread: content read source_id=external_file:[0-9a-f]+ line=1 workspace_id=ws-(alpha|beta)\b",
            compact);

        string json = tool.Content(
            "search",
            query: "CrossWorkspaceNeedle",
            workspace_id: "all",
            limit: 10,
            format: "json");
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement[] rows = doc.RootElement.EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Contains(rows, row =>
            row.GetProperty("workspace_id").GetString() == "ws-alpha"
            && row.GetProperty("display_id").GetString() == "alpha"
            && row.GetProperty("display_path").GetString() == "alpha.log");
        Assert.Contains(rows, row =>
            row.GetProperty("workspace_id").GetString() == "ws-beta"
            && row.GetProperty("display_id").GetString() == "beta"
            && row.GetProperty("display_path").GetString() == "beta.log");
    }

    [Fact]
    public void Content_ReadUsesWorkspaceIdForExternalSourceIdReturnedByWorkspaceSearch()
    {
        string alphaRoot = Path.Combine(_dir, "external-read-alpha");
        Directory.CreateDirectory(alphaRoot);
        string alphaSymbols = Path.Combine(alphaRoot, ".miller", "symbols.db");
        string alphaLog = Path.Combine(alphaRoot, "alpha.log");
        File.WriteAllText(alphaLog, "CrossWorkspaceExternalReadMarker in alpha.");
        var store = new ContentCorpusExternalStore();
        store.Import(ContentCorpusSidecar.ContentDbPathFor(alphaSymbols), alphaLog, displayPath: "alpha.log");
        using (var registry = WorkspaceRegistry.Open(_workspace.RegistryDbPath))
        {
            registry.UpsertSeen("ws-alpha", "alpha", alphaRoot, alphaSymbols);
            registry.MarkScanned("ws-alpha", revision: 1);
        }
        var tool = new ContentTool(_workspace, store);

        string json = tool.Content(
            "search",
            query: "CrossWorkspaceExternalReadMarker",
            workspace_id: "alpha",
            limit: 10,
            format: "json");
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement hit = Assert.Single(doc.RootElement.EnumerateArray());
        string sourceId = hit.GetProperty("source_id").GetString()!;
        int line = hit.GetProperty("line").GetInt32();
        string workspaceId = hit.GetProperty("workspace_id").GetString()!;

        string read = tool.Content(
            "read",
            source_id: sourceId,
            workspace_id: workspaceId,
            line: line,
            context_lines: 0);

        Assert.StartsWith("external_file:", sourceId, StringComparison.Ordinal);
        Assert.Contains("alpha.log:1-1", read);
        Assert.Contains("1: CrossWorkspaceExternalReadMarker in alpha.", read);
        Assert.DoesNotContain("content failed:", read, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Content_SearchRegisteredWorkspaceSource_FailsWhenContentDbIsStale()
    {
        const string sourceText = """
            public class Api
            {
                public void Handle()
                {
                    throw new InvalidOperationException("StaleWorkspaceSourceMarker");
                }
            }
            """;
        using var fixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [new JulieDbFixture.SymbolRow("sym-api", "Api", "class", "csharp", "src/Api.cs", "public class Api", 1, null)
            {
                EndLine = 7,
            }],
            fileContent: new Dictionary<string, string>
            {
                ["src/Api.cs"] = sourceText,
            },
            revisions:
            [
                new JulieDbFixture.RevisionRow(1),
                new JulieDbFixture.RevisionRow(2),
            ]);
        ContentCorpusWriter.Write(
            ContentCorpusSidecar.ContentDbPathFor(fixture.DbPath),
            fixture.DbPath,
            fixture.WorkspaceRoot,
            workspaceId: "ws-stale",
            revision: 1);
        using (var registry = WorkspaceRegistry.Open(_workspace.RegistryDbPath))
        {
            registry.UpsertSeen("ws-stale", "stale", fixture.WorkspaceRoot, fixture.DbPath);
            registry.MarkScanned("ws-stale", revision: 2);
        }
        var tool = new ContentTool(_workspace, new ContentCorpusExternalStore());

        string output = tool.Content(
            "search",
            query: "StaleWorkspaceSourceMarker",
            content_kind: TextContentKind.WorkspaceSource,
            workspace_id: "all");

        Assert.StartsWith("content failed:", output, StringComparison.Ordinal);
        Assert.Contains("is stale", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("expected 2", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Content_ReadCanOpenWorkspaceSourceIdReturnedByWorkspaceSearch()
    {
        const string sourceText = """
            public class Api
            {
                public void Handle()
                {
                    throw new InvalidOperationException("WorkspaceReadMarker");
                }
            }
            """;
        using var fixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [new JulieDbFixture.SymbolRow("sym-api", "Api", "class", "csharp", "src/Api.cs", "public class Api", 1, null)
            {
                EndLine = 7,
            }],
            fileContent: new Dictionary<string, string>
            {
                ["src/Api.cs"] = sourceText,
            },
            revisions:
            [
                new JulieDbFixture.RevisionRow(1),
            ]);
        ContentCorpusWriter.Write(
            ContentCorpusSidecar.ContentDbPathFor(fixture.DbPath),
            fixture.DbPath,
            fixture.WorkspaceRoot,
            workspaceId: "ws-source",
            revision: 1);
        using (var registry = WorkspaceRegistry.Open(_workspace.RegistryDbPath))
        {
            registry.UpsertSeen("ws-source", "source", fixture.WorkspaceRoot, fixture.DbPath);
            registry.MarkScanned("ws-source", revision: 1);
        }
        var tool = new ContentTool(_workspace, new ContentCorpusExternalStore());

        string json = tool.Content(
            "search",
            query: "WorkspaceReadMarker",
            content_kind: TextContentKind.WorkspaceSource,
            workspace_id: "source",
            format: "json");
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement hit = Assert.Single(doc.RootElement.EnumerateArray());
        string sourceId = hit.GetProperty("source_id").GetString()!;
        int line = hit.GetProperty("line").GetInt32();

        string read = tool.Content("read", source_id: sourceId, line: line, context_lines: 0);

        Assert.Contains("src/Api.cs:", read);
        Assert.Contains($"{line}: ", read);
        Assert.Contains("WorkspaceReadMarker", read);
    }

    [Fact]
    public void Content_SearchCompact_IncludesSourceIdInEachHitAndReadFooter()
    {
        string logPath = Path.Combine(_dir, "ci.log");
        File.WriteAllText(logPath, """
            build started
            SourceIdFooterMarker failed in integration
            build finished
            """);
        var tool = new ContentTool(_workspace, new ContentCorpusExternalStore());
        tool.Content("import", path: logPath);

        string search = tool.Content("search", query: "SourceIdFooterMarker", limit: 5);

        Assert.Contains("source_id=external_file:", search, StringComparison.Ordinal);
        Assert.Contains("ci.log:2  external_file", search, StringComparison.Ordinal);
        Assert.Contains("SourceIdFooterMarker failed", search, StringComparison.Ordinal);
        Assert.Matches(@"\nread: content read source_id=external_file:[0-9a-f]+ line=2\b", search);
    }

    [Fact]
    public void Content_Read_AcceptsUniqueDisplayPathAlias()
    {
        string logPath = Path.Combine(_dir, "build.log");
        File.WriteAllText(logPath, """
            build started
            DisplayPathAliasMarker on line two
            build finished
            """);
        var tool = new ContentTool(_workspace, new ContentCorpusExternalStore());
        tool.Content("import", path: logPath, display_path: "build.log");

        string read = tool.Content("read", source_id: "build.log", line: 2, context_lines: 0);

        Assert.Contains("build.log:2-2", read);
        Assert.Contains("2: DisplayPathAliasMarker on line two", read);
        Assert.DoesNotContain("build started", read);
    }

    [Fact]
    public void Content_Read_RejectsAmbiguousDisplayPathAlias()
    {
        string logA = Path.Combine(_dir, "a.log");
        string logB = Path.Combine(_dir, "b.log");
        File.WriteAllText(logA, "AmbiguousAliasMarker alpha\n");
        File.WriteAllText(logB, "AmbiguousAliasMarker beta\n");
        var tool = new ContentTool(_workspace, new ContentCorpusExternalStore());
        tool.Content("import", path: logA, display_path: "dup.log");
        tool.Content("import", path: logB, display_path: "dup.log");

        string output = tool.Content("read", source_id: "dup.log", line: 1, context_lines: 0);

        Assert.StartsWith("content failed:", output, StringComparison.Ordinal);
        Assert.Contains("matches multiple imported sources", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("external_file:", output, StringComparison.Ordinal);
        Assert.DoesNotContain("AmbiguousAliasMarker", output, StringComparison.OrdinalIgnoreCase);
    }
}
