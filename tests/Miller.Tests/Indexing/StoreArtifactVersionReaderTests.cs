using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Indexing.Store;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins the leadership version read for a workspace whose store pointer names a view the serving generation
/// does not carry. Before this, that state made the version UNREADABLE, every caller converted the failure
/// into an ineligible verdict, and the workspace was permanently wedged.
/// </summary>
public sealed class StoreArtifactVersionReaderTests
{
    private const string LegacySentinel = "legacy-reader-was-called";

    [Fact]
    public void LeadershipPreflightReadsMetadataWithoutAcquiringAnUnpublishedView()
    {
        using var fixture = StorePointerFixture.Create(binaryVersion: "2.34.4");
        using var registry = new StoreReaderRegistrationRegistry(startScheduler: false);
        using IDisposable scope = StoreReaderRegistrationContext.Use(fixture.StoreRoot,
            new(new StoreReaderRegistrationRunner((_, _) =>
                throw new InvalidOperationException("Leadership preflight attempted reader admission.")), registry));

        Assert.Equal("2.34.4", StoreArtifactVersionReader.ReadForLeadership(
            fixture.LegacyArtifactPath, _ => LegacySentinel));
        Assert.Equal(0, registry.Count);
    }

    /// <summary>
    /// Proves: an unpublished view no longer makes the leadership version unreadable. The FAMILY-scope
    /// <c>store_meta.binary_version</c> is returned instead, because there is no per-view version in the store.
    /// </summary>
    [Fact]
    public void ReadForLeadershipReturnsTheFamilyVersionWhenTheViewIsUnpublished()
    {
        using var fixture = StorePointerFixture.Create(binaryVersion: "2.34.4");

        string? version = StoreArtifactVersionReader.ReadForLeadership(
            fixture.LegacyArtifactPath,
            _ => LegacySentinel);

        Assert.Equal("2.34.4", version);
    }

    /// <summary>
    /// Proves: binary_version never goes backwards for an unpublished view either — an older extractor stays
    /// ineligible — and MILLER_ALLOW_EXTRACTOR_DOWNGRADE remains the single override. This is the guard that
    /// replaces the dead <c>recoverUnpublishedView</c> flag.
    /// </summary>
    [Fact]
    public void AnOlderExtractorStaysIneligibleForAnUnpublishedViewInANewerFamily()
    {
        using var fixture = StorePointerFixture.Create(binaryVersion: "2.34.4");
        string? familyVersion = StoreArtifactVersionReader.ReadForLeadership(
            fixture.LegacyArtifactPath,
            _ => LegacySentinel);

        LeadershipVerdict refused = LeadershipEligibility.Evaluate("2.30.0", familyVersion, allowDowngrade: false);
        LeadershipVerdict overridden = LeadershipEligibility.Evaluate("2.30.0", familyVersion, allowDowngrade: true);

        Assert.False(refused.Eligible);
        Assert.Contains("2.30.0", refused.Reason, StringComparison.Ordinal);
        Assert.Contains("2.34.4", refused.Reason, StringComparison.Ordinal);
        Assert.True(overridden.Eligible);
    }

    /// <summary>
    /// Proves: same-version agent swarms cannot thrash over an unpublished view. Equal versions are eligible
    /// and the artifact is never reported as older than our own, which is what gates the yield request.
    /// </summary>
    [Fact]
    public void EqualVersionsOnAnUnpublishedViewNeitherYieldNorForceARescan()
    {
        using var fixture = StorePointerFixture.Create(binaryVersion: "2.34.4");
        string? familyVersion = StoreArtifactVersionReader.ReadForLeadership(
            fixture.LegacyArtifactPath,
            _ => LegacySentinel);

        LeadershipVerdict verdict = LeadershipEligibility.Evaluate("2.34.4", familyVersion, allowDowngrade: false);

        Assert.True(verdict.Eligible);
        Assert.False(verdict.ArtifactOlderThanOwn);
    }

