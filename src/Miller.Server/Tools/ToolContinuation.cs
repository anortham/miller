using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Miller.Server.Tools;

public sealed record ToolContinuationIdentity(
    string WorkspaceId,
    string SymbolId,
    string ExtractorHash,
    long SourceStartByte,
    long SourceEndByte);

public sealed record ToolOutputPage(
    string Text,
    long StartOffset,
    long EndOffset,
    bool Truncated,
    string? Continuation);

public static partial class ToolOutputBudget
{
    public const int InspectFullBodyMaxBytes = 16 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static ToolOutputPage PageBody(
        string text,
        int maxBytes,
        ToolContinuationIdentity identity,
        string? continuation)
    {
        ArgumentNullException.ThrowIfNull(text);
        ValidateIdentity(identity);
        if (maxBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes, "Output budget must be positive.");

        byte[] bytes = Encoding.UTF8.GetBytes(text);
        long start = 0;
        if (!string.IsNullOrWhiteSpace(continuation))
        {
            ContinuationPayload payload = Decode(continuation);
            ValidateIdentity(payload, identity);
            start = payload.NextOffset;
            if (start < 0 || start >= bytes.LongLength)
                throw Refusal(
                    "continuation_offset_invalid",
                    "Continuation offset is outside the current body.");
            if ((bytes[start] & 0b1100_0000) == 0b1000_0000)
                throw Refusal(
                    "continuation_offset_invalid",
                    "Continuation offset does not start at a UTF-8 code point boundary.");
        }

        long tentativeEnd = Math.Min(bytes.LongLength, start + maxBytes);
        long end = FindValidEnd(bytes, start, tentativeEnd);
        if (end == start && end < bytes.LongLength)
            throw Refusal(
                "output_budget_too_small",
                "Output budget cannot contain the next UTF-8 code point.");

        string pageText = StrictUtf8.GetString(bytes.AsSpan(checked((int)start), checked((int)(end - start))));
        bool truncated = end < bytes.LongLength;
        string? next = truncated ? Encode(identity, end) : null;
        return new ToolOutputPage(pageText, start, end, truncated, next);
    }

    private static long FindValidEnd(byte[] bytes, long start, long tentativeEnd)
    {
        long end = tentativeEnd;
        while (end > start)
        {
            try
            {
                _ = StrictUtf8.GetCharCount(bytes.AsSpan(checked((int)start), checked((int)(end - start))));
                return end;
            }
            catch (DecoderFallbackException)
            {
                end--;
            }
        }

        return start;
    }

    private static string Encode(ToolContinuationIdentity identity, long nextOffset)
    {
        var unsigned = new UnsignedContinuationPayload(
            Version: 1,
            identity.WorkspaceId,
            identity.SymbolId,
            identity.ExtractorHash,
            identity.SourceStartByte,
            identity.SourceEndByte,
            nextOffset);
        byte[] unsignedBytes = JsonSerializer.SerializeToUtf8Bytes(
            unsigned,
            ToolContinuationJsonContext.Default.UnsignedContinuationPayload);
        string checksum = Convert.ToHexStringLower(SHA256.HashData(unsignedBytes));
        var payload = new ContinuationPayload(
            unsigned.Version,
            unsigned.WorkspaceId,
            unsigned.SymbolId,
            unsigned.ExtractorHash,
            unsigned.SourceStartByte,
            unsigned.SourceEndByte,
            unsigned.NextOffset,
            checksum);
        return Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(
            payload,
            ToolContinuationJsonContext.Default.ContinuationPayload));
    }

    private static ContinuationPayload Decode(string token)
    {
        byte[] bytes;
        try
        {
            bytes = Base64UrlDecode(token);
        }
        catch (FormatException)
        {
            throw Refusal("continuation_invalid", "Continuation token is not valid base64url.");
        }

        if (!string.Equals(Base64UrlEncode(bytes), token, StringComparison.Ordinal))
            throw Refusal("continuation_invalid", "Continuation token is not canonical base64url.");

        ContinuationPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize(
                bytes,
                ToolContinuationJsonContext.Default.ContinuationPayload);
        }
        catch (JsonException)
        {
            throw Refusal("continuation_invalid", "Continuation token is not valid JSON.");
        }

        if (payload is null || payload.Version != 1)
            throw Refusal("continuation_invalid", "Continuation token version is unsupported.");
        if (string.IsNullOrWhiteSpace(payload.Checksum))
            throw Refusal("continuation_invalid", "Continuation token checksum is missing.");

        var unsigned = new UnsignedContinuationPayload(
            payload.Version,
            payload.WorkspaceId,
            payload.SymbolId,
            payload.ExtractorHash,
            payload.SourceStartByte,
            payload.SourceEndByte,
            payload.NextOffset);
        string checksum = Convert.ToHexStringLower(
            SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
                unsigned,
                ToolContinuationJsonContext.Default.UnsignedContinuationPayload)));
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(checksum),
                Encoding.ASCII.GetBytes(payload.Checksum)))
        {
            throw Refusal("continuation_invalid", "Continuation token checksum does not match.");
        }

        return payload;
    }

    private static void ValidateIdentity(ToolContinuationIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.SymbolId);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.ExtractorHash);
        if (identity.SourceStartByte < 0 || identity.SourceEndByte <= identity.SourceStartByte)
            throw new ArgumentException("Continuation source span must be positive.", nameof(identity));
    }

    private static void ValidateIdentity(
        ContinuationPayload payload,
        ToolContinuationIdentity expected)
    {
        if (!string.Equals(payload.WorkspaceId, expected.WorkspaceId, StringComparison.Ordinal))
            throw Refusal(
                "continuation_workspace_mismatch",
                "Continuation belongs to a different workspace.");
        if (!string.Equals(payload.SymbolId, expected.SymbolId, StringComparison.Ordinal))
            throw Refusal(
                "continuation_symbol_mismatch",
                "Continuation belongs to a different symbol.");
        if (!string.Equals(payload.ExtractorHash, expected.ExtractorHash, StringComparison.Ordinal))
            throw Refusal(
                "continuation_hash_mismatch",
                "Continuation was created for different extracted content.");
        if (payload.SourceStartByte != expected.SourceStartByte ||
            payload.SourceEndByte != expected.SourceEndByte)
        {
            throw Refusal(
                "continuation_span_mismatch",
                "Continuation was created for a different source span.");
        }
    }

    private static ToolDiagnosticException Refusal(string code, string message) =>
        new(ToolDiagnostic.Refusal(
            code,
            message,
            [new ToolDiagnosticAction("inspect(target=\"<symbol-id>\", depth=\"full\")", "restart from the current symbol identity")]));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch
        {
            0 => string.Empty,
            2 => "==",
            3 => "=",
            _ => throw new FormatException("Invalid base64url length."),
        };
        return Convert.FromBase64String(padded);
    }

    private sealed record UnsignedContinuationPayload(
        int Version,
        string WorkspaceId,
        string SymbolId,
        string ExtractorHash,
        long SourceStartByte,
        long SourceEndByte,
        long NextOffset);

    private sealed record ContinuationPayload(
        int Version,
        string WorkspaceId,
        string SymbolId,
        string ExtractorHash,
        long SourceStartByte,
        long SourceEndByte,
        long NextOffset,
        string Checksum);

    [JsonSerializable(typeof(UnsignedContinuationPayload))]
    [JsonSerializable(typeof(ContinuationPayload))]
    private sealed partial class ToolContinuationJsonContext : JsonSerializerContext;
}
