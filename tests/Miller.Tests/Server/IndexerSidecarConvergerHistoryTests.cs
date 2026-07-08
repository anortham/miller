using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Miller.Core.Tokenization;
using Miller.Indexing;
using Miller.Server.Hosting;
using Miller.Tests.Indexing;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// The metric-history cheap arm as wired into <see cref="IndexerSidecarConverger.Converge"/>: recording runs after
/// the sidecar steps, reads the real <c>symbols.db</c>, and is best-effort — a locked or corrupt history file never
/// changes converge behaviour or throws out of the hook.
/// </summary>
public sealed class IndexerSidecarConvergerHistoryTests
{
    [Fact]
    public void Converge_RecordsOneConvergeSnapshot_WithAggregatesAndNoMarkerMetric()
    {
        using var fx = NewFixture();
        var calls = new List<string>();
        IndexerSidecarConverger converger = NewConverger(calls);

        converger.Converge(fx.DbPath, fx.WorkspaceRoot, "workspace-1", revision: 9, fullRebuild: true);

        // The sidecar step still ran — history recording is layered on, not in place of it.
        Assert.Contains("content:9", calls);

        string historyPath = MetricSnapshotAggregates.HistoryDbPathFor(fx.DbPath);
        Assert.Equal(1, SnapshotCount(historyPath));
        Assert.Equal("converge", ScalarString(historyPath, "SELECT source FROM snapshots LIMIT 1;"));
        Assert.Equal(9L, ScalarLong(historyPath, "SELECT revision FROM snapshots LIMIT 1;"));
        Assert.Equal(2d, ScalarDouble(historyPath,
            $"SELECT value FROM snapshot_metrics WHERE metric = '{MetricSnapshotAggregates.SymbolCount}';"));
        // Search disabled ⟹ no region index ⟹ marker metric is absent (not 0).
        Assert.Equal(0L, ScalarLong(historyPath,
            $"SELECT COUNT(*) FROM snapshot_metrics WHERE metric = '{MetricSnapshotAggregates.MarkerTotal}';"));
    }

    [Fact]
    public void Converge_WithRegionSearchDb_RecordsMarkerTotalAndBreakdown()
    {
        using var fx = NewFixture();
        var calls = new List<string>();
        IndexerSidecarConverger converger = NewConverger(calls, searchEnabled: true);

        // A region search index bearing a single TODO comment region, at the revision the converge will record.
        string searchDbPath = Path.Combine(Path.GetDirectoryName(fx.DbPath)!, "search.db");
        WriteRegionSearchDb(searchDbPath, revision: 11, "// TODO tidy this branch later");

        converger.Converge(fx.DbPath, fx.WorkspaceRoot, "workspace-1", revision: 11, fullRebuild: false);

        string historyPath = MetricSnapshotAggregates.HistoryDbPathFor(fx.DbPath);
        Assert.Equal(1d, ScalarDouble(historyPath,
            $"SELECT value FROM snapshot_metrics WHERE metric = '{MetricSnapshotAggregates.MarkerTotal}';"));
        Assert.Equal("{\"TODO\":1,\"FIXME\":0,\"HACK\":0,\"XXX\":0}", ScalarString(historyPath,
            $"SELECT detail_json FROM snapshot_metrics WHERE metric = '{MetricSnapshotAggregates.MarkerTotal}';"));
    }

    [Fact]
    public void Converge_SameRevisionTwice_RecordsOneSnapshot()
    {
        using var fx = NewFixture();
        var calls = new List<string>();
        IndexerSidecarConverger converger = NewConverger(calls);

        converger.Converge(fx.DbPath, fx.WorkspaceRoot, "workspace-1", revision: 4, fullRebuild: false);
        converger.Converge(fx.DbPath, fx.WorkspaceRoot, "workspace-1", revision: 4, fullRebuild: false);

        Assert.Equal(1, SnapshotCount(MetricSnapshotAggregates.HistoryDbPathFor(fx.DbPath)));
    }

    [Fact]
    public void Converge_HistoryLockHeld_SkipsSnapshotWithoutThrowingOrBlocking()
    {
        using var fx = NewFixture();
        var calls = new List<string>();
        IndexerSidecarConverger converger = NewConverger(calls);
        string historyPath = MetricSnapshotAggregates.HistoryDbPathFor(fx.DbPath);

        // Pre-hold the history write lock: the leader converge arm is skip-on-busy (TimeSpan.Zero).
        using (MetricHistoryWriteLock.AcquireFor(historyPath, TimeSpan.FromSeconds(5)))
        {
            converger.Converge(fx.DbPath, fx.WorkspaceRoot, "workspace-1", revision: 3, fullRebuild: false);
        }

        // Converge completed its sidecar work and did not throw; no snapshot was written.
        Assert.Contains("content:3", calls);
        Assert.False(File.Exists(historyPath));
    }

    [Fact]
    public void Converge_CorruptHistoryFile_RecoversAndRecordsWithoutThrowing()
    {
        using var fx = NewFixture();
        var calls = new List<string>();
        IndexerSidecarConverger converger = NewConverger(calls);
        string historyPath = MetricSnapshotAggregates.HistoryDbPathFor(fx.DbPath);

        Directory.CreateDirectory(Path.GetDirectoryName(historyPath)!);
        File.WriteAllText(historyPath, "this is not a sqlite database");

        converger.Converge(fx.DbPath, fx.WorkspaceRoot, "workspace-1", revision: 6, fullRebuild: false);

        // Reactive recovery renamed the corrupt file aside and recorded the snapshot against a fresh DB.
        Assert.Equal(1, SnapshotCount(historyPath));
        string dir = Path.GetDirectoryName(historyPath)!;
        Assert.NotEmpty(Directory.GetFiles(dir, MetricHistoryStore.HistoryDbFileName + ".corrupt-*"));
    }