    /// <summary>
    /// Proves: the new catch filter is narrow. It swallows only ViewNotFound — a store whose generation is not
    /// serving is a real corruption and must stay loud.
    /// </summary>
    [Fact]
    public void ReadForLeadershipStillThrowsWhenTheStoreItselfIsUnreadable()
    {
        using var fixture = StorePointerFixture.Create(binaryVersion: "2.34.4");
        fixture.UpdateStoreMetadata("generation_state", "sealing");

        StoreArtifactVersionReadException error = Assert.Throws<StoreArtifactVersionReadException>(() =>
            StoreArtifactVersionReader.ReadForLeadership(fixture.LegacyArtifactPath, _ => LegacySentinel));

        Assert.Equal(
            FamilyStoreReadFailure.SchemaIncompatible,
            Assert.IsType<FamilyStoreReadException>(error.InnerException).Failure);
    }

    /// <summary>
    /// Proves: CurrentMissing, GenerationMissing, and StoreMissing are NOT mapped to an eligible blank version.
    /// A blank artifact version makes LeadershipEligibility.Evaluate return Eligible: true, which would let an
    /// older extractor claim a family a newer one wrote.
    /// </summary>
    [Fact]
    public void AnEmptyFamilyStoreStillThrowsForLeadership()
    {
        using var fixture = StorePointerFixture.Create(binaryVersion: "2.34.4");
        File.Delete(Path.Combine(fixture.StoreRoot, "CURRENT"));

        StoreArtifactVersionReadException error = Assert.Throws<StoreArtifactVersionReadException>(() =>
            StoreArtifactVersionReader.ReadForLeadership(fixture.LegacyArtifactPath, _ => LegacySentinel));

        Assert.Equal(
            FamilyStoreReadFailure.CurrentMissing,
            Assert.IsType<FamilyStoreReadException>(error.InnerException).Failure);
    }

    /// <summary>
    /// Proves: the bootstrap writer gate never reads "the store is beyond this Miller" as "there is nothing to
    /// compare". Only a family with no serving generation at all counts as a genuine first import.
    /// </summary>
    [Fact]
    public void TryReadFamilyWriterFloorSeparatesFirstImportFromAnUnreadableStore()
    {
        using var firstImport = StorePointerFixture.Create(binaryVersion: "2.34.4");
        System.IO.Directory.Delete(firstImport.StoreRoot, recursive: true);

        bool comparable = StoreArtifactVersionReader.TryReadFamilyWriterFloor(
            firstImport.Binding,
            out string? emptyFamilyVersion,
            out FamilyStoreReadException? noFailure);

        Assert.True(comparable);
        Assert.Null(emptyFamilyVersion);
        Assert.Null(noFailure);

        using var beyondThisMiller = StorePointerFixture.Create(binaryVersion: "2.34.4");
        beyondThisMiller.UpdateStoreMetadata("store_sqlite_schema_version", "99");

        bool readable = StoreArtifactVersionReader.TryReadFamilyWriterFloor(
            beyondThisMiller.Binding,
            out string? unreadableVersion,
            out FamilyStoreReadException? unreadable);

        Assert.False(readable);
        Assert.Null(unreadableVersion);
        Assert.Equal(FamilyStoreReadFailure.SchemaIncompatible, Assert.IsType<FamilyStoreReadException>(unreadable).Failure);
    }

    /// <summary>
    /// Proves: a family whose store root does not exist YET is a first import, not a corruption. This is the
    /// ordinary path for every brand-new workspace, and for the legacy-artifact migration. The store root is
    /// canonicalized before any typed check runs, so a missing directory arrives as a raw
    /// DirectoryNotFoundException; reading that as unreadable made bootstrap refuse to create any family at
    /// all, which broke four store scale tests that had nothing to do with unpublished views.
    /// </summary>
    [Fact]
    public void TryReadFamilyWriterFloorTreatsAnAbsentStoreRootAsAFirstImport()
    {
        using var fixture = StorePointerFixture.Create(binaryVersion: "2.34.4");
        System.IO.Directory.Delete(fixture.StoreRoot, recursive: true);

        bool comparable = StoreArtifactVersionReader.TryReadFamilyWriterFloor(
            fixture.Binding,
            out string? familyVersion,
            out FamilyStoreReadException? unreadable);

        Assert.True(comparable);
        Assert.Null(familyVersion);
        Assert.Null(unreadable);
        // And the verdict that follows must be eligible, whatever this Miller bundles.
        Assert.True(LeadershipEligibility.Evaluate("2.30.0", familyVersion, allowDowngrade: false).Eligible);
    }

