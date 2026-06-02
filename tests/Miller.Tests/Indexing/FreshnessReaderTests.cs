using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Xunit;
using Rev = Miller.Tests.Indexing.JulieDbFixture.RevisionRow;
using Fc = Miller.Tests.Indexing.JulieDbFixture.RevisionFileChangeRow;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins the M3 revision poll (decision-2). The reader holds ONE long-lived <c>Mode=ReadOnly</c> connection with
/// no lingering transaction: <see cref="FreshnessReader.LatestRevision"/> is
/// <c>SELECT MAX(revision_id) FROM extraction_revisions</c> (0 when absent; one DB = one root, no workspace
/// filter) and <see cref="FreshnessReader.ChangedSince"/> reads the v1 <c>revision_file_changes</c> delta. The
/// load-bearing contract — the one the whole "poll, never reopen" design rests on — is the no-reopen test: a
/// SECOND connection commits a new revision, and the reader's NEXT poll on its existing connection sees it.
/// </summary>
public sealed class FreshnessReaderTests
{
    private static readonly IReadOnlyList<JulieDbFixture.SymbolRow> NoSymbols =
        Array.Empty<JulieDbFixture.SymbolRow>();

    [Fact]
    public void LatestRevision_ReturnsMaxRevisionId()
    {
        using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, NoSymbols,
            revisions: new[] { new Rev(1), new Rev(2), new Rev(3) });
        using var reader = new FreshnessReader(fx.DbPath);

