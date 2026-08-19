using Miller.Indexing;
using Miller.Server.Tools;

namespace Miller.Server.Workspaces;

/// <summary>
/// Removes registered workspace data under all workspace write leases.
/// Live, sensitive, machine-global, corrupt-path, unregistered, and in-use targets are never deleted.
/// </summary>
public static class WorkspaceRemoval
{
    /// <summary>
    /// Remove by registry selector (display id, unique prefix, full id, or registered root path).
    /// </summary>
    /// <exception cref="KeyNotFoundException">No registry row matched <paramref name="selector"/>.</exception>
    public static WorkspaceRemoveResult RemoveById(
        WorkspaceRegistry registry,
        string selector,
        string? liveRoot,
        string? protectedMillerDir = null,
        Func<string, IDisposable?>? acquireWriterLock = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);

        WorkspaceRegistryRow row = WorkspaceRegistrySelector.Resolve(registry, selector);
        if (!TryRegisteredMillerDir(row, out string millerDir))
        {
            return WorkspaceRemoveResult.RefusedInvalidRegistration(
                millerDir,
                row.WorkspaceId,
                row.CanonicalRoot);
        }
        return Remove(
            registry,
            row.WorkspaceId,
            row.CanonicalRoot,
            millerDir,
            liveRoot,
            protectedMillerDir,
            acquireWriterLock);
    }

    /// <summary>Remove by workspace root path (the dir that CONTAINS the <c>.miller</c> index dir).</summary>
    public static WorkspaceRemoveResult RemoveByPath(
        WorkspaceRegistry registry,
        string path,
        string? liveRoot,
        string? protectedMillerDir = null,
        Func<string, IDisposable?>? acquireWriterLock = null)
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

        string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(fullPath);
        WorkspaceRegistryRow? match = WorkspaceRegistryRootMatcher.FindByRoot(registry.List(), canonicalRoot);
        if (match is null)
            return WorkspaceRemoveResult.NotFound(Path.Combine(canonicalRoot, ".miller"), root: canonicalRoot);
        if (liveRoot is not null && WorkspaceSafety.IsLiveWorkspace(match.CanonicalRoot, liveRoot))
        {
            return WorkspaceRemoveResult.RefusedLive(
                Path.Combine(match.CanonicalRoot, ".miller"),
                match.WorkspaceId,
                match.CanonicalRoot);
        }

        if (!TryRegisteredMillerDir(match, out string registeredMillerDir))
        {
            return WorkspaceRemoveResult.RefusedInvalidRegistration(
                registeredMillerDir,
                match.WorkspaceId,
                match.CanonicalRoot);
        }

        return Remove(
            registry,
            match.WorkspaceId,
            match.CanonicalRoot,
            registeredMillerDir,
            liveRoot,
            protectedMillerDir,
            acquireWriterLock);
    }

    private static WorkspaceRemoveResult Remove(
        WorkspaceRegistry registry,
        string? workspaceId,
        string? root,
        string millerDir,
        string? liveRoot,
        string? protectedMillerDir,
        Func<string, IDisposable?>? acquireWriterLock)
    {
        if (liveRoot is not null && root is not null && WorkspaceSafety.IsLiveWorkspace(root, liveRoot))
            return WorkspaceRemoveResult.RefusedLive(millerDir, workspaceId, root);
        if (root is not null &&
            WorkspaceRootSafety.IsSensitiveRoot(root, WorkspaceRootSafety.SensitiveRootCandidates()))
        {
            return WorkspaceRemoveResult.RefusedSensitive(millerDir, workspaceId, root);
        }
        if (!string.IsNullOrWhiteSpace(protectedMillerDir) &&
            SamePath(millerDir, protectedMillerDir))
        {
            return WorkspaceRemoveResult.RefusedSensitive(millerDir, workspaceId, root);
        }

        if (!Directory.Exists(millerDir))
        {
            if (workspaceId is null)
                return WorkspaceRemoveResult.NotFound(millerDir, workspaceId, root);
            registry.Remove(workspaceId);
            return WorkspaceRemoveResult.Removed(millerDir, workspaceId, root, indexDirDeleted: false);
        }

        // Delete the index data while HOLDING all four workspace-local write leases (indexer → content →
        // history → ct.lock), so no Miller process can start writing this index — nor a CLI content import,
        // history append, or CT store write, which hold those sidecar locks WITHOUT the indexer lock — mid-delete.
        // Any lease unavailable ⇒ refuse, delete nothing. Only the held lock files are skipped (an open
        // FileShare.None handle cannot be deleted on Windows); after release, the leftover lock files + empty dir
        // are removed best-effort — a writer that sneaks in after release finds an already-empty index and does a
        // clean rebuild.
        using (WorkspaceWriteLeases? leases =
            WorkspaceWriteLeases.TryAcquireForRemove(
                millerDir,
                acquireWriterLock ?? SingleWriterLock.TryAcquire))
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

    internal static bool TryRegisteredMillerDir(WorkspaceRegistryRow row, out string millerDir)
    {
        ArgumentNullException.ThrowIfNull(row);
        millerDir = row.CanonicalRoot;
        try
        {
            millerDir = Path.Combine(row.CanonicalRoot, ".miller");
            string expectedDb = Path.GetFullPath(Path.Combine(millerDir, "symbols.db"));
            string actualDb = Path.GetFullPath(row.IndexDbPath);
            if (!SamePath(actualDb, expectedDb))
                return false;

            millerDir = Path.GetDirectoryName(expectedDb) ?? millerDir;
            if (Directory.Exists(row.CanonicalRoot) &&
                !SamePath(PathCanonicalizer.CanonicalizeRoot(row.CanonicalRoot), row.CanonicalRoot))
            {
                return false;
            }
            if (Directory.Exists(millerDir) &&
                !SamePath(PathCanonicalizer.CanonicalizeRoot(millerDir), millerDir))
            {
                return false;
            }
            return true;
        }
        catch (Exception ex) when (
            ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
}
