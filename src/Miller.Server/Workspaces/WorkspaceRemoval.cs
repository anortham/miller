using Miller.Indexing;
using Miller.Indexing.Store;
using Miller.Server.Tools;
using Miller.Testing;

namespace Miller.Server.Workspaces;

/// <summary>
/// Removes registered workspace data under all workspace write leases.
/// Live, sensitive, machine-global, corrupt-path, unregistered, and in-use targets are never deleted.
///
/// <para>A removed workspace that was a family-store member also leaves per-view sidecars behind in the SHARED
/// store (<c>content-</c>/<c>search-</c>/<c>vector-&lt;sha&gt;.db</c> under <c>&lt;store root&gt;/sidecars</c>),
/// which no <c>.miller</c> delete can reach. Those are reclaimed through <see cref="StoreSidecarReclaim"/> as
/// part of the removal. The reclaim is best-effort by design: a busy sidecar lease, an absent store root, or a
/// file a reader still holds leaves the files in place and reports it in
/// <see cref="WorkspaceRemoveResult.SidecarReclaim"/>. It never turns a successful removal into a failure.</para>
///
/// <para>The reclaim is also DURABLE across a crash: <see cref="StoreSidecarReclaim.RecordIntent"/> writes the
/// workspace-to-view mapping beside the sidecars before the registry row is deleted, so a process that dies in
/// the window between the delete and the reclaim leaves a record the next pass can finish from.</para>
/// </summary>
public static class WorkspaceRemoval
{
    private const string GeneratedPolicyDirectoryName = "ignore-policies";

    /// <summary>
    /// Remove by registry selector (display id, unique prefix, full id, or registered root path).
    /// </summary>
    /// <exception cref="KeyNotFoundException">No registry row matched <paramref name="selector"/>.</exception>
    public static WorkspaceRemoveResult RemoveById(
        WorkspaceRegistry registry,
        string selector,
        string? liveRoot,
        string? protectedMillerDir = null,
        Func<string, IDisposable?>? acquireWriterLock = null,
        Func<string, IDisposable?>? acquireSidecarLease = null,
        Func<StoreSidecarReclaimTarget, bool, StoreViewRetirementOutcome>? retireView = null) =>
        RemoveByIdCore(
            registry,
            selector,
            liveRoot,
            protectedMillerDir,
            acquireWriterLock,
            acquireSidecarLease,
            retireView,
            MillerHome.ResolveMillerDirectory());

    internal static WorkspaceRemoveResult RemoveById(
        WorkspaceRegistry registry,
        string selector,
        string millerDirectory,
        string? liveRoot = null,
        string? protectedMillerDir = null,
        Func<string, IDisposable?>? acquireWriterLock = null,
        Func<string, IDisposable?>? acquireSidecarLease = null,
        Func<StoreSidecarReclaimTarget, bool, StoreViewRetirementOutcome>? retireView = null) =>
        RemoveByIdCore(
            registry,
            selector,
            liveRoot,
            protectedMillerDir,
            acquireWriterLock,
            acquireSidecarLease,
            retireView,
            millerDirectory);

    private static WorkspaceRemoveResult RemoveByIdCore(
        WorkspaceRegistry registry,
        string selector,
        string? liveRoot,
        string? protectedMillerDir,
        Func<string, IDisposable?>? acquireWriterLock,
        Func<string, IDisposable?>? acquireSidecarLease,
        Func<StoreSidecarReclaimTarget, bool, StoreViewRetirementOutcome>? retireView,
        string millerDirectory)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        ArgumentException.ThrowIfNullOrWhiteSpace(millerDirectory);

