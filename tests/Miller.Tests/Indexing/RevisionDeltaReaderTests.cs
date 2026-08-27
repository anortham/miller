using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins <see cref="RevisionDeltaReader"/> — the per-file-revision-stamp mechanism behind the CT revision-delta
/// contract (design 2026-07-03-ct-revision-delta-design.md §1). Builds real <see cref="JulieDbFixture"/> extract
/// DBs carrying an <c>extraction_revisions</c> cursor + a <c>revision_file_changes</c> journal and asserts the
/// truthful-inclusion (R1) and honest-span-failure (R3) rules. R2 (ignore exclusion) is proven at the delta-tool
/// layer where Miller's watch/ignore policy lives (<see cref="Miller.Tests.Server.Cli.ImpactRevisionDeltaCliTests"/>).
/// </summary>
public sealed class RevisionDeltaReaderTests
{
    private const string DefaultArtifactId = "artifact-default";

    private static JulieDbFixture.SymbolRow Symbol(string id, string name, string path) =>
        new(id, name, "method", "csharp", path, $"void {name}()", 1, ParentId: null) { EndLine = 3 };

    private static JulieDbFixture Build(
        IReadOnlyList<JulieDbFixture.RevisionRow> revisions,
        IReadOnlyList<JulieDbFixture.RevisionFileChangeRow> changes,
        IReadOnlyList<JulieDbFixture.SymbolRow>? symbols = null) =>
        JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            symbols ?? new[] { Symbol("00000000000000000000000000000001", "Handle", "src/Service.cs") },
            revisions: revisions,
            fileChanges: changes);

    [Fact]
    public void Read_IncludesFilesMillerDoesNotParse_R1()
    {
        // R1 truthful inclusion: a changed file that produces NO code symbols (a data fixture Miller does not
        // parse) still appears — the delta answers "what on disk that Miller watches changed", not "what indexed".
        using JulieDbFixture fx = Build(
            revisions: new[] { new JulieDbFixture.RevisionRow(1), new JulieDbFixture.RevisionRow(2), new JulieDbFixture.RevisionRow(3) },
            changes: new[]
            {
                new JulieDbFixture.RevisionFileChangeRow(2, "src/Service.cs", "updated"),
                new JulieDbFixture.RevisionFileChangeRow(3, "fixtures/sample-data.csv", "inserted"),
            });

        RevisionDeltaResult result = RevisionDeltaReader.Read(
            fx.DbPath, fromRevision: 1, fromArtifactId: DefaultArtifactId);

        Assert.Equal(RevisionDeltaStatus.Complete, result.Status);
        Assert.Equal(3, result.ToRevision);
        Assert.Equal(1, result.FromRevision);
        Assert.Contains("src/Service.cs", result.ChangedPaths);
        Assert.Contains("fixtures/sample-data.csv", result.ChangedPaths);
    }

    [Fact]
    public void Read_RenameAppearsAsDeletePlusCreate_R1()
    {
        // A rename is journaled as a delete of the old path + an insert of the new path; both must appear.
        using JulieDbFixture fx = Build(
            revisions: new[] { new JulieDbFixture.RevisionRow(1), new JulieDbFixture.RevisionRow(2) },
            changes: new[]
            {
                new JulieDbFixture.RevisionFileChangeRow(2, "src/OldName.cs", "deleted"),
                new JulieDbFixture.RevisionFileChangeRow(2, "src/NewName.cs", "inserted"),
            });

        RevisionDeltaResult result = RevisionDeltaReader.Read(
            fx.DbPath, fromRevision: 1, fromArtifactId: DefaultArtifactId);

        Assert.Equal(RevisionDeltaStatus.Complete, result.Status);
        Assert.Contains("src/OldName.cs", result.ChangedPaths);
        Assert.Contains("src/NewName.cs", result.ChangedPaths);
    }

    [Fact]
    public void Read_FromEqualsCurrent_IsCompleteAndEmpty_NotUnavailable()
    {
        // "Complete + empty" (nothing changed since the base) is a distinct, truthful state — never unavailable.
        using JulieDbFixture fx = Build(
            revisions: new[] { new JulieDbFixture.RevisionRow(1), new JulieDbFixture.RevisionRow(2) },
            changes: new[] { new JulieDbFixture.RevisionFileChangeRow(2, "src/Service.cs", "updated") });

        RevisionDeltaResult result = RevisionDeltaReader.Read(
            fx.DbPath, fromRevision: 2, fromArtifactId: DefaultArtifactId);

        Assert.Equal(RevisionDeltaStatus.Complete, result.Status);
        Assert.Equal(2, result.ToRevision);
        Assert.Empty(result.ChangedPaths);
    }

    [Fact]
    public void Read_BaseAheadOfCurrent_IsUnavailable_R3_RebuiltIndex()
    {
        // R3: a base ahead of the current revision means the counter went backward — a full rebuild restarted
        // julie's revision counter (a new index generation). The span is unreconstructable → unavailable, never a
        // guessed-empty delta. to_revision still reports the real current revision so Eros can detect the skew.
        using JulieDbFixture fx = Build(
            revisions: new[] { new JulieDbFixture.RevisionRow(1), new JulieDbFixture.RevisionRow(2) },
            changes: new[] { new JulieDbFixture.RevisionFileChangeRow(2, "src/Service.cs", "updated") });

        RevisionDeltaResult result = RevisionDeltaReader.Read(
            fx.DbPath, fromRevision: 267, fromArtifactId: DefaultArtifactId);

        Assert.Equal(RevisionDeltaStatus.Unavailable, result.Status);
        Assert.Equal("from_after_current", result.Reason);
        Assert.Equal(2, result.ToRevision);
        Assert.Empty(result.ChangedPaths);
    }

    [Fact]
    public void Read_BaseBelowRetainedFloor_IsUnavailable_R3_PrunedHistory()
    {
        // R3: history below the base was pruned/rebuilt — revisions between the base and the retained floor are
        // unrecorded, so the span cannot be reconstructed → unavailable.
        using JulieDbFixture fx = Build(
            revisions: new[]
            {
                new JulieDbFixture.RevisionRow(10), new JulieDbFixture.RevisionRow(11), new JulieDbFixture.RevisionRow(12),
            },
            changes: new[] { new JulieDbFixture.RevisionFileChangeRow(11, "src/Service.cs", "updated") });

        RevisionDeltaResult result = RevisionDeltaReader.Read(
            fx.DbPath, fromRevision: 3, fromArtifactId: DefaultArtifactId);

        Assert.Equal(RevisionDeltaStatus.Unavailable, result.Status);
        Assert.Equal("pruned_history", result.Reason);
        Assert.Equal(12, result.ToRevision);
    }

    [Fact]
    public void Read_BaseAtRetainedFloorMinusOne_IsComplete()
    {
        // The boundary of the pruned-history rule: from == floor - 1 is the earliest base the retained journal can
        // still vouch for (every revision after it is recorded), so it is complete, not unavailable.
        using JulieDbFixture fx = Build(
            revisions: new[] { new JulieDbFixture.RevisionRow(10), new JulieDbFixture.RevisionRow(11) },
            changes: new[] { new JulieDbFixture.RevisionFileChangeRow(11, "src/Service.cs", "updated") });

        RevisionDeltaResult result = RevisionDeltaReader.Read(
            fx.DbPath, fromRevision: 9, fromArtifactId: DefaultArtifactId);

        Assert.Equal(RevisionDeltaStatus.Complete, result.Status);
        Assert.Contains("src/Service.cs", result.ChangedPaths);
    }

    [Fact]
    public void Read_MissingArtifactBase_IsUnavailable_R3_GenerationUnknown()
    {
        // R3 generation guard: a bare revision number is not enough because full rebuilds restart the counter.
        // Without the caller's base artifact_id, Miller cannot vouch that the base revision belongs to this DB
        // generation, so it must return unavailable rather than a guessed complete delta.
        using JulieDbFixture fx = Build(
            revisions: new[] { new JulieDbFixture.RevisionRow(10), new JulieDbFixture.RevisionRow(11) },
            changes: new[] { new JulieDbFixture.RevisionFileChangeRow(11, "src/Service.cs", "updated") });

        RevisionDeltaResult result = RevisionDeltaReader.Read(fx.DbPath, fromRevision: 9);

        Assert.Equal(RevisionDeltaStatus.Unavailable, result.Status);
        Assert.Equal("missing_from_artifact_id", result.Reason);
        Assert.Equal(DefaultArtifactId, result.ArtifactId);
        Assert.Empty(result.ChangedPaths);
    }

    [Fact]
    public void Read_ArtifactMismatch_IsUnavailable_R3_EvenWhenRevisionSpanLooksComplete()
    {
        // R3 generation guard: after a full rebuild, a new artifact can climb past the old numeric base. The
        // revision span can look reconstructable, but it crosses artifact generations and must be unavailable.
        using JulieDbFixture fx = Build(
            revisions: new[] { new JulieDbFixture.RevisionRow(51), new JulieDbFixture.RevisionRow(101) },
            changes: new[] { new JulieDbFixture.RevisionFileChangeRow(101, "src/Service.cs", "updated") });

        RevisionDeltaResult result = RevisionDeltaReader.Read(
            fx.DbPath, fromRevision: 50, fromArtifactId: "artifact-before-rebuild");

        Assert.Equal(RevisionDeltaStatus.Unavailable, result.Status);
        Assert.Equal("artifact_changed", result.Reason);
        Assert.Equal(101, result.ToRevision);
        Assert.Equal(DefaultArtifactId, result.ArtifactId);
        Assert.Empty(result.ChangedPaths);
    }

    [Fact]
    public void Read_MissingExtractDb_IsUnavailable_R3()
    {
        // A missing extract DB is not a crash and not an empty delta: the mechanism cannot vouch → unavailable.
        string missing = Path.Combine(Path.GetTempPath(), "miller-no-such-" + Guid.NewGuid().ToString("N"), "symbols.db");

        RevisionDeltaResult result = RevisionDeltaReader.Read(
            missing, fromRevision: 1, fromArtifactId: DefaultArtifactId);

        Assert.Equal(RevisionDeltaStatus.Unavailable, result.Status);
        Assert.Equal("no_index", result.Reason);
    }

    [Fact]
    public void Read_MissingJournalTable_IsUnavailable_R3_LegacyExtract()
    {
        // An older julie-extract artifact predating the change journal cannot serve deltas → unavailable, so Eros
        // negotiates by capability and never reads a legacy-shaped response as "complete".
        using JulieDbFixture fx = Build(
            revisions: new[] { new JulieDbFixture.RevisionRow(1), new JulieDbFixture.RevisionRow(2) },
            changes: new[] { new JulieDbFixture.RevisionFileChangeRow(2, "src/Service.cs", "updated") });
        DropTable(fx.DbPath, "revision_file_changes");

        RevisionDeltaResult result = RevisionDeltaReader.Read(
            fx.DbPath, fromRevision: 1, fromArtifactId: DefaultArtifactId);

        Assert.Equal(RevisionDeltaStatus.Unavailable, result.Status);
        Assert.Equal("no_journal", result.Reason);
    }

    [Fact]
    public void Read_StoreManifestEntryIdenticalAcrossGenerations_YieldsNoChangedPath()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Execute(
            connection,
            """
            CREATE TABLE store_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT INTO store_meta VALUES ('family_id','family-1');
            CREATE TABLE manifests (
              view_id TEXT NOT NULL,
              generation INTEGER NOT NULL,
              manifest_hash TEXT NOT NULL,
              request_id TEXT NOT NULL,
              created_at TEXT NOT NULL,
              PRIMARY KEY(view_id,generation));
            INSERT INTO manifests VALUES
              ('view-a',1,'manifest-1','request-1','2026-08-25T00:00:00Z'),
              ('view-a',2,'manifest-2','request-2','2026-08-26T00:00:00Z');
            CREATE TABLE manifest_entries (
              view_id TEXT NOT NULL,
              generation INTEGER NOT NULL,
              path TEXT NOT NULL,
              language TEXT NOT NULL,
              version_id INTEGER,
              status TEXT NOT NULL,
              observed_content_hash TEXT,
              indexed_at TEXT NOT NULL,
              error_class TEXT,
              error_json TEXT,
              PRIMARY KEY(view_id,generation,path));
            INSERT INTO manifest_entries VALUES
              ('view-a',1,'src/Unchanged.cs','csharp',7,'indexed','blake3:same','2026-08-25T00:00:00Z',NULL,NULL),
              ('view-a',2,'src/Unchanged.cs','csharp',7,'indexed','blake3:same','2026-08-26T00:00:00Z',NULL,NULL),
              ('view-a',1,'src/Touched.cs','csharp',8,'indexed','blake3:before','2026-08-25T00:00:00Z',NULL,NULL),
              ('view-a',2,'src/Touched.cs','csharp',9,'indexed','blake3:after','2026-08-26T00:00:00Z',NULL,NULL);
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
              created_at TEXT NOT NULL);
            INSERT INTO store_log VALUES
              (1,'request-1','manifest_flipped','view-a',1,NULL,NULL,1,'{}','2026-08-25T00:00:00Z'),
              (2,'request-2','manifest_flipped','view-a',2,NULL,NULL,1,'{}','2026-08-26T00:00:00Z');
            CREATE TEMP TABLE _miller_session (view_id TEXT NOT NULL, generation INTEGER NOT NULL);
            INSERT INTO _miller_session VALUES ('view-a',2);
            """);
        using var session = new RawConnectionSession(connection);

        RevisionDeltaResult result = RevisionDeltaReader.Read(session, fromRevision: 1, fromArtifactId: "family-1");

        Assert.Equal(RevisionDeltaStatus.Complete, result.Status);
        Assert.Equal(1, result.FromRevision);
        Assert.Equal(2, result.ToRevision);
        Assert.Equal(["src/Touched.cs"], result.ChangedPaths);
        Assert.DoesNotContain("src/Unchanged.cs", result.ChangedPaths);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<string>>(result.DeletedPaths));
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed class RawConnectionSession : IWorkspaceReadSession
    {
        private readonly SqliteConnection _connection;

        public RawConnectionSession(SqliteConnection connection)
        {
            _connection = connection;
        }

        public WorkspaceReadSnapshot Snapshot => throw new NotSupportedException();

        public TResult Read<TResult>(Func<SqliteConnection, TResult> query) => query(_connection);

        public void Dispose()
        {
        }
    }

    private static void DropTable(string dbPath, string table)
    {
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
        };
        using var conn = new SqliteConnection(csb.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DROP TABLE {table};";
        cmd.ExecuteNonQuery();
    }
}
