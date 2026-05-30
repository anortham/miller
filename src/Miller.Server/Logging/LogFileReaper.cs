namespace Miller.Server.Logging;

/// <summary>
/// The pure, I/O-free planner for the startup sweep of stale per-pid log files (m8-design §D6). With per-process
/// log files (<c>miller-&lt;pid&gt;-*.log/.jsonl</c>, decision §D1), Serilog prunes the dated rolls WITHIN one pid
/// prefix but never the abandoned prefixes of pids that have since exited — so they accumulate forever as pids
/// churn. <see cref="Plan"/> decides which files to delete; the actual <c>File.Delete</c> is a thin best-effort
/// infra step the caller performs.
///
/// <para><b>Rules.</b> Files are grouped by their owning <see cref="LogFileInfo.Pid"/>. The CURRENT process's pid
/// is NEVER deleted (it owns the live file), and neither is any pid in <c>livePids</c> — a still-running PEER
/// (M3: one leader + N readers share <c>&lt;root&gt;/.miller/logs</c>) owns an OPEN file whose mtime can freeze
/// near its start when it idles, so a recency-only cull would wrongly target it and, on macOS/Linux,
/// <c>File.Delete</c> would silently unlink the open file out from under it (POSIX unlink throws nothing).
/// Liveness is therefore excluded BEFORE the recency cull, and live pids do NOT consume a keep slot. Of the
/// remaining DEAD non-current pids, the newest <paramref name="keep"/> by their most-recent
/// <see cref="LogFileInfo.LastWriteUtc"/> are kept; every file of an older dead pid is returned for deletion (a
/// pid's files are kept or deleted as a group). The result order is deterministic: oldest pid first, then by path
/// within a pid, so the plan is stable across runs and easy to assert.</para>
/// </summary>
public static class LogFileReaper
{
    /// <summary>
    /// Plan which stale per-pid log files to delete, treating only the current pid as live (the recency-only
    /// behavior). Prefer the <see cref="Plan(IEnumerable{LogFileInfo}, int, int, IReadOnlySet{int})"/> overload
    /// in the multi-process daemon so a still-running peer's files are never targeted; this overload is the pure
    /// recency planner for unit tests and single-process callers.
    /// </summary>
    /// <param name="files">The discovered per-pid log files (any order; may be empty).</param>
    /// <param name="keep">
    /// How many non-current pids to retain (newest by last-write). Values &lt; 0 are treated as 0; a value at or
    /// above the non-current pid count keeps everything (returns empty).
    /// </param>
    /// <param name="currentPid">This process's pid — its files are never deleted.</param>
    /// <returns>The paths to delete, oldest-pid-first then path-ordered. Deterministic; never null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="files"/> is null.</exception>
    public static IReadOnlyList<string> Plan(IEnumerable<LogFileInfo> files, int keep, int currentPid) =>
        Plan(files, keep, currentPid, EmptyPidSet);

    // A reused empty set so the recency-only overload allocates nothing extra on each call.
    private static readonly IReadOnlySet<int> EmptyPidSet = new HashSet<int>();

    /// <summary>
    /// Plan which stale per-pid log files to delete, never targeting the current pid OR any still-running peer in
    /// <paramref name="livePids"/>. See the type remarks for the liveness + grouping + keep rules.
    /// </summary>
    /// <param name="files">The discovered per-pid log files (any order; may be empty).</param>
    /// <param name="keep">
    /// How many DEAD non-current pids to retain (newest by last-write). Values &lt; 0 are treated as 0; a value at
    /// or above the dead non-current pid count keeps everything (returns empty). Live pids are kept regardless and
    /// do NOT consume a keep slot.
    /// </param>
    /// <param name="currentPid">This process's pid — its files are never deleted.</param>
    /// <param name="livePids">
    /// The pids of processes still running (probed by the caller). Their files are never deleted, and they are
    /// excluded before the recency cull so a live peer's frozen-mtime file cannot fall out of the keep window.
    /// </param>
    /// <returns>The paths to delete, oldest-pid-first then path-ordered. Deterministic; never null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="files"/> or <paramref name="livePids"/> is null.</exception>
    public static IReadOnlyList<string> Plan(
        IEnumerable<LogFileInfo> files, int keep, int currentPid, IReadOnlySet<int> livePids)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(livePids);

        int keepCount = Math.Max(0, keep);

        // Group every file by its owning pid, excluding the current pid AND any still-running peer outright (a live
        // process's open file is sacrosanct — never count it toward the keep window, never plan it for deletion).
        var byPid = files
            .Where(f => f.Pid != currentPid && !livePids.Contains(f.Pid))
            .GroupBy(f => f.Pid)
            .Select(g => new
            {
                Pid = g.Key,
                // A pid's recency is its most-recent write across all of its rolled files.
                Newest = g.Max(f => f.LastWriteUtc),
                Files = g.ToList(),
            })
            .ToList();

        // Rank pids newest-first (tie-break by pid so the choice is deterministic), keep the first keepCount,
        // and the rest are stale. Empty when keepCount covers (or exceeds) the candidate pids.
        var stalePids = byPid
            .OrderByDescending(p => p.Newest)
            .ThenByDescending(p => p.Pid)
            .Skip(keepCount)
            .ToList();

        // Emit the stale files oldest-pid-first, path-ordered within a pid, for a stable, assertable plan.
        return stalePids
            .OrderBy(p => p.Newest)
            .ThenBy(p => p.Pid)
            .SelectMany(p => p.Files
                .Select(f => f.Path)
                .OrderBy(path => path, StringComparer.Ordinal))
            .ToList();
    }
}

/// <summary>
/// One per-pid log file the startup sweep considers: its <see cref="Path"/>, the <see cref="Pid"/> that owns it
/// (parsed from the <c>miller-&lt;pid&gt;-*</c> name), and its <see cref="LastWriteUtc"/> (the file's mtime, used
/// to rank pids by recency).
/// </summary>
/// <param name="Path">The absolute path to the log file.</param>
/// <param name="Pid">The owning process id parsed from the file name.</param>
/// <param name="LastWriteUtc">The file's last-write timestamp (UTC), used to rank pid recency.</param>
public readonly record struct LogFileInfo(string Path, int Pid, DateTime LastWriteUtc);
