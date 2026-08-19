namespace Miller.Testing;

/// <summary>
/// Watch health for CT verdicts. Unknown or degraded watch health forces <c>Unknown</c>.
/// </summary>
public sealed class CtWatchHealth : IContinuousTestWatchHealthSource
{
    private readonly object _gate = new();
    private string _state = "unknown";
    private string? _observedRevision;
    private DateTimeOffset? _lastSuccessAt;
    private DateTimeOffset? _lastErrorAt;
    private string? _errorCode;

    public bool IsHealthy
    {
        get
        {
            lock (_gate)
                return string.Equals(_state, "good", StringComparison.Ordinal);
        }
    }

    public void RecordSuccess(string? observedRevision = null)
    {
        lock (_gate)
        {
            _state = "good";
            _observedRevision = observedRevision;
            _lastSuccessAt = DateTimeOffset.UtcNow;
            _errorCode = null;
        }
    }

    public void RecordError(string errorCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        lock (_gate)
        {
            _state = "degraded";
            _lastErrorAt = DateTimeOffset.UtcNow;
            _errorCode = errorCode;
        }
    }

    public ContinuousTestWatchHealthSnapshot Get(string workspaceId) => Snapshot(workspaceId);

    public ContinuousTestWatchHealthSnapshot Snapshot(string workspaceId)
    {
        _ = workspaceId;
        lock (_gate)
        {
            return new ContinuousTestWatchHealthSnapshot(
                _state,
                _observedRevision,
                _lastSuccessAt,
                _lastErrorAt,
                _errorCode);
        }
    }
}
