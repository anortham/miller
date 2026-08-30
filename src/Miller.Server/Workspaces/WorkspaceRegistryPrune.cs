using Miller.Indexing;
using Miller.Indexing.Store;

namespace Miller.Server.Workspaces;

/// <summary>
/// Registry GC: remove rows whose <c>canonical_root</c> no longer exists on disk. Composes
/// <see cref="WorkspaceRegistry.List"/> + <see cref="Directory.Exists"/> + <see cref="WorkspaceRegistry.Remove"/>
/// without opening any index artifact.
///
/// <para>A caller that supplies a <c>maintainStore</c> callback also has each registered family's coordinator
/// queue tidied through julie-extract's <c>store maintain</c> — terminal request rows a lagging consumer cursor
/// would otherwise pin forever. The callback is OPTIONAL and the default is none, so a prune spawns no
/// subprocess unless its caller asked for one; the dashboard's registry-only prune is unchanged.</para>
///
/// <para>A pruned row that was a family-store member also owns per-view sidecar files, so the prune reclaims
/// them through <see cref="StoreSidecarReclaim"/>: Miller-owned files only, never <c>store.db</c> or
/// <c>coord.db</c>, and never a view another member still claims. Reclaim is best-effort — a busy lease or a
/// held file leaves the files in place and reports it; the registry row still goes.</para>
///
/// <para>A real prune is also the DISCHARGE point for reclaims earlier passes owed, across every registered
/// family. A dry run discharges nothing.</para>
/// </summary>
public static class WorkspaceRegistryPrune
{
    private const int DefaultMaxProducerRetirementsPerRun = 5;

    public sealed record Entry(
        string WorkspaceId,
        string DisplayId,
        string Root);

    public sealed record RetirementFailure(
        string WorkspaceId,
        string DisplayId,
        string Root,
        StoreViewRetirementOutcome Outcome);

    public sealed record Result(
        bool DryRun,
        IReadOnlyList<Entry> Pruned,
        int Kept,
        StoreSidecarReclaimResult SidecarReclaim = default,
        StoreMaintenanceOutcome StoreMaintenance = default,
        IReadOnlyList<RetirementFailure> RetirementFailures = null!);

    public static Result Run(
        WorkspaceRegistry registry,
        string? protectedWorkspaceId,
        bool dryRun,
        Func<string, IDisposable?>? acquireSidecarLease = null,
        Func<string, StoreMaintenanceOutcome>? maintainStore = null,
        Func<StoreSidecarReclaimTarget, bool, StoreViewRetirementOutcome>? retireView = null,
        int maxProducerRetirements = DefaultMaxProducerRetirementsPerRun)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentOutOfRangeException.ThrowIfNegative(maxProducerRetirements);

