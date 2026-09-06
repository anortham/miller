using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Indexing.Store;
using Miller.Server.Resolution;
using Miller.Server.Tools;
using Miller.Server.Workspaces;
using Xunit;

namespace Miller.Tests.Server;

[Trait("Category", "Scale")]
public sealed class StoreSidecarCursorScaleTests
{
    [Fact]
    public void Published_cursor_contract_preserves_real_delta_and_exact_release()
    {
        string binary = ScaleTestSupport.RequireJulieServer();
        using var fixture = PublishedStore.Create(binary, [("source.cs", "public sealed class First { }\n")]);
        var oldSequences = new Dictionary<StoreSidecarKind, long>();
        var oldStamps = new Dictionary<StoreSidecarKind, StoreSidecarStamp>();
        var retiredKeys = new Dictionary<StoreSidecarKind, StoreSidecarCursorKey>();
        long retainedSequence = 0;
        long recoveredSequence = 0;
        foreach (StoreSidecarKind kind in new[] { StoreSidecarKind.Content, StoreSidecarKind.Search })
        {
            using FamilyStoreReadSession initial = FamilyStoreReadSession.Open(fixture.Binding);
            oldSequences[kind] = initial.Snapshot.Freshness.StoreLogSequence!.Value;
            oldStamps[kind] = StoreSidecarStamp.FromSnapshot(kind, initial.Snapshot);
            retiredKeys[kind] = StoreSidecarCursorIdentity.Create(initial.Snapshot, kind);
            var cursor = RealCursor(binary, fixture.Binding.StoreRoot, initial.Snapshot, kind, (_, sequence) =>
            {
                Assert.True(StoreSidecarCatalog.IsCurrent(
                    StoreSidecarCatalog.PathFor(fixture.Binding.StoreRoot, kind, fixture.Binding.ViewId),
                    StoreSidecarStamp.FromSnapshot(kind, initial.Snapshot)));
                Assert.Equal(oldSequences[kind], sequence);
            });
            SidecarConvergenceDetail detail = Converge(kind, fixture.Binding.StoreRoot, initial, cursor);
            Assert.Equal(SidecarConvergencePath.Full, detail.Path);
            Assert.True(cursor.CompleteCommitted().Succeeded);
        }
        ScaleTestSupport.RunJulie(
            binary, "store", "maintain", "promote", "--store", fixture.Binding.StoreRoot,
            "--family", fixture.Binding.FamilyId.ToString("D"), "--apply", "--json");
        using (FamilyStoreReadSession promoted = FamilyStoreReadSession.Open(fixture.Binding))
        {
            Assert.NotEqual(
                retiredKeys[StoreSidecarKind.Content].GenerationName,
                StoreSidecarCursorIdentity.Create(promoted.Snapshot, StoreSidecarKind.Content).GenerationName);
            foreach (StoreSidecarKind kind in new[] { StoreSidecarKind.Content, StoreSidecarKind.Search })
            {
                var cursor = RealCursor(binary, fixture.Binding.StoreRoot, promoted.Snapshot, kind);
                Assert.Equal(
                    SidecarConvergencePath.Full,
                    Converge(kind, fixture.Binding.StoreRoot, promoted, cursor).Path);
                Assert.True(cursor.CompleteCommitted().Succeeded);
                oldSequences[kind] = promoted.Snapshot.Freshness.StoreLogSequence!.Value;
                oldStamps[kind] = StoreSidecarStamp.FromSnapshot(kind, promoted.Snapshot);
                Assert.False(CursorExists(fixture.Binding.StoreRoot, retiredKeys[kind].ConsumerId));
                if (kind == StoreSidecarKind.Content)
                    Assert.True(CursorExists(
                        fixture.Binding.StoreRoot,
                        retiredKeys[StoreSidecarKind.Search].ConsumerId));
            }
        }
        File.Delete(StoreSidecarCursorJournal.PathFor(fixture.Binding.StoreRoot, fixture.Binding.ViewId));

        File.AppendAllText(Path.Combine(fixture.Binding.WorkspaceRoot, "source.cs"), " \n");
        fixture.Update(binary, "source.cs", "cursor-update-1");
        Assert.Equal(0, ReaderRegistrationCount(fixture.Binding.StoreRoot));
        ScaleTestSupport.RunJulie(
            binary, "store", "maintain", "gc", "--store", fixture.Binding.StoreRoot,
            "--family", fixture.Binding.FamilyId.ToString("D"), "--apply", "--json");
        using (FamilyStoreReadSession updated = FamilyStoreReadSession.Open(fixture.Binding))
        {
            retainedSequence = updated.Snapshot.Freshness.StoreLogSequence!.Value;
            foreach (StoreSidecarKind kind in new[] { StoreSidecarKind.Content, StoreSidecarKind.Search })
            {
                RevisionDeltaResult retained = RevisionDeltaReader.Read(
                    updated, oldSequences[kind], updated.Snapshot.ArtifactOrStoreId);
                Assert.Equal(RevisionDeltaStatus.Complete, retained.Status);
                int advances = 0;
                var cursor = RealCursor(binary, fixture.Binding.StoreRoot, updated.Snapshot, kind, (_, sequence) =>
                {
                    advances++;
                    StoreSidecarStamp expected = sequence == oldSequences[kind]
                        ? oldStamps[kind]
                        : StoreSidecarStamp.FromSnapshot(kind, updated.Snapshot);
                    Assert.True(StoreSidecarCatalog.IsCurrent(
                        StoreSidecarCatalog.PathFor(fixture.Binding.StoreRoot, kind, fixture.Binding.ViewId),
                        expected));
                });
                SidecarConvergenceDetail detail = Converge(kind, fixture.Binding.StoreRoot, updated, cursor);
                Assert.Equal(SidecarConvergencePath.Incremental, detail.Path);
                Assert.True(cursor.CompleteCommitted().Succeeded);
                Assert.Equal(2, advances);
            }
        }

        File.AppendAllText(Path.Combine(fixture.Binding.WorkspaceRoot, "source.cs"), " \n");
        fixture.Update(binary, "source.cs", "cursor-update-2");
        using (FamilyStoreReadSession owedSnapshot = FamilyStoreReadSession.Open(fixture.Binding))
        {
            StoreSidecarCursorAdvance realAdvance = Advance(binary, fixture.Binding.StoreRoot);
            var lost = new StoreSidecarCursorSession(
                fixture.Binding.StoreRoot,
                owedSnapshot.Snapshot,
                StoreSidecarKind.Content,
                (key, sequence) =>
                {
                    StoreConsumerCursorOutcome applied = realAdvance(key, sequence);
                    Assert.True(applied.Succeeded);
                    return new(false, false, null, null, null, "reply lost");
                },
                Release(binary, fixture.Binding.StoreRoot));
            Assert.Equal(
                SidecarConvergencePath.Incremental,
                Converge(StoreSidecarKind.Content, fixture.Binding.StoreRoot, owedSnapshot, lost).Path);
            Assert.False(lost.CompleteCommitted().Succeeded);
        }

        StoreSidecarCursorKey contentKey;
        StoreSidecarCursorKey searchKey;
        using (FamilyStoreReadSession retrySnapshot = FamilyStoreReadSession.Open(fixture.Binding))
        {
            recoveredSequence = retrySnapshot.Snapshot.Freshness.StoreLogSequence!.Value;
            int rebuilds = 0;
            var retry = RealCursor(binary, fixture.Binding.StoreRoot, retrySnapshot.Snapshot, StoreSidecarKind.Content);
            SidecarConvergenceDetail current = Converge(
                StoreSidecarKind.Content,
                fixture.Binding.StoreRoot,
                retrySnapshot,
                retry,
                () => rebuilds++);
            Assert.Equal(SidecarConvergencePath.Current, current.Path);
            Assert.True(retry.CompleteCommitted().Succeeded);
            Assert.Equal(0, rebuilds);
            contentKey = StoreSidecarCursorIdentity.Create(retrySnapshot.Snapshot, StoreSidecarKind.Content);
            searchKey = StoreSidecarCursorIdentity.Create(retrySnapshot.Snapshot, StoreSidecarKind.Search);
        }
        Assert.True(StoreConsumerCursorRunner.Release(
            binary, fixture.Binding.StoreRoot, contentKey.FamilyId, contentKey.ConsumerId).Succeeded);
        Assert.False(CursorExists(fixture.Binding.StoreRoot, contentKey.ConsumerId));
        Assert.True(CursorExists(fixture.Binding.StoreRoot, searchKey.ConsumerId));
        Assert.True(StoreConsumerCursorRunner.Release(
            binary, fixture.Binding.StoreRoot, searchKey.FamilyId, searchKey.ConsumerId).Succeeded);
        StoreViewRetirementOutcome retired = StoreViewRetirementRunner.Run(
            binary,
            new(fixture.Binding.FamilyId, fixture.Binding.ViewId, fixture.Binding.StoreRoot),
            apply: true);
        Assert.Equal(StoreViewRetirementDisposition.Retired, retired.Disposition);
        string? evidencePath = Environment.GetEnvironmentVariable("MILLER_CURSOR_SCALE_EVIDENCE");
        if (!string.IsNullOrWhiteSpace(evidencePath))
        {
            File.WriteAllText(evidencePath, JsonSerializer.Serialize(new
            {
                schema_version = 1,
                extractor = new
                {
                    version = new JulieExtractRunner(binary).QueryVersion(),
                    sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(binary))),
                },
                family_id = fixture.Binding.FamilyId,
                view_id = fixture.Binding.ViewId,
                protected_baseline_sequences = oldSequences,
                retired_generation = retiredKeys[StoreSidecarKind.Content].GenerationName,
                retained_sequence = retainedSequence,
                recovered_sequence = recoveredSequence,
                consumers = new
                {
                    content = new { contentKey.GenerationName, contentKey.ConsumerId },
                    search = new { searchKey.GenerationName, searchKey.ConsumerId },
                },
                verified = new
                {
                    baseline_advance_before_delta = true,
                    reader_registrations_during_gc = 0,
                    complete_delta_after_producer_gc = true,
                    sidecar_commit_before_target_advance = true,
                    lost_reply_recovered_without_rebuild = true,
                    exact_content_release_preserved_search = true,
                    generation_rollover_released_each_obsolete_kind_after_its_new_commit = true,
                    exact_view_retirement = retired.Disposition.ToString(),
                },
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    [Fact]
    public void Published_language_inventory_serves_context_content_and_search()
    {
        string binary = ScaleTestSupport.RequireJulieServer();
        (string Language, string FileName, string Source)[] samples = LanguageSamples();
        using var catalog = JsonDocument.Parse(ScaleTestSupport.RunJulie(binary, "languages", "--json"));
        string[] supported = catalog.RootElement.GetProperty("languages").GetProperty("languages")
            .EnumerateArray().Select(item => item.GetProperty("language").GetString()!)
            .Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(supported, samples.Select(sample => sample.Language).Order(StringComparer.Ordinal).ToArray());
        using var fixture = PublishedStore.Create(
            binary,
            samples.Select(sample => (Path.Combine(sample.Language, sample.FileName), sample.Source)).ToArray());
        var initialCounters = new Dictionary<string, SidecarConvergenceCounters>();
        using (FamilyStoreReadSession initial = FamilyStoreReadSession.Open(fixture.Binding))
        {
            foreach (StoreSidecarKind kind in new[] { StoreSidecarKind.Content, StoreSidecarKind.Search })
            {
                var measurement = new SidecarConvergenceMeasurement();
                WriteFull(kind, StoreSidecarCatalog.PathFor(
                    fixture.Binding.StoreRoot, kind, fixture.Binding.ViewId), initial, measurement);
                initialCounters[kind.ToString()] = measurement.Complete();
            }
        }

        foreach ((string language, string fileName, _) in samples)
            File.AppendAllText(Path.Combine(fixture.Binding.WorkspaceRoot, language, fileName), " \n");
        fixture.Import(binary, "language-update");
        using FamilyStoreReadSession updated = FamilyStoreReadSession.Open(fixture.Binding);
        string[] served = updated.Read(connection => Languages(connection, "symbols"));
        Assert.Equal(supported, served);
        var inventory = updated.Read(Inventory);
        var projections = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (StoreSidecarKind kind in new[] { StoreSidecarKind.Content, StoreSidecarKind.Search })
        {
            string incremental = StoreSidecarCatalog.PathFor(fixture.Binding.StoreRoot, kind, fixture.Binding.ViewId);
            var measurement = new SidecarConvergenceMeasurement();
            SidecarConvergenceDetail detail = Converge(
                kind, fixture.Binding.StoreRoot, updated, new PassthroughCursor(), measurement: measurement);
            Assert.Equal(SidecarConvergencePath.Incremental, detail.Path);
            string full = Path.Combine(fixture.Directory, "full-" + kind + ".db");
            WriteFull(kind, full, updated, measurement: null);
            Assert.Equal(CanonicalRows(kind, full), CanonicalRows(kind, incremental));
            Assert.Equal(
                StoreSidecarStamp.FromSnapshot(kind, updated.Snapshot),
                StoreSidecarCatalog.TryRead(incremental));
            string table = kind == StoreSidecarKind.Content ? "content_sources" : "search_symbols";
            Assert.Equal(supported, LanguagesFromSidecar(incremental, table));
            SidecarConvergenceCounters counters = measurement.Complete();
            Assert.True(counters.ChangedPaths >= supported.Length);
            projections[kind.ToString().ToLowerInvariant()] = new
            {
                counters,
                languages = supported.Length,
                canonical_rows = CanonicalRows(kind, incremental).Count,
            };
        }

        MillerRepositoryIndex index = RepositoryIndexLoader.LoadSession(updated);
        var representatives = updated.Read(RepresentativeSymbols);
        Assert.Equal(supported, representatives.Keys.Order(StringComparer.Ordinal).ToArray());
        var provider = new ReopeningContextProvider(fixture.Binding, index);
        var contextTool = new ContextTool(provider);
        foreach ((string language, (string symbolId, string name)) in representatives)
        {
            string output = contextTool.Context(
                "explain " + name,
                token_budget: 500,
                max_hops: 0,
                entry_symbols: [symbolId],
                format: "json",
                workspace_id: fixture.Binding.FamilyId.ToString("D"),
                ensure_fresh: false);
            using JsonDocument document = JsonDocument.Parse(output);
            JsonElement root = document.RootElement;
            Assert.False(root.TryGetProperty("error", out _));
            Assert.False(root.TryGetProperty("diagnostic", out _));
            Assert.Contains(
                root.GetProperty("bundle").EnumerateArray(),
                item => item.GetProperty("item_type").GetString() == "symbol"
                    && item.GetProperty("role").GetString() == "pivot"
                    && item.GetProperty("symbol_id").GetString() == symbolId);
            projections["context:" + language] = new
            {
                symbol_id = symbolId,
                name,
                item_type = "symbol",
                role = "pivot",
                error = false,
                diagnostic = false,
            };
        }

        string? evidencePath = Environment.GetEnvironmentVariable("MILLER_SIDECAR_SCALE_EVIDENCE");
        if (!string.IsNullOrWhiteSpace(evidencePath))
        {
            var provenance = samples.ToDictionary(
                sample => sample.Language,
                sample => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sample.Source))),
                StringComparer.Ordinal);
            File.WriteAllText(evidencePath, JsonSerializer.Serialize(new
            {
                schema_version = 1,
                extractor = new
                {
                    version = new JulieExtractRunner(binary).QueryVersion(),
                    sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(binary))),
                },
                fixture_provenance = new
                {
                    source = "ReaderRetentionLanguageScaleTests embedded copies",
                    julie_extractors_commit = "3b3e5b6f03b724448df9012bb75224e99ca68f5d",
                    samples = provenance,
                },
                supported_languages = supported,
                symbol_inventory = inventory,
                projections,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static StoreSidecarCursorSession RealCursor(
        string binary,
        string storeRoot,
        WorkspaceReadSnapshot snapshot,
        StoreSidecarKind kind,
        Action<StoreSidecarCursorKey, long>? beforeAdvance = null) =>
        new(
            storeRoot,
            snapshot,
            kind,
            (key, sequence) =>
            {
                beforeAdvance?.Invoke(key, sequence);
                return StoreConsumerCursorRunner.Advance(
                    binary, storeRoot, key.FamilyId, key.GenerationName, key.ConsumerId, sequence);
            },
            Release(binary, storeRoot));

    private static StoreSidecarCursorAdvance Advance(string binary, string storeRoot) =>
        (key, sequence) => StoreConsumerCursorRunner.Advance(
            binary, storeRoot, key.FamilyId, key.GenerationName, key.ConsumerId, sequence);

    private static StoreSidecarCursorRelease Release(string binary, string storeRoot) =>
        key => StoreConsumerCursorRunner.Release(binary, storeRoot, key.FamilyId, key.ConsumerId);

    private static SidecarConvergenceDetail Converge(
        StoreSidecarKind kind,
        string storeRoot,
        IWorkspaceReadSession session,
        IStoreSidecarCursorSession cursor,
        Action? onWrite = null,
        SidecarConvergenceMeasurement? measurement = null)
    {
        SidecarConvergenceDetail detail = kind switch
        {
            StoreSidecarKind.Content => new ContentCorpusSidecar()
                .EnsureStoreCurrentWithCursor(storeRoot, session, cursor, measurement),
            StoreSidecarKind.Search => new SymbolSearchSidecar(true, RegionIndexOptions.Disabled)
                .EnsureStoreCurrentWithCursor(storeRoot, session, cursor, measurement),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        if (detail.DidWork)
            onWrite?.Invoke();
        return detail;
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

    private static bool CursorExists(string storeRoot, string consumerId)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(storeRoot, "coord.db")};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM consumer_cursors WHERE consumer_id=$id;";
        command.Parameters.AddWithValue("$id", consumerId);
        return (long)command.ExecuteScalar()! == 1;
    }

    private static long ReaderRegistrationCount(string storeRoot)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(storeRoot, "coord.db")};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM reader_registrations;";
        return (long)command.ExecuteScalar()!;
    }

    private static (string Language, string FileName, string Source)[] LanguageSamples()
    {
        FieldInfo field = typeof(Miller.Tests.Indexing.ReaderRetentionLanguageScaleTests)
            .GetField("Samples", BindingFlags.NonPublic | BindingFlags.Static)!;
        return ((ValueTuple<string, string, string>[])field.GetValue(null)!)
            .Select(sample => (sample.Item1, sample.Item2, sample.Item3)).ToArray();
    }

    private static string[] Languages(SqliteConnection connection, string table)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT DISTINCT language FROM {table} ORDER BY language;";
        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read()) rows.Add(reader.GetString(0));
        return rows.ToArray();
    }

    private static string[] LanguagesFromSidecar(string path, string table)
    {
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        connection.Open();
        return Languages(connection, table);
    }

    private static IReadOnlyList<object> Inventory(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT language,kind,COUNT(*) FROM symbols GROUP BY language,kind ORDER BY language,kind;";
        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new List<object>();
        while (reader.Read())
            rows.Add(new { language = reader.GetString(0), kind = reader.GetString(1), count = reader.GetInt64(2) });
        return rows;
    }

    private static Dictionary<string, (string SymbolId, string Name)> RepresentativeSymbols(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT language,symbol_id,name FROM symbols WHERE name<>'' ORDER BY language,path,name;";
        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        while (reader.Read()) rows.TryAdd(reader.GetString(0), (reader.GetString(1), reader.GetString(2)));
        return rows;
    }

    private static IReadOnlyList<string> CanonicalRows(StoreSidecarKind kind, string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        connection.Open();
        string[] queries = kind == StoreSidecarKind.Content
            ? ["SELECT source_id,content_kind,ifnull(workspace_id,''),ifnull(workspace_revision,-1),ifnull(path,''),language,content_hash,source_bytes,status FROM content_sources ORDER BY source_id", "SELECT chunk_id,source_id,language,raw_text FROM content_chunks ORDER BY chunk_id"]
            : ["SELECT symbol_id,name,ifnull(signature,''),kind,language,path FROM search_symbols ORDER BY symbol_id", "SELECT symbol_id,body FROM symbols_fts ORDER BY symbol_id", "SELECT symbol_id,name_collapsed,qual_collapsed FROM symbols_trigram ORDER BY symbol_id"];
        var rows = new List<string>();
        foreach (string query in queries)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = query;
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
                rows.Add(string.Join('\u001f', Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetValue(i))));
        }
        return rows;
    }

    private sealed class PassthroughCursor : IStoreSidecarCursorSession
    {
        public bool TryProtectBaseline(StoreSidecarStamp baseline) => true;
        public void PrepareTarget(long sequence) { }
        public StoreSidecarCursorCompletion CompleteCommitted() => new(true, false, null);
    }

    private sealed class ReopeningContextProvider(
        StoreFamilyBinding binding,
        MillerRepositoryIndex index) : IWorkspaceIndexProvider
    {
        public WorkspaceReadContext Resolve(string? workspaceId, WorkspaceRefreshMode refresh)
        {
            var session = FamilyStoreReadSession.Open(binding, workspaceId);
            return new(
                index,
                new SmartTargetResolver(index),
                new WorkspaceReadHandle(session),
                binding.FamilyId.ToString("D"),
                binding.WorkspaceRoot,
                session.Snapshot.Freshness.StoreLogSequence!.Value,
                true,
                "current",
                null);
        }
    }

    private sealed class PublishedStore : IDisposable
    {
        private PublishedStore(string directory, StoreFamilyBinding binding)
        {
            Directory = directory;
            Binding = binding;
        }

        internal string Directory { get; }
        internal StoreFamilyBinding Binding { get; }

        internal static PublishedStore Create(string binary, IReadOnlyList<(string Path, string Source)> files)
        {
            string directory = Path.Combine(Path.GetTempPath(), "miller-sidecar-scale-" + Guid.NewGuid().ToString("N"));
            string root = Path.Combine(directory, "workspace");
            System.IO.Directory.CreateDirectory(root);
            foreach ((string path, string source) in files)
            {
                string target = Path.Combine(root, path);
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllText(target, source);
            }
            var binding = new StoreFamilyBinding(Guid.NewGuid(), Path.Combine(directory, "store"), "view-scale", root, StoreBindingState.Ready);
            var fixture = new PublishedStore(directory, binding);
            fixture.Import(binary, "initial-import");
            return fixture;
        }

        internal void Import(string binary, string request) =>
            ScaleTestSupport.RunJulie(
                binary, "store", "import", "--store", Binding.StoreRoot,
                "--family", Binding.FamilyId.ToString("D"), "--root", Binding.WorkspaceRoot,
                "--view", Binding.ViewId, "--level", "full", "--jobs", "1",
                "--request-id", request, "--idempotency-key", request, "--json");

        internal void Update(string binary, string path, string request) =>
            ScaleTestSupport.RunJulie(
                binary, "store", "update", "--store", Binding.StoreRoot,
                "--family", Binding.FamilyId.ToString("D"), "--root", Binding.WorkspaceRoot,
                "--view", Binding.ViewId, "--file", path, "--level", "full",
                "--request-id", request, "--idempotency-key", request, "--json");

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { System.IO.Directory.Delete(Directory, recursive: true); }
            catch (IOException) { }
        }
    }
}
