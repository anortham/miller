using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Miller.Server.Workspaces;

/// <summary>What one full-scan drain observed: whether a valid scan request was serviced, plus the TTL/claim
/// bookkeeping the caller (the leader's debounce tick) logs.</summary>
internal sealed record FullScanDrainResult(bool Requested, int ExpiredDiscarded, int ClaimSkipped)
{
    public static FullScanDrainResult Empty { get; } = new(Requested: false, ExpiredDiscarded: 0, ClaimSkipped: 0);
}

/// <summary>What one file-converge drain observed: the deduplicated requested paths in request order, plus the
/// TTL/claim bookkeeping the caller (the leader's debounce tick) logs.</summary>
internal sealed record FileConvergeDrainResult(IReadOnlyList<string> Paths, int ExpiredDiscarded, int ClaimSkipped)
{
    public static FileConvergeDrainResult Empty { get; } = new(Paths: [], ExpiredDiscarded: 0, ClaimSkipped: 0);
}

internal static partial class LeaderScanRequestQueue
{
    private const int SchemaVersion = 1;
    private const string OperationFullScan = "full_scan";
    private const string OperationFileConverge = "file_converge";
    private const string RequestDirectoryName = "requests";
    private const string FullScanSuffix = ".full-scan.json";
    private const string FileConvergeSuffix = ".file-converge.json";
    private const string ClaimedSuffix = ".claimed";
    private const string StampFormat = "yyyyMMddHHmmssfffffff";

    /// <summary>
    /// How long an unserviced request (or a leftover claimed file) may sit before the drain discards it without
    /// servicing. A request can only rot when no drain-capable leader consumed it (e.g. the lock holder is an old
    /// build that never drains this kind) — servicing a 10-minute-old converge is pointless (the requesting gate
    /// poll gave up within seconds) and a forever-growing requests dir would surprise-scan a future leader.
    /// </summary>
    internal static readonly TimeSpan RequestTtl = TimeSpan.FromMinutes(10);

