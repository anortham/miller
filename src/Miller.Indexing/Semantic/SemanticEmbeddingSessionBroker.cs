namespace Miller.Indexing.Semantic;

/// <summary>Owns the one lazy embedding session shared by query and convergence work in this process.</summary>
public sealed class SemanticEmbeddingSessionBroker : IAsyncDisposable, IDisposable
{
    private readonly bool _enabled;
    private readonly Func<SemanticEmbeddingSession?> _sessionFactory;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    private SemanticEmbeddingSession? _session;
    private bool _factoryInvoked;
    private bool _disposed;
    private int _waitingQueries;

    public SemanticEmbeddingSessionBroker(
        bool enabled,
        Func<SemanticEmbeddingSession?> sessionFactory)
    {
        ArgumentNullException.ThrowIfNull(sessionFactory);
        _enabled = enabled;
        _sessionFactory = sessionFactory;
    }

    public SemanticSessionState State
    {
        get
        {
            lock (_sync)
                return _disposed ? SemanticSessionState.Stopped : _session?.State ?? SemanticSessionState.NotStarted;
        }
    }

    public string? UnavailableReason
    {
        get
        {
            lock (_sync)
            {
                if (_disposed)
                    return "semantic session broker is disposed";
                if (!_enabled)
                    return "semantic retrieval is disabled";
                return _session?.UnavailableReason;
            }
        }
    }

    public SemanticEncoderHandshake? Handshake
    {
        get
        {
            lock (_sync)
                return _session?.Handshake;
        }
    }

    public bool Available => GetSession() is not null;

    public async Task<SemanticEmbedOutcome> EmbedQueryAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (GetSession() is not { } session)
            return SemanticEmbedOutcome.Fail(UnavailableReason ?? "the semantic sidecar is unavailable");

        Interlocked.Increment(ref _waitingQueries);
        bool acquired = false;
        try
        {
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            Interlocked.Decrement(ref _waitingQueries);
            if (IsDisposed())
                return SemanticEmbedOutcome.Fail(UnavailableReason!);
            return await session.EmbedQueryAsync(text, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!acquired)
                Interlocked.Decrement(ref _waitingQueries);
            else
                _operationGate.Release();
        }
    }

    public async Task<SemanticEmbedOutcome> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (GetSession() is not { } session)
            return SemanticEmbedOutcome.Fail(UnavailableReason ?? "the semantic sidecar is unavailable");

        while (true)
        {
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (Volatile.Read(ref _waitingQueries) == 0)
                break;

            _operationGate.Release();
            await Task.Yield();
        }

        try
        {
            if (IsDisposed())
                return SemanticEmbedOutcome.Fail(UnavailableReason!);
            return await session.EmbedBatchAsync(texts, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        SemanticEmbeddingSession? session;
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            session = _session;
            _session = null;
        }

        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (session is not null)
                await session.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private SemanticEmbeddingSession? GetSession()
    {
        lock (_sync)
        {
            if (_disposed || !_enabled)
                return null;
            if (!_factoryInvoked)
            {
                _session = _sessionFactory();
                _factoryInvoked = true;
            }
            return _session;
        }
    }

    private bool IsDisposed()
    {
        lock (_sync)
            return _disposed;
    }
}
