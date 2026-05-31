using Miller.Core.Freshness;
using Miller.Indexing;

namespace Miller.Server.Hosting;

/// <summary>
/// The M6 <c>edit</c> mutation gate (m6-design decision-3, Components/3, impl-order step 6). It answers one
/// question before any edit is planned/applied: <em>is the index's view of this file still the file on disk?</em>
/// It reads julie's BLAKE3 snapshot from <c>files.hash</c>, hashes the current disk bytes with BLAKE3, and runs
/// both hashes through the pure <see cref="StalenessCheck"/>. Exact text is still supplied when available as the
/// collision/normalization guard, but the required freshness path is raw bytes vs <c>files.hash</c>.
///
/// <para>If julie has no hash snapshot for the file, or the extract metadata does not declare
/// <c>hash_algorithm = blake3</c>, the gate cannot verify freshness and reports
/// <see cref="FreshnessResult.Stale"/> with <see cref="GateResult.IndexedContentFound"/> = false — the tool
/// surfaces a "no indexed snapshot" message and blocks unless <c>allow_stale</c>. The gate itself never decides
/// whether <c>allow_stale</c> overrides the verdict; that is the tool's call (decision-3).</para>
/// </summary>
public static class FreshnessGate
{
    /// <summary>
    /// The gate verdict plus whether an indexed snapshot existed at all — the tool needs both to craft the
    /// right message and to honour <c>allow_stale</c> correctly.
    /// </summary>
    /// <param name="Result">Fresh when the indexed BLAKE3 hash matches the disk bytes; Stale otherwise.</param>
    /// <param name="IndexedContentFound">
    /// True when julie had a usable BLAKE3 hash for the file; false when there was nothing trustworthy to
    /// compare against, which always reads <see cref="FreshnessResult.Stale"/>.
    /// </param>
    public readonly record struct GateResult(FreshnessResult Result, bool IndexedContentFound);

    /// <summary>
    /// Check whether the index's snapshot of <paramref name="filePath"/> matches the current disk bytes at the
    /// same path. Relative paths resolve against the process working directory.
    /// </summary>
    /// <param name="dbPath">The julie extract DB Miller reads (Mode=ReadOnly).</param>
    /// <param name="filePath">The indexed (relative) file path julie keyed the snapshot under.</param>
    /// <param name="diskText">The file's current on-disk text, supplied only for exact-text comparison.</param>
    /// <exception cref="ArgumentNullException"><paramref name="dbPath"/>, <paramref name="filePath"/>, or <paramref name="diskText"/> is null.</exception>
    /// <exception cref="FileNotFoundException">The DB file does not exist (surfaced from the read layer).</exception>
    /// <exception cref="InvalidOperationException">The DB directory is not writable (D4 read discipline).</exception>
    public static GateResult Check(string dbPath, string filePath, string diskText)
    {
        return Check(dbPath, filePath, filePath, diskText);
    }

    /// <summary>
    /// Check whether the index's snapshot of <paramref name="indexedFilePath"/> matches the current disk bytes
    /// at <paramref name="diskPath"/>.
    /// </summary>
    /// <param name="dbPath">The julie extract DB Miller reads (Mode=ReadOnly).</param>
    /// <param name="indexedFilePath">The indexed relative file path julie keyed the snapshot under.</param>
    /// <param name="diskPath">The current file path to hash as raw bytes.</param>
    /// <param name="diskText">The file's current text, supplied only for exact-text comparison.</param>
    public static GateResult Check(string dbPath, string indexedFilePath, string diskPath, string diskText)
    {
        ArgumentNullException.ThrowIfNull(dbPath);
        ArgumentNullException.ThrowIfNull(indexedFilePath);
        ArgumentNullException.ThrowIfNull(diskPath);
        ArgumentNullException.ThrowIfNull(diskText);

        string? hashAlgorithm = ExtractFileHashReader.ReadHashAlgorithm(dbPath);
        if (!StringComparer.Ordinal.Equals(hashAlgorithm, "blake3"))
            return new GateResult(FreshnessResult.Stale, IndexedContentFound: false);

        string? indexedHash = ExtractFileHashReader.ReadFileHash(dbPath, indexedFilePath);
        if (string.IsNullOrWhiteSpace(indexedHash))
            return new GateResult(FreshnessResult.Stale, IndexedContentFound: false);

        string? indexedText = ExtractReader.ReadIndexedFileText(dbPath, indexedFilePath);
        string currentHash = ContentHasher.Blake3FileHex(diskPath);
        var indexed = new IndexedSnapshot(indexedHash, indexedText);
        var current = new CurrentProbe(currentHash, diskText);
        return new GateResult(StalenessCheck.Check(indexed, current), IndexedContentFound: true);
    }
}
