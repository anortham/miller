using Blake3;

namespace Miller.Indexing;

/// <summary>
/// File-content hashing used for julie extract freshness. Julie v1 stores BLAKE3 over raw file bytes in
/// <c>files.content_hash</c> as a <c>blake3:&lt;hex&gt;</c> token, so callers must hash bytes exactly as they
/// exist on disk and normalize the stored token to bare hex (see <see cref="NormalizeHash"/>) before comparing.
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

    /// <summary>
    /// Reduce a julie content-hash token to Miller's canonical bare-hex form. julie v1 stores
    /// <c>files.content_hash</c> as <c>blake3:&lt;hex&gt;</c> (the algo prefix), while a disk hash from
    /// <see cref="Blake3FileHex"/> is bare hex. Strips a leading <c>blake3:</c> scheme token (scheme matched
    /// case-insensitively) and returns the hex value byte-exact (no case folding) so the result stays
    /// <see cref="StringComparison.Ordinal"/>-comparable. A token with no recognized prefix is already canonical.
    /// This is the SINGLE blake3 normalizer — never reuse it to strip a <c>sha256:</c> prefix (hash-domain split).
    /// </summary>
    public static string NormalizeHash(string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);

        const string Blake3Scheme = "blake3:";
        if (hash.StartsWith(Blake3Scheme, StringComparison.OrdinalIgnoreCase))
            return hash[Blake3Scheme.Length..];

        return hash;
    }
}
