using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Miller.Core.Freshness;

namespace Miller.Indexing;

/// <summary>
/// The per-workspace scan-failure record on disk: <c>&lt;workspace&gt;/.miller/scan-failure.json</c>. Every Miller
/// process on the workspace reads it, so the retry spacing holds ACROSS processes and restarts — which is the
/// whole point. Without it a force rebuild that cannot succeed is re-forced by every fresh process forever.
///
/// <para><b>Never authoritative enough to fail a scan.</b> A missing, truncated, half-written, or unparseable
/// record reads as "no recorded failure" and a failed write is swallowed: several Miller processes share this
/// file, and the worst outcome of losing it is one un-throttled retry, while throwing would break indexing
/// outright.</para>
///
/// <para>Writes are temp-then-rename with a pid-unique temp name, so a concurrent writer cannot tear a reader's
/// view and two processes cannot collide on the same temp path.</para>
///
/// <para><b>Windows sharing.</b> Readers open with <c>FileShare.ReadWrite | FileShare.Delete</c> and the rename
/// retries briefly. On Windows a handle opened with the default share mode denies DELETE, so a concurrent
/// <c>workspace status</c>/<c>health</c>/dashboard read makes the writer's replace fail with a sharing violation —
/// and a swallowed one means the failure is never persisted and the next attempt runs with no backoff at all, on
/// the platform where the atomic-replace assumption is weakest.</para>
/// </summary>
public static partial class ScanFailureJournal
{
    /// <summary>The record's file name inside the workspace's <c>.miller</c> directory.</summary>
    public const string FileName = "scan-failure.json";

    // A reader on another process can hold the target for a few ms; the write is best-effort, so a short bounded
    // retry is the whole budget — blocking a scan on a diagnostics read would be worse than losing the record.
    private const int ReplaceAttempts = 5;
    private static readonly TimeSpan ReplaceRetryDelay = TimeSpan.FromMilliseconds(20);

    /// <summary>The record path for the workspace owning <paramref name="millerDir"/>.</summary>
    public static string PathFor(string millerDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(millerDir);
        return Path.Combine(Path.GetFullPath(millerDir), FileName);
    }

    /// <summary>The recorded failure, or null when none is recorded or the record is unreadable/malformed.</summary>
    public static ScanFailureRecord? TryRead(string millerDir)
    {
        string path;
        try
        {
            path = PathFor(millerDir);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }

        try
        {
            if (!File.Exists(path))
                return null;
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            ScanFailureDocument? document = JsonSerializer.Deserialize(
                stream, ScanFailureJsonContext.Default.ScanFailureDocument);
            return document?.ToRecord();
        }
        catch (Exception ex) when (
            ex is JsonException or IOException or UnauthorizedAccessException or FormatException
                or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Record <paramref name="record"/> atomically. Best-effort: never throws.</summary>
    public static void TryWrite(string millerDir, ScanFailureRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        string finalPath;
        try
        {
            finalPath = PathFor(millerDir);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return;
        }

        string tempPath = finalPath + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
            File.WriteAllText(
                tempPath,
                JsonSerializer.Serialize(
                    ScanFailureDocument.From(record), ScanFailureJsonContext.Default.ScanFailureDocument));
            MoveWithRetry(tempPath, finalPath);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            TryDeleteFile(tempPath);
        }
    }

    private static void MoveWithRetry(string tempPath, string finalPath)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(tempPath, finalPath, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < ReplaceAttempts)
            {
                Thread.Sleep(ReplaceRetryDelay);
            }
        }
    }

    /// <summary>Clear the recorded failure (a scan succeeded). Best-effort: never throws.</summary>
    public static void TryClear(string millerDir)
    {
        try
        {
            TryDeleteFile(PathFor(millerDir));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
                return;
            }
            catch (IOException) when (attempt < ReplaceAttempts)
            {
                Thread.Sleep(ReplaceRetryDelay);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }
        }
    }

    // The on-disk shape. Separate from ScanFailureRecord so Miller.Core carries no serialization attributes and
    // so the intent is stored as a stable NAME rather than an enum ordinal a reordered enum would silently
    // reinterpret. An unknown name reads as null (the record is discarded), never as a wrong intent.
    internal sealed record ScanFailureDocument(
        string Intent,
        int? ExitCode,
        int ConsecutiveFailures,
        int Jobs,
        string LastFailureAtUtc,
        string NextAttemptAtUtc)
    {
        internal static ScanFailureDocument From(ScanFailureRecord record) => new(
            record.Intent.ToString(),
            record.ExitCode,
            record.ConsecutiveFailures,
            record.Jobs,
            FormatTimestamp(record.LastFailureAtUtc),
            FormatTimestamp(record.NextAttemptAtUtc));

        internal ScanFailureRecord? ToRecord()
        {
            // Enum.TryParse accepts a bare number, so a corrupt "intent": "9" would otherwise become an undefined
            // ScanIntent that every switch below silently treats as a heal.
            if (!Enum.TryParse(Intent, ignoreCase: false, out ScanIntent intent) || !Enum.IsDefined(intent))
                return null;
            if (ConsecutiveFailures <= 0 || Jobs < 0)
                return null;
            if (!TryParseTimestamp(LastFailureAtUtc, out DateTimeOffset lastFailure) ||
                !TryParseTimestamp(NextAttemptAtUtc, out DateTimeOffset nextAttempt))
                return null;

            return new ScanFailureRecord(intent, ExitCode, ConsecutiveFailures, Jobs, lastFailure, nextAttempt);
        }

        private static string FormatTimestamp(DateTimeOffset value) =>
            value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

        private static bool TryParseTimestamp(string? raw, out DateTimeOffset value) =>
            DateTimeOffset.TryParse(
                raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value);
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
    [JsonSerializable(typeof(ScanFailureDocument))]
    internal sealed partial class ScanFailureJsonContext : JsonSerializerContext;
}
