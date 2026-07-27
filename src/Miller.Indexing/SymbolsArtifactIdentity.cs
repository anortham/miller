using Microsoft.Data.Sqlite;

namespace Miller.Indexing;

/// <summary>How much a reader could learn about an artifact's <c>artifact_metadata</c> table.</summary>
public enum ArtifactStampState
{
    /// <summary>There is no artifact at the given path, so no derived sidecar has a live generation to match.</summary>
    SourceMissing,

    /// <summary>The artifact exists but could not be read, so nothing about its generation is known.</summary>
    Unreadable,

    /// <summary>The artifact was read and provably has no <c>artifact_metadata</c>: a pre-stamping extract.</summary>
    Absent,

    /// <summary>The artifact was read and has an <c>artifact_metadata</c> table.</summary>
    Present,
}

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
    ArtifactStampState StampState)
{
    /// <summary>An identity for an artifact that exists but could not be read.</summary>
    public static SymbolsArtifactIdentity Unprovable(long revision) =>
        new(revision, null, ArtifactStampState.Unreadable);

    /// <summary>Read the current identity of <paramref name="symbolsDbPath"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="symbolsDbPath"/> is null or blank.</exception>
    public static SymbolsArtifactIdentity Read(string symbolsDbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolsDbPath);
        using var freshness = new FreshnessReader(symbolsDbPath);
        return new SymbolsArtifactIdentity(
            freshness.LatestRevision(),
            freshness.ArtifactId(),
            freshness.HasArtifactMetadata() ? ArtifactStampState.Present : ArtifactStampState.Absent);
    }

    /// <summary>
    /// Read the current identity of <paramref name="symbolsDbPath"/>, reporting an unreadable artifact rather
    /// than throwing when it is absent or locked.
    /// </summary>
    /// <remarks>
    /// A read gate must not turn an unreadable artifact into a hard failure. <see cref="ArtifactStampState.Unreadable"/>
    /// means "cannot prove anything", which the comparisons below treat as the historical revision-only behaviour;
    /// turning it into a throw would take a working workspace to broken on a transient lock. An artifact that is
    /// simply GONE is different in kind and not transient — nothing derived from it can be current — so it gets
    /// its own state rather than borrowing the benefit of the doubt.
    /// </remarks>
    public static SymbolsArtifactIdentity TryRead(string symbolsDbPath)
    {
        if (string.IsNullOrWhiteSpace(symbolsDbPath) || !File.Exists(symbolsDbPath))
            return new SymbolsArtifactIdentity(0, null, ArtifactStampState.SourceMissing);

        try
        {
            return Read(symbolsDbPath);
        }
        catch (Exception ex) when (
            ex is SqliteException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return Unprovable(0);
        }
    }

    /// <summary>
    /// Whether a derived sidecar stamped with <paramref name="stampedArtifactId"/> came from this generation,
    /// ignoring revision. Use when the caller has ALREADY checked the sidecar against its own expected revision:
    /// that expectation may legitimately differ from the live artifact's latest revision, so re-checking it here
    /// would reject a sidecar the caller deliberately asked for. The artifact id is the part revision cannot
    /// prove — a promote restarts the counter, so only the id separates two generations at the same revision.
    /// </summary>
    /// <remarks>
    /// A sidecar written before artifact stamping existed carries a null id and can never be proven current, so
    /// it reports stale and rebuilds exactly once.
    ///
    /// An artifact with no id of its own splits four ways. A provably absent <c>artifact_metadata</c> table is a
    /// genuine pre-stamping extract: it falls back to revision equality rather than rebuilding forever, but only
    /// for an equally pre-stamping sidecar — a sidecar that DOES carry a stamp contradicts it, because whatever
    /// stamped it read an artifact that had metadata. A metadata table that exists but carries no
    /// <c>artifact_id</c> is something the pinned extractor never emits, so it is refused rather than trusted. An
    /// unreadable artifact proves nothing either way and keeps serving. A MISSING artifact is not ambiguous at
    /// all: there is no generation for the sidecar to belong to, so it is refused.
    /// </remarks>
    public bool MatchesArtifact(string? stampedArtifactId)
    {
        if (StampState == ArtifactStampState.SourceMissing)
            return false;
        if (ArtifactId is not null)
            return string.Equals(stampedArtifactId, ArtifactId, StringComparison.Ordinal);

        return StampState switch
        {
            ArtifactStampState.Unreadable => true,
            ArtifactStampState.Absent => stampedArtifactId is null,
            _ => false,
        };
    }

    /// <summary>
    /// Whether a derived sidecar stamped with <paramref name="stampedRevision"/> and
    /// <paramref name="stampedArtifactId"/> was built from this generation.
    /// </summary>
    public bool Matches(long stampedRevision, string? stampedArtifactId) =>
        stampedRevision == Revision && MatchesArtifact(stampedArtifactId);
}
