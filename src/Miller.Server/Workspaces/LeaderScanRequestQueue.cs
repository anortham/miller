using System.Text.Json;
using System.Text.Json.Serialization;

namespace Miller.Server.Workspaces;

internal static partial class LeaderScanRequestQueue
{
    private const int SchemaVersion = 1;
    private const string OperationFullScan = "full_scan";
    private const string RequestDirectoryName = "requests";
    private const string FullScanSuffix = ".full-scan.json";

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

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(FullScanRequest))]
    private sealed partial class LeaderScanRequestJsonContext : JsonSerializerContext;
}