    // ---- helpers ---------------------------------------------------------------------------------------------

    private static JulieDbFixture NewFixture() =>
        JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                Row("id-a", "Aye", "src/A.cs"),
                Row("id-b", "Bee", "src/B.cs"),
            });

    private static JulieDbFixture.SymbolRow Row(string id, string name, string path) =>
        new(id, name, "method", "csharp", path, $"void {name}()", 3, null)
        {
            EndLine = 7,
            StartByte = 30,
            EndByte = 70,
        };

    // A converger with no-op sidecar delegates so only the history arm exercises the real DB. searchEnabled=true
    // lets the history arm open a pre-written region search.db (the no-op search delegates still do no real work).
    private static IndexerSidecarConverger NewConverger(List<string> calls, bool searchEnabled = false) =>
        new(
            searchEnabled,
            (symbolsDbPath, workspaceRoot, workspaceId, revision) =>
            {
                calls.Add($"content:{revision}");
                return false;
            },
            (string _, long _, string _, out string? reason) => { reason = null; return false; },
            (string _, long _, string _, out string? reason) => { reason = null; return false; },
            symbolsDbPath => Path.Combine(Path.GetDirectoryName(symbolsDbPath)!, "content.db"),
            symbolsDbPath => Path.Combine(Path.GetDirectoryName(symbolsDbPath)!, "search.db"),
            (_, _, _) => false,
            NullLogger.Instance);

    // Minimal region-bearing search.db (same shape SearchIndexWriter emits) with one comment region, so the history
    // arm's FtsRegionSearchIndex.Open at `revision` succeeds and the marker scan finds the seeded marker text.
    private static void WriteRegionSearchDb(string searchDbPath, long revision, string commentRawText)
    {
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = searchDbPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false,
        };
        using var connection = new SqliteConnection(csb.ToString());
        connection.Open();

        using (var ddl = connection.CreateCommand())
        {
            ddl.CommandText = """
                CREATE VIRTUAL TABLE regions_fts USING fts5(
                    region_id UNINDEXED, body, tokenize='unicode61 remove_diacritics 0');
                CREATE TABLE search_regions(
                    region_id TEXT PRIMARY KEY, kind TEXT NOT NULL, path TEXT NOT NULL, language TEXT NOT NULL,
                    containing_symbol_id TEXT, start_line INTEGER NOT NULL, end_line INTEGER NOT NULL,
                    start_byte INTEGER NOT NULL, end_byte INTEGER NOT NULL, raw_text TEXT NOT NULL,
                    doc_len INTEGER NOT NULL);
                CREATE INDEX ix_search_regions_kind ON search_regions(kind);
                CREATE TABLE meta(
                    revision INTEGER, doc_count INTEGER, avgdl REAL, schema_version INTEGER,
                    region_count INTEGER, region_avgdl REAL, region_index_enabled INTEGER);
                """;
            ddl.ExecuteNonQuery();
        }

        var tokens = new List<string>(16);
        CodeTokenizer.Tokenize(commentRawText, tokens);
        int docLen = tokens.Count;

        using (var region = connection.CreateCommand())
        {
            region.CommandText = """
                INSERT INTO search_regions
                    (region_id, kind, path, language, containing_symbol_id, start_line, end_line,
                     start_byte, end_byte, raw_text, doc_len)
                VALUES ('r-1', 'comment', 'src/A.cs', 'csharp', NULL, 3, 3, 0, $eb, $raw, $len);
                """;
            region.Parameters.AddWithValue("$eb", commentRawText.Length);
            region.Parameters.AddWithValue("$raw", commentRawText);
            region.Parameters.AddWithValue("$len", docLen);
            region.ExecuteNonQuery();
        }

        using (var fts = connection.CreateCommand())
        {
            fts.CommandText = "INSERT INTO regions_fts(region_id, body) VALUES ('r-1', $body);";
            fts.Parameters.AddWithValue("$body", string.Join(' ', tokens));
            fts.ExecuteNonQuery();
        }

        using var meta = connection.CreateCommand();
        meta.CommandText = """
            INSERT INTO meta(revision, doc_count, avgdl, schema_version, region_count, region_avgdl, region_index_enabled)
            VALUES ($rev, 0, 0.0, $schema, 1, $avgdl, 1);
            """;
        meta.Parameters.AddWithValue("$rev", revision);
        meta.Parameters.AddWithValue("$schema", SearchIndexWriter.SchemaVersion);
        meta.Parameters.AddWithValue("$avgdl", (double)docLen);
        meta.ExecuteNonQuery();
    }

    private static int SnapshotCount(string historyPath) =>
        (int)ScalarLong(historyPath, "SELECT COUNT(*) FROM snapshots;");

    private static long ScalarLong(string dbPath, string sql) => Scalar(dbPath, sql, r => r.GetInt64(0));
    private static double ScalarDouble(string dbPath, string sql) => Scalar(dbPath, sql, r => r.GetDouble(0));
    private static string ScalarString(string dbPath, string sql) => Scalar(dbPath, sql, r => r.GetString(0));

    private static T Scalar<T>(string dbPath, string sql, Func<SqliteDataReader, T> map)
    {
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
        };
        using var connection = new SqliteConnection(csb.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using SqliteDataReader reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return map(reader);
    }
}
