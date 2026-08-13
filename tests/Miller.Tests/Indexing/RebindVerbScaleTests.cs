using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Live proof of the julie-extract 2.27.0 <c>rebind</c> verb through <see cref="JulieExtractRunner.Rebind"/>:
/// scan a fixture tree, COPY the artifact, retarget the copy at a second byte-identical tree, and prove the
/// follow-up incremental scan of that copy has nothing to do. Spawns the pinned binary, so it is
/// <c>[Trait("Category","Scale")]</c> and obtains the binary via
/// <see cref="ScaleTestSupport.RequireJulieServer"/>; SKIPS when <c>.tools/julie-extract</c> is absent.
/// </summary>
[Trait("Category", "Scale")]
public sealed class RebindVerbScaleTests
{
    [Fact]
    public void Rebind_OnAnArtifactCopy_RetargetsTheRootAndLeavesTheFollowUpScanWithNothingToDo()
    {
        string binary = ScaleTestSupport.RequireJulieServer();

        string work = Path.Combine(Path.GetTempPath(), "miller-rebind-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(work, "checkout-a");
        string target = Path.Combine(work, "checkout-b");
        string db = Path.Combine(work, ".miller", "symbols.db");
        string copy = Path.Combine(work, ".miller", "symbols.db.rebuild");
        try
        {
            WriteFixtureTree(source);
            WriteFixtureTree(target);

            var runner = new JulieExtractRunner(binary);
            ExtractReport scanned = runner.Scan(source, db);
            Assert.NotEqual("failed", scanned.Status);

            string? sourceArtifactId = ReadArtifactId(db);
            SqliteConnection.ClearAllPools();
            File.Copy(db, copy);

            RebindReport rebound = runner.Rebind(copy, target, TestContext.Current.CancellationToken);

            Assert.True(rebound.Changed);
            // Identity, not string equality. julie-extract canonicalizes through Rust's std::fs::canonicalize
            // and so emits the Win32 \\?\ extended-length prefix, while PathCanonicalizer.CanonicalizeRoot
            // deliberately strips it and ParseRebindReport carries julie's strings verbatim by design.
            // ArtifactRootIdentity.Matches strips both operands — and also absorbs the second Windows
            // divergence a bare strip would miss, that Rust reflects on-disk casing while Miller preserves
            // as-launched casing. Production is already prefix-immune this way (WorkspaceId strips before
            // hashing, so both spellings yield one workspace_id); only these assertions were not.
            Assert.True(
                ArtifactRootIdentity.Matches(rebound.PreviousRoot, PathCanonicalizer.CanonicalizeRoot(source)),
                $"previous root '{rebound.PreviousRoot}' should identify the source root '{source}'");
            Assert.True(
                ArtifactRootIdentity.Matches(rebound.NewRoot, PathCanonicalizer.CanonicalizeRoot(target)),
                $"new root '{rebound.NewRoot}' should identify the target root '{target}'");
            Assert.Equal(sourceArtifactId, rebound.PreviousArtifactId);
            Assert.NotEqual(rebound.PreviousArtifactId, rebound.NewArtifactId);
            Assert.Equal(rebound.NewArtifactId, ReadArtifactId(copy));
            Assert.Equal(rebound.NewRoot, ReadMetadata(copy, "root_path"));
            Assert.Equal(rebound.PreviousRoot, ReadMetadata(copy, "rebound_from_root"));
            Assert.Equal(rebound.PreviousArtifactId, ReadMetadata(copy, "rebound_from_artifact_id"));

            ExtractReport reconciled = runner.Scan(target, copy);

            Assert.True(reconciled.IsNoChange, $"expected no_change, got {reconciled.Status}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(work, recursive: true); } catch (IOException) { /* best-effort temp cleanup */ }
        }
    }

    [Fact]
    public void Rebind_AtTheRootTheArtifactAlreadyRecords_SucceedsWithoutChangingAnything()
    {
        string binary = ScaleTestSupport.RequireJulieServer();

        string work = Path.Combine(Path.GetTempPath(), "miller-rebind-noop-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(work, "checkout-a");
        string db = Path.Combine(work, ".miller", "symbols.db");
        try
        {
            WriteFixtureTree(source);

            var runner = new JulieExtractRunner(binary);
            Assert.NotEqual("failed", runner.Scan(source, db).Status);
            string? artifactId = ReadArtifactId(db);

            RebindReport rebound = runner.Rebind(db, source, TestContext.Current.CancellationToken);

            Assert.False(rebound.Changed);
            Assert.Equal(rebound.PreviousRoot, rebound.NewRoot);
            Assert.Equal(artifactId, rebound.PreviousArtifactId);
            Assert.Equal(artifactId, rebound.NewArtifactId);
            Assert.Equal(artifactId, ReadArtifactId(db));
            Assert.Null(ReadMetadata(db, "rebound_from_root"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(work, recursive: true); } catch (IOException) { /* best-effort temp cleanup */ }
        }
    }

    [Fact]
    public void Rebind_OnAnArtifactWithNoCommittedRevision_RefusesAsIncompatible()
    {
        string binary = ScaleTestSupport.RequireJulieServer();

        string work = Path.Combine(Path.GetTempPath(), "miller-rebind-shell-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(work, "checkout-a");
        string target = Path.Combine(work, "checkout-b");
        string db = Path.Combine(work, ".miller", "symbols.db");
        try
        {
            WriteFixtureTree(source);
            WriteFixtureTree(target);

            var runner = new JulieExtractRunner(binary);
            Assert.NotEqual("failed", runner.Scan(source, db).Status);

            using (SqliteConnection connection = OpenReadWrite(db))
            {
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM extraction_revisions;";
                command.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            var ex = Assert.Throws<IncompatibleExtractException>(
                () => runner.Rebind(db, target, TestContext.Current.CancellationToken));

            Assert.Contains("no_committed_revision", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(work, recursive: true); } catch (IOException) { /* best-effort temp cleanup */ }
        }
    }

    private static void WriteFixtureTree(string root)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "widget.cs"), """
            namespace Demo;

            public sealed class RebindWidget
            {
                public int MillerRebindMarker(int size) => size;
            }
            """);
    }

    private static SqliteConnection OpenReadWrite(string dbPath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            ForeignKeys = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static string? ReadArtifactId(string dbPath) => ReadMetadata(dbPath, "artifact_id");

    private static string? ReadMetadata(string dbPath, string key)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        using (connection)
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM artifact_metadata WHERE key = $key;";
            command.Parameters.AddWithValue("$key", key);
            return command.ExecuteScalar() as string;
        }
    }
}
