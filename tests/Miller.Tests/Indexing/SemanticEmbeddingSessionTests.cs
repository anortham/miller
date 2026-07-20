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
        Assert.Equal(SemanticSessionState.CircuitOpen, session.State);
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
