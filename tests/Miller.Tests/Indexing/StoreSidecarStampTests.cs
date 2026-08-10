using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class StoreSidecarStampTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "miller-store-sidecar-stamp-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void StampPublishesTheExactFamilyViewManifestAndCursorAtomically()
    {
        Directory.CreateDirectory(_root);
        string databasePath = StoreSidecarCatalog.PathFor(_root, StoreSidecarKind.Search, "view/with/slashes");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE payload(value TEXT); INSERT INTO payload VALUES ('ready');";
            command.ExecuteNonQuery();
        }

        StoreSidecarStamp expected = StoreSidecarStamp.FromSnapshot(
            StoreSidecarKind.Search,
            Snapshot("manifest-a", sequence: 17));
        StoreSidecarCatalog.Stamp(databasePath, expected);

        Assert.Equal(expected, StoreSidecarCatalog.TryRead(databasePath));
        Assert.Equal("family-a:gen-001", expected.StoreInstanceId);
        Assert.Equal("gen-001", expected.GenerationName);
        Assert.Equal(3, expected.ManifestGeneration);
        Assert.Equal(IndexLevels.FullMetadataValue, expected.IndexLevel);
        Assert.True(StoreSidecarCatalog.IsCurrent(databasePath, expected));
        Assert.False(StoreSidecarCatalog.IsCurrent(
            databasePath,
            StoreSidecarStamp.FromSnapshot(StoreSidecarKind.Search, Snapshot("manifest-b", sequence: 18))));
        Assert.StartsWith(
            Path.Combine(PathCanonicalizer.CanonicalizeRoot(_root), "sidecars"),
            databasePath,
            StringComparison.Ordinal);
    }

    [Fact]
    public void StampUpgradesAnOlderCompletenessSchemaBeforePublishingTheNewToken()
    {
        Directory.CreateDirectory(_root);
        string databasePath = StoreSidecarCatalog.PathFor(_root, StoreSidecarKind.Vector, "view-a");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE store_sidecar_stamp(
                    singleton INTEGER PRIMARY KEY CHECK(singleton=1),
                    kind TEXT NOT NULL,
                    family_id TEXT NOT NULL,
                    view_id TEXT NOT NULL,
                    manifest_hash TEXT NOT NULL,
                    store_log_sequence INTEGER NOT NULL,
                    resolution_stamp TEXT);
                INSERT INTO store_sidecar_stamp VALUES (1,'vector','family-a','view-a','old',1,'old');
                """;
            command.ExecuteNonQuery();
        }

        StoreSidecarStamp expected = StoreSidecarStamp.FromSnapshot(
            StoreSidecarKind.Vector,
            Snapshot("manifest-new", sequence: 19));

        StoreSidecarCatalog.Stamp(databasePath, expected);

        Assert.Equal(expected, StoreSidecarCatalog.TryRead(databasePath));
    }

    [Fact]
    public void MissingOrUnstampedSidecarIsNeverCurrent()
    {
        Directory.CreateDirectory(_root);
        string databasePath = StoreSidecarCatalog.PathFor(_root, StoreSidecarKind.Content, "view-a");
        StoreSidecarStamp expected = StoreSidecarStamp.FromSnapshot(
            StoreSidecarKind.Content,
            Snapshot("manifest-a", sequence: 4));

        Assert.Null(StoreSidecarCatalog.TryRead(databasePath));
        Assert.False(StoreSidecarCatalog.IsCurrent(databasePath, expected));
    }

    [Fact]
    public void UnknownSidecarKindIsTreatedAsAnUnstampedArtifact()
    {
        Directory.CreateDirectory(_root);
        string databasePath = StoreSidecarCatalog.PathFor(_root, StoreSidecarKind.Search, "view-a");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            connection.Open();
        StoreSidecarCatalog.Stamp(
            databasePath,
            StoreSidecarStamp.FromSnapshot(StoreSidecarKind.Search, Snapshot("manifest-a", sequence: 4)));

        using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE store_sidecar_stamp SET kind='future';";
            command.ExecuteNonQuery();
        }

        Assert.Null(StoreSidecarCatalog.TryRead(databasePath));
    }

    [Fact]
    public void StoreStatusFactsReportMissingSidecarsAgainstThePinnedCursor()
    {
        Directory.CreateDirectory(_root);
        WorkspaceReadSnapshot snapshot = Snapshot("manifest-a", sequence: 4);

        SearchSidecarFacts search = new SymbolSearchSidecar(enabled: true).InspectStore(_root, snapshot);
        ContentCorpusFacts content = new ContentCorpusSidecar().InspectStore(_root, snapshot);

        Assert.Equal("missing", search.State);
        Assert.Equal(4, search.ExpectedRevision);
        Assert.Equal("missing", content.State);
        Assert.Equal(4, content.WorkspaceRevision);
    }

    [Fact]
    public void VectorSidecarRefusesEveryNonExactFamilyViewCursor()
    {
        Directory.CreateDirectory(_root);
        WorkspaceReadSnapshot snapshot = Snapshot("manifest-a", sequence: 17);
        string databasePath = VectorSidecar.PathForStore(_root, snapshot.ViewId);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            connection.Open();
        }

        StoreSidecarStamp exact = StoreSidecarStamp.FromSnapshot(StoreSidecarKind.Vector, snapshot);
        StoreSidecarCatalog.Stamp(databasePath, exact);

        Assert.Equal(StoreSidecarCatalog.PathFor(_root, StoreSidecarKind.Vector, snapshot.ViewId), databasePath);
        Assert.True(StoreSidecarCatalog.IsCurrent(databasePath, exact));
        Assert.False(StoreSidecarCatalog.IsCurrent(
            databasePath,
            exact with { ViewId = "view-b" }));
        Assert.False(StoreSidecarCatalog.IsCurrent(
            databasePath,
            exact with { ManifestHash = "manifest-b" }));
        Assert.False(StoreSidecarCatalog.IsCurrent(
            databasePath,
            exact with { StoreLogSequence = 18 }));
        Assert.False(StoreSidecarCatalog.IsCurrent(
            databasePath,
            exact with { ResolutionStamp = "resolution-b" }));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private WorkspaceReadSnapshot Snapshot(string manifestHash, long sequence) =>
        new(
            _root,
            "workspace-a",
            "family-a",
            "view-a",
            new WorkspaceFreshnessToken(
                "family-a",
                3,
                manifestHash,
                sequence,
                "resolution-a",
                StoreInstanceId: "family-a:gen-001",
                ViewId: "view-a",
                GenerationName: "gen-001",
                ManifestGeneration: 3,
                IndexLevel: IndexLevels.FullMetadataValue,
                LevelStampL1: "l1-a",
                LevelStampL2: "l2-a",
                LevelStampL3: "l3-a"),
            IndexLevels.FullMetadataValue,
            WorkspaceReadMode.FamilyStore,
            GenerationName: "gen-001",
            ManifestGeneration: 3);
}
