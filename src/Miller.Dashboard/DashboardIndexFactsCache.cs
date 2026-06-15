using System.Collections.Concurrent;
using System.Globalization;

namespace Miller.Dashboard;

/// <summary>
/// Short-lived cache for per-workspace aggregate index facts so the landing page does not open every
/// <c>symbols.db</c> on each request. Invalidates when the index file timestamp, registry revision, or
/// registry state changes.
/// </summary>
public static class DashboardIndexFactsCache
{
    private static readonly ConcurrentDictionary<string, CacheEntry> Entries = new(StringComparer.Ordinal);

    private static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(
        int.TryParse(Environment.GetEnvironmentVariable("MILLER_DASHBOARD_INDEX_CACHE_SECONDS"), out int seconds) && seconds >= 0
            ? seconds
            : 30);

    public static DashboardWorkspaceFacts Read(DashboardWorkspaceRow workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (DefaultTtl <= TimeSpan.Zero)
            return DashboardIndexFactsReader.Read(workspace);

        string key = BuildKey(workspace);
        long writeTicks = TryGetIndexWriteTicks(workspace.IndexDbPath);
        if (Entries.TryGetValue(key, out CacheEntry? cached) && cached.IsFresh(writeTicks, DefaultTtl))
            return cached.Facts;

        DashboardWorkspaceFacts facts = DashboardIndexFactsReader.Read(workspace);
        Entries[key] = new CacheEntry(facts, writeTicks, DateTime.UtcNow);
        return facts;
    }

    public static void Clear() => Entries.Clear();

    private static string BuildKey(DashboardWorkspaceRow workspace) =>
        string.Join(
            '|',
            workspace.WorkspaceId,
            workspace.IndexDbPath,
            workspace.LastRevision?.ToString(CultureInfo.InvariantCulture) ?? "null",
            workspace.State);

    private static long TryGetIndexWriteTicks(string indexDbPath)
    {
        try
        {
            return File.Exists(indexDbPath)
                ? File.GetLastWriteTimeUtc(indexDbPath).Ticks
                : 0L;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PathTooLongException)
        {
            return 0L;
        }
    }

    private sealed record CacheEntry(DashboardWorkspaceFacts Facts, long WriteTicks, DateTime CachedAt)
    {
        public bool IsFresh(long writeTicks, TimeSpan ttl) =>
            WriteTicks == writeTicks && DateTime.UtcNow - CachedAt < ttl;
    }
}