        var pruned = new List<Entry>();
        var retirementFailures = new List<RetirementFailure>();
        var blockedFamilies = new HashSet<Guid>();
        var reclaimed = StoreSidecarReclaimResult.None;
        int kept = 0;
        int producerRetirements = 0;
        foreach (WorkspaceRegistryRow row in registry.List())
        {
            if (!string.IsNullOrWhiteSpace(protectedWorkspaceId) &&
                string.Equals(row.WorkspaceId, protectedWorkspaceId, StringComparison.Ordinal))
            {
                kept++;
                continue;
            }

            if (Directory.Exists(row.CanonicalRoot))
            {
                kept++;
                continue;
            }

            WorkspaceRemoval.StoreViewCapture capture =
                WorkspaceRemoval.CaptureStoreView(registry, row.WorkspaceId);
            if (capture.Failure is { } captureFailure)
            {
                retirementFailures.Add(new RetirementFailure(
                    row.WorkspaceId,
                    row.DisplayId,
                    row.CanonicalRoot,
                    captureFailure));
                blockedFamilies.Add(captureFailure.FamilyId);
                kept++;
                continue;
            }

            StoreSidecarReclaimTarget? target = capture.Target;
            if (target is not null)
            {
                if (!HasConfirmedLinkedWorktreeRemoval(registry, row, target))
                {
                    retirementFailures.Add(new RetirementFailure(
                        row.WorkspaceId,
                        row.DisplayId,
                        row.CanonicalRoot,
                        UnconfirmedLinkedWorktreeRemoval(target)));
                    blockedFamilies.Add(target.FamilyId);
                    kept++;
                    continue;
                }

                if (producerRetirements >= maxProducerRetirements)
                {
                    retirementFailures.Add(new RetirementFailure(
                        row.WorkspaceId,
                        row.DisplayId,
                        row.CanonicalRoot,
                        DeferredProducerRetirement(target)));
                    blockedFamilies.Add(target.FamilyId);
                    kept++;
                    continue;
                }

                if (!WorkspaceRemoval.TryRetireView(
                        target,
                        retireView,
                        apply: !dryRun,
                        out StoreViewRetirementOutcome outcome))
                {
                    retirementFailures.Add(new RetirementFailure(
                        row.WorkspaceId,
                        row.DisplayId,
                        row.CanonicalRoot,
                        outcome));
                    blockedFamilies.Add(target.FamilyId);
                    kept++;
                    continue;
                }

                producerRetirements++;
            }

            if (dryRun)
            {
                pruned.Add(new Entry(row.WorkspaceId, row.DisplayId, row.CanonicalRoot));
                continue;
            }

            _ = StoreSidecarReclaim.RecordIntent(target);
            registry.Remove(row.WorkspaceId);
            reclaimed = StoreSidecarReclaimResult.Combine(
                reclaimed,
                StoreSidecarReclaim.Reclaim(registry, target, acquireSidecarLease));
            pruned.Add(new Entry(row.WorkspaceId, row.DisplayId, row.CanonicalRoot));
        }

        var maintained = StoreMaintenanceOutcome.None;
        if (!dryRun)
        {
            (StoreSidecarReclaimResult discharged, maintained) =
                SweepFamilies(registry, acquireSidecarLease, maintainStore, blockedFamilies);
            reclaimed = StoreSidecarReclaimResult.Combine(reclaimed, discharged);
        }

