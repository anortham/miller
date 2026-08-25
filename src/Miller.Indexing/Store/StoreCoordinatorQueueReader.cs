using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Miller.Indexing.Store;

/// <summary>One (kind, state) bucket of the family-store coordinator queue.</summary>
public sealed record StoreCoordinatorQueueGroup(string Kind, string State, long Count);

/// <summary>
/// What the family-store coordinator queue holds right now: the pending work, how long the oldest queued
/// request has waited, and whether a <c>claimed</c> request names an owner process that no longer exists.
/// </summary>
/// <param name="QueuedCount">Requests waiting for a claim.</param>
/// <param name="ClaimedCount">Requests a writer has claimed.</param>
/// <param name="OldestQueuedAgeSeconds">Age of the oldest queued request, or null when none is queued.</param>
/// <param name="DeadClaimOwner">A claim owner the liveness probe proved gone, or null.</param>
/// <param name="Groups">The (kind, state) counts, ordered by kind then state.</param>
/// <param name="WedgedAfterSeconds">The queued-age threshold this reading was judged against.</param>
public sealed record StoreCoordinatorQueueFacts(
    long QueuedCount,
    long ClaimedCount,
    long? OldestQueuedAgeSeconds,
    string? DeadClaimOwner,
    IReadOnlyList<StoreCoordinatorQueueGroup> Groups,
    long WedgedAfterSeconds)
{
    /// <summary>
    /// True when the queue cannot drain on its own: a request whose claim owner is gone will never be
    /// finished by that owner, and a request nobody has claimed for longer than the threshold has no writer.
    /// </summary>
    public bool Wedged =>
        DeadClaimOwner is not null
        || (OldestQueuedAgeSeconds is { } age && age >= WedgedAfterSeconds);

    /// <summary>A one-line, human-readable statement of what the queue holds. Never null.</summary>
    public string Description
    {
        get
        {
            string counts =
                $"queued {QueuedCount.ToString(CultureInfo.InvariantCulture)}, " +
                $"claimed {ClaimedCount.ToString(CultureInfo.InvariantCulture)}";
            if (DeadClaimOwner is { } owner)
                return $"{counts}; claim owner '{owner}' is gone";
            if (OldestQueuedAgeSeconds is { } age && Wedged)
                return $"{counts}; oldest queued {age.ToString(CultureInfo.InvariantCulture)}s with no writer";
            if (OldestQueuedAgeSeconds is { } waiting)
                return $"{counts}; oldest queued {waiting.ToString(CultureInfo.InvariantCulture)}s";
            return counts;
        }
    }
}

/// <summary>
/// A read-only, bounded look at the family-store coordinator queue (<c>coord.db</c>).
///
/// <para>Miller wedged that queue in the field and <c>workspace status</c>/<c>health</c> showed nothing at
/// all: every Miller-side signal reported the SERVED view, which stays perfectly readable while new work
/// stops arriving. This reader is the missing fact. It never writes, never deletes a row, and never repairs
/// anything — coordinator repair belongs to julie-extract — and it fails soft in every direction: a missing,
/// locked, or malformed <c>coord.db</c> yields <c>null</c>, which renders nowhere.</para>
///
/// <para>It returns <c>null</c> for an EMPTY queue too, so a healthy workspace's status and health output
/// stays byte-identical to a build without it — the same conditional-presence rule as <c>scan_failure</c>.</para>
/// </summary>
public static class StoreCoordinatorQueueReader
{
    /// <summary>Operator override for <see cref="DefaultWedgedAfterSeconds"/>.</summary>
    public const string WedgedAfterSecondsEnvVar = "MILLER_STORE_QUEUE_WEDGED_SECONDS";

    /// <summary>
    /// The phrase every wedged-queue diagnostic carries. Single-sourced so a consumer that must recommend a
    /// different remedy — a refresh cannot drain a blocked queue — recognizes the reason by one agreed string
    /// rather than by re-matching prose.
    /// </summary>
    public const string BlockedQueueMarker = "family-store coordinator queue is blocked";

    /// <summary>
    /// How long a request may sit UNCLAIMED before the queue is called wedged. Matches the coordinator's
    /// default per-request timeout: past it, no live writer is working the queue.
    /// </summary>
    public const long DefaultWedgedAfterSeconds = 300;

    // Twelve buckets is the whole (kind, state) space for pending work; the owner list is capped for the same
    // reason. Neither query may become a scan of a queue somebody let grow.
    private const int MaxGroups = 12;
    private const int MaxClaimOwners = 16;

    /// <summary>
    /// The pending queue at <paramref name="storeRoot"/>, or null when the queue is empty or unreadable.
    /// </summary>
    public static StoreCoordinatorQueueFacts? Read(string? storeRoot) =>
        Read(storeRoot, DateTimeOffset.UtcNow, IsProcessAlive, WedgedAfterSecondsFromEnvironment());

