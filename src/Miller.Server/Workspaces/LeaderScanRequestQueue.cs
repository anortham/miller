using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Miller.Indexing;

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

/// <summary>What one yield drain observed: whether a valid yield request was serviced, the highest requester
/// extractor version seen this pass (numeric major.minor.patch order) with that requester's pid, plus the
/// TTL/claim bookkeeping the caller (the leader's debounce tick) logs.</summary>
internal sealed record YieldDrainResult(
    bool Requested, string? MaxRequesterVersion, int RequesterPid, int ExpiredDiscarded, int ClaimSkipped)
{
    public static YieldDrainResult Empty { get; } = new(
        Requested: false, MaxRequesterVersion: null, RequesterPid: 0, ExpiredDiscarded: 0, ClaimSkipped: 0);
}

internal static partial class LeaderScanRequestQueue
{
    private const int SchemaVersion = 1;
    private const string OperationFullScan = "full_scan";
    private const string OperationFileConverge = "file_converge";
    private const string OperationYield = "yield";
    private const string RequestDirectoryName = "requests";
    private const string FullScanSuffix = ".full-scan.json";
    private const string FileConvergeSuffix = ".file-converge.json";
    private const string YieldSuffix = ".yield.json";
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

    /// <summary>
    /// Ask the live indexer leader (another process) to abdicate so a newer-extractor reader can take over
    /// (version-aware leadership D4). Written by a reader whose pinned extractor is newer than the leader's;
    /// drained by the leader's debounce tick, which decides whether to yield (Task 3 orchestration).
    /// </summary>
    public static void RequestYield(string millerDir, string workspaceId, int requesterPid, string requesterExtractorVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(millerDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requesterExtractorVersion);
        if (requesterPid <= 0)
            throw new ArgumentOutOfRangeException(nameof(requesterPid), requesterPid, "Requester pid must be positive.");

        string requestDir = RequestDirectoryFor(millerDir);
        Directory.CreateDirectory(requestDir);

        string requestId = Guid.NewGuid().ToString("N");
        string stamp = DateTimeOffset.UtcNow.ToString(StampFormat, CultureInfo.InvariantCulture);
        string finalPath = Path.Combine(requestDir, $"{stamp}-{Environment.ProcessId}-{requestId}{YieldSuffix}");
        string tempPath = finalPath + ".tmp";
        var request = new YieldRequest(
            SchemaVersion,
            OperationYield,
            requestId,
            workspaceId,
            requesterPid,
            requesterExtractorVersion,
            DateTimeOffset.UtcNow);

        try
        {
            File.WriteAllText(
                tempPath,
                JsonSerializer.Serialize(request, LeaderScanRequestJsonContext.Default.YieldRequest));
            File.Move(tempPath, finalPath);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    /// <summary>
    /// Drain pending yield requests. When several arrive in one pass, the result surfaces the HIGHEST requester
    /// extractor version (numeric major.minor.patch order) and that requester's pid — the leader only needs to
    /// know the strongest challenger. Requests older than <see cref="RequestTtl"/> are discarded without
    /// servicing; a request that cannot be claimed (renamed) right now is skipped this tick.
    /// </summary>
    public static YieldDrainResult DrainYieldRequests(string millerDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(millerDir);

        string requestDir = RequestDirectoryFor(millerDir);
        if (!Directory.Exists(requestDir))
            return YieldDrainResult.Empty;

        DateTimeOffset now = DateTimeOffset.UtcNow;
        int expired = SweepExpiredClaims(requestDir, YieldSuffix, now);
        int skipped = 0;
        string? maxVersion = null;
        int maxVersionPid = 0;
        foreach (string path in Directory.EnumerateFiles(requestDir, "*" + YieldSuffix).Order(StringComparer.Ordinal))
        {
            if (IsExpired(path, now))
            {
                TryDelete(path); // the requesting reader's poll gave up long ago; a stale yield must not dethrone a leader
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
                YieldRequest? request = JsonSerializer.Deserialize(
                    json,
                    LeaderScanRequestJsonContext.Default.YieldRequest);
                if (request is { SchemaVersion: SchemaVersion, Operation: OperationYield }
                    && IsStrongerChallenger(request.RequesterExtractorVersion, maxVersion))
                {
                    maxVersion = request.RequesterExtractorVersion;
                    maxVersionPid = request.RequesterPid;
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

        return new YieldDrainResult(maxVersion is not null, maxVersion, maxVersionPid, expired, skipped);
    }

    // Does this drained yield's version beat the strongest challenger seen so far this pass? Ordering is
    // LeadershipEligibility.CompareVersions (numeric major.minor.patch, "2.10.1" > "2.3.0"), which THROWS on a
    // version carrying no X.Y.Z token — a yield with an uncomparable version is dropped like malformed JSON
    // rather than surfaced for the leader to compare garbage against its own version.
    private static bool IsStrongerChallenger(string? candidateVersion, string? currentMax)
    {
        if (string.IsNullOrWhiteSpace(candidateVersion))
            return false;
        try
        {
            return currentMax is null
                ? LeadershipEligibility.CompareVersions(candidateVersion, candidateVersion) == 0 // pure parse check
                : LeadershipEligibility.CompareVersions(candidateVersion, currentMax) > 0;
        }
        catch (ArgumentException)
        {
            return false;
        }
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

    private sealed record YieldRequest(
        int SchemaVersion,
        string Operation,
        string RequestId,
        string WorkspaceId,
        int RequesterPid,
        string RequesterExtractorVersion,
        DateTimeOffset CreatedAtUtc);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(FullScanRequest))]
    [JsonSerializable(typeof(FileConvergeRequest))]
    [JsonSerializable(typeof(YieldRequest))]
    private sealed partial class LeaderScanRequestJsonContext : JsonSerializerContext;
}
