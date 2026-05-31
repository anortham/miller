using Blake3;

namespace Miller.Indexing;

/// <summary>
/// File-content hashing used for julie extract freshness. Julie stores BLAKE3 over raw file bytes in
/// <c>files.hash</c>, so callers must hash bytes exactly as they exist on disk.
/// </summary>
public static class ContentHasher
{
    public static string Blake3Hex(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        var hash = Hasher.Hash(bytes);
        return Convert.ToHexStringLower(hash.AsSpan());
    }

    public static string Blake3FileHex(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return Blake3Hex(File.ReadAllBytes(path));
    }
}
