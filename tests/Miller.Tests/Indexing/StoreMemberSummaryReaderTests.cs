using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class StoreMemberSummaryReaderTests
{
    [Fact]
    public void ReadPrioritizesCurrentViewAndCapsDisplayLabels()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE views(view_id TEXT PRIMARY KEY, root TEXT NOT NULL);
                INSERT INTO views(view_id, root) VALUES
                    ('view-a', '/repos/alpha'),
                    ('view-b', '/repos/bravo'),
                    ('view-c', '/repos/charlie'),
                    ('view-d', '/repos/delta'),
                    ('view-e', '/repos/echo');
                """;
            command.ExecuteNonQuery();
        }

        StoreMemberSummary summary = StoreMemberSummaryReader.Read(connection, "view-c", maxLabels: 2);

        Assert.Equal(5, summary.TotalCount);
        Assert.Equal(3, summary.OmittedCount);
        Assert.Equal(
            WorkspaceId.Display("/repos/charlie", WorkspaceId.FromCanonicalRoot("/repos/charlie")),
            summary.DisplayLabels[0]);
        Assert.Equal(
            WorkspaceId.Display("/repos/alpha", WorkspaceId.FromCanonicalRoot("/repos/alpha")),
            summary.DisplayLabels[1]);
    }
}
