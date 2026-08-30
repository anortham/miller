using System.Collections.Concurrent;
using System.Globalization;
using Miller.Indexing;
using Miller.Indexing.Reads;

namespace Miller.Dashboard;

/// <summary>
/// Short-lived cache for per-workspace aggregate index facts so the landing page does not open every index on
/// each request. It caches in BOTH read modes. The key is identity only — the mode plus the workspace id — and
/// every changing input lives in the stamp, so a new revision replaces an entry instead of adding one that is
/// never released.
///
/// <para>Store mode is witnessed by <see cref="WorkspaceReadSessionFactory.Probe(string, string, string?, bool?)"/>:
/// the generation identity, the manifest generation, the manifest hash and the store-log sequence. The legacy
/// <c>symbols.db</c> timestamp is NOT that witness and is never read as one — under store mode the legacy file is
/// optional and frozen, so a current-generation change would hide behind it
/// (<c>docs/findings/2026-08-09-index-store-ph3-acceptance.md</c>). Only its PRESENCE is folded in, because that
/// is what flips the facts between a preserved legacy export and a native store view. A probe that cannot answer
/// reads uncached, so an unreadable or unbound store is never served from cache.</para>
///
/// <para>The stamp also carries the registry row, the sidecar files, the producer binary version and the
/// store-wide views identity (<see cref="DashboardStoreViewsWitness"/>), because the facts copy all of them and
/// none of them moves the store-log sequence. The views identity is the load-bearing one: the facts summarize
/// EVERY view in the store, so registering or retiring any other workspace changes what this one renders.</para>
/// </summary>
public static class DashboardIndexFactsCache
{
    private static readonly ConcurrentDictionary<string, CacheEntry> Entries = new(StringComparer.Ordinal);

    private const int MaxEntries = 256;

    private static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(
        int.TryParse(Environment.GetEnvironmentVariable("MILLER_DASHBOARD_INDEX_CACHE_SECONDS"), out int seconds) && seconds >= 0
            ? seconds
            : 120);

    public static DashboardWorkspaceFacts Read(DashboardWorkspaceRow workspace, bool? storeEnabled = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        bool enabled = storeEnabled ?? WorkspaceReadSessionFactory.StoreEnabledFromEnvironment();
        if (DefaultTtl <= TimeSpan.Zero)
            return DashboardIndexFactsReader.Read(workspace, storeEnabled: enabled);

        string? stamp = enabled ? TryBuildStoreStamp(workspace) : BuildLegacyStamp(workspace);
        if (stamp is null)
            return DashboardIndexFactsReader.Read(workspace, storeEnabled: enabled);

        string key = BuildKey(workspace, enabled);
        TimeSpan ttl = TtlFor(workspace.WorkspaceId);
        if (Entries.TryGetValue(key, out CacheEntry? cached) && cached.IsFresh(stamp, ttl))
            return cached.Facts;

        DashboardWorkspaceFacts facts = DashboardIndexFactsReader.Read(workspace, storeEnabled: enabled);
        Entries[key] = new CacheEntry(facts, stamp, DateTime.UtcNow);
        TrimToCapacity();
        return facts;
    }

    public static void Clear()
    {
        Entries.Clear();
        DashboardStoreViewsWitness.Clear();
    }

    /// <summary>
    /// Drops the least recently cached entries once the map is larger than a registry realistically serves.
    /// An entry is replaced only when its own workspace is read again, so a workspace removed through the CLI
    /// or MCP is never read again and would otherwise hold its fact graph for the life of the process — and this
    /// registry grows a row for every agent worktree, which nothing retires on its own.
    /// </summary>
    private static void TrimToCapacity()
    {
        if (Entries.Count <= MaxEntries)
            return;

        foreach (KeyValuePair<string, CacheEntry> entry in Entries
            .OrderBy(entry => entry.Value.CachedAt)
            .Take(Entries.Count - MaxEntries))
        {
            Entries.TryRemove(entry);
        }
    }

    internal static int Count => Entries.Count;

    internal static int Capacity => MaxEntries;

    private static string BuildKey(DashboardWorkspaceRow workspace, bool storeEnabled) =>
        (storeEnabled ? "store|" : "legacy|") + workspace.WorkspaceId;

