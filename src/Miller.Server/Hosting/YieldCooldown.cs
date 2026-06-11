namespace Miller.Server.Hosting;

/// <summary>
/// The anti-flap cooldown after a leader abdicates to a newer-extractor challenger (version-aware leadership
/// D4): the old leader suppresses its own claim retries while (a) less than <see cref="Duration"/> has elapsed
/// since the yield AND (b) the recorded requester pid is still alive — so the challenger's 5s retry wins the
/// re-race instead of the abdicator immediately re-claiming. Either condition failing (cooldown expired, or the
/// requester died without ever claiming) resumes normal claim retries: the abdicator is still version-eligible
/// relative to the artifact, so a dead challenger must not freeze the workspace. Pure and clock/probe-injected
/// so the state machine is fast-suite testable; not thread-safe by design (only the indexer claim loop touches it).
/// </summary>
internal sealed class YieldCooldown
{
    /// <summary>How long a claim is suppressed after abdicating, while the challenger is alive (spec D4).</summary>
    internal static readonly TimeSpan Duration = TimeSpan.FromSeconds(60);

    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<int, bool> _aliveProbe;

    private DateTimeOffset _beganAtUtc;
    private int _requesterPid; // <= 0 means no cooldown is active

    /// <param name="clock">UTC now source (injected for tests; production passes the system clock).</param>
    /// <param name="aliveProbe">Whether a process with the given pid is running (production:
    /// <see cref="LeaderIdentityFile.IsProcessAlive(int)"/>).</param>
    public YieldCooldown(Func<DateTimeOffset> clock, Func<int, bool> aliveProbe)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(aliveProbe);
        _clock = clock;
        _aliveProbe = aliveProbe;
    }

    /// <summary>
    /// Start the cooldown toward <paramref name="requesterPid"/> (the challenger this instance just yielded
    /// to). A non-positive pid arms nothing — defensive; the request queue validates pids on write.
    /// </summary>
    public void Begin(int requesterPid)
    {
        if (requesterPid <= 0)
        {
            _requesterPid = 0;
            return;
        }
        _requesterPid = requesterPid;
        _beganAtUtc = _clock();
    }

    /// <summary>
    /// True while the cooldown still suppresses a leadership claim. Expiry or requester death permanently
    /// clears the cooldown (it never re-arms until the next <see cref="Begin"/>).
    /// </summary>
    public bool SuppressesClaim()
    {
        if (_requesterPid <= 0)
            return false;

        if (_clock() - _beganAtUtc >= Duration || !_aliveProbe(_requesterPid))
        {
            _requesterPid = 0; // expired or the challenger died: resume normal claim retries for good
            return false;
        }

        return true;
    }
}
