namespace Miller.Server.Hosting;

/// <summary>
/// The post-apply index convergence seam (m6-design decision-6, impl-order step 9). After <c>edit</c> writes
/// files to disk, the index must converge so the NEXT edit's freshness gate sees the new content. Two paths
/// both converge, abstracted here so the tool is testable with a recorder:
///
/// <list type="bullet">
///   <item><b>This instance is the indexer leader:</b> call <c>extract update --file</c> for each changed file
///   immediately (deterministic reindex + revision bump → this instance's FreshnessService swaps the index).</item>
///   <item><b>This instance is NOT the leader:</b> the file write already emitted a FileSystemWatcher event the
///   leader's M3 watcher reconciles, so this is a no-op — the watcher + freshness poll are the backstop.</item>
/// </list>
///
/// Either way the next edit's freshness gate is the ultimate safety net (decision-6).
/// </summary>
public interface IEditWriteThrough
{
    /// <summary>
    /// Converge the index after <paramref name="changedFiles"/> (absolute paths) were written. The
    /// implementation decides whether to reindex inline (leader) or rely on the watcher (follower). Best-effort:
    /// convergence failure must NOT fail the already-committed edit (the freshness gate is the backstop), so
    /// implementations swallow/log their own errors rather than throw.
    /// </summary>
    void Converge(IReadOnlyList<string> changedFiles);
}
