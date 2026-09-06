using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Indexing.Store;
using Miller.Tests.Support;
using Xunit;

namespace Miller.Tests.Server;

public sealed class StoreSidecarCursorIntegrationTests
{
    [Fact]
    public void Cursor_identity_is_length_prefixed_stable_and_bounded()
    {
        string cursorId = StoreSidecarCursorIdentity.CursorId(
            "family-a",
            "family-a:gen-1",
            "view-a",
            StoreSidecarKind.Content,
            "gen-001");

        Assert.Equal(
            "miller-sc-v1:51A93B271D54D4B18B5700440E3C35286184CFEE7D64B7A3E103939D79A2A1C6",
            cursorId);
        Assert.True(cursorId.Length <= 128);
        Assert.NotEqual(
            StoreSidecarCursorIdentity.CursorId("ab", "c", "view-a", StoreSidecarKind.Content, "gen-001"),
            StoreSidecarCursorIdentity.CursorId("a", "bc", "view-a", StoreSidecarKind.Content, "gen-001"));
    }

    [Fact]
    public void Baseline_identity_is_durable_before_advance_and_retries_after_restart()
    {
        using var root = new TempDirectory();
        WorkspaceReadSnapshot snapshot = Snapshot();
        var calls = new List<(string ConsumerId, long Sequence)>();
        StoreSidecarCursorAdvance lost = (cursor, sequence) =>
        {
            StoreSidecarCursorState state = new StoreSidecarCursorJournal(
                root.Path,
                cursor.FamilyId,
                cursor.ViewId).Read();
            StoreSidecarCursorEntry pending = Assert.Single(state.Entries);
            Assert.Equal(cursor.ConsumerId, pending.ConsumerId);
            Assert.Equal(sequence, pending.DesiredSequence);
            calls.Add((cursor.ConsumerId, sequence));
            return new(false, false, null, null, null, "reply lost");
        };
        var first = new StoreSidecarCursorSession(
            root.Path,
            snapshot,
            StoreSidecarKind.Content,
            lost,
            static _ => throw new InvalidOperationException());

        Assert.False(first.TryProtectBaseline(StoreSidecarStamp.FromSnapshot(StoreSidecarKind.Content, snapshot) with
        {
            StoreLogSequence = 7,
        }));

        StoreSidecarCursorKey expected = StoreSidecarCursorIdentity.Create(snapshot, StoreSidecarKind.Content);
        var restarted = new StoreSidecarCursorSession(
            root.Path,
            snapshot,
            StoreSidecarKind.Content,
            (cursor, sequence) =>
            {
                calls.Add((cursor.ConsumerId, sequence));
                return AdvanceSuccess(cursor, sequence);
            },
            static _ => throw new InvalidOperationException());

        Assert.True(restarted.TryProtectBaseline(StoreSidecarStamp.FromSnapshot(StoreSidecarKind.Content, snapshot) with
        {
            StoreLogSequence = 7,
        }));
        Assert.All(calls, call => Assert.Equal((expected.ConsumerId, 7), call));
        StoreSidecarCursorEntry acknowledged = Assert.Single(
            new StoreSidecarCursorJournal(root.Path, expected.FamilyId, expected.ViewId).Read().Entries);
        Assert.Equal(7, acknowledged.AcknowledgedSequence);
    }

    [Fact]
    public void Pending_target_survives_publication_boundary_and_advances_only_after_matching_stamp()
    {
        using var root = new TempDirectory();
        WorkspaceReadSnapshot snapshot = Snapshot(storeLogSequence: 11);
        int advances = 0;
        int writes = 0;
        StoreSidecarCursorAdvance advance = (cursor, sequence) =>
        {
            advances++;
            return AdvanceSuccess(cursor, sequence);
        };
        var beforePublish = new StoreSidecarCursorSession(
            root.Path,
            snapshot,
            StoreSidecarKind.Content,
            advance,
            static _ => throw new InvalidOperationException(),
            () => writes++);

        beforePublish.PrepareTarget(11);
        StoreSidecarCursorCompletion before = beforePublish.CompleteCommitted();

        Assert.False(before.Succeeded);
        Assert.Equal(0, advances);
        Assert.Equal(1, writes);
        Stamp(root.Path, snapshot, StoreSidecarKind.Content);

        var restarted = new StoreSidecarCursorSession(
            root.Path,
            snapshot,
            StoreSidecarKind.Content,
            advance,
            static _ => throw new InvalidOperationException());
        StoreSidecarCursorCompletion after = restarted.CompleteCommitted();

        Assert.True(after.Succeeded);
        Assert.True(after.DidWork);
        Assert.Equal(1, advances);
    }

