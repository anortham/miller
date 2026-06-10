using System.Text.Json;
using System.Text.Json.Serialization;

namespace Miller.Server.Workspaces;

internal static partial class LeaderScanRequestQueue
{
    private const int SchemaVersion = 1;
    private const string OperationFullScan = "full_scan";
    private const string OperationFileConverge = "file_converge";
    private const string RequestDirectoryName = "requests";
    private const string FullScanSuffix = ".full-scan.json";
    private const string FileConvergeSuffix = ".file-converge.json";

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
        string stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfffffff", System.Globalization.CultureInfo.InvariantCulture);
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

    public static bool DrainFullScanRequests(string millerDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(millerDir);

        string requestDir = RequestDirectoryFor(millerDir);
        if (!Directory.Exists(requestDir))
            return false;

        bool requested = false;
        foreach (string path in Directory.EnumerateFiles(requestDir, "*" + FullScanSuffix).Order(StringComparer.Ordinal))
        {
            try
            {
                string json = File.ReadAllText(path);
                FullScanRequest? request = JsonSerializer.Deserialize(
                    json,
                    LeaderScanRequestJsonContext.Default.FullScanRequest);
                if (request is { SchemaVersion: SchemaVersion, Operation: OperationFullScan })
                    requested = true;
                TryDelete(path);
            }
            catch (JsonException)
            {
                TryDelete(path);
            }
            catch (IOException)
            {
                // Another process may still be moving or deleting the file. Leave it for the next leader tick.
            }
            catch (UnauthorizedAccessException)
            {
                // Same treatment as IOException: keep the request around for a later retry.
            }
        }

        return requested;
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
        string stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfffffff", System.Globalization.CultureInfo.InvariantCulture);
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
    /// Drain pending single-file converge requests, returning the deduplicated paths in request order. Same
    /// failure discipline as <see cref="DrainFullScanRequests"/>: malformed requests are deleted; a request that
    /// cannot be read/deleted right now is left for a later tick.
    /// </summary>
    public static IReadOnlyList<string> DrainFileConvergeRequests(string millerDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(millerDir);

        string requestDir = RequestDirectoryFor(millerDir);
        if (!Directory.Exists(requestDir))
            return [];

        List<string>? paths = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(requestDir, "*" + FileConvergeSuffix).Order(StringComparer.Ordinal))
        {
            try
            {
                string json = File.ReadAllText(path);
                FileConvergeRequest? request = JsonSerializer.Deserialize(
                    json,
                    LeaderScanRequestJsonContext.Default.FileConvergeRequest);
                if (request is { SchemaVersion: SchemaVersion, Operation: OperationFileConverge, Paths: { } requested })
                {
                    foreach (string requestedPath in requested)
                    {
                        if (!string.IsNullOrWhiteSpace(requestedPath) && seen.Add(requestedPath))
                            (paths ??= []).Add(requestedPath);
                    }
                }
                TryDelete(path);
            }
            catch (JsonException)
            {
                TryDelete(path);
            }
            catch (IOException)
            {
                // Another process may still be moving or deleting the file. Leave it for the next leader tick.
            }
            catch (UnauthorizedAccessException)
            {
                // Same treatment as IOException: keep the request around for a later retry.
            }
        }

        return (IReadOnlyList<string>?)paths ?? [];
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
