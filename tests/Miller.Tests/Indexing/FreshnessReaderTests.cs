using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Xunit;
using Rev = Miller.Tests.Indexing.JulieDbFixture.RevisionRow;
using Fc = Miller.Tests.Indexing.JulieDbFixture.RevisionFileChangeRow;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins the M3 revision poll (decision-2 / verified-fact 1, 5). The reader holds ONE long-lived
/// <c>Mode=ReadOnly</c> connection with no lingering transaction: <see cref="FreshnessReader.LatestRevision"/>
/// is <c>SELECT MAX(revision) ... WHERE workspace_id=@id</c> (0 when absent) and
/// <see cref="FreshnessReader.ChangedSince"/> reads the <c>revision_file_changes</c> delta. The load-bearing
/// contract — the one the whole "poll, never reopen" design rests on — is the last test: a SECOND connection
/// commits a new revision, and the reader's NEXT poll on its existing connection sees it (no reopen).
/// </summary>
public sealed class FreshnessReaderTests
{
    private const string Ws = "ws-fresh-001";
    private const string Other = "ws-other-999";

    private static readonly IReadOnlyList<JulieDbFixture.SymbolRow> NoSymbols =
        Array.Empty<JulieDbFixture.SymbolRow>();

    [Fact]
    public void LatestRevision_ReturnsMaxForTheWorkspace()
    {
        using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, NoSymbols, workspaceId: Ws,
            revisions: new[] { new Rev(1, Ws), new Rev(2, Ws), new Rev(3, Ws) });
        using var reader = new FreshnessReader(fx.DbPath);