    [Fact]
    public void Failed_final_advance_retries_from_committed_stamp_in_fresh_session()
    {
        using var root = new TempDirectory();
        WorkspaceReadSnapshot snapshot = Snapshot(storeLogSequence: 12);
        var first = new StoreSidecarCursorSession(
            root.Path,
            snapshot,
            StoreSidecarKind.Content,
            static (_, _) => new(false, false, null, null, null, "reply lost"),
            static _ => throw new InvalidOperationException());
        first.PrepareTarget(12);
        Stamp(root.Path, snapshot, StoreSidecarKind.Content);

        StoreSidecarCursorCompletion failed = first.CompleteCommitted();

        Assert.False(failed.Succeeded);
        StoreSidecarCursorKey expected = StoreSidecarCursorIdentity.Create(snapshot, StoreSidecarKind.Content);
        StoreSidecarCursorEntry pending = Assert.Single(
            new StoreSidecarCursorJournal(root.Path, expected.FamilyId, expected.ViewId).Read().Entries);
        Assert.Null(pending.AcknowledgedSequence);
        int retries = 0;
        var restarted = new StoreSidecarCursorSession(
            root.Path,
            snapshot,
            StoreSidecarKind.Content,
            (key, sequence) => { retries++; return AdvanceSuccess(key, sequence); },
            static _ => throw new InvalidOperationException());

        StoreSidecarCursorCompletion recovered = restarted.CompleteCommitted();

        Assert.True(recovered.Succeeded);
        Assert.True(recovered.DidWork);
        Assert.Equal(1, retries);
    }

    [Fact]
    public void Current_without_journal_and_acknowledged_current_do_zero_mutation()
    {
        using var root = new TempDirectory();
        WorkspaceReadSnapshot snapshot = Snapshot(storeLogSequence: 13);
        Stamp(root.Path, snapshot, StoreSidecarKind.Content);
        int calls = 0;
        int writes = 0;
        StoreSidecarCursorAdvance advance = (cursor, sequence) =>
        {
            calls++;
            return AdvanceSuccess(cursor, sequence);
        };
        var absent = new StoreSidecarCursorSession(
            root.Path,
            snapshot,
            StoreSidecarKind.Content,
            advance,
            static _ => throw new InvalidOperationException(),
            () => writes++);

        StoreSidecarCursorCompletion noJournal = absent.CompleteCommitted();

        Assert.True(noJournal.Succeeded);
        Assert.False(noJournal.DidWork);
        Assert.Equal(0, calls);
        Assert.Equal(0, writes);
        Assert.False(new StoreSidecarCursorJournal(root.Path, snapshot.ArtifactOrStoreId, snapshot.ViewId).Exists);

        absent.PrepareTarget(13);
        Assert.True(absent.CompleteCommitted().Succeeded);
        calls = 0;
        writes = 0;
        var acknowledged = new StoreSidecarCursorSession(
            root.Path,
            snapshot,
            StoreSidecarKind.Content,
            advance,
            static _ => throw new InvalidOperationException(),
            () => writes++);

        StoreSidecarCursorCompletion current = acknowledged.CompleteCommitted();

        Assert.True(current.Succeeded);
        Assert.False(current.DidWork);
        Assert.Equal(0, calls);
        Assert.Equal(0, writes);
    }

    [Theory]
    [InlineData(StoreSidecarKind.Content)]
    [InlineData(StoreSidecarKind.Search)]
    public void Missing_baseline_is_advanced_before_complete_delta_and_target_after_commit(StoreSidecarKind kind)
    {
        using StoreFixture fixture = StoreFixture.Create();
        using (FamilyStoreReadSession initial = FamilyStoreReadSession.Open(fixture.Binding))
            EnsureLegacy(kind, fixture.Binding.StoreRoot, initial);
        AppendAddedFileManifest(fixture);
        var calls = new List<long>();
        using FamilyStoreReadSession updated = FamilyStoreReadSession.Open(fixture.Binding);
        var cursor = new StoreSidecarCursorSession(
            fixture.Binding.StoreRoot,
            updated.Snapshot,
            kind,
            (key, sequence) =>
            {
                StoreSidecarStamp stamp = StoreSidecarCatalog.TryRead(StoreSidecarCatalog.PathFor(
                    fixture.Binding.StoreRoot, kind, fixture.Binding.ViewId))!;
                Assert.Equal(sequence, stamp.StoreLogSequence);
                calls.Add(sequence);
                return AdvanceSuccess(key, sequence);
            },
            static _ => throw new InvalidOperationException());

        SidecarConvergenceDetail detail = EnsureWithCursor(kind, fixture.Binding.StoreRoot, updated, cursor);
        StoreSidecarCursorCompletion completion = cursor.CompleteCommitted();

        Assert.Equal(new(SidecarConvergencePath.Incremental, SidecarConvergenceReason.None, true), detail);
        Assert.True(completion.Succeeded);
        Assert.Equal([2, 3], calls);
    }