    /// <summary>
    /// Proves: a family that HAS a store.db but cannot report its version fails CLOSED. A lost CURRENT
    /// pointer, a deleted serving generation, or a missing coord.db all say "the serving generation is
    /// unreadable", never "nothing was ever written here" — and a store.db a NEWER extractor produced may
    /// still sit under the root. Granting a blank floor there would let an older extractor write into the
    /// family and take store_meta.binary_version backwards for every member view.
    /// </summary>
    [Theory]
    [InlineData("CURRENT")]
    [InlineData("coord.db")]
    public void TryReadFamilyWriterFloorFailsClosedWhenAStoreDatabaseSurvivesTheDamage(string removed)
    {
        using var damaged = StorePointerFixture.Create(binaryVersion: "2.34.4");
        File.Delete(Path.Combine(damaged.StoreRoot, removed));
        Assert.True(File.Exists(Path.Combine(damaged.StoreRoot, "gen-001", "store.db")));

        bool comparable = StoreArtifactVersionReader.TryReadFamilyWriterFloor(
            damaged.Binding,
            out string? familyVersion,
            out FamilyStoreReadException? unreadable);

        Assert.False(comparable);
        Assert.Null(familyVersion);
        Assert.NotNull(unreadable);
    }

    /// <summary>
    /// Proves: a family version the comparison cannot parse fails the WRITER gate closed.
    /// <c>LeadershipEligibility.Evaluate</c> deliberately calls an unparseable artifact version eligible —
    /// a downgrade cannot be proven — which is right for display and for legacy artifacts, and wrong for a
    /// gate that decides whether to write into a shared family. A corrupt or foreign value there would
    /// remove the floor entirely and let any extractor write.
    /// </summary>
    [Theory]
    [InlineData("garbage")]
    [InlineData("v-next")]
    [InlineData("   ")]
    public void TryReadFamilyWriterFloorRefusesAVersionItCannotCompare(string version)
    {
        using var fixture = StorePointerFixture.Create(binaryVersion: version);

        bool comparable = StoreArtifactVersionReader.TryReadFamilyWriterFloor(
            fixture.Binding,
            out string? familyVersion,
            out FamilyStoreReadException? unreadable);

        Assert.False(comparable);
        Assert.Null(familyVersion);
        Assert.NotNull(unreadable);
        // And the leniency it corrects is real: the shared matrix WOULD have let this through.
        Assert.True(LeadershipEligibility.Evaluate("2.30.0", version, allowDowngrade: false).Eligible);
    }

    /// <summary>
    /// Proves: an EMPTY family root is still a first import. The resolver creates the store directory when
    /// it plans a view, so the ordinary first import reaches the gate with a root that exists and holds
    /// nothing. Failing closed there would wedge every new workspace.
    /// </summary>
    [Fact]
    public void TryReadFamilyWriterFloorTreatsAnEmptyStoreRootAsAFirstImport()
    {
        using var fixture = StorePointerFixture.Create(binaryVersion: "2.34.4");
        System.IO.Directory.Delete(fixture.StoreRoot, recursive: true);
        System.IO.Directory.CreateDirectory(fixture.StoreRoot);

        bool comparable = StoreArtifactVersionReader.TryReadFamilyWriterFloor(
            fixture.Binding,
            out string? familyVersion,
            out FamilyStoreReadException? unreadable);

        Assert.True(comparable);
        Assert.Null(familyVersion);
        Assert.Null(unreadable);
    }

    /// <summary>
    /// A serving family store whose <c>views</c> table omits the pointer's view id, plus the matching
    /// <c>.miller/store.json</c>. This is the reported wedge: a view PLANNED in the registry and never
    /// PUBLISHED in the family store.
    /// </summary>
    private sealed class StorePointerFixture : IDisposable
    {
        private static readonly Guid Family = Guid.Parse("11111111-1111-4111-8111-111111111111");

        private StorePointerFixture(string directory, StoreFamilyBinding binding, string legacyArtifactPath)
        {
            Directory = directory;
            Binding = binding;
            LegacyArtifactPath = legacyArtifactPath;
        }

        public string Directory { get; }

