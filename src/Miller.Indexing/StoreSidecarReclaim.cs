using System.Text;
using Miller.Indexing.Store;

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
///
/// <para>"Its sidecars" is the WHOLE per-view set, not just the three active artifacts: the <c>.rebuild</c>
/// shadow, every retained <c>.gen-&lt;tag&gt;</c> generation (each as large as the active artifact, and GC'd only
/// by a live converge service that will never wake for this view again), the content sidecar's preservation
/// marker, and the view's freshness stamp at the store root.</para>
/// </summary>
public static class StoreSidecarReclaim
{
    /// <summary>The reclaim is a courtesy on a removal path, so it waits briefly and then gives up.</summary>
    internal static readonly TimeSpan LeaseTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The suffix of an owed-reclaim record: <c>&lt;view key&gt;.reclaim-owed</c> in the sidecar directory,
    /// holding the raw view id. Written when a reclaim cannot finish, read by the next one.
    /// </summary>
    internal const string OwedRecordSuffix = ".reclaim-owed";

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
    ///
    /// <para>An unfinished reclaim is OWED, never dropped. The registry row that named the view is already gone,
    /// so a busy lease (the converger holds the same one for a whole content+search pass) or a held file would
    /// otherwise strand those files forever with nothing left on disk that names them. The view id is written to
    /// an owed-reclaim record beside the sidecars, and the next reclaim or
    /// <see cref="DischargeOwed"/> on this store finishes the job.</para>
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
        if (!TryResolveSidecarDirectory(target.StoreRoot, out string storeRoot, out string sidecarDirectory))
            return StoreSidecarReclaimResult.None;

        IDisposable? lease = (acquireLease ?? TryAcquireLease)(storeRoot);
        if (lease is null)
        {
            RecordOwed(sidecarDirectory, target.ViewId);
            return new StoreSidecarReclaimResult(0, 0, 0, LeaseBusyReason);
        }