    [Theory]
    [InlineData(StoreSidecarKind.Content)]
    [InlineData(StoreSidecarKind.Search)]
    public void Empty_delta_is_baselined_before_fast_forward(StoreSidecarKind kind)
    {
        using StoreFixture fixture = StoreFixture.Create();
        using (FamilyStoreReadSession initial = FamilyStoreReadSession.Open(fixture.Binding))
            EnsureLegacy(kind, fixture.Binding.StoreRoot, initial);
        AppendReusedManifestImport(fixture);
        var calls = new List<long>();
        using FamilyStoreReadSession updated = FamilyStoreReadSession.Open(fixture.Binding);
        var cursor = Cursor(fixture, updated.Snapshot, kind, calls);

        SidecarConvergenceDetail detail = EnsureWithCursor(kind, fixture.Binding.StoreRoot, updated, cursor);
        StoreSidecarCursorCompletion completion = cursor.CompleteCommitted();

        Assert.Equal(new(SidecarConvergencePath.EmptyDelta, SidecarConvergenceReason.None, true), detail);
        Assert.True(completion.Succeeded);
        Assert.Equal([2, 5], calls);
    }

    [Theory]
    [InlineData(StoreSidecarKind.Content)]
    [InlineData(StoreSidecarKind.Search)]
    public void Trimmed_delta_forces_full_after_baseline_protection(StoreSidecarKind kind)
    {
        using StoreFixture fixture = StoreFixture.Create();
        using (FamilyStoreReadSession initial = FamilyStoreReadSession.Open(fixture.Binding))
            EnsureLegacy(kind, fixture.Binding.StoreRoot, initial);
        AppendReusedManifestImport(fixture);
        ExecuteStore(fixture, "DELETE FROM store_log WHERE sequence <= 2;");
        var calls = new List<long>();
        using FamilyStoreReadSession updated = FamilyStoreReadSession.Open(fixture.Binding);
        var cursor = Cursor(fixture, updated.Snapshot, kind, calls);

        SidecarConvergenceDetail detail = EnsureWithCursor(kind, fixture.Binding.StoreRoot, updated, cursor);
        StoreSidecarCursorCompletion completion = cursor.CompleteCommitted();

        Assert.Equal(new(SidecarConvergencePath.Full, SidecarConvergenceReason.DeltaIncomplete, true), detail);
        Assert.True(completion.Succeeded);
        Assert.Equal([2, 5], calls);
    }

    [Theory]
    [InlineData(StoreSidecarKind.Content)]
    [InlineData(StoreSidecarKind.Search)]
    public void Mismatched_baseline_report_forces_full_and_never_advances_target_before_commit(StoreSidecarKind kind)
    {
        using StoreFixture fixture = StoreFixture.Create();
        using (FamilyStoreReadSession initial = FamilyStoreReadSession.Open(fixture.Binding))
            EnsureLegacy(kind, fixture.Binding.StoreRoot, initial);
        AppendAddedFileManifest(fixture);
        var calls = new List<long>();
        using FamilyStoreReadSession updated = FamilyStoreReadSession.Open(fixture.Binding);
        var cursor = new StoreSidecarCursorSession(
            fixture.Binding.StoreRoot,
            updated.Snapshot,
            kind,
            (key, sequence) =>
            {
                calls.Add(sequence);
                if (sequence == 2)
                    return new(true, true, "gen-wrong", key.ConsumerId, sequence, null);
                Assert.True(StoreSidecarCatalog.IsCurrent(
                    StoreSidecarCatalog.PathFor(fixture.Binding.StoreRoot, key.Kind, key.ViewId),
                    StoreSidecarStamp.FromSnapshot(key.Kind, updated.Snapshot)));
                return AdvanceSuccess(key, sequence);
            },
            static _ => throw new InvalidOperationException());

        SidecarConvergenceDetail detail = EnsureWithCursor(kind, fixture.Binding.StoreRoot, updated, cursor);
        StoreSidecarCursorCompletion completion = cursor.CompleteCommitted();

        Assert.Equal(new(SidecarConvergencePath.Full, SidecarConvergenceReason.DeltaIncomplete, true), detail);
        Assert.True(completion.Succeeded);
        Assert.Equal([2, 2, 2, 3], calls);
    }

