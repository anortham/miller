using Miller.Indexing;

namespace Miller.Server.Hosting;

/// <summary>
/// The pure poll-then-swap decision behind <see cref="FreshnessService"/> (m3-design decision-2/-5): the
/// testable seam with no SQLite, no timer, and no subprocess. Given the index holder, the latest persisted
/// revision and artifact identity (read by the service from <c>extraction_revisions</c> /
/// <c>artifact_metadata</c>), and a rebuild factory, it rebuilds and atomically swaps the index when the
/// writer has moved ahead of the held index — or when the artifact FILE was replaced by a full rebuild — so
/// a reader instance converges on the leader's writes without churning while the writer is idle.
/// </summary>
public static class FreshnessPoller
{
    /// <summary>
    /// Rebuild and <see cref="IndexHolder.Swap"/> when the writer moved ahead (<paramref name="latestRevision"/>
    /// strictly greater than the holder's built revision) OR the artifact was replaced
    /// (<paramref name="latestArtifactId"/> and the held id are both known and differ — a full rebuild promoted
    /// a fresh file whose RESTARTED revision counter may land at or below the held revision, so the revision
    /// comparison alone would keep serving the pre-rebuild index forever; 2026-06-11 Eros field report #2).
    /// Returns true iff it swapped.
    ///
    /// <para>Strictly-greater (not just unequal) on the revision arm is deliberate: a no-op <c>extract update</c>
    /// does not bump the revision (verified-fact 2), so an unchanged writer leaves this a true no-op — no
    /// rebuild, no swap, no allocation. A null id on either side means "unknown" and never forces a swap.</para>
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="holder"/> or <paramref name="rebuild"/> is null.</exception>
    public static bool PollOnce(
        IndexHolder holder, long latestRevision, string? latestArtifactId, Func<MillerRepositoryIndex> rebuild)
    {
        ArgumentNullException.ThrowIfNull(holder);
        ArgumentNullException.ThrowIfNull(rebuild);

        string? builtArtifactId = holder.BuiltArtifactId;
        bool artifactReplaced = latestArtifactId is not null && builtArtifactId is not null
            && !string.Equals(latestArtifactId, builtArtifactId, StringComparison.Ordinal);
        if (!artifactReplaced && latestRevision <= holder.BuiltRevision)
            return false;

        MillerRepositoryIndex next = rebuild();
        holder.Swap(next, latestRevision, latestArtifactId ?? builtArtifactId);
        return true;
    }

    /// <summary>Revision-only overload (no artifact identity available): swaps only on a strict revision
    /// advance, exactly the historical decision rule.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="holder"/> or <paramref name="rebuild"/> is null.</exception>
    public static bool PollOnce(IndexHolder holder, long latestRevision, Func<MillerRepositoryIndex> rebuild) =>
        PollOnce(holder, latestRevision, latestArtifactId: null, rebuild);
}
