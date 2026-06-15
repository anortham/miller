using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins the tolerant <c>artifact_metadata.binary_version</c> reader feeding the leadership-eligibility gate:
/// it returns the recorded extractor version from a v1 artifact and null — never throws — for a missing DB
/// file, a pre-v1 artifact without the <c>artifact_metadata</c> table, a missing key, or a file that is not a
/// SQLite database (mirrors <see cref="ExtractReader.ReadRootPath"/>'s tolerance). Fast suite (synthetic
/// fixture, no julie-extract binary).
/// </summary>
public sealed class ExtractBinaryVersionReaderTests
{
    [Fact]
    public void TryRead_ReturnsTheRecordedBinaryVersion()
    {
        using var fx = JulieDbFixture.CreateDefault();

        Assert.Equal(MillerExtractContract.PinnedJulieExtractVersion, ExtractBinaryVersionReader.TryRead(fx.DbPath));
    }

    [Fact]
    public void TryRead_MissingDbFile_ReturnsNull()
    {
        string missing = Path.Combine(Path.GetTempPath(), "miller-bv-" + Guid.NewGuid().ToString("N"), "symbols.db");

        Assert.Null(ExtractBinaryVersionReader.TryRead(missing));
    }

    [Fact]
    public void TryRead_MissingMetadataTable_ReturnsNull()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            rows: [],
            createMetadataTable: false);

        Assert.Null(ExtractBinaryVersionReader.TryRead(fx.DbPath));
    }

    [Fact]
    public void TryRead_MissingBinaryVersionKey_ReturnsNull()
    {
        using var fx = JulieDbFixture.CreateDefault();
        DeleteBinaryVersionRow(fx.DbPath);

        Assert.Null(ExtractBinaryVersionReader.TryRead(fx.DbPath));
    }

    [Fact]
    public void TryRead_FileIsNotASqliteDatabase_ReturnsNull()
    {
        string dir = Path.Combine(Path.GetTempPath(), "miller-bv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "symbols.db");
        try
        {
            File.WriteAllText(path, "this is not a sqlite database");

            Assert.Null(ExtractBinaryVersionReader.TryRead(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TryRead_WithConnection_ReturnsTheRecordedBinaryVersion()
    {
        using var fx = JulieDbFixture.CreateDefault();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fx.DbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();

        Assert.Equal(MillerExtractContract.PinnedJulieExtractVersion, ExtractBinaryVersionReader.TryRead(connection));
    }

    [Fact]
    public void TryRead_WithConnection_NullConnection_ReturnsNull()
    {
        Assert.Null(ExtractBinaryVersionReader.TryRead((SqliteConnection)null!));
    }

    [Fact]
    public void TryRead_WithConnection_MissingMetadataTable_ReturnsNull()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            rows: [],
            createMetadataTable: false);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fx.DbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();

        Assert.Null(ExtractBinaryVersionReader.TryRead(connection));
    }

    private static void DeleteBinaryVersionRow(string dbPath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM artifact_metadata WHERE key = 'binary_version';";
        cmd.ExecuteNonQuery();
    }
}
