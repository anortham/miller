using Microsoft.Data.Sqlite;
using Miller.Indexing.Store;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class StoreCoordinatorQueueReaderTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000);

    private readonly string _storeRoot =
        Path.Combine(Path.GetTempPath(), "miller-coord-queue-" + Guid.NewGuid().ToString("N"));

    public StoreCoordinatorQueueReaderTests() => Directory.CreateDirectory(_storeRoot);

    public void Dispose() => Directory.Delete(_storeRoot, recursive: true);

    [Fact]
    public void AMissingCoordinatorReadsAsNoFacts()
    {
        Assert.Null(Read());
        Assert.Null(StoreCoordinatorQueueReader.Read(storeRoot: null));
    }

    [Fact]
    public void AMalformedCoordinatorReadsAsNoFacts()
    {
        File.WriteAllText(CoordinatorPath, "not a database");

        Assert.Null(Read());
    }

    [Fact]
    public void AQueueWithNoPendingWorkReadsAsNoFacts()
    {
        CreateCoordinator();
        Insert("r-1", "update", "committed", createdAt: Now.ToUnixTimeMilliseconds());

        Assert.Null(Read());
    }

    [Fact]
    public void AFreshlyQueuedRequestIsReportedButNotWedged()
    {
        CreateCoordinator();
        Insert("r-1", "update", "queued", Now.AddSeconds(-30).ToUnixTimeMilliseconds());

        StoreCoordinatorQueueFacts facts = Assert.IsType<StoreCoordinatorQueueFacts>(Read());

        Assert.False(facts.Wedged);
        Assert.Equal(1, facts.QueuedCount);
        Assert.Equal(0, facts.ClaimedCount);
        Assert.Equal(30, facts.OldestQueuedAgeSeconds);
        Assert.Null(facts.DeadClaimOwner);
        Assert.Equal([new StoreCoordinatorQueueGroup("update", "queued", 1)], facts.Groups);
    }

    [Fact]
    public void AQueuedRequestOlderThanTheThresholdIsWedged()
    {
        CreateCoordinator();
        Insert("r-1", "update", "queued", Now.AddSeconds(-3600).ToUnixTimeMilliseconds());
        Insert("r-2", "import", "queued", Now.AddSeconds(-60).ToUnixTimeMilliseconds());

        StoreCoordinatorQueueFacts facts = Assert.IsType<StoreCoordinatorQueueFacts>(Read());

        Assert.True(facts.Wedged);
        Assert.Equal(2, facts.QueuedCount);
        Assert.Equal(3600, facts.OldestQueuedAgeSeconds);
        Assert.Contains("no writer", facts.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void AClaimWhoseOwnerIsGoneIsWedgedHoweverYoungItIs()
    {
        CreateCoordinator();
        Insert("r-1", "update", "claimed", Now.ToUnixTimeMilliseconds(), claimOwner: "cli-4242");

        StoreCoordinatorQueueFacts facts = Assert.IsType<StoreCoordinatorQueueFacts>(Read(alive: false));

        Assert.True(facts.Wedged);
        Assert.Equal(0, facts.QueuedCount);
        Assert.Equal(1, facts.ClaimedCount);
        Assert.Equal("cli-4242", facts.DeadClaimOwner);
        Assert.Null(facts.OldestQueuedAgeSeconds);
    }

    [Fact]
    public void AClaimWhoseOwnerIsAliveIsNotWedged()
    {
        CreateCoordinator();
        Insert("r-1", "update", "claimed", Now.ToUnixTimeMilliseconds(), claimOwner: "cli-4242");

        StoreCoordinatorQueueFacts facts = Assert.IsType<StoreCoordinatorQueueFacts>(Read(alive: true));

        Assert.False(facts.Wedged);
        Assert.Null(facts.DeadClaimOwner);
    }

    [Fact]
    public void AnOwnerCarryingNoPidIsNeverCalledDead()
    {
        CreateCoordinator();
        Insert("r-1", "update", "claimed", Now.ToUnixTimeMilliseconds(), claimOwner: "writer");

        StoreCoordinatorQueueFacts facts = Assert.IsType<StoreCoordinatorQueueFacts>(Read(alive: false));

        Assert.False(facts.Wedged);
        Assert.Null(facts.DeadClaimOwner);
    }

    [Theory]
    [InlineData("cli-4242", 4242)]
    [InlineData("miller-store-7", 7)]
    [InlineData("writer", null)]
    [InlineData("cli-", null)]
    [InlineData("cli-0", null)]
    [InlineData("cli-12x", null)]
    [InlineData("cli--3", 3)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void OwnerPidsAreReadOnlyWhenTheyAreUnambiguous(string? owner, int? expected)
    {
        Assert.Equal(expected, StoreCoordinatorQueueReader.TryReadOwnerPid(owner));
    }

    [Theory]
    [InlineData(null, StoreCoordinatorQueueReader.DefaultWedgedAfterSeconds)]
    [InlineData("", StoreCoordinatorQueueReader.DefaultWedgedAfterSeconds)]
    [InlineData("nope", StoreCoordinatorQueueReader.DefaultWedgedAfterSeconds)]
    [InlineData("-5", StoreCoordinatorQueueReader.DefaultWedgedAfterSeconds)]
    [InlineData("0", 0L)]
    [InlineData(" 45 ", 45L)]
    public void TheWedgeThresholdOverrideIsParsedOnce(string? raw, long expected)
    {
        Assert.Equal(expected, StoreCoordinatorQueueReader.ParseWedgedAfterSeconds(raw));
    }

    private StoreCoordinatorQueueFacts? Read(bool alive = true) =>
        StoreCoordinatorQueueReader.Read(
            _storeRoot,
            Now,
            _ => alive,
            StoreCoordinatorQueueReader.DefaultWedgedAfterSeconds);

    private string CoordinatorPath => Path.Combine(_storeRoot, "coord.db");

    private void CreateCoordinator()
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE requests (
              request_id TEXT PRIMARY KEY,
              idempotency_key TEXT NOT NULL,
              kind TEXT NOT NULL,
              payload_json TEXT NOT NULL,
              state TEXT NOT NULL,
              requester_id TEXT NOT NULL,
              claim_owner TEXT,
              claim_heartbeat_at INTEGER,
              created_at INTEGER NOT NULL,
              updated_at INTEGER NOT NULL
            ) STRICT;
            """;
        command.ExecuteNonQuery();
    }

    private void Insert(
        string requestId,
        string kind,
        string state,
        long createdAt,
        string? claimOwner = null)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO requests
              (request_id, idempotency_key, kind, payload_json, state, requester_id,
               claim_owner, claim_heartbeat_at, created_at, updated_at)
            VALUES ($id, $id, $kind, '{}', $state, 'miller', $owner, $heartbeat, $created, $created);
            """;
        command.Parameters.AddWithValue("$id", requestId);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$owner", (object?)claimOwner ?? DBNull.Value);
        command.Parameters.AddWithValue("$heartbeat", claimOwner is null ? DBNull.Value : createdAt);
        command.Parameters.AddWithValue("$created", createdAt);
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = CoordinatorPath,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }
}
