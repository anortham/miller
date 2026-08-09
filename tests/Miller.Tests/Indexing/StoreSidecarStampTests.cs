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
            new WorkspaceFreshnessToken("family-a", 3, manifestHash, sequence, "resolution-a"),
            IndexLevels.FullMetadataValue,
            WorkspaceReadMode.FamilyStore);
}
