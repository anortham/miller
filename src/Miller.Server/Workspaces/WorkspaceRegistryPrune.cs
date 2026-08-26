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
    public sealed record Entry(
        string WorkspaceId,
        string DisplayId,
        string Root);

    public sealed record Result(
        bool DryRun,
        IReadOnlyList<Entry> Pruned,
        int Kept,
        StoreSidecarReclaimResult SidecarReclaim = default,
        StoreMaintenanceOutcome StoreMaintenance = default);

    public static Result Run(
        WorkspaceRegistry registry,
        string? protectedWorkspaceId,
        bool dryRun,
        Func<string, IDisposable?>? acquireSidecarLease = null,
        Func<string, StoreMaintenanceOutcome>? maintainStore = null)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var pruned = new List<Entry>();
        var reclaimed = StoreSidecarReclaimResult.None;
        int kept = 0;
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

            if (dryRun)
            {
                pruned.Add(new Entry(row.WorkspaceId, row.DisplayId, row.CanonicalRoot));
                continue;
            }

            // The view id lives in the store_members row, which the workspace delete cascades away. Capture it
            // FIRST, write the owed-reclaim record to disk, then delete, then reclaim — the reclaim re-reads the
            // members table and spares any view a surviving workspace still claims. The record is what survives
            // a crash in the window between the delete and the reclaim; the in-memory capture does not.
            StoreSidecarReclaimTarget? target = StoreSidecarReclaimTarget.Capture(registry, row.WorkspaceId);
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
                SweepFamilies(registry, acquireSidecarLease, maintainStore);
            reclaimed = StoreSidecarReclaimResult.Combine(reclaimed, discharged);
        }

        return new Result(dryRun, pruned, kept, reclaimed, maintained);
    }

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
        Func<string, StoreMaintenanceOutcome>? maintainStore)
    {
        var discharged = StoreSidecarReclaimResult.None;
        var maintained = StoreMaintenanceOutcome.None;
        foreach (StoreFamilyRegistryRow family in registry.ListStoreFamilies())
        {
            discharged = StoreSidecarReclaimResult.Combine(
                discharged,
                StoreSidecarReclaim.DischargeOwed(registry, family.StoreRoot, acquireSidecarLease));
            if (maintainStore is not null)
                maintained = StoreMaintenanceOutcome.Combine(maintained, maintainStore(family.StoreRoot));
        }

        return (discharged, maintained);
    }
}
