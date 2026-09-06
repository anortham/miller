using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Indexing.Store;
using Miller.Tests.Support;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class SidecarConvergenceCostTests
{
    [Fact]
    public void Collector_reports_logical_work_without_counting_internal_storage_rows()
    {
        var measurement = new SidecarConvergenceMeasurement();
        measurement.RecordDelta(rowsRead: 4, changedPaths: 3, deletedPaths: 1);
        measurement.RecordRows(inserted: 2, updated: 1, deleted: 1);
        measurement.RecordFull(files: 3, documents: 2);

        SidecarConvergenceCounters counters = measurement.Complete();

        Assert.Equal(new(4, 3, 1, 2, 1, 1, 3, 2), Deterministic(counters));
        Assert.True(counters.Elapsed >= TimeSpan.Zero);
    }

    [Fact]
    public void Completed_counter_snapshot_keeps_its_operation_boundary_time()
    {
        TimeSpan elapsed = TimeSpan.FromMilliseconds(4);
        var measurement = new SidecarConvergenceMeasurement(() => elapsed);

        SidecarConvergenceCounters completed = measurement.Complete();
        elapsed = TimeSpan.FromSeconds(9);

        Assert.Equal(TimeSpan.FromMilliseconds(4), completed.Elapsed);
        Assert.Equal(TimeSpan.FromSeconds(9), measurement.Complete().Elapsed);
    }

    [Theory]
    [InlineData(StoreSidecarKind.Content)]
    [InlineData(StoreSidecarKind.Search)]
    public void Incremental_and_full_builds_have_canonical_row_and_stamp_equivalence(StoreSidecarKind kind)
    {
        using StoreFixture fixture = StoreFixture.Create();
        ConfigureInitial(fixture);
        string incrementalPath = StoreSidecarCatalog.PathFor(fixture.Binding.StoreRoot, kind, fixture.Binding.ViewId);
        using (FamilyStoreReadSession initial = FamilyStoreReadSession.Open(fixture.Binding))
            WriteFull(kind, incrementalPath, initial, measurement: null);
        if (kind == StoreSidecarKind.Content)
        {
            string importPath = Path.Combine(fixture.Root, "external.log");
            File.WriteAllText(importPath, "external content survives both convergence paths");
            new ContentCorpusExternalStore().Import(incrementalPath, importPath);
        }
        string fullPath = Path.Combine(fixture.Root, "full-" + kind + ".db");
        File.Copy(incrementalPath, fullPath);
        ConfigureUpdated(fixture);
        using FamilyStoreReadSession updated = FamilyStoreReadSession.Open(fixture.Binding);
        var incrementalMeasurement = new SidecarConvergenceMeasurement();

        SidecarConvergenceDetail detail = Converge(
            kind, fixture.Binding.StoreRoot, updated, incrementalMeasurement);
        var fullMeasurement = new SidecarConvergenceMeasurement();
        WriteFull(kind, fullPath, updated, fullMeasurement);

        Assert.Equal(SidecarConvergencePath.Incremental, detail.Path);
        if (kind == StoreSidecarKind.Search)
        {
            AssertFtsMappings(fullPath);
            AssertFtsMappings(incrementalPath);
        }
        Assert.Equal(CanonicalRows(kind, fullPath), CanonicalRows(kind, incrementalPath));
        StoreSidecarStamp expected = StoreSidecarStamp.FromSnapshot(kind, updated.Snapshot);
        Assert.Equal(expected, StoreSidecarCatalog.TryRead(incrementalPath));
        Assert.Equal(expected, StoreSidecarCatalog.TryRead(fullPath));
        Assert.Equal(
            kind == StoreSidecarKind.Content
                ? new DeterministicCounters(4, 2, 2, 1, 2, 2, 0, 0)
                : new DeterministicCounters(4, 2, 2, 1, 2, 1, 0, 0),
            Deterministic(incrementalMeasurement.Complete()));
        Assert.Equal(new(0, 0, 0, 3, 0, 0, 3, 3), Deterministic(fullMeasurement.Complete()));
    }

    [Theory]
    [InlineData(StoreSidecarKind.Content)]
    [InlineData(StoreSidecarKind.Search)]
    public void Incomplete_delta_records_full_fallback_work(StoreSidecarKind kind)
    {
        using StoreFixture fixture = StoreFixture.Create();
        ConfigureInitial(fixture);
        string path = StoreSidecarCatalog.PathFor(fixture.Binding.StoreRoot, kind, fixture.Binding.ViewId);
        using (FamilyStoreReadSession initial = FamilyStoreReadSession.Open(fixture.Binding))
            WriteFull(kind, path, initial, measurement: null);
        ConfigureUpdated(fixture);
        Execute(fixture, "DELETE FROM store_log WHERE sequence <= 2;");
        using FamilyStoreReadSession updated = FamilyStoreReadSession.Open(fixture.Binding);
        var measurement = new SidecarConvergenceMeasurement();

        SidecarConvergenceDetail detail = Converge(kind, fixture.Binding.StoreRoot, updated, measurement);
        SidecarConvergenceCounters counters = measurement.Complete();

        Assert.Equal(SidecarConvergencePath.Full, detail.Path);
        Assert.Equal(SidecarConvergenceReason.DeltaIncomplete, detail.Reason);
        Assert.Equal(new(0, 0, 0, 3, 0, 0, 3, 3), Deterministic(counters));
    }

    [Theory]
    [InlineData(StoreSidecarKind.Content)]
    [InlineData(StoreSidecarKind.Search)]
    public void Empty_delta_counts_the_inspected_zero_row_span_only(StoreSidecarKind kind)
    {
        using StoreFixture fixture = StoreFixture.Create();
        ConfigureInitial(fixture);
        string path = StoreSidecarCatalog.PathFor(fixture.Binding.StoreRoot, kind, fixture.Binding.ViewId);
        using (FamilyStoreReadSession initial = FamilyStoreReadSession.Open(fixture.Binding))
            WriteFull(kind, path, initial, measurement: null);
        Execute(
            fixture,
            """
            INSERT INTO store_log VALUES
              (3,'request-reuse','store_import_l3_chunk','view-a',2,2,3,0,'{}','2026-08-09T00:00:03Z'),
              (4,'request-reuse','store_import_completed','view-a',2,NULL,3,1,'{"manifest":{"disposition":"reused"}}','2026-08-09T00:00:04Z'),
              (5,'request-reuse','store_resolve_completed','view-a',2,NULL,3,1,'{}','2026-08-09T00:00:05Z');
            """);
        using FamilyStoreReadSession updated = FamilyStoreReadSession.Open(fixture.Binding);
        var measurement = new SidecarConvergenceMeasurement();

        SidecarConvergenceDetail detail = Converge(kind, fixture.Binding.StoreRoot, updated, measurement);

        Assert.Equal(SidecarConvergencePath.EmptyDelta, detail.Path);
        Assert.Equal(
            kind == StoreSidecarKind.Content
                ? new DeterministicCounters(0, 0, 0, 0, 4, 0, 0, 0)
                : new DeterministicCounters(0, 0, 0, 0, 0, 0, 0, 0),
            Deterministic(measurement.Complete()));
    }

    [Theory]
    [InlineData(StoreSidecarKind.Content)]
    [InlineData(StoreSidecarKind.Search)]
    public void Equivalent_sqlite_fixtures_produce_identical_deterministic_counters(StoreSidecarKind kind)
    {
        Assert.Equal(MeasureIncremental(kind), MeasureIncremental(kind));
    }

    [Fact]
    public void Repeated_real_delta_reads_count_each_inspected_row()
    {
        using StoreFixture fixture = StoreFixture.Create();
        ConfigureInitial(fixture);
        ConfigureUpdated(fixture);
        using FamilyStoreReadSession session = FamilyStoreReadSession.Open(fixture.Binding);
        var measurement = new SidecarConvergenceMeasurement();

        RevisionDeltaResult first = RevisionDeltaReader.Read(
            session, 2, session.Snapshot.ArtifactOrStoreId, measurement);
        RevisionDeltaResult second = RevisionDeltaReader.Read(
            session, 2, session.Snapshot.ArtifactOrStoreId, measurement);

        Assert.Equal(RevisionDeltaStatus.Complete, first.Status);
        Assert.Equal(first.Status, second.Status);
        Assert.Equal(first.FromRevision, second.FromRevision);
        Assert.Equal(first.ToRevision, second.ToRevision);
        Assert.Equal(first.ChangedPaths, second.ChangedPaths);
        Assert.Equal(first.DeletedPaths, second.DeletedPaths);
        Assert.Equal(new(8, 4, 4, 0, 0, 0, 0, 0), Deterministic(measurement.Complete()));
    }

    [Fact]
    public void Benchmark_fixture_emits_real_content_and_search_measurements()
    {
        string? output = Environment.GetEnvironmentVariable("MILLER_SIDECAR_BENCH_OUTPUT");
        if (string.IsNullOrWhiteSpace(output))
            return;

        var result = new Dictionary<string, BenchmarkPair>(StringComparer.Ordinal);
        foreach (StoreSidecarKind kind in new[] { StoreSidecarKind.Content, StoreSidecarKind.Search })
            result[kind.ToString().ToLowerInvariant()] = MeasureBenchmark(kind);
        File.WriteAllText(output, JsonSerializer.Serialize(result));
    }

    private static SidecarConvergenceDetail Converge(
        StoreSidecarKind kind,
        string storeRoot,
        IWorkspaceReadSession session,
        SidecarConvergenceMeasurement measurement)
    {
        var cursor = new PassthroughCursor();
        return kind switch
        {
            StoreSidecarKind.Content => new ContentCorpusSidecar()
                .EnsureStoreCurrentWithCursor(storeRoot, session, cursor, measurement),
            StoreSidecarKind.Search => new SymbolSearchSidecar(true, RegionIndexOptions.Disabled)
                .EnsureStoreCurrentWithCursor(storeRoot, session, cursor, measurement),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    private static DeterministicCounters MeasureIncremental(StoreSidecarKind kind)
    {
        using StoreFixture fixture = StoreFixture.Create();
        ConfigureInitial(fixture);
        string path = StoreSidecarCatalog.PathFor(fixture.Binding.StoreRoot, kind, fixture.Binding.ViewId);
        using (FamilyStoreReadSession initial = FamilyStoreReadSession.Open(fixture.Binding))
            WriteFull(kind, path, initial, measurement: null);
        ConfigureUpdated(fixture);
        using FamilyStoreReadSession updated = FamilyStoreReadSession.Open(fixture.Binding);
        var measurement = new SidecarConvergenceMeasurement();
        Assert.Equal(
            SidecarConvergencePath.Incremental,
            Converge(kind, fixture.Binding.StoreRoot, updated, measurement).Path);
        return Deterministic(measurement.Complete());
    }

    private static BenchmarkPair MeasureBenchmark(StoreSidecarKind kind)
    {
        using StoreFixture fixture = StoreFixture.Create();
        ConfigureInitial(fixture);
        string incrementalPath = StoreSidecarCatalog.PathFor(fixture.Binding.StoreRoot, kind, fixture.Binding.ViewId);
        using (FamilyStoreReadSession initial = FamilyStoreReadSession.Open(fixture.Binding))
            WriteFull(kind, incrementalPath, initial, measurement: null);
        string fullPath = Path.Combine(fixture.Root, "benchmark-full-" + kind + ".db");
        ConfigureUpdated(fixture);
        using FamilyStoreReadSession updated = FamilyStoreReadSession.Open(fixture.Binding);
        var incremental = new SidecarConvergenceMeasurement();
        Assert.Equal(
            SidecarConvergencePath.Incremental,
            Converge(kind, fixture.Binding.StoreRoot, updated, incremental).Path);
        SidecarConvergenceCounters incrementalCounters = incremental.Complete();
        var full = new SidecarConvergenceMeasurement();
        WriteFull(kind, fullPath, updated, full);
        SidecarConvergenceCounters fullCounters = full.Complete();
        Assert.Equal(CanonicalRows(kind, fullPath), CanonicalRows(kind, incrementalPath));
        if (kind == StoreSidecarKind.Search)
        {
            AssertFtsMappings(fullPath);
            AssertFtsMappings(incrementalPath);
        }
        return new(ToBenchmark(incrementalCounters), ToBenchmark(fullCounters));
    }

    private static void AssertFtsMappings(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        foreach (string table in new[] { "symbols_fts", "symbols_trigram" })
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT COUNT(*)
                FROM {table} AS fts
                LEFT JOIN search_symbols AS metadata
                  ON metadata.symbol_id=fts.symbol_id
                WHERE metadata.symbol_id IS NULL;
                """;
            Assert.Equal(0L, Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
            command.CommandText = $"SELECT COUNT(*) FROM {table};";
            long ftsCount = Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            command.CommandText = "SELECT COUNT(*) FROM search_symbols;";
            Assert.Equal(Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture), ftsCount);
        }
        using SqliteCommand uniqueDocIds = connection.CreateCommand();
        uniqueDocIds.CommandText = "SELECT COUNT(*)-COUNT(DISTINCT doc_id) FROM search_symbols;";
        Assert.Equal(0L, Convert.ToInt64(uniqueDocIds.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void WriteFull(
        StoreSidecarKind kind,
        string path,
        IWorkspaceReadSession session,
        SidecarConvergenceMeasurement? measurement)
    {
        if (kind == StoreSidecarKind.Content)
            ContentCorpusWriter.WriteStoreView(path, session, writeLockTimeout: null, measurement);
        else
            SearchIndexWriter.WriteStoreView(path, session, RegionIndexOptions.Disabled, measurement);
    }

    private static IReadOnlyList<string> CanonicalRows(StoreSidecarKind kind, string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        string[] queries = kind == StoreSidecarKind.Content
            ?
            [
                "SELECT source_id,content_kind,ifnull(workspace_id,''),ifnull(workspace_revision,-1),ifnull(path,''),ifnull(url,''),display_path,language,content_hash,source_bytes,line_count,is_test,status FROM content_sources ORDER BY source_id",
                "SELECT chunk_id,source_id,content_kind,ifnull(path,''),ifnull(url,''),display_path,language,line_start,line_end,byte_start,byte_end,raw_text,doc_len,is_test,source_bytes,ifnull(containing_symbol_id,''),ifnull(containing_symbol_name,'') FROM content_chunks ORDER BY chunk_id",
                "SELECT source_id,symbol_id,symbol_name,path,start_line,end_line FROM content_symbol_spans ORDER BY source_id,symbol_id",
            ]
            :
            [
                "SELECT symbol_id,name,ifnull(signature,''),kind,language,path,start_line,end_line,ifnull(parent_symbol_id,''),is_test,test_container,test_lifecycle,test_evidence_status,ifnull(test_evidence_reason,''),doc_len FROM search_symbols ORDER BY symbol_id",
                "SELECT symbol_id,body FROM symbols_fts ORDER BY symbol_id",
                "SELECT symbol_id,name_collapsed,qual_collapsed FROM symbols_trigram ORDER BY symbol_id",
            ];
        var rows = new List<string>();
        foreach (string query in queries)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = query;
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(string.Join('\u001f', Enumerable.Range(0, reader.FieldCount)
                    .Select(index => Convert.ToString(reader.GetValue(index), System.Globalization.CultureInfo.InvariantCulture))));
            }
        }
        return rows;
    }

    private static void ConfigureInitial(StoreFixture fixture)
    {
        Write(fixture, "same.cs", "class SameOld {}\n");
        Write(fixture, "delete.cs", "class DeleteMe {}\n");
        Write(fixture, "alias-a.cs", "class Alias {}\n");
        Write(fixture, "alias-z.cs", "class Alias {}\n");
        Execute(
            fixture,
            """
            UPDATE file_versions SET content_hash=$same_hash,content_bytes=$same_bytes WHERE version_id=2;
            UPDATE manifest_entries SET observed_content_hash=$same_hash WHERE view_id='view-a' AND generation=2 AND path='same.cs';
            INSERT INTO file_versions VALUES
              (3,'delete.cs',$delete_hash,1,'csharp',$delete_bytes,1,NULL,1,2,3),
              (4,'alias-a.cs',$alias_a_hash,1,'csharp',$alias_a_bytes,1,NULL,1,2,3),
              (5,'alias-z.cs',$alias_z_hash,1,'csharp',$alias_z_bytes,1,NULL,1,2,3);
            INSERT INTO manifest_entries VALUES
              ('view-a',2,'delete.cs','csharp',3,'indexed',$delete_hash,'2026-08-09T00:00:00Z',NULL,NULL),
              ('view-a',2,'alias-a.cs','csharp',4,'indexed',$alias_a_hash,'2026-08-09T00:00:00Z',NULL,NULL),
              ('view-a',2,'alias-z.cs','csharp',5,'indexed',$alias_z_hash,'2026-08-09T00:00:00Z',NULL,NULL);
            INSERT INTO symbols VALUES
              (3,'delete-symbol','delete.cs','csharp','DeleteMe','class',NULL,NULL,NULL,NULL,1,1,1,2,0,1,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,1.0,NULL,0,0,0,NULL),
              (4,'alias-symbol','alias-a.cs','csharp','Alias','class',NULL,NULL,NULL,NULL,1,1,1,2,0,1,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,1.0,NULL,0,0,0,NULL),
              (5,'alias-symbol','alias-z.cs','csharp','Alias','class',NULL,NULL,NULL,NULL,1,1,1,2,0,1,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,1.0,NULL,0,0,0,NULL);
            """,
            ("$same_hash", Hash(fixture, "same.cs")),
            ("$same_bytes", Bytes(fixture, "same.cs")),
            ("$delete_hash", Hash(fixture, "delete.cs")),
            ("$delete_bytes", Bytes(fixture, "delete.cs")),
            ("$alias_a_hash", Hash(fixture, "alias-a.cs")),
            ("$alias_a_bytes", Bytes(fixture, "alias-a.cs")),
            ("$alias_z_hash", Hash(fixture, "alias-z.cs")),
            ("$alias_z_bytes", Bytes(fixture, "alias-z.cs")));
    }

    private static void ConfigureUpdated(StoreFixture fixture)
    {
        Write(fixture, "same.cs", "class SameNew { int Value; }\n");
        Write(fixture, "added.cs", "class Added {}\n");
        Execute(
            fixture,
            """
            INSERT INTO file_versions VALUES
              (6,'same.cs',$same_hash,1,'csharp',$same_bytes,1,NULL,1,2,3),
              (7,'added.cs',$added_hash,1,'csharp',$added_bytes,1,NULL,1,2,3);
            INSERT INTO manifests VALUES ('view-a',3,'manifest-updated','request-updated','2026-08-10T00:00:00Z');
            INSERT INTO manifest_entries VALUES
              ('view-a',3,'same.cs','csharp',6,'indexed',$same_hash,'2026-08-10T00:00:00Z',NULL,NULL),
              ('view-a',3,'added.cs','csharp',7,'indexed',$added_hash,'2026-08-10T00:00:00Z',NULL,NULL),
              ('view-a',3,'alias-z.cs','csharp',5,'indexed',$alias_hash,'2026-08-10T00:00:00Z',NULL,NULL);
            INSERT INTO symbols VALUES
              (6,'symbol','same.cs','csharp','SameNew','class',NULL,NULL,NULL,NULL,1,1,1,2,0,1,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,1.0,NULL,0,0,0,NULL),
              (7,'added-symbol','added.cs','csharp','Added','class',NULL,NULL,NULL,NULL,1,1,1,2,0,1,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,1.0,NULL,0,0,0,NULL);
            UPDATE views SET current_generation=3,updated_at='2026-08-10T00:00:00Z' WHERE view_id='view-a';
            INSERT INTO store_log VALUES (3,'request-updated','manifest_flipped','view-a',3,NULL,NULL,1,'{}','2026-08-10T00:00:01Z');
            """,
            ("$same_hash", Hash(fixture, "same.cs")),
            ("$same_bytes", Bytes(fixture, "same.cs")),
            ("$added_hash", Hash(fixture, "added.cs")),
            ("$added_bytes", Bytes(fixture, "added.cs")),
            ("$alias_hash", Hash(fixture, "alias-z.cs")));
    }

    private static void Write(StoreFixture fixture, string path, string content) =>
        File.WriteAllText(Path.Combine(fixture.Binding.WorkspaceRoot, path), content, Encoding.UTF8);

    private static string Hash(StoreFixture fixture, string path) =>
        "blake3:" + ContentHasher.Blake3Hex(File.ReadAllBytes(Path.Combine(fixture.Binding.WorkspaceRoot, path)));

    private static long Bytes(StoreFixture fixture, string path) =>
        new FileInfo(Path.Combine(fixture.Binding.WorkspaceRoot, path)).Length;

    private static void Execute(StoreFixture fixture, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(fixture.Binding.StoreRoot, "gen-001", "store.db"),
            Pooling = false,
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object value) in parameters)
            command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }

    private static DeterministicCounters Deterministic(SidecarConvergenceCounters counters) => new(
        counters.DeltaRowsRead,
        counters.ChangedPaths,
        counters.DeletedPaths,
        counters.RowsInserted,
        counters.RowsUpdated,
        counters.RowsDeleted,
        counters.FullFiles,
        counters.FullDocuments);

    private static BenchmarkCounters ToBenchmark(SidecarConvergenceCounters counters) => new(
        counters.DeltaRowsRead,
        counters.ChangedPaths,
        counters.DeletedPaths,
        counters.RowsInserted,
        counters.RowsUpdated,
        counters.RowsDeleted,
        counters.FullFiles,
        counters.FullDocuments,
        counters.Elapsed.TotalMilliseconds);

    private sealed class PassthroughCursor : IStoreSidecarCursorSession
    {
        public bool TryProtectBaseline(StoreSidecarStamp baseline) => true;

        public void PrepareTarget(long sequence)
        {
        }

        public StoreSidecarCursorCompletion CompleteCommitted() => new(true, false, null);
    }

    private readonly record struct DeterministicCounters(
        int DeltaRowsRead,
        int ChangedPaths,
        int DeletedPaths,
        int RowsInserted,
        int RowsUpdated,
        int RowsDeleted,
        int FullFiles,
        int FullDocuments);

    private sealed record BenchmarkPair(BenchmarkCounters Incremental, BenchmarkCounters Full);

    private sealed record BenchmarkCounters(
        int DeltaRowsRead,
        int ChangedPaths,
        int DeletedPaths,
        int RowsInserted,
        int RowsUpdated,
        int RowsDeleted,
        int FullFiles,
        int FullDocuments,
        double ElapsedMilliseconds);
}
