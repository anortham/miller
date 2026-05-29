using System.Security.Cryptography;
using System.Text;
using Miller.Core.Freshness;
using Miller.Indexing;

namespace Miller.Server.Hosting;

/// <summary>
/// The M6 <c>edit</c> mutation gate (m6-design decision-3, Components/3, impl-order step 6). It answers one
/// question before any edit is planned/applied: <em>is the index's view of this file still the file on disk?</em>
/// It reads julie's indexed snapshot (<see cref="ExtractReader.ReadIndexedFileText"/>) and compares it to the
/// current disk text. Both sides are SHA256-hashed (the SAME algorithm both sides — this deliberately sidesteps
/// julie's blake3 so Miller needs no new hash dependency and the comparison is content-authoritative, not
/// mtime-based) and run through the pure <see cref="StalenessCheck"/>; the exact text is supplied on both sides
/// so its byte-exact tiebreaker is the final word (guards a hash collision or a line-ending mismatch).
///
/// <para>If julie has no snapshot for the file (it was never indexed, or its content column is NULL), the gate
/// cannot verify freshness and reports <see cref="FreshnessResult.Stale"/> with
/// <see cref="GateResult.IndexedContentFound"/> = false — the tool surfaces a "no indexed snapshot" message and
/// blocks unless <c>allow_stale</c>. The gate itself never decides whether <c>allow_stale</c> overrides the
/// verdict; that is the tool's call (decision-3).</para>
/// </summary>
public static class FreshnessGate
{
    /// <summary>
    /// The gate verdict plus whether an indexed snapshot existed at all — the tool needs both to craft the
    /// right message and to honour <c>allow_stale</c> correctly.
    /// </summary>
    /// <param name="Result">Fresh when the indexed snapshot matches the disk text; Stale otherwise.</param>
    /// <param name="IndexedContentFound">
    /// True when julie had a content snapshot for the file; false when there was nothing to compare against
    /// (an un-indexed file or a NULL content column), which always reads <see cref="FreshnessResult.Stale"/>.
    /// </param>
    public readonly record struct GateResult(FreshnessResult Result, bool IndexedContentFound);

    /// <summary>
    /// Check whether the index's snapshot of <paramref name="filePath"/> matches <paramref name="diskText"/>.
    /// </summary>
    /// <param name="dbPath">The julie extract DB Miller reads (Mode=ReadOnly).</param>
    /// <param name="filePath">The indexed (relative) file path julie keyed the snapshot under.</param>
    /// <param name="diskText">The file's current on-disk text (already read by the caller).</param>
    /// <exception cref="ArgumentNullException"><paramref name="dbPath"/>, <paramref name="filePath"/>, or <paramref name="diskText"/> is null.</exception>
    /// <exception cref="FileNotFoundException">The DB file does not exist (surfaced from the read layer).</exception>
    /// <exception cref="InvalidOperationException">The DB directory is not writable (D4 read discipline).</exception>
    public static GateResult Check(string dbPath, string filePath, string diskText)
    {
        ArgumentNullException.ThrowIfNull(dbPath);
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(diskText);

        string? indexedText = ExtractReader.ReadIndexedFileText(dbPath, filePath);
        if (indexedText is null)
        {
            // No snapshot to compare against — cannot verify freshness, so treat as Stale (the tool decides
            // whether allow_stale lets the edit through despite the missing baseline).
            return new GateResult(FreshnessResult.Stale, IndexedContentFound: false);
        }

        var indexed = new IndexedSnapshot(Sha256Hex(indexedText), indexedText);
        var current = new CurrentProbe(Sha256Hex(diskText), diskText);
        return new GateResult(StalenessCheck.Check(indexed, current), IndexedContentFound: true);
    }

    private static string Sha256Hex(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
