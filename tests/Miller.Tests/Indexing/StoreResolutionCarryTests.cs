using Microsoft.Data.Sqlite;
using Miller.Indexing.Store;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class StoreResolutionCarryTests
{
    [Theory]
    [InlineData("[]", false)]
    [InlineData("[\"\"]", false)]
    [InlineData("[\"Type\"]", true)]
    [InlineData("not-json", true)]
    [InlineData("{}", true)]
    public void HasResolveKeyTreatsOnlyEmptyNameArraysAsNoKeys(string json, bool expected)
    {
        Assert.Equal(expected, StoreResolutionCarry.HasResolveKey(json));
    }

    [Fact]
    public void MissingStoreDoesNotCarry()
    {
        using var dir = new TempDir("missing-carry");
        Assert.False(StoreResolutionCarry.TryCarryExactWhenNoResolveKeys(dir.Root, "view-a"));
    }

    [Fact]
    public void EmptyTouchedNamesRestoresExactAndAdvancesThePredecessor()
    {
        using var dir = new TempDir("empty-names");
        string storeRoot = dir.Root;
        CreateFamily(storeRoot, touchedNames: "[]", viewState: "unbound", exactAt: null);

        Assert.True(StoreResolutionCarry.TryCarryExactWhenNoResolveKeys(storeRoot, "view-a"));

        using var connection = Open(Path.Combine(storeRoot, "gen-001", "store.db"));
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT v.resolution_state, v.resolution_base_id, v.resolution_delta_generation,
                   v.resolution_exact_at, s.predecessor_manifest_generation, s.predecessor_manifest_hash
            FROM views v
            JOIN resolution_scope_state s ON s.view_id = v.view_id
            WHERE v.view_id = 'view-a'
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("exact", reader.GetString(0));
        Assert.Equal("base-1", reader.GetString(1));
        Assert.Equal(1, reader.GetInt64(2));
        Assert.Equal(2, reader.GetInt64(3));
        Assert.Equal(2, reader.GetInt64(4));
        Assert.Equal("hash-2", reader.GetString(5));
        Assert.True(StoreWalCheckpoint.IsOwed(storeRoot));
    }

    [Fact]
    public void NamedTouchedKeysDoNotCarry()
    {
        using var dir = new TempDir("named-keys");
        CreateFamily(dir.Root, touchedNames: "[\"App\"]", viewState: "unbound", exactAt: null);

        Assert.False(StoreResolutionCarry.TryCarryExactWhenNoResolveKeys(dir.Root, "view-a"));

        using var connection = Open(Path.Combine(dir.Root, "gen-001", "store.db"));
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT resolution_state, resolution_exact_at FROM views WHERE view_id='view-a'";
        using SqliteDataReader reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("unbound", reader.GetString(0));
        Assert.True(reader.IsDBNull(1));
    }

    [Fact]
    public void AlreadyExactViewDoesNotCarry()
    {
        using var dir = new TempDir("already-exact");
        CreateFamily(dir.Root, touchedNames: "[]", viewState: "exact", exactAt: 2);

        Assert.False(StoreResolutionCarry.TryCarryExactWhenNoResolveKeys(dir.Root, "view-a"));
    }

    private static void CreateFamily(string storeRoot, string touchedNames, string viewState, long? exactAt)
    {
        Directory.CreateDirectory(Path.Combine(storeRoot, "gen-001"));
        File.WriteAllText(Path.Combine(storeRoot, "CURRENT"), "gen-001");
        string database = Path.Combine(storeRoot, "gen-001", "store.db");
        using var connection = Open(database);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE views (
              view_id TEXT PRIMARY KEY,
              current_generation INTEGER,
              resolution_state TEXT,
              resolution_base_id TEXT,
              resolution_delta_generation INTEGER,
              resolution_exact_at INTEGER
            );
            CREATE TABLE resolution_scope_batches (
              transition_id INTEGER PRIMARY KEY,
              view_id TEXT,
              scope_usable INTEGER,
              to_manifest_generation INTEGER
            );
            CREATE TABLE resolution_scope_journal (
              transition_id INTEGER,
              path TEXT,
              touched_names_json TEXT
            );
            CREATE TABLE resolution_scope_state (
              view_id TEXT PRIMARY KEY,
              predecessor_manifest_generation INTEGER,
              predecessor_manifest_hash TEXT,
              base_id TEXT,
              delta_generation INTEGER,
              resolver_output_epoch INTEGER,
              current_manifest_generation INTEGER,
              current_manifest_hash TEXT,
              journal_through_transition_id INTEGER
            );
            INSERT INTO views
              (view_id, current_generation, resolution_state, resolution_base_id,
               resolution_delta_generation, resolution_exact_at)
            VALUES ('view-a', 2, $state, NULL, NULL, $exact);
            INSERT INTO resolution_scope_batches
              (transition_id, view_id, scope_usable, to_manifest_generation)
            VALUES (7, 'view-a', 1, 2);
            INSERT INTO resolution_scope_journal
              (transition_id, path, touched_names_json)
            VALUES (7, 'README.md', $names);
            INSERT INTO resolution_scope_state
              (view_id, predecessor_manifest_generation, predecessor_manifest_hash, base_id,
               delta_generation, resolver_output_epoch, current_manifest_generation,
               current_manifest_hash, journal_through_transition_id)
            VALUES ('view-a', 1, 'hash-1', 'base-1', 1, 1, 2, 'hash-2', 7);
            """;
        command.Parameters.AddWithValue("$state", viewState);
        command.Parameters.AddWithValue("$exact", exactAt.HasValue ? exactAt.Value : DBNull.Value);
        command.Parameters.AddWithValue("$names", touchedNames);
        command.ExecuteNonQuery();
    }

    private static SqliteConnection Open(string database)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir(string label)
        {
            Root = Path.Combine(Path.GetTempPath(), "miller-resolution-carry-" + label + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
