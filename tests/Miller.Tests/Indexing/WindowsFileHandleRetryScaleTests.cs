using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Indexing.Semantic;
using Xunit;

namespace Miller.Tests.Indexing;

[Trait("Category", "Scale")]
public sealed class WindowsFileHandleRetryScaleTests
{
    [Fact]
    public async Task FullRebuildPromotion_RetriesAWindowsHeldLiveDbUntilTheHandleIsReleased()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows file-sharing semantics are required.");

        string root = Directory.CreateTempSubdirectory("miller-windows-promote-").FullName;
        try
        {
            string livePath = Path.Combine(root, "symbols.db");
            WriteMarkerDb(livePath, "before-rebuild");
            WriteMarkerDb(FullRebuildPromotion.RebuildDbPathFor(livePath), "after-rebuild");

            using FileStream held = new(livePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            Task release = ReleaseAfterAsync(held, TimeSpan.FromMilliseconds(300));

            try
            {
                FullRebuildPromotion.Promote(livePath);
            }
            finally
            {
                await release;
            }

            Assert.Equal("after-rebuild", ReadMarker(livePath));
            Assert.False(File.Exists(FullRebuildPromotion.RebuildDbPathFor(livePath)));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task VectorGenerationPromotion_RetriesHeldActiveRotationAndCleansTheShadow()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows file-sharing semantics are required.");

        string root = Directory.CreateTempSubdirectory("miller-windows-vector-").FullName;
        try
        {
            string millerDir = Path.Combine(root, ".miller");
            Directory.CreateDirectory(millerDir);
            string activePath = Path.Combine(millerDir, "vectors.db");
            var manager = VectorGenerationManager.ForActivePath(activePath);
            WriteMarker(activePath, "old-generation");
            WriteMarker(manager.ShadowPath, "new-generation");

            using FileStream held = new(activePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            Task release = ReleaseAfterAsync(held, TimeSpan.FromMilliseconds(300));

            try
            {
                VectorPromoteResult result = manager.Promote("new-tag", "old-tag");

                Assert.Equal(VectorPromoteKind.Incompatible, result.Kind);
                Assert.Equal(manager.RetainedPathFor("old-tag"), result.RetainedPath);
            }
            finally
            {
                await release;
            }

            Assert.Equal("old-generation", ReadMarkerFile(manager.RetainedPathFor("old-tag")));
            Assert.Equal("new-generation", ReadMarkerFile(activePath));
            Assert.False(File.Exists(manager.ShadowPath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task ReleaseAfterAsync(FileStream held, TimeSpan delay)
    {
        await Task.Delay(delay);
        held.Dispose();
    }

    private static void WriteMarkerDb(string path, string marker)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE marker (value TEXT NOT NULL); INSERT INTO marker (value) VALUES ($value);";
        command.Parameters.AddWithValue("$value", marker);
        command.ExecuteNonQuery();
    }

    private static string ReadMarker(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM marker;";
        return (string)command.ExecuteScalar()!;
    }

    private static void WriteMarker(string path, string marker) => File.WriteAllText(path, marker);

    private static string ReadMarkerFile(string path) => File.ReadAllText(path);
}
