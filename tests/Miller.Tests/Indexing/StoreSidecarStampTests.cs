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
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString()))
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
    public void ScopeTokenChangesWhenAnyStampFieldChanges()
    {
        StoreSidecarStamp stamp = StoreSidecarStamp.FromSnapshot(
            StoreSidecarKind.Vector,
            Snapshot("manifest-a", sequence: 17));
        string token = ScopeToken(stamp);

        Assert.Equal(token, ScopeToken(stamp));
        StoreSidecarStamp[] variants =
        [
            stamp with { Kind = StoreSidecarKind.Content },
            stamp with { FamilyId = "family-b" },
            stamp with { ViewId = "view-b" },
            stamp with { ManifestHash = "manifest-b" },
            stamp with { StoreLogSequence = 18 },
            stamp with { ResolutionStamp = null },
            stamp with { StoreInstanceId = "family-a:gen-002" },
            stamp with { GenerationName = "gen-002" },
            stamp with { ManifestGeneration = 4 },
            stamp with { IndexLevel = "L1" },
            stamp with { LevelStampL1 = "l1-b" },
            stamp with { LevelStampL2 = "l2-b" },
            stamp with { LevelStampL3 = "l3-b" },
        ];

        Assert.All(variants, variant => Assert.NotEqual(token, ScopeToken(variant)));
    }

    [Fact]
    public void StampUpgradesAnOlderCompletenessSchemaBeforePublishingTheNewToken()
    {
        Directory.CreateDirectory(_root);
        string databasePath = StoreSidecarCatalog.PathFor(_root, StoreSidecarKind.Vector, "view-a");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString()))
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
        Assert.Null(StoreSidecarCatalog.TryLastGood(databasePath, expected));
    }

    [Fact]
    public void TryLastGood_SameFamilyAndViewAtAnEarlierSequence_ReturnsThatStamp()
    {
        Directory.CreateDirectory(_root);
        string databasePath = StoreSidecarCatalog.PathFor(_root, StoreSidecarKind.Search, "view-a");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE payload(value TEXT); INSERT INTO payload VALUES ('ready');";
            command.ExecuteNonQuery();
        }

        StoreSidecarStamp earlier = StoreSidecarStamp.FromSnapshot(
            StoreSidecarKind.Search,
            Snapshot("manifest-a", sequence: 17));
        StoreSidecarCatalog.Stamp(databasePath, earlier);
        StoreSidecarStamp live = StoreSidecarStamp.FromSnapshot(
            StoreSidecarKind.Search,
            Snapshot("manifest-b", sequence: 21));

        Assert.False(StoreSidecarCatalog.IsCurrent(databasePath, live));
        Assert.Equal(earlier, StoreSidecarCatalog.TryLastGood(databasePath, live));
    }

    [Fact]
    public void TryLastGood_DifferentFamilyOrView_IsNeverLastGood()
    {
        Directory.CreateDirectory(_root);
        string databasePath = StoreSidecarCatalog.PathFor(_root, StoreSidecarKind.Search, "view-a");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString()))
        {
            connection.Open();
        }

        StoreSidecarCatalog.Stamp(
            databasePath,
            StoreSidecarStamp.FromSnapshot(StoreSidecarKind.Search, Snapshot("manifest-a", sequence: 17)));

        StoreSidecarStamp otherFamily = StoreSidecarStamp.FromSnapshot(
            StoreSidecarKind.Search,
            Snapshot("manifest-a", sequence: 21, familyId: "family-b"));
        StoreSidecarStamp otherView = StoreSidecarStamp.FromSnapshot(
            StoreSidecarKind.Search,
            Snapshot("manifest-a", sequence: 21, viewId: "view-b"));

        Assert.Null(StoreSidecarCatalog.TryLastGood(databasePath, otherFamily));
        Assert.Null(StoreSidecarCatalog.TryLastGood(databasePath, otherView));
    }

    [Fact]
    public void AllowsLastGoodServe_FamilyStoreSnapshot_IsTrueWhenResolutionIsExact()
    {
        WorkspaceReadSnapshot exact = Snapshot("manifest-a", sequence: 21, resolutionState: "exact");
        WorkspaceReadSnapshot converging = Snapshot("manifest-a", sequence: 21, resolutionState: "converging");
        WorkspaceReadSnapshot unbound = Snapshot("manifest-a", sequence: 21, resolutionState: "unbound");

        Assert.True(StoreSidecarCatalog.AllowsLastGoodServe(exact));
        Assert.True(StoreSidecarCatalog.AllowsLastGoodServe(converging));
        Assert.True(StoreSidecarCatalog.AllowsLastGoodServe(unbound));
    }

    [Fact]
    public void AllowsLastGoodServe_LegacySnapshot_IsFalse()
    {
        var snapshot = new WorkspaceReadSnapshot(
            _root,
            "workspace-a",
            "artifact-a",
            "view-a",
            new WorkspaceFreshnessToken("artifact-a", 4),
            IndexLevels.FullMetadataValue,
            WorkspaceReadMode.LegacyArtifact);

        Assert.False(StoreSidecarCatalog.AllowsLastGoodServe(snapshot));
    }

    [Fact]
    public void TryResolveReadable_ExactSnapshotWithEarlierSidecar_ReturnsLastGood()
    {
        Directory.CreateDirectory(_root);
        string databasePath = StoreSidecarCatalog.PathFor(_root, StoreSidecarKind.Search, "view-a");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString()))
        {
            connection.Open();
        }

        WorkspaceReadSnapshot earlierSnapshot = Snapshot("manifest-a", sequence: 17);
        StoreSidecarStamp earlier = StoreSidecarStamp.FromSnapshot(StoreSidecarKind.Search, earlierSnapshot);
        StoreSidecarCatalog.Stamp(databasePath, earlier);
        WorkspaceReadSnapshot live = Snapshot("manifest-b", sequence: 21, resolutionState: "exact");
        StoreSidecarStamp expected = StoreSidecarStamp.FromSnapshot(StoreSidecarKind.Search, live);

        Assert.Equal(earlier, StoreSidecarCatalog.TryResolveReadable(databasePath, expected, live));
    }

    [Fact]
    public void TryResolveReadable_ExactCurrentSidecar_ReturnsExpected()
    {
        Directory.CreateDirectory(_root);
        string databasePath = StoreSidecarCatalog.PathFor(_root, StoreSidecarKind.Search, "view-a");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString()))
        {
            connection.Open();
        }

        WorkspaceReadSnapshot live = Snapshot("manifest-a", sequence: 21, resolutionState: "exact");
        StoreSidecarStamp expected = StoreSidecarStamp.FromSnapshot(StoreSidecarKind.Search, live);
        StoreSidecarCatalog.Stamp(databasePath, expected);

        Assert.Equal(expected, StoreSidecarCatalog.TryResolveReadable(databasePath, expected, live));
    }

    [Fact]
    public void UnknownSidecarKindIsTreatedAsAnUnstampedArtifact()
    {
        Directory.CreateDirectory(_root);
        string databasePath = StoreSidecarCatalog.PathFor(_root, StoreSidecarKind.Search, "view-a");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString()))
            connection.Open();
        StoreSidecarCatalog.Stamp(
            databasePath,
            StoreSidecarStamp.FromSnapshot(StoreSidecarKind.Search, Snapshot("manifest-a", sequence: 4)));

        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString()))
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
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString()))
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

    private WorkspaceReadSnapshot Snapshot(
        string manifestHash,
        long sequence,
        string familyId = "family-a",
        string viewId = "view-a",
        string? resolutionState = null) =>
        new(
            _root,
            "workspace-a",
            familyId,
            viewId,
            new WorkspaceFreshnessToken(
                familyId,
                3,
                manifestHash,
                sequence,
                "resolution-a",
                StoreInstanceId: $"{familyId}:gen-001",
                ViewId: viewId,
                GenerationName: "gen-001",
                ManifestGeneration: 3,
                IndexLevel: IndexLevels.FullMetadataValue,
                LevelStampL1: "l1-a",
                LevelStampL2: "l2-a",
                LevelStampL3: "l3-a"),
            IndexLevels.FullMetadataValue,
            WorkspaceReadMode.FamilyStore,
            GenerationName: "gen-001",
            ManifestGeneration: 3,
            ResolutionState: resolutionState);

    private static string ScopeToken(StoreSidecarStamp stamp) => stamp.ScopeToken;
}
