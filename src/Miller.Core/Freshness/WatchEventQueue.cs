namespace Miller.Core.Freshness;

/// <summary>
/// Per-path coalescing event queue with a bounded-overflow drain. A direct port of julie's
/// <c>src/watcher/queue.rs</c> (the authoritative cross-language source), so Miller and julie collapse a
/// burst of file-system events identically.
///
/// <para>
/// <b>Coalescing.</b> Events are keyed on their affected path (<see cref="WatchEvent.Path"/>). Enqueuing an
/// event whose path already sits in the queue merges it into the most-recent matching entry <em>in place</em>
/// (preserving FIFO position) via <see cref="Merge"/>; it consumes no new slot.
/// </para>
/// <para>
/// <b>Overflow.</b> When a <em>new distinct</em> path would push the queue to <see cref="MaxQueue"/> or beyond,
/// the oldest entries are dropped from the front until the count falls to <see cref="OverflowTarget"/>, then the
/// new event is appended. Any drop sets <see cref="NeedsRescan"/> — the signal for the router to force a single
/// whole-repo <c>extract scan</c> reconcile rather than trust a lossy event stream. Merges never overflow.
/// </para>
/// <para>This type is pure logic — no FileSystemWatcher, no SQLite, no threads. It is not thread-safe; the
/// hosted watcher serializes access under its own lock (as julie does with a Tokio mutex).</para>
/// </summary>
public sealed class WatchEventQueue
{
    /// <summary>Upper bound on queued distinct paths before the overflow drain triggers (julie: <c>MAX_QUEUE_SIZE</c>).</summary>
    public const int MaxQueue = 1000;

    /// <summary>The count the overflow drain reduces the queue to (julie: <c>OVERFLOW_TARGET_SIZE</c>).</summary>
    public const int OverflowTarget = 750;

    // VecDeque equivalent: front = oldest, back = newest. LinkedList gives O(1) front-drop and tail-append.
    private readonly LinkedList<WatchEvent> _queue = new();
    private bool _needsRescan;

    /// <summary>Number of distinct-path entries currently queued (coalesced, not raw event volume).</summary>
    public int Count => _queue.Count;

    /// <summary>
    /// True once an overflow drain has dropped events since the last <see cref="ClearNeedsRescan"/>. Sticky:
    /// <see cref="Drain"/> does not clear it — the router consumes the flag and reconciles via a full scan.
    /// </summary>
    public bool NeedsRescan => _needsRescan;

    /// <summary>
    /// Enqueue an event, coalescing it into an existing same-path entry when present, otherwise appending it
    /// (running the overflow drain first if the queue is at <see cref="MaxQueue"/>).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="incoming"/> is null.</exception>
    public void Enqueue(WatchEvent incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);

        // Coalesce into the most-recent matching path (julie uses rposition: search from the back).
        for (var node = _queue.Last; node is not null; node = node.Previous)
        {
            if (string.Equals(node.Value.Path, incoming.Path, StringComparison.Ordinal))
            {
                node.Value = Merge(node.Value, incoming);
                return; // merge consumes no slot and cannot overflow
            }
        }

        // New distinct path: drain headroom if at/over the cap, then append.
        if (_queue.Count >= MaxQueue)
        {
            while (_queue.Count > OverflowTarget)
            {
                _queue.RemoveFirst(); // drop oldest
            }
            _needsRescan = true;
        }

        _queue.AddLast(incoming);
    }

    /// <summary>
    /// Drain and return all queued events in FIFO order (oldest first), emptying the queue. Does NOT clear
    /// <see cref="NeedsRescan"/> — call <see cref="ClearNeedsRescan"/> after the rescan has been scheduled.
    /// </summary>
    public IReadOnlyList<WatchEvent> Drain()
    {
        if (_queue.Count == 0)
            return Array.Empty<WatchEvent>();

        var drained = new List<WatchEvent>(_queue.Count);
        for (var node = _queue.First; node is not null; node = node.Next)
            drained.Add(node.Value);
        _queue.Clear();
        return drained;
    }

    /// <summary>Reset <see cref="NeedsRescan"/> to false after the forced rescan has been scheduled.</summary>
    public void ClearNeedsRescan() => _needsRescan = false;

    /// <summary>
    /// Coalesce <paramref name="existing"/> with a later <paramref name="incoming"/> event for the same path.
    /// A faithful port of julie's <c>merge_file_change</c> table:
    /// <list type="bullet">
    /// <item>(Modified, Modified) → Modified</item>
    /// <item>(Created, Modified) → Created (a freshly-created file edited before indexing is still a create)</item>
    /// <item>(Deleted, Created|Modified) → Modified (delete-then-reappear is a content change)</item>
    /// <item>(Created, Deleted) → Deleted (created and removed before indexing collapses to a delete)</item>
    /// <item>(Renamed, Modified) → Renamed (a trailing modify of the rename destination keeps the rename)</item>
    /// <item>anything else → the incoming event (last-write-wins)</item>
    /// </list>
    /// Pure static; the same inputs always yield the same output.
    /// </summary>
    public static WatchEvent Merge(WatchEvent existing, WatchEvent incoming)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(incoming);

        return (existing.Kind, incoming.Kind) switch
        {
            (WatchEventKind.Modified, WatchEventKind.Modified) => incoming,
            (WatchEventKind.Created, WatchEventKind.Modified) => existing,
            (WatchEventKind.Deleted, WatchEventKind.Created) => Modify(incoming.Path),
            (WatchEventKind.Deleted, WatchEventKind.Modified) => incoming,
            (WatchEventKind.Created, WatchEventKind.Deleted) => incoming,
            (WatchEventKind.Renamed, WatchEventKind.Modified) => existing,
            _ => incoming,
        };
    }

    private static WatchEvent Modify(string path) => new(path, WatchEventKind.Modified);
}
