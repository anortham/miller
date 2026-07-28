using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;

namespace Miller.Indexing.Semantic;

public sealed record SemanticBrokerSnapshot(
    string State,
    string EndpointIdentity,
    bool IsOwner,
    bool OwnershipDegraded,
    string? OwnershipDegradedReason,
    int ReconnectCount,
    int SpawnAttempts,
    int? OwnerProcessId,
    string? ModelId = null,
    string? ModelSha256 = null,
    string? Backend = null,
    bool Accelerated = false,
    int RetiredOwnerCount = 0,
    string ServerVersion = "1",
    bool AcceleratorLeaseHeld = false,
    string? BackendDegradedReason = null);

public sealed class SharedSemanticBrokerConnectionFactory :
    ISemanticSidecarConnectionFactory,
    ISemanticBrokerSnapshotRecorder
{
    private static readonly TimeSpan DefaultDirectConnectTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan DefaultInitializationTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan ReElectionInterval = TimeSpan.FromSeconds(1);

    private readonly object _sync = new();
    private readonly string _executable;
    private readonly SemanticBrokerEndpoint _endpoint;
    private readonly SemanticEncoderPin _pin;
    private readonly IReadOnlyDictionary<string, string?> _environment;
    private readonly TimeSpan _directConnectTimeout;
    private readonly TimeSpan _initializationTimeout;
    private readonly TimeSpan _pollInterval;
    private readonly bool _requireWindowsJob;
    private readonly Func<Process, WindowsBrokerJobAttachment> _attachWindowsJob;
    private readonly Action? _passiveConnectionAccepted;
    private readonly Action? _connectionDisposed;
    private readonly SemaphoreSlim _spawnGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<BrokerConnection> _connections = [];

    private Process? _ownerProcess;
    private StreamWriter? _ownerInput;
    private WindowsBrokerJob? _ownerJob;
    private bool _disposed;
    private bool _hasConnected;
    private int _activeConnects;
    private TaskCompletionSource<bool> _connectsDrained = CompletedDrain();
    private SemanticBrokerSnapshot _snapshot;

    public SharedSemanticBrokerConnectionFactory(
        string toolsRoot,
        string millerHome,
        SemanticEncoderPin pin)
        : this(
            SemanticSidecarLayout.ExecutablePath(toolsRoot),
            toolsRoot,
            millerHome,
            pin,
            environment: null,
            DefaultDirectConnectTimeout,
            DefaultInitializationTimeout,
            DefaultPollInterval,
            requireWindowsJob: OperatingSystem.IsWindows(),
            attachWindowsJob: null)
    {
    }

    internal SharedSemanticBrokerConnectionFactory(
        string executable,
        string toolsRoot,
        string millerHome,
        SemanticEncoderPin pin,
        IReadOnlyDictionary<string, string?>? environment,
        TimeSpan directConnectTimeout,
        TimeSpan initializationTimeout,
        TimeSpan pollInterval,
        bool requireWindowsJob,
        Func<Process, WindowsBrokerJobAttachment>? attachWindowsJob,
        Action? passiveConnectionAccepted = null,
        Action? connectionDisposed = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolsRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(millerHome);
        ArgumentNullException.ThrowIfNull(pin);

        _executable = executable;
        _endpoint = SemanticBrokerEndpoint.Create(millerHome, pin);
        _pin = pin;
        _environment = environment ?? new Dictionary<string, string?>();
        _directConnectTimeout = directConnectTimeout;
        _initializationTimeout = initializationTimeout;
        _pollInterval = pollInterval;
        _requireWindowsJob = requireWindowsJob;
        _attachWindowsJob = attachWindowsJob ?? WindowsBrokerJob.Attach;
        _passiveConnectionAccepted = passiveConnectionAccepted;
        _connectionDisposed = connectionDisposed;
        _snapshot = new SemanticBrokerSnapshot(
            "disconnected",
            _endpoint.Identity,
            false,
            false,
            null,
            0,
            0,
            null);
    }

    public SemanticBrokerSnapshot Snapshot
    {
        get
        {
            ClearExitedOwner();
            lock (_sync)
            {
                return _snapshot;
            }
        }
    }

    public async ValueTask<ISemanticSidecarConnection> ConnectAsync(
        CancellationToken cancellationToken)
    {
        EnterConnect();
        try
        {
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetime.Token);
            try
            {
                return await ConnectCoreAsync(operation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (_lifetime.IsCancellationRequested
                    && !cancellationToken.IsCancellationRequested)
            {
                throw new ObjectDisposedException(
                    nameof(SharedSemanticBrokerConnectionFactory));
            }
        }
        finally
        {
            ExitConnect();
        }
    }

    public async ValueTask<SemanticBrokerSnapshot?> ObserveExistingAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        ThrowIfDisposed();

        using var observation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        observation.CancelAfter(timeout);
        BrokerConnection? connection;
        try
        {
            connection = await TryConnectAsync(timeout, observation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        if (connection is null)
            return null;

        await using var passiveFactory = new PassiveConnectionFactory(connection, this);
        _passiveConnectionAccepted?.Invoke();
        await using var session = new SemanticEmbeddingSession(
            passiveFactory,
            new SemanticSessionOptions
            {
                InitTimeout = timeout,
                RequestTimeout = timeout,
                ShutdownTimeout = timeout,
            },
            _pin);
        try
        {
            SemanticEncoderHandshake? handshake =
                await session.EnsureStartedAsync(observation.Token).ConfigureAwait(false);
            return handshake is null ? null : Snapshot;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private async ValueTask<ISemanticSidecarConnection> ConnectCoreAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        BrokerConnection? connection = await TryConnectAsync(
            _directConnectTimeout,
            cancellationToken).ConfigureAwait(false);
        if (connection is not null)
        {
            return await RegisterOrDisposeAsync(connection).ConfigureAwait(false);
        }

        await _spawnGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            connection = await TryConnectAsync(
                _directConnectTimeout,
                cancellationToken).ConfigureAwait(false);
            if (connection is not null)
            {
                return await RegisterOrDisposeAsync(connection).ConfigureAwait(false);
            }

            await EnsureOwnerCandidateAsync(cancellationToken).ConfigureAwait(false);
            DateTimeOffset nextElection = DateTimeOffset.UtcNow + ReElectionInterval;

            using var initialization = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            initialization.CancelAfter(_initializationTimeout);
            while (true)
            {
                connection = await TryConnectAsync(
                    _directConnectTimeout,
                    initialization.Token).ConfigureAwait(false);
                if (connection is not null)
                {
                    return await RegisterOrDisposeAsync(connection).ConfigureAwait(false);
                }

                ClearExitedOwner();
                if (DateTimeOffset.UtcNow >= nextElection)
                {
                    await EnsureOwnerCandidateAsync(initialization.Token).ConfigureAwait(false);
                    nextElection = DateTimeOffset.UtcNow + ReElectionInterval;
                }
                await Task.Delay(_pollInterval, initialization.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Semantic broker {_endpoint.Identity} did not become ready within {_initializationTimeout}.");
        }
        finally
        {
            _spawnGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task connectsDrained;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _snapshot = _snapshot with
            {
                State = "disposed",
                IsOwner = false,
                OwnerProcessId = null,
            };
            connectsDrained = _connectsDrained.Task;
        }

        _lifetime.Cancel();
        await connectsDrained.ConfigureAwait(false);

        BrokerConnection[] connections;
        Process? ownerProcess;
        StreamWriter? ownerInput;
        WindowsBrokerJob? ownerJob;

        lock (_sync)
        {
            connections = [.. _connections];
            _connections.Clear();
            ownerProcess = _ownerProcess;
            ownerInput = _ownerInput;
            ownerJob = _ownerJob;
            _ownerProcess = null;
            _ownerInput = null;
            _ownerJob = null;
        }

        foreach (BrokerConnection connection in connections)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        if (ownerInput is not null)
        {
            await ownerInput.DisposeAsync().ConfigureAwait(false);
        }

        ownerJob?.Dispose();
        if (ownerProcess is not null)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await ownerProcess.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                ownerProcess.Dispose();
            }
        }
    }

    void ISemanticBrokerSnapshotRecorder.RecordHandshake(SemanticEncoderHandshake handshake)
    {
        ArgumentNullException.ThrowIfNull(handshake);
        lock (_sync)
        {
            _snapshot = _snapshot with
            {
                State = "ready",
                ModelId = handshake.Pin.ModelId,
                ModelSha256 = handshake.Pin.ModelSha256,
                Backend = handshake.ResolvedBackend,
                Accelerated = handshake.Accelerated,
                AcceleratorLeaseHeld = handshake.AcceleratorLeaseHeld,
                BackendDegradedReason = handshake.DegradedReason,
            };
        }
    }

    private async Task EnsureOwnerCandidateAsync(CancellationToken cancellationToken)
    {
        ClearExitedOwner();
        lock (_sync)
        {
            if (_ownerProcess is { HasExited: false })
            {
                return;
            }
        }

        Directory.CreateDirectory(_endpoint.DirectoryPath);
        var startInfo = new ProcessStartInfo
        {
            FileName = _executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in _endpoint.BrokerArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach ((string key, string? value) in _environment)
        {
            startInfo.Environment[key] = value;
        }

        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("The semantic broker process did not start.");
        }

        var ownerInput = process.StandardInput;
        WindowsBrokerJobAttachment attachment = _requireWindowsJob
            ? _attachWindowsJob(process)
            : WindowsBrokerJobAttachment.NotRequired;

        bool accepted;
        lock (_sync)
        {
            accepted = !_disposed;
            if (accepted)
            {
                _ownerProcess = process;
                _ownerInput = ownerInput;
                _ownerJob = attachment.Job;
                _snapshot = _snapshot with
                {
                    State = "starting",
                    IsOwner = true,
                    OwnerProcessId = process.Id,
                    SpawnAttempts = _snapshot.SpawnAttempts + 1,
                    OwnershipDegraded = _requireWindowsJob && !attachment.IsAttached,
                    OwnershipDegradedReason = attachment.FailureReason,
                };
            }
        }

        if (!accepted)
        {
            await ownerInput.DisposeAsync().ConfigureAwait(false);
            attachment.Job?.Dispose();
            process.Dispose();
            throw new ObjectDisposedException(nameof(SharedSemanticBrokerConnectionFactory));
        }

        _ = DrainAsync(process.StandardOutput);
        _ = DrainAsync(process.StandardError);
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task<BrokerConnection?> TryConnectAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connect.CancelAfter(timeout);
        try
        {
            Stream stream;
            if (OperatingSystem.IsWindows())
            {
                var pipe = new NamedPipeClientStream(
                    ".",
                    _endpoint.WindowsPipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous);
                try
                {
                    await pipe.ConnectAsync(connect.Token).ConfigureAwait(false);
                    stream = pipe;
                }
                catch
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }
            else
            {
                var socket = new Socket(
                    AddressFamily.Unix,
                    SocketType.Stream,
                    ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(
                        new UnixDomainSocketEndPoint(_endpoint.UnixSocketPath),
                        connect.Token).ConfigureAwait(false);
                    stream = new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }

            return new BrokerConnection(stream, OnConnectionDisposed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private BrokerConnection Register(BrokerConnection connection)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            _connections.Add(connection);
            bool isOwner = _ownerProcess is { HasExited: false };
            _snapshot = _snapshot with
            {
                State = "connected",
                IsOwner = isOwner,
                OwnerProcessId = isOwner ? _ownerProcess!.Id : null,
                ReconnectCount = _hasConnected
                    ? _snapshot.ReconnectCount + 1
                    : _snapshot.ReconnectCount,
            };
            _hasConnected = true;
        }

        return connection;
    }

    private async ValueTask<BrokerConnection> RegisterOrDisposeAsync(BrokerConnection connection)
    {
        try
        {
            return Register(connection);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void ClearExitedOwner()
    {
        Process? process = null;
        StreamWriter? input = null;
        WindowsBrokerJob? job = null;
        lock (_sync)
        {
            if (_ownerProcess is not { HasExited: true })
            {
                return;
            }

            process = _ownerProcess;
            input = _ownerInput;
            job = _ownerJob;
            _ownerProcess = null;
            _ownerInput = null;
            _ownerJob = null;
            _snapshot = _snapshot with
            {
                IsOwner = false,
                OwnerProcessId = null,
                RetiredOwnerCount = _snapshot.RetiredOwnerCount + 1,
            };
        }

        input?.Dispose();
        job?.Dispose();
        process.Dispose();
    }

    private void OnConnectionDisposed(BrokerConnection connection)
    {
        lock (_sync)
        {
            _connections.Remove(connection);
        }
        _connectionDisposed?.Invoke();
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is not null)
            {
            }
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private void EnterConnect()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_activeConnects++ == 0)
            {
                _connectsDrained = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    private void ExitConnect()
    {
        TaskCompletionSource<bool>? drained = null;
        lock (_sync)
        {
            if (--_activeConnects == 0)
                drained = _connectsDrained;
        }

        drained?.TrySetResult(true);
    }

    private static TaskCompletionSource<bool> CompletedDrain()
    {
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        completion.SetResult(true);
        return completion;
    }

    private sealed class PassiveConnectionFactory(
        BrokerConnection connection,
        ISemanticBrokerSnapshotRecorder recorder)
        : ISemanticSidecarConnectionFactory, ISemanticBrokerSnapshotRecorder
    {
        private BrokerConnection? _connection = connection;

        public ValueTask<ISemanticSidecarConnection> ConnectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BrokerConnection? accepted = Interlocked.Exchange(ref _connection, null);
            if (accepted is null)
                throw new InvalidOperationException("The passive broker connection has already been consumed.");
            return ValueTask.FromResult<ISemanticSidecarConnection>(accepted);
        }

        public async ValueTask DisposeAsync()
        {
            BrokerConnection? unclaimed = Interlocked.Exchange(ref _connection, null);
            if (unclaimed is not null)
                await unclaimed.DisposeAsync().ConfigureAwait(false);
        }

        public void RecordHandshake(SemanticEncoderHandshake handshake) => recorder.RecordHandshake(handshake);
    }

    private sealed class BrokerConnection : ISemanticSidecarConnection
    {
        private readonly Stream _stream;
        private readonly Action<BrokerConnection> _onDisposed;
        private int _disposed;

        public BrokerConnection(Stream stream, Action<BrokerConnection> onDisposed)
        {
            _stream = stream;
            _onDisposed = onDisposed;
            Input = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n",
            };
            Output = new StreamReader(
                stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false,
                leaveOpen: true);
        }

        public TextWriter Input { get; }

        public TextReader Output { get; }

        public bool IsClosed => Volatile.Read(ref _disposed) != 0;

        public void Abort()
        {
            DisposeCore();
        }

        public async ValueTask DisposeAsync()
        {
            if (!DisposeCore())
            {
                return;
            }

            await _stream.DisposeAsync().ConfigureAwait(false);
        }

        private bool DisposeCore()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return false;
            }

            _onDisposed(this);
            try
            {
                _stream.Dispose();
            }
            catch (IOException)
            {
            }

            return true;
        }
    }
}
