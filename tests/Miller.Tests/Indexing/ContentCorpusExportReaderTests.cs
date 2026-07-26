using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class ContentCorpusExportReaderTests : IDisposable
{
    private readonly string _dir;
    private readonly string _contentDbPath;

    public ContentCorpusExportReaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-content-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _contentDbPath = Path.Combine(_dir, "content.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void ExportJsonLines_IsDeterministic_AndIncludesErosMetadata()
    {
        using var fixture = WriteWorkspaceContent();
        string webPath = Path.Combine(_dir, "page.md");
        File.WriteAllText(webPath, "Web export marker\n");
        new ContentCorpusExternalStore().ImportMarkdown(
            _contentDbPath,
            webPath,
            "https://example.test/export",
            displayPath: "Export Page");
        var reader = new ContentCorpusExportReader();

        string first = reader.ExportJsonLines(_contentDbPath);
        string second = reader.ExportJsonLines(_contentDbPath);
        using var streamed = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        streamed.NewLine = "\r\n";
        long streamedCount = reader.WriteJsonLines(_contentDbPath, streamed);

        Assert.Equal(first, second);
        JsonElement[] rows = ParseLines(first);
        Assert.Equal(first, streamed.ToString());
        Assert.Equal(rows.Length, streamedCount);
        Assert.Contains(rows, row => row.GetProperty("content_kind").GetString() == TextContentKind.WorkspaceSource);
        Assert.Contains(rows, row => row.GetProperty("content_kind").GetString() == TextContentKind.Web);
        Assert.Equal(rows.Select(row => row.GetProperty("content_kind").GetString()).Order(StringComparer.Ordinal),
            rows.Select(row => row.GetProperty("content_kind").GetString()));

        JsonElement source = rows.Single(row => row.GetProperty("content_kind").GetString() == TextContentKind.WorkspaceSource);
        Assert.Equal(ContentCorpusSchema.SchemaVersion, source.GetProperty("schema_version").GetInt32());
        Assert.Equal("workspace-1", source.GetProperty("workspace_id").GetString());
        Assert.Equal(12, source.GetProperty("workspace_revision").GetInt64());
        Assert.Equal("src/Api.cs", source.GetProperty("path").GetString());
        Assert.Equal(JsonValueKind.Null, source.GetProperty("url").ValueKind);
        Assert.Equal("src/Api.cs", source.GetProperty("display_path").GetString());
        Assert.Equal("csharp", source.GetProperty("language").GetString());
        Assert.Equal(1, source.GetProperty("line_start").GetInt32());
        Assert.True(source.GetProperty("byte_end").GetInt64() > source.GetProperty("byte_start").GetInt64());
        Assert.True(source.GetProperty("source_bytes").GetInt64() > 0);
        Assert.StartsWith("blake3:", source.GetProperty("content_hash").GetString(), StringComparison.Ordinal);
        Assert.Contains("KnownExportError", source.GetProperty("chunk_text").GetString());
        Assert.True(source.GetProperty("doc_len").GetInt32() > 0);
        Assert.False(source.GetProperty("is_test").GetBoolean());
        Assert.Equal("sym-api", source.GetProperty("containing_symbol_id").GetString());
        Assert.Equal("Api", source.GetProperty("containing_symbol_name").GetString());
        Assert.Equal("active", source.GetProperty("source_status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(source.GetProperty("indexed_at_utc").GetString()));
        Assert.False(source.TryGetProperty("embedding", out _));

        JsonElement web = rows.Single(row => row.GetProperty("content_kind").GetString() == TextContentKind.Web);
        Assert.Equal(JsonValueKind.Null, web.GetProperty("workspace_id").ValueKind);
        Assert.Equal(JsonValueKind.Null, web.GetProperty("workspace_revision").ValueKind);
        Assert.Equal("https://example.test/export", web.GetProperty("url").GetString());
        Assert.Equal("Export Page", web.GetProperty("display_path").GetString());
    }

    [Fact]
    public void ExportJsonLines_CanFilterByKindAndWorkspaceId()
    {
        using var fixture = WriteWorkspaceContent();
        string logPath = Path.Combine(_dir, "ci.log");
        File.WriteAllText(logPath, "External export marker\n");
        string webPath = Path.Combine(_dir, "page.md");
        File.WriteAllText(webPath, "Web export marker\n");
        var store = new ContentCorpusExternalStore();
        store.Import(_contentDbPath, logPath);
        store.ImportMarkdown(_contentDbPath, webPath, "https://example.test/page", displayPath: "Web Page");
        var reader = new ContentCorpusExportReader();

        JsonElement[] webRows = ParseLines(reader.ExportJsonLines(_contentDbPath, contentKind: TextContentKind.Web));
        Assert.Single(webRows);
        Assert.All(webRows, row => Assert.Equal(TextContentKind.Web, row.GetProperty("content_kind").GetString()));

        JsonElement[] workspaceRows = ParseLines(reader.ExportJsonLines(_contentDbPath, workspaceId: "workspace-1"));
        Assert.Equal(2, workspaceRows.Length);
        Assert.All(workspaceRows, row => Assert.Equal("workspace-1", row.GetProperty("workspace_id").GetString()));
        Assert.DoesNotContain(workspaceRows, row => row.GetProperty("content_kind").GetString() == TextContentKind.ExternalFile);
        Assert.DoesNotContain(workspaceRows, row => row.GetProperty("content_kind").GetString() == TextContentKind.Web);

        JsonElement[] sourceRows = ParseLines(reader.ExportJsonLines(
            _contentDbPath,
            contentKind: TextContentKind.WorkspaceSource,
            workspaceId: "workspace-1"));
        JsonElement source = Assert.Single(sourceRows);
        Assert.Equal(TextContentKind.WorkspaceSource, source.GetProperty("content_kind").GetString());
        Assert.Equal("workspace-1", source.GetProperty("workspace_id").GetString());
    }

    [Fact]
    public void ExportJsonLines_LegacySchemaAndMissingOptionalColumn_PreservesImportedText()
    {
        using var fixture = WriteWorkspaceContent();
        string logPath = Path.Combine(_dir, "legacy.log");
        File.WriteAllText(logPath, "LegacyRecoveryMarker\n");
        new ContentCorpusExternalStore().Import(
            _contentDbPath,
            logPath,
            displayPath: "legacy.log");
        using (var connection = new SqliteConnection($"Data Source={_contentDbPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE content_meta SET schema_version = 1;
                ALTER TABLE content_chunks DROP COLUMN url;
                """;
            command.ExecuteNonQuery();
        }

        string jsonl = new ContentCorpusExportReader().ExportJsonLines(
            _contentDbPath,
            contentKind: TextContentKind.ExternalFile);

        JsonElement row = Assert.Single(ParseLines(jsonl));
        Assert.Equal(1, row.GetProperty("schema_version").GetInt32());
        Assert.Equal("legacy.log", row.GetProperty("display_path").GetString());
        Assert.Contains("LegacyRecoveryMarker", row.GetProperty("chunk_text").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("url").ValueKind);
    }

    private JulieDbFixture WriteWorkspaceContent()
    {
        const string sourceText = """
            public class Api
            {
                public void Handle()
                {
                    throw new InvalidOperationException("KnownExportError");
                }
            }
            """;
        var fixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new JulieDbFixture.SymbolRow("sym-api", "Api", "class", "csharp", "src/Api.cs", "public class Api", 1, null)
                {
                    EndLine = 7,
                },
            ],
            fileContent: new Dictionary<string, string>
            {
                ["src/Api.cs"] = sourceText,
            },
            extraFiles:
            [
                new JulieDbFixture.FileSpec("docs/guide.md")
                {
                    Language = "markdown",
                    DiskText = "KnownExportDoc appears here.",
                },
            ]);

        ContentCorpusWriter.Write(
            _contentDbPath,
            fixture.DbPath,
            fixture.WorkspaceRoot,
            workspaceId: "workspace-1",
            revision: 12);
        return fixture;
    }

    private static JsonElement[] ParseLines(string jsonl)
    {
        string[] lines = jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Select(static line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
    }
}
