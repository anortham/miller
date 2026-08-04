using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text.Json;
using Miller.Indexing;
using Miller.Indexing.Semantic;
using Miller.Server.Telemetry;
using Miller.Server.Tools;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class SharedSemanticBrokerConnectionFactoryTests : IAsyncLifetime
{
    private const string CounterVariable = "MILLER_FAKE_SHARED_BROKER_COUNTER";
    private const string DelayVariable = "MILLER_FAKE_SHARED_BROKER_DELAY_MS";
    private const string CrashFirstMarkerVariable =
        "MILLER_FAKE_SHARED_BROKER_CRASH_FIRST_MARKER";
    private const string ExitDuringDelayVariable =
        "MILLER_FAKE_SHARED_BROKER_EXIT_ON_OWNER_CLOSE_DURING_DELAY";
    private const string DegradedReasonVariable =
        "MILLER_FAKE_SHARED_BROKER_DEGRADED_REASON";
    private const string HealthDelayVariable =
        "MILLER_FAKE_SHARED_BROKER_HEALTH_DELAY_MS";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "mb-" + Guid.NewGuid().ToString("N")[..10]);
    private readonly SemanticEncoderPin _pin = SemanticEncoderSelection.Active with
    {
        ModelId = SemanticEncoderSelection.Active.ModelId + "-test-" + Guid.NewGuid().ToString("N"),
    };

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task EightFactories_ConvergeOnOneBrokerAndAllHandshake()
    {
        string counter = Path.Combine(_root, "loads.txt");
        await using BrokerFactoryGroup group = CreateGroup(8, counter, loadDelayMs: 400);

        SemanticEncoderHandshake?[] handshakes = await group.HandshakesAsync(
            TestContext.Current.CancellationToken);

        for (int i = 0; i < handshakes.Length; i++)
            Assert.True(
                handshakes[i]?.Pin.ModelSha256 == SemanticEncoderSelection.Active.ModelSha256,
                group.Sessions[i].UnavailableReason);
        Assert.Single(File.ReadAllLines(counter));
        await WaitUntilAsync(
            () => group.Factories.Count(factory => factory.Snapshot.IsOwner) == 1,
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        SharedSemanticBrokerConnectionFactory owner =
            Assert.Single(group.Factories, factory => factory.Snapshot.IsOwner);
        Assert.True(owner.Snapshot.SpawnAttempts >= 1);
        Assert.All(group.Factories, factory =>
        {
            Assert.Equal("ready", factory.Snapshot.State);
            Assert.Equal(
                SemanticEncoderSelection.Active.ModelSha256,
                factory.Snapshot.ModelSha256);
        });
    }

    [Fact]
    public async Task CpuFallbackHandshake_FlowsThroughSnapshotFactsAndRenderWithoutOwnershipConfusion()
    {
        const string degradedReason = "accelerator resource exhausted; demoted to cpu";
        string counter = Path.Combine(_root, "cpu-fallback-loads.txt");
        await using SharedSemanticBrokerConnectionFactory factory =
            CreateFactory(counter, loadDelayMs: 0, degradedReason: degradedReason);
        await using var session = new SemanticEmbeddingSession(
            factory,
            expectedEncoder: _pin,
            ownsConnectionFactory: false);

        SemanticEncoderHandshake? handshake = await session.EnsureStartedAsync(
            TestContext.Current.CancellationToken);

        Assert.NotNull(handshake);
        SemanticBrokerSnapshot snapshot = factory.Snapshot;
        Assert.Equal("cpu", snapshot.Backend);
        Assert.False(snapshot.AcceleratorLeaseHeld);
        Assert.False(snapshot.OwnershipDegraded);
        Assert.Null(snapshot.OwnershipDegradedReason);
        Assert.Equal(degradedReason, snapshot.BackendDegradedReason);

        SemanticBrokerFacts brokerFacts = SemanticBrokerFacts.From(SemanticMode.On, snapshot);
        var facts = new WorkspaceFacts(
            "/repo",
            "repo-id",
            "/repo/.miller/symbols.db",
            true,
            1,
            1,
            1,
            1,
            true,
            true,
            SemanticBroker: brokerFacts);
        string compact = WorkspaceRender.Status(facts, TelemetrySummary.Empty, json: false);
        string json = WorkspaceRender.Status(facts, TelemetrySummary.Empty, json: true);

        Assert.Contains("backend: cpu", compact);
        Assert.Contains("accelerator_lease: not_held", compact);
        Assert.Contains($"backend_degraded: {degradedReason}", compact);
        Assert.DoesNotContain("ownership_degraded:", compact);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement broker = doc.RootElement.GetProperty("semantic_broker");
        Assert.False(broker.GetProperty("ownership_degraded").GetBoolean());
        Assert.Equal(JsonValueKind.Null, broker.GetProperty("ownership_degraded_reason").ValueKind);
        Assert.Equal(
            degradedReason,
            broker.GetProperty("backend_degraded_reason").GetString());
    }

    [Fact]
    public async Task PassiveObservation_DisposesAConnectedStreamCanceledBeforeSessionAcceptance()
    {
        string counter = Path.Combine(_root, "passive-cancel-loads.txt");
        await using SharedSemanticBrokerConnectionFactory owner =
            CreateFactory(counter, loadDelayMs: 0);
        await using ISemanticSidecarConnection ownerConnection =
            await owner.ConnectAsync(TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        int disposedConnections = 0;
        await using var observer = new SharedSemanticBrokerConnectionFactory(
            RequireBrokerHostExecutable(),
            toolsRoot: _root,
            millerHome: _root,
            pin: _pin,
            environment: EnvironmentFor(
                counter,
                loadDelayMs: 0,
                crashFirstMarker: null,
                exitOnOwnerCloseDuringDelay: false),
            directConnectTimeout: TimeSpan.FromMilliseconds(30),
            initializationTimeout: TimeSpan.FromSeconds(10),
            pollInterval: TimeSpan.FromMilliseconds(20),
            requireWindowsJob: false,
            attachWindowsJob: null,
            passiveConnectionAccepted: cancellation.Cancel,
            connectionDisposed: () => Interlocked.Increment(ref disposedConnections));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await observer.ObserveExistingAsync(TimeSpan.FromMilliseconds(500), cancellation.Token));

        Assert.Equal(1, disposedConnections);
        Assert.Equal(0, observer.Snapshot.SpawnAttempts);
    }

    [Fact]
    public async Task PassiveObservation_HealthSilenceRespectsTheTotalWallClockBound()
    {
        string counter = Path.Combine(_root, "passive-timeout-loads.txt");
        await using SharedSemanticBrokerConnectionFactory owner =
            CreateFactory(counter, loadDelayMs: 0, healthDelayMs: 5_000);
        await using ISemanticSidecarConnection ownerConnection =
            await owner.ConnectAsync(TestContext.Current.CancellationToken);
        await using SharedSemanticBrokerConnectionFactory observer =
            CreateFactory(counter, loadDelayMs: 0, healthDelayMs: 5_000);
        var elapsed = Stopwatch.StartNew();

        SemanticBrokerSnapshot? snapshot = await observer.ObserveExistingAsync(
            TimeSpan.FromMilliseconds(100),
            TestContext.Current.CancellationToken);

        elapsed.Stop();
        Assert.Null(snapshot);
        Assert.InRange(elapsed.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(2));
        Assert.Equal(0, observer.Snapshot.SpawnAttempts);
    }

    [Fact]
    public async Task ColdLoadHolderDeath_ReelectsWithinTheInitializationBudget()
    {
        string counter = Path.Combine(_root, "loads.txt");
        string crashMarker = Path.Combine(_root, "crashed-first");
        await using BrokerFactoryGroup group = CreateGroup(
            8,
            counter,
            loadDelayMs: 300,
            crashFirstMarker: crashMarker);

        SemanticEncoderHandshake?[] handshakes = await group.HandshakesAsync(
            TestContext.Current.CancellationToken);

        Assert.All(handshakes, Assert.NotNull);
        Assert.Equal(2, File.ReadAllLines(counter).Length);
        await WaitUntilAsync(
            () => group.Factories.Count(factory => factory.Snapshot.IsOwner) == 1,
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task OwnerDisposalStopsItsBroker_NonOwnerDisposalDoesNot_AndSurvivorReelects()
    {
        string counter = Path.Combine(_root, "loads.txt");
        await using BrokerFactoryGroup group = CreateGroup(3, counter, loadDelayMs: 250);
        _ = await group.HandshakesAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => group.Factories.Count(factory => factory.Snapshot.IsOwner) == 1,
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        SharedSemanticBrokerConnectionFactory owner =
            Assert.Single(group.Factories, factory => factory.Snapshot.IsOwner);
        SharedSemanticBrokerConnectionFactory[] nonOwners =
            group.Factories.Where(factory => !factory.Snapshot.IsOwner).ToArray();

        await nonOwners[0].DisposeAsync();
        await using (var proof = new SemanticEmbeddingSession(
            nonOwners[1],
            expectedEncoder: _pin))
        {
            Assert.True(
                await proof.EnsureStartedAsync(TestContext.Current.CancellationToken) is not null,
                proof.UnavailableReason);
        }

        await owner.DisposeAsync();
        SemanticEmbedOutcome recovered = await group.Sessions[
                group.Factories.IndexOf(nonOwners[1])]
            .EmbedQueryAsync("re-elect", TestContext.Current.CancellationToken);

        Assert.True(recovered.Succeeded, recovered.FailureReason);
        Assert.True(nonOwners[1].Snapshot.IsOwner);
        Assert.Equal(2, File.ReadAllLines(counter).Length);
    }

    [Fact]
    public async Task ExitedOwner_IsRetiredBeforeTheReplacementOverwritesItsHandles()
    {
        string counter = Path.Combine(_root, "loads.txt");
        await using SharedSemanticBrokerConnectionFactory factory =
            CreateFactory(counter, loadDelayMs: 0);
        var firstSession = new SemanticEmbeddingSession(
            factory,
            expectedEncoder: _pin,
            ownsConnectionFactory: false);
        Assert.NotNull(await firstSession.EnsureStartedAsync(
            TestContext.Current.CancellationToken));
        int firstPid = Assert.IsType<int>(factory.Snapshot.OwnerProcessId);

        using (Process child = Process.GetProcessById(firstPid))
        {
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync(TestContext.Current.CancellationToken);
        }

        Assert.False(factory.Snapshot.IsOwner);
        Assert.Null(factory.Snapshot.OwnerProcessId);
        Assert.Equal(1, factory.Snapshot.RetiredOwnerCount);

        await firstSession.DisposeAsync();

        await using var replacementSession = new SemanticEmbeddingSession(
            factory,
            expectedEncoder: _pin,
            ownsConnectionFactory: false);
        Assert.NotNull(await replacementSession.EnsureStartedAsync(
            TestContext.Current.CancellationToken));

        Assert.NotEqual(firstPid, factory.Snapshot.OwnerProcessId);
        Assert.Equal(1, factory.Snapshot.RetiredOwnerCount);
    }

    [Fact]
    public async Task DisposeDuringColdConnect_SerializesTeardownAndReturnsFactoryDisposal()
    {
        string counter = Path.Combine(_root, "loads.txt");
        SharedSemanticBrokerConnectionFactory factory =
            CreateFactory(
                counter,
                loadDelayMs: 3000,
                exitOnOwnerCloseDuringDelay: true);
        Task<ISemanticSidecarConnection> first = factory
            .ConnectAsync(TestContext.Current.CancellationToken)
            .AsTask();
        await WaitUntilAsync(
            () => factory.Snapshot.SpawnAttempts == 1,
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        Task<ISemanticSidecarConnection> waiter = factory
            .ConnectAsync(TestContext.Current.CancellationToken)
            .AsTask();

        await factory.DisposeAsync();
        Assert.True(
            first.IsCompleted && waiter.IsCompleted,
            "factory disposal returned before active and waiting connects completed");
        Exception?[] failures = await Task.WhenAll(
                CaptureConnectFailureAsync(first),
                CaptureConnectFailureAsync(waiter))
            .WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        Assert.All(failures, failure =>
        {
            var disposed = Assert.IsType<ObjectDisposedException>(failure);
            Assert.Contains(
                nameof(SharedSemanticBrokerConnectionFactory),
                disposed.ObjectName,
                StringComparison.Ordinal);
        });
        Assert.Equal("disposed", factory.Snapshot.State);
    }

    [Fact]
    public async Task WindowsJobAttachmentFailure_IsVisibleWhileOwnerStdinRemainsAuthoritative()
    {
        string counter = Path.Combine(_root, "loads.txt");
        bool attachAttempted = false;
        SharedSemanticBrokerConnectionFactory factory = CreateFactory(
            counter,
            loadDelayMs: 0,
            requireWindowsJob: true,
            attachWindowsJob: _ =>
            {
                attachAttempted = true;
                return WindowsKillOnCloseJobAttachment.Failed("job attach denied");
            });
        await using var session = new SemanticEmbeddingSession(
            factory,
            expectedEncoder: _pin,
            ownsConnectionFactory: false);

        Assert.True(
            await session.EnsureStartedAsync(TestContext.Current.CancellationToken) is not null,
            session.UnavailableReason);
        Assert.True(attachAttempted);
        Assert.Equal("ready", factory.Snapshot.State);
        Assert.True(factory.Snapshot.IsOwner);
        Assert.True(factory.Snapshot.OwnershipDegraded);
        Assert.Equal("job attach denied", factory.Snapshot.OwnershipDegradedReason);
        int ownerPid = Assert.IsType<int>(factory.Snapshot.OwnerProcessId);
        SemanticBrokerEndpoint endpoint = SemanticBrokerEndpoint.Create(
            _root,
            _pin);

        await factory.DisposeAsync();
        await WaitUntilAsync(
            () => !ProcessExists(ownerPid),
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Assert.False(await CanConnectAsync(
            endpoint,
            TestContext.Current.CancellationToken));
    }

    private BrokerFactoryGroup CreateGroup(
        int count,
        string counter,
        int loadDelayMs,
        string? crashFirstMarker = null)
    {
        var factories = Enumerable.Range(0, count)
            .Select(_ => CreateFactory(
                counter,
                loadDelayMs,
                crashFirstMarker: crashFirstMarker))
            .ToList();
        var sessions = factories
            .Select(factory => new SemanticEmbeddingSession(
                factory,
                expectedEncoder: _pin,
                ownsConnectionFactory: false))
            .ToList();
        return new BrokerFactoryGroup(factories, sessions);
    }

    private SharedSemanticBrokerConnectionFactory CreateFactory(
        string counter,
        int loadDelayMs,
        bool requireWindowsJob = false,
        Func<Process, WindowsKillOnCloseJobAttachment>? attachWindowsJob = null,
        string? crashFirstMarker = null,
        bool exitOnOwnerCloseDuringDelay = false,
        string? degradedReason = null,
        int healthDelayMs = 0)
    {
        string executable = RequireBrokerHostExecutable();
        return new SharedSemanticBrokerConnectionFactory(
            executable,
            toolsRoot: _root,
            millerHome: _root,
            pin: _pin,
            environment: EnvironmentFor(
                counter,
                loadDelayMs,
                crashFirstMarker,
                exitOnOwnerCloseDuringDelay,
                degradedReason,
                healthDelayMs),
            directConnectTimeout: TimeSpan.FromMilliseconds(30),
            initializationTimeout: TimeSpan.FromSeconds(10),
            pollInterval: TimeSpan.FromMilliseconds(20),
            requireWindowsJob: requireWindowsJob,
            attachWindowsJob: attachWindowsJob);
    }

    private static IReadOnlyDictionary<string, string?> EnvironmentFor(
        string counter,
        int loadDelayMs,
        string? crashFirstMarker,
        bool exitOnOwnerCloseDuringDelay,
        string? degradedReason = null,
        int healthDelayMs = 0)
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [CounterVariable] = counter,
            [DelayVariable] = loadDelayMs.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
        };
        if (crashFirstMarker is not null)
            environment[CrashFirstMarkerVariable] = crashFirstMarker;
        if (exitOnOwnerCloseDuringDelay)
            environment[ExitDuringDelayVariable] = "1";
        if (degradedReason is not null)
            environment[DegradedReasonVariable] = degradedReason;
        if (healthDelayMs > 0)
            environment[HealthDelayVariable] = healthDelayMs.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        return environment;
    }

    private static string RequireBrokerHostExecutable()
    {
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string executable = OperatingSystem.IsWindows()
            ? "Miller.SharedBrokerTestHost.exe"
            : "Miller.SharedBrokerTestHost";
        string hostRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "Miller.SharedBrokerTestHost",
            "bin"));
        string? candidate = new[] { configuration, "Release", "Debug" }
            .Distinct(StringComparer.Ordinal)
            .Select(value => Path.Combine(hostRoot, value, "net10.0", executable))
            .FirstOrDefault(File.Exists);
        Assert.True(
            candidate is not null,
            $"The shared broker test host was not built under {hostRoot}.");
        return candidate!;
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition did not become true");
            await Task.Delay(20, cancellationToken);
        }
    }

    private static async Task<Exception?> CaptureConnectFailureAsync(
        Task<ISemanticSidecarConnection> connect)
    {
        try
        {
            await using ISemanticSidecarConnection connection = await connect;
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static bool ProcessExists(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task<bool> CanConnectAsync(
        SemanticBrokerEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(100));
        try
        {
            if (OperatingSystem.IsWindows())
            {
                await using var pipe = new NamedPipeClientStream(
                    ".",
                    endpoint.WindowsPipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous);
                await pipe.ConnectAsync(timeout.Token);
                return pipe.IsConnected;
            }

            using var socket = new Socket(
                AddressFamily.Unix,
                SocketType.Stream,
                ProtocolType.Unspecified);
            await socket.ConnectAsync(
                new UnixDomainSocketEndPoint(endpoint.UnixSocketPath),
                timeout.Token);
            return socket.Connected;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private sealed class BrokerFactoryGroup(
        List<SharedSemanticBrokerConnectionFactory> factories,
        List<SemanticEmbeddingSession> sessions) : IAsyncDisposable
    {
        public List<SharedSemanticBrokerConnectionFactory> Factories { get; } = factories;

        public List<SemanticEmbeddingSession> Sessions { get; } = sessions;

        public Task<SemanticEncoderHandshake?[]> HandshakesAsync(CancellationToken cancellationToken) =>
            Task.WhenAll(Sessions.Select(session => session.EnsureStartedAsync(cancellationToken)));

        public async ValueTask DisposeAsync()
        {
            foreach (SemanticEmbeddingSession session in Sessions)
                await session.DisposeAsync();
            foreach (SharedSemanticBrokerConnectionFactory factory in Factories)
                await factory.DisposeAsync();
        }
    }
}
