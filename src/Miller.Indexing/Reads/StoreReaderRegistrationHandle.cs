using Miller.Indexing.Store;

namespace Miller.Indexing.Reads;

internal enum ReaderLifecycleStatus { Acquiring, Acquired, RenewDegraded, AcquireReleaseOwed, CloseOwed, ReleaseOwed, Released, Legacy }

internal sealed class StoreReaderRegistrationHandle : IDisposable
{
    private readonly object _gate = new();
    private readonly StoreReaderRegistrationRunner? _runner;
    private readonly ReaderAcquireRequest? _request;
    private readonly StoreReaderRegistrationRegistry? _registry;
    private ReaderAcquireResult? _acquired;
    private ReaderLifecycleStatus _status;
    private ReaderFailure? _lastFailure;
    private DateTimeOffset _nextAttemptAt;
    private bool _disposed;
    private int _references = 1;
    private long _schedulingDeadlineTicks;
    private Func<bool>? _releaseGuard;

    private StoreReaderRegistrationHandle() => _status = ReaderLifecycleStatus.Legacy;

    private StoreReaderRegistrationHandle(StoreReaderRegistrationRunner runner, ReaderAcquireRequest request, StoreReaderRegistrationRegistry registry)
    {
        _runner = runner; _request = request; _registry = registry;
        _status = ReaderLifecycleStatus.Acquiring;
    }

    internal ReaderLifecycleStatus Status { get { lock (_gate) return _status; } }
    internal ReaderFailure? LastFailure { get { lock (_gate) return _lastFailure; } }
    internal StoreReaderSnapshot Snapshot { get { lock (_gate) return _acquired?.Snapshot ?? throw new InvalidOperationException("No admitted reader snapshot."); } }
    internal DateTimeOffset? ExpiresAt { get { lock (_gate) return _acquired?.ExpiresAt; } }
    internal long SchedulingDeadlineTicks => Interlocked.Read(ref _schedulingDeadlineTicks);

