namespace Miller.Indexing;

/// <summary>
/// The family-store view a leaving workspace owned, captured from its registry rows BEFORE the rows go away.
///
/// <para>A Miller-owned per-view sidecar (<c>search-</c>/<c>content-</c>/<c>vector-&lt;sha&gt;.db</c> under
/// <c>&lt;store root&gt;/sidecars</c>) is named by the view id, and nothing else on disk records which workspace
/// owns it. The <c>store_members</c> row is that record, and <see cref="WorkspaceRegistry.Remove"/> cascades it
/// away with the workspace row — so a removal that reads the view id AFTER the delete reads nothing, and the
/// sidecar files stay behind forever. Capture first, delete the rows, then reclaim.</para>
///
/// <para>The view id must come from this record. Never rediscover a sidecar by listing the sidecar directory or
/// by re-hashing a root: a stale or guessed name deletes another live member's index.</para>
/// </summary>
public sealed record StoreSidecarReclaimTarget(Guid FamilyId, string ViewId, string StoreRoot)
{
    /// <summary>
    /// The store view <paramref name="workspaceId"/> owns, or null when the workspace is not a store member (a
    /// legacy standalone artifact, or a member row that never existed).
    /// </summary>
    public static StoreSidecarReclaimTarget? Capture(WorkspaceRegistry registry, string? workspaceId)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (string.IsNullOrWhiteSpace(workspaceId))
            return null;

        StoreMemberRegistryRow? member = registry.GetStoreMember(workspaceId);
        if (member is null || string.IsNullOrWhiteSpace(member.ViewId))
            return null;

        StoreFamilyRegistryRow? family = registry.GetStoreFamily(member.FamilyId);
        if (family is null || string.IsNullOrWhiteSpace(family.StoreRoot))
            return null;

        return new StoreSidecarReclaimTarget(member.FamilyId, member.ViewId, family.StoreRoot);
    }
}

/// <summary>
/// What a sidecar reclaim actually did. Every field is best-effort: a reclaim never fails the removal that asked
/// for it, so a skip is reported, not thrown.
/// </summary>
/// <param name="FilesDeleted">Sidecar files (and <c>-wal</c>/<c>-shm</c> siblings) deleted.</param>
/// <param name="BytesReclaimed">The sum of those files' lengths, measured before the delete.</param>
/// <param name="FilesRetained">Files that exist but could not be deleted, usually because a reader holds them.</param>
/// <param name="SkipReason">Why nothing (or not everything) was reclaimed, or null when there is nothing to say.</param>
public readonly record struct StoreSidecarReclaimResult(
    int FilesDeleted,
    long BytesReclaimed,
    int FilesRetained,
    string? SkipReason)
{
    /// <summary>Nothing was reclaimed and there is nothing to report — the workspace owned no store view.</summary>
    public static StoreSidecarReclaimResult None => default;

    /// <summary>Whether this result carries anything a caller should show.</summary>
    public bool HasReport => FilesDeleted > 0 || FilesRetained > 0 || SkipReason is not null;

    /// <summary>Sum two results, keeping the first skip reason so a batch reports the reason it hit.</summary>
    public static StoreSidecarReclaimResult Combine(
        StoreSidecarReclaimResult left,
        StoreSidecarReclaimResult right) =>
        new(
            left.FilesDeleted + right.FilesDeleted,
            left.BytesReclaimed + right.BytesReclaimed,
            left.FilesRetained + right.FilesRetained,
            left.SkipReason ?? right.SkipReason);
}

/// <summary>
/// Deletes the Miller-owned per-view sidecars of a workspace that has left the family store.
///
/// <para>julie-extract owns <c>store.db</c>/<c>coord.db</c> and the <c>views</c> row; this reclaim touches
/// neither. It deletes only files Miller created under <c>&lt;store root&gt;/sidecars</c>, and only for a view
/// that no registry row claims any more.</para>
/// </summary>
public static class StoreSidecarReclaim
{
    /// <summary>The reclaim is a courtesy on a removal path, so it waits briefly and then gives up.</summary>
    internal static readonly TimeSpan LeaseTimeout = TimeSpan.FromSeconds(2);

    internal const string SidecarDirectoryName = "sidecars";

    /// <summary>Another process holds the family sidecar write lease; removal proceeded, the files stayed.</summary>
    public const string LeaseBusyReason = "sidecar write lease busy";

    /// <summary>The view still belongs to a registered workspace, so its sidecars are live data.</summary>
    public const string StillAMemberReason = "view is still a store member";

    /// <summary>A reader holds a sidecar file open, so it could not be deleted.</summary>
    public const string FilesInUseReason = "sidecar files in use";

    /// <summary>
    /// Delete <paramref name="target"/>'s sidecars under the family sidecar write lease.
    ///
    /// <para>Call this AFTER the registry rows are gone: the reclaim re-reads <c>store_members</c> and refuses
    /// any view a live member still claims. A null target, an absent store root, an absent sidecar directory, and
    /// an absent file are all ordinary outcomes, not failures.</para>
    /// </summary>
    /// <param name="acquireLease">
    /// Test seam for the family sidecar write lease. It returns null when the lease is unavailable.
    /// </param>
    public static StoreSidecarReclaimResult Reclaim(
        WorkspaceRegistry registry,
        StoreSidecarReclaimTarget? target,
        Func<string, IDisposable?>? acquireLease = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (target is null)
            return StoreSidecarReclaimResult.None;
        if (IsStillAMember(registry, target))
            return new StoreSidecarReclaimResult(0, 0, 0, StillAMemberReason);

        string storeRoot;
        try
        {
            storeRoot = Path.GetFullPath(target.StoreRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return StoreSidecarReclaimResult.None;
        }

        if (!Directory.Exists(Path.Combine(storeRoot, SidecarDirectoryName)))
            return StoreSidecarReclaimResult.None;

        IDisposable? lease = (acquireLease ?? TryAcquireLease)(storeRoot);
        if (lease is null)
            return new StoreSidecarReclaimResult(0, 0, 0, LeaseBusyReason);

        using (lease)
        {
            int deleted = 0;
            int retained = 0;
            long bytes = 0;
            foreach (StoreSidecarKind kind in Enum.GetValues<StoreSidecarKind>())
            {
                string sidecarPath;
                try
                {
                    sidecarPath = StoreSidecarCatalog.PathFor(storeRoot, kind, target.ViewId);
                }
                catch (Exception ex) when (
                    ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    continue;
                }

                Delete(sidecarPath, ref deleted, ref bytes, ref retained);
                Delete(sidecarPath + "-wal", ref deleted, ref bytes, ref retained);
                Delete(sidecarPath + "-shm", ref deleted, ref bytes, ref retained);
            }

            return new StoreSidecarReclaimResult(
                deleted,
                bytes,
                retained,
                retained > 0 ? FilesInUseReason : null);
        }
    }

    private static void Delete(string path, ref int deleted, ref long bytes, ref int retained)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
                return;
            long length = file.Length;
            File.Delete(path);
            deleted++;
            bytes += length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            retained++;
        }
    }

    private static bool IsStillAMember(WorkspaceRegistry registry, StoreSidecarReclaimTarget target)
    {
        foreach (StoreMemberRegistryRow member in registry.ListStoreMembers())
        {
            if (member.FamilyId == target.FamilyId &&
                string.Equals(member.ViewId, target.ViewId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static IDisposable? TryAcquireLease(string storeRoot)
    {
        try
        {
            return FamilyStoreSidecarWriteLease.AcquireFor(storeRoot, LeaseTimeout);
        }
        catch (Exception ex) when (
            ex is TimeoutException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }
}
