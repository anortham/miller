using System.Globalization;
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
            var record = new CtDaemonLeaseRecord(holder, now, root, millerVersion);
            WriteLease(root, record);
            WriteStatus(root, new CtDaemonStatusRecord(CtDaemonLifecycleState.Running, "acquired", holder, now));
            return new CtDaemonLease(stream, lockPath, record);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public void WriteStatus(CtDaemonLifecycleState state, string reason, TimeProvider? time = null) =>
        WriteStatus(state, reason, CtDaemonActivity.Idle, run: null, loopTickAtUtc: null, time);

    /// <summary>
    /// Publishes the status with the daemon's current activity and, while a provider run is in flight, the
    /// run it is executing. <paramref name="loopTickAtUtc"/> is the main loop's last tick, written verbatim
    /// so that a reader can subtract it from <see cref="CtDaemonStatusRecord.UpdatedAtUtc"/> and get the
    /// loop's lag from two stamps of the same clock. The writer never invents it: the pulse republishes the
    /// value the loop stamped, and a null stays null.
    /// </summary>
    public void WriteStatus(
        CtDaemonLifecycleState state,
        string reason,
        CtDaemonActivity activity,
        CtDaemonRunProgress? run,
        DateTimeOffset? loopTickAtUtc = null,
        TimeProvider? time = null)
    {
        DateTimeOffset now = (time ?? TimeProvider.System).GetUtcNow();
        WriteStatus(
            Record.WorkspaceRoot,
            new CtDaemonStatusRecord(state, reason, Record.Identity, now, activity, run, loopTickAtUtc));
    }

    public void Dispose()
    {
        FileStream? stream = _lockStream;
        if (stream is null)
            return;
        _lockStream = null;
        try
        {
            // A record about a root this process is LEAVING must never re-mint the control plane.
            // In the normal case the directory holds the lock file this very method is about to
            // release, so replace-only behaves identically; it differs only when the tree was
            // deleted under a live daemon, which is exactly the resurrect to refuse.
            WriteStatus(
                Record.WorkspaceRoot,
                new CtDaemonStatusRecord(
                    CtDaemonLifecycleState.Stopped,
                    "released",
                    Record.Identity,
                    DateTimeOffset.UtcNow),
                CtDaemonWriteMode.ReplaceExistingOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        stream.Dispose();
    }

    internal static void WriteStatus(
        string workspaceRoot,
        CtDaemonStatusRecord status,
        CtDaemonWriteMode mode = CtDaemonWriteMode.CreateIfMissing) =>
        CtDaemonJson.WriteAtomic(
            CtDaemonProtocol.StatusPath(workspaceRoot),
            status,
            CtDaemonJsonContext.Default.CtDaemonStatusRecord,
            mode);

    private static void WriteLease(string workspaceRoot, CtDaemonLeaseRecord record) =>
        CtDaemonJson.WriteAtomic(
            CtDaemonProtocol.LeasePath(workspaceRoot),
            record,
            CtDaemonJsonContext.Default.CtDaemonLeaseRecord);

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

/// <summary>
/// Whether a control-plane write may CREATE the file and the directory that holds it.
///
/// <para><see cref="ReplaceExistingOnly"/> exists because a status record about a workspace this
/// process is LEAVING used to re-mint the very tree it was tearing down: every write went through
/// one unconditional <c>Directory.CreateDirectory</c>, so a detach record recreated
/// <c>&lt;worktree&gt;/.miller/ct/</c> under a root that had just been removed. Observed live on
/// 2026-08-21, where it defeated <c>git worktree remove</c> twice — the recreated directory left the
/// worktree untracked-dirty and git refused.</para>
///
/// <para>The rule that separates the two: a write that says "a live daemon serves this root" may
/// create (an attach record, a lease, a command addressed to a proven-live daemon); a
/// write that says "nothing serves this root any more" may only REPLACE. An absent destination is
/// then success, not an error — a control plane that is already gone needs no record saying so, and
/// its absence reads as stopped.</para>
/// </summary>
public enum CtDaemonWriteMode
{
    /// <summary>Create the file and its directory when they are absent.</summary>
    CreateIfMissing,

    /// <summary>Replace an existing file only. Never create the file, never create the directory.</summary>
    ReplaceExistingOnly,
}

public static class CtDaemonJson
{
    public static string Serialize<T>(T value, JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.Serialize(value, typeInfo);

    public static string Serialize(CtDaemonLeaseRecord value) =>
        Serialize(value, CtDaemonJsonContext.Default.CtDaemonLeaseRecord);

    public static string Serialize(CtDaemonCommandRequest value) =>
        Serialize(value, CtDaemonJsonContext.Default.CtDaemonCommandRequest);

    public static string Serialize(CtDaemonCommandAck value) =>
        Serialize(value, CtDaemonJsonContext.Default.CtDaemonCommandAck);

    public static string Serialize(CtDaemonStatusRecord value) =>
        Serialize(value, CtDaemonJsonContext.Default.CtDaemonStatusRecord);

    /// <summary>Bounded retries for replacing a control-plane file, matching the scan-failure journal.</summary>
    private const int ReplaceAttempts = 5;

    private static readonly TimeSpan ReplaceRetryDelay = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// Confirmations before a control-plane file is called absent, and the wait between them. Short on
    /// purpose: this is the cost an absent file pays on a hot path, and the window being stepped over
    /// is under a millisecond.
    /// </summary>
    private const int MissingConfirmations = 3;

    private static readonly TimeSpan MissingRetryDelay = TimeSpan.FromMilliseconds(5);

    /// <summary>
    /// How long to wait after failed publish attempt <paramref name="attempt"/>. The delay GROWS with
    /// the attempt and carries jitter, and both halves earn their place. A fixed delay makes writers
    /// that collided retry in lockstep, so they collide again on every attempt and burn the whole
    /// budget without anyone making progress - measured as "Unable to remove the file to be replaced"
    /// on 3 of 25 loaded runs with concurrent writers on one path. Jitter spreads them apart; growth
    /// gives a destination held by a scanner time to settle. This matches the jittered backoff the
    /// indexer's scan-failure journal already uses.
    /// </summary>
    private static TimeSpan RetryDelayFor(int attempt) =>
        (ReplaceRetryDelay * attempt) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 20));

    /// <summary>
    /// Whether the file is really absent, as opposed to momentarily unlinked by a publish in flight.
    /// <c>ReplaceFile</c> removes the destination name before the replacement lands, so ONE stat that
    /// says "absent" cannot tell "no daemon has ever run here" from "a status is being published right
    /// now" - measured at 14 to 32 of 300 reads against a writer in a tight loop. A handful of short
    /// re-probes separates them: a genuinely absent file answers in about ten milliseconds, and a
    /// publish window is stepped over instead of being reported to the caller as "no record".
    /// </summary>
    private static bool ExistsThroughPublishWindow(string path)
    {
        // No directory means no publish window to step over: a publish creates that directory and
        // never removes it, so the file cannot be mid-replace. Without this, every status call on a
        // workspace whose control plane no daemon ever created paid the full ~10ms of retries to
        // learn what one stat already knew — on the call the contract calls the cheap one.
        if (Path.GetDirectoryName(path) is { Length: > 0 } dir && !Directory.Exists(dir))
            return false;

        for (var attempt = 1; ; attempt++)
        {
            if (File.Exists(path))
                return true;
            if (attempt >= MissingConfirmations)
                return false;

            Thread.Sleep(MissingRetryDelay);
        }
    }

    /// <summary>
    /// Reads a control-plane record without blocking the writer.
    ///
    /// <c>File.ReadAllText</c> opens with <c>FileShare.Read</c>, which withholds FILE_SHARE_DELETE. On
    /// Windows that makes a READER block the writer's replace: <c>MoveFileEx</c> with
    /// MOVEFILE_REPLACE_EXISTING needs DELETE access on the destination and fails with
    /// ERROR_SHARING_VIOLATION. The daemon rewrites its status every 250 ms while a waiting
    /// <c>tests run --wait</c> polls it every 50 ms, so the collision is near-certain within seconds.
    /// Sharing delete as well as read makes a reader harmless. POSIX renames over open files, so this
    /// only ever bit Windows.
    ///
    /// <para>The OPEN is retried on the same bounded schedule the publish uses, because sharing cuts
    /// both ways. <c>ReplaceFile</c> holds the destination itself for the instant it swaps, so a reader
    /// that opens inside that instant is refused - measured here at 23 of 300 reads against a writer in
    /// a tight loop, with zero torn reads. Without the retry those refusals reach the caller as a null
    /// record, which reads as "no daemon" rather than "ask again", and the publish side retried while
    /// the read side did not. A JsonException is NOT retried: the publish is atomic, so unparseable
    /// bytes are a genuinely corrupt file and reading it five times cannot help.</para>
    /// </summary>
    public static T? TryRead<T>(string path, JsonTypeInfo<T> typeInfo)
    {
        if (!ExistsThroughPublishWindow(path))
            return default;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return JsonSerializer.Deserialize(reader.ReadToEnd(), typeInfo);
            }
            catch (JsonException)
            {
                return default;
            }
            catch (Exception ex) when (
                (ex is IOException or UnauthorizedAccessException) && attempt < ReplaceAttempts)
            {
                Thread.Sleep(ReplaceRetryDelay);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return default;
            }
        }
    }

    public static void WriteAtomic<T>(
        string path,
        T value,
        JsonTypeInfo<T> typeInfo,
        CtDaemonWriteMode mode = CtDaemonWriteMode.CreateIfMissing)
    {
        // Probed BEFORE the temp file is staged. Staging alone would recreate the directory this
        // mode exists to leave alone, because the temp name is a sibling of the destination.
        if (mode == CtDaemonWriteMode.ReplaceExistingOnly && !File.Exists(path))
            return;

        string? dir = Path.GetDirectoryName(path);
        if (mode == CtDaemonWriteMode.CreateIfMissing && !string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // The temp name carries the writing process AND thread. A fixed "<path>.tmp" is shared state:
        // two concurrent writers would overwrite each other's staged bytes, and the loser's
        // finally-block delete would remove the winner's file before it was published. Process id plus
        // thread id keeps the name deterministic and bounded - a crashed writer leaves at most one
        // stale temp per thread, which the next writer on that thread overwrites - where a Guid would
        // orphan a new file on every crash and nothing would ever reap them.
        string tempPath = string.Create(
            CultureInfo.InvariantCulture,
            $"{path}.{Environment.ProcessId}.{Environment.CurrentManagedThreadId}.tmp");
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(value, typeInfo));
            MoveWithRetry(tempPath, path, mode);
        }
        // The destination tree went away between the probe and the write. Leaving is the whole point
        // of this mode, so an absent destination is success. Both shapes are IOException subclasses,
        // so a create-mode write keeps reporting them as it always did.
        catch (Exception ex) when (
            mode == CtDaemonWriteMode.ReplaceExistingOnly
            && ex is DirectoryNotFoundException or FileNotFoundException)
        {
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

    /// <summary>
    /// Publishes the staged temp file over the destination.
    ///
    /// <c>File.Replace</c>, NOT <c>File.Move(overwrite: true)</c>. Measured on Windows 11 against a
    /// reader holding the destination open:
    /// <code>
    /// writer                     reader share=Read      reader share=ReadWrite|Delete
    /// File.Move(overwrite:true)  UnauthorizedAccess     UnauthorizedAccess
    /// File.Replace               IOException            OK
    /// </code>
    /// So both halves are load-bearing and neither works alone: <see cref="TryRead"/> must share
    /// delete, and the publish must be <c>ReplaceFile</c>, which is the Win32 call designed to swap a
    /// file that somebody is reading. Retrying <c>File.Move</c> would not have helped - a poller holds
    /// the file open for as long as it polls, so every attempt fails the same way.
    ///
    /// <c>File.Replace</c> requires the destination to exist, so a first write is a plain move. The
    /// two are attempted in a retry loop rather than gated on a stale <c>File.Exists</c> check,
    /// because another process can create or delete the destination in between.
    /// </summary>
    private static void MoveWithRetry(
        string tempPath,
        string finalPath,
        CtDaemonWriteMode mode = CtDaemonWriteMode.CreateIfMissing)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (File.Exists(finalPath))
                {
                    // ignoreMetadataErrors: the destination's ACLs and attributes are irrelevant here;
                    // a metadata copy failure must not fail the publish of a status record.
                    File.Replace(tempPath, finalPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                else if (mode == CtDaemonWriteMode.ReplaceExistingOnly)
                {
                    // The destination vanished between the caller's probe and this call. The Move
                    // branch below would CREATE it, and with it the directory this mode must leave
                    // alone, so the second half of the guard belongs here and not only at the top.
                    return;
                }
                else
                {
                    File.Move(tempPath, finalPath);
                }

                return;
            }
            catch (Exception ex) when (
                (ex is IOException or UnauthorizedAccessException) && attempt < ReplaceAttempts)
            {
                // Lost the race (the destination appeared or vanished between the probe and the call),
                // or a Defender scan is holding the freshly written temp file.
                Thread.Sleep(RetryDelayFor(attempt));
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
[JsonSerializable(typeof(CtDaemonCommandRequest))]
[JsonSerializable(typeof(CtDaemonCommandAck))]
[JsonSerializable(typeof(CtDaemonStatusRecord))]
// Nested inside CtDaemonStatusRecord. The published binary is Native AOT, where a type the source
// generator was never told about fails when a run is in flight, not at build time.
[JsonSerializable(typeof(CtDaemonRunProgress))]
[JsonSerializable(typeof(CtDaemonLeaseIdentity))]
[JsonSerializable(typeof(CtFreshnessKey))]
internal sealed partial class CtDaemonJsonContext : JsonSerializerContext;
