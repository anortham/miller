using System.Text;

namespace Miller.Indexing;

internal static class SourceTextDecoder
{
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly UnicodeEncoding StrictUtf16Le =
        new(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);

    private static readonly UnicodeEncoding StrictUtf16Be =
        new(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);

    public static bool TryDecode(byte[] bytes, out string text)
    {
        try
        {
            text = Decode(bytes);
            return true;
        }
        catch (Exception ex) when (ex is DecoderFallbackException or ArgumentException)
        {
            text = string.Empty;
            return false;
        }
    }

    public static string Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        if (bytes is [0xFF, 0xFE, ..])
            return StrictUtf16Le.GetString(bytes, 2, bytes.Length - 2);
        if (bytes is [0xFE, 0xFF, ..])
            return StrictUtf16Be.GetString(bytes, 2, bytes.Length - 2);

        return StrictUtf8.GetString(bytes);
    }

    public static string? SliceUtf8ByteSpan(string text, int startByte, int endByte)
    {
        if (startByte < 0 || endByte <= startByte)
            return null;

        byte[] bytes = Encoding.UTF8.GetBytes(text);
        if (startByte >= bytes.Length)
            return null;

        int end = Math.Min(endByte, bytes.Length);
        if (end <= startByte)
            return null;

        return Encoding.UTF8.GetString(bytes, startByte, end - startByte);
    }
}
