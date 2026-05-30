using Miller.Indexing;

namespace Miller.Server.Hosting;

/// <summary>
/// The three <c>extract</c> sub-operations the indexer can perform, abstracted so the indexer's dispatch loop
/// (<see cref="IndexerCore"/>) is unit-testable without spawning <c>julie-server</c>. The production
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
    /// Run a whole-repo <c>extract scan</c> over the canonical root. With <paramref name="force"/> <c>false</c>
    /// (the M3 default) julie does a hash-delta reconcile — only changed files are re-extracted; with
    /// <paramref name="force"/> <c>true</c> it rebuilds the workspace from scratch (julie <c>scan --force</c>),
    /// the <c>workspace full</c> operation (M7 decision-3). The indexer's own overflow/HEAD reconcile path stays
    /// on the delta default; only an explicit operator <c>full</c> passes <c>force</c>.
    /// </summary>
    ExtractReport Scan(bool force = false);
}
