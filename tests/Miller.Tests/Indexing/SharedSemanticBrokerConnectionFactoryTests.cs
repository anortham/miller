using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using Miller.Indexing.Semantic;
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

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "mb-" + Guid.NewGuid().ToString("N")[..10]);

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
        Assert.Equal(1, group.Factories.Count(factory => factory.Snapshot.IsOwner));
        Assert.All(group.Factories, factory => Assert.True(factory.Snapshot.SpawnAttempts >= 1));
        Assert.All(group.Factories, factory =>
        {
            Assert.Equal("ready", factory.Snapshot.State);
            Assert.Equal(
                SemanticEncoderSelection.Active.ModelSha256,
                factory.Snapshot.ModelSha256);
        });
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
        Assert.Equal(1, group.Factories.Count(factory => factory.Snapshot.IsOwner));
    }

    [Fact]
    public async Task OwnerDisposalStopsItsBroker_NonOwnerDisposalDoesNot_AndSurvivorReelects()
    {
        string counter = Path.Combine(_root, "loads.txt");
        await using BrokerFactoryGroup group = CreateGroup(3, counter, loadDelayMs: 250);
        _ = await group.HandshakesAsync(TestContext.Current.CancellationToken);
        SharedSemanticBrokerConnectionFactory owner =
            Assert.Single(group.Factories, factory => factory.Snapshot.IsOwner);
        SharedSemanticBrokerConnectionFactory[] nonOwners =
            group.Factories.Where(factory => !factory.Snapshot.IsOwner).ToArray();

        await nonOwners[0].DisposeAsync();
        await using (var proof = new SemanticEmbeddingSession(
            nonOwners[1],
            expectedEncoder: SemanticEncoderSelection.Active))
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
            expectedEncoder: SemanticEncoderSelection.Active,
            ownsConnectionFactory: false);
        Assert.NotNull(await firstSession.EnsureStartedAsync(
            TestContext.Current.CancellationToken));
        int firstPid = Assert.IsType<int>(factory.Snapshot.OwnerProcessId);

        using (Process child = Process.GetProcessById(firstPid))
        {
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync(TestContext.Current.CancellationToken);
        }
        await firstSession.DisposeAsync();

        await using var replacementSession = new SemanticEmbeddingSession(
            factory,
            expectedEncoder: SemanticEncoderSelection.Active,
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
                return WindowsBrokerJobAttachment.Failed("job attach denied");
            });
        await using var session = new SemanticEmbeddingSession(
            factory,
            expectedEncoder: SemanticEncoderSelection.Active,
            ownsConnectionFactory: false);

        Assert.True(
            await session.EnsureStartedAsync(TestContext.Current.CancellationToken) is not null,
            session.UnavailableReason);
        Assert.True(attachAttempted);
        Assert.Equal("ready", factory.Snapshot.State);
        Assert.True(factory.Snapshot.IsOwner);
        Assert.True(factory.Snapshot.OwnershipDegraded);
        Assert.Equal("job attach denied", factory.Snapshot.DegradedReason);
        int ownerPid = Assert.IsType<int>(factory.Snapshot.OwnerProcessId);
        SemanticBrokerEndpoint endpoint = SemanticBrokerEndpoint.Create(
            _root,
            SemanticEncoderSelection.Active);

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
                expectedEncoder: SemanticEncoderSelection.Active,
                ownsConnectionFactory: false))
            .ToList();
        return new BrokerFactoryGroup(factories, sessions);
    }

    private SharedSemanticBrokerConnectionFactory CreateFactory(
        string counter,
        int loadDelayMs,
        bool requireWindowsJob = false,
        Func<Process, WindowsBrokerJobAttachment>? attachWindowsJob = null,
        string? crashFirstMarker = null,
        bool exitOnOwnerCloseDuringDelay = false)
    {
        string executable = RequireBrokerHostExecutable();
        return new SharedSemanticBrokerConnectionFactory(
            executable,
            toolsRoot: _root,
            millerHome: _root,
            pin: SemanticEncoderSelection.Active,
            environment: EnvironmentFor(
                counter,
                loadDelayMs,
                crashFirstMarker,
                exitOnOwnerCloseDuringDelay),
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
        bool exitOnOwnerCloseDuringDelay)
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
