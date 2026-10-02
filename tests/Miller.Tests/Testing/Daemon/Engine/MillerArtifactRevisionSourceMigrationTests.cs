using Miller.Indexing.Reads;
using Miller.Indexing.Store;
using Miller.Tests.Support;
using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Daemon.Engine;

[Collection(StoreEnvironmentCollection.Name)]
public sealed class MillerArtifactRevisionSourceMigrationTests : IDisposable
{
    private readonly string? _originalStoreMode =
        Environment.GetEnvironmentVariable(WorkspaceReadSessionFactory.StoreEnvironmentVariable);

    public MillerArtifactRevisionSourceMigrationTests() =>
        Environment.SetEnvironmentVariable(WorkspaceReadSessionFactory.StoreEnvironmentVariable, "off");

    [Fact]
    public async Task Retained_store_pointer_reports_source_rebuild_required_without_reader_calls()
    {
        using StoreFixture fixture = StoreFixture.Create();
        StoreWorkspacePointer.Write(fixture.Binding.WorkspaceRoot, fixture.Binding);
        Assert.True(WorkspaceReadSessionFactory.StorePointerRequiresSourceRebuild(fixture.Binding.WorkspaceRoot));

        var calls = new List<string>();
        using var registry = new StoreReaderRegistrationRegistry(startScheduler: false);
        using IDisposable scope = StoreReaderRegistrationContext.Use(fixture.Binding.StoreRoot,
            new(new StoreReaderRegistrationRunner((args, _) =>
            {
                calls.Add(args[2]);
                return fixture.ReaderReply(args);
            }), registry));

        var poller = new ContinuousTestRevisionPoller(new MillerArtifactRevisionSource());
        var request = new ContinuousTestRevisionPollRequest(
            "workspace-a",
            fixture.Binding.WorkspaceRoot,
            [],
            new RecordingEnqueuer());
        ContinuousTestRevisionReadResult read = await poller.ReadAsync(request, TestContext.Current.CancellationToken);
        ContinuousTestRevisionPollResult poll = poller.ApplyRead(request, read);

        Assert.Equal("source_rebuild_required", read.Status);
        Assert.False(read.Observation!.IndexFresh);
        Assert.StartsWith("BindingNotReady:", read.Observation.Reason, StringComparison.Ordinal);
        Assert.Contains("still has a store pointer", read.Observation.Reason, StringComparison.Ordinal);
        Assert.Equal("source_rebuild_required", poll.Status);
        Assert.Contains("BindingNotReady:", poll.Reason, StringComparison.Ordinal);
        Assert.Contains("still has a store pointer", poll.Reason, StringComparison.Ordinal);
        Assert.Empty(calls);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task Missing_legacy_artifact_remains_generic_degradation()
    {
        string root = Directory.CreateTempSubdirectory("miller-ct-store-refusal-").FullName;
        try
        {
            Assert.False(WorkspaceReadSessionFactory.StorePointerRequiresSourceRebuild(root));

            var poller = new ContinuousTestRevisionPoller(new MillerArtifactRevisionSource());
            var request = new ContinuousTestRevisionPollRequest(
                "workspace-a",
                root,
                [],
                new RecordingEnqueuer());
            ContinuousTestRevisionReadResult read = await poller.ReadAsync(request, TestContext.Current.CancellationToken);
            ContinuousTestRevisionPollResult poll = poller.ApplyRead(request, read);

            Assert.Equal("degraded", read.Status);
            Assert.Null(read.Observation!.Reason);
            Assert.Equal("degraded", poll.Status);
            Assert.Equal("degraded", poll.Reason);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Explicit_store_on_refusal_keeps_its_pinned_capability_message()
    {
        Environment.SetEnvironmentVariable(WorkspaceReadSessionFactory.StoreEnvironmentVariable, "on");
        string root = Directory.CreateTempSubdirectory("miller-ct-store-mode-refusal-").FullName;
        try
        {
            var poller = new ContinuousTestRevisionPoller(new MillerArtifactRevisionSource());
            var request = new ContinuousTestRevisionPollRequest(
                "workspace-a",
                root,
                [],
                new RecordingEnqueuer());
            ContinuousTestRevisionReadResult read = await poller.ReadAsync(request, TestContext.Current.CancellationToken);
            ContinuousTestRevisionPollResult poll = poller.ApplyRead(request, read);

            Assert.Equal("degraded", read.Status);
            Assert.Contains("MILLER_INDEX_STORE=on is unsupported by pinned julie-extract", read.Reason, StringComparison.Ordinal);
            Assert.Equal("degraded", poll.Status);
            Assert.StartsWith("degraded: MILLER_INDEX_STORE=on is unsupported", poll.Reason, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public void Dispose() =>
        Environment.SetEnvironmentVariable(WorkspaceReadSessionFactory.StoreEnvironmentVariable, _originalStoreMode);
}
