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
    DateTimeOffset StartedAtUtc,
    string? ExtractorVersion = null);

/// <summary>
/// What one liveness probe of a pid observed: whether a process wearing the pid runs right now, and when it
/// started (null when the start time is unreadable — the pid-reuse cross-check then degrades gracefully).
/// </summary>
internal readonly record struct LeaderProcessProbe(bool Running, DateTimeOffset? StartedAtUtc);

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

    /// <summary>
    /// A pid that started more than this long AFTER the identity was recorded cannot be the recording process —
    /// the pid was recycled. Generous enough to absorb clock skew and write latency.
    /// </summary>
    internal static readonly TimeSpan PidReuseStartTolerance = TimeSpan.FromSeconds(10);

    /// <summary>Whether a process with <paramref name="pid"/> is currently running on this machine.</summary>
    public static bool IsProcessAlive(int pid) => IsProcessAlive(pid, recordedAtUtc: null, probe: null);

    /// <summary>
    /// Whether the process with <paramref name="pid"/> is alive and, when <paramref name="recordedAtUtc"/> is
    /// known, old enough to be the same process that wrote the observed record.
    /// </summary>
    internal static bool IsProcessAlive(int pid, DateTimeOffset? recordedAtUtc) =>
        IsProcessAlive(pid, recordedAtUtc, probe: null);

    /// <summary>
    /// Whether the process that recorded <paramref name="identity"/> is still running: the pid must be alive AND
    /// must not have started significantly after the identity was recorded (pid reuse). Legacy identities without
    /// a recorded timestamp skip the reuse cross-check.
    /// </summary>
    public static bool IsProcessAlive(LeaderIdentity identity) => IsProcessAlive(identity, probe: null);

    /// <summary>Test seam: <paramref name="probe"/> replaces the real process probe (null = real).</summary>
    internal static bool IsProcessAlive(LeaderIdentity identity, Func<int, LeaderProcessProbe>? probe)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return IsProcessAlive(
            identity.Pid,
            identity.StartedAtUtc == default ? null : identity.StartedAtUtc,
            probe);
    }

    private static bool IsProcessAlive(int pid, DateTimeOffset? recordedAtUtc, Func<int, LeaderProcessProbe>? probe)
    {
        try
        {
            LeaderProcessProbe result = (probe ?? ProbeProcess)(pid);
            if (!result.Running)
                return false;
            if (recordedAtUtc is { } recorded && result.StartedAtUtc is { } started &&
                started - recorded > PidReuseStartTolerance)
                return false; // started well AFTER the identity was recorded: a different process wearing the pid
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false; // no such pid (or it exited between lookup and probe)
        }
        catch (Exception)
        {
            // The probe is advisory and can be DENIED rather than answered — e.g. Win32Exception (access denied)
            // probing an elevated process on Windows after pid reuse. A denied probe means a process with this
            // pid exists but cannot be interrogated: collapse to alive, because collapsing to dead would make
            // `workspace health` spuriously report indexer_leader_dead on a mere probe failure.
            return true;
        }
    }

    private static LeaderProcessProbe ProbeProcess(int pid)
    {
        using var process = System.Diagnostics.Process.GetProcessById(pid);
        if (process.HasExited)
            return new LeaderProcessProbe(Running: false, StartedAtUtc: null);
        return new LeaderProcessProbe(Running: true, StartedAtUtc: ReadStartTimeUtc(process));
    }

    // Start time is the one probe field that is best-effort even for a live, answerable pid (it can be denied
    // independently of HasExited); an unreadable start time skips the reuse cross-check, it never kills a leader.
    private static DateTimeOffset? ReadStartTimeUtc(System.Diagnostics.Process process)
    {
        try
        {
            return process.StartTime.ToUniversalTime();
        }
        catch (Exception)
        {
            return null;
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
