using System.Text;

namespace Miller.Server.Telemetry;

/// <summary>
/// Estimates the token cost of a returned string — the north-star KPI input (bytes_returned → est_tokens).
/// M2 uses a UTF-8-bytes / 4 heuristic behind this seam; it is swappable for <c>Microsoft.ML.Tokenizers</c>
/// later without touching callers. Deliberately cheap: this runs on every tool call.
/// </summary>
public static class TokenEstimator
{
    /// <summary>The UTF-8 byte length of <paramref name="text"/> (the bytes_returned proxy).</summary>
    public static long ByteLength(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : Encoding.UTF8.GetByteCount(text);

    /// <summary>Estimated tokens for <paramref name="text"/> (≈ UTF-8 bytes / 4, rounded up).</summary>
    public static long Count(string? text) => CountFromBytes(ByteLength(text));

    /// <summary>Estimated tokens for an already-known UTF-8 byte length (≈ bytes / 4, rounded up).</summary>
    public static long CountFromBytes(long bytes) => bytes <= 0 ? 0 : (bytes + 3) / 4;
}
