using System.Security.Cryptography;
using System.Text;

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

public readonly record struct BoundedPrefixRender(string Output, int RetainedCount);

public sealed record ToolPopulationContinuationIdentity(
    string Kind,
    string WorkspaceId,
    string PopulationFingerprint,
    string RequestFingerprint);

public sealed record ToolPopulationContinuationCursor(int Offset);

public static partial class ToolOutputBudget
{
    public const int McpRowLimit = 10;
    public const int ContextMcpMaxTokens = 2400;
    public const int WorkspaceOnboardingMcpRowLimit = 3;
    public const int InspectFullBodyMaxBytes = 4 * 1024;
    public const int InspectMcpMaxBytes = 12 * 1024;
    public const int InspectMcpDocMaxBytes = 2 * 1024;
    public const int EditMcpMaxBytes = 12 * 1024;
    public const int EditDiffMaxBytes = 8 * 1024;
    public const int ContentMcpMaxBytes = 12 * 1024;
    public const int SearchMcpMaxBytes = 12 * 1024;
    public const int SearchMcpSnippetMaxBytes = 512;
    public const int ImpactMcpMaxBytes = 12 * 1024;
    public const int PatternsMcpMaxBytes = 12 * 1024;
    public const int PatternsMcpDiagnosticReserveBytes = 1024;
    public const int WorkspaceMcpMaxBytes = 12 * 1024;
    public const int WorkspaceHealthMcpMaxBytes = WorkspaceMcpMaxBytes;

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
        Func<IReadOnlyList<T>, int, string> renderer,
        int maxCandidateItems = int.MaxValue) =>
        RenderPrefixWithinByteBudgetWithCount(items, maxBytes, renderer, maxCandidateItems).Output;

    public static BoundedPrefixRender RenderPrefixWithinByteBudgetWithCount<T>(
        IReadOnlyList<T> items,
        int maxBytes,
        Func<IReadOnlyList<T>, int, string> renderer,
        int maxCandidateItems = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(renderer);
        if (maxBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes, "Output budget must be positive.");
        if (maxCandidateItems < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxCandidateItems),
                maxCandidateItems,
                "Candidate limit must be positive.");
        }

        int candidateCount = Math.Min(items.Count, maxCandidateItems);
        IReadOnlyList<T> candidates = candidateCount == items.Count
            ? items
            : items.Take(candidateCount).ToArray();
        string full = renderer(candidates, items.Count - candidateCount);
        if (Encoding.UTF8.GetByteCount(full) <= maxBytes)
            return new BoundedPrefixRender(full, candidateCount);

        T[] retained = candidates.ToArray();
        int low = 0;
        int high = retained.Length;
        string best = renderer(Array.Empty<T>(), items.Count);
        int bestCount = 0;
        RequireWithinByteBudget(best, maxBytes);

        while (low <= high)
        {
            int count = low + ((high - low) / 2);
            var prefix = new ArraySegment<T>(retained, 0, count);
            string output = renderer(prefix, items.Count - count);
            if (Encoding.UTF8.GetByteCount(output) <= maxBytes)
            {
                best = output;
                bestCount = count;
                low = count + 1;
            }
            else
            {
                high = count - 1;
            }
        }

        return new BoundedPrefixRender(best, bestCount);
    }

    public static string RequireWithinByteBudget(string output, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (maxBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes, "Output budget must be positive.");
        if (Encoding.UTF8.GetByteCount(output) > maxBytes)
        {
            throw new ToolDiagnosticException(ToolDiagnostic.Refusal(
                "output_metadata_too_large",
                "Output metadata exceeds the configured byte budget; narrow the request inputs."));
        }

        return output;
    }

    public static string BoundSearchSnippet(string snippet, bool boundAgentOutput, out bool truncated)
    {
        ArgumentNullException.ThrowIfNull(snippet);
        truncated = false;
        if (!boundAgentOutput || Encoding.UTF8.GetByteCount(snippet) <= SearchMcpSnippetMaxBytes)
            return snippet;

        int byteBudget = SearchMcpSnippetMaxBytes - Encoding.UTF8.GetByteCount("…");
        int utf16Length = 0;
        int utf8Length = 0;
        foreach (Rune rune in snippet.EnumerateRunes())
        {
            if (utf8Length + rune.Utf8SequenceLength > byteBudget)
                break;
            utf8Length += rune.Utf8SequenceLength;
            utf16Length += rune.Utf16SequenceLength;
        }

        truncated = true;
        return snippet[..utf16Length] + "…";
    }

    public static string TruncateUtf8(string text, int maxBytes, string suffix)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(suffix);
        if (maxBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes, "Output budget must be positive.");

        int suffixBytes = Encoding.UTF8.GetByteCount(suffix);
        if (suffixBytes > maxBytes)
            throw new ArgumentException("Suffix exceeds the output byte budget.", nameof(suffix));
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes)
            return text;

        byte[] bytes = Encoding.UTF8.GetBytes(text);
        int prefixBudget = maxBytes - suffixBytes;
        long prefixEnd = FindValidEnd(bytes, 0, prefixBudget);
        return StrictUtf8.GetString(bytes.AsSpan(0, checked((int)prefixEnd))) + suffix;
    }

    public static string EncodePopulationCursor(
        ToolPopulationContinuationIdentity identity,
        ToolPopulationContinuationCursor cursor)
    {
        ValidatePopulationIdentity(identity);
        if (cursor.Offset < 0)
            throw new ArgumentOutOfRangeException(nameof(cursor), "Population cursor offset cannot be negative.");

        var buffer = new List<byte>(192) { ContinuationFormatVersion, PopulationContinuationType };
        WriteTokenString(buffer, identity.Kind);
        WriteTokenString(buffer, identity.WorkspaceId);
        WriteTokenString(buffer, identity.PopulationFingerprint);
        WriteTokenString(buffer, identity.RequestFingerprint);
        WriteTokenVarInt(buffer, cursor.Offset);
        return Base64UrlEncode(SealToken(buffer));
    }

    public static ToolPopulationContinuationCursor DecodePopulationCursor(
        string token,
        ToolPopulationContinuationIdentity expected)
    {
        ValidatePopulationIdentity(expected);
        PopulationContinuationPayload payload = DecodePopulationCursorPayload(token);

        if (!string.Equals(payload.Kind, expected.Kind, StringComparison.Ordinal))
        {
            throw Refusal(
                "continuation_kind_mismatch",
                "Continuation belongs to a different result population.");
        }
        if (!string.Equals(payload.WorkspaceId, expected.WorkspaceId, StringComparison.Ordinal) ||
            !string.Equals(
                payload.PopulationFingerprint,
                expected.PopulationFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(payload.RequestFingerprint, expected.RequestFingerprint, StringComparison.Ordinal))
        {
            throw Refusal(
                "stale_continuation",
                "Continuation no longer matches the requested result population.");
        }

        return new ToolPopulationContinuationCursor(payload.Offset);
    }

    internal static ToolPopulationContinuationCursor PeekPopulationCursorPosition(string token) =>
        new(DecodePopulationCursorPayload(token).Offset);

    private static PopulationContinuationPayload DecodePopulationCursorPayload(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw Refusal("continuation_invalid", "Population continuation is empty.");

        byte[] payloadBytes;
        try
        {
            payloadBytes = Base64UrlDecode(token);
        }
        catch (FormatException)
        {
            throw Refusal("continuation_invalid", "Population continuation is malformed.");
        }
        if (!string.Equals(Base64UrlEncode(payloadBytes), token, StringComparison.Ordinal))
            throw Refusal("continuation_invalid", "Population continuation is not canonical base64url.");

        TokenReader reader = OpenTokenReader(
            payloadBytes,
            PopulationContinuationType,
            invalidMessage: "Population continuation is malformed.",
            checksumMessage: "Population continuation checksum is invalid.",
            versionMessage: "Population continuation version is unsupported.");
        string kind;
        string workspaceId;
        string populationFingerprint;
        string requestFingerprint;
        long offset;
        try
        {
            kind = reader.ReadString();
            workspaceId = reader.ReadString();
            populationFingerprint = reader.ReadString();
            requestFingerprint = reader.ReadString();
            offset = reader.ReadVarInt();
            reader.RequireEnd();
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
        {
            throw Refusal("continuation_invalid", "Population continuation is malformed.");
        }

        if (offset > int.MaxValue)
            throw Refusal("continuation_offset_invalid", "Population continuation offset is invalid.");

        return new PopulationContinuationPayload(
            kind, workspaceId, populationFingerprint, requestFingerprint, (int)offset);
    }

    private static void ValidatePopulationIdentity(ToolPopulationContinuationIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.Kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.PopulationFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.RequestFingerprint);
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
        var buffer = new List<byte>(160) { ContinuationFormatVersion, BodyContinuationType };
        WriteTokenString(buffer, identity.WorkspaceId);
        WriteTokenString(buffer, identity.SymbolId);
        WriteTokenString(buffer, identity.ExtractorHash);
        WriteTokenVarInt(buffer, identity.SourceStartByte);
        WriteTokenVarInt(buffer, identity.SourceEndByte);
        WriteTokenVarInt(buffer, nextOffset);
        return Base64UrlEncode(SealToken(buffer));
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

        TokenReader reader = OpenTokenReader(
            bytes,
            BodyContinuationType,
            invalidMessage: "Continuation token is malformed.",
            checksumMessage: "Continuation token checksum does not match.",
            versionMessage: "Continuation token version is unsupported.");
        try
        {
            var payload = new ContinuationPayload(
                reader.ReadString(),
                reader.ReadString(),
                reader.ReadString(),
                reader.ReadVarInt(),
                reader.ReadVarInt(),
                reader.ReadVarInt());
            reader.RequireEnd();
            return payload;
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
        {
            throw Refusal("continuation_invalid", "Continuation token is malformed.");
        }
    }

    private const byte ContinuationFormatVersion = 2;
    private const byte BodyContinuationType = 1;
    private const byte PopulationContinuationType = 2;
    private const int ContinuationChecksumBytes = 32;

    private static void WriteTokenString(List<byte> buffer, string value)
    {
        bool hexPacked = IsPackableHex(value);
        byte[] raw = hexPacked ? Convert.FromHexString(value) : StrictUtf8.GetBytes(value);
        buffer.Add(hexPacked ? (byte)1 : (byte)0);
        WriteTokenVarInt(buffer, raw.Length);
        buffer.AddRange(raw);
    }

    private static bool IsPackableHex(string value)
    {
        if (value.Length < 2 || value.Length % 2 != 0)
            return false;
        foreach (char c in value)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
                return false;
        }

        return true;
    }

    private static void WriteTokenVarInt(List<byte> buffer, long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        ulong remaining = (ulong)value;
        while (remaining >= 0x80)
        {
            buffer.Add((byte)(remaining | 0x80));
            remaining >>= 7;
        }

        buffer.Add((byte)remaining);
    }

    private static byte[] SealToken(List<byte> buffer)
    {
        byte[] token = new byte[buffer.Count + ContinuationChecksumBytes];
        buffer.CopyTo(token, 0);
        SHA256.HashData(token.AsSpan(0, buffer.Count), token.AsSpan(buffer.Count));
        return token;
    }

    private static TokenReader OpenTokenReader(
        byte[] payload,
        byte expectedType,
        string invalidMessage,
        string checksumMessage,
        string versionMessage)
    {
        if (payload.Length < 2 + ContinuationChecksumBytes)
            throw Refusal("continuation_invalid", invalidMessage);
        if (payload[0] != ContinuationFormatVersion)
            throw Refusal("continuation_invalid", versionMessage);
        if (payload[1] != expectedType)
            throw Refusal("continuation_invalid", invalidMessage);

        int contentLength = payload.Length - ContinuationChecksumBytes;
        Span<byte> computed = stackalloc byte[ContinuationChecksumBytes];
        SHA256.HashData(payload.AsSpan(0, contentLength), computed);
        if (!CryptographicOperations.FixedTimeEquals(computed, payload.AsSpan(contentLength)))
            throw Refusal("continuation_invalid", checksumMessage);

        return new TokenReader(payload, position: 2, end: contentLength);
    }

    private sealed class TokenReader(byte[] bytes, int position, int end)
    {
        private readonly byte[] _bytes = bytes;
        private readonly int _end = end;
        private int _position = position;

        public long ReadVarInt()
        {
            ulong value = 0;
            int shift = 0;
            while (true)
            {
                if (_position >= _end || shift > 63)
                    throw new FormatException("Continuation varint is truncated or oversized.");
                byte current = _bytes[_position++];
                value |= (ulong)(current & 0x7F) << shift;
                if ((current & 0x80) == 0)
                    break;
                shift += 7;
            }

            if (value > long.MaxValue)
                throw new FormatException("Continuation varint exceeds Int64.");
            return (long)value;
        }

        public string ReadString()
        {
            if (_position >= _end)
                throw new FormatException("Continuation string flag is missing.");
            byte flag = _bytes[_position++];
            if (flag > 1)
                throw new FormatException("Continuation string flag is unknown.");
            long length = ReadVarInt();
            if (length > _end - _position)
                throw new FormatException("Continuation string payload is truncated.");
            ReadOnlySpan<byte> raw = _bytes.AsSpan(_position, (int)length);
            _position += (int)length;
            return flag == 1 ? Convert.ToHexStringLower(raw) : StrictUtf8.GetString(raw);
        }

        public void RequireEnd()
        {
            if (_position != _end)
                throw new FormatException("Continuation payload has trailing bytes.");
        }
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

    private sealed record ContinuationPayload(
        string WorkspaceId,
        string SymbolId,
        string ExtractorHash,
        long SourceStartByte,
        long SourceEndByte,
        long NextOffset);

    private sealed record PopulationContinuationPayload(
        string Kind,
        string WorkspaceId,
        string PopulationFingerprint,
        string RequestFingerprint,
        int Offset);
}
