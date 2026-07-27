using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class SymbolsArtifactIdentityTests
{
    [Fact]
    public void MatchesArtifact_MetadataTableAbsent_FallsBackToTheHistoricalBehaviour()
    {
        var preStamping = SymbolsArtifactIdentity.Unprovable(7);

        Assert.True(preStamping.MatchesArtifact(null));
        Assert.True(preStamping.MatchesArtifact("artifact-anything"));
    }

    [Fact]
    public void MatchesArtifact_MetadataPresentButCarriesNoId_RefusesInsteadOfTrusting()
    {
        var anomalous = new SymbolsArtifactIdentity(7, null, MetadataPresent: true);

        Assert.False(anomalous.MatchesArtifact(null));
        Assert.False(anomalous.MatchesArtifact("artifact-a"));
    }

    [Fact]
    public void Matches_MetadataPresentButCarriesNoId_RefusesEvenAtTheSameRevision()
    {
        var anomalous = new SymbolsArtifactIdentity(7, null, MetadataPresent: true);

        Assert.False(anomalous.Matches(7, null));
    }

    [Fact]
    public void ReadSymbolsIdentity_RebasedOnACallerRevision_KeepsTheMetadataPresentVerdict()
    {
        using var fx = JulieDbFixture.CreateDefault();
        Exec(fx.DbPath, "DELETE FROM artifact_metadata WHERE key = 'artifact_id';");

        // Rebasing an identity onto a caller-supplied revision must not quietly drop the fact that the artifact
        // HAS metadata and simply omitted its id — that is what makes a null id untrustworthy rather than
        // historical. `with` preserves it; reconstructing positionally did not.
        SymbolsArtifactIdentity rebased = SymbolsArtifactIdentity.Read(fx.DbPath) with { Revision = 42 };

        Assert.Equal(42, rebased.Revision);
        Assert.True(rebased.MetadataPresent);
        Assert.False(rebased.MatchesArtifact("artifact-a"));
    }

    [Fact]
    public void Read_RealArtifact_ReportsItsIdAndThatMetadataIsPresent()
    {
        using var fx = JulieDbFixture.CreateDefault();

        SymbolsArtifactIdentity identity = SymbolsArtifactIdentity.Read(fx.DbPath);

        Assert.True(identity.MetadataPresent);
        Assert.NotNull(identity.ArtifactId);
        Assert.True(identity.MatchesArtifact(identity.ArtifactId));
        Assert.False(identity.MatchesArtifact("artifact-someone-else"));
    }

    [Fact]
    public void Read_MetadataTableRetainedButArtifactIdRowDeleted_DoesNotTrustTheMissingId()
    {
        using var fx = JulieDbFixture.CreateDefault();
        Exec(fx.DbPath, "DELETE FROM artifact_metadata WHERE key = 'artifact_id';");

        SymbolsArtifactIdentity identity = SymbolsArtifactIdentity.Read(fx.DbPath);

        Assert.True(identity.MetadataPresent);
        Assert.Null(identity.ArtifactId);
        Assert.False(identity.MatchesArtifact("artifact-a"));
    }

    [Fact]
    public void TryRead_UnreadableArtifact_ReportsAnUnprovableGenerationRatherThanThrowing()
    {
        SymbolsArtifactIdentity identity =
            SymbolsArtifactIdentity.TryRead(Path.Combine(Path.GetTempPath(), "miller-absent-" + Guid.NewGuid().ToString("N")));

        Assert.False(identity.MetadataPresent);
        Assert.Null(identity.ArtifactId);
        Assert.True(identity.MatchesArtifact("anything"));
    }

    private static void Exec(string dbPath, string sql)
    {
        SqliteConnection.ClearAllPools();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
