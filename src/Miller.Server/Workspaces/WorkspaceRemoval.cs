using Miller.Indexing;
using Miller.Server.Tools;

namespace Miller.Server.Workspaces;

/// <summary>
/// The shared removal core behind the CLI <c>workspace remove</c> verb and the dashboard's
/// <c>POST /workspace/remove</c> endpoint: resolve the registration, delete its <c>.miller</c> index dir
/// under all three workspace write leases, and unregister the row — or refuse honestly. Extracted from
/// <c>CliDispatch.WorkspaceRemove</c>/<c>RemoveMillerDir</c> so both callers share one behavior:
/// <list type="bullet">
/// <item>selector resolution via <see cref="WorkspaceRegistrySelector"/> (<see cref="RemoveById"/> throws
/// <see cref="KeyNotFoundException"/> on no match — each caller renders its own usage/not-found surface);</item>
/// <item>the gone-root best-effort prune (R4) for <see cref="RemoveByPath"/> — a deleted repo can still be
/// unregistered even though its path no longer canonicalizes;</item>
/// <item>the live-root refusal (<see cref="WorkspaceSafety.IsLiveWorkspace"/>), applied only when the caller
/// supplies a non-null <paramref name="liveRoot"/> — the one-shot CLI and the dashboard serve no workspace
/// in-process and pass <c>null</c>;</item>
/// <item>the unconditional in-use refusal: the delete happens while HOLDING the indexer, content, and history
/// write leases (<see cref="WorkspaceWriteLeases.TryAcquireForRemove"/>), so no Miller process — including a
/// CLI content import or history append that holds a sidecar lock WITHOUT the indexer lock — can be writing
/// this index mid-delete. Any lease unavailable ⇒ refuse, delete nothing.</item>
/// </list>
/// </summary>
public static class WorkspaceRemoval
{
    /// <summary>
    /// Remove by registry selector (display id, unique prefix, full id, or registered root path).
    /// </summary>
    /// <exception cref="KeyNotFoundException">No registry row matched <paramref name="selector"/>.</exception>
    public static WorkspaceRemoveResult RemoveById(WorkspaceRegistry registry, string selector, string? liveRoot)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);

        WorkspaceRegistryRow row = WorkspaceRegistrySelector.Resolve(registry, selector);
        string millerDir = Path.GetDirectoryName(row.IndexDbPath)
            ?? throw new InvalidOperationException(
                $"Cannot determine the .miller directory for index DB path '{row.IndexDbPath}'.");
        return Remove(registry, row.WorkspaceId, row.CanonicalRoot, millerDir, liveRoot);
    }

    /// <summary>Remove by workspace root path (the dir that CONTAINS the <c>.miller</c> index dir).</summary>
    public static WorkspaceRemoveResult RemoveByPath(WorkspaceRegistry registry, string path, string? liveRoot)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // A GONE dir cannot be canonicalized, so best-effort prune a registry row whose canonical root
        // lexically matches the full path (R4 — lets a CI teardown clean the registry after deleting the repo).
        string fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
        {
            string goneMillerDir = Path.Combine(fullPath, ".miller");
            WorkspaceRegistryRow? stale =
                WorkspaceRegistryRootMatcher.FindByPossiblyMissingPath(registry.List(), fullPath);
            if (stale is null)
                return WorkspaceRemoveResult.NotFound(goneMillerDir);
            registry.Remove(stale.WorkspaceId);
            return WorkspaceRemoveResult.Removed(
                goneMillerDir, stale.WorkspaceId, stale.CanonicalRoot, indexDirDeleted: false);
        }

        // Existing dir: canonicalize and match a registry row (ordinal canonical root, like the server's
        // FindByCanonicalRoot), falling back to a local .miller cleanup when no row is registered.
        string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(fullPath);
        WorkspaceRegistryRow? match = WorkspaceRegistryRootMatcher.FindByRoot(registry.List(), canonicalRoot);
        string millerDir = match is { } m
            ? Path.GetDirectoryName(m.IndexDbPath) ?? Path.Combine(canonicalRoot, ".miller")
            : Path.Combine(canonicalRoot, ".miller");
        return Remove(registry, match?.WorkspaceId, match?.CanonicalRoot ?? canonicalRoot, millerDir, liveRoot);
    }

    // Delete one `.miller` dir under the cross-process writer lock. Live root ⇒ refused (only when the caller
    // serves one); missing dir ⇒ a clean not-found (prune any stale row); lock held by another writer ⇒ refused,
    // NOT deleted; otherwise delete + unregister.
    private static WorkspaceRemoveResult Remove(
        WorkspaceRegistry registry, string? workspaceId, string? root, string millerDir, string? liveRoot)
    {
        if (liveRoot is not null && root is not null && WorkspaceSafety.IsLiveWorkspace(root, liveRoot))
            return WorkspaceRemoveResult.RefusedLive(millerDir, workspaceId, root);

        if (!Directory.Exists(millerDir))
        {
            if (workspaceId is null)
                return WorkspaceRemoveResult.NotFound(millerDir, workspaceId, root);
            registry.Remove(workspaceId);
            return WorkspaceRemoveResult.Removed(millerDir, workspaceId, root, indexDirDeleted: false);
        }

        // Delete the index data while HOLDING all three workspace-local write leases (indexer → content →
        // history), so no Miller process can start writing this index — nor a CLI content import / history append,
        // which hold content.lock / history.lock WITHOUT the indexer lock — mid-delete. Any lease unavailable ⇒
        // refuse, delete nothing. Only the held lock files are skipped (an open FileShare.None handle cannot be
        // deleted on Windows); after release, the leftover lock files + empty dir are removed best-effort — a
        // writer that sneaks in after release finds an already-empty index and does a clean rebuild.
        using (WorkspaceWriteLeases? leases =
            WorkspaceWriteLeases.TryAcquireForRemove(millerDir, SingleWriterLock.TryAcquire))
        {
            if (leases is null)
                return WorkspaceRemoveResult.RefusedInUse(millerDir, workspaceId, root);
            SingleWriterLock.DeleteContentsExceptLock(millerDir, WorkspaceWriteLeases.SidecarLockFileNames);
        }

        SingleWriterLock.TryDeleteEmptiedDir(millerDir);
        if (workspaceId is not null)
            registry.Remove(workspaceId);
        return WorkspaceRemoveResult.Removed(millerDir, workspaceId, root);
    }
}
