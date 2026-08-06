using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins the read boundary provenance surfacing depends on: an artifact that was never rebound carries none of
/// the three additive keys and must read as NULL, never as an empty record — an empty record would put an empty
/// <c>rebound_from</c> object into the Eros-facing status/health JSON. Absent files and broken databases degrade
/// the same way.
/// </summary>
public sealed class RebindProvenanceReaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("miller-rebind-provenance-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string DbPath => Path.Combine(_dir, "symbols.db");

    private void CreateArtifact(params (string Key, string Value)[] metadata)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "CREATE TABLE artifact_metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);";
        cmd.ExecuteNonQuery();
        foreach ((string key, string value) in metadata)
        {
            cmd.CommandText = "INSERT INTO artifact_metadata (key, value) VALUES ($key, $value);";
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$value", value);
            cmd.ExecuteNonQuery();
        }
    }

    [Fact]
    public void Read_AReboundArtifact_CarriesAllThreeKeysVerbatim()
    {
        CreateArtifact(
            ("root_path", "/repo/worktree"),
            ("rebound_from_root", "/repo/main"),
            ("rebound_from_artifact_id", "artifact-source-77"),
            ("rebound_at", "2026-08-05T09:14:22.123456789Z"));

        RebindProvenanceMetadata? provenance = RebindProvenanceReader.Read(DbPath);

        Assert.NotNull(provenance);
        Assert.Equal("/repo/main", provenance.SourceRoot);
        Assert.Equal("artifact-source-77", provenance.SourceArtifactId);
        Assert.Equal("2026-08-05T09:14:22.123456789Z", provenance.ReboundAt);
    }

    [Fact]
    public void Read_AnArtifactThatWasNeverRebound_IsNullRatherThanAnEmptyRecord()
    {
        CreateArtifact(("root_path", "/repo/worktree"), ("index_level", "full"));

        Assert.Null(RebindProvenanceReader.Read(DbPath));
    }

    [Fact]
    public void Read_ASourceRootPresentWithoutTheOptionalKeys_KeepsTheFactAndNullsTheRest()
    {
        CreateArtifact(("rebound_from_root", "/repo/main"));

        RebindProvenanceMetadata? provenance = RebindProvenanceReader.Read(DbPath);

        Assert.NotNull(provenance);
        Assert.Equal("/repo/main", provenance.SourceRoot);
        Assert.Null(provenance.SourceArtifactId);
        Assert.Null(provenance.ReboundAt);
    }

    [Fact]
    public void Read_ABlankSourceRoot_IsNull()
    {
        CreateArtifact(("rebound_from_root", "   "), ("rebound_at", "2026-08-05T09:14:22Z"));

        Assert.Null(RebindProvenanceReader.Read(DbPath));
    }

    [Fact]
    public void Read_AnAbsentArtifact_IsNull() => Assert.Null(RebindProvenanceReader.Read(DbPath));

    [Fact]
    public void Read_ANullOrBlankPath_IsNull()
    {
        Assert.Null(RebindProvenanceReader.Read(null));
        Assert.Null(RebindProvenanceReader.Read("   "));
    }

    [Fact]
    public void Read_AnArtifactWithoutTheMetadataTable_IsNull()
    {
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString()))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "CREATE TABLE files (path TEXT PRIMARY KEY);";
            cmd.ExecuteNonQuery();
        }

        Assert.Null(RebindProvenanceReader.Read(DbPath));
    }

    [Fact]
    public void Read_ACorruptArtifact_IsNull()
    {
        File.WriteAllText(DbPath, "not a sqlite database");

        Assert.Null(RebindProvenanceReader.Read(DbPath));
    }
}
