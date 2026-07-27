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
public readonly record struct SymbolsArtifactIdentity(long Revision, string? ArtifactId)
{
    /// <summary>Read the current identity of <paramref name="symbolsDbPath"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="symbolsDbPath"/> is null or blank.</exception>
    public static SymbolsArtifactIdentity Read(string symbolsDbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolsDbPath);
        using var freshness = new FreshnessReader(symbolsDbPath);
        return new SymbolsArtifactIdentity(freshness.LatestRevision(), freshness.ArtifactId());
    }

    /// <summary>
    /// Whether a derived sidecar stamped with <paramref name="stampedRevision"/> and
    /// <paramref name="stampedArtifactId"/> was built from this generation.
    /// </summary>
    /// <remarks>
    /// A sidecar written before artifact stamping existed carries a null id and can never be proven current, so
    /// it reports stale and rebuilds exactly once. An artifact whose own id is unreadable (a pre-artifact_id
    /// extract) falls back to revision equality — the historical behaviour — rather than rebuilding forever.
    /// </remarks>
    public bool Matches(long stampedRevision, string? stampedArtifactId)
    {
        if (stampedRevision != Revision)
            return false;
        if (ArtifactId is null)
            return true;
        return string.Equals(stampedArtifactId, ArtifactId, StringComparison.Ordinal);
    }
}
