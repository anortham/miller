using System.Diagnostics;

namespace Miller.Testing;

/// <summary>
/// The daemon's live activity, written by the parts that do the work and read by the part that publishes
/// <c>daemon.status.json</c>.
///
/// <para><b>Why this exists.</b> The daemon used to publish the reason "executing" once and then block inside
/// <c>DrainReadyAsync</c> for the whole run. The status file did not move again until the run ended, so a
/// reader could not separate a slow suite from a wedged one, and <c>tests run --wait</c> had nothing to wait
/// on except the verdict — which is not a completion signal. One dogfood run sat at "executing" for 12
/// minutes with only the pulse's timestamp moving.</para>
///
/// <para><b>Draining and running are separate on purpose.</b> One drain executes every ready project in turn,
/// so a child ending is NOT the daemon going idle. Collapsing the two would let a waiting caller return in
/// the gap between two projects of the same drain and report a verdict that is missing the rest.</para>
///
/// <para><b>Two clocks on purpose.</b> <see cref="CtDaemonRunProgress.RunStartedAtUtc"/> is wall-clock,
/// because a reader prints it. The silence measurement is monotonic (<see cref="Stopwatch"/>), because a
/// wall-clock jump — an NTP correction, a laptop waking — must not read as ten minutes of silence and must
/// not hide it either.</para>
///
/// <para>Every member is safe to call from any thread. The provider's two stream drain loops stamp output
/// while the daemon's pulse task reads.</para>
/// </summary>
public sealed class CtRunActivityCell
{
    /// <summary>
    /// The share of the stall bound a child may be silent for and still read as
    /// <see cref="CtRunActivity.Active"/>. A quarter, so "quiet" appears well before the kill is due and an
    /// operator watching the file sees the run slow down rather than only see it die.
    /// </summary>
    private const double ActiveFraction = 0.25;

    /// <summary>
    /// The bound used for the <see cref="CtRunActivity"/> wording when the stall guard is switched off. The
    /// words still describe the child, but <see cref="CtRunActivity.Stalled"/> is never reported, because
    /// nothing will act on it.
    /// </summary>
    private static readonly TimeSpan UnboundedReference = TimeSpan.FromMinutes(10);

    private readonly object _gate = new();
    private readonly TimeSpan _stallTimeout;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<long> _timestamp;

    private bool _draining;
    private bool _queued;
    private string? _projectPath;
    private string? _runId;
    private int _selectedCaseCount;
    private DateTimeOffset _runStartedAtUtc;
    private long _lastOutputTicks;

    // Written outside the gate by the stream drain loops; volatile so a publish on another thread cannot
    // keep reporting "starting" after the child has spoken.
    private volatile bool _sawOutput;

    /// <param name="stallTimeout">
    /// The runner's <see cref="TestProcessRunnerOptions.OutputStallTimeout"/>, so the reported words agree
    /// with the bound that will actually kill the run. A non-positive or infinite value means the guard is
    /// off.
    /// </param>
    /// <param name="clock">Wall clock for the run start stamp. Tests supply their own.</param>
    public CtRunActivityCell(TimeSpan stallTimeout, Func<DateTimeOffset>? clock = null)
        : this(stallTimeout, clock, Stopwatch.GetTimestamp)
    {
    }

    /// <summary>
    /// Test seam for the monotonic clock. The silence classification is the whole point of this type, and it
    /// cannot be proven by sleeping: the production bound is ten minutes.
    /// </summary>
    internal CtRunActivityCell(TimeSpan stallTimeout, Func<DateTimeOffset>? clock, Func<long> timestamp)
    {
        ArgumentNullException.ThrowIfNull(timestamp);
        _stallTimeout = stallTimeout;
        _clock = clock ?? (static () => DateTimeOffset.UtcNow);
        _timestamp = timestamp;
    }

    /// <summary>
    /// The daemon loop is about to drain ready work and will be blocked until every project in that drain has
    /// finished. Called before the first child starts, so a caller waiting for the daemon to settle cannot
    /// slip through the gap between accepting work and running it.
    /// </summary>
    public void BeginDrain()
    {
        lock (_gate)
        {
            _draining = true;
            _queued = false;
        }
    }

    /// <summary>The drain returned. Any child it started has already ended.</summary>
    public void EndDrain()
    {
        lock (_gate)
        {
            _draining = false;
            ClearRun();
        }
    }

    /// <summary>Work is ready but cannot execute — normally another workspace holds the execution budget.</summary>
    public void EnterQueued()
    {
        lock (_gate)
        {
            if (!_draining)
                _queued = true;
        }
    }

    /// <summary>Nothing is outstanding. Used by the daemon loop when it finds no ready work.</summary>
    public void EnterIdle()
    {
        lock (_gate)
        {
            if (!_draining)
                _queued = false;
        }
    }

    /// <summary>A provider run started. Replaces any previous run's details.</summary>
    public void BeginRun(string projectPath, string runId, int selectedCaseCount)
    {
        lock (_gate)
        {
            _projectPath = projectPath;
            _runId = runId;
            _selectedCaseCount = selectedCaseCount;
            _runStartedAtUtc = _clock();
            _lastOutputTicks = _timestamp();
            _sawOutput = false;
        }
    }

    /// <summary>
    /// The child said something. Called from the provider's stream drain loops, so it stays off the gate.
    /// </summary>
    public void StampOutput()
    {
        Interlocked.Exchange(ref _lastOutputTicks, _timestamp());
        _sawOutput = true;
    }

    /// <summary>
    /// The provider run finished, however it finished. The daemon stays <see cref="CtDaemonActivity.Executing"/>
    /// until the whole drain returns.
    /// </summary>
    public void EndRun()
    {
        lock (_gate)
            ClearRun();
    }

    /// <summary>
    /// The activity to publish, and the run details when a child is in flight. Taken as one snapshot under the
    /// gate, so a reader never sees a run attached to an idle daemon or the reverse.
    /// </summary>
    public (CtDaemonActivity Activity, CtDaemonRunProgress? Run) Read()
    {
        lock (_gate)
        {
            CtDaemonActivity activity = _draining
                ? CtDaemonActivity.Executing
                : _queued ? CtDaemonActivity.Queued : CtDaemonActivity.Idle;

            if (_projectPath is null || _runId is null)
                return (activity, null);

            return (
                activity,
                new CtDaemonRunProgress(
                    _projectPath,
                    _runId,
                    _selectedCaseCount,
                    _runStartedAtUtc,
                    ClassifySilence(SinceLastOutput())));
        }
    }

    private TimeSpan SinceLastOutput()
    {
        long elapsed = _timestamp() - Interlocked.Read(ref _lastOutputTicks);
        return elapsed <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(elapsed / (double)Stopwatch.Frequency);
    }

    private void ClearRun()
    {
        _projectPath = null;
        _runId = null;
        _selectedCaseCount = 0;
        _sawOutput = false;
    }

    private CtRunActivity ClassifySilence(TimeSpan silence)
    {
        if (!_sawOutput)
            return CtRunActivity.Starting;

        bool bounded = _stallTimeout > TimeSpan.Zero && _stallTimeout != Timeout.InfiniteTimeSpan;
        TimeSpan reference = bounded ? _stallTimeout : UnboundedReference;

        if (silence < reference * ActiveFraction)
            return CtRunActivity.Active;

        // With the guard off nothing will kill the run, so reporting "stalled" would name an action that is
        // not coming. It stays "quiet" however long the silence runs.
        return silence >= reference && bounded ? CtRunActivity.Stalled : CtRunActivity.Quiet;
    }
}