    public static void RequestFullScan(string millerDir, string workspaceId, long baselineRevision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(millerDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        if (baselineRevision < 0)
            throw new ArgumentOutOfRangeException(
                nameof(baselineRevision), baselineRevision, "Baseline revision must be non-negative.");

        string requestDir = RequestDirectoryFor(millerDir);
        Directory.CreateDirectory(requestDir);

        string requestId = Guid.NewGuid().ToString("N");
        string stamp = DateTimeOffset.UtcNow.ToString(StampFormat, CultureInfo.InvariantCulture);
        string finalPath = Path.Combine(requestDir, $"{stamp}-{Environment.ProcessId}-{requestId}{FullScanSuffix}");
        string tempPath = finalPath + ".tmp";
        var request = new FullScanRequest(
            SchemaVersion,
            OperationFullScan,
            requestId,
            workspaceId,
            baselineRevision,
            DateTimeOffset.UtcNow,
            Environment.ProcessId);

        try
        {
            File.WriteAllText(
                tempPath,
                JsonSerializer.Serialize(request, LeaderScanRequestJsonContext.Default.FullScanRequest));
            File.Move(tempPath, finalPath);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    public static FullScanDrainResult DrainFullScanRequests(string millerDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(millerDir);

        string requestDir = RequestDirectoryFor(millerDir);
        if (!Directory.Exists(requestDir))
            return FullScanDrainResult.Empty;

        DateTimeOffset now = DateTimeOffset.UtcNow;
        int expired = SweepExpiredClaims(requestDir, FullScanSuffix, now);
        int skipped = 0;
        bool requested = false;
        foreach (string path in Directory.EnumerateFiles(requestDir, "*" + FullScanSuffix).Order(StringComparer.Ordinal))
        {
            if (IsExpired(path, now))
            {
                TryDelete(path); // too old to be worth a whole-repo scan; the requester's poll gave up long ago
                expired++;
                continue;
            }

            if (!TryClaim(path, out string claimedPath))
            {
                skipped++;
                continue;
            }
            if (claimedPath.Length == 0)
                continue; // vanished before the claim: another process raced us; nothing to service

            try
            {
                string json = File.ReadAllText(claimedPath);
                FullScanRequest? request = JsonSerializer.Deserialize(
                    json,
                    LeaderScanRequestJsonContext.Default.FullScanRequest);
                if (request is { SchemaVersion: SchemaVersion, Operation: OperationFullScan })
                    requested = true;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // Malformed or unreadable AFTER a successful claim: we own it now, so drop it rather than
                // re-reading it forever. The TTL sweep is the backstop if even the delete below fails.
            }
            finally
            {
                TryDelete(claimedPath);
            }
        }

        return new FullScanDrainResult(requested, expired, skipped);
    }

    /// <summary>
    /// Ask the indexer leader (possibly another process) to reindex specific files — the single-file analog of
    /// <see cref="RequestFullScan"/>. Written by readers whose write-through/gate-time recovery cannot reindex
    /// inline; drained by the leader's debounce tick. Empty/whitespace paths are dropped; an all-empty request
    /// writes nothing.
    /// </summary>
    public static void RequestFileConverge(string millerDir, string workspaceId, IReadOnlyList<string> fullPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(millerDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentNullException.ThrowIfNull(fullPaths);

        string[] paths = fullPaths.Where(static p => !string.IsNullOrWhiteSpace(p)).ToArray();
        if (paths.Length == 0)
            return;

        string requestDir = RequestDirectoryFor(millerDir);
        Directory.CreateDirectory(requestDir);

        string requestId = Guid.NewGuid().ToString("N");
        string stamp = DateTimeOffset.UtcNow.ToString(StampFormat, CultureInfo.InvariantCulture);
        string finalPath = Path.Combine(requestDir, $"{stamp}-{Environment.ProcessId}-{requestId}{FileConvergeSuffix}");
        string tempPath = finalPath + ".tmp";
        var request = new FileConvergeRequest(
            SchemaVersion,
            OperationFileConverge,
            requestId,
            workspaceId,
            paths,
            DateTimeOffset.UtcNow,
            Environment.ProcessId);

        try
        {
            File.WriteAllText(
                tempPath,
                JsonSerializer.Serialize(request, LeaderScanRequestJsonContext.Default.FileConvergeRequest));
            File.Move(tempPath, finalPath);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    /// <summary>
    /// Drain pending single-file converge requests, returning the deduplicated paths in request order. Requests
    /// older than <see cref="RequestTtl"/> are discarded without servicing; a request that cannot be claimed
    /// (renamed) right now is skipped this tick rather than serviced unclaimed.
    /// </summary>
    public static FileConvergeDrainResult DrainFileConvergeRequests(string millerDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(millerDir);

        string requestDir = RequestDirectoryFor(millerDir);
        if (!Directory.Exists(requestDir))
            return FileConvergeDrainResult.Empty;

        DateTimeOffset now = DateTimeOffset.UtcNow;
        int expired = SweepExpiredClaims(requestDir, FileConvergeSuffix, now);
        int skipped = 0;
        List<string>? paths = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(requestDir, "*" + FileConvergeSuffix).Order(StringComparer.Ordinal))
        {
            if (IsExpired(path, now))
            {
                TryDelete(path); // the requesting gate poll gave up within seconds; a 10-minute-old converge is noise
                expired++;
                continue;
            }

            if (!TryClaim(path, out string claimedPath))
            {
                skipped++;
                continue;
            }
            if (claimedPath.Length == 0)
                continue; // vanished before the claim: another process raced us; nothing to service

            try
            {
                string json = File.ReadAllText(claimedPath);
                FileConvergeRequest? request = JsonSerializer.Deserialize(
                    json,
                    LeaderScanRequestJsonContext.Default.FileConvergeRequest);
                if (request is { SchemaVersion: SchemaVersion, Operation: OperationFileConverge, Paths: { } requestedPaths })
                {
                    foreach (string requestedPath in requestedPaths)
                    {
                        if (!string.IsNullOrWhiteSpace(requestedPath) && seen.Add(requestedPath))
                            (paths ??= []).Add(requestedPath);
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // Malformed or unreadable AFTER a successful claim: we own it now, so drop it rather than
                // re-reading it forever. The TTL sweep is the backstop if even the delete below fails.
            }
            finally
            {
                TryDelete(claimedPath);
            }
        }

        return new FileConvergeDrainResult((IReadOnlyList<string>?)paths ?? [], expired, skipped);
    }

    // Claim a request for exclusive servicing by renaming it out of the drain's enumeration pattern. Returns
    // false when the file exists but cannot be moved (held open, undeletable, permission-denied) — the caller
    // must SKIP it this tick instead of servicing an unclaimed request every 250ms forever (M4). Returns true
    // with an empty claimedPath when the file vanished first (another process raced us; nothing to service).
    private static bool TryClaim(string path, out string claimedPath)
    {
        string target = path + ClaimedSuffix;
        try
        {
            File.Move(path, target);
            claimedPath = target;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            claimedPath = string.Empty;
            return File.Exists(path) ? false : true;
        }
    }

    // Delete claimed leftovers older than the TTL: a leader that claimed a request and crashed before deleting
    // it must not strand the file forever (claimed names no longer match the drain pattern, so only this sweep
    // removes them). Returns how many were swept.
    private static int SweepExpiredClaims(string requestDir, string suffix, DateTimeOffset now)
    {
        int swept = 0;
        foreach (string claimed in Directory.EnumerateFiles(requestDir, "*" + suffix + ClaimedSuffix))
        {
            if (IsExpired(claimed, now))
            {
                TryDelete(claimed);
                swept++;
            }
        }

        return swept;
    }

    // A request's age comes from the leading UTC stamp in its file name (which survives the claim rename);
    // a foreign/unstampable name falls back to the file's last-write time, and an unreadable age reads as
    // fresh (the claim + JSON guards still bound what it can cost).
    private static bool IsExpired(string path, DateTimeOffset now)
    {
        string name = Path.GetFileName(path);
        int dash = name.IndexOf('-', StringComparison.Ordinal);
        string stamp = dash > 0 ? name[..dash] : string.Empty;
        if (stamp.Length == StampFormat.Length
            && DateTimeOffset.TryParseExact(
                stamp, StampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset written))
        {
            return now - written > RequestTtl;
        }

        try
        {
            return now.UtcDateTime - File.GetLastWriteTimeUtc(path) > RequestTtl;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static string RequestDirectoryFor(string millerDir) =>
        Path.Combine(Path.GetFullPath(millerDir), RequestDirectoryName);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record FullScanRequest(
        int SchemaVersion,
        string Operation,
        string RequestId,
        string WorkspaceId,
        long BaselineRevision,
        DateTimeOffset RequestedAtUtc,
        int RequestingProcessId);

    private sealed record FileConvergeRequest(
        int SchemaVersion,
        string Operation,
        string RequestId,
        string WorkspaceId,
        string[] Paths,
        DateTimeOffset RequestedAtUtc,
        int RequestingProcessId);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(FullScanRequest))]
    [JsonSerializable(typeof(FileConvergeRequest))]
    private sealed partial class LeaderScanRequestJsonContext : JsonSerializerContext;
}