    [Fact]
    public void Generation_switch_acknowledges_new_content_before_releasing_old_content_only()
    {
        using var root = new TempDirectory();
        WorkspaceReadSnapshot oldSnapshot = Snapshot(storeLogSequence: 4);
        var released = new List<StoreSidecarCursorKey>();
        StoreSidecarCursorRelease release = key =>
        {
            released.Add(key);
            return new(true, true, key.GenerationName, key.ConsumerId, null, null);
        };
        foreach (StoreSidecarKind kind in new[] { StoreSidecarKind.Content, StoreSidecarKind.Search })
        {
            Stamp(root.Path, oldSnapshot, kind);
            var old = new StoreSidecarCursorSession(
                root.Path, oldSnapshot, kind, AdvanceSuccess, release);
            old.PrepareTarget(4);
            Assert.True(old.CompleteCommitted().Succeeded);
        }

        WorkspaceReadSnapshot next = Snapshot(
            storeLogSequence: 8,
            storeInstanceId: "11111111-1111-4111-8111-111111111111:gen-002",
            generationName: "gen-002");
        var order = new List<string>();
        var current = new StoreSidecarCursorSession(
            root.Path,
            next,
            StoreSidecarKind.Content,
            (key, sequence) =>
            {
                order.Add("advance:" + key.GenerationName);
                return AdvanceSuccess(key, sequence);
            },
            key =>
            {
                order.Add("release:" + key.Kind + ":" + key.GenerationName);
                return release(key);
            });
        current.PrepareTarget(8);
        Stamp(root.Path, next, StoreSidecarKind.Content);

        StoreSidecarCursorCompletion completion = current.CompleteCommitted();

        Assert.True(completion.Succeeded);
        Assert.Equal(["advance:gen-002", "release:Content:gen-001"], order);
        StoreSidecarCursorState state = new StoreSidecarCursorJournal(
            root.Path, next.ArtifactOrStoreId, next.ViewId).Read();
        Assert.Contains(state.Entries, entry => entry.Kind == StoreSidecarKind.Content && entry.GenerationName == "gen-002");
        Assert.Contains(state.Entries, entry => entry.Kind == StoreSidecarKind.Search && entry.GenerationName == "gen-001");
        Assert.DoesNotContain(state.Entries, entry => entry.Kind == StoreSidecarKind.Content && entry.GenerationName == "gen-001");
    }

    [Fact]
    public void Generation_release_failure_retries_exact_old_kind_without_rebuild_or_new_advance()
    {
        using var root = new TempDirectory();
        WorkspaceReadSnapshot oldSnapshot = Snapshot(storeLogSequence: 4);
        foreach (StoreSidecarKind kind in new[] { StoreSidecarKind.Content, StoreSidecarKind.Search })
        {
            Stamp(root.Path, oldSnapshot, kind);
            var old = new StoreSidecarCursorSession(
                root.Path,
                oldSnapshot,
                kind,
                AdvanceSuccess,
                static _ => throw new InvalidOperationException());
            old.PrepareTarget(4);
            Assert.True(old.CompleteCommitted().Succeeded);
        }
        WorkspaceReadSnapshot next = Snapshot(
            storeLogSequence: 8,
            storeInstanceId: "11111111-1111-4111-8111-111111111111:gen-002",
            generationName: "gen-002");
        Stamp(root.Path, next, StoreSidecarKind.Content);
        var first = new StoreSidecarCursorSession(
            root.Path,
            next,
            StoreSidecarKind.Content,
            AdvanceSuccess,
            key => new(false, false, key.GenerationName, key.ConsumerId, null, "busy"));
        first.PrepareTarget(8);

        StoreSidecarCursorCompletion failed = first.CompleteCommitted();

        Assert.False(failed.Succeeded);
        StoreSidecarCursorKey oldContent = StoreSidecarCursorIdentity.Create(oldSnapshot, StoreSidecarKind.Content);
        StoreSidecarCursorKey oldSearch = StoreSidecarCursorIdentity.Create(oldSnapshot, StoreSidecarKind.Search);
        StoreSidecarCursorState pending = new StoreSidecarCursorJournal(
            root.Path, next.ArtifactOrStoreId, next.ViewId).Read();
        Assert.Contains(pending.Entries, entry => entry.ConsumerId == oldContent.ConsumerId);
        Assert.Contains(pending.Entries, entry => entry.ConsumerId == oldSearch.ConsumerId);
        int advances = 0;
        var released = new List<string>();
        var restarted = new StoreSidecarCursorSession(
            root.Path,
            next,
            StoreSidecarKind.Content,
            (key, sequence) => { advances++; return AdvanceSuccess(key, sequence); },
            key =>
            {
                released.Add(key.ConsumerId);
                return new(true, true, key.GenerationName, key.ConsumerId, null, null);
            });

        StoreSidecarCursorCompletion recovered = restarted.CompleteCommitted();

        Assert.True(recovered.Succeeded);
        Assert.Equal(0, advances);
        Assert.Equal([oldContent.ConsumerId], released);
        StoreSidecarCursorState final = new StoreSidecarCursorJournal(
            root.Path, next.ArtifactOrStoreId, next.ViewId).Read();
        Assert.DoesNotContain(final.Entries, entry => entry.ConsumerId == oldContent.ConsumerId);
        Assert.Contains(final.Entries, entry => entry.ConsumerId == oldSearch.ConsumerId);
    }