        using (lease)
        {
            // The target's own record is skipped here and settled below, so one view is never reclaimed twice in
            // one pass — a file a reader holds would otherwise be counted as retained by both.
            StoreSidecarReclaimResult owed =
                DischargeOwedUnderLease(registry, storeRoot, sidecarDirectory, skipViewId: target.ViewId);
            StoreSidecarReclaimResult mine = DeleteView(storeRoot, sidecarDirectory, target.ViewId);
            if (mine.FilesRetained > 0)
                RecordOwed(sidecarDirectory, target.ViewId);
            else
                ClearOwed(sidecarDirectory, target.ViewId);
            return StoreSidecarReclaimResult.Combine(mine, owed);
        }
    }

    /// <summary>
    /// Finish every reclaim this store still owes, from records earlier passes left behind. This is the retry
    /// path a busy lease depends on, so <c>workspace prune</c> runs it for every registered family.
    ///
    /// <para>A record whose view a registered workspace claims again (a removed root re-registered at the same
    /// path) is live data: its files are kept and only the record is dropped. The claim check matches on view id
    /// alone, which over-matches across families — and over-matching only ever KEEPS files.</para>
    /// </summary>
    public static StoreSidecarReclaimResult DischargeOwed(
        WorkspaceRegistry registry,
        string storeRoot,
        Func<string, IDisposable?>? acquireLease = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (!TryResolveSidecarDirectory(storeRoot, out string fullRoot, out string sidecarDirectory))
            return StoreSidecarReclaimResult.None;
        if (EnumerateOwedRecords(sidecarDirectory).Count == 0)
            return StoreSidecarReclaimResult.None;

        IDisposable? lease = (acquireLease ?? TryAcquireLease)(fullRoot);
        if (lease is null)
            return new StoreSidecarReclaimResult(0, 0, 0, LeaseBusyReason);

        using (lease)
            return DischargeOwedUnderLease(registry, fullRoot, sidecarDirectory, skipViewId: null);
    }

    private static StoreSidecarReclaimResult DischargeOwedUnderLease(
        WorkspaceRegistry registry,
        string storeRoot,
        string sidecarDirectory,
        string? skipViewId)
    {
        IReadOnlyList<string> records = EnumerateOwedRecords(sidecarDirectory);
        if (records.Count == 0)
            return StoreSidecarReclaimResult.None;

        HashSet<string> claimed = new(
            registry.ListStoreMembers().Select(static member => member.ViewId ?? string.Empty),
            StringComparer.Ordinal);
        var total = StoreSidecarReclaimResult.None;
        foreach (string record in records)
        {
            string? viewId = ReadOwedRecord(record);
            if (viewId is not null && string.Equals(viewId, skipViewId, StringComparison.Ordinal))
                continue;
            if (viewId is null || claimed.Contains(viewId))
            {
                DeleteQuietly(record);
                continue;
            }

            StoreSidecarReclaimResult one = DeleteView(storeRoot, sidecarDirectory, viewId);
            total = StoreSidecarReclaimResult.Combine(total, one);
            if (one.FilesRetained == 0)
                DeleteQuietly(record);
        }

        return total;
    }

    /// <summary>
    /// Delete every Miller-owned file of one view: each sidecar kind's active artifact, its <c>-wal</c>/<c>-shm</c>
    /// siblings, the <c>.rebuild</c> shadow trio, every retained generation, the content sidecar's preservation
    /// marker, and the view's freshness stamp at the store root.
    ///
    /// <para>Retained generations carry a tag nothing off-view records, so they are found by listing — but the
    /// PREFIX listed is built from the captured view id, so the listing only ever learns tags, never which view
    /// is being reclaimed. A generation is the same size as the active artifact and is GC'd only by that
    /// workspace's own live converge service, so leaving it behind strands the largest file of the set.</para>
    /// </summary>
    private static StoreSidecarReclaimResult DeleteView(string storeRoot, string sidecarDirectory, string viewId)
    {
        int deleted = 0;
        int retained = 0;
        long bytes = 0;
        foreach (StoreSidecarKind kind in Enum.GetValues<StoreSidecarKind>())
        {
            string sidecarPath;
            try
            {
                sidecarPath = StoreSidecarCatalog.PathFor(storeRoot, kind, viewId);
            }
            catch (Exception ex) when (
                ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                continue;
            }

            DeleteTrio(sidecarPath, ref deleted, ref bytes, ref retained);
            DeleteTrio(sidecarPath + ".rebuild", ref deleted, ref bytes, ref retained);
            if (kind == StoreSidecarKind.Content)
            {
                Delete(
                    sidecarPath + ContentCorpusWriter.PreservationFailureSuffix,
                    ref deleted, ref bytes, ref retained);
            }

            string generationPrefix = Path.GetFileNameWithoutExtension(sidecarPath) + ".gen-";
            foreach (string generation in EnumerateFiles(sidecarDirectory, generationPrefix + "*"))
                Delete(generation, ref deleted, ref bytes, ref retained);
        }

        try
        {
            Delete(StoreFreshnessStamp.FilePath(storeRoot, viewId), ref deleted, ref bytes, ref retained);
        }
        catch (Exception ex) when (
            ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }

        return new StoreSidecarReclaimResult(deleted, bytes, retained, retained > 0 ? FilesInUseReason : null);
    }

    private static void DeleteTrio(string path, ref int deleted, ref long bytes, ref int retained)
    {
        Delete(path, ref deleted, ref bytes, ref retained);
        Delete(path + "-wal", ref deleted, ref bytes, ref retained);
        Delete(path + "-shm", ref deleted, ref bytes, ref retained);
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

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// The full store root and its sidecar directory, or false when either is absent. The directory is resolved
    /// through <see cref="StoreSidecarCatalog.DirectoryFor"/> so a listing sees the same directory
    /// <see cref="StoreSidecarCatalog.PathFor"/> writes into.
    /// </summary>
    private static bool TryResolveSidecarDirectory(
        string? storeRoot,
        out string fullRoot,
        out string sidecarDirectory)
    {
        fullRoot = string.Empty;
        sidecarDirectory = string.Empty;
        if (string.IsNullOrWhiteSpace(storeRoot))
            return false;
        try
        {
            fullRoot = Path.GetFullPath(storeRoot);
            sidecarDirectory = StoreSidecarCatalog.DirectoryFor(fullRoot);
        }
        catch (Exception ex) when (
            ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }

        return Directory.Exists(sidecarDirectory);
    }

    private static IReadOnlyList<string> EnumerateOwedRecords(string sidecarDirectory) =>
        EnumerateFiles(sidecarDirectory, "*" + OwedRecordSuffix);

    private static IReadOnlyList<string> EnumerateFiles(string directory, string pattern)
    {
        try
        {
            return Directory.GetFiles(directory, pattern);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return [];
        }
    }

    /// <summary>
    /// Remember that this view is still owed a reclaim. The record NAME is the view key and its CONTENT is the
    /// raw view id, so a record whose content does not hash to its own name is unusable and is dropped rather
    /// than acted on — a planted file must never name another view's files.
    /// </summary>
    private static void RecordOwed(string sidecarDirectory, string viewId)
    {
        try
        {
            File.WriteAllText(OwedRecordPath(sidecarDirectory, viewId), viewId, Encoding.UTF8);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }
    }

    private static void ClearOwed(string sidecarDirectory, string viewId)
    {
        try
        {
            DeleteQuietly(OwedRecordPath(sidecarDirectory, viewId));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
        }
    }

    private static string OwedRecordPath(string sidecarDirectory, string viewId) =>
        Path.Combine(sidecarDirectory, StoreSidecarCatalog.ViewKey(viewId) + OwedRecordSuffix);

    private static string? ReadOwedRecord(string path)
    {
        try
        {
            string name = Path.GetFileName(path);
            if (!name.EndsWith(OwedRecordSuffix, StringComparison.Ordinal))
                return null;
            string viewId = File.ReadAllText(path, Encoding.UTF8).Trim();
            if (viewId.Length == 0)
                return null;
            string expected = name[..^OwedRecordSuffix.Length];
            return string.Equals(StoreSidecarCatalog.ViewKey(viewId), expected, StringComparison.Ordinal)
                ? viewId
                : null;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
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

    /// <summary>
    /// The real lease, taken WITHOUT creating the sidecar directory. A reclaim is leaving this store, and a
    /// caller that is leaving must never manufacture the directory it is cleaning out.
    /// </summary>
    private static IDisposable? TryAcquireLease(string storeRoot) =>
        FamilyStoreSidecarWriteLease.TryAcquireExisting(storeRoot, LeaseTimeout);
}
