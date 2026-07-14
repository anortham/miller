using Miller.Indexing;
using System.Text.Json;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// The live end-to-end path (D6): drive the real <c>julie-extract</c> over a tiny throwaway repo,
/// read the produced DB, build the index, and assert a known symbol is found. Subprocess + extraction won't
/// fit the &lt;10s default budget, so this is <c>[Trait("Category","Scale")]</c> and EXCLUDED by the default
/// suite (<c>--filter "Category!=Scale"</c>). It is network/binary dependent: if <c>.tools/julie-extract</c>
/// is absent (restore not run) it <see cref="Assert.Skip"/>s with an actionable message rather than failing.
/// </summary>
[Trait("Category", "Scale")]
public sealed class LiveExtractIndexTests
{
    [Fact]
    public void LiveExtract_TsqlMerge_RoundTripsThroughPatternReadersAndExport()
    {
        string binary = ScaleTestSupport.RequireJulieServer();
        string work = Path.Combine(Path.GetTempPath(), "miller-live-merge-" + Guid.NewGuid().ToString("N"));
        string repo = Path.Combine(work, "repo");
        string db = Path.Combine(work, ".miller", "symbols.db");
        Directory.CreateDirectory(repo);

        try
        {
            File.WriteAllText(Path.Combine(repo, "merge.sql"), """
                MERGE [dbo].[CustomerTarget] AS target
                USING (VALUES (1, N'alpha')) AS source (CustomerId, Name)
                ON target.CustomerId = source.CustomerId
                WHEN MATCHED THEN
                    UPDATE SET target.Name = source.Name
                WHEN NOT MATCHED THEN
                    INSERT (CustomerId, Name) VALUES (source.CustomerId, source.Name);
                """);

            var runner = new JulieExtractRunner(binary);
            ExtractReport report = runner.Scan(repo, db, force: true);
            Assert.NotEqual("failed", report.Status);

            var reader = new PatternFactsReader();
            PatternListRow listed = Assert.Single(
                reader.List(db),
                row => row.PatternId == "sql.merge_statement.v1");
            Assert.Contains("sql", listed.Languages);

            PatternMatchRow match = Assert.Single(reader.Search(
                db,
                patternId: "sql.merge_statement.v1",
                language: null,
                metadataFilter: null,
                limit: 10));
            Assert.Equal("merge.sql", match.Path);
            Assert.Equal("CustomerTarget", match.Metadata.GetProperty("target_table").GetString());
            Assert.Equal("values", match.Metadata.GetProperty("source_kind").GetString());
            Assert.False(match.Metadata.TryGetProperty("source_table", out _));
            Assert.True(match.Metadata.GetProperty("has_when_matched").GetBoolean());
            Assert.True(match.Metadata.GetProperty("has_when_not_matched").GetBoolean());
            Assert.Null(match.MetadataError);

            string exportLine = Assert.Single(
                PatternFactsExportReader.ExportJsonLines(db)
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries),
                line => line.Contains("\"pattern_id\":\"sql.merge_statement.v1\"", StringComparison.Ordinal));
            using JsonDocument export = JsonDocument.Parse(exportLine);
            Assert.Equal("merge.sql", export.RootElement.GetProperty("path").GetString());
            using JsonDocument metadata = JsonDocument.Parse(
                export.RootElement.GetProperty("metadata_json").GetString()!);
            Assert.Equal("CustomerTarget", metadata.RootElement.GetProperty("target_table").GetString());
            Assert.Equal("values", metadata.RootElement.GetProperty("source_kind").GetString());
            Assert.True(metadata.RootElement.GetProperty("has_when_matched").GetBoolean());
            Assert.True(metadata.RootElement.GetProperty("has_when_not_matched").GetBoolean());
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void LiveExtract_ScanReadBuildQuery_FindsKnownSymbol()
    {
        string binary = ScaleTestSupport.RequireJulieServer();

        // --- create a tiny throwaway fixture repo under a temp dir ---
        string work = Path.Combine(Path.GetTempPath(), "miller-live-" + Guid.NewGuid().ToString("N"));
        string repo = Path.Combine(work, "repo");
        string dbDir = Path.Combine(work, ".miller");
        string db = Path.Combine(dbDir, "symbols.db");
        Directory.CreateDirectory(repo);
        try
        {
            File.WriteAllText(Path.Combine(repo, "widget.cs"), """
                namespace Demo;

                public sealed class WidgetFactory
                {
                    public Widget CreateMillerWidget(int size) => new Widget(size);
                }

                public sealed record Widget(int Size);
                """);

            // --- scan with the real binary ---
            var runner = new JulieExtractRunner(binary!);
            ExtractReport report = runner.Scan(repo, db, force: true);

            Assert.Equal("scan", report.Operation);
            Assert.NotEqual("failed", report.Status);
            Assert.NotNull(report.Artifact);                                                                      // a successful scan carries the artifact block
            Assert.Equal(MillerExtractContract.ExpectedSqliteSchemaVersion, report.Artifact!.SqliteSchemaVersion); // pinned schema
            Assert.Equal(MillerExtractContract.ExpectedExtractContractVersion, report.Artifact.ExtractContractVersion); // pinned contract
            Assert.True(report.SymbolsExtracted > 0, "scan should extract at least one symbol");

            // --- read -> build -> query ---
            var symbols = SqliteSymbolReader.Read(db);
            Assert.NotEmpty(symbols);

            var repoIndex = MillerRepositoryIndex.Build(symbols);
            var hits = repoIndex.Search("CreateMillerWidget", limit: 10);

            Assert.NotEmpty(hits);
            var names = hits.Select(h => repoIndex.Resolve(h.Document.DocId).Name).ToList();
            Assert.Contains("CreateMillerWidget", names);

            // The component token "widget" must also surface the type via the tokenizer flow end to end.
            var widgetHits = repoIndex.Search("widget", limit: 20);
            Assert.Contains(widgetHits, h => repoIndex.Resolve(h.Document.DocId).Name == "WidgetFactory");

            // info reuses the flat shape; symbols land in *_total.
            ExtractReport info = runner.Info(db);
            Assert.True(info.SymbolsTotal > 0, "info should report the extracted symbol total");
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { /* best-effort temp cleanup */ }
        }
    }
}