    private static string BuildLegacyStamp(DashboardWorkspaceRow workspace) =>
        string.Join(
            '|',
            TryGetIndexWriteTicks(workspace.IndexDbPath).ToString(CultureInfo.InvariantCulture),
            LegacySidecarState(workspace.IndexDbPath),
            RegistryState(workspace));

    private static string? TryBuildStoreStamp(DashboardWorkspaceRow workspace)
    {
        WorkspaceFreshnessProbe probe;
        try
        {
            probe = WorkspaceReadSessionFactory.Probe(
                workspace.IndexDbPath,
                workspace.CanonicalRoot,
                workspace.WorkspaceId,
                storeEnabled: true);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or InvalidOperationException
                or ArgumentException or NotSupportedException)
        {
            return null;
        }

        // A store that cannot name its views cannot witness the member summary the facts copy, so that read
        // stays uncached rather than being stamped with a witness that is missing.
        string? views = DashboardStoreViewsWitness.Read(probe.StoreRoot);
        if (views is null)
            return null;

        return string.Join(
            '|',
            probe.IndexGenerationIdentity ?? "null",
            probe.ManifestGeneration?.ToString(CultureInfo.InvariantCulture) ?? "null",
            probe.ManifestHash ?? "null",
            probe.Revision.ToString(CultureInfo.InvariantCulture),
            probe.BinaryVersion ?? "null",
            views,
            File.Exists(workspace.IndexDbPath) ? "legacy_export" : "native",
            StoreSidecarState(probe.StoreRoot, probe.ViewId),
            RegistryState(workspace));
    }

    // `last_seen_at` is deliberately absent: the registry rewrites it on every touch and the facts never copy it,
    // so folding it in would expire every entry before a later request could reuse it.
    private static string RegistryState(DashboardWorkspaceRow workspace) =>
        string.Join(
            '|',
            workspace.DisplayId,
            workspace.CanonicalRoot,
            workspace.IndexDbPath,
            workspace.LastRevision?.ToString(CultureInfo.InvariantCulture) ?? "null",
            workspace.State,
            workspace.LastScanAt ?? "null",
            workspace.LastError ?? "null");

    private static string LegacySidecarState(string indexDbPath)
    {
        try
        {
            return string.Join(
                '|',
                FileState(SymbolSearchSidecar.SearchDbPathFor(indexDbPath)),
                FileState(ContentCorpusSidecar.ContentDbPathFor(indexDbPath)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return "unknown|unknown";
        }
    }

    private static string StoreSidecarState(string? storeRoot, string? viewId)
    {
        if (string.IsNullOrWhiteSpace(storeRoot) || string.IsNullOrWhiteSpace(viewId))
            return "unknown|unknown";

        try
        {
            return string.Join(
                '|',
                FileState(StoreSidecarCatalog.PathFor(storeRoot, StoreSidecarKind.Search, viewId)),
                FileState(StoreSidecarCatalog.PathFor(storeRoot, StoreSidecarKind.Content, viewId)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return "unknown|unknown";
        }
    }

    private static string FileState(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists
                ? string.Concat(
                    info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture),
                    ":",
                    info.Length.ToString(CultureInfo.InvariantCulture))
                : "absent";
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or PathTooLongException
                or ArgumentException or NotSupportedException)
        {
            return "unknown";
        }
    }

    /// <summary>
    /// The TTL for one workspace, shortened by up to a quarter by a hash of its id so a registry of many
    /// workspaces does not expire every entry into the same poll.
    /// </summary>
    private static TimeSpan TtlFor(string workspaceId)
    {
        long spread = DefaultTtl.Ticks / 4;
        return spread <= 0
            ? DefaultTtl
            : TimeSpan.FromTicks(DefaultTtl.Ticks - (long)(DashboardStableHash.Of(workspaceId) % (ulong)spread));
    }

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

    private sealed record CacheEntry(DashboardWorkspaceFacts Facts, string Stamp, DateTime CachedAt)
    {
        public bool IsFresh(string stamp, TimeSpan ttl) =>
            string.Equals(Stamp, stamp, StringComparison.Ordinal) && DateTime.UtcNow - CachedAt < ttl;
    }
}
