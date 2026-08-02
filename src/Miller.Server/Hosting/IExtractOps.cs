using Miller.Core.Freshness;
using Miller.Indexing;

namespace Miller.Server.Hosting;

/// <summary>
/// The three <c>extract</c> sub-operations the indexer can perform, abstracted so the indexer's dispatch loop
/// (<see cref="IndexerCore"/>) is unit-testable without spawning <c>julie-extract</c>. The production
/// implementation (<see cref="JulieExtractOps"/>) canonicalizes the supplied path under the workspace root
/// (verified-fact 4) and routes through <see cref="JulieExtractRunner"/>; tests substitute a recorder.
///
/// <para>Each call serializes a single in-flight subprocess (the hosted service holds the lock); the methods
/// here are synchronous because <c>extract</c> is a blocking subprocess invocation.</para>
/// </summary>
public interface IExtractOps
{
    /// <summary>
    /// Re-index a single changed file (julie <c>extract update --file</c>). <paramref name="path"/> is the
    /// affected path as observed by the watcher; the implementation canonicalizes it before calling julie.
    /// </summary>
    ExtractReport Update(string path);

    /// <summary>
    /// Remove a single file's symbols (julie <c>extract delete --file</c>; idempotent). Same canonicalization
    /// contract as <see cref="Update"/> — the path may already be gone, so a lexical canonicalization is used.
    /// </summary>
    ExtractReport Delete(string path);

    /// <summary>
    /// Run a whole-repo <c>extract scan</c> over the canonical root. <paramref name="intent"/> decides both the
    /// julie invocation — <see cref="ScanIntent.IncrementalReconcile"/> is the M3 default hash-delta reconcile,
    /// every other intent is <c>scan --force</c>, a from-scratch rebuild — and, upstream of this call, whether a
    /// retry was allowed to downgrade. The indexer's own overflow/HEAD reconcile path stays on the delta default.
    /// <paramref name="jobs"/> is an explicit extraction-parallelism cap that BEATS
    /// <c>MILLER_EXTRACT_JOBS</c>: it carries a caller's safety response (a post-OOM retry passes <c>1</c>) that a
    /// stale operator override must not undo. Null uses the ambient policy.
    /// </summary>
    ExtractReport Scan(ScanIntent intent = ScanIntent.IncrementalReconcile, int? jobs = null);
}
