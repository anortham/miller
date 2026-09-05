using Miller.Indexing.Reads;

namespace Miller.Indexing.Store;

internal sealed class StoreReaderRegistrationRegistry : IDisposable
{
    internal static readonly TimeSpan DiagnosticInterval = TimeSpan.FromSeconds(30);
    private static readonly Lazy<StoreReaderRegistrationRegistry> SharedOwner = new(() => new());
    internal static StoreReaderRegistrationRegistry Shared => SharedOwner.Value;
    private readonly object _gate = new();
    private readonly Dictionary<StoreReaderRegistrationHandle, string> _handles = [];
    private readonly HashSet<string> _nonces = new(StringComparer.Ordinal);
    private readonly int _capacity;
    private readonly Timer? _timer;
    private bool _disposed;
    private int _ticking;
    internal Func<DateTimeOffset> UtcNow { get; }

    internal StoreReaderRegistrationRegistry(bool startScheduler = true, int capacity = 1024, Func<DateTimeOffset>? utcNow = null)
    {
        if (capacity is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        UtcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        if (startScheduler) _timer = new Timer(_ => ScheduledTick(), null, DiagnosticInterval, DiagnosticInterval);
    }

    internal int Count { get { lock (_gate) return _handles.Count; } }

    internal void Attach(StoreReaderRegistrationHandle handle, string nonce)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_handles.Count >= _capacity) throw new StoreReaderRegistrationException(ReaderFailure.RegistryCapacity);
            if (!_nonces.Add(nonce)) throw new StoreReaderRegistrationException(ReaderFailure.InvalidArguments);
            _handles.Add(handle, nonce);
        }
    }

    internal void Detach(StoreReaderRegistrationHandle handle)
    {
        lock (_gate) if (_handles.Remove(handle, out string? nonce)) _nonces.Remove(nonce);
    }

    internal void Tick(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _ticking, 1) != 0) return;
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(StoreReaderRegistrationRunner.ProcessTimeout);
            StoreReaderRegistrationHandle[] selected;
            lock (_gate)
            {
                selected = _handles.Keys.OrderBy(handle => handle.SchedulingDeadlineTicks).ToArray();
            }
            foreach (StoreReaderRegistrationHandle handle in selected)
            {
                if (budget.IsCancellationRequested) break;
                handle.Service(now, budget.Token);
            }
        }
        finally { Volatile.Write(ref _ticking, 0); }
    }

    private void ScheduledTick()
    {
        Tick(UtcNow(), CancellationToken.None);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose();
            // Stopping the owner cannot certify that callers closed their SQLite handles.
            // Keep registrations and owed requests reachable for disposal/manual drain.
        }
    }
}
