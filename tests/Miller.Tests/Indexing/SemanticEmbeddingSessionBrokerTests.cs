using System.Text;
using System.Text.Json;
using Miller.Indexing.Semantic;
using Miller.Tests.Support;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class SemanticEmbeddingSessionBrokerTests
{
    private static SemanticSessionOptions FastOptions => new()
    {
        RequestTimeout = TimeSpan.FromSeconds(10),
        InitTimeout = TimeSpan.FromSeconds(10),
        ShutdownTimeout = TimeSpan.FromSeconds(1),
        FatalThreshold = 1,
        RestartBackoff = TimeSpan.Zero,
        RestartBackoffCap = TimeSpan.Zero,
        Delay = static (_, _) => Task.CompletedTask,
    };

    [Fact]
    public async Task ConcurrentQueryAndConvergenceDemand_CreateOneSessionAndShareCircuitState()
    {
        int sessions = 0;
        await using var broker = new SemanticEmbeddingSessionBroker(
            enabled: true,
            () =>
            {
                Interlocked.Increment(ref sessions);
                return new SemanticEmbeddingSession(
                    FakeSemanticSidecar.InProcessLauncher(FakeSidecarFault.GarbageOnStdout), FastOptions);
            });

        await Task.WhenAll(
            broker.EmbedQueryAsync("query", TestContext.Current.CancellationToken),
            broker.EmbedBatchAsync(["document"], TestContext.Current.CancellationToken));

        Assert.Equal(1, sessions);
        Assert.Equal(SemanticSessionState.CircuitOpen, broker.State);
    }

    [Fact]
    public async Task DisabledBroker_NeverInvokesTheSessionFactory()
    {
        int sessions = 0;
        await using var broker = new SemanticEmbeddingSessionBroker(
            enabled: false,
            () =>
            {
                sessions++;
                return new SemanticEmbeddingSession(FakeSemanticSidecar.InProcessLauncher(), FastOptions);
            });

        SemanticEmbedOutcome query = await broker.EmbedQueryAsync(
            "query", TestContext.Current.CancellationToken);
        SemanticEmbedOutcome batch = await broker.EmbedBatchAsync(
            ["document"], TestContext.Current.CancellationToken);

        Assert.False(query.Succeeded);
        Assert.False(batch.Succeeded);
        Assert.Equal(0, sessions);
        Assert.Equal(SemanticSessionState.NotStarted, broker.State);
    }

    [Fact]
    public async Task Dispose_StopsTheOwnedSessionAndRefusesLaterDemand()
    {
        var broker = new SemanticEmbeddingSessionBroker(
            enabled: true,
            () => new SemanticEmbeddingSession(FakeSemanticSidecar.InProcessLauncher(), FastOptions));

        Assert.True((await broker.EmbedQueryAsync(
            "query", TestContext.Current.CancellationToken)).Succeeded);

        await broker.DisposeAsync();
        SemanticEmbedOutcome afterDispose = await broker.EmbedQueryAsync(
            "query", TestContext.Current.CancellationToken);

        Assert.False(afterDispose.Succeeded);
        Assert.Equal(SemanticSessionState.Stopped, broker.State);
    }

    [Fact]
    public async Task WaitingQuery_RunsBeforeTheNextConvergenceBatch()
    {
        var launcher = new GatedLauncher(FakeSemanticSidecar.InProcessLauncher());
        await using var broker = new SemanticEmbeddingSessionBroker(
            enabled: true,
            () => new SemanticEmbeddingSession(launcher, FastOptions));

        Task<SemanticEmbedOutcome> firstBatch = broker.EmbedBatchAsync(
            ["first"], TestContext.Current.CancellationToken);
        await launcher.FirstBatchWaiting;
        Task<SemanticEmbedOutcome> secondBatch = broker.EmbedBatchAsync(
            ["second"], TestContext.Current.CancellationToken);
        Task<SemanticEmbedOutcome> query = broker.EmbedQueryAsync(
            "query", TestContext.Current.CancellationToken);

        launcher.ReleaseFirstBatch();
        await Task.WhenAll(firstBatch, secondBatch, query);

        Assert.Equal(["health", "embed_batch", "embed_query", "embed_batch"], launcher.Methods);
    }

    private sealed class GatedLauncher(ISemanticSidecarLauncher inner) : ISemanticSidecarLauncher
    {
        private readonly TaskCompletionSource _firstBatchWaiting =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirstBatch =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Methods { get; } = [];

        public Task FirstBatchWaiting => _firstBatchWaiting.Task;

        public ISemanticSidecarChannel Launch() => new GatedChannel(inner.Launch(), this);

        public void ReleaseFirstBatch() => _releaseFirstBatch.TrySetResult();

        private sealed class GatedChannel(ISemanticSidecarChannel inner, GatedLauncher owner)
            : ISemanticSidecarChannel
        {
            public TextWriter StandardInput { get; } = new GatedWriter(inner.StandardInput, owner);

            public TextReader StandardOutput => inner.StandardOutput;

            public bool HasExited => inner.HasExited;

            public void Terminate() => inner.Terminate();

            public void Dispose() => inner.Dispose();
        }

        private sealed class GatedWriter(TextWriter inner, GatedLauncher owner) : TextWriter
        {
            private readonly StringBuilder _pending = new();

            public override Encoding Encoding => inner.Encoding;

            public override Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
            {
                _pending.Append(buffer.Span);
                return Task.CompletedTask;
            }

            public override async Task FlushAsync(CancellationToken cancellationToken)
            {
                string request = _pending.ToString().TrimEnd('\r', '\n');
                _pending.Clear();
                using JsonDocument json = JsonDocument.Parse(request);
                string method = json.RootElement.GetProperty("method").GetString()!;
                owner.Methods.Add(method);
                if (method == "embed_batch" && owner.Methods.Count(item => item == "embed_batch") == 1)
                {
                    owner._firstBatchWaiting.TrySetResult();
                    await owner._releaseFirstBatch.Task.WaitAsync(cancellationToken);
                }

                await inner.WriteAsync((request + "\n").AsMemory(), cancellationToken);
                await inner.FlushAsync(cancellationToken);
            }
        }
    }
}
