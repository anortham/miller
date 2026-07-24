using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    private static (string? WorkspaceId, string? WorkspaceRoot, string? TargetHash) ReadTelemetryAttribution(string dbPath)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT workspace_id, workspace_root, target_hash FROM tool_telemetry WHERE tool = 'content' AND op = 'read' LIMIT 1;";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read(), "expected one content-read telemetry row");
        return (
            r.IsDBNull(0) ? null : r.GetString(0),
            r.IsDBNull(1) ? null : r.GetString(1),
            r.IsDBNull(2) ? null : r.GetString(2));
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
        Assert.Contains("ci.log  external_file  source_id=", search);
        Assert.Contains("  :2  ", search);
        Assert.Contains("SecretToken42 failed", search);

        string read = tool.Content("read", source_id: sourceId, line: 2, context_lines: 0);
        Assert.Contains("ci.log:2-2", read);
        Assert.Contains("2: SecretToken42 failed in integration", read);
        Assert.DoesNotContain("build started", read);
        Assert.DoesNotContain("build finished", read);

        string listJson = tool.Content("list", format: "json");
        using JsonDocument listDoc = JsonDocument.Parse(listJson);
        JsonElement listedSource = Assert.Single(
            listDoc.RootElement.GetProperty("kinds")[0].GetProperty("sources").EnumerateArray());
        Assert.Equal(sourceId, listedSource.GetProperty("source_id").GetString());
        Assert.Equal("ci.log", Path.GetFileName(listedSource.GetProperty("display_path").GetString()));

        string removed = tool.Content("remove", source_id: sourceId);
        Assert.Contains("removed", removed);

        string afterRemove = tool.Content("search", query: "SecretToken42");
        Assert.Contains("No results", afterRemove, StringComparison.Ordinal);
        Assert.Contains("content_kind", afterRemove, StringComparison.Ordinal);
    }

    private ContentTool ToolWithImportedSources(params string[] displayPaths)
    {
        var tool = new ContentTool(_workspace, new ContentCorpusExternalStore());
        for (int i = 0; i < displayPaths.Length; i++)
        {
            string file = Path.Combine(_dir, $"import-{i}.txt");
            File.WriteAllText(file, "alpha line one\nbeta line two\ngamma line three\n");
            tool.Content("import", path: file, display_path: displayPaths[i]);
        }

        return tool;
    }

    private string ImportedSourceId(ContentTool tool, string displayPath)
    {
        string listJson = tool.Content("list", format: "json");
        using JsonDocument doc = JsonDocument.Parse(listJson);
        return doc.RootElement.GetProperty("kinds").EnumerateArray()
            .SelectMany(static kind => kind.GetProperty("sources").EnumerateArray())
            .Single(source => source.GetProperty("display_path").GetString() == displayPath)
            .GetProperty("source_id").GetString()!;
    }

    [Fact]
    public void Content_Read_ExactSourceId_StillResolves()
    {
        ContentTool tool = ToolWithImportedSources("docs/plans/alpha.md");
        string sourceId = ImportedSourceId(tool, "docs/plans/alpha.md");

        string read = tool.Content("read", source_id: sourceId, line: 2, context_lines: 0);

        Assert.Contains("2: beta line two", read, StringComparison.Ordinal);
    }

    [Fact]
    public void Content_Read_UniqueExactDisplayPath_StillResolves()
    {
        ContentTool tool = ToolWithImportedSources("docs/plans/alpha.md", "docs/guides/setup.md");

        string read = tool.Content("read", source_id: "docs/plans/alpha.md", line: 2, context_lines: 0);

        Assert.Contains("2: beta line two", read, StringComparison.Ordinal);
    }

    [Fact]
    public void Content_Read_ExactDisplayPath_StillResolvesCaseInsensitively()
    {
        ContentTool tool = ToolWithImportedSources("docs/plans/alpha.md");

        string read = tool.Content("read", source_id: "DOCS/PLANS/ALPHA.MD", line: 2, context_lines: 0);

        Assert.Contains("2: beta line two", read, StringComparison.Ordinal);
    }

    [Fact]
    public void Content_Read_AmbiguousExactDisplayPath_StillListsCandidates()
    {
        ContentTool tool = ToolWithImportedSources(
            "shared/notes.md", "shared/notes.md", "shared/notes.md",
            "shared/notes.md", "shared/notes.md", "shared/notes.md");

        string read = tool.Content("read", source_id: "shared/notes.md", line: 1);

        Assert.Contains("matches multiple imported sources by display_path", read, StringComparison.Ordinal);
        Assert.Contains("diagnostic_code=ambiguous_source", read, StringComparison.Ordinal);
        Assert.Equal(5, CountOccurrences(read, "external_file:"));
    }

    [Fact]
    public void Content_Read_UniquePathSuffix_Resolves()
    {
        ContentTool tool = ToolWithImportedSources("docs/plans/alpha.md", "docs/guides/setup.md");

        string read = tool.Content("read", source_id: "plans/alpha.md", line: 2, context_lines: 0);

        Assert.Contains("2: beta line two", read, StringComparison.Ordinal);
    }

    [Fact]
    public void Content_Read_UniqueBasenameSuffix_Resolves()
    {
        ContentTool tool = ToolWithImportedSources("docs/plans/alpha.md", "docs/guides/setup.md");

        string read = tool.Content("read", source_id: "alpha.md", line: 2, context_lines: 0);

        Assert.Contains("2: beta line two", read, StringComparison.Ordinal);
    }

    [Fact]
    public void Content_Read_PartialSegmentSuffix_DoesNotResolve()
    {
        ContentTool tool = ToolWithImportedSources("docs/plans/alpha.md");

        string read = tool.Content("read", source_id: "ans/alpha.md", line: 2);

        Assert.Contains("Content source 'ans/alpha.md' was not found.", read, StringComparison.Ordinal);
        Assert.Contains("diagnostic_code=source_not_found", read, StringComparison.Ordinal);
    }

    [Fact]
    public void Content_Read_AmbiguousSuffix_ListsCandidatesCappedAtFive()
    {
        ContentTool tool = ToolWithImportedSources(
            "a1/x.md", "a2/x.md", "a3/x.md", "a4/x.md", "a5/x.md", "a6/x.md");

        string read = tool.Content("read", source_id: "x.md", line: 1);

        Assert.Contains("matches multiple imported sources by display_path", read, StringComparison.Ordinal);
        Assert.Equal(5, CountOccurrences(read, "external_file:"));
    }

    [Fact]
    public void Content_Read_FullMiss_AppendsNearestDisplayPathSuggestions()
    {
        ContentTool tool = ToolWithImportedSources(
            "docs/plans/alpha.md", "docs/plans/beta.md", "docs/guides/setup.md");

        string read = tool.Content("read", source_id: "plans/missing.md", line: 1);

        Assert.Contains("Content source 'plans/missing.md' was not found.", read, StringComparison.Ordinal);
        Assert.Contains("docs/plans/alpha.md", read, StringComparison.Ordinal);
        Assert.Contains("docs/plans/beta.md", read, StringComparison.Ordinal);
        Assert.DoesNotContain("docs/guides/setup.md", read, StringComparison.Ordinal);
    }

    [Fact]
    public void Content_Read_FullMiss_CapsSuggestionsAtThreeInDeterministicOrder()
    {
        ContentTool tool = ToolWithImportedSources(
            "docs/plans/e.md", "docs/plans/d.md", "docs/plans/c.md", "docs/plans/b.md", "docs/plans/a.md");

        string read = tool.Content("read", source_id: "plans/missing.md", line: 1);

        Assert.Equal(3, CountOccurrences(read, "docs/plans/"));
        Assert.Contains("docs/plans/a.md, docs/plans/b.md, docs/plans/c.md", read, StringComparison.Ordinal);
    }

    [Fact]
    public void Content_Read_FullMissWithNothingSimilar_StaysAPlainNotFound()
    {
        ContentTool tool = ToolWithImportedSources("docs/plans/alpha.md");

        string read = tool.Content("read", source_id: "zzzzz", line: 1);

        Assert.Contains("Content source 'zzzzz' was not found.", read, StringComparison.Ordinal);
        Assert.DoesNotContain("Nearest imported paths", read, StringComparison.Ordinal);
    }

    [Fact]
    public void Content_Remove_StaysStrict_AndRefusesDisplayPathAndSuffixAliases()
    {
        ContentTool tool = ToolWithImportedSources("docs/plans/alpha.md");

        string byDisplayPath = tool.Content("remove", source_id: "docs/plans/alpha.md");
        string bySuffix = tool.Content("remove", source_id: "alpha.md");

        Assert.Contains("not found: docs/plans/alpha.md", byDisplayPath, StringComparison.Ordinal);
        Assert.Contains("not found: alpha.md", bySuffix, StringComparison.Ordinal);

        string stillReadable = tool.Content("read", source_id: "docs/plans/alpha.md", line: 2, context_lines: 0);
        Assert.Contains("2: beta line two", stillReadable, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal);
             i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private ContentTool ToolWithNumberedSource(int lineCount, string displayPath)
    {
        var tool = new ContentTool(_workspace, new ContentCorpusExternalStore());
        string file = Path.Combine(_dir, "numbered.txt");
        File.WriteAllText(file, string.Join('\n', Enumerable.Range(1, lineCount).Select(i => $"line {i}")));
        tool.Content("import", path: file, display_path: displayPath);
        return tool;
    }

    private int SourceLineCount(ContentTool tool, string displayPath)
    {
        using JsonDocument doc = JsonDocument.Parse(tool.Content("list", format: "json"));
        return doc.RootElement.GetProperty("kinds").EnumerateArray()
            .SelectMany(static kind => kind.GetProperty("sources").EnumerateArray())
            .Single(source => source.GetProperty("display_path").GetString() == displayPath)
            .GetProperty("line_count").GetInt32();
    }

    private static (int Start, int End) RenderedRange(string compact)
    {
        string header = compact.Split('\n').First(l => l.Contains(":", StringComparison.Ordinal) && l.Contains('-'));
        string range = header[(header.LastIndexOf(':') + 1)..];
        string[] parts = range.Split('-');
        return (int.Parse(parts[0]), int.Parse(parts[1]));
    }

    private ContentTool ToolWithNeedleSource(string displayPath, params int[] needleLines)
    {
        var tool = new ContentTool(_workspace, new ContentCorpusExternalStore());
        string file = Path.Combine(_dir, displayPath.Replace('/', '-'));
        string[] lines = Enumerable.Range(1, 400)
            .Select(i => needleLines.Contains(i) ? $"GroupNeedle marker at line {i}" : $"filler line {i}")
            .ToArray();
        File.WriteAllText(file, string.Join('\n', lines));
        tool.Content("import", path: file, display_path: displayPath);
        return tool;
    }

    private static string SearchBody(string compact)
    {
        int handoff = compact.IndexOf("\n\nread: ", StringComparison.Ordinal);
        return handoff < 0 ? compact : compact[..handoff];
    }

    [Fact]
    public void Content_SearchMultipleHitsInOneSource_RendersSourceIdExactlyOnce()
    {
        ContentTool tool = ToolWithNeedleSource("logs/app.log", 5, 350);
        string sourceId = ImportedSourceId(tool, "logs/app.log");

        string compact = tool.Content("search", query: "GroupNeedle", limit: 10);
        string body = SearchBody(compact);

        Assert.Equal(1, CountOccurrences(body, sourceId));
        Assert.Equal(1, CountOccurrences(body, "logs/app.log  external_file  source_id="));
        Assert.Contains("  :5  ", body, StringComparison.Ordinal);
        Assert.Contains("  :350  ", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Content_SearchMultipleHitsInOneSource_KeepsTrailingReadHandoff()
    {
        ContentTool tool = ToolWithNeedleSource("logs/app.log", 5, 350);
        string sourceId = ImportedSourceId(tool, "logs/app.log");

        string compact = tool.Content("search", query: "GroupNeedle", limit: 10);

        Assert.Matches($@"\n\nread: content read source_id={Regex.Escape(sourceId)} line=(5|350)$", compact);
    }

    [Fact]
    public void Content_SearchAcrossTwoSources_RendersOneHeaderPerSource()
    {
        var tool = new ContentTool(_workspace, new ContentCorpusExternalStore());
        foreach (string name in new[] { "one", "two" })
        {
            string file = Path.Combine(_dir, $"{name}.log");
            File.WriteAllText(file, "GroupNeedle marker here.\nfiller.");
            tool.Content("import", path: file, display_path: $"logs/{name}.log");
        }

        string body = SearchBody(tool.Content("search", query: "GroupNeedle", limit: 10));

        Assert.Equal(1, CountOccurrences(body, "logs/one.log  external_file  source_id="));
        Assert.Equal(1, CountOccurrences(body, "logs/two.log  external_file  source_id="));
    }

    [Fact]
    public void Content_SearchGroupedCompact_LeavesSearchJsonUnchanged()
    {
        ContentTool tool = ToolWithNeedleSource("logs/app.log", 5, 350);
        string sourceId = ImportedSourceId(tool, "logs/app.log");

        string json = tool.Content("search", query: "GroupNeedle", limit: 10, format: "json");

        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        JsonElement[] results = doc.RootElement.EnumerateArray().ToArray();
        Assert.Equal(2, results.Length);
        foreach (JsonElement result in results)
        {
            Assert.Equal(sourceId, result.GetProperty("source_id").GetString());
            Assert.Equal("external_file", result.GetProperty("content_kind").GetString());
            Assert.Equal("logs/app.log", result.GetProperty("display_path").GetString());
        }

        Assert.Equal(new[] { 5, 350 }, results.Select(r => r.GetProperty("line").GetInt32()).OrderBy(l => l).ToArray());
        Assert.Equal(
            new[]
            {
                "source_id", "chunk_id", "content_kind", "display_path", "url",
                "line", "line_start", "line_end", "score", "snippet", "source_bytes",
            },
            results[0].EnumerateObject().Select(static p => p.Name).ToArray());
    }

    [Fact]
    public void Content_Read_NumberedFixture_HasExpectedLineCount()
    {
        ContentTool tool = ToolWithNumberedSource(500, "big/log.txt");

        Assert.Equal(500, SourceLineCount(tool, "big/log.txt"));
    }

    [Fact]
    public void Content_Read_OversizedWindowMidFile_ClampsToTwoHundredWithContinuationNote()
    {
        ContentTool tool = ToolWithNumberedSource(500, "big/log.txt");

        string read = tool.Content("read", source_id: "big/log.txt", line: 250, context_lines: 150);

        Assert.StartsWith(
            "window clamped to 200 lines (requested 301) — continue with line=450 context_lines=150",
            read,
            StringComparison.Ordinal);
        Assert.Equal((100, 299), RenderedRange(read));
        Assert.Contains("250: line 250", read, StringComparison.Ordinal);
    }

    [Fact]
    public void Content_Read_OversizedWindowClippedAtStartOfFile_ClampsFromLineOne()
    {
        ContentTool tool = ToolWithNumberedSource(500, "big/log.txt");

        string read = tool.Content("read", source_id: "big/log.txt", line: 50, context_lines: 300);

        Assert.StartsWith("window clamped to 200 lines (requested 601)", read, StringComparison.Ordinal);
        Assert.Equal((1, 200), RenderedRange(read));
        Assert.Contains("50: line 50", read, StringComparison.Ordinal);
    }

    [Fact]
    public void Content_Read_OversizedWindowClippedAtEndOfFile_KeepsRequestedCenter()
    {
        ContentTool tool = ToolWithNumberedSource(500, "big/log.txt");

        string read = tool.Content("read", source_id: "big/log.txt", line: 495, context_lines: 300);

        Assert.StartsWith("window clamped to 200 lines (requested 601)", read, StringComparison.Ordinal);
        Assert.Contains("495: line 495", read, StringComparison.Ordinal);
        (int start, int end) = RenderedRange(read);
        Assert.Equal(200, end - start + 1);
        Assert.InRange(495, start, end);
    }

    [Fact]
    public void Content_Read_WindowClippedByEndOfFileWithinBudget_RendersNoClampNote()
    {
        ContentTool tool = ToolWithNumberedSource(500, "big/log.txt");

        string read = tool.Content("read", source_id: "big/log.txt", line: 495, context_lines: 150);

        Assert.DoesNotContain("window clamped", read, StringComparison.Ordinal);
        Assert.Equal((345, 500), RenderedRange(read));
    }

    [Fact]
    public void Content_Read_OneLineSource_RendersThatLineWithoutClampNote()
    {
        ContentTool tool = ToolWithNumberedSource(1, "tiny/one.txt");

        string read = tool.Content("read", source_id: "tiny/one.txt", line: 1, context_lines: 10);

        Assert.DoesNotContain("window clamped", read, StringComparison.Ordinal);
        Assert.Equal((1, 1), RenderedRange(read));
        Assert.Contains("1: line 1", read, StringComparison.Ordinal);
    }

    [Fact]
    public void Content_Read_ContextLinesBeyondSourceLength_RendersWholeSourceWithoutClampNote()
    {
        ContentTool tool = ToolWithNumberedSource(5, "tiny/five.txt");

        string read = tool.Content("read", source_id: "tiny/five.txt", line: 3, context_lines: 1000);

        Assert.DoesNotContain("window clamped", read, StringComparison.Ordinal);
        Assert.Equal((1, 5), RenderedRange(read));
    }

    [Fact]
    public void Content_Read_ContextLinesLargerThanTheClamp_StillRendersRequestedCenter()
    {
        ContentTool tool = ToolWithNumberedSource(2000, "big/huge.txt");

        string read = tool.Content("read", source_id: "big/huge.txt", line: 1000, context_lines: 500);

        Assert.Contains("1000: line 1000", read, StringComparison.Ordinal);
        (int start, int end) = RenderedRange(read);
        Assert.Equal(200, end - start + 1);
        Assert.InRange(1000, start, end);
    }

    private static int ContinuationLine(string compact)
    {
        string note = compact.Split('\n')[0];
        int at = note.IndexOf("line=", StringComparison.Ordinal) + "line=".Length;
        return int.Parse(note[at..note.IndexOf(' ', at)]);
    }

    [Theory]
    [InlineData(150)]
    [InlineData(500)]
    public void Content_Read_ClampContinuationChain_ResumesExactlyAfterTheLastRenderedLine(int contextLines)
    {
        ContentTool tool = ToolWithNumberedSource(2000, "big/huge.txt");

        string page = tool.Content("read", source_id: "big/huge.txt", line: 1000, context_lines: contextLines);
        (int _, int firstEnd) = RenderedRange(page);

        string nextPage = tool.Content(
            "read",
            source_id: "big/huge.txt",
            line: ContinuationLine(page),
            context_lines: contextLines);
        (int nextStart, int _) = RenderedRange(nextPage);

        Assert.Equal(firstEnd + 1, nextStart);
    }

    [Theory]
    [InlineData(250)]
    [InlineData(300)]
    [InlineData(900)]
    public void Content_Read_ClampContinuationChain_TerminatesAndCoversEveryLineWithoutGaps(int contextLines)
    {
        ContentTool tool = ToolWithNumberedSource(700, "big/log.txt");

        var covered = new SortedSet<int>();
        string page = tool.Content("read", source_id: "big/log.txt", line: 1, context_lines: contextLines);
        for (int hop = 0; hop < 20; hop++)
        {
            (int start, int end) = RenderedRange(page);
            covered.UnionWith(Enumerable.Range(start, end - start + 1));
            if (!page.StartsWith("window clamped", StringComparison.Ordinal))
                break;
            page = tool.Content(
                "read",
                source_id: "big/log.txt",
                line: ContinuationLine(page),
                context_lines: contextLines);
        }

        Assert.Equal(Enumerable.Range(1, 700).ToArray(), covered.ToArray());
        Assert.DoesNotContain("window clamped", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Content_Read_ClampedWindow_RendersIdenticalLinesInCompactAndJson()
    {
        ContentTool tool = ToolWithNumberedSource(500, "big/log.txt");

        string compact = tool.Content("read", source_id: "big/log.txt", line: 250, context_lines: 150);
        string json = tool.Content("read", source_id: "big/log.txt", line: 250, context_lines: 150, format: "json");

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        (int start, int end) = RenderedRange(compact);
        Assert.Equal(start, root.GetProperty("line_start").GetInt32());
        Assert.Equal(end, root.GetProperty("line_end").GetInt32());

        int[] jsonLines = root.GetProperty("lines").EnumerateArray()
            .Select(l => l.GetProperty("line").GetInt32()).ToArray();
        Assert.Equal(Enumerable.Range(start, end - start + 1).ToArray(), jsonLines);
    }

    [Fact]
    public void Content_Read_ClampNote_StaysOutOfJson()
    {
        ContentTool tool = ToolWithNumberedSource(500, "big/log.txt");

        string json = tool.Content("read", source_id: "big/log.txt", line: 250, context_lines: 150, format: "json");

        Assert.DoesNotContain("window clamped", json, StringComparison.Ordinal);
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal(
            new[] { "source_id", "display_path", "line_start", "line_end", "truncated_line_count", "lines" },
            doc.RootElement.EnumerateObject().Select(static p => p.Name).ToArray());
    }

    [Fact]
    public void Content_Read_DefaultCapHugeLine_BoundsCompactAndReportsTruncation()
    {
        string logPath = Path.Combine(_dir, "huge-single-line.log");
        File.WriteAllText(logPath, new string('x', 200_000));
        var tool = new ContentTool(_workspace, new ContentCorpusExternalStore());
        tool.Content("import", path: logPath, display_path: "huge-single-line.log");

        string output = tool.Content(
            "read",
            source_id: "huge-single-line.log",
            line: 1,
            context_lines: 0);

        Assert.DoesNotContain("content read failed:", output, StringComparison.Ordinal);
        Assert.InRange(output.Length, 1, 48_000);
        Assert.Contains("read truncated_lines=1", output, StringComparison.Ordinal);
        Assert.EndsWith("…", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Content_Read_DefaultCapEscapeHeavyLine_BoundsJsonAndReportsTruncation()
    {
        string logPath = Path.Combine(_dir, "escape-heavy-single-line.log");
        File.WriteAllText(logPath, string.Concat(Enumerable.Repeat("\u0001\"\\", 60_000)));
        var tool = new ContentTool(_workspace, new ContentCorpusExternalStore());
        tool.Content("import", path: logPath, display_path: "escape-heavy-single-line.log");

        string output = tool.Content(
            "read",
            source_id: "escape-heavy-single-line.log",
            line: 1,
            context_lines: 0,
            format: "json");

        Assert.InRange(output.Length, 1, 48_000);
        using JsonDocument doc = JsonDocument.Parse(output);
        Assert.Equal(1, doc.RootElement.GetProperty("truncated_line_count").GetInt32());
        JsonElement line = Assert.Single(doc.RootElement.GetProperty("lines").EnumerateArray());
        Assert.True(line.GetProperty("truncated").GetBoolean());
        Assert.EndsWith("…", line.GetProperty("text").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Content_Read_JsonTruncationReportsSameLengthReplacement()
    {
        string logPath = Path.Combine(_dir, "same-length-truncation.log");
        File.WriteAllText(logPath, new string('a', 156) + "\u0001");
        var tool = new ContentTool(_workspace, new ContentCorpusExternalStore());
        tool.Content("import", path: logPath, display_path: "same-length-truncation.log");

        string output = tool.Content(
            "read",
            source_id: "same-length-truncation.log",
            line: 1,
            context_lines: 0,
            format: "json");

        using JsonDocument doc = JsonDocument.Parse(output);
        Assert.Equal(1, doc.RootElement.GetProperty("truncated_line_count").GetInt32());
        JsonElement line = Assert.Single(doc.RootElement.GetProperty("lines").EnumerateArray());
        Assert.True(line.GetProperty("truncated").GetBoolean());
        Assert.EndsWith("…", line.GetProperty("text").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Content_UnknownLongOperation_JsonDiagnosticStaysWithinHardBudget()
    {
        string output = new ContentTool(_workspace, new ContentCorpusExternalStore()).Content(
            new string('x', 50_000),
            format: "json");

        Assert.InRange(output.Length, 1, 8_000);
        using JsonDocument doc = JsonDocument.Parse(output);
        Assert.Equal("content_error", doc.RootElement.GetProperty("diagnostic_code").GetString());
        Assert.EndsWith("…", doc.RootElement.GetProperty("operation").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("source", "workspace_source")]
    [InlineData("workspace_source", "workspace_source")]
    [InlineData("SOURCE", "workspace_source")]
    [InlineData("docs", "workspace_docs")]
    [InlineData("doc", "workspace_docs")]
    [InlineData("workspace_docs", "workspace_docs")]
    [InlineData("DOCS", "workspace_docs")]
    [InlineData("config", "workspace_config")]
    [InlineData("workspace_config", "workspace_config")]
    [InlineData("external", "external_file")]
    [InlineData("external_file", "external_file")]
    [InlineData("file", "external_file")]
    [InlineData("web", "web")]
    [InlineData("WEB", "web")]
    public void Content_SearchKindAliases_ResolveToCanonicalKinds(string alias, string canonical)
    {
        var tool = new ContentTool(_workspace, new ContentCorpusExternalStore());

        string output = tool.Content("search", query: "MissingSecretValue", content_kind: alias, limit: 1);

        Assert.Contains(canonical, output, StringComparison.Ordinal);
    }

    [Fact]
    public void Content_SearchKindAll_DefaultsToExternalFileForSearch()
    {
        var tool = new ContentTool(_workspace, new ContentCorpusExternalStore());

        string output = tool.Content("search", query: "MissingSecretValue", content_kind: "all", limit: 1);

        Assert.Contains("external_file", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Content_SearchUnknownKind_ErrorListsCanonicalValuesAndAliases()
    {
        var tool = new ContentTool(_workspace, new ContentCorpusExternalStore());

        string output = tool.Content("search", query: "anything", content_kind: "markdown", limit: 1);

        Assert.Contains(
            "content_kind must be all, workspace_source (alias source), workspace_docs (aliases docs, doc), " +
            "workspace_config (alias config), external_file (aliases external, file), or web.",
            output,
            StringComparison.Ordinal);
        Assert.Contains("diagnostic_code=search_error", output, StringComparison.Ordinal);
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
            Assert.Contains("No results", output, StringComparison.Ordinal);
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
        Assert.Equal("identifier_like", doc.RootElement.GetProperty("query_shape").GetString());
        Assert.Equal("true_no_hit", doc.RootElement.GetProperty("empty_diagnosis").GetString());
        Assert.DoesNotContain("MissingSecretValue", row.MetadataJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Content_SearchDocsSourceLikeQuery_RecordsModeMismatchDiagnosis()
    {
        var tool = new ContentTool(_workspace, new ContentCorpusExternalStore());

        using (var ledger = TelemetryLedger.Open(_workspace.TelemetryDbPath, _workspace.WorkspaceId, _workspace.WorkspaceRoot))
        {
            using var scope = ledger.Measure("content", op: null);
            string output = tool.Content("search", query: "if (value == null)", content_kind: "docs", limit: 3);
            Assert.Contains("No results", output, StringComparison.Ordinal);
        }

        var row = ReadTelemetryOpMetadata(_workspace.TelemetryDbPath);
        Assert.Equal("search", row.Op);
        Assert.Equal("empty", row.Outcome);
        using JsonDocument doc = JsonDocument.Parse(row.MetadataJson);
        Assert.Equal("workspace_docs", doc.RootElement.GetProperty("content_kind").GetString());
        Assert.Equal("no_content_hits", doc.RootElement.GetProperty("empty_reason").GetString());
        Assert.Equal("source_like", doc.RootElement.GetProperty("query_shape").GetString());
        Assert.Equal("mode_mismatch", doc.RootElement.GetProperty("empty_diagnosis").GetString());
        Assert.DoesNotContain("if (value == null)", row.MetadataJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Content_SearchNoResults_CompactStatesTheDiagnosisInsteadOfTheGenericTriedLine()
    {
        var tool = new ContentTool(_workspace, new ContentCorpusExternalStore());

        string output = tool.Content("search", query: "MissingSecretValue", content_kind: "docs", limit: 3);

        Assert.Contains("No results for content search.", output, StringComparison.Ordinal);
        Assert.Contains("No lexical match for 'MissingSecretValue' in workspace_docs.", output, StringComparison.Ordinal);

        Assert.DoesNotContain("Tried content_kind=", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Try content_kind=docs, source, external_file", output, StringComparison.Ordinal);
        Assert.DoesNotContain("use workspace_id=all only for registered workspace audits", output, StringComparison.Ordinal);

        Assert.Single(NextActionLines(output));
    }

    [Fact]
    public void Content_SearchNoResults_CompactSourceLikeQueryAgainstDocs_NamesSourceMode()
    {
        var tool = new ContentTool(_workspace, new ContentCorpusExternalStore());

        string output = tool.Content("search", query: "if (value == null)", content_kind: "docs", limit: 3);

        Assert.Contains("looks like source code", output, StringComparison.Ordinal);
        Assert.Contains("search mode=source", output, StringComparison.Ordinal);
        string action = Assert.Single(NextActionLines(output));
        Assert.Contains("search query=if (value == null) mode=source", action, StringComparison.Ordinal);
    }

    [Fact]
    public void Content_SearchNoResults_CompactDocsLikeQueryAgainstSource_NamesContentMode()
    {
        var tool = new ContentTool(_workspace, new ContentCorpusExternalStore());

        string output = tool.Content("search", query: "installation guide", content_kind: "source", limit: 3);

        Assert.Contains("reads like prose", output, StringComparison.Ordinal);
        Assert.Contains("search mode=content", output, StringComparison.Ordinal);
        string action = Assert.Single(NextActionLines(output));
        Assert.Contains("search query=installation guide mode=content", action, StringComparison.Ordinal);
    }

    [Fact]
    public void Content_SearchNoResults_CompactShortQuery_ShowsOneCorrectedExample()
    {
        var tool = new ContentTool(_workspace, new ContentCorpusExternalStore());

        string output = tool.Content("search", query: "ab", content_kind: "docs", limit: 3);

        Assert.Contains("too short", output, StringComparison.Ordinal);
        Assert.Contains("e.g. query=\"connection refused\"", output, StringComparison.Ordinal);
        Assert.Single(NextActionLines(output));
    }

    [Fact]
    public void Content_SearchNoResults_CompactPathLikeQuery_RedirectsToFileMode()
    {
        var tool = new ContentTool(_workspace, new ContentCorpusExternalStore());

        string output = tool.Content("search", query: "src/Miller.Server/Tools/ContentTool.cs", content_kind: "docs", limit: 3);

        Assert.Contains("looks like a path", output, StringComparison.Ordinal);
        Assert.Contains("search mode=file", output, StringComparison.Ordinal);
        string action = Assert.Single(NextActionLines(output));
        Assert.Contains("mode=file", action, StringComparison.Ordinal);
    }

    [Fact]
    public void Content_SearchNoResults_CompactNaturalLanguageTrueNoHit_SaysRetryWithLiteralWords()
    {
        var tool = new ContentTool(_workspace, new ContentCorpusExternalStore());

        string output = tool.Content("search", query: "retry budget exceeded", content_kind: "docs", limit: 3);

        Assert.Contains("No lexical match for 'retry budget exceeded' in workspace_docs.", output, StringComparison.Ordinal);
        Assert.Contains("words that appear literally in the docs text", output, StringComparison.Ordinal);
        string action = Assert.Single(NextActionLines(output));
        Assert.Contains("mode=all-text", action, StringComparison.Ordinal);
    }

    [Fact]
    public void Content_SearchNoResults_CompactImportedKindTrueNoHit_OffersListThenWiden()
    {
        var tool = new ContentTool(_workspace, new ContentCorpusExternalStore());

        string output = tool.Content("search", query: "MissingSecretValue", content_kind: "web", limit: 3);

        Assert.Contains("No lexical match for 'MissingSecretValue' in web.", output, StringComparison.Ordinal);
        string[] actions = NextActionLines(output);
        Assert.Equal(2, actions.Length);
        Assert.Contains("content list content_kind=web", actions[0], StringComparison.Ordinal);
        Assert.Contains("mode=all-text", actions[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Content_SearchNoResults_CompactNeverSuggestsContentKindAllText_WhichContentSearchRejects()
    {
        var tool = new ContentTool(_workspace, new ContentCorpusExternalStore());

        foreach (string kind in new[] { "docs", "source", "config", "external", "web" })
        {
            string output = tool.Content("search", query: "retry budget exceeded", content_kind: kind, limit: 3);
            Assert.DoesNotContain("content_kind=all-text", output, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("if (value == null)", "docs")]
    [InlineData("if (value == null)", "config")]
    [InlineData("installation guide", "source")]
    [InlineData("ab", "docs")]
    [InlineData("src/Miller.Server/Tools/ContentTool.cs", "docs")]
    [InlineData("retry budget exceeded", "docs")]
    [InlineData("MissingSecretValue", "web")]
    [InlineData("MissingSecretValue", "external")]
    public void Content_SearchNoResults_CompactStaysWithinLineAndCharBudget(string query, string contentKind)
    {
        var tool = new ContentTool(_workspace, new ContentCorpusExternalStore());

        string output = tool.Content("search", query: query, content_kind: contentKind, limit: 3);

        Assert.InRange(output.Split('\n').Length, 1, 6);
        Assert.InRange(output.Length, 1, 400);
        Assert.InRange(NextActionLines(output).Length, 1, 2);
    }

    private static string[] NextActionLines(string output)
    {
        int start = output.IndexOf("Next:", StringComparison.Ordinal);
        if (start < 0)
            return [];
        return output[start..]
            .Split('\n')
            .Skip(1)
            .Where(static line => line.StartsWith("  ", StringComparison.Ordinal))
            .ToArray();
    }

    private const string ContentKindRejection = "content_kind must be";

    private static string ReplayContentAction(ContentTool tool, JsonElement action)
    {
        JsonElement args = action.GetProperty("args");
        string? Arg(string key) => args.TryGetProperty(key, out JsonElement value) ? value.GetString() : null;

        return tool.Content(
            Arg("operation"),
            query: Arg("query"),
            content_kind: Arg("content_kind"),
            workspace_id: Arg("workspace_id"),
            format: "json");
    }

    private static void AssertNextActionsAreAccepted(ContentTool tool, string output)
    {
        JsonElement[] actions = JsonDocument.Parse(output).RootElement
            .GetProperty("next_actions").EnumerateArray().ToArray();
        Assert.NotEmpty(actions);

        foreach (JsonElement action in actions)
        {
            string tool_ = action.GetProperty("tool").GetString()!;
            JsonElement args = action.GetProperty("args");

            if (string.Equals(tool_, "content", StringComparison.Ordinal))
            {
                string replay = ReplayContentAction(tool, action);
                Assert.DoesNotContain(ContentKindRejection, replay, StringComparison.Ordinal);
                continue;
            }

            if (string.Equals(tool_, "search", StringComparison.Ordinal))
            {
                string mode = args.GetProperty("mode").GetString()!;
                Assert.NotEqual(SearchToolMode.Auto, SearchTool.ParseMode(mode));
            }
        }
    }

    [Fact]
    public void Content_SearchNoResults_JsonNextActionsAreAcceptedByTheirOwnParsers()
    {
        var tool = new ContentTool(_workspace, new ContentCorpusExternalStore());

        string output = tool.Content("search", query: "MissingSecretValue", content_kind: "docs", format: "json", limit: 3);

        AssertNextActionsAreAccepted(tool, output);
    }

    [Fact]
    public void Content_ErrorRecovery_JsonNextActionsAreAcceptedByTheirOwnParsers()
    {
        var tool = new ContentTool(_workspace, new ContentCorpusExternalStore());

        string output = tool.Content("read", source_id: "external_file:deadbeef", line: 1, format: "json");

        AssertNextActionsAreAccepted(tool, output);
    }

    [Fact]
    public void Content_SearchNoResults_JsonIncludesRecoveryGuidance()
    {
        var tool = new ContentTool(_workspace, new ContentCorpusExternalStore());

        string output = tool.Content("search", query: "MissingSecretValue", content_kind: "docs", format: "json", limit: 3);

        using JsonDocument doc = JsonDocument.Parse(output);
        JsonElement root = doc.RootElement;
        Assert.Equal("search", root.GetProperty("operation").GetString());
        Assert.Equal("no_results", root.GetProperty("diagnostic_code").GetString());
        JsonElement results = root.GetProperty("results");
        Assert.Equal(JsonValueKind.Array, results.ValueKind);
        Assert.Empty(results.EnumerateArray());
        JsonElement[] actions = root.GetProperty("next_actions").EnumerateArray().ToArray();
        Assert.True(actions.Length >= 3);
        Assert.Contains(actions, action =>
            action.GetProperty("tool").GetString() == "search"
            && action.GetProperty("args").GetProperty("mode").GetString() == "all-text");
        Assert.Contains(actions, action =>
            action.GetProperty("tool").GetString() == "search"
            && action.GetProperty("args").GetProperty("mode").GetString() == "source");
    }

    [Fact]
    public void Content_SearchNoResults_JsonTopLevelShapeIsFrozen()
    {
        var tool = new ContentTool(_workspace, new ContentCorpusExternalStore());

        string output = tool.Content("search", query: "retry budget exceeded", content_kind: "docs", format: "json", limit: 3);

        using JsonDocument doc = JsonDocument.Parse(output);
        JsonElement root = doc.RootElement;

        Assert.Equal(
            new[] { "operation", "error", "diagnostic_code", "content_kind", "results", "next_actions" },
            root.EnumerateObject().Select(static property => property.Name).ToArray());
        Assert.Equal("search", root.GetProperty("operation").GetString());
        Assert.Equal("No results.", root.GetProperty("error").GetString());
        Assert.Equal("no_results", root.GetProperty("diagnostic_code").GetString());
        Assert.Equal("workspace_docs", root.GetProperty("content_kind").GetString());
        Assert.Empty(root.GetProperty("results").EnumerateArray());

        JsonElement[] actions = root.GetProperty("next_actions").EnumerateArray().ToArray();
        Assert.Equal(3, actions.Length);
        Assert.Equal(
            new[] { "search", "content", "search" },
            actions.Select(static action => action.GetProperty("tool").GetString()).ToArray());
        Assert.Equal("all-text", actions[0].GetProperty("args").GetProperty("mode").GetString());
        Assert.Equal("all", actions[1].GetProperty("args").GetProperty("workspace_id").GetString());
        Assert.Equal("source", actions[2].GetProperty("args").GetProperty("mode").GetString());
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
        Assert.Contains("Example Web Tool  web  source_id=", webSearch);
        Assert.Contains("  :3  ", webSearch);
        Assert.Contains("WebToolMarker appears in markdown", webSearch);
        Assert.DoesNotContain("ci.log", webSearch);

        string read = tool.Content("read", source_id: sourceId, line: 3, context_lines: 0);
        Assert.Contains("Example Web Tool:3-3", read);
        Assert.Contains("3: WebToolMarker appears in markdown.", read);

        string listJson = tool.Content("list", content_kind: TextContentKind.Web, format: "json");
        using JsonDocument listDoc = JsonDocument.Parse(listJson);
        JsonElement listedSource = Assert.Single(
            listDoc.RootElement.GetProperty("kinds")[0].GetProperty("sources").EnumerateArray());
        Assert.Equal(sourceId, listedSource.GetProperty("source_id").GetString());
        Assert.Equal("https://example.test/web-tool", listedSource.GetProperty("url").GetString());
    }

    [Fact]
    public void Content_List_ReturnsBoundedPerKindInventoryWithExactTotals()
    {
        var tool = new ContentTool(_workspace, new ContentCorpusExternalStore());
        for (int i = 0; i < 5; i++)
        {
            string path = Path.Combine(_dir, $"external-{i}.log");
            File.WriteAllText(path, $"external {i}");
            tool.Content("import", path: path, display_path: $"external-{i}.log");
        }
        for (int i = 0; i < 3; i++)
        {
            string path = Path.Combine(_dir, $"web-{i}.md");
            File.WriteAllText(path, $"web {i}");
            tool.Content(
                "add_markdown",
                path: path,
                url: $"https://example.test/{i}",
                display_path: $"web-{i}");
        }

        string json = tool.Content("list", limit: 2, format: "json");

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        Assert.Equal(
            ["schema_version", "per_kind_limit", "total_count", "returned_count", "omitted_count", "kinds"],
            root.EnumerateObject().Select(static property => property.Name).ToArray());
        Assert.Equal(2, root.GetProperty("schema_version").GetInt32());
        Assert.Equal(8, root.GetProperty("total_count").GetInt32());
        Assert.Equal(4, root.GetProperty("returned_count").GetInt32());
        Assert.Equal(4, root.GetProperty("omitted_count").GetInt32());
        JsonElement[] kinds = root.GetProperty("kinds").EnumerateArray().ToArray();
        Assert.Equal([TextContentKind.ExternalFile, TextContentKind.Web],
            kinds.Select(static kind => kind.GetProperty("content_kind").GetString()!).ToArray());
        Assert.Equal([5, 3], kinds.Select(static kind => kind.GetProperty("total_count").GetInt32()).ToArray());
        Assert.All(kinds, static kind => Assert.Equal(2, kind.GetProperty("returned_count").GetInt32()));
        Assert.All(kinds, static kind => Assert.Equal(2, kind.GetProperty("sources").GetArrayLength()));
        Assert.True(json.Length < 4_000, json);
    }

    [Fact]
    public void Content_List_CompactAndJsonStayWithinHardCharacterBudgets()
    {
        var tool = new ContentTool(_workspace, new ContentCorpusExternalStore());
        string escapeHeavy = string.Concat(Enumerable.Repeat("\u0001\"\\", 2_000));
        for (int i = 0; i < 20; i++)
        {
            string externalPath = Path.Combine(_dir, $"bounded-{i}.log");
            File.WriteAllText(externalPath, "bounded");
            tool.Content("import", path: externalPath, display_path: escapeHeavy + i);

            string webPath = Path.Combine(_dir, $"bounded-{i}.md");
            File.WriteAllText(webPath, "bounded");
            tool.Content(
                "add_markdown",
                path: webPath,
                url: $"https://example.test/{i}/" + escapeHeavy,
                display_path: escapeHeavy + i);
        }

        string compact = tool.Content("list", limit: 20);
        string json = tool.Content("list", limit: 20, format: "json");

        Assert.True(compact.Length <= 16_000, compact.Length.ToString());
        Assert.True(json.Length <= 48_000, json.Length.ToString());
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal(40, doc.RootElement.GetProperty("returned_count").GetInt32());
    }

    [Fact]
    public void Content_Shape_EscapeHeavyLinesReturnValidJsonWithinHardCharacterBudget()
    {
        string path = Path.Combine(_dir, "shape-escape-heavy.log");
        string escapeHeavy = string.Concat(Enumerable.Repeat("\u0001\"\\", 2_000));
        File.WriteAllText(path, string.Join('\n', Enumerable.Repeat(escapeHeavy, 10)));
        var tool = new ContentTool(_workspace, new ContentCorpusExternalStore());
        using JsonDocument imported = JsonDocument.Parse(tool.Content("import", path: path, format: "json"));
        string sourceId = imported.RootElement.GetProperty("source_id").GetString()!;

        string json = tool.Content("shape", source_id: sourceId, format: "json");

        Assert.True(json.Length <= 8_000, json.Length.ToString());
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal(2, doc.RootElement.GetProperty("schema_version").GetInt32());
        Assert.Equal(5, doc.RootElement.GetProperty("head").GetArrayLength());
        Assert.Equal(5, doc.RootElement.GetProperty("tail").GetArrayLength());
    }

    [Fact]
    public void Content_Shape_ReturnsBoundedHeadTailAndTextDerivedSeverityCounts()
    {
        string path = Path.Combine(_dir, "shape.log");
        File.WriteAllText(path, """
            DEBUG preparing
            INFO connected
            ordinary line
            WARN retrying
            ERROR request failed
            FATAL shutting down
            final line
            """);
        var tool = new ContentTool(_workspace, new ContentCorpusExternalStore());
        using JsonDocument imported = JsonDocument.Parse(tool.Content("import", path: path, format: "json"));
        string sourceId = imported.RootElement.GetProperty("source_id").GetString()!;

        string json = tool.Content("shape", source_id: sourceId, format: "json");

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        Assert.Equal(2, root.GetProperty("schema_version").GetInt32());
        Assert.Equal("text_derived", root.GetProperty("severity_basis").GetString());
        Assert.Equal(7, root.GetProperty("line_count").GetInt32());
        Assert.Equal(5, root.GetProperty("head").GetArrayLength());
        Assert.Equal(5, root.GetProperty("tail").GetArrayLength());
        JsonElement severity = root.GetProperty("severity");
        Assert.Equal(1, severity.GetProperty("fatal").GetInt32());
        Assert.Equal(1, severity.GetProperty("error").GetInt32());
        Assert.Equal(1, severity.GetProperty("warning").GetInt32());
        Assert.Equal(1, severity.GetProperty("info").GetInt32());
        Assert.Equal(1, severity.GetProperty("debug").GetInt32());
        Assert.Equal(2, severity.GetProperty("other").GetInt32());
        Assert.True(json.Length < 8_000, json);
    }

    [Theory]
    [InlineData(null, "missing_source_id")]
    [InlineData("external_file:missing", "source_not_found")]
    public void Content_Shape_UsesTypedJsonFailures(string? sourceId, string expectedCode)
    {
        var tool = new ContentTool(_workspace, new ContentCorpusExternalStore());
        if (sourceId is not null)
        {
            string path = Path.Combine(_dir, "shape-diagnostic.log");
            File.WriteAllText(path, "ready");
            tool.Content("import", path: path);
        }

        string json = tool.Content("shape", source_id: sourceId, format: "json");

        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal(expectedCode, doc.RootElement.GetProperty("diagnostic_code").GetString());
    }

    [Fact]
    public void Content_Shape_AmbiguousDisplayPathUsesTypedJsonFailure()
    {
        ContentTool tool = ToolWithImportedSources("shared/shape.log", "shared/shape.log");

        string json = tool.Content("shape", source_id: "shared/shape.log", format: "json");

        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal("ambiguous_source", doc.RootElement.GetProperty("diagnostic_code").GetString());
    }

    [Fact]
    public void Content_Shape_MissingCorpusUsesTypedJsonFailure()
    {
        var tool = new ContentTool(_workspace, new ContentCorpusExternalStore());

        string json = tool.Content("shape", source_id: "external_file:missing", format: "json");

        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal("content_corpus_missing", doc.RootElement.GetProperty("diagnostic_code").GetString());
    }

    [Theory]
    [InlineData("read")]
    [InlineData("shape")]
    public void Content_ReadAndShapeDiagnosticsStayWithinHardCharacterBudgets(string operation)
    {
        ContentTool tool = ToolWithImportedSources("docs/plans/known.log");
        string requested = "docs/plans/" + string.Concat(Enumerable.Repeat("\u0001\"\\", 10_000));

        string compact = tool.Content(operation, source_id: requested, line: 1);
        string json = tool.Content(operation, source_id: requested, line: 1, format: "json");

        Assert.True(compact.Length <= 8_000, compact.Length.ToString());
        Assert.True(json.Length <= 8_000, json.Length.ToString());
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal("source_not_found", doc.RootElement.GetProperty("diagnostic_code").GetString());
    }

    [Fact]
    public void Content_ExportOperationIsHardRemovedFromMcpSurface()
    {
        var method = typeof(ContentTool).GetMethod(nameof(ContentTool.Content))!;
        string description = method.GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), inherit: false)
            .Cast<System.ComponentModel.DescriptionAttribute>()
            .Single()
            .Description;

        string result = new ContentTool(_workspace, new ContentCorpusExternalStore()).Content("export");

        Assert.StartsWith("content failed:", result, StringComparison.Ordinal);
        Assert.DoesNotContain("export", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("export", description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(method.GetParameters(), static parameter => parameter.Name == "content_workspace_id");
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

        Assert.Contains("alpha (ws-alpha)", compact, StringComparison.Ordinal);
        Assert.Contains("beta (ws-beta)", compact, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(compact, "alpha (ws-alpha)"));
        Assert.Equal(1, CountOccurrences(compact, "beta (ws-beta)"));
        Assert.Contains("alpha.log  external_file  source_id=", compact, StringComparison.Ordinal);
        Assert.Contains("beta.log  external_file  source_id=", compact, StringComparison.Ordinal);
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
    public void Content_ReadTelemetry_UsesTheResolvedPathAndCrossWorkspaceIdentity()
    {
        string alphaRoot = Path.Combine(_dir, "telemetry-read-alpha");
        Directory.CreateDirectory(alphaRoot);
        string alphaSymbols = Path.Combine(alphaRoot, ".miller", "symbols.db");
        string alphaLog = Path.Combine(alphaRoot, "opaque-input-name.log");
        File.WriteAllText(alphaLog, "ResolvedPathTelemetryMarker");
        var store = new ContentCorpusExternalStore();
        ExternalContentImportResult imported = store.Import(
            ContentCorpusSidecar.ContentDbPathFor(alphaSymbols), alphaLog, displayPath: "docs/served.md");
        using (var registry = WorkspaceRegistry.Open(_workspace.RegistryDbPath))
        {
            registry.UpsertSeen("ws-alpha", "alpha", alphaRoot, alphaSymbols);
            registry.MarkScanned("ws-alpha", revision: 1);
        }
        var tool = new ContentTool(_workspace, store);

        using (var ledger = TelemetryLedger.Open(
            _workspace.TelemetryDbPath, _workspace.WorkspaceId, _workspace.WorkspaceRoot))
        {
            using TelemetryScope scope = ledger.Measure("content", op: null);
            string output = tool.Content(
                "read", source_id: imported.SourceId, workspace_id: "ws-alpha", line: 1, context_lines: 0);
            Assert.Contains("ResolvedPathTelemetryMarker", output, StringComparison.Ordinal);
        }

        var row = ReadTelemetryAttribution(_workspace.TelemetryDbPath);
        Assert.Equal("ws-alpha", row.WorkspaceId);
        Assert.Equal(alphaRoot, row.WorkspaceRoot);
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("docs/served.md"))),
            row.TargetHash);
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
        Assert.Contains("ci.log  external_file  source_id=", search, StringComparison.Ordinal);
        Assert.Contains("  :2  ", search, StringComparison.Ordinal);
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
    public void Content_ReadUnknownSource_CompactIncludesRecoveryGuidance()
    {
        var tool = new ContentTool(_workspace, new ContentCorpusExternalStore());
        string logPath = Path.Combine(_dir, "known.log");
        File.WriteAllText(logPath, "Known source exists.\n");
        tool.Content("import", path: logPath);

        string output = tool.Content("read", source_id: "not-a-real-source-id", line: 1, context_lines: 0);

        Assert.Contains("Content source 'not-a-real-source-id' was not found", output, StringComparison.Ordinal);
        Assert.Contains("content search", output, StringComparison.Ordinal);
        Assert.Contains("content list", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Content_ReadUnknownSource_JsonIncludesDiagnosticRecovery()
    {
        var tool = new ContentTool(_workspace, new ContentCorpusExternalStore());
        string logPath = Path.Combine(_dir, "known.log");
        File.WriteAllText(logPath, "Known source exists.\n");
        tool.Content("import", path: logPath);

        string output = tool.Content("read", source_id: "not-a-real-source-id", line: 1, context_lines: 0, format: "json");

        using JsonDocument doc = JsonDocument.Parse(output);
        JsonElement root = doc.RootElement;
        Assert.Equal("read", root.GetProperty("operation").GetString());
        Assert.Equal("source_not_found", root.GetProperty("diagnostic_code").GetString());
        Assert.Contains("not-a-real-source-id", root.GetProperty("error").GetString(), StringComparison.Ordinal);
        JsonElement[] actions = root.GetProperty("next_actions").EnumerateArray().ToArray();
        Assert.Contains(actions, action =>
            action.GetProperty("tool").GetString() == "content"
            && action.GetProperty("args").GetProperty("operation").GetString() == "search");
        Assert.Contains(actions, action =>
            action.GetProperty("tool").GetString() == "content"
            && action.GetProperty("args").GetProperty("operation").GetString() == "list");
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

        Assert.StartsWith("content read failed:", output, StringComparison.Ordinal);
        Assert.Contains("matches multiple imported sources", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("external_file:", output, StringComparison.Ordinal);
        Assert.Contains("content list", output, StringComparison.Ordinal);
        Assert.DoesNotContain("AmbiguousAliasMarker", output, StringComparison.OrdinalIgnoreCase);
    }
}
