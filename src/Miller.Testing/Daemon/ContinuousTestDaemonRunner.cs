namespace Miller.Testing;

public sealed class ContinuousTestDaemonRunnerOptions
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    public Func<DateTimeOffset> Clock { get; init; } = static () => DateTimeOffset.UtcNow;
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;
    public Action<Exception>? ErrorHandler { get; init; }

    internal void Validate()
    {
        if (PollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(PollInterval), "poll interval must be positive");
        ArgumentNullException.ThrowIfNull(Clock);
        ArgumentNullException.ThrowIfNull(Delay);
    }
}

public interface IContinuousTestDaemonEnqueuer
{
    ContinuousTestDaemonEnqueueResult Enqueue(ContinuousTestDaemonChange change);
}

public sealed class ContinuousTestDaemonRunner : IContinuousTestDaemonEnqueuer, IAsyncDisposable
{
    private readonly ContinuousTestDaemonQueue _queue;
    private readonly ContinuousTestDaemonRunnerOptions _options;
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;

    public ContinuousTestDaemonRunner(
        ContinuousTestDaemonQueue queue,
        ContinuousTestDaemonRunnerOptions? options = null)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _options = options ?? new ContinuousTestDaemonRunnerOptions();
        _options.Validate();
    }

    public bool IsRunning
    {
        get
        {
            lock (_lock)
                return _loop is { IsCompleted: false };
        }
    }

    public Task? Completion
    {
        get
        {
            lock (_lock)
                return _loop;
        }
    }

    public ContinuousTestDaemonEnqueueResult Enqueue(ContinuousTestDaemonChange change) =>
        _queue.Enqueue(change);

    public void Start()
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            if (_loop is { IsCompleted: false })
                throw new InvalidOperationException("continuous test daemon runner is already running");

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;
            _loop = Task.Run(() => RunLoopAsync(token), CancellationToken.None);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (_lock)
        {
            cts = _cts;
            loop = _loop;
        }

        if (cts is null || loop is null)
            return;

        await cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await loop.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (loop.IsCompleted)
            {
                bool dispose = false;
                lock (_lock)
                {
                    if (ReferenceEquals(_loop, loop) && ReferenceEquals(_cts, cts))
                    {
                        _cts = null;
                        dispose = true;
                    }
                }

                if (dispose)
                    cts.Dispose();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await StopAsync().ConfigureAwait(false);
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _queue.DrainReadyAsync(_options.Clock(), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exc)
            {
                _options.ErrorHandler?.Invoke(exc);
            }

            try
            {
                await _options.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ContinuousTestDaemonRunner));
    }
}