    internal void SetReleaseGuard(Func<bool> guard)
    {
        ArgumentNullException.ThrowIfNull(guard);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_releaseGuard is not null) throw new InvalidOperationException("A release guard is already installed.");
            _releaseGuard = guard;
        }
    }

    internal IDisposable Retain()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_status is not (ReaderLifecycleStatus.Acquired or ReaderLifecycleStatus.RenewDegraded or ReaderLifecycleStatus.Legacy))
                throw new InvalidOperationException("The reader registration is not acquired.");
            checked { _references++; }
            return new RetainedOwner(this);
        }
    }

    internal static StoreReaderRegistrationHandle Legacy() => new();
    internal static StoreReaderRegistrationHandle Acquire(StoreReaderRegistrationRunner runner, ReaderAcquireRequest request,
        StoreReaderRegistrationRegistry registry, CancellationToken cancellationToken)
    {
        request.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var handle = new StoreReaderRegistrationHandle(runner, request, registry);
        registry.Attach(handle, request.OwnerNonce);
        lock (handle._gate)
        {
            try
            {
                handle._acquired = runner.Acquire(request, cancellationToken);
                handle._status = ReaderLifecycleStatus.Acquired;
                handle.ScheduleNextAttempt();
                return handle;
            }
            catch (StoreReaderRegistrationException error) when (!error.MayHaveAcquired)
            {
                handle._status = ReaderLifecycleStatus.Released;
                registry.Detach(handle);
                throw;
            }
            catch
            {
                // Even cancellation may race the producer's commit. The registry already owns
                // this exact request, and recovery must acquire the same nonce only to release.
                handle._disposed = true;
                handle._references = 0;
                handle._status = ReaderLifecycleStatus.AcquireReleaseOwed;
                handle.ScheduleNextAttempt();
                throw;
            }
        }
    }

    internal void RenewIfBefore(DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_status is not (ReaderLifecycleStatus.Acquired or ReaderLifecycleStatus.RenewDegraded)
                || _acquired!.ExpiresAt > deadline) return;
            try { Renew(cancellationToken); }
            catch (Exception error)
            {
                RecordFailure(error);
                _status = ReaderLifecycleStatus.RenewDegraded;
            }
            finally { ScheduleNextAttempt(); }
        }
    }

    internal void Service(DateTimeOffset now, CancellationToken cancellationToken)
    {
        // One slow foreground open/dispose cannot block the shared scheduler's other handles.
        if (!Monitor.TryEnter(_gate)) return;
        try
        {
            if (_status is ReaderLifecycleStatus.Acquiring or ReaderLifecycleStatus.Released or ReaderLifecycleStatus.Legacy
                || now < _nextAttemptAt) return;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_status == ReaderLifecycleStatus.AcquireReleaseOwed)
                {
                    _acquired = _runner!.Acquire(_request!, cancellationToken);
                    _status = ReaderLifecycleStatus.ReleaseOwed;
                }
                if (_status is ReaderLifecycleStatus.CloseOwed or ReaderLifecycleStatus.ReleaseOwed)
                    Release(cancellationToken);
                else if (_acquired!.ExpiresAt <= now + StoreReaderRegistrationRegistry.DiagnosticInterval + StoreReaderRegistrationRunner.ProcessTimeout)
                    Renew(cancellationToken);
            }
            catch (Exception error)
            {
                RecordFailure(error);
                if (_status == ReaderLifecycleStatus.Acquired) _status = ReaderLifecycleStatus.RenewDegraded;
            }
            finally { ScheduleNextAttempt(); }
        }
        finally { Monitor.Exit(_gate); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                if (_status == ReaderLifecycleStatus.CloseOwed)
                {
                    try { Release(CancellationToken.None); }
                    catch (Exception error) { RecordFailure(error); }
                    finally { ScheduleNextAttempt(); }
                }
                return;
            }
            _disposed = true;
            DropReference();
        }
    }

    private void DropReference()
    {
        if (--_references != 0 || _status is ReaderLifecycleStatus.Legacy or ReaderLifecycleStatus.Released) return;
        _status = ReaderLifecycleStatus.ReleaseOwed;
        try { Release(CancellationToken.None); }
        catch (Exception error) { RecordFailure(error); }
        finally { ScheduleNextAttempt(); }
    }

    private void Release(CancellationToken cancellationToken)
    {
        if (_references != 0) throw new InvalidOperationException("A live owner still retains this registration.");
        if (_releaseGuard is not null)
        {
            _status = ReaderLifecycleStatus.CloseOwed;
            if (!_releaseGuard())
            {
                _lastFailure = ReaderFailure.Operational;
                return;
            }
        }
        _status = ReaderLifecycleStatus.ReleaseOwed;
        _runner!.Release(_request!, _acquired!, cancellationToken);
        _status = ReaderLifecycleStatus.Released;
        _lastFailure = null;
        _releaseGuard = null;
        _registry!.Detach(this);
    }

    private void RecordFailure(Exception error) => _lastFailure = error is StoreReaderRegistrationException registration
        ? registration.Failure : ReaderFailure.Transport;

    private void Renew(CancellationToken cancellationToken)
    {
        _acquired = _runner!.Renew(_request!, _acquired!, cancellationToken);
        _status = ReaderLifecycleStatus.Acquired;
        _lastFailure = null;
    }

    private void ScheduleNextAttempt()
    {
        _nextAttemptAt = _registry!.UtcNow() + StoreReaderRegistrationRegistry.DiagnosticInterval;
        long deadline = _status is ReaderLifecycleStatus.Acquired or ReaderLifecycleStatus.RenewDegraded
            ? Math.Max(_nextAttemptAt.UtcTicks, (_acquired!.ExpiresAt - StoreReaderRegistrationRegistry.DiagnosticInterval
                - StoreReaderRegistrationRunner.ProcessTimeout).UtcTicks)
            : _nextAttemptAt.UtcTicks;
        Interlocked.Exchange(ref _schedulingDeadlineTicks, deadline);
    }

    private sealed class RetainedOwner(StoreReaderRegistrationHandle owner) : IDisposable
    {
        private StoreReaderRegistrationHandle? _owner = owner;
        public void Dispose()
        {
            StoreReaderRegistrationHandle? handle = Interlocked.Exchange(ref _owner, null);
            if (handle is not null) lock (handle._gate) handle.DropReference();
        }
    }
}
