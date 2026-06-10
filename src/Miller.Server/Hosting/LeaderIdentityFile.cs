using System.Text.Json;
using System.Text.Json.Serialization;

namespace Miller.Server.Hosting;

/// <summary>
/// Who the indexer leader for a workspace is, as recorded in <c>&lt;workspace&gt;/.miller/leader.json</c>. The
/// leader writes it when it wins the writer lock and removes it on graceful step-down; a crash leaves a stale
/// file behind, so consumers MUST pair <see cref="Pid"/> with a liveness probe before trusting it. This exists
/// for diagnosability: real deployments run several Miller processes per workspace (Claude Code, Cursor, CLI),
/// and freshness convergence is owned by whichever one leads — possibly an older build from a plugin cache.
/// </summary>
public sealed record LeaderIdentity(
    int Pid,
    string Version,
    string? ProcessPath,
    DateTimeOffset StartedAtUtc);

/// <summary>
/// Atomic read/write of the leader identity sidecar file. Same temp-write-then-move discipline as
/// <c>LeaderScanRequestQueue</c>; reads return null for missing/malformed files (never throw for an expected
/// condition).
/// </summary>
public static partial class LeaderIdentityFile
{
    private const string FileName = "leader.json";

    public static string PathFor(string millerDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(millerDir);
        return Path.Combine(Path.GetFullPath(millerDir), FileName);
    }

    /// <summary>Record <paramref name="identity"/> as the leader for the workspace owning <paramref name="millerDir"/>.</summary>
    public static void Write(string millerDir, LeaderIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        string finalPath = PathFor(millerDir);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        string tempPath = finalPath + ".tmp";
        try
        {
            File.WriteAllText(
                tempPath,
                JsonSerializer.Serialize(identity, LeaderIdentityJsonContext.Default.LeaderIdentity));
            File.Move(tempPath, finalPath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    /// <summary>The recorded leader identity, or null when none is recorded (or the file is unreadable/malformed).</summary>
    public static LeaderIdentity? TryRead(string millerDir)
    {
        string path = PathFor(millerDir);
        try
        {
            if (!File.Exists(path))
                return null;
            return JsonSerializer.Deserialize(
                File.ReadAllText(path), LeaderIdentityJsonContext.Default.LeaderIdentity);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Best-effort removal (graceful leader step-down). Never throws.</summary>
    public static void TryDelete(string millerDir) => TryDeleteFile(PathFor(millerDir));

    /// <summary>Whether a process with <paramref name="pid"/> is currently running on this machine.</summary>
    public static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false; // no such pid (or it exited between lookup and probe)
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(LeaderIdentity))]
    internal sealed partial class LeaderIdentityJsonContext : JsonSerializerContext;
}