        Assert.Equal(3, reader.LatestRevision(Ws));
    }

    [Fact]
    public void LatestRevision_IsScopedByWorkspaceId_DoesNotLeakAcrossWorkspaces()
    {
        // Two workspaces in one DB: each must see only its own MAX, never the other's higher revision.
        using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, NoSymbols, workspaceId: Ws,
            revisions: new[] { new Rev(2, Ws), new Rev(9, Other) });
        using var reader = new FreshnessReader(fx.DbPath);

        Assert.Equal(2, reader.LatestRevision(Ws));
        Assert.Equal(9, reader.LatestRevision(Other));
    }

    [Fact]
    public void LatestRevision_UnknownWorkspace_ReturnsZero()
    {
        using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, NoSymbols, workspaceId: Ws,
            revisions: new[] { new Rev(5, Ws) });
        using var reader = new FreshnessReader(fx.DbPath);

        // No rows for this id → MAX(revision) is SQL NULL → the reader maps that to 0 (the "no revision yet"
        // sentinel), NOT an exception and NOT the other workspace's value.
        Assert.Equal(0, reader.LatestRevision("ws-does-not-exist"));
    }

    [Fact]
    public void LatestRevision_EmptyTable_ReturnsZero()
    {
        using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, NoSymbols, workspaceId: Ws); // no revision rows
        using var reader = new FreshnessReader(fx.DbPath);

        Assert.Equal(0, reader.LatestRevision(Ws));
    }

    [Fact]
    public void ChangedSince_ReturnsOnlyRowsAfterTheGivenRevision_ForTheWorkspace()
    {
        using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, NoSymbols, workspaceId: Ws,
            revisions: new[] { new Rev(1, Ws), new Rev(2, Ws), new Rev(3, Ws) },
            fileChanges: new[]
            {
                new Fc(1, Ws, "a.cs", "added"),
                new Fc(2, Ws, "b.cs", "modified"),
                new Fc(3, Ws, "a.cs", "deleted"),
                new Fc(3, Other, "leak.cs", "added"), // different workspace → must NOT appear
            });
        using var reader = new FreshnessReader(fx.DbPath);

        var changes = reader.ChangedSince(1, Ws); // strictly after revision 1

        Assert.Equal(2, changes.Count);
        Assert.Contains(changes, c => c.FilePath == "b.cs" && c.ChangeKind == RevisionChangeKind.Modified && c.Revision == 2);
        Assert.Contains(changes, c => c.FilePath == "a.cs" && c.ChangeKind == RevisionChangeKind.Deleted && c.Revision == 3);
        Assert.DoesNotContain(changes, c => c.FilePath == "leak.cs");
    }

    [Fact]
    public void ChangedSince_AtLatestRevision_ReturnsEmpty()
    {
        using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, NoSymbols, workspaceId: Ws,
            revisions: new[] { new Rev(1, Ws), new Rev(2, Ws) },
            fileChanges: new[] { new Fc(1, Ws, "a.cs", "added"), new Fc(2, Ws, "b.cs", "modified") });
        using var reader = new FreshnessReader(fx.DbPath);

        Assert.Empty(reader.ChangedSince(2, Ws));
    }

    [Fact]
    public void ChangedSince_ParsesAllThreeChangeKinds()
    {
        using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, NoSymbols, workspaceId: Ws,
            revisions: new[] { new Rev(1, Ws) },
            fileChanges: new[]
            {
                new Fc(1, Ws, "added.cs", "added"),
                new Fc(1, Ws, "mod.cs", "modified"),
                new Fc(1, Ws, "del.cs", "deleted"),
            });
        using var reader = new FreshnessReader(fx.DbPath);

        var changes = reader.ChangedSince(0, Ws);

        Assert.Contains(changes, c => c.FilePath == "added.cs" && c.ChangeKind == RevisionChangeKind.Added);
        Assert.Contains(changes, c => c.FilePath == "mod.cs" && c.ChangeKind == RevisionChangeKind.Modified);
        Assert.Contains(changes, c => c.FilePath == "del.cs" && c.ChangeKind == RevisionChangeKind.Deleted);
    }

    [Fact]
    public void Poll_AfterASecondConnectionCommitsANewRevision_SeesItWithoutReopen()
    {
        // THE load-bearing contract (decision-2 / verified-fact 8): the reader keeps a single long-lived
        // Mode=ReadOnly connection with NO open explicit transaction. A separate writer connection commits a
        // brand-new revision row; the reader's NEXT LatestRevision call on its EXISTING connection observes the
        // bump. If the reader pinned a transaction snapshot (or required a reopen), this would still read 1.
        using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, NoSymbols, workspaceId: Ws,
            revisions: new[] { new Rev(1, Ws) });
        using var reader = new FreshnessReader(fx.DbPath);

        Assert.Equal(1, reader.LatestRevision(Ws)); // first poll: snapshot at revision 1

        // Simulate julie-server's writer: a SECOND connection inserts revision 2 and COMMITS.
        var writeCsb = new SqliteConnectionStringBuilder
        {
            DataSource = fx.DbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
        };
        using (var writer = new SqliteConnection(writeCsb.ToString()))
        {
            writer.Open();
            using var cmd = writer.CreateCommand();
            cmd.CommandText =
                "INSERT INTO canonical_revisions " +
                "(revision, workspace_id, kind, created_at) VALUES (2, $ws, 'incremental', 0);";
            cmd.Parameters.AddWithValue("$ws", Ws);
            cmd.ExecuteNonQuery();
        }

        // Next poll on the SAME long-lived read connection must see the writer's committed bump.
        Assert.Equal(2, reader.LatestRevision(Ws));
    }

    [Fact]
    public void Constructor_MissingDb_Throws()
    {
        string missing = Path.Combine(Path.GetTempPath(), "miller-fr-missing-" + Guid.NewGuid().ToString("N"), "symbols.db");
        Assert.Throws<FileNotFoundException>(() => new FreshnessReader(missing));
    }

    [Fact]
    public void LatestRevision_NullWorkspaceId_Throws()
    {
        using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, NoSymbols, workspaceId: Ws);
        using var reader = new FreshnessReader(fx.DbPath);
        Assert.Throws<ArgumentNullException>(() => reader.LatestRevision(null!));
    }
}