    [Fact]
    public void Corrupt_journal_fails_closed_without_cursor_mutation_or_file_loss()
    {
        using var root = new TempDirectory();
        WorkspaceReadSnapshot snapshot = Snapshot();
        Stamp(root.Path, snapshot, StoreSidecarKind.Content);
        string path = StoreSidecarCursorJournal.PathFor(root.Path, snapshot.ViewId);
        File.WriteAllText(path, "{\"family_id\":\"wrong\"}");
        int calls = 0;
        var cursor = new StoreSidecarCursorSession(
            root.Path,
            snapshot,
            StoreSidecarKind.Content,
            (key, sequence) => { calls++; return AdvanceSuccess(key, sequence); },
            key => { calls++; return new(true, true, key.GenerationName, key.ConsumerId, null, null); });

        StoreSidecarCursorCompletion completion = cursor.CompleteCommitted();

        Assert.False(completion.Succeeded);
        Assert.Equal(0, calls);
        Assert.Equal("{\"family_id\":\"wrong\"}", File.ReadAllText(path));
    }

    [Fact]
    public void Watermark_ahead_of_restored_sidecar_cannot_protect_older_baseline()
    {
        using var root = new TempDirectory();
        WorkspaceReadSnapshot snapshot = Snapshot(storeLogSequence: 10);
        Stamp(root.Path, snapshot, StoreSidecarKind.Content);
        var first = new StoreSidecarCursorSession(
            root.Path,
            snapshot,
            StoreSidecarKind.Content,
            AdvanceSuccess,
            static _ => throw new InvalidOperationException());
        first.PrepareTarget(10);
        Assert.True(first.CompleteCommitted().Succeeded);
        int advances = 0;
        var restored = new StoreSidecarCursorSession(
            root.Path,
            snapshot,
            StoreSidecarKind.Content,
            (key, sequence) => { advances++; return AdvanceSuccess(key, sequence); },
            static _ => throw new InvalidOperationException());
        StoreSidecarStamp older = StoreSidecarStamp.FromSnapshot(StoreSidecarKind.Content, snapshot) with
        {
            StoreLogSequence = 7,
        };

        Assert.False(restored.TryProtectBaseline(older));
        Assert.Equal(0, advances);
    }

    private static StoreConsumerCursorOutcome AdvanceSuccess(StoreSidecarCursorKey cursor, long sequence) =>
        new(true, true, cursor.GenerationName, cursor.ConsumerId, sequence, null);

    private static StoreSidecarCursorSession Cursor(
        StoreFixture fixture,
        WorkspaceReadSnapshot snapshot,
        StoreSidecarKind kind,
        List<long> calls) =>
        new(
            fixture.Binding.StoreRoot,
            snapshot,
            kind,
            (key, sequence) =>
            {
                calls.Add(sequence);
                return AdvanceSuccess(key, sequence);
            },
            static _ => throw new InvalidOperationException());

