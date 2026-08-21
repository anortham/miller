using System.Collections.Concurrent;

namespace Miller.Server.Workspaces;

/// <summary>
/// The PROCESS-WIDE coalescing guard behind serve-then-refresh cross-workspace reads
/// (<see cref="WorkspaceRefreshMode.Background"/>).
///
/// <para>The blocking arm throttled itself because every caller queued behind the same scan; a fire-and-forget arm
/// has no such queue, so this gate IS the throttle. Ten cross-workspace reads must start ONE refresh, not ten.</para>
///
/// <para>It is a SEPARATE singleton, not a field on <see cref="WorkspaceIndexProvider"/>, because that provider is
/// registered <c>AddTransient</c> for the concrete type and for all seven provider interfaces
/// (<c>MillerServiceRegistration.AddMillerServices</c>). Every tool call therefore builds fresh provider instances —
/// <c>SearchTool</c> alone injects two of them — so an instance field would coalesce nothing in production while a
/// single-instance test looked green.</para>
///
/// <para>A refresh that has just FINISHED also holds the gate for <see cref="DefaultCooldown"/>. One tool call
/// resolves several read contexts in a row (SearchTool up to three, ContextTool two); with the real thread pool an
/// early refresh can complete between two of them and re-arm the guard, so the same call would start a second scan.
/// The cooldown covers that window. It never touches <see cref="WorkspaceRefreshMode.Blocking"/>: an explicit
/// <c>ensure_fresh=true</c> always refreshes.</para>
/// </summary>
public sealed class BackgroundRefreshGate
{
    /// <summary>
    /// How long a just-finished refresh keeps holding the gate. Long enough to cover one tool call's several
    /// resolves, short enough that a follow-up read still picks up a real change promptly.
    /// </summary>
    public static readonly TimeSpan DefaultCooldown = TimeSpan.FromSeconds(5);

    private const long InFlight = long.MinValue;

    private readonly ConcurrentDictionary<string, long> _state = new(StringComparer.Ordinal);
    private readonly Func<long> _nowMilliseconds;
    private readonly long _cooldownMilliseconds;

    public BackgroundRefreshGate()
        : this(DefaultCooldown)
    {
    }

    /// <param name="cooldown">How long a completed refresh keeps holding the gate.</param>
    /// <param name="nowMilliseconds">
    /// A MONOTONIC millisecond source; defaults to <see cref="Environment.TickCount64"/>. Never the wall clock — a
    /// clock correction must not open or extend the cooldown.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="cooldown"/> is negative.</exception>
    internal BackgroundRefreshGate(TimeSpan cooldown, Func<long>? nowMilliseconds = null)
    {
        if (cooldown < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(cooldown), cooldown, "Cooldown must be non-negative.");
        _cooldownMilliseconds = (long)cooldown.TotalMilliseconds;
        _nowMilliseconds = nowMilliseconds ?? (static () => Environment.TickCount64);
    }

    /// <summary>
    /// Claim the right to start a background refresh for this workspace. False means one is already running, or one
    /// finished inside the cooldown — either way the caller starts nothing and serves the pinned view.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="workspaceId"/> is null, empty, or whitespace.</exception>
    public bool TryEnter(string workspaceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        long now = _nowMilliseconds();
        bool entered = false;
        _state.AddOrUpdate(
            workspaceId,
            _ =>
            {
                entered = true;
                return InFlight;
            },
            (_, existing) =>
            {
                // Every factory invocation reassigns the flag: AddOrUpdate re-runs its factories when the compare
                // and swap loses, and a stale `true` from a losing attempt would admit a second refresh.
                if (existing == InFlight || now - existing < _cooldownMilliseconds)
                {
                    entered = false;
                    return existing;
                }

                entered = true;
                return InFlight;
            });
        return entered;
    }

    /// <summary>
    /// Report that the refresh finished — successfully or not — and start its cooldown. A failed refresh gets the
    /// same cooldown as a successful one: re-running a scan that just failed is the worse of the two mistakes.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="workspaceId"/> is null, empty, or whitespace.</exception>
    public void Release(string workspaceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        _state[workspaceId] = _nowMilliseconds();
    }
}
