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

public sealed record ToolReferenceContinuationIdentity(
    string WorkspaceId,
    string SymbolId,
    string ArtifactId,
    long Revision,
    string ReferenceKind,
    bool IncludeDefinition,
    int Limit);

public sealed record ToolReferenceContinuationCursor(int ExactOffset, int FallbackOffset);

public static partial class ToolOutputBudget
{
    public const int McpRowLimit = 10;
    public const int ContextMcpMaxTokens = 2400;
    public const int WorkspaceOnboardingMcpRowLimit = 3;
    public const int InspectFullBodyMaxBytes = 4 * 1024;
    public const int PatternsMcpMaxBytes = 12 * 1024;
    public const int WorkspaceHealthMcpMaxBytes = 12 * 1024;

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

    public static string RenderPrefixWithinByteBudget<T>(
        IReadOnlyList<T> items,
        int maxBytes,
        Func<IReadOnlyList<T>, int, string> renderer)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(renderer);
        if (maxBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes, "Output budget must be positive.");

        string full = renderer(items, 0);
        if (Encoding.UTF8.GetByteCount(full) <= maxBytes)
            return full;

        T[] retained = items.ToArray();
        int low = 0;
        int high = retained.Length;
        string best = renderer(Array.Empty<T>(), retained.Length);
        if (Encoding.UTF8.GetByteCount(best) > maxBytes)
            throw new InvalidOperationException("Output metadata exceeds the configured byte budget.");

        while (low <= high)
        {
            int count = low + ((high - low) / 2);
            var prefix = new ArraySegment<T>(retained, 0, count);
            string output = renderer(prefix, retained.Length - count);
            if (Encoding.UTF8.GetByteCount(output) <= maxBytes)
            {
                best = output;
                low = count + 1;
            }
            else
            {
                high = count - 1;
            }
        }