        WorkspaceRegistryRow row = WorkspaceRegistrySelector.Resolve(registry, selector);
        if (!TryRegisteredMillerDir(row, out string millerDir))
        {
            return WorkspaceRemoveResult.RefusedInvalidRegistration(
                millerDir,
                row.WorkspaceId,
                row.CanonicalRoot);
        }
        if (!TryRegisteredGlobalPolicyPath(row, millerDirectory, out string globalPolicyPath))
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
            globalPolicyPath,
            liveRoot,
            protectedMillerDir,
            acquireWriterLock,
            acquireSidecarLease,
            retireView);
    }

    /// <summary>Remove by workspace root path (the dir that CONTAINS the <c>.miller</c> index dir).</summary>
    public static WorkspaceRemoveResult RemoveByPath(
        WorkspaceRegistry registry,
        string path,
        string? liveRoot,
        string? protectedMillerDir = null,
        Func<string, IDisposable?>? acquireWriterLock = null,
        Func<string, IDisposable?>? acquireSidecarLease = null,
        Func<StoreSidecarReclaimTarget, bool, StoreViewRetirementOutcome>? retireView = null) =>
        RemoveByPathCore(
            registry,
            path,
            liveRoot,
            protectedMillerDir,
            acquireWriterLock,
            acquireSidecarLease,
            retireView,
            MillerHome.ResolveMillerDirectory());

    internal static WorkspaceRemoveResult RemoveByPath(
        WorkspaceRegistry registry,
        string path,
        string millerDirectory,
        string? liveRoot = null,
        string? protectedMillerDir = null,
        Func<string, IDisposable?>? acquireWriterLock = null,
        Func<string, IDisposable?>? acquireSidecarLease = null,
        Func<StoreSidecarReclaimTarget, bool, StoreViewRetirementOutcome>? retireView = null) =>
        RemoveByPathCore(
            registry,
            path,
            liveRoot,
            protectedMillerDir,
            acquireWriterLock,
            acquireSidecarLease,
            retireView,
            millerDirectory);

    private static WorkspaceRemoveResult RemoveByPathCore(
        WorkspaceRegistry registry,
        string path,
        string? liveRoot,
        string? protectedMillerDir,
        Func<string, IDisposable?>? acquireWriterLock,
        Func<string, IDisposable?>? acquireSidecarLease,
        Func<StoreSidecarReclaimTarget, bool, StoreViewRetirementOutcome>? retireView,
        string millerDirectory)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(millerDirectory);

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
            if (liveRoot is not null && WorkspaceSafety.IsLiveWorkspace(stale.CanonicalRoot, liveRoot))
            {
                return WorkspaceRemoveResult.RefusedLive(
                    goneMillerDir,
                    stale.WorkspaceId,
                    stale.CanonicalRoot);
            }
            if (WorkspaceRootSafety.IsSensitiveRoot(
                    stale.CanonicalRoot,
                    WorkspaceRootSafety.SensitiveRootCandidates()))
            {
                return WorkspaceRemoveResult.RefusedSensitive(
                    goneMillerDir,
                    stale.WorkspaceId,
                    stale.CanonicalRoot);
            }
            if (!string.IsNullOrWhiteSpace(protectedMillerDir) &&
                SamePath(goneMillerDir, protectedMillerDir))
            {
                return WorkspaceRemoveResult.RefusedSensitive(
                    goneMillerDir,
                    stale.WorkspaceId,
                    stale.CanonicalRoot);
            }
            if (!TryRegisteredMillerDir(stale, out string staleMillerDir))
            {
                return WorkspaceRemoveResult.RefusedInvalidRegistration(
                    goneMillerDir,
                    stale.WorkspaceId,
                    stale.CanonicalRoot);
            }
            if (!TryRegisteredGlobalPolicyPath(stale, millerDirectory, out string stalePolicyPath))
            {
                return WorkspaceRemoveResult.RefusedInvalidRegistration(
                    goneMillerDir,
                    stale.WorkspaceId,
                    stale.CanonicalRoot);
            }
            return Remove(
                registry,
                stale.WorkspaceId,
                stale.CanonicalRoot,
                staleMillerDir,
                stalePolicyPath,
                liveRoot,
                protectedMillerDir,
                acquireWriterLock,
                acquireSidecarLease,
                retireView);
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
        if (!TryRegisteredGlobalPolicyPath(match, millerDirectory, out string globalPolicyPath))
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
            globalPolicyPath,
            liveRoot,
            protectedMillerDir,
            acquireWriterLock,
            acquireSidecarLease,
            retireView);
    }

    private static WorkspaceRemoveResult Remove(
        WorkspaceRegistry registry,
        string? workspaceId,
        string? root,
        string millerDir,
        string globalPolicyPath,
        string? liveRoot,
        string? protectedMillerDir,
        Func<string, IDisposable?>? acquireWriterLock,
        Func<string, IDisposable?>? acquireSidecarLease,
        Func<StoreSidecarReclaimTarget, bool, StoreViewRetirementOutcome>? retireView)
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

        // The view id lives in the store_members row, which the workspace delete cascades away. Capture it
        // BEFORE the delete: after the row is gone nothing on disk records which view this workspace owned, and
        // the sidecars would be unreclaimable forever. Nothing is deleted from the capture alone.
        //
        // The capture lives in MEMORY, so it dies with a crashed process. Every registry delete below is
        // therefore preceded by StoreSidecarReclaim.RecordIntent, which writes the same mapping to disk beside
        // the sidecars; the reclaim clears it only once the delete pass completes. The intent write sits
        // immediately before each delete so a refusal above never leaves a record behind, and a write that fails
        // never fails the removal — it surfaces in the reclaim's skip reason instead.
        StoreViewCapture capture = CaptureStoreView(registry, workspaceId);
        if (capture.Failure is { } captureFailure)
        {
            return WorkspaceRemoveResult.RefusedRetirement(
                millerDir,
                workspaceId,
                root,
                captureFailure);
        }
        StoreSidecarReclaimTarget? sidecarTarget = capture.Target;

        if (!Directory.Exists(millerDir))
        {
            if (workspaceId is null)
                return WorkspaceRemoveResult.NotFound(millerDir, workspaceId, root);
            StoreViewRetirementTargetResult retirement = RetireView(sidecarTarget, retireView, apply: true);
            if (!retirement.Succeeded)
            {
                return WorkspaceRemoveResult.RefusedRetirement(
                    millerDir,
                    workspaceId,
                    root,
                    retirement.Outcome);
            }
            string? missingIgnorePolicyCleanupError = TryDeleteGeneratedPolicy(globalPolicyPath);
            _ = StoreSidecarReclaim.RecordIntent(sidecarTarget);
            registry.Remove(workspaceId);
            StoreSidecarReclaimResult missingSidecarReclaim =
                StoreSidecarReclaim.Reclaim(registry, sidecarTarget, acquireSidecarLease);
            return WorkspaceRemoveResult.Removed(
                millerDir,
                workspaceId,
                root,
                indexDirDeleted: false,
                missingSidecarReclaim,
                missingIgnorePolicyCleanupError);
        }

        // The CT daemon is a FIFTH live holder, and the only one the lease bundle CANNOT hold. Its lease sits
        // one level down, at <.miller>/ct/daemon-v1.lock: an open FileShare.None handle inside a directory
        // blocks that directory's recursive delete on Windows, and the skip-set below cannot spare it because
        // that set is top-level file NAMES. So a live daemon made the delete throw PARTWAY THROUGH and left the
        // workspace neither removed nor intact — some sidecars gone, the registry row still there.
        //
        // The daemon lease is therefore TAKEN AND HELD across the delete, exactly like the other four holders.
        // Probing it and letting go again was check-then-act: a daemon that started in the window between the
        // probe and the delete reproduced the very half-deleted workspace the refusal exists to prevent.
        FileStream? ctDaemonLease = TryHoldContinuousTestDaemonLease(millerDir, out bool ctDaemonLive);
        try
        {
            if (ctDaemonLive)
                return WorkspaceRemoveResult.RefusedInUse(millerDir, workspaceId, root);

            // Delete the index data while HOLDING all four workspace-local write leases (indexer → content →
            // history → ct.lock) plus the CT daemon lease, so no Miller process can start writing this index —
            // nor a CLI content import, history append, or CT store write, which hold those sidecar locks
            // WITHOUT the indexer lock — mid-delete. Any lease unavailable ⇒ refuse, delete nothing. Only the
            // held lock files and the CT control-plane dir are skipped (an open FileShare.None handle cannot be
            // deleted on Windows); after release, the leftovers + empty dir are removed best-effort — a writer
            // that sneaks in after release finds an already-empty index and does a clean rebuild.
            using (WorkspaceWriteLeases? leases =
                WorkspaceWriteLeases.TryAcquireForRemove(
                    millerDir,
                    acquireWriterLock ?? SingleWriterLock.TryAcquire))
            {
                if (leases is null)
                    return WorkspaceRemoveResult.RefusedInUse(millerDir, workspaceId, root);
                StoreViewRetirementTargetResult retirement = RetireView(sidecarTarget, retireView, apply: true);
                if (!retirement.Succeeded)
                {
                    return WorkspaceRemoveResult.RefusedRetirement(
                        millerDir,
                        workspaceId,
                        root,
                        retirement.Outcome);
                }
                SingleWriterLock.DeleteContentsExceptLock(millerDir, DeleteSkipNames);
            }
        }
        finally
        {
            ctDaemonLease?.Dispose();
        }

        SingleWriterLock.TryDeleteEmptiedDir(millerDir);
        string? ignorePolicyCleanupError = TryDeleteGeneratedPolicy(globalPolicyPath);
        if (workspaceId is not null)
        {
            _ = StoreSidecarReclaim.RecordIntent(sidecarTarget);
            registry.Remove(workspaceId);
        }

        StoreSidecarReclaimResult sidecarReclaim =
            StoreSidecarReclaim.Reclaim(registry, sidecarTarget, acquireSidecarLease);
        return WorkspaceRemoveResult.Removed(
            millerDir,
            workspaceId,
            root,
            indexDirDeleted: true,
            sidecarReclaim,
            ignorePolicyCleanupError);
    }

    internal static bool TryRetireView(
        StoreSidecarReclaimTarget? target,
        Func<StoreSidecarReclaimTarget, bool, StoreViewRetirementOutcome>? retireView,
        bool apply,
        out StoreViewRetirementOutcome outcome)
    {
        StoreViewRetirementTargetResult result = RetireView(target, retireView, apply);
        outcome = result.Outcome;
        return result.Succeeded;
    }

    internal static StoreViewCapture CaptureStoreView(WorkspaceRegistry registry, string? workspaceId)
    {
        ArgumentNullException.ThrowIfNull(registry);
        StoreSidecarReclaimTarget? target = StoreSidecarReclaimTarget.Capture(registry, workspaceId);
        if (target is not null || string.IsNullOrWhiteSpace(workspaceId))
            return new(target, null);

        StoreMemberRegistryRow? member = registry.GetStoreMember(workspaceId);
        if (member is null)
            return new(null, null);

        return new(
            null,
            new StoreViewRetirementOutcome(
                StoreViewRetirementDisposition.Failed,
                member.FamilyId,
                member.ViewId,
                0,
                0,
                0,
                "captured store member mapping is incomplete"));
    }

    private static StoreViewRetirementTargetResult RetireView(
        StoreSidecarReclaimTarget? target,
        Func<StoreSidecarReclaimTarget, bool, StoreViewRetirementOutcome>? retireView,
        bool apply)
    {
        if (target is null)
            return StoreViewRetirementTargetResult.Success(default);
        if (retireView is null)
        {
            return StoreViewRetirementTargetResult.Failure(
                FailedRetirement(target, "store view retirement producer is unavailable"));
        }

        StoreViewRetirementOutcome planned;
        try
        {
            planned = retireView(target, false);
        }
        catch (Exception failure) when (IsExpectedRetirementFailure(failure))
        {
            return StoreViewRetirementTargetResult.Failure(FailedRetirement(target, failure.Message));
        }

        if (!MatchesTarget(planned, target) ||
            planned.Disposition is not (StoreViewRetirementDisposition.Planned or StoreViewRetirementDisposition.AlreadyAbsent))
        {
            return StoreViewRetirementTargetResult.Failure(
                InvalidRetirement(
                    target,
                    planned,
                    "store view retirement preview did not return Planned or AlreadyAbsent"));
        }
        if (planned.Disposition == StoreViewRetirementDisposition.AlreadyAbsent)
            return StoreViewRetirementTargetResult.Success(planned);
        if (!apply)
            return StoreViewRetirementTargetResult.Success(planned);

        StoreViewRetirementOutcome applied;
        try
        {
            applied = retireView(target, true);
        }
        catch (Exception failure) when (IsExpectedRetirementFailure(failure))
        {
            return StoreViewRetirementTargetResult.Failure(FailedRetirement(target, failure.Message));
        }

        if (!MatchesTarget(applied, target) ||
            applied.Disposition is not (StoreViewRetirementDisposition.Retired or StoreViewRetirementDisposition.AlreadyAbsent))
        {
            return StoreViewRetirementTargetResult.Failure(
                InvalidRetirement(target, applied, "store view retirement apply did not return Retired or AlreadyAbsent"));
        }
        return StoreViewRetirementTargetResult.Success(applied);
    }

    private static bool MatchesTarget(StoreViewRetirementOutcome outcome, StoreSidecarReclaimTarget target) =>
        outcome.FamilyId == target.FamilyId &&
        string.Equals(outcome.ViewId, target.ViewId, StringComparison.Ordinal);

    private static StoreViewRetirementOutcome InvalidRetirement(
        StoreSidecarReclaimTarget target,
        StoreViewRetirementOutcome reported,
        string fallbackError) =>
        reported.Disposition == StoreViewRetirementDisposition.Failed &&
        MatchesTarget(reported, target)
            ? reported
            : FailedRetirement(target, string.IsNullOrWhiteSpace(reported.Error) ? fallbackError : reported.Error);

    private static StoreViewRetirementOutcome FailedRetirement(
        StoreSidecarReclaimTarget target,
        string error) =>
        new(
            StoreViewRetirementDisposition.Failed,
            target.FamilyId,
            target.ViewId,
            0,
            0,
            0,
            error);

    private static bool IsExpectedRetirementFailure(Exception failure) =>
        failure is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or NotSupportedException
            or ArgumentException
            or System.ComponentModel.Win32Exception;

    private readonly record struct StoreViewRetirementTargetResult(
        bool Succeeded,
        StoreViewRetirementOutcome Outcome)
    {
        public static StoreViewRetirementTargetResult Success(StoreViewRetirementOutcome outcome) => new(true, outcome);

        public static StoreViewRetirementTargetResult Failure(StoreViewRetirementOutcome outcome) => new(false, outcome);
    }

    internal readonly record struct StoreViewCapture(
        StoreSidecarReclaimTarget? Target,
        StoreViewRetirementOutcome? Failure);

    /// <summary>
    /// The entry names inside a <c>.miller</c> dir that the guarded delete must NOT touch: the sidecar
    /// write-lock files this remove holds, plus the CT control-plane directory <c>ct/</c>.
    ///
    /// <para><c>ct/</c> is spared for the same reason the held lock files are. This remove holds
    /// <c>ct/daemon-v1.lock</c> open with <see cref="FileShare.None"/> across the delete, and on Windows an open
    /// exclusive handle makes the recursive delete of its PARENT directory throw — half-way through, after the
    /// index data is already gone. Skipping the directory keeps the destructive step total, and
    /// <see cref="SingleWriterLock.TryDeleteEmptiedDir"/> removes <c>ct/</c> once the handle is released.</para>
    ///
    /// <para>The skip is unconditional, not "only when the handle is held", because that is what makes the
    /// never-ran-CT case safe too: there is no handle to take when <c>ct/</c> does not exist yet, so a daemon
    /// that starts mid-delete would otherwise create the directory under the delete and break it the same way.
    /// The worst case then is a leftover <c>.miller/ct/</c>, never a half-deleted index.</para>
    /// </summary>
    private static readonly IReadOnlySet<string> DeleteSkipNames =
        new HashSet<string>(WorkspaceWriteLeases.SidecarLockFileNames, StringComparer.OrdinalIgnoreCase)
        {
            CtDaemonProtocol.DirectoryName,
        };

    /// <summary>
    /// Take the CT daemon's lease inside <paramref name="millerDir"/> and HOLD it (the returned stream), or set
    /// <paramref name="liveDaemon"/> because a live daemon already owns it.
    ///
    /// <para>The daemon's lease is an open <see cref="FileShare.None"/> handle on <c>ct/daemon-v1.lock</c>,
    /// exactly like every other Miller lease, so this is the same acquisition every other holder gets. The lock
    /// FILE outlives its daemon, so file existence alone is not the signal — a workspace that ever ran CT would
    /// then be unremovable forever. The handle is also the ONLY signal: reading <c>daemon.lease.json</c> as well
    /// would add a second, WEAKER one, because <c>CtDaemonLease.IsIdentityLive</c> collapses a denied process
    /// probe to "live", so a crashed daemon's leftover JSON plus a reused PID would refuse forever.</para>
    ///
    /// <para>Only a real sharing/lock violation counts as a live holder — the one shared Windows 32/33 /
    /// POSIX 11/35 table in <see cref="SingleWriterLock.IsLockContention"/>, which
    /// <c>CtDaemonLease.TryAcquire</c> already mirrors. Any OTHER denial is NOT proof of a holder: a
    /// <see cref="FileShare.None"/> holder produces ERROR_SHARING_VIOLATION, never ERROR_ACCESS_DENIED. A lock
    /// file carrying FILE_ATTRIBUTE_READONLY (restored from a ZIP, stamped by a sync client), an ACL that denies
    /// this user, or a directory sitting where the file belongs all raise
    /// <see cref="UnauthorizedAccessException"/> with no daemon anywhere — and reading those as "held" made
    /// <c>workspace remove</c> refuse forever, naming a writer that does not exist and giving the user nothing
    /// to stop. They are reported as no holder and the delete proceeds, which is safe because
    /// <see cref="DeleteSkipNames"/> spares the whole <c>ct/</c> directory either way.</para>
    ///
    /// <para>The path is built from <paramref name="millerDir"/> (the delete target) rather than from the
    /// registry root, so this can only ever ask about the directory this call is about to delete.</para>
    ///
    /// <para><see cref="FileMode.Open"/>, never <c>OpenOrCreate</c>: CT is opt-in, and the existence of
    /// <c>.miller/ct/</c> means "a daemon was started here". A remove must not manufacture that.</para>
    /// </summary>
    private static FileStream? TryHoldContinuousTestDaemonLease(string millerDir, out bool liveDaemon)
    {
        liveDaemon = false;
        string lockPath = Path.Combine(
            millerDir, CtDaemonProtocol.DirectoryName, CtDaemonProtocol.LockFileName);
        try
        {
            return new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null; // no CT daemon has ever run in this workspace: there is no lease to hold.
        }
        catch (Exception ex) when (
            ex is IOException contention &&
            SingleWriterLock.IsLockContention(contention, OperatingSystem.IsWindows()))
        {
            liveDaemon = true; // another process holds the lease: the daemon is live.
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // unprobeable, which is not the same fact as held. See the remarks above.
        }
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

    private static bool TryRegisteredGlobalPolicyPath(
        WorkspaceRegistryRow row,
        string? millerDirectoryOverride,
        out string policyPath)
    {
        ArgumentNullException.ThrowIfNull(row);
        policyPath = string.Empty;
        if (!TryRegisteredMillerDir(row, out _))
            return false;

        try
        {
            string canonicalRoot = Directory.Exists(row.CanonicalRoot)
                ? PathCanonicalizer.CanonicalizeRoot(row.CanonicalRoot)
                : Path.GetFullPath(row.CanonicalRoot);
            if (!SamePath(canonicalRoot, row.CanonicalRoot))
                return false;

            string millerDirectory = Path.GetFullPath(
                millerDirectoryOverride ?? MillerHome.ResolveMillerDirectory());
            string policyDirectory = Path.GetFullPath(
                Path.Combine(millerDirectory, GeneratedPolicyDirectoryName));
            string canonicalWorkspaceId = WorkspaceId.FromCanonicalRoot(canonicalRoot);
            string candidate = JulieIgnoreSeeder.GeneratedGlobalIgnorePathForWorkspaceId(
                canonicalWorkspaceId,
                millerDirectory);
            string expected = Path.Combine(
                policyDirectory,
                canonicalWorkspaceId + JulieIgnoreSeeder.WorkspaceIgnoreFileName);
            string? candidateDirectory = Path.GetDirectoryName(candidate);
            if (candidateDirectory is null ||
                !SamePath(candidate, expected) ||
                !SamePath(candidateDirectory, policyDirectory))
            {
                return false;
            }

            policyPath = candidate;
            return true;
        }
        catch (Exception ex) when (
            ex is ArgumentException or IOException or UnauthorizedAccessException or
            NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static string? TryDeleteGeneratedPolicy(string policyPath)
    {
        try
        {
            File.Delete(policyPath);
            return null;
        }
        catch (Exception ex) when (
            ex is ArgumentException or IOException or UnauthorizedAccessException or
            NotSupportedException or System.Security.SecurityException)
        {
            return Directory.Exists(policyPath)
                ? "the generated policy path is a directory"
                : ex.Message;
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
