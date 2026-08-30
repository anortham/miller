using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Miller.Dashboard;

/// <summary>
/// The identity of the family store's whole <c>views</c> table, memoized per store root.
///
/// <para>Every workspace's facts copy a summary of EVERY view in the store — <c>StoreMemberSummaryReader</c>
/// counts and labels the whole table, not this view's own rows — so registering or retiring ANY workspace
/// changes the member count and labels that every other workspace renders. Nothing about that write moves a
/// single view's manifest generation, manifest hash or store-log sequence, so without this witness the
/// per-workspace freshness stamp would hold a stale member summary for a whole cache TTL.</para>
///
/// <para>Null when the store cannot answer. The caller reads uncached on null, so an unreadable or
/// mid-promotion store is never served from cache.</para>
/// </summary>
internal static class DashboardStoreViewsWitness
{
    /// <summary>
    /// How long one reading serves. The witness is store-wide and identical for every workspace, so this is
    /// what keeps a landing page of many workspaces to a single store read rather than one read per row.
    /// </summary>
    private static readonly TimeSpan MemoWindow = TimeSpan.FromSeconds(2);

    private static readonly ConcurrentDictionary<string, Memo> Memos = new(StringComparer.Ordinal);

    public static string? Read(string? storeRoot) => Read(storeRoot, DateTime.UtcNow);

    internal static string? Read(string? storeRoot, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(storeRoot))
            return null;

        if (Memos.TryGetValue(storeRoot, out Memo? memo) && now - memo.ReadAt < MemoWindow)
            return memo.Witness;

        string? witness = ReadUncached(storeRoot);
        Memos[storeRoot] = new Memo(witness, now);
        return witness;
    }

    internal static void Clear() => Memos.Clear();

    private static string? ReadUncached(string storeRoot)
    {
        try
        {
            string currentPath = Path.Combine(storeRoot, "CURRENT");
            if (!File.Exists(currentPath))
                return null;

            string generation = File.ReadAllText(currentPath).Trim();
            if (string.IsNullOrWhiteSpace(generation) ||
                generation is "." or ".." ||
                generation.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            {
                return null;
            }

            string databasePath = Path.Combine(storeRoot, generation, "store.db");
            if (!File.Exists(databasePath))
                return null;

            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            connection.Open();
            using (SqliteCommand pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA query_only=ON; PRAGMA busy_timeout=1000;";
                pragma.ExecuteNonQuery();
            }

            using SqliteCommand views = connection.CreateCommand();
            views.CommandText = "SELECT view_id, root FROM views ORDER BY view_id";
            using SqliteDataReader reader = views.ExecuteReader();

            var digest = new StringBuilder();
            int count = 0;
            while (reader.Read())
            {
                digest.Append(reader.GetString(0)).Append(' ').Append(reader.GetString(1)).Append('|');
                count++;
            }

            return string.Concat(
                generation,
                ":",
                count.ToString(CultureInfo.InvariantCulture),
                ":",
                DashboardStableHash.Of(digest.ToString()).ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (
            ex is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException
                or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private sealed record Memo(string? Witness, DateTime ReadAt);
}
