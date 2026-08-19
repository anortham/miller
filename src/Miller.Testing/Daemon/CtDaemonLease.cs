using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Miller.Testing;

/// <summary>
/// Per-workspace CT daemon singleton. The open <c>FileShare.None</c> handle on
/// <c>daemon-v1.lock</c> is the lease; <c>daemon.lease.json</c> names the holder as PID plus
/// process start time so a reused PID cannot inherit a dead daemon.
/// </summary>
public sealed class CtDaemonLease : IDisposable
{
    private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(2);

    private FileStream? _lockStream;

    public CtDaemonLeaseRecord Record { get; }
    public string LockFilePath { get; }

    private CtDaemonLease(FileStream lockStream, string lockFilePath, CtDaemonLeaseRecord record)
    {
        _lockStream = lockStream;
        LockFilePath = lockFilePath;
        Record = record;
    }

    public static CtDaemonLeaseIdentity CurrentIdentity()
    {
        using var process = Process.GetCurrentProcess();
        return IdentityOf(process);
    }

    public static bool IsIdentityLive(CtDaemonLeaseIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        try
        {
            using var process = Process.GetProcessById(identity.Pid);
            if (process.HasExited)
                return false;
            DateTimeOffset started = IdentityOf(process).ProcessStartTimeUtc;
            return AlmostEqual(started, identity.ProcessStartTimeUtc);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
        catch (Exception)
        {
            // Probe denied (Windows access-denied after pid reuse). Collapse to live so a
            // mere probe failure cannot steal a daemon that may still hold the OS lock.
            return true;
        }
    }

    public static CtDaemonLeaseRecord? TryRead(string workspaceRoot)
    {
        string path = CtDaemonProtocol.LeasePath(workspaceRoot);
        return CtDaemonJson.TryRead(path, CtDaemonJsonContext.Default.CtDaemonLeaseRecord);
    }

    public static CtDaemonHeartbeatRecord? TryReadHeartbeat(string workspaceRoot)
    {
        string path = CtDaemonProtocol.HeartbeatPath(workspaceRoot);
        return CtDaemonJson.TryRead(path, CtDaemonJsonContext.Default.CtDaemonHeartbeatRecord);
    }

    public static CtDaemonStatusRecord? TryReadStatus(string workspaceRoot)
    {
        string path = CtDaemonProtocol.StatusPath(workspaceRoot);
        return CtDaemonJson.TryRead(path, CtDaemonJsonContext.Default.CtDaemonStatusRecord);
    }

    public static CtDaemonLeaseRecord? TryReadLive(
        string workspaceRoot,
        Func<CtDaemonLeaseIdentity, bool>? isLive = null)
    {
        CtDaemonLeaseRecord? record = TryRead(workspaceRoot);
        if (record is null)
            return null;
        Func<CtDaemonLeaseIdentity, bool> probe = isLive ?? IsIdentityLive;
        return probe(record.Identity) ? record : null;
    }

    public static CtDaemonLease? TryAcquire(
        string workspaceRoot,
        string millerVersion,
        CtDaemonLeaseIdentity? identity = null,
        TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(millerVersion);
        if (!Directory.Exists(workspaceRoot))
            throw new DirectoryNotFoundException($"CT workspace root does not exist: {workspaceRoot}");

        string root = Path.GetFullPath(workspaceRoot);
        Directory.CreateDirectory(CtDaemonProtocol.RootDirectory(root));
        string lockPath = CtDaemonProtocol.LockPath(root);

        FileStream stream;
        try
        {
            stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) when (IsLockContention(ex))
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        try
        {
            TimeProvider clock = time ?? TimeProvider.System;
            DateTimeOffset now = clock.GetUtcNow();
            CtDaemonLeaseIdentity holder = identity ?? CurrentIdentity();
            var record = new CtDaemonLeaseRecord(holder, now, now, root, millerVersion);
            WriteLease(root, record);
            WriteHeartbeat(root, new CtDaemonHeartbeatRecord(holder, now));
            WriteStatus(root, new CtDaemonStatusRecord(CtDaemonLifecycleState.Running, "acquired", holder, now));
            return new CtDaemonLease(stream, lockPath, record);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public void Heartbeat(TimeProvider? time = null)
    {
        ObjectDisposedException.ThrowIf(_lockStream is null, this);
        DateTimeOffset now = (time ?? TimeProvider.System).GetUtcNow();
        WriteHeartbeat(Record.WorkspaceRoot, new CtDaemonHeartbeatRecord(Record.Identity, now));
    }

    public void WriteStatus(CtDaemonLifecycleState state, string reason, TimeProvider? time = null)
    {
        DateTimeOffset now = (time ?? TimeProvider.System).GetUtcNow();
        WriteStatus(
            Record.WorkspaceRoot,
            new CtDaemonStatusRecord(state, reason, Record.Identity, now));
    }

    public void Dispose()
    {
        FileStream? stream = _lockStream;
        if (stream is null)
            return;
        _lockStream = null;
        try
        {
            WriteStatus(CtDaemonLifecycleState.Stopped, "released");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        stream.Dispose();
    }

    internal static void WriteStatus(string workspaceRoot, CtDaemonStatusRecord status) =>
        CtDaemonJson.WriteAtomic(
            CtDaemonProtocol.StatusPath(workspaceRoot),
            status,
            CtDaemonJsonContext.Default.CtDaemonStatusRecord);

    private static void WriteLease(string workspaceRoot, CtDaemonLeaseRecord record) =>
        CtDaemonJson.WriteAtomic(
            CtDaemonProtocol.LeasePath(workspaceRoot),
            record,
            CtDaemonJsonContext.Default.CtDaemonLeaseRecord);

    private static void WriteHeartbeat(string workspaceRoot, CtDaemonHeartbeatRecord record) =>
        CtDaemonJson.WriteAtomic(
            CtDaemonProtocol.HeartbeatPath(workspaceRoot),
            record,
            CtDaemonJsonContext.Default.CtDaemonHeartbeatRecord);

    private static CtDaemonLeaseIdentity IdentityOf(Process process) =>
        new(process.Id, new DateTimeOffset(process.StartTime.ToUniversalTime()));

    private static bool AlmostEqual(DateTimeOffset left, DateTimeOffset right) =>
        (left - right).Duration() <= StartTimeTolerance;

    private static bool IsLockContention(IOException ex)
    {
        int nativeError = ex.HResult & 0xFFFF;
        if (OperatingSystem.IsWindows())
            return nativeError is 32 or 33;
        return nativeError is 11 or 35;
    }
}

public static class CtDaemonJson
{
    public static string Serialize<T>(T value, JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.Serialize(value, typeInfo);

    public static string Serialize(CtDaemonLeaseRecord value) =>
        Serialize(value, CtDaemonJsonContext.Default.CtDaemonLeaseRecord);

    public static string Serialize(CtDaemonHeartbeatRecord value) =>
        Serialize(value, CtDaemonJsonContext.Default.CtDaemonHeartbeatRecord);

    public static string Serialize(CtDaemonCommandRequest value) =>
        Serialize(value, CtDaemonJsonContext.Default.CtDaemonCommandRequest);

    public static string Serialize(CtDaemonCommandAck value) =>
        Serialize(value, CtDaemonJsonContext.Default.CtDaemonCommandAck);

    public static string Serialize(CtDaemonStatusRecord value) =>
        Serialize(value, CtDaemonJsonContext.Default.CtDaemonStatusRecord);

    public static T? TryRead<T>(string path, JsonTypeInfo<T> typeInfo)
    {
        try
        {
            if (!File.Exists(path))
                return default;
            return JsonSerializer.Deserialize(File.ReadAllText(path), typeInfo);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }

    public static void WriteAtomic<T>(string path, T value, JsonTypeInfo<T> typeInfo)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        string tempPath = path + ".tmp";
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(value, typeInfo));
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}

internal sealed class CtFreshnessKeyJsonConverter : JsonConverter<CtFreshnessKey>
{
    public override CtFreshnessKey Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("freshness must be an object");

        string? indexIdentity = null;
        long? revision = null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("freshness object is malformed");

            string name = reader.GetString() ?? "";
            reader.Read();
            if (name.Equals("index_identity", StringComparison.OrdinalIgnoreCase))
                indexIdentity = reader.GetString();
            else if (name.Equals("revision", StringComparison.OrdinalIgnoreCase))
                revision = reader.GetInt64();
            else
                reader.Skip();
        }

        if (string.IsNullOrWhiteSpace(indexIdentity) || revision is null)
            throw new JsonException("freshness requires index_identity and revision");
        return new CtFreshnessKey(indexIdentity, revision.Value);
    }

    public override void Write(Utf8JsonWriter writer, CtFreshnessKey value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("index_identity", value.IndexIdentity);
        writer.WriteNumber("revision", value.Revision);
        writer.WriteEndObject();
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
    UseStringEnumConverter = true,
    Converters = [typeof(CtFreshnessKeyJsonConverter)])]
[JsonSerializable(typeof(CtDaemonLeaseRecord))]
[JsonSerializable(typeof(CtDaemonHeartbeatRecord))]
[JsonSerializable(typeof(CtDaemonCommandRequest))]
[JsonSerializable(typeof(CtDaemonCommandAck))]
[JsonSerializable(typeof(CtDaemonStatusRecord))]
[JsonSerializable(typeof(CtDaemonLeaseIdentity))]
[JsonSerializable(typeof(CtFreshnessKey))]
internal sealed partial class CtDaemonJsonContext : JsonSerializerContext;