        return new Result(dryRun, pruned, kept, reclaimed, maintained, retirementFailures);
    }

    /// <summary>
    /// Whether the missing root of <paramref name="row"/> is PROVEN to be a linked worktree git itself removed,
    /// which is the only shape whose family-store view a prune may retire.
    ///
    /// <para>The proof is the repository's COMMON dir present and readable while this worktree's admin dir is
    /// gone. The admin dir's PARENT (<c>&lt;repo&gt;/.git/worktrees</c>) is not the proof: git deletes that
    /// directory when the last worktree of a repository goes, so a tidy cleanup made every remaining row
    /// permanently unprunable. The common dir only vanishes with the repository.</para>
    ///
    /// <para>Every failure direction refuses. An unmounted worktree volume leaves the admin dir in place —
    /// refuse. A whole repository on an unmounted volume loses the common dir — refuse. A fault at or above the
    /// repository stops the common dir answering TRUE — refuse. A fault confined to
    /// <c>&lt;common&gt;/worktrees</c> is refused by <see cref="ConfirmedAbsent"/>, which will not read a failed
    /// probe as an absence.</para>
    ///
    /// <para>Lineage falls back to the store tables because <c>workspaces.git_is_linked</c>/<c>git_dir</c> were
    /// added after many rows were written, and <c>store_members.root_git_dir</c> plus
    /// <c>store_families.canonical_common_dir</c> carry the same facts for those rows. Only a NULL
    /// <c>git_is_linked</c> falls back — an explicit <c>0</c> is a recorded answer of "plain checkout", and a
    /// plain checkout's admin dir IS its common dir, which is refused outright.</para>
    /// </summary>
    private static bool HasConfirmedLinkedWorktreeRemoval(
        WorkspaceRegistry registry,
        WorkspaceRegistryRow row,
        StoreSidecarReclaimTarget target)
    {
        if (row.GitIsLinked == false)
            return false;

        string? adminDir = LineageDirectory(row.GitDir)
            ?? LineageDirectory(registry.GetStoreMember(row.WorkspaceId)?.RootGitDir);
        string? commonDir = LineageDirectory(row.GitCommonDir)
            ?? LineageDirectory(registry.GetStoreFamily(target.FamilyId)?.CanonicalCommonDir);
        if (adminDir is null || commonDir is null)
            return false;
        if (string.Equals(adminDir, commonDir, LineagePathComparison))
            return false;

        return Directory.Exists(commonDir) &&
            !File.Exists(adminDir) &&
            ConfirmedAbsent(adminDir, commonDir);
    }

    /// <summary>
    /// Whether <paramref name="adminDir"/> is proven ABSENT rather than merely unreadable.
    /// <see cref="Directory.Exists"/> answers false on a permission or I/O fault as well as on absence, and a
    /// wrong answer here retires a live worktree's store view, so the absence has to come from a listing that
    /// SUCCEEDED. While git still keeps <c>&lt;common&gt;/worktrees</c>, list it and require the admin dir to be
    /// missing from it. Once git has removed the last worktree and taken that directory with it, list the common
    /// dir instead and require the parent itself to be missing. A listing that throws proves nothing and refuses.
    /// </summary>
    private static bool ConfirmedAbsent(string adminDir, string commonDir)
    {
        string? parent = LineageDirectory(Path.GetDirectoryName(adminDir));
        if (parent is null)
            return false;

        bool parentPresent = Directory.Exists(parent);
        string listed = parentPresent ? parent : commonDir;
        string absentee = parentPresent ? adminDir : parent;

        try
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(listed))
            {
                if (string.Equals(LineageDirectory(entry), absentee, LineagePathComparison))
                    return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// A recorded lineage path in the one spelling the presence and equality tests can trust, or null when it is
    /// blank or unusable. Pure string work: these paths are expected to be gone, so nothing resolves them against
    /// the filesystem.
    /// </summary>
    private static string? LineageDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            return TrimTrailingSeparators(
                PathCanonicalizer.StripWindowsVerbatimPrefix(Path.GetFullPath(path)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return null;
        }
    }

    private static string TrimTrailingSeparators(string path)
    {
        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length == 0 ? path : trimmed;
    }

    private static StringComparison LineagePathComparison =>
        ArtifactRootIdentity.ComparisonFor(OperatingSystem.IsWindows(), OperatingSystem.IsMacOS());

    private static StoreViewRetirementOutcome UnconfirmedLinkedWorktreeRemoval(
        StoreSidecarReclaimTarget target) =>
        new(
            StoreViewRetirementDisposition.Failed,
            target.FamilyId,
            target.ViewId,
            0,
            0,
            0,
            "linked-worktree removal is not confirmed for the missing workspace root; use exact workspace remove after confirming removal");

    private static StoreViewRetirementOutcome DeferredProducerRetirement(
        StoreSidecarReclaimTarget target) =>
        new(
            StoreViewRetirementDisposition.Failed,
            target.FamilyId,
            target.ViewId,
            0,
            0,
            0,
            "producer retirement deferred by the per-run prune limit; rerun prune to advance");

    /// <summary>
    /// One pass over EVERY registered family, not only the families this prune touched.
    ///
    /// <para>It finishes the reclaims earlier passes could not: a removal that hit a busy sidecar lease left the
    /// view id in an owed record, because by then no registry row named it any more. Prune is the discharge
    /// point. Coordinator maintenance rides the same loop for the same reason — the rows a lagging consumer
    /// pinned belong to the family, not to any one workspace that left it.</para>
    /// </summary>
    private static (StoreSidecarReclaimResult Reclaimed, StoreMaintenanceOutcome Maintained) SweepFamilies(
        WorkspaceRegistry registry,
        Func<string, IDisposable?>? acquireSidecarLease,
        Func<string, StoreMaintenanceOutcome>? maintainStore,
        IReadOnlySet<Guid> blockedFamilies)
    {
        var discharged = StoreSidecarReclaimResult.None;
        var maintained = StoreMaintenanceOutcome.None;
        foreach (StoreFamilyRegistryRow family in registry.ListStoreFamilies())
        {
            if (blockedFamilies.Contains(family.FamilyId))
                continue;

            discharged = StoreSidecarReclaimResult.Combine(
                discharged,
                StoreSidecarReclaim.DischargeOwed(registry, family.StoreRoot, acquireSidecarLease));
            if (maintainStore is not null)
                maintained = StoreMaintenanceOutcome.Combine(maintained, maintainStore(family.StoreRoot));
        }

        return (discharged, maintained);
    }
}