    private static SidecarConvergenceDetail EnsureLegacy(
        StoreSidecarKind kind,
        string storeRoot,
        IWorkspaceReadSession session) =>
        kind switch
        {
            StoreSidecarKind.Content => new ContentCorpusSidecar().EnsureStoreCurrentDetailed(storeRoot, session),
            StoreSidecarKind.Search => new SymbolSearchSidecar(true, RegionIndexOptions.Disabled)
                .EnsureStoreCurrentDetailed(storeRoot, session),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private static SidecarConvergenceDetail EnsureWithCursor(
        StoreSidecarKind kind,
        string storeRoot,
        IWorkspaceReadSession session,
        IStoreSidecarCursorSession cursor) =>
        kind switch
        {
            StoreSidecarKind.Content => new ContentCorpusSidecar().EnsureStoreCurrentWithCursor(storeRoot, session, cursor),
            StoreSidecarKind.Search => new SymbolSearchSidecar(true, RegionIndexOptions.Disabled)
                .EnsureStoreCurrentWithCursor(storeRoot, session, cursor),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private static void AppendReusedManifestImport(StoreFixture fixture) =>
        ExecuteStore(
            fixture,
            """
            INSERT INTO store_log VALUES
              (3,'request-reuse','store_import_l3_chunk','view-a',2,2,3,0,'{}','2026-08-09T00:00:03Z'),
              (4,'request-reuse','store_import_completed','view-a',2,NULL,3,1,
               '{"manifest":{"disposition":"reused"}}','2026-08-09T00:00:04Z'),
              (5,'request-reuse','store_resolve_completed','view-a',2,NULL,3,1,
               '{}','2026-08-09T00:00:05Z');
            """);

    private static void AppendAddedFileManifest(StoreFixture fixture) =>
        ExecuteStore(
            fixture,
            """
            INSERT INTO file_versions VALUES
              (3,'added.cs','blake3:added',1,'csharp',12,1,NULL,1,2,3);
            INSERT INTO manifests VALUES
              ('view-a',3,'manifest-added','request-added','2026-08-09T00:00:02Z');
            INSERT INTO manifest_entries VALUES
              ('view-a',3,'same.cs','csharp',2,'indexed','blake3:visible','2026-08-09T00:00:02Z',NULL,NULL),
              ('view-a',3,'added.cs','csharp',3,'indexed','blake3:added','2026-08-09T00:00:02Z',NULL,NULL);
            INSERT INTO symbols VALUES
              (3,'added-symbol','added.cs','csharp','Added','class',NULL,NULL,NULL,NULL,1,1,1,2,0,1,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,1.0,NULL,0,0,0,NULL);
            UPDATE views SET current_generation=3,updated_at='2026-08-09T00:00:02Z' WHERE view_id='view-a';
            INSERT INTO store_log VALUES
              (3,'request-added','manifest_flipped','view-a',3,NULL,NULL,1,'{}','2026-08-09T00:00:02Z');
            """);

    private static void ExecuteStore(StoreFixture fixture, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(fixture.Binding.StoreRoot, "gen-001", "store.db"),
            Pooling = false,
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static WorkspaceReadSnapshot Snapshot(
        long storeLogSequence = 9,
        string storeInstanceId = "11111111-1111-4111-8111-111111111111:gen-001",
        string generationName = "gen-001") =>
        new(
            "/workspace",
            "workspace-a",
            "11111111-1111-4111-8111-111111111111",
            "view-a",
            new WorkspaceFreshnessToken(
                "11111111-1111-4111-8111-111111111111",
                2,
                "manifest-a",
                storeLogSequence,
                "resolution-a",
                StoreInstanceId: storeInstanceId,
                ViewId: "view-a",
                GenerationName: generationName,
                ManifestGeneration: 2,
                IndexLevel: "full",
                LevelStampL1: "l1-a",
                LevelStampL2: "l2-a",
                LevelStampL3: "l3-a"),
            "full",
            WorkspaceReadMode.FamilyStore,
            GenerationName: generationName,
            ManifestGeneration: 2);

    private static void Stamp(string storeRoot, WorkspaceReadSnapshot snapshot, StoreSidecarKind kind)
    {
        string databasePath = StoreSidecarCatalog.PathFor(storeRoot, kind, snapshot.ViewId);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
        {
            connection.Open();
        }
        StoreSidecarCatalog.Stamp(databasePath, StoreSidecarStamp.FromSnapshot(kind, snapshot));
    }

    private sealed class TempDirectory : IDisposable
    {
        internal TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "miller-sidecar-cursor-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
        }
    }
}
