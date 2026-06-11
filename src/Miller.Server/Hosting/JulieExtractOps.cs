using Miller.Indexing;

namespace Miller.Server.Hosting;

/// <summary>
/// The production <see cref="IExtractOps"/>: canonicalizes the watcher-supplied path under the workspace root
/// (verified-fact 4 — symlink-resolved for BOTH root and file so julie's inside-root check passes) and routes
/// each operation through <see cref="JulieExtractRunner"/> against the Miller-owned extract DB.
///
/// <para>The root is canonicalized ONCE at construction; per-file paths are canonicalized on each call via
/// <see cref="PathCanonicalizer.CanonicalizeFile"/> (which tolerates a just-deleted tail). The runner methods
/// are reached through injected delegates so the canonicalization contract is unit-testable without spawning a
/// process (see <see cref="CreateForTest"/>); the production factory binds them to a real runner.</para>
/// </summary>
public sealed class JulieExtractOps : IExtractOps
{
    private readonly string _canonicalRoot;
    private readonly string _db;
    private readonly Func<string, string, string, ExtractReport> _update; // (root, db, file)
    private readonly Func<string, string, string, ExtractReport> _delete; // (root, db, file)
    private readonly Func<string, string, bool, ExtractReport> _scan;     // (root, db, force)

    private JulieExtractOps(
        string canonicalRoot,
        string db,
        Func<string, string, string, ExtractReport> update,
        Func<string, string, string, ExtractReport> delete,
        Func<string, string, bool, ExtractReport> scan)
    {
        _canonicalRoot = canonicalRoot;
        _db = db;
        _update = update;
        _delete = delete;
        _scan = scan;
    }

    /// <summary>
    /// The production factory: bind the ops to a real <see cref="JulieExtractRunner"/>. <paramref name="canonicalRoot"/>
    /// must already be canonical (the bootstrap resolves it once); <paramref name="db"/> is the extract DB path.
    /// </summary>
    /// <exception cref="ArgumentNullException">Any required argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="canonicalRoot"/> or <paramref name="db"/> is empty.</exception>
    public static JulieExtractOps Create(string canonicalRoot, string db, JulieExtractRunner runner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(db);
        ArgumentNullException.ThrowIfNull(runner);
        return new JulieExtractOps(
            canonicalRoot, db,
            update: runner.Update,
            delete: runner.Delete,
            scan: (root, dbPath, force) => runner.Scan(root, dbPath, force));
    }

    /// <summary>
    /// Test seam: bind the runner methods to recording delegates so the canonical <c>(root, db, file)</c> that
    /// would reach julie is asserted without spawning the binary. Not used in production.
    /// </summary>
    public static JulieExtractOps CreateForTest(
        string canonicalRoot,
        string db,
        Func<string, string, string, ExtractReport> update,
        Func<string, string, string, ExtractReport> delete,
        Func<string, string, bool, ExtractReport> scan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(db);
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(delete);
        ArgumentNullException.ThrowIfNull(scan);
        return new JulieExtractOps(canonicalRoot, db, update, delete, scan);
    }

    /// <inheritdoc/>
    public ExtractReport Update(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string canonicalFile = PathCanonicalizer.CanonicalizeFile(_canonicalRoot, path);
        return _update(_canonicalRoot, _db, canonicalFile);
    }

    /// <inheritdoc/>
    public ExtractReport Delete(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        // The deleted file no longer exists, so julie can only LEXICALLY normalize --file while it canonicalizes
        // --root. On Windows that drops the \\?\ verbatim prefix julie's own canonicalize adds to the root, so a
        // clean file path trips julie's outside-root check (file_outside_root) and the delete is silently dropped
        // until a full scan. Re-apply the prefix (no-op on POSIX / for an already-verbatim path) so the file is
        // seen as inside the root. update() does NOT need this — its file still exists, so julie canonicalizes it.
        string canonicalFile = PathCanonicalizer.AddWindowsVerbatimPrefix(
            PathCanonicalizer.CanonicalizeFile(_canonicalRoot, path));
        return _delete(_canonicalRoot, _db, canonicalFile);
    }

    /// <inheritdoc/>
    public ExtractReport Scan(bool force = false) => _scan(_canonicalRoot, _db, force);
}
