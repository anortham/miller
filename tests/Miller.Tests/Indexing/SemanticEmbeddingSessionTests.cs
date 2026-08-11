using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Miller.Indexing.Semantic;
using Miller.Tests.Support;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class SemanticEmbeddingSessionTests
{
    private static readonly SemanticEncoderPin Pin = MillerSemanticContract.DefaultEncoder;

    // Generous budgets everywhere except the stall test: a timeout is the thing under test in exactly one
    // place, and a tight budget elsewhere would turn a loaded parallel suite into a flake.
    private static SemanticSessionOptions FastOptions => new()
    {
        RequestTimeout = TimeSpan.FromSeconds(10),
        InitTimeout = TimeSpan.FromSeconds(10),
        ShutdownTimeout = TimeSpan.FromSeconds(1),
        RestartBackoff = TimeSpan.Zero,
        RestartBackoffCap = TimeSpan.Zero,
        Delay = static (_, _) => Task.CompletedTask,
    };

    private static SemanticSessionOptions StallOptions =>
        FastOptions with { RequestTimeout = TimeSpan.FromMilliseconds(300) };

    [Fact]
    public async Task Handshake_RecordsThePinnedEncoderFingerprint()
    {
        await using var session = new SemanticEmbeddingSession(FakeSemanticSidecar.InProcessLauncher(), FastOptions);

        SemanticEncoderHandshake? handshake = await session.EnsureStartedAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(handshake);
        Assert.Equal(MillerSemanticContract.EncoderFingerprint(Pin), handshake.EncoderFingerprint);
        Assert.Equal(Pin.Dims, handshake.Dims);
        Assert.Equal(SemanticSessionState.Ready, session.State);
        Assert.Null(session.UnavailableReason);
    }

    [Fact]
    public async Task EmbedBatch_RoundTripsDeterministicUnitNormVectors()
    {
        await using var session = new SemanticEmbeddingSession(FakeSemanticSidecar.InProcessLauncher(), FastOptions);
        string[] texts = ["public sealed class VectorStore", "converge the search sidecar", ""];

        SemanticEmbedOutcome outcome = await session.EmbedBatchAsync(texts, TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded, outcome.FailureReason);
        Assert.Equal(texts.Length, outcome.Vectors.Count);
        Assert.Empty(outcome.FlaggedIndices);
        for (int i = 0; i < texts.Length; i++)
        {
            Assert.Equal(Pin.Dims, outcome.Vectors[i].Length);
            Assert.Equal(1.0, Norm(outcome.Vectors[i]), SemanticEmbeddingSession.NormTolerance);
        }

        Assert.Equal(
            FakeSemanticSidecar.ExpectedVector("document", texts[0], Pin.Dims),
            outcome.Vectors[0]);
        Assert.Equal(
            FakeSemanticSidecar.ExpectedVector("document", "[empty]", Pin.Dims),
            outcome.Vectors[2]);
    }

    [Fact]
    public async Task EmbedQuery_UsesTheQueryRoleSoItDiffersFromTheDocumentVector()
    {
        await using var session = new SemanticEmbeddingSession(FakeSemanticSidecar.InProcessLauncher(), FastOptions);

        SemanticEmbedOutcome outcome = await session.EmbedQueryAsync("workspace refresh", TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded, outcome.FailureReason);
        float[] vector = Assert.Single(outcome.Vectors);
        Assert.Equal(FakeSemanticSidecar.ExpectedVector("query", "workspace refresh", Pin.Dims), vector);
        Assert.NotEqual(FakeSemanticSidecar.ExpectedVector("document", "workspace refresh", Pin.Dims), vector);
    }

    [Fact]
    public async Task AcceleratedEmbed_RefreshesSnapshotWhenTheBrokerDemotesToCpu()
    {
        await using var factory = new RecordingConnectionFactory(
            FakeSemanticSidecar.InProcessLauncher(FakeSidecarFault.AcceleratorDemotion));
        await using var session = new SemanticEmbeddingSession(factory, FastOptions);

        SemanticEmbedOutcome outcome =
            await session.EmbedQueryAsync("workspace refresh", TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded, outcome.FailureReason);
        Assert.Equal(2, factory.Handshakes.Count);
        Assert.True(factory.Handshakes[0].Accelerated);
        Assert.True(factory.Handshakes[0].AcceleratorLeaseHeld);
        Assert.False(factory.Handshakes[1].Accelerated);
        Assert.False(factory.Handshakes[1].AcceleratorLeaseHeld);
        Assert.Equal("cpu", factory.Handshakes[1].ResolvedBackend);
        Assert.Equal(
            "accelerator resource exhausted; permanently demoted to CPU",
            factory.Handshakes[1].DegradedReason);
        Assert.Same(factory.Handshakes[1], session.Handshake);
    }

    [Fact]
    public async Task AcceleratedEmbed_RefreshFailureReconnectsOnceWithoutChargingTheCircuit()
    {
        await using var session = new SemanticEmbeddingSession(
            FakeSemanticSidecar.SequencedLauncher(
                FakeSidecarFault.AcceleratorRefreshFailure,
                FakeSidecarFault.None),
            FastOptions);

        SemanticEmbedOutcome first =
            await session.EmbedQueryAsync("first", TestContext.Current.CancellationToken);
        SemanticEmbedOutcome second =
            await session.EmbedQueryAsync("second", TestContext.Current.CancellationToken);

        Assert.True(first.Succeeded, first.FailureReason);
        Assert.True(second.Succeeded, second.FailureReason);
        Assert.Equal(1, session.RestartCount);
        Assert.Equal(SemanticSessionState.Ready, session.State);
    }

    [Fact]
    public async Task AcceleratedReconnect_RefreshesALaterCpuDemotion()
    {
        await using var factory = new RecordingConnectionFactory(
            FakeSemanticSidecar.SequencedLauncher(
                FakeSidecarFault.AcceleratorRefreshFailure,
                FakeSidecarFault.AcceleratorDemotion));
        await using var session = new SemanticEmbeddingSession(factory, FastOptions);

        SemanticEmbedOutcome first =
            await session.EmbedQueryAsync("first", TestContext.Current.CancellationToken);
        SemanticEmbedOutcome second =
            await session.EmbedQueryAsync("second", TestContext.Current.CancellationToken);

        Assert.True(first.Succeeded, first.FailureReason);
        Assert.True(second.Succeeded, second.FailureReason);
        SemanticEncoderHandshake handshake = Assert.IsType<SemanticEncoderHandshake>(session.Handshake);
        Assert.Equal("cpu", handshake.ResolvedBackend);
        Assert.False(handshake.Accelerated);
        Assert.False(handshake.AcceleratorLeaseHeld);
        Assert.Equal(
            "accelerator resource exhausted; permanently demoted to CPU",
            handshake.DegradedReason);
    }

    [Fact]
    public async Task CanceledRuntimeHealth_AbortsTheStreamAndTheNextEmbedReconnects()
    {
        await using var factory = new RecordingConnectionFactory(
            FakeSemanticSidecar.SequencedLauncher(
                FakeSidecarFault.AcceleratorRefreshDelay,
                FakeSidecarFault.None));
        await using var session = new SemanticEmbeddingSession(factory, FastOptions);
        Assert.NotNull(await session.EnsureStartedAsync(TestContext.Current.CancellationToken));
        await factory.WaitForMethodAsync("health", TestContext.Current.CancellationToken);

        using var cancellation = new CancellationTokenSource();
        Task<SemanticEmbedOutcome> firstTask =
            session.EmbedQueryAsync("first", cancellation.Token);
        await factory.WaitForMethodAsync("embed_query", TestContext.Current.CancellationToken);
        await factory.WaitForMethodAsync("health", TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        SemanticEmbedOutcome first = await firstTask;

        Assert.True(first.Succeeded, first.FailureReason);
        Assert.Equal(1, session.RestartCount);
        Assert.Equal(SemanticSessionState.Restarting, session.State);

        SemanticEmbedOutcome second =
            await session.EmbedQueryAsync("second", TestContext.Current.CancellationToken);

        Assert.True(second.Succeeded, second.FailureReason);
        Assert.Equal(2, factory.ConnectCount);
        Assert.Equal(1, session.RestartCount);
        Assert.Equal(SemanticSessionState.Ready, session.State);
    }

    [Fact]
    public async Task EmptyBatch_IsNotAnError()
    {
        await using var session = new SemanticEmbeddingSession(FakeSemanticSidecar.InProcessLauncher(), FastOptions);

        SemanticEmbedOutcome outcome = await session.EmbedBatchAsync([], TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded, outcome.FailureReason);
        Assert.Empty(outcome.Vectors);
    }

    [Fact]
    public async Task PoisonItem_IsAZeroVectorFlaggedByIndexWhileTheBatchSucceeds()
    {
        await using var session = new SemanticEmbeddingSession(
            FakeSemanticSidecar.InProcessLauncher(FakeSidecarFault.PoisonItem, [1]),
            FastOptions);

        SemanticEmbedOutcome outcome = await session.EmbedBatchAsync(
            ["alpha", "beta", "gamma"], TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded, outcome.FailureReason);
        Assert.Equal(3, outcome.Vectors.Count);
        Assert.Equal([1], outcome.FlaggedIndices);
        Assert.All(outcome.Vectors[1], component => Assert.Equal(0f, component));
        Assert.Equal(SemanticSessionState.Ready, session.State);
    }

    [Fact]
    public async Task Stall_EndsInABoundedTimeoutRatherThanAHang()
    {
        await using var session = new SemanticEmbeddingSession(
            FakeSemanticSidecar.InProcessLauncher(FakeSidecarFault.StallForever),
            StallOptions);

        SemanticEmbedOutcome outcome = await session.EmbedBatchAsync(["alpha"], TestContext.Current.CancellationToken);

        Assert.False(outcome.Succeeded);
        Assert.Contains("no response", outcome.FailureReason);
        Assert.True(session.RestartCount >= 1);
    }

    [Fact]
    public async Task GarbageOnStdout_FailsLoudlyAndNeverMisparses()
    {
        await using var session = new SemanticEmbeddingSession(
            FakeSemanticSidecar.InProcessLauncher(FakeSidecarFault.GarbageOnStdout),
            FastOptions);

        SemanticEmbedOutcome outcome = await session.EmbedBatchAsync(["alpha"], TestContext.Current.CancellationToken);

        Assert.False(outcome.Succeeded);
        Assert.Contains("not decodable JSON", outcome.FailureReason);
        Assert.Empty(outcome.Vectors);
    }

    [Fact]
    public async Task RequestIdDesync_IsTreatedAsAStreamFaultNotAnApplicationError()
    {
        await using var session = new SemanticEmbeddingSession(
            FakeSemanticSidecar.InProcessLauncher(FakeSidecarFault.RequestIdDesync),
            FastOptions);

        SemanticEmbedOutcome outcome = await session.EmbedBatchAsync(["alpha"], TestContext.Current.CancellationToken);

        Assert.False(outcome.Succeeded);
        Assert.Contains("desync", outcome.FailureReason);
        Assert.True(session.RestartCount >= 1);
    }

    [Fact]
    public async Task ErrorEnvelope_FailsTheRequestWithoutRestartingTheChild()
    {
        await using var session = new SemanticEmbeddingSession(
            FakeSemanticSidecar.InProcessLauncher(FakeSidecarFault.ErrorEnvelope),
            FastOptions);

        SemanticEmbedOutcome outcome = await session.EmbedBatchAsync(["alpha"], TestContext.Current.CancellationToken);

        Assert.False(outcome.Succeeded);
        Assert.Contains("[internal_error]", outcome.FailureReason);
        Assert.Equal(0, session.RestartCount);
        Assert.Equal(SemanticSessionState.Ready, session.State);
    }

    [Fact]
    public async Task RepeatedApplicationErrors_NeverOpenTheCircuit()
    {
        await using var session = new SemanticEmbeddingSession(
            FakeSemanticSidecar.InProcessLauncher(FakeSidecarFault.ErrorEnvelope),
            FastOptions);

        for (int i = 0; i < 5; i++)
            await session.EmbedBatchAsync(["alpha"], TestContext.Current.CancellationToken);

        Assert.Equal(SemanticSessionState.Ready, session.State);
        Assert.Equal(0, session.RestartCount);
    }

    [Fact]
    public async Task CrashMidBatch_RestartsWithinTheCallAndTheRetrySucceeds()
    {
        await using var session = new SemanticEmbeddingSession(
            FakeSemanticSidecar.SequencedLauncher(FakeSidecarFault.CrashMidBatch, FakeSidecarFault.None),
            FastOptions);

        SemanticEmbedOutcome crashed = await session.EmbedBatchAsync(["alpha"], TestContext.Current.CancellationToken);
        SemanticEmbedOutcome recovered = await session.EmbedBatchAsync(["alpha"], TestContext.Current.CancellationToken);

        Assert.True(crashed.Succeeded, crashed.FailureReason);
        Assert.True(recovered.Succeeded, recovered.FailureReason);
        Assert.Equal(1, session.RestartCount);
        Assert.Equal(
            FakeSemanticSidecar.ExpectedVector("document", "alpha", Pin.Dims),
            recovered.Vectors[0]);
    }

    [Fact]
    public async Task TransportFailure_AbortsOnlyTheConnectionThenReconnectsThroughTheFactory()
    {
        await using var factory = new RecordingConnectionFactory(
            FakeSemanticSidecar.SequencedLauncher(FakeSidecarFault.CrashMidBatch, FakeSidecarFault.None));
        await using var session = new SemanticEmbeddingSession(factory, FastOptions);

        SemanticEmbedOutcome outcome =
            await session.EmbedBatchAsync(["alpha"], TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded, outcome.FailureReason);
        Assert.Equal(2, factory.ConnectCount);
        Assert.True(factory.Connections[0].Aborted);
        Assert.True(factory.Connections[0].Disposed);
        Assert.False(factory.Connections[1].Aborted);
    }

    [Fact]
    public async Task Dispose_ClosesTheConnectionWithoutDisposingABorrowedFactoryOrSendingShutdown()
    {
        await using var factory = new RecordingConnectionFactory(FakeSemanticSidecar.InProcessLauncher());
        var session = new SemanticEmbeddingSession(factory, FastOptions, ownsConnectionFactory: false);
        await session.EnsureStartedAsync(TestContext.Current.CancellationToken);

        await session.DisposeAsync();

        Assert.True(Assert.Single(factory.Connections).Disposed);
        Assert.False(factory.Disposed);
        Assert.DoesNotContain("shutdown", factory.Methods);
    }

    [Fact]
    public async Task Dispose_DisposesAnExplicitlyOwnedFactory()
    {
        var factory = new RecordingConnectionFactory(FakeSemanticSidecar.InProcessLauncher());
        var session = new SemanticEmbeddingSession(factory, FastOptions, ownsConnectionFactory: true);
        await session.EnsureStartedAsync(TestContext.Current.CancellationToken);

        await session.DisposeAsync();

        Assert.True(factory.Disposed);
    }

    [Fact]
    public async Task ThreeConsecutiveTransportFailures_OpenTheCircuitWithAStatedReason()
    {
        await using var session = new SemanticEmbeddingSession(
            FakeSemanticSidecar.InProcessLauncher(FakeSidecarFault.CrashMidBatch),
            FastOptions);

        for (int i = 0; i < 3; i++)
            await session.EmbedBatchAsync(["alpha"], TestContext.Current.CancellationToken);

        Assert.Equal(SemanticSessionState.CircuitOpen, session.State);
        Assert.Contains("consecutive failures", session.UnavailableReason);

        SemanticEmbedOutcome afterOpen = await session.EmbedBatchAsync(["alpha"], TestContext.Current.CancellationToken);
        Assert.False(afterOpen.Succeeded);
        Assert.Equal(session.UnavailableReason, afterOpen.FailureReason);
    }

    [Fact]
    public async Task ModelNotPrepared_IsAStatedRefusalWithNoRestartLoop()
    {
        await using var session = new SemanticEmbeddingSession(
            FakeSemanticSidecar.InProcessLauncher(FakeSidecarFault.ModelNotPrepared),
            FastOptions);

        SemanticEmbedOutcome outcome = await session.EmbedBatchAsync(["alpha"], TestContext.Current.CancellationToken);

        Assert.False(outcome.Succeeded);
        Assert.Contains("model_not_prepared", outcome.FailureReason);
        Assert.Equal(SemanticSessionState.ModelNotPrepared, session.State);
        Assert.Equal(0, session.RestartCount);
    }

    [Fact]
    public async Task ModelNotPrepared_EmbedsFailFastUntilAnExplicitReadinessProbeRecoversTheSession()
    {
        await using var factory = new RecordingConnectionFactory(
            FakeSemanticSidecar.SequencedLauncher(
                FakeSidecarFault.ModelNotPrepared,
                FakeSidecarFault.None));
        await using var session = new SemanticEmbeddingSession(factory, FastOptions);

        SemanticEmbedOutcome first =
            await session.EmbedBatchAsync(["alpha"], TestContext.Current.CancellationToken);
        SemanticEmbedOutcome parked =
            await session.EmbedBatchAsync(["beta"], TestContext.Current.CancellationToken);

        Assert.False(first.Succeeded);
        Assert.False(parked.Succeeded);
        Assert.Equal(1, factory.ConnectCount);
        Assert.Equal(SemanticSessionState.ModelNotPrepared, session.State);
        Assert.Null(session.Handshake);
        Assert.Equal(0, session.RestartCount);

        SemanticEncoderHandshake? handshake =
            await session.EnsureStartedAsync(TestContext.Current.CancellationToken);
        SemanticEmbedOutcome recovered =
            await session.EmbedBatchAsync(["gamma"], TestContext.Current.CancellationToken);

        Assert.NotNull(handshake);
        Assert.True(recovered.Succeeded, recovered.FailureReason);
        Assert.Equal(2, factory.ConnectCount);
        Assert.Equal(SemanticSessionState.Ready, session.State);
        Assert.Equal(0, session.RestartCount);
    }

    [Fact]
    public async Task DisposedSession_RefusesFurtherCallsInsteadOfRelaunching()
    {
        var session = new SemanticEmbeddingSession(FakeSemanticSidecar.InProcessLauncher(), FastOptions);
        await session.EnsureStartedAsync(TestContext.Current.CancellationToken);
        await session.DisposeAsync();

        SemanticEmbedOutcome outcome = await session.EmbedBatchAsync(["alpha"], TestContext.Current.CancellationToken);

        Assert.False(outcome.Succeeded);
        Assert.Equal(SemanticSessionState.Stopped, session.State);
    }

    [Theory]
    [InlineData("qwen3-0.6b-f16", 384, "dims")]
    [InlineData("some-other-model", 512, "not a pinned Miller encoder")]
    public void MatchEncoder_RefusesAHandshakeThatDisagreesWithThePin(string modelId, int dims, string expectedReason)
    {
        var health = ReadyHealth() with { ModelId = modelId, Dims = dims };

        SemanticEncoderHandshake? handshake = SemanticEmbeddingSession.MatchEncoder(health, out string? reason);

        Assert.Null(handshake);
        Assert.Contains(expectedReason, reason);
    }

    [Fact]
    public void MatchEncoder_AcceptsAHandshakeThatOmitsTheAdditiveKeys()
    {
        var health = new SemanticSidecarHealth(
            Ready: true,
            Dims: Pin.Dims,
            ModelId: Pin.ModelId,
            ModelSha256: "",
            ModelRevision: "",
            Pooling: "",
            Normalization: "",
            ResolvedBackend: "",
            Accelerated: false,
            DegradedReason: null);

        SemanticEncoderHandshake? handshake = SemanticEmbeddingSession.MatchEncoder(health, out string? reason);

        Assert.Null(reason);
        Assert.NotNull(handshake);
        Assert.Equal(MillerSemanticContract.EncoderFingerprint(Pin), handshake.EncoderFingerprint);
    }

    [Fact]
    public void MatchEncoder_RefusesAWrongModelHashEvenWhenEveryOtherFieldAgrees()
    {
        var health = ReadyHealth() with { ModelSha256 = new string('a', 64) };

        SemanticEncoderHandshake? handshake = SemanticEmbeddingSession.MatchEncoder(health, out string? reason);

        Assert.Null(handshake);
        Assert.Contains("model_sha256", reason);
    }

    [Fact]
    public void MatchEncoder_RefusesAKnownEncoderThatIsNotTheExpectedPin()
    {
        SemanticEncoderPin other = MillerSemanticContract.FallbackEncoder;
        var health = new SemanticSidecarHealth(
            Ready: true,
            Dims: other.Dims,
            ModelId: other.ModelId,
            ModelSha256: other.ModelSha256,
            ModelRevision: other.ModelRevision,
            Pooling: other.Pooling,
            Normalization: "l2",
            ResolvedBackend: "cpu",
            Accelerated: false,
            DegradedReason: null);

        SemanticEncoderHandshake? handshake = SemanticEmbeddingSession.MatchEncoder(health, Pin, out string? reason);

        Assert.Null(handshake);
        Assert.Contains(other.ModelId, reason);
        Assert.Contains(Pin.ModelId, reason);
    }

    [Fact]
    public void MatchEncoder_WithExpectedPin_AcceptsTheExpectedEncoder()
    {
        SemanticEncoderHandshake? handshake = SemanticEmbeddingSession.MatchEncoder(ReadyHealth(), Pin, out string? reason);

        Assert.Null(reason);
        Assert.NotNull(handshake);
    }

    [Fact]
    public void MatchEncoder_WithInjectedEvaluationPin_AcceptsAnEncoderOutsideProductionSelection()
    {
        SemanticEncoderPin evaluationPin = SemanticEvaluationAdapter.CodeRankEncoder;
        var health = new SemanticSidecarHealth(
            Ready: true,
            Dims: evaluationPin.Dims,
            ModelId: evaluationPin.ModelId,
            ModelSha256: evaluationPin.ModelSha256,
            ModelRevision: evaluationPin.ModelRevision,
            Pooling: evaluationPin.Pooling,
            Normalization: "l2",
            ResolvedBackend: "mps",
            Accelerated: true,
            DegradedReason: null);

        SemanticEncoderHandshake? handshake =
            SemanticEmbeddingSession.MatchEncoder(health, evaluationPin, out string? reason);

        Assert.Null(reason);
        Assert.NotNull(handshake);
        Assert.Same(evaluationPin, handshake.Pin);
        Assert.Equal(MillerSemanticContract.EncoderFingerprint(evaluationPin), handshake.EncoderFingerprint);
    }

    [Fact]
    public void ForServe_StdioFactoryPassesTheExplicitServeVerbAndSelectedModel()
    {
        var factory = StdioSemanticSidecarConnectionFactory.ForServe("/tools/julie-semantic-sidecar", Pin);

        Assert.Equal(["serve", "--model", Pin.ModelId], factory.Arguments);
    }

    [Fact]
    public void AbsentSidecarExecutable_MakesThisVeryTestReportSkippedInsteadOfFailed()
    {
        FakeSemanticSidecar.RequireSidecarExecutable(located: null);
        Assert.Fail("The guard must skip on an absent executable, so this line is unreachable.");
    }

    [Fact]
    public void SidecarExecutable_IsPresentInANormalBuildSoTheScaleTestsActuallyRun()
    {
        Assert.NotNull(FakeSemanticSidecar.LocateSidecarExecutable());
    }

    private static SemanticSidecarHealth ReadyHealth() => new(
        Ready: true,
        Dims: Pin.Dims,
        ModelId: Pin.ModelId,
        ModelSha256: Pin.ModelSha256,
        ModelRevision: Pin.ModelRevision,
        Pooling: Pin.Pooling,
        Normalization: "l2",
        ResolvedBackend: "cpu",
        Accelerated: false,
        DegradedReason: null);

    private static double Norm(float[] vector) =>
        Math.Sqrt(vector.Sum(component => (double)component * component));

    private sealed class RecordingConnectionFactory(ISemanticSidecarConnectionFactory inner)
        : ISemanticSidecarConnectionFactory, ISemanticBrokerSnapshotRecorder
    {
        private readonly Channel<string> _observedMethods = Channel.CreateUnbounded<string>();

        public List<RecordingConnection> Connections { get; } = [];

        public List<string> Methods { get; } = [];

        public List<SemanticEncoderHandshake> Handshakes { get; } = [];

        public int ConnectCount { get; private set; }

        public bool Disposed { get; private set; }

        public async ValueTask<ISemanticSidecarConnection> ConnectAsync(CancellationToken cancellationToken)
        {
            ConnectCount++;
            ISemanticSidecarConnection connection = await inner.ConnectAsync(cancellationToken);
            var recording = new RecordingConnection(connection, RecordMethod);
            Connections.Add(recording);
            return recording;
        }

        public async Task WaitForMethodAsync(string method, CancellationToken cancellationToken)
        {
            while (await _observedMethods.Reader.WaitToReadAsync(cancellationToken))
            {
                while (_observedMethods.Reader.TryRead(out string? observed))
                {
                    if (observed == method)
                        return;
                }
            }

            throw new InvalidOperationException($"Method '{method}' was not observed.");
        }

        public async ValueTask DisposeAsync()
        {
            Disposed = true;
            await inner.DisposeAsync();
        }

        public void RecordHandshake(SemanticEncoderHandshake handshake) => Handshakes.Add(handshake);

        private void RecordMethod(string method)
        {
            Methods.Add(method);
            if (!_observedMethods.Writer.TryWrite(method))
                throw new InvalidOperationException($"Method '{method}' could not be recorded.");
        }
    }

    private sealed class RecordingConnection : ISemanticSidecarConnection
    {
        private readonly ISemanticSidecarConnection _inner;

        public RecordingConnection(ISemanticSidecarConnection inner, Action<string> methodObserved)
        {
            _inner = inner;
            Input = new RecordingWriter(inner.Input, methodObserved);
        }

        public TextWriter Input { get; }

        public TextReader Output => _inner.Output;

        public bool IsClosed => _inner.IsClosed;

        public bool Aborted { get; private set; }

        public bool Disposed { get; private set; }

        public void Abort()
        {
            Aborted = true;
            _inner.Abort();
        }

        public async ValueTask DisposeAsync()
        {
            Disposed = true;
            await _inner.DisposeAsync();
        }
    }

    private sealed class RecordingWriter(TextWriter inner, Action<string> methodObserved) : TextWriter
    {
        private readonly StringBuilder _pending = new();

        public override Encoding Encoding => inner.Encoding;

        public override async Task WriteAsync(
            ReadOnlyMemory<char> buffer,
            CancellationToken cancellationToken = default)
        {
            _pending.Append(buffer.Span);
            await inner.WriteAsync(buffer, cancellationToken);
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            string request = _pending.ToString().TrimEnd('\r', '\n');
            _pending.Clear();
            using JsonDocument json = JsonDocument.Parse(request);
            methodObserved(json.RootElement.GetProperty("method").GetString()!);
            await inner.FlushAsync(cancellationToken);
        }
    }
}

/// <summary>
/// The same session driven against the fake running as a REAL child process, which is the only shape that
/// exercises process launch, stdio pipes, stdout purity, and kill-on-dispose. Scale-tagged because it spawns a
/// subprocess; it SKIPS (never fails) when the test apphost is absent.
/// </summary>
[Trait("Category", "Scale")]
public sealed class SemanticEmbeddingSessionProcessTests
{
    private static readonly SemanticEncoderPin Pin = MillerSemanticContract.DefaultEncoder;

    private static SemanticSessionOptions ProcessOptions => new()
    {
        RequestTimeout = TimeSpan.FromSeconds(5),
        InitTimeout = TimeSpan.FromSeconds(30),
        ShutdownTimeout = TimeSpan.FromMilliseconds(500),
        RestartBackoff = TimeSpan.FromMilliseconds(20),
        RestartBackoffCap = TimeSpan.FromMilliseconds(50),
    };

    [Fact]
    public async Task RealChildProcess_HandshakesAndRoundTripsABatch()
    {
        string executable = FakeSemanticSidecar.RequireSidecarExecutable();
        await using var session = new SemanticEmbeddingSession(
            FakeSemanticSidecar.ProcessLauncher(executable), ProcessOptions);

        SemanticEncoderHandshake? handshake = await session.EnsureStartedAsync(TestContext.Current.CancellationToken);
        SemanticEmbedOutcome outcome = await session.EmbedBatchAsync(
            ["alpha", "beta"], TestContext.Current.CancellationToken);

        Assert.NotNull(handshake);
        Assert.Equal(MillerSemanticContract.EncoderFingerprint(Pin), handshake.EncoderFingerprint);
        Assert.True(outcome.Succeeded, outcome.FailureReason);
        Assert.Equal(
            FakeSemanticSidecar.ExpectedVector("document", "alpha", Pin.Dims),
            outcome.Vectors[0]);
    }

    [Fact]
    public async Task RealChildProcess_StallEndsInABoundedTimeout()
    {
        string executable = FakeSemanticSidecar.RequireSidecarExecutable();
        var options = ProcessOptions with { RequestTimeout = TimeSpan.FromMilliseconds(400) };
        await using var session = new SemanticEmbeddingSession(
            FakeSemanticSidecar.ProcessLauncher(executable, FakeSidecarFault.StallForever), options);

        SemanticEmbedOutcome outcome = await session.EmbedBatchAsync(["alpha"], TestContext.Current.CancellationToken);

        Assert.False(outcome.Succeeded);
        Assert.Contains("no response", outcome.FailureReason);
    }

    [Fact]
    public async Task RealChildProcess_CrashRestartsAndRecovers()
    {
        string executable = FakeSemanticSidecar.RequireSidecarExecutable();
        await using var session = new SemanticEmbeddingSession(
            FakeSemanticSidecar.ProcessLauncher(executable, FakeSidecarFault.CrashMidBatch), ProcessOptions);

        SemanticEmbedOutcome outcome = await session.EmbedBatchAsync(["alpha"], TestContext.Current.CancellationToken);

        Assert.False(outcome.Succeeded);
        Assert.True(session.RestartCount >= 1);
    }

    [Fact]
    public async Task RealChildProcess_ShutdownStopsTheChildCleanly()
    {
        string executable = FakeSemanticSidecar.RequireSidecarExecutable();
        await using var session = new SemanticEmbeddingSession(
            FakeSemanticSidecar.ProcessLauncher(executable), ProcessOptions);
        await session.EnsureStartedAsync(TestContext.Current.CancellationToken);

        await session.ShutdownAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SemanticSessionState.Stopped, session.State);
    }
}
