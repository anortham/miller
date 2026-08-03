using System.Text.Json;

namespace Miller.Indexing;

/// <summary>
/// One record from julie-extract's <c>--progress-file</c> (progress-file contract v1): the phase a scan had
/// entered and how far its counters had advanced. Miller reads only the LAST record, and only to explain a
/// kill — "stalled" is decided by the file's length, not by parsing it.
/// </summary>
/// <param name="Phase">
/// The phase key, shared with the finished report's <c>profile.phases</c>: <c>existing_artifact</c>,
/// <c>discovery</c>, <c>force_metadata</c>, <c>extraction_spool</c>, <c>writer_open</c>, <c>artifact_write</c>.
/// </param>
public sealed record ScanProgressRecord(
    string Phase,
    long ElapsedMs,
    long FilesDiscovered,
    long FilesSupported,
    long FilesExtracted,
    long FilesSpooled)
{
    /// <summary>
    /// Parse one JSONL line. Returns null for anything that is not a usable record — the contract requires
    /// consumers to skip a torn trailing line, a blank line, and a record left half-written by a failed write,
    /// and a diagnostic that throws on those would turn a bad kill message into a worse crash.
    /// </summary>
    public static ScanProgressRecord? TryParse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;
        try
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            return new ScanProgressRecord(
                document.RootElement.TryGetProperty("phase", out var phase)
                && phase.ValueKind == JsonValueKind.String
                    ? phase.GetString() ?? "unknown"
                    : "unknown",
                Number(document.RootElement, "elapsed_ms"),
                Number(document.RootElement, "files_discovered"),
                Number(document.RootElement, "files_supported"),
                Number(document.RootElement, "files_extracted"),
                Number(document.RootElement, "files_spooled"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static long Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out long parsed)
            ? parsed
            : 0;

    /// <summary>
    /// The last parseable record in <paramref name="text"/>, scanning backwards. Backwards because a failed
    /// write leaves damage at the point it failed and every LATER record still parses, so the newest usable
    /// record is the one nearest the end.
    ///
    /// <para>An unterminated trailing line is dropped even when it parses. The contract makes the terminating
    /// newline — not JSON validity — the proof that the producer finished a record, because a reader can
    /// otherwise consume a line the writer is still extending. Miller reads this file only after the child is
    /// dead, so today the dropped line's counters would have been genuine; conforming anyway is what keeps a
    /// future live-progress reader from inheriting a subtly wrong parser.</para>
    /// </summary>
    public static ScanProgressRecord? LastIn(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        string[] lines = text.Split('\n');
        int lastTerminated = text.EndsWith('\n') ? lines.Length - 1 : lines.Length - 2;
        for (int i = lastTerminated; i >= 0; i--)
        {
            if (TryParse(lines[i]) is { } record)
                return record;
        }
        return null;
    }

    /// <summary>
    /// A one-line "where it was when we killed it" phrase, or null when the file is missing, empty, or holds
    /// nothing parseable. Best-effort: a diagnostic read must never be able to fail the caller that is already
    /// reporting a failure.
    /// </summary>
    public static string? DescribeLastProgress(string? progressFilePath)
    {
        if (string.IsNullOrWhiteSpace(progressFilePath))
            return null;
        try
        {
            using var stream = new FileStream(
                progressFilePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return LastIn(reader.ReadToEnd())?.Describe();
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
               or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Human-readable phase and counters for a kill message.</summary>
    public string Describe() =>
        $"last reported phase '{Phase}' at {ElapsedMs / 1000d:0.#}s " +
        $"({FilesExtracted}/{FilesSupported} files extracted, {FilesSpooled} spooled)";
}