        public StoreFamilyBinding Binding { get; }

        public string StoreRoot => Binding.StoreRoot;

        /// <summary>The <c>.miller/symbols.db</c> path every reader entry point takes; it need not exist.</summary>
        public string LegacyArtifactPath { get; }

        public static StorePointerFixture Create(string binaryVersion)
        {
            string directory = Path.Combine(
                Path.GetTempPath(),
                "miller-store-version-" + Guid.NewGuid().ToString("N"));
            string workspace = Path.Combine(directory, "workspace");
            string store = Path.Combine(directory, "store");
            string generation = Path.Combine(store, "gen-001");
            System.IO.Directory.CreateDirectory(Path.Combine(workspace, ".miller"));
            System.IO.Directory.CreateDirectory(generation);
            File.WriteAllText(Path.Combine(store, "CURRENT"), "gen-001\n");
            CreateCoordinator(Path.Combine(store, "coord.db"));
            workspace = PathCanonicalizer.CanonicalizeRoot(workspace);
            store = PathCanonicalizer.CanonicalizeRoot(store);
            CreateStore(Path.Combine(generation, "store.db"), binaryVersion);

            var binding = new StoreFamilyBinding(
                Family,
                store,
                "view-never-published",
                workspace,
                StoreBindingState.Ready);
            StoreWorkspacePointer.Write(workspace, binding);
            return new StorePointerFixture(
                directory,
                binding,
                Path.Combine(workspace, ".miller", "symbols.db"));
        }

        public void UpdateStoreMetadata(string key, string value)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(StoreRoot, "gen-001", "store.db"),
                Pooling = false,
            }.ToString());
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE store_meta SET value=$value WHERE key=$key;";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (System.IO.Directory.Exists(Directory))
                System.IO.Directory.Delete(Directory, recursive: true);
        }

        private static void CreateCoordinator(string path)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Pooling = false,
            }.ToString());
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "CREATE TABLE consumer_cursors (consumer_id TEXT PRIMARY KEY, generation_name TEXT NOT NULL, " +
                "store_log_sequence INTEGER NOT NULL, updated_at INTEGER NOT NULL) STRICT;";
            command.ExecuteNonQuery();
        }

        private static void CreateStore(string path, string binaryVersion)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Pooling = false,
            }.ToString());
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            // store_meta carries every key ValidateStoreMetadata requires. The views table is deliberately
            // EMPTY: the pointer's view was planned but never published.
            command.CommandText =
                """
                CREATE TABLE store_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL) STRICT;
                INSERT INTO store_meta VALUES
                  ('family_id','11111111-1111-4111-8111-111111111111'),
                  ('store_sqlite_schema_version','2'),
                  ('store_format_epoch','1'),
                  ('min_reader_version','2.31.0'),
                  ('binary_version',$binary_version),
                  ('extraction_identity_epoch','1'),
                  ('generation_state','serving');
                CREATE TABLE views (
                  view_id TEXT PRIMARY KEY,
                  root TEXT NOT NULL,
                  current_generation INTEGER,
                  resolution_state TEXT NOT NULL,
                  resolution_base_id TEXT,
                  resolution_delta_generation INTEGER,
                  resolution_exact_at INTEGER,
                  created_at TEXT NOT NULL,
                  updated_at TEXT NOT NULL) STRICT;
                CREATE TABLE manifests (
                  view_id TEXT NOT NULL,
                  generation INTEGER NOT NULL,
                  manifest_hash TEXT NOT NULL,
                  request_id TEXT NOT NULL,
                  created_at TEXT NOT NULL,
                  PRIMARY KEY(view_id,generation)) STRICT;
                CREATE TABLE store_log (
                  sequence INTEGER PRIMARY KEY,
                  request_id TEXT NOT NULL,
                  event_kind TEXT NOT NULL,
                  view_id TEXT,
                  generation INTEGER,
                  version_id INTEGER,
                  level INTEGER,
                  terminal INTEGER NOT NULL,
                  payload_json TEXT NOT NULL,
                  created_at TEXT NOT NULL) STRICT;
                """;
            command.Parameters.AddWithValue("$binary_version", binaryVersion);
            command.ExecuteNonQuery();
        }
    }
}