        return best;
    }

    public static string EncodeReferenceCursor(
        ToolReferenceContinuationIdentity identity,
        ToolReferenceContinuationCursor cursor)
    {
        ValidateReferenceIdentity(identity);
        if (cursor.ExactOffset < 0 || cursor.FallbackOffset < 0)
            throw new ArgumentOutOfRangeException(nameof(cursor), "Reference cursor offsets cannot be negative.");

        var unsigned = new UnsignedReferenceContinuationPayload(
            1,
            identity.WorkspaceId,
            identity.SymbolId,
            identity.ArtifactId,
            identity.Revision,
            identity.ReferenceKind,
            identity.IncludeDefinition,
            identity.Limit,
            cursor.ExactOffset,
            cursor.FallbackOffset);
        byte[] unsignedBytes = JsonSerializer.SerializeToUtf8Bytes(
            unsigned,
            ToolContinuationJsonContext.Default.UnsignedReferenceContinuationPayload);
        string checksum = Convert.ToHexStringLower(SHA256.HashData(unsignedBytes));
        var payload = new ReferenceContinuationPayload(
            unsigned.Version,
            unsigned.WorkspaceId,
            unsigned.SymbolId,
            unsigned.ArtifactId,
            unsigned.Revision,
            unsigned.ReferenceKind,
            unsigned.IncludeDefinition,
            unsigned.Limit,
            unsigned.ExactOffset,
            unsigned.FallbackOffset,
            checksum);
        return Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(
            payload,
            ToolContinuationJsonContext.Default.ReferenceContinuationPayload));
    }

    public static ToolReferenceContinuationCursor DecodeReferenceCursor(
        string token,
        ToolReferenceContinuationIdentity expected)
    {
        ValidateReferenceIdentity(expected);
        if (string.IsNullOrWhiteSpace(token))
            throw Refusal("continuation_invalid", "Reference continuation is empty.");

        byte[] payloadBytes;
        try
        {
            payloadBytes = Base64UrlDecode(token);
        }
        catch (FormatException)
        {
            throw Refusal("continuation_invalid", "Reference continuation is malformed.");
        }
        if (!string.Equals(Base64UrlEncode(payloadBytes), token, StringComparison.Ordinal))
            throw Refusal("continuation_invalid", "Reference continuation is not canonical base64url.");

        ReferenceContinuationPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize(
                payloadBytes,
                ToolContinuationJsonContext.Default.ReferenceContinuationPayload)
                ?? throw new JsonException("Reference continuation payload is empty.");
        }
        catch (JsonException)
        {
            throw Refusal("continuation_invalid", "Reference continuation is malformed.");
        }

        var unsigned = new UnsignedReferenceContinuationPayload(
            payload.Version,
            payload.WorkspaceId,
            payload.SymbolId,
            payload.ArtifactId,
            payload.Revision,
            payload.ReferenceKind,
            payload.IncludeDefinition,
            payload.Limit,
            payload.ExactOffset,
            payload.FallbackOffset);
        byte[] unsignedBytes = JsonSerializer.SerializeToUtf8Bytes(
            unsigned,
            ToolContinuationJsonContext.Default.UnsignedReferenceContinuationPayload);
        string checksum = Convert.ToHexStringLower(SHA256.HashData(unsignedBytes));
        if (string.IsNullOrWhiteSpace(payload.Checksum) || payload.Checksum.Length != checksum.Length)
            throw Refusal("continuation_invalid", "Reference continuation checksum is invalid.");
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(checksum),
                Encoding.ASCII.GetBytes(payload.Checksum)))
        {
            throw Refusal("continuation_invalid", "Reference continuation checksum is invalid.");
        }

        if (payload.Version != 1 ||
            !string.Equals(payload.WorkspaceId, expected.WorkspaceId, StringComparison.Ordinal) ||
            !string.Equals(payload.SymbolId, expected.SymbolId, StringComparison.Ordinal) ||
            !string.Equals(payload.ArtifactId, expected.ArtifactId, StringComparison.Ordinal) ||
            payload.Revision != expected.Revision ||
            !string.Equals(payload.ReferenceKind, expected.ReferenceKind, StringComparison.Ordinal) ||
            payload.IncludeDefinition != expected.IncludeDefinition ||
            payload.Limit != expected.Limit)
        {
            throw Refusal(
                "continuation_stale",
                "Reference continuation does not match the current workspace, target, artifact, filter, or limit.");
        }
        if (payload.ExactOffset < 0 || payload.FallbackOffset < 0)
            throw Refusal("continuation_offset_invalid", "Reference continuation offsets are invalid.");

        return new ToolReferenceContinuationCursor(payload.ExactOffset, payload.FallbackOffset);
    }

    private static void ValidateReferenceIdentity(ToolReferenceContinuationIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.SymbolId);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.ArtifactId);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.ReferenceKind);
        if (identity.Revision < 0)
            throw new ArgumentOutOfRangeException(nameof(identity), "Reference revision cannot be negative.");
        if (identity.Limit < 1)
            throw new ArgumentOutOfRangeException(nameof(identity), "Reference limit must be positive.");
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

    private sealed record UnsignedReferenceContinuationPayload(
        int Version,
        string WorkspaceId,
        string SymbolId,
        string ArtifactId,
        long Revision,
        string ReferenceKind,
        bool IncludeDefinition,
        int Limit,
        int ExactOffset,
        int FallbackOffset);

    private sealed record ReferenceContinuationPayload(
        int Version,
        string WorkspaceId,
        string SymbolId,
        string ArtifactId,
        long Revision,
        string ReferenceKind,
        bool IncludeDefinition,
        int Limit,
        int ExactOffset,
        int FallbackOffset,
        string Checksum);

    [JsonSerializable(typeof(UnsignedContinuationPayload))]
    [JsonSerializable(typeof(ContinuationPayload))]
    [JsonSerializable(typeof(UnsignedReferenceContinuationPayload))]
    [JsonSerializable(typeof(ReferenceContinuationPayload))]
    private sealed partial class ToolContinuationJsonContext : JsonSerializerContext;
}