        Assert.Equal(3, reader.LatestRevision());
    }

    [Fact]
    public void LatestRevision_EmptyTable_ReturnsZero()
    {
        using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, NoSymbols);
        using var reader = new FreshnessReader(fx.DbPath);

        // MAX over zero rows is SQL NULL → the "no revision yet" sentinel 0 (unchanged from the old contract).
        Assert.Equal(0, reader.LatestRevision());
    }

    [Fact]
    public void LatestRevision_TwoSeparateDbs_DoNotLeak()
    {
        // v1 has one DB per root (no workspace_id column); separate roots are separate files, so a reader
        // over one DB can never observe another root's MAX. This replaces the old per-workspace scoping test.
        using var a = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, NoSymbols,
            revisions: new[] { new Rev(2) });
        using var b = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, NoSymbols,
            revisions: new[] { new Rev(9) });
        using var ra = new FreshnessReader(a.DbPath);
        using var rb = new FreshnessReader(b.DbPath);

        Assert.Equal(2, ra.LatestRevision());
        Assert.Equal(9, rb.LatestRevision());
    }

    [Fact]
    public void ChangedSince_ReturnsOnlyRowsAfterTheGivenRevision()
    {
        using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, NoSymbols,
            revisions: new[] { new Rev(1), new Rev(2), new Rev(3) },
            fileChanges: new[]
            {
                new Fc(1, "a.cs", "inserted"),
                new Fc(2, "b.cs", "updated"),
                new Fc(3, "a.cs", "deleted"),
            });
        using var reader = new FreshnessReader(fx.DbPath);

        var changes = reader.ChangedSince(1); // strictly after revision 1

        Assert.Equal(2, changes.Count);
        Assert.Contains(changes, c => c.Path == "b.cs" && c.ChangeKind == RevisionChangeKind.Modified && c.RevisionId == 2);
        Assert.Contains(changes, c => c.Path == "a.cs" && c.ChangeKind == RevisionChangeKind.Deleted && c.RevisionId == 3);
    }

    [Fact]
    public void ChangedSince_AtLatestRevision_ReturnsEmpty()
    {
        using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, NoSymbols,
            revisions: new[] { new Rev(1), new Rev(2) },
            fileChanges: new[] { new Fc(1, "a.cs", "inserted"), new Fc(2, "b.cs", "updated") });
        using var reader = new FreshnessReader(fx.DbPath);

        Assert.Empty(reader.ChangedSince(2));
    }

    [Fact]
    public void ChangedSince_ParsesAllFourV1ChangeKinds()
    {
        using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, NoSymbols,
            revisions: new[] { new Rev(1) },
            fileChanges: new[]
            {
                new Fc(1, "inserted.cs", "inserted"),
                new Fc(1, "updated.cs", "updated"),
                new Fc(1, "deleted.cs", "deleted"),
                new Fc(1, "unsupported.cs", "unsupported"),
            });
        using var reader = new FreshnessReader(fx.DbPath);

        var changes = reader.ChangedSince(0);

        Assert.Contains(changes, c => c.Path == "inserted.cs"    && c.ChangeKind == RevisionChangeKind.Added);
        Assert.Contains(changes, c => c.Path == "updated.cs"     && c.ChangeKind == RevisionChangeKind.Modified);
        Assert.Contains(changes, c => c.Path == "deleted.cs"     && c.ChangeKind == RevisionChangeKind.Deleted);
        Assert.Contains(changes, c => c.Path == "unsupported.cs" && c.ChangeKind == RevisionChangeKind.Unsupported);
    }

    [Fact]
    public void ChangedSince_UnknownChangeKind_ThrowsLoudly_NoCheckConstraintInV1()
    {
        // v1 has NO CHECK constraint on change_kind (schema.rs:47) — Miller is the only guard. Inject a drifted
        // value directly and assert the reader fails loud rather than silently misclassifying.
        using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, NoSymbols,
            revisions: new[] { new Rev(1) });
        InsertRawFileChange(fx.DbPath, revisionId: 1, fileId: "f-x", path: "x.cs", changeKind: "renamed");
        using var reader = new FreshnessReader(fx.DbPath);

        var ex = Assert.Throws<InvalidOperationException>(() => reader.ChangedSince(0));
        Assert.Contains("renamed", ex.Message, StringComparison.Ordinal);
        Assert.Contains("inserted|updated|deleted|unsupported", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Poll_AfterASecondConnectionCommitsANewRevision_SeesItWithoutReopen()
    {
        // THE load-bearing contract (decision-2): the reader keeps a single long-lived Mode=ReadOnly connection
        // with NO open explicit transaction. A separate writer connection commits a brand-new revision row; the
        // reader's NEXT LatestRevision call on its EXISTING connection observes the bump. If the reader pinned a
        // transaction snapshot (or required a reopen), this would still read 1.
        using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, NoSymbols,
            revisions: new[] { new Rev(1) });
        using var reader = new FreshnessReader(fx.DbPath);

        Assert.Equal(1, reader.LatestRevision()); // first poll: snapshot at revision 1

        // Simulate julie-extract's writer: a SECOND connection inserts revision 2 and COMMITS.
        var writeCsb = new SqliteConnectionStringBuilder
        {
            DataSource = fx.DbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
        };
        using (var writer = new SqliteConnection(writeCsb.ToString()))
        {
            writer.Open();
            using var cmd = writer.CreateCommand();
            cmd.CommandText =
                "INSERT INTO extraction_revisions " +
                "(revision_id, operation, mode, started_at, completed_at, binary_version, " +
                "extract_contract_version, sqlite_schema_version, counts_json) " +
                "VALUES (2, 'scan', 'full', '', '', '2.0.0', '1', '1', '{}');";
            cmd.ExecuteNonQuery();
        }

        // Next poll on the SAME long-lived read connection must see the writer's committed bump.
        Assert.Equal(2, reader.LatestRevision());
    }

    [Fact]
    public void Constructor_MissingDb_Throws()
    {
        string missing = Path.Combine(Path.GetTempPath(), "miller-fr-missing-" + Guid.NewGuid().ToString("N"), "symbols.db");
        Assert.Throws<FileNotFoundException>(() => new FreshnessReader(missing));
    }

    // Raw INSERT into v1 revision_file_changes, bypassing the typed Fc record so a drifted change_kind can be
    // written (v1 has no CHECK constraint — Miller is the only guard, asserted above).
    private static void InsertRawFileChange(
        string dbPath, long revisionId, string fileId, string path, string changeKind)
    {
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
        };
        using var conn = new SqliteConnection(csb.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO revision_file_changes (revision_id, file_id, path, change_kind) " +
            "VALUES ($rev, $fid, $p, $ck);";
        cmd.Parameters.AddWithValue("$rev", revisionId);
        cmd.Parameters.AddWithValue("$fid", fileId);
        cmd.Parameters.AddWithValue("$p", path);
        cmd.Parameters.AddWithValue("$ck", changeKind);
        cmd.ExecuteNonQuery();
    }
}
