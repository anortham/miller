using Microsoft.Data.Sqlite;

namespace Miller.Indexing;

/// <summary>
/// The identity of one <c>symbols.db</c> generation: the latest extraction revision plus the
/// <c>artifact_metadata.artifact_id</c> that a full-rebuild promote replaces.
/// </summary>
/// <remarks>
/// Revision alone is NOT a generation identity. <see cref="FullRebuildPromotion"/> swaps in a freshly extracted
/// file whose revision counter restarts at 1, so a workspace sitting at revision 1 that force-rebuilds lands on
/// revision 1 again. A derived sidecar that compares only revision reads that collision as "fresh" and serves
/// pre-rebuild data indefinitely. Every derived artifact must therefore stamp and compare the artifact id too.
/// </remarks>
public readonly record struct SymbolsArtifactIdentity(
    long Revision,
    string? ArtifactId,
    bool MetadataPresent = false)
{
    /// <summary>Read the current identity of <paramref name="symbolsDbPath"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="symbolsDbPath"/> is null or blank.</exception>
    public static SymbolsArtifactIdentity Read(string symbolsDbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolsDbPath);
        using var freshness = new FreshnessReader(symbolsDbPath);
        return new SymbolsArtifactIdentity(
            freshness.LatestRevision(), freshness.ArtifactId(), freshness.HasArtifactMetadata());
    }

    /// <summary>
    /// Whether a derived sidecar stamped with <paramref name="stampedRevision"/> and
    /// <paramref name="stampedArtifactId"/> was built from this generation.
    /// </summary>
    /// <remarks>
    /// A sidecar written before artifact stamping existed carries a null id and can never be proven current, so
    /// it reports stale and rebuilds exactly once.
    ///
    /// An artifact with no id of its own splits two ways. No <c>artifact_metadata</c> at all is a genuine
    /// pre-stamping extract, so it falls back to revision equality — the historical behaviour — rather than
    /// rebuilding forever. A metadata table that EXISTS but carries no <c>artifact_id</c> is something the
    /// pinned extractor never emits, so it is treated as unprovable and refused rather than trusted.
    /// </remarks>
    /// <summary>
    /// Read the current identity of <paramref name="symbolsDbPath"/>, yielding a null <see cref="ArtifactId"/>
    /// rather than throwing when the artifact is absent, locked, or carries no id.
    /// </summary>
    /// <remarks>
    /// A read gate must not turn an unreadable artifact into a hard failure: a null id means "cannot prove the
    /// generation", which <see cref="MatchesArtifact"/> already treats as the historical revision-only
    /// behaviour. Turning it into a throw would take a pre-artifact_id extract from working to broken.
    /// </remarks>
    public static SymbolsArtifactIdentity TryRead(string symbolsDbPath)
    {
        if (string.IsNullOrWhiteSpace(symbolsDbPath) || !File.Exists(symbolsDbPath))
            return new SymbolsArtifactIdentity(0, null);

        try
        {
            return Read(symbolsDbPath);
        }
        catch (Exception ex) when (
            ex is SqliteException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return new SymbolsArtifactIdentity(0, null);
        }
    }

    /// <summary>
    /// Whether a derived sidecar stamped with <paramref name="stampedArtifactId"/> came from this generation,
    /// ignoring revision. Use when the caller has ALREADY checked the sidecar against its own expected revision:
    /// that expectation may legitimately differ from the live artifact's latest revision, so re-checking it here
    /// would reject a sidecar the caller deliberately asked for. The artifact id is the part revision cannot
    /// prove — a promote restarts the counter, so only the id separates two generations at the same revision.
    /// </summary>
    public bool MatchesArtifact(string? stampedArtifactId)
    {
        if (ArtifactId is null)
            return !MetadataPresent;
        return string.Equals(stampedArtifactId, ArtifactId, StringComparison.Ordinal);
    }

    public bool Matches(long stampedRevision, string? stampedArtifactId)
    {
        if (stampedRevision != Revision)
            return false;
        if (ArtifactId is null)
            return !MetadataPresent;
        return string.Equals(stampedArtifactId, ArtifactId, StringComparison.Ordinal);
    }
}
