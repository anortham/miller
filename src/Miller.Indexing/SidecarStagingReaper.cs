namespace Miller.Indexing;

/// <summary>
/// Deletes orphaned sidecar staging files (<c>.search-build-*.db</c>, <c>.content-build-*.db</c>)
/// left behind when a process died mid-build. Staging names carry a GUID, so a crashed build's
/// file is never overwritten by the next build and accumulates forever without this reaper.
/// </summary>
public static class SidecarStagingReaper
{
    public static readonly TimeSpan DefaultStaleAge = TimeSpan.FromMinutes(15);

    private static readonly string[] StagingPrefixes = [".search-build-", ".content-build-"];

    /// <summary>
    /// Best-effort reap of every sidecar staging prefix in <paramref name="sidecarDirectory"/> for a caller
    /// that is NOT starting a build. The sidecar writers reap only as a build begins, so a workspace whose
    /// scans never reach them — one stuck in a scan error, say — never reclaims its own orphans; workspace
    /// lifecycle paths call this instead. Never throws: an unreadable directory or a held handle must leave
    /// the lifecycle call intact.
    /// </summary>
    public static int ReapWorkspaceStaging(string? sidecarDirectory, TimeSpan staleAge) =>
        ReapWorkspaceStaging(sidecarDirectory, staleAge, ReapStale);

    internal static int ReapWorkspaceStaging(
        string? sidecarDirectory,
        TimeSpan staleAge,
        Func<string, string, TimeSpan, string?, int> reap)
    {
        if (string.IsNullOrWhiteSpace(sidecarDirectory))
            return 0;

        int reaped = 0;
        foreach (string prefix in StagingPrefixes)
        {
            try
            {
                reaped += reap(sidecarDirectory, prefix, staleAge, null);
            }
            catch (Exception)
            {
                // One unreadable prefix must not cost the other prefix its reap, nor fail the caller.
            }
        }

        return reaped;
    }

    /// <summary>
    /// Best-effort delete of <paramref name="prefix"/><c>*.db</c> files in
    /// <paramref name="directory"/> not written for at least <paramref name="staleAge"/>.
    /// A live build keeps its staging file's write time fresh (SQLite writes continuously), and
    /// callers hold the single-writer lock, so age alone cannot race a legitimate sibling build;
    /// <paramref name="exceptPath"/> additionally shields the caller's own staging file.
    /// </summary>
    public static int ReapStale(string directory, string prefix, TimeSpan staleAge, string? exceptPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        if (!Directory.Exists(directory))
            return 0;

        DateTime cutoffUtc = DateTime.UtcNow - staleAge;
        int reaped = 0;
        foreach (string candidate in Directory.EnumerateFiles(directory, prefix + "*.db"))
        {
            if (exceptPath is not null
                && string.Equals(Path.GetFullPath(candidate), Path.GetFullPath(exceptPath), StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                if (File.GetLastWriteTimeUtc(candidate) >= cutoffUtc)
                    continue;
                File.Delete(candidate);
                reaped++;
            }
            catch (IOException)
            {
                // Held open or vanished between enumeration and delete; the next build retries.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return reaped;
    }
}