    internal static StoreCoordinatorQueueFacts? Read(
        string? storeRoot,
        DateTimeOffset nowUtc,
        Func<int, bool> isProcessAlive,
        long wedgedAfterSeconds)
    {
        ArgumentNullException.ThrowIfNull(isProcessAlive);
        if (string.IsNullOrWhiteSpace(storeRoot))
            return null;

        string coordinatorPath = Path.Combine(storeRoot, "coord.db");
        if (!File.Exists(coordinatorPath))
            return null;

        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(coordinatorPath),
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            connection.Open();

            (List<StoreCoordinatorQueueGroup> groups, long? oldestQueuedMillis) = ReadGroups(connection);
            if (groups.Count == 0)
                return null;

            long queued = groups
                .Where(static group => string.Equals(group.State, "queued", StringComparison.Ordinal))
                .Sum(static group => group.Count);
            long claimed = groups
                .Where(static group => string.Equals(group.State, "claimed", StringComparison.Ordinal))
                .Sum(static group => group.Count);
            long? oldestQueuedAge = oldestQueuedMillis is { } created
                ? Math.Max(0, (long)(nowUtc - DateTimeOffset.FromUnixTimeMilliseconds(created)).TotalSeconds)
                : null;

            return new StoreCoordinatorQueueFacts(
                queued,
                claimed,
                oldestQueuedAge,
                claimed == 0 ? null : FirstDeadClaimOwner(connection, isProcessAlive),
                groups,
                wedgedAfterSeconds);
        }
        catch (Exception failure) when (
            failure is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    internal static long WedgedAfterSecondsFromEnvironment() =>
        ParseWedgedAfterSeconds(Environment.GetEnvironmentVariable(WedgedAfterSecondsEnvVar));

    /// <summary>
    /// The pure env-value ⇒ threshold mapping. Blank, unparseable, and negative values fall back to the
    /// default; <c>0</c> is honored because it is the only way to say "any unclaimed request is a wedge",
    /// which a diagnosis run wants.
    /// </summary>
    internal static long ParseWedgedAfterSeconds(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return DefaultWedgedAfterSeconds;

        return long.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
               && parsed >= 0
            ? parsed
            : DefaultWedgedAfterSeconds;
    }

    private static (List<StoreCoordinatorQueueGroup> Groups, long? OldestQueuedMillis) ReadGroups(
        SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT kind, state, COUNT(*), MIN(created_at)
            FROM requests
            WHERE state IN ('queued', 'claimed')
            GROUP BY kind, state
            ORDER BY kind, state
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", MaxGroups);
        using SqliteDataReader reader = command.ExecuteReader();

        var groups = new List<StoreCoordinatorQueueGroup>();
        long? oldestQueuedMillis = null;
        while (reader.Read())
        {
            string state = reader.GetString(1);
            groups.Add(new StoreCoordinatorQueueGroup(reader.GetString(0), state, reader.GetInt64(2)));
            if (!string.Equals(state, "queued", StringComparison.Ordinal) || reader.IsDBNull(3))
                continue;
            long created = reader.GetInt64(3);
            oldestQueuedMillis = oldestQueuedMillis is { } current ? Math.Min(current, created) : created;
        }

        return (groups, oldestQueuedMillis);
    }

    private static string? FirstDeadClaimOwner(SqliteConnection connection, Func<int, bool> isProcessAlive)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT claim_owner
            FROM requests
            WHERE state = 'claimed' AND claim_owner IS NOT NULL
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", MaxClaimOwners);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string owner = reader.GetString(0);
            if (TryReadOwnerPid(owner) is { } pid && !isProcessAlive(pid))
                return owner;
        }

        return null;
    }

    /// <summary>
    /// The pid julie-extract encodes in a claim owner (<c>cli-&lt;pid&gt;</c>), or null when the owner does not
    /// carry one. An owner whose pid cannot be read is never called dead — an unreadable identity is unknown,
    /// and reporting unknown as a wedge would make every future owner format a false alarm.
    /// </summary>
    internal static int? TryReadOwnerPid(string? claimOwner)
    {
        if (string.IsNullOrWhiteSpace(claimOwner))
            return null;
        int separator = claimOwner.LastIndexOf('-');
        if (separator < 0 || separator == claimOwner.Length - 1)
            return null;
        return int.TryParse(
            claimOwner[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int pid)
            && pid > 0
            ? pid
            : null;
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return true;
        }
        catch (Exception failure) when (failure is ArgumentException or InvalidOperationException)
        {
            return false;
        }
        catch (Exception)
        {
            // An unanswerable probe is not evidence of death; a live owner must never be reported gone.
            return true;
        }
    }
}
