namespace Miller.Core.Freshness;

/// <summary>The outcome of a <see cref="StalenessCheck.Check"/>: the indexed view either matches the file or it does not.</summary>
public enum FreshnessResult
{
    /// <summary>The index matches the current file — safe to act on.</summary>
    Fresh,

    /// <summary>The file changed under the index — the caller must re-read / re-index before mutating.</summary>
    Stale,
}

/// <summary>
/// What Miller recorded about a file when it last indexed it: the content hash julie computed, and
/// optionally the exact source text captured at index time. <see cref="IndexedText"/> is null when only the
/// hash was retained (the common case); supply it to enable the exact-text tiebreaker.
/// </summary>
public sealed record IndexedSnapshot
{
    /// <summary>The content hash recorded at index time (julie's blake3 digest, an opaque string token).</summary>
    public string IndexedHash { get; }

    /// <summary>The exact source text recorded at index time, or null if only the hash was retained.</summary>
    public string? IndexedText { get; }

    /// <exception cref="ArgumentNullException"><paramref name="indexedHash"/> is null.</exception>
    public IndexedSnapshot(string indexedHash, string? indexedText)
    {
        ArgumentNullException.ThrowIfNull(indexedHash);
        IndexedHash = indexedHash;
        IndexedText = indexedText;
    }
}

/// <summary>
/// What Miller observes about a file right now: its current content hash, and optionally the exact text
/// just read from disk. <see cref="CurrentText"/> is null when the caller only stat-hashed the file.
/// </summary>
public sealed record CurrentProbe
{
    /// <summary>The file's current content hash.</summary>
    public string CurrentHash { get; }

    /// <summary>The file's current exact source text, or null if not read.</summary>
    public string? CurrentText { get; }

    /// <exception cref="ArgumentNullException"><paramref name="currentHash"/> is null.</exception>
    public CurrentProbe(string currentHash, string? currentText)
    {
        ArgumentNullException.ThrowIfNull(currentHash);
        CurrentHash = currentHash;
        CurrentText = currentText;
    }
}

/// <summary>
/// The pure mutation-gate primitive (decision log #6) M6 <c>edit</c> calls before applying a change. Compares
/// an <see cref="IndexedSnapshot"/> against a <see cref="CurrentProbe"/> with zero file system access.
///
/// <para>A target is <see cref="FreshnessResult.Stale"/> iff the content hash differs, OR exact text is
/// supplied on <em>both</em> sides and differs (ordinal/byte-exact). When exact text is supplied on only one
/// side it cannot be compared, so the hash alone decides. mtime is deliberately not a parameter: per eros, the
/// content hash is the authority and mtime is only ever a cheap upstream "maybe changed" pre-filter, never the
/// staleness verdict.</para>
/// </summary>
public static class StalenessCheck
{
    /// <summary>
    /// Compare the indexed snapshot against the current probe. See the type summary for the exact rule.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="indexed"/> or <paramref name="current"/> is null.</exception>
    public static FreshnessResult Check(IndexedSnapshot indexed, CurrentProbe current)
    {
        ArgumentNullException.ThrowIfNull(indexed);
        ArgumentNullException.ThrowIfNull(current);

        // Primary signal: the content hash. A difference is decisive regardless of text.
        if (!string.Equals(indexed.IndexedHash, current.CurrentHash, StringComparison.Ordinal))
            return FreshnessResult.Stale;

        // Hashes agree. If exact text is available on both sides, it is the final word (guards a hash
        // collision or a line-ending/normalization mismatch). A byte-exact difference => stale.
        if (indexed.IndexedText is { } indexedText && current.CurrentText is { } currentText
            && !string.Equals(indexedText, currentText, StringComparison.Ordinal))
        {
            return FreshnessResult.Stale;
        }

        return FreshnessResult.Fresh;
    }
}
