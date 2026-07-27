using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class SymbolsArtifactIdentityTests
{
    [Fact]
    public void MatchesArtifact_UnreadableArtifact_ProvesNothingAndKeepsServing()
    {
        var unreadable = SymbolsArtifactIdentity.Unprovable(7);

        Assert.True(unreadable.MatchesArtifact(null));
        Assert.True(unreadable.MatchesArtifact("artifact-anything"));
    }

    [Fact]
    public void MatchesArtifact_PreStampingArtifact_AcceptsAnEquallyUnstampedSidecar()
    {
        var preStamping = new SymbolsArtifactIdentity(7, null, ArtifactStampState.Absent);

        Assert.True(preStamping.MatchesArtifact(null));
    }

    [Fact]
    public void MatchesArtifact_PreStampingArtifactUnderAStampedSidecar_RefusesTheContradiction()
    {
        // Whatever stamped the sidecar read an artifact that HAD metadata. An artifact provably without it is a
        // different generation, not the one the sidecar was built from — unlike an unreadable artifact, which
        // proves nothing either way.
        var preStamping = new SymbolsArtifactIdentity(7, null, ArtifactStampState.Absent);

        Assert.False(preStamping.MatchesArtifact("artifact-a"));
        Assert.False(preStamping.Matches(7, "artifact-a"));
    }

    [Fact]
    public void MatchesArtifact_StampPresentButCarriesNoId_RefusesInsteadOfTrusting()
    {
        var anomalous = new SymbolsArtifactIdentity(7, null, ArtifactStampState.Present);

        Assert.False(anomalous.MatchesArtifact(null));
        Assert.False(anomalous.MatchesArtifact("artifact-a"));
    }

    [Fact]
    public void Matches_StampPresentButCarriesNoId_RefusesEvenAtTheSameRevision()
    {
        var anomalous = new SymbolsArtifactIdentity(7, null, ArtifactStampState.Present);

        Assert.False(anomalous.Matches(7, null));
    }

    [Fact]
    public void ReadSymbolsIdentity_RebasedOnACallerRevision_KeepsTheStampStateVerdict()
    {
        using var fx = JulieDbFixture.CreateDefault();
        Exec(fx.DbPath, "DELETE FROM artifact_metadata WHERE key = 'artifact_id';");

        // Rebasing an identity onto a caller-supplied revision must not quietly drop the fact that the artifact
        // HAS metadata and simply omitted its id — that is what makes a null id untrustworthy rather than
        // historical. `with` preserves it; reconstructing positionally did not.
        SymbolsArtifactIdentity rebased = SymbolsArtifactIdentity.Read(fx.DbPath) with { Revision = 42 };

        Assert.Equal(42, rebased.Revision);
        Assert.Equal(ArtifactStampState.Present, rebased.StampState);
        Assert.False(rebased.MatchesArtifact("artifact-a"));
    }

    [Fact]
    public void Read_RealArtifact_ReportsItsIdAndThatMetadataIsPresent()
    {
        using var fx = JulieDbFixture.CreateDefault();

        SymbolsArtifactIdentity identity = SymbolsArtifactIdentity.Read(fx.DbPath);

        Assert.Equal(ArtifactStampState.Present, identity.StampState);
        Assert.NotNull(identity.ArtifactId);
        Assert.True(identity.MatchesArtifact(identity.ArtifactId));
        Assert.False(identity.MatchesArtifact("artifact-someone-else"));
    }

    [Fact]
    public void Read_MetadataTableRetainedButEmptied_DoesNotDemoteItToAPreStampingArtifact()
    {
        using var fx = JulieDbFixture.CreateDefault();
        Exec(fx.DbPath, "DELETE FROM artifact_metadata;");

        SymbolsArtifactIdentity identity = SymbolsArtifactIdentity.Read(fx.DbPath);

        Assert.Equal(ArtifactStampState.Present, identity.StampState);
        Assert.Null(identity.ArtifactId);
        Assert.False(identity.MatchesArtifact(null));
        Assert.False(identity.MatchesArtifact("artifact-a"));
    }

    [Fact]
    public void Read_MetadataTableRetainedButArtifactIdRowDeleted_DoesNotTrustTheMissingId()
    {
        using var fx = JulieDbFixture.CreateDefault();
        Exec(fx.DbPath, "DELETE FROM artifact_metadata WHERE key = 'artifact_id';");

        SymbolsArtifactIdentity identity = SymbolsArtifactIdentity.Read(fx.DbPath);

        Assert.Equal(ArtifactStampState.Present, identity.StampState);
        Assert.Null(identity.ArtifactId);
        Assert.False(identity.MatchesArtifact("artifact-a"));
    }

    [Fact]
    public void TryRead_MissingArtifact_RefusesEverySidecarRatherThanThrowing()
    {
        SymbolsArtifactIdentity identity =
            SymbolsArtifactIdentity.TryRead(Path.Combine(Path.GetTempPath(), "miller-absent-" + Guid.NewGuid().ToString("N")));

        Assert.Equal(ArtifactStampState.SourceMissing, identity.StampState);
        Assert.Null(identity.ArtifactId);
        Assert.False(identity.MatchesArtifact("anything"));
        Assert.False(identity.MatchesArtifact(null));
    }

    [Fact]
    public void MatchesArtifact_ExistingButUnreadableArtifact_KeepsServingRatherThanFailingOnATransientLock() =>
        Assert.True(SymbolsArtifactIdentity.Unprovable(7).Matches(7, "artifact-anything"));

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
