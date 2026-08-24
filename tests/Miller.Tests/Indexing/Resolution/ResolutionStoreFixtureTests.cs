using Microsoft.Data.Sqlite;
using Xunit;

namespace Miller.Tests.Indexing.Resolution;

public sealed class ResolutionStoreFixtureTests
{
    [Fact]
    public void WriteTransaction_UsesOneWriteConnectionForAllFixtureRows()
    {
        using ResolutionStoreFixture fixture = ResolutionStoreFixture.Create();
        int connectionsBeforeTransaction = fixture.WriteConnectionOpenCount;

        fixture.WriteTransaction(() =>
        {
            fixture.AddFile(1, "src/App.cs");
            fixture.AddSymbol(1, "symbol", "App", "class", "src/App.cs");
            fixture.AddTypeFact(1, "type-fact", "symbol", "App");
            fixture.AddIdentifier(1, "identifier", "App", "src/App.cs");
            fixture.AddPending(1, "pending", "symbol", "App", "src/App.cs");
            fixture.AddRelationship(1, "relationship", "symbol", "symbol", "src/App.cs");
        });

        Assert.Equal(connectionsBeforeTransaction + 1, fixture.WriteConnectionOpenCount);

        using SqliteConnection connection = fixture.OpenRead();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM symbols WHERE symbol_id='symbol';";
        Assert.Equal(1L, command.ExecuteScalar());
    }
}
