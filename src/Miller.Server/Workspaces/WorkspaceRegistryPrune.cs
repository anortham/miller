using Miller.Indexing;

namespace Miller.Server.Workspaces;

/// <summary>
/// Registry GC: remove rows whose <c>canonical_root</c> no longer exists on disk. Composes
/// <see cref="WorkspaceRegistry.List"/> + <see cref="Directory.Exists"/> + <see cref="WorkspaceRegistry.Remove"/>
/// without opening any index artifact or spawning julie-extract.
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
        StoreSidecarReclaimResult SidecarReclaim = default);

    public static Result Run(
        WorkspaceRegistry registry,
        string? protectedWorkspaceId,
        bool dryRun,
        Func<string, IDisposable?>? acquireSidecarLease = null)
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

        if (!dryRun)
            reclaimed = StoreSidecarReclaimResult.Combine(reclaimed, DischargeOwed(registry, acquireSidecarLease));

        return new Result(dryRun, pruned, kept, reclaimed);
    }

    /// <summary>
    /// Finish the reclaims earlier passes could not: a removal that hit a busy sidecar lease left the view id in
    /// an owed record, because by then no registry row named it any more. Prune is the discharge point, so the
    /// sweep covers EVERY registered family, not only the families this prune touched.
    /// </summary>
    private static StoreSidecarReclaimResult DischargeOwed(
        WorkspaceRegistry registry,
        Func<string, IDisposable?>? acquireSidecarLease)
    {
        var discharged = StoreSidecarReclaimResult.None;
        foreach (StoreFamilyRegistryRow family in registry.ListStoreFamilies())
        {
            discharged = StoreSidecarReclaimResult.Combine(
                discharged,
                StoreSidecarReclaim.DischargeOwed(registry, family.StoreRoot, acquireSidecarLease));
        }

        return discharged;
    }
}
