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
    /// A directory listing failed, so whether a retained generation is still on disk is UNKNOWN. An unreadable
    /// directory is not an empty one: reading it as empty cleared the owed record while the files remained.
    /// </summary>
    public const string ListingFailedReason = "sidecar listing failed";

    public const string CursorReleaseFailedReason = "sidecar cursor release failed";

    public const string CursorJournalInvalidReason = "sidecar cursor journal invalid";

    public const string IntentNotRecordedReason = "sidecar reclaim intent could not be persisted";

    /// <summary>
    /// Appended to a skip reason when the owed-reclaim record itself could not be written. The files stay AND
    /// nothing on disk names them any more, which is a worse fact than the skip reason alone.
    /// </summary>
    public const string NotRecordedSuffix = "; reclaim not recorded";

    /// <summary>
    /// Write <paramref name="target"/>'s owed-reclaim record BEFORE the registry rows are deleted.
    ///
    /// <para>The <c>store_members</c> row is the ONLY thing that maps a workspace to its view id, and
    /// <see cref="WorkspaceRegistry.Remove"/> cascades it away. A crash after that delete and before
    /// <see cref="Reclaim"/> finishes therefore used to lose the mapping forever: the sidecar files stayed on
    /// disk with nothing left anywhere that named them. Writing the intent first makes the record the durable
    /// hand-off — <see cref="Reclaim"/> clears it only after the delete pass actually completes, and
    /// <see cref="DischargeOwed"/> finishes the job after any crash.</para>
    ///
    /// <para>Call this before producer retirement or registry deletion. Either operation can make an unrecorded
    /// cursor identity impossible to recover after a crash. A claimed view keeps the record; known callers may
    /// retry removal, while an ambiguous producer reply cannot erase the only durable cleanup intent.</para>
    ///
    /// <para>Nothing is created here. An absent store root or an absent sidecar directory means there is nothing
    /// to reclaim, so there is nothing to record — a caller that is LEAVING a store must never manufacture the
    /// directory it is cleaning out.</para>
    ///
    /// <para>A concurrent <see cref="DischargeOwed"/> that sees the view still claimed retains both the record and
    /// files, so it cannot reopen the crash window before registry deletion.</para>
    /// </summary>
    /// <returns>
    /// False only when a record was needed and the durable write failed. Callers must not retire the producer
    /// view or delete the registry mapping until the intent is durable.
    /// </returns>
    public static bool RecordIntent(StoreSidecarReclaimTarget? target)
    {
        if (target is null)
            return true;
        if (!TryResolveSidecarDirectory(target.StoreRoot, out _, out string sidecarDirectory))
            return true;
        return RecordOwed(sidecarDirectory, target.ViewId);
    }

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
    /// an owed-reclaim record beside the sidecars — by <see cref="RecordIntent"/> before the rows go, and again
    /// here whenever this pass leaves anything behind — and the next reclaim or <see cref="DischargeOwed"/> on
    /// this store finishes the job.</para>
    /// </summary>
    /// <param name="acquireLease">
    /// Test seam for the family sidecar write lease. It returns null when the lease is unavailable.
    /// </param>
    public static StoreSidecarReclaimResult Reclaim(
        WorkspaceRegistry registry,
        StoreSidecarReclaimTarget? target,
        Func<string, IDisposable?>? acquireLease = null) =>
        Reclaim(registry, target, acquireLease, listFiles: null);

    /// <param name="listFiles">
    /// Test seam for a directory listing. It returns null when the listing FAILED, which is a different fact
    /// from an empty directory and must never be read as one.
    /// </param>
    internal static StoreSidecarReclaimResult Reclaim(
        WorkspaceRegistry registry,
        StoreSidecarReclaimTarget? target,
        Func<string, IDisposable?>? acquireLease,
        FileLister? listFiles,
        CursorReleaser? releaseCursor = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (target is null)
            return StoreSidecarReclaimResult.None;
        if (!TryResolveSidecarDirectory(target.StoreRoot, out string storeRoot, out string sidecarDirectory))
            return StoreSidecarReclaimResult.None;

        // A cheap pre-lease refusal: a view a live member claims is live data, and there is no point waiting on
        // the lease to learn that. It is NOT the decisive check — see the re-check under the lease below.
        if (IsStillAMember(registry, target))
            return StillAMember(sidecarDirectory, target.ViewId);

        IDisposable? lease = (acquireLease ?? TryAcquireLease)(storeRoot);
        if (lease is null)
            return new StoreSidecarReclaimResult(0, 0, 0, Owe(sidecarDirectory, target.ViewId, LeaseBusyReason));

        using (lease)
        {
            // The membership check above ran BEFORE a lease wait that lasts up to LeaseTimeout, so it is stale by
            // the time the files are actually deleted: `workspace remove` followed by `workspace open` on the same
            // root re-registers the workspace, and the store family resolver reconciles it onto the SAME view id
            // out of the store catalog. Re-check while holding the lease, immediately before deleting, so the
            // decision is made on the freshest registry the delete can see.
            //
            // Residual window: the registration path (StoreFamilyResolver.ResolveOrCreate) writes its
            // store_members row WITHOUT this lease, so a row that lands between this read and the delete is still
            // missed. That window is the width of one registry read plus the delete loop rather than a two-second
            // lease wait, and it is the same window every registry-read-then-act path in Miller has.
            if (IsStillAMember(registry, target))
                return StillAMember(sidecarDirectory, target.ViewId);

            // The target's own record is skipped here and settled below, so one view is never reclaimed twice in
            // one pass — a file a reader holds would otherwise be counted as retained by both.
            StoreSidecarReclaimResult owed = DischargeOwedUnderLease(
                registry, storeRoot, sidecarDirectory, skipViewId: target.ViewId, listFiles, releaseCursor);
            StoreSidecarReclaimResult mine = DeleteView(
                storeRoot, sidecarDirectory, target, listFiles, releaseCursor);
            if (mine.SkipReason is { } reason)
                mine = mine with { SkipReason = Owe(sidecarDirectory, target.ViewId, reason) };
            else
                ClearOwed(sidecarDirectory, target.ViewId);
            return StoreSidecarReclaimResult.Combine(mine, owed);
        }
    }

    /// <summary>
    /// The view is claimed, so its files and durable removal intent are retained. A crash after producer
    /// retirement must not erase the intent before registry deletion.
    /// </summary>
    private static StoreSidecarReclaimResult StillAMember(string sidecarDirectory, string viewId)
    {
        return new StoreSidecarReclaimResult(0, 0, 0, StillAMemberReason);
    }

    /// <summary>Write the owed record for an unfinished reclaim, and say so in the reason when that fails.</summary>
    private static string Owe(string sidecarDirectory, string viewId, string reason) =>
        RecordOwed(sidecarDirectory, viewId) ? reason : reason + NotRecordedSuffix;

    /// <summary>
    /// Finish every reclaim this store still owes, from records earlier passes left behind. This is the retry
    /// path a busy lease depends on, so <c>workspace prune</c> runs it for every registered family.
    ///
    /// <para>A record whose view a registered workspace claims is retained with its live files because the
    /// process may have crashed after producer retirement but before registry deletion. The claim check is
    /// scoped to THIS store — see <see cref="ClaimedViewIds"/>.</para>
    /// </summary>
    public static StoreSidecarReclaimResult DischargeOwed(
        WorkspaceRegistry registry,
        string storeRoot,
        Func<string, IDisposable?>? acquireLease = null) =>
        DischargeOwed(registry, storeRoot, acquireLease, listFiles: null);

    /// <param name="listFiles">Test seam; null return means the listing FAILED, not that the directory is empty.</param>
    internal static StoreSidecarReclaimResult DischargeOwed(
        WorkspaceRegistry registry,
        string storeRoot,
        Func<string, IDisposable?>? acquireLease,
        FileLister? listFiles)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (!TryResolveSidecarDirectory(storeRoot, out string fullRoot, out string sidecarDirectory))
            return StoreSidecarReclaimResult.None;
        if (EnumerateOwedRecords(sidecarDirectory, listFiles).Count == 0)
            return StoreSidecarReclaimResult.None;

        IDisposable? lease = (acquireLease ?? TryAcquireLease)(fullRoot);
        if (lease is null)
            return new StoreSidecarReclaimResult(0, 0, 0, LeaseBusyReason);

        using (lease)
            return DischargeOwedUnderLease(
                registry, fullRoot, sidecarDirectory, skipViewId: null, listFiles, releaseCursor: null);
    }

    private static StoreSidecarReclaimResult DischargeOwedUnderLease(
        WorkspaceRegistry registry,
        string storeRoot,
        string sidecarDirectory,
        string? skipViewId,
        FileLister? listFiles,
        CursorReleaser? releaseCursor)
    {
        IReadOnlyList<string> records = EnumerateOwedRecords(sidecarDirectory, listFiles);
        if (records.Count == 0)
            return StoreSidecarReclaimResult.None;

        HashSet<string> claimed = ClaimedViewIds(registry, sidecarDirectory);
        var total = StoreSidecarReclaimResult.None;
        foreach (string record in records)
        {
            string? viewId = ReadOwedRecord(record);
            if (viewId is not null && string.Equals(viewId, skipViewId, StringComparison.Ordinal))
                continue;
            if (viewId is null)
            {
                DeleteQuietly(record);
                continue;
            }
            if (claimed.Contains(viewId))
                continue;

            StoreSidecarReclaimResult one = DeleteView(
                storeRoot,
                sidecarDirectory,
                new StoreSidecarReclaimTarget(Guid.Empty, viewId, storeRoot),
                listFiles,
                releaseCursor);
            total = StoreSidecarReclaimResult.Combine(total, one);
            // The record survives ANY incomplete pass, held files and a failed listing alike. Dropping it on an
            // unreadable directory clears the last name those files have while the files are still there.
            if (one.SkipReason is null)
                DeleteQuietly(record);
        }

        return total;
    }

    /// <summary>
    /// The view ids a live member claims IN THE STORE that owns <paramref name="sidecarDirectory"/>.
    ///
    /// <para>A view id is unique per family, not globally (<c>UNIQUE(family_id, view_id)</c>), so matching on the
    /// view id alone lets another family's member spare this store's record — which KEEPS the files but drops the
    /// record that names them, stranding them with nothing left to find them by. Real view ids are minted UUIDs,
    /// so that collision is vanishingly unlikely; the scope is here because it is cheap and because it makes the
    /// pre-removal intent record unambiguous.</para>
    ///
    /// <para>A member is claimed unless its family PROVABLY belongs to a different store. A family row that is
    /// missing, or a store root that cannot be resolved, keeps the member in the set: the safe error is to keep
    /// files, never to delete them. (A missing family row cannot strand a member anyway — <c>store_members</c>
    /// cascades on the family delete.)</para>
    /// </summary>
    private static HashSet<string> ClaimedViewIds(WorkspaceRegistry registry, string sidecarDirectory)
    {
        var familyDirectories = new Dictionary<Guid, string?>();
        foreach (StoreFamilyRegistryRow family in registry.ListStoreFamilies())
        {
            familyDirectories[family.FamilyId] =
                TryResolveSidecarDirectory(family.StoreRoot, out _, out string directory) ? directory : null;
        }

        var claimed = new HashSet<string>(StringComparer.Ordinal);
        foreach (StoreMemberRegistryRow member in registry.ListStoreMembers())
        {
            if (string.IsNullOrWhiteSpace(member.ViewId))
                continue;
            if (familyDirectories.TryGetValue(member.FamilyId, out string? directory) &&
                directory is not null &&
                !SameDirectory(directory, sidecarDirectory))
            {
                continue; // another store's view that happens to share this id
            }

            claimed.Add(member.ViewId);
        }

        return claimed;
    }

    private static bool SameDirectory(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(left),
            Path.TrimEndingDirectorySeparator(right),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    /// <summary>
    /// Delete every Miller-owned file of one view: each sidecar kind's active artifact, its <c>-wal</c>/<c>-shm</c>
    /// siblings, the <c>.rebuild</c> shadow trio, every retained generation, the content sidecar's preservation
    /// marker, and the view's freshness stamp at the store root.
    ///
    /// <para>Retained generations carry a tag nothing off-view records, so they are found by listing — but the
    /// PREFIX listed is built from the captured view id, so the listing only ever learns tags, never which view
    /// is being reclaimed. A generation is the same size as the active artifact and is GC'd only by that
    /// workspace's own live converge service, so leaving it behind strands the largest file of the set.</para>
    ///
    /// <para>A generation listing that FAILS is not an empty directory. A transient sharing violation or an ACL
    /// denial on the sidecar directory used to read as "this view has no retained generations", which reported a
    /// complete reclaim and cleared the owed record while the largest files of the set were still on disk. A
    /// failed listing is reported as <see cref="ListingFailedReason"/>, which keeps the record owed.</para>
    /// </summary>
    private static StoreSidecarReclaimResult DeleteView(
        string storeRoot,
        string sidecarDirectory,
        StoreSidecarReclaimTarget target,
        FileLister? listFiles,
        CursorReleaser? releaseCursor)
    {
        string viewId = target.ViewId;
        StoreSidecarReclaimResult cursorRelease = ReleaseCursors(storeRoot, target, releaseCursor);
        if (cursorRelease.SkipReason is not null)
            return cursorRelease;

        int deleted = 0;
        int retained = 0;
        long bytes = 0;
        bool listingFailed = false;
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
            IReadOnlyList<string>? generations =
                ListFiles(sidecarDirectory, generationPrefix + "*", listFiles);
            if (generations is null)
            {
                listingFailed = true;
            }
            else
            {
                foreach (string generation in generations)
                    Delete(generation, ref deleted, ref bytes, ref retained);
            }
        }

        try
        {
            Delete(StoreFreshnessStamp.FilePath(storeRoot, viewId), ref deleted, ref bytes, ref retained);
        }
        catch (Exception ex) when (
            ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }

        Delete(StoreSidecarCursorJournal.PathFor(storeRoot, viewId), ref deleted, ref bytes, ref retained);

        // A failed listing outranks a held file: it is the reason the caller cannot know what is left.
        // FilesRetained stays a count of files PROVED undeletable, so the unknown never inflates it.
        string? reason = listingFailed ? ListingFailedReason : retained > 0 ? FilesInUseReason : null;
        return new StoreSidecarReclaimResult(deleted, bytes, retained, reason);
    }

    private static StoreSidecarReclaimResult ReleaseCursors(
        string storeRoot,
        StoreSidecarReclaimTarget target,
        CursorReleaser? releaseCursor)
    {
        string journalPath = StoreSidecarCursorJournal.PathFor(storeRoot, target.ViewId);
        if (!File.Exists(journalPath))
            return StoreSidecarReclaimResult.None;

        StoreSidecarCursorState state;
        try
        {
            state = StoreSidecarCursorJournal.ReadForReclaim(storeRoot, target.ViewId);
            if (target.FamilyId != Guid.Empty &&
                (!Guid.TryParse(state.FamilyId, out Guid journalFamily) || journalFamily != target.FamilyId))
            {
                return new(0, 0, 1, CursorJournalInvalidReason);
            }
        }
        catch (StoreSidecarCursorStateException)
        {
            return new(0, 0, 1, CursorJournalInvalidReason);
        }

        CursorReleaser operation = releaseCursor ?? ReleaseCursor;
        foreach (StoreSidecarCursorEntry entry in state.Entries)
        {
            StoreConsumerCursorOutcome outcome;
            try
            {
                outcome = operation(
                    storeRoot,
                    state.FamilyId,
                    new StoreSidecarCursorKey(
                        state.FamilyId,
                        entry.StoreInstanceId,
                        state.ViewId,
                        entry.Kind,
                        entry.GenerationName,
                        entry.ConsumerId));
            }
            catch (Exception error) when (
                error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                return new(0, 0, 1, CursorReleaseFailedReason);
            }
            if (!outcome.Succeeded || outcome.ConsumerId != entry.ConsumerId)
                return new(0, 0, 1, CursorReleaseFailedReason);
        }
        return StoreSidecarReclaimResult.None;
    }

    internal delegate StoreConsumerCursorOutcome CursorReleaser(
        string storeRoot,
        string familyId,
        StoreSidecarCursorKey cursor);

    private static StoreConsumerCursorOutcome ReleaseCursor(
        string storeRoot,
        string familyId,
        StoreSidecarCursorKey cursor)
    {
        string binaryPath = Path.Combine(
            AppContext.BaseDirectory,
            ".tools",
            OperatingSystem.IsWindows() ? "julie-extract.exe" : "julie-extract");
        return StoreConsumerCursorRunner.Release(
            binaryPath,
            storeRoot,
            familyId,
            cursor.ConsumerId);
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

    /// <summary>
    /// One directory listing. A null return means the listing FAILED — a distinct fact from an empty directory,
    /// which every caller must handle rather than collapse.
    /// </summary>
    internal delegate IReadOnlyList<string>? FileLister(string directory, string pattern);

    /// <summary>
    /// The owed records in this store's sidecar directory. A listing failure yields NOTHING to discharge, which
    /// leaves every record on disk for the next pass — the safe direction for this caller.
    /// </summary>
    private static IReadOnlyList<string> EnumerateOwedRecords(string sidecarDirectory, FileLister? listFiles) =>
        ListFiles(sidecarDirectory, "*" + OwedRecordSuffix, listFiles) ?? [];

    private static IReadOnlyList<string>? ListFiles(string directory, string pattern, FileLister? listFiles)
    {
        if (listFiles is not null)
            return listFiles(directory, pattern);
        try
        {
            return Directory.GetFiles(directory, pattern);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Remember that this view is still owed a reclaim. The record NAME is the view key and its CONTENT is the
    /// raw view id, so a record whose content does not hash to its own name is unusable and is dropped rather
    /// than acted on — a planted file must never name another view's files.
    /// </summary>
    /// <returns>False when the record could not be written, which the caller must report rather than swallow.</returns>
    private static bool RecordOwed(string sidecarDirectory, string viewId)
    {
        try
        {
            string path = OwedRecordPath(sidecarDirectory, viewId);
            if (string.Equals(ReadOwedRecord(path), viewId, StringComparison.Ordinal))
                return true;
            File.WriteAllText(path, viewId, Encoding.UTF8);
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
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
