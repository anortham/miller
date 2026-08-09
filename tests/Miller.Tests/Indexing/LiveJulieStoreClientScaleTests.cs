using Microsoft.Data.Sqlite;
using Miller.Indexing.Store;
using Xunit;

namespace Miller.Tests.Indexing;

[Trait("Category", "Scale")]
public sealed class LiveJulieStoreClientScaleTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "miller-store-client-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ImportUpdateResolveExportDeleteAndIdempotentRetryRoundTrip()
    {
        string binary = ScaleTestSupport.RequireJulieServer();
        string root = Path.Combine(_directory, "root");
        string store = Path.Combine(_directory, "family");
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, "sample.cs"),
            "namespace Sample; public static class Value { public static int Get() => 1; }");

        string family = Guid.NewGuid().ToString("D");
        var client = new JulieStoreClient(binary, TimeSpan.FromSeconds(60));
        var scan = new StoreScanControls([], 1, null, null, Environment.ProcessId);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var import = new StoreImportRequest(
            store,
            family,
            "view-a",
            root,
            StoreLevel.L1,
            Controls("import-a", "import-key"),
            scan,
            null);

        StoreRequestResult imported = client.Submit(import, cancellationToken);
        StoreRequestResult retried = client.Submit(import with
        {
            Request = Controls("import-retry", "import-key"),
        }, cancellationToken);

        Assert.Equal(StoreRequestState.Committed, imported.State);
        Assert.Equal(StoreLevel.L1, imported.RequestedLevel);
        Assert.True(imported.Completion.L1);
        Assert.False(imported.Completion.L2);
        Assert.Equal(imported.Request, retried.Request);
        Assert.Equal(imported.Manifest, retried.Manifest);

        File.WriteAllText(
            Path.Combine(root, "sample.cs"),
            "namespace Sample; public static class Value { public static int Get() => 2; }");
        StoreRequestResult updated = client.Submit(new StoreUpdateRequest(
            store,
            family,
            "view-a",
            root,
            "sample.cs",
            StoreLevel.Full,
            Controls("update-a", "update-key"),
            scan), cancellationToken);
        StoreRequestResult resolved = client.Submit(new StoreResolveRequest(
            store,
            family,
            "view-a",
            Controls("resolve-a", "resolve-key")), cancellationToken);
        string exportPath = Path.Combine(_directory, "view-a.db");
        StoreRequestResult exported = client.Submit(new StoreExportRequest(
            store,
            family,
            "view-a",
            exportPath), cancellationToken);
        StoreRequestResult deleted = client.Submit(new StoreDeleteRequest(
            store,
            family,
            "view-a",
            root,
            ["sample.cs"],
            Controls("delete-a", "delete-key")), cancellationToken);

        Assert.True(updated.Completion.L1);
        Assert.True(updated.Completion.L2);
        Assert.True(updated.Completion.L3);
        Assert.Equal(StoreResolutionState.Exact, resolved.Resolution.State);
        Assert.True(resolved.Resolution.ExactAtMatches);
        Assert.Equal(Path.GetFileName(exportPath), Path.GetFileName(exported.Export?.Output));
        Assert.True(File.Exists(exported.Export?.Output));
        Assert.Equal(0, deleted.RowCounts.FileVersions);
        Assert.Equal(StoreRequestState.Committed, deleted.State);
    }

    [Fact]
    public void RequestTimeoutLeavesDurableWorkForASuccessor()
    {
        string binary = ScaleTestSupport.RequireJulieServer();
        string root = Path.Combine(_directory, "root");
        string store = Path.Combine(_directory, "family");
        Directory.CreateDirectory(root);
        string sourcePath = Path.Combine(root, "sample.cs");
        File.WriteAllText(sourcePath, "namespace Sample; public sealed class First { }");

        string family = Guid.NewGuid().ToString("D");
        var client = new JulieStoreClient(binary, TimeSpan.FromSeconds(60));
        var scan = new StoreScanControls([], 1, null, null, Environment.ProcessId);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        client.Submit(new StoreImportRequest(
            store,
            family,
            "view-a",
            root,
            StoreLevel.L1,
            Controls("seed", "seed-key"),
            scan,
            null), cancellationToken);

        File.WriteAllText(sourcePath, "namespace Sample; public sealed class Second { }");
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(store, "coord.db"),
        }.ToString()))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO writer_lease(
                    resource, holder_id, holder_pid, holder_version,
                    fencing_token, heartbeat_at, expires_at)
                VALUES('store-writer', 'live-test-holder', $pid, '2.31.0', 9001, $now, $expires)
                """;
            command.Parameters.AddWithValue("$pid", Environment.ProcessId);
            command.Parameters.AddWithValue("$now", now);
            command.Parameters.AddWithValue("$expires", now + 60_000);
            command.ExecuteNonQuery();
        }

        var update = new StoreUpdateRequest(
            store,
            family,
            "view-a",
            root,
            "sample.cs",
            StoreLevel.Full,
            new StoreRequestControls("timed-out", "timeout-key", TimeSpan.FromSeconds(1)),
            scan);
        StoreRequestResult timedOut = client.Submit(update, cancellationToken);

        Assert.Equal(1, timedOut.ExitCode);
        Assert.Equal(new StoreFailureClass("request_timeout"), timedOut.Failure.Class);
        Assert.True(timedOut.State is StoreRequestState.Queued or StoreRequestState.Claimed);

        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(store, "coord.db"),
        }.ToString()))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DELETE FROM writer_lease WHERE resource = 'store-writer'";
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        StoreRequestResult completed = client.Submit(update with
        {
            Request = new StoreRequestControls("successor", "timeout-key", TimeSpan.FromSeconds(30)),
        }, cancellationToken);

        Assert.Equal(StoreRequestState.Committed, completed.State);
        Assert.Equal("timed-out", completed.Request.Id);
        Assert.True(completed.Completion.L3);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static StoreRequestControls Controls(string requestId, string idempotencyKey) =>
        new(requestId, idempotencyKey, TimeSpan.FromSeconds(30));
}
